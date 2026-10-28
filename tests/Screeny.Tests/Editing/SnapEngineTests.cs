using System.Windows;
using System.Windows.Media;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Editing;
using Screeny.Overlay.Tools;

namespace Screeny.Tests.Editing;

public class SnapEngineTests
{
    private static readonly Rect Target = new(100, 100, 50, 40); // лево 100, центр 125, право 150; верх 100, центр 120, низ 140

    [Fact]
    public void NoTargets_NoChange()
    {
        var r = SnapEngine.Snap(new Rect(10, 10, 20, 20), [], 6);
        Assert.Equal(default, r.Offset);
        Assert.Empty(r.Guides);
    }

    [Fact]
    public void LeftToLeft_WithinThreshold_Snaps()
    {
        // Лево 104 → 100; по Y далеко от всех линий цели.
        var r = SnapEngine.Snap(new Rect(104, 300, 60, 20), [Target], 6);
        Assert.Equal(new Vector(-4, 0), r.Offset);
        var g = Assert.Single(r.Guides);
        Assert.Equal(GuideOrientation.Vertical, g.Orientation);
        Assert.Equal(100, g.Position);
        // Через общую протяжённость: от верха цели до низа объекта.
        Assert.Equal(100, g.Start);
        Assert.Equal(320, g.End);
    }

    [Fact]
    public void OutsideThreshold_NoSnap()
    {
        // Лево 107 до 100 — 7, вне порога; ближе всех правый край 127 к центру цели 125.
        var r = SnapEngine.Snap(new Rect(107, 300, 20, 20), [Target], 6);
        Assert.Equal(new Vector(-2, 0), r.Offset);
        var far = SnapEngine.Snap(new Rect(200, 300, 21, 20), [Target], 6);
        Assert.Equal(default, far.Offset);
        Assert.Empty(far.Guides);
    }

    [Fact]
    public void ExactlyAtThreshold_Snaps_JustBeyond_DoesNot()
    {
        Assert.Equal(new Vector(-6, 0), SnapEngine.Snap(new Rect(156, 300, 30, 20), [Target], 6).Offset); // лево 156 → право цели 150
        Assert.Equal(default, SnapEngine.Snap(new Rect(156.5, 300, 30, 20), [Target], 6).Offset);
    }

    [Fact]
    public void CenterToCenter_Snaps()
    {
        // Центр объекта 127 → центр цели 125; края 117 и 137 далеко от 100 и 150.
        var r = SnapEngine.Snap(new Rect(117, 300, 20, 20), [Target], 6);
        Assert.Equal(new Vector(-2, 0), r.Offset);
        Assert.Equal(125, Assert.Single(r.Guides).Position);
    }

    [Fact]
    public void NearestCandidateWins()
    {
        var a = new Rect(0, 0, 10, 10);     // правый край 10
        var b = new Rect(13, 50, 10, 10);   // левый край 13
        // Левый край объекта 12: до 10 — 2, до 13 — 1 → к 13.
        var r = SnapEngine.Snap(new Rect(12, 200, 40, 5), [a, b], 6);
        Assert.Equal(new Vector(1, 0), r.Offset);
    }

    [Fact]
    public void EquidistantTargets_FirstTargetWins()
    {
        var left = new Rect(100, 0, 0, 10);
        var right = new Rect(110, 0, 0, 10);
        var result = SnapEngine.SnapPoint(new Point(105, 100), [left, right], 5);
        Assert.Equal(new Vector(-5, 0), result.Offset);
        Assert.Equal(100, Assert.Single(result.Guides).Position);
    }

    [Fact]
    public void BothAxes_SnapIndependently()
    {
        // Право 153 → 150 (−3); верх 98 → 100 (+2).
        var r = SnapEngine.Snap(new Rect(121, 98, 32, 30), [Target], 6);
        Assert.Equal(new Vector(-3, 2), r.Offset);
        Assert.Contains(r.Guides, g => g.Orientation == GuideOrientation.Vertical && g.Position == 150);
        Assert.Contains(r.Guides, g => g.Orientation == GuideOrientation.Horizontal && g.Position == 100);
    }

