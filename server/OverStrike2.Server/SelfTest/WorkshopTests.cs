using System.Text.Json;
using OverStrike2.Core;
using static OverStrike2.Server.SelfTest.T;

namespace OverStrike2.Server.SelfTest;

/// <summary>The acf and the workshop log, the program's command line, job 8's download line, the Web API door, job 6's download restart
/// and the stuck state's evidence, job 9's background downloads - and the two collisions of a player's pick with job 9's download.</summary>
static class WorkshopTests
{
    public const string Acf = "\"AppWorkshop\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"SizeOnDisk\"\t\t\"30\"\n\t\"WorkshopItemsInstalled\"\n\t{\n"
        + "\t\t\"111\"\n\t\t{\n\t\t\t\"size\"\t\t\"10\"\n\t\t\t\"timeupdated\"\t\t\"1000\"\n\t\t\t\"manifest\"\t\t\"7\"\n\t\t}\n"
        + "\t\t\"222\"\n\t\t{\n\t\t\t\"size\"\t\t\"20\"\n\t\t\t\"timeupdated\"\t\t\"2000\"\n\t\t\t\"manifest\"\t\t\"8\"\n\t\t}\n\t}\n"
        + "\t\"WorkshopItemDetails\"\n\t{\n\t\t\"111\"\n\t\t{\n\t\t\t\"manifest\"\t\t\"7\"\n\t\t}\n"
        + "\t\t\"222\"\n\t\t{\n\t\t\t\"manifest\"\t\t\"8\"\n\t\t}\n\t}\n}\n";

    static DateTimeOffset At(int d, int h, int m, int s) => Scenario.At(2026, 10, d, h, m, s);

    static string WsDir => Path.Combine(Scenario.Root, "game", "bin", "linuxsteamrt64", "steamapps", "workshop");

    public static void Run()
    {
        // ---- K-M: the acf and the workshop log; the prefetch is gone
        Check("K: an acf round-trips byte-identical through kv_parse/kv_dump", KeyValues.Dump(KeyValues.Parse(Acf)) == Acf);
        Check("K: acf_installed reads ids and timeupdated", KeyValues.Installed(Acf) is { Count: 2 } inst && inst["111"] == 1000 && inst["222"] == 2000);
        var ev = Downloads.Events(["[2001-01-01 12:00:00] [AppID 730] Download item 333 requested by app",
                                   "[2001-01-01 12:14:53] [AppID 730] Download item 333 result : OK",
                                   "[2001-01-01 12:14:54] [AppID 730] Download item 444 result : No Connection", "noise"]);
        Check("L: workshop_log download lines parse",
              ev.SequenceEqual([new DownloadEvent("333", "requested", ""), new DownloadEvent("333", "result", "OK"), new DownloadEvent("444", "result", "No Connection")]));
        Check("M: no prefetch - its plan, its list, its Workshop details, its window and its restart are gone",
              !Kinds.Planned.Contains("prefetch") && !Settings.Keys.Any(k => k.Path.Contains("prefetch", StringComparison.OrdinalIgnoreCase))
              && !new WatchState().ToJson().Contains("prefetch"));
        Directory.CreateDirectory(Path.Combine(WsDir, "content", "730", "111"));
        File.WriteAllText(Path.Combine(WsDir, "appworkshop_730.acf"), Acf);
        Check("CLI: --warmup-reset (what an older pre.sh still calls) is gone", Quiet(() => Program.Main(["watch", "--warmup-reset"])).Code == 2);
        Check("CLI: an argument it does not know exits at once (2) - never the watcher's loop in the foreground of pre.sh",
              Quiet(() => Program.Main(["organize", "--observe"])).Code == 2 && Quiet(() => Program.Main(["nonsense"])).Code == 2);
        Check("CLI: watch --observe, organize --dry-run and --state are gone: each exits at once (2), so an old command line runs neither the watcher nor the real organize",
              Quiet(() => Program.Main(["watch", "--observe"])).Code == 2 && Quiet(() => Program.Main(["organize", "--dry-run"])).Code == 2
              && Quiet(() => Program.Main(["organize", "--state", Dir("cli-state")])).Code == 2);
        var off = Path.Combine(Dir("cli"), "off.jsonc");
        File.WriteAllText(off, "{ \"schema\": 1, \"hud\": { \"addon\": \"3823993566\" }, \"restarts\": { \"updates\": false, \"newVersions\": false, \"freezeRecovery\": false, \"downloadRestart\": false, \"daily\": \"off\" }, \"maps\": { \"backgroundDownloads\": false, \"downloadLine\": false } }");
        var (code, said) = Quiet(() => Program.Main(["watch", "--settings", off]));
        Check($"CLI: every job of the watcher off - nothing to do, the watcher does not start ({code})", code == 0 && said.Contains("nothing to do"));
        var allOff = ServerConfig.From(Settings.Parse(File.ReadAllText(off), Part.Server, _ => null), _ => null);
        Check("CLI: ... that is all seven switches: the settings file's", Program.NothingToDo(allOff));
        foreach (var (name, on) in new (string, Action<ServerConfig>)[]
        {
            ("restarts.updates", c => c.Updates = true), ("restarts.newVersions", c => c.NewVersions = true), ("restarts.freezeRecovery", c => c.FreezeRecovery = true),
            ("restarts.daily", c => c.Daily = new TimeOfDay(7, 0)), ("restarts.downloadRestart", c => c.DownloadRestart = true),
            ("maps.backgroundDownloads", c => c.BackgroundDownloads = true), ("maps.downloadLine", c => c.DownloadLine = true),
        })
        {
            var one = ServerConfig.From(Settings.Parse(File.ReadAllText(off), Part.Server, _ => null), _ => null);
            on(one);
            Check($"CLI: ... {name} alone on is a job for the watcher", !Program.NothingToDo(one));
        }
        var broken = Path.Combine(Dir("cli2"), "broken.jsonc");
        File.WriteAllText(broken, "{ \"schema\": 7 }");
        (code, said) = Quiet(() => Program.Main(["install", "--settings", broken]));
        Check("CLI: a settings file this program cannot use: nothing done, and it says so", code == Settings.RefusedExit && said.Contains("cannot be used"));

        Line();
        Door();
        DownloadRestart();
        Stuck();
        Job9();
        Collisions();
    }

