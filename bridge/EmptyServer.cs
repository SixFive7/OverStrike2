// An empty server: once no human has been connected for more than ResetEmptyMinutes (10), the Fun page's changes are undone - the grenade drops
// and catch-up among them, ordinary fun settings - with gg_fun reset over RCON, and the server goes back to Arms Race (ModeSwitch.cs).

static partial class Program
{
    public const int ResetEmptyMinutes = 10;   // nobody connected for longer than this: the fun settings go back to their defaults

    /// <summary>The Fun page's changes undone (the settings module's gg_fun reset, Fun.cs): every row back to its first choice - the grenade drops
    /// and catch-up too. They last until the map changes anyway; an empty server puts them back sooner, like the other settings.</summary>
    public const string FunResetCommand = Fun.ResetCommand + " \"the server\"";

    // ---- the reset: nobody on for more than ResetEmptyMinutes, and the server-wide settings go back to their defaults ----

    static DateTime? NobodySince;   // nobody connected - in game, or reloading inside the load grace - since then
    static bool ResetDone;          // this empty spell's reset has been done
    static DateTime ResetProbeAt;   // when to ask RCON status again (once a minute while it says somebody is there, or does not answer)

    /// <summary>From Tick. The log stream says who is present (VoteRound.cs: in game, or reloading inside its 10-minute grace). Past
    /// ResetEmptyMinutes of nobody, RCON status has the last word - a bridge that started mid-map has not seen the joins - and only "0 humans"
    /// resets: the fun settings back. Once per empty spell; anybody connecting ends it.</summary>
    static void ResetWhenEmpty(DateTime now)
    {
        if (PresentIds(now).Count > 0) { NobodySince = null; ResetDone = false; return; }
        NobodySince ??= now;
        if (ResetDone) { ModeBackToDefault(now); return; } // ModeSwitch.cs: back to Arms Race too, once nothing is in the way
        if ((now - NobodySince.Value).TotalMinutes <= ResetEmptyMinutes || now < ResetProbeAt) return;
        ResetProbeAt = now.AddMinutes(1);
        if (Send("status") is not { } status) { Log("reset: RCON not answering - asking again in a minute"); return; }
        if (HumansRx().Match(status) is { Success: true } h && int.Parse(h.Groups[1].Value) > 0)
        {
            Log($"reset: the log stream saw nobody for {ResetEmptyMinutes} min, but RCON status says {h.Groups[1].Value} human(s) - no reset");
            return;
        }
        ResetDone = true;
        var fun = Send(FunResetCommand);
        Log($"reset: nobody on for over {ResetEmptyMinutes} min - {Fun.ResetCommand} -> {fun ?? "(rcon failed)"}");
        ModeBackToDefault(now);
    }
}
