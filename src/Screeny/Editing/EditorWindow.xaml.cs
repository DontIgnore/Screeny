using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Screeny.Annotations;
using Screeny.Export;
using Screeny.Features;
using Screeny.History;
using Screeny.Logging;
using Screeny.Overlay;
using Screeny.Overlay.Tools;
using Screeny.Settings;
using Screeny.Themes;

namespace Screeny.Editing;

/// <summary>Что окну редактора нужно от приложения.</summary>
/// <param name="Save">Фоновое кодирование и запись истории и выбранного файла.</param>
/// <param name="Pin">Закрепить картинку на экране.</param>
/// <param name="IsShuttingDown">Приложение завершается: не спрашивать про несохранённые пометки.</param>
public sealed record EditorServices(Func<BitmapSource, string, byte[]?, string?, Task<ImageSaveResult>> Save,
    Action<BitmapSource> Pin, Func<int> ReserveClipboard, Func<int, bool> IsCurrentClipboard,
    Action<Task> Track, Func<bool> IsShuttingDown);

/// <summary>
/// Повторное редактирование снимка из истории: тот же редактор аннотаций, что в оверлее, на холсте с масштабом.
/// «Копировать», «Сохранить как…» и «Закрепить» добавляют новую запись в историю; исходная не меняется.
/// <para>
/// Режим «Оригинал» (только если у записи есть <c>.orig.png</c>) — просмотр чистого снимка: пометки скрыты, рисовать
/// нельзя, а «Начать заново с оригинала» удаляет пометки одним шагом undo и возвращает к рисованию. Действия в этом
/// режиме сохраняют то, что видно: снимок без пометок (и пустой документ пометок у новой записи).
/// </para>
/// </summary>
public partial class EditorWindow : ChromeWindow, IAnnotationEditorHost, IFlyoutHost
{
    private const double FitMargin = 24;      // поля вокруг снимка в режиме «вписать», DIP
    private const double ZoomStep = 1.25;
    private const double MaxDeviceZoom = 8;   // 800 %

    private readonly EditSession _session;
    private readonly AppSettings _settings;
    private readonly EditorServices _services;
    private readonly AnnotationEditor _editor;
    private readonly int _w, _h;

    private double _zoom = 1;                 // DIP на пиксель снимка
    private Vector _offset;                   // левый верхний угол снимка во вьюпорте, DIP
    private bool _fit = true;                 // масштаб следует за размером окна
    private Point? _panFrom;                  // панорама: точка нажатия во вьюпорте
    private Vector _panOffset;
    private bool _spaceDown;
    private bool _originalMode;
    private bool _closing;
    private bool _noticeDismissed;
    private long _savedRevision;
    private int _beautifyEdits, _savedBeautifyEdits;
    private long _saveRequest, _lastSavedRequest;
    private DispatcherTimer? _toastTimer;

