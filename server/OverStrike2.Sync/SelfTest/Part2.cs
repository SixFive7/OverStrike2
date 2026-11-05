using System.Diagnostics;
using System.Text.Json.Nodes;
using OverStrike2.Core;
using static OverStrike2.Sync.SelfTest.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>The exception the tests raise for a crashed run: its name is what the mail says.</summary>
sealed class ValueError(string message) : Exception(message);

/// <summary>An engine the tests count the instances of.</summary>
sealed class SpyEngine : FakeEngine
{
    public static readonly List<SpyEngine> All = [];
    public SpyEngine() => All.Add(this);
}

/// <summary>The second part: the bridge's two posts, a run's publish and finish on a bridge folder of its own, the test engine's
/// stop with real processes, whole runs in batches on stand-ins for SteamCMD and the test engine, the problem mail, the status file, a join
/// during the tests, Steam's push-back, the stalls, a map gone from the Workshop, and the run's clock.</summary>
static class Part2
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

    public static void Run()
    {
        var root = T.Root;
        Bridge(root);
        Engines();
        WholeRun(root);
        Issues(root);
        StatusFile();
        Join();
        Stops();
        Stalls();
        Gone();
        WorkshopJson();
        Clock();
    }

    // ---- the bridge: the verdicts report and the reload, against a stand-in bridge on localhost; a run's publish and finish end to end
    static void Bridge(string root)
    {
        using var srv = new MiniHttp();
        (BridgeUrl, BridgeSecret) = ($"http://127.0.0.1:{srv.Port}", "s3cr3t");
        var one = W.Run(Cfg(), new FakeClock(1793500000.0));
        var ans = one.ReportVerdicts([("111", "ok"), ("222", "removed"), ("333", "hidden")]);
        Check("bridge: the verdicts go to POST /removed/<secret> in the plugin's format (magic line, id TAB verdict)",
              srv.Got[0].Path == "/removed/s3cr3t" && srv.Got[0].Body == "gg-removed\t1\n111\tok\n222\tremoved\n333\thidden\n" && (srv.Got[0].ContentType ?? "").StartsWith("text/plain", StringComparison.Ordinal));
        Check("bridge: its answer is kept for the mail", ans.StartsWith("3 checked", StringComparison.Ordinal));
        Check("bridge: POST /reload/<secret> after a publish", one.Reload() == "reload: maps: 9 maps" && srv.Got[1].Path == "/reload/s3cr3t");
        var data = Data;
        Directory.CreateDirectory(data);
        const string liveMaps = "[{\"rank\": 1, \"id\": 111, \"name\": \"a\", \"sizeMb\": 5}, {\"rank\": 2, \"id\": 222, \"name\": \"b\", \"sizeMb\": 7}]";
        const string liveBroken = "{\"schema\": \"x\", \"maps\": [], \"packs\": [], \"removed\": [{\"id\": 3659287341}], \"blocked\": []}";
        File.WriteAllText(Path.Combine(data, "maps.json"), Py.DumpsFile(A(liveMaps), 1));
        File.WriteAllText(Path.Combine(data, "broken.json"), Py.DumpsFile(O(liveBroken), 2));
        File.WriteAllText(Path.Combine(data, "search.json"), Py.DumpsFile(O("{\"111\": \"a 111\", \"222\": \"b 222\"}"), 0));
        var nt = W.Run(Cfg(), new FakeClock(1793500000.0));
        nt.Ledger.Load();
        nt.State.Load();
        nt.VerdictsToReport = [("111", "ok"), ("222", "removed")];
        var passRec = O("{\"title\": \"new map\", \"tags\": [\"Armsrace\", \"Wingman\"], \"size\": 20971520, \"subs\": 3, \"lifetime\": 3, \"description\": \"d\", \"status\": \"tested\", \"verdict\": \"pass\", \"updated\": 1793400000}");
        var packRec = O("{\"title\": \"a pack\", \"tags\": [\"Armsrace\"], \"size\": 1048576, \"status\": \"tested\", \"verdict\": \"nonmap\", \"preview\": \"\", \"description\": \"\"}");
        nt.State.Data["items"] = new JsonObject { ["901"] = passRec, ["902"] = packRec };
        lock (srv.Got)
            srv.Got.Clear();
        var files0 = BridgeData.Read(nt.Cfg.BridgeData);
        Check("blocked.json: none in the bridge's folder - none blocked, and no problem", files0.Blocked.Count == 0);
        File.WriteAllText(Path.Combine(data, "blocked.json"), "[{\"id\": 3076804331, \"name\": \"x\", \"map\": \"x\", \"reason\": \"r\"}]");
        Check("blocked.json: read when it is there", BridgeData.Read(nt.Cfg.BridgeData).BlockedIds().SetEquals(["3076804331"]));
        File.WriteAllText(Path.Combine(data, "blocked.json"), "{\"blocked\": 5}");
        try
        {
            BridgeData.Read(nt.Cfg.BridgeData);
            Check("blocked.json: one that is no list stops the publish (ValueError)", false);
        }
        catch (BadDataException e)
        {
            Check("blocked.json: one that is no list stops the publish (ValueError)", e.Message.Contains("blocked.json"));
        }
        File.Delete(Path.Combine(data, "blocked.json"));
        nt.Publish([new("901", "pass", O("{\"level\": \"x\"}"), passRec), new("902", "nonmap", O("{\"kind\": \"pack\", \"detail\": \"models\"}"), packRec)],
                   [("222", "removed")], new Dictionary<string, JsonObject>());
        var after = (JsonArray)Read(Path.Combine(data, "maps.json"))!;
        Check("run: the pass is appended to the live maps.json as #3",
              Py.Eq(new JsonArray(after.Take(2).Select(e => e?.DeepClone()).ToArray()), A(liveMaps)) && Py.Int(after[2]!["id"]) == 901 && Py.Int(after[2]!["rank"]) == 3);
        var brokenNow = Read(Path.Combine(data, "broken.json"))!;
        Check("run: the pack joined broken.json's packs, the removed seed untouched",
              Py.Arr(brokenNow["packs"]).Select(p => Py.Int(p!["id"])).SequenceEqual([902L]) && Py.Eq(brokenNow["removed"], A("[{\"id\": 3659287341}]")));
        Check("run: the Wingman list is created with the pass", Py.Arr(Read(Path.Combine(data, "maps-wingman.json"))).Select(e => Py.Int(e!["id"])).SequenceEqual([901L]));
        Check("run: the verdicts went to /removed, then the files were reloaded",
              srv.Got.Select(g => g.Path).SequenceEqual(["/removed/s3cr3t", "/reload/s3cr3t"]) && srv.Got[0].Body.Contains("222\tremoved"));
        Check("run: the old files are backed up", Directory.GetDirectories(Path.Combine(nt.Cfg.StateDir, "backups")).Length == 1);
        Check("run: search.json, written after the reload (the reload does not read it), holds the new map",
              ((JsonObject)Read(Path.Combine(data, "search.json"))!).ContainsKey("901") && Py.StrList(Py.Get(nt.Report["published"], "files")).Contains("search.json"));
        Check("run: both items are marked published", Py.Str(passRec["status"]) == "published" && Py.Str(packRec["status"]) == "published");
        // a file that changed under the plan is planned again; a result that is not the live files plus additions is refused
        var nt2 = W.Run(Cfg(), new FakeClock(1793500100.0));
        nt2.Ledger.Load();
        nt2.State.Load();
        var rec3 = O("{\"title\": \"third\", \"tags\": [\"Armsrace\"], \"size\": 1048576, \"subs\": 1, \"status\": \"tested\", \"verdict\": \"pass\", \"updated\": 1}");
        var grown = (JsonArray)after.DeepClone();
        grown.Add(O("{\"rank\": 4, \"id\": 950, \"name\": \"added by the release\", \"sizeMb\": 1}"));
        File.WriteAllText(Path.Combine(data, "maps.json"), Py.DumpsFile(grown, 1));
        nt2.Publish([new("903", "pass", O("{\"level\": \"y\"}"), rec3)], [], new Dictionary<string, JsonObject>());
        var nowMaps = (JsonArray)Read(Path.Combine(data, "maps.json"))!;
        Check("run: a maps.json that grew meanwhile is merged into, never written from the older copy",
              Py.Eq(new JsonArray(nowMaps.Take(4).Select(e => e?.DeepClone()).ToArray()), grown) && Py.Int(nowMaps[4]!["id"]) == 903 && Py.Int(nowMaps[4]!["rank"]) == 5);
        nt.Finish();
        var repTxt = Directory.GetFiles(Path.Combine(nt.Cfg.StateDir, "reports"), "*.txt");
        Check("run: the report and its text are written; a run without a problem mails nothing",
              repTxt.Length > 0 && Py.Str(nt.Report["mail"]).StartsWith("NOT sent: no problem", StringComparison.Ordinal) && Py.Arr(nt.Report["issues"]).Count == 0);
        (BridgeUrl, BridgeSecret) = ("", "");
    }

    // ---- the test engine's stop, with real processes: a wrapper that dies at the SIGTERM over an engine that does not
    static void Engines()
    {
        if (!OperatingSystem.IsLinux())
        {
            foreach (var name in new[] { "engine stop: the group is up before the stop", "engine stop: a frozen engine goes with its wrapper, by SIGKILL on the group",
                                         "engine stop under a SIGTERM: the signals still go, and no frozen engine stays", "engine stop under a SIGTERM: ... and the stop goes on up",
                                         "engine start: nothing answering is a refusal (not run)", "engine start: an engine still running from the test tree is killed first",
                                         "engine start: its own attempt is gone again" })
                Linux(name, () => false);
            return;
        }
        var stopRoot = Dir("engine-stop");
        var real = new SyncClock(SystemClock.Instance, Paris);
        var te = new TestEngine(Cfg(), stopRoot, stopRoot, real, Log.Line) { QuitWait = 0, StopWait = 1 };
        var wrapper = Native.StartSession(["bash", "-c", "bash -c 'trap \"\" TERM; exec sleep 300' & wait"], stopRoot, null, false);
        te.Adopt(wrapper);
        Thread.Sleep(1000);
        var stopPgid = wrapper.Id;
        Check("engine stop: the group is up before the stop", Native.GroupAlive(stopPgid));
        te.Stop();
        Check("engine stop: a frozen engine goes with its wrapper, by SIGKILL on the group", !Native.GroupAlive(stopPgid) && !te.HasProcess);
        var going = new TestEngine(Cfg(), stopRoot, stopRoot, new SyncClock(new EndingClock(T0, T0), Paris), Log.Line);   // every wait of this clock throws at once
        var frozen = Native.StartSession(["bash", "-c", "bash -c 'trap \"\" TERM; exec sleep 300' & wait"], stopRoot, null, false);
        going.Adopt(frozen);
        Thread.Sleep(1000);
        var frozenPgid = frozen.Id;
        var wentUp = false;
        try
        {
            going.Stop();
        }
        catch (StopRequested)
        {
            wentUp = true;
        }
        for (var i = 0; i < 30 && Native.GroupAlive(frozenPgid); i++)
            Thread.Sleep(100);
        Check("engine stop under a SIGTERM: the signals still go, and no frozen engine stays", !Native.GroupAlive(frozenPgid) && !going.HasProcess);
        Check("engine stop under a SIGTERM: ... and the stop goes on up", wentUp);
        var farmBin = Path.Combine(stopRoot, "farm", "game", "bin");
        Directory.CreateDirectory(farmBin);
        File.Copy("/bin/sleep", Path.Combine(farmBin, "cs2"));
        var stale = Process.Start(Path.Combine(farmBin, "cs2"), "300");
        File.WriteAllText(Path.Combine(stopRoot, "farm", "game", "cs2.sh"), "#!/bin/bash\nsleep 300\n");
        File.SetUnixFileMode(Path.Combine(stopRoot, "farm", "game", "cs2.sh"), (UnixFileMode)0b111_101_101);
        Thread.Sleep(300);
        var (okStart, whyStart) = te.Start(2);
        Check($"engine start: nothing answering is a refusal ({whyStart})", !okStart && whyStart.Contains("did not answer"));
        Check("engine start: an engine still running from the test tree is killed first", stale.WaitForExit(5000) && stale.ExitCode == 137);
        Check("engine start: its own attempt is gone again", !te.HasProcess);
    }

    public static void FakeWorld()
    {
        Hooks.SteamCmd = (_, staging, _, _) => new FakeSteam(staging);
        Hooks.TestEngine = (_, _, _, _, _) => new FakeEngine();
        Hooks.ScanItem = _ => O("{\"verdict\": \"map\", \"maps\": [\"lvl\"], \"models\": 0, \"files\": 1, \"names\": []}");
        Hooks.BuildFarm = (_, _, _, _) => "test tree built";
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
    static void WholeRun(string root)
    {
        FakeWorld();
        RunRoot = Path.Combine(root, "run");
        RunData = Path.Combine(RunRoot, "data");
        Directory.CreateDirectory(RunData);
        (State, Work, Data) = (Path.Combine(RunRoot, "state"), Path.Combine(RunRoot, "work"), RunData);
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(RunData, "maps.json"), Py.DumpsFile(A("[{\"rank\": 1, \"id\": 11, \"name\": \"a\", \"sizeMb\": 5}]"), 1));
        File.WriteAllText(Path.Combine(RunData, "broken.json"), Py.DumpsFile(O("{\"schema\": \"x\", \"maps\": [], \"packs\": [], \"removed\": [], \"blocked\": []}"), 2));
        File.WriteAllText(Path.Combine(RunData, "search.json"), Py.DumpsFile(O("{\"11\": \"a 11\"}"), 0));
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
        FakeSteam.Answers = new() { ["1002"] = "Rate Limit Exceeded" };   // Steam pushes back at the sixth: no daily cap ends a run any more, that does
        var rep = nr.Run();
        FakeSteam.Answers = [];
        Check("run: a new upload first, then strictly by subscribers", FakeSteam.Asked.Take(5).SequenceEqual(["2000", "1006", "1005", "1004", "1003"]));
        Check("run: every download was tested", Loads.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(FakeSteam.Asked.Take(5).OrderBy(x => x, StringComparer.Ordinal)));
        Check("run: 5 downloads in batches of 2: 3 logins, one engine for each batch with work", FakeSteam.Logins == 3 && Py.Int(Py.Get(rep["downloads"], "ok")) == 5);
        Check("run: Steam's push-back ends it, with the reason", Py.StrOr(Py.Get(rep["downloads"], "stop")).Contains("Rate Limit Exceeded"));
        var wing = Py.Arr(Read(Path.Combine(RunData, "maps-wingman.json")));
        Check("run: every batch published (5 maps on the list, the report adds them up)",
              wing.Count == 5 && Py.Arr(Py.Get(Py.Get(rep["published"], "modes"), "wingman")).Count == 5 && Py.StrList(Py.Get(rep["published"], "files")).Contains("maps-wingman.json"));
        Check("run: staging is empty again", Directory.GetFileSystemEntries(Path.Combine(RunRoot, "work", "staging", "steamapps", "workshop", "content", "730")).Length == 0);
        StateAfter = (JsonObject)Read(StateFile)!;
        Check("run: the rest waits, the day's book says 5", Py.Int(Py.Get(Py.Get(StateAfter["downloads"], "2026-11-01"), "items")) == 5
              && Py.Obj(StateAfter["items"]).Count(kv => Py.Str(Py.Get(kv.Value, "status")) is "queued" or "retry") == 3);
        Check("run: the mail's estimate counts what is left", Py.Int(Py.Get(rep["backlog"], "items")) == 3
              && File.ReadAllText(Py.Slice(Py.Str(rep["report_path"]), 0, -5) + ".txt").Contains("Backlog: 3 item(s)"));
        Check("run: the state remembers the run, and when the API steps last ran", Py.Float(Py.Get(StateAfter["last_run"], "ended")) > 0 && Py.Float(StateAfter["last_api"]) == T0);
        Check("run: the estimate counts on the day without the restarts' stretches", Py.Float(Py.Get(rep["backlog"], "hours")) == 22);
        Check("run: Steam's push-back is a problem the mail names (not sent here: no SMTP host)",
              Py.StrList(rep["issues"]).Any(i => i.Contains("SteamCMD pushed back")) && Py.Str(rep["mail"]).StartsWith("NOT sent: no SMTP host", StringComparison.Ordinal));
    }

    public static readonly JsonObject Quiet = O("""
        {"day": "2026-11-03", "started_hm": "12:00", "ended_hm": "15:00", "downloads": {"stop": "the run's deadline (time is kept for this batch's tests)"},
         "tests": {"stop": "somebody was on the server until the deadline"}, "serve": {"stop": "the run's deadline"}, "errors": []}
        """);

    public static JsonObject QuietWith(params (string Key, JsonNode? Value)[] fields)
    {
        var c = (JsonObject)Quiet.DeepClone();
        foreach (var (k, v) in fields)
            c[k] = v?.DeepClone();
        return c;
    }

    // ---- the issues-only mail: what counts, what does not, the words, once a day per problem
    static void Issues(string root)
    {
        Check("issues: a run that ended at its deadline, or waited for players, is no problem", Mail.IssuesOf(Quiet).Count == 0);
        var cases = new (string Extra, string Key)[]
        {
            ("""{"api_stop": "Steam's Web API answered HTTP 429 to QueryFiles - every API step stops for the rest of the day"}""", "steam-webapi"),
            ("""{"downloads": {"stop": "SteamCMD answered 'Rate Limit Exceeded' - no more downloads today"}}""", "steamcmd-pushback"),
            ("""{"serve": {"stop": "SteamCMD answered 'Access Denied' twice in a row - no more downloads today"}}""", "steamcmd-pushback"),
            ("""{"downloads": {"stop": "3 downloads in a row failed - no more in this run"}}""", "downloads-in-a-row"),
            ("""{"downloads": {"stop": "SteamCMD did not log in anonymously: no answer"}}""", "steamcmd"),
            ("""{"tests": {"stop": "test engine did not answer in 90 s", "engine_failed": "test engine did not answer in 90 s"}}""", "test-engine"),
            ("""{"tests": {"stop": "the test tree could not be built: disk full"}}""", "tests-stopped"),
            ("""{"discovery": {"skipped": "failed: HTTP 500"}}""", "discovery-failed"),
            ("""{"check": {"suspicious": true}}""", "check-not-believed"),
            ("""{"serve": {"handover_failed": [["305", "its folder is empty"]]}}""", "handover-failed"),
            ("""{"serve": {"stop": "205.0 GB of hand-overs wait for the server already"}}""", "handovers-waiting"),
            ("""{"disk": {"low": "80.0 GB free on the game's volume (the mark is 100.0 GB)"}}""", "disk-low"),
            ("""{"watcher_keep": {"computed": "the next morning", "problems": ["ratings.json is 80 h old (at most 72 h)"]}}""", "deletion-refused:"),
            ("""{"errors": ["the bridge's data cannot be read (x) - nothing done in this run"]}""", "error:"),
        };
        foreach (var (extra, key) in cases)
        {
            var got = Mail.IssuesOf(QuietWith(O(extra).Select(kv => (kv.Key, kv.Value)).ToArray()));
            Check($"issues: {key} counts ({(got.Count > 0 ? Py.Cut(got[0].What, 70) : "nothing")})", got.Count == 1 && got[0].Key.StartsWith(key, StringComparison.Ordinal) && got[0].Did.Length > 0);
        }
        var sentMails = new List<(string Subject, string Body)>();
        var realSend = Hooks.SendMail;
        Hooks.SendMail = (_, subject, body) =>
        {
            sentMails.Add((subject, body));
            return null;
        };
        var icfg = Cfg();
        icfg.StateDir = Path.Combine(root, "issues-state");
        Directory.CreateDirectory(icfg.StateDir);
        File.WriteAllText(Path.Combine(icfg.StateDir, "dashboard-url"), "https://dash.example/\n");
        // the dashboard's address is a settings key now (sync.mail.dashboardUrl): a file in the state folder means nothing
        var withFile = new JsonObject { ["schema"] = 1, ["paths"] = new JsonObject { ["syncState"] = "/" + icfg.StateDir.Replace('\\', '/').TrimStart('/') } }.ToJsonString();
        Check("issues: the dashboard's address is SYNC_DASHBOARD_URL alone - a <state>/dashboard-url file means nothing",
              SyncConfig.From(Settings.Parse(withFile, Part.Server, _ => null, _ => throw new FileNotFoundException()), _ => null).DashboardUrl == "");
        var withKey = """{ "schema": 1, "sync": { "mail": { "dashboardUrl": "https://dash.example/" } } }""";
        Check("issues: ... and set there, it is the one the mails point to",
              SyncConfig.From(Settings.Parse(withKey, Part.Server, _ => null, _ => throw new FileNotFoundException()), _ => null).DashboardUrl == "https://dash.example/");
        icfg.DashboardUrl = "https://dash.example/";
        var ic = new FakeClock(T0);
        var probs = Mail.IssuesOf(QuietWith(("api_stop", "Steam's Web API answered HTTP 429 to QueryFiles"), ("report_path", "/x/r.json")));
        var out1 = Mail.MailIssues(icfg, C(ic), probs, QuietWith(("report_path", "/x/r.json")), Hooks.SendMail, Log.Line);
        Check("issues: a problem is mailed - what went wrong, what the sync did, the dashboard, the report",
              out1.StartsWith("sent", StringComparison.Ordinal) && sentMails.Count == 1 && sentMails[0].Body.Contains("What went wrong: Steam's Web API pushed back")
              && sentMails[0].Body.Contains("What the sync did: Every Web API step stopped") && sentMails[0].Body.Contains("The dashboard: https://dash.example/")
              && sentMails[0].Body.Contains("/x/r.txt") && sentMails[0].Subject.Contains("pushed back"));
        ic.T += 3600;
        var out2 = Mail.MailIssues(icfg, C(ic), probs, Quiet, Hooks.SendMail, Log.Line);
        Check("issues: the same problem an hour later is not mailed again", out2.StartsWith("NOT sent: every problem", StringComparison.Ordinal) && sentMails.Count == 1);
        var probs2 = probs.Concat(Mail.IssuesOf(QuietWith(("disk", O("{\"low\": \"50 GB free\"}"))))).ToList();
        var out3 = Mail.MailIssues(icfg, C(ic), probs2, Quiet, Hooks.SendMail, Log.Line);
        Check("issues: a new problem is mailed, the ongoing one named as still going on", out3.StartsWith("sent", StringComparison.Ordinal) && sentMails.Count == 2
              && sentMails[1].Body.Contains("disk is running low") && sentMails[1].Body.Contains("Still going on, mailed already in the last day: Steam's Web API"));
        ic.T += 21 * 3600;
        Mail.MailIssues(icfg, C(ic), probs, Quiet, Hooks.SendMail, Log.Line);
        Check("issues: a problem that persists is mailed again once a day", sentMails.Count == 3);
        Check("issues: no problem, no mail", Mail.MailIssues(icfg, C(ic), [], Quiet, Hooks.SendMail, Log.Line).StartsWith("NOT sent: no problem", StringComparison.Ordinal) && sentMails.Count == 3);
        var crash = Mail.MailCrash(icfg, C(ic), new ValueError("boom"), false, Hooks.SendMail, Log.Line);
        Check("issues: a run that crashed is mailed from the service", crash.StartsWith("sent", StringComparison.Ordinal) && sentMails[^1].Body.Contains("The run failed: ValueError('boom')"));
        Check("issues: ...once a day too", Mail.MailCrash(icfg, C(ic), new ValueError("boom 2"), false, Hooks.SendMail, Log.Line).StartsWith("NOT sent: every problem", StringComparison.Ordinal));
        Hooks.SendMail = realSend;
    }

    /// <summary>What in `doc` breaks schema 1 (the dashboard's contract: fields may be added, never renamed or dropped).</summary>
    public static List<string> StatusProblems(JsonNode? doc)
    {
        var out_ = new List<string>();
        JsonNode? Need(JsonNode? obj, string key, string kinds, string where)
        {
            if (obj is not JsonObject o || !o.ContainsKey(key))
            {
                out_.Add($"{where}.{key} missing");
                return null;
            }
            var v = o[key];
            var kind = v switch
            {
                null => "null", JsonObject => "dict", JsonArray => "list",
                _ when Py.IsString(v) => "str", _ when Py.IsBool(v) => "bool", _ when Py.Integer(v) is not null => "int", _ when Py.IsNumber(v) => "float", _ => "?",
            };
            if (!kinds.Split('|').Contains(kind))
                out_.Add($"{where}.{key} is {kind}");
            return v;
        }
        Need(doc, "schema", "int", "");
        if (Py.Integer(Py.Get(doc, "schema")) != 1)
            out_.Add("schema is not 1");
        Need(doc, "updated", "int", "");
        var now = Need(doc, "now", "dict", "") ?? new JsonObject();
        if (Py.Str(now["state"]) is not ("idle" or "waiting" or "discovery" or "check" or "ratings" or "serve" or "download" or "test" or "publish" or "stopped"))
            out_.Add($"now.state is {Py.Str(now["state"])}");
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
        var next = Py.Arr(Need(queue, "next", "list", "queue"));
        if (next.Count > 25)
            out_.Add($"queue.next holds {next.Count}");
        foreach (var e in next)
            foreach (var (f, kind) in new[] { ("id", "str"), ("name", "str"), ("mode", "str"), ("sizeMb", "int"), ("why", "str") })
                Need(e, f, kind, "queue.next[]");
        var serve = Need(queue, "serve", "dict", "queue");
        Need(serve, "missing", "int", "queue.serve");
        Need(serve, "bytes", "int", "queue.serve");
        var keep = Need(doc, "keep", "dict", "");
        foreach (var e in Py.Arr(Need(keep, "parts", "list", "keep")))
        {
            if (Py.Str(Py.Get(e, "part")) is not ("up" or "rated" or "popular" or "subscribed" or "own" or "hud" or "queues"))
                out_.Add($"keep.parts[].part is {Py.Str(Py.Get(e, "part"))}");
            foreach (var (f, kind) in new[] { ("mode", "str"), ("count", "int"), ("onDisk", "int"), ("gb", "int|float") })
                Need(e, f, kind, "keep.parts[]");
        }
        Need(keep, "totalGb", "int|float", "keep");
        var hist = Need(doc, "history", "dict", "");
        var runs = Py.Arr(Need(hist, "runs", "list", "history"));
        var maps = Py.Arr(Need(hist, "maps", "list", "history"));
        if (runs.Count > 50 || maps.Count > 200)
            out_.Add($"history holds {runs.Count} runs, {maps.Count} maps");
        foreach (var e in runs)
            foreach (var f in new[] { "started", "ended", "kind", "downloads", "bytes", "pass", "rejected", "listed", "removed", "stop" })
                if (!Py.Has(e, f))
                    out_.Add($"history.runs[].{f} missing");
        foreach (var e in maps)
        {
            foreach (var (f, kind) in new[] { ("at", "int"), ("id", "str"), ("name", "str"), ("mode", "str"), ("event", "str"), ("why", "str") })
                Need(e, f, kind, "history.maps[]");
            if (Py.Str(Py.Get(e, "event")) is not ("listed" or "broken" or "removed" or "changed" or "handed-over" or "failed"))
                out_.Add($"history.maps[].event is {Py.Str(Py.Get(e, "event"))}");
        }
        return out_;
    }

    // ---- the status file for the in-game dashboard (schema 1): written by the run into the bridge's data, atomically
    static void StatusFile()
    {
        var sp = Path.Combine(RunData, "sync-status.json");
        var sdoc = Read(sp)!;
        var problems = StatusProblems(sdoc);
        Check($"status: the run wrote sync-status.json into the bridge's data, schema 1 ({T.Show(problems.Take(4))})", problems.Count == 0);
        var now = sdoc["now"]!;
        Check("status: after the run it says idle, no map, no step, no run", Py.Str(now["state"]) == "idle" && now["map"] is null && now["step"] is null && now["run"] is null
              && Py.Str(now["why"]).Contains("has ended"));
        Check("status: written atomically - no tmp file left beside it", !Directory.GetFiles(RunData).Any(f => Path.GetFileName(f).StartsWith(".sync-status", StringComparison.Ordinal)));
        var today = sdoc["today"]!;
        Check("status: today's counts - 5 downloads, 5 passes, the day's API calls", (Py.Int(today["downloads"]), Py.Int(today["pass"])) == (5, 5) && Py.Integer(today["apiCalls"]) is not null);
        var totals = sdoc["totals"]!;
        Check("status: the totals and the lists - 5 tested, 5 passed, 5 on the Wingman list, 1 on Arms Race",
              (Py.Int(totals["tested"]), Py.Int(totals["pass"]), Py.Int(totals["listed"]!["wingman"]), Py.Int(totals["listed"]!["armsrace"])) == (5, 5, 5, 1));
        var queue = sdoc["queue"]!;
        Check("status: the queue - the 3 left, in the order the downloads take them, each with why", Py.Int(queue["total"]) == 3
              && Py.Arr(queue["next"]).Select(e => Py.Str(e!["id"])).SequenceEqual(["1002", "1001", "1000"]) && Py.Arr(queue["next"]).All(e => Py.Truthy(e!["why"]))
              && Py.Int(queue["byMode"]!["wingman"]) == 3 && Py.Float(queue["eta"]) > T0);
        var run0 = sdoc["history"]!["runs"]![0]!;
        Check("status: the run's history line - manual, 5 downloads, 5 passed, 5 listed, Steam's push-back as its stop",
              Py.Str(run0["kind"]) == "manual" && (Py.Int(run0["downloads"]), Py.Int(run0["pass"]), Py.Int(run0["listed"])) == (5, 5, 5) && Py.StrOr(run0["stop"]).Contains("Rate Limit"));
        var ev = Py.Arr(sdoc["history"]!["maps"]);
        var kinds = ev.Select(e => Py.Str(e!["event"])).Distinct().OrderBy(x => x, StringComparer.Ordinal);
        Check($"status: the map events, newest first - 5 listed, the failed download ({T.Show(kinds)})",
              ev.Count(e => Py.Str(e!["event"]) == "listed") == 5 && ev.Any(e => Py.Str(e!["event"]) == "failed" && Py.Str(e["id"]) == "1002")
              && ev.Select(e => Py.Int(e!["at"])).SequenceEqual(ev.Select(e => Py.Int(e!["at"])).OrderByDescending(x => x)));
        Check("status: the day's failed downloads are counted", Py.Int(today["failedDownloads"]) == 1);
        var stObj = new Status(RunCfg, C(new FakeClock(T0)), Log.Line) { Path = Path.Combine(RunData, "tick-status.json"), TickSeconds = 0.05 };
        stObj.Set(("state", "download"), ("why", "x"));
        stObj.StartTicker();
        Thread.Sleep(400);
        stObj.StopTicker();
        var tick = Read(stObj.Path)!;
        Check("status: the ticker writes the file while a run is active, and stops", Py.Str(tick["now"]!["state"]) == "download" && !stObj.Ticking);
    }

    // ---- somebody joins during a test: the engine stops at once, nothing runs until the quiet time is over
    static void Join()
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
        Check("join: it waited until 10 minutes after they left, and then finished the batch", slept.Sum() >= 600 && Py.Arr(Py.Get(rep2["tests"], "rows")).Count == 3);
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
    static void Stops()
    {
        AddItems(("3001", 5000, "denied"), ("3002", 4000, "fine"), ("3003", 3000, "limited"), ("3004", 2000, "never reached"));
        FakeSteam.Answers = new() { ["3001"] = "Access Denied", ["3003"] = "Rate Limit Exceeded" };
        var cfg3 = Cfg();
        (cfg3.Batch, cfg3.DownloadPause) = (10, 0);
        var rep3 = W.Run(cfg3, new FakeClock(T0 + 2 * 86400)).Run();
        var state3 = (JsonObject)Read(StateFile)!;
        Check("stop: one access-denied item is failed, the next one goes on", Py.Str(state3["items"]!["3001"]!["status"]) == "failed" && Py.Str(state3["items"]!["3002"]!["status"]) == "published");
        Check("stop: a rate limit ends the downloads, the next item is not asked for",
              Py.StrOr(Py.Get(rep3["downloads"], "stop")).Contains("Rate Limit Exceeded") && Py.Str(state3["items"]!["3004"]!["status"]) == "queued");
        var rep4 = W.Run(cfg3, new FakeClock(T0 + 2 * 86400 + 3600)).Run();
        Check("stop: a later run that day downloads nothing", Py.Int(Py.Get(rep4["downloads"], "ok")) == 0 && Py.StrOr(Py.Get(rep4["downloads"], "stop")).Contains("Rate Limit Exceeded"));
        FakeSteam.Answers = [];
    }

    // ---- a SteamCMD stall: one is routine - a fresh session takes the next map and the downloads go on, nothing to mail; a second stall
    // in a run ends its downloads, and that, or one map stalling twice in a day, is a problem for the mail
    static void Stalls()
    {
        HumansNow[0] = 0;
        var cfg3 = Cfg();
        (cfg3.Batch, cfg3.DownloadPause) = (10, 0);
        AddItems(("5001", 99000, "stall 5001"), ("5002", 98000, "stall 5002"), ("5003", 97000, "stall 5003"));
        (FakeSteam.Asked, FakeSteam.Answers) = ([], new() { ["5001"] = SteamCmdWords.Stall + " 15m00s" });
        var logins5 = FakeSteam.Logins;
        var rep5 = W.Run(cfg3, new FakeClock(T0 + 5 * 86400)).Run();
        var stalls5 = Py.Arr(rep5["stalls"]).Select(x => (Py.Str(x![0]), Py.Int(x[1]))).ToList();
        Check($"stall: one stall is routine - a fresh SteamCMD session, the next map at once, the downloads go on ({T.Show(FakeSteam.Asked.Take(3))}, {FakeSteam.Logins - logins5} logins)",
              FakeSteam.Asked.Take(3).SequenceEqual(["5001", "5002", "5003"]) && !Py.StrOr(Py.Get(rep5["downloads"], "stop")).Contains("stalled")
              && stalls5.SequenceEqual([("5001", 1L)]) && FakeSteam.Logins - logins5 >= 2);
        Check("stall: ... and nothing about SteamCMD for the mail", !Mail.IssuesOf(rep5).Any(i => i.Key.StartsWith("steamcmd", StringComparison.Ordinal)));
        AddItems(("6001", 99500, "stall 6001"), ("6002", 99400, "stall 6002"), ("6003", 99300, "stall 6003"));
        (FakeSteam.Asked, FakeSteam.Answers) = ([], new() { ["6001"] = SteamCmdWords.Stall + " 15m00s", ["6002"] = SteamCmdWords.Stall + " 15m00s" });
        var rep6 = W.Run(cfg3, new FakeClock(T0 + 6 * 86400)).Run();
        var keys6 = Mail.IssuesOf(rep6).Select(i => i.Key).ToList();
        Check($"stall: a second stall in a run ends its downloads (6003 not asked) and is a problem for the mail ({Py.StrOr(Py.Get(rep6["downloads"], "stop"), "None")})",
              FakeSteam.Asked.Take(2).SequenceEqual(["6001", "6002"]) && !FakeSteam.Asked.Contains("6003") && Py.StrOr(Py.Get(rep6["downloads"], "stop")).Contains("stalled twice")
              && keys6.Count(k => k == "steamcmd-stalls") == 1 && !keys6.Contains("steamcmd"));
        Check("stall: one map stalling twice in a day is a problem too; once is not",
              Mail.IssuesOf(O("{\"stalls\": [[\"7001\", 2]]}")).Select(i => i.Key).SequenceEqual(["steamcmd-stalls"]) && Mail.IssuesOf(O("{\"stalls\": [[\"7001\", 1]]}")).Count == 0);
        FakeSteam.Answers = [];
    }

    // ---- a listed map gone from the Workshop: the run's check has it, its event comes once - when the bridge's removed.json does not hold
    // that verdict yet - and verdicts that do not reach the bridge are a problem (the only check)
    static void Gone()
    {
        File.WriteAllText(Path.Combine(RunData, "removed.json"), "{\"checked\": \"x\", \"verdicts\": {\"11\": \"ok\"}}");
        Hooks.Check = (_, _, _, _, _) => (new Dictionary<string, JsonObject> { ["11"] = O("{\"publishedfileid\": \"11\", \"result\": 9}") }, []);
        var cfg7 = Cfg();
        (cfg7.Batch, cfg7.DownloadPause) = (1, 0);
        cfg7.Lower(O("{\"api\": true}"));
        static int GoneEvents(SyncRun run) => Py.Arr(Py.Get(run.State.Data["history"], "maps")).Count(e => Py.Str(e!["id"]) == "11" && Py.Str(e["event"]) == "removed");
        var r7 = W.Run(cfg7, new FakeClock(T0 + 7 * 86400));
        r7.Run();
        Check($"check: a listed map gone from the Workshop is in the run's check, and says so in the history ({GoneEvents(r7)})",
              Py.Eq(Py.Get(r7.Report["check"], "removed"), A("[[\"11\", \"removed\"]]")) && GoneEvents(r7) == 1);
        File.WriteAllText(Path.Combine(RunData, "removed.json"), "{\"checked\": \"x\", \"verdicts\": {\"11\": \"removed\"}}");
        var r8 = W.Run(cfg7, new FakeClock(T0 + 7 * 86400 + 3600));
        r8.Run();
        Check($"check: ... at the next check still in its check, but no second event: the bridge holds the verdict ({GoneEvents(r8)})",
              Py.Eq(Py.Get(r8.Report["check"], "removed"), A("[[\"11\", \"removed\"]]")) && GoneEvents(r8) == 1);
        Check($"issues: verdicts that do not reach the bridge are a problem for the mail - nothing else tells it ({Py.StrOr(Py.Get(r8.Report["published"], "verdicts"), "None")})",
              Mail.IssuesOf(r8.Report).Any(i => i.Key == "verdicts-not-reported"));
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
    static void WorkshopJson()
    {
        var wroot = Path.Combine(T.Root, "wsj");
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
              Py.Int(wj["version"]) == 1 && Py.Integer(wj["written"]) == (long)(T0 + 9 * 86400) && maps.Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(["21", "22", "23", "24"])
              && Py.Eq(maps["21"], O("""{"created": 100, "updated": 500, "content": "921", "subs": 5, "lifetime": 7, "favs": 2, "lifetimeFavs": 3, "views": 11, "passed": 500}"""))
              && Py.IsString(maps["21"]!["content"]) && Py.Integer(maps["21"]!["passed"]) is not null);
        Check("workshop.json: passed is the version the sync accepts - a changed map still waiting for its test keeps the old one, our own map has its newest",
              (Py.Int(maps["24"]!["updated"]), Py.Int(maps["24"]!["passed"])) == (800, 400) && (Py.Int(maps["23"]!["updated"]), Py.Int(maps["23"]!["passed"])) == (700, 700));
        Check($"workshop.json: a passing test of a map's new content moves its passed up to it ({Py.Dumps(maps["22"])})",
              (Py.Int(maps["22"]!["updated"]), Py.Int(maps["22"]!["passed"])) == (900, 900) && Loads.Contains("22"));
        var ex = O(new Service(cfgw, C(fc), Hooks, Log, new WebApiDoor(2, fc)).Export());
        Check("export: the random queues as queues.json holds them - its lanes and the open ballot's requests",
              Py.Eq(ex["queues"], queues) && Py.StrList(ex["queues"]!["random"]!["armsrace-new"]) is ["21"] && Py.StrList(ex["queues"]!["requested"]) is ["24"]);
        Check("workshop.json: the bridge's own reader (bridge/Popularity.cs) reads it as meant - every map, its numbers, whether its current content passed", () =>
        {
            var program = System.Reflection.Assembly.Load("GunGameBridge").GetType("Program", true)!;
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

    // ---- no time windows: a run lasts sync.runHours, cut short before the restarts; the service starts one whenever nobody plays and work waits
    static void Clock()
    {
        var fcd = new FakeClock(T0 + 3 * 86400);
        var c = C(fcd);
        var day0 = c.AtTime(new TimeOfDay(0, 0));
        string Hm(double ts) => c.Hm(ts);
        fcd.T = day0 + 12 * 3600;
        Check("run: at noon a run lasts 3 h", Hm(W.Run(Cfg(), fcd).Deadline) == "15:00");
        fcd.T = day0 + 5 * 3600;
        Check("run: at 05:00 it ends at 06:45, before the 07:00 restart", Hm(W.Run(Cfg(), fcd).Deadline) == "06:45");
        fcd.T = day0 + 2 * 3600;
        Check("run: at 02:00 it runs its 3 h - no prefetch to keep clear of", Hm(W.Run(Cfg(), fcd).Deadline) == "05:00");
        var cfgs = Cfg();
        Check("service: no start in the daily restart's stretch (06:45-07:10)", Service.StartBlocked(cfgs, c, day0 + 6.9 * 3600) is not null && Service.StartBlocked(cfgs, c, day0 + 7.2 * 3600) is null);
        Check("service: the night is the sync's - a run may start at 03:10 and at 03:30, nothing waits for a prefetch",
              Service.StartBlocked(cfgs, c, day0 + (3 * 60 + 10) * 60) is null && Service.StartBlocked(cfgs, c, day0 + 3.5 * 3600) is null
              && typeof(Service).GetMethods().All(m => !m.Name.Contains("Prefetch", StringComparison.Ordinal)) && typeof(SyncRun).GetMethods().All(m => !m.Name.Contains("Prefetch", StringComparison.Ordinal)));
        var at0630 = Service.StartBlocked(cfgs, c, day0 + 6.5 * 3600);
        Check($"service: no run with under 20 minutes before the daily restart's cut: at 06:30 it would end at 06:45 ({at0630 ?? "None"}) - at 06:20 one starts",
              (at0630 ?? "").Contains("daily restart") && Service.StartBlocked(cfgs, c, day0 + (6 * 60 + 20) * 60) is null);
        fcd.T = day0 + 12 * 3600;
        Check("service: work waits while maps are queued, unless Steam stopped today's downloads; or when the API steps are due",
              Service.WorkWaits(cfgs, c, new JsonObject { ["items"] = O("{\"1\": {\"status\": \"queued\"}}"), ["last_api"] = fcd.T })
              && !Service.WorkWaits(cfgs, c, new JsonObject { ["items"] = O("{\"1\": {\"status\": \"queued\"}}"), ["last_api"] = fcd.T, ["downloads"] = new JsonObject { [c.Day()] = O("{\"stop\": \"x\"}") } })
              && Service.WorkWaits(cfgs, c, new JsonObject { ["items"] = new JsonObject(), ["last_api"] = fcd.T - 5 * 3600 })
              && !Service.WorkWaits(cfgs, c, new JsonObject { ["items"] = new JsonObject(), ["last_api"] = fcd.T }));
    }
}
