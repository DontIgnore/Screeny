using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Logging;
using Screeny.Native;

namespace Screeny.Themes;

/// <summary>
/// Window without system frame: own 40 px title (icon, name, minimize/close) from style in Theme.xaml.
/// Dragging and double-click on title handled by system via WindowChrome.CaptionHeight;
/// double-click maximize works only for resizable windows (others lack WS_MAXIMIZEBOX).
/// </summary>
public class ChromeWindow : Window
{
    public static readonly DependencyProperty ShowCaptionTitleProperty =
        DependencyProperty.Register(nameof(ShowCaptionTitle), typeof(bool), typeof(ChromeWindow), new PropertyMetadata(true));

    public static readonly DependencyProperty CaptionIconProperty =
        DependencyProperty.Register(nameof(CaptionIcon), typeof(ImageSource), typeof(ChromeWindow));

    public static readonly DependencyProperty CaptionContentProperty =
        DependencyProperty.Register(nameof(CaptionContent), typeof(object), typeof(ChromeWindow));

    private static ImageSource? _appIcon;

    public ChromeWindow()
    {
        // Implicit style by type doesn't apply to descendants (SettingsWindow etc.) — add explicitly.
        SetResourceReference(StyleProperty, typeof(ChromeWindow));
        CaptionIcon = _appIcon ??= LoadAppIcon();
        Loaded += (_, _) => FitToMonitor(center: WindowStartupLocation == WindowStartupLocation.CenterScreen);
        LocationChanged += (_, _) => { if (IsLoaded && CurrentMonitor() != _monitor) FitToMonitor(center: false); };
    }

    /// <summary>
    /// Natural height limit, DIP: scrollable window (settings, wizard) doesn't grow higher even if monitor allows.
    /// Final <see cref="FrameworkElement.MaxHeight"/> — min of it and monitor work area (<see cref="WindowFit.CapHeight"/>).
    /// </summary>
    public double NaturalMaxHeight { get; set; } = double.PositiveInfinity;

    /// <summary>
    /// Only for test instance: work area in device pixels instead of real (small monitor check without second monitor; environment variable <c>SCREENY_DEV_WORKAREA=x,y,w,h</c>, read by App).
    ///
    /// </summary>
    public static Rect? DevWorkAreaOverride { get; set; }

    private IntPtr _monitor;

