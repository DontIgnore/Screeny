using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Editing;
using Screeny.Export;
using Screeny.Features;
using Screeny.Overlay;
using Screeny.Overlay.Tools;
using Screeny.Settings;

namespace Screeny.Tests.Editing;

/// <summary>Windowless editor controller: real HUD controls in STA thread, stub host.</summary>
[Collection(WpfControls.Name)]
public class AnnotationEditorTests
{
    private sealed class FakeHost : IAnnotationEditorHost
    {
        public bool OptionsEnabled { get; set; } = true;
        public bool CanUsePipette { get; set; } = true;
        public bool IsClosing { get; set; }
        public bool HasGesture { get; set; }
        public int BeautifyChanges, Focuses;
        public Point PixelToHud(Point px) => px;
        public double PixelsToHud(double length) => length;
        public void FocusSurface() => Focuses++;
        public void ResetInteraction() { }
        public void RefreshHover() { }
        public void OnPipetteChanged(bool on) { }
        public void OnBeautifyChanged() => BeautifyChanges++;
    }

    private static void Sta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static (AnnotationEditor Editor, FakeHost Host, AppSettings Settings, ToolbarControl Toolbar) Create(BeautifyState? beautify = null,
        AppSettings? withSettings = null)
    {
        var px = new byte[40 * 30 * 4];
        var bmp = BitmapSource.Create(40, 30, 96, 96, PixelFormats.Bgra32, null, px, 40 * 4);
        bmp.Freeze();
        var settings = withSettings ?? new AppSettings();
        var host = new FakeHost();
        var toolbar = new ToolbarControl();
        var parts = new AnnotationEditorParts(toolbar, new OptionsFlyouts(toolbar), new TextBox(), new AnnotationLayer(), new RulerLayer());
        var editor = new AnnotationEditor(parts, bmp, settings, host, beautify ?? new BeautifyState(false, BeautifyOptions.Default, Persist: true));
        return (editor, host, settings, toolbar);
    }

    [Fact]
    public void Gesture_DrawsShapeWithCurrentTool_OneUndoStep() => Sta(() =>
    {
        var (editor, _, _, _) = Create();
        editor.SetTool(ToolKind.Rect);
        Assert.True(editor.BeginGesture(new Point(2, 2)));
        editor.MoveGesture(new Point(20, 15));
        Assert.True(editor.EndGesture(new Point(20, 15)));
        Assert.IsType<RectAnnotation>(Assert.Single(editor.Document.Items));
        editor.Undo();
        Assert.Empty(editor.Document.Items);
    });

    [Fact]
    public void HandleKey_ToolKeysOnlyWhenEditing_UndoAlways() => Sta(() =>
    {
        var (editor, _, _, _) = Create();
        Assert.False(editor.HandleKey(Key.D2, ModifierKeys.None, editing: false));
        Assert.Equal(ToolKind.Select, editor.CurrentTool);
        Assert.True(editor.HandleKey(Key.D2, ModifierKeys.None, editing: true));
        Assert.Equal(ToolKind.Rect, editor.CurrentTool);
        Assert.True(editor.HandleKey(Key.NumPad3, ModifierKeys.None, editing: true));
        Assert.Equal(ToolKind.Ellipse, editor.CurrentTool);
        Assert.False(editor.HandleKey(Key.D2, ModifierKeys.Control, editing: true)); // Ctrl+number — not tool
        Assert.True(editor.HandleKey(Key.Z, ModifierKeys.Control, editing: false));
        Assert.False(editor.HandleKey(Key.Q, ModifierKeys.None, editing: true));
    });

    [Fact]
    public void HandleKey_DeleteAndArrows_ActOnSelectedShape() => Sta(() =>
    {
        var (editor, host, _, _) = Create();
        var rect = new RectAnnotation(new Rect(5, 5, 10, 10), new AnnotationStyle(Colors.Red, 3));
        editor.Document.Add(rect);
        Assert.False(editor.HandleKey(Key.Delete, ModifierKeys.None, editing: true)); // nothing selected
        editor.Document.Selected = rect;
        Assert.True(editor.HandleKey(Key.Right, ModifierKeys.Control, editing: true));
        Assert.Equal(new Rect(15, 5, 10, 10), rect.Rect);
        host.HasGesture = true; // mid-host-gesture arrows don't move shape
        Assert.True(editor.HandleKey(Key.Down, ModifierKeys.None, editing: true));
        Assert.Equal(new Rect(15, 5, 10, 10), rect.Rect);
        Assert.True(editor.HandleKey(Key.Delete, ModifierKeys.None, editing: true));
        Assert.Empty(editor.Document.Items);
    });

