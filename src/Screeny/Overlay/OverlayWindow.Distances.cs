using System.Windows.Input;
using Screeny.Features;
using Screeny.Overlay.Tools;

namespace Screeny.Overlay;

// Расстояния до соседних границ (как «красные линии» в Figma): с линейкой — при наведении без нажатия, в режиме выбора
// области и с инструментом «Выбор» — пока зажат Alt (тоже без нажатия: Alt посреди протяжки по-прежнему только
// выключает прилипание). Границы ищутся тем же EdgeSnapper, что и для прилипания рамки, но с узкой полосой. Линии живут
// в RulerLayer и, как измерение линейки, не попадают ни в документ, ни в экспорт.
public partial class OverlayWindow
{
    private BitmapLuma? _shotLuma;         // общая яркость снимка (плитки) для прилипания и расстояний
    private EdgeSnapper? _distanceEdges;   // свой кэш оценок: наведение не должно сбрасывать кэш жеста рамки
    private PixelPoint? _distanceAt;       // пиксель, для которого показаны расстояния

    private BitmapLuma ShotLuma => _shotLuma ??= new BitmapLuma(_shot.Bitmap);

    /// <summary>
    /// Нужны ли расстояния сейчас: функция «Линейка» включена, нет жеста, пипетки и режима текста, и либо выбрана линейка,
    /// либо зажат Alt при выборе области или с инструментом «Выбор».
    /// </summary>
    private bool DistancesWanted()
    {
        if (_closing || _textMode || _editor.PipetteOn || HasGesture || !_settings.ToolAllowed(ToolKind.Ruler)) return false;
        if (_mode == OverlayMode.Editing && Toolbar.CurrentTool == ToolKind.Ruler) return true;
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        return alt && (_mode == OverlayMode.Selecting || Toolbar.CurrentTool == ToolKind.Select);
    }

    /// <summary>Показать расстояния от пикселя <paramref name="p"/> или убрать их, если они сейчас не нужны.</summary>
    private void UpdateDistances(PixelPoint p)
    {
        if (!DistancesWanted()) { ClearDistances(); return; }
        if (_distanceAt == p && RulerOverlay.Distances is not null) return;
        // Линейка меряет внутри выделения (там же рисуют), Alt — в пределах монитора под курсором.
        var bounds = _mode == OverlayMode.Editing && Toolbar.CurrentTool == ToolKind.Ruler ? _selection.Rect : MonitorAt(p);
        _distanceEdges ??= new EdgeSnapper(ShotLuma, EdgeDistances.BandHalf);
        _distanceAt = p;
        RulerOverlay.Distances = EdgeDistances.Measure(_distanceEdges, p, bounds);
    }

    private void ClearDistances()
    {
        _distanceAt = null;
        RulerOverlay.Distances = null;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ClearDistances();
    }
}