    [Fact]
    public void OnlyOneAxis_InRange_OtherUnchanged()
    {
        var r = SnapEngine.Snap(new Rect(300, 103, 10, 10), [Target], 6);
        Assert.Equal(new Vector(0, -3), r.Offset);
        Assert.All(r.Guides, g => Assert.Equal(GuideOrientation.Horizontal, g.Orientation));
    }

    [Fact]
    public void Guides_SpanAllTargetsSharingTheLine()
    {
        var upper = new Rect(100, 0, 20, 20);
        var lower = new Rect(100, 500, 20, 20);
        var r = SnapEngine.Snap(new Rect(102, 200, 60, 10), [upper, lower], 6);
        var g = Assert.Single(r.Guides);
        Assert.Equal(100, g.Position);
        Assert.Equal(0, g.Start);
        Assert.Equal(520, g.End);
    }

    [Fact]
    public void Point_SnapsLikeZeroSizeRect_WithOneGuidePerAxis()
    {
        var r = SnapEngine.SnapPoint(new Point(148, 122), [Target], 6);
        Assert.Equal(new Vector(2, -2), r.Offset);
        Assert.Equal(2, r.Guides.Count);
    }

    [Fact]
    public void ZeroOrNegativeThreshold_NoSnap() => Assert.Equal(default, SnapEngine.Snap(new Rect(101, 300, 5, 5), [Target], 0).Offset);
}

public class SnapperToolTests
{
    private static readonly AnnotationStyle Style = new(Colors.Red, 3);

    private static Snapper MakeSnapper(Func<bool>? enabled = null, Rect? area = null)
        => new(enabled ?? (() => true), () => 6, () => area ?? Rect.Empty);

    [Fact]
    public void SelectTool_DragNearOtherShape_LeftEdgesAlign_AndUndoIsOneStep()
    {
        var doc = new AnnotationDocument();
        var anchor = new RectAnnotation(new Rect(100, 100, 50, 50), Style);
        var moving = new RectAnnotation(new Rect(0, 300, 30, 30), Style);
        doc.Add(anchor); doc.Add(moving);
        var snap = MakeSnapper();
        var tool = new SelectTool(snap);
        tool.Down(new Point(0, 315), doc, Style); // за левый край (фигура без заливки ловится по обводке)
        Assert.True(tool.IsActive);
        tool.Move(new Point(103, 315)); // лево 103 → 100
        Assert.Equal(100, moving.Rect.Left);
        Assert.NotEmpty(snap.Guides);
        tool.Move(new Point(104, 316)); // всё ещё в пороге: стоит на месте, без накопленной ошибки
        Assert.Equal(100, moving.Rect.Left);
        tool.Move(new Point(175, 316)); // ушли от целей: следует за мышью точно
        Assert.Equal(175, moving.Rect.Left);
        Assert.Empty(snap.Guides);
        tool.Move(new Point(104, 315));
        tool.Up(new Point(104, 315));
        Assert.Equal(new Rect(100, 300, 30, 30), moving.Rect);
        Assert.Empty(snap.Guides); // направляющие исчезают при отпускании
        doc.Undo.Undo();
        Assert.Equal(new Rect(0, 300, 30, 30), moving.Rect);
        Assert.Equal(new Rect(100, 100, 50, 50), anchor.Rect);
    }

