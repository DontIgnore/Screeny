"""Python reference for the C# prototype: token IDs, preprocessed pixels and embeddings for both candidates.

    work/venv/Scripts/python -I -X utf8 tools/semantic-module/reference.py --model siglip2
    work/venv/Scripts/python -I -X utf8 tools/semantic-module/reference.py --model eg2

Writes work/ref/<model>/:
  tokens.json            {string: [ids]} for STRINGS (exact model input, see tokenize_* below)
  pixels/<image>.f32     preprocessed model input (float32, C order) for every test image
  emb_<variant>.json     {"images": {name: [768]}, "queries": {q: [768]}} L2-normalized, per variant
  retrieval_<variant>.txt
Variants: "torch" (fp32 PyTorch, the ground truth) and every ONNX file set in ONNX_VARIANTS.
"""
import argparse
import json
import os

import numpy as np
import onnxruntime as ort
import torch
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
WORK = os.path.join(HERE, "work")
TESTSET = os.path.join(WORK, "testset")

# Tokenizer parity strings: languages, whitespace, digits, emoji (incl. ZWJ), rare CJK (byte fallback), special-token
# lookalikes, case folding edge cases, and one string longer than 64 tokens (SigLIP truncation).
STRINGS = [
    "cat", "Cat", "котик", "Котик на диване", "график продаж", "счёт", "Счёт на оплату № 128", "error dialog",
    "code", "a photo of a cat", "一只猫", "猫の写真", "エラーダイアログ", "gato en el sofá", "factura de la luz",
    "🐱", "cat 🐈‍⬛ 👩‍💻", "  leading and trailing  ", "multiple   spaces\tand\nnewline", "12345 3.14 2026-10-30",
    "𠜎𠜱𠝹", "ΟΔΟΣ", "İstanbul", "Straße", "<eos> <pad> <bos> literal", "<|image|>", "C:\\Users\\Name\\file.png",
    "https://example.com/a?b=c", "",
    "this is a deliberately long query string that keeps going and going with many many words so that it ends up "
    "being longer than sixty four tokens after tokenization which forces the tokenizer to truncate the sequence and "
    "decide where the end of sequence token goes",
]


def load_testset():
    with open(os.path.join(TESTSET, "testset.json"), encoding="utf-8") as f:
        return json.load(f)


def l2n(x):
    x = np.asarray(x, dtype=np.float32)
    return x / np.linalg.norm(x, axis=-1, keepdims=True)


def retrieval_report(ts, img, qry):
    names = ts["images"]
    im = np.stack([img[n] for n in names])
    lines, top1, hit3, dt1, dh3, dn = [], 0, 0, 0, 0, 0
    for q, rel in ts["queries"].items():
        s = im @ np.asarray(qry[q])
        order = np.argsort(-s)
        t1 = names[order[0]] in rel
        h3 = any(names[i] in rel for i in order[:3])
        top1 += t1
        hit3 += h3
        if all(r.startswith("desk_") for r in rel):
            dt1, dh3, dn = dt1 + t1, dh3 + h3, dn + 1
        lines.append(f"{q:14} top1={'Y' if t1 else 'n'} hit@3={'Y' if h3 else 'n'}  " +
                     ", ".join(f"{names[i]}:{s[i]:.3f}" for i in order[:3]))
    n = len(ts["queries"])
    lines.append(f"TOTAL top1 {top1}/{n}  hit@3 {hit3}/{n}   (desktop-density subset: top1 {dt1}/{dn}  hit@3 {dh3}/{dn})")
    return "\n".join(lines)


# ---------------------------------------------------------------- SigLIP 2

def siglip_tokenize(tok, s):
    # SigLIP 2 was trained on lowercased text; the HF tokenizer does NOT lowercase. Python str.lower() is the reference.
    # split_special_tokens: a query is plain text, "<eos>" typed by the user must not become the EOS id.
    return tok(s.lower(), padding="max_length", max_length=64, truncation=True, split_special_tokens=True)["input_ids"]


