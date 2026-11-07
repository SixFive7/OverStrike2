// --selftest, the vote for the next map (the only way a player changes the map) and the game modes:
// VoteRound.cs, ModeSwitch.cs, MapVote.cs, Modes.cs. The vote runs in the harness (SelfTestHarness.cs), with a fake game server that
// remembers game_type and game_mode; every rule docs/reference.md states has a check here - the ballot's shape and
// its page, the picks while it is open, everybody voting, the timer, ties, no votes, Keep and Play this map again, the wait after a vote, a
// Wingman map with five on, the modes' map loads, the defaults after a restart and after 10 minutes with nobody on, the per-mode lists,
// weights, block lists and server cfgs.

static partial class Program
{
    static void VoteSelfTest(Action<string, bool> Check)
    {
        // ---- the shared parts: the click, the codec, the mode table ----
        Check("Vote click: !mv <round> <n> passes, anything else does not; Random is option 99, above every card",
            MapVote.IsAction("!mv 1793000000 3") && MapVote.IsAction("!mv 5 0") && MapVote.IsAction($"!mv 5 {MapVote.RandomOption}") && !MapVote.IsAction("!mv 3") && !MapVote.IsAction("!mv x 3")
            && !MapVote.IsAction("!mv 1 2 3") && !MapVote.IsAction("!mv 1 100") && !MapVote.IsAction("!mv  1 2") && !MapVote.IsAction("!map 1 2")
            && MapVote.ParseClick("!mv 17 4") is (17, 4) && MapVote.ParseClick("!mv 17") is null && MapVote.Click(17, 4) == "!mv 17 4" && MapVote.MaxMaps < MapVote.RandomOption);
        Check("Vote click: the menu's own actions stay what they were, and a vote click is not one of them", !Hud.IsAction("!mv 1 2") && Hud.IsAction("!up"));
        Check("The wait's words: minutes and seconds, or seconds", MapVote.Left(80) == "1 min 20 s" && MapVote.Left(45) == "45 s" && MapVote.Left(0) == "1 s");
        var codec = new VoteState
        {
            Round = 1793000000, Open = false, SecondsLeft = 7, Present = 5, Cast = 4, MatchEnded = true, Freeze = true, StarterName = "Alice\tB", DoneSeconds = 8,
            Options = [new(1, "armsrace", VoteSource.Picked, "3070000012", "#12 aim\tx\nwith a very long name that goes on and on", "17 MB", 2, "Alice and Bob"),
                       new(2, "wingman", VoteSource.Rolled, "de_nuke", "de_nuke (official)", "", 1, ""),
                       new(3, "deathmatch", VoteSource.Keep, "de_dust2", "Play this map again", "de_dust2 (official)", 0, ""),
                       new(MapVote.RandomOption, "deathmatch", VoteSource.Random, "", "Random", "", 1, "")],
            Choices = new() { [2001] = 1, [2002] = 1, [2003] = 2, [2004] = MapVote.RandomOption },
            Result = new VoteState.Outcome(1, "most", "#12 aim x"),
        };
        var hudText = new HudState { Map = "#1 x" }.Encode() + codec.Encode();
        var back = VoteState.Decode(hudText);
        Check("Vote codec: what goes out comes back - the vote (a match end's, everybody frozen), its cards (Keep, Random last), the choices and the result",
            back.Round == codec.Round && !back.Open && back.SecondsLeft == 7 && back.Present == 5 && back.Cast == 4 && back.MatchEnded && back.Freeze && back.WaitFor == 0 && back.StarterName == "Alice B" && back.DoneSeconds == 8
            && back.Options.Count == 4 && back.Options[0] is { N: 1, Mode: "armsrace", Source: VoteSource.Picked, Key: "3070000012", Size: "17 MB", Votes: 2, Who: "Alice and Bob" }
            && back.Options[0].Label.Length <= 40 && !back.Options[0].Label.Contains('\t') && back.Options[2] is { Source: VoteSource.Keep, Label: "Play this map again" }
            && back.Options[3] is { Source: VoteSource.Random, N: MapVote.RandomOption } && back.Maps.Count == 3 && back.Random?.N == MapVote.RandomOption
            && back.Choices.Count == 4 && back.ChoiceOf(2003) == 2 && back.ChoiceOf(9) == 0 && back.Result is { N: 1, How: "most", Label: "#12 aim x" });
        var lines = codec.Encode(endTimes: true).Split('\n');
        Check("Vote codec: nothing goes out that no plugin reads - no mode lines; mv's 7th field and mvo's 4th, which the plugin reads by place, stay in place and empty; mvr ends at its label",
            !lines.Any(l => l.StartsWith("mode\t") || l.StartsWith("mi\t")) && lines.Single(l => l.StartsWith("mv\t")).Split('\t') is { Length: 13 } mv && mv[6] == "end" && mv[7] == "0" && mv[8] == "1"
            && lines.Where(l => l.StartsWith("mvo\t")).All(l => l.Split('\t') is { Length: 11 } o && o[4] == "" && o[5] == codec.Numbered(int.Parse(o[1]))!.Key)
            && lines.Single(l => l.StartsWith("mvr\t")) == "mvr\t1\tmost\t#12 aim x");
        Check("Vote codec: the HUD's own state reads the same text unharmed, and a text without vote lines is no vote",
            HudState.Decode(hudText)?.Map == "#1 x" && VoteState.Decode(new HudState().Encode()) is { Round: 0, Options.Count: 0 } && VoteState.Decode(null).Round == 0
            && VoteState.Decode("mvo\t1\tarmsrace\tpicked\t1\t1\tx\t\t1\t\n").Options.Count == 0 && VoteState.Decode("mvo\t1\tarmsrace\tqueue\t1\t1\tx\t\t1\t\nmv\t1\topen\t1\t1\t0\tmid\t0\t1\n").Options.Count == 0);
        Check("Modes: ids unique and plain, Arms Race first and the default, the convar pairs unique and Valve's (1/0, 1/2, 0/2)",
            Modes.All.Select(m => m.Id).Distinct().Count() == Modes.All.Length && Modes.All.All(m => m.Id.All(char.IsAsciiLetterLower)) && Modes.Default == Modes.ArmsRace
            && Modes.All[0] == Modes.ArmsRace && Modes.All.Select(m => (m.GameType, m.GameMode)).Distinct().Count() == Modes.All.Length
            && (Modes.ArmsRace.GameType, Modes.ArmsRace.GameMode, Modes.Deathmatch.GameType, Modes.Deathmatch.GameMode, Modes.Wingman.GameType, Modes.Wingman.GameMode) == (1, 0, 1, 2, 0, 2)
            && Modes.All.All(m => Modes.FromConVars(m.GameType, m.GameMode) == m) && Modes.FromConVars(0, 0) is null);
        Check("Modes: an empty id is Arms Race (the map entries from before the modes), an unknown one nothing, Of falls back to the default",
            Modes.Find("") == Modes.ArmsRace && Modes.Find(null) == Modes.ArmsRace && Modes.Find("Deathmatch") == Modes.Deathmatch && Modes.Find("casual") is null && Modes.Of("casual") == Modes.ArmsRace);
        Check("Modes: Valve's maxplayers - Wingman 4, the others 16 - and what the bridge sends to load a map in a mode",
            Modes.Wingman.MaxPlayers == 4 && Modes.Wingman.Suits(4) && !Modes.Wingman.Suits(5) && Modes.Deathmatch.Suits(10)
            && Modes.PrepareLine(Modes.Deathmatch) == "os_mode deathmatch;game_type 1;game_mode 2" && Modes.SyncLine(Modes.Wingman) == "os_mode wingman"
            && Modes.Deathmatch.MapsFile == "maps-deathmatch.json" && Modes.ArmsRace.MapsFile == "maps.json" && Modes.BlockFile == "blocked.txt");
        Check($"Modes: Valve's official pools - Deathmatch {Modes.Deathmatch.Official.Length} maps, Wingman {Modes.Wingman.Official.Length}, Arms Race {Modes.ArmsRace.Official.Length} - each map once, Arms Race's the bridge's OFFICIAL_MAPS",
            Modes.Deathmatch.Official.Length == 15 && Modes.Wingman.Official.Length == 7 && Modes.All.All(m => m.Official.Distinct().Count() == m.Official.Length)
            && Modes.ArmsRace.Official.Order().SequenceEqual(Officials.Select(o => o.Name).Order()));

        // ---- the harness (SelfTestHarness.cs), and a fake server that remembers game_type and game_mode ----
        var h = new Harness("vote", new DateTime(2026, 11, 1, 20, 0, 0, DateTimeKind.Utc));
        ModeMapsDir = h.Dir;
        int gameType = 1, gameMode = 0, statusHumans = 0;
        var onOfficial = true;            // the fake server's map: official (the boot map) or a Workshop map
        (int, int) snapshot = (1, 0);     // what CS2 saved when it last went from an official map to a Workshop map
        var fakePlugin = false;           // the plugin on the fake server too: it answers os_mode (Settings.SetMode), and its block has the told values back in the convars at once
        Modes.Mode? fakeTold = null, fakeLoaded = Modes.ArmsRace, fakeLoadsAs = null; // what os_mode said last; the mode the map on loaded in (the convars at its start); a load that ignores its convars
        h.Server = command =>
        {
            foreach (var part in command.Split(';'))
                switch (part.Trim().Split(' '))
                {
                    case ["os_mode", var id]: fakeTold = Modes.Find(id); break;
                    case ["game_type", var t]: gameType = int.Parse(t); break;
                    case ["game_mode", var g]: gameMode = int.Parse(g); break;
                    case ["host_workshop_map", _]: if (onOfficial) snapshot = (gameType, gameMode); onOfficial = false; fakeLoaded = fakeLoadsAs ?? Modes.FromConVars(gameType, gameMode); break; // official -> Workshop: CS2 saves every convar
                    case ["changelevel", _]: if (!onOfficial) (gameType, gameMode) = snapshot; onOfficial = true; fakeLoaded = fakeLoadsAs ?? Modes.FromConVars(gameType, gameMode); break;      // Workshop -> official: ...and restores them
                }
            if (fakePlugin && fakeTold is { } toldNow)
            {
                (gameType, gameMode) = (toldNow.GameType, toldNow.GameMode);
                if (command == Modes.SyncLine(toldNow))
                    return fakeLoaded == toldNow ? $"os_mode: mode {toldNow.Id}: on already"
                        : $"os_mode: mode {toldNow.Id}: game_type {toldNow.GameType}, game_mode {toldNow.GameMode} kept; its block list and features at the next map start";
            }
            return command == "game_type" ? $"game_type = {gameType}" : command == "game_mode" ? $"game_mode = {gameMode}"
                : command == "status" ? $"hostname : test\nspawn    : 2\nplayers  : {statusHumans} humans, 0 bots (0 max)\n#end" : "";
        };
        var loads = 0;
        try
        {
            void Land() => h.Land(() => S.Map is { Official: true } o ? o.Name : $"vote_bsp_{++loads}", settle: 2); // an official map loads by its name
            void Fresh() // a bridge that has just started (ResetAll), with the plugin drawing the vote, on a fake server in Arms Race on its boot map
            {
                h.Fresh();
                HudActive = () => true; ModeProbeEnabled = false;
                (gameType, gameMode, statusHumans) = (1, 0, 0); (onOfficial, snapshot) = (true, (1, 0));
                (fakePlugin, fakeTold, fakeLoaded, fakeLoadsAs) = (false, null, Modes.ArmsRace, null);
            }
            void Settle() => VoteWaitUntil = default; // the 2 minutes after a vote, waived for the tests that are not about them
            void Click((string Name, string Id) p, int n, long? round = null) => HudAct($"{p.Id}\t{p.Name}\t{MapVote.Click(round ?? Ballot?.Round ?? 1, n)}");
            void Pick((string Name, string Id) p, MapInfo map) => Command(p.Name, p.Id, $"!map {ArgOf(map)}");
            // A pick that goes through, for the tests about the modes: the wait waived, and every player present votes for it.
            void Ask((string Name, string Id) p, MapInfo map)
            {
                Settle();
                Pick(p, map);
                if (Ballot is { Open: true } b && b.Options.FirstOrDefault(x => x.Map != null && Key(x.Map) == Key(map)) is { } card)
                    foreach (var id in PresentIds(h.Clock).Where(x => !b.Choices.ContainsKey(x)).ToList()) CastBallot(Players[id].Name, id, $"{b.Round} {card.N}");
            }
            BallotOption Opt(int n) => Ballot!.Options.First(o => o.N == n); // by its number
            int OptOf(Func<BallotOption, bool> pick) => Ballot!.Options.First(pick).N;

            var (alice, bob, cara, dan, eve) = (("Alice", "[U:1:2001]"), ("Bob", "[U:1:2002]"), ("Cara", "[U:1:2003]"), ("Dan", "[U:1:2004]"), ("Eve", "[U:1:2005]"));
            var small = Maps.Where(m => m.SizeMb < 400 && m.Rank > 0).ToList();
            var m = Enumerable.Range(0, 12).Select(i => small[30 + i * 11]).ToList(); // twelve ordinary, distinct Arms Race maps

            // ---- no plugin that draws the vote: a match end rolls the dice, as on a vanilla server ----
            Fresh();
            HudActive = () => false;
            foreach (var p in new[] { alice, bob, cara }) h.Join(p);
            h.MatchEnd();
            Check("No plugin draws the vote: a match end rolls the dice", Ballot is null && Pending is { Auto: true, By: "the dice" } && h.Said("Match over - Rolling the dice"));

            // ---- the ballot at a match end: Play this map again, the server's mode to a page, one map of each other mode, Random ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) h.Join(p);
            Ask(alice, m[0]); Land(); // the map that is on
            var pageMaps = Maps.Skip(1040).Take(MapVote.PageCards).ToList();
            foreach (var x in Maps.Except(pageMaps).Where(x => x != m[0])) S.Crashes[Key(x)] = 1; // every other map has crashed: the dice can only take these
            var dmLeft = Modes.Deathmatch.Official.Take(2).ToHashSet();
            foreach (var name in Modes.Deathmatch.Official.Skip(2)) Broken[name] = 3; // and Deathmatch can only take two
            h.MatchEnd();
            Console.WriteLine("ballot: " + string.Join(" | ", Ballot?.Options.Select(o => $"{o.N}. {OptionLabel(Ballot, o)} ({o.Source}, {o.Mode.Id})") ?? []));
            var fill = MapVote.PageCards - 3; // the server's own mode fills what Keep and the two other modes leave of the page
            Check($"A match end with the plugin drawing it opens the vote at once, the wait after the last vote notwithstanding: a page of {MapVote.PageCards} cards numbered 1 to {MapVote.PageCards} and Random (option {MapVote.RandomOption}), {MapVote.Seconds} s, in chat and on the board",
                Ballot is { Open: true, MatchEnded: true } b1 && b1.Options.Count == MapVote.PageCards + 1 && b1.Options[^1] is { Source: VoteSource.Random, Map: null, N: MapVote.RandomOption }
                && (b1.Ends - b1.Opened).TotalSeconds == MapVote.Seconds && b1.Options.SkipLast(1).Select(o => o.N).SequenceEqual(Enumerable.Range(1, MapVote.PageCards))
                && Pending is null && h.Said($"Match over - Vote for the next map: {MapVote.Seconds} s, or until all 3 have voted"));
            Check($"...Play this map again first, then {fill} Arms Race maps, then one Deathmatch and one Wingman map (3 on: Wingman suits)",
                Opt(1) is { Source: VoteSource.Keep } k1 && k1.Map == m[0]
                && Ballot!.Options.Skip(1).Take(MapVote.PageCards - 1).Select(o => o.Mode.Id).SequenceEqual(Enumerable.Repeat("armsrace", fill).Concat(["deathmatch", "wingman"]))
                && Ballot.Options.Skip(1).Take(MapVote.PageCards - 1).All(o => o.Source == VoteSource.Rolled));
            Check("...each map once, never the map that is on among the random ones, and only maps the dice may roll (no crasher, no broken map)",
                Ballot!.Options.Skip(1).Take(MapVote.PageCards - 1).Select(o => Key(o.Map!)).Distinct().Count() == MapVote.PageCards - 1 && Ballot.Options.Skip(1).All(o => o.Map == null || Key(o.Map) != Key(m[0]))
                && Ballot.Options.Skip(1).Take(fill).All(o => pageMaps.Contains(o.Map!)) && dmLeft.Contains(Ballot.Options[fill + 1].Map!.Name) && Modes.Wingman.Official.Contains(Ballot.Options[fill + 2].Map!.Name)
                && Ballot.Options[fill + 1].Map! is { Official: true, Mode: "deathmatch" });
            Check("...and nobody's vote is cast yet", Ballot.Choices.Count == 0);
            var hud = LiveVote();
            Check("The live state carries it: the vote (a match end's, nobody's call, everybody frozen), its cards with their modes and how they got there",
                hud.Round == Ballot.Round && hud.Open && hud.MatchEnded && hud.Freeze && hud.StarterName == "" && hud.SecondsLeft == MapVote.Seconds && hud.Present == 3
                && hud.Waiting.SequenceEqual(["Alice", "Bob", "Cara"]) && hud.Voters.Count == 0 && hud.Options.Count == MapVote.PageCards + 1
                && hud.Options[0] is { Source: VoteSource.Keep, Label: "Play this map again" } keepCard && keepCard.Key == Key(m[0]) && keepCard.Size == Clip(Label(m[0]), 40)
                && hud.Options[^1] is { Source: VoteSource.Random, Label: "Random", Key: "" } && hud.Options.Skip(1).Take(MapVote.PageCards - 1).All(o => o.Source == VoteSource.Rolled && o.Key != "" && o.Label.Length <= 40));

            // ---- fewer maps eligible than a page: the ballot holds what the dice find, never a map twice ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) h.Join(p);
            var three = Maps.Skip(1040).Take(3).ToList();
            foreach (var x in Maps.Except(three)) S.Crashes[Key(x)] = 1; // three Arms Race maps left on either list
            h.MatchEnd();
            Check("Only three Arms Race maps may roll: the three, one Deathmatch, one Wingman map and Random - no repeat to make up the number; no map known to be on, so no Keep card",
                Ballot!.Options.Where(o => o.Mode == Modes.ArmsRace && o.Map != null).Select(o => o.Map!).ToHashSet().SetEquals(three) && Ballot.Options.Count(o => o.Map != null) == 5
                && Ballot.Options[^1].Source == VoteSource.Random && Ballot.Options.All(o => o.Source != VoteSource.Keep));

            // ---- five on: Wingman (Valve's maxplayers 4) is no card, no pick, and no winner ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara, dan, eve }) h.Join(p);
            h.MatchEnd();
            Check($"Five on: no Wingman card - {MapVote.PageCards - 1} Arms Race maps, one Deathmatch map, Random",
                Ballot!.Options.Select(o => o.Mode.Id).SequenceEqual(Enumerable.Repeat("armsrace", MapVote.PageCards - 1).Concat(["deathmatch", "armsrace"]))
                && LiveVote().Options.All(o => o.Mode != Modes.WingmanId));
            var wing = PoolOf(Modes.Wingman).First(x => x.Official);
            ClearNotices(); Command(alice.Item1, alice.Item2, $"!map {wing.Name} wingman");
            Check("...a Wingman map cannot be picked with five on, during a vote or not", h.Said("Wingman takes 4 players") && Ballot!.Options.All(o => o.Mode != Modes.Wingman));
            Fresh();
            foreach (var p in new[] { alice, bob, cara, dan }) h.Join(p); // four on: Wingman suits
            h.MatchEnd();
            var wn = OptOf(o => o.Mode == Modes.Wingman);
            var arN = OptOf(o => o.Mode == Modes.ArmsRace && o.Source == VoteSource.Rolled);
            h.Join(eve); // a fifth joins during the vote: counted, and waited for
            Click(alice, wn); Click(bob, wn); Click(cara, wn); Click(dan, arN); Click(eve, arN);
            Check("...nor can its card win once five are on: the next most votes do", Ballot is { Open: false } && Pending?.Map == Opt(arN).Map && ModeOf(Pending!.Map) != Modes.Wingman);

            // ---- alone, one vote ends it at once ----
            Fresh();
            h.Join(alice);
            h.MatchEnd(); Click(alice, 1);
            Check("Alone, one vote ends it at once", Ballot is { Open: false, Result.How: "all" } && Pending is { By: "the vote" });

            // ---- votes change and go back as often as a player likes; a picked map without votes leaves; the settle time ----
            Fresh();
            foreach (var p in new[] { alice, bob }) h.Join(p);
            var solo = m[5];
            Pick(alice, solo);
            Click(alice, 0);
            Check("Her pick's own vote taken back: the map she put on the ballot has no votes left and leaves it, until somebody puts it on again",
                Ballot is { Open: true } sb && sb.Options.All(o => o.Map != solo) && sb.Choices.Count == 0);
            Pick(bob, solo);
            Check("...as Bob does: back on, with his vote", Ballot!.Options.SingleOrDefault(o => o.Map == solo) is { } back2 && Ballot.Choices[bob.Item2] == back2.N);
            MapVote.InactivitySeconds = 5;
            try
            {
                var (r1, r2) = (Ballot!.Options.Where(o => o.Source == VoteSource.Rolled).ElementAt(0).N, Ballot.Options.Where(o => o.Source == VoteSource.Rolled).ElementAt(1).N);
                Click(alice, r1);
                Check($"The inactivity timer ({MapVote.InactivitySeconds} s): everybody has voted, the vote stays open, the screen counts it down and names who reset it",
                    Ballot is { Open: true } && LiveVote() is { Inactivity: 5, Waiting.Count: 0, ResetBy: "Alice" } st && HudVote.WaitLine(st) == "Everybody has voted" && HudVote.Clocks(st) is { IdleNum: "5 s", Idle: "Reset by Alice" } && HudVote.InactivityLine(st) == "Inactivity timer 5 s - Reset by Alice");
                h.Run(3); Click(alice, r2);
                h.Run(3);
                Check("...a change starts it again: 3 s after the change, still open", Ballot is { Open: true });
                h.Run(1); HudAct($"{bob.Item2}	{bob.Item1}	{MapVote.Busy(Ballot!.Round)}");
                Check("...and so does work in the map browser that reaches the server (!mvb <round>, a page, a sort, a filter, a search): now Bob reset it",
                    Ballot is { Open: true } && LiveVote() is { Inactivity: 5, ResetBy: "Bob" } && MapVote.IsAction(MapVote.Busy(17)) && !MapVote.IsAction("!mvb") && !MapVote.IsAction("!mvb 1 2"));
                HudAct($"{alice.Item2}	{alice.Item1}	{MapVote.Busy(Ballot!.Round - 1)}");
                Check("...a stale round's activity is no activity", LiveVote().ResetBy == "Bob");
                h.Run(4);
                Check("...still open 4 s after it", Ballot is { Open: true });
                h.Run(1);
                Check($"...and {MapVote.InactivitySeconds} s after the last activity it closes", Ballot is { Open: false } && Pending is { By: "the vote" });
            }
            finally { MapVote.InactivitySeconds = 0; }

            // ---- the vote's window (HudVote.Cards): who started it, its two clocks and its words in every state; what chat and the board say ----
            Check($"The limit's bar: w10 from {MapVote.Seconds} s up, then a step down every {HudVote.LimitStepSeconds} s, w0 for the last of them; one class of the eleven at a time",
                HudVote.LimitStepSeconds * 10 == MapVote.Seconds && new[] { (61, 10), (60, 10), (59, 9), (55, 9), (54, 8), (38, 6), (27, 4), (7, 1), (6, 0), (1, 0), (0, 0) }.All(x => HudVote.LimitStep(x.Item1) == $"w{x.Item2}")
                && HudVote.LimitClasses("x", 30).Count() == 11 && HudVote.LimitClasses("x", 30).Single(c => c.On) is { Id: "x", Class: "w4" });
            string VarOf(VoteState v, string name) => HudVote.Cards(v, 2001, null).Vars.Single(x => x.Name == name).Text;
            string TextOf(VoteState v, string id) => HudVote.Cards(v, 2001, null).Texts.Single(x => x.Id == id).Text;
            bool ClassOn(VoteState v, string id, string cls) => HudVote.Cards(v, 2001, null).Classes.Single(x => x.Id == id && x.Class == cls).On;
            string StepOf(VoteState v, string id) => HudVote.Cards(v, 2001, null).Classes.Single(x => x.Id == id && x.On).Class;
            string Header(VoteState v) => string.Join(" | ", new[] { HudVote.StartVar, HudVote.WaitVar, HudVote.LimitNumVar, HudVote.LimitVar, HudVote.IdleKeyVar, HudVote.IdleNumVar, HudVote.IdleVar }.Select(n => VarOf(v, n)));
            bool Capitals(IEnumerable<string> lines) => lines.All(l => Sections.Lower(l) == null);
            VoteState WinState() => LiveVote();
            var fay = ("Fay", "[U:1:2006]");
            var six = new[] { alice, bob, cara, dan, eve, fay };
            void WinStart(int inactivity) // six on, a map on (so Keep is on the ballot), the wait waived, nothing said yet
            {
                MapVote.InactivitySeconds = 0;
                Fresh();
                foreach (var p in six) h.Join(p);
                Ask(dan, m[7]); Land(); Settle(); ClearNotices();
                MapVote.InactivitySeconds = inactivity;
            }
            string VoteState1(string kind, string how, int votes) => $"mode\tarmsrace\tArms Race\nmv\t9\tdone\t8\t3\t3\t{kind}\t0\t1\nmvo\t1\tarmsrace\tkeep\t\tk\tKeep playing this map\t#5 x\t{votes}\t\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t{votes}\t\nmvr\t{(how == "random" || how == "rnone" ? 99 : 1)}\t{(how == "random" ? "all" : how == "rnone" ? "none" : how)}\t#5 x\tarmsrace\n";
            Check("What came of the vote, in the window's words: kept, kept in a tie, nobody voted, a match end's Play this map again; the dice's part in it",
                HudVote.DoneLine(VoteState.Decode(VoteState1("mid", "all", 2))) == "The vote kept #5 x (2 of 3)" && HudVote.DoneLine(VoteState.Decode(VoteState1("mid", "tie", 1))) == "The vote kept #5 x (1 of 3) - A tie, drawn by the dice"
                && HudVote.DoneLine(VoteState.Decode(VoteState1("mid", "none", 0))) == "Nobody voted - #5 x stays" && HudVote.DoneLine(VoteState.Decode(VoteState1("end", "all", 3))) == "#5 x again (3 of 3) - Its next match starts on it"
                && HudVote.DoneLine(VoteState.Decode(VoteState1("mid", "random", 2))) == "Random won (2 of 3) - The dice rolled #5 x" && HudVote.DoneLine(VoteState.Decode(VoteState1("end", "rnone", 0))) == "Nobody voted - The dice rolled #5 x");
            Check("A match end's vote has nobody to name; an open vote's last 10 s are vlow, one second before them not; the browser's page takes vdone and vlow off",
                HudVote.StartLine(VoteState.Decode("mv\t9\topen\t8\t3\t0\tend\t0\t1\n")) == "Match over" && ClassOn(VoteState.Decode($"mv\t9\topen\t{HudVote.LowSeconds}\t3\t0\tmid\t0\t1\n"), HudView.Screen, "vlow")
                && !ClassOn(VoteState.Decode($"mv\t9\topen\t{HudVote.LowSeconds + 1}\t3\t0\tmid\t0\t1\n"), HudView.Screen, "vlow") && !ClassOn(VoteState.Decode(VoteState1("mid", "all", 2)), HudView.Screen, "vlow")
                && HudMenu.Maps(null, new HudState(), new HudBrowser(), 1, h.Clock).Classes is var browserPage && browserPage.Contains((HudView.Screen, "vdone", false)) && browserPage.Contains((HudView.Screen, "vlow", false)));
            Check("A risky card's lock, with its capitals: Locked - Click to unlock, then Click again - Vote anyway",
                HudMenu.Locked == "Locked - Click to unlock" && HudMenu.Unlocked == "Click again - Vote anyway" && Capitals([HudMenu.Locked, HudMenu.Unlocked]));
            Check("The capitals rule leaves a map's own words alone: the dash in a marked title ends no section; everybody's run-end check reads every line of the vote's (MessagesCapitalsCheck)",
                Sections.Lower($"Bob voted for {M.A("aim_x - by Somebody")} - {M.V("1 of 2")} voted") == null && Sections.Lower("Bob voted for aim_x - by Somebody") == "by Somebody");
            if (File.Exists(HudLayoutFile))
                Check("The layout has a label for each of the header's variables", new[] { HudVote.StartVar, HudVote.WaitVar, HudVote.LimitNumVar, HudVote.LimitVar, HudVote.IdleKeyVar, HudVote.IdleNumVar, HudVote.IdleVar }.All(n => File.ReadAllText(HudLayoutFile).Contains($"text=\"{{s:{n}}}\"")));

            WinStart(5);
            StartVote(alice.Item1, alice.Item2);
            var startedHud = BuildHud(h.Clock);
            Check("The vote started: who started it stands in the window nobody can close; the line for everybody goes to chat and the board, no top line over the window",
                VarOf(WinState(), HudVote.StartVar) == "Started by Alice" && startedHud.TopText == "" && startedHud.Says.Count == 1
                && M.Plain(startedHud.Says[0].Text) == $"Alice started a vote for the next map: {MapVote.Seconds} s, or until all 6 have voted" && startedHud.Says[0].Text.Contains(M.N("Alice")) && startedHud.Says[0].Text.Contains(M.V($"{MapVote.Seconds} s"))
                && startedHud.Lately.Any(l => l.Text == M.Plain(startedHud.Says[0].Text)));
            var (winA, winB) = (Ballot!.Options.Where(o => o.Source == VoteSource.Rolled).ElementAt(0).N, Ballot.Options.Where(o => o.Source == VoteSource.Rolled).ElementAt(1).N);
            try
            {
                h.Run(22);
                Click(alice, winA); Click(bob, winA); Click(cara, winA); Click(dan, winB);
                var w1 = WinState();
                Check("Voting: who has not voted, the limit's digits and how many have, the inactivity row waiting; votes/present on the tiles; the limit's bar at its step",
                    Header(w1) == "Started by Alice | Waiting for: Eve, Fay | 38 s | 4 of 6 voted | Inactivity timer |  | Starts when everybody has voted"
                    && ClassOn(w1, HudView.Screen, "voting") && !ClassOn(w1, HudView.Screen, "vdone") && !ClassOn(w1, HudView.Screen, "vlow")
                    && StepOf(w1, HudVote.LimitBar) == "w6" && StepOf(w1, HudVote.IdleBar) == "w0" && TextOf(w1, "gg_m1_score") == "3/6" && TextOf(w1, "gg_m2_score") == "1/6" && TextOf(w1, "gg_m3_score") == "0/6");
                h.Run(11);
                Click(eve, winA); Click(fay, winB);
                var w2 = WinState();
                Check("Everybody voted: the limit runs on, the inactivity timer counts and names who reset it; a reset jumps its bar to full",
                    Header(w2) == "Started by Alice | Everybody has voted | 27 s | 6 of 6 voted | Inactivity timer | 5 s | Reset by Fay" && StepOf(w2, HudVote.LimitBar) == "w4" && StepOf(w2, HudVote.IdleBar) == "w10");
                h.Run(2);
                var w3 = WinState();
                Check("...its bar a step a second", VarOf(w3, HudVote.IdleNumVar) == "3 s" && StepOf(w3, HudVote.IdleBar) == "w6");
                h.Run(3);
                var won = Clip(Label(Opt(winA).Map!), 40);
                var w4d = WinState();
                var decidedHud = BuildHud(h.Clock);
                Check("Decided, the change counting down: who won and how, the second row counts down on its own bar, vdone, the winner lit, Next map; everybody frozen",
                    Header(w4d) == $"Started by Alice | {won} won (4 of 6) - Picked by the vote |  |  | Changing in | {ChangeDelay} s | " && w4d is { Open: false, Freeze: true, Kept: false } && w4d.DoneSeconds == ChangeDelay
                    && ClassOn(w4d, HudView.Screen, "vdone") && !ClassOn(w4d, HudView.Screen, "vlow") && ClassOn(w4d, "gg_m1", "now") && TextOf(w4d, "gg_m1_play_text") == "Next map" && TextOf(w4d, "gg_m2_play_text") == ""
                    && StepOf(w4d, HudVote.IdleBar) == "w10" && StepOf(w4d, HudVote.LimitBar) == "w0");
                Check("...chat and the board say it, once, with its count of everybody present; no top line over the window, no second line for the change",
                    M.Plain(decidedHud.Says[^1].Text) is var decidedLine && decidedLine.StartsWith("Everybody has voted: ") && decidedLine.EndsWith($"(4 of 6) - Changing in {ChangeDelay} s") && decidedHud.TopText == ""
                    && decidedHud.Lately.Count(l => l.Text == decidedLine) == 1 && decidedHud.Says.Count(l => M.Plain(l.Text).Contains("Changing in")) == 1 && decidedHud.Says[^1].Text.Contains(M.V($"{ChangeDelay} s")));
                h.Run(ChangeDelay / 2);
                var w5 = WinState();
                Check("...and its bar goes down with its seconds", VarOf(w5, HudVote.IdleNumVar) == $"{ChangeDelay - ChangeDelay / 2} s" && StepOf(w5, HudVote.IdleBar) == HudVote.Share(ChangeDelay - ChangeDelay / 2, ChangeDelay) && w5.Freeze);

                WinStart(5);
                var keptLabel = Clip(CurrentLabel(), 40);
                var keptMap = S.Map;
                StartVote(bob.Item1, bob.Item2);
                var keepN = OptOf(o => o.Source == VoteSource.Keep);
                var otherN = OptOf(o => o.Source == VoteSource.Rolled);
                foreach (var p in new[] { alice, bob, cara, dan }) Click(p, keepN);
                foreach (var p in new[] { eve, fay }) Click(p, otherN);
                h.Run(5);
                var w6 = WinState();
                var keptHud = BuildHud(h.Clock);
                Check($"Kept the map: for {MapVote.KeptSeconds} s the window says so and closes in that time, vdone, the card lit, everybody frozen; no change, no top line; chat and the board say it",
                    Header(w6) == $"Started by Bob | The vote kept {keptLabel} (4 of 6) |  |  | Closes in | {MapVote.KeptSeconds} s | " && w6 is { Open: false, Freeze: true, Kept: true, DoneSeconds: MapVote.KeptSeconds }
                    && ClassOn(w6, HudView.Screen, "vdone") && ClassOn(w6, "gg_m1", "now") && TextOf(w6, "gg_m1_score") == "4/6" && TextOf(w6, "gg_m1_play_text") == "Kept" && VarOf(w6, HudVote.NextVar) == "Kept" && StepOf(w6, HudVote.IdleBar) == "w10"
                    && Pending is null && S.Map == keptMap && keptHud.TopText == "" && M.Plain(keptHud.Says[^1].Text) == $"The vote kept {keptLabel} (4 of 6)" && keptHud.Lately.Any(l => l.Text == $"The vote kept {keptLabel} (4 of 6)")
                    && HudView.VoteLine(w6) == Hud.Clean($"The vote kept {keptLabel}", HudView.VoteLineMax));
                Check("...its end time rides the change-driven text", VoteState.Decode(BuildVoteState(h.Clock).Encode(endTimes: true)).AsOf(HudState.Ms(h.Clock.AddSeconds(3))) is { Kept: true, DoneSeconds: MapVote.KeptSeconds } kept3 && kept3.SecondsLeft == MapVote.KeptSeconds - 3);
                ClearNotices(); StartVote(cara.Item1, cara.Item2);
                Check("...a vote cannot be called meanwhile: the wait after a vote has begun", Ballot is { KeptUntil: not null } && h.Said("You can call a vote again in"));
                h.Run(MapVote.KeptSeconds - 1);
                Check("...still frozen a second before the end", WinState() is { Freeze: true, SecondsLeft: 1 } kept1 && VarOf(kept1, HudVote.IdleNumVar) == "1 s");
                h.Run(1);
                Check("...then everybody unfreezes and every window closes at the same moment: no vote, the wait before the next one", Ballot is null && WinState() is { Round: 0, Freeze: false, WaitFor: > 0 });
            }
            finally { MapVote.InactivitySeconds = 0; }

            // ---- every vote says in chat and on the board who voted and who still has to ----
            WinStart(0);
            StartVote(alice.Item1, alice.Item2);
            ClearNotices();
            var (lineA, lineB, lineKeep) = (Ballot!.Options.Where(o => o.Source == VoteSource.Rolled).ElementAt(0).N, Ballot.Options.Where(o => o.Source == VoteSource.Rolled).ElementAt(1).N, OptOf(o => o.Source == VoteSource.Keep));
            var (mapA, mapB) = (Clip(Label(Opt(lineA).Map!), 32), Clip(Label(Opt(lineB).Map!), 32));
            var lineKey = VoteLineKey(Ballot);
            string Last() => M.Plain(BuildHud(h.Clock).Says[^1].Text);
            Click(bob, lineA);
            var said1 = BuildHud(h.Clock);
            Check("A vote clicked on a card: one line in chat and on the board, who did it and on what, who has voted and who still has to, in chat's colours and plain on the board, no top line",
                said1.Says.Count == 1 && M.Plain(said1.Says[0].Text) == $"Bob voted for {mapA} - 1 of 6 voted: Bob - Waiting for: Alice, Cara, Dan, Eve, Fay"
                && said1.Says[0].Text.Contains(M.N("Bob")) && said1.Says[0].Text.Contains(M.N("Alice")) && said1.Says[0].Text.Contains(M.A(mapA)) && said1.Says[0].Text.Contains(M.V("1 of 6"))
                && said1.Lately.Single(l => l.Key == lineKey) is { Account: 0 } late1 && late1.Text == M.Plain(said1.Says[0].Text) && said1.TopText == "");
            Click(cara, lineA);
            var said2 = BuildHud(h.Clock);
            Check("...every vote again: a line each in chat, the board keeps the newest",
                said2.Says.Count == 2 && said2.Lately.Single(l => l.Key == lineKey).Text == $"Cara voted for {mapA} - 2 of 6 voted: Bob, Cara - Waiting for: Alice, Dan, Eve, Fay");
            Click(bob, lineB);
            Check("...a changed vote says so too", Last() == $"Bob voted for {mapB} - 2 of 6 voted: Bob, Cara - Waiting for: Alice, Dan, Eve, Fay");
            Click(bob, 0);
            Check("...a vote taken back too: he is waited for again", Last() == "Bob took their vote back - 1 of 6 voted: Cara - Waiting for: Alice, Bob, Dan, Eve, Fay");
            Click(cara, 0);
            Check("...and with nobody left: Nobody has voted", Last() == "Cara took their vote back - Nobody has voted - Waiting for: Alice, Bob, Cara, Dan, Eve, Fay");
            var saidBefore = BuildHud(h.Clock).Says.Count;
            Click(cara, 0); Click(bob, lineA); Click(bob, lineA);
            Check("...a click that changes nothing says nothing", BuildHud(h.Clock).Says.Count == saidBefore + 1);
            Click(dan, lineKeep);
            Check("...Keep and Random in their own words", Last().StartsWith($"Dan voted to keep {Clip(CurrentLabel(), 32)} - 2 of 6 voted: Bob, Dan - "));
            Click(eve, MapVote.RandomOption);
            Check("......", Last().StartsWith("Eve voted for Random - 3 of 6 voted: Bob, Dan, Eve - "));
            Click(alice, lineA); Click(cara, lineA); Click(fay, lineA);
            Check("...and the last one: Everybody has voted, with every name; then the result", M.Plain(BuildHud(h.Clock).Says[^2].Text) == $"Fay voted for {mapA} - Everybody has voted: Alice, Bob, Cara, Dan, Eve, Fay" && Last().StartsWith("Everybody has voted: "));
            foreach (var (names, cut) in new[] { ("Longer Player Name No ", false), ("P", true) })
            {
                Fresh();
                var sixteen = Enumerable.Range(1, 16).Select(i => ($"{names}{i:00}", $"[U:1:{3000 + i}]")).ToArray();
                foreach (var p in sixteen) h.Join(p);
                h.MatchEnd();
                var card = Ballot!.Options.First(o => o.Source == VoteSource.Rolled && o.Mode == Modes.ArmsRace).N;
                foreach (var p in sixteen.Take(8)) Click(p, card);
                Check($"Sixteen on, names of {names.Length + 2} characters: the line stays within {Messages.ChatLineMax} characters, both lists as far as they fit and +N or a number after them ({Last().Length}: {Last()})",
                    Last().Length <= Messages.ChatLineMax && Last().Contains("8 of 16 voted: ") && Last().Contains(" - Waiting for: ") && (cut ? Last().Contains("  +") : Last().Contains("8 players")));
            }

            // ---- a player who tries to call a vote when none may be called: their own line says why, chat and the board name them, for everybody ----
            Fresh();
            foreach (var p in six) h.Join(p);
            Ask(dan, m[7]); Land(); ClearNotices(); // a vote just held: the wait runs
            var leftNow = MapVote.Left((int)Math.Ceiling((VoteWaitUntil - h.Clock).TotalSeconds));
            Command(bob.Item1, bob.Item2, "!next");
            var no1 = BuildHud(h.Clock);
            var named = $"Bob tried to start another vote - The next one can start in {leftNow}";
            Check("You cannot call a vote yet: the player's own line says when, chat and the board name who tried, for everybody, in the refusal's colour; no top line",
                no1.Notes.Count == 1 && no1.Notes[2002].Text == $"You can call a vote again in {leftNow}" && no1.NoteCats[2002] == Messages.Vote.Id
                && no1.Says.Count == 1 && no1.Says[0].Account == 0 && M.Plain(no1.Says[0].Text) == named && no1.Says[0].Text.StartsWith(M.Refused("")) && no1.Says[0].Text.Contains(M.N("Bob")) && no1.Says[0].Text.Contains(M.V(leftNow))
                && no1.Lately.Count == 1 && no1.Lately[0].Account == 0 && no1.Lately[0].Text == named && no1.TopText == "" && Ballot is null);
            h.Run(1); Command(bob.Item1, bob.Item2, "!next");
            var no2 = BuildHud(h.Clock);
            Check($"...a player who keeps trying: their line and their row stay current, chat names them once in {RefusedQuietSeconds} s", no2.Says.Count == 1 && no2.Lately.Count == 1 && no2.Lately[0].Text != named && no2.Notes[2002].Text != $"You can call a vote again in {leftNow}");
            h.Run(RefusedQuietSeconds); Command(bob.Item1, bob.Item2, "!next");
            Check("...and again after that", BuildHud(h.Clock) is { Says.Count: 2, Lately.Count: 1 });
            Pick(cara, m[3]);
            Check("...a card's Play in the wait is such a try too: Cara is named beside Bob", BuildHud(h.Clock) is { Says.Count: 3, Lately.Count: 2 } no3 && M.Plain(no3.Says[^1].Text).StartsWith("Cara tried to start another vote - The next one can start in ") && no3.Notes.ContainsKey(2003));
            Fresh();
            foreach (var p in six) h.Join(p);
            Ask(dan, m[7]); ClearNotices(); // the change counts down
            Command(bob.Item1, bob.Item2, "!next");
            var no4 = BuildHud(h.Clock);
            var changing = Clip(Label(m[7]), 40);
            Check("...and while a change counts down: the same two lines, in their words", no4.Notes[2002].Text == $"Already changing to {changing} - Wait for it" && M.Plain(no4.Says[^1].Text) == $"Bob tried to start a vote - Already changing to {changing}" && Pending is { });

            // ---- the top line: one line, a newer one replaces it ----
            Fresh();
            Announce("A long one", 60); h.Run(1);
            Announce("A short one", 5);
            Check("The top line: a newer line replaces the one that is on", BuildHud(h.Clock) is { News: "A short one", TopText: "A short one", TopCat: "server" });
            h.Run(6);
            Check("...and nothing comes back when its time is up", BuildHud(h.Clock) is { News: "", TopText: "" });

            // ---- the transport (HudTransport.cs): countdowns as end times, so a state that only counts down does not change ----
            Fresh();
            foreach (var p in new[] { alice, bob }) h.Join(p);
            Announce("A line", 60); Tell(alice.Item2, "A note", 30);
            h.MatchEnd();
            Click(alice, Ballot!.Options.First(o => o.Source == VoteSource.Rolled).N); // a vote open, Bob still to vote: its clock runs
            string EndTimesText() => BuildHud(h.Clock).Encode(endTimes: true) + BuildVoteState(h.Clock).Encode(endTimes: true);
            string SecondsText() => BuildHud(h.Clock).Encode() + BuildVoteState(h.Clock).Encode();
            var (t1, l1) = (EndTimesText(), SecondsText());
            h.Run(1);
            var (t2, l2) = (EndTimesText(), SecondsText());
            Check("The change-driven text: a second of countdown changes nothing in it - its end times stand - while the seconds' text changes", t1 == t2 && l1 != l2);
            var asOf = HudState.Decode(t2)!.AsOf(HudState.Ms(h.Clock));
            var bySeconds = HudState.Decode(l2)!;
            Check("...counted down from its end times it says what the seconds' text says: the news, the note, the vote's clock",
                asOf.News == bySeconds.News && asOf.NewsFor == bySeconds.NewsFor && asOf.Notes[2001] == bySeconds.Notes[2001]
                && VoteState.Decode(t2).AsOf(HudState.Ms(h.Clock)).SecondsLeft == VoteState.Decode(l2).SecondsLeft && VoteState.Decode(t2).Round == Ballot!.Round);
            Check("...and a countdown that has run out is gone", HudState.Decode(t2)!.AsOf(HudState.Ms(h.Clock.AddSeconds(61))).News == ""
                && !HudState.Decode(t2)!.AsOf(HudState.Ms(h.Clock.AddSeconds(31))).Notes.ContainsKey(2001) && VoteState.Decode(t2).AsOf(HudState.Ms(h.Clock.AddSeconds(90))).SecondsLeft == 0);
            PublishHud(); var published = HudTextVersion; PublishHud();
            Check("A state published again unchanged keeps its version", HudTextVersion == published);
            Announce("Another line", 10); PublishHud();
            Check("...a change takes the next one; every answer starts with it and the bridge's clock, and a poll without a change gets a few bytes",
                HudTextVersion == published + 1 && StateAnswer().Split('\n')[1].StartsWith($"{Hud.VersionLine}\t{HudTextVersion}\t") && UnchangedAnswer().EndsWith($"{Hud.Unchanged}\n") && UnchangedAnswer().Length < 80
                && HudVersionAsked("?v=17&draw=1") == 17 && HudVersionAsked("?draw=1") == null && HudState.Decode(StateAnswer())?.News == "Another line");

            // ---- during the vote: picks go on it, a click changes or takes back a vote ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara, dan }) h.Join(p); // four on: the vote stays open while three of them vote
            Ask(dan, m[9]); Land(); // a map on, so Keep is on the ballot
            h.MatchEnd();
            var round = Ballot!.Round;
            ClearNotices(); StartVote(bob.Item1, bob.Item2);
            Check("Start a vote while one is open: one vote at a time - it says the vote is on", Pending is null && Ballot!.Round == round && Ballot.Open && h.Said("The vote for the next map is on"));
            ClearNotices();
            Check("A click on Start a vote (!next) the same, nothing changes", HudAct($"{bob.Item2}\t{bob.Item1}\t!next") && Pending is null && Ballot!.Open && Ballot.Round == round && h.Said("The vote for the next map is on"));
            ClearNotices(); HudAct($"{alice.Item2}\t{alice.Item1}\t!up");
            Check("...what does not touch the map goes on as ever", h.Said("You liked"));
            Click(alice, 2); Click(alice, 3);
            Check("A second click changes the vote", Ballot!.Choices[alice.Item2] == 3 && Ballot.Choices.Count == 1);
            Click(alice, 0);
            Check("...and 0 takes it back", Ballot!.Choices.Count == 0);
            ClearNotices(); Click(bob, 60);
            Check("A card the ballot does not have: a note, nothing cast", h.Said("not on this ballot") && Ballot!.Choices.Count == 0);
            ClearNotices(); Click(bob, 1, round - 1);
            Check("A click for another round: a note, nothing cast", h.Said("That vote is over") && Ballot!.Choices.Count == 0);
            Check("A click must be one: !mv without a round is refused at the door", !HudAct($"{bob.Item2}\tBob\t!mv 1") && Ballot!.Choices.Count == 0);
            var lastFill = Ballot!.Options.Where(o => o.Source == VoteSource.Rolled && o.Mode == Modes.ArmsRace).MaxBy(o => o.Order)!;
            var offBallot = m.Where(x => x != m[9] && Ballot.Options.All(o => o.Map != x)).ToList(); // maps of ours the dice did not put on it
            var (p6, p7) = (offBallot[0], offBallot[1]);
            ClearNotices(); Pick(alice, p6);
            Check("A pick while the vote is open goes on the ballot for everybody with her vote, in the place and number of a random card nobody voted for; nothing changes",
                Pending is null && Ballot!.Open && Ballot.Options.Count == MapVote.PageCards + 1 && Ballot.Options.Single(o => o.Map == p6) is { Source: VoteSource.Picked } added
                && added.N == lastFill.N && !Ballot.Options.Contains(lastFill) && Ballot.Choices[alice.Item2] == added.N && h.Said("Alice added"));
            var shownAdd = LiveVote();
            var addedN = Ballot!.Options.FirstOrDefault(o => o.Map == p6)?.N ?? -1;
            Check("...the live state shows it first (the most votes), with who picked it, her vote lit", shownAdd.Options[0] is { Source: VoteSource.Picked, Who: "Alice" } a1 && a1.N == addedN && shownAdd.ChoiceOf(2001) == addedN);
            ClearNotices();
            Check("A card's Play while the vote is open (the HUD's !map) adds the same way: a second pick of hers is another card, her vote moved to it - and her first, with no votes left, leaves the ballot",
                HudAct($"{alice.Item2}\t{alice.Item1}\t!map {ArgOf(p7)}") && Ballot!.Options.Count(o => o.Source == VoteSource.Picked) == 1 && Ballot.Choices[alice.Item2] == Ballot.Options.Single(o => o.Map == p7).N
                && Ballot.Options.All(o => o.Map != p6));
            var p7Card = LiveVote();
            var p7Slot = p7Card.Maps.FindIndex(o => o.Key == Key(p7)) + 1;
            Check("...a click on the card her vote is on takes it back (!mv <round> 0), and on another card moves it",
                HudVote.Click(p7Card, $"gg_m{p7Slot}_play", 0, 2001) == MapVote.Click(p7Card.Round, 0) && HudVote.Click(p7Card, $"gg_m{p7Slot}_play", 0, 2002) == MapVote.Click(p7Card.Round, p7Card.Maps[p7Slot - 1].N)
                && HudVote.Click(p7Card, HudVote.RandomButton, 0, 2001) == MapVote.Click(p7Card.Round, MapVote.RandomOption));
            var two0 = Opt(2).Map!;
            ClearNotices(); Pick(bob, two0);
            Check("...a map the ballot holds already gets the player's vote alone", Ballot!.Choices[bob.Item2] == 2 && h.Said("is on the vote already"));
            ClearNotices(); Pick(cara, m[9]);
            Check("...the map that is on is the Keep card: a pick of it is a vote to keep it", Ballot!.Choices[cara.Item2] == OptOf(o => o.Source == VoteSource.Keep) && h.Said("is on the vote - Your vote is on it"));
            Check("Three of four have voted: still open, waiting for Dan, everybody frozen", Ballot!.Open && LiveVote() is { Freeze: true } w4 && w4.Waiting.SequenceEqual(["Dan"]));
            h.Leave(bob); h.Run(1);
            Check("A voter who leaves takes their vote along, and the base shrinks to the 3 still on", !Ballot!.Choices.ContainsKey(bob.Item2) && Ballot.Choices.Count == 2 && VotePresent(Ballot, h.Clock) == 3);
            var sevenN = Ballot.Options.Single(o => o.Map == p7).N;
            Click(cara, sevenN);
            Check("...two of three on one card is no early end: Dan has not voted", Ballot!.Open);
            Click(dan, sevenN);
            Check("A picked map wins like any other once everybody has voted: 3 of 3", Ballot is { Open: false, Result.How: "all" } && Pending is { By: "the vote" } addWin && addWin.Map == p7);

            // ---- a risky pick asks that player twice, during a vote too ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) h.Join(p);
            Broken = new() { [Key(m[3])] = 3 }; // crashed 3 of 3 test loads: the dice skip it, a pick asks twice
            h.MatchEnd();
            var bobs = m.Skip(4).First(x => Ballot!.Options.All(o => o.Map != x));
            ClearNotices(); Pick(alice, m[3]);
            Check("A risky pick during a vote asks that player a second time first", h.Said($"Click again within {PickConfirmSeconds} s to vote anyway") && Ballot!.Options.All(o => o.Map != m[3]) && !Ballot.Choices.ContainsKey(alice.Item2));
            Pick(bob, bobs); Pick(alice, m[3]);
            Check("...another player's pick between leaves her confirm standing: her second click puts it on the ballot, with her vote",
                Ballot is { Open: true } rb && rb.Options.Single(o => o.Map == m[3]).N == rb.Choices[alice.Item2] && rb.Options.Any(o => o.Map == bobs));
            Broken = new();

            // ---- the timer: the most votes win at 60 s, not before ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara, dan, eve }) h.Join(p);
            h.MatchEnd();
            Click(alice, 2); Click(bob, 2); Click(cara, 4); Click(dan, 1);
            h.Run(MapVote.Seconds - 1);
            Check($"Not everybody (4 of 5): after {MapVote.Seconds - 1} s it is still open", Ballot is { Open: true } && Pending is null);
            var two = Opt(2).Map;
            h.Run(1);
            Check($"At {MapVote.Seconds} s it is decided: the most votes win", Ballot is { Open: false, Result: { N: 2, How: "most" } } && Pending?.Map == two && h.Said("(2 of 5)"));
            Land();
            Check("The winner is the vote's: picked by 'the vote', the vote over, and the next one waits", S.PickedBy == "the vote" && Ballot is null && LiveVote().WaitFor > 0);

            // ---- ties and no votes ----
            var wins = new Dictionary<int, int>();
            for (var trial = 0; trial < 120; trial++)
            {
                Fresh();
                foreach (var p in new[] { alice, bob, cara }) h.Join(p);
                h.MatchEnd();
                Click(alice, 1); Click(bob, 3); // 1 and 1 of 3: a tie at the timer
                h.Run(MapVote.Seconds);
                if (Ballot?.Result is { } r) wins[r.N] = wins.GetValueOrDefault(r.N) + 1;
            }
            Check($"A tie is drawn at random among the tied (120 ties: card 1 won {wins.GetValueOrDefault(1)}, card 3 {wins.GetValueOrDefault(3)}, the others {wins.Where(w => w.Key is not (1 or 3)).Sum(w => w.Value)})",
                wins.GetValueOrDefault(1) >= 35 && wins.GetValueOrDefault(3) >= 35 && wins.Keys.All(k => k is 1 or 3) && wins.Values.Sum() == 120);
            Check("...and says so", h.Said("A tie at 1 vote each, drawn by the dice"));
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) h.Join(p);
            h.MatchEnd();
            h.Run(MapVote.Seconds);
            Check("No votes at all at a match end: Random - the dice roll a map of the server's mode",
                Ballot is { Result: { How: "none" } nr } && nr.N == MapVote.RandomOption && Pending is { By: "the vote" } nv && InArmsRace(nv.Map) && h.Said("Nobody voted - Rolling the dice"));
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) h.Join(p);
            h.MatchEnd();
            Click(alice, MapVote.RandomOption); Click(bob, MapVote.RandomOption); Click(cara, 1);
            Check("Random can win like any card: 2 of 3 once everybody has voted, and the dice roll after the vote", Ballot is { Result: { How: "all" } rr } && rr.N == MapVote.RandomOption && Pending is { By: "the vote" } && h.Said("The vote chose Random"));

