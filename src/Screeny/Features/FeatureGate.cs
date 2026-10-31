using Screeny.Overlay;
using Screeny.Overlay.Tools;
using Screeny.Settings;

namespace Screeny.Features;

/// <summary>
/// Unified disabled-feature check (<see cref="FeatureCatalog"/>). Asked when building panel and in every
/// key and action handler: hidden button with live key is the main error that must not happen.
/// Settings read anew each time: settings window changes same <see cref="AppSettings"/> object.
/// </summary>
public static class FeatureGate
{
    /// <summary>Tool available: ruler — only with Ruler feature enabled.</summary>
    public static bool ToolAllowed(this AppSettings settings, ToolKind kind)
        => kind != ToolKind.Ruler || settings.IsFeatureOn(FeatureCatalog.Ruler);

    /// <summary>Completion action available: pin — with Pin feature, text — with recognition.</summary>
    public static bool ActionAllowed(this AppSettings settings, CaptureAction action) => action switch
    {
        CaptureAction.Pin => settings.IsFeatureOn(FeatureCatalog.Pins),
        CaptureAction.CopyText => settings.IsFeatureOn(FeatureCatalog.Ocr),
        _ => true,
    };

    /// <summary>
    /// Enter copies screenshot: by setting or always if no history (otherwise screenshot would be lost).
    /// Saved <see cref="AppSettings.CopyOnCapture"/> value unchanged — history enabled, it applies again.
    /// </summary>
    public static bool CopyOnEnter(this AppSettings settings)
        => settings.CopyOnCapture || !settings.IsFeatureOn(FeatureCatalog.History);

    /// <summary>
    /// Settings window fields hidden with their feature save unchanged (whatever hidden
    /// checkboxes say): without history — folder, limit, originals and “Enter copies”; without pin — “Don't capture pinned”;
    /// without text recognition — “Precise recognition”.
    /// So disabling history never looks like disabling originals and never offers to delete them.
    /// </summary>
    public static void KeepHiddenSettings(AppSettings updated, AppSettings current)
    {
        if (!updated.IsFeatureOn(FeatureCatalog.History))
        {
            updated.HistoryFolder = current.HistoryFolder;
            updated.HistoryLimit = current.HistoryLimit;
            updated.KeepOriginals = current.KeepOriginals;
            updated.CopyOnCapture = current.CopyOnCapture;
        }
        if (!updated.IsFeatureOn(FeatureCatalog.Pins)) updated.ExcludePinsFromCapture = current.ExcludePinsFromCapture;
        if (!updated.IsFeatureOn(FeatureCatalog.Ocr)) updated.PreciseOcr = current.PreciseOcr;
    }

    /// <summary>
    /// New disabled-feature list from checkboxes (settings window, wizard). Built from current: ids not in
    /// <paramref name="choices"/> (including unknown from newer version) remain as they were.
    /// </summary>
    public static List<string> MergeDisabled(IEnumerable<string> current, IEnumerable<KeyValuePair<string, bool>> choices)
    {
        var settings = new AppSettings { DisabledFeatures = [.. current] };
        foreach (var (id, on) in choices) settings.SetFeature(id, on);
        return settings.DisabledFeatures;
    }
}
