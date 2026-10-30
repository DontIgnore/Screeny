# Сборка скачиваемого модуля точного распознавания (PaddleOCR PP-OCRv5): Screeny-OcrModule-<N>.zip.
#   powershell -File tools/pack-ocr-module.ps1                 — версия 2, модели из tools/ocr-module/models
#   powershell -File tools/pack-ocr-module.ps1 -Version 3 -OutputDir C:\tmp\ocr
# Результат в releases/ocr-module/ (git-ignored, как releases/): архив и рядом .sha256. Этот SHA256 и размер вписываются
# в src/Screeny/Modules/OcrModule.cs (см. tools/ocr-module/README.md), затем архив прикладывается к релизу GitHub с тегом
# ocr-module-<N>.
# С версии 2 в модуле только модели и лицензии: ONNX Runtime и рантайм VC++ — общий компонент ort-runtime
# (tools/pack-ort-runtime.ps1), его версия записана в module.json ("requires"). Версия 1 везла их в себе.
# Модели качаются заранее только из источника в tools/ocr-module/README.md (ModelScope RapidOCR v3.10.0); их SHA256
# сверяются ниже, при расхождении сборка прерывается.
param(
    [int]$Version = 2,
    [int]$RuntimeVersion = 1,
    [string]$ModelsDir,
    [string]$OutputDir
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'module-zip.ps1')
if (-not $ModelsDir) { $ModelsDir = Join-Path $root 'tools/ocr-module/models' }
if (-not $OutputDir) { $OutputDir = Join-Path $root 'releases/ocr-module' }
if ($Version -lt 2) { throw "Версия модуля без своего рантайма — от 2 (версия 1 собиралась прежним скриптом)." }

# Имя в архиве → исходный файл и ожидаемый SHA256.
$files = [ordered]@{
    'det.onnx'                = @((Join-Path $ModelsDir 'ch_PP-OCRv5_det_mobile.onnx'), '4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae')
    'rec.onnx'                = @((Join-Path $ModelsDir 'cyrillic_PP-OCRv5_rec_mobile.onnx'), '90f761b4bfcce0c8c561c0cb5c887b0971d3ec01c32164bdf7374a35b0982711')
    'LICENSES/NOTICE.txt'     = @((Join-Path $root 'tools/ocr-module/LICENSES/NOTICE.txt'), '386d8f7dbb8c7e4c7467af3fa624f0f073df53d70b517980ad4790a9a037d270')
    'LICENSES/Apache-2.0.txt' = @((Join-Path $root 'tools/ocr-module/LICENSES/Apache-2.0.txt'), 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30')
}

New-ModuleZip -Id 'ocr-paddle' -Version $Version -Files $files -Requires ([ordered]@{ 'ort-runtime' = $RuntimeVersion }) `
    -OutputDir $OutputDir -ZipName "Screeny-OcrModule-$Version.zip" -ClassFile 'src/Screeny/Modules/OcrModule.cs'
