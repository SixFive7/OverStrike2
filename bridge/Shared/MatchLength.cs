// The match length on the game's side. Arms Race and Deathmatch are each one long round, so a match lasts as long as that round's time. The bridge's !time (Program.SetMatchLength) changes it for
// the match that is on as well as for the ones to come: it sets mp_roundtime, which every round start reads, and asks the plugin to set the round that is on (Command,
// HudScreenMatch.cs): CCSGameRules.RoundTime, counted from the round's start, so the time already played counts. A length the match is already past, or has less than
// LeastSeconds of, ends the match LeastSeconds from now, not at once. The next round start - a new match, Restart match, a new map - reads mp_roundtime again, so nothing here
// outlives the round.
//
// Each of the two keeps its own length (release 197), and both start at 60 minutes, the engine's ceiling: Deathmatch's matches had Valve's 10, which was too short. Each mode's
// cfg sets 60 at every load (server/gamemode_*_server.cfg), after which the bridge puts a chosen length back.
//
// Wingman plays a match of rounds (release 199), where a round's time of 10 minutes would only make one round long: its length is how many rounds, 8 or 16 - Valve's 16, first
// to 9, to begin with - which is mp_maxrounds. Its sides switch at half of them (mp_halftime), so a new count takes the match that is on only while neither count's halftime
// has come (ThisMatch): fewer rounds played than half the shorter of the two. Otherwise it starts with the next match, and the plugin sets nothing: the bridge sets
// mp_maxrounds when a match ends (a kept map's next match starts without a load), before its own Restart match, and 2 s after every Wingman load, whose cfg sets 16.
//
// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
public static class MatchLength
{
    /// <summary>The plugin's server command: os_matchlength &lt;minutes&gt;, from the server console and RCON only.</summary>
    public const string Command = "os_matchlength";

    /// <summary>A match the new length leaves less than this goes on for this long.</summary>
    public const int LeastSeconds = 60;

    /// <summary>The modes whose match is one round, so that its length is the round's time: Arms Race and Deathmatch.</summary>
    public static bool OneRound(string modeId) => modeId is Modes.ArmsRaceId or Modes.DeathmatchId;

    /// <summary>The mode whose match is a number of rounds, which is its length: Wingman.</summary>
    public static bool InRounds(string modeId) => modeId == Modes.WingmanId;

    /// <summary>Arms Race's and Deathmatch's two lengths in minutes, and Wingman's in rounds: 8 (first to 5, the sides switching after 4) or 16 (Valve's: first to 9, after 8).</summary>
    public const int ShortMinutes = 10, LongMinutes = 60, ShortRounds = 8, LongRounds = 16;

    /// <summary>A mode's two lengths, the short one and the long one, which is the mode's own: what it starts with, and its cfg sets at every load.</summary>
    public static (int Short, int Long) Choices(string modeId) => InRounds(modeId) ? (ShortRounds, LongRounds) : (ShortMinutes, LongMinutes);

    /// <summary>A mode's own length, which its cfg sets at every load: 60 minutes in Arms Race and in Deathmatch (Valve's Deathmatch has 10), 16 rounds in Wingman
    /// (server/gamemode_*_server.cfg).</summary>
    public static int Default(string modeId) => Choices(modeId).Long;

    /// <summary>A length as its mode counts it: "10 minutes", "16 rounds".</summary>
    public static string Words(string modeId, int length) => $"{length} {(InRounds(modeId) ? "rounds" : "minutes")}";

    /// <summary>Whether a new count of rounds takes the match that is on, its sides switching at half the count: only while neither count's halftime has come - fewer
    /// rounds played than half the shorter of the two. A count of 0 (no limit) has no halftime.</summary>
    public static bool ThisMatch(int played, int current, int wanted) => played < (current > 0 ? Math.Min(current, wanted) : wanted) / 2;

    /// <summary>What the command did. Set: the match that is on lasts the new length. EndsSoon: it is past that, or nearly, and ends LeastSeconds from now. NoMatch: no match is on
    /// (warmup, the match-end screen, a map still loading), so the new length starts the next one. NotHere: nothing was set (a plugin before release 199 in Wingman). Later: a
    /// Wingman match past the halftime the new count would move - nothing set, and the next match takes it.</summary>
    public enum Result { Set, EndsSoon, NoMatch, NotHere, Later }

    /// <summary>The round's time for a new length, whole seconds from the round's start, with this much of the round played.</summary>
    public static int RoundSeconds(int minutes, double playedSeconds) => Math.Max(minutes * 60, (int)Math.Ceiling(Math.Max(0, playedSeconds)) + LeastSeconds);

    /// <summary>Which of Set and EndsSoon that round's time is.</summary>
    public static Result Of(int minutes, double playedSeconds) => RoundSeconds(minutes, playedSeconds) > minutes * 60 ? Result.EndsSoon : Result.Set;

    static string Word(Result result) => result switch { Result.Set => "set", Result.EndsSoon => "ends soon", Result.NoMatch => "no match on", Result.Later => "the next match", _ => "not this mode" };

    /// <summary>The command's reply: the first words say what it did (Read), the rest is for the bridge's log.</summary>
    public static string Reply(Result result, string details) => $"{Command}: {Word(result)} - {details}";

    /// <summary>What a reply says it did; null when RCON failed, or the reply is not the command's (a plugin without it).</summary>
    public static Result? Read(string? reply)
    {
        if (reply == null) return null;
        foreach (var result in new[] { Result.Set, Result.EndsSoon, Result.NoMatch, Result.NotHere, Result.Later })
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
