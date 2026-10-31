namespace Screeny.Hotkeys;

[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Win = 8 }

public readonly record struct HotkeySpec(HotkeyModifiers Modifiers, int VirtualKey)
{
    /// <summary>PrintScreen without modifiers (VK_SNAPSHOT).</summary>
    public static readonly HotkeySpec PrintScreen = new(HotkeyModifiers.None, 0x2C);
    /// <summary>Fallback hotkey when PrintScreen unavailable and nothing else works.</summary>
    public static readonly HotkeySpec Fallback = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x53);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(HotkeyParser.KeyName(VirtualKey));
        return string.Join("+", parts);
    }
}
