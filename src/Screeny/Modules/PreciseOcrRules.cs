using Screeny.Features;
using Screeny.Settings;

namespace Screeny.Modules;

/// <summary>
/// Precise recognition rules. Not a <see cref="FeatureCatalog"/> feature: catalog stores disabled ids, new id
/// would be considered enabled for all. Here opposite — consent (<see cref="AppSettings.PreciseOcr"/>, default off)
/// and module of needed version on disk.
/// </summary>
public static class PreciseOcrRules
{
    /// <summary>Changelog label (<c>&lt;!-- feature: ocr-precise --&gt;</c>), visible only to those with
    /// precise recognition active.</summary>
    public const string NotesTag = "ocr-precise";

    /// <summary>
    /// “Precise recognition active”: “Text and QR recognition” feature enabled, user consented, and installed
    /// module exactly of version this build needs. The only such check — asked by both text mode
    /// and changelog filter.
    /// </summary>
    public static bool IsActive(AppSettings settings, int? installedVersion)
        => settings.IsFeatureOn(FeatureCatalog.Ocr) && settings.PreciseOcr && installedVersion == OcrModule.Version;

    /// <summary>Module installed but wrong version (app updated): Windows OCR works, settings show “Update module”.
    /// Module itself never re-downloaded without pressing.</summary>
    public static bool NeedsModuleUpdate(AppSettings settings, int? installedVersion)
        => settings.IsFeatureOn(FeatureCatalog.Ocr) && settings.PreciseOcr && installedVersion is { } v && v != OcrModule.Version;

    /// <summary>
    /// Wizard offers “Download precise recognition”: module of needed version absent, deletion not waiting for restart (download
    /// would hit busy files), download not running and source exists (dev without <c>--module-source</c> — no).
    /// </summary>
    public static bool WizardOffersDownload(PreciseOcrStatus? status)
        => status is { Ready: false, RemovalPending: false, Downloading: false, SourceAvailable: true };

    /// <summary>
    /// <c>isOn</c> for changelog filter (<see cref="Updates.ReleaseNotes.Filter"/>, <see cref="Updates.UpdatePrompt"/>):
    /// label <see cref="NotesTag"/> — by <see cref="IsActive"/>, others — by disabled features. Without this
    /// <see cref="AppSettings.IsFeatureOn"/> would count unknown id as enabled.
    /// </summary>
    public static Func<string, bool> NotesFilter(AppSettings settings, Func<int?> installedVersion)
        => id => id == NotesTag ? IsActive(settings, installedVersion()) : settings.IsFeatureOn(id);
}
