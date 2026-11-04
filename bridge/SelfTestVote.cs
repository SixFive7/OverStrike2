// --selftest, the end-of-match vote (#26) and the game modes (#27 c): VoteRound.cs, ModeSwitch.cs, MapVote.cs, Modes.cs. The vote runs on
// a handed clock with a fake timer wheel and a fake game server that remembers game_type and game_mode, like the queue's tests; every
// rule docs/reference.md states has a check here - the ballot's shape, the queue's places and votes, the maps added while it is open,
// the majority, the timer, ties, no votes, the vote a player starts (Keep playing, the wait after it), the vote's hold, the modes' map
// loads, the defaults after a restart and after 10 minutes with nobody on, the per-mode lists, weights, block lists and server cfgs.
using System.Text.Json;

static partial class Program
{
    static void VoteSelfTest(Action<string, bool> Check)
    {
        // ---- the shared parts: the click, the codec, the mode table ----
        Check("Vote click: !mv <round> <n> passes, anything else does not",
            MapVote.IsAction("!mv 1793000000 3") && MapVote.IsAction("!mv 5 0") && !MapVote.IsAction("!mv 3") && !MapVote.IsAction("!mv x 3")
            && !MapVote.IsAction("!mv 1 2 3") && !MapVote.IsAction("!mv 1 100") && !MapVote.IsAction("!mv  1 2") && !MapVote.IsAction("!map 1 2")
            && MapVote.ParseClick("!mv 17 4") is (17, 4) && MapVote.ParseClick("!mv 17") is null && MapVote.Click(17, 4) == "!mv 17 4");
        Check("Vote click: the menu's own actions stay what they were, and a vote click is not one of them", !Hud.IsAction("!mv 1 2") && Hud.IsAction("!up"));
        Check($"Early close (the owner's 40 c): only once every player present has voted ({string.Join(", ", Enumerable.Range(1, 6).Select(n => $"{MapVote.Needed(n)} of {n}"))}); alone, one vote",
            Enumerable.Range(1, 10).All(n => MapVote.Needed(n) == n) && MapVote.Needed(0) == 1 && Enumerable.Range(1, 10).All(n => MapVote.Majority(n) * 2 > n && (MapVote.Majority(n) - 1) * 2 <= n));
        var codec = new VoteState
        {
            Mode = "deathmatch", ModeName = "Deathmatch", Round = 1793000000, Open = false, SecondsLeft = 7, Present = 5, Needed = 3, Cast = 4,
            Modes = Modes.All.Select(m => new VoteState.ModeInfo(m.Id, m.Name, m.MaxPlayers, m.Suits(5))).ToList(),
            Options = [new(1, "armsrace", VoteSource.Queue, "12", "3070000012", "#12 aim\tx\nwith a very long name that goes on and on", "17 MB", 2, "Alice and Bob"),
                       new(2, "wingman", VoteSource.Rolled, "de_nuke", "de_nuke", "de_nuke (official)", "", 1, ""), new(3, "deathmatch", VoteSource.Random, "", "", "Random", "", 1, "")],
            Choices = new() { [2001] = 1, [2002] = 1, [2003] = 2, [2004] = 3 },
            Result = new VoteState.Outcome(1, "majority", "#12 aim x", "armsrace"),
        };
        var hudText = new HudState { Map = "#1 x" }.Encode() + codec.Encode();
        var back = VoteState.Decode(hudText);
        Check("Vote codec: what goes out comes back - the mode, the modes, the vote, its options (Random last), the choices and the result",
            back.Mode == "deathmatch" && back.ModeName == "Deathmatch" && back.Modes.Count == 3 && back.Modes.Single(m => m.Id == "wingman") is { MaxPlayers: 4, Offered: false }
            && back.Round == codec.Round && !back.Open && back.SecondsLeft == 7 && back.Present == 5 && back.Needed == 3 && back.Cast == 4
            && back.Options.Count == 3 && back.Options[0] is { N: 1, Mode: "armsrace", Source: VoteSource.Queue, Arg: "12", Key: "3070000012", Size: "17 MB", Votes: 2, Who: "Alice and Bob" }
            && back.Options[0].Label.Length <= 40 && !back.Options[0].Label.Contains('\t') && back.Options[2].Source == VoteSource.Random
            && back.Choices.Count == 4 && back.ChoiceOf(2003) == 2 && back.ChoiceOf(9) == 0 && back.Result is { N: 1, How: "majority", Mode: "armsrace" });
        Check("Vote codec: the HUD's own state reads the same text unharmed, and a text without vote lines is no vote",
            HudState.Decode(hudText)?.Map == "#1 x" && VoteState.Decode(new HudState().Encode()) is { Round: 0, Options.Count: 0, Mode: "" } && VoteState.Decode(null).Round == 0
            && VoteState.Decode("mvo\t1\tarmsrace\tqueue\t1\t1\tx\t\t1\t\n").Options.Count == 0);
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
            && Modes.Deathmatch.MapsFile == "maps-deathmatch.json" && Modes.ArmsRace.MapsFile == "maps.json" && Modes.Wingman.BlockFile == "blocked-wingman.txt" && Modes.ArmsRace.BlockFile == "blocked.txt");
        Check($"Modes: Valve's official pools - Deathmatch {Modes.Deathmatch.Official.Length} maps, Wingman {Modes.Wingman.Official.Length}, Arms Race {Modes.ArmsRace.Official.Length} - each map once, Arms Race's the bridge's OFFICIAL_MAPS",
            Modes.Deathmatch.Official.Length == 15 && Modes.Wingman.Official.Length == 7 && Modes.All.All(m => m.Official.Distinct().Count() == m.Official.Length)
            && Modes.ArmsRace.Official.Order().SequenceEqual(Officials.Select(o => o.Name).Order()));

