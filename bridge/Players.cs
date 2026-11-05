// Who is on the server, from the log stream alone.
static partial class Program
{
    public const int ReloadGraceSeconds = 600;    // a map change logs every player as disconnected; reloading (and downloading) the next map may take this long

    // ---- presence: who is on the server, from the log stream alone ----

    /// <summary>One player as the log stream describes them. A map change logs every player as "disconnected" with reason
    /// NETWORK_DISCONNECT_SHUTDOWN and then "connected" again once they have loaded the new map (captured 2026-09-19), so that
    /// reason means "reloading", not "left"; any other reason (DISCONNECT_BY_USER, ...) is a real departure.</summary>
    sealed class Player
    {
        public string Name = "";
        public DateTime? GoneSince;   // really left at this time
        public DateTime? ReloadSince; // reloading since this map change or server restart
        public bool Joining;          // a real join is under way: "connected" after being away, "entered the game" still to come
    }

    static readonly Dictionary<string, Player> Players = new();

    static Player PlayerOf(string id) => Players.TryGetValue(id, out var p) ? p : Players[id] = new Player();

    /// <summary>A "connected" or "entered the game" line. Returns true when this is a real join, as opposed to the reconnect every map change causes.</summary>
    static bool Arrive(LogEvent e, DateTime now)
    {
        var known = Players.TryGetValue(e.SteamId, out var p);
        p ??= Players[e.SteamId] = new Player();
        var real = !known || p.GoneSince != null || (p.ReloadSince is { } r && (now - r).TotalSeconds > ReloadGraceSeconds);
        p.Name = e.Name; p.GoneSince = null; p.ReloadSince = null;
        if (e.Kind == Kind.Connected) { p.Joining |= real; return false; } // a join survives the reconnects in between: a map change, MultiAddonManager's addon download
        var fresh = p.Joining || real;
        p.Joining = false;
        return fresh;
    }

    /// <summary>A "disconnected" line. Any reason with SHUTDOWN in it is a reconnect, not a departure: NETWORK_DISCONNECT_SHUTDOWN at a map
    /// change, NETWORK_DISCONNECT_LOOPSHUTDOWN when MultiAddonManager makes a joining client reconnect to fetch the HUD addon. A join under
    /// way stays under way through those.</summary>
    static void Depart(string id, string reason, DateTime now)
    {
        var p = PlayerOf(id);
        if (reason.Contains("SHUTDOWN")) { if (p.GoneSince == null) p.ReloadSince ??= now; }
        else { p.GoneSince = now; p.ReloadSince = null; p.Joining = false; HudSeen.Remove(id); } // left for real: the HUD check forgets them
    }

    /// <summary>A chat line or a click proves its sender is on the server.</summary>
    static void Present(string id, string name)
    {
        var p = PlayerOf(id);
        p.Name = name; p.GoneSince = null; p.ReloadSince = null;
    }

    /// <summary>A map start or a server restart: everyone in game is now reloading. The log usually said so already; this is the safety net.</summary>
    static void AllReloading(DateTime now)
    {
        foreach (var p in Players.Values) if (p.GoneSince == null) p.ReloadSince ??= now;
    }
}
