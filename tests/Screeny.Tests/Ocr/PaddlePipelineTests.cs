using System.Windows;
using Screeny.Ocr.Paddle;

namespace Screeny.Tests.Ocr;

/// <summary>Пре- и постобработка PaddleOCR без моделей: синтетические вероятности, карты и картинки.</summary>
public class PaddlePipelineTests
{
    private static readonly string[] Dict = ["a", "b", "c"]; // классы: 0 — blank, 1..3 — a,b,c, 4 — пробел

    /// <summary>Вероятности CTC: на каждом шаге один класс с вероятностью <paramref name="p"/>, остальное поровну.</summary>
    private static float[] Probs(int[] classes, int C, float p = 0.9f)
    {
        var data = new float[classes.Length * C];
        for (int t = 0; t < classes.Length; t++)
            for (int c = 0; c < C; c++) data[t * C + c] = c == classes[t] ? p : (1 - p) / (C - 1);
        return data;
    }

    [Fact]
    public void Decode_CollapsesRepeatsAndDropsBlanks()
    {
        var r = PaddlePipeline.Decode(Probs([1, 1, 0, 1, 2, 2, 0, 0, 3], 5), 9, 5, Dict, 1);
        Assert.Equal("aabc", r.Text);
    }

    [Fact]
    public void Decode_ClassAfterDictionary_IsSpace()
    {
        var r = PaddlePipeline.Decode(Probs([1, 4, 2], 5), 3, 5, Dict, 1);
        Assert.Equal("a b", r.Text);
    }

    [Fact]
    public void Decode_CharBoundsFollowCtcSteps()
    {
        var r = PaddlePipeline.Decode(Probs([0, 1, 1, 0, 2], 5), 5, 5, Dict, stepPx: 4);
        Assert.Equal("ab", r.Text);
        Assert.Equal([4.0, 16.0], r.CharLeft);   // шаг 1 и шаг 4
        Assert.Equal([8.0, 20.0], r.CharRight);
    }

    [Fact]
    public void Decode_ConfidenceIsMeanOfEmittedSteps()
    {
        var data = Probs([1, 2], 5, 0.8f);
        data[5 + 2] = 0.6f; // второй шаг: класс 2 с 0,6
        var r = PaddlePipeline.Decode(data, 2, 5, Dict, 1);
        Assert.Equal(0.7f, r.Confidence, 3);
    }

    [Fact]
    public void Decode_AllBlank_IsEmptyWithZeroConfidence()
    {
        var r = PaddlePipeline.Decode(Probs([0, 0, 0], 5), 3, 5, Dict, 1);
        Assert.Equal("", r.Text);
        Assert.Equal(0, r.Confidence);
    }

    [Fact]
    public void ParseDict_DropsTrailingEmptyLine()
        => Assert.Equal(["a", "b", " "], PaddlePipeline.ParseDict("a\nb\n \n"));

    /// <summary>Белая картинка с чёрными «словами» — столбцы [x0, x1) по всей высоте строки.</summary>
    private static OcrImage Line(int w, int h, params (int X0, int X1)[] words)
    {
        var px = new byte[w * h * 4];
        Array.Fill(px, (byte)255);
        foreach (var (x0, x1) in words)
            for (int y = 2; y < h - 2; y++)
                for (int x = x0; x < x1; x++)
                {
                    int o = (y * w + x) * 4;
                    px[o] = px[o + 1] = px[o + 2] = 0;
                }
        return new OcrImage(px, w, h);
    }

    [Fact]
    public void Split_ShortLine_IsNotCut()
    {
        var img = Line(200, 48, (10, 190));
        var parts = PaddlePipeline.Split(img, new Int32Rect(0, 0, 200, 48), PaddlePipeline.MaxRecWidth);
        Assert.Equal([(new Int32Rect(0, 0, 200, 48), false)], parts);
    }

