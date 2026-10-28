namespace Screeny.Overlay;

public readonly record struct PixelPoint(int X, int Y);

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;   // exclusive
    public int Bottom => Y + Height; // exclusive
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(PixelPoint p) => p.X >= X && p.Y >= Y && p.X < Right && p.Y < Bottom;

    public PixelRect Clamp(PixelRect bounds)
    {
        int x1 = Math.Max(X, bounds.X), y1 = Math.Max(Y, bounds.Y);
        int x2 = Math.Min(Right, bounds.Right), y2 = Math.Min(Bottom, bounds.Bottom);
        return new PixelRect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }

    public PixelRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    public static PixelRect FromCorners(int x1, int y1, int x2, int y2)
        => new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    public System.Windows.Rect ToRect() => new(X, Y, Width, Height);
    public System.Windows.Int32Rect ToInt32Rect() => new(X, Y, Width, Height);
}

public enum Handle { None, Move, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }
