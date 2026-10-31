using Screeny.Localization;
using Screeny.Logging;

namespace Screeny.Migration;

/// <summary>
/// Wizard actions in <c>--dev</c> and tests: do nothing to system, only log, remember calls
/// and mutate <see cref="FakeEnvironmentSnapshot"/>, so re-scan shows result.
/// </summary>
public sealed class FakeConflictActions(FakeEnvironmentSnapshot env) : IConflictActions
{
    private readonly object _gate = new();
    private readonly List<string> _calls = new();

    /// <summary>Calls: “close:lightshot”, “autostart:sharex”, “elevated:lightshot”, “uninstall:sharex”, “printscreen”.</summary>
    public IReadOnlyList<string> Calls { get { lock (_gate) return _calls.ToList(); } }

    /// <summary>What elevated action will return (for testing UAC cancel and errors in UI/tests).</summary>
    public ActionOutcome ElevatedOutcome { get; set; } = ActionOutcome.Done;

    private void Record(string call)
    {
        lock (_gate) _calls.Add(call);
        Log.Error($"[dev] fake setup wizard action: {call}");
    }

    public Task<ActionResult> CloseProcessesAsync(Conflict conflict)
    {
        Record("close:" + conflict.Id);
        env.ProcessList.RemoveAll(p => conflict.Processes.Any(c => c.Pid == p.Pid));
        return Task.FromResult(new ActionResult(ActionOutcome.Done, Loc.F("Action.Closed", conflict.Program.DisplayName)));
    }

    /// <summary>Like <see cref="ConflictActions"/> in elevated instance: disables entries for all users.</summary>
    public bool Elevated { get; init; }

    public ActionResult DisableAutostart(Conflict conflict)
    {
        Record("autostart:" + conflict.Id);
        if (Elevated)
        {
            Disable(conflict, machine: true);
            return new(ActionOutcome.Done, Loc.F("Action.MachineAutostartOff", conflict.Program.DisplayName));
        }
        Disable(conflict, machine: false);
        return conflict.NeedsAdminForAutostart
            ? new(ActionOutcome.NeedsAdmin, Loc.F("Action.AutostartNeedsAdmin", conflict.Program.DisplayName))
            : new(ActionOutcome.Done, Loc.F("Action.AutostartOff", conflict.Program.DisplayName));
    }

    public async Task<ActionResult> DisableAutostartElevatedAsync(Conflict conflict)
    {
        Record("elevated:" + conflict.Id);
        await Task.Delay(300);
        if (ElevatedOutcome == ActionOutcome.Cancelled)
            return new(ActionOutcome.Cancelled, Loc.T("Action.ElevationCancelled"));
        if (ElevatedOutcome != ActionOutcome.Done)
            return new(ActionOutcome.Failed, Loc.F("Action.AutostartFailedCode", conflict.Program.DisplayName, 3));
        Disable(conflict, machine: true);
        return new(ActionOutcome.Done, Loc.F("Action.MachineAutostartOff", conflict.Program.DisplayName));
    }

    public ActionResult LaunchUninstaller(Conflict conflict)
    {
        Record("uninstall:" + conflict.Id);
        return new(ActionOutcome.Done, Loc.F("Action.DevUninstaller", conflict.Program.DisplayName));
    }

    public ActionResult DisableSystemPrintScreen()
    {
        Record("printscreen");
        env.PrintScreenKeyForSnippingEnabled = 0;
        return new(ActionOutcome.Done, ConflictActions.SystemPrintScreenDoneText);
    }

    private void Disable(Conflict conflict, bool machine)
    {
        var disabled = StartupApproved.DisabledValue(DateTime.UtcNow);
        for (int i = 0; i < env.AutostartList.Count; i++)
        {
            var a = env.AutostartList[i];
            if (a.IsMachineWide == machine && conflict.Autostarts.Contains(a))
                env.AutostartList[i] = a with { Approval = disabled };
        }
    }
}

/// <summary>
/// Only place where real or fake environment is chosen. In <c>--dev</c> always fake — real
/// wizard actions impossible in test instance, even without <c>--scenario</c>.
/// </summary>
public static class MigrationServices
{
    public sealed record Services(Func<IEnvironmentSnapshot> Capture, IConflictActions Actions, bool IsFake);

    public static Services Create(bool isDev, string? scenario)
    {
        if (isDev)
        {
            var env = FakeEnvironmentSnapshot.ForScenario(scenario);
            return new Services(() => env, new FakeConflictActions(env), IsFake: true);
        }
        return new Services(WindowsEnvironmentSnapshot.Capture, ConflictActions.CreateReal(elevated: false), IsFake: false);
    }
}
