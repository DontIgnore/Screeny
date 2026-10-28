using System.Windows;
using Screeny.Export;

namespace Screeny.Overlay;

// Оформление (beautify): кнопка ✨ и клавиша B включают, flyout у кнопки ✨ меняет фон, отступ, скругление и тень.
// Вокруг выделения ничего не рисуется (панель и подпись размера не прыгают): включённое оформление видно по
// подсвеченной кнопке ✨ и значку ✨ у подписи размера. Результат Copy/Save/Pin и история проходят через
// BeautifyComposer. Режим текста (OCR) работает по исходному снимку без оформления.
public partial class OverlayWindow
{
    /// <summary>
    /// Данные для повторного редактирования из истории: документ аннотаций в координатах региона (фигуры целиком
    /// вне региона не попадают — их нет и в картинке) и, если включена настройка, регион без пометок и оформления.
    /// Сбой здесь не должен мешать копированию — только журнал. Без функции «История» ничего не собирается.
    /// </summary>
    private (string? Json, System.Windows.Media.Imaging.BitmapSource? Original) HistorySidecarFor(PixelRect region)
    {
        if (!_settings.IsFeatureOn(Screeny.Features.FeatureCatalog.History)) return (null, null);
        string? json = null;
        System.Windows.Media.Imaging.BitmapSource? original = null;
        try
        {
            var area = region.ToRect();
            var inside = Document.Items.Where(a => a.Bounds.IntersectsWith(area));
            json = Screeny.Annotations.AnnotationSerializer.Serialize(inside, area.TopLeft, region.Width, region.Height, _editor.BeautifyOn ? _editor.Beautify : null);
        }
        catch (Exception ex) { Screeny.Logging.Log.Error("Annotations serialize failed", ex); }
        try { original = OriginalForHistory(_settings.KeepOriginals, _shot.Bitmap, region); }
        catch (Exception ex) { Screeny.Logging.Log.Error("Original region copy failed", ex); }
        return (json, original);
    }

    /// <summary>Регион снимка без пометок и оформления для <c>.orig.png</c>; при выключенной настройке — null (файл не пишется).</summary>
    public static System.Windows.Media.Imaging.BitmapSource? OriginalForHistory(bool keepOriginals, System.Windows.Media.Imaging.BitmapSource shot, PixelRect region)
        => keepOriginals ? ImageComposer.Compose(shot, region, null) : null;

    /// <summary>Итоговая картинка: снимок с аннотациями и, если включено, оформление.</summary>
    private (System.Windows.Media.Imaging.BitmapSource Image, PixelRect ScreenRegion) ComposeResult(PixelRect region)
    {
        var image = ImageComposer.Compose(_shot.Bitmap, region, Document);
        var screen = region.Offset(_shot.VirtualRect.X, _shot.VirtualRect.Y);
        if (!_editor.BeautifyOn) return (image, screen);
        // Закреплённый снимок встаёт так, чтобы сам снимок остался на месте, а оформление было вокруг.
        int p = _editor.Beautify.PaddingPx;
        return (BeautifyComposer.Compose(image, _editor.Beautify), new PixelRect(screen.X - p, screen.Y - p, screen.Width + 2 * p, screen.Height + 2 * p));
    }
}
