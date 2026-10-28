using System.Runtime.InteropServices;

namespace Screeny.Native;

internal static class DwmApi
{
    public const int DWMWA_CLOAKED = 14, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    /// <summary>Тёмные системные элементы окна: 20 с Windows 10 20H1, 19 — на более ранних сборках.</summary>
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    /// <summary>Только Windows 11: скругление углов и цвет 1-px обводки, которую рисует DWM.</summary>
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out User32.RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
