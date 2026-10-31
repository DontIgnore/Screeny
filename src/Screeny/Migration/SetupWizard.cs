using Screeny.Hotkeys;
using Screeny.Settings;

namespace Screeny.Migration;

/// <summary>What is selected in wizard hotkey section.</summary>
public sealed record WizardHotkeyChoice(bool PrintScreen, bool WinShiftS, HotkeySpec? Custom);

/// <summary>First-run wizard rules: when to show, what counts as new conflict, which hotkey list to apply.</summary>
public static class SetupWizard
{
    /// <summary>
    /// Current wizard version. Raise to show wizard again to all who already passed.
    /// 2 — wizard gained feature choice: installed copies see it once.
    /// </summary>
    public const int Version = 2;

    /// <summary>Show wizard at startup: user hasn't passed (or skipped) this version.</summary>
    public static bool ShouldShowOnStartup(AppSettings settings) => settings.SetupWizardVersion < Version;

    /// <summary>
    /// Conflicts worth reminding via notification after passed wizard: program conflicts with enabled hotkey
    /// and wasn't among known (shown in wizard or previous notification).
    /// </summary>
    public static IReadOnlyList<Conflict> NewBlockingConflicts(IReadOnlyList<Conflict> conflicts, IEnumerable<HotkeyBinding> hotkeys,
        IEnumerable<string> known)
    {
        var knownSet = new HashSet<string>(known, StringComparer.Ordinal);
        var list = hotkeys.ToList();
        return conflicts.Where(c => !knownSet.Contains(c.Id) && c.Blocks(list)).ToList();
    }

    /// <summary>What to do at startup.</summary>
    public enum StartupAction { Nothing, ShowWizard, CheckForNewConflicts }

    /// <summary>
    /// Wizard — on explicit <c>--setup-wizard</c> or if working instance hasn't passed it; else working instance
    /// checks new conflicts in background. Test instance without key shows nothing and doesn't check real environment.
    /// </summary>
    public static StartupAction OnStartup(bool isDev, bool wizardRequested, AppSettings settings)
        => wizardRequested ? StartupAction.ShowWizard
            : isDev ? StartupAction.Nothing
            : ShouldShowOnStartup(settings) ? StartupAction.ShowWizard
            : StartupAction.CheckForNewConflicts;

    /// <summary>
    /// Startup check: only still-found programs remain known (fixed and returned counts as new); new ones
    /// conflicting with enabled hotkey are returned for notification and immediately become known.
    /// </summary>
    public static (IReadOnlyList<Conflict> Fresh, List<string> Known) CheckForNewConflicts(IReadOnlyList<Conflict> conflicts,
        IEnumerable<HotkeyBinding> hotkeys, IEnumerable<string> known)
    {
        var present = conflicts.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var stillKnown = known.Where(present.Contains).Distinct(StringComparer.Ordinal).ToList();
        var fresh = NewBlockingConflicts(conflicts, hotkeys, stillKnown);
        return (fresh, [.. stillKnown, .. fresh.Select(c => c.Id)]);
    }

    /// <summary>Tray notification text: “PrintScreen is busy by Lightshot — open wizard?”</summary>
    public static string NotificationText(IReadOnlyList<Conflict> conflicts, IEnumerable<HotkeyBinding> hotkeys)
    {
        var list = hotkeys.ToList();
        var first = conflicts[0];
        var key = list.Where(h => h.Enabled && first.Keys.Contains(h.Kind)).Select(h => h.DisplayName).FirstOrDefault() ?? Localization.Loc.T("Wizard.HotkeyGeneric");
        var names = string.Join(Localization.Loc.ListSeparator, conflicts.Select(c => c.Program.DisplayName));
        return Localization.Loc.F(conflicts.Count == 1 ? "Wizard.NotifyOne" : "Wizard.NotifyMany", key, names);
    }

    /// <summary>
    /// Hotkey list after wizard: PrintScreen and Win+Shift+S by checkboxes, custom combo from field added (enabled)
    /// to configured ones; other custom combos untouched. Normalization in <see cref="AppSettings.NormalizeHotkeys"/>.
    /// </summary>
    public static List<HotkeyBinding> BuildHotkeys(IEnumerable<HotkeyBinding> current, WizardHotkeyChoice choice)
    {
        var customs = current.Where(b => b.Kind == HotkeyKind.Custom).ToList();
        if (choice.Custom is { } spec)
        {
            customs.RemoveAll(b => b.ToRegisterSpec() == spec);
            customs.Add(HotkeyBinding.Custom(spec.ToString(), enabled: true));
        }
        return AppSettings.NormalizeHotkeys([HotkeyBinding.PrintScreen(choice.PrintScreen), HotkeyBinding.WinShiftS(choice.WinShiftS), .. customs]);
    }

    /// <summary>At least one hotkey enabled (otherwise screenshot only from tray).</summary>
    public static bool HasAnyHotkey(WizardHotkeyChoice choice, IEnumerable<HotkeyBinding> current)
        => choice.PrintScreen || choice.WinShiftS || choice.Custom is not null || current.Any(b => b.Kind == HotkeyKind.Custom && b.Enabled);
}
