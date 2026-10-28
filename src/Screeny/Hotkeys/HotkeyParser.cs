using System.Windows.Input;

namespace Screeny.Hotkeys;

public static class HotkeyParser
{
    private static readonly Dictionary<string, HotkeyModifiers> ModNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = HotkeyModifiers.Control, ["control"] = HotkeyModifiers.Control,
        ["alt"] = HotkeyModifiers.Alt, ["shift"] = HotkeyModifiers.Shift,
        ["win"] = HotkeyModifiers.Win, ["windows"] = HotkeyModifiers.Win,
    };

    private static readonly Dictionary<string, Key> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PrintScreen"] = Key.PrintScreen, ["PrtSc"] = Key.PrintScreen, ["Snapshot"] = Key.PrintScreen,
        ["Esc"] = Key.Escape, ["Ins"] = Key.Insert, ["Del"] = Key.Delete,
    };

    public static bool TryParse(string text, out HotkeySpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var mods = HotkeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!ModNames.TryGetValue(parts[i], out var m)) return false;
            mods |= m;
        }
        var last = parts[^1];
        if (ModNames.ContainsKey(last)) return false;
        if (last.Length == 1 && char.IsAsciiDigit(last[0])) last = "D" + last; // "5" → Key.D5, а не числовое значение enum
        else if (last.All(char.IsAsciiDigit)) return false;
        if (!KeyAliases.TryGetValue(last, out var key) && !Enum.TryParse(last, true, out key)) return false;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return false;
        spec = new HotkeySpec(mods, vk);
        return true;
    }

    public static string KeyName(int vk)
    {
        var key = KeyInterop.KeyFromVirtualKey(vk);
        return key == Key.PrintScreen ? "PrintScreen" : key.ToString();
    }
}
