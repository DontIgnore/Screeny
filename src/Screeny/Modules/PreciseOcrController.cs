using Screeny.Logging;

namespace Screeny.Modules;

/// <summary>Что показать в настройках про модуль точного распознавания.</summary>
/// <param name="Installed">Установленная версия (с диска) или null.</param>
/// <param name="Progress">Проценты загрузки, пока <paramref name="Downloading"/>.</param>
/// <param name="RemovalPending">Модуль удалён, но файлы держит загруженная библиотека: сотрутся после перезапуска.</param>
/// <param name="Error">Последняя ошибка загрузки или установки (или загрузки модуля в процесс) — для красной строки.</param>
/// <param name="SourceAvailable">Скачать есть откуда: обычный запуск — GitHub, dev — только с <c>--module-source</c>.</param>
public sealed record PreciseOcrStatus(int? Installed, bool Downloading, int Progress, bool RemovalPending, string? Error, bool SourceAvailable)
{
    /// <summary>Стоит ровно та версия, что нужна этой сборке.</summary>
    public bool Ready => Installed == OcrModule.Version && !RemovalPending;
    /// <summary>Стоит другая версия (Screeny обновился): нужно «Обновить модуль».</summary>
    public bool Outdated => Installed is { } v && v != OcrModule.Version && !RemovalPending;
}

/// <summary>Модуль точного распознавания для окна настроек и мастера.</summary>
public interface IPreciseOcrHost
{
    PreciseOcrStatus Status { get; }
    /// <summary>Состояние поменялось (прогресс, конец загрузки, удаление). Всегда в потоке UI.</summary>
    event Action? Changed;
    /// <param name="notifyInTray">Итог показать уведомлением в трее (загрузка из мастера: окно уже закрыто).</param>
    void StartDownload(bool notifyInTray = false);
    void CancelDownload();
    void Remove();
    /// <summary>Отменить отложенное удаление (<see cref="PreciseOcrStatus.RemovalPending"/>).</summary>
    void UndoRemove();
}

/// <summary>
/// Скачивание, установка и удаление модуля. Скачивается только по явному действию пользователя (кнопка в настройках,
/// галочка в мастере) — в том числе при выключенной проверке обновлений; сам модуль никогда не перекачивается.
/// Одна загрузка за раз: мастер и настройки видят одну и ту же. Установка (хеши, распаковка) идёт в фоне,
/// события — в потоке UI.
/// </summary>
public sealed class PreciseOcrController : IPreciseOcrHost
{
    private readonly ModuleInstaller _installer;
    private readonly Func<IModuleSource?> _source;
    private readonly Func<string, bool> _inUse;
    private readonly SynchronizationContext _ui;
    private CancellationTokenSource? _download;
    private int _progress;
    private string? _error;
    private int? _installed;

    /// <summary>Модуль поставлен (true) или нет (false, с текстом ошибки; отмена — без текста), и нужно ли уведомление в трее.</summary>
    public event Action<bool, string?, bool>? Finished;
    public event Action? Changed;
    /// <summary>Модуль удалён (или помечен к удалению после перезапуска).</summary>
    public event Action? Removed;
    /// <summary>Отложенное удаление отменено («Не удалять»), модуль снова на месте.</summary>
    public event Action? Restored;

    /// <param name="source">Источник архива или null — скачать неоткуда (dev без <c>--module-source</c>).</param>
    /// <param name="inUse">Папку модуля держит загруженная в процесс библиотека.</param>
    public PreciseOcrController(ModuleInstaller installer, Func<IModuleSource?> source, Func<string, bool> inUse)
    {
        _installer = installer;
        _source = source;
        _inUse = inUse;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        Refresh();
    }

    /// <summary>Установленная версия (обновляется после установки и удаления).</summary>
    public int? InstalledVersion => _installed;

    /// <summary>Папка модуля нужной версии, если он установлен и не помечен к удалению.</summary>
    public string? ReadyFolder => Status.Ready ? _installer.TargetFolder : null;

