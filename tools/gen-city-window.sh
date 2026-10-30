#!/usr/bin/env bash
# 스튜디오 창밖 사진(engine/assets/studio/city_window.jpg)을 로컬 FLUX.1-schnell 로 다시 만든다.
# 시드 고정이라 같은 그림이 나온다. 다른 풍경을 원하면 SEED 를 바꾸거나 tools/imagegen.py -n 4 로 후보를 뽑아 고른다.
# 사진 속 산 능선은 높이의 약 41 %, EnvRenderer 의 CITY_EYE/CITY_YAW 가 이 구도에 맞춰져 있다.
set -euo pipefail
cd "$(dirname "$0")/.."
SEED=${SEED:-63}
tools/comfyui.sh
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
tools/imagegen.py "photograph from an upper floor window looking out over an ordinary modern city at dusk, rows of rectangular apartment blocks and office buildings with flat rooftops and warm lit windows, glowing streets, the city spreads toward dark forested mountain ridges on the horizon, deep blue evening sky with soft clouds and a faint warm afterglow, horizon in the middle of the frame, realistic, natural colors, no famous buildings, no spires, no window frame, no reflections, no text" \
  -o "$TMP/city.png" -W 1344 -H 832 --seed "$SEED"
mkdir -p engine/assets/studio
python3 -c "from PIL import Image; import sys; Image.open(sys.argv[1]).convert('RGB').save(sys.argv[2], quality=92)" "$TMP/city.png" engine/assets/studio/city_window.jpg
echo "[city] engine/assets/studio/city_window.jpg (seed $SEED)"
