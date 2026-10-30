using System.Windows;
using System.Windows.Media;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Export;
using Screeny.Overlay.Tools;

namespace Screeny.Overlay;

// Сценарии тестового экземпляра (--dev --overlay-test x.png --scenario имя): готовые фигуры и состояние параметров,
// чтобы проверить UI по скриншоту окна без синтетического ввода. Без --dev сюда не попасть (StartupArgs).
public partial class OverlayWindow
{
    /// <summary>Имена сценариев для <c>--scenario</c>.</summary>
    public static readonly string[] DevScenarios = ["shape", "shape-tool", "arrow", "text", "ruler", "badge", "qr", "beautify", "beautify-sunset", "beautify-light",
        "guides", "textalign", "edgesnap", "flyout-beautify", "flyout-beautify-mid", "flyout-tool", "flyout-shape", "pen-bounds",
        "ocr", "ocr-select", "readme-hero", "readme-text", "readme-beautify", "privacy", "distances", "burst"];

    /// <summary>Сценарий privacy: дождаться проверки на личные данные, снять кадры до и после «Скрыть» и закрыться.</summary>
    private bool _devPrivacy;

    /// <summary>
    /// Кадр самого окна оверлея (RenderTargetBitmap корня, не снимок экрана) рядом с исходником:
    /// <c>&lt;имя&gt;.&lt;tag&gt;.png</c>, для папки кадров серии — <c>&lt;папка&gt;.&lt;tag&gt;.png</c> рядом с ней (не в ней: иначе
    /// следующий запуск взял бы кадр окна за кадр серии).
    /// </summary>
    private void DevRender(string tag)
    {
        if (_dev?.Source is not { } source) return;
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(Root.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(Root.ActualHeight * dpi.DpiScaleY);
        if (w <= 0 || h <= 0) return;
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(Root);
        rtb.Freeze();
        var path = Directory.Exists(source) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)) + $".{tag}.png" : Path.ChangeExtension(source, $".{tag}.png");
        FileSaver.Save(rtb, path);
        Screeny.Logging.Log.Error($"Dev render saved to {path}");
    }

    /// <summary>
    /// Проверка на личные данные закончилась (dev). Сценарий privacy: кадр с плашкой, «Скрыть», кадр после, повторная
    /// проверка уже пикселизированной области (в журнал — сколько нашлось теперь), проверка, что Ctrl+Z убирает всё
    /// одним шагом, и закрытие оверлея.
    /// </summary>
    private async void DevPrivacyChecked(IReadOnlyList<Screeny.Ocr.PersonalDataMatch> matches)
    {
        if (!_devPrivacy) return;
        _devPrivacy = false;
        await Dispatcher.InvokeAsync(() => DevRender("privacy"), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        int before = Document.Items.Count;
        HidePersonalData();
        int added = Document.Items.Count - before;
        await Dispatcher.InvokeAsync(() => DevRender("privacy-hidden"), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var image = ImageComposer.Compose(_shot.Bitmap, _selection.Rect, Document);
        var layout = await Task.Run(() => Screeny.Ocr.OcrService.RecognizeLayoutAsync(image));
        int after = layout is null ? -1 : Screeny.Ocr.PersonalDataDetector.Detect(layout).Count;
        Document.Undo.Undo();
        int afterUndo = Document.Items.Count;
        Screeny.Logging.Log.Error($"Dev privacy: {matches.Count} found, Hide added {added} pixelate shape(s), {after} found after Hide, {afterUndo} shape(s) after one Ctrl+Z");
        Cancel();
    }

    /// <summary>
    /// Сценарии ocr-select и readme-text: как только показана первая раскладка (Windows OCR), выделить слова в этой части
    /// выделения (доли ширины и высоты; ocr-select — левая треть верхней половины).
    /// </summary>
    private Rect? _devSelectWhenReady;

    /// <summary>
    /// Раскладка режима текста показана (dev). Для ocr-select — выделение рамкой, как мышью: уточнение,
    /// пришедшее позже, переносит его на свои слова по месту (<see cref="Screeny.Ocr.LayoutRemap"/>). В журнал — слова
    /// раскладки, чтобы по нему было видно, какая раскладка на экране.
    /// </summary>
    private void DevTextLayoutShown()
    {
        if (_ocr is null) return;
        Screeny.Logging.Log.Error($"Dev text mode: layout shown, {_ocr.WordCount} words: {_ocr.TextOf(null).ReplaceLineEndings(" | ")}");
        if (_devSelectWhenReady is not { } f || _ocr.IsEmpty) return;
        _devSelectWhenReady = null;
        var r = _selection.Rect;
        _textSelection = _ocr.InRect(new Rect(r.X + r.Width * f.X, r.Y + r.Height * f.Y, r.Width * f.Width, r.Height * f.Height));
        _textAnchor = _textSelection.Count > 0 ? _textSelection.Min() : null;
        ShowTextSelection();
    }

    private void ApplyDevScenario(string name)
    {
        var r = _selection.HasSelection ? _selection.Rect : _shot.PixelBounds;
        Point At(double fx, double fy) => new(r.X + r.Width * fx, r.Y + r.Height * fy);
        Rect Box(double fx, double fy, double fw, double fh) => new(At(fx, fy), new Size(r.Width * fw, r.Height * fh));
        var red = ToolbarControl.Palette[0];
        var blue = ToolbarControl.Palette[4];
        var green = ToolbarControl.Palette[3];

        switch (name)
        {
            case "shape":
            {
                // Залитый прямоугольник выбран, активный образец — заливка.
                var filled = new RectAnnotation(Box(0.05, 0.08, 0.35, 0.35), new AnnotationStyle(red, 4) { Fill = ColorHex.WithOpacity(blue, 40) });
                Document.Add(filled);
                Document.Add(new EllipseAnnotation(Box(0.5, 0.08, 0.4, 0.35), new AnnotationStyle(red, 3) { StrokeEnabled = false, Fill = ColorHex.WithOpacity(green, 60) }));
                Document.Add(new RectAnnotation(Box(0.05, 0.55, 0.4, 0.3), new AnnotationStyle(red, 3)));
                Document.Add(new EllipseAnnotation(Box(0.5, 0.55, 0.4, 0.3), new AnnotationStyle(blue, 5) { Fill = ColorHex.WithOpacity(red, 80) }));
                _editor.Options.ActiveTarget = PaintTarget.Fill;
                Document.Selected = filled;
                Document.NotifyChanged();
                break;
            }
            case "shape-tool":
                // Будущий прямоугольник: обводка + заливка из настроек сессии, ничего не выбрано.
                _editor.Options.Absorb(new AnnotationStyle(red, 3) { Fill = ColorHex.WithOpacity(red, 40) }, OptionsSectionKind.Shape);
                SetTool(ToolKind.Rect);
                break;
            case "arrow":
            {
                ArrowAnnotation Arrow(double y, ArrowStyle style, Color c) => new(At(0.08, y), At(0.85, y + 0.08), new AnnotationStyle(c, 4) { Arrow = style });
                Document.Add(Arrow(0.08, ArrowStyle.Normal, red));
                Document.Add(Arrow(0.3, ArrowStyle.Double, blue));
                Document.Add(Arrow(0.52, ArrowStyle.Dashed, green));
                var thick = Arrow(0.74, ArrowStyle.Thick, red);
                Document.Add(thick);
                Document.Selected = thick;
                Document.NotifyChanged();
                break;
            }
            case "text":
            {
                Document.Add(new TextAnnotation(At(0.06, 0.1), "Plain text with shadow", new AnnotationStyle(red, 4)));
                var withBg = new TextAnnotation(At(0.06, 0.4), "Text on a rounded background", new AnnotationStyle(Colors.White, 4) { TextBackground = StyleOptions.DefaultTextBackground });
                Document.Add(withBg);
                Document.Add(new TextAnnotation(At(0.06, 0.7), "Yellow background", new AnnotationStyle(Colors.Black, 3) { TextBackground = ToolbarControl.Palette[2] }));
                _editor.Options.ActiveTarget = PaintTarget.Background;
                Document.Selected = withBg;
                Document.NotifyChanged();
                break;
            }
            case "ruler":
                if (!Screeny.Features.FeatureGate.ToolAllowed(_settings, ToolKind.Ruler)) break; // линейка выключена
                SetTool(ToolKind.Ruler);
                _editor.Tool(ToolKind.Ruler).Down(At(0.1, 0.2), Document, _editor.CurrentStyle);
                _editor.Tool(ToolKind.Ruler).Up(At(0.75, 0.7));
                break;
            case "badge":
            {
                // Состояние после C при выделении: лупа и бейдж (без записи в буфер обмена).
                var p = new PixelPoint((int)At(0.2, 0.3).X, (int)At(0.2, 0.3).Y);
                UpdateMagnifier(p);
                var c = Pipette.ColorAt(_shot.Bitmap, p);
                ShowPipetteBadge(c, Localization.Loc.F("Pipette.Copied", ColorHex.Rgb(c)), TimeSpan.FromMinutes(1));
                break;
            }
            case "beautify":
                // Оформление по умолчанию (синий, M, 8, тень) с выбранным инструментом «Выбор».
                _editor.SetBeautifyOptions(BeautifyOptions.Default);
                _editor.SetBeautify(true);
                break;
            case "beautify-sunset":
                // Закат, L, 16, тень + секция прямоугольника слева от секции оформления.
                _editor.SetBeautifyOptions(new BeautifyOptions(BeautifyPreset.Sunset, BeautifyPadding.L, 16, true));
                _editor.SetBeautify(true);
                SetTool(ToolKind.Rect);
                break;
            case "beautify-light":
                // Светлый, S, без скругления и тени, с готовой стрелкой.
                Document.Add(new ArrowAnnotation(At(0.15, 0.2), At(0.6, 0.6), new AnnotationStyle(red, 4)));
                _editor.SetBeautifyOptions(new BeautifyOptions(BeautifyPreset.Light, BeautifyPadding.S, 0, false));
                _editor.SetBeautify(true);
                break;
            case "guides":
            {
                // Перетаскивание инструментом «Выбор» посреди жеста: правый прямоугольник почти выровнен по верху и
                // центру левого — видны направляющие (жест не завершён, мышь не захвачена: только для скриншота).
                Document.Add(new RectAnnotation(Box(0.08, 0.2, 0.3, 0.3), new AnnotationStyle(red, 3)));
                var moving = new RectAnnotation(Box(0.5, 0.6, 0.3, 0.25), new AnnotationStyle(blue, 3));
                Document.Add(moving);
                SetTool(ToolKind.Select);
                var grip = new Point(moving.Rect.Left, moving.Rect.Top + moving.Rect.Height / 2);
                if (_editor.BeginGesture(grip))
                    _editor.MoveGesture(grip + new Vector(r.Width * 0.02, -(moving.Rect.Top - At(0, 0.2).Y) + 3));
                break;
            }
            case "textalign":
            {
                // Многострочный текст в трёх выравниваниях; выбран средний — в его параметрах (рядом с ним) нажата кнопка «по центру».
                const string text = "Short line\nA much longer second line\nMid";
                Document.Add(new TextAnnotation(At(0.04, 0.1), text, new AnnotationStyle(red, 4) { TextAlign = TextAlign.Left }));
                var center = new TextAnnotation(At(0.36, 0.1), text, new AnnotationStyle(Colors.White, 4) { TextAlign = TextAlign.Center, TextBackground = StyleOptions.DefaultTextBackground });
                Document.Add(center);
                Document.Add(new TextAnnotation(At(0.68, 0.1), text, new AnnotationStyle(Colors.Black, 4) { TextAlign = TextAlign.Right, TextBackground = ToolbarControl.Palette[2] }));
                Document.Selected = center;
                Document.NotifyChanged();
                break;
            }
            case "edgesnap":
            {
                // Протяжка новой рамки посреди жеста (как мышью): правый и нижний край в нескольких пикселях от
                // контрастных границ снимка — прилипают, прилипшие края видны направляющими через весь снимок.
                _selection.Rect = default;
                _mode = OverlayMode.Selecting;
                Toolbar.Visibility = Visibility.Collapsed;
                var a = new PixelPoint((int)At(0.1, 0.1).X, (int)At(0.1, 0.1).Y);
                var b = new PixelPoint((int)At(0.5, 0.5).X, (int)At(0.5, 0.5).Y);
                _dragAnchor = a;
                _thresholdPassed = true;
                BeginFrameGesture();
                Dim.Hint = null;
                DragNewFrame(a, b);
                RefreshSelectionVisuals();
                UpdateMagnifier(b);
                break;
            }
            case "flyout-beautify":
                // Оформление включено: вокруг выделения ничего нет, его параметры — во flyout'е у кнопки ✨ (как после наведения).
                _editor.SetBeautify(true);
                Dispatcher.BeginInvoke(() => _editor.Flyouts.DevOpenBeautify(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                break;
            case "flyout-beautify-mid":
                // Тот же flyout, остановленный на 80-й миллисекунде анимации открытия: вылет из кнопки, строки проявляются.
                _editor.SetBeautify(true);
                Dispatcher.BeginInvoke(() => _editor.Flyouts.DevFreezeBeautifyAt(80), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                break;
            case "flyout-tool":
                // Активный «Прямоугольник»: обводка и заливка во flyout'е у его кнопки (как после наведения).
                _editor.Options.Absorb(new AnnotationStyle(red, 3) { Fill = ColorHex.WithOpacity(red, 40) }, OptionsSectionKind.Shape);
                SetTool(ToolKind.Rect);
                Dispatcher.BeginInvoke(() => _editor.Flyouts.DevOpenTool(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                break;
            case "flyout-shape":
            {
                // Инструментом «Выбор» выбрана стрелка: её стили во flyout'е над ней; рядом залитый прямоугольник.
                Document.Add(new RectAnnotation(Box(0.08, 0.15, 0.3, 0.3), new AnnotationStyle(red, 4) { Fill = ColorHex.WithOpacity(blue, 40) }));
                var arrow = new ArrowAnnotation(At(0.45, 0.55), At(0.85, 0.35), new AnnotationStyle(blue, 6) { Arrow = ArrowStyle.Thick });
                Document.Add(arrow);
                SetTool(ToolKind.Select);
                Document.Selected = arrow;
                Document.NotifyChanged();
                break;
            }
            case "pen-bounds":
            {
                // Карандаш толщиной 20 с резкими изгибами выбран: пунктирная рамка охватывает весь штрих.
                var pen = new PenAnnotation(At(0.1, 0.25), new AnnotationStyle(red, 20));
                foreach (var (fx, fy) in new[] { (0.3, 0.1), (0.2, 0.6), (0.45, 0.3), (0.4, 0.85), (0.6, 0.2), (0.55, 0.7), (0.7, 0.5), (0.65, 0.9) })
                    pen.AddPoint(At(fx, fy));
                Document.Add(pen);
                Document.Add(new LineAnnotation(At(0.75, 0.15), At(0.95, 0.4), new AnnotationStyle(blue, 20)));
                Document.Add(new RectAnnotation(Box(0.75, 0.55, 0.2, 0.3), new AnnotationStyle(green, 20)));
                SetTool(ToolKind.Select);
                Document.Selected = pen;
                Document.NotifyChanged();
                break;
            }
            case "ocr":
                // Режим текста по выделению: сначала Windows OCR, затем (если модуль установлен и включён) уточнение.
                ToggleTextMode();
                break;
            case "ocr-select":
                // То же, но с выделением части слов до уточнения: видно, как выделение переносится на новые слова.
                _devSelectWhenReady = new Rect(0, 0, 1 / 3.0, 0.5);
                ToggleTextMode();
                break;
            // Сценарии readme-*: кадры для README по синтетическому рабочему столу; доли подобраны под его разметку
            // (--select из docs/maintainers.md).
            case "readme-hero":
            {
                // Подпись и стрелка к самому высокому столбцу графика, рамка вокруг первой метрики; ничего не выбрано.
                Document.Add(new RectAnnotation(Box(0.029, 0.098, 0.128, 0.117), new AnnotationStyle(red, 3)));
                Document.Add(new TextAnnotation(At(0.105, 0.37), "Best week so far", new AnnotationStyle(Colors.White, 4) { TextBackground = red }));
                Document.Add(new ArrowAnnotation(At(0.335, 0.405), At(0.762, 0.425), new AnnotationStyle(red, 5)));
                SetTool(ToolKind.Arrow);
                break;
            }
            case "readme-text":
                // Режим текста: выделены строки русской заметки, открыта плашка QR-кода.
                _devSelectWhenReady = new Rect(0.02, 0.43, 0.96, 0.17);
                _devOpenCodeWhenReady = true;
                ToggleTextMode();
                break;
            case "readme-beautify":
            {
                // Итоговая картинка с оформлением (как при копировании) пишется рядом с исходником: <имя>.export.png.
                Document.Add(new TextAnnotation(At(0.11, 0.17), "Best week so far", new AnnotationStyle(Colors.White, 4) { TextBackground = red }));
                Document.Add(new ArrowAnnotation(At(0.355, 0.22), At(0.765, 0.25), new AnnotationStyle(red, 5)));
                _editor.SetBeautifyOptions(new BeautifyOptions(BeautifyPreset.Blue, BeautifyPadding.L, 16, true));
                _editor.SetBeautify(true);
                if (_dev?.Source is { } source && _selection.HasSelection)
                {
                    var path = Path.ChangeExtension(source, ".export.png");
                    FileSaver.Save(ComposeResult(_selection.Rect).Image, path);
                    Screeny.Logging.Log.Error($"Dev export saved to {path}");
                }
                break;
            }
            case "distances":
            {
                // Линейка и курсор над точкой (для синтетического рабочего стола с --select 120,80,1040,560 — над синей
                // кнопкой): линии до её краёв. Кадр окна — <имя>.distances.png, затем оверлей закрывается.
                if (!Screeny.Features.FeatureGate.ToolAllowed(_settings, ToolKind.Ruler)) break;
                SetTool(ToolKind.Ruler);
                var p = new PixelPoint((int)At(0.106, 0.886).X, (int)At(0.106, 0.886).Y);
                Dispatcher.BeginInvoke(() =>
                {
                    UpdateDistances(p);
                    if (RulerOverlay.Distances is { } d)
                        Screeny.Logging.Log.Error($"Dev distances at {p.X},{p.Y}: left {d.ToLeft}, right {d.ToRight}, up {d.ToTop}, down {d.ToBottom} px");
                    DevRender("distances");
                    Cancel();
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                break;
            }
            case "burst":
                // С --burst-test: кадр окна на последнем кадре серии, переход к первому (как PageUp до упора), проверка, что
                // снимок, источник редактора и кэши яркости сменились, кадр окна и итоговая картинка выделения, закрытие.
                Dispatcher.BeginInvoke(() =>
                {
                    if (!HasBurst) { Screeny.Logging.Log.Error("Dev burst: no burst frames (start with --burst-test <folder>)"); Cancel(); return; }
                    DevRender("burst");
                    int was = _frameIndex;
                    _ = ShotLuma; // яркость прежнего кадра загружена: смена кадра обязана её сбросить
                    ShowFrame(0);
                    var bitmap = _frames[0].Bitmap;
                    bool swapped = ReferenceEquals(_shot.Bitmap, bitmap) && ReferenceEquals(_editor.Source, bitmap) && ReferenceEquals(ShotImage.Source, bitmap)
                                   && ReferenceEquals(Annotations.Source, bitmap) && _shotLuma is null && _edgeSnapper is null && _distanceEdges is null;
                    Screeny.Logging.Log.Error($"Dev burst: frame {was + 1} -> {_frameIndex + 1} of {_frames.Count}, caches reset: {swapped}");
                    DevRender("burst-first");
                    if (_dev?.Source is { } folder && _selection.HasSelection)
                    {
                        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + ".burst-export.png";
                        FileSaver.Save(ComposeResult(_selection.Rect).Image, path);
                        Screeny.Logging.Log.Error($"Dev export saved to {path}");
                    }
                    Cancel();
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                break;
            case "privacy":
                // Проверка запускается сама, как только выделение устоялось (--select); дальше — DevPrivacyChecked.
                _devPrivacy = true;
                break;
            case "qr":
                // Режим текста по выделению; как только коды найдены, плашка открывается на первой ссылке
                // (или первом коде). Ни «Открыть», ни «Копировать» сценарий не нажимает.
                _devOpenCodeWhenReady = true;
                ToggleTextMode();
                break;
            default:
                Screeny.Logging.Log.Error($"Unknown dev scenario '{name}'. Known: {string.Join(", ", DevScenarios)}");
                break;
        }
    }
}
