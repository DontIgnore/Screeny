namespace Screeny.History;

/// <param name="Path">Снимок истории.</param>
public sealed record HistoryItem(string Path, DateTime CreatedAt)
{
    /// <summary>Документ аннотаций <c>&lt;stamp&gt;.screeny.json</c>, если он есть.</summary>
    public string? JsonPath { get; init; }

    /// <summary>Оригинал региона без пометок <c>&lt;stamp&gt;.orig.png</c>, если он есть (настройка «Хранить оригиналы»).</summary>
    public string? OriginalPath { get; init; }

    /// <summary>Распознанный текст <c>&lt;stamp&gt;.text.json</c> (<see cref="HistoryText"/>), если снимок уже распознан.</summary>
    public string? TextPath { get; init; }

    /// <summary>Векторы смыслового поиска <c>&lt;stamp&gt;.vec</c> (<see cref="Semantic.SemanticVectors"/>), если снимок уже проиндексирован.</summary>
    public string? VectorPath { get; init; }
}
