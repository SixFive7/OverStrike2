using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>The watcher's restart of a previous run, judged once the engine answers, and the bridge's note of what it changed (see Watch.cs).</summary>
public sealed partial class Watch
{
    /// <summary>Judge the restart a PREVIOUS run of the watcher triggered (it is recorded just before the container goes down, so the verdict
    /// belongs to the next start). A record from this very run is left for the next one.</summary>
    void VerifyPending(double now)
    {
        if (State.Pending is not { } pending || pending.At >= Started)
            return;
        if (firstAnswer is null)
        {
            if (now - pending.At > RecoverWithin && now - Started > RecoverWithin)
            {
                State.Pending = null;
                if (pending.Kind == "freeze")
                {
                    State.FreezeMisses++;
                    if (State.FreezeMisses >= Rules.FreezeMaxMisses)
                        State.FreezeTripped = (long)now;
                    Log.Line($"ALERT: the server has not answered A2S {Words.Hm(now - pending.At)} after the freeze kill (miss {State.FreezeMisses} of {Rules.FreezeMaxMisses}"
                             + (State.FreezeTripped != 0 ? $", FREEZE BREAKER LATCHED - delete {Config.Paths.StateFile} to re-arm" : "") + ")");
                }
                else
                    Log.Line($"WARNING: the {pending.Kind} restart was not followed by an A2S answer within {Words.Hm(RecoverWithin)}");
                Save();
            }
            return;
        }
        State.Pending = null;
        var took = (long)(firstAnswer.Value - pending.At);
        if (pending.Kind == "freeze")
        {
            State.FreezeMisses = 0;
            Log.Line($"freeze recovery worked: A2S answered {took} s after the kill");
        }
        else
        {
            if (took is > 0 and < 3600 && pending.Kind == "daily")
                State.Durations = [.. State.Durations.Append((int)took).TakeLast(5)];
            Log.Line($"measured: the {pending.Kind} restart took {took} s from quit to A2S answering (last [{string.Join(", ", State.Durations)}])");
        }
        var nowVersion = PatchVersion();
        if (pending.Kind == "update")
        {
            if (nowVersion is not null && nowVersion != pending.From)
            {
                State.UpdateMisses = 0;
                Log.Line($"update installed: PatchVersion {pending.From} -> {nowVersion}");
            }
            else
            {
                State.UpdateMisses++;
                var misses = State.UpdateMisses;
                if (misses >= Rules.UpdateMaxMisses)
                {
                    State.UpdateTripped = (long)now;
                    State.UpdateTrippedFor = pending.Required ?? 0;
                }
                else
                    State.UpdateRetryAfter = (long)now + (long)Rules.UpdateRetry[(int)Math.Min(misses, Rules.UpdateRetry.Length) - 1];
                Log.Line($"{(State.UpdateTripped != 0 ? "ALERT" : "WARNING")}: the update restart left PatchVersion on {nowVersion ?? "None"} (miss {State.UpdateMisses} of {Rules.UpdateMaxMisses})"
                         + (State.UpdateTripped != 0 ? $" - UPDATE BREAKER LATCHED for this build (a newer required build, or deleting {Config.Paths.StateFile}, re-arms it)" : ""));
            }
        }
        if (pending.Kind == "version")
        {
            if (InstalledNow is { } inst && inst.Fingerprint == pending.Source)
                Log.Line($"new version installed: {pending.Installed ?? "?"} -> {inst.Fingerprint}");
            else
            {
                State.VersionTrippedFor = pending.Source ?? "";
                Log.Line($"ALERT: the version restart did not install the new version {pending.Source} (installed now: {InstalledNow?.Fingerprint ?? "nothing recorded"}) - "
                         + "no restart for it again; a newer version re-arms it");
            }
        }
        if (pending.Kind != "freeze")
        {
            if (pending.From is not null && nowVersion is not null && nowVersion != pending.From)
                updated.Add((BridgeWire.UpdatedBody(BridgeWire.GameUpdate, pending.From, nowVersion), 0, now));
            if (pending.Installed is not null && InstalledNow is { } i2 && i2.Fingerprint != pending.Installed)
                updated.Add((BridgeWire.UpdatedBody(BridgeWire.OurUpdate, pending.Installed, i2.Fingerprint), 0, now));
        }
        Save();
    }

    /// <summary>After a restart that changed something, the bridge hears what (POST /updated): it shows the players a short note.</summary>
    void SendUpdated(double now)
    {
        for (var i = 0; i < updated.Count; i++)
        {
            var (body, tries, next) = updated[i];
            if (now < next)
                continue;
            var (told, reply) = Bridge.Post(BridgeWire.Updated, body, NoticeTimeout);
            if (told == "ok" || told == "none" || tries + 1 >= UpdatedTries)
            {
                Log.Line(told == "ok" ? $"the bridge took the after-restart note ({body.Split('\t')[2]})" : told == "none" ? "no bridge to tell what the restart changed"
                         : $"WARNING: the bridge did not take the after-restart note {UpdatedTries} times ({reply}) - dropped");
                updated.RemoveAt(i--);
            }
            else
            {
                if (tries == 0)
                    Log.Line($"the bridge did not take the after-restart note ({body.Split('\t')[2]}: {reply}) - tried again every {UpdatedRetry:0} s, up to {UpdatedTries} times");
                updated[i] = (body, tries + 1, now + UpdatedRetry);
            }
        }
    }
}
