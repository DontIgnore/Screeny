using Screeny.Hotkeys;

namespace Screeny.Migration;

/// <summary>
/// Program that may take Screeny hotkeys.
/// </summary>
/// <param name="Id">Short id (whitelist for <c>--elevated-action</c>): only Latin letters and digits.</param>
/// <param name="ExeNames">Exe names: processes found by them (without “.exe”), Run entries (by command) and startup shortcuts (by target).</param>
/// <param name="AutostartNames">Known Run value names or shortcut file names (“ShareX.lnk”) — second sign if exe in command differs.</param>
/// <param name="UninstallNames">Start of DisplayName in Programs and Features (further — end of string, space, “-” or “(”).</param>
/// <param name="TypicalKeys">Which Screeny hotkeys the program occupies in default settings.</param>
/// <param name="KeysText">Its default keys — for wizard label (PowerToys has own label from UI strings).</param>
/// <param name="DetectByRemapOnly">PowerToys: only Keyboard Manager key remapping interferes, not launch itself.</param>
public sealed record ConflictProgram(
    string Id,
    string DisplayName,
    IReadOnlyList<string> ExeNames,
    IReadOnlyList<string> AutostartNames,
    IReadOnlyList<string> UninstallNames,
    IReadOnlySet<HotkeyKind> TypicalKeys,
    string KeysText,
    bool DetectByRemapOnly = false)
{
    public bool MatchesExe(string? fileName)
        => fileName is not null && ExeNames.Any(e => string.Equals(e, fileName, StringComparison.OrdinalIgnoreCase));

    public bool MatchesProcess(string processName)
        => ExeNames.Any(e => string.Equals(Path.GetFileNameWithoutExtension(e), processName, StringComparison.OrdinalIgnoreCase));

    public bool MatchesAutostart(AutostartEntry entry)
        => MatchesExe(entry.ExeFileName) || AutostartNames.Any(n => string.Equals(n, entry.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>“Lightshot-5.5.0.7”, “Greenshot 1.2.10.6”, “PowerToys (Preview) x64” — yes; “Greenshotter” — no.</summary>
    public bool MatchesUninstall(string displayName)
    {
        foreach (var name in UninstallNames)
        {
            if (!displayName.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (displayName.Length == name.Length) return true;
            char next = displayName[name.Length];
            if (next is ' ' or '-' or '(' or '_' or '.') return true;
        }
        return false;
    }
}

/// <summary>
/// Table of programs interfering with Screeny hotkeys. Signs cross-checked with program sources and installed
/// Lightshot (details in comments near each entry).
/// </summary>
public sealed class ConflictCatalog(IReadOnlyList<ConflictProgram> programs)
{
    private static readonly IReadOnlySet<HotkeyKind> PrintScreenOnly = new HashSet<HotkeyKind> { HotkeyKind.PrintScreen };
    private static readonly IReadOnlySet<HotkeyKind> None = new HashSet<HotkeyKind>();

    public IReadOnlyList<ConflictProgram> Programs { get; } = programs;

    public ConflictProgram? Find(string id) => Programs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    public static ConflictCatalog Default { get; } = new(
    [
        // Typical install: HKLM WOW6432Node Run, value “Lightshot” → “…\Skillbrains\lightshot\Lightshot.exe”;
        // running process — “…\lightshot\5.5.0.7\Lightshot.exe”; Uninstall: “Lightshot-5.5.0.7” (Inno Setup, unins000.exe).
        new("lightshot", "Lightshot", ["Lightshot.exe"], ["Lightshot"], ["Lightshot"], PrintScreenOnly, "PrintScreen"),
        // ShareX/StartupManager.cs: shortcut “ShareX.lnk” in Startup folder with -silent key, flag — HKCU StartupApproved\StartupFolder.
        // Defaults: PrintScreen — full screen, Ctrl+PrintScreen — region, Alt+PrintScreen — window.
        new("sharex", "ShareX", ["ShareX.exe"], ["ShareX.lnk", "ShareX"], ["ShareX"], PrintScreenOnly, "PrintScreen, Ctrl+PrintScreen, Alt+PrintScreen"),
        // Greenshot/Helpers/StartupHelper.cs: Run value “Greenshot” (HKCU, HKLM and WOW6432Node) or shortcut “Greenshot.lnk”.
        new("greenshot", "Greenshot", ["Greenshot.exe"], ["Greenshot", "Greenshot.lnk"], ["Greenshot"], PrintScreenOnly, "PrintScreen, Alt+PrintScreen, Ctrl+PrintScreen"),
        // Snipaste: process Snipaste.exe; F1 — snapshot, F3 — pin. Doesn't intersect Screeny defaults,
        // but is second screenshot tool. Autostart entry name not confirmed — search by exe in command.
        new("snipaste", "Snipaste", ["Snipaste.exe"], ["Snipaste"], ["Snipaste"], None, "F1, F3"),
        // PicPick (NGWIN): PrintScreen — full screen, Alt/Ctrl/Shift+PrintScreen — window and region. Autostart entry name
        // not confirmed — search by exe in command or shortcut target.
        new("picpick", "PicPick", ["picpick.exe"], ["PicPick", "PicPick.lnk"], ["PicPick"], PrintScreenOnly, "PrintScreen, Alt+PrintScreen, Shift+PrintScreen"),
        // PowerToys: doesn't take PrintScreen/Win+Shift+S by default; only remapping those keys
        // in Keyboard Manager interferes. Autostart is scheduler task, disabled in PowerToys settings itself.
        new("powertoys", "PowerToys", ["PowerToys.exe"], [], ["PowerToys"], None, "Keyboard Manager", DetectByRemapOnly: true),
    ]);
}
