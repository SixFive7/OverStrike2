// The match-end screen cut short. A match-end vote that keeps the map - Play this map again - starts the next match after the countdown a vote's map change has
// (the bridge's ChangeDelay, 8 s), not when the screen runs out: every mode's cfg sets mp_match_restart_delay 90, so that the screen lasts through the vote. As the
// vote decides, the bridge asks the plugin (Command, plugin/OverStrike2/HudScreenNextMatch.cs).
//
// The game ends the screen at its start - the game rules' m_flIntermissionStartTime - plus mp_match_restart_delay: on 222dc9e's rehearsal (2026-11-12) the
// screen of a match that ended in Arms Race ended 90 s after it came up, while m_flIntermissionEndTime had passed about 10 s into it and ended nothing. So the
// plugin sets mp_match_restart_delay to the time the screen has been up plus the countdown (NewDelay), and puts the value it had back once the next match's round
// has started, or CheckSeconds after the new end at the latest - never across a map change, whose cfg sets its own. There the game starts the next match on the
// same map, as at any end of the screen (mp_match_end_restart 1): no map load, so nobody goes through a load and no fairness restart follows. Only while the
// screen is up (GAMEPHASE_MATCH_ENDED), and only ever sooner - but for the hold: a vote won by a map still to download holds the screen HoldSeconds from the
// decision, sooner or later than it would end, so the next match never starts on the old map while the server fetches the new one. CheckSeconds after the
// new end the plugin looks whether the next match did start, and logs it. The reply says when the screen ends then, to a hundredth of a second (EndsIn): the
// bridge's countdown ends there, with the game's.
//
// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
public static class NextMatch
{
    /// <summary>The plugin's server command: os_nextmatch &lt;seconds&gt; [start|hold], from the server console and RCON only. Every reply also names the game rules'
    /// clocks, for the log. With start the screen's start moves instead (m_flIntermissionStartTime to now + seconds - the delay), and the convar stays: a form
    /// for the live lab to try. With hold the screen ends the seconds from now even when that is later than it would (HoldLine). The bridge sends the bare
    /// one and the hold.</summary>
    public const string Command = "os_nextmatch";

    /// <summary>The hold's word, and how long a vote won by a map still to download holds the match-end screen from its decision: the map changes the moment it
    /// is in, and a download that takes longer lets the screen end, and the next match start on the old map until then.</summary>
    public const string HoldWord = "hold";
    public const int HoldSeconds = 120;

    /// <summary>The convar the game ends the screen by, counted from its start.</summary>
    public const string Delay = "mp_match_restart_delay";

    /// <summary>The seconds it takes: 1 up to two minutes - the vote's limit, a change's countdown and the margin fit (the bridge's ScreenToVote).</summary>
    public const int LeastSeconds = 1, MostSeconds = 120;

    /// <summary>How long after the new end the plugin looks whether the next match started, and puts the delay back if the next match's round start has not.</summary>
    public const int CheckSeconds = 2;

    /// <summary>What the command did. Set: the screen ends, and the next match starts, the seconds asked from now. Sooner: the screen ends by then already -
    /// nothing changed. NotEnded: no match-end screen is up (a match runs, a map loads) - nothing changed. Refused: no number of seconds it takes, no game rules,
    /// no delay to read, or no start of the screen known.</summary>
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

    /// <summary>The hold: the screen ends the seconds from now, sooner or later than it would.</summary>
    public static string HoldLine(int seconds) => $"{Command} {seconds} {HoldWord}";

    /// <summary>When a Set reply says the screen ends, in seconds from the reply: "ends in 8.42 s" - its whole-second convar ends it up to a second after the
    /// seconds asked. A plugin before 5.16.5 says the seconds asked ("ends in 8 s"); null for a reply that names none.</summary>
    public static double? EndsIn(string? reply) =>
        reply != null && System.Text.RegularExpressions.Regex.Match(reply, @"ends in (\d+(?:\.\d+)?) s\b") is { Success: true } m
            && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : null;

    /// <summary>The delay that ends a screen up since start (game time, not later than now) the seconds asked from now: the time it has been up plus those seconds -
    /// rounded up to whole seconds for a convar that takes no fraction (whole), so it ends up to a second later, never sooner. Null when its delay (delay) ends
    /// it by then already - unless it holds the screen (hold), which may end it later than its delay would.</summary>
    public static double? NewDelay(double now, double start, double delay, int seconds, bool whole, bool hold = false)
    {
        if (!hold && start + delay <= now + seconds) return null;
        var up = now - start + seconds;
        return whole ? Math.Ceiling(up - 0.0005) : Math.Round(up, 2, MidpointRounding.AwayFromZero);
    }
}
