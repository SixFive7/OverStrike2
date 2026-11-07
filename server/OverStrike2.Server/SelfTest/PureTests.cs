using OverStrike2.Core;
using static OverStrike2.Server.SelfTest.T;

namespace OverStrike2.Server.SelfTest;

/// <summary>The version trap, Valve's replies, the console lines, the daily window, both freeze decisions, the gates, the state file and
/// the texts - the decisions on their own.</summary>
static class PureTests
{
    public static void Run()
    {
        Scenario.Setup();
        var inf = Scenario.SteamInf;
        // ---- the version trap (the real steam.inf of a running server, ServerVersion FIRST on purpose)
        Check("trap: PatchVersion is the field read", Versions.PatchVersion(inf) == "1.41.8.8");
        Check("trap: dots removed -> 14188", Versions.VersionInt(Versions.PatchVersion(inf)) == 14188);
        var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, Versions.ValveUrl, Versions.VersionInt(Versions.PatchVersion(inf)));
        Check("trap: the Valve URL carries 14188, never ServerVersion 2000924", url.Contains("version=14188") && !url.Contains("2000924"));
        Check("trap: ServerVersion itself is rejected (no dots)", Versions.VersionInt("2000924") is null);
        Check("trap: a dotless 14188 is rejected too", Versions.VersionInt("14188") is null);
        Check("trap: steam.inf without PatchVersion -> None", Versions.PatchVersion("ServerVersion=2000924\n") is null);
        Check("trap: 'PatchVersionX=' is not PatchVersion", Versions.PatchVersion("PatchVersionX=9.9\n") is null);
        Check("trap: Versions.ReadPatchVersion reads the volume's steam.inf", Versions.ReadPatchVersion(Path.Combine(Scenario.Root, "game", "csgo", "steam.inf")) == "1.41.8.8");

        // ---- Valve's replies (their shapes as measured)
        const string stale = "{\"response\":{\"success\":true,\"up_to_date\":false,\"version_is_listable\":false,\"required_version\":14188,\"message\":\"Your server is out of date, please upgrade\"}}";
        const string current = "{\"response\":{\"success\":true,\"up_to_date\":true,\"version_is_listable\":true}}";
        Check("valve: 14186 -> stale, required 14188", Versions.ValveVerdict(stale, 14186) is ("stale", 14188, _));
        Check("valve: current", Versions.ValveVerdict(current, 14188) is ("current", null, null));
        Check("valve: required not newer -> unknown", Versions.ValveVerdict(stale, 14188).Answer == "unknown");
        Check("valve: success false -> unknown", Versions.ValveVerdict("{\"response\":{\"success\":false}}", 1).Answer == "unknown");
        Check("valve: not JSON -> unknown", Versions.ValveVerdict("<html>502</html>", 1).Answer == "unknown");
        Check("valve: required as string -> unknown", Versions.ValveVerdict("{\"response\":{\"success\":true,\"up_to_date\":false,\"required_version\":\"14188\"}}", 14186).Answer == "unknown");
        Check("valve: up_to_date as string -> unknown", Versions.ValveVerdict("{\"response\":{\"success\":true,\"up_to_date\":\"false\",\"required_version\":14188}}", 14186).Answer == "unknown");
        Check("valve: required true (bool) -> unknown", Versions.ValveVerdict("{\"response\":{\"success\":true,\"up_to_date\":false,\"required_version\":true}}", 14186).Answer == "unknown");

