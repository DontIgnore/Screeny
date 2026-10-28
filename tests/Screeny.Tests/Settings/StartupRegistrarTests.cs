using Screeny.Settings;

namespace Screeny.Tests.Settings;

public class StartupRegistrarTests
{
    [Fact]
    public void BuildCommand_ForExe_QuotesPath()
        => Assert.Equal("\"C:\\Apps\\Screeny.exe\"", StartupRegistrar.BuildCommand(@"C:\Apps\Screeny.exe", @"C:\Apps\Screeny.dll"));

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData(@"C:\Program Files\dotnet\DOTNET.EXE")]
    public void BuildCommand_ForDotnetHost_AddsAppDll(string host)
        => Assert.Equal($"\"{host}\" \"C:\\src\\bin\\Screeny.dll\"", StartupRegistrar.BuildCommand(host, @"C:\src\bin\Screeny.dll"));

    [Fact]
    public void BuildCommand_ForDotnetHostWithoutDll_QuotesHostOnly()
        => Assert.Equal("\"C:\\dotnet\\dotnet.exe\"", StartupRegistrar.BuildCommand(@"C:\dotnet\dotnet.exe", null));

    [Theory]
    [InlineData("\"C:\\Apps\\Screeny.exe\"")]
    [InlineData("\"c:\\apps\\screeny.EXE\"")]
    [InlineData("C:\\Apps\\Screeny.exe")]
    [InlineData("  \"C:\\Apps\\Screeny.exe\"  ")]
    public void Matches_SameExe_IgnoringCaseAndQuotes(string stored)
        => Assert.True(StartupRegistrar.Matches(stored, "\"C:\\Apps\\Screeny.exe\""));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"C:\\Old\\Screeny.exe\"")]
    [InlineData("\"C:\\Program Files\\dotnet\\dotnet.exe\"")]
    public void Matches_OtherPath_IsFalse(string? stored)
        => Assert.False(StartupRegistrar.Matches(stored, "\"C:\\Apps\\Screeny.exe\""));

    [Fact]
    public void Matches_DotnetCommand_ComparesWholeCommand()
    {
        var expected = "\"C:\\dotnet\\dotnet.exe\" \"C:\\src\\Screeny.dll\"";
        Assert.True(StartupRegistrar.Matches("\"c:\\dotnet\\dotnet.exe\" \"c:\\src\\screeny.dll\"", expected));
        Assert.False(StartupRegistrar.Matches("\"C:\\dotnet\\dotnet.exe\" \"C:\\other\\Screeny.dll\"", expected));
    }
}

public class StartupRegistrarRunKeyTests
{
    private sealed class FakeRunKey : IRunKey
    {
        public Dictionary<string, string> Values { get; } = new();
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string value) => Values[name] = value;
        public void Delete(string name) => Values.Remove(name);
    }

    private const string Installed = "\"C:\\Users\\u\\AppData\\Local\\DontIgnore.Screeny\\Screeny.exe\"";

    [Fact]
    public void Repair_RewritesOldPortablePath()
    {
        var key = new FakeRunKey();
        key.Set(StartupRegistrar.Name, "\"D:\\Tools\\Screeny\\publish\\Screeny.exe\"");
        Assert.True(StartupRegistrar.RepairStalePath(key, Installed));
        Assert.Equal(Installed, key.Get(StartupRegistrar.Name));
    }

    [Fact]
    public void Repair_LeavesMatchingEntry()
    {
        var key = new FakeRunKey();
        var stored = Installed.Trim('"').ToUpperInvariant();
        key.Set(StartupRegistrar.Name, stored);
        Assert.False(StartupRegistrar.RepairStalePath(key, Installed));
        Assert.Equal(stored, key.Get(StartupRegistrar.Name));
    }

    [Fact]
    public void Repair_NeverEnablesDisabledStartup()
    {
        var key = new FakeRunKey();
        Assert.False(StartupRegistrar.RepairStalePath(key, Installed));
        Assert.Empty(key.Values);
    }

    [Theory]
    [InlineData("\"C:\\Users\\u\\AppData\\Local\\DontIgnore.Screeny\\Screeny.exe\"", true)]
    [InlineData("C:\\Users\\u\\AppData\\Local\\DontIgnore.Screeny\\current\\Screeny.exe", true)]
    [InlineData("\"c:\\users\\u\\appdata\\local\\dontignore.screeny\\Screeny.exe\"", true)]
    [InlineData("\"D:\\Tools\\Screeny\\publish\\Screeny.exe\"", false)]
    [InlineData("\"C:\\Users\\u\\AppData\\Local\\DontIgnore.Screeny.Other\\Screeny.exe\"", false)] // соседняя папка с тем же префиксом
    public void RemoveIfInside_DeletesOnlyOwnEntry(string stored, bool removed)
    {
        var key = new FakeRunKey();
        key.Set(StartupRegistrar.Name, stored);
        Assert.Equal(removed, StartupRegistrar.RemoveIfInside(key, @"C:\Users\u\AppData\Local\DontIgnore.Screeny\"));
        Assert.Equal(removed, key.Get(StartupRegistrar.Name) is null);
    }

    [Theory]
    [InlineData("\"C:\\a b\\Screeny.exe\" --x", "C:\\a b\\Screeny.exe")]
    [InlineData("C:\\a\\Screeny.exe", "C:\\a\\Screeny.exe")]
    [InlineData("\"", null)]
    [InlineData("", null)]
    public void ExecutableOf(string command, string? expected)
        => Assert.Equal(expected, StartupRegistrar.ExecutableOf(command));

    [Fact]
    public void LaunchPath_OverridesProcessPath()
    {
        try
        {
            StartupRegistrar.UseLaunchPath(@"C:\Root\Screeny.exe");
            Assert.Equal("\"C:\\Root\\Screeny.exe\"", StartupRegistrar.CurrentCommand());
        }
        finally { StartupRegistrar.UseLaunchPath(null); }
        Assert.Null(StartupRegistrar.LaunchPath);
    }
}
