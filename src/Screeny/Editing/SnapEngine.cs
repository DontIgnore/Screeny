using System.Windows;

namespace Screeny.Editing;

/// <summary>Направляющая: вертикальная — линия <c>x = Position</c> от <c>Start</c> до <c>End</c> по Y, горизонтальная — наоборот.</summary>
public enum GuideOrientation { Vertical, Horizontal }

/// <summary>Тонкая линия-направляющая в пикселях снимка: показывает, к чему прилип край или центр.</summary>
public readonly record struct SnapGuide(GuideOrientation Orientation, double Position, double Start, double End);

/// <param name="Offset">Поправка к положению перемещаемого объекта (по каждой оси — 0, если прилипания нет).</param>
/// <param name="Guides">Направляющие для совпавших краёв и центров уже после поправки.</param>
public sealed record SnapResult(Vector Offset, IReadOnlyList<SnapGuide> Guides)
{
    public static SnapResult None { get; } = new(default, []);
}

/// <summary>
/// Прилипание краёв и центров (smart guides), чистая логика без UI. По каждой оси независимо сравниваются
/// левый край, центр и правый край перемещаемого прямоугольника (по Y — верх, центр, низ) с теми же линиями
/// целей; ближайшая пара в пределах порога задаёт поправку. Точка — прямоугольник нулевого размера.
/// </summary>
public static class SnapEngine
{
    /// <summary>Совпадение линий после поправки (погрешность double).</summary>
    private const double Same = 1e-3;

    public static SnapResult Snap(Rect moving, IReadOnlyList<Rect> targets, double threshold)
    {
        if (moving.IsEmpty || targets.Count == 0 || !(threshold > 0)) return SnapResult.None;
        double? dx = Nearest(moving, targets, vertical: true, threshold);
        double? dy = Nearest(moving, targets, vertical: false, threshold);
        if (dx is null && dy is null) return SnapResult.None;

        var snapped = Rect.Offset(moving, dx ?? 0, dy ?? 0);
        var guides = new List<SnapGuide>();
        if (dx is not null) AddGuides(guides, snapped, targets, vertical: true);
        if (dy is not null) AddGuides(guides, snapped, targets, vertical: false);
        return new SnapResult(new Vector(dx ?? 0, dy ?? 0), guides);
    }

    /// <summary>Точка прилипает так же, как прямоугольник нулевого размера.</summary>
    public static SnapResult SnapPoint(Point p, IReadOnlyList<Rect> targets, double threshold) => Snap(new Rect(p, p), targets, threshold);

    /// <summary>Линии прямоугольника по оси: вертикальные (X) — лево, центр, право; горизонтальные (Y) — верх, центр, низ.</summary>
    private static void Lines(Rect r, bool vertical, Span<double> lines)
    {
        double start = vertical ? r.Left : r.Top;
        double length = vertical ? r.Width : r.Height;
        lines[0] = start;
        lines[1] = start + length / 2;
        lines[2] = vertical ? r.Right : r.Bottom;
    }

    /// <summary>Наименьшая по модулю поправка, совмещающая любую линию объекта с любой линией цели; null — ничего в пределах порога.</summary>
    private static double? Nearest(Rect moving, IReadOnlyList<Rect> targets, bool vertical, double threshold)
    {
        Span<double> movingLines = stackalloc double[3];
        Span<double> targetLines = stackalloc double[3];
        Lines(moving, vertical, movingLines);
        double? best = null;
        foreach (var t in targets)
        {
            if (t.IsEmpty) continue;
            Lines(t, vertical, targetLines);
            foreach (var v in targetLines)
                foreach (var m in movingLines)
                {
                    double d = v - m;
                    if (Math.Abs(d) <= threshold && (best is null || Math.Abs(d) < Math.Abs(best.Value))) best = d;
                }
        }
        return best;
    }

