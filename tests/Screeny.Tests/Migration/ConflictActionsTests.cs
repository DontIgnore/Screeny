using System.ComponentModel;
using Microsoft.Win32;
using Screeny.Migration;

namespace Screeny.Tests.Migration;

/// <summary>Реальная логика действий на подставных реестре, процессах и запуске программ — система не трогается.</summary>
public class ConflictActionsTests
{
    private sealed class FakeRegistry : IRegistryWriter
    {
        public List<(RegistryHive Hive, string SubKey, string Name, object Value)> Writes { get; } = new();
        public Exception? Throw { get; set; }

        public void SetBinary(RegistryHive hive, string subKey, string name, byte[] value)
        {
            if (Throw is not null) throw Throw;
            Writes.Add((hive, subKey, name, value));
        }

        public void SetDword(RegistryHive hive, string subKey, string name, int value)
        {
            if (Throw is not null) throw Throw;
            Writes.Add((hive, subKey, name, value));
        }
    }

    private sealed class FakeProcess(string name, string? path, bool closesOnRequest) : IRunningProcess
    {
        public string Name { get; set; } = name;
        public string? Path { get; } = path;
        public bool HasExited { get; private set; }
        public bool CloseRequested { get; private set; }
        public bool Killed { get; private set; }
        public Exception? KillThrows { get; set; }

        public bool CloseMainWindow()
        {
            CloseRequested = true;
            if (closesOnRequest) HasExited = true;
            return closesOnRequest;
        }

        public Task<bool> WaitForExitAsync(TimeSpan timeout) => Task.FromResult(HasExited);

        public void Kill()
        {
            if (KillThrows is not null) throw KillThrows;
            Killed = true;
            HasExited = true;
        }

        public void Dispose() { }
    }

    private sealed class FakeProcesses : IProcessControl
    {
        public Dictionary<int, FakeProcess> ByPid { get; } = new();
        public IRunningProcess? Open(int pid) => ByPid.GetValueOrDefault(pid);
    }

    private sealed class FakeShell : IShellLauncher
    {
        public List<(string File, string Args)> Opened { get; } = new();
        public List<(string File, IReadOnlyList<string> Args)> Elevated { get; } = new();
        public int ExitCode { get; set; }
        public Exception? Throw { get; set; }

        public void Open(string file, string arguments)
        {
            if (Throw is not null) throw Throw;
            Opened.Add((file, arguments));
        }

        public Task<int> RunElevatedAsync(string file, IReadOnlyList<string> arguments)
        {
            if (Throw is not null) throw Throw;
            Elevated.Add((file, arguments));
            return Task.FromResult(ExitCode);
        }
    }

    private readonly FakeRegistry _registry = new();
    private readonly FakeProcesses _processes = new();
    private readonly FakeShell _shell = new();
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private ConflictActions Actions(bool elevated = false)
        => new(_registry, _processes, _shell, elevated, ConflictCatalog.Default, () => Now, TimeSpan.Zero);

    private static Conflict Find(FakeEnvironmentSnapshot env, string id)
        => ConflictDetector.Detect(env, ConflictCatalog.Default).Single(c => c.Id == id);

    [Fact]
    public async Task Close_AsksWindowFirst_AndDoesNotKillWhenItExits()
    {
        var env = FakeEnvironmentSnapshot.Conflicts();
        var p = new FakeProcess("ShareX", @"C:\Program Files\ShareX\ShareX.exe", closesOnRequest: true);
        _processes.ByPid[41002] = p;
        var result = await Actions().CloseProcessesAsync(Find(env, "sharex"));
        Assert.True(result.Success);
        Assert.True(p.CloseRequested);
        Assert.False(p.Killed);
    }

