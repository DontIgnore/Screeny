using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Screeny.Capture;
using Screeny.Localization;

namespace Screeny.Overlay;

// Серия снимков (удержание клавиши снимка): оверлей открывается с лентой кадров внизу монитора под курсором. Клик по
// миниатюре или PageUp/PageDown меняет замороженный кадр (стрелки заняты сдвигом выделения и фигур); выделение, пометки
// и всё остальное остаются и работают с выбранным кадром. Смена кадра сбрасывает всё, что держит пиксели прежнего:
// яркость для прилипания и расстояний, лупу и пипетку (через источник редактора), эффекты (их кэш по источнику),
// режим текста с кодами (перераспознаётся), проверку на личные данные (перепроверяется), окна под курсором. Превью
// оформления в оверлее нет — оно собирается из текущего кадра при завершении. В результат и историю уходит только
// выбранный кадр; остальные отпускаются при закрытии.
public partial class OverlayWindow
{
    private const double FilmstripGap = 12;

    private IReadOnlyList<BurstFrame> _frames = [];
    private int _frameIndex = -1;
    private Rect? _filmstripRect;   // место ленты в DIP: панель инструментов её не перекрывает

    /// <summary>Оверлей показывает серию (больше одного кадра).</summary>
    public bool HasBurst => _frames.Count > 1;

    /// <summary>Кадр серии, который сейчас на экране (0 — снимок по нажатию).</summary>
    public int FrameIndex => _frameIndex;

    /// <summary>
    /// Кадры серии до показа окна: все в геометрии первого (размер снимка тот же). Меньше двух кадров — обычный снимок.
    /// </summary>
    public void SetBurst(IReadOnlyList<BurstFrame> frames, int initial)
    {
        if (frames.Count < 2 || frames.Any(f => f.Bitmap.PixelWidth != _shot.Bitmap.PixelWidth || f.Bitmap.PixelHeight != _shot.Bitmap.PixelHeight)) return;
        _frames = frames;
        _frameIndex = -1;
        BuildFilmstrip();
        ShowFrame(initial);
    }

    private void BuildFilmstrip()
    {
        FilmstripItems.Children.Clear();
        for (int i = 0; i < _frames.Count; i++)
        {
            int index = i;
            var image = new Image { Source = _frames[i].Thumbnail, Stretch = Stretch.Uniform, Height = 54 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var thumb = new Border
            {
                Child = image,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0),
                Cursor = Cursors.Hand,
                ToolTip = Loc.F("Burst.FrameTip", i + 1, _frames.Count),
            };
            thumb.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (!HasGesture) ShowFrame(index);
                Focus(); // клавиатура остаётся у оверлея: Enter копирует выбранный кадр
            };
            FilmstripItems.Children.Add(thumb);
        }
        Filmstrip.PreviewMouseWheel -= OnFilmstripWheel;
        Filmstrip.PreviewMouseWheel += OnFilmstripWheel;
        Filmstrip.Visibility = Visibility.Visible;
    }

    /// <summary>Колесо над лентой листает её, а не меняет толщину кисти.</summary>
    private void OnFilmstripWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        FilmstripScroll.ScrollToHorizontalOffset(FilmstripScroll.HorizontalOffset - e.Delta / 2.0);
    }

    /// <summary>Сменить замороженный кадр. Идущий жест и ввод текста завершаются, режим текста перераспознаёт новый кадр.</summary>
    private void ShowFrame(int index)
    {
        if (!HasBurst || _closing) return;
        index = Math.Clamp(index, 0, _frames.Count - 1);
        if (index == _frameIndex) return;
        bool first = _frameIndex < 0;
        if (!first)
        {
            CommitTextEdit();
            ResetInteraction();
        }
        bool text = _textMode;
        if (text) ExitTextMode(); // распознанные слова, коды и уточнение относятся к прежнему кадру

        _frameIndex = index;
        var frame = _frames[index];
        _shot = _shot with { Bitmap = frame.Bitmap };
        _windows = frame.Windows;
        ShotImage.Source = frame.Bitmap;
        _editor.SetSource(frame.Bitmap);   // пипетка, эффекты (кэш по источнику), лупа берёт _shot.Bitmap
        _shotLuma = null;                  // прилипание рамки и расстояния
        _edgeSnapper = null;
        _distanceEdges = null;
        ClearDistances();
        HidePipetteBadge();
        CancelPrivacyCheck();              // перепроверка по новому кадру — в RefreshSelectionVisuals
        UpdateFilmstrip();
        RefreshSelectionVisuals();
        if (text) ToggleTextMode();
        if (IsLoaded && !HasGesture && !_textMode) UpdateHover(CurrentMousePixel(), Mouse.GetPosition(PixelCanvas));
    }

    private void UpdateFilmstrip()
    {
        var accent = (Brush)FindResource("Brush.Accent");
        for (int i = 0; i < FilmstripItems.Children.Count; i++)
            if (FilmstripItems.Children[i] is Border b) b.BorderBrush = i == _frameIndex ? accent : Brushes.Transparent;
        FilmstripCounter.Text = Loc.F("Burst.Counter", _frameIndex + 1, _frames.Count);
        if (FilmstripItems.Children.Count > _frameIndex && _frameIndex >= 0 && IsLoaded)
            ((FrameworkElement)FilmstripItems.Children[_frameIndex]).BringIntoView();
    }

    /// <summary>Лента — по центру внизу рабочей области монитора под курсором, не шире её.</summary>
    private void PlaceFilmstrip()
    {
        if (!HasBurst) { _filmstripRect = null; return; }
        var work = _map.PixelToDip(MonitorAt(CurrentMousePixel(), workArea: true));
        Filmstrip.MaxWidth = Math.Max(120, work.Width - 2 * FilmstripGap);
        Filmstrip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Filmstrip.DesiredSize;
        double x = work.Left + (work.Width - size.Width) / 2, y = work.Bottom - size.Height - FilmstripGap;
        Canvas.SetLeft(Filmstrip, x);
        Canvas.SetTop(Filmstrip, y);
        _filmstripRect = new Rect(x, y, size.Width, size.Height);
        if (_frameIndex >= 0 && _frameIndex < FilmstripItems.Children.Count)
            Dispatcher.BeginInvoke(() => ((FrameworkElement)FilmstripItems.Children[_frameIndex]).BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Рабочая область для панели инструментов: без полосы, которую занимает лента кадров на том же мониторе.</summary>
    private Rect ToolbarWorkArea()
    {
        var work = SelectionWorkArea();
        if (_filmstripRect is not { } film || !work.IntersectsWith(film)) return work;
        return new Rect(work.X, work.Y, work.Width, Math.Max(0, film.Top - FilmstripGap / 2 - work.Y));
    }

    /// <summary>Оверлей закрыт: невыбранные кадры больше никому не нужны (выбранный уже ушёл в результат).</summary>
    private void ReleaseFrames()
    {
        if (_frames.Count == 0) return;
        _frames = [];
        FilmstripItems.Children.Clear();
        Filmstrip.Visibility = Visibility.Collapsed;
    }
}
