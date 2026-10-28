using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using Microsoft.Win32;
using Screeny.Themes;

namespace Screeny.Export;

public enum ImageFileFormat { Png, Jpeg }

public static class FileSaver
{
    /// <summary>Порядок фильтров совпадает с <see cref="FromFilterIndex"/>.</summary>
    private const string Filter = "PNG|*.png|JPEG|*.jpg";

    /// <summary>Диалог «Сохранить как» (PNG/JPEG). Возвращает итоговый путь или null, если пользователь отменил.</summary>
    public static string? SaveWithDialog(BitmapSource image, Window? owner = null)
    {
        var path = ChoosePath(owner);
        if (path is null) return null;
        Save(image, path);
        return path;
    }

    public static string? ChoosePath(Window? owner = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = Localization.Loc.T("Common.SaveScreenshotTitle"),
            Filter = Filter,
            FileName = $"Screeny_{DateTime.Now:yyyyMMdd_HHmmss}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            AddExtension = true,
        };
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true) return null;
        return ConfirmResolvedPath(owner, dialog.FilterIndex, dialog.FileName);
    }

    /// <summary>Формат по 1-based индексу фильтра диалога (1 — PNG, 2 — JPEG).</summary>
    public static ImageFileFormat FromFilterIndex(int filterIndex) => filterIndex == 2 ? ImageFileFormat.Jpeg : ImageFileFormat.Png;

    /// <summary>
    /// Формат определяется выбранным фильтром, а не расширением. Если расширение не соответствует формату
    /// («shot.bmp» при PNG), дописывается правильное: «shot.bmp.png».
    /// </summary>
    public static (ImageFileFormat Format, string Path) Resolve(int filterIndex, string fileName)
    {
        var format = FromFilterIndex(filterIndex);
        return (format, HasExtensionFor(fileName, format) ? fileName : fileName + DefaultExtension(format));
    }

    /// <summary>Если приложение изменило имя после системного диалога, отдельно подтверждает замену итогового файла.</summary>
    public static string? ResolveForSave(int filterIndex, string fileName, Func<string, bool> confirmOverwrite)
    {
        var path = Resolve(filterIndex, fileName).Path;
        if (!string.Equals(path, fileName, StringComparison.OrdinalIgnoreCase) && File.Exists(path) && !confirmOverwrite(path)) return null;
        return path;
    }

    public static string? ConfirmResolvedPath(Window? owner, int filterIndex, string fileName)
        => ResolveForSave(filterIndex, fileName,
            path => Dialogs.Confirm(owner, Localization.Loc.T("File.ReplaceTitle"), Localization.Loc.F("File.ReplaceText", path), Localization.Loc.T("File.Replace"), danger: true));

    private static bool HasExtensionFor(string path, ImageFileFormat format)
        => Path.GetExtension(path).ToLowerInvariant() is var ext && format switch
        {
            ImageFileFormat.Jpeg => ext is ".jpg" or ".jpeg",
            _ => ext is ".png",
        };

    private static string DefaultExtension(ImageFileFormat format) => format == ImageFileFormat.Jpeg ? ".jpg" : ".png";

    /// <summary>Формат по расширению (.jpg/.jpeg — JPEG, остальное — PNG).</summary>
    public static void Save(BitmapSource image, string path)
        => Save(image, path, HasExtensionFor(path, ImageFileFormat.Jpeg) ? ImageFileFormat.Jpeg : ImageFileFormat.Png);

    /// <summary>
    /// Пишет во временный файл рядом и заменяет целевой только после успешной записи:
    /// при ошибке существующий файл остаётся нетронутым.
    /// </summary>
    public static void Save(BitmapSource image, string path, ImageFileFormat format)
    {
        BitmapEncoder encoder = format == ImageFileFormat.Jpeg ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
        // JPEG не умеет альфу — сводим на непрозрачный формат.
        var frame = format == ImageFileFormat.Jpeg ? new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0) : image;
        encoder.Frames.Add(BitmapFrame.Create(frame));

        var full = Path.GetFullPath(path);
        var tmp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                encoder.Save(fs);
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* не мешаем исходной ошибке */ }
            throw;
        }
    }
}
