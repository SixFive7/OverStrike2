// The fairness restart (the owner's request of 2026-11-09): in Arms Race and Deathmatch a match goes live the moment the first player picks a team
// (their cfgs switch the warmup off), so whoever loads the map more slowly comes into a match that is already on - at level 0 in Arms Race, as CS2
// starts a late joiner. A player who comes in during the match's first minute therefore starts it over, once per map: the restart waits until the
// last player of the map before has spawned too, at most until the minute is up, and the bridge counts it down on the countdown banner first.
//
//   - the match goes live: the map's first round start out of warmup with a human on a team (Live). Those on a team then started it;
//   - a late join: a human's first spawn on the map after that, within WindowSeconds, who was not on a team when it went live - a player of the map
//     before who took longer to load, or a new joiner. A bot never is one;
//   - the ask (Due): once there is a late join, as soon as every human of the map before has spawned on this one, or when the window is over -
//     whichever comes first - and once per map. Nobody late, nothing asked. A human who leaves for real is no longer waited for; one who never comes
//     back is waited for until the window is over;
//   - what follows is the bridge's (Restarts.cs, FairRestart): the Restart match's path and its refusals - no restart while a vote is on, a map
//     change counts down or loads, the server's restart counts down, the match has just ended or was restarted in the last minute - a countdown of
//     CountdownSeconds on the countdown banner ("The match restarts for fairness - Bob is in"), then the plugin's os_restart.
//
// Shared like Hud.cs: the plugin feeds it the game (plugin/OverStrike2/EarlyJoin.cs) and the bridge's --selftest drives it on a handed clock. Plain BCL only.
public sealed class EarlyJoin
{
    /// <summary>The plugin's word to the bridge on /act when the plan asks: never a chat command (!fair is How it works' alias), never a click.</summary>
    public const string Command = "!latejoin";

    /// <summary>A first spawn this soon after the match went live is a late join.</summary>
    public const int WindowSeconds = 60;

    /// <summary>The countdown banner's seconds before the fairness restart goes out.</summary>
    public const int CountdownSeconds = 5;

    /// <summary>The modes whose matches it restarts: Arms Race and Deathmatch, one long round each and no warmup. Wingman keeps its warmup, and a player
    /// who comes in late still plays the round (its cfg's mp_join_grace_time).</summary>
    public static bool Applies(string modeId) => modeId is Modes.ArmsRaceId or Modes.DeathmatchId;

    /// <summary>The line for everybody in chat and on the board as the countdown starts.</summary>
    public static string Line(string name) => $"The match restarts for fairness in {M.V(MapVote.Left(CountdownSeconds))} - {M.N(name)} is in";

    /// <summary>The countdown banner's title while it counts down (HudView.CountdownOf).</summary>
    public static string BannerTitle(string name) => $"The match restarts for fairness - {name} is in";

    readonly HashSet<long> seen = new();     // the humans on the server during this map: the next map waits for them
    HashSet<long> expected = new();          // the humans of the map before, waited for on this one
    readonly HashSet<long> spawned = new();  // the humans who have spawned on this map
    readonly HashSet<long> atStart = new();  // the humans on a team when the match went live
    readonly List<(long Account, string Name)> late = new();
    DateTime liveAt = DateTime.MinValue;
    bool on, asked;
    string? skipped;                         // why this map has no fairness restart at all (the plugin loaded mid-map)

    /// <summary>A new map: the humans of the one before are waited for on it, and everything else is this map's.</summary>
    public void MapStarted()
    {
        expected = new HashSet<long>(seen);
        seen.Clear();
        spawned.Clear();
        atStart.Clear();
        late.Clear();
        (liveAt, on, asked, skipped) = (DateTime.MinValue, false, false, null);
    }

    /// <summary>Nothing on this map: it began before the plugin saw it (a reload), so nobody can tell who started its match.</summary>
    public void Skip(string why) => skipped = why;

    /// <summary>A human leaves for real (not the reconnect of a map change, nor the addon download's): nobody waits for them any more.</summary>
    public void Left(long account)
    {
        expected.Remove(account);
        seen.Remove(account);
    }

    /// <summary>A round start out of warmup, with the humans on a team then. The first with any is the match going live; every other changes nothing
    /// (the fairness restart's own round start, a Restart match, the next match on the map). True when this one was taken.</summary>
    public bool Live(DateTime now, IReadOnlyCollection<long> onTeam, bool modeApplies)
    {
        if (liveAt != DateTime.MinValue || skipped != null || onTeam.Count == 0) return false;
        (liveAt, on) = (now, modeApplies);
        foreach (var a in onTeam.Where(a => a > 0)) // 0: os_fair live's stand-in for nobody, which nobody waits for on the next map
        {
            atStart.Add(a);
            spawned.Add(a);
            seen.Add(a);
        }
        return true;
    }

    /// <summary>A human spawns on a team. Their first spawn on the map, after the match went live and within WindowSeconds of it, by somebody not on a
    /// team then, is a late join. True when it was one.</summary>
    public bool Spawned(long account, string name, DateTime now)
    {
        seen.Add(account);
        if (!spawned.Add(account)) return false;
        if (liveAt == DateTime.MinValue || atStart.Contains(account) || (now - liveAt).TotalSeconds > WindowSeconds) return false;
        late.Add((account, name));
        return true;
    }

    /// <summary>Whether to ask for the restart now - and for whom, the last one in: a late join, and every human of the map before spawned or the window
    /// over. Once per map: what it answers once, it never answers again. Null: not now.</summary>
    public (long Account, string Name)? Due(DateTime now)
    {
        if (asked || !on || skipped != null || liveAt == DateTime.MinValue || late.Count == 0) return null;
        if (!expected.All(spawned.Contains) && (now - liveAt).TotalSeconds < WindowSeconds) return null;
        asked = true;
        return late[^1];
    }

    /// <summary>The humans of the map before who have not spawned on this one yet.</summary>
    public int Waiting => expected.Count(a => !spawned.Contains(a));

    /// <summary>For the console: where this map stands.</summary>
    public string Describe(DateTime now) =>
        skipped != null ? $"nothing on this map: {skipped}"
        : liveAt == DateTime.MinValue ? $"the match is not live yet; {expected.Count} human(s) of the map before expected"
        : !on ? "this mode is not restarted for fairness"
        : asked ? $"asked for {late[^1].Name}, {late.Count} late join(s)"
        : $"live {(now - liveAt).TotalSeconds:0} s ago, {atStart.Count} on a team then; {late.Count} late join(s); {Waiting} of the map before still loading";
}
