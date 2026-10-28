using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Screeny.Ocr;

/// <summary>Распознавание текста встроенным в Windows движком (офлайн, языки профиля пользователя).</summary>
public static class OcrService
{
    public static bool IsAvailable => OcrEngine.TryCreateFromUserProfileLanguages() is not null;

    /// <summary>
    /// Текст снимка или null, если в системе нет ни одного языка с поддержкой OCR.
    /// Движок одного языка не читает чужую письменность (английский превращает кириллицу в «3aKpb1Ba»),
    /// поэтому распознаём всеми языками профиля и берём самый правдоподобный результат.
    /// </summary>
    public static async Task<string?> RecognizeAsync(BitmapSource image, bool adaptive = true)
        => (await RecognizeLayoutAsync(image, adaptive))?.TextOf(null);

    /// <summary>Текст с медианной высотой слова ниже порога (в пикселях) считается мелким и распознаётся ещё и с увеличением.</summary>
    public const double SmallWordHeight = 18;

    /// <summary>
    /// Масштабы дополнительных проходов для мелкого текста. Подобраны на наборе трудных снимков:
    /// 2–2,5× дают меньше всего ошибок, 3× и больше снова хуже.
    /// </summary>
    public static readonly double[] UpscalePasses = [2.0, 2.5];

    /// <summary>
    /// Текст с геометрией: строки, слова и рамки слов в пикселях <paramref name="image"/>.
    /// null — в системе нет ни одного языка с OCR.
    /// Движок Windows плохо читает мелкий текст: строка в 12 px даёт «срабатыват.е» вместо «срабатывание»,
    /// а нередко не находит вообще ничего. Поэтому первый проход измеряет высоту слов, и если текст мелкий
    /// (или ничего не найдено в небольшой картинке), картинка распознаётся ещё и увеличенной в 2 и 2,5 раза.
    /// Итог собирается голосованием по строкам (<see cref="OcrLayout.Consensus"/>).
    /// </summary>
    public static async Task<OcrLayout?> RecognizeLayoutAsync(BitmapSource image, bool adaptive = true)
    {
        var engines = Engines();
        if (engines.Count == 0) return null;
        var first = await RecognizeCoreAsync(engines, image, 1);
        if (!adaptive || !NeedsUpscale(MedianWordHeight(first), Math.Max(image.PixelWidth, image.PixelHeight))) return first;

        // Проходы независимы и идут параллельно. Экземпляр OcrEngine не распознаёт две картинки сразу,
        // поэтому у каждого прохода свои движки.
        var frozen = image.IsFrozen ? image : (BitmapSource)image.GetAsFrozen();
        var passes = (await Task.WhenAll(UpscalePasses.Select(s => Task.Run(() => RecognizeCoreAsync(Engines(), frozen, s))))).ToList();
        passes.Add(first);
        // Основа — самый крупный проход, если он что-то нашёл; иначе тот, что прочитал больше всего.
        int baseIndex = passes[UpscalePasses.Length - 1].IsEmpty
            ? passes.Select((p, i) => (p, i)).OrderByDescending(x => Score(x.p)).First().i
            : UpscalePasses.Length - 1;
        return OcrLayout.Consensus(passes, baseIndex);
    }

    /// <summary>Мелкий текст (медиана ниже <see cref="SmallWordHeight"/>) или ничего не найдено в небольшой картинке.</summary>
    public static bool NeedsUpscale(double? medianWordHeight, int longestSide)
        => medianWordHeight is { } h ? h < SmallWordHeight : longestSide <= 1600;

    /// <summary>Распознавание с принудительным увеличением (для сравнения и тестов). Рамки — в пикселях исходной картинки.</summary>
    public static async Task<OcrLayout?> RecognizeAtScaleAsync(BitmapSource image, double upscale)
    {
        var engines = Engines();
        return engines.Count == 0 ? null : await RecognizeCoreAsync(engines, image, upscale);
    }

