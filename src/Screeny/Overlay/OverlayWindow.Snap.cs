using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Screeny.Capture;
using Screeny.Editing;

namespace Screeny.Overlay;

// Прилипание рамки снимка: при протяжке новой рамки и перетаскивании ручек тянущийся край прилипает к краям окон
// (WindowFinder) и к контрастным границам на снимке (EdgeSnapper) в пределах 8 экранных пикселей. Прилипший край
// показывается направляющей через весь снимок. Alt выключает прилипание (и фигур тоже), стрелки не прилипают.
public partial class OverlayWindow
{
    /// <summary>Порог прилипания рамки в экранных пикселях (DIP).</summary>
    public const double FrameSnapDip = 8;

    private EdgeSnapper? _edgeSnapper;         // лениво: яркость снимка читается плитками только у курсора
    private readonly List<SnapGuide> _frameGuides = new();

    /// <summary>Прилипание включено в настройках и Alt не зажат.</summary>
    private bool SnapActive => _settings.SnapEnabled && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

    private int FrameSnapThreshold => (int)Math.Round(FrameSnapDip * _map.DpiScale);

    /// <summary>Начало жеста рамки: оценки границ прошлого жеста больше не нужны.</summary>
    private void BeginFrameGesture() => _edgeSnapper?.ResetCache();

    /// <summary>Протяжка новой рамки от <paramref name="anchor"/> до <paramref name="p"/> с прилипанием.</summary>
    private void DragNewFrame(PixelPoint anchor, PixelPoint p)
    {
        _frameGuides.Clear();
        _selection.Rect = SelectionModel.FromPoints(anchor, p, SnapActive ? SnapFrameEdge : null);
        ShowFrameGuides();
    }

    /// <summary>Перетаскивание ручки или края рамки с прилипанием (перемещение целиком не прилипает).</summary>
    private void DragFrameHandle(PixelPoint p)
    {
        _frameGuides.Clear();
        _selection.DragTo(p, SnapActive ? SnapFrameEdge : null);
        ShowFrameGuides();
    }

    private void ShowFrameGuides() => SnapGuides.Guides = _frameGuides.ToArray();

    private void HideFrameGuides()
    {
        _frameGuides.Clear();
        if (!_editor.ToolGestureActive) SnapGuides.Guides = [];
    }

    /// <summary>
    /// Ближайший в пределах порога кандидат для края: видимый край окна (окно сверху в этой точке) или
    /// контрастная граница снимка в полосе вокруг курсора. Прилипший край запоминается для направляющей.
    /// </summary>
    private int? SnapFrameEdge(EdgeAxis axis, int edge, int cross)
    {
        int threshold = FrameSnapThreshold;
        int? best = null;
        void Consider(int candidate)
        {
            if (Math.Abs(candidate - edge) <= threshold && (best is null || Math.Abs(candidate - edge) < Math.Abs(best.Value - edge))) best = candidate;
        }

        foreach (var e in WindowFinder.VisibleEdges(_windows, axis, cross)) Consider(e);

        _edgeSnapper ??= new EdgeSnapper(ShotLuma);
        if (_edgeSnapper.Find(axis, edge, cross, threshold) is { } found) Consider(found);

        if (best is { } b)
        {
            var bounds = _shot.PixelBounds;
            _frameGuides.Add(axis == EdgeAxis.Vertical
                ? new SnapGuide(GuideOrientation.Vertical, b, bounds.Y, bounds.Bottom)
                : new SnapGuide(GuideOrientation.Horizontal, b, bounds.X, bounds.Right));
        }
        return best;
    }

    // ---------- Alt ----------

    /// <summary>
    /// Голый Alt: не отдаём системе (иначе отпускание Alt включает режим меню окна и уводит клавиатуру),
    /// а идущий жест пересчитываем — прилипание выключается или включается сразу. Alt+F4 и прочие сочетания не трогаем.
    /// </summary>
    private static bool IsBareAlt(KeyEventArgs e) => (e.Key == Key.System ? e.SystemKey : e.Key) is Key.LeftAlt or Key.RightAlt;

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (!IsBareAlt(e)) return;
        e.Handled = true;
        RefreshSnapGestureSoon();
    }

    /// <summary>Пересчитать жест после того, как состояние Alt в Keyboard.Modifiers обновится.</summary>
    private void RefreshSnapGestureSoon() => Dispatcher.BeginInvoke(RefreshSnapGesture, DispatcherPriority.Input);

    private void RefreshSnapGesture()
    {
        if (_closing || _textMode) return;
        // Без нажатия Alt показывает или убирает расстояния до соседних границ (OverlayWindow.Distances.cs).
        if (Mouse.LeftButton != MouseButtonState.Pressed && !HasGesture) UpdateDistances(CurrentMousePixel());
        if (_editor.ToolGestureActive)
        {
            var p = Mouse.GetPosition(PixelCanvas);
            var r = _selection.Rect;
            _editor.RefreshGesture(new Point(Math.Clamp(p.X, r.X, r.Right), Math.Clamp(p.Y, r.Y, r.Bottom)));
            return;
        }
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        if (_draggingHandle)
        {
            DragFrameHandle(CurrentMousePixel());
            RefreshSelectionVisuals();
        }
        else if (_dragAnchor is { } a && _thresholdPassed)
        {
            DragNewFrame(a, CurrentMousePixel());
            RefreshSelectionVisuals();
        }
    }
}
