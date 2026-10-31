using System.Windows;
using Screeny.Export;

namespace Screeny.Overlay;

// Decoration (beautify): ✨ button and B key enable, flyout at ✨ changes background, margin, rounding, shadow.
// Nothing drawn around selection (panel and size label don't jump): enabled decoration visible by
// highlighted ✨ button and ✨ icon next to size label. Copy/Save/Pin result and history go through
// BeautifyComposer. Text mode (OCR) works on original screenshot without decoration.
public partial class OverlayWindow
{
    /// <summary>
    /// Data for re-editing from history: annotation document in region coordinates (shapes entirely
    /// outside region don't get in — absent in image too) and, if setting enabled, region without marks and decoration.
    /// Failure here must not block copy — only log. Without History feature nothing collected.
    /// </summary>
    private (string? Json, System.Windows.Media.Imaging.BitmapSource? Original) HistorySidecarFor(PixelRect region)
    {
        if (!_settings.IsFeatureOn(Screeny.Features.FeatureCatalog.History)) return (null, null);
        string? json = null;
        System.Windows.Media.Imaging.BitmapSource? original = null;
        try
        {
            var area = region.ToRect();
            var inside = Document.Items.Where(a => a.Bounds.IntersectsWith(area));
            json = Screeny.Annotations.AnnotationSerializer.Serialize(inside, area.TopLeft, region.Width, region.Height, _editor.BeautifyOn ? _editor.Beautify : null);
        }
        catch (Exception ex) { Screeny.Logging.Log.Error("Annotations serialize failed", ex); }
        try { original = OriginalForHistory(_settings.KeepOriginals, _shot.Bitmap, region); }
        catch (Exception ex) { Screeny.Logging.Log.Error("Original region copy failed", ex); }
        return (json, original);
    }

    /// <summary>Region without marks and decoration for <c>.orig.png</c>; when setting disabled — null (file not written).</summary>
    public static System.Windows.Media.Imaging.BitmapSource? OriginalForHistory(bool keepOriginals, System.Windows.Media.Imaging.BitmapSource shot, PixelRect region)
        => keepOriginals ? ImageComposer.Compose(shot, region, null) : null;

    /// <summary>Final image: screenshot with annotations and, if enabled, decoration.</summary>
    private (System.Windows.Media.Imaging.BitmapSource Image, PixelRect ScreenRegion) ComposeResult(PixelRect region)
    {
        var image = ImageComposer.Compose(_shot.Bitmap, region, Document);
        var screen = region.Offset(_shot.VirtualRect.X, _shot.VirtualRect.Y);
        if (!_editor.BeautifyOn) return (image, screen);
        // Pinned screenshot placed so screenshot itself stays in place, decoration around.
        int p = _editor.Beautify.PaddingPx;
        return (BeautifyComposer.Compose(image, _editor.Beautify), new PixelRect(screen.X - p, screen.Y - p, screen.Width + 2 * p, screen.Height + 2 * p));
    }
}