    /// <summary>Main with its output caught: (exit code, what it wrote).</summary>
    public static (int Code, string Said) Quiet(Func<int> run)
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

    /// <summary>N2. the download line: what is said, when, and to nobody when nobody is on.</summary>
    static void Line()
    {
        var sent = new List<string>();
        var eng = new FakeEngine(Path.Combine(Dir("line"), "console.log")) { OnRcon = (c, _) => { sent.AddRange(c); return ["gg_notice: shown"]; } };
        var data = Dir("bridge0");
        File.WriteAllText(Path.Combine(data, "maps-deathmatch.json"), "[{\"rank\": 1, \"id\": 900, \"name\": \"Bastion\", \"sizeMb\": 155}]");
        var config = Scenario.Copy(Scenario.Template, Scenario.Template.Paths with { BridgeData = data });
        var log = new Log("test", new FakeClock(At(4, 15, 0, 0)), Scenario.Paris, _ => { });
        var ln = new DownloadLine(config, eng, log);
        ln.ConsoleLine("11/01 08:45:57 [Workshop] Updating 900: state 3", 100.0);
        ln.Step(101.0, 2);
        Check("line: nothing in its first seconds", sent.Count == 0);
        ln.Step(103.5, 0);
        Check("line: nothing with nobody on", sent.Count == 0);
        ln.Step(103.5, 2);
        Check($"line: the name and its size once it has taken 3 s ({Show(sent)})", sent.Count == 1 && sent[0].StartsWith("gg_notice 4 ") && sent[0].Contains("Bastion") && sent[0].Contains("155 MB"));
        ln.Step(104.5, 2);
        Check("line: not more often than every 2 s", sent.Count == 1);
        ln.Staged = id => id == "900" ? (155L << 20) / 2 : 0;
        ln.Step(106.0, 2);
        Check($"line: the progress from the staging folders ({sent[^1]})", sent[^1].Contains("50 percent (78 MB of 155 MB)") || sent[^1].Contains("50 percent (77 MB of 155 MB)"));
        ln.Staged = id => id == "900" ? 155L << 20 : 0;
        ln.Step(108.5, 2);
        Check($"line: the bytes in the size the list gives, never more than the whole ({sent[^1]})", sent[^1].Contains("99 percent (155 MB of 155 MB)"));
        Check("line: a size in the units the HUD shows", Downloads.SizeWords(464L << 20) == "464 MB" && Downloads.SizeWords(3L << 29) == "1.5 GB");
        ln.ConsoleLine("11/01 08:46:30 [Workshop] Updating 900: state 5", 107.0);
        var n = sent.Count;
        ln.Step(110.0, 2);
        Check("line: state 5 ends it", sent.Count == n && ln.ActiveDownloads.Count == 0);
        ln.ConsoleLine("11/01 [MultiAddonManager] Addon download started for 901", 200.0);
        ln.ConsoleLine("11/01 [MultiAddonManager] Downloading addon 901: 3.00/6.00 MB (50.00%)", 203.0);
        ln.Step(204.0, 1);
        Check($"line: MultiAddonManager's own bytes, an unlisted id by its number ({sent[^1]})", sent[^1].Contains("Workshop item 901") && sent[^1].Contains("50 percent"));
        ln.ConsoleLine("11/01 [MultiAddonManager] Addon 901 downloaded successfully", 205.0);
        Check("line: its success ends it", ln.ActiveDownloads.Count == 0);
        ln.Step(300.0, 1, new HashSet<string> { "902" });
        ln.Started("902", 290.0);
        n = sent.Count;
        ln.Step(300.0, 1, new HashSet<string> { "902" });
        Check("line: never about the watcher's own downloads", sent.Count == n);
        ln.Finished("902");
        ln.Started("900", 400.0);
        ln.Failed("900", "No Connection", 401.0);
        ln.Step(402.0, 3);
        Check($"line: a failure says so once, and what to do ({sent[^1]})", sent[^1].StartsWith("gg_notice 10 ") && sent[^1].Contains("could not download Bastion: No Connection"));
        n = sent.Count;
        ln.Step(405.0, 3);
        Check("line: then nothing more", sent.Count == n);
        ln.ConsoleLine("nothing about downloads here", 500.0);
        ln.ConsoleLine("[Workshop] Updating 903: state 1", 500.0);
        ln.ConsoleLine("[Workshop] Updating 903: state 1", 500.0);
        ln.ConsoleLine("[Workshop] Loading custom game \"903\" with map \"x\"", 500.0);
        Check("line: a cached map's load leaves nothing behind", ln.ActiveDownloads.Count == 0);
        ln.Staged = _ => 0;
        ln.Started("905", 600.0);
        ln.Step(604.0, 2, new HashSet<string> { "906" });
        Check($"line: a map a player waits for behind a background download says so ({sent[^1]})", sent[^1].Contains("finishing the background download of Workshop item 906 first - Then Workshop item 905"));
        ln.Finished("905");
        ln.ConsoleLine("11/05 [Workshop] Downloading 907 (0 bytes / 335.80 MB)", 700.0);
        ln.Step(704.0, 2, new HashSet<string> { "906" });
        Check($"line: the engine's own progress line, nothing of it yet: still behind the background download ({sent[^1]})",
              sent[^1].Contains("finishing the background download of Workshop item 906 first - Then Workshop item 907"));
        ln.ConsoleLine("11/05 [Workshop] Downloading 907 (167.90 MB / 335.80 MB)", 705.0);
        ln.Step(707.0, 2, new HashSet<string> { "906" });
        Check($"line: the engine's own progress, exact - not the lists' size or the staging folder ({sent[^1]})",
              sent[^1].Contains("Server is downloading Workshop item 907: 50 percent (160 MB of 320 MB)"));
        ln.ConsoleLine("11/05 [Workshop] Updating 907: state 5", 708.0);
        Check("line: the engine's own download ends with its state 5", ln.ActiveDownloads.Count == 0);
    }

