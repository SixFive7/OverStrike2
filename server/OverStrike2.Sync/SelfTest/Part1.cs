using System.Text;
using System.Text.Json.Nodes;
using OverStrike2.Core;
using static OverStrike2.Sync.SelfTest.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>The first part: the limits (the Web API ledger's caps, the 2-second gap, the stops on 429 / 403 /
/// 5xx / network failures), the configuration's clamps, discovery's stop rule, the check's verdicts, the downloads' book, the scan's advert
/// rules and its LZ4 reader, the verdicts, the publish plan, the mail, the backlog and the merge rule.</summary>
static class Part1
{
    public const string N = "2026-11-01";

    static Settings ReadSettings(string json) => Settings.Parse(json, Part.Sync, _ => null, path => throw new FileNotFoundException(path));

    public static void Run()
    {
        var root = T.Root;
        Directory.CreateDirectory(root);
        (State, Work, Data, GameRoot) = (Path.Combine(root, "state"), Path.Combine(root, "work"), Path.Combine(root, "data"), Path.Combine(root, "game-none"));
        Config();
        LedgerChecks(root);
        Door(root);
        DiscoveryChecks(root);
        CheckStep(root);
        Downloads(root);
        ScanChecks(root);
        VerdictChecks();
        PublishPlan(root);
        MailAndBacklog();
        MergeRule();
    }

    // ---- configuration: the limits can only be lowered
    static void Config()
    {
        var s = ReadSettings("""{ "schema": 1, "sync": { "apiDayCap": 20000, "apiRunCap": 99999, "downloadMax": 8000, "downloadMaxMb": 999999, "downloadPauseSeconds": 0.1 }, "steam": { "apiGapSeconds": 0.5 } }""");
        var c = SyncConfig.From(s, _ => null);
        Check("config: a day cap above 10,000 is clamped to 10,000", c.ApiDayCap == 10000);
        Check("config: the run's cap never exceeds the day cap", c.ApiRunCap == 10000);
        Check("config: the gap never drops under 2 s", c.ApiGap == 2.0);
        // No such property, and the settings file knows no such key (a warning)
        Check("config: no daily download cap any more: SYNC_DOWNLOAD_MAX and SYNC_DOWNLOAD_MAX_MB mean nothing",
              typeof(SyncConfig).GetProperties().All(p => !p.Name.StartsWith("DownloadMax", StringComparison.Ordinal) && p.Name != "DownloadBytes")
              && s.Warnings.Any(w => w.StartsWith("sync.downloadMax:", StringComparison.Ordinal)) && s.Warnings.Any(w => w.StartsWith("sync.downloadMaxMb:", StringComparison.Ordinal)));
        Check("config: the pause between downloads never drops under 2 s", c.DownloadPause == 2.0);
        s = ReadSettings("""{ "schema": 1, "sync": { "apiNightCap": 50 } }""");
        c = SyncConfig.From(s, _ => null);
        Check("config: defaults are 500 calls a run, 10,000 in 24 h, 2 s - SYNC_API_NIGHT_CAP, a name of before, means nothing",
              (c.ApiRunCap, c.ApiDayCap, c.ApiGap) == (500, 10000, 2.0) && s.Warnings.Any(w => w.StartsWith("sync.apiNightCap:", StringComparison.Ordinal)));
        Check("config: no time windows - the daily restart at 07:00 to keep clear of (no prefetch any more), runs of 3 h, the API every 4 h",
              new[] { "DayStart", "Start", "PrefetchAt", "WatcherState" }.All(n => typeof(SyncConfig).GetProperty(n) is null)
              && (c.DailyRestart, c.RunHours, c.ApiEvery) == (new TimeOfDay(7, 0), 3, 4 * 3600.0));
        c.Lower(O("""{"api_run_cap": 50, "batch": 5, "publish": false, "api": true}"""));
        Check("config: run-now lowers a run's limits, and may ask for the API steps", (c.ApiRunCap, c.Batch, c.Publish, c.ForceApi) == (50, 5, false, true));
        c.Lower(O("""{"api_run_cap": 5000, "batch": 1000}"""));
        Check("config: run-now cannot raise them", (c.ApiRunCap, c.Batch) == (50, 5));
        c.Lower(O("""{"api": false, "only_ids": [11, "12", "x"]}"""));
        Check("config: run-now may leave the API steps out, and limit the run to some maps", c.SkipApi && c.OnlyIds is ["11", "12"]);
        Check("ledger: the hard day cap cannot be raised by a caller", new ApiLedger("x", 50000, C(new FakeClock(0))).DayCap == 10000);
    }

    // ---- the ledger: rolling 24 h, the day, persisted before every call
    static void LedgerChecks(string root)
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
    static void Door(string root)
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
    static void DiscoveryChecks(string root)
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
        Check("discovery: SYNC_MAX_PAGES bounds it", Node.Int(d["pages"]) == 1 && Node.Str(d["stop"]).Contains("sync.maxPages"));
    }

    // ---- the check: removed, changed, the 10 % guard
    static void CheckStep(string root)
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
        Check("check: a map updated since SYNC_CHANGED_SINCE is changed at the first check", j.Changed is ["104"]);
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

    // ---- downloads: the day's items and volume, persisted
    static void Downloads(string root)
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

    /// <summary>A valid LZ4 block of literals only: compressor stand-in for the test.</summary>
    public static byte[] Lz4Literals(byte[] data)
    {
        var out_ = new List<byte>();
        var lit = data.Length;
        out_.Add((byte)((lit >= 15 ? 15 : lit) << 4));
        if (lit >= 15)
        {
            var n = lit - 15;
            while (n >= 255)
            {
                out_.Add(255);
                n -= 255;
            }
            out_.Add((byte)n);
        }
        out_.AddRange(data);
        return out_.ToArray();
    }

    static byte[] U32(long v) => BitConverter.GetBytes((uint)v);
    static byte[] U16(int v) => BitConverter.GetBytes((ushort)v);

    /// <summary>A VPK v1 holding `files` {path: bytes}, data inline (archive 0x7fff).</summary>
    public static byte[] VpkOf(params (string Path, byte[] Body)[] files)
    {
        var byExt = new List<(string Ext, List<(string Folder, List<(string Stem, byte[] Body)> Items)> Folders)>();
        foreach (var (path, body) in files)
        {
            var folder = path[..path.LastIndexOf('/')];
            var name = path[(path.LastIndexOf('/') + 1)..];
            var stem = name[..name.LastIndexOf('.')];
            var ext = name[(name.LastIndexOf('.') + 1)..];
            var e = byExt.FindIndex(x => x.Ext == ext);
            if (e < 0)
            {
                byExt.Add((ext, []));
                e = byExt.Count - 1;
            }
            var folders = byExt[e].Folders;
            var f = folders.FindIndex(x => x.Folder == folder);
            if (f < 0)
            {
                folders.Add((folder, []));
                f = folders.Count - 1;
            }
            folders[f].Items.Add((stem, body));
        }
        var tree = new List<byte>();
        var data = new List<byte>();
        foreach (var (ext, folders) in byExt)
        {
            tree.AddRange(Encoding.UTF8.GetBytes(ext));
            tree.Add(0);
            foreach (var (folder, items) in folders)
            {
                tree.AddRange(Encoding.UTF8.GetBytes(folder));
                tree.Add(0);
                foreach (var (stem, body) in items)
                {
                    tree.AddRange(Encoding.UTF8.GetBytes(stem));
                    tree.Add(0);
                    tree.AddRange(U32(0));
                    tree.AddRange(U16(0));
                    tree.AddRange(U16(0x7FFF));
                    tree.AddRange(U32(data.Count));
                    tree.AddRange(U32(body.Length));
                    tree.AddRange(U16(0xFFFF));
                    data.AddRange(body);
                }
                tree.Add(0);
            }
            tree.Add(0);
        }
        tree.Add(0);
        return [.. U32(0x55AA1234), .. U32(1), .. U32(tree.Count), .. tree, .. data];
    }

    public static byte[] Kv3Resource(byte[] text)
    {
        var body = new byte[120];
        "\u00053VK"u8.CopyTo(body);
        U32(1).CopyTo(body, 20);
        var packed = Lz4Literals(text);
        U32(text.Length).CopyTo(body, 72);
        U32(packed.Length).CopyTo(body, 76);
        var kv3 = body.Concat(packed).ToArray();
        var head = new byte[16];
        U32(8).CopyTo(head, 8);
        U32(1).CopyTo(head, 12);
        return [.. head, .. "DATA"u8, .. U32(8), .. U32(kv3.Length), .. kv3];
    }

    // ---- the scan: the advert rules
    static void ScanChecks(string root)
    {
        var cmds = Scan.CommandsIn("say \"Hey guys! Want to open some free cases? Use code PICHET on Clash.gg for 3 FREE cases\"\0m_x"u8.ToArray());
        Check("scan: a Clash.gg promo is a gambling ad", Scan.AdvertOf(cmds, false).Category == "gambling ad");
        Check("scan: 'say connect <ip>' is a chat advert", Scan.AdvertOf(Scan.CommandsIn("say connect 203.0.113.6:27015"u8.ToArray()), false).Category == "chat advert");
        Check("scan: a bind to connect with a client-command entity is a client connect",
              Scan.AdvertOf(Scan.CommandsIn("bind w \"connect 203.0.113.52:27215; bind w +forward\""u8.ToArray()), true).Category == "client connect");
        Check("scan: a bind to connect run on the server only is not listed",
              Scan.AdvertOf(Scan.CommandsIn("bind w \"connect 203.0.113.52:27215\""u8.ToArray()), false).Category == "server connect");
        Check("scan: a bare connect with a client-command entity is a client connect", Scan.AdvertOf(Scan.CommandsIn("connect 203.0.113.4:27015"u8.ToArray()), true).Category == "client connect");
        Check("scan: a bare server-side connect is not listed (CS2 refuses it)", Scan.AdvertOf(Scan.CommandsIn("connect 203.0.113.4:27015"u8.ToArray()), false).Category == "server connect");
        Check("scan: an ordinary map has none", Scan.AdvertOf(Scan.CommandsIn("say \"Leave a like if you enjoyed the map !\""u8.ToArray()), false) == (null, null));
        var blob = Enumerable.Repeat("say connect 203.0.113.4:27015\0point_clientcommand\0"u8.ToArray(), 3).SelectMany(x => x).ToArray();
        Check("scan: the LZ4 reader decodes a literal block", Scan.Lz4Block(Lz4Literals(blob), blob.Length).SequenceEqual(blob));
        byte[] match = [0x41, .. "abcd"u8, 4, 0];               // 4 literals, then a match of 5 at offset 4
        Check("scan: the LZ4 reader copies an overlapping match", Scan.Lz4Block(match, 64).SequenceEqual("abcdabcda"u8.ToArray()));
        // a fake KV3 v5 entity lump inside a resource, inside a map VPK, inside the item's VPK
        var resource = Kv3Resource(blob);
        Check("scan: kv3_strings finds the commands in a v5 LZ4 entity lump", Encoding.UTF8.GetString(Scan.Kv3Strings(resource)).Contains("say connect 203.0.113.4"));
        var inner = VpkOf(("maps/adtest/entities/default_ents.vents_c", Kv3Resource("connect 203.0.113.4:27015\0point_clientcommand\0"u8.ToArray())));
        var itemDir = Path.Combine(root, "item1");
        Directory.CreateDirectory(itemDir);
        File.WriteAllBytes(Path.Combine(itemDir, "1_dir.vpk"), VpkOf(("maps/adtest.vpk", inner), ("materials/x.vmat_c", "m"u8.ToArray())));
        var s = Scan.Item(itemDir);
        Check("scan: a map VPK is found and named", Node.Str(s["verdict"]) == "map" && Node.StrList(s["maps"]) is ["adtest"]);
        Check("scan: its entity lump's connect with a client-command entity makes it a client connect", Node.Str(s["advert"]) == "client connect");
        var itemDir2 = Path.Combine(root, "item1b");
        Directory.CreateDirectory(itemDir2);
        File.WriteAllBytes(Path.Combine(itemDir2, "1b_dir.vpk"), VpkOf(("maps/adtest.vpk", VpkOf(("maps/adtest/entities/default_ents.vents_c", resource)))));
        Check("scan: a 'say connect' in the entity lump is a chat advert", Node.Str(Scan.Item(itemDir2)["advert"]) == "chat advert");
        var packDir = Path.Combine(root, "item2");
        Directory.CreateDirectory(packDir);
        File.WriteAllBytes(Path.Combine(packDir, "2_dir.vpk"), VpkOf(("models/a/b.vmdl_c", "x"u8.ToArray()), ("models/a/c.vmdl_c", "y"u8.ToArray())));
        Check("scan: models and no map is a pack", Node.Str(Scan.Item(packDir)["verdict"]) == "pack");
        var emptyDir = Path.Combine(root, "item3");
        Directory.CreateDirectory(emptyDir);
        File.WriteAllBytes(Path.Combine(emptyDir, "3_dir.vpk"), VpkOf(("materials/a/b.vmat_c", "x"u8.ToArray())));
        Check("scan: no map and no models is empty", Node.Str(Scan.Item(emptyDir)["verdict"]) == "empty");
        var legacyDir = Path.Combine(root, "item4");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllBytes(Path.Combine(legacyDir, "4_legacy.bin"), "x"u8.ToArray());
        Check("scan: only a _legacy.bin is a CS:GO-era upload", Node.Str(Scan.Item(legacyDir)["verdict"]) == "legacy");
    }

    static (string Kind, JsonObject Detail) Fv(string rec) => Verdicts.Final(O(rec), N);

    // ---- verdicts
    static void VerdictChecks()
    {
        const string ok = "\"scan\": {\"verdict\": \"map\", \"maps\": [\"x\"], \"advert\": null}";
        Check("verdict: a pass", Fv($"{{{ok}, \"loads\": [{{\"verdict\": \"pass\", \"level\": \"x\", \"mode\": \"armsrace\"}}]}}").Kind == "pass");
        Check("verdict: a pass after a crash is a (flaky) pass", Node.Truthy(Fv($"{{{ok}, \"loads\": [{{\"verdict\": \"crash\"}}, {{\"verdict\": \"pass\", \"level\": \"x\"}}]}}").Detail["flaky"]));
        Check("verdict: one crash is not a verdict yet", Fv($"{{{ok}, \"loads\": [{{\"verdict\": \"crash\"}}]}}").Kind == "pending");
        var (k, d) = Fv($"{{{ok}, \"loads\": [{{\"verdict\": \"crash\"}}, {{\"verdict\": \"crash\"}}, {{\"verdict\": \"crash\"}}]}}");
        Check("verdict: 3 of 3 crashes is a crasher with crashes 3", k == "crasher" && Node.Int(d["crashes"]) == 3 && Node.Str(d["evidence"]).Contains("all 3"));
        (k, d) = Fv($"{{{ok}, \"loads\": [{{\"verdict\": \"freeze\"}}, {{\"verdict\": \"rejected\"}}, {{\"verdict\": \"rejected\"}}]}}");
        Check("verdict: 1 freeze of 3 is a crasher with crashes 1", k == "crasher" && Node.Int(d["crashes"]) == 1 && Node.Str(d["evidence"]).Contains("1 of 3"));
        Check("verdict: 3 error maps make a non-map", Fv($"{{{ok}, \"loads\": [{{\"verdict\": \"errormap\"}}, {{\"verdict\": \"errormap\"}}, {{\"verdict\": \"errormap\"}}]}}").Kind == "nonmap");
        var stub = "{\"verdict\": \"rejected\", \"level\": \"de_dust2\"}";
        Check("verdict: a stock map instead of its own is a stub", Node.Str(Fv($"{{{ok}, \"loads\": [{stub}, {stub}, {stub}]}}").Detail["kind"]) == "stub");
        var abort = "{\"verdict\": \"abort\"}";
        Check("verdict: an aborted load does not count", Fv($"{{{ok}, \"loads\": [{abort}, {abort}, {abort}, {abort}, {abort}]}}").Kind == "pending");
        Check("verdict: a pack never loads", Fv("{\"scan\": {\"verdict\": \"pack\", \"models\": 3}}").Kind == "nonmap");
        (k, d) = Fv("{\"scan\": {\"verdict\": \"map\", \"maps\": [\"x\"], \"advert\": \"chat advert\", \"advert_sample\": \"say connect 203.0.113.4\"}, \"loads\": [{\"verdict\": \"pass\"}]}");
        Check("verdict: a loading map with a chat advert goes on broken.json", k == "advert" && Node.Str(d["reason"]) == "advertises another server");
        (k, _) = Fv("{\"scan\": {\"verdict\": \"map\", \"maps\": [\"x\"], \"advert\": \"server connect\"}, \"loads\": [{\"verdict\": \"pass\", \"level\": \"x\"}]}");
        Check("verdict: a server-side connect alone is still a pass", k == "pass");
        (k, _) = Fv($"{{{ok}, \"loads\": [{{\"verdict\": \"pass\", \"chat\": [\"connect 203.0.113.8:27015\"]}}]}}");
        Check("verdict: an advert seen in chat on a test load counts", k == "advert");
        Check("verdict: kick", Fv($"{{{ok}, \"loads\": [{{\"verdict\": \"kick\"}}]}}").Kind == "kick");
    }

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
    static void PublishPlan(string root)
    {
        const string maps = "[{\"rank\": 1, \"id\": 111, \"name\": \"a\", \"sizeMb\": 5}, {\"rank\": 2, \"id\": 222, \"name\": \"b\", \"sizeMb\": 7}]";
        const string broken = "{\"schema\": \"x\", \"maps\": [], \"packs\": [], \"removed\": [], \"blocked\": []}";
        const string search = "{\"111\": \"a 111\", \"222\": \"b 222\"}";
        var files = Files(maps, broken, "[]", "[]", search);
        var st = new SyncState(Path.Combine(root, "st4.json"));
        var details = new Dictionary<string, JsonObject> { ["111"] = O("{\"publishedfileid\": \"111\", \"result\": 9, \"tags\": [{\"tag\": \"Wingman\"}], \"subscriptions\": 9}") };
        var (texts, summary) = Verdicts.PlanPublish(files, st, N, Outcomes(), [("111", "removed")], true, details);
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
        var (texts2, _) = Verdicts.PlanPublish(files2, st, N, [], [], true, details2);
        var dm2 = A(texts2["maps-deathmatch.json"]);
        Check("publish: backfill adds a listed map that carries the tag, keeps the old entries and the official one",
              dm2.Where(e => Node.Truthy(e!["id"])).Select(e => Node.Int(e!["id"])).SequenceEqual([111L, 7L]) && Node.Eq(dm2[^1], O("{\"name\": \"de_dust2\", \"official\": true}")));
        Check("publish: nothing to write when nothing changed", Verdicts.PlanPublish(files2, st, N, [], [], false, new Dictionary<string, JsonObject>()).Texts.Count == 0);
        // blocked.json: the maps blocked for good, a file of their own - known, never a candidate, never published by a test
        var fb = Files(maps, broken, "[]", "[]", search, "[{\"id\": 902, \"name\": \"b\", \"map\": \"b\", \"reason\": \"kicks everybody\"}, 905]");
        var (tb, _) = Verdicts.PlanPublish(fb, st, N, Outcomes(), [], true, details);
        Check("blocked.json: a blocked map that passed is not appended, a blocked crasher not added to broken.json",
              A(tb["maps.json"]).All(e => Node.Int(e!["id"]) != 902) && Node.Arr(O(tb["broken.json"])["maps"]).All(e => Node.Int(e!["id"]) != 905));
        Check("blocked.json: its ids are known, as broken:blocked; without the file nothing is",
              fb.KnownIds(Cfg()).GetValueOrDefault("902") == "broken:blocked" && fb.KnownIds(Cfg()).GetValueOrDefault("905") == "broken:blocked" && !files.KnownIds(Cfg()).ContainsKey("902"));
        var se = O(texts["search.json"]);
        Check("publish: search.json gets the mode lists' new maps too, whatever their mode",
              se.ContainsKey("903") && Node.Str(se["903"]).Contains("nice") && Node.Str(se["903"]).Contains("wingman") && texts["search.json"] == ExpectSearch);
        st.Items["7"] = O("{\"title\": \"q old\", \"tags\": [\"Deathmatch\"], \"description\": \"red walls\"}");
        var files3 = Files(maps, broken, "[{\"rank\": 1, \"id\": 7, \"name\": \"q\", \"sizeMb\": 1}, {\"name\": \"de_dust2\", \"official\": true}]", "[]", search);
        var (texts3, _) = Verdicts.PlanPublish(files3, st, N, [], [], false, new Dictionary<string, JsonObject>());
        Check("publish: a map on a mode list before search.json covered it gets its text from the state, the other texts stay, an official entry gets none",
              texts3.ContainsKey("search.json") && Node.Eq(O(texts3["search.json"]), O("{\"111\": \"a 111\", \"222\": \"b 222\", \"7\": \"q 7 deathmatch red walls\"}")) && texts3.Keys.SequenceEqual(["search.json"]));
        files3.Search = O(texts3["search.json"]);
        Check("publish: ...and then there is nothing to write", Verdicts.PlanPublish(files3, st, N, [], [], false, new Dictionary<string, JsonObject>()).Texts.Count == 0);
    }

    public static JsonObject R() => O("""
        {"day": "2026-11-01", "manual": false, "api_stop": "Steam's Web API answered HTTP 429 to QueryFiles - every API step stops for the rest of the day",
         "discovery": {"skipped": "stopped"}, "check": {"asked": 10, "calls": 1}, "downloads": {"ok": 0}, "tests": {"rows": []},
         "published": {}, "limits": {"api_run": 3, "api_run_cap": 500, "api_24h": 3, "api_day_cap": 10000}}
        """);

    static JsonObject With(JsonObject r, params (string Key, JsonNode? Value)[] fields)
    {
        var c = (JsonObject)r.DeepClone();
        foreach (var (k, v) in fields)
            c[k] = v?.DeepClone();
        return c;
    }

    // ---- the mail, and the backlog: strictly most subscribed first, whatever the mode; its size and estimate in the mail
    static void MailAndBacklog()
    {
        var r = R();
        var (subject, body) = RunReport.Summary(r, Cfg());
        Check("mail: a stop is in the subject and the body", subject.Contains("STOPPED: HTTP 429") && body.Contains("STOPPED: Steam's Web API answered HTTP 429"));
        Check("mail: the limits are reported", body.Contains("Web API calls this run 3 of 500, last 24 h 3 of 10000"));
        var items = O("""
            {"1": {"tags": ["Deathmatch"], "subs": 100, "size": 10485760}, "2": {"tags": ["Deathmatch"], "subs": 90, "size": 10485760},
             "3": {"tags": ["Wingman"], "subs": 5, "size": 10485760}, "4": {"tags": ["Armsrace", "Deathmatch"], "subs": 50, "size": 10485760},
             "5": {"tags": ["Wingman", "Deathmatch"], "subs": 95, "size": 10485760}, "6": {"tags": ["Armsrace"], "subs": 1, "size": 10485760}}
            """);
        var all = items.Select(kv => kv.Key).ToList();
        Check("backlog: strictly by subscribers across the modes", Verdicts.OrderBacklog(items, all).SequenceEqual(["1", "5", "2", "4", "3", "6"]));
        var bs = Verdicts.BacklogStats(items, all);
        Check("backlog: counted per mode", Node.Int(bs["items"]) == 6 && Node.Eq(bs["by_mode"], O("{\"armsrace\": 2, \"deathmatch\": 4, \"wingman\": 2}")));
        var big = new JsonObject();
        for (var i = 0; i < 5255; i++)
            big[i.ToString()] = O("{\"tags\": [\"Wingman\"], \"subs\": 1, \"size\": 104857600}");
        var bigIds = big.Select(kv => kv.Key).ToList();
        var both = Verdicts.BacklogStats(big, bigIds);
        Check("backlog: no time windows, about 22 h a day at 31 s a map, 2,554 a day - no daily cap: 3 days for the backlog of 2026-11-01",
              Node.Float(both["hours"]) == 22 && Node.Int(both["per_day"]) == 2554 && Node.Int(both["days"]) == 3);
        Check("backlog: a pace measured counts", Node.Int(Verdicts.BacklogStats(big, bigIds, 60.0)["days"]) == 4);
        var r2 = With(r, ("api_stop", null), ("backlog", bs), ("backlog_until", " (until about 2026-11-04)"));
        var (_, body2) = RunReport.Summary(r2, Cfg());
        Check("mail: the backlog's size, per mode, and its estimate", body2.Contains("Backlog: 6 item(s) waiting") && body2.Contains("Deathmatch 4, Wingman 2, Arms Race 2")
              && body2.Contains("until about 2026-11-04") && body2.Contains("strictly by subscribers"));
        var rows = Node.Strings(Enumerable.Range(0, 60).Select(i => $"PASS     map{i} ({i}) - wingman, loads as x in 1s").Append("CRASHER  bad (1) - crashes the server"));
        var r3 = With(r, ("api_stop", null), ("tests", new JsonObject { ["rows"] = rows }),
                      ("published", new JsonObject
                      {
                          ["modes"] = new JsonObject { ["wingman"] = new JsonArray(Enumerable.Range(0, 60).Select(i => (JsonNode?)new JsonArray(i.ToString(), "m")).ToArray()) },
                          ["files"] = Node.Strings(["maps-wingman.json"]),
                      }));
        var (_, body3) = RunReport.Summary(r3, Cfg());
        Check("mail: a long run gives counts, what did not pass, a taste of what did", body3.Contains("60 PASS, 1 CRASHER.") && body3.Contains("CRASHER  bad (1)")
              && body3.Contains("and 50 more passes") && !body3.Contains("map30 "));
        Check("mail: a long mode list is counted", body3.Contains("maps-wingman.json  60 new maps"));
        var split = Enumerable.Range(0, 41).Select(i => $"PASS{(char)(0x1C + i % 4)}map{i}").ToList();
        var (_, body4) = RunReport.Summary(With(r, ("api_stop", null), ("tests", new JsonObject { ["rows"] = Node.Strings(split) })), Cfg());
        Check("mail: a row's first word ends where core's SearchText.Split ends it - at U+001C to U+001F too, as search.json's words do",
              body4.Contains("   41 PASS."));
        Check("mail: a run says when it started", RunReport.Summary(With(r, ("started_hm", "03:30")), Cfg()).Subject.Contains("(the run of 03:30)"));
        // the publish steps of a run add up
        var mp = RunReport.MergePublished(O("""{"listed": [["1", 1, "a", 5]], "modes": {"wingman": [["2", "b"]]}, "files": ["maps.json"], "reload": "x"}"""),
                                     O("""{"listed": [["3", 2, "c", 5]], "modes": {"wingman": [["4", "d"]], "deathmatch": [["5", "e"]]}, "files": ["maps.json", "search.json"], "reload": "y"}"""))!;
        Check("publish: a run's batches add up", Node.Arr(mp["listed"]).Select(x => Node.Str(x![0])).SequenceEqual(["1", "3"])
              && Node.Arr(mp["modes"]!["wingman"]).Select(x => Node.Str(x![0])).SequenceEqual(["2", "4"]) && Node.StrList(mp["files"]) is ["maps.json", "search.json"]
              && Node.Str(mp["reload"]) == "y" && Node.Eq(mp["modes"]!["deathmatch"], A("[[\"5\", \"e\"]]")));
        Check("service: a backlog waits while anything is queued or staged without a verdict",
              Service.BacklogWaits(O("{\"items\": {\"1\": {\"status\": \"queued\"}}}")) && Service.BacklogWaits(O("{\"items\": {\"1\": {\"status\": \"tested\"}}}"))
              && !Service.BacklogWaits(O("{\"items\": {\"1\": {\"status\": \"tested\", \"verdict\": \"pass\"}, \"2\": {\"status\": \"published\"}}}")));
        // nobody on: a map change shows 0 for a moment, so once somebody was seen it takes the quiet minutes
        var real = Hooks.HumansOn;
        var seen = new Queue<int>([1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Hooks.HumansOn = (_, _) => seen.Dequeue();
        var fc = new FakeClock(1793500000.0);
        var waits = new List<double>();
        var nq = W.Run(Cfg(), fc, true, n =>
        {
            waits.Add(n);
            fc.T += n;
        });
        Check("wait: somebody on now means waiting, and 0 humans alone is not enough for the next 10 minutes", nq.WaitForNobody("x") && waits.Count == 10);
        seen = new Queue<int>([0]);
        waits.Clear();
        var nq2 = W.Run(Cfg(), fc, true, n =>
        {
            waits.Add(n);
            fc.T += n;
        });
        Check("wait: nobody seen this run: at once", nq2.WaitForNobody("x") && waits.Count == 0);
        Hooks.HumansOn = real;
    }

    // ---- the merge check: a publish only ever adds to the live files
    static void MergeRule()
    {
        var old = Encoding.UTF8.GetBytes("[{\"rank\": 1, \"id\": 5, \"name\": \"a\", \"sizeMb\": 1}]");
        Check("merge: an append passes", Verdicts.MergeProblems("maps.json", old, "[{\"rank\": 1, \"id\": 5, \"name\": \"a\", \"sizeMb\": 1}, {\"rank\": 2, \"id\": 6, \"name\": \"b\", \"sizeMb\": 1}]").Count == 0);
        Check("merge: a changed entry is refused", Verdicts.MergeProblems("maps.json", old, "[{\"rank\": 1, \"id\": 5, \"name\": \"X\", \"sizeMb\": 1}]").Count > 0);
        Check("merge: a rank given twice is refused", Verdicts.MergeProblems("maps.json", old, "[{\"rank\": 1, \"id\": 5, \"name\": \"a\", \"sizeMb\": 1}, {\"rank\": 1, \"id\": 6, \"name\": \"b\", \"sizeMb\": 1}]").Count > 0);
        var ob = Encoding.UTF8.GetBytes("{\"schema\": \"x\", \"maps\": [{\"id\": 1}], \"packs\": []}");
        Check("merge: broken.json with entries added passes", Verdicts.MergeProblems("broken.json", ob, "{\"schema\": \"x\", \"maps\": [{\"id\": 1}, {\"id\": 2}], \"packs\": [{\"id\": 3}]}").Count == 0);
        Check("merge: broken.json losing an entry is refused", Verdicts.MergeProblems("broken.json", ob, "{\"schema\": \"x\", \"maps\": [], \"packs\": []}").Count > 0);
        Check("merge: a mode list may re-rank but never lose a map",
              Verdicts.MergeProblems("maps-wingman.json", "[{\"rank\": 1, \"id\": 7}]"u8.ToArray(), "[{\"rank\": 1, \"id\": 8}, {\"rank\": 2, \"id\": 7}]").Count == 0
              && Verdicts.MergeProblems("maps-wingman.json", "[{\"rank\": 1, \"id\": 7}]"u8.ToArray(), "[{\"rank\": 1, \"id\": 8}]").Count > 0);
    }
}
