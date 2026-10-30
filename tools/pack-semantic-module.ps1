# Сборка скачиваемого модуля смыслового поиска по истории (EmbeddingGemma 2, 4-bit ONNX): Screeny-SemanticModule-<N>.zip.
#   powershell -File tools/pack-semantic-module.ps1 -ModelsDir <папка с embeddinggemma-2-ONNX и embeddinggemma-2>
# По умолчанию модели берутся из tools/semantic-module/work/hf (git-ignored, см. tools/semantic-module/README.md,
# раздел Reproduce): onnx-community/embeddinggemma-2-ONNX@daa72c5 (текстовая модель и визуальный кодировщик q4) и
# tokenizer.model из google/embeddinggemma-2@f9972ac. Имена файлов сохраняются: графы ссылаются на свои *.onnx_data.
# Результат в releases/semantic-module/ (git-ignored): архив и .sha256. SHA256 и размер вписываются в
# src/Screeny/Modules/SemanticModule.cs, затем архив прикладывается к релизу GitHub с тегом semantic-module-<N>.
# Нативный ORT — общий компонент ort-runtime (tools/pack-ort-runtime.ps1), его версия записана в module.json.
param(
    [int]$Version = 1,
    [int]$RuntimeVersion = 1,
    [string]$ModelsDir,
    [string]$OutputDir
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'module-zip.ps1')
if (-not $ModelsDir) { $ModelsDir = Join-Path $root 'tools/semantic-module/work/hf' }
if (-not $OutputDir) { $OutputDir = Join-Path $root 'releases/semantic-module' }
if ($Version -lt 1) { throw "Версия модуля — целое число от 1." }

$onnx = Join-Path $ModelsDir 'embeddinggemma-2-ONNX/onnx'
$files = [ordered]@{
    'onnx/model_q4.onnx'               = @((Join-Path $onnx 'model_q4.onnx'), 'f9eeba97acddf139b8ee2ddf04bc30dceafa88de93fadf74d7644e0d61a477a9')
    'onnx/model_q4.onnx_data'          = @((Join-Path $onnx 'model_q4.onnx_data'), 'c3975f2d1ab7a1878ae31a7d7a9b7804a827aff3800b60dfceafce21cac3df49')
    'onnx/vision_encoder_q4.onnx'      = @((Join-Path $onnx 'vision_encoder_q4.onnx'), '7ea284226d4938f0ad921ab091f1d80a9ca699aa802984ef5cd5eec4f4761d96')
    'onnx/vision_encoder_q4.onnx_data' = @((Join-Path $onnx 'vision_encoder_q4.onnx_data'), '0a9d6c927334f152a33dd90874f65d6ea5228999abe6a450d3f7813677fa704c')
    'tokenizer.model'                  = @((Join-Path $ModelsDir 'embeddinggemma-2/tokenizer.model'), 'e594c8a90eb08d8bda498ff4747977dc827ae0c3c56b5c0d41a605a22d02ef03')
    'LICENSES/NOTICE.txt'              = @((Join-Path $root 'tools/semantic-module/LICENSES/NOTICE.txt'), 'f2c18e9706b56bc1fe4cd499c0f6059f0bc24cc35a05b3bb178e0217f7dc1402')
    'LICENSES/Apache-2.0.txt'          = @((Join-Path $root 'tools/semantic-module/LICENSES/Apache-2.0.txt'), 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30')
}

New-ModuleZip -Id 'semantic-eg2' -Version $Version -Files $files -Requires ([ordered]@{ 'ort-runtime' = $RuntimeVersion }) `
    -OutputDir $OutputDir -ZipName "Screeny-SemanticModule-$Version.zip" -ClassFile 'src/Screeny/Modules/SemanticModule.cs'
