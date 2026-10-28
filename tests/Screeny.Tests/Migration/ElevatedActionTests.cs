using Screeny.Migration;

namespace Screeny.Tests.Migration;

public class ElevatedActionTests
{
    private static readonly ConflictCatalog Catalog = ConflictCatalog.Default;

    [Fact]
    public void Parse_AcceptsExactlyThreeWhitelistedTokens()
        => Assert.Equal("lightshot", ElevatedAction.Parse(["--elevated-action", "disable-autostart", "lightshot"], Catalog));

    [Theory]
    [InlineData("--elevated-action", "disable-autostart", "unknown")]
    [InlineData("--elevated-action", "disable-autostart", "LIGHTSHOT")]
    [InlineData("--elevated-action", "disable-autostart", @"HKLM\SOFTWARE\Run")]
    [InlineData("--elevated-action", "disable-autostart", @"C:\Windows\System32\cmd.exe")]
    [InlineData("--elevated-action", "disable-autostart", "../lightshot")]
    [InlineData("--elevated-action", "disable-autostart", "lightshot ")]
    [InlineData("--elevated-action", "disable-autostart", "")]
    [InlineData("--elevated-action", "delete-value", "lightshot")]
    [InlineData("--ELEVATED-ACTION", "disable-autostart", "lightshot")]
    [InlineData("disable-autostart", "--elevated-action", "lightshot")]
    public void Parse_RejectsAnythingElse(string a, string b, string c) => Assert.Null(ElevatedAction.Parse([a, b, c], Catalog));

    [Fact]
    public void Parse_RejectsExtraOrMissingArguments()
    {
        Assert.Null(ElevatedAction.Parse(["--elevated-action", "disable-autostart"], Catalog));
        Assert.Null(ElevatedAction.Parse(["--elevated-action", "disable-autostart", "lightshot", "sharex"], Catalog));
        Assert.Null(ElevatedAction.Parse(["--dev", "--elevated-action", "disable-autostart", "lightshot"], Catalog));
        Assert.Null(ElevatedAction.Parse(["--elevated-action", "disable-autostart", "lightshot", "--dev"], Catalog));
        Assert.Null(ElevatedAction.Parse([], Catalog));
    }

    [Theory]
    [InlineData(new[] { "--elevated-action" }, true)]
    [InlineData(new[] { "--dev", "--Elevated-Action", "x" }, true)]
    [InlineData(new[] { "--dev", "--settings" }, false)]
    [InlineData(new string[0], false)]
    public void IsRequested_FindsFlagAnywhere(string[] args, bool expected) => Assert.Equal(expected, ElevatedAction.IsRequested(args));

    private static (int Code, FakeConflictActions Actions) Run(string[] args, FakeEnvironmentSnapshot env)
    {
        var actions = new FakeConflictActions(env) { Elevated = true };
        return (ElevatedAction.Run(args, Catalog, () => env, actions), actions);
    }

    [Fact]
    public void Run_InvalidArguments_ExitCode2_AndNoAction()
    {
        var (code, actions) = Run(["--elevated-action", "disable-autostart", @"HKLM\x"], FakeEnvironmentSnapshot.Conflicts());
        Assert.Equal(ElevatedAction.ExitInvalidArgs, code);
        Assert.Empty(actions.Calls);
    }

    [Fact]
    public void Run_FindsEntryItself_AndDoesExactlyOneAction()
    {
        var (code, actions) = Run(["--elevated-action", "disable-autostart", "lightshot"], FakeEnvironmentSnapshot.Conflicts());
        Assert.Equal(ElevatedAction.ExitOk, code);
        Assert.Equal(["autostart:lightshot"], actions.Calls);
    }

    [Fact]
    public void Run_NothingMachineWide_ExitCode1()
    {
        var (code, actions) = Run(["--elevated-action", "disable-autostart", "sharex"], FakeEnvironmentSnapshot.Conflicts());
        Assert.Equal(ElevatedAction.ExitNothingToDo, code);
        Assert.Empty(actions.Calls);
        (code, _) = Run(["--elevated-action", "disable-autostart", "greenshot"], FakeEnvironmentSnapshot.Conflicts());
        Assert.Equal(ElevatedAction.ExitNothingToDo, code);
    }

    [Fact]
    public void Arguments_AreTheWhitelistedTriple()
        => Assert.Equal(["--elevated-action", "disable-autostart", "picpick"], ElevatedAction.Arguments("picpick"));
}
