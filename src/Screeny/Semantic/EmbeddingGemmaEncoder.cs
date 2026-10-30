using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Screeny.Modules;

namespace Screeny.Semantic;

/// <summary>Картинка в RGB24 (как <c>PIL.Image.convert("RGB")</c> для непрозрачного PNG).</summary>
public sealed record RgbImage(byte[] Pixels, int Width, int Height)
{
    public static RgbImage Load(string path)
    {
        BitmapSource frame;
        using (var ms = new MemoryStream(File.ReadAllBytes(path), writable: false))
        {
            var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            frame = dec.Frames[0];
        }
        var f = new FormatConvertedBitmap(frame, PixelFormats.Rgb24, null, 0);
        int w = f.PixelWidth, h = f.PixelHeight;
        var px = new byte[w * h * 3];
        f.CopyPixels(px, w * 3, 0);
        return new RgbImage(px, w, h);
    }
}

/// <summary>
/// EmbeddingGemma 2 из модуля (<see cref="SemanticModule"/>, выгрузка onnx-community, q4) на ONNX Runtime, только CPU.
/// Картинка: бикубическое уменьшение Pillow с сохранением пропорций под бюджет <see cref="SoftTokens"/>, патчи 16×16,
/// визуальный кодировщик → мягкие токены (один на 3×3 патча), затем текстовая модель по
/// [BOS, &lt;|image&gt;, &lt;|image|&gt;×n, &lt;image|&gt;, EOS]. Текст: та же текстовая модель; усреднение и L2-нормировка —
/// внутри графа. Запрос — без префикса, распознанный текст снимка — с префиксом документа (так лучше на наборе спайка).
/// Визуальная сессия создаётся при первой картинке: для запросов хватает текстовой.
/// </summary>
public sealed class EmbeddingGemmaEncoder : IDisposable
{
    public const int Patch = 16, Pool = 3, Boi = 255999, ImageToken = 258880, Eoi = 258882, Dimensions = 768;
    /// <summary>Мягких токенов на картинку: 140 — вдвое дешевле 280, а мелкий текст покрывает вектор распознанного текста.</summary>
    public const int SoftTokens = 140;
    /// <summary>Распознанный текст длиннее — обрезается (стоимость текстовой модели растёт с длиной).</summary>
    public const int MaxTextTokens = 512;
    public const string DocumentPrefix = "title: none | text: ";

    private static readonly DenseTensor<float> Empty = new(Array.Empty<float>(), [0, 512]);
    private readonly string _folder;
    private readonly int _threads;
    private readonly InferenceSession _model;
    private readonly GemmaTokenizer _tokenizer;
    private readonly object _visionGate = new();
    private InferenceSession? _vision;

    private EmbeddingGemmaEncoder(string folder, int threads, InferenceSession model, GemmaTokenizer tokenizer)
    {
        _folder = folder; _threads = threads; _model = model; _tokenizer = tokenizer;
    }

    /// <summary>Текстовая сессия и токенизатор из папки модуля (ORT грузится из общего компонента рядом).</summary>
    public static EmbeddingGemmaEncoder Load(string folder, int threads)
    {
        OrtRuntimeModule.EnsureLoaded(folder);
        var tokenizer = new GemmaTokenizer(Path.Combine(folder, SemanticModule.TokenizerFile));
        var model = Create(Path.Combine(folder, SemanticModule.TextModelFile), threads);
        return new EmbeddingGemmaEncoder(folder, threads, model, tokenizer);
    }

    /// <summary>Визуальная сессия создана (для журнала и освобождения памяти).</summary>
    public bool VisionLoaded { get { lock (_visionGate) return _vision is not null; } }

    private static InferenceSession Create(string path, int threads)
    {
        // Параметры сессии — нативный объект: нужен только при создании.
        using var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
        o.IntraOpNumThreads = threads;
        o.InterOpNumThreads = 1;
        // Без арены: длина входа у каждого снимка своя, и арена держала бы память самого длинного прогона до освобождения сессий.
        o.EnableCpuMemArena = false;
        // Без активного ожидания между прогонами: фоновое приложение в трее не должно крутить ядра вхолостую.
        o.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        return new InferenceSession(path, o);
    }

