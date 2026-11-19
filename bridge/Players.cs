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
        public DateTime? Returning;   // on the way back through a reconnect (a map change's, the HUD addon's) since its departure, "entered the game" still to
                                      // come: a sign-on whatever the server's list says meanwhile (EndDroppedSignOns)
        public DateTime? MissingSince; // signing on, and the server's list has lacked them since then, read after read
        public bool OffList;          // on the way back and off the server's list: said once
    }

    static readonly Dictionary<string, Player> Players = new();

    static Player PlayerOf(string id) => Players.TryGetValue(id, out var p) ? p : Players[id] = new Player();

    /// <summary>A "connected" or "entered the game" line. Returns true when this is a real join, as opposed to the reconnect every map change causes.</summary>
    static bool Arrive(LogEvent e, DateTime now)
    {
        var known = Players.TryGetValue(e.SteamId, out var p);
        p ??= Players[e.SteamId] = new Player();
        var real = !known || p.GoneSince != null || (p.ReloadSince is { } r && (now - r).TotalSeconds > ReloadGraceSeconds);
        p.Name = e.Name; p.GoneSince = null; p.ReloadSince = null; p.MissingSince = null; p.OffList = false;
        if (e.Kind == Kind.Connected) { p.Joining |= real; p.SigningOn = now; SignOnsChanged(); return false; } // a join survives the reconnects in between: a map change, MultiAddonManager's addon download
        var fresh = p.Joining || real;
        p.Joining = false;
        if (p.SigningOn != null || p.Returning != null) { p.SigningOn = null; p.Returning = null; SignOnsChanged(); }
        return fresh;
    }

    /// <summary>A "disconnected" line. A reconnect is no departure (EarlyJoin.Reconnects, the plugin's rule too: a reason with SHUTDOWN in it) -
    /// NETWORK_DISCONNECT_SHUTDOWN at a map change, NETWORK_DISCONNECT_LOOPSHUTDOWN when MultiAddonManager makes a joining client reconnect to fetch the HUD
    /// addon: a join under way stays under way through those, and the player is on the way back (Returning) until "entered the game" - a sign-on. Every other
    /// reason is a departure, NETWORK_DISCONNECT_LOOPDEACTIVATE too.</summary>
    static void Depart(string id, string reason, DateTime now)
    {
        var p = PlayerOf(id);
        p.MissingSince = null;
        if (EarlyJoin.Reconnects(reason)) // on the way back: a return that still counts keeps its start, one past its bound starts anew
        {
            if (p.GoneSince == null) { p.ReloadSince ??= now; p.Returning = p.Returning is { } back && (now - back).TotalSeconds <= EarlyJoin.SignOnMostSeconds ? back : now; }
        }
        else // left for real: the HUD check forgets them, and it is no sign-on
        {
            p.GoneSince = now; p.ReloadSince = null; p.Joining = false; HudSeen.Remove(id);
            if (p.SigningOn != null || p.Returning != null) { p.SigningOn = null; p.Returning = null; SignOnsChanged(); }
        }
    }

    /// <summary>A chat line or a click proves its sender is on the server - under the name the server shows for them, which the history keeps (History.cs).</summary>
    static void Present(string id, string name)
    {
        var p = PlayerOf(id);
        p.Name = name; p.GoneSince = null; p.ReloadSince = null;
        MatchHistory.Shown(AccountId(id), name);
    }

    /// <summary>A map start or a server restart: everyone in game is now reloading. The log usually said so already; this is the safety net.</summary>
    static void AllReloading(DateTime now)
    {
        foreach (var p in Players.Values) if (p.GoneSince == null) p.ReloadSince ??= now;
    }

    /// <summary>The humans whose sign-on is not complete by the log stream: "connected" and not yet "entered the game" - at CS2's prompt to confirm a first
    /// Workshop download, downloading the map, loading it - and everybody on the way back through a reconnect (a map change's, the HUD addon's), from its
    /// departure to "entered the game", connected again or not yet: a game that downloads the map itself after the prompt, or crashes, is off the server
    /// meanwhile and writes no line (seen live on 2026-11-18: 47 s off it, on a 1.7 GB map). For the prompt's joiner the "connected" line is all the game writes: the
    /// game hears of a client only once it has every addon (no controller, nothing in os_joining - the live lab's runs AW and AX), and CS2's drop of it writes no
    /// "disconnected". So each counts EarlyJoin.SignOnMostSeconds at the most from its "connected" or its departure, and the server's own list ends a new
    /// joiner's sooner where it is asked (EndDroppedSignOns) - never one on the way back.</summary>
    static List<string> SigningOnNames(DateTime now) => [.. Players.Where(kv => kv.Value.GoneSince == null && SignOnSince(kv.Value, now) != null)
        .OrderBy(kv => SignOnSince(kv.Value, now)).Select(kv => kv.Value.Name is { Length: > 0 } n ? n : kv.Key)];

    /// <summary>Since when this player's sign-on counts now: the earlier of their "connected" and their reconnect's departure that is within
    /// EarlyJoin.SignOnMostSeconds; null when neither is.</summary>
    static DateTime? SignOnSince(Player p, DateTime now) => new[] { p.SigningOn, p.Returning }.Where(t => t is { } at && (now - at).TotalSeconds <= EarlyJoin.SignOnMostSeconds).Min();

    /// <summary>The sign-ons the server's own client list (status) no longer has: gone before they were in - CS2's drop of a first-download prompt nobody answered
    /// among them, which writes no "disconnected" line. Not one whose "connected" came in the last 3 s (the list and the log stream may cross), nor one mid-reconnect
    /// (a SHUTDOWN departure in the last 10 s), nor one the list has lacked for less than EarlyJoin.DropConfirmSeconds, read after read: the log stream can say a
    /// reconnect's departure a moment after the list has lost them - the HUD addon's, whose next "connected" is a second or two away (seen live on 2026-11-18).
    /// And never one on the way back through a reconnect (Returning): off the list mid-way - their own download of the map, or a crash - they stay a
    /// sign-on to its bound, which is said once.</summary>
    static void EndDroppedSignOns(IReadOnlyList<EarlyJoin.StatusClient> clients, DateTime now)
    {
        var listed = clients.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var ended = false;
        foreach (var p in Players.Values)
        {
            if (p is not { GoneSince: null, SigningOn: { } at }) continue; // only who connected: one not connected again yet is off the list anyway
            if (listed.Contains(p.Name)) { p.MissingSince = null; continue; }
            if ((now - at).TotalSeconds < 3 || p.ReloadSince is { } r && (now - r).TotalSeconds <= 10) continue;
            p.MissingSince ??= now;
            if ((now - p.MissingSince.Value).TotalSeconds < EarlyJoin.DropConfirmSeconds) continue;
            if (p.Returning != null)
            {
                if (!p.OffList) Log($"joining: {p.Name} is on the way back through a reconnect, and the server lists them no more - their own download of the map, or a crash, which CS2 writes no line for: still joining until they are in, {EarlyJoin.SignOnMostSeconds} s from their departure at the most");
                p.OffList = true;
                continue;
            }
            p.SigningOn = null; p.MissingSince = null; p.GoneSince = now; p.Joining = false; ended = true; // gone: no longer on the server for anything (PresentIds) - the live lab on 997c7bb kept such a connect present, and its warm-up asked for an end
            Log($"joining: {p.Name} was connecting, and the server lists them no more - gone before they were in, which CS2 writes no \"disconnected\" line for");
        }
        if (ended) SignOnsChanged();
    }

    /// <summary>What the plugin's os_warmup last heard from the bridge (EarlyJoin.BridgeViewCommand) - whom its log stream has signing on and status's joiners -
    /// and whether a word to it is due: one a second at the most, and only when either changed, so a join's "connected" and "entered the game" a moment apart
    /// say nothing.</summary>
    static List<string> SignOnsTold = [];
    static List<string>? StatusTold;
    static bool SignOnsTell;

    /// <summary>The server's own list (status), read every StatusWatchSeconds while anyone signs on by the log stream or its last reading had anyone joining, and
    /// each second of a warm-up (JoiningNow): the humans not yet active at the last reading (null: none read since the bridge started, or no players
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

    /// <summary>status, read now: its humans not yet active (StatusJoining, told to os_warmup when they change), and the log stream's sign-ons it no longer has
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
        EndSignOnsPastBound(now);
        var names = SigningOnNames(now);
        if ((!names.SequenceEqual(SignOnsTold) || !SameNames(StatusJoining, StatusTold)) && Send(EarlyJoin.BridgeViewCommand(names, StatusJoining)) != null)
            (SignOnsTold, StatusTold) = (names, StatusJoining); // no answer: the next change tells it
        if (names.Count > 0) WatchStatus();
        var open = Players.Values.Where(p => p.GoneSince == null).Select(p => SignOnSince(p, now)).OfType<DateTime>().ToList();
        if (open.Count > 0) After((int)Math.Max(1000, (open.Min().AddSeconds(EarlyJoin.SignOnMostSeconds + 1) - now).TotalMilliseconds), SignOnsChanged); // the bound ends one with no line
    }

    /// <summary>A new joiner whose sign-on ran past its bound (EarlyJoin.SignOnMostSeconds after their "connected") without "entered the game", and whom the
    /// server's list did not end first (EndDroppedSignOns: not read meanwhile - a short status, RCON down): gone before they were in, as a dropped one is - no
    /// longer present for anything (PresentIds: a vote's present, the empty server's reset). Never one on the way back through a reconnect (Returning): a map
    /// change's load of a big map may take longer, and its departure was none. The second ghost the lab round on 997c7bb left: a dead connect counted as present
    /// for good when nothing read the list.</summary>
    static void EndSignOnsPastBound(DateTime now)
    {
        foreach (var p in Players.Values)
        {
            if (p is not { GoneSince: null, SigningOn: { } at, Returning: null } || (now - at).TotalSeconds <= EarlyJoin.SignOnMostSeconds) continue;
            p.SigningOn = null; p.MissingSince = null; p.GoneSince = now; p.Joining = false;
            Log($"joining: {p.Name} connected {(now - at).TotalSeconds:0} s ago and never got in - gone before they were in, past the sign-on's {EarlyJoin.SignOnMostSeconds} s");
        }
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