    /// <summary>P1. the Web API door: the update check, the watcher's only Web API call - each at least the gap after the last one ended.</summary>
    static void Door()
    {
        var clock = new FakeClock(At(5, 12, 0, 0));
        var asked = new List<double>();
        var valve = new ValveApi(new WebApiDoor(2, clock), _ =>
        {
            asked.Add(clock.T);
            clock.Sleep(0.4);                     // the call itself takes a moment of the fake clock
            return (200, "{\"response\":{\"success\":true,\"up_to_date\":true,\"version_is_listable\":true}}");
        });
        var verdicts = Enumerable.Range(0, 3).Select(_ => valve.Check(14188)).ToList();
        var gaps = asked.Zip(asked.Skip(1), (a, b) => Math.Round(b - (a + 0.4), 2)).ToList();
        Check($"P1: the update check through the one door - three in a row, each at least 2 s after the last ended (gaps {Show(gaps)}), all answered",
              asked.Count == 3 && gaps.All(g => g >= 2) && verdicts.All(v => v.Answer == "current"));
    }

    static string Wslog => Scenario.Template.Paths.WorkshopLog;
    static void Ws(params string[] lines) => File.AppendAllText(Wslog, string.Concat(lines.Select(l => l + "\n")));

    /// <summary>A player's download that ends "No Connection": the process is marked broken at once (a retry in the same process cannot work).</summary>
    static void BrokenDownload(Scenario s)
    {
        s.Step(5);
        Ws("[x] [AppID 730] Download item 998 requested by app", "[x] [AppID 730] Download item 998 result : No Connection");
        s.Step(3);
    }

