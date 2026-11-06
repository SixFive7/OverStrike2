using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>THE SERVICE (os2-sync serve): no time windows - a run starts whenever the game server has had nobody on for sync.quietMinutes and
/// there is work (maps waiting for a download or a test, or the API steps due), lasts sync.runHours, and is cut short before the game
/// server's daily restart. After a run that did nothing it waits (IdleUntil). &lt;state&gt;/run-now (any content, or JSON with lower
/// limits; {"api": true} runs the API steps whatever their cadence) starts a manual run at once.</summary>
public sealed class Service(SyncConfig cfg, SyncClock clock, Seams seams, SyncLog log, WebApiDoor door)
{
    public const double IdleWait = 1800;             // after a run that did nothing, the service waits at least this long before it looks again
    public const double MinRun = 1200;               // no automatic run starts with less than this before its cut: a run of minutes does nothing
    public const double Loop = 30;

    /// <summary>Anything for a day's run to do: an item still to download, or one staged without a verdict.</summary>
    public static bool BacklogWaits(JsonObject state)
    {
        foreach (var (_, node) in Node.Obj(state["items"]))
        {
            if (node is not JsonObject rec)
                continue;
            var status = Node.Str(rec["status"]);
            if (status is "queued" or "retry" or "downloaded" || (status == "tested" && !Node.Truthy(rec["verdict"])))
                return true;
        }
        return false;
    }

    /// <summary>A run's end, were it to start now: sync.runHours on, cut short 15 minutes before the game server's daily restart, so the test
    /// engine never runs through it.</summary>
    public static double RunDeadline(SyncConfig cfg, SyncClock clock, double? now = null)
    {
        var t = now ?? clock.Now();
        var end = t + cfg.RunHours * 3600.0;
        if (cfg.DailyRestart is { } daily)
            foreach (var day in new[] { 0, 1 })
            {
                var at = clock.AtTime(daily, t + day * 86400.0) - 900;
                if (t < at && at < end)
                    end = at;
            }
        return end;
    }

    /// <summary>Why no run may start now by the clock, or null: the daily restart's stretch (15 minutes before it to 10 after), and a run that
    /// would have under MinRun before its cut - a run of a few minutes only writes a report.</summary>
    public static string? StartBlocked(SyncConfig cfg, SyncClock clock, double? now = null)
    {
        var t = now ?? clock.Now();
        if (cfg.DailyRestart is { } daily)
        {
            var at = clock.AtTime(daily, t);
            if (at - 900 <= t && t < at + 600)
                return "the game server's daily restart";
        }
        var end = RunDeadline(cfg, clock, t);
        if (end - t < MinRun)
            return $"a run now would have {Text.Hms(end - t)} before the game server's daily restart cuts it short";
        return null;
    }

    /// <summary>A run has work: maps to download or test (and Steam has not stopped today's downloads), or discovery and the check are due.</summary>
    public static bool WorkWaits(SyncConfig cfg, SyncClock clock, JsonObject state)
    {
        var book = Node.Obj(Node.Get(state["downloads"], clock.Day()));
        if (BacklogWaits(state) && !Node.Truthy(book["stop"]))
            return true;
        return clock.Now() - Node.Float(state["last_api"]) >= cfg.ApiEvery;
    }

    /// <summary>When the service looks again after a run that did nothing. Something stopped it - a join, the deadline, a failed download, a
    /// publish that had to wait: it tries again after IdleWait. Nothing stopped it: it had nothing it could do, because what still waits cannot
    /// be downloaded today (the brake) or at all (gone from the Workshop). Only the next check or a new day can change that, so it looks again
    /// then, not every IdleWait for a run that writes a report and nothing else.</summary>
    public static double IdleUntil(SyncConfig cfg, SyncClock clock, JsonObject report, JsonNode? lastApi)
    {
        var now = clock.Now();
        var stopped = new[] { "downloads", "tests", "serve" }.Any(step => Node.Truthy(Node.Get(report[step], "stop")) || Node.Truthy(Node.Get(report[step], "failed")))
                      || Node.Truthy(Node.Get(report["tests"], "waiting_verdict")) || Node.Truthy(Node.Get(report["published"], "skipped"));
        if (stopped)
            return now + IdleWait;
        return Math.Max(now + IdleWait, Math.Min(Node.Float(lastApi) + cfg.ApiEvery, clock.AtTime(new TimeOfDay(0, 0), now + 86400)));
    }

    SyncRun NewRun(SyncConfig runCfg, bool manual, Status status) => new(runCfg, clock, seams, log, manual, status: status, door: door);

