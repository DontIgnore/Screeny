using Microsoft.Win32;

namespace Screeny.Migration;

/// <summary>
/// Флаг «Отключено» автозапуска, как его ставит Диспетчер задач: значение REG_BINARY с тем же именем, что у записи Run
/// (или имя файла ярлыка), в <c>Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{Run|Run32|StartupFolder}</c>.
/// 12 байт: 4 байта флага (чётный первый байт, обычно 02, — включено; нечётный, обычно 03, — отключено) и 8 байт FILETIME
/// момента отключения. Нет значения — запись включена. Сама запись Run не трогается, поэтому отключение обратимо
/// (Диспетчер задач → Автозагрузка → «Включить»).
/// </summary>
public static class StartupApproved
{
    public const string BaseKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>Включена ли запись по байтам флага; null или пустой массив — включена.</summary>
    public static bool IsEnabled(byte[]? value) => value is not { Length: > 0 } || (value[0] & 1) == 0;

    /// <summary>Значение «отключено пользователем» с отметкой времени, как пишет Диспетчер задач.</summary>
    public static byte[] DisabledValue(DateTime utcNow)
    {
        var bytes = new byte[12];
        bytes[0] = 0x03;
        BitConverter.TryWriteBytes(bytes.AsSpan(4), utcNow.ToFileTimeUtc());
        return bytes;
    }

    /// <summary>
    /// Раздел и подключ флага для места автозапуска. HKCU Run не перенаправляется WOW64, поэтому у пользователя <c>Run32</c>
    /// не бывает; 32-битный HKLM Run (WOW6432Node) хранит флаг в <c>Run32</c>. Подключ открывается в 64-битном представлении.
    /// </summary>
    public static (RegistryHive Hive, string SubKey) KeyFor(AutostartLocation location) => location switch
    {
        AutostartLocation.RunUser => (RegistryHive.CurrentUser, BaseKey + @"\Run"),
        AutostartLocation.RunMachine64 => (RegistryHive.LocalMachine, BaseKey + @"\Run"),
        AutostartLocation.RunMachine32 => (RegistryHive.LocalMachine, BaseKey + @"\Run32"),
        AutostartLocation.StartupFolderUser => (RegistryHive.CurrentUser, BaseKey + @"\StartupFolder"),
        AutostartLocation.StartupFolderCommon => (RegistryHive.LocalMachine, BaseKey + @"\StartupFolder"),
        _ => throw new ArgumentOutOfRangeException(nameof(location)),
    };
}

/// <summary>
/// Разбор строки <c>UninstallString</c> в файл и аргументы для запуска через ShellExecute. Тихие ключи не добавляются,
/// <c>QuietUninstallString</c> не используется вовсе: пользователь видит родной деинсталлятор программы.
/// </summary>
public static class UninstallCommand
{
    /// <summary>
    /// <c>"C:\…\unins000.exe" /x</c> → файл и аргументы; <c>MsiExec.exe /I{GUID}</c> или <c>/X{GUID}</c> → <c>msiexec.exe /X{GUID}</c>
    /// (без других ключей); путь без кавычек — до «.exe». null — строку не удалось разобрать или это не exe.
    /// </summary>
    public static (string File, string Arguments)? Parse(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim();
        var exe = ExtractExePath(text);
        if (exe is null) return null;
        var name = Path.GetFileName(exe);
        if (name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            int brace = text.IndexOf('{');
            int end = brace >= 0 ? text.IndexOf('}', brace) : -1;
            if (brace < 0 || end < 0) return null;
            var guid = text[brace..(end + 1)];
            if (!Guid.TryParse(guid, out _)) return null;
            return ("msiexec.exe", "/X" + guid);
        }
        int start = text.StartsWith('"') ? text.IndexOf('"', 1) + 1 : text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4;
        return (exe, text[start..].Trim());
    }

    /// <summary>Путь exe в начале команды: в кавычках или без них (до первого «.exe»). null, если exe не найден.</summary>
    public static string? ExtractExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim();
        string path;
        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            if (close < 0) return null;
            path = text[1..close];
        }
        else
        {
            int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe < 0) return null;
            path = text[..(exe + 4)];
        }
        path = path.Trim();
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && path.Length > 4 ? path : null;
    }
}