    [Fact]
    public void DrawingDisabled_BlocksGesturesKeysAndUndo() => Sta(() =>
    {
        var (editor, _, _, _) = Create();
        editor.SetTool(ToolKind.Rect);
        editor.Document.Add(new RectAnnotation(new Rect(1, 1, 5, 5), new AnnotationStyle(Colors.Red, 3)));
        editor.DrawingEnabled = false;
        Assert.Equal(ToolKind.Select, editor.CurrentTool); // disabling resets tool
        Assert.False(editor.BeginGesture(new Point(2, 2)));
        Assert.False(editor.HandleKey(Key.D2, ModifierKeys.None, editing: true));
        editor.Undo();
        Assert.Single(editor.Document.Items);
        editor.DrawingEnabled = true;
        editor.Undo();
        Assert.Empty(editor.Document.Items);
    });

    [Fact]
    public void Beautify_PersistsOnlyWhenAsked() => Sta(() =>
    {
        var (persisted, host, settings, _) = Create();
        persisted.SetBeautify(true);
        persisted.SetBeautifyOptions(new BeautifyOptions(BeautifyPreset.Mint, BeautifyPadding.L, 16, false));
        Assert.True(settings.BeautifyEnabled);
        Assert.Equal(BeautifyPreset.Mint, settings.BeautifyPreset);
        Assert.Equal(2, host.BeautifyChanges);

        var record = new BeautifyOptions(BeautifyPreset.Sunset, BeautifyPadding.S, 0, true);
        var (local, _, localSettings, _) = Create(new BeautifyState(true, record, Persist: false));
        Assert.True(local.BeautifyOn);
        Assert.Equal(record, local.Beautify);
        local.SetBeautify(false);
        local.SetBeautifyOptions(BeautifyOptions.Default);
        Assert.False(localSettings.BeautifyEnabled);                    // default — disabled, not touched
        Assert.Equal(BeautifyOptions.Default.Preset, localSettings.BeautifyPreset);
        Assert.False(local.BeautifyOn);
    });

    [Fact]
    public void AdjustThickness_RemembersInSettings() => Sta(() =>
    {
        var (editor, _, settings, toolbar) = Create();
        editor.AdjustThickness(+2);
        Assert.Equal(5, toolbar.CurrentThickness);
        Assert.Equal(5, settings.LastThickness);
    });

    [Fact]
    public void TextEdit_DeselectsShape_PaletteColorsTextNotShape() => Sta(() =>
    {
        // Black opaque base: eyedropper gives color different from default red.
        var px = new byte[40 * 30 * 4];
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        var bmp = BitmapSource.Create(40, 30, 96, 96, PixelFormats.Bgra32, null, px, 40 * 4);
        bmp.Freeze();
        var text = new TextBox();
        var bar = new ToolbarControl();
        var parts = new AnnotationEditorParts(bar, new OptionsFlyouts(bar), text, new AnnotationLayer(), new RulerLayer());
        var editor = new AnnotationEditor(parts, bmp, new AppSettings(), new FakeHost(), new BeautifyState(false, BeautifyOptions.Default, Persist: true));
        editor.SetTool(ToolKind.Rect);
        editor.BeginGesture(new Point(2, 2)); editor.MoveGesture(new Point(20, 15)); editor.EndGesture(new Point(20, 15));
        var rect = Assert.IsType<RectAnnotation>(Assert.Single(editor.Document.Items));
        var rectColor = rect.Style.Color;
        editor.Document.Selected = rect;
        editor.Document.NotifyChanged();

        editor.SetTool(ToolKind.Text);
        editor.BeginGesture(new Point(5, 20));
        Assert.True(editor.IsTextEditing);
        Assert.Null(editor.Document.Selected); // strip and palette now about entering text

        editor.PickColor(new PixelPoint(1, 1));
        text.Text = "Hi";
        editor.CommitTextEdit();

        var shape = Assert.Single(editor.Document.Items.OfType<RectAnnotation>());
        Assert.Equal(rectColor, shape.Style.Color);
        Assert.Equal(Colors.Black, Assert.Single(editor.Document.Items.OfType<TextAnnotation>()).Style.Color);
    });

    [Fact]
    public void TextEdit_FieldAlignsLikeBlock_MinWidthOnlyForLeft() => Sta(() =>
    {
        var px = new byte[40 * 30 * 4];
        var bmp = BitmapSource.Create(40, 30, 96, 96, PixelFormats.Bgra32, null, px, 40 * 4);
        bmp.Freeze();
        var text = new TextBox { MinWidth = 40 };
        var bar = new ToolbarControl();
        var parts = new AnnotationEditorParts(bar, new OptionsFlyouts(bar), text, new AnnotationLayer(), new RulerLayer());
        var settings = new AppSettings { LastTextAlign = TextAlign.Right };
        var editor = new AnnotationEditor(parts, bmp, settings, new FakeHost(), new BeautifyState(false, BeautifyOptions.Default, Persist: true));

        editor.SetTool(ToolKind.Text);
        editor.BeginGesture(new Point(5, 5));
        Assert.True(editor.IsTextEditing);
        Assert.Equal(TextAlignment.Right, text.TextAlignment);
        Assert.Equal(0, text.MinWidth); // short line doesn't shift right of empty field
        text.Text = "Hi";
        editor.CommitTextEdit();
        Assert.Equal(TextAlign.Right, Assert.Single(editor.Document.Items.OfType<TextAnnotation>()).Style.TextAlign);

        settings.LastTextAlign = TextAlign.Left;
        var leftText = new TextBox { MinWidth = 40 };
        var left = new AnnotationEditor(parts with { TextEditor = leftText }, bmp, settings, new FakeHost(), new BeautifyState(false, BeautifyOptions.Default, Persist: true));
        left.SetTool(ToolKind.Text);
        left.BeginGesture(new Point(5, 5));
        Assert.Equal(TextAlignment.Left, leftText.TextAlignment);
        Assert.Equal(40, leftText.MinWidth);
        left.CancelTextEdit();
    });
    // ---------- Disabled features ----------

