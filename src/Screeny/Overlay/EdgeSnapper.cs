using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Screeny.Overlay;

/// <summary>Граница между пикселями: вертикальная — между столбцами <c>e − 1</c> и <c>e</c>, горизонтальная — между строками.</summary>
public enum EdgeAxis { Vertical, Horizontal }

/// <summary>
/// Прилипание края рамки: <paramref name="edge"/> — граница, куда сейчас встаёт край (как <see cref="PixelRect.X"/>
/// или <see cref="PixelRect.Right"/>), <paramref name="cross"/> — координата курсора по другой оси.
/// Возвращает границу, к которой край прилип, или null.
/// </summary>
public delegate int? EdgeSnap(EdgeAxis axis, int edge, int cross);

/// <summary>Яркость пикселя 0…255; абстракция над снимком, чтобы искать границы и в тестах на синтетике.</summary>
public interface ILumaSource
{
    int Width { get; }
    int Height { get; }
    int Luma(int x, int y);
}

/// <summary>Яркость из буфера Bgra32 (тесты, маленькие картинки).</summary>
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
/// Яркость снимка, читаемая лениво плитками 64×64 через <see cref="BitmapSource.CopyPixels(Int32Rect, Array, int, int)"/>:
/// на 4K копируется только окрестность курсора, а не весь кадр. Плитки кэшируются на время жизни объекта.
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
/// Поиск контрастной границы на снимке рядом с курсором — для прилипания края рамки выделения. Чистая логика.
/// <para>
/// Для вертикальной границы <c>e</c> берётся полоса строк <c>cross ± <see cref="BandHalf"/></c> и в каждой строке
/// перепад яркости <c>L(e) − L(e − 1)</c>. Граница — кандидат, если средний перепад (со знаком) по полосе не меньше
/// <see cref="MinContrast"/> и не меньше <see cref="MinAgreement"/> строк перепадают в ту же сторону хотя бы на
/// <see cref="MinStep"/>. Знак отсекает шум (у него перепады в разные стороны), порог — плавные градиенты,
/// согласие строк — текст и мелкие детали. Из соседних кандидатов берётся локальный максимум (размытая
/// на 2 px граница даёт одну позицию), из оставшихся — ближайший к <c>edge</c>.
/// </para>
/// Считается лениво и только в окрестности ±порог; результат по каждой границе кэшируется до <see cref="ResetCache"/>
/// (вызывается в начале жеста).
/// <para>
/// <see cref="FindNext"/> — та же оценка границы, но первая граница в одну сторону от точки (расстояния до элементов у
/// линейки). Для неё полосу берут уже (<paramref name="bandHalf"/>): кнопка высотой 24 px в полосе ±20 строк занимает
/// меньше <see cref="MinAgreement"/> строк, и её край не нашёлся бы.
/// </para>
/// </summary>
/// <param name="bandHalf">Половина полосы устойчивости, пиксели; по умолчанию <see cref="BandHalf"/>.</param>
public sealed class EdgeSnapper(ILumaSource luma, int bandHalf = EdgeSnapper.BandHalf)
{
    /// <summary>Половина высоты (ширины) полосы, по которой проверяется устойчивость границы, пиксели.</summary>
    public const int BandHalf = 20;
    /// <summary>Минимальный средний перепад яркости по полосе (0…255).</summary>
    public const double MinContrast = 16;
    /// <summary>Минимальный перепад в одной строке, чтобы она «согласилась» с границей.</summary>
    public const int MinStep = 8;
    /// <summary>Доля строк полосы, перепадающих в одну сторону.</summary>
    public const double MinAgreement = 0.8;
    /// <summary>Шаг сетки центра полосы вдоль края (для кэша), пиксели.</summary>
    private const int CrossStep = 4;
    /// <summary>Больше оценок в кэше не держим: наведение мышью без жеста иначе копило бы их без конца.</summary>
    private const int MaxCachedScores = 250_000;

    private readonly Dictionary<(EdgeAxis Axis, int Edge, int Cross), double> _scores = new();

