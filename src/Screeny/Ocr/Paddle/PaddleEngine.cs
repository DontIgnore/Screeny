using System.Windows;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Screeny.Ocr.Paddle;

/// <summary>
/// PaddleOCR PP-OCRv5 (mobile det + cyrillic rec) on ONNX Runtime, CPU only. Only app class that
/// touches ORT types: until precise recognition enabled, neither managed ORT assembly nor native library loads.
/// Pre/post-processing — <see cref="PaddlePipeline"/>.
/// </summary>
public sealed class PaddleEngine : IDisposable
{
    public const string DetFile = "det.onnx", RecFile = "rec.onnx";

    private readonly InferenceSession _det, _rec;
    private readonly string[] _dict;
    private readonly int _par;

    /// <summary>Time of last run by stages (for log).</summary>
    public string Stats { get; private set; } = "";

    private PaddleEngine(InferenceSession det, InferenceSession rec, string[] dict, int par)
    {
        _det = det; _rec = rec; _dict = dict; _par = par;
    }

    /// <summary>Sessions from module folder. <paramref name="par"/> lines recognized in parallel, each session has
    /// <paramref name="threads"/> ORT threads (<see cref="PaddlePipeline.ThreadSplit"/>).</summary>
    public static PaddleEngine Load(string folder, int par, int threads)
    {
        OrtNative.EnsureLoaded(folder);
        // Session parameters are native object: needed only when creating sessions.
        using var options = Options(threads);
        InferenceSession? det = null, rec = null;
        try
        {
            det = new InferenceSession(Path.Combine(folder, DetFile), options);
            rec = new InferenceSession(Path.Combine(folder, RecFile), options);
            return new PaddleEngine(det, rec, PaddlePipeline.ParseDict(rec.ModelMetadata.CustomMetadataMap["character"]), Math.Max(1, par));
        }
        catch { rec?.Dispose(); det?.Dispose(); throw; }
    }

    private static SessionOptions Options(int threads)
    {
        var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
        if (threads > 0) { so.IntraOpNumThreads = threads; so.InterOpNumThreads = 1; }
        return so;
    }

    public void Dispose() { _det.Dispose(); _rec.Dispose(); }

    /// <summary>
    /// Detection and recognition; word borders in pixels of <paramref name="src"/>. Cancel interrupts running ORT run
    /// (<see cref="RunOptions.Terminate"/>), not just discards result: otherwise canceled full-screen refinement
    /// would keep using CPU on weak PC for seconds.
    /// </summary>
    public OcrLayout Run(OcrImage src, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double scale = PaddlePipeline.DetScaleFor(src.W, src.H);
        var img = scale > 1.01 ? PaddlePipeline.Upscale(src, scale) : src;
        ct.ThrowIfCancellationRequested();
        var boxes = Detect(img, ct);
        long tDet = sw.ElapsedMilliseconds;
        var recs = Recognize(img, boxes, ct);
        var picked = new List<(Int32Rect Box, RecResult R)>();
        for (int i = 0; i < boxes.Count; i++)
        {
            var r = recs[i];
            if (!PaddlePipeline.Keep(r)) continue;
            picked.Add((boxes[i], r with { Text = Homoglyphs.Fix(r.Text) }));
        }
        Stats = $"boxes={boxes.Count} det={tDet}ms rec={sw.ElapsedMilliseconds - tDet}ms";
        return PaddlePipeline.ToLayout(picked, (double)src.W / img.W, box => PaddlePipeline.InkRows(img, box));
    }

    private List<Int32Rect> Detect(OcrImage img, CancellationToken ct)
    {
        var (rw, rh) = PaddlePipeline.DetInputSize(img.W, img.H);
        var buf = new float[3 * rh * rw];
        PaddlePipeline.ResizeInto(img, new Int32Rect(0, 0, img.W, img.H), rw, rh, buf, 0, rw, rh * rw, PaddlePipeline.DetScale, PaddlePipeline.DetBias);
        var prob = RunSession(_det, new DenseTensor<float>(buf, [1, 3, rh, rw]), ct, out _);
        return PaddlePipeline.Boxes(prob, rw, rh, img.W, img.H);
    }

    /// <summary>Lines one by one (batch 1: different-width lines lose on padding), <see cref="_par"/> at once;
    /// <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/> thread-safe.</summary>
    private RecResult[] Recognize(OcrImage img, List<Int32Rect> crops, CancellationToken ct)
    {
        var chunks = new List<(int Parent, Int32Rect Box, bool SpaceBefore)>();
        for (int i = 0; i < crops.Count; i++)
            foreach (var (box, space) in PaddlePipeline.Split(img, crops[i], PaddlePipeline.MaxRecWidth)) chunks.Add((i, box, space));
        var parts = new RecResult[chunks.Count];
        var order = Enumerable.Range(0, chunks.Count).OrderBy(i => (double)chunks[i].Box.Width / Math.Max(1, chunks[i].Box.Height)).ToList();
        try { RecognizeChunks(img, chunks, order, parts, ct); }
        // Cancel mid-run: interrupted lines throw OCE inside loop bodies, Parallel wraps into AggregateException.
        catch (AggregateException ex) when (ct.IsCancellationRequested && ex.Flatten().InnerExceptions.All(e => e is OperationCanceledException))
        { throw new OperationCanceledException(ct); }
        var results = new RecResult[crops.Count];
        for (int i = 0; i < crops.Count; i++)
            results[i] = PaddlePipeline.Join(crops[i], Enumerable.Range(0, chunks.Count).Where(c => chunks[c].Parent == i)
                .Select(c => (chunks[c].Box, chunks[c].SpaceBefore, parts[c])).ToList());
        return results;
    }

    private void RecognizeChunks(OcrImage img, List<(int Parent, Int32Rect Box, bool SpaceBefore)> chunks, List<int> order, RecResult[] parts, CancellationToken ct)
    {
        Parallel.ForEach(order, new ParallelOptions { MaxDegreeOfParallelism = _par, CancellationToken = ct }, i =>
        {
            var crop = chunks[i].Box;
            int rw = Math.Min(3200, PaddlePipeline.RecWidth(crop));
            int W = Math.Max(320, rw); // right zeros = gray after normalization, like padding in PaddleX
            var buf = new float[3 * PaddlePipeline.RecHeight * W];
            PaddlePipeline.ResizeInto(img, crop, rw, PaddlePipeline.RecHeight, buf, 0, W, PaddlePipeline.RecHeight * W, PaddlePipeline.RecScale, PaddlePipeline.RecBias);
            var data = RunSession(_rec, new DenseTensor<float>(buf, [1, 3, PaddlePipeline.RecHeight, W]), ct, out var dims);
            int T = dims[1], C = dims[2];
            // CTC step width in crop pixels: input step W, in crop — accounting for resize to height 48.
            parts[i] = PaddlePipeline.Decode(data, T, C, _dict, (double)W / T * crop.Width / rw);
        });
    }

    private static float[] RunSession(InferenceSession session, DenseTensor<float> input, CancellationToken ct, out int[] dims)
    {
        using var options = new RunOptions();
        using var stop = ct.Register(() => options.Terminate = true);
        try
        {
            using var outs = session.Run([NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), input)], session.OutputMetadata.Keys.Take(1).ToList(), options);
            var t = outs.First().AsTensor<float>();
            dims = t.Dimensions.ToArray();
            return t is DenseTensor<float> dt ? dt.Buffer.ToArray() : t.ToArray();
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }
}
