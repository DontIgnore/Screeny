namespace Screeny.Overlay;

/// <summary>Пиксели снимка → DIP окна оверлея (мышь приходит уже в пикселях снимка через GetPosition(PixelCanvas)).</summary>
public sealed class CoordinateMapper
{
    public PixelPoint VirtualOrigin { get; }
    public double DpiScale { get; }

    public CoordinateMapper(PixelPoint virtualOrigin, double dpiScale)
    {
        VirtualOrigin = virtualOrigin;
        DpiScale = dpiScale <= 0 ? 1.0 : dpiScale;
    }

    public System.Windows.Point PixelToDip(PixelPoint p) => new(p.X / DpiScale, p.Y / DpiScale);
    public System.Windows.Rect PixelToDip(PixelRect r) => new(r.X / DpiScale, r.Y / DpiScale, r.Width / DpiScale, r.Height / DpiScale);
}
