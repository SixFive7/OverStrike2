namespace OverStrike2;

/// <summary>The spawn invisibility's decisions (ArrivalCloak.cs), free of CounterStrikeSharp so the self-test drives them on handed ticks, times and positions.
/// Every human who spawns is hidden from that moment - they and everything they carry, in every mode - until the first of:
///   - the first moment no enemy has them spotted (in free-for-all, no other player): the game's own spotted state, the radar's, which it re-evaluates about
///     every half second (the live lab, 2026-11-10: 0 to 0.5 s after a turn of the view, 0.27 s on average). The state is cleared for the radar after each
///     read while hidden, so a read between two evaluations finds nobody: "seen" holds SeenHold after the last read that found an enemy, and nothing ends
///     the hide that way before SeenHold after the spawn, so the first evaluation has come;
///   - their first attack: a shot, a swing, a throw, damage dealt to anybody else;
///   - leaving their spawn: the spawn cloak's notion, more than SpawnCloakPlan.Radius sideways or Height up or down from where the game placed them;
///   - MaxSeconds after the spawn.
/// Once ended it does not come back until the next spawn.</summary>
public sealed class ArrivalPlan
{
    public const double MaxSeconds = 10, SeenHold = 0.65;

    public const string Unseen = "no enemy sees them", Attacked = "attacked", Hurt = "dealt damage", TimeUp = "its 10 s are over";

    sealed class Life
    {
        public long SpawnTick;
        public double SpawnAt, SeenAt = double.NegativeInfinity;
        public bool Placed;
        public double X, Y, Z;
        public string? Spent;
    }

    readonly Dictionary<int, Life> lives = new();

    /// <summary>A human's new life: hidden from now. Its spawn point is taken at the first look a tick later.</summary>
    public void Spawned(int slot, long tick, double now) => lives[slot] = new Life { SpawnTick = tick, SpawnAt = now };

    /// <summary>The life is over: a death, a disconnect.</summary>
    public void Ended(int slot) => lives.Remove(slot);

    /// <summary>A new map: every life goes with the old one.</summary>
    public void MapStarted() => lives.Clear();

    /// <summary>The life's hide ends until the next spawn (it attacked). False when there is no such life or it ended already.</summary>
    public bool Spend(int slot, string why)
    {
        if (!lives.TryGetValue(slot, out var life) || life.Spent != null) return false;
        life.Spent = why;
        return true;
    }

    /// <summary>One living human, this pass: whether the hide still holds. seen: this pass's read of the game's spotted state names an enemy.</summary>
    public bool Hides(int slot, long tick, double now, double x, double y, double z, bool seen)
    {
        if (!lives.TryGetValue(slot, out var life) || life.Spent != null) return false;
        if (seen) life.SeenAt = now;
        if (!life.Placed)
        {
            if (tick > life.SpawnTick) (life.Placed, life.X, life.Y, life.Z) = (true, x, y, z); // the game may still be placing the pawn in the spawn's own tick
        }
        else if (SpawnCloakPlan.Off(life.X, life.Y, life.Z, x, y, z)) { life.Spent = SpawnCloakPlan.Left; return false; }
        if (now - life.SpawnAt >= MaxSeconds) { life.Spent = TimeUp; return false; }
        if (now - life.SpawnAt >= SeenHold && now - life.SeenAt > SeenHold) { life.Spent = Unseen; return false; }
        return true;
    }

    /// <summary>The slots whose life still hides (not ended), into a list the caller keeps.</summary>
    public void Unspent(List<int> into)
    {
        into.Clear();
        foreach (var (slot, life) in lives) if (life.Spent == null) into.Add(slot);
    }

    /// <summary>Whether the slot has a life whose hide has not ended.</summary>
    public bool Ready(int slot) => lives.TryGetValue(slot, out var life) && life.Spent == null;

    /// <summary>Why the slot's hide ended, or null: not ended, or no life.</summary>
    public string? SpentBecause(int slot) => lives.TryGetValue(slot, out var life) ? life.Spent : null;

    /// <summary>For the console: where this life stands.</summary>
    public string Describe(int slot, double now) =>
        !lives.TryGetValue(slot, out var life) ? "no life"
        : life.Spent != null ? $"seen since: {life.Spent}"
        : $"hidden {now - life.SpawnAt:0.0} s, {(double.IsNegativeInfinity(life.SeenAt) ? "no enemy has seen them" : $"an enemy saw them {now - life.SeenAt:0.0} s ago")}";
}
