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
    /// <summary>Поток серии ещё опрашивает клавишу; снимается только в его обратном вызове (App.Burst.cs).</summary>
    private bool _recorderActive;
    private readonly ImageSaveWorker _imageSaver = new();
    private readonly HashSet<Task> _pendingOperations = new();
    /// <summary>Номер последней операции, писавшей в буфер обмена: устаревший результат OCR его не перезаписывает.</summary>
    private int _clipboardOperation;

    /// <summary>Любая запись картинки в буфер (pin, история) делает незавершённый OCR устаревшим.</summary>
    public void NoteClipboardWrite() => _clipboardOperation++;
    private int ReserveClipboardWrite() => ++_clipboardOperation;
    private bool IsCurrentClipboardWrite(int operation) => operation == _clipboardOperation;

    /// <summary>Ключи запуска; <see cref="StartupArgs.Dev"/> — тестовый экземпляр, который не мешает рабочему.</summary>
    public StartupArgs Args { get; private set; } = StartupArgs.Parse([]);
    public bool IsDev => Args.Dev;
    public SettingsStore SettingsStore { get; private set; } = SettingsStore.Default();
    public AppSettings Settings { get; private set; } = new();
    /// <summary>Папка истории; null — функция «История» выключена (снимки только копируются, папка не читается).</summary>
    public HistoryStore? History { get; private set; }
    /// <summary>Пункты трея «История…» и «Открыть папку истории».</summary>
    public bool HistoryFeatureOn => Settings.IsFeatureOn(FeatureCatalog.History);
    /// <summary>Пункт трея «Закрепить из буфера обмена».</summary>
    public bool PinsFeatureOn => Settings.ActionAllowed(CaptureAction.Pin);

    /// <summary>Фоновое распознавание текста снимков для поиска в истории (<see cref="HistoryTextIndexer"/>).</summary>
    private HistoryTextIndexer? _textIndexer;

    /// <summary>Распознавать текст для поиска: только с функциями «История» и «Распознавание текста».</summary>
    private bool TextIndexOn => !IsShuttingDown && History is not null && TextSearchOn;

    /// <summary>
    /// Поиск по тексту в истории: функция «Распознавание текста» включена и в Windows есть хотя бы один язык OCR. Без языков
    /// индексатор ничего не распознает, и окно истории вечно показывало бы «ждут распознавания».
    /// </summary>
    private bool TextSearchOn => Settings.IsFeatureOn(FeatureCatalog.Ocr) && (_ocrHasLanguages ??= OcrHasLanguages());

    /// <summary>Есть ли языки OCR; перепроверяется при каждом применении функций (языки могли доустановить).</summary>
    private bool? _ocrHasLanguages;

    private static bool OcrHasLanguages()
    {
        try
        {
            if (OcrService.LanguageTags().Count > 0) return true;
            Log.Error("Windows has no OCR languages: history search is off");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("OCR languages check failed", ex);
            return false;
        }
    }

    public ICommand CaptureCommand { get; private set; } = null!;
    public ICommand HistoryCommand { get; private set; } = null!;
    public ICommand OpenFolderCommand { get; private set; } = null!;
    public ICommand PinFromClipboardCommand { get; private set; } = null!;
    public ICommand SettingsCommand { get; private set; } = null!;
    public ICommand ExitCommand { get; private set; } = null!;
    /// <summary>Все работающие хоткеи снимка через « / » (длинный список — первый и «…»); «—», если ни одного.</summary>
    public string HotkeyText => HotkeyReport.ActiveText();

    /// <summary>Состояние хоткеев после последнего применения: что работает, что занято, включён ли запасной.</summary>
    public HotkeyReport HotkeyReport => _hotkeys?.Report ?? HotkeyReport.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Любая ошибка старта — сообщение и выход, а не процесс без трея и хоткея, держащий mutex
    /// (из-за которого все следующие запуски молча завершались бы как «второй экземпляр»).
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Повышенный экземпляр мастера: одно действие из белого списка и выход — до mutex, трея и хоткеев.
        if (ElevatedAction.IsRequested(e.Args)) { RunElevatedActionAndExit(e.Args); return; }
        try
        {
            Args = StartupArgs.FromProcess(e.Args); // --dev или SCREENY_DEV=1
            // Событие создаём до mutex: иначе второй экземпляр мог создать его, взвести и закрыть
            // раньше, чем первый успел открыть, — и сигнал терялся вместе с объектом ядра.
            _captureEvent = new EventWaitHandle(false, EventResetMode.AutoReset, CaptureEventName + Args.Suffix);
            _mutex = new Mutex(true, MutexName + Args.Suffix, out _ownsMutex);
            if (!_ownsMutex)
            {
                // Тестовый экземпляр никогда не просит первый сделать снимок: тот сделал бы настоящий снимок экрана.
                if (IsDev) { Log.Error("Dev instance is already running; exiting"); Shutdown(3); return; }
                // Разрешаем первому экземпляру вывести оверлей на передний план — иначе он не получит клавиатуру.
                User32.AllowSetForegroundWindow(-1 /* ASFW_ANY */);
                _captureEvent.Set();
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (_, ex) => { Log.Error("Unhandled", ex.Exception); ex.Handled = true; };
            ThemeManager.Initialize(this);
            if (IsDev) SettingsStore = SettingsStore.Dev();
            // Проверка маленького монитора без второго монитора: окна подгоняются под эту рабочую область (пиксели устройства).
            if (IsDev && WindowFit.ParseWorkArea(Environment.GetEnvironmentVariable("SCREENY_DEV_WORKAREA")) is { } work) ChromeWindow.DevWorkAreaOverride = work;
            bool firstDevRun = IsDev && !File.Exists(SettingsStore.FilePath);
            Settings = SettingsStore.Load();
            Loc.Apply(Settings.Language);
            ApplyLanguageFonts();
            Loc.Changed += OnLanguageChanged;
            // Тестовый экземпляр не пишет в историю пользователя: своя папка по умолчанию.
            if (firstDevRun) Settings.HistoryFolder = SettingsStore.DevHistoryFolder();
            UpdateHistoryStore();
            InitTextIndexer();

            CaptureCommand = new RelayCommand(StartCapture);
            HistoryCommand = new RelayCommand(ShowHistory);
            OpenFolderCommand = new RelayCommand(OpenHistoryFolder);
            PinFromClipboardCommand = new RelayCommand(PinFromClipboard);
            SettingsCommand = new RelayCommand(ShowSettings);
            ExitCommand = new RelayCommand(ExitApp);

            _tray = (TaskbarIcon)FindResource("TrayIcon");
            _tray.DataContext = this;
            _tray.ForceCreate(enablesEfficiencyMode: false); // EcoQoS замедлил бы показ оверлея

            InitHotkeys();
            ListenForSecondInstance();
            InitPreciseOcr(); // App.PreciseOcr.cs; до окон из ключей: настройкам нужно состояние модуля
            OpenWindowsFromArgs();
            // Догоняющее распознавание старых снимков — не сразу: запуск и первые действия важнее.
            if (TextIndexOn) _textIndexer!.Start(TimeSpan.FromSeconds(IsDev ? 3 : 30));
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

    /// <summary>Приложение завершается: окна редактора не спрашивают про несохранённые пометки.</summary>
    public bool IsShuttingDown { get; private set; }

    private async void ExitApp()
    {
        if (await PrepareForExitAsync()) Shutdown();
    }

    /// <summary>Запрещает новые действия и дожидается незаконченных операций и сохранений. false — выход уже идёт.</summary>
    private async Task<bool> PrepareForExitAsync()
    {
        if (IsShuttingDown) return false;
        IsShuttingDown = true;
        _textIndexer?.Stop(); // распознавание не держит выход; недописанный текст распознается в следующий раз
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
        _sessionEnding = true; // App.Updates.cs: тихое обновление при таком выходе не применяется
        // При завершении сеанса Windows не гарантирует время на ожидание или продолжения UI.
        if (!_imageSaver.Queue.DrainAsync().Wait(TimeSpan.FromSeconds(5)))
            Log.Error("Timed out waiting for image saves during session ending");
        base.OnSessionEnding(e);
    }

    /// <summary>
    /// Ручная проверка окон: <c>--settings</c> и <c>--history</c> открывают окно сразу после старта,
    /// <c>--dev --overlay-test x.png</c> — оверлей по картинке, <c>--dev --edit x.png</c> — редактор снимка.
    /// Работает только у первого экземпляра — второй, как обычно, лишь просит первый сделать снимок.
    /// </summary>
    private void OpenWindowsFromArgs()
    {
        if (Args.OpenSettings) ShowSettings();
        if (Args.OpenHistory && Args.Scenario == "search") RunDevSearchScenario(); // App.DevHistory.cs, только dev
        else if (Args.OpenHistory && Args.Scenario == "pin-clipboard") RunDevPinScenario();
        else if (Args.OpenHistory) ShowHistory();
        if (Args.Feedback is { } feedback) ShowFeedback(feedback == "idea" ? Feedback.FeedbackKind.Idea : Feedback.FeedbackKind.Bug, Args.Scenario);
        if (Args.OverlayTest is not null || Args.BurstTest is not null) StartCapture();
        if (Args.Edit is not null) OpenEditor(Args.Edit)?.ApplyDevScenario(Args.Scenario);
    }

    /// <summary>
    /// Регистрирует все включённые хоткеи из настроек; проблемы (занято, PrintScreen у Windows, запасной Ctrl+Alt+S) —
    /// одним уведомлением. Тестовый экземпляр не регистрирует ни одного <c>RegisterHotKey</c> (они у рабочего), а хук
    /// Win+Shift+S ставит только с <c>--enable-ll-hook</c>.
    /// </summary>
    private void InitHotkeys()
    {
        _hotkeys = new HotkeyManager(new WindowsHotkeyBackend(Dispatcher), allowRegisterHotKey: !IsDev, allowHook: !IsDev || Args.EnableLlHook);
        _hotkeys.PressedKey += OnHotkeyPressed; // App.Burst.cs: удержание клавиши — серия снимков
        var result = _hotkeys.Apply(Settings.Hotkeys, strict: false);
        if (result.Report.ProblemSummary() is { } problem)
        {
            Log.Error($"Hotkeys: {problem}");
            Notify(problem);
        }
        OnHotkeysChanged();
    }

    /// <summary>
    /// Применяет новый список хоткеев (для мастера первого запуска и окна настроек): нормализует, атомарно
    /// перерегистрирует и при успехе сохраняет в настройки. При неудаче прежние хоткеи продолжают работать,
    /// а <see cref="HotkeyApplyResult.Error"/> называет запись, которая не заработала.
    /// Вызывать из UI-потока после запуска (<see cref="OnStartup"/> уже создал менеджер хоткеев).
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

    /// <summary>Строгое применение нормализованного списка; о вновь включившемся запасном хоткее — уведомление.</summary>
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
    /// <c>--dev --settings --scenario hotkey-problems</c>: окно настроек так, будто PrintScreen забирает Windows, первое
    /// своё сочетание занято, хук Win+Shift+S не поставился и работает запасной хоткей — для проверки подсказок без
    /// настоящей регистрации.
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
    /// Применяет и сохраняет настройки из окна. Возвращает ошибку или null.
    /// Порядок: папка → автозапуск → хоткеи последними (атомарно; при неудаче откатывается и автозапуск), и только потом
    /// изменения переносятся в текущий объект Settings. Так при ошибке ничего не применено наполовину,
    /// а LastColor/LastThickness, которые мог поменять оверлей, не затираются значениями с момента открытия окна.
    /// Хоткеи перерегистрируются и без изменений в списке: так PrintScreen подхватывается после отключения системного
    /// захвата Windows. Запись, которая не работала и раньше, сохранение не блокирует — блокирует только вновь включённая.
    /// </summary>
    private SettingsApplyError? ApplySettings(AppSettings updated)
    {
        // Без истории папка не нужна и не создаётся (её поля в окне скрыты и приходят без изменений).
        if (updated.IsFeatureOn(FeatureCatalog.History))
        {
            try { Directory.CreateDirectory(updated.HistoryFolder); }
            catch (Exception ex) { Log.Error("History folder failed", ex); return new(Loc.F("App.HistoryFolderCreateFailed", ex.Message)); }
        }

        // Тестовый экземпляр не трогает автозапуск: он принадлежит рабочему. Хоткеи — через HotkeyManager,
        // который в dev сам ничего не регистрирует (кроме хука с --enable-ll-hook).
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

    /// <summary>Переносит поля окна настроек в текущие настройки (последние цвет/толщину оверлея не трогает) и сохраняет.</summary>
    private bool CopyUserSettings(AppSettings updated)
    {
        // Оригиналы писались в прежнюю папку истории: при выключении настройки предлагаем удалить их оттуда.
        // Только если история была и осталась включённой: выключение самой истории файлы не трогает и ничего не предлагает.
        // Хранилище — прежнее (прежняя папка); если история была выключена и включается этим же сохранением — то, что
        // появится после ApplyFeatures.
        bool offerDeleteOriginals = Settings.KeepOriginals && !updated.KeepOriginals && updated.IsFeatureOn(FeatureCatalog.History);
        var originalsStore = offerDeleteOriginals ? History : null;
        // То же с распознанным текстом: выключили распознавание текста, а история осталась — предлагаем удалить .text.json.
        bool offerDeleteTexts = Settings.IsFeatureOn(FeatureCatalog.Ocr) && !updated.IsFeatureOn(FeatureCatalog.Ocr)
                                && updated.IsFeatureOn(FeatureCatalog.History);
        var textsStore = offerDeleteTexts ? History : null;
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
        if (offerDeleteTexts) textsStore ??= History;
        _historyWindow?.Refresh();
        foreach (var pin in Pins) pin.SetExcludedFromCapture(Settings.ExcludePinsFromCapture);
        bool saved = SaveSettings();
        if (originalsStore is not null || textsStore is not null) OfferDeleteSidecars(originalsStore, textsStore);
        return saved;
    }

    /// <summary>Хранилище истории по текущим настройкам; без функции «История» его нет (файлы в папке не трогаются).</summary>
    private void UpdateHistoryStore()
        => History = Settings.IsFeatureOn(FeatureCatalog.History) ? new HistoryStore(Settings.HistoryFolder, Settings.HistoryLimit) : null;

    /// <summary>
    /// Функции (<see cref="FeatureCatalog"/>) изменились в настройках или мастере: выключенная исчезает сразу — окно истории
    /// закрывается, хранилище убирается, закреплённые снимки закрываются, пункты трея прячутся, открытые редакторы
    /// перестраивают панель. Включённая обратно просто работает: история — со старой папкой.
    /// </summary>
    private void ApplyFeatures()
    {
        if (!HistoryFeatureOn) _historyWindow?.Close(); // до того, как хранилище станет null
        UpdateHistoryStore();
        _ocrHasLanguages = null;
        bool pins = Settings.ActionAllowed(CaptureAction.Pin);
        if (!pins) foreach (var pin in Pins.ToList()) pin.Close();
        if (_historyWindow is not null)
        {
            _historyWindow.CanPin = pins;
            _historyWindow.SearchEnabled = TextSearchOn;
        }
        // Папка могла смениться, функции — выключиться: проход по папке начинается заново (готовые снимки пропускаются).
        _textIndexer?.Stop();
        if (TextIndexOn) _textIndexer?.Start(TimeSpan.FromSeconds(5));
        foreach (Window window in Windows)
            if (window is EditorWindow editor) editor.ApplyFeatures();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HistoryFeatureOn)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinsFeatureOn)));
    }

    /// <summary>
    /// Настройку хранения оригиналов выключили: уже сохранённые <c>.orig.png</c> всё ещё содержат то, что было
    /// скрыто размытием. Выключили распознавание текста: <c>.text.json</c> всё ещё содержат текст со скриншотов.
    /// Спрашиваем по очереди, удалить ли их; «Оставить» ничего не трогает (настройка всё равно выключена).
    /// </summary>
    private async void OfferDeleteSidecars(HistoryStore? originalsStore, HistoryStore? textsStore)
    {
        if (originalsStore is not null)
            await OfferDelete(originalsStore, originalsStore.ListOriginals, "App.DeleteOriginalsTitle", "App.DeleteOriginalsText", "App.OriginalsPartlyDeletedTitle");
        if (textsStore is not null)
            await OfferDelete(textsStore, textsStore.ListTexts, "App.DeleteTextsTitle", "App.DeleteTextsText", "App.TextsPartlyDeletedTitle");
    }

    private async Task OfferDelete(HistoryStore store, Func<IReadOnlyList<string>> list, string titleKey, string textKey, string partlyKey)
    {
        var files = await _imageSaver.Queue.Enqueue(list);
        if (IsShuttingDown) return;
        int count = files.Count;
        if (count == 0) return;
        if (!Dialogs.Confirm(_settingsWindow, Loc.F(titleKey, count), Loc.T(textKey),
                Loc.T("Common.Delete"), danger: true, cancelLabel: Loc.T("App.Keep")))
            return;
        int deleted = await _imageSaver.Queue.Enqueue(() => store.DeleteFiles(files));
        if (deleted < count) Dialogs.Info(_settingsWindow, Loc.T(partlyKey), Loc.F("App.OriginalsPartlyDeletedText", deleted, count));
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
            // Подсказка трея ограничена 127 символами: длинный список сокращается так же, как в меню (и короче на длину версии).
            _tray.ToolTipText = IsDev ? $"Screeny {version} (dev)"
                : names.Count == 0 ? Loc.F("Tray.TipClick", version)
                : Loc.F("Tray.TipHotkeys", version, HotkeyReport.FormatNames(names, maxLength: Math.Max(40, 89 - version.Length)));
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HotkeyText)));
    }

    /// <summary>
    /// Язык сменили в настройках или мастере. Подписи из XAML (меню трея, окна) обновились сами; здесь — шрифт для
    /// иероглифов и собранная в коде подсказка трея. Окна с текстом из кода подписаны на <see cref="Loc.Changed"/> сами.
    /// </summary>
    private void OnLanguageChanged()
    {
        ApplyLanguageFonts();
        OnHotkeysChanged();
    }

    /// <summary>
    /// Шрифт интерфейса с запасным для японского или китайского: у Segoe UI нет иероглифов, а без явного шрифта WPF может
    /// взять начертания другого языка (японские знаки китайскими глифами).
    /// </summary>
    private void ApplyLanguageFonts() => Resources["Font.UI"] = new System.Windows.Media.FontFamily(Loc.UiFontFamily);

    public void Notify(string message)
    {
        _updateBalloon = null; // клик по этому уведомлению не открывает окно обновления
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

    /// <summary>
    /// Индексатор текста для поиска. Распознаёт быстрым OCR Windows с паузами и не начинает, пока открыт оверлей;
    /// запись файла идёт через общую очередь истории, чтобы не пересекаться с удалением и ротацией.
    /// </summary>
    private void InitTextIndexer()
    {
        _textIndexer = new HistoryTextIndexer(() => History, () => TextIndexOn, () => _capturing, new WindowsOcrTextRecognizer(),
            _imageSaver.Queue, throttle: TimeSpan.FromSeconds(2), freshDelay: TimeSpan.FromSeconds(2));
        _textIndexer.Indexed += (path, text) => Dispatcher.BeginInvoke(() => _historyWindow?.OnTextIndexed(path, text));
    }

    /// <summary>Сигнал от второго экземпляра. Поток завершается по _stopListening при выходе; ошибки не роняют процесс.</summary>
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
            catch (ObjectDisposedException) { /* выход */ }
            catch (Exception ex) { Log.Error("Second-instance listener failed", ex); }
        }) { IsBackground = true, Name = "Screeny.SecondInstanceListener" };
        thread.Start();
    }

    /// <summary>Снимок из трея, второго экземпляра, тестового запуска: всегда один кадр, без серии.</summary>
    public void StartCapture() => StartCapture(holdKey: null);

    /// <param name="holdKey">
    /// Основная клавиша сработавшего хоткея: пока её держат, снимается серия (App.Burst.cs), и оверлей открывается только
    /// после отпускания. null — оверлей сразу.
    /// </param>
    private void StartCapture(int? holdKey)
    {
        // Пока прежний поток серии опрашивает клавишу, новый снимок не начинается: два опроса сразу открыли бы два оверлея.
        if (_capturing || _recorderActive || IsShuttingDown) return;
        // Тестовый экземпляр никогда не снимает экран: только картинки из --overlay-test или --burst-test.
        if (IsDev && Args.OverlayTest is null && Args.BurstTest is null)
        {
            Notify("Dev instance never captures the screen: start it with --overlay-test <png> or --burst-test <folder>.");
            return;
        }
        _capturing = true; // и на всё удержание: повторный WM_HOTKEY (MOD_NOREPEAT и так их не шлёт) и клик в трее не мешают
        var latency = Stopwatch.StartNew();
        string mode = "plain";
        try
        {
            Screenshot shot;
            List<WindowInfo> windows;
            OverlayDevOptions? dev = null;
            IReadOnlyList<BurstFrame>? devFrames = null;
            if (IsDev)
            {
                holdKey = null; // клавиши не опрашиваются, экран не снимается
                if (Args.BurstTest is { } folder)
                {
                    (shot, devFrames) = LoadDevBurst(folder);
                    dev = new OverlayDevOptions(Args.Select, Args.Scenario, folder);
                }
                else
                {
                    shot = DevShot.Load(Args.OverlayTest!);
                    dev = new OverlayDevOptions(Args.Select, Args.Scenario, Args.OverlayTest);
                }
                windows = new List<WindowInfo>();
            }
            else
            {
                shot = ScreenCapture.CaptureVirtualScreen();
                windows = WindowFinder.Snapshot(shot.VirtualRect, exclude: OwnWindowHandles());
            }
            OverlayWindow? overlay = null;
            bool closed = false, burst = devFrames is { Count: > 1 }, released = false;
            void Release()
            {
                if (released) return;
                released = true;
                ReleaseBurstMemory();
            }
            // Опрос клавиши — до создания окна: окно строится, пока клавишу ещё держат, и к отпусканию уже готово.
            if (holdKey is { } key)
            {
                mode = "tap";
                BurstRecorder.Start(key, shot, windows, OwnWindowHandles(), latency, result => Dispatcher.BeginInvoke(() =>
                {
                    _recorderActive = false;
                    if (overlay is null) return; // окно не создалось: ошибка уже показана
                    if (result.IsBurst) { mode = "burst"; burst = true; }
                    OpenAfterHold(overlay, result, closed, key, Release);
                }));
                _recorderActive = true; // обратный вызов придёт в UI-поток не раньше, чем закончится этот метод
            }
            overlay = new OverlayWindow(shot, windows, Settings, dev, OcrRefiner);
            overlay.Completed += OnCaptureCompleted;
            overlay.DisplaysChanged += () => Notify(Loc.T("App.DisplaysChanged"));
            overlay.Closed += (_, _) =>
            {
                closed = true;
                _capturing = false;
                SaveSettings(); // последний цвет и толщина
                if (burst) Release();
            };
            overlay.ContentRendered += (_, _) => Log.Error($"Capture latency: {latency.ElapsedMilliseconds} ms from request to overlay on screen ({mode}, active: {overlay.IsActive})");
            if (devFrames is not null) overlay.SetBurst(devFrames, BurstTimeline.InitialFrame(devFrames.Count));
            if (holdKey is null) overlay.Show();
        }
        catch (Exception ex)
        {
            _capturing = false;
            Log.Error("Capture failed", ex);
            Notify(Loc.T("App.CaptureFailed"));
        }
    }

    /// <summary>Наши окна (pin, история, настройки) не должны подсвечиваться автодетектом.</summary>
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
    /// Сначала снимок в историю (своя защита от ошибок), затем действие в своём try: ошибка действия
    /// сообщается с причиной, а снимок уже не теряется.
    /// </summary>
    private void OnCaptureCompleted(CaptureResult r)
    {
        if (!IsShuttingDown) TrackOperation(CompleteCaptureAsync(r));
    }

    private async Task CompleteCaptureAsync(CaptureResult r)
    {
        _capturing = false; // оверлей уже скрыт; следующий хоткей не должен зависеть от действия
        bool inHistory = false;
        bool copy = r.Action == CaptureAction.CopyExplicit || (r.Action == CaptureAction.Copy && Settings.CopyOnEnter());
        int clipboardOperation = copy ? ReserveClipboardWrite() : 0;
        try
        {
            if (r.Action == CaptureAction.CopyText)
            {
                CopyRecognizedText(r.Text ?? ""); // текст, а не снимок: в историю не попадает
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
        // Занятый буфер — частый случай: ClipboardService возвращает false, остальные ошибки пробрасывает.
        if (!ClipboardService.CopyImage(image, png))
            Notify(Loc.T(inHistory ? "App.ClipboardBusyInHistory" : "App.ClipboardBusyNotCopied"));
    }

    /// <summary>Загрузка и кодирование копии выполняются в общей очереди; запись в буфер — в UI-потоке.</summary>
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

    /// <summary>Текст, выбранный в режиме текста оверлея (Ctrl+T), — в буфер обмена с превью в уведомлении.</summary>
    private void CopyRecognizedText(string text)
    {
        _clipboardOperation++;
        if (!ClipboardService.CopyText(text)) { Notify(Loc.T("App.ClipboardBusyText")); return; }
        var preview = text.ReplaceLineEndings(" ");
        Notify(Loc.F("App.TextCopied", preview.Length > 80 ? preview[..80] + "…" : preview));
    }

    private HistoryWindow? _historyWindow;

    /// <summary>Окно истории одно: повторный вызов обновляет и активирует существующее.</summary>
    public void ShowHistory()
    {
        if (History is null) return; // функция «История» выключена
        if (_historyWindow is null)
        {
            // Окно закрывается раньше, чем хранилище убирается (ApplyFeatures), поэтому History здесь не null.
            _historyWindow = new HistoryWindow(() => History!, PinAtCursor, path => OpenEditor(path), _imageSaver.Queue)
            {
                DevShowFirstCardActions = IsDev && Args.Scenario == "hover",
                CanPin = Settings.ActionAllowed(CaptureAction.Pin),
                SearchEnabled = TextSearchOn,
            };
            _historyWindow.Closed += (_, _) => _historyWindow = null;
        }
        _historyWindow.Refresh();
        _historyWindow.Show();
        if (_historyWindow.WindowState == WindowState.Minimized) _historyWindow.WindowState = WindowState.Normal;
        _historyWindow.Activate();
    }

    /// <summary>
    /// Окно повторного редактирования снимка истории. Каждое действие в нём («Копировать», «Сохранить как…»,
    /// «Закрепить») добавляет новую запись; исходная не меняется. null — снимок не открылся (сообщение уже показано).
    /// </summary>
    public EditorWindow? OpenEditor(string pngPath)
    {
        if (History is null) return null; // повторное редактирование — часть истории
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
        editor.Closed += (_, _) => SaveSettings(); // последний цвет, толщина, обводка/заливка
        editor.Show();
        editor.Activate();
        return editor;
    }

    private async Task<ImageSaveResult> SaveImageAsync(BitmapSource image, string? json, BitmapSource? original, byte[]? originalPng, string? path)
    {
        var store = History; // настройки папки могут измениться до окончания операции
        var result = await _imageSaver.SaveAsync(store, image, json, original, originalPng, path, DateTime.Now);
        if (result.HistoryError is { } error) Log.Error("History save failed", error);
        if (result.InHistory && ReferenceEquals(store, History)) _historyWindow?.Refresh();
        if (result.HistoryPath is { } saved && TextIndexOn) _textIndexer?.Add(saved);
        return result;
    }

    /// <summary>Закрепить картинку по центру монитора, на котором курсор (для снимков из истории).</summary>
    public void PinAtCursor(BitmapSource image)
    {
        User32.GetCursorPos(out var cursor);
        var info = new User32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<User32.MONITORINFO>() };
        User32.GetMonitorInfo(User32.MonitorFromPoint(cursor, User32.MONITOR_DEFAULTTONEAREST), ref info);
        var work = info.rcWork;
        int x = (work.Left + work.Right - image.PixelWidth) / 2, y = (work.Top + work.Bottom - image.PixelHeight) / 2;
        ShowPin(image, new PixelRect(Math.Max(work.Left, x), Math.Max(work.Top, y), image.PixelWidth, image.PixelHeight));
    }

    /// <summary>
    /// Пункт трея «Закрепить из буфера обмена»: картинка (PNG, файл картинки, DIB) закрепляется по центру монитора
    /// с курсором. Нет картинки или буфер занят — короткое уведомление; буфер не меняется.
    /// </summary>
    public void PinFromClipboard() => PinFromClipboard(new SystemClipboardSource());

    internal void PinFromClipboard(IClipboardSource clipboard)
    {
        if (IsShuttingDown || !Settings.ActionAllowed(CaptureAction.Pin)) return;
        var result = ClipboardImageReader.Read(clipboard);
        switch (result.Status)
        {
            case ClipboardImageStatus.Image:
                try { PinAtCursor(result.Image!); }
                catch (Exception ex) { Log.Error("Pin from clipboard failed", ex); Notify(Loc.F("App.PinFailed", ex.Message)); }
                break;
            case ClipboardImageStatus.Busy:
                Notify(Loc.T("App.ClipboardBusyPin"));
                break;
            default:
                Notify(Loc.T("App.ClipboardNoImage"));
                break;
        }
    }

    /// <summary>Все открытые закреплённые снимки — закрываются при выходе.</summary>
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
        _hotkeys?.Dispose(); // снимает RegisterHotKey и низкоуровневый хук
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
