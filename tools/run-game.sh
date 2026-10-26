#!/usr/bin/env bash
# 게임을 화면 보내기 모드로 띄운다. 창은 어느 모니터에도 뜨지 않고 화면은 통합 브라우저(http://127.0.0.1:7780/)로만 보인다.
#   cage 가 있으면: 모니터 없는 가상 Wayland 화면(wlroots headless, GPU 가속) 안에서 렌더링한다.
#   없으면: 예전처럼 작은 테두리 없는 창을 화면 구석으로 보낸다 (sudo apt install cage 권장).
# 사용법: tools/run-game.sh [Godot 인자...]   (환경 변수 GODOT 로 실행 파일 지정)
set -euo pipefail
cd "$(dirname "$0")/.."
GODOT="${GODOT:-$HOME/Applications/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64}"

if command -v cage >/dev/null; then
  export WLR_BACKENDS=headless WLR_LIBINPUT_NO_DEVICES=1 WLR_RENDERER=gles2 DOLLY_OFFSCREEN=1
  # cage·Wayland 의 무해한 경고(EGL 장치 목록, 키맵, 아이콘 프로토콜)는 걸러 낸다
  exec cage -- "$GODOT" --display-driver wayland --path godot "$@" -- --stream 2>&1 \
    | grep --line-buffered -v -E 'eglQueryDevicesEXT|^"$|xkbcomp|Could not resolve keysym|^> |protocol not found|XDG activation|wayland_thread.cpp|Errors from xkbcomp'
else
  exec "$GODOT" --path godot "$@" -- --stream
fi
