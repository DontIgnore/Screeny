using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Export;
using Screeny.Features;
using Screeny.Overlay;
using Screeny.Overlay.Tools;
using Screeny.Settings;

namespace Screeny.Editing;

/// <summary>
/// Что хост (оверлей или окно повторного редактирования) даёт редактору аннотаций: состояние своих режимов,
/// пересчёт координат для поля ввода текста и реакции HUD на изменения редактора.
/// </summary>
public interface IAnnotationEditorHost
{
    /// <summary>Доступны ли параметры вообще (оверлей: есть выделение, режим рисования, не режим текста).</summary>
    bool OptionsEnabled { get; }
    /// <summary>Можно ли сейчас включить пипетку.</summary>
    bool CanUsePipette { get; }
    /// <summary>Хост закрывается: курсор и подсказки уже не обновляются.</summary>
    bool IsClosing { get; }
    /// <summary>Идёт ли любой жест мыши хоста (включая жест инструмента редактора).</summary>
    bool HasGesture { get; }
    /// <summary>Точка снимка (пиксели) → координаты HUD-холста, где лежит поле ввода текста.</summary>
    Point PixelToHud(Point px);
    /// <summary>Длина в пикселях снимка → длина в HUD (размер шрифта поля ввода).</summary>
    double PixelsToHud(double length);
    /// <summary>Вернуть клавиатуру поверхности после ввода текста.</summary>
    void FocusSurface();
    /// <summary>Сбросить текущий жест мыши хоста целиком (пипетка включается посреди жеста).</summary>
    void ResetInteraction();
    /// <summary>Пересчитать курсор и подсветку под мышью.</summary>
    void RefreshHover();
    /// <summary>Пипетка включена или выключена (оверлей прячет лупу).</summary>
    void OnPipetteChanged(bool on);
    /// <summary>Оформление включено/выключено или сменились его параметры (превью нет: оформление меняет только результат).</summary>
    void OnBeautifyChanged();
    /// <summary>Область снимка в пикселях: её края и центр — цели прилипания фигур (оверлей — выделение, редактор — холст).</summary>
    Rect SnapArea => Rect.Empty;
}

/// <summary>Элементы HUD, которыми управляет редактор: панель, всплывающие панели параметров, поле ввода текста, слои снимка.</summary>
/// <param name="Flyouts">Flyout'ы параметров (у кнопок панели и рядом с фигурой); показываются после <see cref="OptionsFlyouts.Attach"/>.</param>
/// <param name="Guides">Слой направляющих прилипания (необязателен: без него прилипание работает, но линий не видно).</param>
public sealed record AnnotationEditorParts(ToolbarControl Toolbar, OptionsFlyouts Flyouts, TextBox TextEditor, AnnotationLayer Layer, RulerLayer Ruler,
    SnapGuideLayer? Guides = null);

/// <param name="Enabled">Включено ли оформление при открытии.</param>
/// <param name="Options">Параметры оформления при открытии.</param>
/// <param name="Persist">
/// Записывать ли включение и параметры в настройки. Оверлей запоминает их для следующих снимков; окно повторного
/// редактирования берёт оформление из записи истории и настройки пользователя не трогает.
/// </param>
public sealed record BeautifyState(bool Enabled, BeautifyOptions Options, bool Persist);

/// <summary>
/// Редактор аннотаций без своего окна: диспетчер инструментов, жест текущего инструмента, поле ввода текста,
/// выбор, параметры (flyout'ы) и палитра, пипетка, линейка, undo/redo, оформление и клавиши рисования.
/// Его используют оверлей (поверх замороженного экрана) и окно повторного редактирования из истории.
/// Все точки — в пикселях снимка; хост сам ограничивает их областью редактирования.
/// </summary>
public sealed class AnnotationEditor
{
    /// <summary>Порог прилипания фигур в экранных пикселях (DIP); в пиксели снимка пересчитывается через масштаб хоста.</summary>
    public const double SnapThresholdDip = 6;

