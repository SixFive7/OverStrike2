// The sync's self-test: the keep set, the scores and the hand-over to the game server.
using System.Text.Json.Nodes;
using OverStrike2.Core;
using MapsFolder = OverStrike2.Server.MapsFolder;
using ServerConfig = OverStrike2.Server.ServerConfig;
using ServerLog = OverStrike2.Server.Log;
using ServerPaths = OverStrike2.Server.ServerPaths;
using OverStrike2.TestKit;
using static OverStrike2.TestKit.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>The acf, the keep set (core's), the scores and the ratings, the queue, the hand-over and its marker, serve, the staging
/// folder's clean-up and tidy, reshaped inputs, and the hand-over taken in by the restart watcher's own code.</summary>
static class HandOverTests
{
    internal const double KT = 1793700000.0;
    static string srv = "", sdata = "", sstate = "", swork = "", sgame = "", gws = "", stg = "", hdir = "";
    static List<string> missing = [];

    internal static void Acf(string root)
    {
        var acf = Path.Combine(root, "appworkshop_730.acf");
        File.WriteAllText(acf, "\"AppWorkshop\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"WorkshopItemsInstalled\"\n\t{\n\t\t\"1\"\n\t\t{\n\t\t\t\"timeupdated\"\t\t\"5\"\n\t\t}\n"
                               + "\t\t\"2\"\n\t\t{\n\t\t\t\"timeupdated\"\t\t\"6\"\n\t\t}\n\t}\n\t\"WorkshopItemDetails\"\n\t{\n\t\t\"1\"\n\t\t{\n\t\t\t\"manifest\"\t\t\"9\"\n\t\t}\n\t}\n}\n");
        var items = Handover.AcfItems(acf);
        Check("acf: items read", items.Count == 2 && items["1"] == 5 && items["2"] == 6);
        Handover.AcfForget(acf, ["1"]);
        Check("acf: an item is forgotten in both sections, the rest kept", Handover.AcfItems(acf) is { Count: 1 } left && left["2"] == 6 && !File.ReadAllText(acf).Contains("\"1\""));
    }

    internal static JsonObject KData()
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

    // ---- the keep set: Core's KeepSet, shared with the restart watcher
    internal static void KeepSetChecks(JsonObject kdata)
    {
        var probe = W.Run(Cfg(), new FakeClock(KT));
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
    internal static void Votes(string root)
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
    internal static void RunSteps(string root, JsonObject kdata)
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
              "140": {"status": "queued", "why": "new", "size": 1073741824, "tags": ["Armsrace"], "ws": "ok", "app": 730, "subs": 1, "created": 1},
              "141": {"status": "queued", "why": "new", "size": 171798691840, "tags": ["Armsrace"], "ws": "ok", "app": 730, "subs": 1, "created": 1},
              "142": {"status": "skipped", "evidence": "a CS:GO-era upload (its Workshop file is x): no CS2 map, not downloaded", "size": 1, "tags": ["Armsrace"], "ws": "ok", "app": 730, "subs": 1, "created": 1},
              "143": {"status": "queued", "why": "new", "size": 5, "tags": ["Armsrace"], "ws": "ok", "app": 730, "subs": 999, "created": 1, "lifetime": 1},
              "144": {"status": "queued", "why": "new", "size": 5, "tags": ["Armsrace"], "ws": "ok", "app": 730, "subs": 1, "created": 1, "lifetime": 1}},
             "baseline": {}, "downloads": {}}
            """));
        var ncfg = Cfg();
        nn = W.Run(ncfg, new FakeClock(KT));
        nn.State.Load();
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
        Check("ratings: once a day - not again within SyncRun.RatingsEvery", Node.Str(Node.Get(nn.Report["ratings"], "skipped")).Contains("fetched"));
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
        var order = nn.Queue(new Dictionary<string, string>());
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
    internal static void HandOver()
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
              && Files.FolderStats(Path.Combine(hdir, "305")) == (Node.Int(mk["files"]), Node.Int(mk["bytes"])));
    }

    // ---- serve: the keep set's maps the server does not store are fetched and handed over; what it stores, or waits, is not
    internal static void Serve()
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
        Check($"serve: every keep-set map the server lacks is fetched and handed over ({missing.Count}), 160 GB too, never one it stores or one waiting already ({Node.Compact(serveRep)})",
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
        Check("serve: the status file counts them", Node.Int(ns7.StatusPicture()["queue"]!["serve"]!["outdated"]) == 2);
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
        // the same content is the same version: Steam's acf dates a map by its content, the Workshop by its page, and a map published a few
        // minutes after its upload stays dated earlier in the acf, for good. 110 below: the server's acf says 1, its tested version 50, but
        // the server holds the Workshop's own content (8110) - nothing to fetch, where the dates alone fetched it and handed it over twice a
        // day, for the server to drop it as not needed
        Check("serve: Holds - a later or equal date holds a version; an earlier one only with the Workshop's own content",
              Handover.Holds(50, "1", 50, "2") && Handover.Holds(51, null, 50, "") && Handover.Holds(1, "8110", 50, "8110")
              && !Handover.Holds(1, "8110", 50, "8111") && !Handover.Holds(1, "8110", 50, "") && !Handover.Holds(1, "", 50, ""));
        var serverAcf = Path.Combine(gws, "appworkshop_730.acf");
        var savedAcf = File.ReadAllText(serverAcf);
        File.WriteAllText(serverAcf, savedAcf.Replace("\t\t\"110\"\n\t\t{\n\t\t\t\"timeupdated\"\t\t\"1\"\n",
                                                      "\t\t\"110\"\n\t\t{\n\t\t\t\"timeupdated\"\t\t\"1\"\n\t\t\t\"manifest\"\t\t\"8110\"\n"));
        FakeSteam.Asked = [];
        var ns10 = W.Run(ncfg2, new FakeClock(KT + 110));
        ns10.State.Load();
        ns10.State.Baseline["110"] = 50;
        ns10.State.Workshop["110"] = O("{\"updated\": 50, \"content\": \"8110\"}");
        var keep10 = ns10.SafeKeepSet(ns10.KeepInputs(), KT);
        var same = Handover.ServeWants(keep10, Handover.ServerMaps(sgame), Handover.Pending(hdir), ns10.State.Data, ncfg2, Handover.ServerManifests(sgame)).Outdated;
        var other = (JsonObject)ns10.State.Data.DeepClone();
        other["workshop"]!["110"]!["content"] = "8111";
        var moved = Handover.ServeWants(keep10, Handover.ServerMaps(sgame), Handover.Pending(hdir), other, ncfg2, Handover.ServerManifests(sgame)).Outdated;
        Check($"serve: a stored map dated in its acf before its tested version, with the Workshop's own content, is not outdated (110); with other content it is ({T.Show(same)}, {T.Show(moved)})",
              Handover.ServerManifests(sgame).GetValueOrDefault("110") == "8110" && !same.Contains("110") && moved.Contains("110"));
        ns10.Serve();
        Check($"serve: ...so it is neither downloaded nor handed over ({T.Show(FakeSteam.Asked)})",
              !FakeSteam.Asked.Contains("110") && !Handover.Pending(hdir).ContainsKey("110") && Node.Int(ns10.Report["serve"]!["outdated"]) == 0);
        File.WriteAllText(serverAcf, savedAcf);
        File.WriteAllText(stateFile, savedState);
        Unhand("110");                                     // nothing to undo when the check passes; when it fails, the later checks stay as they were
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
    internal static void Staged()
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
        Check($"serve: a map staged already with SteamCMD's entry is handed over without a download; an older staged version and one not staged are downloaded ({Node.Compact(s3show)})",
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
    internal static void Tidy()
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

    // ---- a reshaped input (a list where an object is read, a string, an object of the wrong things) is logged and counted, never a crash
    internal static void Reshaped(string root)
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
        // a key twice in one object parses, and throws once it is read: an exception inside the keep set itself
        var votesPath = Path.Combine(sdata, "votes.json");
        var votes = File.ReadAllText(votesPath);
        File.WriteAllText(votesPath, "{\"110\": {\"[U:1:1]\": 1}, \"110\": {\"[U:1:2]\": 1}}");
        var ks2 = probe.SafeKeepSet(probe.KeepInputs(), KT);
        Check($"reshaped inputs: an exception inside the keep set is caught and named ({T.Show(ks2.Problems.Take(1))})",
              ks2.Problems[0].StartsWith("the keep set could not be computed: ArgumentException", StringComparison.Ordinal) && ks2.All.Count == 0);
        var nsr = W.Run(ncfg2, new FakeClock(KT + 400));
        nsr.State.Load();
        nsr.Serve();
        Check("reshaped inputs: serve stops at such a keep set and names it as an error, the run goes on",
              Node.StrOr(Node.Get(nsr.Report["serve"], "skipped")).Contains("could not be computed") && Node.StrList(nsr.Report["errors"]).Any(e => e.Contains("serve:")));
        File.WriteAllText(votesPath, votes);
    }

    // ---- the whole hand-over: what the sync hands over, the restart watcher's own code takes in, and the sync then sees the map as stored
    internal static void TakenIn(string root)
    {
        const string name = "hand-over: the restart watcher's organize takes in what the sync handed over - its folder, its link, its acf entries, its manifest - and the sync then sees it stored";
        if (!OperatingSystem.IsLinux())
        {
            Linux(name, () => false);
            return;
        }
        var game = Path.Combine(root, "ti-game");
        var data = Path.Combine(root, "ti-data");
        var work = Path.Combine(root, "ti-work");
        var serverWs = Path.Combine(game, "game", "bin", "linuxsteamrt64", "steamapps", "workshop");
        Directory.CreateDirectory(Path.Combine(serverWs, "content", "730"));
        var acf = new List<Kv>
        {
            Kv.Of("AppWorkshop", [Kv.Text("appid", "730"), Kv.Text("SizeOnDisk", "0"), Kv.Of("WorkshopItemsInstalled", []), Kv.Of("WorkshopItemDetails", [])]),
        };
        File.WriteAllText(Path.Combine(serverWs, "appworkshop_730.acf"), KeyValues.Dump(acf));
        Put(Path.Combine(data, "maps.json"), "[{\"rank\": 1, \"id\": 4100, \"name\": \"Taken In\", \"sizeMb\": 1}]");
        var staging = Path.Combine(work, "staging");
        var stagingWs = Path.Combine(staging, "steamapps", "workshop");
        Put(Path.Combine(stagingWs, "content", "730", "4100", "4100_dir.vpk"), "a map");
        Put(Path.Combine(stagingWs, "content", "730", "4100", "maps", "inner.txt"), "more");
        Put(Path.Combine(stagingWs, "appworkshop_730.acf"), KeyValues.Dump(
        [
            Kv.Of("AppWorkshop", [Kv.Of("WorkshopItemsInstalled", [Kv.Of("4100", [Kv.Text("size", "9"), Kv.Text("timeupdated", "1790000000"), Kv.Text("manifest", "424242")])]),
                                  Kv.Of("WorkshopItemDetails", [Kv.Of("4100", [Kv.Text("manifest", "424242"), Kv.Text("timeupdated", "1790000000"), Kv.Text("timetouched", "1790000001")])])]),
        ]));
        var depot = Path.Combine(root, "ti-depot");
        Put(Path.Combine(depot, "730_424242.manifest"), "manifest bytes");
        var handover = Path.Combine(game, "workshop-sync", "handover");
        var (ok, words) = Handover.HandOver(staging, handover, "4100", "Taken In", depot, RunTests.T0, Log.Line);
        var serverDepot = Path.Combine(root, "ti-server-depot");
        Directory.CreateDirectory(serverDepot);
        var config = ServerConfig.From(new OverStrike2.Core.Settings(), _ => null) with
        {
            Paths = new ServerPaths { GameDir = game, BridgeData = data, Handover = handover, Depotcache = serverDepot, Plugins = Path.Combine(root, "ti-plugins") },
            MapsDelete = "off",
        };
        var clock = new FakeClock(RunTests.T0);
        var log = new ServerLog("test", clock, Paris, _ => { });
        var said = new MapsFolder(config, log, clock, Paris).Organize();
        var stored = Handover.ServerMaps(game);
        var link = Path.Combine(serverWs, "content", "730", "4100");
        Check(name, () => ok && stored.TryGetValue("4100", out var t) && t == 1790000000 && Files.IsLink(link)
                          && File.Exists(Path.Combine(link, "maps", "inner.txt")) && File.Exists(Path.Combine(serverDepot, "730_424242.manifest"))
                          && !File.Exists(Path.Combine(handover, "4100.json")) && KeyValues.Entries(File.ReadAllText(Path.Combine(serverWs, "appworkshop_730.acf")), "4100") is { } e
                          && e.Details.GetValueOrDefault("timetouched") == "1790000001" && words.StartsWith("handed over", StringComparison.Ordinal) && said.Length > 0);
    }
}
