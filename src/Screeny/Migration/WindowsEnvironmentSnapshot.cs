using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Screeny.Logging;

namespace Screeny.Migration;

/// <summary>
/// Real environment snapshot. Read-only: registry (Run, StartupApproved, Uninstall, keyboard), Startup folders,
/// process list and PowerToys settings. Individual read errors logged and don't abort snapshot.
/// </summary>
public sealed class WindowsEnvironmentSnapshot : IEnvironmentSnapshot
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<ProcessEntry> Processes { get; private init; } = [];
    public IReadOnlyList<AutostartEntry> Autostart { get; private init; } = [];
    public IReadOnlyList<UninstallEntry> Uninstall { get; private init; } = [];
    public int? PrintScreenKeyForSnippingEnabled { get; private init; }
    public int OsBuild { get; private init; }
    public string? PowerToysKeyboardConfig { get; private init; }

    /// <summary>Reads everything anew. Can be called from background thread.</summary>
    public static WindowsEnvironmentSnapshot Capture() => new()
    {
        Processes = Safe(ReadProcesses, "processes"),
        Autostart = Safe(ReadAutostart, "autostart"),
        Uninstall = Safe(ReadUninstall, "uninstall"),
        PrintScreenKeyForSnippingEnabled = ReadPrintScreenValue(),
        OsBuild = Environment.OSVersion.Version.Build,
        PowerToysKeyboardConfig = ReadPowerToysConfig(),
    };

    private static IReadOnlyList<T> Safe<T>(Func<List<T>> read, string what)
    {
        try { return read(); }
        catch (Exception ex) { Log.Error($"Environment snapshot: {what} failed", ex); return []; }
    }

    private static List<ProcessEntry> ReadProcesses()
    {
        var list = new List<ProcessEntry>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { list.Add(new ProcessEntry(p.ProcessName, ProcessPath(p.Id), p.Id)); }
                catch (InvalidOperationException) { /* process already exited */ }
            }
        }
        return list;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// Exe path by PID via QueryFullProcessImageName: unlike <c>Process.MainModule</c> works for processes
    /// started by admin. null — no access.
    /// </summary>
    public static string? ProcessPath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    private static List<AutostartEntry> ReadAutostart()
    {
        var approvals = new Dictionary<AutostartLocation, RegistryKey?>();
        try
        {
            foreach (var location in Enum.GetValues<AutostartLocation>())
            {
                var (hive, sub) = StartupApproved.KeyFor(location);
                approvals[location] = OpenRead(hive, RegistryView.Registry64, sub);
            }
            var list = new List<AutostartEntry>();
            ReadRun(list, approvals, RegistryHive.CurrentUser, RegistryView.Default, AutostartLocation.RunUser);
            ReadRun(list, approvals, RegistryHive.LocalMachine, RegistryView.Registry64, AutostartLocation.RunMachine64);
            ReadRun(list, approvals, RegistryHive.LocalMachine, RegistryView.Registry32, AutostartLocation.RunMachine32);
            ReadStartupFolder(list, approvals, Environment.GetFolderPath(Environment.SpecialFolder.Startup), AutostartLocation.StartupFolderUser);
            ReadStartupFolder(list, approvals, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), AutostartLocation.StartupFolderCommon);
            return list;
        }
        finally
        {
            foreach (var key in approvals.Values) key?.Dispose();
        }
    }

    private static void ReadRun(List<AutostartEntry> list, Dictionary<AutostartLocation, RegistryKey?> approvals,
        RegistryHive hive, RegistryView view, AutostartLocation location)
    {
        using var run = OpenRead(hive, view, RunKey);
        if (run is null) return;
        foreach (var name in run.GetValueNames())
        {
            if (run.GetValue(name) is not string command) continue;
            list.Add(new AutostartEntry(location, name, command, approvals[location]?.GetValue(name) as byte[]));
        }
    }

    private static void ReadStartupFolder(List<AutostartEntry> list, Dictionary<AutostartLocation, RegistryKey?> approvals,
        string folder, AutostartLocation location)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*.lnk"))
        {
            var name = Path.GetFileName(file);
            var target = ShortcutTarget(file) ?? "";
            list.Add(new AutostartEntry(location, name, target.Length > 0 ? $"\"{target}\"" : "", approvals[location]?.GetValue(name) as byte[]));
        }
    }

    private static List<UninstallEntry> ReadUninstall()
    {
        var list = new List<UninstallEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64), (RegistryHive.CurrentUser, RegistryView.Registry32),
                 })
        {
            using var root = OpenRead(hive, view, UninstallKey);
            if (root is null) continue;
            foreach (var keyName in root.GetSubKeyNames())
            {
                bool machine = hive == RegistryHive.LocalMachine;
                try
                {
                    using var key = root.OpenSubKey(keyName);
                    if (key?.GetValue("DisplayName") is not string displayName || string.IsNullOrWhiteSpace(displayName)) continue;
                    // Key not redirected by WOW64 visible in both views — read once.
                    if (!seen.Add($"{hive}|{keyName}|{displayName}")) continue;
                    list.Add(new UninstallEntry(keyName, displayName.Trim(), key.GetValue("UninstallString") as string,
                        key.GetValue("InstallLocation") as string, machine));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        return list;
    }

    private static int? ReadPrintScreenValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard");
            return key?.GetValue("PrintScreenKeyForSnippingEnabled") is int v ? v : null;
        }
        catch (Exception ex) { Log.Error("Environment snapshot: keyboard settings failed", ex); return null; }
    }

    /// <summary>JSON of active Keyboard Manager config if module enabled in PowerToys. Read-only files.</summary>
    private static string? ReadPowerToysConfig()
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "PowerToys");
            var general = Path.Combine(root, "settings.json");
            if (!File.Exists(general)) return null;
            using (var doc = JsonDocument.Parse(File.ReadAllText(general)))
            {
                if (doc.RootElement.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.Object
                    && enabled.TryGetProperty("Keyboard Manager", out var km) && km.ValueKind == JsonValueKind.False)
                    return null;
            }
            var kmFolder = Path.Combine(root, "Keyboard Manager");
            string config = "default";
            var kmSettings = Path.Combine(kmFolder, "settings.json");
            if (File.Exists(kmSettings))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(kmSettings));
                if (doc.RootElement.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
                    && props.TryGetProperty("activeConfiguration", out var active) && active.ValueKind == JsonValueKind.Object
                    && active.TryGetProperty("value", out var value) && value.GetString() is { Length: > 0 } name
                    && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains(".."))
                    config = name;
            }
            var file = Path.Combine(kmFolder, config + ".json");
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error("Environment snapshot: PowerToys settings failed", ex);
            return null;
        }
    }

    private static RegistryKey? OpenRead(RegistryHive hive, RegistryView view, string subKey)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            return baseKey.OpenSubKey(subKey);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>Shortcut target via IShellLink (read-only file); null — failed.</summary>
    private static string? ShortcutTarget(string lnk)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(lnk, 0 /* STGM_READ */);
            var buffer = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            return buffer.Length > 0 ? buffer.ToString() : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
