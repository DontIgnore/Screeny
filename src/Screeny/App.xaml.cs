using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Screeny.Capture;
using Screeny.Editing;
using Screeny.Export;
using Screeny.Features;
using Screeny.History;
using Screeny.Hotkeys;
using Screeny.Localization;
using Screeny.Logging;
using Screeny.Migration;
using Screeny.Native;
using Screeny.Ocr;
using Screeny.Overlay;
using Screeny.Pin;
using Screeny.Settings;
using Screeny.Themes;

namespace Screeny;

public partial class App : Application, INotifyPropertyChanged
{
    private const string MutexName = "Screeny.SingleInstance";
    private const string CaptureEventName = "Screeny.CaptureRequested";
    private const string DevTrayText = "Screeny (dev)";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _captureEvent;
    private readonly ManualResetEvent _stopListening = new(false);
    private TaskbarIcon? _tray;
    private HotkeyManager? _hotkeys;
    private bool _capturing;
    private readonly ImageSaveWorker _imageSaver = new();
    private readonly HashSet<Task> _pendingOperations = new();
    /// <summary>Number of last operation that wrote to clipboard: outdated OCR result does not overwrite it.</summary>
    private int _clipboardOperation;

    /// <summary>Any image write to clipboard (pin, history) makes unfinished OCR outdated.</summary>
    public void NoteClipboardWrite() => _clipboardOperation++;
    private int ReserveClipboardWrite() => ++_clipboardOperation;
    private bool IsCurrentClipboardWrite(int operation) => operation == _clipboardOperation;

    /// <summary>Startup keys; <see cref="StartupArgs.Dev"/> — test instance that doesn't interfere with working.</summary>
    public StartupArgs Args { get; private set; } = StartupArgs.Parse([]);
    public bool IsDev => Args.Dev;
    public SettingsStore SettingsStore { get; private set; } = SettingsStore.Default();
    public AppSettings Settings { get; private set; } = new();
    /// <summary>History folder; null — History feature disabled (screenshots only copied, folder not read).</summary>
    public HistoryStore? History { get; private set; }
    /// <summary>Tray items “History…” and “Open history folder”.</summary>
    public bool HistoryFeatureOn => Settings.IsFeatureOn(FeatureCatalog.History);

    public ICommand CaptureCommand { get; private set; } = null!;
    public ICommand HistoryCommand { get; private set; } = null!;
    public ICommand OpenFolderCommand { get; private set; } = null!;
    public ICommand SettingsCommand { get; private set; } = null!;
    public ICommand ExitCommand { get; private set; } = null!;
    /// <summary>All working screenshot hotkeys joined by “ / ” (long list — first and “…”); “—” if none.</summary>
    public string HotkeyText => HotkeyReport.ActiveText();

