// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.

/// <summary>The scores at a match-end vote (content v4.14.0, the owner's 292 a): there TAB cannot peek. CS2 takes each player's own pawn away for the whole end
/// screen - the client's own console says "Shutdown prediction for player slot 0" as the match ends and "Added prediction" only as the next one starts (the live
/// lab's final round of release 218) - and the server reads TAB from that pawn alone (CounterStrikeSharp's OnPlayerButtonsChanged), so no key reaches it. A
/// click does. So once a player has voted, or the vote is decided, the ballot's SCORES button steps their window aside for CS2's own end-screen scoreboard, up
/// behind it all along, and it stays aside - no timer (292 a) - until their click on Back to the vote, the decision of the vote they looked during (they see the
/// result), or the match-end screen's end; a look begun after the decision ends only at Back to the vote or the screen's end. Only classes, drawn by HudFrame:
/// vscoresok - the button may show; vscores - the look is on, and Back to the vote shows; vpeek with it, as with TAB held mid-match, so the stylesheet steps the
/// window, the column, the dim, the pointer and the dialog aside as for TAB. The window stays open, frozen and with the mouse throughout. Mid-match TAB stays the
/// way: the button is for the match-end screen alone, once CS2 shows its scoreboard there (meos).</summary>
public static class HudScores
{
    public const string Button = "os_v_scores", BackButton = "os_v_tovote";

    /// <summary>Whether a player may step the window aside now: the vote's window shows, the match-end screen shows CS2's scoreboard (meos, HudFrame.World.ChatLift),
    /// and they may look (HudFrame.Peekable: they have voted, or the vote is decided).</summary>
    public static bool Allowed(bool window, bool matchEnd, VoteState vote, long account) => window && matchEnd && HudFrame.Peekable(vote, account);

    /// <summary>Whether this player's look is on now: the match-end screen still up, the vote round it began in, and - begun while the vote was open - not past that
    /// vote's decision. No timer: it lasts until Back to the vote (Click), that decision or the screen's end.</summary>
    public static bool On(HudPlayer p, VoteState vote, bool matchEnd) =>
        matchEnd && vote.Round > 0 && p.Scores is { } s && s.Round == vote.Round && !(s.WhileOpen && !vote.Open);

    /// <summary>A click on SCORES or on Back to the vote: what it did, for the log - null for any other button. SCORES while the player may not look, or is looking,
    /// changes nothing; Back to the vote ends the look.</summary>
    public static string? Click(HudPlayer p, string buttonId, bool window, bool matchEnd, VoteState vote, long account)
    {
        if (buttonId == Button)
        {
            if (On(p, vote, matchEnd)) return "already looking - nothing";
            if (!Allowed(window, matchEnd, vote, account)) return $"not now ({WhyNot(window, matchEnd, vote)})";
            p.Scores = (vote.Round, vote.Open);
            return vote.Open ? "the window steps aside until Back to the vote or the vote's decision" : "the window steps aside until Back to the vote";
        }
        if (buttonId != BackButton) return null;
        var was = On(p, vote, matchEnd);
        p.Scores = null;
        return was ? "the window is back" : "not looking - nothing";
    }

    static string WhyNot(bool window, bool matchEnd, VoteState vote) =>
        vote.Round <= 0 ? "no vote" : !matchEnd ? "not at the match-end screen - TAB is the way" : !window ? "no window" : "not voted yet";

    /// <summary>Why a look that was on is off now, for the log - "" while it is still on: the match-end screen gone, the vote over, the vote decided, the window
    /// closed.</summary>
    public static string Ended(HudPlayer p, VoteState vote, bool window, bool matchEnd) =>
        p.Scores is not { } s ? "Back to the vote"
        : !matchEnd ? "the match-end screen ended"
        : vote.Round != s.Round ? "the vote is over"
        : s.WhileOpen && !vote.Open ? "the vote decided"
        : !window ? "the window closed" : "";

    /// <summary>Why a look ended with the map (the plugin's ScoresEndWithMap), for the log.</summary>
    public const string MapEnded = "the match-end screen ended with the map";

    /// <summary>A player's game disconnects (the plugin's HudScreen.Gone) with a look on - before any frame saw it end, as a change sent out at once (a vote's
    /// official map) has it: the disconnects come before the decision reaches the plugin and before the map's end, and the live lab on release 223 found no line
    /// at all. Why it ended, for the log - a map change's disconnect (HudChat.MapChangeReason) as the map's end words it, any other as their leaving - and the
    /// look forgotten; null when none was on.</summary>
    public static string? Gone(HudPlayer p, string reason)
    {
        if (p.Scores == null) return null;
        p.Scores = null;
        return reason == HudChat.MapChangeReason ? MapEnded : "they left the game";
    }
}
