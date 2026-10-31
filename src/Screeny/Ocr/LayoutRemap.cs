using System.Windows;

namespace Screeny.Ocr;

/// <summary>
/// Moving word selection to another layout of same screenshot: refined recognition replaced Windows OCR layout,
/// and user already selected part of text. Throughword indices of two engines don't match (they split lines
/// and words differently), so location is moved: new words whose border intersects at least one previous
/// selected word get selected. Edge touching doesn't count — neighboring word not grabbed.
/// </summary>
public static class LayoutRemap
{
    public static HashSet<int> Selection(OcrLayout from, IReadOnlySet<int> selection, OcrLayout to)
    {
        var result = new HashSet<int>();
        var rects = selection.Where(i => i >= 0 && i < from.WordCount).Select(i => from.Word(i).Box).ToList();
        if (rects.Count == 0) return result;
        for (int j = 0; j < to.WordCount; j++)
        {
            var box = to.Word(j).Box;
            if (rects.Any(r => Overlaps(r, box))) result.Add(j);
        }
        return result;
    }

    /// <summary>Anchor word (selection start for Shift+click): first new word at place of old, or null.</summary>
    public static int? Word(OcrLayout from, int? word, OcrLayout to)
    {
        if (word is not { } w || w < 0 || w >= from.WordCount) return null;
        var hit = Selection(from, new HashSet<int> { w }, to);
        return hit.Count > 0 ? hit.Min() : null;
    }

    private static bool Overlaps(Rect a, Rect b)
    {
        var i = Rect.Intersect(a, b);
        return !i.IsEmpty && i.Width > 0 && i.Height > 0;
    }
}
