using Microsoft.Win32;
using Screeny.Hotkeys;
using Screeny.Migration;
using Screeny.Settings;

namespace Screeny.Tests.Migration;

public class SetupWizardTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(SetupWizard.Version, false)]
    [InlineData(SetupWizard.Version + 1, false)]
    public void ShouldShowOnStartup_ByVersion(int passed, bool expected)
        => Assert.Equal(expected, SetupWizard.ShouldShowOnStartup(new AppSettings { SetupWizardVersion = passed }));

    [Fact]
    public void PassedFirstVersion_SeesWizardWithFeaturesOnce()
    {
        Assert.True(SetupWizard.Version >= 2); // выбор функций появился во второй версии мастера
        var settings = new AppSettings { SetupWizardVersion = 1 };
        Assert.Equal(SetupWizard.StartupAction.ShowWizard, SetupWizard.OnStartup(isDev: false, wizardRequested: false, settings));
        settings.SetupWizardVersion = SetupWizard.Version;
        Assert.Equal(SetupWizard.StartupAction.CheckForNewConflicts, SetupWizard.OnStartup(isDev: false, wizardRequested: false, settings));
    }

    private static IReadOnlyList<Conflict> Conflicts() => ConflictDetector.Detect(FakeEnvironmentSnapshot.Conflicts(), ConflictCatalog.Default);

    [Fact]
    public void NewBlockingConflicts_OnlyUnknownAndBlockingEnabledHotkey()
    {
        var hotkeys = new[] { HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(false) };
        // Snipaste хоткеи Screeny не занимает — не напоминаем; Lightshot уже известен.
        var fresh = SetupWizard.NewBlockingConflicts(Conflicts(), hotkeys, ["lightshot"]);
        Assert.Equal(["sharex"], fresh.Select(c => c.Id));
        Assert.Empty(SetupWizard.NewBlockingConflicts(Conflicts(), hotkeys, ["lightshot", "sharex"]));
    }

    [Fact]
    public void NewBlockingConflicts_PrintScreenOff_NoReminder()
    {
        var hotkeys = new[] { HotkeyBinding.PrintScreen(false), HotkeyBinding.WinShiftS(true) };
        Assert.Empty(SetupWizard.NewBlockingConflicts(Conflicts(), hotkeys, []));
    }

    [Fact]
    public void NotificationText_NamesKeyAndProgram()
    {
        var fresh = Conflicts().Where(c => c.Id == "lightshot").ToList();
        var text = SetupWizard.NotificationText(fresh, [HotkeyBinding.PrintScreen(true)]);
        Assert.StartsWith("PrintScreen занят программой Lightshot — открыть мастер?", text);
    }

    [Theory]
    [InlineData(true, false, 0, SetupWizard.StartupAction.Nothing)]
    [InlineData(true, true, 1, SetupWizard.StartupAction.ShowWizard)]
    [InlineData(false, false, 0, SetupWizard.StartupAction.ShowWizard)]
    [InlineData(false, false, SetupWizard.Version, SetupWizard.StartupAction.CheckForNewConflicts)]
    [InlineData(false, true, SetupWizard.Version, SetupWizard.StartupAction.ShowWizard)]
    public void OnStartup_Decision(bool isDev, bool requested, int passed, SetupWizard.StartupAction expected)
        => Assert.Equal(expected, SetupWizard.OnStartup(isDev, requested, new AppSettings { SetupWizardVersion = passed }));

    [Fact]
    public void CheckForNewConflicts_FixedThenReturned_NotifiesOnceAgain()
    {
        var hotkeys = new[] { HotkeyBinding.PrintScreen(true) };
        var withLightshot = Conflicts().Where(c => c.Id == "lightshot").ToList();
        // После мастера Lightshot ещё мешал — известен, уведомления нет.
        var (fresh, known) = SetupWizard.CheckForNewConflicts(withLightshot, hotkeys, ["lightshot"]);
        Assert.Empty(fresh);
        Assert.Equal(["lightshot"], known);
        // Его убрали — забываем.
        (fresh, known) = SetupWizard.CheckForNewConflicts([], hotkeys, known);
        Assert.Empty(fresh);
        Assert.Empty(known);
        // Вернулся — одно уведомление…
        (fresh, known) = SetupWizard.CheckForNewConflicts(withLightshot, hotkeys, known);
        Assert.Equal(["lightshot"], fresh.Select(c => c.Id));
        Assert.Equal(["lightshot"], known);
        // …и не при каждом запуске.
        (fresh, _) = SetupWizard.CheckForNewConflicts(withLightshot, hotkeys, known);
        Assert.Empty(fresh);
    }

    [Fact]
    public void BuildHotkeys_SetsCheckboxes_AddsCustom_KeepsOtherCustoms()
    {
        var current = new List<HotkeyBinding> { HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(false), HotkeyBinding.Custom("Ctrl+Shift+X", false) };
        Assert.True(HotkeyParser.TryParse("Ctrl+Alt+Q", out var spec));
        var list = SetupWizard.BuildHotkeys(current, new WizardHotkeyChoice(PrintScreen: false, WinShiftS: true, Custom: spec));
        Assert.Equal(HotkeyBinding.PrintScreen(false), list[0]);
        Assert.Equal(HotkeyBinding.WinShiftS(true), list[1]);
        Assert.Contains(HotkeyBinding.Custom("Ctrl+Shift+X", false), list);
        Assert.Contains(list, b => b.Kind == HotkeyKind.Custom && b.ToRegisterSpec() == spec && b.Enabled);
    }

    [Fact]
    public void BuildHotkeys_ExistingCustom_IsReEnabledNotDuplicated()
    {
        var current = new List<HotkeyBinding> { HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(false), HotkeyBinding.Custom("Ctrl+Shift+X", false) };
        Assert.True(HotkeyParser.TryParse("Ctrl+Shift+X", out var spec));
        var list = SetupWizard.BuildHotkeys(current, new WizardHotkeyChoice(true, false, spec));
        Assert.Single(list, b => b.Kind == HotkeyKind.Custom);
        Assert.True(list.Single(b => b.Kind == HotkeyKind.Custom).Enabled);
    }

    [Fact]
    public void BuildHotkeys_NothingChosen_NormalizesToPrintScreen()
    {
        var list = SetupWizard.BuildHotkeys(AppSettings.DefaultHotkeys(), new WizardHotkeyChoice(false, false, null));
        Assert.True(list[0].Enabled);
        Assert.False(SetupWizard.HasAnyHotkey(new WizardHotkeyChoice(false, false, null), AppSettings.DefaultHotkeys()));
    }

    /// <summary>Применение хоткеев из мастера через настоящий <see cref="HotkeyManager"/> на подставном бэкенде.</summary>
    [Fact]
    public void WizardHotkeys_ApplyThroughHotkeyManager()
    {
        var backend = new RecordingBackend();
        using var manager = new HotkeyManager(backend, allowRegisterHotKey: true, allowHook: true);
        manager.Apply(AppSettings.DefaultHotkeys(), strict: false);
        Assert.True(HotkeyParser.TryParse("Ctrl+Alt+Q", out var spec));
        var list = SetupWizard.BuildHotkeys(AppSettings.DefaultHotkeys(), new WizardHotkeyChoice(true, true, spec));
        var result = manager.Apply(list, strict: true);
        Assert.True(result.Success);
        Assert.All(result.Report.Entries, e => Assert.Equal(HotkeyStatus.Active, e.Status));
        Assert.True(manager.IsHookInstalled);
        Assert.Contains(spec, backend.Registered);
    }

    [Fact]
    public void KnownConflicts_NormalizeDropsNullsAndDuplicates()
    {
        var s = new AppSettings { KnownConflicts = null! }.Normalize();
        Assert.Empty(s.KnownConflicts);
        s = new AppSettings { KnownConflicts = ["lightshot", "lightshot", " ", "sharex"] }.Normalize();
        Assert.Equal(["lightshot", "sharex"], s.KnownConflicts);
    }

    private sealed class RecordingBackend : IHotkeyBackend
    {
        public List<HotkeySpec> Registered { get; } = new();

        public bool TryRegister(HotkeySpec spec, Action pressed, out IDisposable? handle, out int win32Error)
        {
            Registered.Add(spec);
            handle = new Noop();
            win32Error = 0;
            return true;
        }

        public bool TryInstallWinShiftSHook(Action triggered, out IDisposable? hook, out int win32Error)
        {
            hook = new Noop();
            win32Error = 0;
            return true;
        }

        public bool IsPrintScreenReservedBySystem() => false;

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}

