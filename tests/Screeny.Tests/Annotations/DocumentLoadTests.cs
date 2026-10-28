using System.Windows;
using System.Windows.Media;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;

namespace Screeny.Tests.Annotations;

public class DocumentLoadTests
{
    private static readonly AnnotationStyle S = new(Colors.Red, 3);

    [Fact]
    public void Load_AddsWithoutUndoSteps_AndNotifies()
    {
        var doc = new AnnotationDocument();
        int changes = 0;
        doc.Changed += () => changes++;
        doc.Load([new RectAnnotation(new Rect(0, 0, 5, 5), S), new EllipseAnnotation(new Rect(1, 1, 5, 5), S)]);
        Assert.Equal(2, doc.Items.Count);
        Assert.False(doc.Undo.CanUndo);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void RemoveAll_IsOneUndoStep_RestoringOrder()
    {
        var doc = new AnnotationDocument();
        var a = new RectAnnotation(new Rect(0, 0, 5, 5), S);
        var b = new LineAnnotation(new Point(0, 0), new Point(9, 9), S);
        var c = new NumberAnnotation(new Point(3, 3), 1, S);
        doc.Load([a, b]);
        doc.Add(c);
        doc.Selected = b;

        doc.RemoveAll();
        Assert.Empty(doc.Items);
        Assert.Null(doc.Selected);

        doc.Undo.Undo();
        Assert.Equal([a, b, c], doc.Items);
        doc.Undo.Redo();
        Assert.Empty(doc.Items);
        doc.Undo.Undo();
        doc.Undo.Undo(); // шаг добавления метки
        Assert.Equal([a, b], doc.Items);
    }

    [Fact]
    public void RemoveAll_OnEmptyDocument_AddsNoStep()
    {
        var doc = new AnnotationDocument();
        doc.RemoveAll();
        Assert.False(doc.Undo.CanUndo);
    }
}
