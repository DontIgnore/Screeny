using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace Screeny.Ocr;

/// <summary>Found QR or barcode: content, format and border in image pixels.</summary>
public sealed record DetectedCode(string Text, BarcodeFormat Format, Rect Box)
{
    /// <summary>Format label for plaque: “QR code”, “EAN-13” etc.</summary>
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

    /// <summary>Link can be offered to open (only http/https; see <see cref="CodeScanner.IsOpenableUrl"/>).</summary>
    public bool IsOpenableUrl => CodeScanner.IsOpenableUrl(Text);
}

/// <summary>
/// QR/barcode search ZXing.Net (Apache-2.0) in Bgra32 pixels — without System.Drawing.
/// Pure function: doesn't touch UI, can run outside UI thread.
/// </summary>
public static class CodeScanner
{
    public static readonly BarcodeFormat[] Formats =
    [
        BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.EAN_13, BarcodeFormat.EAN_8,
        BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.UPC_A,
    ];

    /// <summary>Image pixels in Bgra32 (stride = width × 4) — captured on image thread, recognized anywhere.</summary>
    public static byte[] Pixels(BitmapSource image)
    {
        var src = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var px = new byte[image.PixelWidth * image.PixelHeight * 4];
        src.CopyPixels(px, image.PixelWidth * 4, 0);
        return px;
    }

    public static IReadOnlyList<DetectedCode> Scan(BitmapSource image) => Scan(Pixels(image), image.PixelWidth, image.PixelHeight);

    /// <summary>
    /// All codes in image (multiple), including light on dark. Borders in image pixels,
    /// in reading order (top-down, left-right).
    /// </summary>
    public static IReadOnlyList<DetectedCode> Scan(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return [];
        var source = new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var found = new List<DetectedCode>();
        // Light code on dark background (dark theme) ZXing finds itself: TryInverted repeats search on inverted
        // brightness if direct found nothing.
        Collect(found, Decode(source), bgra, width, height);
        return found.OrderBy(c => Math.Round(c.Box.Top / 8)).ThenBy(c => c.Box.Left).ToList();
    }

    private static Result[]? Decode(LuminanceSource source)
    {
        var reader = new BarcodeReaderGeneric
        {
            // Rotated linear codes TryHarder finds itself and returns points in image coordinates;
            // AutoRotate would return them in rotated copy coordinates. QR independent of rotation.
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
            // ZXing throws internally on rare images; absence of code is not reason to break text mode.
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
    /// ResultPoints are not border: for QR they are centers of finder squares (≈3.5 modules inward from edge),
    /// for linear barcode — two points on one scanline. Build border around them.
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
            // Scanline height unknown: take fraction of length (EAN/UPC bars ≈ 0.7 width), at least 16 px.
            double len = Math.Max(w, h);
            double across = Math.Max(16, len * 0.35);
            double along = Math.Max(4, len * 0.04);
            box = w >= h
                ? new Rect(minX - along, (minY + maxY) / 2 - across, w + 2 * along, 2 * across)
                : new Rect((minX + maxX) / 2 - across, minY - along, 2 * across, h + 2 * along);
        }
        else
        {
            // QR version 1 has 14 modules between finder centers, 3.5 to edge → +25% each side.
            double pad = Math.Max(4, Math.Max(w, h) * 0.25);
            box = new Rect(minX - pad, minY - pad, w + 2 * pad, h + 2 * pad);
        }
        box.Intersect(new Rect(0, 0, width, height));
        return box;
    }

    private static bool IsLinear(Result r) => r.BarcodeFormat is not (BarcodeFormat.QR_CODE or BarcodeFormat.DATA_MATRIX);

    /// <summary>
    /// Linear barcode height by pixels: from scanline up/down while rows repeat its bar pattern (≥ 85% matches after brightness threshold). Vertical code — same by columns.
    ///
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
        if (hi - lo < 4) return guess; // pattern not repeated — keep estimate

        const double pad = 4;
        var box = horizontal
            ? new Rect(from - pad, lo - pad, to - from + 2 * pad, hi - lo + 1 + 2 * pad)
            : new Rect(lo - pad, from - pad, hi - lo + 1 + 2 * pad, to - from + 2 * pad);
        box.Intersect(new Rect(0, 0, width, height));
        return box;
    }

    /// <summary>
    /// Link from code can be offered only with explicit http/https scheme:
    /// <c>javascript:</c>, <c>file:</c>, <c>ms-settings:</c> and other protocols never open.
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
