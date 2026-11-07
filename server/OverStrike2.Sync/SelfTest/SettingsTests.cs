// The sync's self-test: the settings: what the sync reads of the settings file, its clamps, and a run-now that only lowers them.
using System.Text.Json.Nodes;
using OverStrike2.Core;
using static OverStrike2.TestKit.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>The settings file: every key the sync reads reaches its config, the hard limits clamp, a run-now may only lower a limit; the
/// keep set's queue rule follows the discovery stock.</summary>
static class SettingsTests
{
    static Settings ReadSettings(string json) => Settings.Parse(json, Part.Sync, _ => null, path => throw new FileNotFoundException(path));

    // ---- configuration: the limits can only be lowered
    internal static void Config()
    {
        var s = ReadSettings("""{ "schema": 1, "sync": { "apiDayCap": 20000, "apiRunCap": 99999, "downloadPauseSeconds": 0.1 }, "steam": { "apiGapSeconds": 0.5 } }""");
        var c = SyncConfig.From(s, _ => null);
        Check("config: a day cap above 10,000 is clamped to 10,000", c.ApiDayCap == 10000);
        Check("config: the run's cap never exceeds the day cap", c.ApiRunCap == 10000);
        Check("config: the gap never drops under 2 s", c.ApiGap == 2.0);
        Check("config: the pause between downloads never drops under 2 s", c.DownloadPause == 2.0);
        s = ReadSettings("""{ "schema": 1 }""");
        c = SyncConfig.From(s, _ => null);
        Check("config: defaults are 500 calls a run, 10,000 in 24 h, 2 s apart", (c.ApiRunCap, c.ApiDayCap, c.ApiGap) == (500, 10000, 2.0));
        Check("config: the daily restart at 07:00 to keep clear of, runs of 3 h, the API every 4 h", (c.DailyRestart, c.RunHours, c.ApiEvery) == (new TimeOfDay(7, 0), 3, 4 * 3600.0));
        c.Lower(O("""{"api_run_cap": 50, "batch": 5, "publish": false, "api": true}"""));
        Check("config: run-now lowers a run's limits, and may ask for the API steps", (c.ApiRunCap, c.Batch, c.Publish, c.ForceApi) == (50, 5, false, true));
        c.Lower(O("""{"api_run_cap": 5000, "batch": 1000}"""));
        Check("config: run-now cannot raise them", (c.ApiRunCap, c.Batch) == (50, 5));
        c.Lower(O("""{"api": false, "only_ids": [11, "12", "x"]}"""));
        Check("config: run-now may leave the API steps out, and limit the run to some maps", c.SkipApi && c.OnlyIds is ["11", "12"]);
        Check("ledger: the hard day cap cannot be raised by a caller", new ApiLedger("x", 50000, C(new FakeClock(0))).DayCap == 10000);
    }

    static Settings Parse(string json, Dictionary<string, string>? files = null) =>
        Settings.Parse(json, Part.Sync, _ => null, p => files is not null && files.TryGetValue(p, out var t) ? t : throw new FileNotFoundException(p));

