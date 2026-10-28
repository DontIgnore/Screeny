using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Screeny.Ocr.Paddle;

/// <summary>Картинка BGRA (как отдаёт WPF CopyPixels при Bgra32).</summary>
public sealed record OcrImage(byte[] Bgra, int W, int H)
{
    /// <summary>Пиксели замороженной картинки; вызывать можно с любого потока.</summary>
    public static OcrImage FromBitmap(BitmapSource bmp)
    {
        BitmapSource src = bmp.Format == PixelFormats.Bgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        var px = new byte[src.PixelWidth * 4 * src.PixelHeight];
        src.CopyPixels(px, src.PixelWidth * 4, 0);
        return new OcrImage(px, src.PixelWidth, src.PixelHeight);
    }
}

/// <summary>Распознанный фрагмент строки: текст, уверенность CTC и горизонтальные границы каждого символа
/// в пикселях от левого края выреза (по шагам CTC).</summary>
public sealed record RecResult(string Text, float Confidence, double[] CharLeft, double[] CharRight);

/// <summary>
/// Пре- и постобработка PP-OCR (DB-детектор + SVTR-распознаватель с CTC) без OpenCV и без ONNX Runtime: всё, что можно
/// проверить тестами без моделей. Параметры ниже — лучшая конфигурация из замеров качества и скорости на наборе мелкого
/// экранного текста (кириллица и латиница). Модуль с моделями содержит только данные и нативный
/// рантайм, поэтому правки здесь не требуют новой версии модуля.
/// </summary>
public static class PaddlePipeline
{
    // DB-постобработка как в PaddleX 3.x (OCR.yaml).
    public const double Thresh = 0.3, BoxThresh = 0.6, Unclip = 1.5;
    /// <summary>Строка шире этого (после приведения к высоте 48) режется на куски по промежуткам между словами.</summary>
    public const int MaxRecWidth = 480;
    /// <summary>Области до 0,25 Мп увеличиваются ×2 перед детекцией: иначе детектор теряет начало мелкой строки.</summary>
    public const long UpscaleMaxPixels = 250_000;
    /// <summary>
    /// Фрагмент до <see cref="ShortFragmentLength"/> символов с уверенностью CTC ниже порога отбрасывается: обычно это иконка
    /// или рамка, принятая детектором за текст. Порог подобран на одном реальном снимке со списком ников (от 0,6 до 0,9
    /// результат не менялся) — это риск подгонки; на новых примерах его стоит перепроверить.
    /// </summary>
    public const double MinShortConfidence = 0.6;
    public const int ShortFragmentLength = 4;
    public const int RecHeight = 48;

    /// <summary>
    /// Схема потоков: <c>par</c> строк распознаются параллельно, у каждой сессии <c>threads</c> потоков ORT. По замерам
    /// (13700K, 24 логических ядра) лучшая — 4×4: потоков всего примерно две трети логических ядер, поровну между
    /// параллельными строками, не больше 4×4 (дальше выигрыша нет). На 8 логических — 2×2, на 4 — 1×2, на 2 — 1×1.
    /// </summary>
    public static (int Par, int Threads) ThreadSplit(int processorCount)
    {
        int n = Math.Max(1, processorCount * 2 / 3);
        int par = Math.Clamp((int)Math.Floor(Math.Sqrt(n)), 1, 4);
        return (par, Math.Clamp(n / par, 1, 4));
    }

    /// <summary>Множитель перед детекцией: ×2 для небольших областей.</summary>
    public static double DetScaleFor(int w, int h) => (long)w * h <= UpscaleMaxPixels ? 2 : 1;

    /// <summary>Размер входа детектора: родное разрешение (короткая сторона не меньше 64, длинная не больше 4000), стороны кратны 32.</summary>
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

    /// <summary>DB-постобработка: бинаризация, связные компоненты (8-связность), оценка, unclip, перевод в координаты картинки.
    /// Экранный текст горизонтальный, поэтому вместо minAreaRect и контуров — прямоугольники компонент.</summary>
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
    /// Режет вырез на куски не шире <paramref name="maxRecWidth"/> (в масштабе высоты 48) по столбцам без «чернил»:
    /// фон — медиана яркости по краю выреза, промежуток — не меньше четверти высоты строки. Если промежутка нет,
    /// режет где придётся (без пробела). В rec-модели PP-OCRv5 есть глобальное внимание вдоль строки: время и память
    /// растут быстрее ширины, а вход шире 3200 ещё и сжимается.
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
        // Середины промежутков между словами.
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

    /// <summary>Ширина входа rec для выреза: пропорционально высоте 48.</summary>
    public static int RecWidth(Int32Rect crop) => Math.Max(1, (int)Math.Ceiling((double)RecHeight * crop.Width / Math.Max(1, crop.Height)));

    /// <summary>CTC greedy: 0 — blank, k — dict[k−1], класс после словаря — пробел.</summary>
    /// <param name="stepPx">Ширина шага CTC в пикселях выреза.</param>
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

    /// <summary>Словарь rec-модели из метаданных ONNX (ключ <c>character</c>): кончается переводом строки, последний пустой
    /// элемент — не символ.</summary>
    public static string[] ParseDict(string character) => character.TrimEnd('\n').Split('\n');

    /// <summary>Куски одной строки (<see cref="Split"/>) обратно в строку: пробел на месте реза по промежутку, рамки символов
    /// — в пикселях от левого края исходного выреза, уверенность — средняя по символам.</summary>
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

    /// <summary>Фрагмент оставляется: не короткий или уверенный (см. <see cref="MinShortConfidence"/>). Порог — на фрагмент
    /// целиком, а не на отдельные символы.</summary>
    public static bool Keep(RecResult r) => r.Text.Trim().Length > ShortFragmentLength || r.Confidence >= MinShortConfidence;

    /// <summary>Билинейный ресайз области в стиле cv2.INTER_LINEAR (центры пикселей) прямо в тензор CHW:
    /// dst[off + c·plane + y·stride + x] = v·scale[c] + bias[c], каналы B,G,R.</summary>
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
    /// Строки рамки, где есть «чернила» (как в <see cref="Split"/>: яркость отличается от медианы края больше чем на 48),
    /// с запасом в пиксель-два. Рамка детектора после unclip примерно в полтора раза выше текста, и подсветка слов
    /// соседних строк в режиме текста наезжала бы друг на друга. Чернил нет — рамка как есть.
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
        // Полоса чернил вокруг середины рамки: края рамки могут задевать хвосты букв соседних строк. Разрывы до восьмой
        // части высоты — точки над «і», «ё» и т. п. — полосу не прерывают.
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

    /// <summary>Фрагменты строк → раскладка Screeny: фрагменты одной высоты склеиваются в строку, текст режется на слова
    /// по пробелам, рамка слова — по шагам CTC его первой и последней буквы. <paramref name="k"/> — перевод координат
    /// картинки детектора в пиксели исходной (после увеличения ×2 — 0,5). <paramref name="wordRows"/> — вертикальные границы
    /// рамок слов (<see cref="InkRows"/>); строки при этом всё равно группируются по рамкам детектора.</summary>
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
