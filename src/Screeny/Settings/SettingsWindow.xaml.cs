using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using Screeny.Features;
using Screeny.Hotkeys;
using Screeny.Localization;
using Screeny.Logging;
using Screeny.Themes;

namespace Screeny.Settings;

/// <summary>
/// Settings window. Saving delegated to App: hotkey re-registration may fail (busy) —
/// then show error under entry and keep window open. Validation errors red under own field.
/// </summary>
public partial class SettingsWindow : ChromeWindow
{
    public const int MinLimit = 1, MaxLimit = 500;

    private readonly Func<AppSettings, SettingsApplyError?> _apply;
    private readonly List<CustomRow> _customRows = new();

    /// <summary>Custom combo row: field, X and error under field. Enabled saved as in settings.</summary>
    private sealed record CustomRow(FrameworkElement Root, HotkeyBox Box, TextBlock Error, bool Enabled);

    /// <param name="current">Current settings (not modified by window).</param>
    /// <param name="hotkeys">What actually works now: busy entries and fallback hotkey shown as tooltips.</param>
    /// <param name="printScreenReserved">PrintScreen taken by Windows system screen capture.</param>
    /// <param name="apply">Apply and save; returns error or null.</param>
    /// <param name="preciseOcr">Precise recognition module; null — no block.</param>
    public SettingsWindow(AppSettings current, HotkeyReport hotkeys, bool printScreenReserved, bool startupEnabled,
        Func<AppSettings, SettingsApplyError?> apply, Modules.IPreciseOcrHost? preciseOcr = null)
    {
        InitializeComponent();
        _apply = apply;

        LanguagePicker.Fill(LanguageInput, current.Language);
        ShowHotkeys(current, hotkeys);
        PrintScreenWarning.Visibility = printScreenReserved ? Visibility.Visible : Visibility.Collapsed;

        FolderInput.Text = current.HistoryFolder;
        LimitInput.Text = current.HistoryLimit.ToString();
        CopyInput.IsChecked = current.CopyOnCapture;
        StartupInput.IsChecked = startupEnabled;
        ExcludePinsInput.IsChecked = current.ExcludePinsFromCapture;
        KeepOriginalsInput.IsChecked = current.KeepOriginals;
        SnapInput.IsChecked = current.SnapEnabled;
        UpdatesInput.IsChecked = current.CheckForUpdates;

        _current = current;
        _features = new FeatureCheckList(FeaturesPanel, current.DisabledFeatures);
        if (preciseOcr is not null)
        {
            _preciseOcr = new PreciseOcrPanel(preciseOcr, current.PreciseOcr);
            _features.AddBelow(FeatureCatalog.Ocr, _preciseOcr.Root);
        }
        _features.Changed += ShowFeatureFields;
        ShowFeatureFields();
        // Content scrolls, bottom buttons always visible: window not taller than 780 DIP and work area of its monitor
        // (ChromeWindow fits it on open and monitor change).
        NaturalMaxHeight = 780;
        // Update client App creates after windows from startup keys (--settings): version and state after window load.
        Loaded += (_, _) => InitAbout();
        Loc.Changed += OnLanguageChanged;
        Closed += (_, _) => Loc.Changed -= OnLanguageChanged;
    }

    /// <summary>
    /// Language changed while window open (wizard opened from here): XAML labels updated, here — code-built.
    /// Already shown errors stay in old language until next validation.
    /// </summary>
    private void OnLanguageChanged()
    {
        LanguagePicker.Relabel(LanguageInput);
        ShowCurrentProblems(_report); // hotkey problem texts rebuilt in new language
        _preciseOcr?.Refresh();
        ShowAboutStatus();
    }

    private HotkeySpec? _fallback;
    private HotkeyReport _report = HotkeyReport.Empty;

    private void ShowFallbackHint()
    {
        HotkeyHint.Visibility = _fallback is null ? Visibility.Collapsed : Visibility.Visible;
        if (_fallback is { } fb) HotkeyHint.Text = Loc.F("Settings.FallbackActive", fb);
    }

    private readonly AppSettings _current;
    private readonly FeatureCheckList _features;
    private readonly PreciseOcrPanel? _preciseOcr;

    /// <summary>Module downloaded and App already wrote consent to settings: “Use” checkbox per them.</summary>
    public void RefreshPreciseOcr(bool use) => _preciseOcr?.SetUse(use);

