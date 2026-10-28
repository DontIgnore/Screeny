using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Screeny.Annotations;

/// <summary>
/// Рисует документ в пикселях снимка (слой лежит в PixelCanvas под ScaleTransform).
/// Мышь обрабатывает окно оверлея, поэтому слой не участвует в hit-test.
/// </summary>
public sealed class AnnotationLayer : FrameworkElement
{
    private static readonly Pen SelectionPen = CreateSelectionPen();

    private double _outlineScale = 1;
    private Pen? _scaledSelectionPen;

    public AnnotationDocument Document { get; } = new();
    public BitmapSource? Source { get; set; }

    /// <summary>
    /// Толщина рамки выбранной фигуры в единицах слоя (1 — как в оверлее). Окно редактора с масштабом задаёт
    /// 1/масштаб, чтобы рамка на экране оставалась тонкой и видимой при любом зуме.
    /// </summary>
    public double OutlineScale
    {
        get => _outlineScale;
        set
        {
            if (_outlineScale == value || !(value > 0)) return;
            _outlineScale = value;
            _scaledSelectionPen = null;
            InvalidateVisual();
        }
    }

    public AnnotationLayer()
    {
        Document.Changed += InvalidateVisual;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Source is null) return;
        Document.Render(dc, new RenderContext(Source, ForExport: false));
        if (Document.Selected is not { } s) return;
        if (_outlineScale == 1) dc.DrawRectangle(null, SelectionPen, s.Bounds.InflateCopy(3));
        else dc.DrawRectangle(null, ScaledSelectionPen(), s.Bounds.InflateCopy(3 * _outlineScale));
    }

    private Pen ScaledSelectionPen()
    {
        if (_scaledSelectionPen is not null) return _scaledSelectionPen;
        var pen = new Pen(Brushes.DeepSkyBlue, _outlineScale) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return _scaledSelectionPen = pen;
    }

    private static Pen CreateSelectionPen()
    {
        var pen = new Pen(Brushes.DeepSkyBlue, 1) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        return pen;
    }
}
