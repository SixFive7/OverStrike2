using static OverStrike2.Server.SelfTest.T;

namespace OverStrike2.Server.SelfTest;

/// <summary>The scenarios: the real Watch loop with a fake clock and a fake engine - both freeze paths, the loop protection, the planned
/// restarts, the quit notice, the console probe at boot. The Python watcher's tests, check for check (same names); where a restart now
/// announces itself to the bridge first (POST /restart-notice), the check says so.</summary>
static class ScenarioTests
{
    static DateTimeOffset At(int d, int h, int m, int s) => Scenario.At(2026, 10, d, h, m, s);

    /// <summary>A scenario that answers for `warm` s, then freezes (A2S and RCON dead, process up), for `seconds` more.</summary>
    static Scenario FrozenAfter(double seconds, Action<Scenario>? setup = null, double warm = 20)
    {
        var s = Scenario.Make(At(4, 15, 0, 0));
        s.Step(warm);
        setup?.Invoke(s);
        s.Eng.Freeze();
        s.Step(seconds);
        return s;
    }

    public static void Run()
    {
        // A. path a: signature, A2S silent -> kill after the 10 s grace, not before - RCON may even still answer
        var s = Scenario.Make(At(4, 15, 0, 0));
        s.Step(20);
        Check("A: console.log proven live by the echo probe", s.W.Console is not null);
        s.Eng.A2s = false;
        s.Eng.Write("SV:  Level loading started for 'error'");
        s.Eng.Write("LoopInit:  in \"error\" map - disconnecting...");
        s.Step(8);
        Check("A: no kill inside the grace (8 s)", s.Eng.Killed.Count == 0);
        s.Step(6);
        Check($"A: killed after the 10 s grace ({Show(s.Eng.Killed.Take(1))})", s.Eng.Killed.Count == 1 && s.Eng.Killed[0].Contains("error-map"));
        Check("A: the kill is persisted as pending freeze", s.W.State.Pending?.Kind == "freeze");

        // B. path b: silence alone
        s = FrozenAfter(8);
        Check("B1: A2S and RCON silent for 8 s -> no kill yet", s.Eng.Killed.Count == 0);
        s.Step(6);
        Check($"B1: ... killed once both have been silent 10 s on several polls ({Show(s.Eng.Killed.Take(1))})",
              s.Eng.Killed.Count == 1 && s.Eng.Killed[0].Contains("silent freeze") && s.W.State.Pending?.Kind == "freeze");
        Check("B1: one kill, counted as a freeze, the engine marked going down",
              s.W.State.Restarts.Select(r => r.Kind).SequenceEqual(["freeze"]) && s.W.GoingDown is not null);

        s = FrozenAfter(0);
        s.Eng.RconUp = true;
        s.Step(3600);
        Check("B2: an hour of A2S silence while RCON still answers -> no kill", s.Eng.Killed.Count == 0);

        s = Scenario.Make(At(4, 15, 0, 0));
        s.Step(20);
        for (var i = 0; i < 30; i++)                // a lost packet now and then: one poll fails, the next answers
        {
            s.Eng.Freeze();
            s.Step(2);
            s.Eng.Thaw();
            s.Step(4);
        }
        Check("B3: single lost polls, over and over -> no kill", s.Eng.Killed.Count == 0);

        s = Scenario.Make(At(4, 15, 0, 0));
        s.Eng.Freeze();
        s.Step(600);
        Check("B4: an engine that has not answered once since the watcher started (boot) is never killed for silence", s.Eng.Killed.Count == 0 && s.W.ArmedPid is null);

        s = FrozenAfter(300, x => x.Eng.PidValue = 4343);
        Check("B5: a new engine process (relaunch) is not armed until it answers -> no kill", s.Eng.Killed.Count == 0);
        s.Eng.Thaw();
        s.Step(4);
        Check("B5: ... it answers, so it is armed", s.W.ArmedPid == 4343);
        s.Eng.Freeze();
        s.Step(14);
        Check("B5: ... and a freeze after that is killed", s.Eng.Killed.Count == 1);

        s = FrozenAfter(120, x => x.Eng.Write("L 10/04/2026 - 15:00:20: rcon from \"192.0.2.101:40112\": command \"quit\""));
        Check("B6: silence after someone's RCON quit (the bridge's !restart) -> no kill", s.Eng.Killed.Count == 0 && s.W.ArmedPid is null);

        s = Scenario.Make(At(4, 6, 59, 50));
        s.Step(5);
        var b7 = s;
        b7.Eng.OnRcon = (c, r) =>            // the quit is delivered, and the engine goes silent but does not exit
        {
            var o = b7.Eng.BaseRcon(c, r);
            if (c is ["quit"])
            {
                b7.Eng.PidValue = 4242;
                b7.Eng.Freeze();
            }
            return o;
        };
        s.Step(120);
        Check($"B7: during the watcher's own restart the silence path stays out: one kill, the planned one ({Show(s.Eng.Killed)})",
              s.Eng.Killed.Count == 1 && s.Eng.Killed[0].Contains("after `quit`") && s.W.State.Restarts.Select(r => r.Kind).SequenceEqual(["daily"]));

        s = FrozenAfter(14);
        Check("B8: first silent freeze killed", s.Eng.Killed.Count == 1);
        var st = s.W.State.Copy();
        s = Scenario.Make(s.Clock.Now.AddSeconds(30));    // the container came back 30 s later
        s.Carry(st);
        s.Step(10);
        s.Eng.Freeze();
        s.Step(30);
        Check("B8: a second freeze inside the 3 min floor waits (loop protection)", s.Eng.Killed.Count == 0);
        s.Step(180);
        Check("B8: ... and is killed once the floor has passed", s.Eng.Killed.Count == 1);
        Check("B8: the previous kill was judged recovered on this start", s.W.State.FreezeMisses == 0);
        s = FrozenAfter(60, x => x.W.State.FreezeTripped = 1);
        Check("B8: a latched freeze breaker blocks silence kills too", s.Eng.Killed.Count == 0);
        s = FrozenAfter(60, x => x.W.State.Restarts = [.. Enumerable.Range(0, 10).Select(i => new Restart((long)x.Clock.T - 4000 - i * 600, "freeze"))]);
        Check("B8: the daily cap of 10 freeze kills blocks silence kills too", s.Eng.Killed.Count == 0);

        s = Scenario.Make(At(4, 15, 0, 0));
        s.Step(20);
        for (var i = 0; i < 20; i++)                // a slow map load: both silent 6 s, then it answers again
        {
            s.Eng.Freeze();
            s.Step(6);
            s.Eng.Thaw();
            s.Step(30);
        }
        Check("B9: loads silent for 6 s (3x the longest measured) never trip it", s.Eng.Killed.Count == 0);

        s = FrozenAfter(0);
        Check("B10: RCON is only probed while A2S fails", s.Eng.AliveProbes <= 1);
        s.Eng.Thaw();
        s.Step(600);
        Check("B10: ... no RCON probes at all while A2S answers", s.Eng.AliveProbes <= 1);

        // B11. the same freeze with the probes taking their real time on a dead engine (A2S 1 s, RCON 1.5 s timeouts)
        s = Scenario.Make(At(4, 15, 0, 0));
        s.Step(20);
        var b11 = s;
        var killAt = new List<double>();
        b11.Eng.OnProbe = () =>
        {
            var answer = b11.Eng.BaseProbe();
            if (answer is null)
                b11.Clock.Sleep(LocalEngine.A2sTimeout);
            return answer;
        };
        b11.Eng.OnAlive = () =>
        {
            var answer = b11.Eng.BaseAlive();
            if (!answer)
                b11.Clock.Sleep(LocalEngine.RconProbeTimeout);
            return answer;
        };
        b11.Eng.OnKill = (pid, why) =>
        {
            killAt.Add(b11.Clock.T);
            b11.Eng.BaseKill(pid, why);
        };
        var frozenAt = s.Clock.T;
        s.Eng.Freeze();
        s.Step(60);
        Check($"B11: with real probe timeouts the kill lands 10-16 s after the freeze ({Show(killAt.Select(t => (t - frozenAt).ToString("0.0")))} s, {Show(s.Eng.Killed.Take(1))})",
              killAt.Count == 1 && killAt[0] - frozenAt is >= 10 and <= 16);

        // C. a map that starts loading inside the grace cancels the freeze (path a); RCON answering keeps path b out too
        s = Scenario.Make(At(4, 15, 0, 0));
        s.Step(20);
        s.Eng.Write("SV:  Level loading started for 'error'");
        s.Eng.A2s = false;
        s.Step(6);
        s.Eng.Write("SV:  Level loading started for 'ar_baggage'");
        s.Step(200);
        Check("C: recovered by itself -> no kill", s.Eng.Killed.Count == 0);

        // D. daily restart, empty server: quits at 07:00, once
        s = Scenario.Make(At(4, 6, 59, 30));
        s.Step(40);
        Check("D: quit at 07:00 when empty", s.Has("quit") && s.Eng.Chat.Count == 0);
        Check("D: done_day recorded", s.W.State.DoneDay == "2026-10-04");
        s.Eng.PidValue = 4242;
        var n = s.Count("quit");
        s.Step(1200);
        Check("D: no second daily restart that day", s.Count("quit") == n);

        // E. the daily restart with players on waits for an empty server: no warning, no quit at a match end, never a kick
        s = Scenario.Make(At(4, 6, 59, 0));
        s.Eng.Humans = 3;
        s.Step(300);
        s.Eng.Write("L 10/04/2026 - 07:05:00: Game Over: gungameprogressive mg_armsrace ar_shoots score 0:0 after 15 min");
        s.Step(60);
        Check("E: players on: no warning and no quit at the match end - it waits",
              !s.Has("quit") && s.Eng.Chat.Count == 0 && s.Eng.Notices.Count == 0 && s.W.Due is { Kind: "daily" });
        s.Eng.Humans = 0;
        s.Step(30);
        Check("E: ... and it restarts the moment the server is empty, silently", s.Has("quit") && s.Eng.Chat.Count == 0 && s.W.State.DoneDay == "2026-10-04");
        s = Scenario.Make(At(4, 6, 59, 0));
        s.Eng.Humans = 2;
        s.Step(Rules.DailyWindow + 120);
        s.Eng.Humans = 0;
        s.Step(60);
        Check("E1: players on until DAILY_WINDOW after the slot: no daily restart that day", !s.Has("quit") && s.W.State.DoneDay != "2026-10-04");

        // E2. an update restart with players on - the restart that goes at a match end. No bridge to announce it to: the watcher's own
        //     warning on the HUD for 10 minutes, its last line for 30 s at the match end, then the quit; chat stays empty
        s = Scenario.Make(At(4, 15, 0, 0), ("stale", 14189, null));
        (s.Eng.Humans, s.Eng.Hud) = (3, true);
        s.Step(300);
        Check("E2: an update with players on: the warning is on the HUD for 600 s, not in chat, no quit mid-match",
              s.Eng.Notices.Count > 0 && s.Eng.Notices[0].Seconds == 600 && s.Eng.Notices[0].Text.Contains("end of this match") && s.Eng.Chat.Count == 0 && !s.Has("quit"));
        s.Eng.Write("L 10/04/2026 - 15:00:00: Game Over: gungameprogressive mg_armsrace ar_shoots score 0:0 after 15 min");   // the stamp is not read
        s.Step(10);
        Check("E2: the restart line on the HUD for 30 s, then quit - chat stays empty",
              s.Has("quit") && s.Eng.Notices.Count == 2 && s.Eng.Notices[1].Seconds == 30 && s.Eng.Notices[1].Text.Contains("CS2 update") && s.Eng.Chat.Count == 0);

        // E3. the same without a HUD: both lines go to chat after gg_notice was asked
        s = Scenario.Make(At(4, 15, 0, 0), ("stale", 14189, null));
        s.Eng.Humans = 3;
        s.Step(300);
        Check("E3: no HUD: warned once in chat, no quit mid-match", s.Eng.Chat.Count == 1 && s.Eng.Chat[0].Contains("end of this match") && !s.Has("quit"));
        s.Eng.Write("L 10/04/2026 - 15:00:00: Game Over: gungameprogressive mg_armsrace ar_shoots score 0:0 after 15 min");
        s.Step(10);
        Check("E3: the restart line, then quit after Game Over",
              s.Has("quit") && s.Eng.Chat[^1].Contains("CS2 update") && s.Eng.Cmds.Count(c => c.StartsWith("gg_notice ", StringComparison.Ordinal)) == 2);

        // F. A2S says empty, RCON says 1 human -> no restart on a disagreement
        s = Scenario.Make(At(4, 6, 59, 50));
        s.Eng.OnProbe = () => new EngineInfo("1.41.8.8", 0, 0, "ar_shoots");
        s.Eng.Humans = 1;
        s.Step(60);
        Check("F: disagreement -> no quit", !s.Has("quit"));

        // G. stale build, empty server -> update restart; three misses latch the breaker, a newer build re-arms it
        s = Scenario.Make(At(4, 15, 0, 0), ("stale", 14189, null));
        s.Step(30);
        Check("G: stale + empty -> quit", s.Has("quit") && s.W.State.Pending?.Kind == "update");
        var quits = 1;
        var history = new List<string>();
        for (var i = 0; i < 8; i++)                // each restart is judged on the NEXT start, so keep going until it latches
        {
            st = s.W.State.Copy();
            s = Scenario.Make(s.Clock.Now.AddHours(3), ("stale", 14189, null));
            s.Carry(st);
            s.Step(60);
            quits += s.Count("quit");
            history.Add($"({s.W.State.UpdateMisses}, {s.W.State.UpdateTripped != 0}, {s.Count("quit")})");
            if (s.W.State.UpdateTripped != 0)
                break;
        }
        Check($"G: latched after the 3rd unchanged build, 3 update restarts in total ({string.Join(", ", history)})",
              s.W.State.UpdateTripped != 0 && s.W.State.UpdateMisses == 3 && quits == 3);
        st = s.W.State.Copy();
        s = Scenario.Make(s.Clock.Now.AddHours(12), ("stale", 14189, null));
        s.Carry(st);
        s.Step(600);
        Check("G: still stale for the same build -> no restart while latched", !s.Has("quit"));
        s.Valve.Answer = _ => ("stale", 14190, null);
        s.W.VerdictAt = 0;
        s.Step(30);
        Check("G: a newer required build re-arms the breaker", s.W.State.UpdateTripped == 0);

        // H. console.log never proven -> players on -> the daily restart waits for an empty server
        s = Scenario.Make(At(4, 6, 59, 0));
        var h = s;
        h.Eng.OnRcon = (c, r) => h.Eng.BaseRcon(c.Where(x => !x.StartsWith("echo ", StringComparison.Ordinal)).ToList(), r);
        s.Eng.Humans = 2;
        s.Step(900);
        Check("H: no live console, players on -> no quit, no chat", !s.Has("quit") && s.W.Console is null);
        s.Eng.Humans = 0;
        s.Step(30);
        Check("H: ... and quits once they leave", s.Has("quit"));
        s = Scenario.Make(At(4, 15, 0, 0));
        var h2 = s;
        h2.Eng.OnRcon = (c, r) => h2.Eng.BaseRcon(c.Where(x => !x.StartsWith("echo ", StringComparison.Ordinal)).ToList(), r);
        s.Step(20);
        s.Eng.Freeze();
        s.Step(600);
        Check("H: no live console.log -> no silence kill either (a quit could not be told apart)", s.Eng.Killed.Count == 0 && s.W.Console is null);

        // I. an undelivered quit is never forced (no quit echo would make the bridge book a false crash)
        s = Scenario.Make(At(4, 6, 59, 50));
        var iq = s;
        iq.Eng.OnRcon = (c, r) => c is ["quit"] ? null : iq.Eng.BaseRcon(c, r);
        s.Step(120);
        Check("I: quit not delivered -> engine not killed, restart counted once",
              s.Eng.Killed.Count == 0 && s.Eng.PidValue == 4242 && s.W.State.Restarts.Count(r => r.Kind == "daily") == 1);

        QuitNotice();
        Boot();
    }

