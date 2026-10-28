namespace Screeny.Ocr.Paddle;

/// <summary>
/// Похожие буквы кириллицы и латиницы. Кириллическая rec-модель пишет латиницу внутри русского текста похожими
/// кириллическими буквами («Privaсу», «PrintSсгееn»). В слове со смешанной письменностью буквы меньшинства заменяются
/// двойниками письменности большинства. Замена 1:1 — позиции символов (шаги CTC) не сдвигаются.
/// </summary>
public static class Homoglyphs
{
    private const string Cyr = "аАвВеЕкКмМнНоОрРсСтТуУхХгіІјЈѕЅԁ";
    private const string Lat = "aABBeEkKMMHHoOpPcCTTyYxXrIIjJsSd";
    // «в» и «н» строчные похожи на латиницу только в некоторых шрифтах: в обратную сторону (латиница → кириллица)
    // их не меняем, чтобы «B» и «H» в английских словах не превращались в «в» и «н».
    private static readonly Dictionary<char, char> ToLat = Cyr.Zip(Lat).ToDictionary(p => p.First, p => p.Second);
    private static readonly Dictionary<char, char> ToCyr = Cyr.Zip(Lat).Where(p => p.First is not ('в' or 'н' or 'м' or 'т' or 'г' or 'і' or 'І' or 'ј' or 'Ј' or 'ѕ' or 'Ѕ' or 'ԁ'))
        .GroupBy(p => p.Second).ToDictionary(g => g.Key, g => g.First().First);

    private static bool IsCyr(char c) => c is >= 'Ѐ' and <= 'ӿ' or 'ԁ';
    private static bool IsLat(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    public static bool HasMixedWord(string text) => text.Split(' ').Any(w => w.Any(IsCyr) && w.Any(IsLat));

    public static string Fix(string text) => string.Join(" ", text.Split(' ').Select(FixWord));

    private static string FixWord(string w)
    {
        int cyr = w.Count(IsCyr), lat = w.Count(IsLat);
        if (cyr == 0 || lat == 0) return w;
        var map = lat >= cyr ? ToLat : ToCyr;
        var fixedWord = new string(w.Select(c => map.TryGetValue(c, out var r) ? r : c).ToArray());
        // Если двойника нашлось не для всех букв меньшинства, слово оставляем как есть: это не гомоглифы («X5K7M», «Wi-Fiсеть»).
        return fixedWord.Any(IsCyr) && fixedWord.Any(IsLat) ? w : fixedWord;
    }
}
