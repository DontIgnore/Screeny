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
/// Test instance (<c>--dev --overlay-test</c>): overlay doesn't close on deactivation
/// (captured via PrintWindow), can open immediately with selection and ready scenario.
/// </summary>
public sealed record OverlayDevOptions(PixelRect? Select, string? Scenario, string? Source = null);

/// <summary>
/// Fullscreen window over entire virtual screen with “frozen” screenshot.
/// Selection and annotations stored in screenshot pixels; displayed via ScaleTransform(1/dpi).
/// </summary>
public partial class OverlayWindow : Window
{
    private const int DragThreshold = 3;

    private readonly Screenshot _shot;
    private readonly List<WindowInfo> _windows;
    private readonly AppSettings _settings;
    private readonly SelectionModel _selection;
    /// <summary>Drawing: tools, text, select, options (flyouts), eyedropper, ruler, undo, decoration.</summary>
    private readonly AnnotationEditor _editor;
    private bool _toolbarBelow = true;  // panel below selection: its button flyouts open down
    private DispatcherTimer? _badgeTimer;
    private CoordinateMapper _map;
    private PixelPoint? _dragAnchor;   // start dragging new selection
    private bool _thresholdPassed;     // dragging new selection moved beyond click threshold
    private bool _draggingHandle;
    private OverlayMode _mode = OverlayMode.Selecting;
    private bool _closing;             // Complete/Cancel already started: Deactivated from Hide() ignored
    private bool _dialogOpen;          // our modal dialog open (save): deactivation expected

    // Text mode (Ctrl+T): recognized words selectable and copyable.
    private bool _textMode;
    private int _ocrGeneration;        // each entry/exit; stale recognition result discarded
    private OcrLayout? _ocr;           // word borders in screenshot pixels; null — still recognizing
    private HashSet<int> _textSelection = new();
    private int? _textAnchor;          // word selection starts from (also for Shift+click)
    private Point? _textPress;         // press point of current selection gesture
    private bool _textDragFromWord;    // gesture started on word → reading-order selection, else border
    private bool _textDragging;        // gesture moved beyond click threshold
    private HashSet<int> _textBase = new(); // selection before gesture: Shift/Ctrl add to it
    private DispatcherTimer? _hintTimer;
    private bool _copyWhenReady;       // Enter/Ctrl+C pressed while still recognizing: copy as soon as ready
    // Precise recognition (downloadable module): Windows OCR result shown immediately, refinement in background replaces layout.
    private readonly IOcrRefiner? _refiner;
    private CancellationTokenSource? _refineCts;
    private bool _refining;            // refinement running: hint shows “refining…”
    private OcrLayout? _pendingRefined; // refinement arrived mid-selection gesture: applied when gesture ends
    /// <summary>Refinement must finish within this from session readiness, else Windows OCR result stays.</summary>
    private static readonly TimeSpan RefineBudget = TimeSpan.FromSeconds(5);
    /// <summary>Preparation (module file check and session load, first ≈0.3s) — no longer than this.</summary>
    private static readonly TimeSpan PrepareLimit = TimeSpan.FromSeconds(10);

    public event Action<CaptureResult>? Completed;
    /// <summary>Overlay closed because monitor configuration changed.</summary>
    public event Action? DisplaysChanged;

    private readonly OverlayDevOptions? _dev;

    /// <param name="refiner">Precise recognition for text mode; null — only Windows OCR.</param>
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

        // Decoration state and parameters remembered in settings for next screenshots.
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

    /// <summary>Test launch ready selection — as if just selected by mouse — and scenario.</summary>
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

    // ---------- Placement and DPI ----------

