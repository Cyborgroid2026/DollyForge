#!/usr/bin/env bash
# Dolly Forge 실행: ./run.sh
# Godot 없음 — C# + Rust 전용 엔진.
#   1. Rust 월드 서버 (이미 떠 있으면 그대로 쓴다)
#   2. 실사 에셋 내려받기 (처음 한 번)
#   3. C# 엔진 빌드 + 실행 (포트 7780 프레임 스트리밍)
#   4. VS Code 통합 브라우저 자동 열기
set -euo pipefail
cd "$(dirname "$0")"

SERVER_PORT=7700
STREAM_URL="http://127.0.0.1:7780/"
LOG_DIR="$PWD/.run-logs"
mkdir -p "$LOG_DIR"

listening() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }

open_integrated_browser() {
  command -v code >/dev/null || return 1
  local id=dollyforge.dollyforge-browser
  if python3 tools/vscode-browser/build.py "$LOG_DIR/dollyforge-browser.vsix" >/dev/null 2>&1 &&
     code --install-extension "$LOG_DIR/dollyforge-browser.vsix" --force >/dev/null 2>&1; then
    code --open-url "vscode://$id/open?url=$STREAM_URL" >/dev/null 2>&1
  fi
}

banner() {
  printf '\n\033[1;32m%s\033[0m\n' "════════════════════════════════════════════════════════════"
  printf '\033[1;32m  Dolly Forge 실행 중 → %s\033[0m\n' "$STREAM_URL"
  printf '  브라우저에서 위 주소를 여세요.\n'
  printf '  화면을 클릭하면 마우스가 잡히고 Esc 로 놓입니다. 끝내려면 이 터미널에서 Ctrl+C.\n'
  printf '\033[1;32m%s\033[0m\n\n' "════════════════════════════════════════════════════════════"
}

if listening 7780; then
  echo "[run] 게임이 이미 실행 중입니다."
  banner; exit 0
fi

SERVER_PID=""
cleanup() { [ -n "$SERVER_PID" ] && kill "$SERVER_PID" 2>/dev/null || true; }
trap cleanup EXIT INT TERM

echo "[run] 1/3 Rust 월드 서버"
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
      echo "  서버가 시작하지 못했습니다. 로그: $LOG_DIR/server.log" >&2
      tail -n 20 "$LOG_DIR/server.log" >&2; exit 1
    fi
    sleep 0.1
  done
  echo "  127.0.0.1:$SERVER_PORT"
fi

echo "[run] 2/3 C# 엔진 빌드"
(cd engine && dotnet build --nologo -v quiet) 2>&1

echo "[run] 3/3 게임 실행"
open_integrated_browser &
banner

# 헤드리스 실행: DISPLAY=:1 (X.Org 서버가 :1 에 있음) / 없으면 offscreen
if xdpyinfo -display :1 >/dev/null 2>&1; then
  export DISPLAY=:1
elif [ -n "${DISPLAY:-}" ] || [ -n "${WAYLAND_DISPLAY:-}" ]; then
  : # 기존 디스플레이 사용
else
  export SDL_VIDEODRIVER=offscreen
fi
exec dotnet run --project engine --no-build 2>&1
