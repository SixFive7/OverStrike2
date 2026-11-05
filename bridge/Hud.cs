// The in-game HUD - the server's interface, with the chat commands beside it (ChatCommands.cs): one
// custom_hud_layout window (the menu: maps with full-text search and an on-screen keyboard, the vote, game settings, help and how it works,
// beside the live map state), the board beside the scoreboard while TAB is held (the click action panel: Start a vote, likes, the menu), a
// now-playing card, a map-change banner, the news line everybody sees and the note only its player sees. Shared like Guide.cs: the bridge
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
    /// <summary>The layout release as a version word: a click that proves no content says this one (HudCheck).</summary>
    public const string Release = "v3.0.0";

    /// <summary>The release in the layout's and the stylesheets' file names (hud/layout/gungame_hud_{ReleaseFile}.xml, hud/styles/gungame_hud_{ReleaseFile}.css,
    /// hud/styles/gungame_hud_thumbs_{ReleaseFile}.css): the one place a version keeps its old form, because every content shipped so far has only these files
    /// and the plugin must keep asking for them. CS2 caches a layout by its path for the whole game session, so a player who still has an older release
    /// loaded never gets it served for a new one: a new release is a new path (README "Building and publishing the HUD addon").</summary>
    public const string ReleaseFile = "v3_0_0";

    /// <summary>The layout's content on that path. A content update keeps the path, so games holding older contents of the same release stay in
    /// use - during Valve's review and until a player's Steam has downloaded the update - and one plugin drives them all. The rule since v4.6.0:
    /// the plugin sends what this content has, and an older content simply lacks what it lacks (a panel a game does not have takes nothing in
    /// it). What an older content needs to show its own pages keeps coming: its page classes, c31 and c32 (every content in use holds the panels
    /// those two uncover) and the svge/svlt version classes its join banner reads. The newest content a server knows rides on gg_global as one class,
    /// latest-N (HudVote.LatestClass); from v4.6.0 on every content shows its own outdated banner for an N above its own. The version a player's game
    /// holds comes from the HUD check's click (HudCheck.VersionButton). hud/reference/ holds every shipped content's layout, which --selftest
    /// holds this one to.</summary>
    public const string Content = "v4.6.0";

    /// <summary>The layout as the client looks it up: the SOURCE path, extension included. Anything else spawns fine and shows nothing.</summary>
    public const string Layout = "panorama/layout/custom_game/gungame_hud_" + ReleaseFile + ".xml";

    /// <summary>The switch, on the cs2 service: the Workshop id of the published HUD addon. Empty (the default) draws no HUD - the plugin
    /// answers chat commands in chat and shows the small centre welcome panel, and pre.sh does not even load MultiAddonManager.</summary>
    public const string AddonVariable = "OS2_HUD_ADDON";

    /// <summary>The switch's old name, read only while AddonVariable is not set at all: a one-time fallback for a deployment that still says
    /// it the old way, to be removed in the next release.</summary>
    public const string OldAddonVariable = "GUNGAME_HUD_ADDON";

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

    /// <summary>The query the plugin adds to its GET of the live state: whether it draws the HUD (draw=1) or runs undrawn (draw=0, no addon). Only
    /// a drawing plugin makes the bridge open votes (the bridge's HudActive); a poll without it is an older plugin's, which counts as drawing.</summary>
    public const string DrawQuery = "draw";

    /// <summary>The actions a click may ask for without an argument. They are written like the chat commands, which is what the bridge and
    /// the plugin speak to each other on /act: a typed command arrives as the same click.</summary>
    public static readonly string[] Actions = ["!up", "!down", "!next", "!next random", "!previous", "!restart", "!time 10", "!time 60"];

    /// <summary>The "new maps in random picks" setting's old words: an older content's Game page still has its buttons, which hear that it is gone
    /// (the bridge's Selection.cs).</summary>
    public static readonly string[] NewMapsActions = ["!newmaps always", "!newmaps half", "!newmaps never"];

    /// <summary>Whether a click may ask for this. The bridge refuses anything else on its /act path, so the menu can never reach a server
    /// command, a name search or anything free-form: a map is named by its rank, an official map's name or a Workshop id.</summary>
    public static bool IsAction(string command)
    {
        if (Actions.Contains(command) || NewMapsActions.Contains(command)) return true;
        if (command.StartsWith("!hudok ", StringComparison.Ordinal)) return HudCheck.IsVersionWord(command[7..]); // the HUD check's click (HudCheck)
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

    public const int RowsPerGroup = 4, LinesPerRule = 4; // the layout has exactly this many slots

    /// <summary>The Help page: where each thing is in the menu. Command is the place to click, Words what it does; every action has its chat
    /// command too (ChatCommands.cs, !help).</summary>
    public static readonly Group[] Help =
    [
        new("Maps", [
            new("Maps  ·  Play", "Put any map to the vote: a vote opens, your vote on that map"),
            new("Search", "The keyboard on the Maps page: name, tags or a Workshop ID"),
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
            new("Fun", "Gravity, friendly fire, grenade drops, catch-up and more"),
            new("Server  ·  Game", "Match length: 10 or 60 minutes, from the next match"),
            new("Restart", "Server  ·  Game: reboot a stuck server - Click twice to confirm")]),
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
            "Click a card to vote, another to change it, your own to take it back. A map a player put on the ballot leaves it once it has no votes.",
            "A match end opens a vote by itself. After any vote, 2 minutes before the next one can be called. No votes: mid-match the map stays, at a match end Random."]),
        new("The ballot", [
            "Keep playing this map while the match is on, Play this map again after it, every map a player put on it, and one map of each other mode.",
            "Then this mode: a fresh map (a Workshop upload or update of the last 30 days), a surprise new to us, and favourites the server keeps fill the page.",
            "Wingman takes 4 players at most: with more on, no Wingman map is on it or can win. The most votes first; a second page only when needed.",
            "Every card but a player's pick is on the server's disk, so it loads at once. A risky map shows its record, and putting it to the vote takes a second click."]),
        new("The dice", [
            "Random is the dice after the vote. Like every card the dice pick, it is a map on the server's disk: a favourite it keeps, or one of a small stock new to us.",
            "1 random pick in 100 takes a too-big map (measured XL, or marked Too big by a player), the other 99 one that is not; an empty group hands the pick to the other.",
            "The dice change the map by themselves only at a server start, 10 minutes with nobody on, a broken map. Previous puts the map before this one to the vote."]),
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

/// <summary>The live state the bridge serves on /hud/&lt;secret&gt; and the plugin puts on screen: the map that is on and what picked it, the
/// likes on it, the most liked maps and a change counting down. A text format of tab-separated lines rather than JSON, so the native-AOT bridge
/// needs no serializer context for it and both ends share one tested codec. The vote's own lines follow it (VoteState, MapVote.cs).</summary>
public sealed class HudState
{
    public const string Magic = "gg-hud\t1";
    public const int ListLength = 5;    // the board and the live column have five rows for the vote's leading cards and five for the most liked maps

