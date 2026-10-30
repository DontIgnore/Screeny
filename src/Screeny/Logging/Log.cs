namespace Screeny.Logging;

public static class Log
{
    /// <summary>Порог ротации: больший журнал переименовывается в screeny.log.1 (старый .1 перезаписывается).</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>Переменная окружения с другим путём журнала: тесты пишут во временную папку, а не в настоящий
    /// screeny.log (его хвост уходит в сообщения об ошибках).</summary>
    public const string PathVariable = "SCREENY_LOG_FILE";

    private static readonly string PathValue = Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screeny", "screeny.log");
    private static readonly object Gate = new();

    /// <summary>Файл журнала (общий для рабочего и тестового экземпляров).</summary>
    public static string FilePath => PathValue;

    public static void Error(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathValue)!);
                try { RotateIfNeeded(PathValue, MaxBytes); }
                catch { /* не смогли переименовать (файл занят) — всё равно дописываем сообщение */ }
                File.AppendAllText(PathValue, $"{DateTime.Now:O} {message}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}");
            }
        }
        catch { /* логирование не должно падать */ }
    }

    public static void RotateIfNeeded(string path, long maxBytes)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.Length > maxBytes) File.Move(path, path + ".1", overwrite: true);
    }
}
