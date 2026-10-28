using System.ComponentModel;
using Microsoft.Win32;
using Screeny.Localization;
using Screeny.Logging;

namespace Screeny.Migration;

public enum ActionOutcome
{
    Done,
    Failed,
    /// <summary>Пользователь отменил запрос прав администратора (UAC).</summary>
    Cancelled,
    /// <summary>Нужны права администратора: для записи для всех пользователей есть отдельное действие.</summary>
    NeedsAdmin,
}

/// <summary>Итог одного действия мастера — понятный пользователю текст для карточки.</summary>
public sealed record ActionResult(ActionOutcome Outcome, string Message)
{
    public bool Success => Outcome == ActionOutcome.Done;
}

/// <summary>Действия мастера над мешающими программами. Реальные — <see cref="ConflictActions"/>, для dev и тестов — <see cref="FakeConflictActions"/>.</summary>
public interface IConflictActions
{
    /// <summary>Закрыть процессы программы из найденного списка: <c>CloseMainWindow</c>, через 2 с — <c>Kill</c>.</summary>
    Task<ActionResult> CloseProcessesAsync(Conflict conflict);

    /// <summary>Отключить автозапуск через <c>StartupApproved</c> (как Диспетчер задач). Без прав — только записи текущего пользователя.</summary>
    ActionResult DisableAutostart(Conflict conflict);

    /// <summary>Отключить автозапуск для всех пользователей: перезапуск Screeny с правами администратора и ожидание его завершения.</summary>
    Task<ActionResult> DisableAutostartElevatedAsync(Conflict conflict);

    /// <summary>Запустить родной деинсталлятор программы (<c>UninstallString</c>, без тихих ключей).</summary>
    ActionResult LaunchUninstaller(Conflict conflict);

    /// <summary><c>HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled = 0</c>: PrintScreen больше не открывает захват Windows.</summary>
    ActionResult DisableSystemPrintScreen();
}

/// <summary>Запись в реестр (только то, что нужно мастеру).</summary>
public interface IRegistryWriter
{
    void SetBinary(RegistryHive hive, string subKey, string name, byte[] value);
    void SetDword(RegistryHive hive, string subKey, string name, int value);
}

/// <summary>Работа с процессами по PID.</summary>
public interface IProcessControl
{
    /// <summary>Процесс по PID или null, если его уже нет.</summary>
    IRunningProcess? Open(int pid);
}

public interface IRunningProcess : IDisposable
{
    string Name { get; }
    string? Path { get; }
    bool HasExited { get; }
    bool CloseMainWindow();
    Task<bool> WaitForExitAsync(TimeSpan timeout);
    void Kill();
}

/// <summary>Запуск внешних программ через ShellExecute.</summary>
public interface IShellLauncher
{
    void Open(string file, string arguments);

    /// <summary>Запуск с <c>runas</c> и ожидание завершения; возвращает код выхода. Отмена UAC — <see cref="Win32Exception"/> 1223.</summary>
    Task<int> RunElevatedAsync(string file, IReadOnlyList<string> arguments);
}

