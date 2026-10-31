using Screeny.Hotkeys;

namespace Screeny.Tests.Hotkeys;

/// <summary>HotkeyManager logic on fake backend: real RegisterHotKey and hook not called in tests.</summary>
public class HotkeyManagerTests
{
    private sealed class FakeHandle(HotkeySpec? spec, Action callback) : IDisposable
    {
        public HotkeySpec? Spec { get; } = spec;
        public Action Callback { get; } = callback;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeBackend : IHotkeyBackend
    {
        public bool Reserved;
        public Dictionary<HotkeySpec, int> Errors = new();
        public int HookError;
        public readonly List<FakeHandle> Handles = new();
        public readonly List<FakeHandle> Hooks = new();
        public int RegisterCalls, HookCalls;

        public bool TryRegister(HotkeySpec spec, Action pressed, out IDisposable? handle, out int win32Error)
        {
            RegisterCalls++;
            if (Errors.TryGetValue(spec, out win32Error)) { handle = null; return false; }
            var h = new FakeHandle(spec, pressed);
            Handles.Add(h);
            handle = h;
            return true;
        }

        public bool TryInstallWinShiftSHook(Action triggered, out IDisposable? hook, out int win32Error)
        {
            HookCalls++;
            win32Error = HookError;
            if (HookError != 0) { hook = null; return false; }
            var h = new FakeHandle(null, triggered);
            Hooks.Add(h);
            hook = h;
            return true;
        }

        public bool IsPrintScreenReservedBySystem() => Reserved;

        public IEnumerable<HotkeySpec> Live => Handles.Where(h => !h.Disposed).Select(h => h.Spec!.Value);
    }

    private static readonly HotkeySpec CtrlShiftX = Parse("Ctrl+Shift+X");
    private static readonly HotkeySpec CtrlQ = Parse("Ctrl+Q");
    private static HotkeySpec Parse(string text) { Assert.True(HotkeyParser.TryParse(text, out var s)); return s; }

    private static HotkeyBinding[] List(bool ps = true, bool wss = false, params string[] customs)
        => [HotkeyBinding.PrintScreen(ps), HotkeyBinding.WinShiftS(wss), .. customs.Select(c => HotkeyBinding.Custom(c))];

    private static (HotkeyManager, FakeBackend) Create(bool allowRegister = true, bool allowHook = true)
    {
        var backend = new FakeBackend();
        return (new HotkeyManager(backend, allowRegister, allowHook), backend);
    }

    [Fact]
    public void Startup_RegistersEveryEnabledHotkey()
    {
        var (m, b) = Create();
        var r = m.Apply(List(ps: true, wss: true, "Ctrl+Shift+X"), strict: false);
        Assert.True(r.Success);
        Assert.Equal([HotkeySpec.PrintScreen, CtrlShiftX], b.Live);
        Assert.Single(b.Hooks);
        Assert.Equal(["PrintScreen", "Win+Shift+S", "Ctrl+Shift+X"], r.Report.ActiveNames);
        Assert.Null(r.Report.Fallback);
        Assert.Null(r.Report.ProblemSummary());
    }

    [Fact]
    public void DisabledEntries_AreNotRegistered()
    {
        var (m, b) = Create();
        var r = m.Apply([HotkeyBinding.PrintScreen(false), HotkeyBinding.WinShiftS(false), HotkeyBinding.Custom("Ctrl+Q")], strict: false);
        Assert.Equal([CtrlQ], b.Live);
        Assert.Empty(b.Hooks);
        Assert.Equal(HotkeyStatus.Disabled, r.Report.Find(HotkeyKind.PrintScreen)!.Status);
    }

    [Fact]
    public void AnyHandle_RaisesPressed()
    {
        var (m, b) = Create();
        int pressed = 0;
        m.Pressed += () => pressed++;
        m.Apply(List(ps: true, wss: true, "Ctrl+Q"), strict: false);
        foreach (var h in b.Handles) h.Callback();
        b.Hooks[0].Callback();
        Assert.Equal(3, pressed);
    }

    [Fact]
    public void PrintScreenReserved_AndNothingElse_RegistersFallback()
    {
        var (m, b) = Create();
        b.Reserved = true;
        var r = m.Apply(List(), strict: false);
        Assert.Equal([HotkeySpec.Fallback], b.Live); // PrintScreen not even registered under system capture
        Assert.Equal(HotkeyStatus.ReservedBySystem, r.Report.Find(HotkeyKind.PrintScreen)!.Status);
        Assert.Equal(HotkeySpec.Fallback, r.Report.Fallback);
        Assert.Equal(["Ctrl+Alt+S"], r.Report.ActiveNames);
        Assert.Contains("Используется Ctrl+Alt+S", r.Report.ProblemSummary());
    }

    [Fact]
    public void PrintScreenBusy_AndNothingElse_RegistersFallback()
    {
        var (m, b) = Create();
        b.Errors[HotkeySpec.PrintScreen] = GlobalHotkey.ErrorHotkeyAlreadyRegistered;
        var r = m.Apply(List(), strict: false);
        Assert.Equal(HotkeyStatus.Busy, r.Report.Find(HotkeyKind.PrintScreen)!.Status);
        Assert.Equal(HotkeySpec.Fallback, r.Report.Fallback);
    }

