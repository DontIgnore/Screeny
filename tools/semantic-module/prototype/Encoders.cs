using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SemanticProto;

/// <summary>Decoded image as RGB24 (what <c>PIL.Image.convert("RGB")</c> gives for an opaque PNG).</summary>
public sealed record RgbImage(byte[] Pixels, int Width, int Height)
{
    public static RgbImage Load(string path)
    {
        var dec = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        var f = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Rgb24, null, 0);
        int w = f.PixelWidth, h = f.PixelHeight;
        var px = new byte[w * h * 3];
        f.CopyPixels(px, w * 3, 0);
        return new RgbImage(px, w, h);
    }
}

public static class Vec
{
    public static float[] Normalize(ReadOnlySpan<float> v)
    {
        double s = 0;
        foreach (var x in v) s += (double)x * x;
        var r = new float[v.Length];
        float inv = (float)(1.0 / Math.Sqrt(s));
        for (int i = 0; i < v.Length; i++) r[i] = v[i] * inv;
        return r;
    }

    public static double Dot(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += (double)a[i] * b[i];
        return s;
    }
}

public interface IEmbedder : IDisposable
{
    float[] EmbedImage(RgbImage image);
    float[] EmbedQuery(string query);
}

public static class Sessions
{
    public static InferenceSession Create(string path, int threads)
    {
        var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (threads > 0) { o.IntraOpNumThreads = threads; o.InterOpNumThreads = 1; }
        return new InferenceSession(path, o);
    }
}

/// <summary>SigLIP 2 (fixed resolution): PIL bilinear resize to S×S, (x/255 − 0.5)/0.5, CHW; text = 64 ids.</summary>
public sealed class SiglipEmbedder(string visionPath, string textPath, string tokenizerPath, int imageSize, int threads) : IEmbedder
{
    private readonly InferenceSession _vision = Sessions.Create(visionPath, threads);
    private readonly InferenceSession _text = Sessions.Create(textPath, threads);
    public GemmaTokenizer Tokenizer { get; } = new(tokenizerPath);

    public float[] Preprocess(RgbImage img)
    {
        int s = imageSize;
        var r = PilResize.Resize(img.Pixels, img.Width, img.Height, s, s, PilResize.Filter.Bilinear);
        var chw = new float[3 * s * s];
        for (int i = 0; i < s * s; i++)
            for (int c = 0; c < 3; c++)
                chw[c * s * s + i] = (float)((r[i * 3 + c] * 0.00392156862745098 - 0.5) / 0.5);
        return chw;
    }

    public float[] EmbedImage(RgbImage image) => EmbedPixels(Preprocess(image));

    public float[] EmbedPixels(float[] chw)
    {
        var t = new DenseTensor<float>(chw, [1, 3, imageSize, imageSize]);
        using var o = _vision.Run([NamedOnnxValue.CreateFromTensor("pixel_values", t)]);
        return Vec.Normalize(o.First().AsTensor<float>().ToArray());
    }

    public float[] EmbedQuery(string query)
    {
        var t = new DenseTensor<long>(Tokenizer.SiglipIds(query), [1, 64]);
        using var o = _text.Run([NamedOnnxValue.CreateFromTensor("input_ids", t)]);
        return Vec.Normalize(o.First().AsTensor<float>().ToArray());
    }

    public void Dispose() { _vision.Dispose(); _text.Dispose(); }
}

/// <summary>
/// EmbeddingGemma 2 (onnx-community export): aspect-preserving PIL bicubic resize into a patch budget, 16×16 patches,
/// vision_encoder → soft tokens (one per 3×3 patches), then the text model over
/// [BOS, &lt;|image&gt;, &lt;|image|&gt;×n, &lt;image|&gt;, EOS] with the soft tokens; mean pooling and L2 norm are in the graph.
/// </summary>
public sealed class EmbeddingGemmaEmbedder : IEmbedder
{
    public const int Patch = 16, Pool = 3, Boi = 255999, ImageToken = 258880, Eoi = 258882;
    public const string QueryPrefix = "task: search result | query: ";
    private static readonly DenseTensor<float> Empty = new(new float[0], [0, 512]);
    private readonly InferenceSession? _vision;
    private readonly InferenceSession _model;
    public GemmaTokenizer Tokenizer { get; }
    public int SoftTokens { get; }
    public string Prefix { get; }

    public EmbeddingGemmaEmbedder(string visionPath, string modelPath, string tokenizerPath, int softTokens, int threads, string prefix = QueryPrefix)
    {
        _vision = visionPath.Length == 0 ? null : Sessions.Create(visionPath, threads);
        _model = Sessions.Create(modelPath, threads);
        Tokenizer = new GemmaTokenizer(tokenizerPath);
        SoftTokens = softTokens;
        Prefix = prefix;
    }

    /// <summary>transformers get_aspect_ratio_preserving_size.</summary>
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

    /// <summary>Patches [maxPatches, 768] (row, col, channel inside a patch), positions [maxPatches, 2] = (col, row), padded with 0 / −1.</summary>
    public (float[] Patches, long[] Positions, int Valid) Preprocess(RgbImage img)
    {
        int maxPatches = SoftTokens * Pool * Pool;
        var (th, tw) = TargetSize(img.Height, img.Width, maxPatches);
        var r = PilResize.Resize(img.Pixels, img.Width, img.Height, tw, th, PilResize.Filter.Bicubic);
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
        return EmbedPatches(patches, pos, valid);
    }

    public float[] EmbedPatches(float[] patches, long[] pos, int valid)
    {
        if (_vision is null) throw new InvalidOperationException("text-only embedder");
        int maxPatches = pos.Length / 2;
        using var v = _vision.Run([
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

    public float[] EmbedQuery(string query) => RunModel(Tokenizer.GemmaIds(Prefix + query), Empty);

    /// <summary>Text as is (no prefix), at most 512 tokens.</summary>
    public float[] EmbedText(string text)
    {
        var ids = Tokenizer.GemmaIds(text);
        if (ids.Length > 512) { ids = ids[..512]; ids[^1] = GemmaTokenizer.Eos; }
        return RunModel(ids, Empty);
    }

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
        return Vec.Normalize(o.First().AsTensor<float>().ToArray());
    }

    public void Dispose() { _vision?.Dispose(); _model.Dispose(); }
}
