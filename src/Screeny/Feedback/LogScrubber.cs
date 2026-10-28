using System.Text;
using System.Text.RegularExpressions;

namespace Screeny.Feedback;

/// <summary>Что заменять в журнале: папка профиля и имя пользователя этой учётной записи.</summary>
public sealed record ScrubContext(string? UserProfile, string? UserName)
{
    public static ScrubContext Current() => new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName);
}

/// <summary>
/// Хвост журнала для технических подробностей. Журнал пишет пути (настройки, история, модули, временные папки) и тексты
/// исключений; заголовков окон в нём нет. Убирается то, что указывает на человека:
/// <list type="bullet">
/// <item>папка профиля (в любых слешах, с удвоенными <c>\\</c>) → <c>%USERPROFILE%</c>;</item>
/// <item>любая другая папка внутри <c>Users\</c> (короткое имя 8.3, чужой профиль) → <c>Users\&lt;user&gt;</c>;</item>
/// <item>имя пользователя отдельной частью пути (своя папка истории на другом диске) → <c>&lt;user&gt;</c>;</item>
/// <item>строка тестового режима текста с распознанным текстом скриншота — целиком.</item>
/// </list>
/// Длинные строки обрезаются.
/// </summary>
public static class LogScrubber
{
    public const int TailLines = 100;
    public const int MaxLineLength = 500;
    /// <summary>С конца файла читается не больше: 100 строк с трассировками стека в него помещаются.</summary>
    public const int MaxReadBytes = 128 * 1024;

    public const string UserProfileToken = "%USERPROFILE%";
    public const string UserToken = "<user>";
    public const string RemovedLine = "[line removed: recognized text]";

    /// <summary>Строки, которые несут содержимое скриншота (распознанный текст): не отправляются вовсе.</summary>
    private static readonly string[] DroppedMarkers = ["Dev text mode:"];

    private static readonly Regex UsersFolder = new(@"(?<=[\\/]Users[\\/]+)(?!%)[^\\/\s""'<>|*?:]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Scrub(string line, ScrubContext context)
    {
        if (DroppedMarkers.Any(m => line.Contains(m, StringComparison.Ordinal))) return RemovedLine;
        var text = line;
        if (!string.IsNullOrEmpty(context.UserProfile))
        {
            var profile = context.UserProfile.TrimEnd('\\', '/');
            foreach (var form in ProfileForms(profile))
                text = text.Replace(form, UserProfileToken, StringComparison.OrdinalIgnoreCase);
        }
        text = UsersFolder.Replace(text, UserToken);
        if (context.UserName is { Length: >= 2 } name && name != UserToken)
            text = Regex.Replace(text, $@"(?<=[\\/]){Regex.Escape(name)}(?=[\\/""'\s]|$)", UserToken,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text.Length > MaxLineLength ? text[..MaxLineLength] + "…" : text;
    }

    /// <summary>C:\Users\Name, C:/Users/Name, C:\\Users\\Name — длинные первыми, чтобы короткая форма не разрезала длинную.</summary>
    private static IEnumerable<string> ProfileForms(string profile)
        => new[] { profile.Replace("\\", "\\\\"), profile, profile.Replace('\\', '/') }.Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Последние <paramref name="count"/> строк, очищенные, через <c>\n</c>.</summary>
    public static string Tail(IEnumerable<string> lines, ScrubContext context, int count = TailLines)
    {
        var tail = new Queue<string>(count);
        foreach (var line in lines)
        {
            if (tail.Count == count) tail.Dequeue();
            tail.Enqueue(line);
        }
        return string.Join("\n", tail.Select(l => Scrub(l, context)));
    }

    /// <summary>Хвост файла журнала: пустая строка, если журнала нет или он недоступен. Файл открывается без блокировки записи.</summary>
    public static string ReadTail(string path, ScrubContext context, int count = TailLines)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bool cut = stream.Length > MaxReadBytes;
            if (cut) stream.Seek(-MaxReadBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd().ReplaceLineEndings("\n").Split('\n').ToList();
            if (cut && lines.Count > 0) lines.RemoveAt(0); // первая строка прочитана с середины
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return Tail(lines, context, count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
