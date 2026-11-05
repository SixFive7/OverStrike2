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
    /// <summary>The layout release: part of the layout's and the stylesheets' file names (hud/layout/gungame_hud_{Release}.xml,
    /// hud/styles/gungame_hud_{Release}.css, hud/styles/gungame_hud_thumbs_{Release}.css), and every release is a new Workshop item.
    /// CS2 caches a layout by its path for the whole game session, so a player who still has an older release loaded never gets it
    /// served for a new one: a new release is a new path (README "Building and publishing the HUD addon").</summary>
    public const string Release = "v3_0_0";

    /// <summary>The layout's content on that path. A release can be new content on the SAME path: content v3_1_0 (the
    /// too-big vote, the new-maps setting, How it works, the new maps' pictures) keeps every panel of v3_0_0 and only adds, so one plugin
    /// drives a game holding either - players keep a working HUD while Valve reviews the update and until their game has downloaded it.
    /// The panels it adds get words only once the player's game has clicked one of them (HudView.NewIds, HudView.NewButtons).
    /// Content v3_2_0 (the Ads vote, the Fun page with friendly fire's rows, two more Help rows, the restart button's blink, the pictures of #3565 and
    /// up) does the same one level up: it keeps every panel of v3_1_0, and what it adds waits for a click on one of its own buttons
    /// (HudView.NewIds32, HudView.NewButtons32). So one plugin drives a game holding any of the three.</summary>
    /// Over Strike 2 (v3_2_0 never shipped) is content v4_0_0, the same way: every panel of v3_1_0 kept, what it adds gated as
    /// v3_2_0's was (NewIds32), and its version checked against the server's by the layout itself (HudVote.cs). Content v4_1_0 keeps every
    /// panel of v4_0_0 and adds the popup that asks why a player disliked a map, and the Fun page's row for the grenade drops, whose buttons
    /// moved there from the Game page: nothing is sent to their panels (HudView.NewButtons41), so no game has to prove it holds them.
    /// Content v4_2_0 keeps every panel of v4_1_0 and adds the pictures of the maps not listed yet (hud/thumbs.py --ahead), a picture button on each
    /// card (HudPreview) and the mode filter's chips: their state is classes on panels every game has, so again nothing is sent to them (NewButtons42).
    /// Content v4_3_0 keeps every panel of v4_2_0 and adds the Sync tab: the Workshop sync's dashboard (HudDash.cs), whose words are
    /// dialog variables and whose state is classes on gg_screen, so nothing is sent to its panels either (NewButtons43).
    /// Content v4_4_0 keeps every panel of v4_3_0 and adds the vote's pages, Random's button and the
    /// map browser that adds to the vote, each card's mode label and colour, the inline reasons of a Dislike, the Fun page's Random and Default and
    /// the Our maps glow; the board's Too big and Ads buttons step aside. Again nothing is sent to its new panels (HudVote.NewButtons44).
    /// Content v4_5_0 keeps every panel of v4_4_0 and adds where the votes go - each card's bar and voters,
    /// the leader or a tie lit, the summary strip (HudVote.NewIds45: only a game that has shown v4_5_0 gets its classes) - the waiting line, Remove
    /// mine on a card of your own request, the line back to a vote you closed, the Fun page's Catch-up row and its changed marks, and Our maps' pulse.
    public const string Content = "v4_5_0";

    /// <summary>The layout as the client looks it up: the SOURCE path, extension included. Anything else spawns fine and shows nothing.</summary>
    public const string Layout = "panorama/layout/custom_game/gungame_hud_" + Release + ".xml";

    /// <summary>The switch, on the cs2 service: the Workshop id of the published HUD addon. Empty (the default) draws no HUD - the plugin
    /// answers chat commands in chat and shows the small centre welcome panel, and pre.sh does not even load MultiAddonManager.</summary>
    public const string AddonVariable = "OS2_HUD_ADDON";

    /// <summary>The switch's old name, read only while AddonVariable is not set at all: a one-time fallback for a deployment that still says
    /// it the old way, to be removed in the next release.</summary>
    public const string OldAddonVariable = "GUNGAME_HUD_ADDON";

    /// <summary>Paths on the bridge's HTTP port, followed by LOG_SECRET: the plugin GETs the live state, the map catalog and the search
    /// index, and POSTs a player's click.</summary>
    public const string StatePath = "/hud/", ActPath = "/act/", CatalogPath = "/catalog/", SearchPath = "/search/", DashPath = "/dash/";

    /// <summary>The actions a click may ask for without an argument. They are written like the chat commands, which is what the bridge and
    /// the plugin speak to each other on /act: a typed command arrives as the same click.</summary>
    public static readonly string[] Actions = ["!up", "!down", "!next", "!next random", "!previous", "!restart", "!time 10", "!time 60"];

    /// <summary>The "new maps in random picks" setting as the Game page switches it (the bridge's Selection.cs).</summary>
    public static readonly string[] NewMapsActions = ["!newmaps always", "!newmaps half", "!newmaps never"];

    /// <summary>Whether a click may ask for this. The bridge refuses anything else on its /act path, so the menu can never reach a server
    /// command, a name search or anything free-form: a map is named by its rank, an official map's name or a Workshop id.</summary>
    public static bool IsAction(string command)
    {
        if (Actions.Contains(command) || NewMapsActions.Contains(command)) return true;
        if (command.StartsWith("!hudok ", StringComparison.Ordinal)) return HudCheck.IsRelease(command[7..]); // the HUD check's click (HudCheck)
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

    /// <summary>The Help rows content v3_2_0 added: a fifth row in two groups (gg_c{group + 1}_5). None any more - one Fun row, one row
    /// for a Dislike's reasons, no Ads row - so those rows stay collapsed.</summary>
    public static readonly (int Group, Row Row)[] HelpMore = [];

    /// <summary>The Help page: where each thing is in the menu. Command is the place to click, Words what it does; every action has its chat
    /// command too (ChatCommands.cs, !help).</summary>
    public static readonly Group[] Help =
    [
        new("Maps", [
            new("Maps  ·  Play", "Put any map to the vote: a vote opens, your vote on that map"),
            new("Search", "The keyboard on the Maps page: name, tags or a Workshop ID"),
            new("Start a vote", "On the board or in the menu: a vote for the next map"),
            new("Previous  ·  !back", "The map before this one, to the vote")]),
        new("The vote", [
            new("Everybody votes", "Everybody is frozen until all have voted, 60 s at the most"),
            new("Cards", "Click to vote, another to change it, yours to take it back"),
            new("Random", "The dice after the vote, in this mode"),
            new("The wait", "2 minutes after a vote; a match end opens one anyway")]),
        new("Likes", [
            new("Like  ·  Dislike", "On the map that is on; the other one takes a mark back first"),
            new("Dislike  ·  reasons", "Too big: 1 random pick in 100; Ads: never a random pick; the rest: a dislike"),
            new("Maps  ·  arrows", "Like or dislike any map's card: up likes, down dislikes"),
            new("Sort  ·  Likes", "The most liked maps first")]),
        new("Game", [
            new("Fun", "Gravity, friendly fire, grenade drops, catch-up and more"),
            new("Match length", "10 or 60 minutes, from the next match"),
            new("Restart", "Reboot a stuck server - click twice to confirm")]),
        new("The board", [
            new("Hold TAB", "The live board beside the scoreboard, with the mouse"),
            new("TAB + E", "The full menu"),
            new("How  ·  Sync", "Every rule and chance of a map pick, the map sync"),
            new("Chat  ·  !help", "Every action has a chat command too - !help lists them")]),
    ];

    /// <summary>The "How it works" page (layout content v3_1_0) - the one set of rule texts (the Rules tab is gone; !rules opens this page): the
    /// vote, the ballot's recipe, the dice (what is on the server's disk, the 1% too-big rule), the points and what likes do, and every exclusion,
    /// tersely and completely. Each number in it is the bridge's own constant: --selftest (SelfTestHud.cs, SelfTestRules.cs, SelfTestBallot.cs)
    /// fails when one of them moves without these words.</summary>
    public static readonly Rule[] How =
    [
        new("The vote", [
            "Play on a card puts its map to the vote, with your vote on it. While a vote is on, it goes on that ballot. Start a vote calls one without a map.",
            "A vote freezes everybody - no moving, shooting or closing it - until all have voted, 60 s at the most. Most votes win; a tie is drawn.",
            "Click a card to vote, another to change it, your own to take it back. A map a player put on the ballot leaves it once it has no votes.",
            "A match end opens a vote by itself. After any vote, 2 minutes before the next one can be called. No votes: mid-match the map stays, at a match end Random."]),
        new("The ballot", [
            "Keep playing this map while the match is on, Play this map again after it, every map a player put on it, and one map of each other mode.",
            "Then this mode: a fresh map - a Workshop upload or update of the last 30 days - a surprise new to us, and favourites the server keeps fill the page.",
            "Wingman takes 4 players at most: with more on, no Wingman map is on it or can win. The most votes first; a second page only when needed.",
            "Every card but a player's pick is on the server's disk, so it loads at once. A risky map shows its record, and putting it to the vote takes a second click."]),
        new("The dice", [
            "Random is the dice after the vote. Like every card the dice pick, it is a map on the server's disk: a favourite it keeps, or one of a small stock new to us.",
            "1 random pick in 100 takes a too-big map (measured XL, or marked Too big by a player), the other 99 one that is not; an empty group hands the pick to the other.",
            "The dice change the map by themselves only at a server start, 10 minutes with nobody on, a broken map. Previous puts the map before this one to the vote."]),
        new("Points: each one doubles the odds", [
            "Workshop popularity - rating, players, trend - gives up to 4 points, each Like a point (up to 5 Likes), and each Dislike or Too big takes 2.",
            "A card shown and not chosen loses 1 point, 2 at most, fading to nothing over 28 days.",
            "One mark per player per map, kept: Like, or Dislike with a reason. Only Too big and Ads do more than a Dislike. The opposite one takes it back first.",
            "Nothing else counts: a map's download size never makes it more or less likely."]),
        new("Never rolled", [
            "The map that is on, and the last 10 maps played (while others are left).",
            "Maps that crashed the server or are on the broken list, blocked maps, and maps gone from the Workshop. A Workshop update lifts a crash mark.",
            "Maps a player marked Ads (they show ads or send players to another server) - anybody can still put them to the vote."]),
    ];

    public const int HowSections = 5, HowLines = 4; // the How page's sections and the lines in each (the layout has exactly this many)

    /// <summary>The start page: three steps, each a title, where to click and a few words.</summary>
    public static readonly (string Title, string Command, string Words)[] Steps =
    [
        ("Pick a map", "Maps", "Every map with its picture; Play puts it to the vote."),
        ("Vote", "The vote", "Everybody votes; the most votes win."),
        ("Like it", "Like  Dislike", "Your likes steer the random maps."),
    ];

    public const string Headline = "Race up the weapon ladder.";
    public const string Lede = "Kills earn you the next gun; a kill with the golden knife on the last rung wins the match. Every map is a Workshop map, and any player can pick one.";
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

    public static string Tally(int up, int down) => up + down == 0 ? "no votes yet" : $"+{up}{(down > 0 ? $"  -{down}" : "")}";
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
    public long CatalogVersion;                         // the map catalog's version: the plugin fetches it again when this moves
    public string CheckMode = "";                       // the HUD check as configured (HUD_CHECK: off, dryrun, enforce); empty from an older bridge
    public Dictionary<string, string> Proofs = new();   // release -> the first real click verified on it (who, when): dryrun is enforce there
    public Dictionary<long, string> Verified = new();   // account id -> the release it last clicked in
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

    public string Encode()
    {
        var s = new StringBuilder(Magic).Append('\n');
        void Line(params object[] fields) => s.Append(string.Join('\t', fields.Select(f => f is string t ? Hud.Clean(t) : Convert.ToString(f, CultureInfo.InvariantCulture)))).Append('\n');
        Line("maps", Maps);
        Line("now", Map, By, Size, Key, Up, Down, Status);
        if (Loading) Line("loading");
        foreach (var (id, v) in MyVotes) Line("vote", id, v);
        foreach (var (map, up, down) in Top.Take(ListLength)) Line("top", map, up, down);
        if (ChangeIn >= 0) Line("change", ChangeMap, ChangeBy, ChangeIn);
        if (RoundMinutes > 0) Line("set", RoundMinutes);
        if (CatalogVersion > 0) Line("cat", CatalogVersion);
        if (CheckMode != "") Line("chk", CheckMode);
        foreach (var (release, proof) in Proofs) Line("proof", release, proof);
        foreach (var (account, release) in Verified) Line("ok", account, release);
        if (TabMode != "") Line("tab", TabMode);
        if (News != "" && NewsFor > 0) s.Append("news\t").Append(NewsFor.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Hud.Clean(News, 240)).Append('\n');
        foreach (var (account, (text, secs)) in Notes)
            if (text != "" && secs > 0)
                s.Append("note\t").Append(account.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(secs.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Hud.Clean(text, 140)).Append('\n');
        foreach (var (account, (map, secs)) in Why)
            if (secs > 0) Line("why", account, secs, map);    // an older plugin skips it
        if (PrevArg != "") Line("prev", PrevArg, PrevLabel);
        if (SearchVersion > 0) Line("srch", SearchVersion);
        if (NewMaps != "") Line("newmaps", NewMaps);         // an older plugin skips the lines it does not know
        if (Big > 0 || SizeClass != "") Line("big", Big, SizeClass);
        if (Ads > 0) Line("ads", Ads);                     // an older plugin skips it
        if (DashVersion > 0) Line("dash", DashVersion);    // an older plugin skips it
        if (RestartKind != "") s.Append("rst\t").Append(Hud.Clean(RestartKind, 12)).Append('\t').Append(Hud.Clean(RestartWhen, 12)).Append('\t')
            .Append(RestartIn.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(RestartAt.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Hud.Clean(RestartWords, 240)).Append('\n');
        if (BgId > 0) Line("bgdl", BgId, BgPercent, BgTitle); // an older plugin skips these two
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
                case "set": s.RoundMinutes = Int(f, 1); break;
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
                case "proof" when HudCheck.IsRelease(Str(f, 1)): s.Proofs[Str(f, 1)] = Str(f, 2); break;
                case "ok" when Long(f, 1) >= 0 && HudCheck.IsRelease(Str(f, 2)): s.Verified[Long(f, 1)] = Str(f, 2); break;
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
    /// content v3_1_0 (since v4_4_0 a Dislike's reason; the button stepped aside).</summary>
    public const string BoardUp = "gg_b_vup", BoardDown = "gg_b_vdown", BoardNext = "gg_b_next", BoardOpen = "gg_b_open", BoardBig = "gg_b_vbig",
        BoardAds = "gg_b_vads"; // content v3_2_0

    public const string TabHow = "gg_tab_how", TabFun = "gg_tab_fun"; // content v3_1_0, v3_2_0

    /// <summary>The window's tabs (no Queue and no Rules page any more - in a game still holding an older content their
    /// tabs do nothing).</summary>
    public static readonly (string Id, HudSlots.Page Page)[] Tabs =
        [("gg_tab_start", HudSlots.Page.Start), ("gg_tab_maps", HudSlots.Page.Maps), ("gg_tab_game", HudSlots.Page.Game),
         ("gg_tab_help", HudSlots.Page.Help), (TabHow, HudSlots.Page.How), (TabFun, HudSlots.Page.Fun), (HudDash.Tab, HudSlots.Page.Sync)];

    /// <summary>The buttons content v3_1_0 added. A click on one proves that player's game has that content: from then on the plugin sends
    /// them the words of NewIds too. They need nothing from the server themselves - fixed labels, and their state in classes on panels that
    /// v3_0_0 has as well.</summary>
    public static readonly string[] NewButtons =
        [BoardBig, TabHow, HudMenu.NewAlways, HudMenu.NewHalf, HudMenu.NewNever, .. Enumerable.Range(1, HudMenu.Cards).Select(k => $"{HudMenu.Card(k)}_big")];

    /// <summary>The panels content v3_1_0 added that the plugin sends something to (a label's words, a row's "used"). A game that still holds
    /// the v3_0_0 content does not have them, and is never sent them: the plugin sends them only to a player whose game has clicked one of
    /// NewButtons (HudScreen), per player. The selftest checks that everything else it sends is in the shipped v3_0_0 layout.</summary>
    public static readonly HashSet<string> NewIds = NewPanels();

    /// <summary>The buttons content v3_2_0 added: the board's Ads, the Fun tab, each card's Ads and every choice of the Fun page. A click on
    /// one proves that player's game holds v3_2_0 (and so v3_1_0 too).</summary>
    public static readonly string[] NewButtons32 =
        [BoardAds, TabFun, HudBrowser.OursButton, .. Enumerable.Range(1, HudMenu.Cards).Select(k => $"{HudMenu.Card(k)}_ads"),
         .. Enumerable.Range(1, Fun.Rows).SelectMany(r => Enumerable.Range(1, Fun.ChoicesPerRow).Select(c => HudMenu.FunButton(r, c)))];

    /// <summary>The panels content v3_2_0 added that the plugin sends something to: the board's ads count, the Fun page (its note, every row,
    /// title, words, choice and choice label - the choices get classes, so they are here as well as in NewButtons32) and the two Help rows.
    /// Sent only to a player whose game has clicked one of NewButtons32, like NewIds one level down.</summary>
    public static readonly HashSet<string> NewIds32 = NewPanels32();

    /// <summary>The buttons content v4_1_0 added: the popup's reasons. They need nothing sent to them; a click on one proves that player's game
    /// holds v4_1_0, and so everything before it. (The grenade drops' buttons moved to the Fun page and are no proof: every content has them.)</summary>
    public static readonly string[] NewButtons41 = [.. Enumerable.Range(1, DownReasons.All.Length).Select(HudVote.WhyButton)];

    /// <summary>The buttons content v4_2_0 added: each card's picture button and the mode filter's chips. They need nothing sent to them either; a click
    /// on one proves that player's game holds v4_2_0.</summary>
    public static readonly string[] NewButtons42 = [.. Enumerable.Range(1, HudMenu.Cards).Select(HudMenu.PicButton), .. HudBrowser.ModeButtons.Select(b => b.Id)];

    /// <summary>The buttons content v4_3_0 added: the Sync tab and the pagers of its lists (HudDash.LayoutButtons). Nothing is sent to them; a click on
    /// one proves that player's game holds v4_3_0.</summary>
    public static readonly string[] NewButtons43 = HudDash.LayoutButtons;

    static HashSet<string> NewPanels32()
    {
        var ids = new HashSet<string> { "gg_b_ads", HudMenu.FunNote, HudBrowser.OursButton }; // the Our maps chip gets its "on" class
        for (var r = 1; r <= Fun.Rows; r++)
        {
            ids.Add(HudMenu.FunRow(r)); ids.Add($"{HudMenu.FunRow(r)}_title"); ids.Add($"{HudMenu.FunRow(r)}_txt");
            for (var c = 1; c <= Fun.ChoicesPerRow; c++) { ids.Add(HudMenu.FunButton(r, c)); ids.Add($"{HudMenu.FunButton(r, c)}_txt"); }
        }
        foreach (var (g, _) in Hud.HelpMore) { ids.Add($"gg_c{g + 1}_5"); ids.Add($"gg_c{g + 1}_5_cmd"); ids.Add($"gg_c{g + 1}_5_txt"); }
        return ids;
    }

    /// <summary>The content a panel came with: 32 for v3_2_0's, 31 for v3_1_0's, 0 for one every game has.</summary>
    public static int LevelOf(string id) => HudVote.NewIds45.Contains(id) ? 45 : NewIds32.Contains(id) ? 32 : NewIds.Contains(id) ? 31 : 0;

    /// <summary>The content a button proves a game holds: 44, 43, 42, 41, 32, 31, or 0 for a button every game has.</summary>
    public static int LevelOfButton(string id) => HudVote.NewButtons45.Contains(id) ? 45 : HudVote.NewButtons44.Contains(id) ? 44 : NewButtons43.Contains(id) ? 43 : NewButtons42.Contains(id) ? 42 : NewButtons41.Contains(id) ? 41
        : NewButtons32.Contains(id) ? 32 : NewButtons.Contains(id) ? 31 : 0;

    static HashSet<string> NewPanels()
    {
        var ids = new HashSet<string> { "gg_b_big", HudMenu.NewMapsNow };
        for (var s = 1; s <= Hud.HowSections; s++)
        {
            ids.Add($"gg_h{s}_title");
            for (var l = 1; l <= Hud.HowLines; l++) { ids.Add($"gg_h{s}_{l}"); ids.Add($"gg_h{s}_{l}_txt"); }
        }
        return ids;
    }

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
        for (var s = 0; s < Hud.HowSections; s++) // content v3_1_0 (NewIds): the plugin sends these only to a game that has them
        {
            var how = s < Hud.How.Length ? Hud.How[s] : null;
            t.Add(($"gg_h{s + 1}_title", how?.Title ?? ""));
            for (var l = 0; l < Hud.HowLines; l++) t.Add(($"gg_h{s + 1}_{l + 1}_txt", how != null && l < how.Lines.Length ? how.Lines[l] : ""));
        }
        foreach (var (g, row) in Hud.HelpMore) // content v3_2_0 (NewIds32)
        {
            t.Add(($"gg_c{g + 1}_5_cmd", row.Command));
            t.Add(($"gg_c{g + 1}_5_txt", row.Also == "" ? row.Words : $"{row.Words}  ·  {row.Also}"));
        }
        t.Add((HudMenu.FunNote, HudMenu.FunNoteText)); // content v3_2_0 (NewIds32)
        return t;
    }

    /// <summary>Rows of the help and how-it-works pages that hold something: the layout collapses the rest.</summary>
    public static List<(string Id, string Class, bool On)> StaticClasses()
    {
        var c = new List<(string, string, bool)>();
        for (var g = 0; g < Hud.Help.Length; g++)
            for (var r = 0; r < Hud.RowsPerGroup; r++) c.Add(($"gg_c{g + 1}_{r + 1}", "used", r < Hud.Help[g].Rows.Length));
        for (var s = 0; s < Hud.HowSections; s++)
            for (var l = 0; l < Hud.HowLines; l++) c.Add(($"gg_h{s + 1}_{l + 1}", "used", s < Hud.How.Length && l < Hud.How[s].Lines.Length));
        foreach (var (g, _) in Hud.HelpMore) c.Add(($"gg_c{g + 1}_5", "used", true)); // content v3_2_0 (NewIds32)
        return c;
    }

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
        var by = s.By == "" ? "" : s.By == "the dice" ? "rolled by the dice" : $"picked by {s.By}";
        var byline = string.Join("  ·  ", new[] { by, s.Size }.Where(x => x != ""));
        var status = s.Loading ? "Loading - everyone reconnects in a moment" : s.Status;
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
        t.Add(("gg_b_big", s.Big.ToString(CultureInfo.InvariantCulture))); // content v3_1_0 (NewIds): the too-big votes on the map that is on
        t.Add(("gg_b_ads", s.Ads.ToString(CultureInfo.InvariantCulture))); // content v3_2_0 (NewIds32): the ads votes on it
        t.Add(("gg_news_text", s.NewsFor > 0 ? s.News : ""));
        t.Add(("gg_b_count", s.Maps > 0 ? $"{s.Maps} maps" : ""));
        t.Add(("gg_w_lede", Hud.Lede));
        t.Add(("gg_w_maps", s.Maps > 0 ? $"{s.Maps} Workshop maps" : "Workshop maps"));
        t.Add(("gg_k_map", s.ChangeMap));
        t.Add(("gg_k_by", s.ChangeBy == "" ? "" : s.ChangeBy == "the dice" ? "rolled by the dice" : $"picked by {s.ChangeBy}"));
        t.Add(("gg_t_map", map));
        t.Add(("gg_t_by", byline == "" ? Hud.Tally(s.Up, s.Down) : $"{byline}  ·  {Hud.Tally(s.Up, s.Down)}"));
        t.Add(("gg_t_hint", "Like it? Hold TAB and click Like or Dislike."));
        return t;
    }

    /// <summary>The vote's cards that have votes, the most first: the live column's rows while a vote is on.</summary>
    static List<VoteState.Option> Leading(VoteState? v) => v is not { Round: > 0 } ? [] : v.Options.Where(o => o.Votes > 0).OrderByDescending(o => o.Votes).Take(HudState.ListLength).ToList();

    /// <summary>The live classes, the same for everybody: which list rows are in use, an empty vote or top list, a change counting down.</summary>
    public static List<(string Id, string Class, bool On)> Classes(HudState s, VoteState? v = null)
    {
        var c = new List<(string, string, bool)>
        {
            (Global, "ready", true), (Global, "changing", s.ChangeIn >= 0), (Global, "loading", s.Loading), (Global, "news", s.NewsFor > 0 && s.News != ""),
            // content v3_1_0: the new-maps setting that is on lights its button; on gg_global, which a v3_0_0 game has too (and no rule for)
            (Global, "nm-always", s.NewMaps == "always"), (Global, "nm-half", s.NewMaps == "half"), (Global, "nm-never", s.NewMaps == "never"),
        };
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

    /// <summary>The seconds on the map-change banner, recomputed by the plugin every second from the deadline it took off the state.</summary>
    public static string Countdown(int seconds) => seconds <= 0 ? "now" : seconds.ToString(CultureInfo.InvariantCulture);

    /// <summary>What the contextual button asks for while the vote is open: the plugin opens that player's window on it (nothing goes to the bridge).</summary>
    public const string OpenVote = "!vote";

    /// <summary>The button under the likes (the board's, and the window's live column's): Start a vote, Keep playing on it; the vote
    /// open - open it; the wait after a vote - when the next can start; a change under way - nothing. Command is what a click makes the player say,
    /// null for nothing, OpenVote for the window.</summary>
    public static (string Text, string Sub, string? Command) Action(HudState s, VoteState? v = null)
    {
        if (s.ChangeIn >= 0 || s.Loading) return ("Map change under way", "wait for the next map", null);
        if (v is { Open: true, Round: > 0 }) return ("The vote is on: open it", "pick the next map, or keep playing", OpenVote);
        if (v?.WaitFor > 0) return ("Start a vote", $"the next one can start in {MapVote.Left(v.WaitFor)}", null);
        return ("Start a vote", "for the next map - Keep playing is on it", "!next");
    }

    /// <summary>The note this player sees now, if any: the bridge's answer to what they clicked.</summary>
    public static string NoteOf(HudState s, long account) => s.Notes.TryGetValue(account, out var n) && n.For > 0 ? n.Text : "";

    /// <summary>Per-player texts: the greeting, the toast's first line, your own like, the button under it (in the window and on the board) and
    /// your note.</summary>
    public static List<(string Id, string Text)> PlayerTexts(HudState s, long account, string name, bool welcomeBack, VoteState? v = null)
    {
        var mine = s.MyVotes.GetValueOrDefault(account);
        var (text, sub, _) = Action(s, v);
        return
        [
            ("gg_w_hello", $"Welcome, {Hud.Clean(name, 32)}"),
            ("gg_t_eyebrow", welcomeBack ? $"Welcome back, {Hud.Clean(name, 32)}" : "Now playing"),
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
        var asked = s.Why.TryGetValue(account, out var why) ? why.For : 0; // the popup (content v4_1_0): classes on gg_screen, for which an older game has no rule; the drain line a second in, so a second Dislike starts it again
        return [(Screen, "voted-up", mine > 0), (Screen, "voted-down", mine < 0), (Screen, "act-off", command == null),
            (Screen, "note", NoteOf(s, account) != ""), (Screen, "voted-big", mine == HudCatalog.TooBigVote), (Screen, "voted-ads", mine == HudCatalog.AdsVote),
            (Screen, "why", asked > 0), (Screen, "why-run", asked is > 0 and < (int)DownReasons.PopupSeconds)];
    }

    /// <summary>A click, as the plugin receives it: the action it asks the bridge for, or null for the buttons that only work the window.
    /// The board's buttons are the same as the live column's: a like on the map that is on, and Start a vote.</summary>
    public static string? Command(string buttonId, HudState? s, VoteState? v = null) => buttonId switch
    {
        ButtonUp or BoardUp => "!up",
        ButtonDown or BoardDown => "!down",
        BoardBig => "!down big", // content v3_1_0's Too big: a Dislike's reason (one command for it)
        BoardAds => "!down ads", // content v3_2_0
        ButtonAct or BoardNext => s == null ? null : Action(s, v).Command,
        _ => null,
    };
}

/// <summary>Who has what on screen, per player slot, kept free of CounterStrikeSharp like GuidePanel so the bridge's --selftest can drive
/// it on a handed clock. The plugin feeds it events and asks Frame for the wanted view of every slot, then sends only the difference.
///
/// The window opens for a first-time player once they have spawned (or after WaitLimitSeconds), and on !info; it takes the mouse while
/// open - and with it the player's movement, which is why it closes itself after IdleCloseSeconds without a click, and at once when its
/// player is hurt. A returning player gets a short "welcome back" card instead, and everybody gets the "now playing" card at their first
/// spawn on each map.
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
    public const double DrainAfterOpenSeconds = 1; // the idle line starts this long after the window opens: a transition on a panel only just shown does not run (seen in a real client, 2026-11-02; a click restarts it after FadeSeconds)
    public const double FadeSeconds = 0.3;        // between the two steps of showing or hiding; more than one plugin frame (0.25 s)

    public enum Page { Start, Maps, Game, Help, How, Fun, Sync } // How: content v3_1_0; Fun: v3_2_0; Sync: v4_3_0 (the Queue and Rules pages are gone)

    /// <summary>What one slot should see now. Window/Toast: visible at all (fading included); WindowIn/ToastIn: fully shown. Toast: 0 none,
    /// 1 now playing, 2 welcome back. Draining: the bar that shows the idle close is running (it restarts by losing this for a moment).</summary>
    public readonly record struct View(bool Window, bool WindowIn, bool Capture, Page Page, bool Draining, int Toast, bool ToastIn);

    sealed class Slot
    {
        public bool Open;
        public Page Page;
        public DateTime OpenAt = DateTime.MaxValue;    // a window waiting for its moment
        public DateTime OpenedAt, ClosedAt, IdleUntil, DrainFrom;
        public DateTime? WaitingSince;                 // css_guide came for a player who has not spawned yet
        public bool WaitingFirst;
        public int Toast;
        public DateTime ToastFrom = DateTime.MaxValue, ToastUntil;
        public bool SeenMap;                           // had the now-playing card (or the welcome) on this map
    }

    readonly Dictionary<int, Slot> slots = new();

    public bool Idle => slots.Values.All(s => !s.Open && s.OpenAt == DateTime.MaxValue && s.WaitingSince == null && s.Toast == 0);

    Slot Of(int slot) => slots.TryGetValue(slot, out var s) ? s : slots[slot] = new Slot();

    /// <summary>css_guide from the bridge: a real join. First time: the window; returning: the welcome-back card. Right away (after the
    /// spawn delay) for a player who is alive, at their next spawn otherwise.</summary>
    public void Joined(int slot, bool first, bool alive, DateTime now)
    {
        var s = Of(slot);
        s.SeenMap = true;
        s.Toast = 0; s.ToastFrom = DateTime.MaxValue;
        if (alive) Schedule(s, first, now.AddSeconds(SpawnDelaySeconds));
        else { s.WaitingSince = now; s.WaitingFirst = first; }
    }

    public void Spawned(int slot, DateTime now)
    {
        var s = Of(slot);
        if (s.WaitingSince != null) { s.WaitingSince = null; Schedule(s, s.WaitingFirst, now.AddSeconds(SpawnDelaySeconds)); return; }
        if (s.SeenMap || s.Open) return;
        s.SeenMap = true;
        Card(s, 1, now.AddSeconds(SpawnDelaySeconds));
    }

    void Schedule(Slot s, bool first, DateTime at)
    {
        if (first) { s.OpenAt = at; s.Page = Page.Start; }
        else Card(s, 2, at);
    }

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

    /// <summary>The HUD check (HudCheck): the window on its start page, where the gold button says "Click to play" - after the spawn
    /// delay, instead of this spawn's card; nothing when it is open already.</summary>
    public void Check(int slot, DateTime now)
    {
        var s = Of(slot);
        s.SeenMap = true;
        s.WaitingSince = null;
        if (s.Open) return;
        s.Toast = 0; s.ToastFrom = DateTime.MaxValue;
        s.Page = Page.Start;
        s.OpenAt = now.AddSeconds(SpawnDelaySeconds);
    }

    /// <summary>The window on its start page (the HUD check, css_guide), or closed with "off" (TAB coming up on the TAB window, a hit, the
    /// TAB chord switched off, css_gghud release).</summary>
    public void Info(int slot, string arg, DateTime now)
    {
        var s = Of(slot);
        s.WaitingSince = null;
        if (arg.Trim().ToLowerInvariant() is "off" or "close" or "stop" or "0") { Close(s, now); return; }
        s.Page = Page.Start;
        if (!s.Open) Open(s, now); else Touch(s, now);
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

    /// <summary>The player took damage: an open window gives them the mouse and their movement back at once. A captured player cannot
    /// move, and a first-time player reading the window would otherwise be easy prey once spawn protection wears off.</summary>
    public void Hurt(int slot, DateTime now)
    {
        if (slots.TryGetValue(slot, out var s) && s.Open) Close(s, now);
    }

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
        s.Open = false; s.OpenAt = DateTime.MaxValue;
    }

    /// <summary>Every slot's wanted view at this moment. Advances the timers: a waiting window opens, an idle one closes, cards expire.</summary>
    public List<(int Slot, View View)> Frame(DateTime now)
    {
        var frame = new List<(int, View)>();
        foreach (var (id, s) in slots)
        {
            if (s.WaitingSince is { } w && (now - w).TotalSeconds >= WaitLimitSeconds) { s.WaitingSince = null; Schedule(s, s.WaitingFirst, now); }
            if (!s.Open && now >= s.OpenAt) Open(s, now);
            if (s.Open && now >= s.IdleUntil) Close(s, now);
            var fading = !s.Open && now < s.ClosedAt.AddSeconds(FadeSeconds);
            if (s.Toast != 0 && now >= s.ToastUntil.AddSeconds(FadeSeconds)) { s.Toast = 0; s.ToastFrom = DateTime.MaxValue; }
            var toast = s.Toast != 0 && now >= s.ToastFrom && !s.Open && !fading ? s.Toast : 0;
            frame.Add((id, new View(
                Window: s.Open || fading,
                WindowIn: s.Open && now >= s.OpenedAt.AddSeconds(FadeSeconds),
                Capture: s.Open,
                Page: s.Page,
                Draining: s.Open && now >= s.DrainFrom,
                Toast: toast,
                ToastIn: toast != 0 && now >= s.ToastFrom.AddSeconds(FadeSeconds) && now < s.ToastUntil)));
        }
        return frame;
    }
}