    /// <summary>Q. The quit notice: the bridge hears of a planned quit right before it, at /quitting/&lt;secret&gt; - the quit's echo dies with
    /// the process (the engine segfaults on quit), and without either the bridge books a crash. Before it, the restart notice
    /// (/restart-notice): with nobody on, "now" and 0 s.</summary>
    static void QuitNotice()
    {
        var order = new List<string>();
        string? answer = "ok: going down";
        Exception? fail = null;
        Scenario.NewBridge = () => new FakeBridge
        {
            Reply = (what, body) =>
            {
                order.Add($"{what}: {body}");
                if (fail is not null)
                    throw fail;
                return what == "quitting" ? answer : "ok";
            },
        };
        Scenario Recorded(DateTimeOffset start, (string, int?, string?)? verdict = null)
        {
            var s = Scenario.Make(start, verdict);
            s.Eng.OnRcon = (c, r) =>
            {
                if (c is ["quit"])
                    order.Add("quit");
                return s.Eng.BaseRcon(c, r);
            };
            return s;
        }
        int Quitting() => order.Count(o => o.StartsWith("quitting: ", StringComparison.Ordinal));

        var s = Recorded(At(4, 6, 59, 50));
        s.Step(40);
        Check($"Q1: the daily restart tells the bridge first, then quits: {Show(order.Select(o => o.Split(':')[0]))}",
              order.SequenceEqual(["restart-notice: gg-restart\t1\tdaily\tnow\t0\n", "quitting: gg-quit\t1\tdaily\n", "quit"]) && s.W.State.DoneDay == "2026-10-04");

        order.Clear();
        fail = new HttpRequestException("Connection refused");
        s = Recorded(At(4, 6, 59, 50));
        s.Step(150);
        var tries = Quitting();
        Check($"Q2: a bridge that does not take the notice: no quit, no restart counted, the notice tried again every minute ({tries} tries)",
              !s.Has("quit") && s.W.State.Restarts.Count == 0 && tries is >= 2 and <= 4 && s.W.Due is not null
              && order.Count(o => o.StartsWith("restart-notice", StringComparison.Ordinal)) == tries);
        fail = null;
        s.Step(70);
        Check("Q2: ... and once it does, the restart goes: notice, then quit",
              order.TakeLast(2).Select(o => o.Split(':')[0]).SequenceEqual(["quitting", "quit"]) && s.W.State.Restarts.Count(r => r.Kind == "daily") == 1);

        order.Clear();
        answer = "Not Found";
        s = Recorded(At(4, 6, 59, 50));
        s.Step(90);
        Check("Q3: an older bridge (its 404 page, no 'ok') has not taken it either: no quit", !s.Has("quit") && s.W.State.Restarts.Count == 0);
        answer = "ok: going down";

        order.Clear();
        fail = new TaskCanceledException("timed out");
        s = Recorded(At(4, 15, 0, 0), ("stale", 14189, null));
        s.Step(Watch.QuitNoticePatience - 120);
        Check("Q4: an update restart waits for the notice too...", !s.Has("quit"));
        s.Step(300);
        Check("Q4: ... but not for ever: after QUIT_NOTICE_PATIENCE an outdated server turning everybody away is worse than a false mark",
              s.Has("quit") && s.W.State.Pending?.Kind == "update");
        order.Clear();
        s = Recorded(At(4, 6, 59, 50));
        s.Step(Watch.QuitNoticePatience - 120);
        Check("Q8: the daily restart waits for the notice too...", !s.Has("quit") && s.W.State.Restarts.Count == 0);
        s.Step(300);
        Check("Q8: ... and, like every planned restart, goes ahead after QUIT_NOTICE_PATIENCE without the bridge's answer",
              s.Has("quit") && s.W.State.Pending?.Kind == "daily");
        fail = null;

        order.Clear();
        s = Scenario.Make(At(4, 6, 59, 50));
        var q5 = s;
        q5.Eng.OnRcon = (c, r) => c is ["quit"] ? null : q5.Eng.BaseRcon(c, r);
        s.Step(40);
        var quitBodies = order.Where(o => o.StartsWith("quitting: ", StringComparison.Ordinal)).Select(o => o["quitting: ".Length..]).Take(2).ToList();
        Check($"Q5: the bridge took the notice but the quit could not be delivered: the notice is taken back ({Show(quitBodies.Select(b => b.TrimEnd('\n').Replace('\t', ' ')))})",
              quitBodies.SequenceEqual(["gg-quit\t1\tdaily\n", "gg-quit\t1\tcancel\tdaily\n"]) && s.Eng.Killed.Count == 0
              && order.Contains("restart-notice: gg-restart\t1\tcancel\tdaily\n"));

        order.Clear();
        s = FrozenAfter(30);
        Check("Q6: a freeze kill sends no notice - the bridge records it as the crash it is", s.Eng.Killed.Count == 1 && order.Count == 0);

        Scenario.NewBridge = () => null;
        Check("Q7: no bridge configured: the notice says so and the quit goes as before", new HttpBridge(null).Post("quitting", "gg-quit\t1\tdaily\n", 8) == ("none", null));
        Check("Q7: ... and the watcher reads the bridge's address from bridge.url and bridge.secret, else from the log push's own address",
              ServerConfig.BridgeEndpoint("http://bridge:8080", "s3cret", null) == ("http://bridge:8080", "s3cret")
              && ServerConfig.BridgeEndpoint("http://bridge:8080", "", null) is null);
    }

