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

    /// <summary>Version immediately after current: no intermediates (known by delta chain).</summary>
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
        var ja = UpdatePrompt.Decide(Next(TwoLanguages), _ => true, UpdateCheckKind.Manual, "ja"); // no section — English
        Assert.Equal(["Screeny 0.2.1", "Pinning fixed."], ja.Notes.Select(b => b.PlainText));
        // All items about disabled — silent, like one language: other sections don't block decision.
        Assert.Equal(UpdatePromptAction.ApplySilently, UpdatePrompt.Decide(Next(TwoLanguages), AllOff, UpdateCheckKind.Automatic, "ru").Action);
    }

    /// <summary>
    /// Silent only if silent per language section: here English has general item, Russian all disabled. Russian UI shows its section (empty, “concerns disabled features”), but asks.
    ///
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

        // Headers without labels: filter would remove all, but nothing to hide — show as-is.
        var headings = UpdatePrompt.Decide(Next("# Screeny 0.2.1"), AllOff, UpdateCheckKind.Automatic);
        Assert.Equal(UpdatePromptAction.Show, headings.Action);
        Assert.Equal("Screeny 0.2.1", Assert.Single(headings.Notes).PlainText);
    }

    [Fact]
    public void EarlierVersions_AreFilteredSeparately()
    {
        // Last version header label “Text and codes” must not hide previous version items without own H1.
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
    [InlineData("<!-- feature: pins -->")] // only comments — no content
    public void EarlierVersionWithoutNotes_IsNotSilent(string? earlier)
    {
        // Intermediate version unknown: in doubt ask, not silent.
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
        // No delta chain: unknown whether target immediately after current. Silent only when known.
        var unknown = new UpdateCandidate("0.2.2", Tagged, EarlierNotes: null);
        var prompt = UpdatePrompt.Decide(unknown, AllOff, UpdateCheckKind.Automatic);
        Assert.Equal(UpdatePromptAction.Show, prompt.Action);
        Assert.Equal(["Screeny 0.2.1", "Текст и коды", "Быстрее распознаётся текст.", "Закреплённый снимок тоже распознаётся."], prompt.Notes.Select(b => b.PlainText));
        Assert.False(prompt.OnlyDisabledFeatures);

        var direct = new UpdateCandidate("0.2.2", Tagged, EarlierNotes: []);
        Assert.Equal(UpdatePromptAction.ApplySilently, UpdatePrompt.Decide(direct, AllOff, UpdateCheckKind.Automatic).Action);

        // What is visible shown even without chain.
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
        Assert.DoesNotContain(prompt.Notes, b => b.PlainText == "Installation and updates"); // only own language section
        Assert.DoesNotContain(prompt.Notes, b => b.PlainText is "Текст и коды" or "История");
    }
}