/// <summary>
/// Реальные действия мастера. Ничего не удаляет само: автозапуск отключается флагом StartupApproved (обратимо),
/// программа удаляется её собственным деинсталлятором, закрываются только процессы из найденного списка.
/// </summary>
public sealed class ConflictActions(
    IRegistryWriter registry,
    IProcessControl processes,
    IShellLauncher shell,
    bool elevated,
    ConflictCatalog catalog,
    Func<DateTime>? utcNow = null,
    TimeSpan? closeTimeout = null) : IConflictActions
{
    private const int ErrorCancelled = 1223;
    private readonly TimeSpan _closeTimeout = closeTimeout ?? TimeSpan.FromSeconds(2);

    /// <summary>Реальные действия на этой машине. Не вызывать в <c>--dev</c> и в тестах — для них <see cref="MigrationServices"/>.</summary>
    public static ConflictActions CreateReal(bool elevated)
        => new(new WindowsRegistryWriter(), new WindowsProcessControl(), new WindowsShellLauncher(), elevated, ConflictCatalog.Default);

    public async Task<ActionResult> CloseProcessesAsync(Conflict conflict)
    {
        var name = conflict.Program.DisplayName;
        try
        {
            foreach (var entry in conflict.Processes)
            {
                using var process = processes.Open(entry.Pid);
                if (process is null || process.HasExited) continue;
                // PID мог достаться другому процессу: закрываем только тот, что нашли при сканировании.
                if (!IsSame(process, entry)) continue;
                process.CloseMainWindow();
                if (await process.WaitForExitAsync(_closeTimeout)) continue;
                if (process.HasExited || !IsSame(process, entry)) continue;
                process.Kill();
                if (!await process.WaitForExitAsync(_closeTimeout))
                    return new(ActionOutcome.Failed, Loc.F("Action.NotClosed", name));
            }
            return new(ActionOutcome.Done, Loc.F("Action.Closed", name));
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            Log.Error($"Close {conflict.Id} failed", ex);
            return new(ActionOutcome.Failed, Loc.F("Action.CloseNoRights", name));
        }
    }

    private static bool IsSame(IRunningProcess process, ProcessEntry entry)
        => string.Equals(process.Name, entry.Name, StringComparison.OrdinalIgnoreCase)
           && (entry.Path is null || process.Path is null || string.Equals(process.Path, entry.Path, StringComparison.OrdinalIgnoreCase));

    public ActionResult DisableAutostart(Conflict conflict)
    {
        var name = conflict.Program.DisplayName;
        var entries = (elevated ? conflict.MachineAutostarts : conflict.UserAutostarts).ToList();
        if (entries.Count == 0)
            return conflict.NeedsAdminForAutostart && !elevated
                ? new(ActionOutcome.NeedsAdmin, Loc.F("Action.AutostartNeedsAdmin", name))
                : new(ActionOutcome.Done, Loc.F("Action.AutostartAlreadyOff", name));
        try
        {
            foreach (var entry in entries)
            {
                var (hive, subKey) = StartupApproved.KeyFor(entry.Location);
                registry.SetBinary(hive, subKey, entry.Name, StartupApproved.DisabledValue((utcNow ?? (() => DateTime.UtcNow))()));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Error($"Disable autostart {conflict.Id} failed", ex);
            return new(ActionOutcome.Failed, Loc.F("Action.AutostartNoRegistry", name));
        }
        return !elevated && conflict.NeedsAdminForAutostart
            ? new(ActionOutcome.NeedsAdmin, Loc.F("Action.AutostartUserOffMachineNeedsAdmin", name))
            : new(ActionOutcome.Done, Loc.F("Action.AutostartOff", name));
    }

    public async Task<ActionResult> DisableAutostartElevatedAsync(Conflict conflict)
    {
        var name = conflict.Program.DisplayName;
        if (catalog.Find(conflict.Id) is null) return new(ActionOutcome.Failed, Loc.T("Action.UnknownApp"));
        var (file, prefix) = ElevatedAction.SelfLaunch();
        try
        {
            int code = await shell.RunElevatedAsync(file, [.. prefix, .. ElevatedAction.Arguments(conflict.Id)]);
            return code switch
            {
                ElevatedAction.ExitOk => new(ActionOutcome.Done, Loc.F("Action.MachineAutostartOff", name)),
                ElevatedAction.ExitNothingToDo => new(ActionOutcome.Done, Loc.F("Action.AutostartAlreadyOff", name)),
                _ => new(ActionOutcome.Failed, Loc.F("Action.AutostartFailedCode", name, code)),
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new(ActionOutcome.Cancelled, Loc.T("Action.ElevationCancelled"));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Error($"Elevated disable autostart {conflict.Id} failed", ex);
            return new(ActionOutcome.Failed, Loc.F("Action.ElevationFailed", ex.Message));
        }
    }

    public ActionResult LaunchUninstaller(Conflict conflict)
    {
        var name = conflict.Program.DisplayName;
        if (UninstallCommand.Parse(conflict.Uninstaller?.UninstallString) is not { } command)
            return new(ActionOutcome.Failed, Loc.F("Action.UninstallerNotFound", name));
        try
        {
            shell.Open(command.File, command.Arguments);
            return new(ActionOutcome.Done, Loc.F("Action.UninstallerStarted", name));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new(ActionOutcome.Cancelled, Loc.T("Action.UninstallerCancelled"));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Error($"Uninstaller {conflict.Id} failed", ex);
            return new(ActionOutcome.Failed, Loc.F("Action.UninstallerFailed", name));
        }
    }

    public ActionResult DisableSystemPrintScreen()
    {
        try
        {
            registry.SetDword(RegistryHive.CurrentUser, @"Control Panel\Keyboard", "PrintScreenKeyForSnippingEnabled", 0);
            return new(ActionOutcome.Done, SystemPrintScreenDoneText);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Error("Disable system PrintScreen failed", ex);
            return new(ActionOutcome.Failed, Loc.T("Action.PrintScreenSettingFailed"));
        }
    }

    /// <summary>
    /// Значение пишется сразу, но Windows может подхватить его только после выхода из системы: официального описания
    /// того, когда изменение вступает в силу без переключателя в Параметрах, нет, а Параметры сами предупреждают о перезагрузке.
    /// </summary>
    public static string SystemPrintScreenDoneText => Loc.T("Action.PrintScreenDone");
}

internal sealed class WindowsRegistryWriter : IRegistryWriter
{
    public void SetBinary(RegistryHive hive, string subKey, string name, byte[] value)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(subKey, writable: true);
        key.SetValue(name, value, RegistryValueKind.Binary);
    }

    public void SetDword(RegistryHive hive, string subKey, string name, int value)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(subKey, writable: true);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }
}

internal sealed class WindowsProcessControl : IProcessControl
{
    public IRunningProcess? Open(int pid)
    {
        try { return new RunningProcess(System.Diagnostics.Process.GetProcessById(pid)); }
        catch (ArgumentException) { return null; } // уже завершился
    }

    private sealed class RunningProcess(System.Diagnostics.Process process) : IRunningProcess
    {
        public string Name => process.ProcessName;
        public string? Path => WindowsEnvironmentSnapshot.ProcessPath(process.Id);
        public bool HasExited => process.HasExited;
        public bool CloseMainWindow() => process.CloseMainWindow();
        public void Kill() => process.Kill();

        public async Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try { await process.WaitForExitAsync(cts.Token); return true; }
            catch (OperationCanceledException) { return process.HasExited; }
        }

        public void Dispose() => process.Dispose();
    }
}

internal sealed class WindowsShellLauncher : IShellLauncher
{
    public void Open(string file, string arguments)
    {
        using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, arguments) { UseShellExecute = true });
    }

    public async Task<int> RunElevatedAsync(string file, IReadOnlyList<string> arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(' ', arguments.Select(a => $"\"{a}\"")),
        };
        using var process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Process did not start");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