    /// <summary>
    /// Wizard opened from here (“Check conflicts…”) applied its choice: hotkeys and “Check for updates” — from
    /// current settings again, features — only untouched here (<see cref="FeatureCheckList.Sync"/>). Else “Save” would return
    /// what was at window open.
    /// </summary>
    public void RefreshFromLiveSettings(HotkeyReport hotkeys)
    {
        ShowHotkeys(_current, hotkeys);
        LanguagePicker.Select(LanguageInput, _current.Language);
        UpdatesInput.IsChecked = _current.CheckForUpdates;
        _features.Sync(_current.DisabledFeatures);
        ShowFeatureFields();
    }

    private void ShowHotkeys(AppSettings current, HotkeyReport hotkeys)
    {
        foreach (var block in new[] { PrintScreenError, WinShiftSError, HotkeysError }) block.Visibility = Visibility.Collapsed;
        _fallback = null;
        HotkeyHint.Visibility = Visibility.Collapsed;
        _customRows.Clear();
        CustomRows.Children.Clear();
        PrintScreenInput.IsChecked = current.IsHotkeyEnabled(HotkeyKind.PrintScreen);
        WinShiftSInput.IsChecked = current.IsHotkeyEnabled(HotkeyKind.WinShiftS);
        foreach (var b in current.Hotkeys.Where(b => b.Kind == HotkeyKind.Custom))
            AddCustomRow(HotkeyParser.TryParse(b.Spec ?? "", out var spec) ? spec : null, b.Enabled);
        ShowCurrentProblems(hotkeys);
    }

    /// <summary>Disabled feature fields disappear immediately (history — section, “Enter copies”; pin — its checkbox).</summary>
    private void ShowFeatureFields()
    {
        bool history = _features.IsOn(FeatureCatalog.History);
        HistorySection.Visibility = CopyInput.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        StartupInput.Margin = new Thickness(0, history ? 12 : 0, 0, 0); // first checkbox of section — no top margin
        ExcludePinsInput.Visibility = _features.IsOn(FeatureCatalog.Pins) ? Visibility.Visible : Visibility.Collapsed;
        if (_preciseOcr is not null) _preciseOcr.Root.Visibility = _features.IsOn(FeatureCatalog.Ocr) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Entries not working now (busy, hook failed) and fallback hotkey — tooltips.</summary>
    private void ShowCurrentProblems(HotkeyReport report)
    {
        _report = report;
        foreach (var state in report.Entries)
        {
            if (state.Status is not (HotkeyStatus.Busy or HotkeyStatus.Failed) || state.Message is null) continue;
            if (ErrorTargetFor(state.Binding) is { } target) ShowError(target, state.Message);
        }
        _fallback = report.Fallback;
        ShowFallbackHint();
    }

    private CustomRow AddCustomRow(HotkeySpec? spec, bool enabled = true)
    {
        var box = new HotkeyBox { Spec = spec };
        var remove = new Button { Content = "\uE711", Style = (Style)FindResource("IconButton"), Margin = new Thickness(4, 0, 0, 0) };
        Loc.Bind(box, ToolTipProperty, "Hotkeys.BoxTip");
        Loc.Bind(remove, ToolTipProperty, "Settings.RemoveShortcut");
        var error = new TextBlock { Style = (Style)FindResource("ErrorText") };
        var line = new DockPanel();
        DockPanel.SetDock(remove, Dock.Right);
        line.Children.Add(remove);
        line.Children.Add(box);
        var root = new StackPanel { Margin = new Thickness(0, _customRows.Count == 0 ? 0 : 8, 0, 0) };
        root.Children.Add(line);
        root.Children.Add(error);
        var row = new CustomRow(root, box, error, enabled);
        remove.Click += (_, _) => RemoveCustomRow(row);
        Loc.Bind(remove, AutomationProperties.NameProperty, "Settings.RemoveShortcut");
        Loc.Bind(box, AutomationProperties.NameProperty, "Hotkeys.CustomName");
        _customRows.Add(row);
        CustomRows.Children.Add(root);
        return row;
    }

    private void RemoveCustomRow(CustomRow row)
    {
        _customRows.Remove(row);
        CustomRows.Children.Remove(row.Root);
        if (_customRows.Count > 0) _customRows[0].Root.Margin = new Thickness(0);
        AddCustomButton.Focus();
    }

    private void OnAddCustom(object sender, RoutedEventArgs e)
    {
        var row = AddCustomRow(null);
        row.Box.Focus();
    }

    /// <summary>“How to disable”: Windows page with “Use Print Screen key to open screen capture” setting.</summary>
    private void OnOpenKeyboardSettings(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-keyboard") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Error("Open keyboard settings failed", ex);
            ShowError(PrintScreenError, Loc.T("Settings.OpenSettingsFailed"));
        }
    }