public class StartupApprovedTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(new byte[0], true)]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 6, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 3, 0, 0, 0, 0x39, 0x96, 0x33, 0xF4, 0x2A, 0xD6, 0xD9, 0x01 }, false)]
    [InlineData(new byte[] { 1, 0, 0, 0 }, false)]
    public void IsEnabled(byte[]? value, bool expected) => Assert.Equal(expected, StartupApproved.IsEnabled(value));

    [Fact]
    public void DisabledValue_IsTaskManagerFormat()
    {
        var now = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        var value = StartupApproved.DisabledValue(now);
        Assert.Equal(12, value.Length);
        Assert.Equal([3, 0, 0, 0], value[..4]);
        Assert.Equal(now.ToFileTimeUtc(), BitConverter.ToInt64(value, 4));
        Assert.False(StartupApproved.IsEnabled(value));
    }

    [Theory]
    [InlineData(AutostartLocation.RunUser, RegistryHive.CurrentUser, "Run")]
    [InlineData(AutostartLocation.RunMachine64, RegistryHive.LocalMachine, "Run")]
    [InlineData(AutostartLocation.RunMachine32, RegistryHive.LocalMachine, "Run32")]
    [InlineData(AutostartLocation.StartupFolderUser, RegistryHive.CurrentUser, "StartupFolder")]
    [InlineData(AutostartLocation.StartupFolderCommon, RegistryHive.LocalMachine, "StartupFolder")]
    public void KeyFor(AutostartLocation location, RegistryHive hive, string leaf)
        => Assert.Equal((hive, StartupApproved.BaseKey + "\\" + leaf), StartupApproved.KeyFor(location));
}

