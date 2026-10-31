using System.Windows.Media;
using Screeny.Annotations;
using Screeny.Export;
using Screeny.Features;
using Screeny.Hotkeys;
using Screeny.Localization;
using Screeny.Logging;

namespace Screeny.Settings;

public sealed class AppSettings
{
    public const int MinHistoryLimit = 1, MaxHistoryLimit = 500;
    public const int MinThickness = 1, MaxThickness = 20;

    /// <summary>Settings file format version; <see cref="SettingsMigrator"/> upgrades old files to it.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Format version. File without field — 1 (before hotkey list).</summary>
    public int SettingsVersion { get; set; } = CurrentVersion;

    /// <summary>
    /// Screenshot hotkeys: always exactly one PrintScreen and one WinShiftS entry (after <see cref="Normalize"/>), then custom
    /// combos. At least one entry enabled. Fallback <c>Ctrl+Alt+S</c> not stored here: <see cref="HotkeyManager"/> enables it
    /// when PrintScreen unavailable and nothing else works.
    /// </summary>
    public List<HotkeyBinding> Hotkeys { get; set; } = DefaultHotkeys();

    public static List<HotkeyBinding> DefaultHotkeys() => [HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(false)];
    public string HistoryFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screeny", "History");
    public int HistoryLimit { get; set; } = 50;
    public bool CopyOnCapture { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public string LastColor { get; set; } = "#FF3B30";
    public int LastThickness { get; set; } = 3;
    /// <summary>Hide pinned screenshots from screen capture (SetWindowDisplayAffinity).</summary>
    public bool ExcludePinsFromCapture { get; set; } = false;
    /// <summary>
    /// Store region without marks (<c>.orig.png</c>) next to history screenshot for re-editing.
    /// Default off: original contains what is hidden by blur and pixelate.
    /// </summary>
    public bool KeepOriginals { get; set; } = false;
    /// <summary>
    /// Selection border snap to window edges and contrast edges, shape guides. Default on;
    /// <c>Alt</c> during drag temporarily disables.
    /// </summary>
    public bool SnapEnabled { get; set; } = true;
    /// <summary>
    /// Check updates on GitHub Releases. Screenshots never sent: Screeny goes to network only for updates,
    /// on user request — for precise recognition module (this setting doesn't block its download) and when
    /// user sends error/suggestion.
    /// Default on; shown in settings and first-run wizard.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>
    /// Precise text recognition (PaddleOCR from downloadable module, <see cref="Modules.OcrModule"/>). Consent, not catalog feature:
    /// default off, enabled only by explicit action — module download or settings checkbox.
    /// Works only if module of needed version installed (<see cref="Modules.PreciseOcrRules.IsActive"/>).
    /// </summary>
    public bool PreciseOcr { get; set; } = false;
    /// <summary>
    /// First-run wizard version user has passed (0 — never). Wizard shown if
    /// value less than current wizard version; so appears in portable and after install.
    /// </summary>
    public int SetupWizardVersion { get; set; } = 0;
    /// <summary>
    /// Ids of conflicting programs (<c>Migration.ConflictCatalog</c>) user has seen in wizard or notification.
    /// At startup only new ones notified — those not here.
    /// </summary>
    public List<string> KnownConflicts { get; set; } = new();
    /// <summary>
    /// Ids of disabled features (<see cref="FeatureCatalog"/>). Disabled stored, not enabled: feature from new version
    /// default enabled. Unknown ids (from newer Screeny) kept as-is, affect nothing.
    /// </summary>
    public List<string> DisabledFeatures { get; set; } = new();
    /// <summary>
    /// UI language: <see cref="Loc.Auto"/> (Windows language, default for old files without field) or code from
    /// <see cref="Loc.Supported"/>. Culture name (“ru-RU”) read as language Auto would choose for it,
    /// unknown — as “auto”.
    /// </summary>
    public string Language { get; set; } = Loc.Auto;
    /// <summary>
    /// Random GUID for error/suggestion messages (<c>installId</c>): server uses it only for rate limiting.
    /// Unrelated; created at first real send, before — null. Not GUID — null (created anew).
    /// </summary>
    public string? InstallId { get; set; }

    public bool IsFeatureOn(string id) => !DisabledFeatures.Contains(id);

    public void SetFeature(string id, bool on)
    {
        DisabledFeatures.Remove(id);
        if (!on) DisabledFeatures.Add(id);
    }

    // Last overlay options strip values.
    public const int MinFillOpacity = 10, MaxFillOpacity = 100;
    /// <summary>Stroke for rectangle and ellipse. At least one of stroke/fill enabled.</summary>
    public bool LastStrokeEnabled { get; set; } = true;
    public bool LastFillEnabled { get; set; } = false;
    /// <summary>Fill opacity in percent (fill color — palette color).</summary>
    public int LastFillOpacity { get; set; } = 40;
    public ArrowStyle LastArrowStyle { get; set; } = ArrowStyle.Normal;
    public bool LastTextBackground { get; set; } = false;
    public TextAlign LastTextAlign { get; set; } = TextAlign.Left;

    // Decoration (beautify): default off, state and parameters remembered.
    public bool BeautifyEnabled { get; set; } = false;
    public BeautifyPreset BeautifyPreset { get; set; } = BeautifyOptions.Default.Preset;
    public BeautifyPadding BeautifyPadding { get; set; } = BeautifyOptions.Default.Padding;
    /// <summary>Screenshot corner rounding in pixels: 0, 8 or 16.</summary>
    public int BeautifyRadius { get; set; } = BeautifyOptions.Default.Radius;
    public bool BeautifyShadow { get; set; } = BeautifyOptions.Default.Shadow;

    /// <summary>
    /// Replaces invalid values (null, empty strings, relative folder, unrecognizable color or hotkey,
    /// out-of-range numbers) with defaults. Needed after reading file that may be hand-edited.
    /// </summary>
    public AppSettings Normalize()
    {
        var d = new AppSettings();
        Hotkeys = NormalizeHotkeys(Hotkeys);
        if (!IsValidFolder(HistoryFolder)) HistoryFolder = d.HistoryFolder;
        if (!IsValidColor(LastColor)) LastColor = d.LastColor;
        HistoryLimit = Math.Clamp(HistoryLimit, MinHistoryLimit, MaxHistoryLimit);
        LastThickness = Math.Clamp(LastThickness, MinThickness, MaxThickness);
        LastFillOpacity = Math.Clamp(LastFillOpacity, MinFillOpacity, MaxFillOpacity);
        if (!Enum.IsDefined(LastArrowStyle)) LastArrowStyle = d.LastArrowStyle;
        if (!Enum.IsDefined(LastTextAlign)) LastTextAlign = d.LastTextAlign;
        if (!LastStrokeEnabled && !LastFillEnabled) LastStrokeEnabled = true; // invisible shape
        if (!Enum.IsDefined(BeautifyPreset)) BeautifyPreset = d.BeautifyPreset;
        if (!Enum.IsDefined(BeautifyPadding)) BeautifyPadding = d.BeautifyPadding;
        BeautifyRadius = BeautifyOptions.SnapRadius(BeautifyRadius);
        if (SetupWizardVersion < 0) SetupWizardVersion = 0;
        Language = Loc.NormalizeSetting(Language);
        InstallId = Guid.TryParse(InstallId, out var installId) ? installId.ToString("D") : null;
        KnownConflicts = (KnownConflicts ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        DisabledFeatures = (DisabledFeatures ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        // KeepOriginals — bool without invalid values: missing field stays false (default).
        // SnapEnabled — same: missing field (old settings) stays true.
        return this;
    }

    /// <summary>
    /// Normalizes hotkey list to form expected by settings window, wizard and <see cref="HotkeyManager"/>:
    /// first PrintScreen entry, second WinShiftS (duplicates collapsed, “enabled” if any copy enabled),
    /// then custom combos in canonical form without duplicates; unrecognized and invalid (<see cref="HotkeyRules"/>)
    /// dropped with log entry. Custom “PrintScreen” becomes PrintScreen entry. If nothing enabled —
    /// PrintScreen enabled.
    /// </summary>
    public static List<HotkeyBinding> NormalizeHotkeys(IEnumerable<HotkeyBinding?>? source)
    {
        bool printScreen = false, winShiftS = false;
        var customs = new List<HotkeyBinding>();
        var seen = new HashSet<HotkeySpec>();
        foreach (var b in source ?? [])
        {
            if (b is null) continue;
            switch (b.Kind)
            {
                case HotkeyKind.PrintScreen: printScreen |= b.Enabled; break;
                case HotkeyKind.WinShiftS: winShiftS |= b.Enabled; break;
                case HotkeyKind.Custom:
                    if (b.Spec is null || !HotkeyParser.TryParse(b.Spec, out var spec) || HotkeyRules.Validate(spec) is not null)
                    {
                        Log.Error($"Settings: dropped invalid custom hotkey \"{b.Spec}\"");
                        break;
                    }
                    if (spec == HotkeySpec.PrintScreen) { printScreen |= b.Enabled; break; }
                    if (!seen.Add(spec)) break; // duplicate: first entry wins
                    customs.Add(HotkeyBinding.Custom(spec.ToString(), b.Enabled));
                    break;
                default:
                    Log.Error($"Settings: dropped hotkey of unknown kind {(int)b.Kind}");
                    break;
            }
        }
        if (!printScreen && !winShiftS && !customs.Any(c => c.Enabled)) printScreen = true;
        return [HotkeyBinding.PrintScreen(printScreen), HotkeyBinding.WinShiftS(winShiftS), .. customs];
    }

    /// <summary>
    /// Enables/disables PrintScreen / WinShiftS (for first-run wizard). Custom combos edited via
    /// <see cref="Hotkeys"/>. Like <see cref="NormalizeHotkeys"/>, doesn't leave all hotkeys disabled.
    /// </summary>
    public void SetHotkeyEnabled(HotkeyKind kind, bool enabled)
    {
        var entry = kind switch
        {
            HotkeyKind.PrintScreen => HotkeyBinding.PrintScreen(enabled),
            HotkeyKind.WinShiftS => HotkeyBinding.WinShiftS(enabled),
            _ => throw new ArgumentException("Custom hotkeys are edited through Hotkeys", nameof(kind)),
        };
        Hotkeys = NormalizeHotkeys(Hotkeys.Where(b => b.Kind != kind).Prepend(entry));
    }

    public bool IsHotkeyEnabled(HotkeyKind kind) => Hotkeys.Any(b => b.Kind == kind && b.Enabled);

    private static bool IsValidFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Path.IsPathFullyQualified(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.GetFullPath(path).Length > 0; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool IsValidColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { return ColorConverter.ConvertFromString(text) is Color; }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException) { return false; }
    }
}
