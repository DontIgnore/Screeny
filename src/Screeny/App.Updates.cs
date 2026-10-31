using System.Windows;
using System.Windows.Input;
using Screeny.Localization;
using Screeny.Logging;
using Screeny.Settings;
using Screeny.Themes;
using Screeny.Updates;

namespace Screeny;

/// <summary>
/// Updates and autostart path. Separate file so App.xaml.cs changes minimally: from there only
/// <see cref="InitUpdates"/> (at end of startup) and <see cref="DisposeUpdates"/> (on exit) are called.
/// </summary>
public partial class App
{
    private VelopackInfo _velopack = VelopackInfo.None;
    private UpdateService? _updates;
    private UpdateWindow? _updateWindow;
    private ICommand? _checkUpdatesCommand;
    /// <summary>Last tray notification — “Version X available”: click opens update window.</summary>
    private UpdateCandidate? _updateBalloon;
    /// <summary>
    /// Update that wasn't asked about (all changes concern disabled features): version being downloaded
    /// and downloaded content that will apply without windows on exit.
    /// </summary>
    private string? _silentVersion;
    private UpdateCandidate? _silentReady;
    /// <summary>Increments when silent update is canceled (version skipped, installing normally): unfinished silent
    /// download, when done, sees different number and does not prepare apply.</summary>
    private int _silentGeneration;
    /// <summary>Normal update already handed to Update.exe: silent-on-exit not launched second time.</summary>
    private bool _applyScheduled;
    /// <summary>Exit due to Windows session ending: silent update not applied (Update.exe may be interrupted).</summary>
    private bool _sessionEnding;

    /// <summary>Tray menu item “Check for updates…”</summary>
    public ICommand CheckUpdatesCommand => _checkUpdatesCommand ??= new RelayCommand(() => _ = CheckForUpdatesManuallyAsync());

    private void InitUpdates()
    {
        _velopack = VelopackInfo.Detect();
        // Autostart path already set in Program.Main. Test instance does not touch working Run key.
        if (!IsDev && _velopack.IsInstalled) RepairStartupPath();

        IUpdateClient client = UpdateService.ChooseSource(IsDev, Args.UpdateSource) switch
        {
            UpdateSourceKind.Folder => VelopackUpdateClient.ForFolder(Args.UpdateSource!),
            UpdateSourceKind.Github => VelopackUpdateClient.ForGithub(),
            _ => NoUpdateClient.Instance, // dev without --update-source: no network, manual check also
        };
        _updates = new UpdateService(client, UpdateStateStore.Default(IsDev), () => Settings.CheckForUpdates);
        _updates.UpdateFound += OnUpdateFound;
        if (_tray is not null) _tray.TrayBalloonTipClicked += (_, _) => { if (_updateBalloon is { } u) ShowUpdateWindow(u); };

        if (UpdateService.AutomaticAllowed(IsDev, Args.UpdateSource is not null, client.CanUpdate)) _ = _updates.RunAsync();
        if (Args.CheckUpdates) _ = CheckForUpdatesManuallyAsync();
        OnHotkeysChanged(); // tray hint was built before client appeared: now with version from Velopack package
    }

    /// <summary>
    /// Version of this copy: from Velopack package (same as update check), without it — from assembly.
    /// Before <see cref="InitUpdates"/> — always from assembly.
    /// </summary>
    public string CurrentVersion => AppVersion.Choose(_updates?.Client.CurrentVersion, AppVersion.Informational());

    /// <summary>Copy can update itself (installed or portable, and has update source).</summary>
    public bool CanUpdateItself => _updates?.Client.CanUpdate == true;

    /// <summary>
    /// Autostart enabled but points to another exe (old portable <c>publish\Screeny.exe</c>, previous folder) —
    /// rewrite to stub of installed version. Disabled autostart is not enabled.
    /// </summary>
    private static void RepairStartupPath()
    {
        try
        {
            if (StartupRegistrar.RepairStalePath(new RegistryRunKey(), StartupRegistrar.CurrentCommand()))
                Log.Error($"Startup entry pointed to another copy; rewritten to {StartupRegistrar.CurrentCommand()}");
        }
        catch (Exception ex) { Log.Error("Startup path repair failed", ex); }
    }

    private void OnUpdateFound(UpdateCandidate update)
    {
        if (IsShuttingDown || _updateWindow is not null) return;
        if (UpdatePrompt.Decide(update, NotesFilter, UpdateCheckKind.Automatic, Loc.Culture.Name).Action == UpdatePromptAction.ApplySilently)
        {
            _ = DownloadSilentlyAsync(update);
            return;
        }
        Notify(Loc.F("Updates.AvailableBalloon", update.Version));
        _updateBalloon = update; // after Notify: it resets flag for other notifications
    }

    /// <summary>
    /// All version changes concern disabled features: don't distract, download in background; <see cref="DisposeUpdates"/>
    /// applies on exit. Error — only log, next check retries. While downloading, version could be
    /// skipped or install started from window, and app could start exiting: then downloaded content silently not applied.
    /// </summary>
    private async Task DownloadSilentlyAsync(UpdateCandidate update)
    {
        if (_updates is null || IsShuttingDown || _silentVersion == update.Version) return;
        var updates = _updates;
        int generation = ++_silentGeneration;
        _silentVersion = update.Version;
        _silentReady = null;
        try
        {
            await updates.DownloadAsync(update, null, CancellationToken.None);
            if (generation != _silentGeneration || IsShuttingDown || updates.IsSkipped(update.Version)) return;
            _silentReady = update;
            Log.Error($"Update {update.Version} concerns only disabled features: downloaded, will be applied silently on exit");
        }
        catch (Exception ex)
        {
            Log.Error($"Silent update {update.Version} download failed", ex);
            if (generation == _silentGeneration) _silentVersion = null;
        }
    }

