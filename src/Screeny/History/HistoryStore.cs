using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Screeny.Logging;

namespace Screeny.History;

/// <summary>Sidecar files of screenshot for re-editing.</summary>
/// <param name="Json">Annotation document (<see cref="Annotations.AnnotationSerializer"/>); null — not write.</param>
/// <param name="OriginalPng">Region without marks and decoration; null — not write (setting disabled).</param>
public sealed record HistorySidecar(string? Json, byte[]? OriginalPng);

/// <summary>
/// History screenshots folder. Only files named <c>yyyyMMdd_HHmmss_fff[_N].png</c> are considered own:
/// user may choose folder with other images; rotation/cleanup must not touch them.
/// Screenshot may have sidecar files <c>&lt;name&gt;.screeny.json</c> and <c>&lt;name&gt;.orig.png</c> (re-editing):
/// List() doesn't show them, Delete/Prune/Clear delete them with screenshot.
/// </summary>
public sealed class HistoryStore
{
    private const string StampFormat = "yyyyMMdd_HHmmss_fff";
    private const string TempSuffix = ".tmp";
    private static readonly Regex NamePattern =
        new(@"^(?<stamp>\d{8}_\d{6}_\d{3})(?:_(?<n>[1-9]\d{0,8}))?\.png$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Sidecar suffixes: annotation document and original without marks.</summary>
    public const string JsonSuffix = ".screeny.json", OriginalSuffix = ".orig.png";

    /// <summary>Strict sidecar name: history screenshot stamp + suffix. Foreign foo.screeny.json don't get here.</summary>
    private static readonly Regex SidecarPattern =
        new(@"^(?<base>\d{8}_\d{6}_\d{3}(?:_[1-9]\d{0,8})?)\.(?:screeny\.json|orig\.png)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Folder { get; }
    public int Limit { get; }

    public HistoryStore(string folder, int limit) { Folder = folder; Limit = Math.Max(1, limit); }

    public string Save(BitmapSource image, DateTime? timestamp = null)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return Save(ms.ToArray(), timestamp);
    }

    /// <summary>Saves already encoded PNG (to avoid double encoding for clipboard and history).</summary>
    public string Save(byte[] png, DateTime? timestamp = null) => Save(png, sidecar: null, timestamp);

    /// <summary>
    /// Saves screenshot with sidecar files atomically: everything written to temp files with “foreign” names
    /// (List() doesn't see), then renamed — sidecars first, screenshot last, so entry doesn't appear without its data.
    /// On failure temp and already renamed files removed (rollback).
    /// If process crashed between renames, orphaned sidecars remain — Prune and Clear remove them.
    /// </summary>
    public string Save(byte[] png, HistorySidecar? sidecar, DateTime? timestamp = null)
    {
        Directory.CreateDirectory(Folder);
        var stamp = (timestamp ?? DateTime.Now).ToString(StampFormat, CultureInfo.InvariantCulture);
        var path = NextFreePath(stamp);
        var (jsonPath, originalPath) = SidecarPaths(path);

        var files = new List<(string Final, byte[] Data)>();
        if (sidecar?.Json is { } json) files.Add((jsonPath, Encoding.UTF8.GetBytes(json)));
        if (sidecar?.OriginalPng is { } original) files.Add((originalPath, original));
        files.Add((path, png)); // screenshot last

        var moved = new List<string>();
        var temporaryFiles = new List<string>();
        try
        {
            foreach (var (final, data) in files)
            {
                var temp = final + TempSuffix;
                using var fs = new FileStream(temp, FileMode.CreateNew);
                temporaryFiles.Add(temp);
                fs.Write(data, 0, data.Length);
            }
            foreach (var (final, _) in files)
            {
                File.Move(final + TempSuffix, final, overwrite: false);
                moved.Add(final);
            }
        }
        catch
        {
            // Cleanup must not hide original error.
            foreach (var temp in temporaryFiles) TryDelete(temp);
            foreach (var final in moved) TryDelete(final);
            throw;
        }
        Prune(keep: path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* cleanup — best effort */ }
    }

    /// <summary>Sidecar paths of screenshot (same folder): annotation document and original without marks.</summary>
    public static (string Json, string Original) SidecarPaths(string pngPath)
    {
        var stem = Path.Combine(Path.GetDirectoryName(pngPath) ?? "", Path.GetFileNameWithoutExtension(pngPath));
        return (stem + JsonSuffix, stem + OriginalSuffix);
    }

    /// <summary>
    /// Name for new screenshot with this stamp. Suffix is maximum existing + 1, not first free:
    /// after rotation lower numbers free up, and reused “_1” would sort older than surviving “_10”.
    /// </summary>
    private string NextFreePath(string stamp)
    {
        int max = -1;
        foreach (var p in Directory.EnumerateFiles(Folder, stamp + "*.png"))
            if (TryParseName(Path.GetFileName(p), out _, out var n) && Path.GetFileName(p).StartsWith(stamp, StringComparison.Ordinal))
                max = Math.Max(max, n);
        for (int i = max + 1; ; i++)
        {
            var path = Path.Combine(Folder, i == 0 ? stamp + ".png" : $"{stamp}_{i}.png");
            var (json, original) = SidecarPaths(path);
            // Orphaned sidecar with this name must not attach to new screenshot.
            if (!File.Exists(path) && !File.Exists(json) && !File.Exists(original)) return path;
        }
    }

    /// <summary>
    /// Screenshots newest to oldest: by time from name, then collision suffix numerically.
    /// Foreign files don't enter list. Folder read error → empty list.
    /// </summary>
    public IReadOnlyList<HistoryItem> List()
    {
        try { return ListOrThrow(); }
        catch (Exception ex) when (IsListError(ex))
        {
            Log.Error($"History list failed: {Folder}", ex);
            return Array.Empty<HistoryItem>();
        }
    }

    /// <summary>Like <see cref="List"/>, but propagates folder read error (history window needs to show it). No folder — empty.</summary>
    public IReadOnlyList<HistoryItem> ListOrThrow()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshots = new List<(string Path, DateTime Time, int Suffix)>();
        try
        {
            foreach (var p in Directory.EnumerateFiles(Folder))
            {
                var name = Path.GetFileName(p);
                names.Add(name);
                if (TryParseName(name, out var t, out var n)) snapshots.Add((p, t, n));
            }
        }
        catch (DirectoryNotFoundException) { return Array.Empty<HistoryItem>(); }
        return snapshots
            .OrderByDescending(s => s.Time)
            .ThenByDescending(s => s.Suffix)
            .Select(s =>
            {
                var (json, original) = SidecarPaths(s.Path);
                return new HistoryItem(s.Path, s.Time)
                {
                    JsonPath = names.Contains(Path.GetFileName(json)) ? json : null,
                    OriginalPath = names.Contains(Path.GetFileName(original)) ? original : null,
                };
            })
            .ToList();
    }

