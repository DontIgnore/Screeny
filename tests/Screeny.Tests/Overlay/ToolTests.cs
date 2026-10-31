using System.Windows;
using System.Windows.Media;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Overlay.Tools;

namespace Screeny.Tests.Overlay;

public class ToolTests
{
    private static readonly AnnotationStyle Style = new(Colors.Red, 3);

    [Fact]
    public void ShapeTool_Drag_CreatesOneUndoStep()
    {
        var doc = new AnnotationDocument();
        var tool = new ShapeTool(ToolKind.Rect);
        tool.Down(new Point(10, 10), doc, Style);
        tool.Move(new Point(30, 20));
        tool.Up(new Point(50, 40));
        var rect = Assert.IsType<RectAnnotation>(Assert.Single(doc.Items));
        Assert.Equal(new Rect(10, 10, 40, 30), rect.Rect);
        doc.Undo.Undo();
        Assert.Empty(doc.Items);
        Assert.False(doc.Undo.CanUndo);
    }

    [Fact]
    public void ShapeTool_ClickWithoutDrag_LeavesNothing()
    {
        var doc = new AnnotationDocument();
        var tool = new ShapeTool(ToolKind.Arrow);
        tool.Down(new Point(10, 10), doc, Style);
        tool.Up(new Point(10, 11));
        Assert.Empty(doc.Items);
        Assert.False(doc.Undo.CanUndo);
        Assert.False(doc.Undo.CanRedo);
    }

    [Fact]
    public void ArrowTool_EndsAtReleasePoint()
    {
        var doc = new AnnotationDocument();
        var tool = new ShapeTool(ToolKind.Arrow);
        tool.Down(new Point(0, 0), doc, Style);
        tool.Up(new Point(80, 60));
        var arrow = Assert.IsType<ArrowAnnotation>(Assert.Single(doc.Items));
        Assert.Equal(new Point(80, 60), arrow.End);
    }

    [Fact]
    public void PenTool_SkipsPointsCloserThanStep()
    {
        var doc = new AnnotationDocument();
        var tool = new PenTool(highlighter: false);
        tool.Down(new Point(0, 0), doc, Style);
        tool.Move(new Point(0.5, 0));
        tool.Move(new Point(5, 0));
        tool.Up(new Point(10, 0));
        var pen = Assert.IsType<PenAnnotation>(Assert.Single(doc.Items));
        Assert.Equal(3, pen.Points.Count);
    }

    [Fact]
    public void SelectTool_Drag_MovesAsSingleUndoStep()
    {
        var doc = new AnnotationDocument();
        var rect = new RectAnnotation(new Rect(0, 0, 50, 50), Style);
        doc.Add(rect);
        var tool = new SelectTool();
        tool.Down(new Point(0, 25), doc, Style); // on border
        Assert.Same(rect, doc.Selected);
        tool.Move(new Point(5, 30));
        tool.Move(new Point(10, 35));
        tool.Up(new Point(10, 35));
        Assert.Equal(new Rect(10, 10, 50, 50), rect.Rect);
        doc.Undo.Undo();
        Assert.Equal(new Rect(0, 0, 50, 50), rect.Rect);
        doc.Undo.Undo();
        Assert.Empty(doc.Items);
    }

    [Fact]
    public void SelectTool_ClickOnEmpty_ClearsSelection()
    {
        var doc = new AnnotationDocument();
        var rect = new RectAnnotation(new Rect(0, 0, 50, 50), Style);
        doc.Add(rect);
        doc.Selected = rect;
        new SelectTool().Down(new Point(300, 300), doc, Style);
        Assert.Null(doc.Selected);
    }

    [Fact]
    public void ShapeTool_DuringDrag_ShapeIsLiveNotInItems()
    {
        var doc = new AnnotationDocument();
        var tool = new ShapeTool(ToolKind.Rect);
        tool.Down(new Point(10, 10), doc, Style);
        tool.Move(new Point(40, 40));
        Assert.True(tool.IsActive);
        Assert.Empty(doc.Items);
        Assert.IsType<RectAnnotation>(doc.Live);
        Assert.False(doc.Undo.CanUndo);
        tool.Up(new Point(40, 40));
        Assert.False(tool.IsActive);
        Assert.Null(doc.Live);
        Assert.Single(doc.Items);
    }

