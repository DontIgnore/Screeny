# Build the downloadable precise OCR module (PaddleOCR PP-OCRv5 on ONNX Runtime): Screeny-OcrModule-<N>.zip.
#   powershell -File tools/pack-ocr-module.ps1                 — version 1, models from tools/ocr-module/models
#   powershell -File tools/pack-ocr-module.ps1 -Version 2 -OutputDir C:\tmp\ocr
# Output in releases/ocr-module/ (git-ignored, like releases/): the archive and a .sha256 file.
# The archive is deterministic: the same files produce the same SHA256 (name order, entry timestamps, no compression,
# fixed field order in module.json). Record this SHA256 and size in src/Screeny/Modules/OcrModule.cs (see
# tools/ocr-module/README.md), then attach the archive to the GitHub release tagged ocr-module-<N>.
# Download models in advance only from the source in tools/ocr-module/README.md (ModelScope RapidOCR v3.10.0); their SHA256
# values are verified below, and a mismatch aborts the build.
param(
    [int]$Version = 1,
    [string]$ModelsDir,
    [string]$OutputDir,
    [string]$CrtDir
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $ModelsDir) { $ModelsDir = Join-Path $root 'tools/ocr-module/models' }
if (-not $OutputDir) { $OutputDir = Join-Path $root 'releases/ocr-module' }
if ($Version -lt 1) { throw "Версия модуля — целое число от 1." }

# ONNX Runtime must match Microsoft.ML.OnnxRuntime.Managed in src/Screeny/Screeny.csproj exactly.
$ortVersion = '1.31.0'
$ort = Join-Path $env:USERPROFILE ".nuget/packages/microsoft.ml.onnxruntime/$ortVersion"
if (-not (Test-Path $ort)) { throw "Нет пакета Microsoft.ML.OnnxRuntime $ortVersion в кэше NuGet. Скачайте его так (см. tools/ocr-module/README.md): dotnet new console -o `$env:TEMP\ort-restore; dotnet add `$env:TEMP\ort-restore package Microsoft.ML.OnnxRuntime --version $ortVersion" }

# Visual C++ runtime (required by onnxruntime.dll): redistributable files from the installed Visual Studio.
if (-not $CrtDir) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path $vswhere)) { throw 'Не найден vswhere.exe: укажите -CrtDir <папка Microsoft.VC14x.CRT (x64)>.' }
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $CrtDir = Get-ChildItem (Join-Path $vs 'VC/Redist/MSVC') -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Get-ChildItem (Join-Path $_.FullName 'x64') -Directory -Filter 'Microsoft.VC*.CRT' } | Select-Object -First 1 -ExpandProperty FullName
    if (-not $CrtDir) { throw 'Не найден рантайм Visual C++ (VC/Redist/MSVC/*/x64/Microsoft.VC*.CRT): укажите -CrtDir.' }
}

# Archive name → source file and expected SHA256 (if pinned).
$files = [ordered]@{
    'det.onnx'                                = @((Join-Path $ModelsDir 'ch_PP-OCRv5_det_mobile.onnx'), '4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae')
    'rec.onnx'                                = @((Join-Path $ModelsDir 'cyrillic_PP-OCRv5_rec_mobile.onnx'), '90f761b4bfcce0c8c561c0cb5c887b0971d3ec01c32164bdf7374a35b0982711')
    'onnxruntime.dll'                         = @((Join-Path $ort 'runtimes/win-x64/native/onnxruntime.dll'), '0c42dc4e278f0968fdf9ebf1b1e54a74c657a9942e70803e453c91993451cf4c')
    'onnxruntime_providers_shared.dll'        = @((Join-Path $ort 'runtimes/win-x64/native/onnxruntime_providers_shared.dll'), 'e45d471ea2a842b268c51291b96d53568b0dae10807f43d1fb7dc0eb84e83723')
    # VC++ runtime 14.50.35710 (Visual Studio 18). A Visual Studio update changes the hashes: it requires a new module
    # (new version and new hashes here and in OcrModule.cs), not a silent rebuild of the same version.
    'vcruntime140.dll'                        = @((Join-Path $CrtDir 'vcruntime140.dll'), '184146852727a9db4eea06178716bec3cdbb1015c911f6b0f915b184ad7775b2')
    'vcruntime140_1.dll'                      = @((Join-Path $CrtDir 'vcruntime140_1.dll'), 'e6bfb3662ab4b1969a73441dbe35c96d51441b6bff8cf1fe7430bd5b246ca605')
    'msvcp140.dll'                            = @((Join-Path $CrtDir 'msvcp140.dll'), 'def46aa6a8f72f27bafac0c43334419486a4d1dcdb6c479a8ef7034b3e1fa4cb')
    'msvcp140_1.dll'                          = @((Join-Path $CrtDir 'msvcp140_1.dll'), '2dd670f874562fbdca5b022df1943d70a57ba91fde559280e3a1daebe4db2380')
    'LICENSES/NOTICE.txt'                     = @((Join-Path $root 'tools/ocr-module/LICENSES/NOTICE.txt'), '8bacac1f4316a0852f9dd1fef2234c0cdb9753ac430543c42d8c49e3015fe7d0')
    'LICENSES/Apache-2.0.txt'                 = @((Join-Path $root 'tools/ocr-module/LICENSES/Apache-2.0.txt'), 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30')
    'LICENSES/onnxruntime-LICENSE.txt'        = @((Join-Path $ort 'LICENSE'), 'c250d6278f0b47a6439fb7592b08b58a55eb9f535aa49a1db63211c3f982b674')
    'LICENSES/onnxruntime-ThirdPartyNotices.txt' = @((Join-Path $ort 'ThirdPartyNotices.txt'), 'c53a76501ef60db6f865f20599f220761201ac4057683acefdab37d861b86622')
}

function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

$entries = @()
foreach ($name in $files.Keys) {
    $src, $expected = $files[$name]
    if (-not (Test-Path -LiteralPath $src)) { throw "Нет файла для $name`: $src" }
    $hash = Get-Sha256 $src
    if ($expected -and $hash -ne $expected) {
        $hint = if ($name -like '*140*.dll') { ' Рантайм VC++ другой версии: соберите новую версию модуля и обновите хеши в скрипте и в OcrModule.cs.' } else { '' }
        throw "SHA256 $src не совпадает: $hash, ожидался $expected. Сборка прервана.$hint"
    }
    $entries += [pscustomobject]@{ Name = $name; Source = (Resolve-Path -LiteralPath $src).Path; Size = (Get-Item -LiteralPath $src).Length; Sha = $hash }
}
# Names use ordinal ordering, independent of culture.
$sorted = [System.Collections.Generic.List[object]]::new()
$names = [string[]]($entries | ForEach-Object Name)
[Array]::Sort($names, [StringComparer]::Ordinal)
foreach ($n in $names) { $sorted.Add(($entries | Where-Object Name -eq $n)) }

# module.json: fixed field order, LF, UTF-8 without BOM.
$lines = @('{', "  `"id`": `"ocr-paddle`",", "  `"version`": $Version,", '  "files": [')
for ($i = 0; $i -lt $sorted.Count; $i++) {
    $e = $sorted[$i]
    $comma = if ($i -lt $sorted.Count - 1) { ',' } else { '' }
    $lines += "    { `"path`": `"$($e.Name)`", `"size`": $($e.Size), `"sha256`": `"$($e.Sha)`" }$comma"
}
$lines += @('  ]', '}', '')
$manifest = [System.Text.UTF8Encoding]::new($false).GetBytes(($lines -join "`n"))