    private static AppSettings Without(params string[] features)
    {
        var s = new AppSettings();
        foreach (var f in features) s.SetFeature(f, false);
        return s;
    }

    [Fact]
    public void DisabledFeatures_KeysAreNotHandled_ButtonsHidden() => Sta(() =>
    {
        var (editor, _, _, toolbar) = Create(withSettings: Without(FeatureCatalog.Pipette, FeatureCatalog.Beautify, FeatureCatalog.Ruler));
        Assert.False(editor.HandleKey(Key.I, ModifierKeys.None, editing: true));
        Assert.False(editor.PipetteOn);
        Assert.False(editor.HandleKey(Key.B, ModifierKeys.None, editing: true));
        Assert.False(editor.BeautifyOn);
        Assert.False(editor.HandleKey(Key.OemPlus, ModifierKeys.None, editing: true));
        Assert.False(editor.HandleKey(Key.Add, ModifierKeys.None, editing: true));
        Assert.Equal(ToolKind.Select, editor.CurrentTool);
        Assert.Equal(Visibility.Collapsed, toolbar.ToolButton(ToolKind.Ruler).Visibility);
        Assert.Equal(Visibility.Collapsed, toolbar.BeautifyToggle.Visibility);
        Assert.Equal(Visibility.Collapsed, ((UIElement)toolbar.FindName("PipetteButton")).Visibility);
        Assert.True(editor.HandleKey(Key.D2, ModifierKeys.None, editing: true)); // other tools work
        Assert.Equal(ToolKind.Rect, editor.CurrentTool);
    });

    [Fact]
    public void DisabledFeatures_DirectCallsAreIgnored() => Sta(() =>
    {
        var (editor, _, settings, _) = Create(withSettings: Without(FeatureCatalog.Pipette, FeatureCatalog.Beautify, FeatureCatalog.Ruler));
        editor.SetPipette(true);
        Assert.False(editor.PipetteOn);
        editor.SetTool(ToolKind.Ruler);
        Assert.Equal(ToolKind.Select, editor.CurrentTool);
        editor.SetBeautify(true);
        Assert.False(editor.BeautifyOn);
        Assert.False(settings.BeautifyEnabled);
    });

    [Fact]
    public void BeautifyOff_SavedBeautifyIgnored_AndNotOverwritten() => Sta(() =>
    {
        var settings = Without(FeatureCatalog.Beautify);
        settings.BeautifyEnabled = true;
        var (editor, _, _, toolbar) = Create(new BeautifyState(true, BeautifyOptions.Default, Persist: true), settings);
        Assert.False(editor.BeautifyOn); // screenshot not decorated
        Assert.True(settings.BeautifyEnabled); // saved value untouched
        Assert.NotEqual(true, ((System.Windows.Controls.Primitives.ToggleButton)toolbar.BeautifyToggle).IsChecked);
    });

    [Fact]
    public void ApplyFeatures_TurnsOffWhatWasDisabledWhileOpen_AndShowsItAgain() => Sta(() =>
    {
        var settings = new AppSettings();
        var (editor, _, _, toolbar) = Create(new BeautifyState(true, BeautifyOptions.Default, Persist: false), settings);
        editor.SetTool(ToolKind.Ruler);
        Assert.True(editor.BeautifyOn);
        settings.SetFeature(FeatureCatalog.Ruler, false);
        settings.SetFeature(FeatureCatalog.Beautify, false);
        editor.ApplyFeatures();
        Assert.Equal(ToolKind.Select, editor.CurrentTool);
        Assert.False(editor.BeautifyOn);
        Assert.Equal(Visibility.Collapsed, toolbar.ToolButton(ToolKind.Ruler).Visibility);
        settings.SetFeature(FeatureCatalog.Ruler, true);
        editor.ApplyFeatures();
        Assert.Equal(Visibility.Visible, toolbar.ToolButton(ToolKind.Ruler).Visibility);
        Assert.True(editor.HandleKey(Key.OemPlus, ModifierKeys.None, editing: true));
        Assert.Equal(ToolKind.Ruler, editor.CurrentTool);
    });
}
