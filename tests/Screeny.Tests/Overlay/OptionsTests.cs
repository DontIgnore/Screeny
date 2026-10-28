using System.Windows;
using System.Windows.Media;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Overlay;
using Screeny.Overlay.Tools;
using Screeny.Settings;

namespace Screeny.Tests.Overlay;

public class OptionsSectionsTests
{
    private static readonly AnnotationStyle Style = new(Colors.Red, 3);

    [Theory]
    [InlineData(ToolKind.Rect, OptionsSectionKind.Shape)]
    [InlineData(ToolKind.Ellipse, OptionsSectionKind.Shape)]
    [InlineData(ToolKind.Arrow, OptionsSectionKind.Arrow)]
    [InlineData(ToolKind.Text, OptionsSectionKind.Text)]
    public void Tool_WithParameters_HasItsSection(ToolKind tool, OptionsSectionKind expected)
        => Assert.Equal([expected], OptionsSections.For(new OptionsContext(tool, null)));

    [Theory]
    [InlineData(ToolKind.Select)]
    [InlineData(ToolKind.Line)]
    [InlineData(ToolKind.Pen)]
    [InlineData(ToolKind.Highlighter)]
    [InlineData(ToolKind.Number)]
    [InlineData(ToolKind.Pixelate)]
    [InlineData(ToolKind.Blur)]
    [InlineData(ToolKind.Ruler)]
    public void Tool_WithoutParameters_HidesStrip(ToolKind tool) => Assert.Empty(OptionsSections.For(new OptionsContext(tool, null)));

    [Fact]
    public void Beautify_AddsItsSection_AfterToolOrSelection()
    {
        Assert.Equal([OptionsSectionKind.Beautify], OptionsSections.For(new OptionsContext(ToolKind.Select, null) { Beautify = true }));
        Assert.Equal([OptionsSectionKind.Shape, OptionsSectionKind.Beautify], OptionsSections.For(new OptionsContext(ToolKind.Rect, null) { Beautify = true }));
        var text = new TextAnnotation(new Point(0, 0), "t", Style);
        Assert.Equal([OptionsSectionKind.Text, OptionsSectionKind.Beautify], OptionsSections.For(new OptionsContext(ToolKind.Rect, text) { Beautify = true }));
        Assert.Equal([OptionsSectionKind.Shape], OptionsSections.For(new OptionsContext(ToolKind.Rect, null) { Beautify = false }));
    }

    [Fact]
    public void Beautify_HasNoPaintTargets_PaletteKeepsMainColor()
    {
        Assert.Empty(OptionsSections.Targets(OptionsSectionKind.Beautify));
        Assert.Equal(PaintTarget.Color, OptionsSections.EffectiveTarget([OptionsSectionKind.Beautify], PaintTarget.Fill));
    }

    [Fact]
    public void Selected_WinsOverTool()
    {
        var arrow = new ArrowAnnotation(new Point(0, 0), new Point(10, 10), Style);
        Assert.Equal([OptionsSectionKind.Arrow], OptionsSections.For(new OptionsContext(ToolKind.Rect, arrow)));
        var text = new TextAnnotation(new Point(0, 0), "t", Style);
        Assert.Equal([OptionsSectionKind.Text], OptionsSections.For(new OptionsContext(ToolKind.Select, text)));
        var ellipse = new EllipseAnnotation(new Rect(0, 0, 5, 5), Style);
        Assert.Equal([OptionsSectionKind.Shape], OptionsSections.For(new OptionsContext(ToolKind.Select, ellipse)));
    }

    [Fact]
    public void SelectedLine_WhileRectToolActive_HasNoSections()
    {
        var line = new LineAnnotation(new Point(0, 0), new Point(10, 10), Style);
        Assert.Empty(OptionsSections.For(new OptionsContext(ToolKind.Rect, line)));
    }

