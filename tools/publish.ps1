# Publish Screeny to publish/Screeny.exe (single file).
#   powershell -File tools/publish.ps1                 — requires .NET 10 Desktop Runtime installed (~1 MB exe)
#   powershell -File tools/publish.ps1 -SelfContained — standalone exe with bundled runtime (~70 MB)
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$sc = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish (Join-Path $root 'src/Screeny/Screeny.csproj') -c Release -r win-x64 --self-contained $sc `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
Get-ChildItem $out | Select-Object Name, @{ n = 'MB'; e = { [Math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
