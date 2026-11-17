// Who is on the server: the log stream says it, and RCON status has the last word when the stream knew nobody.
using System.Text.RegularExpressions;

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
        public DateTime? SigningOn;   // "connected" seen and "entered the game" not yet - any connect, a map change's included: its sign-on is not complete
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
        if (e.Kind == Kind.Connected) { p.Joining |= real; p.SigningOn = now; return false; } // a join survives the reconnects in between: a map change, MultiAddonManager's addon download
        var fresh = p.Joining || real;
        p.Joining = false; p.SigningOn = null;
        return fresh;
    }

    /// <summary>A "disconnected" line. Any reason with SHUTDOWN in it is a reconnect, not a departure: NETWORK_DISCONNECT_SHUTDOWN at a map
    /// change, NETWORK_DISCONNECT_LOOPSHUTDOWN when MultiAddonManager makes a joining client reconnect to fetch the HUD addon. A join under
    /// way stays under way through those, and so does a sign-on: the second "connected" comes a second later.</summary>
    static void Depart(string id, string reason, DateTime now)
    {
        var p = PlayerOf(id);
        if (reason.Contains("SHUTDOWN")) { if (p.GoneSince == null) p.ReloadSince ??= now; }
        else { p.GoneSince = now; p.ReloadSince = null; p.Joining = false; p.SigningOn = null; HudSeen.Remove(id); } // left for real - CS2's time-out of an unanswered download prompt too: the HUD check forgets them, and it is no sign-on
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

    /// <summary>The humans whose sign-on is not complete by the log stream: "connected" and not yet "entered the game" - at CS2's prompt to confirm a first
    /// Workshop download (the live lab saw the plugin's controllers say nothing of that state, while the "connected" line had come), downloading the map,
    /// loading it, a map change's reconnect. Within ReloadGraceSeconds of that line: an "entered" the stream missed does not hold anything for good.</summary>
    static List<string> SigningOnNames(DateTime now) => [.. Players.Where(kv => kv.Value is { GoneSince: null, SigningOn: { } at } && (now - at).TotalSeconds <= ReloadGraceSeconds)
        .OrderBy(kv => kv.Value.SigningOn).Select(kv => kv.Value.Name is { Length: > 0 } n ? n : kv.Key)];

    /// <summary>The players present: in game by the log stream, or reloading inside the load grace. One who left is not.</summary>
    static HashSet<string> PresentIds(DateTime now) => Players.Where(kv => kv.Value.GoneSince == null
        && (kv.Value.ReloadSince is not { } r || (now - r).TotalSeconds <= ReloadGraceSeconds)).Select(kv => kv.Key).ToHashSet();

    [GeneratedRegex(@"^players\s*:\s*(\d+) humans", RegexOptions.Multiline)] private static partial Regex HumansRx();

    /// <summary>Humans on the server. The connect/disconnect stream answers it for free; when that set is empty - the bridge may have started
    /// mid-map and missed the joins - one RCON status settles it, and its "players : N humans" line is well inside the first response packet.</summary>
    static int Humans() => Players.Values.Count(p => p.GoneSince == null && p.ReloadSince == null) is var n && n > 0 ? n
        : Send("status") is { } reply && HumansRx().Match(reply) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;
}
