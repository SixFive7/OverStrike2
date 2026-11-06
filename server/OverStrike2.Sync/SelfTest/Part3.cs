using System.Reflection;
using System.Text.Json.Nodes;
using OverStrike2.Core;
using MapsFolder = OverStrike2.Server.MapsFolder;
using static OverStrike2.Sync.SelfTest.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>SteamCMD that writes an acf naming the item it just downloaded (alone), and once it quits every item of its session still staged, as
/// the real one's acf is complete by then. A hand-over that could not read the acf at once waits for that quit; on Windows a read can be refused
/// for a moment when something else has the new file open, and on this clock the 20 s the run waits for the entry take no time at all.</summary>
sealed class AcfSteam(string installDir) : FakeSteam(installDir)
{
    readonly List<string> session = [];

    string Acf => Path.Combine(InstallDir, "steamapps", "workshop", "appworkshop_730.acf");

    void WriteAcf(IEnumerable<string> wids) =>
        File.WriteAllText(Acf, "\"AppWorkshop\"\n{\n\t\"WorkshopItemsInstalled\"\n\t{\n"
                               + string.Concat(wids.Select(w => $"\t\t\"{w}\"\n\t\t{{\n\t\t\t\"size\"\t\t\"1\"\n\t\t\t\"timeupdated\"\t\t\"9\"\n\t\t\t\"manifest\"\t\t\"4{w}\"\n\t\t}}\n")) + "\t}\n}\n");

    public override (bool Ok, string Reason, long Bytes) Download(string wid)
    {
        var ok = base.Download(wid);
        session.Add(wid);
        WriteAcf([wid]);
        return ok;
    }

    public override void Stop()
    {
        var staged = session.Where(w => Directory.Exists(Path.Combine(InstallDir, "steamapps", "workshop", "content", "730", w))).ToList();
        if (staged.Count > 0)
            WriteAcf(staged);
        session.Clear();
    }
}

/// <summary>SteamCMD at its worst: its acf names the downloads only once it quits.</summary>
sealed class LateAcfSteam(string installDir) : FakeSteam(installDir)
{
    public static readonly List<string> Got = [];
    public static Action<Dictionary<string, string>> WriteAcf = _ => { };

    public override (bool Ok, string Reason, long Bytes) Download(string wid)
    {
        Got.Add(wid);
        return base.Download(wid);
    }

    public override void Stop()
    {
        if (Got.Count > 0)
            WriteAcf(Got.ToDictionary(w => w, _ => "200"));
    }
}

/// <summary>A test-engine stand-in that is gone the moment it is asked: the real test load runs its first steps and says crash.</summary>
sealed class DownEngine : ITestEngine
{
    public static readonly List<string> Sent = [];
    sealed class NoLines : ILines
    {
        public List<string> Lines() => [];
    }
    public int Starts { get; set; }
    public ILines? Tail { get; } = new NoLines();
    public bool Alive() => false;
    public (bool Ok, string Why) Start() => (false, "no");
    public void Stop()
    {
    }
    public string? Rcon(string command)
    {
        Sent.Add(command);
        return "";
    }
    public EngineInfo? A2s() => null;
}

/// <summary>A ratings API stand-in: scores for some ids, Most Popular orders per tag, a stop after some calls.</summary>
sealed class VoteApi(Dictionary<string, double> scores, int? stopAfter = null, Dictionary<string, List<string>>? trends = null) : IWorkshopApi
{
    public Dictionary<string, int> ByApi { get; } = [];
    public readonly List<string> Asked = [];
    public readonly List<(string Tag, int Days)> Tags = [];

    public Dictionary<string, JsonArray> Votes(IReadOnlyList<string> ids)
    {
        ByApi["GetDetails"] = ByApi.GetValueOrDefault("GetDetails") + 1;
        if (stopAfter is { } n && ByApi["GetDetails"] > n)
            throw new StopApiException("HTTP 429");
        Asked.AddRange(ids);
        return ids.Where(scores.ContainsKey).ToDictionary(w => w, w => new JsonArray(scores[w], 1, 0, 5));
    }

    public (List<string> Ids, string NextCursor, long Total) Trend(string tag, string cursor, int days, int perPage = 100)
    {
        Tags.Add((tag, days));
        var list = trends?.GetValueOrDefault(tag) ?? [];
        return ([.. list], "", list.Count);
    }

    public (List<JsonObject> Items, string NextCursor, long Total) QueryFiles(IReadOnlyList<string> tags, string cursor, int perPage) => throw new NotSupportedException();
    public Dictionary<string, JsonObject> Details(IReadOnlyList<string> ids) => throw new NotSupportedException();
}

/// <summary>The third part: acf editing, the keep set (Core's, the restart watcher's too), the scores, the 223, the ratings, the
/// queue, the hand-over and the watcher reading it, serve, the clean-up, the tidy step, one definition per name, the real test load, and
/// reshaped inputs.</summary>
static class Part3
{
    const double KT = 1793700000.0;
    static string srv = "", sdata = "", sstate = "", swork = "", sgame = "", gws = "", stg = "", hdir = "";
    static List<string> missing = [];

    public static void Run()
    {
        var root = T.Root;
        Acf(root);
        var kdata = KData();
        KeepSetChecks(kdata);
        Votes(root);
        RunSteps(root, kdata);
        HandOver();
        Serve();
        Staged();
        Tidy();
        Definitions();
        Reshaped(root);
    }

