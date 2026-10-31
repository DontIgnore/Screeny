using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Screeny.Overlay;

/// <summary>
/// Ruler measurement over screenshot: line with ticks and label Δx, Δy, length. Drawn in screenshot pixels
/// (inside PixelCanvas), thicknesses and font multiplied by <see cref="DpiScale"/>, so they are constant on screen.
/// </summary>
public sealed class RulerLayer : FrameworkElement
{
    private static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0x78, 0xF6)));
    private static readonly Brush HaloBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0x29, 0x25, 0x24)));
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private RulerMeasurement? _measurement;
    private double _dpiScale = 1.0;

    public RulerLayer() => IsHitTestVisible = false;

    public RulerMeasurement? Measurement { get => _measurement; set { if (_measurement == value) return; _measurement = value; InvalidateVisual(); } }
    public double DpiScale { get => _dpiScale; set { if (_dpiScale == value) return; _dpiScale = value; InvalidateVisual(); } }

    protected override void OnRender(DrawingContext dc)
    {
        if (_measurement is not { } m) return;
        double s = _dpiScale;
        var a = m.Start + new Vector(0.5, 0.5); // pixel center
        var b = m.End + new Vector(0.5, 0.5);
        var halo = new Pen(HaloBrush, 4 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var line = new Pen(LineBrush, 2 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(halo, a, b);
        dc.DrawLine(line, a, b);

        // Ticks across line at ends.
        var dir = b - a;
        var n = dir.LengthSquared > 1e-9 ? new Vector(-dir.Y, dir.X) / dir.Length * (6 * s) : new Vector(0, 6 * s);
        foreach (var end in new[] { a, b })
        {
            dc.DrawLine(halo, end - n, end + n);
            dc.DrawLine(line, end - n, end + n);
        }
        if (m.Length < 1) return;

        var text = new FormattedText(m.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 12 * s, Brushes.White, pixelsPerDip: 1.0);
        double padX = 8 * s, padY = 4 * s;
        var size = new Size(text.Width + padX * 2, text.Height + padY * 2);
        // Label at middle, shifted by normal not to cover line.
        var mid = a + dir / 2;
        var offset = dir.LengthSquared > 1e-9 ? new Vector(-dir.Y, dir.X) / dir.Length : new Vector(0, -1);
        if (offset.Y > 0) offset = -offset; // above line
        // Distance at which label rectangle fully leaves line at any slope.
        double clearance = Math.Abs(offset.X) * size.Width / 2 + Math.Abs(offset.Y) * size.Height / 2 + 6 * s;
        var center = mid + offset * clearance;
        var box = new Rect(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height);
        dc.DrawRoundedRectangle(LabelBrush, null, box, 6 * s, 6 * s);
        dc.DrawText(text, new Point(box.X + padX, box.Y + padY));
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
