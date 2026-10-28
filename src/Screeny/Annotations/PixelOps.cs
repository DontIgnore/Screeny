namespace Screeny.Annotations;

/// <summary>Операции над BGRA32-буфером (stride = width*4), на месте.</summary>
public static class PixelOps
{
    public static void Pixelate(byte[] px, int width, int height, int blockSize)
    {
        blockSize = Math.Max(1, blockSize);
        for (int by = 0; by < height; by += blockSize)
        for (int bx = 0; bx < width; bx += blockSize)
        {
            int bw = Math.Min(blockSize, width - bx), bh = Math.Min(blockSize, height - by);
            long b = 0, g = 0, r = 0, a = 0; int n = bw * bh;
            for (int y = by; y < by + bh; y++) for (int x = bx; x < bx + bw; x++)
            { int i = (y * width + x) * 4; b += px[i]; g += px[i + 1]; r += px[i + 2]; a += px[i + 3]; }
            byte ab = (byte)Math.Round(b / (double)n), ag = (byte)Math.Round(g / (double)n), ar = (byte)Math.Round(r / (double)n), aa = (byte)Math.Round(a / (double)n);
            for (int y = by; y < by + bh; y++) for (int x = bx; x < bx + bw; x++)
            { int i = (y * width + x) * 4; px[i] = ab; px[i + 1] = ag; px[i + 2] = ar; px[i + 3] = aa; }
        }
    }

    /// <summary>
    /// Два прохода (горизонталь + вертикаль) усреднения окном 2r+1; края — clamp.
    /// Скользящая сумма: O(w·h) независимо от радиуса.
    /// </summary>
    /// <param name="scratch">Временный буфер не короче width*height*4 для повторного использования между вызовами; null — выделить.</param>
    public static void BoxBlur(byte[] px, int width, int height, int radius, byte[]? scratch = null)
    {
        if (radius <= 0 || width <= 0 || height <= 0) return;
        int len = width * height * 4;
        if (scratch is null || scratch.Length < len) scratch = new byte[len];
        HorizontalPass(px, scratch, width, height, radius);
        VerticalPass(scratch, px, width, height, radius);
    }

    private static void HorizontalPass(byte[] src, byte[] dst, int w, int h, int r)
    {
        int n = 2 * r + 1, last = w - 1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            int sb = 0, sg = 0, sr = 0, sa = 0;
            for (int k = -r; k <= r; k++)
            {
                int i = row + Math.Clamp(k, 0, last) * 4;
                sb += src[i]; sg += src[i + 1]; sr += src[i + 2]; sa += src[i + 3];
            }
            for (int x = 0; x < w; x++)
            {
                int o = row + x * 4;
                dst[o] = (byte)(sb / n); dst[o + 1] = (byte)(sg / n); dst[o + 2] = (byte)(sr / n); dst[o + 3] = (byte)(sa / n);
                int add = row + Math.Min(x + r + 1, last) * 4, sub = row + Math.Max(x - r, 0) * 4;
                sb += src[add] - src[sub]; sg += src[add + 1] - src[sub + 1];
                sr += src[add + 2] - src[sub + 2]; sa += src[add + 3] - src[sub + 3];
            }
        }
    }

    /// <summary>Построчно: суммы по столбцам в int[w*4] — последовательный доступ к памяти.</summary>
    private static void VerticalPass(byte[] src, byte[] dst, int w, int h, int r)
    {
        int n = 2 * r + 1, last = h - 1, rowLen = w * 4;
        var sums = new int[rowLen];
        for (int k = -r; k <= r; k++)
        {
            int row = Math.Clamp(k, 0, last) * rowLen;
            for (int c = 0; c < rowLen; c++) sums[c] += src[row + c];
        }
        for (int y = 0; y < h; y++)
        {
            int o = y * rowLen;
            for (int c = 0; c < rowLen; c++) dst[o + c] = (byte)(sums[c] / n);
            int add = Math.Min(y + r + 1, last) * rowLen, sub = Math.Max(y - r, 0) * rowLen;
            for (int c = 0; c < rowLen; c++) sums[c] += src[add + c] - src[sub + c];
        }
    }
}
