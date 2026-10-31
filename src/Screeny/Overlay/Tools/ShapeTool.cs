using System.Windows;
using System.Windows.Input;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Editing;

namespace Screeny.Overlay.Tools;

/// <summary>
/// Shapes by two points: border, ellipse, line, arrow, pixelate, blur.
/// During drag shape is live annotation of document (outside undo); enters document only on Up.
/// </summary>
/// <param name="constrain">Shift held: square/circle, line 45° step. Without — freeform.</param>
/// <param name="snap">
/// Snapping: start point and dragged corner (line end) snap to edges/centers of other shapes and area.
/// With Shift doesn't work — square and 45° beat it.
/// </param>
public sealed class ShapeTool(ToolKind kind, Func<bool>? constrain = null, Snapper? snap = null) : ITool
{
    private const double MinSize = 2;
    private AnnotationDocument? _doc;
    private Annotation? _shape;
    private Point _start;

    public Cursor Cursor => Cursors.Cross;
    public bool IsActive => _shape is not null;

    public void Down(Point px, AnnotationDocument doc, AnnotationStyle style)
    {
        snap?.Begin(doc);
        if (snap is not null && constrain?.Invoke() != true) px = snap.SnapPoint(px);
        _doc = doc; _start = px;
        _shape = kind switch
        {
            ToolKind.Rect => new RectAnnotation(new Rect(px, px), style),
            ToolKind.Ellipse => new EllipseAnnotation(new Rect(px, px), style),
            ToolKind.Line => new LineAnnotation(px, px, style),
            ToolKind.Arrow => new ArrowAnnotation(px, px, style),
            ToolKind.Pixelate => new PixelateAnnotation(new Rect(px, px), style),
            ToolKind.Blur => new BlurAnnotation(new Rect(px, px), style),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        doc.Live = _shape;
        // Effects during drag computed on downscaled copy; full computation after Up.
        if (_shape is EffectAnnotation effect) effect.BeginLiveEdit();
    }

    public void Move(Point px)
    {
        if (_shape is null || _doc is null) return;
        bool shift = constrain?.Invoke() == true;
        if (shift) snap?.Hide();
        else if (snap is not null) px = snap.SnapPoint(px);
        switch (_shape)
        {
            case LineAnnotation line:
                line.End = shift ? SnapAngle(_start, px) : px;
                break;
            case BoxAnnotation box:
                box.Rect = shift ? Square(_start, px) : new Rect(_start, px);
                break;
        }
        _doc.NotifyChanged();
    }

    public void Up(Point px)
    {
        if (_shape is null || _doc is null) return;
        Move(px);
        var shape = _shape;
        _shape = null;
        snap?.End();
        _doc.Live = null;
        if (shape is EffectAnnotation effect) effect.EndLiveEdit();
        var b = shape is LineAnnotation l ? new Rect(l.Start, l.End) : shape.GeometryBounds;
        // Accidental click without drag discarded: nothing in undo, redo untouched.
        if (b.Width < MinSize && b.Height < MinSize) { _doc.NotifyChanged(); return; }
        _doc.Add(shape);
    }

    public void Cancel()
    {
        if (_shape is null) return;
        if (_shape is EffectAnnotation effect) effect.EndLiveEdit();
        _shape = null;
        snap?.End();
        if (_doc is not null) _doc.Live = null;
    }

    private static Rect Square(Point a, Point b)
    {
        double side = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        return new Rect(a, new Point(a.X + SignOrPlus(b.X - a.X) * side, a.Y + SignOrPlus(b.Y - a.Y) * side));
    }

    /// <summary>Offset sign; on strictly vertical/horizontal movement zero side grows positive.</summary>
    private static int SignOrPlus(double v) => v < 0 ? -1 : 1;

    /// <summary>Shift for lines — 45° step.</summary>
    private static Point SnapAngle(Point a, Point b)
    {
        var v = b - a;
        double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        return a + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
    }
}