        // ---- the harness: a clock that moves only when told, a timer wheel, a fake server ----
        var (realSend, realAfter, realNow, realStateDir, realHud, realCapable, realProbe) = (Send, After, Now, StateDir, HudActive, VoteCapable, ModeProbeEnabled);
        string dir;
        try { dir = Path.Combine(AppContext.BaseDirectory, $"selftest-vote-{Environment.ProcessId}"); Directory.CreateDirectory(dir); }
        catch (Exception) { dir = Path.Combine(Path.GetTempPath(), $"gungamebridge-vote-{Environment.ProcessId}"); Directory.CreateDirectory(dir); }
        StateDir = dir; ModeMapsDir = dir;
        var clock = new DateTime(2026, 11, 1, 20, 0, 0, DateTimeKind.Utc);
        Now = () => clock;
        var timers = new List<(DateTime Due, long Seq, Action Work)>();
        long seq = 0;
        After = (ms, work) => timers.Add((clock.AddMilliseconds(ms), seq++, work));
        var sent = new List<string>();
        int gameType = 1, gameMode = 0, statusHumans = 0;
        var onOfficial = true;            // the fake server's map: official (the boot map) or a Workshop map
        (int, int) snapshot = (1, 0);     // what CS2 saved when it last went from an official map to a Workshop map
        var fakePlugin = false;           // the plugin on the fake server too: it answers os_mode (Settings.SetMode), and its block has the told values back in the convars at once
        Modes.Mode? fakeTold = null, fakeLoaded = Modes.ArmsRace, fakeLoadsAs = null; // what os_mode said last; the mode the map on loaded in (the convars at its start); a load that ignores its convars
        Send = command =>
        {
            sent.Add(command);
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
        HudActive = () => false;
        VoteCapable = () => true;
        var loads = 0;
        try
        {
            void Run(double seconds)
            {
                var end = clock.AddSeconds(seconds);
                while (clock < end)
                {
                    var step = clock.AddSeconds(1) < end ? clock.AddSeconds(1) : end;
                    while (timers.Where(t => t.Due <= step).OrderBy(t => t.Due).ThenBy(t => t.Seq).FirstOrDefault() is { Work: not null } t)
                    {
                        timers.Remove(t);
                        if (t.Due > clock) clock = t.Due;
                        t.Work();
                    }
                    clock = step;
                    Tick();
                }
            }
            void Line(string ev) => Ingest($"{clock:MM/dd/yyyy - HH:mm:ss}.000 - {ev}", null);
            void Join((string Name, string Id) p) { Line($"\"{p.Name}<2><{p.Id}><>\" connected, address \"192.0.2.1:27005\""); Line($"\"{p.Name}<2><{p.Id}><>\" entered the game"); }
            void Leave((string Name, string Id) p, string reason = "NETWORK_DISCONNECT_DISCONNECT_BY_USER") => Line($"\"{p.Name}<2><{p.Id}><CT>\" disconnected (reason \"{reason}\")");
            void MatchEnd() => Line("Game Over: gungameprogressive mg_armsrace somemap score 0:0 after 9 min");
            void Land() // the countdown runs out, the server logs everyone out with reason SHUTDOWN, loads the map, and everyone reconnects
            {
                Run(ChangeDelay);
                var stay = Players.Where(kv => kv.Value.GoneSince == null).Select(kv => (kv.Value.Name, kv.Key)).ToList();
                foreach (var p in stay) Leave(p, "NETWORK_DISCONNECT_SHUTDOWN");
                Line($"Loading map \"{(S.Map is { Official: true } o ? o.Name : $"vote_bsp_{++loads}")}\""); Line("Started:  \"\"");
                foreach (var p in stay) Join(p);
                Run(2);
            }
            void Fresh()
            {
                S = new State(); Broken = new(); Packs = new(); Votes = new(); ClearPickConfirm();
                RemovedSeed = new(); Checked = new(); Removed = new(); Blocked = new();
                Players.Clear(); Queue.Clear(); Owners.Clear(); Airtime.Clear(); VClock = 0; AskSeq = 0; SkipVotes.Clear();
                Pending = null; AnnouncePending = false; CooldownUntil = default; AnnounceUntil = default; AnnounceDeadline = default; MatchEndUntil = default;
                StartRollOwed = false; StartMapAt = default; StartRollRetryAt = default; LastNamedMapStart = default; HoldUntil = default; EmptySince = null;
                NewsItems.Clear(); NewsSent = default; NewsFirst = default; NewsLast = default;
                ResetCrashWatch(); WatchStop(); ClearNotices(); RestartConfirmBy = ""; RestartConfirmUntil = default; RestartCooldownUntil = default; StartRollNeedsProof = false;
                Sizes = new(); SizeBounds = (0, 0, 0); NobodySince = null; ResetDone = false; ResetProbeAt = default; WhyOpen.Clear();
                Ballot = null; VoteHeld = false; ModeBeforeChange = null; ModeProbing = false; ModeProbeEnabled = false; ModeProbeAt = default; ModeResetRetryAt = default; ModeMaps.Clear();
                MidVoteWait.Clear();
                VoteCapable = () => true;
                timers.Clear(); sent.Clear(); (gameType, gameMode, statusHumans) = (1, 0, 0); (onOfficial, snapshot) = (true, (1, 0)); ModeVerifyDue = false; ModeReloads = 0;
                (fakePlugin, fakeTold, fakeLoaded, fakeLoadsAs) = (false, null, Modes.ArmsRace, null);
                if (File.Exists(MaintenanceFile)) File.Delete(MaintenanceFile);
                InMaintenance();
                clock = clock.AddHours(1);
            }
            bool Said(string fragment) => Told.Any(t => t.Contains(fragment));
            void Settle() => Run(Cooldown + 1); // past the change cooldown that a landed change opens
            void Click((string Name, string Id) p, int n, long? round = null) => HudAct($"{p.Id}\t{p.Name}\t{MapVote.Click(round ?? Ballot?.Round ?? 1, n)}");
            void Ask((string Name, string Id) p, MapInfo map) => Command(p.Name, p.Id, $"!map {ArgOf(map)}");
            BallotOption Opt(int n) => Ballot!.Options[n - 1];
            int OptOf(Func<BallotOption, bool> pick) => Ballot!.Options.First(pick).N;

            var (alice, bob, cara, dan, eve) = (("Alice", "[U:1:2001]"), ("Bob", "[U:1:2002]"), ("Cara", "[U:1:2003]"), ("Dan", "[U:1:2004]"), ("Eve", "[U:1:2005]"));
            var small = Maps.Where(m => m.SizeMb < 400 && m.Rank > 0).ToList();
            var m = Enumerable.Range(0, 12).Select(i => small[30 + i * 11]).ToList(); // twelve ordinary, distinct Arms Race maps

            // ---- no plugin that draws the vote: a match end works as it always did ----
            Fresh();
            VoteCapable = () => false;
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            MatchEnd();
            Check("No plugin that draws the vote is polling: no vote, the dice roll at the match end as before",
                Ballot is null && Pending is { Auto: true, Voted: false } && Said("Nobody picked a map, rolling the dice"));

            // ---- the ballot at a match end: at least MinMaps maps and Random - the server's mode, and a map of each other mode ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Ask(alice, m[0]); Land(); Settle(); // the map that is on: Alice's
            var eight = Maps.Where(IsNew).Skip(40).Take(8).ToList();
            foreach (var x in Maps.Where(IsNew).Except(eight)) S.Crashes[Key(x)] = 1; // every new map but eight has crashed: the dice can only take those eight
            var dmLeft = Modes.Deathmatch.Official.Take(2).ToHashSet();
            foreach (var name in Modes.Deathmatch.Official.Skip(2)) Broken[name] = 3; // and Deathmatch can only take two
            MatchEnd();
            Console.WriteLine("ballot: " + string.Join(" | ", Ballot?.Options.Select(o => $"{o.N}. {OptionLabel(o)} ({o.Source}, {o.Mode.Id})") ?? []));
            var fill = MapVote.MinMaps - 2; // the server's own mode fills what the other two modes leave
            Check($"A match end with a plugin that draws it opens the vote: {MapVote.MinMaps} maps and Random, numbered 1 to {MapVote.MinMaps + 1}, for {MapVote.Seconds} s, said on the news line",
                Ballot is { Open: true, MidMatch: false } b1 && b1.Options.Count == MapVote.MinMaps + 1 && b1.Options[^1] is { Source: VoteSource.Random, Map: null } && (b1.Ends - b1.Opened).TotalSeconds == MapVote.Seconds
                && b1.Options.Select(o => o.N).SequenceEqual(Enumerable.Range(1, MapVote.MinMaps + 1)) && Pending is null && Said($"Match over - vote for the next map: {MapVote.Seconds} s"));
            Check($"...the server's own mode first ({fill} Arms Race maps), then one Deathmatch and one Wingman map - with 3 on, Wingman suits; no Keep playing at a match end",
                Ballot!.Options.Take(MapVote.MinMaps).Select(o => o.Mode.Id).SequenceEqual(Enumerable.Repeat("armsrace", fill).Concat(["deathmatch", "wingman"]))
                && Ballot.Options.Take(MapVote.MinMaps).All(o => o.Source == VoteSource.Rolled) && Opt(MapVote.MinMaps + 1).Mode == Modes.ArmsRace && Ballot.Options.All(o => o.Source != VoteSource.Keep));
            Check("...each map once, never the map that is on, and only maps the dice may roll (no crasher, no broken map)",
                Ballot.Options.Take(MapVote.MinMaps).Select(o => Key(o.Map!)).Distinct().Count() == MapVote.MinMaps && Ballot.Options.All(o => o.Map == null || Key(o.Map) != Key(m[0]))
                && Ballot.Options.Take(fill).All(o => eight.Contains(o.Map!)) && dmLeft.Contains(Opt(fill + 1).Map!.Name) && Modes.Wingman.Official.Contains(Opt(fill + 2).Map!.Name)
                && Opt(fill + 1).Map!.Official && Opt(fill + 1).Map!.Mode == "deathmatch");
            Check("...and nobody's vote is cast yet: nobody queued anything", Ballot.Choices.Count == 0);
            var hud = VoteState.Decode(HudTextNow());
            Check("The live state carries it: the mode, the three modes, the vote (a match end's, nobody's), its options with their modes and how they got there",
                hud.Mode == "armsrace" && hud.Modes.Count == 3 && hud.Round == Ballot.Round && hud.Open && !hud.MidMatch && hud.Starter == 0 && hud.SecondsLeft == MapVote.Seconds && hud.Present == 3 && hud.Needed == 3
                && hud.Waiting.SequenceEqual(["Alice", "Bob", "Cara"]) && hud.Voters.Count == 0
                && hud.Options.Count == MapVote.MinMaps + 1 && hud.Options[fill].Mode == "deathmatch" && hud.Options[^1] is { Source: VoteSource.Random, Label: "Random", Key: "" }
                && hud.Options.Take(MapVote.MinMaps).All(o => o.Source == VoteSource.Rolled && o.Key != "" && o.Label.Length <= 40));

            // ---- fewer maps eligible than that: the ballot holds what the dice find, never a map twice ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            var three = Maps.Where(IsNew).Skip(40).Take(3).ToList();
            foreach (var x in Maps.Except(three)) S.Crashes[Key(x)] = 1; // three Arms Race maps left on either list
            MatchEnd();
            Check("Only three Arms Race maps may roll: the three, one Deathmatch, one Wingman map and Random - no repeat to make up the number",
                Ballot!.Options.Where(o => o.Mode == Modes.ArmsRace && o.Map != null).Select(o => o.Map!).ToHashSet().SetEquals(three) && Ballot.Options.Count(o => o.Map != null) == 5
                && Ballot.Options[^1].Source == VoteSource.Random);

            // ---- five on: Wingman (Valve's maxplayers 4) is not offered ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara, dan, eve }) Join(p);
            MatchEnd();
            Check($"Five on: no Wingman option - {MapVote.MinMaps - 1} Arms Race maps, one Deathmatch map, Random",
                Ballot!.Options.Select(o => o.Mode.Id).SequenceEqual(Enumerable.Repeat("armsrace", MapVote.MinMaps - 1).Concat(["deathmatch", "armsrace"]))
                && VoteState.Decode(HudTextNow()).Modes.Single(x => x.Id == "wingman").Offered == false);

            // ---- the queue: each requester's first request is listed, with their vote on it ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara, dan }) Join(p);
            Ask(dan, m[9]); Land(); // Dan's map is on, so the requests below queue
            Ask(alice, m[1]); Ask(alice, m[2]); Ask(bob, m[3]); Ask(cara, m[1]);
            Check("The queue before the match end: Alice and Cara's map first, then Bob's, then Alice's second", Order(clock).Select(r => r.Map).SequenceEqual([m[1], m[3], m[2]]));
            Settle(); MatchEnd();
            Console.WriteLine("ballot with a queue: " + string.Join(" | ", Ballot?.Options.Select(o => $"{o.N}. {OptionLabel(o)} ({o.Source}, {o.Mode.Id})") ?? []));
            Check("Every queued map comes first, in play order - Alice's second request too (the owner's 2026-11-03: all of them)",
                Opt(1) is { Source: VoteSource.Queue } q1 && q1.Map == m[1] && Opt(2) is { Source: VoteSource.Queue } q2 && q2.Map == m[3] && Opt(3) is { Source: VoteSource.Queue } q3 && q3.Map == m[2]);
            Check("...each requester's vote is already cast on their first: Alice and Cara for theirs, Bob for his; Alice's second has none, Dan's is his to give",
                Ballot!.Choices.Count == 3 && Ballot.Choices[alice.Item2] == 1 && Ballot.Choices[cara.Item2] == 1 && Ballot.Choices[bob.Item2] == 2 && !Ballot.Choices.ContainsKey(dan.Item2));
            Check($"...then the server's own mode up to {MapVote.MinMaps} maps with the mode mix, and Random: 3 queued, 3 Arms Race, Deathmatch, Wingman (4 on: Wingman suits)",
                Ballot.Options.Select(o => (o.Source, o.Mode.Id)).SequenceEqual([(VoteSource.Queue, "armsrace"), (VoteSource.Queue, "armsrace"), (VoteSource.Queue, "armsrace"),
                    (VoteSource.Rolled, "armsrace"), (VoteSource.Rolled, "armsrace"), (VoteSource.Rolled, "armsrace"), (VoteSource.Rolled, "deathmatch"), (VoteSource.Rolled, "wingman"), (VoteSource.Random, "armsrace")]));
            var shown = VoteState.Decode(HudTextNow());
            Check("...and the live state says who queued them and lights each player's vote", shown.Options[0].Who == "Alice and Cara" && shown.Options[1].Who == "Bob" && shown.Options[2].Who == "Alice"
                && shown.Options[0].Votes == 2 && shown.Options[2].Votes == 0 && shown.ChoiceOf(2001) == 1 && shown.ChoiceOf(2002) == 2 && shown.ChoiceOf(2004) == 0 && shown.Cast == 3 && shown.Needed == 4);
            Check("...who voted what, by name (the cards' chips), and who still has to vote (the waiting line): Dan",
                shown.Voters[1].SequenceEqual(["Alice", "Cara"]) && shown.Voters[2].SequenceEqual(["Bob"]) && !shown.Voters.ContainsKey(3) && shown.Waiting.SequenceEqual(["Dan"])
                && HudVote.WaitLine(shown) == "Waiting for: Dan");
            Click(dan, 1);
            Check("Once every one of them has voted the vote ends at once: Dan is the last, 3 of 4 for Alice and Cara's map (the owner's 40 c)",
                Ballot is { Open: false, Result: { N: 1, How: "all" } } && Pending is { Voted: true } won && won.Map == m[1] && Said("Everybody has voted") && Said("(3 of 4)"));
            Check("...which plays as their request: it leaves the queue and they hold it; Bob's and Alice's second stay queued for the next vote",
                Pending!.Owners.Select(o => o.Name).Order().SequenceEqual(["Alice", "Cara"]) && Queue.Select(r => r.Map.Rank).Order().SequenceEqual(new[] { m[2].Rank, m[3].Rank }.Order()));
            Ask(eve, m[8]); // a request while the change counts down: the vote's change is not replaced
            Check("A request during the vote's countdown queues: the vote's change is not replaced", Pending?.Map == m[1] && Queue.Any(r => r.Map == m[8]));
            Land();
            Check("Landed: Alice and Cara's map, held by them; the vote is gone from the live state", S.Map == m[1] && Holders(clock).Count == 2 && Ballot is null && VoteState.Decode(HudTextNow()).Round == 0);
            Settle(); MatchEnd();
            var bobsCard = OptOf(o => o.Map == m[3]);
            ClearNotices(); Command(bob.Item1, bob.Item2, $"!cancel {ArgOf(m[3])}"); Run(9); // the news line merges a burst: it says so within 8 s
            Check("Remove mine (the bridge's !cancel during the vote): Bob's request leaves the queue and his vote on its card goes with it; the card keeps its number",
                !Queue.Any(r => r.Map == m[3]) && !Ballot!.Choices.ContainsKey(bob.Item2) && Opt(bobsCard).Map == m[3] && Said("Bob cancelled"));
            Ask(bob, m[3]); // and back, for the checks below: a request made during a vote goes on the ballot, with his vote
            Check("...asking again during the vote puts his vote back on that card", Ballot!.Choices[bob.Item2] == bobsCard);
            Check("At the next match end the queue's votes are cast again: Bob's map with his vote, Alice's second with hers, Eve's with hers",
                Ballot!.Choices[bob.Item2] == OptOf(o => o.Map == m[3]) && Ballot.Choices[alice.Item2] == OptOf(o => o.Map == m[2]) && Ballot.Choices[eve.Item2] == OptOf(o => o.Map == m[8])
                && Ballot.Options.Take(3).All(o => o.Source == VoteSource.Queue));

            // ---- one player, one queued map: their own vote is the majority ----
            Fresh();
            Join(alice);
            Ask(alice, m[4]); Land(); Ask(alice, m[5]);
            Settle(); MatchEnd();
            Check($"Alone with a queued map: the queue's vote is everybody's, but the queue alone ends no vote in its first {MapVote.QueueVoteSeconds} s - the screen stays up to be seen",
                Ballot is { Open: true } && Ballot.Choices.Count == 1 && Pending is null);
            Run(MapVote.QueueVoteSeconds);
            Check("...then it ends, and the map is hers",
                Ballot is { Open: false, Result.How: "all" } && Pending is { } solo && solo.Map == m[5] && solo.Owners is [{ Name: "Alice" }]);
            Fresh();
            Join(alice);
            Ask(alice, m[4]); Land(); Ask(alice, m[5]);
            Settle(); MatchEnd();
            Click(alice, Ballot!.Choices[alice.Item2]);
            Check("...and her own click on the card the queue voted for ends it at once (her vote now, not the queue's)",
                Ballot is { Open: false, Result.How: "all" } && Pending is { } clicked && clicked.Map == m[5]);

            // ---- during the vote: only the vote picks a map (the owner's 26); a pick goes on the ballot (the owner's 2026-11-03); a click changes or takes back a vote ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara, dan }) Join(p); // four on: the vote stays open while three of them vote
            MatchEnd();
            var round = Ballot!.Round;
            var randomN = OptOf(o => o.Source == VoteSource.Random);
            ClearNotices(); NextRandom(bob.Item1, bob.Item2);
            Check("Next while the vote is open: one vote at a time - it says the vote is on and how to open it", Pending is null && Ballot!.Round == round && Ballot.Open && Said("The vote for the next map is on"));
            foreach (var asking in new[] { "!next", "!skip" })
            {
                ClearNotices();
                Check($"A click on {asking} while the vote is open: the same, nothing changes", HudAct($"{bob.Item2}\t{bob.Item1}\t{asking}") && Pending is null && Ballot!.Open && Ballot.Round == round && Said("The vote for the next map is on"));
            }
            foreach (var asking in new[] { "!previous", "!done" })
            {
                ClearNotices();
                Check($"A click on {asking} while the vote is open: what to do instead, nothing changes", HudAct($"{bob.Item2}\t{bob.Item1}\t{asking}") && Pending is null && Ballot!.Open && Queue.Count == 0 && Said(MapVote.WaitLine));
            }
            ClearNotices(); HudAct($"{alice.Item2}\t{alice.Item1}\t!cancel all"); HudAct($"{alice.Item2}\t{alice.Item1}\t!up");
            Check("...what does not touch the map goes on as ever", !Said(MapVote.WaitLine));
            Click(alice, 2); Click(alice, 3);
            Check("A second click changes the vote", Ballot!.Choices[alice.Item2] == 3 && Ballot.Choices.Count == 1);
            Click(alice, 0);
            Check("...and 0 takes it back", Ballot!.Choices.Count == 0);
            ClearNotices(); Click(bob, 60);
            Check("An option the ballot does not have: a note, nothing cast", Said("No option 60 on this ballot") && Ballot!.Choices.Count == 0);
            ClearNotices(); Click(bob, 1, round - 1);
            Check("A click for another round: a note, nothing cast", Said("That vote is over") && Ballot!.Choices.Count == 0);
            Check("A click must be one: !mv without a round is refused at the door", !HudAct($"{bob.Item2}\tBob\t!mv 1") && Ballot!.Choices.Count == 0);
            ClearNotices(); Ask(alice, m[6]);
            Check($"A request while the vote is open goes on the ballot for everybody: numbered after every other ({randomN + 1}), on screen before Random, with her vote on it; nothing queued, no change",
                Pending is null && Ballot!.Open && Queue.Count == 0 && Ballot.Options[^1].Source == VoteSource.Random && Ballot.Options[^2] is { Source: VoteSource.Added, AddedBy: "Alice" } added
                && added.Map == m[6] && added.N == randomN + 1 && Ballot.Choices[alice.Item2] == randomN + 1 && Said("Alice added") && Said($"to the vote - !vote {randomN + 1}"));
            var shownAdd = VoteState.Decode(HudTextNow());
            Check("...the live state shows it, with who added it and her vote lit", shownAdd.Options.Single(o => o.Source == VoteSource.Added) is { Who: "Alice" } a1 && a1.N == randomN + 1
                && shownAdd.ChoiceOf(2001) == randomN + 1 && shownAdd.Options[^1].Source == VoteSource.Random);
            ClearNotices(); Ask(alice, m[7]);
            Check($"...{MapVote.MaxAdded} map a player: a second one is refused, with what to do", Ballot!.Options.Count(o => o.Source == VoteSource.Added) == 1 && Said("You added a map to this vote already"));
            var two0 = Ballot!.Options.First(o => o.N == 2).Map!;
            ClearNotices(); Ask(bob, two0);
            Check("...a map the ballot holds already gets the player's vote alone", Ballot!.Choices[bob.Item2] == 2 && Ballot.Options.Count(o => o.Source == VoteSource.Added) == 1 && Said("is on the vote as 2 already"));
            ClearNotices();
            Check("A card's Play while the vote is open (the HUD's !map) adds the same way: Cara's map, with her vote",
                HudAct($"{cara.Item2}\t{cara.Item1}\t!map {ArgOf(m[7])}") && Ballot!.Open && Ballot.Options.Any(o => o.Source == VoteSource.Added && o.AddedBy == "Cara" && o.Map == m[7] && o.N == randomN + 2)
                && Ballot.Choices[cara.Item2] == randomN + 2 && Ballot.Options[^1].Source == VoteSource.Random);
            Check($"The numbers: up to {MapVote.MaxMaps} maps and Random, so a click's option has one or two digits", MapVote.MaxOptions == MapVote.MaxMaps + 1 && MapVote.IsAction($"!mv 1 {MapVote.MaxOptions}") && MapVote.MaxOptions < 100);
            Check("Three of four have voted: still open, waiting for Dan", Ballot!.Open && VoteState.Decode(HudTextNow()).Waiting.SequenceEqual(["Dan"]));
            Leave(bob);
            Run(1);
            Check("A voter who leaves takes their vote along, and the base shrinks to the 3 still on", !Ballot!.Choices.ContainsKey(bob.Item2) && Ballot.Choices.Count == 2 && VotePresent(Ballot, clock) == 3);
            Click(alice, randomN + 2);
            Check("...two of the three on Cara's map is no early end: Dan has not voted", Ballot!.Open);
            Click(dan, randomN + 2);
            Check("An added map wins like any other once everybody has voted: 3 of 3 for Cara's, held by the vote", Ballot is { Open: false, Result.How: "all" } && Pending is { Voted: true, Owners.Count: 0 } addWin && addWin.Map == m[7]);

            // ---- the timer: the most votes win at 60 s, not before ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara, dan, eve }) Join(p);
            MatchEnd();
            Click(alice, 2); Click(bob, 2); Click(cara, 4); Click(dan, 1);
            Run(MapVote.Seconds - 1);
            Check($"No majority (2, 1, 1 of 5): after {MapVote.Seconds - 1} s it is still open", Ballot is { Open: true } && Pending is null);
            var two = Opt(2).Map;
            Run(1);
            Check($"At {MapVote.Seconds} s it is decided: the most votes win", Ballot is { Open: false, Result: { N: 2, How: "most" } } && Pending?.Map == two && Said("(2 of 4 votes)"));
            Land(); Settle();
            Check("A rolled winner is the vote's map: held by the vote, picked by 'the vote'", VoteHeld && S.PickedBy == "the vote" && Holders(clock).Count == 0);
            var held = BuildHud(clock);
            Check("...the live state shows a holder (account 0); the board's button starts the vote for the next map, no skip count", held.Holders.SequenceEqual([0L]) && held.Skip is null
                && HudView.Action(held, 2001).Command == "!next" && !held.VoteOpen);
            Ask(bob, m[7]);
            Check("A request on the vote's map queues instead of replacing it", Pending is null && Queue.Any(r => r.Map == m[7]));
            ClearNotices(); NextRandom(cara.Item1, cara.Item2);
            Check("Next on the vote's map starts the vote for the next map at once: Keep playing first, then the queue (Bob's, with his vote), the server's mode, Random last",
                Ballot is { Open: true, MidMatch: true } mid && mid.Starter == cara.Item2 && mid.Options[0] is { Source: VoteSource.Keep, N: 1 } keep && keep.Map == S.Map
                && mid.Options[1] is { Source: VoteSource.Queue } bq && bq.Map == m[7] && mid.Choices[bob.Item2] == 2 && mid.Options.Count(o => o.Map != null) == MapVote.MinMaps
                && mid.Options[^1].Source == VoteSource.Random && Said("Cara started a vote for the next map") && Said("Keep playing is on it"));
            var midHud = VoteState.Decode(HudTextNow());
            Check("...the live state says it is a vote a player started, and whose (the plugin opens that player's window alone), with the Keep card's words",
                midHud is { MidMatch: true, Starter: 2003, Open: true } && midHud.Options[0] is { Source: VoteSource.Keep } kk && kk.Label.StartsWith("Keep playing") && BuildHud(clock).VoteOpen);
            ClearNotices(); Command(dan.Item1, dan.Item2, "!skip");
            Check("...!skip during it says the vote is on: one vote at a time", Ballot!.Round == midHud.Round && Said("The vote for the next map is on"));
            Click(dan, 2); Click(eve, 2);
            Check("...three of five is no early end any more: Alice and Cara have not voted", Ballot!.Open && VoteState.Decode(HudTextNow()).Waiting.SequenceEqual(["Alice", "Cara"]));
            Click(alice, 1); Click(cara, 2);
            Check("Everybody has voted, 4 of 5 for Bob's queued map: it replaces the vote's map and plays as his request", Ballot is { Open: false, Result.How: "all" } && Pending?.Map == m[7] && Pending.Owners is [{ Name: "Bob" }]);
            Land(); Settle(); MatchEnd();
            Check("A match end ends the vote's hold like a request's, and opens the next vote", !VoteHeld && Ballot is { Open: true });
            Run(MapVote.Seconds); Land(); Settle();
            Check("(the next vote's map, held by the vote)", VoteHeld);
            foreach (var p in new[] { alice, bob, cara, dan, eve }) Leave(p);
            Run(2);
            Check("Nobody on: the vote's map is held by nobody, so the first request after it plays at once", !VoteHeld);
            Join(alice); Ask(alice, m[10]);
            Check("...as it does", Pending is { } back0 && back0.Map == m[10] && back0.Owners is [{ Name: "Alice" }]);

            // ---- ties and no votes ----
            var wins = new Dictionary<int, int>();
            for (var trial = 0; trial < 120; trial++)
            {
                Fresh();
                foreach (var p in new[] { alice, bob, cara }) Join(p);
                MatchEnd();
                Click(alice, 1); Click(bob, 3); // 1 and 1 of 3: no majority, a tie at the timer
                Run(MapVote.Seconds);
                if (Ballot?.Result is { } r) wins[r.N] = wins.GetValueOrDefault(r.N) + 1;
            }
            Check($"A tie is drawn at random among the tied (120 ties: option 1 won {wins.GetValueOrDefault(1)}, option 3 {wins.GetValueOrDefault(3)}, the others {wins.Where(w => w.Key is not (1 or 3)).Sum(w => w.Value)})",
                wins.GetValueOrDefault(1) >= 35 && wins.GetValueOrDefault(3) >= 35 && wins.Keys.All(k => k is 1 or 3) && wins.Values.Sum() == 120);
            Check("...and says so", Said("A tie at 1 vote each, drawn by the dice"));
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            MatchEnd();
            Run(MapVote.Seconds);
            Check("No votes at all at a match end: Random - the dice roll a map of the server's mode, held by the vote",
                Ballot is { Result: { How: "none" } nr } && nr.N == MapVote.MinMaps + 1 && Pending is { Voted: true, Owners.Count: 0 } nv && InArmsRace(nv.Map) && Said("Nobody voted - rolling the dice"));
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            MatchEnd();
            var rn = OptOf(o => o.Source == VoteSource.Random);
            Click(alice, rn); Click(bob, rn); Click(cara, 1);
            Check("Random can win like any option: 2 of 3 once everybody has voted, and the dice roll after the vote", Ballot is { Result: { How: "all" } rr } && rr.N == rn && Pending is { Voted: true } && Said("The vote chose Random"));

            // ---- the vote a player starts (Next, the owner's 2026-11-03): Keep playing, and the wait after it ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Ask(alice, m[4]); Land(); Settle(); // Alice's request is on
            ClearNotices(); NextRandom(bob.Item1, bob.Item2);
            Check("Next on somebody's request starts the vote too: Keep playing first, the request stays unless the vote decides", Ballot is { Open: true, MidMatch: true } kv && kv.Starter == bob.Item2
                && kv.Options[0].Source == VoteSource.Keep && Holders(clock) is [{ Name: "Alice" }] && Pending is null);
            Click(alice, 1); Click(cara, 1); Click(bob, 2);
            Check("Keep wins once everybody has voted, 2 of 3: no change, Alice still holds her map, the vote is gone, and the news line says so",
                Ballot is null && Pending is null && S.Map == m[4] && Holders(clock) is [{ Name: "Alice" }] && Said("The vote kept"));
            ClearNotices(); NextRandom(bob.Item1, bob.Item2);
            Check($"Bob, whose vote kept the map, waits {MidVoteCooldownMinutes} minutes before starting another - and hears how long is left",
                Ballot is null && Said($"Your last vote kept the map - you can start another in {MidVoteCooldownMinutes} min"));
            ClearNotices(); NextRandom(cara.Item1, cara.Item2);
            Check("...anybody else may start one at once", Ballot is { Open: true } kc && kc.Starter == cara.Item2);
            Run(MapVote.Seconds);
            Check("Nobody votes on a vote a player started: the map stays, Alice's request with it", Ballot is null && Pending is null && S.Map == m[4] && Holders(clock) is [{ Name: "Alice" }]
                && Said("Nobody voted -") && Said("stays"));
            Run(MidVoteCooldownMinutes * 60 - MapVote.Seconds + 1);
            ClearNotices(); NextRandom(bob.Item1, bob.Item2);
            Check($"...after {MidVoteCooldownMinutes} minutes Bob may start one again", Ballot is { Open: true } kb && kb.Starter == bob.Item2);
            var rolledN = OptOf(o => o.Source == VoteSource.Rolled && o.Mode == Modes.ArmsRace);
            var rolledMap = Opt(rolledN).Map!;
            Click(bob, rolledN); Click(cara, rolledN); Click(alice, 1);
            Check("Another map winning the vote replaces somebody's request - only the vote can (the old skip vote)", Pending is { Voted: true, Owners.Count: 0 } rep && rep.Map == rolledMap && Said("Everybody has voted"));
            Land();
            Check("...landed: the vote's map, held by the vote; Alice's request is over", S.Map == rolledMap && VoteHeld && Holders(clock).Count == 0);
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Ask(alice, m[4]); Land(); Settle();
            ClearNotices(); Command(bob.Item1, bob.Item2, "!next random");
            Check("!random starts the vote with its player's vote on Random", Ballot is { Open: true, MidMatch: true } rv && rv.Choices[bob.Item2] == rv.Options.First(o => o.Source == VoteSource.Random).N);
            Command(cara.Item1, cara.Item2, "!next random"); Command(alice.Item1, alice.Item2, "!next random");
            Check("...and while it is open puts that player's vote on Random: all three on it decide, and the dice roll", Ballot is { Open: false, Result.How: "all" } && Pending is { Voted: true } && Said("The vote chose Random"));
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Ask(alice, m[4]); Land(); Settle();
            ClearNotices(); NextRandom(alice.Item1, alice.Item2);
            Check("The holder's own Next is I'm done: nothing queued, so the vote opens - a match end's kind, no Keep playing", Ballot is { Open: true, MidMatch: false } dn
                && dn.Options.All(o => o.Source != VoteSource.Keep) && Holders(clock).Count == 0);
            Run(MapVote.Seconds);
            Check("...where nobody voting is Random", Pending is { Voted: true } && Said("Nobody voted - rolling the dice"));
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Ask(alice, m[4]); Land(); Settle();
            NextRandom(bob.Item1, bob.Item2);
            MatchEnd();
            Check("A match that ends during a vote a player started: it runs on as a match end's - everybody's window (no longer mid-match on the live state), Alice's turn over",
                Ballot is { Open: true, MatchEnded: true } && Holders(clock).Count == 0 && VoteState.Decode(HudTextNow()) is { MidMatch: false, Starter: 0 });
            Run(MapVote.Seconds);
            Check("...and nobody voting is Random then, not Keep", Pending is { Voted: true } && Said("Nobody voted - rolling the dice"));
            Fresh();
            VoteCapable = () => false;
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            ClearNotices(); NextRandom(bob.Item1, bob.Item2);
            Check("Without a plugin that draws the vote, Next rolls the dice as before", Ballot is null && Pending is { } old && old.By == "Bob" && Said("Bob rolled the dice"));

            // ---- the vote is dropped by a map start, the maintenance flag, everybody leaving ----
            Fresh();
            foreach (var p in new[] { alice, bob }) Join(p);
            MatchEnd();
            Line("Loading map \"de_somewhere\"");
            Check("A map start nobody asked for drops the vote without a change", Ballot is null && Pending is null);
            Fresh();
            foreach (var p in new[] { alice, bob }) Join(p);
            MatchEnd(); Click(alice, 1);
            File.WriteAllText(MaintenanceFile, "");
            Run(MapVote.Seconds);
            Check("The maintenance flag at the decision: no change", Ballot is null && Pending is null);
            File.Delete(MaintenanceFile); InMaintenance();
            Fresh();
            foreach (var p in new[] { alice, bob }) Join(p);
            MatchEnd();
            Leave(alice); Leave(bob); Run(1);
            Check("Everybody leaves during the vote: it is dropped, no change", Ballot is null && Pending is null);
            Fresh();
            foreach (var p in new[] { alice, bob }) Join(p);
            MatchEnd(); Line("rcon from \"192.0.2.9:5000\": command \"quit\"");
            Check("A restart drops it too", Ballot is null && Pending is null);

            // ---- more requests than the ballot's eight: every one is on it, and the mode mix still is ----
            Fresh();
            var six = new[] { alice, bob, cara, dan, eve, ("Fay", "[U:1:2006]") };
            foreach (var p in six) Join(p);
            var twelve = Enumerable.Range(0, 12).Select(i => small[200 + i * 7]).ToList();
            Ask(six[5], m[11]); Land();
            for (var i = 0; i < 12; i++) Ask(six[i % 6], twelve[i]);
            Settle(); MatchEnd();
            Console.WriteLine("ballot of twelve requests: " + string.Join(" | ", Ballot?.Options.Select(o => $"{o.N}. {OptionLabel(o)} ({o.Source}, {o.Mode.Id})") ?? []));
            Check("Twelve requests from six players: all twelve on the ballot in play order, each requester's vote on their first; the requests hold no Deathmatch map, so one is added (6 on: no Wingman); no Arms Race fill; Random last",
                Ballot!.Options.Count(o => o.Source == VoteSource.Queue) == 12 && Ballot.Options.Where(o => o.Source == VoteSource.Queue).Select(o => o.Map!).ToHashSet().SetEquals(twelve)
                && Ballot.Options.Single(o => o.Source == VoteSource.Rolled).Mode == Modes.Deathmatch && Ballot.Options[^1].Source == VoteSource.Random && Ballot.Options.Count == 14
                && Ballot.Choices.Count == 6 && Ballot.Choices.Values.All(n => n <= 12));

            // ---- a Deathmatch map wins: it loads in Deathmatch ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            S.RoundMinutes = ShortRoundMinutes; // a match length the players set, which is Arms Race's alone
            MatchEnd();
            var dmOpt = OptOf(o => o.Mode == Modes.Deathmatch);
            var dmMap = Opt(dmOpt).Map!;
            Click(alice, dmOpt); Click(bob, dmOpt); Click(cara, dmOpt);
            Check("All three vote for the Deathmatch map: decided", Pending?.Map == dmMap && Said("- Deathmatch"));
            sent.Clear();
            Run(ChangeDelay);
            var prep = sent.IndexOf("os_mode deathmatch;game_type 1;game_mode 2");
            Check($"The map loads in its mode: os_mode, game_type and game_mode first, then the map command ({string.Join(" | ", sent)})",
                prep >= 0 && sent.IndexOf($"changelevel {dmMap.Name}") == prep + 1 && (gameType, gameMode) == (1, 2) && S.Mode == "deathmatch" && CurrentMode == Modes.Deathmatch);
            Land();
            Check("Landed in Deathmatch: the plugin hears the mode again after the map start, the live state says Deathmatch",
                sent.Contains("os_mode deathmatch") && VoteState.Decode(HudTextNow()) is { Mode: "deathmatch", ModeName: "Deathmatch" } && S.Map == dmMap);
            Check("...and the match length is not applied outside Arms Race", !sent.Any(c => c.StartsWith("mp_roundtime")));
            ClearNotices(); Command(alice.Item1, alice.Item2, "!time 60");
            Check("...nor can it be set there", Said("The match length is Arms Race's") && S.RoundMinutes == ShortRoundMinutes);
            Settle(); MatchEnd();
            Check($"The next vote in Deathmatch: {MapVote.MinMaps - 2} Deathmatch maps, then Arms Race and Wingman, and Random in Deathmatch",
                Ballot!.Options.Select(o => o.Mode.Id).SequenceEqual(Enumerable.Repeat("deathmatch", MapVote.MinMaps - 2).Concat(["armsrace", "wingman", "deathmatch"]))
                && Ballot.Options.Take(MapVote.MinMaps - 2).All(o => o.Map!.Official && o.Map != dmMap) && Ballot.FillMode == Modes.Deathmatch);
            var arOpt = MapVote.MinMaps - 1;
            Check("...and the ballot shows another mode's map with its mode after it", OptionLabel(Opt(arOpt)).EndsWith(" - Arms Race") && !OptionLabel(Opt(1)).Contains(" - "));
            var arMap = Opt(arOpt).Map!;
            Click(alice, arOpt); Click(bob, arOpt); Click(cara, arOpt);
            sent.Clear(); Run(ChangeDelay);
            var back1 = sent.IndexOf("os_mode armsrace;game_type 1;game_mode 0");
            Check("Back to Arms Race by the vote: os_mode armsrace;game_type 1;game_mode 0, then the Workshop map",
                back1 >= 0 && sent.IndexOf($"host_workshop_map {arMap.Id}") == back1 + 1 && S.Mode == "armsrace");
            Land();
            Check("...where the match length the players set applies again", sent.Contains($"mp_roundtime {ShortRoundMinutes}"));

            // ---- a Workshop map, then an official map of another mode: CS2's snapshot puts the old mode back, so it loads once more ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Ask(alice, m[2]); Land(); Settle(); // an Arms Race Workshop map: CS2 saved its convars (Arms Race) on the way there
            Check("The fake server went to a Workshop map and saved Arms Race", !onOfficial && snapshot == (1, 0));
            MatchEnd();
            var dmn = OptOf(o => o.Mode == Modes.Deathmatch);
            var dmo = Opt(dmn).Map!;
            Click(alice, dmn); Click(bob, dmn); Click(cara, dmn);
            Run(ChangeDelay);
            Check("The Deathmatch official map goes out with its mode, and CS2 restores Arms Race under it", sent.Contains($"changelevel {dmo.Name}") && (gameType, gameMode) == (1, 0) && S.Mode == "deathmatch");
            sent.Clear();
            Land();
            Check("After it lands the bridge reads the mode, finds Arms Race, and loads the map once more in Deathmatch",
                sent.Contains("game_type") && sent.Contains("os_mode deathmatch;game_type 1;game_mode 2") && sent.IndexOf($"changelevel {dmo.Name}") == sent.IndexOf("os_mode deathmatch;game_type 1;game_mode 2") + 1
                && AnnouncePending && Said($"{Label(dmo)} loads once more, in Deathmatch") && ModeReloads == 1);
            Check("...official to official, so this time nothing is restored: the server is in Deathmatch", (gameType, gameMode) == (1, 2));
            sent.Clear(); Run(1); // an empty server loads an official map again within seconds (3 s, live on 2026-11-01)
            Line($"Loading map \"{dmo.Name}\""); Line("Started:  \"\"");
            Run(2);
            Check("The second load lands, the mode is right, and nothing loads a third time", !AnnouncePending && S.Map == dmo && sent.Contains("game_type") && !sent.Any(c => c.StartsWith("changelevel")));
            Check("...and it is still the vote's map, held by the vote", VoteHeld && S.PickedBy == "the vote");

            // ---- the same with the plugin on, as on the live server: its block has the told values back in the convars before the bridge asks,
            // so only its reply to os_mode tells (seen on 2026-11-01: cs_office told Deathmatch stayed in Arms Race, with no second load) ----
            const string settingsFile = "../plugin/OverStrike2/Settings.cs";
            Check("The plugin's reply to os_mode has the words the bridge looks for when a map did not load in the told mode (Settings.SetMode)",
                !File.Exists(settingsFile) || File.ReadAllText(settingsFile).Contains($"kept; its block list and features {ModeNotLoadedWords}\"", StringComparison.Ordinal));
            Fresh();
            fakePlugin = true;
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Ask(alice, m[2]); Land(); Settle();
            Check("With the plugin: an Arms Race Workshop map says on already and loads once", ModeReloads == 0 && fakeLoaded == Modes.ArmsRace && sent.Count(c => c.StartsWith("host_workshop_map")) == 1);
            MatchEnd();
            var dmpn = OptOf(o => o.Mode == Modes.Deathmatch);
            var dmp = Opt(dmpn).Map!;
            Click(alice, dmpn); Click(bob, dmpn); Click(cara, dmpn);
            Run(ChangeDelay);
            Check("With the plugin: CS2 restored Arms Race under the Deathmatch official map, and the convars read Deathmatch again", fakeLoaded == Modes.ArmsRace && (gameType, gameMode) == (1, 2) && S.Mode == "deathmatch");
            sent.Clear();
            Land();
            Check($"...the plugin's reply alone has it load once more in Deathmatch ({string.Join(" | ", sent)})",
                sent.Contains("os_mode deathmatch") && sent.IndexOf($"changelevel {dmp.Name}") == sent.IndexOf("os_mode deathmatch;game_type 1;game_mode 2") + 1 && sent.Contains($"changelevel {dmp.Name}")
                && AnnouncePending && Said($"{Label(dmp)} loads once more, in Deathmatch") && ModeReloads == 1 && S.Mode == "deathmatch");
            sent.Clear(); Run(1);
            Line($"Loading map \"{dmp.Name}\""); Line("Started:  \"\"");
            Run(2);
            Check("...the second load is in Deathmatch: the plugin says on already, and nothing loads a third time",
                fakeLoaded == Modes.Deathmatch && !AnnouncePending && S.Map == dmp && sent.Contains("os_mode deathmatch") && !sent.Any(c => c.StartsWith("changelevel")) && ModeReloads == 1);
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Settle(); MatchEnd();
            var wgn = OptOf(o => o.Mode == Modes.Wingman);
            var wgm = Opt(wgn).Map!;
            Click(alice, wgn); Click(bob, wgn); Click(cara, wgn);
            Run(ChangeDelay);
            sent.Clear();
            Land();
            Check("Official to official in another mode restores nothing: the plugin says on already, and it loads once",
                wgm.Official && fakeLoaded == Modes.Wingman && S.Mode == "wingman" && S.Map == wgm && sent.Contains("os_mode wingman") && !sent.Any(c => c.StartsWith("changelevel")) && ModeReloads == 0 && !AnnouncePending);
            Fresh();
            fakePlugin = true; fakeLoadsAs = Modes.Deathmatch;
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Ask(alice, m[2]); Land();
            Check("A Workshop map the plugin says came up in another mode is left as it is: one load, and the mode as told",
                sent.Count(c => c.StartsWith("host_workshop_map")) == 1 && ModeReloads == 0 && S.Mode == "armsrace" && !AnnouncePending);

            // ---- the defaults: Arms Race after a restart, and after 10 minutes with nobody on ----
            Fresh();
            S.Mode = "deathmatch"; (gameType, gameMode) = (1, 2);
            foreach (var p in new[] { alice, bob }) Join(p);
            Line("rcon from \"192.0.2.9:5000\": command \"quit\"");
            Check("A server restart: back to Arms Race", S.Mode == "armsrace");
            Fresh();
            S.Mode = "wingman"; (gameType, gameMode) = (0, 2);
            Line("Loading map \"ar_shoots\""); Line("Started:  \"\"");
            Check("A start of the start map nobody asked for (a server start): Arms Race", S.Mode == "armsrace");
            Fresh();
            S.Mode = "deathmatch"; (gameType, gameMode) = (1, 2);
            foreach (var p in new[] { alice, bob }) Join(p);
            Leave(alice); Leave(bob);
            Run(ResetEmptyMinutes * 60 - 5);
            Check($"Nobody on for just under {ResetEmptyMinutes} minutes in Deathmatch: it stays", S.Mode == "deathmatch" && Pending is null);
            Run(70);
            Check($"...past {ResetEmptyMinutes} minutes: a random Arms Race map, announced, and loaded in Arms Race",
                Said("back to Arms Race") && sent.Contains("os_mode armsrace;game_type 1;game_mode 0") && sent.Any(c => c.StartsWith("host_workshop_map"))
                && S.Mode == "armsrace" && (gameType, gameMode) == (1, 0) && S.Map is { } backOn && InArmsRace(backOn) && !backOn.Official);
            Fresh();
            foreach (var p in new[] { alice, bob }) Join(p);
            Leave(alice); Leave(bob);
            Run(ResetEmptyMinutes * 60 + 70);
            Check("An empty server in Arms Race already: no map change for the reset", Pending is null && !sent.Any(c => c.StartsWith("os_mode")));

            // ---- a change that never lands gives the old mode back ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            MatchEnd();
            var wOpt = OptOf(o => o.Mode == Modes.Wingman);
            Click(alice, wOpt); Click(bob, wOpt); Click(cara, wOpt);
            Run(ChangeDelay);
            Check("A Wingman change goes out", S.Mode == "wingman" && (gameType, gameMode) == (0, 2) && AnnouncePending);
            sent.Clear(); Run(600);
            Check("...and never logs its map start: after the load timeout the server is told Arms Race again",
                S.Mode == "armsrace" && sent.Contains("os_mode armsrace;game_type 1;game_mode 0") && (gameType, gameMode) == (1, 0) && !VoteHeld);

            // ---- a bridge that starts asks the server which mode it is in ----
            Fresh();
            (gameType, gameMode) = (0, 2);
            ModeProbeEnabled = true; ModeProbing = true;
            Run(1);
            Check("A bridge that starts reads game_type and game_mode over RCON: the server is in Wingman", S.Mode == "wingman" && !ModeProbing);
            (gameType, gameMode) = (0, 0);
            ModeProbing = true; Run(11);
            Check("...convars that are none of our modes (Casual) leave the recorded mode, and the probe stops", S.Mode == "wingman" && !ModeProbing);
            ModeProbeEnabled = false;

            // ---- Wingman with five on: the ballot's own maps and Random come from Arms Race ----
            Fresh();
            S.Mode = "wingman";
            foreach (var p in new[] { alice, bob, cara, dan, eve }) Join(p);
            MatchEnd();
            Check($"In Wingman with five on: {MapVote.MinMaps - 1} Arms Race maps, a Deathmatch map, Random in Arms Race - no Wingman",
                Ballot!.FillMode == Modes.ArmsRace && Ballot.Options.Select(o => o.Mode.Id).SequenceEqual(Enumerable.Repeat("armsrace", MapVote.MinMaps - 1).Concat(["deathmatch", "armsrace"])));

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
            PicturesBridgeSelfTest(Check, alice); // SelfTestPictures.cs: a Play in a mode, a name only another mode has

            // ---- the weights and the exclusions outside Arms Race ----
            Fresh();
            ModeMaps["deathmatch"] = [new(1, 3070000001, "dm_one", 41, false, "deathmatch"), new(2, 3070000002, "dm_two", 12, false, "deathmatch"), new(3, 3070000003, "dm_three", 9, false, "deathmatch")];
            var pool = PoolOf(Modes.Deathmatch);
            var total = pool.Sum(Weight);
            var rolls = Enumerable.Range(0, 30000).Select(_ => RandomPick(true, 1, Modes.Deathmatch)!).GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
            var worst = pool.Max(x => Math.Abs(rolls.GetValueOrDefault(Key(x)) - 30000 * Weight(x) / total) / (30000 * Weight(x) / total));
            Check($"Deathmatch's dice: officials weigh like a list's #1 (3x), the Workshop list's ranks 3x to 1x; 30000 rolls land within 15% of every share (worst {worst:P1})",
                pool.Where(x => x.Official).All(x => Weight(x) == 3) && Weight(pool.First(x => x.Name == "dm_three")) == 1 && Weight(pool.First(x => x.Name == "dm_two")) == 2 && worst < 0.15);
            Votes[Key(pool.First(x => x.Name == "dm_two"))] = new() { [alice.Item2] = 1, [bob.Item2] = 1 };
            Check("...and the votes count there as everywhere: two upvotes, four times the odds", Weight(pool.First(x => x.Name == "dm_two")) == 8);
            S.Crashes["de_dust2"] = 1; Broken["de_mirage"] = 3; Votes["de_inferno"] = new() { [alice.Item2] = AdsVote };
            Blocked["3070000001"] = ("dm_one", "dm_one", "test"); S.Map = pool.First(x => x.Name == "de_nuke");
            var never = new[] { "de_dust2", "de_mirage", "de_inferno", "3070000001", "de_nuke" };
            Check("...and the exclusions too: never a crasher, a broken or blocked map, one marked Ads, nor the map that is on",
                Enumerable.Range(0, 4000).Select(_ => Key(RandomPick(true, 1, Modes.Deathmatch)!)).All(k => !never.Contains(k)));

            // ---- the block lists, the server cfgs and their mounts ----
            const string blockDir = "../plugin/OverStrike2"; // since the fold of 2026-11-01
            if (Modes.All.All(x => File.Exists(Path.Combine(blockDir, x.BlockFile))))
            {
                var lists = Modes.All.ToDictionary(x => x.Id, x => BlockList.Parse(File.ReadAllText(Path.Combine(blockDir, x.BlockFile)), out var errors) is var l && errors.Count == 0 ? l : []);
                var names = lists.ToDictionary(kv => kv.Key, kv => kv.Value.Select(e => e.ConVar).Order().ToList());
                Check($"Block lists: {string.Join(", ", Modes.All.Select(x => $"{x.BlockFile} {lists[x.Id].Count}"))} - each parses, the same game-changers in every mode",
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
                if (Modes.All.All(x => File.Exists(Path.Combine(blockDir, x.BlockFile))))
                {
                    var agree = Modes.All.SelectMany(x => BlockList.Parse(File.ReadAllText(Path.Combine(blockDir, x.BlockFile)), out _).Where(e => e.Value != null && cfgs[x.Id].ContainsKey(e.ConVar))
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
            Check("The rules' numbers for the vote are phrases the words may use", RuleNumbers().Contains($"{MapVote.Seconds} seconds") && RuleNumbers().Contains($"at least {MapVote.MinMaps} maps")
                && RuleNumbers().Contains($"up to {Modes.Wingman.MaxPlayers} players") && RuleNumbers().Contains($"waits {MidVoteCooldownMinutes} minutes"));

            string HudTextNow() { RefreshHud(); return HudText; }
        }
        finally
        {
            (Send, After, Now, StateDir, HudActive, VoteCapable, ModeProbeEnabled) = (realSend, realAfter, realNow, realStateDir, realHud, realCapable, realProbe);
            S = new State(); Votes = new(); Broken = new(); Blocked = new(); Players.Clear(); Queue.Clear(); Owners.Clear(); Airtime.Clear(); Pending = null; AnnouncePending = false;
            NewsItems.Clear(); CooldownUntil = default; MaintenanceOn = false; WatchStop(); ResetCrashWatch(); ClearNotices(); SkipVotes.Clear();
            Ballot = null; VoteHeld = false; ModeBeforeChange = null; ModeProbing = false; ModeMaps.Clear(); ModeMapsDir = null; MidVoteWait.Clear();
            try { Directory.Delete(dir, true); } catch (Exception e) { Console.WriteLine($"selftest: could not remove {dir}: {e.Message}"); }
        }
    }
}
