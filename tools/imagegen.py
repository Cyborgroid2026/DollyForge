#!/usr/bin/env python3
"""로컬 ComfyUI(FLUX.1-schnell)로 이미지를 만든다. 표준 라이브러리만 쓴다.

사용:
  tools/comfyui.sh                       # 서버 먼저
  tools/imagegen.py "prompt" -o generated/x.png [-W 1024 -H 1024 --seed 1 -n 1]
-n 이 2 이상이면 x_01.png, x_02.png … 로 저장한다.
"""
import argparse
import json
import random
import sys
import time
import urllib.parse
import urllib.request
import uuid
from pathlib import Path

URL = "http://127.0.0.1:8188"
CKPT = "flux1-schnell-fp8.safetensors"


def workflow(prompt, w, h, seed, steps):
    # FLUX schnell: cfg 1, 4스텝, 16채널 잠재공간(EmptySD3LatentImage)
    return {
        "1": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": CKPT}},
        "2": {"class_type": "CLIPTextEncode", "inputs": {"text": prompt, "clip": ["1", 1]}},
        "3": {"class_type": "CLIPTextEncode", "inputs": {"text": "", "clip": ["1", 1]}},
        "4": {"class_type": "EmptySD3LatentImage", "inputs": {"width": w, "height": h, "batch_size": 1}},
        "5": {"class_type": "KSampler", "inputs": {
            "model": ["1", 0], "positive": ["2", 0], "negative": ["3", 0], "latent_image": ["4", 0],
            "seed": seed, "steps": steps, "cfg": 1.0, "sampler_name": "euler", "scheduler": "simple", "denoise": 1.0}},
        "6": {"class_type": "VAEDecode", "inputs": {"samples": ["5", 0], "vae": ["1", 2]}},
        "7": {"class_type": "SaveImage", "inputs": {"images": ["6", 0], "filename_prefix": "dollyforge"}},
    }


def call(path, data=None):
    req = urllib.request.Request(URL + path, data=json.dumps(data).encode() if data is not None else None,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req) as r:
        return r.read()


def generate(prompt, w, h, seed, steps, timeout=600):
    pid = json.loads(call("/prompt", {"prompt": workflow(prompt, w, h, seed, steps),
                                      "client_id": str(uuid.uuid4())}))["prompt_id"]
    end = time.time() + timeout
    while time.time() < end:
        hist = json.loads(call(f"/history/{pid}")).get(pid)
        if hist:
            if hist.get("status", {}).get("status_str") == "error":
                sys.exit(f"[imagegen] 생성 실패: {json.dumps(hist['status'], ensure_ascii=False)}")
            for out in hist["outputs"].values():
                for img in out.get("images", []):
                    return call("/view?" + urllib.parse.urlencode(img))
        time.sleep(0.5)
    sys.exit("[imagegen] 시간 초과")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("prompt")
    ap.add_argument("-o", "--out", default="generated/image.png")
    ap.add_argument("-W", "--width", type=int, default=1024)
    ap.add_argument("-H", "--height", type=int, default=1024)
    ap.add_argument("--seed", type=int)
    ap.add_argument("--steps", type=int, default=4)
    ap.add_argument("-n", "--count", type=int, default=1)
    a = ap.parse_args()

    try:
        call("/system_stats")
    except OSError:
        sys.exit("[imagegen] ComfyUI 가 꺼져 있음 — tools/comfyui.sh 먼저 실행")

    out = Path(a.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    seed = a.seed if a.seed is not None else random.randrange(2**32)
    for i in range(a.count):
        path = out if a.count == 1 else out.with_name(f"{out.stem}_{i + 1:02d}{out.suffix}")
        t = time.time()
        path.write_bytes(generate(a.prompt, a.width, a.height, seed + i, a.steps))
        print(f"[imagegen] {path}  seed={seed + i}  {time.time() - t:.1f}s")


if __name__ == "__main__":
    main()
