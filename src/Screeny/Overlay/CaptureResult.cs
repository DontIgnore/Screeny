using System.Windows.Media.Imaging;

namespace Screeny.Overlay;

/// <summary>
/// Copy — Enter: copies only if CopyOnCapture is enabled.
/// CopyExplicit — Ctrl+C or the Copy button: always copies.
/// Save — the path has already been selected in the overlay dialog (<see cref="CaptureResult.SavePath"/>).
/// CopyText — text was selected in text mode (Ctrl+T); see <see cref="CaptureResult.Text"/>.
/// </summary>
public enum CaptureAction { Copy, CopyExplicit, Save, Pin, CopyText }

/// <param name="Region">Selection in screenshot pixels.</param>
/// <param name="ScreenRegion">
/// The same selection in screen pixels, accounting for the virtual screen origin. When styled, it is expanded
/// by the padding so the pinned image surrounds the original position. Its size matches <paramref name="Image"/>.
/// </param>
/// <param name="SavePath">For <see cref="CaptureAction.Save"/>, the file selected by the user.</param>
/// <param name="Text">For <see cref="CaptureAction.CopyText"/>, the recognized and selected text.</param>
/// <param name="AnnotationsJson">Annotation document in region coordinates (<see cref="Annotations.AnnotationSerializer"/>), stored as a history sidecar.</param>
/// <param name="Original">The region without annotations or styling, only when Keep Originals is enabled.</param>
public sealed record CaptureResult(BitmapSource Image, CaptureAction Action, PixelRect Region, PixelRect ScreenRegion, string? SavePath = null, string? Text = null,
    string? AnnotationsJson = null, BitmapSource? Original = null);
