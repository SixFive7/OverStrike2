// The sync's self-test: the publish.
using System.Text.Json.Nodes;
using System.Text;
using static OverStrike2.Sync.SelfTest.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>The publish: the plan of what the bridge's files become, the merge rule that only adds, and a publish end to end against a
/// stand-in bridge.</summary>
static class PublishTests
{
    public static JsonObject Rec(string wid, string[] tags, long subs, string? title = null) => new()
    {
        ["title"] = title ?? $"map {wid}", ["tags"] = Node.Strings(tags), ["size"] = 41L << 20, ["subs"] = subs, ["lifetime"] = subs, ["description"] = "nice map", ["preview"] = "p",
    };

    // The bridge's lists of this fixture as the sync writes them - maps.json indent 1, broken.json 2, search.json 0 - byte for byte
    const string ExpectMaps = "[\n {\n  \"rank\": 1,\n  \"id\": 111,\n  \"name\": \"a\",\n  \"sizeMb\": 5\n },\n {\n  \"rank\": 2,\n  \"id\": 222,\n  \"name\": \"b\",\n  \"sizeMb\": 7\n },\n {\n  \"rank\": 3,\n  \"id\": 902,\n  \"name\": \"map 902\",\n  \"sizeMb\": 41\n },\n {\n  \"rank\": 4,\n  \"id\": 901,\n  \"name\": \"map 901\",\n  \"sizeMb\": 41\n }\n]\n";
    const string ExpectBroken = "{\n  \"schema\": \"x\",\n  \"maps\": [\n    {\n      \"id\": 905,\n      \"rank\": null,\n      \"name\": \"map 905\",\n      \"crashes\": 3,\n      \"evidence\": \"crashes the server while loading - all 3\"\n    },\n    {\n      \"id\": 906,\n      \"rank\": null,\n      \"name\": \"map 906\",\n      \"crashes\": 0,\n      \"evidence\": \"posts a gambling ad in chat\",\n      \"reason\": \"posts a gambling ad\"\n    },\n    {\n      \"id\": 222,\n      \"rank\": 2,\n      \"name\": \"map 222\",\n      \"crashes\": 3,\n      \"evidence\": \"crashes after its update\"\n    }\n  ],\n  \"packs\": [\n    {\n      \"id\": 904,\n      \"name\": \"map 904\",\n      \"kind\": \"pack\",\n      \"evidence\": \"confirmed by test\",\n      \"phrase\": null,\n      \"detail\": \"models only\",\n      \"list\": \"sync\",\n      \"formerRank\": null,\n      \"spareRank\": null,\n      \"preview\": \"p\",\n      \"description\": \"nice map\"\n    }\n  ],\n  \"removed\": [],\n  \"blocked\": []\n}\n";
    public const string ExpectSearch = "{\n\"111\": \"a 111\",\n\"222\": \"b 222\",\n\"902\": \"map 902 902 armsrace nice map\",\n\"901\": \"map 901 901 armsrace deathmatch nice map\",\n\"903\": \"map 903 903 wingman nice map\"\n}\n";

    static List<Verdicts.Outcome> Outcomes() =>
    [
        new("901", "pass", O("{\"level\": \"x\"}"), Rec("901", ["Armsrace", "Deathmatch"], 5)),
        new("902", "pass", O("{\"level\": \"y\"}"), Rec("902", ["Armsrace"], 50)),
        new("903", "pass", O("{\"level\": \"z\"}"), Rec("903", ["Wingman"], 1)),
        new("904", "nonmap", O("{\"kind\": \"pack\", \"detail\": \"models only\"}"), Rec("904", ["Armsrace"], 0)),
        new("905", "crasher", O("{\"crashes\": 3, \"evidence\": \"crashes the server while loading - all 3\"}"), Rec("905", ["Armsrace"], 0)),
        new("906", "advert", O("{\"reason\": \"posts a gambling ad\", \"evidence\": \"posts a gambling ad in chat\", \"sample\": \"clash.gg\"}"), Rec("906", ["Armsrace"], 0)),
        new("222", "crasher", O("{\"crashes\": 3, \"evidence\": \"crashes after its update\"}"), Rec("222", ["Armsrace"], 0)),
    ];

