# Публикация Screeny в publish/Screeny.exe (один файл).
#   powershell -File tools/publish.ps1                 — нужен установленный .NET 10 Desktop Runtime (~1 МБ exe)
#   powershell -File tools/publish.ps1 -SelfContained — автономный exe со встроенным рантаймом (~70 МБ)
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
