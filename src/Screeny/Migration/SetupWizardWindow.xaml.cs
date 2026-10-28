using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Features;
using Screeny.Hotkeys;
using Screeny.Localization;
using Screeny.Logging;
using Screeny.Settings;
using Screeny.Themes;

namespace Screeny.Migration;

/// <summary>Что мастеру нужно от приложения.</summary>
/// <param name="Hotkeys">Текущий список хоткеев (из настроек).</param>
/// <param name="CheckForUpdates">Текущее значение галочки обновлений.</param>
/// <param name="DisabledFeatures">Выключенные функции (<see cref="AppSettings.DisabledFeatures"/>): их галочки сняты.</param>
/// <param name="Language">Текущая настройка языка (<see cref="AppSettings.Language"/>). Выбор в мастере сразу меняет язык
/// интерфейса для просмотра, а сохраняется вместе с остальным выбором; простой «Пропустить» возвращает прежний.</param>
/// <param name="ApplyHotkeys">Применить и сохранить хоткеи (<see cref="App.ApplyHotkeys"/>).</param>
/// <param name="Finish">
/// Мастер пройден: true/false — сохранить «Проверять обновления», null — «Пропустить»; второй аргумент — id показанных
/// конфликтов; третий — переключённые в мастере функции (id → включена) или null («Пропустить» их не меняет);
/// четвёртый — выбранный язык или null («Пропустить»: вернуть сохранённый).
/// «Пропустить» (и крестик) после «Применить», на котором не заработал хоткей, передаёт выбранные функции и галочку
/// обновлений, как «Применить»: их пользователь уже подтвердил, а хоткеи остаются прежними.
/// </param>
/// <param name="DownloadPreciseOcr">Скачать модуль точного распознавания в фоне (итог — уведомлением в трее); null — модуль
/// уже стоит, галочки «Скачать точное распознавание» нет.</param>
public sealed record SetupWizardHost(
    IReadOnlyList<HotkeyBinding> Hotkeys,
    bool CheckForUpdates,
    IReadOnlyList<string> DisabledFeatures,
    string Language,
    MigrationServices.Services Services,
    ConflictCatalog Catalog,
    Func<IEnumerable<HotkeyBinding>, HotkeyApplyResult> ApplyHotkeys,
    Action<bool?, IReadOnlyList<string>, IReadOnlyList<KeyValuePair<string, bool>>?, string?> Finish,
    Action? DownloadPreciseOcr = null);

/// <summary>
/// Мастер первого запуска: один экран. Если найдены мешающие программы — карточки с действиями («Закрыть сейчас»,
/// «Убрать из автозапуска», «Удалить программу…», для записей всех пользователей — «Отключить с правами администратора»)
/// и системный захват PrintScreen; затем выбор хоткеев, функций (<see cref="FeatureCatalog"/>: у Velopack-установщика нет
/// экрана компонентов, поэтому модули выбираются здесь, сразу после установки) и «Проверять обновления». Без согласия ничего не меняется:
/// действия выполняются по «Применить» (или по своей кнопке), результат каждого виден в карточке.
/// </summary>
public partial class SetupWizardWindow : ChromeWindow
{
    private readonly SetupWizardHost _host;
    private readonly IConflictActions _actions;
    private readonly List<Card> _cards = new();
    /// <summary>Результаты действий по id программы: переживают пересканирование.</summary>
    private readonly Dictionary<string, List<ActionResult>> _results = new();
    /// <summary>Путь exe для иконки — и после того, как программа перестала мешать.</summary>
    private readonly Dictionary<string, string> _exePaths = new();
    private IReadOnlyList<Conflict> _conflicts = [];
    private bool _systemPrintScreen;
    private CheckBox? _systemPrintScreenInput;
    private ActionResult? _systemPrintScreenResult;
    private bool _printScreenChangedNow;
    private bool _finished;
    private bool _busy;
    private readonly FeatureCheckList _features;

    private sealed record Card(Conflict Conflict, CheckBox? Close, CheckBox? Autostart);

