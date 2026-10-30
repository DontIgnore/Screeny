using System.Windows;
using System.Windows.Input;
using Screeny.Localization;
using Screeny.Logging;
using Screeny.Settings;
using Screeny.Themes;
using Screeny.Updates;

namespace Screeny;

/// <summary>
/// Обновления и путь автозапуска. Отдельный файл, чтобы App.xaml.cs менялся минимально: оттуда вызываются только
/// <see cref="InitUpdates"/> (в конце старта) и <see cref="DisposeUpdates"/> (при выходе).
/// </summary>
public partial class App
{
    private VelopackInfo _velopack = VelopackInfo.None;
    private UpdateService? _updates;
    private UpdateWindow? _updateWindow;
    private ICommand? _checkUpdatesCommand;
    /// <summary>Последнее уведомление трея — «Доступна версия X»: клик по нему открывает окно обновления.</summary>
    private UpdateCandidate? _updateBalloon;
    /// <summary>
    /// Обновление, о котором не спрашивали (все изменения — про выключенные функции): версия, которая скачивается,
    /// и скачанное, которое применится без окон при выходе.
    /// </summary>
    private string? _silentVersion;
    private UpdateCandidate? _silentReady;
    /// <summary>Растёт, когда тихое обновление отменено (пропустили версию, ставят обычным путём): незаконченная тихая
    /// загрузка, закончившись, видит другое число и не готовит применение.</summary>
    private int _silentGeneration;
    /// <summary>Обычное обновление уже передано Update.exe: тихое при выходе не запускаем второй раз.</summary>
    private bool _applyScheduled;
    /// <summary>Выход из-за завершения сеанса Windows: тихое обновление не применяем (Update.exe могут прервать).</summary>
    private bool _sessionEnding;

    /// <summary>Пункт меню трея «Проверить обновления…».</summary>
    public ICommand CheckUpdatesCommand => _checkUpdatesCommand ??= new RelayCommand(() => _ = CheckForUpdatesManuallyAsync());

    private void InitUpdates()
    {
        _velopack = VelopackInfo.Detect();
        // Путь автозапуска уже задан в Program.Main. Тестовый экземпляр не трогает запись Run рабочего.
        if (!IsDev && _velopack.IsInstalled) RepairStartupPath();

        IUpdateClient client = UpdateService.ChooseSource(IsDev, Args.UpdateSource) switch
        {
            UpdateSourceKind.Folder => VelopackUpdateClient.ForFolder(Args.UpdateSource!),
            UpdateSourceKind.Github => VelopackUpdateClient.ForGithub(VelopackUpdateClient.IsPrerelease(AppVersion.Informational())),
            _ => NoUpdateClient.Instance, // dev без --update-source: никакой сети, и ручная проверка тоже
        };
        _updates = new UpdateService(client, UpdateStateStore.Default(IsDev), () => Settings.CheckForUpdates);
        _updates.UpdateFound += OnUpdateFound;
        if (_tray is not null) _tray.TrayBalloonTipClicked += (_, _) => { if (_updateBalloon is { } u) ShowUpdateWindow(u); };

        if (UpdateService.AutomaticAllowed(IsDev, Args.UpdateSource is not null, client.CanUpdate)) _ = _updates.RunAsync();
        if (Args.CheckUpdates) _ = CheckForUpdatesManuallyAsync();
        OnHotkeysChanged(); // подсказка трея была собрана до появления клиента: теперь с версией из пакета Velopack
    }

    /// <summary>
    /// Версия этой копии: из пакета Velopack (та же, что у проверки обновлений), у копии без него — из сборки.
    /// До <see cref="InitUpdates"/> — всегда из сборки.
    /// </summary>
    public string CurrentVersion => AppVersion.Choose(_updates?.Client.CurrentVersion, AppVersion.Informational());

