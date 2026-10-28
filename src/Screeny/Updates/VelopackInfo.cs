using Screeny.Logging;
using Velopack.Locators;

namespace Screeny.Updates;

/// <summary>Как запущена эта копия: из установщика, из портативной сборки Velopack или просто exe (publish, bin).</summary>
/// <param name="RootDir">Корень Velopack: <c>%LocalAppData%\DontIgnore.Screeny</c> у установленной, папка распаковки у портативной.</param>
/// <param name="StubPath">Стаб <c>&lt;корень&gt;\Screeny.exe</c>: не меняется между обновлениями, на него указывают ярлыки.</param>
public sealed record VelopackInfo(bool IsManaged, bool IsPortable, string? RootDir, string? StubPath)
{
    public static readonly VelopackInfo None = new(false, false, null, null);

    /// <summary>Установлена через Setup.exe (не портативная).</summary>
    public bool IsInstalled => IsManaged && !IsPortable;

    /// <summary>После <c>VelopackApp.Run()</c>. Никогда не бросает.</summary>
    public static VelopackInfo Detect()
    {
        try
        {
            if (!VelopackLocator.IsCurrentSet) return None;
            var locator = VelopackLocator.Current;
            if (locator.CurrentlyInstalledVersion is null || string.IsNullOrEmpty(locator.RootAppDir)) return None;
            return new VelopackInfo(true, locator.IsPortable, locator.RootAppDir, StubFor(locator.RootAppDir, Environment.ProcessPath));
        }
        catch (Exception ex)
        {
            Log.Error("Velopack locator failed", ex);
            return None;
        }
    }

    /// <summary>Стаб в корне с тем же именем, что и exe процесса; null, если его нет.</summary>
    public static string? StubFor(string rootDir, string? processPath)
    {
        if (string.IsNullOrEmpty(processPath)) return null;
        var stub = Path.Combine(rootDir, Path.GetFileName(processPath));
        return File.Exists(stub) ? stub : null;
    }
}
