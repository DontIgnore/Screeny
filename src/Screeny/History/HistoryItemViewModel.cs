using System.ComponentModel;
using System.Windows.Media.Imaging;

namespace Screeny.History;

/// <summary>Миниатюра и исходный размер снимка; Image заморожена, null — файл не прочитался.</summary>
public sealed record ThumbnailResult(BitmapSource? Image, int Width, int Height)
{
    public static readonly ThumbnailResult Empty = new(null, 0, 0);
}

/// <summary>
/// Карточка истории: миниатюра (без удержания файла), подпись и временная метка «Скопировано».
/// Миниатюра загружается отдельно (<see cref="LoadThumbnail"/> в фоне, затем <see cref="SetThumbnail"/> в UI-потоке).
/// </summary>
public sealed class HistoryItemViewModel : INotifyPropertyChanged
{
    public const int ThumbnailWidth = 200;
    public const int ThumbnailHeight = 130;

    private string? _status;
    private BitmapSource? _thumbnail;
    private string _sizeText = "";

    public HistoryItemViewModel(HistoryItem item)
    {
        Path = item.Path;
        CreatedAt = item.CreatedAt;
    }

    public string Path { get; }
    public DateTime CreatedAt { get; }
    /// <summary>Дата и время снимка в региональном формате пользователя, с секундами (снимки идут подряд).</summary>
    public string DateText => CreatedAt.ToString("G", System.Globalization.CultureInfo.CurrentCulture);

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set { _thumbnail = value; OnPropertyChanged(nameof(Thumbnail)); }
    }

    public string SizeText
    {
        get => _sizeText;
        private set { _sizeText = value; OnPropertyChanged(nameof(SizeText)); }
    }

    /// <summary>Кнопки действий видны без наведения (тестовый снимок окна истории).</summary>
    public bool ForceActions { get; set; }

    /// <summary>Кнопка и пункт меню «Закрепить на экране»: без функции «Закрепление» их нет.</summary>
    public bool CanPin
    {
        get => _canPin;
        set { _canPin = value; OnPropertyChanged(nameof(CanPin)); }
    }
    private bool _canPin = true;

    public string? Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(nameof(Status)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void SetThumbnail(ThumbnailResult result)
    {
        Thumbnail = result.Image;
        SizeText = result.Width > 0 ? $"{result.Width}×{result.Height}" : "";
    }

    /// <summary>Полноразмерная картинка; OnLoad — чтобы файл не оставался открытым (его можно удалить).</summary>
    public BitmapSource LoadFull()
    {
        using var fs = File.OpenRead(Path);
        return Decode(fs, decodeWidth: 0);
    }

    /// <summary>
    /// Читает файл и декодирует уменьшенную копию. Потокобезопасно (результат заморожен), поэтому вызывается
    /// из фонового потока. Через поток, а не Uri: <c>new Uri(path)</c> ломается на '#' и '%' в пути.
    /// </summary>
    public static ThumbnailResult LoadThumbnail(string path)
    {
        try
        {
            using var ms = new MemoryStream(File.ReadAllBytes(path), writable: false);
            var frame = BitmapFrame.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            int width = frame.PixelWidth, height = frame.PixelHeight;
            ms.Position = 0;
            int decodeWidth = 0, decodeHeight = 0;
            if (width > ThumbnailWidth || height > ThumbnailHeight)
            {
                if ((double)width / ThumbnailWidth >= (double)height / ThumbnailHeight) decodeWidth = ThumbnailWidth;
                else decodeHeight = ThumbnailHeight;
            }
            var image = Decode(ms, decodeWidth, decodeHeight);
            return new ThumbnailResult(image, width, height);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException
                                       or FileFormatException or ArgumentException or InvalidOperationException)
        {
            Logging.Log.Error($"History thumbnail failed: {path}", ex);
            return ThumbnailResult.Empty;
        }
    }

    private static BitmapImage Decode(Stream stream, int decodeWidth, int decodeHeight = 0)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad; // читает поток целиком в EndInit, после него поток можно закрыть
        // IgnoreImageCache не ставим: кэш WPF ключуется по Uri, а с StreamSource этот флаг бросает ArgumentNullException
        if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
        if (decodeHeight > 0) bmp.DecodePixelHeight = decodeHeight;
        bmp.StreamSource = stream;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
