using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>ONE RUN of the sync: discovery, the check and the ratings (when due), the hand-over, then downloads, tests and the publish
/// in batches until the backlog or the run ends, or Steam pushes back; its report, its mail (problems only) and its line in the history.</summary>
public sealed partial class SyncRun
{
    public const int BackupsKept = 10;               // the newest publishes' copies of the bridge's lists, about 1 MB each, on the backed-up volume
    public const int ReportsKept = 100;              // the newest runs' reports, each with its text
    public const int QueueCheck = 3000;              // the check asks the Workshop about this many of the download queue's head
    public const double RatingsEvery = 20 * 3600;    // the ratings are fetched again once this old: once a day, by whichever run comes then
    public const int TrendDays = 30;                 // Most Popular's window: the trend over the last 30 days
    public const int TrendPages = 20;                // pages of 100 a mode's Most Popular walk reads at most...
    public const int TrendWant = 150;                // ...stopping once this many of the mode's listed maps were in it (100 stay, the rest is slack)
    public const double Day = 86400;

    public SyncConfig Cfg { get; }
    public SyncClock Clock { get; }
    public bool Manual { get; }
    public Seams Seams { get; }
    public Status Status { get; }
    public SyncLog Log { get; }
    public ApiLedger Ledger { get; }
    public SyncState State { get; }
    public JsonObject Report { get; }
    public string Day0 { get; }
    public double T0 { get; }
    public double Deadline { get; set; }
    public double? HumanAt { get; set; }             // when this run last saw somebody on the game server (or could not tell)
    public int EngineStarts { get; set; }            // the test engine's starts this run, over all its batches
    public List<(string Id, long Stalls)> Stalls { get; } = [];   // every SteamCMD stall in this run: one is routine, two stop the downloads
    public double Worked { get; set; }               // seconds spent on downloads and tests, for the pace in the mail
    public int Decided { get; set; }                 // items decided in that time
    public ITestEngine? Engine { get; private set; }
    public List<(string Id, string Verdict)> VerdictsToReport { get; set; } = [];   // this run's check of every listed map it answered
    public JsonObject? Backlog { get; private set; }
    public SteamApi? Api { get; private set; }
    public HashSet<string> QTop { get; private set; } = [];
    public HashSet<string> QFresh { get; private set; } = [];
    public HashSet<string> QChanged { get; private set; } = [];
    public int AcfWait { get; set; } = 20;           // seconds serve waits for SteamCMD's acf entry of a download (it came 1.0-1.4 s after Success)
    public Dictionary<string, string> ListedMode { get; set; } = [];
    public Dictionary<string, long> ListedSize { get; set; } = [];
    public Dictionary<string, string> ListedName { get; set; } = [];
    (string Phase, string Why) phaseState = ("waiting", Status.PhaseWhy["start"]);
    (double At, Dictionary<string, (double? Score, long? Subs, HashSet<string> Popular)> Cut)? cut;
    readonly Action<double> sleep;
    readonly WebApiDoor door;

    /// <param name="door">The program's one Web API door; a test's run gets its own.</param>
    /// <param name="sleep">The waits for an empty server (a test's moves its clock).</param>
    public SyncRun(SyncConfig cfg, SyncClock clock, Seams seams, SyncLog log, WebApiDoor door, bool manual = false, Action<double>? sleep = null, Status? status = null)
    {
        Cfg = cfg;
        Clock = clock;
        Seams = seams;
        Log = log;
        Manual = manual;
        this.sleep = sleep ?? (s => clock.Sleep(s));
        this.door = door;
        Status = status ?? new Status(cfg, clock, log.Line);
        Day0 = clock.Day();
        T0 = clock.Now();
        Directory.CreateDirectory(cfg.StateDir);
        Directory.CreateDirectory(Path.Combine(cfg.StateDir, "reports"));
        Directory.CreateDirectory(cfg.WorkDir);
        log.File ??= Path.Combine(cfg.StateDir, "sync.log");
        Deadline = Service.RunDeadline(cfg, clock);
        Report = new JsonObject
        {
            ["day"] = Day0, ["manual"] = manual, ["started"] = clock.Stamp(), ["started_hm"] = clock.Hm(),
            ["errors"] = new JsonArray(), ["waited"] = new JsonArray(),
        };
        Ledger = new ApiLedger(Path.Combine(cfg.StateDir, "api-ledger.json"), cfg.ApiDayCap, clock);
        State = new SyncState(Path.Combine(cfg.StateDir, "state.json"));
    }

