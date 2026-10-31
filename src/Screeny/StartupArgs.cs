using System.Globalization;
using Screeny.Overlay;

namespace Screeny;

/// <summary>
/// Command-line keys.
/// <list type="bullet">
/// <item><c>--settings</c>, <c>--history</c> — open window right after startup.</item>
/// <item><c>--dev</c> — test instance: own mutex/event names (suffix <see cref="DevSuffix"/>), own settings file
/// and history folder, no hotkey registration, no screen capture at startup or tray click.</item>
/// <item><c>--overlay-test &lt;png&gt;</c> (only with <c>--dev</c>) — open overlay on image as if screenshot.</item>
/// <item><c>--select x,y,w,h</c> (only with <c>--overlay-test</c>) — ready selection.</item>
/// <item><c>--edit &lt;png&gt;</c> (only with <c>--dev</c>) — re-edit window like from history.</item>
/// <item><c>--scenario &lt;name&gt;</c> (with <c>--overlay-test</c>, <c>--edit</c>, <c>--history</c> or <c>--settings</c>) — ready window state
/// for UI verification without mouse input: overlay shapes set, <c>original</c> for editor, <c>hover</c> for history,
/// <c>hotkey-problems</c> for settings, <c>conflicts</c> for first-run wizard.</item>
/// <item><c>--setup-wizard</c> — open first-run wizard (conflicts with other programs) right after startup.
/// In <c>--dev</c> wizard works only with fake environment and fake actions; without key dev instance never shows wizard.</item>
///
/// <item><c>--enable-ll-hook</c> (only with <c>--dev</c>) — allow test instance low-level Win+Shift+S hook
/// (if enabled in settings). Without key dev instance never installs hook.</item>
/// <item><c>--update-source &lt;folder&gt;</c> (only with <c>--dev</c>) — search updates in local releases folder
/// (result of <c>tools/pack.ps1</c>) instead of GitHub. Without key dev instance never checks updates.</item>
/// <item><c>--check-updates</c> (only with <c>--dev</c>) — manual update check right after startup.</item>
/// <item><c>--module-source &lt;folder&gt;</c> (only with <c>--dev</c>) — take precise recognition module archive
/// (<c>Screeny-OcrModule-N.zip</c>, result of <c>tools/pack-ocr-module.ps1</c>) from local folder instead of GitHub. Without this
/// key dev instance never downloads module.</item>
/// <item><c>--feedback-endpoint &lt;url&gt;</c> (only with <c>--dev</c>) — where to send error/suggestion messages
/// (own test server). Without key dev instance never sends, “Send” says address not set.</item>
/// <item><c>--feedback bug|idea</c> — open error/suggestion window right after startup. With <c>--dev</c> and
/// <c>--scenario</c> (<c>sending</c>, <c>sent</c>, <c>offline</c>, <c>rate-limited</c>, <c>invalid</c>, <c>server</c>,
/// <c>validation</c>, <c>details</c>, <c>not-configured</c>) — ready state with fake send, without network.</item>
/// <item>Environment variable <c>SCREENY_DEV=1</c> — same as <c>--dev</c>: survives restart via
/// Velopack stub and Update.exe, so test copy doesn't become working instance.</item>
/// </list>
/// Without <c>--dev</c> test keys ignored: working instance doesn't open overlay on foreign file.
/// </summary>
public sealed record StartupArgs(bool Dev, bool OpenSettings, bool OpenHistory, string? OverlayTest, PixelRect? Select, string? Scenario, string? Edit = null, bool EnableLlHook = false,
    string? UpdateSource = null, bool CheckUpdates = false, bool SetupWizard = false, string? ModuleSource = null,
    string? FeedbackEndpoint = null, string? Feedback = null)
{
    public const string DevSuffix = ".Dev";
    public const string DevEnvironmentVariable = "SCREENY_DEV";

    /// <summary>Keys of current process accounting for <see cref="DevEnvironmentVariable"/>.</summary>
    public static StartupArgs FromProcess(IReadOnlyList<string> args)
        => Parse(args, Environment.GetEnvironmentVariable(DevEnvironmentVariable));

    /// <summary>Suffix for kernel object and file names of test instance.</summary>
    public string Suffix => Dev ? DevSuffix : "";

    /// <param name="devEnvironment">Value of <see cref="DevEnvironmentVariable"/>: “1” or “true” enable dev mode.</param>
    public static StartupArgs Parse(IReadOnlyList<string> args, string? devEnvironment = null)
    {
        bool dev = devEnvironment?.Trim() is "1" || string.Equals(devEnvironment?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        bool settings = false, history = false, llHook = false, checkUpdates = false, wizard = false;
        string? overlay = null, scenario = null, edit = null, updateSource = null, moduleSource = null, feedbackEndpoint = null, feedback = null;
        PixelRect? select = null;
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            if (Is(a, "--dev")) dev = true;
            else if (Is(a, "--settings")) settings = true;
            else if (Is(a, "--history")) history = true;
            else if (Is(a, "--overlay-test")) overlay = Next();
            else if (Is(a, "--scenario")) scenario = Next();
            else if (Is(a, "--select")) select = TryParseRect(Next());
            else if (Is(a, "--edit")) edit = Next();
            else if (Is(a, "--enable-ll-hook")) llHook = true;
            else if (Is(a, "--update-source")) updateSource = Next();
            else if (Is(a, "--check-updates")) checkUpdates = true;
            else if (Is(a, "--setup-wizard")) wizard = true;
            else if (Is(a, "--module-source")) moduleSource = Next();
            else if (Is(a, "--feedback-endpoint")) feedbackEndpoint = Next();
            else if (Is(a, "--feedback")) feedback = Next()?.Trim().ToLowerInvariant();
        }
        if (!dev) (overlay, select, scenario, edit, llHook, updateSource, checkUpdates, moduleSource, feedbackEndpoint) = (null, null, null, null, false, null, false, null, null);
        if (string.IsNullOrWhiteSpace(feedbackEndpoint)) feedbackEndpoint = null;
        if (feedback is not ("bug" or "idea")) feedback = null;
        if (string.IsNullOrWhiteSpace(updateSource)) updateSource = null;
        if (string.IsNullOrWhiteSpace(moduleSource)) moduleSource = null;
        if (string.IsNullOrWhiteSpace(overlay)) (overlay, select) = (null, null);
        if (string.IsNullOrWhiteSpace(edit)) edit = null;
        if (overlay is null && edit is null && !history && !settings && !wizard && feedback is null) scenario = null;
        return new StartupArgs(dev, settings, history, overlay, select, scenario, edit, llHook, updateSource, checkUpdates, wizard, moduleSource,
            feedbackEndpoint, feedback);
    }

    private static bool Is(string a, string key) => string.Equals(a, key, StringComparison.OrdinalIgnoreCase);

    /// <summary>“x,y,w,h” in image pixels; null if format invalid or size not positive.</summary>
    public static PixelRect? TryParseRect(string? text)
    {
        var parts = text?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 4 }) return null;
        var v = new int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i])) return null;
        return v[2] > 0 && v[3] > 0 ? new PixelRect(v[0], v[1], v[2], v[3]) : null;
    }
}
