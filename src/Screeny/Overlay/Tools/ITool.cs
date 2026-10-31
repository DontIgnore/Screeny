using System.Windows;
using System.Windows.Input;
using Screeny.Annotations;

namespace Screeny.Overlay.Tools;

/// <summary>Editor tool. All points in screenshot pixels, already limited to selection.</summary>
public interface ITool
{
    void Down(Point px, AnnotationDocument doc, AnnotationStyle style);
    void Move(Point px);
    void Up(Point px);
    /// <summary>Aborts gesture without trace: live shape discarded, offset rolled back. Without active gesture does nothing.</summary>
    void Cancel();
    /// <summary>Whether gesture running (between Down and Up/Cancel).</summary>
    bool IsActive { get; }
    Cursor Cursor { get; }
}
