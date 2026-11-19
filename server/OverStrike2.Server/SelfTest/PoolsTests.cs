using OverStrike2.Core;
using static OverStrike2.TestKit.T;

namespace OverStrike2.Server.SelfTest;

/// <summary>The game's own map pools at every engine start (Watch.Pools.cs): read once, sent to the bridge until it takes them, given up after
/// PoolsTries, and nothing sent from a read with a problem - on the real loop, a fake clock and a fake bridge.</summary>
static class PoolsTests
{
    public static void Run()
    {
        var found = new GamePools.Found("1.41.9.4",
            [new("gungame", "deathmatch", 1, 2, ["de_alpha", "de_beta", "de_gone"], ["de_gone"]), new("classic", "scrimcomp2v2", 0, 2, ["de_alpha"], [])],
            ["de_alpha", "de_beta"], new(StringComparer.OrdinalIgnoreCase) { ["de_alpha"] = "Alpha" }, null);
        var reads = 0;
        Scenario.NewGame = dir => { reads++; return dir.EndsWith("game", StringComparison.Ordinal) ? found : found with { Problem = "the wrong folder" }; };
        var refusals = 2;
        Scenario.NewBridge = () => new FakeBridge { Reply = (what, _) => what == BridgeWire.Pools && refusals-- > 0 ? null : "ok: pools taken" };
        var s = Scenario.Make(Scenario.At(2026, 10, 4, 15, 0, 0));
        s.Step(200);
        var posts = s.Bridge.Bodies(BridgeWire.Pools);
        Check($"pools: the watcher reads the game's own map pools once, from STEAMAPPDIR/game, and says what they hold ({Show(s.Lines.Where(l => l.Contains("map pools")))})",
              reads == 1 && s.Lines.Any(l => l.Contains("the game's own map pools, CS2 1.41.9.4: deathmatch 2, scrimcomp2v2 1; 2 maps installed; named but not installed: de_gone")));
        Check($"pools: ... sends them to the bridge, again every {Watch.PoolsRetry:0} s while it refuses, and no more once it took them ({posts.Count} posts)",
              posts.Count == 3 && posts.All(b => b == BridgeWire.PoolsBody(found)) && s.Lines.Count(l => l.Contains("did not take the game's own map pools")) == 1
              && s.Lines.Any(l => l.Contains("the bridge took the game's own map pools (ok: pools taken)")));
        s.Step(600);
        Check("pools: ... and never again in this watcher's life", s.Bridge.Bodies(BridgeWire.Pools).Count == 3 && reads == 1);

        Scenario.NewBridge = () => new FakeBridge { Reply = (what, _) => what == BridgeWire.Pools ? "refused: not now" : "ok" };
        s = Scenario.Make(Scenario.At(2026, 10, 4, 15, 0, 0));
        s.Step(Watch.PoolsRetry * Watch.PoolsTries + 120);
        Check($"pools: a bridge that never takes them is given up on after {Watch.PoolsTries} tries, with a warning - it keeps the pools it has",
              s.Bridge.Bodies(BridgeWire.Pools).Count == Watch.PoolsTries && s.Lines.Any(l => l.StartsWith("WARNING: the bridge did not take the game's own map pools", StringComparison.Ordinal)));

        Scenario.NewGame = _ => found with { Pools = [], Problem = "no gamemodes.txt in pak01_dir.vpk" };
        Scenario.NewBridge = () => new FakeBridge();
        s = Scenario.Make(Scenario.At(2026, 10, 4, 15, 0, 0));
        s.Step(120);
        Check("pools: a read with a problem sends nothing and says why: the bridge keeps the pools it has",
              s.Bridge.Bodies(BridgeWire.Pools).Count == 0 && s.Lines.Any(l => l.Contains("WARNING: the game's own map pools could not be read (no gamemodes.txt in pak01_dir.vpk)")));

        Scenario.NewGame = _ => found;
        Scenario.NewBridge = () => null;
        s = Scenario.Make(Scenario.At(2026, 10, 4, 15, 0, 0));
        s.Step(120);
        Check("pools: without a bridge configured nothing is sent, and the log says so once", s.Lines.Count(l => l.Contains("no bridge to tell the game's own map pools")) == 1);

        Scenario.NewGame = null;
        Scenario.NewBridge = () => null;
    }
}