    public int Maps;                                    // playable maps on the list, as the help counts them
    public string Map = "", By = "", Size = "", Key = "", Status = "";
    public int Up, Down;                                // likes and dislikes on the map that is on
    public bool Loading;                                // a change went out and its map has not logged its start yet
    public Dictionary<long, int> MyVotes = new();       // account id -> its like (+1) or dislike (a reason's value) on the map that is on
    public List<(string Map, int Up, int Down)> Top = new();
    public string ChangeMap = "", ChangeBy = "";        // a change counting down...
    public int ChangeIn = -1;                           // ...and the seconds until it goes out; -1 when none is
    public int RoundMinutes;                            // the match length !time set
    public string RoundBy = "";                         // ...and who set it, while it is not the default
    public long CatalogVersion;                         // the map catalog's version: the plugin fetches it again when this moves
    public string CheckMode = "";                       // the HUD check as configured (HUD_CHECK: off, warn - "dryrun" on the wire - or enforce); empty from an older bridge
    public Dictionary<long, string> Verified = new();   // account id -> the HUD its game showed at the check's click since its last real join (HudCheck.VersionWord), players on the server only
    public string TabMode = "";                         // the TAB chord (HUD_TAB: off, menu, on); empty from an older bridge
    public string News = "";                            // the news line everybody sees (a pick, a skip vote, the queue moving, a crash)...
    public int NewsFor;                                 // ...and for how many more seconds
    public Dictionary<long, (string Text, int For)> Notes = new(); // account id -> the note only that player sees, and its seconds left
    public Dictionary<long, (string Map, int For)> Why = new();    // account id -> the map a Dislike's popup asks about, and its seconds left (Votes.cs)
    public string PrevLabel = "", PrevArg = "";         // the map before this one, for the Previous button
    public long SearchVersion;                          // the search index's version: the plugin fetches it again when this moves
    public string NewMaps = "";                         // the new-maps setting of an older bridge (always, half or never); the setting is gone, so empty
    public int Big;                                     // too-big votes on the map that is on (each is also one of its Down)
    public string SizeClass = "";                       // its measured class (S, M, L, XL), empty when unmeasured
    public int Ads;                                     // ads votes on the map that is on (each is also one of its Down)
    public long DashVersion;                            // the Sync page's block (HudDash): the plugin fetches it again when this moves
    public string RestartKind = "";                     // a restart coming (the bridge's Restarts.cs): game-update (a CS2 update), our-update, daily, downloads; "" none
    public string RestartWhen = "";                     // ...match-end (at the end of this match) or now
    public int RestartIn = -1;                          // ...its seconds left, -1 while it waits for the match's end
    public long RestartAt;                              // ...the moment it goes, unix seconds (0 while it waits for the match's end): the end time a countdown counts to
    public string RestartWords = "";                    // ...and the warning in words, as the news line says it
    public long BgId;                                   // the restart watcher's background download (the bridge's RandomQueue.cs): the map's Workshop id, 0 none
    public int BgPercent = -1;                          // ...its progress, 0-100, -1 before any
    public string BgTitle = "";                         // ...and its title
    public string TopText = "", TopCat = "";            // the top line (Messages.cs): the words and the category's id...
    public long TopAtMs, TopUntilMs;                    // ...since when and until when, unix milliseconds
    public string BanTone = "", BanHead = "", BanLine = ""; // the status banner: its tone (problem, warning, done, info), headline and line...
    public long BanUntilMs;                             // ...and until when, 0 while its state lasts
    public Dictionary<long, string> NoteCats = new();   // account id -> the category of that player's note
    public List<Messages.LatelyLine> Lately = new();    // the board's Lately lines, oldest first
    public List<(long Seq, long Account, string Text)> Says = new(); // chat lines, each said once by its number (account 0: everybody); the text carries its marks (M)
    public List<(long Seq, long Account, bool Alert, long UntilMs, string Text)> Centres = new(); // CS2's plain centre text and alert, held until then
    public string CrashNote = "";                       // a crash within the last minutes, as the welcome page says it
    public string ChangeKey = "";                       // the key of the map a change counts down to: its picture
    public string RestartBy = "";                       // the player who pressed a restart counting down
    public string RestartAfter = "";                    // what players do afterwards, as the countdown's second line says it
    public long RematchAtMs;                            // Restart match counting down: the moment it goes...
    public string RematchBy = "";                       // ...and who pressed it
    public int RematchIn = -1;                          // ...its seconds left, -1 when none is (worked out by AsOf)

    /// <summary>The end times behind the seconds above, unix milliseconds (0: none). The bridge's change-driven text (EndTimes: true, HudTransport.cs)
    /// carries these instead of the seconds, which would change it every second; the plugin counts down from them itself (AsOf).</summary>
    public long ChangeAtMs, NewsUntilMs;
    public Dictionary<long, long> NoteUntilMs = new(), WhyUntilMs = new();

