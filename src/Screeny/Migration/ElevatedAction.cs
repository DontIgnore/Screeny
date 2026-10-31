using System.Text.RegularExpressions;
using Screeny.Logging;

namespace Screeny.Migration;

/// <summary>
/// Screeny instance restarted with admin rights for one action:
/// <c>Screeny.exe --elevated-action disable-autostart &lt;id&gt;</c>. Accepts only id from <see cref="ConflictCatalog"/>
/// (whitelist) and exactly three arguments — never registry path or command. Finds entry again via environment snapshot,
/// disables autostart for all users via StartupApproved flag and exits with code. No UI, no mutex, no hotkeys:
/// <see cref="App"/> calls <see cref="RunReal"/> as first startup line.
/// </summary>
public static partial class ElevatedAction
{
    public const string Flag = "--elevated-action";
    public const string DisableAutostartVerb = "disable-autostart";

    public const int ExitOk = 0;
    /// <summary>No all-users entry or already disabled.</summary>
    public const int ExitNothingToDo = 1;
    /// <summary>Arguments failed whitelist.</summary>
    public const int ExitInvalidArgs = 2;
    public const int ExitFailed = 3;

    /// <summary>Whether arguments contain elevated action key (anywhere): such process never launches normal app.</summary>
    public static bool IsRequested(IReadOnlyList<string> args)
        => args.Any(a => string.Equals(a?.Trim(), Flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Arguments for launch: exactly three tokens.</summary>
    public static string[] Arguments(string conflictId) => [Flag, DisableAutostartVerb, conflictId];

    [GeneratedRegex("^[a-z0-9]{1,32}$")]
    private static partial Regex IdPattern();

    /// <summary>Program id or null if arguments failed validation (extra, unknown id, path, wrong order).</summary>
    public static string? Parse(IReadOnlyList<string> args, ConflictCatalog catalog)
    {
        if (args.Count != 3) return null;
        if (!string.Equals(args[0], Flag, StringComparison.Ordinal)) return null;
        if (!string.Equals(args[1], DisableAutostartVerb, StringComparison.Ordinal)) return null;
        var id = args[2];
        if (id is null || !IdPattern().IsMatch(id)) return null;
        return catalog.Find(id)?.Id;
    }

    /// <summary>Executes one action and returns exit code.</summary>
    public static int Run(IReadOnlyList<string> args, ConflictCatalog catalog, Func<IEnvironmentSnapshot> capture, IConflictActions actions)
    {
        if (Parse(args, catalog) is not { } id)
        {
            Log.Error($"Elevated action rejected: {args.Count} argument(s)");
            return ExitInvalidArgs;
        }
        var conflict = ConflictDetector.Detect(capture(), catalog).FirstOrDefault(c => c.Id == id);
        if (conflict is null || !conflict.NeedsAdminForAutostart) return ExitNothingToDo;
        var result = actions.DisableAutostart(conflict);
        if (!result.Success) Log.Error($"Elevated action {id} failed: {result.Message}");
        return result.Success ? ExitOk : ExitFailed;
    }

    /// <summary>Entry point of elevated instance: real environment and real actions in admin mode.</summary>
    public static int RunReal(IReadOnlyList<string> args)
    {
        try { return Run(args, ConflictCatalog.Default, WindowsEnvironmentSnapshot.Capture, ConflictActions.CreateReal(elevated: true)); }
        catch (Exception ex) { Log.Error("Elevated action crashed", ex); return ExitFailed; }
    }

    /// <summary>
    /// How to restart self: usually <c>Screeny.exe</c>; under <c>dotnet run</c> process is dotnet.exe, and first argument
    /// is path to Screeny.dll (as in <see cref="Settings.StartupRegistrar.BuildCommand"/>).
    /// </summary>
    public static (string File, string[] Prefix) SelfLaunch()
    {
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("Process path is unknown");
        bool dotnetHost = string.Equals(Path.GetFileName(process), "dotnet.exe", StringComparison.OrdinalIgnoreCase);
        if (!dotnetHost) return (process, []);
        var dll = Path.Combine(AppContext.BaseDirectory, (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "Screeny") + ".dll");
        return (process, [dll]);
    }
}
