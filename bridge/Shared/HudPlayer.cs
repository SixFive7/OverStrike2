// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.

/// <summary>Everything the HUD keeps for the player in one slot, in one record: the plugin makes a new one when a player takes the slot and drops it when
/// they leave (a map change's reconnect included), so nothing of the slot's last player reaches the next. What a player keeps through a reconnect is kept by
/// account, never here: the HUD their game showed at the check's click, their time on (JoinTimes), their likes and votes (the bridge's). HudSlots, HudGate,
/// HudTab and FunClicks keep their own part of a slot, and forget it at the same moments (HudScreen.Leave).</summary>
public sealed class HudPlayer
{
    /// <summary>The map browser: sort, filters, search, page.</summary>
    public HudBrowser Browser { get; } = new();

    /// <summary>The card of an unlisted Workshop id this player typed.</summary>
    public WorkshopCard Card { get; } = new();

    /// <summary>The vote this player is looking at: its round, the ballot's page, and whether the map browser is open on it (MAPS, Browse).</summary>
    public (long Round, int Page, bool Browse) VoteView;

    /// <summary>The ballot's cards as last drawn for this player, and before the last change: a click counts against what the player saw (HudFrame.SeenOrder).</summary>
    public (long Round, int Page, int[] Now, int[] Before, DateTime ChangedAt) Drawn = (0, 0, [], [], DateTime.MinValue);

    /// <summary>The card this player's vote is on as the frames have seen it (0: none), and since when: a click there in the first HudVote.KeepSeconds keeps the
    /// vote (HudFrame.KeepsVote).</summary>
    public (long Round, int N, DateTime Since) Mine;

    /// <summary>After this player's Add to the vote: the round, their vote before it, and until when their ballot page follows their vote to its page
    /// (HudFrame.FollowVote). Round 0: not following.</summary>
    public (long Round, int From, DateTime Until) Follow;

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

    /// <summary>When everything this player was sent is sent again: a class set while the client is still loading the layout never lands (HudScreen).</summary>
    public DateTime? ResendAt;

    /// <summary>Seconds left of an armed restart button's second click, 0 when it is not armed.</summary>
    public static int ArmedSeconds(DateTime until, DateTime now) => now < until ? (int)Math.Ceiling((until - now).TotalSeconds) : 0;
}