    /// <summary>Unix milliseconds of a moment, as the end times carry it.</summary>
    public static long Ms(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>The state as it stands at this moment (unix milliseconds on the bridge's clock): every countdown worked out from its end time, what has
    /// run out gone. A text without end times (an older bridge) is as it came.</summary>
    public HudState AsOf(long nowMs)
    {
        var h = (HudState)MemberwiseClone();
        int Secs(long until) => (int)Math.Ceiling((until - nowMs) / 1000.0);
        if (ChangeAtMs > 0) h.ChangeIn = Math.Max(0, Secs(ChangeAtMs));
        if (NewsUntilMs > 0 && Secs(NewsUntilMs) is var news) (h.News, h.NewsFor) = news > 0 ? (News, news) : ("", 0);
        h.Notes = new(Notes);
        h.NoteCats = new(NoteCats);
        foreach (var (account, until) in NoteUntilMs)
            if (Secs(until) is > 0 and var left && Notes.TryGetValue(account, out var note)) h.Notes[account] = (note.Text, left); else h.Notes.Remove(account);
        h.Why = new(Why);
        foreach (var (account, until) in WhyUntilMs)
            if (Secs(until) is > 0 and var left && Why.TryGetValue(account, out var why)) h.Why[account] = (why.Map, left); else h.Why.Remove(account);
        if (RestartAt > 0) h.RestartIn = Math.Max(0, Secs(RestartAt * 1000));
        if (TopUntilMs > 0 && nowMs >= TopUntilMs) (h.TopText, h.TopCat, h.TopAtMs, h.TopUntilMs) = ("", "", 0, 0);
        if (BanUntilMs > 0 && nowMs >= BanUntilMs) (h.BanTone, h.BanHead, h.BanLine, h.BanUntilMs) = ("", "", "", 0);
        h.RematchIn = RematchAtMs > 0 ? Math.Max(0, Secs(RematchAtMs)) : -1;
        (h.News, h.RestartWords) = (h.FillLeft(h.News), h.FillLeft(h.RestartWords));
        return h;
    }

    /// <summary>A restart's words with its countdown filled in from RestartIn: in so long, or in a moment once it is due.</summary>
    public string FillLeft(string words) => !words.Contains(Hud.LeftToken, StringComparison.Ordinal) ? words
        : words.Replace(Hud.LeftToken, RestartIn > 0 ? MapVote.Left(RestartIn) : "a moment", StringComparison.Ordinal);

    public string Encode(bool endTimes = false)
    {
        var (news, restartWords) = endTimes ? (News, RestartWords) : (FillLeft(News), FillLeft(RestartWords)); // counted down by the HUD itself, or filled in now
        var s = new StringBuilder(Magic).Append('\n');
        void Line(params object[] fields) => s.Append(string.Join('\t', fields.Select(f => f is string t ? Hud.Clean(t) : Convert.ToString(f, CultureInfo.InvariantCulture)))).Append('\n');
        Line("maps", Maps);
        Line("now", Map, By, Size, Key, Up, Down, Status);
        if (Loading) Line("loading");
        foreach (var (id, v) in MyVotes) Line("vote", id, v);
        foreach (var (map, up, down) in Top.Take(ListLength)) Line("top", map, up, down);
        if (ChangeIn >= 0 && endTimes) Line("changeat", ChangeMap, ChangeBy, ChangeAtMs);
        else if (ChangeIn >= 0) Line("change", ChangeMap, ChangeBy, ChangeIn);
        if (RoundMinutes > 0) Line("set", RoundMinutes, RoundBy); // an older plugin reads the first field
        if (CatalogVersion > 0) Line("cat", CatalogVersion);
        if (CheckMode != "") Line("chk", CheckMode);
        foreach (var (account, version) in Verified) Line("ok", account, version);
        if (TabMode != "") Line("tab", TabMode);
        if (news != "" && NewsFor > 0)
            s.Append(endTimes ? "newsat\t" : "news\t").Append((endTimes ? NewsUntilMs : NewsFor).ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Hud.Clean(news, 240)).Append('\n');
        foreach (var (account, (text, secs)) in Notes)
            if (text != "" && secs > 0)
                s.Append(endTimes ? "noteat\t" : "note\t").Append(account.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append((endTimes ? NoteUntilMs.GetValueOrDefault(account) : secs).ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Hud.Clean(text, 140)).Append('\n');
        foreach (var (account, (map, secs)) in Why)
            if (secs > 0) Line(endTimes ? "whyat" : "why", account, endTimes ? WhyUntilMs.GetValueOrDefault(account) : secs, map); // an older plugin skips it
        if (PrevArg != "") Line("prev", PrevArg, PrevLabel);
        if (SearchVersion > 0) Line("srch", SearchVersion);
        if (NewMaps != "") Line("newmaps", NewMaps);         // an older plugin skips the lines it does not know
        if (Big > 0 || SizeClass != "") Line("big", Big, SizeClass);
        if (Ads > 0) Line("ads", Ads);                     // an older plugin skips it
        if (DashVersion > 0) Line("dash", DashVersion);    // an older plugin skips it
        if (RestartKind != "") s.Append("rst\t").Append(Hud.Clean(RestartKind, 12)).Append('\t').Append(Hud.Clean(RestartWhen, 12)).Append('\t')
            .Append((endTimes && RestartAt > 0 ? 0 : RestartIn).ToString(CultureInfo.InvariantCulture)).Append('\t').Append(RestartAt.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Hud.Clean(restartWords, 240)).Append('\n');
        if (BgId > 0) Line("bgdl", BgId, BgPercent, BgTitle); // an older plugin skips these two and everything after them
        void Raw(string name, params object[] fields) => s.Append(name).Append('\t').Append(string.Join('\t', fields.Select(f => f is string t ? t : Convert.ToString(f, CultureInfo.InvariantCulture)))).Append('\n');
        if (TopText != "") Raw("topl", TopAtMs, TopUntilMs, Hud.Clean(TopCat, 12), Hud.Clean(TopText, Messages.TopMax));
        if (BanHead != "") Raw("ban", Hud.Clean(BanTone, 12), BanUntilMs, Hud.Clean(BanHead, 80), Hud.Clean(BanLine, Messages.TopMax));
        foreach (var (account, cat) in NoteCats) if (Notes.ContainsKey(account)) Raw("notec", account, Hud.Clean(cat, 12));
        foreach (var l in Lately) Raw("late", l.AtMs, l.Account, Hud.Clean(l.Key, 40), Hud.Clean(l.Text, Messages.TopMax));
        foreach (var (seq, account, text) in Says) Raw("say", seq, account, Hud.Clean(text, 400));
        foreach (var (seq, account, alert, until, text) in Centres) Raw("ctr", seq, account, alert ? 1 : 0, until, Hud.Clean(text, Messages.CentreMax));
        if (CrashNote != "") Raw("crashn", Hud.Clean(CrashNote, Messages.TopMax));
        if (ChangeKey != "") Raw("chgkey", Hud.Clean(ChangeKey, 40));
        if (RestartKind != "") Raw("rstby", Hud.Clean(RestartBy, 32), Hud.Clean(RestartAfter, 80));
        if (RematchAtMs > 0) Raw("rematch", RematchAtMs, Hud.Clean(RematchBy, 32));
        return s.ToString();
    }

    /// <summary>The state out of Encode's text, or null when it is not one (an old bridge, an error page). Unknown lines are skipped,
    /// so a newer bridge can add some without breaking an older plugin.</summary>
    public static HudState? Decode(string? text)
    {
        if (text == null || !text.StartsWith(Magic)) return null;
        var s = new HudState();
        static int Int(string[] f, int i) => i < f.Length && int.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        static long Long(string[] f, int i) => i < f.Length && long.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
        static string Str(string[] f, int i) => i < f.Length ? f[i] : "";
        foreach (var raw in text.Split('\n').Skip(1))
        {
            var f = raw.TrimEnd('\r').Split('\t');
            switch (f[0])
            {
                case "maps": s.Maps = Int(f, 1); break;
                case "now": (s.Map, s.By, s.Size, s.Key, s.Up, s.Down, s.Status) = (Str(f, 1), Str(f, 2), Str(f, 3), Str(f, 4), Int(f, 5), Int(f, 6), Str(f, 7)); break;
                case "loading": s.Loading = true; break;
                case "vote": if (Long(f, 1) is >= 0 and var id) s.MyVotes[id] = HudCatalog.Carried(Int(f, 2)); break;
                case "top": if (s.Top.Count < ListLength) s.Top.Add((Str(f, 1), Int(f, 2), Int(f, 3))); break;
                case "change": (s.ChangeMap, s.ChangeBy, s.ChangeIn) = (Str(f, 1), Str(f, 2), Int(f, 3)); break;
                case "changeat" when Long(f, 3) > 0: (s.ChangeMap, s.ChangeBy, s.ChangeIn, s.ChangeAtMs) = (Str(f, 1), Str(f, 2), 0, Long(f, 3)); break;
                case "newsat" when Long(f, 1) > 0: (s.NewsFor, s.News, s.NewsUntilMs) = (1, Str(f, 2), Long(f, 1)); break;
                case "noteat" when Long(f, 1) >= 0 && Long(f, 2) > 0: s.Notes[Long(f, 1)] = (Str(f, 3), 1); s.NoteUntilMs[Long(f, 1)] = Long(f, 2); break;
                case "whyat" when Long(f, 1) >= 0 && Long(f, 2) > 0: s.Why[Long(f, 1)] = (Str(f, 3), 1); s.WhyUntilMs[Long(f, 1)] = Long(f, 2); break;
                case "set": (s.RoundMinutes, s.RoundBy) = (Int(f, 1), Str(f, 2)); break;
                case "cat": s.CatalogVersion = Long(f, 1); break;
                case "chk": s.CheckMode = Str(f, 1); break;
                case "tab": s.TabMode = Str(f, 1); break;
                case "news": (s.NewsFor, s.News) = (Int(f, 1), Str(f, 2)); break;
                case "note" when Long(f, 1) >= 0 && Int(f, 2) > 0: s.Notes[Long(f, 1)] = (Str(f, 3), Int(f, 2)); break;
                case "why" when Long(f, 1) >= 0 && Int(f, 2) > 0: s.Why[Long(f, 1)] = (Str(f, 3), Int(f, 2)); break;
                case "prev": (s.PrevArg, s.PrevLabel) = (Str(f, 1), Str(f, 2)); break;
                case "srch": s.SearchVersion = Long(f, 1); break;
                case "newmaps": s.NewMaps = Str(f, 1); break;
                case "big": (s.Big, s.SizeClass) = (Int(f, 1), Str(f, 2)); break;
                case "ads": s.Ads = Int(f, 1); break;
                case "dash": s.DashVersion = Long(f, 1); break;
                case "rst" when Str(f, 1) != "": (s.RestartKind, s.RestartWhen, s.RestartIn, s.RestartAt, s.RestartWords) = (Str(f, 1), Str(f, 2), f.Length > 3 && int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ri) ? ri : -1, Math.Max(0, Long(f, 4)), Str(f, 5)); break;
                case "bgdl" when Long(f, 1) > 0: (s.BgId, s.BgPercent, s.BgTitle) = (Long(f, 1), f.Length > 2 && int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bp) ? Math.Clamp(bp, -1, 100) : -1, Str(f, 3)); break;
                case "ok" when Long(f, 1) >= 0 && HudCheck.IsVersionWord(Str(f, 2)): s.Verified[Long(f, 1)] = HudCheck.Normal(Str(f, 2)); break;
                case "topl" when Str(f, 4) != "": (s.TopAtMs, s.TopUntilMs, s.TopCat, s.TopText) = (Long(f, 1), Long(f, 2), Str(f, 3), Str(f, 4)); break;
                case "ban" when Str(f, 3) != "": (s.BanTone, s.BanUntilMs, s.BanHead, s.BanLine) = (Str(f, 1), Math.Max(0, Long(f, 2)), Str(f, 3), Str(f, 4)); break;
                case "notec" when Long(f, 1) >= 0: s.NoteCats[Long(f, 1)] = Str(f, 2); break;
                case "late" when Str(f, 4) != "": s.Lately.Add(new Messages.LatelyLine(Long(f, 1), Math.Max(0, Long(f, 2)), Str(f, 3), Str(f, 4))); break;
                case "say" when Long(f, 1) > 0: s.Says.Add((Long(f, 1), Math.Max(0, Long(f, 2)), Str(f, 3))); break;
                case "ctr" when Long(f, 1) > 0: s.Centres.Add((Long(f, 1), Math.Max(0, Long(f, 2)), Int(f, 3) == 1, Long(f, 4), Str(f, 5))); break;
                case "crashn": s.CrashNote = Str(f, 1); break;
                case "chgkey": s.ChangeKey = Str(f, 1); break;
                case "rstby": (s.RestartBy, s.RestartAfter) = (Str(f, 1), Str(f, 2)); break;
                case "rematch" when Long(f, 1) > 0: (s.RematchAtMs, s.RematchBy) = (Long(f, 1), Str(f, 2)); break;
            }
        }
        return s;
    }
}

/// <summary>What goes on screen: label texts by panel id and classes by panel id, out of a HudState (and the vote's VoteState). Global ones
/// are the same for everybody and are set once for all players; per-player ones (your like, your note) are set per player. The plugin only
/// sends what changed since the last time.</summary>
public static class HudView
{
    /// <summary>Panel ids the plugin addresses. The layout's root may not carry an id, so global classes go on gg_global and a player's
    /// own state (window open, page, toast, banner) on gg_screen inside it.</summary>
    public const string Global = "gg_global", Screen = "gg_screen";

