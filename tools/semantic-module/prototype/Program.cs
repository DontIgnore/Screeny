using System.Diagnostics;
using System.Text.Json;
using SemanticProto;

// Spike prototype. See ../README.md for the commands and what each one proves.
//   SemanticProto tokens  <siglip2|eg2> [--work DIR]
//   SemanticProto parity  <siglip2|eg2> [--variant fp32] [--work DIR] [--ort DIR]
//   SemanticProto bench   <siglip2|eg2> [--variant ...] [--threads 1,2,0] [--soft 280] [--work DIR] [--ort DIR]
//   SemanticProto search  <siglip2|eg2> [--variant ...] [--soft 280] [--prefix search|none]  (retrieval table only)
Console.OutputEncoding = System.Text.Encoding.UTF8;
var opt = new Dictionary<string, string>();
for (int i = 2; i + 1 < args.Length; i += 2) opt[args[i].TrimStart('-')] = args[i + 1];
string cmd = args.Length > 0 ? args[0] : "help", model = args.Length > 1 ? args[1] : "siglip2";
string work = Path.GetFullPath(opt.GetValueOrDefault("work", Path.Combine(AppContext.BaseDirectory, "../../../../work")));
string ortDir = opt.GetValueOrDefault("ort", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".nuget/packages/microsoft.ml.onnxruntime/1.31.0/runtimes/win-x64/native"));
string variant = opt.GetValueOrDefault("variant", "fp32");
int softTokens = int.Parse(opt.GetValueOrDefault("soft", "280"));
string prefix = opt.GetValueOrDefault("prefix", "search") == "none" ? "" : EmbeddingGemmaEmbedder.QueryPrefix;
string testset = Path.Combine(work, "testset");

bool eg2 = model == "eg2";
string refDir = Path.Combine(work, "ref", eg2 ? "embeddinggemma-2" + (softTokens == 280 ? "" : $"-s{softTokens}") : "siglip2-base-patch16-224");
string sigDir = Path.Combine(work, "onnx", "siglip2-base-patch16-224"), sigHf = Path.Combine(work, "hf", "siglip2-base-patch16-224");
string egOnnx = Path.Combine(work, "hf", "embeddinggemma-2-ONNX", "onnx"), egHf = Path.Combine(work, "hf", "embeddinggemma-2");
string tokPath = eg2 ? Path.Combine(egHf, "tokenizer.model") : Path.Combine(sigHf, "tokenizer.model");

switch (cmd)
{
    case "tokens": Tokens(); break;
    case "parity": Parity(); break;
    case "bench": Bench(); break;
    case "search": Search(); break;
    case "hybrid": Hybrid(); break;
    default: Console.WriteLine("commands: tokens | parity | bench | search  <siglip2|eg2>  (see Program.cs)"); break;
}

IEmbedder Create(int threads, string v)
{
    OrtRuntime.EnsureLoaded(ortDir);
    if (eg2)
    {
        string s = v switch { "fp32" => "", "q8" => "_quantized", "q4" => "_q4", _ => throw new ArgumentException(v) };
        return new EmbeddingGemmaEmbedder(Path.Combine(egOnnx, $"vision_encoder{s}.onnx"), Path.Combine(egOnnx, $"model{s}.onnx"), tokPath, softTokens, threads, prefix);
    }
    var (vf, tf) = v switch
    {
        "fp32" => ("vision.onnx", "text.onnx"), "w8" => ("vision_w8.onnx", "text_w8.onnx"), "w8g4" => ("vision_w8.onnx", "text_w8g4.onnx"),
        "q4" => ("vision_q4.onnx", "text_q4.onnx"), "int8" => ("vision_int8.onnx", "text_int8.onnx"), _ => throw new ArgumentException(v),
    };
    return new SiglipEmbedder(Path.Combine(sigDir, vf), Path.Combine(sigDir, tf), tokPath, 224, threads);
}

