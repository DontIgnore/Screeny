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
/// What host (overlay or re-edit window) provides to annotation editor: state of its modes,
/// coordinate conversion for text input field and HUD reactions to editor changes.
/// </summary>
public interface IAnnotationEditorHost
{
    /// <summary>Whether options are available at all (overlay: has selection, drawing mode, not text mode).</summary>
    bool OptionsEnabled { get; }
    /// <summary>Whether eyedropper can be enabled now.</summary>
    bool CanUsePipette { get; }
    /// <summary>Host closes: cursor and tooltips no longer updated.</summary>
    bool IsClosing { get; }
    /// <summary>Whether any host mouse gesture is running (including editor tool gesture).</summary>
    bool HasGesture { get; }
    /// <summary>Screenshot point (pixels) → HUD canvas coordinates where text input field lies.</summary>
    Point PixelToHud(Point px);
    /// <summary>Length in screenshot pixels → length in HUD (text field font size).</summary>
    double PixelsToHud(double length);
    /// <summary>Return keyboard to surface after text input.</summary>
    void FocusSurface();
    /// <summary>Reset host's current mouse gesture entirely (eyedropper enabled mid-gesture).</summary>
    void ResetInteraction();
    /// <summary>Recalculate cursor and highlight under mouse.</summary>
    void RefreshHover();
    /// <summary>Eyedropper enabled or disabled (overlay hides magnifier).</summary>
    void OnPipetteChanged(bool on);
    /// <summary>Decoration enabled/disabled or its parameters changed (no preview: decoration affects only result).</summary>
    void OnBeautifyChanged();
    /// <summary>Screenshot region in pixels: its edges and center are snap targets for shapes (overlay — selection, editor — canvas).</summary>
    Rect SnapArea => Rect.Empty;
}

/// <summary>HUD elements controlled by editor: panel, options flyouts, text input field, screenshot layers.</summary>
/// <param name="Flyouts">Options flyouts (near panel buttons and next to shape); shown after <see cref="OptionsFlyouts.Attach"/>.</param>
/// <param name="Guides">Snap guide layer (optional: without it snapping works but lines invisible).</param>
public sealed record AnnotationEditorParts(ToolbarControl Toolbar, OptionsFlyouts Flyouts, TextBox TextEditor, AnnotationLayer Layer, RulerLayer Ruler,
    SnapGuideLayer? Guides = null);

/// <param name="Enabled">Whether decoration is enabled on open.</param>
/// <param name="Options">Decoration parameters on open.</param>
/// <param name="Persist">
/// Whether to write enabling and parameters to settings. Overlay remembers them for next screenshots; re-edit window
/// takes decoration from history entry and does not touch user settings.
/// </param>
public sealed record BeautifyState(bool Enabled, BeautifyOptions Options, bool Persist);

/// <summary>
/// Annotation editor without own window: tool dispatcher, current tool gesture, text input field,
/// selection, options (flyouts) and palette, eyedropper, ruler, undo/redo, decoration and drawing keys.
/// Used by overlay (over frozen screen) and re-edit window from history.
/// All points are in screenshot pixels; host itself limits them to editing region.
/// </summary>
public sealed class AnnotationEditor
{
    /// <summary>Shape snap threshold in screen pixels (DIP); converted to screenshot pixels via host scale.</summary>
    public const double SnapThresholdDip = 6;

    private readonly AnnotationEditorParts _parts;
    private readonly IAnnotationEditorHost _host;
    private readonly AppSettings _settings;
    private readonly Dictionary<ToolKind, ITool> _tools;
    private readonly TextTool _textTool;
    private readonly Snapper _snapper;
    private readonly RulerTool _ruler;
    private readonly bool _persistBeautify;
    private bool _pipette;                    // eyedropper mode: next click on screenshot picks color
    private ITool? _activeTool;               // tool currently driving mouse (between Down and Up)
    private (Point At, AnnotationStyle Style)? _textEdit;
    private Brush? _editorBackground;         // text field background from XAML when text has no background
    private double? _editorMinWidth;          // minimum field width from XAML (for left alignment)
    private string? _stripKey;                // what's shown in options now: without changes Show not called
    private bool _gestureStarting;            // tool Down ongoing: selected shape already being dragged, its flyout not shown
    private bool _beautifyOn;
    private BeautifyOptions _beautify;
    private bool _drawingEnabled = true;

    public AnnotationEditor(AnnotationEditorParts parts, BitmapSource source, AppSettings settings, IAnnotationEditorHost host, BeautifyState beautify)
    {
        _parts = parts; _host = host; _settings = settings;
        Source = source;
        parts.Layer.Source = source;

        Func<bool> shift = () => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        // Snapping: setting enabled and Alt not held; threshold — 6 screen pixels at current host scale.
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
        toolbar.ApplyFeatures(settings); // disabled feature buttons hidden; their keys cut off by HandleKey and Set*
        // Initial tool is Select, so accidental click draws nothing.
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
            RefreshOptions(); // shape selection and undo/redo change options and their flyout location
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
        // Without Decoration feature screenshot not decorated even if enabled in settings (its value untouched).
        _beautifyOn = beautify.Enabled && BeautifyAllowed;
        _beautify = beautify.Options;
        toolbar.SetBeautify(_beautifyOn);
        parts.Flyouts.Beautify.Show(_beautify);
        toolbar.BeautifyRequested += ToggleBeautify;
        parts.Flyouts.Beautify.OptionsChanged += SetBeautifyOptions;

        parts.TextEditor.PreviewKeyDown += OnTextEditorKeyDown;
        parts.TextEditor.LostKeyboardFocus += (_, _) => CommitTextEdit();
    }

