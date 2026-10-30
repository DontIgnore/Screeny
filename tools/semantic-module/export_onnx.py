"""Export SigLIP 2 image and text encoders to two ONNX files (fp32).

    work/venv/Scripts/python -I tools/semantic-module/export_onnx.py --model google/siglip2-base-patch16-224

Reads the checkpoint from work/hf/<name> (download it first with `hf download`), writes
work/onnx/<name>/vision.onnx, text.onnx and params.json (logit_scale, logit_bias, image size).

The graphs return the projected features (get_image_features / get_text_features, i.e. after the MAP head and the
text head), NOT L2-normalized: normalization happens in the caller.
  vision.onnx: pixel_values float32 [N,3,S,S] -> image_embeds float32 [N,768]
  text.onnx:   input_ids    int64   [N,64]    -> text_embeds  float32 [N,768]
The text tower pools the last position, so input_ids must always be padded to exactly 64 (pad id 0).
"""
import argparse
import json
import os

import torch
from transformers import AutoModel

HERE = os.path.dirname(os.path.abspath(__file__))
WORK = os.path.join(HERE, "work")
OPSET = 17


class Vision(torch.nn.Module):
    def __init__(self, m):
        super().__init__()
        self.m = m

    def forward(self, pixel_values):
        out = self.m.get_image_features(pixel_values=pixel_values)
        return out if torch.is_tensor(out) else out.pooler_output


class Text(torch.nn.Module):
    def __init__(self, m):
        super().__init__()
        self.m = m

    def forward(self, input_ids):
        out = self.m.get_text_features(input_ids=input_ids)
        return out if torch.is_tensor(out) else out.pooler_output


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default="google/siglip2-base-patch16-224")
    args = ap.parse_args()
    name = args.model.split("/")[-1]
    src = os.path.join(WORK, "hf", name)
    dst = os.path.join(WORK, "onnx", name)
    os.makedirs(dst, exist_ok=True)

    model = AutoModel.from_pretrained(src).eval()
    size = model.config.vision_config.image_size
    length = model.config.text_config.max_position_embeddings

    with torch.no_grad():
        torch.onnx.export(Vision(model), (torch.zeros(1, 3, size, size),), os.path.join(dst, "vision.onnx"),
                          input_names=["pixel_values"], output_names=["image_embeds"],
                          dynamic_axes={"pixel_values": {0: "batch"}, "image_embeds": {0: "batch"}},
                          opset_version=OPSET, dynamo=False)
        torch.onnx.export(Text(model), (torch.zeros(1, length, dtype=torch.int64),), os.path.join(dst, "text.onnx"),
                          input_names=["input_ids"], output_names=["text_embeds"],
                          dynamic_axes={"input_ids": {0: "batch"}, "text_embeds": {0: "batch"}},
                          opset_version=OPSET, dynamo=False)

    params = {
        "model": args.model,
        "image_size": size,
        "max_length": length,
        "pad_id": 0,
        "eos_id": 1,
        "logit_scale": float(model.logit_scale.item()),
        "logit_bias": float(model.logit_bias.item()),
        "image_mean": [0.5, 0.5, 0.5],
        "image_std": [0.5, 0.5, 0.5],
    }
    with open(os.path.join(dst, "params.json"), "w", encoding="utf-8") as f:
        json.dump(params, f, indent=2)
    for fn in sorted(os.listdir(dst)):
        print(f"{fn}: {os.path.getsize(os.path.join(dst, fn)):,} bytes")


if __name__ == "__main__":
    main()
