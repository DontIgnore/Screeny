# Semantic search module: research spike

Goal: an optional, offline module that lets the history search find screenshots by meaning ("котик", "cat",
"error dialog", "счёт") in Russian, English and the other UI languages. This folder holds the scripts, a C# console
prototype and these findings. Nothing here is wired into the app (`src/` is untouched).

Candidates: **EmbeddingGemma 2** (`google/embeddinggemma-2`, primary per the owner) and **SigLIP 2**
(`google/siglip2-base-patch16-224`, baseline). Everything big (venv, checkpoints, ONNX files, test images) lives in
`work/`, which is git-ignored.

<!-- RESULTS -->

## Reproduce

All commands from the repo root. Python 3.12, `work/` is created on the fly.

```powershell
$W = 'tools/semantic-module/work'
py -3.12 -m venv $W/venv
& $W/venv/Scripts/pip install torch==2.15.0 torchvision==0.29.2 --index-url https://download.pytorch.org/whl/cpu
& $W/venv/Scripts/pip install transformers==5.22.0 onnx==1.23.2 onnxruntime==1.31.0 onnxscript sentencepiece protobuf pillow numpy "sentence-transformers>=6.1"

# checkpoints (Hugging Face, not gated)
& $W/venv/Scripts/hf download google/siglip2-base-patch16-224 --local-dir $W/hf/siglip2-base-patch16-224
& $W/venv/Scripts/hf download google/embeddinggemma-2 --local-dir $W/hf/embeddinggemma-2
# onnx-community export of EmbeddingGemma 2: text model + vision encoder only (no audio, no fp16)
& $W/venv/Scripts/hf download onnx-community/embeddinggemma-2-ONNX config.json tokenizer.json tokenizer_config.json `
    onnx/model.onnx onnx/model.onnx_data onnx/model_quantized.onnx onnx/model_quantized.onnx_data onnx/model_q4.onnx onnx/model_q4.onnx_data `
    onnx/vision_encoder.onnx onnx/vision_encoder.onnx_data onnx/vision_encoder_quantized.onnx onnx/vision_encoder_quantized.onnx_data `
    onnx/vision_encoder_q4.onnx onnx/vision_encoder_q4.onnx_data --local-dir $W/hf/embeddinggemma-2-ONNX

$py = "$W/venv/Scripts/python"
& $py -I tools/semantic-module/export_onnx.py --model google/siglip2-base-patch16-224   # SigLIP 2 -> vision.onnx, text.onnx
& $py -I tools/semantic-module/quantize.py --model siglip2-base-patch16-224             # int8 / w8 / q4 variants
& $py -I -X utf8 tools/semantic-module/make_testset.py                                 # 19 images, 27 queries
& $py -I -X utf8 tools/semantic-module/reference.py --model siglip2                     # tokens, pixels, embeddings, retrieval
& $py -I -X utf8 tools/semantic-module/reference.py --model eg2
& $py -X utf8 tools/semantic-module/eg2_experiments.py --budgets 70 140 280 --all-prefixes

dotnet build -c Release tools/semantic-module/prototype
$P = 'tools/semantic-module/prototype/bin/Release/net10.0-windows10.0.19041.0/SemanticProto.exe'
& $P tokens eg2;  & $P tokens siglip2                      # token-id parity with Python
& $P parity eg2 --variant q4 --soft 280                    # preprocessing + embedding parity, retrieval table
& $P parity siglip2 --variant w8
& $P bench eg2 --variant q4 --soft 70 --threads 1,2,0      # load time, ms/image, ms/query, memory
& $P search eg2 --variant q4 --soft 70 --prefix none       # retrieval table for a setting
```

The prototype loads `onnxruntime.dll` 1.31.0 from the NuGet cache folder (`--ort DIR` to change), exactly like the app
loads it from the module folder: managed `Microsoft.ML.OnnxRuntime.Managed` 1.31.0 plus a `DllImportResolver`.

Files:

| File | What |
|---|---|
| `export_onnx.py` | SigLIP 2 → `vision.onnx` / `text.onnx` (projected features, opset 17) |
| `quantize.py` | SigLIP 2 quantization variants (dynamic int8, weight-only 8-bit, 4-bit) |
| `make_testset.py` | synthetic test set: 4 CC0 photos (Wikimedia Commons) + 15 UI screenshots drawn with Pillow |
| `reference.py` | Python ground truth: token ids, preprocessed pixels, embeddings per variant, retrieval |
| `eg2_experiments.py` | EmbeddingGemma 2 query prefix × vision token budget grid |
| `prototype/` | C# (.NET 10) tokenizer, PIL-exact resize, both encoders on ORT 1.31.0, parity + bench |
