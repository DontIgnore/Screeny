using System.Collections.Concurrent;
using Screeny.History;

namespace Screeny.Semantic;

/// <summary>
/// Смысловой поиск по истории: запрос превращается в вектор один раз и сравнивается с обоими векторами каждого снимка
/// (картинка и распознанный текст, берётся большее). Сходства EmbeddingGemma 2 сжаты (подходящие ≈0,62–0,78, остальные
/// ≈0,55–0,70), поэтому порога по абсолютной величине нет: остаются снимки не хуже лучшего на <see cref="Window"/> и не
/// больше <see cref="MaxHits"/>. Оценки — меньше 1: точные совпадения текста (<see cref="TextSearchProvider"/>, 1,0) в
/// <see cref="HistorySearch"/> идут первыми, затем найденное по смыслу — по сходству.
/// Векторы читаются из sidecar-файлов один раз и кэшируются по пути; индексатор обновляет кэш (<see cref="Update"/>).
/// </summary>
public sealed class SemanticSearchProvider(SemanticService service) : IHistorySearchProvider
{
    /// <summary>Насколько хуже лучшего снимка может быть найденный.</summary>
    public const double Window = 0.06;
    public const int MaxHits = 12;

    private readonly ConcurrentDictionary<string, SemanticVectors?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Новые векторы снимка (индексатор), null — забыть (снимок удалён или переиндексируется).</summary>
    public void Update(string path, SemanticVectors? vectors) => _cache[path] = vectors;

    /// <summary>Забыть все векторы (модуль удалён, папка истории сменилась).</summary>
    public void Clear() => _cache.Clear();

    public async Task<IReadOnlyList<HistorySearchHit>> SearchAsync(string query, IReadOnlyList<HistorySearchEntry> entries, CancellationToken cancellationToken)
    {
        if (!service.IsActive || entries.Count == 0) return [];
        var items = entries.Select(e => (e.Path, Vectors: Vectors(e.Path))).ToList();
        // Ещё ничего не проиндексировано: модель для запроса (≈0,3 ГБ) не загружается зря.
        var marker = SemanticService.CurrentMarker;
        if (!items.Any(i => i.Vectors?.Marker == marker)) return [];
        var q = await service.EmbedQueryAsync(query, cancellationToken);
        if (q is null) return [];
        cancellationToken.ThrowIfCancellationRequested();
        return Rank(q, items, cancellationToken);
    }

    /// <summary>Отбор и порядок: не хуже лучшего на <see cref="Window"/>, не больше <see cref="MaxHits"/>, оценка в (0; 0,99].</summary>
    public static IReadOnlyList<HistorySearchHit> Rank(float[] query, IEnumerable<(string Path, SemanticVectors? Vectors)> items, CancellationToken ct = default)
    {
        var marker = SemanticService.CurrentMarker;
        var scored = new List<(string Path, double Score)>();
        foreach (var (path, vectors) in items)
        {
            ct.ThrowIfCancellationRequested();
            if (vectors is null || vectors.Marker != marker) continue;
            double s = vectors.Score(query);
            if (!double.IsNegativeInfinity(s)) scored.Add((path, s));
        }
        if (scored.Count == 0) return [];
        double best = scored.Max(x => x.Score);
        return scored
            .Where(x => x.Score >= best - Window)
            .OrderByDescending(x => x.Score)
            .Take(MaxHits)
            .Select(x => new HistorySearchHit(x.Path, Math.Clamp(x.Score, 0.01, 0.99)))
            .ToList();
    }

    private SemanticVectors? Vectors(string path)
        => _cache.GetOrAdd(path, p => SemanticVectors.TryRead(HistoryStore.VectorPath(p)));
}
