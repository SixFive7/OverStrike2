// The sync's self-test: whole runs on stand-ins for SteamCMD and the test engine.
using System.Text.Json.Nodes;
using OverStrike2.Core;
using OverStrike2.TestKit;
using static OverStrike2.TestKit.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>Whole runs in batches on stand-ins for SteamCMD and the test engine: the status file, a join during the tests, Steam's
/// push-back, the stalls, a map gone from the Workshop, workshop.json.</summary>
static class RunTests
{
    public const double T0 = 1793540000.0;           // an afternoon
    public static JsonObject StateAfter = [];
    public static string RunData = "", RunRoot = "";
    public static SyncConfig RunCfg = null!;

    public static readonly List<string> Loads = [];
    public static readonly int[] HumansNow = [0];

    public static JsonObject FakeLoad(string wid, Func<bool> stopCheck)
    {
        Loads.Add(wid);
        if (stopCheck())
            return O($"{{\"id\": \"{wid}\", \"verdict\": \"abort\", \"reason\": \"somebody came on the game server\"}}");
        return O($"{{\"id\": \"{wid}\", \"verdict\": \"pass\", \"reason\": \"loads\", \"level\": \"lvl\", \"load_s\": 1.0, \"mode\": \"wingman\", \"bots\": 0}}");
    }

    public static void FakeWorld()
    {
        Hooks.SteamCmd = (_, staging, _, _) => new FakeSteam(staging);
        Hooks.TestEngine = (_, _, _, _, _) => new FakeEngine();
        Hooks.ScanItem = _ => O("{\"verdict\": \"map\", \"maps\": [\"lvl\"], \"models\": 0, \"files\": 1, \"names\": []}");
        Hooks.BuildTestTree = (_, _, _, _) => "test tree built";
        Hooks.KillStaleEngines = (_, _) => 0;
        Hooks.PatchVersion = _ => "1.41.9.4";
        Hooks.Check = (_, _, _, _, _) => ([], []);
        Hooks.TestLoad = (_, wid, _, _, _, stop, _) => FakeLoad(wid, stop);
        Hooks.HumansOn = (_, _) => HumansNow[0];
    }

    public static JsonObject RunItem(int i) => O($$"""
        {"status": "queued", "ws": "ok", "app": 730, "tags": ["Wingman"], "subs": {{10 * (i + 1)}}, "lifetime": 1, "size": 1000, "created": {{T0 - 100 * 86400}},
         "title": "m{{i}}", "description": "", "first": "2026-11-01", "why": "new"}
        """);

    static JsonObject Item(int i, params (string Key, JsonNode Value)[] fields)
    {
        var o = RunItem(i);
        foreach (var (k, v) in fields)
            o[k] = v.DeepClone();
        return o;
    }

    static string StateFile => Path.Combine(RunRoot, "state", "state.json");

