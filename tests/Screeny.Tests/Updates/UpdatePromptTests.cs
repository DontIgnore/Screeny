using Screeny.Updates;

namespace Screeny.Tests.Updates;

public class UpdatePromptTests
{
    private const string Tagged = """
        # Screeny 0.2.1
        ## Текст и коды <!-- feature: ocr -->
        - Быстрее распознаётся текст.
        - Закреплённый снимок тоже распознаётся. <!-- feature: ocr, pins -->
        """;

    private static bool AllOff(string id) => false;

    /// <summary>Версия сразу за текущей: промежуточных нет (известно по цепочке дельт).</summary>
    private static UpdateCandidate Next(string? notes) => new("0.2.1", notes, EarlierNotes: []);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<!-- feature: ocr -->")]
    public void NoNotes_PromptsAsBefore(string? notes)
    {
        foreach (var kind in new[] { UpdateCheckKind.Automatic, UpdateCheckKind.Manual })
        {
            var prompt = UpdatePrompt.Decide(Next(notes), AllOff, kind);
            Assert.Equal(UpdatePromptAction.Show, prompt.Action);
            Assert.Empty(prompt.Notes);
            Assert.False(prompt.OnlyDisabledFeatures);
        }
    }

    private const string TwoLanguages = """
        <!-- lang: en -->
        # Screeny 0.2.1
        - Pinning fixed. <!-- feature: pins -->
        <!-- lang: ru -->
        # Screeny 0.2.1
        - Исправлено закрепление. <!-- feature: pins -->
        """;

    [Fact]
    public void Language_SelectsSection_BeforeFiltering()
    {
        var ru = UpdatePrompt.Decide(Next(TwoLanguages), _ => true, UpdateCheckKind.Manual, "ru");
        Assert.Equal(["Screeny 0.2.1", "Исправлено закрепление."], ru.Notes.Select(b => b.PlainText));
        var ja = UpdatePrompt.Decide(Next(TwoLanguages), _ => true, UpdateCheckKind.Manual, "ja"); // нет раздела — английский
        Assert.Equal(["Screeny 0.2.1", "Pinning fixed."], ja.Notes.Select(b => b.PlainText));
        // Все пункты про выключенное — молча, как и с одним языком: другие разделы не мешают решению.
        Assert.Equal(UpdatePromptAction.ApplySilently, UpdatePrompt.Decide(Next(TwoLanguages), AllOff, UpdateCheckKind.Automatic, "ru").Action);
    }

    /// <summary>
    /// Молча — только если молча по каждому языковому разделу: здесь в английском есть общий пункт, а в русском всё про
    /// выключенное. Русский интерфейс показывает свой раздел (пустой, «касается выключенных функций»), но спрашивает.
    /// </summary>
    [Fact]
    public void Silent_OnlyWhenEveryLanguageSectionIsSilent()
    {
        const string uneven = """
            <!-- lang: en -->
            # Screeny 0.2.1
            - Pinning fixed. <!-- feature: pins -->
            - General fix.
            <!-- lang: ru -->
            # Screeny 0.2.1
            - Исправлено закрепление. <!-- feature: pins -->
            """;
        foreach (var language in new[] { "ru", "en", "ja" })
            Assert.Equal(UpdatePromptAction.Show, UpdatePrompt.Decide(Next(uneven), AllOff, UpdateCheckKind.Automatic, language).Action);
        var ru = UpdatePrompt.Decide(Next(uneven), AllOff, UpdateCheckKind.Automatic, "ru");
        Assert.True(ru.OnlyDisabledFeatures);
        Assert.Empty(ru.Notes);
        foreach (var language in new[] { "ru", "en", "ja" })
            Assert.Equal(UpdatePromptAction.ApplySilently, UpdatePrompt.Decide(Next(TwoLanguages), AllOff, UpdateCheckKind.Automatic, language).Action);
    }

    [Fact]
    public void SomethingVisible_PromptsWithFilteredNotes()
    {
        var prompt = UpdatePrompt.Decide(Next(Tagged), id => id == "pins", UpdateCheckKind.Automatic);
        Assert.Equal(UpdatePromptAction.Show, prompt.Action);
        Assert.False(prompt.OnlyDisabledFeatures);
        Assert.Equal(["Screeny 0.2.1", "Текст и коды", "Закреплённый снимок тоже распознаётся."], prompt.Notes.Select(b => b.PlainText));
    }

    [Fact]
    public void OnlyDisabledFeatures_AutomaticAppliesSilently()
    {
        var prompt = UpdatePrompt.Decide(Next(Tagged), AllOff, UpdateCheckKind.Automatic);
        Assert.Equal(UpdatePromptAction.ApplySilently, prompt.Action);
        Assert.Empty(prompt.Notes);
        Assert.True(prompt.OnlyDisabledFeatures);
    }

    [Fact]
    public void OnlyDisabledFeatures_ManualStillShowsWindow()
    {
        var prompt = UpdatePrompt.Decide(Next(Tagged), AllOff, UpdateCheckKind.Manual);
        Assert.Equal(UpdatePromptAction.Show, prompt.Action);
        Assert.Empty(prompt.Notes);
        Assert.True(prompt.OnlyDisabledFeatures);
    }

