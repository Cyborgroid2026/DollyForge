#!/usr/bin/env bash
# MakeHuman CC0 자산(기본 인체 메시·모프 타깃·리그)을 내려받는다.
# 출처: https://github.com/makehumancommunity/makehuman (자산 라이선스 CC0 1.0, LICENSE.ASSETS.md)
# 고정 커밋에서 tools/makehuman-files.txt 목록만 받는다 (약 42 MB). git 에는 넣지 않는다.
set -euo pipefail
cd "$(dirname "$0")/.."
REV=a8bc2d54ff0ac92e78ff71431b1023eda42bf482
BASE="https://raw.githubusercontent.com/makehumancommunity/makehuman/$REV/makehuman/data"
DEST=engine/assets/makehuman
mkdir -p "$DEST"
n=0
while IFS= read -r f; do
  [ -z "$f" ] && continue
  out="$DEST/$f"
  [ -s "$out" ] && continue
  mkdir -p "$(dirname "$out")"
  curl -sfL --retry 3 -o "$out.part" "$BASE/$f" && mv "$out.part" "$out"
  n=$((n+1))
done < tools/makehuman-files.txt
echo "[makehuman] 새로 받은 파일 $n 개 → $DEST"
