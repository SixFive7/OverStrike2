// --selftest, part two: requests and the fair queue, votes, the weighted dice, !previous, the start roll, the maintenance
// flag and the on-screen guide. Everything runs on a handed clock with a fake timer wheel, and every RCON command is recorded,
// so a whole evening of play - joins, map loads, match ends, departures - replays in milliseconds and the same way every time.
using System.Text.Json;
using System.Text.RegularExpressions;

static partial class Program
{
    static void QueueSelfTest(Action<string, bool> Check)
    {
        var (realSend, realAfter, realNow, realStateDir, realHud) = (Send, After, Now, StateDir, HudActive);
        HudActive = () => false; // the HUD plugin is not polling unless a test says so
        string dir;
        try { dir = Path.Combine(AppContext.BaseDirectory, $"selftest-state-{Environment.ProcessId}"); Directory.CreateDirectory(dir); }
        catch (Exception) { dir = Path.Combine(Path.GetTempPath(), $"gungamebridge-selftest-{Environment.ProcessId}"); Directory.CreateDirectory(dir); } // the image's /app is not writable
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
                    Tick();
                }
            }
            void Line(string ev) // a log line; a "Loading map" one is also the fake server loading that map
            {
                if (ev.StartsWith("Loading map")) { statusSpawn++; if (statusMap != null && ev.Split('"') is [_, var loaded, ..]) statusMap = loaded; }
                Ingest($"{clock:MM/dd/yyyy - HH:mm:ss}.000 - {ev}", null);
            }
            // A player saying something in chat (presence only, since 2026-10-21), and then doing it - what a click in the menu asks the
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
                S = new State(); Broken = new(); Packs = new(); Votes = new(); ClearPickConfirm();
                RemovedSeed = new(); Checked = new(); Removed = new(); Blocked = new();
                Players.Clear(); Queue.Clear(); Owners.Clear(); Airtime.Clear(); VClock = 0; AskSeq = 0;
                Pending = null; AnnouncePending = false; CooldownUntil = default; AnnounceUntil = default; AnnounceDeadline = default; MatchEndUntil = default;
                StartRollOwed = false; StartMapAt = default; StartRollRetryAt = default; LastNamedMapStart = default; rconDown = false; HoldUntil = default; EmptySince = null; NewsItems.Clear(); NewsSent = default; NewsFirst = default; NewsLast = default;
                statusMap = null; statusSpawn = 1; ResetCrashWatch(); WatchStop(); ClearNotices(); RestartConfirmBy = ""; RestartConfirmUntil = default; RestartCooldownUntil = default; StartRollNeedsProof = false;
                Sizes = new(); SizeBounds = (0, 0, 0); NobodySince = null; ResetDone = false; ResetProbeAt = default; // Selection.cs
                timers.Clear(); Quiet();
                if (File.Exists(MaintenanceFile)) File.Delete(MaintenanceFile);
                InMaintenance();
                clock = clock.AddHours(1);
            }
            List<MapInfo> Upcoming() => Order(clock).Select(r => r.Map).ToList();
            bool Said(string fragment) => sent.Any(c => c.StartsWith("say ") && c.Contains(fragment)) || Told.Any(t => t.Contains(fragment)); // a note or the news line (Notices.cs)
            void Quiet() { sent.Clear(); ClearNotices(); }
            var small = Maps.Where(m => m.SizeMb < 400 && m.Rank > 0).ToList(); // small maps: their announcements stay short
            var m = Enumerable.Range(0, 12).Select(i => small[20 + i * 7]).ToList(); // twelve ordinary, distinct, rollable maps
            var (alice, bob, cara, dan) = (("Alice", "[U:1:1001]"), ("Bob", "[U:1:1002]"), ("Cara", "[U:1:1003]"), ("Dan", "[U:1:1004]"));
            string Rank(int i) => m[i].Rank.ToString();

            // ---- requests on a random map, and one player asking five times against two asking once ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Talk(alice, $"!map {Rank(1)}");
            Check("A request on a random map starts at once (the 8 s countdown), owned by its requester",
                Pending is { Auto: false } p1 && p1.Map == m[1] && p1.Owners is [{ Name: "Alice" }] && Said("Alice picked") && Said("changing in"));
            foreach (var i in new[] { 2, 3, 4, 5 }) Talk(alice, $"!map {Rank(i)}");
            Check("While it counts down, her next four wait in the queue instead of cutting it short", Pending?.Map == m[1] && Queue.Count == 4);
            Land(); Run(30);
            Talk(bob, $"!map {Rank(6)}"); Talk(cara, $"!map {Rank(7)}");
            Check("Fairness: Bob's and Cara's single requests go ahead of Alice's remaining four, in the order they asked",
                Upcoming().SequenceEqual([m[6], m[7], m[2], m[3], m[4], m[5]]));
            Check("Alice's map is not replaced by their requests: it is hers until the match ends or !done", Pending == null && Holders(clock) is [{ Name: "Alice" }]);
            Run(400); MatchEnd();
            Check("The match ends: Bob's request is next", Pending?.Map == m[6] && Said("Match over. Bob picked"));
            Land();
            Check("Its map start: the board's queue line says what comes after it", HudView.QueueLine(BuildHud(clock)).StartsWith($"Next: {Clip(Label(m[7]), 36)} (Cara)"));
            Run(300); MatchEnd();
            Check("Then Cara's", Pending?.Map == m[7]);
            Land(); Run(300); MatchEnd();
            Check("Then Alice's second, and her other three keep their own order", Pending?.Map == m[2] && Upcoming().SequenceEqual([m[3], m[4], m[5]]));
            Check($"Airtime was charged per map played (Alice {Airtime.GetValueOrDefault(alice.Item2):0} s, Bob {Airtime.GetValueOrDefault(bob.Item2):0} s, Cara {Airtime.GetValueOrDefault(cara.Item2):0} s)",
                Airtime.GetValueOrDefault(alice.Item2) is > 420 and < 450 && Airtime.GetValueOrDefault(bob.Item2) is > 295 and < 320 && Airtime.GetValueOrDefault(cara.Item2) is > 295 and < 320);

            // ---- least airtime goes first, whoever asked first ----
            Fresh();
            foreach (var p in new[] { bob, cara, dan }) Join(p);
            Talk(bob, $"!map {Rank(1)}"); Land(); Run(1500);
            Talk(bob, "!done");
            Talk(cara, $"!map {Rank(2)}"); Land(); Run(120); Talk(cara, "!done");
            Talk(dan, $"!map {Rank(3)}"); Land(); Run(60);
            Talk(bob, $"!map {Rank(4)}"); Talk(cara, $"!map {Rank(5)}");
            Check("Least airtime first: Cara (2 min played) goes ahead of Bob (25 min played) although Bob asked first", Upcoming().SequenceEqual([m[5], m[4]]));
            foreach (var p in new[] { bob, cara, dan }) Leave(p);
            Run(60 + FreshStartMinutes * 60 - 5);
            Check("Airtime survives a quiet spell...", Airtime.GetValueOrDefault(bob.Item2) > 1000);
            Run(10);
            Check($"...but {FreshStartMinutes} minutes with nobody on resets everyone to zero: the next evening starts even", Airtime.Count == 0 && VClock == 0);

            // ---- a map several players asked for jumps ahead; one of them leaving undoes that ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara, dan }) Join(p);
            Talk(dan, $"!map {Rank(1)}"); Land(); Run(20);
            Talk(alice, $"!map {Rank(2)}"); Talk(bob, $"!map {Rank(3)}"); Talk(cara, $"!map {Rank(3)}");
            Check("Same-map pile-up: the map Bob and Cara both asked for goes ahead of Alice's earlier one", Upcoming().SequenceEqual([m[3], m[2]]));
            Check("...and the queue shows both names on it", QueueLines(clock, 3).Any(l => l.Contains("(Bob+Cara)")));
            Leave(cara); Run(61);
            Check("Cara gone past the 60 s grace: it is Bob's single request again, back behind Alice's earlier one", Upcoming().SequenceEqual([m[2], m[3]]));
            Talk(dan, $"!map {Rank(1)}");
            Check("Asking for the map you already hold is answered, not queued", Said("is already yours") && Queue.Count == 2);
            Talk(alice, $"!map {Rank(2)}");
            Check("Asking twice for the same map is answered with its place, not queued twice", Said("already in the queue, 1st in line") && Queue.Count == 2);
            foreach (var i in new[] { 4, 5, 6, 7 }) Talk(alice, $"!map {Rank(i)}");
            Talk(alice, $"!map {Rank(8)}");
            Check($"{MaxRequestsEach} requests each at most: the sixth is refused", Said($"You have {MaxRequestsEach} maps in the queue already") && Queue.Count(r => r.By.Any(a => a.Id == alice.Item2)) == MaxRequestsEach);
            var shown = QueueLines(clock, 3);
            Console.WriteLine("queue: " + string.Join(" | ", shown));
            Check($"!queue fits three lines of at most 120 characters ({shown.Max(l => l.Length)} longest)", shown.Count <= 3 && shown.All(l => l.Length <= 120) && shown[0].StartsWith("Now: "));
            Talk(alice, "!cancel");
            Check("!cancel drops your newest request", !Upcoming().Contains(m[7]) && Upcoming().Contains(m[6]));
            Talk(alice, "!cancel all");
            Check("!cancel all drops every one of yours and nobody else's", Upcoming().SequenceEqual([m[3]]));

            // ---- the 60 s disconnect grace, and the reload grace a map change needs ----
            Fresh();
            foreach (var p in new[] { alice, bob, dan }) Join(p);
            Talk(dan, $"!map {Rank(1)}"); Land(); Run(20);
            Talk(alice, $"!map {Rank(2)}"); Talk(alice, $"!map {Rank(3)}");
            Run(10); Quiet();
            Leave(alice); Run(59);
            Check("A player who left keeps their requests for 60 s", Upcoming().SequenceEqual([m[2], m[3]]));
            Run(2); Run(3);
            Check("After 60 s they are gone, and chat says so", Upcoming().Count == 0 && Said("Alice left, their 2 requests are gone."));
            Talk(bob, $"!map {Rank(4)}"); Leave(bob); Run(30); Join(bob); Run(60);
            Check("Back within the grace: the request never left", Upcoming().SequenceEqual([m[4]]));
            Leave(bob, "NETWORK_DISCONNECT_SHUTDOWN"); Run(300);
            Check("A map change logs everyone out (reason SHUTDOWN): that is a reload, not a departure, 5 min later still queued", Upcoming().SequenceEqual([m[4]]));
            Run(301);
            Check("...but a reload that never came back gives up after 10 minutes", Upcoming().Count == 0);

            // ---- consent: !done, alone and with co-owners ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Talk(alice, $"!map {Rank(1)}"); Land(); Run(60);
            Talk(bob, $"!map {Rank(2)}");
            Talk(bob, "!done");
            Check("!done from someone who does not hold the map is only a skip vote (1 of 2), and nothing changes", SkipTally(clock) == (1, 2, 2) && Pending == null);
            Quiet(); Talk(alice, "!done");
            Check("!done from the requester: the queue moves on at once", Pending?.Map == m[2] && Said("Alice is done. Bob picked"));
            Land(); Run(61);
            Talk(cara, $"!map {Rank(2)}");
            Check("Asking for the map that is on joins its owners", Holders(clock).Select(o => o.Name).SequenceEqual(["Bob", "Cara"]));
            Talk(alice, $"!map {Rank(3)}"); Talk(bob, "!done");
            Check("One co-owner done: the map stays for the other", Pending == null && Holders(clock) is [{ Name: "Cara" }]);
            Talk(cara, "!done");
            Check("Last owner done: next request", Pending?.Map == m[3]);
            Land(); Run(61); Talk(alice, "!done");
            Check("!done with an empty queue leaves the map on, free", Pending == null && Holders(clock).Count == 0);
            Talk(bob, $"!map {Rank(4)}");
            Check("...and the next request replaces it right away", Pending?.Map == m[4]);

            // ---- automatic maps against requests ----
            Fresh();
            foreach (var p in new[] { alice, bob }) Join(p);
            MatchEnd();
            var dice = Pending;
            Check("A match end with an empty queue rolls the dice", dice is { Auto: true, By: "the dice" } && Said("Nobody picked a map, rolling the dice"));
            Run(3); Quiet();
            var want = dice?.Map == m[1] ? m[0] : m[1]; // not the map the dice happened to roll: that one would be claimed, not replaced (m[0] is not used below)
            Talk(alice, $"!map {want.Rank}");
            Run(8);
            Check("A request replaces a dice countdown outright, and the dice's countdown stops",
                Pending == null && S.Map == want && !sent.Any(c => c.Contains($"Changing to {Label(dice!.Map)}")) && sent.Contains($"host_workshop_map {want.Id}"));
            Run(30); MatchEnd();
            Check("A match end while the requested map is still loading is the old map's: ignored", Pending == null && AnnouncePending);
            Land(); Run(30);
            Talk(bob, "!next");
            Check("!next is refused while somebody else's request is on", Pending == null && Said("is Alice's request until its match ends"));
            Talk(bob, $"!map {Rank(2)}"); Talk(alice, "!next");
            Check("!next from the requester means done: the queue goes first", Pending?.Map == m[2] && Said("Alice is done. Bob picked"));
            Land(); Run(61); Talk(bob, "!done"); Talk(bob, "!next");
            Check("!next on a free map with an empty queue rolls the dice for the player", Pending is { Auto: true, By: "Bob" } && Said("Bob rolled the dice"));
            Land(); Run(10);
            Talk(alice, $"!map {Rank(3)}");
            Check($"A request inside the {Cooldown} s change cooldown waits for it...", Pending == null && Upcoming().SequenceEqual([m[3]]));
            Run(Cooldown - 5);
            Check("...then starts by itself", Pending?.Map == m[3]);
            Land(); Run(30);
            Talk(bob, $"!map {Rank(4)}");
            Leave(alice); Run(61);
            Check("The requester gone for over 60 s while others are queued: the next request starts, and chat says why", Pending?.Map == m[4] && Said("Alice left. Bob picked"));
            Land(); Run(30); Leave(bob); Run(61);
            Check("The requester gone with nothing queued: the map just stays on, free", Pending == null && Holders(clock).Count == 0 && S.Map == m[4]);
            Join(alice); Talk(alice, $"!map {S.Map!.Rank}");
            Check("Asking for the random map that is on claims it: it is now theirs", Pending == null && Holders(clock) is [{ Name: "Alice" }]);

            // ---- !skip: a majority of the players who do not hold the map ends it ----
            Fresh();
            var eve = ("Eve", "[U:1:1005]");
            foreach (var p in new[] { alice, bob, cara, dan, eve }) Join(p);
            Talk(alice, $"!map {Rank(1)}"); Land(); Run(61);
            Talk(eve, $"!map {Rank(2)}"); Run(10); Quiet();
            Talk(bob, "!skip");
            Check("The holder is not counted: Bob's skip is 1 vote of the 4 others, 3 needed", SkipTally(clock) == (1, 4, 3) && Pending == null);
            Run(3);
            Check("...and chat says how far it is", Said("Bob voted to skip") && Said("1/4 - 2 more needed."));
            Talk(cara, "!done");
            Check("A non-holder's !done is the same vote", SkipTally(clock) == (2, 4, 3));
            Talk(cara, "!skip");
            Check("Voting twice counts once", SkipTally(clock) == (2, 4, 3) && Said("You voted already"));
            Leave(bob); Run(1);
            Check("A voter who leaves before the majority takes the vote along: 1 of the 3 still on", SkipTally(clock) == (1, 3, 2) && Pending == null);
            Talk(dan, "!skip");
            Check("Majority: the map goes exactly as on !done - the queue moves on", Pending?.Map == m[2] && Said("Skipped by vote (2/3). Eve picked"));
            Land();
            Check("The map changed: every vote is gone", SkipVotes.Count == 0 && SkipTally(clock) == (0, 3, 2));
            Join(bob); Run(1); Talk(alice, "!skip"); Talk(cara, "!skip");
            Check("Two of four others is no majority yet", Pending == null && Holders(clock) is [{ Name: "Eve" }]);
            Leave(dan); Run(1);
            Check("A non-voter leaving can complete it: 2 of the 3 still on", Holders(clock).Count == 0);
            Fresh();
            Join(alice); Talk(alice, $"!map {Rank(1)}"); Land(); Run(10);
            Check("A holder alone on the server: no majority to reach", SkipTally(clock) == null);
            Talk(alice, "!skip");
            Check("...and their own !skip is their !done", Holders(clock).Count == 0);

            // ---- the crash counter (CrashWatch.cs): RCON status's spawn counter tells a new server process from the old one ----
            // The fake server counts its loads in statusSpawn and shows the map it loaded last; Crash() is the process dying without a
            // quit and the new one booting on its start map, as the log shows that.
            void Crash() { statusSpawn = 0; Line($"Loading map \"{StartMap}\""); Line("Started:  \"\""); }
            Fresh(); statusMap = "bsp"; statusSpawn = 7;
            Join(alice); Talk(alice, $"!map {Rank(1)}"); Land(); Run(10);
            Check("The first reading after the bridge started is only the baseline", SpawnSeen == 8 && S.Crashes.Count == 0);
            Run(60); Talk(alice, "!done"); Talk(alice, $"!map {Rank(2)}"); Land(); Run(10); // past the change cooldown, so the request starts at once
            Check("Every map start of the same process counts the spawn counter up: no crash", SpawnSeen == 9 && S.Crashes.Count == 0);
            var crashed = S.Map!; Quiet();
            Crash(); Run(10); Join(bob);
            Check("A new process without a quit is recorded against the map that was on, said at once and to whoever joins",
                S.Crashes.GetValueOrDefault(Key(crashed)) == 1 && S.Crashes.Count == 1 && Said($"The server went down on {Clip(Label(crashed), 44)} - it is marked")
                && Said($"after a crash on {Clip(Label(crashed), 44)}"));
            Quiet(); Talk(cara, $"!map {crashed.Rank}");
            Check("...!map now asks first", Said("crashed the server 1 time(s)") && Pending?.Map != crashed);
            Check("...the dice skip it", Enumerable.Range(0, 500).Select(_ => RandomPick(true, 3)).All(x => x == null || Key(x) != Key(crashed)));
            Check("...and the HUD's map card says so", BuildCatalog().Maps.FirstOrDefault(x => x.Id == crashed.Id) is { Crashes: 1 });
            File.WriteAllText(MaintenanceFile, ""); InMaintenance(); Quiet(); Join(dan);
            Check("Joiners do not hear about it while the maintenance flag exists...", !Said("after a crash"));
            File.Delete(MaintenanceFile); InMaintenance();
            Run(CrashNoticeMinutes * 60); Quiet(); Join(dan);
            Check("...nor once the reconnect burst is long over", !Said("after a crash"));

            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Talk(alice, $"!map {Rank(3)}"); Land(); Run(10);
            File.WriteAllText(MaintenanceFile, ""); InMaintenance();
            Crash(); Run(20);
            Check("Maintenance (the map sweeps, the cache work): a new process is not recorded, and nobody is told",
                S.Crashes.Count == 0 && S.CrashNotice == null && SpawnSeen == 1);
            File.Delete(MaintenanceFile); InMaintenance();

            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Talk(alice, $"!map {Rank(3)}"); Land(); Run(10);
            Line("rcon from \"192.0.2.2:5555\": command \"quit\"");
            Crash(); Run(20);
            Check("A new process after a clean quit (!restart, the 07:00 restart, an admin) is not a crash", S.Crashes.Count == 0 && SpawnSeen == 1);

            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Talk(alice, $"!map {Rank(3)}"); Land(); Run(10);
            Run(60); Talk(alice, "!done"); Talk(alice, $"!map {Rank(4)}"); Run(ChangeDelay);
            Check("(the change to the next map has gone out)", AnnouncePending && S.Map == m[4]);
            Crash(); Run(20);
            Check("A process that dies while a map loads is that map's crash", S.Crashes.GetValueOrDefault(Key(m[4])) == 1 && S.Crashes.Count == 1);

            // two quick starts: a reading counts only when status shows the newest start's map, and the starts are judged together
            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Talk(alice, $"!map {Rank(5)}"); Land(); Run(10);
            Run(60); Talk(alice, "!done"); Talk(alice, $"!map {Rank(6)}"); Land();
            statusSpawn++; statusMap = "bsp_ahead"; Run(6); // before its reading the server is on another map already, whose log line is late
            Ingest($"{clock:MM/dd/yyyy - HH:mm:ss}.000 - Loading map \"bsp_ahead\"", null); Run(20);
            Check("Status ahead of the log: no reading until the late start is in, then both are judged together - no crash",
                S.Crashes.Count == 0 && SpawnSeen == 6 && SpawnSeenStarts == MapStarts);

            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Talk(alice, $"!map {Rank(7)}"); Land(); Run(10);
            var wasOn = S.Map!;
            rconDown = true; Crash(); Run(2); Line("Loading map \"bsp_quick\""); Run(3); rconDown = false; Run(30);
            Check("Two starts before one reading, the first of them the new process's boot: what was on before the boot is blamed, not the boot map",
                S.Crashes.GetValueOrDefault(Key(wasOn)) == 1 && S.Crashes.Count == 1);

            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Talk(alice, $"!map {Rank(8)}"); Land(); Run(10);
            rconDown = true; Line("Loading map \"ar_baggage\""); Line("Started:  \"\""); Run(2); Crash(); Run(3); rconDown = false; Run(30);
            Check("Two starts before one reading, the second the new process's boot: the map the first one loaded is blamed, not the one before it",
                S.Crashes.GetValueOrDefault("ar_baggage") == 1 && S.Crashes.Count == 1);

            Fresh(); statusMap = "bsp"; statusSpawn = 3;
            Join(alice); Talk(alice, $"!map {Rank(9)}"); Land(); Run(10);
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
            Check("...then rolls the dice", Pending is { Auto: true } && Said("Server started, rolling the dice"));
            Join(alice); Join(dan);
            Land(); Run(61); Talk(dan, $"!map {Rank(1)}"); Land(); Run(30); Talk(alice, $"!map {Rank(2)}");
            S.CleanShutdown = true; Session("token-3"); Run(3);
            Check("A restarted server is left alone while it loads its start map, queue or not", Pending == null);
            Line("Loading map \"ar_shoots\""); Run(4);
            Check("...and for 5 s after it", Pending == null);
            Run(2);
            Check("After a restart the queue comes first: Alice's waiting request, not the dice", Pending?.Map == m[2] && Said("Server started. Alice picked"));

            // ---- the live boot of 2026-10-04: the token change went unseen, the one map start had no name, RCON answered 15 s later ----
            Fresh();
            S.ServerToken = "boot"; S.LogName = "aim_old_map"; S.CleanShutdown = true; // the state the bridge started with: this server's token already known
            rconDown = true;
            Session("boot"); Line("Started:  \"\"");
            Check("A lone nameless map start is taken for START_MAP, and the start roll is owed", S.Map?.Name == "ar_shoots" && StartRollOwed);
            Run(15);
            Check("RCON not answering: no countdown into the void, the roll stays owed", Pending == null && !Said("rolling the dice") && StartRollOwed);
            rconDown = false; Run(11);
            Check("RCON answering: the start roll goes out", Pending is { Auto: true } && Said("Server started, rolling the dice"));
            Run(ChangeDelay);
            Check("...and only a change command that went out settles it", sent.Any(c => c.StartsWith("host_workshop_map")) && !StartRollOwed);
            Fresh();
            Session("boot-1"); S.CleanShutdown = true; Session("boot-2"); Line("Loading map \"ar_shoots\"");
            File.WriteAllText(MaintenanceFile, ""); Run(30);
            Check("Maintenance: the owed start roll waits...", Pending == null && StartRollOwed);
            File.Delete(MaintenanceFile); Run(2);
            Check("...and goes out once the flag is gone", Pending is { Auto: true } && Said("Server started, rolling the dice"));

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
            bool Rolled() => Pending is { Auto: true } && Said("Server started, rolling the dice");
            void Confirm((string Name, string Id) p) { Talk(p, "!restart"); Talk(p, "!restart"); }

            Fresh(); statusMap = "ar_shoots"; Session("prod");
            Join(alice); Talk(alice, $"!map {Rank(1)}"); Land(); Run(60);
            Confirm(alice); Run(4);
            Check("!restart: quit goes out 3 s after the confirm", sent.Contains("quit"));
            Restart(clean: true); Run(6);
            Check("Restart path 1, !restart on a map that is up: the boot's start map rolls the dice", Rolled());

            Fresh(); statusMap = "ar_shoots"; Session("prod");
            Join(alice); Run(5); Talk(alice, $"!map {Rank(1)}"); Run(ChangeDelay + 120); // the change went out; the download stalls
            Confirm(alice); Run(4); Quiet();
            Restart(clean: true); Run(6);
            Check("Restart path 2, !restart on a stuck load: the boot is not taken for the stuck map", !Said($"Now on {Label(m[1])}") && S.Map?.Name == "ar_shoots" && !AnnouncePending);
            Check("...and the dice roll", Rolled());

            Fresh(); statusMap = "ar_shoots"; Session("prod");
            Join(alice); Run(5); Talk(alice, $"!map {Rank(1)}"); Run(ChangeDelay + 120);
            Confirm(alice); Run(4); Quiet();
            Restart(clean: true, header: "ar_shoots"); Run(6);
            Check("Restart path 2 with the boot's map start named by X-Game-Map: the same", !Said($"Now on {Label(m[1])}") && Rolled());

            Fresh(); statusMap = "ar_shoots"; Session("prod");
            Join(alice); Run(5); Talk(alice, $"!map {Rank(1)}"); Run(1);
            Confirm(alice); Run(3); // the quit goes out while the countdown still runs: its host_workshop_map would hit a dead server
            Quiet(); Restart(clean: true); Run(6);
            Check("Restart path 3, !restart during a countdown: the change dies with the old process, the boot rolls", !Said($"Now on {Label(m[1])}") && Rolled());

            Fresh(); statusMap = "ar_shoots"; Session("prod");
            Join(alice); Join(bob); Run(30); MatchEnd();
            Check("Restart path 4, the 07:00 restart with players on: the match ends, the dice count down...", Pending is { Auto: true });
            Run(3); Quiet(); Restart(clean: true); Run(6); // the restart watcher quits 3 s after Game Over
            Check("...the watcher's quit lands inside that countdown, and the boot still rolls", Rolled());

            Fresh(); statusMap = "ar_shoots"; Session("prod");
            Join(alice); Run(5); Talk(alice, $"!map {Rank(1)}"); Run(ChangeDelay + 3); // the map crashes the server while it loads
            Quiet(); Restart(clean: false); Run(6);
            Check("Restart path 5, a crash or freeze kill during a load (no quit echo): the boot rolls", !Said($"Now on {Label(m[1])}") && Rolled());

            Fresh(); statusMap = "ar_shoots"; Session("prod");
            Join(alice); Talk(alice, $"!map {Rank(1)}"); Land(); Run(60);
            Quiet(); Restart(clean: false); Run(6);
            Check("Restart path 6, Docker restarting cs2 alone (SIGKILL, no echo) on a map that is up: the boot rolls", Rolled());
            Check("...and with no quit echo, the crash counter marks the map that was up", S.Crashes.GetValueOrDefault(Key(m[1])) == 1 && S.Crashes.Count == 1);

            Fresh(); statusMap = "ar_shoots"; Session("prod");
            Join(alice); Join(bob); Talk(alice, $"!map {Rank(1)}"); Land(); Run(60); Talk(bob, $"!map {Rank(2)}");
            Confirm(alice); Run(4); Quiet(); Restart(clean: true); Run(6);
            Check("A restart with somebody queued serves the queue, not the dice", Pending?.Map == m[2] && Said("Server started. Bob picked"));

            Fresh(); Session("prod");
            Join(alice); Talk(alice, "!map ar_shoots"); Run(ChangeDelay); Line("Loading map \"ar_shoots\""); Line("Started:  \"\""); Run(20);
            Check("A player's own !map of the start map is theirs: no roll", Pending == null && S.PickedBy == "Alice" && !StartRollOwed);

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

            // ---- the maintenance flag: no automatic change of any kind; player commands still work ----
            Fresh();
            foreach (var p in new[] { alice, bob }) Join(p);
            File.WriteAllText(MaintenanceFile, "");
            Run(1);
            Check("The flag is noticed within a second", MaintenanceOn);
            Session("token-a"); S.CleanShutdown = true; Session("token-b"); Line("Loading map \"ar_shoots\""); Run(10);
            Check("Maintenance: no start roll", Pending == null);
            foreach (var p in new[] { alice, bob }) Join(p);
            MatchEnd();
            Check("Maintenance: no match-end roll", Pending == null);
            Talk(alice, $"!map {Rank(1)}");
            Check("Maintenance: a player's !map on a free map still changes it", Pending?.Map == m[1]);
            Land(); Run(70); Talk(bob, $"!map {Rank(2)}"); MatchEndUntil = default; MatchEnd(); Run(5);
            Check("Maintenance: the match ends and the queue does not advance by itself", Pending == null && Upcoming().SequenceEqual([m[2]]));
            File.Delete(MaintenanceFile); Run(1);
            Check("Flag gone: automatic changes resume, and the waiting request starts", !MaintenanceOn && Pending?.Map == m[2]);

            // ---- !previous ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Talk(alice, "!previous");
            Check("!previous with nothing on record says so", Said("no previous map on record yet") && Pending == null);
            Talk(bob, "!next"); var first = Pending!.Map; Land(); Run(70);
            Talk(alice, $"!map {Rank(1)}"); Land(); Run(61); Talk(alice, "!done");
            Talk(bob, "!previous");
            Check("!previous asks for the map before this one, and on a free map it loads at once", Pending?.Map == first && Pending.Owners is [{ Name: "Bob" }]);
            Land(); Run(70); Talk(bob, "!done"); Talk(cara, "!previous");
            Check("...and again goes back the other way", Pending?.Map == m[1]);
            Land(); Run(30); Talk(alice, "!previous");
            Check("While someone's request is on, !previous is queued like any request", Pending == null && Upcoming().SequenceEqual([first]));

            // ---- votes ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Talk(alice, "!up");
            Check("!up without a known map asks for a number", Said("Not sure which map this is"));
            Talk(alice, $"!map {Rank(1)}"); Run(ChangeDelay);
            var key = Key(m[1]);
            Talk(alice, "!up");
            Check("A vote while the next map is still loading is refused, not given to a map nobody has seen yet", Said("still loading") && Tally(key) == (0, 0));
            Land();
            Talk(alice, "!up"); Talk(bob, "!down");
            Check("!up and !down vote on the map that is on", Tally(key) == (1, 1) && Said("You upvoted") && Said("You downvoted") && NoteFor.ContainsKey(1001) && NoteFor.ContainsKey(1002));
            Talk(alice, "!up");
            Check("The same vote again takes it back", Tally(key) == (0, 1) && Said("You took back your upvote"));
            Talk(alice, "!down"); Talk(alice, "!up");
            Check("The other vote replaces yours: one vote per player per map", Tally(key) == (1, 1) && Votes[key].Count == 2 && Said("You changed your vote"));
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
            Check("The most liked list (the board's live column; the menu's Votes sort shows them all): most upvotes first, fewer downvotes breaking the tie",
                top[0].Map == m[2] && top[0].Up == 3 && top.FindIndex(r => r.Map == m[4]) is > 0 and var i4 && top.FindIndex(r => r.Map == m[3]) > i4);
            Talk(cara, $"!up {Rank(4)}"); Talk(bob, $"!up {Rank(4)}");
            Check("Taking back the last vote on a map leaves no empty entry behind", !Votes.ContainsKey(Key(m[4])));
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
            Check($"Workshop rank: #1 weighs 3x the last map (#{Maps.Count}), the middle about 2x",
                Math.Abs(Weight(Maps[0]) / Weight(Maps[^1]) - 3) < 1e-9 && Math.Abs(Weight(mid) / Weight(Maps[^1]) - 2) < 0.01);
            var realMaps = Maps;
            Maps = Enumerable.Range(1, 12).Select(i => new MapInfo(i, 5_000_000 + i, $"fixture_{i}", 10)).ToList();
            Votes = new() { [Key(Maps[11])] = new() { ["[U:1:1]"] = 1 }, [Key(Maps[0])] = new() { ["[U:1:1]"] = -1 } };
            var weights = Maps.ToDictionary(x => x, Weight);
            var draws = Enumerable.Range(0, 60000).Select(_ => RandomPick(true, 1)!).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count() / 60000.0);
            var worst = Maps.Max(x => Math.Abs(draws.GetValueOrDefault(x) / (weights[x] / weights.Values.Sum()) - 1));
            Check($"The dice follow the weights: 60000 rolls over 12 fixtures, every map within 10% of its share (worst {worst:P1})", worst < 0.10);
            S.History = Maps.Take(10).ToList();
            Check("The dice skip the last 10 maps played while anything else is left", Enumerable.Range(0, 500).All(_ => RandomPick(true, 1)!.Rank > 10));
            S.History = Maps.Take(12).ToList();
            Check("...and fall back to the whole pool when nothing else is", RandomPick(true, 1) is not null);
            S.History = new();
            Maps = [new MapInfo(1, 6_000_001, "huge_liked", 900), new MapInfo(2, 6_000_002, "huge", 900), new MapInfo(3, 6_000_003, "small", 10)];
            Votes = new() { [Key(Maps[0])] = new() { ["[U:1:1]"] = 1 } };
            var bigRolls = Enumerable.Range(0, 300).Select(_ => RandomPick(true, 1)!.Name).ToHashSet();
            Check("A map's download size is no reason either way (the owner's 4c, 2026-10-24): a 900 MB map nobody liked is rolled like a 10 MB one",
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
            Talk(alice, $"!map {Rank(1)}");
            Check("The pick is on the news line at once, with the countdown's length", NewsNow.Text.Contains("Alice picked") && NewsNow.Text.Contains("changing in"));
            Land(); Run(20);
            Check("The map changes, and nothing at all went to chat: no countdown, no after-load line", sent.Contains($"host_workshop_map {m[1].Id}") && !sent.Any(c => c.StartsWith("say ")));
            HudActive = () => false;
            Run(Cooldown); Quiet(); Talk(alice, "!done"); Talk(bob, $"!map {Rank(2)}"); Land(); Run(20);
            Check("...and the same without the HUD's plugin polling: chat stays quiet", !sent.Any(c => c.StartsWith("say ")) && sent.Contains($"host_workshop_map {m[2].Id}"));

            // ---- the HUD's live state, as the plugin reads it on /hud, and clicks coming back on /act ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Talk(alice, $"!map {Rank(1)}"); Land(); Run(Cooldown + 1); // past the change cooldown, so a release starts the next request at once
            Talk(bob, $"!map {Rank(2)}"); Talk(cara, $"!map {Rank(3)}"); Talk(bob, "!up"); Run(1);
            var hud = HudState.Decode(HudText)!;
            Console.WriteLine("hud: " + HudText.Replace('\n', '|').Replace('\t', ' '));
            Check("The HUD state names the map, who picked it, its votes, who holds it and who voted what",
                hud.Map == Clip(Label(m[1]), 40) && hud.By == "Alice" && (hud.Up, hud.Down) == (1, 0) && hud.Holders.SequenceEqual([1001L]) && hud.MyVotes.GetValueOrDefault(1002) == 1 && hud.Maps == Playable);
            Check("...the queue in play order with who asked, and the most liked maps",
                hud.Next.Select(n => n.Map).SequenceEqual([Clip(Label(m[2]), 36), Clip(Label(m[3]), 36)]) && hud.Next[0].Who == "Bob" && hud.Top is [var liked] && liked.Up == 1 && liked.Map == Clip(Label(m[1]), 36));
            Talk(bob, "!skip"); Run(1);
            hud = HudState.Decode(HudText)!;
            Check("A skip vote shows its count, and the window's button fits each player: the holder lets go, the others vote it off",
                hud.Skip == (1, 2, 2) && HudView.Action(hud, 1001).Command == "!done" && HudView.Action(hud, 1002).Command == "!skip" && HudView.Action(hud, 1003).Text.Contains("1/2"));
            Quiet();
            Check("A click on the HUD acts exactly like the chat command: Cara's upvote is counted and said",
                HudAct("[U:1:1003]\tCara\t!up") && Tally(Key(m[1])) == (2, 0) && Said("You upvoted") && NoteFor.ContainsKey(1003));
            Check("Only the menu's own commands pass: no !rcon, no map by free-form name, no malformed sender",
                !HudAct("[U:1:1003]\tCara\t!rcon quit") && !HudAct("[U:1:1003]\tCara\t!map aim") && !HudAct("[U:1:1003]\tCara\t!map 5; quit")
                && !HudAct("[U:1:1003]\tCara\t!up 12 x") && !HudAct("[U:1:1003]\tCara\t!time 30")
                && !HudAct("1003\tCara\t!up") && !HudAct("[U:1:1003]\t!up") && !HudAct("") && Pending == null && Tally(Key(m[1])) == (2, 0));
            Run(1); // the next tick rebuilds the state and, the votes having moved, the catalog
            hud = HudState.Decode(HudText)!;
            Check("The menu's queue page: every request in play order, with the map's command argument and who asked, by account",
                hud.Queue.Select(q => q.Arg).SequenceEqual([Rank(2), Rank(3)]) && hud.Queue[0].Accounts.SequenceEqual([1002L]) && hud.Queue[1].Who == "Cara"
                && hud.Queue[0].Key == Key(m[2]) && hud.RoundMinutes == S.RoundMinutes && hud.CatalogVersion == CatalogVersion);
            var catalog = HudCatalog.Decode(CatalogText)!;
            Check($"The menu's catalog: every listed and official map once ({catalog.Maps.Count}), with its votes and whose they are",
                catalog.Maps.Count == Maps.Count(x => !IsPack(x)) + Officials.Count && catalog.Maps.Select(x => x.Key).Distinct().Count() == catalog.Maps.Count
                && catalog.Maps.First(x => x.Key == Key(m[1])) is { Up: 2, Down: 0 } && catalog.MyVote(1003, Key(m[1])) == 1 && catalog.MyVote(1001, Key(m[1])) == 0
                && catalog.Maps.Where(x => x.Official).All(x => x.Arg.StartsWith("ar_") && Hud.IsAction("!map " + x.Arg))
                && catalog.Maps.Where(x => !x.Official).All(x => x.Arg == x.Rank.ToString() && Hud.IsAction("!up " + x.Arg)));
            var version = CatalogVersion;
            Check("A menu click on a card acts like the chat command: Cara's downvote on map 4 by its rank", HudAct($"[U:1:1003]\tCara\t!down {Rank(4)}")
                && Tally(Key(m[4])) == (0, 1) && CatalogVersion > version);
            Run(1);
            Check("...and the catalog the plugin fetches next shows it", HudCatalog.Decode(CatalogText)!.MyVote(1003, Key(m[4])) == -1 && HudState.Decode(HudText)!.CatalogVersion == CatalogVersion);
            Check("A cancel button acts like !cancel <map>: Cara's request goes, Bob's stays", HudAct($"[U:1:1003]\tCara\t!cancel {Rank(3)}") && Upcoming().SequenceEqual([m[2]]));
            Check("!time by click sets the match length", HudAct("[U:1:1003]\tCara\t!time 10") && S.RoundMinutes == 10 && HudAct("[U:1:1003]\tCara\t!time 60") && S.RoundMinutes == 60);
            Talk(alice, "!done"); Run(2);
            hud = HudState.Decode(HudText)!;
            Check($"A change counting down: the banner gets its map, who asked and the seconds left ({hud.ChangeIn}), and nobody gets a button for it",
                hud.ChangeMap == Clip(Label(m[2]), 40) && hud.ChangeBy == "Bob" && hud.ChangeIn is > 0 and < 8 && Enumerable.Range(1001, 3).All(a => HudView.Action(hud, a).Command == null));
            Check("The state's text round-trips through the codec unchanged", HudState.Decode(hud.Encode())!.Encode() == hud.Encode());
            Land(); Run(2);
            hud = HudState.Decode(HudText)!;
            Check("Once it has loaded: Bob's map, Bob holds it, the change is over", hud.Map == Clip(Label(m[2]), 40) && hud.ChangeIn == -1 && hud.Holders.SequenceEqual([1002L]) && !hud.Loading);

            // ---- queue news: merged, rate-limited, always with the order ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara, dan }) Join(p);
            Talk(dan, $"!map {Rank(1)}"); Land(); Run(30); Quiet();
            Talk(alice, $"!map {Rank(2)}"); Talk(bob, $"!map {Rank(3)}"); Talk(cara, $"!map {Rank(3)}");
            Run(3);
            var burst = Told.ToList();
            Console.WriteLine("news: " + string.Join(" | ", burst));
            Check("Three queue changes in one burst become one merged news line, not three - and nothing goes to chat",
                burst.Count == 1 && burst[0].Contains("Alice queued") && burst[0].Contains("Bob queued") && burst[0].Contains("Cara also wants") && NewsNow.Text == burst[0]
                && !sent.Any(c => c.StartsWith("say ")));
            Quiet(); Talk(alice, $"!map {Rank(4)}"); Run(4);
            Check("A change right after is held back: at most one news burst per 8 s", Told.Count == 0);
            Run(3);
            Check("...and still arrives", Said("Alice queued"));

            // ---- maps gone from the Workshop (Removed.cs): refused, never rolled, dropped from the queue, unblocked when they come back ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            var goneMap = m[3];
            var goneId = goneMap.Id.ToString();
            RemovedSeed = new() { [goneId] = Workshop.Removed }; RecomputeRemoved();
            Quiet();
            Talk(alice, $"!map {Rank(3)}");
            Check("A map gone from the Workshop is refused by number, with what happened and what to do, and nothing is queued",
                Said($"{Clip(Label(goneMap), 44)} was removed from the Workshop - nobody can download it. Pick another.") && Pending == null && Queue.Count == 0);
            Quiet();
            Talk(bob, $"!map {goneMap.Id}");
            Check("...by its workshop id too", Said("was removed from the Workshop - nobody can download it. Pick another.") && Pending == null);
            Check("...and the refusal fits one chat line for every map on the list",
                Maps.All(x => { Removed = new() { [x.Id.ToString()] = Workshop.Removed }; return RemovedRefusal(x).Length <= 120; }));
            RecomputeRemoved();
            if (File.Exists("broken.json"))
                Check("The maps found removed (2026-10-20 and 2026-10-24) are in the repo's broken.json, as the seed: any number, each a removed verdict",
                    ParseRemoved(File.ReadAllText("broken.json")) is { Count: >= 2 } seedRepo
                    && seedRepo.ContainsKey("3659287341") && seedRepo.ContainsKey("3790257852") && seedRepo.Values.All(v => v == Workshop.Removed));
            else Console.WriteLine("skip the broken.json seed check: no broken.json beside the bridge");
            Check("The seed reads bare ids, strings and objects, keeps a stated verdict, and drops what is not an id",
                ParseRemoved("""{"removed":[3659287341,"3790257852",{"id":1,"verdict":"hidden"},{"id":2,"verdict":"ok"},{"rank":5},null,-3]}""") is { Count: 4 } sp
                && sp["1"] == Workshop.Hidden && sp["2"] == Workshop.Removed && ParseRemoved("{}").Count == 0 && ParseRemoved("""{"removed":5}""").Count == 0);
            var dicePool = Maps.Where(x => x.Rank > 0).Take(400).ToList();
            RemovedSeed = dicePool.Skip(3).Take(390).ToDictionary(x => x.Id.ToString(), _ => Workshop.Removed); RecomputeRemoved();
            var goneRolls = Enumerable.Range(0, 2000).Select(_ => RandomPick(true, 3)).Where(x => x != null).Select(x => x!).ToList();
            Check($"The dice never roll a map gone from the Workshop ({RemovedSeed.Count} of them, 2,000 rolls)", goneRolls.Count > 0 && goneRolls.All(x => !IsRemoved(x)));
            RemovedSeed = new() { [goneId] = Workshop.Removed }; RecomputeRemoved();
            Check("The plugin is asked to check every map the list holds, the seed too, and no non-map",
                Workshop.DecodeIds(CheckList()) is var checkIds && checkIds.Contains(goneMap.Id) && Maps.Where(x => !IsPack(x) && x.Id > 0).All(x => checkIds.Contains(x.Id))
                && Packs.Keys.All(k => !checkIds.Contains(long.Parse(k))));

            // the daily check's report: a map coming back, a queued map going, the map that is on going
            var answer = ApplyRemovedReport(Workshop.EncodeReport([(goneMap.Id, Workshop.Ok)]));
            Check("A map the check finds downloadable again is unblocked, though the seed still lists it, and the report says so",
                !IsRemoved(goneMap) && answer.Contains("1 unblocked") && File.Exists(Path.Combine(StateDir, "removed.json")));
            Quiet();
            Talk(alice, $"!map {Rank(3)}");
            Check("...and can be picked again", Pending?.Map == goneMap || Upcoming().Contains(goneMap));
            Land(); Run(30);
            Talk(bob, $"!map {Rank(5)}"); Talk(cara, $"!map {Rank(6)}");
            Check("(two requests wait behind Alice's map)", Upcoming().SequenceEqual([m[5], m[6]]));
            Run(20); Quiet();
            answer = ApplyRemovedReport(Workshop.EncodeReport([(m[5].Id, Workshop.Removed), (m[6].Id, Workshop.Ok)]));
            Run(10);
            Check("A queued map the check finds gone is dropped from the queue, and chat says why",
                Upcoming().SequenceEqual([m[6]]) && Said("is gone from the Workshop - its request is dropped.") && answer.Contains("1 newly blocked"));
            Check("...and Checked keeps the verdict, so a restarted bridge still knows", Checked.Verdicts.GetValueOrDefault(m[5].Id.ToString()) == Workshop.Removed);
            Talk(alice, "!done"); Run(1);
            var landing = Pending?.Map;
            Land(); Run(30); Quiet();
            answer = ApplyRemovedReport(Workshop.EncodeReport([(landing!.Id, Workshop.Hidden)]));
            Check("The map that is ON going private makes way at once: the dice roll another, and chat says why",
                Pending is { Auto: true } rolled && rolled.Map != landing && !IsRemoved(rolled.Map) && Said("is no longer public on the Workshop - nobody new can download it. Rolling the dice"));
            Land(); Run(30);
            var before = Removed.Count;
            answer = ApplyRemovedReport(Workshop.EncodeReport(Maps.Take(200).Select((x, i) => (x.Id, i < 30 ? Workshop.Removed : Workshop.Ok))));
            Check("A report calling 30 of 200 maps gone (more than 10%) is not believed: nothing changes",
                answer.StartsWith("ignored") && Removed.Count == before);
            Check("An empty or garbled report changes nothing either", ApplyRemovedReport("not a report").StartsWith("ignored") && Removed.Count == before);
            RemovedSeed = new() { [goneId] = Workshop.Removed }; Checked = new(); RecomputeRemoved();
            S.History.Add(goneMap);
            Talk(cara, "!previous");
            Check("!previous refuses a map gone from the Workshop", Said("was removed from the Workshop - nobody can download it"));
            RemovedSeed = new(); Checked = new(); RecomputeRemoved();

            // the Workshop's own words, as the Web API answers
            var details = """{"response":{"result":1,"resultcount":4,"publishedfiledetails":[{"publishedfileid":"3659287341","result":9},{"publishedfileid":"3084291314","result":1,"visibility":0,"banned":false},{"publishedfileid":"3453526792","result":1,"visibility":2,"banned":false},{"publishedfileid":"3395240479","result":1,"visibility":0,"banned":true},{"result":1}]}}""";
            Check("The Web API's answer reads as verdicts: gone, downloadable, private, banned; an entry without an id is skipped",
                Workshop.ParseDetails(details) is var pd && pd.Count == 4 && pd[0] == (3659287341L, Workshop.Removed) && pd[1] == (3084291314L, Workshop.Ok)
                && pd[2] == (3453526792L, Workshop.Hidden) && pd[3] == (3395240479L, Workshop.Banned) && Workshop.ParseDetails("{}").Count == 0);
            Check("The id list and the report survive their codecs, and a report keeps only well-formed lines",
                Workshop.DecodeIds(Workshop.EncodeIds([5, 7, 5])).SequenceEqual([5L, 7L]) && Workshop.DecodeIds("nope").Count == 0
                && Workshop.DecodeReport(Workshop.EncodeReport([(5, Workshop.Ok), (7, Workshop.Banned)]) + "x\tok\n9\tmaybe\n-1\tok\n") is [(5, "ok"), (7, "banned")]);

            // ---- blocked for good, with its own reason (Blocked.cs): glassbox kicked every player off the server on 2026-10-20 ----
            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Blocked = new() { ["3076804331"] = ("glassbox", "glassboxfinal", "kicked every player off the server (2026-10-20)") };
            var glassLine = "glassbox (3076804331) kicked every player off the server (2026-10-20) - it is blocked.";
            Talk(alice, "!map 3076804331");
            Check("A blocked map is refused by id, in its own words, and nothing is queued", Said(glassLine) && Pending == null && Queue.Count == 0);
            Quiet(); Talk(bob, "!map https://steamcommunity.com/sharedfiles/filedetails/?id=3076804331");
            Check("...by a pasted Workshop URL", Said(glassLine) && Pending == null);
            Quiet(); Talk(cara, "!map glassboxfinal");
            Check("...and by its name or its map's name, though maps.json does not list it", Said(glassLine) && Pending == null);
            Quiet(); S.History.Add(new MapInfo(0, 3076804331, "workshop 3076804331", 0)); Talk(alice, "!previous");
            Check("...and from !previous", Said(glassLine) && Pending == null);
            var listedBlock = m[4];
            Blocked[listedBlock.Id.ToString()] = (listedBlock.Name, "", "froze the server");
            Check("A blocked map on the list: never rolled, and left out of the HUD's map browser",
                Enumerable.Range(0, 800).Select(_ => RandomPick(true, 3)).All(x => x == null || !IsBlocked(x)) && BuildCatalog().Maps.All(x => x.Id != listedBlock.Id));
            Check("The refusal fits one chat line whatever the name and reason",
                BlockedRefusal(new MapInfo(0, 3076804331, "x", 0)) is var r0 && r0.Length <= 120
                && (Blocked["3076804331"] = (new string('n', 90), "", new string('r', 200))) != default && BlockedRefusal(new MapInfo(0, 3076804331, "x", 0)).Length <= 120);
            Check("The list reads objects and bare ids; a missing reason still blocks",
                ParseBlocked("""{"blocked":[{"id":5,"name":"a","map":"b","reason":"c"},6,{"name":"no id"},{"id":7}]}""") is { Count: 3 } pb
                && pb["5"] == ("a", "b", "c") && pb["6"].Item3 == "is on the blocked list" && pb["7"].Item1 == "workshop 7" && ParseBlocked("{}").Count == 0);
            if (File.Exists("broken.json"))
                Check("glassbox is on the repo's broken.json blocked list, with its reason",
                    ParseBlocked(File.ReadAllText("broken.json")).TryGetValue("3076804331", out var gb) && gb == ("glassbox", "glassboxfinal", "kicked every player off the server (2026-10-20)"));
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
            RemovedSeed = new() { [goneId] = Workshop.Removed }; RecomputeRemoved();
            Check("The bridge's catalog marks it", BuildCatalog().Maps.FirstOrDefault(x => x.Id == goneMap.Id) is { Removed: true });
            RemovedSeed = new(); Checked = new(); RecomputeRemoved();

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
            Talk(alice, "!map 3076999999");
            Check("...its new map can be picked at once", Pending?.Map.Id == 3076999999);
            File.WriteAllText(mapsPath, "[{\"rank\":1,");
            Check("A maps.json that does not parse changes nothing", ReloadMaps(mapsPath, force: false).Contains("unreadable") && Maps.Count == keepMaps.Count + 1);
            File.WriteAllText(mapsPath, JsonSerializer.Serialize(keepMaps.Take(10).ToList(), Ctx.Default.ListMapInfo));
            Check("...nor one that would cut the list by more than half", ReloadMaps(mapsPath, force: false).Contains("less than half") && Maps.Count == keepMaps.Count + 1);
            Check("...unless forced (POST /reload/<secret>?force)", ReloadMaps(mapsPath, force: true).Contains("10 maps") && Maps.Count == 10);
            Maps = keepMaps;

            Fresh();
            foreach (var p in new[] { alice, bob, cara }) Join(p);
            Talk(alice, $"!map {Rank(1)}"); Land(); Run(30);
            Talk(bob, $"!map {Rank(5)}"); Talk(cara, $"!map {Rank(6)}");
            Check("(Bob's and Cara's requests wait)", Upcoming().SequenceEqual([m[5], m[6]]));
            File.WriteAllText(brokenPath, $$"""{"maps":[{"id":{{m[2].Id}},"crashes":2}],"packs":[{"id":5000001,"name":"a skin pack"}],"removed":[{{m[5].Id}}],"blocked":[{"id":{{m[6].Id}},"name":"{{m[6].Name}}","reason":"froze the server"}]}""");
            Quiet(); reloaded = ReloadBroken(brokenPath); Run(10);
            Check("A new broken.json is taken while the bridge runs: its crashers, non-maps, removed and blocked maps, all at once",
                Broken.ContainsKey(m[2].Id.ToString()) && Packs.ContainsKey("5000001") && IsRemoved(m[5]) && IsBlocked(m[6])
                && reloaded.Contains("1 newly gone from the Workshop, 0 back, 1 newly blocked"));
            Check("...the requests for the maps that just became unplayable are dropped, and chat says why",
                Upcoming().Count == 0 && Said("is gone from the Workshop - its request is dropped.") && Said("is blocked now - its request is dropped."));
            Quiet(); Talk(bob, $"!map {Rank(6)}");
            Check("...and a map it blocks is refused at once, in its own words", Said("froze the server - it is blocked."));
            File.WriteAllText(brokenPath, "{\"maps\":[");
            Check("A broken.json that does not parse changes nothing", ReloadBroken(brokenPath).Contains("unreadable") && IsRemoved(m[5]) && IsBlocked(m[6]));

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
            ApplyRemovedReport(Workshop.EncodeReport([(m[3].Id, Workshop.Removed)])); WatchFiles(); WatchFiles();
            Check("The bridge's own write of removed.json is not read back", removedReads == 0 && IsRemoved(m[3]));
            File.WriteAllText(RemovedPath, JsonSerializer.Serialize(new RemovedFile { Checked = "2026-10-20T00:00:00Z" }, Ctx.Default.RemovedFile));
            File.SetLastWriteTimeUtc(RemovedPath, DateTime.UtcNow.AddMinutes(5));
            WatchFiles(); WatchFiles();
            Check("...but an edit by hand is, and takes effect", removedReads == 1 && !IsRemoved(m[3]));
            WatchStop();
            var asked = DataFiles(force: true).Select(f => f.Item1).ToList();
            Check("POST /reload reads every data file the watch reads - search.json among them (until 2026-11-01 it was marked as read without being read)",
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
            Broken = new(); Packs = new(); RemovedSeed = new(); Checked = new(); Removed = new(); Blocked = new();

            SelectionSelfTest(Check, Fresh, p => Join(p), (p, reason) => Leave(p, reason), Talk, Run, Said, Quiet, sent); // SelfTestSelection.cs

            // ---- the welcome panel, measured, and its timing as the plugin drives it ----
            var html = Guide.Html();
            Console.WriteLine("panel: " + html);
            Check($"The panel fits without scrolling: {Guide.Lines.Length} lines of at most {Guide.LineWidth} characters (widest {Guide.Lines.Max(l => l.Length)})",
                Guide.Lines.Length <= Guide.MaxLines && Guide.Lines.All(l => l.Length <= Guide.LineWidth));
            Check($"Its HTML is small ({html.Length} characters), uses only font, b and br, and points at the site and !help",
                html.Length <= 400 && Regex.Matches(html, "<(/?)([a-z]+)").All(t => t.Groups[2].Value is "font" or "b" or "br") && html.Contains(Site) && html.Contains("!help"));
            var panel = new GuidePanel();
            var t0 = clock;
            string? Shows(int slot, double at) => panel.Frame(t0.AddSeconds(at)).Where(f => f.Slot == slot).Select(f => f.Html).Cast<string?>().FirstOrDefault();
            panel.Request(3, alive: true, t0);
            Check("css_guide on a living player: the panel waits for the HUD, then shows", Shows(3, 1) == null && Shows(3, 2) == html);
            Check($"It stays {Guide.ShowSeconds} s, is cleared once, and then that player gets nothing", Shows(3, 1.5 + Guide.ShowSeconds - 0.5) == html
                && Shows(3, 1.5 + Guide.ShowSeconds) == GuidePanel.Clear && Shows(3, 30) == null && panel.Idle);
            panel.Command(4, "", t0); panel.Command(4, "off", t0.AddSeconds(1));
            Check("!info shows it again, !info off clears it", Shows(4, 1) == GuidePanel.Clear && panel.Idle);
            panel.Request(5, alive: false, t0);
            Check("A dead or spectating player gets it at their next spawn...", Shows(5, 5) == null && !panel.Idle);
            panel.Spawned(5, t0.AddSeconds(6));
            Check("...after the spawn delay", Shows(5, 7) == null && Shows(5, 8) == html);
            panel.Gone(5); panel.Request(6, alive: false, t0);
            Check($"...or after {GuidePanel.WaitLimitSeconds} s if they never spawn; a player who leaves is forgotten", Shows(6, GuidePanel.WaitLimitSeconds) == html && Shows(5, 30) == null);
            // The name and tagline at a real join (the owner, 2026-11-01): centre screen, a few seconds, the same clock as the panel.
            var tag = Guide.TaglineHtml();
            var tagPanel = new GuidePanel(tag, Guide.TaglineSeconds);
            string? Tags(int slot, double at) => tagPanel.Frame(t0.AddSeconds(at)).Where(f => f.Slot == slot).Select(f => f.Html).Cast<string?>().FirstOrDefault();
            tagPanel.Request(7, alive: true, t0);
            Check($"The tagline at a join: '{Guide.Name}' and '{Guide.Tagline}' after the spawn delay, for {Guide.TaglineSeconds} s, then cleared once",
                tag.Contains("OVER STRIKE 2") && tag.Contains("Over the top - On the Huis") && Regex.Matches(tag, "<(/?)([a-z]+)").All(t => t.Groups[2].Value is "font" or "b" or "br")
                && Tags(7, 1) == null && Tags(7, 2) == tag && Tags(7, 1.5 + Guide.TaglineSeconds - 0.5) == tag && Tags(7, 1.5 + Guide.TaglineSeconds) == GuidePanel.Clear
                && Tags(7, 9) == null && tagPanel.Idle && Guide.TaglineSeconds is >= 2 and <= 6 && Guide.Lines[0].Contains(Guide.Name) && Guide.Lines.Contains(Guide.Tagline));

            // ---- the HUD on screen, per player, on a handed clock (HudSlots; the plugin only sends what Frame changes) ----
            HudSlots.View At(HudSlots hs, int slot, double seconds) => hs.Frame(t0.AddSeconds(seconds)).FirstOrDefault(f => f.Slot == slot).View;
            var win = new HudSlots();
            win.Joined(1, first: true, alive: true, t0);
            Check("A first-time player who is alive: the window opens after the spawn delay, on its start page, and takes the mouse",
                !At(win, 1, 1).Window && At(win, 1, 1.6) is { Window: true, Page: HudSlots.Page.Start, Capture: true, Toast: 0 });
            Check("...and its drain bar starts a moment later", !At(win, 1, 1.7).Draining && At(win, 1, 1.9).Draining);
            win.Click(1, "gg_tab_rules", t0.AddSeconds(10));
            Check("A tab switches the page, restarts the drain bar and keeps the window up", At(win, 1, 10.1) is { Page: HudSlots.Page.Rules, Draining: false } && At(win, 1, 10.4).Draining);
            Check($"Left alone {HudSlots.IdleCloseSeconds} s after the last click it closes itself: the mouse is let go at once, the window fades out",
                At(win, 1, 10 + HudSlots.IdleCloseSeconds - 0.1).Capture && At(win, 1, 10 + HudSlots.IdleCloseSeconds + 0.1) is { Capture: false, WindowIn: false, Window: true }
                && !At(win, 1, 10 + HudSlots.IdleCloseSeconds + 0.5).Window);
            win.Info(1, "", t0.AddSeconds(60));
            Check("!info opens it again, on the start page, visible first and fully in a moment later (Panorama cannot fade what turns visible)",
                At(win, 1, 60.1) is { Window: true, WindowIn: false, Page: HudSlots.Page.Start } && At(win, 1, 60.4).WindowIn);
            win.Click(1, HudView.ButtonClose, t0.AddSeconds(61));
            Check("The close button closes it", At(win, 1, 61.1) is { Capture: false, WindowIn: false } && !At(win, 1, 61.5).Window);
            win.Info(1, "", t0.AddSeconds(62)); win.Info(1, "off", t0.AddSeconds(63));
            Check("!info off closes it too", !At(win, 1, 63.5).Window && win.Idle);
            win.Click(1, HudView.ButtonUp, t0.AddSeconds(64));
            Check("A click from a player without the window open does nothing", !At(win, 1, 64.1).Window);
            win.Info(1, "", t0.AddSeconds(70));
            var reading = At(win, 1, 70.5).Capture;
            win.Hurt(1, t0.AddSeconds(75));
            Check("Shot while reading: the window lets go of the mouse (and the player's movement) at once", reading && !At(win, 1, 75.1).Capture && !At(win, 1, 75.5).Window);
            var back = new HudSlots();
            back.Joined(2, first: false, alive: false, t0);
            Check("A returning player not yet spawned: nothing until they do...", At(back, 2, 5).Toast == 0);
            back.Spawned(2, t0.AddSeconds(6));
            Check($"...then the welcome-back card, never the window, for {HudSlots.ToastSeconds} s, fading in and out",
                At(back, 2, 7.6) is { Toast: 2, ToastIn: false, Window: false } && At(back, 2, 7.9).ToastIn && At(back, 2, 15.4) is { Toast: 2, ToastIn: true }
                && At(back, 2, 15.6) is { Toast: 2, ToastIn: false } && At(back, 2, 15.9).Toast == 0);
            back.Spawned(2, t0.AddSeconds(20));
            Check("...and their respawns show nothing", At(back, 2, 21.6).Toast == 0);
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
            S = new State(); Votes = new(); Players.Clear(); Queue.Clear(); Owners.Clear(); Airtime.Clear(); Pending = null; AnnouncePending = false;
            NewsItems.Clear(); CooldownUntil = default; MaintenanceOn = false; WatchStop(); ResetCrashWatch();
            try { Directory.Delete(dir, true); } catch (Exception e) { Console.WriteLine($"selftest: could not remove {dir}: {e.Message}"); }
        }
    }
}
