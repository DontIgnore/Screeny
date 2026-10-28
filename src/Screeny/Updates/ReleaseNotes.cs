using System.Text;
using System.Text.RegularExpressions;

namespace Screeny.Updates;

public enum NoteBlockKind { Heading1, Heading2, Heading3, Bullet, Numbered, Paragraph }

/// <summary>Кусок текста: обычный, жирный или код.</summary>
public sealed record NoteInline(string Text, bool Bold = false, bool Code = false);

/// <param name="Number">Номер пункта нумерованного списка.</param>
/// <param name="Level">Вложенность пункта списка (0 — верхний уровень).</param>
public sealed record NoteBlock(NoteBlockKind Kind, IReadOnlyList<NoteInline> Inlines, int Number = 0, int Level = 0)
{
    public string PlainText => string.Concat(Inlines.Select(i => i.Text));

    /// <summary>
    /// Каких функций (<see cref="Features.FeatureCatalog"/>) касается блок; пусто — общий, показывается всегда.
    /// Несколько id — блок показывается, если включена хотя бы одна из функций.
    /// </summary>
    public IReadOnlyList<string> Features { get; init; } = [];

    /// <summary>Глубина заголовка в исходнике (число <c>#</c>, 1–6); 0 — не заголовок или не из <see cref="ReleaseNotes.Parse"/>.</summary>
    public int Depth { get; init; }
}

