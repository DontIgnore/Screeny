using System.Windows;
using System.Windows.Input;
using Screeny.Editing;

namespace Screeny.Overlay;

// Оверлей как хост редактора аннотаций: режимы оверлея решают, доступны ли параметры и пипетка,
// HUD живёт в DIP поверх снимка в пикселях (масштаб 1/dpi). Flyout'ы параметров — в слое HUD.
public partial class OverlayWindow : IAnnotationEditorHost, IFlyoutHost
{
    bool IAnnotationEditorHost.OptionsEnabled => _mode == OverlayMode.Editing && !_textMode && _selection.HasSelection;
    bool IAnnotationEditorHost.CanUsePipette => _mode == OverlayMode.Editing && !_textMode && !_closing;
    bool IAnnotationEditorHost.IsClosing => _closing;
    bool IAnnotationEditorHost.HasGesture => HasGesture;

    Point IAnnotationEditorHost.PixelToHud(Point px) => _map.PixelToDip(new PixelPoint((int)Math.Round(px.X), (int)Math.Round(px.Y)));
    double IAnnotationEditorHost.PixelsToHud(double length) => length / _map.DpiScale;

    void IAnnotationEditorHost.FocusSurface() => Focus();
    void IAnnotationEditorHost.ResetInteraction() => ResetInteraction();
    void IAnnotationEditorHost.RefreshHover() => UpdateHover(CurrentMousePixel(), Mouse.GetPosition(PixelCanvas));

    void IAnnotationEditorHost.OnPipetteChanged(bool on)
    {
        if (!on) Magnifier.Visibility = Visibility.Collapsed;
    }

    void IAnnotationEditorHost.OnBeautifyChanged() => RefreshSelectionVisuals();

    Rect IAnnotationEditorHost.SnapArea => _selection.HasSelection ? _selection.Rect.ToRect() : Rect.Empty;

    System.Windows.Controls.Canvas IFlyoutHost.FlyoutLayer => Hud;
    Rect IFlyoutHost.FlyoutWorkArea => _selection.HasSelection ? SelectionWorkArea() : _map.PixelToDip(_shot.PixelBounds);
    bool IFlyoutHost.ToolbarFlyoutsBelow => _toolbarBelow;

    /// <summary>Видимая рамка фигуры в DIP (часть за выделением обрезана — по ней и ставим flyout).</summary>
    Rect? IFlyoutHost.ShapeRect(Screeny.Annotations.Annotation shape)
    {
        var b = shape.Bounds;
        b.Intersect(_selection.Rect.ToRect());
        if (b.IsEmpty) return null;
        return new Rect(b.X / _map.DpiScale, b.Y / _map.DpiScale, b.Width / _map.DpiScale, b.Height / _map.DpiScale);
    }
}
