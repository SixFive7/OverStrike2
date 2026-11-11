// The match-end screen cut short. A match-end vote that keeps the map - Play this map again - starts the next match after the countdown a vote's map change has
// (the bridge's ChangeDelay, 8 s), not when the screen's mp_match_restart_delay runs out: every mode's cfg sets 90 s there, so that the screen lasts through
// the vote. As the vote decides, the bridge asks the plugin (Command, plugin/OverStrike2/HudScreenNextMatch.cs), which moves the game's own end of the screen -
// the game rules' m_flIntermissionEndTime, set when the screen came up - to that moment. There the game starts the next match on the same map as it always does at
// the screen's end (mp_match_end_restart 1): no map load, so nobody goes through a load and no fairness restart follows. Only while the screen is up
// (GAMEPHASE_MATCH_ENDED), and only ever sooner. CheckSeconds after the new end the plugin looks whether the next match did start, and logs it either way.
//
// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
public static class NextMatch
{
    /// <summary>The plugin's server command: os_nextmatch &lt;seconds&gt; [start], from the server console and RCON only. With start the screen's start time moves back
    /// by as much as its end: for a game that reads the end off the start and mp_match_restart_delay instead - a second try for the live lab, should the end time
    /// alone not take; the bridge sends the bare form.</summary>
    public const string Command = "os_nextmatch";

    /// <summary>The seconds it takes: 1 up to a minute.</summary>
    public const int LeastSeconds = 1, MostSeconds = 60;

    /// <summary>How long after the new end the plugin looks whether the next match started.</summary>
    public const int CheckSeconds = 2;

    /// <summary>What the command did. Set: the screen ends, and the next match starts, the seconds asked from now. Sooner: the screen ends sooner than that by itself -
    /// nothing moved. NotEnded: no match-end screen is up (a match runs, a map loads) - nothing moved. Refused: no number of seconds it takes, or no game rules.</summary>
    public enum Result { Set, Sooner, NotEnded, Refused }

    static string Word(Result result) => result switch { Result.Set => "set", Result.Sooner => "sooner by itself", Result.NotEnded => "no match end", _ => "refused" };

    /// <summary>The command's reply: the first words say what it did (Read), the rest is for the bridge's log.</summary>
    public static string Reply(Result result, string details) => $"{Command}: {Word(result)} - {details}";

    /// <summary>What a reply says it did; null when RCON failed, or the reply is not the command's (a plugin without it: "Unknown command").</summary>
    public static Result? Read(string? reply)
    {
        if (reply == null) return null;
        foreach (var result in new[] { Result.Set, Result.Sooner, Result.NotEnded, Result.Refused })
            if (reply.Contains($"{Command}: {Word(result)} - ", StringComparison.Ordinal)) return result;
        return null;
    }

    /// <summary>The line the bridge sends: the bare form.</summary>
    public static string Line(int seconds) => $"{Command} {seconds}";

    /// <summary>The screen's new end, in game time, for a screen up now whose end is end (0 or less: the game set none): now plus the seconds asked - or null when it
    /// ends by then already.</summary>
    public static float? NewEnd(float now, float end, int seconds) => end > 0 && end <= now + seconds ? null : now + seconds;
}