    void L(string message) => Log.Line(message);

    JsonArray Errors => (JsonArray)Report["errors"]!;

    JsonObject Section(string name, Func<JsonObject> make)
    {
        if (Report[name] is JsonObject o)
            return o;
        var fresh = make();
        Report[name] = fresh;
        return fresh;
    }

    string Staging => Path.Combine(Cfg.WorkDir, "staging");
    string Content => WorkshopPaths.Content(WorkshopPaths.OfSteamCmd(Staging));
    string StagingAcf => WorkshopPaths.Acf(WorkshopPaths.OfSteamCmd(Staging));

    /// <summary>The keep set (core's, the restart watcher's too), with the config's rules, and never an exception: an input of a shape it does
    /// not read gives an empty set whose problems say why - nothing is handed over or deleted on it.</summary>
    public KeepSetResult SafeKeepSet(IReadOnlyDictionary<string, JsonNode?> inputs, double now, string hud = "") =>
        KeepSet.Safe(inputs, UnixTime.At(now), hud, L, Cfg.KeepRules);

    public Dictionary<string, JsonNode?> KeepInputs() => KeepSet.Inputs(Cfg.BridgeData);

    public void Phase(string name)
    {
        Report["phase"] = name;
        L($"phase: {name}");
        phaseState = (Status.PhaseState.GetValueOrDefault(name, "waiting"), Status.PhaseWhy.GetValueOrDefault(name, "Working."));
        Status.Set(("state", phaseState.Phase), ("why", phaseState.Why), ("map", null), ("step", null));
        Status.Rebuild(this, true);
        Status.Write();
    }

    public double Left() => Deadline - Clock.Now();

    /// <summary>The status file: the map this step is on now, and how far the step is.</summary>
    public void OnMap(string wid, int done, int total)
    {
        var rec = State.Item(wid) ?? [];
        var name = Text.Clean(Node.StrOr(rec["title"]), 80);
        Status.Set(("map", new JsonObject
        {
            ["id"] = wid, ["name"] = name.Length > 0 ? name : ListedName.GetValueOrDefault(wid, ""), ["mode"] = Status.ListMode(rec, ListedMode, wid),
            ["sizeMb"] = (Node.Int(rec["size"]) is var size && size != 0 ? size : ListedSize.GetValueOrDefault(wid)) >> 20,
        }), ("step", new JsonObject { ["done"] = done, ["total"] = total }));
        Status.Rebuild(this);
        Status.Write();
    }

    /// <summary>A map's event for the status file's history (listed, broken, removed, changed, handed-over, failed), kept in the state.</summary>
    public void Event(string wid, string evt, string why)
    {
        try
        {
            var hist = State.Section("history");
            if (hist["maps"] is not JsonArray maps)
            {
                maps = [];
                hist["maps"] = maps;
            }
            var rec = State.Item(wid) ?? [];
            var name = Text.Clean(Node.StrOr(rec["title"]), 80);
            maps.Add(new JsonObject
            {
                ["at"] = (long)Math.Truncate(Clock.Now()), ["id"] = wid, ["name"] = name.Length > 0 ? name : ListedName.GetValueOrDefault(wid, ""),
                ["mode"] = Status.ListMode(rec, ListedMode, wid), ["event"] = evt, ["why"] = Text.Clean(why, 200),
            });
            while (maps.Count > Status.Maps)
                maps.RemoveAt(0);
        }
        catch (Exception e) when (e is not StopRequested)
        {
            L($"status: an event of {wid} not kept ({Text.Of(e)})");
        }
    }

