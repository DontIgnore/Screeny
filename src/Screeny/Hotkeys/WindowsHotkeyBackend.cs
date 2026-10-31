using System.Windows.Threading;

namespace Screeny.Hotkeys;

/// <summary>Real RegisterHotKey and low-level hook. Created in UI thread.</summary>
public sealed class WindowsHotkeyBackend(Dispatcher dispatcher) : IHotkeyBackend
{
    public bool TryRegister(HotkeySpec spec, Action pressed, out IDisposable? handle, out int win32Error)
    {
        bool ok = GlobalHotkey.TryRegister(spec, out var hotkey, out win32Error);
        if (hotkey is not null) hotkey.Pressed += pressed;
        handle = hotkey;
        return ok;
    }

    public bool TryInstallWinShiftSHook(Action triggered, out IDisposable? hook, out int win32Error)
    {
        // From hook thread — straight to UI thread, without waiting: hook thread must return to its queue.
        var started = LowLevelKeyboardHook.TryStart(() => dispatcher.BeginInvoke(triggered), out win32Error);
        hook = started;
        return started is not null;
    }

    public bool IsPrintScreenReservedBySystem() => PrintScreenPolicy.IsReservedBySystem(HotkeySpec.PrintScreen);
}
