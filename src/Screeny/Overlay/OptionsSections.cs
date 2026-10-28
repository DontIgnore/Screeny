using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Overlay.Tools;

namespace Screeny.Overlay;

/// <summary>
/// Секция параметров. Секции инструмента и фигуры показывает <see cref="OptionsPanelControl"/> во flyout'е у кнопки
/// инструмента или рядом с фигурой; оформление — <see cref="BeautifySection"/> во flyout'е у кнопки ✨.
/// </summary>
public enum OptionsSectionKind
{
    /// <summary>Прямоугольник и эллипс: образцы «Обводка» и «Заливка».</summary>
    Shape,
    /// <summary>Стрелка: четыре стиля.</summary>
    Arrow,
    /// <summary>Текст: образцы «Текст» и «Фон».</summary>
    Text,
    /// <summary>Оформление (beautify): фон-градиент, отступ, скругление, тень. Показывается, пока оформление включено.</summary>
    Beautify,
}

/// <summary>Что красит клик по палитре (и пипетка).</summary>
public enum PaintTarget
{
    /// <summary>Основной цвет: обводка фигуры, цвет текста, линия и т.п.</summary>
    Color,
    /// <summary>Заливка прямоугольника/эллипса.</summary>
    Fill,
    /// <summary>Подложка текста.</summary>
    Background,
}

/// <summary>
/// Всё, от чего зависит набор секций. Расширяется полями (например, <c>bool Beautify</c>) без смены сигнатуры
/// <see cref="OptionsSections.For"/>.
/// </summary>
public sealed record OptionsContext(ToolKind Tool, Annotation? Selected)
{
    /// <summary>Оформление включено: его секция идёт после секции инструмента или фигуры.</summary>
    public bool Beautify { get; init; }
}

public static class OptionsSections
{
    /// <summary>
    /// Секции по порядку. Выбранная фигура важнее инструмента: выбрана линия при активном
    /// прямоугольнике — секций нет. Пустой список — параметров нет (палитра красит основной цвет).
    /// </summary>
    public static IReadOnlyList<OptionsSectionKind> For(OptionsContext context)
    {
        var main = context.Selected is { } selected ? ForAnnotation(selected) : ForTool(context.Tool);
        var sections = new List<OptionsSectionKind>(2);
        if (main is { } kind) sections.Add(kind);
        if (context.Beautify) sections.Add(OptionsSectionKind.Beautify);
        return sections;
    }

    public static OptionsSectionKind? ForTool(ToolKind tool) => tool switch
    {
        ToolKind.Rect or ToolKind.Ellipse => OptionsSectionKind.Shape,
        ToolKind.Arrow => OptionsSectionKind.Arrow,
        ToolKind.Text => OptionsSectionKind.Text,
        _ => null,
    };

    /// <summary>Только прямоугольник и эллипс, а не любые <see cref="BoxAnnotation"/>: у эффектов цвета нет.</summary>
    public static OptionsSectionKind? ForAnnotation(Annotation a) => a switch
    {
        RectAnnotation or EllipseAnnotation => OptionsSectionKind.Shape,
        ArrowAnnotation => OptionsSectionKind.Arrow,
        TextAnnotation => OptionsSectionKind.Text,
        _ => null,
    };

    /// <summary>Образцы цвета, которые есть у секции.</summary>
    public static IReadOnlyList<PaintTarget> Targets(OptionsSectionKind kind) => kind switch
    {
        OptionsSectionKind.Shape => [PaintTarget.Color, PaintTarget.Fill],
        OptionsSectionKind.Text => [PaintTarget.Color, PaintTarget.Background],
        _ => [],
    };

    /// <summary>Активный образец, если он есть в показанных секциях; иначе палитра красит основной цвет.</summary>
    public static PaintTarget EffectiveTarget(IReadOnlyList<OptionsSectionKind> sections, PaintTarget active)
        => active == PaintTarget.Color || sections.Any(s => Targets(s).Contains(active)) ? active : PaintTarget.Color;
}
