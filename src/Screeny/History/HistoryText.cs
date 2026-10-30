using System.Text;
using System.Text.Json;

namespace Screeny.History;

/// <summary>
/// Чем распознан текст снимка: движок, версия индексатора и языки OCR. Если отметка снимка не совпадает с текущей
/// (поменяли языки Windows, подняли <see cref="HistoryTextIndexer.IndexVersion"/>), снимок распознаётся заново.
/// </summary>
/// <param name="Languages">Теги языков движка; сравниваются без учёта порядка и регистра.</param>
public sealed record HistoryTextMarker(string Engine, int Version, IReadOnlyList<string> Languages)
{
    public bool SameAs(HistoryTextMarker? other)
        => other is not null && string.Equals(Engine, other.Engine, StringComparison.OrdinalIgnoreCase) && Version == other.Version
           && Normalized(Languages).SequenceEqual(Normalized(other.Languages));

    private static IEnumerable<string> Normalized(IEnumerable<string> languages)
        => languages.Select(l => l.Trim().ToLowerInvariant()).Distinct().OrderBy(l => l, StringComparer.Ordinal);
}

/// <summary>
/// Распознанный текст снимка истории — sidecar <c>&lt;stamp&gt;.text.json</c>:
/// <code>
/// { "version": 1, "engine": "windows-ocr", "indexVersion": 1, "languages": [ "en-US", "ru" ], "text": "…" }
/// </code>
/// Файл необязателен: у старых снимков его нет, их текст распознаётся в фоне. Пустой <c>text</c> — снимок уже
/// распознан, текста в нём нет (иначе пустые снимки распознавались бы при каждом запуске). Текст берётся только из
/// сохранённого снимка с пометками, никогда из <c>.orig.png</c>: размытое пользователем не должно попасть в файл.
/// </summary>
public sealed record HistoryText(string Text, HistoryTextMarker Marker)
{
    public const int FormatVersion = 1;

    public string Serialize()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", FormatVersion);
            w.WriteString("engine", Marker.Engine);
            w.WriteNumber("indexVersion", Marker.Version);
            w.WriteStartArray("languages");
            foreach (var language in Marker.Languages) w.WriteStringValue(language);
            w.WriteEndArray();
            w.WriteString("text", Text);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Разбор файла; null — битый JSON, нет обязательного поля или формат новее поддерживаемого (снимок распознаётся заново).</summary>
    public static HistoryText? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() is < 1 or > FormatVersion) return null;
            if (!root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return null;
            string engine = root.TryGetProperty("engine", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";
            int indexVersion = root.TryGetProperty("indexVersion", out var iv) && iv.ValueKind == JsonValueKind.Number ? iv.GetInt32() : 0;
            var languages = new List<string>();
            if (root.TryGetProperty("languages", out var langs) && langs.ValueKind == JsonValueKind.Array)
                foreach (var l in langs.EnumerateArray())
                    if (l.ValueKind == JsonValueKind.String) languages.Add(l.GetString()!);
            return new HistoryText(text.GetString()!, new HistoryTextMarker(engine, indexVersion, languages));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException) { return null; }
    }

    /// <summary>Читает sidecar; null — файла нет, он не читается или битый.</summary>
    public static HistoryText? TryRead(string? path)
    {
        if (path is null) return null;
        try { return TryParse(File.ReadAllText(path, Encoding.UTF8)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
