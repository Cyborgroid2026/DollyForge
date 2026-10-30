#!/usr/bin/env bash
# 인형 크롬 매트캡(engine/assets/studio/matcap.png)을 로컬 FLUX.1-schnell 로 다시 만든다.
# ZBrush 식 재질 구: 은빛 액체 크롬 구 사진에서 원을 찾아(허프 변환) 구만 정사각형으로 잘라 512×512 로 저장한다.
# 셰이더는 시점 기준 법선으로 이 구를 읽는다 (Shader.cs BodyFrag). 시드 고정이라 같은 그림이 나온다.
set -euo pipefail
cd "$(dirname "$0")/.."
SEED=${SEED:-605}
tools/comfyui.sh
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
tools/imagegen.py "ZBrush matcap material sphere, a polished silver liquid chrome ball that fills the entire square image edge to edge, mid grey gunmetal silver with a subtle cool blue tint, soft gradient reflections of a dark studio, two tall bright white strip light reflections near the left and right edges, a broad soft highlight on the upper left, darker band below the middle, light floor reflection at the bottom, smooth, high detail, no background visible, no text" \
  -o "$TMP/sphere.png" -W 768 -H 768 --seed "$SEED"
python3 - "$TMP/sphere.png" engine/assets/studio/matcap.png <<'PY'
import sys, cv2
img = cv2.imread(sys.argv[1]); g = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
c = cv2.HoughCircles(cv2.GaussianBlur(g, (9, 9), 2), cv2.HOUGH_GRADIENT, dp=1.5, minDist=500, param1=60, param2=40, minRadius=200, maxRadius=380)
x, y, r = [float(v) for v in c[0][0]]
r *= 0.985
cv2.imwrite(sys.argv[2], cv2.resize(img[int(y - r):int(y + r), int(x - r):int(x + r)], (512, 512), interpolation=cv2.INTER_AREA))
PY
echo "[matcap] engine/assets/studio/matcap.png (seed $SEED)"