    /// <summary>
    /// Window monitor. Before show, window centered on screen not yet in place — take monitor under cursor,
    /// where Windows will open it; after — monitor with most of window.
    /// </summary>
    private IntPtr CurrentMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        if (!IsLoaded && WindowStartupLocation == WindowStartupLocation.CenterScreen && User32.GetCursorPos(out var cursor))
            return User32.MonitorFromPoint(cursor, User32.MONITOR_DEFAULTTONEAREST);
        return User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTONEAREST);
    }

    /// <summary>
    /// Window per monitor it actually is on (not primary like <c>SystemParameters.WorkArea</c>): height no
    /// larger than that monitor work area minus margin, window entirely inside — centered (when opened centered on screen)
    /// or nearest to previous place. Repeated on monitor change and DPI change.
    /// </summary>
    private void FitToMonitor(bool center)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || WindowState != WindowState.Normal) return;
        _monitor = CurrentMonitor();
        Rect workPx;
        if (DevWorkAreaOverride is { } forced) workPx = forced;
        else
        {
            var info = new User32.MONITORINFO { cbSize = Marshal.SizeOf<User32.MONITORINFO>() };
            if (_monitor == IntPtr.Zero || !User32.GetMonitorInfo(_monitor, ref info)) return;
            var w = info.rcWork;
            workPx = new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
        }
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        double cap = WindowFit.CapHeight(WindowFit.ToDips(workPx, scale).Height, NaturalMaxHeight);
        if (Math.Abs(MaxHeight - cap) > 0.5)
        {
            MaxHeight = cap;
            UpdateLayout(); // SizeToContent recalculates height under new limit before window placement
        }
        if (!User32.GetWindowRect(hwnd, out var r)) return;
        var current = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        var target = WindowFit.FitInto(current, workPx, center);
        if (target == current) return;
        bool resize = Math.Abs(target.Width - current.Width) > 0.5 || Math.Abs(target.Height - current.Height) > 0.5;
        User32.SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(target.X), (int)Math.Round(target.Y), (int)Math.Round(target.Width), (int)Math.Round(target.Height),
            User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | (resize ? 0 : User32.SWP_NOSIZE));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsLoaded) Dispatcher.BeginInvoke(() => FitToMonitor(center: false));
    }

    /// <summary>Show window name in title (dialogs show title in body).</summary>
    public bool ShowCaptionTitle
    {
        get => (bool)GetValue(ShowCaptionTitleProperty);
        set => SetValue(ShowCaptionTitleProperty, value);
    }

    /// <summary>Custom title elements, left of window buttons (clickable: don't drag window).</summary>
    public object? CaptionContent
    {
        get => GetValue(CaptionContentProperty);
        set => SetValue(CaptionContentProperty, value);
    }

    public ImageSource? CaptionIcon
    {
        get => (ImageSource?)GetValue(CaptionIconProperty);
        set => SetValue(CaptionIconProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Minimize") is Button minimize) minimize.Click += (_, _) => WindowState = WindowState.Minimized;
        if (GetTemplateChild("PART_Close") is Button close) close.Click += (_, _) => Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyNativeTheme();
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        // Height limit before first layout so window doesn't open as tower above screen; place refined in Loaded.
        FitToMonitor(center: WindowStartupLocation == WindowStartupLocation.CenterScreen);
    }

    private const int WM_GETMINMAXINFO = 0x0024;

    /// <summary>
    /// Without system title, Windows maximizes window to entire monitor with edges beyond border thickness
    /// (standard WindowChrome issue) and covers taskbar. Set maximized window exactly work area of its monitor — then no offset compensation in template needed.
    ///
    /// </summary>
    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var monitor = User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTONEAREST);
        var info = new User32.MONITORINFO { cbSize = Marshal.SizeOf<User32.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !User32.GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var mmi = Marshal.PtrToStructure<User32.MINMAXINFO>(lParam);
        var work = info.rcWork;
        var area = info.rcMonitor;
        mmi.ptMaxPosition = new User32.POINT { X = work.Left - area.Left, Y = work.Top - area.Top };
        mmi.ptMaxSize = new User32.POINT { X = work.Right - work.Left, Y = work.Bottom - work.Top };
        Marshal.StructureToPtr(mmi, lParam, true);
        return IntPtr.Zero; // don't mark handled: WPF adds MinWidth/MinHeight to window
    }

    /// <summary>Alt+F4: without system title close ourselves, not relying on system menu.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.System && e.SystemKey == Key.F4)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>
    /// DWM attributes: rounding (Windows 11), dark system elements in dark theme, window border color.
    /// On Windows 10 attributes 33/34 unsupported — errors ignored.
    /// </summary>
    internal void ApplyNativeTheme()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int round = DwmApi.DWMWCP_ROUND;
        DwmApi.DwmSetWindowAttribute(hwnd, DwmApi.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int dark = ThemeManager.Current == AppTheme.Dark ? 1 : 0;
        if (DwmApi.DwmSetWindowAttribute(hwnd, DwmApi.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
            DwmApi.DwmSetWindowAttribute(hwnd, DwmApi.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));
        if (TryFindResource("Color.WindowBorder") is Color c)
        {
            int colorRef = c.R | (c.G << 8) | (c.B << 16);
            DwmApi.DwmSetWindowAttribute(hwnd, DwmApi.DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
        }
    }

    /// <summary>32 px frame from screeny.ico: crisp at 16 DIP on 100–200% scale.</summary>
    private static ImageSource? LoadAppIcon()
    {
        try
        {
            var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/Assets/screeny.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.Where(f => f.PixelWidth >= 32).OrderBy(f => f.PixelWidth).FirstOrDefault()
                        ?? decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            if (frame.CanFreeze) frame.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            Log.Error("App icon load failed", ex);
            return null;
        }
    }
}
