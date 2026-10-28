using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace Screeny.Ocr;

/// <summary>Найденный QR- или штрихкод: содержимое, формат и рамка в пикселях картинки.</summary>
public sealed record DetectedCode(string Text, BarcodeFormat Format, Rect Box)
{
    /// <summary>Подпись формата для плашки: «QR-код», «EAN-13» и т.п.</summary>
    public string FormatName => Format switch
    {
        BarcodeFormat.QR_CODE => Localization.Loc.T("Codes.QrCode"),
        BarcodeFormat.DATA_MATRIX => "Data Matrix",
        BarcodeFormat.EAN_13 => "EAN-13",
        BarcodeFormat.EAN_8 => "EAN-8",
        BarcodeFormat.UPC_A => "UPC-A",
        BarcodeFormat.CODE_128 => "Code 128",
        BarcodeFormat.CODE_39 => "Code 39",
        _ => Format.ToString(),
    };

    /// <summary>Ссылку можно предложить открыть (только http/https; см. <see cref="CodeScanner.IsOpenableUrl"/>).</summary>
    public bool IsOpenableUrl => CodeScanner.IsOpenableUrl(Text);
}

/// <summary>
/// Поиск QR- и штрихкодов ZXing.Net (Apache-2.0) в пикселях Bgra32 — без System.Drawing.
/// Чистая функция: не трогает UI и может работать вне UI-потока.
/// </summary>
public static class CodeScanner
{
    public static readonly BarcodeFormat[] Formats =
    [
        BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.EAN_13, BarcodeFormat.EAN_8,
        BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.UPC_A,
    ];

    /// <summary>Пиксели картинки в Bgra32 (stride = ширина × 4) — снимаются в потоке картинки, распознаются где угодно.</summary>
    public static byte[] Pixels(BitmapSource image)
    {
        var src = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var px = new byte[image.PixelWidth * image.PixelHeight * 4];
        src.CopyPixels(px, image.PixelWidth * 4, 0);
        return px;
    }

    public static IReadOnlyList<DetectedCode> Scan(BitmapSource image) => Scan(Pixels(image), image.PixelWidth, image.PixelHeight);

    /// <summary>
    /// Все коды в картинке (несколько сразу), включая светлые на тёмном. Рамки — в пикселях картинки,
    /// по порядку чтения (сверху вниз, слева направо).
    /// </summary>
    public static IReadOnlyList<DetectedCode> Scan(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return [];
        var source = new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var found = new List<DetectedCode>();
        // Светлый код на тёмном фоне (тёмная тема) ZXing ищет сам: TryInverted повторяет поиск по инвертированной
        // яркости, если в прямой ничего не нашлось.
        Collect(found, Decode(source), bgra, width, height);
        return found.OrderBy(c => Math.Round(c.Box.Top / 8)).ThenBy(c => c.Box.Left).ToList();
    }

