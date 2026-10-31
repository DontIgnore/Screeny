using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using Screeny.Localization;
using Screeny.Logging;

namespace Screeny.Modules;

/// <summary>Where module archive comes from.</summary>
public interface IModuleSource
{
    /// <summary>Archive stream and its length if known.</summary>
    Task<(Stream Body, long? Length)> OpenAsync(CancellationToken ct);
}

/// <summary>Archive over HTTP (GitHub release).</summary>
public sealed class HttpModuleSource(HttpClient http, Uri url) : IModuleSource
{
    public Uri Url => url;

    public async Task<(Stream Body, long? Length)> OpenAsync(CancellationToken ct)
    {
        var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            response.Dispose();
            throw new ModuleInstallException(code == 404 ? Loc.T("Module.NotFound404") : Loc.F("Module.ServerStatus", code));
        }
        return (await response.Content.ReadAsStreamAsync(ct), response.Content.Headers.ContentLength);
    }
}

/// <summary>Archive from local folder (<c>--dev --module-source &lt;folder&gt;</c>): no network.</summary>
public sealed class FolderModuleSource(string folder, string fileName) : IModuleSource
{
    public Task<(Stream Body, long? Length)> OpenAsync(CancellationToken ct)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path)) throw new ModuleInstallException(Loc.F("Module.FileMissing", folder, fileName));
        var s = File.OpenRead(path);
        return Task.FromResult<(Stream, long?)>((s, s.Length));
    }
}

public enum ModuleSourceKind { None, Github, Folder }

/// <summary>Installation error understandable to user (shown in settings and notification).</summary>
public sealed class ModuleInstallException(string message) : Exception(message);