    /// <summary>Размер под бюджет патчей с сохранением пропорций (transformers get_aspect_ratio_preserving_size).</summary>
    public static (int H, int W) TargetSize(int height, int width, int maxPatches)
    {
        double factor = Math.Sqrt((double)maxPatches * Patch * Patch / ((double)height * width));
        int side = Pool * Patch;
        int th = (int)Math.Floor(factor * height / side) * side, tw = (int)Math.Floor(factor * width / side) * side;
        int maxSide = maxPatches / (Pool * Pool) * side;
        if (th == 0 && tw == 0) throw new ArgumentException("image too small");
        if (th == 0) { th = side; tw = Math.Min((int)Math.Floor((double)width / height) * side, maxSide); }
        else if (tw == 0) { tw = side; th = Math.Min((int)Math.Floor((double)height / width) * side, maxSide); }
        return (th, tw);
    }

    /// <summary>Патчи [maxPatches, 768] (строка, столбец, канал внутри патча), позиции [maxPatches, 2] = (столбец, строка), дополнены 0 / −1.</summary>
    public static (float[] Patches, long[] Positions, int Valid) Preprocess(RgbImage img, int softTokens = SoftTokens)
    {
        int maxPatches = softTokens * Pool * Pool;
        var (th, tw) = TargetSize(img.Height, img.Width, maxPatches);
        var r = PilResize.Resize(img.Pixels, img.Width, img.Height, tw, th);
        int ph = th / Patch, pw = tw / Patch, dim = Patch * Patch * 3;
        var patches = new float[maxPatches * dim];
        var pos = new long[maxPatches * 2];
        Array.Fill(pos, -1L);
        for (int py = 0; py < ph; py++)
            for (int px = 0; px < pw; px++)
            {
                int n = py * pw + px, o = n * dim;
                for (int y = 0; y < Patch; y++)
                    for (int x = 0; x < Patch; x++)
                    {
                        int s = ((py * Patch + y) * tw + px * Patch + x) * 3;
                        for (int c = 0; c < 3; c++) patches[o++] = (float)(r[s + c] * 0.00392156862745098);
                    }
                pos[n * 2] = px;
                pos[n * 2 + 1] = py;
            }
        return (patches, pos, ph * pw);
    }

    public float[] EmbedImage(RgbImage image)
    {
        var (patches, pos, valid) = Preprocess(image);
        InferenceSession vision;
        lock (_visionGate) vision = _vision ??= Create(Path.Combine(_folder, SemanticModule.VisionFile), _threads);
        int maxPatches = pos.Length / 2;
        using var v = vision.Run([
            NamedOnnxValue.CreateFromTensor("pixel_values", new DenseTensor<float>(patches, [1, maxPatches, Patch * Patch * 3])),
            NamedOnnxValue.CreateFromTensor("pixel_position_ids", new DenseTensor<long>(pos, [1, maxPatches, 2]))]);
        var feats = v.First().AsTensor<float>();
        int n = feats.Dimensions[0];
        if (n != valid / (Pool * Pool)) throw new InvalidOperationException($"soft tokens {n}, expected {valid / (Pool * Pool)}");
        var ids = new long[n + 4];
        ids[0] = GemmaTokenizer.Bos; ids[1] = Boi; ids[^2] = Eoi; ids[^1] = GemmaTokenizer.Eos;
        for (int i = 0; i < n; i++) ids[i + 2] = ImageToken;
        return RunModel(ids, (DenseTensor<float>)feats);
    }

    /// <summary>Распознанный текст снимка (с префиксом документа, не длиннее <see cref="MaxTextTokens"/>).</summary>
    public float[] EmbedDocument(string text) => RunModel(_tokenizer.Ids(DocumentPrefix + text, MaxTextTokens), Empty);

    /// <summary>Запрос поиска (без префикса).</summary>
    public float[] EmbedQuery(string query) => RunModel(_tokenizer.Ids(query, MaxTextTokens), Empty);

    private float[] RunModel(long[] ids, DenseTensor<float> imageFeatures)
    {
        var mask = new long[ids.Length];
        Array.Fill(mask, 1L);
        using var o = _model.Run([
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, ids.Length])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, [1, ids.Length])),
            NamedOnnxValue.CreateFromTensor("image_features", imageFeatures),
            NamedOnnxValue.CreateFromTensor("video_features", Empty),
            NamedOnnxValue.CreateFromTensor("audio_features", Empty)], ["sentence_embedding"]);
        return SemanticVectors.Normalize(o.First().AsTensor<float>().ToArray());
    }

    public void Dispose()
    {
        lock (_visionGate) { _vision?.Dispose(); _vision = null; }
        _model.Dispose();
    }
}
