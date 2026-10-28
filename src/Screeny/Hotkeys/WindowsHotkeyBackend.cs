using System.Windows.Threading;

namespace Screeny.Hotkeys;

/// <summary>Настоящие RegisterHotKey и низкоуровневый хук. Создаётся в UI-потоке.</summary>
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
        // Из потока хука — сразу в UI-поток, не дожидаясь: поток хука должен вернуться к своей очереди.
        var started = LowLevelKeyboardHook.TryStart(() => dispatcher.BeginInvoke(triggered), out win32Error);
        hook = started;
        return started is not null;
    }

    public bool IsPrintScreenReservedBySystem() => PrintScreenPolicy.IsReservedBySystem(HotkeySpec.PrintScreen);
}
