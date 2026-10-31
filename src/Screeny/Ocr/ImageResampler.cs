namespace Screeny.Ocr;

/// <summary>
/// BGRA image scaling with bicubic Catmull-Rom filter, separable axes.
/// For OCR letter edge sharpness matters: bilinear upscale blurs, nearest neighbor gives jaggies.
/// </summary>
public static class ImageResampler
{
    public static byte[] ResizeBgra(byte[] src, int w, int h, int nw, int nh)
    {
        if (nw == w && nh == h) return (byte[])src.Clone();
        var tmp = new float[nw * h * 4];
        // Horizontal: w → nw.
        var (hIdx, hWts) = Kernel(w, nw);
        Parallel.For(0, h, y =>
        {
            int row = y * w * 4, outRow = y * nw * 4;
            for (int x = 0; x < nw; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < 4; k++)
                {
                    int i = row + hIdx[x * 4 + k] * 4; float wt = hWts[x * 4 + k];
                    b += src[i] * wt; g += src[i + 1] * wt; r += src[i + 2] * wt; a += src[i + 3] * wt;
                }
                int o = outRow + x * 4;
                tmp[o] = b; tmp[o + 1] = g; tmp[o + 2] = r; tmp[o + 3] = a;
            }
        });
        // Vertical: h → nh.
        var dst = new byte[nw * nh * 4];
        var (vIdx, vWts) = Kernel(h, nh);
        Parallel.For(0, nh, y =>
        {
            for (int x = 0; x < nw; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < 4; k++)
                {
                    int i = (vIdx[y * 4 + k] * nw + x) * 4; float wt = vWts[y * 4 + k];
                    b += tmp[i] * wt; g += tmp[i + 1] * wt; r += tmp[i + 2] * wt; a += tmp[i + 3] * wt;
                }
                int o = (y * nw + x) * 4;
                dst[o] = Clamp(b); dst[o + 1] = Clamp(g); dst[o + 2] = Clamp(r); dst[o + 3] = Clamp(a);
            }
        });
        return dst;
    }

    /// <summary>For each output pixel — four source indices (clamped) and Catmull-Rom weights.</summary>
    private static (int[] Index, float[] Weight) Kernel(int size, int newSize)
    {
        var idx = new int[newSize * 4];
        var wts = new float[newSize * 4];
        double ratio = (double)size / newSize;
        for (int o = 0; o < newSize; o++)
        {
            double center = (o + 0.5) * ratio - 0.5;
            int c = (int)Math.Floor(center);
            double t = center - c;
            for (int k = 0; k < 4; k++)
            {
                idx[o * 4 + k] = Math.Clamp(c - 1 + k, 0, size - 1);
                wts[o * 4 + k] = (float)CatmullRom(k - 1 - t);
            }
        }
        return (idx, wts);
    }

    private static double CatmullRom(double x)
    {
        x = Math.Abs(x);
        if (x < 1) return 1.5 * x * x * x - 2.5 * x * x + 1;
        if (x < 2) return -0.5 * x * x * x + 2.5 * x * x - 4 * x + 2;
        return 0;
    }

    private static byte Clamp(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);
}
