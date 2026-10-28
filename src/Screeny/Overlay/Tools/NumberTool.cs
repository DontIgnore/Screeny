using System.Windows;
using System.Windows.Input;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Editing;

namespace Screeny.Overlay.Tools;

/// <summary>
/// Метка показывается на Down как живая, пока кнопка зажата — едет за мышью, и попадает в документ
/// (одним шагом undo) на Up. С <paramref name="snap"/> её края и центр прилипают к другим фигурам и области.
/// </summary>
public sealed class NumberTool(Snapper? snap = null) : ITool
{
    private AnnotationDocument? _doc;
    private NumberAnnotation? _label;
    private Point _center;

    public Cursor Cursor => Cursors.Hand;
    public bool IsActive => _label is not null;

    public void Down(Point px, AnnotationDocument doc, AnnotationStyle style)
    {
        _doc = doc;
        snap?.Begin(doc);
        _label = new NumberAnnotation(px, doc.NextNumber(), style);
        _center = px;
        Place(px);
        doc.Live = _label;
    }

    public void Move(Point px)
    {
        if (_label is null || _doc is null) return;
        Place(px);
        _doc.NotifyChanged();
    }

    public void Up(Point px)
    {
        if (_label is null || _doc is null) return;
        Place(px);
        var label = _label;
        _label = null;
        snap?.End();
        _doc.Live = null;
        _doc.Add(label);
    }

    public void Cancel()
    {
        if (_label is null) return;
        _label = null;
        snap?.End();
        if (_doc is not null) _doc.Live = null;
    }

    /// <summary>Центр метки — в точку мыши с поправкой прилипания (по рамке метки: края и центр).</summary>
    private void Place(Point px)
    {
        if (_label is null) return;
        var bounds = _label.GeometryBounds;
        var at = px + (snap?.SnapRect(Rect.Offset(bounds, px - _center)) ?? default);
        _label.Offset(at - _center);
        _center = at;
    }
}