    public EditorWindow(EditSession session, AppSettings settings, EditorServices services)
    {
        InitializeComponent();
        _session = session; _settings = settings; _services = services;
        _w = session.Base.PixelWidth; _h = session.Base.PixelHeight;

        BaseImage.Source = session.Base;
        foreach (var layer in new FrameworkElement[] { BaseImage, Annotations, RulerOverlay, SnapGuides })
        {
            layer.Width = _w;
            layer.Height = _h;
        }
        // Фигуры документа из оверлея могут выходить за регион: на холсте видно ровно то, что попадёт в картинку.
        Annotations.Clip = new RectangleGeometry(new Rect(0, 0, _w, _h));
        Annotations.Document.Load(session.Annotations);

        // Оформление — из записи истории; настройки оверлея окно не трогает.
        var beautify = session.Beautify is { } b
            ? new BeautifyState(true, b, Persist: false)
            : new BeautifyState(false, BeautifyOptions.FromSettings(settings), Persist: false);
        var flyouts = new OptionsFlyouts(Toolbar);
        flyouts.Attach(this);
        _editor = new AnnotationEditor(new AnnotationEditorParts(Toolbar, flyouts, TextEditor, Annotations, RulerOverlay, SnapGuides), session.Base, settings, this, beautify);
        Toolbar.ConfigureForEditor(settings);
        Toolbar.Visibility = Visibility.Visible;
        Toolbar.ActionRequested += a => { if (a == CaptureAction.Save) SaveAs(); else Run(a, null); };
        Toolbar.CancelRequested += Close;
        Document.Changed += UpdateClearButton;

        ShowTitle();
        Localization.Loc.Changed += OnLanguageChanged;
        Closed += (_, _) => Localization.Loc.Changed -= OnLanguageChanged;
        ModeSwitch.Visibility = session.HasOriginal ? Visibility.Visible : Visibility.Collapsed;
        UpdateNotice();
        UpdateClearButton();

        Viewport.MouseLeftButtonDown += OnViewportDown;
        Viewport.MouseMove += OnViewportMove;
        Viewport.MouseLeftButtonUp += OnViewportUp;
        Viewport.MouseRightButtonDown += OnViewportRightDown;
        Viewport.MouseDown += OnViewportMiddleDown;
        Viewport.MouseUp += OnViewportMiddleUp;
        Viewport.MouseWheel += OnViewportWheel;
        Viewport.LostMouseCapture += (_, e) => { if (ReferenceEquals(e.OriginalSource, Viewport)) ResetInteraction(); };
        Viewport.MouseLeave += (_, _) => { if (!_editor.PipetteOn) Magnifier.Visibility = Visibility.Collapsed; };
        Viewport.SizeChanged += (_, _) => Relayout();
        Loaded += (_, _) => { _editor.RefreshOptions(); Relayout(); Focus(); };
    }

    private AnnotationDocument Document => Annotations.Document;
    private bool Dirty => Document.Undo.Revision != _savedRevision || _beautifyEdits != _savedBeautifyEdits;
    private double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Функции включили или выключили в настройках, пока окно открыто: панель и клавиши — по новым.</summary>
    public void ApplyFeatures() => _editor.ApplyFeatures();

    /// <summary>
    /// Тестовый запуск (<c>--dev --edit x.png --scenario имя</c>): <c>original</c> — сразу в режиме «Оригинал»,
    /// <c>pin</c> — после открытия «Закрепить» (новая запись истории без буфера обмена), <c>select</c> — выбрана
    /// первая фигура и есть измерение линейки.
    /// </summary>
    public void ApplyDevScenario(string? scenario)
    {
        if (scenario == "original" && _session.HasOriginal) OriginalMode.IsChecked = true;
        if (scenario == "pin") Dispatcher.BeginInvoke(() => Run(CaptureAction.Pin, null), DispatcherPriority.ApplicationIdle);
        if (scenario == "guides")
        {
            // Масштаб 150 % и перетаскивание «Выбором» посреди жеста: правая рамка почти выровнена по верху левой —
            // видна направляющая в 1 пиксель устройства (жест не завершён, мышь не захвачена: только для скриншота).
            var style = new AnnotationStyle(ToolbarControl.Palette[0], 3);
            Document.Add(new Screeny.Annotations.Shapes.RectAnnotation(new Rect(_w * 0.1, _h * 0.2, _w * 0.25, _h * 0.3), style));
            var moving = new Screeny.Annotations.Shapes.RectAnnotation(new Rect(_w * 0.45, _h * 0.4, _w * 0.2, _h * 0.25), style with { Color = ToolbarControl.Palette[4] });
            Document.Add(moving);
            Dispatcher.BeginInvoke(() =>
            {
                ZoomAt(1.5 / Dpi, ViewTransform.Transform(new Point(_w * 0.4, _h * 0.35)));
                _editor.SetTool(ToolKind.Select);
                var grip = new Point(moving.Rect.Left, moving.Rect.Top + moving.Rect.Height / 2);
                if (_editor.BeginGesture(grip)) _editor.MoveGesture(grip + new Vector(0, _h * 0.2 - moving.Rect.Top + 3));
            }, DispatcherPriority.ApplicationIdle);
        }
        // Flyout'ы параметров: оформление у ✨, «Прямоугольник» у его кнопки, выбранная фигура рядом с ней, рамка толстого карандаша.
        if (scenario is "flyout-beautify")
        {
            _editor.SetBeautify(true);
            Dispatcher.BeginInvoke(() => _editor.Flyouts.DevOpenBeautify(), DispatcherPriority.ApplicationIdle);
        }
        if (scenario is "flyout-beautify-mid")
        {
            _editor.SetBeautify(true);
            Dispatcher.BeginInvoke(() => _editor.Flyouts.DevFreezeBeautifyAt(80), DispatcherPriority.ApplicationIdle);
        }
        if (scenario is "flyout-tool")
        {
            _editor.SetTool(ToolKind.Rect);
            Dispatcher.BeginInvoke(() => _editor.Flyouts.DevOpenTool(), DispatcherPriority.ApplicationIdle);
        }
        if (scenario is "flyout-shape" or "pen-bounds")
        {
            Annotation shape = scenario == "pen-bounds"
                ? DevPen()
                : new Screeny.Annotations.Shapes.RectAnnotation(new Rect(_w * 0.35, _h * 0.3, _w * 0.3, _h * 0.35),
                    new AnnotationStyle(ToolbarControl.Palette[0], 4) { Fill = ColorHex.WithOpacity(ToolbarControl.Palette[4], 40) });
            Document.Add(shape);
            _editor.SetTool(ToolKind.Select);
            Dispatcher.BeginInvoke(() => { Document.Selected = shape; Document.NotifyChanged(); }, DispatcherPriority.ApplicationIdle);
        }
        if (scenario == "select" && Document.Items.Count > 0 && _settings.ToolAllowed(ToolKind.Ruler))
        {
            // Выбрана первая фигура и измерение линейкой поперёк снимка: рамка и подпись при масштабе «вписать».
            Document.Selected = Document.Items[0];
            Document.NotifyChanged();
            _editor.Tool(ToolKind.Ruler).Down(new Point(_w * 0.1, _h * 0.8), Document, _editor.CurrentStyle);
            _editor.Tool(ToolKind.Ruler).Up(new Point(_w * 0.7, _h * 0.6));
        }
    }

