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

/// <summary>Инлайн-панель: инструменты, палитра, толщина, undo/redo, действия завершения.</summary>
public partial class ToolbarControl : UserControl
{
    public const int MinThickness = 1, MaxThickness = 20;

    public static readonly Color[] Palette =
    [
        Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0xFF, 0x95, 0x00), Color.FromRgb(0xFF, 0xCC, 0x00), Color.FromRgb(0x34, 0xC7, 0x59),
        Color.FromRgb(0x00, 0x7A, 0xFF), Color.FromRgb(0xAF, 0x52, 0xDE), Colors.White, Colors.Black,
    ];

    /// <summary>Инструмент, глиф, ключ подписи (<see cref="Loc"/>) и горячая клавиша — в порядке кнопок на панели.</summary>
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
    /// <summary>Кнопка OCR: войти в режим текста или выйти из него.</summary>
    public event Action? TextModeRequested;
    public event Action? CancelRequested;
    public event Action? UndoRequested;
    public event Action? RedoRequested;
    public event Action<ToolKind>? ToolChanged;
    /// <summary>
    /// Клик по цвету палитры. Сама панель свой цвет не меняет: оверлей решает, что красить
    /// (основной цвет — <see cref="SetColor"/>, или заливку/фон в полосе параметров — <see cref="MarkSwatch"/>).
    /// </summary>
    public event Action<Color>? ColorPicked;
    /// <summary>Кнопка пипетки (клавиша I).</summary>
    public event Action? PipetteRequested;
    /// <summary>Кнопка ✨ (клавиша B): включить или выключить оформление.</summary>
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
            // У инструментов с параметрами — точка в правом нижнем углу: наведение на активный открывает параметры.
            if (OptionsSections.ForTool(t.Kind) is not null)
            {
                var dot = new Ellipse { Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 4, 4), IsHitTestVisible = false };
                dot.SetResourceReference(Shape.FillProperty, "Overlay.Ink70");
                var glyph = new TextBlock { Text = t.Glyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                button.Content = new Grid { Width = 32, Height = 30, Children = { glyph, dot } };
            }
            // Обычные Unicode-символы Segoe Fluent Icons «перехватывает» пустым глифом — берём их из Segoe UI Symbol.
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
        // Панель редактора живёт, пока в настройках меняют язык: подсказки из кода собираются заново.
        // Loaded может прийти не один раз (вкладка, повторный показ): подписка без дублей.
        Loaded += (_, _) => { Loc.Changed -= ApplyTexts; Loc.Changed += ApplyTexts; ApplyTexts(); };
        Unloaded += (_, _) => Loc.Changed -= ApplyTexts;
    }

    private AppSettings? _settings;

    /// <summary>Подсказки и подписи, которые зависят от состояния панели (режим текста, редактор, история).</summary>
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

    /// <summary>Кнопки инструментов по порядку панели (якоря flyout'ов параметров).</summary>
    public IEnumerable<ButtonBase> ToolButtons => _toolButtons.Values;

    /// <summary>Кнопка инструмента: к ней «пристёгивается» flyout его параметров.</summary>
    public ButtonBase ToolButton(ToolKind kind) => _toolButtons[kind];

    /// <summary>Кнопка ✨: к ней «пристёгивается» flyout оформления.</summary>
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

    /// <summary>Основной цвет (обводка, линия, текст…): подсвечивает его на палитре и красит точку толщины.</summary>
    public void SetColor(Color color)
    {
        CurrentColor = color;
        MarkSwatch(color);
        ThicknessDot.Fill = new SolidColorBrush(color);
    }

    /// <summary>Подсветить цвет на палитре, не меняя основной (активный образец полосы — заливка или фон). null — ничего.</summary>
    public void MarkSwatch(Color? color)
    {
        foreach (var (c, swatch) in _swatches)
            swatch.BorderBrush = c == color ? Brushes.White : SwatchRing;
    }

    /// <summary>Режим пипетки включён — кнопка нажата.</summary>
    public void SetPipette(bool on) => PipetteButton.IsChecked = on;

    /// <summary>Оформление включено — кнопка ✨ нажата.</summary>
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

    /// <summary>Инструменты, палитра, толщина, пипетка и undo доступны (окно редактора в режиме «Оригинал» их выключает).</summary>
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
    /// Панель в окне повторного редактирования: без режима текста (OCR), ✕ закрывает окно, «Копировать» и прочие
    /// действия добавляют новую запись в историю (если она включена), окно остаётся открытым.
    /// </summary>
    public void ConfigureForEditor(AppSettings settings)
    {
        _forEditor = true;
        ApplyFeatures(settings);
    }

    /// <summary>
    /// Кнопки выключенных функций (<see cref="FeatureCatalog"/>) скрываются целиком, вместе с подсказками; включённых —
    /// показываются снова (окно редактора живёт, пока меняют настройки). OCR в окне редактора скрыт всегда.
    /// Клавиши проверяют редактор и хост.
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
    /// Режим текста: рисование, палитра, толщина и undo недоступны (в нём ничего не рисуется),
    /// OCR подсвечен, а главная кнопка копирует выбранный текст вместо картинки.
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
        OcrButton.IsChecked = _textMode; // состояние задаёт оверлей через SetTextMode, а не сам клик
        TextModeRequested?.Invoke();
    }
    private void OnCancel(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
    private void OnPipette(object sender, RoutedEventArgs e)
    {
        PipetteButton.IsChecked = !PipetteButton.IsChecked; // состояние задаёт оверлей через SetPipette
        PipetteRequested?.Invoke();
    }
    private void OnBeautify(object sender, RoutedEventArgs e)
    {
        BeautifyButton.IsChecked = !BeautifyButton.IsChecked; // состояние задаёт оверлей через SetBeautify
        BeautifyRequested?.Invoke();
    }
    private void OnUndo(object sender, RoutedEventArgs e) => UndoRequested?.Invoke();
    private void OnRedo(object sender, RoutedEventArgs e) => RedoRequested?.Invoke();
}
