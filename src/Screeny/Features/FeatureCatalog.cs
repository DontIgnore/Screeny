namespace Screeny.Features;

/// <summary>Disablable feature: id stored in settings and changelog labels (<c>&lt;!-- feature: id --&gt;</c>).</summary>
/// <param name="TitleKey">Title key in UI strings (<see cref="Localization.Loc"/>).</param>
/// <param name="DescriptionKey">Description key.</param>
public sealed record FeatureInfo(string Id, string TitleKey, string DescriptionKey)
{
    public string Title => Localization.Loc.T(TitleKey);
    public string Description => Localization.Loc.T(DescriptionKey);
}

/// <summary>
/// Features user can disable in first-run wizard and settings. Disabled feature disappears
/// entirely: no buttons, keys, menu items, settings sections; its code not run; changelog items about it
/// not shown. Settings store list of disabled (<see cref="Settings.AppSettings.DisabledFeatures"/>),
/// so new features are enabled by default.
/// </summary>
public static class FeatureCatalog
{
    public const string Ocr = "ocr";
    public const string History = "history";
    public const string Pins = "pins";
    public const string Beautify = "beautify";
    public const string Pipette = "pipette";
    public const string Ruler = "ruler";

    public static IReadOnlyList<FeatureInfo> All { get; } =
    [
        new(Ocr, "Feature.Ocr.Title", "Feature.Ocr.Description"),
        new(History, "Feature.History.Title", "Feature.History.Description"),
        new(Pins, "Feature.Pins.Title", "Feature.Pins.Description"),
        new(Beautify, "Feature.Beautify.Title", "Feature.Beautify.Description"),
        new(Pipette, "Feature.Pipette.Title", "Feature.Pipette.Description"),
        new(Ruler, "Feature.Ruler.Title", "Feature.Ruler.Description"),
    ];

    public static bool IsKnown(string id) => All.Any(f => f.Id == id);
}
