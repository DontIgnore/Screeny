using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Screeny.Export;
using Screeny.Logging;
using Screeny.Native;
using Screeny.Overlay;

namespace Screeny.Pin;

/// <summary>
/// Pinned screenshot above all windows. Size and position set in device pixels via SetWindowPos,
/// so at 100% scale image matches original on-screen place pixel-perfect.
/// </summary>
public partial class PinWindow : Window
{
    private const double MinScale = 0.2, MaxZoom = 5, MinOpacity = 0.2;
    private static readonly Brush HoverStroke = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0x78, 0xF6)));
    private static readonly Brush IdleStroke = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));

    private readonly BitmapSource _image;
    private readonly DispatcherTimer _badgeTimer;
    private double _scale = 1.0;
    private bool _dpiFixPending;
    private PixelRect? _initialRegion; // until user moved or scaled window — exact original place

    public PinWindow(BitmapSource image, PixelRect screenRegion, bool excludeFromCapture = false)
    {
        InitializeComponent();
        _image = image;
        _initialRegion = screenRegion;
        PinImage.Source = image;
        UpdateScalingMode();

        _badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _badgeTimer.Tick += (_, _) => { _badgeTimer.Stop(); ZoomBadge.Visibility = Visibility.Collapsed; };

        SourceInitialized += (_, _) =>
        {
            SetPixelBounds(screenRegion.X, screenRegion.Y, screenRegion.Width, screenRegion.Height);
            SetExcludedFromCapture(excludeFromCapture);
        };
        Loaded += (_, _) => { Activate(); Focus(); };
        MouseEnter += (_, _) => Frame.Stroke = HoverStroke;
        MouseLeave += (_, _) => Frame.Stroke = IdleStroke;
    }

    public double Scale => _scale;

    /// <summary>Don't appear in new screen captures (Windows 10 2004+; older systems — doesn't work, logged).</summary>
    public void SetExcludedFromCapture(bool exclude)
    {
        if (!User32.SetWindowDisplayAffinity(Hwnd, exclude ? User32.WDA_EXCLUDEFROMCAPTURE : User32.WDA_NONE))
            Log.Error($"SetWindowDisplayAffinity({(exclude ? "WDA_EXCLUDEFROMCAPTURE" : "WDA_NONE")}) failed, Win32 error {Marshal.GetLastWin32Error()}");
    }

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    private void SetPixelBounds(int x, int y, int w, int h)
        => User32.SetWindowPos(Hwnd, new IntPtr(-1) /* HWND_TOPMOST */, x, y, Math.Max(1, w), Math.Max(1, h), User32.SWP_NOACTIVATE);

    private User32.RECT PixelBounds() { User32.GetWindowRect(Hwnd, out var r); return r; }

    /// <summary>
    /// Upper scale limit: no more than 500% and no more than window fits virtual screen
    /// (else 2560×1440 × 500% would give 12800×7200 layered window).
    /// </summary>
    private double MaxScale
    {
        get
        {
            int vw = User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN), vh = User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN);
            if (vw <= 0 || vh <= 0) return MaxZoom;
            double fit = Math.Min(vw / (double)Math.Max(1, _image.PixelWidth), vh / (double)Math.Max(1, _image.PixelHeight));
            return Math.Max(MinScale, Math.Min(MaxZoom, fit));
        }
    }

    /// <summary>Scale around point under cursor (in screen pixels) so it stays in place.</summary>
    public void ZoomTo(double scale, Point? anchorScreen = null)
    {
        ApplyScale(scale, anchorScreen);
        ShowBadge($"{Math.Round(_scale * 100)} %");
    }

    /// <summary>Physical window size = screenshot size × scale; anchor stays.</summary>
    private void ApplyScale(double scale, Point? anchorScreen)
    {
        _initialRegion = null;
        scale = Math.Clamp(scale, MinScale, MaxScale);
        var r = PixelBounds();
        double oldW = r.Right - r.Left, oldH = r.Bottom - r.Top;
        int newW = (int)Math.Round(_image.PixelWidth * scale), newH = (int)Math.Round(_image.PixelHeight * scale);
        var anchor = anchorScreen ?? new Point(r.Left + oldW / 2, r.Top + oldH / 2);
        double fx = oldW > 0 ? (anchor.X - r.Left) / oldW : 0.5, fy = oldH > 0 ? (anchor.Y - r.Top) / oldH : 0.5;
        int x = (int)Math.Round(anchor.X - fx * newW), y = (int)Math.Round(anchor.Y - fy * newH);
        _scale = scale;
        SetPixelBounds(x, y, newW, newH);
        UpdateScalingMode();
    }

    /// <summary>
    /// When moving to monitor with different scale, WPF applies system-proposed size (recalculated by DPI),
    /// and “1 screenshot pixel = _scale screen pixels” breaks. Restore physical size
    /// after WPF finishes WM_DPICHANGED; anchor — cursor (if over window) or center.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_dpiFixPending) return;
        _dpiFixPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            _dpiFixPending = false;
            if (Hwnd == IntPtr.Zero) return;
            if (_initialRegion is { } ir)
            {
                // Window still at original place (DPI changed at first layout): return exactly there.
                SetPixelBounds(ir.X, ir.Y, (int)Math.Round(ir.Width * _scale), (int)Math.Round(ir.Height * _scale));
                return;
            }
            var r = PixelBounds();
            Point? anchor = User32.GetCursorPos(out var c) && c.X >= r.Left && c.X < r.Right && c.Y >= r.Top && c.Y < r.Bottom
                ? new Point(c.X, c.Y) : null;
            ApplyScale(_scale, anchor);
        });
    }

    private void UpdateScalingMode()
        => RenderOptions.SetBitmapScalingMode(PinImage, Math.Abs(_scale - 1) < 0.001 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

    public void SetOpacity(double value)
    {
        Opacity = Math.Clamp(value, MinOpacity, 1.0);
        ShowBadge(Localization.Loc.F("Pin.OpacityBadge", Math.Round(Opacity * 100)));
    }

    private void ShowBadge(string text)
    {
        ZoomText.Text = text;
        ZoomBadge.Visibility = Visibility.Visible;
        _badgeTimer.Stop();
        _badgeTimer.Start();
    }

    // ---------- Input ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // DragMove “eats” double-click, so check beforehand.
        if (e.ClickCount == 2) { Close(); return; }
        _initialRegion = null;
        try { DragMove(); }
        catch (InvalidOperationException) { /* button already released */ }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        int dir = Math.Sign(e.Delta);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SetOpacity(Opacity + 0.1 * dir); return; }
        // PointToScreen in PerMonitorV2 returns device pixels.
        var anchor = PointToScreen(e.GetPosition(this));
        ZoomTo(_scale * (dir > 0 ? 1.1 : 1 / 1.1), anchor);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.C && ctrl) { Copy(); e.Handled = true; }
        else if (e.Key == Key.S && ctrl) { Save(); e.Handled = true; }
    }

    private async void Copy()
    {
        try
        {
            if (Application.Current is not App app) return;
            var copied = await app.CopyImageInBackground(() => _image);
            if (copied == false && IsVisible) ShowBadge(Localization.Loc.T("Common.ClipboardBusy"));
        }
        catch (Exception ex) { Log.Error("Pin copy failed", ex); }
    }

    private void Save()
    {
        try
        {
            var path = FileSaver.ChoosePath(this);
            if (path is not null && Application.Current is App app) app.SavePinnedImage(_image, path);
        }
        catch (Exception ex) { Log.Error("Pin save failed", ex); }
    }

    private void OnCopy(object sender, RoutedEventArgs e) => Copy();
    private void OnSave(object sender, RoutedEventArgs e) => Save();
    private void OnResetZoom(object sender, RoutedEventArgs e) { ZoomTo(1.0); }
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private void OnOpacity(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            SetOpacity(v);
    }
}
