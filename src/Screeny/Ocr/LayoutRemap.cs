using System.Windows;

namespace Screeny.Ocr;

/// <summary>
/// Перенос выделения слов на другую раскладку того же снимка: уточнённое распознавание заменило раскладку Windows OCR,
/// а пользователь уже выделил часть текста. Сквозные индексы слов у двух движков не совпадают (они иначе режут строки
/// и слова), поэтому переносится место: выделяются новые слова, рамка которых пересекает рамку хотя бы одного прежнего
/// выделенного слова. Касание краями не считается — соседнее слово не прихватывается.
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

    /// <summary>Слово-якорь (начало выделения для Shift+клик): первое новое слово на месте прежнего, или null.</summary>
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
