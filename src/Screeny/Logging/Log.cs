namespace Screeny.Logging;

public static class Log
{
    /// <summary>Rotation threshold: a larger log is renamed to screeny.log.1, replacing the previous .1 file.</summary>
    public const long MaxBytes = 1024 * 1024;

    private static readonly string PathValue = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screeny", "screeny.log");
    private static readonly object Gate = new();

    /// <summary>Log file shared by production and development instances.</summary>
    public static string FilePath => PathValue;

    public static void Error(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathValue)!);
                try { RotateIfNeeded(PathValue, MaxBytes); }
                catch { /* If renaming fails (the file is in use), append the message anyway. */ }
                File.AppendAllText(PathValue, $"{DateTime.Now:O} {message}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}");
            }
        }
        catch { /* Logging must not throw. */ }
    }

    public static void RotateIfNeeded(string path, long maxBytes)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.Length > maxBytes) File.Move(path, path + ".1", overwrite: true);
    }
}