    /// <summary>Карандаш толщиной 20 с резкими изгибами через весь снимок.</summary>
    private Screeny.Annotations.Shapes.PenAnnotation DevPen()
    {
        var pen = new Screeny.Annotations.Shapes.PenAnnotation(new Point(_w * 0.1, _h * 0.25), new AnnotationStyle(ToolbarControl.Palette[0], 20));
        foreach (var (fx, fy) in new[] { (0.3, 0.1), (0.2, 0.6), (0.45, 0.3), (0.4, 0.85), (0.6, 0.2), (0.55, 0.7), (0.8, 0.5), (0.75, 0.9) })
            pen.AddPoint(new Point(_w * fx, _h * fy));
        return pen;
    }

    // ---------- Масштаб и панорама ----------

    /// <summary>«Вписать»: целиком в окне, но не крупнее 100 % (мелкий снимок не растягивается). Оформление не учитывается:
    /// превью нет, и включение оформления холст не сдвигает.</summary>
    private double FitZoom()
    {
        double vw = Math.Max(1, Viewport.ActualWidth - 2 * FitMargin), vh = Math.Max(1, Viewport.ActualHeight - 2 * FitMargin);
        return Math.Min(Math.Min(vw / _w, vh / _h), 1 / Dpi);
    }

    private void Relayout()
    {
        if (Viewport.ActualWidth <= 0) return;
        if (_fit) _zoom = FitZoom();
        ClampOffset();
        ApplyView();
    }

    /// <summary>Меньше вьюпорта — по центру; больше — без пустых полей за краем.</summary>
    private void ClampOffset()
    {
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        double sw = _w * _zoom, sh = _h * _zoom;
        double x = sw <= vw ? (vw - sw) / 2 : Math.Clamp(_offset.X, vw - sw, 0);
        double y = sh <= vh ? (vh - sh) / 2 : Math.Clamp(_offset.Y, vh - sh, 0);
        // Сдвиг кратен пикселю устройства: на 100 % снимок не размывается.
        double dpi = Dpi;
        _offset = new Vector(Math.Round(x * dpi) / dpi, Math.Round(y * dpi) / dpi);
    }

