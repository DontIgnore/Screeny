using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Editing;
using Screeny.Export;
using Screeny.Overlay;
using Screeny.Overlay.Tools;
using Screeny.Settings;

namespace Screeny.Tests.Overlay;

/// <summary>Options flyouts in windowless tree: panel and layer in one Canvas 1280×800.</summary>
[Collection(WpfControls.Name)]
public class OptionsFlyoutsTests
{
    private static readonly Rect Work = new(0, 0, 1280, 800);

    private sealed class Host(Canvas layer) : IFlyoutHost
    {
        public Canvas FlyoutLayer { get; } = layer;
        public Rect FlyoutWorkArea => Work;
        public bool ToolbarFlyoutsBelow { get; set; } = true;
        public Rect? ShapeRect(Annotation shape) => shape.Bounds;
    }

    private sealed class EditorHost : IAnnotationEditorHost
    {
        public bool OptionsEnabled => true;
        public bool CanUsePipette => true;
        public bool IsClosing => false;
        public bool HasGesture => false;
        public Point PixelToHud(Point px) => px;
        public double PixelsToHud(double length) => length;
        public void FocusSurface() { }
        public void ResetInteraction() { }
        public void RefreshHover() { }
        public void OnPipetteChanged(bool on) { }
        public void OnBeautifyChanged() { }
    }