    public static bool IsListError(Exception ex)
        => ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;

    /// <summary>
    /// Deletes screenshot and — if it's a history screenshot (strict name) — its sidecar files. Screenshot delete error
    /// propagates; failed sidecar remains orphan and is removed on next rotation or cleanup.
    /// </summary>
    public void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (!TryParseName(Path.GetFileName(path), out _, out _)) return;
        var (json, original) = SidecarPaths(path);
        foreach (var sidecar in new[] { json, original })
        {
            try { if (File.Exists(sidecar)) File.Delete(sidecar); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"History sidecar delete failed: {sidecar}", ex); }
        }
    }

    /// <summary>Deletes all history screenshots with their sidecars (and orphans); foreign files in folder (incl. PNG) untouched.</summary>
    public void Clear()
    {
        foreach (var item in List()) Delete(item.Path);
        DeleteOrphanSidecars();
    }

    /// <summary>
    /// Saved originals without marks (<c>&lt;stamp&gt;.orig.png</c>) in history folder: they contain what was
    /// hidden by blur. Foreign files (<c>bar.orig.png</c>) not counted. No folder or read error — empty.
    /// </summary>
    public IReadOnlyList<string> ListOriginals()
    {
        try { return Directory.EnumerateFiles(Folder).Where(p => IsOriginalName(Path.GetFileName(p))).ToList(); }
        catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
        catch (Exception ex) when (IsListError(ex))
        {
            Log.Error($"History originals list failed: {Folder}", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Deletes saved originals (only <see cref="ListOriginals"/>); screenshots and marks documents remain,
    /// and entries open in editor as ready images. Returns number of files deleted.
    /// </summary>
    public int DeleteOriginals() => DeleteOriginals(ListOriginals());

    internal int DeleteOriginals(IEnumerable<string> originals)
    {
        int deleted = 0;
        foreach (var p in originals)
        {
            try { File.Delete(p); deleted++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"History original delete failed: {p}", ex); }
        }
        return deleted;
    }

    /// <summary>Time from name like yyyyMMdd_HHmmss_fff[_N].png.</summary>
    public static bool TryParseTimestamp(string fileName, out DateTime timestamp) => TryParseName(fileName, out timestamp, out _);

    /// <summary>Time and collision number (0 — no suffix) from screenshot name; false for foreign file.</summary>
    public static bool TryParseName(string fileName, out DateTime timestamp, out int suffix)
    {
        timestamp = default; suffix = 0;
        var m = NamePattern.Match(fileName);
        if (!m.Success) return false;
        if (!DateTime.TryParseExact(m.Groups["stamp"].Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp))
            return false;
        if (m.Groups["n"].Success) suffix = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        return true;
    }

    private static bool IsOriginalName(string fileName)
        => fileName.EndsWith(OriginalSuffix, StringComparison.OrdinalIgnoreCase)
           && TryParseName(fileName[..^OriginalSuffix.Length] + ".png", out _, out _);

    /// <summary>
    /// Keeps <see cref="Limit"/> screenshots. Just saved is not deleted even if its stamp is older
    /// than others (clock set back): otherwise Save would return path to nonexistent file.
    /// </summary>
    private void Prune(string keep)
    {
        var others = List().Where(i => !string.Equals(i.Path, keep, StringComparison.OrdinalIgnoreCase));
        foreach (var item in others.Skip(Limit - 1))
        {
            try { Delete(item.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"History prune failed: {item.Path}", ex); }
        }
        DeleteOrphanSidecars();
    }

    /// <summary>
    /// Sidecar files with strict stamp name without main screenshot (crash between renames, screenshot deleted
    /// manually). Foreign <c>foo.screeny.json</c> and <c>bar.orig.png</c> untouched.
    /// </summary>
    private void DeleteOrphanSidecars()
    {
        List<string> files;
        try { files = Directory.EnumerateFiles(Folder).ToList(); }
        catch (Exception ex) when (IsListError(ex)) { return; }
        foreach (var p in files)
        {
            var m = SidecarPattern.Match(Path.GetFileName(p));
            if (!m.Success) continue;
            var snapshotName = m.Groups["base"].Value + ".png";
            if (!TryParseName(snapshotName, out _, out _) || File.Exists(Path.Combine(Folder, snapshotName))) continue;
            try { File.Delete(p); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"History orphan delete failed: {p}", ex); }
        }
    }
}
