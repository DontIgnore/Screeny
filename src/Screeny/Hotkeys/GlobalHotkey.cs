using System.Runtime.InteropServices;
using System.Windows.Interop;
using Screeny.Native;

namespace Screeny.Hotkeys;

/// <summary>Registers global hotkey on message-only window. Call from UI thread.</summary>
public sealed class GlobalHotkey : IDisposable
{
    /// <summary>ERROR_HOTKEY_ALREADY_REGISTERED: combo already used by another app.</summary>
    public const int ErrorHotkeyAlreadyRegistered = 1409;

    private static int _nextId = 0xB000;
    private readonly HwndSource _source;
    private readonly int _id;
    public HotkeySpec Spec { get; }
    public event Action? Pressed;

    private GlobalHotkey(HotkeySpec spec, HwndSource source, int id) { Spec = spec; _source = source; _id = id; source.AddHook(WndProc); }

    public static bool TryRegister(HotkeySpec spec, out GlobalHotkey? hotkey) => TryRegister(spec, out hotkey, out _);

    /// <param name="win32Error">GetLastError code on failure (1409 — busy), 0 on success.</param>
    public static bool TryRegister(HotkeySpec spec, out GlobalHotkey? hotkey, out int win32Error)
    {
        hotkey = null;
        win32Error = 0;
        var source = new HwndSource(new HwndSourceParameters("ScreenyHotkeySink") { WindowStyle = 0, Width = 0, Height = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
        int id = Interlocked.Increment(ref _nextId);
        if (!User32.RegisterHotKey(source.Handle, id, (uint)spec.Modifiers | User32.MOD_NOREPEAT, (uint)spec.VirtualKey))
        {
            win32Error = Marshal.GetLastWin32Error(); // before any other Win32 calls
            source.Dispose();
            return false;
        }
        hotkey = new GlobalHotkey(spec, source, id);
        return true;
    }

    public static bool IsAlreadyRegistered(int win32Error) => win32Error == ErrorHotkeyAlreadyRegistered;

    /// <summary>User text: “busy” only for 1409, otherwise with error code.</summary>
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
