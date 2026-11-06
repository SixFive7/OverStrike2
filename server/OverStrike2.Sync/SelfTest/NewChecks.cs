using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using OverStrike2.Core;
using MapsFolder = OverStrike2.Server.MapsFolder;
using ServerConfig = OverStrike2.Server.ServerConfig;
using ServerLog = OverStrike2.Server.Log;
using ServerPaths = OverStrike2.Server.ServerPaths;
using static OverStrike2.Sync.SelfTest.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>What the port brings new: the settings file, the Python sync's formats it must keep byte for byte, the program's one Web API
/// door, the command line, the SMTP client, and - in the game image - SteamCMD's driver, the test tree, and the whole hand-over taken in
/// by the restart watcher's own code.</summary>
static class NewChecks
{
    public static void Run()
    {
        var root = Dir("new");
        SettingsFile(root);
        StockRule();
        Formats();
        DoorAndLedger(root);
        Cli(root);
        SmtpClient();
        TailLines(root);
        SteamCmdDriver(root);
        TestTree(root);
        TakenIn(root);
        Stopping();
    }

    static Settings Parse(string json, Dictionary<string, string>? files = null) =>
        Settings.Parse(json, Part.Server, _ => null, p => files is not null && files.TryGetValue(p, out var t) ? t : throw new FileNotFoundException(p));