    /// <summary>Humans on the game server by A2S (null: it did not answer, which counts as somebody); remembers when it last saw any.</summary>
    public int? Humans()
    {
        var h = Seams.HumansOn(Cfg, Clock);
        if (h is null or > 0)
            HumanAt = Clock.Now();
        return h;
    }

    /// <summary>True once the game server shows 0 humans (by A2S) - and has for sync.quietMinutes, if this run saw anybody: a map change shows
    /// 0 for a moment. False if the deadline comes first.</summary>
    public bool WaitForNobody(string what, double margin = 0)
    {
        var t = Clock.Now();
        var said = false;
        while (true)
        {
            var h = Humans();
            var flag = File.Exists(Path.Combine(Cfg.BridgeData, "maintenance"));
            var quiet = HumanAt is null || Clock.Now() - HumanAt >= Cfg.Quiet;
            if (h == 0 && !flag && quiet)
            {
                if (said)
                {
                    ((JsonArray)Report["waited"]!).Add($"{what} for {Text.Hms(Clock.Now() - t)} (somebody on the server, or the maintenance flag)");
                    Status.Set(("state", phaseState.Phase), ("why", phaseState.Why));
                    Status.Write();
                }
                return true;
            }
            if (!said)
            {
                L($"{what}: waiting - " + (h is null ? "the game server does not answer A2S" : h > 0 ? $"{h} human(s) on the game server"
                    : flag ? "the bridge's maintenance flag is set" : $"somebody was on the server {Text.Hms(Clock.Now() - HumanAt!.Value)} ago"));
                said = true;
                Status.Set(("state", "waiting"), ("why", Text.WhyWaiting(h, flag, Cfg.Quiet)));
                Status.Write();
            }
            if (Left() <= margin + 60)
            {
                ((JsonArray)Report["waited"]!).Add($"{what}: gave up at the deadline ({(h is null ? "nobody answered" : $"{h} human(s)")})");
                return false;
            }
            sleep(60);
        }
    }

    // -------------------------------------------------------------------------------------------------------------- the steps

    /// <summary>One run, with the status file kept up to date: a ticker writes it every TickSeconds until the run ends.</summary>
    public JsonObject Run()
    {
        Status.Set(("run", new JsonObject { ["started"] = (long)Math.Truncate(T0), ["deadline"] = (long)Math.Truncate(Deadline), ["kind"] = Manual ? "manual" : "auto" }),
                   ("map", null), ("step", null));
        Status.StartTicker();
        try
        {
            return RunInner();
        }
        finally
        {
            Status.StopTicker();
            Status.Set(("state", "idle"), ("why", $"The run that started at {Clock.Hm(T0)} has ended."), ("map", null), ("step", null), ("run", null));
            Status.Rebuild(this, true);
            Status.Write();
        }
    }

