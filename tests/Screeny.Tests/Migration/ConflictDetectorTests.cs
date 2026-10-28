using Screeny.Hotkeys;
using Screeny.Migration;

namespace Screeny.Tests.Migration;

public class ConflictDetectorTests
{
    private static readonly ConflictCatalog Catalog = ConflictCatalog.Default;

    private static IReadOnlyList<Conflict> Detect(FakeEnvironmentSnapshot env) => ConflictDetector.Detect(env, Catalog);

    [Fact]
    public void EmptyEnvironment_NoConflicts()
    {
        var env = new FakeEnvironmentSnapshot();
        Assert.Empty(Detect(env));
        Assert.False(ConflictDetector.IsSystemPrintScreenOn(env));
    }

    [Theory]
    [InlineData("Lightshot", "lightshot")]
    [InlineData("ShareX", "sharex")]
    [InlineData("Greenshot", "greenshot")]
    [InlineData("Snipaste", "snipaste")]
    [InlineData("picpick", "picpick")]
    public void RunningProcess_IsConflict(string processName, string id)
    {
        var env = new FakeEnvironmentSnapshot();
        env.ProcessList.Add(new ProcessEntry(processName, null, 100));
        var conflict = Assert.Single(Detect(env));
        Assert.Equal(id, conflict.Id);
        Assert.True(conflict.IsRunning);
        Assert.Empty(conflict.Autostarts);
    }

    [Fact]
    public void PowerToysProcess_WithoutRemap_IsNotConflict()
    {
        var env = new FakeEnvironmentSnapshot();
        env.ProcessList.Add(new ProcessEntry("PowerToys", @"C:\Users\u\AppData\Local\PowerToys\PowerToys.exe", 5));
        // Как на машине разработки: переназначены только PageUp/PageDown.
        env.PowerToysKeyboardConfig = """{"remapKeys":{"inProcess":[{"originalKeys":"33","newRemapKeys":"35"},{"originalKeys":"34","newRemapKeys":"46"}]},"remapShortcuts":{"global":[],"appSpecific":[]}}""";
        Assert.Empty(Detect(env));
    }

    [Fact]
    public void PowerToysRemapOfPrintScreen_IsConflict()
    {
        var env = new FakeEnvironmentSnapshot();
        env.ProcessList.Add(new ProcessEntry("PowerToys", null, 5));
        env.PowerToysKeyboardConfig = """{"remapKeys":{"inProcess":[{"originalKeys":"44","newRemapKeys":"91;16;83"}]}}""";
        var conflict = Assert.Single(Detect(env));
        Assert.Equal("powertoys", conflict.Id);
        Assert.Equal([HotkeyKind.PrintScreen], conflict.Keys);
        Assert.True(conflict.IsRunning);
    }