void Tokens()
{
    var tok = new GemmaTokenizer(tokPath);
    var refTokens = JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(Path.Combine(refDir, "tokens.json")))!;
    int ok = 0;
    foreach (var (s, expected) in refTokens)
    {
        long[] got = eg2 ? tok.GemmaIds(s) : tok.SiglipIds(s);
        if (got.SequenceEqual(expected.Select(x => (long)x))) { ok++; continue; }
        Console.WriteLine($"MISMATCH {JsonSerializer.Serialize(s)}\n  py {string.Join(' ', expected.Take(24))}\n  cs {string.Join(' ', got.Take(24))}");
    }
    Console.WriteLine($"{model}: token ids identical for {ok}/{refTokens.Count} strings" +
                      (eg2 ? "" : " (incl. padding to 64 with 0, EOS=1 placement, truncation)"));
}

Dictionary<string, Dictionary<string, float[]>> LoadEmb(string v) =>
    JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, float[]>>>(File.ReadAllText(Path.Combine(refDir, $"emb_{v}.json")))!;

TestSet LoadSet() => JsonSerializer.Deserialize<TestSet>(File.ReadAllText(Path.Combine(testset, "testset.json")))!;

void Parity()
{
    var set = LoadSet();
    var refV = LoadEmb(variant);
    var refTorch = LoadEmb("torch");
    using var e = Create(0, variant);
    Console.WriteLine($"== {model} variant {variant}, ORT from {OrtRuntime.LoadedFrom}");
    double minSame = 1, minE2E = 1, minE2ETorch = 1, minQ = 1;
    var img = new Dictionary<string, float[]>();
    foreach (var n in set.images)
    {
        var rgb = RgbImage.Load(Path.Combine(testset, n));
        float[] refPix = ReadF32(Path.Combine(refDir, "pixels", n + ".f32"));
        float[] csPix; float[] same, e2e;
        if (e is SiglipEmbedder s)
        {
            csPix = s.Preprocess(rgb);
            same = s.EmbedPixels(refPix);
            e2e = s.EmbedPixels(csPix);
        }
        else
        {
            var g = (EmbeddingGemmaEmbedder)e;
            var (patches, pos, valid) = g.Preprocess(rgb);
            csPix = patches;
            long[] refPos = ReadI64(Path.Combine(refDir, "pixels", n + ".pos.i64"));
            if (!refPos.SequenceEqual(pos)) Console.WriteLine($"  {n}: position ids differ");
            same = g.EmbedPatches(refPix, refPos, refPos.Count(p => p >= 0) / 2);
            e2e = g.EmbedPatches(patches, pos, valid);
        }
        int diff = 0; double maxd = 0;
        for (int i = 0; i < Math.Min(csPix.Length, refPix.Length); i++)
        {
            double d = Math.Abs(csPix[i] - refPix[i]);
            if (d > 1e-6) diff++;
            maxd = Math.Max(maxd, d);
        }
        double cSame = Vec.Dot(same, refV["images"][n]), cE2E = Vec.Dot(e2e, refV["images"][n]), cT = Vec.Dot(e2e, refTorch["images"][n]);
        minSame = Math.Min(minSame, cSame); minE2E = Math.Min(minE2E, cE2E); minE2ETorch = Math.Min(minE2ETorch, cT);
        Console.WriteLine($"  {n,-22} pixels: {diff,7} of {refPix.Length} differ (max {maxd:F4}{(csPix.Length != refPix.Length ? $", LENGTH {csPix.Length}≠{refPix.Length}" : "")})  " +
                          $"cos same-input {cSame:F6}  cos C#-pipeline {cE2E:F6}  vs torch {cT:F6}");
        img[n] = e2e;
    }
    var q = new Dictionary<string, float[]>();
    foreach (var query in set.queries.Keys)
    {
        q[query] = e.EmbedQuery(query);
        minQ = Math.Min(minQ, Vec.Dot(q[query], refV["queries"][query]));
    }
    Console.WriteLine($"MIN cos: images same-input {minSame:F6}, images C# pipeline {minE2E:F6} (vs torch fp32 {minE2ETorch:F6}), queries {minQ:F6}");
    Console.WriteLine(Retrieval.Report(set, img, q));
}

void Search()
{
    var set = LoadSet();
    using var e = Create(0, variant);
    var img = set.images.ToDictionary(n => n, n => e.EmbedImage(RgbImage.Load(Path.Combine(testset, n))));
    var q = set.queries.Keys.ToDictionary(x => x, x => e.EmbedQuery(x));
    Console.WriteLine($"== {model} {variant} soft={softTokens} prefix={(prefix.Length > 0 ? "search" : "none")}");
    Console.WriteLine(Retrieval.Report(set, img, q));
}

