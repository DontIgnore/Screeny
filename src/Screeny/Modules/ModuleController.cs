using Screeny.Logging;

namespace Screeny.Modules;

/// <summary>Что показать в настройках про скачиваемый модуль.</summary>
/// <param name="Installed">Установленная версия (с диска) или null.</param>
/// <param name="Progress">Проценты загрузки, пока <paramref name="Downloading"/>.</param>
/// <param name="RemovalPending">Модуль удалён, но файлы держит загруженная библиотека: сотрутся после перезапуска.</param>
/// <param name="Error">Последняя ошибка загрузки или установки (или загрузки модуля в процесс) — для красной строки.</param>
/// <param name="SourceAvailable">Скачать есть откуда: обычный запуск — GitHub, dev — только с <c>--module-source</c>.</param>
/// <param name="Expected">Версия модуля, которая нужна этой сборке.</param>
/// <param name="RequirementsReady">Общие компоненты (<see cref="ModuleSpec.Requires"/>) на месте.</param>
/// <param name="DownloadBytes">Сколько скачать, чтобы модуль заработал: он сам (если не той версии) и недостающие компоненты.</param>
public sealed record ModuleStatus(int? Installed, bool Downloading, int Progress, bool RemovalPending, string? Error, bool SourceAvailable,
    int Expected = OcrModule.Version, bool RequirementsReady = true, long DownloadBytes = 0)
{
    /// <summary>Стоит ровно та версия, что нужна этой сборке, вместе с общими компонентами.</summary>
    public bool Ready => Installed == Expected && !RemovalPending && RequirementsReady;
    /// <summary>Стоит другая версия (Screeny обновился): нужно «Обновить модуль».</summary>
    public bool Outdated => Installed is { } v && v != Expected && !RemovalPending;
}

/// <summary>Скачиваемый модуль для окна настроек и мастера.</summary>
public interface IModuleHost
{
    ModuleStatus Status { get; }
    /// <summary>Состояние поменялось (прогресс, конец загрузки, удаление). Всегда в потоке UI.</summary>
    event Action? Changed;
    /// <param name="notifyInTray">Итог показать уведомлением в трее (загрузка из мастера: окно уже закрыто).</param>
    void StartDownload(bool notifyInTray = false);
    void CancelDownload();
    void Remove();
    /// <summary>Отменить отложенное удаление (<see cref="ModuleStatus.RemovalPending"/>).</summary>
    void UndoRemove();
}

/// <summary>
/// Скачивание, установка и удаление модуля (точное распознавание, смысловой поиск). Скачивается только по явному действию
/// пользователя (кнопка в настройках, галочка в мастере) — в том числе при выключенной проверке обновлений; сам модуль
/// никогда не перекачивается. Сначала ставятся недостающие общие компоненты (<see cref="ModuleSpec.Requires"/>), затем
/// модуль; прогресс — общий по размеру архивов. Одна загрузка за раз: мастер и настройки видят одну и ту же. Установка
/// (хеши, распаковка) идёт в фоне, события — в потоке UI.
/// </summary>
public sealed class ModuleController : IModuleHost
{
    /// <summary>Тестовый экземпляр без <c>--module-source</c> не ходит в сеть.</summary>
    public const string DevNoSource = "Dev instance downloads the module only from a local folder: start it with --module-source <folder>.";

    private readonly ModuleInstaller _installer;
    private readonly Func<ModuleSpec, IModuleSource?> _source;
    private readonly Func<string, bool> _inUse;
    private readonly IReadOnlyList<ModuleSpec> _others;
    private readonly SynchronizationContext _ui;
    private CancellationTokenSource? _download;
    private int _progress;
    private string? _error;
    private int? _installed;
    private bool _requirementsReady;

    /// <summary>Модуль поставлен (true) или нет (false, с текстом ошибки; отмена — без текста), и нужно ли уведомление в трее.</summary>
    public event Action<bool, string?, bool>? Finished;
    public event Action? Changed;
    /// <summary>Модуль удалён (или помечен к удалению после перезапуска).</summary>
    public event Action? Removed;
    /// <summary>Отложенное удаление отменено («Не удалять»), модуль снова на месте.</summary>
    public event Action? Restored;

    /// <param name="source">Источник архива модуля или компонента; null — скачать неоткуда (dev без <c>--module-source</c>).</param>
    /// <param name="inUse">Папку держит загруженная в процесс библиотека.</param>
    /// <param name="others">Другие модули в том же корне: общий компонент удаляется, только если ни одному из установленных не нужен.</param>
    public ModuleController(ModuleInstaller installer, Func<ModuleSpec, IModuleSource?> source, Func<string, bool> inUse,
        IReadOnlyList<ModuleSpec>? others = null)
    {
        _installer = installer;
        _source = source;
        _inUse = inUse;
        _others = others ?? [];
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        Refresh();
    }

    public ModuleSpec Spec => _installer.Spec;

    /// <summary>Установленная версия (обновляется после установки и удаления).</summary>
    public int? InstalledVersion => _installed;

