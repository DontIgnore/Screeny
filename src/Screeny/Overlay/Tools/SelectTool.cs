using System.Windows;
using System.Windows.Input;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Editing;

namespace Screeny.Overlay.Tools;

/// <summary>
/// Annotation select and move. During drag shape moves directly,
/// on release applied offset recorded into undo as one step (without re-applying).
/// With <paramref name="snap"/> edges and center snap to other shapes and area: offset computed from press point
/// (raw mouse offset + snap correction), so shape doesn't “stick” or accumulate error.
/// </summary>
public sealed class SelectTool(Snapper? snap = null) : ITool
{
    private AnnotationDocument? _doc;
    private Annotation? _dragging;
    private Point _down;
    private Rect _startBounds;
    private Vector _total;

    public Cursor Cursor => Cursors.Arrow;
    public bool IsActive => _dragging is not null;

    public void Down(Point px, AnnotationDocument doc, AnnotationStyle style)
    {
        _doc = doc;
        doc.Selected = doc.HitTest(px);
        _dragging = doc.Selected;
        _down = px;
        _total = default;
        if (_dragging is not null)
        {
            _startBounds = _dragging.GeometryBounds;
            snap?.Begin(doc, exclude: _dragging);
        }
        // Effect during drag computed on downscaled copy, full recompute after release.
        if (_dragging is EffectAnnotation effect) effect.BeginLiveEdit();
        doc.NotifyChanged();
    }

    public void Move(Point px)
    {
        if (_dragging is null || _doc is null) return;
        var raw = px - _down;
        var desired = raw + (snap?.SnapRect(Rect.Offset(_startBounds, raw)) ?? default);
        var delta = desired - _total;
        if (delta.LengthSquared == 0) return;
        _dragging.Offset(delta);
        _total = desired;
        _doc.NotifyChanged();
    }

    public void Up(Point px)
    {
        if (_dragging is null || _doc is null) return;
        Move(px);
        var dragged = _dragging;
        var total = _total;
        _dragging = null;
        _total = default;
        snap?.End();
        if (dragged is EffectAnnotation effect) { effect.EndLiveEdit(); _doc.NotifyChanged(); }
        if (total.LengthSquared == 0) return;
        // Shape could be undone (Ctrl+Z) mid-gesture: no step, offset rolled back,
        // so Redo returns it to previous place.
        if (!_doc.RecordMove(dragged, total))
        {
            dragged.Offset(-total);
            _doc.NotifyChanged();
        }
    }

    public void Cancel()
    {
        if (_dragging is null) return;
        if (_total.LengthSquared > 0) _dragging.Offset(-_total);
        if (_dragging is EffectAnnotation effect) effect.EndLiveEdit();
        _dragging = null;
        _total = default;
        snap?.End();
        _doc?.NotifyChanged();
    }
}
