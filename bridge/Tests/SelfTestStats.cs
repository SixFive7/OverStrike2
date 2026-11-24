// --selftest, the STATS page (Stats.cs, HudStats.cs) and our ballots (Ballots.cs): every record worked out over a made-up history, likes, ballots and crashes;
// the block both ends read; the live bridge keeping a decided ballot and making the block again when something changes; the ballots read back from made-up day
// logs; and the lab's seed (deploy/lab-seed/) parsing and filling every tile. Made-up players only.
static partial class Program
{
    static void StatsSelfTest(Action<string, bool> Check)
    {
        var zone = TimeSpan.FromHours(1);
        DateTimeOffset D(int day, int h, int m) => new(2026, 11, day, h, m, 0, zone);
        HudHistory.Player P(long account, string name, int seconds, DateTimeOffset first, HudHistory.Frags? f = null) => new(account, name, seconds, first, f);
        HudHistory.Entry M(int day, int h, int m, int minutes, long id, string name, string mode, string endedBy, string score, HudHistory.Player[] players, int most = 0, long winner = 0,
            HudHistory.Pair[]? pairs = null) =>
            new(D(day, h, m), D(day, h, m).AddMinutes(minutes), id, name, id > 0 ? "" : name, mode, endedBy, score, players, [], "live", most, winner, pairs);
        var f1 = new HudHistory.Frags(Kills: 30, Deaths: 10, Hs: 20, Knife: 3, Chickens: 5, Far: 41.2);
        var f2 = new HudHistory.Frags(Kills: 25, Deaths: 12, Hs: 10, Knife: 1, Chickens: 9, Far: 12);
        // newest first, as the bridge keeps them: four days, three modes, a late one, a Wingman blowout
        var entries = new List<HudHistory.Entry>
        {
            M(19, 0, 30, 20, 3000000003, "Gamma", "wingman", HudHistory.ByMatchEnd, "13:2", [P(1001, "Alice", 1200, D(19, 0, 30)), P(1002, "Bob", 1100, D(19, 0, 31))], 2),
            M(18, 22, 0, 30, 3000000001, "Alpha", "armsrace", HudHistory.ByMatchEnd, "", [P(1001, "Alice", 1800, D(18, 22, 0), f1), P(1002, "Bob", 1700, D(18, 22, 1), f2)], 3, 1002,
                [new(1001, 1002, 7), new(1002, 1001, 4)]),
            M(18, 21, 0, 50, 3000000002, "Beta", "deathmatch", HudHistory.ByMatchEnd, "", [P(1002, "Bob", 3000, D(18, 21, 0)), P(1003, "Cara", 2900, D(18, 21, 2))], 2),
            M(17, 21, 0, 10, 3000000001, "Alpha", "armsrace", HudHistory.ByMapChange, "", [P(1001, "Alice", 600, D(17, 21, 0))], 1),
            M(10, 21, 0, 12, 3000000001, "Alpha", "armsrace", HudHistory.ByMatchEnd, "", [P(1003, "Cara", 700, D(10, 21, 0))], 1),
        };
        var likes = new Dictionary<string, Dictionary<string, int>>
        {
            ["3000000001"] = new() { ["[U:1:1001]"] = 1, ["[U:1:1002]"] = 1, ["[U:1:1003]"] = -1 },
            ["3000000002"] = new() { ["[U:1:1001]"] = HudCatalog.CrashVote, ["[U:1:1002]"] = -1 },
            ["3000000003"] = new() { ["[U:1:1003]"] = 1 },
            ["3000000099"] = new() { ["[U:1:1009]"] = 1 },
        };
        var ballots = new List<BallotRecord>
        {
            new(D(18, 21, 59), 1, "armsrace", "all", 3, [new("3000000001", "Alpha", "armsrace", 2), new("3000000002", "Beta", "deathmatch", 1), new(RandomKey, "Random", "armsrace", 0)], "3000000001", "live"),
            new(D(19, 0, 29), 2, "armsrace", "all", 2, [new("3000000003", "Gamma", "wingman", 2), new("3000000001", "Alpha", "armsrace", 0)], "3000000003", "live"),
        };
        var crashes = new Dictionary<string, int> { ["3000000002"] = 2 };
        var st = MakeStats(entries, likes, ballots, crashes, D(19, 1, 0), new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc));
        string V(string name) => st.Vars.LastOrDefault(x => x.Name == name).Text ?? "";
        Check($"Stats: the head - matches, players, since the first; the maps new this week ({V("osst")}; {V("osstnw")})",
            V("osst") == "5 matches  ·  3 players  ·  since Tue 10 Nov" && V("osstnw") == "2 maps: Gamma, Beta" && !st.Classes["stnone"]);
        Check("...the most played maps, a bar each against the first, the first's picture; ties go to the most recent",
            V("osstp1n") == "Alpha" && V("osstp1v") == "3 matches" && V("osstp2n") == "Gamma" && V("osstp3n") == "Beta" && V("osstp2v") == "1 match" && V("osstp4n") == ""
            && !st.Steps.ContainsKey("stp1") && st.Steps["stp2"] == 3 && st.Steps["stp4"] == 0 && st.Pictures[HudStats.PlayedPicture] == "3000000001");
        Check("...the most voted for in our ballots (Random no map), the most liked (fewer dislikes first in a tie) with its picture, the least liked, the hall of shame - the crashes counted and the crash dislikes",
            V("osstv1n") == "Gamma" && V("osstv1v") == "2 votes" && V("osstv2n") == "Alpha" && V("osstv3n") == "Beta" && V("osstv3v") == "1 vote" && V("osstv4n") == ""
            && V("osstl1n") == "Alpha" && V("osstl1v") == "+2  -1" && V("osstl2n") == "Gamma" && V("osstl2v") == "+1" && st.Pictures[HudStats.LikedPicture] == "3000000001"
            && V("osstd1n") == "Beta" && V("osstd1v") == "2 dislikes" && V("osstd2n") == "Alpha" && V("osstd2v") == "1 dislike"
            && V("osstc1n") == "Beta" && V("osstc1v") == "3 crashes" && V("osstc2n") == "");
        Check("...the players' records: matches, hours, evenings, the night owl, the first one in; Arms Race's wins and the kills shown only when there are some",
            V("ossr1n") == "Alice" && V("ossr1v") == "3 matches" && V("ossr2n") == "Bob" && V("ossr2v") == "1.6 h" && V("ossr3n") == "Alice" && V("ossr3v") == "3 evenings"
            && V("ossr4n") == "Alice" && V("ossr4v") == "1 match after midnight" && V("ossr5n") == "Alice" && V("ossr5v") == "3 times"
            && V("ossr6n") == "Bob" && V("ossr6v") == "1 win" && st.Classes["str6"] && V("ossr7n") == "Alice" && V("ossr7v") == "30 kills" && st.Classes["str7"]);
        Check("...the matches' records: the longest, the shortest real one (a minute and its match end), the busiest evening, the Wingman blowout - the winner's rounds first",
            V("ossm1n") == "Beta  ·  Wed 18 Nov" && V("ossm1v") == "50 min" && V("ossm2n") == "Alpha  ·  Tue 10 Nov" && V("ossm2v") == "12 min"
            && V("ossm3n") == "Wed 18 Nov  ·  3 players" && V("ossm3v") == "2 matches" && V("ossm4n") == "Gamma  ·  Thu 19 Nov" && V("ossm4v") == "13:2");
        Check($"...the modes' shares, a bar each ({V("osstmd")})", V("osstmd") == "Arms Race 60 %  ·  Deathmatch 20 %  ·  Wingman 20 %" && st.Steps["stmar"] == 6 && st.Steps["stmdm"] == 2 && st.Steps["stmwm"] == 2);
        Check("...the fun records from the frags: the top fragger in one match, the sharpshooter past 50 kills, knives, chickens, the longest shot, the nemesis; counting since",
            V("ossf1n") == "Alice" && V("ossf1v") == "30 kills" && V("ossf2n") == "" && !st.Classes["stf2"] && V("ossf3n") == "Alice" && V("ossf3v") == "3 knife kills"
            && V("ossf4n") == "Bob" && V("ossf4v") == "9 chickens" && V("ossf5n") == "Alice" && V("ossf5v") == "41 m" && V("ossf6n") == "Alice on Bob" && V("ossf6v") == "7 kills"
            && st.Classes["stf1"] && st.Classes["stf6"] && V("ossfnote") == "Counting since Tue 17 Nov");
        var you = st.YouOf(1001);
        Check($"...You, a player's own line: matches, hours, the favourite map, likes given, since, the place by matches; a player who only liked a map, and one never seen ({string.Join(" | ", you.Select(x => x.Text))})",
            you.Select(x => x.Text).SequenceEqual(["3 matches", "1.0 h", "Alpha  ·  2 matches", "1 like  ·  1 dislike", "since Tue 17 Nov", "#1 of 3 by matches"])
            && st.YouOf(1009).Select(x => x.Text).SequenceEqual(["No match yet", "", "", "1 like  ·  0 dislikes", "", ""]) && st.YouOf(4242)[0].Text == "No match yet");
        var round = HudStats.Decode(st.Encode());
        var (globalVars, globalClasses) = round!.Global();
        bool Bit(string cls) => globalClasses.Single(c => c.Class == cls).On;
        Check("Stats block: read back as sent - every word, class, bar, picture and every player's own line - and put on os_global alone: the page's classes and each "
            + "bar's step as its four bits (row 2's 3: stp2-1 and stp2-2; Arms Race's 6: stmar-2 and stmar-4), row 1 none - no panel of the page's own (content v4.16.0)",
            round.Encode() == st.Encode() && round.Vars.SequenceEqual(st.Vars) && round.You.Count == st.You.Count && round.YouOf(1001).SequenceEqual(you)
            && globalVars.Count == st.Vars.Count && globalClasses.All(c => c.Id == HudView.Global)
            && globalClasses.Count == HudStats.ClassNames.Length + HudStats.Bars.Length * HudStats.StepBits.Length
            && Bit("stp2-1") && Bit("stp2-2") && !Bit("stp2-4") && !Bit("stp2-8") && !Bit("stmar-1") && Bit("stmar-2") && Bit("stmar-4") && !Bit("stmar-8")
            && !globalClasses.Any(c => c.Class.StartsWith("stp1", StringComparison.Ordinal)) && HudStats.Decode("nonsense") == null);
        var empty = MakeStats([], new(), [], new Dictionary<string, int>(), D(19, 1, 0), null);
        Check("...with no history yet: the page says so (stnone), every row empty, every fun record hidden",
            empty.Classes["stnone"] && empty.Vars.Where(x => x.Name.StartsWith("ossr", StringComparison.Ordinal) || x.Name.StartsWith("osstp", StringComparison.Ordinal)).All(x => x.Text == "")
            && HudStats.ClassNames.Where(c => c.StartsWith("stf", StringComparison.Ordinal)).All(c => !empty.Classes[c]) && empty.Vars.Single(x => x.Name == "osstnw").Text == "none yet");

