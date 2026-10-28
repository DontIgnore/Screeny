using System.Runtime.InteropServices;
using System.Text;
using Screeny.Native;
using Screeny.Overlay;

namespace Screeny.Capture;

public static class WindowFinder
{
    /// <summary>Видимые окна в Z-порядке (первое — самое верхнее), без своих окон и без оболочки.</summary>
    public static List<WindowInfo> Snapshot(PixelRect virtualRect, ISet<IntPtr> exclude)
    {
        var list = new List<WindowInfo>();
        IntPtr shell = User32.GetShellWindow();
        var bounds = new PixelRect(0, 0, virtualRect.Width, virtualRect.Height);
        User32.EnumWindows((hwnd, _) =>
        {
            if (hwnd == shell || exclude.Contains(hwnd)) return true;
            if (!User32.IsWindowVisible(hwnd) || User32.IsIconic(hwnd)) return true;
            int ex = User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE);
            // Панели инструментов и «прозрачные для мыши» окна (оверлеи, подсказки) не выделяем.
            if ((ex & (User32.WS_EX_TOOLWINDOW | User32.WS_EX_TRANSPARENT)) != 0) return true;
            if (DwmApi.DwmGetWindowAttribute(hwnd, DwmApi.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            var cls = new StringBuilder(256); User32.GetClassName(hwnd, cls, cls.Capacity);
            var c = cls.ToString();
            if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow") return true;

            if (DwmApi.DwmGetWindowAttribute(hwnd, DwmApi.DWMWA_EXTENDED_FRAME_BOUNDS, out User32.RECT r, Marshal.SizeOf<User32.RECT>()) != 0
                && !User32.GetWindowRect(hwnd, out r)) return true;
            var rect = new PixelRect(r.Left - virtualRect.X, r.Top - virtualRect.Y, r.Right - r.Left, r.Bottom - r.Top);
            if (rect.Width < 10 || rect.Height < 10) return true;
            rect = rect.Clamp(bounds);
            if (rect.IsEmpty) return true;

            // Заголовок не читаем: GetWindowText шлёт WM_GETTEXT и может зависнуть на неотвечающем окне.
            list.Add(new WindowInfo(hwnd, rect));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static WindowInfo? PickTopmostAt(IReadOnlyList<WindowInfo> windows, PixelPoint p)
        => windows.FirstOrDefault(w => w.Rect.Contains(p));

    /// <summary>
    /// Видимые края окон на линии курсора — кандидаты прилипания рамки. Для вертикальных краёв (<see cref="EdgeAxis.Vertical"/>)
    /// берутся окна, чья высота накрывает строку <paramref name="cross"/>, и только те края, у которых это окно —
    /// верхнее в этой точке (край, закрытый другим окном, не виден и не притягивает). Значения — границы между
    /// пикселями, как <see cref="PixelRect.X"/> и <see cref="PixelRect.Right"/>.
    /// </summary>
    public static IEnumerable<int> VisibleEdges(IReadOnlyList<WindowInfo> windows, EdgeAxis axis, int cross)
    {
        foreach (var w in windows)
        {
            var r = w.Rect;
            if (axis == EdgeAxis.Vertical)
            {
                if (cross < r.Y || cross >= r.Bottom) continue;
                if (PickTopmostAt(windows, new PixelPoint(r.X, cross)) == w) yield return r.X;
                if (PickTopmostAt(windows, new PixelPoint(r.Right - 1, cross)) == w) yield return r.Right;
            }
            else
            {
                if (cross < r.X || cross >= r.Right) continue;
                if (PickTopmostAt(windows, new PixelPoint(cross, r.Y)) == w) yield return r.Y;
                if (PickTopmostAt(windows, new PixelPoint(cross, r.Bottom - 1)) == w) yield return r.Bottom;
            }
        }
    }
}
