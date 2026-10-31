using Microsoft.Win32;

namespace Screeny.Migration;

/// <summary>
/// Autostart “Disabled” flag as Task Manager sets: REG_BINARY with same name as Run entry
/// (or shortcut file name), in <c>Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{Run|Run32|StartupFolder}</c>.
/// 12 bytes: 4 flag bytes (even first byte, usually 02 — enabled; odd, usually 03 — disabled) and 8 bytes FILETIME
/// of disable moment. No value — enabled. Run entry itself untouched, so disable reversible
/// (Task Manager → Startup → “Enable”).
/// </summary>
public static class StartupApproved
{
    public const string BaseKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>Whether entry enabled by flag bytes; null or empty array — enabled.</summary>
    public static bool IsEnabled(byte[]? value) => value is not { Length: > 0 } || (value[0] & 1) == 0;

    /// <summary>“Disabled by user” value with timestamp, as Task Manager writes.</summary>
    public static byte[] DisabledValue(DateTime utcNow)
    {
        var bytes = new byte[12];
        bytes[0] = 0x03;
        BitConverter.TryWriteBytes(bytes.AsSpan(4), utcNow.ToFileTimeUtc());
        return bytes;
    }

    /// <summary>
    /// Hive/subkey for autostart location. HKCU Run not redirected by WOW64, so user never has <c>Run32</c>;
    /// 32-bit HKLM Run (WOW6432Node) has flag in <c>Run32</c>. Subkey opened in 64-bit view.
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
/// Parses <c>UninstallString</c> into file and arguments for ShellExecute. Silent keys not added,
/// <c>QuietUninstallString</c> never used: user sees program's native uninstaller.
/// </summary>
public static class UninstallCommand
{
    /// <summary>
    /// <c>"C:\…\unins000.exe" /x</c> → file and args; <c>MsiExec.exe /I{GUID}</c> or <c>/X{GUID}</c> → <c>msiexec.exe /X{GUID}</c>
    /// (no other keys); unquoted path — up to “.exe”. null — string unparsable or not exe.
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

    /// <summary>Exe path at command start: quoted or not (to first “.exe”). null if no exe found.</summary>
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
