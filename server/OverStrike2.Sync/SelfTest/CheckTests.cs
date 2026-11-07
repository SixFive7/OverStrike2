// The sync's self-test: discovery and the check.
using System.Text.Json.Nodes;
using static OverStrike2.Sync.SelfTest.T;
using static OverStrike2.Sync.SelfTest.W;
using OverStrike2.Core;

namespace OverStrike2.Sync.SelfTest;

/// <summary>Discovery's stop rule, and the check's verdicts: gone, hidden, banned, changed, the 10% guard.</summary>
static class CheckTests
{
    static JsonNode Page(int[] ids, string cursor) => new JsonObject
    {
        ["response"] = new JsonObject
        {
            ["total"] = 1000, ["next_cursor"] = cursor,
            ["publishedfiledetails"] = new JsonArray(ids.Select(i => (JsonNode?)new JsonObject
            {
                ["publishedfileid"] = i.ToString(), ["title"] = $"map {i}", ["file_size"] = "1048576", ["time_created"] = 1793400000, ["time_updated"] = 1793400000,
                ["tags"] = new JsonArray(new JsonObject { ["tag"] = "Armsrace" }), ["consumer_appid"] = 730,
            }).ToArray()),
        },
    };

    // ---- discovery: newest first, stops at the first page with nothing new
    internal static void DiscoveryChecks(string root)
    {
        var (led, clock) = W.Ledger(Path.Combine(root, "ledger-disc.json"));
        var (api, sent, _) = ApiWith(led, clock, [Page([10, 9, 8], "c2"), Page([7, 6, 5], "c3"), Page([4, 3, 2], "c4"), Page([1], "")]);
        var st = new SyncState(Path.Combine(root, "st1.json"));
        var cfg = Cfg();
        cfg.WebApiKey = Key;
        var known = new Dictionary<string, string> { ["7"] = "listed", ["6"] = "listed", ["5"] = "listed" };
        var d = Discovery.Discover(api, cfg, known, st, C(clock).Day(), clock.T + 3600, () => clock.T);
        Check("discovery: page 1 new, page 2 all known -> stops after 2 pages", Node.Int(d["pages"]) == 2 && Node.StrList(d["new"]) is ["10", "9", "8"]);
        Check("discovery: says why it stopped", Node.Str(d["stop"]).Contains("page 2 held nothing new"));
        Check("discovery: the cursor of page 1 is sent with page 2", sent[1].Url.Contains("cursor=c2"));
        Check("discovery: new items are remembered as queued with their details",
              Node.Str(st.Item("9")!["status"]) == "queued" && Node.Int(st.Item("9")!["size"]) == 1048576 && Node.Str(st.Item("9")!["ws"]) == "ok");
        (api, sent, _) = ApiWith(led, clock, [Page([11, 10, 9], "c2"), Page([8, 7], "c3")]);
        d = Discovery.Discover(api, cfg, known, st, C(clock).Day(), clock.T + 3600, () => clock.T);
        Check("discovery: the next day, an item seen before is not new (stop after the page with only id 11 new)", Node.StrList(d["new"]) is ["11"] && Node.Int(d["pages"]) == 2);
        cfg.MaxPages = 1;
        (api, sent, _) = ApiWith(led, clock, [Page([20, 21], "c2"), Page([22], "")]);
        d = Discovery.Discover(api, cfg, new Dictionary<string, string>(), st, C(clock).Day(), clock.T + 3600, () => clock.T);
        Check("discovery: sync.maxPages bounds it", Node.Int(d["pages"]) == 1 && Node.Str(d["stop"]).Contains("sync.maxPages"));
    }

    // ---- the check: removed, changed, the 10 % guard
    internal static void CheckStep(string root)
    {
        var cfg = Cfg();
        var st = new SyncState(Path.Combine(root, "st2.json"));
        var listed = Enumerable.Range(100, 60).Select(i => i.ToString()).ToList();
        var det = listed.ToDictionary(w => w, w => new JsonObject { ["publishedfileid"] = w, ["result"] = 1, ["visibility"] = 0, ["time_updated"] = 1700000000 });
        det["101"] = new JsonObject { ["publishedfileid"] = "101", ["result"] = 9 };
        det["102"]["visibility"] = 2;
        det["103"]["banned"] = true;
        det["104"]["time_updated"] = 1793000000;      // updated after sync.changedSince (2026-10-25)
        var j = Discovery.JudgeListed(det, listed, st, cfg);
        Check("check: gone, hidden and banned are told apart", j.Removed.OrderBy(x => x.Id).SequenceEqual([("101", "removed"), ("102", "hidden"), ("103", "banned")]));
        Check("check: a map updated since sync.changedSince is changed at the first check", j.Changed is ["104"]);
        Check("check: the others get a baseline", Node.Int(st.Baseline["100"]) == 1700000000 && !st.Baseline.ContainsKey("104"));
        det["100"]["time_updated"] = 1700000500;
        j = Discovery.JudgeListed(det, listed, st, cfg);
        Check("check: later, a map updated past its baseline is changed", j.Changed.Contains("100"));
        Check("check: 3 gone of 60 is believed", !j.Suspicious);
        foreach (var w in listed.Take(10))
            det[w] = new JsonObject { ["publishedfileid"] = w, ["result"] = 9 };
        j = Discovery.JudgeListed(det, listed, st, cfg);
        Check("check: over 10% of 50+ gone is not believed (the bridge's own rule)", j.Suspicious);
    }
}
