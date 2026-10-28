using System.Text.RegularExpressions;
using Screeny.Logging;

namespace Screeny.Migration;

/// <summary>
/// Экземпляр Screeny, перезапущенный с правами администратора ради одного действия:
/// <c>Screeny.exe --elevated-action disable-autostart &lt;id&gt;</c>. Принимает только id из <see cref="ConflictCatalog"/>
/// (белый список) и ровно три аргумента — никогда путь реестра или команду. Сам заново находит запись через снимок среды,
/// отключает автозапуск для всех пользователей флагом StartupApproved и выходит с кодом. Без UI, без mutex и без хоткеев:
/// <see cref="App"/> вызывает <see cref="RunReal"/> первой строкой запуска.
/// </summary>
public static partial class ElevatedAction
{
    public const string Flag = "--elevated-action";
    public const string DisableAutostartVerb = "disable-autostart";

    public const int ExitOk = 0;
    /// <summary>Записи для всех пользователей нет или она уже отключена.</summary>
    public const int ExitNothingToDo = 1;
    /// <summary>Аргументы не прошли белый список.</summary>
    public const int ExitInvalidArgs = 2;
    public const int ExitFailed = 3;

    /// <summary>Есть ли в аргументах ключ повышенного действия (в любом месте): такой процесс никогда не запускает обычное приложение.</summary>
    public static bool IsRequested(IReadOnlyList<string> args)
        => args.Any(a => string.Equals(a?.Trim(), Flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Аргументы для запуска: ровно три токена.</summary>
    public static string[] Arguments(string conflictId) => [Flag, DisableAutostartVerb, conflictId];

    [GeneratedRegex("^[a-z0-9]{1,32}$")]
    private static partial Regex IdPattern();

    /// <summary>id программы или null, если аргументы не прошли проверку (лишние, неизвестный id, путь, другой порядок).</summary>
    public static string? Parse(IReadOnlyList<string> args, ConflictCatalog catalog)
    {
        if (args.Count != 3) return null;
        if (!string.Equals(args[0], Flag, StringComparison.Ordinal)) return null;
        if (!string.Equals(args[1], DisableAutostartVerb, StringComparison.Ordinal)) return null;
        var id = args[2];
        if (id is null || !IdPattern().IsMatch(id)) return null;
        return catalog.Find(id)?.Id;
    }

    /// <summary>Выполняет одно действие и возвращает код выхода.</summary>
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

    /// <summary>Точка входа повышенного экземпляра: реальная среда и реальные действия в режиме администратора.</summary>
    public static int RunReal(IReadOnlyList<string> args)
    {
        try { return Run(args, ConflictCatalog.Default, WindowsEnvironmentSnapshot.Capture, ConflictActions.CreateReal(elevated: true)); }
        catch (Exception ex) { Log.Error("Elevated action crashed", ex); return ExitFailed; }
    }

    /// <summary>
    /// Как перезапустить себя: обычно <c>Screeny.exe</c>; под <c>dotnet run</c> процесс — dotnet.exe, и первым аргументом
    /// идёт путь к Screeny.dll (как в <see cref="Settings.StartupRegistrar.BuildCommand"/>).
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
