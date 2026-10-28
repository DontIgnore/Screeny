using System.Windows;
using System.Windows.Media;

namespace Screeny.Annotations;

/// <summary>Векторная аннотация в пикселях снимка. Один и тот же Render используется для экрана и экспорта.</summary>
public abstract class Annotation
{
    private AnnotationStyle _style;
    private SolidColorBrush? _brush, _fillBrush;
    private Pen? _pen, _hitPen;

    protected Annotation(AnnotationStyle style) => _style = style;

    /// <summary>Смена стиля сбрасывает кэш кистей, перьев и прочих зависящих от стиля ресурсов.</summary>
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
    /// Видимый охват: все пиксели отрисовки (с толщиной обводки, наконечниками, подложкой). По нему строится
    /// рамка выбора и отбор фигур, попадающих в регион.
    /// </summary>
    public abstract Rect Bounds { get; }

    /// <summary>
    /// Геометрический охват без толщины обводки (прямоугольник фигуры, концы линии): к его краям и центру
    /// прилипают фигуры. По умолчанию совпадает с <see cref="Bounds"/>.
    /// </summary>
    public virtual Rect GeometryBounds => Bounds;
    public abstract void Render(DrawingContext dc, RenderContext ctx);
    public virtual bool HitTest(Point p) => Bounds.InflateCopy(4).Contains(p);
    public abstract void Offset(Vector delta);

    /// <summary>Вызывается после смены <see cref="Style"/>: сбросить свои кэши.</summary>
    protected virtual void OnStyleChanged() { }

    /// <summary>Замороженная кисть цвета стиля (кэш до смены стиля).</summary>
    protected SolidColorBrush StyleBrush => _brush ??= Freeze(new SolidColorBrush(Style.Color));

    /// <summary>Замороженная кисть заливки стиля или null без заливки (кэш до смены стиля).</summary>
    protected SolidColorBrush? FillBrush => Style.Fill is { } fill ? _fillBrush ??= Freeze(new SolidColorBrush(fill)) : null;

    /// <summary>Перо обводки или null, если обводка выключена.</summary>
    protected Pen? StrokePenOrNull => Style.StrokeEnabled ? MakePen() : null;

    /// <summary>Замороженное перо стиля с круглыми концами (кэш до смены стиля).</summary>
    protected Pen MakePen() => _pen ??= Freeze(new Pen(StyleBrush, Style.Thickness)
    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });

    /// <summary>Толстая невидимая «ручка» для попадания мышью по тонким линиям (кэш до смены стиля).</summary>
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
