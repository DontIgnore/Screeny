using System.Windows;

namespace Screeny.Ocr;

/// <summary>Слово с рамкой в пикселях снимка.</summary>
public sealed record OcrWord(string Text, Rect Box);

/// <summary>Строка в порядке чтения движка. <see cref="Text"/> — строка целиком так, как её собрал движок.
/// <see cref="NoSpaces"/> — язык строки пишется без пробелов (японский, китайский): части строки склеиваются слитно.</summary>
public sealed record OcrLine(IReadOnlyList<OcrWord> Words, string Text, bool NoSpaces = false);

/// <summary>
/// Распознанный текст с геометрией: строки, слова и их рамки. Слова пронумерованы сквозным индексом
/// в порядке чтения (строка за строкой), выделение — набор этих индексов.
/// </summary>
public sealed class OcrLayout
{
    private readonly (int Line, int Word)[] _index;

    /// <param name="noSpaces">Письменность без пробелов между словами (японский, китайский): движок отдаёт
    /// иероглифы отдельными «словами», и склеивать их надо без пробела.</param>
    /// <param name="language">Язык движка, распознавшего раскладку (BCP-47), если известен.</param>
    public OcrLayout(IReadOnlyList<OcrLine> lines, bool noSpaces = false, string? language = null)
    {
        Lines = lines.Where(l => l.Words.Count > 0).Select(l => noSpaces && !l.NoSpaces ? l with { NoSpaces = true } : l).ToList();
        NoSpaces = noSpaces;
        Language = language;
        var index = new List<(int, int)>();
        for (int l = 0; l < Lines.Count; l++)
            for (int w = 0; w < Lines[l].Words.Count; w++) index.Add((l, w));
        _index = index.ToArray();
    }

    public static OcrLayout Empty { get; } = new([]);

    public IReadOnlyList<OcrLine> Lines { get; }
    public bool NoSpaces { get; }
    public string? Language { get; }
    public int WordCount => _index.Length;
    public bool IsEmpty => _index.Length == 0;

    public OcrWord Word(int i) { var (l, w) = _index[i]; return Lines[l].Words[w]; }
    public int LineOf(int i) => _index[i].Line;

    /// <summary>
    /// Перевод рамок из координат распознанной картинки в пиксели снимка: картинку могли уменьшить
    /// под предел движка (<paramref name="scale"/> &lt; 1), а сама она — вырез области (<paramref name="offset"/>).
    /// </summary>
    public OcrLayout Map(double scale, Vector offset)
    {
        double k = scale > 0 ? 1 / scale : 1;
        Rect M(Rect r) => new(r.X * k + offset.X, r.Y * k + offset.Y, r.Width * k, r.Height * k);
        return new OcrLayout(Lines.Select(l => l with { Words = l.Words.Select(w => w with { Box = M(w.Box) }).ToList() }).ToList(), NoSpaces, Language);
    }