        // ---- console lines (as the container log has them)
        string[] sig = ["SV:  Level loading started for 'error'", "LoopInit:  in \"error\" map - disconnecting...",
                        "../../game/shared/loopmodegame.cpp (652) : Assertion Failed in function LoopInit():"];
        foreach (var line in sig)
            Check($"freeze signature: {line[..Math.Min(40, line.Length)]}", ConsoleLines.IsFreezeSignature(line));
        Check("freeze: a normal level load is not the signature", !ConsoleLines.IsFreezeSignature("SV:  Level loading started for 'ar_shoots'"));
        Check("freeze: chat cannot fake it", !ConsoleLines.IsFreezeSignature("L 01/03/2001 - 12:00:01: \"Evil<3><[U:1:999]><CT>\" say \"SV:  Level loading started for 'error'\""));
        Check("freeze: chat cannot fake the LoopInit line", !ConsoleLines.IsFreezeSignature("L 01/03/2001 - 12:00:01: \"Evil<3><[U:1:999]><CT>\" say \"LoopInit:  in \"error\" map\""));
        Check("level: ar_shoots", ConsoleLines.LevelStarted("SV:  Level loading started for 'ar_shoots'") == "ar_shoots");
        Check("level: error", ConsoleLines.LevelStarted("SV:  Level loading started for 'error'") == "error");
        string[] over = ["L 01/05/2001 - 12:00:00: Game Over: gungameprogressive mg_armsrace ar_shoots score 0:0 after 7 min",
                         "01/05/2001 - 12:00:00.123 - Game Over: gungameprogressive mg_armsrace de_stmarc score 0:0 after 15 min"];
        foreach (var line in over)
            Check($"game over: {line[..30]}", ConsoleLines.IsGameOver(line));
        Check("game over: chat cannot fake it", !ConsoleLines.IsGameOver("L 01/05/2001 - 12:00:00: \"Remy<2><[U:1:1]><CT>\" say \"Game Over: nice try\""));
        Check("game over: chat with a fake prefix inside cannot fake it",
              !ConsoleLines.IsGameOver("L 01/05/2001 - 12:00:00: \"Remy<2><[U:1:1]><CT>\" say \"L 01/05/2001 - 12:00:00: Game Over: x\""));

        // ---- the daily window (a fixed summer-time offset, and the real zone across its clock change)
        var cest = TimeZoneInfo.CreateCustomTimeZone("CEST", Scenario.Cest, "CEST", "CEST");
        double At(int d, int h, int m, int s) => Watch.Seconds(Scenario.At(2026, 10, d, h, m, s));
        var seven = new TimeOfDay(7, 0);
        Check("daily: 07:00:00 due", Rules.DailyDue(At(4, 7, 0, 0), seven, cest, "2026-10-03"));
        Check("daily: 08:59 still due", Rules.DailyDue(At(4, 8, 59, 0), seven, cest, "2026-10-03"));
        Check("daily: 09:00 window closed", !Rules.DailyDue(At(4, 9, 0, 0), seven, cest, "2026-10-03"));
        Check("daily: 06:59 not yet", !Rules.DailyDue(At(4, 6, 59, 59), seven, cest, "2026-10-03"));
        Check("daily: done today", !Rules.DailyDue(At(4, 7, 30, 0), seven, cest, "2026-10-04"));
        Check("daily: off", !Rules.DailyDue(At(4, 7, 30, 0), null, cest, ""));
        var dstEnd = Watch.Seconds(new DateTimeOffset(2026, 10, 25, 7, 30, 0, TimeSpan.FromHours(1)));   // the morning summer time ends: 07:30 CET
        Check("daily: DST-end morning 07:30 CET is due", Rules.DailyDue(dstEnd, seven, Scenario.Paris, "2026-10-24"));
        Check("daily: a planned restart's day is its slot's, also for a slot near midnight (the day it began, not the day after)",
              Rules.LocalDay(Rules.NextSlot(Watch.Seconds(new DateTimeOffset(2026, 10, 5, 0, 30, 0, Scenario.Cest)) - Rules.DailyWindow, new TimeOfDay(23, 30), cest)!.Value, cest) == "2026-10-04");