    private void ApplyView()
    {
        _editor.CommitTextEdit(); // поле ввода поставлено по прежнему масштабу
        ViewTransform.Matrix = new Matrix(_zoom, 0, 0, _zoom, _offset.X, _offset.Y);
        double device = _zoom * Dpi;
        // Линейка (в DIP) и рамка выбора (в пикселях устройства) — постоянного размера на экране при любом масштабе, как в оверлее.
        RulerOverlay.DpiScale = 1 / _zoom;
        Annotations.OutlineScale = 1 / device;
        SnapGuides.PixelSize = 1 / device; // направляющие — 1 пиксель устройства при любом масштабе
        RenderOptions.SetBitmapScalingMode(BaseImage, device >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        _zoomPercent = Math.Round(device * 100);
        ShowZoomText();
        _editor.Flyouts.Reposition(); // flyout фигуры едет вместе с ней, у кнопок — за панелью
    }

    /// <summary>Масштаб вокруг точки вьюпорта: пиксель снимка под ней остаётся на месте.</summary>
    private void ZoomAt(double zoom, Point anchor)
    {
        double min = Math.Min(FitZoom(), 1 / Dpi), max = MaxDeviceZoom / Dpi;
        zoom = Math.Clamp(zoom, min, max);
        var px = (anchor - (Point)_offset) / _zoom;
        _zoom = zoom;
        _fit = false;
        _offset = (Vector)anchor - px * zoom;
        ClampOffset();
        ApplyView();
    }

    private void SetFit() { _fit = true; Relayout(); }

    private void SetActualSize()
    {
        var center = new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);
        ZoomAt(1 / Dpi, center);
    }

