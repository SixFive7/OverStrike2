// A run's hand-over: the keep set's maps the game server lacks or stores older, downloaded and handed over; the staging folder's clean-up.
using System.Globalization;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

public sealed partial class SyncRun
{
    JsonObject ServeReport() => Section("serve", () => new JsonObject { ["ok"] = 0, ["bytes"] = 0, ["failed"] = new JsonArray() });

    (bool Ok, string Words) HandOverSafe(string wid, string? title)
    {
        try
        {
            return Handover.HandOver(Staging, Cfg.HandoverDir, wid, title, Cfg.Depotcache, Clock.Now(), L);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (false, $"the hand-over failed ({e.Message})");
        }
    }

    bool AcfNames(string wid, JsonObject rec)
    {
        var entry = Handover.AcfEntries(StagingAcf, wid);
        return entry is { } e && new[] { "size", "timeupdated", "manifest" }.All(k => Text.Digits(e.Installed.GetValueOrDefault(k)))
               && Handover.Holds(long.Parse(e.Installed["timeupdated"], CultureInfo.InvariantCulture), e.Installed["manifest"], Node.Int(rec["updated"]),
                                 Handover.WorkshopContent(State.Data, wid));
    }

    /// <summary>THE HAND-OVER, for what is missing or outdated (Handover.ServeWants): the keep set's maps the game server does not store - the
    /// upvoted maps, each mode's best, the own maps - and the ones it stores in an older version than the one this sync accepted, downloaded
    /// with SteamCMD (anonymous, with every push-back stop and the brake of the downloads) and handed over at once; never what waits in the
    /// hand-over folder, nor the stock and the ballot's maps (the server fetches those itself), nor an excluded id. Their version was
    /// tested when they were listed or updated: no test again.</summary>
    public void Serve()
    {
        var rep = ServeReport();
        if (!Cfg.Handover)
        {
            rep["skipped"] = "sync.handover is off";
            return;
        }
        var keep = SafeKeepSet(KeepInputs(), Clock.Now());
        var brokenKeep = keep.Problems.FirstOrDefault(p => p.StartsWith("the keep set could not be computed", StringComparison.Ordinal));
        if (brokenKeep is not null)
        {
            rep["skipped"] = brokenKeep;
            Errors.Add($"serve: {brokenKeep} - nothing handed over");
            return;
        }
        var has = Handover.ServerMaps(Cfg.GameDir);
        var pending = Handover.Pending(Cfg.HandoverDir);
        var (missing, outdated) = Handover.ServeWants(keep, has, pending, State.Data, Cfg, Handover.ServerManifests(Cfg.GameDir));
        var want = missing.Concat(outdated).ToList();
        var newer = new HashSet<string>(outdated);
        rep["missing"] = missing.Count;
        rep["outdated"] = outdated.Count;
        rep["stored"] = has.Count;
        rep["pending"] = pending.Count;
        if (has.Count == 0)
        {
            rep["skipped"] = "the game server's acf is not readable here - nothing fetched";
            return;
        }
        if (want.Count == 0)
        {
            L($"serve: the game server stores every map of the keep set, none older than its tested version ({pending.Count} pending hand-over(s))");
            return;
        }
        if (pending.Values.Sum() >= Handover.MaxBytes)
        {
            new Stop(Stop.HandOversWaiting, $"{Text.Mb(pending.Values.Sum())} of hand-overs wait for the server already").Into(rep);
            return;
        }
        try
        {
            var (forgotten, dropped) = Handover.TidyStaging(Staging, Cfg.HandoverDir, State.Items, L);
            rep["tidied"] = new JsonArray(forgotten, dropped);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            L($"staging: not tidied ({e.Message})");
        }
        var handed = new List<string>();
        var later = new List<(string Id, JsonObject Rec, long Bytes)>();
        // what is staged already - an earlier run's download it could not hand over (killed, or it looked before SteamCMD had written its
        // acf: 136 of 160 once) - is handed over as it is, without a download, unless the Workshop has a newer one
        foreach (var wid in want.Where(Staged).ToList())
        {
            var rec = State.Item(wid) ?? [];
            var entry = Handover.AcfEntries(StagingAcf, wid);
            if (entry is not { } e || !Text.Digits(e.Installed.GetValueOrDefault("timeupdated"))
                || !Handover.Holds(long.Parse(e.Installed["timeupdated"], CultureInfo.InvariantCulture), e.Installed.GetValueOrDefault("manifest"),
                                   Node.Int(rec["updated"]), Handover.WorkshopContent(State.Data, wid)))
                continue;                                  // no entry in SteamCMD's acf, or an older version: downloaded again below
            var bytes = Files.FolderStats(Path.Combine(Content, wid)).Bytes;
            var (done, words) = HandOverSafe(wid, Node.StrOr(rec["title"]));
            L($"serve: {wid} {Text.Cut(Node.StrOr(rec["title"]), 40)} was staged already, {words}");
            if (done)
            {
                Bump(rep, "ok", 1);
                Bump(rep, "bytes", bytes);
                Bump(rep, "staged", 1);
                handed.Add(wid);
                Event(wid, "handed-over", (newer.Contains(wid) ? "Its newer version handed over" : "Handed over") + $" to the game server from an earlier download ({Text.Mb(bytes)}).");
            }
        }
        var stalledIds = new HashSet<string>(Stalls.Select(s => s.Id));
        want = want.Where(w => !handed.Contains(w) && !stalledIds.Contains(w)).ToList();   // a map that stalled waits for the next run
        L($"serve: {want.Count} map(s) of the keep set the game server lacks or stores in an older version ({want.Count(newer.Contains)}) - downloading them for it"
          + (handed.Count > 0 ? $" ({handed.Count} handed over from the staging folder first)" : ""));
        var budget = new DownloadBudget(State, Day0);
        var session = Session(budget);
        try
        {
            for (var step = 0; step < want.Count; step++)
            {
                var wid = want[step];
                var (ok, why) = budget.Allows(wid);
                if (!ok && budget.DayOver)
                {
                    new Stop(Stop.Pushback, why).Into(rep);
                    break;
                }
                if (!ok)
                {
                    L($"serve: {wid}: {why}");
                    continue;
                }
                OnMap(wid, step, want.Count);
                if (Left() < 900)
                {
                    new Stop(Stop.Routine, "the run's deadline").Into(rep);
                    break;
                }
                var tWait = Clock.Now();
                if (!WaitForNobody("serve", 900))
                {
                    new Stop(Stop.Routine, "somebody was on the server until the deadline").Into(rep);
                    break;
                }
                if (session.Ready(Clock.Now() - tWait) is { } noLogin)
                {
                    noLogin.Into(rep);
                    break;
                }
                var t = Clock.Now();
                var (good, reason, nbytes) = session.Download(wid);
                if (good && Staged(wid))
                {
                    var onDisk = Files.FolderStats(Path.Combine(Content, wid)).Bytes;
                    var size = Math.Max(nbytes, onDisk);
                    budget.Spend(size, wid);
                    Bump(rep, "downloads", 1);
                    Bump(rep, "dl_bytes", size);
                    session.Worked();
                    var rec = State.Item(wid) ?? [];
                    // SteamCMD names the item in its acf 1.0-1.4 s after it says Success (looking at once missed 136 of 160): wait for its
                    // entry of this version, else hand it over once SteamCMD has quit
                    var fresh = false;
                    for (var i = 0; i < AcfWait; i++)
                    {
                        if (AcfNames(wid, rec))
                        {
                            fresh = true;
                            break;
                        }
                        Clock.Sleep(1);
                    }
                    var (done, words) = fresh ? HandOverSafe(wid, Node.StrOr(rec["title"]))
                        : (false, "SteamCMD's acf does not name this version yet - handed over once it has quit");
                    L($"serve: {wid} {Text.Cut(Node.StrOr(rec["title"]), 40)} downloaded in {Text.Fixed(Clock.Now() - t, 0)} s, {words}");
                    if (done)
                    {
                        Bump(rep, "ok", 1);
                        Bump(rep, "bytes", size);
                        handed.Add(wid);
                        Event(wid, "handed-over", (newer.Contains(wid) ? "Its newer version downloaded" : "Downloaded") + $" and handed over to the game server ({Text.Mb(size)}).");
                    }
                    else
                        later.Add((wid, rec, size));             // once SteamCMD has quit, its acf is complete
                }
                else
                {
                    List(rep, "failed").Add(Node.Array(wid, reason));
                    Event(wid, "failed", $"Its download failed ({reason}).");
                    L($"serve: the download of {wid} failed: {reason}");
                    if (session.Failed(wid, reason) is { } stop)
                    {
                        stop.Into(rep);
                        break;
                    }
                }
                State.Save();
                Clock.Sleep(Cfg.DownloadPause);
            }
        }
        finally
        {
            session.End();
            State.Save();
        }
        foreach (var (wid, rec, bytes) in later)                // SteamCMD has quit: its acf names them now
        {
            var (done, words) = HandOverSafe(wid, Node.StrOr(rec["title"]));
            L($"serve: {wid} after SteamCMD quit: {words}{(done ? "" : " - it stays staged for the next run")}");
            if (!done)
                List(rep, "handover_failed").Add(Node.Array(wid, words));
            if (done)
            {
                Bump(rep, "ok", 1);
                Bump(rep, "bytes", bytes);
                handed.Add(wid);
                Event(wid, "handed-over", (newer.Contains(wid) ? "Its newer version downloaded" : "Downloaded") + $" and handed over to the game server ({Text.Mb(bytes)}).");
            }
            else
                List(rep, "failed").Add(Node.Array(wid, words));
        }
        try                                               // SteamCMD writes its acf from memory: it named the early ones again
        {
            Handover.AcfForget(StagingAcf, handed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
        }
        var waitingNow = Handover.Pending(Cfg.HandoverDir);
        rep["left"] = want.Count(w => !waitingNow.ContainsKey(w));
        Limits(budget);
        L($"serve: {Node.Str(rep["ok"])} handed over ({Text.Mb(Node.Int(rep["bytes"]))}), {Node.Arr(rep["failed"]).Count} failed, {Node.Str(rep["left"])} still missing"
          + (Node.Truthy(rep["stop"]) ? $" - stopped: {Node.Str(rep["stop"])}" : ""));
    }

    /// <summary>A SteamCMD session for a downloading step, on the day's budget.</summary>
    SteamSession Session(DownloadBudget budget) => new(Seams.SteamCmd(Cfg, Staging, Path.Combine(Cfg.WorkDir, "steamcmd.log"), Clock), budget, Stalled, L);

    /// <summary>A SteamCMD stall on `wid`: booked (the run's list, the day's count of the map), logged. True when it is the run's second - the
    /// downloads stop then; one alone is routine, and the mail speaks of stalls only from two in a run, or two of one map in a day.</summary>
    public bool Stalled(string wid, string reason, DownloadBudget budget)
    {
        Stalls.Add((wid, budget.Stall(wid)));
        Report["stalls"] = new JsonArray(Stalls.Select(s => (JsonNode?)new JsonArray(s.Id, s.Stalls)).ToArray());
        L($"SteamCMD stalled on {wid} ({reason}), the run's stall {Stalls.Count}{(Stalls.Count >= 2 ? " - no more downloads in this run" : " - a fresh session for the next map")}");
        return Stalls.Count >= 2;
    }

    /// <summary>Forget the staged downloads that have a verdict (their folders and their acf entries); keep the rest. A pass the keep set holds
    /// - its score puts it among its mode's best now that it is listed, say, or an update of a map the server stores - is handed over instead
    /// (THE HAND-OVER) when the game server lacks it or stores an older copy than the tested one, so the server never downloads it again; when
    /// that fails it stays staged, and the next run's serve step hands it over without a download (or downloads it again when SteamCMD's acf
    /// does not name it).</summary>
    public void CleanupStaging()
    {
        if (!Directory.Exists(Content))
            return;
        var done = Directory.EnumerateFileSystemEntries(Content).Select(p => Path.GetFileName(p)!).Where(w => Node.Truthy(State.Item(w)?["verdict"])).ToList();
        if (done.Count == 0)
            return;
        if (Cfg.Handover && Cfg.Publish)
        {
            var keep = SafeKeepSet(KeepInputs(), Clock.Now());     // the lists as just published
            if (keep.Problems.Any(p => p.StartsWith("the keep set could not be computed", StringComparison.Ordinal)))
            {
                L("staging: the keep set could not be computed - nothing removed, so no map the keep set may want is lost");
                return;
            }
            var has = Handover.ServerMaps(Cfg.GameDir);
            var pending = Handover.Pending(Cfg.HandoverDir);
            var rep = ServeReport();
            foreach (var wid in done.ToList())
            {
                var rec = State.Item(wid) ?? [];
                var entry = has.ContainsKey(wid) ? Handover.AcfEntries(StagingAcf, wid) : null;   // the staged copy's version, as SteamCMD names it
                var stagedT = entry is { } e && Text.Digits(e.Installed.GetValueOrDefault("timeupdated")) ? long.Parse(e.Installed["timeupdated"], CultureInfo.InvariantCulture) : 0;
                var newer = has.TryGetValue(wid, out var stored) && stagedT > stored;
                if (Node.Str(rec["verdict"]) != "pass" || !keep.All.Contains(wid) || (has.ContainsKey(wid) && !newer) || pending.ContainsKey(wid) || Cfg.Exclude.Contains(wid))
                    continue;
                var (ok, why) = HandOverSafe(wid, rec["title"] is null ? null : Node.Str(rec["title"]));
                L($"hand-over {wid} {Text.Cut(Node.StrOr(rec["title"]), 40)} (tested, in the keep set{(newer ? ", newer than the server's copy" : "")}): {why}"
                  + (ok ? "" : " - it stays staged: the next run's serve hands it over"));
                if (!ok)
                    List(rep, "handover_failed").Add(Node.Array(wid, why));
                done.Remove(wid);                          // handed over, or kept for the next run - never deleted while the keep set wants it
                if (ok)
                {
                    Bump(rep, "tested", 1);
                    Event(wid, "handed-over", newer ? "Its update tested and handed over to the game server: it replaces the older copy." : "Tested and handed over to the game server.");
                }
            }
        }
        if (done.Count == 0)
            return;
        try
        {
            if (File.Exists(StagingAcf))
                Handover.AcfForget(StagingAcf, done);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            L($"staging: the acf was not edited ({e.Message}) - the folders stay");
            return;
        }
        foreach (var wid in done)
        {
            try
            {
                var path = Path.Combine(Content, wid);
                if (Files.IsLink(path))
                    File.Delete(path);
                else
                    Directory.Delete(path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        L($"staging: {done.Count} decided item(s) removed");
    }
}
