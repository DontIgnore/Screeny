using System.Windows;
using System.Windows.Input;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;

namespace Screeny.Overlay.Tools;

/// <summary>Штрих рисуется как живая аннотация и попадает в документ одним шагом undo на Up (клик — точка).</summary>
public sealed class PenTool(bool highlighter) : ITool
{
    private const double MinStep = 1.5;
    private AnnotationDocument? _doc;
    private PenAnnotation? _stroke;
    private Point _last;

    public Cursor Cursor => Cursors.Pen;
    public bool IsActive => _stroke is not null;

    public void Down(Point px, AnnotationDocument doc, AnnotationStyle style)
    {
        _doc = doc; _last = px;
        _stroke = highlighter ? new HighlighterAnnotation(px, style) : new PenAnnotation(px, style);
        doc.Live = _stroke;
    }

    public void Move(Point px)
    {
        if (_stroke is null || (px - _last).Length < MinStep) return;
        _stroke.AddPoint(px);
        _last = px;
        _doc?.NotifyChanged();
    }

    public void Up(Point px)
    {
        if (_stroke is null || _doc is null) return;
        Move(px);
        var stroke = _stroke;
        _stroke = null;
        _doc.Live = null;
        _doc.Add(stroke);
    }

    public void Cancel()
    {
        if (_stroke is null) return;
        _stroke = null;
        if (_doc is not null) _doc.Live = null;
    }
}
