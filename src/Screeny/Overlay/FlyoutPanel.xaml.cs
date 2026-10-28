using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Screeny.Overlay;

/// <summary>Содержимое flyout'а, элементы которого появляются поочерёдно (строки, кнопки).</summary>
public interface IFlyoutItems
{
    IReadOnlyList<UIElement> FlyoutItems { get; }
}

/// <summary>
/// Всплывающая панель параметров в слое HUD (не Popup: живёт в дереве окна, поэтому её видит PrintWindow,
/// она не забирает активность и клики по ней распознаются как клики по HUD).
/// <para>
/// Открытие — «вылет из якоря»: карточка масштабируется 0,85→1 из точки якоря (<see cref="UIElement.RenderTransformOrigin"/>)
/// и проявляется за <see cref="OpenMs"/> мс (CubicEase EaseOut), а её элементы (<see cref="IFlyoutItems"/>) проявляются
/// поочерёдно с шагом <see cref="StaggerMs"/> мс, сдвигаясь на <see cref="ItemShift"/> px к своему месту. Закрытие —
/// быстрее (<see cref="CloseMs"/> мс), без поочерёдности. Если в системе выключено «Показывать анимацию»
/// (<see cref="SystemParameters.ClientAreaAnimation"/>), панель появляется и исчезает сразу.
/// </para>
/// Кадры считает сама панель (<see cref="CompositionTarget.Rendering"/>), поэтому любой кадр можно показать
/// детерминированно (<see cref="FreezeOpeningAt"/> — для проверки по скриншоту).
/// </summary>
public partial class FlyoutPanel : UserControl
{
    public const double OpenMs = 160, CloseMs = 100;
    /// <summary>Шаг поочерёдного появления элементов, их длительность и задержка первого.</summary>
    public const double StaggerMs = 28, ItemMs = 140, ItemDelayMs = 30;
    public const double StartScale = 0.85, CloseScale = 0.95, ItemShift = 6;

    /// <summary>Включена ли анимация (системная настройка); тесты могут подменить.</summary>
    public static Func<bool> AnimationsEnabled { get; set; } = () => SystemParameters.ClientAreaAnimation;

    private static readonly CubicEase Ease = CreateEase();

    private enum Phase { Closed, Opening, Open, Closing }

    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Stopwatch _clock = new();
    private Phase _phase = Phase.Closed;
    private IReadOnlyList<UIElement> _items = [];
    private bool _ticking;
    private double _closeFromOpacity = 1, _closeFromScale = 1;

    public FlyoutPanel()
    {
        InitializeComponent();
        RenderTransform = _scale;
        Unloaded += (_, _) => StopTicking();
    }

    /// <summary>Содержимое карточки (секция параметров или оформления).</summary>
    public UIElement? Body
    {
        get => Host.Content as UIElement;
        set => Host.Content = value;
    }

    /// <summary>Открыта или открывается (закрывающаяся считается закрытой: по ней уже не кликнуть).</summary>
    public bool IsOpen => _phase is Phase.Opening or Phase.Open;

    /// <summary>Идёт анимация открытия или закрытия.</summary>
    public bool IsAnimating => _phase is Phase.Opening or Phase.Closing;

    /// <summary>Открытая панель начала закрываться (с анимацией или сразу).</summary>
    public event Action? Closing;

    /// <summary>Панель под якорем (вылетает вниз); false — над якорем.</summary>
    public bool Below { get; private set; }

    /// <summary>Полная длительность открытия с поочерёдным появлением <paramref name="items"/> элементов.</summary>
    public static double OpenDuration(int items)
        => Math.Max(OpenMs, items <= 0 ? 0 : ItemDelayMs + (items - 1) * StaggerMs + ItemMs);

