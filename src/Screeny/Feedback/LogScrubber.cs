using System.Text;
using System.Text.RegularExpressions;

namespace Screeny.Feedback;

/// <summary>What to replace in log: profile folder and username of this account.</summary>
public sealed record ScrubContext(string? UserProfile, string? UserName)
{
    public static ScrubContext Current() => new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName);
}

/// <summary>
/// Log tail for technical details. Log writes paths (settings, history, modules, temp folders) and exception
/// texts; no window titles. Removes what identifies a person:
/// <list type="bullet">
/// <item>profile folder (any slashes, doubled <c>\\</c>) → <c>%USERPROFILE%</c>;</item>
/// <item>any other folder inside <c>Users\</c> (short 8.3 name, foreign profile) → <c>Users\&lt;user&gt;</c>;</item>
/// <item>username as separate path part (own history folder on another drive) → <c>&lt;user&gt;</c>;</item>
/// <item>test text-mode line with recognized screenshot text — entirely.</item>
/// </list>
/// Long lines are truncated.
/// </summary>
public static class LogScrubber
{
    public const int TailLines = 100;
    public const int MaxLineLength = 500;
    /// <summary>From file end read at most: 100 lines with stack traces fit into it.</summary>
    public const int MaxReadBytes = 128 * 1024;

    public const string UserProfileToken = "%USERPROFILE%";
    public const string UserToken = "<user>";
    public const string RemovedLine = "[line removed: recognized text]";

    /// <summary>Lines carrying screenshot content (recognized text) are not sent at all.</summary>
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

    /// <summary>C:\Users\Name, C:/Users/Name, C:\\Users\\Name — long first, so short form doesn't cut long.</summary>
    private static IEnumerable<string> ProfileForms(string profile)
        => new[] { profile.Replace("\\", "\\\\"), profile, profile.Replace('\\', '/') }.Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last <paramref name="count"/> lines, scrubbed, joined by <c>\n</c>.</summary>
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

    /// <summary>Log file tail: empty line if no log or inaccessible. File opened without write lock.</summary>
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
            if (cut && lines.Count > 0) lines.RemoveAt(0); // first line read from middle
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return Tail(lines, context, count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
