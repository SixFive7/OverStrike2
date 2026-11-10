namespace OverStrike2;

// The cloak's decisions, free of CounterStrikeSharp so the self-test drives them on handed ticks and positions: who the spawn cloak hides
// (SpawnCloakPlan), which entities the cloak has painted and how each looked before (CloakBook), and whom it shields from the bots
// (CloakShield). Cloak.cs and BotGuard.cs feed them the game and do what they say.

/// <summary>Why a player is hidden. Each reason holds on its own; the player is seen again once none holds, and every reason ends with the
/// life (a death, a respawn, a disconnect, the map).</summary>
[Flags]
public enum CloakWhy
{
    None = 0,
    Ghost = 1, // the fun page's Ghost: a kill (FunEffects.cs)
    Spawn = 2, // the spawn cloak: the menu window open on the spawn (SpawnCloakPlan)
    Test = 4,  // os_cloak hide, from the console: to look at the hide from another player's game
    Arrival = 8, // the spawn invisibility: every human's spawn, until no enemy sees them (ArrivalPlan.cs)
}

/// <summary>The spawn cloak, per player and per life: a player whose menu window is open while they are on their spawn is hidden for as long as it
/// stays open, in every game mode. The cloak is the window's only: a spawn with the window shut is a normal spawn, seen by everybody. Hidden while
/// all of this holds:
///   - the menu window is open. It may have opened on this spawn or before it: the window stays open through damage and death, so a player
///     killed with it open respawns with it open, on their spawn, and the cloak comes on at that respawn;
///   - this life is still on its spawn: never more than Radius from the point it spawned at, sideways, nor Height up or down. The point is
///     taken one tick after the spawn, once the game has placed the pawn;
///   - this life has not attacked: no shot, swing or throw, and no damage dealt to anybody;
///   - no vote freeze is on, and none has started or ended during this life.
/// Leaving the spawn, attacking and a freeze's start or end spend the life's cloak: it does not come back until the next spawn, not even back on
/// the spot, so nobody walks somewhere and turns it on again, and nobody hides again after a fight. Closing the window only ends it until the
/// window opens again. There is no time limit: an open window keeps its player from looking around, and the cloak never leaves the spawn, so
/// nobody camps with it.
///
/// The radius: 64 units is two player widths (the hull is 32 wide), a quarter of a second of running - room for the pawn settling onto the floor
/// and a stray step, never for taking another position. Up or down it allows 72 units, a player's height: a jump on the spot (57 units) stays
/// on the spawn; a drop off a ledge, a lift or a jump pad does not.</summary>
public sealed class SpawnCloakPlan
{
    public const double Radius = 64, Height = 72;

    public const string Left = "left their spawn", Attacked = "attacked", Hurt = "dealt damage", FreezeStarted = "a vote freeze started",
        FreezeEnded = "a vote freeze ended";

    sealed class Life
    {
        public long SpawnTick;
        public bool Placed;
        public double X, Y, Z;
        public string? Spent;
    }

    readonly Dictionary<int, Life> lives = new();
    bool frozen;

    /// <summary>A new life: on its spawn, nothing spent. Its spawn point is taken at the first look a tick later.</summary>
    public void Spawned(int slot, long tick) => lives[slot] = new Life { SpawnTick = tick };

    /// <summary>The life is over: a death, a disconnect.</summary>
    public void Ended(int slot) => lives.Remove(slot);

    /// <summary>A new map: every life goes with the old one.</summary>
    public void MapStarted() { lives.Clear(); frozen = false; }

    /// <summary>The life's cloak is spent until the next spawn (it attacked). False when there is no such life or it was spent already.</summary>
    public bool Spend(int slot, string why)
    {
        if (!lives.TryGetValue(slot, out var life) || life.Spent != null) return false;
        life.Spent = why;
        return true;
    }

    /// <summary>Once a pass, before the players: a vote freeze that started or ended since the last pass spends every life's cloak. True when it did.</summary>
    public bool Freeze(bool frozenNow)
    {
        if (frozenNow == frozen) return false;
        frozen = frozenNow;
        foreach (var life in lives.Values) life.Spent ??= frozenNow ? FreezeStarted : FreezeEnded;
        return true;
    }

    /// <summary>One living player, this pass: whether the cloak hides them now. The window, and whether the moment allows it at all (no freeze),
    /// are the caller's. Takes the spawn point at the first look after the spawn's tick, and spends the life once the player is off it - open
    /// window or not.</summary>
    public bool Hides(int slot, long tick, double x, double y, double z, bool windowOpen, bool allowed)
    {
        if (!lives.TryGetValue(slot, out var life) || life.Spent != null) return false;
        if (!life.Placed)
        {
            if (tick <= life.SpawnTick) return false; // the game may still be placing the pawn
            (life.Placed, life.X, life.Y, life.Z) = (true, x, y, z);
        }
        if (Off(life.X, life.Y, life.Z, x, y, z)) { life.Spent = Left; return false; }
        return windowOpen && allowed;
    }

    /// <summary>A position off the spawn point: more than Radius away sideways, or more than Height up or down.</summary>
    public static bool Off(double spawnX, double spawnY, double spawnZ, double x, double y, double z)
    {
        double dx = x - spawnX, dy = y - spawnY;
        return dx * dx + dy * dy > Radius * Radius || Math.Abs(z - spawnZ) > Height;
    }

    /// <summary>The slots whose life still has its cloak (not spent), into a list the caller keeps: the ones to look at this pass.</summary>
    public void Unspent(List<int> into)
    {
        into.Clear();
        foreach (var (slot, life) in lives) if (life.Spent == null) into.Add(slot);
    }

