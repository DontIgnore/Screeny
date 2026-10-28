using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using Screeny.Localization;
using Screeny.Logging;

namespace Screeny.Modules;

/// <summary>Откуда берётся архив модуля.</summary>
public interface IModuleSource
{
    /// <summary>Поток архива и его длина, если известна.</summary>
    Task<(Stream Body, long? Length)> OpenAsync(CancellationToken ct);
}

/// <summary>Архив по HTTP (релиз GitHub).</summary>
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

/// <summary>Архив из локальной папки (<c>--dev --module-source &lt;папка&gt;</c>): в сеть не ходит.</summary>
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

/// <summary>Ошибка установки, понятная пользователю (показывается в настройках и в уведомлении).</summary>
public sealed class ModuleInstallException(string message) : Exception(message);

/// <summary>Что ставится: id, версия и ожидаемые SHA256 архива и его <c>module.json</c> (зашиты в приложении, см.
/// <see cref="OcrModule"/>; null у манифеста — не проверять, только для тестов).</summary>
public sealed record ModuleSpec(string Id, int Version, string ZipSha256, string? ManifestSha256 = null,
    long MaxZipBytes = 200_000_000, long MaxUnpackedBytes = 400_000_000)
{
    public string FolderName => $"{Id}-{Version}";

    /// <summary>Сколько ждать ответа сервера и каждого следующего куска данных. Сервер замолчал дольше — «нет связи»,
    /// а не вечное ожидание (у ответа с <c>ResponseHeadersRead</c> своего таймаута на чтение нет).</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public enum RemoveResult { Removed, NotInstalled, PendingRestart }

/// <summary>
/// Установка модуля в <c>&lt;root&gt;\&lt;id&gt;-&lt;версия&gt;\</c>: скачать во временный файл (с прогрессом и отменой) → сверить
/// SHA256 архива с зашитым → распаковать во временную папку, отвергая пути за её пределами (zip-slip) и абсолютные →
/// сверить файлы с <c>module.json</c> → переименовать временную папку в итоговую. Временные файлы — в том же корне
/// (тот же том, переименование атомарное) и удаляются при любой ошибке и отмене. Загружать что-либо из модуля можно
/// только после успешной установки.
/// </summary>
public sealed class ModuleInstaller(string root, ModuleSpec spec)
{
    /// <summary>Метка в папке модуля: удалить при следующем запуске (файлы держит загруженная библиотека).</summary>
    public const string RemovedMarker = ".removed";
    private const string DownloadPrefix = ".download-", StagingPrefix = ".staging-";

    public string Root => root;
    public ModuleSpec Spec => spec;
    public string TargetFolder => Path.Combine(root, spec.FolderName);

    /// <summary>Источник по ключам запуска: <c>--module-source</c> (только dev) — папка; обычный запуск — GitHub;
    /// тестовый экземпляр без папки — ниоткуда (как у обновлений: dev-запуски не ходят в сеть).</summary>
    public static ModuleSourceKind ChooseSource(bool isDev, string? localSource)
        => localSource is not null ? ModuleSourceKind.Folder : isDev ? ModuleSourceKind.None : ModuleSourceKind.Github;

    /// <summary>Установленная версия модуля: <paramref name="preferred"/>, если она есть, иначе самая новая; null — нет ни одной.
    /// Файлы здесь не хешируются (это делает загрузка), проверяется только <c>module.json</c>.</summary>
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
    /// Полный путь для записи архива <paramref name="name"/> внутри <paramref name="folder"/> или null, если имя абсолютное,
    /// с диском, с «..» или иначе выходит за пределы папки.
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
    /// Скачать и поставить. <paramref name="progress"/> — проценты загрузки (0–100). Возвращает папку модуля.
    /// Временный архив открыт без общего доступа от первой записи до конца распаковки: между проверкой хеша и распаковкой
    /// его никто не подменит.
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
                    Mark(target); // часть файлов могла уже удалиться: такую папку не считаем установленной
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
        // Таймаут бездействия: ответ сервера и каждый кусок данных должны прийти за ReadTimeout, отсчёт — заново после куска.
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
    /// Переименование распакованной папки в итоговую. Антивирус проверяет только что записанные DLL и держит их открытыми —
    /// переименование на это время не проходит, поэтому несколько попыток с паузой; не вышло — понятное сообщение.
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

    /// <summary>Распаковка с защитой от zip-slip: каждый путь проверяется <see cref="SafeTarget"/>, общий размер ограничен.</summary>
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
    /// Удалить модуль. Папка, из которой нативная библиотека уже загружена в этот процесс (<paramref name="inUse"/>), не
    /// трогается, а помечается <see cref="RemovedMarker"/>: модуль считается удалённым сразу, а файлы стирает
    /// <see cref="CleanupOnStartup"/>. Так же — если удалить не вышло по другой причине.
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

    /// <summary>Отменить отложенное удаление: снять метку, если файлы нужной версии целы. false — целых файлов нет.</summary>
    public bool Restore()
    {
        var marker = Path.Combine(TargetFolder, RemovedMarker);
        if (!File.Exists(marker)) return false;
        TryDelete(marker);
        if (!File.Exists(marker) && ModuleManifest.Verify(TargetFolder, spec.Id, spec.Version, spec.ManifestSha256) is null) return true;
        Mark(TargetFolder);
        return false;
    }

    /// <summary>Отложенное удаление есть: папка нужной версии помечена, но ещё не стёрта.</summary>
    public bool RemovalPending => File.Exists(Path.Combine(TargetFolder, RemovedMarker));

    /// <summary>При запуске: стереть помеченные папки и остатки прерванных установок.</summary>
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
    /// Удаление Screeny (хук Velopack): стереть всю папку модулей. Не вышло целиком (модуль держит работающая портативная
    /// копия — папка у них общая) — стереть, что можно, а оставшиеся папки модулей пометить <see cref="RemovedMarker"/>:
    /// следующий запуск любой копии их уберёт и считать установленными не будет.
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

    /// <summary>После установки новой версии прежние не нужны.</summary>
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
