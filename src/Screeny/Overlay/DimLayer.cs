using System.Windows;
using System.Windows.Media;

namespace Screeny.Overlay;

/// <summary>
/// Затемнение с «дыркой» под выделение, подсветка окна под курсором, рамка выделения и ручки.
/// Рисуется в пикселях снимка (внутри PixelCanvas), поэтому 1 единица = 1 пиксель устройства.
/// </summary>
public sealed class DimLayer : FrameworkElement
{
    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)));
    private static readonly Brush HintFill = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0x4C, 0x9B, 0xFF)));
    private static readonly Pen HintPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0x9B, 0xFF)), 2));
    private static readonly Pen BorderPen = Freeze(new Pen(Brushes.White, 1));
    private static readonly Pen BorderDashPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x30, 0x78, 0xF6)), 1) { DashStyle = new DashStyle([4, 4], 0) });
    private static readonly Pen HandlePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x30, 0x78, 0xF6)), 1));

    private PixelRect? _hole, _hint;
    private bool _showHandles;
    private double _dpiScale = 1.0;

    public DimLayer() => IsHitTestVisible = false;

    public PixelRect? Hole { get => _hole; set { if (_hole == value) return; _hole = value; InvalidateVisual(); } }
    public PixelRect? Hint { get => _hint; set { if (_hint == value) return; _hint = value; InvalidateVisual(); } }
    public bool ShowHandles { get => _showHandles; set { if (_showHandles == value) return; _showHandles = value; InvalidateVisual(); } }
    public double DpiScale { get => _dpiScale; set { if (_dpiScale == value) return; _dpiScale = value; InvalidateVisual(); } }

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        if (_hole is { IsEmpty: false } hole)
        {
            DrawDimAround(dc, full, hole.ToRect());
            // Рамка по внешнему краю выделения: полупиксельный сдвиг даёт чёткую линию в 1 px.
            var border = new Rect(hole.X - 0.5, hole.Y - 0.5, hole.Width + 1, hole.Height + 1);
            dc.DrawRectangle(null, BorderPen, border);
            dc.DrawRectangle(null, BorderDashPen, border);
            if (_showHandles) DrawHandles(dc, hole);
        }
        else
        {
            dc.DrawRectangle(DimBrush, null, full);
        }

        if (_hint is { IsEmpty: false } hint)
            dc.DrawRectangle(HintFill, HintPen, new Rect(hint.X + 1, hint.Y + 1, Math.Max(0, hint.Width - 2), Math.Max(0, hint.Height - 2)));
    }

    /// <summary>Затемнение четырьмя прямоугольниками вокруг дырки (сверху, снизу, слева, справа) — без CombinedGeometry.</summary>
    private static void DrawDimAround(DrawingContext dc, Rect full, Rect hole)
    {
        hole.Intersect(full);
        if (hole.IsEmpty) { dc.DrawRectangle(DimBrush, null, full); return; }
        DrawIfAny(dc, full.Left, full.Top, full.Width, hole.Top - full.Top);              // сверху
        DrawIfAny(dc, full.Left, hole.Bottom, full.Width, full.Bottom - hole.Bottom);     // снизу
        DrawIfAny(dc, full.Left, hole.Top, hole.Left - full.Left, hole.Height);           // слева
        DrawIfAny(dc, hole.Right, hole.Top, full.Right - hole.Right, hole.Height);        // справа
    }

    private static void DrawIfAny(DrawingContext dc, double x, double y, double w, double h)
    {
        if (w > 0 && h > 0) dc.DrawRectangle(DimBrush, null, new Rect(x, y, w, h));
    }

    private void DrawHandles(DrawingContext dc, PixelRect r)
    {
        double s = Math.Round(7 * _dpiScale), h = s / 2;
        double cx = r.X + r.Width / 2.0, cy = r.Y + r.Height / 2.0;
        DrawHandle(dc, r.X, r.Y, s, h); DrawHandle(dc, cx, r.Y, s, h); DrawHandle(dc, r.Right, r.Y, s, h);
        DrawHandle(dc, r.Right, cy, s, h); DrawHandle(dc, r.Right, r.Bottom, s, h); DrawHandle(dc, cx, r.Bottom, s, h);
        DrawHandle(dc, r.X, r.Bottom, s, h); DrawHandle(dc, r.X, cy, s, h);
    }

    private static void DrawHandle(DrawingContext dc, double x, double y, double s, double h)
        => dc.DrawRectangle(Brushes.White, HandlePen, new Rect(Math.Round(x - h) + 0.5, Math.Round(y - h) + 0.5, s, s));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