// Step 0 of the app integration: OCR-text embedding (Windows OCR, every profile language) + image embedding at a low
// budget, scored with one query embedding, vs image-only at 280. 2 threads, q4.
void Hybrid()
{
    var set = LoadSet();
    int threads = int.Parse(opt.GetValueOrDefault("threads", "2"));
    var ocr = set.images.ToDictionary(n => n, n => OcrText(Path.Combine(testset, n)));
    foreach (var n in set.images) Console.WriteLine($"  ocr {n,-24} {ocr[n].Length,5} chars");
    var budgets = new[] { 70, 140, 280 };
    var img = new Dictionary<int, Dictionary<string, float[]>>();
    var ms = new Dictionary<int, double>();
    Dictionary<string, float[]>? txt = null, txtRaw = null; double txtMs = 0;
    Dictionary<string, float[]> q = new();
    foreach (var b in budgets)
    {
        softTokens = b;
        using var e = (EmbeddingGemmaEmbedder)Create(threads, "q4");
        e.EmbedImage(RgbImage.Load(Path.Combine(testset, set.images[0]))); // warm-up
        var sw = Stopwatch.StartNew();
        img[b] = set.images.ToDictionary(n => n, n => e.EmbedImage(RgbImage.Load(Path.Combine(testset, n))));
        ms[b] = sw.Elapsed.TotalMilliseconds / set.images.Length;
        if (txt is null)
        {
            sw.Restart();
            txt = set.images.Where(n => ocr[n].Length > 0).ToDictionary(n => n, n => e.EmbedText("title: none | text: " + Cap(ocr[n])));
            txtMs = sw.Elapsed.TotalMilliseconds / Math.Max(1, txt.Count);
            txtRaw = set.images.Where(n => ocr[n].Length > 0).ToDictionary(n => n, n => e.EmbedText(Cap(ocr[n])));
            q = set.queries.Keys.ToDictionary(x => x, x => e.EmbedText(x));
        }
    }
    Console.WriteLine($"q4, {threads} threads: image ms 70={ms[70]:F0} 140={ms[140]:F0} 280={ms[280]:F0}; OCR-text embedding {txtMs:F0} ms");
    Report("image-only 280", n => img[280][n], null);
    Report("image-only 140", n => img[140][n], null);
    Report("image-only 70", n => img[70][n], null);
    Report("ocr-text only (title prompt)", null, txt);
    foreach (var b in new[] { 70, 140 })
    {
        Report($"hybrid max, text(title)+img{b}", n => img[b][n], txt);
        Report($"hybrid max, text(raw)+img{b}", n => img[b][n], txtRaw);
    }

    void Report(string name, Func<string, float[]>? image, Dictionary<string, float[]>? text)
    {
        var scored = new Dictionary<string, float[]>();
        int top1 = 0, hit3 = 0, dTop1 = 0, dHit3 = 0, dN = 0;
        var sb = new System.Text.StringBuilder();
        foreach (var (query, rel) in set.queries)
        {
            var ranked = set.images.Select(n =>
            {
                double s = double.MinValue;
                if (image is not null) s = Vec.Dot(image(n), q[query]);
                if (text is not null && text.TryGetValue(n, out var t)) s = Math.Max(s, Vec.Dot(t, q[query]));
                return (n, s);
            }).OrderByDescending(x => x.s).ToList();
            bool t1 = rel.Contains(ranked[0].n), h3 = ranked.Take(3).Any(x => rel.Contains(x.n));
            top1 += t1 ? 1 : 0; hit3 += h3 ? 1 : 0;
            if (rel.All(r => r.StartsWith("desk_"))) { dN++; dTop1 += t1 ? 1 : 0; dHit3 += h3 ? 1 : 0; }
            if (query is "котик" or "cat" or "график" or "счёт" or "error dialog")
                sb.AppendLine($"    {query,-14} " + string.Join(", ", ranked.Take(3).Select(x => $"{x.n}:{x.s:F3}")));
        }
        Console.WriteLine($"{name,-34} top1 {top1}/{set.queries.Count} hit@3 {hit3}/{set.queries.Count} desktop {dTop1}/{dN} {dHit3}/{dN}");
        Console.Write(sb);
    }
}