    /// <summary>Слово под точкой (с допуском в пикселях снимка) или null.</summary>
    public int? WordAt(Point p, double tolerance = 3)
    {
        int? best = null;
        double bestDist = double.MaxValue;
        for (int i = 0; i < _index.Length; i++)
        {
            var b = Word(i).Box;
            var padded = new Rect(b.X - tolerance, b.Y - tolerance, b.Width + 2 * tolerance, b.Height + 2 * tolerance);
            if (!padded.Contains(p)) continue;
            double dx = Math.Max(0, Math.Max(b.Left - p.X, p.X - b.Right)), dy = Math.Max(0, Math.Max(b.Top - p.Y, p.Y - b.Bottom));
            double d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    /// <summary>Все слова от <paramref name="a"/> до <paramref name="b"/> включительно в порядке чтения.</summary>
    public HashSet<int> Range(int a, int b)
    {
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        return Enumerable.Range(lo, hi - lo + 1).ToHashSet();
    }

    /// <summary>Слова, рамка которых пересекает прямоугольник: выделить «только этот угол картинки».</summary>
    public HashSet<int> InRect(Rect area)
    {
        var set = new HashSet<int>();
        for (int i = 0; i < _index.Length; i++)
            if (Word(i).Box.IntersectsWith(area)) set.Add(i);
        return set;
    }

    /// <summary>Все слова строки, в которой стоит слово <paramref name="i"/>.</summary>
    public HashSet<int> LineRange(int i)
    {
        int line = LineOf(i), first = i, last = i;
        while (first > 0 && LineOf(first - 1) == line) first--;
        while (last < _index.Length - 1 && LineOf(last + 1) == line) last++;
        return Range(first, last);
    }

    public HashSet<int> All() => Enumerable.Range(0, _index.Length).ToHashSet();

    /// <summary>Рамка строки: объединение рамок её слов.</summary>
    public static Rect LineBox(OcrLine line)
    {
        var r = line.Words[0].Box;
        foreach (var w in line.Words) r.Union(w.Box);
        return r;
    }

    /// <summary>
    /// Смешанный текст (русский и английский на одной картинке) один движок читает только наполовину.
    /// Берём раскладку лучшего движка и каждую её строку заменяем строкой другого движка на том же месте,
    /// если та правдоподобнее по <paramref name="score"/>. Строки сопоставляются по перекрытию рамок.
    /// </summary>
    /// <param name="fits">При равной оценке выигрывает движок, чей язык подходит письменности строки
    /// (латинскую «Invoice» читает английская модель, а не русская, у которой выходит «lnvoice»).</param>
    public static OcrLayout MergeBest(IReadOnlyList<OcrLayout> layouts, int baseIndex, Func<string, int> score, Func<string, string?, bool>? fits = null)
    {
        var basis = layouts[baseIndex];
        var used = new HashSet<OcrLine>(ReferenceEqualityComparer.Instance);
        var lines = new List<OcrLine>();
        foreach (var line in basis.Lines)
        {
            var box = LineBox(line);
            var best = line;
            int bestScore = score(line.Text);
            bool bestFits = fits?.Invoke(line.Text, basis.Language) ?? false;
            for (int k = 0; k < layouts.Count; k++)
            {
                if (k == baseIndex) continue;
                foreach (var other in layouts[k].Lines)
                {
                    // Та же строка, а не соседняя или объемлющая: иначе одна длинная строка другого движка
                    // подменила бы несколько строк и текст задвоился бы.
                    if (used.Contains(other) || Overlap(box, LineBox(other)) < 0.6) continue;
                    int sc = score(other.Text);
                    bool otherFits = fits?.Invoke(other.Text, layouts[k].Language) ?? false;
                    if (sc > bestScore || (sc == bestScore && otherFits && !bestFits)) { best = other; bestScore = sc; bestFits = otherFits; }
                }
            }
            if (!ReferenceEquals(best, line)) used.Add(best);
            lines.Add(best);
        }
        return new OcrLayout(lines, false, basis.Language);
    }

    /// <summary>
    /// Голосование проходов на разных масштабах: для каждой строки основы берётся текст, который совпал
    /// у большинства проходов на том же месте (сравнение без учёта пробелов). При равенстве остаётся строка основы.
    /// Одиночный сбой на одном масштабе («вызываюпцне» при 2,4×) так не проходит, если два других прочли верно.
    /// </summary>
    public static OcrLayout Consensus(IReadOnlyList<OcrLayout> passes, int baseIndex)
    {
        var basis = passes[baseIndex];
        var lines = new List<OcrLine>();
        foreach (var line in basis.Lines)
        {
            var box = LineBox(line);
            var votes = new List<OcrLine> { line };
            for (int k = 0; k < passes.Count; k++)
            {
                if (k == baseIndex) continue;
                OcrLine? match = null; double best = 0.6;
                foreach (var other in passes[k].Lines)
                {
                    double o = Overlap(box, LineBox(other));
                    if (o >= best) { best = o; match = other; }
                }
                if (match is not null) votes.Add(match);
            }
            string Key(OcrLine l) => string.Concat(l.Text.Where(ch => !char.IsWhiteSpace(ch)));
            var winner = votes.GroupBy(Key).OrderByDescending(g => g.Count()).ThenBy(g => g.Contains(line) ? 0 : 1).First();
            lines.Add(winner.Contains(line) ? line : winner.First());
        }
        return new OcrLayout(lines, false, basis.Language);
    }

    /// <summary>Пересечение к объединению (IoU): строки совпадают, только если почти одно и то же место.</summary>
    private static double Overlap(Rect a, Rect b)
    {
        var i = Rect.Intersect(a, b);
        if (i.IsEmpty) return 0;
        double inter = i.Width * i.Height, union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>
    /// Текст выделения: строки через перевод строки, слова через пробел (или слитно для письменности без пробелов).
    /// Целиком выделенная строка берётся в написании движка. Пустое выделение означает весь текст.
    /// </summary>
    public string TextOf(IReadOnlySet<int>? selection)
    {
        var parts = new List<string>();
        int i = 0;
        for (int l = 0; l < Lines.Count; l++)
        {
            var line = Lines[l];
            var picked = new List<string>();
            for (int w = 0; w < line.Words.Count; w++, i++)
                if (selection is null || selection.Count == 0 || selection.Contains(i)) picked.Add(line.Words[w].Text);
            if (picked.Count == 0) continue;
            parts.Add(picked.Count == line.Words.Count && line.Text.Length > 0 ? line.Text : string.Join(line.NoSpaces ? "" : " ", picked));
        }
        return string.Join(Environment.NewLine, parts);
    }

    /// <summary>
    /// Подряд идущие выделенные слова одной строки сливаются в один прямоугольник: подсветка читается как
    /// выделенный текст, а не как набор отдельных плашек.
    /// </summary>
    public IEnumerable<Rect> SelectionRuns(IReadOnlySet<int> selection)
    {
        Rect? run = null;
        int runLine = -1, prev = -2;
        for (int i = 0; i < _index.Length; i++)
        {
            if (!selection.Contains(i)) continue;
            var box = Word(i).Box;
            if (run is { } r && LineOf(i) == runLine && prev == i - 1) { r.Union(box); run = r; }
            else
            {
                if (run is { } done) yield return done;
                run = box; runLine = LineOf(i);
            }
            prev = i;
        }
        if (run is { } last) yield return last;
    }
}