    [Fact]
    public void ShapeTool_ClickWithoutDrag_DoesNotClearRedo()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(new Rect(0, 0, 10, 10), Style));
        doc.Undo.Undo();
        var tool = new ShapeTool(ToolKind.Rect);
        tool.Down(new Point(50, 50), doc, Style);
        tool.Up(new Point(50, 51));
        Assert.True(doc.Undo.CanRedo);
        Assert.Empty(doc.Items);
        Assert.Null(doc.Live);
    }

    [Fact]
    public void ShapeTool_UndoDuringDrag_DoesNotRemovePreviousShape()
    {
        var doc = new AnnotationDocument();
        var a = new RectAnnotation(new Rect(0, 0, 10, 10), Style);
        doc.Add(a);
        var tool = new ShapeTool(ToolKind.Rect);
        tool.Down(new Point(50, 50), doc, Style);
        doc.Undo.Undo();               // Ctrl+Z with button held removes A
        Assert.Empty(doc.Items);
        tool.Up(new Point(51, 50));    // mini-move: B discarded
        Assert.Empty(doc.Items);
        Assert.True(doc.Undo.CanRedo);
        doc.Undo.Redo();
        Assert.Same(a, Assert.Single(doc.Items));
    }

    [Fact]
    public void ShapeTool_Cancel_DropsLiveShape()
    {
        var doc = new AnnotationDocument();
        var tool = new ShapeTool(ToolKind.Ellipse);
        tool.Down(new Point(0, 0), doc, Style);
        tool.Move(new Point(50, 50));
        tool.Cancel();
        Assert.False(tool.IsActive);
        Assert.Null(doc.Live);
        Assert.Empty(doc.Items);
        tool.Up(new Point(60, 60)); // late Up after undo adds nothing
        Assert.Empty(doc.Items);
    }

    [Fact]
    public void ShapeTool_HorizontalLine_IsKept()
    {
        var doc = new AnnotationDocument();
        var tool = new ShapeTool(ToolKind.Line);
        tool.Down(new Point(0, 10), doc, Style);
        tool.Up(new Point(40, 10));
        Assert.IsType<LineAnnotation>(Assert.Single(doc.Items));
    }

    [Fact]
    public void ShapeTool_ShiftStrictlyVertical_MakesSquare()
    {
        var doc = new AnnotationDocument();
        var tool = new ShapeTool(ToolKind.Rect, () => true);
        tool.Down(new Point(10, 10), doc, Style);
        tool.Up(new Point(10, 40));
        var rect = Assert.IsType<RectAnnotation>(Assert.Single(doc.Items));
        Assert.Equal(new Rect(10, 10, 30, 30), rect.Rect);
    }

    [Fact]
    public void PenTool_Cancel_DropsStroke()
    {
        var doc = new AnnotationDocument();
        var tool = new PenTool(highlighter: false);
        tool.Down(new Point(0, 0), doc, Style);
        tool.Move(new Point(20, 0));
        Assert.Empty(doc.Items);
        Assert.NotNull(doc.Live);
        tool.Cancel();
        Assert.Null(doc.Live);
        Assert.Empty(doc.Items);
    }

    [Fact]
    public void NumberTool_AddsOnUp_AsOneUndoStep()
    {
        var doc = new AnnotationDocument();
        var tool = new NumberTool();
        tool.Down(new Point(5, 5), doc, Style);
        Assert.Empty(doc.Items);
        Assert.True(tool.IsActive);
        tool.Up(new Point(5, 5));
        var n = Assert.IsType<NumberAnnotation>(Assert.Single(doc.Items));
        Assert.Equal(1, n.Number);
        doc.Undo.Undo();
        Assert.Empty(doc.Items);
    }

    [Fact]
    public void SelectTool_UndoDuringDrag_DoesNotRecordMoveForRemovedShape()
    {
        var doc = new AnnotationDocument();
        var rect = new RectAnnotation(new Rect(0, 0, 50, 50), Style);
        doc.Add(rect);
        var tool = new SelectTool();
        tool.Down(new Point(0, 25), doc, Style);
        tool.Move(new Point(10, 35));
        doc.Undo.Undo();               // shape deleted mid-drag
        tool.Up(new Point(20, 45));
        Assert.Empty(doc.Items);
        Assert.False(doc.Undo.CanUndo);
        Assert.True(doc.Undo.CanRedo);
        doc.Undo.Redo();
        Assert.Same(rect, Assert.Single(doc.Items));
        Assert.Equal(new Rect(0, 0, 50, 50), rect.Rect); // returned to original place
    }

    [Fact]
    public void SelectTool_Cancel_RevertsOffset()
    {
        var doc = new AnnotationDocument();
        var rect = new RectAnnotation(new Rect(0, 0, 50, 50), Style);
        doc.Add(rect);
        var tool = new SelectTool();
        tool.Down(new Point(0, 25), doc, Style);
        tool.Move(new Point(10, 35));
        Assert.True(tool.IsActive);
        tool.Cancel();
        Assert.False(tool.IsActive);
        Assert.Equal(new Rect(0, 0, 50, 50), rect.Rect);
        tool.Up(new Point(30, 30));
        Assert.Equal(new Rect(0, 0, 50, 50), rect.Rect);
        doc.Undo.Undo(); // single step — addition
        Assert.Empty(doc.Items);
        Assert.False(doc.Undo.CanUndo);
    }

    [Fact]
    public void SelectTool_ClickWithoutMove_AddsNoUndoStep()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(new Rect(0, 0, 50, 50), Style));
        var tool = new SelectTool();
        tool.Down(new Point(0, 25), doc, Style);
        tool.Up(new Point(0, 25));
        Assert.False(tool.IsActive);
        doc.Undo.Undo();
        Assert.False(doc.Undo.CanUndo);
    }
}
