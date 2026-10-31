using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Screeny.Localization;

namespace Screeny.Modules;

/// <summary>Module file from <c>module.json</c>: path relative to module folder (via “/”), size and SHA256 (hex).</summary>
public sealed record ModuleFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// <c>module.json</c> in module root: id, version and list of all other files with SHA256. Written by
/// <c>tools/pack-ocr-module.ps1</c>; app checks unpacked files against it before install and once more — one
/// time per process — before first native library load.
/// </summary>
public sealed record ModuleManifest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("files")] IReadOnlyList<ModuleFile> Files)
{
    public const string FileName = "module.json";

    /// <summary>Parses <c>module.json</c>; null — unreadable or wrong content.</summary>
    public static ModuleManifest? TryRead(string folder)
    {
        try
        {
            var json = File.ReadAllText(System.IO.Path.Combine(folder, FileName));
            var m = JsonSerializer.Deserialize<ModuleManifest>(json);
            return m is { Id: not null, Files: not null } && m.Files.All(f => f is { Path: not null, Sha256: not null }) ? m : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// Folder matches manifest: <c>module.json</c> itself is the one hardcoded (<paramref name="manifestSha256"/>;
    /// else swapped manifest with files would pass), id and version expected, each file present with correct
    /// size and SHA256, and no extra files. Returns error text or null.
    /// </summary>
    public static string? Verify(string folder, string expectedId, int expectedVersion, string? manifestSha256 = null)
    {
        if (manifestSha256 is not null)
        {
            var path = System.IO.Path.Combine(folder, FileName);
            try
            {
                if (!File.Exists(path)) return Loc.F("Manifest.Missing", FileName);
                if (!string.Equals(Sha256Of(path), manifestSha256, StringComparison.OrdinalIgnoreCase)) return Loc.F("Manifest.Changed", FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Loc.F("Manifest.Unreadable", FileName); }
        }
        if (TryRead(folder) is not { } m) return Loc.F("Manifest.Missing", FileName);
        if (m.Id != expectedId || m.Version != expectedVersion) return Loc.F("Manifest.WrongModule", FileName, m.Id, m.Version, expectedId, expectedVersion);
        var root = System.IO.Path.GetFullPath(folder);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in m.Files)
        {
            if (ModuleInstaller.SafeTarget(root, f.Path) is not { } full) return Loc.F("Manifest.BadPath", FileName, f.Path);
            if (!listed.Add(full)) return Loc.F("Manifest.Duplicate", f.Path);
            var info = new FileInfo(full);
            if (!info.Exists) return Loc.F("Manifest.NoFile", f.Path);
            if (info.Length != f.Size) return Loc.F("Manifest.SizeMismatch", f.Path);
            if (!string.Equals(Sha256Of(full), f.Sha256, StringComparison.OrdinalIgnoreCase)) return Loc.F("Manifest.HashMismatch", f.Path);
        }
        listed.Add(System.IO.Path.Combine(root, FileName));
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            if (!listed.Contains(System.IO.Path.GetFullPath(file))) return Loc.F("Manifest.ExtraFile", System.IO.Path.GetRelativePath(root, file));
        return null;
    }

    public static string Sha256Of(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }
}