    static BridgeData Files(string maps, string broken, string dm, string wm, string? search, string? blocked = null) => new()
    {
        Maps = A(maps), Broken = O(broken), ModeLists = new() { ["deathmatch"] = A(dm), ["wingman"] = A(wm) }, Search = search is null ? null : O(search),
        Blocked = blocked is null ? [] : A(blocked),
    };

    // ---- the publish plan
    internal static void Plan(string root)
    {
        const string maps = "[{\"rank\": 1, \"id\": 111, \"name\": \"a\", \"sizeMb\": 5}, {\"rank\": 2, \"id\": 222, \"name\": \"b\", \"sizeMb\": 7}]";
        const string broken = "{\"schema\": \"x\", \"maps\": [], \"packs\": [], \"removed\": [], \"blocked\": []}";
        const string search = "{\"111\": \"a 111\", \"222\": \"b 222\"}";
        var files = Files(maps, broken, "[]", "[]", search);
        var st = new SyncState(Path.Combine(root, "st4.json"));
        var details = new Dictionary<string, JsonObject> { ["111"] = O("{\"publishedfileid\": \"111\", \"result\": 9, \"tags\": [{\"tag\": \"Wingman\"}], \"subscriptions\": 9}") };
        var (texts, summary) = PublishPlan.Of(files, st, Outcomes(), [("111", "removed")], true, details);
        var newmaps = A(texts["maps.json"]);
        Check("publish: Arms Race passes are appended, most subscribed first, ranks go on",
              newmaps.Skip(2).Select(e => (Node.Int(e!["rank"]), Node.Int(e["id"]))).SequenceEqual([(3L, 902L), (4L, 901L)]) && Node.Eq(new JsonArray(newmaps.Take(2).Select(e => e?.DeepClone()).ToArray()), A(maps)));
        Check("publish: maps.json is written in its own format (indent 1, a final newline)", texts["maps.json"] == ExpectMaps);
        Check("publish: sizeMb in whole MB", Node.Int(newmaps[2]!["sizeMb"]) == 41);
        Check("publish: search.json gets the new maps", O(texts["search.json"]).ContainsKey("902") && Node.Str(O(texts["search.json"])["902"]).Contains("nice"));
        var nb = O(texts["broken.json"]);
        Check("publish: a pack goes on packs with its kind", Node.Arr(nb["packs"]).Select(p => Node.Int(p!["id"])).SequenceEqual([904L]) && Node.Str(nb["packs"]![0]!["kind"]) == "pack");
        Check("publish: a crasher and an advert go on maps", Node.Arr(nb["maps"]).Select(e => Node.Int(e!["id"])).ToHashSet().SetEquals([905L, 906L, 222L]));
        Check("publish: the advert has its reason, crashes 0", Node.Str(Node.Arr(nb["maps"]).First(e => Node.Int(e!["id"]) == 906)!["reason"]) == "posts a gambling ad");
        Check("publish: a listed map that turned bad keeps its rank on broken.json", Node.Int(Node.Arr(nb["maps"]).First(e => Node.Int(e!["id"]) == 222)!["rank"]) == 2);
        Check("publish: a listed map gone from the Workshop goes to the bridge as a verdict, never into broken.json", Node.Arr(nb["removed"]).Count == 0);
        Check("publish: ...but it is in the summary, for the bridge's report and the mail", Node.Eq(summary["removed"], A("[[\"111\", 1, \"a\", \"removed\"]]")));
        Check("publish: broken.json keeps its format (indent 2)", texts["broken.json"] == ExpectBroken);
        var dm = A(texts["maps-deathmatch.json"]);
        var wm = A(texts["maps-wingman.json"]);
        Check("publish: the Deathmatch list gets the pass tagged Deathmatch, in the maps.json shape",
              dm.Select(e => Node.Int(e!["id"])).SequenceEqual([901L]) && ((JsonObject)dm[0]!).Select(kv => kv.Key).ToHashSet().SetEquals(["rank", "id", "name", "sizeMb"]));
        Check("publish: the Wingman list gets the Wingman pass (rank by popularity)", wm.Select(e => Node.Int(e!["id"])).SequenceEqual([903L]));
        Check("publish: the removed listed map is not backfilled into a mode list", wm.All(e => Node.Int(e!["id"]) != 111));
        var files2 = Files(maps, broken, "[{\"rank\": 1, \"id\": 7, \"name\": \"q\", \"sizeMb\": 1}, {\"name\": \"de_dust2\", \"official\": true}]", "[]", null);
        var details2 = new Dictionary<string, JsonObject> { ["111"] = O("{\"publishedfileid\": \"111\", \"result\": 1, \"tags\": [{\"tag\": \"Deathmatch\"}], \"subscriptions\": 9}") };
        var (texts2, _) = PublishPlan.Of(files2, st, [], [], true, details2);
        var dm2 = A(texts2["maps-deathmatch.json"]);
        Check("publish: backfill adds a listed map that carries the tag, keeps the old entries and the official one",
              dm2.Where(e => Node.Truthy(e!["id"])).Select(e => Node.Int(e!["id"])).SequenceEqual([111L, 7L]) && Node.Eq(dm2[^1], O("{\"name\": \"de_dust2\", \"official\": true}")));
        Check("publish: nothing to write when nothing changed", PublishPlan.Of(files2, st, [], [], false, new Dictionary<string, JsonObject>()).Texts.Count == 0);
        // blocked.json: the maps blocked for good, a file of their own - known, never a candidate, never published by a test
        var fb = Files(maps, broken, "[]", "[]", search, "[{\"id\": 902, \"name\": \"b\", \"map\": \"b\", \"reason\": \"kicks everybody\"}, 905]");
        var (tb, _) = PublishPlan.Of(fb, st, Outcomes(), [], true, details);
        Check("blocked.json: a blocked map that passed is not appended, a blocked crasher not added to broken.json",
              A(tb["maps.json"]).All(e => Node.Int(e!["id"]) != 902) && Node.Arr(O(tb["broken.json"])["maps"]).All(e => Node.Int(e!["id"]) != 905));
        Check("blocked.json: its ids are known, as broken:blocked; without the file nothing is",
              fb.KnownIds(Cfg()).GetValueOrDefault("902") == "broken:blocked" && fb.KnownIds(Cfg()).GetValueOrDefault("905") == "broken:blocked" && !files.KnownIds(Cfg()).ContainsKey("902"));
        var se = O(texts["search.json"]);
        Check("publish: search.json gets the mode lists' new maps too, whatever their mode",
              se.ContainsKey("903") && Node.Str(se["903"]).Contains("nice") && Node.Str(se["903"]).Contains("wingman") && texts["search.json"] == ExpectSearch);
        st.Items["7"] = O("{\"title\": \"q old\", \"tags\": [\"Deathmatch\"], \"description\": \"red walls\"}");
        var files3 = Files(maps, broken, "[{\"rank\": 1, \"id\": 7, \"name\": \"q\", \"sizeMb\": 1}, {\"name\": \"de_dust2\", \"official\": true}]", "[]", search);
        var (texts3, _) = PublishPlan.Of(files3, st, [], [], false, new Dictionary<string, JsonObject>());
        Check("publish: a map on a mode list before search.json covered it gets its text from the state, the other texts stay, an official entry gets none",
              texts3.ContainsKey("search.json") && Node.Eq(O(texts3["search.json"]), O("{\"111\": \"a 111\", \"222\": \"b 222\", \"7\": \"q 7 deathmatch red walls\"}")) && texts3.Keys.SequenceEqual(["search.json"]));
        files3.Search = O(texts3["search.json"]);
        Check("publish: ...and then there is nothing to write", PublishPlan.Of(files3, st, [], [], false, new Dictionary<string, JsonObject>()).Texts.Count == 0);
    }

