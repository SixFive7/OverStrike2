namespace OverStrike2;

/// <summary>Everybody stays on their side when a match-end screen runs out on the same map - after Play this map again, whether the plugin cut the screen short
/// (os_nextmatch) or it ran its 90 s. The game ends that screen with a reset of its own: it moves every human to Unassigned, kicks the bots and resets the match,
/// and each player then sits at the team select until they pick a side again, the first pick setting off a second start (the live lab, 2026-11-12). No convar
/// keeps the sides: sv_disable_teamselect_menu leaves players Unassigned without a menu, mp_force_pick_time assigns nobody, sv_human_autojoin_team and mp_humanteam
/// put everybody on one side, and the game ignores mp_restartgame while the screen is up. So the plugin puts them back itself (TeamKeep.cs):
///   - at a match end, a snapshot: the side of every human on T or CT (MatchEnded; one per screen);
///   - during the screen, a switch to the other side moves their snapshot with them, a switch to spectating drops it (they chose to watch), and a real departure
///     drops it (Left); somebody who joins during the screen has none and keeps the game's own team select;
///   - the first move of a human with a snapshot to Unassigned - no player can pick that - is the game closing the screen (TeamChanged answers true);
///   - the pass, a frame later, once the game has moved everybody: every human with a snapshot who is on Unassigned then goes back to their side, all in one
///     pass, so the start the first of them sets off finds all of them on a side - in an order that never puts a side two ahead, should a team limit count
///     (the larger side first, then turn about). Then the snapshot is gone, and a map start drops it too. Bots are the game's: they come back with the first human.
/// The fairness restart does not see them as joins: no map starts, so the map's record of who joined stays (EarlyJoin). No CounterStrikeSharp type in it: the
/// self-test drives it.</summary>
public sealed class TeamKeepPlan
{
    public const int Unassigned = 0, Spectator = 1, Terrorist = 2, CounterTerrorist = 3;

    readonly Dictionary<ulong, int> sides = new(); // the humans of this screen: SteamID -> T or CT
    bool screen;                                   // a match-end screen is up, its snapshot taken
    bool closing;                                  // ...and the game has begun moving its humans to Unassigned: the pass is due

    /// <summary>A match-end screen is up and its snapshot taken; the pass has not been made.</summary>
    public bool ScreenUp => screen;

    /// <summary>How many humans the pass would put back, as things stand.</summary>
    public int Count => sides.Count;

    /// <summary>A match ends: the side of every human on T or CT now (0: no SteamID - a bot, a player not yet authenticated). One snapshot a screen: the end's
    /// other words (the win panel, the intermission, the round end) change nothing.</summary>
    public void MatchEnded(IEnumerable<(ulong Id, int Team)> humans)
    {
        if (screen) return;
        sides.Clear();
        foreach (var (id, team) in humans)
            if (id != 0 && team is Terrorist or CounterTerrorist) sides[id] = team;
        (screen, closing) = (true, false);
    }

    /// <summary>A human's team changed to team (not by leaving). True only for the first move of a human with a snapshot to Unassigned while the screen is up:
    /// the game is closing it, and the pass is due. A switch to the other side moves their snapshot, spectating drops it; the rest - outside a screen, after its
    /// close, somebody without a snapshot - changes nothing.</summary>
    public bool TeamChanged(ulong id, int team)
    {
        if (!screen || closing || !sides.ContainsKey(id)) return false;
        switch (team)
        {
            case Terrorist or CounterTerrorist: sides[id] = team; return false;
            case Spectator: sides.Remove(id); return false;
            case Unassigned: closing = true; return true;
            default: return false;
        }
    }

    /// <summary>A human left for real: nothing of theirs to put back.</summary>
    public void Left(ulong id) => sides.Remove(id);

    /// <summary>A new map: no screen, nothing to put back - the screen ended in a map change, or a vote's change came during it.</summary>
    public void MapStarted()
    {
        sides.Clear();
        (screen, closing) = (false, false);
    }

    /// <summary>The pass: who goes back to which side - every human with a snapshot whose team is Unassigned now (current: SteamID -> team, the humans on the
    /// server) - in an order that never puts one side two ahead of the other, the larger side first. The screen is over after it, its snapshot gone.</summary>
    public List<(ulong Id, int Team)> Pass(IReadOnlyDictionary<ulong, int> current)
    {
        var due = sides.Where(s => current.TryGetValue(s.Key, out var now) && now == Unassigned).OrderBy(s => s.Key).ToList();
        sides.Clear();
        (screen, closing) = (false, false);
        var t = due.Where(s => s.Value == Terrorist).ToList();
        var ct = due.Where(s => s.Value == CounterTerrorist).ToList();
        var (first, second) = t.Count >= ct.Count ? (t, ct) : (ct, t);
        var order = new List<(ulong, int)>();
        for (var i = 0; i < first.Count; i++)
        {
            order.Add((first[i].Key, first[i].Value));
            if (i < second.Count) order.Add((second[i].Key, second[i].Value));
        }
        return order;
    }
}
