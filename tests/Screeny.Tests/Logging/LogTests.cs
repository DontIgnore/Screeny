using Screeny.Logging;

namespace Screeny.Tests.Logging;

public class LogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScreenyTests", Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(_dir, "screeny.log");

    [Fact]
    public void RotateIfNeeded_LargeFile_MovesToDotOneReplacingOld()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LogPath, new string('x', 2000));
        File.WriteAllText(LogPath + ".1", "old");
        Log.RotateIfNeeded(LogPath, maxBytes: 1000);
        Assert.False(File.Exists(LogPath));
        Assert.Equal(2000, new FileInfo(LogPath + ".1").Length);
    }

    [Fact]
    public void RotateIfNeeded_SmallOrMissingFile_KeepsIt()
    {
        Log.RotateIfNeeded(LogPath, maxBytes: 1000); // нет файла — не бросает
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LogPath, "short");
        Log.RotateIfNeeded(LogPath, maxBytes: 1000);
        Assert.True(File.Exists(LogPath));
        Assert.False(File.Exists(LogPath + ".1"));
    }

    [Fact]
    public void MaxBytes_IsOneMegabyte() => Assert.Equal(1024 * 1024, Log.MaxBytes);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}