    [Fact]
    public void PrintScreenFailed_WithOtherCode_IsFailedAndFallsBack()
    {
        var (m, b) = Create();
        b.Errors[HotkeySpec.PrintScreen] = 5;
        var r = m.Apply(List(), strict: false);
        var ps = r.Report.Find(HotkeyKind.PrintScreen)!;
        Assert.Equal(HotkeyStatus.Failed, ps.Status);
        Assert.Equal(5, ps.Win32Error);
        Assert.Equal(HotkeySpec.Fallback, r.Report.Fallback);
    }

    [Fact]
    public void PrintScreenReserved_ButWinShiftSWorks_NoFallbackAndNoNotification()
    {
        var (m, b) = Create();
        b.Reserved = true;
        var r = m.Apply(List(ps: true, wss: true), strict: false);
        Assert.Null(r.Report.Fallback);
        Assert.Empty(b.Live);
        Assert.Null(r.Report.ProblemSummary());
    }

    [Fact]
    public void PrintScreenDisabled_NeverFallsBack()
    {
        var (m, b) = Create();
        b.Errors[CtrlQ] = GlobalHotkey.ErrorHotkeyAlreadyRegistered;
        var r = m.Apply([HotkeyBinding.PrintScreen(false), HotkeyBinding.WinShiftS(false), HotkeyBinding.Custom("Ctrl+Q")], strict: false);
        Assert.Null(r.Report.Fallback);
        Assert.Empty(r.Report.ActiveNames);
        Assert.Contains("Скриншот — по клику", r.Report.ProblemSummary());
    }

    [Fact]
    public void FallbackAlsoBusy_ReportsBothAndTray()
    {
        var (m, b) = Create();
        b.Reserved = true;
        b.Errors[HotkeySpec.Fallback] = GlobalHotkey.ErrorHotkeyAlreadyRegistered;
        var r = m.Apply(List(), strict: false);
        Assert.Null(r.Report.Fallback);
        Assert.NotNull(r.Report.FallbackError);
        var text = r.Report.ProblemSummary()!;
        Assert.Contains("PrintScreen", text);
        Assert.Contains("Ctrl+Alt+S", text);
        Assert.Contains("по клику", text);
    }

    [Fact]
    public void Startup_CollectsAllProblemsInOneSummary()
    {
        var (m, b) = Create();
        b.Errors[CtrlQ] = GlobalHotkey.ErrorHotkeyAlreadyRegistered;
        b.HookError = 5;
        var r = m.Apply(List(ps: true, wss: true, "Ctrl+Q"), strict: false);
        Assert.True(r.Success);
        Assert.Equal(["PrintScreen"], r.Report.ActiveNames);
        var text = r.Report.ProblemSummary()!;
        Assert.Contains("Ctrl+Q", text);
        Assert.Contains("Win+Shift+S", text);
    }

    [Fact]
    public void ReapplySameList_ReusesHandles()
    {
        var (m, b) = Create();
        m.Apply(List(ps: true, wss: true, "Ctrl+Q"), strict: false);
        int calls = b.RegisterCalls;
        var r = m.Apply(List(ps: true, wss: true, "Ctrl+Q"), strict: true);
        Assert.True(r.Success);
        Assert.Equal(calls, b.RegisterCalls); // own combo not re-registered (would give 1409)
        Assert.Equal(1, b.HookCalls);
        Assert.All(b.Handles, h => Assert.False(h.Disposed));
        Assert.False(b.Hooks[0].Disposed);
    }

    [Fact]
    public void RemovingEntries_UnregistersThem()
    {
        var (m, b) = Create();
        m.Apply(List(ps: true, wss: true, "Ctrl+Q", "Ctrl+Shift+X"), strict: false);
        var r = m.Apply(List(ps: true, wss: false, "Ctrl+Shift+X"), strict: true);
        Assert.True(r.Success);
        Assert.Equal([HotkeySpec.PrintScreen, CtrlShiftX], b.Live);
        Assert.True(b.Hooks[0].Disposed);
        Assert.False(m.IsHookInstalled);
    }

    [Fact]
    public void Strict_NewBusyCustom_RollsBackEverything()
    {
        var (m, b) = Create();
        m.Apply(List(ps: true, wss: false, "Ctrl+Q"), strict: false);
        var before = m.Report;
        b.Errors[CtrlShiftX] = GlobalHotkey.ErrorHotkeyAlreadyRegistered;

        var r = m.Apply(List(ps: false, wss: true, "Ctrl+Shift+X"), strict: true);

        Assert.False(r.Success);
        Assert.Equal(HotkeyKind.Custom, r.Error!.Binding.Kind);
        Assert.Equal(HotkeyStatus.Busy, r.Error.Status);
        Assert.Contains("занят", r.Error.Message);
        Assert.Same(before, m.Report);
        Assert.Equal([HotkeySpec.PrintScreen, CtrlQ], b.Live); // old keep working
        Assert.True(b.Hooks.Single().Disposed);                // new hook removed
        Assert.False(m.IsHookInstalled);
    }

