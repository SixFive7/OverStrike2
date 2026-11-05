// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.

/// <summary>One map's worth of the check in the plugin, per player slot: whose clock runs, who was warned, whose time is up. Every map
/// change reconnects everybody, so it starts over then; a verification lasts until the player's next real join.</summary>
public sealed class HudGate
{
    public enum Act { Warn, Kick, WouldKick }

    sealed class Slot { public long Account; public DateTime Due; public bool Warned, Ended; }

    readonly Dictionary<int, Slot> waiting = new();
    readonly HashSet<int> spawned = new(); // slots that spawned on this map already
    HudCheck.Mode lastMode = HudCheck.Mode.Off;

    /// <summary>A spawn. The first on this map of a player the server has not seen click starts their clock - true then, so the plugin
    /// opens the window. Later spawns, and verified players, change nothing.</summary>
    public bool Spawned(int slot, long account, bool verified, DateTime now)
    {
        if (!spawned.Add(slot) || verified) return false;
        waiting[slot] = new Slot { Account = account, Due = now.AddSeconds(HudCheck.GraceSeconds) };
        return true;
    }

    /// <summary>Still waiting for this slot's click: the gold button says "Click to play" for them, and a click is the check, not a command.</summary>
    public bool Checking(int slot) => waiting.ContainsKey(slot);

    public void Verified(int slot) => waiting.Remove(slot);

    /// <summary>The seconds this slot's clock has left (the Players page says it); 0 when nothing waits.</summary>
    public int SecondsLeft(int slot, DateTime now) => waiting.TryGetValue(slot, out var s) && !s.Ended ? Math.Max(0, (int)Math.Ceiling((s.Due - now).TotalSeconds)) : 0;

    public void Gone(int slot) { waiting.Remove(slot); spawned.Remove(slot); }

    public void MapStarted() { waiting.Clear(); spawned.Clear(); }

    /// <summary>The check has just become stricter (off to warn, or warn to enforce): everybody still waiting gets a full grace
    /// period from now, so nobody who ignored the dry run is thrown out on the spot. Returns their slots, for the window.</summary>
    public List<int> Rearm(DateTime now)
    {
        foreach (var s in waiting.Values) { s.Due = now.AddSeconds(HudCheck.GraceSeconds); s.Warned = s.Ended = false; }
        return waiting.Keys.ToList();
    }

    /// <summary>What is due now: the warning WarnSeconds before the end, then the disconnect - or, with warn, only the log line.
    /// Verified players leave the list; with the check off nothing happens at all.</summary>
    public List<(int Slot, Act Act, int Seconds)> Frame(DateTime now, HudCheck.Mode mode, Func<long, bool> verified)
    {
        var acts = new List<(int, Act, int)>();
        var resumed = lastMode == HudCheck.Mode.Off && mode != HudCheck.Mode.Off;
        lastMode = mode;
        if (mode == HudCheck.Mode.Off) return acts;
        foreach (var (slot, s) in waiting.ToList())
        {
            if (verified(s.Account)) { waiting.Remove(slot); continue; }
            if (s.Ended) continue;
            // The check was off for a while (no word from the bridge): nobody's warning is swallowed by the pause.
            if (resumed && !s.Warned && (s.Due - now).TotalSeconds < HudCheck.WarnSeconds) s.Due = now.AddSeconds(HudCheck.WarnSeconds);
            var left = (s.Due - now).TotalSeconds;
            if (!s.Warned && left <= HudCheck.WarnSeconds) { s.Warned = true; acts.Add((slot, Act.Warn, (int)Math.Ceiling(Math.Max(0, left)))); }
            if (left <= 0) { s.Ended = true; acts.Add((slot, mode == HudCheck.Mode.Enforce ? Act.Kick : Act.WouldKick, 0)); }
        }
        return acts;
    }
}