    public const string ButtonClose = "gg_btn_close", ButtonUp = "gg_btn_up", ButtonDown = "gg_btn_down", ButtonAct = "gg_btn_act";

    /// <summary>The board's buttons (TAB): your like on the map that is on, Start a vote, and the full menu. BoardBig, the too-big mark, is
    /// content v3.1.0 (since v4.4.0 a Dislike's reason; the button stepped aside).</summary>
    public const string BoardUp = "gg_b_vup", BoardDown = "gg_b_vdown", BoardNext = "gg_b_next", BoardOpen = "gg_b_open", BoardBig = "gg_b_vbig",
        BoardAds = "gg_b_vads"; // content v3.2.0

    public const string TabHow = "gg_tab_how", TabFun = "gg_tab_fun", TabGame = "gg_tab_game", TabMaps = "gg_tab_maps";

    /// <summary>The window's tabs and sub-tabs: START, MAPS (Browse and, while a vote is on, Vote - the vote's own buttons, HudVote.BackButton and
    /// BrowseButton), FUN, SERVER (Players, Game, Sync) and HELP (Help, How it works). A main tab opens its first page; a sub-tab its own. The
    /// tabs of the contents before v4.6.0 keep working under their ids - Start, Maps, Game, Fun, Help, How it works, Sync - and an older content's
    /// Rules tab opens How it works, where its words went; its Queue tab does nothing (there is no queue).</summary>
    public static readonly (string Id, HudSlots.Page Page)[] Tabs =
        [("gg_tab_start", HudSlots.Page.Start), (TabMaps, HudSlots.Page.Maps), (TabGame, HudSlots.Page.Game), ("gg_tab_help", HudSlots.Page.Help),
         (TabHow, HudSlots.Page.How), (TabFun, HudSlots.Page.Fun), (HudDash.Tab, HudSlots.Page.Sync), ("gg_tab_server", HudSlots.Page.Players),
         ("gg_sub_players", HudSlots.Page.Players), ("gg_sub_help", HudSlots.Page.Help), ("gg_tab_rules", HudSlots.Page.How)];

    /// <summary>The page a tab or sub-tab opens, or null for any other button.</summary>
    public static HudSlots.Page? TabPage(string buttonId)
    {
        foreach (var (id, page) in Tabs) if (id == buttonId) return page;
        return null;
    }

    /// <summary>The caption beside the sub-tabs, per page (a variable on gg_screen, SubCaptionVar): what the page is for; the Maps page's says
    /// whether a Play adds to the vote that is on.</summary>
    public const string SubCaptionVar = "ossub";

    public static string SubCaption(HudSlots.Page page, bool ballot, bool browsingVote) => page switch
    {
        HudSlots.Page.Start => "",
        HudSlots.Page.Maps => ballot ? "Click a map to vote, yours again to take it back" : browsingVote ? "Browse: Play puts a map on this vote, with your vote"
            : "Every map: Play puts it to the vote",
        HudSlots.Page.Fun => "Anybody, at any time, for everybody",
        HudSlots.Page.Players => "Who is on, and which HUD their game shows",
        HudSlots.Page.Game => "The match length and the stuck-server restart",
        HudSlots.Page.Sync => "The Workshop sync: what it does now, its queue, its history",
        HudSlots.Page.Help => "Where everything is",
        _ => "Every rule and chance of a map pick",
    };

    /// <summary>The words that never change: help, how it works, the start page. Sent once per layout entity (they are global too).</summary>
    public static List<(string Id, string Text)> StaticTexts()
    {
        var t = new List<(string, string)>
        {
            ("gg_w_headline", Hud.Headline), ("gg_w_hint", Hud.MenuHint),
            ("gg_b_cmds", HudTab.BoardLine(HudTab.Mode.Off)),
        };
        for (var i = 0; i < Hud.Steps.Length; i++)
        {
            t.Add(($"gg_s{i + 1}_title", Hud.Steps[i].Title));
            t.Add(($"gg_s{i + 1}_cmd", Hud.Steps[i].Command));
            t.Add(($"gg_s{i + 1}_txt", Hud.Steps[i].Words));
        }
        for (var g = 0; g < Hud.Help.Length; g++)
        {
            t.Add(($"gg_c{g + 1}_name", Hud.Help[g].Name));
            for (var r = 0; r < Hud.RowsPerGroup; r++)
            {
                var row = r < Hud.Help[g].Rows.Length ? Hud.Help[g].Rows[r] : null;
                t.Add(($"gg_c{g + 1}_{r + 1}_cmd", row?.Command ?? ""));
                t.Add(($"gg_c{g + 1}_{r + 1}_txt", row == null ? "" : row.Also == "" ? row.Words : $"{row.Words}  ·  {row.Also}"));
            }
        }
        for (var s = 0; s < Hud.HowSections; s++)
        {
            var how = s < Hud.How.Length ? Hud.How[s] : null;
            t.Add(($"gg_h{s + 1}_title", how?.Title ?? ""));
            for (var l = 0; l < Hud.HowLines; l++) t.Add(($"gg_h{s + 1}_{l + 1}_txt", how != null && l < how.Lines.Length ? how.Lines[l] : ""));
        }
        t.Add((HudMenu.FunNote, HudMenu.FunNoteText));
        t.Add(("gg_w_back_eyebrow", "Now playing"));
        t.Add(("gg_w_back_hint", LikeHint));
        return t;
    }

