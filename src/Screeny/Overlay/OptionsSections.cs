using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Overlay.Tools;

namespace Screeny.Overlay;

/// <summary>
/// Options section. Tool and shape sections shown by <see cref="OptionsPanelControl"/> in flyout at tool button
/// or next to shape; decoration — <see cref="BeautifySection"/> in flyout at ✨ button.
/// </summary>
public enum OptionsSectionKind
{
    /// <summary>Rectangle and ellipse: “Stroke” and “Fill” swatches.</summary>
    Shape,
    /// <summary>Arrow: four styles.</summary>
    Arrow,
    /// <summary>Text: “Text” and “Background” swatches.</summary>
    Text,
    /// <summary>Decoration (beautify): background gradient, margin, rounding, shadow. Shown while decoration enabled.</summary>
    Beautify,
}

/// <summary>What palette click (and eyedropper) paints.</summary>
public enum PaintTarget
{
    /// <summary>Main color: shape stroke, text color, line etc.</summary>
    Color,
    /// <summary>Rectangle/ellipse fill.</summary>
    Fill,
    /// <summary>Text background.</summary>
    Background,
}

/// <summary>
/// Everything section set depends on. Extends with fields (e.g., <c>bool Beautify</c>) without signature change.
/// <see cref="OptionsSections.For"/>.
/// </summary>
public sealed record OptionsContext(ToolKind Tool, Annotation? Selected)
{
    /// <summary>Decoration enabled: its section goes after tool or shape section.</summary>
    public bool Beautify { get; init; }
}

public static class OptionsSections
{
    /// <summary>
    /// Sections in order. Selected shape beats tool: line selected with active rectangle — no sections. Empty list — no options (palette paints main color).
    ///
    /// </summary>
    public static IReadOnlyList<OptionsSectionKind> For(OptionsContext context)
    {
        var main = context.Selected is { } selected ? ForAnnotation(selected) : ForTool(context.Tool);
        var sections = new List<OptionsSectionKind>(2);
        if (main is { } kind) sections.Add(kind);
        if (context.Beautify) sections.Add(OptionsSectionKind.Beautify);
        return sections;
    }

    public static OptionsSectionKind? ForTool(ToolKind tool) => tool switch
    {
        ToolKind.Rect or ToolKind.Ellipse => OptionsSectionKind.Shape,
        ToolKind.Arrow => OptionsSectionKind.Arrow,
        ToolKind.Text => OptionsSectionKind.Text,
        _ => null,
    };

    /// <summary>Only rectangle and ellipse, not any <see cref="BoxAnnotation"/>: effects have no color.</summary>
    public static OptionsSectionKind? ForAnnotation(Annotation a) => a switch
    {
        RectAnnotation or EllipseAnnotation => OptionsSectionKind.Shape,
        ArrowAnnotation => OptionsSectionKind.Arrow,
        TextAnnotation => OptionsSectionKind.Text,
        _ => null,
    };

    /// <summary>Color swatches section has.</summary>
    public static IReadOnlyList<PaintTarget> Targets(OptionsSectionKind kind) => kind switch
    {
        OptionsSectionKind.Shape => [PaintTarget.Color, PaintTarget.Fill],
        OptionsSectionKind.Text => [PaintTarget.Color, PaintTarget.Background],
        _ => [],
    };

    /// <summary>Active swatch if present in shown sections; else palette paints main color.</summary>
    public static PaintTarget EffectiveTarget(IReadOnlyList<OptionsSectionKind> sections, PaintTarget active)
        => active == PaintTarget.Color || sections.Any(s => Targets(s).Contains(active)) ? active : PaintTarget.Color;
}
