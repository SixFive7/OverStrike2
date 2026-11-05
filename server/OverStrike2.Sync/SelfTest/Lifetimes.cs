using System.Text.Json.Nodes;
using OverStrike2.Core;
using static OverStrike2.Sync.SelfTest.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>A clock for a whole service loop: Sleep moves it on, and past the end it says stop, as the service's own clock does at a SIGTERM.</summary>
sealed class EndingClock(double t, double end) : IClock
{
    public double T { get; private set; } = t;
    public DateTimeOffset Now => SyncClock.At(T);

    public void Sleep(TimeSpan span)
    {
        if (span > TimeSpan.Zero)
            T += span.TotalSeconds;
        if (T >= end)
            throw new StopRequested();
    }
}

/// <summary>A map's record and the state folder over days: a new version that fails its test, the download tries per content version, and
/// what the state folder keeps.</summary>
static class Lifetimes
{
    const double T0 = Part2.T0;                       // an afternoon
    const double Hour = 3600;
    const long U0 = 1793000000, U1 = U0 + 1000, U2 = U0 + 2000;
    const string Fails = "ERROR! Download item failed (Failure).";

    public static void Run()
    {
        OldLedger();
        FailedVersion();
        NoOpRuns();
        Tries();
        OldCounts();
        Backups();
        Reports();
        Hooks.Check = (_, _, _, _, _) => ([], []);
    }

    static JsonObject Details(string wid, long updated) => O($$"""
        {"publishedfileid": "{{wid}}", "result": 1, "time_created": 100, "time_updated": {{updated}}, "hcontent_file": "9{{wid}}", "subscriptions": 5,
         "lifetime_subscriptions": 7, "favorited": 2, "lifetime_favorited": 3, "views": 11, "consumer_appid": 730, "title": "map {{wid}}", "tags": [{"tag": "Armsrace"}]}
        """);

    /// <summary>A listed map's record, as a published one has it.</summary>
    static JsonObject Listed(long updated, params (string Key, JsonNode Value)[] fields)
    {
        var o = O($$"""
            {"status": "published", "ws": "ok", "app": 730, "tags": ["Armsrace"], "subs": 5, "lifetime": 7, "size": 1000, "created": 100, "updated": {{updated}},
             "title": "a map", "description": "", "first": "2026-10-01", "why": "new", "attempts": 0, "tests": 0, "creator": ""}
            """);
        foreach (var (k, v) in fields)
            o[k] = v.DeepClone();
        return o;
    }

    static JsonObject Load(string wid, string verdict) =>
        O($"{{\"id\": \"{wid}\", \"verdict\": \"{verdict}\", \"reason\": \"the test\", \"level\": \"lvl\", \"load_s\": 1.0, \"mode\": \"armsrace\", \"bots\": 0}}");

