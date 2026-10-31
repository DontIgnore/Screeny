using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Screeny.Annotations;

/// <summary>
/// Draws the document in screenshot pixels (layer sits in PixelCanvas under ScaleTransform).
/// The overlay window handles mouse, so the layer is not hit-tested.
/// </summary>
public sealed class AnnotationLayer : FrameworkElement
{
    private static readonly Pen SelectionPen = CreateSelectionPen();

    private double _outlineScale = 1;
    private Pen? _scaledSelectionPen;

    public AnnotationDocument Document { get; } = new();
    public BitmapSource? Source { get; set; }

    /// <summary>
    /// Selected shape border thickness in layer units (1 — like in overlay). The editor window with zoom sets
    /// 1/zoom so the border stays thin and visible on screen at any zoom.
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
