// The sync's self-test: the limits: the Web API ledger, the door, the stops, the downloads' book.
using System.Text.Json.Nodes;
using OverStrike2.Core;
using static OverStrike2.Sync.SelfTest.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>The limits: the Web API ledger's rolling 24-hour cap and its day, the program's one door and its gap, the stops on 429, 403,
/// 401, server errors and network failures, the key, and the day's downloads with the bug brake.</summary>
static class LimitTests
{
    // ---- the ledger: rolling 24 h, the day, persisted before every call
    internal static void LedgerChecks(string root)
    {
        var lp = Path.Combine(root, "ledger1.json");
        var (led, clock) = W.Ledger(lp);
        var today = C(clock).Day();
        for (var i = 0; i < 600; i++)
            led.Reserve(today, "GetPublishedFileDetails");
        Check("ledger: no cap per day but the 24 h one: call 601 of a day goes", led.Reserve(today, "QueryFiles") >= 0);
        var file = Read(lp)!;
        Check("ledger: every reserved call is on disk, counted under its day", Node.Arr(file["calls"]).Count == 601 && Node.Eq(file["days"], new JsonObject { [today] = 601 }));
        var (led2, _) = W.Ledger(lp, t: clock.T);
        Check("ledger: the day's count survives a restart (a new process reads the file)", led2.Count(today) == 601);
        var (capped, sentC, _) = ApiWith(W.Ledger(Path.Combine(root, "ledger-run.json")).Ledger, new FakeClock(1793500000.0), Many(J("{\"response\": {}}"), 5));
        capped.RunCap = 3;
        for (var i = 0; i < 3; i++)
            capped.Call("GetPublishedFileDetails", SteamApi.DetailsUrl, data: [("itemcount", "1")]);
        try
        {
            capped.Call("GetPublishedFileDetails", SteamApi.DetailsUrl, data: [("itemcount", "1")]);
            Check("api: the run's cap - its 4th call of 3 is refused, and nothing is sent", false);
        }
        catch (StopApiException e)
        {
            Check("api: the run's cap - its 4th call of 3 is refused, and nothing is sent", e.Message.Contains("this run's cap of 3") && sentC.Count == 3);
        }
        // the rolling day: 10,000 across days
        lp = Path.Combine(root, "ledger2.json");
        (led, clock) = W.Ledger(lp);
        led.Data!["calls"] = new JsonArray(Enumerable.Range(0, 9999).Select(i => (JsonNode?)new JsonArray(clock.T - 3600 - i * 0.5, "QueryFiles", 200)).ToArray());
        led.Data["days"] = new JsonObject { ["2026-10-31"] = 9999 };
        led.Save();
        led.Reserve(C(clock).Day(), "QueryFiles");
        try
        {
            led.Reserve(C(clock).Day(), "QueryFiles");
            Check("ledger: the 10,001st call within 24 h is refused, whatever day it is", false);
        }
        catch (StopApiException e)
        {
            Check("ledger: the 10,001st call within 24 h is refused, whatever day it is", e.Message.Contains("10000 Web API calls in 24 hours"));
        }
        clock.T += 86400;
        led.Reserve(C(clock).Day(), "QueryFiles");
        Check("ledger: 24 h later the old calls have rolled off (a call exactly 24 h old included)", led.Last24h() == 1);
        // an unreadable ledger means no calls at all
        lp = Path.Combine(root, "ledger3.json");
        File.WriteAllText(lp, "{not json");
        (led, clock) = W.Ledger(lp);
        try
        {
            led.Reserve(C(clock).Day(), "QueryFiles");
            Check("ledger: an unreadable ledger allows no call", false);
        }
        catch (StopApiException e)
        {
            Check("ledger: an unreadable ledger allows no call", e.Message.Contains("does not parse"));
        }
    }

