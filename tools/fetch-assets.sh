#!/usr/bin/env bash
# 실사 렌더링용 CC0 에셋(Poly Haven, https://polyhaven.com — CC0 1.0)을 내려받는다.
# 용량이 커서 git 에는 넣지 않는다 (godot/assets 는 .gitignore). 이미 있는 파일은 건너뛴다.
# 사용법: fetch-assets.sh [대상 폴더, 기본: godot]
set -euo pipefail
cd "$(dirname "$0")/../${1:-godot}"
BASE=https://dl.polyhaven.org/file/ph-assets

fetch() { # url dest
  [ -s "$2" ] && return 0
  mkdir -p "$(dirname "$2")"
  echo "  ↓ $2"
  curl -sfL --retry 3 -o "$2.part" "$1" && mv "$2.part" "$2"
}

echo "[assets] 하늘 HDRI"
for h in qwantani_mid_morning_puresky kloofendal_48d_partly_cloudy_puresky kloppenheim_06_puresky; do
  fetch "$BASE/HDRIs/hdr/2k/${h}_2k.hdr" "assets/hdri/$h.hdr"
done

echo "[assets] PBR 텍스처 (색·노멀·거칠기)"
for t in aerial_grass_rock forrest_ground_01 rock_face_03 rocky_trail weathered_planks white_plaster_02 bark_brown_02 rock_boulder_dry plywood metal_plate \
         concrete_pavers red_brick_03 worn_asphalt beige_wall_001 grey_tiles corrugated_iron concrete_wall_008 rust_coarse_01; do
  for m in diff nor_gl rough; do
    fetch "$BASE/Textures/jpg/1k/$t/${t}_${m}_1k.jpg" "assets/tex/$t/$m.jpg"
  done
done

echo "[assets] 소나무 잎 카드 (pine_tree_01 의 실사 가지 텍스처)"
for m in twig_diff twig_alpha twig_nor_gl; do
  fetch "$BASE/Models/jpg/1k/pine_tree_01/pine_tree_01_${m}_1k.jpg" "assets/tex/pine/$m.jpg"
done

echo "[assets] 활엽수 잎 카드 (tree_small_02 의 잎 텍스처 — 자작나무 잎으로 쓴다)"
for m in leaves_diff leaves_alpha leaves_nor_gl; do
  fetch "$BASE/Models/jpg/1k/tree_small_02/tree_small_02_${m}_1k.jpg" "assets/tex/leaves/$m.jpg"
done

cat > assets/LICENSE.txt <<'TXT'
이 폴더의 텍스처와 HDRI 는 Poly Haven (https://polyhaven.com) 의 CC0 1.0 에셋입니다.
tools/fetch-assets.sh 로 내려받습니다.
TXT
echo "[assets] 완료"