    JsonObject RunInner()
    {
        string? unreadable = null;
        try
        {
            Ledger.Load();
            State.Load();
        }
        catch (Exception e) when (e is BadDataException or IOException or UnauthorizedAccessException)
        {
            unreadable = e.Message;
        }
        Phase("start");                                     // after the load: the run's first picture on the dashboard has its totals
        if (unreadable is not null)
        {
            Errors.Add(unreadable);
            Finish();
            return Report;
        }
        if (Ledger.Broken is { } broken)
            Errors.Add(broken);
        BridgeData files;
        try
        {
            files = BridgeData.Read(Cfg.BridgeData);
        }
        catch (Exception e) when (e is BadDataException or IOException or UnauthorizedAccessException)
        {
            Errors.Add($"the bridge's data cannot be read ({e.Message}) - nothing done in this run");
            Finish();
            return Report;
        }
        RequeueLostDownloads();
        var known = files.KnownIds(Cfg);
        var listed = files.Listed();
        L($"start: {listed.Count} listed map(s), {known.Count} id(s) known, {State.Items.Count} item(s) in the state, deadline {Clock.Hm(Deadline)}{(Manual ? " (manual run)" : "")}");
        TakeHandVerdicts(known);                            // <state>/adopt.json (RunAdopt.cs): published below, like the sync's own verdicts
        var csgoEra = CsgoEraVerdicts(known);               // the CS:GO-era uploads skipped before they had a verdict (RunQueue.cs): published below too
        if (csgoEra > 0)
            Report["csgo_era"] = csgoEra;
        var key = Cfg.WebApiKey.Length > 0 ? Cfg.WebApiKey : null;
        var api = new SteamApi(Ledger, Day0, door, key, SteamApi.HttpSend, s => Clock.Sleep(s), L, Cfg.ApiRunCap);
        Api = api;
        var lastApi = Node.Float(State.Data["last_api"]);
        var apiDue = (Cfg.ForceApi || Clock.Now() - lastApi >= Cfg.ApiEvery || !Service.BacklogWaits(State.Data)) && !Cfg.SkipApi;
        var details = new Dictionary<string, JsonObject>();
        var removedNow = new List<(string Id, string Verdict)>();
        if (!apiDue)
        {
            var why = $"the last ran at {Clock.Hm(lastApi)}; again after {Text.Hms(Cfg.ApiEvery)}, or once nothing waits";
            foreach (var step in new[] { "discovery", "check", "ratings" })
                Report[step] = new JsonObject { ["skipped"] = why };
            L($"discovery, the check and the ratings: not this run - {why}");
        }
        else
        {
            Phase("discovery");
            DiscoverStep(api, known);
            Phase("check");
            (details, removedNow) = CheckStep(api, files, known, listed);
            // the ratings: the Workshop's score of every listed map and of every map waiting for its test, once a day
            Phase("ratings");
            Ratings(api, listed);
            if (api.StopReason is { } r2)
                Report["api_stop"] = r2;
            State.Data["last_api"] = Clock.Now();
            State.Save();
        }
        var stopped = api.StopReason ?? (Ledger.Data is not null ? Ledger.Stopped(Day0) : null);
        if (stopped is not null && Issues.PushbackRx().IsMatch(stopped))   // Steam said stop: no downloads either, today
            new DownloadBudget(State, Day0).RefuseForToday($"the Web API answered {Issues.ShortStop(stopped)}");
        // THE HAND-OVER: the keep set's maps the game server lacks or stores in an older version, handed over before any test
        Phase("serve");
        Serve();
        // downloads, tests and the publish, in batches: a long run publishes as it goes, and staging stays small
        var first = true;
        while (true)
        {
            var tBatch = Clock.Now();
            Phase("downloads");
            var got = Download(known);
            Phase("tests");
            var outcomes = ScanAndTest();
            Worked += Clock.Now() - tBatch;
            Decided += outcomes.Count;
            // this batch's verdicts and any earlier one that could not be published then; and the CS:GO-era uploads the queue left out, skipped
            // with their verdict (RunQueue.cs)
            Phase("publish");
            var decided = new HashSet<string>(outcomes.Select(o => o.Id));
            foreach (var (wid, node) in State.Items)
                if (!decided.Contains(wid) && node is JsonObject rec && Node.Truthy(rec["verdict"])
                    && (Node.Str(rec["status"]) is "tested" or "downloaded" || (Node.Str(rec["status"]) == "skipped" && Verdicts.FromWorkshopFile(rec))))
                    outcomes.Add(new Verdicts.Outcome(wid, Node.Str(rec["verdict"]), Node.Obj(rec["detail"]), rec));
            var before = Report["published"] as JsonObject;
            before = (JsonObject?)before?.DeepClone();
            Publish(outcomes, first ? removedNow : [], details);
            Report["published"] = RunReport.MergePublished(before, Report["published"] as JsonObject)?.DeepClone();
            VerdictsToReport = [];                        // the check's verdicts go to the bridge once a run
            first = false;
            CleanupStaging();
            JsonObject dl = Node.Obj(Report["downloads"]), t = Node.Obj(Report["tests"]);
            if (got == 0 || Node.Truthy(dl["stop"]) || Node.Truthy(t["stop"]) || Left() < 300)
                break;
            try
            {
                known = BridgeData.Read(Cfg.BridgeData).KnownIds(Cfg);
            }
            catch (Exception e) when (e is BadDataException or IOException or UnauthorizedAccessException)
            {
                // the publish step says so; the known ids of the last batch still hold
            }
        }
        Finish();
        return Report;
    }