    // ---- the settings file: every key the sync reads reaches its config; the HUD item; the values no environment variable had
    static void SettingsFile(string root)
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
        Check("settings: with no sync.changedSince no listed map counts as changed at its first check", j.Changed.Count == 0 && Py.Int(st.Baseline["5"]) == 1793000000);
        // restarts.daily off: nothing to keep clear of
        var off = Cfg();
        off.DailyRestart = null;
        var clock = C(new FakeClock(Part2.T0));
        var day0 = clock.AtTime(new TimeOfDay(0, 0));
        Check("settings: restarts.daily off - a run is not cut short and may start at any time",
              Service.RunDeadline(off, clock, day0 + 5 * 3600) == day0 + 8 * 3600 && Service.StartBlocked(off, clock, day0 + 6.9 * 3600) is null);
        // the HUD item on the dashboard is hud.addon now, not the restart watcher's keep.json
        var cfg = Cfg();
        (cfg.StateDir, cfg.WorkDir, cfg.BridgeData, cfg.GameDir) = (Path.Combine(root, "hs"), Path.Combine(root, "hw"), Path.Combine(root, "hd"), Path.Combine(root, "hg"));
        Directory.CreateDirectory(cfg.BridgeData);
        cfg.HudAddon = "3823993566";
        var run = W.Run(cfg, new FakeClock(Part2.T0));
        var heavy = run.StatusHeavy();
        var hud = Py.Arr(heavy["keep"]!["parts"]).First(p => Py.Str(p!["part"]) == "hud");
        Check("status: the keep set's HUD item is hud.addon", Py.Int(hud!["count"]) == 1 && !Py.StrList(heavy["keep"]!["problems"]).Any(p => p.StartsWith("no HUD item", StringComparison.Ordinal)));
    }

    // ---- the keep set's queue rule follows the discovery stock (bridge.discoveryStock), as the restart watcher's does: the bridge's lanes
    // are the queues, so a stock of N under 10 wants lanes of N, and with the stock off (0) empty lanes are no reason to refuse
    static void StockRule()
    {
        var c4 = SyncConfig.From(Parse("""{ "schema": 1, "bridge": { "discoveryStock": 4 } }"""), _ => null);
        var c0 = SyncConfig.From(Parse("""{ "schema": 1, "bridge": { "discoveryStock": 0 } }"""), _ => null);
        var c10 = SyncConfig.From(Parse("""{ "schema": 1 }"""), _ => null);
        static string[] QueueProblems(SyncConfig rules, JsonObject queues)
        {
            var cfg = Cfg();
            cfg.KeepRules = rules.KeepRules;
            var data = Part3.KData();
            data["queues.json"] = queues.DeepClone();
            return W.Run(cfg, new FakeClock(1793700000.0)).SafeKeepSet(Part3.Inputs(data), 1793700000.0, "500").Problems
                .Where(p => p.Contains(" queue holds ", StringComparison.Ordinal)).ToArray();
        }
        var four = O("""{"version": 1, "random": {"armsrace-new": ["1", "2", "3", "4"], "armsrace-original": ["1", "2", "3", "4"], "deathmatch": ["5", "6", "7", "8"], "wingman": ["9", "10", "11", "12"]}, "requested": []}""");
        var none = O("""{"version": 1, "random": {"armsrace-new": [], "armsrace-original": [], "deathmatch": [], "wingman": []}, "requested": []}""");
        Check("keep set: the queue rule follows the discovery stock - lanes of 4 for a stock of 4, none for a stock that is off, 10 by default (KeepRules.ForStock, as the restart watcher's)",
              c4.KeepRules.MinQueue == 4 && c0.KeepRules.MinQueue == 0 && c10.KeepRules.MinQueue == 10 && c4.KeepRules == KeepRules.ForStock(4)
              && QueueProblems(c4, four).Length == 0 && QueueProblems(c0, none).Length == 0 && QueueProblems(c10, four).Length == 4);
    }

    // ---- the formats the Python sync wrote, which this program keeps byte for byte (references made by Python itself when the port was written)
    static void Formats()
    {
        var tricky = O("""
            {"f": [0.1, 1e16, 1.5e-07, 1793500000.0, 0.547, 2.675, 1e-05, 123456789.125, -0.0, 1e15], "s": "a\"b\\c\nd\te\u0001\u007fé‮😀",
             "n": null, "t": true, "e": [], "o": {}, "i": [1, -2, 12345678901234]}
            """);
        Check("json: compact as the sync's own files (Python's separators, floats, ensure_ascii off)",
              Py.Compact(tricky) == "{\"f\":[0.1,1e+16,1.5e-07,1793500000.0,0.547,2.675,1e-05,123456789.125,-0.0,1000000000000000.0],\"s\":\"a\\\"b\\\\c\\nd\\te\\u0001\u007fé‮\U0001f600\",\"n\":null,\"t\":true,\"e\":[],\"o\":{},\"i\":[1,-2,12345678901234]}");
        Check("json: indent 0 (search.json) and the default separators (the hand-over marker) as Python writes them",
              Py.Dumps(O("{\"a\": [1, {\"b\": \"c\"}], \"d\": {}}"), 0) == "{\n\"a\": [\n1,\n{\n\"b\": \"c\"\n}\n],\n\"d\": {}\n}"
              && Py.Dumps(O("{\"a\": [1, 2], \"b\": {\"c\": null}}")) == "{\"a\": [1, 2], \"b\": {\"c\": null}}"
              && Py.Dumps(new JsonObject { ["x"] = 1793500000.0, ["y"] = 2L }) == "{\"x\": 1793500000.0, \"y\": 2}");
        var cases = new (double X, int D)[] { (0.25, 1), (0.35, 1), (2.675, 2), (1.25, 1), (2.5, 0), (3.5, 0), (1342177280 / 1073741824.0, 1), (0.125, 2), (-0.04, 1), (99.95, 1) };
        Check("numbers: \"%.Nf\" and round() as Python's - the exact binary value, ties to even",
              string.Join("|", cases.Select(c => Py.Fixed(c.X, c.D))) == "0.2|0.3|2.67|1.2|2|4|1.2|0.12|-0.0|100.0"
              && string.Join("|", cases.Select(c => Py.Repr(Py.Round(c.X, c.D)))) == "0.2|0.3|2.67|1.2|2.0|4.0|1.2|0.12|-0.0|100.0");
        Check("words: sizes as the Python sync's mb()",
              string.Join("|", new long[] { 0, 1048575, 1572864, 1073741824, 1342177280, 1610612736, 5L << 30 }.Select(b => Text.Mb(b))) == "0 MB|1 MB|2 MB|1.0 GB|1.2 GB|1.5 GB|5.0 GB");
        Check("words: clean() and the search text as Python's - control characters, bidi overrides, a cut at a word, by code points",
              Text.Clean("  a\u0000b‮c\t\td  " + string.Concat(Enumerable.Repeat("word ", 30)), 50) == "a bc d word word word word word word word word…"
              && Text.Clean(string.Concat(Enumerable.Repeat("\U0001F600", 30)) + " tail", 20) == string.Concat(Enumerable.Repeat("\U0001f600", 20)) + "…"
              && Verdicts.SearchBlob("Big  Map!", "77", ["Arms Race", "Wingman"], "A <b>bold</b> map\nwith lines") == "big map! 77 arms race wingman a <b>bold</b> map with lines");
    }

    // ---- one door per program: the gap holds from one run's last call to the next run's first; a call without its key reserves nothing
    static void DoorAndLedger(string root)
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
        Check("api: a keyed call without the key is neither sent nor counted in the ledger", sent2.Count == 0 && Py.Arr(led2.Data!["calls"]).Count == 0 && led2.Count(C(clock2).Day()) == 0);
    }

    static (int Code, string Said) Quiet(Func<int> run)
    {
        var (o, e) = (Console.Out, Console.Error);
        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetError(sw);
        try
        {
            return (run(), sw.ToString());
        }
        finally
        {
            Console.SetOut(o);
            Console.SetError(e);
        }
    }

    // ---- the command line
    static void Cli(string root)
    {
        Check("cli: an argument it does not know exits at once (2), so does a command", Quiet(() => Program.Main(["serve", "--nonsense"])).Code == 2
              && Quiet(() => Program.Main(["nonsense"])).Code == 2 && Quiet(() => Program.Main(["once", "{not json"])).Code == 2);
        string[][] gone = [["serve", "--dry-run"], ["once", "--dry-run"], ["serve", "--state", root], ["once", "--work", root], ["serve", "--test-port", "27400"], ["once", "--no-handover"]];
        Check("cli: --dry-run, --state, --work, --test-port and --no-handover are gone: each exits at once (2), so an old command line never starts a run in their place",
              gone.All(a => Quiet(() => Program.Main(a)).Code == 2));
        var (code, said) = Quiet(() => Program.Main(["version"]));
        Check("cli: version", code == 0 && said.StartsWith("os2-sync ", StringComparison.Ordinal));
        var file = Path.Combine(root, "cli.jsonc");
        File.WriteAllText(file, "{ \"schema\": 7 }");
        (code, said) = Quiet(() => Program.Main(["once", "--settings", file]));
        Check("cli: a settings file this program cannot use: nothing done, and it says so", code == Settings.RefusedExit && said.Contains("cannot be used"));
        foreach (var (what, text, why) in new[]
        {
            ("missing", "{ \"schema\": 1 }", "hud.addon: missing"),
            ("empty", "{ \"schema\": 1, \"hud\": { \"addon\": \"\" } }", "hud.addon: empty"),
            ("not digits", "{ \"schema\": 1, \"hud\": { \"addon\": \"abc\" } }", "hud.addon: not digits"),
        })
        {
            File.WriteAllText(file, text);
            foreach (var command in new[] { "serve", "once" })
            {
                (code, said) = Quiet(() => Program.Main([command, "--settings", file]));
                Check($"cli: {command} with hud.addon {what} refuses as os2-server does: one ALERT line naming the key and the fix, exit {Settings.RefusedExit}",
                      code == Settings.RefusedExit && said.Trim().Split('\n').Length == 1 && said.Contains("ALERT") && said.Contains(why) && said.Contains("required: the Workshop id") && said.Contains("fix the file"));
            }
        }
        (code, said) = Quiet(() => Program.Main(["once", "--settings", Path.Combine(root, "no-such-file.jsonc")]));
        Check("cli: no settings file at all: the same", code == Settings.RefusedExit && said.Contains("no settings file at") && said.Contains("hud.addon: missing"));
        File.WriteAllText(file, "{ \"schema\": 1 }");
        (code, said) = Quiet(() => Program.Main(["doctor", "--settings", file]));
        Check("cli: doctor still works with no hud.addon, and says so", code == 1 && said.Contains("NOT USABLE: hud.addon: missing") && said.Contains("Web API key:"));
        (code, said) = Quiet(() => Program.Main(["version", "--settings", file]));
        Check("cli: version too", code == 0 && said.StartsWith("os2-sync ", StringComparison.Ordinal) && said.Contains("settings: NOT USABLE - hud.addon: missing"));
        File.WriteAllText(file, "// a comment\n{ \"schema\": 1, \"hud\": { \"addon\": \"3823993566\" }, \"sync\": { \"webApiKey\": \"\" }, \"paths\": { \"syncState\": \"/cli-state\" }, }");
        (code, said) = Quiet(() => Program.Main(["doctor", "--settings", file]));
        Check("cli: doctor says what it sees, the key without its value", code == 0 && said.Contains("Web API key: absent") && said.Contains("path state: /cli-state ") && said.Contains("the HUD item: 3823993566"));
        (code, said) = Quiet(() => Program.Main(["version", "--settings", file]));
        Check("cli: version with a good file: its one line", code == 0 && said.StartsWith("os2-sync ", StringComparison.Ordinal) && !said.Contains("NOT USABLE"));
        var (savedContent, savedPath) = (Environment.GetEnvironmentVariable(Settings.ContentVariable), Environment.GetEnvironmentVariable(Settings.PathVariable));
        Environment.SetEnvironmentVariable(Settings.PathVariable, null);
        Environment.SetEnvironmentVariable(Settings.ContentVariable, "{ \"schema\": 1, \"hud\": { \"addon\": \"3823993566\" }, \"sync\": { \"webApiKey\": \"\" } }");
        (code, said) = Quiet(() => Program.Main(["doctor"]));
        Check($"cli: {Settings.ContentVariable} is read like the file, and doctor names it", code == 0 && said.Contains($"settings: {Settings.ContentSource}") && said.Contains("the HUD item: 3823993566"));
        (code, said) = Quiet(() => Program.Main(["doctor", "--settings", file]));
        Check("cli: --settings wins over it, for a test", code == 0 && said.Contains($"settings: {file}"));
        Environment.SetEnvironmentVariable(Settings.ContentVariable, "{ \"schema\": 1 }");
        (code, said) = Quiet(() => Program.Main(["once"]));
        Check("cli: a value with no hud.addon refuses the same way, and the ALERT names the variable", code == Settings.RefusedExit && said.Contains($"{Settings.ContentSource}: hud.addon: missing"));
        Environment.SetEnvironmentVariable(Settings.ContentVariable, savedContent);
        Environment.SetEnvironmentVariable(Settings.PathVariable, savedPath);
    }

    // ---- the problem mail's SMTP client, against a stand-in relay on localhost
    static void SmtpClient()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var lines = new List<string>();
        var server = new Thread(() =>
        {
            try
            {
                using var c = listener.AcceptTcpClient();
                var s = c.GetStream();
                var r = new StreamReader(s, Encoding.UTF8);
                void Say(string t) => s.Write(Encoding.UTF8.GetBytes(t + "\r\n"));
                Say("220 relay.test ESMTP");
                var data = false;
                while (r.ReadLine() is { } line)
                {
                    lines.Add(line);
                    if (data)
                    {
                        if (line == ".")
                        {
                            data = false;
                            Say("250 queued");
                        }
                        continue;
                    }
                    if (line.StartsWith("EHLO", StringComparison.Ordinal))
                        Say("250-relay.test\r\n250 8BITMIME");
                    else if (line == "DATA")
                    {
                        data = true;
                        Say("354 go ahead");
                    }
                    else if (line == "QUIT")
                    {
                        Say("221 bye");
                        break;
                    }
                    else
                        Say("250 ok");
                }
            }
            catch (Exception e) when (e is IOException or SocketException)
            {
            }
        }) { IsBackground = true };
        server.Start();
        var cfg = Cfg();
        (cfg.SmtpHost, cfg.SmtpPort, cfg.MailFrom, cfg.MailTo) = ("127.0.0.1", port, "The Sync <sync@example.com>", ["a@example.com", "b@example.com"]);
        var why = Mail.Send(cfg, "a subject é", "line one\n.a line that starts with a dot\nlast");
        server.Join(5000);
        listener.Stop();
        Check($"mail: sent through an SMTP relay - the sender's address, every recipient, the body dot-stuffed ({why ?? "sent"})",
              why is null && lines.Contains("MAIL FROM:<sync@example.com>") && lines.Contains("RCPT TO:<a@example.com>") && lines.Contains("RCPT TO:<b@example.com>")
              && lines.Contains("Subject: =?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("a subject é")) + "?=") && lines.Contains("..a line that starts with a dot") && lines.Contains("last") && !lines.Contains("STARTTLS"));
        Check("mail: no relay configured, no mail", Mail.Send(Cfg(), "s", "b") == "no SMTP host, sender or recipient configured");
        Check("mail: a plain subject stays as it is, and no line break makes a header of its own",
              Mail.HeaderText("Over Strike 2: a problem") == "Over Strike 2: a problem" && Mail.HeaderText("a\r\nBcc: x") == "a  Bcc: x");
        var longName = string.Concat(Enumerable.Repeat("Der gr\u00fcne Server \U0001F600 ", 12));
        var folded = Mail.HeaderText(longName).Split("\r\n ");
        var words = folded.Select(w => w[10..^2]).Select(Convert.FromBase64String).ToList();
        Check($"mail: a long subject beyond ASCII is encoded words that fit their lines, each whole characters, and read back the same ({folded.Length} words)",
              folded.Length > 1 && folded.All(w => w.StartsWith("=?utf-8?B?", StringComparison.Ordinal) && w.EndsWith("?=", StringComparison.Ordinal) && w.Length + "Subject: ".Length <= 76)
              && words.All(b => !Encoding.UTF8.GetString(b).Contains('\uFFFD')) && string.Concat(words.Select(Encoding.UTF8.GetString)) == longName);
    }

    // ---- the console's tail: new lines only, a line cut short kept for later, a truncated file read again from its start
    static void TailLines(string root)
    {
        var path = Path.Combine(root, "console.log");
        File.WriteAllText(path, "old line\n");
        var tail = new Tail(path);
        File.AppendAllText(path, "one\ntw");
        var first = tail.Lines();
        File.AppendAllText(path, "o\r\nthree\n");
        var second = tail.Lines();
        File.WriteAllText(path, "fresh\n");
        var third = tail.Lines();
        Check("tail: new lines only, a line cut short kept for later, a truncated log read from its start",
              first is ["one"] && second is ["two", "three"] && third is ["fresh"] && tail.Lines().Count == 0);
    }

    // ---- SteamCMD's driver: a stand-in steamcmd.sh that answers as SteamCMD does, in a session of its own
    static void SteamCmdDriver(string root)
    {
        if (!OperatingSystem.IsLinux())
        {
            foreach (var name in new[] { "steamcmd: the anonymous login, a download, a failure", "steamcmd: an item it says nothing about is a stall, after its timeout",
                                         "steamcmd: the stop takes the whole process group, a child that ignores quit too" })
                Linux(name, () => false);
            return;
        }
        var dir = Path.Combine(root, "steamcmd");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "steamcmd.sh");
        File.WriteAllText(exe, """
            #!/bin/bash
            sleep 300 &
            echo "Redirecting stderr to 'stderr.txt'"
            echo "Connecting anonymously to Steam Public...OK"
            echo "Waiting for user info...OK"
            printf 'Steam>'
            while read -r cmd a b; do
              case "$cmd" in
                workshop_download_item)
                  case "$b" in
                    111) echo "Downloading item 111 ..."; echo "Success. Downloaded item 111 to \"/x/content/730/111\" (1234 bytes)";;
                    222) echo "ERROR! Download item 222 failed (Access Denied)";;
                    333) sleep 3;;
                  esac
                  printf 'Steam>';;
                quit) exit 0;;
              esac
            done

            """);
        File.SetUnixFileMode(exe, (UnixFileMode)0b111_101_101);
        var clock = new SyncClock(SystemClock.Instance, Paris);
        var steam = new SteamCmdProcess(exe, Path.Combine(dir, "staging"), Path.Combine(dir, "steamcmd.log"), dir, clock) { LoginTimeout = 20, DownloadTimeout = 1 };
        var login = steam.Start();
        var good = steam.Download("111");
        var bad = steam.Download("222");
        Check($"steamcmd: the anonymous login, a download, a failure ({login.Why}; {good.Reason}; {bad.Reason})",
              login.Ok && good is (true, "OK", 1234) && bad is (false, "Access Denied", 0) && File.ReadAllText(Path.Combine(dir, "steamcmd.log")).Contains("Downloaded item 111"));
        var stall = steam.Download("333");
        Check($"steamcmd: an item it says nothing about is a stall, after its timeout ({stall.Reason})",
              !stall.Ok && stall.Reason == SteamCmdWords.Stall + " 1s" && stall.Reason.StartsWith(SteamCmdWords.Stall, StringComparison.Ordinal));
        var pid = typeof(SteamCmdProcess).GetField("proc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(steam) is Process p ? p.Id : -1;
        steam.Stop();
        Check("steamcmd: the stop takes the whole process group, a child that ignores quit too", pid > 0 && !Native.GroupAlive(pid));
    }

    // ---- the test tree: links into the game's install, the engine's own files copied, no log address, no plugins
    static void TestTree(string root)
    {
        if (!OperatingSystem.IsLinux())
        {
            Linux("test tree: links into the game, the engine's files copied, the cfgs without log addresses, gameinfo without the plugins", () => false);
            return;
        }
        var game = Path.Combine(root, "tree-game");
        var work = Path.Combine(root, "tree-work");
        var cfgDir = Path.Combine(root, "tree-cfg");
        Put(Path.Combine(game, "game", "cs2.sh"), "#!/bin/bash\n");
        Put(Path.Combine(game, "game", "other.txt"), "x");
        Put(Path.Combine(game, "game", "bin", "linuxsteamrt64", "cs2"), "engine");
        Put(Path.Combine(game, "game", "bin", "linuxsteamrt64", "libengine.so"), "lib");
        Put(Path.Combine(game, "game", "bin", "linuxsteamrt64", "x.log"), "log");
        Put(Path.Combine(game, "game", "bin", "linuxsteamrt64", "steamapps", "workshop", "appworkshop_730.acf"), "acf");
        Put(Path.Combine(game, "game", "bin", "other", "tool"), "t");
        Put(Path.Combine(game, "game", "csgo", "maps", "x.vpk"), "m");
        Put(Path.Combine(game, "game", "csgo", "addons", "metamod", "x"), "m");
        Put(Path.Combine(game, "game", "csgo", "console.log"), "c");
        Put(Path.Combine(game, "game", "csgo", "cfg", "server.cfg"), "hostname x\nlogaddress_add_http \"http://198.51.100.9/log\"\n");
        Put(Path.Combine(game, "game", "csgo", "cfg", "sub", "a.cfg"), "a 1\nLOGADDRESS_ADD x\nb 2");
        Put(Path.Combine(game, "game", "csgo", "gameinfo.gi"), "\t\t\tGame\tcsgo/addons/metamod\n\t\t\tGame\tcsgo\n");
        Put(Path.Combine(cfgDir, "gamemode_armsrace_server.cfg"), "mp_x 1\n");
        var line = Farm.Build(game, work, cfgDir, new SyncClock(SystemClock.Instance, Paris));
        var farm = Path.Combine(work, "farm", "game");
        Check($"test tree: links into the game, the engine's files copied, the cfgs without log addresses, gameinfo without the plugins ({line})",
              !Handover.IsLink(Path.Combine(farm, "cs2.sh")) && File.Exists(Path.Combine(farm, "cs2.sh")) && Handover.IsLink(Path.Combine(farm, "other.txt"))
              && !Handover.IsLink(Path.Combine(farm, "bin", "linuxsteamrt64", "cs2")) && File.Exists(Path.Combine(farm, "bin", "linuxsteamrt64", "libengine.so"))
              && !File.Exists(Path.Combine(farm, "bin", "linuxsteamrt64", "x.log")) && Handover.IsLink(Path.Combine(farm, "bin", "linuxsteamrt64", "steamapps"))
              && new FileInfo(Path.Combine(farm, "bin", "linuxsteamrt64", "steamapps")).LinkTarget == Path.Combine(work, "staging", "steamapps")
              && Handover.IsLink(Path.Combine(farm, "bin", "other")) && Handover.IsLink(Path.Combine(farm, "csgo", "maps"))
              && !Directory.Exists(Path.Combine(farm, "csgo", "addons")) && !File.Exists(Path.Combine(farm, "csgo", "console.log"))
              && File.ReadAllText(Path.Combine(farm, "csgo", "cfg", "server.cfg")) == Farm.TestServerCfg
              && File.ReadAllText(Path.Combine(farm, "csgo", "cfg", "sub", "a.cfg")) == "a 1\nb 2"
              && File.Exists(Path.Combine(farm, "csgo", "cfg", "gamemode_armsrace_server.cfg"))
              && File.ReadAllText(Path.Combine(farm, "csgo", "gameinfo.gi")) == "\t\t\tGame\tcsgo\n"
              && line.Contains("2 engine files copied") && line.Contains("search path removed") && line.Contains("from 2 cfg file(s)"));
    }

    // ---- the whole hand-over: what the sync hands over, the restart watcher's own code takes in, and the sync then sees the map as stored
    static void TakenIn(string root)
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
        var (ok, words) = Handover.HandOver(staging, handover, "4100", "Taken In", depot, Part2.T0, Log.Line);
        var serverDepot = Path.Combine(root, "ti-server-depot");
        Directory.CreateDirectory(serverDepot);
        var config = new ServerConfig
        {
            Paths = new ServerPaths { GameDir = game, BridgeData = data, Handover = handover, Depotcache = serverDepot, Plugins = Path.Combine(root, "ti-plugins") },
            MapsDelete = "off",
        };
        var clock = new FakeClock(Part2.T0);
        var log = new ServerLog("test", clock, Paris, _ => { });
        var said = new MapsFolder(config, log, clock, Paris).Organize();
        var stored = Handover.ServerMaps(game);
        var link = Path.Combine(serverWs, "content", "730", "4100");
        Check(name, () => ok && stored.TryGetValue("4100", out var t) && t == 1790000000 && Handover.IsLink(link)
                          && File.Exists(Path.Combine(link, "maps", "inner.txt")) && File.Exists(Path.Combine(serverDepot, "730_424242.manifest"))
                          && !File.Exists(Path.Combine(handover, "4100.json")) && KeyValues.Entries(File.ReadAllText(Path.Combine(serverWs, "appworkshop_730.acf")), "4100") is { } e
                          && e.Details.GetValueOrDefault("timetouched") == "1790000001" && words.StartsWith("handed over", StringComparison.Ordinal) && said.Length > 0);
    }

    // ---- a SIGTERM: the next wait ends the run with its clean-up, never as an error
    static void Stopping()
    {
        var stoppable = new StoppableClock();
        stoppable.Stop();
        var threw = false;
        try
        {
            new SyncClock(stoppable, Paris).Sleep(60);
        }
        catch (StopRequested)
        {
            threw = true;
        }
        Check("service: a SIGTERM ends the next wait at once, as StopRequested (the run's clean-up runs, nothing is mailed as a crash)", threw);
    }
}
