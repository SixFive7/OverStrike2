// The sync's self-test: discovery and the check.
using System.Text.Json.Nodes;
using static OverStrike2.TestKit.T;
using static OverStrike2.Sync.SelfTest.W;
using OverStrike2.Core;

namespace OverStrike2.Sync.SelfTest;

/// <summary>Discovery's stop rule, and the check's verdicts: gone, hidden, banned, changed, the 10% guard.</summary>
static class CheckTests
{
    internal static JsonNode Page(int[] ids, string cursor) => new JsonObject
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

    // ---- discovery: newest first, stops at the first page with nothing new (the week's full walk done already: WalkChecks)
    internal static void DiscoveryChecks(string root)
    {
        var (led, clock) = W.Ledger(Path.Combine(root, "ledger-disc.json"));
        var (api, sent, _) = ApiWith(led, clock, [Page([10, 9, 8], "c2"), Page([7, 6, 5], "c3"), Page([4, 3, 2], "c4"), Page([1], "")]);
        var st = new SyncState(Path.Combine(root, "st1.json"));
        st.Data["walk"] = new JsonObject { ["at"] = clock.T - 3600 };
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

    // ---- the week's full walk: every page, past a page with nothing new, once a week; the queued maps the check had found gone come back; the
    // same door, ledger and caps; a walk cut short is not the week's
    internal static void WalkChecks(string root)
    {
        const double Week = 7 * 86400;
        var (led, clock) = W.Ledger(Path.Combine(root, "ledger-walk.json"));
        var (api, sent, ft) = ApiWith(led, clock, [Page([10, 9, 8], "c2"), Page([7, 6, 5], "c3"), Page([4, 3, 2], "c4"), Page([1], "")]);
        var st = new SyncState(Path.Combine(root, "st-walk.json"));
        st.Items["5"] = O("""{"status": "queued", "why": "new", "ws": "removed", "title": "gone 5", "tags": ["Armsrace"], "app": 730, "first": "2026-11-01"}""");
        st.Items["3"] = O("""{"status": "published", "why": "new", "ws": "ok", "title": "seen before", "tags": ["Armsrace"], "app": 730}""");
        var cfg = Cfg();
        cfg.WebApiKey = Key;
        var known = new Dictionary<string, string> { ["7"] = "listed", ["6"] = "listed" };
        var t0 = clock.T;
        var d = Discovery.Discover(api, cfg, known, st, C(clock).Day(), clock.T + 3600, () => clock.T);
        Check($"walk: none done yet - every page, on past page 2 with nothing new, to the last ({Node.Str(d["pages"])} pages, {Node.Str(d["stop"])})",
              Node.Int(d["pages"]) == 4 && Node.StrList(d["new"]) is ["10", "9", "8", "4", "2", "1"] && Node.Str(d["stop"]) == "the last page" && Node.Str(d["walk"]) == "whole");
        Check("walk: a queued map the check had found gone, public on a page, is back - its verdict ok, queued again; one seen before and ok is left as it was",
              Node.StrList(d["back"]) is ["5"] && Node.Str(st.Item("5")!["ws"]) == "ok" && Node.Str(st.Item("5")!["status"]) == "queued" && Node.Str(st.Item("5")!["title"]) == "map 5"
              && Node.Str(st.Item("3")!["title"]) == "seen before" && Node.Str(st.Item("3")!["status"]) == "published");
        var rec = Node.Obj(st.Data["walk"]);
        Check($"walk: the state records it - when it started, its pages, what it found ({Node.Compact(rec)})",
              Node.Float(rec["at"]) == t0 && Node.Int(rec["pages"]) == 4 && Node.Int(rec["new"]) == 6 && Node.Int(rec["back"]) == 1 && Node.Str(rec["stop"]) == "the last page");
        Check($"walk: through the one door and the ledger - 4 calls, 2 s apart, each booked ({Show(ft.Sleeps)})",
              sent.Count == 4 && ft.Sleeps.SequenceEqual([2.0, 2.0, 2.0]) && led.Count(C(clock).Day()) == 4);
        (api, sent, _) = ApiWith(led, clock, [Page([11, 10], "c2"), Page([9], "c3"), Page([8], "")]);
        d = Discovery.Discover(api, cfg, known, st, C(clock).Day(), clock.T + 3600, () => clock.T);
        Check("walk: not again within the week - the next discovery stops at the first page with nothing new, as every default run",
              Node.Int(d["pages"]) == 2 && Node.StrList(d["new"]) is ["11"] && Node.Str(d["stop"]) == "page 2 held nothing new" && d["walk"] is null && sent.Count == 2);
        clock.T = t0 + Week;
        (api, sent, _) = ApiWith(led, clock, [Page([11, 10], "c2"), Page([12], "")]);
        d = Discovery.Discover(api, cfg, known, st, C(clock).Day(), clock.T + 3600, () => clock.T);
        Check("walk: a week after the last it walks again - past page 1 with nothing new",
              Node.Int(d["pages"]) == 2 && Node.StrList(d["new"]) is ["12"] && Node.Str(d["walk"]) == "whole" && Node.Float(Node.Get(st.Data["walk"], "at")) == t0 + Week);
        // cut short: by the clock, or the run's cap of Web API calls - what it read is remembered, the walk is not the week's, and the next walks
        var none = new Dictionary<string, string>();
        var st2 = new SyncState(Path.Combine(root, "st-walk2.json"));
        (api, sent, _) = ApiWith(led, clock, [Page([20], "c2"), Page([21], "c3"), Page([22], "c4"), Page([23], "c5"), Page([24], "")]);
        d = Discovery.Discover(api, cfg, none, st2, C(clock).Day(), clock.T + 3, () => clock.T);
        Check($"walk: cut short by the run's clock - what it read is remembered, but it is not the week's ({Node.Str(d["stop"])})",
              Node.Str(d["walk"]) == "cut short" && Node.Str(d["stop"]) == "the run's deadline" && st2.Data["walk"] is null && Node.StrList(d["new"]) is ["20", "21", "22"]);
        (api, sent, _) = ApiWith(led, clock, [Page([30], "c2"), Page([31], "c3"), Page([32], "c4"), Page([33], "")]);
        api.RunCap = 2;
        try
        {
            Discovery.Discover(api, cfg, new Dictionary<string, string> { ["31"] = "listed" }, st2, C(clock).Day(), clock.T + 3600, () => clock.T);
            Check("walk: ... so the next discovery walks again, held by the run's cap of Web API calls like any other step - its 3rd call of 2 refused", false);
        }
        catch (StopApiException e)
        {
            Check("walk: ... so the next discovery walks again, held by the run's cap of Web API calls like any other step - its 3rd call of 2 refused",
                  e.Message.Contains("this run's cap of 2") && sent.Count == 2 && st2.Data["walk"] is null && st2.Item("30") is not null);
        }
        cfg.MaxPages = 2;
        (api, sent, _) = ApiWith(led, clock, [Page([40], "c2"), Page([41], "c3"), Page([42], "")]);
        d = Discovery.Discover(api, cfg, none, st2, C(clock).Day(), clock.T + 3600, () => clock.T);
        Check("walk: sync.maxPages bounds it; one that read as many pages as the settings allow is the week's",
              Node.Int(d["pages"]) == 2 && Node.Str(d["stop"]).Contains("sync.maxPages") && Node.Str(d["walk"]) == "whole" && Node.Int(Node.Get(st2.Data["walk"], "pages")) == 2);
        var lowered = Cfg();
        lowered.WebApiKey = Key;
        lowered.Lower(O("{\"max_pages\": 1}"));
        var st3 = new SyncState(Path.Combine(root, "st-walk3.json"));
        (api, sent, _) = ApiWith(led, clock, [Page([50], "c2"), Page([51], "")]);
        d = Discovery.Discover(api, lowered, none, st3, C(clock).Day(), clock.T + 3600, () => clock.T);
        Check("walk: a run-now's max_pages takes none - its pages may not reach every page, and nothing is recorded",
              Node.Int(d["pages"]) == 1 && d["walk"] is null && st3.Data["walk"] is null);
        var same = Cfg();
        same.WebApiKey = Key;
        same.Lower(O("{\"max_pages\": 5000, \"batch\": 3}"));
        (api, sent, _) = ApiWith(led, clock, [Page([50], "c2"), Page([51], "")]);
        d = Discovery.Discover(api, same, none, st3, C(clock).Day(), clock.T + 3600, () => clock.T);
        Check("walk: a run-now that lowers nothing of the pages walks when it is due", Node.Int(d["pages"]) == 2 && Node.Str(d["walk"]) == "whole");
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
