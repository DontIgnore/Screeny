using System.Runtime.InteropServices;
using System.Windows.Interop;
using Screeny.Native;

namespace Screeny.Hotkeys;

/// <summary>Регистрирует глобальный хоткей на message-only окне. Вызывать из UI-потока.</summary>
public sealed class GlobalHotkey : IDisposable
{
    /// <summary>ERROR_HOTKEY_ALREADY_REGISTERED: сочетание уже занято другим приложением.</summary>
    public const int ErrorHotkeyAlreadyRegistered = 1409;

    private static int _nextId = 0xB000;
    private readonly HwndSource _source;
    private readonly int _id;
    public HotkeySpec Spec { get; }
    public event Action? Pressed;

    private GlobalHotkey(HotkeySpec spec, HwndSource source, int id) { Spec = spec; _source = source; _id = id; source.AddHook(WndProc); }

    public static bool TryRegister(HotkeySpec spec, out GlobalHotkey? hotkey) => TryRegister(spec, out hotkey, out _);

    /// <param name="win32Error">Код GetLastError при неудаче (1409 — занято), 0 при успехе.</param>
    public static bool TryRegister(HotkeySpec spec, out GlobalHotkey? hotkey, out int win32Error)
    {
        hotkey = null;
        win32Error = 0;
        var source = new HwndSource(new HwndSourceParameters("ScreenyHotkeySink") { WindowStyle = 0, Width = 0, Height = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
        int id = Interlocked.Increment(ref _nextId);
        if (!User32.RegisterHotKey(source.Handle, id, (uint)spec.Modifiers | User32.MOD_NOREPEAT, (uint)spec.VirtualKey))
        {
            win32Error = Marshal.GetLastWin32Error(); // до любых других вызовов Win32
            source.Dispose();
            return false;
        }
        hotkey = new GlobalHotkey(spec, source, id);
        return true;
    }

    public static bool IsAlreadyRegistered(int win32Error) => win32Error == ErrorHotkeyAlreadyRegistered;

    /// <summary>Текст для пользователя: «занято» только для 1409, иначе — с кодом ошибки.</summary>
    public static string DescribeFailure(HotkeySpec spec, int win32Error) => IsAlreadyRegistered(win32Error)
        ? Localization.Loc.F("Hotkeys.Busy", spec)
        : Localization.Loc.F("Hotkeys.RegisterFailed", spec, win32Error);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == User32.WM_HOTKEY && wParam.ToInt32() == _id) { Pressed?.Invoke(); handled = true; }
        return IntPtr.Zero;
    }

    public void Dispose() { User32.UnregisterHotKey(_source.Handle, _id); _source.RemoveHook(WndProc); _source.Dispose(); }
}
