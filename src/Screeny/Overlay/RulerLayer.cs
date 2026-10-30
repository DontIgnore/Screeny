using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Screeny.Overlay;

/// <summary>
/// Измерение линейки поверх снимка: линия с засечками и подпись Δx, Δy, длина. Рисуется в пикселях снимка
/// (внутри PixelCanvas), толщины и шрифт домножаются на <see cref="DpiScale"/>, чтобы на экране они были постоянными.
/// Здесь же расстояния до соседних границ при наведении (<see cref="Distances"/>): тонкие линии в четыре стороны
/// с подписями в пикселях. Слой не входит в документ и в экспорт.
/// </summary>
public sealed class RulerLayer : FrameworkElement
{
    private static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0x78, 0xF6)));
    private static readonly Brush HaloBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0x29, 0x25, 0x24)));
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private RulerMeasurement? _measurement;
    private EdgeDistances? _distances;
    private double _dpiScale = 1.0;

    public RulerLayer() => IsHitTestVisible = false;

    public RulerMeasurement? Measurement { get => _measurement; set { if (_measurement == value) return; _measurement = value; InvalidateVisual(); } }
    public EdgeDistances? Distances { get => _distances; set { if (_distances == value) return; _distances = value; InvalidateVisual(); } }
    public double DpiScale { get => _dpiScale; set { if (_dpiScale == value) return; _dpiScale = value; InvalidateVisual(); } }

    protected override void OnRender(DrawingContext dc)
    {
        if (_distances is { } d) RenderDistances(dc, d);
        if (_measurement is not { } m) return;
        double s = _dpiScale;
        var a = m.Start + new Vector(0.5, 0.5); // центр пикселя
        var b = m.End + new Vector(0.5, 0.5);
        var halo = new Pen(HaloBrush, 4 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var line = new Pen(LineBrush, 2 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(halo, a, b);
        dc.DrawLine(line, a, b);

        // Засечки поперёк линии на концах.
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
        // Подпись у середины, сдвинута по нормали, чтобы не закрывать саму линию.
        var mid = a + dir / 2;
        var offset = dir.LengthSquared > 1e-9 ? new Vector(-dir.Y, dir.X) / dir.Length : new Vector(0, -1);
        if (offset.Y > 0) offset = -offset; // над линией
        // Расстояние, при котором прямоугольник подписи целиком уходит с линии при любом её наклоне.
        double clearance = Math.Abs(offset.X) * size.Width / 2 + Math.Abs(offset.Y) * size.Height / 2 + 6 * s;
        var center = mid + offset * clearance;
        var box = new Rect(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height);
        dc.DrawRoundedRectangle(LabelBrush, null, box, 6 * s, 6 * s);
        dc.DrawText(text, new Point(box.X + padX, box.Y + padY));
    }

    /// <summary>Линии от центра пикселя курсора до границ, засечка на границе, подпись у середины линии.</summary>
    private void RenderDistances(DrawingContext dc, EdgeDistances d)
    {
        double s = _dpiScale;
        var halo = new Pen(HaloBrush, 3 * s);
        var line = new Pen(LineBrush, 1 * s);
        var c = new Point(d.From.X + 0.5, d.From.Y + 0.5);
        void Leg(Point end, int value, bool horizontal)
        {
            if (value <= 0) return;
            var tick = horizontal ? new Vector(0, 4 * s) : new Vector(4 * s, 0);
            dc.DrawLine(halo, c, end);
            dc.DrawLine(halo, end - tick, end + tick);
            dc.DrawLine(line, c, end);
            dc.DrawLine(line, end - tick, end + tick);
            var text = new FormattedText(string.Create(CultureInfo.InvariantCulture, $"{value} px"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Face, 11 * s, Brushes.White, pixelsPerDip: 1.0);
            double padX = 6 * s, padY = 2 * s;
            var size = new Size(text.Width + padX * 2, text.Height + padY * 2);
            var mid = c + (end - c) / 2;
            // Над горизонтальной линией, справа от вертикальной: подпись не закрывает саму линию.
            var at = horizontal ? new Point(mid.X - size.Width / 2, mid.Y - size.Height - 4 * s) : new Point(mid.X + 6 * s, mid.Y - size.Height / 2);
            var box = new Rect(at, size);
            dc.DrawRoundedRectangle(LabelBrush, null, box, 5 * s, 5 * s);
            dc.DrawText(text, new Point(box.X + padX, box.Y + padY));
        }
        Leg(new Point(d.Left, c.Y), d.ToLeft, horizontal: true);
        Leg(new Point(d.Right, c.Y), d.ToRight, horizontal: true);
        Leg(new Point(c.X, d.Top), d.ToTop, horizontal: false);
        Leg(new Point(c.X, d.Bottom), d.ToBottom, horizontal: false);
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