    /// <summary>Pixel source for effects (pixelate, blur) and eyedropper.</summary>
    public BitmapSource Source { get; }
    public AnnotationDocument Document => _parts.Layer.Document;
    /// <summary>Stroke/fill, arrow style, text background, active swatch.</summary>
    public StyleOptions Options { get; }
    public ToolKind CurrentTool => _parts.Toolbar.CurrentTool;
    public AnnotationStyle CurrentStyle => StyleForTool(CurrentTool);
    public AnnotationStyle StyleForTool(ToolKind tool) => Options.StyleFor(tool, _parts.Toolbar.CurrentColor, _parts.Toolbar.CurrentThickness);
    public ITool Tool(ToolKind kind) => _tools[kind];
    public Cursor CursorFor(ToolKind kind) => _tools[kind].Cursor;
    public RulerTool Ruler => _ruler;
    /// <summary>Shape snapping: current guides and gesture targets.</summary>
    public Snapper Snapper => _snapper;
    public bool PipetteOn => _pipette;
    public bool IsTextEditing => _textEdit is not null;
    /// <summary>Tool is driving mouse (between Down and Up).</summary>
    public bool HasGesture => _activeTool is not null;
    /// <summary>Tool gesture actually running: undo/redo and repaint forbidden.</summary>
    public bool ToolGestureActive => _activeTool?.IsActive == true;
    public bool BeautifyOn => _beautifyOn;
    private bool BeautifyAllowed => _settings.IsFeatureOn(FeatureCatalog.Beautify);
    /// <summary>Options flyouts: decoration near ✨ button, tool near its button, shape next to it.</summary>
    public OptionsFlyouts Flyouts => _parts.Flyouts;
    public BeautifyOptions Beautify => _beautify;

    /// <summary>
    /// Whether drawing and editing allowed (editor window in “Original” mode disables): tools, palette,
    /// thickness, undo and drawing keys. Disabling resets tool to Select and deselects shape.
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

    // ---------- Tool gesture ----------

    /// <summary>Press with current tool. true — gesture started (host needs to capture mouse).</summary>
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
    /// Alt pressed or released mid-gesture: same gesture at same point recalculated, and snapping
    /// with guides enabled or disabled immediately, without mouse movement.
    /// </summary>
    public void RefreshGesture(Point px) { if (ToolGestureActive) _activeTool?.Move(px); }

    /// <summary>Release. true — there was gesture (host release mouse capture).</summary>
    public bool EndGesture(Point px)
    {
        if (_activeTool is not { } tool) return false;
        _activeTool = null;
        tool.Up(px);
        RefreshOptions(); // shape released: its flyout appears at new location
        return true;
    }

    /// <summary>Abort gesture without trace: live shape discarded, offset rolled back.</summary>
    public void CancelGesture()
    {
        var tool = _activeTool;
        _activeTool = null;
        tool?.Cancel();
        if (tool is not null) RefreshOptions();
    }

    /// <summary>While tool drives gesture, undo/redo forbidden: otherwise foreign step under live shape would be undone.</summary>
    public void Undo() { if (!ToolGestureActive && _drawingEnabled) Document.Undo.Undo(); }
    public void Redo() { if (!ToolGestureActive && _drawingEnabled) Document.Undo.Redo(); }

    /// <summary>Mouse wheel: thickness ±1, remembered.</summary>
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

    // ---------- Keys ----------

    /// <summary>
    /// Drawing keys: Ctrl+Z/Ctrl+Y, Delete/Backspace and arrows for selected shape, and in drawing mode
    /// (<paramref name="editing"/>) also I (eyedropper), B (decoration) and tool keys. false — not our key.
    /// Keys of disabled features (eyedropper, decoration, ruler) — also not ours.
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

    // ---------- Text ----------

    private void BeginTextEdit(Point px, AnnotationDocument doc, AnnotationStyle style)
    {
        var editor = _parts.TextEditor;
        _textEdit = (px, style);
        var hud = _host.PixelToHud(px);
        editor.Text = "";
        editor.FontSize = _host.PixelsToHud(TextAnnotation.FontSizeFor(style.Thickness));
        ShowTextEditorStyle(style);
        // Compensate TextBox border and inner padding so text sits where it will be drawn.
        Canvas.SetLeft(editor, hud.X - 3);
        Canvas.SetTop(editor, hud.Y - 1);
        editor.Visibility = Visibility.Visible;
        // While text is being entered, options and palette edit it, not previously selected shape.
        Deselect();
        editor.Focus();
        Keyboard.Focus(editor);
    }

