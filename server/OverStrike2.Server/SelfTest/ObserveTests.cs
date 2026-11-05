using static OverStrike2.Server.SelfTest.T;

namespace OverStrike2.Server.SelfTest;

/// <summary>THE OBSERVING WATCHER (watch --observe), for a run beside the acting one: it looks and says what it would do, and does none of it -
/// no quit, no kill, no download, no line on the HUD, nothing posted to the bridge, and its counters in a state folder of its own.</summary>
static class ObserveTests
{
    static DateTimeOffset At(int d, int h, int m, int s) => Scenario.At(2026, 10, d, h, m, s);

    public static void Run()
    {
        var observe = Path.Combine(Scenario.Root, "update-watch", "observe");
        Scenario.NewBridge = () => new FakeBridge();
        var s = Scenario.Make(At(4, 6, 59, 50), observe: true, state: observe);
        s.Step(40);
        Check($"observe: the daily restart that is due is a log line - no quit, nothing posted ({Show(s.Log.Lines.Where(l => l.StartsWith("OBSERVE")).Take(3))})",
              !s.Has("quit") && s.Bridge.Posts.Count == 0 && s.Log.Lines.Any(l => l == "OBSERVE: would send over RCON: quit")
              && s.Log.Lines.Any(l => l.StartsWith("OBSERVE: would POST /quitting: gg-quit 1 daily")));
        Check("observe: its counters are its own: the acting watcher's state file is not touched",
              File.Exists(Path.Combine(observe, "state.json")) && !File.Exists(Path.Combine(Scenario.Root, "update-watch", "state.json")) && s.W.State.DoneDay == "2026-10-04");
        Check("observe: it looks like the acting one does - A2S, RCON status, the console probe's echo",
              s.Has("status") && s.Eng.Cmds.Any(c => c.StartsWith("echo ", StringComparison.Ordinal)));
        s = Scenario.Make(At(4, 15, 0, 0), observe: true, state: observe);
        s.Step(20);
        s.Eng.Freeze();
        s.Step(30);
        Check("observe: a freeze is a log line, not a kill", s.Eng.Killed.Count == 0 && s.Log.Lines.Any(l => l.StartsWith("OBSERVE: would kill the engine")));
        s = Scenario.Make(At(4, 15, 0, 0), ("stale", 14189, null), observe: true, state: observe);
        (s.Eng.Humans, s.Eng.Hud) = (3, true);
        s.Step(60);
        Check("observe: an update with players on: no line on the HUD, nothing posted - what the bridge would have been told is a log line",
              s.Eng.Notices.Count == 0 && s.Eng.Chat.Count == 0 && s.Bridge.Posts.Count == 0
              && s.Log.Lines.Any(l => l.StartsWith("OBSERVE: would POST /restart-notice: gg-restart 1 game-update match-end 10")));
        Scenario.NewBridge = () => null;
    }
}