    [Fact]
    public void Effects_AreNotShapes()
    {
        Assert.Null(OptionsSections.ForAnnotation(new PixelateAnnotation(new Rect(0, 0, 5, 5), Style)));
        Assert.Null(OptionsSections.ForAnnotation(new BlurAnnotation(new Rect(0, 0, 5, 5), Style)));
    }

    [Fact]
    public void EffectiveTarget_FallsBackToColor_WhenSectionHasNoSuchSwatch()
    {
        Assert.Equal(PaintTarget.Fill, OptionsSections.EffectiveTarget([OptionsSectionKind.Shape], PaintTarget.Fill));
        Assert.Equal(PaintTarget.Color, OptionsSections.EffectiveTarget([OptionsSectionKind.Text], PaintTarget.Fill));
        Assert.Equal(PaintTarget.Background, OptionsSections.EffectiveTarget([OptionsSectionKind.Text], PaintTarget.Background));
        Assert.Equal(PaintTarget.Color, OptionsSections.EffectiveTarget([OptionsSectionKind.Arrow], PaintTarget.Background));
        Assert.Equal(PaintTarget.Color, OptionsSections.EffectiveTarget([], PaintTarget.Fill));
    }
}

public class StyleOptionsTests
{
    private static readonly Color Blue = Color.FromRgb(0, 0x7A, 0xFF);

    private static StyleOptions Options(Action<AppSettings>? setup = null)
    {
        var s = new AppSettings();
        setup?.Invoke(s);
        return StyleOptions.FromSettings(s, Blue);
    }

    [Fact]
    public void StyleFor_GivesFillOnlyToRectAndEllipse()
    {
        var o = Options(s => s.LastFillEnabled = true);
        var rect = o.StyleFor(ToolKind.Rect, Colors.Red, 3);
        Assert.Equal(ColorHex.WithOpacity(Blue, 40), rect.Fill); // заливка — цвет палитры с прозрачностью 40 %
        Assert.Equal(Colors.Red, rect.Color);
        Assert.NotNull(o.StyleFor(ToolKind.Ellipse, Colors.Red, 3).Fill);
        foreach (var tool in new[] { ToolKind.Line, ToolKind.Pen, ToolKind.Highlighter, ToolKind.Number, ToolKind.Arrow, ToolKind.Text, ToolKind.Pixelate })
            Assert.Equal(new AnnotationStyle(Colors.Red, 3) { Arrow = tool == ToolKind.Arrow ? o.Arrow : ArrowStyle.Normal }, o.StyleFor(tool, Colors.Red, 3));
    }

    [Fact]
    public void StyleFor_ArrowStyle_OnlyForArrow_TextBackground_OnlyForText()
    {
        var o = Options(s => { s.LastArrowStyle = ArrowStyle.Thick; s.LastTextBackground = true; });
        Assert.Equal(ArrowStyle.Thick, o.StyleFor(ToolKind.Arrow, Colors.Red, 3).Arrow);
        Assert.Equal(ArrowStyle.Normal, o.StyleFor(ToolKind.Line, Colors.Red, 3).Arrow);
        Assert.Equal(StyleOptions.DefaultTextBackground, o.StyleFor(ToolKind.Text, Colors.Red, 3).TextBackground);
        Assert.Null(o.StyleFor(ToolKind.Rect, Colors.Red, 3).TextBackground);
    }

    [Fact]
    public void FromSettings_BothOff_KeepsStroke()
    {
        var o = Options(s => { s.LastStrokeEnabled = false; s.LastFillEnabled = false; });
        Assert.True(o.StyleFor(ToolKind.Rect, Colors.Red, 3).StrokeEnabled);
    }

