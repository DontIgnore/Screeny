namespace Screeny.History;

/// <param name="Path">Screenshot in the history.</param>
public sealed record HistoryItem(string Path, DateTime CreatedAt)
{
    /// <summary>Annotation document <c>&lt;stamp&gt;.screeny.json</c>, if present.</summary>
    public string? JsonPath { get; init; }

    /// <summary>Unannotated original region <c>&lt;stamp&gt;.orig.png</c>, if saved using the Keep Originals setting.</summary>
    public string? OriginalPath { get; init; }
}
