using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Screeny.Annotations;
using Screeny.Overlay.Tools;

namespace Screeny.Overlay;

/// <summary>Что окно даёт всплывающим панелям: слой, рабочую область, сторону панели и рамку фигуры.</summary>
public interface IFlyoutHost
{
    /// <summary>Слой поверх всего (Canvas), в котором живут flyout'ы; в его координатах всё размещение.</summary>
    Canvas FlyoutLayer { get; }
    /// <summary>Рабочая область в координатах слоя: flyout'ы за неё не выходят.</summary>
    Rect FlyoutWorkArea { get; }
    /// <summary>Внешняя сторона панели — нижняя (панель под выделением): flyout'ы кнопок открываются вниз.</summary>
    bool ToolbarFlyoutsBelow { get; }
    /// <summary>Видимая рамка фигуры в координатах слоя; null — фигура сейчас не видна.</summary>
    Rect? ShapeRect(Annotation shape);
}

/// <summary>
/// Состояние, от которого зависят flyout'ы. <paramref name="Section"/> — секция инструмента или выбранной фигуры
/// (null — параметров нет), <paramref name="Shape"/> — фигура, выбранная инструментом «Выбор» (её параметры
/// показываются рядом с ней), <paramref name="ShapeHidden"/> — фигуру сейчас тащат.
/// </summary>
public sealed record FlyoutState(bool Enabled, ToolKind Tool, OptionsSectionKind? Section, Annotation? Shape, bool ShapeHidden, bool BeautifyOn)
{
    public static readonly FlyoutState Disabled = new(false, ToolKind.Select, null, null, false, false);
}

/// <summary>
/// Контекстные всплывающие панели параметров вместо нижней полосы:
/// <list type="bullet">
/// <item>оформление — у кнопки ✨, по наведению, пока оформление включено (и сразу после включения);</item>
/// <item>параметры инструмента — у кнопки активного инструмента, у которого они есть, по наведению; при первом выборе
/// такого инструмента в сессии открываются сами на <see cref="IntroMs"/> мс;</item>
/// <item>параметры фигуры, выбранной инструментом «Выбор», — рядом с фигурой (над ней или под ней); пока фигуру тащат,
/// панель скрыта.</item>
/// </list>
/// Наведение с намерением: открыть через <see cref="HoverOpenMs"/> мс, закрыть через <see cref="HoverCloseMs"/> мс после
/// ухода, пока курсор не на кнопке, не на панели и не в «мостике» между ними.
/// Секции и события остаются прежними (<see cref="Options"/>, <see cref="Beautify"/>); меняется только место показа.
/// </summary>
public sealed class OptionsFlyouts
{
    public const int HoverOpenMs = 150, HoverCloseMs = 300, IntroMs = 1500;
    /// <summary>Рамка выбора рисуется на 3 px шире фигуры — flyout отступает от неё.</summary>
    private const double ShapeFrame = 4;

    private enum Anchor { None, Tool, Shape }

    private readonly ToolbarControl _toolbar;
    private readonly HoverIntent _beautifyHover, _optionsHover;
    private readonly DispatcherTimer _introTimer;
    private readonly HashSet<ToolKind> _introduced = new();
    private FlyoutPanel? _introFlyout;
    private readonly Dictionary<FlyoutPanel, FrameworkElement> _mutedTooltips = new();
    private IFlyoutHost? _host;
    private FlyoutState _state = FlyoutState.Disabled;
    private Anchor _optionsAnchor;
    private ToolKind _anchorTool;