    private readonly AnnotationEditorParts _parts;
    private readonly IAnnotationEditorHost _host;
    private readonly AppSettings _settings;
    private readonly Dictionary<ToolKind, ITool> _tools;
    private readonly TextTool _textTool;
    private readonly Snapper _snapper;
    private readonly RulerTool _ruler;
    private readonly bool _persistBeautify;
    private bool _pipette;                    // режим пипетки: следующий клик по снимку берёт цвет
    private ITool? _activeTool;               // инструмент, который сейчас ведёт мышь (между Down и Up)
    private (Point At, AnnotationStyle Style)? _textEdit;
    private Brush? _editorBackground;         // фон поля ввода из XAML, когда у текста нет подложки
    private double? _editorMinWidth;          // минимальная ширина поля из XAML (для выравнивания влево)
    private string? _stripKey;                // что сейчас показано в параметрах: без изменений Show не вызывается
    private bool _gestureStarting;            // идёт Down инструмента: выбранную фигуру уже тащат, её flyout не показываем
    private bool _beautifyOn;
    private BeautifyOptions _beautify;
    private bool _drawingEnabled = true;

    public AnnotationEditor(AnnotationEditorParts parts, BitmapSource source, AppSettings settings, IAnnotationEditorHost host, BeautifyState beautify)
    {
        _parts = parts; _host = host; _settings = settings;
        Source = source;
        parts.Layer.Source = source;

        Func<bool> shift = () => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        // Прилипание: настройка включена и Alt не зажат; порог — 6 экранных пикселей при текущем масштабе хоста.
        _snapper = new Snapper(
            enabled: () => _settings.SnapEnabled && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt),
            threshold: () => SnapThresholdDip / Math.Max(1e-6, _host.PixelsToHud(1)),
            area: () => _host.SnapArea);
        _snapper.GuidesChanged += () => { if (parts.Guides is { } layer) layer.Guides = _snapper.Guides; };
        _textTool = new TextTool(_snapper);
        _ruler = new RulerTool(shift);
        _ruler.Changed += () => parts.Ruler.Measurement = _ruler.Measurement;
        _tools = new Dictionary<ToolKind, ITool>
        {
            [ToolKind.Select] = new SelectTool(_snapper),
            [ToolKind.Rect] = new ShapeTool(ToolKind.Rect, shift, _snapper),
            [ToolKind.Ellipse] = new ShapeTool(ToolKind.Ellipse, shift, _snapper),
            [ToolKind.Line] = new ShapeTool(ToolKind.Line, shift, _snapper),
            [ToolKind.Arrow] = new ShapeTool(ToolKind.Arrow, shift, _snapper),
            [ToolKind.Pen] = new PenTool(highlighter: false),
            [ToolKind.Highlighter] = new PenTool(highlighter: true),
            [ToolKind.Text] = _textTool,
            [ToolKind.Number] = new NumberTool(_snapper),
            [ToolKind.Pixelate] = new ShapeTool(ToolKind.Pixelate, shift, _snapper),
            [ToolKind.Blur] = new ShapeTool(ToolKind.Blur, shift, _snapper),
            [ToolKind.Ruler] = _ruler,
        };
        _textTool.EditRequested += BeginTextEdit;

        var toolbar = parts.Toolbar;
        toolbar.ApplyFeatures(settings); // кнопки выключенных функций скрыты; их клавиши отсекают HandleKey и Set*
        // Начальный инструмент — выбор, чтобы случайный клик ничего не рисовал.
        toolbar.SetActiveTool(ToolKind.Select);
        var color = ParseColor(settings.LastColor);
        toolbar.SetColor(color);
        toolbar.SetThickness(settings.LastThickness);
        Options = StyleOptions.FromSettings(settings, color);
        toolbar.SetUndoState(false, false);
        toolbar.UndoRequested += Undo;
        toolbar.RedoRequested += Redo;
        toolbar.ToolChanged += _ => OnToolSwitched();
        toolbar.ColorPicked += OnColorPicked;
        toolbar.PipetteRequested += TogglePipette;
        toolbar.ThicknessChanged += t => _settings.LastThickness = t;
        Document.Changed += () =>
        {
            toolbar.SetUndoState(Document.Undo.CanUndo, Document.Undo.CanRedo);
            RefreshOptions(); // выбор фигуры и undo/redo меняют параметры и место их flyout'а
        };
        var strip = parts.Flyouts.Options;
        strip.TargetClicked += OnTargetClicked;
        strip.StrokeNoneClicked += () => EditStyle(Options.ToggleStroke);
        strip.FillNoneClicked += () => EditStyle(Options.ToggleFill);
        strip.FillOpacityClicked += () => EditStyle(Options.NextFillOpacity);
        strip.BackgroundNoneClicked += () => EditStyle(Options.ToggleTextBackground);
        strip.ArrowStyleClicked += a => EditStyle(s => StyleOptions.WithArrow(s, a));
        strip.TextAlignClicked += a => EditStyle(s => StyleOptions.WithTextAlign(s, a));

