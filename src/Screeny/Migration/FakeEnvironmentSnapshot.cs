namespace Screeny.Migration;

/// <summary>
/// Fake environment for tests and dev scenarios (<c>--dev --setup-wizard --scenario conflicts</c>). Mutable:
/// <see cref="FakeConflictActions"/> “executes” actions by mutating it — so re-scan in wizard shows result.
/// </summary>
public sealed class FakeEnvironmentSnapshot : IEnvironmentSnapshot
{
    public List<ProcessEntry> ProcessList { get; } = new();
    public List<AutostartEntry> AutostartList { get; } = new();
    public List<UninstallEntry> UninstallList { get; } = new();

    public IReadOnlyList<ProcessEntry> Processes => ProcessList.ToList();
    public IReadOnlyList<AutostartEntry> Autostart => AutostartList.ToList();
    public IReadOnlyList<UninstallEntry> Uninstall => UninstallList.ToList();
    public int? PrintScreenKeyForSnippingEnabled { get; set; } = 0;
    public int OsBuild { get; set; } = 26100;
    public string? PowerToysKeyboardConfig { get; set; }

    /// <summary>Dev scenarios: “conflicts” — Lightshot (HKLM), ShareX (HKCU), running Snipaste and system PrintScreen; else — empty.</summary>
    public static FakeEnvironmentSnapshot ForScenario(string? scenario)
        => string.Equals(scenario, "conflicts", StringComparison.OrdinalIgnoreCase) ? Conflicts() : new FakeEnvironmentSnapshot();

    public static FakeEnvironmentSnapshot Conflicts()
    {
        var env = new FakeEnvironmentSnapshot { PrintScreenKeyForSnippingEnabled = 1 };
        const string lightshot = @"C:\Program Files (x86)\Skillbrains\lightshot\Lightshot.exe";
        env.ProcessList.Add(new ProcessEntry("Lightshot", @"C:\Program Files (x86)\Skillbrains\lightshot\5.5.0.7\Lightshot.exe", 41001));
        env.AutostartList.Add(new AutostartEntry(AutostartLocation.RunMachine32, "Lightshot", lightshot));
        env.UninstallList.Add(new UninstallEntry("{30A5B3C9-2084-4063-A32A-628A98DE512B}_is1", "Lightshot-5.5.0.7",
            "\"C:\\Program Files (x86)\\Skillbrains\\lightshot\\unins000.exe\"", @"C:\Program Files (x86)\Skillbrains\lightshot\", true));

        env.ProcessList.Add(new ProcessEntry("ShareX", @"C:\Program Files\ShareX\ShareX.exe", 41002));
        env.AutostartList.Add(new AutostartEntry(AutostartLocation.StartupFolderUser, "ShareX.lnk", "\"C:\\Program Files\\ShareX\\ShareX.exe\" -silent"));
        env.UninstallList.Add(new UninstallEntry("82E6AC09-0FEF-4390-AD9F-0DD3F5561EFC_is1", "ShareX",
            "\"C:\\Program Files\\ShareX\\unins000.exe\"", @"C:\Program Files\ShareX\", true));

        env.ProcessList.Add(new ProcessEntry("Snipaste", @"C:\Tools\Snipaste\Snipaste.exe", 41003));
        return env;
    }
}