    public static double? MedianWordHeight(OcrLayout layout)
    {
        if (layout.IsEmpty) return null;
        var heights = Enumerable.Range(0, layout.WordCount).Select(i => layout.Word(i).Box.Height).OrderBy(h => h).ToList();
        return heights[heights.Count / 2];
    }

    /// <summary>Оценка раскладки для выбора между проходами: сумма правдоподобия строк.</summary>
    public static int Score(OcrLayout layout) => layout.Lines.Sum(l => LinePlausibility(l.Text));

    private static async Task<OcrLayout> RecognizeCoreAsync(List<OcrEngine> engines, BitmapSource image, double upscale)
    {
        var bitmap = ToSoftwareBitmap(image, upscale, out double scale);
        try
        {
            // Языки распознают одну и ту же картинку независимо — параллельно.
            var layouts = (await Task.WhenAll(engines.Select(async engine =>
            {
                var result = await engine.RecognizeAsync(bitmap);
                bool noSpaces = IsNoSpaceLanguage(engine.RecognizerLanguage.LanguageTag);
                var lines = result.Lines.Select(l => new OcrLine(
                    l.Words.Select(w => new OcrWord(w.Text, new System.Windows.Rect(w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height))).ToList(),
                    l.Text, noSpaces)).ToList();
                return new OcrLayout(lines, noSpaces, engine.RecognizerLanguage.LanguageTag);
            }))).ToList();
            int baseIndex = PickBest(layouts.Select(l => l.TextOf(null)).ToList());
            var best = OcrLayout.MergeBest(layouts, baseIndex, LinePlausibility, LanguageFits);
            return best.Map(scale, new System.Windows.Vector(0, 0));
        }
        finally { bitmap.Dispose(); }
    }

    private static readonly string[] CyrillicLanguages = ["ru", "uk", "be", "bg", "sr", "mk", "kk", "ky", "tg", "mn"];

    /// <summary>Язык движка подходит письменности строки: кириллическая строка — кириллический язык, латинская — нет.</summary>
    public static bool LanguageFits(string line, string? languageTag)
    {
        if (string.IsNullOrEmpty(languageTag)) return false;
        int latin = line.Count(ch => char.IsLetter(ch) && ch < 'ɐ'), cyr = line.Count(ch => ch is >= 'Ѐ' and <= 'ӿ');
        if (latin == 0 && cyr == 0) return false;
        bool cyrLang = languageTag.Contains("-Cyrl", StringComparison.OrdinalIgnoreCase)
            || (!languageTag.Contains("-Latn", StringComparison.OrdinalIgnoreCase)
                && CyrillicLanguages.Any(c => languageTag.Equals(c, StringComparison.OrdinalIgnoreCase) || languageTag.StartsWith(c + "-", StringComparison.OrdinalIgnoreCase)));
        return cyr > latin ? cyrLang : !cyrLang && !IsNoSpaceLanguage(languageTag);
    }