    /// <summary>Whether the slot has a life whose cloak is not spent.</summary>
    public bool Ready(int slot) => lives.TryGetValue(slot, out var life) && life.Spent == null;

    /// <summary>Why the slot's cloak is spent, or null: not spent, or no life.</summary>
    public string? SpentBecause(int slot) => lives.TryGetValue(slot, out var life) ? life.Spent : null;

    /// <summary>For the console: where this life stands - "not placed yet", "on its spawn, 12 units away", "spent: left their spawn", "no life".</summary>
    public string Describe(int slot, double x, double y, double z)
    {
        if (!lives.TryGetValue(slot, out var life)) return "no life";
        if (life.Spent != null) return $"spent: {life.Spent}";
        if (!life.Placed) return "not placed yet";
        double dx = x - life.X, dy = y - life.Y;
        return $"on its spawn, {Math.Sqrt(dx * dx + dy * dy):0} units away and {z - life.Z:+0;-0;0} up";
    }
}

/// <summary>What the cloak has painted: every entity, by its handle (index and serial, so a reused index is another entity), with the hidden
/// player it belongs to now and how it looked before the cloak touched it. A pass holds every entity each hidden player carries now; what nobody
/// held in a pass has left them - dropped, thrown, taken away, its player seen again - and goes back to how it looked before.
///
/// An entity that moves from one hidden player to another keeps the look from before the first: its hidden look is never taken for its own.</summary>
public sealed class CloakBook<T>
{
    sealed class Entry(int slot, T before)
    {
        public int Slot = slot;
        public readonly T Before = before;
        public bool Held = true;
    }

    readonly Dictionary<uint, Entry> entries = new();
    readonly List<(uint Handle, int Slot, T Before)> gone = new(); // End's answer, kept: a pass runs every tick, and allocates nothing
    readonly List<uint> leaving = new();

    public int Count => entries.Count;

    /// <summary>A pass begins: nothing is held yet.</summary>
    public void Begin()
    {
        foreach (var e in entries.Values) e.Held = false;
    }

    /// <summary>The entity belongs to this hidden player now. True when the book has it already - its look from before stays as it was taken;
    /// false when it is new: the caller takes its look now and Adds it.</summary>
    public bool Hold(uint handle, int slot)
    {
        if (!entries.TryGetValue(handle, out var e)) return false;
        (e.Slot, e.Held) = (slot, true);
        return true;
    }

    /// <summary>A new entity of this hidden player, with its look from before the cloak touches it.</summary>
    public void Add(uint handle, int slot, T before) => entries[handle] = new Entry(slot, before);

    /// <summary>Everything this slot's hidden player holds counts as held in this pass: their look could not be taken this time (an error), so
    /// nothing of theirs goes back half-way.</summary>
    public void Keep(int slot)
    {
        foreach (var e in entries.Values) if (e.Slot == slot) e.Held = true;
    }

    /// <summary>The pass is over: every entity nobody held, with its look from before, out of the book - to be put back. The list is the book's
    /// own, good until the next End.</summary>
    public IReadOnlyList<(uint Handle, int Slot, T Before)> End()
    {
        gone.Clear();
        leaving.Clear();
        foreach (var (handle, e) in entries)
            if (!e.Held) { gone.Add((handle, e.Slot, e.Before)); leaving.Add(handle); }
        foreach (var handle in leaving) entries.Remove(handle);
        return gone;
    }

    /// <summary>Everything, out of the book: the plugin unloads and puts it all back.</summary>
    public List<(uint Handle, int Slot, T Before)> Clear()
    {
        var all = entries.Select(kv => (kv.Key, kv.Value.Slot, kv.Value.Before)).ToList();
        entries.Clear();
        return all;
    }

    /// <summary>Forget everything without putting anything back: a new map, whose entities are new.</summary>
    public void Forget() => entries.Clear();

    /// <summary>How many entities a slot's hidden player holds in the book.</summary>
    public int Of(int slot) => entries.Values.Count(e => e.Slot == slot);

    /// <summary>The looks from before of every entity a slot's hidden player holds, for the log.</summary>
    public IEnumerable<T> Held(int slot) => entries.Values.Where(e => e.Slot == slot).Select(e => e.Before);
}

/// <summary>The cloak's shield against the bots (BotGuard.cs). The cloak hides a player from people - the game draws nothing of them - but a bot aims by what
/// the server knows, and shoots a hidden player as readily as any: the live lab's player, on its spawn with the window open 19 s into the spawn cloak, fell to
/// one shot from a bot about 1300 units away. So a bot's damage to a hidden player is refused - until the player's own first attack in that hide: a shot, a
/// throw or a hit on somebody else ends the shield, though not the hide. The spawn cloak ends at such an attack anyway (SpawnCloakPlan.Attacked); Ghost keeps
/// a killer hidden for its seconds whatever they do, and os_cloak hide for as long as it was told, so without this a hidden player could fight the bots from
/// a cover the bots cannot answer. A hide is Cloak.Hide's number, new each time a player goes from seen to hidden: the next hide shields again, and an attack
/// in an earlier one never spends it.</summary>
public sealed class CloakShield
{
    readonly Dictionary<int, int> spent = new(); // slot -> the hide whose shield the player's own attack ended

    /// <summary>The player attacked, while in this hide (0: seen, which has no shield to end).</summary>
    public void Attacked(int slot, int hide)
    {
        if (hide != 0) spent[slot] = hide;
    }

    /// <summary>Whether a bot's damage to the player, in this hide, is refused: hidden, and no attack of theirs in it.</summary>
    public bool Shields(int slot, int hide) => hide != 0 && spent.GetValueOrDefault(slot) != hide;

    /// <summary>A map ends: every hide goes with it.</summary>
    public void Clear() => spent.Clear();
}
