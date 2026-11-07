// --selftest, part two: a pick is a vote (the vote, its freeze and its wait, end to end), likes, the weighted dice, !previous,
// the crash watch, the start roll, the maintenance flag and the on-screen guide. Everything runs on a handed clock with a fake timer wheel, and
// every RCON command is recorded, so a whole evening of play - joins, map loads, match ends, departures - replays in milliseconds and the same
// way every time. The vote's own rules in detail are SelfTestVote.cs's.
using System.Text.Json;

static partial class Program
{
    static void QueueSelfTest(Action<string, bool> Check)
    {
        var (realSend, realAfter, realNow, realStateDir, realHud) = (Send, After, Now, StateDir, HudActive);
        HudActive = () => false; // the HUD plugin is not polling unless a test says so
        string dir;
        try { dir = Path.Combine(AppContext.BaseDirectory, $"selftest-state-{Environment.ProcessId}"); Directory.CreateDirectory(dir); }
        catch (Exception) { dir = Path.Combine(Path.GetTempPath(), $"os2-bridge-selftest-{Environment.ProcessId}"); Directory.CreateDirectory(dir); } // the image's /app is not writable
        StateDir = dir;
        var clock = new DateTime(2026, 10, 4, 20, 0, 0, DateTimeKind.Utc);
        Now = () => clock;
        var timers = new List<(DateTime Due, long Seq, Action Work)>();
        long seq = 0;
        After = (ms, work) => timers.Add((clock.AddMilliseconds(ms), seq++, work));
        var sent = new List<string>();
        var guideReply = "";
        var rconDown = false;
        string? statusMap = null; // the map RCON status reports (its spawngroup 1 line), following every "Loading map" once a test sets it; null: a status reply without one
        var statusSpawn = 1;      // its "spawn" line: the maps this server process has loaded - every "Loading map" counts up, a new process starts over
        Send = command =>
        {
            sent.Add(command);
            return rconDown ? null : command.StartsWith(Guide.Command) ? guideReply
                : command == "status" && statusMap != null ? $"hostname : test\nspawn    : {statusSpawn}\nplayers  : 0 humans, 0 bots (0 max)\n---------spawngroups----\nloaded spawngroup(  1)  : SV:  [1: {statusMap} | main lump | mapload]\nloaded spawngroup(  2)  : SV:  [2: prefabs/misc/team_select | main lump | mapload | point_prefab]\n#end"
                : "";
        };
        var loads = 0;
        try
        {
            // ---- harness: log lines in the HTTP push form, a clock that only moves when told to ----
            void Run(double seconds) // moves the clock a second at a time, firing every timer that falls due, then the once-a-second tick
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
                    Tick(); AfterWork(); // what the event loop does with each tick
                }
            }
            void Line(string ev) // a log line; a "Loading map" one is also the fake server loading that map
            {
                if (ev.StartsWith("Loading map")) { statusSpawn++; if (statusMap != null && ev.Split('"') is [_, var loaded, ..]) statusMap = loaded; }
                Ingest($"{clock:MM/dd/yyyy - HH:mm:ss}.000 - {ev}", null);
            }
            // A player saying something in chat (presence only), and then doing it - what a click in the menu asks the
            // bridge for, in the same words the chat commands had.
            void Talk((string Name, string Id) p, string text) { Line($"\"{p.Name}<2><{p.Id}><CT>\" say \"{text}\""); Command(p.Name, p.Id, text); }
            void Join((string Name, string Id) p) { Line($"\"{p.Name}<2><{p.Id}><>\" connected, address \"192.0.2.1:27005\""); Line($"\"{p.Name}<2><{p.Id}><>\" entered the game"); }
            void Leave((string Name, string Id) p, string reason = "NETWORK_DISCONNECT_DISCONNECT_BY_USER") => Line($"\"{p.Name}<2><{p.Id}><CT>\" disconnected (reason \"{reason}\")");
            void MatchEnd() => Line("Game Over: gungameprogressive mg_armsrace somemap score 0:0 after 9 min");
            void Land() // the countdown runs out, the server logs everyone out with reason SHUTDOWN, loads the map, and everyone reconnects
            {
                Run(ChangeDelay);
                var back = Players.Where(kv => kv.Value.GoneSince == null).Select(kv => (kv.Value.Name, kv.Key)).ToList();
                foreach (var p in back) Leave(p, "NETWORK_DISCONNECT_SHUTDOWN");
                Line($"Loading map \"bsp_{++loads}\""); Line("Started:  \"\"");
                foreach (var p in back) Join(p);
            }
            void Fresh() // a bridge that has just started, on a server that is on some random map nobody asked for
            {
                S = new State(); Broken = new(); Packs = new(); Votes = new(); PickConfirms.Clear();
                Checked = new(); Removed = new(); Blocked = new();
                Players.Clear(); Ballot = null; VoteWaitUntil = default; LastRound = 0;
                Pending = null; AnnouncePending = false; AnnounceDeadline = default; MatchEndUntil = default;
                StartRollOwed = false; StartMapAt = default; StartRollRetryAt = default; LastNamedMapStart = default; rconDown = false;
                statusMap = null; statusSpawn = 1; ResetCrashWatch(); WatchStop(); ClearNotices(); RestartDue = null; RematchDue = null; RestartConfirmBy = ""; RestartConfirmUntil = default; RestartCooldownUntil = default; StartRollNeedsProof = false;
                Sizes = new(); SizeBounds = (0, 0, 0); NobodySince = null; ResetDone = false; ResetProbeAt = default; // Sizes.cs, EmptyServer.cs
                WhyOpen.Clear(); // Likes.cs
                timers.Clear(); Quiet();
                if (File.Exists(MaintenanceFile)) File.Delete(MaintenanceFile);
                InMaintenance();
                clock = clock.AddHours(1);
            }
            bool Said(string fragment) => sent.Any(c => c.StartsWith("say ") && c.Contains(fragment)) || Told.Any(t => t.Contains(fragment)); // a note or the news line (Notices.cs)
            void Quiet() { sent.Clear(); ClearNotices(); }
            var small = Maps.Where(m => m.SizeMb < 400 && m.Rank > 0).ToList(); // small maps: their announcements stay short
            var m = Enumerable.Range(0, 12).Select(i => small[20 + i * 7]).ToList(); // twelve ordinary, distinct, rollable maps
            var (alice, bob, cara, dan) = (("Alice", "[U:1:1001]"), ("Bob", "[U:1:1002]"), ("Cara", "[U:1:1003]"), ("Dan", "[U:1:1004]"));
            string Rank(int i) => m[i].Rank.ToString();
            // A pick that goes through, for the tests that are not about the vote: the wait after the last vote waived, and every player present
            // votes for it (alone, the picker's own vote is everybody's, so it is decided at once).
            void Go((string Name, string Id) p, int i) => GoTo(p, m[i]);
            void GoTo((string Name, string Id) p, MapInfo map)
            {
                VoteWaitUntil = default;
                Talk(p, $"!map {ArgOf(map)}");
                if (Ballot is { Open: true } b && b.Options.FirstOrDefault(o => o.Map != null && Key(o.Map) == Key(map)) is { } o)
                    foreach (var id in PresentIds(clock).Where(id => !b.Choices.ContainsKey(id)).ToList()) CastBallot(Players[id].Name, id, $"{b.Round} {o.N}");
            }
            void VoteFor((string Name, string Id) p, int n) => Command(p.Name, p.Id, MapVote.Click(Ballot?.Round ?? 0, n));

