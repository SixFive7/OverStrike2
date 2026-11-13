// The sync's self-test: the hand verdicts (RunAdopt.cs).
using System.Text.Json.Nodes;
using OverStrike2.Core;
using static OverStrike2.TestKit.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>The hand verdicts: what &lt;state&gt;/adopt.json may say, what a run takes of it and what it skips, and its publish onto broken.json's packs
/// through the sync's own plan and merge check.</summary>
static class AdoptTests
{
    const string Good = """{"id": 21, "name": "pack one", "kind": "pack", "evidence": "confirmed by test", "list": "tail", "phrase": null, "detail": "only models (sweep 2026-10-05)"}""";

    static (List<Adopt.Entry> Ok, List<(string Id, string Why)> Refused) P(string json) => Adopt.Parse(JsonNode.Parse(json));

    static bool Throws(string json)
    {
        try
        {
            Adopt.Parse(JsonNode.Parse(json));
            return false;
        }
        catch (BadDataException e)
        {
            return e.Message.Contains("adopt.json", StringComparison.Ordinal);
        }
    }

    // ---- what the file may say
    internal static void Parse()
    {
        var (ok, refused) = P($$"""{"nonmaps": [{{Good}}]}""");
        Check("adopt: an entry with every field it needs is taken, as written",
              refused.Count == 0 && ok is [{ Id: "21", Name: "pack one", Kind: "pack", Evidence: "confirmed by test", List: "tail", Phrase: null, Preview: "" } e]
              && e.Detail == "only models (sweep 2026-10-05)");
        (ok, refused) = P("""
            {"nonmaps": [{"id": "0042", "kind": "empty", "evidence": "identified from description", "list": "spare", "detail": "  empty \n upload ",
                          "phrase": "x.vmdl_c", "preview": "https://img.example/42"},
                         {"id": 43, "name": "  a\tname ", "kind": "legacy", "evidence": "identified from its Workshop file", "list": "scrape", "detail": "d",
                          "phrase": "", "preview": "http://img.example/43"}]}
            """);
        Check("adopt: the id as the lists write it (a string of digits, no leading zeros), a name for one without, text cleaned, an https preview kept",
              refused.Count == 0 && ok.Count == 2 && ok[0] is { Id: "42", Name: "Workshop item 42", Detail: "empty upload", Phrase: "x.vmdl_c", Preview: "https://img.example/42" }
              && ok[1] is { Id: "43", Name: "a name", Kind: "legacy", Phrase: null, Preview: "" });
        Check("adopt: a detail longer than 300 characters is clipped",
              P($$"""{"nonmaps": [{"id": 5, "kind": "pack", "evidence": "confirmed by test", "list": "tail", "detail": "{{new string('a', 400)}}"}]}""").Ok[0].Detail.Length <= 301);
        (ok, refused) = P("""
            {"nonmaps": [5, {"name": "no id"}, {"id": 0, "kind": "pack"}, {"id": "12a"},
                         {"id": 7, "kind": "skin", "evidence": "confirmed by test", "list": "tail", "detail": "d"},
                         {"id": 8, "kind": "pack", "evidence": "by eye", "list": "tail", "detail": "d"},
                         {"id": 9, "kind": "pack", "evidence": "confirmed by test", "list": "hand", "detail": "d"},
                         {"id": 10, "kind": "pack", "evidence": "confirmed by test", "list": "tail", "detail": "  "},
                         {"id": 11, "kind": "pack", "evidence": "confirmed by test", "list": "tail", "detail": "d", "phrase": 3},
                         {"id": 12, "kind": "pack", "evidence": "confirmed by test", "list": "tail", "detail": "d"},
                         {"id": "12", "kind": "empty", "evidence": "confirmed by test", "list": "tail", "detail": "d"}]}
            """);
        var why = refused.ToDictionary(r => r.Id, r => r.Why);
        Check("adopt: an entry that breaks a rule is skipped with its reason - no object, no id, a kind, evidence or list broken.json does not know, no detail, "
              + "a phrase that is no text, an id named twice - and the others are taken",
              ok.Select(x => x.Id).SequenceEqual(["12"]) && why.Count == 10
              && why["#1"] == "not an object" && why["#2"].StartsWith("no id", StringComparison.Ordinal) && why["#3"].StartsWith("no id", StringComparison.Ordinal)
              && why["#4"].StartsWith("no id", StringComparison.Ordinal) && why["7"].StartsWith("its kind is none of pack, empty, stub, legacy", StringComparison.Ordinal)
              && why["8"].StartsWith("its evidence is none of 'confirmed by test'", StringComparison.Ordinal) && why["9"].StartsWith("its list is none of live", StringComparison.Ordinal)
              && why["10"].StartsWith("no detail", StringComparison.Ordinal) && why["11"] == "its phrase is not text" && why["12"] == "named twice in the file");
        Check("adopt: a file that is not {\"nonmaps\": [...]} takes nothing (BadDataException)",
              Throws("{}") && Throws("[]") && Throws("""{"nonmaps": 5}""") && Throws("null") && Throws("""{"packs": []}"""));
    }