        // ---- the freeze decision (path a)
        Check("freeze: all conditions -> kill", Rules.FreezeConfirmed(1090, 1000, 999, true, false, 90));
        Check("freeze: grace not over", !Rules.FreezeConfirmed(1089, 1000, 999, true, false, 90));
        Check("freeze: A2S answering", !Rules.FreezeConfirmed(1200, 1000, null, true, false, 90));
        Check("freeze: engine already gone", !Rules.FreezeConfirmed(1200, 1000, 999, false, false, 90));
        Check("freeze: another map started loading", !Rules.FreezeConfirmed(1200, 1000, 999, true, true, 90));
        Check("freeze: silence began long after the signature", !Rules.FreezeConfirmed(1500, 1000, 1100, true, false, 90));
        Check("freeze (path a): no signature -> no path-a kill", !Rules.FreezeConfirmed(99999, null, 0, true, false, 90));
        Check("freeze: the default grace is 10 s", new ServerConfig { Paths = Scenario.Template.Paths }.FreezeGrace == 10 && new Settings().Restarts.FreezeGraceSeconds == 10);
        Check("freeze (path a) at 10 s: 10 s of silence after the signature -> kill", Rules.FreezeConfirmed(1010, 1000, 1000, true, false, 10));
        Check("freeze (path a) at 10 s: 9 s -> no kill", !Rules.FreezeConfirmed(1009, 1000, 1000, true, false, 10));
        Check("polling: A2S every 2 s with a 1 s timeout, so the 10 s grace spans several polls",
              Watch.A2sEvery == 2 && LocalEngine.A2sTimeout == TimeSpan.FromSeconds(1) && Rules.SilentPolls >= 3 && Watch.A2sEvery * Rules.SilentPolls < 10);

        // ---- silence alone (path b)
        Check("silence: armed, A2S and RCON dead 10 s, 5 polls each -> kill", Rules.SilenceConfirmed(1010, 1000, 5, 1000, 5, true, true, false, 10));
        Check("silence: 9 s -> no kill", !Rules.SilenceConfirmed(1009, 1000, 5, 1000, 5, true, true, false, 10));
        Check("silence: RCON silent only 9 s -> no kill", !Rules.SilenceConfirmed(1010, 1000, 5, 1001.5, 5, true, true, false, 10));
        Check("silence: RCON answering -> no kill, however long A2S is silent", !Rules.SilenceConfirmed(99999, 1000, 999, null, 0, true, true, false, 10));
        Check("silence: two failed polls are not a streak, whatever the clock says", !Rules.SilenceConfirmed(1100, 1000, 2, 1000, 2, true, true, false, 10));
        Check("silence: not armed (never answered since it started) -> no kill", !Rules.SilenceConfirmed(1100, 1000, 9, 1000, 9, false, true, false, 10));
        Check("silence: no engine process -> no kill", !Rules.SilenceConfirmed(1100, 1000, 9, 1000, 9, true, false, false, 10));
        Check("silence: the watcher itself is taking it down -> no kill", !Rules.SilenceConfirmed(1100, 1000, 9, 1000, 9, true, true, true, 10));
        string[] quits = ["01/01/2001 - 12:05:34.547 - rcon from \"192.0.2.10:64265\": command \"quit\"",
                          "L 01/03/2001 - 12:01:02: rcon from \"192.0.2.101:40112\": command \"quit\"",
                          "L 01/03/2001 - 12:01:02: rcon from \"127.0.0.1:5555\": command \"_restart\""];
        foreach (var line in quits)
            Check($"quit echo: {line[^40..]}", ConsoleLines.IsQuitEcho(line));
        Check("quit echo: other rcon commands are not one", !ConsoleLines.IsQuitEcho("L 01/03/2001 - 12:01:02: rcon from \"127.0.0.1:5555\": command \"say quit\""));
        Check("quit echo: chat cannot fake it", !ConsoleLines.IsQuitEcho("L 01/03/2001 - 12:01:02: \"Evil<3><[U:1:999]><CT>\" say \"rcon from \"x\": command \"quit\"\""));

