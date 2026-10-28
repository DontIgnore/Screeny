using Screeny.Overlay;

namespace Screeny.Capture;

/// <summary>Окно верхнего уровня. Rect — в пикселях снимка (уже смещён на VirtualRect).</summary>
public sealed record WindowInfo(IntPtr Handle, PixelRect Rect);