static string Cap(string s) => s.Length > 2000 ? s[..2000] : s;

static string OcrText(string path)
{
    var file = Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask().GetAwaiter().GetResult();
    using var stream = file.OpenReadAsync().AsTask().GetAwaiter().GetResult();
    var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
    using var bmp = decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
    var parts = new List<string>();
    foreach (var lang in Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(lang);
        if (engine is null) continue;
        var text = engine.RecognizeAsync(bmp).AsTask().GetAwaiter().GetResult().Text;
        if (!string.IsNullOrWhiteSpace(text) && !parts.Contains(text)) parts.Add(text);
    }
    return string.Join('\n', parts);
}

void Bench()
{
    var set = LoadSet();
    var images = set.images.Where(n => n.EndsWith(".png")).Select(n => RgbImage.Load(Path.Combine(testset, n))).ToList();
    OrtRuntime.EnsureLoaded(ortDir);
    foreach (var t in opt.GetValueOrDefault("threads", "1,2,0").Split(',').Select(int.Parse))
    {
        GC.Collect();
        var p0 = Process.GetCurrentProcess(); p0.Refresh();
        long ws0 = p0.WorkingSet64, priv0 = p0.PrivateMemorySize64;
        var sw = Stopwatch.StartNew();
        using var e = Create(t, variant);
        double load = sw.Elapsed.TotalMilliseconds;
        e.EmbedImage(images[0]); e.EmbedQuery("warm up");
        sw.Restart();
        foreach (var im in images) e.EmbedImage(im);
        double perImage = sw.Elapsed.TotalMilliseconds / images.Count;
        var qs = set.queries.Keys.ToList();
        sw.Restart();
        foreach (var q in qs) e.EmbedQuery(q);
        double perQuery = sw.Elapsed.TotalMilliseconds / qs.Count;
        var p = Process.GetCurrentProcess(); p.Refresh();
        Console.WriteLine($"{model} {variant}{(eg2 ? $" soft={softTokens}" : "")} threads={(t == 0 ? "default" : t.ToString())}: load {load:F0} ms, " +
                          $"image {perImage:F0} ms, query {perQuery:F1} ms, working set +{(p.WorkingSet64 - ws0) / 1048576} MB " +
                          $"(peak {p.PeakWorkingSet64 / 1048576} MB), private +{(p.PrivateMemorySize64 - priv0) / 1048576} MB");
    }
}

static float[] ReadF32(string path) { var b = File.ReadAllBytes(path); var f = new float[b.Length / 4]; Buffer.BlockCopy(b, 0, f, 0, b.Length); return f; }
static long[] ReadI64(string path) { var b = File.ReadAllBytes(path); var f = new long[b.Length / 8]; Buffer.BlockCopy(b, 0, f, 0, b.Length); return f; }

public sealed record TestSet(string[] images, Dictionary<string, string[]> queries);

public static class Retrieval
{
    public static string Report(TestSet set, Dictionary<string, float[]> img, Dictionary<string, float[]> q)
    {
        var sb = new System.Text.StringBuilder();
        int top1 = 0, hit3 = 0, dTop1 = 0, dHit3 = 0, dN = 0;
        foreach (var (query, rel) in set.queries)
        {
            var ranked = set.images.Select(n => (n, s: Vec.Dot(img[n], q[query]))).OrderByDescending(x => x.s).ToList();
            bool t1 = rel.Contains(ranked[0].n), h3 = ranked.Take(3).Any(x => rel.Contains(x.n));
            top1 += t1 ? 1 : 0; hit3 += h3 ? 1 : 0;
            if (rel.All(r => r.StartsWith("desk_"))) { dN++; dTop1 += t1 ? 1 : 0; dHit3 += h3 ? 1 : 0; }
            sb.AppendLine($"{query,-18} top1={(t1 ? "Y" : "n")} hit@3={(h3 ? "Y" : "n")}  " + string.Join(", ", ranked.Take(3).Select(x => $"{x.n}:{x.s:F3}")));
        }
        sb.Append($"TOTAL top1 {top1}/{set.queries.Count}  hit@3 {hit3}/{set.queries.Count}   (desktop-density subset: top1 {dTop1}/{dN}  hit@3 {dHit3}/{dN})");
        return sb.ToString();
    }
}
