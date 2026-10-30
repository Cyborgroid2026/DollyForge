"""레퍼런스 그림(tools/bodyfit/ref/front.png·back.png) → 인형에 옮길 근육 디테일 법선 지도.

1. Marigold normals v1-1 (prs-eth, OpenRAIL++) 로 픽셀별 표면 법선을 추정한다 (카메라 공간: x 오른쪽, y 위, z 화면 밖).
2. 몸 영역(GrabCut)을 구하고, 몸 안에서만 평균 낸 흐린 법선(정규화 합성곱)을 빼서 근육 크기 이하의 디테일만 남긴다.
   실루엣 경계는 몸/배경 법선이 급히 바뀌어 가짜 디테일이 되므로 안쪽으로 깎아 낸다.
3. engine/assets/body/ref-detail-{front,back}.png (16비트, 값 = 디테일 × 2 + 0.5) 로 저장한다.

실행: ~/ComfyUI/.venv/bin/python tools/refdetail/make_detail.py   (CUDA, 약 1분)
"""
import sys
from pathlib import Path

import cv2
import numpy as np
import torch
from PIL import Image
from diffusers import MarigoldNormalsPipeline

ROOT = Path(__file__).resolve().parents[2]
REF = ROOT / "tools/bodyfit/ref"
OUT = ROOT / "engine/assets/body"
SIGMA = float(sys.argv[1]) if len(sys.argv) > 1 else 12.0   # 고역 통과 기준 (px, 키 약 910 px → 12 px ≈ 2.3 cm)
EDGE = 7           # 실루엣 안쪽으로 깎는 폭 (px)


def body_mask(img):
    m = np.zeros(img.shape[:2], np.uint8)
    bg, fg = np.zeros((1, 65)), np.zeros((1, 65))
    cv2.grabCut(img, m, (300, 40, 560, 1000), bg, fg, 6, cv2.GC_INIT_WITH_RECT)
    m = np.where((m == 1) | (m == 3), 1, 0).astype(np.uint8)
    return cv2.morphologyEx(m, cv2.MORPH_OPEN, np.ones((5, 5), np.uint8))


def main():
    pipe = MarigoldNormalsPipeline.from_pretrained(
        "prs-eth/marigold-normals-v1-1", variant="fp16", torch_dtype=torch.float16).to("cuda")
    OUT.mkdir(parents=True, exist_ok=True)
    for name in ("front", "back"):
        path = REF / f"{name}.png"
        n = pipe(Image.open(path).convert("RGB"), num_inference_steps=10, ensemble_size=4,
                 processing_resolution=1024, match_input_resolution=True,
                 generator=torch.Generator("cuda").manual_seed(0)).prediction[0].astype(np.float32)
        m = body_mask(cv2.imread(str(path))).astype(np.float32)
        blur_m = cv2.GaussianBlur(m, (0, 0), SIGMA) + 1e-4
        low = np.stack([cv2.GaussianBlur(n[..., c] * m, (0, 0), SIGMA) / blur_m for c in range(3)], -1)
        inner = cv2.erode(m, np.ones((2 * EDGE + 1, 2 * EDGE + 1), np.uint8))
        inner = cv2.GaussianBlur(inner, (0, 0), EDGE / 2)
        d = (n - low) * inner[..., None]
        enc = np.clip(d * 2 + 0.5, 0, 1)
        cv2.imwrite(str(OUT / f"ref-detail-{name}.png"), (enc[..., ::-1] * 65535).astype(np.uint16))
        # 비교용 원본 법선·몸 영역 (tools/refdetail/compare.py)
        here = Path(__file__).parent
        cv2.imwrite(str(here / f"ref-normals-{name}.png"), ((n[..., ::-1] * 0.5 + 0.5).clip(0, 1) * 65535).astype(np.uint16))
        cv2.imwrite(str(here / f"ref-mask-{name}.png"), (m * 255).astype(np.uint8))
        print(f"[refdetail] {name}: |d| 평균 {np.abs(d)[inner > 0.5].mean():.3f} → {OUT / f'ref-detail-{name}.png'}")


if __name__ == "__main__":
    sys.exit(main())
