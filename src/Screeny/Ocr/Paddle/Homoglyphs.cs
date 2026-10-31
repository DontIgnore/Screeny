namespace Screeny.Ocr.Paddle;

/// <summary>
/// Similar Cyrillic/Latin letters. Cyrillic rec-model writes Latin inside Russian text with similar
/// Cyrillic letters (“Privaсу”, “PrintSсгееn”). In word with mixed script, minority letters are replaced
/// by majority-script counterparts. 1:1 replacement — character positions (CTC steps) don't shift.
/// </summary>
public static class Homoglyphs
{
    private const string Cyr = "аАвВеЕкКмМнНоОрРсСтТуУхХгіІјЈѕЅԁ";
    private const string Lat = "aABBeEkKMMHHoOpPcCTTyYxXrIIjJsSd";
    // lowercase “в” and “н” resemble Latin only in some fonts: reverse (Latin → Cyrillic)
    // not replaced, so “B” and “H” in English words don't become “в”/“н”.
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
        // If not all minority letters have doubles, word left as-is: not homoglyphs (“X5K7M”, “Wi-Fiсеть”).
        return fixedWord.Any(IsCyr) && fixedWord.Any(IsLat) ? w : fixedWord;
    }
}
