#!/usr/bin/env bash
# Dolly Forge 실행: ./run.sh
# Godot 없음 — C# + Rust 전용 엔진.
#   1. Rust 월드 서버 (이미 떠 있으면 그대로 쓴다)
#   2. C# 엔진 빌드 + 실행 (포트 7780 프레임 스트리밍)
#   3. 엔진이 준비되면 VS Code 통합 브라우저 자동 열기
set -euo pipefail
cd "$(dirname "$0")"

SERVER_PORT=7700
STREAM_URL="http://127.0.0.1:7780/"
LOG_DIR="$PWD/.run-logs"
mkdir -p "$LOG_DIR"

listening() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }

# VS Code 통합 브라우저 열기
open_browser() {
  command -v code >/dev/null || return 0
  # dollyforge 확장이 설치되어 있으면 바로 사용
  local id=dollyforge.dollyforge-browser
  if code --list-extensions 2>/dev/null | grep -q "$id"; then
    code --open-url "vscode://$id/open?url=$STREAM_URL" >/dev/null 2>&1 && return 0
  fi
  # 없으면 빌드·설치 후 열기
  if python3 tools/vscode-browser/build.py "$LOG_DIR/dollyforge-browser.vsix" >/dev/null 2>&1 &&
     code --install-extension "$LOG_DIR/dollyforge-browser.vsix" --force >/dev/null 2>&1; then
    code --open-url "vscode://$id/open?url=$STREAM_URL" >/dev/null 2>&1 && return 0
  fi
  # 최후 폴백: VS Code Simple Browser (URL 인코딩 필요)
  local enc_url
  enc_url=$(python3 -c "import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1],safe=''))" "$STREAM_URL" 2>/dev/null || echo "$STREAM_URL")
  code --open-url "vscode://vscode.simpleBrowser/show?url=$enc_url" >/dev/null 2>&1 || true
}

banner() {
  printf '\n\033[1;32m%s\033[0m\n' "════════════════════════════════════════════════════════════"
  printf '\033[1;32m  Dolly Forge → %s\033[0m\n' "$STREAM_URL"
  printf '  VS Code 통합 브라우저가 자동으로 열립니다.\n'
  printf '  화면을 클릭하면 마우스가 잡히고 Esc 로 놓입니다. 끝내려면 Ctrl+C.\n'
  printf '\033[1;32m%s\033[0m\n\n' "════════════════════════════════════════════════════════════"
}

if listening 7780; then
  echo "[run] 게임이 이미 실행 중입니다."
  open_browser
  banner; exit 0
fi

SERVER_PID=""
cleanup() { [ -n "$SERVER_PID" ] && kill "$SERVER_PID" 2>/dev/null || true; }
trap cleanup EXIT INT TERM

# ── 1. Rust 월드 서버 ──────────────────────────────────────────────────
echo "[run] 1/2 Rust 월드 서버"
if listening "$SERVER_PORT"; then
  echo "  이미 127.0.0.1:$SERVER_PORT 에서 실행 중"
else
  if ! (cd server && cargo build --quiet) >"$LOG_DIR/server-build.log" 2>&1; then
    echo "  서버 빌드 실패. 로그: $LOG_DIR/server-build.log" >&2
    tail -n 30 "$LOG_DIR/server-build.log" >&2; exit 1
  fi
  (cd server && DOLLY_DEV_ECHO_CODE=1 exec cargo run --quiet) >"$LOG_DIR/server.log" 2>&1 &
  SERVER_PID=$!
  for _ in $(seq 1 100); do
    listening "$SERVER_PORT" && break
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
      echo "  서버 시작 실패. 로그: $LOG_DIR/server.log" >&2
      tail -n 20 "$LOG_DIR/server.log" >&2; exit 1
    fi
    sleep 0.1
  done
  echo "  127.0.0.1:$SERVER_PORT"
fi

# ── 2. C# 엔진 빌드 ───────────────────────────────────────────────────
echo "[run] 2/2 C# 엔진 빌드 + 실행"

# 헤드리스 디스플레이 선택
if xdpyinfo -display :1 >/dev/null 2>&1; then
  export DISPLAY=:1
elif [ -n "${DISPLAY:-}" ] || [ -n "${WAYLAND_DISPLAY:-}" ]; then
  : # 기존 환경 변수 사용
else
  export SDL_VIDEODRIVER=offscreen
fi

# 해부학 인체 자산 (MakeHuman CC0) — 처음 한 번 받는다
[ -s engine/assets/makehuman/3dobjs/base.obj ] || ./tools/fetch-makehuman.sh || echo "  MakeHuman 자산을 못 받아 SDF 몸으로 실행합니다"

(cd engine && dotnet build --nologo -v quiet) 2>&1

# 엔진 실행 — [stream] 줄이 출력되면 브라우저 열기
dotnet run --project engine --no-build 2>&1 | while IFS= read -r line; do
  printf '%s\n' "$line"
  if [[ "$line" == *"[stream] http"* ]]; then
    banner
    open_browser &
  fi
done
