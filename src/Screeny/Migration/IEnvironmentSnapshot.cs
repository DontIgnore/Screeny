namespace Screeny.Migration;

/// <summary>Running process: name without “.exe”, full path (null if unreadable) and PID.</summary>
public sealed record ProcessEntry(string Name, string? Path, int Pid);

/// <summary>Where autostart is recorded. Place determines <c>StartupApproved</c> key and admin rights.</summary>
public enum AutostartLocation
{
    /// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run (not redirected by WOW64).</summary>
    RunUser,
    /// <summary>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run, 64-bit view.</summary>
    RunMachine64,
    /// <summary>HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run (32-bit programs).</summary>
    RunMachine32,
    /// <summary>Shortcut in user Startup folder.</summary>
    StartupFolderUser,
    /// <summary>Shortcut in common Startup folder (for all users).</summary>
    StartupFolderCommon,
}

/// <summary>
/// Autostart entry. <see cref="Name"/> — Run value name or shortcut file name (“ShareX.lnk”); <see cref="Command"/> —
/// command from Run or shortcut target. <see cref="Approval"/> — bytes from <c>Explorer\StartupApproved\…</c> (null — no value,
/// entry enabled): Task Manager stores “Disabled” this way without deleting entry.
/// </summary>
public sealed record AutostartEntry(AutostartLocation Location, string Name, string Command, byte[]? Approval = null)
{
    public bool IsEnabled => StartupApproved.IsEnabled(Approval);

    /// <summary>Entry for all users (HKLM or common folder): admin rights needed to disable.</summary>
    public bool IsMachineWide => Location is AutostartLocation.RunMachine64 or AutostartLocation.RunMachine32 or AutostartLocation.StartupFolderCommon;

    /// <summary>Exe name from command or shortcut target (“Lightshot.exe”); null if couldn't extract.</summary>
    public string? ExeFileName => UninstallCommand.ExtractExePath(Command) is { } path ? System.IO.Path.GetFileName(path) : null;

    /// <summary>Understandable location for user.</summary>
    public string LocationText => Location switch
    {
        AutostartLocation.RunUser => Localization.Loc.T("Autostart.RunUser"),
        AutostartLocation.StartupFolderUser => Localization.Loc.T("Autostart.StartupFolderUser"),
        AutostartLocation.StartupFolderCommon => Localization.Loc.T("Autostart.StartupFolderCommon"),
        _ => Localization.Loc.T("Autostart.Machine"),
    };
}

/// <summary>Programs and Features entry. <see cref="UninstallString"/> — native uninstaller (silent keys never read).</summary>
public sealed record UninstallEntry(string KeyName, string DisplayName, string? UninstallString, string? InstallLocation, bool MachineWide);

/// <summary>
/// Environment snapshot on which <see cref="ConflictDetector"/> searches conflicting programs. Read-only: real implementation
/// (<see cref="WindowsEnvironmentSnapshot"/>) reads registry, startup folders and process list; fake
/// (<see cref="FakeEnvironmentSnapshot"/>) — for tests and dev scenarios.
/// </summary>
public interface IEnvironmentSnapshot
{
    IReadOnlyList<ProcessEntry> Processes { get; }
    IReadOnlyList<AutostartEntry> Autostart { get; }
    IReadOnlyList<UninstallEntry> Uninstall { get; }

    /// <summary><c>HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled</c>; null — no value.</summary>
    int? PrintScreenKeyForSnippingEnabled { get; }

    /// <summary>Windows build: without value above system PrintScreen capture is on by default from Windows 11 (22000).</summary>
    int OsBuild { get; }

    /// <summary>
    /// JSON of active PowerToys Keyboard Manager config (<c>%LocalAppData%\Microsoft\PowerToys\Keyboard Manager</c>)
    /// or null: no PowerToys, module disabled, or file unreadable.
    /// </summary>
    string? PowerToysKeyboardConfig { get; }
}
