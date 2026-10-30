namespace Screeny.Overlay;

/// <summary>
/// Расстояния от пикселя под курсором до ближайшей контрастной границы в четырёх направлениях (как «красные линии»
/// в Figma), в пикселях снимка. Границы — как у прилипания рамки (<see cref="EdgeSnapper.FindNext"/>); если в
/// направлении границы нет, линия идёт до края области <see cref="Bounds"/>. Чистая логика.
/// <para>
/// Граница <c>e</c> лежит между пикселями <c>e − 1</c> и <c>e</c>. Расстояние — число пикселей строго между пикселем
/// курсора и границей: курсор в столбце 150 внутри элемента со столбцами 100…199 даёт 50 слева и 49 справа
/// (50 + 1 + 49 = ширина элемента).
/// </para>
/// </summary>
/// <param name="From">Пиксель под курсором.</param>
/// <param name="Bounds">Область поиска: выделение у линейки, монитор при Alt.</param>
/// <param name="Left">Левая граница (координата x границы).</param>
/// <param name="Right">Правая граница.</param>
/// <param name="Top">Верхняя граница (координата y).</param>
/// <param name="Bottom">Нижняя граница.</param>
public readonly record struct EdgeDistances(PixelPoint From, PixelRect Bounds, int Left, int Right, int Top, int Bottom)
{
    /// <summary>Половина полосы устойчивости границы для расстояний: края кнопок и полей высотой от ~10 px.</summary>
    public const int BandHalf = 4;

    public int ToLeft => From.X - Left;
    public int ToRight => Right - From.X - 1;
    public int ToTop => From.Y - Top;
    public int ToBottom => Bottom - From.Y - 1;

    /// <summary>
    /// Границы вокруг <paramref name="p"/> в пределах <paramref name="bounds"/>; null — точка вне области.
    /// </summary>
    public static EdgeDistances? Measure(EdgeSnapper edges, PixelPoint p, PixelRect bounds)
    {
        if (bounds.IsEmpty || p.X < bounds.X || p.Y < bounds.Y || p.X >= bounds.Right || p.Y >= bounds.Bottom) return null;
        int left = edges.FindNext(EdgeAxis.Vertical, p.X, p.Y, -1, bounds.X + 1) ?? bounds.X;
        int right = edges.FindNext(EdgeAxis.Vertical, p.X + 1, p.Y, +1, bounds.Right - 1) ?? bounds.Right;
        int top = edges.FindNext(EdgeAxis.Horizontal, p.Y, p.X, -1, bounds.Y + 1) ?? bounds.Y;
        int bottom = edges.FindNext(EdgeAxis.Horizontal, p.Y + 1, p.X, +1, bounds.Bottom - 1) ?? bounds.Bottom;
        // FindNext держится внутри снимка, а не области: граница за краем области — это сам край.
        return new EdgeDistances(p, bounds, Math.Max(left, bounds.X), Math.Min(right, bounds.Right), Math.Max(top, bounds.Y), Math.Min(bottom, bounds.Bottom));
    }
}
