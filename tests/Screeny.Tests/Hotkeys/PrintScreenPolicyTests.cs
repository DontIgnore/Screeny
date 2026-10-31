using Screeny.Hotkeys;

namespace Screeny.Tests.Hotkeys;

public class PrintScreenPolicyTests
{
    [Theory]
    [InlineData(1, 19045, true)]   // explicitly enabled
    [InlineData(0, 26200, false)]  // explicitly disabled
    [InlineData(null, 26200, true)] // Windows 11: PrintScreen opens Snipping Tool by default
    [InlineData(null, 19045, false)] // Windows 10: disabled by default
    public void IsSnippingKeyEnabled(int? registryValue, int osBuild, bool expected)
        => Assert.Equal(expected, PrintScreenPolicy.IsSnippingKeyEnabled(registryValue, osBuild));

    [Fact]
    public void IsReservedBySystem_OnlyForBarePrintScreen()
    {
        HotkeyParser.TryParse("PrintScreen", out var bare);
        HotkeyParser.TryParse("Ctrl+PrintScreen", out var withCtrl);
        Assert.True(PrintScreenPolicy.IsReservedBySystem(bare, snippingKeyEnabled: true));
        Assert.False(PrintScreenPolicy.IsReservedBySystem(bare, snippingKeyEnabled: false));
        Assert.False(PrintScreenPolicy.IsReservedBySystem(withCtrl, snippingKeyEnabled: true));
    }
}