    /// <summary>Rows of the help and how-it-works pages that hold something: the layout collapses the rest. And c31 and c32 on gg_screen, for
    /// everybody: the contents before v4.6.0 show their How it works, Fun and Help rows only under them, and every content in use holds those panels.</summary>
    public static List<(string Id, string Class, bool On)> StaticClasses()
    {
        var c = new List<(string, string, bool)> { (Screen, "c31", true), (Screen, "c32", true) };
        for (var g = 0; g < Hud.Help.Length; g++)
            for (var r = 0; r < Hud.RowsPerGroup; r++) c.Add(($"gg_c{g + 1}_{r + 1}", "used", r < Hud.Help[g].Rows.Length));
        for (var s = 0; s < Hud.HowSections; s++)
            for (var l = 0; l < Hud.HowLines; l++) c.Add(($"gg_h{s + 1}_{l + 1}", "used", s < Hud.How.Length && l < Hud.How[s].Lines.Length));
        return c;
    }

    /// <summary>The classes the plugin sends only for the contents before v4.6.0, which this content's stylesheet has no rule for: c31 and c32
    /// (StaticClasses), the join banner's vjoin (beside the version classes, HudVote.VersionClasses), and the marks that light an older card's Too big
    /// and Ads buttons (bigvoted, adsvoted: a Dislike's reasons now).</summary>
    public static readonly string[] OlderContentClasses = ["c31", "c32", "bigvoted", "adsvoted", "news", "note", "nd-on", "nd-off", "cu-on", "cu-off"];

    /// <summary>The panels only the contents before v4.6.0 have: their news line, their note, their lede.</summary>
    public static readonly string[] OlderContentTexts = ["gg_news_text", "gg_note_text", "gg_w_lede"];

    public const string LikeHint = "Like it? Hold TAB and click Like or Dislike.";

    /// <summary>The board's one line about the vote (its old queue line), about this many characters at the most (the mock review).</summary>
    public const int QueueLineMax = 52;

    /// <summary>The board's vote line: the vote that is on, its result, the wait before the next one, or how to call one.</summary>
    public static string VoteLine(VoteState? v) => v == null || v.Round <= 0
        ? (v?.WaitFor > 0 ? $"The next vote can start in {MapVote.Left(v.WaitFor)}" : "No vote on: Play on a map, or Start a vote")
        : v.Open ? $"The vote is on: {v.SecondsLeft} s left  ·  {v.Cast} of {v.Present} voted"
        : v.Result is { } r ? Hud.Clean($"The vote chose {r.Label}", QueueLineMax) : "The vote is over";

    /// <summary>The live texts, the same for everybody. The board (b) and the window's live column (w) show the same facts; the column also lists
    /// the vote's leading cards (its old queue rows) and the most liked maps, the board has the one vote line.</summary>
    public static List<(string Id, string Text)> Texts(HudState s, VoteState? v = null)
    {
        var map = s.Map == "" ? "Waiting for the server" : s.Map;
        var by = Hud.ByWords(s.By);
        var byline = string.Join("  ·  ", new[] { by, s.Size }.Where(x => x != ""));
        var status = s.Loading ? "Loading - Everyone reconnects in a moment" : s.Status;
        var t = new List<(string, string)>();
        foreach (var p in new[] { "b", "w" })
        {
            t.Add(($"gg_{p}_map", map));
            t.Add(($"gg_{p}_by", byline));
            t.Add(($"gg_{p}_up", s.Up.ToString(CultureInfo.InvariantCulture)));
            t.Add(($"gg_{p}_down", s.Down.ToString(CultureInfo.InvariantCulture)));
            t.Add(($"gg_{p}_status", status));
        }
        var leading = Leading(v);
        for (var i = 0; i < HudState.ListLength; i++)
        {
            var n = i < leading.Count ? leading[i] : null;
            t.Add(($"gg_w_n{i + 1}", n?.Label ?? ""));
            t.Add(($"gg_w_nw{i + 1}", n == null ? "" : $"{n.Votes} vote{(n.Votes == 1 ? "" : "s")}"));
            var top = i < s.Top.Count ? s.Top[i] : ("", 0, 0);
            t.Add(($"gg_w_t{i + 1}", top.Item1));
            t.Add(($"gg_w_ts{i + 1}", top.Item1 == "" ? "" : $"+{top.Item2}{(top.Item3 > 0 ? $" -{top.Item3}" : "")}"));
        }
        t.Add(("gg_w_nempty", v is { Round: > 0 } ? "No votes yet: click a card on the Maps page." : "No vote on. Play on a map puts it to the vote."));
        t.Add(("gg_w_tempty", "No likes yet. Like a map: its card's up arrow."));
        t.Add(("gg_b_queue", VoteLine(v)));
        t.Add(("gg_b_big", s.Big.ToString(CultureInfo.InvariantCulture))); // the too-big marks on the map that is on (the board's hidden row)
        t.Add(("gg_b_ads", s.Ads.ToString(CultureInfo.InvariantCulture))); // ...and the ads marks
        t.Add(("gg_rst_text", s.RestartKind == "" ? "" : s.RestartWords)); // a restart coming: its words in the window's foot (its countdown: RestartCountdown)
        t.Add(("gg_news_text", s.NewsFor > 0 ? s.News : ""));
        t.Add(("gg_b_count", s.Maps > 0 ? $"{s.Maps} maps" : ""));
        t.Add(("gg_w_lede", ""));
        t.Add(("gg_w_back", string.Join("  ·  ", new[] { map, byline, Hud.Tally(s.Up, s.Down) }.Where(x => x != ""))));
        t.Add(("gg_w_crash", s.CrashNote));
        t.Add(("gg_top_tab", Messages.CategoryOf(s.TopCat)?.Name ?? ""));
        t.Add(("gg_top_text", s.TopText));
        t.Add(("gg_ban_head", s.BanHead));
        t.Add(("gg_ban_line", s.BanLine));
        t.Add(("gg_w_maps", s.Maps > 0 ? $"{s.Maps} Workshop maps" : "Workshop maps"));
        var countdown = CountdownOf(s);
        t.Add(("gg_k_eyebrow", countdown?.Eyebrow ?? ""));
        t.Add(("gg_k_map", countdown?.Title ?? ""));
        t.Add(("gg_k_by", countdown?.Line ?? ""));
        t.Add(("gg_k_secs", countdown == null ? "" : Countdown(countdown.Value.Seconds)));
        t.Add(("gg_t_map", map));
        t.Add(("gg_t_by", byline == "" ? Hud.Tally(s.Up, s.Down) : $"{byline}  ·  {Hud.Tally(s.Up, s.Down)}"));
        t.Add(("gg_t_hint", LikeHint));
        return t;
    }

    /// <summary>The vote's cards that have votes, the most first: the live column's rows while a vote is on.</summary>
    static List<VoteState.Option> Leading(VoteState? v) => v is not { Round: > 0 } ? [] : v.Options.Where(o => o.Votes > 0).OrderByDescending(o => o.Votes).Take(HudState.ListLength).ToList();

