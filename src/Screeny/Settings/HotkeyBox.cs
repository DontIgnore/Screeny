using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Screeny.Hotkeys;

namespace Screeny.Settings;

/// <summary>Hotkey entry field: press combo — displays; Backspace clears.</summary>
public sealed class HotkeyBox : TextBox
{
    private HotkeySpec? _spec;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Arrow;
        InputMethod.SetIsInputMethodEnabled(this, false);
    }

    public event Action? SpecChanged;

    public HotkeySpec? Spec
    {
        get => _spec;
        set { _spec = value; Text = value?.ToString() ?? ""; SpecChanged?.Invoke(); }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        var mods = Keyboard.Modifiers;
        if (key is Key.Tab or Key.Escape && mods == ModifierKeys.None) return; // navigation and window close
        e.Handled = true;
        if (key is Key.Back or Key.Delete && mods == ModifierKeys.None) { Spec = null; return; }
        if (HotkeyRules.FromKeys(key, mods) is { } spec) Spec = spec;
    }

    /// <summary>PrintScreen system delivers to app only as KeyUp.</summary>
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.Snapshot) { e.Handled = true; Spec = HotkeyRules.FromKeys(Key.Snapshot, Keyboard.Modifiers); }
    }
}
