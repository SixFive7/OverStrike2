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
        var joiningReply = ""; // ...and to os_joining: none, as from a plugin without it (nobody waited for), unless a test says otherwise
        string? statusTable = null; // ...and status's players table: none, as from a short status, unless a test gives one
        Func<string>? joiningNow = null, statusNow = null; // ...or both made at the moment they are asked, from a replay's own state
        Func<string>? warmUpNow = null, warmEndNow = null; // ...and os_warmup and os_warmup end, a warm-up test's: none, as from a plugin without it
        h.Server = command => rconDown ? null : command == MatchPhase.Command ? phaseReply : command == EarlyJoin.JoiningCommand ? joiningNow?.Invoke() ?? joiningReply
            : command == EarlyJoin.WarmUpCommand ? warmUpNow?.Invoke() ?? "" : command == $"{EarlyJoin.WarmUpCommand} {EarlyJoin.EndWord}" ? warmEndNow?.Invoke() ?? ""
            : command.StartsWith(MatchLength.Command, StringComparison.Ordinal) ? lengthReply
            : command == "status" ? statusNow?.Invoke() ?? statusTable ?? "hostname : test\nspawn    : 2\nplayers  : 2 humans, 0 bots (0 max)\n#end"
            : command == RematchCommand ? restartReply : command == RematchFallback ? bareReply : "";
        try
        {
            ToldAll = said;
            void Click((string Name, string Id) p, string command) => Command(p.Name, p.Id, command);
            void Twice((string Name, string Id) p, string command) { Click(p, command); Click(p, command); }
            bool InChat(string line) => SaysNow.Any(l => M.Plain(l.Text) == line);
            bool OnBoard(string line) => LatelyNow.Any(l => l.Text == line);
            List<string> ComingNames() => [.. JoiningNow(h.Clock).Select(c => c.Name)]; // who the warm-up waits for now, by name
            HudState Live() => HudState.Decode(BuildHud(h.Clock).Encode(endTimes: true))!.AsOf(HudState.Ms(h.Clock)); // the live state as the plugin has it at this moment
            void Fresh() // a bridge that has just started (ResetAll), with the plugin drawing the HUD, on the lists below, Alice and Bob on
            {
                h.Fresh();
                HudActive = () => true; rconDown = false; lengthReply = null; (restartReply, bareReply) = ("os_restart: the match restarts in 1 s", ""); phaseReply = ""; joiningReply = ""; statusTable = null;
                joiningNow = null; statusNow = null; warmUpNow = null; warmEndNow = null;
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
            Check("...and its ask goes with the second click: no note is left telling her to click again for the rest of its 30 s",
                !NoteFor.TryGetValue(4001, out var restartNote) || !restartNote.Text.StartsWith(RestartAsk, StringComparison.Ordinal));
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
            Check("A map that is loading - a change goes out at once - does not hold the restart: its banner is the restart's, and the quit goes out", bannerNow?.Eyebrow == "Server" && h.Sent.Contains("quit"));
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
            Check("...and its ask goes with the second click: no note is left telling her to click again for the rest of its 30 s",
                !NoteFor.TryGetValue(4001, out var rematchNote) || !rematchNote.Text.StartsWith(RematchAsk, StringComparison.Ordinal));
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

            // ---- another player's first click takes the confirmation over: the first one's ask goes with it ----
            Fresh();
            Click(alice, "!rematch");
            Click(bob, "!rematch");
            Check("Restart match: Bob's first click after Alice's takes the window over - her ask goes, his stands",
                !NoteFor.ContainsKey(4001) && NoteFor.TryGetValue(4002, out var bobAsk) && bobAsk.Text.StartsWith(RematchAsk, StringComparison.Ordinal) && RematchConfirmBy == bob.Item2);
            Click(alice, "!rematch");
            Check("...and her next click asks again, taking it back - his ask goes, and nothing counts down",
                NoteFor.TryGetValue(4001, out var aliceAgain) && aliceAgain.Text.StartsWith(RematchAsk, StringComparison.Ordinal) && !NoteFor.ContainsKey(4002) && RematchDue == null);
            Fresh();
            Click(alice, "!restart");
            Click(bob, "!restart");
            Check("Restart the server: the same - Bob's first click takes the window over, and Alice's ask goes",
                !NoteFor.ContainsKey(4001) && NoteFor.TryGetValue(4002, out var bobRestart) && bobRestart.Text.StartsWith(RestartAsk, StringComparison.Ordinal) && RestartConfirmBy == bob.Item2);

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

            // ---- the chat's !cancel: the newest countdown of the player's own ----
            Fresh();
            Twice(alice, "!rematch");
            ClearNotices(); Click(bob, Hud.CancelMine);
            Check("The chat's !cancel (!cancel mine): Bob's stops nothing of Alice's - her restart of the match counts on - and he reads why",
                RematchDue is { By: "Alice" } && h.Said("No countdown of yours runs - A countdown's own button in the menu stops anybody's"));
            ClearNotices(); Click(alice, Hud.CancelMine);
            Check("...Alice's stops her own, as its button's Cancel does, with the same news line", RematchDue == null && h.Said("Alice cancelled their restart of the match - It plays on"));
            Fresh();
            Twice(alice, "!restart");
            ClearNotices(); Click(alice, Hud.CancelMine);
            Check("...her restart of the server too", RestartDue == null && h.Said("Alice cancelled their restart of the server - It plays on"));
            ApplyRestartNotice("os2-restart\t1\tdaily\tnow\t30");
            ClearNotices(); Click(alice, Hud.CancelMine);
            Check("...and never the watcher's, which is nobody's", RestartDue is { Kind: "daily" } && h.Said("No countdown of yours runs"));

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
            Check("Refused while a map loads (a change goes out at once)", h.Said("The next map is still loading - Wait for it") && RematchDue == null);
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
            Check("...also inside the 30 s: a next match 12 s after the end - the screen cut short - is no match that has just ended",
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

            // ---- the warm-up (EarlyJoin.cs, WarmUp.cs): every map starts in CS2's own, the plugin's; the bridge follows it and ends it once everybody coming has
            // spawned (two humans at least), at the most since the first spawn, or at !start - its end the one restart. The plugin faked here: CS2's warm-up on or
            // off, the plugin's plan, os_warmup's answer and os_joining's; the server's own list (status) from who is where ----
            void Connects((string Name, string Id) p) => h.Line($"\"{p.Name}<2><{p.Id}><>\" connected, address \"192.0.2.9:27005\""); // the line as CS2's log push sends it
            void Enters((string Name, string Id) p) => h.Line($"\"{p.Name}<2><{p.Id}><>\" entered the game");
            string Table(params (string Name, string State)[] rows) => "---------players--------\n  id     time ping loss      state   rate adr name\n"
                + "65535 [NoChan]    0    0 challenging      0unknown ''\n" + string.Concat(rows.Select((r, i) => $"{i,5}    00:13   20    0 {r.State,10} 786432 192.0.2.{i + 1}:27005 '{r.Name}'\n")) + "#end";
            var warmOn = false;            // CS2's warm-up
            var gameClock = true;          // ...whose own clock ends it at the most (a game that took no new mp_warmuptime: false)
            DateTime? warmEndedAt = null;  // ...ended at
            var warmEndedBy = "";          // ...by the bridge (os_warmup end) or the game (its own clock)
            EarlyJoin fakePlan = new();    // the plugin's plan
            // A map with its warm-up: the cast on the map before (reload: through the change) or none, the script of their connects, entries, spawns, drops off the
            // server, real departures and !start, half a second at a time. CS2's own clock ends the warm-up at the most from the first spawn, as the plugin sets it.
            // What came of it: the warm-up's end (s after the map's start; -1: still on) and who ended it, the restarts that went out (a match is never restarted),
            // who spawned after the end (they joined the match that runs); look: the test's own look each step.
            (double EndAt, string By, int Restarts, List<string> Behind) Warm((string Name, string Id)[] cast, bool reload, (double At, string What, int Who)[] script, double until, Action<double>? look = null)
            {
                Fresh();
                h.Leave(alice); h.Leave(bob);
                if (reload) { foreach (var p in cast) h.Join(p); foreach (var p in cast) h.Leave(p, "NETWORK_DISCONNECT_SHUTDOWN"); } // the map change: everybody on the way back
                h.Line("Loading map \"ar_warm\""); h.Line("Started:  \"\"");
                fakePlan = new EarlyJoin();
                fakePlan.MapStarted();
                (warmOn, warmEndedAt, warmEndedBy, gameClock) = (false, null, "", true);
                var state = new string[cast.Length]; // "" not connected, "conn" connected, "in" in the game, "off" off the server mid-way, "gone" left for real
                long Acct(int i) => AccountId(cast[i].Id);
                joiningNow = () => EarlyJoin.JoiningReply(Enumerable.Range(0, cast.Length).Where(i => state[i] == "conn").Select(i => cast[i].Name)
                    .Concat(fakePlan.StillToSpawn(Enumerable.Range(0, cast.Length).Where(i => state[i] == "in").Select(Acct), h.Clock).Select(a => cast.First(p => AccountId(p.Id) == a).Name)));
                statusNow = () => Table([.. Enumerable.Range(0, cast.Length).Where(i => state[i] is "conn" or "in").Select(i => (cast[i].Name, state[i] == "in" ? "active" : "connecting"))]);
                warmUpNow = () => EarlyJoin.WarmUpReply(warmOn, fakePlan.FirstSpawn is { } f ? (h.Clock - f).TotalSeconds : null, fakePlan.SpawnedCount,
                    fakePlan.FirstSpawn is { } g && gameClock ? (g.AddSeconds(EarlyJoin.WarmUpMostSeconds) - h.Clock).TotalSeconds : null);
                warmEndNow = () => { if (warmOn) (warmOn, warmEndedAt, warmEndedBy) = (false, h.Clock, "bridge"); return warmUpNow!(); };
                var start = h.Clock;
                var behind = new List<string>();
                h.Sent.Clear();
                for (var t = 0.0; t < until; t += 0.5)
                {
                    if (t == 1) warmOn = true; // the plugin begins it a second into the map
                    if (warmOn && gameClock && fakePlan.FirstSpawn is { } first && h.Clock >= first.AddSeconds(EarlyJoin.WarmUpMostSeconds))
                        (warmOn, warmEndedAt, warmEndedBy) = (false, h.Clock, "game"); // CS2's own clock: the most from the first spawn
                    foreach (var (_, what, who) in script.Where(e => e.At >= t && e.At < t + 0.5))
                    {
                        var p = cast[who];
                        if (what == "conn") { state[who] = "conn"; Connects(p); }
                        else if (what == "enter") { state[who] = "in"; Enters(p); fakePlan.Entered(Acct(who), h.Clock); }
                        else if (what == "off") state[who] = "off"; // the game leaves the server mid-way: its own download of the map, or a crash - no line
                        else if (what == "leave") { state[who] = "gone"; h.Leave(p); fakePlan.Left(Acct(who)); }
                        else if (what == "spawn" && fakePlan.Spawned(Acct(who), h.Clock) && warmEndedAt != null) behind.Add(p.Name);
                        else if (what == "start") Click(p, Hud.StartWord);
                    }
                    h.Run(0.5);
                    look?.Invoke(t + 0.5);
                }
                return (warmEndedAt is { } end ? (end - start).TotalSeconds : -1, warmEndedBy, h.Sent.Count(c => c == RematchCommand || c == RematchFallback), behind);
            }
            (string Name, string Id)[] five = [("Ike", "[U:1:2101]"), ("Gus", "[U:1:2102]"), ("Pia", "[U:1:2103]"), ("Hal", "[U:1:2104]"), ("Max", "[U:1:2105]")];
            const int Most = EarlyJoin.WarmUpMostSeconds, Settle = EarlyJoin.SettleSeconds;
            HudView.CountdownWords? Banner() => HudView.CountdownOf(Live());

            // 2026-11-18's Shortdust (Wingman), with made-up names: all back at once, in the game at +3 to +5 s but Max at +13 s; the first team pick at +8.5 s, spawns at +9,
            // +10, +15 and Max's at +29 s. Release 218 restarted the match twice, at +18 s and, for Max, at +34 s.
            HudView.CountdownWords? bannerSeen = null;
            var sd = Warm(five, true, [(0, "conn", 0), (0, "conn", 1), (0, "conn", 2), (0, "conn", 3), (0, "conn", 4),
                (3, "enter", 0), (4, "enter", 1), (4, "enter", 2), (5, "enter", 3), (13, "enter", 4),
                (8.5, "spawn", 2), (9, "spawn", 1), (10, "spawn", 0), (15, "spawn", 3), (29, "spawn", 4)], 60,
                t => { if (t == 20) bannerSeen = Banner(); });
            Check($"The warm-up, 2026-11-18's Shortdust replayed: it waits for everybody coming - Max at CS2's team menu too - and the bridge ends it {Settle} s after the "
                + $"last spawn (+{sd.EndAt:0} s); no restart goes out, and nobody joins behind (release 218: two restarts)",
                sd.EndAt >= 29 + Settle && sd.EndAt <= 29 + Settle + 2 && sd.By == "bridge" && sd.Restarts == 0 && sd.Behind.Count == 0 && WarmUpSince == null);
            Check($"...meanwhile the countdown banner says WARM-UP, whom it waits for and what they do, and counts down to the latest start, CS2's own end: {bannerSeen}",
                bannerSeen is { Eyebrow: "Warm-up", Title: "Waiting for Max - picking a team", Line: "The match starts when everyone is in - !start to begin now", Logo: true, Warm: true, Seconds: >= 108 and <= 110 }
                && Banner() == null);
            Check("...and the line for everybody as the match starts says why", h.Said("The match starts now - Everybody is in"));
            // gg_moonglass (Arms Race): everybody in the game by +4 s; the first pick at +6.5 s, the next 0.5 s on, +9.5, +9.5 and Max's at +20.5 s. Release 218 did not wait at
            // all (nobody connecting) and restarted twice.
            var mg = Warm(five, true, [(1, "conn", 0), (1, "conn", 1), (1, "conn", 2), (1, "conn", 4), (2, "conn", 3),
                (2, "enter", 2), (3, "enter", 0), (3, "enter", 1), (4, "enter", 4), (4, "enter", 3),
                (6.5, "spawn", 0), (7, "spawn", 2), (9.5, "spawn", 3), (9.5, "spawn", 1), (20.5, "spawn", 4)], 60);
            Check($"...2026-11-18's gg_moonglass: the three at the team menu are waited for, and it ends {Settle} s after Max's spawn (+{mg.EndAt:0} s) - no restart",
                mg.EndAt >= 20.5 + Settle && mg.EndAt <= 20.5 + Settle + 2 && mg.Restarts == 0 && mg.Behind.Count == 0);
            // Transit (Wingman, 1.7 GB): Gus in at +3 s and playing from +8 s; Ike connected at +14 s, off the server from +51 s (a download of his own, or a crash: no
            // line), back at +98 s, in at +112 s, spawning at +117 s; Pia in at +24 s, spawning at +27 s; Max connected at +42 s, in at +52 s, spawning at +59 s; Hal not
            // connected before +82 s - nothing but the log stream's departure names him till then - in at +88 s, spawning at +93 s. Release 218 restarted at +94 and +122 s.
            HudView.CountdownWords? offSeen = null, backSeen = null;
            var tr = Warm(five, true, [(0, "conn", 1), (3, "enter", 1), (8, "spawn", 1), (14, "conn", 0), (19, "conn", 2), (24, "enter", 2), (27, "spawn", 2),
                (42, "conn", 4), (51, "off", 0), (52, "enter", 4), (59, "spawn", 4), (82, "conn", 3), (88, "enter", 3), (93, "spawn", 3),
                (98, "conn", 0), (112, "enter", 0), (117, "spawn", 0)], 150,
                t => { if (t == 95) offSeen = Banner(); else if (t == 105) backSeen = Banner(); });
            Check($"...2026-11-18's Transit: everybody on the way back through the change is coming until in the game - the one off the server for 47 s, the one nothing "
                + $"else named before his connect - so it ends {Settle} s after the last spawn (+{tr.EndAt:0} s), inside its {Most} s from the first spawn: no restart",
                tr.EndAt >= 117 + Settle && tr.EndAt <= 117 + Settle + 2 && tr.EndAt < 8 + Most && tr.Restarts == 0 && tr.Behind.Count == 0);
            Check($"...and the banner tells what the last one does: off the server mid-way after his connect, downloading ({offSeen?.Title}); back on it, loading ({backSeen?.Title})",
                offSeen?.Title == "Waiting for Ike - downloading" && backSeen?.Title == "Waiting for Ike - loading");

            // ---- its most, alone, !start, a newcomer, a leaver ----
            (string Name, string Id)[] three = [("Ana", "[U:1:2111]"), ("Bo", "[U:1:2112]"), ("Cy", "[U:1:2113]")];
            var cap = Warm(three, true, [(0, "conn", 0), (0, "conn", 1), (2, "enter", 0), (3, "enter", 1), (5, "spawn", 0), (6, "spawn", 1),
                (140, "conn", 2), (145, "enter", 2), (150, "spawn", 2)], 160);
            Check($"Warm-up: a player from the map before who does not come back is waited for until the most, {Most} s after the first spawn (+{cap.EndAt:0} s) - CS2's own "
                + "clock ends it, the bridge sends no end; the line names who joins it on arrival, and when they come they join the match that runs: no restart",
                cap.EndAt >= 5 + Most && cap.EndAt <= 5 + Most + 1 && cap.By == "game" && !h.Sent.Contains($"{EarlyJoin.WarmUpCommand} {EarlyJoin.EndWord}")
                && cap.Restarts == 0 && cap.Behind is ["Cy"] && h.Said($"The match starts now - {Most} s are up - Cy joins it on arrival"));
            var alone = Warm(three, false, [(0, "conn", 0), (2, "enter", 0), (5, "spawn", 0), (60, "conn", 1), (64, "enter", 1), (70, "spawn", 1)], 100,
                t => { if (t == 30) bannerSeen = Banner(); });
            Check($"...one human alone is no \"all\": it runs on, so a friend who connects meanwhile gets the same start - Bo at +60 s, and it ends {Settle} s after his spawn (+{alone.EndAt:0} s)",
                bannerSeen is { Title: "Nobody else on the way yet", Line: "Anyone who joins now starts with you - !start to begin now", Seconds: >= 94 and <= 96 }
                && alone.EndAt >= 70 + Settle && alone.EndAt <= 70 + Settle + 2 && alone.Behind.Count == 0 && alone.Restarts == 0);
            var solo = Warm(three, false, [(0, "conn", 0), (2, "enter", 0), (5, "spawn", 0)], 140);
            Check($"...and with nobody coming all along, at the most (+{solo.EndAt:0} s), by CS2's own clock", solo.EndAt >= 5 + Most && solo.EndAt <= 5 + Most + 1 && solo.By == "game"
                && h.Said($"The match starts now - {Most} s are up"));
            HudView.CountdownWords? lateSeen = null;
            var stuck = Warm(three, false, [(0, "conn", 0), (2, "enter", 0), (5, "spawn", 0)], 140,
                t => { if (t == 1.5) gameClock = false; if (t == 60) lateSeen = Banner(); });
            Check($"...a game whose own clock does not end it (it took no new mp_warmuptime): the bridge ends it {WarmUpLateSeconds} s past the most (+{stuck.EndAt:0} s), and "
                + "the banner counted to the most from the first spawn all the same",
                stuck.By == "bridge" && stuck.EndAt >= 5 + Most + WarmUpLateSeconds && stuck.EndAt <= 5 + Most + WarmUpLateSeconds + 1.5 && lateSeen is { Seconds: >= 64 and <= 66 });
            var started = Warm(three, true, [(0, "conn", 0), (0, "conn", 1), (2, "enter", 0), (3, "enter", 1), (5, "spawn", 0), (6, "spawn", 1), (30, "start", 1)], 40);
            Check($"Warm-up: !start, by anybody, begins the match now for a friend who is not coming (+{started.EndAt:0} s); the line says who typed it and who joins on arrival",
                started.EndAt >= 30 && started.EndAt <= 31 && started.By == "bridge" && started.Restarts == 0 && h.Said("The match starts now - Bo typed !start - Cy joins it on arrival"));
            h.Quiet();
            Click(three[0], Hud.StartWord);
            Check("...and outside a warm-up it is refused: the match is on already", h.Said("No warm-up on - The match is on already") && !h.Sent.Contains($"{EarlyJoin.WarmUpCommand} {EarlyJoin.EndWord}"));
            var newcomer = Warm(three, true, [(0, "conn", 0), (0, "conn", 1), (3, "conn", 2), (2, "enter", 0), (3, "enter", 1), (5, "spawn", 0), (6, "spawn", 1),
                (20, "enter", 2), (24, "spawn", 2)], 40);
            Check($"Warm-up: somebody new who connects during it is coming too - waited for, and it ends {Settle} s after their spawn (+{newcomer.EndAt:0} s)",
                newcomer.EndAt >= 24 + Settle && newcomer.EndAt <= 24 + Settle + 2 && newcomer.Behind.Count == 0);
            HudView.CountdownWords? settleSeen = null;
            var leaver = Warm(three, true, [(0, "conn", 0), (0, "conn", 1), (2, "enter", 0), (3, "enter", 1), (5, "spawn", 0), (6, "spawn", 1), (20, "leave", 2)], 40,
                t => { if (t == 23) settleSeen = Banner(); });
            Check($"...somebody who leaves during it is coming no more: it ends {Settle} s after their departure (+{leaver.EndAt:0} s), the banner counting the settle down",
                leaver.EndAt >= 20 + Settle && leaver.EndAt <= 20 + Settle + 2 && settleSeen is { Title: "Everybody is in", Line: "The match starts in a moment", Seconds: >= 1 and <= Settle });
            var late = Warm(three, true, [(0, "conn", 0), (0, "conn", 1), (2, "enter", 0), (3, "enter", 1), (5, "spawn", 0), (6, "spawn", 1), (2, "leave", 2),
                (30, "conn", 2), (33, "enter", 2), (38, "spawn", 2)], 60);
            Check($"...and whoever comes after it ends just joins: no restart, the match runs on (+{late.EndAt:0} s, then a spawn at +38 s)",
                late.EndAt < 30 && late.Restarts == 0 && late.Behind is ["Cy"]);

            // ---- what holds it, what it refuses, and a plugin without it ----
            Warm(three, true, [(0, "conn", 0), (0, "conn", 1), (2, "enter", 0), (3, "enter", 1), (5, "spawn", 0)], 8);
            Twice(alice, "!rematch");
            Check("Warm-up: Restart match is refused while it lasts - !start begins the match", h.Said($"It is the warm-up - Type {Hud.StartWord} to begin the match now") && RematchDue == null);
            StartVote(three[0].Item1, three[0].Item2);
            Click(three[0], Hud.StartWord);
            Check("...and so is !start while a vote decides: the warm-up ends after it, as the banner says",
                h.Said("Not during the vote for the next map - The warm-up ends after it") && warmOn && Banner()?.Line == "The match starts after the vote for the next map");
            Fresh();
            warmUpNow = () => "Unknown command 'os_warmup'!";
            h.Line("Loading map \"ar_old\""); h.Line("Started:  \"\"");
            h.Join(three[0]); h.Join(three[1]);
            h.Sent.Clear();
            h.Run(5);
            Check("Warm-up: a plugin without os_warmup - the bridge asks once on the map, and follows none: no banner", h.Sent.Count(c => c == EarlyJoin.WarmUpCommand) == 1 && WarmUpSince == null && Banner() == null);
            Warm(three, true, [(0, "conn", 0), (0, "conn", 1), (2, "enter", 0), (3, "enter", 1), (5, "spawn", 0)], 8);
            warmOn = false; // the game left it by itself after a spawn (an operator's mp_warmup_end, say)
            h.Run(1.1);
            Check("...a warm-up the game left by itself after a spawn is over for the bridge too: the match is on, and the line says so",
                WarmUpSince == null && Banner() == null && h.Said("The match starts now - The warm-up is over - Bo and Cy join it on arrival"));
            Warm(three, true, [(0, "conn", 0), (0, "conn", 1), (2, "enter", 0), (3, "enter", 1)], 6);
            warmOn = false; // off before anybody spawned: the plugin begins it again within seconds (EarlyJoinWatch)
            h.Run(2.1);
            var followedThrough = WarmUpSince != null && Banner() == null;
            warmOn = true;
            h.Run(1.1);
            Check("...but off before anybody has spawned, the bridge waits for the plugin to begin it again, and follows it on: no line, the banner back",
                followedThrough && WarmUpSince != null && Banner() is { Warm: true } && !h.Said("The match starts now - The warm-up is over"));

            // ---- who is coming, by the server's own list and the log stream: the live lab's run AX - a join needing a map it lacked, CS2's prompt to confirm a first
            // Workshop download left unanswered. The server logged the connect and nothing after it; os_joining said nobody (the game hears of a client only once it
            // has every addon); CS2 dropped it at 120 s and wrote no "disconnected" line. The server's own list has such a joiner connecting until the drop ----
            var cas = ("Cas", "[U:1:2009]");
            Fresh();
            joiningReply = EarlyJoin.JoiningReply([]);
            statusTable = Table(("Alice", "active"), ("Bob", "active"), ("Cas", "connecting"));
            Connects(cas);
            h.Run(1.1);
            Check("Warm-up's waiting set: the lab's joiner at the prompt - the bridge's log stream has them signing on from their \"connected\" alone, and tells the "
                + "plugin's os_warmup so (the names as hex)", SigningOnNames(h.Clock) is ["Cas"] && h.Sent.Contains(EarlyJoin.BridgeViewCommand(["Cas"])));
            h.Run(3);
            Check("...and reads the server's own list every 2 s while anyone signs on, and tells os_warmup its joiners too (run AY's status line)",
                h.Sent.Count(c => c == "status") >= 1 && h.Sent.Contains(EarlyJoin.BridgeViewCommand(["Cas"], ["Cas"])));
            ClearNotices(); h.Sent.Clear();
            Check("...JoiningNow names them while os_joining says nobody: the server's list has them connecting", ComingNames() is ["Cas"] && h.Sent.Contains("status"));
            h.Run(60);
            Check($"...and what they do is downloading: connected {PromptGuessSeconds} s and more with no controller in the game - CS2's prompt to download the Workshop "
                + "items, and the download", JoiningNow(h.Clock) is [("Cas", EarlyJoin.Downloading)]);
            statusTable = Table(("Alice", "active"), ("Bob", "active")); // CS2 drops the unanswered prompt: no line, and gone from its list
            h.Run(1.1);
            var notYet = ComingNames() is ["Cas"];
            h.Run(EarlyJoin.DropConfirmSeconds + StatusWatchSeconds + 0.1);
            Check($"...CS2's drop writes no \"disconnected\" line; the server's list lacks them read after read - a second alone is no drop, the HUD addon's reconnect "
                + $"looks so - and after {EarlyJoin.DropConfirmSeconds} s that ends their sign-on, and os_warmup hears nobody from either",
                notYet && SigningOnNames(h.Clock) is [] && ComingNames() is [] && h.Sent.Contains(EarlyJoin.BridgeViewCommand([], [])));
            Fresh();
            joiningReply = EarlyJoin.JoiningReply([]);
            Connects(cas);
            h.Run(EarlyJoin.SignOnMostSeconds - 1);
            var held = ComingNames() is ["Cas"];
            h.Run(2.1);
            Check($"...with no players table from status, the log stream alone counts them {EarlyJoin.SignOnMostSeconds} s from their \"connected\" - the prompt's "
                + $"{EarlyJoin.PromptSeconds} s and a margin: a ghost holds nothing past the drop", held && ComingNames() is []);
            Fresh();
            joiningReply = EarlyJoin.JoiningReply([]);
            statusTable = Table(("Alice", "active"), ("Bob", "active"), ("Cas", "connecting"));
            Connects(cas);
            h.Run(20);
            h.Leave(cas, "NETWORK_DISCONNECT_LOOPSHUTDOWN"); // MultiAddonManager has the joiner reconnect for the HUD addon
            statusTable = Table(("Alice", "active"), ("Bob", "active")); // a second between the two connections
            h.Run(1.1);
            var throughReconnect = ComingNames() is ["Cas"];
            statusTable = Table(("Alice", "active"), ("Bob", "active"), ("Cas", "connecting"));
            Connects(cas);
            h.Run(3);
            Check("...still coming through the reconnect for the HUD addon (LOOPSHUTDOWN, then \"connected\" again a second later), though the server's list lacks them in between",
                throughReconnect && ComingNames() is ["Cas"]);
            Enters(cas);
            statusTable = Table(("Alice", "active"), ("Bob", "active"), ("Cas", "active"));
            h.Run(1.1);
            Check("...and in once they are in the game", ComingNames() is []);
            Fresh();
            joiningReply = EarlyJoin.JoiningReply([]);
            Connects(cas);
            h.Run(30);
            h.Leave(cas, "NETWORK_DISCONNECT_TIMEDOUT"); // a time-out once the game knows them writes the line
            h.Run(1.1);
            Check("...a joiner whose departure the log does write (a time-out once the game has them) is coming no more", ComingNames() is []);
            Fresh();
            joiningReply = EarlyJoin.JoiningReply(["Cas"]);
            statusTable = Table(("Alice", "active"), ("Bob", "active"), ("Cas", "connecting"));
            Connects(cas);
            Check("...a joiner all three name - the plugin, the server's list and the log stream - is named once", ComingNames() is ["Cas"]);
            // the HUD addon's reconnect seen first by the server's list (seen live on 2026-11-18: a joiner's sign-on ended as "gone before they were in" in the same
            // second as their LOOPSHUTDOWN line): a list that lacks a joiner once is no drop
            var kit = ("Kit", "[U:1:2010]");
            Fresh();
            statusTable = Table(("Alice", "active"), ("Bob", "active"), ("Kit", "connecting"));
            Connects(kit);
            h.Run(7);
            statusTable = Table(("Alice", "active"), ("Bob", "active")); // the reconnect: the list has lost them, the log stream has not said it yet
            h.Run(1.1);
            var raced = ComingNames() is ["Kit"];
            h.Leave(kit, "NETWORK_DISCONNECT_LOOPSHUTDOWN");
            h.Run(1);
            statusTable = Table(("Alice", "active"), ("Bob", "active"), ("Kit", "connecting"));
            Connects(kit);
            h.Run(3);
            Check($"...the server's list lacking a joiner a moment before the log stream says the HUD addon's reconnect (LOOPSHUTDOWN) is no drop: the list must lack them "
                + $"{EarlyJoin.DropConfirmSeconds} s, read after read, and the reconnect's departure makes them one on the way back", raced && ComingNames() is ["Kit"]);
            Fresh();
            var lou = ("Lou", "[U:1:2011]");
            Connects(lou);
            h.Run(5);
            h.Leave(lou, "NETWORK_DISCONNECT_LOOPDEACTIVATE");
            Check("...a game that goes with NETWORK_DISCONNECT_LOOPDEACTIVATE has left - no reconnect, no sign-on, not on the server - as the plugin has it too (EarlyJoin.Reconnects)",
                SigningOnNames(h.Clock) is [] && !PresentIds(h.Clock).Contains(lou.Item2));

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