    /// <summary>R. downloads broken in the engine process -> a "workshop" restart: only at 0 humans, the clean quit, limits.</summary>
    static void DownloadRestart()
    {
        var logs = Dir("wslog1");
        Scenario.Paths(p => p with { Logs = logs });
        File.WriteAllText(Wslog, "");
        void Off(ServerConfig c) => (c.Daily, c.Updates) = (null, false);
        var s = Scenario.Make(At(4, 15, 0, 0), tune: Off);
        (s.Eng.Hud, s.Eng.Humans) = (true, 1);
        BrokenDownload(s);
        Check("R: a player's download that ends 'No Connection' marks the process broken", s.W.Workshop.Broken is not null);
        s.Step(120);
        Check("R: with a human on, no restart and no restart line", !s.Has("quit") && !s.Eng.Notices.Any(x => x.Text.Contains("Workshop downloads are broken")));
        s.Eng.Humans = 0;
        s.Step(10);
        Check("R: the first moment nobody is on: a line for the log, then the clean quit", s.Has("quit") && s.Eng.Notices.Any(x => x.Text.Contains("Workshop downloads are broken")));
        Check("R: counted as a workshop restart", s.W.State.Restarts.Select(r => r.Kind).SequenceEqual(["workshop"]) && s.W.State.Pending?.Kind == "workshop");
        // R2. the limits: one 1 h ago -> warn only; three in 24 h -> warn only
        foreach (var (ago, label) in new[] { (new long[] { 3600 }, "one 1 h ago"), (new long[] { 9 * 3600, 12 * 3600, 15 * 3600 }, "three in 24 h") })
        {
            s = Scenario.Make(At(4, 15, 0, 0), tune: Off);
            var now = (long)s.Clock.T;
            s.W.State.Restarts = [.. ago.Select(a => new Restart(now - a, "workshop"))];
            s.Eng.Humans = 0;
            BrokenDownload(s);
            s.Step(60);
            Check($"R2: {label} -> no restart, only a warning", !s.Has("quit") && s.W.Workshop.Broken is not null);
        }
        // R2b. the gate's warning comes once per change, not once a second: it names WHEN the restart it waits on was, never how long ago
        s = Scenario.Make(At(4, 15, 0, 0), tune: Off);
        s.W.State.Restarts = [new Restart((long)s.Clock.T - 60, "daily")];
        s.Eng.Humans = 0;
        BrokenDownload(s);
        s.Step(180);
        var gate = s.Log.Lines.Where(l => l.Contains("NOT restarting")).ToList();
        Check($"R2b: a planned restart 1 min before the breakage -> the warning once in 3 min, naming when it was ({gate.Count} line(s): {Show(gate.Take(2))})",
              gate.Count == 1 && gate[0].Contains("planned restart was at ") && !s.Has("quit"));
        var z = Scenario.Paris;
        Check("R2b: every gate names a moment, never an age",
              !Rules.PlannedBlock(new WatchState { Restarts = [new Restart(1000 - 100, "daily")] }, 1000, z)!.Value.Why.Contains(" ago")
              && !Rules.FreezeBlock(new WatchState { Restarts = [new Restart(1000 - 100, "freeze")] }, 1000, z)!.Value.Why.Contains(" ago")
              && !Rules.WorkshopBlock(new WatchState { Restarts = [new Restart(1000 - 100, "daily")] }, 1000, z)!.Contains(" ago")
              && !Rules.WorkshopBlock(new WatchState { Restarts = [new Restart(1000 - 3600, "workshop")] }, 1000, z)!.Contains(" ago"));
        // R3. a download that works later clears the flag
        s = Scenario.Make(At(4, 15, 0, 0), tune: Off);
        (s.Eng.Hud, s.Eng.Humans) = (true, 1);
        BrokenDownload(s);
        Ws("[x] [AppID 730] Download item 999 result : OK");
        s.Step(3);
        Check("R3: an OK download clears the broken flag", s.W.Workshop.Broken is null);
    }

    const string CancelManifests = "[x] [AppID 730] Update canceled: Failed downloading 306 manifests (No connection)";
    const string CancelChunk = "[x] [AppID 730] Update canceled: Failed updating depot 730 while downloading chunk \"a5e8\" (Failure) (Content unavailable) \"No Download Source\"";
    const string CancelEmpty = "[x] [AppID 730] Update canceled: Failed updating depot 730 while starting download (No connection to content servers)";