    // ---- the publish plan: a hand verdict's own evidence, list and phrase; the sync's own keep theirs
    internal static void Plan()
    {
        var files = new BridgeData
        {
            Maps = A("[{\"rank\": 1, \"id\": 111, \"name\": \"a\", \"sizeMb\": 5}]"), Broken = O("{\"schema\": \"x\", \"maps\": [], \"packs\": []}"),
            ModeLists = new() { ["deathmatch"] = [], ["wingman"] = [] },
        };
        var hand = O("{\"title\": \"hand\", \"description\": \"\", \"preview\": \"\"}");
        var own = O("{\"title\": \"own\", \"description\": \"d\", \"preview\": \"p\"}");
        var (texts, _) = PublishPlan.Of(files, new SyncState(Temp("adopt-plan.json")),
            [new("31", "nonmap", O("{\"kind\": \"pack\", \"detail\": \"only models\", \"evidence\": \"identified from description\", \"list\": \"tail\", \"phrase\": \"x.vmdl_c\"}"), hand),
             new("32", "nonmap", O("{\"kind\": \"empty\", \"detail\": \"nothing\"}"), own)], [], false, new Dictionary<string, JsonObject>());
        var packs = Node.Arr(O(texts["broken.json"])["packs"]).OfType<JsonObject>().ToDictionary(p => Node.Str(p["id"]));
        Check("publish: a hand verdict's packs entry says its own evidence, list and phrase",
              Node.Str(packs["31"]["evidence"]) == "identified from description" && Node.Str(packs["31"]["list"]) == "tail" && Node.Str(packs["31"]["phrase"]) == "x.vmdl_c"
              && Node.Str(packs["31"]["detail"]) == "only models" && Node.Str(packs["31"]["name"]) == "hand");
        Check("publish: ...and the sync's own still says 'confirmed by test', found by the sync, no phrase",
              Node.Str(packs["32"]["evidence"]) == "confirmed by test" && Node.Str(packs["32"]["list"]) == "sync" && packs["32"]["phrase"] is null
              && packs["32"].ContainsKey("phrase") && Node.Str(packs["32"]["description"]) == "d");
    }

    static string Entry(object id, string kind = "pack", string evidence = "confirmed by test", string list = "tail", string detail = "only models (sweep 2026-10-05)",
                        string extra = "") =>
        $$"""{"id": {{(id is string s ? $"\"{s}\"" : id)}}, "name": "item {{id}}", "kind": "{{kind}}", "evidence": "{{evidence}}", "list": "{{list}}", "detail": "{{detail}}"{{extra}}}""";