    private void OnZoomBadgeClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_fit) SetActualSize(); else SetFit();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Relayout();
    }

    // ---------- Мышь ----------

    private bool IsFromHud(object source)
    {
        for (var d = source as DependencyObject; d is not null && !ReferenceEquals(d, Viewport);
             d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, TextEditor) || ReferenceEquals(d, Notice) || ReferenceEquals(d, ZoomBadge)) return true;
        return false;
    }

    private Rect ImageRect => new(0, 0, _w, _h);

    private Point ClampToImage(Point p) => new(Math.Clamp(p.X, 0, _w), Math.Clamp(p.Y, 0, _h));

    private PixelPoint ToPixel(Point p) => new(Math.Clamp((int)Math.Floor(p.X), 0, _w - 1), Math.Clamp((int)Math.Floor(p.Y), 0, _h - 1));

    private void OnViewportDown(object sender, MouseButtonEventArgs e)
    {
        if (IsFromHud(e.OriginalSource)) return;
        e.Handled = true;
        _editor.CommitTextEdit();
        Focus();
        if (_spaceDown) { StartPan(e); return; }
        var p = e.GetPosition(PixelCanvas);
        bool inside = ImageRect.Contains(p);
        if (_editor.PipetteOn) { if (inside) _editor.PickColor(ToPixel(p)); return; }
        // Рисовать можно только на снимке; клик мимо снимает выбор фигуры.
        if (!inside) { _editor.Deselect(); return; }
        if (_editor.BeginGesture(ClampToImage(p))) Viewport.CaptureMouse();
    }

    private void OnViewportMove(object sender, MouseEventArgs e)
    {
        if (_panFrom is { } from)
        {
            _offset = _panOffset + (e.GetPosition(Viewport) - from);
            _fit = false;
            ClampOffset();
            ApplyView();
            return;
        }
        var p = e.GetPosition(PixelCanvas);
        if (_editor.HasGesture && e.LeftButton == MouseButtonState.Pressed)
        {
            _editor.MoveGesture(ClampToImage(p));
            return;
        }
        if (IsFromHud(e.OriginalSource)) { Viewport.Cursor = Cursors.Arrow; Magnifier.Visibility = Visibility.Collapsed; return; }
        UpdateHover(p);
    }

    private void OnViewportUp(object sender, MouseButtonEventArgs e)
    {
        if (_panFrom is not null) { EndPan(); return; }
        // Состояние жеста сбрасывается до ReleaseMouseCapture: он синхронно вызывает LostMouseCapture.
        if (_editor.EndGesture(ClampToImage(e.GetPosition(PixelCanvas)))) Viewport.ReleaseMouseCapture();
    }

    private void OnViewportMiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || IsFromHud(e.OriginalSource)) return;
        e.Handled = true;
        StartPan(e);
    }

    private void OnViewportMiddleUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && _panFrom is not null) { e.Handled = true; EndPan(); }
    }

    private void StartPan(MouseButtonEventArgs e)
    {
        if (_editor.HasGesture) return;
        _editor.CommitTextEdit();
        _panFrom = e.GetPosition(Viewport);
        _panOffset = _offset;
        Viewport.Cursor = Cursors.SizeAll;
        Viewport.CaptureMouse();
    }

    private void EndPan()
    {
        _panFrom = null;
        if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
        RefreshHoverNow();
    }

    private void OnViewportRightDown(object sender, MouseButtonEventArgs e)
    {
        if (IsFromHud(e.OriginalSource)) return;
        e.Handled = true;
        StepBack(closeWhenNothingLeft: false);
    }

    private void OnViewportWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsFromHud(e.OriginalSource)) return;
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            ZoomAt(e.Delta > 0 ? _zoom * ZoomStep : _zoom / ZoomStep, e.GetPosition(Viewport));
        else
            _editor.AdjustThickness(Math.Sign(e.Delta));
    }

    private void RefreshHoverNow() => UpdateHover(Mouse.GetPosition(PixelCanvas));

    private void UpdateHover(Point p)
    {
        bool inside = ImageRect.Contains(p);
        if (_spaceDown) { Viewport.Cursor = Cursors.SizeAll; Magnifier.Visibility = Visibility.Collapsed; return; }
        if (_editor.PipetteOn)
        {
            Viewport.Cursor = Cursors.Cross;
            if (inside) UpdateMagnifier(ToPixel(p)); else Magnifier.Visibility = Visibility.Collapsed;
            return;
        }
        if (!inside) { Viewport.Cursor = Cursors.Arrow; return; }
        var tool = _editor.CurrentTool;
        Viewport.Cursor = tool == ToolKind.Select
            ? (Document.HitTest(p) is not null ? Cursors.Hand : Cursors.Arrow)
            : _editor.CursorFor(tool);
    }

    private void UpdateMagnifier(PixelPoint p)
    {
        Magnifier.Visibility = Visibility.Visible;
        Magnifier.Update(_session.Base, p);
        if (Magnifier.ActualWidth <= 0) Magnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Magnifier.ActualWidth > 0 ? new Size(Magnifier.ActualWidth, Magnifier.ActualHeight) : Magnifier.DesiredSize;
        var cursor = ViewTransform.Transform(new Point(p.X + 0.5, p.Y + 0.5));
        const double offset = 20;
        double x = cursor.X + offset, y = cursor.Y + offset;
        if (x + size.Width > Viewport.ActualWidth) x = cursor.X - offset - size.Width;
        if (y + size.Height > Viewport.ActualHeight) y = cursor.Y - offset - size.Height;
        Canvas.SetLeft(Magnifier, x);
        Canvas.SetTop(Magnifier, y);
    }

    // ---------- Клавиатура ----------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Пока вводится текст, клавиши редактора (и пробел-панорама) не работают.
        if (_editor.IsTextEditing) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        e.Handled = true;
        switch (key)
        {
            case Key.LeftAlt or Key.RightAlt:
                // Голый Alt выключает прилипание посреди жеста; системе не отдаём (режим меню окна уводит клавиатуру).
                RefreshSnapGestureSoon();
                break;
            case Key.Space:
                if (!_spaceDown) { _spaceDown = true; RefreshHoverNow(); }
                break;
            case Key.Escape:
                StepBack(closeWhenNothingLeft: true);
                break;
            case Key.C when ctrl:
                Run(CaptureAction.CopyExplicit, null);
                break;
            case Key.S when ctrl:
                SaveAs();
                break;
            case Key.F3 when _settings.ActionAllowed(CaptureAction.Pin):
                Run(CaptureAction.Pin, null);
                break;
            case Key.D0 or Key.NumPad0 when ctrl:
                SetFit();
                break;
            case Key.D1 or Key.NumPad1 when ctrl:
                SetActualSize();
                break;
            case Key.OemPlus or Key.Add when ctrl:
                ZoomAt(_zoom * ZoomStep, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
                break;
            case Key.OemMinus or Key.Subtract when ctrl:
                ZoomAt(_zoom / ZoomStep, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
                break;
            default:
                // Клавиши рисования (инструменты, I, B, Ctrl+Z/Y, Delete, стрелки); в «Оригинале» рисование выключено.
                if (!_editor.HandleKey(key, mods, editing: true)) e.Handled = false;
                break;
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if ((e.Key == Key.System ? e.SystemKey : e.Key) is Key.LeftAlt or Key.RightAlt)
        {
            e.Handled = true;
            RefreshSnapGestureSoon();
            return;
        }
        if (e.Key == Key.Space && _spaceDown)
        {
            _spaceDown = false;
            if (_panFrom is null) RefreshHoverNow();
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        _spaceDown = false; // KeyUp пробела могло уйти в другое окно
    }

    /// <summary>Esc и правый клик снимают по одному слою: жест → пипетка → линейка → выбор/инструмент → (Esc) закрыть окно.</summary>
    private void StepBack(bool closeWhenNothingLeft)
    {
        if (((IAnnotationEditorHost)this).HasGesture) { ResetInteraction(); return; }
        if (_editor.PipetteOn) { _editor.SetPipette(false); return; }
        if (_editor.Ruler.Measurement is not null) { _editor.Ruler.Clear(); return; }
        if (Document.Selected is not null || _editor.CurrentTool != ToolKind.Select)
        {
            _editor.Deselect();
            _editor.SetTool(ToolKind.Select);
            return;
        }
        if (closeWhenNothingLeft) Close();
    }

    /// <summary>После смены состояния Alt (оно обновится к приоритету Input) жест пересчитывается в той же точке.</summary>
    private void RefreshSnapGestureSoon() => Dispatcher.BeginInvoke(() =>
    {
        if (!_closing && _editor.ToolGestureActive) _editor.RefreshGesture(ClampToImage(Mouse.GetPosition(PixelCanvas)));
    }, DispatcherPriority.Input);

    private void ResetInteraction()
    {
        _editor.CancelGesture();
        _panFrom = null;
        if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
    }

    // ---------- Режимы ----------

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (_editor is null) return; // IsChecked из XAML во время InitializeComponent
        SetOriginalMode(OriginalMode.IsChecked == true);
    }

    private void SetOriginalMode(bool on)
    {
        if (_originalMode == on) return;
        ResetInteraction();
        _originalMode = on;
        Annotations.Visibility = on ? Visibility.Hidden : Visibility.Visible; // скрыты, не удалены
        _editor.DrawingEnabled = !on;
        if (on) _noticeDismissed = false;
        UpdateNotice();
        UpdateClearButton();
        RefreshHoverNow();
    }

    private void OnClearAnnotations(object sender, RoutedEventArgs e)
    {
        if (_originalMode) return;
        ResetInteraction();
        _editor.CommitTextEdit();
        Document.RemoveAll();
    }

    /// <summary>«Начать заново с оригинала»: пометки удаляются одним шагом undo, рисование — на чистом снимке.</summary>
    private void OnRestartFromOriginal(object sender, RoutedEventArgs e)
    {
        Document.RemoveAll();
        MarkedMode.IsChecked = true;
    }

    private void UpdateClearButton()
    {
        ClearButton.IsEnabled = !_originalMode && Document.Items.Count > 0;
    }

    private void OnNoticeClose(object sender, RoutedEventArgs e)
    {
        _noticeDismissed = true;
        Notice.Visibility = Visibility.Collapsed;
    }

    private double _zoomPercent = 100;

    private void ShowZoomText() => ZoomText.Text = Localization.Loc.F(_fit ? "Editor.ZoomFit" : "Editor.Zoom", _zoomPercent);

    private void ShowTitle()
        => Title = HistoryStore.TryParseTimestamp(System.IO.Path.GetFileName(_session.Path), out var t)
            ? Localization.Loc.F("Editor.TitleShot", t.ToString("G", System.Globalization.CultureInfo.CurrentCulture))
            : Localization.Loc.F("Editor.TitleFile", System.IO.Path.GetFileName(_session.Path));

    /// <summary>
    /// Язык сменили в настройках, пока окно открыто: заголовок, масштаб и плашка собираются заново (подсказки панели
    /// и оформления обновляют себя сами). Уже показанное сообщение о том, как открыт снимок, остаётся на прежнем языке.
    /// </summary>
    private void OnLanguageChanged()
    {
        ShowTitle();
        ShowZoomText();
        UpdateNotice();
    }

    private void UpdateNotice()
    {
        string? text = null;
        bool restart = false;
        if (_originalMode)
        {
            text = Localization.Loc.T("Editor.NoticeOriginal");
            restart = true;
        }
        else if (_session.Message is { } message)
            text = message;
        else if (!_session.HasOriginal)
            text = Localization.Loc.T("Editor.NoticeNoOriginal");
        RestartButton.Visibility = restart ? Visibility.Visible : Visibility.Collapsed;
        NoticeText.Text = text ?? "";
        Notice.Visibility = text is null || _noticeDismissed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _toastTimer.Tick += (_, _) => { _toastTimer?.Stop(); Toast.Visibility = Visibility.Collapsed; };
        _toastTimer.Start();
    }

    // ---------- Сохранение ----------

    private void SaveAs()
    {
        if (_closing) return;
        _editor.CommitTextEdit();
        ResetInteraction();
        var dialog = new SaveFileDialog
        {
            Title = Localization.Loc.T("Common.SaveScreenshotTitle"),
            Filter = "PNG|*.png|JPEG|*.jpg",
            FileName = $"Screeny_{DateTime.Now:yyyyMMdd_HHmmss}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        var path = FileSaver.ConfirmResolvedPath(this, dialog.FilterIndex, dialog.FileName);
        if (path is not null) Run(CaptureAction.Save, path);
    }

    /// <summary>
    /// Копировать, сохранить или закрепить то, что видно (в режиме «Оригинал» — без пометок), и добавить это новой
    /// записью истории со своими sidecar-файлами. Окно остаётся открытым.
    /// </summary>
    private void Run(CaptureAction action, string? savePath)
    {
        if (!_settings.ActionAllowed(action)) return; // закрепление выключено
        _services.Track(RunAsync(action, savePath));
    }

    private async Task RunAsync(CaptureAction action, string? savePath)
    {
        if (_closing || _services.IsShuttingDown()) return;
        _editor.CommitTextEdit();
        ResetInteraction();
        _editor.Deselect();
        EditResult result;
        try
        {
            IReadOnlyList<Annotation> visible = _originalMode ? Array.Empty<Annotation>() : Document.Items.ToList();
            result = EditResult.Build(_session, visible, _editor.BeautifyOn ? _editor.Beautify : null, _settings.KeepOriginals);
        }
        catch (Exception ex)
        {
            Log.Error("Editor compose failed", ex);
            ShowToast(Localization.Loc.F("Editor.ComposeFailed", ex.Message));
            return;
        }
        bool originalMode = _originalMode;
        long revision = Document.Undo.Revision, request = ++_saveRequest;
        int beautifyEdits = _beautifyEdits;
        int clipboard = action == CaptureAction.CopyExplicit ? _services.ReserveClipboard() : 0;
        if (!_closing) ShowToast(Localization.Loc.T("Editor.Saving"));
        string? historyKey = null; // как дописать к итогу: новая запись или ошибка записи в историю
        try
        {
            var saved = await _services.Save(result.Image, result.AnnotationsJson, result.OriginalPng,
                action == CaptureAction.Save ? savePath : null);
            if (saved.InHistory && !originalMode && request > _lastSavedRequest)
            {
                _lastSavedRequest = request;
                _savedRevision = revision;
                _savedBeautifyEdits = beautifyEdits;
            }
            // Историю выключили, пока окно открыто: запись не пишется, и это не ошибка.
            historyKey = saved.InHistory ? "Editor.ToastNewEntry" : saved.HistoryError is null ? null : "Editor.ToastHistoryFailed";
            if (saved.SaveError is not null) throw saved.SaveError;
            switch (action)
            {
                case CaptureAction.CopyExplicit when _services.IsCurrentClipboard(clipboard):
                    var copied = ClipboardService.CopyImage(result.Image, saved.Png);
                    if (!_closing) ShowToast(WithHistory(Localization.Loc.T(copied ? "Common.Copied" : "Common.ClipboardBusy"), historyKey));
                    break;
                case CaptureAction.Save when savePath is not null:
                    if (!_closing) ShowToast(WithHistory(Localization.Loc.T("Editor.Saved"), historyKey));
                    break;
                case CaptureAction.Pin:
                    _services.Pin(result.Image);
                    if (!_closing) ShowToast(WithHistory(Localization.Loc.T("Editor.Pinned"), historyKey));
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Editor action {action} failed", ex);
            if (!_closing) ShowToast(WithHistory(Localization.Loc.F("Editor.Failed", ex.Message), historyKey));
        }
    }

    private static string WithHistory(string text, string? historyKey) => historyKey is null ? text : Localization.Loc.F(historyKey, text);

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closing) return;
        _editor.CommitTextEdit();
        if (Dirty && !_services.IsShuttingDown())
        {
            if (!Dialogs.Confirm(this, Localization.Loc.T("Editor.CloseTitle"), CloseWithoutSavingText(_settings), Localization.Loc.T("Common.Close"), danger: true))
            {
                e.Cancel = true;
                return;
            }
        }
        _closing = true;
        ResetInteraction();
    }

    /// <summary>
    /// Вопрос при закрытии с несохранёнными пометками — по текущим настройкам: историю могли выключить, пока окно открыто.
    /// </summary>
    public static string CloseWithoutSavingText(AppSettings settings)
    {
        bool pins = settings.ActionAllowed(CaptureAction.Pin);
        if (settings.IsFeatureOn(FeatureCatalog.History))
            return Localization.Loc.T(pins ? "Editor.CloseHistoryPins" : "Editor.CloseHistory");
        return Localization.Loc.T(pins ? "Editor.ClosePins" : "Editor.CloseLost");
    }

    // ---------- Хост редактора ----------

    bool IAnnotationEditorHost.OptionsEnabled => true;
    bool IAnnotationEditorHost.CanUsePipette => !_closing && !_originalMode;
    bool IAnnotationEditorHost.IsClosing => _closing;
    bool IAnnotationEditorHost.HasGesture => _editor.HasGesture || _panFrom is not null;
    Point IAnnotationEditorHost.PixelToHud(Point px) => ViewTransform.Transform(px);
    double IAnnotationEditorHost.PixelsToHud(double length) => length * _zoom;
    void IAnnotationEditorHost.FocusSurface() => Focus();
    void IAnnotationEditorHost.ResetInteraction() => ResetInteraction();
    void IAnnotationEditorHost.RefreshHover() { if (IsLoaded) RefreshHoverNow(); }
    void IAnnotationEditorHost.OnPipetteChanged(bool on) { if (!on) Magnifier.Visibility = Visibility.Collapsed; }

    Rect IAnnotationEditorHost.SnapArea => ImageRect;

    void IAnnotationEditorHost.OnBeautifyChanged() => _beautifyEdits++; // оформление меняет итоговую картинку, холст не трогает

    // ---------- Хост flyout'ов ----------

    Canvas IFlyoutHost.FlyoutLayer => FlyoutLayer;
    Rect IFlyoutHost.FlyoutWorkArea => new(0, 0, FlyoutLayer.ActualWidth, FlyoutLayer.ActualHeight);
    bool IFlyoutHost.ToolbarFlyoutsBelow => false; // панель внизу окна: flyout'ы её кнопок открываются вверх

    /// <summary>Рамка фигуры на экране (с масштабом и сдвигом), обрезанная по вьюпорту; за кадром — null.</summary>
    Rect? IFlyoutHost.ShapeRect(Annotation shape)
    {
        if (!Viewport.IsVisible) return null;
        var view = ViewTransform.TransformBounds(shape.Bounds);
        view.Intersect(new Rect(0, 0, Viewport.ActualWidth, Viewport.ActualHeight));
        if (view.IsEmpty) return null;
        return Viewport.TransformToVisual(FlyoutLayer).TransformBounds(view);
    }
}