    /// <summary>S-T: the stuck state: no futile retry, the evidence saved; no prefetch restart any more.</summary>
    static void Stuck()
    {
        Check("S: the cancel line parses as its own event",
              Downloads.Events([CancelManifests]).SequenceEqual([new DownloadEvent("", "canceled", "Failed downloading 306 manifests (No connection)")]));
        Check("S: the three stuck signatures (result or cancel line)",
              Downloads.Stuck("No Connection") && Downloads.Stuck("Failure", CancelChunk) && Downloads.Stuck("Failure", CancelEmpty) && Downloads.Stuck("Failure", CancelManifests));
        Check("S: other failures are not the stuck state", !Downloads.Stuck("Failure", null) && !Downloads.Stuck("Busy", "Failed updating depot 730 (Disk write failure)"));
        var (state, fault) = WatchState.FromJson("{\"schema\": 2, \"restarts\": [[100, \"prefetch\"]], \"prefetch_restart_day\": \"2026-10-22\", \"prefetch_day\": \"2026-10-22\", \"pending\": {\"kind\": \"prefetch\", \"at\": 100}}");
        Check("S: a state file from before the prefetch went - a prefetch restart in its history and pending, the prefetch's days - still loads "
              + "(a fault would stop every restart); the days are dropped",
              fault is null && state.Restarts.SequenceEqual([new Restart(100, "prefetch")]) && state.Pending?.Kind == "prefetch" && !state.ToJson().Contains("prefetch_day"));
        Check("S: a prefetch restart in the history is no planned kind any more: it neither fills the cap nor holds the floor",
              Rules.PlannedBlock(new WatchState { Restarts = [new Restart(1000 - 100, "prefetch")] }, 1000, Scenario.Paris) is null
              && !Kinds.Planned.Contains("prefetch") && !Kinds.EmptyOnly.Contains("prefetch"));

        var logs = Dir("steamlogs-src");
        var depot = Dir("depotcache");
        foreach (var name in new[] { "730_1.manifest", "730_2.manifest" })
            File.WriteAllText(Path.Combine(depot, name), "");
        File.WriteAllText(Path.Combine(logs, "content_log.txt"),
            "[x] Got 31 download sources and 0 caching proxies via ContentServerDirectoryService\n"
            + "[x] HTTPS (SteamCache,1) - a: a/depot/730/manifest/1/5/2 - manifest request received 503 (Service Unavailable) HTTP response\n"
            + "[x] No target sources connected\n");
        Scenario.Paths(p => p with { Logs = logs, Depotcache = depot });
        File.WriteAllText(Wslog, "");
        void Off(ServerConfig c) => (c.Daily, c.Updates) = (null, false);

        // O4. a download that ends "No Connection" marks the process broken AT ONCE, and the Steam client's logs are kept
        var s = Scenario.Make(At(4, 15, 0, 0), tune: Off);
        (s.Eng.Hud, s.Eng.Humans) = (true, 1);
        s.Step(5);
        Ws("[x] [AppID 730] Download item 998 requested by app", CancelManifests, "[x] [AppID 730] Download item 998 result : No Connection");
        s.Step(3);
        var snapDir = s.W.Config.Paths.SnapshotDir;
        var snaps = Directory.Exists(snapDir) ? Directory.EnumerateDirectories(snapDir).OrderBy(x => x, StringComparer.Ordinal).ToList() : [];
        Check("O4: 'No Connection' -> broken at once", s.W.Workshop.Broken is not null);
        var summary = snaps.Count > 0 ? File.ReadAllText(Path.Combine(snaps[0], "summary.txt")) : "";
        Check($"O4: one snapshot of the Steam logs, with a summary ({Show(snaps.Select(Path.GetFileName))})",
              snaps.Count == 1 && File.Exists(Path.Combine(snaps[0], "content_log.txt")) && summary.Contains("No target sources connected: 1")
              && summary.Contains("HTTP 503: 1") && summary.Contains("2 manifests"));
        s.Step(660);
        Check("O4: still one snapshot ten minutes later", Directory.EnumerateDirectories(snapDir).Count() == 1);
        s.Eng.Humans = 0;
        s.Step(10);
        Check("O4: ... and the download restart follows at 0 humans", s.Has("quit") && s.W.State.Restarts.Select(r => r.Kind).SequenceEqual(["workshop"]));
        // O5. "Failure" with a "No Download Source" cancel line is the stuck state too; a disk error is not
        foreach (var (cancel, stuck, label) in new[] { (CancelChunk, true, "chunk 'No Download Source'"),
                                                       ("[x] [AppID 730] Update canceled: Failed updating depot 730 (Disk write failure)", false, "a disk write failure") })
        {
            s = Scenario.Make(At(4, 15, 0, 0), tune: Off);
            (s.Eng.Hud, s.Eng.Humans) = (true, 1);
            s.Step(5);
            Ws("[x] [AppID 730] Download item 997 requested by app", cancel, "[x] [AppID 730] Download item 997 result : Failure");
            s.Step(3);
            Check($"O5: {label} -> {(stuck ? "broken at once" : "not broken")}", stuck ? s.W.Workshop.Broken is not null : s.W.Workshop.Broken is null);
        }
        // T. no restart before a prefetch any more, and no prefetch: from 03:20 to 03:35, nobody on, an old engine - nothing
        s = Scenario.Make(At(13, 3, 20, 0));
        s.Step(900);
        Check("T: 03:25 and 03:30 with nobody on -> no restart and no download",
              !s.Has("quit") && !s.Eng.Cmds.Any(c => c.StartsWith("mm_download_addon", StringComparison.Ordinal)) && s.W.State.Restarts.Count == 0);
        Check("T: no warm-up either: a new engine process downloads nothing of its own at its start",
              !s.Eng.Cmds.Any(c => c.StartsWith("mm_download_addon", StringComparison.Ordinal) || c.StartsWith("host_workshop_map", StringComparison.Ordinal)));
    }