    [Fact]
    public void Untagged_AlwaysShownInFull()
    {
        var prompt = UpdatePrompt.Decide(Next("# Screeny 0.2.1\n- Исправления."), AllOff, UpdateCheckKind.Automatic);
        Assert.Equal(UpdatePromptAction.Show, prompt.Action);
        Assert.Equal(2, prompt.Notes.Count);

        // Одни заголовки без меток: фильтр убрал бы всё, но прятать нечего — показываем как есть.
        var headings = UpdatePrompt.Decide(Next("# Screeny 0.2.1"), AllOff, UpdateCheckKind.Automatic);
        Assert.Equal(UpdatePromptAction.Show, headings.Action);
        Assert.Equal("Screeny 0.2.1", Assert.Single(headings.Notes).PlainText);
    }

    [Fact]
    public void EarlierVersions_AreFilteredSeparately()
    {
        // Метка заголовка «Текст и коды» последней версии не должна спрятать пункты предыдущей версии без своего H1.
        var update = new UpdateCandidate("0.2.2", Tagged, EarlierNotes: ["- Исправлено закрепление. <!-- feature: pins -->", "- Общее."]);
        var prompt = UpdatePrompt.Decide(update, AllOff, UpdateCheckKind.Automatic);
        Assert.Equal(UpdatePromptAction.Show, prompt.Action);
        Assert.Equal("Общее.", Assert.Single(prompt.Notes).PlainText);

        var allOff = new UpdateCandidate("0.2.2", Tagged, EarlierNotes: ["- Исправлено закрепление. <!-- feature: pins -->"]);
        Assert.Equal(UpdatePromptAction.ApplySilently, UpdatePrompt.Decide(allOff, AllOff, UpdateCheckKind.Automatic).Action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("<!-- feature: pins -->")] // одни комментарии — содержимого нет
    public void EarlierVersionWithoutNotes_IsNotSilent(string? earlier)
    {
        // Про промежуточную версию ничего не известно: в сомнении спрашиваем, а не ставим молча.
        var update = new UpdateCandidate("0.2.2", Tagged, EarlierNotes: ["- Исправлено закрепление. <!-- feature: pins -->", earlier]);
        var prompt = UpdatePrompt.Decide(update, AllOff, UpdateCheckKind.Automatic);
        Assert.Equal(UpdatePromptAction.Show, prompt.Action);
        Assert.False(prompt.OnlyDisabledFeatures);
        Assert.False(UpdatePrompt.Decide(update, AllOff, UpdateCheckKind.Manual).OnlyDisabledFeatures);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void TargetWithoutNotes_IsNotSilent(string? target)
    {
        var update = new UpdateCandidate("0.2.2", target, EarlierNotes: ["- Исправлено закрепление. <!-- feature: pins -->"]);
        Assert.Equal(UpdatePromptAction.Show, UpdatePrompt.Decide(update, AllOff, UpdateCheckKind.Automatic).Action);
    }

    [Fact]
    public void UnknownIntermediateVersions_AreNotSilent()
    {
        // Цепочки дельт нет: неизвестно, сразу ли целевая за текущей. Молча — только когда это известно.
        var unknown = new UpdateCandidate("0.2.2", Tagged, EarlierNotes: null);
        var prompt = UpdatePrompt.Decide(unknown, AllOff, UpdateCheckKind.Automatic);
        Assert.Equal(UpdatePromptAction.Show, prompt.Action);
        Assert.Equal(["Screeny 0.2.1", "Текст и коды", "Быстрее распознаётся текст.", "Закреплённый снимок тоже распознаётся."], prompt.Notes.Select(b => b.PlainText));
        Assert.False(prompt.OnlyDisabledFeatures);

        var direct = new UpdateCandidate("0.2.2", Tagged, EarlierNotes: []);
        Assert.Equal(UpdatePromptAction.ApplySilently, UpdatePrompt.Decide(direct, AllOff, UpdateCheckKind.Automatic).Action);

        // То, что видно, показывается и без цепочки.
        var visible = UpdatePrompt.Decide(unknown, id => id == "ocr", UpdateCheckKind.Automatic);
        Assert.Equal(UpdatePromptAction.Show, visible.Action);
        Assert.NotEmpty(visible.Notes);
    }

    [Fact]
    public void Release020_WithEverythingOff_StillShowsGeneralChanges()
    {
        var notes = File.ReadAllText(ReleaseNotesTests.RepoFile("docs", "releases", "0.2.0.md"));
        var prompt = UpdatePrompt.Decide(Next(notes), AllOff, UpdateCheckKind.Automatic, "ru");
        Assert.Equal(UpdatePromptAction.Show, prompt.Action);
        Assert.Contains(prompt.Notes, b => b.PlainText == "Установка и обновления");
        Assert.DoesNotContain(prompt.Notes, b => b.PlainText == "Installation and updates"); // только раздел своего языка
        Assert.DoesNotContain(prompt.Notes, b => b.PlainText is "Текст и коды" or "История");
    }
}
