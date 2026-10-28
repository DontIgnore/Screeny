using Screeny.Updates;

namespace Screeny.Tests.Updates;

public class ReleaseNotesTests
{
    [Fact]
    public void Empty_HasNoBlocks()
    {
        Assert.Empty(ReleaseNotes.Parse(null));
        Assert.Empty(ReleaseNotes.Parse("  \n\n"));
    }

    [Fact]
    public void Headings_ListsAndParagraphs()
    {
        var blocks = ReleaseNotes.Parse("""
            # Screeny 0.2.0
            Первая строка
            вторая строка.

            ## Новое
            - Пункт **жирный** и `код`
              продолжение пункта
            * второй
              - вложенный
            1. первый
            2) второй
            ### Мелочи
            """);
        Assert.Collection(blocks,
            b => { Assert.Equal(NoteBlockKind.Heading1, b.Kind); Assert.Equal("Screeny 0.2.0", b.PlainText); },
            b => { Assert.Equal(NoteBlockKind.Paragraph, b.Kind); Assert.Equal("Первая строка вторая строка.", b.PlainText); },
            b => Assert.Equal(NoteBlockKind.Heading2, b.Kind),
            b =>
            {
                Assert.Equal(NoteBlockKind.Bullet, b.Kind);
                Assert.Equal("Пункт жирный и код продолжение пункта", b.PlainText);
                Assert.Contains(b.Inlines, i => i.Bold && i.Text == "жирный");
                Assert.Contains(b.Inlines, i => i.Code && i.Text == "код");
            },
            b => { Assert.Equal(NoteBlockKind.Bullet, b.Kind); Assert.Equal(0, b.Level); },
            b => { Assert.Equal(NoteBlockKind.Bullet, b.Kind); Assert.Equal(1, b.Level); Assert.Equal("вложенный", b.PlainText); },
            b => { Assert.Equal(NoteBlockKind.Numbered, b.Kind); Assert.Equal(1, b.Number); },
            b => { Assert.Equal(NoteBlockKind.Numbered, b.Kind); Assert.Equal(2, b.Number); },
            b => Assert.Equal(NoteBlockKind.Heading3, b.Kind));
    }

    [Fact]
    public void Links_KeepOnlyText_ItalicMarkersRemoved()
    {
        var inlines = ReleaseNotes.ParseInlines("См. [страницу](https://example.com) и *курсив*, snake_case и a__b");
        Assert.Equal("См. страницу и курсив, snake_case и a__b", string.Concat(inlines.Select(i => i.Text)));
        Assert.DoesNotContain(inlines, i => i.Text.Contains("example.com"));
    }

    [Fact]
    public void Escapes_AndUnclosedBold()
    {
        Assert.Equal("2 * 3 = 6", string.Concat(ReleaseNotes.ParseInlines(@"2 \* 3 = 6").Select(i => i.Text)));
        var unclosed = ReleaseNotes.ParseInlines("**не закрыт");
        Assert.Equal("не закрыт", Assert.Single(unclosed).Text);
    }

    [Fact]
    public void HtmlAndFences_AreShownAsText()
    {
        var blocks = ReleaseNotes.Parse("<b>x</b>\n```\ncode line\n```\n---\nпосле");
        Assert.Equal("<b>x</b>", blocks[0].PlainText);
        Assert.True(Assert.Single(blocks[1].Inlines).Code);
        Assert.Equal("после", blocks[2].PlainText);
    }

    [Fact]
    public void Comments_AreNotShown()
    {
        var blocks = ReleaseNotes.Parse("""
            Текст <!-- скрытое --> дальше
            <!-- строка из одного комментария -->
            <!-- многострочный
            комментарий -->
            - пункт <!-- ещё -->
            """);
        Assert.Collection(blocks,
            b => { Assert.Equal(NoteBlockKind.Paragraph, b.Kind); Assert.Equal("Текст  дальше", b.PlainText); },
            b => { Assert.Equal(NoteBlockKind.Bullet, b.Kind); Assert.Equal("пункт", b.PlainText); });
        Assert.All(blocks, b => Assert.Empty(b.Features));
    }