    [Fact]
    public void ToggleStroke_RefusedWithoutFill_AllowedWithFill()
    {
        var o = Options();
        var plain = o.StyleFor(ToolKind.Rect, Colors.Red, 3);
        Assert.Same(plain, o.ToggleStroke(plain));
        var filled = o.ToggleFill(plain);
        Assert.NotNull(filled.Fill);
        var fillOnly = o.ToggleStroke(filled);
        Assert.False(fillOnly.StrokeEnabled);
        Assert.Same(fillOnly, o.ToggleFill(fillOnly)); // убрать заливку у фигуры без обводки нельзя
    }

    [Fact]
    public void Paint_ActiveSwatch()
    {
        var o = Options();
        var s = o.StyleFor(ToolKind.Rect, Colors.Red, 3);
        var fill = o.Paint(s, PaintTarget.Fill, Colors.Green);
        Assert.Equal(ColorHex.WithOpacity(Colors.Green, 40), fill.Fill); // заливка включилась, 40 %
        Assert.Equal(Colors.Red, fill.Color);                           // обводка не тронута
        var fill80 = fill with { Fill = ColorHex.WithOpacity(Colors.Green, 80) };
        Assert.Equal(ColorHex.WithOpacity(Colors.Yellow, 80), o.Paint(fill80, PaintTarget.Fill, Colors.Yellow).Fill); // прозрачность сохраняется
        var stroke = o.Paint(fill.WithStroke(false), PaintTarget.Color, Colors.Black);
        Assert.Equal(Colors.Black, stroke.Color);
        Assert.True(stroke.StrokeEnabled);                              // покраска обводки возвращает её
        var text = o.Paint(o.StyleFor(ToolKind.Text, Colors.Red, 3), PaintTarget.Background, Colors.White);
        Assert.Equal(Colors.White, text.TextBackground);
    }

    [Fact]
    public void NextFillOpacity_CyclesSteps_AndRemembers()
    {
        var o = Options(s => s.LastFillEnabled = true);
        var s = o.StyleFor(ToolKind.Rect, Colors.Red, 3);
        var seen = new List<int>();
        for (int i = 0; i < 5; i++) { s = o.NextFillOpacity(s); seen.Add(ColorHex.OpacityPercent(s.Fill!.Value)); }
        Assert.Equal([60, 80, 100, 20, 40], seen);
        Assert.Equal(40, o.FillOpacity);
    }

    [Fact]
    public void Absorb_RemembersEditedSection_AndSavesToSettings()
    {
        var o = Options();
        var edited = new AnnotationStyle(Colors.Red, 3) { StrokeEnabled = false, Fill = ColorHex.WithOpacity(Colors.Green, 60) };
        o.Absorb(edited, OptionsSectionKind.Shape);
        o.Absorb(new AnnotationStyle(Colors.Red, 3) { Arrow = ArrowStyle.Dashed }, OptionsSectionKind.Arrow);
        o.Absorb(new AnnotationStyle(Colors.Red, 3) { TextBackground = Colors.White }, OptionsSectionKind.Text);
        var next = o.StyleFor(ToolKind.Rect, Colors.Red, 3);
        Assert.False(next.StrokeEnabled);
        Assert.Equal(edited.Fill, next.Fill);
        Assert.Equal(Colors.White, o.StyleFor(ToolKind.Text, Colors.Red, 3).TextBackground);

        var settings = new AppSettings();
        o.SaveTo(settings);
        Assert.False(settings.LastStrokeEnabled);
        Assert.True(settings.LastFillEnabled);
        Assert.Equal(60, settings.LastFillOpacity);
        Assert.Equal(ArrowStyle.Dashed, settings.LastArrowStyle);
        Assert.True(settings.LastTextBackground);
    }

    [Fact]
    public void SwatchColor_ShowsActiveSwatchColor()
    {
        var s = new AnnotationStyle(Colors.Red, 3) { Fill = ColorHex.WithOpacity(Colors.Green, 40) };
        Assert.Equal(Colors.Red, StyleOptions.SwatchColor(s, PaintTarget.Color));
        Assert.Equal(Colors.Green, StyleOptions.SwatchColor(s, PaintTarget.Fill)); // без прозрачности
        Assert.Null(StyleOptions.SwatchColor(s, PaintTarget.Background));
    }
}

