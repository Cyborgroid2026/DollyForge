#!/usr/bin/env bash
# Dolly Forge 실행: ./run.sh
# VS Code 작업 "Dolly Forge: 실행" 과 같은 순서로 띄운다.
#   1. 실사 에셋 내려받기 (처음 한 번)
#   2. Rust 월드 서버 (이미 떠 있으면 그대로 쓴다)
#   3. C# 빌드와 Godot 에셋 가져오기
#   4. Godot 게임을 모니터 없는 가상 화면에서 실행 (창은 뜨지 않는다)
#   5. VS Code 통합 브라우저(Open Integrated Browser)로 게임 화면을 자동으로 연다
# 끝낼 때는 Ctrl+C. 이 스크립트가 띄운 서버와 게임을 함께 정리한다.
set -euo pipefail
cd "$(dirname "$0")"

GODOT="${GODOT:-$HOME/Applications/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64}"
SERVER_PORT=7700
STREAM_URL="http://127.0.0.1:7780/"
LOG_DIR="$PWD/.run-logs"
mkdir -p "$LOG_DIR"

listening() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }

# VS Code 통합 브라우저로 연다: 저장소의 작은 확장(tools/vscode-browser)이 vscode:// 주소를 받아
# workbench.action.browser.open 을 부른다. 처음 한 번만 설치한다.
open_integrated_browser() {
  command -v code >/dev/null || return 1
  local id=dollyforge.dollyforge-browser
  if ! code --list-extensions 2>/dev/null | grep -qx "$id"; then
    python3 tools/vscode-browser/build.py "$LOG_DIR/dollyforge-browser.vsix" &&
      code --install-extension "$LOG_DIR/dollyforge-browser.vsix" >/dev/null 2>&1 || return 1
  fi
  code --open-url "vscode://$id/open?url=$STREAM_URL" >/dev/null 2>&1
}

banner() {
  if open_integrated_browser; then
    printf '\n\033[1;32m  Dolly Forge → VS Code 통합 브라우저에 열었습니다 (%s)\033[0m\n' "$STREAM_URL"
    printf '  탭이 보이지 않으면: 명령 팔레트(Ctrl+Shift+P) → "Open Integrated Browser" → 위 주소\n'
    printf '  화면을 클릭하면 마우스가 잡히고 Esc 로 놓입니다. 끝내려면 이 터미널에서 Ctrl+C.\n\n'
    return
  fi
  printf '\n\033[1;32m%s\033[0m\n' "════════════════════════════════════════════════════════════"
  printf '\033[1;32m  Dolly Forge 실행 중 → %s\033[0m\n' "$STREAM_URL"
  printf '  VS Code: 명령 팔레트(Ctrl+Shift+P) → "Open Integrated Browser" → 위 주소\n'
  printf '  화면을 클릭하면 마우스가 잡히고 Esc 로 놓입니다. 끝내려면 이 터미널에서 Ctrl+C.\n'
  printf '\033[1;32m%s\033[0m\n\n' "════════════════════════════════════════════════════════════"
}

if [ ! -x "$GODOT" ]; then
  echo "Godot 4.7.2 .NET 을 찾을 수 없습니다: $GODOT" >&2
  echo "다른 위치라면 GODOT=/경로/Godot ./run.sh 로 지정하세요." >&2
  exit 1
fi

if listening 7780; then
  echo "[run] 게임이 이미 실행 중입니다."
  banner
  exit 0
fi

SERVER_PID=""
cleanup() {
  [ -n "$SERVER_PID" ] && kill "$SERVER_PID" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

echo "[run] 1/4 실사 에셋"
./tools/fetch-assets.sh godot

echo "[run] 2/4 Rust 월드 서버"
if listening "$SERVER_PORT"; then
  echo "  이미 127.0.0.1:$SERVER_PORT 에서 실행 중 — 그대로 씁니다"
else
  if ! (cd server && cargo build --quiet) >"$LOG_DIR/server-build.log" 2>&1; then
    echo "  서버 빌드 실패. 로그: $LOG_DIR/server-build.log" >&2
    tail -n 30 "$LOG_DIR/server-build.log" >&2
    exit 1
  fi
  (cd server && DOLLY_DEV_ECHO_CODE=1 exec cargo run --quiet) >"$LOG_DIR/server.log" 2>&1 &
  SERVER_PID=$!
  for _ in $(seq 1 100); do
    listening "$SERVER_PORT" && break
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
      echo "  서버가 시작하지 못했습니다. 로그: $LOG_DIR/server.log" >&2
      tail -n 20 "$LOG_DIR/server.log" >&2
      exit 1
    fi
    sleep 0.1
  done
  echo "  127.0.0.1:$SERVER_PORT (로그: $LOG_DIR/server.log)"
fi

echo "[run] 3/4 C# 빌드 · Godot 에셋 가져오기"
(cd godot && dotnet build --nologo -v quiet && "$GODOT" --headless --path . --import >"$LOG_DIR/import.log" 2>&1)

echo "[run] 4/4 게임 (화면 보내기)"
GODOT="$GODOT" ./tools/run-game.sh 2>&1 | while IFS= read -r line; do
  printf '%s\n' "$line"
  [[ "$line" == *"[stream] http"* ]] && banner
done
