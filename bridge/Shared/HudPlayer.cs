// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.

/// <summary>Everything the HUD keeps for the player in one slot, in one record: the plugin makes a new one when a player takes the slot and drops it when
/// they leave (a map change's reconnect included), so nothing of the slot's last player reaches the next. What a player keeps through a reconnect is kept by
/// account, never here: the HUD their game showed at the check's click, their time on (JoinTimes), their likes and votes (the bridge's). HudSlots, HudGate,
/// HudTab and FunClicks keep their own part of a slot, and forget it at the same moments (HudScreen.Leave).</summary>
public sealed class HudPlayer
{
    /// <summary>The map browser: sort, filters, search, page.</summary>
    public HudBrowser Browser { get; } = new();

    /// <summary>The HISTORY page: its chips, Mine, the order, the search, the page (content v4.15.0).</summary>
    public HudHistoryPage History { get; } = new();

    /// <summary>The card of an unlisted Workshop id this player typed.</summary>
    public WorkshopCard Card { get; } = new();

    /// <summary>The vote this player is looking at: its round, the ballot's page, and whether the map browser is open on it (MAPS, Browse).</summary>
    public (long Round, int Page, bool Browse) VoteView;

    /// <summary>This player's last Add to the vote from the map browser: the round, the Play it came from and when - a click on that very button within
    /// HudVote.DoubleClickSeconds is the second click of a double click (HudFrame.SecondClick). Round 0: none.</summary>
    public (long Round, string Button, DateTime At) Added;

    /// <summary>This player's last click as HudFrame.ReadClick read it: the control, whether it came by its version twin, and when - a twin's click and its control's
    /// own within HudFrame.TwinPairMs are one click (content v4.17.0).</summary>
    public (string Id, bool Twin, DateTime At) LastClick = ("", false, DateTime.MinValue);

    /// <summary>HISTORY's picture panels as this player's frames last asked for them (HudFrame.OnePerFrame): a page turn's new pictures come one a frame. Empty off
    /// HISTORY, so the page starts afresh each time it is opened.</summary>
    public Dictionary<string, string[]> HistoryPictures { get; } = new();

    /// <summary>A bar's snap, by its panel (HudFrame.Snapped, content v4.14.0): the step it shows, and where the snap stands - 0 none, 1 snapped, 2 the new step taken.</summary>
    public Dictionary<string, (string Shown, int Stage)> Snap { get; } = new();

    /// <summary>The vote round whose window was opened for this player (once a round), and whether that vote's freeze holds it open now.</summary>
    public long VoteShown;
    public bool VoteOpened;

    /// <summary>This player's page of each of the Sync page's lists (HudDash.View).</summary>
    public Dictionary<string, int> DashPages { get; } = new();

    /// <summary>Until when the second click of Restart the server and of Restart the match counts.</summary>
    public DateTime RestartArmed, RematchArmed;

    /// <summary>Catch-up's card under the crosshair: its head, its rows, red for a bounty, from and until when.</summary>
    public (string Head, string[] Rows, bool Bad, DateTime From, DateTime Until)? CatchUp;

    /// <summary>A note of the plugin's own for this player (no line to the bridge): its words, until when, its category.</summary>
    public (string Text, DateTime Until, string Cat)? Note;

    /// <summary>Until when this player's search is helped, after a click on the search area (qhelp on os_screen, content v4.11.0): a search ends it sooner, as does a
    /// second click.</summary>
    public DateTime SearchHelpUntil;

    /// <summary>The scores at a match-end vote (HudScores, content v4.14.0): the vote round this player's look at CS2's scoreboard is for, and whether it began while
    /// that vote was open - its decision ends it then. No timer (292 a). Null: none.</summary>
    public (long Round, bool WhileOpen)? Scores;

    /// <summary>When everything this player was sent is sent again: a class set while the client is still loading the layout never lands (HudScreen).</summary>
    public DateTime? ResendAt;

    /// <summary>Seconds left of an armed restart button's second click, 0 when it is not armed.</summary>
    public static int ArmedSeconds(DateTime until, DateTime now) => now < until ? (int)Math.Ceiling((until - now).TotalSeconds) : 0;
}
