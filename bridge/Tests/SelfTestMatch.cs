// --selftest, the restarts a player can press and the match length (Restarts.cs, MatchLength.cs, Program.SetMatchLength, HudMenu.Game): the second click of Restart the server counts
// down on the countdown banner, naming who pressed it, then quits; Restart match counts down the same way, then sends os_restart; what either refuses and what ends them; and a new match
// length, each mode's own - minutes in Arms Race and Deathmatch, rounds in Wingman (release 199). Runs in the harness (SelfTestHarness.cs), on its own lists, with a
// fake game server whose RCON may fall silent. What only a real server can show - that mp_restartgame resets the ladder and that the round's time reaches the clients - is the client test's.
static partial class Program
{
    static void MatchSelfTest(Action<string, bool> Check)
    {
        var h = new Harness("match", new DateTime(2026, 11, 5, 19, 0, 0, DateTimeKind.Utc));
        var rconDown = false;
        string? lengthReply = null; // what the plugin answers to its command
        var (alice, bob) = (("Alice", "[U:1:4001]"), ("Bob", "[U:1:4002]"));
        var said = new List<string>(); // this test's own lines, for the capitals below
        var toldAll = ToldAll;
        string? restartReply = "os_restart: the match restarts in 1 s", bareReply = ""; // the plugin's answers to os_restart and to a bare mp_restartgame
        var phaseReply = ""; // the plugin's answer to os_phase: none, as from a plugin without it, unless a test says otherwise
        h.Server = command => rconDown ? null : command == MatchPhase.Command ? phaseReply : command.StartsWith(MatchLength.Command, StringComparison.Ordinal) ? lengthReply
            : command == "status" ? "hostname : test\nspawn    : 2\nplayers  : 2 humans, 0 bots (0 max)\n#end"
            : command == RematchCommand ? restartReply : command == RematchFallback ? bareReply : "";
        try
        {
            ToldAll = said;
            void Click((string Name, string Id) p, string command) => Command(p.Name, p.Id, command);
            void Twice((string Name, string Id) p, string command) { Click(p, command); Click(p, command); }
            bool InChat(string line) => SaysNow.Any(l => M.Plain(l.Text) == line);
            bool OnBoard(string line) => LatelyNow.Any(l => l.Text == line);
            HudState Live() => HudState.Decode(BuildHud(h.Clock).Encode(endTimes: true))!.AsOf(HudState.Ms(h.Clock)); // the live state as the plugin has it at this moment
            void Fresh() // a bridge that has just started (ResetAll), with the plugin drawing the HUD, on the lists below, Alice and Bob on
            {
                h.Fresh();
                HudActive = () => true; rconDown = false; lengthReply = null; (restartReply, bareReply) = ("os_restart: the match restarts in 1 s", ""); phaseReply = "";
                Maps = Enumerable.Range(1, 60).Select(r => new MapInfo(r, 9_000_000 + r, $"ar_{r}", 10 + r % 40)).ToList();
                ModeMaps.Clear();
                S.Map = Maps[20]; S.LogName = Maps[20].Name; S.Mode = Modes.ArmsRaceId;
                h.Join(alice); h.Join(bob);
                ClearNotices(); h.Sent.Clear();
            }

            // ---- Restart the server: the second click, the countdown, the quit ----
            Fresh();
            Click(alice, "!restart");
            Check("Restart the server: the first click asks, to that player alone, and nothing counts down", NoteFor.ContainsKey(4001) && h.Said("Click or type it again within 30 s") && RestartDue == null && !h.Sent.Contains("quit"));
            Click(alice, "!restart");
            var pressed = Live();
            Check("...the second click starts the countdown of a restart of kind player, naming who pressed it, in the live state as the plugin reads it",
                pressed is { RestartKind: PlayerKind, RestartWhen: "now", RestartBy: "Alice", RestartIn: PressedSeconds } && pressed.RestartAfter == "Back in a minute - Reconnect then (console: retry)" && !h.Sent.Contains("quit"));
            Check("...the countdown banner, the same as the restart counting down: the logo, who pressed it, what to do afterwards, the seconds",
                HudView.CountdownOf(pressed) is { Eyebrow: "Server", Title: "Alice is restarting the server", Line: "Back in a minute - Reconnect then (console: retry)", Logo: true, Seconds: PressedSeconds });
            var chatLine = $"Alice is restarting the server in {PressedSeconds} s - Back in a minute, reconnect then (console: retry)";
            Check($"...chat and the board keep the real seconds, the mark never: {chatLine}", InChat(chatLine) && OnBoard(chatLine) && !SaysNow.Any(l => l.Text.Contains(Hud.LeftToken)) && !LatelyNow.Any(l => l.Text.Contains(Hud.LeftToken)));
            Check("...its name in chat is marked for its team's colour, its seconds as a value", SaysNow.Any(l => l.Text == $"{M.N("Alice")} is restarting the server in {M.V($"{PressedSeconds} s")} - Back in a minute, reconnect then (console: retry)"));
            Check("...and the window's foot counts down from the end time, unchanged in the change-driven text",
                pressed.RestartWords.StartsWith($"Alice is restarting the server in {PressedSeconds} s")
                && BuildHud(h.Clock).Encode(endTimes: true) == BuildHud(h.Clock.AddSeconds(1)).Encode(endTimes: true));
            Check("...nobody can call a vote while it counts down", CannotCall(h.Clock) is { } noVote && noVote.StartsWith($"The server restarts in {PressedSeconds} s - No vote"));
            ClearNotices();
            Click(bob, "!restart");
            Check("...another player's click says it is coming; it starts nothing new", h.Said($"The server restarts in {PressedSeconds} s already") && RestartDue is { By: "Alice" });
            h.Run(PressedSeconds - 1);
            Check("...no quit before its end, and the banner counts", !h.Sent.Contains("quit") && Live().RestartIn == 1 && HudView.CountdownOf(Live())?.Seconds == 1);
            h.Run(1.5);
            Check("...then the quit, marked clean as it goes; the warning goes with it", h.Sent.Contains("quit") && S.CleanShutdown && RestartDue == null && Live().RestartKind == "" && S.Map == null);
            ClearNotices();
            Click(alice, "!restart");
            Check("...and the cooldown holds after it, with a capital after the dash", h.Said("Just restarted - Wait"));

            Fresh();
            Click(alice, "!restart");
            StartVote(bob.Item1, bob.Item2);
            var voteWasOpen = Ballot is { Open: true };
            Click(alice, "!restart");
            Check("A vote that is open ends at the second click: the server would go down under it", voteWasOpen && Ballot == null && RestartDue is { Kind: PlayerKind });
            Fresh();
            Click(alice, "!restart");
            StartChange(new Change(Maps[3], "Bob"), "Going");
            Click(alice, "!restart");
            var bannerNow = HudView.CountdownOf(Live());
            h.Run(PressedSeconds + 1);
            Check("A map change counting down dies with it: its countdown banner gives way, and its map command never goes out", bannerNow?.Eyebrow == "Server" && h.Sent.Contains("quit") && !h.Sent.Any(c => c.StartsWith("host_workshop_map")));
            Fresh();
            Twice(alice, "!restart");
            h.Line("Game Over: gungameprogressive mg_armsrace somemap score 0:0 after 9 min");
            Check("A match end while it counts down opens no vote: the server goes down under it", Ballot == null && Pending == null && RestartDue is { Kind: PlayerKind });
            h.Run(PressedSeconds + 1);
            Check("...and the quit still goes", h.Sent.Contains("quit"));

            // ---- the countdown's Cancel (its button counts down, and anybody who could press it may stop it): Restart the server ----
            Fresh();
            Twice(alice, "!restart");
            ClearNotices();
            Click(bob, "!cancel restart");
            Check("Cancel: Bob's click on the countdown (!cancel restart) stops Alice's restart of the server; the news line says who cancelled whose; the live state carries no restart",
                RestartDue == null && h.Said("Bob cancelled Alice's restart of the server - It plays on") && Live().RestartKind == "" && HudView.CountdownOf(Live()) == null);
            h.Run(PressedSeconds + 1);
            Check("...and the quit never goes", !h.Sent.Contains("quit") && !S.CleanShutdown);
            ClearNotices(); Twice(alice, "!restart");
            Check("...its cooldown went with it: a restart that never happened holds up no other", RestartDue is { Kind: PlayerKind, By: "Alice" } && !h.Said("Just restarted"));
            ClearNotices(); Click(alice, "!cancel restart");
            Check("...the presser may cancel too, in her own words", RestartDue == null && h.Said("Alice cancelled their restart of the server"));
            ClearNotices(); Click(bob, "!cancel restart");
            Check("...a Cancel with nothing counting down is answered and changes nothing", h.Said("No restart of the server counts down") && RestartDue == null);
            ApplyRestartNotice("os2-restart	1	daily	now	30");
            ClearNotices(); Click(bob, "!cancel restart");
            Check("...and the watcher's countdown is not the players' to cancel", RestartDue is { Kind: "daily" } && h.Said("No restart of the server counts down"));

            // ---- the watcher and a player's restart ----
            Fresh();
            Check("The watcher cannot name a player's restart", ApplyRestartNotice($"os2-restart\t1\t{PlayerKind}\tnow\t5").StartsWith("refused: the kind is") && ApplyUpdated($"os2-updated\t1\t{PlayerKind}\t").StartsWith("refused: the kind is") && RestartDue == null);
            Twice(alice, "!restart");
            Check("...nor take one back", ApplyRestartNotice($"os2-restart\t1\tcancel\t{PlayerKind}").StartsWith("refused") && RestartDue is { Kind: PlayerKind });
            Check("A player's countdown stands whatever the watcher sends: a notice, a reminder, a cancel of its own, the note of an update",
                ApplyRestartNotice("os2-restart\t1\tdaily\tnow\t10").StartsWith("ok: daily restart: a player's restart goes first") && ApplyRestartNotice("os2-restart\t1\tdaily\tmatch-end\t10").StartsWith("ok")
                && ApplyRestartNotice("os2-restart\t1\tcancel\tdaily").StartsWith("refused") && ApplyUpdated("os2-updated\t1\tdaily\t").StartsWith("ok") && RestartDue is { Kind: PlayerKind, By: "Alice" });
            h.Run(PressedSeconds + 1);
            Check("...and its quit goes", h.Sent.Contains("quit"));
            Fresh();
            ApplyRestartNotice("os2-restart\t1\tdaily\tnow\t30");
            ClearNotices();
            Twice(alice, "!restart");
            Check("The watcher's countdown is not replaced by a player's", h.Said("The server restarts in 30 s already") && RestartDue is { Kind: "daily" } && !h.Sent.Contains("quit"));
            Fresh();
            ApplyRestartNotice("os2-restart\t1\tdaily\tmatch-end\t10");
            Twice(alice, "!restart");
            Check("A restart that waits for the match's end gives way to a player's", RestartDue is { Kind: PlayerKind, GoesAt: not null });

            // ---- every restart's lines: the real seconds in chat and on the board, the right row, capitals ----
            foreach (var (kind, why, after) in RestartKinds)
            {
                Fresh();
                ApplyRestartNotice($"os2-restart\t1\t{kind}\tmatch-end\t10");
                var waiting = $"{why}: the server restarts at the end of this match - {after}";
                var waits = Live();
                var isUpdate = kind == "game-update";
                Check($"{kind}, waiting for the match's end: said once with its row, and {(isUpdate ? "the top line holds it, the banner does not" : "the banner holds it")}",
                    InChat(waiting) && OnBoard(waiting) && waits.RestartWords == waiting
                    && (isUpdate ? waits.BanHead == "" && waits.TopText == waiting : waits.BanHead == "The server restarts after this match" && waits.BanLine == $"{why} - {after}" && waits.TopText == ""));
                if (isUpdate)
                {
                    h.Run(Messages.DefaultSeconds * 2);
                    Announce("Something newer", 4);
                    var covered = Live().TopText == "Something newer";
                    h.Run(5);
                    Check("...a newer line covers the top line while it lasts, and it comes back until the restart", covered && Live().TopText == waiting);
                }
                h.Line("Game Over: gungameprogressive mg_armsrace somemap score 0:0 after 9 min");
                var counting = $"{why}: the server restarts in 10 s - {after}";
                var counts = Live();
                Check($"{kind}, at the match's end: the countdown, with the real seconds in chat and on the board{(isUpdate ? "; the top line says what to do, without a number that would stand still" : "")}",
                    InChat(counting) && OnBoard(counting) && counts is { RestartIn: 10 } && counts.BanHead == "" && !SaysNow.Any(l => l.Text.Contains(Hud.LeftToken)) && !LatelyNow.Any(l => l.Text.Contains(Hud.LeftToken))
                    && (!isUpdate || counts.TopText == $"{why}: the server restarts for it - {after}") && HudView.CountdownOf(counts) is { Eyebrow: "Server", Logo: true, Seconds: 10 });
                ClearNotices();
            }
            Fresh();
            ApplyUpdated("os2-updated\t1\tgame-update\t1.41.9.4\t1.41.9.5");
            Check("Updated to ...: the top line, chat and the board for a minute, with a capital after the dash; the value marked for chat",
                Live().TopText == "Updated to CS2 1.41.9.5 - Welcome back" && Live().TopUntilMs == HudState.Ms(h.Clock.AddSeconds(Messages.Updated.Seconds))
                && OnBoard("Updated to CS2 1.41.9.5 - Welcome back") && SaysNow.Any(l => l.Text == $"Updated to {M.V("CS2 1.41.9.5")} - Welcome back"));
            ClearNotices();
            TellUpdated(alice.Item2);
            Check("...and a joiner gets it as a line of their own, in their chat and on their board", NoteFor.ContainsKey(4001) && !NoteFor.ContainsKey(4002) && SaysNow.Single().Account == 4001 && LatelyNow.Single().Account == 4001);

            // ---- Restart match: the second click, the countdown, os_restart ----
            Fresh();
            var onMap = S.Map;
            Click(alice, "!rematch");
            Check("Restart match: the first click asks, to that player alone, and nothing counts down", NoteFor.ContainsKey(4001) && h.Said("Click or type it again within 30 s") && RematchDue == null && Live().RematchIn == -1);
            Click(alice, "!rematch");
            var rematch = Live();
            Check("...the second click starts its countdown, on the wire and in the banner: the logo, who pressed it, the seconds",
                rematch is { RematchBy: "Alice", RematchIn: PressedSeconds } && rematch.RematchAtMs == HudState.Ms(h.Clock.AddSeconds(PressedSeconds)) && !h.Sent.Contains(RematchCommand)
                && HudView.CountdownOf(rematch) is { Eyebrow: "Match", Title: "Alice is restarting the match", Line: "The whole match starts over", Logo: true, Seconds: PressedSeconds });
            var rematchLine = $"Alice is restarting the match in {PressedSeconds} s - The ladder, the time and catch-up start over";
            Check($"...and chat and the board say it for everybody, naming what starts over: {rematchLine}", InChat(rematchLine) && OnBoard(rematchLine) && SaysNow.Any(l => l.Account == 0 && l.Text.StartsWith(M.N("Alice"), StringComparison.Ordinal)));
            Check("...the state stands still in the change-driven text while it counts down", BuildHud(h.Clock).Encode(endTimes: true) == BuildHud(h.Clock.AddSeconds(1)).Encode(endTimes: true));
            Check("...nobody can call a vote while it counts down", CannotCall(h.Clock) is { } noVote2 && noVote2.StartsWith($"The match restarts in {PressedSeconds} s - No vote"));
            ClearNotices();
            Click(bob, "!rematch");
            Check("...another click says who pressed it and how long is left", h.Said($"Alice is restarting the match already - It goes in {PressedSeconds} s") && RematchDue is { By: "Alice" });
            h.Run(PressedSeconds - 1);
            Check("...nothing goes out before its end, and the banner counts", !h.Sent.Contains(RematchCommand) && Live().RematchIn == 1);
            h.Run(1.5);
            Check("...then os_restart 1 over RCON - the plugin's restart, which its guard lets through - and nothing else; the countdown is over",
                h.Sent.Contains(RematchCommand) && !h.Sent.Contains(RematchFallback) && RematchDue == null && Live().RematchIn == -1 && HudView.CountdownOf(Live()) == null);
            Check("...the map, its picker, the mode and a clean state stay: this is no restart of the server, and no map change", S.Map == onMap && !S.CleanShutdown && !h.Sent.Contains("quit") && !h.Sent.Any(c => c.StartsWith("changelevel") || c.StartsWith("host_workshop_map")) && !LoadPending);
            ClearNotices();
            Click(bob, "!rematch");
            Check($"...the next one waits: {RematchWaitSeconds} s from the second click", h.Said("The match was just restarted - Wait") && RematchConfirmBy == "");
            h.Run(RematchWaitSeconds);
            Click(bob, "!rematch");
            Check("...and then it may be pressed again, by anyone", RematchConfirmBy == bob.Item2);

            // ---- the countdown's Cancel: Restart match ----
            Fresh();
            Twice(alice, "!rematch");
            ClearNotices();
            Click(bob, "!cancel rematch");
            Check("Cancel: Bob's click on the countdown (!cancel rematch) stops Alice's restart of the match, and the news line says so; the live state carries none",
                RematchDue == null && h.Said("Bob cancelled Alice's restart of the match - It plays on") && Live().RematchIn == -1 && HudView.CountdownOf(Live()) == null);
            h.Run(PressedSeconds + 1);
            Check("...nothing goes out, and its wait went with it: it may be pressed again at once", !h.Sent.Contains(RematchCommand) && RematchWaitUntil == default);
            ClearNotices(); Twice(bob, "!rematch");
            Check("...as Bob does", RematchDue is { By: "Bob" } && !h.Said("The match was just restarted"));
            RematchDue = new Rematch("Cara", h.Clock.AddSeconds(5), Fair: true);
            ClearNotices(); Click(alice, "!cancel rematch");
            Check("...the fairness restart is no button's: a Cancel leaves it", RematchDue is { Fair: true } && h.Said("No restart of the match counts down"));

            // ---- what Restart match refuses, and what ends it ----
            Fresh();
            Click(alice, "!restart"); Click(alice, "!restart");
            ClearNotices();
            Twice(bob, "!rematch");
            Check("Refused while the server's restart counts down", h.Said($"The server restarts in {PressedSeconds} s - No restart of the match") && RematchDue == null);
            Fresh();
            StartChange(new Change(Maps[3], "Bob"), "Going");
            ClearNotices();
            Twice(alice, "!rematch");
            Check("Refused while a map change counts down", h.Said("Already changing to") && h.Said("- Wait for it") && RematchDue == null);
            Fresh();
            LoadPending = true; LoadUntil = h.Clock.AddMinutes(5);
            ClearNotices();
            Twice(alice, "!rematch");
            Check("Refused while a map loads", h.Said("The next map is still loading - Wait for it") && RematchDue == null);
            Fresh();
            StartVote(bob.Item1, bob.Item2);
            var voteOpen = Ballot is { Open: true };
            ClearNotices();
            Twice(alice, "!rematch");
            Check("Refused while a vote is on", voteOpen && h.Said("The vote for the next map is on - It decides first") && RematchDue == null);
            Fresh();
            h.MatchEnd(); EndBallot("the test's"); // a match end, its vote out of the way: what is left is the screen
            ClearNotices();
            Twice(alice, "!rematch");
            Check("Refused right after a match's end, with a plugin that cannot say whether its screen is up (no os_phase): the 30 s after it, as before - the next one starts by itself",
                h.Said("This match has just ended - The next one starts by itself") && RematchDue == null && h.Sent.Contains(MatchPhase.Command));
            h.Run(31);
            ClearNotices(); Twice(alice, "!rematch");
            Check("...and after those 30 s it goes", RematchDue is { By: "Alice" });
            Fresh();
            h.MatchEnd(); EndBallot("the test's");
            phaseReply = MatchPhase.Reply(MatchPhase.Ended);
            h.Run(40);
            ClearNotices(); Twice(alice, "!rematch");
            Check("The plugin's os_phase says the match-end screen is up: refused, also past the 30 s - a screen the vote did not cut runs 90 s, and the game would ignore the restart",
                h.Said("This match has just ended - The next one starts by itself") && RematchDue == null && EndScreenAt != null);
            phaseReply = MatchPhase.Reply(1);
            ClearNotices(); Twice(alice, "!rematch");
            Check("...the moment it says the next match is on, Restart match goes, and the screen is asked about no more",
                RematchDue is { By: "Alice" } && EndScreenAt == null);
            Fresh();
            h.MatchEnd(); EndBallot("the test's");
            phaseReply = MatchPhase.Reply(1);
            h.Run(12);
            ClearNotices(); Twice(alice, "!rematch");
            Check("...also inside the 30 s: a next match 12 s after the end - the screen cut short and everybody back on their side - is no match that has just ended",
                RematchDue is { By: "Alice" });
            Fresh();
            h.MatchEnd(); EndBallot("the test's");
            phaseReply = MatchPhase.Reply(MatchPhase.Ended);
            h.Run(EndScreenMostSeconds);
            ClearNotices(); Twice(alice, "!rematch");
            Check($"...but a screen is never taken as up past {EndScreenMostSeconds} s after the match end", RematchDue is { By: "Alice" } && EndScreenAt == null);
            Check("os_phase's replies read back - the phase, or none: no game rules, a plugin without it, no answer",
                MatchPhase.Read(MatchPhase.Reply(5)) == 5 && MatchPhase.Read(MatchPhase.Reply(1)) == 1 && MatchPhase.Read(MatchPhase.NoRules) == null
                && MatchPhase.Read($"Unknown command '{MatchPhase.Command}'!") == null && MatchPhase.Read(null) == null && MatchPhase.Read("") == null);
            Fresh();
            Twice(alice, "!rematch");
            h.Line("Game Over: gungameprogressive mg_armsrace somemap score 0:0 after 9 min");
            var overBy = RematchDue;
            h.Run(PressedSeconds + 1);
            Check("A match end while it counts down ends it: the match has ended by itself, and the vote opens as ever", overBy == null && !h.Sent.Contains(RematchCommand) && Ballot != null);
            Fresh();
            Twice(alice, "!rematch");
            h.Line("Loading map \"ar_other\"");
            var mapBy = RematchDue;
            h.Run(PressedSeconds + 1);
            Check("A map start while it counts down ends it", mapBy == null && !h.Sent.Contains(RematchCommand));
            Fresh();
            Twice(alice, "!rematch");
            ServerGoingDown("a test");
            var downBy = RematchDue;
            h.Run(PressedSeconds + 1);
            Check("The server going down ends it", downBy == null && !h.Sent.Contains(RematchCommand));
            Fresh();
            Twice(alice, "!rematch");
            Click(bob, "!restart"); Click(bob, "!restart");
            var pressedOver = RematchDue;
            h.Run(PressedSeconds + 1);
            Check("A restart of the server pressed while it counts down ends it", pressedOver == null && !h.Sent.Contains(RematchCommand) && h.Sent.Contains("quit"));
            Fresh();
            Twice(alice, "!rematch");
            rconDown = true;
            ClearNotices();
            h.Run(PressedSeconds + 1);
            rconDown = false;
            Check("RCON not answering: the line for everybody says it did not restart", h.Said("The server did not answer - The match did not restart") && RematchDue == null);

            // ---- the fairness restart (EarlyJoin.cs): the plugin's ask on /act for every join, the countdown, os_restart; joins folding in; the refusals that hold ----
            void Fair((string Name, string Id) p) => HudAct($"{p.Id}\t{p.Name}\t{EarlyJoin.Command}");
            var cara = ("Cara", "[U:1:2003]");
            var dan = ("Dan", "[U:1:2004]");
            Fresh();
            Fair(bob);
            var fair = Live();
            Check($"The fairness restart: the plugin's ask for Bob counts down {EarlyJoin.CountdownSeconds} s on the countdown banner - the logo, who came in, the seconds - and nothing goes out before",
                fair is { RematchBy: "Bob", RematchFair: true, RematchCount: 1, RematchIn: EarlyJoin.CountdownSeconds } && fair.RematchAtMs == HudState.Ms(h.Clock.AddSeconds(EarlyJoin.CountdownSeconds))
                && !h.Sent.Contains(RematchCommand)
                && HudView.CountdownOf(fair) is { Eyebrow: "Match", Title: "The match restarts for fairness - Bob is in", Line: "The whole match starts over", Logo: true, Seconds: EarlyJoin.CountdownSeconds });
            var fairLine = $"The match restarts for fairness in {EarlyJoin.CountdownSeconds} s - Bob is in";
            Check($"...chat and the board say it for everybody: {fairLine}", InChat(fairLine) && OnBoard(fairLine));
            h.Run(2);
            ClearNotices();
            Fair(cara);
            Fair(cara); // the same join twice (a click repeated): named once
            fair = Live();
            Check("...Cara joining while it counts down folds into it: one restart, at the same moment, and the banner and a line name them both",
                fair is { RematchBy: "Bob and Cara", RematchFair: true, RematchCount: 2 } && fair.RematchAtMs == HudState.Ms(h.Clock.AddSeconds(EarlyJoin.CountdownSeconds - 2))
                && HudView.CountdownOf(fair) is { Title: "The match restarts for fairness - Bob and Cara are in" }
                && InChat($"The match restarts for fairness in {EarlyJoin.CountdownSeconds - 2} s - Bob and Cara are in"));
            Click(alice, "!rematch");
            Click(alice, "!rematch");
            Check("...a player's Restart match meanwhile says the fairness restart comes already", h.Said("The match restarts for fairness already - It goes in") && RematchDue is { Fair: true });
            h.Run(EarlyJoin.CountdownSeconds);
            Check("...then the plugin's os_restart goes out once, as Restart match's does, and the banner is gone",
                h.Sent.Count(c => c == RematchCommand) == 1 && RematchDue == null && Live().RematchIn == -1 && HudView.CountdownOf(Live()) == null);
            h.Sent.Clear();
            ClearNotices();
            Fair(dan);
            Check("...Dan joining as it goes out folds into it too: the game is starting everybody over, so nothing more counts down", RematchDue == null && !SaysNow.Any());
            h.Run(5);
            Fair(dan);
            Check("...not once per map: the next join after that is a restart of its own",
                RematchDue is { Fair: true, By: "Dan" } && Live() is { RematchBy: "Dan", RematchCount: 1 });
            h.Run(EarlyJoin.CountdownSeconds + 1);
            Check("...and goes out as the first did", h.Sent.Count(c => c == RematchCommand) == 1 && RematchDue == null);
            ClearNotices(); h.Sent.Clear();
            Twice(alice, "!rematch");
            Check("...and starts no wait: a player's Restart match right after a fairness restart goes - only a pressed one makes the next one wait",
                RematchDue is { Fair: false, By: "Alice" } && !h.Said("The match was just restarted") && RematchWaitUntil > h.Clock);
            Fresh();
            StartVote(bob.Item1, bob.Item2);
            ClearNotices();
            Fair(alice);
            Check("The fairness restart: none while a vote is on - nobody is told", Ballot is { Open: true } && RematchDue == null && !SaysNow.Any());
            Fresh();
            Twice(alice, "!rematch");
            ClearNotices(); h.Sent.Clear();
            Fair(bob);
            Check("...a join while a player's Restart match counts down folds into that one: it starts Bob over too, and nothing else counts down",
                RematchDue is { Fair: false } && !SaysNow.Any());
            h.Run(PressedSeconds + 1);
            ClearNotices(); h.Sent.Clear();
            h.Run(5);
            Fair(bob);
            Check($"...but the minute after a Restart match, which a player's next Restart match waits, does not hold a join's: Bob restarts it", RematchDue is { Fair: true, By: "Bob" });
            Fresh();
            S.Mode = Modes.WingmanId; S.Map = new MapInfo(0, 0, "de_inferno", 0, true, Modes.WingmanId);
            Fair(bob);
            Check("...in Wingman too, like Arms Race and Deathmatch", RematchDue is { Fair: true, By: "Bob" });
            Fresh();
            h.MatchEnd(); EndBallot("the test's");
            phaseReply = MatchPhase.Reply(1);
            h.Run(15);
            Fair(cara);
            Check("...and in a next match on the same map 15 s after the match end - the screen cut short - a join restarts it: the plugin's os_phase says the match is on",
                RematchDue is { Fair: true, By: "Cara" });
            Fresh();
            h.MatchEnd(); EndBallot("the test's");
            phaseReply = MatchPhase.Reply(MatchPhase.Ended);
            Fair(cara);
            Check("...while a join the plugin asks about with the screen still up restarts nothing", RematchDue == null);
            Fresh();
            Fair(bob);
            Fair(cara);
            var older = string.Join('\n', BuildHud(h.Clock).Encode(endTimes: true).Split('\n').Select(l => l.StartsWith("rematch\t", StringComparison.Ordinal)
                ? string.Join('\t', l.Split('\t').Take(3)) : l));
            var olderPlugin = HudState.Decode(older)!;
            Check("The fairness restart on the wire: the rematch line's third field, and its fourth, the count of names - a line without them (an older bridge) is a player's Restart match by one",
                Live() is { RematchFair: true, RematchCount: 2 } && olderPlugin is { RematchFair: false, RematchBy: "Bob and Cara", RematchCount: 1 } && olderPlugin.RematchAtMs > 0);

            // ---- a plugin without os_restart, a restart the game ignores, no answer at all ----
            Fresh();
            restartReply = "Unknown command 'os_restart'!";
            Twice(alice, "!rematch");
            h.Run(PressedSeconds + 1);
            Check("A plugin that has no os_restart (5.4.1 and older): its Unknown command, then mp_restartgame 1 as before - and nobody is told of a failure",
                h.Sent.Where(c => c == RematchCommand || c == RematchFallback).SequenceEqual([RematchCommand, RematchFallback]) && !h.Said("The match did not restart"));
            Fresh();
            (restartReply, bareReply) = ("Unknown command 'os_restart'!", "SV: Convar 'mp_restartgame' is cheat protected, change ignored.");
            Twice(alice, "!rematch");
            h.Run(PressedSeconds + 1);
            Check("A restart the game ignores (mp_restartgame cheat protected): everybody is told it did not restart, not left with a countdown that came to nothing",
                h.Sent.Contains(RematchFallback) && h.Said("The server refused the restart - The match did not restart"));
            Fresh();
            Twice(alice, "!rematch");
            rconDown = true;
            h.Run(PressedSeconds + 1);
            Check("No answer at all: everybody is told the server did not answer", h.Said("The server did not answer - The match did not restart"));
            rconDown = false;

            // ---- in the other modes ----
            foreach (var (mode, words) in new[] { (Modes.WingmanId, "The rounds and the scores start over"), (Modes.DeathmatchId, "The time and the scores start over") })
            {
                Fresh();
                S.Mode = mode;
                Twice(alice, "!rematch");
                var line = $"Alice is restarting the match in {PressedSeconds} s - {words}";
                h.Run(PressedSeconds + 1);
                Check($"{mode}: Restart match works there too, in that mode's words, and the same command goes out", OnBoard(line) && h.Sent.Contains(RematchCommand));
                if (mode == Modes.WingmanId)
                    Check($"{mode}: ...with Wingman's count of rounds right before it: the match it starts over plays the chosen count whole",
                        h.Sent.IndexOf($"mp_maxrounds {MatchLength.LongRounds}") is >= 0 and var maxAt && h.Sent.IndexOf(RematchCommand) == maxAt + 1);
                h.Sent.Clear(); ClearNotices();
                (S.RoundMinutes, S.DmRoundMinutes, S.WmRounds) = (DefaultRoundMinutes, DefaultRoundMinutes, MatchLength.LongRounds);
                if (mode == Modes.WingmanId)
                {
                    // Wingman (release 199): a match of rounds, whose length is how many - its own, 16 (Valve's) to begin with, 8 the short one
                    var (shortRounds, longRounds) = (MatchLength.ShortRounds, MatchLength.LongRounds);
                    Check($"{mode}: its own match length in rounds, {longRounds} until a player chooses, on the wire as rounds and with nobody named",
                        Live().Rounds == longRounds && Live().RoundMinutes == 0 && Live().RoundBy == "" && MatchLength.Default(mode) == longRounds && MatchLength.InRounds(mode)
                        && !MatchLength.OneRound(mode) && new State().WmRounds == longRounds && MatchLength.Choices(mode) == (shortRounds, longRounds));
                    Click(alice, $"!time {longRounds}");
                    Check($"{mode}: !time {longRounds} there is no change", h.Said($"Matches are already {longRounds} rounds") && h.Sent.Count == 0);
                    Click(alice, $"!time {ShortRoundMinutes}");
                    Check($"{mode}: minutes are no length there - refused in its own count, nothing sent, nothing stored",
                        h.Said($"Wingman's matches are {shortRounds} or {longRounds} rounds") && h.Sent.Count == 0 && S.WmRounds == longRounds);
                    foreach (var (result, effect) in new (MatchLength.Result?, string)[] { (MatchLength.Result.Set, "This match too"), (MatchLength.Result.NoMatch, "From this match on"),
                                 (MatchLength.Result.Later, "From the next match"), (MatchLength.Result.NotHere, "From the next match"), (null, "From the next match") })
                    {
                        h.Sent.Clear(); ClearNotices();
                        (S.WmRounds, S.WmRoundsBy) = (longRounds, null);
                        lengthReply = result is { } r ? MatchLength.Reply(r, "details") : "Unknown command";
                        Click(alice, $"!time {shortRounds}");
                        var wmWords = $"Alice set the match length to {shortRounds} rounds - {effect}";
                        Check($"{mode}: !time {shortRounds}, the plugin answering {result?.ToString() ?? "nothing it knows"}: only the plugin is asked - it sets mp_maxrounds where no halftime is at stake - and it is Wingman's own, named; for everybody: {wmWords}",
                            h.Sent.Contains($"{MatchLength.Command} {shortRounds}") && !h.Sent.Any(x => x.StartsWith("mp_maxrounds", StringComparison.Ordinal)) && !h.Sent.Any(x => x.StartsWith("mp_roundtime", StringComparison.Ordinal))
                            && S.WmRounds == shortRounds && S.WmRoundsBy == "Alice" && (S.RoundMinutes, S.DmRoundMinutes) == (DefaultRoundMinutes, DefaultRoundMinutes)
                            && Live().Rounds == shortRounds && Live().RoundBy == "Alice" && Live().TopText == wmWords);
                    }
                    lengthReply = null;
                    h.Sent.Clear();
                    h.MatchEnd();
                    Check($"{mode}: a match's end sends the chosen {shortRounds}: the next match - a kept map's starts without a load - plays it from its first round",
                        h.Sent.Contains($"mp_maxrounds {shortRounds}"));
                    h.Sent.Clear();
                    h.Line($"Loading map \"{S.Map!.Name}\"");
                    h.Run(3);
                    Check($"{mode}: a new map gets the chosen {shortRounds} 2 s after its start, after the mode's cfg has set its {longRounds}", h.Sent.Contains($"mp_maxrounds {shortRounds}"));
                    Click(alice, "!time");
                    Check($"{mode}: a bare !time flips it back to {longRounds}, its own, and the Game page names nobody again",
                        S.WmRounds == longRounds && S.WmRoundsBy == null && Live().RoundBy == "" && Live().Rounds == longRounds);
                    h.Sent.Clear();
                    h.Line($"Loading map \"{Maps[22].Name}\"");
                    h.Run(3);
                    Check($"{mode}: ...and with its own {longRounds} on, a load sends no count: the mode's cfg has set it", !h.Sent.Any(x => x.StartsWith("mp_maxrounds", StringComparison.Ordinal)));
                    continue;
                }
                // Deathmatch (release 197): one long round, as Arms Race - its own length, 60 minutes to begin with, as Arms Race's (Valve's is 10)
                Check($"{mode}: its own match length, {DefaultRoundMinutes} minutes until a player chooses, on the wire and with nobody named",
                    Live().RoundMinutes == DefaultRoundMinutes && Live().RoundBy == "" && MatchLength.Default(mode) == DefaultRoundMinutes && MatchLength.OneRound(mode) && Live().Rounds == 0
                    && new State().DmRoundMinutes == DefaultRoundMinutes);
                Click(alice, $"!time {DefaultRoundMinutes}");
                Check($"{mode}: !time {DefaultRoundMinutes} there is no change", h.Said($"Matches are already {DefaultRoundMinutes} minutes") && h.Sent.Count == 0);
                Click(alice, $"!time {ShortRoundMinutes}");
                var dmWords = $"Alice set the match length to {ShortRoundMinutes} minutes";
                Check($"{mode}: !time {ShortRoundMinutes} sets Deathmatch's own - mp_roundtime and the round that is on - named on the Game page; Arms Race's stays as it was",
                    h.Sent.IndexOf($"mp_roundtime {ShortRoundMinutes}") is var dmFirst and >= 0 && h.Sent.IndexOf($"{MatchLength.Command} {ShortRoundMinutes}") == dmFirst + 1
                    && S.DmRoundMinutes == ShortRoundMinutes && S.DmRoundBy == "Alice" && S.RoundMinutes == DefaultRoundMinutes && S.RoundBy == null
                    && Live().RoundMinutes == ShortRoundMinutes && Live().RoundBy == "Alice" && h.Said(dmWords));
                h.Sent.Clear();
                h.Line($"Loading map \"{S.Map!.Name}\"");
                h.Run(3);
                Check($"{mode}: a new map puts the chosen {ShortRoundMinutes} back after the mode's cfg has run, 2 s after its start", h.Sent.Contains($"mp_roundtime {ShortRoundMinutes}"));
                Click(alice, "!time");
                Check($"{mode}: a bare !time flips it back to {DefaultRoundMinutes}, its own, and the Game page names nobody again",
                    S.DmRoundMinutes == DefaultRoundMinutes && S.DmRoundBy == null && Live().RoundBy == "" && h.Sent.Contains($"mp_roundtime {DefaultRoundMinutes}"));
                h.Sent.Clear();
                h.Line($"Loading map \"{Maps[22].Name}\""); // another map: a second start of the same one at once is the same start
                h.Run(3);
                Check($"{mode}: ...and its own {DefaultRoundMinutes} goes out after every Deathmatch load too - a server whose Deathmatch cfg is still Valve's 10 gets 60 all the same",
                    h.Sent.Contains($"mp_roundtime {DefaultRoundMinutes}"));
            }

            // ---- the match length, for the match that is on ----
            Check("The round's time: from the round's start, the time played counting; at least a minute from now",
                MatchLength.RoundSeconds(10, 0) == 600 && MatchLength.RoundSeconds(60, 900) == 3600 && MatchLength.RoundSeconds(10, 530) == 600 && MatchLength.RoundSeconds(10, 540) == 600
                && MatchLength.RoundSeconds(10, 540.5) == 601 && MatchLength.RoundSeconds(10, 600) == 660 && MatchLength.RoundSeconds(10, 900) == 960 && MatchLength.RoundSeconds(10, -3) == 600);
            Check("...which is the match that is on as asked, or ending soon",
                MatchLength.Of(10, 0) == MatchLength.Result.Set && MatchLength.Of(60, 900) == MatchLength.Result.Set && MatchLength.Of(10, 540) == MatchLength.Result.Set
                && MatchLength.Of(10, 541) == MatchLength.Result.EndsSoon && MatchLength.Of(10, 900) == MatchLength.Result.EndsSoon);
            Check("Wingman's count takes the match that is on only before the halftime of either count: fewer rounds played than half the shorter one; a count of 0 has none",
                MatchLength.ThisMatch(0, 16, 8) && MatchLength.ThisMatch(3, 16, 8) && !MatchLength.ThisMatch(4, 16, 8) && MatchLength.ThisMatch(3, 8, 16) && !MatchLength.ThisMatch(4, 8, 16)
                && !MatchLength.ThisMatch(9, 16, 8) && MatchLength.ThisMatch(7, 0, 16) && !MatchLength.ThisMatch(8, 0, 16));
            var results = new[] { MatchLength.Result.Set, MatchLength.Result.EndsSoon, MatchLength.Result.NoMatch, MatchLength.Result.NotHere, MatchLength.Result.Later };
            Check("The plugin's reply says what it did, and is read back; anything else is no word of its", results.All(r => MatchLength.Read("noise\n" + MatchLength.Reply(r, "details") + "\n") == r)
                && MatchLength.Read(null) == null && MatchLength.Read("") == null && MatchLength.Read("Unknown command") == null);
            foreach (var (result, effect) in new (MatchLength.Result?, string)[] { (MatchLength.Result.Set, "This match too"), (MatchLength.Result.EndsSoon, $"This match ends in {MatchLength.LeastSeconds} s"), (MatchLength.Result.NoMatch, "From this match on"), (null, "From the next match") })
            {
                Fresh();
                lengthReply = result is { } r ? MatchLength.Reply(r, "details") : "Unknown command";
                Click(alice, $"!time {ShortRoundMinutes}");
                var words = $"Alice set the match length to {ShortRoundMinutes} minutes - {effect}";
                var first = h.Sent.IndexOf($"mp_roundtime {ShortRoundMinutes}");
                Check($"!time, the plugin answering {result?.ToString() ?? "nothing it knows"}: mp_roundtime for the rounds to come, then the plugin for the one that is on; for everybody: {words}",
                    first >= 0 && h.Sent.IndexOf($"{MatchLength.Command} {ShortRoundMinutes}") == first + 1 && S.RoundMinutes == ShortRoundMinutes && S.RoundBy == "Alice"
                    && Live().TopText == words && InChat(words) && OnBoard(words) && Live().RoundMinutes == ShortRoundMinutes);
            }
            Fresh();
            rconDown = true;
            Click(alice, $"!time {ShortRoundMinutes}");
            rconDown = false;
            Check("RCON not answering: stored, for the next map's load, and it says so", S.RoundMinutes == ShortRoundMinutes && h.Said("From the next match"));
            Fresh();
            lengthReply = MatchLength.Reply(MatchLength.Result.Set, "details");
            Click(alice, $"!time {DefaultRoundMinutes}");
            Check("The length that is set already: said to that player, and nothing goes out", NoteFor.ContainsKey(4001) && h.Said($"Matches are already {DefaultRoundMinutes} minutes") && h.Sent.Count == 0);
            Click(alice, "!time 20");
            Check("A length there is no button for: refused", h.Said($"Matches are {ShortRoundMinutes} or {DefaultRoundMinutes} minutes") && h.Sent.Count == 0);

            // ---- the menu's side: the buttons, what they say, what they send ----
            var browser = new HudBrowser();
            var page = HudMenu.Game(new HudState { RoundMinutes = 10, RoundBy = "Bob" }, 0, 0);
            string T(string id) => page.Texts.Single(x => x.Id == id).Text;
            Check("Game page: both restarts say what they are, unarmed; a length not the default says who set it, with a capital",
                T("os_set_rematch_text") == "Restart the match" && T("os_set_restart_text") == "Restart the server" && T("os_set_time_now") == "Matches last 10 minutes - Set by Bob"
                && !page.Classes.Single(x => x.Id == HudMenu.Rematch && x.Class == "armed").On && !page.Classes.Single(x => x.Id == HudMenu.Restart && x.Class == "armed").On);
            page = HudMenu.Game(new HudState(), 0, 7);
            Check("...Restart match after its first click: press again, its seconds, armed; the other button is not",
                T("os_set_rematch_text") == "Press again within 7 s" && page.Classes.Single(x => x.Id == HudMenu.Rematch && x.Class == "armed").On && T("os_set_restart_text") == "Restart the server" && !page.Classes.Single(x => x.Id == HudMenu.Restart && x.Class == "armed").On);
            var wingman = HudMenu.Game(new HudState { Rounds = 8, RoundBy = "Bob" }, 0, 0);
            Check("Game page in Wingman (release 199): its length in rounds and who set it, the short button lit; the two buttons send Wingman's counts, which the bridge takes from a click",
                wingman.Texts.Single(x => x.Id == "os_set_time_now").Text == "Matches last 8 rounds - Set by Bob" && wingman.Classes.Contains((HudMenu.Time10, "on", true)) && wingman.Classes.Contains((HudMenu.Time60, "on", false))
                && HudMenu.Game(new HudState { Rounds = 16 }, 0, 0).Texts.Single(x => x.Id == "os_set_time_now").Text == "Matches last 16 rounds"
                && HudMenu.Command(HudMenu.Time10, null, new HudState(), browser, 1, h.Clock, null, Modes.WingmanId) == "!time 8" && HudMenu.Command(HudMenu.Time60, null, new HudState(), browser, 1, h.Clock, null, Modes.WingmanId) == "!time 16"
                && HudMenu.Command(HudMenu.Time10, null, new HudState(), browser, 1, h.Clock) == "!time 10" && HudMenu.Command(HudMenu.Time60, null, new HudState(), browser, 1, h.Clock, null, Modes.DeathmatchId) == "!time 60"
                && Hud.IsAction("!time 8") && Hud.IsAction("!time 16") && HudAct("[U:1:4003]\tCara\t!time 16"));
            Check("...the button sends !rematch, which the bridge takes from a click", HudMenu.Command(HudMenu.Rematch, null, new HudState(), browser, 1, h.Clock) == "!rematch" && Hud.IsAction("!rematch") && HudAct("[U:1:4003]\tCara\t!rematch"));
            Check("...the chat command is the same click, and teaches its aliases", ChatCommands.Parse("!rematch") is { Kind: ChatCommands.Kind.Bridge, Arg: "!rematch", Typed: "" } && M.Plain(ChatCommands.Teach(ChatCommands.Parse("!rr"))) == "You typed !rr - Ran !rematch");
            var vote = new VoteState { Round = 3 };
            Check("A click the state says will be refused arms nothing: the note says why, and the button does not ask for a second click",
                !HudMenu.RestartRefused(HudMenu.Restart, new HudState(), null) && !HudMenu.RestartRefused(HudMenu.Rematch, new HudState(), new VoteState()) && !HudMenu.RestartRefused(HudMenu.Rematch, null, vote)
                && HudMenu.RestartRefused(HudMenu.Rematch, new HudState { RematchIn = 4 }, null) && HudMenu.RestartRefused(HudMenu.Rematch, new HudState { RestartWhen = "now" }, null) && HudMenu.RestartRefused(HudMenu.Rematch, new HudState { ChangeIn = 2 }, null)
                && HudMenu.RestartRefused(HudMenu.Rematch, new HudState { Loading = true }, null) && HudMenu.RestartRefused(HudMenu.Rematch, new HudState(), vote)
                && HudMenu.RestartRefused(HudMenu.Restart, new HudState { RestartWhen = "now" }, null) && !HudMenu.RestartRefused(HudMenu.Restart, new HudState { RematchIn = 4 }, vote));

            // ---- capitals: everything this test made the bridge say but the vote's own lines, the menu's words too ----
            var words2 = said.Where(t => !M.Plain(t).Contains("the next map", StringComparison.Ordinal)).Concat(HudMenu.Game(new HudState { RoundMinutes = 10, RoundBy = "Bob" }, 12, 12).Texts.Select(x => x.Text)).Concat(HudMenu.Game(new HudState(), 0, 0).Texts.Select(x => x.Text))
                .Concat(HudMenu.Game(new HudState { Rounds = 8, RoundBy = "Bob" }, 0, 0).Texts.Select(x => x.Text)).Distinct().ToList();
            var lower = words2.Where(t => Sections.Lower(t, ["Alice", "Bob", "Cara"]) != null).Select(t => $"\"{Sections.Lower(t, ["Alice", "Bob", "Cara"])}\" in \"{M.Plain(t)}\"").ToList();
            Check($"Capitals: every section of the {words2.Count} lines said here starts with one{(lower.Count > 0 ? ": " + string.Join("; ", lower.Take(8)) : "")}", lower.Count == 0);
        }
        finally
        {
            ToldAll = toldAll;
            ToldAll?.AddRange(said); // the run's own capitals check sees them too
            h.Dispose();
        }
    }
}