    /// <summary>The live classes, the same for everybody: which list rows are in use, an empty vote or top list, a change counting down.</summary>
    public static List<(string Id, string Class, bool On)> Classes(HudState s, VoteState? v = null)
    {
        var c = new List<(string, string, bool)>
        {
            (Global, "ready", true), (Global, "changing", CountdownOf(s) != null), (Global, "cd-logo", CountdownOf(s)?.Logo == true), (Global, "loading", s.Loading),
            (Global, "news", s.NewsFor > 0 && s.News != ""), (Global, "rst", s.RestartKind != ""), (Global, "top", s.TopText != ""), (Global, "ban", s.BanHead != ""), (Global, "crash", s.CrashNote != ""),
        };
        foreach (var cat in Messages.Categories) c.Add((TopPanel, cat.Class, cat.Id == s.TopCat));
        foreach (var tone in Tones) c.Add((BannerPanel, "tone-" + tone, tone == s.BanTone));
        var leading = Leading(v).Count;
        for (var i = 0; i < HudState.ListLength; i++)
        {
            c.Add(($"gg_w_row_n{i + 1}", "used", i < leading));
            c.Add(($"gg_w_row_t{i + 1}", "used", i < s.Top.Count));
        }
        c.Add(("gg_w_nempty", "used", leading == 0));
        c.Add(("gg_w_tempty", "used", s.Top.Count == 0));
        return c;
    }

    public const string TopPanel = "gg_top", BannerPanel = "gg_ban", LinePanel = "gg_wl";
    public static readonly string[] Tones = ["problem", "warning", "done", "info"];

    /// <summary>What the countdown banner shows: a map change, Restart match or a restart counting down. Logo: the project's logo in the picture's place.</summary>
    public readonly record struct CountdownWords(string Eyebrow, string Title, string Line, bool Logo, int Seconds);

    public static CountdownWords? CountdownOf(HudState s)
    {
        if (s.ChangeIn >= 0) return new("Map change", s.ChangeMap, Hud.ByWords(s.ChangeBy), false, s.ChangeIn);
        if (s.RematchIn >= 0) return new("Match", $"{Hud.Clean(s.RematchBy, 32)} is restarting the match", "The ladder, the time and catch-up start over", true, s.RematchIn);
        if (s.RestartKind != "" && s.RestartWhen == "now" && s.RestartIn >= 0)
            return new("Server", s.RestartBy != "" ? $"{Hud.Clean(s.RestartBy, 32)} is restarting the server" : $"The server restarts in {MapVote.Left(s.RestartIn)}", s.RestartAfter, true, s.RestartIn);
        return null;
    }

    /// <summary>The seconds on the map-change banner, recomputed by the plugin every second from the deadline it took off the state.</summary>
    public static string Countdown(int seconds) => seconds <= 0 ? "now" : seconds.ToString(CultureInfo.InvariantCulture);

    /// <summary>A restart's countdown beside its words in the window's foot, from its end time by the plugin's own clock (0:25), or what it waits
    /// for; "" without one.</summary>
    public static string RestartCountdown(HudState s, DateTime now)
    {
        if (s.RestartKind == "") return "";
        if (s.RestartAt <= 0) return "after this match";
        var left = Math.Max(0, (int)Math.Ceiling((DateTimeOffset.FromUnixTimeSeconds(s.RestartAt).UtcDateTime - now).TotalSeconds));
        return left <= 0 ? "now" : $"{left / 60}:{left % 60:00}";
    }

    /// <summary>One row of the Players page (SERVER, Players): a player on the server, their team, the HUD their game showed at the HUD check's
    /// click (HudCheck.IsVersionWord: a content, older, or Hud.Release for a HUD whose content is not known; "" not seen yet), the check's seconds
    /// left while it waits for that click, their vote while one is open, and how long they have been on.</summary>
    public sealed record PlayerRow(string Name, string Team, string Version, int CheckSeconds, string Vote, string OnFor);

    public const int PlayerRows = 16;
    public const string PlayersVar = "ospl", PlayersSummaryVar = "ospsum";

    /// <summary>The Players page's words, as variables on gg_global that the page's labels inherit: osplNa the name, osplNb the team, osplNc the HUD
    /// when it is the server's own, osplNo an older or unknown one, osplNk the HUD check's clock, osplNd the vote, osplNe how long on; ospsum the
    /// line over them. The rows the list does not reach are empty.</summary>
    public static List<(string Name, string Text)> PlayersPage(IReadOnlyList<PlayerRow> rows)
    {
        var w = new List<(string, string)>();
        static string Var(int k, char f) => $"{PlayersVar}{k.ToString(CultureInfo.InvariantCulture)}{f}";
        var seen = rows.Where(r => r.CheckSeconds <= 0).Select(r => r.Version).ToList();
        var current = seen.Count(v => v == Hud.Content);
        var older = seen.Count(HudCheck.IsOutdated);
        var newer = seen.Count(v => HudCheck.LevelOf(v) > HudVote.ContentLevel);
        var unknown = seen.Count(v => v == Hud.Release);
        var unseen = rows.Count - current - older - newer - unknown;
        w.Add((PlayersSummaryVar, rows.Count == 0 ? "Nobody on the server" : string.Join("  ·  ", new[]
        {
            $"{rows.Count} on the server", current > 0 ? $"{current} on the server's HUD, {Hud.Content}" : "", older > 0 ? $"{older} older" : "",
            newer > 0 ? $"{newer} newer" : "", unknown > 0 ? $"{unknown} unknown" : "", unseen > 0 ? $"{unseen} not seen yet" : "",
            rows.Count > PlayerRows ? $"the first {PlayerRows} here" : "",
        }.Where(x => x != ""))));
        for (var k = 1; k <= PlayerRows; k++)
        {
            var r = k <= rows.Count ? rows[k - 1] : null;
            var checking = r is { CheckSeconds: > 0 };
            var mine = r != null && !checking && r.Version == Hud.Content;
            w.Add((Var(k, 'a'), r == null ? "" : Hud.Clean(r.Name, 32)));
            w.Add((Var(k, 'b'), r?.Team ?? ""));
            w.Add((Var(k, 'c'), mine ? r!.Version : ""));
            w.Add((Var(k, 'o'), r == null || mine || checking ? "" : HudCheck.VersionWords(r.Version)));
            w.Add((Var(k, 'k'), checking ? $"HUD check: {r!.CheckSeconds} s left" : ""));
            w.Add((Var(k, 'd'), r?.Vote ?? ""));
            w.Add((Var(k, 'e'), r?.OnFor ?? ""));
        }
        return w;
    }

    /// <summary>How long a player has been on since their last real join, as the Players page says it: "42 min", "1 h 05", "-" when not known.</summary>
    public static string OnFor(TimeSpan? since) => since is not { } d ? "-" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min" : $"{(int)d.TotalHours} h {d.Minutes:00}";

    /// <summary>What the contextual button asks for while the vote is open: the plugin opens that player's window on it (nothing goes to the bridge).</summary>
    public const string OpenVote = "!vote";

    /// <summary>The button under the likes (the board's, and the window's live column's): Start a vote, Keep playing on it; the vote
    /// open - open it; the wait after a vote - when the next can start; a change under way - nothing. Command is what a click makes the player say,
    /// null for nothing, OpenVote for the window.</summary>
    public static (string Text, string Sub, string? Command) Action(HudState s, VoteState? v = null)
    {
        if (s.ChangeIn >= 0 || s.Loading) return ("Map change under way", "Wait for the next map", null);
        if (v is { Open: true, Round: > 0 }) return ("The vote is on: open it", "Pick the next map, or keep playing", OpenVote);
        if (v?.WaitFor > 0) return ("Start a vote", $"The next one can start in {MapVote.Left(v.WaitFor)}", "!next");
        return ("Start a vote", "For the next map - Keep playing is on it", "!next");
    }

    /// <summary>The note this player sees now, if any: the bridge's answer to what they clicked.</summary>
    public static string NoteOf(HudState s, long account) => s.Notes.TryGetValue(account, out var n) && n.For > 0 ? n.Text : "";