            // ---- a pick is a vote, end to end ----
            Fresh();
            Join(alice);
            Talk(alice, $"!map {Rank(1)}");
            Check("Alone, a pick opens the vote with that map on it and the picker's vote, which is everybody's: decided at once, and the change counts down",
                Pending is { Auto: false, By: "the vote" } p1 && p1.Map == m[1] && Said("Alice put") && Said("Everybody has voted") && Ballot is { Open: false });
            var frozen = BuildVoteState(clock);
            Check("...everybody stays frozen through the countdown", frozen is { Freeze: true, Open: false } && frozen.Result?.How == "all");
            Land();
            Check("...and once it is out the vote is over: nobody frozen, the map is the vote's", Ballot == null && !BuildVoteState(clock).Freeze && S.Map == m[1] && S.PickedBy == "the vote");
            Quiet(); Talk(alice, $"!map {Rank(2)}");
            Check($"After any vote, {MapVote.WaitSeconds / 60} minutes before anybody can call another: a pick is refused, saying how long",
                Ballot == null && Pending == null && Said("You can call a vote again in") && Said("Alice tried to start another vote - The next one can start in"));
            Quiet(); Talk(alice, "!next");
            Check("...Start a vote too", Ballot == null && Said("You can call a vote again in"));
            var waitState = BuildVoteState(clock);
            Check("...and the live state carries the wait, so the board's button says it, looks off, and a click gets its answer with the time left",
                waitState.WaitFor is > 0 and <= MapVote.WaitSeconds && HudView.Action(BuildHud(clock), waitState) is { Command: "!next", Text: "Start a vote" } && HudView.PlayerClasses(BuildHud(clock), 1, waitState).Contains((HudView.Screen, "act-off", true)));
            Run(MapVote.WaitSeconds);
            Quiet(); Talk(alice, $"!map {Rank(1)}");
            Check("The map that is on is no pick outside a vote: it is on already", Ballot == null && Said("is on now"));
            Quiet(); Talk(alice, "!next random");
            Check("!random, alone: the vote opens with the player's vote on Random, which is everybody's - the dice roll a map of this mode",
                Pending is { By: "the vote" } pr && pr.Map != m[1] && Said("The vote chose Random"));
            Land();

            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Go(alice, 1); Land(); Run(MapVote.WaitSeconds + 1); Quiet();
            Talk(alice, $"!map {Rank(2)}");
            var vote = Ballot!;
            var vs = BuildVoteState(clock);
            Check($"With three on, a pick opens the vote for everybody and freezes everybody until all have voted: the map on it with Alice's vote, Keep playing, one map of each other mode, random maps of this mode to a page of {MapVote.PageCards}, Random beside the cards",
                vote is { Open: true } && Pending == null && vs is { Open: true, Freeze: true, MatchEnded: false } && vs.Maps.Count == MapVote.PageCards
                && vs.Maps[0] is { Source: VoteSource.Picked, Votes: 1, Who: "Alice" } firstCard && firstCard.Key == Key(m[2])
                && vs.Maps.Count(o => o.Source == VoteSource.Keep) == 1 && vs.Maps.Single(o => o.Source == VoteSource.Keep) is { Label: "Keep playing this map", Key: var keepKey } && keepKey == Key(m[1])
                && vs.Maps.Count(o => o.Mode == Modes.DeathmatchId) == 1 && vs.Maps.Count(o => o.Mode == Modes.WingmanId) == 1 && vs.Random is { N: MapVote.RandomOption }
                && vs.Choices[1001] == firstCard.N && vs.Waiting.SequenceEqual(["Bob", "Cara"]) && vs.Present == 3);
            var fillN = vote.Options.Where(o => o.Source == VoteSource.Rolled && o.Mode.Id == Modes.ArmsRaceId).MaxBy(o => o.Order)!.N;
            Quiet(); Talk(bob, $"!map {Rank(3)}");
            vs = BuildVoteState(clock);
            Check("A pick while the vote is open goes on it with the picker's vote, in the place - and the number - of a random card nobody voted for: one page still holds it all",
                vs.Maps.Count == MapVote.PageCards && vs.Maps.Single(o => o.Key == Key(m[3])) is { Source: VoteSource.Picked, Votes: 1, Who: "Bob" } bobs && bobs.N == fillN
                && vote.Choices["[U:1:1002]"] == fillN && Said("Bob added"));
            Check("...the cards with the most votes first, ties in their order: the two picks, then Keep", vs.Maps.Take(3).Select(o => o.Source).SequenceEqual([VoteSource.Picked, VoteSource.Picked, VoteSource.Keep]));
            Quiet(); Talk(cara, $"!map {Rank(3)}");
            Check("...a map on it already gets the vote, and joins its picker", vote.Choices["[U:1:1003]"] == fillN && BuildVoteState(clock).Maps.Single(o => o.N == fillN).Who == "Bob and Cara");
            Check("...that was everybody: the most votes win, everybody stays frozen through the countdown", Pending?.Map == m[3] && Said("Everybody has voted") && BuildVoteState(clock).Freeze);
            Quiet(); Talk(alice, $"!map {Rank(4)}");
            Check("A pick during the countdown is refused", Said("Already changing to") && Pending?.Map == m[3]);
            Land();

            Run(MapVote.WaitSeconds + 1); Quiet();
            Talk(alice, "!next");
            vote = Ballot!;
            var keepN = vote.Options.Single(o => o.Source == VoteSource.Keep).N;
            Check("Start a vote opens one without a map, Keep playing on it, nobody's vote cast", vote is { Open: true } && vote.Choices.Count == 0 && Said("Alice started a vote"));
            foreach (var p in new[] { alice, bob, cara }) VoteFor(p, keepN);
            Check("Keep playing wins: nothing changes, and the window says so, everybody still frozen", Pending == null && Ballot is { KeptUntil: not null } && BuildVoteState(clock) is { Freeze: true, Kept: true } && Said("The vote kept"));
            Run(MapVote.KeptSeconds);
            Check("...until everybody unfreezes at the same moment", Pending == null && Ballot == null && !BuildVoteState(clock).Freeze);
            Run(MapVote.WaitSeconds + 1); Quiet();
            Talk(bob, "!next"); Run(MapVote.Seconds + MapVote.KeptSeconds + 1);
            Check($"Nobody voting in the {MapVote.Seconds} s keeps the map mid-match", Pending == null && Ballot == null && Said("Nobody voted") && S.Map == m[3]);
            HudActive = () => true;
            Quiet(); MatchEnd();
            vote = Ballot!;
            Check("A match end opens the vote by itself, the wait after the last one notwithstanding: Play this map again on it",
                vote is { Open: true, MatchEnded: true } && BuildVoteState(clock).Maps.Single(o => o.Source == VoteSource.Keep).Label == "Play this map again" && Said("Match over - Vote for the next map"));
            Run(MapVote.Seconds + 1);
            Check("...nobody voting there is Random: the dice roll a map of this mode", Pending is { By: "the vote" } mr && mr.Map != m[3] && Said("Nobody voted - Rolling the dice"));
            HudActive = () => false;
            Land();
            Run(MapVote.WaitSeconds + 1); Quiet();
            Talk(alice, "!next"); Run(5);
            HudActive = () => true; MatchEnd(); HudActive = () => false;
            Check("A match that ends while a vote is open makes it the match end's: Keep reads Play this map again, nobody voting is Random",
                Ballot is { Open: true, MatchEnded: true } && BuildVoteState(clock).Maps.Single(o => o.Source == VoteSource.Keep).Label == "Play this map again");
            Run(MapVote.Seconds);
            Check("...and so it rolls the dice", Pending != null && Said("Nobody voted - Rolling the dice"));
            Land();
            Run(MapVote.WaitSeconds + 1); Quiet();
            Talk(alice, $"!map {Rank(5)}");
            var dan5 = Ballot!.Options.Single(o => o.Map == m[5]).N;
            Join(dan);
            vs = BuildVoteState(clock);
            Check("A player who joins during a vote is counted present and waited for (and the plugin freezes them too)", vs.Present == 4 && vs.Waiting.Contains("Dan") && vs.Freeze);
            VoteFor(bob, dan5); VoteFor(cara, dan5);
            Check("...so three of four is not everybody yet", Pending == null && Ballot is { Open: true });
            VoteFor(dan, dan5);
            Check("...and their vote ends it", Pending?.Map == m[5]);
            Land();

            // ---- the crash counter (CrashWatch.cs): RCON status's spawn counter tells a new server process from the old one ----
            // The fake server counts its loads in statusSpawn and shows the map it loaded last; Crash() is the process dying without a
            // quit and the new one booting on its start map, as the log shows that.
            void Crash() { statusSpawn = 0; Line($"Loading map \"{StartMap}\""); Line("Started:  \"\""); }
            Fresh(); statusMap = "bsp"; statusSpawn = 7;
            Join(alice); Go(alice, 1); Land(); Run(10);
            Check("The first reading after the bridge started is only the baseline", SpawnSeen == 8 && S.Crashes.Count == 0);
            Go(alice, 2); Land(); Run(10);
            Check("Every map start of the same process counts the spawn counter up: no crash", SpawnSeen == 9 && S.Crashes.Count == 0);
            var crashed = S.Map!; Quiet();
            Crash(); Run(10); Join(bob);
            Check("A new process without a quit is recorded against the map that was on, said at once and to whoever joins",
                S.Crashes.GetValueOrDefault(Key(crashed)) == 1 && S.Crashes.Count == 1 && Said($"The server crashed on {Clip(Label(crashed), 44)} and has restarted - That map no longer comes up at random")
                && Said($"after a crash on {Clip(Label(crashed), 44)}"));
            Quiet(); Talk(cara, $"!map {crashed.Rank}");
            Check("...a pick of it now asks twice (that player's own confirm)", Said("crashed the server 1 time(s)") && Pending?.Map != crashed && Ballot == null);
            Check("...the dice skip it", Enumerable.Range(0, 500).Select(_ => RandomPick()).All(x => x == null || Key(x) != Key(crashed)));
            Check("...and the HUD's map card says so", BuildCatalog().Maps.FirstOrDefault(x => x.Id == crashed.Id) is { Crashes: 1 });
            File.WriteAllText(MaintenanceFile, ""); InMaintenance(); Quiet(); Join(dan);
            Check("Joiners do not hear about it while the maintenance flag exists...", !Said("after a crash"));
            File.Delete(MaintenanceFile); InMaintenance();
            Run(CrashNoticeMinutes * 60); Quiet(); Join(dan);
            Check("...nor once the reconnect burst is long over", !Said("after a crash"));

            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Go(alice, 3); Land(); Run(10);
            File.WriteAllText(MaintenanceFile, ""); InMaintenance();
            Crash(); Run(20);
            Check("Maintenance (the map sweeps, the cache work): a new process is not recorded, and nobody is told",
                S.Crashes.Count == 0 && CrashNotice == "" && SpawnSeen == 1);
            File.Delete(MaintenanceFile); InMaintenance();

            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Go(alice, 3); Land(); Run(10);
            Line("rcon from \"192.0.2.2:5555\": command \"quit\"");
            Crash(); Run(20);
            Check("A new process after a clean quit (!restart, the 07:00 restart, an admin) is not a crash", S.Crashes.Count == 0 && SpawnSeen == 1);

