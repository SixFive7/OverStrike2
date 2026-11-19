// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.

/// <summary>Which content a connection's game holds, as far as the plugin may go by it: a new connection in a slot starts with none known, and nothing new in
/// a content goes to it until its own HUD check names one (the owner's 287 b, for every content's own panels). The lab round on rel-220's 997c7bb: one client,
/// its game first on v4.15.0 and then, a new game, on v4.14.0 in the same slot, was handed HISTORY's cards at its first full update, 18 s before its check -
/// the account's last game's content stood for the new one, and the slot's page was its last occupant's.
///
/// The one connection that is no new game is a game coming back through a map change: CS2 reconnects every game when the map changes, the same game with the
/// same content, and what was known of it stays known (the bridge's word, HudCheck.VersionOf). Every other connection - a new game, a reconnect in the middle
/// of a map, the account's own included - counts as holding no known content until its own click. From the connection's start (OnClientConnect, before its
/// first full update) to its put-in, where its account is known, nothing is known of any slot's game.</summary>
public sealed class HudConnections
{
    /// <summary>How long after a map's start a game that left at that map's change still counts as coming back: its load, the slowest's included.</summary>
    public const int MapChangeSeconds = 180;

    readonly HashSet<int> starting = new();                   // slots whose connection began and is not in yet: nothing known of their game
    readonly HashSet<long> leftAtChange = new();              // accounts whose game left at the last map change: they come back as the same game
    DateTime mapStart = DateTime.MinValue;

    /// <summary>A connection begins in this slot (OnClientConnect): no content known for it until its put-in says whose game it is.</summary>
    public void Connect(int slot) => starting.Add(slot);

    /// <summary>The map ends: the games that leave now leave for the map change (LeftAtMapChange), not the ones of the change before.</summary>
    public void MapEnded() => leftAtChange.Clear();

    /// <summary>A game left at a map change (a disconnect between the map's end and the next map's start).</summary>
    public void LeftAtMapChange(long account)
    {
        if (account > 0) leftAtChange.Add(account);
    }

    /// <summary>The next map started: the games that left at its change come back within MapChangeSeconds.</summary>
    public void MapStarted(DateTime now) => mapStart = now;

    /// <summary>The connection is in (OnClientPutInServer), its account known. True when it is a game coming back through the map change - what was known of
    /// its content stays known; false for every other connection: a new game, whose account's last content no longer counts (the caller forgets it until
    /// this game's own click).</summary>
    public bool PutIn(int slot, long account, DateTime now)
    {
        starting.Remove(slot);
        return leftAtChange.Remove(account) && (now - mapStart).TotalSeconds is >= 0 and <= MapChangeSeconds;
    }

    /// <summary>The slot's player left: nothing of its connection is kept.</summary>
    public void Gone(int slot) => starting.Remove(slot);

    /// <summary>The content the frame may take this slot's game to hold: none while its connection has not come in yet, else what is known of its account.</summary>
    public string VersionAt(int slot, string known) => starting.Contains(slot) ? "" : known;

    /// <summary>Whether this slot's connection began and is not in yet.</summary>
    public bool Starting(int slot) => starting.Contains(slot);
}
