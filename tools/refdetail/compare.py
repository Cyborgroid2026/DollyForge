"""레퍼런스(Marigold 법선)와 게임 인형(dolly-engine --refnormals)의 형태 비교.

  형태 일치 = 겹친 몸 영역의 평균 법선 각도 차 e → 1 − e/45°
  디테일 일치 = 근육 크기 고역 성분(x·y)의 상관계수 (0 = 무관, 1 = 같은 근육 무늬)
  실루엣 일치 = 인형별 영역의 IoU (segment.py 의 SAM 영역 대 게임 인형)
사용: python3 tools/refdetail/compare.py <ours 폴더> [비교이미지.jpg]
"""
import sys
from pathlib import Path

import cv2
import numpy as np

HERE = Path(__file__).parent
SIGMA = 12.0


def load_ref(view):
    n = cv2.imread(str(HERE / f"ref-normals-{view}.png"), -1)[..., ::-1].astype(np.float32) / 65535 * 2 - 1
    m = cv2.imread(str(HERE / f"ref-mask-{view}.png"), 0) > 127
    return n, m


def highpass(n, m):
    mf = m.astype(np.float32)
    bm = cv2.GaussianBlur(mf, (0, 0), SIGMA) + 1e-4
    low = np.stack([cv2.GaussianBlur(n[..., c] * mf, (0, 0), SIGMA) / bm for c in range(3)], -1)
    return n - low


def main():
    ours_dir = Path(sys.argv[1])
    tiles, scores = [], []
    for view in ("front", "back"):
        rn, rm = load_ref(view)
        o = cv2.imread(str(ours_dir / f"ours-{view}.png"))[..., ::-1].astype(np.float32)
        om = o.sum(-1) > 0
        on = o / 255 * 2 - 1
        on /= np.linalg.norm(on, axis=-1, keepdims=True) + 1e-6
        both = rm & om
        inner = cv2.erode(both.astype(np.uint8), np.ones((15, 15), np.uint8)) > 0
        ang = np.degrees(np.arccos(np.clip((rn * on).sum(-1), -1, 1)))[inner]
        shape = max(0.0, 1 - ang.mean() / 45)
        hr, ho = highpass(rn, rm), highpass(on, om)
        a, b = hr[..., :2][inner].ravel(), ho[..., :2][inner].ravel()
        corr = float(np.corrcoef(a, b)[0, 1])
        ref_sil = cv2.imread(str(HERE.parents[1] / f"engine/assets/body/ref-silhouette-{view}.png"))
        ours_id = cv2.imread(str(ours_dir / f"ours-id-{view}.png"))
        ious = []
        for ch in (2, 1):        # BGR: R = 여, G = 남
            a_, b_ = ref_sil[..., ch] > 127, ours_id[..., ch] > 127
            ious.append((a_ & b_).sum() / max(1, (a_ | b_).sum()))
        scores += [shape, max(0.0, corr), *ious]
        print(f"{view:5}  각도차 평균 {ang.mean():5.1f}°  형태 일치 {shape*100:4.0f}%  디테일 일치 {corr*100:4.0f}%  실루엣 여 {ious[0]*100:3.0f}% 남 {ious[1]*100:3.0f}%")
        vis = lambda x: ((x * 0.5 + 0.5).clip(0, 1) * 255).astype(np.uint8)
        r = vis(rn) * rm[..., None]; g = vis(on) * om[..., None]
        tiles.append(np.hstack([r[40:1010, 300:860], g[40:1010, 300:860]]))
    print(f"형태 반영도 평균 {np.mean(scores)*100:.0f}%  (형태·디테일·실루엣 × 앞뒤)")
    if len(sys.argv) > 2:
        cv2.imwrite(sys.argv[2], cv2.resize(np.hstack(tiles)[..., ::-1], None, fx=0.5, fy=0.5))


if __name__ == "__main__":
    main()