    [Theory]
    [InlineData("91;16;83")]
    [InlineData("92;160;83")]
    [InlineData("16;91;83")]
    public void PowerToysShortcutRemapOfWinShiftS_IsConflict(string keys)
    {
        var json = $$$"""{"remapShortcuts":{"global":[{"originalKeys":"{{{keys}}}","newRemapKeys":"44"}],"appSpecific":[]}}""";
        Assert.Equal([HotkeyKind.WinShiftS], PowerToysRemaps.BlockedKeys(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"remapShortcuts":{"global":[{"originalKeys":"162;83"}]}}""")]
    [InlineData("""{"remapKeys":{"inProcess":[{"originalKeys":44}]}}""")]
    public void PowerToysRemaps_IgnoresUnrelatedOrBrokenConfig(string? json) => Assert.Empty(PowerToysRemaps.BlockedKeys(json));

    [Fact]
    public void LightshotHklmRun32_NeedsAdmin()
    {
        var env = new FakeEnvironmentSnapshot();
        env.AutostartList.Add(new AutostartEntry(AutostartLocation.RunMachine32, "Lightshot", @"C:\Program Files (x86)\Skillbrains\lightshot\Lightshot.exe"));
        var conflict = Assert.Single(Detect(env));
        Assert.Equal("lightshot", conflict.Id);
        Assert.True(conflict.NeedsAdminForAutostart);
        Assert.Empty(conflict.UserAutostarts);
        Assert.Contains(HotkeyKind.PrintScreen, conflict.Keys);
    }

    [Fact]
    public void AutostartDisabledByStartupApproved_IsNotConflict()
    {
        var env = new FakeEnvironmentSnapshot();
        // Значение, которое пишет Диспетчер задач при «Отключить»: 03 00 00 00 + FILETIME.
        env.AutostartList.Add(new AutostartEntry(AutostartLocation.RunMachine32, "Lightshot", @"C:\x\Lightshot.exe",
            [3, 0, 0, 0, 0x39, 0x96, 0x33, 0xF4, 0x2A, 0xD6, 0xD9, 0x01]));
        env.UninstallList.Add(new UninstallEntry("k", "Lightshot-5.5.0.7", "\"C:\\x\\unins000.exe\"", null, true));
        Assert.Empty(Detect(env));
    }

    [Fact]
    public void AutostartExplicitlyEnabledByStartupApproved_IsConflict()
    {
        var env = new FakeEnvironmentSnapshot();
        env.AutostartList.Add(new AutostartEntry(AutostartLocation.RunUser, "Greenshot", "\"C:\\Program Files\\Greenshot\\Greenshot.exe\"",
            [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        var conflict = Assert.Single(Detect(env));
        Assert.Equal("greenshot", conflict.Id);
        Assert.False(conflict.NeedsAdminForAutostart);
        Assert.Single(conflict.UserAutostarts);
    }

    [Fact]
    public void ShareXStartupFolderShortcut_IsUserAutostart()
    {
        var env = new FakeEnvironmentSnapshot();
        env.AutostartList.Add(new AutostartEntry(AutostartLocation.StartupFolderUser, "ShareX.lnk", "\"C:\\Program Files\\ShareX\\ShareX.exe\""));
        var conflict = Assert.Single(Detect(env));
        Assert.Equal("sharex", conflict.Id);
        Assert.False(conflict.NeedsAdminForAutostart);
    }

    [Fact]
    public void AutostartMatchedByExeName_EvenWithOtherValueName()
    {
        var env = new FakeEnvironmentSnapshot();
        env.AutostartList.Add(new AutostartEntry(AutostartLocation.RunUser, "SnipasteAuto", "C:\\Tools\\Snipaste\\Snipaste.exe -silent"));
        Assert.Equal("snipaste", Assert.Single(Detect(env)).Id);
    }

    [Fact]
    public void CommonStartupFolder_NeedsAdmin()
    {
        var env = new FakeEnvironmentSnapshot();
        env.AutostartList.Add(new AutostartEntry(AutostartLocation.StartupFolderCommon, "Greenshot.lnk", ""));
        Assert.True(Assert.Single(Detect(env)).NeedsAdminForAutostart);
    }

    [Fact]
    public void InstalledButNotRunningNorAutostarting_IsNotConflict()
    {
        var env = new FakeEnvironmentSnapshot();
        env.UninstallList.Add(new UninstallEntry("k", "ShareX", "\"C:\\Program Files\\ShareX\\unins000.exe\"", null, true));
        Assert.Empty(Detect(env));
    }

    [Fact]
    public void UnrelatedProgramsAreIgnored()
    {
        var env = new FakeEnvironmentSnapshot();
        env.ProcessList.Add(new ProcessEntry("Greenshotter", null, 1));
        env.ProcessList.Add(new ProcessEntry("SnippingTool", null, 2));
        env.AutostartList.Add(new AutostartEntry(AutostartLocation.RunUser, "Steam", "\"C:\\Steam\\steam.exe\" -silent"));
        Assert.Empty(Detect(env));
    }

    [Theory]
    [InlineData("Lightshot-5.5.0.7", true)]
    [InlineData("Lightshot", true)]
    [InlineData("Lightshot (remove only)", true)]
    [InlineData("LightshotPlus", false)]
    [InlineData("Greenshot", false)]
    public void UninstallNameMatching(string displayName, bool expected)
        => Assert.Equal(expected, Catalog.Find("lightshot")!.MatchesUninstall(displayName));

    [Fact]
    public void Uninstaller_IsAttached_AndPowerToysPrefersBundle()
    {
        var env = new FakeEnvironmentSnapshot();
        env.ProcessList.Add(new ProcessEntry("PowerToys", null, 5));
        env.PowerToysKeyboardConfig = """{"remapKeys":{"inProcess":[{"originalKeys":"44","newRemapKeys":"0"}]}}""";
        env.UninstallList.Add(new UninstallEntry("{7031}", "PowerToys (Preview)", "MsiExec.exe /X{70316E2B-A36C-4CF2-A14C-431BEC482141}", null, false));
        env.UninstallList.Add(new UninstallEntry("{5FC1}", "PowerToys (Preview) x64",
            "\"C:\\Users\\u\\AppData\\Local\\Package Cache\\{5FC150C0-5191-4799-8969-8E3E290427FD}\\PowerToysUserSetup-0.100.2-x64.exe\"  /uninstall", null, false));
        var conflict = Assert.Single(Detect(env));
        Assert.Equal("{5FC1}", conflict.Uninstaller!.KeyName);
    }

    [Fact]
    public void DevConflictsScenario_FindsLightshotShareXSnipasteAndSystemPrintScreen()
    {
        var env = FakeEnvironmentSnapshot.Conflicts();
        var conflicts = Detect(env);
        Assert.Equal(["lightshot", "sharex", "snipaste"], conflicts.Select(c => c.Id));
        Assert.True(conflicts[0].NeedsAdminForAutostart);
        Assert.False(conflicts[1].NeedsAdminForAutostart);
        Assert.NotNull(conflicts[1].Uninstaller);
        Assert.Empty(conflicts[2].Keys);
        Assert.True(ConflictDetector.IsSystemPrintScreenOn(env));
    }

    [Theory]
    [InlineData(1, 19045, true)]
    [InlineData(0, 26100, false)]
    [InlineData(null, 26100, true)]
    [InlineData(null, 19045, false)]
    public void SystemPrintScreen(int? value, int build, bool expected)
    {
        var env = new FakeEnvironmentSnapshot { PrintScreenKeyForSnippingEnabled = value, OsBuild = build };
        Assert.Equal(expected, ConflictDetector.IsSystemPrintScreenOn(env));
    }

    [Fact]
    public void Blocks_OnlyEnabledOverlappingHotkeys()
    {
        var env = new FakeEnvironmentSnapshot();
        env.ProcessList.Add(new ProcessEntry("Lightshot", null, 1));
        var conflict = Assert.Single(Detect(env));
        Assert.True(conflict.Blocks([HotkeyBinding.PrintScreen(true)]));
        Assert.False(conflict.Blocks([HotkeyBinding.PrintScreen(false), HotkeyBinding.WinShiftS(true)]));
    }

    [Fact]
    public void CatalogIds_AreSafeWhitelistTokens()
    {
        foreach (var p in Catalog.Programs)
            Assert.Matches("^[a-z0-9]{1,32}$", p.Id);
        Assert.Equal(Catalog.Programs.Count, Catalog.Programs.Select(p => p.Id).Distinct().Count());
    }
}
