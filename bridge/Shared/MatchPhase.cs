// The game's phase, as the plugin answers it over RCON (Command, plugin/OverStrike2/HudScreenNextMatch.cs). The bridge asks while a match's end is fresh: a
// Restart match or a fairness restart is refused while the match-end screen is up - the game ignores a restart then - and taken the moment the next match is
// on, which may come 8 s after the vote decided (NextMatch.cs) or after the screen's full 90 s. Before this the bridge went by the clock alone: the 30 s after
// a match end.
//
// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
public static class MatchPhase
{
    /// <summary>The plugin's server command: os_phase, from the server console and RCON only.</summary>
    public const string Command = "os_phase";

    /// <summary>The game rules' phase while the match-end screen is up: GAMEPHASE_MATCH_ENDED.</summary>
    public const int Ended = 5;

    /// <summary>The command's reply: the phase, and what it means for the bridge.</summary>
    public static string Reply(int phase) => $"{Command}: {phase} - {(phase == Ended ? "the match-end screen is up" : "no match-end screen")}";

    /// <summary>The reply with no game rules to read (a map still loading): no phase - the bridge goes by the clock.</summary>
    public static string NoRules => $"{Command}: none - no game rules";

    /// <summary>The phase a reply names; null when RCON failed, the plugin has no such command ("Unknown command"), or it could read no phase.</summary>
    public static int? Read(string? reply)
    {
        if (reply == null) return null;
        var at = reply.IndexOf($"{Command}: ", StringComparison.Ordinal);
        if (at < 0) return null;
        var rest = reply[(at + Command.Length + 2)..];
        var end = rest.IndexOf(' ');
        return int.TryParse(end < 0 ? rest : rest[..end], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var phase) ? phase : null;
    }
}