/// <summary>What is installed: id, version and expected SHA256 of archive and its <c>module.json</c> (hardcoded, see
/// <see cref="OcrModule"/>; null manifest — don't check, only for tests).</summary>
public sealed record ModuleSpec(string Id, int Version, string ZipSha256, string? ManifestSha256 = null,
    long MaxZipBytes = 200_000_000, long MaxUnpackedBytes = 400_000_000)
{
    public string FolderName => $"{Id}-{Version}";

    /// <summary>How long to wait for server response and each next data chunk. Server silent longer — “no connection”,
    /// not eternal wait (response with <c>ResponseHeadersRead</c> has no read timeout).</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public enum RemoveResult { Removed, NotInstalled, PendingRestart }

/// <summary>
/// Module install into <c>&lt;root&gt;\&lt;id&gt;-&lt;version&gt;\</c>: download to temp file (with progress and cancel) → verify
/// archive SHA256 against hardcoded → unpack to temp folder, rejecting paths outside it (zip-slip) and absolute →
/// verify files against <c>module.json</c> → rename temp folder to final. Temp files in same root
/// (same volume, atomic rename) and removed on any error/cancel. Loading anything from module allowed
/// only after successful install.
/// </summary>
public sealed class ModuleInstaller(string root, ModuleSpec spec)
{
    /// <summary>Marker in module folder: delete at next launch (files held by loaded library).</summary>
    public const string RemovedMarker = ".removed";
    private const string DownloadPrefix = ".download-", StagingPrefix = ".staging-";

    public string Root => root;
    public ModuleSpec Spec => spec;
    public string TargetFolder => Path.Combine(root, spec.FolderName);

    /// <summary>Source by startup keys: <c>--module-source</c> (only dev) — folder; normal launch — GitHub;
    /// test instance without folder — nowhere (like updates: dev runs don't go to network).</summary>
    public static ModuleSourceKind ChooseSource(bool isDev, string? localSource)
        => localSource is not null ? ModuleSourceKind.Folder : isDev ? ModuleSourceKind.None : ModuleSourceKind.Github;

    /// <summary>Installed module version: <paramref name="preferred"/>, if present, else newest; null — none.
    /// Files not hashed here (download does), only <c>module.json</c> checked.</summary>
    public static int? DetectInstalled(string root, string id, int preferred)
    {
        var versions = InstalledVersions(root, id);
        return versions.Contains(preferred) ? preferred : versions.Count > 0 ? versions.Max() : null;
    }

    public static List<int> InstalledVersions(string root, string id)
    {
        var result = new List<int>();
        if (!Directory.Exists(root)) return result;
        foreach (var dir in Directory.EnumerateDirectories(root, id + "-*"))
        {
            if (!int.TryParse(Path.GetFileName(dir)[(id.Length + 1)..], out int v) || v <= 0) continue;
            if (File.Exists(Path.Combine(dir, RemovedMarker))) continue;
            if (ModuleManifest.TryRead(dir) is { } m && m.Id == id && m.Version == v) result.Add(v);
        }
        return result;
    }

    /// <summary>
    /// Full path for writing archive <paramref name="name"/> inside <paramref name="folder"/> or null if name absolute,
    /// has drive, has “..” or otherwise escapes folder.
    /// </summary>
    public static string? SafeTarget(string folder, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0')) return null;
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') || Path.IsPathRooted(name)) return null;
        if (normalized.Split('/').Any(part => part is ".." or ".")) return null;
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full.Length > root.Length ? full : null;
    }

    public static string NoConnection => Loc.T("Module.NoConnection");

    /// <summary>
    /// Download and install. <paramref name="progress"/> — download percent (0–100). Returns module folder.
    /// Temp archive open without sharing from first write to end of unpack: between hash check and unpack
    /// nobody can swap it.
    /// </summary>
    public async Task<string> InstallAsync(IModuleSource source, Action<int>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var token = Guid.NewGuid().ToString("N");
        var zip = Path.Combine(root, DownloadPrefix + token + ".zip");
        var staging = Path.Combine(root, StagingPrefix + token);
        try
        {
            await using (var file = new FileStream(zip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            {
                var hash = await DownloadAsync(source, file, progress, ct);
                if (!string.Equals(hash, spec.ZipSha256, StringComparison.OrdinalIgnoreCase))
                    throw new ModuleInstallException(Loc.T("Module.HashMismatch"));
                ct.ThrowIfCancellationRequested();
                file.Position = 0;
                Extract(file, staging);
            }
            if (ModuleManifest.Verify(staging, spec.Id, spec.Version, spec.ManifestSha256) is { } error)
                throw new ModuleInstallException(Loc.F("Module.Corrupt", error));
            ct.ThrowIfCancellationRequested();
            var target = TargetFolder;
            if (Directory.Exists(target))
            {
                try { Directory.Delete(target, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Error($"Previous module folder is in use: {target}", ex);
                    Mark(target); // some files may already be deleted: such folder not counted as installed
                    throw new ModuleInstallException(Loc.T("Module.OldFilesBusy"));
                }
            }
            MoveWithRetry(staging, target);
            RemoveOtherVersions();
            return target;
        }
        finally
        {
            TryDelete(zip);
            TryDeleteDir(staging);
        }
    }

    private async Task<string> DownloadAsync(IModuleSource source, Stream output, Action<int>? progress, CancellationToken ct)
    {
        // Idle timeout: server response and each data chunk must arrive within ReadTimeout, countdown restarts after chunk.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(spec.ReadTimeout);
        try
        {
            var (body, length) = await source.OpenAsync(idle.Token);
            await using var input = body;
            if (length > spec.MaxZipBytes) throw new ModuleInstallException(Loc.T("Module.TooLarge"));
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long done = 0; int lastPercent = -1;
            progress?.Invoke(0);
            int n;
            idle.CancelAfter(spec.ReadTimeout);
            while ((n = await input.ReadAsync(buffer, idle.Token)) > 0)
            {
                idle.CancelAfter(spec.ReadTimeout);
                done += n;
                if (done > spec.MaxZipBytes) throw new ModuleInstallException(Loc.T("Module.TooLarge"));
                sha.AppendData(buffer, 0, n);
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                if (length is > 0 && (int)(done * 100 / length.Value) is var p && p != lastPercent) { lastPercent = Math.Min(100, p); progress?.Invoke(lastPercent); }
            }
            await output.FlushAsync(ct);
            progress?.Invoke(100);
            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Log.Error($"Module download stalled for {spec.ReadTimeout.TotalSeconds:0} s", ex);
            throw new ModuleInstallException(NoConnection);
        }
    }

    /// <summary>
    /// Renames unpacked folder to final. Antivirus checks just-written DLLs and holds them open —
    /// rename fails during that, so several attempts with pause; failed — clear message.
    /// </summary>
    public static void MoveWithRetry(string from, string to, int attempts = 5, int delayMs = 200)
    {
        for (int i = 1; ; i++)
        {
            try { Directory.Move(from, to); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i >= attempts)
                {
                    Log.Error($"Module folder move failed after {attempts} attempts: {from} -> {to}", ex);
                    throw new ModuleInstallException(Loc.T("Module.FilesLocked"));
                }
                Thread.Sleep(delayMs * i);
            }
        }
    }

    /// <summary>Unpack with zip-slip protection: each path checked by <see cref="SafeTarget"/>, total size limited.</summary>
    public void Extract(Stream zip, string staging)
    {
        Directory.CreateDirectory(staging);
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (SafeTarget(staging, entry.FullName) is not { } dest) throw new ModuleInstallException(Loc.F("Module.BadPath", entry.FullName));
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(dest); continue; }
            total += entry.Length;
            if (total > spec.MaxUnpackedBytes) throw new ModuleInstallException(Loc.T("Module.UnpackTooLarge"));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: false);
        }
    }

    /// <summary>
    /// Delete module. Folder where native library is loaded into this process (<paramref name="inUse"/>) is not
    /// touched but marked <see cref="RemovedMarker"/>: module considered deleted immediately, files removed by
    /// <see cref="CleanupOnStartup"/>. Same if deletion fails for other reason.
    /// </summary>
    public RemoveResult Remove(Func<string, bool>? inUse = null)
    {
        var versions = InstalledVersions(root, spec.Id);
        if (versions.Count == 0) return RemoveResult.NotInstalled;
        var result = RemoveResult.Removed;
        foreach (var v in versions)
        {
            var dir = Path.Combine(root, $"{spec.Id}-{v}");
            if (inUse?.Invoke(dir) == true) { Mark(dir); result = RemoveResult.PendingRestart; }
            else if (!DeleteOrMark(dir)) result = RemoveResult.PendingRestart;
        }
        return result;
    }

    /// <summary>Cancel pending deletion: remove marker if files of needed version intact. false — no intact files.</summary>
    public bool Restore()
    {
        var marker = Path.Combine(TargetFolder, RemovedMarker);
        if (!File.Exists(marker)) return false;
        TryDelete(marker);
        if (!File.Exists(marker) && ModuleManifest.Verify(TargetFolder, spec.Id, spec.Version, spec.ManifestSha256) is null) return true;
        Mark(TargetFolder);
        return false;
    }

    /// <summary>Pending deletion exists: folder of needed version marked but not yet erased.</summary>
    public bool RemovalPending => File.Exists(Path.Combine(TargetFolder, RemovedMarker));

    /// <summary>At startup: erase marked folders and remnants of interrupted installs.</summary>
    public void CleanupOnStartup()
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(StagingPrefix, StringComparison.Ordinal) || File.Exists(Path.Combine(dir, RemovedMarker))) TryDeleteDir(dir);
            }
            foreach (var file in Directory.EnumerateFiles(root, DownloadPrefix + "*")) TryDelete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"Modules cleanup failed: {root}", ex); }
    }

    /// <summary>
    /// Uninstall Screeny (Velopack hook): erase entire modules folder. If not fully (module held by running portable
    /// copy — shared folder) — erase what's possible, mark remaining module folders <see cref="RemovedMarker"/>:
    /// next launch of any copy removes them and won't count as installed.
    /// </summary>
    public static void DeleteRootOrMark(string root)
    {
        if (!Directory.Exists(root)) return;
        try { Directory.Delete(root, recursive: true); return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"Modules folder not fully deleted: {root}", ex); }
        foreach (var dir in Directory.EnumerateDirectories(root))
            if (!Path.GetFileName(dir).StartsWith('.')) DeleteOrMark(dir);
            else TryDeleteDir(dir);
    }

    /// <summary>After installing new version previous ones not needed.</summary>
    private void RemoveOtherVersions()
    {
        foreach (var v in InstalledVersions(root, spec.Id).Where(v => v != spec.Version))
            DeleteOrMark(Path.Combine(root, $"{spec.Id}-{v}"));
    }

    private static bool DeleteOrMark(string dir)
    {
        try { Directory.Delete(dir, recursive: true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Module folder not deleted, marked for removal on next start: {dir}", ex);
            Mark(dir);
            return false;
        }
    }

    private static bool Mark(string dir)
    {
        try { File.WriteAllText(Path.Combine(dir, RemovedMarker), ""); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"Module removal marker failed: {dir}", ex); return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"Module temp file not deleted: {path}", ex); }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"Module temp folder not deleted: {path}", ex); }
    }
}