    [Fact]
    public void HeadingTag_AppliesUntilSameOrHigherHeading()
    {
        var blocks = ReleaseNotes.Parse("""
            # Версия
            ## Текст <!-- feature: ocr -->
            Абзац.
            - пункт
            ### Подраздел
            - вложенный раздел
            ## Другое
            - общий
            """);
        Assert.Equal(["", "ocr", "ocr", "ocr", "ocr", "ocr", "", ""], blocks.Select(b => string.Join(",", b.Features)));
        Assert.Equal("Текст", blocks[1].PlainText);
    }

    [Fact]
    public void BulletTag_OverridesHeading_AndCoversNestedItems()
    {
        var blocks = ReleaseNotes.Parse("""
            ## История <!-- feature: history -->
            - про историю
            - про закрепление <!-- feature: pins -->
              - вложенный
            - снова про историю
            ## Общее
            - пункт <!-- Feature: Pins , History -->
              продолжение
            - второй
              продолжение <!-- feature: ocr -->
            """);
        Assert.Equal(["history", "history", "pins", "pins", "history", "", "pins,history", "ocr"],
            blocks.Select(b => string.Join(",", b.Features)));
        Assert.Equal("пункт продолжение", blocks[6].PlainText);
        Assert.Equal("второй продолжение", blocks[7].PlainText);
    }

    [Fact]
    public void Filter_DropsDisabled_AndHeadingsLeftEmpty()
    {
        var blocks = ReleaseNotes.Parse("""
            # Версия
            ## Текст <!-- feature: ocr -->
            - распознавание
            ## Снимки
            ### Пипетка
            - пипетка <!-- feature: pipette -->
            ### Мелочи
            - общий
            - история и закрепление <!-- feature: history, pins -->
            """);
        Func<string, bool> isOn = id => id == "pins";
        var shown = ReleaseNotes.Filter(blocks, isOn);
        Assert.Equal(["Версия", "Снимки", "Мелочи", "общий", "история и закрепление"], shown.Select(b => b.PlainText));

        Assert.Empty(ReleaseNotes.Filter(ReleaseNotes.Parse("# Версия\n## Текст <!-- feature: ocr -->\n- пункт"), _ => false));
        Assert.Equal(3, ReleaseNotes.Filter(blocks.Take(3).ToList(), _ => true).Count);
    }

    [Fact]
    public void Filter_DeeperHeadingIsNestedUnderH3()
    {
        // «####» показывается как «###», но вложен в него: родитель с оставшимся подразделом не пропадает.
        var blocks = ReleaseNotes.Parse("""
            ## Снимки
            ### Инструменты
            #### Пипетка
            - пипетка <!-- feature: pipette -->
            #### Общее
            - общий
            ### Пусто <!-- feature: ocr -->
            #### Вложенный
            - про текст
            """);
        Assert.Equal(4, blocks.Single(b => b.PlainText == "Пипетка").Depth);
        var shown = ReleaseNotes.Filter(blocks, _ => false);
        Assert.Equal(["Снимки", "Инструменты", "Общее", "общий"], shown.Select(b => b.PlainText));
    }

    [Fact]
    public void StandaloneTag_AppliesToNextBlock()
    {
        var blocks = ReleaseNotes.Parse("""
            # Версия
            <!-- feature: ocr -->

            ## Текст
            - распознавание
            ## Общее
            - общий
            <!-- feature: pins -->
            - закрепление
            - снова общий
            <!-- feature: history -->
            Абзац про историю.
            """);
        Assert.Equal(["", "ocr", "ocr", "", "", "pins", "", "history"], blocks.Select(b => string.Join(",", b.Features)));
        Assert.Equal(["Версия", "Общее", "общий", "снова общий"], ReleaseNotes.Filter(blocks, _ => false).Select(b => b.PlainText));
    }

