"""레퍼런스 그림 속 두 인형의 관절 위치 (RTMPose, rtmlib · Apache-2.0) → engine/assets/body/ref-pose.json
엔진(RefDetail.cs)이 인형을 그림 자세에 2D 로 겹칠 때 쓴다. 왼쪽부터 여성형·남성형.
실행: ~/ComfyUI/.venv/bin/python tools/refdetail/pose.py
"""
import json
from pathlib import Path

import cv2
from rtmlib import Body

ROOT = Path(__file__).resolve().parents[2]
NAMES = ["nose", "eyeL", "eyeR", "earL", "earR", "shoulderL", "shoulderR", "elbowL", "elbowR",
         "wristL", "wristR", "hipL", "hipR", "kneeL", "kneeR", "ankleL", "ankleR"]


def main():
    body = Body(mode="performance", backend="onnxruntime", device="cpu")
    out = {}
    for view in ("front", "back"):
        kps, scores = body(cv2.imread(str(ROOT / f"tools/bodyfit/ref/{view}.png")))
        people = sorted(zip(kps, scores), key=lambda ks: ks[0][:, 0].mean())[:2]   # 왼쪽 = 여성형
        out[view] = [{n: [float(x), float(y)] for n, (x, y) in zip(NAMES, k)} for k, _ in people]
        print(f"[pose] {view}: 신뢰도 " + ", ".join(f"{s.mean():.2f}" for _, s in people))
    path = ROOT / "engine/assets/body/ref-pose.json"
    path.write_text(json.dumps(out, indent=1))
    print(f"[pose] {path}")


if __name__ == "__main__":
    main()
