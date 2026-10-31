using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Screeny.Annotations.Shapes;

/// <summary>
/// Effect over screenshot pixels in rectangle (pixelate, blur).
/// Result cached by (actual pixel rectangle, thickness, screenshot).
/// <para>
/// Live editing: while shape is dragged, tool calls <see cref="BeginLiveEdit"/>,
/// and screen shows cheap preview — area downscaled by <see cref="PreviewFactor"/>,
/// with same effect and stretched back. After <see cref="EndLiveEdit"/> full result is computed.
/// Export (<see cref="RenderContext.ForExport"/>) always gets full result even if
/// EndLiveEdit was forgotten. Without live API calls behavior is unchanged: always full computation.
/// </para>
/// </summary>
public abstract class EffectAnnotation(Rect rect, AnnotationStyle style) : BoxAnnotation(rect, style)
{
    /// <summary>Factor by which preview area is downscaled.</summary>
    public const int PreviewFactor = 4;

    private readonly record struct CacheKey(Int32Rect Area, double Thickness, BitmapSource Source);

    private BitmapSource? _full, _preview;
    private CacheKey _fullKey, _previewKey;

    /// <summary>Whether geometry is being changed now (mouse drag).</summary>
    public bool IsLive { get; private set; }

    /// <summary>Begin live editing: Render on screen draws cheap preview.</summary>
    public void BeginLiveEdit() => IsLive = true;

    /// <summary>End live editing: next Render computes effect fully and discards preview.</summary>
    public void EndLiveEdit() { IsLive = false; _preview = null; }

    /// <param name="scale">Buffer scale relative to screenshot (1 — full size, 1/PreviewFactor — preview):
    /// effect block size/radius is multiplied by it.</param>
    protected abstract void Apply(byte[] bgra, int width, int height, double scale);

    public override bool HitTest(Point p) => Bounds.InflateCopy(2).Contains(p);

    /// <summary>Pixels of screenshot rectangle with effect applied; null if rectangle empty or outside screenshot.</summary>
    public BitmapSource? Process(BitmapSource source)
    {
        var area = PixelArea(source);
        if (area.IsEmpty) return null;
        var key = new CacheKey(area, Style.Thickness, source);
        if (_full is not null && _fullKey == key) return _full;

        EnsureFormat(source);
        int stride = area.Width * 4;
        var px = new byte[stride * area.Height];
        source.CopyPixels(area, px, stride, 0);
        Apply(px, area.Width, area.Height, 1.0);

        (_full, _fullKey) = (Create(px, area.Width, area.Height), key);
        return _full;
    }

    /// <summary>
    /// Cheap preview: every PreviewFactor-th pixel of every PreviewFactor-th row → effect with reduced parameter.
    /// For small areas returns full result.
    /// </summary>
    public BitmapSource? ProcessPreview(BitmapSource source)
    {
        var area = PixelArea(source);
        if (area.IsEmpty) return null;
        if (area.Width < PreviewFactor * 2 || area.Height < PreviewFactor * 2) return Process(source);
        var key = new CacheKey(area, Style.Thickness, source);
        if (_preview is not null && _previewKey == key) return _preview;

        EnsureFormat(source);
        int pw = area.Width / PreviewFactor, ph = area.Height / PreviewFactor;
        var row = new byte[area.Width * 4];
        var px = new byte[pw * ph * 4];
        for (int y = 0; y < ph; y++)
        {
            source.CopyPixels(new Int32Rect(area.X, area.Y + y * PreviewFactor, area.Width, 1), row, row.Length, 0);
            int o = y * pw * 4;
            for (int x = 0; x < pw; x++) Buffer.BlockCopy(row, x * PreviewFactor * 4, px, o + x * 4, 4);
        }
        Apply(px, pw, ph, 1.0 / PreviewFactor);

        (_preview, _previewKey) = (Create(px, pw, ph), key);
        return _preview;
    }

    private static void EnsureFormat(BitmapSource source)
    {
        // Screenshot is always Bgra32 (ScreenCapture); effects operate directly on this buffer.
        if (source.Format != PixelFormats.Bgra32)
            throw new ArgumentException($"Expected a Bgra32 image, got {source.Format}.", nameof(source));
    }

    private static BitmapSource Create(byte[] px, int w, int h)
    {
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Rect ∩ screenshot bounds, rounded to integer pixels.</summary>
    private Int32Rect PixelArea(BitmapSource source)
    {
        if (Rect.IsEmpty) return Int32Rect.Empty;
        int x1 = Math.Max(0, (int)Math.Floor(Rect.Left)), y1 = Math.Max(0, (int)Math.Floor(Rect.Top));
        int x2 = Math.Min(source.PixelWidth, (int)Math.Ceiling(Rect.Right)), y2 = Math.Min(source.PixelHeight, (int)Math.Ceiling(Rect.Bottom));
        return x2 - x1 < 1 || y2 - y1 < 1 ? Int32Rect.Empty : new Int32Rect(x1, y1, x2 - x1, y2 - y1);
    }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var area = PixelArea(ctx.Source);
        if (area.IsEmpty) return;
        var image = IsLive && !ctx.ForExport ? ProcessPreview(ctx.Source) : Process(ctx.Source);
        if (image is null) return;
        dc.DrawImage(image, new Rect(area.X, area.Y, area.Width, area.Height));
    }
}

public sealed class PixelateAnnotation(Rect rect, AnnotationStyle style) : EffectAnnotation(rect, style)
{
    protected override void Apply(byte[] bgra, int width, int height, double scale)
        => PixelOps.Pixelate(bgra, width, height, blockSize: Math.Max(1, (int)Math.Round((int)Math.Max(6, Style.Thickness * 3) * scale)));
}

public sealed class BlurAnnotation(Rect rect, AnnotationStyle style) : EffectAnnotation(rect, style)
{
    protected override void Apply(byte[] bgra, int width, int height, double scale)
    {
        int radius = Math.Max(1, (int)Math.Round((int)Math.Max(3, Style.Thickness * 2) * scale));
        // Two box blur passes ≈ Gaussian blur: text under it is unreadable. Shared temporary buffer.
        var scratch = new byte[bgra.Length];
        PixelOps.BoxBlur(bgra, width, height, radius, scratch);
        PixelOps.BoxBlur(bgra, width, height, radius, scratch);
    }
}