    /// <summary>Японский и китайский пишутся без пробелов между словами.</summary>
    public static bool IsNoSpaceLanguage(string languageTag)
        => languageTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase) || languageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>Движки для языков профиля, у которых есть OCR, в порядке предпочтения пользователя.</summary>
    private static List<OcrEngine> Engines()
    {
        var engines = new List<OcrEngine>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in Windows.System.UserProfile.GlobalizationPreferences.Languages)
        {
            var lang = new Windows.Globalization.Language(tag);
            if (!OcrEngine.IsLanguageSupported(lang) || !seen.Add(lang.LanguageTag)) continue;
            if (OcrEngine.TryCreateFromLanguage(lang) is { } e) engines.Add(e);
        }
        if (engines.Count == 0 && OcrEngine.TryCreateFromUserProfileLanguages() is { } fallback) engines.Add(fallback);
        return engines;
    }

    /// <param name="upscale">Желаемое увеличение; итоговый масштаб (<paramref name="scale"/>) ограничен пределом движка.</param>
    private static SoftwareBitmap ToSoftwareBitmap(BitmapSource image, double upscale, out double scale)
    {
        BitmapSource src = image;
        double fit = ScaleToFit(image.PixelWidth, image.PixelHeight, (int)OcrEngine.MaxImageDimension);
        // Больше предела движка — уменьшаем средствами WPF; увеличение (мелкий текст) — своим бикубическим фильтром.
        if (fit < 1) src = new TransformedBitmap(image, new ScaleTransform(fit, fit));
        if (src.Format != PixelFormats.Bgra32) src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var pixels = new byte[w * 4 * h];
        src.CopyPixels(pixels, w * 4, 0);

        if (fit < 1) { scale = fit; upscale = 1; }
        else scale = 1;
        double up = Math.Min(Math.Max(upscale, 1), (double)OcrEngine.MaxImageDimension / Math.Max(w, h));
        if (up > 1.02)
        {
            int nw = Math.Max(1, (int)Math.Round(w * up)), nh = Math.Max(1, (int)Math.Round(h * up));
            pixels = ImageResampler.ResizeBgra(pixels, w, h, nw, nh);
            scale = (double)nw / w;
            w = nw; h = nh;
        }

        var sb = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Ignore);
        sb.CopyFromBuffer(pixels.AsBuffer());
        return sb;
    }

    /// <summary>Индекс самого правдоподобного текста; при равенстве — первый (язык выше в списке пользователя).</summary>
    public static int PickBest(IReadOnlyList<string> candidates)
    {
        int best = 0;
        for (int i = 1; i < candidates.Count; i++)
            if (Plausibility(candidates[i]) > Plausibility(candidates[best])) best = i;
        return best;
    }

    /// <summary>
    /// Сумма длин «чистых» слов: буквы одной письменности (латиница или кириллица) без цифр внутри,
    /// либо только цифры. Мусор чужой модели («3aKpb1Ba'0T», «Соmmittеd») не засчитывается.
    /// </summary>
    public static int Plausibility(string text)
    {
        int score = 0;
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim('.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '«', '»', '—', '-');
            if (word.Length == 0) continue;
            bool latin = false, cyrillic = false, digit = false, other = false;
            foreach (var ch in word)
            {
                if (char.IsDigit(ch)) digit = true;
                else if (ch is >= 'Ѐ' and <= 'ӿ') cyrillic = true;
                else if (char.IsLetter(ch) && ch < 'ɐ') latin = true;
                else if (char.IsLetter(ch)) other = true;
            }
            bool letters = latin || cyrillic || other;
            bool clean = letters ? !digit && (latin ? 1 : 0) + (cyrillic ? 1 : 0) + (other ? 1 : 0) == 1 : digit;
            if (clean) score += word.Length;
        }
        return score;
    }

    /// <summary>
    /// Правдоподобие одной строки. Строка почти всегда написана одной письменностью, поэтому «чистые» слова
    /// основной письменности строки идут в полный вес, а слова другой — в половину: так «Thank уои» (английская
    /// строка, прочитанная русской моделью) проигрывает «Thank you», а «используйте git commit» остаётся русской.
    /// </summary>
    public static int LinePlausibility(string line)
    {
        int latin = 0, cyrillic = 0, rest = 0;
        foreach (var raw in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            int clean = Plausibility(raw);
            if (clean == 0) continue;
            bool hasLatin = raw.Any(ch => char.IsLetter(ch) && ch < 'ɐ'), hasCyr = raw.Any(ch => ch is >= 'Ѐ' and <= 'ӿ');
            if (hasLatin) latin += clean; else if (hasCyr) cyrillic += clean; else rest += clean;
        }
        int main = Math.Max(latin, cyrillic), minor = Math.Min(latin, cyrillic);
        return 2 * (main + rest) + minor;
    }

    /// <summary>Коэффициент уменьшения, чтобы длинная сторона не превышала предел движка (не увеличивает).</summary>
    public static double ScaleToFit(int width, int height, int maxDimension)
    {
        int longest = Math.Max(width, height);
        return longest <= maxDimension ? 1.0 : (double)maxDimension / longest;
    }

    public static string JoinLines(IReadOnlyList<IReadOnlyList<string>> lines)
        => string.Join(Environment.NewLine, lines.Select(words => string.Join(" ", words)));
}