        // ---- the live bridge: a decided ballot kept, the block made again ----
        var h = new Harness("stats", new DateTime(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc));
        try
        {
            h.Fresh();
            HudActive = () => true; // the plugin draws: a match end opens the vote (the harness puts the bridge's own back)
            var alice = ("Alice", "[U:1:1001]");
            var bob = ("Bob", "[U:1:1002]");
            h.Join(alice); h.Join(bob);
            h.Run(5);
            h.MatchEnd();
            h.Run(2);
            var b = Ballot;
            var cards = b?.Options.Where(o => o.Source == VoteSource.Rolled).Take(2).ToList() ?? [];
            if (b != null && cards.Count == 2) { Command(alice.Item1, alice.Item2, MapVote.Click(b.Round, cards[0].N)); Command(bob.Item1, bob.Item2, MapVote.Click(b.Round, cards[0].N)); }
            h.Run(5);
            Console.WriteLine($"stats test ballot: {(b == null ? "none opened" : $"round {b.Round}, open {b.Open}, {b.Choices.Count} vote(s), {b.Options.Count} cards")}, now {(Ballot == null ? "no ballot" : $"open {Ballot.Open}")}");
            var kept = File.Exists(BallotsPath) ? File.ReadAllLines(BallotsPath) : [];
            var record = kept.Length == 1 ? BallotOf(kept[0]) : null;
            Check($"Ballots, live: a decided ballot is a line of ballots.jsonl - every card with its final votes, what won, how, who was there ({kept.Length} line)",
                b != null && record is { How: "all", Present: 2, Source: "live" } && record.Cards.Count == b.Options.Count && record.Cards.Sum(c => c.Votes) == 2
                && record.Won == Key(cards[0].Map!) && record.Cards.Single(c => c.Key == record.Won).Votes == 2 && Ballots.Count == 1 && record.Cards.Any(c => c.Key == RandomKey));
            var version = StatsNow.Version;
            Likes["3000000777"] = new() { ["[U:1:1001]"] = 1 };
            StatsDirty(); AfterWork();
            var after = StatsNow.Version;
            StatsDirty(); AfterWork(); // made again, the same words: the version stays
            Check("...the STATS block is made again after a change (a ballot, a match, a like, a crash) and its version moves only when its words do; the live state names it, /stats/ serves it",
                after > version && StatsNow.Version == after && LiveHud().StatsVersion == after && StatsText == StatsNow.Encode() && HudStats.Decode(StatsText)!.Vars.Any(x => x.Name == "osstv1n" && x.Text != ""));
            File.AppendAllText(BallotsPath, "a line that is no ballot\n");
            Check("...and a bridge reads them back at its start, a line that does not read skipped and said",
                LoadBallots().Contains("1 decided ballot(s)", StringComparison.Ordinal) && LoadBallots().Contains("1 line(s) that do not read skipped", StringComparison.Ordinal) && Ballots.Count == 1);
            // the lab's fake players vote on a test copy (deploy's bridgepost: Test A to Test D), but the record counts real humans alone (core's LabFakes)
            var fake = (LabFakes.Players[0].Name, LabFakes.SteamId(LabFakes.Players[0].Account));
            h.Join(fake);
            var withFake = new BallotState { Round = 4242 };
            var mapA = new MapInfo(1, 3000000001, "alpha", 100);
            var mapB = new MapInfo(2, 3000000002, "beta", 100);
            withFake.Options.Add(new BallotOption(VoteSource.Rolled, mapA, Modes.ArmsRace, 1) { N = 1 });
            withFake.Options.Add(new BallotOption(VoteSource.Rolled, mapB, Modes.ArmsRace, 2) { N = 2 });
            withFake.Choices[alice.Item2] = 1; withFake.Choices[bob.Item2] = 1; withFake.Choices[fake.Item2] = 2;
            var fakeTally = withFake.Options.ToDictionary(o => o.N, o => withFake.Choices.Values.Count(n => n == o.N));
            KeepBallot(withFake, withFake.Options[0], "all", fakeTally, PresentIds(Now()).Count);
            var fakeRecord = Ballots[^1];
            Check($"...a vote a fake player of the lab's took part in is kept with real humans alone: not among its present ({fakeRecord.Present} of {PresentIds(Now()).Count} on), "
                  + "none of its votes on the cards; what won stays the vote's own",
                PresentIds(Now()).Contains(fake.Item2) && fakeRecord is { Present: 2, How: "all" } && fakeRecord.Cards[0].Votes == 2 && fakeRecord.Cards[1].Votes == 0
                && fakeRecord.Won == Key(mapA));
            var keptBefore = Ballots.Count;
            var linesBefore = File.ReadAllLines(BallotsPath).Length;
            var fakesOnly = new BallotState { Round = 4243 };
            fakesOnly.Options.Add(new BallotOption(VoteSource.Rolled, mapA, Modes.ArmsRace, 1) { N = 1 });
            fakesOnly.Choices[fake.Item2] = 1;
            KeepBallot(fakesOnly, fakesOnly.Options[0], "all", new() { [1] = 1 }, 1); // as at the decision: the fake alone counted as present
            Check("...and a vote only fakes were at or voted in is kept nowhere, as the history keeps no match nobody human was in",
                Ballots.Count == keptBefore && File.ReadAllLines(BallotsPath).Length == linesBefore);
            // a vote on a map the bridge did not pick, that no list or pool names: its Keep card has no map, and kept it was recorded as Random (the live lab's S1 on rel-223)
            var keptUnknown = new BallotState { Round = 4244, KeepLabel = "aim_nobodys_map" };
            keptUnknown.Options.Add(new BallotOption(VoteSource.Keep, null, Modes.Wingman, 0) { N = 1 });
            keptUnknown.Options.Add(new BallotOption(VoteSource.Rolled, mapA, Modes.ArmsRace, 1) { N = 2 });
            keptUnknown.Options.Add(new BallotOption(VoteSource.Random, null, Modes.ArmsRace, 99) { N = 3 });
            keptUnknown.Choices[alice.Item2] = 1; keptUnknown.Choices[bob.Item2] = 1;
            KeepBallot(keptUnknown, keptUnknown.Options[0], "all", new() { [1] = 2, [2] = 0, [3] = 0 }, PresentIds(Now()).Count);
            var keptRecord = Ballots[^1];
            var keptOfficial = new BallotState { Round = 4245, KeepLabel = "de_inferno (official)" };
            keptOfficial.Options.Add(new BallotOption(VoteSource.Keep, new MapInfo(0, 0, "de_inferno", 0, true, Modes.WingmanId), Modes.Wingman, 0) { N = 1 });
            keptOfficial.Options.Add(new BallotOption(VoteSource.Random, null, Modes.Wingman, 99) { N = 2 });
            keptOfficial.Choices[alice.Item2] = 1;
            KeepBallot(keptOfficial, keptOfficial.Options[0], "all", new() { [1] = 1, [2] = 0 }, PresentIds(Now()).Count);
            var keptOfficialRecord = Ballots[^1];
            Check($"...a kept map is on record as the map it kept, never Random: with no map known, by the name the server logged ({keptRecord.Cards[0].Key}), in the mode it "
                  + "was kept in, its votes on it and won naming it; an official map by its name; Random stays Random",
                keptRecord is { Round: 4244, Won: "name:aim_nobodys_map" } && keptRecord.Cards[0] is { Key: "name:aim_nobodys_map", Name: "aim_nobodys_map", Mode: "wingman", Votes: 2 }
                && keptRecord.Cards[2] is { Key: RandomKey, Name: "Random" } && keptOfficialRecord is { Round: 4245, Won: "de_inferno" }
                && keptOfficialRecord.Cards[0] is { Key: "de_inferno", Name: "de_inferno", Mode: "wingman", Votes: 1 });
        }
        finally { h.Dispose(); }

