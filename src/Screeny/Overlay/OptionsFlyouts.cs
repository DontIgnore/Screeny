using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Screeny.Annotations;
using Screeny.Overlay.Tools;

namespace Screeny.Overlay;

/// <summary>What window gives flyouts: layer, work area, panel side and shape border.</summary>
public interface IFlyoutHost
{
    /// <summary>Layer above everything (Canvas) where flyouts live; coordinates for placement.</summary>
    Canvas FlyoutLayer { get; }
    /// <summary>Work area in layer coordinates: flyouts don't leave it.</summary>
    Rect FlyoutWorkArea { get; }
    /// <summary>Panel outer side is bottom (panel below selection): button flyouts open down.</summary>
    bool ToolbarFlyoutsBelow { get; }
    /// <summary>Visible shape border in layer coordinates; null — shape not visible now.</summary>
    Rect? ShapeRect(Annotation shape);
}

/// <summary>
/// State flyouts depend on. <paramref name="Section"/> — tool or selected shape section
/// (null — no options), <paramref name="Shape"/> — shape selected by Select tool (its options
/// shown next to it), <paramref name="ShapeHidden"/> — shape currently dragged.
/// </summary>
public sealed record FlyoutState(bool Enabled, ToolKind Tool, OptionsSectionKind? Section, Annotation? Shape, bool ShapeHidden, bool BeautifyOn)
{
    public static readonly FlyoutState Disabled = new(false, ToolKind.Select, null, null, false, false);
}

