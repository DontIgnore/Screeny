using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Capture;
using Screeny.Editing;
using Screeny.Export;
using Screeny.Features;
using Screeny.Localization;
using Screeny.Native;
using Screeny.Ocr;
using Screeny.Ocr.Paddle;
using Screeny.Overlay.Tools;
using Screeny.Settings;

namespace Screeny.Overlay;

public enum OverlayMode { Selecting, Editing }

/// <summary>
/// Тестовый экземпляр (<c>--dev --overlay-test</c>): оверлей не закрывается при потере активности
/// (его снимают сбоку через PrintWindow), может сразу открыться с выделением и готовым сценарием.
/// </summary>
public sealed record OverlayDevOptions(PixelRect? Select, string? Scenario, string? Source = null);

/// <summary>
/// Полноэкранное окно поверх всего виртуального экрана со «замороженным» снимком.
/// Выделение и аннотации хранятся в пикселях снимка; на экран выводятся через ScaleTransform(1/dpi).
/// </summary>
public partial class OverlayWindow : Window
{
    private const int DragThreshold = 3;

    private readonly Screenshot _shot;
    private readonly List<WindowInfo> _windows;
    private readonly AppSettings _settings;
    private readonly SelectionModel _selection;
    /// <summary>Рисование: инструменты, текст, выбор, параметры (flyout'ы), пипетка, линейка, undo, оформление.</summary>
    private readonly AnnotationEditor _editor;
    private bool _toolbarBelow = true;  // панель под выделением: flyout'ы её кнопок открываются вниз
    private DispatcherTimer? _badgeTimer;
    private CoordinateMapper _map;
    private PixelPoint? _dragAnchor;   // начало перетаскивания нового выделения
    private bool _thresholdPassed;     // перетаскивание нового выделения ушло дальше порога клика
    private bool _draggingHandle;
    private OverlayMode _mode = OverlayMode.Selecting;
    private bool _closing;             // Complete/Cancel уже начались: Deactivated от Hide() игнорируем
    private bool _dialogOpen;          // открыт наш модальный диалог (сохранение): потеря активности ожидаема

    // Режим текста (Ctrl+T): распознанные слова можно выделять мышью и копировать.
    private bool _textMode;
    private int _ocrGeneration;        // каждый вход и выход из режима; устаревший результат распознавания отбрасывается
    private OcrLayout? _ocr;           // рамки слов в пикселях снимка; null — ещё распознаём
    private HashSet<int> _textSelection = new();
    private int? _textAnchor;          // слово, от которого тянется выделение (и для Shift+клик)
    private Point? _textPress;         // точка нажатия текущего жеста выделения
    private bool _textDragFromWord;    // жест начался на слове → выделение по порядку чтения, иначе — рамкой
    private bool _textDragging;        // жест ушёл дальше порога клика
    private HashSet<int> _textBase = new(); // выделение до жеста: Shift/Ctrl добавляют к нему
    private DispatcherTimer? _hintTimer;
    private bool _copyWhenReady;       // Enter/Ctrl+C нажали, пока ещё распознаём: скопировать сразу по готовности
    // Точное распознавание (скачиваемый модуль): результат Windows OCR показывается сразу, уточнение в фоне заменяет раскладку.
    private readonly IOcrRefiner? _refiner;
    private CancellationTokenSource? _refineCts;
    private bool _refining;            // идёт уточнение: в подсказке «уточняю…»
    private OcrLayout? _pendingRefined; // уточнение пришло посреди жеста выделения: применяется, когда жест закончится
    /// <summary>Уточнение должно успеть за столько от готовности сессий, иначе остаётся результат Windows OCR.</summary>
    private static readonly TimeSpan RefineBudget = TimeSpan.FromSeconds(5);
    /// <summary>Подготовка (проверка файлов модуля и загрузка сессий, первый раз ≈0,3 с) — не дольше этого.</summary>
    private static readonly TimeSpan PrepareLimit = TimeSpan.FromSeconds(10);

    public event Action<CaptureResult>? Completed;
    /// <summary>Оверлей закрыт, потому что изменилась конфигурация мониторов.</summary>
    public event Action? DisplaysChanged;

    private readonly OverlayDevOptions? _dev;

    /// <param name="refiner">Точное распознавание для режима текста; null — только Windows OCR.</param>
    public OverlayWindow(Screenshot shot, List<WindowInfo> windows, AppSettings settings, OverlayDevOptions? dev = null, IOcrRefiner? refiner = null)
    {
        InitializeComponent();
        _shot = shot; _windows = windows; _settings = settings; _dev = dev; _refiner = refiner;
        _selection = new SelectionModel(shot.PixelBounds);
        _map = new CoordinateMapper(new PixelPoint(shot.VirtualRect.X, shot.VirtualRect.Y), 1.0);

        ShotImage.Source = shot.Bitmap;
        foreach (var layer in new FrameworkElement[] { ShotImage, Dim, Annotations, TextOverlay, RulerOverlay, SnapGuides })
        {
            layer.Width = shot.Bitmap.PixelWidth;
            layer.Height = shot.Bitmap.PixelHeight;
        }

        // Состояние и параметры оформления запоминаются в настройках для следующих снимков.
        var flyouts = new OptionsFlyouts(Toolbar);
        flyouts.Attach(this);
        _editor = new AnnotationEditor(new AnnotationEditorParts(Toolbar, flyouts, TextEditor, Annotations, RulerOverlay, SnapGuides), shot.Bitmap, settings, this,
            new BeautifyState(settings.BeautifyEnabled, BeautifyOptions.FromSettings(settings), Persist: true));
        Toolbar.ActionRequested += a =>
        {
            if (a == CaptureAction.Save) SaveAs();
            else if (_textMode && a == CaptureAction.CopyExplicit) CopyText();
            else Complete(a);
        };
        Toolbar.TextModeRequested += ToggleTextMode;
        Toolbar.CancelRequested += Cancel;

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SourceInitialized += (_, _) => PlaceOverVirtualScreen();
        Loaded += (_, _) =>
        {
            _shownAt = DateTime.UtcNow;
            Activate();
            Focus();
            User32.SetForegroundWindow(new WindowInteropHelper(this).Handle);
            UpdateHover(CurrentMousePixel(), Mouse.GetPosition(PixelCanvas));
            ApplyDevOptions();
        };
    }

