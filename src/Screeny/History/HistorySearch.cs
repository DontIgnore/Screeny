using System.Collections.Concurrent;
using System.Text;

namespace Screeny.History;

/// <summary>Снимок для поиска: распознанный текст и прочие данные записи, если они есть.</summary>
/// <param name="Text">Распознанный текст (<see cref="HistoryText"/>); null — ещё не распознан.</param>
/// <param name="Metadata">Другие данные записи для поиска (заголовок окна, программа), если их сохранили; сейчас не сохраняются.</param>
public sealed record HistorySearchEntry(string Path, DateTime CreatedAt, string? Text, string? Metadata = null);

/// <summary>Найденный снимок; оценка от 0 до 1, у разных поставщиков сравнима (берётся наибольшая).</summary>
public sealed record HistorySearchHit(string Path, double Score);

/// <summary>
/// Способ поиска по истории. Первый — по тексту (<see cref="TextSearchProvider"/>); смысловой поиск подключается
/// сюда же отдельным поставщиком, а <see cref="HistorySearch"/> объединяет результаты.
/// </summary>
public interface IHistorySearchProvider
{
    /// <summary>
    /// Подходящие снимки; запрос не пустой. Вызывается вне UI-потока; каждый новый запрос отменяет прежний
    /// (<paramref name="cancellationToken"/>), поэтому долгий поставщик должен его проверять.
    /// </summary>
    Task<IReadOnlyList<HistorySearchHit>> SearchAsync(string query, IReadOnlyList<HistorySearchEntry> entries, CancellationToken cancellationToken);
}

/// <summary>Приведение текста для сравнения: без регистра, без диакритики латиницы и кириллицы (é → e, ё → е), полноширинные знаки — обычные.</summary>
public static class SearchNormalizer
{
    public static string Normalize(string text)
    {
        if (text.Length == 0) return text;
        // KD раскладывает «é» на «e» + знак и превращает полноширинные «Ａ» в «A». Убираем только комбинируемые знаки
        // блока U+0300–U+036F: японские знаки озвончения (U+3099) остаются, иначе «が» совпадало бы с «か».
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (ch is < '̀' or > 'ͯ') sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>Слова запроса после <see cref="Normalize"/>; пустой запрос — пустой список.</summary>
    public static IReadOnlyList<string> Terms(string query)
        => Normalize(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
}

/// <summary>
/// Поиск по распознанному тексту и данным записи: подходит снимок, в котором есть каждое слово запроса (подстрокой,
/// без регистра и диакритики). Приведённый текст кэшируется по пути, пока не поменяется сам текст, поэтому на тысячах
/// снимков поиск — один проход по строкам в памяти.
/// </summary>
public sealed class TextSearchProvider : IHistorySearchProvider
{
    private readonly ConcurrentDictionary<string, (string? Text, string? Metadata, string Normalized)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<HistorySearchHit>> SearchAsync(string query, IReadOnlyList<HistorySearchEntry> entries, CancellationToken cancellationToken)
        => Task.FromResult(Search(query, entries, cancellationToken));

    public IReadOnlyList<HistorySearchHit> Search(string query, IReadOnlyList<HistorySearchEntry> entries, CancellationToken cancellationToken = default)
    {
        var terms = SearchNormalizer.Terms(query);
        var hits = new List<HistorySearchHit>();
        if (terms.Count == 0) return hits;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var haystack = Normalized(entry);
            if (haystack.Length == 0) continue;
            bool all = true;
            foreach (var term in terms)
                if (!haystack.Contains(term, StringComparison.Ordinal)) { all = false; break; }
            if (all) hits.Add(new HistorySearchHit(entry.Path, 1.0));
        }
        return hits;
    }

    private string Normalized(HistorySearchEntry entry)
    {
        if (entry.Text is null && entry.Metadata is null) return "";
        if (_cache.TryGetValue(entry.Path, out var cached)
            && ReferenceEquals(cached.Text, entry.Text) && ReferenceEquals(cached.Metadata, entry.Metadata))
            return cached.Normalized;
        var normalized = SearchNormalizer.Normalize(entry.Metadata is null ? entry.Text! : $"{entry.Text}\n{entry.Metadata}");
        _cache[entry.Path] = (entry.Text, entry.Metadata, normalized);
        return normalized;
    }
}

/// <summary>Объединение поставщиков: у снимка — наибольшая оценка, порядок — по оценке, затем от новых к старым.</summary>
public sealed class HistorySearch(params IHistorySearchProvider[] providers)
{
    /// <summary>Найденные снимки в порядке показа; null — запрос пустой (показывать всё). Поставщики работают параллельно.</summary>
    public async Task<IReadOnlyList<HistorySearchHit>?> SearchAsync(string? query, IReadOnlyList<HistorySearchEntry> entries,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var results = await Task.WhenAll(providers.Select(p => p.SearchAsync(query, entries, cancellationToken)));
        cancellationToken.ThrowIfCancellationRequested();
        var best = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
            foreach (var hit in result)
                if (!best.TryGetValue(hit.Path, out var score) || hit.Score > score) best[hit.Path] = hit.Score;
        var created = entries.ToDictionary(e => e.Path, e => e.CreatedAt, StringComparer.OrdinalIgnoreCase);
        return best
            .Where(kv => created.ContainsKey(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => created[kv.Key])
            .Select(kv => new HistorySearchHit(kv.Key, kv.Value))
            .ToList();
    }
}
