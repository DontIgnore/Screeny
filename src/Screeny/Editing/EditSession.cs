using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Annotations;
using Screeny.Export;
using Screeny.History;
using Screeny.Logging;
using Screeny.Overlay;

namespace Screeny.Editing;

/// <summary>
/// History entry opened for re-editing.
/// <list type="bullet">
/// <item>Has <c>.orig.png</c> and readable <c>.screeny.json</c> — base <see cref="Base"/> = original, marks and decoration from JSON,
/// “Original / With marks” toggle available.</item>
/// <item>No original — base = ready PNG (old marks and decoration already in image), empty document.</item>
/// <item>Original exists but JSON missing, malformed, has unknown marks, newer than supported or different size — also ready PNG and
/// <see cref="Message"/>: on original without marks what was hidden by blur would be lost.</item>
/// </list>
/// </summary>
/// <param name="Path">History screenshot that is opened.</param>
/// <param name="Base">Editing base (Bgra32, 96 DPI, frozen): pixel source for effects and eyedropper.</param>
/// <param name="HasOriginal">Base is original without marks (has toggle).</param>
/// <param name="OriginalPng">Original bytes: read at open, because history rotation after save
/// may delete source entry before original is copied to new one.</param>
/// <param name="Message">What went wrong reading marks; null — everything ok.</param>
public sealed record EditSession(string Path, BitmapSource Base, bool HasOriginal, byte[]? OriginalPng,
    IReadOnlyList<Annotation> Annotations, BeautifyOptions? Beautify, string? Message)
{
    /// <exception cref="IOException">Screenshot itself unreadable.</exception>
    /// <exception cref="NotSupportedException">File is not an image.</exception>
    public static EditSession Load(string pngPath)
    {
        var finished = Decode(File.ReadAllBytes(pngPath));
        var (jsonPath, originalPath) = HistoryStore.SidecarPaths(pngPath);
        if (!File.Exists(originalPath)) return Flat(pngPath, finished, null);

        string? problem;
        try
        {
            var originalPng = File.ReadAllBytes(originalPath);
            var original = Decode(originalPng);
            if (!File.Exists(jsonPath)) problem = Localization.Loc.T("Edit.NoDocument");
            else
            {
                var file = AnnotationSerializer.Deserialize(File.ReadAllText(jsonPath));
                if (file.Width != original.PixelWidth || file.Height != original.PixelHeight)
                    problem = Localization.Loc.F("Edit.SizeMismatch", file.Width, file.Height, original.PixelWidth, original.PixelHeight);
                else if (file.Skipped > 0)
                    problem = Localization.Loc.F("Edit.UnknownMarks", file.Skipped);
                else
                    return new EditSession(pngPath, original, true, originalPng, file.Annotations, file.Beautify, null);
            }
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or NotSupportedException
                                       or FileFormatException or ArgumentException or InvalidOperationException)
        {
            Log.Error($"Re-edit sidecar read failed: {pngPath}", ex);
            // Details (exception text) in log; user gets clear reason in his language.
            problem = ex switch
            {
                UnsupportedDocumentVersionException newer => Localization.Loc.F("Edit.NewerDocument", newer.Version),
                FormatException => Localization.Loc.T("Edit.DocumentDamaged"),
                _ => Localization.Loc.T("Edit.ReadOriginalFailed"),
            };
        }
        return Flat(pngPath, finished, Localization.Loc.F("Edit.OpenedFlat", problem));
    }

    private static EditSession Flat(string path, BitmapSource finished, string? message)
        => new(path, finished, false, null, [], null, message);

    /// <summary>PNG → independent Bgra32 96 DPI: effects work only with Bgra32, and file DPI must not change on-screen size.</summary>
    public static BitmapSource Decode(byte[] png)
    {
        using var ms = new MemoryStream(png, writable: false);
        var frame = BitmapFrame.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = source.PixelWidth, h = source.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        source.CopyPixels(px, stride, 0);
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        result.Freeze();
        return result;
    }
}

/// <summary>Editor result for action (copy, save, pin): image and sidecar files of new history entry.</summary>
/// <param name="Image">Base with marks and, if enabled, decoration.</param>
/// <param name="AnnotationsJson">Exactly what is drawn on <paramref name="Image"/> (in “Original” mode — empty).</param>
/// <param name="OriginalPng">Original for new entry: only if source had it and storage setting enabled.</param>
public sealed record EditResult(BitmapSource Image, string AnnotationsJson, byte[]? OriginalPng)
{
    /// <param name="annotations">Visible marks; in “Original” mode — empty list.</param>
    public static EditResult Build(EditSession session, IReadOnlyList<Annotation> annotations, BeautifyOptions? beautify, bool keepOriginals)
    {
        var b = session.Base;
        var region = new PixelRect(0, 0, b.PixelWidth, b.PixelHeight);
        var area = region.ToRect();
        var visible = annotations.Where(a => a.Bounds.IntersectsWith(area)).ToList();
        var doc = new AnnotationDocument();
        doc.Load(visible);
        var image = ImageComposer.Compose(b, region, doc);
        if (beautify is not null) image = BeautifyComposer.Compose(image, beautify);
        var json = AnnotationSerializer.Serialize(visible, new Point(0, 0), region.Width, region.Height, beautify);
        return new EditResult(image, json, keepOriginals && session.HasOriginal ? session.OriginalPng : null);
    }
}
