using System.Runtime.InteropServices;
using Screeny.Logging;

namespace Screeny.Hotkeys;

/// <summary>
/// Низкоуровневый хук клавиатуры (<c>WH_KEYBOARD_LL</c>) для Win+Shift+S — это сочетание зарезервировано ОС, и
/// <c>RegisterHotKey</c> для него не работает.
/// <para>
/// Хук живёт на отдельном потоке со своим циклом сообщений: Windows вызывает callback через очередь потока, который
/// поставил хук, и молча снимает хук, если callback не уложился в <c>LowLevelHooksTimeout</c>. Поэтому callback только
/// читает vkCode/флаги, спрашивает <see cref="WinShiftSDetector"/> и при срабатывании отправляет своему потоку
/// сообщение; маску меню «Пуск» (SendInput) и уведомление <see cref="Triggered"/> делает уже обработчик этого
/// сообщения в цикле.
/// </para>
/// Не создаётся в тестах и в <c>--dev</c> без <c>--enable-ll-hook</c> (решает <see cref="HotkeyManager"/>).
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int LlkhfInjected = 0x10, LlkhfUp = 0x80;
    private const uint WmQuit = 0x0012, WmTriggered = 0x8000 + 0x5A1; // WM_APP + n
    private const uint PmNoRemove = 0;
    private const uint InputKeyboard = 1, KeyeventfExtendedKey = 0x1, KeyeventfKeyUp = 0x2;

    private readonly WinShiftSDetector _detector = new();
    private readonly HookProc _proc; // держим делегат: иначе GC соберёт его, пока хук стоит
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private uint _threadId;
    private IntPtr _hook;
    private int _startError;
    private int _disposed;

    /// <summary>Сработал Win+Shift+S. Вызывается в потоке хука: подписчик сам переходит в UI-поток.</summary>
    public event Action? Triggered;

    private LowLevelKeyboardHook()
    {
        _proc = Callback;
        _thread = new Thread(Run) { IsBackground = true, Name = "Screeny.KeyboardHook", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>Ставит хук; null и код ошибки Windows, если <c>SetWindowsHookEx</c> не удался.</summary>
    public static LowLevelKeyboardHook? TryStart(Action triggered, out int win32Error)
    {
        var hook = new LowLevelKeyboardHook();
        hook.Triggered += triggered;
        hook._thread.Start();
        if (!hook._ready.Wait(TimeSpan.FromSeconds(5)))
        {
            win32Error = 1460; // ERROR_TIMEOUT
            hook.Dispose();
            return null;
        }
        if (hook._hook == IntPtr.Zero)
        {
            win32Error = hook._startError;
            hook.Dispose();
            return null;
        }
        win32Error = 0;
        return hook;
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove); // создаёт очередь сообщений до первого PostThreadMessage
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) _startError = Marshal.GetLastWin32Error();
        _ready.Set();
        if (_hook == IntPtr.Zero) return;
        if (Volatile.Read(ref _disposed) != 0) { UnhookWindowsHookEx(_hook); return; } // TryStart уже сдался по таймауту
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WmTriggered) OnTriggered();
                else DispatchMessage(ref msg);
            }
        }
        catch (Exception ex) { Log.Error("Keyboard hook loop failed", ex); }
        finally { UnhookWindowsHookEx(_hook); }
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            // KBDLLHOOKSTRUCT: vkCode (0), scanCode (4), flags (8).
            int vk = Marshal.ReadInt32(lParam, 0);
            int flags = Marshal.ReadInt32(lParam, 8);
            var d = _detector.OnKey(vk, down: (flags & LlkhfUp) == 0, injected: (flags & LlkhfInjected) != 0);
            // Не удалось отправить себе сообщение — снимаем «маска ожидается» сразу: иначе каждое отпускание Win
            // придерживалось бы навсегда. «Пуск» тогда может открыться, но клавиша не залипнет.
            if (d.Triggered && !PostThreadMessage(_threadId, WmTriggered, IntPtr.Zero, IntPtr.Zero)) _detector.OnMaskSent();
            if (d.Swallow) return 1;
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>Маска «Пуск» (vkE8 вниз/вверх), затем придержанные отпускания Win, затем уведомление.</summary>
    private void OnTriggered()
    {
        var winUps = _detector.OnMaskSent();
        var inputs = new INPUT[2 + winUps.Count];
        inputs[0] = Key(WinShiftSDetector.MaskKey, 0);
        inputs[1] = Key(WinShiftSDetector.MaskKey, KeyeventfKeyUp);
        for (int i = 0; i < winUps.Count; i++) inputs[2 + i] = Key(winUps[i], KeyeventfKeyUp | KeyeventfExtendedKey); // Win — расширенная клавиша
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            Log.Error($"Keyboard hook: SendInput failed ({Marshal.GetLastWin32Error()})");
        try { Triggered?.Invoke(); }
        catch (Exception ex) { Log.Error("Keyboard hook trigger handler failed", ex); }
    }

    private static INPUT Key(int vk, uint flags) => new()
    {
        type = InputKeyboard,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags } },
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        if (_thread.IsAlive && !_thread.Join(2000)) Log.Error("Keyboard hook thread did not stop in time");
    }

    // ---- Win32 ----
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    /// <summary>Объединение размером с MOUSEINPUT (самый большой член): иначе SendInput отвергнет cbSize.</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
