// --selftest, the fairness restart's plan (Shared/EarlyJoin.cs) on a handed clock: who came into a match late, how long it waits for the map before's players,
// once per map, the modes, a reload. The bridge's half - the ask on /act, the countdown, os_restart and its refusals - is MatchSelfTest's (SelfTestMatch.cs);
// that the game fires its round start and spawns as the plugin expects is the live lab's.
static partial class Program
{
    static void EarlyJoinSelfTest(Action<string, bool> Check)
    {
        var t0 = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
        const long Ann = 1, Ben = 2, Cas = 3, Dee = 4;
        DateTime At(double s) => t0.AddSeconds(s);

        // A map with Ann, Ben and Cas on, then the next map: Ann picks a team first, the others load more slowly.
        EarlyJoin Next(params long[] onTheMapBefore)
        {
            var p = new EarlyJoin();
            p.MapStarted();
            foreach (var a in onTheMapBefore) p.Spawned(a, $"player {a}", t0.AddMinutes(-30)); // seen on the map before
            p.MapStarted();
            return p;
        }

        var plan = Next(Ann, Ben, Cas);
        Check("Fairness: no round start with a human on a team, no live match - nothing is late and nothing is asked",
            !plan.Live(At(0), [], true) && !plan.Spawned(Ben, "Ben", At(5)) && plan.Due(At(70)) == null);

        plan = Next(Ann, Ben, Cas);
        var live = plan.Live(At(0), [Ann], true);
        var benLate = plan.Spawned(Ben, "Ben", At(12));
        var waitsForCas = plan.Due(At(12)) == null && plan.Waiting == 1;
        var casLate = plan.Spawned(Cas, "Cas", At(29));
        var asked = plan.Due(At(29));
        Check("Fairness: the match goes live with Ann on a team; Ben and Cas come in late - the ask waits until Cas, the last of the map before, has spawned, and names him",
            live && benLate && waitsForCas && casLate && asked == (Cas, "Cas"));
        Check("...once per map: it never asks again, not even for a later late join", plan.Due(At(30)) == null && plan.Spawned(Dee, "Dee", At(40)) && plan.Due(At(41)) == null);

        plan = Next(Ann, Ben, Cas);
        plan.Live(At(0), [Ann], true);
        plan.Spawned(Ben, "Ben", At(10));
        Check($"Fairness: one of the map before never comes back - the ask waits for him until the window's {EarlyJoin.WindowSeconds} s are over, then names the last one in",
            plan.Due(At(EarlyJoin.WindowSeconds - 1)) == null && plan.Due(At(EarlyJoin.WindowSeconds)) == (Ben, "Ben"));

        plan = Next(Ann, Ben, Cas);
        plan.Live(At(0), [Ann], true);
        plan.Spawned(Ben, "Ben", At(10));
        plan.Left(Cas);
        Check("Fairness: one of the map before leaves for real - nobody waits for him: the ask comes at once", plan.Due(At(11)) == (Ben, "Ben"));

        plan = Next(Ann, Ben);
        plan.Live(At(0), [Ann, Ben], true);
        var deeLate = plan.Spawned(Dee, "Dee", At(20));
        Check("Fairness: everybody of the map before started the match; a new joiner 20 s in is a late join too, and asks at once", deeLate && plan.Due(At(20)) == (Dee, "Dee"));

        plan = Next(Ann, Ben);
        plan.Live(At(0), [Ann], true);
        var tooLate = plan.Spawned(Ben, "Ben", At(EarlyJoin.WindowSeconds + 1));
        Check($"Fairness: a first spawn more than {EarlyJoin.WindowSeconds} s after the match went live is no late join: nothing is asked", !tooLate && plan.Due(At(EarlyJoin.WindowSeconds + 2)) == null);

        plan = Next(Ann, Ben);
        plan.Live(At(0), [Ann], true);
        plan.Spawned(Ann, "Ann", At(3)); // Ann's respawn
        Check("Fairness: a spawn of somebody who started the match - a respawn, the restart's own - is never a late join", plan.Due(At(70)) == null);

        plan = Next(Ann, Ben);
        var first = plan.Live(At(0), [Ann], true);
        var again = plan.Live(At(30), [Ann, Ben], true);
        plan.Spawned(Ben, "Ben", At(31));
        Check("Fairness: only the map's first live round start counts - a later one (a Restart match, the next match on the map) moves nothing, so Ben at 31 s is late",
            first && !again && plan.Due(At(31)) == (Ben, "Ben"));

        plan = Next(Ann, Ben);
        plan.Live(At(0), [Ann], false);
        plan.Spawned(Ben, "Ben", At(10));
        Check("Fairness: never in a mode it does not apply to (Wingman keeps its warmup)", plan.Due(At(70)) == null
            && EarlyJoin.Applies(Modes.ArmsRaceId) && EarlyJoin.Applies(Modes.DeathmatchId) && !EarlyJoin.Applies(Modes.WingmanId));

        plan = Next(Ann, Ben);
        plan.Skip("the plugin loaded during this map");
        Check("Fairness: a plugin loaded mid-map does nothing on that map", !plan.Live(At(0), [Ann], true) && !plan.Spawned(Ben, "Ben", At(10)) && plan.Due(At(70)) == null
            && plan.Describe(At(70)).Contains("the plugin loaded during this map"));
        plan.MapStarted();
        Check("...and is itself again from the next map on, which waits for the humans seen on this one", plan.Live(At(100), [Ann], true) && plan.Waiting == 1);

        Check("Fairness: its words - the line for everybody and the banner's title", M.Plain(EarlyJoin.Line("Bob")) == $"The match restarts for fairness in {EarlyJoin.CountdownSeconds} s - Bob is in"
            && EarlyJoin.BannerTitle("Bob") == "The match restarts for fairness - Bob is in");
        Check("Fairness: its word on /act is the plugin's alone - a click may carry it, chat has no such command",
            Hud.IsAction(EarlyJoin.Command) && ChatCommands.Parse(EarlyJoin.Command).Kind == ChatCommands.Kind.Unknown);
    }
}
