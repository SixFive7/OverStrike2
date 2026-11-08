// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.

/// <summary>One map's worth of the check in the plugin, per player slot: whose clock runs, who was warned, whose time is up. Every map
/// change reconnects everybody, so it starts over then; a verification lasts until the player's next real join.
///
/// The end of a player's grace also ends the window the welcome or the check opened for them (ClosesWindow), whatever the mode but enforce, which
/// disconnects them: a game that cannot draw our window - a content before v4.8.0, a download that failed, a joiner during a hold - never clicks its
/// X, so the server would hold that window open all map, and the spawn cloak, which hides a player whose window is open on their spawn, would hide
/// them at every spawn, from people and, through the bots' guard, from the bots (release 194).</summary>
public sealed class HudGate
{
    /// <summary>Warn: the warning, WarnSeconds before the end. Kick: the end, with enforce. WouldKick: the end, with warn - a log line. Lapse: the end
    /// with the check off - nobody is told or disconnected, once, and the clock is not ended: off is also the pause while the bridge says nothing
    /// (HudCheck.ModeOf), after which the warning and its WarnSeconds still come.</summary>
    public enum Act { Warn, Kick, WouldKick, Lapse }

    /// <summary>Whether an act ends the player's window - the welcome pinned at their join, or the check's - and with it the spawn cloak: the end
    /// of the grace with warn or with the check off. With enforce the player is disconnected instead.</summary>
    public static bool ClosesWindow(Act act) => act is Act.WouldKick or Act.Lapse;

    sealed class Slot { public long Account; public DateTime Due; public bool Warned, Ended, Lapsed; }

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
        foreach (var s in waiting.Values) { s.Due = now.AddSeconds(HudCheck.GraceSeconds); s.Warned = s.Ended = s.Lapsed = false; }
        return waiting.Keys.ToList();
    }

    /// <summary>What is due now: the warning WarnSeconds before the end, then the disconnect - or, with warn, only the log line. With the check off
    /// nobody is warned or disconnected; the end is a Lapse, once, which only closes the window (ClosesWindow) and leaves the clock running. Verified
    /// players leave the list.</summary>
    public List<(int Slot, Act Act, int Seconds)> Frame(DateTime now, HudCheck.Mode mode, Func<long, bool> verified)
    {
        var acts = new List<(int, Act, int)>();
        var resumed = lastMode == HudCheck.Mode.Off && mode != HudCheck.Mode.Off;
        lastMode = mode;
        foreach (var (slot, s) in waiting.ToList())
        {
            if (verified(s.Account)) { waiting.Remove(slot); continue; }
            if (s.Ended) continue;
            if (mode == HudCheck.Mode.Off)
            {
                // Off is also the pause while the bridge says nothing: the end of the grace closes the window and nothing else - when the check
                // is back, the warning and its WarnSeconds still come before the disconnect, as below
                if (now >= s.Due && !s.Lapsed) { s.Lapsed = true; acts.Add((slot, Act.Lapse, 0)); }
                continue;
            }
            // The check was off for a while (no word from the bridge): nobody's warning is swallowed by the pause.
            if (resumed && !s.Warned && (s.Due - now).TotalSeconds < HudCheck.WarnSeconds) s.Due = now.AddSeconds(HudCheck.WarnSeconds);
            var left = (s.Due - now).TotalSeconds;
            if (!s.Warned && left <= HudCheck.WarnSeconds) { s.Warned = true; acts.Add((slot, Act.Warn, (int)Math.Ceiling(Math.Max(0, left)))); }
            if (left <= 0) { s.Ended = true; acts.Add((slot, mode == HudCheck.Mode.Enforce ? Act.Kick : Act.WouldKick, 0)); }
        }
        return acts;
    }
}
