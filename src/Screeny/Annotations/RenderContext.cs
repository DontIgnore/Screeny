namespace Screeny.Annotations;

/// <param name="Source">Исходный снимок — эффекты (пикселизация, размытие) берут пиксели из него.</param>
public sealed record RenderContext(System.Windows.Media.Imaging.BitmapSource Source, bool ForExport);
