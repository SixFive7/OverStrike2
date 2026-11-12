// --selftest, the fairness restart's plan (Shared/EarlyJoin.cs) on a handed clock: which spawn is a join, when the match runs, the minute's window that every
// restart opens again, a reload, the words. The bridge's half - the ask on /act, the countdown, joins folding in, os_restart and its refusals - is
// MatchSelfTest's (SelfTestMatch.cs); that the game fires its round start and spawns as the plugin expects is the live lab's.
static partial class Program
{
    static void EarlyJoinSelfTest(Action<string, bool> Check)
    {
        var t0 = new DateTime(2026, 11, 10, 20, 0, 0, DateTimeKind.Utc);
        const long Ann = 1, Ben = 2, Cas = 3, Dee = 4;
        DateTime At(double s) => t0.AddSeconds(s);

        EarlyJoin Map()
        {
            var p = new EarlyJoin();
            p.MapStarted();
            p.MatchStarted(At(0), 0); // the map's first round start, everybody still loading
            return p;
        }

        var plan = Map();
        Check("Fairness: the first human in starts the match running and asks nothing - nobody else plays it",
            !plan.Spawned(Ann, At(15), 0) && plan.Running(At(20)) == 5);
        Check("...the next one in, 30 s on, joins a match somebody plays in its first minute: a restart",
            plan.Spawned(Ben, At(45), 1));
        Check("...a respawn, the restart's own spawn, a team switch are no joins", !plan.Spawned(Ann, At(46), 1) && !plan.Spawned(Ben, At(51), 1));
        plan.MatchStarted(At(51), 2); // the restart's round start: a new start, the window open again
        Check("...the restart's round start opens the window again: Cas, 50 s after it, restarts the match again - not once per map",
            plan.Spawned(Cas, At(101), 2) && plan.Running(At(101)) == 50);
        plan.MatchStarted(At(107), 3);
        Check($"...and a join more than {EarlyJoin.WindowSeconds} s after the last start plays on", !plan.Spawned(Dee, At(107 + EarlyJoin.WindowSeconds), 3));
        var ten = Map();
        var restarts = 0;
        for (var i = 0; i < 10; i++) // the i-th player in, 30 s after the one before, with i on a team; each restart's round start comes 6 s after its ask
        {
            var at = At(10 + i * 30);
            if (ten.Spawned(100 + i, at, i)) { restarts++; ten.MatchStarted(at.AddSeconds(EarlyJoin.CountdownSeconds + 1), i + 1); }
        }
        Check($"Fairness: a 10-player match, each one in 30 s after the one before, restarts {restarts} times - every join but the first's, as the owner expects", restarts == 9);

        plan = Map();
        plan.Spawned(Ann, At(10), 0);
        plan.Left(Ann);
        Check("Fairness: somebody who leaves for real and comes back has connected again - their first spawn is a join (with Ben playing)",
            !plan.Spawned(Ben, At(20), 0) && plan.Spawned(Ann, At(30), 1));
        plan = Map();
        plan.Spawned(Ann, At(10), 0);
        Check("Fairness: a join with nobody else on a team - the others left - starts the match running again instead of restarting it",
            !plan.Spawned(Ben, At(40), 0) && plan.Running(At(40)) == 0 && plan.Spawned(Cas, At(50), 1));
        plan = Map();
        plan.Spawned(Ann, At(10), 0);
        plan.Spawned(Ben, At(20), 1);
        plan.MatchStarted(At(26), 2); // the fairness restart for Ben
        // The match ends, and its screen runs out on the same map: the game moves Ann and Ben to Unassigned, the plugin puts them back on their sides
        // (TeamKeep.cs), and the game's start finds both on a side. No map started: the map's record of who joined stays.
        plan.MatchStarted(At(700), 2);
        Check("Fairness: players put back on their sides after a match-end screen ran out on the same map are no joins - no map start, so the record of who joined stays",
            !plan.Spawned(Ann, At(700), 1) && !plan.Spawned(Ben, At(700), 1) && plan.Running(At(710)) == 10);
        Check("...while somebody who joined during that screen, and picks a side in the new match's first minute, is a join: the match restarts for them",
            plan.Spawned(Cas, At(730), 2));
        plan = Map();
        plan.MatchStarted(At(0), 2); // a restart, or the game commencing, with two on a team
        Check("Fairness: a match that starts with humans on a team runs from its start: a join 20 s in restarts it", plan.Spawned(Ann, At(20), 2));

        plan = Map();
        plan.Force(At(5));
        Check("Fairness: os_fair live - the match runs from now and the next join counts as joining somebody, so a lone test game's spawn asks",
            plan.Spawned(Ann, At(10), 0) && plan.Describe(At(10)).Contains("restarts it"));
        plan = Map();
        plan.Force(At(5));
        plan.MatchStarted(At(8), 0); // the game commencing as the lone test game picks a team
        Check("...and the game commencing as that lone game picks a team does not undo it", plan.Spawned(Ann, At(9), 0));

        plan = Map();
        plan.Skip("the plugin loaded during this map");
        Check("Fairness: a plugin loaded mid-map does nothing on that map", !plan.Spawned(Ann, At(5), 0) && !plan.Spawned(Ben, At(10), 1)
            && plan.Describe(At(10)).Contains("the plugin loaded during this map"));
        plan.MapStarted();
        plan.MatchStarted(At(100), 0);
        Check("...and is itself again from the next map on", !plan.Spawned(Ann, At(105), 0) && plan.Spawned(Ben, At(110), 1));

        Check("Fairness: its words - one name, two, three, more; the line for everybody and the banner's title",
            EarlyJoin.Names(["Bob"]) == "Bob" && EarlyJoin.Names(["Bob", "Cas"]) == "Bob and Cas" && EarlyJoin.Names(["Bob", "Cas", "Dee"]) == "Bob, Cas and Dee"
            && EarlyJoin.Names(["Bob", "Cas", "Dee", "Eve", "Fay"]) == "Bob, Cas, Dee and 2 more"
            && M.Plain(EarlyJoin.Line("Bob", 1)) == $"The match restarts for fairness in {EarlyJoin.CountdownSeconds} s - Bob is in"
            && M.Plain(EarlyJoin.Line("Bob and Cas", 2, 3)) == "The match restarts for fairness in 3 s - Bob and Cas are in"
            && EarlyJoin.BannerTitle("Bob") == "The match restarts for fairness - Bob is in" && EarlyJoin.BannerTitle("Bob and Cas", 2) == "The match restarts for fairness - Bob and Cas are in");
        Check("Fairness: its word on /act is the plugin's alone - a click may carry it, chat has no such command",
            Hud.IsAction(EarlyJoin.Command) && ChatCommands.Parse(EarlyJoin.Command).Kind == ChatCommands.Kind.Unknown);
    }
}
