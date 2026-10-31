using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Screeny.Ocr.Paddle;

/// <summary>BGRA image (as WPF CopyPixels gives for Bgra32).</summary>
public sealed record OcrImage(byte[] Bgra, int W, int H)
{
    /// <summary>Pixels of frozen image; callable from any thread.</summary>
    public static OcrImage FromBitmap(BitmapSource bmp)
    {
        BitmapSource src = bmp.Format == PixelFormats.Bgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        var px = new byte[src.PixelWidth * 4 * src.PixelHeight];
        src.CopyPixels(px, src.PixelWidth * 4, 0);
        return new OcrImage(px, src.PixelWidth, src.PixelHeight);
    }
}

/// <summary>Recognized line fragment: text, CTC confidence and horizontal bounds of each character
/// in pixels from crop left edge (by CTC steps).</summary>
public sealed record RecResult(string Text, float Confidence, double[] CharLeft, double[] CharRight);

/// <summary>
/// PP-OCR pre/post-processing (DB detector + SVTR recognizer with CTC) without OpenCV and ONNX Runtime: everything
/// testable without models. Parameters below are best config from quality/speed measurements on small
/// screen text set (Cyrillic and Latin). Module with models contains only data and native
/// runtime, so edits here don't require new module version.
/// </summary>
public static class PaddlePipeline
{
    // DB post-processing as in PaddleX 3.x (OCR.yaml).
    public const double Thresh = 0.3, BoxThresh = 0.6, Unclip = 1.5;
    /// <summary>Line wider than this (after resize to height 48) split into chunks by word gaps.</summary>
    public const int MaxRecWidth = 480;
    /// <summary>Areas up to 0.25 MP upscaled ×2 before detection: otherwise detector loses start of small line.</summary>
    public const long UpscaleMaxPixels = 250_000;
    /// <summary>
    /// Fragment up to <see cref="ShortFragmentLength"/> chars with CTC confidence below threshold discarded: usually icon
    /// or border taken for text. Threshold tuned on one real screenshot with nick list (0.6 to 0.9
    /// unchanged) — overfitting risk; recheck on new samples.
    /// </summary>
    public const double MinShortConfidence = 0.6;
    public const int ShortFragmentLength = 4;
    public const int RecHeight = 48;

    /// <summary>
    /// Thread scheme: <c>par</c> lines parallel, each session <c>threads</c> ORT threads. By measurements
    /// (13700K, 24 logical cores) best is 4×4: total threads about two-thirds logical cores, equal between
    /// parallel lines, no more than 4×4 (no gain beyond). On 8 logical — 2×2, on 4 — 1×2, on 2 — 1×1.
    /// </summary>
    public static (int Par, int Threads) ThreadSplit(int processorCount)
    {
        int n = Math.Max(1, processorCount * 2 / 3);
        int par = Math.Clamp((int)Math.Floor(Math.Sqrt(n)), 1, 4);
        return (par, Math.Clamp(n / par, 1, 4));
    }

    /// <summary>Multiplier before detection: ×2 for small areas.</summary>
    public static double DetScaleFor(int w, int h) => (long)w * h <= UpscaleMaxPixels ? 2 : 1;

    /// <summary>Detector input size: native resolution (short side ≥ 64, long ≤ 4000), sides multiple of 32.</summary>
    public static (int W, int H) DetInputSize(int w, int h)
    {
        double ratio = Math.Min(w, h) < 64 ? 64.0 / Math.Min(w, h) : 1;
        if (Math.Max(w, h) * ratio > 4000) ratio = 4000.0 / Math.Max(w, h);
        return (Math.Max((int)Math.Round(w * ratio / 32) * 32, 32), Math.Max((int)Math.Round(h * ratio / 32) * 32, 32));
    }

    private static readonly float[] DetMean = [0.485f, 0.456f, 0.406f], DetStd = [0.229f, 0.224f, 0.225f];
    public static readonly float[] DetScale = DetStd.Select(s => 1 / (255f * s)).ToArray();
    public static readonly float[] DetBias = DetMean.Zip(DetStd, (m, s) => -m / s).ToArray();
    public static readonly float[] RecScale = [1 / 127.5f, 1 / 127.5f, 1 / 127.5f], RecBias = [-1f, -1f, -1f];

