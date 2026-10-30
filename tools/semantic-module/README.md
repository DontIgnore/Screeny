# Semantic search module: research spike

Goal: an optional, offline module that lets the history search find screenshots by meaning ("котик", "cat",
"error dialog", "счёт") in Russian, English and the other UI languages. This folder holds the scripts, a C# console
prototype and these findings. Nothing here is wired into the app (`src/` is untouched).

Candidates: **EmbeddingGemma 2** (`google/embeddinggemma-2`, primary per the owner) and **SigLIP 2**
(`google/siglip2-base-patch16-224`, baseline). Everything big (venv, checkpoints, ONNX files, test images) lives in
`work/`, which is git-ignored.

## Verdict

**Recommended: EmbeddingGemma 2, text model + vision encoder only, `q4` ONNX from onnx-community, vision budget 280
soft tokens, no query prefix.** Download about 288 MB (+ the
shared ONNX Runtime, see below). License is Apache-2.0, so a GPL-3 project can ship it in a module zip.

It is the only candidate that finds screenshots by the *text in them* at real desktop density, and across languages
(English query, Russian screenshot and the reverse): 8/8 vs 0/8 for SigLIP 2 on the desktop-density subset. Photos and
UI kinds ("cat", "график", "error dialog") work for both.

What makes it not a free win: **indexing cost**. About 9–11 s of CPU per screenshot on 1–2 threads (2 s on all 24
threads) on this i7-13700K, and about 1 GB working set while indexing. That is fine for new captures, one at a time
in the background, but a backfill of an existing history is hours (see Cost). The module is viable only with
backfill done lazily (idle, on AC power, newest first) and/or at budget 140 (3.8 s per image, but q4 at 140 finds only
6/8 small-text screenshots, top-1 and hit@3).

SigLIP 2 is cheaper (about 0.27 s per image on 2 threads, ~30× less) but it does not read small UI text at 224×224
(square squash), and its weights are bigger at the same fidelity (fp32 1.5 GB; the 4/8-bit variants lose quality or
need ~340 MB). Keep it only as a fallback if EmbeddingGemma 2's indexing cost is rejected.

## 1. License

| Item | Verdict |
|---|---|
| `google/embeddinggemma-2` (commit `f9972ac`, created 2026-09-14, updated 2026-10-28) | Card: `license: apache-2.0`. The "License" link goes to `ai.google.dev/gemma/docs/gemma_4_license`, which is the plain Apache License 2.0 text (no Gemma Terms of Use). Repo is **not gated**. |
| Card sentence "Deployments must adhere to the Gemma Prohibited Use Policy" | A statement in the model card, not a term of the Apache-2.0 grant. Not a redistribution restriction. Link it in the module NOTICE as information. |
| `onnx-community/embeddinggemma-2-ONNX` (commit `daa72c5`, 2026-10-06) | Apache-2.0, a conversion of the above. Its fp32 graph matches the current `google/` weights (cos 1.00000), so the later upstream update did not change the weights. |
| Tokenizer `tokenizer.model` (Gemma 3/4, 262k) | Ships in the same Apache-2.0 repo. |
| `google/siglip2-*` (all checkpoints checked) | Apache-2.0 on HF; upstream big_vision: code Apache-2.0, "other materials" CC-BY-4.0. Both GPLv3-compatible. |
| SigLIP 2 Gemma tokenizer (256k) | Byte-identical (SHA256 `61a7b147…`) to `tokenizer/tokenizer.model` in `google/gemma_pytorch`, whose repo license is Apache-2.0; the big_vision GCS copy differs only in the name of piece 255999. Not under the Gemma ToU. |

The module zip is separate data shipped next to the GPL program (like the OCR models): redistribute with
`LICENSES/Apache-2.0.txt` and a NOTICE that credits Google DeepMind (EmbeddingGemma 2) and the onnx-community ONNX
export, with the repo commits and file hashes below.