    // ---- the merge check: a publish only ever adds to the live files
    internal static void MergeRule()
    {
        var old = Encoding.UTF8.GetBytes("[{\"rank\": 1, \"id\": 5, \"name\": \"a\", \"sizeMb\": 1}]");
        Check("merge: an append passes", PublishPlan.MergeProblems("maps.json", old, "[{\"rank\": 1, \"id\": 5, \"name\": \"a\", \"sizeMb\": 1}, {\"rank\": 2, \"id\": 6, \"name\": \"b\", \"sizeMb\": 1}]").Count == 0);
        Check("merge: a changed entry is refused", PublishPlan.MergeProblems("maps.json", old, "[{\"rank\": 1, \"id\": 5, \"name\": \"X\", \"sizeMb\": 1}]").Count > 0);
        Check("merge: a rank given twice is refused", PublishPlan.MergeProblems("maps.json", old, "[{\"rank\": 1, \"id\": 5, \"name\": \"a\", \"sizeMb\": 1}, {\"rank\": 1, \"id\": 6, \"name\": \"b\", \"sizeMb\": 1}]").Count > 0);
        var ob = Encoding.UTF8.GetBytes("{\"schema\": \"x\", \"maps\": [{\"id\": 1}], \"packs\": []}");
        Check("merge: broken.json with entries added passes", PublishPlan.MergeProblems("broken.json", ob, "{\"schema\": \"x\", \"maps\": [{\"id\": 1}, {\"id\": 2}], \"packs\": [{\"id\": 3}]}").Count == 0);
        Check("merge: broken.json losing an entry is refused", PublishPlan.MergeProblems("broken.json", ob, "{\"schema\": \"x\", \"maps\": [], \"packs\": []}").Count > 0);
        Check("merge: a mode list may re-rank but never lose a map",
              PublishPlan.MergeProblems("maps-wingman.json", "[{\"rank\": 1, \"id\": 7}]"u8.ToArray(), "[{\"rank\": 1, \"id\": 8}, {\"rank\": 2, \"id\": 7}]").Count == 0
              && PublishPlan.MergeProblems("maps-wingman.json", "[{\"rank\": 1, \"id\": 7}]"u8.ToArray(), "[{\"rank\": 1, \"id\": 8}]").Count > 0);
    }