    /// <summary>What the engine's Steam client does after a download: the acf names it, its folder is there.</summary>
    static void Landed(string id)
    {
        var acf = Path.Combine(WsDir, "appworkshop_730.acf");
        var text = File.ReadAllText(acf);
        const string head = "\t\"WorkshopItemsInstalled\"\n\t{\n";
        var entry = $"\t\t\"{id}\"\n\t\t{{\n\t\t\t\"size\"\t\t\"1\"\n\t\t\t\"timeupdated\"\t\t\"1\"\n\t\t\t\"manifest\"\t\t\"1\"\n\t\t}}\n";
        var at = text.IndexOf(head, StringComparison.Ordinal) + head.Length;
        File.WriteAllText(acf, text[..at] + entry + text[at..]);
        File.SetLastWriteTimeUtc(acf, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000)));   // a new stamp, also within one second
        Directory.CreateDirectory(Path.Combine(WsDir, "content", "730", id));
    }

    static List<string> Downloads9(Scenario s) => s.Eng.Cmds.Where(c => c.StartsWith("mm_download_addon", StringComparison.Ordinal)).ToList();

    /// <summary>J9. job 9: the queued maps downloaded in the background - the open ballot's first, then the queues' entries in play order;
    /// one at a time; silent; never beside a download somebody waits for; the bridge told what is on disk.</summary>
    static void Job9()
    {
        var j9 = Dir("j9data");
        File.WriteAllText(Path.Combine(j9, "queues.json"), "{\"version\": 1, \"random\": {\"armsrace-new\": [\"901\", \"902\"], \"armsrace-original\": [\"903\"], "
                                                           + "\"deathmatch\": [\"904\", \"de_dust2\"], \"wingman\": [\"905\"]}, \"requested\": [\"900\"]}");
        File.WriteAllText(Path.Combine(WsDir, "appworkshop_730.acf"), Acf.Replace("\"111\"", "\"901\""));   // 901 installed, with its folder: on disk
        Directory.CreateDirectory(Path.Combine(WsDir, "content", "730", "901"));
        var hold = false;
        Scenario.NewBridge = () => new FakeBridge { Reply = (what, _) => what == "ready" ? "ok: 1 on disk" + (hold ? "; hold: the vote is open" : "") : "ok" };
        Scenario.Paths(p => p with { BridgeData = j9 });
        var s = Scenario.Make(At(4, 15, 0, 0));
        (s.Eng.Hud, s.Eng.Humans) = (true, 2);
        s.Step(30);
        Check($"J9: the request first: {Show(Downloads9(s))}", Downloads9(s).SequenceEqual(["mm_download_addon 900"]));
        Check("J9: the bridge hears which queued maps are on disk, at its /ready/<secret>",
              s.Bridge.Posts.Count > 0 && s.Bridge.Posts[0] == ("ready", "gg-ready\t1\n901\n"));
        s.Eng.Write("[MultiAddonManager] Addon download started for 900");
        s.Eng.Write("[MultiAddonManager] Downloading addon 900: 10.00/100.00 MB (10.00%)");
        s.Step(10);
        Check("J9: silent - no news line about it, with players on", s.Eng.Notices.Count == 0);
        Ws("[x] [AppID 730] Download item 900 requested by app", "[x] [AppID 730] Download item 900 result : OK");
        Landed("900");
        s.Step(25);
        Check($"J9: one at a time - after its result the next missing one, in play order (903, not 902): {Show(Downloads9(s))}",
              Downloads9(s).SequenceEqual(["mm_download_addon 900", "mm_download_addon 903"]));
        s.Eng.Write("[Workshop] Updating 903: state 1");
        s.Step(10);
        Check($"J9: a player's pick of the map it is fetching: the line shows it ({Show(s.Eng.Notices.TakeLast(1))})", s.Eng.Notices.Any(x => x.Text.Contains("903")));
        Ws("[x] [AppID 730] Download item 903 result : OK");
        Landed("903");
        s.Eng.Write("[Workshop] Loading custom game \"903\" with map \"x\"");
        s.Eng.Write("[Workshop] Updating 999: state 1");
        s.Step(30);
        Check($"J9: while a download somebody waits for runs, nothing new starts: {Show(Downloads9(s))}", Downloads9(s).SequenceEqual(["mm_download_addon 900", "mm_download_addon 903"]));
        s.Eng.Write("[Workshop] Updating 999: state 5");
        s.Step(25);
        Check($"J9: ... and once it is done, the next: {Show(Downloads9(s))}", Downloads9(s)[^1] == "mm_download_addon 904");
        hold = true;
        Ws("[x] [AppID 730] Download item 904 result : OK");
        Landed("904");
        s.Step(45);
        Check($"J9: the bridge says hold (a vote, a change counting down, a map loading): nothing new starts: {Show(Downloads9(s))}", Downloads9(s)[^1] == "mm_download_addon 904");
        hold = false;
        s.Step(25);
        Check($"J9: ... and once it does not, the next one goes (905): {Show(Downloads9(s))}", Downloads9(s)[^1] == "mm_download_addon 905");
        Ws("[x] [AppID 730] Download item 905 result : OK");
        Landed("905");
        s.Step(25);
        Check($"J9: ... then the one after it in play order (902, the second round): {Show(Downloads9(s))}", Downloads9(s)[^1] == "mm_download_addon 902");
        Ws("[x] [AppID 730] Download item 902 result : Busy");
        s.Step(25);
        Check($"J9: a failure is tried again later, and nothing is left to fetch now: {Show(Downloads9(s))}",
              Downloads9(s)[^1] == "mm_download_addon 902" && s.W.Workshop.Ready.FailedIds["902"].Tries == 1);
        s.W.Workshop.Ready.FailedIds["902"] = (1, 0);
        s.Step(25);
        Check($"J9: its retry: {Show(Downloads9(s))}", Downloads9(s)[^1] == "mm_download_addon 902");
        Ws("[x] [AppID 730] Download item 902 result : No Connection");
        s.Step(5);
        Check("J9: a stuck result marks the process broken (job 6), and nothing more starts in it", s.W.Workshop.Broken is not null);
        s.Step(60);
        Check("J9: ... nothing more", Downloads9(s)[^1] == "mm_download_addon 902" && Downloads9(s).Count(c => c == "mm_download_addon 902") == 2);
        Check("J9: the log push's address is read as the image templates it, for the check against bridge.url and bridge.secret",
              ServerConfig.LogPush("http:\\/\\/bridge:8080\\/log\\/d4ab") == ("http://bridge:8080", "d4ab") && ServerConfig.LogPush("http://bridge:8080/nolog") is null);
        Scenario.NewBridge = () => null;
    }

    /// <summary>X. A player picks a map while job 9 downloads one in the background. Steam runs one Workshop download at a time and one already
    /// running cannot be stopped. (1) The chosen map is the one job 9 is fetching: it becomes the player's download, with no second
    /// download, and the line shows its progress. (2) Job 9 is fetching another map: the change waits for it, the line says so ("finishing the
    /// background download of X first", with its percent), then shows the chosen map's progress. In both: no double download, no stuck change,
    /// and the hold is released afterwards. And (3) the ready report names the background download in flight, with its progress.</summary>
    static void Collisions()
    {
        var data = Dir("xdata");
        File.WriteAllText(Path.Combine(data, "maps.json"), JsonSerializer.Serialize(new[]
        {
            new { rank = 1, id = 910, name = "Chosen Too", sizeMb = 100 }, new { rank = 2, id = 911, name = "Next One", sizeMb = 100 },
            new { rank = 3, id = 920, name = "Background Map", sizeMb = 100 }, new { rank = 4, id = 921, name = "After That", sizeMb = 100 },
            new { rank = 5, id = 950, name = "The Pick", sizeMb = 100 },
        }));
        void Queues(params string[] lane) => File.WriteAllText(Path.Combine(data, "queues.json"),
            JsonSerializer.Serialize(new { version = 1, random = new Dictionary<string, string[]> { ["armsrace-new"] = lane }, requested = Array.Empty<string>() }));
        File.WriteAllText(Path.Combine(WsDir, "appworkshop_730.acf"), Acf);
        var hold = (string?)null;
        Scenario.NewBridge = () => new FakeBridge { Reply = (what, _) => what == "ready" ? "ok: 0 on disk" + (hold is null ? "" : "; hold: " + hold) : "ok" };
        Scenario.Paths(p => p with { BridgeData = data });

        // (1) the chosen map is the one job 9 is fetching
        Queues("910", "911");
        var s = Scenario.Make(At(4, 15, 0, 0));
        (s.Eng.Hud, s.Eng.Humans) = (true, 2);
        s.Step(30);
        Check($"X1: job 9 fetches the first queued map ({Show(Downloads9(s))})", Downloads9(s).SequenceEqual(["mm_download_addon 910"]));
        s.Eng.Write("[MultiAddonManager] Addon download started for 910");
        s.Eng.Write("[MultiAddonManager] Downloading addon 910: 30.00/100.00 MB (30.00%)");
        s.Step(4);
        hold = "a map change is counting down";            // a player picked 910: the bridge's change counts down, then the engine asks for it
        Ws("[x] [AppID 730] Download item 910 requested by app");
        s.Eng.Write("[Workshop] Updating 910: state 1");
        s.Step(6);
        Check($"X1: the chosen map is the one job 9 fetches: it is the player's download now, and the line shows its progress ({Show(s.Eng.Notices.TakeLast(1).Select(x => x.Text))})",
              s.Eng.Notices.Count > 0 && s.Eng.Notices[^1].Text.Contains("Server is downloading Chosen Too: 30 percent"));
        s.Step(30);
        Check($"X1: ... no second download of it, and no new one beside it ({Show(Downloads9(s))})",
              Downloads9(s).SequenceEqual(["mm_download_addon 910"]) && !s.Eng.Cmds.Any(c => c.StartsWith("host_workshop_map", StringComparison.Ordinal)));
        Ws("[x] [AppID 730] Download item 910 result : OK");
        s.Eng.Write("[MultiAddonManager] Addon 910 downloaded successfully");
        Landed("910");
        s.Eng.Write("[Workshop] Loading custom game \"910\" with map \"x\"");
        s.Step(5);
        Check("X1: ... once it is in, nothing hangs: no background download in flight, no line left", s.W.Workshop.Ready.Current is null && s.W.Workshop.Line.ActiveDownloads.Count == 0);
        s.Step(25);
        Check($"X1: ... the bridge's hold keeps the next one back ({Show(Downloads9(s))})", Downloads9(s).Count == 1);
        hold = null;
        s.Step(25);
        Check($"X1: ... and once it is released, the next queued map goes ({Show(Downloads9(s))})", Downloads9(s)[^1] == "mm_download_addon 911");

        // (2) job 9 is fetching another map
        Queues("920", "921");
        File.WriteAllText(Path.Combine(WsDir, "appworkshop_730.acf"), Acf);
        s = Scenario.Make(At(4, 16, 0, 0));
        (s.Eng.Hud, s.Eng.Humans) = (true, 2);
        s.Step(30);
        Check($"X2: job 9 fetches the first queued map ({Show(Downloads9(s))})", Downloads9(s).SequenceEqual(["mm_download_addon 920"]));
        s.Eng.Write("[MultiAddonManager] Addon download started for 920");
        s.Eng.Write("[MultiAddonManager] Downloading addon 920: 63.00/100.00 MB (63.00%)");
        s.Step(12);                                          // the next look reports it
        var reported = s.Bridge.Bodies("ready").LastOrDefault() ?? "";
        Check($"X3: the ready report names the background download in flight, with its percent and title ({reported.Replace("\n", " | ").Replace('\t', ' ')})",
              reported.Contains("current\t920\t63\tBackground Map\n"));
        hold = "a map change is counting down";
        Ws("[x] [AppID 730] Download item 950 requested by app");
        s.Eng.Write("[Workshop] Updating 950: state 1");
        s.Step(6);
        Check($"X2: another map is chosen while job 9 fetches: the line says the background download finishes first, with its progress ({Show(s.Eng.Notices.TakeLast(1).Select(x => x.Text))})",
              s.Eng.Notices.Count > 0 && s.Eng.Notices[^1].Text.Contains("finishing the background download of Background Map first (63 percent) - Then The Pick"));
        s.Step(30);
        Check($"X2: ... no second download beside it, and none of the chosen map by the watcher ({Show(Downloads9(s))})",
              Downloads9(s).SequenceEqual(["mm_download_addon 920"]));
        Ws("[x] [AppID 730] Download item 920 result : OK");
        s.Eng.Write("[MultiAddonManager] Addon 920 downloaded successfully");
        Landed("920");
        s.W.Workshop.Line.Staged = id => id == "950" ? 50L << 20 : 0;   // the chosen map's own download has begun
        s.Step(6);
        Check($"X2: ... once the background one is in, the line shows the chosen map's own progress ({Show(s.Eng.Notices.TakeLast(1).Select(x => x.Text))})",
              s.Eng.Notices[^1].Text.Contains("Server is downloading The Pick: 50 percent"));
        var lastReport = s.Bridge.Bodies("ready").LastOrDefault() ?? "";
        Check("X3: ... and the report no longer names a background download", !lastReport.Contains("current\t"));
        Ws("[x] [AppID 730] Download item 950 result : OK");
        Landed("950");
        s.Eng.Write("[Workshop] Updating 950: state 5");
        s.Eng.Write("[Workshop] Loading custom game \"950\" with map \"x\"");
        s.Step(25);
        Check("X2: ... when that is in too, nothing hangs: no line, no background download in flight", s.W.Workshop.Line.ActiveDownloads.Count == 0 && s.W.Workshop.Ready.Current is null);
        hold = null;
        s.Step(25);
        Check($"X2: ... and once the hold is released, the next queued map goes: no stuck change ({Show(Downloads9(s))})", Downloads9(s)[^1] == "mm_download_addon 921");
        Scenario.NewBridge = () => null;
    }
}
