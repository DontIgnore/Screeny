using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Screeny.Overlay;

/// <summary>Лупа 15×15 px (<see cref="Cells"/>) вокруг курсора с перекрестием, координатами и цветом пикселя.</summary>
public partial class MagnifierControl : UserControl
{
    private const int Cells = 15;
    private const double ZoomSize = 124;
    private ImageBrush? _brush;
    private BitmapSource? _source;
    private PixelPoint? _last;
    private readonly SolidColorBrush _swatch = new(Colors.Transparent); // одна кисть, меняется только Color
    private readonly byte[] _pixel = new byte[4];

    public MagnifierControl()
    {
        InitializeComponent();
        Zoom.Width = Zoom.Height = ZoomSize;
        CenterCell.Width = CenterCell.Height = ZoomSize / Cells + 1;
        Swatch.Background = _swatch;
    }

    /// <summary>Пиксель снимка в центре лупы (последний переданный в <see cref="Update"/>).</summary>
    public PixelPoint? Current => _last;

    /// <summary>Показывать цвет пикселя (образец и HEX); выключенная пипетка его прячет — остаются координаты.</summary>
    public bool ShowColor
    {
        get => ColorRow.Visibility == Visibility.Visible;
        set => ColorRow.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Update(BitmapSource shot, PixelPoint p)
    {
        bool sameShot = ReferenceEquals(shot, _source);
        if (sameShot && _last == p) return; // тот же пиксель — ничего не меняется
        if (!sameShot)
        {
            _source = shot;
            _brush = new ImageBrush(shot) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            Zoom.Fill = _brush;
        }
        _last = p;
        // Снимок в 96 DPI, поэтому DIP-координаты Viewbox совпадают с пикселями.
        _brush!.Viewbox = new Rect(p.X - Cells / 2, p.Y - Cells / 2, Cells, Cells);
        Coords.Text = $"{p.X}, {p.Y}";

        if (p.X >= 0 && p.Y >= 0 && p.X < shot.PixelWidth && p.Y < shot.PixelHeight)
        {
            shot.CopyPixels(new Int32Rect(p.X, p.Y, 1, 1), _pixel, 4, 0);
            var c = Color.FromRgb(_pixel[2], _pixel[1], _pixel[0]);
            if (_swatch.Color != c)
            {
                _swatch.Color = c;
                ColorText.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
        }
    }
}
