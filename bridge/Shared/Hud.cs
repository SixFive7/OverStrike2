// The in-game HUD - the server's interface, with the chat commands beside it (ChatCommands.cs): one
// custom_hud_layout window (the menu: maps with full-text search from chat (!s), the vote, game settings, help and how it works,
// beside the live map state), the board beside the scoreboard while TAB is held (the click action panel: Start a vote, likes, the menu), a
// now-playing card, a map-change banner, the top line everybody sees and the line only its player sees. Shared like Guide.cs: the bridge
// compiles this file (it builds the live state, and --selftest drives everything here on a handed clock), and the OverStrike2 plugin links it
// in and puts it on screen. Plain BCL only: no CounterStrikeSharp type, no reflection (the bridge is native AOT).
//
// The layout itself (XML + CSS) is a Workshop addon, hud/. It holds structure, style and a few fixed headings
// ("Now playing", the tab names); every other word comes from here, as dialog variables, so changing it is a plugin deploy, not a
// Workshop republish.
using System.Globalization;
using System.Text;

public static class Hud
{
    /// <summary>The release in the layout's and the stylesheets' file names (hud/layout/gungame_hud_{ReleaseFile}.xml, hud/styles/gungame_hud_{ReleaseFile}.css,
    /// hud/styles/gungame_hud_thumbs_{ReleaseFile}.css): the one place a version keeps its old form, because every content shipped so far has only these files
    /// and the plugin must keep asking for them. CS2 caches a layout by its path for the whole game session, so a player who still has an older release
    /// loaded never gets it served for a new one: a new release is a new path (hud/PUBLISH.md). The content on that path is HudVersion.Content.</summary>
    public const string ReleaseFile = "v3_0_0";

    /// <summary>The layout as the client looks it up: the SOURCE path, extension included. Anything else spawns fine and shows nothing.</summary>
    public const string Layout = "panorama/layout/custom_game/gungame_hud_" + ReleaseFile + ".xml";

    /// <summary>Paths on the bridge's HTTP port, followed by LOG_SECRET: the plugin GETs the live state, the map catalog and the search
    /// index, and POSTs a player's click.</summary>
    public const string StatePath = "/hud/", ActPath = "/act/", CatalogPath = "/catalog/", SearchPath = "/search/", DashPath = "/dash/";

    /// <summary>The live state's long poll (the bridge's HudTransport.cs, the plugin's BridgeLink.cs): how long the bridge holds a poll that has the
    /// newest version before it answers that nothing changed; the version line every answer starts with; the word of an answer without a change.</summary>
    public const int LongPollSeconds = 25;
    public const string VersionLine = "ver", Unchanged = "unchanged";

    /// <summary>Where a restart's warning says how long is left: the HUD puts the countdown there from the restart's end time (HudState.AsOf), so
    /// the words stay the same while they count down; a plugin that long-polls no version gets them filled in by the bridge.</summary>
    public const string LeftToken = "{left}";

    /// <summary>The query the plugin adds to its GET of the live state: whether it draws the HUD (draw=1), or draws nothing a vote may count on (draw=0,
    /// during a hold, while Steam does not show the HUD item: HudScreen.RequireAddon). Only a drawing plugin makes the bridge open votes (the bridge's HudActive).</summary>
    public const string DrawQuery = "draw";

    /// <summary>The actions a click may ask for without an argument. They are written like the chat commands, which is what the bridge and
    /// the plugin speak to each other on /act: a typed command arrives as the same click.</summary>
    public static readonly string[] Actions = ["!up", "!down", "!next", "!next random", "!previous", "!restart", "!rematch", "!time", "!time 10", "!time 60", "!time 8", "!time 16"];

    /// <summary>Whether a click may ask for this. The bridge refuses anything else on its /act path, so the menu can never reach a server
    /// command, a name search or anything free-form: a map is named by its rank, an official map's name or a Workshop id.</summary>
    public static bool IsAction(string command)
    {
        if (Actions.Contains(command)) return true;
        if (command.StartsWith("!hudok ", StringComparison.Ordinal)) return HudVersion.IsWord(command[7..]); // the HUD check's click (HudCheck)
        if (command == EarlyJoin.Command) return true; // the fairness restart's ask: the plugin's own, never a chat command (ChatCommands has no such word)
        var space = command.IndexOf(' ');
        if (space < 0) return false;
        var (verb, arg) = (command[..space], command[(space + 1)..]);
        if (verb == "!down" && DownReasons.IsArg(arg)) return true; // a dislike's reason, with or without its map (the popup, the chat's !down big, !down 12 big)
        if (verb == "!map" && SplitMode(arg) is { Mode: not "" } inMode) return IsMapArg(inMode.Map); // a card's Play in another mode that also lists the map
        return verb is "!map" or "!up" or "!down" && IsMapArg(arg);
    }

