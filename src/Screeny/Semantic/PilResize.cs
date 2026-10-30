namespace Screeny.Semantic;

/// <summary>
/// Порт 8-битного бикубического ресемплинга Pillow (libImaging/Resample.c: precompute_coeffs, normalize_coeffs_8bpc,
/// ImagingResampleHorizontal/Vertical_8bpc) для RGB24 построчно. Свёртка со сглаживанием, сначала по горизонтали,
/// коэффициенты с фиксированной точкой (22 дробных бита), промежуточный результат в uint8. Так готовит картинки
/// transformers для EmbeddingGemma 2 (torchvision — порт того же кода): вектор из приложения совпадает с эталонным
/// (косинус ≥ 0,9998, см. tools/semantic-module/README.md).
/// </summary>
public static class PilResize
{
    private const int PrecisionBits = 32 - 8 - 2;

    private static double Bicubic(double x)
    {
        if (x < 0) x = -x;
        const double a = -0.5;
        if (x < 1) return ((a + 2) * x - (a + 3)) * x * x + 1;
        if (x < 2) return (((x - 5) * x + 8) * x - 4) * a;
        return 0;
    }

    private static int Coeffs(int inSize, int outSize, out int[] bounds, out int[] kk)
    {
        double scale = (double)inSize / outSize;
        double filterscale = scale < 1.0 ? 1.0 : scale;
        double support = 2.0 * filterscale;
        int ksize = (int)Math.Ceiling(support) * 2 + 1;
        var pre = new double[outSize * ksize];
        bounds = new int[outSize * 2];
        for (int xx = 0; xx < outSize; xx++)
        {
            double center = (xx + 0.5) * scale;
            double ww = 0.0, ss = 1.0 / filterscale;
            int xmin = (int)(center - support + 0.5);
            if (xmin < 0) xmin = 0;
            int xmax = (int)(center + support + 0.5);
            if (xmax > inSize) xmax = inSize;
            xmax -= xmin;
            for (int x = 0; x < xmax; x++)
            {
                double w = Bicubic((x + xmin - center + 0.5) * ss);
                pre[xx * ksize + x] = w;
                ww += w;
            }
            for (int x = 0; x < xmax; x++)
                if (ww != 0.0) pre[xx * ksize + x] /= ww;
            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }
        kk = new int[pre.Length];
        for (int i = 0; i < pre.Length; i++)
            kk[i] = pre[i] < 0 ? (int)(-0.5 + pre[i] * (1 << PrecisionBits)) : (int)(0.5 + pre[i] * (1 << PrecisionBits));
        return ksize;
    }

    private static byte Clip8(int v)
    {
        v >>= PrecisionBits;
        return v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
    }

    /// <summary>RGB24 (по строкам, шаг 3·ширина) → <paramref name="outW"/>×<paramref name="outH"/>, как <c>PIL.Image.resize(…, BICUBIC)</c>.</summary>
    public static byte[] Resize(byte[] src, int width, int height, int outW, int outH)
    {
        if (outW == width && outH == height) return (byte[])src.Clone();
        bool needH = outW != width, needV = outH != height;
        int ksH = Coeffs(width, outW, out var bH, out var kH);
        int ksV = Coeffs(height, outH, out var bV, out var kV);
        int yFirst = bV[0], yLast = bV[outH * 2 - 2] + bV[outH * 2 - 1];

        byte[] cur = src;
        int curW = width;
        if (needH)
        {
            for (int i = 0; i < outH; i++) bV[i * 2] -= yFirst;
            int rows = yLast - yFirst;
            var tmp = new byte[outW * rows * 3];
            for (int y = 0; y < rows; y++)
            {
                int srow = (y + yFirst) * width * 3, drow = y * outW * 3;
                for (int xx = 0; xx < outW; xx++)
                {
                    int xmin = bH[xx * 2], xmax = bH[xx * 2 + 1], k = xx * ksH;
                    int s0 = 1 << (PrecisionBits - 1), s1 = s0, s2 = s0;
                    for (int x = 0; x < xmax; x++)
                    {
                        int p = srow + (x + xmin) * 3, w = kH[k + x];
                        s0 += src[p] * w; s1 += src[p + 1] * w; s2 += src[p + 2] * w;
                    }
                    tmp[drow + xx * 3] = Clip8(s0); tmp[drow + xx * 3 + 1] = Clip8(s1); tmp[drow + xx * 3 + 2] = Clip8(s2);
                }
            }
            cur = tmp; curW = outW;
        }
        // Только по горизонтали: во временной картинке уже все строки (yFirst = 0, yLast = height).
        if (!needV) return cur;
        var dst = new byte[outW * outH * 3];
        for (int yy = 0; yy < outH; yy++)
        {
            int ymin = bV[yy * 2], ymax = bV[yy * 2 + 1], k = yy * ksV, drow = yy * outW * 3;
            for (int xx = 0; xx < outW * 3; xx++)
            {
                int s = 1 << (PrecisionBits - 1);
                for (int y = 0; y < ymax; y++) s += cur[(y + ymin) * curW * 3 + xx] * kV[k + y];
                dst[drow + xx] = Clip8(s);
            }
        }
        return dst;
    }
}
