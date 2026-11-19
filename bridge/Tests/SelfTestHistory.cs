// --selftest, the match history (History.cs, HudHistory.cs): the record as the game's events make it, its file's lines and the plugin's block, the backfill
// read back from made-up day logs and a console log, the live bridge writing it, and the map browser's History view. Made-up players only.
static partial class Program
{
    static void HistorySelfTest(Action<string, bool> Check)
    {
        DateTimeOffset T(int h, int m, int s = 0) => new(2026, 11, 20, h, m, s, TimeSpan.FromHours(1));
        var got = new List<HudHistory.Entry>();

        // ---- the record ----
        var t = new MatchTracker { Finished = got.Add };
        t.MapStarted(T(21, 0), "de_alpha", 3000000001, "Alpha", "wingman");
        t.Entered(T(21, 0, 20), 1001, "Alice");
        t.Entered(T(21, 1), 1002, "Bob");
        t.MatchEnded(T(21, 11), "scrimcomp2v2 mg_x de_alpha score 9:4 after 10 min");
        var first = got.Count == 1 ? got[0] : null;
        Check("History: a match to its end screen is one entry - its map, mode and score, each human with their time in it",
            first is { MapId: 3000000001, MapName: "Alpha", Mode: "wingman", Score: "9:4", EndedBy: HudHistory.ByMatchEnd, Missing.Count: 0 }
            && first.Players.Count == 2 && first.Players[0] is { Account: 1001, Name: "Alice", Seconds: 640 } && first.Players[1] is { Account: 1002, Seconds: 600 }
            && first.Start == T(21, 0) && first.End == T(21, 11) && first.From == T(21, 0, 20));
        t.Left(T(21, 15), 1002);
        t.Entered(T(21, 16), 1003, "Cara");
        t.MatchEnded(T(21, 22), "scrimcomp2v2 mg_x de_alpha score 3:9 after 10 min");
        var second = got.Count == 2 ? got[1] : null;
        Check("...played again, the next match on that map runs from the end screen, with whoever was in it and for how long",
            second is { MapId: 3000000001, Score: "3:9" } && second.Start == T(21, 11)
            && second.Players.Select(p => (p.Account, p.Seconds)).SequenceEqual([(1001L, 660), (1002L, 240), (1003L, 360)]));
        t.MapStarted(T(21, 23), "de_beta", 3000000002, "Beta", "wingman");
        Check("...the end screen alone before a change is no match", got.Count == 2);
        t.Entered(T(21, 23, 30), 1004, "Dan");
        t.Restarted(T(21, 23, 40), fair: true);
        t.Restarted(T(21, 30), fair: false);
        var third = got.Count == 3 ? got[2] : null;
        Check("...a fairness restart is the same match, a player's restart after its first minute ends it; after a change only who entered again is in",
            third is { MapName: "Beta", EndedBy: HudHistory.ByRestart } && third.Start == T(21, 23) && third.Players.Count == 1 && third.Players[0] is { Account: 1004, Seconds: 390 });
        t.MapStarted(T(21, 40), "ar_gamma", 0, "ar_gamma", "armsrace");
        var fourth = got.Count == 4 ? got[3] : null;
        t.Entered(T(21, 40, 10), 1001, "Alice");
        t.ServerDown(T(21, 50));
        var fifth = got.Count == 5 ? got[4] : null;
        Check("...a map change ends the match that was on (Beta's after the restart, from it), and the server going down the next",
            fourth is { EndedBy: HudHistory.ByMapChange, MapName: "Beta" } && fourth.Start == T(21, 30) && fourth.Players[0].Seconds == 600
            && fifth is { EndedBy: HudHistory.ByServerStop, MapId: 0, MapName: "ar_gamma", Mode: "armsrace" } && fifth.Players.Count == 1 && fifth.Players[0].Seconds == 590);
        t.MapStarted(T(22, 0), "de_delta", 3000000004, "Delta", "wingman");
        t.MapStarted(T(22, 10), "de_eps", 3000000005, "Eps", "wingman");
        t.Entered(T(22, 10, 5), 1001, "Alice");
        t.MapStarted(T(22, 10, 50), "de_zeta", 3000000006, "Zeta", "wingman");
        Check("...a match nobody human was in is no entry, nor one they were in for under a minute", got.Count == 5);
        var u = new MatchTracker { Finished = got.Add };
        u.Entered(T(23, 0), 1002, "Bob");
        u.Seen(T(23, 1), 1003, "Cara");
        u.MatchEnded(T(23, 9), "gungameprogressive mg_x ar_x score 0:0 after 20 min");
        var sixth = got.Count == 6 ? got[5] : null;
        Check("...a record that begins inside a match says its start and players are not known; Arms Race's 0:0 is no score",
            sixth is { Mode: "armsrace", Score: "" } && sixth.Missing.SequenceEqual(["players", "start"]) && sixth.Players.Count == 2);
        var v = new MatchTracker { Finished = got.Add };
        v.MapStarted(T(23, 30), "de_eta", 3000000007, "Eta", "wingman");
        v.Entered(T(23, 31), 1001, "Alice");
        v.Gap(T(23, 35), T(23, 37));
        v.Gap(T(23, 40), T(23, 59));
        var seventh = got.Count == 7 ? got[6] : null;
        Check("...a short hole in the record leaves the match on, who and how long not known in full; a long one ends it where the record stopped, its end not known",
            seventh is { EndedBy: HudHistory.ByUnknown } && seventh.End == T(23, 40) && seventh.Missing.SequenceEqual(["end", "players", "times"]) && seventh.Players[0].Seconds == 540);
        var w = new MatchTracker { Finished = got.Add };
        w.MapStarted(T(1, 0), "de_theta", 3000000008, "Theta", "wingman");
        w.Entered(T(1, 1), 1002, "Bob");
        w.Left(T(1, 20), 1002);
        var finished = w.Finish(T(3, 0));
        var last = got.Count == 8 ? got[7] : null;
        Check("...at the record's end a match still on is an entry, ending at the last human's leaving, its end not known",
            finished && last is { EndedBy: HudHistory.ByUnknown } && last.End == T(1, 20) && last.Missing.SequenceEqual(["end"]) && !w.Finish(T(3, 1)));

        // ---- the file's lines, the plugin's block, the words ----
        var odd = first! with { Players = [new HudHistory.Player(1009, "Ann \"the\" Öst\t1", 61, first.Start)] };
        var back = HudHistory.FromJsonLine(HudHistory.ToJsonLine(odd));
        var block = new HudHistory { Version = 7, Entries = [second!, first] };
        var decoded = HudHistory.Decode(block.Encode());
        Check("History: an entry's line reads back the same - any name, its time's offset - and the plugin's block carries the newest first with its version",
            back != null && back.Players[0].Name == "Ann \"the\" Öst\t1" && back.Start == odd.Start && back.Start.Offset == TimeSpan.FromHours(1) && back.MapId == odd.MapId
            && back.Score == "9:4" && back.Missing.Count == 0 && HudHistory.FromJsonLine("not json") == null && HudHistory.FromJsonLine("{\"v\":2}") == null
            && decoded is { Version: 7 } && decoded.Entries.Count == 2 && decoded.Entries[0].End == second!.End && HudHistory.Decode("nonsense") == null);
        Check("...its words: the day and span from when its humans were in, who longest first, its length and Wingman's score, a mark for what is not known",
            HudHistory.When(first) == "Fri 20 Nov  ·  21:00 - 21:11" && HudHistory.Who(first) == "Alice and Bob"
            && HudHistory.Who(first with { Players = Enumerable.Range(1, 6).Select(i => new HudHistory.Player(i, $"P{i}", 100 - i, first.Start)).ToList() }) == "P1, P2, P3, P4 and 2 more"
            && HudHistory.Note(first) == "11 min  ·  9:4" && HudHistory.Note(sixth!) == "9 min  ·  from the logs, partly"
            && HudHistory.When(last! with { End = T(1, 20).AddDays(1) }).EndsWith("01:01 - Sat 01:20", StringComparison.Ordinal));
        var stateLine = new HudState { HistoryVersion = 42 };
        Check("...the live state names the block's version, and a state without it names none", HudState.Decode(stateLine.Encode())?.HistoryVersion == 42
            && HudState.Decode(new HudState().Encode())?.HistoryVersion == 0);
        Check("A dislike's reason for a map that took the server down: ready, carried by the codecs as itself (an older plugin reads a dislike); it joins the popup with its button (os_why_7) in the HUD content that has it",
            DownReasons.Crash is { Word: "crash", Value: HudCatalog.CrashVote, Label: "It crashed the server" } && HudCatalog.Carried(HudCatalog.CrashVote) == HudCatalog.CrashVote
            && HudCatalog.Carried(-8) == -1 && !DownReasons.All.Contains(DownReasons.Crash));

        // ---- the backfill ----
        const string Day = "2026-11-20", Slash = "11/20/2026"; // a day log's lines and a console's are built from their parts (no date beside a time in this file)
        string ConsoleLine(string time) => $"11/20 {time} [Server] L {Slash} - {time}: ";
        string[] dayLog =
        [
            Day + " 20:00:00 settings: the environment variable OVERSTRIKE2_SETTINGS_CONTENT",
            Day + " 20:10:00 os_mode wingman;game_type 0;game_mode 2 -> os_mode: mode wingman: on already",
            Day + " 20:10:00 host_workshop_map 3000000001 -> ",
            Day + " 20:10:01 MapStart   de_alpha [X-Game-Map: de_alpha]",
            Day + " 20:10:01 playing: #12 Alpha  ·  200 MB  ·  Picked by the vote",
            Day + " 20:10:01 MapStart [X-Game-Map: de_alpha]",
            Day + " 20:10:05 Connected Alice [U:1:1001]",
            Day + " 20:10:08 Entered Alice [U:1:1001]",
            Day + " 20:10:30 Entered Bob [U:1:1002]",
            Day + " 20:10:40 rematch: The match restarts for fairness in 5 s - Bob is in",
            Day + " 20:10:45 rematch by Bob: os_restart 1 -> os_restart: the match restarts in 1 s",
            Day + " 20:20:00 MatchEnd   scrimcomp2v2 mg_ar_shoots de_alpha score 9:7 after 9 min",
            Day + " 20:21:00 host_workshop_map 3000000002 -> ",
            Day + " 20:21:01 Disconnected Alice [U:1:1001] NETWORK_DISCONNECT_SHUTDOWN",
            Day + " 20:21:01 Disconnected Bob [U:1:1002] NETWORK_DISCONNECT_SHUTDOWN",
            Day + " 20:21:02 MapStart   de_beta [X-Game-Map: de_beta]",
            Day + " 20:21:02 playing: Beta  ·  1.0 GB  ·  Picked by the vote",
            Day + " 20:21:05 Entered Alice [U:1:1001]",
            Day + " 20:30:00 Disconnected Alice [U:1:1001] NETWORK_DISCONNECT_DISCONNECT_BY_USER",
            Day + " 20:40:00 MapStart   ar_shoots [X-Game-Map: ar_shoots]",
            Day + " 20:40:00 playing: ar_shoots",
            Day + " 20:40:02 change: Server started - Rolling the dice",
            Day + " 20:40:03 os_mode armsrace -> os_mode: mode armsrace: on already",
            Day + " 20:41:00 Entered Cara [U:1:1003]",
            Day + " 20:45:00 Chat Cara [U:1:1003] gg",
        ];
        string[] consoleLog =
        [
            ConsoleLine("19:00:00") + "Loading map \"de_gamma\"",
            ConsoleLine("19:00:01") + "Started: \"\"",
            ConsoleLine("19:01:00") + "\"Dan<3><[U:1:1004]><>\" entered the game",
            ConsoleLine("19:12:00") + "Game Over: deathmatch mg_x de_gamma score 0:0 after 12 min",
            ConsoleLine("20:15:00") + "\"Eve<4><[U:1:1005]><>\" entered the game",
            "11/20 20:15:01 [Server] some other console line",
        ];
        var (filled, words) = Backfill([("bridge-2026-11-20.log", dayLog), ("console.log", consoleLog)], TimeZoneInfo.Utc, null);
        DateTimeOffset U(int h, int m, int s = 0) => new(2026, 11, 20, h, m, s, TimeSpan.Zero);
        Check($"History backfill: the day logs and the console's, in time order - {filled.Count} entries ({words})",
            filled.Count == 4 && words.StartsWith($"history backfill: 4 match(es) from {Day} 19:00 to {Day} 20:45, 2 of them incomplete, 4 human(s)", StringComparison.Ordinal));
        var (gamma, alpha, beta, shoots) = filled.Count == 4 ? (filled[0], filled[1], filled[2], filled[3]) : (null, null, null, null);
        Check("...a console's match: its map known by its file alone (no Workshop id), its mode from the Game Over line",
            gamma is { LogName: "de_gamma", MapName: "de_gamma", MapId: 0, Mode: "deathmatch", Source: "backfill" } && gamma.Missing.SequenceEqual(["map id"])
            && gamma.Players.Single() is { Account: 1004, Seconds: 660 });
        Check("...a day log's: the Workshop id the change sent, the name the server showed, the score, the fairness restart no split",
            alpha is { MapId: 3000000001, MapName: "Alpha", Mode: "wingman", Score: "9:7", EndedBy: HudHistory.ByMatchEnd } && alpha.Missing.Count == 0 && alpha.Start == U(20, 10, 1)
            && alpha.Players.Select(p => (p.Account, p.Seconds)).SequenceEqual([(1001L, 592), (1002L, 570)]));
        Check("...the end screen before the change is no match; a match the server's start ended says so",
            beta is { MapId: 3000000002, MapName: "Beta", EndedBy: HudHistory.ByServerStart } && beta.Players.Single() is { Account: 1001, Seconds: 535 });
        Check("...an official map has no Workshop id to miss, a mode said just after its start is its own, and the match on at the logs' end is written, its end not known",
            shoots is { MapId: 0, MapName: "ar_shoots", Mode: "armsrace", EndedBy: HudHistory.ByUnknown } && shoots.Missing.SequenceEqual(["end"]) && shoots.End == U(20, 45)
            && !filled.SelectMany(e => e.Players).Any(p => p.Account == 1005));
        var (until, _) = Backfill([("bridge-2026-11-20.log", dayLog)], TimeZoneInfo.Utc, U(20, 25));
        Check("...--until stops where the live record takes over", until.Count == 1 && until[0].MapName == "Alpha");

        // ---- the live bridge writes it ----
        var h = new Harness("history", new DateTime(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc));
        try
        {
            h.Fresh();
            var before = History.Version;
            h.Line("Loading map \"de_live\"");
            h.Join(("Alice", "[U:1:1001]"));
            h.Run(120);
            h.Join(("Bob", "[U:1:1002]"));
            h.Run(60);
            h.Leave(("Bob", "[U:1:1002]"));
            h.Run(60);
            h.MatchEnd();
            var lines = File.Exists(HistoryPath) ? File.ReadAllLines(HistoryPath) : [];
            var live = lines.Length == 1 ? HudHistory.FromJsonLine(lines[0]) : null;
            Check("History, live: the bridge's log stream makes the entry, appends it to history.jsonl and moves the plugin's copy",
                live is { LogName: "de_live", EndedBy: HudHistory.ByMatchEnd, Mode: "armsrace", Source: "live" } && live.Players.Select(p => (p.Account, p.Seconds)).SequenceEqual([(1001L, 240), (1002L, 60)])
                && History.Entries.Count == 1 && History.Version > before && History.Encode().Contains("de_live", StringComparison.Ordinal));
            File.AppendAllText(HistoryPath, "a line that is no entry\n");
            Check("...and a bridge reads it back at its start, newest first, a line that does not read skipped and said",
                LoadHistory().Contains("1 match(es)", StringComparison.Ordinal) && LoadHistory().Contains("1 line(s) that do not read skipped", StringComparison.Ordinal)
                && History.Entries.Count == 1 && History.Entries[0].LogName == "de_live");
        }
        finally { h.Dispose(); }

        // ---- the plugin's cost: a block at its most, decoded as the plugin takes it after every match end; the History view worked out over a full catalog ----
        var big = new HudHistory { Version = 9 };
        for (var i = 0; i < HudHistory.BlockMax; i++)
            big.Entries.Add(first! with
            {
                Start = first.Start.AddMinutes(-15 * i), End = first.End.AddMinutes(-15 * i), MapId = 3000000000 + i % 500, MapName = $"Map {i % 500}",
                Players = Enumerable.Range(0, 4).Select(k => new HudHistory.Player(2000 + k, $"Player number {k}", 600, first.Start)).ToList(),
            });
        var bigText = big.Encode();
        HudHistory.Decode(bigText); // the first, which also compiles it
        var decodes = Enumerable.Range(0, 5).Select(_ => { var sw = System.Diagnostics.Stopwatch.StartNew(); var d = HudHistory.Decode(bigText); sw.Stop(); return (Count: d?.Entries.Count ?? 0, Ms: sw.Elapsed.TotalMilliseconds); }).ToList();
        var decodeMs = decodes.Select(x => x.Ms).OrderBy(x => x).ElementAt(2);
        var bigCatalog = new HudCatalog();
        for (var i = 0; i < 9000; i++) bigCatalog.Maps.Add(new($"{3000000000 + i}", $"{i + 1}", i + 1, 3000000000 + i, $"Map {i}", 100, false, -1, 0, 0, 0, RankAr: i + 1));
        var viewTimes = Enumerable.Range(0, 5).Select(i => { var vb = new HudBrowser { History = true, Past = big, Page = i }; var sw = System.Diagnostics.Stopwatch.StartNew(); var n = vb.Matches(bigCatalog, 1001).Count; vb.PageOf(vb.Matches(bigCatalog, 1001)); vb.PastPage(); sw.Stop(); return (n, sw.Elapsed.TotalMilliseconds); }).ToList();
        var viewMs = viewTimes.Select(x => x.Item2).OrderBy(x => x).ElementAt(2);
        Console.WriteLine($"history cost: decode {HudHistory.BlockMax} matches ({bigText.Length / 1024} KB) {string.Join(", ", decodes.Select(x => x.Ms.ToString("0.0")))} ms; "
            + $"the view over 9000 maps {string.Join(", ", viewTimes.Select(x => x.Item2.ToString("0.0")))} ms");
        Check($"History: the plugin's block at its most ({HudHistory.BlockMax} matches, {bigText.Length / 1024} KB) decodes whole ({decodeMs:0.0} ms, median of 5, this build) - off the "
            + $"game thread (BridgeLink.TakeHistory); a player's History view over 9,000 maps takes {viewMs:0.0} ms, once for each change of the history or the catalog",
            decodes.All(x => x.Count == HudHistory.BlockMax) && viewTimes.All(x => x.n == HudHistory.BlockMax));

        // ---- the map browser's History view ----
        var catalog = new HudCatalog
        {
            Maps =
            [
                new("3000000001", "12", 12, 3000000001, "Alpha", 200, false, -1, 0, 3, 1, RankAr: 12, RankWm: 5),
                new("3000000002", "40", 40, 3000000002, "Beta", 1000, false, -1, 0, 0, 2, RankAr: 40, RankWm: 9),
                new("ar_shoots", "ar_shoots", 0, 0, "ar_shoots", 0, true, -1, 0, 1, 0, RankAr: 0),
            ],
        };
        catalog.Votes[1001] = new() { ["3000000001"] = 1, ["3000000002"] = -1 };
        var unlisted = alpha! with { MapId = 3999999999, MapName = "Gone Map", LogName = "de_gone", Players = [new HudHistory.Player(1005, "Eve", 300, alpha.Start)] };
        var past = new HudHistory { Version = 3, Entries = [shoots!, beta!, alpha, unlisted] }; // newest first
        var b = new HudBrowser { Past = past };
        Check("History view: the History button turns it on, from the first page; a search the chat sets turns it off again",
            b.Click(HudBrowser.HistoryButton, 0) && b.History && b.Page == 0 && b.Matches(catalog, 1001).Count == 4);
        var cards = b.Matches(catalog, 1001);
        var page = b.PageOf(cards);
        var onPage = b.PastPage();
        Check("...a card a match, newest first: each the map's own card - an official one by its name, one no list has now made under the name it had",
            cards.Select(c => c.Name).SequenceEqual(["ar_shoots", "Beta", "Alpha", "Gone Map"]) && cards[3] is { Key: "3999999999", Arg: "3999999999", Up: 0 }
            && onPage.Count == 4 && onPage[1] == beta && page.Count == 4);
        var (texts, classes) = HudMenu.Maps(catalog, new HudState(), b, 1001, DateTime.UtcNow, history: true);
        var (_, oldClasses) = HudMenu.Maps(catalog, new HudState(), b, 1001, DateTime.UtcNow);
        Check($"...only to a content that has it (level {HudBrowser.HistoryLevel}, after v4.14.0): to an older one nothing of it, so no game logs a panel it lacks",
            !oldClasses.Any(x => x.Id == HudBrowser.HistoryButton || x.Class is "hist" or "hinc") && HudBrowser.HasHistory("v4.14.0") == HudVersion.Level >= HudBrowser.HistoryLevel
            && !HudBrowser.HasHistory("v4.13.0"));
        string Text(string id) => texts.LastOrDefault(x => x.Id == id).Text ?? "";
        bool On(string id, string cls) => classes.LastOrDefault(x => x.Id == id && x.Class == cls).On;
        var hw = HudMenu.HistoryWords(b);
        Check("...the page: its count, the History button lit and hist on os_screen; each card's likes and the player's own vote as in the browser; the match lines as variables",
            Text("os_m_count") == "4 matches" && On(HudBrowser.HistoryButton, "on") && On(HudView.Screen, "hist") && Text("os_m2_name") == "Beta" && Text("os_m3_votes") == "+3  -1"
            && On("os_m3", "liked") && On("os_m2", "disliked") && !On("os_m1", "liked") && On("os_m1", "hinc") && !On("os_m3", "hinc")
            && hw.Count == 3 * HudMenu.Cards && hw.Contains(("osh3w", HudHistory.When(alpha))) && hw.Contains(("osh3p", "Alice and Bob")) && hw.Contains(("osh5w", "")));
        Check("...a card's Like, Dislike and Play are the browser's own, for that match's map: the one vote a player has on it",
            HudMenu.Command("os_m3_up", catalog, new HudState(), b, 1001, DateTime.UtcNow) == "!up 12" && HudMenu.Command("os_m2_down", catalog, new HudState(), b, 1001, DateTime.UtcNow) == "!down 40"
            && HudMenu.Command("os_m4_up", catalog, new HudState(), b, 1001, DateTime.UtcNow) == "!up 3999999999");
        b.Mode = "wingman";
        var wingman = b.Matches(catalog, 1001).Select(c => c.Name).ToList();
        b.Mode = ""; b.Liked = true;
        var liked = b.Matches(catalog, 1001).Select(c => c.Name).ToList();
        b.Liked = false; b.Query = "eve";
        var byPlayer = b.Matches(catalog, 1001).Select(c => c.Name).ToList();
        b.Query = ""; b.Descending = true;
        var oldest = b.Matches(catalog, 1001).Select(c => c.Name).FirstOrDefault();
        b.Descending = false;
        var (emptyTexts, _) = HudMenu.Maps(catalog, new HudState(), new HudBrowser { History = true, Past = new HudHistory { Version = 1 } }, 1001, DateTime.UtcNow, history: true);
        b.Find("dust");
        Check("...the filters the browser has: the mode, Liked, a search by the map's or a player's name, oldest first; an empty history says so",
            wingman.SequenceEqual(["Beta", "Alpha", "Gone Map"]) && liked.SequenceEqual(["Alpha"]) && byPlayer.SequenceEqual(["Gone Map"]) && oldest == "Gone Map"
            && emptyTexts.LastOrDefault(x => x.Id == "os_m_empty").Text == HudMenu.HistoryEmpty && !b.History && HudMenu.HistoryWords(b).All(x => x.Text == ""));
    }
}
