using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Screeny.Feedback;

/// <summary>Error message or suggestion: determines window hints and GitHub issue template.</summary>
public enum FeedbackKind { Bug, Idea }

/// <summary>
/// Technical details for error message (<c>diagnostics</c>). Log already scrubbed by <see cref="LogScrubber"/>;
/// window shows user exactly this object and sends it.
/// </summary>
public sealed record FeedbackDiagnostics(string AppVersion, string OsVersion, string UiLanguage, int Dpi, int Monitors, string LogTail);

/// <summary>What user filled, without <c>installId</c>: it appears only on real send.</summary>
public sealed record FeedbackDraft(FeedbackKind Kind, string Title, string Description, string? Contact, FeedbackDiagnostics? Diagnostics)
{
    /// <summary>Request body: fields without edge spaces, empty contact — null.</summary>
    public FeedbackRequest ToRequest(string installId) => new(
        Kind,
        Title.Trim(),
        Description.Trim(),
        string.IsNullOrWhiteSpace(Contact) ? null : Contact.Trim(),
        Diagnostics,
        installId);
}

/// <summary>Body of <c>POST /api/feedback</c>.</summary>
public sealed record FeedbackRequest(FeedbackKind Kind, string Title, string Description, string? Contact, FeedbackDiagnostics? Diagnostics, string InstallId)
{
    /// <summary>
    /// camelCase, type — “bug”/“idea”, null written explicitly (<c>"contact": null</c>). Cyrillic and rest as-is, without \u.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
