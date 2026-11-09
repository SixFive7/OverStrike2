// !time: the match length, 10 or 60 minutes, for the match that is on too - Arms Race and Deathmatch are each one long round (mp_roundtime), and each keeps its own length
// (release 197): Arms Race's starts at 60, Deathmatch's at Valve's 10. Wingman plays a match of rounds, and keeps its own.

static partial class Program
{
    public const int DefaultRoundMinutes = 60, ShortRoundMinutes = 10; // 60 is the engine ceiling for mp_roundtime; Arms Race is one long round, so this is the match length

    /// <summary>A mode's match length and who set it: Arms Race's or Deathmatch's; none (0) in Wingman.</summary>
    static (int Minutes, string? By) MatchLengthOf(Modes.Mode mode) =>
        mode.ArmsRace ? (S.RoundMinutes, S.RoundBy) : mode.Id == Modes.DeathmatchId ? (S.DmRoundMinutes, S.DmRoundBy) : (0, null);

    /// <summary>The mode that is on gets its length: mp_roundtime, which its next round start reads. Nothing in Wingman.</summary>
    static void ApplyMatchLength()
    {
        var (minutes, _) = MatchLengthOf(CurrentMode);
        if (minutes > 0) Log($"mp_roundtime {minutes} -> {Send($"mp_roundtime {minutes}") ?? "(rcon failed)"}");
    }

    /// <summary>!time: switch the match length between 10 and 60 minutes, for the match that is on too (MatchLength.cs), in the mode that is on. Stored in state and re-applied
    /// after every map load, because the mode's cfg sets mp_roundtime back to its own each time the mode is created. Wingman's match is rounds: refused there.</summary>
    static void SetMatchLength(string by, string steamId, string arg)
    {
        var mode = CurrentMode; // ModeSwitch.cs
        if (!MatchLength.Applies(mode.Id)) { Tell(steamId, M.Refused($"The match length is Arms Race's and Deathmatch's - {mode.Name} plays a match of rounds")); return; }
        var (now, _) = MatchLengthOf(mode);
        var want = arg.Trim() switch
        {
            "" => now == DefaultRoundMinutes ? ShortRoundMinutes : DefaultRoundMinutes, // bare !time flips it
            var a when int.TryParse(a, out var n) && n == ShortRoundMinutes => ShortRoundMinutes,
            var a when int.TryParse(a, out var n) && n == DefaultRoundMinutes => DefaultRoundMinutes,
            _ => 0,
        };
        if (want == 0) { Tell(steamId, M.Refused($"Matches are {ShortRoundMinutes} or {DefaultRoundMinutes} minutes")); return; }
        if (want == now) { Tell(steamId, $"Matches are already {M.V($"{want} minutes")}"); return; }
        var setBy = want == MatchLength.DefaultMinutes(mode.Id) ? null : Clip(by, 32); // the Game page says who set a length that is not the mode's own
        if (mode.ArmsRace) (S.RoundMinutes, S.RoundBy) = (want, setBy);
        else (S.DmRoundMinutes, S.DmRoundBy) = (want, setBy);
        Save();
        ApplyMatchLength();
        var reply = Send($"{MatchLength.Command} {want}"); // the round that is on: the plugin says what it did (HudScreenMatch.cs)
        Log($"{MatchLength.Command} {want} -> {reply ?? "(rcon failed)"}");
        Post(Messages.News, $"{M.N(Clip(by, 32))} set the match length to {M.V($"{want} minutes")} - {MatchLength.Effect(MatchLength.Read(reply))}");
    }
}