    // ---- the API door: the gap, the stops, the key
    internal static void Door(string root)
    {
        var (led, clock) = W.Ledger(Path.Combine(root, "ledger4.json"));
        var (api, sent, ft) = ApiWith(led, clock, Many(J("{\"response\": {\"publishedfiledetails\": []}}"), 5));
        for (var i = 0; i < 5; i++)
            api.Details(["1", "2"]);
        Check("api: 5 calls made one by one", sent.Count == 5);
        Check("api: at least 2 s between calls (4 waits of 2 s on a frozen clock)", ft.Sleeps.SequenceEqual([2.0, 2.0, 2.0, 2.0]));
        Check("api: an honest User-Agent naming the tool", sent.All(r => r.Headers["User-Agent"].StartsWith("os2-sync/", StringComparison.Ordinal)));
        Check("api: GetPublishedFileDetails is a POST without the key", sent.All(r => r.Method == "POST" && r.Body is not null && !r.Url.Contains(Key)));
        foreach (var code in new[] { 429, 403, 401 })
        {
            var lp = Path.Combine(root, $"ledger-{code}.json");
            (led, clock) = W.Ledger(lp);
            (api, sent, ft) = ApiWith(led, clock, [code, J("{\"response\": {}}")]);
            try
            {
                api.Details(["1"]);
                Check($"api: HTTP {code} stops the day", false);
            }
            catch (StopApiException e)
            {
                Check($"api: HTTP {code} stops the day", e.Message.Contains($"HTTP {code}"));
            }
            Check($"api: HTTP {code} is sent once, never retried", sent.Count == 1);
            try
            {
                api.Details(["1"]);
                Check($"api: after HTTP {code} no further call goes out", false);
            }
            catch (StopApiException)
            {
                Check($"api: after HTTP {code} no further call goes out", sent.Count == 1);
            }
            var (led2, _) = W.Ledger(lp, t: clock.T);
            try
            {
                led2.Reserve(C(clock).Day(), "QueryFiles");
                Check($"api: the HTTP {code} stop is in the ledger, so a restart cannot resume it today", false);
            }
            catch (StopApiException e)
            {
                Check($"api: the HTTP {code} stop is in the ledger, so a restart cannot resume it today", e.Message.Contains("stopped earlier today"));
            }
        }
        // 5xx: three in a row stop (the retries count)
        (led, clock) = W.Ledger(Path.Combine(root, "ledger-5xx.json"));
        (api, sent, ft) = ApiWith(led, clock, [502, 503, 500, J("{\"response\": {}}")]);
        try
        {
            api.Details(["1"]);
            Check("api: 3 server errors in a row stop the day", false);
        }
        catch (StopApiException e)
        {
            Check("api: 3 server errors in a row stop the day", e.Message.Contains("server errors") && sent.Count == 3);
        }
        Check("api: the 5xx retries wait 30 s and 90 s", ft.Sleeps.Take(1).SequenceEqual([30.0]) && ft.Sleeps.Contains(90));
        // 5xx: a single one is retried and the run goes on
        (led, clock) = W.Ledger(Path.Combine(root, "ledger-5xx1.json"));
        (api, sent, ft) = ApiWith(led, clock, [503, J("{\"response\": {\"publishedfiledetails\": [{\"publishedfileid\": \"7\", \"result\": 1}]}}")]);
        var got = api.Details(["7"]);
        Check("api: one 503 is retried and the answer used", got.ContainsKey("7") && sent.Count == 2 && led.Stopped(C(clock).Day()) is null);
        // 5 in a run stop, even not in a row
        (led, clock) = W.Ledger(Path.Combine(root, "ledger-5xx5.json"));
        var answers = new List<object>();
        for (var i = 0; i < 5; i++)
            answers.AddRange([500, J("{\"response\": {}}")]);
        (api, sent, ft) = ApiWith(led, clock, answers);
        string? stopped = null;
        for (var i = 0; i < 6; i++)
        {
            try
            {
                api.Details(["1"]);
            }
            catch (StopApiException e)
            {
                stopped = e.Message;
                break;
            }
        }
        Check("api: 5 server errors in a run stop it", stopped is not null && stopped.Contains("5 server errors"));
        // network failures
        (led, clock) = W.Ledger(Path.Combine(root, "ledger-net.json"));
        (api, sent, ft) = ApiWith(led, clock, Many("net", 6));
        stopped = null;
        for (var i = 0; i < 3; i++)
        {
            try
            {
                api.Details(["1"]);
            }
            catch (StopApiException e)
            {
                stopped = e.Message;
                break;
            }
            catch (ApiErrorException)
            {
            }
        }
        Check("api: 5 network failures stop the day", stopped is not null && stopped.Contains("5 network failures"));
        // other 4xx: given up at once, not retried, the run goes on
        (led, clock) = W.Ledger(Path.Combine(root, "ledger-400.json"));
        (api, sent, ft) = ApiWith(led, clock, [400, J("{\"response\": {}}")]);
        try
        {
            api.Details(["1"]);
            Check("api: HTTP 400 is given up without a retry", false);
        }
        catch (StopApiException)
        {
            Check("api: HTTP 400 is given up without a retry", false);
        }
        catch (ApiErrorException)
        {
            Check("api: HTTP 400 is given up without a retry", sent.Count == 1 && led.Stopped(C(clock).Day()) is null);
        }
        // the key: only on the keyed calls, never in a message
        (led, clock) = W.Ledger(Path.Combine(root, "ledger-key.json"));
        (api, sent, ft) = ApiWith(led, clock, [J("{\"response\": {\"total\": 0, \"publishedfiledetails\": []}}")]);
        api.QueryFiles(["Armsrace", "Deathmatch", "Wingman"], "*", 100);
        var q = sent[0].Url;
        Check("api: QueryFiles carries the key, app 730, the three tags, newest first, 100 a page, the cursor",
              q.Contains("key=" + Key) && q.Contains("appid=730") && q.Contains("requiredtags%5B2%5D=Wingman") && q.Contains("query_type=1")
              && q.Contains("numperpage=100") && q.Contains("cursor=%2A") && q.Contains("match_all_tags=false"));
        Check("api: the redactor hides the key", !api.Redact($"url ...key={Key}&x").Contains(Key));
        var (api2, _, _) = ApiWith(led, clock, [], key: null);
        try
        {
            api2.QueryFiles(["Armsrace"], "*", 100);
            Check("api: no key, no QueryFiles", false);
        }
        catch (StopApiException e)
        {
            Check("api: no key, no QueryFiles", e.Message.Contains("no Web API key"));
        }
        // the key now comes through the settings file (sync.webApiKey, "file:PATH" by default): a missing or malformed file is no key
        static string KeyFrom(string path) => SyncConfig.From(Settings.Parse(new JsonObject { ["schema"] = 1, ["sync"] = new JsonObject { ["webApiKey"] = "file:" + path } }.ToJsonString(),
                                                                             Part.Sync, _ => null), _ => null).WebApiKey;
        Check("key: a malformed secret file reads as no key", KeyFrom(Path.Combine(root, "nokey")) == "");
        File.WriteAllText(Path.Combine(root, "key.txt"), Key + "\n");
        Check("key: the secret file's key is read, whitespace stripped", KeyFrom(Path.Combine(root, "key.txt")) == Key);
    }