    /// <summary>Input field shows text color and background of future annotation.</summary>
    private void ShowTextEditorStyle(AnnotationStyle style)
    {
        var editor = _parts.TextEditor;
        _editorBackground ??= editor.Background;
        editor.Foreground = new SolidColorBrush(style.Color);
        editor.Background = style.TextBackground is { } bg ? new SolidColorBrush(bg) : _editorBackground;
        // Field grows by longest line like annotation block, and aligns lines same. Minimum width
        // from XAML is only for left alignment: otherwise short line would center (right) in empty field
        // and after Enter jump to left edge of block.
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

    // ---------- Options and palette ----------

    /// <summary>Shape to which options and palette apply: selected and still in document.</summary>
    private Annotation? OptionsSubject => Document.Selected is { } s && Document.Items.Contains(s) ? s : null;

    private IReadOnlyList<OptionsSectionKind> CurrentSections()
    {
        if (!_host.OptionsEnabled) return [];
        var sections = OptionsSections.For(new OptionsContext(CurrentTool, OptionsSubject) { Beautify = _beautifyOn });
        // Without drawing only decoration remains: it changes result, not marks.
        return _drawingEnabled ? sections : sections.Where(s => s == OptionsSectionKind.Beautify).ToList();
    }

    /// <summary>Style edited by options and palette: selected shape, entering text or future shape of tool.</summary>
    private AnnotationStyle EffectiveStyle()
    {
        if (OptionsSubject is { } s) return s.Style;
        if (_textEdit is { } edit) return edit.Style;
        return CurrentStyle;
    }

    /// <summary>
    /// Shows in options tool or shape section and style, highlights active swatch color on palette
    /// and tells flyouts new state (open near shape, close, move).
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
        // Shape options next to it — only on explicit Select tool selection; while dragged, hidden.
        var shape = CurrentTool == ToolKind.Select ? OptionsSubject : null;
        _parts.Flyouts.Update(new FlyoutState(_host.OptionsEnabled && !_host.IsClosing, CurrentTool, main, shape,
            ShapeHidden: ToolGestureActive || _gestureStarting, BeautifyOn: _beautifyOn));
    }

    /// <summary>
    /// Edit from options or palette: new style applied to selected shape as one undo step
    /// (or to entering text) and remembered as future shape options.
    /// </summary>
    private void EditStyle(Func<AnnotationStyle, AnnotationStyle> op)
    {
        if (ToolGestureActive || !_drawingEnabled) return; // don't repaint shape under live gesture
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

    /// <summary>Palette click (and eyedropper color) paints active swatch or main color.</summary>
    private void OnColorPicked(Color c)
    {
        var target = OptionsSections.EffectiveTarget(CurrentSections(), Options.ActiveTarget);
        EditStyle(s => Options.Paint(s, target, c));
    }

    /// <summary>Swatch click makes it active; disabled part (fill, background, stroke) gets enabled.</summary>
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

    /// <summary>Reset options cache: next RefreshOptions will show them anew.</summary>
    public void InvalidateOptions() => _stripKey = null;

    /// <summary>Unified tool change: button, key, Esc. Eyedropper disabled, ruler measurement removed.
    /// Tool of disabled feature (ruler) not selected.</summary>
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
    /// Features enabled/disabled while host open (editor window): panel buttons per new settings,
    /// disabled eyedropper, ruler and decoration removed. Saved BeautifyEnabled unchanged.
    /// </summary>
    public void ApplyFeatures()
    {
        _parts.Toolbar.ApplyFeatures(_settings);
        if (_pipette && !_settings.IsFeatureOn(FeatureCatalog.Pipette)) SetPipette(false);
        if (!_settings.ToolAllowed(CurrentTool)) SetTool(ToolKind.Select);
        if (_beautifyOn && !BeautifyAllowed) ApplyBeautify(false, persist: false);
    }

    // ---------- Eyedropper ----------

    public void TogglePipette() => SetPipette(!_pipette);

    /// <summary>Eyedropper mode (button or I): crosshair cursor with magnifier, next click on screenshot picks color. One-shot.</summary>
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

    /// <summary>Eyedropper click: screenshot pixel color → active swatch, mode off.</summary>
    public void PickColor(PixelPoint p)
    {
        var c = Pipette.ColorAt(Source, p);
        SetPipette(false);
        OnColorPicked(c);
    }

    // ---------- Decoration ----------

    public void ToggleBeautify() => SetBeautify(!_beautifyOn);

    public void SetBeautify(bool on)
    {
        if (on && !BeautifyAllowed) return; // and BeautifyEnabled setting untouched
        ApplyBeautify(on, _persistBeautify);
    }

    private void ApplyBeautify(bool on, bool persist)
    {
        if (_host.IsClosing) return;
        _beautifyOn = on;
        if (persist) _settings.BeautifyEnabled = on;
        _parts.Toolbar.SetBeautify(on);
        RefreshOptions();
        _parts.Flyouts.OnBeautifyToggled(on); // enabled — decoration options immediately at ✨ button
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
        catch (Exception) { /* broken setting — default color */ }
        return ToolbarControl.Palette[0];
    }
}
