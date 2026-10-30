# Сборка общего компонента ONNX Runtime для скачиваемых модулей (точное распознавание, смысловой поиск):
# Screeny-OrtRuntime-<N>.zip — onnxruntime.dll 1.31.0, рантайм Visual C++, лицензии и module.json.
#   powershell -File tools/pack-ort-runtime.ps1                 — версия 1
#   powershell -File tools/pack-ort-runtime.ps1 -Version 2 -OutputDir C:\tmp\ort
# Результат в releases/ort-runtime/ (git-ignored): архив и рядом .sha256. SHA256 и размер вписываются в
# src/Screeny/Modules/OrtRuntimeModule.cs, затем архив прикладывается к релизу GitHub с тегом ort-runtime-<N>.
# Модули с моделями (pack-ocr-module.ps1, pack-semantic-module.ps1) ссылаются на версию компонента в module.json
# ("requires"), сами библиотек не везут: одна копия нативного ORT на процесс.
param(
    [int]$Version = 1,
    [string]$OutputDir,
    [string]$CrtDir
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'module-zip.ps1')
if (-not $OutputDir) { $OutputDir = Join-Path $root 'releases/ort-runtime' }
if ($Version -lt 1) { throw "Версия компонента — целое число от 1." }

# ONNX Runtime — ровно та версия, что у Microsoft.ML.OnnxRuntime.Managed в src/Screeny/Screeny.csproj.
$ortVersion = '1.31.0'
$ort = Join-Path $env:USERPROFILE ".nuget/packages/microsoft.ml.onnxruntime/$ortVersion"
if (-not (Test-Path $ort)) { throw "Нет пакета Microsoft.ML.OnnxRuntime $ortVersion в кэше NuGet. Скачайте его так (см. tools/ocr-module/README.md): dotnet new console -o `$env:TEMP\ort-restore; dotnet add `$env:TEMP\ort-restore package Microsoft.ML.OnnxRuntime --version $ortVersion" }

# Рантайм Visual C++ (от него зависит onnxruntime.dll): распространяемые файлы из установленной Visual Studio.
if (-not $CrtDir) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw 'Не найден vswhere.exe: укажите -CrtDir <папка Microsoft.VC14x.CRT (x64)>.' }
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $CrtDir = Get-ChildItem (Join-Path $vs 'VC/Redist/MSVC') -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Get-ChildItem (Join-Path $_.FullName 'x64') -Directory -Filter 'Microsoft.VC*.CRT' } | Select-Object -First 1 -ExpandProperty FullName
    if (-not $CrtDir) { throw 'Не найден рантайм Visual C++ (VC/Redist/MSVC/*/x64/Microsoft.VC*.CRT): укажите -CrtDir.' }
}

# Имя в архиве → исходный файл и ожидаемый SHA256.
$files = [ordered]@{
    'onnxruntime.dll'                         = @((Join-Path $ort 'runtimes/win-x64/native/onnxruntime.dll'), '0c42dc4e278f0968fdf9ebf1b1e54a74c657a9942e70803e453c91993451cf4c')
    'onnxruntime_providers_shared.dll'        = @((Join-Path $ort 'runtimes/win-x64/native/onnxruntime_providers_shared.dll'), 'e45d471ea2a842b268c51291b96d53568b0dae10807f43d1fb7dc0eb84e83723')
    # Рантайм VC++ 14.50.35710 (Visual Studio 18). Обновилась Visual Studio — хеши не сойдутся: это новая версия
    # компонента (новые хеши здесь и в OrtRuntimeModule.cs), а не тихая пересборка того же номера.
    'vcruntime140.dll'                        = @((Join-Path $CrtDir 'vcruntime140.dll'), '184146852727a9db4eea06178716bec3cdbb1015c911f6b0f915b184ad7775b2')
    'vcruntime140_1.dll'                      = @((Join-Path $CrtDir 'vcruntime140_1.dll'), 'e6bfb3662ab4b1969a73441dbe35c96d51441b6bff8cf1fe7430bd5b246ca605')
    'msvcp140.dll'                            = @((Join-Path $CrtDir 'msvcp140.dll'), 'def46aa6a8f72f27bafac0c43334419486a4d1dcdb6c479a8ef7034b3e1fa4cb')
    'msvcp140_1.dll'                          = @((Join-Path $CrtDir 'msvcp140_1.dll'), '2dd670f874562fbdca5b022df1943d70a57ba91fde559280e3a1daebe4db2380')
    'LICENSES/NOTICE.txt'                     = @((Join-Path $root 'tools/ort-runtime/LICENSES/NOTICE.txt'), 'f17e6793be0677d83275925399bce474de4a92ad0a40f00e0af7399142f3b8f4')
    'LICENSES/onnxruntime-LICENSE.txt'        = @((Join-Path $ort 'LICENSE'), 'c250d6278f0b47a6439fb7592b08b58a55eb9f535aa49a1db63211c3f982b674')
    'LICENSES/onnxruntime-ThirdPartyNotices.txt' = @((Join-Path $ort 'ThirdPartyNotices.txt'), 'c53a76501ef60db6f865f20599f220761201ac4057683acefdab37d861b86622')
}

Write-Host "CRT: $CrtDir"
New-ModuleZip -Id 'ort-runtime' -Version $Version -Files $files -OutputDir $OutputDir -ZipName "Screeny-OrtRuntime-$Version.zip" `
    -ClassFile 'src/Screeny/Modules/OrtRuntimeModule.cs' `
    -HashHint ' Рантайм VC++ другой версии: соберите новую версию компонента и обновите хеши в скрипте и в OrtRuntimeModule.cs.'