            // ---- the restart watcher's notice: the engine segfaults on quit and its echo dies with the process ----
            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Go(alice, 3); Land(); Run(10);
            Check("The restart watcher's notice: refused unless it is gg-quit 1 with a kind",
                ApplyQuitNotice("hello\n").StartsWith("refused") && ApplyQuitNotice("gg-quit\t2\tdaily").StartsWith("refused") && ApplyQuitNotice("gg-quit\t1\t").StartsWith("refused") && !S.CleanShutdown);
            Check("...a notice is the echo: the process is going down cleanly, the start roll owed, the map forgotten",
                ApplyQuitNotice("gg-quit\t1\tdaily") == "ok: going down" && S.CleanShutdown && StartRollOwed && S.Map == null);
            S.PickedBy = "a marker the echo must leave alone";
            Line("rcon from \"127.0.0.1:33908\": command \"quit\"");
            Check("...an echo that arrives as well changes nothing", S.PickedBy == "a marker the echo must leave alone" && S.CleanShutdown);
            S.PickedBy = null;
            Crash(); Run(20);
            Check("...and the new process is no crash, without any echo", S.Crashes.Count == 0 && SpawnSeen == 1);

            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Go(alice, 3); Land(); Run(10);
            var (mapOnBefore, logBefore) = (S.Map, S.LogName);
            S.Mode = Modes.DeathmatchId;
            Check("A notice the watcher takes back (its quit could not be delivered): the server stays up, nothing is clean any more",
                ApplyQuitNotice("gg-quit\t1\tdaily") == "ok: going down" && ApplyQuitNotice("gg-quit\t1\tcancel\tdaily") == "ok: cancelled" && !S.CleanShutdown
                && ApplyQuitNotice("gg-quit\t1\tcancel\tdaily") == "ok: nothing was announced");
            Check("...and the bridge knows what is on again: the map, its name in the log, its mode, and no start roll is owed",
                mapOnBefore == m[3] && S.Map == m[3] && S.LogName == logBefore && S.Mode == Modes.DeathmatchId && !StartRollOwed);
            S.Mode = Modes.ArmsRaceId;
            var onAfterCancel = Key(m[3]);
            Crash(); Run(20);
            Check("...so a crash after it is still a crash", S.Crashes.GetValueOrDefault(onAfterCancel) == 1);

            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Go(alice, 3); Land(); Run(10);
            ApplyQuitNotice("gg-quit\t1\tdaily");
            Line("Loading map \"ar_shoots\""); Line("Started:  \"\"");
            Check("A map start after the notice says for itself what is on: a late take-back brings nothing back",
                ApplyQuitNotice("gg-quit\t1\tcancel\tdaily") == "ok: nothing was announced" && S.Map != m[3]);

            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Go(alice, 3); Land(); Run(10);
            Talk(alice, "!restart"); Talk(alice, "!restart"); Run(PressedSeconds + 1);
            Check("The bridge's own !restart marks its quit clean as it sends it", sent.Contains("quit") && S.CleanShutdown);
            Crash(); Run(20);
            Check("...so its restart is no crash even when the echo never comes", S.Crashes.Count == 0 && SpawnSeen == 1);

            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Go(alice, 3); Land(); Run(10);
            Go(alice, 4); Run(ChangeDelay);
            Check("(the change to the next map has gone out)", AnnouncePending && S.Map == m[4]);
            Crash(); Run(20);
            Check("A process that dies while a map loads is that map's crash", S.Crashes.GetValueOrDefault(Key(m[4])) == 1 && S.Crashes.Count == 1);

            // two quick starts: a reading counts only when status shows the newest start's map, and the starts are judged together
            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Go(alice, 5); Land(); Run(10);
            Go(alice, 6); Land();
            statusSpawn++; statusMap = "bsp_ahead"; Run(6); // before its reading the server is on another map already, whose log line is late
            Ingest($"{clock:MM/dd/yyyy - HH:mm:ss}.000 - Loading map \"bsp_ahead\"", null); Run(20);
            Check("Status ahead of the log: no reading until the late start is in, then both are judged together - no crash",
                S.Crashes.Count == 0 && SpawnSeen == 6 && SpawnSeenStarts == MapStarts);

            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Go(alice, 7); Land(); Run(10);
            var wasOn = S.Map!;
            rconDown = true; Crash(); Run(2); Line("Loading map \"bsp_quick\""); Run(3); rconDown = false; Run(30);
            Check("Two starts before one reading, the first of them the new process's boot: what was on before the boot is blamed, not the boot map",
                S.Crashes.GetValueOrDefault(Key(wasOn)) == 1 && S.Crashes.Count == 1);

            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Go(alice, 8); Land(); Run(10);
            rconDown = true; Line("Loading map \"ar_baggage\""); Line("Started:  \"\""); Run(2); Crash(); Run(3); rconDown = false; Run(30);
            Check("Two starts before one reading, the second the new process's boot: the map the first one loaded is blamed, not the one before it",
                S.Crashes.GetValueOrDefault("ar_baggage") == 1 && S.Crashes.Count == 1);

            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Go(alice, 9); Land(); Run(10);
            statusSpawn = 2; Line("Loading map \"bsp_unseen\""); Run(10);
            Check("A counter that fell by more loads than the bridge saw starts (a boot it never saw) is not judged", S.Crashes.Count == 0 && SpawnSeen == 3);

            Fresh(); statusMap = "bsp_on"; statusSpawn = 6; S.Map = m[10]; S.LogName = "bsp_on"; // a bridge (re)started while the server plays m[10]
            SpawnBaseline();
            Check("A bridge that starts reads the process that is running...", SpawnSeen == 6 && S.Crashes.Count == 0);
            Crash(); Run(10);
            Check("...so a crash of the map that was on when it started counts too", S.Crashes.GetValueOrDefault(Key(m[10])) == 1);
            Fresh(); statusMap = "bsp_elsewhere"; statusSpawn = 6; S.Map = m[10]; S.LogName = "bsp_on";
            SpawnBaseline(); Run(60);
            Check("...but not from a status that shows another map than the one it knew: no reading, so nothing to blame later", SpawnSeen == null);
            Check("RCON status's spawn line is read, and a reply without one reads as none",
                StatusSpawn("hostname : Over Strike 2\nspawn    : 10\nversion  : 1.41.9.1") == 10 && StatusSpawn("hostname : x\n") == null);

            // ---- the start roll, and a fresh bridge that must not yank a running server's map ----
            Fresh();
            Join(alice); Session("token-1"); Talk(alice, "hi"); Run(10);
            Check("The first server token a fresh bridge sees does not roll by itself: a running server keeps its map", Pending == null);
            S.CleanShutdown = true; Session("token-2"); Line("Loading map \"ar_shoots\""); Run(4);
            Check("A new server process is left alone for 5 s after its start map...", Pending == null);
            Run(2);
            Check("...then rolls the dice", Pending is { Auto: true } && Said("Server started - Rolling the dice"));
            Join(alice); Join(dan);
            Land(); Run(61); Go(dan, 1); Land(); Run(30); Go(alice, 2);
            Check("(a change counts down when the server restarts)", Pending?.Map == m[2]);
            S.CleanShutdown = true; Session("token-3"); Line("Loading map \"ar_shoots\""); Run(4);
            Check("A restarted server's start map ends the change that died with the old process, and the server is left alone for 5 s", Pending == null && !AnnouncePending);
            Run(2);
            Check("...then the dice roll: there is no queue to serve any more", Pending is { Auto: true } && Said("Server started - Rolling the dice"));

            // ---- players on at a server start: they choose - a vote instead of the dice, and nobody voting rolls them ----
            Fresh();
            HudActive = () => true;
            S.CleanShutdown = true; Session("token-v"); Line("Loading map \"ar_shoots\""); Join(alice); Join(dan); Run(6);
            Check("Players on at a server start: a vote for everybody opens instead of the dice, called by nobody, and the start roll is settled",
                Ballot is { Open: true, Starter: "", ServerStart: true, MatchEnded: false } && Pending == null && Said("Server started - Vote for the next map") && !Said("Rolling the dice") && !StartRollOwed);
            Run(MapVote.Seconds + 1);
            Check("...nobody voting rolls the dice, as the start roll would have", Pending is { By: "the vote" } && Said("Nobody voted - Rolling the dice"));
            HudActive = () => false;
            Fresh();
            S.CleanShutdown = true; Session("token-e"); Line("Loading map \"ar_shoots\""); Run(6);
            Check("...an empty server's start: the dice, no vote", Ballot == null && Pending is { Auto: true } && Said("Server started - Rolling the dice"));

            // ---- the live boot of 2026-10-04: the token change went unseen, the one map start had no name, RCON answered 15 s later ----
            Fresh();
            S.ServerToken = "boot"; S.LogName = "aim_old_map"; S.CleanShutdown = true; // the state the bridge started with: this server's token already known
            rconDown = true;
            Session("boot"); Line("Started:  \"\"");
            Check("A lone nameless map start is taken for START_MAP, and the start roll is owed", S.Map?.Name == "ar_shoots" && StartRollOwed);
            Run(15);
            Check("RCON not answering: no countdown into the void, the roll stays owed", Pending == null && !Said("rolling the dice") && StartRollOwed);
            rconDown = false; Run(11);
            Check("RCON answering: the start roll goes out", Pending is { Auto: true } && Said("Server started - Rolling the dice"));
            Run(ChangeDelay);
            Check("...and only a change command that went out settles it", sent.Any(c => c.StartsWith("host_workshop_map")) && !StartRollOwed);
            Fresh();
            Session("boot-1"); S.CleanShutdown = true; Session("boot-2"); Line("Loading map \"ar_shoots\"");
            File.WriteAllText(MaintenanceFile, ""); Run(30);
            Check("Maintenance: the owed start roll waits...", Pending == null && StartRollOwed);
            File.Delete(MaintenanceFile); Run(2);
            Check("...and goes out once the flag is gone", Pending is { Auto: true } && Said("Server started - Rolling the dice"));

            // ---- production restarts as they really happen: CS2 keeps its X-Server-Instance-Token across process restarts (one token,
            // 5e1f0c2ab7d94a36, in state.json from 2026-09-15 to 2026-10-05, through every deploy, daily restart and !restart), so a
            // restart shows only as the quit's RCON echo (none after a crash or a kill), then the boot's lone nameless map start ----
            void Restart(bool clean, string? header = null)
            {
                if (clean) Line("rcon from \"192.0.2.101:40000\": command \"quit\"");
                rconDown = true; Run(60); rconDown = false; // the process is gone, SteamCMD and pre.sh run, the engine boots
                statusSpawn = 1; if (statusMap != null) statusMap = header ?? StartMap; // the new process has loaded its start map
                Session("prod"); Ingest($"{clock:MM/dd/yyyy - HH:mm:ss}.000 - Started:  \"\"", header);
            }
            bool Rolled() => Pending is { Auto: true } && Said("Server started - Rolling the dice");
            void Confirm((string Name, string Id) p) { Talk(p, "!restart"); Talk(p, "!restart"); }

