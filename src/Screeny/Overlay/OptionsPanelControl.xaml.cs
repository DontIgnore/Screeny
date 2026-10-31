using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Screeny.Annotations;

namespace Screeny.Overlay;

/// <summary>
/// Options of one section (<see cref="OptionsSections.ForTool"/> / <see cref="OptionsSections.ForAnnotation"/>):
/// stroke and fill, arrow style or text. Flyout content at tool button or next to selected shape.
/// Itself changes nothing — reports clicks via events, shows state by <see cref="Show"/>.
/// All buttons Focusable=False: keyboard stays with window.
/// </summary>
public partial class OptionsPanelControl : UserControl, IFlyoutItems
{
    private OptionsSectionKind? _shown;

    /// <summary>Swatch click: make active (disabled part enables).</summary>
    public event Action<PaintTarget>? TargetClicked;
    public event Action? StrokeNoneClicked;
    public event Action? FillNoneClicked;
    public event Action? FillOpacityClicked;
    public event Action? BackgroundNoneClicked;
    public event Action<ArrowStyle>? ArrowStyleClicked;
    public event Action<TextAlign>? TextAlignClicked;

    public OptionsPanelControl() => InitializeComponent();

    /// <summary>Shown section; null — nothing to show.</summary>
    public OptionsSectionKind? Shown => _shown;

    /// <summary>Rows (arrow — buttons) of shown section: appear one by one on flyout open.</summary>
    public IReadOnlyList<UIElement> FlyoutItems => SectionPanel(_shown) is { } panel ? panel.Children.Cast<UIElement>().ToList() : [];

    /// <summary>
    /// Show section and its state: <paramref name="style"/> — style of selected or future shape,
    /// <paramref name="active"/> — highlighted swatch, <paramref name="fillOpacity"/> — fill opacity label.
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

    /// <summary>ToggleButton toggles IsChecked itself; state set by editor via Show.</summary>
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