            // ---- a vote a player starts: Keep playing, the wait after every vote, !random, a match end overtaking it, Play this map again ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) h.Join(p);
            Ask(alice, m[4]); Land(); Settle();
            ClearNotices(); StartVote(bob.Item1, bob.Item2);
            Check("Start a vote while a match runs: Keep playing this map first, nobody's vote cast, everybody frozen", Ballot is { Open: true, MatchEnded: false } kv && kv.Starter == bob.Item2
                && kv.Options[0] is { Source: VoteSource.Keep, N: 1 } && LiveVote() is { Freeze: true, StarterName: "Bob" } ks && ks.Options[0].Label == "Keep playing this map" && Pending is null
                && h.Said("Bob started a vote for the next map"));
            Click(alice, 1); Click(cara, 1); Click(bob, 2);
            Check($"Keep wins once everybody has voted, 2 of 3: no change, the window says so for {MapVote.KeptSeconds} s, everybody still frozen",
                Ballot is { Open: false, KeptUntil: not null } && Pending is null && S.Map == m[4] && LiveVote() is { Freeze: true, Kept: true } && h.Said("The vote kept"));
            h.Run(MapVote.KeptSeconds);
            Check("...then the vote is gone and everybody unfreezes at the same moment", Ballot is null && Pending is null && !LiveVote().Freeze);
            ClearNotices(); StartVote(cara.Item1, cara.Item2);
            Check($"...and for {MapVote.WaitSeconds / 60} minutes nobody may start another - the wait after every vote", Ballot is null && h.Said("You can call a vote again in") && h.Said("Cara tried to start another vote - The next one can start in"));
            h.Run(MapVote.WaitSeconds + 1);
            ClearNotices(); StartVote(cara.Item1, cara.Item2);
            Check("...then anybody may", Ballot is { Open: true } kc && kc.Starter == cara.Item2);
            h.Run(MapVote.Seconds);
            Check("Nobody votes on a vote a player started: the map stays", Ballot is { KeptUntil: not null } && Pending is null && S.Map == m[4] && h.Said("Nobody voted -") && h.Said("stays"));
            h.Run(MapVote.WaitSeconds + 1);
            ClearNotices(); StartVote(bob.Item1, bob.Item2);
            var rolledN = OptOf(o => o.Source == VoteSource.Rolled && o.Mode == Modes.ArmsRace);
            var rolledMap = Opt(rolledN).Map!;
            Click(bob, rolledN); Click(cara, rolledN); Click(alice, 1);
            Check("Another card winning the vote changes the map", Pending is { By: "the vote" } rep && rep.Map == rolledMap && h.Said("Everybody has voted"));
            Land();
            Check("...landed: the vote's map", S.Map == rolledMap && S.PickedBy == "the vote");
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) h.Join(p);
            Ask(alice, m[4]); Land(); Settle();
            ClearNotices(); Command(bob.Item1, bob.Item2, "!next random");
            Check("!random starts the vote with its player's vote on Random", Ballot is { Open: true, MatchEnded: false } rv && rv.Choices[bob.Item2] == MapVote.RandomOption);
            Command(cara.Item1, cara.Item2, "!next random"); Command(alice.Item1, alice.Item2, "!next random");
            Check("...and while it is open puts that player's vote on Random: all three on it decide, and the dice roll", Ballot is { Open: false, Result.How: "all" } && Pending is { By: "the vote" } && h.Said("The vote chose Random"));
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) h.Join(p);
            Ask(alice, m[4]); Land(); Settle();
            StartVote(bob.Item1, bob.Item2);
            h.MatchEnd();
            Check("A match that ends during a vote a player started: it runs on as a match end's - its Keep card is Play this map again",
                Ballot is { Open: true, MatchEnded: true } && LiveVote() is { MatchEnded: true } me && me.Options.Single(o => o.Source == VoteSource.Keep).Label == "Play this map again");
            h.Run(MapVote.Seconds);
            Check("...and nobody voting is Random then, not Keep", Pending is { By: "the vote" } && h.Said("Nobody voted - Rolling the dice"));
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) h.Join(p);
            Ask(alice, m[4]); Land();
            h.MatchEnd();
            var again = OptOf(o => o.Source == VoteSource.Keep);
            Click(alice, again); Click(bob, again); Click(cara, again);
            Check("Play this map again wins at a match end: no change - the server's next match is on the same map (mp_match_end_restart)",
                Ballot is { KeptUntil: not null } && Pending is null && S.Map == m[4] && h.Said("again") && h.Said("Its next match"));

            // ---- the vote is dropped by a map start, the maintenance flag, everybody leaving, a restart ----
            Fresh();
            foreach (var p in new[] { alice, bob }) h.Join(p);
            h.MatchEnd();
            h.Line("Loading map \"de_somewhere\"");
            Check("A map start nobody asked for drops the vote without a change", Ballot is null && Pending is null);
            Fresh();
            foreach (var p in new[] { alice, bob }) h.Join(p);
            h.MatchEnd(); Click(alice, 1);
            File.WriteAllText(MaintenanceFile, "");
            h.Run(MapVote.Seconds);
            Check("The maintenance flag at the decision: no change", Ballot is null && Pending is null);
            File.Delete(MaintenanceFile); InMaintenance();
            Fresh();
            foreach (var p in new[] { alice, bob }) h.Join(p);
            h.MatchEnd();
            h.Leave(alice); h.Leave(bob); h.Run(1);
            Check("Everybody leaves during the vote: it is dropped, no change", Ballot is null && Pending is null);
            Fresh();
            foreach (var p in new[] { alice, bob }) h.Join(p);
            h.MatchEnd(); h.Line("rcon from \"192.0.2.9:5000\": command \"quit\"");
            Check("A restart drops it too", Ballot is null && Pending is null);

            // ---- more picks than a page: a random card makes room while one nobody voted for is left, then a second page ----
            Fresh();
            var nine = new[] { alice, bob, cara, dan, eve, ("Fay", "[U:1:2006]"), ("Gus", "[U:1:2007]"), ("Hal", "[U:1:2008]"), ("Ida", "[U:1:2009]") };
            foreach (var p in nine) h.Join(p);
            h.Join(("Joe", "[U:1:2010]")); // ten on: Joe does not vote, so the vote stays open
            Ask(nine[0], m[11]); Land();
            h.MatchEnd();
            var many = small.Skip(200).Where(x => x != m[11] && Ballot!.Options.All(o => o.Map != x)).Where((x, i) => i % 7 == 0).Take(9).ToList();
            for (var i = 0; i < 9; i++) Pick(nine[i], many[i]);
            var big9 = LiveVote();
            Console.WriteLine("ballot of nine picks: " + string.Join(" | ", big9.Options.Select(o => $"{o.N}. {o.Label} ({o.Source}, {o.Mode}, {o.Votes})")));
            Check($"Nine players each pick a map during the vote (10 on: no Wingman): every pick is on the ballot with its picker's vote; the {MapVote.PageCards - 2} random Arms Race cards made room, then a second page holds the rest - the picks first, then Keep and the Deathmatch card",
                big9.Open && big9.Maps.Count(o => o.Source == VoteSource.Picked) == 9 && many.All(x => big9.Maps.Any(o => o.Key == Key(x))) && big9.Maps.Count == 11
                && big9.Maps.Take(9).All(o => o.Source == VoteSource.Picked && o.Votes == 1) && big9.Maps.Skip(9).Select(o => o.Source).SequenceEqual([VoteSource.Keep, VoteSource.Rolled])
                && HudVote.Pages(big9) == 2 && HudVote.Cards(big9, 2001, null).Classes.Contains((HudView.Screen, "vmore", true))
                && HudVote.Cards(big9, 2001, null, page: 1).Classes.Contains((HudBrowser.PrevButton, "off", false)) && !HudVote.Cards(big9, 2001, null, page: 1).Classes.Contains((HudView.Screen, "vmore", true)));

            // ---- a Deathmatch map wins: it loads in Deathmatch ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) h.Join(p);
            S.RoundMinutes = ShortRoundMinutes; // a match length the players set, which is Arms Race's alone
            h.MatchEnd();
            var dmOpt = OptOf(o => o.Mode == Modes.Deathmatch);
            var dmMap = Opt(dmOpt).Map!;
            Click(alice, dmOpt); Click(bob, dmOpt); Click(cara, dmOpt);
            Check("All three vote for the Deathmatch map: decided", Pending?.Map == dmMap && h.Said("- Deathmatch"));
            h.Sent.Clear();
            h.Run(ChangeDelay);
            var prep = h.Sent.IndexOf("os_mode deathmatch;game_type 1;game_mode 2");
            Check($"The map loads in its mode: os_mode, game_type and game_mode first, then the map command ({string.Join(" | ", h.Sent)})",
                prep >= 0 && h.Sent.IndexOf($"changelevel {dmMap.Name}") == prep + 1 && (gameType, gameMode) == (1, 2) && S.Mode == "deathmatch" && CurrentMode == Modes.Deathmatch);
            Land();
            Check("Landed in Deathmatch: the plugin hears the mode again after the map start (os_mode: the plugin's own mode, which the HUD shows)",
                h.Sent.Contains("os_mode deathmatch") && CurrentMode == Modes.Deathmatch && S.Map == dmMap);
            Check("...and the match length is not applied outside Arms Race", !h.Sent.Any(c => c.StartsWith("mp_roundtime")));
            ClearNotices(); Command(alice.Item1, alice.Item2, "!time 60");
            Check("...nor can it be set there", h.Said("The match length is Arms Race's") && S.RoundMinutes == ShortRoundMinutes);
            Settle(); h.MatchEnd();
            Check($"The next vote in Deathmatch: Play this map again, {MapVote.PageCards - 3} Deathmatch maps, then Arms Race and Wingman, and Random in Deathmatch",
                Ballot!.Options.Select(o => o.Mode.Id).SequenceEqual(Enumerable.Repeat("deathmatch", MapVote.PageCards - 2).Concat(["armsrace", "wingman", "deathmatch"]))
                && Ballot.Options[0].Source == VoteSource.Keep && Ballot.Options.Skip(1).Take(MapVote.PageCards - 3).All(o => o.Map!.Official && o.Map != dmMap) && Ballot.FillMode == Modes.Deathmatch);
            var arOpt = OptOf(o => o.Mode == Modes.ArmsRace && o.Source == VoteSource.Rolled);
            Check("...and the ballot shows another mode's map with its mode after it", OptionLabel(Ballot, Opt(arOpt)).EndsWith(" - Arms Race") && !OptionLabel(Ballot, Opt(2)).Contains(" - "));
            var arMap = Opt(arOpt).Map!;
            Click(alice, arOpt); Click(bob, arOpt); Click(cara, arOpt);
            h.Sent.Clear(); h.Run(ChangeDelay);
            var back1 = h.Sent.IndexOf("os_mode armsrace;game_type 1;game_mode 0");
            Check("Back to Arms Race by the vote: os_mode armsrace;game_type 1;game_mode 0, then the Workshop map",
                back1 >= 0 && h.Sent.IndexOf($"host_workshop_map {arMap.Id}") == back1 + 1 && S.Mode == "armsrace");
            Land();
            Check("...where the match length the players set applies again", h.Sent.Contains($"mp_roundtime {ShortRoundMinutes}"));

            // ---- a Workshop map, then an official map of another mode: CS2's snapshot puts the old mode back, so it loads once more ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) h.Join(p);
            Ask(alice, m[2]); Land(); Settle(); // an Arms Race Workshop map: CS2 saved its convars (Arms Race) on the way there
            Check("The fake server went to a Workshop map and saved Arms Race", !onOfficial && snapshot == (1, 0));
            h.MatchEnd();
            var dmn = OptOf(o => o.Mode == Modes.Deathmatch);
            var dmo = Opt(dmn).Map!;
            Click(alice, dmn); Click(bob, dmn); Click(cara, dmn);
            h.Run(ChangeDelay);
            Check("The Deathmatch official map goes out with its mode, and CS2 restores Arms Race under it", h.Sent.Contains($"changelevel {dmo.Name}") && (gameType, gameMode) == (1, 0) && S.Mode == "deathmatch");
            h.Sent.Clear();
            Land();
            Check("After it lands the bridge reads the mode, finds Arms Race, and loads the map once more in Deathmatch",
                h.Sent.Contains("game_type") && h.Sent.Contains("os_mode deathmatch;game_type 1;game_mode 2") && h.Sent.IndexOf($"changelevel {dmo.Name}") == h.Sent.IndexOf("os_mode deathmatch;game_type 1;game_mode 2") + 1
                && AnnouncePending && h.Said($"{Label(dmo)} loads once more, in Deathmatch") && ModeReloads == 1);
            Check("...official to official, so this time nothing is restored: the server is in Deathmatch", (gameType, gameMode) == (1, 2));
            h.Sent.Clear(); h.Run(1); // an empty server loads an official map again within seconds (3 s, live on 2026-11-01)
            h.Line($"Loading map \"{dmo.Name}\""); h.Line("Started:  \"\"");
            h.Run(2);
            Check("The second load lands, the mode is right, and nothing loads a third time", !AnnouncePending && S.Map == dmo && h.Sent.Contains("game_type") && !h.Sent.Any(c => c.StartsWith("changelevel")));
            Check("...and it is still the vote's map", S.PickedBy == "the vote");

            // ---- the same with the plugin on, as on the live server: its block has the told values back in the convars before the bridge asks,
            // so only its reply to os_mode tells (seen on 2026-11-01: cs_office told Deathmatch stayed in Arms Race, with no second load) ----
            const string settingsFile = "../plugin/OverStrike2/GameSettings.cs";
            Check("The plugin's reply to os_mode has the words the bridge looks for when a map did not load in the told mode (GameSettings.SetMode)",
                !File.Exists(settingsFile) || File.ReadAllText(settingsFile).Contains($"kept; its block list and features {ModeNotLoadedWords}\"", StringComparison.Ordinal));
            Fresh();
            fakePlugin = true;
            foreach (var p in new[] { alice, bob, cara }) h.Join(p);
            Ask(alice, m[2]); Land(); Settle();
            Check("With the plugin: an Arms Race Workshop map says on already and loads once", ModeReloads == 0 && fakeLoaded == Modes.ArmsRace && h.Sent.Count(c => c.StartsWith("host_workshop_map")) == 1);
            h.MatchEnd();
            var dmpn = OptOf(o => o.Mode == Modes.Deathmatch);
            var dmp = Opt(dmpn).Map!;
            Click(alice, dmpn); Click(bob, dmpn); Click(cara, dmpn);
            h.Run(ChangeDelay);
            Check("With the plugin: CS2 restored Arms Race under the Deathmatch official map, and the convars read Deathmatch again", fakeLoaded == Modes.ArmsRace && (gameType, gameMode) == (1, 2) && S.Mode == "deathmatch");
            h.Sent.Clear();
            Land();
            Check($"...the plugin's reply alone has it load once more in Deathmatch ({string.Join(" | ", h.Sent)})",
                h.Sent.Contains("os_mode deathmatch") && h.Sent.IndexOf($"changelevel {dmp.Name}") == h.Sent.IndexOf("os_mode deathmatch;game_type 1;game_mode 2") + 1 && h.Sent.Contains($"changelevel {dmp.Name}")
                && AnnouncePending && h.Said($"{Label(dmp)} loads once more, in Deathmatch") && ModeReloads == 1 && S.Mode == "deathmatch");
            h.Sent.Clear(); h.Run(1);
            h.Line($"Loading map \"{dmp.Name}\""); h.Line("Started:  \"\"");
            h.Run(2);
            Check("...the second load is in Deathmatch: the plugin says on already, and nothing loads a third time",
                fakeLoaded == Modes.Deathmatch && !AnnouncePending && S.Map == dmp && h.Sent.Contains("os_mode deathmatch") && !h.Sent.Any(c => c.StartsWith("changelevel")) && ModeReloads == 1);
            foreach (var p in new[] { alice, bob, cara }) h.Join(p);
            Settle(); h.MatchEnd();
            var wgn = OptOf(o => o.Mode == Modes.Wingman);
            var wgm = Opt(wgn).Map!;
            Click(alice, wgn); Click(bob, wgn); Click(cara, wgn);
            h.Run(ChangeDelay);
            h.Sent.Clear();
            Land();
            Check("Official to official in another mode restores nothing: the plugin says on already, and it loads once",
                wgm.Official && fakeLoaded == Modes.Wingman && S.Mode == "wingman" && S.Map == wgm && h.Sent.Contains("os_mode wingman") && !h.Sent.Any(c => c.StartsWith("changelevel")) && ModeReloads == 0 && !AnnouncePending);
            Fresh();
            fakePlugin = true; fakeLoadsAs = Modes.Deathmatch;
            foreach (var p in new[] { alice, bob, cara }) h.Join(p);
            Ask(alice, m[2]); Land();
            Check("A Workshop map the plugin says came up in another mode is left as it is: one load, and the mode as told",
                h.Sent.Count(c => c.StartsWith("host_workshop_map")) == 1 && ModeReloads == 0 && S.Mode == "armsrace" && !AnnouncePending);

            // ---- the defaults: Arms Race after a restart, and after 10 minutes with nobody on ----
            Fresh();
            S.Mode = "deathmatch"; (gameType, gameMode) = (1, 2);
            foreach (var p in new[] { alice, bob }) h.Join(p);
            h.Line("rcon from \"192.0.2.9:5000\": command \"quit\"");
            Check("A server restart: back to Arms Race", S.Mode == "armsrace");
            Fresh();
            S.Mode = "wingman"; (gameType, gameMode) = (0, 2);
            h.Line("Loading map \"ar_shoots\""); h.Line("Started:  \"\"");
            Check("A start of the start map nobody asked for (a server start): Arms Race", S.Mode == "armsrace");
            Fresh();
            S.Mode = "deathmatch"; (gameType, gameMode) = (1, 2);
            foreach (var p in new[] { alice, bob }) h.Join(p);
            h.Leave(alice); h.Leave(bob);
            h.Run(ResetEmptyMinutes * 60 - 5);
            Check($"Nobody on for just under {ResetEmptyMinutes} minutes in Deathmatch: it stays", S.Mode == "deathmatch" && Pending is null);
            h.Run(70);
            Check($"...past {ResetEmptyMinutes} minutes: a random Arms Race map, announced, and loaded in Arms Race",
                h.Said("Back to Arms Race") && h.Sent.Contains("os_mode armsrace;game_type 1;game_mode 0") && h.Sent.Any(c => c.StartsWith("host_workshop_map"))
                && S.Mode == "armsrace" && (gameType, gameMode) == (1, 0) && S.Map is { } backOn && InArmsRace(backOn) && !backOn.Official);
            Fresh();
            foreach (var p in new[] { alice, bob }) h.Join(p);
            h.Leave(alice); h.Leave(bob);
            h.Run(ResetEmptyMinutes * 60 + 70);
            Check("An empty server in Arms Race already: no map change for the reset", Pending is null && !h.Sent.Any(c => c.StartsWith("os_mode")));

            // ---- a change that never lands gives the old mode back ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) h.Join(p);
            h.MatchEnd();
            var wOpt = OptOf(o => o.Mode == Modes.Wingman);
            Click(alice, wOpt); Click(bob, wOpt); Click(cara, wOpt);
            h.Run(ChangeDelay);
            Check("A Wingman change goes out", S.Mode == "wingman" && (gameType, gameMode) == (0, 2) && AnnouncePending);
            h.Sent.Clear(); h.Run(600);
            Check("...and never logs its map start: after the load timeout the server is told Arms Race again",
                S.Mode == "armsrace" && h.Sent.Contains("os_mode armsrace;game_type 1;game_mode 0") && (gameType, gameMode) == (1, 0));

            // ---- a bridge that starts asks the server which mode it is in ----
            Fresh();
            (gameType, gameMode) = (0, 2);
            ModeProbeEnabled = true; ModeProbing = true;
            h.Run(1);
            Check("A bridge that starts reads game_type and game_mode over RCON: the server is in Wingman", S.Mode == "wingman" && !ModeProbing);
            (gameType, gameMode) = (0, 0);
            ModeProbing = true; h.Run(11);
            Check("...convars that are none of our modes (Casual) leave the recorded mode, and the probe stops", S.Mode == "wingman" && !ModeProbing);
            ModeProbeEnabled = false;

            // ---- Wingman with five on: the ballot's own maps and Random come from Arms Race ----
            Fresh();
            S.Mode = "wingman";
            foreach (var p in new[] { alice, bob, cara, dan, eve }) h.Join(p);
            h.MatchEnd();
            Check($"In Wingman with five on: {MapVote.PageCards - 1} Arms Race maps, a Deathmatch map, Random in Arms Race - no Wingman",
                Ballot!.FillMode == Modes.ArmsRace && Ballot.Options.Select(o => o.Mode.Id).SequenceEqual(Enumerable.Repeat("armsrace", MapVote.PageCards - 1).Concat(["deathmatch", "armsrace"])));

            // ---- the per-mode lists: maps-<mode>.json beside maps.json ----
            Fresh();
            var dmFile = ModeMapsPath(Modes.Deathmatch);
            if (File.Exists(dmFile)) File.Delete(dmFile);
            Check($"No maps-deathmatch.json: Deathmatch has Valve's {Modes.Deathmatch.Official.Length} official maps", ReloadModeMaps(Modes.Deathmatch).Contains("not found") && PoolOf(Modes.Deathmatch).Count == 15
                && PoolOf(Modes.Deathmatch).All(x => x.Official && x.Mode == "deathmatch"));
            File.WriteAllText(dmFile, """
                [{"rank": 1, "id": 3070000001, "name": "dm_one", "sizeMb": 41}, {"rank": 2, "id": 3070000002, "name": " dm_two ", "sizeMb": 12},
                 {"rank": 3, "id": 3070000003, "name": "dm_three", "sizeMb": 0}, {"name": "de_extra", "official": true},
                 {"rank": 4, "id": 0, "name": "no id"}, {"rank": 5, "id": 3070000005, "name": ""}, {"name": "de_dust2", "official": true}]
                """);
            var read = ReloadModeMaps(Modes.Deathmatch);
            Console.WriteLine("reload: " + read);
            Check("maps-deathmatch.json in maps.json's shape: 3 Workshop maps, an official extra; entries without an id or a name skipped, an official map once",
                ModeWorkshop(Modes.Deathmatch).Count == 3 && PoolOf(Modes.Deathmatch).Count == 15 + 1 + 3 && PoolOf(Modes.Deathmatch).All(x => x.Mode == "deathmatch")
                && ModeWorkshop(Modes.Deathmatch).Any(x => x.Name == "dm_two") && read.Contains("3 Workshop maps"));
            var dmOne = ModeWorkshop(Modes.Deathmatch)[0];
            Check("Another mode's Workshop map: no list number in its label, its Workshop id as its argument, found by its id in the mode it belongs to",
                Label(dmOne) == "dm_one" && ArgOf(dmOne) == "3070000001" && Find("3070000001") is [{ Mode: "deathmatch", Name: "dm_one" }] && Detail(dmOne) == " (41 MB)");
            var unlistedHere = Find("3070000099") is [{ Mode: "" }];
            S.Mode = "wingman";
            var unlistedThere = Find("3070000099") is [{ Mode: "wingman" }];
            S.Mode = "armsrace";
            Check("An unlisted Workshop id loads in the mode the server is in (empty is Arms Race)", unlistedHere && unlistedThere);
            File.WriteAllText(dmFile, "[ not json");
            Check("An unreadable file is kept as it was", ReloadModeMaps(Modes.Deathmatch).Contains("unreadable") && ModeWorkshop(Modes.Deathmatch).Count == 3);
            File.WriteAllText(dmFile, """[{"rank": 1, "id": 3070000001, "name": "dm_one", "sizeMb": 41}]""");
            Check("A file with less than half the Workshop maps is kept as it was, unless forced",
                ReloadModeMaps(Modes.Deathmatch).Contains("less than half") && ModeWorkshop(Modes.Deathmatch).Count == 3 && ReloadModeMaps(Modes.Deathmatch, true).Contains("1 Workshop maps") && ModeWorkshop(Modes.Deathmatch).Count == 1);
            File.Delete(dmFile);

            Fresh();
            PicturesBridgeSelfTest(Check); // SelfTestPictures.cs: a Play in a mode, a name only another mode has

            // ---- the weights and the exclusions outside Arms Race ----
            Fresh();
            ModeMaps["deathmatch"] = [new(1, 3070000001, "dm_one", 41, false, "deathmatch"), new(2, 3070000002, "dm_two", 12, false, "deathmatch"), new(3, 3070000003, "dm_three", 9, false, "deathmatch")];
            var pool = PoolOf(Modes.Deathmatch);
            var total = pool.Sum(Weight);
            var rolls = Enumerable.Range(0, 30000).Select(_ => RandomPick(Modes.Deathmatch)!).GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
            var worst = pool.Max(x => Math.Abs(rolls.GetValueOrDefault(Key(x)) - 30000 * Weight(x) / total) / (30000 * Weight(x) / total));
            var official = Math.Pow(2, PopularityPoints * OfficialPopularity);
            var neutral = Math.Pow(2, PopularityPoints * NeutralPopularity);
            Check($"Deathmatch's dice: Valve's official maps count as {OfficialPopularity} popular, a Workshop map without ratings as {NeutralPopularity}; 30000 rolls land within 15% of every share (worst {worst:P1})",
                pool.Where(x => x.Official).All(x => Math.Abs(Weight(x) - official) < 1e-9) && pool.Where(x => !x.Official).All(x => Math.Abs(Weight(x) - neutral) < 1e-9) && worst < 0.15);
            Votes[Key(pool.First(x => x.Name == "dm_two"))] = new() { [alice.Item2] = 1, [bob.Item2] = 1 };
            Check("...and the votes count there as everywhere: two upvotes, four times the odds", Math.Abs(Weight(pool.First(x => x.Name == "dm_two")) - 4 * neutral) < 1e-9);
            S.Crashes["de_dust2"] = 1; Broken["de_mirage"] = 3; Votes["de_inferno"] = new() { [alice.Item2] = AdsVote };
            Blocked["3070000001"] = ("dm_one", "dm_one", "test"); S.Map = pool.First(x => x.Name == "de_nuke");
            var never = new[] { "de_dust2", "de_mirage", "de_inferno", "3070000001", "de_nuke" };
            Check("...and the exclusions too: never a crasher, a broken or blocked map, one marked Ads, nor the map that is on",
                Enumerable.Range(0, 4000).Select(_ => Key(RandomPick(Modes.Deathmatch)!)).All(k => !never.Contains(k)));

            // ---- the block lists, the server cfgs and their mounts ----
            const string blockDir = "../plugin/OverStrike2"; // the plugin's one block file, a value per mode (Modes.Blocked)
            var blockPath = Path.Combine(blockDir, Modes.BlockFile);
            if (File.Exists(blockPath))
            {
                var lists = Modes.All.ToDictionary(x => x.Id, x => Modes.Blocked(File.ReadAllText(blockPath), x, out var errors) is var l && errors.Count == 0 ? l : []);
                var names = lists.ToDictionary(kv => kv.Key, kv => kv.Value.Select(e => e.ConVar).Order().ToList());
                Check($"Block list {Modes.BlockFile}: {string.Join(", ", Modes.All.Select(x => $"{x.Id} {lists[x.Id].Count}"))} - each mode's values parse, the same game-changers in every mode",
                    lists.Values.All(l => l.Count == 30 && l.All(e => e.Why.Length >= 8)) && names.Values.All(n => n.SequenceEqual(names["armsrace"])));
                Check("...every mode's list keeps that mode's game_type and game_mode, by its own values", Modes.All.All(x => lists[x.Id].Single(e => e.ConVar == "game_type").Value == $"{x.GameType}"
                    && lists[x.Id].Single(e => e.ConVar == "game_mode").Value == $"{x.GameMode}"));
                Check("...Deathmatch and Wingman give a value for every entry (no boot baseline from Arms Race)", lists.Where(kv => kv.Key != "armsrace").All(kv => kv.Value.All(e => e.Value != null)));
                Check("...the modes differ where the modes do: free-for-all, buying, respawns",
                    lists["deathmatch"].Single(e => e.ConVar == "mp_teammates_are_enemies").Value == "1" && lists["wingman"].Single(e => e.ConVar == "mp_teammates_are_enemies").Value == "0"
                    && lists["deathmatch"].Single(e => e.ConVar == "mp_buy_anywhere").Value == "1" && lists["wingman"].Single(e => e.ConVar == "mp_buytime").Value == "20"
                    && lists["wingman"].Single(e => e.ConVar == "mp_respawn_on_death_t").Value == "0" && lists["deathmatch"].Single(e => e.ConVar == "mp_respawn_on_death_t").Value == "1");
                Check("...and none blocks a movement setting, friendly fire or the bridge's match length", lists.Values.SelectMany(l => l).All(e => !BlockList.Movement.Contains(e.ConVar)
                    && e.ConVar is not ("mp_friendlyfire" or "mp_roundtime") && !e.ConVar.StartsWith("ff_damage_")));
                Check("...and every mode keeps no bots, the match-end replay without a changelevel, and no F1/F2 votes", lists.Values.All(l => l.Single(e => e.ConVar == "bot_quota").Value is null or "0"
                    && l.Single(e => e.ConVar == "mp_match_end_changelevel").Value is null or "0" && l.Single(e => e.ConVar == "sv_allow_votes").Value is null or "0"));
            }
            else Console.WriteLine("skip block list checks: the plugin's files are not beside this bridge");
            const string cfgDir = "../server";
            if (Modes.All.All(x => File.Exists(Path.Combine(cfgDir, x.ServerCfg))))
            {
                var cfgs = Modes.All.ToDictionary(x => x.Id, x => ParseCfg(File.ReadAllText(Path.Combine(cfgDir, x.ServerCfg))));
                var delay = MapVote.Seconds + 8 + 5; // the vote, the deployed 8 s countdown (CHANGE_DELAY_SECONDS), a margin
                Check($"Server cfgs: every mode's keeps the match-end screen up through the vote (mp_match_restart_delay >= {delay}), replays the map, no changelevel, no CS2 map vote, no F1/F2 votes",
                    cfgs.Values.All(c => int.TryParse(c.GetValueOrDefault("mp_match_restart_delay"), out var d) && d >= delay && c.GetValueOrDefault("mp_match_end_restart") == "1"
                        && c.GetValueOrDefault("mp_match_end_changelevel") == "0" && c.GetValueOrDefault("mp_endmatch_votenextmap") == "0" && c.GetValueOrDefault("sv_allow_votes") == "0"));
                Check("...no bots in any mode (Valve fills Deathmatch with ten), and none filled in by the quota mode where Valve's would add one",
                    cfgs.Values.All(c => c.GetValueOrDefault("bot_quota") == "0") && cfgs["deathmatch"].GetValueOrDefault("bot_quota_mode") == "normal" && cfgs["wingman"].GetValueOrDefault("bot_quota_mode") == "normal");
                Check("...Wingman puts back what Arms Race or Deathmatch leave: two teams, grenades to buy, a pause between rounds",
                    cfgs["wingman"].GetValueOrDefault("mp_teammates_are_enemies") == "0" && cfgs["wingman"].GetValueOrDefault("mp_buy_allow_grenades") == "1"
                    && int.Parse(cfgs["wingman"].GetValueOrDefault("mp_round_restart_delay") ?? "0") > 0);
                if (File.Exists(blockPath))
                {
                    var agree = Modes.All.SelectMany(x => Modes.Blocked(File.ReadAllText(blockPath), x, out _).Where(e => e.Value != null && cfgs[x.Id].ContainsKey(e.ConVar))
                        .Select(e => (Mode: x.Id, e.ConVar, Block: e.Value!, Cfg: cfgs[x.Id][e.ConVar]))).ToList();
                    Check($"...and where a mode's cfg sets a game-changer, its block list keeps the same value ({agree.Count} pairs)", agree.Count > 0 && agree.All(a => Fun.Same(a.Block, a.Cfg)));
                }
            }
            else Console.WriteLine("skip server cfg checks: server is not two folders up");
            if (File.Exists(ComposeFile))
            {
                var yml = File.ReadAllText(ComposeFile);
                Check("the deployment's compose file mounts every mode's server cfg read-only into the game's cfg folder",
                    Modes.All.All(x => yml.Contains($"/{x.ServerCfg}:/home/steam/cs2-dedicated/game/csgo/cfg/{x.ServerCfg}:ro")));
            }
            Check("The rules' numbers for the vote are phrases the words may use", RuleNumbers().Contains($"{MapVote.Seconds} seconds") && RuleNumbers().Contains($"up to {Modes.Wingman.MaxPlayers} players")
                && RuleNumbers().Contains($"{MapVote.WaitSeconds / 60} minutes after a vote") && RuleNumbers().Contains($"takes {Modes.Wingman.MaxPlayers} players at most"));

        }
        finally { h.Dispose(); ModeMapsDir = null; ModeProbeEnabled = false; }
    }
}