# The ZIP is written manually rather than with ZipArchive: .NET Framework and .NET 8+ store uncompressed entries differently,
# so Windows PowerShell and pwsh would produce different archives. This format uses stored entries (no compression: models and
# libraries compress poorly), UTF-8 names, a fixed DOS timestamp of 2026-01-01 00:00, and no extra fields.
if (-not ('StoredZip' -as [type])) {
    Add-Type -TypeDefinition @'
using System; using System.IO; using System.Text;
public static class StoredZip {
    static readonly uint[] Table = MakeTable();
    static uint[] MakeTable() {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }
    public static uint Crc32(byte[] data) { uint c = 0xFFFFFFFFu; foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8); return c ^ 0xFFFFFFFFu; }
    public static void Write(string path, string[] names, byte[][] data) {
        const ushort Time = 0, Date = ((2026 - 1980) << 9) | (1 << 5) | 1;
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var w = new BinaryWriter(fs)) {
            var offsets = new long[names.Length]; var crcs = new uint[names.Length];
            for (int i = 0; i < names.Length; i++) {
                var name = Encoding.UTF8.GetBytes(names[i]);
                offsets[i] = fs.Position; crcs[i] = Crc32(data[i]);
                w.Write(0x04034b50u); w.Write((ushort)10); w.Write((ushort)0x0800); w.Write((ushort)0); w.Write(Time); w.Write(Date);
                w.Write(crcs[i]); w.Write((uint)data[i].Length); w.Write((uint)data[i].Length); w.Write((ushort)name.Length); w.Write((ushort)0);
                w.Write(name); w.Write(data[i]);
            }
            long cd = fs.Position;
            for (int i = 0; i < names.Length; i++) {
                var name = Encoding.UTF8.GetBytes(names[i]);
                w.Write(0x02014b50u); w.Write((ushort)20); w.Write((ushort)10); w.Write((ushort)0x0800); w.Write((ushort)0); w.Write(Time); w.Write(Date);
                w.Write(crcs[i]); w.Write((uint)data[i].Length); w.Write((uint)data[i].Length); w.Write((ushort)name.Length);
                w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write((uint)offsets[i]);
                w.Write(name);
            }
            long end = fs.Position;
            w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)names.Length); w.Write((ushort)names.Length);
            w.Write((uint)(end - cd)); w.Write((uint)cd); w.Write((ushort)0);
        }
    }
}
'@
}
New-Item -ItemType Directory -Force $OutputDir | Out-Null
$zipPath = Join-Path $OutputDir "Screeny-OcrModule-$Version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
$zipNames = [string[]](@('module.json') + @($sorted | ForEach-Object Name))
$zipData = [byte[][]](@(, $manifest) + @($sorted | ForEach-Object { , [System.IO.File]::ReadAllBytes($_.Source) }))
[StoredZip]::Write([System.IO.Path]::GetFullPath($zipPath), $zipNames, $zipData)

$zipHash = Get-Sha256 $zipPath
$zipSize = (Get-Item $zipPath).Length
Set-Content -Path "$zipPath.sha256" -Value "$zipHash  Screeny-OcrModule-$Version.zip" -Encoding ascii
Write-Host "CRT: $CrtDir"
$sorted | Select-Object Name, Size, Sha | Format-Table -AutoSize
Write-Host "Модуль $Version`: $zipPath"
Write-Host "  размер $zipSize байт"
Write-Host "  SHA256 $zipHash"
$manifestHash = ([System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($manifest)) -replace '-', '').ToLowerInvariant()
Write-Host "  SHA256 module.json $manifestHash"
Write-Host "В src/Screeny/Modules/OcrModule.cs: Version = $Version, ZipSha256 = `"$zipHash`", ManifestSha256 = `"$manifestHash`", ZipSize = $zipSize"
