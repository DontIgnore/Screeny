using System.Text;
using System.Text.RegularExpressions;

namespace Screeny.Updates;

public enum NoteBlockKind { Heading1, Heading2, Heading3, Bullet, Numbered, Paragraph }

/// <summary>Text piece: normal, bold or code.</summary>
public sealed record NoteInline(string Text, bool Bold = false, bool Code = false);

/// <param name="Number">Numbered list item number.</param>
/// <param name="Level">List nesting (0 — top).</param>
public sealed record NoteBlock(NoteBlockKind Kind, IReadOnlyList<NoteInline> Inlines, int Number = 0, int Level = 0)
{
    public string PlainText => string.Concat(Inlines.Select(i => i.Text));

    /// <summary>
    /// Which features (<see cref="Features.FeatureCatalog"/>) block concerns; empty — general, always shown.
    /// Multiple ids — block shown if at least one feature enabled.
    /// </summary>
    public IReadOnlyList<string> Features { get; init; } = [];

    /// <summary>Header depth in source (number of <c>#</c>, 1–6); 0 — not header or not from <see cref="ReleaseNotes.Parse"/>.</summary>
    public int Depth { get; init; }
}

/// <summary>
/// Very small markdown subset for changelog: headers <c>#</c>–<c>###</c> (deeper — as <c>###</c>),
/// bullets (<c>-</c>, <c>*</c>, <c>+</c>) and numbered lists, bold (<c>**</c>, <c>__</c>), <c>`code`</c>.
/// Links shown as text only (no navigation), italic and other markup removed, HTML not interpreted
/// (except comments <c>&lt;!-- --&gt;</c>: not shown).
/// <para>
/// Feature label — comment <c>&lt;!-- feature: ocr --&gt;</c> (or <c>&lt;!-- feature: ocr, history --&gt;</c>) in line
/// of header or item; invisible on GitHub. Header label applies to everything under it until next header of same or
/// higher level (level — number of <c>#</c> in source), item label replaces it for that item (and nested
/// items). Label on separate line applies to next block: next header section or next
/// item (paragraph). Unmarked — general. Labels in code blocks, in <c>`code`</c> and multiline comments
/// not recognized; <c>&lt;!--</c> inside <c>`code`</c> is not comment.
/// </para>
/// <para>
/// Languages — sections in one file: line <c>&lt;!-- lang: en --&gt;</c> starts language section until next such
/// line. Update window shows UI language section, else English, else first (<see cref="ForLanguage"/>).
/// Text before first language label — common for all languages; file without labels — one language for all.
/// </para>
/// </summary>
public static partial class ReleaseNotes
{
    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*\s*$")]
    private static partial Regex HeadingRx();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex BulletRx();

    [GeneratedRegex(@"^(\s*)(\d{1,4})[.)]\s+(.*)$")]
    private static partial Regex NumberedRx();

    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex RuleRx();

    /// <summary>Characters <c>\</c> escapes.</summary>
    private const string Escapable = "\\`*_[]()#+-.!<>";