    [Fact]
    public void SelectTool_SnapDisabled_FollowsMouseExactly()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(new Rect(100, 100, 50, 50), Style));
        var moving = new RectAnnotation(new Rect(0, 300, 40, 40), Style);
        doc.Add(moving);
        var snap = MakeSnapper(enabled: () => false);
        var tool = new SelectTool(snap);
        tool.Down(new Point(0, 320), doc, Style);
        tool.Up(new Point(103, 320));
        Assert.Equal(103, moving.Rect.Left);
        Assert.Empty(snap.Guides);
    }

    [Fact]
    public void SelectTool_Cancel_RevertsSnappedOffset()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(new Rect(100, 100, 50, 50), Style));
        var moving = new RectAnnotation(new Rect(0, 300, 30, 30), Style);
        doc.Add(moving);
        var snap = MakeSnapper();
        var tool = new SelectTool(snap);
        tool.Down(new Point(0, 315), doc, Style);
        tool.Move(new Point(103, 315));
        Assert.Equal(100, moving.Rect.Left);
        tool.Cancel();
        Assert.Equal(new Rect(0, 300, 30, 30), moving.Rect);
        Assert.Empty(snap.Guides);
    }

    [Fact]
    public void SelectTool_SnapsToArea()
    {
        var doc = new AnnotationDocument();
        var moving = new RectAnnotation(new Rect(50, 50, 20, 20), Style);
        doc.Add(moving);
        var tool = new SelectTool(MakeSnapper(area: new Rect(0, 0, 200, 100)));
        tool.Down(new Point(50, 60), doc, Style);
        tool.Up(new Point(137, 63)); // по X линий области (0, 100, 200) рядом нет; верх 53 → центр области по Y 50
        Assert.Equal(new Rect(137, 50, 20, 20), moving.Rect);
    }

    [Fact]
    public void ShapeTool_CornerSnapsToOtherShapeEdge()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(new Rect(100, 100, 50, 50), Style));
        var tool = new ShapeTool(ToolKind.Rect, () => false, MakeSnapper());
        tool.Down(new Point(10, 10), doc, Style);
        tool.Move(new Point(147, 60));
        tool.Up(new Point(147, 60)); // право 147 → 150
        var r = Assert.IsType<RectAnnotation>(doc.Items[^1]);
        Assert.Equal(new Rect(10, 10, 140, 50), r.Rect);
    }

    [Fact]
    public void ShapeTool_WithShift_DoesNotSnap()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(new Rect(100, 100, 50, 50), Style));
        var snap = MakeSnapper();
        var tool = new ShapeTool(ToolKind.Line, () => true, snap);
        tool.Down(new Point(10, 102), doc, Style);
        tool.Up(new Point(148, 102)); // Shift: горизонталь от начала, без прилипания к 150 и к 100
        var line = Assert.IsType<LineAnnotation>(doc.Items[^1]);
        Assert.Equal(new Point(10, 102), line.Start);
        Assert.Equal(148, line.End.X, 6);
        Assert.Equal(102, line.End.Y, 6);
        Assert.Empty(snap.Guides);
    }

    [Fact]
    public void ArrowEnds_Snap()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(new Rect(100, 100, 50, 50), Style));
        var tool = new ShapeTool(ToolKind.Arrow, () => false, MakeSnapper());
        tool.Down(new Point(98, 300), doc, Style); // X 98 → 100
        tool.Up(new Point(126, 123));              // → центр (125, 125)
        var arrow = Assert.IsType<ArrowAnnotation>(doc.Items[^1]);
        Assert.Equal(new Point(100, 300), arrow.Start);
        Assert.Equal(new Point(125, 125), arrow.End);
    }

    [Fact]
    public void NumberTool_CenterSnaps_AndFollowsMouseWhilePressed()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(new Rect(100, 100, 50, 50), Style));
        var tool = new NumberTool(MakeSnapper());
        tool.Down(new Point(400, 400), doc, Style);
        tool.Move(new Point(127, 300));
        tool.Up(new Point(127, 300)); // центр 127 → 125
        var n = Assert.IsType<NumberAnnotation>(doc.Items[^1]);
        Assert.Equal(new Point(125, 300), n.Center);
    }

    [Fact]
    public void TextTool_ClickPointSnaps()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(new Rect(100, 100, 50, 50), Style));
        var tool = new TextTool(MakeSnapper());
        Point? at = null;
        tool.EditRequested += (p, _, _) => at = p;
        tool.Down(new Point(103, 400), doc, Style);
        Assert.Equal(new Point(100, 400), at);
    }
}