    /// <summary>Ошибка загрузки модуля в процесс (<see cref="Ocr.Paddle.PreciseOcrService.LoadError"/>), если была.</summary>
    public Func<string?> LoadError { get; init; } = () => null;

    public PreciseOcrStatus Status => new(_installed, _download is not null, _progress, _installer.RemovalPending,
        _error ?? (LoadError() is { } e ? Localization.Loc.F("PreciseOcr.LoadFailed", e) : null), _source() is not null);

    public void Refresh() => _installed = ModuleInstaller.DetectInstalled(_installer.Root, OcrModule.Id, OcrModule.Version);

    /// <summary>При запуске, в фоне: стереть помеченное к удалению и остатки прерванных загрузок.</summary>
    public Task CleanupAsync() => Task.Run(_installer.CleanupOnStartup);

    public void StartDownload(bool notifyInTray = false)
    {
        if (_download is not null) return;
        if (_source() is not { } source)
        {
            Fail("Dev instance downloads the module only from a local folder: start it with --module-source <folder>.", notifyInTray);
            return;
        }
        var cts = new CancellationTokenSource();
        _download = cts;
        _progress = 0;
        _error = null;
        Changed?.Invoke();
        _ = RunAsync(source, cts, notifyInTray);
    }

    private async Task RunAsync(IModuleSource source, CancellationTokenSource cts, bool notifyInTray)
    {
        string? error = null;
        bool ok = false, cancelled = false;
        try
        {
            await Task.Run(() => _installer.InstallAsync(source, p => _ui.Post(_ => { if (_download == cts) { _progress = p; Changed?.Invoke(); } }, null), cts.Token));
            ok = true;
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex)
        {
            error = FriendlyError(ex);
            Log.Error("OCR module download or install failed", ex);
        }
        finally { cts.Dispose(); }
        _download = null;
        _error = error;
        Refresh();
        Changed?.Invoke();
        // Загрузка из мастера всегда заканчивается уведомлением — и при отмене (её мог нажать в настройках сам пользователь).
        if (!cancelled) Finished?.Invoke(ok, error, notifyInTray);
        else if (notifyInTray) Finished?.Invoke(false, Localization.Loc.T("PreciseOcr.Cancelled"), true);
    }

    /// <summary>Текст ошибки для настроек и уведомления: свои сообщения как есть, сеть и диск — понятными словами;
    /// сообщения исключений .NET пользователю не показываются (они в журнале).</summary>
    public static string FriendlyError(Exception ex) => ex switch
    {
        ModuleInstallException m => m.Message,
        System.Net.Http.HttpRequestException or TimeoutException => ModuleInstaller.NoConnection,
        UnauthorizedAccessException => Localization.Loc.T("PreciseOcr.NoAccess"),
        IOException => Localization.Loc.T("PreciseOcr.WriteFailed"),
        System.IO.InvalidDataException => Localization.Loc.T("PreciseOcr.ArchiveCorrupt"),
        _ => Localization.Loc.T("PreciseOcr.InstallFailed"),
    };

    public void CancelDownload() => _download?.Cancel();

    public void Remove()
    {
        if (_download is not null) return;
        _error = null;
        var result = _installer.Remove(_inUse);
        Log.Error($"OCR module removal: {result}");
        Refresh();
        if (result != RemoveResult.NotInstalled) Removed?.Invoke();
        Changed?.Invoke();
    }

    public void UndoRemove()
    {
        bool restored = _installer.Restore();
        _error = restored ? null : Localization.Loc.T("PreciseOcr.AlreadyDeleted");
        Refresh();
        if (restored) Restored?.Invoke();
        Changed?.Invoke();
    }

    private void Fail(string message, bool notifyInTray)
    {
        _error = message;
        Changed?.Invoke();
        Finished?.Invoke(false, message, notifyInTray);
    }
}
