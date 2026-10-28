using Screeny.Hotkeys;

namespace Screeny.Tests.Hotkeys;

public class PrintScreenPolicyTests
{
    [Theory]
    [InlineData(1, 19045, true)]   // явно включено
    [InlineData(0, 26200, false)]  // явно выключено
    [InlineData(null, 26200, true)] // Windows 11: по умолчанию PrintScreen открывает Snipping Tool
    [InlineData(null, 19045, false)] // Windows 10: по умолчанию выключено
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