    /// <summary>A fresh world: the listed maps on the bridge's list, a state with these records, the API steps due.</summary>
    static SyncConfig World(string name, JsonObject items, JsonObject baseline, double lastApi)
    {
        Part2.FakeWorld();
        Part2.HumansNow[0] = 0;
        (FakeSteam.Asked, FakeSteam.Answers) = ([], []);
        var root = Dir(name);
        (W.State, Work, Data, GameRoot, Depotcache, BridgeUrl, BridgeSecret) =
            (Path.Combine(root, "state"), Path.Combine(root, "work"), Path.Combine(root, "data"), Path.Combine(root, "game"), Path.Combine(root, "depot"), "", "");
        Directory.CreateDirectory(W.State);
        Directory.CreateDirectory(Data);
        var maps = new JsonArray(items.Select((kv, i) => (JsonNode?)O($"{{\"rank\": {i + 1}, \"id\": {kv.Key}, \"name\": \"m{kv.Key}\", \"sizeMb\": 5}}")).ToArray());
        File.WriteAllText(Path.Combine(Data, "maps.json"), Py.DumpsFile(maps, 1));
        File.WriteAllText(Path.Combine(Data, "broken.json"), Py.DumpsFile(O("{\"schema\": \"x\", \"maps\": [], \"packs\": [], \"removed\": [], \"blocked\": []}"), 2));
        File.WriteAllText(Path.Combine(Data, "search.json"), Py.DumpsFile(new JsonObject(items.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)$"m{kv.Key}"))), 0));
        Json(Path.Combine(W.State, "state.json"), new JsonObject { ["schema"] = 1, ["items"] = items, ["baseline"] = baseline, ["downloads"] = new JsonObject(), ["last_api"] = lastApi });
        var cfg = Cfg();
        cfg.DownloadPause = 0;
        return cfg;
    }

    static JsonObject Saved() => (JsonObject)Read(Path.Combine(W.State, "state.json"))!;

    static int Events(JsonObject state, string wid, string evt) =>
        Py.Arr(Py.Get(state["history"], "maps")).Count(e => Py.Str(e!["id"]) == wid && Py.Str(e["event"]) == evt);

    // ---- a ledger as the deployed Python sync wrote it: "nights", no "days". It must keep counting, or the check and the ratings stop at the switch
    static void OldLedger()
    {
        var path = Path.Combine(Dir("old-ledger"), "api-ledger.json");
        var calls = $"[[{T0 - 600}, \"GetPublishedFileDetails\", 200], [{T0 - 300}, \"QueryFiles\", null], [{T0 - 2 * 86400}, \"QueryFiles\", 200]]";
        File.WriteAllText(path, $"{{\"schema\": 1, \"calls\": {calls}, \"nights\": {{\"2026-11-05\": 472, \"2026-11-04\": 31}}, \"stops\": {{\"2026-11-04\": \"HTTP 429\"}}}}\n");
        var fc = new FakeClock(T0);
        var led = new ApiLedger(path, 10000, C(fc));
        led.Load();
        Check($"old ledger: \"nights\" and no \"days\" parses ({led.Broken ?? "no problem"})", led.Broken is null && led.Data is not null);
        Check("old ledger: ... its count per day is the day's count", led.Count("2026-11-05") == 472 && led.Count("2026-11-04") == 31);
        Check("old ledger: ... its calls still count for the 24-hour cap, its stops still stop", led.Last24h() == 2 && led.Stopped("2026-11-04") == "HTTP 429");
        led.Reserve("2026-11-05", "QueryFiles");
        var saved = (JsonObject)Read(path)!;
        Check("old ledger: ... the next write says \"days\", goes on counting, and keeps the calls and the stops",
              !saved.ContainsKey("nights") && Py.Int(Py.Get(saved["days"], "2026-11-05")) == 473 && Py.Int(Py.Get(saved["days"], "2026-11-04")) == 31
              && Py.Arr(saved["calls"]).Count == 3 && Py.Str(Py.Get(saved["stops"], "2026-11-04")) == "HTTP 429");
        var again = new ApiLedger(path, 10000, C(fc));
        again.Load();
        Check("old ledger: ... and that file reads back", again.Broken is null && again.Count("2026-11-05") == 473 && again.Last24h() == 3);
        File.WriteAllText(path, "{\"schema\": 1, \"calls\": [], \"days\": {\"2026-11-05\": 5}, \"nights\": {\"2026-11-05\": 9}, \"stops\": {}}\n");
        var both = new ApiLedger(path, 10000, C(fc));
        both.Load();
        Check("old ledger: where both are there, \"days\" counts", both.Broken is null && both.Count("2026-11-05") == 5);
        File.WriteAllText(path, "{\"schema\": 1, \"calls\": [], \"nights\": 5, \"stops\": {}}\n");
        var bad = new ApiLedger(path, 10000, C(fc));
        bad.Load();
        Check("old ledger: \"nights\" of another shape is still no ledger: no Web API call until it is fixed", bad.Broken is not null && bad.Data is null);
    }

    // ---- a listed map whose new version fails its test: tested once when it is seen, then at most once a day, and nothing waits in between
    static void FailedVersion()
    {
        var cfg = World("failed-version", new JsonObject { ["501"] = Listed(U0) }, new JsonObject { ["501"] = U0 }, T0 - 5 * Hour);
        var version = U1;
        Hooks.Check = (_, ids, _, _, _) => (ids.ToDictionary(i => i, i => Details(i, version)), []);
        var loads = new List<string>();
        var verdict = "crash";
        Hooks.TestLoad = (_, wid, _, _, _, _, _) =>
        {
            loads.Add(wid);
            return Load(wid, verdict);
        };
        var fc = new FakeClock(T0);
        JsonObject Step(double at)
        {
            fc.T = at;
            return W.Run(cfg, fc, false).Run();
        }
        string Seen()
        {
            var st = Saved();
            var r = Py.Obj(Py.Get(st["items"], "501"));
            return $"{FakeSteam.Asked.Count} downloads, {loads.Count} loads, status {Py.Str(r["status"])}, verdict {Py.Str(r["verdict"])}, {Events(st, "501", "changed")} changed events";
        }
        Step(T0);
        var first = Saved();
        var rec = Py.Obj(Py.Get(first["items"], "501"));
        Check($"failed version: tested at the check that saw it ({Seen()})",
              FakeSteam.Asked.Count == 1 && loads.Count == 3 && Py.Str(rec["verdict"]) == "crasher" && Py.Str(rec["status"]) == "published"
              && Py.Int(Py.Get(first["baseline"], "501")) == U0 && Events(first, "501", "changed") == 1);
        var later = Step(T0 + 4 * Hour + 60);
        Check($"failed version: the next check the same day downloads and tests nothing ({Seen()})", FakeSteam.Asked.Count == 1 && loads.Count == 3);
        Check("failed version: ... its report still says the Workshop's version is not the accepted one",
              Py.StrList(Py.Get(later["check"], "changed")).Contains("501"));
        var afterSecond = Saved();
        Check($"failed version: ... nothing waits and no event says it is tested again ({Seen()})",
              !Service.BacklogWaits(afterSecond) && Events(afterSecond, "501", "changed") == 1);
        var third = Step(T0 + 8 * Hour + 120);
        var afterThird = Saved();
        Check($"failed version: ... nor at the check after it: no run to do with the map until tomorrow ({Seen()})",
              FakeSteam.Asked.Count == 1 && !Service.BacklogWaits(afterThird) && !Py.StrList(Py.Get(third["downloads"], "braked")).Contains("501"));
        Step(T0 + 14 * Hour);                          // 04:33 the next day
        var nextDay = Saved();
        Check($"failed version: the next day it is tested once more, and the history says why ({Seen()})",
              FakeSteam.Asked.Count == 2 && loads.Count == 6 && Py.Str(Py.Get(Py.Get(nextDay["items"], "501"), "decided")) == C(fc).Day()
              && Py.Arr(Py.Get(nextDay["history"], "maps")).Any(e => Py.Str(e!["event"]) == "changed" && Py.Str(e["why"]).Contains("once a day")));
        Step(T0 + 18 * Hour + 60);
        Check($"failed version: ... and not a second time that day ({Seen()})", FakeSteam.Asked.Count == 2 && loads.Count == 6);
        version = U2;
        Step(T0 + 22 * Hour + 120);
        Check($"failed version: a newer version the same day is new content: tested at once ({Seen()})",
              FakeSteam.Asked.Count == 3 && loads.Count == 9 && Py.Int(Py.Get(Py.Get(Saved()["items"], "501"), "updated")) == U2);
        verdict = "pass";
        Step(T0 + 38 * Hour);                          // 04:33 the day after
        var passed = Saved();
        Check($"failed version: a re-test that passes moves the baseline ({Seen()})",
              FakeSteam.Asked.Count == 4 && Py.Int(Py.Get(passed["baseline"], "501")) == U2 && Py.Str(Py.Get(Py.Get(passed["items"], "501"), "verdict")) == "pass");
        Step(T0 + 42 * Hour + 60);
        Check($"failed version: ... and it is not changed any more ({Seen()})", FakeSteam.Asked.Count == 4 && !Service.BacklogWaits(Saved()));
    }

    /// <summary>Runs in a whole service loop, by the reports they leave: one report per run (they are named by the minute).</summary>
    static int RunsOfService(SyncConfig cfg, double from, double to)
    {
        var clock = new EndingClock(from, to);
        var fc = new FakeClock(from);
        try
        {
            new Service(cfg, new SyncClock(clock, Paris), Hooks, Log, new WebApiDoor(2, fc)).Serve();
        }
        catch (StopRequested)
        {
        }
        return Directory.GetFiles(Path.Combine(W.State, "reports"), "*.json").Length;
    }

    // ---- nothing a run can do: the service does not start a run every 30 minutes for it
    static void NoOpRuns()
    {
        var day = new SyncClock(new FakeClock(T0), Paris).Day();
        // a map downloaded twice today already (the brake says not today), and one the Workshop no longer hands out
        var cfg = World("no-op", new JsonObject
        {
            ["601"] = Listed(U1, ("status", "queued"), ("why", "changed")),
            ["602"] = Listed(U1, ("status", "queued"), ("why", "changed"), ("ws", "removed")),
        }, new JsonObject { ["601"] = U1, ["602"] = U1 }, T0 - 5 * Hour);
        var st = Saved();
        st["downloads"] = new JsonObject { [day] = O("{\"items\": 2, \"bytes\": 2000, \"maps\": {\"601\": 2}}") };
        Json(Path.Combine(W.State, "state.json"), st);
        Hooks.Check = (_, ids, _, _, _) => (ids.ToDictionary(i => i, i => Details(i, U1)), []);
        var runs = RunsOfService(cfg, T0, T0 + 8 * Hour);
        Check($"no-op runs: a map only the brake holds and a map gone from the Workshop start no run between two checks ({runs} runs in 8 h, the checks are 2 or 3)", runs <= 3);
        Check("no-op runs: ... and the brake held", FakeSteam.Asked.Count == 0);
        var now = new SyncClock(new FakeClock(T0), Paris);
        JsonObject Report(string json) => O(json);
        JsonNode Api = T0 - 600;
        Check("no-op runs: a run nothing stopped looks again at the next check",
              Service.IdleUntil(cfg, now, Report("{\"downloads\": {\"ok\": 0, \"failed\": [], \"braked\": [\"601\"]}}"), Api) == T0 - 600 + cfg.ApiEvery);
        foreach (var (what, json) in new[]
        {
            ("a download stopped", "{\"downloads\": {\"stop\": \"somebody was on the server until the deadline\"}}"),
            ("a download failed", "{\"downloads\": {\"failed\": [[\"7\", \"x\"]]}}"),
            ("the tests stopped", "{\"tests\": {\"stop\": \"the run's deadline\"}}"),
            ("a map waits for its verdict", "{\"tests\": {\"waiting_verdict\": 2}}"),
            ("the hand-over stopped", "{\"serve\": {\"stop\": \"the run's deadline\"}}"),
            ("the publish had to wait", "{\"published\": {\"skipped\": \"somebody was on the server until the deadline\"}}"),
        })
            Check($"no-op runs: ... {what}: a new try after the usual wait", Service.IdleUntil(cfg, now, Report(json), Api) == T0 + Service.IdleWait);
        Check("no-op runs: ... a check due within the wait does not shorten it", Service.IdleUntil(cfg, now, Report("{}"), T0 - cfg.ApiEvery + 60) == T0 + Service.IdleWait);
        var evening = now.AtTime(new TimeOfDay(23, 0), T0);
        var late = new SyncClock(new FakeClock(evening), Paris);
        Check("no-op runs: ... a new day comes before the next check: the brake is over, it looks then",
              Service.IdleUntil(cfg, late, Report("{}"), evening + 3 * Hour) == now.AtTime(new TimeOfDay(0, 0), evening + 86400));
    }

    // ---- the download tries are per content version
    static void Tries()
    {
        var cfg = World("tries", new JsonObject { ["502"] = Listed(U0, ("attempts", 3), ("downloaded", "2026-10-20")) }, new JsonObject { ["502"] = U0 }, T0 - 5 * Hour);
        var version = U1;
        Hooks.Check = (_, ids, _, _, _) => (ids.ToDictionary(i => i, i => Details(i, version)), []);
        Hooks.TestLoad = (_, wid, _, _, _, _, _) => Load(wid, "pass");
        FakeSteam.Answers = new() { ["502"] = Fails };
        var fc = new FakeClock(T0);
        JsonObject Rec() => Py.Obj(Py.Get(Saved()["items"], "502"));
        string Seen() => $"attempts {Py.Str(Rec()["attempts"])}, status {Py.Str(Rec()["status"])}: {Py.Str(Rec()["evidence"])}";
        W.Run(cfg, fc, false).Run();
        Check($"tries: a map downloaded 3 times in its life has its full 3 tries for a new version ({Seen()})",
              Py.Int(Rec()["attempts"]) == 1 && Py.Str(Rec()["status"]) == "retry" && Py.Str(Rec()["evidence"]).Contains("1 of 3 tries"));
        fc.T = T0 + 1800;
        W.Run(cfg, fc, false).Run();
        fc.T = T0 + 3600;
        W.Run(cfg, fc, false).Run();
        Check($"tries: the third failure of one version is the last ({Seen()})",
              Py.Int(Rec()["attempts"]) == 3 && Py.Str(Rec()["status"]) == "failed" && Py.Str(Rec()["evidence"]).Contains("3 of 3 tries"));
        var asked = FakeSteam.Asked.Count;
        fc.T = T0 + 4 * Hour + 60;
        W.Run(cfg, fc, false).Run();
        Check($"tries: ... the same version is not tried again ({Seen()})", FakeSteam.Asked.Count == asked && Py.Str(Rec()["status"]) == "failed");
        version = U2;
        FakeSteam.Answers = [];
        fc.T = T0 + 8 * Hour + 120;
        W.Run(cfg, fc, false).Run();
        var after = Saved();
        Check($"tries: a newer version is downloaded and tested after all ({Seen()})",
              FakeSteam.Asked.Count == asked + 1 && Py.Int(Py.Get(after["baseline"], "502")) == U2 && Py.Str(Rec()["verdict"]) == "pass");
        Check($"tries: a download that worked leaves no tries counted ({Seen()})", Py.Int(Rec()["attempts"]) == 0);
        var rec = Listed(U1, ("attempts", 2), ("status", "retry"));
        var st = new SyncState(Path.Combine(W.State, "other.json"));
        st.Items["503"] = rec;
        st.Remember(Details("503", U2), "2026-11-01", "new");
        Check("tries: a map that waits for its download has its tries back when a newer version appears", Py.Int(rec["attempts"]) == 0 && Py.Str(rec["status"]) == "retry");
        rec["attempts"] = 2;
        st.Remember(Details("503", U2), "2026-11-01", "new");
        Check("tries: ... the same version keeps them", Py.Int(rec["attempts"]) == 2);
    }

    // ---- a state the old count left: a "5 of 3 tries" comes back for one more go, a real "3 of 3" stays
    static void OldCounts()
    {
        var cfg = World("old-counts", new JsonObject
        {
            ["504"] = Listed(U1, ("status", "failed"), ("attempts", 5), ("why", "changed"), ("evidence", "SteamCMD could not download it (x), 5 of 3 tries")),
            ["505"] = Listed(U1, ("status", "failed"), ("attempts", 3), ("why", "changed"), ("evidence", "SteamCMD could not download it (x), 3 of 3 tries")),
            ["506"] = Listed(U1, ("attempts", 7)),
        }, new JsonObject { ["504"] = U0, ["505"] = U0, ["506"] = U1 }, T0 - 5 * Hour);
        Hooks.Check = (_, ids, _, _, _) => (ids.ToDictionary(i => i, i => Details(i, U1)), []);
        Hooks.TestLoad = (_, wid, _, _, _, _, _) => Load(wid, "pass");
        W.Run(cfg, new FakeClock(T0), false).Run();
        var st = Saved();
        JsonNode? Field(string wid, string key) => Py.Get(Py.Get(st["items"], wid), key);
        Check($"old counts: 5 of 3 tries is downloaded and tested after all ({Py.Str(Field("504", "status"))}, {T.Show(FakeSteam.Asked)})",
              FakeSteam.Asked.Contains("504") && Py.Str(Field("504", "verdict")) == "pass" && Py.Int(Py.Get(st["baseline"], "504")) == U1);
        Check($"old counts: a real 3 of 3 of this version stays failed ({Py.Str(Field("505", "status"))})", !FakeSteam.Asked.Contains("505") && Py.Str(Field("505", "status")) == "failed");
        Check("old counts: a published map's count is cleared and the map left alone", Py.Int(Field("506", "attempts")) == 0 && !FakeSteam.Asked.Contains("506") && Py.Str(Field("506", "status")) == "published");
    }

    // ---- every publish copies the lists to backups/<stamp>/: the newest few stay
    static void Backups()
    {
        var cfg = World("backups", new JsonObject { ["510"] = Listed(U0) }, new JsonObject { ["510"] = U0 }, T0);
        var folder = Path.Combine(W.State, "backups");
        for (var i = 1; i <= 14; i++)
            T.Put(Path.Combine(folder, $"202610{i:00}-120000", "maps.json"), "[]");
        T.Put(Path.Combine(folder, "keep-me", "note.txt"), "by hand");
        var run = W.Run(cfg, new FakeClock(T0));
        run.Ledger.Load();
        run.State.Load();
        var pass = O("{\"title\": \"new\", \"tags\": [\"Armsrace\"], \"size\": 1048576, \"subs\": 3, \"lifetime\": 3, \"description\": \"d\", \"status\": \"tested\", \"verdict\": \"pass\", \"updated\": 5}");
        run.State.Items["511"] = pass;
        run.Publish([new("511", "pass", O("{\"level\": \"x\"}"), pass)], [], new Dictionary<string, JsonObject>());
        var stamps = Directory.GetDirectories(folder).Select(p => Path.GetFileName(p)!).Where(n => n != "keep-me").OrderBy(n => n, StringComparer.Ordinal).ToList();
        var own = Path.GetFileName(Py.Str(Py.Get(run.Report["published"], "backup")));
        Check($"backups: a publish leaves the newest {SyncRun.BackupsKept}, its own among them ({stamps.Count}, oldest {stamps[0]})",
              stamps.Count == SyncRun.BackupsKept && stamps.Contains(own) && stamps[0] == "20261006-120000" && File.Exists(Path.Combine(folder, own, "maps.json")));
        Check("backups: a folder that is no backup is left alone", File.Exists(Path.Combine(folder, "keep-me", "note.txt")));
    }

    // ---- every run writes reports/<day>-<hhmm>[-manual].json and .txt: enough stay for the status file's history
    static void Reports()
    {
        var cfg = World("reports", new JsonObject { ["520"] = Listed(U0) }, new JsonObject { ["520"] = U0 }, T0);
        var folder = Path.Combine(W.State, "reports");
        var clock = new SyncClock(new FakeClock(T0), Paris);
        for (var i = 0; i < 130; i++)
        {
            var stem = $"2026-10-{1 + i / 24:00}-{i % 24:00}00{(i % 2 == 0 ? "" : "-manual")}";
            T.Put(Path.Combine(folder, stem + ".json"), "{\"started\": \"a day\", \"ended\": \"that day\", \"day\": \"2026-10-01\"}");
            T.Put(Path.Combine(folder, stem + ".txt"), "a report");
        }
        T.Put(Path.Combine(folder, "notes.md"), "by hand");
        T.Put(Path.Combine(folder, "2026-10-30-1200.txt"), "a report without its json");
        var run = W.Run(cfg, new FakeClock(T0));
        run.Ledger.Load();
        run.State.Load();
        run.Finish();
        var json = Directory.GetFiles(folder, "*.json").Select(p => Path.GetFileNameWithoutExtension(p)!).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var text = Directory.GetFiles(folder, "*.txt").Select(p => Path.GetFileNameWithoutExtension(p)!).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Check($"reports: a run's end leaves the newest {SyncRun.ReportsKept} with their texts, its own among them ({json.Count} and {text.Count})",
              json.Count == SyncRun.ReportsKept && text.Count == SyncRun.ReportsKept + 1 && File.Exists(Py.Str(run.Report["report_path"])) && !json.Contains("2026-10-01-0000"));
        Check("reports: what is no report of a run is left alone", File.Exists(Path.Combine(folder, "notes.md")));
        Check($"reports: the status file's history can still start from them ({Status.SeedRuns(W.State, clock).Count} of {Status.Runs})",
              SyncRun.ReportsKept >= Status.Runs && Status.SeedRuns(W.State, clock).Count == Status.Runs);
    }
}
