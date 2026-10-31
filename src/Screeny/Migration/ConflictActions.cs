using System.ComponentModel;
using Microsoft.Win32;
using Screeny.Localization;
using Screeny.Logging;

namespace Screeny.Migration;

public enum ActionOutcome
{
    Done,
    Failed,
    /// <summary>User canceled admin rights request (UAC).</summary>
    Cancelled,
    /// <summary>Admin rights needed: for writing for all users there is separate action.</summary>
    NeedsAdmin,
}

/// <summary>Result of one wizard action — user-understandable text for card.</summary>
public sealed record ActionResult(ActionOutcome Outcome, string Message)
{
    public bool Success => Outcome == ActionOutcome.Done;
}

/// <summary>Wizard actions on conflicting programs. Real — <see cref="ConflictActions"/>, for dev/tests — <see cref="FakeConflictActions"/>.</summary>
public interface IConflictActions
{
    /// <summary>Close program processes from found list: <c>CloseMainWindow</c>, after 2s — <c>Kill</c>.</summary>
    Task<ActionResult> CloseProcessesAsync(Conflict conflict);

    /// <summary>Disable autostart via <c>StartupApproved</c> (like Task Manager). Without rights — only current user entries.</summary>
    ActionResult DisableAutostart(Conflict conflict);

    /// <summary>Disable autostart for all users: restart Screeny with admin rights and wait for exit.</summary>
    Task<ActionResult> DisableAutostartElevatedAsync(Conflict conflict);

    /// <summary>Run program's native uninstaller (<c>UninstallString</c>, without silent keys).</summary>
    ActionResult LaunchUninstaller(Conflict conflict);

    /// <summary><c>HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled = 0</c>: PrintScreen no longer opens Windows capture.</summary>
    ActionResult DisableSystemPrintScreen();
}

/// <summary>Registry write (only what wizard needs).</summary>
public interface IRegistryWriter
{
    void SetBinary(RegistryHive hive, string subKey, string name, byte[] value);
    void SetDword(RegistryHive hive, string subKey, string name, int value);
}

/// <summary>Working with processes by PID.</summary>
public interface IProcessControl
{
    /// <summary>Process by PID or null if gone.</summary>
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

/// <summary>Launching external programs via ShellExecute.</summary>
public interface IShellLauncher
{
    void Open(string file, string arguments);

    /// <summary>Launch with <c>runas</c> and wait; returns exit code. UAC cancel — <see cref="Win32Exception"/> 1223.</summary>
    Task<int> RunElevatedAsync(string file, IReadOnlyList<string> arguments);
}

/// <summary>
/// Real wizard actions. Nothing deletes itself: autostart disabled via StartupApproved flag (reversible),
/// program removed by its own uninstaller, only processes from found list are closed.
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

    /// <summary>Real actions on this machine. Do not call in <c>--dev</c> or tests — use <see cref="MigrationServices"/>.</summary>
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
                // PID could be reused by another process: close only the one found at scan.
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
    /// Value written immediately, but Windows may pick it up only after sign-out: no official description
    /// of when change takes effect without Settings toggle, and Settings itself warns about restart.
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
        catch (ArgumentException) { return null; } // already exited
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