        _persistBeautify = beautify.Persist;
        // Без функции «Оформление» снимок не оформляется, даже если оно включено в настройках (их значение не трогаем).
        _beautifyOn = beautify.Enabled && BeautifyAllowed;
        _beautify = beautify.Options;
        toolbar.SetBeautify(_beautifyOn);
        parts.Flyouts.Beautify.Show(_beautify);
        toolbar.BeautifyRequested += ToggleBeautify;
        parts.Flyouts.Beautify.OptionsChanged += SetBeautifyOptions;

        parts.TextEditor.PreviewKeyDown += OnTextEditorKeyDown;
        parts.TextEditor.LostKeyboardFocus += (_, _) => CommitTextEdit();
    }

    /// <summary>Источник пикселей для эффектов (пикселизация, размытие) и пипетки.</summary>
    public BitmapSource Source { get; private set; }

    /// <summary>
    /// Другой кадр под теми же пометками (серия снимков в оверлее): пипетка берёт цвет с него, а пикселизация и размытие
    /// пересчитываются — их кэш привязан к источнику.
    /// </summary>
    public void SetSource(BitmapSource source)
    {
        Source = source;
        _parts.Layer.Source = source;
        _parts.Layer.InvalidateVisual();
    }
    public AnnotationDocument Document => _parts.Layer.Document;
    /// <summary>Обводка/заливка, стиль стрелки, фон текста, активный образец.</summary>
    public StyleOptions Options { get; }
    public ToolKind CurrentTool => _parts.Toolbar.CurrentTool;
    public AnnotationStyle CurrentStyle => StyleForTool(CurrentTool);
    public AnnotationStyle StyleForTool(ToolKind tool) => Options.StyleFor(tool, _parts.Toolbar.CurrentColor, _parts.Toolbar.CurrentThickness);
    public ITool Tool(ToolKind kind) => _tools[kind];
    public Cursor CursorFor(ToolKind kind) => _tools[kind].Cursor;
    public RulerTool Ruler => _ruler;
    /// <summary>Прилипание фигур: текущие направляющие и цели жеста.</summary>
    public Snapper Snapper => _snapper;
    public bool PipetteOn => _pipette;
    public bool IsTextEditing => _textEdit is not null;
    /// <summary>Инструмент ведёт мышь (между Down и Up).</summary>
    public bool HasGesture => _activeTool is not null;
    /// <summary>Жест инструмента реально идёт: undo/redo и перекраска запрещены.</summary>
    public bool ToolGestureActive => _activeTool?.IsActive == true;
    public bool BeautifyOn => _beautifyOn;
    private bool BeautifyAllowed => _settings.IsFeatureOn(FeatureCatalog.Beautify);
    /// <summary>Flyout'ы параметров: оформление у кнопки ✨, инструмент у его кнопки, фигура рядом с ней.</summary>
    public OptionsFlyouts Flyouts => _parts.Flyouts;
    public BeautifyOptions Beautify => _beautify;

    /// <summary>
    /// Можно ли рисовать и править (окно редактора в режиме «Оригинал» выключает это): инструменты, палитра,
    /// толщина, undo и клавиши рисования. Выключение сбрасывает инструмент на «Выбор» и снимает выбор фигуры.
    /// </summary>
    public bool DrawingEnabled
    {
        get => _drawingEnabled;
        set
        {
            if (_drawingEnabled == value) return;
            if (!value)
            {
                CommitTextEdit();
                CancelGesture();
                SetPipette(false);
                if (Document.Selected is not null) { Document.Selected = null; Document.NotifyChanged(); }
                SetTool(ToolKind.Select);
            }
            _drawingEnabled = value;
            _parts.Toolbar.SetDrawingEnabled(value);
            _parts.Toolbar.SetUndoState(Document.Undo.CanUndo, Document.Undo.CanRedo);
            _stripKey = null;
            RefreshOptions();
        }
    }

    // ---------- Жест инструмента ----------

    /// <summary>Нажатие текущим инструментом. true — начался жест (хосту нужно захватить мышь).</summary>
    public bool BeginGesture(Point px)
    {
        if (!_drawingEnabled) return false;
        var tool = _tools[CurrentTool];
        _gestureStarting = true;
        try { tool.Down(px, Document, CurrentStyle); }
        finally { _gestureStarting = false; }
        if (!tool.IsActive) { RefreshOptions(); return false; }
        _activeTool = tool;
        return true;
    }

    public void MoveGesture(Point px) => _activeTool?.Move(px);

    /// <summary>
    /// Alt нажат или отпущен посреди жеста: тот же жест в той же точке пересчитывается, и прилипание
    /// с направляющими включается или выключается сразу, без движения мыши.
    /// </summary>
    public void RefreshGesture(Point px) { if (ToolGestureActive) _activeTool?.Move(px); }

    /// <summary>Отпускание. true — жест был (хосту отпустить захват мыши).</summary>
    public bool EndGesture(Point px)
    {
        if (_activeTool is not { } tool) return false;
        _activeTool = null;
        tool.Up(px);
        RefreshOptions(); // фигуру отпустили: её flyout появляется на новом месте
        return true;
    }

    /// <summary>Прервать жест без следа: живая фигура отбрасывается, сдвиг откатывается.</summary>
    public void CancelGesture()
    {
        var tool = _activeTool;
        _activeTool = null;
        tool?.Cancel();
        if (tool is not null) RefreshOptions();
    }

    /// <summary>Пока инструмент ведёт жест, undo/redo запрещены: иначе отменится чужой шаг под живой фигурой.</summary>
    public void Undo() { if (!ToolGestureActive && _drawingEnabled) Document.Undo.Undo(); }
    public void Redo() { if (!ToolGestureActive && _drawingEnabled) Document.Undo.Redo(); }

    /// <summary>Колесо мыши: толщина ±1, запоминается.</summary>
    public void AdjustThickness(int delta)
    {
        if (!_drawingEnabled) return;
        _parts.Toolbar.SetThickness(_parts.Toolbar.CurrentThickness + delta);
        _settings.LastThickness = _parts.Toolbar.CurrentThickness;
    }

    public void Deselect()
    {
        if (Document.Selected is null) return;
        Document.Selected = null;
        Document.NotifyChanged();
    }

    // ---------- Клавиши ----------

    /// <summary>
    /// Клавиши рисования: Ctrl+Z/Ctrl+Y, Delete/Backspace и стрелки для выбранной фигуры, а в режиме рисования
    /// (<paramref name="editing"/>) ещё I (пипетка), B (оформление) и клавиши инструментов. false — не наша клавиша.
    /// Клавиши выключенных функций (пипетка, оформление, линейка) — тоже не наши.
    /// </summary>
    public bool HandleKey(Key key, ModifierKeys mods, bool editing)
    {
        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        switch (key)
        {
            case Key.I when editing && !ctrl && _settings.IsFeatureOn(FeatureCatalog.Pipette):
                TogglePipette();
                return true;
            case Key.B when editing && !ctrl && BeautifyAllowed:
                ToggleBeautify();
                return true;
            case Key.Z when ctrl:
                Undo();
                return true;
            case Key.Y when ctrl:
                Redo();
                return true;
            case Key.Delete or Key.Back when Document.Selected is { } selected:
                if (!ToolGestureActive && _drawingEnabled) Document.Remove(selected);
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down when Document.Selected is { } moving:
                if (_host.HasGesture || !_drawingEnabled) return true;
                int step = ctrl ? 10 : 1;
                int dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
                int dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
                Document.Move(moving, new Vector(dx, dy));
                return true;
            default:
                if (editing && !ctrl && _drawingEnabled && ToolForKey(key) is { } tool && _settings.ToolAllowed(tool)) { SetTool(tool); return true; }
                return false;
        }
    }

    public static ToolKind? ToolForKey(Key key)
    {
        if (key is >= Key.NumPad0 and <= Key.NumPad9) key = Key.D0 + (key - Key.NumPad0);
        if (key == Key.Subtract) key = Key.OemMinus;
        if (key == Key.Add) key = Key.OemPlus;
        foreach (var t in ToolbarControl.Tools) if (t.Key == key) return t.Kind;
        return null;
    }

    // ---------- Текст ----------

    private void BeginTextEdit(Point px, AnnotationDocument doc, AnnotationStyle style)
    {
        var editor = _parts.TextEditor;
        _textEdit = (px, style);
        var hud = _host.PixelToHud(px);
        editor.Text = "";
        editor.FontSize = _host.PixelsToHud(TextAnnotation.FontSizeFor(style.Thickness));
        ShowTextEditorStyle(style);
        // Компенсируем рамку и внутренний отступ TextBox, чтобы текст встал туда же, где будет нарисован.
        Canvas.SetLeft(editor, hud.X - 3);
        Canvas.SetTop(editor, hud.Y - 1);
        editor.Visibility = Visibility.Visible;
        // Пока вводится текст, параметры и палитра правят его, а не выбранную ранее фигуру.
        Deselect();
        editor.Focus();
        Keyboard.Focus(editor);
    }

    /// <summary>Поле ввода показывает цвет текста и подложку будущей аннотации.</summary>
    private void ShowTextEditorStyle(AnnotationStyle style)
    {
        var editor = _parts.TextEditor;
        _editorBackground ??= editor.Background;
        editor.Foreground = new SolidColorBrush(style.Color);
        editor.Background = style.TextBackground is { } bg ? new SolidColorBrush(bg) : _editorBackground;
        // Поле растёт по самой длинной строке, как блок аннотации, и выравнивает строки так же. Минимальная ширина
        // из XAML — только для выравнивания влево: иначе короткая строка встала бы по центру (вправо) пустого поля
        // и после Enter прыгнула бы к левому краю блока.
        _editorMinWidth ??= editor.MinWidth;
        editor.MinWidth = style.TextAlign == TextAlign.Left ? _editorMinWidth.Value : 0;
        editor.TextAlignment = style.TextAlign switch
        {
            TextAlign.Center => TextAlignment.Center,
            TextAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
    }

    public void CommitTextEdit()
    {
        if (_textEdit is not { } edit) return;
        _textEdit = null;
        var text = _parts.TextEditor.Text.TrimEnd();
        _parts.TextEditor.Visibility = Visibility.Collapsed;
        if (text.Length > 0) Document.Add(new TextAnnotation(edit.At, text, edit.Style));
        _host.FocusSurface();
    }

    public void CancelTextEdit()
    {
        if (_textEdit is null) return;
        _textEdit = null;
        _parts.TextEditor.Visibility = Visibility.Collapsed;
        _host.FocusSurface();
    }

    private void OnTextEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; CancelTextEdit(); }
        else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; CommitTextEdit(); }
    }

    // ---------- Параметры и палитра ----------

    /// <summary>Фигура, к которой применяются параметры и палитра: выбранная и ещё лежащая в документе.</summary>
    private Annotation? OptionsSubject => Document.Selected is { } s && Document.Items.Contains(s) ? s : null;

    private IReadOnlyList<OptionsSectionKind> CurrentSections()
    {
        if (!_host.OptionsEnabled) return [];
        var sections = OptionsSections.For(new OptionsContext(CurrentTool, OptionsSubject) { Beautify = _beautifyOn });
        // Без рисования остаётся только оформление: оно меняет итог, а не пометки.
        return _drawingEnabled ? sections : sections.Where(s => s == OptionsSectionKind.Beautify).ToList();
    }

    /// <summary>Стиль, который правят параметры и палитра: выбранной фигуры, вводимого текста или будущей фигуры инструмента.</summary>
    private AnnotationStyle EffectiveStyle()
    {
        if (OptionsSubject is { } s) return s.Style;
        if (_textEdit is { } edit) return edit.Style;
        return CurrentStyle;
    }

    /// <summary>
    /// Показывает в параметрах секцию инструмента или фигуры и стиль, подсвечивает на палитре цвет активного образца
    /// и сообщает flyout'ам новое состояние (открыть у фигуры, закрыть, переставить).
    /// </summary>
    public void RefreshOptions()
    {
        var sections = CurrentSections();
        OptionsSectionKind? main = sections.Where(k => k != OptionsSectionKind.Beautify).Select(k => (OptionsSectionKind?)k).FirstOrDefault();
        var style = EffectiveStyle();
        var target = OptionsSections.EffectiveTarget(sections, Options.ActiveTarget);
        int opacity = style.Fill is { } f ? ColorHex.OpacityPercent(f) : Options.FillOpacity;
        var key = $"{main}|{style}|{target}|{opacity}";
        if (key != _stripKey)
        {
            _stripKey = key;
            _parts.Flyouts.Options.Show(main, style, target, opacity);
            _parts.Toolbar.MarkSwatch(StyleOptions.SwatchColor(style, target));
        }
        // Параметры фигуры рядом с ней — только при явном выборе инструментом «Выбор»; пока её тащат, они скрыты.
        var shape = CurrentTool == ToolKind.Select ? OptionsSubject : null;
        _parts.Flyouts.Update(new FlyoutState(_host.OptionsEnabled && !_host.IsClosing, CurrentTool, main, shape,
            ShapeHidden: ToolGestureActive || _gestureStarting, BeautifyOn: _beautifyOn));
    }

    /// <summary>
    /// Правка из параметров или палитры: новый стиль применяется к выбранной фигуре одним шагом undo
    /// (или к вводимому тексту) и запоминается как параметры будущих фигур.
    /// </summary>
    private void EditStyle(Func<AnnotationStyle, AnnotationStyle> op)
    {
        if (ToolGestureActive || !_drawingEnabled) return; // не перекрашиваем фигуру под живым жестом
        var sections = CurrentSections();
        var subject = OptionsSubject;
        var before = EffectiveStyle();
        var after = op(before);
        if (after != before)
        {
            if (subject is not null) Document.Restyle(subject, after);
            else if (_textEdit is { } edit) { _textEdit = (edit.At, after); ShowTextEditorStyle(after); }
            foreach (var section in sections) Options.Absorb(after, section);
            if (after.Color != before.Color)
            {
                _parts.Toolbar.SetColor(after.Color);
                _settings.LastColor = ColorHex.Rgb(after.Color);
            }
            Options.SaveTo(_settings);
        }
        _stripKey = null;
        RefreshOptions();
    }

    /// <summary>Клик по палитре (и цвет из пипетки) красит активный образец или основной цвет.</summary>
    private void OnColorPicked(Color c)
    {
        var target = OptionsSections.EffectiveTarget(CurrentSections(), Options.ActiveTarget);
        EditStyle(s => Options.Paint(s, target, c));
    }

    /// <summary>Клик по образцу делает его активным; выключенная часть (заливка, фон, обводка) при этом включается.</summary>
    private void OnTargetClicked(PaintTarget target)
    {
        Options.ActiveTarget = target;
        var style = EffectiveStyle();
        bool shape = CurrentSections().Contains(OptionsSectionKind.Shape);
        if (target == PaintTarget.Fill && style.Fill is null) EditStyle(Options.ToggleFill);
        else if (target == PaintTarget.Background && style.TextBackground is null) EditStyle(Options.ToggleTextBackground);
        else if (target == PaintTarget.Color && shape && !style.StrokeEnabled) EditStyle(Options.ToggleStroke);
        else { _stripKey = null; RefreshOptions(); }
    }

    /// <summary>Сбросить кэш параметров: следующий RefreshOptions покажет их заново.</summary>
    public void InvalidateOptions() => _stripKey = null;

    /// <summary>Единая смена инструмента: кнопка, клавиша, Esc. Пипетка выключается, измерение линейки убирается.
    /// Инструмент выключенной функции (линейка) не выбирается.</summary>
    public void SetTool(ToolKind kind)
    {
        if (!_settings.ToolAllowed(kind)) return;
        _parts.Toolbar.SetActiveTool(kind);
        OnToolSwitched();
    }

    private void OnToolSwitched()
    {
        CommitTextEdit();
        SetPipette(false);
        if (CurrentTool != ToolKind.Ruler) _ruler.Clear();
        RefreshOptions();
        _parts.Flyouts.OnToolPicked(CurrentTool);
        _host.RefreshHover();
    }

    /// <summary>
    /// Функции включили или выключили, пока хост открыт (окно редактора): кнопки панели по новым настройкам,
    /// выключенные пипетка, линейка и оформление снимаются. Сохранённое BeautifyEnabled при этом не меняется.
    /// </summary>
    public void ApplyFeatures()
    {
        _parts.Toolbar.ApplyFeatures(_settings);
        if (_pipette && !_settings.IsFeatureOn(FeatureCatalog.Pipette)) SetPipette(false);
        if (!_settings.ToolAllowed(CurrentTool)) SetTool(ToolKind.Select);
        if (_beautifyOn && !BeautifyAllowed) ApplyBeautify(false, persist: false);
    }

    // ---------- Пипетка ----------

    public void TogglePipette() => SetPipette(!_pipette);

    /// <summary>Режим пипетки (кнопка или I): курсор-перекрестие с лупой, следующий клик по снимку берёт цвет. Одноразовый.</summary>
    public void SetPipette(bool on)
    {
        if (on && (!_host.CanUsePipette || !_drawingEnabled || !_settings.IsFeatureOn(FeatureCatalog.Pipette))) on = false;
        if (_pipette == on) { _parts.Toolbar.SetPipette(on); return; }
        if (on) { CommitTextEdit(); _host.ResetInteraction(); }
        _pipette = on;
        _parts.Toolbar.SetPipette(on);
        _host.OnPipetteChanged(on);
        if (!_host.IsClosing) _host.RefreshHover();
    }

    /// <summary>Клик пипеткой: цвет пикселя снимка — в активный образец, режим выключается.</summary>
    public void PickColor(PixelPoint p)
    {
        var c = Pipette.ColorAt(Source, p);
        SetPipette(false);
        OnColorPicked(c);
    }

    // ---------- Оформление ----------

    public void ToggleBeautify() => SetBeautify(!_beautifyOn);

    public void SetBeautify(bool on)
    {
        if (on && !BeautifyAllowed) return; // и настройку BeautifyEnabled не трогаем
        ApplyBeautify(on, _persistBeautify);
    }

    private void ApplyBeautify(bool on, bool persist)
    {
        if (_host.IsClosing) return;
        _beautifyOn = on;
        if (persist) _settings.BeautifyEnabled = on;
        _parts.Toolbar.SetBeautify(on);
        RefreshOptions();
        _parts.Flyouts.OnBeautifyToggled(on); // включили — параметры оформления сразу у кнопки ✨
        _host.OnBeautifyChanged();
    }

    public void SetBeautifyOptions(BeautifyOptions options)
    {
        _beautify = options;
        if (_persistBeautify) options.SaveTo(_settings);
        _parts.Flyouts.Beautify.Show(options);
        _host.OnBeautifyChanged();
    }

    public static Color ParseColor(string? text)
    {
        try { if (text is not null && ColorConverter.ConvertFromString(text) is Color c) return c; }
        catch (Exception) { /* битая настройка — цвет по умолчанию */ }
        return ToolbarControl.Palette[0];
    }
}