def run_siglip(name):
    from transformers import AutoModel, AutoProcessor
    src = os.path.join(WORK, "hf", name)
    out = os.path.join(WORK, "ref", name)
    os.makedirs(os.path.join(out, "pixels"), exist_ok=True)
    ts = load_testset()
    proc = AutoProcessor.from_pretrained(src)
    model = AutoModel.from_pretrained(src).eval()
    tok = proc.tokenizer

    tokens = {s: siglip_tokenize(tok, s) for s in STRINGS}
    # Cross-check against the sentencepiece library on the shipped tokenizer.model (ids + EOS, no BOS).
    import sentencepiece as spm
    sp = spm.SentencePieceProcessor(model_file=os.path.join(src, "tokenizer.model"))
    for s, ids in tokens.items():
        raw = sp.encode(s.lower())[:63] + [1]
        raw += [0] * (64 - len(raw))
        if raw != ids:
            print("HF fast tokenizer != sentencepiece for", repr(s), ids[:12], raw[:12])
    with open(os.path.join(out, "tokens.json"), "w", encoding="utf-8") as f:
        json.dump(tokens, f, ensure_ascii=False)

    pix = {}
    for n in ts["images"]:
        pv = proc.image_processor(images=Image.open(os.path.join(TESTSET, n)).convert("RGB"), return_tensors="np")["pixel_values"]
        pix[n] = pv.astype(np.float32)
        pix[n].tofile(os.path.join(out, "pixels", n + ".f32"))
    qids = {q: siglip_tokenize(tok, q) for q in ts["queries"]}

    with torch.no_grad():
        def imf(p):
            o = model.get_image_features(pixel_values=torch.from_numpy(p))
            return (o if torch.is_tensor(o) else o.pooler_output)[0].numpy()
        img = {n: l2n(imf(p)) for n, p in pix.items()}

        def tf(ids):
            o = model.get_text_features(input_ids=torch.tensor([ids]))
            return (o if torch.is_tensor(o) else o.pooler_output)[0].numpy()
        qry = {q: l2n(tf(ids)) for q, ids in qids.items()}
    save_variant(out, "torch", ts, img, qry)

    d = os.path.join(WORK, "onnx", name)
    for variant, (vf, tf_) in {"fp32": ("vision.onnx", "text.onnx"), "int8": ("vision_int8.onnx", "text_int8.onnx"),
                               "q4e": ("vision_int8.onnx", "text_q4e.onnx"), "q4": ("vision_q4.onnx", "text_q4.onnx"),
                               "w8": ("vision_w8.onnx", "text_w8.onnx"), "w8g4": ("vision_w8.onnx", "text_w8g4.onnx")}.items():
        if not os.path.exists(os.path.join(d, tf_)):
            continue
        vs = ort.InferenceSession(os.path.join(d, vf), providers=["CPUExecutionProvider"])
        tsn = ort.InferenceSession(os.path.join(d, tf_), providers=["CPUExecutionProvider"])
        img_o = {n: l2n(vs.run(None, {"pixel_values": p})[0][0]) for n, p in pix.items()}
        qry_o = {q: l2n(tsn.run(None, {"input_ids": np.array([ids], dtype=np.int64)})[0][0]) for q, ids in qids.items()}
        save_variant(out, variant, ts, img_o, qry_o, img, qry)


# ---------------------------------------------------------------- EmbeddingGemma 2

QUERY_PREFIX = "task: search result | query: "


def eg2_tokenize(tok, s):
    # BOS + pieces + EOS, no padding (batch of one). Special tokens in user text stay plain text.
    return tok(s, split_special_tokens=True)["input_ids"]