    public static EdgeSnapper FromBgra(byte[] bgra, int width, int height, int stride) => new(new BgraLuma(bgra, width, height, stride));

    /// <summary>Яркость по Rec. 601 в целых: 0…255.</summary>
    public static int LumaOf(int r, int g, int b) => (r * 299 + g * 587 + b * 114) / 1000;

    /// <summary>Сбросить кэш оценок (новый жест: полоса вокруг курсора другая).</summary>
    public void ResetCache() => _scores.Clear();

    /// <summary>
    /// Ближайшая к <paramref name="edge"/> контрастная граница в пределах <paramref name="threshold"/> по оси
    /// <paramref name="axis"/>, устойчивая в полосе вокруг <paramref name="cross"/>; null — такой нет.
    /// </summary>
    public int? Find(EdgeAxis axis, int edge, int cross, int threshold)
    {
        int length = axis == EdgeAxis.Vertical ? luma.Width : luma.Height;
        int crossLength = axis == EdgeAxis.Vertical ? luma.Height : luma.Width;
        if (threshold < 0 || length < 2 || crossLength < 1) return null;
        // Полоса центрируется по сетке в CrossStep пикселей: при движении курсора вдоль края оценки берутся из кэша.
        cross = Math.Clamp(cross / CrossStep * CrossStep + CrossStep / 2, 0, crossLength - 1);
        int from = Math.Max(1, edge - threshold), to = Math.Min(length - 1, edge + threshold);
        int? best = null;
        for (int e = from; e <= to; e++)
        {
            double s = Score(axis, e, cross);
            if (s <= 0) continue;
            // Локальный максимум: на размытой границе из двух равных соседей берётся правый (нижний).
            if (s < Score(axis, e - 1, cross) || s <= Score(axis, e + 1, cross)) continue;
            if (best is null || Math.Abs(e - edge) < Math.Abs(best.Value - edge)) best = e;
        }
        return best;
    }

    /// <summary>
    /// Первая контрастная граница от <paramref name="from"/> в сторону <paramref name="direction"/> (−1 — к меньшим
    /// координатам, +1 — к большим) не дальше <paramref name="limit"/> включительно, устойчивая в полосе вокруг
    /// <paramref name="cross"/>; null — такой нет. Граница — как в <see cref="Find"/>: прошла пороги и локальный максимум.
    /// </summary>
    public int? FindNext(EdgeAxis axis, int from, int cross, int direction, int limit)
    {
        int length = axis == EdgeAxis.Vertical ? luma.Width : luma.Height;
        int crossLength = axis == EdgeAxis.Vertical ? luma.Height : luma.Width;
        if (direction is not (-1 or 1) || length < 2 || crossLength < 1) return null;
        cross = Math.Clamp(cross / CrossStep * CrossStep + CrossStep / 2, 0, crossLength - 1);
        limit = Math.Clamp(limit, 1, length - 1);
        if (direction < 0 ? from < limit : from > limit) return null; // начало уже за пределом
        from = Math.Clamp(from, 1, length - 1);
        for (int e = from; direction < 0 ? e >= limit : e <= limit; e += direction)
        {
            double s = Score(axis, e, cross);
            if (s <= 0) continue;
            if (s < Score(axis, e - 1, cross) || s <= Score(axis, e + 1, cross)) continue;
            return e;
        }
        return null;
    }

    /// <summary>Сила границы <paramref name="e"/> (средний |перепад|) или 0, если она не проходит пороги.</summary>
    private double Score(EdgeAxis axis, int e, int cross)
    {
        int length = axis == EdgeAxis.Vertical ? luma.Width : luma.Height;
        if (e < 1 || e > length - 1) return 0;
        var key = (axis, e, cross);
        if (_scores.TryGetValue(key, out var cached)) return cached;
        if (_scores.Count >= MaxCachedScores) _scores.Clear();

        int crossLength = axis == EdgeAxis.Vertical ? luma.Height : luma.Width;
        int c0 = Math.Max(0, cross - bandHalf), c1 = Math.Min(crossLength - 1, cross + bandHalf);
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