    /// <summary>The service's loop, until it is told to stop (StopRequested out of a wait).</summary>
    public void Serve()
    {
        Directory.CreateDirectory(cfg.StateDir);
        log.File = Path.Combine(cfg.StateDir, "sync.log");
        log.Line($"service start (version {Program.Version}): no time windows - a run whenever nobody plays and there is work, at most {cfg.RunHours} h each, never "
                 + $"around the {cfg.DailyRestart?.ToString() ?? "(off)"} restart; {(cfg.Enabled ? "enabled" : "DISABLED (sync.enabled false)")}, "
                 + $"Web API key {(cfg.WebApiKey.Length > 0 ? "present" : "absent")}, caps: {cfg.ApiRunCap} calls a run, {cfg.ApiDayCap} in 24 h, discovery and the check every "
                 + $"{Text.Hms(cfg.ApiEvery)}; downloads: no daily cap, Steam's push-back stops them, a map at most {DownloadBudget.DownloadsPerMapDay} times a day");
        foreach (var w in cfg.BootWarnings)
            log.Line("WARNING: " + w);
        var started = clock.Now();
        var seen = started;                           // when the server last showed somebody (or did not answer); a start cannot know who just left, so it counts
        var idleUntil = 0.0;
        string? said = null;
        var status = new Status(cfg, clock, log.Line);
        (string State, string Why)? shown = null;
        var shownAt = 0.0;

        void Show(string state, string why)
        {
            var changed = shown != (state, why);
            if (!changed && clock.Now() - shownAt < Status.IdleSeconds)
                return;
            try
            {
                var n = NewRun(cfg, false, status);
                n.State.Load();
                n.Ledger.Load();
                status.Rebuild(n, changed, Status.IdleHeavy);
            }
            catch (Exception e) when (e is not StopRequested)
            {
                log.Line($"status: the idle picture not rebuilt ({Text.Of(e)})");
            }
            status.Set(("state", state), ("why", why), ("map", null), ("step", null), ("run", null));
            status.Write();
            (shown, shownAt) = ((state, why), clock.Now());
        }

        while (true)
        {
            var trigger = Path.Combine(cfg.StateDir, "run-now");
            if (File.Exists(trigger))
            {
                var overrides = Node.ReadJson(trigger) as JsonObject ?? [];
                try
                {
                    File.Delete(trigger);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
                var runCfg = cfg.Clone();
                runCfg.Lower(overrides);
                log.Line($"run-now: a manual run ({SyncConfig.Describe(overrides)})");
                try
                {
                    NewRun(runCfg, true, status).Run();
                }
                catch (Exception e) when (e is not StopRequested)   // the service must outlive any one run
                {
                    log.Line($"the manual run failed: {Text.Of(e)}\n{e}");
                    log.Line($"mail {Mail.MailCrash(cfg, clock, e, true, seams.SendMail, log.Line)}");
                }
                shown = null;
            }
            else if (!cfg.Enabled)
                Show("stopped", "The sync is switched off.");
            else if (clock.Now() < idleUntil)
                Show("idle", $"The last run found nothing to do: the sync looks again at {clock.Hm(idleUntil)}.");
            else
            {
                var why = StartBlocked(cfg, clock);
                var state = "waiting";
                string? words = null;
                if (why is not null)
                    words = "The server's daily restart is near: the sync waits until it is over.";
                var h = seams.HumansOn(cfg, clock);
                var flag = File.Exists(Path.Combine(cfg.BridgeData, "maintenance"));
                if (h is null or > 0 || flag)
                    seen = clock.Now();
                var minutes = (long)(cfg.Quiet / 60);
                if (why is null && h is null)
                    (why, words) = ("the game server does not answer A2S", "The game server does not answer, so the sync waits.");
                else if (why is null && h > 0)
                    (why, words) = ($"{h} human(s) on the game server", $"Somebody is playing: the sync waits until the server has been empty for {minutes} minutes.");
                else if (why is null && clock.Now() - seen < cfg.Quiet)
                    (why, words) = seen == started
                        ? ($"the service started {Text.Hms(clock.Now() - started)} ago, and it cannot know who just left", $"The sync has just started: it waits until the server has been empty for {minutes} minutes.")
                        : ($"somebody was on the server {Text.Hms(clock.Now() - seen)} ago (or the maintenance flag)",
                           flag ? "The server is in maintenance, so the sync waits." : $"Somebody played a moment ago: the sync waits until the server has been empty for {minutes} minutes.");
                var st = Node.ReadJson(Path.Combine(cfg.StateDir, "state.json")) as JsonObject ?? [];
                if (why is null && !WorkWaits(cfg, clock, st))
                {
                    why = "nothing to do: no backlog, discovery and the check not due";
                    var book = Node.Obj(Node.Get(st["downloads"], clock.Day()));
                    var due = clock.Hm(Node.Float(st["last_api"]) + cfg.ApiEvery);
                    if (BacklogWaits(st) && Node.Truthy(book["stop"]))
                        (state, words) = ("stopped", $"Steam asked for a pause today ({Text.Clean(Node.Str(book["stop"]), 120)}): the downloads go on tomorrow; the next Workshop check is at {due}.");
                    else
                        (state, words) = ("idle", $"Nothing to do: no maps are waiting, and the next Workshop check is at {due}.");
                }
                if (why is not null)
                {
                    if (why != said)
                        log.Line($"not starting a run: {why}");
                    said = why;
                    Show(state, words ?? why);
                }
                else
                {
                    said = null;
                    try
                    {
                        var run = NewRun(cfg.Clone(), false, status);
                        var report = run.Run();
                        if (Node.Truthy(report["idle"]))
                            idleUntil = IdleUntil(cfg, clock, report, run.State.Data["last_api"]);
                    }
                    catch (Exception e) when (e is not StopRequested)
                    {
                        log.Line($"the run failed: {Text.Of(e)}\n{e}");
                        log.Line($"mail {Mail.MailCrash(cfg, clock, e, false, seams.SendMail, log.Line)}");
                        idleUntil = clock.Now() + IdleWait;
                    }
                    shown = null;
                }
            }
            clock.Sleep(Loop);
        }
    }

    /// <summary>os2-sync once: one run now, then exit (the same rules), with a run-now's lower limits.</summary>
    public JsonObject Once(JsonObject overrides)
    {
        Directory.CreateDirectory(cfg.StateDir);
        log.File = Path.Combine(cfg.StateDir, "sync.log");
        var runCfg = cfg.Clone();
        runCfg.Lower(overrides);
        foreach (var w in cfg.BootWarnings)
            log.Line("WARNING: " + w);
        return NewRun(runCfg, true, new Status(runCfg, clock, log.Line)).Run();
    }

    /// <summary>os2-sync status: the ledger's counters and the state's totals.</summary>
    public string StatusText()
    {
        var ledger = new ApiLedger(Path.Combine(cfg.StateDir, "api-ledger.json"), cfg.ApiDayCap, clock);
        ledger.Load();
        var st = new SyncState(Path.Combine(cfg.StateDir, "state.json")).Load();
        var counts = new JsonObject();
        foreach (var (_, node) in st.Items)
        {
            var status = Node.Str(Node.Get(node, "status"));
            counts[status] = Node.Int(counts[status]) + 1;
        }
        return Node.Indented(new JsonObject
        {
            ["api_last_24h"] = ledger.Data is not null ? ledger.Last24h() : null, ["api_today"] = ledger.Count(clock.Day()),
            ["api_stop_today"] = ledger.Data is not null ? ledger.Stopped(clock.Day()) : null, ["items"] = counts, ["last_run"] = st.Data["last_run"]?.DeepClone(),
            ["last_api"] = st.Data["last_api"]?.DeepClone(), ["key"] = cfg.WebApiKey.Length > 0 ? "present" : "absent",
        }, 1);
    }

    /// <summary>os2-sync export: what this sync has published, as entries a republish from the repo can merge (id, rank, name, subscriptions,
    /// lifetime, sizeMb, updated, tags, description, preview), from the bridge's files and state.json, and the random queues as queues.json
    /// holds them ("queues": version 1, its lanes under "random" and "requested"; null without the file), which the HUD's pictures take first.
    /// The server's data is ahead of the repo after a run that published; this is the way back.</summary>
    public string Export()
    {
        var st = new SyncState(Path.Combine(cfg.StateDir, "state.json")).Load();
        var files = BridgeData.Read(cfg.BridgeData);
        var mine = new HashSet<string>(st.Items.Where(kv => Node.Str(Node.Get(kv.Value, "status")) == "published").Select(kv => kv.Key));
        JsonObject Entry(JsonObject m, JsonObject rec) => new()
        {
            ["id"] = Node.Int(m["id"]), ["rank"] = m["rank"]?.DeepClone(), ["name"] = m["name"]?.DeepClone(), ["sizeMb"] = m["sizeMb"]?.DeepClone(),
            ["subscriptions"] = rec["subs"]?.DeepClone(), ["lifetime"] = rec["lifetime"]?.DeepClone(),
            ["updated"] = DateTimeOffset.FromUnixTimeSeconds(Node.Int(rec["updated"])).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["tags"] = Node.Truthy(rec["tags"]) ? rec["tags"]!.DeepClone() : new JsonArray(), ["description"] = Node.StrOr(rec["description"]), ["preview"] = Node.StrOr(rec["preview"]),
        };
        var maps = new JsonArray();
        foreach (var m in files.Maps.OfType<JsonObject>().Where(m => mine.Contains(Node.Str(m["id"]))))
            maps.Add(Entry(m, st.Item(Node.Str(m["id"])) ?? []));
        var modes = new JsonObject();
        foreach (var (mode, entries) in files.ModeLists)
            modes[mode] = new JsonArray(entries.OfType<JsonObject>().Where(m => mine.Contains(Node.Str(m["id"]))).Select(m => (JsonNode?)Entry(m, st.Item(Node.Str(m["id"])) ?? [])).ToArray());
        var broken = new JsonArray();
        foreach (var section in new[] { "maps", "packs" })
            foreach (var e in Node.Arr(files.Broken[section]).OfType<JsonObject>().Where(e => mine.Contains(Node.Str(e["id"]))))
            {
                var copy = (JsonObject)e.DeepClone();
                copy["section"] = section;
                broken.Add(copy);
            }
        var queues = Node.ReadJson(Path.Combine(cfg.BridgeData, "queues.json")) as JsonObject;   // the random queues, as the bridge wrote them
        return Node.Indented(new JsonObject
        {
            ["generated"] = clock.Stamp(), ["maps.json"] = maps, ["modes"] = modes, ["broken.json"] = broken, ["queues"] = queues?.DeepClone(),
        }, 1);
    }
}
