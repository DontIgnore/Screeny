using System.Windows.Input;
using Screeny.Hotkeys;

namespace Screeny.Tests.Hotkeys;

public class HotkeyRulesTests
{
    [Theory]
    [InlineData(Key.X, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+X")]
    [InlineData(Key.PrintScreen, ModifierKeys.None, "PrintScreen")]
    [InlineData(Key.F9, ModifierKeys.Alt, "Alt+F9")]
    [InlineData(Key.D1, ModifierKeys.Windows | ModifierKeys.Control, "Ctrl+Win+D1")]
    public void FromKeys_BuildsSpec(Key key, ModifierKeys mods, string expected)
        => Assert.Equal(expected, HotkeyRules.FromKeys(key, mods)?.ToString());

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightShift)]
    [InlineData(Key.LWin)]
    [InlineData(Key.LeftAlt)]
    public void FromKeys_IgnoresBareModifiers(Key key) => Assert.Null(HotkeyRules.FromKeys(key, ModifierKeys.Control));

    [Theory]
    [InlineData("Win+Shift+S")]   // system screen capture
    [InlineData("S")]             // would block letter input everywhere
    [InlineData("Shift+A")]       // also normal text input
    [InlineData("Enter")]
    public void Validate_RejectsReservedAndTypingKeys(string text)
    {
        Assert.True(HotkeyParser.TryParse(text, out var spec));
        Assert.NotNull(HotkeyRules.Validate(spec));
    }

    [Theory]
    [InlineData("PrintScreen")]
    [InlineData("F8")]
    [InlineData("Ctrl+Alt+S")]
    [InlineData("Ctrl+Shift+2")]
    public void Validate_AcceptsUsableHotkeys(string text)
    {
        Assert.True(HotkeyParser.TryParse(text, out var spec));
        Assert.Null(HotkeyRules.Validate(spec));
    }
}
