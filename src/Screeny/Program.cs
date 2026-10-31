using Screeny.Logging;
using Screeny.Settings;
using Velopack;
using Velopack.Locators;

namespace Screeny;

/// <summary>
/// Entry point instead of generated WPF Main (<c>StartupObject</c> in csproj): Velopack requires
/// <see cref="VelopackApp"/> to run first — on install/update/uninstall Update.exe runs Screeny.exe
/// with special keys, and <c>Run()</c> handles them and exits before any window.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            // Hooks without UI and with timeouts (uninstall — 30s): only fast actions.
            .OnBeforeUninstallFastCallback(_ => { RemoveStartupEntry(); RemoveModules(); })
            // Downloaded update applied only explicitly (App.Updates.cs): by button in window or silently on exit if
            // update check still enabled. Else Velopack would install it at next launch itself.
            .SetAutoApplyOnStartup(false)
            .Run();

        // Before any window (wizard, --settings): autostart writes stable root stub, not current\Screeny.exe.
        // Test instance doesn't touch Run key at all, so value affects nothing for it.
        StartupRegistrar.UseLaunchPath(Updates.VelopackInfo.Detect().StubPath);

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>Uninstall: autostart entry pointing into install folder, else Windows would launch nonexistent exe.</summary>
    private static void RemoveStartupEntry()
    {
        try
        {
            if (VelopackLocator.IsCurrentSet && VelopackLocator.Current.RootAppDir is { } root)
                StartupRegistrar.RemoveIfInside(new RegistryRunKey(), root);
        }
        catch (Exception ex) { Log.Error("Uninstall hook: startup entry removal failed", ex); }
    }

    /// <summary>
    /// Uninstall: downloaded modules (<c>%LocalAppData%\Screeny\Modules</c>, precise recognition) — ~30 MB that
    /// are useless without Screeny. Only this folder: history and user settings remain. Folder shared with portable
    /// copy: if it runs, it will return to Windows recognition and offer re-download. If module currently loaded
    /// into running portable copy, its folder marked and erased at its next launch.
    /// </summary>
    private static void RemoveModules()
    {
        try { Modules.ModuleInstaller.DeleteRootOrMark(Modules.OcrModule.ModulesRoot(dev: false)); }
        catch (Exception ex) { Log.Error("Uninstall hook: modules removal failed", ex); }
    }
}