    private static Result[]? Decode(LuminanceSource source)
    {
        var reader = new BarcodeReaderGeneric
        {
            // Повёрнутые линейные коды TryHarder ищет сам и возвращает точки в координатах картинки;
            // AutoRotate вернул бы их в координатах повёрнутой копии. QR от поворота не зависит.
            AutoRotate = false,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = Formats,
            },
        };
        try { return reader.DecodeMultiple(source); }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            // ZXing на редких картинках бросает изнутри; отсутствие кода — не повод ломать режим текста.
            Screeny.Logging.Log.Error("Barcode decode failed", ex);
            return null;
        }
    }

    private static void Collect(List<DetectedCode> found, Result[]? results, byte[] bgra, int width, int height)
    {
        if (results is null) return;
        foreach (var r in results)
        {
            if (string.IsNullOrEmpty(r.Text)) continue;
            var box = BoxOf(r, width, height);
            if (IsLinear(r) && !box.IsEmpty) box = FitLinear(r, box, bgra, width, height);
            if (box.IsEmpty) continue;
            var code = new DetectedCode(r.Text, r.BarcodeFormat, box);
            if (!found.Any(f => f.Text == code.Text && f.Format == code.Format && f.Box.IntersectsWith(code.Box))) found.Add(code);
        }
    }

    /// <summary>
    /// ResultPoints — не рамка: у QR это центры поисковых квадратов (≈3,5 модуля внутрь от края),
    /// у линейного штрихкода — две точки на одной строке сканирования. Достраиваем рамку вокруг них.
    /// </summary>
    public static Rect BoxOf(Result r, int width, int height)
    {
        var pts = r.ResultPoints?.Where(p => p is not null).ToArray() ?? [];
        if (pts.Length == 0) return Rect.Empty;
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        double w = maxX - minX, h = maxY - minY;
        bool linear = pts.Length <= 2 || IsLinear(r);
        Rect box;
        if (linear)
        {
            // Высоту строки сканирования не знаем: берём долю длины (у EAN/UPC штрихи ≈ 0,7 ширины), не меньше 16 px.
            double len = Math.Max(w, h);
            double across = Math.Max(16, len * 0.35);
            double along = Math.Max(4, len * 0.04);
            box = w >= h
                ? new Rect(minX - along, (minY + maxY) / 2 - across, w + 2 * along, 2 * across)
                : new Rect((minX + maxX) / 2 - across, minY - along, 2 * across, h + 2 * along);
        }
        else
        {
            // У QR версии 1 между центрами поисковых квадратов 14 модулей, а до края 3,5 → +25 % с каждой стороны.
            double pad = Math.Max(4, Math.Max(w, h) * 0.25);
            box = new Rect(minX - pad, minY - pad, w + 2 * pad, h + 2 * pad);
        }
        box.Intersect(new Rect(0, 0, width, height));
        return box;
    }

    private static bool IsLinear(Result r) => r.BarcodeFormat is not (BarcodeFormat.QR_CODE or BarcodeFormat.DATA_MATRIX);

    /// <summary>
    /// Высота линейного штрихкода по пикселям: от строки сканирования вверх и вниз, пока строки повторяют
    /// её рисунок штрихов (≥ 85 % совпадений после порога по яркости). Вертикальный код — то же по столбцам.
    /// </summary>
    private static Rect FitLinear(Result r, Rect guess, byte[] bgra, int width, int height)
    {
        var pts = r.ResultPoints.Where(p => p is not null).ToArray();
        if (pts.Length < 2) return guess;
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        bool horizontal = maxX - minX >= maxY - minY;
        int length = horizontal ? width : height, across = horizontal ? height : width;
        int from = Math.Clamp((int)Math.Floor(horizontal ? minX : minY), 0, length - 1);
        int to = Math.Clamp((int)Math.Ceiling(horizontal ? maxX : maxY), from, length - 1);
        int line = (int)Math.Round(horizontal ? (minY + maxY) / 2 : (minX + maxX) / 2);
        if (line < 0 || line >= across || to - from < 8) return guess;

        bool Dark(int along, int at)
        {
            int x = horizontal ? along : at, y = horizontal ? at : along;
            int i = (y * width + x) * 4;
            return bgra[i] * 114 + bgra[i + 1] * 587 + bgra[i + 2] * 299 < 128_000;
        }
        var reference = new bool[to - from + 1];
        for (int k = 0; k < reference.Length; k++) reference[k] = Dark(from + k, line);
        bool Matches(int at)
        {
            int same = 0;
            for (int k = 0; k < reference.Length; k++) if (Dark(from + k, at) == reference[k]) same++;
            return same >= reference.Length * 0.85;
        }
        int lo = line, hi = line;
        while (lo > 0 && Matches(lo - 1)) lo--;
        while (hi < across - 1 && Matches(hi + 1)) hi++;
        if (hi - lo < 4) return guess; // рисунок не повторяется — оставляем оценку

        const double pad = 4;
        var box = horizontal
            ? new Rect(from - pad, lo - pad, to - from + 2 * pad, hi - lo + 1 + 2 * pad)
            : new Rect(lo - pad, from - pad, hi - lo + 1 + 2 * pad, to - from + 2 * pad);
        box.Intersect(new Rect(0, 0, width, height));
        return box;
    }

    /// <summary>
    /// Ссылку из кода можно предложить открыть только с явной схемой http или https:
    /// <c>javascript:</c>, <c>file:</c>, <c>ms-settings:</c> и прочие протоколы не открываются никогда.
    /// </summary>
    public static bool IsOpenableUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (trimmed.Any(char.IsControl)) return false;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host);
    }
}