    [Fact]
    public void Strict_NewHookFailure_RollsBack()
    {
        var (m, b) = Create();
        m.Apply(List(), strict: false);
        b.HookError = 5;
        var r = m.Apply(List(ps: true, wss: true, "Ctrl+Q"), strict: true);
        Assert.False(r.Success);
        Assert.Equal(HotkeyKind.WinShiftS, r.Error!.Binding.Kind);
        Assert.Contains("5", r.Error.Message);
        Assert.Equal([HotkeySpec.PrintScreen], b.Live); // Ctrl+Q registered in this attempt removed
    }

    [Fact]
    public void Strict_EntryThatAlreadyFailed_DoesNotBlock()
    {
        var (m, b) = Create();
        b.Errors[CtrlQ] = GlobalHotkey.ErrorHotkeyAlreadyRegistered;
        m.Apply(List(ps: true, wss: false, "Ctrl+Q"), strict: false);
        var r = m.Apply(List(ps: true, wss: false, "Ctrl+Q", "Ctrl+Shift+X"), strict: true);
        Assert.True(r.Success);
        Assert.Equal(HotkeyStatus.Busy, r.Report.Entries.Single(e => e.Binding.Spec == "Ctrl+Q").Status);
        Assert.Contains(CtrlShiftX, b.Live);
    }

    [Fact]
    public void Strict_ReservedPrintScreen_IsNotAnErrorAndFallsBack()
    {
        var (m, b) = Create();
        m.Apply([HotkeyBinding.PrintScreen(false), HotkeyBinding.WinShiftS(false), HotkeyBinding.Custom("Ctrl+Q")], strict: false);
        b.Reserved = true;
        var r = m.Apply(List(), strict: true);
        Assert.True(r.Success);
        Assert.Equal(HotkeySpec.Fallback, r.Report.Fallback);
        Assert.Equal([HotkeySpec.Fallback], b.Live);
    }

    [Fact]
    public void Retry_AfterWindowsReleasesPrintScreen_DropsFallback()
    {
        var (m, b) = Create();
        b.Reserved = true;
        m.Apply(List(), strict: false);
        b.Reserved = false;
        var r = m.Apply(List(), strict: true);
        Assert.Null(r.Report.Fallback);
        Assert.Equal([HotkeySpec.PrintScreen], b.Live);
    }

    [Fact]
    public void CustomEqualToFallback_ReusesFallbackHandle()
    {
        var (m, b) = Create();
        b.Reserved = true;
        m.Apply(List(), strict: false);
        var fallbackHandle = b.Handles.Single();
        var r = m.Apply(List(ps: true, wss: false, "Ctrl+Alt+S"), strict: true);
        Assert.True(r.Success);
        Assert.Null(r.Report.Fallback); // own combo works — fallback not needed
        Assert.False(fallbackHandle.Disposed);
        Assert.Single(b.Handles);
        Assert.Equal(["Ctrl+Alt+S"], r.Report.ActiveNames);
    }

    [Fact]
    public void DevMode_RegistersNothingAndNeverFallsBack()
    {
        var (m, b) = Create(allowRegister: false, allowHook: false);
        b.Reserved = true;
        var r = m.Apply(List(ps: true, wss: true, "Ctrl+Q"), strict: true);
        Assert.True(r.Success);
        Assert.Equal(0, b.RegisterCalls);
        Assert.Equal(0, b.HookCalls);
        Assert.All(r.Report.Entries.Where(e => e.Binding.Enabled), e => Assert.Equal(HotkeyStatus.NotAllowed, e.Status));
        Assert.Null(r.Report.Fallback);
        Assert.Null(r.Report.ProblemSummary());
    }

    [Fact]
    public void DevWithLlHookFlag_InstallsOnlyTheHook()
    {
        var (m, b) = Create(allowRegister: false, allowHook: true);
        var r = m.Apply(List(ps: true, wss: true), strict: false);
        Assert.Equal(0, b.RegisterCalls);
        Assert.Single(b.Hooks);
        Assert.Equal(["Win+Shift+S"], r.Report.ActiveNames);
    }

    [Fact]
    public void Dispose_ReleasesEverything()
    {
        var (m, b) = Create();
        b.Reserved = false;
        m.Apply(List(ps: true, wss: true, "Ctrl+Q"), strict: false);
        m.Dispose();
        Assert.All(b.Handles, h => Assert.True(h.Disposed));
        Assert.True(b.Hooks[0].Disposed);
    }

    [Theory]
    [InlineData(new string[0], "—")]
    [InlineData(new[] { "PrintScreen" }, "PrintScreen")]
    [InlineData(new[] { "PrintScreen", "Win+Shift+S" }, "PrintScreen / Win+Shift+S")]
    [InlineData(new[] { "PrintScreen", "Win+Shift+S", "Ctrl+Shift+X" }, "PrintScreen / …")]
    [InlineData(new[] { "Ctrl+Shift+Alt+F12" }, "Ctrl+Shift+Alt+F12")]
    public void FormatNames(string[] names, string expected) => Assert.Equal(expected, HotkeyReport.FormatNames(names));
}
