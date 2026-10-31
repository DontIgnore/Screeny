using System.Windows;
using System.Windows.Input;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Editing;

namespace Screeny.Overlay.Tools;

/// <summary>
/// Label shown on Down as live while button held — follows mouse, enters document
/// (one undo step) on Up. With <paramref name="snap"/> its edges and center snap to other shapes and area.
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

    /// <summary>Label center at mouse point with snap correction (by label border: edges and center).</summary>
    private void Place(Point px)
    {
        if (_label is null) return;
        var bounds = _label.GeometryBounds;
        var at = px + (snap?.SnapRect(Rect.Offset(bounds, px - _center)) ?? default);
        _label.Offset(at - _center);
        _center = at;
    }
}