    /// <summary>Копия умеет обновляться сама (установлена или портативная, и есть источник обновлений).</summary>
    public bool CanUpdateItself => _updates?.Client.CanUpdate == true;

    /// <summary>
    /// Автозапуск включён, но указывает на другой exe (старый портативный <c>publish\Screeny.exe</c>, прежняя папка) —
    /// переписываем на стаб установленной версии. Выключенный автозапуск не включаем.
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
        _updateBalloon = update; // после Notify: тот сбрасывает признак для остальных уведомлений
    }

    /// <summary>
    /// Все изменения версии — про выключенные функции: не отвлекаем, а скачиваем в фоне; применит <see cref="DisposeUpdates"/>
    /// при выходе. Ошибка — только в журнал, следующая проверка попробует снова. Пока шла загрузка, версию могли
    /// пропустить или начать ставить из окна, а приложение — начать выходить: тогда скачанное молча не применяется.
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
    /// Ручная проверка (меню трея и «О программе» в настройках): идёт и при выключенной автоматической. null — проверки не
    /// было: приложение выходит или окно обновления уже открыто (тогда оно выводится вперёд).
    /// </summary>
    public async Task<UpdateCheckResult?> CheckForUpdatesAsync()
    {
        if (_updates is null || IsShuttingDown) return null;
        if (_updateWindow is not null) { _updateWindow.Activate(); return null; }
        var result = await _updates.CheckAsync(UpdateCheckKind.Manual);
        return IsShuttingDown ? null : result;
    }

    /// <summary>Пункт трея: итог — диалогом, найденная версия — окном обновления.</summary>
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

    /// <summary>Окно показывается всегда, даже если все изменения — про выключенные функции (так бывает у ручной проверки).</summary>
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
                CancelSilentUpdate(); // пропущенную не ставим и молча — даже если её тихая загрузка ещё идёт
                return true;
            });
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
        _updateWindow.Activate();
    }

    /// <summary>
    /// Скачать, дождаться незаконченных сохранений снимков (как при обычном выходе), передать применение Update.exe
    /// и выйти: Update.exe ждёт завершения процесса, заменяет <c>current\</c> и запускает новую версию.
    /// Идущая тихая загрузка той же версии не повторяется: ждём её и берём скачанное (<see cref="UpdateService.DownloadAsync"/>).
    /// </summary>
    private async Task InstallUpdateAsync(UpdateService updates, UpdateCandidate update, Action<int> progress, CancellationToken ct)
    {
        CancelSilentUpdate(); // ставится это обновление, а не тихое
        await updates.DownloadAsync(update, progress, ct);
        ct.ThrowIfCancellationRequested();
        if (!await PrepareForExitAsync()) return;
        CancelSilentUpdate();
        _applyScheduled = true; // и если запуск Update.exe не удался — тихое при выходе всё равно не нужно
        try { updates.Client.ApplyAfterExit(update, RestartArgs()); }
        catch (Exception ex)
        {
            // Окна уже заблокированы для выхода: сообщаем и всё равно завершаемся, обновление можно повторить после запуска.
            Log.Error("Update apply failed to start", ex);
            Dialogs.Info(null, Loc.T("Updates.ApplyFailedTitle"), Loc.F("Updates.ApplyFailedText", ex.Message));
        }
        Shutdown();
    }

    /// <summary>
    /// Ключи для перезапуска после обновления. Тестовый экземпляр перезапускается тестовым (с теми же ключами),
    /// иначе новая версия стала бы рабочим экземпляром; обычный — без ключей.
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
    /// Выход с тихо скачанным обновлением: Update.exe дождётся завершения процесса и применит его без окон и без
    /// перезапуска. Не применяем, если за это время выключили «Проверять обновления», пропустили версию, включили функцию
    /// из списка изменений, уже запланировали обычное обновление или выходим из-за завершения сеанса Windows.
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
        // Функцию могли снова включить: тогда в списке изменений есть что показать — пусть спросит следующая проверка.
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
