using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Screeny.Features;
using Screeny.Localization;
using Screeny.Overlay.Tools;
using Screeny.Settings;

namespace Screeny.Overlay;

/// <summary>Inline panel: tools, palette, thickness, undo/redo, completion actions.</summary>
public partial class ToolbarControl : UserControl
{
    public const int MinThickness = 1, MaxThickness = 20;

    public static readonly Color[] Palette =
    [
        Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0xFF, 0x95, 0x00), Color.FromRgb(0xFF, 0xCC, 0x00), Color.FromRgb(0x34, 0xC7, 0x59),
        Color.FromRgb(0x00, 0x7A, 0xFF), Color.FromRgb(0xAF, 0x52, 0xDE), Colors.White, Colors.Black,
    ];

    /// <summary>Tool, glyph, label key (<see cref="Loc"/>) and hotkey — in panel button order.</summary>
    public static readonly (ToolKind Kind, string Glyph, string NameKey, Key Key, string KeyText)[] Tools =
    [
        (ToolKind.Select, "↖", "Tool.Select", Key.D1, "1"),
        (ToolKind.Rect, "▭", "Tool.Rect", Key.D2, "2"),
        (ToolKind.Ellipse, "◯", "Tool.Ellipse", Key.D3, "3"),
        (ToolKind.Line, "╱", "Tool.Line", Key.D4, "4"),
        (ToolKind.Arrow, "➚", "Tool.Arrow", Key.D5, "5"),
        (ToolKind.Pen, "", "Tool.Pen", Key.D6, "6"),
        (ToolKind.Highlighter, "", "Tool.Highlighter", Key.D7, "7"),
        (ToolKind.Text, "", "Tool.Text", Key.D8, "8"),
        (ToolKind.Number, "①", "Tool.Number", Key.D9, "9"),
        (ToolKind.Pixelate, "▦", "Tool.Pixelate", Key.D0, "0"),
        (ToolKind.Blur, "◌", "Tool.Blur", Key.OemMinus, "-"),
        (ToolKind.Ruler, "", "Tool.Ruler", Key.OemPlus, "="),
    ];

    private readonly Dictionary<ToolKind, ToggleButton> _toolButtons = new();
    private readonly List<(Color Color, Border Swatch)> _swatches = new();

    public event Action<CaptureAction>? ActionRequested;
    /// <summary>OCR button: enter or exit text mode.</summary>
    public event Action? TextModeRequested;
    public event Action? CancelRequested;
    public event Action? UndoRequested;
    public event Action? RedoRequested;
    public event Action<ToolKind>? ToolChanged;
    /// <summary>
    /// Palette color click. Panel itself doesn't change color: overlay decides what to paint
    /// (main color — <see cref="SetColor"/>, or fill/background in options strip — <see cref="MarkSwatch"/>).
    /// </summary>
    public event Action<Color>? ColorPicked;
    /// <summary>Eyedropper button (I key).</summary>
    public event Action? PipetteRequested;
    /// <summary>✨ button (B key): enable or disable decoration.</summary>
    public event Action? BeautifyRequested;
    public event Action<int>? ThicknessChanged;

    public ToolbarControl()
    {
        InitializeComponent();
        foreach (var t in Tools)
        {
            var button = new ToggleButton
            {
                Content = t.Glyph,
                Style = (Style)FindResource("IconButton"),
            };
            // Tools with options have dot bottom-right: hovering active opens options.
            if (OptionsSections.ForTool(t.Kind) is not null)
            {
                var dot = new Ellipse { Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 4, 4), IsHitTestVisible = false };
                dot.SetResourceReference(Shape.FillProperty, "Overlay.Ink70");
                var glyph = new TextBlock { Text = t.Glyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                button.Content = new Grid { Width = 32, Height = 30, Children = { glyph, dot } };
            }
            // Plain Unicode symbols of Segoe Fluent Icons are “intercepted” by empty glyph — take from Segoe UI Symbol.
            if (t.Glyph[0] < '') { button.FontFamily = new FontFamily("Segoe UI Symbol"); button.FontSize = 16; }
            var kind = t.Kind;
            button.Click += (_, _) => { SetActiveTool(kind); ToolChanged?.Invoke(kind); };
            _toolButtons[kind] = button;
            ToolPanel.Children.Add(button);
        }
        foreach (var c in Palette)
        {
            var swatch = new Border
            {
                Width = 18, Height = 18, Margin = new Thickness(2, 0, 2, 0), CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(c), BorderThickness = new Thickness(2), Cursor = Cursors.Hand,
                ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}",
            };
            var color = c;
            swatch.MouseLeftButtonDown += (_, e) => { e.Handled = true; ColorPicked?.Invoke(color); };
            _swatches.Add((c, swatch));
            ColorPanel.Children.Add(swatch);
        }
        ApplyTexts();
        // Editor panel lives while language changed in settings: code tooltips rebuilt.
        // Loaded may come more than once (tab, re-show): subscribe without duplicates.
        Loaded += (_, _) => { Loc.Changed -= ApplyTexts; Loc.Changed += ApplyTexts; ApplyTexts(); };
        Unloaded += (_, _) => Loc.Changed -= ApplyTexts;
    }

    private AppSettings? _settings;

    /// <summary>Tooltips and labels depending on panel state (text mode, editor, history).</summary>
    private void ApplyTexts()
    {
        foreach (var t in Tools) _toolButtons[t.Kind].ToolTip = Loc.F("Tool.WithKey", Loc.T(t.NameKey), t.KeyText);
        CancelButton.ToolTip = Loc.T(_forEditor ? "Toolbar.Close" : "Toolbar.Cancel");
        CopyLabel.Text = Loc.T(_textMode ? "Toolbar.CopyText" : "Common.Copy");
        OcrButton.ToolTip = Loc.T(_textMode ? "Toolbar.OcrBack" : "Toolbar.Ocr");
        CopyButton.ToolTip = Loc.T(_textMode ? "Toolbar.CopyTextTip"
            : !_forEditor ? "Toolbar.CopyEnter"
            : _settings?.IsFeatureOn(FeatureCatalog.History) == true ? "Toolbar.CopyEditorHistory" : "Toolbar.CopyEditor");
    }

    /// <summary>Tool buttons in panel order (anchors of options flyouts).</summary>
    public IEnumerable<ButtonBase> ToolButtons => _toolButtons.Values;

    /// <summary>Tool button: its options flyout “attaches” to it.</summary>
    public ButtonBase ToolButton(ToolKind kind) => _toolButtons[kind];

    /// <summary>✨ button: decoration flyout “attaches” to it.</summary>
    public ButtonBase BeautifyToggle => BeautifyButton;

    public ToolKind CurrentTool { get; private set; }
    public Color CurrentColor { get; private set; }
    public int CurrentThickness { get; private set; } = 3;

    public void SetActiveTool(ToolKind kind)
    {
        CurrentTool = kind;
        foreach (var (k, b) in _toolButtons) b.IsChecked = k == kind;
    }

    private static readonly Brush SwatchRing = CreateFrozen(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));

    /// <summary>Main color (stroke, line, text…): highlights on palette and paints thickness dot.</summary>
    public void SetColor(Color color)
    {
        CurrentColor = color;
        MarkSwatch(color);
        ThicknessDot.Fill = new SolidColorBrush(color);
    }

    /// <summary>Highlight color on palette without changing main (active swatch of strip — fill or background). null — nothing.</summary>
    public void MarkSwatch(Color? color)
    {
        foreach (var (c, swatch) in _swatches)
            swatch.BorderBrush = c == color ? Brushes.White : SwatchRing;
    }

    /// <summary>Eyedropper mode on — button pressed.</summary>
    public void SetPipette(bool on) => PipetteButton.IsChecked = on;

    /// <summary>Decoration enabled — ✨ button pressed.</summary>
    public void SetBeautify(bool on) => BeautifyButton.IsChecked = on;

    private static SolidColorBrush CreateFrozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public void SetThickness(int thickness)
    {
        CurrentThickness = Math.Clamp(thickness, MinThickness, MaxThickness);
        ThicknessDot.Width = ThicknessDot.Height = 3 + CurrentThickness;
        ThicknessText.Text = CurrentThickness.ToString();
    }

    private bool _canUndo, _canRedo, _textMode, _drawingDisabled;

    public void SetUndoState(bool canUndo, bool canRedo)
    {
        _canUndo = canUndo; _canRedo = canRedo;
        UndoButton.IsEnabled = canUndo && !_textMode && !_drawingDisabled;
        RedoButton.IsEnabled = canRedo && !_textMode && !_drawingDisabled;
    }

    /// <summary>Tools, palette, thickness, eyedropper and undo available (editor window “Original” mode disables).</summary>
    public void SetDrawingEnabled(bool on)
    {
        _drawingDisabled = !on;
        ApplyDrawingAvailability();
    }

    private void ApplyDrawingAvailability()
    {
        bool off = _textMode || _drawingDisabled;
        ToolPanel.IsEnabled = ColorPanel.IsEnabled = ThicknessBox.IsEnabled = PipetteButton.IsEnabled = !off;
        ToolPanel.Opacity = ColorPanel.Opacity = ThicknessBox.Opacity = off ? 0.35 : 1;
        SetUndoState(_canUndo, _canRedo);
    }

    /// <summary>
    /// Panel in re-edit window: without text mode (OCR), ✕ closes window, “Copy” and other actions add new history entry (if enabled), window stays open.
    ///
    /// </summary>
    public void ConfigureForEditor(AppSettings settings)
    {
        _forEditor = true;
        ApplyFeatures(settings);
    }

    /// <summary>
    /// Disabled feature buttons (<see cref="FeatureCatalog"/>) hide entirely with tooltips; enabled — show again (editor window lives while settings change). OCR in editor window always hidden.
    ///
    /// Keys checked by editor and host.
    /// </summary>
    public void ApplyFeatures(AppSettings settings)
    {
        static void Show(UIElement element, bool on) => element.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Show(OcrButton, settings.IsFeatureOn(FeatureCatalog.Ocr) && !_forEditor);
        Show(PinButton, settings.ActionAllowed(CaptureAction.Pin));
        Show(BeautifyButton, settings.IsFeatureOn(FeatureCatalog.Beautify));
        Show(PipetteButton, settings.IsFeatureOn(FeatureCatalog.Pipette));
        foreach (var (kind, button) in _toolButtons) Show(button, settings.ToolAllowed(kind));
        _settings = settings;
        ApplyTexts();
    }

    private bool _forEditor;

    /// <summary>
    /// Text mode: drawing, palette, thickness and undo unavailable (nothing drawn),
    /// OCR highlighted, main button copies selected text instead of image.
    /// </summary>
    public void SetTextMode(bool on)
    {
        _textMode = on;
        OcrButton.IsChecked = on;
        ApplyDrawingAvailability();
        ApplyTexts();
    }

    private void OnThicknessWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        SetThickness(CurrentThickness + Math.Sign(e.Delta));
        ThicknessChanged?.Invoke(CurrentThickness);
    }

    private void OnCopy(object sender, RoutedEventArgs e) => ActionRequested?.Invoke(CaptureAction.CopyExplicit);
    private void OnSave(object sender, RoutedEventArgs e) => ActionRequested?.Invoke(CaptureAction.Save);
    private void OnPin(object sender, RoutedEventArgs e) => ActionRequested?.Invoke(CaptureAction.Pin);
    private void OnOcr(object sender, RoutedEventArgs e)
    {
        OcrButton.IsChecked = _textMode; // state set by overlay via SetTextMode, not click itself
        TextModeRequested?.Invoke();
    }
    private void OnCancel(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
    private void OnPipette(object sender, RoutedEventArgs e)
    {
        PipetteButton.IsChecked = !PipetteButton.IsChecked; // state set by overlay via SetPipette
        PipetteRequested?.Invoke();
    }
    private void OnBeautify(object sender, RoutedEventArgs e)
    {
        BeautifyButton.IsChecked = !BeautifyButton.IsChecked; // state set by overlay via SetBeautify
        BeautifyRequested?.Invoke();
    }
    private void OnUndo(object sender, RoutedEventArgs e) => UndoRequested?.Invoke();
    private void OnRedo(object sender, RoutedEventArgs e) => RedoRequested?.Invoke();
}
