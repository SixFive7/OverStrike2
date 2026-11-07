namespace OverStrike2.Server;

/// <summary>The watcher's freeze recovery: both paths to a kill, and the kill (see Watch.cs).</summary>
public sealed partial class Watch
{
    /// <summary>Path a: the error-map signature, confirmed by A2S silence.</summary>
    void TryFreeze(double now)
    {
        if (!Config.FreezeRecovery || signatureAt is not { } sig)
            return;
        var pid = Engine.Pid();
        if (!Rules.FreezeConfirmed(now, sig, silentSince, pid is not null, otherLevel, Config.FreezeGrace))
        {
            if (silentSince is null && now - sig > 60)
            {
                Log.Line("the freeze signature was followed by A2S answering - not a freeze; standing down");
                signatureAt = null;
            }
            return;
        }
        Kill(now, pid!.Value, $"error-map freeze: signature {now - sig:0} s ago, A2S silent for {now - silentSince!.Value:0} s, no other map loading since",
             $"frozen engine (signature {now - sig:0} s ago, A2S silent {now - silentSince.Value:0} s)");
    }

    /// <summary>Path b: silence alone - A2S and RCON both dead for the grace - for the freezes that print nothing.</summary>
    void TrySilence(double now)
    {
        if (!Config.FreezeRecovery || silentSince is not { } silent || now - silent < Config.FreezeGrace)
            return;
        var pid = Engine.Pid();
        var armed = Console is not null && pid is not null && pid == ArmedPid;
        if (!Rules.SilenceConfirmed(now, silentSince, a2sFails, rconSince, rconFails, armed, pid is not null, GoingDown is not null, Config.FreezeGrace))
        {
            var why = GoingDown is not null ? "the watcher is taking it down itself"
                : pid is null ? "no engine process"
                : Console is null ? "no live console.log, so a quit or restart could not be told apart"
                : pid != ArmedPid ? "this engine process has not answered A2S since it started, or a quit/restart was sent"
                : rconSince is null ? "RCON still answers"
                : Math.Min(a2sFails, rconFails) < Rules.SilentPolls ? $"{a2sFails} A2S and {rconFails} RCON polls failed so far, {Rules.SilentPolls} needed"
                : $"RCON silent since {AtClock(rconSince.Value)} only";
            Log.Once("silent", $"A2S silent since {AtClock(silent)} - not killing: {why}", 300);
            return;
        }
        Kill(now, pid!.Value, $"silent freeze: A2S silent for {now - silent:0} s ({a2sFails} polls) and RCON for {now - rconSince!.Value:0} s ({rconFails} polls), no error-map signature",
             $"silent engine (A2S {now - silent:0} s, RCON {now - rconSince.Value:0} s)");
    }

    /// <summary>The one place a freeze kill happens, for both paths: the same gates, the same counters, the same record.</summary>
    void Kill(double now, int pid, string why, string what)
    {
        var blockedWhy = Blocked();
        var block = blockedWhy is null ? Rules.FreezeBlock(State, now, Zone) : (true, blockedWhy);
        if (block is { } b)
        {
            Log.Once("freeze", $"{(b.Alert ? "ALERT: " : "")}{what} but {(b.Alert ? "not killing it" : "waiting")}: {b.Why}");
            return;
        }
        if (!Commit(now, "freeze"))
            return;
        GoingDown = now;
        Engine.Kill(pid, why);
        signatureAt = null;
    }
}
