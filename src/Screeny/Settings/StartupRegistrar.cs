using System.Reflection;
using Microsoft.Win32;

namespace Screeny.Settings;

/// <summary>Значение автозапуска в <c>HKCU\...\Run</c>; интерфейс — чтобы проверять логику без реестра.</summary>
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
    /// Стабильный путь запуска, если он отличается от текущего процесса: у версии из установщика или портативной
    /// сборки Velopack это стаб <c>&lt;корень&gt;\Screeny.exe</c>, который не меняется между обновлениями
    /// (сам exe лежит в <c>current\</c>, а эта папка при обновлении заменяется целиком). null — путь процесса.
    /// </summary>
    public static string? LaunchPath { get; private set; }

    /// <summary>Задаётся один раз при старте (см. <see cref="Updates.VelopackInfo"/>).</summary>
    public static void UseLaunchPath(string? path) => LaunchPath = string.IsNullOrWhiteSpace(path) ? null : path;

    /// <summary>Команда запуска текущего процесса для ключа Run.</summary>
    public static string CurrentCommand()
        => LaunchPath is { } stable ? BuildCommand(stable, null) : BuildCommand(Environment.ProcessPath ?? "", AppAssemblyPath());

    /// <summary>Путь к Screeny.dll (нужен только при запуске через dotnet.exe; single-file сюда не попадает).</summary>
    private static string? AppAssemblyPath()
        => Assembly.GetEntryAssembly()?.GetName().Name is { } name ? Path.Combine(AppContext.BaseDirectory, name + ".dll") : null;

    /// <summary>
    /// <c>"exe"</c>; при запуске через <c>dotnet run</c> процесс — dotnet.exe, поэтому добавляется путь к сборке приложения:
    /// <c>"dotnet.exe" "Screeny.dll"</c>.
    /// </summary>
    public static string BuildCommand(string processPath, string? appAssemblyPath)
    {
        bool isDotnetHost = string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase);
        return isDotnetHost && !string.IsNullOrEmpty(appAssemblyPath)
            ? $"\"{processPath}\" \"{appAssemblyPath}\""
            : $"\"{processPath}\"";
    }

    /// <summary>Записанная команда указывает на тот же запуск (без учёта регистра; кавычки вокруг одиночного пути необязательны).</summary>
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

    /// <summary>Автозапуск включён именно для этого exe (запись от другой копии или старого пути не считается).</summary>
    public static bool IsEnabled() => Matches(new RegistryRunKey().Get(Name), CurrentCommand());

    /// <summary>
    /// Автозапуск включён, но записан другой путь (например, старый портативный <c>publish\Screeny.exe</c>, а запущена
    /// версия из установщика) — переписывает запись на <paramref name="expected"/>. Отсутствующую запись не создаёт:
    /// выключенный автозапуск остаётся выключенным. true — запись изменена.
    /// </summary>
    public static bool RepairStalePath(IRunKey key, string expected)
    {
        var stored = key.Get(Name);
        if (string.IsNullOrWhiteSpace(stored) || Matches(stored, expected)) return false;
        key.Set(Name, expected);
        return true;
    }

    /// <summary>
    /// Удаление программы: убирает запись автозапуска, только если она указывает внутрь <paramref name="rootDir"/>
    /// (запись другой копии Screeny не трогаем). true — запись удалена.
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

    /// <summary>Путь exe из команды Run: первое значение в кавычках или вся строка без кавычек.</summary>
    public static string? ExecutableOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var s = command.Trim();
        if (s[0] != '"') return s;
        int end = s.IndexOf('"', 1);
        return end > 1 ? s[1..end] : null;
    }
}
