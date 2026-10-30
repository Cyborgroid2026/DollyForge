#!/usr/bin/env bash
# 로컬 이미지 생성 서버(ComfyUI)를 백그라운드로 띄운다. 이미 떠 있으면 그대로 둔다.
# 설치 위치: ~/ComfyUI (.venv, PyTorch cu128). 모델: models/checkpoints/flux1-schnell-fp8.safetensors (Apache 2.0)
# 출처: https://github.com/comfyanonymous/ComfyUI , https://huggingface.co/Comfy-Org/flux1-schnell
# 사용: tools/comfyui.sh [stop]   → http://127.0.0.1:8188
set -euo pipefail
cd "$(dirname "$0")/.."
COMFY="${COMFYUI_DIR:-$HOME/ComfyUI}"
URL=http://127.0.0.1:8188
LOG=.run-logs/comfyui.log

if [ "${1:-}" = stop ]; then
  pkill -f "$COMFY/main.py" && echo "[comfyui] 종료" || echo "[comfyui] 실행 중 아님"
  exit 0
fi
if curl -sf "$URL/system_stats" >/dev/null; then
  echo "[comfyui] 이미 실행 중 → $URL"
  exit 0
fi
[ -x "$COMFY/.venv/bin/python" ] || { echo "[comfyui] $COMFY 에 설치되어 있지 않음" >&2; exit 1; }
mkdir -p .run-logs
nohup "$COMFY/.venv/bin/python" "$COMFY/main.py" --listen 127.0.0.1 --port 8188 >"$LOG" 2>&1 &
for _ in $(seq 60); do
  curl -sf "$URL/system_stats" >/dev/null && { echo "[comfyui] 시작됨 → $URL (로그 $LOG)"; exit 0; }
  sleep 1
done
echo "[comfyui] 60초 안에 응답 없음 — $LOG 확인" >&2
exit 1
