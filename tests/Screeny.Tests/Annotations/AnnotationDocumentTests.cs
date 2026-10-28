using System.Windows;
using System.Windows.Media;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;

namespace Screeny.Tests.Annotations;

public class AnnotationDocumentTests
{
    private static AnnotationStyle Style => new(Colors.Red, 3);

    [Fact]
    public void Add_Undo_Redo()
    {
        var doc = new AnnotationDocument();
        var a = new RectAnnotation(new Rect(0, 0, 10, 10), Style);
        doc.Add(a);
        Assert.Single(doc.Items);
        doc.Undo.Undo(); Assert.Empty(doc.Items);
        doc.Undo.Redo(); Assert.Single(doc.Items);
    }

    [Fact]
    public void HitTest_ReturnsTopmost()
    {
        // Прямоугольник ловит клик только по рамке, поэтому точка лежит на рамках обоих.
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(new Rect(0, 0, 100, 100), Style));
        var top = new RectAnnotation(new Rect(0, 0, 20, 20), Style);
        doc.Add(top);
        Assert.Same(top, doc.HitTest(new Point(1, 10)));
        Assert.Null(doc.HitTest(new Point(500, 500)));
    }

    [Fact]
    public void RectHitTest_IgnoresInterior()
    {
        var a = new RectAnnotation(new Rect(0, 0, 100, 100), Style);
        Assert.True(a.HitTest(new Point(0, 50)));
        Assert.False(a.HitTest(new Point(50, 50)));
    }

    [Fact]
    public void Move_IsUndoable()
    {
        var doc = new AnnotationDocument();
        var a = new RectAnnotation(new Rect(0, 0, 10, 10), Style);
        doc.Add(a);
        doc.Move(a, new Vector(5, 7));
        Assert.Equal(new Rect(5, 7, 10, 10), a.GeometryBounds);
        doc.Undo.Undo();
        Assert.Equal(new Rect(0, 0, 10, 10), a.GeometryBounds);
    }

    [Fact]
    public void Remove_IsUndoable()
    {
        var doc = new AnnotationDocument();
        var a = new RectAnnotation(new Rect(0, 0, 10, 10), Style);
        doc.Add(a); doc.Remove(a);
        Assert.Empty(doc.Items);
        doc.Undo.Undo(); Assert.Single(doc.Items);
    }

    [Fact]
    public void Render_DrawsEveryShapeWithoutThrowing()
    {
        var px = new byte[64 * 64 * 4]; Array.Fill(px, (byte)128);
        var source = System.Windows.Media.Imaging.BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, px, 64 * 4);
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(new Rect(1, 1, 20, 20), Style));
        doc.Add(new EllipseAnnotation(new Rect(1, 1, 20, 10), Style));
        doc.Add(new LineAnnotation(new Point(0, 0), new Point(30, 30), Style));
        doc.Add(new ArrowAnnotation(new Point(0, 0), new Point(30, 10), Style));
        var pen = new PenAnnotation(new Point(2, 2), Style); pen.AddPoint(new Point(10, 12)); doc.Add(pen);
        var hl = new HighlighterAnnotation(new Point(2, 2), Style); hl.AddPoint(new Point(20, 2)); doc.Add(hl);
        doc.Add(new TextAnnotation(new Point(5, 5), "Hi", Style));
        doc.Add(new NumberAnnotation(new Point(30, 30), 1, Style));
        doc.Add(new PixelateAnnotation(new Rect(10, 10, 30, 30), Style));
        doc.Add(new BlurAnnotation(new Rect(50, 50, 40, 40), Style)); // частично за краем снимка
        doc.Add(new PixelateAnnotation(new Rect(10, 10, 0, 0), Style)); // нулевой размер (момент MouseDown)

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) doc.Render(dc, new RenderContext(source, ForExport: true));
        Assert.False(dv.ContentBounds.IsEmpty);
    }
}

public class LiveAnnotationTests
{
    private static AnnotationStyle Style => new(Colors.Red, 3);