    [Fact]
    public void MultilineComment_HidesFenceMarkers()
    {
        var blocks = ReleaseNotes.Parse("""
            - до
            <!--
            ```
            -->
            - после <!-- feature: ocr -->
            ```
            код
            ```
            """);
        Assert.Collection(blocks,
            b => Assert.Equal("до", b.PlainText),
            b => { Assert.Equal(NoteBlockKind.Bullet, b.Kind); Assert.Equal("после", b.PlainText); Assert.Equal(["ocr"], b.Features); },
            b => { Assert.Equal("код", b.PlainText); Assert.True(Assert.Single(b.Inlines).Code); });
    }

    [Fact]
    public void CommentMarkerInCodeSpan_IsText()
    {
        var blocks = ReleaseNotes.Parse("""
            - метка пишется как `<!-- feature: ocr -->` в строке
            - пункт `<!--` и дальше
            - второй \<!-- не комментарий
            - третий <!-- feature: pins -->
            """);
        Assert.Equal(4, blocks.Count);
        Assert.Equal("метка пишется как <!-- feature: ocr --> в строке", blocks[0].PlainText);
        Assert.Empty(blocks[0].Features);
        Assert.Equal("пункт <!-- и дальше", blocks[1].PlainText);
        Assert.Equal("второй <!-- не комментарий", blocks[2].PlainText);
        Assert.Equal(["pins"], blocks[3].Features);
    }

    private static string Release020 => File.ReadAllText(RepoFile("docs", "releases", "0.2.0.md"));

    [Fact]
    public void Release020_TagsMatchFeatures()
    {
        var blocks = ReleaseNotes.Parse(ReleaseNotes.ForLanguage(Release020, "ru"));
        Assert.DoesNotContain(blocks, b => b.PlainText.Contains("<!--") || b.PlainText.Contains("feature:"));
        string Tags(string startsWith) => string.Join(",", blocks.Single(b => b.PlainText.StartsWith(startsWith, StringComparison.Ordinal)).Features);
        Assert.Equal("ocr", Tags("Текст и коды"));
        Assert.Equal("ocr", Tags("QR и штрихкоды"));
        Assert.Equal("history", Tags("Повторное редактирование"));
        Assert.Equal("history", Tags("Можно хранить оригинал"));
        Assert.Equal("beautify", Tags("Оформление скриншота"));
        Assert.Equal("pipette,ruler", Tags("Пипетка и линейка"));
        Assert.Equal("", Tags("Обводка и заливка"));
        Assert.Equal("", Tags("Снимки сохраняются в фоне"));
        Assert.All(blocks.SelectMany(b => b.Features), id => Assert.True(Screeny.Features.FeatureCatalog.IsKnown(id), id));

        var noOcrHistory = ReleaseNotes.Filter(blocks, id => id is not ("ocr" or "history"));
        Assert.DoesNotContain(noOcrHistory, b => b.PlainText is "Текст и коды" or "История");
        Assert.Contains(noOcrHistory, b => b.PlainText == "Надёжность");
        // Пипетку прячем, только когда выключены обе функции пункта.
        Assert.Contains(ReleaseNotes.Filter(blocks, id => id != "pipette"), b => b.PlainText.StartsWith("Пипетка", StringComparison.Ordinal));
        Assert.DoesNotContain(ReleaseNotes.Filter(blocks, id => id is not ("pipette" or "ruler")), b => b.PlainText.StartsWith("Пипетка", StringComparison.Ordinal));
    }

    [Fact]
    public void Release020_HasEverySupportedLanguage()
        => Assert.Equal(Screeny.Localization.Loc.Supported, ReleaseNotes.Languages(Release020));