    [Fact]
    public async Task Close_KillsAfterTimeout_TrayAppWithoutWindow()
    {
        var env = FakeEnvironmentSnapshot.Conflicts();
        var p = new FakeProcess("Lightshot", @"C:\Program Files (x86)\Skillbrains\lightshot\5.5.0.7\Lightshot.exe", closesOnRequest: false);
        _processes.ByPid[41001] = p;
        var result = await Actions().CloseProcessesAsync(Find(env, "lightshot"));
        Assert.True(result.Success);
        Assert.True(p.CloseRequested);
        Assert.True(p.Killed);
    }

    [Fact]
    public async Task Close_NeverTouchesProcessThatReusedThePid()
    {
        var env = FakeEnvironmentSnapshot.Conflicts();
        var other = new FakeProcess("notepad", @"C:\Windows\notepad.exe", closesOnRequest: false);
        _processes.ByPid[41001] = other;
        await Actions().CloseProcessesAsync(Find(env, "lightshot"));
        Assert.False(other.CloseRequested);
        Assert.False(other.Killed);
    }

    [Fact]
    public async Task Close_AccessDenied_ReportsUnderstandableError()
    {
        var env = FakeEnvironmentSnapshot.Conflicts();
        _processes.ByPid[41001] = new FakeProcess("Lightshot", null, closesOnRequest: false) { KillThrows = new Win32Exception(5) };
        var result = await Actions().CloseProcessesAsync(Find(env, "lightshot"));
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Contains("администратора", result.Message);
    }

    [Fact]
    public async Task Close_AlreadyExited_IsDone()
    {
        var env = FakeEnvironmentSnapshot.Conflicts();
        var result = await Actions().CloseProcessesAsync(Find(env, "snipaste"));
        Assert.True(result.Success);
    }

    [Fact]
    public void DisableAutostart_User_WritesStartupApprovedFlag()
    {
        var env = FakeEnvironmentSnapshot.Conflicts();
        var result = Actions().DisableAutostart(Find(env, "sharex"));
        Assert.True(result.Success);
        var write = Assert.Single(_registry.Writes);
        Assert.Equal(RegistryHive.CurrentUser, write.Hive);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", write.SubKey);
        Assert.Equal("ShareX.lnk", write.Name);
        Assert.Equal(StartupApproved.DisabledValue(Now), (byte[])write.Value);
    }

    [Fact]
    public void DisableAutostart_MachineWide_WithoutElevation_WritesNothing()
    {
        var env = FakeEnvironmentSnapshot.Conflicts();
        var result = Actions().DisableAutostart(Find(env, "lightshot"));
        Assert.Equal(ActionOutcome.NeedsAdmin, result.Outcome);
        Assert.Empty(_registry.Writes);
    }

    [Fact]
    public void DisableAutostart_Elevated_WritesHklmRun32()
    {
        var env = FakeEnvironmentSnapshot.Conflicts();
        var result = Actions(elevated: true).DisableAutostart(Find(env, "lightshot"));
        Assert.True(result.Success);
        var write = Assert.Single(_registry.Writes);
        Assert.Equal(RegistryHive.LocalMachine, write.Hive);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", write.SubKey);
        Assert.Equal("Lightshot", write.Name);
    }

    [Fact]
    public void DisableAutostart_RegistryDenied_IsFailure()
    {
        var env = FakeEnvironmentSnapshot.Conflicts();
        _registry.Throw = new UnauthorizedAccessException();
        Assert.Equal(ActionOutcome.Failed, Actions().DisableAutostart(Find(env, "sharex")).Outcome);
    }

    [Fact]
    public async Task Elevated_PassesOnlyWhitelistedTokens()
    {
        var env = FakeEnvironmentSnapshot.Conflicts();
        var result = await Actions().DisableAutostartElevatedAsync(Find(env, "lightshot"));
        Assert.True(result.Success);
        var call = Assert.Single(_shell.Elevated);
        Assert.Equal(["--elevated-action", "disable-autostart", "lightshot"], call.Args.TakeLast(3));
        Assert.True(call.Args.Count <= 4); // под dotnet.exe первым идёт путь к Screeny.dll
    }