    // ---- the bridge: the verdicts report and the reload, against a stand-in bridge on localhost; a run's publish and finish end to end
    internal static void Bridge(string root)
    {
        using var srv = new MiniHttp();
        (BridgeUrl, BridgeSecret) = ($"http://127.0.0.1:{srv.Port}", "s3cr3t");
        var one = W.Run(Cfg(), new FakeClock(1793500000.0));
        var (reached, ans) = one.ReportVerdicts([("111", "ok"), ("222", "removed"), ("333", "hidden")]);
        Check("bridge: the verdicts go to POST /removed/<secret> in the plugin's format (magic line, id TAB verdict)",
              srv.Got[0].Path == "/removed/s3cr3t" && srv.Got[0].Body == "gg-removed\t1\n111\tok\n222\tremoved\n333\thidden\n" && (srv.Got[0].ContentType ?? "").StartsWith("text/plain", StringComparison.Ordinal));
        Check("bridge: its answer is kept for the report, and it arrived", reached && ans.StartsWith("3 checked", StringComparison.Ordinal));
        Check("bridge: POST /reload/<secret> after a publish", one.Reload() == "reload: maps: 9 maps" && srv.Got[1].Path == "/reload/s3cr3t");
        var data = Data;
        Directory.CreateDirectory(data);
        const string liveMaps = "[{\"rank\": 1, \"id\": 111, \"name\": \"a\", \"sizeMb\": 5}, {\"rank\": 2, \"id\": 222, \"name\": \"b\", \"sizeMb\": 7}]";
        const string liveBroken = "{\"schema\": \"x\", \"maps\": [], \"packs\": [], \"removed\": [{\"id\": 3659287341}], \"blocked\": []}";
        File.WriteAllText(Path.Combine(data, "maps.json"), Node.File(A(liveMaps), 1));
        File.WriteAllText(Path.Combine(data, "broken.json"), Node.File(O(liveBroken), 2));
        File.WriteAllText(Path.Combine(data, "search.json"), Node.File(O("{\"111\": \"a 111\", \"222\": \"b 222\"}"), 0));
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
            Check("blocked.json: one that is no list stops the publish (BadDataException)", false);
        }
        catch (BadDataException e)
        {
            Check("blocked.json: one that is no list stops the publish (BadDataException)", e.Message.Contains("blocked.json"));
        }
        File.Delete(Path.Combine(data, "blocked.json"));
        nt.Publish([new("901", "pass", O("{\"level\": \"x\"}"), passRec), new("902", "nonmap", O("{\"kind\": \"pack\", \"detail\": \"models\"}"), packRec)],
                   [("222", "removed")], new Dictionary<string, JsonObject>());
        var after = (JsonArray)Read(Path.Combine(data, "maps.json"))!;
        Check("run: the pass is appended to the live maps.json as #3",
              Node.Eq(new JsonArray(after.Take(2).Select(e => e?.DeepClone()).ToArray()), A(liveMaps)) && Node.Int(after[2]!["id"]) == 901 && Node.Int(after[2]!["rank"]) == 3);
        var brokenNow = Read(Path.Combine(data, "broken.json"))!;
        Check("run: the pack joined broken.json's packs, the removed seed untouched",
              Node.Arr(brokenNow["packs"]).Select(p => Node.Int(p!["id"])).SequenceEqual([902L]) && Node.Eq(brokenNow["removed"], A("[{\"id\": 3659287341}]")));
        Check("run: the Wingman list is created with the pass", Node.Arr(Read(Path.Combine(data, "maps-wingman.json"))).Select(e => Node.Int(e!["id"])).SequenceEqual([901L]));
        Check("run: the verdicts went to /removed, then the files were reloaded",
              srv.Got.Select(g => g.Path).SequenceEqual(["/removed/s3cr3t", "/reload/s3cr3t"]) && srv.Got[0].Body.Contains("222\tremoved"));
        Check("run: the old files are backed up", Directory.GetDirectories(Path.Combine(nt.Cfg.StateDir, "backups")).Length == 1);
        Check("run: search.json, written after the reload (the reload does not read it), holds the new map",
              ((JsonObject)Read(Path.Combine(data, "search.json"))!).ContainsKey("901") && Node.StrList(Node.Get(nt.Report["published"], "files")).Contains("search.json"));
        Check("run: both items are marked published", Node.Str(passRec["status"]) == "published" && Node.Str(packRec["status"]) == "published");
        // a file that changed under the plan is planned again; a result that is not the live files plus additions is refused
        var nt2 = W.Run(Cfg(), new FakeClock(1793500100.0));
        nt2.Ledger.Load();
        nt2.State.Load();
        var rec3 = O("{\"title\": \"third\", \"tags\": [\"Armsrace\"], \"size\": 1048576, \"subs\": 1, \"status\": \"tested\", \"verdict\": \"pass\", \"updated\": 1}");
        var grown = (JsonArray)after.DeepClone();
        grown.Add(O("{\"rank\": 4, \"id\": 950, \"name\": \"added by the release\", \"sizeMb\": 1}"));
        File.WriteAllText(Path.Combine(data, "maps.json"), Node.File(grown, 1));
        nt2.Publish([new("903", "pass", O("{\"level\": \"y\"}"), rec3)], [], new Dictionary<string, JsonObject>());
        var nowMaps = (JsonArray)Read(Path.Combine(data, "maps.json"))!;
        Check("run: a maps.json that grew meanwhile is merged into, never written from the older copy",
              Node.Eq(new JsonArray(nowMaps.Take(4).Select(e => e?.DeepClone()).ToArray()), grown) && Node.Int(nowMaps[4]!["id"]) == 903 && Node.Int(nowMaps[4]!["rank"]) == 5);
        nt.Finish();
        var repTxt = Directory.GetFiles(Path.Combine(nt.Cfg.StateDir, "reports"), "*.txt");
        Check("run: the report and its text are written; a run without a problem mails nothing",
              repTxt.Length > 0 && Node.Str(nt.Report["mail"]).StartsWith("NOT sent: no problem", StringComparison.Ordinal) && Node.Arr(nt.Report["issues"]).Count == 0);
        (BridgeUrl, BridgeSecret) = ("", "");
    }
}
