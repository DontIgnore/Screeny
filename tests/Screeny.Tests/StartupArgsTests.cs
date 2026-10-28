using Screeny.Overlay;

namespace Screeny.Tests;

public class StartupArgsTests
{
    [Fact]
    public void Empty_IsWorkingInstance()
    {
        var a = StartupArgs.Parse([]);
        Assert.False(a.Dev);
        Assert.Equal("", a.Suffix);
        Assert.Null(a.OverlayTest);
    }

    [Fact]
    public void Dev_CombinesWithWindowsAndOverlayTest()
    {
        var a = StartupArgs.Parse(["--settings", "--DEV", "--history", "--overlay-test", @"C:\t\shot.png", "--select", "10, 20,300,200", "--scenario", "shapes"]);
        Assert.True(a.Dev);
        Assert.Equal(".Dev", a.Suffix);
        Assert.True(a.OpenSettings);
        Assert.True(a.OpenHistory);
        Assert.Equal(@"C:\t\shot.png", a.OverlayTest);
        Assert.Equal(new PixelRect(10, 20, 300, 200), a.Select);
        Assert.Equal("shapes", a.Scenario);
    }

    [Fact]
    public void TestKeys_WithoutDev_AreIgnored()
    {
        var a = StartupArgs.Parse(["--overlay-test", "x.png", "--select", "0,0,10,10", "--scenario", "shapes", "--settings"]);
        Assert.False(a.Dev);
        Assert.Null(a.OverlayTest);
        Assert.Null(a.Select);
        Assert.Null(a.Scenario);
        Assert.True(a.OpenSettings);
    }

    [Fact]
    public void OverlayTest_WithoutPath_IsIgnored()
    {
        var a = StartupArgs.Parse(["--dev", "--overlay-test"]);
        Assert.True(a.Dev);
        Assert.Null(a.OverlayTest);
    }

    [Fact]
    public void Edit_OnlyWithDev_KeepsScenario()
    {
        var a = StartupArgs.Parse(["--dev", "--edit", @"C:\h\shot.png", "--scenario", "original"]);
        Assert.Equal(@"C:\h\shot.png", a.Edit);
        Assert.Equal("original", a.Scenario);
        Assert.Null(a.OverlayTest);
        Assert.Null(a.Select);

        var b = StartupArgs.Parse(["--edit", "x.png", "--scenario", "original"]);
        Assert.Null(b.Edit);
        Assert.Null(b.Scenario);
    }

    [Fact]
    public void Scenario_WithDevHistory_IsKept_ButAloneIsDropped()
    {
        Assert.Equal("hover", StartupArgs.Parse(["--dev", "--history", "--scenario", "hover"]).Scenario);
        Assert.Equal("hotkey-problems", StartupArgs.Parse(["--dev", "--settings", "--scenario", "hotkey-problems"]).Scenario);
        Assert.Null(StartupArgs.Parse(["--dev", "--scenario", "hover"]).Scenario);
        Assert.Null(StartupArgs.Parse(["--dev", "--select", "0,0,5,5"]).Select); // выделение — только с --overlay-test
    }

    [Theory]
    [InlineData("1,2,3")]
    [InlineData("a,b,c,d")]
    [InlineData("0,0,0,10")]
    [InlineData("0,0,10,-1")]
    [InlineData(null)]
    public void TryParseRect_RejectsBadInput(string? text) => Assert.Null(StartupArgs.TryParseRect(text));

    [Fact]
    public void EnableLlHook_OnlyWithDev()
    {
        Assert.True(StartupArgs.Parse(["--dev", "--enable-ll-hook"]).EnableLlHook);
        Assert.False(StartupArgs.Parse(["--enable-ll-hook"]).EnableLlHook);
        Assert.False(StartupArgs.Parse(["--dev"]).EnableLlHook);
        Assert.False(StartupArgs.Parse([]).EnableLlHook);
    }

    [Theory]
    [InlineData("1")]
    [InlineData(" true ")]
    [InlineData("TRUE")]
    public void DevEnvironment_EqualsDevFlag(string env)
    {
        var a = StartupArgs.Parse(["--update-source", @"C:\rel", "--check-updates"], env);
        Assert.True(a.Dev);
        Assert.Equal(@"C:\rel", a.UpdateSource);
        Assert.True(a.CheckUpdates);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("yes")]
    public void OtherEnvironmentValues_AreNotDev(string? env)
        => Assert.False(StartupArgs.Parse([], env).Dev);

    [Fact]
    public void UpdateKeys_WithoutDev_AreIgnored()
    {
        var a = StartupArgs.Parse(["--update-source", @"C:\rel", "--check-updates"]);
        Assert.Null(a.UpdateSource);
        Assert.False(a.CheckUpdates);
    }

    [Fact]
    public void ModuleSource_WithDev_IsKept()
        => Assert.Equal(@"C:\mod", StartupArgs.Parse(["--dev", "--module-source", @"C:\mod"]).ModuleSource);

    [Fact]
    public void ModuleSource_WithoutDev_IsIgnored()
        => Assert.Null(StartupArgs.Parse(["--module-source", @"C:\mod"]).ModuleSource);

    [Fact]
    public void ModuleSource_WithDevEnvironment_IsKept()
        => Assert.Equal(@"C:\mod", StartupArgs.Parse(["--module-source", @"C:\mod"], "1").ModuleSource);

    [Theory]
    [InlineData("  ")]
    [InlineData("")]
    public void ModuleSource_Blank_IsNull(string value)
        => Assert.Null(StartupArgs.Parse(["--dev", "--module-source", value]).ModuleSource);

    [Fact]
    public void ModuleSource_MissingValue_IsNull()
        => Assert.Null(StartupArgs.Parse(["--dev", "--module-source"]).ModuleSource);

    [Fact]
    public void ModuleSource_DoesNotTurnOnOtherDevKeys()
    {
        var a = StartupArgs.Parse(["--dev", "--module-source", @"C:\mod"]);
        Assert.Null(a.UpdateSource);
        Assert.False(a.CheckUpdates);
        Assert.Null(a.OverlayTest);
    }

    [Fact]
    public void SetupWizard_KeepsScenarioOnlyWithDev()
    {
        var a = StartupArgs.Parse(["--dev", "--setup-wizard", "--scenario", "conflicts"]);
        Assert.True(a.SetupWizard);
        Assert.Equal("conflicts", a.Scenario);
        var b = StartupArgs.Parse(["--setup-wizard", "--scenario", "conflicts"]);
        Assert.True(b.SetupWizard);
        Assert.Null(b.Scenario);
        Assert.False(StartupArgs.Parse(["--dev"]).SetupWizard);
    }
}