    static void Acf(string root)
    {
        var acf = Path.Combine(root, "appworkshop_730.acf");
        File.WriteAllText(acf, "\"AppWorkshop\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"WorkshopItemsInstalled\"\n\t{\n\t\t\"1\"\n\t\t{\n\t\t\t\"timeupdated\"\t\t\"5\"\n\t\t}\n"
                               + "\t\t\"2\"\n\t\t{\n\t\t\t\"timeupdated\"\t\t\"6\"\n\t\t}\n\t}\n\t\"WorkshopItemDetails\"\n\t{\n\t\t\"1\"\n\t\t{\n\t\t\t\"manifest\"\t\t\"9\"\n\t\t}\n\t}\n}\n");
        var items = Handover.AcfItems(acf);
        Check("acf: items read", items.Count == 2 && items["1"] == 5 && items["2"] == 6);
        Handover.AcfForget(acf, ["1"]);
        Check("acf: an item is forgotten in both sections, the rest kept", Handover.AcfItems(acf) is { Count: 1 } left && left["2"] == 6 && !File.ReadAllText(acf).Contains("\"1\""));
    }

    public static JsonObject KData()
    {
        var maps = new JsonArray(Enumerable.Range(1, 129).Select(r => (JsonNode?)new JsonObject { ["rank"] = r, ["id"] = 100 + r, ["name"] = $"a{r}", ["sizeMb"] = 1 }).ToArray());
        var dm = new JsonArray(Enumerable.Range(1, 29).Select(r => (JsonNode?)new JsonObject { ["rank"] = r, ["id"] = 300 + r, ["name"] = $"d{r}", ["sizeMb"] = 1 }).ToArray());
        var scores = new JsonObject();
        foreach (var r in Enumerable.Range(1, 129))
            scores[(100 + r).ToString()] = new JsonArray(r / 200.0, 1, 0, 1000 - r);
        return new JsonObject
        {
            ["maps.json"] = maps, ["maps-deathmatch.json"] = dm, ["maps-wingman.json"] = new JsonArray(), ["broken.json"] = O("{\"maps\": [{\"id\": 101}]}"),
            ["removed.json"] = O("{\"verdicts\": {\"102\": \"removed\"}}"), ["votes.json"] = O("{\"110\": {\"[U:1:1]\": 1, \"[U:1:2]\": -1}, \"111\": {\"[U:1:1]\": -2}}"),
            ["ours.json"] = O("{\"maps\": [{\"id\": 900}]}"),
            ["ratings.json"] = new JsonObject
            {
                ["version"] = 1, ["fetched"] = KT - 60, ["scores"] = scores,
                ["trend"] = new JsonObject
                {
                    ["armsrace"] = Node.Strings(Enumerable.Range(60, 70).Select(r => (100 + r).ToString())), ["deathmatch"] = Node.Strings(["310", "311", "999"]), ["wingman"] = new JsonArray(),
                },
            },
            ["queues.json"] = O("{\"version\": 1, \"random\": {\"armsrace-new\": [\"120\"], \"deathmatch\": [\"301\"]}, \"requested\": [\"150\"]}"),
            ["state.json"] = O("{\"crashes\": {\"103\": 1}}"),
        };
    }

