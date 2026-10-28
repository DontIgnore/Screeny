using System.Windows;
using System.Windows.Input;
using Screeny.Annotations;
using Screeny.Editing;

namespace Screeny.Overlay.Tools;

/// <summary>
/// Клик открывает поле ввода в оверлее; аннотация добавляется, когда ввод подтверждён. С <paramref name="snap"/>
/// левый верхний угол текста прилипает к краям и центрам других фигур и области.
/// </summary>
public sealed class TextTool(Snapper? snap = null) : ITool
{
    /// <summary>Точка (пиксели снимка), документ и стиль, для которых оверлей должен показать поле ввода.</summary>
    public event Action<Point, AnnotationDocument, AnnotationStyle>? EditRequested;

    public Cursor Cursor => Cursors.IBeam;
    /// <summary>Жеста мыши нет: ввод текста ведёт сам оверлей.</summary>
    public bool IsActive => false;

    public void Down(Point px, AnnotationDocument doc, AnnotationStyle style)
    {
        if (snap is not null)
        {
            // Жеста мыши нет: прилипание разовое, направляющие не остаются.
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