    /// <summary>A map that waits for its test with its download gone from the staging folder can never get a verdict: the work folder is disposable,
    /// and a hand-over can take a staged map. It is downloaded again, or it would count as waiting for good. One with a verdict waits only for its
    /// publish (the step after the tests takes it), which needs no folder.</summary>
    public void RequeueLostDownloads()
    {
        var back = new List<string>();
        foreach (var (wid, node) in State.Items)
        {
            if (node is not JsonObject rec || Node.Truthy(rec["verdict"]) || Node.Str(rec["status"]) is not ("downloaded" or "tested") || Staged(wid))
                continue;
            rec["status"] = "queued";
            back.Add(wid);
        }
        if (back.Count > 0)
        {
            State.Save();
            L($"queued again: {back.Count} map(s) waiting for their test with their download gone from the staging folder");
        }
    }

    static void Bump(JsonObject o, string key, long by) => o[key] = Node.Int(o[key]) + by;

    static JsonArray List(JsonObject o, string key)
    {
        if (o[key] is not JsonArray a)
        {
            a = [];
            o[key] = a;
        }
        return a;
    }

    /// <summary>Seconds a map took this run, download and test together; null until enough were decided to say.</summary>
    public double? Pace() => Decided >= 20 ? Worked / Decided : null;

    public bool Staged(string wid) => Directory.Exists(Path.Combine(Content, wid));

    public void Limits(DownloadBudget? budget = null)
    {
        budget ??= new DownloadBudget(State, Day0);
        Report["limits"] = new JsonObject
        {
            ["api_run"] = Api?.Calls ?? 0, ["api_run_cap"] = Cfg.ApiRunCap, ["api_24h"] = Ledger.Data is not null ? Ledger.Last24h() : null,
            ["api_day_cap"] = Ledger.DayCap, ["api_gap"] = Cfg.ApiGap, ["dl_items"] = budget.Items, ["dl_bytes"] = budget.Bytes, ["dl_engine"] = budget.Engine,
            ["engine_starts"] = Engine?.Starts ?? 0, ["engine_starts_max"] = Cfg.EngineStartsMax,
        };
    }

