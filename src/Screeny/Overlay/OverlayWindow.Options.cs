using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Screeny.Annotations;
using Screeny.Export;
using Screeny.Features;
using Screeny.Overlay.Tools;

namespace Screeny.Overlay;

// Полоса параметров, палитра, смена инструмента и пипетка живут в редакторе (Editing/AnnotationEditor);
// здесь — то, что есть только у оверлея: пипетка при выделении (C) и бейдж «скопирован» у лупы.
public partial class OverlayWindow
{
    private void RefreshOptions() => _editor.RefreshOptions();
    private void SetTool(ToolKind kind) => _editor.SetTool(kind);
    private void SetPipette(bool on) => _editor.SetPipette(on);

    // ---------- Пипетка при выделении ----------

    /// <summary>C при выделении области: HEX пикселя под лупой в буфер обмена, оверлей остаётся открытым.</summary>
    private void CopyColorUnderMagnifier()
    {
        if (!_settings.IsFeatureOn(FeatureCatalog.Pipette)) return;
        var p = Magnifier.Current ?? CurrentMousePixel();
        var c = Pipette.ColorAt(_shot.Bitmap, p);
        var hex = ColorHex.Rgb(c);
        // Запись в буфер: незавершённый OCR не должен её перезаписать.
        (Application.Current as App)?.NoteClipboardWrite();
        bool ok = ClipboardService.CopyText(hex);
        ShowPipetteBadge(c, ok ? Localization.Loc.F("Pipette.Copied", hex) : Localization.Loc.T("Common.ClipboardBusy"));
    }

    private void ShowPipetteBadge(Color c, string text, TimeSpan? hideAfter = null)
    {
        PipetteBadgeChip.Background = new SolidColorBrush(c);
        PipetteBadgeText.Text = text;
        PipetteBadge.Visibility = Visibility.Visible;
        PositionPipetteBadge();
        _badgeTimer?.Stop();
        _badgeTimer = new DispatcherTimer { Interval = hideAfter ?? TimeSpan.FromSeconds(1.6) };
        _badgeTimer.Tick += (_, _) => HidePipetteBadge();
        _badgeTimer.Start();
    }

    private void HidePipetteBadge()
    {
        _badgeTimer?.Stop();
        PipetteBadge.Visibility = Visibility.Collapsed;
    }

    /// <summary>Бейдж под лупой (не влезает — над ней); едет вместе с лупой.</summary>
    private void PositionPipetteBadge()
    {
        if (PipetteBadge.Visibility != Visibility.Visible || Magnifier.Visibility != Visibility.Visible) return;
        PipetteBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = Canvas.GetLeft(Magnifier), y = Canvas.GetTop(Magnifier);
        if (double.IsNaN(x) || double.IsNaN(y)) return;
        if (Magnifier.ActualHeight <= 0) Magnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double below = y + (Magnifier.ActualHeight > 0 ? Magnifier.ActualHeight : Magnifier.DesiredSize.Height) + 4;
        var mon = _map.PixelToDip(MonitorAt(Magnifier.Current ?? CurrentMousePixel()));
        Canvas.SetLeft(PipetteBadge, x);
        Canvas.SetTop(PipetteBadge, below + PipetteBadge.DesiredSize.Height <= mon.Bottom ? below : y - 4 - PipetteBadge.DesiredSize.Height);
    }
}
