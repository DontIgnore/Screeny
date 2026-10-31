using System.Windows;
using System.Windows.Media;

namespace Screeny.Annotations;

/// <summary>A vector annotation in screenshot pixels. The same Render is used for screen and export.</summary>
public abstract class Annotation
{
    private AnnotationStyle _style;
    private SolidColorBrush? _brush, _fillBrush;
    private Pen? _pen, _hitPen;

    protected Annotation(AnnotationStyle style) => _style = style;

    /// <summary>Changing the style resets the cache of brushes, pens and other style-dependent resources.</summary>
    public AnnotationStyle Style
    {
        get => _style;
        set
        {
            if (_style == value) return;
            _style = value;
            _brush = null; _fillBrush = null; _pen = null; _hitPen = null;
            OnStyleChanged();
        }
    }

    /// <summary>
    /// Visible bounds: all rendering pixels (including stroke thickness, arrowheads, background). It defines
    /// the selection box and picking shapes that fall into the region.
    /// </summary>
    public abstract Rect Bounds { get; }

    /// <summary>
    /// Geometric bounds without stroke thickness (shape rectangle, line ends): its edges and center
    /// are snap targets for shapes. By default matches <see cref="Bounds"/>.
    /// </summary>
    public virtual Rect GeometryBounds => Bounds;
    public abstract void Render(DrawingContext dc, RenderContext ctx);
    public virtual bool HitTest(Point p) => Bounds.InflateCopy(4).Contains(p);
    public abstract void Offset(Vector delta);

    /// <summary>Called after <see cref="Style"/> changes: reset your caches.</summary>
    protected virtual void OnStyleChanged() { }

    /// <summary>Frozen brush of the style color (cache until style change).</summary>
    protected SolidColorBrush StyleBrush => _brush ??= Freeze(new SolidColorBrush(Style.Color));

    /// <summary>Frozen style fill brush or null when no fill (cache until style change).</summary>
    protected SolidColorBrush? FillBrush => Style.Fill is { } fill ? _fillBrush ??= Freeze(new SolidColorBrush(fill)) : null;

    /// <summary>Stroke pen or null if stroke is off.</summary>
    protected Pen? StrokePenOrNull => Style.StrokeEnabled ? MakePen() : null;

    /// <summary>Frozen style pen with round caps (cache until style change).</summary>
    protected Pen MakePen() => _pen ??= Freeze(new Pen(StyleBrush, Style.Thickness)
    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });

    /// <summary>Thick invisible handle for mouse hit-testing on thin lines (cache until style change).</summary>
    protected Pen HitPen() => _hitPen ??= Freeze(new Pen(Brushes.Black, Style.Thickness + 8));

    protected static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}

internal static class RectExt
{
    public static Rect InflateCopy(this Rect r, double d)
    {
        if (r.IsEmpty) return r;
        if (d < 0 && (r.Width < -2 * d || r.Height < -2 * d)) return Rect.Empty;
        r.Inflate(d, d);
        return r;
    }
}