    public static Dictionary<string, JsonNode?> Inputs(JsonObject data) => data.ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone());

    static string Same(KeepSetResult k) => Node.Compact(new JsonObject
    {
        ["up"] = Node.Strings(k.Up), ["top"] = new JsonObject(k.Top.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)Node.Strings(kv.Value)))),
        ["own"] = Node.Strings(k.Own), ["hud"] = Node.Strings(k.Hud), ["requested"] = Node.Strings(k.Requested), ["all"] = Node.Strings(k.All.OrderBy(x => x, StringComparer.Ordinal)),
        ["scored"] = k.Scored, ["problems"] = Node.Strings(k.Problems),
        ["queues"] = new JsonObject(k.Queues.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)Node.Strings(kv.Value)))),
    });

    // ---- the keep set: Core's KeepSet, shared with the restart watcher
    static void KeepSetChecks(JsonObject kdata)
    {
        var syncTypes = typeof(SyncRun).Assembly.GetTypes();
        var serverTypes = typeof(MapsFolder).Assembly.GetTypes();
        Check("keep set: one copy of the rules - Core's KeepSet; neither program holds its own",
              !syncTypes.Any(t => t.Name.Contains("KeepSet", StringComparison.Ordinal)) && !serverTypes.Any(t => t.Name.Contains("KeepSet", StringComparison.Ordinal))
              && typeof(KeepSet).Assembly.GetName().Name == "OverStrike2.Core");
        Check("keep set: both programs use the same KeepSet, Core's",
              typeof(SyncRun).Assembly.GetReferencedAssemblies().Any(a => a.Name == "OverStrike2.Core") && typeof(MapsFolder).Assembly.GetReferencedAssemblies().Any(a => a.Name == "OverStrike2.Core"));
        var probe = W.Run(Cfg(), new FakeClock(KT));
        Check("keep set: both programs give the same set from the same data",
              Same(probe.SafeKeepSet(Inputs(kdata), KT, "500")) == Same(KeepSet.Safe(Inputs(kdata), SyncClock.At(KT), "500"))
              && probe.SafeKeepSet(Inputs(kdata), KT, "").Problems.SequenceEqual(KeepSet.Safe(Inputs(kdata), SyncClock.At(KT), "").Problems));
        var withBlocked = (JsonObject)kdata.DeepClone();
        withBlocked["blocked.json"] = A("[{\"id\": 105}, 106]");
        var kb = probe.SafeKeepSet(Inputs(withBlocked), KT, "500");
        var k0 = probe.SafeKeepSet(Inputs(kdata), KT, "500");
        Check("keep set: blocked.json's maps are left out of the top lists; without the file they are in, and neither is a problem",
              k0.Top["armsrace"].Contains("105") && k0.Top["armsrace"].Contains("106") && !kb.Top["armsrace"].Intersect(["105", "106"]).Any() && kb.Problems.SequenceEqual(k0.Problems));
        withBlocked["blocked.json"] = O("{\"blocked\": [105]}");
        var kb2 = probe.SafeKeepSet(Inputs(withBlocked), KT, "500");
        Check("keep set: blocked.json may also hold its list under \"blocked\"", !kb2.Top["armsrace"].Contains("105") && kb2.Top["armsrace"].Contains("106"));
    }

    // ---- the scores: IPublishedFileService/GetDetails with includevotes, with the key; a removed item is no score
    static void Votes(string root)
    {
        var (led, clock) = W.Ledger(Path.Combine(root, "votes-ledger.json"));
        var (api, sent, _) = ApiWith(led, clock, [J("""
            {"response": {"publishedfiledetails": [{"publishedfileid": "11", "result": 1, "vote_data": {"score": 0.547, "votes_up": 14, "votes_down": 3}},
             {"publishedfileid": "12", "result": 15}, {"publishedfileid": "13", "result": 1}]}}
            """)]);
        var got = api.Votes(["11", "12", "13"]);
        Check("votes: the score, the votes and the subscriptions of what the Workshop has; result 15 and no vote_data give none",
              got.Count == 1 && Node.Eq(got["11"], A("[0.547, 14, 3, 0]")));
        Check("votes: one keyed GET to IPublishedFileService/GetDetails with includevotes, counted in the ledger as GetDetails",
              sent.Count == 1 && sent[0].Url.StartsWith(SteamApi.VotesUrl, StringComparison.Ordinal) && sent[0].Url.Contains("key=" + Key) && sent[0].Url.Contains("includevotes=true")
              && sent[0].Url.Contains("publishedfileids%5B2%5D=13") && Node.Str(((JsonArray)led.Data!["calls"]!)[^1]![1]) == "GetDetails");
    }

    static SyncRun nn = null!;
    static SyncConfig ncfg2 = null!;

    // ---- a run's own steps, on a folder of their own
    static void RunSteps(string root, JsonObject kdata)
    {
        srv = Path.Combine(root, "srv");
        (sdata, sstate, swork, sgame) = (Path.Combine(srv, "data"), Path.Combine(srv, "state"), Path.Combine(srv, "work"), Path.Combine(srv, "game"));
        gws = Path.Combine(sgame, "game", "bin", "linuxsteamrt64", "steamapps", "workshop");
        foreach (var d in new[] { sdata, sstate, swork, Path.Combine(gws, "content", "730"), Path.Combine(sgame, "maps", "armsrace") })
            Directory.CreateDirectory(d);
        foreach (var (name, value) in kdata)
            Json(Path.Combine(sdata, name), value);
        (State, Work, Data, GameRoot, Depotcache) = (sstate, swork, sdata, sgame, Path.Combine(srv, "depotcache"));
        Directory.CreateDirectory(Depotcache);
        Json(Path.Combine(sstate, "state.json"), O("""
            {"schema": 1, "items": {
              "140": {"status": "skipped", "evidence": "1.5 GB: more than a whole night's download cap (800 MB)", "size": 1073741824, "tags": ["Armsrace"], "ws": "ok", "app": 730, "subs": 1, "created": 1},
              "141": {"status": "skipped", "evidence": "160.0 GB: more than a whole day's download cap (150.0 GB)", "size": 171798691840, "tags": ["Armsrace"], "ws": "ok", "app": 730, "subs": 1, "created": 1},
              "142": {"status": "skipped", "evidence": "a CS:GO-era upload (its Workshop file is x): no CS2 map, not downloaded", "size": 1, "tags": ["Armsrace"], "ws": "ok", "app": 730, "subs": 1, "created": 1},
              "143": {"status": "queued", "why": "new", "size": 5, "tags": ["Armsrace"], "ws": "ok", "app": 730, "subs": 999, "created": 1, "lifetime": 1},
              "144": {"status": "queued", "why": "new", "size": 5, "tags": ["Armsrace"], "ws": "ok", "app": 730, "subs": 1, "created": 1, "lifetime": 1}},
             "baseline": {}, "downloads": {}}
            """));
        var ncfg = Cfg();
        nn = W.Run(ncfg, new FakeClock(KT));
        nn.State.Load();
        nn.RequeueSizeSkips();
        Check("the 223: every map skipped as too big for an old daily cap is queued again, 160 GB too - there is no cap now; a CS:GO-era skip stays",
              Node.Str(nn.State.Item("140")!["status"]) == "queued" && Node.Str(nn.State.Item("141")!["status"]) == "queued" && Node.Str(nn.State.Item("142")!["status"]) == "skipped"
              && Node.Int(nn.Report["requeued"]) == 2);
        var vapi = new VoteApi(new() { ["143"] = 0.99, ["144"] = 0.01, ["140"] = 0.5, ["105"] = 0.7 },
                               trends: new() { ["Armsrace"] = ["999", "105", "106"], ["Deathmatch"] = ["301"], ["Wingman"] = [] });
        nn.Ratings(vapi, ["105", "106"]);
        var rj = Read(Path.Combine(sdata, "ratings.json"))!;
        Check("ratings: the listed maps and every map waiting for its test, scored with their subscriptions and written to the bridge's data with the time",
              vapi.Asked.ToHashSet().SetEquals(["105", "106", "140", "141", "143", "144"]) && Node.Int(rj["version"]) == 1 && Node.Float(rj["fetched"]) == KT
              && Node.Eq(rj["scores"]!["143"], A("[0.99, 1, 0, 5]")) && Node.Truthy(Node.Get(nn.Report["ratings"], "complete")));
        Check("ratings: each mode's Most Popular order over 30 days, by its tag",
              Node.Eq(rj["trend"], O("{\"armsrace\": [\"999\", \"105\", \"106\"], \"deathmatch\": [\"301\"], \"wingman\": []}")) && Node.Int(rj["trend_days"]) == 30
              && vapi.Tags.OrderBy(x => x.Tag, StringComparer.Ordinal).SequenceEqual([("Armsrace", 30), ("Deathmatch", 30), ("Wingman", 30)]));
        nn.Ratings(new VoteApi([]), ["105"]);
        Check("ratings: once a day - not again within RATINGS_EVERY", Node.Str(Node.Get(nn.Report["ratings"], "skipped")).Contains("fetched"));
        ((JsonObject)nn.State.Data["ratings"]!)["fetched"] = KT - 2 * 86400;
        nn.Report.Remove("ratings");
        nn.Ratings(new VoteApi(new() { ["105"] = 0.1 }, stopAfter: 0), ["105"]);
        Check("ratings: cut short by a stop - the old scores kept, with their old time",
              Node.Float(nn.State.Data["ratings"]!["fetched"]) == KT - 2 * 86400 && !Node.Truthy(Node.Get(nn.Report["ratings"], "complete"))
              && Node.Float(nn.State.Data["ratings"]!["scores"]!["143"]![0]) == 0.99);
        var saved = nn.State.Data["ratings"]!.DeepClone();
        ((JsonObject)nn.State.Data["ratings"]!)["fetched"] = KT;
        ((JsonObject)nn.State.Data["ratings"]!).Remove("trend");
        nn.Report.Remove("ratings");
        nn.Ratings(new VoteApi(new() { ["105"] = 0.7 }, trends: new() { ["Armsrace"] = ["105"] }), ["105"]);
        Check("ratings: at once when a list is missing (kept before the lists were three), however fresh",
              Node.Truthy(Node.Get(nn.Report["ratings"], "complete")) && ((JsonObject)nn.State.Data["ratings"]!).ContainsKey("trend"));
        nn.State.Data["ratings"] = saved;
        ((JsonObject)nn.State.Data["ratings"]!)["fetched"] = KT;
        var ratings = (JsonObject)kdata["ratings.json"]!.DeepClone();
        ratings["fetched"] = KT;
        ratings["scores"]!["143"] = A("[0.99, 1, 0]");
        ratings["scores"]!["144"] = A("[0.01, 1, 0]");
        ratings.Remove("trend");
        Json(Path.Combine(sdata, "ratings.json"), ratings);
        nn.ForgetCutoffs();
        var order = nn.Queue(new Dictionary<string, string>(), new Dictionary<string, JsonObject>());
        Check($"queue: a waiting map whose score puts it among its mode's best goes first (143), the rest by popularity: {T.Show(order.Take(4))}",
              order[0] == "143" && order.IndexOf("143") < order.IndexOf("144"));
    }

    static void WriteStagingAcf(Dictionary<string, string> entries) =>
        File.WriteAllText(Path.Combine(stg, "appworkshop_730.acf"), "\"AppWorkshop\"\n{\n\t\"WorkshopItemsInstalled\"\n\t{\n" + string.Concat(entries.Select(kv =>
            $"\t\t\"{kv.Key}\"\n\t\t{{\n\t\t\t\"size\"\t\t\"6\"\n\t\t\t\"timeupdated\"\t\t\"{kv.Value}\"\n\t\t\t\"manifest\"\t\t\"7{kv.Key}\"\n\t\t}}\n")) + "\t}\n}\n");

    static void Stage(string wid)
    {
        Directory.CreateDirectory(Path.Combine(stg, "content", "730", wid));
        File.WriteAllText(Path.Combine(stg, "content", "730", wid, wid + "_dir.vpk"), "staged");
    }

    static void Unhand(string wid)
    {
        foreach (var p in new[] { Path.Combine(hdir, wid + ".json"), Path.Combine(hdir, wid + ".manifest") })
            if (File.Exists(p))
                File.Delete(p);
        if (Directory.Exists(Path.Combine(hdir, wid)))
            Directory.Delete(Path.Combine(hdir, wid), true);
    }

    // ---- the hand-over: a staged download moved, its manifest copied, its marker last; the staging acf forgets it; the watcher reads it
    static void HandOver()
    {
        stg = Path.Combine(swork, "staging", "steamapps", "workshop");
        Directory.CreateDirectory(Path.Combine(stg, "content", "730", "305"));
        File.WriteAllText(Path.Combine(stg, "content", "730", "305", "305_dir.vpk"), "vpk!");
        File.WriteAllText(Path.Combine(stg, "appworkshop_730.acf"),
                          "\"AppWorkshop\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"WorkshopItemsInstalled\"\n\t{\n\t\t\"305\"\n\t\t{\n\t\t\t\"size\"\t\t\"4\"\n\t\t\t\"timeupdated\"\t\t\"77\"\n"
                          + "\t\t\t\"manifest\"\t\t\"555\"\n\t\t}\n\t}\n\t\"WorkshopItemDetails\"\n\t{\n\t\t\"305\"\n\t\t{\n\t\t\t\"manifest\"\t\t\"555\"\n\t\t\t\"timeupdated\"\t\t\"77\"\n"
                          + "\t\t\t\"timetouched\"\t\t\"78\"\n\t\t\t\"latest_timeupdated\"\t\t\"77\"\n\t\t\t\"latest_manifest\"\t\t\"555\"\n\t\t}\n\t}\n}\n");
        File.WriteAllBytes(Path.Combine(srv, "depotcache", "730_555.manifest"), "M"u8.ToArray());
        hdir = Path.Combine(swork, "handover");
        var (ok, words) = Handover.HandOver(Path.Combine(swork, "staging"), hdir, "305", "d5", Path.Combine(srv, "depotcache"), KT, Log.Line);
        var mk = Read(Path.Combine(hdir, "305.json"))!;
        Check($"hand-over: the folder moved, the manifest copied, the marker names its acf entries and what the folder holds ({words})",
              ok && File.Exists(Path.Combine(hdir, "305", "305_dir.vpk")) && File.ReadAllText(Path.Combine(hdir, "305.manifest")) == "M"
              && Node.Eq(mk["installed"], O("{\"size\": \"4\", \"timeupdated\": \"77\", \"manifest\": \"555\"}")) && Node.Str(mk["details"]!["latest_manifest"]) == "555"
              && (Node.Int(mk["files"]), Node.Int(mk["bytes"])) == (1, 4) && !Directory.Exists(Path.Combine(stg, "content", "730", "305"))
              && Handover.AcfItems(Path.Combine(stg, "appworkshop_730.acf")).Count == 0);
        Check("hand-over: what waits in the folder", Handover.Pending(hdir) is { Count: 1 } p && p["305"] == 4);
        Check("hand-over: nothing staged, nothing handed over", !Handover.HandOver(Path.Combine(swork, "staging"), hdir, "306", "", "", KT, Log.Line).Ok);
        // the one contract the two programs share: os2-server's MapsFolder takes in what the sync handed over
        Check("hand-over: os2-server reads the sync's marker as complete",
              MapsFolder.ReadMarker(Path.Combine(hdir, "305.json"), "305").Record is not null
              && MapsFolder.FolderStats(Path.Combine(hdir, "305")) == (Node.Int(mk["files"]), Node.Int(mk["bytes"])));
    }

    // ---- serve: the keep set's maps the server does not store are fetched and handed over; what it stores, or waits, is not
    static void Serve()
    {
        foreach (var wid in new[] { "110", "120", "150", "301", "900" })
        {
            Directory.CreateDirectory(Path.Combine(sgame, "maps", "armsrace", wid));
            var link = Path.Combine(gws, "content", "730", wid);
            if (OperatingSystem.IsLinux())
                Directory.CreateSymbolicLink(link, Path.GetRelativePath(Path.GetDirectoryName(link)!, Path.Combine(sgame, "maps", "armsrace", wid)));
            else
                Directory.CreateDirectory(link);
        }
        File.WriteAllText(Path.Combine(gws, "appworkshop_730.acf"), "\"AppWorkshop\"\n{\n\t\"WorkshopItemsInstalled\"\n\t{\n"
                          + string.Concat(new[] { "110", "120", "150", "301", "900", "999" }.Select(w => $"\t\t\"{w}\"\n\t\t{{\n\t\t\t\"timeupdated\"\t\t\"1\"\n\t\t}}\n")) + "\t}\n}\n");
        Check("server: what the game server stores - in its acf, with a folder behind its link",
              Handover.ServerMaps(sgame).Keys.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(["110", "120", "150", "301", "900"]));
        (FakeSteam.Asked, FakeSteam.Answers) = ([], []);
        Hooks.SteamCmd = (_, staging, _, _) => new AcfSteam(staging);
        Hooks.HumansOn = (_, _) => 0;
        var ratings = (JsonObject)KData()["ratings.json"]!;
        ratings["fetched"] = KT;
        Json(Path.Combine(sdata, "ratings.json"), ratings);
        ncfg2 = Cfg();
        ncfg2.DownloadPause = 0;
        var ns = W.Run(ncfg2, new FakeClock(KT));
        ns.State.Load();
        var want = ns.SafeKeepSet(ns.KeepInputs(), KT);
        ns.Serve();
        var handed = Handover.Pending(hdir).Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        missing = want.Up.Concat(want.Top["armsrace"]).Concat(want.Top["deathmatch"]).Concat(want.Own).Distinct()
            .Except(["110", "120", "150", "301", "900", "305"]).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var diff = missing.Append("305").Except(handed).Concat(handed.Except(missing.Append("305"))).ToList();
        var serveRep = (JsonObject)ns.Report["serve"]!.DeepClone();
        serveRep["diff"] = Node.Strings(diff);
        Check($"serve: every keep-set map the server lacks is fetched and handed over ({missing.Count}), 160 GB too, never one it stores or one waiting already ({Node.Dumps(serveRep)})",
              handed.SequenceEqual(missing.Append("305").OrderBy(x => x, StringComparer.Ordinal)) && !FakeSteam.Asked.Intersect(["110", "301", "900", "305"]).Any()
              && Node.Int(ns.Report["serve"]!["ok"]) == missing.Count);
        FakeSteam.Asked = [];
        var ns2 = W.Run(ncfg2, new FakeClock(KT + 60));
        ns2.State.Load();
        ns2.Serve();
        Check("serve: a second run fetches nothing more", FakeSteam.Asked.Count == 0 && Node.Int(ns2.Report["serve"]!["missing"]) == 0);
        // serve also fetches the stored keep-set maps the Workshop has a newer accepted version of - and only those
        var stateFile = Path.Combine(sstate, "state.json");
        var savedState = File.ReadAllText(stateFile);       // the runs below save it: the later tests get it back as it was
        FakeSteam.Asked = [];
        var ns7 = W.Run(ncfg2, new FakeClock(KT + 90));
        ns7.State.Load();
        foreach (var (k, v) in new[] { ("110", 50), ("120", 1), ("150", 60), ("301", 0) })
            ns7.State.Baseline[k] = v;
        ns7.State.Items["150"] = Merged(ns7.State.Item("150"), ("status", "queued"), ("why", "changed"), ("updated", 80));
        ns7.State.Items["900"] = Merged(ns7.State.Item("900"), ("status", "skipped"), ("why", "changed"), ("updated", 70));
        var keep7 = ns7.SafeKeepSet(ns7.KeepInputs(), KT);
        var (miss7, outd7) = Handover.ServeWants(keep7, Handover.ServerMaps(sgame), Handover.Pending(hdir), ns7.State.Data, ncfg2);
        Check($"serve: outdated - a stored map whose tested version is newer than the server's (110) and an own map the Workshop has newer (900, never tested here); not one as new (120) nor one whose new version waits for its test (150) ({T.Show(outd7)})",
              keep7.All.Contains("110") && keep7.All.Contains("150") && keep7.All.Contains("120") && outd7.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(["110", "900"]) && miss7.Count == 0);
        var guard = (JsonObject)ns7.State.Data.DeepClone();
        guard["baseline"]!["120"] = 30;
        guard["items"]!["120"] = O("{\"verdict\": \"crasher\", \"status\": \"published\", \"why\": \"changed\", \"updated\": 30}");
        Check("serve: nor one whose last test failed (120: the Workshop's version now is the one that crashed)",
              !Handover.ServeWants(keep7, Handover.ServerMaps(sgame), Handover.Pending(hdir), guard, ncfg2).Outdated.Contains("120"));
        Check("serve: the status file counts them", Node.Int(ns7.StatusHeavy()["queue"]!["serve"]!["outdated"]) == 2);
        ns7.Serve();
        Check($"serve: ...both are downloaded and handed over, nothing else ({T.Show(FakeSteam.Asked)})",
              FakeSteam.Asked.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(["110", "900"]) && new[] { "110", "900" }.All(Handover.Pending(hdir).ContainsKey)
              && Node.Int(ns7.Report["serve"]!["outdated"]) == 2 && Node.Int(ns7.Report["serve"]!["ok"]) == 2);
        Check("serve: the history says it was the newer version",
              Node.Arr(ns7.State.Data["history"]!["maps"]).Any(e => Node.Str(e!["id"]) == "110" && Node.Str(e["why"]).Contains("newer version")));
        FakeSteam.Asked = [];
        var ns8 = W.Run(ncfg2, new FakeClock(KT + 100));
        ns8.State.Load();
        ns8.State.Baseline["110"] = 50;
        ns8.Serve();
        Check("serve: a newer version waiting as a hand-over is not fetched again", !FakeSteam.Asked.Contains("110"));
        File.WriteAllText(stateFile, savedState);
        foreach (var w in new[] { "110", "900" })
            Unhand(w);
    }

    static JsonObject Merged(JsonObject? rec, params (string Key, JsonNode Value)[] fields)
    {
        var o = (JsonObject?)rec?.DeepClone() ?? [];
        foreach (var (k, v) in fields)
            o[k] = v.DeepClone();
        return o;
    }

    // ---- serve after a run that could not hand over (it looked before SteamCMD had written its acf): what is staged with SteamCMD's entry
    // goes without a download, an older staged version is downloaded again, a late acf is handed over once SteamCMD has quit, and the
    // clean-up keeps a keep-set pass whose hand-over failed
    static void Staged()
    {
        var (sa, sb, sc, sd) = (missing[0], missing[1], missing[2], missing[3]);
        foreach (var w in new[] { sa, sb, sc, sd })
            Unhand(w);
        Stage(sa);
        Stage(sb);
        WriteStagingAcf(new() { [sa] = "50", [sb] = "50" });
        LateAcfSteam.WriteAcf = WriteStagingAcf;
        Hooks.SteamCmd = (_, staging, _, _) => new LateAcfSteam(staging);
        FakeSteam.Asked = [];
        LateAcfSteam.Got.Clear();
        var ns3 = W.Run(ncfg2, new FakeClock(KT + 120));
        ns3.AcfWait = 1;
        ns3.State.Load();
        if (ns3.State.Item(sb) is null)
            ns3.State.Items[sb] = new JsonObject();
        ns3.State.Item(sb)!["updated"] = 100;      // the Workshop has a newer version than the staged one
        foreach (var w in new[] { sa, sc })
            ns3.State.Item(w)?.Remove("updated");
        ns3.Serve();
        var s3 = (JsonObject)ns3.Report["serve"]!;
        var waiting = Handover.Pending(hdir);
        var s3show = (JsonObject)s3.DeepClone();
        s3show["asked"] = Node.Strings(FakeSteam.Asked);
        Check($"serve: a map staged already with SteamCMD's entry is handed over without a download; an older staged version and one not staged are downloaded ({Node.Dumps(s3show)})",
              waiting.ContainsKey(sa) && !FakeSteam.Asked.Contains(sa) && Node.Int(s3["staged"]) == 1
              && FakeSteam.Asked.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(new[] { sb, sc, sd }.OrderBy(x => x, StringComparer.Ordinal)));
        Check("serve: SteamCMD's acf late - every download is handed over once it has quit, none lost",
              new[] { sb, sc, sd }.All(waiting.ContainsKey) && Node.Int(s3["ok"]) == 4 && Node.Arr(s3["failed"]).Count == 0);
        Check("serve: the staging acf names none of the handed-over maps afterwards",
              !Handover.AcfItems(Path.Combine(stg, "appworkshop_730.acf")).Keys.Intersect([sa, sb, sc, sd]).Any());
        Check("serve: the marker of the re-downloaded map carries the new version, not the staged one",
              Node.Str(Read(Path.Combine(hdir, sb + ".json"))!["installed"]!["timeupdated"]) == "200");
        var (se, sf) = ("998", missing[4]);                 // sf: downloaded once today (sd twice: the brake holds it)
        Unhand(sf);
        Stage(sf);
        Stage(se);
        WriteStagingAcf([]);
        var ncfg4 = Cfg();
        var ns4 = W.Run(ncfg4, new FakeClock(KT + 180));
        ns4.State.Load();
        foreach (var w in new[] { sf, se })
            ns4.State.Items[w] = Merged(ns4.State.Item(w), ("verdict", "pass"), ("status", "tested"), ("title", "t" + w));
        ns4.CleanupStaging();
        Check("cleanup: a keep-set pass whose hand-over failed (no acf entry) stays staged for the next run; a pass the keep set does not want is removed",
              Directory.Exists(Path.Combine(stg, "content", "730", sf)) && !Directory.Exists(Path.Combine(stg, "content", "730", se)));
        // a tested pass of a map the server stores is handed over when it is newer than the server's copy (110: the server's acf says 1, the
        // staged one 40), and removed like any decided download when it is not (120: 1 against 1)
        var stateFile = Path.Combine(sstate, "state.json");
        var savedState = File.ReadAllText(stateFile);
        Stage("110");
        Stage("120");
        WriteStagingAcf(new() { ["110"] = "40", ["120"] = "1" });
        var ns9 = W.Run(ncfg4, new FakeClock(KT + 200));
        ns9.State.Load();
        foreach (var w in new[] { "110", "120" })
            ns9.State.Items[w] = Merged(ns9.State.Item(w), ("verdict", "pass"), ("status", "tested"), ("title", "t" + w));
        ns9.CleanupStaging();
        Check("cleanup: a tested update newer than the server's copy is handed over (110), one as old as it is removed (120)",
              Handover.Pending(hdir).ContainsKey("110") && Node.Str(Read(Path.Combine(hdir, "110.json"))!["installed"]!["timeupdated"]) == "40"
              && !Directory.Exists(Path.Combine(stg, "content", "730", "120")) && !Handover.Pending(hdir).ContainsKey("120")
              && Node.Arr(ns9.State.Data["history"]!["maps"]).Any(e => Node.Str(e!["id"]) == "110" && Node.Str(e["why"]).Contains("replaces the older copy")));
        Unhand("110");
        WriteStagingAcf([]);
        File.WriteAllText(stateFile, savedState);
        Hooks.SteamCmd = (_, staging, _, _) => new AcfSteam(staging);
        FakeSteam.Asked = [];
        var ns5 = W.Run(ncfg2, new FakeClock(KT + 240));
        ns5.State.Load();
        ns5.Serve();
        Check("serve: ...and the next run downloads it (SteamCMD's acf did not name it) and hands it over", FakeSteam.Asked.SequenceEqual([sf]) && Handover.Pending(hdir).ContainsKey(sf));
        Unhand(sd);
        FakeSteam.Asked = [];
        var ns6 = W.Run(ncfg2, new FakeClock(KT + 300));
        ns6.State.Load();
        ns6.Serve();
        Check("serve: the brake - a map downloaded twice today is not downloaded a third time", FakeSteam.Asked.Count == 0 && !Handover.Pending(hdir).ContainsKey(sd));
    }

    // ---- tidy: SteamCMD would download again by itself what its acf names without a folder
    static void Tidy()
    {
        var tg = Path.Combine(srv, "tidy");
        var tstg = Path.Combine(tg, "steamapps", "workshop");
        var thand = Path.Combine(srv, "tidy-handover");
        foreach (var w in new[] { "71", "73", "74" })
            Directory.CreateDirectory(Path.Combine(tstg, "content", "730", w));
        Directory.CreateDirectory(thand);
        foreach (var w in new[] { "73", "74" })
            Json(Path.Combine(thand, w + ".json"), new JsonObject { ["version"] = 1, ["id"] = w, ["bytes"] = 1 });
        File.WriteAllText(Path.Combine(tstg, "appworkshop_730.acf"), "\"AppWorkshop\"\n{\n\t\"WorkshopItemsInstalled\"\n\t{\n"
                          + string.Concat(new[] { "71", "72", "73", "74" }.Select(w => $"\t\t\"{w}\"\n\t\t{{\n\t\t\t\"size\"\t\t\"6\"\n\t\t\t\"timeupdated\"\t\t\"5\"\n\t\t\t\"manifest\"\t\t\"7\"\n\t\t}}\n")) + "\t}\n}\n");
        var items = O("{\"71\": {\"status\": \"downloaded\"}, \"74\": {\"status\": \"downloaded\"}}");
        var res = Handover.TidyStaging(tg, thand, items, Log.Line);
        var left = Handover.AcfItems(Path.Combine(tstg, "appworkshop_730.acf")).Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Check($"tidy: an acf entry without a folder is forgotten (72), a staged copy of a waiting hand-over goes (73), a map waiting for its test stays (71, and 74 although a hand-over waits for it) (({res.Forgotten}, {res.Dropped}), {T.Show(left)})",
              res == (1, 1) && left.SequenceEqual(["71", "74"]) && !Directory.Exists(Path.Combine(tstg, "content", "730", "73")) && Directory.Exists(Path.Combine(tstg, "content", "730", "74")));
        Check("tidy: nothing to do, nothing written", Handover.TidyStaging(tg, thand, items, Log.Line) == (0, 0));
        Hooks.SteamCmd = new Seams().SteamCmd;
        Hooks.HumansOn = new Seams().HumansOn;
    }

    /// <summary>Methods declared twice with one signature in a program's types: what the compiler refuses (the Python sync once crashed
    /// every test load on a second module-level definition of one name).</summary>
    static List<string> Twice(Assembly assembly) =>
        assembly.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                             .Select(m => $"{t.FullName}.{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName))})"))
            .GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

    // ---- one definition per name, and the real test load's first steps
    static void Definitions()
    {
        foreach (var (program, assembly) in new[] { ("os2-sync", typeof(SyncRun).Assembly), ("os2-server", typeof(MapsFolder).Assembly) })
        {
            var twice = Twice(assembly);
            Check($"one definition per name in {program} (twice: {T.Show(twice)})", twice.Count == 0);
        }
        Check("mode_of is test_load's: (mode, game_type, game_mode) from the tags",
              TestLoad.ModeOf(["Wingman"]) == ("wingman", 0, 2) && TestLoad.ModeOf([]) == ("armsrace", 1, 0) && Status.ListMode(O("{\"tags\": [\"Deathmatch\"]}")) == "deathmatch");
        DownEngine.Sent.Clear();
        var got = TestLoad.Load(new DownEngine(), "4242", ["de_x"], ["Wingman"], 20, () => false, C(new FakeClock(KT)));
        Check($"test_load (the real one): the map asked for in its mode, a crash reported - no exception ({Node.StrOr(got["verdict"], "None")})",
              Node.Str(got["verdict"]) == "crash" && DownEngine.Sent.SequenceEqual(["game_type 0; game_mode 2; host_workshop_map 4242"]));
    }

    // ---- a reshaped input (a list where an object is read, a string, an object of the wrong things) is logged and counted, never a crash
    static void Reshaped(string root)
    {
        var shp = Path.Combine(root, "reshaped");
        Directory.CreateDirectory(shp);
        foreach (var (name, value) in new (string, string)[]
                 {
                     ("queues.json", "[]"), ("votes.json", "[\"x\"]"), ("ratings.json", "\"x\""), ("ours.json", "{\"maps\": \"x\"}"), ("maps.json", "{\"x\": 1}"),
                     ("broken.json", "[]"), ("removed.json", "[1]"),
                 })
            File.WriteAllText(Path.Combine(shp, name), value);
        var probe = W.Run(ncfg2, new FakeClock(KT + 400));
        var ks = probe.SafeKeepSet(KeepSet.Inputs(shp), KT);
        Check($"reshaped inputs: the keep set says why it is not to be trusted, no exception ({T.Show(ks.Problems.Take(2))})", ks.Problems.Count > 0 && ks.All.Count == 0);
        var realKeep = Hooks.KeepSet;
        Hooks.KeepSet = (_, _, _, _) => throw new InvalidCastException("'list' object has no attribute 'get'");
        var ks2 = probe.SafeKeepSet(new Dictionary<string, JsonNode?>(), KT);
        Check("reshaped inputs: an exception inside the keep set is caught and named",
              ks2.Problems[0].StartsWith("the keep set could not be computed", StringComparison.Ordinal) && ks2.All.Count == 0);
        var nsr = W.Run(ncfg2, new FakeClock(KT + 400));
        nsr.State.Load();
        nsr.Serve();
        Check("reshaped inputs: serve stops at such a keep set and names it as an error, the run goes on",
              Node.StrOr(Node.Get(nsr.Report["serve"], "skipped")).Contains("could not be computed") && Node.StrList(nsr.Report["errors"]).Any(e => e.Contains("serve:")));
        // The keep set is compiled in from Core, so it cannot be missing; these two hold what a missing one must mean: a keep set that could
        // not be computed is empty, says why, and serve hands over nothing on it
        Hooks.KeepSet = (_, _, _, _) => throw new FileNotFoundException("OverStrike2.Core's KeepSet is not loadable");
        var ksb = probe.SafeKeepSet(KeepSet.Inputs(sdata), KT, "500");
        Check($"no keep set: an empty one that says why ({T.Show(ksb.Problems.Take(1))})",
              ksb.All.Count == 0 && ksb.Problems[0].StartsWith("the keep set could not be computed: FileNotFoundException", StringComparison.Ordinal));
        var nb = W.Run(ncfg2, new FakeClock(KT + 500));
        nb.State.Load();
        nb.Serve();
        Check("no keep set: serve hands over nothing and names it as an error",Node.StrOr(Node.Get(nb.Report["serve"], "skipped")).Contains("could not be computed")
              && Node.StrList(nb.Report["errors"]).Any(e => e.Contains("KeepSet")));
        Hooks.KeepSet = realKeep;
    }
}