    /// <summary>Папка модуля нужной версии, если он установлен (с общими компонентами) и не помечен к удалению.</summary>
    public string? ReadyFolder => Status.Ready ? _installer.TargetFolder : null;

    /// <summary>Ошибка загрузки модуля в процесс (например, <see cref="Ocr.Paddle.PreciseOcrService.LoadError"/>), если была.</summary>
    public Func<string?> LoadError { get; init; } = () => null;

    /// <summary>Текст ошибки загрузки в процесс → строка для настроек (у каждого модуля своя формулировка).</summary>
    public Func<string, string> LoadErrorText { get; init; } = e => Localization.Loc.F("PreciseOcr.LoadFailed", e);

    public ModuleStatus Status => new(_installed, _download is not null, _progress, _installer.RemovalPending,
        _error ?? (LoadError() is { } e ? LoadErrorText(e) : null), _source(Spec) is not null, Spec.Version, _requirementsReady, MissingBytes());

    public void Refresh()
    {
        _installed = ModuleInstaller.DetectInstalled(_installer.Root, Spec.Id, Spec.Version);
        _requirementsReady = Spec.Requires.All(RequirementPresent);
    }

    /// <summary>Компонент нужной версии на диске и не помечен к удалению (файлы не хешируются: это делает загрузка).</summary>
    private bool RequirementPresent(ModuleSpec r)
    {
        var installer = new ModuleInstaller(_installer.Root, r);
        return !installer.RemovalPending && ModuleInstaller.InstalledVersions(_installer.Root, r.Id).Contains(r.Version);
    }

    private long MissingBytes()
        => (_installed == Spec.Version && !_installer.RemovalPending ? 0 : Spec.ZipSize)
           + Spec.Requires.Where(r => !RequirementPresent(r)).Sum(r => r.ZipSize);

    /// <summary>При запуске, в фоне: стереть помеченное к удалению и остатки прерванных загрузок (один раз на корень модулей).</summary>
    public Task CleanupAsync() => Task.Run(_installer.CleanupOnStartup);

    public void StartDownload(bool notifyInTray = false)
    {
        if (_download is not null) return;
        if (_source(Spec) is null)
        {
            Fail(DevNoSource, notifyInTray);
            return;
        }
        var cts = new CancellationTokenSource();
        _download = cts;
        _progress = 0;
        _error = null;
        Changed?.Invoke();
        _ = RunAsync(cts, notifyInTray);
    }

    private async Task RunAsync(CancellationTokenSource cts, bool notifyInTray)
    {
        string? error = null;
        bool ok = false, cancelled = false;
        try
        {
            await Task.Run(() => InstallAllAsync(p => _ui.Post(_ => { if (_download == cts) { _progress = p; Changed?.Invoke(); } }, null), cts.Token));
            ok = true;
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex)
        {
            error = FriendlyError(ex);
            Log.Error($"Module {Spec.Id} download or install failed", ex);
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

    /// <summary>Компоненты (недостающие), затем модуль; прогресс — доля скачанных байт всех архивов.</summary>
    private async Task InstallAllAsync(Action<int> progress, CancellationToken ct)
    {
        var steps = Spec.Requires.Where(r => !RequirementPresent(r)).ToList();
        if (_installed != Spec.Version || _installer.RemovalPending) steps.Add(Spec);
        long total = Math.Max(1, steps.Sum(s => Math.Max(1, s.ZipSize)));
        long before = 0;
        foreach (var step in steps)
        {
            long size = Math.Max(1, step.ZipSize), offset = before;
            var installer = step == Spec ? _installer : new ModuleInstaller(_installer.Root, step);
            // Уже стоящая и целая версия (поставил другой модуль) не перекачивается.
            await installer.EnsureInstalledAsync(() => _source(step), Report, ct);
            before += size;

            void Report(int p) => progress((int)Math.Min(100, (offset + size * p / 100) * 100 / total));
        }
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
        Log.Error($"Module {Spec.Id} removal: {result}");
        if (result != RemoveResult.NotInstalled) RemoveUnusedRequirements();
        Refresh();
        if (result != RemoveResult.NotInstalled) Removed?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Общий компонент больше не нужен ни одному установленному модулю нужной версии — удаляется (или помечается).</summary>
    private void RemoveUnusedRequirements()
    {
        foreach (var r in Spec.Requires)
        {
            bool needed = _others.Any(o => o.Requires.Any(x => x.Id == r.Id && x.Version == r.Version)
                                            && ModuleInstaller.InstalledVersions(_installer.Root, o.Id).Contains(o.Version)
                                            && !new ModuleInstaller(_installer.Root, o).RemovalPending);
            if (needed) continue;
            Log.Error($"Shared component {r.Id} removal: {new ModuleInstaller(_installer.Root, r).Remove(_inUse)}");
        }
    }

    public void UndoRemove()
    {
        bool restored = _installer.Restore();
        // Компоненты могли пометить к удалению вместе с модулем: возвращаем и их, если файлы целы.
        foreach (var r in Spec.Requires) new ModuleInstaller(_installer.Root, r).Restore();
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
