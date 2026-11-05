// !time: the match length, 10 or 60 minutes, for the match that is on too (Arms Race is one long round: mp_roundtime).

static partial class Program
{
    public const int DefaultRoundMinutes = 60, ShortRoundMinutes = 10; // 60 is the engine ceiling for mp_roundtime; Arms Race is one long round, so this is the match length

    static void ApplyMatchLength() => Log($"mp_roundtime {S.RoundMinutes} -> {Send($"mp_roundtime {S.RoundMinutes}") ?? "(rcon failed)"}");

    /// <summary>!time: switch the match length between 10 and 60 minutes, for the match that is on too (MatchLength.cs). Stored in state and re-applied after every map
    /// load, because gamemode_armsrace_server.cfg sets mp_roundtime back to the default each time the mode is created. Arms Race's alone: a Deathmatch or Wingman match keeps its own.</summary>
    static void SetMatchLength(string by, string steamId, string arg)
    {
        if (!CurrentMode.ArmsRace) { Tell(steamId, M.Refused($"The match length is Arms Race's - {CurrentMode.Name} keeps its own")); return; } // ModeSwitch.cs
        var want = arg.Trim() switch
        {
            "" => S.RoundMinutes == DefaultRoundMinutes ? ShortRoundMinutes : DefaultRoundMinutes, // bare !time flips it
            var a when int.TryParse(a, out var n) && n == ShortRoundMinutes => ShortRoundMinutes,
            var a when int.TryParse(a, out var n) && n == DefaultRoundMinutes => DefaultRoundMinutes,
            _ => 0,
        };
        if (want == 0) { Tell(steamId, M.Refused($"Matches are {ShortRoundMinutes} or {DefaultRoundMinutes} minutes")); return; }
        if (want == S.RoundMinutes) { Tell(steamId, $"Matches are already {M.V($"{want} minutes")}"); return; }
        S.RoundMinutes = want;
        S.RoundBy = want == DefaultRoundMinutes ? null : Clip(by, 32); // the Game page says who set a length that is not the default
        Save();
        ApplyMatchLength();
        var reply = Send($"{MatchLength.Command} {want}"); // the round that is on: the plugin says what it did (HudScreenMatch.cs)
        Log($"{MatchLength.Command} {want} -> {reply ?? "(rcon failed)"}");
        Post(Messages.News, $"{M.N(Clip(by, 32))} set the match length to {M.V($"{want} minutes")} - {MatchLength.Effect(MatchLength.Read(reply))}");
    }
}