    [Fact]
    public void Document_LiveAnnotation_RenderedButNotInItems()
    {
        var px = new byte[64 * 64 * 4]; Array.Fill(px, (byte)128);
        var source = System.Windows.Media.Imaging.BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, px, 64 * 4);
        var doc = new AnnotationDocument();
        int changed = 0; doc.Changed += () => changed++;
        var live = new RectAnnotation(new Rect(10, 10, 20, 20), Style);
        doc.Live = live;
        Assert.Equal(1, changed);
        Assert.Empty(doc.Items);
        Assert.False(doc.Undo.CanUndo);
        Assert.Null(doc.HitTest(new Point(10, 20))); // живая фигура не ловится выбором

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) doc.Render(dc, new RenderContext(source, ForExport: false));
        Assert.False(dv.ContentBounds.IsEmpty);

        doc.Live = null;
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Document_LiveNumber_DoesNotAffectNextNumber()
    {
        var doc = new AnnotationDocument();
        doc.Live = new NumberAnnotation(new Point(0, 0), 1, Style);
        Assert.Equal(1, doc.NextNumber());
    }

    [Fact]
    public void RecordMove_ForMissingAnnotation_ReturnsFalse()
    {
        var doc = new AnnotationDocument();
        var a = new RectAnnotation(new Rect(0, 0, 10, 10), Style);
        Assert.False(doc.RecordMove(a, new Vector(5, 5)));
        Assert.False(doc.Undo.CanUndo);
    }

    [Fact]
    public void RecordMove_RecordsAlreadyAppliedOffset()
    {
        var doc = new AnnotationDocument();
        var a = new RectAnnotation(new Rect(0, 0, 10, 10), Style);
        doc.Add(a);
        a.Offset(new Vector(5, 5));
        Assert.True(doc.RecordMove(a, new Vector(5, 5)));
        Assert.Equal(new Rect(5, 5, 10, 10), a.GeometryBounds); // не сдвинуто второй раз
        doc.Undo.Undo();
        Assert.Equal(new Rect(0, 0, 10, 10), a.GeometryBounds);
    }

    [Fact]
    public void Restyle_IsUndoable()
    {
        var doc = new AnnotationDocument();
        var a = new RectAnnotation(new Rect(0, 0, 10, 10), Style);
        doc.Add(a);
        var blue = new AnnotationStyle(Colors.Blue, 3);
        doc.Restyle(a, blue);
        Assert.Equal(blue, a.Style);
        doc.Undo.Undo();
        Assert.Equal(Style, a.Style);
        doc.Undo.Redo();
        Assert.Equal(blue, a.Style);
    }

    [Fact]
    public void Restyle_SameStyle_AddsNoStep()
    {
        var doc = new AnnotationDocument();
        var a = new RectAnnotation(new Rect(0, 0, 10, 10), Style);
        doc.Add(a);
        doc.Restyle(a, Style);
        doc.Undo.Undo();
        Assert.False(doc.Undo.CanUndo);
    }

    [Fact]
    public void Clear_ResetsLive()
    {
        var doc = new AnnotationDocument();
        doc.Live = new RectAnnotation(new Rect(0, 0, 10, 10), Style);
        doc.Clear();
        Assert.Null(doc.Live);
    }
}

public class ArrowGeometryTests
{
    [Fact]
    public void HeadPoints_FormTriangleAtEnd()
    {
        var (left, right) = ArrowAnnotation.HeadWings(new Point(0, 0), new Point(100, 0), headLength: 10, headWidth: 8);
        Assert.Equal(90, left.X, 3); Assert.Equal(-4, left.Y, 3);
        Assert.Equal(90, right.X, 3); Assert.Equal(4, right.Y, 3);
    }
}

public class NumberAnnotationTests
{
    [Fact]
    public void Document_AssignsSequentialNumbers()
    {
        var doc = new AnnotationDocument();
        var s = new AnnotationStyle(Colors.Red, 3);
        doc.Add(new NumberAnnotation(new Point(0, 0), doc.NextNumber(), s));
        doc.Add(new NumberAnnotation(new Point(0, 0), doc.NextNumber(), s));
        Assert.Equal(1, ((NumberAnnotation)doc.Items[0]).Number);
        Assert.Equal(2, ((NumberAnnotation)doc.Items[1]).Number);
        doc.Undo.Undo();
        Assert.Equal(2, doc.NextNumber()); // после отмены номер переиспользуется
    }
}

public class EffectAnnotationTests
{
    [Fact]
    public void Pixelate_ChangesPixelsInsideRegionOnly()
    {
        int w = 40, h = 20;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        { int i = (y * w + x) * 4; byte v = (byte)((x + y) % 2 == 0 ? 0 : 255); px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
        var source = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);

        var effect = new PixelateAnnotation(new Rect(0, 0, 20, 20), new AnnotationStyle(Colors.Red, 3));
        var processed = effect.Process(source);
        Assert.NotNull(processed);
        Assert.Equal(20, processed!.PixelWidth);
        var outPx = new byte[20 * 20 * 4];
        processed.CopyPixels(outPx, 20 * 4, 0);
        // Блок 9×9 шахматки усредняется в серый, а не остаётся 0/255.
        Assert.InRange(outPx[0], 100, 155);
    }
}