    private static void Sta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            var animations = FlyoutPanel.AnimationsEnabled;
            FlyoutPanel.AnimationsEnabled = () => false;
            try { body(); }
            catch (Exception ex) { error = ex; }
            finally { FlyoutPanel.AnimationsEnabled = animations; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static (OptionsFlyouts Flyouts, ToolbarControl Toolbar, Host Host, Canvas Layer) Create(double toolbarY = 500)
    {
        var layer = new Canvas { Width = Work.Width, Height = Work.Height };
        var toolbar = new ToolbarControl();
        Canvas.SetLeft(toolbar, 200);
        Canvas.SetTop(toolbar, toolbarY);
        layer.Children.Add(toolbar);
        var host = new Host(layer);
        var flyouts = new OptionsFlyouts(toolbar);
        flyouts.Attach(host);
        Layout(layer);
        return (flyouts, toolbar, host, layer);
    }

    private static void Layout(UIElement root)
    {
        root.Measure(Work.Size);
        root.Arrange(Work);
        root.UpdateLayout();
    }

    private static Rect RectOf(FrameworkElement e, Canvas layer)
    {
        Layout(layer);
        return e.TransformToVisual(layer).TransformBounds(new Rect(e.RenderSize));
    }

    private static FlyoutState State(ToolKind tool = ToolKind.Select, OptionsSectionKind? section = null, Annotation? shape = null,
        bool hidden = false, bool beautify = false, bool enabled = true) => new(enabled, tool, section, shape, hidden, beautify);

    [Fact]
    public void SelectedShape_FlyoutNextToShape_HiddenWhileDragged() => Sta(() =>
    {
        var (flyouts, toolbar, host, layer) = Create();
        var rect = new RectAnnotation(new Rect(500, 250, 200, 120), new AnnotationStyle(Colors.Red, 4));
        flyouts.Options.Show(OptionsSectionKind.Shape, rect.Style, PaintTarget.Color, 40);

        flyouts.Update(State(section: OptionsSectionKind.Shape, shape: rect));
        Assert.True(flyouts.OptionsFlyout.IsOpen);
        Assert.True(flyouts.OptionsAtShape);
        var fly = RectOf(flyouts.OptionsFlyout, layer);
        Assert.True(fly.Bottom <= rect.Bounds.Top, $"flyout {fly} must be above shape {rect.Bounds}");
        Assert.Equal(rect.Bounds.Left + rect.Bounds.Width / 2, fly.Left + fly.Width / 2, 0);
        Assert.False(HudLayout.Overlaps(fly, RectOf(toolbar, layer)));

        flyouts.Update(State(section: OptionsSectionKind.Shape, shape: rect, hidden: true));
        Assert.False(flyouts.OptionsFlyout.IsOpen);
        Assert.Equal(Visibility.Collapsed, flyouts.OptionsFlyout.Visibility);

        rect.Offset(new Vector(-300, 0));
        flyouts.Update(State(section: OptionsSectionKind.Shape, shape: rect));
        Assert.True(flyouts.OptionsFlyout.IsOpen);
        fly = RectOf(flyouts.OptionsFlyout, layer);
        Assert.Equal(rect.Bounds.Left + rect.Bounds.Width / 2, fly.Left + fly.Width / 2, 0);

        flyouts.Update(State()); // selection cleared
        Assert.False(flyouts.OptionsFlyout.IsOpen);
    });

    [Fact]
    public void ShapeAtTop_FlyoutGoesBelowIt() => Sta(() =>
    {
        var (flyouts, _, _, layer) = Create();
        var rect = new RectAnnotation(new Rect(500, 10, 200, 60), new AnnotationStyle(Colors.Red, 4));
        flyouts.Options.Show(OptionsSectionKind.Shape, rect.Style, PaintTarget.Color, 40);
        flyouts.Update(State(section: OptionsSectionKind.Shape, shape: rect));
        var fly = RectOf(flyouts.OptionsFlyout, layer);
        Assert.True(fly.Top >= rect.Bounds.Bottom, $"flyout {fly} must be below shape {rect.Bounds}");
        Assert.True(flyouts.OptionsFlyout.Below);
    });

    [Fact]
    public void ToolIntro_OpensOncePerTool_AtItsButton_OutsideToolbar() => Sta(() =>
    {
        var (flyouts, toolbar, host, layer) = Create();
        toolbar.SetActiveTool(ToolKind.Rect);
        flyouts.Options.Show(OptionsSectionKind.Shape, new AnnotationStyle(Colors.Red, 3), PaintTarget.Color, 40);
        flyouts.Update(State(ToolKind.Rect, OptionsSectionKind.Shape));
        Assert.False(flyouts.OptionsFlyout.IsOpen); // no hover, no open

        flyouts.OnToolPicked(ToolKind.Rect);
        Assert.True(flyouts.OptionsFlyout.IsOpen);
        var fly = RectOf(flyouts.OptionsFlyout, layer);
        var bar = RectOf(toolbar, layer);
        var button = RectOf((FrameworkElement)toolbar.ToolButton(ToolKind.Rect), layer);
        Assert.True(fly.Top >= bar.Bottom, $"flyout {fly} must be below toolbar {bar}");
        Assert.Equal(button.Left + button.Width / 2, fly.Left + fly.Width / 2, 0);

        // Button tooltip would overlap flyout: while open, disabled.
        Assert.True(OptionsFlyouts.TooltipMuted(toolbar.ToolButton(ToolKind.Rect)));

        flyouts.CloseAll();
        Assert.False(OptionsFlyouts.TooltipMuted(toolbar.ToolButton(ToolKind.Rect)));
        flyouts.OnToolPicked(ToolKind.Rect);
        Assert.False(flyouts.OptionsFlyout.IsOpen); // second time in session — only on hover

        // Another tool without options: flyout closed and won't open.
        flyouts.Update(State(ToolKind.Pen));
        flyouts.OnToolPicked(ToolKind.Pen);
        Assert.False(flyouts.OptionsFlyout.IsOpen);
    });

    [Fact]
    public void ToolbarAboveSelection_ToolFlyoutOpensUpward() => Sta(() =>
    {
        var (flyouts, toolbar, host, layer) = Create(toolbarY: 300);
        host.ToolbarFlyoutsBelow = false;
        toolbar.SetActiveTool(ToolKind.Arrow);
        flyouts.Options.Show(OptionsSectionKind.Arrow, new AnnotationStyle(Colors.Red, 3), PaintTarget.Color, 40);
        flyouts.Update(State(ToolKind.Arrow, OptionsSectionKind.Arrow));
        flyouts.DevOpenTool();
        var fly = RectOf(flyouts.OptionsFlyout, layer);
        Assert.True(fly.Bottom <= RectOf(toolbar, layer).Top);
        Assert.False(flyouts.OptionsFlyout.Below);
    });

    [Fact]
    public void Beautify_OpensWhenEnabled_ClosesWhenDisabled() => Sta(() =>
    {
        var (flyouts, toolbar, _, layer) = Create();
        flyouts.Update(State(beautify: false));
        flyouts.OnBeautifyToggled(true); // state still “disabled”: no anchor
        Assert.False(flyouts.BeautifyFlyout.IsOpen);

        flyouts.Update(State(beautify: true));
        flyouts.OnBeautifyToggled(true);
        Assert.True(flyouts.BeautifyFlyout.IsOpen);
        var fly = RectOf(flyouts.BeautifyFlyout, layer);
        var button = RectOf(toolbar.BeautifyToggle, layer);
        Assert.Equal(button.Left + button.Width / 2, fly.Left + fly.Width / 2, 0);
        Assert.False(HudLayout.Overlaps(fly, RectOf(toolbar, layer)));

        Assert.True(OptionsFlyouts.TooltipMuted(toolbar.BeautifyToggle));

        flyouts.Update(State(beautify: false));
        Assert.False(flyouts.BeautifyFlyout.IsOpen);
        Assert.False(OptionsFlyouts.TooltipMuted(toolbar.BeautifyToggle));
    });

    [Fact]
    public void Disabled_ClosesEverything() => Sta(() =>
    {
        var (flyouts, _, _, _) = Create();
        var rect = new RectAnnotation(new Rect(500, 250, 200, 120), new AnnotationStyle(Colors.Red, 4));
        flyouts.Update(State(section: OptionsSectionKind.Shape, shape: rect, beautify: true));
        flyouts.DevOpenBeautify();
        Assert.True(flyouts.BeautifyFlyout.IsOpen && flyouts.OptionsFlyout.IsOpen);
        flyouts.Update(State(section: OptionsSectionKind.Shape, shape: rect, beautify: true, enabled: false));
        Assert.False(flyouts.BeautifyFlyout.IsOpen || flyouts.OptionsFlyout.IsOpen);
    });

    [Fact]
    public void Contains_RecognizesFlyouts() => Sta(() =>
    {
        var (flyouts, toolbar, _, _) = Create();
        Assert.True(flyouts.Contains(flyouts.OptionsFlyout));
        Assert.True(flyouts.Contains(flyouts.BeautifyFlyout));
        Assert.False(flyouts.Contains(toolbar));
        Assert.False(flyouts.Contains(null));
    });

    [Fact]
    public void FlyoutPanel_Animation_MidFrameIsPartial_FinalIsOpaque() => Sta(() =>
    {
        FlyoutPanel.AnimationsEnabled = () => true;
        var panel = new FlyoutPanel();
        var section = new BeautifySection();
        panel.Body = section;
        panel.FreezeOpeningAt(new Point(0.5, 0), below: true, ms: 80);
        Assert.True(panel.IsOpen);
        Assert.InRange(panel.Opacity, 0.5, 0.99);
        var scale = (ScaleTransform)panel.RenderTransform;
        Assert.InRange(scale.ScaleX, FlyoutPanel.StartScale, 0.999);
        var rows = section.FlyoutItems;
        Assert.Equal(4, rows.Count);
        Assert.True(rows[0].Opacity > rows[^1].Opacity, "строки появляются поочерёдно");
        Assert.Equal(0, rows[^1].Opacity); // last row not started: 30 + 3·28 > 80
        Assert.True(((TranslateTransform)rows[0].RenderTransform).Y < 0, "вниз вылетает — строки приезжают сверху");

        panel.Close(animate: false);
        Assert.Equal(Visibility.Collapsed, panel.Visibility);
        Assert.Equal(1, rows[^1].Opacity); // after close rows reset
    });

    [Fact]
    public void FlyoutPanel_NoSystemAnimation_OpensInstantly() => Sta(() =>
    {
        var panel = new FlyoutPanel { Body = new BeautifySection() };
        panel.Open(new Point(0.5, 1), below: false);
        Assert.False(panel.IsAnimating);
        Assert.Equal(1, panel.Opacity);
        Assert.Equal(Visibility.Visible, panel.Visibility);
        Assert.Equal(1, ((ScaleTransform)panel.RenderTransform).ScaleX);
        panel.Close();
        Assert.Equal(Visibility.Collapsed, panel.Visibility);
    });

    [Fact]
    public void OpenDuration_IncludesStagger()
    {
        Assert.Equal(FlyoutPanel.OpenMs, FlyoutPanel.OpenDuration(0));
        Assert.Equal(FlyoutPanel.ItemDelayMs + 3 * FlyoutPanel.StaggerMs + FlyoutPanel.ItemMs, FlyoutPanel.OpenDuration(4));
    }

    [Fact]
    public void Editor_SelectingWithSelectTool_ShowsShapeFlyoutAfterRelease_NotAfterDrawing() => Sta(() =>
    {
        var (flyouts, toolbar, _, _) = Create();
        var bmp = BitmapSource.Create(1280, 800, 96, 96, PixelFormats.Bgra32, null, new byte[1280 * 800 * 4], 1280 * 4);
        bmp.Freeze();
        var editor = new AnnotationEditor(new AnnotationEditorParts(toolbar, flyouts, new TextBox(), new AnnotationLayer(), new RulerLayer()),
            bmp, new AppSettings(), new EditorHost(), new BeautifyState(false, BeautifyOptions.Default, Persist: false));

        // Drawn rectangle doesn't open options next to it.
        editor.SetTool(ToolKind.Rect);
        flyouts.CloseAll(animate: false); // first tool selection tooltip
        editor.BeginGesture(new Point(400, 200));
        editor.MoveGesture(new Point(600, 320));
        editor.EndGesture(new Point(600, 320));
        Assert.False(flyouts.OptionsAtShape);

        // Select selection: while shape dragged hidden, after release next to it.
        editor.SetTool(ToolKind.Select);
        Assert.True(editor.BeginGesture(new Point(400, 260)));
        editor.MoveGesture(new Point(380, 260));
        Assert.False(flyouts.OptionsFlyout.IsOpen);
        editor.EndGesture(new Point(380, 260));
        Assert.True(flyouts.OptionsAtShape);
        Assert.Equal(OptionsSectionKind.Shape, flyouts.Options.Shown);

        editor.Deselect();
        Assert.False(flyouts.OptionsFlyout.IsOpen);
    });
}
