using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Export;
using Screeny.Features;
using Screeny.Ocr;
using Screeny.Overlay.Tools;

namespace Screeny.Overlay;

// Предупреждение о личных данных: когда выделение устоялось (отпустили мышь, стрелки перестали двигать рамку),
// выделенная область один раз распознаётся в фоне (Windows OCR) и проверяется PersonalDataDetector. Нашлось — рядом
// с панелью плашка «Здесь могут быть личные данные» и «Скрыть»: пикселизация всего найденного одним шагом undo.
// Ничего не скрывается само. Плашка уходит по ✕, при смене выделения и после «Скрыть». Новое выделение, кадр серии,
// закрытие оверлея отменяют идущую проверку (результат отбрасывается по номеру поколения).
public partial class OverlayWindow
{
    /// <summary>Пауза после последнего изменения выделения перед проверкой: стрелки и повторные клики не запускают OCR на каждый шаг.</summary>
    private static readonly TimeSpan PrivacyDelay = TimeSpan.FromMilliseconds(350);
    /// <summary>Запас вокруг рамки найденного слова при скрытии, пиксели снимка.</summary>
    private const int PrivacyPadding = 3;

    private enum PrivacyState { Idle, Scheduled, Running, Done }

    private PrivacyState _privacyState;
    private PixelRect _privacyRect;                   // выделение, к которому относятся проверка и плашка
    private int _privacyGeneration;                   // каждая отмена; устаревший результат отбрасывается
    private DispatcherTimer? _privacyTimer;
    private IReadOnlyList<Rect> _privacyBoxes = [];   // найденное, в пикселях снимка; пусто — плашки нет

    /// <summary>
    /// Вызывается из <see cref="RefreshSelectionVisuals"/>. Рамка изменилась — прежняя проверка отменяется, плашка
    /// прячется. Рамка устоялась (режим рисования, нет жеста, не режим текста) и ещё не проверена — проверка через
    /// <see cref="PrivacyDelay"/>.
    /// </summary>
    private void UpdatePrivacyCheck()
    {
        var rect = _selection.HasSelection ? _selection.Rect : default;
        if (rect != _privacyRect)
        {
            CancelPrivacyCheck();
            _privacyRect = rect;
        }
        bool settled = _mode == OverlayMode.Editing && _selection.HasSelection && !HasGesture && !_textMode && !_closing;
        if (settled && _privacyState == PrivacyState.Idle && _settings.PrivacyCheckAllowed())
        {
            _privacyState = PrivacyState.Scheduled;
            _privacyTimer ??= CreatePrivacyTimer();
            _privacyTimer.Stop();
            _privacyTimer.Start();
        }
        PositionPrivacyChip();
    }

    private DispatcherTimer CreatePrivacyTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PrivacyDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_privacyState == PrivacyState.Scheduled && !_closing) _ = RunPrivacyCheckAsync();
        };
        return timer;
    }

    /// <summary>Отменить проверку и спрятать плашку; следующее устоявшееся выделение проверяется заново.</summary>
    private void CancelPrivacyCheck()
    {
        _privacyTimer?.Stop();
        _privacyGeneration++;
        _privacyState = PrivacyState.Idle;
        _privacyBoxes = [];
        PrivacyChip.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Распознаёт то, что уйдёт в копию (с уже нарисованным: скрытое пикселизацией не находится второй раз), целиком вне
    /// UI-потока: первый проход OCR копирует пиксели и готовит движки синхронно. Ошибка OCR — только журнал.
    /// </summary>
    private async Task RunPrivacyCheckAsync()
    {
        int generation = _privacyGeneration;
        var region = _privacyRect;
        _privacyState = PrivacyState.Running;
        IReadOnlyList<PersonalDataMatch> matches = [];
        try
        {
            var image = ImageComposer.Compose(_shot.Bitmap, region, Document);
            var layout = await Task.Run(() => OcrService.RecognizeLayoutAsync(image));
            if (layout is not null) matches = PersonalDataDetector.Detect(layout);
            // Только тестовый экземпляр и только синтетические картинки: распознанный текст — в журнал для проверки.
            if (_dev is not null && layout is not null) Screeny.Logging.Log.Error($"Dev privacy OCR: {layout.TextOf(null).ReplaceLineEndings(" | ")}");
        }
        catch (Exception ex) { Screeny.Logging.Log.Error("Personal data check failed", ex); }
        if (generation != _privacyGeneration || _closing || region != _privacyRect) return;
        _privacyState = PrivacyState.Done;
        if (!_settings.PrivacyCheckAllowed()) return;
        // В журнал — только виды найденного, не сам текст.
        if (matches.Count > 0)
            Screeny.Logging.Log.Error($"Personal data check: {matches.Count} match(es): {string.Join(", ", matches.Select(m => m.Kind))}");
        var bounds = region.ToRect();
        _privacyBoxes = matches.Select(m =>
        {
            var box = Rect.Offset(m.Box, new Vector(region.X, region.Y));
            box.Inflate(PrivacyPadding, PrivacyPadding);
            box.Intersect(bounds);
            return box;
        }).Where(b => !b.IsEmpty).ToList();
        PositionPrivacyChip();
        if (_dev is not null) DevPrivacyChecked(matches);
    }

    /// <summary>«Скрыть»: пикселизация каждой найденной рамки стилем инструмента «Пикселизация», одним шагом undo.</summary>
    private void OnPrivacyHide(object sender, RoutedEventArgs e) => HidePersonalData();

    private void HidePersonalData()
    {
        if (_privacyBoxes.Count == 0 || _closing || _textMode) return;
        CommitTextEdit();
        ResetInteraction();
        var style = _editor.StyleForTool(ToolKind.Pixelate);
        Document.AddAll(_privacyBoxes.Select(b => (Annotation)new PixelateAnnotation(b, style)).ToList());
        _privacyBoxes = [];
        PositionPrivacyChip();
        Focus();
    }

    private void OnPrivacyDismiss(object sender, RoutedEventArgs e)
    {
        _privacyBoxes = [];
        PositionPrivacyChip();
        Focus();
    }

    /// <summary>
    /// Плашка видна, когда есть что скрыть и видна панель (не режим текста). Справа от панели, если влезает в рабочую
    /// область монитора выделения; иначе с внешней стороны панели (под нижней, над верхней).
    /// </summary>
    private void PositionPrivacyChip()
    {
        bool show = _privacyBoxes.Count > 0 && !_textMode && !_closing && Toolbar.Visibility == Visibility.Visible;
        PrivacyChip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        PrivacyChip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var chip = PrivacyChip.DesiredSize;
        var bar = new Rect(Canvas.GetLeft(Toolbar), Canvas.GetTop(Toolbar), Toolbar.DesiredSize.Width, Toolbar.DesiredSize.Height);
        if (double.IsNaN(bar.X) || double.IsNaN(bar.Y)) return;
        var work = SelectionWorkArea();
        const double gap = 8;
        double x = bar.Right + gap, y = bar.Top + (bar.Height - chip.Height) / 2;
        if (x + chip.Width > work.Right - gap)
        {
            x = bar.Left;
            y = _toolbarBelow ? bar.Bottom + gap : bar.Top - gap - chip.Height;
        }
        Canvas.SetLeft(PrivacyChip, ClampInto(x, work.Left + gap, work.Right - chip.Width - gap));
        Canvas.SetTop(PrivacyChip, ClampInto(y, work.Top + gap, work.Bottom - chip.Height - gap));
    }
}