    /// <summary>Hotkey state after last apply: what works, what's busy, whether fallback enabled.</summary>
    public HotkeyReport HotkeyReport => _hotkeys?.Report ?? HotkeyReport.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Any startup error — message and exit, not process without tray/hotkey holding mutex
    /// (due to which all subsequent launches would silently exit as “second instance”).
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Elevated wizard instance: one whitelisted action and exit — before mutex, tray, hotkeys.
        if (ElevatedAction.IsRequested(e.Args)) { RunElevatedActionAndExit(e.Args); return; }
        try
        {
            Args = StartupArgs.FromProcess(e.Args); // --dev or SCREENY_DEV=1
            // Create event before mutex: otherwise second instance could create it, signal and close
            // before first opened — signal lost with kernel object.
            _captureEvent = new EventWaitHandle(false, EventResetMode.AutoReset, CaptureEventName + Args.Suffix);
            _mutex = new Mutex(true, MutexName + Args.Suffix, out _ownsMutex);
            if (!_ownsMutex)
            {
                // Test instance never asks first to take screenshot: it would take real screenshot.
                if (IsDev) { Log.Error("Dev instance is already running; exiting"); Shutdown(3); return; }
                // Allow first instance to bring overlay to front — otherwise it won't get keyboard.
                User32.AllowSetForegroundWindow(-1 /* ASFW_ANY */);
                _captureEvent.Set();
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (_, ex) => { Log.Error("Unhandled", ex.Exception); ex.Handled = true; };
            ThemeManager.Initialize(this);
            if (IsDev) SettingsStore = SettingsStore.Dev();
            // Small monitor check without second monitor: windows fit into this work area (device pixels).
            if (IsDev && WindowFit.ParseWorkArea(Environment.GetEnvironmentVariable("SCREENY_DEV_WORKAREA")) is { } work) ChromeWindow.DevWorkAreaOverride = work;
            bool firstDevRun = IsDev && !File.Exists(SettingsStore.FilePath);
            Settings = SettingsStore.Load();
            Loc.Apply(Settings.Language);
            ApplyLanguageFonts();
            Loc.Changed += OnLanguageChanged;
            // Test instance does not write to user history: own default folder.
            if (firstDevRun) Settings.HistoryFolder = SettingsStore.DevHistoryFolder();
            UpdateHistoryStore();

            CaptureCommand = new RelayCommand(StartCapture);
            HistoryCommand = new RelayCommand(ShowHistory);
            OpenFolderCommand = new RelayCommand(OpenHistoryFolder);
            SettingsCommand = new RelayCommand(ShowSettings);
            ExitCommand = new RelayCommand(ExitApp);

            _tray = (TaskbarIcon)FindResource("TrayIcon");
            _tray.DataContext = this;
            _tray.ForceCreate(enablesEfficiencyMode: false); // EcoQoS would slow overlay display

            InitHotkeys();
            ListenForSecondInstance();
            InitPreciseOcr(); // App.PreciseOcr.cs; before windows from keys: settings need module state
            OpenWindowsFromArgs();
            InitUpdates(); // App.Updates.cs
            CheckSetupWizardOnStartup(); // App.SetupWizard.cs
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            MessageBox.Show(Loc.F("App.StartFailed", ex.Message), "Screeny", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>Application is exiting: editor windows don't ask about unsaved marks.</summary>
    public bool IsShuttingDown { get; private set; }

    private async void ExitApp()
    {
        if (await PrepareForExitAsync()) Shutdown();
    }

    /// <summary>Forbids new actions and waits for unfinished operations and saves. false — exit already in progress.</summary>
    private async Task<bool> PrepareForExitAsync()
    {
        if (IsShuttingDown) return false;
        IsShuttingDown = true;
        if (_historyWindow is not null) _historyWindow.IsEnabled = false;
        if (_settingsWindow is not null) _settingsWindow.IsEnabled = false;
        foreach (Window window in Windows)
            if (window is OverlayWindow or EditorWindow or PinWindow) window.IsEnabled = false;
        while (_pendingOperations.Count > 0)
        {
            try { await Task.WhenAll(_pendingOperations.ToArray()); }
            catch (Exception ex) { Log.Error("Pending operation failed during shutdown", ex); }
            _pendingOperations.RemoveWhere(task => task.IsCompleted);
        }
        await _imageSaver.Queue.DrainAsync();
        return true;
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsShuttingDown = true;
        _sessionEnding = true; // App.Updates.cs: silent update not applied on such exit
        // On Windows session end no guarantee of time to wait or continue UI.
        if (!_imageSaver.Queue.DrainAsync().Wait(TimeSpan.FromSeconds(5)))
            Log.Error("Timed out waiting for image saves during session ending");
        base.OnSessionEnding(e);
    }

    /// <summary>
    /// Manual window check: <c>--settings</c> and <c>--history</c> open window right after startup,
    /// <c>--dev --overlay-test x.png</c> — overlay on picture, <c>--dev --edit x.png</c> — screenshot editor.
    /// Works only on first instance — second, as usual, only asks first to take screenshot.
    /// </summary>
    private void OpenWindowsFromArgs()
    {
        if (Args.OpenSettings) ShowSettings();
        if (Args.OpenHistory) ShowHistory();
        if (Args.Feedback is { } feedback) ShowFeedback(feedback == "idea" ? Feedback.FeedbackKind.Idea : Feedback.FeedbackKind.Bug, Args.Scenario);
        if (Args.OverlayTest is not null) StartCapture();
        if (Args.Edit is not null) OpenEditor(Args.Edit)?.ApplyDevScenario(Args.Scenario);
    }

    /// <summary>
    /// Registers all enabled hotkeys from settings; problems (busy, Windows PrintScreen, fallback Ctrl+Alt+S) —
    /// one notification. Test instance registers no <c>RegisterHotKey</c> (they are on working), and hook
    /// Win+Shift+S only with <c>--enable-ll-hook</c>.
    /// </summary>
    private void InitHotkeys()
    {
        _hotkeys = new HotkeyManager(new WindowsHotkeyBackend(Dispatcher), allowRegisterHotKey: !IsDev, allowHook: !IsDev || Args.EnableLlHook);
        _hotkeys.Pressed += StartCapture;
        var result = _hotkeys.Apply(Settings.Hotkeys, strict: false);
        if (result.Report.ProblemSummary() is { } problem)
        {
            Log.Error($"Hotkeys: {problem}");
            Notify(problem);
        }
        OnHotkeysChanged();
    }

    /// <summary>
    /// Applies new hotkey list (for first-run wizard and settings window): normalizes, atomically
    /// re-registers, and on success saves to settings. On failure previous hotkeys keep working,
    /// and <see cref="HotkeyApplyResult.Error"/> names entry that didn't work.
    /// Call from UI thread after startup (<see cref="OnStartup"/> already created hotkey manager).
    /// </summary>
    public HotkeyApplyResult ApplyHotkeys(IEnumerable<HotkeyBinding> hotkeys)
    {
        if (_hotkeys is null) throw new InvalidOperationException("Hotkeys are not initialized yet");
        var normalized = AppSettings.NormalizeHotkeys(hotkeys);
        var result = ApplyHotkeyList(normalized);
        if (result.Success)
        {
            Settings.Hotkeys = normalized;
            SaveSettings();
        }
        return result;
    }

    /// <summary>Strict apply of normalized list; about newly enabled fallback hotkey — notification.</summary>
    private HotkeyApplyResult ApplyHotkeyList(IReadOnlyList<HotkeyBinding> normalized)
    {
        bool hadFallback = HotkeyReport.Fallback is not null;
        var result = _hotkeys!.Apply(normalized, strict: true);
        if (!result.Success) Log.Error($"Hotkeys apply rejected: {result.Error?.Message}");
        else if (!hadFallback && result.Report.ProblemSummary() is { } problem && result.Report.Fallback is not null)
        {
            Log.Error($"Hotkeys: {problem}");
            Notify(problem);
        }
        OnHotkeysChanged();
        return result;
    }

    private SettingsWindow? _settingsWindow;

    public void ShowSettings()
    {
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        var report = HotkeyReport;
        bool printScreenReserved = PrintScreenPolicy.IsReservedBySystem(HotkeySpec.PrintScreen);
        if (IsDev && Args.Scenario == "hotkey-problems") (report, printScreenReserved) = (DevHotkeyProblems(), true);
        _settingsWindow = new SettingsWindow(Settings, report, printScreenReserved, SafeIsStartupEnabled(), ApplySettings, OcrModuleHost);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>
    /// <c>--dev --settings --scenario hotkey-problems</c>: settings window as if PrintScreen is taken by Windows, first
    /// own combo busy, Win+Shift+S hook not installed and fallback hotkey works — to check hints without
    /// real registration.
    /// </summary>
    private HotkeyReport DevHotkeyProblems()
    {
        var states = Settings.Hotkeys.Select(b => b.Kind switch
        {
            _ when !b.Enabled => new HotkeyState(b, HotkeyStatus.Disabled),
            HotkeyKind.PrintScreen => new HotkeyState(b, HotkeyStatus.ReservedBySystem),
            HotkeyKind.WinShiftS => new HotkeyState(b, HotkeyStatus.Failed, 5),
            _ when b.ToRegisterSpec() is not null && b == Settings.Hotkeys.First(h => h.Kind == HotkeyKind.Custom && h.Enabled)
                => new HotkeyState(b, HotkeyStatus.Busy, GlobalHotkey.ErrorHotkeyAlreadyRegistered),
            _ => new HotkeyState(b, HotkeyStatus.Active),
        }).ToList();
        return new HotkeyReport(states, HotkeySpec.Fallback);
    }

    private static bool SafeIsStartupEnabled()
    {
        try { return StartupRegistrar.IsEnabled(); }
        catch (Exception ex) { Log.Error("Startup check failed", ex); return false; }
    }

    /// <summary>
    /// Applies and saves settings from window. Returns error or null.
    /// Order: folder → autostart → hotkeys last (atomically; on failure autostart rolled back), and only then
    /// changes transferred to current Settings object. So on error nothing is half-applied,
    /// and LastColor/LastThickness that overlay might have changed are not overwritten by values from window open time.
    /// Hotkeys re-register even without list changes: so PrintScreen is picked up after disabling Windows system
    /// capture. Entry that didn't work before doesn't block saving — only newly enabled one does.
    /// </summary>
    private SettingsApplyError? ApplySettings(AppSettings updated)
    {
        // Without history folder not needed and not created (its window fields hidden and arrive unchanged).
        if (updated.IsFeatureOn(FeatureCatalog.History))
        {
            try { Directory.CreateDirectory(updated.HistoryFolder); }
            catch (Exception ex) { Log.Error("History folder failed", ex); return new(Loc.F("App.HistoryFolderCreateFailed", ex.Message)); }
        }

        // Test instance does not touch autostart: it belongs to working. Hotkeys via HotkeyManager,
        // which in dev registers nothing itself (except hook with --enable-ll-hook).
        bool startupWas = !IsDev && SafeIsStartupEnabled();
        if (!IsDev && startupWas != updated.StartWithWindows)
        {
            try { StartupRegistrar.Set(updated.StartWithWindows); }
            catch (Exception ex) { Log.Error("Startup registration failed", ex); return new(Loc.T("App.StartupFailed")); }
        }

        var hotkeys = AppSettings.NormalizeHotkeys(updated.Hotkeys);
        var result = ApplyHotkeyList(hotkeys);
        if (!result.Success)
        {
            if (!IsDev) RevertStartup(startupWas, updated.StartWithWindows);
            var failed = result.Error!;
            return new(Loc.F("App.HotkeyFailedChooseAnother", failed.Message), failed.Binding);
        }
        updated.Hotkeys = hotkeys;

        return CopyUserSettings(updated) ? null : new(Loc.T("App.SettingsSaveFailed"));
    }

    /// <summary>Transfers settings window fields to current settings (doesn't touch overlay's last color/thickness) and saves.</summary>
    private bool CopyUserSettings(AppSettings updated)
    {
        // Originals were written to previous history folder: when setting disabled, offer to delete them from there.
        // Only if history was and remains enabled: disabling history itself doesn't touch files and offers nothing.
        // Storage is previous (previous folder); if history was disabled and enabled by same save — what
        // appears after ApplyFeatures.
        bool offerDeleteOriginals = Settings.KeepOriginals && !updated.KeepOriginals && updated.IsFeatureOn(FeatureCatalog.History);
        var originalsStore = offerDeleteOriginals ? History : null;
        Settings.DisabledFeatures = [.. updated.DisabledFeatures];
        Settings.Hotkeys = updated.Hotkeys;
        Settings.HistoryFolder = updated.HistoryFolder;
        Settings.HistoryLimit = updated.HistoryLimit;
        Settings.CopyOnCapture = updated.CopyOnCapture;
        Settings.StartWithWindows = updated.StartWithWindows;
        Settings.ExcludePinsFromCapture = updated.ExcludePinsFromCapture;
        Settings.KeepOriginals = updated.KeepOriginals;
        Settings.SnapEnabled = updated.SnapEnabled;
        Settings.CheckForUpdates = updated.CheckForUpdates;
        Settings.PreciseOcr = updated.PreciseOcr;
        Settings.Language = updated.Language;
        ApplyFeatures();
        Loc.Apply(Settings.Language);
        if (offerDeleteOriginals) originalsStore ??= History;
        _historyWindow?.Refresh();
        foreach (var pin in Pins) pin.SetExcludedFromCapture(Settings.ExcludePinsFromCapture);
        bool saved = SaveSettings();
        if (originalsStore is not null) OfferDeleteOriginals(originalsStore);
        return saved;
    }

    /// <summary>History storage per current settings; without History feature it's absent (files in folder untouched).</summary>
    private void UpdateHistoryStore()
        => History = Settings.IsFeatureOn(FeatureCatalog.History) ? new HistoryStore(Settings.HistoryFolder, Settings.HistoryLimit) : null;

    /// <summary>
    /// Features (<see cref="FeatureCatalog"/>) changed in settings or wizard: disabled disappears immediately — history window
    /// closes, storage removed, pinned screenshots close, tray items hide, open editors
    /// rebuild panel. Re-enabled simply works: history with old folder.
    /// </summary>
    private void ApplyFeatures()
    {
        if (!HistoryFeatureOn) _historyWindow?.Close(); // before storage becomes null
        UpdateHistoryStore();
        bool pins = Settings.ActionAllowed(CaptureAction.Pin);
        if (!pins) foreach (var pin in Pins.ToList()) pin.Close();
        if (_historyWindow is not null) _historyWindow.CanPin = pins;
        foreach (Window window in Windows)
            if (window is EditorWindow editor) editor.ApplyFeatures();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HistoryFeatureOn)));
    }

    /// <summary>
    /// Original storage setting disabled: already saved <c>.orig.png</c> files still contain what was
    /// hidden by blur. Ask whether to delete them; “Keep” touches nothing (setting still off).
    /// </summary>
    private async void OfferDeleteOriginals(HistoryStore store)
    {
        var originals = await _imageSaver.Queue.Enqueue(store.ListOriginals);
        if (IsShuttingDown) return;
        int count = originals.Count;
        if (count == 0) return;
        if (!Dialogs.Confirm(_settingsWindow, Loc.F("App.DeleteOriginalsTitle", count), Loc.T("App.DeleteOriginalsText"),
                Loc.T("Common.Delete"), danger: true, cancelLabel: Loc.T("App.Keep")))
            return;
        int deleted = await _imageSaver.Queue.Enqueue(() => store.DeleteOriginals(originals));
        if (deleted < count) Dialogs.Info(_settingsWindow, Loc.T("App.OriginalsPartlyDeletedTitle"), Loc.F("App.OriginalsPartlyDeletedText", deleted, count));
        _historyWindow?.Refresh();
    }

    private static void RevertStartup(bool was, bool attempted)
    {
        if (was == attempted) return;
        try { StartupRegistrar.Set(was); }
        catch (Exception ex) { Log.Error("Startup revert failed", ex); }
    }

    private void OnHotkeysChanged()
    {
        if (_tray is not null)
        {
            var names = HotkeyReport.ActiveNames;
            var version = CurrentVersion;
            // Tray hint limited to 127 chars: long list shortened same as in menu (and shorter by version length).
            _tray.ToolTipText = IsDev ? $"Screeny {version} (dev)"
                : names.Count == 0 ? Loc.F("Tray.TipClick", version)
                : Loc.F("Tray.TipHotkeys", version, HotkeyReport.FormatNames(names, maxLength: Math.Max(40, 89 - version.Length)));
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HotkeyText)));
    }

    /// <summary>
    /// Language changed in settings or wizard. XAML labels (tray menu, windows) updated themselves; here — font for
    /// CJK and tray hint built in code. Windows with code-built text subscribe to <see cref="Loc.Changed"/> themselves.
    /// </summary>
    private void OnLanguageChanged()
    {
        ApplyLanguageFonts();
        OnHotkeysChanged();
    }

    /// <summary>
    /// UI font with fallback for Japanese/Chinese: Segoe UI has no CJK glyphs, and without explicit font WPF can
    /// take another language's glyphs (Japanese glyphs as Chinese).
    /// </summary>
    private void ApplyLanguageFonts() => Resources["Font.UI"] = new System.Windows.Media.FontFamily(Loc.UiFontFamily);

    public void Notify(string message)
    {
        _updateBalloon = null; // click on this notification does not open update window
        _tray?.ShowNotification(IsDev ? DevTrayText : "Screeny", message);
    }

    private void OpenHistoryFolder()
    {
        if (!HistoryFeatureOn) return;
        try
        {
            Directory.CreateDirectory(Settings.HistoryFolder);
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(Settings.HistoryFolder);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Error("Open history folder failed", ex);
            Notify(Loc.F("App.OpenHistoryFolderFailed", ex.Message));
        }
    }

    /// <summary>Signal from second instance. Thread ends via _stopListening on exit; errors don't crash process.</summary>
    private void ListenForSecondInstance()
    {
        var capture = _captureEvent!;
        var thread = new Thread(() =>
        {
            try
            {
                var handles = new WaitHandle[] { capture, _stopListening };
                while (WaitHandle.WaitAny(handles) == 0)
                {
                    if (Dispatcher.HasShutdownStarted) return;
                    Dispatcher.BeginInvoke(new Action(StartCapture));
                }
            }
            catch (ObjectDisposedException) { /* exit */ }
            catch (Exception ex) { Log.Error("Second-instance listener failed", ex); }
        }) { IsBackground = true, Name = "Screeny.SecondInstanceListener" };
        thread.Start();
    }

    public void StartCapture()
    {
        if (_capturing || IsShuttingDown) return;
        // Test instance never captures screen: only picture from --overlay-test.
        if (IsDev && Args.OverlayTest is null) { Notify("Dev instance never captures the screen: start it with --overlay-test <png>."); return; }
        _capturing = true;
        try
        {
            Screenshot shot;
            List<WindowInfo> windows;
            OverlayDevOptions? dev = null;
            if (IsDev)
            {
                shot = DevShot.Load(Args.OverlayTest!);
                windows = new List<WindowInfo>();
                dev = new OverlayDevOptions(Args.Select, Args.Scenario, Args.OverlayTest);
            }
            else
            {
                shot = ScreenCapture.CaptureVirtualScreen();
                windows = WindowFinder.Snapshot(shot.VirtualRect, exclude: OwnWindowHandles());
            }
            var overlay = new OverlayWindow(shot, windows, Settings, dev, OcrRefiner);
            overlay.Completed += OnCaptureCompleted;
            overlay.DisplaysChanged += () => Notify(Loc.T("App.DisplaysChanged"));
            overlay.Closed += (_, _) => { _capturing = false; SaveSettings(); }; // last color and thickness
            overlay.Show();
        }
        catch (Exception ex)
        {
            _capturing = false;
            Log.Error("Capture failed", ex);
            Notify(Loc.T("App.CaptureFailed"));
        }
    }

    /// <summary>Our windows (pin, history, settings) must not be highlighted by auto-detect.</summary>
    private HashSet<IntPtr> OwnWindowHandles()
    {
        var set = new HashSet<IntPtr>();
        foreach (Window w in Windows)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero) set.Add(hwnd);
        }
        return set;
    }

    /// <summary>
    /// First screenshot to history (own error protection), then action in its own try: action error
    /// reported with reason, and screenshot is already not lost.
    /// </summary>
    private void OnCaptureCompleted(CaptureResult r)
    {
        if (!IsShuttingDown) TrackOperation(CompleteCaptureAsync(r));
    }

    private async Task CompleteCaptureAsync(CaptureResult r)
    {
        _capturing = false; // overlay already hidden; next hotkey must not depend on action
        bool inHistory = false;
        bool copy = r.Action == CaptureAction.CopyExplicit || (r.Action == CaptureAction.Copy && Settings.CopyOnEnter());
        int clipboardOperation = copy ? ReserveClipboardWrite() : 0;
        try
        {
            if (r.Action == CaptureAction.CopyText)
            {
                CopyRecognizedText(r.Text ?? ""); // text, not screenshot: doesn't enter history
                return;
            }
            var result = await SaveImageAsync(r.Image, r.AnnotationsJson, r.Original, null,
                r.Action == CaptureAction.Save ? r.SavePath : null);
            inHistory = result.InHistory;
            if (result.SaveError is not null) throw result.SaveError;
            switch (r.Action)
            {
                case CaptureAction.Copy or CaptureAction.CopyExplicit when copy && IsCurrentClipboardWrite(clipboardOperation):
                    CopyImage(r.Image, result.Png, inHistory);
                    break;
                case CaptureAction.Pin:
                    ShowPin(r.Image, r.ScreenRegion);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Post-capture action {r.Action} failed", ex);
            Notify(r.Action == CaptureAction.CopyText
                ? Loc.F("App.CopyTextFailed", ex.Message)
                : inHistory ? Loc.F("App.FailedSavedToHistory", Loc.F(FailureKey(r.Action), ex.Message)) : Loc.F(FailureKey(r.Action), ex.Message));
        }
    }

    private void TrackOperation(Task task)
    {
        _pendingOperations.Add(task);
        _ = ObserveOperationAsync(task);
    }

    private async Task ObserveOperationAsync(Task task)
    {
        try { await task; }
        catch (Exception ex) { Log.Error("Background operation failed", ex); }
        finally { _pendingOperations.Remove(task); }
    }

    private static string FailureKey(CaptureAction a) => a switch
    {
        CaptureAction.Save => "App.SaveFailed",
        CaptureAction.Pin => "App.PinFailed",
        _ => "App.CopyFailed",
    };

    private void CopyImage(BitmapSource image, byte[] png, bool inHistory)
    {
        // Busy clipboard is common: ClipboardService returns false, other errors propagate.
        if (!ClipboardService.CopyImage(image, png))
            Notify(Loc.T(inHistory ? "App.ClipboardBusyInHistory" : "App.ClipboardBusyNotCopied"));
    }

    /// <summary>Loading and encoding of copy happen in shared queue; clipboard write in UI thread.</summary>
    public Task<bool?> CopyImageInBackground(Func<BitmapSource> load)
    {
        if (IsShuttingDown) return Task.FromResult<bool?>(null);
        var task = CopyImageInBackgroundAsync(load, ReserveClipboardWrite());
        TrackOperation(task);
        return task;
    }

    private async Task<bool?> CopyImageInBackgroundAsync(Func<BitmapSource> load, int operation)
    {
        var encoded = await _imageSaver.Queue.Enqueue(() =>
        {
            var image = load();
            if (!image.IsFrozen) image.Freeze();
            return (Image: image, Png: ClipboardService.EncodePng(image));
        });
        return IsCurrentClipboardWrite(operation) ? ClipboardService.CopyImage(encoded.Image, encoded.Png) : null;
    }

    /// <summary>Text selected in overlay text mode (Ctrl+T) goes to clipboard with preview in notification.</summary>
    private void CopyRecognizedText(string text)
    {
        _clipboardOperation++;
        if (!ClipboardService.CopyText(text)) { Notify(Loc.T("App.ClipboardBusyText")); return; }
        var preview = text.ReplaceLineEndings(" ");
        Notify(Loc.F("App.TextCopied", preview.Length > 80 ? preview[..80] + "…" : preview));
    }

    private HistoryWindow? _historyWindow;

    /// <summary>One history window: repeated call updates and activates existing.</summary>
    public void ShowHistory()
    {
        if (History is null) return; // History feature disabled
        if (_historyWindow is null)
        {
            // Window closes before storage removed (ApplyFeatures), so History not null here.
            _historyWindow = new HistoryWindow(() => History!, PinAtCursor, path => OpenEditor(path), _imageSaver.Queue)
            {
                DevShowFirstCardActions = IsDev && Args.Scenario == "hover",
                CanPin = Settings.ActionAllowed(CaptureAction.Pin),
            };
            _historyWindow.Closed += (_, _) => _historyWindow = null;
        }
        _historyWindow.Refresh();
        _historyWindow.Show();
        if (_historyWindow.WindowState == WindowState.Minimized) _historyWindow.WindowState = WindowState.Normal;
        _historyWindow.Activate();
    }

    /// <summary>
    /// Re-editing window of history screenshot. Each action in it (“Copy”, “Save As…”,
    /// “Pin”) adds new entry; original unchanged. null — screenshot did not open (message already shown).
    /// </summary>
    public EditorWindow? OpenEditor(string pngPath)
    {
        if (History is null) return null; // re-editing is part of history
        EditSession session;
        try { session = EditSession.Load(pngPath); }
        catch (Exception ex)
        {
            Log.Error($"Editor open failed: {pngPath}", ex);
            Dialogs.Info(_historyWindow, Loc.T("App.OpenShotFailedTitle"), Loc.T("App.OpenShotFailedText"));
            return null;
        }
        var services = new EditorServices((image, json, original, path) => SaveImageAsync(image, json, null, original, path),
            PinAtCursor, ReserveClipboardWrite, IsCurrentClipboardWrite, TrackOperation, () => IsShuttingDown);
        var editor = new EditorWindow(session, Settings, services);
        editor.Closed += (_, _) => SaveSettings(); // last color, thickness, stroke/fill
        editor.Show();
        editor.Activate();
        return editor;
    }

    private async Task<ImageSaveResult> SaveImageAsync(BitmapSource image, string? json, BitmapSource? original, byte[]? originalPng, string? path)
    {
        var store = History; // folder settings may change before operation ends
        var result = await _imageSaver.SaveAsync(store, image, json, original, originalPng, path, DateTime.Now);
        if (result.HistoryError is { } error) Log.Error("History save failed", error);
        if (result.InHistory && ReferenceEquals(store, History)) _historyWindow?.Refresh();
        return result;
    }

    /// <summary>Pin image to center of monitor under cursor (for history screenshots).</summary>
    public void PinAtCursor(BitmapSource image)
    {
        User32.GetCursorPos(out var cursor);
        var info = new User32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<User32.MONITORINFO>() };
        User32.GetMonitorInfo(User32.MonitorFromPoint(cursor, User32.MONITOR_DEFAULTTONEAREST), ref info);
        var work = info.rcWork;
        int x = (work.Left + work.Right - image.PixelWidth) / 2, y = (work.Top + work.Bottom - image.PixelHeight) / 2;
        ShowPin(image, new PixelRect(Math.Max(work.Left, x), Math.Max(work.Top, y), image.PixelWidth, image.PixelHeight));
    }

    /// <summary>All open pinned screenshots close on exit.</summary>
    public List<PinWindow> Pins { get; } = new();

    public void ShowPin(BitmapSource image, PixelRect screenRegion)
    {
        if (!Settings.ActionAllowed(CaptureAction.Pin)) return;
        var pin = new PinWindow(image, screenRegion, Settings.ExcludePinsFromCapture);
        pin.Closed += (_, _) => Pins.Remove(pin);
        Pins.Add(pin);
        pin.Show();
    }

    public void SavePinnedImage(BitmapSource image, string path)
    {
        if (IsShuttingDown) return;
        TrackOperation(SavePinnedImageAsync(image, path));
    }

    private async Task SavePinnedImageAsync(BitmapSource image, string path)
    {
        try { await _imageSaver.Queue.Enqueue(() => { FileSaver.Save(image, path); return true; }); }
        catch (Exception ex) { Log.Error("Pin save failed", ex); Notify(Loc.F("App.SaveShotFailed", ex.Message)); }
    }

    private bool SaveSettings()
    {
        try { SettingsStore.Save(Settings); return true; }
        catch (Exception ex) { Log.Error("Settings save failed", ex); return false; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _stopListening.Set();
        ThemeManager.Shutdown();
        foreach (var pin in Pins.ToList()) pin.Close();
        _historyWindow?.Close();
        _settingsWindow?.Close();
        DisposeFeedback();
        DisposeUpdates();
        DisposePreciseOcr();
        _hotkeys?.Dispose(); // unregisters RegisterHotKey and low-level hook
        _tray?.Dispose();
        try { if (_ownsMutex) _mutex?.ReleaseMutex(); }
        catch (Exception ex) { Log.Error("Mutex release failed", ex); }
        _mutex?.Dispose();
        _captureEvent?.Dispose();
        base.OnExit(e);
    }
}

public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => execute();
}
