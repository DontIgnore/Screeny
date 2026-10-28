using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Screeny.Export;
using Screeny.Ocr;

namespace Screeny.Overlay;

// QR- и штрихкоды в режиме текста (Ctrl+T): ищутся параллельно с OCR, подсвечиваются рамкой,
// клик по коду — плашка с содержимым, «Копировать» и (только для http/https) «Открыть».
// Ссылка никогда не открывается сама — только кнопкой.
public partial class OverlayWindow
{
    private IReadOnlyList<DetectedCode> _codes = [];   // в пикселях снимка
    private int? _openCode;                              // код, показанный в плашке
    private bool _devOpenCodeWhenReady;                  // сценарий qr: открыть плашку, как только коды найдены

    /// <summary>Поиск кодов в той же картинке, что уходит в OCR. Пиксели снимаются здесь, распознавание — вне UI-потока.</summary>
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

    /// <summary>Коды найдены (рамки уже в пикселях снимка): подсветить, при сценарии qr — открыть плашку.</summary>
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

    /// <returns>Плашка была открыта.</returns>
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

    /// <summary>Справа от кода; не влезает — слева, затем под ним; всегда целиком на мониторе кода.</summary>
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
        // Запись в буфер: незавершённый OCR не должен её перезаписать.
        (Application.Current as App)?.NoteClipboardWrite();
        bool ok = ClipboardService.CopyText(_codes[i].Text);
        ShowHint(Localization.Loc.T(ok ? "Codes.Copied" : "Common.ClipboardBusy"), TimeSpan.FromSeconds(1.6));
    }

    /// <summary>Только по явному клику и только http/https: схема проверяется ещё раз перед запуском.</summary>
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
        Cancel(); // браузер выходит на передний план; оверлей поверх него больше не нужен
    }

    /// <summary>Клик в режиме текста по коду открывает плашку. true — клик обработан.</summary>
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
