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
        if (e.Kind == Kind.Connected) { p.Joining |= real; p.SigningOn = now; SignOnsChanged(); return false; } // a join survives the reconnects in between: a map change, MultiAddonManager's addon download
        var fresh = p.Joining || real;
        p.Joining = false;
        if (p.SigningOn != null) { p.SigningOn = null; SignOnsChanged(); }
        return fresh;
    }

    /// <summary>A "disconnected" line. Any reason with SHUTDOWN in it is a reconnect, not a departure: NETWORK_DISCONNECT_SHUTDOWN at a map
    /// change, NETWORK_DISCONNECT_LOOPSHUTDOWN when MultiAddonManager makes a joining client reconnect to fetch the HUD addon. A join under
    /// way stays under way through those, and so does a sign-on: the second "connected" comes a second later.</summary>
    static void Depart(string id, string reason, DateTime now)
    {
        var p = PlayerOf(id);
        if (reason.Contains("SHUTDOWN")) { if (p.GoneSince == null) p.ReloadSince ??= now; }
        else // left for real: the HUD check forgets them, and it is no sign-on
        {
            p.GoneSince = now; p.ReloadSince = null; p.Joining = false; HudSeen.Remove(id);
            if (p.SigningOn != null) { p.SigningOn = null; SignOnsChanged(); }
        }
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
    /// Workshop download, downloading the map, loading it, a map change's reconnect. For the prompt's joiner this line is all the game writes: the game hears of a
    /// client only once it has every addon (no controller, nothing in os_joining - the live lab's runs AW and AX), and CS2's drop of it writes no "disconnected".
    /// So it counts EarlyJoin.SignOnMostSeconds at the most from its "connected", and the server's own list ends it sooner where it is asked (EndDroppedSignOns).</summary>
    static List<string> SigningOnNames(DateTime now) => [.. Players.Where(kv => kv.Value is { GoneSince: null, SigningOn: { } at } && (now - at).TotalSeconds <= EarlyJoin.SignOnMostSeconds)
        .OrderBy(kv => kv.Value.SigningOn).Select(kv => kv.Value.Name is { Length: > 0 } n ? n : kv.Key)];

    /// <summary>The sign-ons the server's own client list (status) no longer has: gone before they were in - CS2's drop of a first-download prompt nobody answered
    /// among them, which writes no "disconnected" line. Not one whose "connected" came in the last 3 s (the list and the log stream may cross), nor one mid-reconnect
    /// (a SHUTDOWN departure in the last 10 s: the HUD addon's reconnect, a map change's), whose next "connected" is a second away.</summary>
    static void EndDroppedSignOns(IReadOnlyList<EarlyJoin.StatusClient> clients, DateTime now)
    {
        var listed = clients.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var ended = false;
        foreach (var p in Players.Values)
        {
            if (p is not { GoneSince: null, SigningOn: { } at } || (now - at).TotalSeconds < 3 || listed.Contains(p.Name) || p.ReloadSince is { } r && (now - r).TotalSeconds <= 10) continue;
            p.SigningOn = null; ended = true;
            Log($"fair: {p.Name} was connecting, and the server lists them no more - gone before they were in, which CS2 writes no \"disconnected\" line for");
        }
        if (ended) SignOnsChanged();
    }

    /// <summary>What the plugin's os_fair last heard from the bridge (EarlyJoin.BridgeViewCommand) - whom its log stream has signing on and status's joiners -
    /// and whether a word to it is due: one a second at the most, and only when either changed, so a join's "connected" and "entered the game" a moment apart
    /// say nothing.</summary>
    static List<string> SignOnsTold = [];
    static List<string>? StatusTold;
    static bool SignOnsTell;

    /// <summary>The server's own list (status), read every StatusWatchSeconds while anyone signs on by the log stream or its last reading had anyone joining, and
    /// each second of a fairness restart's wait (FairWait): the humans not yet active at the last reading (null: none read since the bridge started, or no players
    /// table), and whether a reading is due. Not while a map loads (LoadPending): its RCON answers late or not at all then.</summary>
    const int StatusWatchSeconds = 2;
    static List<string>? StatusJoining;
    static bool StatusWatchDue;

    static void WatchStatus()
    {
        if (StatusWatchDue) return;
        StatusWatchDue = true;
        After(StatusWatchSeconds * 1000, StatusWatchTick);
    }

    static void StatusWatchTick()
    {
        StatusWatchDue = false;
        var now = Now();
        if (SigningOnNames(now).Count == 0 && StatusJoining is not { Count: > 0 }) return; // nobody joining: the watch rests until the next "connected"
        if (!LoadPending) ReadServerList(now);
        WatchStatus();
    }

    /// <summary>status, read now: its humans not yet active (StatusJoining, told to os_fair when they change), and the log stream's sign-ons it no longer has
    /// ended (EndDroppedSignOns) - CS2's drop of a first-download prompt, which writes no line. null: no players table.</summary>
    static List<EarlyJoin.StatusClient>? ReadServerList(DateTime now)
    {
        var clients = EarlyJoin.ReadStatus(Send("status"));
        if (clients != null) EndDroppedSignOns(clients, now);
        var joining = clients?.Where(c => c.Joining).Select(c => c.Name).ToList();
        if (!SameNames(joining, StatusJoining)) { StatusJoining = joining; SignOnsChanged(); }
        return clients;
    }

    static bool SameNames(List<string>? a, List<string>? b) => a == null ? b == null : b != null && a.SequenceEqual(b);

    static void SignOnsChanged()
    {
        if (SignOnsTell) return;
        SignOnsTell = true;
        After(1000, TellSignOns);
    }

    static void TellSignOns()
    {
        SignOnsTell = false;
        var now = Now();
        var names = SigningOnNames(now);
        if ((!names.SequenceEqual(SignOnsTold) || !SameNames(StatusJoining, StatusTold)) && Send(EarlyJoin.BridgeViewCommand(names, StatusJoining)) != null)
            (SignOnsTold, StatusTold) = (names, StatusJoining); // no answer: the next change tells it
        if (names.Count > 0) WatchStatus();
        var open = Players.Values.Where(p => p is { GoneSince: null, SigningOn: not null } && (now - p.SigningOn.Value).TotalSeconds <= EarlyJoin.SignOnMostSeconds).Select(p => p.SigningOn!.Value).ToList();
        if (open.Count > 0) After((int)Math.Max(1000, (open.Min().AddSeconds(EarlyJoin.SignOnMostSeconds + 1) - now).TotalMilliseconds), SignOnsChanged); // the bound ends one with no line
    }

    /// <summary>The players present: in game by the log stream, or reloading inside the load grace. One who left is not.</summary>
    static HashSet<string> PresentIds(DateTime now) => Players.Where(kv => kv.Value.GoneSince == null
        && (kv.Value.ReloadSince is not { } r || (now - r).TotalSeconds <= ReloadGraceSeconds)).Select(kv => kv.Key).ToHashSet();

    [GeneratedRegex(@"^players\s*:\s*(\d+) humans", RegexOptions.Multiline)] private static partial Regex HumansRx();

    /// <summary>Humans on the server. The connect/disconnect stream answers it for free; when that set is empty - the bridge may have started
    /// mid-map and missed the joins - one RCON status settles it, and its "players : N humans" line is well inside the first response packet.</summary>
    static int Humans() => Players.Values.Count(p => p.GoneSince == null && p.ReloadSince == null) is var n && n > 0 ? n
        : Send("status") is { } reply && HumansRx().Match(reply) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;
}