    [Theory]
    [InlineData(ElevatedAction.ExitNothingToDo, ActionOutcome.Done)]
    [InlineData(ElevatedAction.ExitFailed, ActionOutcome.Failed)]
    [InlineData(ElevatedAction.ExitInvalidArgs, ActionOutcome.Failed)]
    public async Task Elevated_ExitCodes(int code, ActionOutcome expected)
    {
        _shell.ExitCode = code;
        var result = await Actions().DisableAutostartElevatedAsync(Find(FakeEnvironmentSnapshot.Conflicts(), "lightshot"));
        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public async Task Elevated_UacCancelled_IsCancelledNotError()
    {
        _shell.Throw = new Win32Exception(1223);
        var result = await Actions().DisableAutostartElevatedAsync(Find(FakeEnvironmentSnapshot.Conflicts(), "lightshot"));
        Assert.Equal(ActionOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public void Uninstaller_RunsUninstallStringWithoutQuietKeys()
    {
        var result = Actions().LaunchUninstaller(Find(FakeEnvironmentSnapshot.Conflicts(), "lightshot"));
        Assert.True(result.Success);
        var opened = Assert.Single(_shell.Opened);
        Assert.Equal(@"C:\Program Files (x86)\Skillbrains\lightshot\unins000.exe", opened.File);
        Assert.Equal("", opened.Args);
    }

    [Fact]
    public void Uninstaller_Missing_IsFailure()
    {
        var result = Actions().LaunchUninstaller(Find(FakeEnvironmentSnapshot.Conflicts(), "snipaste"));
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Empty(_shell.Opened);
    }

    [Fact]
    public void SystemPrintScreen_WritesZeroToHkcuKeyboard()
    {
        var result = Actions().DisableSystemPrintScreen();
        Assert.True(result.Success);
        Assert.Contains("выхода из системы", result.Message);
        var write = Assert.Single(_registry.Writes);
        Assert.Equal((RegistryHive.CurrentUser, @"Control Panel\Keyboard", "PrintScreenKeyForSnippingEnabled", (object)0), write);
    }

    [Fact]
    public async Task FakeActions_OnlyRecordAndChangeFakeEnvironment()
    {
        var env = FakeEnvironmentSnapshot.Conflicts();
        var fake = new FakeConflictActions(env);
        var catalog = ConflictCatalog.Default;
        await fake.CloseProcessesAsync(Find(env, "snipaste"));
        fake.DisableAutostart(Find(env, "sharex"));
        await fake.DisableAutostartElevatedAsync(Find(env, "lightshot"));
        fake.DisableSystemPrintScreen();
        Assert.Equal(["close:snipaste", "autostart:sharex", "elevated:lightshot", "printscreen"], fake.Calls);
        // ShareX ещё запущен, Lightshot тоже — но автозапуск обоих отключён, Snipaste закрыт.
        var after = ConflictDetector.Detect(env, catalog);
        Assert.Equal(["lightshot", "sharex"], after.Select(c => c.Id));
        Assert.All(after, c => Assert.Empty(c.Autostarts));
        Assert.False(ConflictDetector.IsSystemPrintScreenOn(env));
    }

    [Fact]
    public void DevServices_AreAlwaysFake()
    {
        Assert.True(MigrationServices.Create(isDev: true, scenario: null).IsFake);
        Assert.True(MigrationServices.Create(isDev: true, scenario: "conflicts").IsFake);
        Assert.IsType<FakeConflictActions>(MigrationServices.Create(isDev: true, scenario: "anything").Actions);
        Assert.IsType<FakeEnvironmentSnapshot>(MigrationServices.Create(isDev: true, scenario: null).Capture());
        Assert.Empty(ConflictDetector.Detect(MigrationServices.Create(isDev: true, scenario: null).Capture(), ConflictCatalog.Default));
    }
}