    /// <summary>J. RCON not listening yet at boot (refused about a second after A2S first answered, as measured). A session that never
    /// opens says nothing about console.log, so it is retried in seconds and never burns a try.</summary>
    static void Boot()
    {
        var s = Scenario.Make(At(4, 15, 0, 0));
        var refused = 0;
        var j = s;
        j.Eng.OnRcon = (c, r) =>
        {
            if (refused < 3)
            {
                refused++;
                return null;
            }
            return j.Eng.BaseRcon(c, r);
        };
        s.Step(60);
        Check($"J: RCON refused at boot is retried within seconds; console.log proven inside a minute ({refused} refusals, {s.W.ProbeTriesDone} try)",
              s.W.Console is not null && s.W.ProbeTriesDone <= 1);
        // J2. RCON never comes up: the fast retries stop at the cap, then the ordinary tries run out into the ALERT
        s = Scenario.Make(At(4, 15, 0, 0));
        s.Eng.OnRcon = (_, _) => null;
        s.Step(Watch.ProbeRconMisses * Watch.ProbeRconRetry + Watch.ProbeTries * Watch.ProbeRetry + 60);
        Check($"J2: a dead RCON is capped at {Watch.ProbeRconMisses} fast retries, then the {Watch.ProbeTries} ordinary tries run out",
              s.W.Console is null && s.W.ProbeRconMissesDone == Watch.ProbeRconMisses && s.W.ProbeTriesDone >= Watch.ProbeTries);
    }
}
