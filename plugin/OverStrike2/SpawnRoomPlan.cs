namespace OverStrike2;

/// <summary>A team spawn point as the room's plan sees it: its entity's index, whether it is on, where it stands and which way it faces.</summary>
public sealed record SpawnSpot(int Index, bool Enabled, double X, double Y, double Z, double Yaw);

/// <summary>Room on a Wingman map for any number of players (SpawnRoom.cs), free of CounterStrikeSharp so the self-test drives it. The game holds a side at
/// its spawn points: a bot gets "Team is full" once every one is taken, and a joining human displaces a bot (the live lab, 2026-11-10, de_inferno at 4 v 4).
/// So a side always has room for one more than it holds: first the map's own spawn points that are off (Valve's maps switch their 5v5 set off in Wingman),
/// the nearest to the ones that are on first; then new ones - halfway between two that are on, two players apart at least and on one floor, the closest
/// pairs first, a pair once; and if that is not enough, on a spawn point that is on, round the side's ones - whether the game then frees two players on one
/// spot is the live lab's to see.</summary>
public static class SpawnRoomPlan
{
    /// <summary>Two spawn points at least this far apart sideways have room for a third halfway (the hull is 32 wide).</summary>
    public const double PairMin = 64;

    /// <summary>...and no more than this apart up or down: one floor.</summary>
    public const double PairMaxUp = 32;

    /// <summary>How many spawn points a side needs: room for one more than it holds, at most half the server's slots and one.</summary>
    public static int Needed(int onTeam, int maxPlayers) => Math.Max(1, Math.Min(onTeam + 1, (Math.Max(2, maxPlayers) + 1) / 2 + 1));

    /// <summary>What to do for a side to have `needed` spawn points on: the indices of its spots to switch on, and the new ones to make (where and facing).
    /// Nothing when it has them already.</summary>
    public static (List<int> Enable, List<(double X, double Y, double Z, double Yaw)> Add) Plan(IReadOnlyList<SpawnSpot> spots, int needed)
    {
        var enable = new List<int>();
        var add = new List<(double, double, double, double)>();
        var on = spots.Where(s => s.Enabled).ToList();
        var missing = needed - on.Count;
        if (missing <= 0) return (enable, add);
        // the map's own, switched off: the nearest to what is on first (with nothing on, in the map's order)
        foreach (var s in spots.Where(s => !s.Enabled).OrderBy(s => on.Count == 0 ? 0 : on.Min(o => Dist(o, s))).ThenBy(s => s.Index))
        {
            if (missing == 0) break;
            enable.Add(s.Index);
            on.Add(s with { Enabled = true });
            missing--;
        }
        if (missing == 0 || on.Count == 0) return (enable, add);
        // halfway between two that are on: room for a player, one floor, the closest pairs first
        var pairs = new List<(double D, SpawnSpot A, SpawnSpot B)>();
        for (var i = 0; i < on.Count; i++)
            for (var j = i + 1; j < on.Count; j++)
            {
                var (a, b) = (on[i], on[j]);
                var side = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
                if (side >= PairMin && Math.Abs(a.Z - b.Z) <= PairMaxUp) pairs.Add((side, a, b));
            }
        foreach (var (_, a, b) in pairs.OrderBy(p => p.D).ThenBy(p => p.A.Index).ThenBy(p => p.B.Index))
        {
            if (missing == 0) break;
            var mid = ((a.X + b.X) / 2, (a.Y + b.Y) / 2, Math.Max(a.Z, b.Z), a.Yaw);
            if (add.Any(p => Near(p, mid))) continue;
            add.Add(mid);
            missing--;
        }
        // on a spawn point that is on, round the side's ones
        for (var k = 0; missing > 0; k++, missing--)
        {
            var s = on[k % on.Count];
            add.Add((s.X, s.Y, s.Z, s.Yaw));
        }
        return (enable, add);
    }

    static double Dist(SpawnSpot a, SpawnSpot b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    static bool Near((double X, double Y, double Z, double Yaw) a, (double X, double Y, double Z, double Yaw) b) =>
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) < 32 * 32 && Math.Abs(a.Z - b.Z) <= PairMaxUp;
}
