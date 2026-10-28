using System.Windows;
using System.Windows.Input;
using Screeny.Annotations;

namespace Screeny.Overlay.Tools;

/// <summary>Инструмент редактора. Все точки — в пикселях снимка, уже ограниченные выделением.</summary>
public interface ITool
{
    void Down(Point px, AnnotationDocument doc, AnnotationStyle style);
    void Move(Point px);
    void Up(Point px);
    /// <summary>Прерывает жест без следа: живая фигура отбрасывается, сдвиг откатывается. Без активного жеста ничего не делает.</summary>
    void Cancel();
    /// <summary>Идёт ли жест (между Down и Up/Cancel).</summary>
    bool IsActive { get; }
    Cursor Cursor { get; }
}
