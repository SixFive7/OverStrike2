using System.Globalization;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>The watcher's planned restarts: Valve's check, a new version of our parts, which restart is due, the bridge's notices and the
/// quit (see Watch.cs).</summary>
public sealed partial class Watch
{
    void CheckValve(double now)
    {
        if (!Config.Updates)
            return;
        var dotted = PatchVersion();
        var vint = Versions.VersionInt(dotted);
        if (vint is not { } v)
        {
            Log.Once("valve", $"WARNING: steam.inf PatchVersion is {(dotted is null ? "None" : $"'{dotted}'")} - cannot ask Valve, taking no action");
            return;
        }
        if (A2sInfo is { } a && a.Version != dotted && Versions.IsDotted(a.Version))
        {
            Log.Once("valve", $"WARNING: A2S reports {a.Version} but steam.inf says {dotted} - not deciding on a disagreement");
            return;
        }
        if (v == verdictBuild && now - VerdictAt < Config.CheckEvery + checkBackoff)
            return;
        var answer = Valve.Check(v);
        (Verdict, verdictBuild, VerdictAt) = (answer, v, now);
        if (answer.Answer == "unknown")
        {
            checkBackoff = Math.Min(21600, Math.Max(Config.CheckEvery, checkBackoff * 2));
            Log.Once("valve", $"WARNING: cannot establish whether {dotted} ({v}) is current - {answer.Why}. No action; next try in {Words.Hm(Config.CheckEvery + checkBackoff)}");
        }
        else
        {
            checkBackoff = 0;
            if (answer.Answer == "stale")
                Log.Once("valve", $"Valve reports {dotted} ({v}) as OUT OF DATE, required {answer.Required}");
            else
                Log.Once("valve", $"build {dotted} ({v}) is current", OkRepeat);
        }
    }

