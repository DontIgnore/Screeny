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
/// Мастер первого запуска и конфликты с другими программами снимков. Отдельный файл, чтобы в App.xaml.cs были только
/// две точки входа: <see cref="RunElevatedActionAndExit"/> и <see cref="CheckSetupWizardOnStartup"/>.
/// </summary>
public partial class App
{
    private SetupWizardWindow? _setupWizardWindow;
    private ICommand? _setupWizardCommand;
    private bool _wizardBalloonPending;
    private bool _balloonHooked;

    /// <summary>Пункт трея «Проверить конфликты…».</summary>
    public ICommand SetupWizardCommand => _setupWizardCommand ??= new RelayCommand(ShowSetupWizard);

    /// <summary>
    /// <c>--elevated-action …</c>: экземпляр, запущенный мастером с правами администратора ради одного действия. Выходит до
    /// mutex, трея и хоткеев, с кодом из <see cref="ElevatedAction"/>.
    /// </summary>
    private void RunElevatedActionAndExit(string[] args)
    {
        int code = ElevatedAction.RunReal(args);
        Environment.ExitCode = code;
        Shutdown(code);
    }

    /// <summary>
    /// При запуске: мастер ещё не пройден — показать его; пройден — проверить в фоне, не появилась ли новая программа,
    /// которая занимает включённый хоткей, и сообщить о ней уведомлением (не мастером). Тестовый экземпляр показывает
    /// мастер только с <c>--setup-wizard</c> и никогда не проверяет реальную среду.
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
                SaveSettings(); // напоминаем один раз, а не при каждом запуске
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

    /// <summary>Мастер конфликтов: при первом запуске, из трея и из настроек. Среда сканируется в фоне.</summary>
    public async void ShowSetupWizard()
    {
        if (_setupWizardWindow is not null) { _setupWizardWindow.Activate(); return; }
        if (IsShuttingDown) return;
        // В --dev всегда подставная среда и подставные действия (MigrationServices).
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
    /// Мастер пройден или пропущен: запоминаем версию (чтобы не показывать снова) и конфликты, которые остались после него
    /// (чтобы не напоминать о них уведомлением; исправленные забываются — если вернутся, будет уведомление). «Проверять обновления»
    /// и выбор функций сохраняются только по «Применить» (или по «Пропустить» после «Применить», на котором не заработал
    /// хоткей); функции применяются сразу (<see cref="ApplyFeatures"/>).
    /// </summary>
    private void FinishSetupWizard(bool? checkForUpdates, IReadOnlyList<string> seenConflicts, IReadOnlyList<KeyValuePair<string, bool>>? features,
        string? language)
    {
        if (checkForUpdates is { } updates) Settings.CheckForUpdates = updates;
        // Мастер показывал выбранный язык сразу; «Пропустить» возвращает сохранённый.
        if (language is not null) Settings.Language = language;
        Loc.Apply(Settings.Language);
        if (features is { Count: > 0 })
        {
            // Поверх текущих настроек: окно настроек, из которого открыт мастер, могло их уже поменять.
            Settings.DisabledFeatures = FeatureGate.MergeDisabled(Settings.DisabledFeatures, features);
            ApplyFeatures();
        }
        Settings.SetupWizardVersion = Math.Max(Settings.SetupWizardVersion, SetupWizard.Version);
        Settings.KnownConflicts = [.. seenConflicts.Distinct(StringComparer.Ordinal)]; // только то, что мешает и сейчас
        if (!SaveSettings()) Notify(Loc.T("App.WizardSaveFailed"));
        // Мастер открыт из окна настроек и что-то применил: без этого «Сохранить» там вернуло бы хоткеи и обновления с
        // момента открытия. Простой «Пропустить» ничего не менял — несохранённые правки в окне настроек не трогаем.
        if (checkForUpdates is not null) _settingsWindow?.RefreshFromLiveSettings(HotkeyReport);
    }
}
