using Screeny.Hotkeys;

namespace Screeny.Tests.Hotkeys;

public class HotkeyParserTests
{
    [Theory]
    [InlineData("PrintScreen", HotkeyModifiers.None, 0x2C)]
    [InlineData("Ctrl+Shift+X", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x58)]
    [InlineData("ctrl + alt + s", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x53)]
    [InlineData("Win+F1", HotkeyModifiers.Win, 0x70)]
    public void Parse_Valid(string text, HotkeyModifiers mods, int vk)
    {
        Assert.True(HotkeyParser.TryParse(text, out var spec));
        Assert.Equal(mods, spec.Modifiers);
        Assert.Equal(vk, spec.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Banana")]
    public void Parse_Invalid(string text) => Assert.False(HotkeyParser.TryParse(text, out _));

    [Fact]
    public void ToString_RoundTrips()
    {
        HotkeyParser.TryParse("Ctrl+Shift+X", out var spec);
        Assert.Equal("Ctrl+Shift+X", spec.ToString());
    }
}