    private void PlaceOverVirtualScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var r = _shot.VirtualRect;
        // Place window in device pixels via Win32, bypassing WPF DIP conversion.
        // Already in place — do nothing: also protects from WM_DPICHANGED loop.
        User32.GetWindowRect(hwnd, out var current);
        if (current.Left != r.X || current.Top != r.Y || current.Right != r.Right || current.Bottom != r.Bottom)
            User32.SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, r.X, r.Y, r.Width, r.Height, User32.SWP_NOACTIVATE);
        UpdateScale(VisualTreeHelper.GetDpi(this).DpiScaleX);
    }

    /// <summary>
    /// Window across virtual screen with different-DPI monitors: on WM_DPICHANGED (incl. inside
    /// our own SetWindowPos) WPF places window into system-proposed rectangle. Recalculate scale
    /// and after message handling return window to virtual screen.
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
    /// Screenshot and window coordinates tied to monitor layout at capture. If it changed
    /// (monitor disconnected, resolution changed), cannot continue — close without result.
    /// Event arrives from SystemEvents thread, so move to window thread.
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
        base.OnClosed(e);
    }

    // ---------- Coordinates ----------

    private PixelPoint ToPixel(Point canvasPoint)
    {
        var b = _shot.PixelBounds;
        return new PixelPoint(Math.Clamp((int)Math.Floor(canvasPoint.X), 0, b.Width - 1), Math.Clamp((int)Math.Floor(canvasPoint.Y), 0, b.Height - 1));
    }

    /// <summary>Mouse position in screenshot pixels — via inverse PixelCanvas transform, i.e. exactly as during rendering.</summary>
    private PixelPoint MousePixel(MouseEventArgs e) => ToPixel(e.GetPosition(PixelCanvas));
    private PixelPoint CurrentMousePixel() => ToPixel(Mouse.GetPosition(PixelCanvas));

    /// <summary>Point for tools: fractional pixels clamped to selection (draw only inside).</summary>
    private Point ToolPoint(MouseEventArgs e)
    {
        var p = e.GetPosition(PixelCanvas);
        var r = _selection.Rect;
        return new Point(Math.Clamp(p.X, r.X, r.Right), Math.Clamp(p.Y, r.Y, r.Bottom));
    }

    /// <summary>Monitor rectangle (or work area without taskbar) under point, in screenshot pixels.</summary>
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
            if (ReferenceEquals(d, Toolbar) || ReferenceEquals(d, TextEditor) || _editor.Flyouts.Contains(d) || ReferenceEquals(d, CodePopup)) return true;
        return false;
    }

    // ---------- Mouse ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (IsFromHud(e.OriginalSource)) return;
        if (_textMode) { e.Handled = true; TextMouseDown(e); return; }
        CommitTextEdit();
        var p = MousePixel(e);
        e.Handled = true;
        // Eyedropper beats handles and tools: click anywhere on screenshot picks color.
        if (_editor.PipetteOn) { _editor.PickColor(p); return; }

        var handle = _selection.HitTest(p, HandleTolerance);
        if (_mode == OverlayMode.Editing)
        {
            var exact = e.GetPosition(PixelCanvas);
            bool resizeHandle = handle is not (Handle.None or Handle.Move);
            // Select tool on empty space inside area moves area itself.
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
            // Click outside selection by drawing tool ignored: draw only inside area.
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
        _editor.Flyouts.CloseAll(); // panel moves with border
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
        if (IsFromHud(e.OriginalSource)) { Cursor = Cursors.Arrow; if (_editor.PipetteOn) Magnifier.Visibility = Visibility.Collapsed; return; }
        UpdateHover(p, e.GetPosition(PixelCanvas));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_textMode) { TextMouseUp(e); return; }
        var p = MousePixel(e);
        // Gesture state reset before ReleaseMouseCapture: it synchronously calls OnLostMouseCapture.
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

    /// <summary>Mouse capture lost not by our will (Alt+Tab, popup window etc.) — gesture aborted entirely.</summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // Event bubbles from panel buttons and input field too; only window capture matters.
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

    // ---------- Modes and reset ----------

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
    /// Unified reset of current mouse gesture: live shape or annotation offset rolled back, handle drag
    /// ends in place, started new selection discarded, mouse capture released. Idempotent.
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
    /// Esc and right-click remove one layer: gesture → selected annotation / drawing tool →
    /// area selection (annotations remain) → close overlay.
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
    /// Back to area selection. Annotations not deleted: they are in screenshot coordinates, stay
    /// and are clipped by new selection on export.
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
    /// Alt+Tab, UAC, foreign popup: Topmost overlay without focus doesn't react to Esc and blocks work.
    /// Close without result, except when activity left expectedly (our dialog, text input).
    /// </summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_closing || _dialogOpen || _dev is not null) return;
        // Right after show, closing tray menu or Show() itself may briefly take focus:
        // not user leaving, return focus to self. Same if another our window becomes active.
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

    // ---------- Text ----------

    private void CommitTextEdit() => _editor.CommitTextEdit();

    // ---------- HUD rendering ----------

    private void RefreshSelectionVisuals()
    {
        if (!_selection.HasSelection)
        {
            Dim.Hole = null;
            Dim.ShowHandles = false;
            Annotations.Clip = null;
            SizeLabel.Visibility = Visibility.Collapsed;
            return;
        }
        var r = _selection.Rect;
        Dim.Hole = r;
        Dim.ShowHandles = (_mode == OverlayMode.Editing || _draggingHandle) && !_textMode;
        // Annotations (esp. effects) must not extend beyond selection.
        Annotations.Clip = new RectangleGeometry(r.ToRect());

        // Area size; decoration draws nothing around selection — only ✨ icon indicates.
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
    }

    /// <summary>
    /// Below selection along left edge; doesn't fit — above; no place there — inside at bottom edge.
    /// Panel entirely stays in work area of monitor with selection center. Open flyouts
    /// reposition after: on panel outer side, within same work area.
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

    /// <summary>Work area (without taskbar) of monitor with selection center, in DIP.</summary>
    private Rect SelectionWorkArea()
    {
        var r = _selection.Rect;
        return _map.PixelToDip(MonitorAt(new PixelPoint(r.X + r.Width / 2, r.Y + r.Height / 2), workArea: true));
    }

    /// <summary>Clamp where bottom edge wins if panel wider/taller than available space.</summary>
    private static double ClampInto(double v, double min, double max) => HudLayout.ClampInto(v, min, max);

    private void UpdateMagnifier(PixelPoint p)
    {
        Magnifier.Visibility = Visibility.Visible;
        Magnifier.ShowColor = _settings.IsFeatureOn(FeatureCatalog.Pipette); // HEX at magnifier is part of eyedropper (C)
        Magnifier.Update(_shot.Bitmap, p);
        // After first layout magnifier size known; no Measure per mouse move.
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

    // ---------- Keyboard ----------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // While text entered, overlay hotkeys don't work: digits, Enter, Delete go to input field.
        if (_editor.IsTextEditing) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        e.Handled = true;
        // Bare Alt: snapping disabled immediately mid-gesture; not given to system (window menu mode).
        if (key is Key.LeftAlt or Key.RightAlt) { RefreshSnapGestureSoon(); return; }
        if (_textMode) { e.Handled = TextKeyDown(key, ctrl); return; }
        switch (key)
        {
            case Key.Escape:
                StepBack();
                break;
            case Key.Enter:
                // Enter without selection — window or monitor under cursor, like click.
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
                // Selected shape moved by editor; without it arrows move (Shift — stretch) selection.
                if (HasGesture) break;
                int step = ctrl ? 10 : 1;
                int dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
                int dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
                if (shift) _selection.Resize(dx, dy); else _selection.Nudge(dx, dy);
                RefreshSelectionVisuals();
                break;
            default:
                // I, B, Ctrl+Z/Y, Delete and arrows for selected shape, tool keys (only in drawing mode).
                if (!_editor.HandleKey(key, mods, editing: _mode == OverlayMode.Editing)) e.Handled = false;
                break;
        }
    }

    // ---------- Text mode ----------
    //
    // Ctrl+T (or OCR button) recognizes selected area and highlights found words.
    //   click word and drag — reading-order selection, like text editor
    //   drag from empty place — border: only words inside selected (“only top-left corner”)
    //   click word / double — word / line; Shift+click — up to word; Ctrl — add to selection
    //   Ctrl+A — all text
    //   Enter, Ctrl+C, “Copy text” — copies selection (without selection — all text) and closes
    //   Esc, right-click — clear selection, then exit mode; Ctrl+T — exit immediately
    //   F3, Ctrl+S — still about image; drawing, undo and arrows don't work in mode

    private void ToggleTextMode()
    {
        if (_textMode) { ExitTextMode(); return; }
        if (_closing || HasGesture || !_settings.IsFeatureOn(FeatureCatalog.Ocr)) return;
        CommitTextEdit();
        if (_mode == OverlayMode.Selecting)
        {
            // Like Enter: without selection take window or monitor under cursor.
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
        _textMode = true;
        RefreshOptions(); // in text mode no options: flyouts close
        int generation = ++_ocrGeneration;
        _ocr = null;
        _textSelection = new(); _textAnchor = null;
        Toolbar.SetTextMode(true);
        Dim.ShowHandles = false; // handles don't work in text mode, so not shown
        RefreshSelectionVisuals(); // original screenshot recognized, without decoration
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
            // Recognize same as will go to copy (without decoration): blurred/pixelated doesn't become text.
            var image = ImageComposer.Compose(_shot.Bitmap, region, Document);
            if (_refiner is { IsActive: true })
            {
                pixels = OcrImage.FromBitmap(image); // same image — for refinement
                prepared = PrepareRefinerAsync(clock); // module check and sessions — parallel with Windows OCR
            }
            codesTask = ScanCodesAsync(image); // QR/barcodes — parallel with OCR
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
            // Windows OCR found nothing (small text, nicks) — exactly what precise recognition reads: wait for it.
            var refined = await RefineAsync(pixels, generation, prepared!, clock);
            if (generation != _ocrGeneration || _closing || !_textMode) return;
            pixels = null;
            if (refined is { IsEmpty: false }) (layout, hasText, failure) = (refined, true, null);
        }
        if (!hasText)
        {
            // No text — wait for codes: mode shows code even without text.
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
        if (_copyWhenReady) { CopyText(); return; } // Ctrl+T, Enter in a row: as before, whole text in one move
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
    /// Refinement preparation immediately on Ctrl+T while Windows OCR runs: module file check and session load (no longer than
    /// <see cref="PrepareLimit"/>). Returns readiness moment by <paramref name="clock"/> or null — module failed to load
    /// (reason in log and settings) or mode already closed.
    /// </summary>
    private async Task<TimeSpan?> PrepareRefinerAsync(System.Diagnostics.Stopwatch clock)
    {
        _refineCts?.Cancel();
        var cts = new CancellationTokenSource(); // cancel — exit mode, close overlay, new Ctrl+T (CancelRefine)
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
    /// Precise recognition of same area. Budget <see cref="RefineBudget"/> counted from session readiness: first
    /// file check and model load not included, so whole path bounded by <see cref="PrepareLimit"/> +
    /// <see cref="RefineBudget"/>. Layout in area pixels or null: didn't make it (log), error (log) or
    /// canceled — by mode exit, overlay close, new Ctrl+T. Cancel stops ORT run itself.
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
    /// Refined layout replaces shown. No selection — just replacement; selection — moved by location
    /// (<see cref="LayoutRemap"/>). Mid-selection-gesture anchor/range indices refer to old layout:
    /// replacement waits for gesture end. Enter and copy before that take what's on screen.
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

    /// <summary>Recognition gave no words: message for couple seconds and back to drawing.</summary>
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
        // Shift: drag from previous selection start in reading order.
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
            // Reading order: up to word under cursor, between words — to nearest in line.
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

    /// <summary>Word nearest to point: can drag through gaps between words.</summary>
    private int? NearestWord(Point p)
    {
        if (_ocr is null || _ocr.IsEmpty) return null;
        // Below last line to text end, above first to start, like text editor.
        if (p.Y > OcrLayout.LineBox(_ocr.Lines[^1]).Bottom) return _ocr.WordCount - 1;
        if (p.Y < OcrLayout.LineBox(_ocr.Lines[0]).Top) return 0;
        int best = 0; double bestD = double.MaxValue;
        for (int i = 0; i < _ocr.WordCount; i++)
        {
            var b = _ocr.Word(i).Box;
            double dx = Math.Max(0, Math.Max(b.Left - p.X, p.X - b.Right)), dy = Math.Max(0, Math.Max(b.Top - p.Y, p.Y - b.Bottom));
            double d = dx * dx + dy * dy * 4; // vertically further: stay on own line
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

    /// <returns>Key handled. Alt combos (Alt+F4) not intercepted by text mode.</returns>
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
                SaveAs(); // dialog cancel leaves text mode with already recognized text
                return true;
            default:
                return true; // tool digits, undo, Delete, arrows do nothing in text mode
        }
    }

    private void CopyText()
    {
        if (_closing || !_textMode) return;
        if (_ocr is null) { _copyWhenReady = true; ShowHint(Loc.T("TextMode.CopyWhenReady")); return; }
        var text = _ocr.TextOf(_textSelection);
        // Only code without text: Enter copies its content (open in plaque or only one).
        if (_ocr.IsEmpty && (_openCode ?? (_codes.Count == 1 ? 0 : null)) is { } code) text = _codes[code].Text;
        else if (_ocr.IsEmpty && _codes.Count > 1) { ShowHint(Loc.T("TextMode.ClickCode")); return; }
        if (string.IsNullOrWhiteSpace(text)) { ShowHint(Loc.T("TextMode.NoTextInSelection")); return; }
        var region = _selection.Rect;
        _closing = true;
        Hide();
        // Image not needed, text not into history: pass screenshot as-is, without render.
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
            // In text mode temporary message (“Code content copied”) replaced by normal tooltip.
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

    /// <summary>Tooltip right of size label, above selection; doesn't fit — at left edge below.</summary>
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

    // ---------- Editing ----------

    private void SelectHintUnderCursor()
    {
        var p = CurrentMousePixel();
        var rect = Dim.Hint ?? WindowFinder.PickTopmostAt(_windows, p)?.Rect ?? MonitorAt(p);
        _selection.Rect = rect.Clamp(_shot.PixelBounds);
    }

    // ---------- Completion ----------

    /// <summary>
    /// Ctrl+S: dialog shown over overlay while screenshot still on screen. Cancel — stay in editor.
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
        // Format from selected filter, not typed extension: "shot.bmp" with PNG → "shot.bmp.png".
        Complete(CaptureAction.Save, path);
    }

    private void Complete(CaptureAction action) => Complete(action, null);

    private void Complete(CaptureAction action, string? savePath)
    {
        if (_closing || !_settings.ActionAllowed(action)) return;
        CommitTextEdit();
        ResetInteraction(); // live shape must not enter export
        if (!_selection.HasSelection) return;
        var region = _selection.Rect;
        Document.Selected = null;
        var (image, screenRegion) = ComposeResult(region); // with decoration if enabled
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