public class RulerTests
{
    [Fact]
    public void Measurement_DxDyLength()
    {
        var m = new RulerMeasurement(new Point(10, 20), new Point(40, 60));
        Assert.Equal(30, m.Dx);
        Assert.Equal(40, m.Dy);
        Assert.Equal(50, m.Length, 6);
        Assert.Equal("Δx 30 · Δy 40 · 50 px", m.Label);
    }

    [Fact]
    public void Label_UsesAbsoluteOffsets_AndOneDecimal()
        => Assert.Equal("Δx 10 · Δy 1 · 10 px", new RulerMeasurement(new Point(10, 1), new Point(0, 0)).Label);

    [Theory]
    [InlineData(100, 10, 100.5, 0)]   // почти горизонталь → горизонталь
    [InlineData(10, 100, 0, 100.5)]   // почти вертикаль → вертикаль
    [InlineData(100, 90, 95.1, 95.1)] // почти диагональ → 45°
    [InlineData(-100, 95, -97.5, 97.5)]
    public void Snap45_KeepsLength_AndSnapsAngle(double x, double y, double ex, double ey)
    {
        var p = RulerMeasurement.Snap45(new Point(0, 0), new Point(x, y));
        Assert.Equal(ex, p.X, 1);
        Assert.Equal(ey, p.Y, 1);
        Assert.Equal(new Vector(x, y).Length, (p - new Point(0, 0)).Length, 4);
    }

    [Fact]
    public void Between_RoundsToPixels()
    {
        var m = RulerMeasurement.Between(new Point(10.4, 20.6), new Point(50.2, 20.9), snap: false);
        Assert.Equal(new Point(10, 21), m.Start);
        Assert.Equal(new Point(50, 21), m.End);
        Assert.Equal(0, m.Dy);
    }

    [Fact]
    public void Tool_DragMeasures_WithoutTouchingDocument()
    {
        var doc = new AnnotationDocument();
        var tool = new RulerTool();
        int changes = 0;
        tool.Changed += () => changes++;
        tool.Down(new Point(0, 0), doc, new AnnotationStyle(Colors.Red, 3));
        Assert.True(tool.IsActive);
        tool.Move(new Point(30, 0));
        tool.Up(new Point(30, 40));
        Assert.False(tool.IsActive);
        Assert.Equal(50, tool.Measurement!.Value.Length, 6);
        Assert.Empty(doc.Items);
        Assert.False(doc.Undo.CanUndo);
        Assert.True(changes >= 2);
    }

    [Fact]
    public void Tool_ShiftSnaps()
    {
        var tool = new RulerTool(() => true);
        tool.Down(new Point(0, 0), new AnnotationDocument(), new AnnotationStyle(Colors.Red, 3));
        tool.Up(new Point(100, 8));
        Assert.Equal(0, tool.Measurement!.Value.Dy, 6);
    }

    [Fact]
    public void Tool_ClickWithoutDrag_RemovesMeasurement_CancelAndClearToo()
    {
        var doc = new AnnotationDocument();
        var style = new AnnotationStyle(Colors.Red, 3);
        var tool = new RulerTool();
        tool.Down(new Point(0, 0), doc, style);
        tool.Up(new Point(20, 0));
        Assert.NotNull(tool.Measurement);
        tool.Down(new Point(5, 5), doc, style);
        tool.Up(new Point(5, 5));
        Assert.Null(tool.Measurement);

        tool.Down(new Point(0, 0), doc, style);
        tool.Move(new Point(10, 10));
        tool.Cancel();
        Assert.Null(tool.Measurement);
        Assert.False(tool.IsActive);

        tool.Down(new Point(0, 0), doc, style);
        tool.Up(new Point(10, 10));
        tool.Clear();
        Assert.Null(tool.Measurement);
    }
}
