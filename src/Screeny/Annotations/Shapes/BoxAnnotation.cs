using System.Windows;

namespace Screeny.Annotations.Shapes;

/// <summary>Аннотация, заданная прямоугольником (рамка, эллипс, эффекты). Rect меняется при «живом» рисовании.</summary>
public abstract class BoxAnnotation : Annotation
{
    private Rect _rect;

    protected BoxAnnotation(Rect rect, AnnotationStyle style) : base(style) => _rect = rect;

    public Rect Rect { get => _rect; set { _rect = value; OnGeometryChanged(); } }
    /// <summary>Прямоугольник фигуры; обводку (половина толщины снаружи) добавляют фигуры с обводкой.</summary>
    public override Rect Bounds => _rect;
    public override Rect GeometryBounds => _rect;
    public override void Offset(Vector delta) => Rect = Rect.Offset(_rect, delta);

    protected virtual void OnGeometryChanged() { }
}
