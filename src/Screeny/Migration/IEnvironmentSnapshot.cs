namespace Screeny.Migration;

/// <summary>Запущенный процесс: имя без «.exe», полный путь (null, если не удалось прочитать) и PID.</summary>
public sealed record ProcessEntry(string Name, string? Path, int Pid);

/// <summary>Где записан автозапуск. От места зависит ключ <c>StartupApproved</c> и нужны ли права администратора.</summary>
public enum AutostartLocation
{
    /// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run (не перенаправляется WOW64).</summary>
    RunUser,
    /// <summary>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run, 64-битное представление.</summary>
    RunMachine64,
    /// <summary>HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run (32-битные программы).</summary>
    RunMachine32,
    /// <summary>Ярлык в папке «Автозагрузка» пользователя.</summary>
    StartupFolderUser,
    /// <summary>Ярлык в общей папке «Автозагрузка» (для всех пользователей).</summary>
    StartupFolderCommon,
}

/// <summary>
/// Запись автозапуска. <see cref="Name"/> — имя значения Run или имя файла ярлыка («ShareX.lnk»); <see cref="Command"/> —
/// команда из Run или цель ярлыка. <see cref="Approval"/> — байты из <c>Explorer\StartupApproved\…</c> (null — значения нет,
/// запись включена): так Диспетчер задач хранит «Отключено», не удаляя саму запись.
/// </summary>
public sealed record AutostartEntry(AutostartLocation Location, string Name, string Command, byte[]? Approval = null)
{
    public bool IsEnabled => StartupApproved.IsEnabled(Approval);

    /// <summary>Запись для всех пользователей (HKLM или общая папка): для отключения нужны права администратора.</summary>
    public bool IsMachineWide => Location is AutostartLocation.RunMachine64 or AutostartLocation.RunMachine32 or AutostartLocation.StartupFolderCommon;

    /// <summary>Имя exe из команды или цели ярлыка («Lightshot.exe»); null, если не удалось выделить.</summary>
    public string? ExeFileName => UninstallCommand.ExtractExePath(Command) is { } path ? System.IO.Path.GetFileName(path) : null;

    /// <summary>Понятное место для пользователя.</summary>
    public string LocationText => Location switch
    {
        AutostartLocation.RunUser => Localization.Loc.T("Autostart.RunUser"),
        AutostartLocation.StartupFolderUser => Localization.Loc.T("Autostart.StartupFolderUser"),
        AutostartLocation.StartupFolderCommon => Localization.Loc.T("Autostart.StartupFolderCommon"),
        _ => Localization.Loc.T("Autostart.Machine"),
    };
}

/// <summary>Запись «Программы и компоненты». <see cref="UninstallString"/> — родной деинсталлятор (тихие ключи не читаются вовсе).</summary>
public sealed record UninstallEntry(string KeyName, string DisplayName, string? UninstallString, string? InstallLocation, bool MachineWide);

/// <summary>
/// Снимок среды, по которому <see cref="ConflictDetector"/> ищет мешающие программы. Только чтение: реальная реализация
/// (<see cref="WindowsEnvironmentSnapshot"/>) читает реестр, папки автозагрузки и список процессов; подставная
/// (<see cref="FakeEnvironmentSnapshot"/>) — для тестов и dev-сценариев.
/// </summary>
public interface IEnvironmentSnapshot
{
    IReadOnlyList<ProcessEntry> Processes { get; }
    IReadOnlyList<AutostartEntry> Autostart { get; }
    IReadOnlyList<UninstallEntry> Uninstall { get; }

    /// <summary><c>HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled</c>; null — значения нет.</summary>
    int? PrintScreenKeyForSnippingEnabled { get; }

    /// <summary>Сборка Windows: без значения выше системный захват PrintScreen включён по умолчанию с Windows 11 (22000).</summary>
    int OsBuild { get; }

    /// <summary>
    /// JSON активной конфигурации Keyboard Manager из PowerToys (<c>%LocalAppData%\Microsoft\PowerToys\Keyboard Manager</c>)
    /// или null: PowerToys нет, модуль выключен или файл не прочитался.
    /// </summary>
    string? PowerToysKeyboardConfig { get; }
}
