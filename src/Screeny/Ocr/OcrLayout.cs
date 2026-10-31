using System.Windows;

namespace Screeny.Ocr;

/// <summary>Word with border in screenshot pixels.</summary>
public sealed record OcrWord(string Text, Rect Box);

/// <summary>Line in engine reading order. <see cref="Text"/> — whole line as engine assembled.
/// <see cref="NoSpaces"/> — line language has no spaces (Japanese, Chinese): parts joined without space.</summary>
public sealed record OcrLine(IReadOnlyList<OcrWord> Words, string Text, bool NoSpaces = false);

/// <summary>
/// Recognized text with geometry: lines, words and their borders. Words numbered with through index
/// in reading order (line by line), selection is set of these indices.
/// </summary>
public sealed class OcrLayout
{
    private readonly (int Line, int Word)[] _index;

    /// <param name="noSpaces">Script without spaces (Japanese, Chinese): engine returns characters as separate
    /// “words”, join without space.</param>
    /// <param name="language">Engine language that recognized layout (BCP-47), if known.</param>
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
    /// Translates borders from recognized image coordinates to screenshot pixels: image may be downscaled
    /// for engine limit (<paramref name="scale"/> &lt; 1), and itself may be region crop (<paramref name="offset"/>).
    /// </summary>
    public OcrLayout Map(double scale, Vector offset)
    {
        double k = scale > 0 ? 1 / scale : 1;
        Rect M(Rect r) => new(r.X * k + offset.X, r.Y * k + offset.Y, r.Width * k, r.Height * k);
        return new OcrLayout(Lines.Select(l => l with { Words = l.Words.Select(w => w with { Box = M(w.Box) }).ToList() }).ToList(), NoSpaces, Language);
    }

    /// <summary>Word under point (with tolerance in screenshot pixels) or null.</summary>
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

    /// <summary>All words from <paramref name="a"/> to <paramref name="b"/> inclusive in reading order.</summary>
    public HashSet<int> Range(int a, int b)
    {
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        return Enumerable.Range(lo, hi - lo + 1).ToHashSet();
    }

    /// <summary>Words whose border intersects rectangle: select “only this corner of image”.</summary>
    public HashSet<int> InRect(Rect area)
    {
        var set = new HashSet<int>();
        for (int i = 0; i < _index.Length; i++)
            if (Word(i).Box.IntersectsWith(area)) set.Add(i);
        return set;
    }

    /// <summary>All words of line containing word <paramref name="i"/>.</summary>
    public HashSet<int> LineRange(int i)
    {
        int line = LineOf(i), first = i, last = i;
        while (first > 0 && LineOf(first - 1) == line) first--;
        while (last < _index.Length - 1 && LineOf(last + 1) == line) last++;
        return Range(first, last);
    }

    public HashSet<int> All() => Enumerable.Range(0, _index.Length).ToHashSet();

    /// <summary>Line border: union of its word borders.</summary>
    public static Rect LineBox(OcrLine line)
    {
        var r = line.Words[0].Box;
        foreach (var w in line.Words) r.Union(w.Box);
        return r;
    }

    /// <summary>
    /// Mixed text (Russian and English in one image) one engine reads only half.
    /// Take best engine layout and replace each line with line of other engine at same place,
    /// if that is more plausible by <paramref name="score"/>. Lines matched by border overlap.
    /// </summary>
    /// <param name="fits">On equal score, engine whose language suits line script wins
    /// (Latin “Invoice” read by English model, not Russian giving “lnvoice”).</param>
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
                    // Same line, not neighbor or containing: else one long line of other engine
                    // would replace multiple lines and text would duplicate.
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
    /// Voting of passes at different scales: for each base line, text that matched
    /// in majority of passes at same place (comparison ignoring spaces). On tie base line stays.
    /// Single glitch at one scale (“вызываюпцне” at 2.4×) doesn't pass if two others read correctly.
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

    /// <summary>Intersection over union (IoU): lines match only if nearly same place.</summary>
    private static double Overlap(Rect a, Rect b)
    {
        var i = Rect.Intersect(a, b);
        if (i.IsEmpty) return 0;
        double inter = i.Width * i.Height, union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>
    /// Selection text: lines by newline, words by space (or joined for no-space script).
    /// Fully selected line taken in engine's spelling. Empty selection means all text.
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
    /// Consecutive selected words of one line merge into one rectangle: highlight reads as
    /// selected text, not set of separate plaques.
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
