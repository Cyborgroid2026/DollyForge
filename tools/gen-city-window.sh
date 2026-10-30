#!/usr/bin/env bash
# 스튜디오 창밖 사진(engine/assets/studio/city_window.jpg)을 로컬 FLUX.1-schnell 로 다시 만든다.
# 시드 고정이라 같은 그림이 나온다. 다른 풍경을 원하면 SEED 를 바꾸거나 tools/imagegen.py -n 4 로 후보를 뽑아 고른다.
# 레퍼런스 그림의 창밖처럼 푸른 밤의 가상 마천루 (실존 랜드마크 없음). EnvRenderer 의 CITY_EYE/CITY_YAW 가 이 구도에 맞춰져 있다.
set -euo pipefail
cd "$(dirname "$0")/.."
SEED=${SEED:-805}
tools/comfyui.sh
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
tools/imagegen.py "full-frame outdoor night photograph of a fictional futuristic megacity skyline, dozens of tall rectangular glass towers with flat roofs and thin vertical LED light strips, thousands of tiny lit windows in cool white and pale blue, deep navy blue night sky with light haze near the horizon, blue cinematic color grade, towers filling the lower two thirds of the frame, realistic, no spires, no antenna, no famous buildings, no window frame, no text" \
  -o "$TMP/city.png" -W 1344 -H 832 --seed "$SEED"
mkdir -p engine/assets/studio
python3 -c "from PIL import Image; import sys; Image.open(sys.argv[1]).convert('RGB').save(sys.argv[2], quality=92)" "$TMP/city.png" engine/assets/studio/city_window.jpg
echo "[city] engine/assets/studio/city_window.jpg (seed $SEED)"