    /// <summary>Only the newest ReportsKept of reports/&lt;day&gt;...json stay, with their .txt, the one just written among them. Every version's report
    /// starts with its day, whatever followed it then (-&lt;hhmm&gt;[-manual], -manual-&lt;hhmm&gt;, -day, nothing); anything else in the folder is not ours to
    /// delete. A failure here costs a little disk, never the run.</summary>
    void PruneReports(string folder, string own)
    {
        try
        {
            var old = Directory.EnumerateFiles(folder, "*.json").Select(p => Path.GetFileName(p)!)
                .Where(n => n != own && Regex.IsMatch(n, @"^\d{4}-\d{2}-\d{2}\b.*\.json$")).OrderByDescending(n => n, StringComparer.Ordinal).Skip(ReportsKept - 1).ToList();
            foreach (var name in old)
            {
                File.Delete(Path.Combine(folder, name));
                File.Delete(Path.Combine(folder, name[..^5] + ".txt"));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            L($"reports not pruned: {e.Message}");
        }
    }

    public void Finish()
    {
        try
        {
            Queue(new Dictionary<string, string>());   // the backlog as it is at the end of the run, for the mail
            Report["backlog"] = Backlog?.DeepClone();
            if (Backlog is not null && Node.Truthy(Backlog["days"]))
                Report["backlog_until"] = $" (until about {Clock.Day(Clock.Now() + Node.Int(Backlog["days"]) * Day)})";
        }
        catch (Exception e) when (e is not StopRequested)
        {
            L($"backlog not counted: {Text.Of(e)}");
        }
        Report["ended"] = Clock.Stamp();
        Report["ended_hm"] = Clock.Hm();
        if (!Report.ContainsKey("limits"))
        {
            try
            {
                Limits();
            }
            catch (Exception e) when (e is not StopRequested)
            {
            }
        }
        if (Ledger.Data is not null && Ledger.Stopped(Day0) is { } stop)
            Report["api_stop"] = stop;
        var path = Path.Combine(Cfg.StateDir, "reports", $"{Day0}-{Clock.Local(T0).ToString("HHmm", CultureInfo.InvariantCulture)}{(Manual ? "-manual" : "")}.json");
        Report["report_path"] = path;
        var (subject, body) = RunReport.Summary(Report, Cfg);
        try
        {
            Files.WriteAtomic(path, Node.File(Report, 1), FileModes.Private);
            Files.WriteAtomic(path[..^5] + ".txt", subject + "\n\n" + body + "\n", FileModes.Private);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            L($"report not written: {e.Message}");
        }
        PruneReports(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        var idle = !(Node.Truthy(Node.Get(Report["downloads"], "ok")) || Node.Truthy(Node.Get(Report["tests"], "rows")) || Node.Truthy(Node.Get(Report["published"], "files"))
                     || Node.Truthy(Report["errors"]) || Node.Truthy(Node.Get(Report["serve"], "ok")));
        Report["idle"] = idle;
        if (Posix.DiskFree(Cfg.GameDir) is { } free)       // the disk, and the game server's last deletion, for the problems below
        {
            var disk = new JsonObject { ["free"] = free };
            if (free < Cfg.DiskLow)
                disk["low"] = $"{Text.Mb(free)} free on the game's volume (the mark is {Text.Mb(Cfg.DiskLow)})";
            Report["disk"] = disk;
        }
        if (Node.ReadJson(Path.Combine(Cfg.WatcherDir, "keep.json")) is JsonObject wk && Node.Truthy(wk["problems"]) && Node.StrOr(wk["delete"], "on") != "off")
            Report["watcher_keep"] = new JsonObject
            {
                ["computed"] = Node.StrOr(wk["computed"], "?"), ["problems"] = new JsonArray(Node.Arr(wk["problems"]).Take(5).Select(p => p?.DeepClone()).ToArray()),
            };
        if (Cfg.Publish && !File.Exists(Path.Combine(Cfg.BridgeData, WorkshopFile)))
        {
            try                                             // never without one after a run: the bridge's stock waits for it
            {
                WriteWorkshop(BridgeData.Read(Cfg.BridgeData));
            }
            catch (Exception e) when (e is BadDataException or IOException or UnauthorizedAccessException)
            {
            }
        }
        var issues = Issues.Of(Report);
        Report["issues"] = Node.Strings(issues.Select(i => i.What));
        Report["mail"] = Issues.Mail(Cfg, Clock, issues, Report, Seams.SendMail, L);
        L($"mail {Node.Str(Report["mail"])}{(issues.Count > 0 ? "" : $" - this run's report: {path[..^5]}.txt")}");
        try                                                 // again, now with the mail's outcome in it
        {
            Files.WriteAtomic(path, Node.File(Report, 1), FileModes.Private);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        State.Data["last_run"] = new JsonObject { ["ended"] = Clock.Now(), ["did"] = idle ? 0 : 1 };
        try
        {
            var hist = State.Section("history");
            var started = (long)Math.Truncate(T0);
            var runs = Node.Arr(hist["runs"]).Where(e => Node.Float(Node.Get(e, "started")) != started).Select(e => e?.DeepClone()).ToList();
            runs.Insert(0, Status.RunEntry(Report, started, (long)Math.Truncate(Clock.Now())));
            hist["runs"] = new JsonArray(runs.Take(Status.Runs).ToArray());
        }
        catch (Exception e) when (e is not StopRequested)
        {
            L($"status: the run's history line not kept ({Text.Of(e)})");
        }
        try
        {
            State.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            L($"state not saved: {e.Message}");
        }
    }
}
