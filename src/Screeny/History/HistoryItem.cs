namespace Screeny.History;

/// <param name="Path">Снимок истории.</param>
public sealed record HistoryItem(string Path, DateTime CreatedAt)
{
    /// <summary>Документ аннотаций <c>&lt;stamp&gt;.screeny.json</c>, если он есть.</summary>
    public string? JsonPath { get; init; }

    /// <summary>Оригинал региона без пометок <c>&lt;stamp&gt;.orig.png</c>, если он есть (настройка «Хранить оригиналы»).</summary>
    public string? OriginalPath { get; init; }
}
