using System.Windows;
using System.Windows.Media;
using Screeny.Editing;

namespace Screeny.Overlay;

/// <summary>
/// Направляющие прилипания поверх снимка: акцентная линия в 1 пиксель устройства со светлой полупрозрачной
/// каймой (читается и на светлом, и на тёмном фоне). Слой лежит в пикселях снимка; <see cref="PixelSize"/> —
/// сколько единиц слоя в одном пикселе устройства (оверлей — 1, редактор — 1/масштаб), чтобы линия оставалась
/// тонкой при любом зуме. В документ и экспорт направляющие не попадают — это только HUD.
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

    /// <summary>Единиц слоя (пикселей снимка) в одном пикселе устройства.</summary>
    public double PixelSize
    {
        get => _pixelSize;
        set { if (_pixelSize == value || !(value > 0)) return; _pixelSize = value; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Сначала все каймы, потом сердцевины: пересекающиеся направляющие не перекрывают друг друга светлым.
        foreach (var g in _guides) dc.DrawRectangle(HaloBrush, null, LineRect(g, _pixelSize, halo: true));
        foreach (var g in _guides) dc.DrawRectangle(CoreBrush, null, LineRect(g, _pixelSize, halo: false));
    }

    /// <summary>
    /// Линия ложится ровно на столбец (строку) пикселей устройства, в котором лежит позиция: 1 px без размытия
    /// (сдвиг холста редактора тоже кратен пикселю устройства). Кайма — ещё по пикселю с каждой стороны.
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
