using Screeny.History;

namespace Screeny.Tests.History;

/// <summary>Поиск по истории: приведение текста, текстовый поставщик и объединение поставщиков.</summary>
public class HistorySearchTests
{
    private static readonly DateTime T0 = new(2026, 10, 1);

    private static HistorySearchEntry E(string name, string? text, int minutes = 0, string? metadata = null)
        => new(@"C:\h\" + name + ".png", T0.AddMinutes(minutes), text, metadata);

    private static List<string> Find(string query, params HistorySearchEntry[] entries)
        => new HistorySearch(new TextSearchProvider()).Search(query, entries)!.Select(h => Path.GetFileNameWithoutExtension(h.Path)).ToList();

    [Theory]
    [InlineData("Café", "cafe")]
    [InlineData("ÉCOLE", "ecole")]
    [InlineData("Ёлка", "елка")]
    [InlineData("ЙОД", "иод")]
    [InlineData("Ｆｕｌｌ　ｗｉｄｔｈ", "full width")]
    [InlineData("Straße", "straße")]
    public void Normalize_FoldsCaseAndDiacritics(string input, string expected)
        => Assert.Equal(expected, SearchNormalizer.Normalize(input));

    [Fact]
    public void Normalize_KeepsJapaneseVoicingMarks()
        => Assert.NotEqual(SearchNormalizer.Normalize("か"), SearchNormalizer.Normalize("が"));

    [Fact]
    public void AllWordsMustMatch_InAnyOrder()
    {
        var invoice = E("invoice", "INVOICE #1042\nTotal due: 1 250,00 €\nAcme GmbH");
        var error = E("error", "Error 0x80070005\nAccess is denied.");
        Assert.Equal(["invoice"], Find("acme invoice", invoice, error));
        Assert.Equal(["error"], Find("DENIED error", invoice, error));
        Assert.Empty(Find("invoice denied", invoice, error));
    }

    [Fact]
    public void Cyrillic_IsCaseAndYoInsensitive()
    {
        var bill = E("bill", "СЧЁТ НА ОПЛАТУ № 17\nИтого к оплате: 4 500 ₽");
        Assert.Equal(["bill"], Find("счет оплату", bill));
        Assert.Equal(["bill"], Find("ИТОГО", bill));
    }

    [Fact]
    public void Substring_MatchesPartsOfWords()
        => Assert.Equal(["e"], Find("8007", E("e", "Error 0x80070005")));

    [Fact]
    public void NotYetRecognized_NeverMatches()
        => Assert.Empty(Find("anything", E("pending", null)));

    [Fact]
    public void Metadata_IsSearchedToo()
        => Assert.Equal(["shot"], Find("notepad", E("shot", "", metadata: "Untitled - Notepad")));

    [Fact]
    public void EmptyQuery_MeansNoFilter()
    {
        var search = new HistorySearch(new TextSearchProvider());
        Assert.Null(search.Search("", [E("a", "x")]));
        Assert.Null(search.Search("   ", [E("a", "x")]));
    }

    [Fact]
    public void Results_AreNewestFirst()
        => Assert.Equal(["new", "old"], Find("report", E("old", "Q3 report", 0), E("new", "Q4 report", 5)));

    [Fact]
    public void ChangedText_IsReNormalized()
    {
        var provider = new TextSearchProvider();
        Assert.Empty(provider.Search("hello", [E("a", "bye")]));
        Assert.Single(provider.Search("hello", [E("a", "Hello")]));
    }

    private sealed class FixedProvider(params HistorySearchHit[] hits) : IHistorySearchProvider
    {
        public IReadOnlyList<HistorySearchHit> Search(string query, IReadOnlyList<HistorySearchEntry> entries) => hits;
    }

    [Fact]
    public void Providers_AreMerged_ByBestScore()
    {
        var a = E("a", "alpha", 0);
        var b = E("b", "beta", 1);
        var c = E("c", "gamma", 2);
        // Второй поставщик (например, смысловой) находит «a» с высокой оценкой и «c» с низкой.
        var semantic = new FixedProvider(new(a.Path, 0.9), new(c.Path, 0.2), new(@"C:\h\gone.png", 1.0));
        var search = new HistorySearch(new TextSearchProvider(), semantic);
        var hits = search.Search("et", [a, b, c])!; // по тексту — только «beta»
        Assert.Equal([b.Path, a.Path, c.Path], hits.Select(h => h.Path));
        Assert.Equal([1.0, 0.9, 0.2], hits.Select(h => h.Score));
        Assert.DoesNotContain(hits, h => h.Path.EndsWith("gone.png")); // снимка уже нет в истории
    }

    [Fact]
    public void ThousandsOfEntries_SearchStaysFast()
    {
        var entries = Enumerable.Range(0, 5000)
            .Select(i => E($"s{i}", $"Build {i} failed at step {i % 7}. Пользователь получил ошибку доступа к файлу report_{i}.xlsx", i))
            .ToArray();
        var search = new HistorySearch(new TextSearchProvider());
        search.Search("warmup", entries);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var hits = search.Search("ошибку report_4999", entries)!;
        watch.Stop();
        Assert.Equal(@"C:\h\s4999.png", Assert.Single(hits).Path);
        Assert.True(watch.ElapsedMilliseconds < 200, $"{watch.ElapsedMilliseconds} ms");
    }
}
