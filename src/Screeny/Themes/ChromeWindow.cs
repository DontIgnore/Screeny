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
/// Окно без системной рамки: свой заголовок 40 px (иконка, название, «свернуть» и «закрыть») из стиля в Theme.xaml.
/// Перетаскивание и двойной клик по заголовку обрабатывает система через WindowChrome.CaptionHeight;
/// разворачивание двойным кликом работает только у окон с изменяемым размером (у остальных нет WS_MAXIMIZEBOX).
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
        // Неявный стиль по типу не применяется к наследникам (SettingsWindow и т. п.) — подключаем явно.
        SetResourceReference(StyleProperty, typeof(ChromeWindow));
        CaptionIcon = _appIcon ??= LoadAppIcon();
        Loaded += (_, _) => FitToMonitor(center: WindowStartupLocation == WindowStartupLocation.CenterScreen);
        LocationChanged += (_, _) => { if (IsLoaded && CurrentMonitor() != _monitor) FitToMonitor(center: false); };
    }

    /// <summary>
    /// Естественный предел высоты, DIP: окно с прокруткой (настройки, мастер) не растёт выше, даже если монитор позволяет.
    /// Итоговый <see cref="FrameworkElement.MaxHeight"/> — меньшее из него и рабочей области монитора (<see cref="WindowFit.CapHeight"/>).
    /// </summary>
    public double NaturalMaxHeight { get; set; } = double.PositiveInfinity;

    /// <summary>
    /// Только для тестового экземпляра: рабочая область в пикселях устройства вместо настоящей (проверка маленького
    /// монитора без второго монитора; переменная окружения <c>SCREENY_DEV_WORKAREA=x,y,w,h</c>, читает App).
    /// </summary>
    public static Rect? DevWorkAreaOverride { get; set; }

    private IntPtr _monitor;

    /// <summary>
    /// Монитор окна. До показа окно, открываемое по центру экрана, ещё не на своём месте — берём монитор под курсором,
    /// где Windows его и откроет; после — монитор, на котором окно больше всего.
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
    /// Окно по монитору, на котором оно на самом деле (а не по основному, как <c>SystemParameters.WorkArea</c>): высота не
    /// больше рабочей области этого монитора минус поле, окно целиком внутри неё — по центру (при открытии по центру
    /// экрана) или ближайшее к прежнему месту. Повторяется при переходе на другой монитор и смене DPI.
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
            UpdateLayout(); // SizeToContent пересчитывает высоту под новый предел до того, как окно ставится на место
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

    /// <summary>Показывать название окна в заголовке (диалоги показывают заголовок в теле).</summary>
    public bool ShowCaptionTitle
    {
        get => (bool)GetValue(ShowCaptionTitleProperty);
        set => SetValue(ShowCaptionTitleProperty, value);
    }

    /// <summary>Свои элементы в заголовке, слева от кнопок окна (кликабельны: не перетаскивают окно).</summary>
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
        // Предел высоты — до первой раскладки, чтобы окно не открылось башней выше экрана; место уточняется в Loaded.
        FitToMonitor(center: WindowStartupLocation == WindowStartupLocation.CenterScreen);
    }

    private const int WM_GETMINMAXINFO = 0x0024;

    /// <summary>
    /// Без системного заголовка Windows разворачивает окно на весь монитор с выходом за края на толщину рамки
    /// (стандартная проблема WindowChrome) и перекрывает панель задач. Задаём развёрнутому окну ровно рабочую
    /// область его монитора — тогда и отступ-компенсация в шаблоне не нужна.
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
        return IntPtr.Zero; // не помечаем handled: WPF дополнит MinWidth/MinHeight окна
    }

    /// <summary>Alt+F4: без системного заголовка закрываем сами, не полагаясь на системное меню.</summary>
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
    /// Атрибуты DWM: скругление (Windows 11), тёмные системные элементы в тёмной теме, цвет обводки окна.
    /// На Windows 10 атрибуты 33/34 не поддерживаются — ошибки игнорируем.
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

    /// <summary>Кадр 32 px из screeny.ico: чёткий при 16 DIP на 100–200 % масштаба.</summary>
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