    /// <summary>
    /// Manual check (tray menu and About in settings): runs even when automatic is off. null — no check was
    /// made: app exiting or update window already open (then it is brought forward).
    /// </summary>
    public async Task<UpdateCheckResult?> CheckForUpdatesAsync()
    {
        if (_updates is null || IsShuttingDown) return null;
        if (_updateWindow is not null) { _updateWindow.Activate(); return null; }
        var result = await _updates.CheckAsync(UpdateCheckKind.Manual);
        return IsShuttingDown ? null : result;
    }

    /// <summary>Tray item: result — dialog, found version — update window.</summary>
    private async Task CheckForUpdatesManuallyAsync()
    {
        switch (await CheckForUpdatesAsync())
        {
            case UpdateCheckResult.Available found:
                ShowUpdateWindow(found.Update);
                break;
            case UpdateCheckResult.UpToDate upToDate:
                Dialogs.Info(null, Loc.T("Updates.UpToDateTitle"), upToDate.CurrentVersion is { } v ? $"Screeny {v}." : "");
                break;
            case UpdateCheckResult.NotUpdatable:
                Dialogs.Info(null, Loc.T("Updates.NotUpdatableTitle"), Loc.T("Updates.NotUpdatableText"));
                break;
            case UpdateCheckResult.Failed failed:
                Dialogs.Info(null, Loc.T("Updates.CheckFailedTitle"), failed.Error.Message);
                break;
        }
    }

    /// <summary>Window always shown even if all changes concern disabled features (happens for manual check).</summary>
    public void ShowUpdateWindow(UpdateCandidate update)
    {
        _updateBalloon = null;
        if (_updates is null || IsShuttingDown) return;
        if (_updateWindow is not null) { _updateWindow.Activate(); return; }
        var updates = _updates;
        var prompt = UpdatePrompt.Decide(update, NotesFilter, UpdateCheckKind.Manual, Loc.Culture.Name);
        _updateWindow = new UpdateWindow(update, prompt, updates.Client.CurrentVersion,
            (progress, ct) => InstallUpdateAsync(updates, update, progress, ct),
            () =>
            {
                if (!updates.Skip(update.Version)) return false;
                CancelSilentUpdate(); // skipped version not installed silently — even if its silent download is still running
                return true;
            });
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
        _updateWindow.Activate();
    }

    /// <summary>
    /// Download, wait for unfinished screenshot saves (as on normal exit), hand apply to Update.exe
    /// and exit: Update.exe waits for process exit, replaces <c>current\</c> and starts new version.
    /// Ongoing silent download of same version is not repeated: wait for it and use downloaded (<see cref="UpdateService.DownloadAsync"/>).
    /// </summary>
    private async Task InstallUpdateAsync(UpdateService updates, UpdateCandidate update, Action<int> progress, CancellationToken ct)
    {
        CancelSilentUpdate(); // this update is installed, not silent one
        await updates.DownloadAsync(update, progress, ct);
        ct.ThrowIfCancellationRequested();
        if (!await PrepareForExitAsync()) return;
        CancelSilentUpdate();
        _applyScheduled = true; // and if Update.exe launch failed — silent-on-exit not needed either
        try { updates.Client.ApplyAfterExit(update, RestartArgs()); }
        catch (Exception ex)
        {
            // Windows already locked for exit: report and still exit; update can be repeated after launch.
            Log.Error("Update apply failed to start", ex);
            Dialogs.Info(null, Loc.T("Updates.ApplyFailedTitle"), Loc.F("Updates.ApplyFailedText", ex.Message));
        }
        Shutdown();
    }

    /// <summary>
    /// Keys for restart after update. Test instance restarts as test (with same keys),
    /// otherwise new version would become working instance; normal — without keys.
    /// </summary>
    private void CancelSilentUpdate()
    {
        _silentGeneration++;
        _silentVersion = null;
        _silentReady = null;
    }

    private string[] RestartArgs() => IsDev ? Environment.GetCommandLineArgs().Skip(1).ToArray() : [];

    private void DisposeUpdates()
    {
        _updates?.Dispose();
        _updateWindow?.Close();
        ApplySilentUpdateOnExit();
    }

    /// <summary>
    /// Exit with silently downloaded update: Update.exe waits for process exit and applies it without windows and without
    /// restart. Not applied if meanwhile “Check for updates” was disabled, version skipped, a feature
    /// from changelog enabled, normal update already scheduled, or exiting due to Windows session end.
    /// </summary>
    private void ApplySilentUpdateOnExit()
    {
        if (_silentReady is not { } update || _updates is null) return;
        _silentReady = null;
        if (_applyScheduled || !Settings.CheckForUpdates || _updates.IsSkipped(update.Version)) return;
        if (_sessionEnding)
        {
            Log.Error($"Silent update {update.Version} not applied: Windows session is ending");
            return;
        }
        // Feature could be re-enabled: then changelog has something to show — let next check ask.
        if (UpdatePrompt.Decide(update, NotesFilter, UpdateCheckKind.Automatic, Loc.Culture.Name).Action != UpdatePromptAction.ApplySilently)
        {
            Log.Error($"Silent update {update.Version} not applied: it now concerns enabled features");
            return;
        }
        try
        {
            _updates.Client.ApplySilentlyAfterExit(update);
            Log.Error($"Update {update.Version} is being applied silently after exit");
        }
        catch (Exception ex) { Log.Error($"Silent update {update.Version} apply failed to start", ex); }
    }
}
