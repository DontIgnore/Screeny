using System.ComponentModel;
using System.Windows.Media.Imaging;

namespace Screeny.History;

/// <summary>Thumbnail and original screenshot dimensions. Image is frozen; null means the file could not be read.</summary>
public sealed record ThumbnailResult(BitmapSource? Image, int Width, int Height)
{
    public static readonly ThumbnailResult Empty = new(null, 0, 0);
}

/// <summary>
/// A history card with a thumbnail (without holding the file open), caption, and temporary "Copied" status.
/// The thumbnail is loaded separately (<see cref="LoadThumbnail"/> on a background thread, then <see cref="SetThumbnail"/> on the UI thread).
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
    /// <summary>Screenshot date and time in the user's regional format, including seconds for consecutive captures.</summary>
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

    /// <summary>Show action buttons without hovering, for test screenshots of the history window.</summary>
    public bool ForceActions { get; set; }

    /// <summary>Controls whether the Pin to Screen button and menu item are available.</summary>
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

    /// <summary>Loads the full-size image. OnLoad prevents the file from remaining open so it can be deleted.</summary>
    public BitmapSource LoadFull()
    {
        using var fs = File.OpenRead(Path);
        return Decode(fs, decodeWidth: 0);
    }

    /// <summary>
    /// Reads the file and decodes a reduced copy. Thread-safe because the result is frozen; called
    /// on a background thread. Uses a stream because <c>new Uri(path)</c> fails on '#' and '%' in paths.
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
        bmp.CacheOption = BitmapCacheOption.OnLoad; // Reads the entire stream in EndInit so it can be closed afterwards.
        // Do not use IgnoreImageCache: the WPF cache uses Uri keys, and this flag throws ArgumentNullException with StreamSource.
        if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
        if (decodeHeight > 0) bmp.DecodePixelHeight = decodeHeight;
        bmp.StreamSource = stream;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
