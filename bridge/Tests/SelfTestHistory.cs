// --selftest, the match history (History.cs, HudHistory.cs): the record as the game's events make it - with the warm-up and the frags - its file's lines
// and the plugin's block, the backfill read back from made-up day logs and a console log, the live bridge writing it, and the HISTORY page
// (HudHistoryPage). Made-up players only.
static partial class Program
{
    static void HistorySelfTest(Action<string, bool> Check)
    {
        DateTimeOffset T(int h, int m, int s = 0) => new(2026, 11, 20, h, m, s, TimeSpan.FromHours(1));
        var got = new List<HudHistory.Entry>();

        // ---- the record, from before the warm-up (the backfill's matches) ----
        var t = new MatchTracker { Finished = got.Add };
        t.MapStarted(T(21, 0), "de_alpha", 3000000001, "Alpha", "wingman");
        t.Entered(T(21, 0, 20), 1001, "Alice");
        t.Entered(T(21, 1), 1002, "Bob");
        t.MatchEnded(T(21, 11), "scrimcomp2v2 mg_x de_alpha score 9:4 after 10 min");
        var first = got.Count == 1 ? got[0] : null;
        Check("History: a match to its end screen is one entry - its map, mode and score, each human with their time in it, the most of them in it at once",
            first is { MapId: 3000000001, MapName: "Alpha", Mode: "wingman", Score: "9:4", EndedBy: HudHistory.ByMatchEnd, Missing.Count: 0, Most: 2, Winner: 0, Pairs: null }
            && first.Players.Count == 2 && first.Players[0] is { Account: 1001, Name: "Alice", Seconds: 640, Frags: null } && first.Players[1] is { Account: 1002, Seconds: 600 }
            && first.Start == T(21, 0) && first.End == T(21, 11) && first.From == T(21, 0, 20) && first.Key == "3000000001");
        t.Left(T(21, 15), 1002);
        t.Entered(T(21, 16), 1003, "Cara");
        t.MatchEnded(T(21, 22), "scrimcomp2v2 mg_x de_alpha score 3:9 after 10 min");
        var second = got.Count == 2 ? got[1] : null;
        Check("...played again, the next match on that map runs from the end screen, with whoever was in it and for how long",
            second is { MapId: 3000000001, Score: "3:9", Most: 2 } && second.Start == T(21, 11)
            && second.Players.Select(p => (p.Account, p.Seconds)).SequenceEqual([(1001L, 660), (1002L, 240), (1003L, 360)]));
        t.MapStarted(T(21, 23), "de_beta", 3000000002, "Beta", "wingman");
        Check("...the end screen alone before a change is no match", got.Count == 2);
        t.Entered(T(21, 23, 30), 1004, "Dan");
        t.Restarted(T(21, 23, 40), fair: true);
        t.Restarted(T(21, 30), fair: false);
        var third = got.Count == 3 ? got[2] : null;
        Check("...a fairness restart (before the warm-up) is the same match, a player's restart after its first minute ends it; after a change only who entered again is in",
            third is { MapName: "Beta", EndedBy: HudHistory.ByRestart } && third.Start == T(21, 23) && third.Players.Count == 1 && third.Players[0] is { Account: 1004, Seconds: 390 });
        t.MapStarted(T(21, 40), "ar_gamma", 0, "ar_gamma", "armsrace");
        var fourth = got.Count == 4 ? got[3] : null;
        t.Entered(T(21, 40, 10), 1001, "Alice");
        t.ServerDown(T(21, 50));
        var fifth = got.Count == 5 ? got[4] : null;
        Check("...a map change ends the match that was on (Beta's after the restart, from it), and the server going down the next",
            fourth is { EndedBy: HudHistory.ByMapChange, MapName: "Beta" } && fourth.Start == T(21, 30) && fourth.Players[0].Seconds == 600
            && fifth is { EndedBy: HudHistory.ByServerStop, MapId: 0, MapName: "ar_gamma", Mode: "armsrace" } && fifth.Players.Count == 1 && fifth.Players[0].Seconds == 590
            && fifth.Key == "ar_gamma");
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

        // ---- the warm-up: every map starts in it (WarmUp.cs, release 219); its match counts from its end ----
        var warm = new List<HudHistory.Entry>();
        var wu = new MatchTracker { Finished = warm.Add, UsesWarmUp = true, RecordsFrags = true };
        wu.MapStarted(T(18, 0), "de_warm", 3000000011, "Warm", "wingman");
        wu.Entered(T(18, 0, 10), 1001, "Alice");
        wu.Entered(T(18, 0, 40), 1002, "Bob");
        wu.Left(T(18, 1), 1002);
        wu.Entered(T(18, 1, 10), 1003, "Cara");
        wu.Killed(T(18, 1, 20), 1001, 1003, "ak47", "headshot", 10);
        var warming = wu.Warming && !wu.Running;
        wu.WarmUpEnded(T(18, 2));
        wu.Killed(T(18, 3), 1003, 1001, "deagle", "", 20);
        wu.MatchEnded(T(18, 12), "scrimcomp2v2 mg_x de_warm score 9:3 after 10 min");
        var warmed = warm.Count == 1 ? warm[0] : null;
        Check("History with the warm-up: the match counts from the warm-up's end - whoever joined during it in from that start, their seconds from it; one who left during it was never in; a kill in the warm-up no frag",
            warming && warmed is { Start.Hour: 18, Start.Minute: 2, Most: 2 } && warmed.Players.Select(p => (p.Account, p.Seconds, p.First)).SequenceEqual([(1001L, 600, T(18, 2)), (1003L, 600, T(18, 2))])
            && warmed.Players[0].Frags is { Kills: 0, Deaths: 1 } && warmed.Players[1].Frags is { Kills: 1, Hs: 0, Deaths: 0 } && warmed.Pairs is [{ Killer: 1003, Victim: 1001, Kills: 1 }]);
        wu.MapStarted(T(18, 13), "de_cold", 3000000012, "Cold", "wingman");
        wu.Entered(T(18, 13, 5), 1001, "Alice");
        wu.MapStarted(T(18, 18), "de_colder", 3000000013, "Colder", "wingman");
        wu.Entered(T(18, 18, 5), 1001, "Alice");
        wu.ServerDown(T(18, 22));
        Check("...a map that ends during its warm-up records no match: a map change, or the server going down", warm.Count == 1);
        wu.MapStarted(T(18, 30), "de_none", 3000000014, "None", "wingman");
        wu.Entered(T(18, 30, 5), 1001, "Alice");
        wu.NoWarmUp();
        wu.MatchEnded(T(18, 40), "scrimcomp2v2 mg_x de_none score 9:1 after 10 min");
        Check("...a map with no warm-up after all (a plugin without it) runs as CS2 has it, from the map's start as before",
            warm.Count == 2 && warm[1].Start == T(18, 30) && warm[1].Players.Single() is { Account: 1001, Seconds: 595 } && warm[1].Players[0].Frags is { Any: false });
        wu.MapStarted(T(18, 41), "de_unseen", 3000000015, "Unseen", "wingman");
        wu.Entered(T(18, 41, 5), 1002, "Bob");
        wu.MatchEnded(T(18, 51), "scrimcomp2v2 mg_x de_unseen score 9:6 after 10 min");
        Check("...a match end while the record still thinks the warm-up is on (its end went by unseen): the match is kept, from the map's start, its start marked not known",
            warm.Count == 3 && warm[2] is { MapName: "Unseen", Score: "9:6" } && warm[2].Start == T(18, 41) && warm[2].Missing.SequenceEqual(["start"]) && warm[2].Players.Single() is { Account: 1002, Seconds: 595 });
        wu.MapStarted(T(18, 52), "ar_wait", 3000000016, "Wait", "armsrace");
        wu.Entered(T(18, 52, 5), 1001, "Alice");
        wu.Entered(T(18, 52, 9), 1002, "Bob");
        wu.Killed(T(18, 53), 1001, 1002, "knife_t", "", 1); // the knife kill that wins the waiting time's Arms Race
        wu.MatchEnded(T(18, 53), "gungameprogressive mg_x ar_wait score 0:0 after 1 min", inWarmUp: true);
        var wonInWarmUp = warm.Count == 3 && wu.Running && wu.StartedAt == T(18, 53);
        wu.Killed(T(18, 54), 1002, 1001, "ak47", "", 15);
        wu.MatchEnded(T(19, 5), "gungameprogressive mg_x ar_wait score 0:0 after 11 min");
        Check("...the restart kind's waiting time (the match itself, in Arms Race and Deathmatch) won or run out is no match - the bridge saw its warm-up on - and the next match "
            + "on the map is the real one, from that end: everybody in from it, its frags from there, no winner from the waiting time's knife",
            wonInWarmUp && warm.Count == 4 && warm[3] is { MapName: "Wait", Mode: "armsrace", Winner: 0 } && warm[3].Start == T(18, 53) && warm[3].Missing.Count == 0
            && warm[3].Players.Select(p => (p.Account, p.Seconds, p.First)).SequenceEqual([(1001L, 720, T(18, 53)), (1002L, 720, T(18, 53))])
            && warm[3].Players[1].Frags is { Kills: 1 } && warm[3].Players[0].Frags is { Kills: 0, Knife: 0, Deaths: 1 });
        wu.MapStarted(T(19, 6), "ar_wait", 3000000016, "Wait", "armsrace");
        wu.Entered(T(19, 6, 5), 1001, "Alice");
        wu.WarmUpEnded(T(19, 7));
        wu.MatchEnded(T(19, 8), "gungameprogressive mg_x ar_wait score 0:0 after 1 min", inWarmUp: true);
        Check("...and the bridge's word decides should the record have the match on already: a match end in the warm-up is no match", warm.Count == 4 && wu.Running);

        // ---- the live record starts at a map's start: a bridge that starts inside a map writes nothing of it ----
        var restarted = new List<HudHistory.Entry>();
        var rs = new MatchTracker { Finished = restarted.Add, UsesWarmUp = true, RecordsFrags = true, FromMapStart = true };
        rs.Seen(T(21, 0), 1001, "Alice");
        rs.Entered(T(21, 0, 5), 1002, "Bob");
        rs.Killed(T(21, 1), 1001, 1002, "ak47", "", 10);
        rs.MatchEnded(T(21, 4), "gungameprogressive mg_x ar_x score 0:0 after 9 min");
        rs.ServerDown(T(21, 5));
        var nothingYet = restarted.Count == 0 && !rs.Running && !rs.Warming;
        rs.MapStarted(T(21, 10), "ar_after", 3000000020, "After", "armsrace");
        rs.Entered(T(21, 10, 5), 1001, "Alice");
        rs.WarmUpEnded(T(21, 11));
        rs.MatchEnded(T(21, 21), "gungameprogressive mg_x ar_after score 0:0 after 10 min");
        Check("History, the live record: a bridge that starts inside a map writes no match of it - its presence, a kill, a match end, the server's stop - and the next map's "
            + "match is known in full; the backfill, whose first log may begin inside a match, keeps that match not known in full",
            nothingYet && restarted.Count == 1 && restarted[0] is { MapName: "After", Mode: "armsrace" } && restarted[0].Missing.Count == 0
            && restarted[0].Players.Single() is { Account: 1001, Seconds: 600 } && !new MatchTracker().FromMapStart);
        wu.MapStarted(T(19, 10), "dm_idle", 3000000017, "Idle", "deathmatch");
        wu.MatchEnded(T(19, 20), "deathmatch mg_x dm_idle score 0:0 after 10 min"); // an empty map's match run out in its waiting time (the restart kind's)
        var idleWarming = warm.Count == 4 && wu.Warming;
        wu.Entered(T(19, 25), 1001, "Alice");
        wu.MatchEnded(T(19, 26), "deathmatch mg_x dm_idle score 0:0 after 10 min", inWarmUp: true, warmUpGoesOn: true); // ...and with Alice at the team menu, nobody spawned
        var stillWarming = warm.Count == 4 && wu.Warming;
        wu.WarmUpEnded(T(19, 28));
        wu.MatchEnded(T(19, 38), "deathmatch mg_x dm_idle score 0:0 after 10 min");
        Check("...an empty map's match run out during its waiting time, or one ended before anybody spawned, is no match, and the waiting time goes on into the next match: "
            + "the real one counts from the warm-up's end",
            idleWarming && stillWarming && warm.Count == 5 && warm[4] is { MapName: "Idle", Mode: "deathmatch" } && warm[4].Start == T(19, 28)
            && warm[4].Players.Single() is { Account: 1001, Seconds: 600 });
        wu.MapStarted(T(19, 40), "de_named", 3000000018, "Named", "wingman");
        wu.Entered(T(19, 40, 5), 1009, "Steam name at the connect");
        wu.Shown(1009, "Alice"); // the name the server shows (a chat line, a click): on a copy, the lab's test name (OsDocName)
        wu.WarmUpEnded(T(19, 41));
        wu.MatchEnded(T(19, 51), "scrimcomp2v2 mg_x de_named score 9:2 after 10 min");
        Check("...each player under the name the server shows for them (their chat, their clicks), not the one the game's connect line gave: a copy's test name, never a real one "
            + "(the live lab on 5794269 kept the client's Steam name)",
            warm.Count == 6 && warm[5].Players.Single() is { Account: 1009, Name: "Alice" });

        // ---- the frags: the game's kill lines ----
        const string KillDay = "11/20/2026"; // a log line's prefix is built from its parts (no date beside a time in this file)
        string L(string ev) => KillDay + " - 18:05:00.000 - " + ev;
        var kill = FragOf(L("\"Alice<2><[U:1:1001]><CT>\" [486 -150 2] killed \"Bob<3><[U:1:1002]><TERRORIST>\" [-466 -1524 2] with \"scar20\" (headshot penetrated)"));
        var onBot = FragOf(L("\"Alice<2><[U:1:1001]><CT>\" [0 0 0] killed \"Botty<5><BOT><TERRORIST>\" [100 0 0] with \"knife_t\""));
        var byBot = FragOf(L("\"Botty<5><BOT><CT>\" [0 0 0] killed \"Bob<3><[U:1:1002]><TERRORIST>\" [0 0 0] with \"ak47\""));
        Check($"Frags: a kill line read - both players (a bot is 0), the weapon, the game's marks, the metres between them ({kill?.Metres:0.0} m)",
            kill is { What: "kill", Killer: 1001, Victim: 1002, Weapon: "scar20", Marks: "headshot penetrated" } && Math.Abs(kill.Metres - 42.46) < 0.05
            && onBot is { Killer: 1001, Victim: 0, Weapon: "knife_t" } && byBot is { Killer: 0, Victim: 1002 });
        Check("...an assist (a flash's too), a suicide and a chicken; chat that says a kill line, and every other line, is none",
            FragOf(L("\"Cara<4><[U:1:1003]><CT>\" assisted killing \"Bob<3><[U:1:1002]><TERRORIST>\"")) is { What: "assist", Killer: 1003 }
            && FragOf(L("\"Cara<4><[U:1:1003]><CT>\" flash-assisted killing \"Bob<3><[U:1:1002]><TERRORIST>\"")) is { What: "assist", Killer: 1003 }
            && FragOf(L("\"Bob<3><[U:1:1002]><TERRORIST>\" [-373 -1478 2] committed suicide with \"world\"")) is { What: "suicide", Killer: 1002 }
            && FragOf(L("\"Alice<2><[U:1:1001]><CT>\" [1 2 3] killed other \"chicken<77>\" [4 5 6] with \"ak47\"")) is { What: "chicken", Killer: 1001 }
            && FragOf(L("\"Eve<6><[U:1:1005]><CT>\" say \"Alice<2><[U:1:1001]><CT> [0 0 0] killed Bob<3><[U:1:1002]><CT> [0 0 0] with ak47\"")) == null
            && FragOf(L("\"Alice<2><[U:1:1001]><CT>\" entered the game")) == null && FragOf(L("Game Over: deathmatch mg_x de_x score 0:0 after 9 min")) == null
            && FragOf(L("\"Botty<5><BOT><CT>\" [0 0 0] committed suicide with \"world\"")) == null);
        var frag = new List<HudHistory.Entry>();
        var f = new MatchTracker { Finished = frag.Add, UsesWarmUp = true, RecordsFrags = true };
        f.MapStarted(T(19, 0), "ar_frags", 0, "ar_frags", "armsrace");
        foreach (var (who, name) in new[] { (1001L, "Alice"), (1002L, "Bob"), (1003L, "Cara") }) f.Entered(T(19, 0, 5), who, name);
        f.WarmUpEnded(T(19, 1));
        f.Killed(T(19, 2), 1001, 1002, "awp", "noscope", 48.4);
        f.Killed(T(19, 3), 1001, 0, "taser", "attackerinair", 2);
        f.Killed(T(19, 4), 1001, 1002, "ak47", "headshot penetrated", 12);
        f.Killed(T(19, 5), 0, 1001, "ak47", "", 5);
        f.Killed(T(19, 6), 1001, 1003, "ak47", "", 5);
        f.Assisted(T(19, 6), 1002);
        f.Suicide(T(19, 7), 1003);
        f.Chicken(T(19, 8), 1002);
        f.Chicken(T(19, 8), 1002);
        f.Killed(T(19, 10, 58), 1002, 1003, "knifegg", "", 1);
        f.Entered(T(19, 10), 1004, "Dan");
        f.MatchEnded(T(19, 11), "gungameprogressive mg_armsrace ar_frags score 0:0 after 10 min");
        var fr = frag.Count == 1 ? frag[0] : null;
        HudHistory.Frags? Of(long account) => fr?.Players.FirstOrDefault(p => p.Account == account)?.Frags;
        Check("...counted per player: kills (bots too), deaths, assists, head shots, wallbangs, noscopes, in-air, taser and knife kills, chickens, suicides, the longest kill, the best streak",
            Of(1001) is { Kills: 4, Deaths: 1, Hs: 1, Wall: 1, Noscope: 1, Air: 1, Taser: 1, Knife: 0, Streak: 3, Far: 48.4 } && Of(1002) is { Kills: 1, Deaths: 2, Assists: 1, Chickens: 2, Knife: 1 }
            && Of(1003) is { Kills: 0, Deaths: 3, Suicides: 1 } && Of(1004) is { Any: false });
        Check("...Arms Race's winner: the human whose knife kill came with the end; who killed whom, every pair, the most first; the most humans in it at once",
            fr is { Winner: 1002, Most: 4 } && fr.Pairs is [{ Killer: 1001, Victim: 1002, Kills: 2 }, ..] && fr.Pairs.Count == 3 && fr.Nemesis is { Killer: 1001, Kills: 2 }
            && fr.TopFragger is { Player.Account: 1001, Kills: 4 });
        var late = new List<HudHistory.Entry>();
        var l2 = new MatchTracker { Finished = late.Add, RecordsFrags = true };
        l2.MapStarted(T(20, 0), "ar_late", 0, "ar_late", "armsrace");
        l2.Entered(T(20, 0, 5), 1001, "Alice");
        l2.Killed(T(20, 5), 1001, 0, "knife", "", 1);
        l2.MatchEnded(T(20, 10), "gungameprogressive mg_armsrace ar_late score 0:0 after 10 min");
        Check("...a knife kill long before the end won nothing", late.Count == 1 && late[0].Winner == 0);

        // ---- the file's lines, the plugin's block, the words ----
        var odd = first! with { Players = [new HudHistory.Player(1009, "Ann \"the\" Öst\t1", 61, first.Start)] };
        var back = HudHistory.FromJsonLine(HudHistory.ToJsonLine(odd));
        var block = new HudHistory { Version = 7, Entries = [second!, first] };
        var decoded = HudHistory.Decode(block.Encode());
        Check("History: an entry's line reads back the same - any name, its time's offset - and the plugin's block carries the newest first with its version",
            back != null && back.Players[0].Name == "Ann \"the\" Öst\t1" && back.Start == odd.Start && back.Start.Offset == TimeSpan.FromHours(1) && back.MapId == odd.MapId
            && back.Score == "9:4" && back.Missing.Count == 0 && back.Players[0].Frags == null && HudHistory.FromJsonLine("not json") == null && HudHistory.FromJsonLine("{\"v\":2}") == null
            && decoded is { Version: 7 } && decoded.Entries.Count == 2 && decoded.Entries[0].End == second!.End && HudHistory.Decode("nonsense") == null);
        var fragLine = HudHistory.ToJsonLine(fr!);
        var fragBack = HudHistory.FromJsonLine(fragLine);
        var compact = HudHistory.FromJsonLine(HudHistory.ToJsonLine(fr!, compact: true));
        Check($"...the frags in the file whole - a player's every count, the longest kill, the winner, the most at once, the pairs and the top pair - and in the plugin's block only the kills ({fragLine.Length} and {HudHistory.ToJsonLine(fr!, compact: true).Length} characters)",
            fragBack is { Winner: 1002, Most: 4 } && fragBack.Pairs?.Count == 3 && fragBack.Players.First(p => p.Account == 1001).Frags == Of(1001) && fragBack.Players.First(p => p.Account == 1004).Frags is { Any: false }
            && fragLine.Contains("\"nemesis\":[1001,1002,2]", StringComparison.Ordinal)
            && compact is { Winner: 1002, Most: 4, Pairs: null } && compact.Players.First(p => p.Account == 1001).Frags is { Kills: 4, Deaths: 0, Far: 0 } && compact.TopFragger is { Player.Account: 1001 });
        Check("...its words: the day and span from when its humans were in, its length, how it ended with Wingman's score and a mark for what is not known, everybody who joined",
            HudHistory.When(first) == "Fri 20 Nov  ·  21:00 - 21:11" && HudHistory.Duration(first) == "11 min" && HudHistory.Minutes(0.4) == "under a minute" && HudHistory.Minutes(72) == "1 h 12 min"
            && HudHistory.Minutes(120) == "2 h" && HudHistory.Ended(first) == "Match end  ·  9:4" && HudHistory.Ended(sixth!) == "Match end  ·  from the logs, partly"
            && HudHistory.Ended(fifth!) == "Server restart" && HudHistory.Ended(fifth! with { EndedBy = HudHistory.ByServerStart }) == "The server went down"
            && HudHistory.Ended(fourth!) == "Map change" && HudHistory.Ended(third!) == "Match restarted" && HudHistory.Ended(seventh!) == "Not known  ·  from the logs, partly"
            && HudHistory.Joined(first) == "2 players: Alice and Bob" && HudHistory.Joined(fifth!) == "1 player: Alice"
            && HudHistory.Joined(first with { Players = Enumerable.Range(1, 6).Select(i => new HudHistory.Player(i, $"P{i}", 100 - i, first.Start)).ToList() }) == "6 players: P1, P2, P3, P4, P5 and P6"
            && HudHistory.Who(first with { Players = Enumerable.Range(1, 6).Select(i => new HudHistory.Player(i, $"P{i}", 100 - i, first.Start)).ToList() }) == "P1, P2, P3, P4 and 2 more"
            && HudHistory.When(last! with { End = T(1, 20).AddDays(1) }).EndsWith("01:01 - Sat 01:20", StringComparison.Ordinal));
        var stateLine = new HudState { HistoryVersion = 42, StatsVersion = 43 };
        Check("...the live state names the block's version and the STATS page's, and a state without them names none",
            HudState.Decode(stateLine.Encode()) is { HistoryVersion: 42, StatsVersion: 43 } && HudState.Decode(new HudState().Encode()) is { HistoryVersion: 0, StatsVersion: 0 });
        Check("A dislike's reason for a map that took the server down: carried by the codecs as itself (an older plugin reads a dislike); the last reason of all, the popup's seventh button (os_why_7) with content v4.15.0, which has it",
            DownReasons.Crash is { Word: "crash", Value: HudCatalog.CrashVote, Label: "It crashed the server" } && HudCatalog.Carried(HudCatalog.CrashVote) == HudCatalog.CrashVote
            && HudCatalog.Carried(-8) == -1 && DownReasons.All[^1] == DownReasons.Crash && HudVote.WhyButton(DownReasons.All.Length) == "os_why_7"
            && HudVersion.Level >= HudBrowser.HistoryLevel);

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
            ConsoleLine("19:05:00") + "\"Dan<3><[U:1:1004]><CT>\" [0 0 0] killed \"Botty<5><BOT><TERRORIST>\" [400 0 0] with \"ak47\" (headshot)",
            ConsoleLine("19:05:00") + "\"Dan<3><[U:1:1004]><CT>\" [0 0 0] killed \"Botty<6><BOT><TERRORIST>\" [40 0 0] with \"ak47\"",
            ConsoleLine("19:12:00") + "Game Over: deathmatch mg_x de_gamma score 0:0 after 12 min",
            ConsoleLine("20:15:00") + "\"Eve<4><[U:1:1005]><>\" entered the game",
            "11/20 20:15:01 [Server] some other console line",
        ];
        var (filled, words) = Backfill([("bridge-2026-11-20.log", dayLog), ("console.log", consoleLog)], TimeZoneInfo.Utc, null);
        DateTimeOffset U(int h, int m, int s = 0) => new(2026, 11, 20, h, m, s, TimeSpan.Zero);
        Check($"History backfill: the day logs and the console's, in time order - {filled.Count} entries ({words})",
            filled.Count == 4 && words.StartsWith($"history backfill: 4 match(es) from {Day} 19:00 to {Day} 20:45, 2 of them incomplete, 4 human(s)", StringComparison.Ordinal));
        var (gamma, alpha, beta, shoots) = filled.Count == 4 ? (filled[0], filled[1], filled[2], filled[3]) : (null, null, null, null);
        Check("...a console's match: its map known by its file alone (no Workshop id), its mode from the Game Over line, its frags from the console's kill lines - two in the same second each its own",
            gamma is { LogName: "de_gamma", MapName: "de_gamma", MapId: 0, Mode: "deathmatch", Source: "backfill" } && gamma.Missing.SequenceEqual(["map id"])
            && gamma.Players.Single() is { Account: 1004, Seconds: 660, Frags: { Kills: 2, Hs: 1 } });
        Check("...a day log's: the Workshop id the change sent, the name the server showed, the score, the fairness restart no split, no frags (the day logs have none)",
            alpha is { MapId: 3000000001, MapName: "Alpha", Mode: "wingman", Score: "9:7", EndedBy: HudHistory.ByMatchEnd } && alpha.Missing.Count == 0 && alpha.Start == U(20, 10, 1)
            && alpha.Players.Select(p => (p.Account, p.Seconds)).SequenceEqual([(1001L, 592), (1002L, 570)]) && alpha.Players.All(p => p.Frags == null) && alpha.Most == 2);
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
            h.Line("\"Alice<2><[U:1:1001]><CT>\" [0 0 0] killed \"Bob<2><[U:1:1002]><CT>\" [100 0 0] with \"ak47\" (headshot)");
            h.Run(60);
            h.Leave(("Bob", "[U:1:1002]"));
            h.Run(60);
            h.MatchEnd();
            var lines = File.Exists(HistoryPath) ? File.ReadAllLines(HistoryPath) : [];
            var live = lines.Length == 1 ? HudHistory.FromJsonLine(lines[0]) : null;
            Check("History, live: the bridge's log stream makes the entry - a plugin with no warm-up: from the map's start - appends it to history.jsonl and moves the plugin's copy; its frags from the kill lines, never logged",
                live is { LogName: "de_live", EndedBy: HudHistory.ByMatchEnd, Mode: "armsrace", Source: "live", Most: 2 } && live.Players.Select(p => (p.Account, p.Seconds)).SequenceEqual([(1001L, 240), (1002L, 60)])
                && live.Players[0].Frags is { Kills: 1, Hs: 1 } && live.Players[1].Frags is { Deaths: 1 } && live.Pairs is [{ Killer: 1001, Victim: 1002 }]
                && History.Entries.Count == 1 && History.Version > before && History.Encode().Contains("de_live", StringComparison.Ordinal) && !Told.Any(x => x.Contains("killed", StringComparison.Ordinal)));
            File.AppendAllText(HistoryPath, "a line that is no entry\n");
            Check("...and a bridge reads it back at its start, newest first, a line that does not read skipped and said",
                LoadHistory().Contains("1 match(es)", StringComparison.Ordinal) && LoadHistory().Contains("1 line(s) that do not read skipped", StringComparison.Ordinal)
                && History.Entries.Count == 1 && History.Entries[0].LogName == "de_live");

