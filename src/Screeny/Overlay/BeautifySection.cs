using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Screeny.Export;
using Screeny.Localization;

namespace Screeny.Overlay;

/// <summary>
/// Decoration parameters — content of flyout near ✨ button: “Background” row (6 gradient presets), “Margin” S/M/L,
/// “Rounding” 0/8/16 and “Shadow”. Manages own state: click reports new parameter set via
/// <see cref="OptionsChanged"/>, shows what <see cref="Show"/> sets. Button styles taken from flyout
/// (dynamic resources from FlyoutStyles.xaml). All buttons Focusable=False — keyboard stays with window.
/// </summary>
public sealed class BeautifySection : StackPanel, IFlyoutItems
{
    private readonly Dictionary<BeautifyPreset, Button> _presets = new();
    private readonly Dictionary<BeautifyPadding, ToggleButton> _paddings = new();
    private readonly Dictionary<int, ToggleButton> _radii = new();
    private readonly ToggleButton _shadow;
    private BeautifyOptions _options = BeautifyOptions.Default;
    /// <summary>Labels and tooltips from UI strings: rebuilt on language change (editor flyout lives long).</summary>
    private readonly List<Action> _texts = new();

    public event Action<BeautifyOptions>? OptionsChanged;

    public BeautifySection()
    {
        Orientation = Orientation.Vertical;
        Focusable = false;

        var presets = Row("Beautify.Background");
        foreach (var preset in BeautifyOptions.Presets)
        {
            var chip = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(5), Background = BeautifyOptions.Brush(preset) };
            var button = new Button { Content = chip, Focusable = false };
            _texts.Add(() => button.ToolTip = Loc.F("Beautify.BackgroundPreset", BeautifyOptions.Name(preset)));
            button.SetResourceReference(StyleProperty, "SwatchButton");
            var p = preset;
            button.Click += (_, _) => Raise(_options with { Preset = p });
            _presets[preset] = button;
            presets.Children.Add(button);
        }

        var paddings = Row("Beautify.Padding");
        foreach (var padding in Enum.GetValues<BeautifyPadding>())
        {
            var p = padding;
            var button = Toggle(paddings, () => padding.ToString(), () => Loc.F("Beautify.PaddingPixels", BeautifyOptions.PaddingPixels(p)), () => _options with { Padding = p });
            button.MinWidth = 30;
            _paddings[padding] = button;
        }

        var radii = Row("Beautify.Radius");
        foreach (var radius in BeautifyOptions.Radii)
        {
            var r = radius;
            var button = Toggle(radii, () => r.ToString(), () => r == 0 ? Loc.T("Beautify.NoRadius") : Loc.F("Beautify.RadiusPixels", r), () => _options with { Radius = r });
            button.MinWidth = 30;
            _radii[radius] = button;
        }

        var shadow = Row("Beautify.Shadow");
        _shadow = Toggle(shadow, () => Loc.T("Beautify.Soft"), () => Loc.T("Beautify.SoftTip"), () => _options with { Shadow = !_options.Shadow });
        _shadow.Padding = new Thickness(8, 0, 8, 0);
        ApplyTexts();
        // Loaded may come more than once (tab, re-show): subscribe without duplicates.
        Loaded += (_, _) => { Loc.Changed -= ApplyTexts; Loc.Changed += ApplyTexts; ApplyTexts(); };
        Unloaded += (_, _) => Loc.Changed -= ApplyTexts;
    }

    private void ApplyTexts()
    {
        foreach (var apply in _texts) apply();
    }

    /// <summary>Rows in order: on flyout open they appear one by one.</summary>
    public IReadOnlyList<UIElement> FlyoutItems => Children.Cast<UIElement>().ToList();

    /// <summary>Show current parameters: active preset outlined, selected margin, rounding and shadow pressed.</summary>
    public void Show(BeautifyOptions options)
    {
        _options = options;
        foreach (var (preset, button) in _presets) button.Tag = preset == options.Preset ? "Active" : null;
        foreach (var (padding, button) in _paddings) button.IsChecked = padding == options.Padding;
        foreach (var (radius, button) in _radii) button.IsChecked = radius == options.Radius;
        _shadow.IsChecked = options.Shadow;
    }

    private void Raise(BeautifyOptions next)
    {
        if (next == _options) { Show(_options); return; }
        OptionsChanged?.Invoke(next);
    }

    private StackPanel Row(string labelKey)
    {
        var row = new StackPanel();
        row.SetResourceReference(StyleProperty, "Row");
        var t = new TextBlock();
        Loc.Bind(t, TextBlock.TextProperty, labelKey);
        t.SetResourceReference(StyleProperty, "Label");
        row.Children.Add(t);
        Children.Add(row);
        return row;
    }

    private ToggleButton Toggle(Panel row, Func<string> text, Func<string> tooltip, Func<BeautifyOptions> next)
    {
        var button = new ToggleButton { Focusable = false };
        _texts.Add(() => { button.Content = text(); button.ToolTip = tooltip(); });
        button.SetResourceReference(StyleProperty, "StripButton");
        // ToggleButton already toggled IsChecked; state set by Show.
        button.Click += (_, _) => { button.IsChecked = !button.IsChecked; Raise(next()); };
        row.Children.Add(button);
        return button;
    }
}