    // ---- the settings file: every key the sync reads reaches its config; the HUD item; the values no environment variable had
    internal static void SettingsFile(string root)
    {
        var s = Parse("""
            { "schema": 1,
              "server": { "name": "Test Server", "host": "game.test", "port": 27016 },
              "bridge": { "url": "http://bridge.test:8081/", "secret": "abc123" },
              "hud": { "addon": "3823993566" },
              "restarts": { "daily": "06:30" },
              "steam": { "apiGapSeconds": 3.5 },
              "sync": { "enabled": false, "publish": false, "runHours": 2, "apiEveryHours": 6, "tags": ["Armsrace"], "excludeIds": ["123"],
                        "excludeCreators": ["76561190000000002"], "changedSince": "2026-10-01", "modeBackfill": true, "apiRunCap": 300, "apiDayCap": 4000,
                        "maxPages": 9, "downloadPauseSeconds": 5, "holdSeconds": 30, "batch": 7, "quietMinutes": 3, "testPort": 27300, "engineStartsMax": 11,
                        "ratings": false, "handover": false, "diskLowGb": 12, "webApiKey": "file:/key",
                        "mail": { "smtpHost": "smtp.test", "smtpPort": 2525, "from": "sync@example.com", "to": ["a@example.com", "b@example.com"], "dashboardUrl": "https://dash.example/" } },
              "paths": { "bridgeData": "/b", "gameDir": "/g", "syncState": "/s", "syncWork": "/w", "syncCfg": "/c", "steamCmd": "/x/steamcmd.sh", "depotcache": "/d" } }
            """, new() { ["/key"] = Key + "\n" });
        var c = SyncConfig.From(s, n => n == "HOME" ? "/home/test" : null);
        Check("settings: every key the sync reads reaches its config (sync.*, paths.*, server.*, bridge.*, hud.addon, restarts.daily, steam.apiGapSeconds)",
              !c.Enabled && !c.Publish && c.RunHours == 2 && c.ApiEvery == 6 * 3600 && c.Tags is ["Armsrace"] && c.ExcludeCreators.SetEquals(["76561190000000002"])
              && c.ChangedSince == "2026-10-01" && c.ModeBackfill && c.ApiRunCap == 300 && c.ApiDayCap == 4000 && c.ApiGap == 3.5 && c.MaxPages == 9 && c.DownloadPause == 5
              && c.Hold == 30 && c.Batch == 7 && c.Quiet == 180 && c.TestPort == 27300 && c.EngineStartsMax == 11 && !c.Ratings && !c.Handover && c.DiskLow == 12L << 30
              && c.WebApiKey == Key && c.SmtpHost == "smtp.test" && c.SmtpPort == 2525 && c.MailFrom == "sync@example.com" && c.MailTo is ["a@example.com", "b@example.com"]
              && c.DashboardUrl == "https://dash.example/" && c.StateDir == "/s" && c.WorkDir == "/w" && c.GameDir == "/g" && c.BridgeData == "/b" && c.CfgDir == "/c"
              && c.SteamCmd == "/x/steamcmd.sh" && c.Depotcache == "/d" && c.HandoverDir == Path.Combine("/w", "handover") && c.WatcherDir == Path.Combine("/g", "update-watch")
              && c.BridgeUrl == "http://bridge.test:8081" && c.BridgeSecret == "abc123" && c.GameHost == "game.test" && c.GamePort == 27016 && c.ServerName == "Test Server"
              && c.DailyRestart == new TimeOfDay(6, 30) && c.Home == "/home/test" && c.StatusFile == Path.Combine("/b", "sync-status.json"));
        Check("settings: the HUD item (hud.addon) is never a candidate, beside sync.excludeIds", c.HudAddon == "3823993566" && c.Exclude.SetEquals(["123", "3823993566"]));
        // sync.changedSince empty (its default): no listed map counts as changed at its first check - each gets its baseline
        var st = new SyncState(Path.Combine(root, "cs.json"));
        var noDay = Cfg();
        noDay.ChangedSince = "";
        var det = new Dictionary<string, JsonObject> { ["5"] = O("{\"publishedfileid\": \"5\", \"result\": 1, \"time_updated\": 1793000000}") };
        var j = Discovery.JudgeListed(det, ["5"], st, noDay);
        Check("settings: with no sync.changedSince no listed map counts as changed at its first check", j.Changed.Count == 0 && Node.Int(st.Baseline["5"]) == 1793000000);
        // restarts.daily off: nothing to keep clear of
        var off = Cfg();
        off.DailyRestart = null;
        var clock = C(new FakeClock(RunTests.T0));
        var day0 = clock.AtTime(new TimeOfDay(0, 0));
        Check("settings: restarts.daily off - a run is not cut short and may start at any time",
              Service.RunDeadline(off, clock, day0 + 5 * 3600) == day0 + 8 * 3600 && Service.StartBlocked(off, clock, day0 + 6.9 * 3600) is null);
        // the HUD item on the dashboard is hud.addon now, not the restart watcher's keep.json
        var cfg = Cfg();
        (cfg.StateDir, cfg.WorkDir, cfg.BridgeData, cfg.GameDir) = (Path.Combine(root, "hs"), Path.Combine(root, "hw"), Path.Combine(root, "hd"), Path.Combine(root, "hg"));
        Directory.CreateDirectory(cfg.BridgeData);
        cfg.HudAddon = "3823993566";
        var run = W.Run(cfg, new FakeClock(RunTests.T0));
        var heavy = run.StatusPicture();
        var hud = Node.Arr(heavy["keep"]!["parts"]).First(p => Node.Str(p!["part"]) == "hud");
        Check("status: the keep set's HUD item is hud.addon", Node.Int(hud!["count"]) == 1 && !Node.StrList(heavy["keep"]!["problems"]).Any(p => p.StartsWith("no HUD item", StringComparison.Ordinal)));
    }

    // ---- the keep set's queue rule follows the discovery stock (bridge.discoveryStock), as the restart watcher's does: the bridge's lanes
    // are the queues, so a stock of N under 10 wants lanes of N, and with the stock off (0) empty lanes are no reason to refuse
    internal static void StockRule()
    {
        var c4 = SyncConfig.From(Parse("""{ "schema": 1, "bridge": { "discoveryStock": 4 } }"""), _ => null);
        var c0 = SyncConfig.From(Parse("""{ "schema": 1, "bridge": { "discoveryStock": 0 } }"""), _ => null);
        var c10 = SyncConfig.From(Parse("""{ "schema": 1 }"""), _ => null);
        static string[] QueueProblems(SyncConfig rules, JsonObject queues)
        {
            var cfg = Cfg();
            cfg.KeepRules = rules.KeepRules;
            var data = HandOverTests.KData();
            data["queues.json"] = queues.DeepClone();
            return W.Run(cfg, new FakeClock(1793700000.0)).SafeKeepSet(HandOverTests.Inputs(data), 1793700000.0, "500").Problems
                .Where(p => p.Contains(" queue holds ", StringComparison.Ordinal)).ToArray();
        }
        var four = O("""{"version": 1, "random": {"armsrace-new": ["1", "2", "3", "4"], "armsrace-original": ["1", "2", "3", "4"], "deathmatch": ["5", "6", "7", "8"], "wingman": ["9", "10", "11", "12"]}, "requested": []}""");
        var none = O("""{"version": 1, "random": {"armsrace-new": [], "armsrace-original": [], "deathmatch": [], "wingman": []}, "requested": []}""");
        Check("keep set: the queue rule follows the discovery stock - lanes of 4 for a stock of 4, none for a stock that is off, 10 by default (KeepRules.ForStock, as the restart watcher's)",
              c4.KeepRules.MinQueue == 4 && c0.KeepRules.MinQueue == 0 && c10.KeepRules.MinQueue == 10 && c4.KeepRules == KeepRules.ForStock(4)
              && QueueProblems(c4, four).Length == 0 && QueueProblems(c0, none).Length == 0 && QueueProblems(c10, four).Length == 4);
    }
}