    /// <summary>Per-player texts: the greeting, the toast's first line, your own like, the button under it (in the window and on the board) and
    /// your note.</summary>
    public static List<(string Id, string Text)> PlayerTexts(HudState s, long account, string name, bool welcomeBack, VoteState? v = null)
    {
        var mine = s.MyVotes.GetValueOrDefault(account);
        var (text, sub, _) = Action(s, v);
        var note = NoteOf(s, account);
        return
        [
            ("gg_w_hello", welcomeBack ? $"Welcome back, {Hud.Clean(name, 32)}" : $"Welcome, {Hud.Clean(name, 32)}"),
            ("gg_t_eyebrow", "Now playing"),
            ("gg_wl_eyebrow", note == "" ? "" : Messages.CategoryOf(s.NoteCats.GetValueOrDefault(account, ""))?.Name ?? Messages.You.Name),
            ("gg_wl_text", note),
            ("gg_b_you", mine > 0 ? "You liked this map" : mine == HudCatalog.TooBigVote ? "You marked this map too big" : mine == HudCatalog.AdsVote ? "You marked this map: ads"
                : mine < DownReasons.Plain && DownReasons.Of(mine) is { } why ? $"You disliked this map: {why.Short}" : mine < 0 ? "You disliked this map" : "Like it or not?"),
            ("gg_btn_act_text", text),
            ("gg_btn_act_sub", sub),
            ("gg_b_next_text", text),
            ("gg_b_next_sub", sub),
            ("gg_note_text", NoteOf(s, account)),
        ];
    }

    public static List<(string Id, string Class, bool On)> PlayerClasses(HudState s, long account, VoteState? v = null)
    {
        var mine = s.MyVotes.GetValueOrDefault(account);
        var (_, _, command) = Action(s, v);
        if (v?.WaitFor > 0 && v is not { Round: > 0 }) command = null; // the wait after a vote: it looks off, and a click still gets its answer with the time left
        var asked = s.Why.TryGetValue(account, out var why) ? why.For : 0; // the popup (content v4.1.0): classes on gg_screen, for which an older game has no rule; the drain line a second in, so a second Dislike starts it again
        var cat = s.NoteCats.GetValueOrDefault(account, Messages.You.Id);
        var note = NoteOf(s, account) != "";
        List<(string Id, string Class, bool On)> c = [(Screen, "voted-up", mine > 0), (Screen, "voted-down", mine < 0), (Screen, "act-off", command == null),
            (Screen, "note", note), (Screen, "wl", note), (Screen, "voted-big", mine == HudCatalog.TooBigVote), (Screen, "voted-ads", mine == HudCatalog.AdsVote),
            (Screen, "why", asked > 0), (Screen, "why-run", asked is > 0 and < (int)DownReasons.PopupSeconds)];
        foreach (var k in Messages.Categories) c.Add((LinePanel, k.Class, note && k.Id == cat));
        return c;
    }

    /// <summary>The board's Lately rows for one player: the three most recent lines, with how long ago (Messages.LatelyFor).</summary>
    public static List<(string Id, string Text)> LatelyTexts(HudState s, long account, long nowMs)
    {
        var rows = Messages.LatelyFor(s.Lately, account);
        var t = new List<(string, string)>();
        for (var k = 1; k <= Messages.LatelyRows; k++)
        {
            var row = k <= rows.Count ? rows[k - 1] : null;
            t.Add(($"gg_bl_w{k}", row?.Text ?? ""));
            t.Add(($"gg_bl_t{k}", row == null ? "" : Messages.Ago(row.AtMs, nowMs)));
        }
        return t;
    }

    public static List<(string Id, string Class, bool On)> LatelyClasses(HudState s, long account)
    {
        var count = Messages.LatelyFor(s.Lately, account).Count;
        return Enumerable.Range(1, Messages.LatelyRows).Select(k => ($"gg_bl_r{k}", "used", k <= count)).ToList();
    }

    /// <summary>A click, as the plugin receives it: the action it asks the bridge for, or null for the buttons that only work the window.
    /// The board's buttons are the same as the live column's: a like on the map that is on, and Start a vote.</summary>
    public static string? Command(string buttonId, HudState? s, VoteState? v = null) => buttonId switch
    {
        ButtonUp or BoardUp => "!up",
        ButtonDown or BoardDown => "!down",
        BoardBig => "!down big", // content v3.1.0's Too big: a Dislike's reason (one command for it)
        BoardAds => "!down ads", // content v3.2.0
        ButtonAct or BoardNext => s == null ? null : Action(s, v).Command,
        _ => null,
    };
}

/// <summary>Who has what on screen, per player slot, kept free of CounterStrikeSharp like GuidePanel so the bridge's --selftest can drive
/// it on a handed clock. The plugin feeds it events and asks Frame for the wanted view of every slot, then sends only the difference.
///
/// The window opens at every login once the player has spawned (or after WaitLimitSeconds) and stays until its X: it is the welcome, and the X is the
/// HUD check's first click (HudCheck.VersionButton). Opened any other way it takes the mouse - and with it the player's movement - which is why it closes itself
/// after IdleCloseSeconds without a click. Damage, death and a respawn never close it: nothing protects a player while it is open but the vote's freeze. Everybody
/// gets the "now playing" card at their first spawn on each map.
///
/// Panorama cannot animate visibility, and a panel that turns visible and changes in the same update pops instead of fading. So each
/// thing shows in two steps a FadeSeconds apart: visible first (still transparent), then "in" (opaque, in place); and leaves the same way
/// round, "in" first, then invisible - while the mouse is let go at once.</summary>
public sealed class HudSlots
{
    public const double SpawnDelaySeconds = 1.5;  // after a spawn, let the HUD settle (and the client load the layout) first
    public const double WaitLimitSeconds = 20;    // a player who never spawns (spectating, picking a team) gets it anyway after this
    public const double ToastSeconds = 8;
    public const double IdleCloseSeconds = 30;    // the window goes by itself after this long without a click; the drain bar shows it (gungame_hud.css: 30s)
    public const double DrainAfterOpenSeconds = 1; // the idle line starts this long after the window opens: a transition on a panel only just shown does not run (a click restarts it after FadeSeconds)
    public const double FadeSeconds = 0.3;        // between the two steps of showing or hiding; more than one plugin frame (0.25 s)

    /// <summary>The window's pages. START: Start; MAPS: Maps (its browser, and the ballot while a vote is on); FUN: Fun; SERVER: Players, Game, Sync;
    /// HELP: Help, How. Players is v4.6.0's: an older content has no rule for its page class and shows an empty page there.</summary>
    public enum Page { Start, Maps, Game, Help, How, Fun, Sync, Players }

    /// <summary>The main tab a page belongs to, as the layout groups them.</summary>
    public static string GroupOf(Page page) => page switch
    {
        Page.Maps => "maps", Page.Fun => "fun", Page.Players or Page.Game or Page.Sync => "server", Page.Help or Page.How => "help", _ => "start",
    };

    /// <summary>What one slot should see now. Window/Toast: visible at all (fading included); WindowIn/ToastIn: fully shown. Toast: 0 none, 1 now playing.
    /// Draining: the bar that shows the idle close is running (it restarts by losing this for a moment). Returning: the player has been here before.</summary>
    public readonly record struct View(bool Window, bool WindowIn, bool Capture, Page Page, bool Draining, int Toast, bool ToastIn, bool Returning = false);

