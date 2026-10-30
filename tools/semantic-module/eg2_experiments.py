"""EmbeddingGemma 2 knobs on the synthetic set (ONNX fp32 = torch fp32, see reference.py):
query prefix variants and the vision token budget (max_soft_tokens 70/140/280/560/1120).

    work/venv/Scripts/python -I -X utf8 tools/semantic-module/eg2_experiments.py [--dtype q4]
"""
import argparse
import os
import time

import numpy as np
import onnxruntime as ort
from PIL import Image
from transformers import AutoProcessor

from reference import TESTSET, WORK, l2n, load_testset, retrieval_report

PREFIXES = {
    "search": "task: search result | query: ",
    "none": "",
    "classification": "task: classification | query: ",
    "photo-of": "task: search result | query: a screenshot or photo of ",
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dtype", default="", help="'' (fp32), '_quantized' or '_q4'")
    ap.add_argument("--budgets", type=int, nargs="+", default=[70, 140, 280, 560, 1120])
    ap.add_argument("--all-prefixes", action="store_true", help="try every prefix at every budget (default: 280 only)")
    a = ap.parse_args()
    src = os.path.join(WORK, "hf", "embeddinggemma-2")
    od = os.path.join(WORK, "hf", "embeddinggemma-2-ONNX", "onnx")
    proc = AutoProcessor.from_pretrained(src)
    ts = load_testset()
    so = ort.SessionOptions()
    vs = ort.InferenceSession(os.path.join(od, f"vision_encoder{a.dtype}.onnx"), so, providers=["CPUExecutionProvider"])
    ms = ort.InferenceSession(os.path.join(od, f"model{a.dtype}.onnx"), so, providers=["CPUExecutionProvider"])
    empty = np.zeros((0, 512), dtype=np.float32)

    def run(ids, feats=empty):
        x = np.array([ids], dtype=np.int64)
        return l2n(ms.run(["sentence_embedding"], {"input_ids": x, "attention_mask": np.ones_like(x), "image_features": feats,
                                                   "video_features": empty, "audio_features": empty})[0][0])

    def queries(prefix):
        return {q: run(proc.tokenizer(prefix + q, split_special_tokens=True)["input_ids"]) for q in ts["queries"]}

    images = {n: Image.open(os.path.join(TESTSET, n)).convert("RGB") for n in ts["images"]}
    for budget in a.budgets:
        t0 = time.perf_counter()
        img = {}
        for n, im in images.items():
            x = proc(images=[[im]], return_tensors="np", images_kwargs={"max_soft_tokens": budget})
            f = vs.run(None, {"pixel_values": x["pixel_values"].astype(np.float32),
                              "pixel_position_ids": x["image_position_ids"].astype(np.int64)})[0]
            img[n] = run(x["input_ids"][0].tolist(), f)
        dt = (time.perf_counter() - t0) / len(images)
        for pname, prefix in PREFIXES.items():
            if budget != 280 and pname != "search" and not a.all_prefixes:
                continue
            rep = retrieval_report(ts, img, queries(prefix))
            print(f"== budget {budget} prefix {pname}  ({dt * 1000:.0f} ms/image, default threads)")
            print(rep)


if __name__ == "__main__":
    main()