    // ---- downloads: the day's items and volume, persisted
    internal static void Downloads(string root)
    {
        var st = new SyncState(Path.Combine(root, "st3.json"));
        var b = new DownloadBudget(st, "2026-11-01");
        for (var i = 0; i < 3000; i++)
            b.Spend(1L << 30, (10000 + i).ToString());
        Check("downloads: no daily cap - after 3,000 downloads and 3,000 GB in a day the next is allowed",
              b.Allows("1").Ok && b.Items == 3000 && b.Bytes == 3000L << 30);
        b.Spend(10, "7");
        b.Spend(10, "7");
        Check("downloads: the bug brake - a map downloaded twice today is not downloaded a third time today, another one is",
              !b.Allows("7").Ok && b.Allows("7").Why.Contains("the bug brake") && b.Allows("8").Ok);
        st.Save();
        var st2 = new SyncState(Path.Combine(root, "st3.json")).Load();
        Check("downloads: the book and the brake survive a restart", !new DownloadBudget(st2, "2026-11-01").Allows("7").Ok);
        Check("downloads: the next day, the map may come again", new DownloadBudget(st2, "2026-11-02").Allows("7").Ok);
        new DownloadBudget(st2, "z").RefuseForToday("Rate Limit Exceeded");
        st2.Save();
        var st5 = new SyncState(Path.Combine(root, "st3.json")).Load();
        Check("downloads: a rate limit ends the day's downloads, also for a later run that day",
              !new DownloadBudget(st5, "z").Allows().Ok && new DownloadBudget(st5, "z").Allows("1").Why.Contains("Rate Limit Exceeded"));
        Check("downloads: the next day starts clean", new DownloadBudget(st5, "zz").Allows().Ok);
        Check("downloads: what stops them", SteamCmdWords.StopRx().IsMatch("Rate Limit Exceeded") && SteamCmdWords.StopRx().IsMatch("Too many requests")
              && !SteamCmdWords.StopRx().IsMatch("Access Denied") && SteamCmdWords.AccessDeniedRx().IsMatch("Access Denied"));
    }

    // ---- one door per program: the gap holds from one run's last call to the next run's first; a call without its key reserves nothing
    internal static void DoorAndLedger(string root)
    {
        var (led, clock) = W.Ledger(Path.Combine(root, "door-ledger.json"));
        var door = new WebApiDoor(2.0, clock);
        var sent = new List<ApiRequest>();
        var day = C(clock).Day();
        var a = new SteamApi(led, day, door, Key, Opener(Many(J("{\"response\": {}}"), 4), sent), s => clock.Sleep(TimeSpan.FromSeconds(s)), Log.Line);
        a.Details(["1"]);
        var b = new SteamApi(led, day, door, Key, Opener(Many(J("{\"response\": {}}"), 4), sent), s => clock.Sleep(TimeSpan.FromSeconds(s)), Log.Line);
        b.Details(["2"]);
        Check("api: one door for the program - a new run's first call waits out the gap after the last run's", clock.Sleeps.SequenceEqual([2.0]) && sent.Count == 2);
        var (led2, clock2) = W.Ledger(Path.Combine(root, "nokey-ledger.json"));
        var (api, sent2, _) = ApiWith(led2, clock2, [], key: null);
        try
        {
            api.Votes(["1"]);
        }
        catch (StopApiException)
        {
        }
        Check("api: a keyed call without the key is neither sent nor counted in the ledger", sent2.Count == 0 && Node.Arr(led2.Data!["calls"]).Count == 0 && led2.Count(C(clock2).Day()) == 0);
    }
}
