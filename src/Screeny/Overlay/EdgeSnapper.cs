using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Screeny.Overlay;

/// <summary>Boundary between pixels: vertical — between columns <c>e − 1</c> and <c>e</c>, horizontal — between rows.</summary>
public enum EdgeAxis { Vertical, Horizontal }

/// <summary>
/// Border edge snap: <paramref name="edge"/> — boundary where edge now sits (like <see cref="PixelRect.X"/>
/// or <see cref="PixelRect.Right"/>), <paramref name="cross"/> — cursor coordinate on other axis.
/// Returns boundary edge snapped to, or null.
/// </summary>
public delegate int? EdgeSnap(EdgeAxis axis, int edge, int cross);

/// <summary>Pixel brightness 0…255; abstraction over screenshot to search edges in synthetic tests too.</summary>
public interface ILumaSource
{
    int Width { get; }
    int Height { get; }
    int Luma(int x, int y);
}

/// <summary>Brightness from Bgra32 buffer (tests, small images).</summary>
public sealed class BgraLuma : ILumaSource
{
    private readonly byte[] _px;
    private readonly int _stride;

    public BgraLuma(byte[] bgra, int width, int height, int stride)
    {
        if (bgra.Length < stride * height) throw new ArgumentException("Buffer is smaller than stride × height.", nameof(bgra));
        _px = bgra; _stride = stride; Width = width; Height = height;
    }

    public int Width { get; }
    public int Height { get; }
    public int Luma(int x, int y) { int i = y * _stride + x * 4; return EdgeSnapper.LumaOf(_px[i + 2], _px[i + 1], _px[i]); }
}

/// <summary>
/// Screenshot brightness read lazily in 64×64 tiles via <see cref="BitmapSource.CopyPixels(Int32Rect, Array, int, int)"/>:
/// on 4K only cursor neighborhood copied, not whole frame. Tiles cached for object lifetime.
/// </summary>
public sealed class BitmapLuma : ILumaSource
{
    private const int Tile = 64;
    private readonly BitmapSource _bitmap;
    private readonly Dictionary<(int, int), byte[]> _tiles = new();

    public BitmapLuma(BitmapSource bitmap)
    {
        _bitmap = bitmap.Format == PixelFormats.Bgra32 || bitmap.Format == PixelFormats.Bgr32 || bitmap.Format == PixelFormats.Pbgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        Width = bitmap.PixelWidth;
        Height = bitmap.PixelHeight;
    }

    public int Width { get; }
    public int Height { get; }

    public int Luma(int x, int y)
    {
        int tx = x / Tile, ty = y / Tile;
        if (!_tiles.TryGetValue((tx, ty), out var tile)) _tiles[(tx, ty)] = tile = Load(tx, ty);
        return tile[(y - ty * Tile) * Tile + (x - tx * Tile)];
    }

    private byte[] Load(int tx, int ty)
    {
        int x0 = tx * Tile, y0 = ty * Tile, w = Math.Min(Tile, Width - x0), h = Math.Min(Tile, Height - y0);
        var px = new byte[w * h * 4];
        _bitmap.CopyPixels(new Int32Rect(x0, y0, w, h), px, w * 4, 0);
        var luma = new byte[Tile * Tile];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                luma[y * Tile + x] = (byte)EdgeSnapper.LumaOf(px[i + 2], px[i + 1], px[i]);
            }
        return luma;
    }
}

