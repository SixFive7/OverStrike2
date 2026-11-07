// The sync's self-test: a map's record and the state folder over days.
using System.Text.Json.Nodes;
using OverStrike2.Core;
using OverStrike2.TestKit;
using static OverStrike2.TestKit.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>A map's record and the state folder over days: what a run cut short leaves, a new version that fails its test, the tries per
/// content version, what the state folder keeps.</summary>
static class LifetimeTests
{
    const double T0 = RunTests.T0;                       // an afternoon
    const double Hour = 3600;
    const long U0 = 1793000000, U1 = U0 + 1000, U2 = U0 + 2000;
    const string Fails = "ERROR! Download item failed (Failure).";

    internal static void Run()
    {
        LeftBehind();
        FailedVersion();
        NoOpRuns();
        ServiceStart();
        FirstPicture();
        HistoryCounts();
        Tries();
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
    static SyncConfig World(string name, JsonObject items, JsonObject baseline, double lastApi, IReadOnlySet<string>? unlisted = null)
    {
        RunTests.FakeWorld();
        RunTests.HumansNow[0] = 0;
        (FakeSteam.Asked, FakeSteam.Answers) = ([], []);
        var root = Dir(name);
        (W.State, Work, Data, GameRoot, Depotcache, BridgeUrl, BridgeSecret) =
            (Path.Combine(root, "state"), Path.Combine(root, "work"), Path.Combine(root, "data"), Path.Combine(root, "game"), Path.Combine(root, "depot"), "", "");
        Directory.CreateDirectory(W.State);
        Directory.CreateDirectory(Data);
        var listed = items.Where(kv => unlisted is null || !unlisted.Contains(kv.Key)).ToList();
        var maps = new JsonArray(listed.Select((kv, i) => (JsonNode?)O($"{{\"rank\": {i + 1}, \"id\": {kv.Key}, \"name\": \"m{kv.Key}\", \"sizeMb\": 5}}")).ToArray());
        File.WriteAllText(Path.Combine(Data, "maps.json"), Node.File(maps, 1));
        File.WriteAllText(Path.Combine(Data, "broken.json"), Node.File(O("{\"schema\": \"x\", \"maps\": [], \"packs\": [], \"removed\": [], \"blocked\": []}"), 2));
        File.WriteAllText(Path.Combine(Data, "search.json"), Node.File(new JsonObject(listed.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)$"m{kv.Key}"))), 0));
        Json(Path.Combine(W.State, "state.json"), new JsonObject { ["schema"] = 1, ["items"] = items, ["baseline"] = baseline, ["downloads"] = new JsonObject(), ["last_api"] = lastApi });
        var cfg = Cfg();
        cfg.DownloadPause = 0;
        return cfg;
    }

    static JsonObject Saved() => (JsonObject)Read(Path.Combine(W.State, "state.json"))!;

    static int Events(JsonObject state, string wid, string evt) =>
        Node.Arr(Node.Get(state["history"], "maps")).Count(e => Node.Str(e!["id"]) == wid && Node.Str(e["event"]) == evt);

    // ---- what a run cut short leaves in the state (a run killed, a work folder lost): a verdict whose publish never came, a map waiting for
    // its test with its download gone, one that had a load already, a map waiting for its test with its download there
    static void LeftBehind()
    {
        var cfg = World("left-behind", new JsonObject
        {
            ["530"] = Listed(U1, ("status", "downloaded"), ("verdict", "nonmap"), ("decided", "2026-11-03"), ("detail", O("{\"kind\": \"pack\", \"detail\": \"only models\"}")),
                             ("scan", O("{\"verdict\": \"pack\", \"maps\": [], \"models\": 9, \"files\": 9, \"names\": []}")), ("loads", new JsonArray())),
            ["531"] = Listed(U1, ("status", "downloaded"), ("why", "changed"), ("downloaded", "2026-11-03"), ("loads", new JsonArray()),
                             ("scan", O("{\"verdict\": \"map\", \"maps\": [\"lvl\"], \"models\": 0, \"files\": 1, \"names\": []}"))),
            ["532"] = Listed(U1, ("status", "tested"), ("downloaded", "2026-11-03"), ("tests", 1), ("scan", O("{\"verdict\": \"map\", \"maps\": [\"lvl\"], \"models\": 0, \"files\": 1, \"names\": []}")),
                             ("loads", new JsonArray(Load("532", "crash")))),
            ["533"] = Listed(U1, ("status", "downloaded"), ("downloaded", "2026-11-05"), ("loads", new JsonArray())),
        }, new JsonObject { ["531"] = U0, ["533"] = U1 }, T0, new HashSet<string> { "530", "532", "533" });
        T.Put(Path.Combine(Work, "staging", "steamapps", "workshop", "content", "730", "533", "533_dir.vpk"), "x");
        Hooks.Check = (_, ids, _, _, _) => (ids.ToDictionary(i => i, i => Details(i, U1)), []);
        var loads = new List<string>();
        Hooks.TestLoad = (_, wid, _, _, _, _, _) =>
        {
            loads.Add(wid);
            return Load(wid, "pass");
        };
        var report = W.Run(cfg, new FakeClock(T0 + 60), false).Run();
        var st = Saved();
        JsonNode? Field(string wid, string key) => Node.Get(Node.Get(st["items"], wid), key);
        var packs = Node.Arr(((JsonObject)Read(Path.Combine(Data, "broken.json"))!)["packs"]).Select(p => Node.Str(p!["id"])).ToList();
        Check($"left behind: a verdict whose publish never came is published, its folder long gone ({Node.Str(Field("530", "status"))}, packs {T.Show(packs)})",
              Node.Str(Field("530", "status")) == "published" && packs.Contains("530") && !FakeSteam.Asked.Contains("530"));
        Check($"left behind: a map waiting for its test, its download gone, is downloaded and tested after all ({Node.Str(Field("531", "status"))}, {T.Show(FakeSteam.Asked)})",
              FakeSteam.Asked.Contains("531") && Node.Str(Field("531", "verdict")) == "pass" && Node.Int(Node.Get(st["baseline"], "531")) == U1);
        Check($"left behind: ... also one that had a load already ({Node.Str(Field("532", "status"))}, {T.Show(FakeSteam.Asked)})", FakeSteam.Asked.Contains("532") && Node.Str(Field("532", "verdict")) == "pass");
        Check($"left behind: a map waiting for its test with its download there is tested without a new one ({T.Show(loads)})",
              !FakeSteam.Asked.Contains("533") && loads.Contains("533") && Node.Str(Field("533", "verdict")) == "pass");
        Check("left behind: the run says nothing of these as errors", Node.Arr(report["errors"]).Count == 0);
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
            var r = Node.Obj(Node.Get(st["items"], "501"));
            return $"{FakeSteam.Asked.Count} downloads, {loads.Count} loads, status {Node.Str(r["status"])}, verdict {Node.Str(r["verdict"])}, {Events(st, "501", "changed")} changed events";
        }
        Step(T0);
        var first = Saved();
        var rec = Node.Obj(Node.Get(first["items"], "501"));
        Check($"failed version: tested at the check that saw it ({Seen()})",
              FakeSteam.Asked.Count == 1 && loads.Count == 3 && Node.Str(rec["verdict"]) == "crasher" && Node.Str(rec["status"]) == "published"
              && Node.Int(Node.Get(first["baseline"], "501")) == U0 && Events(first, "501", "changed") == 1);
        var later = Step(T0 + 4 * Hour + 60);
        Check($"failed version: the next check the same day downloads and tests nothing ({Seen()})", FakeSteam.Asked.Count == 1 && loads.Count == 3);
        Check("failed version: ... its report still says the Workshop's version is not the accepted one",
              Node.StrList(Node.Get(later["check"], "changed")).Contains("501"));
        var afterSecond = Saved();
        Check($"failed version: ... nothing waits and no event says it is tested again ({Seen()})",
              !Service.BacklogWaits(afterSecond) && Events(afterSecond, "501", "changed") == 1);
        var third = Step(T0 + 8 * Hour + 120);
        var afterThird = Saved();
        Check($"failed version: ... nor at the check after it: no run to do with the map until tomorrow ({Seen()})",
              FakeSteam.Asked.Count == 1 && !Service.BacklogWaits(afterThird) && !Node.StrList(Node.Get(third["downloads"], "braked")).Contains("501"));
        Step(T0 + 14 * Hour);                          // 04:33 the next day
        var nextDay = Saved();
        Check($"failed version: the next day it is tested once more, and the history says why ({Seen()})",
              FakeSteam.Asked.Count == 2 && loads.Count == 6 && Node.Str(Node.Get(Node.Get(nextDay["items"], "501"), "decided")) == C(fc).Day()
              && Node.Arr(Node.Get(nextDay["history"], "maps")).Any(e => Node.Str(e!["event"]) == "changed" && Node.Str(e["why"]).Contains("once a day")));
        Step(T0 + 18 * Hour + 60);
        Check($"failed version: ... and not a second time that day ({Seen()})", FakeSteam.Asked.Count == 2 && loads.Count == 6);
        version = U2;
        Step(T0 + 22 * Hour + 120);
        Check($"failed version: a newer version the same day is new content: tested at once ({Seen()})",
              FakeSteam.Asked.Count == 3 && loads.Count == 9 && Node.Int(Node.Get(Node.Get(Saved()["items"], "501"), "updated")) == U2);
        verdict = "pass";
        Step(T0 + 38 * Hour);                          // 04:33 the day after
        var passed = Saved();
        Check($"failed version: a re-test that passes moves the baseline ({Seen()})",
              FakeSteam.Asked.Count == 4 && Node.Int(Node.Get(passed["baseline"], "501")) == U2 && Node.Str(Node.Get(Node.Get(passed["items"], "501"), "verdict")) == "pass");
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

    // ---- a service start: it cannot know who just left, so it waits the quiet minutes as it does after anybody
    static void ServiceStart()
    {
        var cfg = World("service-start", new JsonObject { ["701"] = Listed(U1) }, new JsonObject { ["701"] = U1 }, T0 - 5 * Hour);
        Hooks.Check = (_, ids, _, _, _) => (ids.ToDictionary(i => i, i => Details(i, U1)), []);
        var early = RunsOfService(cfg, T0, T0 + cfg.Quiet - 60);
        Check($"service start: nobody on and work waiting, still no run in the quiet minutes after a start ({early} runs)", cfg.Quiet == 600 && early == 0);
        var after = RunsOfService(cfg, T0, T0 + cfg.Quiet + 120);
        Check($"service start: ... and a run once they are over ({after} runs)", after == 1);
    }

    // ---- a run's first picture on the dashboard: the state is loaded by then, so it never shows zero totals
    static void FirstPicture()
    {
        var cfg = World("first-picture", new JsonObject { ["801"] = Listed(U1, ("verdict", "pass")) }, new JsonObject { ["801"] = U1 }, T0 - 5 * Hour);
        Hooks.Check = (_, ids, _, _, _) => (ids.ToDictionary(i => i, i => Details(i, U1)), []);
        var fc = new FakeClock(T0);
        JsonObject? first = null;
        var status = new Status(cfg, C(fc), Log.Line) { Wrote = doc => first ??= doc };
        new SyncRun(cfg, C(fc), Hooks, Log, new WebApiDoor(cfg.ApiGap, fc), true, status: status).Run();
        Check($"status: a run's first picture already has the state's totals ({Node.Str(Node.Get(first?["totals"], "tested"))} tested)", Node.Int(Node.Get(first?["totals"], "tested")) == 1);
    }

    // ---- the history's downloads are what was downloaded: a hand-over of what was staged already is none
    static void HistoryCounts()
    {
        var staged = Status.RunEntry(O("{\"serve\": {\"ok\": 3, \"bytes\": 3000, \"failed\": []}}"), 0, 0);
        Check("status: a run's history - 3 handed over without a new download are 0 downloads, 0 bytes", Node.Int(staged["downloads"]) == 0 && Node.Int(staged["bytes"]) == 0);
        var both = Status.RunEntry(O("{\"downloads\": {\"ok\": 2, \"bytes\": 200}, \"serve\": {\"ok\": 3, \"bytes\": 3000, \"downloads\": 1, \"dl_bytes\": 1000}}"), 0, 0);
        Check("status: a run's history - the test's downloads and serve's own", Node.Int(both["downloads"]) == 3 && Node.Int(both["bytes"]) == 1200);
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
        JsonObject Rec() => Node.Obj(Node.Get(Saved()["items"], "502"));
        string Seen() => $"attempts {Node.Str(Rec()["attempts"])}, status {Node.Str(Rec()["status"])}: {Node.Str(Rec()["evidence"])}";
        W.Run(cfg, fc, false).Run();
        Check($"tries: a map downloaded 3 times in its life has its full 3 tries for a new version ({Seen()})",
              Node.Int(Rec()["attempts"]) == 1 && Node.Str(Rec()["status"]) == "retry" && Node.Str(Rec()["evidence"]).Contains("1 of 3 tries"));
        fc.T = T0 + 1800;
        W.Run(cfg, fc, false).Run();
        fc.T = T0 + 3600;
        W.Run(cfg, fc, false).Run();
        Check($"tries: the third failure of one version is the last ({Seen()})",
              Node.Int(Rec()["attempts"]) == 3 && Node.Str(Rec()["status"]) == "failed" && Node.Str(Rec()["evidence"]).Contains("3 of 3 tries"));
        var asked = FakeSteam.Asked.Count;
        fc.T = T0 + 4 * Hour + 60;
        W.Run(cfg, fc, false).Run();
        Check($"tries: ... the same version is not tried again ({Seen()})", FakeSteam.Asked.Count == asked && Node.Str(Rec()["status"]) == "failed");
        version = U2;
        FakeSteam.Answers = [];
        fc.T = T0 + 8 * Hour + 120;
        W.Run(cfg, fc, false).Run();
        var after = Saved();
        Check($"tries: a newer version is downloaded and tested after all ({Seen()})",
              FakeSteam.Asked.Count == asked + 1 && Node.Int(Node.Get(after["baseline"], "502")) == U2 && Node.Str(Rec()["verdict"]) == "pass");
        Check($"tries: a download that worked leaves no tries counted ({Seen()})", Node.Int(Rec()["attempts"]) == 0);
        var rec = Listed(U1, ("attempts", 2), ("status", "retry"));
        var st = new SyncState(Path.Combine(W.State, "other.json"));
        st.Items["503"] = rec;
        st.Remember(Details("503", U2), "2026-11-01", "new");
        Check("tries: a map that waits for its download has its tries back when a newer version appears", Node.Int(rec["attempts"]) == 0 && Node.Str(rec["status"]) == "retry");
        rec["attempts"] = 2;
        st.Remember(Details("503", U2), "2026-11-01", "new");
        Check("tries: ... the same version keeps them", Node.Int(rec["attempts"]) == 2);
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
        var own = Path.GetFileName(Node.Str(Node.Get(run.Report["published"], "backup")));
        Check($"backups: a publish leaves the newest {SyncRun.BackupsKept}, its own among them ({stamps.Count}, oldest {stamps[0]})",
              stamps.Count == SyncRun.BackupsKept && stamps.Contains(own) && stamps[0] == "20261006-120000" && File.Exists(Path.Combine(folder, own, "maps.json")));
        Check("backups: a folder that is no backup is left alone", File.Exists(Path.Combine(folder, "keep-me", "note.txt")));
    }

    // ---- every run writes reports/<day>-<hhmm>[-manual].json and .txt: enough stay for the status file's history
    static void Reports()
    {
        var cfg = World("reports", new JsonObject { ["520"] = Listed(U0) }, new JsonObject { ["520"] = U0 }, T0);
        var folder = Path.Combine(W.State, "reports");
        for (var i = 0; i < 130; i++)
        {
            var stem = $"2026-10-{1 + i / 24:00}-{i % 24:00}00{(i % 2 == 0 ? "" : "-manual")}";
            T.Put(Path.Combine(folder, stem + ".json"), "{\"started\": \"a day\", \"ended\": \"that day\", \"day\": \"2026-10-01\"}");
            T.Put(Path.Combine(folder, stem + ".txt"), "a report");
        }
        T.Put(Path.Combine(folder, "notes.md"), "by hand");
        T.Put(Path.Combine(folder, "2026-10-30-1200.txt"), "a report without its json");
        // the names older versions gave their reports, all of them older than any of the above, and the first test's, which is no report of the service's
        foreach (var older in new[] { "2026-09-01", "2026-09-02-day", "2026-09-03-manual-0540" })
        {
            T.Put(Path.Combine(folder, older + ".json"), "{\"night\": \"2026-09-01\"}");
            T.Put(Path.Combine(folder, older + ".txt"), "a report");
        }
        T.Put(Path.Combine(folder, "probe-2026-09-01-manual-0310.json"), "{\"night\": \"2026-09-01\"}");
        var run = W.Run(cfg, new FakeClock(T0));
        run.Ledger.Load();
        run.State.Load();
        run.Finish();
        var json = Directory.GetFiles(folder, "*.json").Select(p => Path.GetFileNameWithoutExtension(p)!).Where(n => !n.StartsWith("probe", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var text = Directory.GetFiles(folder, "*.txt").Select(p => Path.GetFileNameWithoutExtension(p)!).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Check($"reports: a run's end leaves the newest {SyncRun.ReportsKept} with their texts, its own among them ({json.Count} and {text.Count})",
              json.Count == SyncRun.ReportsKept && text.Count == SyncRun.ReportsKept + 1 && File.Exists(Node.Str(run.Report["report_path"])) && !json.Contains("2026-10-01-0000"));
        Check($"reports: the names older versions gave theirs go the same way ({json[0]})", !json.Any(n => n.StartsWith("2026-09", StringComparison.Ordinal)) && !text.Any(n => n.StartsWith("2026-09", StringComparison.Ordinal)));
        Check("reports: what is no report of a run is left alone", File.Exists(Path.Combine(folder, "notes.md")) && File.Exists(Path.Combine(folder, "probe-2026-09-01-manual-0310.json")));
    }
}