            // with the warm-up: the plugin says it is on, two spawned and nobody still coming - the bridge ends it once everybody is in, and the match starts then
            h.Fresh();
            File.Delete(HistoryPath);
            LoadHistory();
            var warmOn = true;
            h.Server = c => c == EarlyJoin.WarmUpCommand ? EarlyJoin.WarmUpReply(warmOn, 5, 2, 100)
                : c.StartsWith(EarlyJoin.WarmUpCommand + " ", StringComparison.Ordinal) ? ((warmOn = false) ? "" : "os_warmup: ending") // the end asked for: CS2 has ended it at the next look
                : c == EarlyJoin.JoiningCommand ? EarlyJoin.JoiningReply([]) : "";
            h.Line("Loading map \"de_warmlive\"");
            h.Join(("Alice", "[U:1:1001]"));
            h.Join(("Bob", "[U:1:1002]"));
            h.Line("\"Alice<2><[U:1:1001]><CT>\" [0 0 0] killed \"Bob<2><[U:1:1002]><CT>\" [100 0 0] with \"ak47\"");
            var warmedAt = MatchHistory.Warming;
            h.Run(EarlyJoin.SettleSeconds + 5);
            var startedAt = MatchHistory.StartedAt;
            h.Line("\"Bob<2><[U:1:1002]><CT>\" [0 0 0] killed \"Alice<2><[U:1:1001]><CT>\" [100 0 0] with \"knife\"");
            h.Run(300);
            h.MatchEnd();
            var warmLive = History.Entries.FirstOrDefault();
            Check($"...with the warm-up: its end - the bridge asks for it, a look finds it off - starts the match: its start then, both players in from it, the warm-up's kill no frag ({startedAt:HH:mm:ss})",
                warmedAt && warmLive is { LogName: "de_warmlive", EndedBy: HudHistory.ByMatchEnd } && warmLive.Start == startedAt && startedAt > warmLive.Start.AddSeconds(-1)
                && h.Sent.Contains($"{EarlyJoin.WarmUpCommand} {EarlyJoin.EndWord}") && warmLive.Players.All(p => p.First == warmLive.Start)
                && warmLive.Players.First(p => p.Account == 1002).Frags is { Kills: 1, Knife: 1 } && warmLive.Players.First(p => p.Account == 1001).Frags is { Kills: 0, Deaths: 1 });

