# Precise OCR module

The precise OCR module is an optional download for Screeny. It holds only data and a native runtime: two PP-OCRv5
models, ONNX Runtime and the Visual C++ runtime it needs. All the image processing lives in the app
(`src/Screeny/Ocr/Paddle`), so a new module version is needed only when the models or the ONNX Runtime version change.

`tools/pack-ocr-module.ps1` builds the archive. This page lists what it needs and how to publish a new version.

## What you need

### 1. The models

Put them in `tools/ocr-module/models/` (git-ignored). Use only this source: the ONNX conversions of PP-OCRv5 from
the RapidOCR repository on ModelScope, release v3.10.0 (Apache-2.0, converted from the official PaddleOCR models; the
recognizer's dictionary is stored in the ONNX metadata under the key `character`).

```powershell
$dir = 'tools/ocr-module/models'
New-Item -ItemType Directory -Force $dir | Out-Null
$base = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv5'
Invoke-WebRequest "$base/det/ch_PP-OCRv5_det_mobile.onnx" -OutFile "$dir/ch_PP-OCRv5_det_mobile.onnx"
Invoke-WebRequest "$base/rec/cyrillic_PP-OCRv5_rec_mobile.onnx" -OutFile "$dir/cyrillic_PP-OCRv5_rec_mobile.onnx"
```

| File | MB | SHA256 |
|---|---:|---|
| `ch_PP-OCRv5_det_mobile.onnx` | 4.8 | `4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae` |
| `cyrillic_PP-OCRv5_rec_mobile.onnx` | 8.1 | `90f761b4bfcce0c8c561c0cb5c887b0971d3ec01c32164bdf7374a35b0982711` |

The script checks these hashes and stops if a file differs. The Cyrillic recognizer reads Latin text too.

### 2. ONNX Runtime 1.31.0

The script takes `onnxruntime.dll` and its license files from the `Microsoft.ML.OnnxRuntime` 1.31.0 package in your
NuGet cache (`%USERPROFILE%\.nuget\packages`). This must be the same version as `Microsoft.ML.OnnxRuntime.Managed` in
`src/Screeny/Screeny.csproj`. If the package is not in the cache yet, a throwaway project will fetch it:

```powershell
dotnet new console -o $env:TEMP\ort-restore
dotnet add $env:TEMP\ort-restore package Microsoft.ML.OnnxRuntime --version 1.31.0
```

### 3. The Visual C++ runtime

`onnxruntime.dll` depends on `vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll` and `msvcp140_1.dll`. The module
ships its own copies, so users don't need the VC++ Redistributable. The script finds them in the latest Visual Studio
with the C++ tools installed (`VC/Redist/MSVC/*/x64/Microsoft.VC*.CRT`), or you can point it at a folder with
`-CrtDir`. The pinned hashes are for VC++ 14.50.35710. A different Visual Studio version gives different files, which
means a new module version, not a quiet rebuild of the same number.

## Build

```powershell
powershell -File tools/pack-ocr-module.ps1 -Version 1
# -> releases/ocr-module/Screeny-OcrModule-1.zip and a .sha256 file next to it
```

The archive is deterministic: the same files give the same SHA256 in Windows PowerShell and in pwsh. The script writes
its own uncompressed zip with a fixed timestamp and entry order, and `module.json` with a fixed field order. The files
in `LICENSES/` go into the archive byte for byte, which is why this folder has `* -text` in `.gitattributes`.

## Publish a new module version

1. Change what you need (models, or `Microsoft.ML.OnnxRuntime.Managed` in `Screeny.csproj` together with
   `$ortVersion` in the script), then run `tools/pack-ocr-module.ps1 -Version N`.
2. Copy `Version = N`, `ZipSha256`, `ManifestSha256` and `ZipSize` from the script's output into
   `src/Screeny/Modules/OcrModule.cs`. The app accepts only the archive with that hash.
3. Create a GitHub release tagged `ocr-module-N` (separate from app versions) and attach `Screeny-OcrModule-N.zip`.
   The app downloads it from
   `https://github.com/DontIgnore/Screeny/releases/download/ocr-module-N/Screeny-OcrModule-N.zip`.
4. In the app's release notes, tag the item with `<!-- feature: ocr-precise -->`, so only people who use precise
   OCR see it.

To try a module without GitHub, run a dev instance with `--dev --module-source <folder with the zip>`
(see [docs/maintainers.md](../../docs/maintainers.md)).