    /// <summary>Conflict wizard with other screenshot programs (same as first run).</summary>
    private void OnCheckConflicts(object sender, RoutedEventArgs e) => (Application.Current as App)?.ShowSetupWizard();

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Settings.HistoryFolderDialog") };
        if (Directory.Exists(FolderInput.Text)) dialog.InitialDirectory = FolderInput.Text;
        if (dialog.ShowDialog(this) == true) FolderInput.Text = dialog.FolderName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ClearErrors();
        if (CollectHotkeys() is not { } hotkeys) return;
        // Hidden history fields not validated (error under them invisible) and saved as were — KeepHiddenSettings.
        bool history = _features.IsOn(FeatureCatalog.History);
        var folder = FolderInput.Text.Trim();
        int limit = _current.HistoryLimit;
        if (history && (folder.Length == 0 || !Path.IsPathFullyQualified(folder))) { ShowError(FolderError, Loc.T("Settings.FolderRequired")); return; }
        if (history && (!int.TryParse(LimitInput.Text.Trim(), out limit) || limit < MinLimit || limit > MaxLimit))
        { ShowError(LimitError, Loc.F("Settings.LimitInvalid", MinLimit, MaxLimit)); return; }

        // Only fields window edits: App transfers to current settings object,
        // others (LastColor, LastThickness) stay.
        var updated = new AppSettings
        {
            Hotkeys = hotkeys,
            HistoryFolder = folder,
            HistoryLimit = limit,
            CopyOnCapture = CopyInput.IsChecked == true,
            StartWithWindows = StartupInput.IsChecked == true,
            ExcludePinsFromCapture = ExcludePinsInput.IsChecked == true,
            KeepOriginals = KeepOriginalsInput.IsChecked == true,
            SnapEnabled = SnapInput.IsChecked == true,
            CheckForUpdates = UpdatesInput.IsChecked == true,
            Language = LanguagePicker.Selected(LanguageInput),
            PreciseOcr = _preciseOcr?.Use ?? _current.PreciseOcr,
            DisabledFeatures = FeatureGate.MergeDisabled(_current.DisabledFeatures, _features.Changes), // _current — live settings
        };
        FeatureGate.KeepHiddenSettings(updated, _current);
        if (_apply(updated) is { } error)
        {
            ShowError(error.Hotkey is { } b ? ErrorTargetFor(b) ?? GeneralError : GeneralError, error.Message);
            return;
        }
        Close();
    }

    /// <summary>
    /// Hotkey list from window or null on error (shown under field). Empty custom strings skipped; invalid (<see cref="HotkeyRules"/>), duplicates and PrintScreen rejected; at least one hotkey must be enabled.
    ///
    ///
    /// </summary>
    private List<HotkeyBinding>? CollectHotkeys()
    {
        var list = new List<HotkeyBinding>
        {
            HotkeyBinding.PrintScreen(PrintScreenInput.IsChecked == true),
            HotkeyBinding.WinShiftS(WinShiftSInput.IsChecked == true),
        };
        var seen = new HashSet<HotkeySpec>();
        bool ok = true;
        foreach (var row in _customRows)
        {
            if (row.Box.Spec is not { } spec) continue;
            string? error = spec == HotkeySpec.PrintScreen ? Loc.T("Hotkeys.PrintScreenUseBox")
                : HotkeyRules.Validate(spec) ?? (seen.Add(spec) ? null : Loc.T("Settings.DuplicateShortcut"));
            if (error is not null) { ShowError(row.Error, error); ok = false; continue; }
            list.Add(HotkeyBinding.Custom(spec.ToString(), row.Enabled));
        }
        if (!ok) return null;
        if (!list.Any(b => b.Enabled))
        {
            ShowError(HotkeysError, Loc.T("Hotkeys.NeedOne"));
            return null;
        }
        return list;
    }

    /// <summary>Where to show entry error: under checkbox or under custom combo row.</summary>
    private TextBlock? ErrorTargetFor(HotkeyBinding binding) => binding.Kind switch
    {
        HotkeyKind.PrintScreen => PrintScreenError,
        HotkeyKind.WinShiftS => WinShiftSError,
        _ => _customRows.FirstOrDefault(r => r.Box.Spec is { } s && s == binding.ToRegisterSpec())?.Error,
    };

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void ShowError(TextBlock target, string message)
    {
        target.Text = message;
        target.Visibility = Visibility.Visible;
    }

    private void ClearErrors()
    {
        foreach (var block in new[] { PrintScreenError, WinShiftSError, HotkeysError, FolderError, LimitError, GeneralError })
            block.Visibility = Visibility.Collapsed;
        foreach (var row in _customRows) row.Error.Visibility = Visibility.Collapsed;
    }
}
