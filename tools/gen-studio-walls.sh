#!/usr/bin/env bash
# 스튜디오 벽 그림(engine/assets/studio/walls.jpg)을 로컬 FLUX.1-schnell 로 다시 만든다.
# 위 절반 = 뒷벽(창이 있는 -Z 벽), 아래 절반 = 옆벽·앞벽. 각 1792×576, 벽 하나에 그림 한 장(타일 반복 없음).
# 시드 고정이라 같은 그림이 나온다. 원근 없이 평평하고 글자가 없는 후보를 골라 정한 시드다.
set -euo pipefail
cd "$(dirname "$0")/.."
BACK_SEED=${BACK_SEED:-302}
SIDE_SEED=${SIDE_SEED:-303}
P="flat orthographic straight-on view of a dark futuristic interior wall, black anodized gunmetal panels with engraved printed circuit board traces, microchips and solder pads, thin glowing white LED light strips running along some panel edges, a few tiny cool blue indicator lights, very high detail, dark moody studio lighting, evenly lit, no perspective, no floor, no ceiling, no window, no people, no text, no letters"
tools/comfyui.sh
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
tools/imagegen.py "$P" -o "$TMP/back.png" -W 1792 -H 576 --seed "$BACK_SEED"
tools/imagegen.py "$P" -o "$TMP/side.png" -W 1792 -H 576 --seed "$SIDE_SEED"
mkdir -p engine/assets/studio
python3 -c "
from PIL import Image; import sys
a=Image.open(sys.argv[1]).convert('RGB'); b=Image.open(sys.argv[2]).convert('RGB')
s=Image.new('RGB',(1792,1152)); s.paste(a,(0,0)); s.paste(b,(0,576)); s.save(sys.argv[3],quality=92)" "$TMP/back.png" "$TMP/side.png" engine/assets/studio/walls.jpg
echo "[walls] engine/assets/studio/walls.jpg (seed $BACK_SEED / $SIDE_SEED)"
