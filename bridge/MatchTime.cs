// !time: the match length. Arms Race and Deathmatch are each one long round (mp_roundtime): 10 or 60 minutes, for the match that is on too, and each keeps its own
// (release 197); both start at 60. Wingman plays a match of rounds (mp_maxrounds): 8 or 16, 16 to begin with, for the match that is on while no halftime is at stake and
// otherwise from the next match (release 199; MatchLength.cs).

static partial class Program
{
    public const int DefaultRoundMinutes = MatchLength.LongMinutes, ShortRoundMinutes = MatchLength.ShortMinutes; // 60 is the engine ceiling for mp_roundtime; Arms Race is one long round, so this is the match length

    /// <summary>A mode's match length and who set it: Arms Race's or Deathmatch's minutes, Wingman's rounds.</summary>
    static (int Length, string? By) MatchLengthOf(Modes.Mode mode) =>
        mode.ArmsRace ? (S.RoundMinutes, S.RoundBy) : mode.Id == Modes.DeathmatchId ? (S.DmRoundMinutes, S.DmRoundBy)
        : MatchLength.InRounds(mode.Id) ? (S.WmRounds, S.WmRoundsBy) : (0, null);

    /// <summary>The setting a mode's length is: the one round's minutes, mp_roundtime; Wingman's rounds, mp_maxrounds.</summary>
    static string LengthSetting(Modes.Mode mode) => MatchLength.InRounds(mode.Id) ? "mp_maxrounds" : "mp_roundtime";

    /// <summary>The mode that is on gets its length: mp_roundtime, which its next round start reads; in Wingman mp_maxrounds, which the match reads at every round's end -
    /// so only where no halftime is at stake: a map's start, a match's end, the bridge's own Restart match.</summary>
    static void ApplyMatchLength()
    {
        var mode = CurrentMode;
        var (length, _) = MatchLengthOf(mode);
        if (length > 0) Log($"{LengthSetting(mode)} {length} -> {Send($"{LengthSetting(mode)} {length}") ?? "(rcon failed)"}");
    }

    /// <summary>A match's end in Wingman: the next match - on a kept map it starts without a load - plays the chosen count from its first round. The match is over, so no
    /// halftime is at stake. The other modes' next round start reads mp_roundtime, set already.</summary>
    static void MatchLengthAtMatchEnd()
    {
        if (MatchLength.InRounds(CurrentMode.Id)) ApplyMatchLength();
    }

    /// <summary>2 s after every map start - after the mode's cfg has set mp_roundtime to its own, and after the bridge has heard which mode the map is in
    /// (SyncMode, 1.5 s after the start, takes the plugin's word at a start the bridge did not ask for): the mode that is on gets its length back - in
    /// Deathmatch every time, so a server whose Deathmatch cfg is still Valve's 10 gets 60 all the same; in Arms Race a chosen 10; in Wingman a chosen 8.
    /// Decided here, by the mode on record now, not at the start: after an operator's switch the record still names the old mode then, and a Deathmatch
    /// set to 10 ran the cfg's 60 (release 198).</summary>
    static void MatchLengthAfterStart()
    {
        var mode = CurrentMode;
        if (MatchLengthOf(mode) is { Length: > 0 } length && (length.Length != MatchLength.Default(mode.Id) || mode.Id == Modes.DeathmatchId)) ApplyMatchLength();
    }

    /// <summary>!time: switch the match length between the mode's two (MatchLength.cs), in the mode that is on - 10 and 60 minutes for the match that is on too, 8 and 16
    /// rounds in Wingman. Stored in state and re-applied after every map load, because the mode's cfg sets its own back each time the mode is created.</summary>
    static void SetMatchLength(string by, string steamId, string arg)
    {
        var mode = CurrentMode; // ModeSwitch.cs
        var (shortOne, longOne) = MatchLength.Choices(mode.Id);
        var (now, _) = MatchLengthOf(mode);
        var want = arg.Trim() switch
        {
            "" => now == longOne ? shortOne : longOne, // bare !time flips it
            var a when int.TryParse(a, out var n) && (n == shortOne || n == longOne) => n,
            _ => 0,
        };
        if (want == 0)
        {
            Tell(steamId, M.Refused(MatchLength.InRounds(mode.Id) ? $"{mode.Name}'s matches are {shortOne} or {longOne} rounds" : $"Matches are {shortOne} or {longOne} minutes"));
            return;
        }
        if (want == now) { Tell(steamId, $"Matches are already {M.V(MatchLength.Words(mode.Id, want))}"); return; }
        var setBy = want == MatchLength.Default(mode.Id) ? null : Clip(by, 32); // the Game page says who set a length that is not the mode's own
        if (mode.ArmsRace) (S.RoundMinutes, S.RoundBy) = (want, setBy);
        else if (mode.Id == Modes.DeathmatchId) (S.DmRoundMinutes, S.DmRoundBy) = (want, setBy);
        else (S.WmRounds, S.WmRoundsBy) = (want, setBy);
        Save();
        if (MatchLength.OneRound(mode.Id)) ApplyMatchLength(); // mp_roundtime, for the rounds to come; Wingman's mp_maxrounds the plugin sets itself, where no halftime is at stake
        var reply = Send($"{MatchLength.Command} {want}"); // the match that is on: the plugin says what it did (HudScreenMatch.cs)
        Log($"{MatchLength.Command} {want} -> {reply ?? "(rcon failed)"}");
        Post(Messages.News, $"{M.N(Clip(by, 32))} set the match length to {M.V(MatchLength.Words(mode.Id, want))} - {MatchLength.Effect(MatchLength.Read(reply))}");
    }
}