    /// <summary>Готовое выделение тестового запуска — как будто его только что выделили мышью, — и сценарий.</summary>
    private void ApplyDevOptions()
    {
        if (_dev is null) return;
        if (_dev.Select is { } select)
        {
            _selection.Rect = select.Clamp(_shot.PixelBounds);
            if (_selection.HasSelection) EnterEditing();
        }
        if (_dev.Scenario is { } scenario) ApplyDevScenario(scenario);
    }

    private AnnotationDocument Document => Annotations.Document;
    private bool HasGesture => _editor.HasGesture || _draggingHandle || _dragAnchor is not null || _textPress is not null;

    // ---------- Размещение и DPI ----------

    private void PlaceOverVirtualScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var r = _shot.VirtualRect;
        // Ставим окно в пикселях устройства через Win32, минуя DIP-пересчёт WPF.
        // Уже на месте — ничего не делаем: это и защита от цикла с WM_DPICHANGED.
        User32.GetWindowRect(hwnd, out var current);
        if (current.Left != r.X || current.Top != r.Y || current.Right != r.Right || current.Bottom != r.Bottom)
            User32.SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, r.X, r.Y, r.Width, r.Height, User32.SWP_NOACTIVATE);
        UpdateScale(VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    /// <summary>
    /// Окно на весь виртуальный экран пересекает мониторы с разным DPI: на WM_DPICHANGED (в том числе внутри
    /// нашего же SetWindowPos) WPF ставит окно в предложенный системой прямоугольник. Пересчитываем масштаб
    /// и после обработки сообщения возвращаем окно на виртуальный экран.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateScale(newDpi.DpiScaleX);
        Dispatcher.BeginInvoke(PlaceOverVirtualScreen);
    }

    private void UpdateScale(double dpi)
    {
        _map = new CoordinateMapper(new PixelPoint(_shot.VirtualRect.X, _shot.VirtualRect.Y), dpi);
        PixelScale.ScaleX = PixelScale.ScaleY = 1 / _map.DpiScale;
        Dim.DpiScale = _map.DpiScale;
        TextOverlay.DpiScale = _map.DpiScale;
        RulerOverlay.DpiScale = _map.DpiScale;
        RefreshSelectionVisuals();
    }

    /// <summary>
    /// Снимок и координаты окна привязаны к раскладке мониторов на момент захвата. Если она изменилась
    /// (отключили монитор, сменили разрешение), продолжать нельзя — закрываемся без результата.
    /// Событие приходит с потока SystemEvents, поэтому переходим в поток окна.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            DisplaysChanged?.Invoke();
            Cancel();
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        CancelRefine();
        CancelPrivacyCheck();
        base.OnClosed(e);
    }

    // ---------- Координаты ----------

    private PixelPoint ToPixel(Point canvasPoint)
    {
        var b = _shot.PixelBounds;
        return new PixelPoint(Math.Clamp((int)Math.Floor(canvasPoint.X), 0, b.Width - 1), Math.Clamp((int)Math.Floor(canvasPoint.Y), 0, b.Height - 1));
    }

    /// <summary>Позиция мыши в пикселях снимка — через обратное преобразование PixelCanvas, т.е. ровно как при отрисовке.</summary>
    private PixelPoint MousePixel(MouseEventArgs e) => ToPixel(e.GetPosition(PixelCanvas));
    private PixelPoint CurrentMousePixel() => ToPixel(Mouse.GetPosition(PixelCanvas));

    /// <summary>Точка для инструментов: дробные пиксели, прижатые к выделению (рисовать можно только внутри).</summary>
    private Point ToolPoint(MouseEventArgs e)
    {
        var p = e.GetPosition(PixelCanvas);
        var r = _selection.Rect;
        return new Point(Math.Clamp(p.X, r.X, r.Right), Math.Clamp(p.Y, r.Y, r.Bottom));
    }

    /// <summary>Прямоугольник монитора (или его рабочей области без панели задач) под точкой, в пикселях снимка.</summary>
    private PixelRect MonitorAt(PixelPoint p, bool workArea = false)
    {
        var pt = new User32.POINT { X = p.X + _shot.VirtualRect.X, Y = p.Y + _shot.VirtualRect.Y };
        var info = new User32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<User32.MONITORINFO>() };
        if (!User32.GetMonitorInfo(User32.MonitorFromPoint(pt, User32.MONITOR_DEFAULTTONEAREST), ref info)) return _shot.PixelBounds;
        var m = workArea ? info.rcWork : info.rcMonitor;
        return new PixelRect(m.Left - _shot.VirtualRect.X, m.Top - _shot.VirtualRect.Y, m.Right - m.Left, m.Bottom - m.Top).Clamp(_shot.PixelBounds);
    }

    private int HandleTolerance => (int)Math.Round(6 * _map.DpiScale);

    private bool IsFromHud(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, Toolbar) || ReferenceEquals(d, TextEditor) || _editor.Flyouts.Contains(d) || ReferenceEquals(d, CodePopup)
                || ReferenceEquals(d, PrivacyChip)) return true;
        return false;
    }

    // ---------- Мышь ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (IsFromHud(e.OriginalSource)) return;
        ClearDistances(); // жест начинается: линии расстояний мешали бы видеть, что рисуешь
        if (_textMode) { e.Handled = true; TextMouseDown(e); return; }
        CommitTextEdit();
        var p = MousePixel(e);
        e.Handled = true;
        // Пипетка важнее ручек и инструментов: клик в любом месте снимка берёт цвет.
        if (_editor.PipetteOn) { _editor.PickColor(p); return; }

        var handle = _selection.HitTest(p, HandleTolerance);
        if (_mode == OverlayMode.Editing)
        {
            var exact = e.GetPosition(PixelCanvas);
            bool resizeHandle = handle is not (Handle.None or Handle.Move);
            // Инструмент «Выбор» по пустому месту внутри области двигает саму область.
            bool moveSelection = handle == Handle.Move && Toolbar.CurrentTool == ToolKind.Select && Document.HitTest(exact) is null;
            if (resizeHandle || moveSelection)
            {
                Deselect();
                StartHandleDrag(handle, p);
                return;
            }
            if (handle == Handle.Move)
            {
                if (_editor.BeginGesture(ToolPoint(e))) CaptureMouse();
            }
            // Клик вне выделения инструментом рисования игнорируем: рисовать можно только внутри области.
            else Deselect();
            return;
        }

        if (_selection.HasSelection && handle != Handle.None) { StartHandleDrag(handle, p); return; }
        _dragAnchor = p;
        _thresholdPassed = false;
        BeginFrameGesture();
        CaptureMouse();
    }

    private void StartHandleDrag(Handle handle, PixelPoint p)
    {
        _editor.Flyouts.CloseAll(); // панель поедет вместе с рамкой
        _selection.BeginDrag(handle, p);
        _draggingHandle = true;
        BeginFrameGesture();
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_textMode) { TextMouseMove(e); return; }
        var p = MousePixel(e);

        if (_editor.HasGesture && e.LeftButton == MouseButtonState.Pressed)
        {
            _editor.MoveGesture(ToolPoint(e));
            return;
        }
        if (_draggingHandle && e.LeftButton == MouseButtonState.Pressed)
        {
            DragFrameHandle(p);
            RefreshSelectionVisuals();
            return;
        }
        if (_dragAnchor is { } a && e.LeftButton == MouseButtonState.Pressed)
        {
            if (_thresholdPassed || Math.Abs(p.X - a.X) >= DragThreshold || Math.Abs(p.Y - a.Y) >= DragThreshold)
            {
                _thresholdPassed = true;
                DragNewFrame(a, p);
                Dim.Hint = null;
                RefreshSelectionVisuals();
            }
            UpdateMagnifier(p);
            return;
        }
        if (IsFromHud(e.OriginalSource)) { Cursor = Cursors.Arrow; ClearDistances(); if (_editor.PipetteOn) Magnifier.Visibility = Visibility.Collapsed; return; }
        UpdateHover(p, e.GetPosition(PixelCanvas));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_textMode) { TextMouseUp(e); return; }
        var p = MousePixel(e);
        // Состояние жеста сбрасываем до ReleaseMouseCapture: он синхронно вызывает OnLostMouseCapture.
        if (_editor.EndGesture(ToolPoint(e)))
        {
            ReleaseMouseCapture();
            return;
        }
        if (_draggingHandle)
        {
            _selection.EndDrag();
            _draggingHandle = false;
            HideFrameGuides();
            ReleaseMouseCapture();
            RefreshSelectionVisuals();
            if (_mode == OverlayMode.Selecting) EnterEditing();
            return;
        }
        if (_dragAnchor is { } a)
        {
            bool isClick = !_thresholdPassed;
            _dragAnchor = null;
            _thresholdPassed = false;
            HideFrameGuides();
            ReleaseMouseCapture();
            if (isClick) _selection.Rect = WindowFinder.PickTopmostAt(_windows, a)?.Rect ?? MonitorAt(a);
            if (_selection.HasSelection) EnterEditing();
        }
    }

    /// <summary>Захват мыши потерян не по нашей воле (Alt+Tab, всплывшее окно и т.п.) — жест прерывается целиком.</summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // Событие всплывает и от кнопок панели и поля ввода; нас интересует только захват самого окна.
        if (!ReferenceEquals(e.OriginalSource, this)) return;
        ResetInteraction();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        e.Handled = true;
        if (IsFromHud(e.OriginalSource)) return;
        StepBack();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_mode != OverlayMode.Editing || e.Handled || _textMode) return;
        e.Handled = true;
        _editor.AdjustThickness(Math.Sign(e.Delta));
    }

    private void UpdateHover(PixelPoint p, Point exact)
    {
        UpdateDistances(p);
        if (_editor.PipetteOn)
        {
            UpdateMagnifier(p);
            Cursor = Cursors.Cross;
            return;
        }
        if (_mode == OverlayMode.Selecting && !_selection.HasSelection)
        {
            Dim.Hint = WindowFinder.PickTopmostAt(_windows, p)?.Rect ?? MonitorAt(p);
            UpdateMagnifier(p);
            Cursor = Cursors.Cross;
            return;
        }
        var handle = _selection.HitTest(p, HandleTolerance);
        if (_mode == OverlayMode.Editing && handle == Handle.Move)
        {
            Cursor = Toolbar.CurrentTool == ToolKind.Select
                ? (Document.HitTest(exact) is not null ? Cursors.Hand : Cursors.SizeAll)
                : _editor.CursorFor(Toolbar.CurrentTool);
            return;
        }
        Cursor = CursorFor(handle);
    }

    private static Cursor CursorFor(Handle h) => h switch
    {
        Handle.TopLeft or Handle.BottomRight => Cursors.SizeNWSE,
        Handle.TopRight or Handle.BottomLeft => Cursors.SizeNESW,
        Handle.Top or Handle.Bottom => Cursors.SizeNS,
        Handle.Left or Handle.Right => Cursors.SizeWE,
        Handle.Move => Cursors.SizeAll,
        _ => Cursors.Arrow,
    };

    // ---------- Режимы и сброс ----------

    private void EnterEditing()
    {
        _mode = OverlayMode.Editing;
        Dim.Hint = null;
        Magnifier.Visibility = Visibility.Collapsed;
        HidePipetteBadge();
        Toolbar.Visibility = Visibility.Visible;
        RefreshOptions();
        RefreshSelectionVisuals();
    }

    /// <summary>
    /// Единый сброс текущего жеста мыши: живая фигура или сдвиг аннотации откатываются, перетаскивание ручки
    /// завершается на месте, начатое новое выделение отбрасывается, захват мыши отпускается. Идемпотентен.
    /// </summary>
    private void ResetInteraction()
    {
        if (!HasGesture) return;
        if (_textPress is not null)
        {
            _textPress = null; _textDragging = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            ShowTextSelection();
            ApplyPendingRefined();
            return;
        }
        _editor.CancelGesture();
        if (_draggingHandle) { _draggingHandle = false; _selection.EndDrag(); }
        if (_dragAnchor is not null)
        {
            _dragAnchor = null;
            if (_mode == OverlayMode.Selecting) _selection.Rect = default;
        }
        _thresholdPassed = false;
        HideFrameGuides();
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (_mode == OverlayMode.Selecting && _selection.HasSelection) EnterEditing();
        RefreshSelectionVisuals();
        if (_mode == OverlayMode.Selecting && !_closing) UpdateHover(CurrentMousePixel(), Mouse.GetPosition(PixelCanvas));
    }

    /// <summary>
    /// Esc и правый клик снимают по одному слою: жест → выбранная аннотация / инструмент рисования →
    /// выделение области (аннотации остаются) → закрытие оверлея.
    /// </summary>
    private void StepBack()
    {
        if (HasGesture) { ResetInteraction(); return; }
        if (_editor.PipetteOn) { SetPipette(false); return; }
        if (_textMode)
        {
            if (CloseCodePopup()) return;
            if (_textSelection.Count > 0) { _textSelection = new(); _textAnchor = null; ShowTextSelection(); }
            else ExitTextMode();
            return;
        }
        if (_editor.IsTextEditing) { CommitTextEdit(); return; }
        if (_editor.Ruler.Measurement is not null) { _editor.Ruler.Clear(); return; }
        if (Document.Selected is not null || (_mode == OverlayMode.Editing && Toolbar.CurrentTool != ToolKind.Select))
        {
            Deselect();
            SetTool(ToolKind.Select);
            return;
        }
        if (_selection.HasSelection) { ResetSelection(); return; }
        Cancel();
    }

    private void Deselect() => _editor.Deselect();

    /// <summary>
    /// Обратно к выбору области. Аннотации не удаляются: они в координатах снимка, остаются на месте
    /// и при экспорте обрезаются новым выделением.
    /// </summary>
    private void ResetSelection()
    {
        CommitTextEdit();
        ResetInteraction();
        SetPipette(false);
        _editor.Ruler.Clear();
        _mode = OverlayMode.Selecting;
        _selection.Rect = default;
        Deselect();
        Toolbar.Visibility = Visibility.Collapsed;
        RefreshOptions();
        HideHint();
        RefreshSelectionVisuals();
        UpdateHover(CurrentMousePixel(), Mouse.GetPosition(PixelCanvas));
    }

    /// <summary>
    /// Alt+Tab, UAC, всплывшее чужое окно: Topmost-оверлей без фокуса не реагирует на Esc и мешает работать.
    /// Закрываемся без результата, кроме случаев, когда активность ушла ожидаемо (наш диалог, ввод текста).
    /// </summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_closing || _dialogOpen || _dev is not null) return;
        // Сразу после показа активность может на миг забрать закрывающееся меню трея или сам Show():
        // это не уход пользователя, возвращаем фокус себе. То же, если активным стало другое наше окно.
        if (DateTime.UtcNow - _shownAt < ActivationGrace || ForegroundIsOurProcess())
        {
            Dispatcher.BeginInvoke(() => { if (!_closing && IsVisible) Activate(); }, DispatcherPriority.Input);
            return;
        }
        ResetInteraction();
        if (_editor.IsTextEditing) return;
        Cancel();
    }

    private static readonly TimeSpan ActivationGrace = TimeSpan.FromMilliseconds(600);
    private DateTime _shownAt = DateTime.UtcNow;

    private static bool ForegroundIsOurProcess()
    {
        var fg = User32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        User32.GetWindowThreadProcessId(fg, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    // ---------- Текст ----------

    private void CommitTextEdit() => _editor.CommitTextEdit();

    // ---------- Отрисовка HUD ----------

    private void RefreshSelectionVisuals()
    {
        if (!_selection.HasSelection)
        {
            Dim.Hole = null;
            Dim.ShowHandles = false;
            Annotations.Clip = null;
            SizeLabel.Visibility = Visibility.Collapsed;
            UpdatePrivacyCheck();
            return;
        }
        var r = _selection.Rect;
        Dim.Hole = r;
        Dim.ShowHandles = (_mode == OverlayMode.Editing || _draggingHandle) && !_textMode;
        // Аннотации (особенно эффекты) не должны вылезать за выделение.
        Annotations.Clip = new RectangleGeometry(r.ToRect());

        // Размер области; оформление ничего не рисует вокруг выделения — о нём говорит только значок ✨.
        SizeText.Text = $"{r.Width} × {r.Height}";
        BeautifyMark.Visibility = _editor.BeautifyOn && !_textMode ? Visibility.Visible : Visibility.Collapsed;
        SizeLabel.Visibility = Visibility.Visible;
        SizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var dip = _map.PixelToDip(r);
        double top = dip.Top - SizeLabel.DesiredSize.Height - 6;
        if (top < _map.PixelToDip(MonitorAt(new PixelPoint(r.X, r.Y))).Top) top = dip.Top + 6;
        Canvas.SetLeft(SizeLabel, dip.Left);
        Canvas.SetTop(SizeLabel, top);

        if (Toolbar.Visibility == Visibility.Visible) PositionToolbar();
        if (TextHint.Visibility == Visibility.Visible) PositionTextHint();
        UpdatePrivacyCheck();
    }

    /// <summary>
    /// Под выделением по левому краю; если не влезает — над ним; если и там нет места — внутри у нижнего края.
    /// Панель целиком остаётся в рабочей области монитора, на котором центр выделения. Открытые flyout'ы
    /// переставляются следом: с внешней стороны панели, в пределах той же рабочей области.
    /// </summary>
    private void PositionToolbar()
    {
        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var placement = HudLayout.Place(_map.PixelToDip(_selection.Rect), SelectionWorkArea(), Toolbar.DesiredSize);
        Canvas.SetLeft(Toolbar, placement.Toolbar.X);
        Canvas.SetTop(Toolbar, placement.Toolbar.Y);
        _toolbarBelow = placement.Below;
        _editor.Flyouts.Reposition();
    }

    /// <summary>Рабочая область (без панели задач) монитора, на котором центр выделения, в DIP.</summary>
    private Rect SelectionWorkArea()
    {
        var r = _selection.Rect;
        return _map.PixelToDip(MonitorAt(new PixelPoint(r.X + r.Width / 2, r.Y + r.Height / 2), workArea: true));
    }

    /// <summary>Clamp, при котором нижняя граница выигрывает, если панель шире/выше доступного места.</summary>
    private static double ClampInto(double v, double min, double max) => HudLayout.ClampInto(v, min, max);

    private void UpdateMagnifier(PixelPoint p)
    {
        Magnifier.Visibility = Visibility.Visible;
        Magnifier.ShowColor = _settings.IsFeatureOn(FeatureCatalog.Pipette); // HEX у лупы — часть пипетки (C)
        Magnifier.Update(_shot.Bitmap, p);
        // После первой компоновки размер лупы известен; Measure на каждое движение мыши не нужен.
        if (Magnifier.ActualWidth <= 0 || Magnifier.ActualHeight <= 0)
            Magnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Magnifier.ActualWidth > 0 ? new Size(Magnifier.ActualWidth, Magnifier.ActualHeight) : Magnifier.DesiredSize;
        var cursor = _map.PixelToDip(p);
        var mon = _map.PixelToDip(MonitorAt(p));
        const double offset = 20;
        double x = cursor.X + offset, y = cursor.Y + offset;
        if (x + size.Width > mon.Right) x = cursor.X - offset - size.Width;
        if (y + size.Height > mon.Bottom) y = cursor.Y - offset - size.Height;
        Canvas.SetLeft(Magnifier, x);
        Canvas.SetTop(Magnifier, y);
        PositionPipetteBadge();
    }

    // ---------- Клавиатура ----------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Пока вводится текст, горячие клавиши оверлея не работают: цифры, Enter, Delete уходят в поле ввода.
        if (_editor.IsTextEditing) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        e.Handled = true;
        // Голый Alt: прилипание выключается сразу, посреди жеста; системе не отдаём (режим меню окна).
        if (key is Key.LeftAlt or Key.RightAlt) { RefreshSnapGestureSoon(); return; }
        if (_textMode) { e.Handled = TextKeyDown(key, ctrl); return; }
        switch (key)
        {
            case Key.Escape:
                StepBack();
                break;
            case Key.Enter:
                // Enter без выделения — окно или монитор под курсором, как по клику.
                if (_mode == OverlayMode.Selecting && !_selection.HasSelection && !HasGesture) SelectHintUnderCursor();
                Complete(CaptureAction.Copy);
                break;
            case Key.C when ctrl:
                Complete(CaptureAction.CopyExplicit);
                break;
            case Key.C when _mode == OverlayMode.Selecting && Magnifier.Visibility == Visibility.Visible && _settings.IsFeatureOn(FeatureCatalog.Pipette):
                CopyColorUnderMagnifier();
                break;
            case Key.S when ctrl:
                SaveAs();
                break;
            case Key.F3 when _settings.ActionAllowed(CaptureAction.Pin):
                Complete(CaptureAction.Pin);
                break;
            case Key.T when ctrl && _settings.IsFeatureOn(FeatureCatalog.Ocr):
                ToggleTextMode();
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when !_selection.HasSelection:
                e.Handled = false;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when Document.Selected is null:
                // Выбранную фигуру двигает редактор; без неё стрелки двигают (Shift — растягивают) выделение.
                if (HasGesture) break;
                int step = ctrl ? 10 : 1;
                int dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
                int dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
                if (shift) _selection.Resize(dx, dy); else _selection.Nudge(dx, dy);
                RefreshSelectionVisuals();
                break;
            default:
                // I, B, Ctrl+Z/Y, Delete и стрелки для выбранной фигуры, клавиши инструментов (только в режиме рисования).
                if (!_editor.HandleKey(key, mods, editing: _mode == OverlayMode.Editing)) e.Handled = false;
                break;
        }
    }

    // ---------- Режим текста ----------
    //
    // Ctrl+T (или кнопка OCR) распознаёт выделенную область и подсвечивает найденные слова.
    //   нажать на слове и тянуть  — выделение по порядку чтения, как в текстовом редакторе
    //   тянуть с пустого места    — рамка: выделяются только слова внутри неё («только левый верхний угол»)
    //   клик по слову / двойной   — слово / строка; Shift+клик — до этого слова; Ctrl — добавить к выделению
    //   Ctrl+A                    — весь текст
    //   Enter, Ctrl+C, «Копировать текст» — копирует выделенное (без выделения — весь текст) и закрывает
    //   Esc, правый клик          — снять выделение, затем выйти из режима; Ctrl+T — выйти сразу
    //   F3, Ctrl+S                — по-прежнему про картинку; рисование, undo и стрелки в режиме не работают

    private void ToggleTextMode()
    {
        if (_textMode) { ExitTextMode(); return; }
        if (_closing || HasGesture || !_settings.IsFeatureOn(FeatureCatalog.Ocr)) return;
        CommitTextEdit();
        if (_mode == OverlayMode.Selecting)
        {
            // Как Enter: без выделения берём окно или монитор под курсором.
            if (!_selection.HasSelection) SelectHintUnderCursor();
            if (!_selection.HasSelection) return;
            EnterEditing();
        }
        _ = EnterTextModeAsync();
    }

    private async Task EnterTextModeAsync()
    {
        Deselect();
        SetPipette(false);
        _editor.Ruler.Clear();
        ClearDistances();
        _textMode = true;
        RefreshOptions(); // в режиме текста параметров нет: flyout'ы закрываются
        int generation = ++_ocrGeneration;
        _ocr = null;
        _textSelection = new(); _textAnchor = null;
        Toolbar.SetTextMode(true);
        Dim.ShowHandles = false; // ручки в режиме текста не работают, поэтому и не показываем их
        RefreshSelectionVisuals(); // распознаётся исходный снимок, без оформления
        ShowHint(Loc.T("TextMode.Recognizing"));
        Cursor = Cursors.Wait;
        _refiner?.TextModeUsed();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Task<TimeSpan?>? prepared = null;

        var region = _selection.Rect;
        OcrLayout? layout = null;
        OcrImage? pixels = null;
        string? failure = null;
        Task<IReadOnlyList<DetectedCode>> codesTask = Task.FromResult<IReadOnlyList<DetectedCode>>([]);
        try
        {
            // Распознаём то же, что уйдёт в копию (без оформления): размытое и пикселизированное не превращается в текст.
            var image = ImageComposer.Compose(_shot.Bitmap, region, Document);
            if (_refiner is { IsActive: true })
            {
                pixels = OcrImage.FromBitmap(image); // та же картинка — для уточнения
                prepared = PrepareRefinerAsync(clock); // проверка модуля и сессии — параллельно с Windows OCR
            }
            codesTask = ScanCodesAsync(image); // QR и штрихкоды — параллельно с OCR
            layout = await OcrService.RecognizeLayoutAsync(image);
            if (layout is null) failure = Loc.T("TextMode.OcrUnavailable");
        }
        catch (Exception ex)
        {
            Screeny.Logging.Log.Error("OCR in overlay failed", ex);
            failure = Loc.F("TextMode.Failed", ex.Message);
        }
        if (generation != _ocrGeneration || _closing || !_textMode) return;

        var offset = new Vector(region.X, region.Y);
        bool hasText = layout is { IsEmpty: false };
        if (!hasText && pixels is not null)
        {
            // Windows OCR ничего не нашёл (мелкий текст, ники) — как раз то, что точное распознавание читает: ждём его.
            var refined = await RefineAsync(pixels, generation, prepared!, clock);
            if (generation != _ocrGeneration || _closing || !_textMode) return;
            pixels = null;
            if (refined is { IsEmpty: false }) (layout, hasText, failure) = (refined, true, null);
        }
        if (!hasText)
        {
            // Текста нет — ждём коды: только код без текста режим тоже показывает.
            var codes = await codesTask;
            if (generation != _ocrGeneration || _closing || !_textMode) return;
            if (codes.Count == 0) { FailTextMode(failure ?? Loc.T("TextMode.NotFound")); return; }
            ShowTextLayout(OcrLayout.Empty, region);
            ShowCodes(MapCodes(codes, offset));
            ShowHint(HintFor(_textSelection));
            if (_copyWhenReady) CopyText();
            return;
        }

        ShowTextLayout(layout!.Map(1, offset), region);
        if (_copyWhenReady) { CopyText(); return; } // Ctrl+T, Enter подряд: как раньше, весь текст одним движением
        if (pixels is not null) _ = RefineInBackgroundAsync(pixels, region, generation, prepared!, clock, layout.WordCount);
        var found = await codesTask;
        if (generation != _ocrGeneration || _closing || !_textMode || found.Count == 0) return;
        ShowCodes(MapCodes(found, offset));
        ShowHint(HintFor(_textSelection));
    }

    private void ShowTextLayout(OcrLayout layout, PixelRect region)
    {
        _ocr = layout;
        TextOverlay.Show(_ocr, region);
        TextOverlay.Clip = new RectangleGeometry(region.ToRect());
        ShowHint(HintFor(_textSelection));
        UpdateTextCursor(Mouse.GetPosition(PixelCanvas));
        if (_dev is not null) DevTextLayoutShown();
    }

    /// <summary>
    /// Подготовка уточнения сразу по Ctrl+T, пока работает Windows OCR: проверка файлов модуля и загрузка сессий (не дольше
    /// <see cref="PrepareLimit"/>). Возвращает момент готовности по <paramref name="clock"/> или null — модуль не загрузился
    /// (причина в журнале и в настройках) или режим уже закрыли.
    /// </summary>
    private async Task<TimeSpan?> PrepareRefinerAsync(System.Diagnostics.Stopwatch clock)
    {
        _refineCts?.Cancel();
        var cts = new CancellationTokenSource(); // отмена — выход из режима, закрытие оверлея, новый Ctrl+T (CancelRefine)
        _refineCts = cts;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        limit.CancelAfter(PrepareLimit);
        try { return await _refiner!.PrepareAsync(limit.Token) ? clock.Elapsed : null; }
        catch (OperationCanceledException)
        {
            if (!cts.IsCancellationRequested) Screeny.Logging.Log.Error($"Precise OCR not ready within {PrepareLimit.TotalSeconds:0} s; keeping the Windows OCR result");
            return null;
        }
    }

    /// <summary>
    /// Точное распознавание той же области. Бюджет <see cref="RefineBudget"/> считается от готовности сессий: первая
    /// проверка файлов и загрузка моделей в него не входят, так что весь путь ограничен <see cref="PrepareLimit"/> +
    /// <see cref="RefineBudget"/>. Раскладка — в пикселях области или null: не успело (в журнал), ошибка (в журнал) или
    /// отменено — выходом из режима, закрытием оверлея, новым Ctrl+T. Отмена останавливает и сам прогон ORT.
    /// </summary>
    private async Task<OcrLayout?> RefineAsync(OcrImage pixels, int generation, Task<TimeSpan?> prepared, System.Diagnostics.Stopwatch clock)
    {
        if (_refiner is null) return null;
        SetRefining(true);
        try
        {
            if (await prepared is not { } readyAt || generation != _ocrGeneration || _refineCts is not { } cts) return null;
            var budget = RefineBudget - (clock.Elapsed - readyAt);
            if (budget <= TimeSpan.Zero) { Screeny.Logging.Log.Error("Precise OCR skipped: the time budget ran out while Windows OCR was running"); return null; }
            using var timeout = new CancellationTokenSource(budget);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cts.Token);
            try { return await _refiner.RecognizeAsync(pixels, linked.Token); }
            catch (OperationCanceledException)
            {
                if (timeout.IsCancellationRequested && !cts.IsCancellationRequested)
                    Screeny.Logging.Log.Error($"Precise OCR timed out ({RefineBudget.TotalSeconds:0} s after the sessions were ready, {pixels.W}x{pixels.H}); keeping the Windows OCR result");
                return null;
            }
        }
        catch (Exception ex) { Screeny.Logging.Log.Error("Precise OCR failed; keeping the Windows OCR result", ex); return null; }
        finally
        {
            if (generation == _ocrGeneration) SetRefining(false);
        }
    }

    private async Task RefineInBackgroundAsync(OcrImage pixels, PixelRect region, int generation, Task<TimeSpan?> prepared, System.Diagnostics.Stopwatch clock, int windowsWords)
    {
        var refined = await RefineAsync(pixels, generation, prepared, clock);
        if (generation != _ocrGeneration || _closing || !_textMode || refined is not { IsEmpty: false }) return;
        Screeny.Logging.Log.Error($"Text mode: precise OCR layout replaces Windows OCR ({windowsWords} -> {refined.WordCount} words)");
        ApplyRefined(refined.Map(1, new Vector(region.X, region.Y)), region);
    }

    /// <summary>
    /// Уточнённая раскладка вместо показанной. Выделения нет — просто замена; есть — переносится по месту
    /// (<see cref="LayoutRemap"/>). Посреди жеста выделения индексы якоря и диапазона относятся к прежней раскладке:
    /// замена ждёт конца жеста. Enter и копирование до этого момента берут то, что на экране.
    /// </summary>
    private void ApplyRefined(OcrLayout refined, PixelRect region)
    {
        if (_textPress is not null) { _pendingRefined = refined; return; }
        var old = _ocr;
        var selection = old is null ? new HashSet<int>() : LayoutRemap.Selection(old, _textSelection, refined);
        int? anchor = old is null ? null : LayoutRemap.Word(old, _textAnchor, refined);
        ShowTextLayout(refined, region);
        _textSelection = selection;
        _textAnchor = anchor ?? (selection.Count > 0 ? selection.Min() : null);
        ShowTextSelection();
    }

    private void ApplyPendingRefined()
    {
        if (_pendingRefined is not { } refined || !_textMode) return;
        _pendingRefined = null;
        ApplyRefined(refined, _selection.Rect);
    }

    private void SetRefining(bool on)
    {
        if (_refining == on) return;
        _refining = on;
        if (!_textMode || _closing) return;
        if (_ocr is not null) ShowHint(HintFor(_textSelection));
        else if (on) ShowHint(Loc.T("TextMode.Refining"));
    }

    private void CancelRefine()
    {
        _refineCts?.Cancel();
        _refineCts = null;
        _pendingRefined = null;
        _refining = false;
    }

    private static IReadOnlyList<DetectedCode> MapCodes(IReadOnlyList<DetectedCode> codes, Vector offset)
        => codes.Select(c => c with { Box = Rect.Offset(c.Box, offset) }).ToList();

    /// <summary>Распознавание не дало слов: сообщение на пару секунд и обратно к рисованию.</summary>
    private void FailTextMode(string message)
    {
        ExitTextMode();
        ShowHint(message, TimeSpan.FromSeconds(message.Length > 30 ? 4 : 1.8));
    }

    private void ExitTextMode()
    {
        if (!_textMode) return;
        _textMode = false;
        _ocrGeneration++;
        CancelRefine();
        _ocr = null;
        _textSelection = new(); _textAnchor = null; _textPress = null; _textDragging = false; _copyWhenReady = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        ClearCodes();
        TextOverlay.Clear();
        Dim.ShowHandles = _mode == OverlayMode.Editing;
        RefreshSelectionVisuals();
        Toolbar.SetTextMode(false);
        HideHint();
        RefreshOptions();
        UpdateHover(CurrentMousePixel(), Mouse.GetPosition(PixelCanvas));
    }

    private string HintFor(IReadOnlySet<int> selection)
    {
        if (_ocr is { IsEmpty: true } && _codes.Count > 0)
            return Loc.T(_codes.Count == 1 ? "TextMode.OneCode" : "TextMode.ManyCodes");
        var hint = Loc.T(selection.Count > 0 ? "TextMode.HintSelection" : _codes.Count > 0 ? "TextMode.HintNoSelectionCodes" : "TextMode.HintNoSelection");
        return _refining ? Loc.F("TextMode.WithRefining", hint) : hint;
    }

    private void ShowTextSelection(Rect? marquee = null)
    {
        TextOverlay.SetSelection(_textSelection, marquee);
        if (_ocr is not null) ShowHint(HintFor(_textSelection));
    }

    private void TextMouseDown(MouseButtonEventArgs e)
    {
        if (_ocr is null) return;
        if (e.ClickCount == 1 && CodeMouseDown(e)) return;
        var p = e.GetPosition(PixelCanvas);
        var mods = Keyboard.Modifiers;
        int? word = _ocr.WordAt(p, 3 * _map.DpiScale);

        if (e.ClickCount >= 2 && word is { } w2)
        {
            _textSelection = mods.HasFlag(ModifierKeys.Control) ? Union(_textBase, _ocr.LineRange(w2)) : _ocr.LineRange(w2);
            _textAnchor = w2;
            ShowTextSelection();
            return;
        }
        bool extend = mods.HasFlag(ModifierKeys.Shift) && _textAnchor is not null;
        if (extend && word is { } ws)
        {
            _textSelection = _ocr.Range(_textAnchor!.Value, ws);
            ShowTextSelection();
        }
        _textBase = mods.HasFlag(ModifierKeys.Control) ? new HashSet<int>(_textSelection) : new HashSet<int>();
        _textPress = p;
        // Shift: тянем от прежнего начала выделения по порядку чтения.
        _textDragFromWord = word is not null || extend;
        _textDragging = extend;
        if (word is { } w && !extend) _textAnchor = w;
        CaptureMouse();
    }

    private void TextMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(PixelCanvas);
        if (_textPress is not { } start || e.LeftButton != MouseButtonState.Pressed || _ocr is null)
        {
            if (!IsFromHud(e.OriginalSource)) UpdateTextCursor(p); else Cursor = Cursors.Arrow;
            return;
        }
        double threshold = DragThreshold * _map.DpiScale;
        if (!_textDragging && Math.Abs(p.X - start.X) < threshold && Math.Abs(p.Y - start.Y) < threshold) return;
        _textDragging = true;

        if (_textDragFromWord && _textAnchor is { } anchor)
        {
            // По порядку чтения: до слова под курсором, а между словами — до ближайшего по строке.
            int target = _ocr.WordAt(p, 3 * _map.DpiScale) ?? NearestWord(p) ?? anchor;
            _textSelection = Union(_textBase, _ocr.Range(anchor, target));
            ShowTextSelection();
        }
        else
        {
            var area = new Rect(start, p);
            area.Intersect(_selection.Rect.ToRect());
            _textSelection = Union(_textBase, area.IsEmpty ? new HashSet<int>() : _ocr.InRect(area));
            ShowTextSelection(area.IsEmpty ? null : area);
        }
    }

    private void TextMouseUp(MouseButtonEventArgs e)
    {
        if (_textPress is null || _ocr is null) return;
        bool click = !_textDragging;
        _textPress = null; _textDragging = false;
        ReleaseMouseCapture();
        if (click)
        {
            int? word = _ocr.WordAt(e.GetPosition(PixelCanvas), 3 * _map.DpiScale);
            if (word is { } w)
            {
                var set = new HashSet<int>(_textBase);
                if (!set.Add(w) && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) set.Remove(w);
                _textSelection = set;
            }
            else if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _textSelection = new(); _textAnchor = null; }
        }
        ShowTextSelection();
        ApplyPendingRefined();
    }

    /// <summary>Слово, ближайшее к точке: тянуть можно и по промежуткам между словами.</summary>
    private int? NearestWord(Point p)
    {
        if (_ocr is null || _ocr.IsEmpty) return null;
        // Ниже последней строки до конца текста, выше первой до начала, как в текстовом редакторе.
        if (p.Y > OcrLayout.LineBox(_ocr.Lines[^1]).Bottom) return _ocr.WordCount - 1;
        if (p.Y < OcrLayout.LineBox(_ocr.Lines[0]).Top) return 0;
        int best = 0; double bestD = double.MaxValue;
        for (int i = 0; i < _ocr.WordCount; i++)
        {
            var b = _ocr.Word(i).Box;
            double dx = Math.Max(0, Math.Max(b.Left - p.X, p.X - b.Right)), dy = Math.Max(0, Math.Max(b.Top - p.Y, p.Y - b.Bottom));
            double d = dx * dx + dy * dy * 4; // по вертикали дальше: держимся своей строки
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private static HashSet<int> Union(HashSet<int> a, HashSet<int> b) { var r = new HashSet<int>(a); r.UnionWith(b); return r; }

    private void UpdateTextCursor(Point p)
    {
        if (_ocr is null) { Cursor = Cursors.Wait; return; }
        bool inside = _selection.Rect.ToRect().Contains(p);
        Cursor = !inside ? Cursors.Arrow
            : CodeAt(p) is not null ? Cursors.Hand
            : _ocr.WordAt(p, 3 * _map.DpiScale) is not null ? Cursors.IBeam : Cursors.Cross;
    }

    /// <returns>Клавиша обработана. Сочетания с Alt (Alt+F4) режим текста не перехватывает.</returns>
    private bool TextKeyDown(Key key, bool ctrl)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return false;
        switch (key)
        {
            case Key.Escape:
                StepBack();
                return true;
            case Key.T when ctrl:
                ExitTextMode();
                return true;
            case Key.A when ctrl:
                if (_ocr is not null && !_ocr.IsEmpty) { _textSelection = _ocr.All(); _textAnchor = 0; ShowTextSelection(); }
                return true;
            case Key.Enter:
            case Key.C when ctrl:
                CopyText();
                return true;
            case Key.F3 when _settings.ActionAllowed(CaptureAction.Pin):
                ExitTextMode();
                Complete(CaptureAction.Pin);
                return true;
            case Key.S when ctrl:
                SaveAs(); // отмена диалога оставляет в режиме текста с уже распознанным текстом
                return true;
            default:
                return true; // цифры инструментов, undo, Delete, стрелки в режиме текста ничего не делают
        }
    }

    private void CopyText()
    {
        if (_closing || !_textMode) return;
        if (_ocr is null) { _copyWhenReady = true; ShowHint(Loc.T("TextMode.CopyWhenReady")); return; }
        var text = _ocr.TextOf(_textSelection);
        // Только код без текста: Enter копирует его содержимое (открытого в плашке или единственного).
        if (_ocr.IsEmpty && (_openCode ?? (_codes.Count == 1 ? 0 : null)) is { } code) text = _codes[code].Text;
        else if (_ocr.IsEmpty && _codes.Count > 1) { ShowHint(Loc.T("TextMode.ClickCode")); return; }
        if (string.IsNullOrWhiteSpace(text)) { ShowHint(Loc.T("TextMode.NoTextInSelection")); return; }
        var region = _selection.Rect;
        _closing = true;
        Hide();
        // Картинка в результате не нужна, текст в историю не попадает: передаём снимок как есть, без рендера.
        Completed?.Invoke(new CaptureResult(_shot.Bitmap, CaptureAction.CopyText, region, region.Offset(_shot.VirtualRect.X, _shot.VirtualRect.Y), Text: text));
        Close();
    }

    private void ShowHint(string text, TimeSpan? hideAfter = null)
    {
        _hintTimer?.Stop();
        TextHintText.Text = text;
        TextHint.Visibility = Visibility.Visible;
        PositionTextHint();
        if (hideAfter is { } after)
        {
            _hintTimer = new DispatcherTimer { Interval = after };
            // В режиме текста временное сообщение («Содержимое кода скопировано») сменяется обычной подсказкой.
            _hintTimer.Tick += (_, _) =>
            {
                _hintTimer?.Stop();
                if (!_textMode) HideHint();
                else if (_ocr is not null) ShowHint(HintFor(_textSelection));
            };
            _hintTimer.Start();
        }
    }

    private void HideHint() { _hintTimer?.Stop(); TextHint.Visibility = Visibility.Collapsed; }

    /// <summary>Подсказка справа от подписи размера, над выделением; не влезает — у левого края под ней.</summary>
    private void PositionTextHint()
    {
        if (!_selection.HasSelection) return;
        TextHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = Canvas.GetLeft(SizeLabel) + SizeLabel.DesiredSize.Width + 6, y = Canvas.GetTop(SizeLabel);
        if (double.IsNaN(x) || double.IsNaN(y)) return;
        var center = new PixelPoint(_selection.Rect.X + _selection.Rect.Width / 2, _selection.Rect.Y + _selection.Rect.Height / 2);
        var mon = _map.PixelToDip(MonitorAt(center));
        if (x + TextHint.DesiredSize.Width > mon.Right - 8)
        {
            x = Math.Max(mon.Left + 8, mon.Right - 8 - TextHint.DesiredSize.Width);
            y += SizeLabel.DesiredSize.Height + 6;
        }
        Canvas.SetLeft(TextHint, x);
        Canvas.SetTop(TextHint, y - (TextHint.DesiredSize.Height - SizeLabel.DesiredSize.Height) / 2);
    }

    // ---------- Правка ----------

    private void SelectHintUnderCursor()
    {
        var p = CurrentMousePixel();
        var rect = Dim.Hint ?? WindowFinder.PickTopmostAt(_windows, p)?.Rect ?? MonitorAt(p);
        _selection.Rect = rect.Clamp(_shot.PixelBounds);
    }

    // ---------- Завершение ----------

    /// <summary>
    /// Ctrl+S: диалог показывается поверх оверлея, пока снимок ещё на экране. Отмена — остаёмся в редакторе.
    /// </summary>
    private void SaveAs()
    {
        if (_closing) return;
        CommitTextEdit();
        ResetInteraction();
        if (!_selection.HasSelection) return;
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("Common.SaveScreenshotTitle"),
            Filter = "PNG|*.png|JPEG|*.jpg",
            FileName = $"Screeny_{DateTime.Now:yyyyMMdd_HHmmss}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            AddExtension = true,
        };
        string? path = null;
        _dialogOpen = true;
        try
        {
            if (dialog.ShowDialog(this) == true)
                path = FileSaver.ConfirmResolvedPath(this, dialog.FilterIndex, dialog.FileName);
        }
        finally { _dialogOpen = false; }
        if (path is null)
        {
            Activate();
            Focus();
            return;
        }
        // Формат берётся из выбранного фильтра, а не из набранного расширения: "shot.bmp" при PNG → "shot.bmp.png".
        Complete(CaptureAction.Save, path);
    }

    private void Complete(CaptureAction action) => Complete(action, null);

    private void Complete(CaptureAction action, string? savePath)
    {
        if (_closing || !_settings.ActionAllowed(action)) return;
        CommitTextEdit();
        ResetInteraction(); // живая фигура не должна попасть в экспорт
        if (!_selection.HasSelection) return;
        var region = _selection.Rect;
        Document.Selected = null;
        var (image, screenRegion) = ComposeResult(region); // с оформлением, если оно включено
        var (json, original) = HistorySidecarFor(region);
        _closing = true;
        Hide();
        Completed?.Invoke(new CaptureResult(image, action, region, screenRegion, savePath, AnnotationsJson: json, Original: original));
        Close();
    }

    private void Cancel()
    {
        if (_closing) return;
        _closing = true;
        ResetInteraction();
        Close();
    }
}