    sealed class Slot
    {
        public bool Open;
        public Page Page;
        public DateTime OpenAt = DateTime.MaxValue;    // a window waiting for its moment
        public DateTime OpenedAt, ClosedAt, IdleUntil, DrainFrom;
        public DateTime? WaitingSince;                 // css_guide came for a player who has not spawned yet
        public bool Pinned;                            // the welcome: stays until its X, whatever else happens
        public bool Returning;                         // css_guide said this player has been here before
        public int Toast;
        public DateTime ToastFrom = DateTime.MaxValue, ToastUntil;
        public bool SeenMap;                           // had the now-playing card (or the welcome) on this map
    }

    readonly Dictionary<int, Slot> slots = new();

    public bool Idle => slots.Values.All(s => !s.Open && s.OpenAt == DateTime.MaxValue && s.WaitingSince == null && s.Toast == 0);

    Slot Of(int slot) => slots.TryGetValue(slot, out var s) ? s : slots[slot] = new Slot();

    /// <summary>css_guide from the bridge: a real join: the welcome window, on its start page, until its X. Right away (after the spawn delay) for a player who is
    /// alive, at their next spawn otherwise.</summary>
    public void Joined(int slot, bool first, bool alive, DateTime now)
    {
        var s = Of(slot);
        s.SeenMap = true;
        s.Returning = !first;
        s.Pinned = true;
        s.Toast = 0; s.ToastFrom = DateTime.MaxValue;
        s.WaitingSince = null;
        if (alive) Schedule(s, now.AddSeconds(SpawnDelaySeconds));
        else s.WaitingSince = now;
    }

    public void Spawned(int slot, DateTime now)
    {
        var s = Of(slot);
        if (s.WaitingSince != null) { s.WaitingSince = null; Schedule(s, now.AddSeconds(SpawnDelaySeconds)); return; }
        if (s.SeenMap || s.Open) return;
        s.SeenMap = true;
        Card(s, 1, now.AddSeconds(SpawnDelaySeconds));
    }

    static void Schedule(Slot s, DateTime at) { s.OpenAt = at; s.Page = Page.Start; }

    static void Card(Slot s, int kind, DateTime at) { s.Toast = kind; s.ToastFrom = at; s.ToastUntil = at.AddSeconds(ToastSeconds); }

    /// <summary>A new map: every client reconnects, so nothing survives, and everybody is owed the now-playing card again.</summary>
    public void MapStarted() => slots.Clear();

    public void Gone(int slot) => slots.Remove(slot);

    /// <summary>The window on a given page, opened or kept open (a click's worth of idle time): the board's Open full menu, TAB + E, a
    /// Workshop link pasted in chat.</summary>
    public void Show(int slot, Page page, DateTime now)
    {
        var s = Of(slot);
        s.WaitingSince = null;
        s.Page = page;
        if (!s.Open) Open(s, now); else Touch(s, now);
    }

    public Page PageOf(int slot) => slots.TryGetValue(slot, out var s) ? s.Page : Page.Start;

    /// <summary>The HUD check (HudCheck): the welcome window again on a new map for a player who never clicked its X - after the spawn delay, instead of this spawn's
    /// card; nothing when it is open already.</summary>
    public void Check(int slot, DateTime now)
    {
        var s = Of(slot);
        s.SeenMap = true;
        s.WaitingSince = null;
        s.Pinned = true;
        if (s.Open) return;
        s.Toast = 0; s.ToastFrom = DateTime.MaxValue;
        s.Page = Page.Start;
        s.OpenAt = now.AddSeconds(SpawnDelaySeconds);
    }

    /// <summary>The window on its start page (the HUD check, css_guide), or closed with "off" (TAB coming up on the TAB window, the TAB chord
    /// switched off, css_gghud release).</summary>
    public void Info(int slot, string arg, DateTime now)
    {
        var s = Of(slot);
        s.WaitingSince = null;
        if (arg.Trim().ToLowerInvariant() is "off" or "close" or "stop" or "0") { Close(s, now); return; }
        s.Page = Page.Start;
        if (!s.Open) Open(s, now); else Touch(s, now);
    }

    /// <summary>A vote's freeze is over: the window it held closes, unless it is the welcome, which goes back to its start page and waits for its X.</summary>
    public void VoteOver(int slot, DateTime now)
    {
        if (!slots.TryGetValue(slot, out var s) || !s.Open) return;
        if (s.Pinned) { s.Page = Page.Start; Touch(s, now); return; }
        Close(s, now);
    }

    /// <summary>A click in the window: closes it, switches the page, or (any other button) just keeps it open - the plugin turns the
    /// vote and action buttons into the bridge's actions itself (HudView.Command). Clicks from a slot without the window open are ignored.</summary>
    public void Click(int slot, string buttonId, DateTime now)
    {
        if (!slots.TryGetValue(slot, out var s) || !s.Open) return;
        if (buttonId == HudView.ButtonClose) { Close(s, now); return; }
        foreach (var (id, page) in HudView.Tabs) if (buttonId == id) s.Page = page;
        Touch(s, now);
    }

    public bool WindowOpen(int slot) => slots.TryGetValue(slot, out var s) && s.Open;

    void Open(Slot s, DateTime now)
    {
        s.Open = true; s.OpenAt = DateTime.MaxValue; s.OpenedAt = now;
        s.Toast = 0; s.ToastFrom = DateTime.MaxValue;
        Touch(s, now);
        s.DrainFrom = now.AddSeconds(DrainAfterOpenSeconds);
    }

    static void Touch(Slot s, DateTime now) { s.IdleUntil = now.AddSeconds(IdleCloseSeconds); s.DrainFrom = now.AddSeconds(FadeSeconds); }

    /// <summary>The TAB window (HudTab, mode window), every frame while TAB is held: no idle close and no drain bar while it is held,
    /// since nothing TAB opens ends on a timer. Once the hold ends the window closes with TAB, or (TAB + E) turns into the
    /// menu, whose Show starts its idle close afresh.</summary>
    public void Hold(int slot, DateTime now)
    {
        if (!slots.TryGetValue(slot, out var s) || !s.Open) return;
        s.IdleUntil = now.AddSeconds(IdleCloseSeconds);
        s.DrainFrom = DateTime.MaxValue;
    }

    static void Close(Slot s, DateTime now)
    {
        if (s.Open) s.ClosedAt = now;
        s.Open = false; s.OpenAt = DateTime.MaxValue; s.Pinned = false;
    }

    /// <summary>Every slot's wanted view at this moment. Advances the timers: a waiting window opens, an idle one closes, cards expire.</summary>
    public List<(int Slot, View View)> Frame(DateTime now)
    {
        var frame = new List<(int, View)>();
        foreach (var (id, s) in slots)
        {
            if (s.WaitingSince is { } w && (now - w).TotalSeconds >= WaitLimitSeconds) { s.WaitingSince = null; Schedule(s, now); }
            if (!s.Open && now >= s.OpenAt) Open(s, now);
            if (s.Open && !s.Pinned && now >= s.IdleUntil) Close(s, now);
            var fading = !s.Open && now < s.ClosedAt.AddSeconds(FadeSeconds);
            if (s.Toast != 0 && now >= s.ToastUntil.AddSeconds(FadeSeconds)) { s.Toast = 0; s.ToastFrom = DateTime.MaxValue; }
            var toast = s.Toast != 0 && now >= s.ToastFrom && !s.Open && !fading ? s.Toast : 0;
            frame.Add((id, new View(
                Window: s.Open || fading,
                WindowIn: s.Open && now >= s.OpenedAt.AddSeconds(FadeSeconds),
                Capture: s.Open,
                Page: s.Page,
                Draining: s.Open && !s.Pinned && now >= s.DrainFrom,
                Toast: toast,
                ToastIn: toast != 0 && now >= s.ToastFrom.AddSeconds(FadeSeconds) && now < s.ToastUntil,
                Returning: s.Returning)));
        }
        return frame;
    }
}