    /// <summary>
    /// Показать (уже открытую — только обновить точку вылета). <paramref name="origin"/> — точка якоря в долях
    /// размера панели (0..1), из неё панель «вылетает».
    /// </summary>
    public void Open(Point origin, bool below)
    {
        RenderTransformOrigin = origin;
        Below = below;
        if (IsOpen) return;
        _items = (Body as IFlyoutItems)?.FlyoutItems ?? (Body is Panel p ? p.Children.Cast<UIElement>().ToList() : []);
        Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        _phase = Phase.Opening;
        if (!AnimationsEnabled())
        {
            FinishOpen();
            return;
        }
        foreach (var item in _items) item.RenderTransform = new TranslateTransform();
        _clock.Restart();
        ApplyOpening(0);
        StartTicking();
    }

    /// <summary>Скрыть: с анимацией (если включена в системе) или сразу.</summary>
    public void Close(bool animate = true)
    {
        if (_phase == Phase.Closed) return;
        if (_phase == Phase.Closing && animate) return;
        bool wasOpen = IsOpen;
        IsHitTestVisible = false;
        if (wasOpen) Closing?.Invoke();
        ResetItems();
        if (!animate || !AnimationsEnabled())
        {
            FinishClose();
            return;
        }
        _closeFromOpacity = Opacity;
        _closeFromScale = _scale.ScaleX;
        _phase = Phase.Closing;
        _clock.Restart();
        StartTicking();
    }

    /// <summary>
    /// Для проверки по скриншоту: открыть и остановить анимацию на кадре <paramref name="ms"/> миллисекунд
    /// (как будто прошло столько времени с начала открытия). Без анимации в системе показывает итог.
    /// </summary>
    public void FreezeOpeningAt(Point origin, bool below, double ms)
    {
        Close(animate: false);
        bool animations = AnimationsEnabled();
        Open(origin, below);
        if (!animations) return;
        StopTicking();
        ApplyOpening(ms);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double t = _clock.Elapsed.TotalMilliseconds;
        if (_phase == Phase.Opening)
        {
            if (ApplyOpening(t)) FinishOpen();
        }
        else if (_phase == Phase.Closing)
        {
            double p = Ease.Ease(Clamp01(t / CloseMs));
            Opacity = _closeFromOpacity * (1 - p);
            SetScale(_closeFromScale + (CloseScale - _closeFromScale) * p);
            if (t >= CloseMs) FinishClose();
        }
        else StopTicking();
    }

    /// <summary>Кадр открытия в момент <paramref name="t"/> мс; true — открытие закончилось.</summary>
    private bool ApplyOpening(double t)
    {
        double p = Ease.Ease(Clamp01(t / OpenMs));
        Opacity = p;
        SetScale(StartScale + (1 - StartScale) * p);
        double direction = Below ? -1 : 1; // элементы приезжают со стороны якоря
        for (int i = 0; i < _items.Count; i++)
        {
            double q = Ease.Ease(Clamp01((t - ItemDelayMs - i * StaggerMs) / ItemMs));
            _items[i].Opacity = q;
            if (_items[i].RenderTransform is TranslateTransform tt) tt.Y = direction * ItemShift * (1 - q);
        }
        return t >= OpenDuration(_items.Count);
    }

    private void FinishOpen()
    {
        StopTicking();
        _phase = Phase.Open;
        Opacity = 1;
        SetScale(1);
        ResetItems();
    }

    private void FinishClose()
    {
        StopTicking();
        _phase = Phase.Closed;
        Visibility = Visibility.Collapsed;
        Opacity = 1;
        SetScale(1);
    }

    private void ResetItems()
    {
        foreach (var item in _items)
        {
            item.ClearValue(OpacityProperty);
            item.ClearValue(RenderTransformProperty);
        }
    }

    private void SetScale(double s) => _scale.ScaleX = _scale.ScaleY = s;

    private void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;
        CompositionTarget.Rendering += OnTick;
    }

    private void StopTicking()
    {
        if (!_ticking) return;
        _ticking = false;
        CompositionTarget.Rendering -= OnTick;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static CubicEase CreateEase()
    {
        var e = new CubicEase { EasingMode = EasingMode.EaseOut };
        e.Freeze();
        return e;
    }
}
