using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Screeny.Overlay;

/// <summary>Flyout content whose items appear one by one (rows, buttons).</summary>
public interface IFlyoutItems
{
    IReadOnlyList<UIElement> FlyoutItems { get; }
}

/// <summary>
/// Options flyout in HUD layer (not Popup: lives in window tree, so PrintWindow sees it,
/// doesn't steal focus, clicks on it recognized as HUD clicks).
/// <para>
/// Opening — “fly out of anchor”: card scales 0.85→1 from anchor point (<see cref="UIElement.RenderTransformOrigin"/>)
/// and fades in over <see cref="OpenMs"/> ms (CubicEase EaseOut), items (<see cref="IFlyoutItems"/>) fade in
/// one by one with <see cref="StaggerMs"/> ms step, shifting by <see cref="ItemShift"/> px to place. Closing —
/// faster (<see cref="CloseMs"/> ms), without stagger. If system “Show animations” off
/// (<see cref="SystemParameters.ClientAreaAnimation"/>), panel appears/disappears instantly.
/// </para>
/// Frames computed by panel itself (<see cref="CompositionTarget.Rendering"/>), so any frame can be shown
/// deterministically (<see cref="FreezeOpeningAt"/> — for screenshot verification).
/// </summary>
public partial class FlyoutPanel : UserControl
{
    public const double OpenMs = 160, CloseMs = 100;
    /// <summary>Stagger step, duration and first delay.</summary>
    public const double StaggerMs = 28, ItemMs = 140, ItemDelayMs = 30;
    public const double StartScale = 0.85, CloseScale = 0.95, ItemShift = 6;

    /// <summary>Animation enabled (system setting); tests may substitute.</summary>
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

    /// <summary>Card content (options or decoration section).</summary>
    public UIElement? Body
    {
        get => Host.Content as UIElement;
        set => Host.Content = value;
    }

    /// <summary>Open or opening (closing counts closed: can't click).</summary>
    public bool IsOpen => _phase is Phase.Opening or Phase.Open;

    /// <summary>Open/close animation running.</summary>
    public bool IsAnimating => _phase is Phase.Opening or Phase.Closing;

    /// <summary>Open panel started closing (with or without animation).</summary>
    public event Action? Closing;

    /// <summary>Panel below anchor (flies down); false — above.</summary>
    public bool Below { get; private set; }

    /// <summary>Full opening duration with stagger of <paramref name="items"/> items.</summary>
    public static double OpenDuration(int items)
        => Math.Max(OpenMs, items <= 0 ? 0 : ItemDelayMs + (items - 1) * StaggerMs + ItemMs);

    /// <summary>
    /// Show (already open — only update fly-out point). <paramref name="origin"/> — anchor point in fractions of
    /// panel size (0..1), panel “flies out” of it.
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

    /// <summary>Hide: with animation (if enabled in system) or immediately.</summary>
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
    /// For screenshot verification: open and stop animation at frame <paramref name="ms"/> milliseconds
    /// (as if so much time passed since open start). Without system animation shows final.
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

    /// <summary>Opening frame at time <paramref name="t"/> ms; true — opening finished.</summary>
    private bool ApplyOpening(double t)
    {
        double p = Ease.Ease(Clamp01(t / OpenMs));
        Opacity = p;
        SetScale(StartScale + (1 - StartScale) * p);
        double direction = Below ? -1 : 1; // items arrive from anchor side
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
