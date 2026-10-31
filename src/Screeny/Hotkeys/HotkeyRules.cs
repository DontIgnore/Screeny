using System.Windows.Input;

namespace Screeny.Hotkeys;

/// <summary>Build hotkey from press in settings field and check it's reasonable to assign globally.</summary>
public static class HotkeyRules
{
    private static readonly HashSet<Key> ModifierKeySet =
    [
        Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt, Key.RightAlt, Key.LeftShift, Key.RightShift, Key.LWin, Key.RWin, Key.System,
    ];

    /// <summary>Keys assignable without Ctrl/Alt/Win: they are not used while typing text.</summary>
    private static bool IsStandaloneKey(Key key)
        => key is Key.PrintScreen or Key.Pause or Key.Scroll or (>= Key.F1 and <= Key.F24);

    /// <summary>Hotkey from key and modifiers or null if only modifier pressed.</summary>
    public static HotkeySpec? FromKeys(Key key, ModifierKeys modifiers)
    {
        if (ModifierKeySet.Contains(key) || key == Key.None) return null;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return null;
        var mods = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        return new HotkeySpec(mods, vk);
    }

    /// <summary>Error text for user or null if hotkey valid.</summary>
    public static string? Validate(HotkeySpec spec)
    {
        var key = KeyInterop.KeyFromVirtualKey(spec.VirtualKey);
        if (spec.Modifiers == (HotkeyModifiers.Win | HotkeyModifiers.Shift) && key == Key.S)
            return Localization.Loc.T("Hotkeys.WinShiftSSeparate");
        bool hasStrongModifier = (spec.Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0;
        if (!hasStrongModifier && !IsStandaloneKey(key))
            return Localization.Loc.T("Hotkeys.NeedModifier");
        return null;
    }
}