            // the restart kind (Arms Race, Deathmatch): the match itself is the waiting time - won during it, it is no match, and the next match on the map is the real one
            h.Fresh();
            File.Delete(HistoryPath);
            LoadHistory();
            warmOn = true;
            h.Line("Loading map \"ar_waitlive\"");
            h.Join(("Alice", "[U:1:1001]"));
            h.Join(("Bob", "[U:1:1002]"));
            h.Run(2);
            h.Line("\"Alice<2><[U:1:1001]><CT>\" [0 0 0] killed \"Bob<2><[U:1:1002]><CT>\" [100 0 0] with \"knife\"");
            var waiting = MatchHistory.Warming && WarmUpSince != null && WarmUpOnNow;
            HudActive = () => true;
            h.MatchEnd();
            HudActive = () => false;
            warmOn = false; // the plugin's warm-up ended at the bridge's word
            var noEntry = History.Entries.Count == 0 && MatchHistory.Running && WarmUpSince == null;
            var realFrom = MatchHistory.StartedAt;
            h.Run(300);
            h.Line("\"Bob<2><[U:1:1002]><CT>\" [0 0 0] killed \"Alice<2><[U:1:1001]><CT>\" [100 0 0] with \"ak47\"");
            h.Run(300);
            h.MatchEnd();
            var real = History.Entries.FirstOrDefault();
            Check($"...the restart kind's waiting time won (an Arms Race win during it): no entry, the line says the win does not count, and the next match on the map is the real one - from that end ({realFrom:HH:mm:ss}), the waiting time's knife kill no frag",
                waiting && noEntry && h.Said(EarlyJoin.WonInWarmUp) && History.Entries.Count == 1 && real is { LogName: "ar_waitlive", EndedBy: HudHistory.ByMatchEnd, Winner: 0 } && real.Start == realFrom
                && real.Players.All(p => p.First == realFrom) && real.Players.First(p => p.Account == 1002).Frags is { Kills: 1 } && real.Players.First(p => p.Account == 1001).Frags is { Kills: 0, Knife: 0, Deaths: 1 });

