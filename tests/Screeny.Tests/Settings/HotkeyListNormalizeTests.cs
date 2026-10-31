using Screeny.Hotkeys;
using Screeny.Settings;

namespace Screeny.Tests.Settings;

public class HotkeyListNormalizeTests
{
    private static List<HotkeyBinding> N(params HotkeyBinding?[] items) => AppSettings.NormalizeHotkeys(items);

    [Fact]
    public void Defaults_PrintScreenOnWinShiftSOff()
        => Assert.Equal([HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(false)], new AppSettings().Hotkeys);

    [Fact]
    public void Empty_EnablesPrintScreen()
        => Assert.Equal([HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(false)], N());

    [Fact]
    public void Null_EnablesPrintScreen()
        => Assert.Equal([HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(false)], AppSettings.NormalizeHotkeys(null));

    [Fact]
    public void AllDisabled_EnablesPrintScreen()
        => Assert.Equal([HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(false), HotkeyBinding.Custom("Ctrl+Q", false)],
            N(HotkeyBinding.PrintScreen(false), HotkeyBinding.WinShiftS(false), HotkeyBinding.Custom("Ctrl+Q", false)));

    [Fact]
    public void OnlyCustomEnabled_LeavesPrintScreenOff()
        => Assert.Equal([HotkeyBinding.PrintScreen(false), HotkeyBinding.WinShiftS(false), HotkeyBinding.Custom("Ctrl+Q")],
            N(HotkeyBinding.Custom("Ctrl+Q")));

    [Fact]
    public void Order_IsPrintScreenWinShiftSThenCustomsInOrder()
        => Assert.Equal([HotkeyBinding.PrintScreen(false), HotkeyBinding.WinShiftS(true), HotkeyBinding.Custom("Ctrl+B"), HotkeyBinding.Custom("Ctrl+A")],
            N(HotkeyBinding.Custom("Ctrl+B"), HotkeyBinding.WinShiftS(true), HotkeyBinding.Custom("Ctrl+A")));

    [Fact]
    public void DuplicateKinds_CollapseAndAnyEnabledWins()
        => Assert.Equal([HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(true)],
            N(HotkeyBinding.PrintScreen(false), HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(true), HotkeyBinding.WinShiftS(false)));

    [Fact]
    public void DuplicateCustoms_FirstKept_ComparedCanonically()
        => Assert.Equal([HotkeyBinding.PrintScreen(false), HotkeyBinding.WinShiftS(false), HotkeyBinding.Custom("Ctrl+Shift+X")],
            N(HotkeyBinding.Custom("shift+ctrl+x"), HotkeyBinding.Custom("Ctrl+Shift+X", false)));

    [Theory]
    [InlineData("Nonsense+Q")]
    [InlineData("A")]
    [InlineData("Shift+A")]
    [InlineData("Win+Shift+S")] // separate WinShiftS entry for it
    [InlineData("")]
    public void InvalidCustom_IsDropped(string spec)
        => Assert.Equal([HotkeyBinding.PrintScreen(false), HotkeyBinding.WinShiftS(false), HotkeyBinding.Custom("Ctrl+Q")],
            N(HotkeyBinding.Custom(spec), HotkeyBinding.Custom("Ctrl+Q")));

    [Fact]
    public void CustomWithoutSpec_IsDropped()
        => Assert.Equal([HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(false)], N(new HotkeyBinding { Kind = HotkeyKind.Custom, Enabled = true }));

    [Fact]
    public void CustomPrintScreen_BecomesPrintScreenEntry()
        => Assert.Equal([HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(true)],
            N(HotkeyBinding.WinShiftS(true), HotkeyBinding.Custom("PrtSc")));

    [Fact]
    public void UnknownKind_IsDropped()
        => Assert.Equal([HotkeyBinding.PrintScreen(false), HotkeyBinding.WinShiftS(true)],
            N(new HotkeyBinding { Kind = (HotkeyKind)42, Enabled = true }, HotkeyBinding.WinShiftS(true)));

    [Fact]
    public void SetHotkeyEnabled_TogglesKindAndKeepsCustoms()
    {
        var s = new AppSettings { Hotkeys = [HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(false), HotkeyBinding.Custom("Ctrl+Q")] };
        s.SetHotkeyEnabled(HotkeyKind.WinShiftS, true);
        s.SetHotkeyEnabled(HotkeyKind.PrintScreen, false);
        Assert.Equal([HotkeyBinding.PrintScreen(false), HotkeyBinding.WinShiftS(true), HotkeyBinding.Custom("Ctrl+Q")], s.Hotkeys);
        Assert.True(s.IsHotkeyEnabled(HotkeyKind.WinShiftS));
        Assert.False(s.IsHotkeyEnabled(HotkeyKind.PrintScreen));
    }

    [Fact]
    public void SetHotkeyEnabled_DisablingLastOne_KeepsPrintScreenOn()
    {
        var s = new AppSettings();
        s.SetHotkeyEnabled(HotkeyKind.PrintScreen, false);
        Assert.True(s.IsHotkeyEnabled(HotkeyKind.PrintScreen));
    }

    [Fact]
    public void SetHotkeyEnabled_Custom_Throws()
        => Assert.Throws<ArgumentException>(() => new AppSettings().SetHotkeyEnabled(HotkeyKind.Custom, true));
}