Alternatives checked (HF license tags): `jinaai/jina-clip-v2` CC-BY-NC-4.0 (no); `facebook/nllb-clip-*` gated, license not checked;
`sentence-transformers/clip-ViT-B-32-multilingual-v1` Apache-2.0 (old, weak); `laion/CLIP-ViT-B-32-xlm-roberta-base`
MIT; `visheratin/mexma-siglip2` MIT (1 B params); `google/siglip-base-patch16-256-multilingual` Apache-2.0. None reads
text in images like EmbeddingGemma 2; none was evaluated further.

## 2. Size

Parameters, from the safetensors header (sum by component):

| EmbeddingGemma 2 | Params |
|---|---:|
| text: token embedding 262144×512 | 134.2 M |
| text: 24 layers + PLE + 512→768 projection | 136.8 M |
| vision tower (16 layers, patch embedder, projection) | 167.8 M |
| audio tower (not shipped) | 305.6 M |
| **total / shipped (text + vision)** | **744.4 M / 438.8 M** |

Download sizes (bytes of the files that would go in the zip; audio and video are not needed: video frames would use
the same vision encoder, audio is a separate graph we just don't ship):

| Variant | Text model | Vision encoder | + tokenizer | Total | Image fidelity vs fp32 (min cos) |
|---|---:|---:|---:|---:|---:|
| fp32 | 1084.6 MB | 671.1 MB | 4.7 MB | 1760 MB | 1 |
| fp16 | 542 MB | 336 MB | | 883 MB | — card: **do not use fp16** (activations overflow) |
| q8 (`_quantized`) | 314.2 MB | 195.4 MB | 4.7 MB | 514 MB | 0.987 (280) |
| **q4** | **174.5 MB** | **109.1 MB** | **4.7 MB** | **288 MB** | 0.956 (280); queries 0.980 |
| q8 vision + q4 text | 174.5 MB | 195.4 MB | 4.7 MB | 375 MB | (not measured; vision q8 is the bigger fidelity win) |

SigLIP 2 B/16-224 (375 M params: vision 92.9 M, text 282.3 M of which 196.6 M is the 256k×768 embedding):

| Variant | Vision | Text | Image / query min cos | Retrieval (35 q) |
|---|---:|---:|---:|---|
| fp32 | 372 MB | 1129 MB | 1 / 1 | 23/35 top-1 |
| dynamic int8 (`quantize_dynamic`) | 99 MB | 283 MB | **0.61** / 0.87 | 15/35 (ViT activations break int8) |
| weight-only 8-bit (`MatMulNBits` 8) + 4-bit embedding | 114 MB | 222 MB | 0.999 / 0.87 | 23/35, ~340 MB |
| 4-bit everything | 69 MB | 179 MB | 0.94 / 0.88 | 16/35 |

## 3. ONNX on ONNX Runtime 1.31.0

- Other formats exist but don't run on ONNX Runtime and were not evaluated: LiteRT-LM
  (`litert-community/embeddinggemma-2-740m-litert-lm`, 485 MB generic; text-only 165 MB) and GGUF for llama.cpp
  (`ggml-org` / `unsloth`: text Q8_0 310 MB, UD-Q4_K_XL 176 MB, vision `mmproj` Q8_0 555 MB), all Apache-2.0.
- EmbeddingGemma 2: the onnx-community export already exists and loads on ORT 1.31.0 (Python and the C# prototype,
  CPU EP). Graphs: `vision_encoder*.onnx` (`pixel_values` [1, P, 768] patches + `pixel_position_ids` [1, P, 2] →
  `image_features` [n, 512]) and `model*.onnx` (`input_ids`, `attention_mask`, `image_features`, `video_features`,
  `audio_features` → `sentence_embedding` [1, 768], mean-pooled and L2-normalized in-graph). Uses `MatMulNBits`,
  `GatherBlockQuantized`, `MultiHeadAttention`, `RotaryEmbedding` (com.microsoft opset 1) — all present in 1.31.0. No
  own export was needed; weights are external data (`*.onnx_data`, name referenced from the graph: keep file names).
- SigLIP 2: `export_onnx.py` (torch 2.15 legacy exporter, opset 17), runs on 1.31.0.

## 4. Tokenizer and embedding parity (C# vs Python)

C#: `Microsoft.ML.Tokenizers` 2.0.1 (MIT, +0.8 MB with Google.Protobuf) `SentencePieceTokenizer` on `tokenizer.model`.

| | Result |
|---|---|
| EmbeddingGemma 2 token ids, 60 strings (EN, RU, ZH, JA, ES, emoji incl. ZWJ, rare CJK byte fallback, digits, tabs/newlines, leading/trailing/multiple spaces, URLs, paths, `<eos>`/`<pad>`/`<|image|>` typed as text, empty, >64 tokens; each also with the query prefix) | **60/60 identical** to HF `tokenizer(s, split_special_tokens=True)` (BOS 2 … EOS 1). Special-token lookalikes stay plain text in both. |
| SigLIP 2 token ids, 30 strings, lowercased + EOS + pad 0 to 64 + truncation (EOS sticky at position 63) | **30/30 identical**. Lowercasing matches Python `str.lower()` incl. `İ` → `i̇` and final sigma `ΟΔΟΣ` → `οδος` (both differ from .NET `ToLowerInvariant`; handled in `PythonLower`). |
| HF fast tokenizer vs `sentencepiece` library | Identical except `<|image|>`: sentencepiece maps it to the single user-defined id 258880. We follow HF (plain text); a user can't inject an image placeholder. |
| Image preprocessing | C# port of Pillow's resampler (`PilResize.cs`, bicubic/bilinear, fixed-point). Not bit-identical: PNG ≤0.12 % of values differ, JPEG photos ≤0.9 % (WIC vs libjpeg decode), by 1–2 8-bit levels. For EG2 even PNGs differ slightly because transformers resizes with torchvision (a port of Pillow's code, not byte-identical). Patch order, position ids, padding identical. The cosines below carry the parity claim. |
| EG2 embeddings, same input (Python-preprocessed pixels into C# ORT) | cos **1.000000** (fp32 and q4, budgets 140 and 280) |
| EG2 embeddings, full C# pipeline (WIC decode + C# resize + C# tokens) vs Python ORT, same ONNX | min cos **0.99985** (140, fp32) / **0.99981** (140, q4); **0.99997** (280, fp32) / **0.99993** (280, q4); queries 1.000000 |
| SigLIP 2, full C# pipeline vs Python ORT | min cos **0.999997**; queries 1.000000 |

Quantization loss is separate from parity: q4 vs torch fp32 is 0.956 min for images, 0.980 for queries.

## 5. Retrieval quality (synthetic, 19 + 5 images, 35 queries)

Set (`make_testset.py`): 4 CC0 photos (2 cats, 2 dogs), a chat with a cat photo, chat, code editor, 2 invoices (RU/EN),
2 charts, map, error dialog, login form, spreadsheet, 4 text-only documents (lease contract RU, recipe RU, flight
e-mail EN, Wi-Fi note EN), and 5 **desktop-density** screenshots (1920×1080 / 2560×1440, 14 px text: hotel booking
e-mail, tax notice in a browser, VPN settings, warranty PDF, plus a news page as distractor). Queries in RU and EN;
text-only concepts are queried in both languages (cross-lingual).

| Model, variant, setting | top-1 (35) | hit@3 (35) | desktop subset top-1 / hit@3 (8) |
|---|---:|---:|---:|
| SigLIP 2 B/16-224 fp32 | 23 | 24 | 0 / 0 |
| SigLIP 2 w8 (weight-only 8-bit) | 23 | 25 | 0 / 0 |
| EG2 fp32, budget 70, search prefix | 30 | 31 | 3 / 4 |
| EG2 fp32, budget 140, search prefix | 33 | 35 | 6 / 8 |
| EG2 fp32, budget 280, search prefix | 30 | 33 | 8 / 8 |
| **EG2 fp32, budget 280, no prefix** | **35** | **35** | **8 / 8** |
| EG2 fp32, budget 560, no prefix | 33 | 34 | 8 / 8 |
| EG2 q4, budget 280, search prefix (Python / C#) | 31 / 31 | 32 / 32 | 8 / 8 |
| **EG2 q4, budget 280, no prefix (C#)** | **33** | **35** | **8 / 8** |
| EG2 q4, budget 140, no prefix (C#) | 33 | 33 | 6 / 6 |
| EG2 q4, budget 140, search prefix (C#) | 32 | 33 | 5 / 6 |

The queries the owner named:

| Query | SigLIP 2 fp32 top-3 | EG2 q4 280 (search prefix) top-3 |
|---|---|---|
| котик | chat_with_cat ✓, cat_siamese ✓, chat | chat_with_cat ✓, cat_siamese ✓, cat_sitting ✓ |
| cat | cat_siamese ✓, cat_sitting ✓, chat_with_cat ✓ | cat_sitting ✓, cat_siamese ✓, chat_with_cat ✓ |
| график | chart_line ✓, chart_bar ✓, spreadsheet | chart_line ✓, chat, chat_with_cat |
| error dialog | error_dialog ✓, code_editor, … | error_dialog ✓, login_form, chat |
| счёт | invoice_ru ✓, chart_line, spreadsheet | chat, invoice_ru ✓, chat_with_cat (top-1 ✓ with no prefix) |
| code | code_editor ✓, … | code_editor ✓, login_form, chat |
| налог / tax notice (14 px text) | ✗ / ✗ | desk_browser_tax ✓ / ✓ |
| гарантийный талон / warranty card | ✗ / ✗ | desk_pdf_warranty ✓ / ✓ |
| rental agreement (RU document) | ✗ | doc_lease_ru ✓ |

**Does EmbeddingGemma 2 read text in screenshots better?** Yes, clearly, when the budget is high enough: at 280 soft
tokens a 1920×1080 screenshot becomes 1056×576 px and 14 px text survives; at 70 it becomes ~528×288 and most UI text
is gone (3/8). SigLIP 2 at 224×224 reads only large text (it gets the large-font Russian lease and recipe, not the
English query for the Russian lease, and none of the desktop-density ones).

Caveats: tiny synthetic set; prefix and budget were chosen on the same queries they are scored on. EG2 shows
**hubness**: text-heavy Cyrillic `chat.png` and `login_form.png` sit in the top 3 of many queries, and scores are
compressed (relevant ≈ 0.62–0.78, others ≈ 0.55–0.70), so absolute thresholds are fragile. The SigLIP baseline is the
cheapest square-squash variant (the NaFlex / 384–512 px variants would read more text at higher cost).

### Combining with plain text (OCR) search

- Run both and merge by **reciprocal rank fusion** (score = Σ 1/(60 + rank)), lexical hits first when the query matches
  OCR text literally. Don't threshold EG2 cosines absolutely; cut at "within δ of the best score" or top-K.
- The query prefix from the model card (`task: search result | query: `) made little difference at budgets 70/140
  (±1 query) and hurt at 280 (fp32 30 vs 35 top-1; C# q4 31 vs 33). Use no prefix; re-check on real data.
- EG2 is also a strong multilingual *text* embedder in the same space: embedding the OCR text
  (`title: none | text: <ocr>`, ~20–60 ms) gives cross-lingual semantic text search and could let the image side run at
  a lower budget (70–140) for visuals while OCR covers small text. Not measured here; worth one experiment.
- SigLIP 2 gives calibrated sigmoid scores (`sigmoid(cos·exp(4.72) − 16.77)`) if it is ever used; EG2 has no such head.

## 6. Cost (i7-13700K, 24 threads, C# + ORT 1.31.0, PNG test images, fresh process per row)

| Model / setting | Threads | Load | Per image | Per query | Working set while indexing |
|---|---:|---:|---:|---:|---:|
| EG2 q4, budget 280 | 1 | 0.65 s | 11.4 s | 74 ms | ~0.95–1.1 GB |
| EG2 q4, budget 280 | 2 | 0.63 s | 8.8 s | 58 ms | ~0.95–1.1 GB |
| EG2 q4, budget 280 | 24 (default) | 0.77 s | 2.1 s | 16 ms | ~1.0–1.1 GB |
| EG2 q4, budget 140 | 2 | 0.59 s | 3.1–3.8 s | 57 ms | ~0.65–0.8 GB |
| EG2 q4, budget 140 | 24 | 0.69 s | 1.1–1.7 s | 22 ms | ~0.8 GB |
| EG2 q4, budget 70 | 2 | 0.63 s | 1.1–1.7 s | 33–58 ms | ~0.5–0.6 GB |
| EG2 q8, budget 140 | 2 | 0.43 s | 4.3 s | 320 ms | ~0.9 GB |
| SigLIP 2 fp32 | 2 | 1.8 s | 0.27 s | 93 ms | ~1.6 GB (fp32 weights) |
| SigLIP 2 fp32 | 24 | 1.9 s | 0.08 s | 29 ms | ~1.6 GB |
| SigLIP 2 w8 (8-bit MatMulNBits, fp32 embedding) | 2 | 1.4 s | 0.47 s | 262 ms | ~1.0 GB (8-bit MatMulNBits is slower than fp32 on this CPU) |

All rows ran at normal priority on an otherwise idle machine. The plan runs indexing at low priority, where Windows
tends to schedule it on E-cores: q4 / 280 / 2 threads at Idle priority took 8.5 s per image, and **pinned to the
E-cores 13.6 s** (≈1.6× slower). Read the backfill numbers below as optimistic by up to ~1.6× for low-priority work.

Cost is dominated by the vision encoder: budget 280 = 2520 patches through a 16-layer ViT with full attention
(attention matrices alone ~300 MB per layer), so 280 costs ~4–6× budget 70. EG2 at 280 is ~30× SigLIP 2 per image.

Backfill at budget 280, 2 threads: **1000 screenshots ≈ 2.5–3 h (≈ 4 h on E-cores), 5000 ≈ 12–19 h** of background
CPU on this desktop CPU; a 4-core laptop is several times slower. At budget 140: ≈ 1 h per 1000. New captures cost one embedding each
(2–11 s), which is invisible in a background queue. Queries are 20–75 ms (text model only, ~16–58 ms + tokenization).
A query-only session (text model, no vision) would hold ~0.3 GB (not measured separately); unload both sessions when
idle.

## 7. Sharing ONNX Runtime between modules

Problem: the OCR module ships `onnxruntime.dll` 1.31.0 + VC++ runtime in its own folder, and `OrtNative` sets one
`DllImportResolver` for the managed ORT assembly per process. A second module with its own copy would either be
ignored (first load wins, the resolver can't be set twice) or load a second native ORT into the process.

Proposal (concrete):

1. **A third downloadable component, `onnxruntime-1.31.0`**, id `ort-runtime`, version N (`%LocalAppData%\Screeny\Modules\ort-runtime-N\`):
   `onnxruntime.dll`, `onnxruntime_providers_shared.dll`, the four VC++ DLLs, ORT LICENSE + ThirdPartyNotices,
   `module.json`. About 19 MB (`onnxruntime.dll` 17.3 MB, VC++ ~1.2 MB), stored, not compressed. Packed by a
   `tools/pack-ort-runtime.ps1` split out of `pack-ocr-module.ps1` (same deterministic zip, same pinned hashes). Its
   version is tied to `Microsoft.ML.OnnxRuntime.Managed` in `Screeny.csproj` ([1.31.0]).
2. **OCR module v2 and the semantic module v1 carry only models + licenses**, and their `module.json` gets
   `"requires": { "ort-runtime": N }`. `ModuleSpec` gains `Requires`; `ModuleInstaller` installs the runtime first when
   a feature is downloaded (two SHA256-pinned downloads, one progress bar), and removes it only when no installed
   module requires it.
3. **`OrtNative` moves out of `Ocr/Paddle`** (e.g. `Modules/OrtNative.cs`) and is called with the runtime folder only.
   `EnsureLoaded(folder)` stays once-per-process; a second call with another folder throws instead of silently
   reusing (the prototype's `OrtRuntime.EnsureLoaded` does exactly this). Both `PaddleEngine` and the semantic engine
   call `OrtNative.EnsureLoaded(OrtRuntimeModule.ReadyFolder)`. The `App.PreciseOcr` "is loaded from" check becomes a
   check on the runtime folder.
4. Transition: an installed OCR module v1 (with its own DLLs) gets the existing "Update module" flow when the app needs
   v2. Optional shortcut: if ORT is already loaded from OCR v1 and that `onnxruntime.dll` has the pinned SHA256 of the
   runtime component, accept it (same bytes, same version).

Confirmed: both EmbeddingGemma 2 graphs (q4, q8, fp32) and SigLIP 2 run on the NuGet 1.31.0 `onnxruntime.dll` loaded
this way from a folder (`prototype/OrtRuntime.cs`).

Alternative considered: ship `onnxruntime.dll` in the app installer. Simpler (no dependency graph), but +17 MB for
every user (installer is ~17 MB today), against the "OCR is optional" decision.

## 8. Integration plan (app)

- `src/Screeny/Modules/SemanticModule.cs`: like `OcrModule.cs` — `Id = "semantic-eg2"`, `Version`, `ZipSha256`,
  `ManifestSha256`, `ZipSize`, download URL with tag `semantic-module-N`, `Requires = ort-runtime N`.
- `tools/pack-semantic-module.ps1`: like `pack-ocr-module.ps1`, pinned SHA256 of the shipped files:

  | File (keep names: the graphs reference their `_data` files) | SHA256 |
  |---|---|
  | `onnx/model_q4.onnx` | `f9eeba97acddf139b8ee2ddf04bc30dceafa88de93fadf74d7644e0d61a477a9` |
  | `onnx/model_q4.onnx_data` | `c3975f2d1ab7a1878ae31a7d7a9b7804a827aff3800b60dfceafce21cac3df49` |
  | `onnx/vision_encoder_q4.onnx` | `7ea284226d4938f0ad921ab091f1d80a9ca699aa802984ef5cd5eec4f4761d96` |
  | `onnx/vision_encoder_q4.onnx_data` | `0a9d6c927334f152a33dd90874f65d6ea5228999abe6a450d3f7813677fa704c` |
  | `tokenizer.model` (from `google/embeddinggemma-2`) | `e594c8a90eb08d8bda498ff4747977dc827ae0c3c56b5c0d41a605a22d02ef03` |

  Sources: `onnx-community/embeddinggemma-2-ONNX@daa72c5`, `google/embeddinggemma-2@f9972ac`. Plus
  `LICENSES/Apache-2.0.txt` and `NOTICE.txt`. The stored-zip writer reads whole files into memory; at 174 MB that
  still works but should stream.
- `src/Screeny/Semantic/`: `GemmaTokenizer` (+ `Microsoft.ML.Tokenizers` package), `PilResize`,
  `EmbeddingGemmaEncoder` (from `prototype/`), `SemanticIndex` (vectors per history item: model id + version + 768 or
  MRL-256 floats, fp16, in a sidecar file next to the history index; 1000 items ≈ 0.5–1.5 MB; brute-force cosine is
  fine to 100k), `SemanticSearchProvider : IHistorySearchProvider` (embed query once, rank, return item ids + scores
  for fusion with the OCR/text provider).
- Indexing: a dedicated low-priority queue (one background thread, `THREAD_MODE_BACKGROUND_BEGIN`/below-normal
  priority, `IntraOpNumThreads = 2`), new captures first, backfill newest → oldest only when idle and on AC power,
  pausable, resumable (persist progress), never during capture/overlay. Sessions created lazily and disposed after a
  few idle minutes (≈1 GB while indexing). Re-index when the module version changes.
- Settings/wizard: same consent + download flow as precise OCR; show index progress ("Indexed 412 of 2 310").

## What would make it not viable

- **Backfill cost on weak hardware** (hours per thousand screenshots at budget 280). Mitigations: budget 140 for
  backfill, OCR-text embeddings for small text, lazy indexing. If the owner wants "install and search everything in
  minutes" on a laptop, neither candidate's image path at text-reading resolution does that.
- **~1 GB RAM while indexing** at budget 280 (vision attention over 2520 patches). Must be unloaded when idle.
- **Freshness**: the model is six weeks old and the ONNX conversion is third-party (onnx-community, not Google);
  pin the files by SHA256 and re-verify parity before each module release (`reference.py` + `SemanticProto parity`).
- **No fp16 path**; q4 is the only variant under ~300 MB, at 0.956 min image fidelity (retrieval unchanged on this set).

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