    /// <summary>
    /// Направляющая на каждую совпавшую линию: через общую протяжённость объекта и всех целей с этой линией.
    /// Одна позиция — одна направляющая (у точки лево, центр и право совпадают).
    /// </summary>
    private static void AddGuides(List<SnapGuide> guides, Rect snapped, IReadOnlyList<Rect> targets, bool vertical)
    {
        var orientation = vertical ? GuideOrientation.Vertical : GuideOrientation.Horizontal;
        Span<double> movingLines = stackalloc double[3];
        Span<double> targetLines = stackalloc double[3];
        Lines(snapped, vertical, movingLines);
        foreach (var m in movingLines)
        {
            if (guides.Any(g => g.Orientation == orientation && Math.Abs(g.Position - m) < Same)) continue;
            double start = vertical ? snapped.Top : snapped.Left, end = vertical ? snapped.Bottom : snapped.Right;
            bool matched = false;
            foreach (var t in targets)
            {
                if (t.IsEmpty) continue;
                Lines(t, vertical, targetLines);
                if (!(Math.Abs(targetLines[0] - m) < Same || Math.Abs(targetLines[1] - m) < Same || Math.Abs(targetLines[2] - m) < Same)) continue;
                matched = true;
                start = Math.Min(start, vertical ? t.Top : t.Left);
                end = Math.Max(end, vertical ? t.Bottom : t.Right);
            }
            if (matched) guides.Add(new SnapGuide(orientation, m, start, end));
        }
    }
}

/// <summary>
/// Прилипание на время одного жеста инструмента: цели (края и центры других фигур документа и области снимка)
/// собираются на старте жеста, порог и «включено ли» спрашиваются у хоста на каждом движении — так <c>Alt</c>
/// посреди перетаскивания сразу выключает прилипание. Текущие направляющие — в <see cref="Guides"/>.
/// </summary>
/// <param name="enabled">Настройка включена и <c>Alt</c> не зажат.</param>
/// <param name="threshold">Порог в пикселях снимка (6 экранных пикселей при текущем масштабе).</param>
/// <param name="area">Область снимка (выделение в оверлее, холст в редакторе); пустая — без неё.</param>
public sealed class Snapper(Func<bool> enabled, Func<double> threshold, Func<Rect> area)
{
    private Rect[] _targets = [];

    /// <summary>Направляющие текущего прилипания; пусто — ничего не прилипло или жеста нет.</summary>
    public IReadOnlyList<SnapGuide> Guides { get; private set; } = [];
    public event Action? GuidesChanged;

    /// <summary>Начало жеста: цели — все фигуры документа, кроме <paramref name="exclude"/> (перемещаемой), и область.</summary>
    public void Begin(Screeny.Annotations.AnnotationDocument doc, Screeny.Annotations.Annotation? exclude = null)
        => Begin(doc.Items.Where(a => !ReferenceEquals(a, exclude)).Select(a => a.GeometryBounds));

    /// <summary>Начало жеста с готовым списком целей (кроме области — она добавляется сама).</summary>
    public void Begin(IEnumerable<Rect> others)
    {
        var list = others.Where(r => !r.IsEmpty).ToList();
        var a = area();
        if (!a.IsEmpty && a.Width > 0 && a.Height > 0) list.Add(a);
        _targets = list.ToArray();
        SetGuides([]);
    }

    /// <summary>Поправка для перемещаемого прямоугольника (0, если прилипание выключено или не нашлось цели).</summary>
    public Vector SnapRect(Rect moving)
    {
        if (!enabled()) { SetGuides([]); return default; }
        var r = SnapEngine.Snap(moving, _targets, threshold());
        SetGuides(r.Guides);
        return r.Offset;
    }

    /// <summary>Точка (угол рисуемой фигуры, конец линии, позиция текста), прилипшая к линиям целей.</summary>
    public Point SnapPoint(Point p) => p + SnapRect(new Rect(p, p));

    /// <summary>Убрать направляющие, не заканчивая жест (Shift: квадрат и шаг 45° важнее прилипания).</summary>
    public void Hide() => SetGuides([]);

    /// <summary>Конец жеста: направляющие исчезают.</summary>
    public void End()
    {
        _targets = [];
        SetGuides([]);
    }

    private void SetGuides(IReadOnlyList<SnapGuide> guides)
    {
        if (guides.Count == 0 && Guides.Count == 0) return;
        if (guides.SequenceEqual(Guides)) return;
        Guides = guides;
        GuidesChanged?.Invoke();
    }
}
