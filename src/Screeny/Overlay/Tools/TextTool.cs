using System.Windows;
using System.Windows.Input;
using Screeny.Annotations;
using Screeny.Editing;

namespace Screeny.Overlay.Tools;

/// <summary>
/// Click opens input field in overlay; annotation added when input confirmed. With <paramref name="snap"/>
/// text top-left snaps to edges/centers of other shapes and area.
/// </summary>
public sealed class TextTool(Snapper? snap = null) : ITool
{
    /// <summary>Point (screenshot pixels), document and style for which overlay should show input field.</summary>
    public event Action<Point, AnnotationDocument, AnnotationStyle>? EditRequested;

    public Cursor Cursor => Cursors.IBeam;
    /// <summary>No mouse gesture: text input driven by overlay itself.</summary>
    public bool IsActive => false;

    public void Down(Point px, AnnotationDocument doc, AnnotationStyle style)
    {
        if (snap is not null)
        {
            // No mouse gesture: snap one-time, guides don't stay.
            snap.Begin(doc);
            px = snap.SnapPoint(px);
            snap.End();
        }
        EditRequested?.Invoke(px, doc, style);
    }
    public void Move(Point px) { }
    public void Up(Point px) { }
    public void Cancel() { }
}