            // the name the server shows a player by - a chat line's, a click's - is the one the entry keeps: on a copy the lab's test name, which the connect line never has
            h.Fresh();
            File.Delete(HistoryPath);
            LoadHistory();
            warmOn = true;
            h.Line("Loading map \"de_namelive\"");
            h.Join(("Steamy", "[U:1:1003]"));
            h.Join(("Bob", "[U:1:1002]"));
            h.Line("\"Doc Name<2><[U:1:1003]><CT>\" say \"gg\"");
            h.Run(EarlyJoin.SettleSeconds + 5);
            h.Run(300);
            h.MatchEnd();
            Check("...each player under the name the server shows them by (a chat line, a click: on a copy the lab's test name, OsDocName's), never the connect line's",
                History.Entries.FirstOrDefault() is { LogName: "de_namelive" } named && named.Players.First(p => p.Account == 1003).Name == "Doc Name"
                && named.Players.First(p => p.Account == 1002).Name == "Bob");

            // a bridge restart (every deploy restarts it) inside a map: no match - not at a match end, not at the server's next stop - until the next map's start
            h.Fresh();
            File.Delete(HistoryPath);
            LoadHistory();
            h.Join(("Alice", "[U:1:1001]"));
            h.Line("\"Alice<2><[U:1:1001]><CT>\" say \"hello\"");
            h.Run(300);
            h.MatchEnd();
            h.Run(300);
            h.Line("rcon from \"192.0.2.10:64265\": command \"quit\"");
            var afterRestart = History.Entries.Count == 0 && !File.Exists(HistoryPath) && !MatchHistory.Running;
            h.Line("Loading map \"de_afterrestart\"");
            h.Join(("Alice", "[U:1:1001]"));
            MatchHistory.NoWarmUp(); // as for a plugin without the warm-up: the match from the map's start
            h.Run(300);
            h.Line("rcon from \"192.0.2.10:64265\": command \"quit\"");
            var full = History.Entries.FirstOrDefault();
            Check("History, live: a bridge that starts inside a map (a restart, every deploy's) writes no match - not at a match end, not at the server's stop, none with "
                + "no map - and the next map's match is known in full",
                afterRestart && History.Entries.Count == 1 && full is { LogName: "de_afterrestart", EndedBy: HudHistory.ByServerStop } && full.Missing.Count == 0
                && full.Players.Single() is { Account: 1001, Seconds: 300 });