/// <summary>
/// Contextual options flyouts instead of bottom strip:
/// <list type="bullet">
/// <item>decoration — at ✨ button, on hover, while decoration enabled (and right after enabling);</item>
/// <item>tool options — at active tool's button, if it has options, on hover; on first selection of such
/// tool in session they open for <see cref="IntroMs"/> ms;</item>
/// <item>options of shape selected by Select tool — next to shape (above/below); while dragged,
/// panel hidden.</item>
/// </list>
/// Intentional hover: open after <see cref="HoverOpenMs"/> ms, close after <see cref="HoverCloseMs"/> ms after
/// leaving, while cursor not on button, panel or “bridge” between them.
/// Sections and events stay same (<see cref="Options"/>, <see cref="Beautify"/>); only display location changes.
/// </summary>
public sealed class OptionsFlyouts
{
    public const int HoverOpenMs = 150, HoverCloseMs = 300, IntroMs = 1500;
    /// <summary>Selection border drawn 3 px wider than shape — flyout steps away from it.</summary>
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
            // Content changed (other section) — different size: move to stay centered on anchor.
            flyout.SizeChanged += (_, _) => { if (flyout.IsOpen) Reposition(flyout); };
            flyout.Closing += () => RestoreTooltip(flyout);
        }
    }

    /// <summary>Tool or shape options: click events and <see cref="OptionsPanelControl.Show"/>.</summary>
    public OptionsPanelControl Options { get; } = new();
    /// <summary>Decoration options: event and <see cref="BeautifySection.Show"/>.</summary>
    public BeautifySection Beautify { get; } = new();
    public FlyoutPanel OptionsFlyout { get; } = new();
    public FlyoutPanel BeautifyFlyout { get; } = new();

    /// <summary>Options flyout currently next to selected shape.</summary>
    public bool OptionsAtShape => OptionsFlyout.IsOpen && _optionsAnchor == Anchor.Shape;

    /// <summary>Add flyouts to window layer. Without host they manage state but don't show.</summary>
    public void Attach(IFlyoutHost host)
    {
        _host = host;
        host.FlyoutLayer.Children.Add(OptionsFlyout);
        host.FlyoutLayer.Children.Add(BeautifyFlyout);
    }

    /// <summary>Element is one of flyouts (click on it must not reach canvas).</summary>
    public bool Contains(DependencyObject? d) => ReferenceEquals(d, OptionsFlyout) || ReferenceEquals(d, BeautifyFlyout);

    // ---------- State ----------

    /// <summary>New editor state: open, close or move flyouts.</summary>
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
            // Different shape or button→shape transition — fly out anew from new shape.
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

    /// <summary>Tool selected: on first selection of tool with options in session they open automatically.</summary>
    public void OnToolPicked(ToolKind tool)
    {
        if (ToolAnchorButton is not { } button || _state.Tool != tool) return;
        if (!_introduced.Add(tool))
        {
            // Tool selected by click: cursor already on its button — continue as hover.
            if (button.IsMouseOver) _optionsHover.AnchorEnter(button);
            return;
        }
        OpenOptionsAtTool();
        StartIntro(OptionsFlyout);
    }

    /// <summary>Decoration enabled (click or B): its options open immediately, disabled — close.</summary>
    public void OnBeautifyToggled(bool on)
    {
        if (!on || BeautifyAnchorButton is null) { BeautifyFlyout.Close(); return; }
        OpenBeautify();
        // Cursor on button — hover decides; not (pressed B) — panel closes itself like tooltip.
        if (!BeautifyAnchorButton.IsMouseOver) StartIntro(BeautifyFlyout);
    }

    /// <summary>Close all flyouts (border gesture, text mode, window close).</summary>
    public void CloseAll(bool animate = true)
    {
        _beautifyHover.Cancel();
        _optionsHover.Cancel();
        EndIntro(close: false);
        BeautifyFlyout.Close(animate);
        OptionsFlyout.Close(animate);
        _optionsAnchor = Anchor.None;
    }

    /// <summary>Panel or view moved (panel placement, zoom, window size): open flyouts reposition.</summary>
    public void Reposition()
    {
        if (BeautifyFlyout.IsOpen) Reposition(BeautifyFlyout);
        if (OptionsFlyout.IsOpen) Reposition(OptionsFlyout);
    }

    // ---------- For test instance ----------

    /// <summary>Open decoration options (scenario): like after hover.</summary>
    public void DevOpenBeautify() { EndIntro(close: false); if (BeautifyAnchorButton is not null) OpenBeautify(); }

    /// <summary>Open tool options at its button (scenario): like after hover.</summary>
    public void DevOpenTool() { EndIntro(close: false); if (ToolAnchorButton is not null) OpenOptionsAtTool(); }

    /// <summary>Show decoration opening frame after <paramref name="ms"/> ms and stop animation (scenario).</summary>
    public void DevFreezeBeautifyAt(double ms)
    {
        EndIntro(close: false);
        if (_host is null || BeautifyAnchorButton is not { } button) return;
        if (ButtonPlacement(BeautifyFlyout, button) is { } p) BeautifyFlyout.FreezeOpeningAt(p.Origin, p.Below, ms);
    }

    // ---------- Anchors ----------

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

    /// <param name="button">Anchor button: while its flyout open, its tooltip not shown (would overlap flyout).</param>
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

    /// <summary>Button tooltip disabled while its flyout open.</summary>
    public static bool TooltipMuted(DependencyObject button) => !ToolTipService.GetIsEnabled(button);

    /// <summary>Panel button flyout: anchor is button column full panel height, so flyout outside panel.</summary>
    private (Point TopLeft, Point Origin, bool Below)? ButtonPlacement(FlyoutPanel flyout, FrameworkElement button)
    {
        if (_host is null) return null;
        _host.FlyoutLayer.UpdateLayout(); // panel could have just moved
        if (BoundsInLayer(_toolbar) is not { } bar || BoundsInLayer(button) is not { } btn) return null;
        return Placement(flyout, new Rect(btn.Left, bar.Top, btn.Width, bar.Height), _host.ToolbarFlyoutsBelow, bar);
    }

    /// <summary>Shape flyout: above its selection border (no space — below), not on panel.</summary>
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

    /// <summary>Flyout size; hidden (Collapsed) measured as Hidden — Collapsed has zero size.</summary>
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
    /// Element and all ancestors visible (Visibility). Not IsVisible: that requires window on screen, placement
    /// must work for windowless tree too (tests).
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
        catch (InvalidOperationException) { return null; } // not in same tree as layer
    }

    // ---------- First-show tooltip ----------

    private void StartIntro(FlyoutPanel flyout)
    {
        _introFlyout = flyout;
        _introTimer.Stop();
        _introTimer.Start();
    }

    /// <summary>Tooltip end: panel closes if cursor not on it and not on button (else hover manages).</summary>
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
    /// Intentional hover for “button — flyout” pair: open after <see cref="HoverOpenMs"/> ms on button, close
    /// after <see cref="HoverCloseMs"/> ms after leaving. Close decision by cursor position at timer
    /// firing (button ∪ bridge ∪ flyout), not event order of neighboring MouseEnter/MouseLeave.
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
                if (_owner._introFlyout == _flyout) return; // tooltip will close itself
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

        /// <summary>Cursor on button, flyout or gap between (invisible “bridge” of button width).</summary>
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
            // From button through panel edge and gap to flyout, button width.
            var bridge = _flyout.Below ? new Rect(b.Left - 4, b.Top, b.Width + 8, Math.Max(0, fly.Top - b.Top))
                                       : new Rect(b.Left - 4, fly.Bottom, b.Width + 8, Math.Max(0, b.Bottom - fly.Bottom));
            return bridge.Contains(p);
        }
    }
}
