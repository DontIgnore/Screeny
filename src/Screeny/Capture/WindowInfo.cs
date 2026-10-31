using Screeny.Overlay;

namespace Screeny.Capture;

/// <summary>A top-level window. Rect is in screenshot pixels, offset by VirtualRect.</summary>
public sealed record WindowInfo(IntPtr Handle, PixelRect Rect);
