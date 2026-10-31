namespace Screeny.Annotations;

/// <param name="Source">Source screenshot — effects (pixelate, blur) take pixels from it.</param>
public sealed record RenderContext(System.Windows.Media.Imaging.BitmapSource Source, bool ForExport);
