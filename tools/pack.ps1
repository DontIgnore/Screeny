# Сборка релиза Screeny: dotnet publish → vpk pack (Velopack).
#   powershell -File tools/pack.ps1 -Version 0.2.0
#   powershell -File tools/pack.ps1                       — версия из тега текущего коммита (v0.2.0 → 0.2.0)
#   powershell -File tools/pack.ps1 -Version 0.2.1 -OutputDir C:\tmp\rel-0.2.1
# Результат в releases/ (или -OutputDir):
#   Screeny-win-Setup.exe     — установщик (per-user, %LocalAppData%\DontIgnore.Screeny, ставит .NET 10 Desktop Runtime при необходимости)
#   Screeny-win-Portable.zip  — портативная версия (тоже обновляется; .NET 10 Desktop Runtime нужен заранее)
#   DontIgnore.Screeny-<версия>-full.nupkg, *-delta.nupkg, releases.win.json — файлы обновлений
# Все файлы из папки прикладываются к релизу GitHub (см. docs/maintainers.md, «Making a release»).
# Для дельта-обновлений в -OutputDir должны лежать пакеты предыдущей версии (vpk сам найдёт последний *-full.nupkg).
param(
    [string]$Version,
    [string]$OutputDir,
    [string]$NotesFile
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packId = 'DontIgnore.Screeny'   # = папка установки %LocalAppData%\DontIgnore.Screeny (не %LocalAppData%\Screeny, где история)

if (-not $Version) {
    $tag = git -C $root describe --tags --exact-match 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $tag) { throw 'Укажите -Version 1.2.3 или поставьте тег v1.2.3 на текущий коммит.' }
    $Version = $tag.Trim().TrimStart('v')
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Версия '$Version' не в формате SemVer (1.2.3)." }
if (-not $OutputDir) { $OutputDir = Join-Path $root 'releases' }
if (-not $NotesFile) { $NotesFile = Join-Path $root "docs/releases/$Version.md" }
if (-not (Test-Path $NotesFile)) { throw "Нет списка изменений: $NotesFile" }

$publish = Join-Path $root "artifacts/publish/$Version"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host "== dotnet publish $Version" -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src/Screeny/Screeny.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Write-Host "== vpk pack $packId $Version" -ForegroundColor Cyan
Push-Location $root
try {
    dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed ($LASTEXITCODE)" }
    dotnet vpk pack `
        --packId $packId `
        --packTitle Screeny `
        --packVersion $Version `
        --packDir $publish `
        --mainExe Screeny.exe `
        --runtime win-x64 `
        --icon (Join-Path $root 'src/Screeny/Assets/screeny.ico') `
        --releaseNotes $NotesFile `
        --framework net10.0-x64-desktop `
        --shortcuts StartMenuRoot `
        --outputDir $OutputDir
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }
}
finally { Pop-Location }

# Установщик и zip не входят в ленту обновлений (releases.win.json) — даём им короткие имена для страницы релиза.
foreach ($pair in @(@('Setup.exe', 'Screeny-win-Setup.exe'), @('Portable.zip', 'Screeny-win-Portable.zip'))) {
    $from = Join-Path $OutputDir "$packId-win-$($pair[0])"
    $to = Join-Path $OutputDir $pair[1]
    if (Test-Path $from) { Move-Item $from $to -Force }
    if (-not (Test-Path $to)) { throw "vpk не создал $($pair[1])" }
}

Get-ChildItem $OutputDir -File | Sort-Object Name |
    Select-Object Name, @{ n = 'MB'; e = { [Math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
