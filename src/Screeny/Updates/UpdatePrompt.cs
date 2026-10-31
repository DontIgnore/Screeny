namespace Screeny.Updates;

public enum UpdatePromptAction
{
    /// <summary>Show window (or notification) with <see cref="UpdatePrompt.Notes"/>.</summary>
    Show,
    /// <summary>Don't ask: download in background and apply without windows on Screeny exit.</summary>
    ApplySilently,
}

/// <summary>
/// How to propose found update considering disabled features.
/// </summary>
/// <param name="Notes">What to show: changelog without items about disabled features.</param>
/// <param name="OnlyDisabledFeatures">All items concern disabled features — short line instead of list.</param>
public sealed record UpdatePrompt(UpdatePromptAction Action, IReadOnlyList<NoteBlock> Notes, bool OnlyDisabledFeatures = false)
{
    /// <summary>
    /// <list type="bullet">
    /// <item>No changelog — normal proposal, as before.</item>
    /// <item>After filter something remains — proposal with only that.</item>
    /// <item>Nothing remains, but items were marked with features (all disabled): automatic check updates
    /// silently on exit, manual still shows window with line “concern only disabled features”.</item>
    /// <item>Nothing remains and no labels (only empty headers) — normal proposal with original list.</item>
    /// </list>
    /// Silent only when everything changed is known: changelog exists for each version (empty or without
    /// content — already doubt) and intermediates known. In doubt — ask.
    /// </summary>
    /// <param name="update">Found update: <see cref="UpdateCandidate.EarlierNotes"/> null — intermediates
    /// unknown, silent not installed.</param>
    /// <param name="language">UI language: from each list its section taken (<see cref="ReleaseNotes.ForLanguage"/>).</param>
    public static UpdatePrompt Decide(UpdateCandidate update, Func<string, bool> isOn, UpdateCheckKind kind, string language = Localization.Loc.Fallback)
        => Decide(update.AllNotes, isOn, kind, allVersionsKnown: update.EarlierNotes is not null, language);

    /// <param name="notes">Changelogs: target and intermediates (each parsed separately,
    /// filtered separately, so label/header of one version doesn't affect another).</param>
    /// <param name="allVersionsKnown"><paramref name="notes"/> — all versions between current and target; false — only part.</param>
    /// <param name="language">Section to show. “Silent” decision independent of language: made only if silent
    /// per each language section (or no sections) — else normal proposal.</param>
    public static UpdatePrompt Decide(IEnumerable<string?> notes, Func<string, bool> isOn, UpdateCheckKind kind, bool allVersionsKnown,
        string language = Localization.Loc.Fallback)
    {
        var list = notes.ToList();
        var prompt = DecideFor(list, isOn, kind, allVersionsKnown, language);
        if (prompt.Action != UpdatePromptAction.ApplySilently) return prompt;
        var languages = list.SelectMany(ReleaseNotes.Languages).Distinct(StringComparer.OrdinalIgnoreCase);
        return languages.All(l => DecideFor(list, isOn, kind, allVersionsKnown, l).Action == UpdatePromptAction.ApplySilently)
            ? prompt
            : prompt with { Action = UpdatePromptAction.Show };
    }

    private static UpdatePrompt DecideFor(List<string?> notes, Func<string, bool> isOn, UpdateCheckKind kind, bool allVersionsKnown, string language)
    {
        var versions = notes.Select(n => ReleaseNotes.Parse(ReleaseNotes.ForLanguage(n, language))).ToList();
        var blocks = versions.SelectMany(v => v).ToList();
        if (blocks.Count == 0) return new(UpdatePromptAction.Show, blocks);
        var visible = versions.SelectMany(v => ReleaseNotes.Filter(v, isOn)).ToList();
        if (visible.Count > 0) return new(UpdatePromptAction.Show, visible);
        if (!ReleaseNotes.IsTagged(blocks)) return new(UpdatePromptAction.Show, blocks);
        // Some version has no list (or only comments) — unknown what's in it: nothing to hide, show as-is.
        bool everythingKnown = allVersionsKnown && versions.All(v => v.Count > 0);
        if (!everythingKnown) return new(UpdatePromptAction.Show, blocks);
        return new(kind == UpdateCheckKind.Manual ? UpdatePromptAction.Show : UpdatePromptAction.ApplySilently, [], OnlyDisabledFeatures: true);
    }
}