/// <summary>
/// Contrast edge search on screenshot near cursor — for selection border edge snap. Pure logic.
/// <para>
/// For vertical boundary <c>e</c>, band of rows <c>cross ± <see cref="BandHalf"/></c> and in each row
/// brightness step <c>L(e) − L(e − 1)</c>. Boundary candidate if mean step (signed) over band ≥
/// <see cref="MinContrast"/> and ≥ <see cref="MinAgreement"/> rows step same direction by at least
/// <see cref="MinStep"/>. Sign filters noise (mixed directions), threshold smooth gradients,
/// row agreement text/small details. Among neighboring candidates take local maximum (blurred
/// 2 px edge gives one position), remaining — nearest to <c>edge</c>.
/// </para>
/// Computed lazily only within ±threshold neighborhood; result per edge cached until <see cref="ResetCache"/>
/// (called at gesture start).
/// </summary>
public sealed class EdgeSnapper(ILumaSource luma)
{
    /// <summary>Half height/width of band for edge stability check, pixels.</summary>
    public const int BandHalf = 20;
    /// <summary>Minimum mean brightness step over band (0…255).</summary>
    public const double MinContrast = 16;
    /// <summary>Minimum step in one row for it to “agree” with edge.</summary>
    public const int MinStep = 8;
    /// <summary>Fraction of band rows stepping one direction.</summary>
    public const double MinAgreement = 0.8;
    /// <summary>Band center grid step along edge (for cache), pixels.</summary>
    private const int CrossStep = 4;

    private readonly Dictionary<(EdgeAxis Axis, int Edge, int Cross), double> _scores = new();

    public static EdgeSnapper FromBgra(byte[] bgra, int width, int height, int stride) => new(new BgraLuma(bgra, width, height, stride));

    /// <summary>Brightness by Rec. 601 integer: 0…255.</summary>
    public static int LumaOf(int r, int g, int b) => (r * 299 + g * 587 + b * 114) / 1000;

    /// <summary>Reset estimate cache (new gesture: band around cursor different).</summary>
    public void ResetCache() => _scores.Clear();

    /// <summary>
    /// Nearest contrast edge to <paramref name="edge"/> within <paramref name="threshold"/> on axis
    /// <paramref name="axis"/>, stable in band around <paramref name="cross"/>; null — none.
    /// </summary>
    public int? Find(EdgeAxis axis, int edge, int cross, int threshold)
    {
        int length = axis == EdgeAxis.Vertical ? luma.Width : luma.Height;
        int crossLength = axis == EdgeAxis.Vertical ? luma.Height : luma.Width;
        if (threshold < 0 || length < 2 || crossLength < 1) return null;
        // Band centered on grid at CrossStep pixels: moving cursor along edge uses cached estimates.
        cross = Math.Clamp(cross / CrossStep * CrossStep + CrossStep / 2, 0, crossLength - 1);
        int from = Math.Max(1, edge - threshold), to = Math.Min(length - 1, edge + threshold);
        int? best = null;
        for (int e = from; e <= to; e++)
        {
            double s = Score(axis, e, cross);
            if (s <= 0) continue;
            // Local maximum: on blurred edge of two equal neighbors take right (bottom).
            if (s < Score(axis, e - 1, cross) || s <= Score(axis, e + 1, cross)) continue;
            if (best is null || Math.Abs(e - edge) < Math.Abs(best.Value - edge)) best = e;
        }
        return best;
    }

    /// <summary>Edge strength of <paramref name="e"/> (mean |step|) or 0 if fails thresholds.</summary>
    private double Score(EdgeAxis axis, int e, int cross)
    {
        int length = axis == EdgeAxis.Vertical ? luma.Width : luma.Height;
        if (e < 1 || e > length - 1) return 0;
        var key = (axis, e, cross);
        if (_scores.TryGetValue(key, out var cached)) return cached;

        int crossLength = axis == EdgeAxis.Vertical ? luma.Height : luma.Width;
        int c0 = Math.Max(0, cross - BandHalf), c1 = Math.Min(crossLength - 1, cross + BandHalf);
        int n = c1 - c0 + 1, sum = 0, up = 0, down = 0;
        for (int c = c0; c <= c1; c++)
        {
            int d = axis == EdgeAxis.Vertical ? luma.Luma(e, c) - luma.Luma(e - 1, c) : luma.Luma(c, e) - luma.Luma(c, e - 1);
            sum += d;
            if (d >= MinStep) up++;
            else if (d <= -MinStep) down++;
        }
        double mean = (double)sum / n;
        int agree = mean >= 0 ? up : down;
        double score = Math.Abs(mean) >= MinContrast && agree >= MinAgreement * n ? Math.Abs(mean) : 0;
        return _scores[key] = score;
    }
}
