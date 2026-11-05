using System.Globalization;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>ONE RUN of the sync: discovery, the check and the ratings (when due), the hand-over (2c), then downloads, tests and the publish
/// in batches until the backlog or the run ends, or Steam pushes back; its report, its mail (problems only) and its line in the history.</summary>
public sealed class SyncRun
{
    public const int QueueCheck = 3000;              // the check (step 2) asks the Workshop about this many of the download queue's head
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

    /// <param name="sleep">The waits for an empty server (a test's moves its clock).</param>
    /// <param name="door">The program's one Web API door; a test's run gets its own.</param>
    public SyncRun(SyncConfig cfg, SyncClock clock, Seams seams, SyncLog log, bool manual = false, Action<double>? sleep = null, Status? status = null,
                   WebApiDoor? door = null)
    {
        Cfg = cfg;
        Clock = clock;
        Seams = seams;
        Log = log;
        Manual = manual;
        this.sleep = sleep ?? (s => clock.Sleep(s));
        this.door = door ?? new WebApiDoor(cfg.ApiGap, clock.Clock);
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
            ["day"] = Day0, ["manual"] = manual, ["started"] = clock.Stamp(), ["started_hm"] = clock.Hm(), ["notes"] = new JsonArray(),
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
    string Content => Path.Combine(Staging, "steamapps", "workshop", "content", "730");
    string StagingAcf => Path.Combine(Staging, "steamapps", "workshop", "appworkshop_730.acf");

    /// <summary>The keep set, and never an exception: an input of a shape it does not read gives an empty set whose problems say why -
    /// nothing is handed over or deleted on it.</summary>
    public KeepSetResult SafeKeepSet(IReadOnlyDictionary<string, JsonNode?> inputs, double now, string hud = "")
    {
        try
        {
            return Seams.KeepSet(inputs, SyncClock.At(now), hud);
        }
        catch (Exception e) when (e is not StopRequested)      // a reshaped bridge file must never crash a run
        {
            L($"keep set: not computed ({e.GetType().Name}: {e.Message}) - an input has a shape this code does not read; nothing is decided on it");
            return KeepSet.Empty($"{e.GetType().Name}: {e.Message}");
        }
    }

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
        var name = Text.Clean(Py.StrOr(rec["title"]), 80);
        Status.Set(("map", new JsonObject
        {
            ["id"] = wid, ["name"] = name.Length > 0 ? name : ListedName.GetValueOrDefault(wid, ""), ["mode"] = Status.ListMode(rec, ListedMode, wid),
            ["sizeMb"] = (Py.Int(rec["size"]) is var size && size != 0 ? size : ListedSize.GetValueOrDefault(wid)) >> 20,
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
            var name = Text.Clean(Py.StrOr(rec["title"]), 80);
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
            L($"status: an event of {wid} not kept ({Mail.Repr(e)})");
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
                var minutes = Py.D(Math.Floor(Cfg.Quiet / 60));
                L($"{what}: waiting - " + (h is null ? "the game server does not answer A2S" : h > 0 ? $"{h} human(s) on the game server"
                    : flag ? "the bridge's maintenance flag is set" : $"somebody was on the server {Text.Hms(Clock.Now() - HumanAt!.Value)} ago"));
                said = true;
                Status.Set(("state", "waiting"), ("why", h is null ? "The game server does not answer, so the sync waits."
                    : h > 0 ? $"Somebody is playing: the sync waits until the server has been empty for {minutes} minutes."
                    : flag ? "The server is in maintenance, so the sync waits."
                    : $"Somebody played a moment ago: the sync waits until the server has been empty for {minutes} minutes."));
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
        Phase("start");
        try
        {
            Ledger.Load();
            State.Load();
        }
        catch (Exception e) when (e is BadDataException or IOException or UnauthorizedAccessException)
        {
            Errors.Add(e.Message);
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
        RequeueSizeSkips();
        var known = files.KnownIds(Cfg);
        var listed = files.Listed();
        L($"start: {listed.Count} listed map(s), {known.Count} id(s) known, {State.Items.Count} item(s) in the state, deadline {Clock.Hm(Deadline)}{(Manual ? " (manual run)" : "")}");
        var key = Cfg.WebApiKey.Length > 0 ? Cfg.WebApiKey : null;
        var api = new SteamApi(Ledger, Day0, door, key, Seams.WebApi, s => Clock.Sleep(s), L, Cfg.ApiRunCap);
        Api = api;
        var lastApi = Py.Float(State.Data["last_api"]);
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
            // 1. discovery
            Phase("discovery");
            if (key is null)
            {
                L("discovery skipped: no Steam Web API key (sync.webApiKey) - the other steps run");
                Report["discovery"] = new JsonObject { ["skipped"] = "no Web API key in the secret file" };
            }
            else
            {
                try
                {
                    var d = Discovery.Discover(api, Cfg, known, State, Day0, Deadline - 1800, Clock.Now);
                    Report["discovery"] = d;
                    L($"discovery: {Py.Str(d["pages"])} page(s), {Py.Str(d["seen"])} seen, {Py.Arr(d["new"]).Count} new ({Py.Str(d["stop"])})");
                }
                catch (StopApiException stop)
                {
                    Report["discovery"] = new JsonObject { ["skipped"] = stop.Message };
                    L($"discovery stopped: {stop.Message}");
                }
                catch (ApiErrorException e)
                {
                    Report["discovery"] = new JsonObject { ["skipped"] = $"failed: {e.Message}" };
                }
                State.Save();
            }
            // 2. the check: the listed maps, then the head of the download queue
            Phase("check");
            var queueHead = Queue(known, details).Take(QueueCheck).ToList();
            var ids = listed.Concat(queueHead).Distinct().ToList();
            var chk = new JsonObject { ["asked"] = ids.Count };
            try
            {
                var (got, left) = Seams.Check(api, ids, Deadline - 1500, Clock.Now, L);
                details = got;
                chk["calls"] = api.ByApi.GetValueOrDefault("GetPublishedFileDetails");
                chk["left"] = left.Count;
            }
            catch (StopApiException stop)
            {
                chk["skipped"] = stop.Message;
                L($"check stopped: {stop.Message}");
            }
            var judged = details.Count > 0 ? Discovery.JudgeListed(details, listed, State, Cfg) : ([], [], listed.Count, false);
            removedNow = judged.Suspicious ? [] : [.. judged.Removed];
            // the bridge's verdicts so far (removed.json is this sync's, kept by the bridge): a map that goes is an event once
            var bridgeVerdicts = Py.Get(Py.ReadJson(Path.Combine(Cfg.BridgeData, "removed.json")), "verdicts") as JsonObject ?? [];
            var newly = removedNow.Where(rv => !(bridgeVerdicts[rv.Id] is { } v && Py.IsString(v) && Py.Str(v) == rv.Verdict)).ToList();
            chk["removed"] = new JsonArray(removedNow.Select(rv => (JsonNode?)Py.Array(rv.Id, rv.Verdict)).ToArray());
            chk["changed"] = Py.Strings(judged.Changed);
            chk["suspicious"] = judged.Suspicious;
            if (!judged.Suspicious)
                VerdictsToReport = listed.Where(details.ContainsKey).Select(w => (w, Text.WorkshopVerdict(details[w]))).ToList();
            Report["check"] = chk;
            foreach (var wid in judged.Changed)
            {
                if (details.TryGetValue(wid, out var item))
                {
                    State.Remember(item, Day0, "changed");
                    Event(wid, "changed", "It changed on the Workshop: it is tested again.");
                }
            }
            foreach (var (wid, verdict) in newly)         // one event when a map goes, not one at every check
                Event(wid, "removed", $"The Workshop says it is {(verdict == "removed" ? "gone" : verdict)}: it is blocked.");
            foreach (var wid in queueHead)
                if (details.TryGetValue(wid, out var item))
                    State.Remember(item, Day0, Py.StrOr(State.Item(wid)?["why"], "new"));
            State.Save();
            L($"check: {ids.Count} asked, {judged.Removed.Count} gone ({newly.Count} newly), {judged.Changed.Count} changed{(judged.Suspicious ? " - NOT BELIEVED (over 10%)" : "")}");
            if (api.StopReason is { } r1)
                Report["api_stop"] = r1;
            // 2b. the ratings: the Workshop's score of every listed map and of every map waiting for its test, once a day
            Phase("ratings");
            Ratings(api, listed);
            if (api.StopReason is { } r2)
                Report["api_stop"] = r2;
            State.Data["last_api"] = Clock.Now();
            State.Save();
        }
        var stopped = api.StopReason ?? (Ledger.Data is not null ? Ledger.Stopped(Day0) : null);
        if (stopped is not null && Mail.PushbackRx().IsMatch(stopped))   // Steam said stop: no downloads either, today
            new DownloadBudget(State, Day0).RefuseForToday($"the Web API answered {Mail.ShortStop(stopped)}");
        // 2c. THE HAND-OVER: the keep set's maps the game server lacks or stores in an older version, handed over before any test
        Phase("serve");
        Serve();
        // 3.-5. downloads, tests and the publish, in batches: a long run publishes as it goes, and staging stays small
        var first = true;
        while (true)
        {
            var tBatch = Clock.Now();
            Phase("downloads");
            var got = Download(known, details);
            Phase("tests");
            var outcomes = ScanAndTest();
            Worked += Clock.Now() - tBatch;
            Decided += outcomes.Count;
            // this batch's verdicts and any earlier one that could not be published then
            Phase("publish");
            var decided = new HashSet<string>(outcomes.Select(o => o.Id));
            foreach (var (wid, node) in State.Items)
                if (!decided.Contains(wid) && node is JsonObject rec && Py.Truthy(rec["verdict"]) && Py.Str(rec["status"]) == "tested")
                    outcomes.Add(new Verdicts.Outcome(wid, Py.Str(rec["verdict"]), Py.Obj(rec["detail"]), rec));
            var before = Report["published"] as JsonObject;
            before = (JsonObject?)before?.DeepClone();
            Publish(outcomes, first ? removedNow : [], details);
            Report["published"] = Mail.MergePublished(before, Report["published"] as JsonObject)?.DeepClone();
            VerdictsToReport = [];                        // the check's verdicts go to the bridge once a run
            first = false;
            CleanupStaging();
            JsonObject dl = Py.Obj(Report["downloads"]), t = Py.Obj(Report["tests"]);
            if (got == 0 || Py.Truthy(dl["stop"]) || Py.Truthy(t["stop"]) || Left() < 300)
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

    /// <summary>The download queue, best first: items created in the last 14 days (newest first), then listed maps changed since we last saw
    /// them, then everything else we have seen (most subscribed first) - and before all of them, best score first, a waiting map whose
    /// Workshop score would put it among its mode's best (the keep set's top lists), so the maps that will stay on the server are tested
    /// before the rest.</summary>
    public List<string> Queue(IReadOnlyDictionary<string, string> known, IReadOnlyDictionary<string, JsonObject> details)
    {
        var items = State.Items;
        var only = Cfg.OnlyIds;
        var now = Clock.Now();
        var fresh = new List<string>();
        var changed = new List<string>();
        var backlog = new List<string>();
        var tags = new HashSet<string>(Cfg.Tags);
        foreach (var (wid, node) in items)
        {
            if (node is not JsonObject rec || Py.Str(rec["status"]) is not ("queued" or "retry"))
                continue;
            if (only is not null && !only.Contains(wid))
                continue;
            if (known.ContainsKey(wid) && Py.Str(rec["why"]) != "changed")
            {
                rec["status"] = "known";
                continue;
            }
            if (Py.Str(rec["ws"]) != "ok" || !(Py.IsNumber(rec["app"]) && KeepSet.Number(rec["app"]) is 0 or 730))
                continue;
            if (Py.Truthy(rec["creator"]) && Cfg.ExcludeCreators.Contains(Py.Str(rec["creator"])))
            {
                rec["status"] = "skipped";
                rec["evidence"] = "listed by hand (its author is in sync.excludeCreators)";
                continue;
            }
            if (Py.Truthy(rec["filename"]))
            {
                rec["status"] = "skipped";
                rec["evidence"] = $"a CS:GO-era upload (its Workshop file is {Py.Cut(Py.Str(rec["filename"]), 60)}): no CS2 map, not downloaded";
                continue;
            }
            if (!Py.StrList(rec["tags"]).Any(tags.Contains))
                continue;
            if (Py.Str(rec["why"]) == "changed")
                changed.Add(wid);
            else if (now - Py.Int(rec["created"]) < 14 * Day)
                fresh.Add(wid);
            else
                backlog.Add(wid);
        }
        fresh = fresh.OrderBy(w => -Py.Int(Py.Get(items[w], "created"))).ToList();
        changed = changed.OrderBy(w => Verdicts.PopularityKey(items[w], w)).ToList();
        var ordered = fresh.Concat(changed).Concat(Verdicts.OrderBacklog(items, backlog)).ToList();
        var scores = Py.Obj(Py.Get(State.Data["ratings"], "scores"));
        var cuts = TopCutoffs();
        double? Best(string wid)
        {
            var v = scores[wid];
            var s = v is JsonArray a ? a.Count > 0 ? a[0] : null : v;
            var n = v is JsonArray b && b.Count > 3 ? b[3] : null;
            if (!(Py.IsNumber(s) || Py.IsBool(s)))
                return null;
            var score = Py.Float(s);
            var itemTags = Py.Get(items[wid], "tags");
            foreach (var (tag, c) in cuts)
            {
                if (Py.In(itemTags, tag) && ((c.Score is null || score >= c.Score) || (Py.Integer(n) is { } subs && (c.Subs is null || subs >= c.Subs)) || c.Popular.Contains(wid)))
                    return score;
            }
            return null;
        }
        QFresh = [.. fresh];
        QChanged = [.. changed];
        var place = new Dictionary<string, int>();
        for (var i = 0; i < ordered.Count; i++)
            place[ordered[i]] = i;
        var top = ordered.Where(w => Best(w) is not null).OrderBy(w => -Best(w)!.Value).ThenBy(w => place[w]).ToList();
        QTop = [.. top];
        if (top.Count > 0)
            ordered = top.Concat(ordered.Where(w => !QTop.Contains(w))).ToList();
        Backlog = Verdicts.BacklogStats(items, ordered, Pace());
        return ordered;
    }

    /// <summary>{Workshop tag: (the score and the subscriptions a map needs to be among its mode's top by that list - null: the list is
    /// shorter, anything does -, the ids of Most Popular's order up to its mode's top-th listed map)}, from the keep set of the bridge's files
    /// as they are; empty without ratings. Kept for 10 minutes.</summary>
    public Dictionary<string, (double? Score, long? Subs, HashSet<string> Popular)> TopCutoffs()
    {
        if (cut is null || Clock.Now() - cut.Value.At > 600)
        {
            var inputs = KeepInputs();
            var keep = SafeKeepSet(inputs, Clock.Now());
            var trend = Py.Obj(Py.Get(inputs.GetValueOrDefault("ratings.json"), "trend"));
            var c = new Dictionary<string, (double?, long?, HashSet<string>)>();
            if (keep.Scored > 0)
            {
                foreach (var (mode, tag, _, _, _) in GameModes.All)
                {
                    var order = Py.StrList(trend[mode]);
                    var last = keep.Lists.TryGetValue(mode, out var l) ? l.Popular ?? [] : [];
                    var upto = last.Count >= KeepRules.Default.Top && order.Contains(last[^1]) ? order.IndexOf(last[^1]) + 1 : order.Count;
                    var (score, subs) = keep.Cutoff.TryGetValue(mode, out var co) ? co : (null, null);
                    c[tag] = (score, subs, [.. order.Take(upto)]);
                }
            }
            cut = (Clock.Now(), c);
        }
        return cut.Value.Cut;
    }

    public void ForgetCutoffs() => cut = null;

    /// <summary>2b. Once every RatingsEvery - and at once when the ratings kept lack a list the keep set needs - the three lists the keep set
    /// takes each mode's best from: the Workshop's score (vote_data.score) and the current subscriptions of every listed map, every mode, and
    /// of every map still waiting for its test (GetDetails with includevotes, 100 a call); and each mode's Most Popular order, Steam's trend
    /// over the last TrendDays days (QueryFiles RankedByTrend by the mode's tag, pages of 100 until TrendWant of the mode's listed maps were in
    /// it, at most TrendPages). Kept in the state and written to the bridge's data as ratings.json. A run cut short merges what it got into
    /// what was there and keeps the old time: the keep set refuses deletions on ratings over 3 days old, so their age must stay honest.</summary>
    public void Ratings(IWorkshopApi api, IReadOnlyList<string> listed)
    {
        if (State.Data["ratings"] is not JsonObject st)
        {
            st = new JsonObject { ["fetched"] = 0, ["scores"] = new JsonObject() };
            State.Data["ratings"] = st;
        }
        var rep = Section("ratings", () => []);
        if (!Cfg.Ratings)
        {
            rep["skipped"] = "sync.ratings is off";
            return;
        }
        var age = Clock.Now() - Py.Float(st["fetched"]);
        var sample = Py.Obj(st["scores"]).Select(kv => kv.Value).Take(50).ToList();
        var whole = sample.Count > 0 && sample.All(v => v is JsonArray a && a.Count > 3)
                    && GameModes.All.All(m => Py.Get(st["trend"], m.Mode) is JsonArray);
        if (whole && age < RatingsEvery)
        {
            rep["skipped"] = $"fetched {Text.Hms(age)} ago, again after {Text.Hms(RatingsEvery)}";
            if (!File.Exists(Path.Combine(Cfg.BridgeData, "ratings.json")))
                WriteRatings(st);
            return;
        }
        var tags = new HashSet<string>(Cfg.Tags);
        var waiting = State.Items.Where(kv => kv.Value is JsonObject r && Py.Str(r["status"]) is "queued" or "retry" && Py.Str(r["ws"]) == "ok"
                                             && Py.StrList(r["tags"]).Any(tags.Contains)).Select(kv => kv.Key).ToList();
        var ids = listed.Concat(waiting).Distinct().ToList();
        var inputs = KeepInputs();
        var members = new Dictionary<string, HashSet<string>>();
        foreach (var (mode, _, _, _, name) in GameModes.All)
            members[mode] = [.. Py.Arr(inputs.GetValueOrDefault(name)).OfType<JsonObject>().Select(e => Py.StrOr(e["id"])).Where(Py.Digits)];
        var got = new Dictionary<string, JsonArray>();
        var trend = new Dictionary<string, List<string>>();
        var walked = new Dictionary<string, (int Read, int Listed)>();
        var failed = 0;
        try
        {
            for (var start = 0; start < ids.Count; start += Discovery.DetailsBatch)
            {
                if (Clock.Now() >= Deadline - 1200)
                {
                    rep["stop"] = "the run's deadline";
                    break;
                }
                var batch = ids.Skip(start).Take(Discovery.DetailsBatch).ToList();
                try
                {
                    foreach (var (k, v) in api.Votes(batch))
                        got[k] = v;
                }
                catch (ApiErrorException e)
                {
                    failed++;
                    L($"scores of {batch.Count} ids failed for good ({e.Message})");
                }
            }
            foreach (var (mode, tag, _, _, _) in GameModes.All)
            {
                if (Py.Truthy(rep["stop"]))
                    break;
                var order = new List<string>();
                var cursor = "*";
                var hits = 0;
                for (var page = 0; page < TrendPages; page++)
                {
                    List<string> pageIds;
                    string next;
                    try
                    {
                        (pageIds, next, _) = api.Trend(tag, cursor, TrendDays);
                    }
                    catch (ApiErrorException e)
                    {
                        failed++;
                        L($"Most Popular of {mode}, page {page + 1}, failed for good ({e.Message})");
                        break;
                    }
                    order.AddRange(pageIds.Where(w => !order.Contains(w)).Distinct());
                    hits = order.Count(members[mode].Contains);
                    if (hits >= TrendWant || pageIds.Count == 0 || next.Length == 0 || next == cursor)
                        break;
                    cursor = next;
                }
                trend[mode] = order;
                walked[mode] = (order.Count, hits);
            }
        }
        catch (StopApiException stop)
        {
            rep["stop"] = stop.Message;
            L($"ratings stopped: {stop.Message}");
        }
        var complete = !Py.Truthy(rep["stop"]) && failed == 0 && trend.Count == GameModes.All.Length;
        var gotObject = new JsonObject();
        foreach (var (k, v) in got)
            gotObject[k] = v.DeepClone();
        var trendObject = new JsonObject();
        foreach (var (k, v) in trend)
            trendObject[k] = Py.Strings(v);
        if (complete)
        {
            st["scores"] = gotObject;
            st["trend"] = trendObject;
            st["fetched"] = Clock.Now();
            st["trend_days"] = TrendDays;
        }
        else
        {
            if (got.Count > 0)
            {
                var merged = Py.Obj(st["scores"]?.DeepClone());
                foreach (var (k, v) in gotObject)
                    merged[k] = v?.DeepClone();
                st["scores"] = merged;
            }
            if (trend.Count > 0)
            {
                var merged = Py.Obj(st["trend"]?.DeepClone());
                foreach (var (k, v) in trendObject)
                    merged[k] = v?.DeepClone();
                st["trend"] = merged;
            }
        }
        rep["asked"] = ids.Count;
        rep["listed"] = listed.Count;
        rep["waiting"] = waiting.Count;
        rep["scored"] = got.Count;
        rep["failed"] = failed;
        rep["complete"] = complete;
        rep["calls"] = api.ByApi.GetValueOrDefault("GetDetails");
        var walkedObject = new JsonObject();
        foreach (var (m, v) in walked)
            walkedObject[m] = new JsonArray(v.Read, v.Listed);
        rep["trend"] = walkedObject;
        rep["trend_days"] = TrendDays;
        L($"ratings: {got.Count} of {ids.Count} scored with their subscriptions ({listed.Count} listed, {waiting.Count} waiting) in {Py.Str(rep["calls"])} call(s); "
          + $"Most Popular over {TrendDays} days: {(walked.Count > 0 ? string.Join(", ", walked.Select(kv => $"{kv.Key} {kv.Value.Read} read, {kv.Value.Listed} listed")) : "none")}"
          + $"{(complete ? "" : " - INCOMPLETE, the old time kept")}");
        State.Save();
        cut = null;
        if (got.Count > 0 || trend.Count > 0)
            WriteRatings(st);
    }

    /// <summary>&lt;bridge&gt;/ratings.json, version 1: {"fetched": unix time, "scores": {id: [score, votes up, votes down, subscriptions]},
    /// "trend": {mode: [ids in Most Popular order]}, "trend_days": days}.</summary>
    public void WriteRatings(JsonObject st)
    {
        var text = Py.Compact(new JsonObject
        {
            ["version"] = 1, ["fetched"] = Py.Truthy(st["fetched"]) ? st["fetched"]!.DeepClone() : 0,
            ["source"] = "IPublishedFileService/GetDetails vote_data and subscriptions; QueryFiles RankedByTrend",
            ["trend_days"] = Py.Truthy(st["trend_days"]) ? st["trend_days"]!.DeepClone() : TrendDays,
            ["scores"] = Py.Truthy(st["scores"]) ? st["scores"]!.DeepClone() : new JsonObject(), ["trend"] = Py.Truthy(st["trend"]) ? st["trend"]!.DeepClone() : new JsonObject(),
        }) + "\n";
        if (!Cfg.Publish)
        {
            Section("ratings", () => [])["file"] = "not written (sync.publish is off)";
            if (Cfg.DryRun)
                WouldPublish.Write(Cfg, Clock, "ratings.json", text, L);
            return;
        }
        try
        {
            Files.WriteAtomic(Path.Combine(Cfg.BridgeData, "ratings.json"), text, FileModes.Shared);
            Section("ratings", () => [])["file"] = "ratings.json written";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Errors.Add($"ratings.json not written: {e.Message}");
        }
    }

    /// <summary>A map skipped as bigger than a whole day's download cap is queued again - the 223 popular maps the old caps parked (800 MB,
    /// then 3 GB a night) - and nothing is skipped for its size any more: there is no daily cap. They are tested like the rest, by
    /// popularity, the ones the keep set wants first.</summary>
    public void RequeueSizeSkips()
    {
        var back = new List<string>();
        foreach (var (wid, node) in State.Items)
        {
            if (node is not JsonObject rec || Py.Str(rec["status"]) != "skipped" || !Py.StrOr(rec["evidence"]).Contains("download cap", StringComparison.Ordinal))
                continue;
            rec["status"] = "queued";
            rec["requeued"] = $"{Day0}: no daily download cap any more ({Py.StrOr(rec["evidence"])})";
            rec.Remove("skip_cap");
            back.Add(wid);
        }
        if (back.Count > 0)
        {
            State.Save();
            L($"queued again: {back.Count} map(s) skipped as too big for an old daily download cap - there is none any more");
        }
        Report["requeued"] = back.Count;
    }

    JsonObject ServeReport() => Section("serve", () => new JsonObject { ["ok"] = 0, ["bytes"] = 0, ["failed"] = new JsonArray() });

    static void Bump(JsonObject o, string key, long by) => o[key] = Py.Int(o[key]) + by;

    static JsonArray List(JsonObject o, string key)
    {
        if (o[key] is not JsonArray a)
        {
            a = [];
            o[key] = a;
        }
        return a;
    }

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
        return entry is { } e && new[] { "size", "timeupdated", "manifest" }.All(k => Py.Digits(e.Installed.GetValueOrDefault(k)))
               && long.Parse(e.Installed["timeupdated"], CultureInfo.InvariantCulture) >= Py.Int(rec["updated"]);
    }

    /// <summary>THE HAND-OVER, for what is missing or outdated (Handover.ServeWants): the keep set's maps the game server does not store - the
    /// upvoted maps, each mode's best, the own maps - and the ones it stores in an older version than the one this sync accepted, downloaded
    /// with SteamCMD (anonymous, with every push-back stop and the brake of the downloads) and handed over at once; never what waits in the
    /// hand-over folder, nor the random queues and the requests (the server fetches those itself), nor an excluded id. Their version was
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
        var (missing, outdated) = Handover.ServeWants(keep, has, pending, State.Data, Cfg);
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
            rep["stop"] = $"{Text.Mb(pending.Values.Sum())} of hand-overs wait for the server already";
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
            if (entry is not { } e || !Py.Digits(e.Installed.GetValueOrDefault("timeupdated"))
                || long.Parse(e.Installed["timeupdated"], CultureInfo.InvariantCulture) < Py.Int(rec["updated"]))
                continue;                                  // no entry in SteamCMD's acf, or an older version: downloaded again below
            var bytes = Handover.FolderBytes(Path.Combine(Content, wid));
            var (done, words) = HandOverSafe(wid, Py.StrOr(rec["title"]));
            L($"serve: {wid} {Py.Cut(Py.StrOr(rec["title"]), 40)} was staged already, {words}");
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
        var steam = Seams.SteamCmd(Cfg, Staging, Path.Combine(Cfg.WorkDir, "steamcmd.log"), Clock);
        bool started = false;
        int row = 0, denied = 0;
        try
        {
            for (var step = 0; step < want.Count; step++)
            {
                var wid = want[step];
                var (ok, why) = budget.Allows(wid);
                if (!ok)
                {
                    if (why.Contains("no more downloads", StringComparison.Ordinal))
                    {
                        rep["stop"] = why;
                        break;
                    }
                    L($"serve: {wid}: {why}");
                    continue;
                }
                OnMap(wid, step, want.Count);
                if (Left() < 900)
                {
                    rep["stop"] = "the run's deadline";
                    break;
                }
                var tWait = Clock.Now();
                if (!WaitForNobody("serve", 900))
                {
                    rep["stop"] = "somebody was on the server until the deadline";
                    break;
                }
                if (started && Clock.Now() - tWait > 300)
                {
                    steam.Stop();
                    started = false;
                }
                if (!started)
                {
                    (started, why) = steam.Start();
                    if (!started)
                    {
                        rep["stop"] = $"SteamCMD did not log in anonymously: {why}";
                        if (SteamCmdWords.StopRx().IsMatch(why))
                            budget.RefuseForToday($"SteamCMD answered '{why}'");
                        break;
                    }
                }
                var t = Clock.Now();
                var (good, reason, nbytes) = steam.Download(wid);
                if (good && Staged(wid))
                {
                    var onDisk = Handover.FolderBytes(Path.Combine(Content, wid));
                    var size = Math.Max(nbytes, onDisk);
                    budget.Spend(size, wid);
                    Bump(rep, "downloads", 1);
                    Bump(rep, "dl_bytes", size);
                    (row, denied) = (0, 0);
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
                    var (done, words) = fresh ? HandOverSafe(wid, Py.StrOr(rec["title"]))
                        : (false, "SteamCMD's acf does not name this version yet - handed over once it has quit");
                    L($"serve: {wid} {Py.Cut(Py.StrOr(rec["title"]), 40)} downloaded in {Py.Fixed(Clock.Now() - t, 0)} s, {words}");
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
                    List(rep, "failed").Add(Py.Array(wid, reason));
                    row++;
                    budget.Fail();
                    Event(wid, "failed", $"Its download failed ({reason}).");
                    L($"serve: the download of {wid} failed: {reason}");
                    if (SteamCmdWords.AccessDeniedRx().IsMatch(reason))
                        denied++;
                    if (SteamCmdWords.StopRx().IsMatch(reason) || denied >= 2)
                    {
                        rep["stop"] = $"SteamCMD answered '{reason}'{(denied >= 2 ? " twice in a row" : "")} - no more downloads today";
                        budget.RefuseForToday($"SteamCMD answered '{reason}'");
                        break;
                    }
                    if (row >= 3)
                    {
                        rep["stop"] = "3 downloads in a row failed";
                        break;
                    }
                    if (reason.StartsWith(SteamCmdWords.Stall, StringComparison.Ordinal))
                    {
                        if (Stalled(wid, reason, budget))
                        {
                            rep["stop"] = $"SteamCMD stalled twice in this run ({reason}) - no more downloads in it";
                            break;
                        }
                        steam.Stop();                            // a fresh session for the next map: a stall alone is routine
                        started = false;
                    }
                    else if (reason.Contains("not running", StringComparison.Ordinal) || reason.Contains("stdin", StringComparison.Ordinal))
                    {
                        rep["stop"] = $"SteamCMD stopped answering ({reason})";
                        break;
                    }
                }
                State.Save();
                Clock.Sleep(Cfg.DownloadPause);
            }
        }
        finally
        {
            steam.Stop();
            State.Save();
        }
        foreach (var (wid, rec, bytes) in later)                // SteamCMD has quit: its acf names them now
        {
            var (done, words) = HandOverSafe(wid, Py.StrOr(rec["title"]));
            L($"serve: {wid} after SteamCMD quit: {words}{(done ? "" : " - it stays staged for the next run")}");
            if (!done)
                List(rep, "handover_failed").Add(Py.Array(wid, words));
            if (done)
            {
                Bump(rep, "ok", 1);
                Bump(rep, "bytes", bytes);
                handed.Add(wid);
                Event(wid, "handed-over", (newer.Contains(wid) ? "Its newer version downloaded" : "Downloaded") + $" and handed over to the game server ({Text.Mb(bytes)}).");
            }
            else
                List(rep, "failed").Add(Py.Array(wid, words));
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
        L($"serve: {Py.Str(rep["ok"])} handed over ({Text.Mb(Py.Int(rep["bytes"]))}), {Py.Arr(rep["failed"]).Count} failed, {Py.Str(rep["left"])} still missing"
          + (Py.Truthy(rep["stop"]) ? $" - stopped: {Py.Str(rep["stop"])}" : ""));
    }

    /// <summary>A SteamCMD stall on `wid`: booked (the run's list, the day's count of the map), logged. True when it is the run's second - the
    /// downloads stop then; one alone is routine, and the mail speaks of stalls only from two in a run, or two of one map in a day.</summary>
    public bool Stalled(string wid, string reason, DownloadBudget budget)
    {
        Stalls.Add((wid, budget.Stall(wid)));
        Report["stalls"] = new JsonArray(Stalls.Select(s => (JsonNode?)new JsonArray(s.Id, s.Stalls)).ToArray());
        L($"SteamCMD stalled on {wid} ({reason}), the run's stall {Stalls.Count}{(Stalls.Count >= 2 ? " - no more downloads in this run" : " - a fresh session for the next map")}");
        return Stalls.Count >= 2;
    }

    /// <summary>Seconds a map took this run, download and test together; null until enough were decided to say.</summary>
    public double? Pace() => Decided >= 20 ? Worked / Decided : null;

    public bool Staged(string wid) => Directory.Exists(Path.Combine(Content, wid));

    /// <summary>One batch: at most sync.batch downloads on one anonymous SteamCMD login. Returns how many it downloaded.</summary>
    public int Download(IReadOnlyDictionary<string, string> known, IReadOnlyDictionary<string, JsonObject> details)
    {
        var budget = new DownloadBudget(State, Day0);
        var dl = Section("downloads", () => new JsonObject { ["ok"] = 0, ["bytes"] = 0, ["failed"] = new JsonArray(), ["braked"] = new JsonArray() });
        try
        {
            Handover.TidyStaging(Staging, Cfg.HandoverDir, State.Items, L);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            L($"staging: not tidied ({e.Message})");
        }
        var stalled = new HashSet<string>(Stalls.Select(s => s.Id));          // a map that stalled in this run waits for the next one
        var todo = Queue(known, details).Where(w => !Staged(w) && !stalled.Contains(w)).ToList();
        if (todo.Count == 0)
        {
            dl["queued_left"] = 0;
            Limits(budget);
            return 0;
        }
        var (allowed, why) = budget.Allows();
        if (!allowed)
        {
            dl["stop"] = why;
            dl["queued_left"] = todo.Count;
            Limits(budget);
            return 0;
        }
        var steam = Seams.SteamCmd(Cfg, Staging, Path.Combine(Cfg.WorkDir, "steamcmd.log"), Clock);
        var started = false;
        int row = 0, denied = 0, got = 0;
        var done = new HashSet<string>();
        try
        {
            foreach (var wid in todo)
            {
                if (got >= Cfg.Batch)
                    break;
                OnMap(wid, got, Math.Min(todo.Count, Cfg.Batch));
                var rec = State.Item(wid)!;
                (allowed, why) = budget.Allows(wid);
                if (!allowed)
                {
                    if (why.Contains("no more downloads", StringComparison.Ordinal))
                    {
                        dl["stop"] = why;
                        break;
                    }
                    List(dl, "braked").Add(wid);           // downloaded twice today already: a bug somewhere, not again today
                    continue;
                }
                if (Left() < 600 + 25 * (got + 1))
                {
                    dl["stop"] = "the run's deadline (time is kept for this batch's tests)";
                    break;
                }
                var tWait = Clock.Now();
                if (!WaitForNobody("downloads", 600))
                {
                    dl["stop"] = "somebody was on the server until the deadline";
                    break;
                }
                if (started && Clock.Now() - tWait > 300)
                {
                    steam.Stop();                          // a long wait: a fresh login rather than a session gone stale
                    started = false;
                }
                if (!started)
                {
                    (started, why) = steam.Start();
                    if (!started)
                    {
                        dl["stop"] = $"SteamCMD did not log in anonymously: {why}";
                        Errors.Add(Py.Str(dl["stop"]));
                        if (SteamCmdWords.StopRx().IsMatch(why))
                            budget.RefuseForToday($"SteamCMD answered '{why}'");
                        break;
                    }
                    L($"SteamCMD: {why}");
                }
                var t = Clock.Now();
                var (good, reason, nbytes) = steam.Download(wid);
                rec["attempts"] = Py.Int(rec["attempts"]) + 1;
                if (good && Staged(wid))
                {
                    var onDisk = Handover.FolderBytes(Path.Combine(Content, wid));
                    var size = Math.Max(nbytes, onDisk);
                    budget.Spend(size, wid);
                    Bump(dl, "ok", 1);
                    got++;
                    Bump(dl, "bytes", size);
                    rec["status"] = "downloaded";
                    rec["downloaded"] = Day0;
                    rec["loads"] = Py.Str(rec["why"]) == "changed" ? new JsonArray() : Py.Truthy(rec["loads"]) ? rec["loads"]!.DeepClone() : new JsonArray();
                    foreach (var stale in new[] { "scan", "verdict", "decided", "detail" })
                        rec.Remove(stale);
                    (row, denied) = (0, 0);
                    L($"downloaded {wid} {Py.Cut(Py.StrOr(rec["title"]), 40)} ({Text.Mb(size)}) in {Py.Fixed(Clock.Now() - t, 0)} s");
                }
                else
                {
                    List(dl, "failed").Add(Py.Array(wid, reason));
                    row++;
                    budget.Fail();
                    var attempts = Py.Int(rec["attempts"]);
                    Event(wid, "failed", $"Its download failed ({reason}), {attempts} of 3 tries.");
                    rec["status"] = attempts < 3 ? "retry" : "failed";
                    rec["evidence"] = $"SteamCMD could not download it ({reason}), {attempts} of 3 tries";
                    L($"download of {wid} failed: {reason}");
                    if (SteamCmdWords.AccessDeniedRx().IsMatch(reason))
                    {
                        rec["status"] = "failed";          // this item, for now; Steam will not hand it out
                        denied++;
                    }
                    if (SteamCmdWords.StopRx().IsMatch(reason) || denied >= 2)
                    {
                        dl["stop"] = $"SteamCMD answered '{reason}'{(denied >= 2 ? " twice in a row" : "")} - no more downloads today";
                        budget.RefuseForToday($"SteamCMD answered '{reason}'");
                        break;
                    }
                    if (row >= 3)
                    {
                        dl["stop"] = "3 downloads in a row failed - no more in this run";
                        break;
                    }
                    if (reason.StartsWith(SteamCmdWords.Stall, StringComparison.Ordinal))
                    {
                        if (Stalled(wid, reason, budget))
                        {
                            dl["stop"] = $"SteamCMD stalled twice in this run ({reason}) - no more downloads in it";
                            break;
                        }
                        steam.Stop();                     // a fresh session for the next map: a stall alone is routine
                        started = false;
                    }
                    else if (reason.Contains("not running", StringComparison.Ordinal) || reason.Contains("stdin", StringComparison.Ordinal))
                    {
                        dl["stop"] = $"SteamCMD stopped answering ({reason})";
                        break;
                    }
                }
                done.Add(wid);
                State.Save();
                Clock.Sleep(Cfg.DownloadPause);
            }
        }
        finally
        {
            steam.Stop();
            State.Save();
        }
        dl["queued_left"] = todo.Count(w => !done.Contains(w));
        Limits(budget);
        return got;
    }

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

    /// <summary>Scan every staged item that has no verdict, then load the maps among them on the test engine. Returns the items this batch
    /// decided.</summary>
    public List<Verdicts.Outcome> ScanAndTest()
    {
        var t = Section("tests", () => new JsonObject { ["rows"] = new JsonArray() });
        var staged = Directory.Exists(Content) ? Directory.EnumerateFileSystemEntries(Content).Select(p => Path.GetFileName(p)!).OrderBy(x => x, StringComparer.Ordinal).ToList() : [];
        var only = Cfg.OnlyIds;                            // a manual run limited to some maps tests only those
        var work = new List<string>();
        foreach (var wid in staged)
        {
            if (only is not null && !only.Contains(wid))
                continue;
            var rec = State.Item(wid);
            if (rec is null || Py.Str(rec["status"]) is not ("downloaded" or "tested"))
                continue;
            if (!rec.ContainsKey("scan"))
            {
                try
                {
                    rec["scan"] = Seams.ScanItem(Path.Combine(Content, wid));
                }
                catch (Exception e) when (e is not StopRequested)   // a malformed upload must not end the run
                {
                    rec["scan"] = new JsonObject { ["verdict"] = "unknown", ["names"] = new JsonArray(), ["error"] = Py.Cut(Mail.Repr(e), 200) };
                }
            }
            work.Add(wid);
        }
        State.Save();
        var outcomes = new List<Verdicts.Outcome>();
        var toLoad = new List<string>();
        foreach (var wid in work)
        {
            var rec = State.Item(wid)!;
            var (kind, detail) = Verdicts.Final(rec, Day0);
            if (kind == "pending")
                toLoad.Add(wid);
            else
                outcomes.Add(new Verdicts.Outcome(wid, kind, detail, rec));
        }
        if (toLoad.Count > 0)
        {
            var version = Seams.PatchVersion(Cfg.GameDir);
            if (!WaitForNobody("tests", 600))
                t["stop"] = "somebody was on the server until the deadline";
            else
            {
                var stale = Seams.KillStaleEngines(Path.Combine(Cfg.WorkDir, "farm"), Clock);
                if (stale > 0)
                    L($"killed {stale} test engine(s) left running from an earlier run");
                try
                {
                    L(Seams.BuildFarm(Cfg.GameDir, Cfg.WorkDir, Cfg.CfgDir, Clock));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    t["stop"] = $"the test tree could not be built: {e.Message}";
                    Errors.Add(Py.Str(t["stop"]));
                    toLoad = [];
                }
                Engine = Seams.TestEngine(Cfg, Cfg.WorkDir, Cfg.Home, Clock, L);
                Engine.Starts = EngineStarts;
                var budget = new DownloadBudget(State, Day0);
                var needStart = true;
                try
                {
                    for (var step = 0; step < toLoad.Count; step++)
                    {
                        var wid = toLoad[step];
                        var rec = State.Item(wid)!;
                        OnMap(wid, step, toLoad.Count);
                        while (true)
                        {
                            var (kind, _) = Verdicts.Final(rec, Day0);
                            if (kind != "pending")
                                break;
                            if (Left() < 300)
                            {
                                t["stop"] = "the run's deadline";
                                break;
                            }
                            if (Seams.PatchVersion(Cfg.GameDir) != version)
                            {
                                t["stop"] = $"the game was updated during the tests (steam.inf {version ?? "None"} -> {Seams.PatchVersion(Cfg.GameDir) ?? "None"})";
                                break;
                            }
                            if (!WaitForNobody("tests", 300))
                            {
                                t["stop"] = "somebody was on the server until the deadline";
                                break;
                            }
                            if (needStart || !Engine.Alive())
                            {
                                var (ok, why) = Engine.Start();
                                L(why);
                                if (!ok)
                                {
                                    t["stop"] = why;
                                    t["engine_failed"] = why;
                                    break;
                                }
                                needStart = false;
                            }
                            var levels = Py.StrList(Py.Get(rec["scan"], "maps"));
                            var load = Seams.TestLoad(Engine, wid, levels, Py.StrList(rec["tags"]), Cfg.Hold, () => (Humans() ?? 0) > 0, Clock);
                            List(rec, "loads").Add(load);
                            rec["tests"] = Py.Int(rec["tests"]) + 1;
                            rec["status"] = "tested";
                            if (Py.Truthy(load["engine_download"]))
                            {
                                budget.SpendBytes(Py.Int(load["engine_download"]));
                                try                         // what the engine loaded is its own copy: scan that one
                                {
                                    rec["scan"] = Seams.ScanItem(Path.Combine(Content, wid));
                                }
                                catch (Exception e) when (e is not StopRequested)
                                {
                                    if (rec["scan"] is JsonObject scan)
                                        scan["rescan_error"] = Py.Cut(Mail.Repr(e), 200);
                                }
                                L($"{wid}: the test engine fetched its own copy ({Text.Mb(Py.Int(load["engine_download"]))}) - scanned again");
                            }
                            State.Save();
                            L($"{wid} load {Py.Arr(rec["loads"]).Count}: {Py.Str(load["verdict"])} - {Py.Str(load["reason"])}");
                            if (Py.Str(load["verdict"]) is "errormap" or "crash" or "freeze")
                                needStart = true;          // a fresh engine: the old one is down or stuck on the error map
                            if (Py.Str(load["verdict"]) == "abort")
                            {
                                Engine.Stop();             // somebody joined: nothing of ours runs beside them
                                needStart = true;
                                break;
                            }
                        }
                        if (Py.Truthy(t["stop"]))
                            break;
                        var (k2, d2) = Verdicts.Final(rec, Day0);
                        if (k2 != "pending")
                            outcomes.Add(new Verdicts.Outcome(wid, k2, d2, rec));
                    }
                }
                finally
                {
                    Engine.Stop();
                    EngineStarts = Engine.Starts;
                }
            }
        }
        var rows = List(t, "rows");
        foreach (var o in outcomes)
        {
            var (wid, kind, detail, rec) = (o.Id, o.Kind, o.Detail, o.Rec);
            rec["verdict"] = kind;
            rec["decided"] = Day0;
            var kept = new JsonObject();
            foreach (var (k, v) in detail)
                if (k is "kind" or "detail" or "evidence" or "reason" or "level" or "mode" or "crashes" or "sample")
                    kept[k] = v?.DeepClone();
            rec["detail"] = kept;
            var title = Py.Cut(Py.StrOr(rec["title"]), 48);
            if (kind == "pass")
                rows.Add($"PASS     {title} ({wid}) - {Py.Str(detail["mode"])}, loads as {Py.Str(detail["level"])} in {Py.Str(detail["load_s"])}s");
            else if (kind == "nonmap")
                rows.Add($"NON-MAP  {title} ({wid}) - {Py.Cut(detail.ContainsKey("detail") ? Py.Str(detail["detail"]) : "", 110)}");
            else if (kind == "advert")
                rows.Add($"ADVERT   {title} ({wid}) - {Py.Str(detail["reason"])}: {Py.Cut(Py.StrOr(detail["sample"]), 80)}");
            else
                rows.Add($"{Py.LeftPad(kind.ToUpperInvariant(), 8)} {title} ({wid}) - {Py.Cut(detail.ContainsKey("evidence") ? Py.Str(detail["evidence"]) : "", 110)}");
        }
        var pending = work.Where(w => State.Item(w)?["verdict"] is null).ToList();
        if (pending.Count > 0)
        {
            L($"waiting for the next batch or run: {pending.Count} staged item(s) without a verdict");
            t["waiting_verdict"] = pending.Count;
        }
        Limits();
        State.Save();
        return outcomes;
    }

    public void Publish(List<Verdicts.Outcome> outcomes, IReadOnlyList<(string Id, string Verdict)> removedNow, IReadOnlyDictionary<string, JsonObject> details)
    {
        var pub = new JsonObject();
        Report["published"] = pub;
        BridgeData files;
        try
        {
            files = BridgeData.Read(Cfg.BridgeData);          // read again: anything may have changed during the run
        }
        catch (Exception e) when (e is BadDataException or IOException or UnauthorizedAccessException)
        {
            pub["skipped"] = $"the bridge's data cannot be read ({e.Message})";
            return;
        }
        var (texts, summary) = Verdicts.PlanPublish(files, State, Day0, outcomes, removedNow, Cfg.ModeBackfill, details);
        Merge(pub, summary);
        var verdicts = VerdictsToReport;
        if (texts.Count == 0 && verdicts.Count == 0)
        {
            MarkPublished(outcomes, files);
            return;
        }
        if (!Cfg.Publish)
        {
            pub["skipped"] = $"sync.publish is off (would write {(texts.Count > 0 ? string.Join(", ", texts.Keys.OrderBy(x => x, StringComparer.Ordinal)) : "no file")}"
                             + $"{(verdicts.Count > 0 ? $", and report {verdicts.Count} verdicts" : "")})";
            if (Cfg.DryRun)
            {
                var where = new List<string>();
                foreach (var (name, text) in texts)
                    if (WouldPublish.Write(Cfg, Clock, name, text, L) is { } path)
                        where.Add(path);
                if (verdicts.Count > 0 && WouldPublish.Write(Cfg, Clock, "removed.txt", VerdictsBody(verdicts), L) is { } vpath)
                    where.Add(vpath);
                pub["would_publish"] = Py.Strings(where);
                L($"dry run: would publish {string.Join(", ", where)}");
            }
            return;
        }
        if (!WaitForNobody("publish"))
        {
            pub["skipped"] = "somebody was on the server until the deadline - the results wait for the next run";
            return;
        }
        if (verdicts.Count > 0)
        {
            pub["verdicts"] = ReportVerdicts(verdicts);
            L($"Workshop verdicts of {verdicts.Count} listed map(s) to the bridge: {Py.Str(pub["verdicts"])}");
        }
        if (texts.Count == 0)
        {
            MarkPublished(outcomes, files);
            return;
        }
        // the live files once more, right before writing: what is planned must be them plus additions, nothing else
        BridgeData live;
        try
        {
            live = BridgeData.Read(Cfg.BridgeData);
        }
        catch (Exception e) when (e is BadDataException or IOException or UnauthorizedAccessException)
        {
            pub["skipped"] = $"the bridge's data cannot be read ({e.Message})";
            return;
        }
        if (live.Raw.Keys.Union(files.Raw.Keys).Any(n => !SameBytes(live.Raw.GetValueOrDefault(n), files.Raw.GetValueOrDefault(n))))
        {
            (texts, summary) = Verdicts.PlanPublish(live, State, Day0, outcomes, removedNow, Cfg.ModeBackfill, details);
            Merge(pub, summary);
            files = live;
            L("publish: the bridge's data changed while it was planned - planned again on the live files");
        }
        var problems = texts.SelectMany(kv => Verdicts.MergeProblems(kv.Key, files.Raw.GetValueOrDefault(kv.Key), kv.Value)).ToList();
        if (problems.Count > 0)
        {
            pub["skipped"] = $"NOT written - the result would not be the live files plus additions: {string.Join("; ", problems)}";
            Errors.Add(Py.Str(pub["skipped"]));
            return;
        }
        var stamp = Clock.Local().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = Path.Combine(Cfg.StateDir, "backups", stamp);
        Directory.CreateDirectory(backup);
        foreach (var (name, raw) in files.Raw)
            File.WriteAllBytes(Path.Combine(backup, name), raw);
        var written = new List<string>();
        void WriteOne(string name)
        {
            var path = Path.Combine(Cfg.BridgeData, name);
            var nowRaw = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (!SameBytes(nowRaw, files.Raw.GetValueOrDefault(name)))
            {
                Errors.Add($"{name} changed on the share a moment before it was written - left alone");
                return;
            }
            Files.WriteAtomic(path, texts[name], FileModes.Shared);
            var back = File.ReadAllBytes(path);
            if (!SameBytes(back, Py.Utf8(texts[name])))
                Errors.Add($"{name} does not read back as written");
            else if (Verdicts.MergeProblems(name, files.Raw.GetValueOrDefault(name), System.Text.Encoding.UTF8.GetString(back)).Count > 0)
                Errors.Add($"{name}: the written file is not the old one plus additions");
            written.Add(name);
        }
        foreach (var name in new[] { "broken.json", "maps.json", "maps-deathmatch.json", "maps-wingman.json" })
            if (texts.ContainsKey(name))
                WriteOne(name);
        pub["reload"] = Reload();
        // search.json AFTER the reload: POST /reload does not read search.json yet marks it as read, so written before it the new texts would
        // wait for the next change; written now, the bridge's own watch reads it about 10 s later
        if (texts.ContainsKey("search.json"))
            WriteOne("search.json");
        pub["files"] = Py.Strings(written);
        pub["backup"] = backup;
        L($"published {string.Join(", ", written)}; bridge: {Py.Str(pub["reload"])}");
        MarkPublished(outcomes, BridgeData.Read(Cfg.BridgeData));
    }

    static void Merge(JsonObject into, JsonObject from)
    {
        foreach (var (k, v) in from)
            into[k] = v?.DeepClone();
    }

    static bool SameBytes(byte[]? a, byte[]? b) => a is null || b is null ? a is null && b is null : a.AsSpan().SequenceEqual(b);

    static string VerdictsBody(IEnumerable<(string Id, string Verdict)> verdicts) => "gg-removed\t1\n" + string.Concat(verdicts.Select(v => $"{v.Id}\t{v.Verdict}\n"));

    /// <summary>This run's check of the listed maps, handed to the bridge (POST /removed/&lt;secret&gt;) - the only Workshop check it hears: the
    /// bridge keeps them in removed.json, blocks a map that is gone at once (and unblocks one that came back), and applies its own rule - more
    /// than 10% of 50+ gone is not believed. A report that does not arrive is a problem for the mail: nothing else tells the bridge; the next
    /// check reports again.</summary>
    public string ReportVerdicts(IEnumerable<(string Id, string Verdict)> verdicts)
    {
        if (Cfg.BridgeUrl.Length == 0 || Cfg.BridgeSecret.Length == 0)
            return "not reported (no bridge.url / bridge.secret) - nothing else tells the bridge";
        try
        {
            var (status, text) = Seams.BridgePost($"{Cfg.BridgeUrl}/removed/{Cfg.BridgeSecret}", VerdictsBody(verdicts), "text/plain; charset=utf-8");
            if (status is < 200 or >= 300)
                return $"the report failed (HTTP Error {status}) - the next check reports again";
            var said = Py.Cut(text.Trim(), 300);
            return said.Length > 0 ? said : $"HTTP {status}";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            return $"the report failed ({e.Message.Replace(Cfg.BridgeSecret, "<secret>")}) - the next check reports again";
        }
    }

    public void MarkPublished(IReadOnlyList<Verdicts.Outcome> outcomes, BridgeData files)
    {
        var listed = new HashSet<string>(files.Maps.OfType<JsonObject>().Select(m => Py.Str(m["id"])));
        foreach (var (_, entries) in files.ModeLists)
            listed.UnionWith(entries.OfType<JsonObject>().Select(m => Py.Str(m["id"])));
        var on = BridgeData.BrokenIds(files.Broken);
        foreach (var o in outcomes)
        {
            var (wid, kind, detail, rec) = (o.Id, o.Kind, o.Detail, o.Rec);
            if ((listed.Contains(wid) || on.ContainsKey(wid)) && Py.Str(rec["status"]) != "published")
            {
                if (kind == "pass" && listed.Contains(wid))
                    Event(wid, "listed", $"It passed its test: it loads as {Py.StrOr(detail["level"], "its own map")}.");
                else if (on.ContainsKey(wid))
                    Event(wid, "broken", new[] { detail["evidence"], detail["detail"], detail["reason"] }.FirstOrDefault(Py.Truthy) is { } why ? Py.Str(why) : kind);
            }
            if (listed.Contains(wid) || on.ContainsKey(wid))
                rec["status"] = "published";
            else if (kind == "pass" && Py.Str(rec["why"]) == "changed")
                rec["status"] = "published";
            if (kind == "pass")
                State.Baseline[wid] = Py.Int(rec["updated"]);
        }
        State.Save();
    }

    public string Reload()
    {
        if (Cfg.BridgeUrl.Length == 0 || Cfg.BridgeSecret.Length == 0)
            return "not asked (no bridge.url / bridge.secret): it reads the files again within about 10 s";
        try
        {
            var (status, text) = Seams.BridgePost($"{Cfg.BridgeUrl}/reload/{Cfg.BridgeSecret}", "", "text/plain; charset=utf-8");
            if (status is < 200 or >= 300)
                return $"the reload request failed (HTTP Error {status}): the bridge reads the files again by itself within about 10 s";
            var said = Py.Cut(string.Join(" | ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)), 600);
            return said.Length > 0 ? said : $"HTTP {status}";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            return $"the reload request failed ({e.Message.Replace(Cfg.BridgeSecret, "<secret>")}): the bridge reads the files again by itself within about 10 s";
        }
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
        var done = Directory.EnumerateFileSystemEntries(Content).Select(p => Path.GetFileName(p)!).Where(w => Py.Truthy(State.Item(w)?["verdict"])).ToList();
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
                var stagedT = entry is { } e && Py.Digits(e.Installed.GetValueOrDefault("timeupdated")) ? long.Parse(e.Installed["timeupdated"], CultureInfo.InvariantCulture) : 0;
                var newer = has.TryGetValue(wid, out var stored) && stagedT > stored;
                if (Py.Str(rec["verdict"]) != "pass" || !keep.All.Contains(wid) || (has.ContainsKey(wid) && !newer) || pending.ContainsKey(wid) || Cfg.Exclude.Contains(wid))
                    continue;
                var (ok, why) = HandOverSafe(wid, rec["title"] is null ? null : Py.Str(rec["title"]));
                L($"hand-over {wid} {Py.Cut(Py.StrOr(rec["title"]), 40)} (tested, in the keep set{(newer ? ", newer than the server's copy" : "")}): {why}"
                  + (ok ? "" : " - it stays staged: the next run's serve hands it over"));
                if (!ok)
                    List(rep, "handover_failed").Add(Py.Array(wid, why));
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
                if (Handover.IsLink(path))
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

    public void Finish()
    {
        try
        {
            Queue(new Dictionary<string, string>(), new Dictionary<string, JsonObject>());   // the backlog as it is at the end of the run, for the mail
            Report["backlog"] = Backlog?.DeepClone();
            if (Backlog is not null && Py.Truthy(Backlog["days"]))
                Report["backlog_until"] = $" (until about {Clock.Day(Clock.Now() + Py.Int(Backlog["days"]) * Day)})";
        }
        catch (Exception e) when (e is not StopRequested)
        {
            L($"backlog not counted: {Mail.Repr(e)}");
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
        var (subject, body) = Mail.Text(Report, Cfg);
        Report["mail_subject"] = subject;
        try
        {
            Files.WriteAtomic(path, Py.DumpsFile(Report, 1), FileModes.Private);
            Files.WriteAtomic(path[..^5] + ".txt", subject + "\n\n" + body + "\n", FileModes.Private);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            L($"report not written: {e.Message}");
        }
        var idle = !(Py.Truthy(Py.Get(Report["downloads"], "ok")) || Py.Truthy(Py.Get(Report["tests"], "rows")) || Py.Truthy(Py.Get(Report["published"], "files"))
                     || Py.Truthy(Report["errors"]) || Py.Truthy(Py.Get(Report["serve"], "ok")));
        Report["idle"] = idle;
        if (Native.DiskFree(Cfg.GameDir) is { } free)       // the disk, and the game server's last deletion, for the problems below
        {
            var disk = new JsonObject { ["free"] = free };
            if (free < Cfg.DiskLow)
                disk["low"] = $"{Text.Mb(free)} free on the game's volume (the mark is {Text.Mb(Cfg.DiskLow)})";
            Report["disk"] = disk;
        }
        if (Py.ReadJson(Path.Combine(Cfg.WatcherDir, "keep.json")) is JsonObject wk && Py.Truthy(wk["problems"]) && Py.StrOr(wk["delete"], "on") != "off")
            Report["watcher_keep"] = new JsonObject
            {
                ["computed"] = Py.StrOr(wk["computed"], "?"), ["problems"] = new JsonArray(Py.Arr(wk["problems"]).Take(5).Select(p => p?.DeepClone()).ToArray()),
            };
        var issues = Mail.IssuesOf(Report);
        Report["issues"] = Py.Strings(issues.Select(i => i.What));
        Report["mail"] = Mail.MailIssues(Cfg, Clock, issues, Report, Seams.SendMail, L);
        L($"mail {Py.Str(Report["mail"])}{(issues.Count > 0 ? "" : $" - this run's report: {path[..^5]}.txt")}");
        try                                                 // again, now with the mail's outcome in it
        {
            Files.WriteAtomic(path, Py.DumpsFile(Report, 1), FileModes.Private);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        State.Data["last_run"] = new JsonObject { ["ended"] = Clock.Now(), ["did"] = idle ? 0 : 1 };
        try
        {
            var hist = State.Section("history");
            if (!Py.Truthy(hist["runs"]))
                hist["runs"] = Status.SeedRuns(Cfg.StateDir, Clock);
            var started = Clock.UnixOf(Py.Str(Report["started"]));
            var runs = Py.Arr(hist["runs"]).Where(e => !(Py.Get(e, "started") is { } s && started is { } st && Py.Float(s) == st)).Select(e => e?.DeepClone()).ToList();
            runs.Insert(0, Status.RunEntry(Report, Clock));
            hist["runs"] = new JsonArray(runs.Take(Status.Runs).ToArray());
        }
        catch (Exception e) when (e is not StopRequested)
        {
            L($"status: the run's history line not kept ({Mail.Repr(e)})");
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

    /// <summary>The status file's parts but "now", from this run's state (and ledger).</summary>
    public JsonObject StatusHeavy()
    {
        var items = State.Items;
        var day = Clock.Day();
        var book = Py.Obj(Py.Get(State.Data["downloads"], day));
        var decided = items.Select(kv => kv.Value).OfType<JsonObject>().Where(r => Py.Str(r["decided"]) == day && Py.Truthy(r["verdict"])).ToList();
        var today = new JsonObject
        {
            ["downloads"] = Py.Int(book["items"]), ["bytes"] = Py.Int(book["bytes"]), ["pass"] = decided.Count(r => Py.Str(r["verdict"]) == "pass"),
            ["rejected"] = decided.Count(r => Py.Str(r["verdict"]) != "pass"), ["failedDownloads"] = Py.Int(book["failed"]),
            ["apiCalls"] = Ledger.Data is not null ? Ledger.Count(day) : 0,
        };
        if (Py.Truthy(book["stop"]))
            today["stop"] = Py.Cut(Py.Str(book["stop"]), 200);
        var verdicts = items.Select(kv => kv.Value).OfType<JsonObject>().Where(r => Py.Truthy(r["verdict"])).Select(r => Py.Str(r["verdict"])).ToList();
        BridgeData? files;
        try
        {
            files = BridgeData.Read(Cfg.BridgeData);
        }
        catch (Exception e) when (e is BadDataException or IOException or UnauthorizedAccessException)
        {
            files = null;
        }
        var listed = new Dictionary<string, string>();
        var sizes = new Dictionary<string, long>();
        var names = new Dictionary<string, string>();
        if (files is not null)
        {
            foreach (var (mode, entries) in new[] { ("armsrace", files.Maps), ("deathmatch", files.ModeLists["deathmatch"]), ("wingman", files.ModeLists["wingman"]) })
                foreach (var entry in entries.OfType<JsonObject>())
                {
                    var id = Py.StrOr(entry["id"]);
                    if (!Py.Digits(id))
                        continue;
                    listed.TryAdd(id, mode);
                    sizes.TryAdd(id, Py.Int(entry["sizeMb"]) << 20);
                    names.TryAdd(id, Text.Clean(Py.StrOr(entry["name"]), 80));
                }
        }
        (ListedMode, ListedSize, ListedName) = (listed, sizes, names);
        var totals = new JsonObject
        {
            ["tested"] = verdicts.Count, ["pass"] = verdicts.Count(v => v == "pass"), ["broken"] = verdicts.Count(v => Status.BrokenKinds.Contains(v)),
            ["nonmap"] = verdicts.Count(v => v == "nonmap"),
            ["listed"] = new JsonObject
            {
                ["armsrace"] = files?.Maps.Count ?? 0, ["deathmatch"] = files?.ModeLists["deathmatch"].Count ?? 0, ["wingman"] = files?.ModeLists["wingman"].Count ?? 0,
            },
        };
        long SizeOf(string wid) => sizes.TryGetValue(wid, out var s) && s != 0 ? s : Py.Int(Py.Get(items[wid], "size"));
        // the queue, in the order the downloads take it
        var order = Queue(files?.KnownIds(Cfg) ?? new Dictionary<string, string>(), new Dictionary<string, JsonObject>());
        string Why(string wid)
        {
            if (QTop.Contains(wid))
                return "it would make one of its mode's lists of maps the server keeps";
            if (QChanged.Contains(wid))
                return "it changed on the Workshop since its test";
            if (QFresh.Contains(wid))
                return "a new upload";
            return $"by popularity: {Py.Int(Py.Get(items[wid], "subs"))} subscribers";
        }
        var pace = Pace() ?? Verdicts.PaceDefault;
        var byMode = new JsonObject();
        foreach (var (mode, tag, _, _, _) in GameModes.All)
            byMode[mode] = order.Count(w => Py.In(Py.Get(items[w], "tags"), tag));
        var queue = new JsonObject
        {
            ["total"] = order.Count, ["bytes"] = order.Sum(w => Py.Int(Py.Get(items[w], "size"))), ["byMode"] = byMode,
            ["eta"] = order.Count > 0 ? (long)Math.Truncate(Clock.Now() + order.Count * pace * Day / (Verdicts.HoursADay * 3600.0)) : null,
            ["next"] = new JsonArray(order.Take(Status.Next).Select(w => (JsonNode?)new JsonObject
            {
                ["id"] = w, ["name"] = Text.Clean(Py.StrOr(Py.Get(items[w], "title")), 80), ["mode"] = Status.ListMode(items[w], listed, w),
                ["sizeMb"] = Py.Int(Py.Get(items[w], "size")) >> 20, ["why"] = Why(w),
            }).ToArray()),
        };
        // the keep set: its parts as the game server stores them, and what the sync still has to hand over; the HUD item is hud.addon
        var hud = Py.Digits(Cfg.HudAddon) ? Cfg.HudAddon : "";
        var keep = SafeKeepSet(KeepInputs(), Clock.Now(), hud);
        var has = Handover.ServerMaps(Cfg.GameDir);
        var pending = Handover.Pending(Cfg.HandoverDir);
        var (want, outdated) = Handover.ServeWants(keep, has, pending, State.Data, Cfg);
        queue["serve"] = new JsonObject
        {
            ["missing"] = want.Count, ["bytes"] = want.Sum(SizeOf), ["outdated"] = outdated.Count, ["outdatedBytes"] = outdated.Sum(SizeOf),
            ["handedOver"] = pending.Count, ["handedOverBytes"] = pending.Values.Sum(),
        };
        var parts = new JsonArray();
        void Part(string name, string mode, IEnumerable<string> ids, string? queueName = null)
        {
            var list = ids.Distinct().ToList();
            var p = new JsonObject
            {
                ["part"] = name, ["mode"] = mode, ["count"] = list.Count, ["onDisk"] = list.Count(has.ContainsKey),
                ["gb"] = Py.Round(list.Sum(w => (double)SizeOf(w)) / (1L << 30), 1),
            };
            if (queueName is not null)
                p["queue"] = queueName;
            parts.Add(p);
        }
        Part("up", "", keep.Up);
        foreach (var (mode, _, _, _, _) in GameModes.All)
        {
            var lists = keep.Lists.TryGetValue(mode, out var l) ? l : ([], [], []);
            Part("rated", mode, lists.Rated ?? []);
            Part("popular", mode, lists.Popular ?? []);
            Part("subscribed", mode, lists.Subscribed ?? []);
        }
        Part("own", "", keep.Own);
        Part("hud", "", keep.Hud);
        foreach (var (name, ids) in keep.Queues)
            Part("queues", Status.QueueMode.GetValueOrDefault(name, ""), ids, name);
        if (keep.Requested.Count > 0)
            Part("queues", "", keep.Requested, "requested");
        var keepOut = new JsonObject
        {
            ["parts"] = parts, ["totalGb"] = Py.Round(keep.All.Sum(w => (double)SizeOf(w)) / (1L << 30), 1), ["count"] = keep.All.Count,
            ["onDisk"] = keep.All.Count(has.ContainsKey),
            ["problems"] = Py.Strings(keep.Problems.Where(p => !(p.StartsWith("no HUD item", StringComparison.Ordinal) && hud.Length == 0)).Take(5)),
        };
        var hist = State.Section("history");
        if (!Py.Truthy(hist["runs"]))
            hist["runs"] = Status.SeedRuns(Cfg.StateDir, Clock);
        var history = new JsonObject
        {
            ["runs"] = new JsonArray(Py.Arr(hist["runs"]).Take(Status.Runs).Select(e => e?.DeepClone()).ToArray()),
            ["maps"] = new JsonArray(Py.Arr(hist["maps"]).Reverse().Take(Status.Maps).Select(e => e?.DeepClone()).ToArray()),
        };
        return new JsonObject { ["today"] = today, ["totals"] = totals, ["queue"] = queue, ["keep"] = keepOut, ["history"] = history };
    }
}
