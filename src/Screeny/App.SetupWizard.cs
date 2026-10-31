using System.Windows;
using System.Windows.Input;
using H.NotifyIcon;
using Screeny.Features;
using Screeny.Hotkeys;
using Screeny.Localization;
using Screeny.Logging;
using Screeny.Migration;

namespace Screeny;

/// <summary>
/// First-run wizard and conflicts with other screenshot programs. Separate file so App.xaml.cs has only
/// two entry points: <see cref="RunElevatedActionAndExit"/> and <see cref="CheckSetupWizardOnStartup"/>.
/// </summary>
public partial class App
{
    private SetupWizardWindow? _setupWizardWindow;
    private ICommand? _setupWizardCommand;
    private bool _wizardBalloonPending;
    private bool _balloonHooked;

    /// <summary>Tray item “Check conflicts…”</summary>
    public ICommand SetupWizardCommand => _setupWizardCommand ??= new RelayCommand(ShowSetupWizard);

    /// <summary>
    /// <c>--elevated-action …</c>: instance launched by wizard with admin rights for one action. Exits before
    /// mutex, tray and hotkeys, with code from <see cref="ElevatedAction"/>.
    /// </summary>
    private void RunElevatedActionAndExit(string[] args)
    {
        int code = ElevatedAction.RunReal(args);
        Environment.ExitCode = code;
        Shutdown(code);
    }

    /// <summary>
    /// At startup: wizard not passed — show it; passed — check in background if a new program appeared that
    /// occupies an enabled hotkey, and notify via notification (not wizard). Test instance shows
    /// wizard only with <c>--setup-wizard</c> and never checks real environment.
    /// </summary>
    private void CheckSetupWizardOnStartup()
    {
        switch (SetupWizard.OnStartup(IsDev, Args.SetupWizard, Settings))
        {
            case SetupWizard.StartupAction.ShowWizard: ShowSetupWizard(); break;
            case SetupWizard.StartupAction.CheckForNewConflicts: _ = WarnAboutNewConflictsAsync(); break;
        }
    }

    private async Task WarnAboutNewConflictsAsync()
    {
        try
        {
            var services = MigrationServices.Create(IsDev, Args.Scenario);
            var conflicts = await Task.Run(() => ConflictDetector.Detect(services.Capture(), ConflictCatalog.Default));
            if (IsShuttingDown) return;
            var (fresh, known) = SetupWizard.CheckForNewConflicts(conflicts, Settings.Hotkeys, Settings.KnownConflicts);
            if (!known.SequenceEqual(Settings.KnownConflicts))
            {
                Settings.KnownConflicts = known;
                SaveSettings(); // remind once, not every launch
            }
            if (fresh.Count == 0) return;
            HookBalloonClick();
            _wizardBalloonPending = true;
            Notify(SetupWizard.NotificationText(fresh, Settings.Hotkeys));
        }
        catch (Exception ex) { Log.Error("Conflict check failed", ex); }
    }

    private void HookBalloonClick()
    {
        if (_balloonHooked || _tray is null) return;
        _balloonHooked = true;
        _tray.TrayBalloonTipClicked += (_, _) =>
        {
            if (!_wizardBalloonPending) return;
            _wizardBalloonPending = false;
            ShowSetupWizard();
        };
    }

    /// <summary>Conflict wizard: at first run, from tray and from settings. Environment scanned in background.</summary>
    public async void ShowSetupWizard()
    {
        if (_setupWizardWindow is not null) { _setupWizardWindow.Activate(); return; }
        if (IsShuttingDown) return;
        // In --dev always fake environment and fake actions (MigrationServices).
        var services = MigrationServices.Create(IsDev, Args.Scenario);
        IEnvironmentSnapshot env;
        try { env = await Task.Run(services.Capture); }
        catch (Exception ex)
        {
            Log.Error("Setup wizard scan failed", ex);
            Notify(Loc.T("App.WizardScanFailed"));
            return;
        }
        if (_setupWizardWindow is not null || IsShuttingDown) return;
        var host = new SetupWizardHost(Settings.Hotkeys, Settings.CheckForUpdates, [.. Settings.DisabledFeatures], Settings.Language, services, ConflictCatalog.Default,
            ApplyHotkeys, FinishSetupWizard, Modules.PreciseOcrRules.WizardOffersDownload(_ocrModule?.Status) ? DownloadPreciseOcrFromWizard : null);
        _setupWizardWindow = new SetupWizardWindow(host, env);
        _setupWizardWindow.Closed += (_, _) => _setupWizardWindow = null;
        _setupWizardWindow.Show();
        _setupWizardWindow.Activate();
    }

    /// <summary>
    /// Wizard completed or skipped: remember version (not to show again) and conflicts that remain after it
    /// (not to remind via notification; fixed ones are forgotten — if they return, notification). “Check for updates”
    /// and feature choices are saved only on “Apply” (or on “Skip” after “Apply” where hotkey didn't work);
    /// features are applied immediately (<see cref="ApplyFeatures"/>).
    /// </summary>
    private void FinishSetupWizard(bool? checkForUpdates, IReadOnlyList<string> seenConflicts, IReadOnlyList<KeyValuePair<string, bool>>? features,
        string? language)
    {
        if (checkForUpdates is { } updates) Settings.CheckForUpdates = updates;
        // Wizard showed selected language immediately; “Skip” restores saved one.
        if (language is not null) Settings.Language = language;
        Loc.Apply(Settings.Language);
        if (features is { Count: > 0 })
        {
            // Over current settings: settings window from which wizard was opened may have changed them already.
            Settings.DisabledFeatures = FeatureGate.MergeDisabled(Settings.DisabledFeatures, features);
            ApplyFeatures();
        }
        Settings.SetupWizardVersion = Math.Max(Settings.SetupWizardVersion, SetupWizard.Version);
        Settings.KnownConflicts = [.. seenConflicts.Distinct(StringComparer.Ordinal)]; // only what interferes now
        if (!SaveSettings()) Notify(Loc.T("App.WizardSaveFailed"));
        // Wizard opened from settings window and applied something: without this “Save” there would return hotkeys and updates from
        // moment of opening. Plain “Skip” changed nothing — unsaved edits in settings window are untouched.
        if (checkForUpdates is not null) _settingsWindow?.RefreshFromLiveSettings(HotkeyReport);
    }
}
