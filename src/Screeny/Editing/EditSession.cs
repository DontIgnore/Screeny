using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Annotations;
using Screeny.Export;
using Screeny.History;
using Screeny.Logging;
using Screeny.Overlay;

namespace Screeny.Editing;

/// <summary>
/// Запись истории, открытая для повторного редактирования.
/// <list type="bullet">
/// <item>Есть <c>.orig.png</c> и читаемый <c>.screeny.json</c> — основа <see cref="Base"/> = оригинал, пометки и оформление из JSON,
/// доступен переключатель «Оригинал / С пометками».</item>
/// <item>Оригинала нет — основа = готовый PNG (старые пометки и оформление уже в картинке), документ пустой.</item>
/// <item>Оригинал есть, но JSON нет, он битый, содержит неизвестные пометки, новее поддерживаемого или другого размера — тоже готовый PNG и
/// <see cref="Message"/>: на оригинале без пометок пропало бы то, что было скрыто размытием.</item>
/// </list>
/// </summary>
/// <param name="Path">Снимок истории, который открыт.</param>
/// <param name="Base">Основа редактирования (Bgra32, 96 DPI, заморожена): источник пикселей эффектов и пипетки.</param>
/// <param name="HasOriginal">Основа — оригинал без пометок (есть переключатель).</param>
/// <param name="OriginalPng">Байты оригинала: прочитаны при открытии, потому что ротация истории после сохранения
/// может удалить исходную запись раньше, чем оригинал скопирован в новую.</param>
/// <param name="Message">Что пошло не так при чтении пометок; null — всё в порядке.</param>
public sealed record EditSession(string Path, BitmapSource Base, bool HasOriginal, byte[]? OriginalPng,
    IReadOnlyList<Annotation> Annotations, BeautifyOptions? Beautify, string? Message)
{
    /// <exception cref="IOException">Сам снимок не читается.</exception>
    /// <exception cref="NotSupportedException">Файл — не картинка.</exception>
    public static EditSession Load(string pngPath)
    {
        var finished = Decode(File.ReadAllBytes(pngPath));
        var (jsonPath, originalPath) = HistoryStore.SidecarPaths(pngPath);
        if (!File.Exists(originalPath)) return Flat(pngPath, finished, null);

        string? problem;
        try
        {
            var originalPng = File.ReadAllBytes(originalPath);
            var original = Decode(originalPng);
            if (!File.Exists(jsonPath)) problem = Localization.Loc.T("Edit.NoDocument");
            else
            {
                var file = AnnotationSerializer.Deserialize(File.ReadAllText(jsonPath));
                if (file.Width != original.PixelWidth || file.Height != original.PixelHeight)
                    problem = Localization.Loc.F("Edit.SizeMismatch", file.Width, file.Height, original.PixelWidth, original.PixelHeight);
                else if (file.Skipped > 0)
                    problem = Localization.Loc.F("Edit.UnknownMarks", file.Skipped);
                else
                    return new EditSession(pngPath, original, true, originalPng, file.Annotations, file.Beautify, null);
            }
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or NotSupportedException
                                       or FileFormatException or ArgumentException or InvalidOperationException)
        {
            Log.Error($"Re-edit sidecar read failed: {pngPath}", ex);
            // Подробности (текст исключения) — в журнале; пользователю — понятная причина на его языке.
            problem = ex switch
            {
                UnsupportedDocumentVersionException newer => Localization.Loc.F("Edit.NewerDocument", newer.Version),
                FormatException => Localization.Loc.T("Edit.DocumentDamaged"),
                _ => Localization.Loc.T("Edit.ReadOriginalFailed"),
            };
        }
        return Flat(pngPath, finished, Localization.Loc.F("Edit.OpenedFlat", problem));
    }

    private static EditSession Flat(string path, BitmapSource finished, string? message)
        => new(path, finished, false, null, [], null, message);

    /// <summary>PNG → независимый Bgra32 96 DPI: эффекты работают только с Bgra32, а DPI файла не должен менять размер на экране.</summary>
    public static BitmapSource Decode(byte[] png)
    {
        using var ms = new MemoryStream(png, writable: false);
        var frame = BitmapFrame.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = source.PixelWidth, h = source.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        source.CopyPixels(px, stride, 0);
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        result.Freeze();
        return result;
    }
}

/// <summary>Итог редактора для действия (копировать, сохранить, закрепить): картинка и sidecar-файлы новой записи истории.</summary>
/// <param name="Image">Основа с пометками и, если включено, оформлением.</param>
/// <param name="AnnotationsJson">Ровно то, что нарисовано на <paramref name="Image"/> (в режиме «Оригинал» — пусто).</param>
/// <param name="OriginalPng">Оригинал для новой записи: только если он был у исходной и включена настройка хранения.</param>
public sealed record EditResult(BitmapSource Image, string AnnotationsJson, byte[]? OriginalPng)
{
    /// <param name="annotations">Видимые пометки; в режиме «Оригинал» — пустой список.</param>
    public static EditResult Build(EditSession session, IReadOnlyList<Annotation> annotations, BeautifyOptions? beautify, bool keepOriginals)
    {
        var b = session.Base;
        var region = new PixelRect(0, 0, b.PixelWidth, b.PixelHeight);
        var area = region.ToRect();
        var visible = annotations.Where(a => a.Bounds.IntersectsWith(area)).ToList();
        var doc = new AnnotationDocument();
        doc.Load(visible);
        var image = ImageComposer.Compose(b, region, doc);
        if (beautify is not null) image = BeautifyComposer.Compose(image, beautify);
        var json = AnnotationSerializer.Serialize(visible, new Point(0, 0), region.Width, region.Height, beautify);
        return new EditResult(image, json, keepOriginals && session.HasOriginal ? session.OriginalPng : null);
    }
}