            // a map start nobody asked for (an operator's changelevel) takes the mode the bridge last knew; the mode it settles on then is the match's
            h.Fresh();
            S.Mode = Modes.WingmanId;
            h.Line("Loading map \"ar_baggage\"");
            var startedIn = MatchHistory.Mode;
            TakePluginMode(Modes.ArmsRace, null);
            Check($"History, live: the mode the bridge settles on after a map start nobody asked for (the plugin's word) is the match's - not the one it last knew ({startedIn})",
                startedIn == Modes.WingmanId && MatchHistory.Mode == Modes.ArmsRaceId && S.Mode == Modes.ArmsRaceId);
        }
        finally { h.Dispose(); }

        // ---- the plugin's cost: a block at its most, decoded as the plugin takes it after every match end; the HISTORY page worked out over it ----
        var big = new HudHistory { Version = 9 };
        for (var i = 0; i < HudHistory.BlockMax; i++)
            big.Entries.Add(fr! with
            {
                Start = first.Start.AddMinutes(-15 * i), End = first.End.AddMinutes(-15 * i), MapId = 3000000000 + i % 500, MapName = $"Map {i % 500}", LogName = "",
                Players = Enumerable.Range(0, 4).Select(k => new HudHistory.Player(2000 + k, $"Player number {k}", 600, first.Start, new HudHistory.Frags(Kills: 20 + k, Deaths: 10))).ToList(),
            });
        var bigText = big.Encode();
        HudHistory.Decode(bigText); // the first, which also compiles it
        var decodes = Enumerable.Range(0, 5).Select(_ => { var sw = System.Diagnostics.Stopwatch.StartNew(); var d = HudHistory.Decode(bigText); sw.Stop(); return (Count: d?.Entries.Count ?? 0, Ms: sw.Elapsed.TotalMilliseconds); }).ToList();
        var decodeMs = decodes.Select(x => x.Ms).OrderBy(x => x).ElementAt(2);
        var viewTimes = Enumerable.Range(0, 5).Select(i =>
        {
            var hp = new HudHistoryPage { Page = i, Query = i % 2 == 0 ? "" : "number 2" };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var n = hp.Matches(big, 2001).Count;
            hp.View(big, null, new HudState(), new HudBrowser(), 2001, DateTime.UtcNow);
            sw.Stop();
            return (n, sw.Elapsed.TotalMilliseconds);
        }).ToList();
        var viewMs = viewTimes.Select(x => x.Item2).OrderBy(x => x).ElementAt(2);
        Console.WriteLine($"history cost: decode {HudHistory.BlockMax} matches ({bigText.Length / 1024} KB) {string.Join(", ", decodes.Select(x => x.Ms.ToString("0.0")))} ms; "
            + $"the HISTORY page {string.Join(", ", viewTimes.Select(x => x.Item2.ToString("0.0")))} ms");
        Check($"History: the plugin's block at its most ({HudHistory.BlockMax} matches, {bigText.Length / 1024} KB) decodes whole ({decodeMs:0.0} ms, median of 5, this build) - off the "
            + $"game thread (BridgeLink.TakeHistory); a player's HISTORY page over it takes {viewMs:0.0} ms, once for each change of the page or the history",
            decodes.All(x => x.Count == HudHistory.BlockMax) && viewTimes.All(x => x.n == HudHistory.BlockMax));

        // ---- the HISTORY page ----
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
        var again = alpha with { Start = alpha.Start.AddDays(-2), End = alpha.End.AddDays(-2), Most = 3, Winner = 1001 };
        var past = new HudHistory { Version = 3, Entries = [shoots!, beta!, alpha, unlisted, again] }; // newest first
        var page = new HudHistoryPage();
        var s = new HudState { Key = "3000000002" };
        var (vars, classes, pictures) = page.View(past, catalog, s, new HudBrowser(), 1001, DateTime.UtcNow);
        string Var(string name) => vars.LastOrDefault(x => x.Name == name).Text ?? "";
        bool On(string id, string cls) => classes.LastOrDefault(x => x.Id == id && x.Class == cls).On;
        Check($"HISTORY page: four cards a page, newest first; the count, the page, no empty words ({Var("oshcount")})",
            Var("oshcount") == $"5 matches  ·  4 maps  ·  since {HudHistory.Day(again.From)}" && Var("oshpage") == "Page 1 of 2" && Var("oshempty") == ""
            && Var("osh1n") == "ar_shoots" && Var("osh2n") == "Beta" && Var("osh3n") == "Alpha" && Var("osh4n") == "Gone Map"
            && On(HudView.Screen, "hfirst") && !On(HudView.Screen, "hlast") && On(HudView.Screen, "hm-all") && !On(HudView.Screen, "hnone"));
        Check("...a card's words: when, how long, how it ended, everybody who joined, how often its map was played, a fun line, its likes, its size, Vote for it as the browser's Play",
            Var("osh3w") == HudHistory.When(alpha) && Var("osh3d") == "10 min" && Var("osh3e") == "Match end  ·  9:7" && Var("osh3p") == "2 players: Alice and Bob"
            && Var("osh3c") == "Played twice" && Var("osh3f") == $"First played {HudHistory.Day(again.From)}  ·  Most at once: 2" && Var("osh3v") == "+3  -1" && Var("osh3s") == "200 MB"
            && Var("osh3a") == "Put to the vote" && Var("osh1s") == HudMenu.OfficialSize && Var("osh1c") == "Played once" && Var("osh2a") == "On now"
            && Var("osh4v") == "" && Var("osh4a") == "Not on the list" && Var("osh4s") == "");
        bool Card(int k, string state) => On(HudView.Screen, HudHistoryPage.CardClass(k, state));
        Check("...a card's states, classes on os_screen (content v4.16.0: h<k>-used and so on) - used, the player's own like or dislike, official, the match's mode, "
            + "a record known in part, on now, mine - and its picture the map's, on picture panel k; nothing to a panel of the card's own",
            Card(3, "used") && Card(3, "liked") && Card(2, "disliked") && Card(1, "official") && !Card(3, "official") && Card(3, "wm") && !Card(3, "ar") && Card(1, "ar") && !Card(1, "dm")
            && Card(1, "inc") && !Card(3, "inc") && Card(2, "now") && !Card(3, "now") && Card(3, "mine") && !Card(4, "mine") && !Card(4, "liked")
            && classes.All(c => c.Id == HudView.Screen) && pictures.Count == HudHistoryPage.Cards && pictures.Select(x => x.Panel).SequenceEqual(HudHistoryPage.PicturePanels)
            && pictures[3].Classes.Length == 0 && HudHistoryPage.CardStates.All(st => classes.Any(c => c.Class == HudHistoryPage.CardClass(4, st))));
        Check("...a card's Like, Dislike and Vote for it are the browser's own, for that match's map; Vote for it in the match's mode; nothing for a map on no list or the map that is on",
            page.Command("os_h3_up", past, catalog, s, new HudBrowser(), 1001, DateTime.UtcNow) == "!up 12" && page.Command("os_h2_down", past, catalog, s, new HudBrowser(), 1001, DateTime.UtcNow) == "!down 40"
            && page.Command("os_h3_play", past, catalog, s, new HudBrowser(), 1001, DateTime.UtcNow) == "!map 12 wingman" && page.Command("os_h1_play", past, catalog, s, new HudBrowser(), 1001, DateTime.UtcNow) == "!map ar_shoots"
            && page.Command("os_h4_up", past, catalog, s, new HudBrowser(), 1001, DateTime.UtcNow) == null && page.Command("os_h2_play", past, catalog, s, new HudBrowser(), 1001, DateTime.UtcNow) == null
            && page.Command("os_h9_up", past, catalog, s, new HudBrowser(), 1001, DateTime.UtcNow) == null);
        var fifthCard = page.Click(HudHistoryPage.NextButton, page.Matches(past, 1001).Count) ? page.View(past, catalog, s, new HudBrowser(), 1001, DateTime.UtcNow) : default;
        Check("...the pager: the next page has the fifth match, its last; the cards with none are empty and unused",
            fifthCard.Vars?.LastOrDefault(x => x.Name == "osh1f").Text == $"First played {HudHistory.Day(again.From)}  ·  Most at once: 3  ·  Won by Alice"
            && fifthCard.Vars.LastOrDefault(x => x.Name == "osh2n").Text == "" && fifthCard.Classes.LastOrDefault(x => x.Class == HudHistoryPage.CardClass(2, "used")) is { On: false }
            && fifthCard.Classes.LastOrDefault(x => x.Id == HudView.Screen && x.Class == "hlast") is { On: true });
        page.Click("os_hf_wm", 5);
        var wingmanOnly = page.Matches(past, 1001).Select(e => e.MapName).ToList();
        page.Click(HudHistoryPage.MineButton, 5);
        var mine = page.Matches(past, 1001).Select(e => e.MapName).ToList();
        page.Click("os_hf_all", 5);
        page.Click(HudHistoryPage.MineButton, 5);
        page.Find("eve");
        var byPlayer = page.Matches(past, 1001).Select(e => e.MapName).ToList();
        var (searchVars, searchClasses, _) = page.View(past, catalog, s, new HudBrowser(), 1001, DateTime.UtcNow);
        page.Click(HudHistoryPage.ClearButton, 5);
        page.Click(HudHistoryPage.OrderButton, 5);
        var oldest = page.Matches(past, 1001).Select(e => e.Start).FirstOrDefault();
        page.Find("nobody at all");
        var (noneVars, noneClasses, _) = page.View(past, catalog, s, new HudBrowser(), 1001, DateTime.UtcNow);
        var (emptyVars, _, _) = new HudHistoryPage().View(new HudHistory { Version = 1 }, catalog, s, new HudBrowser(), 1001, DateTime.UtcNow);
        var (loadingVars, _, _) = new HudHistoryPage().View(null, catalog, s, new HudBrowser(), 1001, DateTime.UtcNow);
        Check("...the mode chips, Mine, a search by a map's or a player's name (the count says what it found), oldest first; no match found, no history yet, and the history on its way say so",
            wingmanOnly.SequenceEqual(["Beta", "Alpha", "Gone Map", "Alpha"]) && mine.SequenceEqual(["Beta", "Alpha", "Alpha"]) && byPlayer.SequenceEqual(["Gone Map"])
            && searchVars.LastOrDefault(x => x.Name == "oshcount").Text == "1 of 5 matches" && searchClasses.LastOrDefault(x => x.Class == "hquery").On
            && oldest == again.Start && noneVars.LastOrDefault(x => x.Name == "oshempty").Text == "No match on a map or with a player like \"nobody at all\"" && noneClasses.LastOrDefault(x => x.Class == "hnone").On
            && emptyVars.LastOrDefault(x => x.Name == "oshempty").Text == HudHistoryPage.Empty && loadingVars.LastOrDefault(x => x.Name == "oshcount").Text == "Loading the history...");
        Check($"...the page's buttons all take clicks only, and only to a content that has the page (level {HudBrowser.HistoryLevel}, content {HudVersion.OfLevel(HudBrowser.HistoryLevel)})",
            HudHistoryPage.Buttons.Count() == 4 * 3 + 11 && HudHistoryPage.Buttons.All(b => b.StartsWith("os_h", StringComparison.Ordinal)) && !HudBrowser.HasHistory("v4.14.0") && !HudBrowser.HasHistory("v4.15.0")
            && HudBrowser.HasHistory(HudVersion.Content) == HudVersion.Level >= HudBrowser.HistoryLevel && HudView.Tabs.Any(x => x.Id == HudBrowser.HistoryTab && x.Page == HudSlots.Page.History)
            && HudView.Tabs.Any(x => x.Id == HudStats.Tab && x.Page == HudSlots.Page.Stats));
    }
}