    /// <summary>What names a map on /act: a rank or a Workshop id, or an official map's name, of any mode - never a name search.</summary>
    public static bool IsMapArg(string arg) => arg.Length is > 0 and <= WorkshopCard.MaxIdDigits && arg.All(char.IsAsciiDigit)
        || arg.Length is > 3 and <= 32 && (arg.StartsWith("ar_") || arg.StartsWith("de_") || arg.StartsWith("cs_")) && arg.All(ch => ch == '_' || char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch));

    /// <summary>A !map argument as the map and the mode it asks for: "12 deathmatch" is 12 in Deathmatch, "12" is 12 in the first mode that lists it.</summary>
    public static (string Map, string Mode) SplitMode(string arg)
    {
        var at = arg.LastIndexOf(' ');
        return at > 0 && Modes.Find(arg[(at + 1)..]) is { } mode ? (arg[..at], mode.Id) : (arg, "");
    }

    // ---- the words: every number in them is the code's own (--selftest checks it) ----

    public sealed record Row(string Command, string Words, string Also = "");
    public sealed record Group(string Name, Row[] Rows);
    public sealed record Rule(string Title, string[] Lines);

    public const int RowsPerGroup = 4; // the layout has exactly this many slots

    /// <summary>The Help page: where each thing is in the menu. Command is the place to click, Words what it does; every action has its chat
    /// command too (ChatCommands.cs, !help).</summary>
    public static readonly Group[] Help =
    [
        new("Maps", [
            new("Maps  ·  Play", "Put any map to the vote: a vote opens, your vote on that map"),
            new("Search  ·  !find", "On Browse, press Y and type a map name - Or !find anywhere"),
            new("Start a vote", "On the board or in the menu: a vote for the next map"),
            new("Previous  ·  !back", "On the Maps page: the map before this one, to the vote")]),
        new("The vote", [
            new("Everybody votes", "Everybody is frozen until all have voted, 60 s at the most"),
            new("Cards", "Click to vote, another to change it, yours to take it back"),
            new("Random", "The dice after the vote, in this mode"),
            new("The wait", "2 minutes after a vote; a match end opens one anyway")]),
        new("Likes", [
            new("Like  ·  Dislike", "On the map that is on; the other one takes a mark back first"),
            new("Dislike  ·  Reasons", "Too big: 1 random pick in 100; Ads: never a random pick; the rest: a dislike"),
            new("Maps  ·  Arrows", "Like or dislike any map's card: up likes, down dislikes"),
            new("Sort  ·  Likes", "The most liked maps first")]),
        new("Game", [
            new("Fun", "Gravity, friendly fire, grenade drops, catch-up, bots"),
            new("Server  ·  Game", "Match length: 10 or 60 minutes, Wingman 8 or 16 rounds"),
            new("Restart", "Server  ·  Game: the match or the whole server - Click twice to confirm")]),
        new("The board", [
            new("Hold TAB", "The live board beside the scoreboard, with the mouse"),
            new("TAB + E", "The full menu"),
            new("Help  ·  Server", "How it works under Help; who is on, the game and the map sync under Server"),
            new("Chat  ·  !help", "Every action has a chat command too - !help lists them")]),
    ];

    /// <summary>The "How it works" page (layout content v3.1.0) - the one set of rule texts (the Rules tab is gone; !rules opens this page): the
    /// vote, the ballot's recipe, the dice (what is on the server's disk, the 1% too-big rule), the points and what likes do, and every exclusion,
    /// tersely and completely. Each number in it is the bridge's own constant: --selftest (SelfTestHud.cs, SelfTestRules.cs, SelfTestBallot.cs)
    /// fails when one of them moves without these words.</summary>
    public static readonly Rule[] How =
    [
        new("The vote", [
            "Play on a card puts its map to the vote, with your vote on it. While a vote is on, it goes on that ballot. Start a vote calls one without a map.",
            "A vote freezes everybody (no moving, shooting or closing it) until all have voted, 60 s at the most. Most votes win; a tie is drawn.",
            "Click a card to vote, another to change it, your own to take it back. A map put on it leaves once it has no votes, and the card it replaced comes back.",
            "A match end opens a vote by itself. After any vote, 2 minutes before the next one can be called. No votes: mid-match the map stays, at a match end Random."]),
        new("The ballot", [
            "Keep playing this map while the match is on, Play this map again after it, every map a player put on it, and one map of each other mode.",
            "Then this mode: a fresh map (a Workshop upload or update of the last 30 days), a surprise new to us, and favourites the server keeps fill the page.",
            "No card moves while the vote is open; a second page only when needed.",
            "Every card but a player's pick is on the server's disk, so it loads at once. A risky map shows its record, and putting it to the vote takes a second click."]),
        new("The dice", [
            "Random is the dice after the vote. Like every card the dice pick, it is a map on the server's disk: a favourite it keeps, or one of a small stock new to us.",
            "1 random pick in 100 takes a too-big map (measured XL, or marked Too big by a player), the other 99 one that is not; an empty group hands the pick to the other.",
            "The dice change the map by themselves only at an empty server start, 10 minutes with nobody on, a broken map. Previous puts the map before this one to the vote."]),
        new("Points: each one doubles the odds", [
            "Workshop popularity (rating, players, trend) gives up to 4 points, each Like a point (up to 5 Likes), and each Dislike or Too big takes 2.",
            "A card shown and not chosen loses 1 point, 2 at most, fading to nothing over 28 days.",
            "One mark per player per map, kept: Like, or Dislike with a reason. Only Too big and Ads do more than a Dislike. The opposite one takes it back first.",
            "Nothing else counts: a map's download size never makes it more or less likely."]),
        new("Never rolled", [
            "The map that is on, and the last 10 maps played (while others are left).",
            "Maps that crashed the server or are on the broken list, blocked maps, and maps gone from the Workshop. A Workshop update lifts a crash mark.",
            "Maps a player marked Ads (they show ads or send players to another server) - Anybody can still put them to the vote."]),
    ];

    public const int HowSections = 5, HowLines = 4; // the How page's sections and the lines in each (the layout has exactly this many)

    /// <summary>The start page: three steps, each a title, where to click and a few words.</summary>
    public static readonly (string Title, string Command, string Words)[] Steps =
    [
        ("Pick a map", "Maps", "Every map with its picture; Play puts it to the vote."),
        ("Vote", "Maps  ·  Vote", "Everybody votes; the most votes win."),
        ("Like it", "Like  Dislike", "Your likes steer the random maps."),
    ];

    /// <summary>The large text of the welcome window: the tagline. What the game mode is everybody knows.</summary>
    public const string Headline = "Over the top - On the Huis";
    public const string MenuHint = "Hold TAB any time: the board takes clicks - Start a vote, your like, the full menu.";

    // ---- small helpers both programs use ----

    /// <summary>A value as the layout should show it, word for word. Since 2026-09-22 the game reads a dialog value that starts with '#'
    /// as a localization token, so a rank like "#5" or a name like "#1 sniper" would not show as written; an invisible word joiner
    /// (U+2060) in front keeps it plain - the same fix CS2KZ ships. The plugin passes every text through this.</summary>
    public static string Plain(string text) => text.StartsWith('#') ? "⁠" + text : text;

    /// <summary>One line, no tab, no newline - the codec's separators - and never longer than max.</summary>
    public static string Clean(string? text, int max = 80)
    {
        var s = new StringBuilder();
        foreach (var ch in text ?? "") s.Append(ch is '\t' or '\r' or '\n' || char.IsControl(ch) ? ' ' : ch);
        var t = s.ToString().Trim();
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "~";
    }

    /// <summary>Steam account id from "[U:1:123]", a bare account id, or a SteamID64; -1 when unrecognised. Same rules as the bridge's AccountId.</summary>
    public static long AccountId(string? text)
    {
        text = (text ?? "").Trim();
        if (text.StartsWith("[U:1:") && text.EndsWith(']') && long.TryParse(text[5..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var u)) return u;
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? (n >= 76561197960265728 ? n - 76561197960265728 : n) : -1;
    }

    public static string Tally(int up, int down) => up + down == 0 ? "No votes yet" : $"+{up}{(down > 0 ? $"  -{down}" : "")}";

    /// <summary>Who or what picked a map, as a card says it.</summary>
    public static string ByWords(string by) => by == "" ? "" : by == "the dice" ? "Rolled by the dice" : by == "the vote" ? "Picked by the vote" : $"Picked by {by}";
}
