using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Screeny.Capture;
using Screeny.Editing;

namespace Screeny.Overlay;

// Selection border snap: when dragging new border and moving handles, dragged edge snaps to window edges
// (WindowFinder) and contrast image edges (EdgeSnapper) within 8 screen pixels. Snapped edge
// shown by guide across screenshot. Alt disables snapping (also shapes), arrows don't snap.
public partial class OverlayWindow
{
    /// <summary>Border snap threshold in screen pixels (DIP).</summary>
    public const double FrameSnapDip = 8;

    private EdgeSnapper? _edgeSnapper;         // lazily: screenshot brightness read in tiles only near cursor
    private readonly List<SnapGuide> _frameGuides = new();

    /// <summary>Snap enabled in settings and Alt not held.</summary>
    private bool SnapActive => _settings.SnapEnabled && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

    private int FrameSnapThreshold => (int)Math.Round(FrameSnapDip * _map.DpiScale);

    /// <summary>Border gesture start: previous gesture edge estimates no longer needed.</summary>
    private void BeginFrameGesture() => _edgeSnapper?.ResetCache();

    /// <summary>Drag new border from <paramref name="anchor"/> to <paramref name="p"/> with snap.</summary>
    private void DragNewFrame(PixelPoint anchor, PixelPoint p)
    {
        _frameGuides.Clear();
        _selection.Rect = SelectionModel.FromPoints(anchor, p, SnapActive ? SnapFrameEdge : null);
        ShowFrameGuides();
    }

    /// <summary>Drag handle or border edge with snap (whole move doesn't snap).</summary>
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
    /// Nearest candidate within threshold for edge: visible window edge (window top at that point) or
    /// contrast image edge in band around cursor. Snapped edge remembered for guide.
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

        _edgeSnapper ??= new EdgeSnapper(new BitmapLuma(_shot.Bitmap));
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
    /// Bare Alt: not given to system (else Alt release enables window menu mode and steals keyboard),
    /// and ongoing gesture recalculated — snapping disables/enables immediately. Alt+F4 etc. untouched.
    /// </summary>
    private static bool IsBareAlt(KeyEventArgs e) => (e.Key == Key.System ? e.SystemKey : e.Key) is Key.LeftAlt or Key.RightAlt;

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (!IsBareAlt(e)) return;
        e.Handled = true;
        RefreshSnapGestureSoon();
    }

    /// <summary>Recalculate gesture after Alt state in Keyboard.Modifiers updates.</summary>
    private void RefreshSnapGestureSoon() => Dispatcher.BeginInvoke(RefreshSnapGesture, DispatcherPriority.Input);

    private void RefreshSnapGesture()
    {
        if (_closing || _textMode) return;
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
