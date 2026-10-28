using Screeny.Hotkeys;

namespace Screeny.Tests.Hotkeys;

public class GlobalHotkeyTests
{
    private static readonly HotkeySpec CtrlAltS = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x53);

    [Fact]
    public void IsAlreadyRegistered_OnlyFor1409()
    {
        Assert.True(GlobalHotkey.IsAlreadyRegistered(GlobalHotkey.ErrorHotkeyAlreadyRegistered));
        Assert.False(GlobalHotkey.IsAlreadyRegistered(5));
        Assert.False(GlobalHotkey.IsAlreadyRegistered(0));
    }

    [Fact]
    public void DescribeFailure_Busy_SaysTakenByAnotherApp()
        => Assert.Contains("занят", GlobalHotkey.DescribeFailure(CtrlAltS, 1409));

    [Fact]
    public void DescribeFailure_OtherError_IncludesCode()
    {
        var text = GlobalHotkey.DescribeFailure(CtrlAltS, 87);
        Assert.Contains("87", text);
        Assert.DoesNotContain("занят", text);
    }
}
