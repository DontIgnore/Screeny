using System.Windows;

namespace Screeny.Annotations.Shapes;

/// <summary>Annotation defined by rectangle (box, ellipse, effects). Rect changes during “live” drawing.</summary>
public abstract class BoxAnnotation : Annotation
{
    private Rect _rect;

    protected BoxAnnotation(Rect rect, AnnotationStyle style) : base(style) => _rect = rect;

    public Rect Rect { get => _rect; set { _rect = value; OnGeometryChanged(); } }
    /// <summary>Shape rectangle; stroke (half thickness outside) is added by shapes with stroke.</summary>
    public override Rect Bounds => _rect;
    public override Rect GeometryBounds => _rect;
    public override void Offset(Vector delta) => Rect = Rect.Offset(_rect, delta);

    protected virtual void OnGeometryChanged() { }
}