def run_eg2(soft=280):
    from transformers import AutoModel, AutoProcessor
    src = os.path.join(WORK, "hf", "embeddinggemma-2")
    onnx_dir = os.path.join(WORK, "hf", "embeddinggemma-2-ONNX", "onnx")
    out = os.path.join(WORK, "ref", "embeddinggemma-2" + ("" if soft == 280 else f"-s{soft}"))
    os.makedirs(os.path.join(out, "pixels"), exist_ok=True)
    ts = load_testset()
    proc = AutoProcessor.from_pretrained(src)
    tok = proc.tokenizer

    tokens = {s: eg2_tokenize(tok, s) for s in STRINGS}
    tokens.update({QUERY_PREFIX + s: eg2_tokenize(tok, QUERY_PREFIX + s) for s in STRINGS})
    import sentencepiece as spm
    sp = spm.SentencePieceProcessor(model_file=os.path.join(src, "tokenizer.model"))
    for s, ids in tokens.items():
        raw = [2] + sp.encode(s) + [1]
        if raw != ids:
            print("HF fast tokenizer != sentencepiece for", repr(s), ids[:12], raw[:12])
    with open(os.path.join(out, "tokens.json"), "w", encoding="utf-8") as f:
        json.dump(tokens, f, ensure_ascii=False)

    feats = {}
    for n in ts["images"]:
        x = proc(images=[[Image.open(os.path.join(TESTSET, n)).convert("RGB")]], return_tensors="np",
                 images_kwargs={"max_soft_tokens": soft})
        feats[n] = x
        x["pixel_values"].astype(np.float32).tofile(os.path.join(out, "pixels", n + ".f32"))
        x["image_position_ids"].astype(np.int64).tofile(os.path.join(out, "pixels", n + ".pos.i64"))
        with open(os.path.join(out, "pixels", n + ".ids.json"), "w") as f:
            json.dump(x["input_ids"][0].tolist(), f)
    qids = {q: eg2_tokenize(tok, QUERY_PREFIX + q) for q in ts["queries"]}

    model = AutoModel.from_pretrained(src, dtype=torch.float32).eval()

    def pool(x, mask):
        h = model(**{k: torch.from_numpy(v) for k, v in x.items() if k != "num_soft_tokens_per_image"}).last_hidden_state
        m = torch.from_numpy(mask).unsqueeze(-1).float()
        return l2n(((h * m).sum(1) / m.sum(1))[0].numpy())

    with torch.no_grad():
        img = {n: pool(x, x["attention_mask"]) for n, x in feats.items()}
        qry = {}
        for q, ids in qids.items():
            a = np.array([ids], dtype=np.int64)
            qry[q] = pool({"input_ids": a, "attention_mask": np.ones_like(a)}, np.ones_like(a))
    save_variant(out, "torch", ts, img, qry)

    empty = np.zeros((0, 512), dtype=np.float32)
    for variant, suffix in {"fp32": "", "q8": "_quantized", "q4": "_q4"}.items():
        vs = ort.InferenceSession(os.path.join(onnx_dir, f"vision_encoder{suffix}.onnx"), providers=["CPUExecutionProvider"])
        ms = ort.InferenceSession(os.path.join(onnx_dir, f"model{suffix}.onnx"), providers=["CPUExecutionProvider"])

        def run_model(ids, image_features=empty):
            a = np.array([ids], dtype=np.int64)
            return l2n(ms.run(["sentence_embedding"], {"input_ids": a, "attention_mask": np.ones_like(a),
                                                       "image_features": image_features, "video_features": empty,
                                                       "audio_features": empty})[0][0])
        img_o = {}
        for n, x in feats.items():
            f = vs.run(None, {"pixel_values": x["pixel_values"].astype(np.float32),
                              "pixel_position_ids": x["image_position_ids"].astype(np.int64)})[0]
            img_o[n] = run_model(x["input_ids"][0].tolist(), f)
        qry_o = {q: run_model(ids) for q, ids in qids.items()}
        save_variant(out, variant, ts, img_o, qry_o, img, qry)


def save_variant(out, variant, ts, img, qry, ref_img=None, ref_qry=None):
    with open(os.path.join(out, f"emb_{variant}.json"), "w", encoding="utf-8") as f:
        json.dump({"images": {k: v.tolist() for k, v in img.items()}, "queries": {k: v.tolist() for k, v in qry.items()}},
                  f, ensure_ascii=False)
    rep = retrieval_report(ts, img, qry)
    if ref_img is not None:
        ci = [float(img[k] @ ref_img[k]) for k in img]
        cq = [float(qry[k] @ ref_qry[k]) for k in qry]
        rep += f"\ncosine vs torch fp32: images min {min(ci):.5f} mean {np.mean(ci):.5f}; queries min {min(cq):.5f} mean {np.mean(cq):.5f}"
    with open(os.path.join(out, f"retrieval_{variant}.txt"), "w", encoding="utf-8") as f:
        f.write(rep + "\n")
    print(f"== {os.path.basename(out)} {variant}\n{rep}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", choices=["siglip2", "siglip2-b32", "eg2"], default="siglip2")
    ap.add_argument("--soft", type=int, default=280, help="EmbeddingGemma 2 vision token budget (70/140/280/560/1120)")
    a = ap.parse_args()
    if a.model == "eg2":
        run_eg2(a.soft)
    else:
        run_siglip("siglip2-base-patch16-224" if a.model == "siglip2" else "siglip2-base-patch32-256")


if __name__ == "__main__":
    main()