    /// <summary>Our own parts on disk, against what install put in place: a new version once it has been still for VersionSettle.</summary>
    void CheckVersion(double now)
    {
        if (!Config.NewVersions || InstalledNow is not { } inst || now < versionCheckAt)
            return;
        versionCheckAt = now + VersionCheckEvery;
        try
        {
            var sig = Fingerprint.Signature(Config.Paths, inst.ServerFrom, SettingsPath);
            if (sig != versionSig || OnDisk is null)
            {
                OnDisk = Fingerprint.Compute(Config.Paths, inst.ServerFrom, SettingsPath, Env);
                (versionSig, versionSigAt) = (sig, now);   // a change (or the first look): it must be still for VersionSettle
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            versionUnread = true;
            Log.Once("version", $"WARNING: our parts on disk cannot be read ({e.Message}) - no decision on a new version");
            return;
        }
        versionUnread = false;
        var disk = OnDisk!;
        if (disk.Total == inst.Fingerprint)
        {
            if (VersionDue is not null)
                Log.Line($"what is on disk is the installed version again ({inst.Fingerprint}) - no version restart due");
            VersionDue = null;
            return;
        }
        var changed = string.Join(", ", disk.Changed(inst.Parts));
        if (now - versionSigAt < VersionSettle)
        {
            if (VersionDue is { } waiting)
                Log.Line($"our parts are being written again ({changed}) - the restart for {waiting.Total} is off until what is on disk is still");
            VersionDue = null;                         // a match end now would quit into a half-written deploy
            Log.Once("version", $"a new version of our parts is being written ({changed}) - waiting until it is still for {VersionSettle:0} s", 600);
            return;
        }
        if (VersionDue?.Total != disk.Total)
        {
            VersionDue = disk;
            Log.Line($"a new version of our parts is on disk: {changed} (installed {inst.Fingerprint}, on disk {disk.Total})");
        }
    }

    /// <summary>Whether our parts are being written right now, looked at afresh: a deploy that began after CheckVersion's last look counts too.
    /// A quit now would have install copy a half-written plugin folder, and the check after the restart would raise a false alarm. Parts that
    /// cannot be read are no deploy: nothing is decided on them, and they hold no other restart back.</summary>
    bool Deploying(double now)
    {
        if (!Config.NewVersions || InstalledNow is not { } inst || versionUnread)
            return false;
        try
        {
            if (Fingerprint.Signature(Config.Paths, inst.ServerFrom, SettingsPath) != versionSig)
            {
                versionCheckAt = 0;                    // CheckVersion looks at it on the next tick
                return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return OnDisk is { } disk && disk.Total != inst.Fingerprint && now - versionSigAt < VersionSettle;
    }

    string? PlannedKind(double now)
    {
        if (Config.Updates && Verdict.Answer == "stale" && verdictBuild == Versions.VersionInt(PatchVersion()))
        {
            var required = Verdict.Required ?? 0;
            if (State.UpdateTripped != 0 && required > State.UpdateTrippedFor)
            {
                Log.Line($"Valve now requires {required}, newer than the {State.UpdateTrippedFor} the update breaker latched on - re-arming it");
                (State.UpdateTripped, State.UpdateTrippedFor, State.UpdateMisses, State.UpdateRetryAfter) = (0, 0, 0, 0);
                Save();
            }
            if (State.UpdateTripped != 0)
                Log.Once("breaker", $"ALERT: the build is stale (Valve requires {required}) but the update breaker is latched for it - not restarting. Delete {Config.Paths.StateFile} to re-arm.");
            else if (now >= State.UpdateRetryAfter)
                return "update";
        }
        if (Config.NewVersions && VersionDue is { } v)
        {
            if (v.Total != State.VersionTrippedFor)
                return "version";
            Log.Once("version-breaker", $"ALERT: a new version ({v.Total}) is on disk, but a restart did not install it before - not restarting for it again; a newer one re-arms");
        }
        if (Rules.DailyDue(now, Config.Daily, Zone, State.DoneDay))
            return "daily";
        if (Config.DownloadRestart && Workshop.Broken is { } broken)
        {
            if (Rules.WorkshopBlock(State, now, Zone) is { } why)
            {
                Log.Once("workshop-limit", $"WARNING: workshop downloads are broken in this engine process ({broken.Why}) - NOT restarting: {why}. Only warning.", 3600);
                return null;
            }
            return "workshop";
        }
        return null;
    }

    /// <summary>Tell the bridge of a restart: "match-end" when it becomes due (it waits for the end of the current match), "now" with the
    /// seconds left before the quit. True when the bridge took it (it shows the players; it holds its own map change).</summary>
    bool Announce(string kind, string when, int seconds)
    {
        var (told, reply) = Bridge.Post(BridgeWire.RestartNotice, BridgeWire.RestartBody(Kinds.Wire(kind), when, seconds), NoticeTimeout);
        if (told == "ok")
        {
            announced = kind;
            Log.Once("announce", $"the bridge took the {kind} restart's notice ({when}{(when == BridgeWire.AtOnce ? $", {seconds} s" : "")})", 60);
            return true;
        }
        if (told == "failed")
            Log.Once("announce-failed", $"WARNING: the bridge did not take the {kind} restart's notice ({reply})", 900);
        return false;
    }

    void CancelAnnounced()
    {
        if (announced is not { } kind)
            return;
        announced = null;
        Bridge.Post(BridgeWire.RestartNotice, BridgeWire.RestartCancelBody(Kinds.Wire(kind)), NoticeTimeout);
        Log.Line($"the bridge was told that the {kind} restart is off");
    }

    void TryPlanned(double now)
    {
        var kind = PlannedKind(now);
        if (kind is null)
        {
            if (Due is { } d)
            {
                Log.Line($"the {d.Kind} restart is no longer due (window passed or build current) - standing down");
                CancelAnnounced();
            }
            (Due, warned, noticeFailing, CountdownUntil) = (null, false, null, null);
            return;
        }
        if (Due is not { } due || due.Kind != kind)
        {
            if (Due is not null)
                CancelAnnounced();                     // another kind took its place
            (Due, warned, CountdownUntil) = ((kind, now), false, null);
            Log.Line($"{kind} restart due");
        }
        var blockedWhy = Blocked();
        var block = blockedWhy is null ? Rules.PlannedBlock(State, now, Zone) : (true, blockedWhy);
        if (block is { } b)
        {
            Log.Once("planned", $"{(b.Alert ? "ALERT: " : "")}{kind} restart due but {(b.Alert ? "blocked" : "deferred")}: {b.Why}");
            return;
        }
        if (A2sInfo is not { } a2s)
        {
            Log.Once("planned", $"{kind} restart due, but the engine is not answering A2S - waiting");
            return;
        }
        if (CountdownUntil is { } until)
        {
            if (now >= until)
            {
                CountdownUntil = null;
                if (Deploying(now) || !Quit(kind, now, null))   // the bridge showed the countdown and the last line
                {
                    Announce(kind, BridgeWire.AtMatchEnd, RestartCountdown);   // it did not go: it waits for the next match end again
                    announceTried = now;
                }
            }
            return;
        }
        if (Deploying(now))
        {
            Log.Once("planned", $"{kind} restart due, but a new version of our parts is being written - it waits until what is on disk is still");
            return;
        }
        var final = kind == "workshop" ? Rules.WorkshopLine(Rules.BackIn(State.Durations)) : null; // the download restart's line for the log, at an empty server
        if (Kinds.EmptyOnly.Contains(kind) && a2s.Humans > 0)
        {
            var what = kind == "workshop" ? $"downloads broken: {Workshop.Broken?.Why}"
                : $"the daily one, until {AtClock((Rules.NextSlot(now - Rules.DailyWindow, Config.Daily, Zone) ?? now) + Rules.DailyWindow)} at the latest";
            Log.Once("planned", $"{kind} restart due ({what}) but {a2s.Humans} human(s) on - waiting for an empty server, never a kick", 900);
            return;
        }
        if (a2s.Humans == 0)
        {
            if (now < noticeRetry)
                return;                                // the quit notice is tried again then
            var humans = RconHumans();
            if (humans == 0)
            {
                Announce(kind, BridgeWire.AtOnce, 0);
                Quit(kind, now, final);
            }
            else
                Log.Once("planned", $"A2S says the server is empty but RCON status says {(humans?.ToString(CultureInfo.InvariantCulture) ?? "None")} - not restarting on a disagreement");
            return;
        }
        if (Console is null)
        {
            Log.Once("planned", $"{kind} restart due with {a2s.Humans} player(s) on, but no live console.log to see the match end - waiting until the server is empty");
            return;
        }
        if (!warned)
        {
            Announce(kind, BridgeWire.AtMatchEnd, RestartCountdown);   // a bridge that does not take it: nobody is warned
            (warned, announceTried) = (true, now);
        }
        else if (now - announceTried >= AnnounceRefresh)
        {
            Announce(kind, BridgeWire.AtMatchEnd, RestartCountdown);       // again: a bridge swapped (or back) since knows it too
            announceTried = now;
        }
        var since = Due!.Value.Since;
        if (matchEnds.Any(t => t >= since) && matchEnds[^1] >= now - 20 && matchEnds[^1] > usedMatchEnd)
        {
            usedMatchEnd = matchEnds[^1];
            if (Announce(kind, BridgeWire.AtOnce, RestartCountdown))
            {
                CountdownUntil = now + RestartCountdown;
                Log.Line($"{kind} restart: the match is over - the bridge counts down {RestartCountdown} s, then the quit");
            }
            else
                Quit(kind, now, null);                 // no bridge to count down: the quit, unannounced
        }
        else
            Log.Once("planned", $"{kind} restart due, waiting for the end of the current match ({a2s.Humans} player(s))", 900);
    }

    /// <summary>Count a restart on disk BEFORE it happens - the container goes down seconds later. The in-memory state only changes if the
    /// write succeeded, so a failed write can never leave a half-counted restart behind.</summary>
    bool Commit(double now, string kind, int? required = null)
    {
        var fresh = State.Copy();
        fresh.Restarts = [.. fresh.Restarts.Where(e => now - e.At >= 0 && now - e.At < Rules.Window), new Restart((long)now, kind)];
        fresh.Pending = new Pending(kind, (long)now, kind != "freeze" ? PatchVersion() : null, required,
                                    kind != "freeze" ? InstalledNow?.Fingerprint : null, kind != "freeze" ? OnDisk?.Total : null);
        if (kind != "freeze" && Rules.DailyDue(now, Config.Daily, Zone, fresh.DoneDay))   // any quit inside the window is the day's restart:
            fresh.DoneDay = Rules.LocalDay(Rules.NextSlot(now - Rules.DailyWindow, Config.Daily, Zone)!.Value, Zone);   // the slot's day
        var previous = State;
        State = fresh;
        if (Save())
            return true;
        State = previous;
        Log.Line($"ALERT: {kind} restart abandoned - the counters could not be written");
        return false;
    }

    (string Told, string? Why) QuitNotice(string kind, bool cancel = false) =>
        Bridge.Post(BridgeWire.Quitting, cancel ? BridgeWire.QuitCancelBody(kind) : BridgeWire.QuitBody(kind), NoticeTimeout);

    /// <summary>The only place a planned restart happens. The bridge hears of it first: before the counters, so a notice it does not take
    /// costs no restart, and a restart that does not happen after it is taken back. True when the quit went out.</summary>
    bool Quit(string kind, double now, string? finalLine)
    {
        if (now < noticeRetry)
            return false;
        var (told, why) = QuitNotice(kind);
        if (told == "failed")
        {
            noticeFailing ??= now;
            if (now - noticeFailing >= QuitNoticePatience)
                Log.Line($"WARNING: the bridge has not taken the quit notice for {Words.Hm(now - noticeFailing.Value)} ({why}) - the {kind} restart goes ahead without it: a false crash mark can be cleared");
            else
            {
                noticeRetry = now + QuitNoticeRetry;
                Log.Once("quit-notice", $"WARNING: the bridge did not take the quit notice ({why}) - not quitting yet: without it the bridge would book a crash on an innocent map "
                                        + $"(the quit's echo dies with the process). The {kind} restart stays due; the notice is tried again every {QuitNoticeRetry:0} s, and the "
                                        + $"restart goes ahead without it after {Words.Hm(QuitNoticePatience)}", 900);
                return false;
            }
        }
        noticeFailing = null;
        if (!Commit(now, kind, kind == "update" ? Verdict.Required : null))
        {
            if (told == "ok")
                QuitNotice(kind, cancel: true);
            CancelAnnounced();
            return false;
        }
        if (!string.IsNullOrEmpty(finalLine))
        {
            Notice(finalLine, 30);
            Sleep(QuitLead);
        }
        var planned = State.Restarts.Count(e => e.Kind != "freeze" && now - e.At < Rules.Window);
        var bridgeWords = told switch
        {
            "ok" => $"The bridge took the quit notice: {why}.",
            "none" => "No bridge to tell: the echo alone.",
            _ => $"The bridge did not take the quit notice: {why}.",
        };
        Log.Line($"RESTARTING NOW ({kind}): RCON quit, so the image's entry script ends and Docker runs it again (SteamCMD runs again). {planned} planned restart(s) in 24 h. {bridgeWords}");
        var pid = Engine.Pid();
        GoingDown = now;                               // from here on the silence is ours: the silence path must not "rescue" it
        if (!Engine.Rcon("quit", expectReply: false).Opened)
        {
            GoingDown = null;                          // never force an undelivered quit: the engine is not going down
            if (told == "ok")
                QuitNotice(kind, cancel: true);
            CancelAnnounced();
            Log.Line($"WARNING: RCON quit could not be delivered - not forcing it; the {kind} restart stays due and is retried after the {Rules.PlannedMinGap / 60:0} min floor");
            return false;
        }
        (Due, warned, announced) = (null, false, null);   // done: no "no longer due" line while the container goes down
        for (var i = 0; i < QuitTimeout * 2; i++)
        {
            if (Engine.Pid() is null)
                return true;
            Sleep(0.5);
        }
        if (pid is { } p && Engine.Pid() == p)
            Engine.Kill(p, $"it was still alive {QuitTimeout} s after `quit`");
        return true;
    }
}