/// <summary>
/// Очень маленькое подмножество markdown для списка изменений: заголовки <c>#</c>–<c>###</c> (глубже — как <c>###</c>),
/// маркированные (<c>-</c>, <c>*</c>, <c>+</c>) и нумерованные списки, жирный (<c>**</c>, <c>__</c>), <c>`код`</c>.
/// Ссылки показываются только текстом (без перехода), курсив и прочая разметка снимаются, HTML не интерпретируется
/// (кроме комментариев <c>&lt;!-- --&gt;</c>: они не показываются).
/// <para>
/// Метка функции — комментарий <c>&lt;!-- feature: ocr --&gt;</c> (или <c>&lt;!-- feature: ocr, history --&gt;</c>) в строке
/// заголовка или пункта; на GitHub она не видна. Метка заголовка действует на всё под ним до следующего заголовка того же
/// или более высокого уровня (уровень — число <c>#</c> в исходнике), метка пункта заменяет её для этого пункта (и вложенных
/// в него пунктов). Метка на отдельной строке относится к следующему блоку: к разделу следующего заголовка или к
/// следующему пункту (абзацу). Неотмеченное — общее. Метки в блоках кода, в <c>`коде`</c> и в многострочных комментариях
/// не распознаются; <c>&lt;!--</c> внутри <c>`кода`</c> — не комментарий.
/// </para>
/// <para>
/// Языки — разделы в одном файле: строка <c>&lt;!-- lang: en --&gt;</c> начинает раздел языка, он идёт до следующей такой
/// строки. Окно обновления показывает раздел языка интерфейса, иначе английский, иначе первый (<see cref="ForLanguage"/>).
/// Текст до первой метки языка — общий для всех языков; файл без меток — один язык для всех.
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

    /// <summary>Символы, которые <c>\</c> экранирует.</summary>
    private const string Escapable = "\\`*_[]()#+-.!<>";

    [GeneratedRegex(@"^\s*feature\s*:(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatureTagRx();

    [GeneratedRegex(@"^ {0,3}<!--\s*lang\s*:\s*([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)\s*-->\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LangTagRx();

    /// <summary>Метки языков в файле в порядке появления (без повторов); пусто — файл без разделов.</summary>
    public static IReadOnlyList<string> Languages(string? markdown) => Sections(markdown).Sections.Select(s => s.Language)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Раздел списка изменений для языка интерфейса <paramref name="language"/> (код из <see cref="Localization.Loc.Supported"/>),
    /// перед ним — общий текст до первой метки. Берётся первый подходящий раздел: сначала с точно этой меткой, затем
    /// с меткой, которая этому языку соответствует («zh» и «zh-CN» — для «zh-Hans», «en-US» — для «en»); если такого нет —
    /// так же английский, иначе первый раздел. Без меток — текст как есть.
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

    /// <summary>Метка раздела подходит языку: «zh» — для «zh-Hans» (префикс), «en-US» и «zh-CN» — через родительские культуры.</summary>
    private static bool Serves(string tag, string language)
        => language.StartsWith(tag + "-", StringComparison.OrdinalIgnoreCase)
           || string.Equals(Localization.Loc.MatchCulture(tag), language, StringComparison.Ordinal);

    /// <summary>
    /// Общий текст и разделы языков. Метка — строка из одного комментария <c>&lt;!-- lang: xx --&gt;</c> с отступом меньше
    /// четырёх пробелов (больший отступ в Markdown — код); внутри блоков кода и многострочных комментариев метки не считаются.
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
                StripComments(ref rest, ref inComment); // только чтобы знать, открыт ли многострочный комментарий
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
        IReadOnlyList<string>? pendingTag = null; // метка с отдельной строки — для следующего блока
        bool inFence = false, inComment = false;
        // Метки открытых заголовков и пунктов списка: (уровень, функции).
        var headings = new List<(int Level, IReadOnlyList<string> Features)>();
        var items = new List<(int Level, IReadOnlyList<string> Features)>();
        IReadOnlyList<string> HeadingScope() => headings.Count > 0 ? headings[^1].Features : [];

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new NoteBlock(NoteBlockKind.Paragraph, ParseInlines(paragraph.ToString())) { Features = paragraphTag ?? HeadingScope() });
            paragraph.Clear();
            paragraphTag = null;
            items.Clear(); // абзац заканчивает список
        }

        foreach (var rawLine in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            // Внутри многострочного комментария ``` — тоже комментарий, а не начало блока кода.
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
                // Строка из одного комментария — как будто её нет; метка в ней переходит к следующему блоку.
                if (tag is not null) pendingTag = tag;
                continue;
            }
            if (line.Length == 0 || RuleRx().IsMatch(line)) { FlushParagraph(); continue; }
            bool continuation = char.IsWhiteSpace(rawLine.FirstOrDefault()) && paragraph.Length == 0 && blocks.Count > 0
                                && blocks[^1].Kind is NoteBlockKind.Bullet or NoteBlockKind.Numbered
                                && !HeadingRx().IsMatch(line) && !BulletRx().IsMatch(line) && !NumberedRx().IsMatch(line);
            if (!continuation && paragraph.Length == 0 && pendingTag is not null)
            {
                tag ??= pendingTag; // начинается новый блок
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
                // Продолжение пункта списка на следующей строке с отступом; метка здесь — тоже метка пункта.
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

        // Своя метка пункта, иначе метка пункта, в который он вложен, иначе — заголовка.
        IReadOnlyList<string> ItemFeatures(int level, IReadOnlyList<string>? own)
        {
            items.RemoveAll(s => s.Level >= level);
            var features = own ?? (items.Count > 0 ? items[^1].Features : HeadingScope());
            items.Add((level, features));
            return features;
        }
    }

    /// <summary>
    /// Убирает из строки комментарии <c>&lt;!-- --&gt;</c> (и начало многострочного); возвращает id из меток
    /// <c>feature:</c> или null, если меток нет. <c>&lt;!--</c> внутри <c>`кода`</c> и экранированный (<c>\&lt;!--</c>) —
    /// обычный текст (код и экранирование — как в <see cref="ParseInlines"/>).
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
    /// Список изменений без блоков про выключенные функции (выключены все функции блока) и без заголовков, под которыми
    /// после этого ничего не осталось. Заголовок судится только по содержимому: если пункт со своей меткой остался
    /// под заголовком выключенной функции, заголовок остаётся вместе с ним.
    /// </summary>
    public static IReadOnlyList<NoteBlock> Filter(IReadOnlyList<NoteBlock> blocks, Func<string, bool> isOn)
    {
        var kept = blocks.Where(b => HeadingRank(b) is not null || b.Features.Count == 0 || b.Features.Any(isOn)).ToList();
        var result = new List<NoteBlock>(kept.Count);
        for (int i = 0; i < kept.Count; i++)
            if (HeadingRank(kept[i]) is not { } rank || HasContentUnder(kept, i, rank)) result.Add(kept[i]);
        return result;
    }

    /// <summary>Хотя бы один блок отмечен функцией.</summary>
    public static bool IsTagged(IReadOnlyList<NoteBlock> blocks) => blocks.Any(b => b.Features.Count > 0);

    /// <summary>До следующего заголовка того же или более высокого уровня (по числу <c>#</c>) есть не-заголовок.</summary>
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
    /// Уровень заголовка — число <c>#</c> в исходнике (<c>####</c> показывается как <c>###</c>, но вложен в него);
    /// у блока не из <see cref="Parse"/> — по виду. null — не заголовок.
    /// </summary>
    private static int? HeadingRank(NoteBlock block) => block.Kind switch
    {
        NoteBlockKind.Heading1 or NoteBlockKind.Heading2 or NoteBlockKind.Heading3 when block.Depth > 0 => block.Depth,
        NoteBlockKind.Heading1 => 1,
        NoteBlockKind.Heading2 => 2,
        NoteBlockKind.Heading3 => 3,
        _ => null,
    };

    /// <summary>Два пробела (или таб) отступа — один уровень вложенности; не больше двух уровней.</summary>
    private static int Level(string indent)
    {
        int width = indent.Sum(c => c == '\t' ? 4 : 1);
        return Math.Min(width / 2, 2);
    }

    /// <summary>Жирный, код, ссылки (только текст); экранирование <c>\*</c>; одиночные <c>*</c>/<c>_</c> курсива снимаются.</summary>
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
                // «__» внутри слова (snake__case) — не разметка.
                bool wordInside = c == '_' && i > 0 && char.IsLetterOrDigit(text[i - 1]) && i + 2 < text.Length && char.IsLetterOrDigit(text[i + 2]);
                if (wordInside) { current.Append(c).Append(c); i++; continue; }
                Flush();
                bold = !bold;
                i++;
            }
            else if (c == '*' || (c == '_' && (i == 0 || !char.IsLetterOrDigit(text[i - 1]) || i + 1 >= text.Length || !char.IsLetterOrDigit(text[i + 1]))))
            {
                // Курсив не выделяем: маркер просто убираем.
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

    /// <summary><c>[текст](адрес)</c> → текст. Адрес отбрасывается: окно обновления не открывает ссылки.</summary>
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

    /// <summary>Склеивает соседние куски с одинаковым оформлением.</summary>
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