    // ---- a whole run in batches, on stand-ins for SteamCMD and the test engine
    internal static void WholeRun(string root)
    {
        FakeWorld();
        RunRoot = Path.Combine(root, "run");
        RunData = Path.Combine(RunRoot, "data");
        Directory.CreateDirectory(RunData);
        (State, Work, Data) = (Path.Combine(RunRoot, "state"), Path.Combine(RunRoot, "work"), RunData);
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(RunData, "maps.json"), Node.File(A("[{\"rank\": 1, \"id\": 11, \"name\": \"a\", \"sizeMb\": 5}]"), 1));
        File.WriteAllText(Path.Combine(RunData, "broken.json"), Node.File(O("{\"schema\": \"x\", \"maps\": [], \"packs\": [], \"removed\": [], \"blocked\": []}"), 2));
        File.WriteAllText(Path.Combine(RunData, "search.json"), Node.File(O("{\"11\": \"a 11\"}"), 0));
        var items = new JsonObject();
        for (var i = 0; i < 7; i++)
            items[(1000 + i).ToString()] = RunItem(i);
        items["2000"] = Item(0, ("subs", 1), ("created", T0 - 86400), ("title", "new upload"));
        Json(StateFile, new JsonObject { ["schema"] = 1, ["items"] = items, ["baseline"] = new JsonObject(), ["downloads"] = new JsonObject() });
        var cfg = Cfg();
        (cfg.Batch, cfg.DownloadPause) = (2, 0);
        RunCfg = cfg;
        var fc = new FakeClock(T0);
        var nr = W.Run(cfg, fc);
        FakeSteam.Answers = new() { ["1002"] = "Rate Limit Exceeded" };   // Steam pushes back at the sixth, and that ends the run's downloads
        var rep = nr.Run();
        FakeSteam.Answers = [];
        Check("run: a new upload first, then strictly by subscribers", FakeSteam.Asked.Take(5).SequenceEqual(["2000", "1006", "1005", "1004", "1003"]));
        Check("run: every download was tested", Loads.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(FakeSteam.Asked.Take(5).OrderBy(x => x, StringComparer.Ordinal)));
        Check("run: 5 downloads in batches of 2: 3 logins, one engine for each batch with work", FakeSteam.Logins == 3 && Node.Int(Node.Get(rep["downloads"], "ok")) == 5);
        Check("run: Steam's push-back ends it, with the reason", Node.StrOr(Node.Get(rep["downloads"], "stop")).Contains("Rate Limit Exceeded"));
        var wing = Node.Arr(Read(Path.Combine(RunData, "maps-wingman.json")));
        Check("run: every batch published (5 maps on the list, the report adds them up)",
              wing.Count == 5 && Node.Arr(Node.Get(Node.Get(rep["published"], "modes"), "wingman")).Count == 5 && Node.StrList(Node.Get(rep["published"], "files")).Contains("maps-wingman.json"));
        Check("run: staging is empty again", Directory.GetFileSystemEntries(Path.Combine(RunRoot, "work", "staging", "steamapps", "workshop", "content", "730")).Length == 0);
        StateAfter = (JsonObject)Read(StateFile)!;
        Check("run: the rest waits, the day's book says 5", Node.Int(Node.Get(Node.Get(StateAfter["downloads"], "2026-11-01"), "items")) == 5
              && Node.Obj(StateAfter["items"]).Count(kv => Node.Str(Node.Get(kv.Value, "status")) is "queued" or "retry") == 3);
        Check("run: the mail's estimate counts what is left", Node.Int(Node.Get(rep["backlog"], "items")) == 3
              && File.ReadAllText(Node.Str(rep["report_path"])[..^5] + ".txt").Contains("Backlog: 3 item(s)"));
        Check("run: the state remembers the run, and when the API steps last ran", Node.Float(Node.Get(StateAfter["last_run"], "ended")) > 0 && Node.Float(StateAfter["last_api"]) == T0);
        Check("run: the estimate counts on the day without the restarts' stretches", Node.Float(Node.Get(rep["backlog"], "hours")) == 22);
        Check("run: Steam's push-back is a problem the mail names (not sent here: no SMTP host)",
              Node.StrList(rep["issues"]).Any(i => i.Contains("SteamCMD pushed back")) && Node.Str(rep["mail"]).StartsWith("NOT sent: no SMTP host", StringComparison.Ordinal));
    }

    /// <summary>What in `doc` breaks schema 1 (the dashboard's contract: fields may be added, never renamed or dropped).</summary>
    public static List<string> StatusProblems(JsonNode? doc)
    {
        var result = new List<string>();
        JsonNode? Need(JsonNode? obj, string key, string kinds, string where)
        {
            if (obj is not JsonObject o || !o.ContainsKey(key))
            {
                result.Add($"{where}.{key} missing");
                return null;
            }
            var v = o[key];
            var kind = v switch
            {
                null => "null", JsonObject => "dict", JsonArray => "list",
                _ when Node.IsString(v) => "str", _ when Node.IsBool(v) => "bool", _ when Node.Integer(v) is not null => "int", _ when Node.IsNumber(v) => "float", _ => "?",
            };
            if (!kinds.Split('|').Contains(kind))
                result.Add($"{where}.{key} is {kind}");
            return v;
        }
        Need(doc, "schema", "int", "");
        if (Node.Integer(Node.Get(doc, "schema")) != 1)
            result.Add("schema is not 1");
        Need(doc, "updated", "int", "");
        var now = Need(doc, "now", "dict", "") ?? new JsonObject();
        if (Node.Str(now["state"]) is not ("idle" or "waiting" or "discovery" or "check" or "ratings" or "serve" or "download" or "test" or "publish" or "stopped"))
            result.Add($"now.state is {Node.Str(now["state"])}");
        Need(now, "why", "str", "now");
        foreach (var (key, fields) in new (string, (string, string)[])[]
                 {
                     ("map", [("id", "str"), ("name", "str"), ("mode", "str"), ("sizeMb", "int")]), ("step", [("done", "int"), ("total", "int")]),
                     ("run", [("started", "int"), ("deadline", "int"), ("kind", "str")]),
                 })
        {
            var v = Need(now, key, "dict|null", "now");
            if (v is JsonObject)
                foreach (var (f, kind) in fields)
                    Need(v, f, kind, "now." + key);
        }
        var today = Need(doc, "today", "dict", "");
        foreach (var f in new[] { "downloads", "bytes", "pass", "rejected", "failedDownloads", "apiCalls" })
            Need(today, f, "int", "today");
        var totals = Need(doc, "totals", "dict", "");
        foreach (var f in new[] { "tested", "pass", "broken", "nonmap" })
            Need(totals, f, "int", "totals");
        var listed = Need(totals, "listed", "dict", "totals");
        var queue = Need(doc, "queue", "dict", "");
        foreach (var f in new[] { "total", "bytes" })
            Need(queue, f, "int", "queue");
        var byMode = Need(queue, "byMode", "dict", "queue");
        foreach (var mode in new[] { "armsrace", "deathmatch", "wingman" })
        {
            Need(listed, mode, "int", "totals.listed");
            Need(byMode, mode, "int", "queue.byMode");
        }
        Need(queue, "eta", "int|null", "queue");
        var next = Node.Arr(Need(queue, "next", "list", "queue"));
        if (next.Count > 25)
            result.Add($"queue.next holds {next.Count}");
        foreach (var e in next)
            foreach (var (f, kind) in new[] { ("id", "str"), ("name", "str"), ("mode", "str"), ("sizeMb", "int"), ("why", "str") })
                Need(e, f, kind, "queue.next[]");
        var serve = Need(queue, "serve", "dict", "queue");
        Need(serve, "missing", "int", "queue.serve");
        Need(serve, "bytes", "int", "queue.serve");
        var keep = Need(doc, "keep", "dict", "");
        foreach (var e in Node.Arr(Need(keep, "parts", "list", "keep")))
        {
            if (Node.Str(Node.Get(e, "part")) is not ("up" or "rated" or "popular" or "subscribed" or "own" or "hud" or "queues"))
                result.Add($"keep.parts[].part is {Node.Str(Node.Get(e, "part"))}");
            foreach (var (f, kind) in new[] { ("mode", "str"), ("count", "int"), ("onDisk", "int"), ("gb", "int|float") })
                Need(e, f, kind, "keep.parts[]");
        }
        Need(keep, "totalGb", "int|float", "keep");
        var hist = Need(doc, "history", "dict", "");
        var runs = Node.Arr(Need(hist, "runs", "list", "history"));
        var maps = Node.Arr(Need(hist, "maps", "list", "history"));
        if (runs.Count > 50 || maps.Count > 200)
            result.Add($"history holds {runs.Count} runs, {maps.Count} maps");
        foreach (var e in runs)
            foreach (var f in new[] { "started", "ended", "kind", "downloads", "bytes", "pass", "rejected", "listed", "removed", "stop" })
                if (e is not JsonObject eo || !eo.ContainsKey(f))
                    result.Add($"history.runs[].{f} missing");
        foreach (var e in maps)
        {
            foreach (var (f, kind) in new[] { ("at", "int"), ("id", "str"), ("name", "str"), ("mode", "str"), ("event", "str"), ("why", "str") })
                Need(e, f, kind, "history.maps[]");
            if (Node.Str(Node.Get(e, "event")) is not ("listed" or "broken" or "removed" or "changed" or "handed-over" or "failed"))
                result.Add($"history.maps[].event is {Node.Str(Node.Get(e, "event"))}");
        }
        return result;
    }

    // ---- the status file for the in-game dashboard (schema 1): written by the run into the bridge's data, atomically
    internal static void StatusFile()
    {
        var sp = Path.Combine(RunData, "sync-status.json");
        var sdoc = Read(sp)!;
        var problems = StatusProblems(sdoc);
        Check($"status: the run wrote sync-status.json into the bridge's data, schema 1 ({T.Show(problems.Take(4))})", problems.Count == 0);
        var now = sdoc["now"]!;
        Check("status: after the run it says idle, no map, no step, no run", Node.Str(now["state"]) == "idle" && now["map"] is null && now["step"] is null && now["run"] is null
              && Node.Str(now["why"]).Contains("has ended"));
        Check("status: written atomically - no tmp file left beside it", !Directory.GetFiles(RunData).Any(f => Path.GetFileName(f).StartsWith(".sync-status", StringComparison.Ordinal)));
        var today = sdoc["today"]!;
        Check("status: today's counts - 5 downloads, 5 passes, the day's API calls", (Node.Int(today["downloads"]), Node.Int(today["pass"])) == (5, 5) && Node.Integer(today["apiCalls"]) is not null);
        var totals = sdoc["totals"]!;
        Check("status: the totals and the lists - 5 tested, 5 passed, 5 on the Wingman list, 1 on Arms Race",
              (Node.Int(totals["tested"]), Node.Int(totals["pass"]), Node.Int(totals["listed"]!["wingman"]), Node.Int(totals["listed"]!["armsrace"])) == (5, 5, 5, 1));
        var queue = sdoc["queue"]!;
        Check("status: the queue - the 3 left, in the order the downloads take them, each with why", Node.Int(queue["total"]) == 3
              && Node.Arr(queue["next"]).Select(e => Node.Str(e!["id"])).SequenceEqual(["1002", "1001", "1000"]) && Node.Arr(queue["next"]).All(e => Node.Truthy(e!["why"]))
              && Node.Int(queue["byMode"]!["wingman"]) == 3 && Node.Float(queue["eta"]) > T0);
        var run0 = sdoc["history"]!["runs"]![0]!;
        Check("status: the run's history line - manual, 5 downloads, 5 passed, 5 listed, Steam's push-back as its stop",
              Node.Str(run0["kind"]) == "manual" && (Node.Int(run0["downloads"]), Node.Int(run0["pass"]), Node.Int(run0["listed"])) == (5, 5, 5) && Node.StrOr(run0["stop"]).Contains("Rate Limit"));
        var ev = Node.Arr(sdoc["history"]!["maps"]);
        var kinds = ev.Select(e => Node.Str(e!["event"])).Distinct().OrderBy(x => x, StringComparer.Ordinal);
        Check($"status: the map events, newest first - 5 listed, the failed download ({T.Show(kinds)})",
              ev.Count(e => Node.Str(e!["event"]) == "listed") == 5 && ev.Any(e => Node.Str(e!["event"]) == "failed" && Node.Str(e["id"]) == "1002")
              && ev.Select(e => Node.Int(e!["at"])).SequenceEqual(ev.Select(e => Node.Int(e!["at"])).OrderByDescending(x => x)));
        Check("status: the day's failed downloads are counted", Node.Int(today["failedDownloads"]) == 1);
        var stObj = new Status(RunCfg, C(new FakeClock(T0)), Log.Line) { Path = Path.Combine(RunData, "tick-status.json"), TickSeconds = 0.05 };
        stObj.Set(("state", "download"), ("why", "x"));
        stObj.StartTicker();
        Thread.Sleep(400);
        stObj.StopTicker();
        var tick = Read(stObj.Path)!;
        Check("status: the ticker writes the file while a run is active, and stops", Node.Str(tick["now"]!["state"]) == "download" && !stObj.Ticking);
    }

    // ---- somebody joins during a test: the engine stops at once, nothing runs until the quiet time is over
    internal static void Join()
    {
        Loads.Clear();
        FakeSteam.Logins = 0;
        var fc2 = new FakeClock(T0 + 86400);
        var cfg2 = Cfg();
        (cfg2.Batch, cfg2.DownloadPause, cfg2.Quiet) = (3, 0, 600);
        var slept = new List<double>();
        var nj = W.Run(cfg2, fc2, true, n =>
        {
            slept.Add(n);
            fc2.T += n;
            if (slept.Count == 3)
                HumansNow[0] = 0;
        });
        SpyEngine.All.Clear();
        Hooks.TestEngine = (_, _, _, _, _) => new SpyEngine();
        Hooks.TestLoad = (_, wid, _, _, _, stop, _) =>
        {
            Loads.Add(wid);
            if (Loads.Count == 1)
                HumansNow[0] = 2;
            if (stop())
                return O($"{{\"id\": \"{wid}\", \"verdict\": \"abort\", \"reason\": \"somebody came on the game server\"}}");
            return O($"{{\"id\": \"{wid}\", \"verdict\": \"pass\", \"reason\": \"loads\", \"level\": \"lvl\", \"load_s\": 1.0, \"mode\": \"wingman\", \"bots\": 0}}");
        };
        var rep2 = nj.Run();
        Check("join: the engine went down when somebody joined", SpyEngine.All.Count > 0 && SpyEngine.All[0].Stops >= 1 && SpyEngine.All[0].Starts >= 2);
        Check("join: it waited until 10 minutes after they left, and then finished the batch", slept.Sum() >= 600 && Node.Arr(Node.Get(rep2["tests"], "rows")).Count == 3);
        Hooks.TestEngine = (_, _, _, _, _) => new FakeEngine();
        Hooks.TestLoad = (_, wid, _, _, _, stop, _) => FakeLoad(wid, stop);
    }

    static void AddItems(params (string Id, long Subs, string Title)[] items)
    {
        var st = (JsonObject)Read(StateFile)!;
        foreach (var (id, subs, title) in items)
            st["items"]![id] = Item(0, ("subs", subs), ("title", title));
        Json(StateFile, st);
    }

    // ---- a rate limit: no more downloads that day, also for a later run; one access-denied item is that item
    internal static void Stops()
    {
        AddItems(("3001", 5000, "denied"), ("3002", 4000, "fine"), ("3003", 3000, "limited"), ("3004", 2000, "never reached"));
        FakeSteam.Answers = new() { ["3001"] = "Access Denied", ["3003"] = "Rate Limit Exceeded" };
        var cfg3 = Cfg();
        (cfg3.Batch, cfg3.DownloadPause) = (10, 0);
        var rep3 = W.Run(cfg3, new FakeClock(T0 + 2 * 86400)).Run();
        var state3 = (JsonObject)Read(StateFile)!;
        Check("stop: one access-denied item is failed, the next one goes on", Node.Str(state3["items"]!["3001"]!["status"]) == "failed" && Node.Str(state3["items"]!["3002"]!["status"]) == "published");
        Check("stop: a rate limit ends the downloads, the next item is not asked for",
              Node.StrOr(Node.Get(rep3["downloads"], "stop")).Contains("Rate Limit Exceeded") && Node.Str(state3["items"]!["3004"]!["status"]) == "queued");
        var rep4 = W.Run(cfg3, new FakeClock(T0 + 2 * 86400 + 3600)).Run();
        Check("stop: a later run that day downloads nothing", Node.Int(Node.Get(rep4["downloads"], "ok")) == 0 && Node.StrOr(Node.Get(rep4["downloads"], "stop")).Contains("Rate Limit Exceeded"));
        FakeSteam.Answers = [];
    }

    // ---- a SteamCMD stall: one is routine - a fresh session takes the next map and the downloads go on, nothing to mail; a second stall
    // in a run ends its downloads, and that, or one map stalling twice in a day, is a problem for the mail
    internal static void Stalls()
    {
        HumansNow[0] = 0;
        var cfg3 = Cfg();
        (cfg3.Batch, cfg3.DownloadPause) = (10, 0);
        AddItems(("5001", 99000, "stall 5001"), ("5002", 98000, "stall 5002"), ("5003", 97000, "stall 5003"));
        (FakeSteam.Asked, FakeSteam.Answers) = ([], new() { ["5001"] = SteamCmdWords.Stall + " 15m00s" });
        var logins5 = FakeSteam.Logins;
        var rep5 = W.Run(cfg3, new FakeClock(T0 + 5 * 86400)).Run();
        var stalls5 = Node.Arr(rep5["stalls"]).Select(x => (Node.Str(x![0]), Node.Int(x[1]))).ToList();
        Check($"stall: one stall is routine - a fresh SteamCMD session, the next map at once, the downloads go on ({T.Show(FakeSteam.Asked.Take(3))}, {FakeSteam.Logins - logins5} logins)",
              FakeSteam.Asked.Take(3).SequenceEqual(["5001", "5002", "5003"]) && !Node.StrOr(Node.Get(rep5["downloads"], "stop")).Contains("stalled")
              && stalls5.SequenceEqual([("5001", 1L)]) && FakeSteam.Logins - logins5 >= 2);
        Check("stall: ... and nothing about SteamCMD for the mail", !Issues.Of(rep5).Any(i => i.Key.StartsWith("steamcmd", StringComparison.Ordinal)));
        AddItems(("6001", 99500, "stall 6001"), ("6002", 99400, "stall 6002"), ("6003", 99300, "stall 6003"));
        (FakeSteam.Asked, FakeSteam.Answers) = ([], new() { ["6001"] = SteamCmdWords.Stall + " 15m00s", ["6002"] = SteamCmdWords.Stall + " 15m00s" });
        var rep6 = W.Run(cfg3, new FakeClock(T0 + 6 * 86400)).Run();
        var keys6 = Issues.Of(rep6).Select(i => i.Key).ToList();
        Check($"stall: a second stall in a run ends its downloads (6003 not asked) and is a problem for the mail ({Node.StrOr(Node.Get(rep6["downloads"], "stop"), "None")})",
              FakeSteam.Asked.Take(2).SequenceEqual(["6001", "6002"]) && !FakeSteam.Asked.Contains("6003") && Node.StrOr(Node.Get(rep6["downloads"], "stop")).Contains("stalled twice")
              && keys6.Count(k => k == "steamcmd-stalls") == 1 && !keys6.Contains("steamcmd"));
        Check("stall: one map stalling twice in a day is a problem too; once is not",
              Issues.Of(O("{\"stalls\": [[\"7001\", 2]]}")).Select(i => i.Key).SequenceEqual(["steamcmd-stalls"]) && Issues.Of(O("{\"stalls\": [[\"7001\", 1]]}")).Count == 0);
        FakeSteam.Answers = [];
    }

    // ---- a listed map gone from the Workshop: the run's check has it, its event comes once - when the bridge's removed.json does not hold
    // that verdict yet - and verdicts that do not reach the bridge are a problem (the only check)
    internal static void Gone()
    {
        File.WriteAllText(Path.Combine(RunData, "removed.json"), "{\"checked\": \"x\", \"verdicts\": {\"11\": \"ok\"}}");
        Hooks.Check = (_, _, _, _, _) => (new Dictionary<string, JsonObject> { ["11"] = O("{\"publishedfileid\": \"11\", \"result\": 9}") }, []);
        var cfg7 = Cfg();
        (cfg7.Batch, cfg7.DownloadPause) = (1, 0);
        cfg7.Lower(O("{\"api\": true}"));
        static int GoneEvents(SyncRun run) => Node.Arr(Node.Get(run.State.Data["history"], "maps")).Count(e => Node.Str(e!["id"]) == "11" && Node.Str(e["event"]) == "removed");
        var r7 = W.Run(cfg7, new FakeClock(T0 + 7 * 86400));
        r7.Run();
        Check($"check: a listed map gone from the Workshop is in the run's check, and says so in the history ({GoneEvents(r7)})",
              Node.Eq(Node.Get(r7.Report["check"], "removed"), A("[[\"11\", \"removed\"]]")) && GoneEvents(r7) == 1);
        File.WriteAllText(Path.Combine(RunData, "removed.json"), "{\"checked\": \"x\", \"verdicts\": {\"11\": \"removed\"}}");
        var r8 = W.Run(cfg7, new FakeClock(T0 + 7 * 86400 + 3600));
        r8.Run();
        Check($"check: ... at the next check still in its check, but no second event: the bridge holds the verdict ({GoneEvents(r8)})",
              Node.Eq(Node.Get(r8.Report["check"], "removed"), A("[[\"11\", \"removed\"]]")) && GoneEvents(r8) == 1);
        Check($"issues: verdicts that do not reach the bridge are a problem for the mail - nothing else tells it ({Node.StrOr(Node.Get(r8.Report["published"], "verdicts"), "None")})",
              Issues.Of(r8.Report).Any(i => i.Key == "verdicts-not-reported"));
        Hooks.Check = (_, _, _, _, _) => ([], []);
        File.Delete(Path.Combine(RunData, "removed.json"));
    }

    /// <summary>The workshop.json the scenario below wrote, for the bridge's own reader.</summary>
    public static string WorkshopText = "";

    static JsonObject WDet(string wid, long updated, params (string Key, JsonNode Value)[] extra)
    {
        var o = O($$"""
            {"publishedfileid": "{{wid}}", "result": 1, "time_created": 100, "time_updated": {{updated}}, "hcontent_file": "9{{wid}}", "subscriptions": 5,
             "lifetime_subscriptions": 7, "favorited": 2, "lifetime_favorited": 3, "views": 11, "consumer_appid": 730, "title": "map {{wid}}", "tags": [{"tag": "Armsrace"}]}
            """);
        foreach (var (k, v) in extra)
            o[k] = v.DeepClone();
        return o;
    }

    // ---- workshop.json (the bridge's discovery stock reads it, bridge/Popularity.cs): every listed map with the Workshop's numbers as the
    // check saw them, and "passed", the time_updated of the content the sync accepts - the baseline, our own maps' newest; a passing test of
    // new content moves it up to it. The export carries the random queues as queues.json holds them
    internal static void WorkshopJson()
    {
        var wroot = T.Temp("wsj");
        var wdata = Path.Combine(wroot, "data");
        Directory.CreateDirectory(Path.Combine(wroot, "state"));
        Directory.CreateDirectory(wdata);
        Json(Path.Combine(wdata, "maps.json"), A("""[{"rank": 1, "id": 21, "name": "a", "sizeMb": 1}, {"rank": 2, "id": 22, "name": "b", "sizeMb": 1}, {"rank": 3, "id": 24, "name": "d", "sizeMb": 1}]"""));
        Json(Path.Combine(wdata, "maps-deathmatch.json"), A("""[{"rank": 1, "id": 23, "name": "c", "sizeMb": 1}, {"name": "de_dust2", "official": true}]"""));
        Json(Path.Combine(wdata, "broken.json"), O("""{"schema": "x", "maps": [], "packs": []}"""));
        Json(Path.Combine(wdata, "ours.json"), O("""{"maps": [{"id": 23}]}"""));
        var queues = O("""{"version": 1, "length": 10, "random": {"armsrace-new": ["21"], "armsrace-original": ["21"], "deathmatch": [], "wingman": []}, "requested": ["24"]}""");
        Json(Path.Combine(wdata, "queues.json"), queues);
        Json(Path.Combine(wroot, "state", "state.json"), O("""{"schema": 1, "items": {}, "baseline": {"21": 500, "22": 600, "23": 300, "24": 400}, "downloads": {}}"""));
        Hooks.Check = (_, _, _, _, _) => (new Dictionary<string, JsonObject>
        {
            ["21"] = WDet("21", 500), ["22"] = WDet("22", 900), ["23"] = WDet("23", 700, ("creator", "7")), ["24"] = WDet("24", 800, ("tags", new JsonArray())),
        }, []);
        var saved = (State, Work, Data);
        (State, Work, Data) = (Path.Combine(wroot, "state"), Path.Combine(wroot, "work"), wdata);
        var cfgw = Cfg();
        cfgw.Lower(O("{\"api\": true}"));
        (cfgw.DownloadPause, cfgw.ExcludeCreators) = (0, ["7"]);
        var fc = new FakeClock(T0 + 9 * 86400);
        W.Run(cfgw, fc).Run();
        WorkshopText = File.ReadAllText(Path.Combine(wdata, "workshop.json"));
        var wj = O(WorkshopText);
        var maps = (JsonObject)wj["maps"]!;
        Check("workshop.json: the check writes it - version 1, every listed map with the Workshop's numbers as the check saw them",
              Node.Int(wj["version"]) == 1 && Node.Integer(wj["written"]) == (long)(T0 + 9 * 86400) && maps.Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(["21", "22", "23", "24"])
              && Node.Eq(maps["21"], O("""{"created": 100, "updated": 500, "content": "921", "subs": 5, "lifetime": 7, "favs": 2, "lifetimeFavs": 3, "views": 11, "passed": 500}"""))
              && Node.IsString(maps["21"]!["content"]) && Node.Integer(maps["21"]!["passed"]) is not null);
        Check("workshop.json: passed is the version the sync accepts - a changed map still waiting for its test keeps the old one, our own map has its newest",
              (Node.Int(maps["24"]!["updated"]), Node.Int(maps["24"]!["passed"])) == (800, 400) && (Node.Int(maps["23"]!["updated"]), Node.Int(maps["23"]!["passed"])) == (700, 700));
        Check($"workshop.json: a passing test of a map's new content moves its passed up to it ({Node.Compact(maps["22"])})",
              (Node.Int(maps["22"]!["updated"]), Node.Int(maps["22"]!["passed"])) == (900, 900) && Loads.Contains("22"));
        var ex = O(new Service(cfgw, C(fc), Hooks, Log, new WebApiDoor(2, fc)).Export());
        Check("export: the random queues as queues.json holds them - its lanes and the open ballot's requests",
              Node.Eq(ex["queues"], queues) && Node.StrList(ex["queues"]!["random"]!["armsrace-new"]) is ["21"] && Node.StrList(ex["queues"]!["requested"]) is ["24"]);
        Check("workshop.json: the bridge's own reader (bridge/Popularity.cs) reads it as meant - every map, its numbers, whether its current content passed", () =>
        {
            var program = System.Reflection.Assembly.Load("os2-bridge").GetType("Program", true)!;
            var parse = program.GetMethod("ParseWorkshop", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var result = parse.Invoke(null, [WorkshopText])!;
            var read = (System.Collections.IDictionary)result.GetType().GetField("Item1")!.GetValue(result)!;
            var written = (long)result.GetType().GetField("Item2")!.GetValue(result)!;
            object? P(string id, string property) => read[id]!.GetType().GetProperty(property)!.GetValue(read[id]);
            return read.Count == 4 && written == (long)(T0 + 9 * 86400)
                   && (long)P("21", "Created")! == 100 && (long)P("21", "Updated")! == 500 && (string)P("21", "Content")! == "921" && (long)P("21", "Subs")! == 5
                   && (long)P("21", "Lifetime")! == 7 && (long)P("21", "Favs")! == 2 && (long)P("21", "LifetimeFavs")! == 3 && (long)P("21", "Views")! == 11 && (long)P("21", "Passed")! == 500
                   && (bool)P("21", "ContentPassed")! && (bool)P("22", "ContentPassed")! && (bool)P("23", "ContentPassed")! && (bool)P("24", "ContentUntested")! && !(bool)P("24", "ContentPassed")!;
        });
        (State, Work, Data) = saved;
        Hooks.Check = (_, _, _, _, _) => ([], []);
    }

    // ---- the status picture only looks: the queue it shows leaves every item's status as it was; the queue of a run's step marks them
    internal static void PictureOnly()
    {
        var root = Dir("picture-only");
        var saved = (State, Work, Data);
        (State, Work, Data) = (Path.Combine(root, "state"), Path.Combine(root, "work"), Path.Combine(root, "data"));
        Directory.CreateDirectory(Data);
        Json(Path.Combine(Data, "maps.json"), A("""[{"rank": 1, "id": 31, "name": "a", "sizeMb": 1}]"""));
        Json(Path.Combine(Data, "broken.json"), O("""{"schema": "x", "maps": [], "packs": []}"""));
        var run = W.Run(Cfg(), new FakeClock(T0));
        run.State.Items["31"] = O("""{"status": "queued", "why": "new", "ws": "ok", "app": 730, "tags": ["Armsrace"]}""");
        run.State.Items["32"] = O("""{"status": "queued", "why": "new", "ws": "ok", "app": 730, "tags": ["Armsrace"], "filename": "old.bin"}""");
        run.StatusPicture();
        var looked = (Node.Str(run.State.Item("31")!["status"]), Node.Str(run.State.Item("32")!["status"]));
        run.Queue(BridgeData.Read(Data).KnownIds(run.Cfg));
        var marked = (Node.Str(run.State.Item("31")!["status"]), Node.Str(run.State.Item("32")!["status"]));
        Check($"status: the picture only looks - a listed map and a CS:GO-era upload stay queued ({looked}), a step's queue marks them ({marked})",
              looked == ("queued", "queued") && marked == ("known", "skipped"));
        (State, Work, Data) = saved;
    }
}
