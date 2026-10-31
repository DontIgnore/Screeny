using Screeny.Logging;
using Velopack.Locators;

namespace Screeny.Updates;

/// <summary>How this copy runs: from installer, Velopack portable build or plain exe (publish, bin).</summary>
/// <param name="RootDir">Velopack root: <c>%LocalAppData%\DontIgnore.Screeny</c> for installed, unpack folder for portable.</param>
/// <param name="StubPath">Stub <c>&lt;root&gt;\Screeny.exe</c>: unchanged across updates, shortcuts point to it.</param>
public sealed record VelopackInfo(bool IsManaged, bool IsPortable, string? RootDir, string? StubPath)
{
    public static readonly VelopackInfo None = new(false, false, null, null);

    /// <summary>Installed via Setup.exe (not portable).</summary>
    public bool IsInstalled => IsManaged && !IsPortable;

    /// <summary>After <c>VelopackApp.Run()</c>. Never throws.</summary>
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

    /// <summary>Stub in root with same name as process exe; null if absent.</summary>
    public static string? StubFor(string rootDir, string? processPath)
    {
        if (string.IsNullOrEmpty(processPath)) return null;
        var stub = Path.Combine(rootDir, Path.GetFileName(processPath));
        return File.Exists(stub) ? stub : null;
    }
}