    public OptionsFlyouts(ToolbarControl toolbar)
    {
        _toolbar = toolbar;
        OptionsFlyout.Body = Options;
        BeautifyFlyout.Body = Beautify;
        _beautifyHover = new HoverIntent(this, BeautifyFlyout, () => BeautifyAnchorButton, OpenBeautify);
        _optionsHover = new HoverIntent(this, OptionsFlyout, () => ToolAnchorButton, OpenOptionsAtTool);
        _introTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IntroMs) };
        _introTimer.Tick += (_, _) => EndIntro();

        foreach (var button in toolbar.ToolButtons.Append(toolbar.BeautifyToggle))
        {
            button.MouseEnter += (s, _) => { _beautifyHover.AnchorEnter(s); _optionsHover.AnchorEnter(s); };
            button.MouseLeave += (s, _) => { _beautifyHover.AnchorLeave(s); _optionsHover.AnchorLeave(s); };
        }
        foreach (var (flyout, hover) in new[] { (BeautifyFlyout, _beautifyHover), (OptionsFlyout, _optionsHover) })
        {
            flyout.MouseEnter += (_, _) => hover.FlyoutEnter();
            flyout.MouseLeave += (_, _) => hover.FlyoutLeave();
            // Содержимое сменилось (другая секция) — размер другой: переставить, чтобы осталась по центру якоря.
            flyout.SizeChanged += (_, _) => { if (flyout.IsOpen) Reposition(flyout); };
            flyout.Closing += () => RestoreTooltip(flyout);
        }
    }

    /// <summary>Параметры инструмента или фигуры: события кликов и <see cref="OptionsPanelControl.Show"/>.</summary>
    public OptionsPanelControl Options { get; } = new();
    /// <summary>Параметры оформления: событие и <see cref="BeautifySection.Show"/>.</summary>
    public BeautifySection Beautify { get; } = new();
    public FlyoutPanel OptionsFlyout { get; } = new();
    public FlyoutPanel BeautifyFlyout { get; } = new();

    /// <summary>Flyout параметров сейчас стоит рядом с выбранной фигурой.</summary>
    public bool OptionsAtShape => OptionsFlyout.IsOpen && _optionsAnchor == Anchor.Shape;

    /// <summary>Добавить flyout'ы в слой окна. Без хоста они ведут состояние, но не показываются.</summary>
    public void Attach(IFlyoutHost host)
    {
        _host = host;
        host.FlyoutLayer.Children.Add(OptionsFlyout);
        host.FlyoutLayer.Children.Add(BeautifyFlyout);
    }

    /// <summary>Элемент — один из flyout'ов (клик по нему не должен уходить на холст).</summary>
    public bool Contains(DependencyObject? d) => ReferenceEquals(d, OptionsFlyout) || ReferenceEquals(d, BeautifyFlyout);

    // ---------- Состояние ----------

    /// <summary>Новое состояние редактора: открыть, закрыть или переставить flyout'ы.</summary>
    public void Update(FlyoutState state)
    {
        var prev = _state;
        _state = state;
        if (!state.Enabled)
        {
            CloseAll(animate: prev.Enabled);
            return;
        }

        if (!state.BeautifyOn) { _beautifyHover.Cancel(); BeautifyFlyout.Close(); }

        if (ShapeAnchored(state))
        {
            _optionsHover.Cancel();
            if (state.ShapeHidden) { OptionsFlyout.Close(); return; }
            // Другая фигура или переход от кнопки к фигуре — вылет заново из новой фигуры.
            if (OptionsFlyout.IsOpen && (_optionsAnchor != Anchor.Shape || !ReferenceEquals(prev.Shape, state.Shape))) OptionsFlyout.Close(animate: false);
            OpenOptionsAtShape();
            return;
        }

        if (_optionsAnchor == Anchor.Shape || ToolAnchorButton is null || (_optionsAnchor == Anchor.Tool && _anchorTool != state.Tool))
        {
            if (_introFlyout == OptionsFlyout) EndIntro();
            OptionsFlyout.Close();
            _optionsAnchor = Anchor.None;
        }
    }

    /// <summary>Выбран инструмент: при первом выборе инструмента с параметрами в сессии они открываются сами.</summary>
    public void OnToolPicked(ToolKind tool)
    {
        if (ToolAnchorButton is not { } button || _state.Tool != tool) return;
        if (!_introduced.Add(tool))
        {
            // Инструмент выбрали кликом: курсор уже на его кнопке — дальше как при наведении.
            if (button.IsMouseOver) _optionsHover.AnchorEnter(button);
            return;
        }
        OpenOptionsAtTool();
        StartIntro(OptionsFlyout);
    }

    /// <summary>Оформление включили (кликом или B): его параметры открываются сразу, выключили — закрываются.</summary>
    public void OnBeautifyToggled(bool on)
    {
        if (!on || BeautifyAnchorButton is null) { BeautifyFlyout.Close(); return; }
        OpenBeautify();
        // Курсор на кнопке — дальше решает наведение; нет (нажали B) — панель сама закроется, как подсказка.
        if (!BeautifyAnchorButton.IsMouseOver) StartIntro(BeautifyFlyout);
    }

    /// <summary>Закрыть все flyout'ы (жест рамки, режим текста, закрытие окна).</summary>
    public void CloseAll(bool animate = true)
    {
        _beautifyHover.Cancel();
        _optionsHover.Cancel();
        EndIntro(close: false);
        BeautifyFlyout.Close(animate);
        OptionsFlyout.Close(animate);
        _optionsAnchor = Anchor.None;
    }

    /// <summary>Панель или вид сдвинулись (размещение панели, масштаб, размер окна): открытые flyout'ы переставляются.</summary>
    public void Reposition()
    {
        if (BeautifyFlyout.IsOpen) Reposition(BeautifyFlyout);
        if (OptionsFlyout.IsOpen) Reposition(OptionsFlyout);
    }

    // ---------- Для тестового экземпляра ----------

    /// <summary>Открыть параметры оформления (сценарий): как после наведения.</summary>
    public void DevOpenBeautify() { EndIntro(close: false); if (BeautifyAnchorButton is not null) OpenBeautify(); }

    /// <summary>Открыть параметры инструмента у его кнопки (сценарий): как после наведения.</summary>
    public void DevOpenTool() { EndIntro(close: false); if (ToolAnchorButton is not null) OpenOptionsAtTool(); }

    /// <summary>Показать кадр открытия оформления через <paramref name="ms"/> мс и остановить анимацию (сценарий).</summary>
    public void DevFreezeBeautifyAt(double ms)
    {
        EndIntro(close: false);
        if (_host is null || BeautifyAnchorButton is not { } button) return;
        if (ButtonPlacement(BeautifyFlyout, button) is { } p) BeautifyFlyout.FreezeOpeningAt(p.Origin, p.Below, ms);
    }

    // ---------- Якоря ----------

    private static bool ShapeAnchored(FlyoutState s) => s.Enabled && s.Shape is not null && s.Section is not null;

    private FrameworkElement? BeautifyAnchorButton => _state.Enabled && _state.BeautifyOn && Shown(_toolbar.BeautifyToggle) ? _toolbar.BeautifyToggle : null;

    private FrameworkElement? ToolAnchorButton
        => _state.Enabled && !ShapeAnchored(_state) && _state.Section is not null && _toolbar.ToolButton(_state.Tool) is { } b && Shown(b) ? b : null;

    private void OpenBeautify()
    {
        if (BeautifyAnchorButton is not { } button) return;
        Show(BeautifyFlyout, ButtonPlacement(BeautifyFlyout, button), button);
    }

    private void OpenOptionsAtTool()
    {
        if (ToolAnchorButton is not { } button) return;
        if (_optionsAnchor != Anchor.Tool || _anchorTool != _state.Tool) OptionsFlyout.Close(animate: false);
        _optionsAnchor = Anchor.Tool;
        _anchorTool = _state.Tool;
        Show(OptionsFlyout, ButtonPlacement(OptionsFlyout, button), button);
    }

    private void OpenOptionsAtShape()
    {
        _optionsAnchor = Anchor.Shape;
        Show(OptionsFlyout, ShapePlacement());
    }

    private void Reposition(FlyoutPanel flyout)
    {
        if (flyout == BeautifyFlyout)
        {
            if (BeautifyAnchorButton is { } b) Show(flyout, ButtonPlacement(flyout, b), b); else flyout.Close();
            return;
        }
        if (_optionsAnchor == Anchor.Shape) Show(flyout, ShapePlacement());
        else if (ToolAnchorButton is { } t) Show(flyout, ButtonPlacement(flyout, t), t);
        else flyout.Close();
    }

    /// <param name="button">Кнопка-якорь: пока у неё открыт flyout, её подсказка не показывается (легла бы на flyout).</param>
    private void Show(FlyoutPanel flyout, (Point TopLeft, Point Origin, bool Below)? placement, FrameworkElement? button = null)
    {
        if (placement is not { } p) { flyout.Close(animate: false); return; }
        Canvas.SetLeft(flyout, p.TopLeft.X);
        Canvas.SetTop(flyout, p.TopLeft.Y);
        flyout.Open(p.Origin, p.Below);
        if (_mutedTooltips.TryGetValue(flyout, out var muted) && !ReferenceEquals(muted, button)) RestoreTooltip(flyout);
        if (button is not null && !_mutedTooltips.ContainsKey(flyout))
        {
            _mutedTooltips[flyout] = button;
            ToolTipService.SetIsEnabled(button, false);
        }
    }

    private void RestoreTooltip(FlyoutPanel flyout)
    {
        if (!_mutedTooltips.Remove(flyout, out var button)) return;
        button.ClearValue(ToolTipService.IsEnabledProperty);
    }

    /// <summary>Подсказка кнопки отключена, пока у неё открыт flyout.</summary>
    public static bool TooltipMuted(DependencyObject button) => !ToolTipService.GetIsEnabled(button);

    /// <summary>Flyout кнопки панели: якорь — колонка кнопки во всю высоту панели, чтобы flyout был снаружи панели.</summary>
    private (Point TopLeft, Point Origin, bool Below)? ButtonPlacement(FlyoutPanel flyout, FrameworkElement button)
    {
        if (_host is null) return null;
        _host.FlyoutLayer.UpdateLayout(); // панель могли только что передвинуть
        if (BoundsInLayer(_toolbar) is not { } bar || BoundsInLayer(button) is not { } btn) return null;
        return Placement(flyout, new Rect(btn.Left, bar.Top, btn.Width, bar.Height), _host.ToolbarFlyoutsBelow, bar);
    }

    /// <summary>Flyout фигуры: над её рамкой выбора (нет места — под ней), не на панели.</summary>
    private (Point TopLeft, Point Origin, bool Below)? ShapePlacement()
    {
        if (_host is null || _state.Shape is not { } shape || _host.ShapeRect(shape) is not { } rect) return null;
        _host.FlyoutLayer.UpdateLayout();
        rect.Inflate(ShapeFrame, ShapeFrame);
        return Placement(OptionsFlyout, rect, preferBelow: false, BoundsInLayer(_toolbar));
    }

    private (Point TopLeft, Point Origin, bool Below) Placement(FlyoutPanel flyout, Rect anchor, bool preferBelow, Rect? avoid)
    {
        var size = Measure(flyout);
        var p = HudLayout.PlaceFlyout(anchor, size, _host!.FlyoutWorkArea, preferBelow, avoid);
        var topLeft = new Point(Math.Round(p.TopLeft.X), Math.Round(p.TopLeft.Y));
        double ox = size.Width > 0 ? (anchor.Left + anchor.Width / 2 - topLeft.X) / size.Width : 0.5;
        return (topLeft, new Point(Math.Clamp(ox, 0, 1), p.Below ? 0 : 1), p.Below);
    }

    /// <summary>Размер flyout'а; скрытый (Collapsed) измеряется как Hidden — у Collapsed размер нулевой.</summary>
    private static Size Measure(FlyoutPanel flyout)
    {
        bool collapsed = flyout.Visibility == Visibility.Collapsed;
        if (collapsed) flyout.Visibility = Visibility.Hidden;
        flyout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = flyout.DesiredSize;
        if (collapsed) flyout.Visibility = Visibility.Collapsed;
        return size;
    }

    /// <summary>
    /// Элемент и все его предки видимы (Visibility). Не IsVisible: тот ещё требует окна на экране, а размещение
    /// должно работать и для дерева без окна (тесты).
    /// </summary>
    private static bool Shown(DependencyObject element)
    {
        for (var d = element; d is not null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            if (d is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private Rect? BoundsInLayer(FrameworkElement element)
    {
        if (_host is null || !Shown(element)) return null;
        try { return element.TransformToVisual(_host.FlyoutLayer).TransformBounds(new Rect(element.RenderSize)); }
        catch (InvalidOperationException) { return null; } // не в одном дереве с слоем
    }

    // ---------- Подсказка при первом показе ----------

    private void StartIntro(FlyoutPanel flyout)
    {
        _introFlyout = flyout;
        _introTimer.Stop();
        _introTimer.Start();
    }

    /// <summary>Конец подсказки: панель закрывается, если курсор не на ней и не на кнопке (иначе ею управляет наведение).</summary>
    private void EndIntro(bool close = true)
    {
        _introTimer.Stop();
        var flyout = _introFlyout;
        _introFlyout = null;
        if (!close || flyout is null || !flyout.IsOpen) return;
        var hover = flyout == BeautifyFlyout ? _beautifyHover : _optionsHover;
        if (!hover.PointerInside()) flyout.Close();
    }

    /// <summary>
    /// Наведение с намерением для пары «кнопка — flyout»: открыть через <see cref="HoverOpenMs"/> мс на кнопке, закрыть
    /// через <see cref="HoverCloseMs"/> мс после ухода. Решение о закрытии принимается по положению курсора в момент
    /// срабатывания таймера (кнопка ∪ мостик ∪ flyout), а не по порядку событий MouseEnter/MouseLeave соседних элементов.
    /// </summary>
    private sealed class HoverIntent
    {
        private readonly OptionsFlyouts _owner;
        private readonly FlyoutPanel _flyout;
        private readonly Func<FrameworkElement?> _anchor;
        private readonly DispatcherTimer _open, _close;

        public HoverIntent(OptionsFlyouts owner, FlyoutPanel flyout, Func<FrameworkElement?> anchor, Action open)
        {
            _owner = owner; _flyout = flyout; _anchor = anchor;
            _open = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverOpenMs) };
            _close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverCloseMs) };
            _open.Tick += (_, _) =>
            {
                _open.Stop();
                if (_anchor() is { IsMouseOver: true }) open();
            };
            _close.Tick += (_, _) =>
            {
                _close.Stop();
                if (!_flyout.IsOpen) return;
                if (PointerInside()) { _close.Start(); return; }
                if (_owner._introFlyout == _flyout) return; // подсказка закроется сама
                _flyout.Close();
            };
        }

        public void AnchorEnter(object sender)
        {
            if (!ReferenceEquals(sender, _anchor())) return;
            _close.Stop();
            if (!_flyout.IsOpen) { _open.Stop(); _open.Start(); }
        }

        public void AnchorLeave(object sender)
        {
            if (!ReferenceEquals(sender, _anchor())) return;
            _open.Stop();
            if (_flyout.IsOpen) RestartClose();
        }

        public void FlyoutEnter() => _close.Stop();
        public void FlyoutLeave() { if (_flyout.IsOpen) RestartClose(); }

        public void Cancel() { _open.Stop(); _close.Stop(); }

        private void RestartClose() { _close.Stop(); _close.Start(); }

        /// <summary>Курсор на кнопке, на flyout'е или в зазоре между ними (невидимый «мостик» шириной в кнопку).</summary>
        public bool PointerInside()
        {
            if (_flyout.IsMouseOver) return true;
            if (_anchor() is not { } button) return false;
            if (button.IsMouseOver) return true;
            if (_owner._host is not { } host) return false;
            var layer = host.FlyoutLayer;
            var p = Mouse.GetPosition(layer);
            if (_owner.BoundsInLayer(button) is not { } b) return false;
            double left = Canvas.GetLeft(_flyout), top = Canvas.GetTop(_flyout);
            if (double.IsNaN(left) || double.IsNaN(top)) return false;
            var fly = new Rect(left, top, _flyout.ActualWidth, _flyout.ActualHeight);
            if (fly.Contains(p)) return true;
            // От кнопки через край панели и зазор до flyout'а, шириной в кнопку.
            var bridge = _flyout.Below ? new Rect(b.Left - 4, b.Top, b.Width + 8, Math.Max(0, fly.Top - b.Top))
                                       : new Rect(b.Left - 4, fly.Bottom, b.Width + 8, Math.Max(0, b.Bottom - fly.Bottom));
            return bridge.Contains(p);
        }
    }
}
