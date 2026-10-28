using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Screeny.Feedback;

/// <summary>Сообщение об ошибке или предложение: от этого зависят подсказки окна и шаблон issue на GitHub.</summary>
public enum FeedbackKind { Bug, Idea }

/// <summary>
/// Технические подробности к сообщению об ошибке (<c>diagnostics</c>). Журнал уже очищен <see cref="LogScrubber"/>;
/// окно показывает пользователю именно этот объект и его же отправляет.
/// </summary>
public sealed record FeedbackDiagnostics(string AppVersion, string OsVersion, string UiLanguage, int Dpi, int Monitors, string LogTail);

/// <summary>Что заполнил пользователь, без <c>installId</c>: тот появляется только при настоящей отправке.</summary>
public sealed record FeedbackDraft(FeedbackKind Kind, string Title, string Description, string? Contact, FeedbackDiagnostics? Diagnostics)
{
    /// <summary>Тело запроса: поля без пробелов по краям, пустой контакт — null.</summary>
    public FeedbackRequest ToRequest(string installId) => new(
        Kind,
        Title.Trim(),
        Description.Trim(),
        string.IsNullOrWhiteSpace(Contact) ? null : Contact.Trim(),
        Diagnostics,
        installId);
}

/// <summary>Тело <c>POST /api/feedback</c>.</summary>
public sealed record FeedbackRequest(FeedbackKind Kind, string Title, string Description, string? Contact, FeedbackDiagnostics? Diagnostics, string InstallId)
{
    /// <summary>
    /// camelCase, вид — «bug»/«idea», null пишется явно (<c>"contact": null</c>). Кириллица и прочее — как есть, без \u.
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
