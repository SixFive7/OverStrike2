// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.

/// <summary>What chat answers a command beyond ChatCommands' own lines, marked (M): the plugin says these (HudScreen.Chat) and --selftest walks them. A refusal is
/// LightRed, what worked Green, what only informs in the chat's own colour. Also the answers to a click that the plugin gives itself (Messages.Answer).</summary>
public static class ChatReply
{
    public static readonly string
        ChangeUnderWay = M.Refused($"{M.A("Start a vote")}: Nothing to do while a map change is under way"),
        VoteOn = "The vote for the next map is on - The menu shows it: click a card to vote",
        VoteWindowStays = M.Refused("The vote for the next map is on - This closes when it is over"),
        NoVote = M.Refused($"No vote is on - {M.A("Start a vote")}, or put a map to the vote with {M.C("!play")}"),
        CardOpen = M.Done($"Its card is open in the menu: {M.A("Download and play")}"),
        ListLoading = M.Refused("The map list is not loaded yet - Try again in a moment"),
        NoLine = M.Refused("The menu has no line to the server right now - Try again in a moment"),
        HowToSearch = "Search with the chat: press Y, type part of a map name, then press Enter - The matching maps show here"; // a click on the search area (233)

    public static string MenuOn(string page) => M.Done($"The menu is open on {M.A(page)}");

    public static string Searching(string words) => M.Done($"The menu's map browser is searching for \"{M.A(Hud.Clean(words, 40))}\"");

    /// <summary>A chat line on MAPS > Browse whose words find no map (233): the cards stay as they are, and the window's line says why.</summary>
    public static string ChatSearchNone(string words) => M.Refused($"No map matches \"{M.A(Hud.Clean(words, 40))}\" - Try fewer letters or other words");

    public static string NoMatch(string words) => M.Refused($"No map matches \"{M.A(Hud.Clean(words, 40))}\" - Other words, a number, or a Workshop ID");

    public static string Matches(int count) => $"{M.V(count.ToString(System.Globalization.CultureInfo.InvariantCulture))} maps match - The menu shows them: click {M.A("Play")} on one";

    public static string YourVote(string label) => M.Done($"Your vote: {M.A(Hud.Clean(label, 40))}");

    public static string NotInMode(Fun.Setting s, string mode) => M.Refused($"{M.A(s.Title)} is Arms Race's own - Not in {M.V(mode)}");

    public static string Already(Fun.Setting s, Fun.Choice c) => $"{M.A(s.Title)}: {M.V(c.Label)} already";

    public static string DidNotSwitch(Fun.Setting s) => M.Refused($"{M.A(s.Title)} did not switch: the server's settings module did not take it");

    // ---- the ballot's clicks (HudVote, BridgeLink) ----

    /// <summary>A click on a ballot card whose map has left the vote since the player saw it - a player's own map they put another in place of, a map no
    /// longer playable: it counts for nothing (the bridge's CastBallot).</summary>
    public static readonly string CardGone = M.Refused("That map left the vote - Click again");

    /// <summary>A click the plugin could not hand to the server: its line was full, or a vote waited too long for room (BridgeLink).</summary>
    public static readonly string ClickLost = M.Refused("The server did not get that click - Click again");
}