        // ---- the gates
        var z = Scenario.Paris;
        const long now = 1000000;
        var s = new WatchState { Restarts = [.. Enumerable.Range(0, 4).Select(i => new Restart(now - 700 - i * 1000, "daily"))] };
        Check("gate: 4 planned in 24 h -> blocked", Rules.PlannedBlock(s, now, z) is (true, _));
        s.Restarts = [new Restart(now - 300, "update")];
        Check("gate: planned min gap -> deferred", Rules.PlannedBlock(s, now, z) is (false, _));
        s.Restarts = [new Restart(now - 30, "freeze")];
        Check("gate: a freeze kill does not count as planned", Rules.PlannedBlock(s, now, z) is null);
        Check("gate: freeze min gap", Rules.FreezeBlock(s, now, z) is (false, _));
        s.Restarts = [.. Enumerable.Range(0, 10).Select(i => new Restart(now - 200 - i * 300, "freeze"))];
        Check("gate: 10 freeze kills in 24 h -> blocked", Rules.FreezeBlock(s, now, z) is (true, _));
        Check("gate: freeze breaker latched", Rules.FreezeBlock(new WatchState { FreezeTripped = 5 }, now, z) is (true, _));
        Check("gate: clock moved backwards", Rules.PlannedBlock(new WatchState { Restarts = [new Restart(now + 100, "daily")] }, now, z) is (true, _));
        Check("gate: a version restart is a planned one - it fills the cap and holds the floor",
              Rules.PlannedBlock(new WatchState { Restarts = [new Restart(now - 300, "version")] }, now, z) is (false, _)
              && Rules.PlannedBlock(new WatchState { Restarts = [.. Enumerable.Range(0, 4).Select(i => new Restart(now - 700 - i * 1000, "version"))] }, now, z) is (true, _));

        // ---- the state file
        Check("state: defaults validate", WatchState.FromJson(new WatchState().ToJson()).Fault is null);
        Check("state: unknown restart kind is a fault", WatchState.FromJson("{\"restarts\": [[1, \"other\"]]}").Fault is not null);
        Check("state: bool counter is a fault", WatchState.FromJson("{\"update_misses\": true}").Fault is not null);
        Check("state: old schema 1 file is a fault, not a reset", WatchState.FromJson("{\"schema\": 1}").Fault is not null);
        Check("state: pending round-trips", WatchState.FromJson("{\"pending\": {\"kind\": \"update\", \"at\": 5, \"from\": \"1.41.8.8\", \"required\": 14189}}").State.Pending?.Required == 14189);
        var py = "{\"done_day\": \"2026-10-04\", \"durations\": [14, 9], \"freeze_misses\": 0, \"freeze_tripped\": 0, \"pending\": null, \"restarts\": [[1759550400, \"daily\"]], "
                 + "\"schema\": 2, \"update_misses\": 1, \"update_retry_after\": 0, \"update_tripped\": 0, \"update_tripped_for\": 0}\n";
        var (fromPy, pyFault) = WatchState.FromJson(py);
        Check("state: a schema 2 file is read as it is, and written back as schema 3 with the same counters",
              pyFault is null && fromPy.DoneDay == "2026-10-04" && fromPy.Durations.SequenceEqual([14, 9]) && fromPy.UpdateMisses == 1
              && fromPy.Restarts.Single() == new Restart(1759550400, "daily") && WatchState.FromJson(fromPy.ToJson()).State.ToJson() == fromPy.ToJson()
              && fromPy.ToJson().Contains("\"schema\":3"));
        Check("state: a version restart and its pending record round-trip",
              WatchState.FromJson(new WatchState { Restarts = [new Restart(5, "version")], Pending = new Pending("version", 5, "1.41.8.8", null, "aaaa", "bbbb") }.ToJson()).State
                  is { Pending: { Kind: "version", Installed: "aaaa", Source: "bbbb" } } st && st.Restarts.Single().Kind == "version");

        // ---- the texts
        Check("back_in: default 30 before measurements", Rules.BackIn([]) == 30);
        Check("back_in: measured 14 s -> 20", Rules.BackIn([14]) == 20);
        Check("chat_safe: quotes and semicolons", Words.ChatSafe("a \"b\"; quit") == "a 'b', quit");
        Check("the download restart's line says when it is back", Rules.WorkshopLine(30).EndsWith("Back in about 30 seconds."));
    }
}
