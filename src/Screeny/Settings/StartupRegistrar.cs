using System.Reflection;
using Microsoft.Win32;

namespace Screeny.Settings;

/// <summary>Autostart value in <c>HKCU\...\Run</c>; interface to test logic without registry.</summary>
public interface IRunKey
{
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);
}

/// <summary><c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.</summary>
public sealed class RegistryRunKey : IRunKey
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKeyPath)!;
        key.SetValue(name, value);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

public static class StartupRegistrar
{
    public const string Name = "Screeny";

    /// <summary>
    /// Stable launch path if differs from current process: installer or portable Velopack build uses stub <c>&lt;root&gt;\Screeny.exe</c>,
    ///
    /// unchanged across updates (exe in <c>current\</c>, folder replaced entirely on update). null — process path.
    /// </summary>
    public static string? LaunchPath { get; private set; }

    /// <summary>Set once at startup (see <see cref="Updates.VelopackInfo"/>).</summary>
    public static void UseLaunchPath(string? path) => LaunchPath = string.IsNullOrWhiteSpace(path) ? null : path;

    /// <summary>Launch command of current process for Run key.</summary>
    public static string CurrentCommand()
        => LaunchPath is { } stable ? BuildCommand(stable, null) : BuildCommand(Environment.ProcessPath ?? "", AppAssemblyPath());

    /// <summary>Path to Screeny.dll (only when run via dotnet.exe; single-file not here).</summary>
    private static string? AppAssemblyPath()
        => Assembly.GetEntryAssembly()?.GetName().Name is { } name ? Path.Combine(AppContext.BaseDirectory, name + ".dll") : null;

    /// <summary>
    /// <c>"exe"</c>; when run via <c>dotnet run</c> process is dotnet.exe, so app assembly path appended:
    /// <c>"dotnet.exe" "Screeny.dll"</c>.
    /// </summary>
    public static string BuildCommand(string processPath, string? appAssemblyPath)
    {
        bool isDotnetHost = string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase);
        return isDotnetHost && !string.IsNullOrEmpty(appAssemblyPath)
            ? $"\"{processPath}\" \"{appAssemblyPath}\""
            : $"\"{processPath}\"";
    }

    /// <summary>Recorded command points to same launch (case-insensitive; quotes around single path optional).</summary>
    public static bool Matches(string? stored, string expected)
    {
        if (string.IsNullOrWhiteSpace(stored)) return false;
        var s = stored.Trim();
        if (string.Equals(s, expected, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(Unquote(s), Unquote(expected), StringComparison.OrdinalIgnoreCase) && !Unquote(expected).Contains('"');
    }

    private static string Unquote(string s) => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    public static void Set(bool enabled) => Set(new RegistryRunKey(), enabled, CurrentCommand());

    public static void Set(IRunKey key, bool enabled, string command)
    {
        if (enabled) key.Set(Name, command);
        else key.Delete(Name);
    }

    /// <summary>Autostart enabled exactly for this exe (entry from another copy or old path doesn't count).</summary>
    public static bool IsEnabled() => Matches(new RegistryRunKey().Get(Name), CurrentCommand());

    /// <summary>
    /// Autostart enabled but another path recorded (e.g., old portable <c>publish\Screeny.exe</c>, running installer version) —
    /// rewrites entry to <paramref name="expected"/>. Doesn't create missing entry: disabled autostart stays disabled. true — entry changed.
    ///
    /// </summary>
    public static bool RepairStalePath(IRunKey key, string expected)
    {
        var stored = key.Get(Name);
        if (string.IsNullOrWhiteSpace(stored) || Matches(stored, expected)) return false;
        key.Set(Name, expected);
        return true;
    }

    /// <summary>
    /// Uninstall: removes autostart entry only if it points inside <paramref name="rootDir"/>
    /// (another Screeny copy's entry untouched). true — entry removed.
    /// </summary>
    public static bool RemoveIfInside(IRunKey key, string rootDir)
    {
        var exe = ExecutableOf(key.Get(Name));
        if (exe is null || string.IsNullOrWhiteSpace(rootDir)) return false;
        string root;
        string full;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDir)) + Path.DirectorySeparatorChar;
            full = Path.GetFullPath(exe);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        key.Delete(Name);
        return true;
    }

    /// <summary>Exe path from Run command: first quoted value or whole unquoted string.</summary>
    public static string? ExecutableOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var s = command.Trim();
        if (s[0] != '"') return s;
        int end = s.IndexOf('"', 1);
        return end > 1 ? s[1..end] : null;
    }
}
