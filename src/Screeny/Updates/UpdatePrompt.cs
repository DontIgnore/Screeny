namespace Screeny.Updates;

public enum UpdatePromptAction
{
    /// <summary>Показать окно (или уведомление) с <see cref="UpdatePrompt.Notes"/>.</summary>
    Show,
    /// <summary>Не спрашивать: скачать в фоне и применить без окон при выходе из Screeny.</summary>
    ApplySilently,
}

/// <summary>
/// Как предложить найденное обновление с учётом выключенных функций.
/// </summary>
/// <param name="Notes">Что показать: список изменений без пунктов про выключенные функции.</param>
/// <param name="OnlyDisabledFeatures">Все пункты касаются выключенных функций — вместо списка короткая строка.</param>
public sealed record UpdatePrompt(UpdatePromptAction Action, IReadOnlyList<NoteBlock> Notes, bool OnlyDisabledFeatures = false)
{
    /// <summary>
    /// <list type="bullet">
    /// <item>Списка изменений нет — обычное предложение, как раньше.</item>
    /// <item>После фильтра что-то осталось — предложение только с этим.</item>
    /// <item>Ничего не осталось, но пункты были отмечены функциями (все выключены): автоматическая проверка обновляет
    /// молча при выходе, ручная всё равно показывает окно со строкой «касаются только выключенных функций».</item>
    /// <item>Ничего не осталось и меток нет (одни пустые заголовки) — обычное предложение с исходным списком.</item>
    /// </list>
    /// Молча — только когда известно всё, что изменилось: список изменений есть у каждой версии (пустой или без
    /// содержимого — уже сомнение) и промежуточные версии известны. В сомнении — спрашиваем.
    /// </summary>
    /// <param name="update">Найденное обновление: <see cref="UpdateCandidate.EarlierNotes"/> null — промежуточные версии
    /// неизвестны, и молча оно не ставится.</param>
    /// <param name="language">Язык интерфейса: из каждого списка берётся его раздел (<see cref="ReleaseNotes.ForLanguage"/>).</param>
    public static UpdatePrompt Decide(UpdateCandidate update, Func<string, bool> isOn, UpdateCheckKind kind, string language = Localization.Loc.Fallback)
        => Decide(update.AllNotes, isOn, kind, allVersionsKnown: update.EarlierNotes is not null, language);

    /// <param name="notes">Списки изменений: целевой версии и промежуточных (каждый разбирается отдельно,
    /// и фильтруется отдельно, чтобы метка или заголовок одной версии не влияли на другую).</param>
    /// <param name="allVersionsKnown"><paramref name="notes"/> — все версии между текущей и целевой; false — только часть.</param>
    /// <param name="language">Раздел для показа. Решение «молча» от языка не зависит: оно принимается, только если молча
    /// выходит по каждому языковому разделу (или разделов нет) — иначе обычное предложение.</param>
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
        // У какой-то версии списка нет (или в нём одни комментарии) — неизвестно, что в ней: прятать нечего, показываем как есть.
        bool everythingKnown = allVersionsKnown && versions.All(v => v.Count > 0);
        if (!everythingKnown) return new(UpdatePromptAction.Show, blocks);
        return new(kind == UpdateCheckKind.Manual ? UpdatePromptAction.Show : UpdatePromptAction.ApplySilently, [], OnlyDisabledFeatures: true);
    }
}