    public SetupWizardWindow(SetupWizardHost host, IEnvironmentSnapshot initial)
    {
        InitializeComponent();
        // Нижняя панель с «Применить» всегда на экране: не выше 820 DIP и рабочей области своего монитора (ChromeWindow).
        NaturalMaxHeight = 820;
        _host = host;
        _actions = host.Services.Actions;
        PrintScreenInput.IsChecked = host.Hotkeys.Any(h => h.Kind == HotkeyKind.PrintScreen && h.Enabled);
        WinShiftSInput.IsChecked = host.Hotkeys.Any(h => h.Kind == HotkeyKind.WinShiftS && h.Enabled);
        UpdatesInput.IsChecked = host.CheckForUpdates;
        _features = new FeatureCheckList(FeaturesPanel, host.DisabledFeatures);
        AddPreciseOcrChoice();
        LanguagePicker.Fill(LanguageInput, host.Language);
        LanguageInput.SelectionChanged += OnLanguagePicked;
        Loc.Changed += OnLanguageChanged;
        ShowTitle();
        Present(initial);
    }

    private bool _applyingLanguage;

    /// <summary>
    /// Выбор в списке сразу показывает язык (сохраняется он вместе с остальным выбором). Повторный вход — из
    /// обработчика <see cref="Loc.Changed"/>, пока язык уже применяется, — и пустой выбор ничего не делают.
    /// </summary>
    private void OnLanguagePicked(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingLanguage || _finished || LanguagePicker.SelectedOrNull(LanguageInput) is not { } language) return;
        _applyingLanguage = true;
        try { Loc.Apply(language); }
        finally { _applyingLanguage = false; }
    }

    private void ShowTitle()
    {
        if (_host.Services.IsFake) Title = Loc.F("Wizard.FakeSuffix", Loc.T("Wizard.Title"));
    }

    /// <summary>
    /// Язык сменили (здесь или в настройках): подписи из XAML обновились сами, собранное в коде — заголовок, карточки
    /// (с выбором в них) и галочка модуля — строится заново. Уже показанные итоги проверки хоткеев остаются как были.
    /// </summary>
    private void OnLanguageChanged()
    {
        LanguagePicker.Relabel(LanguageInput);
        ShowTitle();
        ShowHeading();
        RebuildCards(keepChoices: true);
        if (_preciseOcrInput is not null) _preciseOcrInput.Content = Loc.F("Wizard.DownloadPreciseOcr", Modules.OcrModule.SizeText);
        if (_appliedOnce) ApplyButton.Content = Loc.T("Wizard.Done");
    }

    /// <summary>Конфликты, найденные при последнем сканировании (для тестов и App).</summary>
    public IReadOnlyList<Conflict> Conflicts => _conflicts;

    private void Present(IEnvironmentSnapshot env)
    {
        _conflicts = ConflictDetector.Detect(env, _host.Catalog);
        _systemPrintScreen = ConflictDetector.IsSystemPrintScreenOn(env);
        foreach (var c in _conflicts)
        {
            if (c.ExePath is { } exe) _exePaths[c.Id] = exe;
        }
        ShowHeading();
        RebuildCards();
    }

    private void ShowHeading()
    {
        bool any = _conflicts.Count > 0 || _systemPrintScreen;
        HeadingText.Text = Loc.T(any ? "Wizard.HeadingConflicts" : "Wizard.HeadingReady");
        IntroText.Text = Loc.T(any ? "Wizard.IntroConflicts" : "Wizard.IntroReady");
    }

    /// <summary>Галочки карточек на время перестройки при смене языка (id программы → «Закрыть», «Убрать из автозапуска»).</summary>
    private Dictionary<string, (bool? Close, bool? Autostart)>? _keptChoices;

    private void RebuildCards(bool keepChoices = false)
    {
        _keptChoices = keepChoices ? _cards.ToDictionary(c => c.Conflict.Id, c => (c.Close?.IsChecked, c.Autostart?.IsChecked)) : null;
        ConflictPanel.Children.Clear();
        _cards.Clear();
        var hotkeys = CurrentChoiceHotkeys();
        foreach (var conflict in _conflicts) ConflictPanel.Children.Add(BuildCard(conflict, hotkeys));
        // Программы, которые после действий больше не мешают, остаются с результатом.
        foreach (var (id, results) in _results)
        {
            if (_conflicts.Any(c => c.Id == id) || _host.Catalog.Find(id) is not { } program) continue;
            ConflictPanel.Children.Add(BuildResolvedCard(program, results));
        }
        if (_systemPrintScreen || _systemPrintScreenResult is not null) ConflictPanel.Children.Add(BuildSystemPrintScreenCard());
        _keptChoices = null;
        ConflictPanel.Visibility = ConflictPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<HotkeyBinding> CurrentChoiceHotkeys()
        => [HotkeyBinding.PrintScreen(PrintScreenInput.IsChecked == true), HotkeyBinding.WinShiftS(WinShiftSInput.IsChecked == true)];

    private Border BuildCard(Conflict conflict, List<HotkeyBinding> chosen)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = conflict.Program.DisplayName, Style = Res<Style>("Body"), FontWeight = FontWeights.SemiBold });
        body.Children.Add(Caption(DescribeFound(conflict)));
        // Клавиши сравниваются в записи каталога («PrintScreen, Ctrl+PrintScreen»), показываются с разделителем языка.
        var names = conflict.Keys.Select(KeyName).ToList();
        bool sameAsUsual = !conflict.Program.DetectByRemapOnly && string.Join(", ", names) == conflict.Program.KeysText;
        var keys = string.Join(Loc.ListSeparator, names);
        var usual = conflict.Program.DetectByRemapOnly ? Loc.T("Wizard.PowerToysKeys")
            : conflict.Program.KeysText.Replace(", ", Loc.ListSeparator, StringComparison.Ordinal);
        body.Children.Add(Caption(conflict.Keys.Count > 0
            ? sameAsUsual ? Loc.F("Wizard.Blocks", keys) : Loc.F("Wizard.BlocksUsually", keys, usual)
            : Loc.F("Wizard.UsuallyTakes", usual)));

        bool blocks = conflict.Blocks(chosen);
        var kept = _keptChoices?.GetValueOrDefault(conflict.Id);
        CheckBox? close = null, autostart = null;
        var buttons = new WrapPanel();
        if (conflict.IsRunning)
        {
            // Завершение процесса — только по явной галочке пользователя; отключение автозапуска обратимо и отмечено заранее.
            close = new CheckBox { Content = Loc.T("Wizard.CloseNow"), IsChecked = kept?.Close ?? false, Margin = new Thickness(0, 10, 0, 0) };
            close.ToolTip = Loc.T("Wizard.CloseNowTip");
            body.Children.Add(close);
        }
        if (conflict.UserAutostarts.Any())
        {
            autostart = new CheckBox { Content = Loc.T("Wizard.RemoveAutostart"), IsChecked = kept?.Autostart ?? blocks, Margin = new Thickness(0, 8, 0, 0) };
            autostart.ToolTip = Loc.T("Wizard.RemoveAutostartTip");
            body.Children.Add(autostart);
        }
        if (conflict.NeedsAdminForAutostart)
        {
            body.Children.Add(Caption(Loc.T("Wizard.MachineAutostart"), top: 10));
            var elevate = IconButton("\uEA18", Loc.T("Wizard.DisableAsAdmin"));
            elevate.Click += async (_, _) => await RunElevated(conflict);
            buttons.Children.Add(elevate);
        }
        if (conflict.Program.DetectByRemapOnly)
            body.Children.Add(Caption(Loc.T("Wizard.PowerToysHint"), top: 10));
        if (conflict.Uninstaller is not null)
        {
            var uninstall = new Button { Content = Loc.T("Wizard.Uninstall"), HorizontalAlignment = HorizontalAlignment.Left };
            uninstall.ToolTip = Loc.T("Wizard.UninstallTip");
            uninstall.Click += (_, _) => Uninstall(conflict);
            buttons.Children.Add(uninstall);
        }
        foreach (FrameworkElement b in buttons.Children) b.Margin = new Thickness(0, 10, 8, 0);
        if (buttons.Children.Count > 0) body.Children.Add(buttons);
        AddResults(body, conflict.Id);
        _cards.Add(new Card(conflict, close, autostart));
        return CardShell(conflict.ExePath, conflict.Program.DisplayName, body);
    }

    private Border BuildResolvedCard(ConflictProgram program, List<ActionResult> results)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = program.DisplayName, Style = Res<Style>("Body"), FontWeight = FontWeights.SemiBold });
        body.Children.Add(Caption(Loc.T("Wizard.Resolved")));
        AddResults(body, program.Id);
        return CardShell(_exePaths.GetValueOrDefault(program.Id), program.DisplayName, body);
    }

    private Border BuildSystemPrintScreenCard()
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = Loc.T("Wizard.WindowsCapture"), Style = Res<Style>("Body"), FontWeight = FontWeights.SemiBold });
        if (_systemPrintScreen)
        {
            body.Children.Add(Caption(Loc.T("Wizard.SystemPrintScreen")));
            _systemPrintScreenInput = new CheckBox
            {
                Content = Loc.T("Wizard.GivePrintScreen"), Margin = new Thickness(0, 10, 0, 0),
                IsChecked = _systemPrintScreenInput?.IsChecked ?? PrintScreenInput.IsChecked == true,
                ToolTip = Loc.T("Wizard.GivePrintScreenTip"),
            };
            body.Children.Add(_systemPrintScreenInput);
            body.Children.Add(Caption(Loc.T("Wizard.SignOutNote"), top: 4));
        }
        else
        {
            _systemPrintScreenInput = null;
        }
        if (_systemPrintScreenResult is { } r) body.Children.Add(ResultText(r));
        return CardShell(null, "Windows", body, glyph: "\uE7F4");
    }

    private void AddResults(Panel body, string id)
    {
        if (!_results.TryGetValue(id, out var list)) return;
        foreach (var r in list) body.Children.Add(ResultText(r));
    }

    private TextBlock ResultText(ActionResult r)
    {
        var text = new TextBlock { Text = (r.Success ? "✓ " : "") + r.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        text.Style = Res<Style>("Caption");
        text.SetResourceReference(TextBlock.ForegroundProperty, r.Outcome switch
        {
            ActionOutcome.Done => "Brush.Ink",
            ActionOutcome.Failed => "Brush.DangerText",
            _ => "Brush.Ink70",
        });
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        return text;
    }

    private static string DescribeFound(Conflict c)
    {
        var parts = new List<string>();
        if (c.IsRunning) parts.Add(Loc.T("Wizard.FoundRunning"));
        foreach (var location in c.Autostarts.Select(a => a.LocationText).Distinct()) parts.Add(location);
        if (c.Program.DetectByRemapOnly) parts.Add(Loc.T("Wizard.FoundRemap"));
        if (c.Uninstaller is null && !c.Program.DetectByRemapOnly) parts.Add(Loc.T("Wizard.FoundNoInstaller"));
        var text = string.Join(Loc.ListSeparator, parts);
        return text.Length == 0 ? "" : Loc.F("Wizard.Found", text);
    }

    private static string KeyName(HotkeyKind kind) => kind switch
    {
        HotkeyKind.PrintScreen => "PrintScreen",
        HotkeyKind.WinShiftS => "Win+Shift+S",
        _ => kind.ToString(),
    };

    private Border CardShell(string? exePath, string name, UIElement body, string? glyph = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = ProgramIcon(exePath, name, glyph);
        grid.Children.Add(icon);
        Grid.SetColumn((UIElement)body, 1);
        grid.Children.Add(body);
        var border = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 8), Child = grid };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Stroke");
        border.BorderThickness = new Thickness(1);
        AutomationProperties.SetName(border, name);
        return border;
    }

    /// <summary>Иконка из exe (если файл есть) или первая буква / символ в круге.</summary>
    private FrameworkElement ProgramIcon(string? exePath, string name, string? glyph)
    {
        if (exePath is not null && ExeIcon.Load(exePath) is { } image)
            return new Image { Source = image, Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        var letter = new TextBlock
        {
            Text = glyph ?? name[..1].ToUpperInvariant(), FontSize = glyph is null ? 15 : 16, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        if (glyph is not null) letter.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        letter.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Ink70");
        var circle = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Child = letter, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        circle.SetResourceReference(Border.BackgroundProperty, "Brush.Ink10");
        return circle;
    }

    private TextBlock Caption(string text, double top = 2)
        => new() { Text = text, Style = Res<Style>("Caption"), TextWrapping = TextWrapping.Wrap, LineHeight = 17, Margin = new Thickness(0, top, 0, 0) };

    private Button IconButton(string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock { Text = glyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = panel, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(button, text);
        return button;
    }

    private T Res<T>(string key) => (T)FindResource(key);

    private void Record(string id, ActionResult result)
    {
        if (!_results.TryGetValue(id, out var list)) _results[id] = list = new List<ActionResult>();
        list.Add(result);
    }

    private async Task Rescan()
    {
        try
        {
            var env = await Task.Run(_host.Services.Capture);
            Present(env);
        }
        catch (Exception ex)
        {
            Log.Error("Setup wizard rescan failed", ex);
            GeneralError.Text = Loc.T("Wizard.RescanFailed");
            GeneralError.Visibility = Visibility.Visible;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ApplyButton.IsEnabled = SkipButton.IsEnabled = RescanButton.IsEnabled = ConflictPanel.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private async void OnRescan(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        GeneralError.Visibility = Visibility.Collapsed;
        await Rescan();
        SetBusy(false);
    }

    private async Task RunElevated(Conflict conflict)
    {
        if (_busy) return;
        SetBusy(true);
        var result = await _actions.DisableAutostartElevatedAsync(conflict);
        await Rescan();
        // Код выхода повышенного экземпляра — не доказательство: верим только пересканированию.
        if (result.Success && _conflicts.FirstOrDefault(c => c.Id == conflict.Id) is { NeedsAdminForAutostart: true })
            result = new ActionResult(ActionOutcome.Failed, Loc.F("Wizard.StillMachineAutostart", conflict.Program.DisplayName));
        Record(conflict.Id, result);
        RebuildCards();
        SetBusy(false);
    }

    private void Uninstall(Conflict conflict)
    {
        if (_busy) return;
        if (!Dialogs.Confirm(this, Loc.F("Wizard.UninstallTitle", conflict.Program.DisplayName), Loc.T("Wizard.UninstallText"),
                Loc.T("Wizard.OpenUninstaller")))
            return;
        Record(conflict.Id, _actions.LaunchUninstaller(conflict));
        RebuildCards();
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        FinishSkipped();
        Close();
    }

    /// <summary>Закрытие крестиком — как «Пропустить»: мастер не появится снова при следующем запуске.</summary>
    protected override void OnClosed(EventArgs e)
    {
        Loc.Changed -= OnLanguageChanged;
        if (!_finished) FinishSkipped();
        base.OnClosed(e);
    }

    /// <summary>
    /// «Пропустить» ничего не меняет, кроме случая, когда «Применить» уже нажимали, но хоткей не заработал: тогда функции
    /// и обновления сохраняются как выбраны (их уже подтвердили), а хоткеи остаются прежними.
    /// </summary>
    private void FinishSkipped()
    {
        if (_hotkeysFailed) Finish(UpdatesInput.IsChecked == true, _features.Changes);
        else Finish(checkForUpdates: null, features: null);
    }

    private void Finish(bool? checkForUpdates, IReadOnlyList<KeyValuePair<string, bool>>? features)
    {
        if (_finished) return;
        _finished = true;
        // Дальше язык меняет только App (сохранённый или возвращённый): окно больше не откликается на смену.
        Loc.Changed -= OnLanguageChanged;
        _host.Finish(checkForUpdates, _conflicts.Select(c => c.Id).ToList(), features, features is null ? null : LanguagePicker.Selected(LanguageInput));
        // Выбор подтверждён (не простой «Пропустить»): загрузка — в фоне, итог придёт уведомлением.
        if (features is not null && _preciseOcrInput is { IsChecked: true } && _features.IsOn(FeatureCatalog.Ocr)) _host.DownloadPreciseOcr?.Invoke();
    }

    private CheckBox? _preciseOcrInput;

    /// <summary>
    /// «Скачать точное распознавание текста» под «Распознаванием текста и QR»: по умолчанию снята (без согласия ничего не
    /// скачивается), видна, только пока отмечено распознавание, и только если модуля ещё нет.
    /// </summary>
    private void AddPreciseOcrChoice()
    {
        if (_host.DownloadPreciseOcr is null) return;
        var box = new CheckBox { Content = Loc.F("Wizard.DownloadPreciseOcr", Modules.OcrModule.SizeText), IsChecked = false };
        var hint = new TextBlock();
        Loc.Bind(hint, TextBlock.TextProperty, "Wizard.PreciseOcrHint");
        hint.SetResourceReference(StyleProperty, "Hint");
        var block = new StackPanel { Margin = new Thickness(28, 8, 0, 0) };
        block.Children.Add(box);
        block.Children.Add(hint);
        _features.AddBelow(FeatureCatalog.Ocr, block);
        _preciseOcrInput = box;
        void Sync() => block.Visibility = _features.IsOn(FeatureCatalog.Ocr) ? Visibility.Visible : Visibility.Collapsed;
        _features.Changed += Sync;
        Sync();
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_appliedOnce) { Close(); return; }
        ClearStatuses();
        if (CollectChoice() is not { } choice) return;

        SetBusy(true);
        try
        {
            foreach (var card in _cards.ToList())
            {
                if (card.Close?.IsChecked == true) Record(card.Conflict.Id, await _actions.CloseProcessesAsync(card.Conflict));
                if (card.Autostart?.IsChecked == true) Record(card.Conflict.Id, _actions.DisableAutostart(card.Conflict));
            }
            if (_systemPrintScreenInput?.IsChecked == true)
            {
                _systemPrintScreenResult = _actions.DisableSystemPrintScreen();
                _printScreenChangedNow |= _systemPrintScreenResult.Success;
            }
            await Rescan();

            var result = _host.ApplyHotkeys(SetupWizard.BuildHotkeys(_host.Hotkeys, choice));
            _hotkeysFailed = !result.Success;
            if (!result.Success)
            {
                var failed = result.Error;
                ShowStatus(failed?.Binding.Kind switch
                {
                    HotkeyKind.PrintScreen => PrintScreenStatus,
                    HotkeyKind.WinShiftS => WinShiftSStatus,
                    _ => CustomStatus,
                }, Loc.F("Wizard.ChooseAnother", failed?.Message ?? Loc.T("Wizard.HotkeyFailed")), error: true);
                return;
            }
            ShowHotkeyStatuses(result.Report);
            Finish(UpdatesInput.IsChecked == true, _features.Changes);
            _appliedOnce = true;
            ApplyButton.Content = Loc.T("Wizard.Done");
            SkipButton.Visibility = Visibility.Collapsed;
            // Выбор уже применён, а «Готово» только закрывает окно: менять его дальше нечем.
            PrintScreenInput.IsEnabled = WinShiftSInput.IsEnabled = CustomInput.IsEnabled = false;
            FeaturesPanel.IsEnabled = UpdatesInput.IsEnabled = LanguageInput.IsEnabled = false;
        }
        finally { SetBusy(false); }
    }

    private bool _appliedOnce;
    /// <summary>Последнее «Применить» не смогло включить хоткей (см. <see cref="FinishSkipped"/>).</summary>
    private bool _hotkeysFailed;

    private WizardHotkeyChoice? CollectChoice()
    {
        var custom = CustomInput.Spec;
        if (custom is { } spec)
        {
            string? error = spec == HotkeySpec.PrintScreen ? Loc.T("Hotkeys.PrintScreenUseBox") : HotkeyRules.Validate(spec);
            if (error is not null) { ShowStatus(CustomStatus, error, error: true); return null; }
        }
        var choice = new WizardHotkeyChoice(PrintScreenInput.IsChecked == true, WinShiftSInput.IsChecked == true, custom);
        if (!SetupWizard.HasAnyHotkey(choice, _host.Hotkeys))
        {
            ShowStatus(CustomStatus, Loc.T("Hotkeys.NeedOne"), error: true);
            return null;
        }
        return choice;
    }

    /// <summary>
    /// Что реально работает после применения. Успех <see cref="App.ApplyHotkeys"/> не значит, что PrintScreen свободен:
    /// запись, которая не работала и раньше, применение не блокирует. Поэтому смотрим статус каждой записи в отчёте.
    /// </summary>
    private void ShowHotkeyStatuses(HotkeyReport report)
    {
        ShowEntryStatus(report.Find(HotkeyKind.PrintScreen), PrintScreenStatus, HotkeyKind.PrintScreen);
        ShowEntryStatus(report.Find(HotkeyKind.WinShiftS), WinShiftSStatus, HotkeyKind.WinShiftS);
        if (CustomInput.Spec is { } spec && report.Entries.FirstOrDefault(s => s.Binding.Kind == HotkeyKind.Custom && s.Binding.ToRegisterSpec() == spec) is { } state)
            ShowEntryStatus(state, CustomStatus, HotkeyKind.Custom);
    }

    private void ShowEntryStatus(HotkeyState? state, TextBlock target, HotkeyKind kind)
    {
        if (state is null || state.Status == HotkeyStatus.Disabled) return;
        var blockers = _conflicts.Where(c => c.Keys.Contains(kind)).Select(c => c.Program.DisplayName).ToList();
        switch (state.Status)
        {
            case HotkeyStatus.Active when kind == HotkeyKind.PrintScreen && (_printScreenChangedNow || _systemPrintScreen):
                ShowStatus(target, Loc.T("Wizard.RegisteredSignOut"), error: false);
                break;
            case HotkeyStatus.Active when blockers.Count > 0:
                ShowStatus(target, Loc.F("Wizard.RegisteredButBlocked", string.Join(Loc.ListSeparator, blockers)), error: false);
                break;
            case HotkeyStatus.Active:
                ShowStatus(target, Loc.T("Wizard.Works"), error: false);
                break;
            case HotkeyStatus.NotAllowed:
                ShowStatus(target, Loc.T("Action.DevHotkeysNotRegistered"), error: false);
                break;
            default:
                var why = state.Message ?? Loc.T("Wizard.NotWorking");
                ShowStatus(target, blockers.Count > 0 ? Loc.F("Wizard.BlockedBy", why, string.Join(Loc.ListSeparator, blockers)) : why, error: true);
                break;
        }
    }

    private void ShowStatus(TextBlock target, string text, bool error)
    {
        target.Text = text;
        target.SetResourceReference(TextBlock.ForegroundProperty, error ? "Brush.DangerText" : "Brush.Ink70");
        target.Visibility = Visibility.Visible;
    }

    private void ClearStatuses()
    {
        foreach (var t in new[] { PrintScreenStatus, WinShiftSStatus, CustomStatus, GeneralError }) t.Visibility = Visibility.Collapsed;
    }
}

/// <summary>Иконка exe через SHGetFileInfo (только чтение файла).</summary>
internal static class ExeIcon
{
    public static ImageSource? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new ShFileInfo();
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;
            try
            {
                var image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally { DestroyIcon(info.hIcon); }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private const uint ShgfiIcon = 0x100, ShgfiLargeIcon = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
