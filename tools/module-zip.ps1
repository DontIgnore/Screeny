# Общая часть скриптов сборки скачиваемых модулей (pack-ort-runtime.ps1, pack-ocr-module.ps1, pack-semantic-module.ps1).
# Подключается через dot-source: . (Join-Path $PSScriptRoot 'module-zip.ps1')
#
# Архив детерминирован: тот же набор файлов даёт тот же SHA256 (порядок имён, время записей, без сжатия, module.json
# с постоянным порядком полей). Zip пишется своим кодом, а не ZipArchive: тот в .NET Framework и в .NET 8+ кладёт записи
# без сжатия по-разному, и Windows PowerShell и pwsh давали бы разные архивы. Формат один: записи «stored» (модели и
# библиотеки почти не сжимаются), имена в UTF-8, постоянное DOS-время 2026-01-01 00:00, без дополнительных полей.
# Файлы читаются потоком (модели семантического поиска — по 100–175 МБ).

function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

if (-not ('StoredZipStream' -as [type])) {
    Add-Type -TypeDefinition @'
using System; using System.IO; using System.Text;
public static class StoredZipStream {
    static readonly uint[] Table = MakeTable();
    static uint[] MakeTable() {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }
    static uint Crc32(Stream s) {
        uint c = 0xFFFFFFFFu; var buf = new byte[1 << 20]; int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) for (int i = 0; i < n; i++) c = Table[(c ^ buf[i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
    // sources[i] — путь к файлу или пустая строка, тогда содержимое — inline[i] (module.json).
    public static void Write(string path, string[] names, string[] sources, byte[][] inline) {
        const ushort Time = 0, Date = ((2026 - 1980) << 9) | (1 << 5) | 1;
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var w = new BinaryWriter(fs)) {
            var offsets = new long[names.Length]; var crcs = new uint[names.Length]; var sizes = new long[names.Length];
            for (int i = 0; i < names.Length; i++) {
                var name = Encoding.UTF8.GetBytes(names[i]);
                using (Stream src = !string.IsNullOrEmpty(sources[i]) ? (Stream)File.OpenRead(sources[i]) : new MemoryStream(inline[i], false)) {
                    sizes[i] = src.Length;
                    if (sizes[i] > uint.MaxValue - 1) throw new InvalidOperationException("file too large for zip32: " + names[i]);
                    crcs[i] = Crc32(src);
                    src.Position = 0;
                    offsets[i] = fs.Position;
                    if (offsets[i] > uint.MaxValue - 1) throw new InvalidOperationException("archive too large for zip32");
                    w.Write(0x04034b50u); w.Write((ushort)10); w.Write((ushort)0x0800); w.Write((ushort)0); w.Write(Time); w.Write(Date);
                    w.Write(crcs[i]); w.Write((uint)sizes[i]); w.Write((uint)sizes[i]); w.Write((ushort)name.Length); w.Write((ushort)0);
                    w.Write(name); w.Flush();
                    src.CopyTo(fs);
                }
            }
            long cd = fs.Position;
            for (int i = 0; i < names.Length; i++) {
                var name = Encoding.UTF8.GetBytes(names[i]);
                w.Write(0x02014b50u); w.Write((ushort)20); w.Write((ushort)10); w.Write((ushort)0x0800); w.Write((ushort)0); w.Write(Time); w.Write(Date);
                w.Write(crcs[i]); w.Write((uint)sizes[i]); w.Write((uint)sizes[i]); w.Write((ushort)name.Length);
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

# Собирает <ZipName> в <OutputDir>: сверяет SHA256 каждого файла с зафиксированным (расхождение — остановка), пишет
# module.json (id, version, requires, files) и архив, рядом — .sha256. Печатает строку для класса модуля в приложении.
#   $Files    — [ordered]@{ 'имя/в/архиве' = @('исходный файл', 'ожидаемый sha256' или $null) }
#   $Requires — [ordered]@{ 'id компонента' = версия } или $null
function New-ModuleZip {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][int]$Version,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Files,
        [System.Collections.IDictionary]$Requires,
        [Parameter(Mandatory)][string]$OutputDir,
        [Parameter(Mandatory)][string]$ZipName,
        [string]$ClassFile,
        [string]$HashHint = ''
    )
    $entries = @()
    foreach ($name in $Files.Keys) {
        $src, $expected = $Files[$name]
        if (-not (Test-Path -LiteralPath $src)) { throw "Нет файла для $name`: $src" }
        $hash = Get-Sha256 $src
        if ($expected -and $hash -ne $expected) { throw "SHA256 $src не совпадает: $hash, ожидался $expected. Сборка прервана.$HashHint" }
        $entries += [pscustomobject]@{ Name = $name; Source = (Resolve-Path -LiteralPath $src).Path; Size = (Get-Item -LiteralPath $src).Length; Sha = $hash }
    }
    # Порядок имён — ординальный, не зависит от культуры.
    $names = [string[]]($entries | ForEach-Object Name)
    [Array]::Sort($names, [StringComparer]::Ordinal)
    $sorted = @(foreach ($n in $names) { $entries | Where-Object Name -eq $n })

    # module.json: постоянный порядок полей, LF, UTF-8 без BOM.
    $lines = @('{', "  `"id`": `"$Id`",", "  `"version`": $Version,")
    if ($Requires -and $Requires.Count -gt 0) {
        $req = @($Requires.Keys | ForEach-Object { "`"$_`": $($Requires[$_])" }) -join ', '
        $lines += "  `"requires`": { $req },"
    }
    $lines += '  "files": ['
    for ($i = 0; $i -lt $sorted.Count; $i++) {
        $e = $sorted[$i]
        $comma = if ($i -lt $sorted.Count - 1) { ',' } else { '' }
        $lines += "    { `"path`": `"$($e.Name)`", `"size`": $($e.Size), `"sha256`": `"$($e.Sha)`" }$comma"
    }
    $lines += @('  ]', '}', '')
    $manifest = [System.Text.UTF8Encoding]::new($false).GetBytes(($lines -join "`n"))

    New-Item -ItemType Directory -Force $OutputDir | Out-Null
    $zipPath = [System.IO.Path]::GetFullPath((Join-Path $OutputDir $ZipName))
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    $zipNames = [string[]](@('module.json') + @($sorted | ForEach-Object Name))
    $sources = [string[]](@($null) + @($sorted | ForEach-Object Source))
    $inline = [byte[][]](@(, $manifest) + @($sorted | ForEach-Object { , [byte[]]@() }))
    [StoredZipStream]::Write($zipPath, $zipNames, $sources, $inline)

    $zipHash = Get-Sha256 $zipPath
    $zipSize = (Get-Item $zipPath).Length
    Set-Content -Path "$zipPath.sha256" -Value "$zipHash  $ZipName" -Encoding ascii
    $manifestHash = ([System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($manifest)) -replace '-', '').ToLowerInvariant()
    $sorted | Select-Object Name, Size, Sha | Format-Table -AutoSize | Out-Host
    Write-Host "Модуль $Id $Version`: $zipPath"
    Write-Host "  размер $zipSize байт"
    Write-Host "  SHA256 $zipHash"
    Write-Host "  SHA256 module.json $manifestHash"
    if ($ClassFile) { Write-Host "В $ClassFile`: Version = $Version, ZipSha256 = `"$zipHash`", ManifestSha256 = `"$manifestHash`", ZipSize = $zipSize" }
}
