using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Screeny.Logging;

namespace Screeny.History;

/// <summary>Sidecar-файлы снимка для повторного редактирования.</summary>
/// <param name="Json">Документ аннотаций (<see cref="Annotations.AnnotationSerializer"/>); null — не писать.</param>
/// <param name="OriginalPng">Регион снимка без пометок и без оформления; null — не писать (настройка выключена).</param>
public sealed record HistorySidecar(string? Json, byte[]? OriginalPng);

/// <summary>
/// Папка снимков истории. Своими считаются только файлы с именем <c>yyyyMMdd_HHmmss_fff[_N].png</c>:
/// пользователь может выбрать папку с другими картинками, и ротация/очистка не должны их трогать.
/// У снимка могут быть sidecar-файлы <c>&lt;имя&gt;.screeny.json</c> и <c>&lt;имя&gt;.orig.png</c> (повторное редактирование):
/// List() их не показывает, Delete/Prune/Clear удаляют вместе со снимком.
/// </summary>
public sealed class HistoryStore
{
    private const string StampFormat = "yyyyMMdd_HHmmss_fff";
    private const string TempSuffix = ".tmp";
    private static readonly Regex NamePattern =
        new(@"^(?<stamp>\d{8}_\d{6}_\d{3})(?:_(?<n>[1-9]\d{0,8}))?\.png$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Суффиксы sidecar-файлов: документ аннотаций и оригинал без пометок.</summary>
    public const string JsonSuffix = ".screeny.json", OriginalSuffix = ".orig.png";

    /// <summary>Строгое имя sidecar-файла: штамп снимка истории + суффикс. Чужие foo.screeny.json сюда не попадают.</summary>
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

    /// <summary>Сохраняет уже закодированный PNG (чтобы не кодировать дважды для буфера и истории).</summary>
    public string Save(byte[] png, DateTime? timestamp = null) => Save(png, sidecar: null, timestamp);

    /// <summary>
    /// Сохраняет снимок вместе с sidecar-файлами атомарно: всё пишется во временные файлы с «чужими» именами
    /// (их List() не видит), затем переименовывается — sidecar-файлы первыми, снимок последним, чтобы запись
    /// не появилась без своих данных. При сбое временные и уже переименованные файлы удаляются (откат).
    /// Если процесс упал между переименованиями, остаются осиротевшие sidecar-файлы — их убирают Prune и Clear.
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
        files.Add((path, png)); // снимок — последним

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
            // Уборка не должна скрывать исходную ошибку.
            foreach (var temp in temporaryFiles) TryDelete(temp);
            foreach (var final in moved) TryDelete(final);
            throw;
        }
        Prune(keep: path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* уборка — по возможности */ }
    }

    /// <summary>Пути sidecar-файлов снимка (в той же папке): документ аннотаций и оригинал без пометок.</summary>
    public static (string Json, string Original) SidecarPaths(string pngPath)
    {
        var stem = Path.Combine(Path.GetDirectoryName(pngPath) ?? "", Path.GetFileNameWithoutExtension(pngPath));
        return (stem + JsonSuffix, stem + OriginalSuffix);
    }

    /// <summary>
    /// Имя для нового снимка с этим штампом. Суффикс — больше максимального существующего, а не первый свободный:
    /// после ротации младшие номера освобождаются, и повторно занятый «_1» оказался бы старше уцелевших «_10».
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
            // Осиротевший sidecar с этим именем не должен приклеиться к новому снимку.
            if (!File.Exists(path) && !File.Exists(json) && !File.Exists(original)) return path;
        }
    }

    /// <summary>
    /// Снимки от новых к старым: по времени из имени, затем по суффиксу коллизии численно.
    /// Чужие файлы не попадают в список. Ошибка чтения папки → пустой список.
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

    /// <summary>Как <see cref="List"/>, но ошибку чтения папки пробрасывает (окну истории нужно её показать). Нет папки — пусто.</summary>
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
    /// Удаляет снимок и — если это снимок истории (строгое имя) — его sidecar-файлы. Ошибка удаления самого снимка
    /// пробрасывается; не удалившийся sidecar остаётся сиротой и уходит при следующей ротации или очистке.
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

    /// <summary>Удаляет все снимки истории с их sidecar-файлами (и осиротевшие); чужие файлы в папке (в том числе PNG) не трогает.</summary>
    public void Clear()
    {
        foreach (var item in List()) Delete(item.Path);
        DeleteOrphanSidecars();
    }

    /// <summary>
    /// Сохранённые оригиналы без пометок (<c>&lt;штамп&gt;.orig.png</c>) в папке истории: они содержат то, что было
    /// скрыто размытием. Чужие файлы (<c>bar.orig.png</c>) не считаются. Нет папки или ошибка чтения — пусто.
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
    /// Удаляет сохранённые оригиналы (только <see cref="ListOriginals"/>); снимки и документы пометок остаются,
    /// и записи открываются в редакторе как готовые картинки. Возвращает, сколько файлов удалено.
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

    /// <summary>Время из имени вида yyyyMMdd_HHmmss_fff[_N].png.</summary>
    public static bool TryParseTimestamp(string fileName, out DateTime timestamp) => TryParseName(fileName, out timestamp, out _);

    /// <summary>Время и номер коллизии (0 — без суффикса) из имени снимка; false для чужого файла.</summary>
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
    /// Оставляет <see cref="Limit"/> снимков. Только что сохранённый не удаляется, даже если его штамп старше
    /// остальных (часы переведены назад): иначе Save вернул бы путь к несуществующему файлу.
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
    /// Sidecar-файлы со строгим именем штампа без основного снимка (сбой между переименованиями, снимок удалён
    /// вручную). Чужие <c>foo.screeny.json</c> и <c>bar.orig.png</c> не трогаются.
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