        // ---- the ballots read back from the day logs ----
        const string Day = "2026-11-16";
        string[] dayLog =
        [
            Day + " 21:26:30 vote: round 1794860790 open for 60 s, called by Alice [U:1:1001], 3 present: 1. Keep playing this map (#362 Assault (CSO 2 port)) (keep, armsrace, liked) | 2. #4 Lake (picked, armsrace, big,liked) | 3. aim_red - Deathmatch (rolled, deathmatch, popular) | 4. de_ancient_night (official) - Deathmatch (rolled, deathmatch) | 99. Random (random, armsrace)",
            Day + " 21:26:31 vote: started by Alice [U:1:1001], with their vote on Random",
            Day + " 21:26:40 vote: Bob [U:1:1002] votes 2 (#4 Lake)",
            Day + " 21:26:45 vote: Cara [U:1:1003] votes 3 (aim_red - Deathmatch)",
            Day + " 21:26:50 vote: Cara [U:1:1003] votes 2 (#4 Lake), was 3",
            Day + " 21:26:55 vote: Alice [U:1:1001] added 11. Shortdust - Wingman and votes for it",
            Day + " 21:27:00 vote: round 1794860790 decided (time, 2 of 3 vote(s), 3 present): 2. #4 Lake -> #4 Lake in Arms Race",
            Day + " 21:40:00 vote: round 1794861000 open for 60 s at a match end, 2 present: 1. Play this map again (#4 Lake) (keep, armsrace, liked) | 2. #9 Unknown Map (rolled, armsrace) | 99. Random (random, armsrace)",
            Day + " 21:40:10 vote: Bob [U:1:1002] votes 1 (Play this map again (#4 Lake))",
            Day + " 21:40:20 vote: round 1794861000 decided (all, 1 of 2 vote(s), 2 present): keep playing this map - #4 Lake",
            Day + " 21:50:00 vote: round 1794861100 open for 60 s, called by Bob [U:1:1002], 2 present: 1. Keep playing this map (#4 Lake) (keep, armsrace) | 99. Random (random, armsrace)",
            Day + " 21:50:30 vote: round 1794861100 dropped - the server is restarting",
            Day + " 21:51:00 some other line",
        ];
        var lists = new Dictionary<string, List<MapInfo>>
        {
            [Modes.ArmsRaceId] = [new(4, 3100000004, "Lake", 900), new(362, 3100000362, "Assault (CSO 2 port)", 300)],
            [Modes.DeathmatchId] = [new(1, 3100000101, "aim_red", 30, Mode: "deathmatch")],
            [Modes.WingmanId] = [new(1, 3100000201, "Shortdust", 40, Mode: "wingman"), new(2, 3100000202, "ShortDust", 50, Mode: "wingman")],
        };
        var (back, words) = BallotsBackfill([dayLog], TimeZoneInfo.Utc, null, lists);
        Check($"Ballots backfill: every decided ballot of the day logs, a dropped one none ({words})",
            back.Count == 2 && words.StartsWith("ballots backfill: 2 decided ballot(s)", StringComparison.Ordinal) && back.All(r => r.Source == "backfill"));
        var first = back.Count == 2 ? back[0] : null;
        Check("...its cards from the open line - a kept map by the label inside its words, a list's place and a mode left out, an official map by its name - and a card added later; each player's last vote counts",
            first is { Round: 1794860790, Mode: "armsrace", How: "time", Present: 3, Won: "3100000004" }
            && first.Cards.Select(c => (c.Key, c.Votes)).SequenceEqual([("3100000362", 0), ("3100000004", 2), ("3100000101", 0), ("de_ancient_night", 0), ("3100000201", 1), (RandomKey, 0)])
            && first.Cards.Single(c => c.Key == "3100000201").Mode == "wingman");
        Check("...a keep decision is the kept map's card; a name no list has stays a name, and the summary counts it",
            back.Count == 2 && back[1] is { How: "all", Won: "3100000004" } && back[1].Cards.Any(c => c.Key == "name:Unknown Map") && words.Contains("1 not: Unknown Map", StringComparison.Ordinal));
        var line = BallotLine(first!);
        Check("...and a ballot's line reads back the same", BallotOf(line) is { } again && again.Cards.SequenceEqual(first!.Cards) && again.At == first.At && again.Won == first.Won && BallotOf("nope") == null);

