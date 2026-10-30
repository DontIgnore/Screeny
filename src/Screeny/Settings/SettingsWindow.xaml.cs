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
/// Окно настроек. Сохранение делегировано App: перерегистрация хоткеев может не удаться (занято) —
/// тогда показываем ошибку под записью и окно не закрываем. Ошибки проверки выводятся красным под своим полем.
/// </summary>
public partial class SettingsWindow : ChromeWindow
{
    public const int MinLimit = 1, MaxLimit = 500;

    private readonly Func<AppSettings, SettingsApplyError?> _apply;
    private readonly List<CustomRow> _customRows = new();

    /// <summary>Строка своего сочетания: поле, крестик и ошибка под полем. Enabled сохраняется как был в настройках.</summary>
    private sealed record CustomRow(FrameworkElement Root, HotkeyBox Box, TextBlock Error, bool Enabled);

    /// <param name="current">Текущие настройки (не изменяются окном).</param>
    /// <param name="hotkeys">Что сейчас реально работает: занятые записи и запасной хоткей показываются подсказками.</param>
    /// <param name="printScreenReserved">PrintScreen забирает системный захват экрана Windows.</param>
    /// <param name="apply">Применить и сохранить; возвращает ошибку или null.</param>
    /// <param name="preciseOcr">Модуль точного распознавания; null — блока нет.</param>
    /// <param name="semantic">Модуль смыслового поиска; null — блока нет.</param>
    /// <param name="semanticProgress">Сколько снимков проиндексировано для смыслового поиска из скольких.</param>
    public SettingsWindow(AppSettings current, HotkeyReport hotkeys, bool printScreenReserved, bool startupEnabled,
        Func<AppSettings, SettingsApplyError?> apply, Modules.IModuleHost? preciseOcr = null, Modules.IModuleHost? semantic = null,
        Func<(int Done, int Total)>? semanticProgress = null)
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
        if (semantic is not null)
        {
            _semantic = new SemanticPanel(semantic, semanticProgress ?? (() => (0, 0)));
            _features.AddBelow(FeatureCatalog.History, _semantic.Root);
        }
        _features.Changed += ShowFeatureFields;
        ShowFeatureFields();
        // Содержимое прокручивается, кнопки внизу всегда видны: окно не выше 780 DIP и рабочей области своего монитора
        // (ChromeWindow подгоняет его при открытии и при переходе на другой монитор).
        NaturalMaxHeight = 780;
        // Клиент обновлений App создаёт после окон из ключей запуска (--settings): версию и состояние — после загрузки окна.
        Loaded += (_, _) => InitAbout();
        Loc.Changed += OnLanguageChanged;
        Closed += (_, _) => Loc.Changed -= OnLanguageChanged;
    }

    /// <summary>
    /// Язык сменили, пока окно открыто (мастер, открытый отсюда): подписи из XAML обновились сами, здесь — собранные в коде.
    /// Уже показанные ошибки остаются на прежнем языке до следующей проверки.
    /// </summary>
    private void OnLanguageChanged()
    {
        LanguagePicker.Relabel(LanguageInput);
        ShowCurrentProblems(_report); // тексты проблем хоткеев собираются заново на новом языке
        _preciseOcr?.Refresh();
        _semantic?.Refresh();
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
    private readonly SemanticPanel? _semantic;

    /// <summary>Поменялся счёт проиндексированных снимков или состояние модуля смыслового поиска.</summary>
    public void RefreshSemantic() => _semantic?.Refresh();

    /// <summary>Тестовый запуск: прокрутить к блоку «Поиск по смыслу» и нарисовать окно в PNG — без снимка экрана.</summary>
    public void DevRenderSemantic(string path)
    {
        _semantic?.Root.BringIntoView();
        UpdateLayout();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(this);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>Модуль скачан, и App уже записал согласие в настройки: галочка «Использовать» — по ним.</summary>
    public void RefreshPreciseOcr(bool use) => _preciseOcr?.SetUse(use);

    /// <summary>
    /// Мастер, открытый отсюда («Проверить конфликты…»), применил свой выбор: хоткеи и «Проверять обновления» — заново из
    /// текущих настроек, функции — только нетронутые здесь (<see cref="FeatureCheckList.Sync"/>). Иначе «Сохранить» вернуло
    /// бы то, что было при открытии окна.
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

    /// <summary>Поля выключенной функции исчезают сразу (история — раздел, «Enter копирует»; закрепление — его галочка).</summary>
    private void ShowFeatureFields()
    {
        bool history = _features.IsOn(FeatureCatalog.History);
        HistorySection.Visibility = CopyInput.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        StartupInput.Margin = new Thickness(0, history ? 12 : 0, 0, 0); // первая галочка раздела — без отступа сверху
        ExcludePinsInput.Visibility = _features.IsOn(FeatureCatalog.Pins) ? Visibility.Visible : Visibility.Collapsed;
        if (_preciseOcr is not null) _preciseOcr.Root.Visibility = _features.IsOn(FeatureCatalog.Ocr) ? Visibility.Visible : Visibility.Collapsed;
        if (_semantic is not null)
        {
            _semantic.Root.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
            _semantic.SetOcrOn(_features.IsOn(FeatureCatalog.Ocr));
        }
    }

    /// <summary>Записи, которые сейчас не работают (заняты, хук не поставился), и запасной хоткей — подсказками.</summary>
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

    /// <summary>«Как отключить»: страница Windows с настройкой «Использовать клавишу Print Screen для захвата экрана».</summary>
    private void OnOpenKeyboardSettings(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-keyboard") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Error("Open keyboard settings failed", ex);
            ShowError(PrintScreenError, Loc.T("Settings.OpenSettingsFailed"));
        }
    }

    /// <summary>Мастер конфликтов с другими программами снимков (тот же, что при первом запуске).</summary>
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
        // Скрытые поля истории не проверяются (ошибку под ними не увидеть) и сохраняются как были — KeepHiddenSettings.
        bool history = _features.IsOn(FeatureCatalog.History);
        var folder = FolderInput.Text.Trim();
        int limit = _current.HistoryLimit;
        if (history && (folder.Length == 0 || !Path.IsPathFullyQualified(folder))) { ShowError(FolderError, Loc.T("Settings.FolderRequired")); return; }
        if (history && (!int.TryParse(LimitInput.Text.Trim(), out limit) || limit < MinLimit || limit > MaxLimit))
        { ShowError(LimitError, Loc.F("Settings.LimitInvalid", MinLimit, MaxLimit)); return; }

        // Только поля, которые правит окно: App переносит их в текущий объект настроек,
        // остальные (LastColor, LastThickness) остаются как есть.
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
            DisabledFeatures = FeatureGate.MergeDisabled(_current.DisabledFeatures, _features.Changes), // _current — живые настройки
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
    /// Список хоткеев из окна или null, если есть ошибка (показана под полем). Пустые строки своих сочетаний
    /// пропускаются; недопустимые (<see cref="HotkeyRules"/>), повторы и PrintScreen отклоняются; хотя бы один хоткей
    /// должен быть включён.
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

    /// <summary>Куда выводить ошибку записи: под галочкой или под строкой своего сочетания.</summary>
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
