using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Screeny.Annotations;

namespace Screeny.Overlay;

/// <summary>
/// Параметры одной секции (<see cref="OptionsSections.ForTool"/> / <see cref="OptionsSections.ForAnnotation"/>):
/// обводка и заливка, стиль стрелки или текст. Содержимое flyout'а у кнопки инструмента или рядом с выбранной фигурой.
/// Сама ничего не меняет — сообщает о кликах событиями, а состояние показывает по <see cref="Show"/>.
/// Все кнопки Focusable=False: клавиатура остаётся у окна.
/// </summary>
public partial class OptionsPanelControl : UserControl, IFlyoutItems
{
    private OptionsSectionKind? _shown;

    /// <summary>Клик по образцу: сделать его активным (выключенная часть при этом включается).</summary>
    public event Action<PaintTarget>? TargetClicked;
    public event Action? StrokeNoneClicked;
    public event Action? FillNoneClicked;
    public event Action? FillOpacityClicked;
    public event Action? BackgroundNoneClicked;
    public event Action<ArrowStyle>? ArrowStyleClicked;
    public event Action<TextAlign>? TextAlignClicked;

    public OptionsPanelControl() => InitializeComponent();

    /// <summary>Показанная секция; null — показывать нечего.</summary>
    public OptionsSectionKind? Shown => _shown;

    /// <summary>Строки (у стрелки — кнопки) показанной секции: при открытии flyout'а они появляются поочерёдно.</summary>
    public IReadOnlyList<UIElement> FlyoutItems => SectionPanel(_shown) is { } panel ? panel.Children.Cast<UIElement>().ToList() : [];

    /// <summary>
    /// Показать секцию и её состояние: <paramref name="style"/> — стиль выбранной фигуры или будущей,
    /// <paramref name="active"/> — подсвеченный образец, <paramref name="fillOpacity"/> — подпись прозрачности заливки.
    /// </summary>
    public void Show(OptionsSectionKind? section, AnnotationStyle style, PaintTarget active, int fillOpacity)
    {
        _shown = section is OptionsSectionKind.Shape or OptionsSectionKind.Arrow or OptionsSectionKind.Text ? section : null;
        foreach (var kind in new[] { OptionsSectionKind.Shape, OptionsSectionKind.Arrow, OptionsSectionKind.Text })
            SectionPanel(kind)!.Visibility = kind == _shown ? Visibility.Visible : Visibility.Collapsed;
        switch (_shown)
        {
            case OptionsSectionKind.Shape: ShowShape(style, active, fillOpacity); break;
            case OptionsSectionKind.Arrow: ShowArrow(style.Arrow); break;
            case OptionsSectionKind.Text: ShowText(style, active); break;
        }
    }

    private Panel? SectionPanel(OptionsSectionKind? kind) => kind switch
    {
        OptionsSectionKind.Shape => ShapeSection,
        OptionsSectionKind.Arrow => ArrowSection,
        OptionsSectionKind.Text => TextSection,
        _ => null,
    };

    private void ShowShape(AnnotationStyle s, PaintTarget active, int fillOpacity)
    {
        StrokeChip.BorderBrush = s.StrokeEnabled ? Brush(s.Color) : (Brush)FindResource("Overlay.Divider");
        StrokeNone.Visibility = s.StrokeEnabled ? Visibility.Collapsed : Visibility.Visible;
        StrokeNoneButton.IsChecked = !s.StrokeEnabled;
        StrokeNoneButton.IsEnabled = !s.StrokeEnabled || s.CanDisableStroke;
        StrokeNoneButton.ToolTip = Localization.Loc.T(!StrokeNoneButton.IsEnabled ? "Options.NeedStrokeOrFill" : s.StrokeEnabled ? "Options.NoStroke" : "Options.RestoreStroke");

        FillChip.Background = s.Fill is { } fill ? Brush(fill) : Brushes.Transparent;
        FillNone.Visibility = s.Fill is null ? Visibility.Visible : Visibility.Collapsed;
        FillNoneButton.IsChecked = s.Fill is null;
        FillNoneButton.IsEnabled = s.Fill is null || s.CanDisableFill;
        FillNoneButton.ToolTip = Localization.Loc.T(!FillNoneButton.IsEnabled ? "Options.NeedStrokeOrFill" : s.Fill is null ? "Options.RestoreFill" : "Options.NoFill");
        FillOpacityButton.Content = $"{fillOpacity} %";

        SetActive(StrokeSwatch, active == PaintTarget.Color);
        SetActive(FillSwatch, active == PaintTarget.Fill);
    }

    private void ShowArrow(ArrowStyle arrow)
    {
        foreach (var (button, kind) in new[] { (ArrowNormal, ArrowStyle.Normal), (ArrowDouble, ArrowStyle.Double), (ArrowDashed, ArrowStyle.Dashed), (ArrowThick, ArrowStyle.Thick) })
            button.IsChecked = kind == arrow;
    }

    private void ShowText(AnnotationStyle s, PaintTarget active)
    {
        TextChip.Background = Brush(s.Color);
        BackgroundChip.Background = s.TextBackground is { } bg ? Brush(bg) : Brushes.Transparent;
        BackgroundNone.Visibility = s.TextBackground is null ? Visibility.Visible : Visibility.Collapsed;
        BackgroundNoneButton.IsChecked = s.TextBackground is null;
        BackgroundNoneButton.ToolTip = Localization.Loc.T(s.TextBackground is null ? "Options.AddBackground" : "Options.NoBackground");
        SetActive(TextSwatch, active != PaintTarget.Background);
        SetActive(BackgroundSwatch, active == PaintTarget.Background);
        foreach (var (button, align) in new[] { (AlignLeft, TextAlign.Left), (AlignCenter, TextAlign.Center), (AlignRight, TextAlign.Right) })
            button.IsChecked = align == s.TextAlign;
    }

    private static void SetActive(Button swatch, bool on) => swatch.Tag = on ? "Active" : null;

    private static SolidColorBrush Brush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>ToggleButton сам переключает IsChecked; состояние задаёт редактор через Show.</summary>
    private static void Revert(object sender) { if (sender is ToggleButton t) t.IsChecked = !t.IsChecked; }

    private void OnStrokeSwatch(object sender, RoutedEventArgs e) => TargetClicked?.Invoke(PaintTarget.Color);
    private void OnFillSwatch(object sender, RoutedEventArgs e) => TargetClicked?.Invoke(PaintTarget.Fill);
    private void OnTextSwatch(object sender, RoutedEventArgs e) => TargetClicked?.Invoke(PaintTarget.Color);
    private void OnBackgroundSwatch(object sender, RoutedEventArgs e) => TargetClicked?.Invoke(PaintTarget.Background);
    private void OnStrokeNone(object sender, RoutedEventArgs e) { Revert(sender); StrokeNoneClicked?.Invoke(); }
    private void OnFillNone(object sender, RoutedEventArgs e) { Revert(sender); FillNoneClicked?.Invoke(); }
    private void OnBackgroundNone(object sender, RoutedEventArgs e) { Revert(sender); BackgroundNoneClicked?.Invoke(); }
    private void OnFillOpacity(object sender, RoutedEventArgs e) => FillOpacityClicked?.Invoke();

    private void OnArrowStyle(object sender, RoutedEventArgs e)
    {
        Revert(sender);
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<ArrowStyle>(tag, out var style)) ArrowStyleClicked?.Invoke(style);
    }

    private void OnTextAlign(object sender, RoutedEventArgs e)
    {
        Revert(sender);
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<TextAlign>(tag, out var align)) TextAlignClicked?.Invoke(align);
    }
}