    /// <summary>DB post-processing: binarization, connected components (8-connectivity), score, unclip, to image coordinates.
    /// Screen text is horizontal, so instead of minAreaRect and contours — component rectangles.</summary>
    public static List<Int32Rect> Boxes(float[] prob, int mw, int mh, int dw, int dh)
    {
        var seen = new bool[prob.Length];
        var boxes = new List<Int32Rect>();
        var stack = new Stack<int>();
        for (int start = 0; start < prob.Length; start++)
        {
            if (seen[start] || prob[start] <= Thresh) continue;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            stack.Push(start); seen[start] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop(), px = p % mw, py = p / mw;
                if (px < x0) x0 = px;
                if (px > x1) x1 = px;
                if (py < y0) y0 = py;
                if (py > y1) y1 = py;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx, ny = py + dy;
                        if (nx < 0 || ny < 0 || nx >= mw || ny >= mh) continue;
                        int q = ny * mw + nx;
                        if (!seen[q] && prob[q] > Thresh) { seen[q] = true; stack.Push(q); }
                    }
            }
            double w = x1 - x0, h = y1 - y0;
            if (Math.Min(w, h) < 3) continue;
            double sum = 0;
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) sum += prob[y * mw + x];
            if (sum / ((x1 - x0 + 1) * (y1 - y0 + 1)) < BoxThresh) continue;
            double d = w * h * Unclip / (2 * (w + h));
            if (Math.Min(w, h) + 2 * d < 5) continue;
            double sx = (double)dw / mw, sy = (double)dh / mh;
            int bx0 = Math.Clamp((int)Math.Round((x0 - d) * sx), 0, dw), by0 = Math.Clamp((int)Math.Round((y0 - d) * sy), 0, dh);
            int bx1 = Math.Clamp((int)Math.Round((x1 + d) * sx), 0, dw), by1 = Math.Clamp((int)Math.Round((y1 + d) * sy), 0, dh);
            if (bx1 - bx0 >= 2 && by1 - by0 >= 2) boxes.Add(new Int32Rect(bx0, by0, bx1 - bx0, by1 - by0));
        }
        return boxes;
    }

    /// <summary>
    /// Splits crop into chunks no wider than <paramref name="maxRecWidth"/> (at height 48 scale) by columns without ink:
    /// background — median brightness along crop edge, gap — at least quarter of line height. If no gap,
    /// splits anywhere (no space). In rec-model PP-OCRv5 there's global attention along line: time/memory
    /// grow faster than width, and input wider than 3200 is also squeezed.
    /// </summary>
    public static List<(Int32Rect Box, bool SpaceBefore)> Split(OcrImage img, Int32Rect r, int maxRecWidth)
    {
        double scale = (double)RecHeight / Math.Max(1, r.Height);
        if (maxRecWidth <= 0 || r.Width * scale <= maxRecWidth) return [(r, false)];
        byte Lum(int x, int y) { int o = (y * img.W + x) * 4; return (byte)((img.Bgra[o] * 29 + img.Bgra[o + 1] * 150 + img.Bgra[o + 2] * 77) >> 8); }
        var border = new List<byte>();
        for (int x = r.X; x < r.X + r.Width; x++) { border.Add(Lum(x, r.Y)); border.Add(Lum(x, r.Y + r.Height - 1)); }
        border.Sort();
        int bg = border[border.Count / 2];
        var empty = new bool[r.Width];
        for (int x = 0; x < r.Width; x++)
        {
            bool ink = false;
            for (int y = r.Y; y < r.Y + r.Height && !ink; y++) ink = Math.Abs(Lum(r.X + x, y) - bg) > 48;
            empty[x] = !ink;
        }
        // Midpoints of word gaps.
        var cuts = new List<int>();
        int minGap = Math.Max(2, r.Height / 4);
        for (int x = 0; x < r.Width;)
        {
            if (!empty[x]) { x++; continue; }
            int s = x;
            while (x < r.Width && empty[x]) x++;
            if (x - s >= minGap && s > 0 && x < r.Width) cuts.Add((s + x) / 2);
        }
        int maxPx = (int)(maxRecWidth / scale);
        var result = new List<(Int32Rect, bool)>();
        int start = 0; bool space = false;
        while (r.Width - start > maxPx)
        {
            int cut = cuts.Where(c => c > start + maxPx / 4 && c <= start + maxPx).DefaultIfEmpty(-1).Max();
            bool atGap = cut > 0;
            if (!atGap) cut = start + maxPx;
            result.Add((new Int32Rect(r.X + start, r.Y, cut - start, r.Height), space));
            start = cut; space = atGap;
        }
        result.Add((new Int32Rect(r.X + start, r.Y, r.Width - start, r.Height), space));
        return result;
    }

    /// <summary>Rec input width for crop: proportional to height 48.</summary>
    public static int RecWidth(Int32Rect crop) => Math.Max(1, (int)Math.Ceiling((double)RecHeight * crop.Width / Math.Max(1, crop.Height)));

    /// <summary>CTC greedy: 0 — blank, k — dict[k−1], class after dictionary — space.</summary>
    /// <param name="stepPx">CTC step width in crop pixels.</param>
    public static RecResult Decode(ReadOnlySpan<float> probs, int T, int C, string[] dict, double stepPx)
    {
        var sb = new System.Text.StringBuilder();
        var left = new List<double>(); var right = new List<double>();
        int prev = -1; double conf = 0; int n = 0;
        for (int t = 0; t < T; t++)
        {
            int best = 0; float bp = probs[t * C];
            for (int c = 1; c < C; c++) if (probs[t * C + c] > bp) { bp = probs[t * C + c]; best = c; }
            if (best != 0 && best != prev)
            {
                sb.Append(best - 1 < dict.Length ? dict[best - 1] : " ");
                left.Add(t * stepPx); right.Add((t + 1) * stepPx); conf += bp; n++;
            }
            prev = best;
        }
        return new RecResult(sb.ToString(), n > 0 ? (float)(conf / n) : 0, left.ToArray(), right.ToArray());
    }

    /// <summary>Rec-model dictionary from ONNX metadata (key <c>character</c>): ends with newline, last empty
    /// element is not character.</summary>
    public static string[] ParseDict(string character) => character.TrimEnd('\n').Split('\n');

    /// <summary>Chunks of one line (<see cref="Split"/>) back to line: space at gap cut, character borders
    /// — in pixels from original crop left edge, confidence — average over characters.</summary>
    public static RecResult Join(Int32Rect crop, IReadOnlyList<(Int32Rect Box, bool SpaceBefore, RecResult R)> chunks)
    {
        var text = new System.Text.StringBuilder();
        var left = new List<double>(); var right = new List<double>();
        double conf = 0; int n = 0;
        foreach (var (box, spaceBefore, r) in chunks)
        {
            double dx = box.X - crop.X;
            if (spaceBefore && text.Length > 0 && r.Text.Length > 0 && text[^1] != ' ' && r.Text[0] != ' ')
            { text.Append(' '); left.Add(dx); right.Add(dx); }
            text.Append(r.Text);
            left.AddRange(r.CharLeft.Select(x => x + dx)); right.AddRange(r.CharRight.Select(x => x + dx));
            conf += r.Confidence * r.Text.Length; n += r.Text.Length;
        }
        return new RecResult(text.ToString(), n > 0 ? (float)(conf / n) : 0, left.ToArray(), right.ToArray());
    }

    /// <summary>Fragment kept: not short or confident (see <see cref="MinShortConfidence"/>). Threshold on fragment
    /// as whole, not individual characters.</summary>
    public static bool Keep(RecResult r) => r.Text.Trim().Length > ShortFragmentLength || r.Confidence >= MinShortConfidence;

    /// <summary>Bilinear area resize in cv2.INTER_LINEAR style (pixel centers) straight into CHW tensor:
    /// dst[off + c·plane + y·stride + x] = v·scale[c] + bias[c], channels B,G,R.</summary>
    public static void ResizeInto(OcrImage img, Int32Rect r, int dw, int dh, float[] dst, int off, int stride, int plane, float[] scale, float[] bias)
    {
        double sx = (double)r.Width / dw, sy = (double)r.Height / dh;
        for (int y = 0; y < dh; y++)
        {
            double fy = (y + 0.5) * sy - 0.5; int iy = (int)Math.Floor(fy); double wy = fy - iy;
            int y0 = Math.Clamp(iy, 0, r.Height - 1) + r.Y, y1 = Math.Clamp(iy + 1, 0, r.Height - 1) + r.Y;
            for (int x = 0; x < dw; x++)
            {
                double fx = (x + 0.5) * sx - 0.5; int ix = (int)Math.Floor(fx); double wx = fx - ix;
                int x0 = Math.Clamp(ix, 0, r.Width - 1) + r.X, x1 = Math.Clamp(ix + 1, 0, r.Width - 1) + r.X;
                int a = (y0 * img.W + x0) * 4, b = (y0 * img.W + x1) * 4, c = (y1 * img.W + x0) * 4, d = (y1 * img.W + x1) * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    double v = (img.Bgra[a + ch] * (1 - wx) + img.Bgra[b + ch] * wx) * (1 - wy) + (img.Bgra[c + ch] * (1 - wx) + img.Bgra[d + ch] * wx) * wy;
                    dst[off + ch * plane + y * stride + x] = (float)v * scale[ch] + bias[ch];
                }
            }
        }
    }

    public static OcrImage Upscale(OcrImage src, double s)
    {
        int nw = (int)Math.Round(src.W * s), nh = (int)Math.Round(src.H * s);
        return new OcrImage(ImageResampler.ResizeBgra(src.Bgra, src.W, src.H, nw, nh), nw, nh);
    }

    /// <summary>
    /// Border rows with ink (like <see cref="Split"/>: brightness differs from edge median by > 48),
    /// with 1-2 px margin. Detector border after unclip is ~1.5× text height, and word highlighting of neighboring
    /// lines in text mode would overlap. No ink — border as is.
    /// </summary>
    public static (int Y, int Height) InkRows(OcrImage img, Int32Rect r)
    {
        if (r.Width <= 0 || r.Height <= 2) return (r.Y, r.Height);
        byte Lum(int x, int y) { int o = (y * img.W + x) * 4; return (byte)((img.Bgra[o] * 29 + img.Bgra[o + 1] * 150 + img.Bgra[o + 2] * 77) >> 8); }
        var border = new List<byte>(r.Width * 2);
        for (int x = r.X; x < r.X + r.Width; x++) { border.Add(Lum(x, r.Y)); border.Add(Lum(x, r.Y + r.Height - 1)); }
        border.Sort();
        int bg = border[border.Count / 2];
        var ink = new bool[r.Height];
        for (int y = 0; y < r.Height; y++)
            for (int x = r.X; x < r.X + r.Width && !ink[y]; x++) ink[y] = Math.Abs(Lum(x, r.Y + y) - bg) > 48;
        // Ink band around border middle: border edges may touch descenders of neighboring lines. Gaps up to eighth
        // of height — dots over “і”, “ё” etc. — don't break band.
        int center = r.Height / 2, seed = -1;
        for (int d = 0; d <= r.Height / 2 && seed < 0; d++)
        {
            if (center - d >= 0 && ink[center - d]) seed = center - d;
            else if (center + d < r.Height && ink[center + d]) seed = center + d;
        }
        if (seed < 0) return (r.Y, r.Height);
        int gap = Math.Max(1, r.Height / 8), top = seed, bottom = seed;
        for (int y = seed - 1, miss = 0; y >= 0 && miss <= gap; y--) { if (ink[y]) { top = y; miss = 0; } else miss++; }
        for (int y = seed + 1, miss = 0; y < r.Height && miss <= gap; y++) { if (ink[y]) { bottom = y; miss = 0; } else miss++; }
        top += r.Y; bottom += r.Y;
        int pad = Math.Max(1, (int)Math.Round((bottom - top + 1) * 0.1));
        int y0 = Math.Max(r.Y, top - pad), y1 = Math.Min(r.Y + r.Height, bottom + 1 + pad);
        return (y0, y1 - y0);
    }

    /// <summary>Line fragments → Screeny layout: fragments of same height glued into line, text split into words
    /// by spaces, word border by CTC steps of its first/last letter. <paramref name="k"/> — coordinate translation
    /// from detector image to original pixels (after ×2 upscale — 0.5). <paramref name="wordRows"/> — vertical bounds
    /// of word borders (<see cref="InkRows"/>); lines still grouped by detector borders.</summary>
    public static OcrLayout ToLayout(IEnumerable<(Int32Rect Box, RecResult R)> items, double k, Func<Int32Rect, (int Y, int Height)>? wordRows = null)
    {
        var rows = new List<(double Cy, List<(Int32Rect Box, RecResult R)> Items)>();
        foreach (var it in items.Where(i => i.R.Text.Trim().Length > 0).OrderBy(i => i.Box.Y + i.Box.Height / 2.0))
        {
            double cy = it.Box.Y + it.Box.Height / 2.0;
            if (rows.Count > 0 && Math.Abs(rows[^1].Cy - cy) < it.Box.Height * 0.5) rows[^1].Items.Add(it);
            else rows.Add((cy, [it]));
        }
        var lines = new List<OcrLine>();
        foreach (var row in rows)
        {
            var words = new List<OcrWord>();
            foreach (var (box, r) in row.Items.OrderBy(i => i.Box.X))
            {
                var (wy, wh) = wordRows?.Invoke(box) ?? (box.Y, box.Height);
                int start = -1;
                for (int i = 0; i <= r.Text.Length; i++)
                {
                    bool end = i == r.Text.Length || r.Text[i] == ' ';
                    if (!end && start < 0) start = i;
                    if (end && start >= 0)
                    {
                        double x0 = box.X + r.CharLeft[start], x1 = box.X + r.CharRight[i - 1];
                        words.Add(new OcrWord(r.Text[start..i], new Rect(x0 * k, wy * k, Math.Max(1, x1 - x0) * k, wh * k)));
                        start = -1;
                    }
                }
            }
            lines.Add(new OcrLine(words, string.Join(" ", words.Select(w => w.Text))));
        }
        return new OcrLayout(lines);
    }
}
