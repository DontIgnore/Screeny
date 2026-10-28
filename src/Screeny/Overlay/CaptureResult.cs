using System.Windows.Media.Imaging;

namespace Screeny.Overlay;

/// <summary>
/// Copy — Enter: копирует, только если включена настройка CopyOnCapture.
/// CopyExplicit — Ctrl+C и кнопка «Копировать»: копирует всегда.
/// Save — путь уже выбран в диалоге оверлея (<see cref="CaptureResult.SavePath"/>).
/// CopyText — в режиме текста (Ctrl+T) выбран текст; он в <see cref="CaptureResult.Text"/>.
/// </summary>
public enum CaptureAction { Copy, CopyExplicit, Save, Pin, CopyText }

/// <param name="Region">Выделение в пикселях снимка.</param>
/// <param name="ScreenRegion">
/// То же выделение в экранных пикселях (с учётом начала виртуального экрана); с оформлением — расширено
/// на отступ, чтобы закреплённый снимок встал вокруг прежнего места. Размер совпадает с <paramref name="Image"/>.
/// </param>
/// <param name="SavePath">Для <see cref="CaptureAction.Save"/> — файл, выбранный пользователем.</param>
/// <param name="Text">Для <see cref="CaptureAction.CopyText"/> — распознанный и выбранный текст.</param>
/// <param name="AnnotationsJson">Документ аннотаций в координатах региона (<see cref="Annotations.AnnotationSerializer"/>) — sidecar истории.</param>
/// <param name="Original">Регион без пометок и оформления — только при включённой настройке «Хранить оригиналы».</param>
public sealed record CaptureResult(BitmapSource Image, CaptureAction Action, PixelRect Region, PixelRect ScreenRegion, string? SavePath = null, string? Text = null,
    string? AnnotationsJson = null, BitmapSource? Original = null);
