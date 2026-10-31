using System.Windows;
using System.Windows.Media;
using Screeny.Editing;

namespace Screeny.Overlay;

/// <summary>
/// Snap guides over screenshot: accent 1 device-pixel line with light semi-transparent
/// border (readable on light and dark). Layer in screenshot pixels; <see cref="PixelSize"/> —
/// how many layer units per device pixel (overlay — 1, editor — 1/scale), so line stays thin at any zoom. Not in document/export — HUD only.
///
/// </summary>
public sealed class SnapGuideLayer : FrameworkElement
{
    private static readonly Brush CoreBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0x78, 0xF6))); // Brush.Accent
    private static readonly Brush HaloBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)));

    private IReadOnlyList<SnapGuide> _guides = [];
    private double _pixelSize = 1;

    public SnapGuideLayer() => IsHitTestVisible = false;

    public IReadOnlyList<SnapGuide> Guides
    {
        get => _guides;
        set
        {
            value ??= [];
            if (value.SequenceEqual(_guides)) return;
            _guides = value;
            InvalidateVisual();
        }
    }

    /// <summary>Layer units (screenshot pixels) per device pixel.</summary>
    public double PixelSize
    {
        get => _pixelSize;
        set { if (_pixelSize == value || !(value > 0)) return; _pixelSize = value; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        // First borders, then cores: intersecting guides don't cover each other with light.
        foreach (var g in _guides) dc.DrawRectangle(HaloBrush, null, LineRect(g, _pixelSize, halo: true));
        foreach (var g in _guides) dc.DrawRectangle(CoreBrush, null, LineRect(g, _pixelSize, halo: false));
    }

    /// <summary>
    /// Line lands exactly on device-pixel column (row) containing position: 1 px without blur
    /// (editor canvas offset also multiple of device pixel). Border one more pixel each side.
    /// </summary>
    private static Rect LineRect(SnapGuide g, double ps, bool halo)
    {
        double at = Math.Floor(g.Position / ps + 1e-6) * ps;
        double from = Math.Min(g.Start, g.End), length = Math.Max(ps, Math.Abs(g.End - g.Start));
        double pad = halo ? ps : 0;
        return g.Orientation == GuideOrientation.Vertical
            ? new Rect(at - pad, from - pad, ps + 2 * pad, length + 2 * pad)
            : new Rect(from - pad, at - pad, length + 2 * pad, ps + 2 * pad);
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
