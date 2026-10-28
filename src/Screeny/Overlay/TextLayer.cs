using System.Windows;
using System.Windows.Media;
using Screeny.Ocr;

namespace Screeny.Overlay;

/// <summary>
/// Режим текста: внутри выделения всё, кроме найденных слов, слегка притушено, поэтому сразу видно, где есть текст.
/// Выделенные слова залиты акцентом подряд по строке, как выделенный текст. Найденные QR- и штрихкоды
/// обведены рамкой акцентного цвета (открытый в плашке — ещё и подсвечен). Рисуется в пикселях снимка.
/// </summary>
public sealed class TextLayer : FrameworkElement
{
    private static readonly Brush Shade = Freeze(new SolidColorBrush(Color.FromArgb(0x4A, 0, 0, 0)));
    private static readonly Brush SelectFill = Freeze(new SolidColorBrush(Color.FromArgb(0x5C, 0x30, 0x78, 0xF6)));
    private static readonly Brush MarqueeFill = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0x30, 0x78, 0xF6)));
    private static readonly Color Accent = Color.FromRgb(0x30, 0x78, 0xF6);

    private OcrLayout? _layout;
    private IReadOnlySet<int> _selection = new HashSet<int>();
    private PixelRect _area;
    private Rect? _marquee;
    private double _dpiScale = 1;
    private Geometry? _shade;           // кэш: на тексте в тысячу слов булева операция на каждый сдвиг мыши тормозит
    private IReadOnlyList<Rect> _codes = [];
    private int? _activeCode;
    private static readonly Brush CodeFill = Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0x30, 0x78, 0xF6)));

    public TextLayer() => IsHitTestVisible = false;

    /// <summary>Пиксели устройства на DIP: обводки держим в 1 DIP при любом масштабе.</summary>
    public double DpiScale { get => _dpiScale; set { if (_dpiScale == value) return; _dpiScale = value; _shade = null; InvalidateVisual(); } }

    public void Show(OcrLayout layout, PixelRect area) { _layout = layout; _area = area; _shade = null; _selection = new HashSet<int>(); _marquee = null; InvalidateVisual(); }

    public void Clear() { _layout = null; _shade = null; _marquee = null; _selection = new HashSet<int>(); _codes = []; _activeCode = null; InvalidateVisual(); }

    /// <summary>Рамки найденных кодов в пикселях снимка; <paramref name="active"/> — код, открытый в плашке.</summary>
    public void SetCodes(IReadOnlyList<Rect> codes, int? active) { _codes = codes; _activeCode = active; _shade = null; InvalidateVisual(); }

    public void SetSelection(IReadOnlySet<int> selection, Rect? marquee) { _selection = selection; _marquee = marquee; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        if (_layout is null || _area.IsEmpty) return;
        double pad = 2 * _dpiScale, radius = 3 * _dpiScale;

        // Притушить область, «вырезав» под каждое слово светлое окно. Геометрия считается один раз.
        if (_shade is null)
        {
            var holes = new GeometryGroup { FillRule = FillRule.Nonzero };
            for (int i = 0; i < _layout.WordCount; i++)
            {
                var b = _layout.Word(i).Box;
                holes.Children.Add(new RectangleGeometry(new Rect(b.X - pad, b.Y - pad, b.Width + 2 * pad, b.Height + 2 * pad), radius, radius));
            }
            foreach (var code in _codes) holes.Children.Add(new RectangleGeometry(code, radius, radius));
            _shade = Geometry.Combine(new RectangleGeometry(_area.ToRect()), holes, GeometryCombineMode.Exclude, null);
            _shade.Freeze();
        }
        dc.DrawGeometry(Shade, null, _shade);

        foreach (var run in _layout.SelectionRuns(_selection))
            dc.DrawRoundedRectangle(SelectFill, null, new Rect(run.X - pad, run.Y - pad, run.Width + 2 * pad, run.Height + 2 * pad), radius, radius);

        // Коды: рамка 2 DIP, открытый в плашке — с заливкой.
        if (_codes.Count > 0)
        {
            var codePen = new Pen(new SolidColorBrush(Accent), 2 * _dpiScale);
            codePen.Freeze();
            for (int i = 0; i < _codes.Count; i++)
                dc.DrawRoundedRectangle(i == _activeCode ? CodeFill : null, codePen, _codes[i], radius, radius);
        }

        if (_marquee is { } m)
        {
            var dash = new Pen(new SolidColorBrush(Accent), _dpiScale) { DashStyle = new DashStyle([4, 3], 0) };
            dash.Freeze();
            dc.DrawRectangle(MarqueeFill, dash, m);
        }
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