    public static TheoryData<string> NotesWithLanguages()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(RepoFile("docs", "releases"), "*.md").Order())
            if (ReleaseNotes.Languages(File.ReadAllText(file)).Count > 0) data.Add(Path.GetFileName(file));
        return data;
    }

    /// <summary>
    /// Разделы языков в любом списке изменений — переводы одного текста: те же блоки того же вида с теми же метками
    /// функций (иначе выключенная функция пряталась бы в одном языке и не пряталась в другом).
    /// </summary>
    [Theory]
    [MemberData(nameof(NotesWithLanguages))]
    public void LanguageSections_HaveTheSameStructureAndTags(string file)
    {
        var notes = File.ReadAllText(RepoFile("docs", "releases", file));
        string Shape(string language) => string.Join("\n", ReleaseNotes.Parse(ReleaseNotes.ForLanguage(notes, language))
            .Select(b => $"{b.Kind}/{b.Level}/{b.Depth}: {string.Join(",", b.Features)}"));
        var languages = ReleaseNotes.Languages(notes);
        var first = Shape(languages[0]);
        foreach (var language in languages) Assert.Equal(first, Shape(language));
    }

    [Fact]
    public void Release020_SectionsAreTheirOwnLanguage()
    {
        // Раздел — только свой язык: в английском нет кириллицы, в русском нет английского заголовка.
        Assert.DoesNotContain(ReleaseNotes.Parse(ReleaseNotes.ForLanguage(Release020, "en")), b => b.PlainText.Any(c => c is >= 'А' and <= 'я'));
        Assert.DoesNotContain(ReleaseNotes.Parse(ReleaseNotes.ForLanguage(Release020, "ru")), b => b.PlainText == "Reliability");
    }

    [Fact]
    public void Release020_SilentUpdateClaimRemoved()
    {
        // Решение владельца: в списке изменений нет фразы о тихой установке и о «ничего не ставится».
        Assert.DoesNotContain("молча,", Release020);
        Assert.DoesNotContain("ничего не ставится", Release020);
        Assert.DoesNotContain("silently", Release020);
    }

    private const string Multi = """
        <!-- lang: ru -->
        # Версия
        - пункт
        <!-- lang: en -->
        # Version
        - item
        <!-- lang: zh -->
        # 版本
        """;

    [Theory]
    [InlineData("ru", "Версия")]
    [InlineData("en", "Version")]
    [InlineData("es", "Version")]     // раздела нет — английский
    [InlineData("zh-Hans", "版本")]   // «zh» подходит для «zh-Hans»
    [InlineData("ja", "Version")]
    public void ForLanguage_PicksSection_ThenEnglish(string language, string heading)
        => Assert.Equal(heading, ReleaseNotes.Parse(ReleaseNotes.ForLanguage(Multi, language))[0].PlainText);

    [Fact]
    public void ForLanguage_WithoutEnglish_TakesTheFirstSection()
    {
        const string notes = "<!-- lang: ru -->\n- первый\n<!-- lang: ja -->\n- 二番目\n";
        Assert.Equal("первый", Assert.Single(ReleaseNotes.Parse(ReleaseNotes.ForLanguage(notes, "es"))).PlainText);
        Assert.Equal("二番目", Assert.Single(ReleaseNotes.Parse(ReleaseNotes.ForLanguage(notes, "ja"))).PlainText);
    }

    [Fact]
    public void ForLanguage_UntaggedNotes_AsIs()
    {
        const string notes = "# Screeny 0.2.1\n- Исправления.";
        Assert.Equal(notes, ReleaseNotes.ForLanguage(notes, "en"));
        Assert.Null(ReleaseNotes.ForLanguage(null, "en"));
        Assert.Empty(ReleaseNotes.Languages(notes));
    }

    [Fact]
    public void ForLanguage_TextBeforeFirstTag_IsShared_AndTagsKeepFeatureMarks()
    {
        const string notes = "# Screeny 0.3.0\n<!-- lang: en -->\n## Text <!-- feature: ocr -->\n- item\n<!-- lang: ru -->\n## Текст <!-- feature: ocr -->\n- пункт\n";
        var ru = ReleaseNotes.Parse(ReleaseNotes.ForLanguage(notes, "ru"));
        Assert.Equal(["Screeny 0.3.0", "Текст", "пункт"], ru.Select(b => b.PlainText));
        Assert.Equal(["ocr"], ru[1].Features);
        Assert.DoesNotContain(ReleaseNotes.Filter(ru, _ => false), b => b.Kind == NoteBlockKind.Bullet);
    }

    [Fact]
    public void ForLanguage_PrefersExactTag_AndTakesOnlyTheFirstMatch()
    {
        const string notes = "<!-- lang: zh -->\n- zh\n<!-- lang: zh-Hans -->\n- zh-Hans\n<!-- lang: en -->\n- en1\n<!-- lang: en -->\n- en2\n";
        Assert.Equal("zh-Hans", Assert.Single(ReleaseNotes.Parse(ReleaseNotes.ForLanguage(notes, "zh-Hans"))).PlainText);
        Assert.Equal("en1", Assert.Single(ReleaseNotes.Parse(ReleaseNotes.ForLanguage(notes, "en"))).PlainText);
        Assert.Equal("en1", Assert.Single(ReleaseNotes.Parse(ReleaseNotes.ForLanguage(notes, "es"))).PlainText);
    }

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("ru-RU", "ru")]
    [InlineData("ja-JP", "ja")]
    public void ForLanguage_RegionalTagServesTheLanguage(string tag, string language)
    {
        var notes = $"<!-- lang: de -->\n- other\n<!-- lang: {tag} -->\n- mine\n";
        Assert.Equal("mine", Assert.Single(ReleaseNotes.Parse(ReleaseNotes.ForLanguage(notes, language))).PlainText);
    }

    [Fact]
    public void ForLanguage_TraditionalChineseDoesNotServeSimplified()
    {
        const string notes = "<!-- lang: en -->\n- en\n<!-- lang: zh-TW -->\n- tw\n";
        Assert.Equal("en", Assert.Single(ReleaseNotes.Parse(ReleaseNotes.ForLanguage(notes, "zh-Hans"))).PlainText);
    }

    [Fact]
    public void Sections_IgnoreTagsInMultilineCommentsAndIndentedCode()
    {
        const string notes = "<!-- lang: en -->\n- one\n<!-- draft\n<!-- lang: ru -->\n    <!-- lang: ja -->\n- two\n   <!-- lang: es -->\n- tres\n";
        Assert.Equal(["en", "es"], ReleaseNotes.Languages(notes));
        Assert.Equal(["one", "two"], ReleaseNotes.Parse(ReleaseNotes.ForLanguage(notes, "en")).Select(b => b.PlainText));
        Assert.Equal("tres", Assert.Single(ReleaseNotes.Parse(ReleaseNotes.ForLanguage(notes, "es"))).PlainText);
    }

    [Fact]
    public void ForLanguage_TagsInCodeBlocksAndInlineDoNotSplit()
    {
        const string notes = "<!-- lang: en -->\n- write `<!-- lang: ru -->` on its own line\n```\n<!-- lang: ru -->\n```\n- still English\n";
        Assert.Equal(["en"], ReleaseNotes.Languages(notes));
        Assert.Contains(ReleaseNotes.Parse(ReleaseNotes.ForLanguage(notes, "ru")), b => b.PlainText == "still English");
    }

    /// <summary>Файл репозитория: поднимаемся от папки сборки тестов до <c>Screeny.sln</c>.</summary>
    internal static string RepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Screeny.sln"))) return Path.Combine([dir.FullName, .. parts]);
        throw new FileNotFoundException("Screeny.sln not found above " + AppContext.BaseDirectory);
    }
}