            Fresh(); statusMap = "ar_shoots"; Session("prod");
            Join(alice); Go(alice, 1); Land(); Run(60);
            Confirm(alice); Run(PressedSeconds - 1);
            Check($"!restart: no quit during its {PressedSeconds} s countdown", !sent.Contains("quit"));
            Run(2);
            Check($"!restart: quit goes out {PressedSeconds} s after the confirm", sent.Contains("quit"));
            Restart(clean: true); Run(6);
            Check("Restart path 1, !restart on a map that is up: the boot's start map rolls the dice", Rolled());

            Fresh(); statusMap = "ar_shoots"; Session("prod");
            Join(alice); Run(5); Go(alice, 1); Run(ChangeDelay + 120); // the change went out; the download stalls
            Confirm(alice); Run(PressedSeconds + 1); Quiet();
            Restart(clean: true); Run(6);
            Check("Restart path 2, !restart on a stuck load: the boot is not taken for the stuck map", !Said($"Now on {Label(m[1])}") && S.Map?.Name == "ar_shoots" && !AnnouncePending);
            Check("...and the dice roll", Rolled());

            Fresh(); statusMap = "ar_shoots"; Session("prod");
            Join(alice); Run(5); Go(alice, 1); Run(ChangeDelay + 120);
            Confirm(alice); Run(PressedSeconds + 1); Quiet();
            Restart(clean: true, header: "ar_shoots"); Run(6);
            Check("Restart path 2 with the boot's map start named by X-Game-Map: the same", !Said($"Now on {Label(m[1])}") && Rolled());

            Fresh(); statusMap = "ar_shoots"; Session("prod");
            Join(alice); Run(5); Go(alice, 1); Run(1);
            sent.Clear(); Confirm(alice); Run(PressedSeconds + 1); // the map change counting down ends at the second click: its host_workshop_map would hit a dying server
            Check("Restart path 3, !restart during a countdown: the change dies at the second click, its map command never goes out", sent.Contains("quit") && !sent.Any(c => c.StartsWith("host_workshop_map")));
            Quiet(); Restart(clean: true); Run(6);
            Check("...and the boot rolls", !Said($"Now on {Label(m[1])}") && Rolled());

            Fresh(); statusMap = "ar_shoots"; Session("prod");
            Join(alice); Join(bob); Run(30); MatchEnd();
            Check("Restart path 4, the 07:00 restart with players on: the match ends, the dice count down...", Pending is { Auto: true });
            Run(3); Quiet(); Restart(clean: true); Run(6); // the restart watcher quits 3 s after Game Over
            Check("...the watcher's quit lands inside that countdown, and the boot still rolls", Rolled());

            Fresh(); statusMap = "ar_shoots"; Session("prod");
            Join(alice); Run(5); Go(alice, 1); Run(ChangeDelay + 3); // the map crashes the server while it loads
            Quiet(); Restart(clean: false); Run(6);
            Check("Restart path 5, a crash or freeze kill during a load (no quit echo): the boot rolls", !Said($"Now on {Label(m[1])}") && Rolled());

            Fresh(); statusMap = "ar_shoots"; Session("prod");
            Join(alice); Go(alice, 1); Land(); Run(60);
            Quiet(); Restart(clean: false); Run(6);
            Check("Restart path 6, Docker restarting cs2 alone (SIGKILL, no echo) on a map that is up: the boot rolls", Rolled());
            Check("...and with no quit echo, the crash counter marks the map that was up", S.Crashes.GetValueOrDefault(Key(m[1])) == 1 && S.Crashes.Count == 1);

            Fresh(); Session("prod");
            Join(alice); Talk(alice, "!map ar_shoots"); Run(ChangeDelay); Line("Loading map \"ar_shoots\""); Line("Started:  \"\""); Run(20);
            Check("A pick of the start map (alone, the picker's vote decides it) is the vote's: no roll", Pending == null && S.PickedBy == "the vote" && !StartRollOwed);

            // ---- a bridge that starts while the server sits on its start map (a missed roll, or the bridge redeployed alone) ----
            Fresh(); S.ServerToken = "prod"; S.Map = Officials.First(o => o.Name == StartMap); S.LogName = StartMap; statusMap = StartMap;
            BridgeStart(); Run(6);
            Check("A bridge starting on a server that RCON says is on its start map, picked by nobody, rolls it", Rolled());
            Fresh(); S.ServerToken = "prod"; S.Map = Officials.First(o => o.Name == StartMap); S.LogName = StartMap; S.PickedBy = "Alice"; statusMap = StartMap;
            BridgeStart(); Run(30);
            Check("...but not when a player picked it", Pending == null);
            Fresh(); S.ServerToken = "prod"; S.Map = m[3]; S.LogName = "bsp_x"; statusMap = "bsp_x";
            BridgeStart(); Run(30);
            Check("...and a server on any other map keeps it", Pending == null && !StartRollOwed);
            Fresh(); S.ServerToken = "prod"; S.Map = Officials.First(o => o.Name == StartMap); S.LogName = StartMap; statusMap = null;
            BridgeStart(); Run(30);
            Check("...nor when RCON status does not say which map is on: a bridge start rolls only on proof", Pending == null);

            // ---- the maintenance flag: no automatic change of any kind; a vote a player calls still decides, as a pick always did ----
            Fresh();
            foreach (var p in new[] { alice, bob }) Join(p);
            File.WriteAllText(MaintenanceFile, "");
            Run(1);
            Check("The flag is noticed within a second", MaintenanceOn);
            Session("token-a"); S.CleanShutdown = true; Session("token-b"); Line("Loading map \"ar_shoots\""); Run(10);
            Check("Maintenance: no start roll", Pending == null && StartRollOwed);
            foreach (var p in new[] { alice, bob }) Join(p);
            HudActive = () => true; MatchEnd(); HudActive = () => false;
            Check("Maintenance: a match end opens no vote and rolls no dice", Pending == null && Ballot == null);
            Go(alice, 1);
            Check("Maintenance: a player's pick is a vote, and its result still changes the map", Pending?.Map == m[1] && !StartRollOwed);
            Land(); Run(70); MatchEndUntil = default; MatchEnd(); Run(5);
            Check("Maintenance: the next match end changes nothing either", Pending == null && Ballot == null);
            File.Delete(MaintenanceFile); Run(1);
            Check("Flag gone: automatic changes resume; the players' vote settled the start map, so no start roll is left owed", !MaintenanceOn && Pending == null);
            MatchEndUntil = default; MatchEnd();
            Check("...and a match end rolls the dice again (no plugin draws a vote here)", Pending is { Auto: true });

            // ---- !previous ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Talk(alice, "!previous");
            Check("!previous with nothing on record says so", Said("no previous map on record yet") && Pending == null && Ballot == null);
            MatchEnd(); var first = Pending!.Map; Land(); Run(70); // no plugin draws a vote: the dice roll at a match end
            Go(alice, 1); Land(); Run(MapVote.WaitSeconds + 1);
            Talk(bob, "!previous");
            var prevCard = Ballot?.Options.FirstOrDefault(o => o.Source == VoteSource.Picked);
            Check("!previous puts the map before this one to the vote, with its player's vote, like a card's Play",
                Ballot is { Open: true } prevB && prevCard?.Map == first && prevB.Choices["[U:1:1002]"] == prevCard.N);
            VoteFor(alice, prevCard!.N); VoteFor(cara, prevCard.N);
            Check("...and the vote takes the server back there", Pending?.Map == first);
            Land(); Run(MapVote.WaitSeconds + 1); Talk(cara, "!next"); Talk(alice, "!previous");
            Check("During a vote, !previous puts that map on the ballot like any pick", Ballot is { Open: true } pv && pv.Options.FirstOrDefault(o => o.Map == m[1]) is { Source: VoteSource.Picked } pm
                && pv.Choices["[U:1:1001]"] == pm.N);

