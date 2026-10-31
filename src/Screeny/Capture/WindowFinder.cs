using System.Runtime.InteropServices;
using System.Text;
using Screeny.Native;
using Screeny.Overlay;

namespace Screeny.Capture;

public static class WindowFinder
{
    /// <summary>Visible windows in Z-order (first is topmost), excluding own windows and shell.</summary>
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
            // Toolbars and click-through windows (overlays, tooltips) are not selected.
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

            // Title not read: GetWindowText sends WM_GETTEXT and can hang on unresponsive window.
            list.Add(new WindowInfo(hwnd, rect));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static WindowInfo? PickTopmostAt(IReadOnlyList<WindowInfo> windows, PixelPoint p)
        => windows.FirstOrDefault(w => w.Rect.Contains(p));

    /// <summary>
    /// Visible window edges on cursor line — snap candidates for selection border. For vertical edges (<see cref="EdgeAxis.Vertical"/>)
    /// windows whose height covers row <paramref name="cross"/> are taken, and only edges where that window is
    /// topmost at that point (edge covered by another window is not visible and doesn't snap). Values are boundaries between
    /// pixels, like <see cref="PixelRect.X"/> and <see cref="PixelRect.Right"/>.
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