    // ---- whole runs: a file taken in, published, kept; a second run; a file that cannot be read
    internal static void InARun(string root)
    {
        var saved = (State, Work, Data, RunTests.HumansNow[0]);
        var dir = Path.Combine(root, "adopt");
        (State, Work, Data) = (Path.Combine(dir, "state"), Path.Combine(dir, "work"), Path.Combine(dir, "data"));
        try
        {
            RunTests.FakeWorld();
            RunTests.HumansNow[0] = 0;
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(State);
            const string oldPack = """{"id": 13, "name": "old pack", "kind": "pack", "evidence": "confirmed by test", "phrase": null, "detail": "models", "list": "live", "formerRank": 9, "spareRank": null, "preview": "", "description": ""}""";
            File.WriteAllText(Path.Combine(Data, "maps.json"), Node.File(A("[{\"rank\": 1, \"id\": 11, \"name\": \"a\", \"sizeMb\": 5}]"), 1));
            File.WriteAllText(Path.Combine(Data, "maps-deathmatch.json"), Node.File(A("[{\"rank\": 1, \"id\": 12, \"name\": \"dm\", \"sizeMb\": 1}]"), 1));
            File.WriteAllText(Path.Combine(Data, "broken.json"),
                              Node.File(O($$"""{"schema": "x", "maps": [{"id": 19, "rank": null, "name": "crasher", "crashes": 3, "evidence": "e"}], "packs": [{{oldPack}}], "removed": [], "blocked": []}"""), 2));
            File.WriteAllText(Path.Combine(Data, "blocked.json"), """[{"id": 14, "name": "b", "map": "b", "reason": "r"}]""");
            File.WriteAllText(Path.Combine(Data, "search.json"), Node.File(O("{\"11\": \"a 11\"}"), 0));
            var brokenBefore = File.ReadAllBytes(Path.Combine(Data, "broken.json"));
            var items = new JsonObject
            {
                ["15"] = O("""{"status": "published", "why": "new", "verdict": "pass", "decided": "2026-11-02", "title": "a pass", "tags": ["Wingman"]}"""),
                ["16"] = O("""{"status": "queued", "why": "new", "ws": "ok", "app": 730, "tags": ["Map"], "title": "waits", "subs": 1, "created": 1}"""),
                ["17"] = O("""
                    {"status": "skipped", "why": "new", "ws": "ok", "app": 730, "tags": ["Deathmatch"], "title": "old csgo", "filename": "maps/x.bsp",
                     "evidence": "a CS:GO-era upload", "description": "an old map", "preview": "https://img.example/17"}
                    """),
            };
            Json(Path.Combine(State, "state.json"), new JsonObject { ["schema"] = 1, ["items"] = items, ["baseline"] = new JsonObject(), ["downloads"] = new JsonObject() });
            var file = $$"""
                {"nonmaps": [
                  {{Entry(21)}},
                  {{Entry("22", "empty", detail: "no map and no models (vpkscan 2026-10-05)", extra: ", \"preview\": \"https://img.example/22\"")}},
                  {{Entry(23, evidence: "identified from description", detail: "skipped untested: description names model files (x.vmdl_c)", extra: ", \"phrase\": \"x.vmdl_c\"")}},
                  {{Entry(17, "legacy", "identified from its Workshop file", "sync", "a CS:GO-era upload: no CS2 map")}},
                  {{Entry(11)}}, {{Entry(12)}}, {{Entry(13)}}, {{Entry(14)}}, {{Entry(19)}}, {{Entry(15)}}, {{Entry(16)}}, {{Entry(21)}}, {{Entry(24, "skin")}},
                  {"name": "no id"}
                ]}
                """;
            File.WriteAllText(Path.Combine(State, Adopt.FileName), file);
            var cfg = Cfg();
            var fc = new FakeClock(RunTests.T0 + 600);
            var rep = W.Run(cfg, fc).Run();
            var broken = (JsonObject)Read(Path.Combine(Data, "broken.json"))!;
            var packs = Node.Arr(broken["packs"]).OfType<JsonObject>().ToList();
            var byId = packs.ToDictionary(p => Node.Str(p["id"]));
            Check("run: the hand verdicts the sync may take join broken.json's packs, after the old entries, which stay as they were (the merge check passed)",
                  Node.Eq(packs[0], O(oldPack)) && packs.Skip(1).Select(p => Node.Str(p["id"])).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(["17", "21", "22", "23"])
                  && Node.Arr(broken["maps"]).Count == 1 && PublishPlan.MergeProblems("broken.json", brokenBefore, Node.File(broken, 2)).Count == 0);
            Check("run: each with its own kind, evidence, list, phrase and detail, no rank, its preview when the file gives one, no description",
                  Node.Eq(byId["21"], O("""{"id": 21, "name": "item 21", "kind": "pack", "evidence": "confirmed by test", "phrase": null, "detail": "only models (sweep 2026-10-05)", "list": "tail", "formerRank": null, "spareRank": null, "preview": "", "description": ""}"""))
                  && Node.Str(byId["22"]["kind"]) == "empty" && Node.Str(byId["22"]["preview"]) == "https://img.example/22"
                  && Node.Str(byId["23"]["evidence"]) == "identified from description" && Node.Str(byId["23"]["phrase"]) == "x.vmdl_c");
            Check("run: a CS:GO-era upload the queue had skipped is taken, and keeps what the Workshop said of it (its title, preview and description)",
                  Node.Str(byId["17"]["kind"]) == "legacy" && Node.Str(byId["17"]["evidence"]) == "identified from its Workshop file" && Node.Str(byId["17"]["list"]) == "sync"
                  && Node.Str(byId["17"]["name"]) == "old csgo" && Node.Str(byId["17"]["preview"]) == "https://img.example/17" && Node.Str(byId["17"]["description"]) == "an old map");
            var st = (JsonObject)Read(Path.Combine(State, "state.json"))!;
            JsonObject It(string w) => Node.Obj(Node.Get(st["items"], w));
            var events = Node.Arr(Node.Get(st["history"], "maps")).OfType<JsonObject>().Where(ev => Node.Str(ev["event"]) == "broken").ToDictionary(ev => Node.Str(ev["id"]), ev => Node.Str(ev["why"]));
            Check("run: the dashboard's history has each as a broken map, with its detail - not the kind of proof it rests on",
                  events.Count == 4 && events["21"] == "only models (sweep 2026-10-05)" && events["23"] == "skipped untested: description names model files (x.vmdl_c)");
            Check("run: the state holds them as published hand verdicts (why adopted, the day), and leaves the sync's own records alone",
                  new[] { "17", "21", "22", "23" }.All(w => Node.Str(It(w)["status"]) == "published" && Node.Str(It(w)["why"]) == Adopt.Why && Node.Str(It(w)["verdict"]) == "nonmap"
                                                         && Node.Str(It(w)["adopted"]) == Node.Str(rep["day"]) && !It(w).ContainsKey("decided"))
                  && Node.Str(It("15")["verdict"]) == "pass" && Node.Str(It("16")["status"]) == "queued" && !It("16").ContainsKey("verdict"));
            var ad = Node.Obj(rep["adopt"]);
            var skipped = Node.Arr(ad["skipped"]).Select(Node.Arr).ToDictionary(r => Node.Str(r[0]), r => Node.Str(r[1]));
            Check("run: what it skips, and why: known to the bridge's lists in any mode or on any list, a verdict of the sync's own, a map it tests itself, and what the file got wrong",
                  Node.Arr(ad["adopted"]).Count == 4 && skipped.Count == 10 && skipped["11"] == "known already (listed)" && skipped["12"] == "known already (listed:deathmatch)"
                  && skipped["13"] == "known already (broken:packs)" && skipped["14"] == "known already (broken:blocked)" && skipped["19"] == "known already (broken:maps)"
                  && skipped["15"] == "the state has a verdict on it already (pass)" && skipped["16"] == "this sync tests it itself (queued)"
                  && skipped["21"] == "named twice in the file" && skipped["24"].StartsWith("its kind", StringComparison.Ordinal) && skipped["#14"].StartsWith("no id", StringComparison.Ordinal));
            var kept = Path.Combine(State, Node.Str(ad["file"]));
            Check("run: the file is taken once - moved to adopted/<time>.json, as it was",
                  !File.Exists(Path.Combine(State, Adopt.FileName)) && Node.Str(ad["file"]).StartsWith(Adopt.Folder + "/", StringComparison.Ordinal)
                  && File.Exists(kept) && File.ReadAllText(kept) == file);
            var text = File.ReadAllText(Node.Str(rep["report_path"])[..^5] + ".txt");
            Check("run: the report says what it took and skipped, and the publish lists them like the sync's own; a clean file is no problem for the mail",
                  text.Contains("0. Hand verdicts (adopt.json in the state folder)", StringComparison.Ordinal) && text.Contains("   4 taken in, 10 skipped.", StringComparison.Ordinal)
                  && text.Contains("   skipped 13: known already (broken:packs)", StringComparison.Ordinal) && text.Contains("broken.json packs  item 21 (21): pack", StringComparison.Ordinal)
                  && text.Contains($"The file is kept as {Node.Str(ad["file"])}.", StringComparison.Ordinal) && Node.StrList(rep["issues"]).All(i => !i.Contains("adopt", StringComparison.Ordinal)));
            var picture = W.Run(cfg, new FakeClock(RunTests.T0 + 900));
            picture.State.Load();
            Check("status: the totals count what this sync tested, not the hand verdicts", Node.Int(Node.Get(picture.StatusPicture()["totals"], "tested")) == 1
                  && Node.Int(Node.Get(picture.StatusPicture()["totals"], "nonmap")) == 0);
            // a second run: no file, nothing new; then a file naming what the state took already
            var after = File.ReadAllBytes(Path.Combine(Data, "broken.json"));
            var rep2 = W.Run(cfg, new FakeClock(RunTests.T0 + 1200)).Run();
            Check("run: with no file, nothing is taken and broken.json stays byte for byte", rep2["adopt"] is null && File.ReadAllBytes(Path.Combine(Data, "broken.json")).AsSpan().SequenceEqual(after));
            File.WriteAllText(Path.Combine(State, Adopt.FileName), $$"""{"nonmaps": [{{Entry(21)}}]}""");
            var rep3 = W.Run(cfg, new FakeClock(RunTests.T0 + 1800)).Run();
            Check("run: a file naming what is on broken.json now takes nothing again (known), and nothing is written twice",
                  Node.Arr(Node.Get(rep3["adopt"], "adopted")).Count == 0 && Node.Str(Node.Arr(Node.Arr(Node.Get(rep3["adopt"], "skipped"))[0])[1]) == "known already (broken:packs)"
                  && File.ReadAllBytes(Path.Combine(Data, "broken.json")).AsSpan().SequenceEqual(after)
                  && Directory.GetFiles(Path.Combine(State, Adopt.Folder)).Length == 2);
            // a file that cannot be read: nothing taken, kept as .refused.json, a problem for the mail
            File.WriteAllText(Path.Combine(State, Adopt.FileName), "{\"nonmaps\": [ not json");
            var rep4 = W.Run(cfg, new FakeClock(RunTests.T0 + 2400)).Run();
            var refusedAs = Node.Str(Node.Get(rep4["adopt"], "file"));
            Check("run: a file that does not parse takes nothing, is kept as adopted/<time>.refused.json, and the mail names it",
                  refusedAs.EndsWith(".refused.json", StringComparison.Ordinal) && File.Exists(Path.Combine(State, refusedAs)) && !File.Exists(Path.Combine(State, Adopt.FileName))
                  && File.ReadAllBytes(Path.Combine(Data, "broken.json")).AsSpan().SequenceEqual(after)
                  && Node.StrList(rep4["issues"]).Any(i => i.Contains("adopt.json not taken in", StringComparison.Ordinal))
                  && File.ReadAllText(Node.Str(rep4["report_path"])[..^5] + ".txt").Contains("   not taken in: adopt.json:", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(State, Adopt.FileName), "{\"packs\": []}");
            var rep5 = W.Run(cfg, new FakeClock(RunTests.T0 + 3000)).Run();
            Check("run: so does a file of another shape", Node.Str(Node.Get(rep5["adopt"], "refused")).Contains("is not {\"nonmaps\": [...]}", StringComparison.Ordinal)
                  && Node.StrList(rep5["errors"]).Any(e => e.StartsWith("adopt.json not taken in", StringComparison.Ordinal)));
            // the short way to take a file at once: run-now {"api": false, "only_ids": []} - no API step, no download, no test, no hand-over; the publish
            var st6 = (JsonObject)Read(Path.Combine(State, "state.json"))!;
            Node.Obj(st6["items"])["26"] = RunTests.RunItem(3);   // a map waiting for its download, which this run must leave waiting
            Json(Path.Combine(State, "state.json"), st6);
            File.WriteAllText(Path.Combine(State, Adopt.FileName), $$"""{"nonmaps": [{{Entry(25)}}]}""");
            var asked = FakeSteam.Asked.Count;
            var quick = Cfg();
            quick.Lower(O("""{"api": false, "only_ids": []}"""));
            var rep6 = W.Run(quick, new FakeClock(RunTests.T0 + 3600)).Run();
            var packs6 = Node.Arr(((JsonObject)Read(Path.Combine(Data, "broken.json"))!)["packs"]).Select(p => Node.Str(p!["id"])).ToList();
            var st6After = (JsonObject)Read(Path.Combine(State, "state.json"))!;
            Check("run: run-now {\"api\": false, \"only_ids\": []} takes the file and publishes it, and downloads, tests and hands over nothing",
                  packs6[^1] == "25" && FakeSteam.Asked.Count == asked && Node.Str(Node.Get(Node.Get(st6After["items"], "26"), "status")) == "queued"
                  && Node.Truthy(Node.Get(rep6["discovery"], "skipped")) && Node.Int(Node.Get(rep6["downloads"], "ok")) == 0);
        }
        finally
        {
            (State, Work, Data, RunTests.HumansNow[0]) = saved;
        }
    }
}
