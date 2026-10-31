using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Screeny.Export;
using Screeny.Ocr;

namespace Screeny.Overlay;

// QR/barcodes in text mode (Ctrl+T): searched parallel with OCR, highlighted with border,
// click on code — plaque with content, “Copy” and (only http/https) “Open”.
// Link never opens itself — only by button.
public partial class OverlayWindow
{
    private IReadOnlyList<DetectedCode> _codes = [];   // in screenshot pixels
    private int? _openCode;                              // code shown in plaque
    private bool _devOpenCodeWhenReady;                  // qr scenario: open plaque as soon as codes found

    /// <summary>Code search on same image that goes to OCR. Pixels captured here, recognition outside UI thread.</summary>
    private static Task<IReadOnlyList<DetectedCode>> ScanCodesAsync(System.Windows.Media.Imaging.BitmapSource image)
    {
        var px = CodeScanner.Pixels(image);
        int w = image.PixelWidth, h = image.PixelHeight;
        return Task.Run(() =>
        {
            try { return CodeScanner.Scan(px, w, h); }
            catch (Exception ex)
            {
                Screeny.Logging.Log.Error("Barcode scan failed", ex);
                return (IReadOnlyList<DetectedCode>)[];
            }
        });
    }

    /// <summary>Codes found (borders already in screenshot pixels): highlight, on qr scenario open plaque.</summary>
    private void ShowCodes(IReadOnlyList<DetectedCode> codes)
    {
        _codes = codes;
        _openCode = null;
        TextOverlay.SetCodes(codes.Select(c => c.Box).ToList(), null);
        if (_devOpenCodeWhenReady && codes.Count > 0)
        {
            _devOpenCodeWhenReady = false;
            int url = codes.ToList().FindIndex(c => c.IsOpenableUrl);
            OpenCodePopup(url >= 0 ? url : 0);
        }
    }

    private int? CodeAt(Point p)
    {
        for (int i = 0; i < _codes.Count; i++)
            if (_codes[i].Box.Contains(p)) return i;
        return null;
    }

    private void OpenCodePopup(int index)
    {
        var code = _codes[index];
        _openCode = index;
        TextOverlay.SetCodes(_codes.Select(c => c.Box).ToList(), index);
        CodeFormatText.Text = code.IsOpenableUrl ? Localization.Loc.F("Codes.FormatLink", code.FormatName) : code.FormatName;
        CodeText.Text = code.Text;
        CodeScroll.ScrollToTop();
        CodeOpenButton.Visibility = code.IsOpenableUrl ? Visibility.Visible : Visibility.Collapsed;
        CodeOpenButton.ToolTip = code.IsOpenableUrl ? Localization.Loc.F("Codes.OpenInBrowser", code.Text) : null;
        CodePopup.Visibility = Visibility.Visible;
        PositionCodePopup(code.Box);
    }

    /// <returns>Plaque was opened.</returns>
    private bool CloseCodePopup()
    {
        if (CodePopup.Visibility != Visibility.Visible) return false;
        CodePopup.Visibility = Visibility.Collapsed;
        _openCode = null;
        if (_codes.Count > 0) TextOverlay.SetCodes(_codes.Select(c => c.Box).ToList(), null);
        return true;
    }

    private void ClearCodes()
    {
        CloseCodePopup();
        _codes = [];
        _devOpenCodeWhenReady = false;
    }

    /// <summary>Right of code; doesn't fit — left, then below; always fully on code's monitor.</summary>
    private void PositionCodePopup(Rect box)
    {
        CodePopup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = CodePopup.DesiredSize;
        var px = new PixelRect((int)box.X, (int)box.Y, Math.Max(1, (int)box.Width), Math.Max(1, (int)box.Height));
        var dip = _map.PixelToDip(px);
        var mon = _map.PixelToDip(MonitorAt(new PixelPoint(px.X + px.Width / 2, px.Y + px.Height / 2), workArea: true));
        const double gap = 10;
        double x = dip.Right + gap, y = dip.Top;
        if (x + size.Width > mon.Right - gap)
        {
            x = dip.Left - gap - size.Width;
            if (x < mon.Left + gap) { x = dip.Left; y = dip.Bottom + gap; }
        }
        Canvas.SetLeft(CodePopup, ClampInto(x, mon.Left + gap, mon.Right - size.Width - gap));
        Canvas.SetTop(CodePopup, ClampInto(y, mon.Top + gap, mon.Bottom - size.Height - gap));
    }

    private void OnCodeCopy(object sender, RoutedEventArgs e)
    {
        if (_openCode is not { } i || i >= _codes.Count) return;
        // Clipboard write: unfinished OCR must not overwrite it.
        (Application.Current as App)?.NoteClipboardWrite();
        bool ok = ClipboardService.CopyText(_codes[i].Text);
        ShowHint(Localization.Loc.T(ok ? "Codes.Copied" : "Common.ClipboardBusy"), TimeSpan.FromSeconds(1.6));
    }

    /// <summary>Only explicit click and only http/https: scheme rechecked before launch.</summary>
    private void OnCodeOpen(object sender, RoutedEventArgs e)
    {
        if (_openCode is not { } i || i >= _codes.Count) return;
        var url = _codes[i].Text.Trim();
        if (!CodeScanner.IsOpenableUrl(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Screeny.Logging.Log.Error("Opening URL from code failed", ex);
            ShowHint(Localization.Loc.T("Codes.OpenFailed"), TimeSpan.FromSeconds(2));
            return;
        }
        Cancel(); // browser comes to front; overlay over it no longer needed
    }

    /// <summary>Click in text mode on code opens plaque. true — click handled.</summary>
    private bool CodeMouseDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(PixelCanvas);
        if (CodeAt(p) is { } i)
        {
            if (_openCode == i) CloseCodePopup(); else OpenCodePopup(i);
            return true;
        }
        CloseCodePopup();
        return false;
    }
}
