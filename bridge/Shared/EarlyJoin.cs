// The fairness restart: no mode has a warmup, so a match is on the moment the first player is in, and whoever loads the map more slowly, or joins later,
// comes into a match that is already on - at level 0 in Arms Race, a round in, or a minute of the match's time gone. So every human who joins a match in
// its first minute starts it over, in every mode, as often as players join: up to 9 restarts in a 10-player match, known and desired.
//
//   - the match runs (Running): from its start - a round start out of warmup with no round played yet: the map's first, a Restart match, a fairness
//     restart, the game commencing (MatchStarted) - when a human is on a team then; with none, from the first human's spawn;
//   - a join: a human's first spawn on a team since they connected or the map loaded. A respawn, the spawn of a restart and a team switch are none; the
//     reconnect of a map change or of the HUD addon's download is no new connect. Never a bot;
//   - the ask (Spawned answers true): a join while another human is on a team and the match has run less than WindowSeconds. The first human in starts
//     the match and asks nothing;
//   - what follows is the bridge's (Restarts.cs, FairRestart): CountdownSeconds on the countdown banner ("The match restarts for fairness - Bob is in"),
//     then the plugin's os_restart. A join while that counts down, or as it goes out, folds into it: one restart, and the banner names them all. The
//     restart's round start is a new start, so the window opens again. None while a vote is on, a map change counts down or loads, the server's restart
//     counts down, or the match has just ended.
//
// Shared like Hud.cs: the plugin feeds it the game (plugin/OverStrike2/EarlyJoinWatch.cs) and the bridge's --selftest drives it on a handed clock. Plain BCL only.
public sealed class EarlyJoin
{
    /// <summary>The plugin's word to the bridge on /act when the plan asks: never a chat command (!fair is How it works' alias), never a click.</summary>
    public const string Command = "!latejoin";

    /// <summary>A join this soon after the match started running (or last restarted) restarts it.</summary>
    public const int WindowSeconds = 60;

    /// <summary>The countdown banner's seconds before the fairness restart goes out.</summary>
    public const int CountdownSeconds = 5;

    /// <summary>The most names the banner and the line give; the rest are "N more".</summary>
    public const int NamesShown = 3;

    /// <summary>The line for everybody in chat and on the board as the countdown starts, or as another join folds into it with seconds left.</summary>
    public static string Line(string names, int count, int seconds = CountdownSeconds) =>
        $"The match restarts for fairness in {M.V(MapVote.Left(seconds))} - {M.N(names)} {(count > 1 ? "are" : "is")} in";

    /// <summary>The countdown banner's title while it counts down (HudView.CountdownOf).</summary>
    public static string BannerTitle(string names, int count = 1) => $"The match restarts for fairness - {names} {(count > 1 ? "are" : "is")} in";

    /// <summary>Who came in, as the banner and the line say it: "Bob", "Bob and Cas", "Bob, Cas and Dee", "Bob, Cas, Dee and 2 more".</summary>
    public static string Names(IReadOnlyList<string> names)
    {
        if (names.Count <= 1) return names.Count == 1 ? names[0] : "";
        if (names.Count > NamesShown) return $"{string.Join(", ", names.Take(NamesShown))} and {names.Count - NamesShown} more";
        return $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
    }

    readonly HashSet<long> joined = new();   // the humans who have spawned on a team since they connected or the map loaded
    DateTime? running;                       // the match runs since then: its start with a human on a team, or the first human's spawn; null: nobody yet
    bool forced;                             // os_fair live: the next join counts as joining somebody
    string? skipped;                         // why this map has no fairness restart at all (the plugin loaded mid-map)

    /// <summary>A new map: nobody has joined it, and its match runs once somebody is in.</summary>
    public void MapStarted()
    {
        joined.Clear();
        (running, forced, skipped) = (null, false, null);
    }

    /// <summary>Nothing on this map: it began before the plugin saw it (a reload), so nobody can tell who has joined.</summary>
    public void Skip(string why) => skipped = why;

    /// <summary>A human leaves for real (not the reconnect of a map change, nor the addon download's): when they come back, their first spawn is a join again.</summary>
    public void Left(long account) => joined.Remove(account);

    /// <summary>A round start out of warmup with no round played yet - the match starts, or starts over (a restart, the fairness one's too) - with this many
    /// humans on a team. With any, the match runs from now; with none, from the first human's spawn - unless os_fair live's join is still to come: the game
    /// commencing as a lone test game picks a team must not undo it.</summary>
    public void MatchStarted(DateTime now, int humansOnTeam) => running = humansOnTeam > 0 || forced ? now : null;

    /// <summary>os_fair live, the client harness's: the match runs from now, and the next join counts as joining somebody - so a lone test game's spawn asks.</summary>
    public void Force(DateTime now) => (running, forced) = (now, true);

    /// <summary>A human spawns on a team; others: the other humans on a team now. True when it asks for the restart: their first spawn since they connected or
    /// the map loaded, somebody else plays, and the match has run less than WindowSeconds. A first spawn with nobody else playing starts the match running.</summary>
    public bool Spawned(long account, DateTime now, int others)
    {
        if (skipped != null || !joined.Add(account)) return false;
        if (forced) (forced, others) = (false, Math.Max(others, 1));
        if (others == 0 || running == null)
        {
            running = now;
            return false;
        }
        return (now - running.Value).TotalSeconds < WindowSeconds;
    }

    /// <summary>The seconds the match has run, or null while nobody plays it.</summary>
    public double? Running(DateTime now) => running is { } at ? (now - at).TotalSeconds : null;

    /// <summary>For the console: where this map stands.</summary>
    public string Describe(DateTime now) =>
        skipped != null ? $"nothing on this map: {skipped}"
        : Running(now) is not { } s ? $"nobody plays the match yet; {joined.Count} human(s) joined this map"
        : s < WindowSeconds ? $"the match has run {s:0} s: a join in the next {WindowSeconds - s:0} s restarts it; {joined.Count} human(s) joined this map"
        : $"the match has run {s:0} s, past the first {WindowSeconds} s: a join plays on; {joined.Count} human(s) joined this map";
}