        // ---- the lab's seed (bridge --lab-seed DIR): made-up players on maps with pictures, every tile of the STATS page with something on it ----
        var seedDir = Path.Combine(Path.GetTempPath(), $"os2-selftest-seed-{Environment.ProcessId}");
        try
        {
            var mapsBefore = MapsFile;
            var made = LabSeedCommand([seedDir, "--zone", "UTC"]);
            var seedEntries = File.ReadAllLines(Path.Combine(seedDir, "history.jsonl")).Where(x => x.Trim() != "").Select(HudHistory.FromJsonLine).ToList();
            var seedBallots = File.ReadAllLines(Path.Combine(seedDir, "ballots.jsonl")).Where(x => x.Trim() != "").Select(BallotOf).ToList();
            var seedLikes = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(seedDir, "votes.json")), Ctx.Default.DictionaryStringDictionaryStringInt32) ?? new();
            var newest = seedEntries.Where(e => e != null).Select(e => e!).OrderByDescending(e => e.End).ToList();
            var seedStats = MakeStats(newest, seedLikes, [.. seedBallots.Where(b => b != null).Select(b => b!)], new Dictionary<string, int>(),
                DateTimeOffset.UtcNow, newest.Count > 0 ? newest[^1].Start.UtcDateTime : null);
            var empties = seedStats.Vars.Where(x => x.Text == "").Select(x => x.Name).ToList();
            var names = newest.SelectMany(e => e.Players).Select(p => p.Name).Distinct().ToList();
            var keys = newest.Select(e => e.Key).Distinct().ToList();
            var pictured = keys.Count(k => HudThumbs.Classes(new HudCatalog.Entry(k, k, 1, long.TryParse(k, out var id) ? id : 0, k, 1, !long.TryParse(k, out _), -1, 0, 0, 0)).Length > 0);
            var seedYou = seedStats.YouOf(9001).Select(x => x.Text).ToList();
            Check($"Lab seed: {newest.Count} matches over {newest.Select(e => e.From.Date).Distinct().Count()} evenings, {names.Count} made-up players ({string.Join(", ", names.Take(5))} ...), "
                  + $"{seedBallots.Count} ballots, {seedLikes.Count} liked maps - every line reads, every map has a picture, every tile of the STATS page and You have something "
                  + $"({(empties.Count == 0 ? "none empty" : "empty: " + string.Join(", ", empties))})",
                made == 0 && MapsFile == mapsBefore && seedEntries.All(e => e != null) && seedBallots.All(b => b != null) && newest.Count >= 12 && newest.Select(e => e.From.Date).Distinct().Count() >= 4
                && names.Count >= 6 && names.Take(4).All(n => n.StartsWith("Test ", StringComparison.Ordinal)) && pictured == keys.Count && empties.Count == 0
                && HudStats.ClassNames.Where(c => c != "stnone").All(c => seedStats.Classes.GetValueOrDefault(c)) && !seedStats.Classes["stnone"] && seedYou.All(x => x != "")
                && newest.All(e => e.Players.All(p => p.Frags != null)) && newest.Any(e => e.Winner > 0) && newest.Any(e => e.Pairs?.Count > 0) && newest.Any(e => e.End.Hour < 5));
        }
        finally { try { Directory.Delete(seedDir, true); } catch (IOException) { } }
    }
}
