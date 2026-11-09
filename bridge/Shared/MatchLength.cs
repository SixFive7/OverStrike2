// The match length on the game's side. Arms Race and Deathmatch are each one long round, so a match lasts as long as that round's time. The bridge's !time (Program.SetMatchLength) changes it for
// the match that is on as well as for the ones to come: it sets mp_roundtime, which every round start reads, and asks the plugin to set the round that is on (Command,
// HudScreenMatch.cs): CCSGameRules.RoundTime, counted from the round's start, so the time already played counts. A length the match is already past, or has less than
// LeastSeconds of, ends the match LeastSeconds from now, not at once. The next round start - a new match, Restart match, a new map - reads mp_roundtime again, so nothing here
// outlives the round.
//
// Each of the two keeps its own length (release 197), and both start at 60 minutes, the engine's ceiling: Deathmatch's matches had Valve's 10, which was too short. Each mode's
// cfg sets 60 at every load (server/gamemode_*_server.cfg), after which the bridge puts a chosen length back. A Wingman match is a whole match of rounds, where a round's time of
// 10 minutes would only make one round long: the bridge refuses !time there, and the plugin's command sets nothing.
//
// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
public static class MatchLength
{
    /// <summary>The plugin's server command: os_matchlength &lt;minutes&gt;, from the server console and RCON only.</summary>
    public const string Command = "os_matchlength";

    /// <summary>A match the new length leaves less than this goes on for this long.</summary>
    public const int LeastSeconds = 60;

    /// <summary>The modes whose match is one round, and so has a length: Arms Race and Deathmatch.</summary>
    public static bool Applies(string modeId) => modeId is Modes.ArmsRaceId or Modes.DeathmatchId;

    /// <summary>A mode's own length, which its cfg sets at every load: 60 minutes in Arms Race and in Deathmatch (server/gamemode_armsrace_server.cfg,
    /// server/gamemode_deathmatch_server.cfg - Valve's Deathmatch has 10).</summary>
    public static int DefaultMinutes(string modeId) => 60;

    /// <summary>What the command did. Set: the match that is on lasts the new length. EndsSoon: it is past that, or nearly, and ends LeastSeconds from now. NoMatch: no match is on
    /// (warmup, the match-end screen, a map still loading), so mp_roundtime starts the next one. NotHere: the map that is on plays a match of rounds (Wingman), and nothing was set.</summary>
    public enum Result { Set, EndsSoon, NoMatch, NotHere }

    /// <summary>The round's time for a new length, whole seconds from the round's start, with this much of the round played.</summary>
    public static int RoundSeconds(int minutes, double playedSeconds) => Math.Max(minutes * 60, (int)Math.Ceiling(Math.Max(0, playedSeconds)) + LeastSeconds);

    /// <summary>Which of Set and EndsSoon that round's time is.</summary>
    public static Result Of(int minutes, double playedSeconds) => RoundSeconds(minutes, playedSeconds) > minutes * 60 ? Result.EndsSoon : Result.Set;

    static string Word(Result result) => result switch { Result.Set => "set", Result.EndsSoon => "ends soon", Result.NoMatch => "no match on", _ => "not this mode" };

    /// <summary>The command's reply: the first words say what it did (Read), the rest is for the bridge's log.</summary>
    public static string Reply(Result result, string details) => $"{Command}: {Word(result)} - {details}";

    /// <summary>What a reply says it did; null when RCON failed, or the reply is not the command's (a plugin without it).</summary>
    public static Result? Read(string? reply)
    {
        if (reply == null) return null;
        foreach (var result in new[] { Result.Set, Result.EndsSoon, Result.NoMatch, Result.NotHere })
            if (reply.Contains($"{Command}: {Word(result)}", StringComparison.Ordinal)) return result;
        return null;
    }

    /// <summary>What the line for everybody says the change does to the match that is on: the section after the new length. Without the plugin's word it is only sure from the next match.</summary>
    public static string Effect(Result? result) => result switch
    {
        Result.Set => "This match too",
        Result.EndsSoon => $"This match ends in {M.V($"{LeastSeconds} s")}",
        Result.NoMatch => "From this match on",
        _ => "From the next match",
    };
}
