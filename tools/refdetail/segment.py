"""레퍼런스 그림 속 두 인형의 정밀한 몸 영역 (SAM · Meta Segment Anything, Apache-2.0, transformers 내장).

인형마다 상자 프롬프트(정수리·발끝 + RTMPose 관절 범위)로 따로 분리한다.
  tools/refdetail/ref-mask-{front,back}.png      두 인형 합친 영역 (make_detail.py·compare.py)
  engine/assets/body/ref-silhouette-{front,back}.png  인형별 영역: R = 여성형, G = 남성형 (엔진 실루엣 맞춤)
실행: ~/ComfyUI/.venv/bin/python tools/refdetail/segment.py   (pose.py 먼저)
"""
import json
from pathlib import Path

import cv2
import numpy as np
import torch
from PIL import Image
from transformers import SamModel, SamProcessor

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).parent
# 정수리·발끝 y (RefDetail.cs RefPlace 와 같은 값)
TOP_BOTTOM = {"front": [(78, 985), (72, 990)], "back": [(90, 985), (80, 990)]}


def main():
    model = SamModel.from_pretrained("facebook/sam-vit-huge").to("cuda").eval()
    proc = SamProcessor.from_pretrained("facebook/sam-vit-huge")
    pose = json.loads((ROOT / "engine/assets/body/ref-pose.json").read_text())
    for view in ("front", "back"):
        img = Image.open(ROOT / f"tools/bodyfit/ref/{view}.png").convert("RGB")
        masks = []
        for f, person in enumerate(pose[view]):
            xs = [p[0] for p in person.values()]
            top, bottom = TOP_BOTTOM[view][f]
            box = [min(xs) - 45, top - 8, max(xs) + 45, bottom + 8]          # 관절 범위 + 팔·몸 두께 여유
            inp = proc(img, input_boxes=[[box]], return_tensors="pt").to("cuda")
            with torch.no_grad():
                out = model(**inp, multimask_output=True)
            ms = proc.image_processor.post_process_masks(out.pred_masks.cpu(), inp["original_sizes"].cpu(), inp["reshaped_input_sizes"].cpu())[0][0]
            best = int(out.iou_scores[0, 0].argmax())
            m = ms[best].numpy().astype(np.uint8)
            n, lab, st, _ = cv2.connectedComponentsWithStats(m)
            if n > 1:
                m = (lab == 1 + int(np.argmax(st[1:, cv2.CC_STAT_AREA]))).astype(np.uint8)
            masks.append(m)
            print(f"[segment] {view} {'여성형' if f == 0 else '남성형'}: 점수 {float(out.iou_scores[0, 0, best]):.3f}, 영역 {m.mean() * 100:.1f}%")
        union = np.maximum(masks[0], masks[1])
        cv2.imwrite(str(HERE / f"ref-mask-{view}.png"), union * 255)
        sil = np.zeros((*union.shape, 3), np.uint8)
        sil[..., 2] = masks[0] * 255; sil[..., 1] = masks[1] * 255                # BGR: R = 여, G = 남
        cv2.imwrite(str(ROOT / f"engine/assets/body/ref-silhouette-{view}.png"), sil)


if __name__ == "__main__":
    main()