public class UninstallCommandTests
{
    [Theory]
    [InlineData("\"C:\\Program Files (x86)\\Skillbrains\\lightshot\\unins000.exe\"", @"C:\Program Files (x86)\Skillbrains\lightshot\unins000.exe", "")]
    [InlineData("\"C:\\P\\setup.exe\"  /uninstall", @"C:\P\setup.exe", "/uninstall")]
    [InlineData(@"C:\Program Files\PicPick\uninst.exe /S", @"C:\Program Files\PicPick\uninst.exe", "/S")]
    [InlineData("MsiExec.exe /X{70316E2B-A36C-4CF2-A14C-431BEC482141}", "msiexec.exe", "/X{70316E2B-A36C-4CF2-A14C-431BEC482141}")]
    [InlineData("MsiExec.exe /I{70316E2B-A36C-4CF2-A14C-431BEC482141} /qn", "msiexec.exe", "/X{70316E2B-A36C-4CF2-A14C-431BEC482141}")]
    public void Parse(string command, string file, string args)
        => Assert.Equal((file, args), UninstallCommand.Parse(command));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rundll32")]
    [InlineData("\"C:\\x\\unins000.exe")]
    [InlineData("MsiExec.exe /X{not-a-guid}")]
    public void Parse_Rejects(string? command) => Assert.Null(UninstallCommand.Parse(command));
}