    [GeneratedRegex(@"^\s*feature\s*:(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatureTagRx();

    [GeneratedRegex(@"^ {0,3}<!--\s*lang\s*:\s*([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)\s*-->\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LangTagRx();

    /// <summary>Language labels in file in appearance order (no duplicates); empty — no sections.</summary>
    public static IReadOnlyList<string> Languages(string? markdown) => Sections(markdown).Sections.Select(s => s.Language)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Changelog section for UI language <paramref name="language"/> (code from <see cref="Localization.Loc.Supported"/>),
    /// before it common text up to first label. First matching section taken: first exactly this label, then
    /// label matching that language (“zh” and “zh-CN” — for “zh-Hans”, “en-US” — for “en”); if none —
    /// same English, else first section. Without labels — text as-is.
    /// </summary>
    public static string? ForLanguage(string? markdown, string language)
    {
        var (common, sections) = Sections(markdown);
        if (sections.Count == 0) return markdown;
        int index = Find(language);
        if (index < 0) index = Find(Localization.Loc.Fallback);
        return common + sections[Math.Max(index, 0)].Text;

        int Find(string lang)
        {
            int exact = sections.FindIndex(s => string.Equals(s.Language, lang, StringComparison.OrdinalIgnoreCase));
            return exact >= 0 ? exact : sections.FindIndex(s => Serves(s.Language, lang));
        }
    }

    /// <summary>Section label matches language: “zh” — for “zh-Hans” (prefix), “en-US” and “zh-CN” — via parent cultures.</summary>
    private static bool Serves(string tag, string language)
        => language.StartsWith(tag + "-", StringComparison.OrdinalIgnoreCase)
           || string.Equals(Localization.Loc.MatchCulture(tag), language, StringComparison.Ordinal);

    /// <summary>
    /// Common text and language sections. Label — line of single comment <c>&lt;!-- lang: xx --&gt;</c> with indent less than
    /// four spaces (larger indent in Markdown — code); inside code blocks and multiline comments labels not counted.
    /// </summary>
    private static (string Common, List<(string Language, string Text)> Sections) Sections(string? markdown)
    {
        var sections = new List<(string Language, StringBuilder Text)>();
        var common = new StringBuilder();
        if (string.IsNullOrEmpty(markdown)) return ("", []);
        bool inFence = false, inComment = false;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (!inComment && line.TrimStart().StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            else if (!inFence && !inComment && LangTagRx().Match(line) is { Success: true } tag)
            {
                sections.Add((tag.Groups[1].Value, new StringBuilder()));
                continue;
            }
            else if (!inFence)
            {
                var rest = line;
                StripComments(ref rest, ref inComment); // only to know if multiline comment open
            }
            (sections.Count > 0 ? sections[^1].Text : common).Append(line).Append('\n');
        }
        return (common.ToString(), sections.Select(s => (s.Language, s.Text.ToString())).ToList());
    }

    public static IReadOnlyList<NoteBlock> Parse(string? markdown)
    {
        var blocks = new List<NoteBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;
        var paragraph = new StringBuilder();
        IReadOnlyList<string>? paragraphTag = null;
        IReadOnlyList<string>? pendingTag = null; // label on separate line — for next block
        bool inFence = false, inComment = false;
        // Open header and list item labels: (level, features).
        var headings = new List<(int Level, IReadOnlyList<string> Features)>();
        var items = new List<(int Level, IReadOnlyList<string> Features)>();
        IReadOnlyList<string> HeadingScope() => headings.Count > 0 ? headings[^1].Features : [];

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new NoteBlock(NoteBlockKind.Paragraph, ParseInlines(paragraph.ToString())) { Features = paragraphTag ?? HeadingScope() });
            paragraph.Clear();
            paragraphTag = null;
            items.Clear(); // paragraph ends list
        }

        foreach (var rawLine in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            // Inside multiline comment ``` is also comment, not code block start.
            if (!inComment && line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { FlushParagraph(); inFence = !inFence; continue; }
            if (inFence)
            {
                if (line.Length > 0) blocks.Add(new NoteBlock(NoteBlockKind.Paragraph, [new NoteInline(line.Trim(), Code: true)]) { Features = HeadingScope() });
                continue;
            }
            bool hadComment = inComment || line.Contains("<!--", StringComparison.Ordinal);
            var tag = StripComments(ref line, ref inComment);
            if (hadComment && line.Trim().Length == 0)
            {
                // Line of single comment — as if absent; its label moves to next block.
                if (tag is not null) pendingTag = tag;
                continue;
            }
            if (line.Length == 0 || RuleRx().IsMatch(line)) { FlushParagraph(); continue; }
            bool continuation = char.IsWhiteSpace(rawLine.FirstOrDefault()) && paragraph.Length == 0 && blocks.Count > 0
                                && blocks[^1].Kind is NoteBlockKind.Bullet or NoteBlockKind.Numbered
                                && !HeadingRx().IsMatch(line) && !BulletRx().IsMatch(line) && !NumberedRx().IsMatch(line);
            if (!continuation && paragraph.Length == 0 && pendingTag is not null)
            {
                tag ??= pendingTag; // new block starts
                pendingTag = null;
            }

            if (HeadingRx().Match(line) is { Success: true } h)
            {
                FlushParagraph();
                int depth = h.Groups[1].Length;
                var kind = depth switch { 1 => NoteBlockKind.Heading1, 2 => NoteBlockKind.Heading2, _ => NoteBlockKind.Heading3 };
                headings.RemoveAll(s => s.Level >= depth);
                var features = tag ?? HeadingScope();
                headings.Add((depth, features));
                items.Clear();
                blocks.Add(new NoteBlock(kind, ParseInlines(h.Groups[2].Value)) { Features = features, Depth = depth });
            }
            else if (BulletRx().Match(line) is { Success: true } b)
            {
                FlushParagraph();
                int level = Level(b.Groups[1].Value);
                blocks.Add(new NoteBlock(NoteBlockKind.Bullet, ParseInlines(b.Groups[2].Value), Level: level) { Features = ItemFeatures(level, tag) });
            }
            else if (NumberedRx().Match(line) is { Success: true } n)
            {
                FlushParagraph();
                int level = Level(n.Groups[1].Value);
                blocks.Add(new NoteBlock(NoteBlockKind.Numbered, ParseInlines(n.Groups[3].Value), int.Parse(n.Groups[2].Value), level) { Features = ItemFeatures(level, tag) });
            }
            else if (continuation)
            {
                // Continuation of list item on next line with indent; label here is item label too.
                var last = blocks[^1];
                blocks[^1] = last with { Inlines = [.. last.Inlines, new NoteInline(" "), .. ParseInlines(line.Trim())], Features = tag ?? last.Features };
                if (tag is not null && items.Count > 0) items[^1] = (items[^1].Level, tag);
            }
            else
            {
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(line.Trim());
                if (tag is not null) paragraphTag = tag;
            }
        }
        FlushParagraph();
        return blocks;

        // Own item label, else label of containing item, else — header.
        IReadOnlyList<string> ItemFeatures(int level, IReadOnlyList<string>? own)
        {
            items.RemoveAll(s => s.Level >= level);
            var features = own ?? (items.Count > 0 ? items[^1].Features : HeadingScope());
            items.Add((level, features));
            return features;
        }
    }

    /// <summary>
    /// Removes comments <c>&lt;!-- --&gt;</c> from line (and multiline start); returns id from labels
    /// <c>feature:</c> or null if none. <c>&lt;!--</c> inside <c>`code`</c> and escaped (<c>\&lt;!--</c>) —
    /// plain text (code and escaping as in <see cref="ParseInlines"/>).
    /// </summary>
    private static IReadOnlyList<string>? StripComments(ref string line, ref bool inComment)
    {
        int i = 0;
        if (inComment)
        {
            int close = line.IndexOf("-->", StringComparison.Ordinal);
            if (close < 0) { line = ""; return null; }
            i = close + 3;
            inComment = false;
        }
        if (i == 0 && !line.Contains("<!--", StringComparison.Ordinal)) return null;
        var text = new StringBuilder(line.Length);
        List<string>? features = null;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < line.Length && Escapable.Contains(line[i + 1]))
            {
                text.Append(c).Append(line[i + 1]);
                i += 2;
            }
            else if (c == '`' && line.IndexOf('`', i + 1) is var end and > 0)
            {
                text.Append(line, i, end - i + 1);
                i = end + 1;
            }
            else if (string.CompareOrdinal(line, i, "<!--", 0, 4) == 0)
            {
                int close = line.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (close < 0) { inComment = true; break; }
                if (FeatureTagRx().Match(line[(i + 4)..close]) is { Success: true } t)
                {
                    features ??= [];
                    foreach (var id in t.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (!features.Contains(id.ToLowerInvariant())) features.Add(id.ToLowerInvariant());
                }
                i = close + 3;
            }
            else
            {
                text.Append(c);
                i++;
            }
        }
        line = text.ToString().TrimEnd();
        return features is { Count: > 0 } ? features : null;
    }

    /// <summary>
    /// Changelog without blocks about disabled features (all block features disabled) and without headers under which
    /// nothing remains after that. Header judged only by content: if item with own label remains under
    /// disabled feature header, header stays with it.
    /// </summary>
    public static IReadOnlyList<NoteBlock> Filter(IReadOnlyList<NoteBlock> blocks, Func<string, bool> isOn)
    {
        var kept = blocks.Where(b => HeadingRank(b) is not null || b.Features.Count == 0 || b.Features.Any(isOn)).ToList();
        var result = new List<NoteBlock>(kept.Count);
        for (int i = 0; i < kept.Count; i++)
            if (HeadingRank(kept[i]) is not { } rank || HasContentUnder(kept, i, rank)) result.Add(kept[i]);
        return result;
    }

    /// <summary>At least one block marked with feature.</summary>
    public static bool IsTagged(IReadOnlyList<NoteBlock> blocks) => blocks.Any(b => b.Features.Count > 0);

    /// <summary>Before next header of same or higher level (by number of <c>#</c>) there is non-header.</summary>
    private static bool HasContentUnder(List<NoteBlock> blocks, int heading, int rank)
    {
        for (int j = heading + 1; j < blocks.Count; j++)
        {
            if (HeadingRank(blocks[j]) is not { } next) return true;
            if (next <= rank) return false;
        }
        return false;
    }

    /// <summary>
    /// Header level — number of <c>#</c> in source (<c>####</c> displayed as <c>###</c>, but nested into it);
    /// for block not from <see cref="Parse"/> — by appearance. null — not header.
    /// </summary>
    private static int? HeadingRank(NoteBlock block) => block.Kind switch
    {
        NoteBlockKind.Heading1 or NoteBlockKind.Heading2 or NoteBlockKind.Heading3 when block.Depth > 0 => block.Depth,
        NoteBlockKind.Heading1 => 1,
        NoteBlockKind.Heading2 => 2,
        NoteBlockKind.Heading3 => 3,
        _ => null,
    };

    /// <summary>Two spaces (or tab) indent — one nesting level; no more than two levels.</summary>
    private static int Level(string indent)
    {
        int width = indent.Sum(c => c == '\t' ? 4 : 1);
        return Math.Min(width / 2, 2);
    }

    /// <summary>Bold, code, links (text only); escaping <c>\*</c>; lone <c>*</c>/<c>_</c> italics removed.</summary>
    public static IReadOnlyList<NoteInline> ParseInlines(string text)
    {
        var result = new List<NoteInline>();
        var current = new StringBuilder();
        bool bold = false;

        void Flush()
        {
            if (current.Length == 0) return;
            result.Add(new NoteInline(current.ToString(), Bold: bold));
            current.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && Escapable.Contains(text[i + 1]))
            {
                current.Append(text[++i]);
            }
            else if (c == '`' && text.IndexOf('`', i + 1) is var end and > 0)
            {
                Flush();
                result.Add(new NoteInline(text[(i + 1)..end], Bold: bold, Code: true));
                i = end;
            }
            else if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                // “__” inside word (snake__case) — not markup.
                bool wordInside = c == '_' && i > 0 && char.IsLetterOrDigit(text[i - 1]) && i + 2 < text.Length && char.IsLetterOrDigit(text[i + 2]);
                if (wordInside) { current.Append(c).Append(c); i++; continue; }
                Flush();
                bold = !bold;
                i++;
            }
            else if (c == '*' || (c == '_' && (i == 0 || !char.IsLetterOrDigit(text[i - 1]) || i + 1 >= text.Length || !char.IsLetterOrDigit(text[i + 1]))))
            {
                // Italic not marked: marker simply removed.
            }
            else if (c == '[' && TryParseLink(text, i, out var label, out int next))
            {
                foreach (var inline in ParseInlines(label)) { Flush(); result.Add(inline with { Bold = inline.Bold || bold }); }
                i = next - 1;
            }
            else current.Append(c);
        }
        Flush();
        return Merge(result);
    }

    /// <summary><c>[text](url)</c> → text. URL discarded: update window doesn't open links.</summary>
    private static bool TryParseLink(string text, int start, out string label, out int next)
    {
        label = "";
        next = start;
        int close = text.IndexOf(']', start + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return false;
        int paren = text.IndexOf(')', close + 2);
        if (paren < 0) return false;
        label = text[(start + 1)..close];
        next = paren + 1;
        return true;
    }

    /// <summary>Merges adjacent pieces with same styling.</summary>
    private static IReadOnlyList<NoteInline> Merge(List<NoteInline> inlines)
    {
        var merged = new List<NoteInline>();
        foreach (var inline in inlines)
        {
            if (inline.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1].Bold == inline.Bold && merged[^1].Code == inline.Code)
                merged[^1] = merged[^1] with { Text = merged[^1].Text + inline.Text };
            else merged.Add(inline);
        }
        return merged;
    }
}
