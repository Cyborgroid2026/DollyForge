#!/usr/bin/env bash
# 가게 진열창 너머 실내 사진을 로컬 FLUX.1-schnell 로 만든다 (tools/shop-interiors.txt 한 줄 = 한 장).
# 결과: godot/assets/shops/<양식>_<번호>.jpg (번호 = ShopNames 안의 순서). 이미 있으면 건너뛴다.
# 시드는 줄 번호로 고정이라 다시 돌려도 같은 그림이 나온다. 생성 이미지는 이 프로젝트 소유 (모델 라이선스 Apache 2.0).
set -euo pipefail
cd "$(dirname "$0")/.."
tools/comfyui.sh
DEST=godot/assets/shops
mkdir -p "$DEST"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
line=0
declare -A index
while IFS='|' read -r style name what; do
  [[ -z "$style" || "$style" == \#* ]] && continue
  line=$((line + 1))
  i=${index[$style]:-0}
  index[$style]=$((i + 1))
  out=$(printf '%s/%s_%02d.jpg' "$DEST" "$style" "$i")
  [ -s "$out" ] && continue
  prompt="photograph taken from the sidewalk looking through a shop window into the interior of $what. Eye level, straight-on, daytime, interior lights on, realistic everyday clutter, natural colors, slightly worn, no people, no readable text or logos, 35mm photo"
  tools/imagegen.py "$prompt" -o "$TMP/shop.png" -W 1216 -H 640 --seed $((1000 + line)) >/dev/null
  python3 -c "from PIL import Image; import sys; Image.open(sys.argv[1]).convert('RGB').save(sys.argv[2], quality=86)" "$TMP/shop.png" "$out"
  echo "[shops] $out  ($name)"
done < tools/shop-interiors.txt
