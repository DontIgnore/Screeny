using Screeny.Logging;

namespace Screeny.Modules;

/// <summary>What to show in settings about precise recognition module.</summary>
/// <param name="Installed">Installed version (from disk) or null.</param>
/// <param name="Progress">Download percent while <paramref name="Downloading"/>.</param>
/// <param name="RemovalPending">Module deleted but files held by loaded library: erased after restart.</param>
/// <param name="Error">Last download/install error (or module load into process) — for red line.</param>
/// <param name="SourceAvailable">Download source exists: normal — GitHub, dev — only with <c>--module-source</c>.</param>
public sealed record PreciseOcrStatus(int? Installed, bool Downloading, int Progress, bool RemovalPending, string? Error, bool SourceAvailable)
{
    /// <summary>Exactly the version this build needs is installed.</summary>
    public bool Ready => Installed == OcrModule.Version && !RemovalPending;
    /// <summary>Another version installed (Screeny updated): need “Update module”.</summary>
    public bool Outdated => Installed is { } v && v != OcrModule.Version && !RemovalPending;
}

/// <summary>Precise recognition module for settings window and wizard.</summary>
public interface IPreciseOcrHost
{
    PreciseOcrStatus Status { get; }
    /// <summary>State changed (progress, download end, deletion). Always UI thread.</summary>
    event Action? Changed;
    /// <param name="notifyInTray">Show result as tray notification (wizard download: window already closed).</param>
    void StartDownload(bool notifyInTray = false);
    void CancelDownload();
    void Remove();
    /// <summary>Cancel pending deletion (<see cref="PreciseOcrStatus.RemovalPending"/>).</summary>
    void UndoRemove();
}

/// <summary>
/// Module download, install and delete. Downloaded only on explicit user action (settings button,
/// wizard checkbox) — including when update check disabled; module itself never re-downloaded.
/// One download at a time: wizard and settings see same. Install (hashing, unpack) in background,
/// events in UI thread.
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

    /// <summary>Module installed (true) or not (false, with error text; cancel without text), and whether tray notification needed.</summary>
    public event Action<bool, string?, bool>? Finished;
    public event Action? Changed;
    /// <summary>Module deleted (or marked for deletion after restart).</summary>
    public event Action? Removed;
    /// <summary>Pending deletion canceled (“Don't delete”), module back in place.</summary>
    public event Action? Restored;

    /// <param name="source">Archive source or null — nothing to download from (dev without <c>--module-source</c>).</param>
    /// <param name="inUse">Module folder held by library loaded into process.</param>
    public PreciseOcrController(ModuleInstaller installer, Func<IModuleSource?> source, Func<string, bool> inUse)
    {
        _installer = installer;
        _source = source;
        _inUse = inUse;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        Refresh();
    }

    /// <summary>Installed version (updates after install/delete).</summary>
    public int? InstalledVersion => _installed;

    /// <summary>Module folder of needed version if installed and not marked for deletion.</summary>
    public string? ReadyFolder => Status.Ready ? _installer.TargetFolder : null;

    /// <summary>Module load error (<see cref="Ocr.Paddle.PreciseOcrService.LoadError"/>), if any.</summary>
    public Func<string?> LoadError { get; init; } = () => null;

    public PreciseOcrStatus Status => new(_installed, _download is not null, _progress, _installer.RemovalPending,
        _error ?? (LoadError() is { } e ? Localization.Loc.F("PreciseOcr.LoadFailed", e) : null), _source() is not null);

    public void Refresh() => _installed = ModuleInstaller.DetectInstalled(_installer.Root, OcrModule.Id, OcrModule.Version);

    /// <summary>At startup in background: erase marked for deletion and interrupted download remnants.</summary>
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
        // Wizard download always ends with notification — also on cancel (could be pressed in settings by user).
        if (!cancelled) Finished?.Invoke(ok, error, notifyInTray);
        else if (notifyInTray) Finished?.Invoke(false, Localization.Loc.T("PreciseOcr.Cancelled"), true);
    }

    /// <summary>Error text for settings and notification: own messages as-is, network/disk in plain words;
    /// .NET exception messages not shown (they are in log).</summary>
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