    [Fact]
    public void Split_LongLine_CutsInGapsBetweenWords()
    {
        // Высота 48: предел 480 px. Слова по 150 px с промежутками по 30 px (больше четверти высоты).
        var words = Enumerable.Range(0, 8).Select(i => (10 + i * 180, 160 + i * 180)).ToArray();
        var img = Line(1450, 48, words);
        var parts = PaddlePipeline.Split(img, new Int32Rect(0, 0, 1450, 48), PaddlePipeline.MaxRecWidth);
        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.True(p.Box.Width <= 480));
        Assert.Equal(1450, parts.Sum(p => p.Box.Width)); // куски без пропусков и наложений
        Assert.False(parts[0].SpaceBefore);
        foreach (var (box, space) in parts.Skip(1))
        {
            Assert.True(space); // рез по промежутку — пробел перед куском
            // Рез — посреди промежутка, не по слову.
            Assert.DoesNotContain(words, w => box.X > w.Item1 && box.X < w.Item2);
        }
    }

    [Fact]
    public void Split_NoGap_CutsAnywhereWithoutSpace()
    {
        var img = Line(1200, 48, (0, 1200));
        var parts = PaddlePipeline.Split(img, new Int32Rect(0, 0, 1200, 48), PaddlePipeline.MaxRecWidth);
        Assert.Equal([480, 480, 240], parts.Select(p => p.Box.Width));
        Assert.All(parts, p => Assert.False(p.SpaceBefore));
    }

    [Fact]
    public void Split_ScalesLimitWithLineHeight()
    {
        // Строка высотой 24: предел 480 при высоте 48 — это 240 px исходной картинки.
        var img = Line(400, 24, (0, 400));
        var parts = PaddlePipeline.Split(img, new Int32Rect(0, 0, 400, 24), PaddlePipeline.MaxRecWidth);
        Assert.Equal([240, 160], parts.Select(p => p.Box.Width));
    }

    [Fact]
    public void Join_AddsSpaceAtGapCutAndShiftsCharBounds()
    {
        var crop = new Int32Rect(100, 0, 300, 20);
        var a = new RecResult("ab", 0.9f, [0, 10], [5, 15]);
        var b = new RecResult("cd", 0.5f, [0, 10], [5, 15]);
        var r = PaddlePipeline.Join(crop, [(new Int32Rect(100, 0, 150, 20), false, a), (new Int32Rect(250, 0, 150, 20), true, b)]);
        Assert.Equal("ab cd", r.Text);
        Assert.Equal([0, 10, 150, 150, 160], r.CharLeft);
        Assert.Equal([5, 15, 150, 155, 165], r.CharRight);
        Assert.Equal(0.7f, r.Confidence, 3);
    }

    [Fact]
    public void Join_CutWithoutGap_GluesWithoutSpace()
    {
        var crop = new Int32Rect(0, 0, 200, 20);
        var r = PaddlePipeline.Join(crop, [(new Int32Rect(0, 0, 100, 20), false, new RecResult("ab", 1, [0, 5], [5, 10])),
            (new Int32Rect(100, 0, 100, 20), false, new RecResult("cd", 1, [0, 5], [5, 10]))]);
        Assert.Equal("abcd", r.Text);
    }

    [Theory]
    [InlineData("•", 0.3f, false)]       // иконка, принятая за текст
    [InlineData("8 ел", 0.59f, false)]
    [InlineData("8 ел", 0.6f, true)]
    [InlineData("Ник", 0.95f, true)]
    [InlineData("длинный текст", 0.2f, true)] // порог только для коротких
    public void Keep_DropsOnlyShortUnsureFragments(string text, float conf, bool keep)
        => Assert.Equal(keep, PaddlePipeline.Keep(new RecResult(text, conf, [], [])));

    [Fact]
    public void Boxes_FindsComponentAndUnclips()
    {
        // Карта 40×20, «строка» 20×6 с вероятностью 0,9; картинка того же размера.
        int mw = 40, mh = 20;
        var prob = new float[mw * mh];
        for (int y = 7; y < 13; y++) for (int x = 10; x < 30; x++) prob[y * mw + x] = 0.9f;
        var box = Assert.Single(PaddlePipeline.Boxes(prob, mw, mh, mw, mh));
        // w=19, h=5 (по крайним пикселям), d = 19·5·1,5 / (2·24) ≈ 2,97 → рамка шире компоненты на ≈3 px с каждой стороны.
        Assert.Equal(new Int32Rect(7, 4, 25, 11), box);
    }

    [Fact]
    public void Boxes_ScalesToImageAndSkipsWeakAndTiny()
    {
        int mw = 64, mh = 32;
        var prob = new float[mw * mh];
        for (int y = 10; y < 20; y++) for (int x = 4; x < 28; x++) prob[y * mw + x] = 0.95f;   // строка
        for (int y = 10; y < 20; y++) for (int x = 36; x < 60; x++) prob[y * mw + x] = 0.4f;   // выше порога 0,3, но оценка < 0,6
        prob[2 * mw + 50] = 0.99f;                                                                // точка-шум
        var boxes = PaddlePipeline.Boxes(prob, mw, mh, mw * 2, mh * 2);
        var box = Assert.Single(boxes);
        Assert.True(box.X < 8 && box.X + box.Width > 54); // координаты картинки ×2
        Assert.True(box.Y < 20 && box.Y + box.Height > 38);
    }

    [Fact]
    public void Boxes_SeparatesComponents()
    {
        int mw = 60, mh = 30;
        var prob = new float[mw * mh];
        for (int y = 4; y < 10; y++) for (int x = 5; x < 50; x++) prob[y * mw + x] = 0.9f;
        for (int y = 18; y < 24; y++) for (int x = 5; x < 50; x++) prob[y * mw + x] = 0.9f;
        var boxes = PaddlePipeline.Boxes(prob, mw, mh, mw, mh);
        Assert.Equal(2, boxes.Count);
        Assert.True(boxes[0].Y < boxes[1].Y);
    }

    [Fact]
    public void ToLayout_SplitsWordsByCtcBoundsAndGroupsRows()
    {
        var line1a = (new Int32Rect(10, 10, 100, 20), new RecResult("ab cd", 0.9f, [0, 10, 30, 40, 50], [8, 18, 38, 48, 58]));
        var line1b = (new Int32Rect(200, 12, 50, 20), new RecResult("ef", 0.9f, [0, 10], [8, 18]));
        var line2 = (new Int32Rect(10, 50, 100, 20), new RecResult("gh", 0.9f, [0, 10], [8, 18]));
        var layout = PaddlePipeline.ToLayout([line2, line1b, line1a], k: 0.5);
        Assert.Equal(2, layout.Lines.Count);
        Assert.Equal("ab cd ef", layout.Lines[0].Text);
        Assert.Equal("gh", layout.Lines[1].Text);
        // «cd»: от левой границы «c» (40) до правой «d» (58), в координатах ×0,5.
        Assert.Equal(new Rect((10 + 40) * 0.5, 5, 18 * 0.5, 10), layout.Lines[0].Words[1].Box);
    }

    /// <summary>Белая картинка 100×<paramref name="h"/>, чёрные строки пикселей <paramref name="rows"/> по ширине 10..90.</summary>
    private static OcrImage Rows(int h, params int[] rows)
    {
        var img = Line(100, h);
        foreach (var y in rows)
            for (int x = 10; x < 90; x++) { int o = (y * 100 + x) * 4; img.Bgra[o] = img.Bgra[o + 1] = img.Bgra[o + 2] = 0; }
        return img;
    }

    [Fact]
    public void InkRows_TightensToTextWithSmallPad()
    {
        var img = Rows(32, Enumerable.Range(10, 10).ToArray()); // текст в строках 10..19
        Assert.Equal((9, 12), PaddlePipeline.InkRows(img, new Int32Rect(0, 0, 100, 32)));
    }

    [Fact]
    public void InkRows_IgnoresNeighbourTailsAtBoxEdge()
    {
        // Строки 1..2 — хвосты букв строки выше, отделены 7 пустыми строками (больше восьмой части высоты).
        var img = Rows(32, [1, 2, .. Enumerable.Range(10, 10)]);
        Assert.Equal((9, 12), PaddlePipeline.InkRows(img, new Int32Rect(0, 0, 100, 32)));
    }

    [Fact]
    public void InkRows_KeepsDotsAboveLetters()
    {
        var img = Rows(32, [8, .. Enumerable.Range(11, 9)]); // точка над «і» через 2 пустые строки
        Assert.Equal((7, 14), PaddlePipeline.InkRows(img, new Int32Rect(0, 0, 100, 32)));
    }

    [Fact]
    public void InkRows_NoInk_KeepsBox()
        => Assert.Equal((4, 20), PaddlePipeline.InkRows(Rows(32), new Int32Rect(0, 4, 100, 20)));

    [Fact]
    public void ToLayout_UsesRowsForWordHeightOnly()
    {
        var item = (new Int32Rect(10, 10, 100, 30), new RecResult("ab", 0.9f, [0, 10], [8, 18]));
        var layout = PaddlePipeline.ToLayout([item], 1, _ => (18, 12));
        Assert.Equal(new Rect(10, 18, 18, 12), layout.Word(0).Box);
    }

    [Fact]
    public void ToLayout_SkipsBlankFragments()
        => Assert.True(PaddlePipeline.ToLayout([(new Int32Rect(0, 0, 10, 10), new RecResult("  ", 1, [0, 1], [1, 2]))], 1).IsEmpty);

    [Theory]
    [InlineData(24, 4, 4)]
    [InlineData(16, 3, 3)]
    [InlineData(8, 2, 2)]
    [InlineData(4, 1, 2)]
    [InlineData(2, 1, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(64, 4, 4)]
    public void ThreadSplit_ScalesWithLogicalCores(int cores, int par, int threads)
        => Assert.Equal((par, threads), PaddlePipeline.ThreadSplit(cores));

    [Theory]
    [InlineData(800, 400, 800, 384)]     // стороны кратны 32 (12,5 → 12: округление к чётному)
    [InlineData(40, 20, 128, 64)]        // короткая сторона не меньше 64
    [InlineData(8000, 1000, 4000, 512)]  // длинная не больше 4000
    public void DetInputSize_Rounds(int w, int h, int ew, int eh) => Assert.Equal((ew, eh), PaddlePipeline.DetInputSize(w, h));

    [Theory]
    [InlineData(423, 62, 2)]
    [InlineData(500, 500, 2)]
    [InlineData(800, 400, 1)]
    public void DetScale_UpscalesOnlySmallRegions(int w, int h, double scale) => Assert.Equal(scale, PaddlePipeline.DetScaleFor(w, h));
}