            // ---- votes ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Talk(alice, "!up");
            Check("!up without a known map asks for a number", Said("Not sure which map this is"));
            Go(alice, 1); Run(ChangeDelay);
            var key = Key(m[1]);
            Talk(alice, "!up");
            Check("A vote while the next map is still loading is refused, not given to a map nobody has seen yet", Said("still loading") && Tally(key) == (0, 0));
            Land();
            Talk(alice, "!up"); Talk(bob, "!down");
            Check("!up and !down like and dislike the map that is on", Tally(key) == (1, 1) && Said("You liked") && Said("You disliked") && NoteFor.ContainsKey(1001) && NoteFor.ContainsKey(1002));
            var markOn = Clip(Label(m[1]), PlainVoteLabel);
            Check("...and chat says what worked in Green: the map an argument, the tally a value, what comes next a command with its values",
                Coloured(SaysNow[^1].Text) == $"δYou disliked α{markOn}δ, now ν+1 -1δ. Why? κ!downδ ν{DownReasons.Words}δ");
            Talk(alice, "!up");
            Check("The same vote again changes nothing and says so: repeating a vote never takes it back", Tally(key) == (1, 1) && Said("You like") && Said("already") && Said("!down takes it back"));
            Check("...which is no refusal and nothing done, so it stays the chat's own colour around its parts", Coloured(SaysNow[^1].Text) == $"You like α{markOn}π already, now ν+1 -1π. κ!downπ takes it back");
            Talk(bob, "!down");
            Check("...for a Dislike as well", Tally(key) == (1, 1) && Said("You dislike") && Said("already"));
            Quiet(); Talk(alice, "!down");
            Check("The opposite vote takes yours back first: Alice's like is gone, nothing is cast, and the note says how to dislike",
                Tally(key) == (0, 1) && !Votes[key].ContainsKey("[U:1:1001]") && Said("Your like of") && Said("is removed - Dislike it again to add a dislike"));
            Quiet(); Talk(alice, "!down");
            Check("...the next Dislike casts it: one vote per player per map", Tally(key) == (0, 2) && Votes[key]["[U:1:1001]"] == -1 && Said("You disliked"));
            Quiet(); Talk(alice, "!up");
            Check("...and back the same way: a Like takes the dislike back", Tally(key) == (0, 1) && Said("Your dislike of") && Said("Like it again to add a like"));
            Quiet(); Talk(alice, "!up");
            Check("...and the next Like casts it", Tally(key) == (1, 1) && Votes[key].Count == 2 && Said("You liked"));
            Talk(cara, $"!up {Rank(2)}"); Talk(bob, $"!up {Rank(2)}"); Talk(alice, $"!up {Rank(2)}"); Talk(cara, $"!up {Rank(3)}"); Talk(bob, $"!up {Rank(3)}");
            Talk(alice, $"!down {Rank(3)}"); Talk(cara, $"!up {Rank(4)}"); Talk(bob, $"!up {Rank(4)}");
            Check("!up <number> votes on any map", Tally(Key(m[2])) == (3, 0) && Tally(Key(m[4])) == (2, 0) && Tally(Key(m[3])) == (2, 1));
            var saved = Votes.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(x => x.Key, x => x.Value));
            LoadVotes();
            Check("Votes persist: votes.json reads back exactly, and no .tmp is left behind",
                Votes.Count == saved.Count && saved.All(kv => Votes[kv.Key].Count == kv.Value.Count && kv.Value.All(x => Votes[kv.Key][x.Key] == x.Value))
                && !File.Exists(VotesPath + ".tmp"));
            var top = TopRows();
            Console.WriteLine("top: " + string.Join(" | ", top.Select(r => $"{Label(r.Map)} +{r.Up} -{r.Down}")));
            Check("The most liked list (the board's live column; the menu's Likes sort shows them all): most likes first, fewer dislikes breaking the tie",
                top[0].Map == m[2] && top[0].Up == 3 && top.FindIndex(r => r.Map == m[4]) is > 0 and var i4 && top.FindIndex(r => r.Map == m[3]) > i4);
            Talk(cara, $"!up {Rank(4)}"); Talk(bob, $"!up {Rank(4)}");
            Check("The same vote again leaves a map's votes as they were", Tally(Key(m[4])) == (2, 0));
            File.WriteAllText(VotesPath, "{ not json");
            LoadVotes();
            Check("An unreadable votes.json is set aside, never overwritten", Votes.Count == 0 && Directory.GetFiles(dir, "votes.json.bad-*").Length == 1);

            // ---- the weights, exactly as the sentence says ----
            Fresh();
            var mid = Maps[Maps.Count / 2];
            double W(int up, int down, MapInfo? on = null)
            {
                var target = on ?? mid;
                Votes = new() { [Key(target)] = Enumerable.Range(0, up).Select(i => ($"[U:1:{9000 + i}]", 1)).Concat(Enumerable.Range(0, down).Select(i => ($"[U:1:{9500 + i}]", -1))).ToDictionary(x => x.Item1, x => x.Item2) };
                return Weight(target);
            }
            var w0 = W(0, 0);
            Check($"One upvote doubles a map's weight ({W(1, 0) / w0:0.###}x), two quadruple it", Math.Abs(W(1, 0) / w0 - 2) < 1e-9 && Math.Abs(W(2, 0) / w0 - 4) < 1e-9);
            Check($"One downvote quarters it ({W(0, 1) / w0:0.###}x): twice as strong as an upvote, the other way", Math.Abs(W(0, 1) / w0 - 0.25) < 1e-9);
            Check("One of each halves it", Math.Abs(W(1, 1) / w0 - 0.5) < 1e-9);
            Check($"Upvotes stop counting at {UpvoteCap} ({W(7, 0) / w0:0}x)", Math.Abs(W(7, 0) / w0 - Math.Pow(2, UpvoteCap)) < 1e-9);
            Votes = new();
            Check($"A map's place on the list is no weight any more: without the Workshop's ratings #1 weighs what the last map (#{Maps.Count}) does - its popularity sets the points (SelfTestBallot.cs)",
                Math.Abs(Weight(Maps[0]) / Weight(Maps[^1]) - 1) < 1e-9 && Math.Abs(Weight(mid) / Weight(Maps[^1]) - 1) < 1e-9);
            var realMaps = Maps;
            Maps = Enumerable.Range(1, 12).Select(i => new MapInfo(i, 5_000_000 + i, $"fixture_{i}", 10)).ToList();
            Votes = new() { [Key(Maps[11])] = new() { ["[U:1:1]"] = 1 }, [Key(Maps[0])] = new() { ["[U:1:1]"] = -1 } };
            var weights = Maps.ToDictionary(x => x, Weight);
            var draws = Enumerable.Range(0, 60000).Select(_ => RandomPick()!).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count() / 60000.0);
            var worst = Maps.Max(x => Math.Abs(draws.GetValueOrDefault(x) / (weights[x] / weights.Values.Sum()) - 1));
            Check($"The dice follow the weights: 60000 rolls over 12 fixtures, every map within 10% of its share (worst {worst:P1})", worst < 0.10);
            S.History = Maps.Take(10).ToList();
            Check("The dice skip the last 10 maps played while anything else is left", Enumerable.Range(0, 500).All(_ => RandomPick()!.Rank > 10));
            S.History = Maps.Take(12).ToList();
            Check("...and fall back to the whole pool when nothing else is", RandomPick() is not null);
            S.History = new();
            Maps = [new MapInfo(1, 6_000_001, "huge_liked", 900), new MapInfo(2, 6_000_002, "huge", 900), new MapInfo(3, 6_000_003, "small", 10)];
            Votes = new() { [Key(Maps[0])] = new() { ["[U:1:1]"] = 1 } };
            var bigRolls = Enumerable.Range(0, 300).Select(_ => RandomPick()!.Name).ToHashSet();
            Check("A map's download size is no reason either way: a 900 MB map nobody liked is rolled like a 10 MB one",
                bigRolls.Contains("huge_liked") && bigRolls.Contains("huge") && bigRolls.Contains("small"));
            Maps = realMaps; Votes = new();

            // ---- the guide on a real join, never on a map change's reconnect; nothing broadcast to greet a newcomer ----
            Fresh();
            guideReply = $"{Guide.Shown} to Alice (first time)";
            Join(alice);
            Check("A first-time player: the guide goes up over RCON, marked first, and chat gets nothing at all (no more five-line help)",
                sent.Contains($"{Guide.Command} 1001 first") && !sent.Any(c => c.StartsWith("say ")));
            Quiet(); Leave(alice, "NETWORK_DISCONNECT_SHUTDOWN"); Join(alice);
            Check("A map change's reconnect shows nothing", !sent.Any(c => c.StartsWith(Guide.Command)) && !sent.Any(c => c.StartsWith("say ")));
            Leave(alice); Run(120); Quiet(); Join(alice);
            Check("A returning player who really left and came back gets the guide again, not marked first, and still no chat",
                sent.Contains($"{Guide.Command} 1001") && !sent.Any(c => c.StartsWith("say ")));
            guideReply = "Unknown command 'css_guide'";
            Quiet(); Join(bob);
            Check("No plugin to show it: nothing goes to chat for a first-time player either - the menu is the interface",
                !sent.Any(c => c.StartsWith("say ")) && Told.Count == 0);
            guideReply = $"{Guide.Shown} to Cara";
            void JoinViaAddon((string Name, string Id) p) // MultiAddonManager makes a joining client reconnect to fetch the HUD addon
            {
                Line($"\"{p.Name}<2><{p.Id}><>\" connected, address \"192.0.2.1:27005\"");
                Leave(p, "NETWORK_DISCONNECT_LOOPSHUTDOWN");
                Join(p);
            }
            Quiet(); JoinViaAddon(cara);
            Check("A first join with a reconnect in between (the HUD addon download) gets the guide once", sent.Count(c => c.StartsWith(Guide.Command)) == 1 && sent.Contains($"{Guide.Command} 1003 first"));
            Leave(cara); Run(120); Quiet(); JoinViaAddon(cara);
            Check("...and so does a returning player's: the reconnect does not swallow the real join", sent.Count(c => c.StartsWith(Guide.Command)) == 1 && sent.Contains($"{Guide.Command} 1003"));

            // ---- no chat at all: the pick is on the news line, the HUD's banner counts it down, its card says what is on ----
            Fresh();
            foreach (var p in new[] { alice, bob }) Join(p);
            HudActive = () => true;
            Quiet();
            Go(alice, 1);
            Check("The pick and its vote are on the news line, the result with the countdown's length", Told.Any(t => t.Contains("Alice put")) && NewsNow.Text.Contains("Everybody has voted") && NewsNow.Text.Contains("Changing in"));
            Land(); Run(20);
            Check("The map changes, and nothing at all went to chat: no countdown, no after-load line", sent.Contains($"host_workshop_map {m[1].Id}") && !sent.Any(c => c.StartsWith("say ")));
            HudActive = () => false;
            Quiet(); Go(bob, 2); Land(); Run(20);
            Check("...and the same without the HUD's plugin polling: chat stays quiet", !sent.Any(c => c.StartsWith("say ")) && sent.Contains($"host_workshop_map {m[2].Id}"));

            // ---- the HUD's live state, as the plugin reads it on /hud, and clicks coming back on /act ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Go(alice, 1); Land(); Talk(bob, "!up"); Run(1);
            var hud = LiveHud();
            var hudVote = LiveVote();
            Console.WriteLine("hud: " + Published().Replace('\n', '|').Replace('\t', ' '));
            Check("The HUD state names the map, what picked it, its likes and who liked what, and the most liked maps",
                hud.Map == Clip(Label(m[1]), 40) && hud.By == "the vote" && (hud.Up, hud.Down) == (1, 0) && hud.MyVotes.GetValueOrDefault(1002) == 1 && hud.Maps == Playable
                && hud.Top is [var liked] && liked.Up == 1 && liked.Map == Clip(Label(m[1]), 36));
            Check("...and, the vote just over, the wait before the next one: the window's button says when, and a click gets its answer", hudVote.WaitFor > 0 && HudView.Action(hud, hudVote).Command == "!next");
            Quiet();
            Check("A click on the HUD acts exactly like the chat command: Cara's like is counted and said",
                HudAct("[U:1:1003]\tCara\t!up") && Tally(Key(m[1])) == (2, 0) && Said("You liked") && NoteFor.ContainsKey(1003));
            Check("Only the menu's own commands pass: no !rcon, no map by free-form name, no malformed sender, none of the queue's old words",
                !HudAct("[U:1:1003]\tCara\t!rcon quit") && !HudAct("[U:1:1003]\tCara\t!map aim") && !HudAct("[U:1:1003]\tCara\t!map 5; quit")
                && !HudAct("[U:1:1003]\tCara\t!up 12 x") && !HudAct("[U:1:1003]\tCara\t!time 30") && !HudAct("[U:1:1003]\tCara\t!cancel 5") && !HudAct("[U:1:1003]\tCara\t!done")
                && !HudAct("1003\tCara\t!up") && !HudAct("[U:1:1003]\t!up") && !HudAct("") && Pending == null && Tally(Key(m[1])) == (2, 0));
            Run(1); // the next tick rebuilds the catalog, the likes having moved
            hud = LiveHud();
            Check("The match length and the catalog's version ride along", hud.RoundMinutes == S.RoundMinutes && hud.CatalogVersion == CatalogVersion);
            var catalog = HudCatalog.Decode(CatalogText)!;
            Check($"The menu's catalog: every listed and official map of every mode once ({catalog.Maps.Count}), with its likes and whose they are",
                catalog.Maps.Count == Maps.Where(x => !IsPack(x)).Concat(Officials).Concat(Modes.All.Where(x => !x.ArmsRace).SelectMany(PoolOf).Where(x => !IsPack(x) && !IsBlocked(x))).Select(Key).Distinct().Count()
                && catalog.Maps.Select(x => x.Key).Distinct().Count() == catalog.Maps.Count
                && catalog.Maps.First(x => x.Key == Key(m[1])) is { Up: 2, Down: 0 } && catalog.MyVote(1003, Key(m[1])) == 1 && catalog.MyVote(1001, Key(m[1])) == 0
                && catalog.Maps.Where(x => x.Official).All(x => x.Arg == x.Name && Hud.IsAction("!map " + x.Arg))
                && catalog.Maps.Where(x => !x.Official).All(x => x.Arg == x.Rank.ToString() && Hud.IsAction("!up " + x.Arg)));
            var version = CatalogVersion;
            Check("A menu click on a card acts like the chat command: Cara's dislike on map 4 by its rank", HudAct($"[U:1:1003]\tCara\t!down {Rank(4)}")
                && Tally(Key(m[4])) == (0, 1) && CatalogVersion > version);
            Run(1);
            Check("...and the catalog the plugin fetches next shows it", HudCatalog.Decode(CatalogText)!.MyVote(1003, Key(m[4])) == -1 && LiveHud().CatalogVersion == CatalogVersion);
            CatalogVersion++; // what a map start, a vote opening or a reload does, between two ticks
            var named = HudState.Decode(Published())!.CatalogVersion;
            var listHad = HudCatalog.Decode(CatalogText)!.Version;
            AfterWork(); // what the event loop does after every piece of work
            Check("The menu's map list is never behind the state that names its version: after every piece of work the list is made again first, then the state goes out",
                named == CatalogVersion && listHad < named && HudCatalog.Decode(CatalogText)!.Version == CatalogVersion && LiveHud().CatalogVersion == CatalogVersion);
            Check("!time by click sets the match length", HudAct("[U:1:1003]\tCara\t!time 10") && S.RoundMinutes == 10 && HudAct("[U:1:1003]\tCara\t!time 60") && S.RoundMinutes == 60);
            Run(MapVote.WaitSeconds); Go(bob, 2); Run(2);
            hud = LiveHud();
            Check($"A change counting down: the banner gets its map, what picked it and the seconds left ({hud.ChangeIn}), and nobody gets a button for it",
                hud.ChangeMap == Clip(Label(m[2]), 40) && hud.ChangeBy == "the vote" && hud.ChangeIn is > 0 and < 8 && HudView.Action(hud, LiveVote()).Command == null);
            Check("The state's text round-trips through the codec unchanged", HudState.Decode(hud.Encode())!.Encode() == hud.Encode());
            Land(); Run(2);
            hud = LiveHud();
            Check("Once it has loaded: its map is on, the change is over", hud.Map == Clip(Label(m[2]), 40) && hud.ChangeIn == -1 && !hud.Loading);

            // ---- maps gone from the Workshop (Removed.cs): refused, never rolled, off an open ballot, unblocked when they come back ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            var goneMap = m[3];
            var goneId = goneMap.Id.ToString();
            Checked = new() { Verdicts = new() { [goneId] = Workshop.Removed } }; RecomputeRemoved();
            Quiet();
            Talk(alice, $"!map {Rank(3)}");
            Check("A map gone from the Workshop is refused by number, with what happened and what to do, and no vote opens",
                Said($"{Clip(Label(goneMap), 44)} was removed from the Workshop - Nobody can download it. Pick another.") && Pending == null && Ballot == null);
            Quiet();
            Talk(bob, $"!map {goneMap.Id}");
            Check("...by its workshop id too", Said("was removed from the Workshop - Nobody can download it. Pick another.") && Pending == null && Ballot == null);
            Check("...and the refusal fits one chat line for every map on the list",
                Maps.All(x => { Removed = new() { [x.Id.ToString()] = Workshop.Removed }; return M.Plain(RemovedRefusal(x)).Length <= 120; }));
            RecomputeRemoved();
            var dicePool = Maps.Where(x => x.Rank > 0).Take(400).ToList();
            Checked = new() { Verdicts = dicePool.Skip(3).Take(390).ToDictionary(x => x.Id.ToString(), _ => Workshop.Removed) }; RecomputeRemoved();
            var goneRolls = Enumerable.Range(0, 2000).Select(_ => RandomPick()).Where(x => x != null).Select(x => x!).ToList();
            Check($"The dice never roll a map gone from the Workshop ({Removed.Count} of them, 2,000 rolls)", goneRolls.Count > 0 && goneRolls.All(x => !IsRemoved(x)));
            Checked = new() { Verdicts = new() { [goneId] = Workshop.Removed } }; RecomputeRemoved();

            // the sync's report (the one Workshop checker): a map coming back, a map on an open ballot going, the map that is on going
            var answer = ApplyRemovedReport(RemovedBody([(goneMap.Id, Workshop.Ok)]));
            Check("A map the sync finds downloadable again is unblocked, and the report says so",
                !IsRemoved(goneMap) && answer.Contains("1 unblocked") && File.Exists(Path.Combine(StateDir, "removed.json")));
            Quiet();
            Go(alice, 3);
            Check("...and can be picked again", Pending?.Map == goneMap);
            Land(); Run(MapVote.WaitSeconds + 1);
            Talk(bob, $"!map {Rank(5)}"); Talk(cara, $"!map {Rank(6)}");
            Check("(Bob's and Cara's picks are on the open ballot)", Ballot is { Open: true } ob && ob.Options.Any(o => o.Map == m[5]) && ob.Options.Any(o => o.Map == m[6]));
            Quiet();
            answer = ApplyRemovedReport(RemovedBody([(m[5].Id, Workshop.Removed), (m[6].Id, Workshop.Ok)]));
            var ob2 = Ballot;
            Check("A map on an open ballot that the sync finds gone leaves it, the votes on it with it (one playable check)",
                ob2 is { Open: true } && ob2.Options.All(o => o.Map != m[5]) && ob2.Options.Any(o => o.Map == m[6]) && !ob2.Choices.ContainsKey("[U:1:1002]") && answer.Contains("1 newly blocked"));
            Check("...and Checked keeps the verdict, so a restarted bridge still knows", Checked.Verdicts.GetValueOrDefault(m[5].Id.ToString()) == Workshop.Removed);
            var sixN = ob2!.Options.First(o => o.Map == m[6]).N;
            VoteFor(alice, sixN); VoteFor(bob, sixN);
            var landing = Pending?.Map;
            Land(); Run(30); Quiet();
            answer = ApplyRemovedReport(RemovedBody([(landing!.Id, Workshop.Hidden)]));
            Check("The map that is ON going private makes way at once: the dice roll another, and the news line says why",
                landing == m[6] && Pending is { Auto: true } rolled && rolled.Map != landing && !IsRemoved(rolled.Map) && Said("is no longer public on the Workshop - Nobody new can download it - Rolling the dice"));
            Land(); Run(30);
            var before = Removed.Count;
            answer = ApplyRemovedReport(RemovedBody(Maps.Take(200).Select((x, i) => (x.Id, i < 30 ? Workshop.Removed : Workshop.Ok))));
            Check("A report calling 30 of 200 maps gone (more than 10%) is not believed: nothing changes",
                answer.StartsWith("ignored") && Removed.Count == before);
            Check("An empty or garbled report changes nothing either", ApplyRemovedReport("not a report").StartsWith("ignored") && Removed.Count == before);
            Checked = new() { Verdicts = new() { [goneId] = Workshop.Removed } }; RecomputeRemoved();
            S.History.Add(goneMap);
            Talk(cara, "!previous");
            Check("!previous refuses a map gone from the Workshop", Said("was removed from the Workshop - Nobody can download it"));
            Checked = new(); RecomputeRemoved();

            Check("The report survives its codec, and keeps only well-formed lines",
                DecodeRemovedReport(RemovedBody([(5, Workshop.Ok), (7, Workshop.Banned)]) + "x\tok\n9\tmaybe\n-1\tok\n") is [(5, "ok"), (7, "banned")]);

            // ---- blocked for good, with its own reason (Blocked.cs): glassbox kicked every player off the server on 2026-10-20 ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Blocked = new() { ["3076804331"] = ("glassbox", "glassboxfinal", "kicked every player off the server (2026-10-20)") };
            var glassLine = "glassbox (3076804331) kicked every player off the server (2026-10-20) - It is blocked.";
            Talk(alice, "!map 3076804331");
            Check("A blocked map is refused by id, in its own words, and no vote opens", Said(glassLine) && Pending == null && Ballot == null);
            Check("...in LightRed in chat, the map an argument, while the window's line and the board have the words alone",
                Coloured(SaysNow[^1].Text) == "ραglassboxρ (3076804331) kicked every player off the server (2026-10-20) - It is blocked." && Told[^1] == glassLine);
            Quiet(); Talk(bob, "!map https://steamcommunity.com/sharedfiles/filedetails/?id=3076804331");
            Check("...by a pasted Workshop URL", Said(glassLine) && Pending == null);
            Quiet(); Talk(cara, "!map glassboxfinal");
            Check("...a name is no click's word (the plugin resolves names): the plain answer", Said("No map is 'glassboxfinal'") && Pending == null);
            Quiet(); S.History.Add(new MapInfo(0, 3076804331, "workshop 3076804331", 0)); Talk(alice, "!previous");
            Check("...and from !previous", Said(glassLine) && Pending == null);
            var listedBlock = m[4];
            Blocked[listedBlock.Id.ToString()] = (listedBlock.Name, "", "froze the server");
            Check("A blocked map on the list: never rolled, and left out of the HUD's map browser",
                Enumerable.Range(0, 800).Select(_ => RandomPick()).All(x => x == null || !IsBlocked(x)) && BuildCatalog().Maps.All(x => x.Id != listedBlock.Id));
            Check("The refusal fits one chat line whatever the name and reason",
                M.Plain(BlockedRefusal(new MapInfo(0, 3076804331, "x", 0))) is var r0 && r0.Length <= 120
                && (Blocked["3076804331"] = (new string('n', 90), "", new string('r', 200))) != default && M.Plain(BlockedRefusal(new MapInfo(0, 3076804331, "x", 0))).Length <= 120);
            Check("The list reads objects and bare ids; a missing reason still blocks",
                ParseBlocked("""{"blocked":[{"id":5,"name":"a","map":"b","reason":"c"},6,{"name":"no id"},{"id":7}]}""") is { Count: 3 } pb
                && pb["5"] == ("a", "b", "c") && pb["6"].Item3 == "is on the blocked list" && pb["7"].Item1 == "workshop 7" && ParseBlocked("{}").Count == 0);
            Check("blocked.json: a plain list reads like the object form; anything else is no list",
                ParseBlocked("""[{"id":5,"name":"a","map":"b","reason":"c"},6]""") is { Count: 2 } plain && plain["5"] == ("a", "b", "c") && plain.ContainsKey("6")
                && ParseBlocked("\"x\"").Count == 0 && ParseBlocked("""{"blocked":{"id":5}}""").Count == 0);
            if (File.Exists("blocked.json"))
                Check("glassbox is on the repo's blocked.json, with its reason",
                    ParseBlocked(File.ReadAllText("blocked.json")).TryGetValue("3076804331", out var gb) && gb == ("glassbox", "glassboxfinal", "kicked every player off the server (2026-10-20)"));
            (BlockedInBroken, BlockedOwn) = (new() { ["5"] = ("old", "", "broken.json's") }, new() { ["5"] = ("new", "", "blocked.json's"), ["6"] = ("six", "", "x") });
            Check("Both lists count, blocked.json's entry first where both name a map", MergeBlocked() is { Count: 2 } both && both["5"].Name == "new" && both["6"].Name == "six");
            (BlockedInBroken, BlockedOwn) = (new(), new());
            Blocked = new();

            // the HUD's map browser: a removed map is shown, as unavailable
            var goneCard = new HudCatalog.Entry(goneId, ArgOf(goneMap), goneMap.Rank, goneMap.Id, goneMap.Name, goneMap.SizeMb, false, -1, 0, 0, 0, true);
            var goneCat = HudCatalog.Decode(new HudCatalog { Version = 1, Maps = [goneCard] }.Encode())!;
            var goneView = HudMenu.Maps(goneCat, new HudState(), new HudBrowser(), 1001, clock);
            Check("The HUD's map card shows a removed map as unavailable: the warning, the button, the red strip - and not as risky",
                goneCat.Maps is [{ Removed: true, Risky: false }] && goneView.Texts.Contains(("gg_m1_warn", "Removed from the Workshop"))
                && goneView.Texts.Contains(("gg_m1_play_text", "Unavailable")) && goneView.Classes.Contains(("gg_m1", "broken", true)));
            var goneBrowser = new HudBrowser();
            Check("...its Play sends the !map the bridge refuses (saying why), without arming a confirmation",
                HudMenu.Command("gg_m1_play", goneCat, new HudState(), goneBrowser, 1001, clock) == $"!map {goneCard.Arg}" && goneBrowser.ConfirmKey == "");
            Check("...and an older bridge's twelve-column line reads as not removed",
                HudCatalog.Decode("gg-catalog\t1\nm\tk\t5\t5\t123\tname\t10\t0\t-1\t0\t0\t0\n")!.Maps is [{ Removed: false }]);
            Checked = new() { Verdicts = new() { [goneId] = Workshop.Removed } }; RecomputeRemoved();
            Check("The bridge's catalog marks it", BuildCatalog().Maps.FirstOrDefault(x => x.Id == goneMap.Id) is { Removed: true });
            Checked = new(); RecomputeRemoved();

            // ---- the data files are read again while the bridge runs (Reload.cs): a new maps.json or broken.json needs no restart ----
            Fresh();
            foreach (var p in new[] { alice, bob }) Join(p);
            var keepMaps = Maps;
            var mapsPath = Path.Combine(StateDir, "maps-reload.json");
            var brokenPath = Path.Combine(StateDir, "broken-reload.json");
            var grown = keepMaps.Append(new MapInfo(keepMaps.Max(x => x.Rank) + 1, 3076999999, "glass_test", 50)).ToList();
            File.WriteAllText(mapsPath, JsonSerializer.Serialize(grown, Ctx.Default.ListMapInfo));
            var reloadVersion = CatalogVersion;
            var reloaded = ReloadMaps(mapsPath, force: false);
            Check("A new maps.json is taken while the bridge runs, the HUD's menu refetches, and the log says what changed",
                Maps.Count == keepMaps.Count + 1 && CatalogVersion != reloadVersion && reloaded.Contains("1 new, 0 gone"));
            GoTo(alice, Maps.Single(x => x.Id == 3076999999));
            Check("...its new map can be picked at once", Pending?.Map.Id == 3076999999);
            File.WriteAllText(mapsPath, "[{\"rank\":1,");
            Check("A maps.json that does not parse changes nothing", ReloadMaps(mapsPath, force: false).Contains("unreadable") && Maps.Count == keepMaps.Count + 1);
            File.WriteAllText(mapsPath, JsonSerializer.Serialize(keepMaps.Take(10).ToList(), Ctx.Default.ListMapInfo));
            Check("...nor one that would cut the list by more than half", ReloadMaps(mapsPath, force: false).Contains("less than half") && Maps.Count == keepMaps.Count + 1);
            Check("...unless forced (POST /reload/<secret>?force)", ReloadMaps(mapsPath, force: true).Contains("10 maps") && Maps.Count == 10);
            Maps = keepMaps;

            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Go(alice, 1); Land(); Run(MapVote.WaitSeconds + 1);
            Talk(bob, $"!map {Rank(5)}"); Talk(cara, $"!map {Rank(6)}");
            Check("(Bob's and Cara's picks are on the open ballot)", Ballot is { Open: true } rb && rb.Options.Any(o => o.Map == m[5]) && rb.Options.Any(o => o.Map == m[6]));
            File.WriteAllText(brokenPath, $$"""{"maps":[{"id":{{m[2].Id}},"crashes":2}],"packs":[{"id":{{m[5].Id}},"name":"a skin pack"}],"blocked":[{"id":{{m[6].Id}},"name":"{{m[6].Name}}","reason":"froze the server"}]}""");
            Quiet(); reloaded = ReloadBroken(brokenPath); Run(1);
            Check("A new broken.json is taken while the bridge runs: its crashers, non-maps and blocked maps, all at once",
                Broken.ContainsKey(m[2].Id.ToString()) && IsPack(m[5]) && IsBlocked(m[6]) && reloaded.Contains("2 newly unplayable, 0 playable again"));
            Check("...the cards of the maps that just became unplayable - a non-map too (packs re-checked) - leave the open ballot",
                Ballot is { Open: true } rb2 && rb2.Options.All(o => o.Map != m[5] && o.Map != m[6]));
            Quiet(); Talk(bob, $"!map {Rank(6)}");
            Check("...and a map it blocks is refused at once, in its own words", Said("froze the server - It is blocked."));
            File.WriteAllText(brokenPath, "{\"maps\":[");
            Check("A broken.json that does not parse changes nothing", ReloadBroken(brokenPath).Contains("unreadable") && IsBlocked(m[6]) && IsPack(m[5]));

            // the watch: a file that changed and then held still for one look is read again, once; the bridge's own write is not read back
            var reads = 0;
            File.WriteAllText(mapsPath, JsonSerializer.Serialize(keepMaps, Ctx.Default.ListMapInfo));
            WatchStart(new List<(string, Func<string>)> { (mapsPath, () => { reads++; return ReloadMaps(mapsPath, false); }) });
            File.WriteAllText(mapsPath, JsonSerializer.Serialize(grown, Ctx.Default.ListMapInfo));
            WatchFiles();
            Check("The watch does not read a file it has just seen change (a copy may still be under way)...", reads == 0);
            WatchFiles();
            Check("...it reads it once the file has held still for one more look...", reads == 1 && Maps.Count == keepMaps.Count + 1);
            WatchFiles(); WatchFiles();
            Check("...and only once", reads == 1);
            Maps = keepMaps;
            var removedReads = 0;
            WatchStart(new List<(string, Func<string>)> { (RemovedPath, () => { removedReads++; return ReloadRemoved(); }) });
            ApplyRemovedReport(RemovedBody([(m[3].Id, Workshop.Removed)])); WatchFiles(); WatchFiles();
            Check("The bridge's own write of removed.json is not read back", removedReads == 0 && IsRemoved(m[3]));
            File.WriteAllText(RemovedPath, JsonSerializer.Serialize(new RemovedFile { Checked = "2026-10-20T00:00:00Z" }, Ctx.Default.RemovedFile));
            File.SetLastWriteTimeUtc(RemovedPath, DateTime.UtcNow.AddMinutes(5));
            WatchFiles(); WatchFiles();
            Check("...but an edit by hand is, and takes effect", removedReads == 1 && !IsRemoved(m[3]));
            WatchStop();
            var asked = DataFiles(force: true).Select(f => f.Item1).ToList();
            Check("POST /reload reads every data file the watch reads - search.json among them (it was once marked as read without being read)",
                asked.SequenceEqual(DataFiles().Select(f => f.Item1)) && asked.Contains(SearchFile) && asked.Contains(MapsFile) && asked.Contains(OursFile));
            var (fileA, fileB) = (Path.Combine(StateDir, "reload-a.txt"), Path.Combine(StateDir, "reload-b.txt"));
            File.WriteAllText(fileA, "a"); File.WriteAllText(fileB, "b");
            var (readsA, readsB) = (0, 0);
            Func<string> readA = () => { readsA++; return "a read"; }, readB = () => { readsB++; return "b read"; };
            WatchStart(new List<(string, Func<string>)> { (fileA, readA), (fileB, readB) });
            File.WriteAllText(fileA, "a2"); File.WriteAllText(fileB, "b2");
            File.SetLastWriteTimeUtc(fileA, DateTime.UtcNow.AddMinutes(1)); File.SetLastWriteTimeUtc(fileB, DateTime.UtcNow.AddMinutes(1));
            ReloadFiles(new List<(string, Func<string>)> { (fileA, readA) });
            WatchFiles(); WatchFiles();
            Check("...and marks as read only what it read: a changed file it did not read is still read by the watch, the one it read is not read twice",
                readsA == 1 && readsB == 1);
            WatchStop();
            Check("GET /health says what the bridge has loaded", HealthText() is var health && health.StartsWith("gg-health\n")
                && health.Contains($"maps {Maps.Count} + {Officials.Count} official") && health.Contains("crash watch: spawn") && health.Contains("search: "));
            Broken = new(); Packs = new(); Checked = new(); Removed = new(); Blocked = new();

            SelectionSelfTest(Check, Fresh, p => Join(p), (p, reason) => Leave(p, reason), Talk, Run, Said, Quiet, sent); // SelfTestSelection.cs

            // ---- the welcome panel, measured, and its timing as the plugin drives it ----
            var t0 = clock;

            // ---- the HUD on screen, per player, on a handed clock (HudSlots; the plugin only sends what Frame changes) ----
            HudSlots.View At(HudSlots hs, int slot, double seconds) => hs.Frame(t0.AddSeconds(seconds)).FirstOrDefault(f => f.Slot == slot).View;
            var win = new HudSlots();
            win.Joined(1, first: true, alive: true, t0);
            Check("A player who joins, alive: the window opens after the spawn delay, on its start page, and takes the mouse",
                !At(win, 1, 1).Window && At(win, 1, 1.6) is { Window: true, Page: HudSlots.Page.Start, Capture: true, Toast: 0, Returning: false });
            Check($"...and stays until its X: no idle close after {HudSlots.IdleCloseSeconds} s, no drain bar, and its foot says nothing about idling (pinned)", At(win, 1, 1.6 + HudSlots.IdleCloseSeconds * 5) is { Capture: true, Draining: false, Pinned: true });
            win.Click(1, "gg_tab_how", t0.AddSeconds(10));
            Check("A tab switches the page and it still stays", At(win, 1, 200) is { Page: HudSlots.Page.How, Capture: true, Draining: false });
            win.Spawned(1, t0.AddSeconds(201));
            Check("A death and a respawn never close it", At(win, 1, 202) is { Window: true, Capture: true });
            win.Click(1, HudView.ButtonClose, t0.AddSeconds(203));
            Check("Its X closes it: the mouse is let go at once, the window fades out", At(win, 1, 203.1) is { Capture: false, WindowIn: false, Window: true } && !At(win, 1, 203.5).Window);
            var idle = new HudSlots();
            idle.Show(1, HudSlots.Page.Fun, t0);
            Check("The menu, opened any other way: its drain bar starts a moment later, and it is not pinned", !At(idle, 1, HudSlots.DrainAfterOpenSeconds - 0.1).Draining && At(idle, 1, HudSlots.DrainAfterOpenSeconds + 0.1) is { Draining: true, Pinned: false });
            idle.Click(1, "gg_tab_how", t0.AddSeconds(10));
            Check("A tab restarts the drain bar and keeps the window up", At(idle, 1, 10.1) is { Page: HudSlots.Page.How, Draining: false } && At(idle, 1, 10.4).Draining);
            Check($"Left alone {HudSlots.IdleCloseSeconds} s after the last click it closes itself: the mouse is let go at once, the window fades out",
                At(idle, 1, 10 + HudSlots.IdleCloseSeconds - 0.1).Capture && At(idle, 1, 10 + HudSlots.IdleCloseSeconds + 0.1) is { Capture: false, WindowIn: false, Window: true }
                && !At(idle, 1, 10 + HudSlots.IdleCloseSeconds + 0.5).Window);
            idle.Show(1, HudSlots.Page.Start, t0.AddSeconds(60));
            Check("Opened again (TAB + E, a pasted link), on its page, visible first and fully in a moment later (Panorama cannot fade what turns visible)",
                At(idle, 1, 60.1) is { Window: true, WindowIn: false, Page: HudSlots.Page.Start } && At(idle, 1, 60.4).WindowIn);
            idle.Click(1, HudView.ButtonClose, t0.AddSeconds(61));
            Check("The close button closes it", At(idle, 1, 61.1) is { Capture: false, WindowIn: false } && !At(idle, 1, 61.5).Window);
            idle.Show(1, HudSlots.Page.Start, t0.AddSeconds(62)); idle.Close(1, t0.AddSeconds(63));
            Check("css_gghud release closes it too", !At(idle, 1, 63.5).Window && idle.Idle);
            idle.Click(1, HudView.ButtonUp, t0.AddSeconds(64));
            Check("A click from a player without the window open does nothing", !At(idle, 1, 64.1).Window);
            idle.Show(1, HudSlots.Page.Start, t0.AddSeconds(70));
            var reading = At(idle, 1, 70.5).Capture;
            Check("Shot while reading: damage never closes the window (there is no close on a hit; only the vote's freeze protects anybody)",
                reading && typeof(HudSlots).GetMethod("Hurt") == null && At(idle, 1, 75.1).Capture && At(idle, 1, 75.5).Window);
            var pinnedVote = new HudSlots();
            pinnedVote.Joined(5, first: true, alive: true, t0);
            pinnedVote.Show(5, HudSlots.Page.Maps, t0.AddSeconds(3));
            pinnedVote.VoteOver(5, t0.AddSeconds(30));
            var openedByVote = new HudSlots();
            openedByVote.Show(6, HudSlots.Page.Maps, t0);
            openedByVote.VoteOver(6, t0.AddSeconds(1));
            Check("A vote's end: the welcome goes back to its start page and waits for its X, any other window closes",
                At(pinnedVote, 5, 31) is { Window: true, Page: HudSlots.Page.Start, Capture: true } && At(openedByVote, 6, 1.5).Capture == false);
            var back = new HudSlots();
            back.Joined(2, first: false, alive: false, t0);
            Check("A returning player not yet spawned: nothing until they do...", !At(back, 2, 5).Window);
            back.Spawned(2, t0.AddSeconds(6));
            Check("...then the same window, saying welcome back; never a card", At(back, 2, 7.6) is { Window: true, Returning: true, Toast: 0 });
            back.Click(2, HudView.ButtonClose, t0.AddSeconds(8));
            back.Spawned(2, t0.AddSeconds(20));
            Check("...and their respawns show nothing", At(back, 2, 21.6) is { Toast: 0, Window: false });
            var maps = new HudSlots();
            maps.Spawned(3, t0);
            Check("Everybody's first spawn on a map gets the now-playing card", At(maps, 3, 1.6).Toast == 1);
            maps.MapStarted(); maps.Spawned(3, t0.AddSeconds(40));
            Check("...again on the next map", At(maps, 3, 41.6).Toast == 1);
            maps.Joined(3, first: true, alive: true, t0.AddSeconds(42));
            Check("A css_guide that lands after the spawn swaps the card for the window", At(maps, 3, 43.6) is { Window: true, Toast: 0 });
            var never = new HudSlots();
            never.Joined(4, first: true, alive: false, t0);
            Check($"A newcomer who never spawns gets the window after {HudSlots.WaitLimitSeconds} s anyway", !At(never, 4, 19).Window && At(never, 4, 20.1).Window);
            never.Gone(4);
            Check("A player who leaves is forgotten", never.Idle && never.Frame(t0.AddSeconds(30)).Count == 0);
        }
        finally
        {
            (Send, After, Now, StateDir, HudActive) = (realSend, realAfter, realNow, realStateDir, realHud);
            S = new State(); Votes = new(); Players.Clear(); Ballot = null; VoteWaitUntil = default; Pending = null; AnnouncePending = false;
            MaintenanceOn = false; WatchStop(); ResetCrashWatch();
            try { Directory.Delete(dir, true); } catch (Exception e) { Console.WriteLine($"selftest: could not remove {dir}: {e.Message}"); }
        }
    }
}
