// THE MESSAGE CATALOGUE: every message a player can get, one line each - which surface it goes on, who sees it there, whether chat and the TAB board
// carry a copy and for whom, its category and how long it stays. A change of mind about a message is one line here. The bridge posts through
// Program.Post (Notices.cs), the plugin through HudScreen.Post; both take the one decision of where a message goes from Route, so one message cannot go two ways.
//
// A message's text is written once, with its functional parts marked (M): chat renders the marks in the chat's functional colours, every other surface
// takes the plain words (M.Plain). Every section of a message starts with a capital - after " - ", after " · " and on a second line - and --selftest holds
// every template to it (Sections).
//
// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Globalization;
using System.Text.RegularExpressions;

public static class Messages
{
    public const int ChatLineMax = 120; // characters a chat line holds on a 1920x1080 screen
    public const double DefaultSeconds = 8; // a kind with no seconds of its own (0: as long as its state lasts) stays this long when posted
    /// <summary>The most characters each surface takes: the top line (and the older contents' news line), a line on the window's page (a note), CS2's centre
    /// text, and a message's text in chat, which chat itself cuts into lines of ChatLineMax.</summary>
    public const int TopMax = 240, LineMax = 140, CentreMax = 80, ChatMax = 400;
    public const int LatelyRows = 3, LatelyKeep = 12, SayKeepSeconds = 20;

    public static readonly Category Join = new("join", "Join"), YourHud = new("hud", "Your HUD"), Vote = new("vote", "Vote"), Fun = new("fun", "Fun"),
        Maps = new("maps", "Maps"), Server = new("server", "Server"), CatchUp = new("catchup", "Catch-up"), You = new("you", "You");

    public static readonly Category[] Categories = [Join, YourHud, Vote, Fun, Maps, Server, CatchUp, You];

    public static Category? CategoryOf(string id) => Categories.FirstOrDefault(c => c.Id == id);

    /// <summary>A kind of message: what it is, its surface and who sees it there, who gets a chat copy and a board copy, its category and how long it
    /// stays (0: as long as its state lasts). News: whether the news line of the contents before v4.6.0 carries it as well. Drawn: nothing posts it - the
    /// live state draws its surface (the window, the ballot's header, a banner), and its row only says what players see there: changing the row changes
    /// nothing on screen (--selftest holds that every other kind is posted somewhere, and no drawn one is).</summary>
    public sealed record Kind(string Id, string Row, Surface Surface, Who On, Who Chat, Who Board, Category Cat, double Seconds, bool News = true, bool Drawn = false);

    // ---- the catalogue. A message that went has no kind ----

    public static readonly Kind
        // joining
        Welcome = new("welcome", "Welcome window, at every login", Surface.Window, Who.Player, Who.Nobody, Who.Nobody, Join, 0, false, Drawn: true),
        Tagline = new("tagline", "Join tagline: the welcome window's large text, and this chat line", Surface.None, Who.Nobody, Who.Player, Who.Nobody, Join, 0, false),
        WelcomeBack = new("back", "Welcome-back card", Surface.Line, Who.Player, Who.Nobody, Who.Nobody, Join, 8, false, Drawn: true),
        NowPlaying = new("playing", "Now-playing card, at each map's first spawn", Surface.Card, Who.Everybody, Who.Nobody, Who.Everybody, Join, 8, false),
        Joined = new("joined", "A player joined with a HUD version", Surface.Plain, Who.Everybody, Who.Everybody, Who.Everybody, Join, 8),
        // your HUD and its version
        Outdated = new("outdated", "Your HUD is out of date: the banner that stays", Surface.Banner, Who.Player, Who.Nobody, Who.Nobody, YourHud, 0, false, Drawn: true),
        Newer = new("newer", "Your HUD is newer than the server's", Surface.Banner, Who.Player, Who.Nobody, Who.Nobody, YourHud, 15, false, Drawn: true),
        Hold = new("hold", "The HUD not shown by Steam", Surface.Banner, Who.Everybody, Who.Nobody, Who.Nobody, YourHud, 45),
        HoldBack = new("holdback", "The HUD not shown by Steam: back", Surface.Banner, Who.Everybody, Who.Nobody, Who.Nobody, YourHud, 30),
        HoldNoMenu = new("nomenu", "A player without the HUD during a hold", Surface.None, Who.Nobody, Who.Player, Who.Nobody, YourHud, 0, false),
        CheckClick = new("check", "HUD check: Click to play", Surface.Window, Who.Player, Who.Nobody, Who.Nobody, Join, 0, false, Drawn: true),
        CheckWarning = new("checkwarn", "HUD check: the disconnect warning", Surface.Alert, Who.Player, Who.Player, Who.Nobody, YourHud, 15, false),
        // the vote
        VoteWindow = new("votewin", "The vote: everybody's window opens and freezes", Surface.Window, Who.Everybody, Who.Nobody, Who.Nobody, Vote, 0, false, Drawn: true),
        VoteStarted = new("votestart", "The vote started: the line for everybody", Surface.Line, Who.Everybody, Who.Everybody, Who.Everybody, Vote, 0),
        VoteInactivity = new("voteidle", "Inactivity timer, and who reset it", Surface.Line, Who.Everybody, Who.Nobody, Who.Nobody, Vote, 0, false, Drawn: true),
        VoteLimit = new("votelimit", "The 60 s limit", Surface.Line, Who.Everybody, Who.Nobody, Who.Nobody, Vote, 0, false, Drawn: true),
        VoteWaiting = new("votewait", "Waiting for: the players who have not voted (chat and the board: VoteClick's line says it)", Surface.Line, Who.Everybody, Who.Nobody, Who.Nobody, Vote, 0, false, Drawn: true),
        VoteDecided = new("votedone", "The vote decided, and the change counting down", Surface.Line, Who.Everybody, Who.Everybody, Who.Everybody, Vote, 0),
        VoteKept = new("votekept", "The vote kept the map", Surface.Line, Who.Everybody, Who.Everybody, Who.Everybody, Vote, 8),
        VoteRefused = new("voteno", "You cannot call a vote yet", Surface.Line, Who.Player, Who.Everybody, Who.Everybody, Vote, 8, false),
        VoteRefusedYou = new("votenoyou", "You cannot call a vote yet: the player's own line, in their own words", Surface.Line, Who.Player, Who.Nobody, Who.Nobody, Vote, 8, false),
        // fun and game settings
        FunChanged = new("fun", "Fun: one setting changed (catch-up and the grenade drops the same)", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Fun, 10),
        FunRandom = new("funrandom", "Fun: Random", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Fun, 10),
        FunDefault = new("fundefault", "Fun: Default", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Fun, 10),
        FunRefused = new("funno", "A Fun click refused: its bubble at the button (FunClicks), so no line on the window's page", Surface.None, Who.Nobody, Who.Player, Who.Player, You, 8, false),
        GameChanger = new("blocked", "A map's game-changer undone", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Fun, 10),
        // the Bots page: a row's change is FunChanged's, as any fun setting's
        BotsPreset = new("botpreset", "Bots: a preset started, and what it set", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Fun, 10),
        BotsOff = new("botsoff", "Bots: Kick all bots, or Default", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Fun, 10),
        // catch-up
        CatchUpBoost = new("boost", "Catch-up's boost", Surface.Card, Who.Player, Who.Player, Who.Player, CatchUp, 5, false),
        Bounty = new("bounty", "The leader's bounty", Surface.Card, Who.Player, Who.Player, Who.Nobody, CatchUp, 5, false),
        // maps, pictures and downloads
        MapCountdown = new("change", "Map change counting down", Surface.Countdown, Who.Everybody, Who.Everybody, Who.Everybody, Maps, 0),
        ServerDownload = new("download", "The server downloading a map", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Maps, 10),
        BackgroundFirst = new("background", "Finishing a background download first", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Maps, 10),
        DownloadFailed = new("dlfailed", "A download failed", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Maps, 10),
        WorkshopDownload = new("workshop", "A Workshop card downloading to the server", Surface.None, Who.Player, Who.Everybody, Who.Everybody, Maps, 0, false),
        PlayAsksFirst = new("asks", "Play asks first (a broken or crashed map): chat too, the only answer a typed !play has", Surface.Line, Who.Player, Who.Player, Who.Nobody, You, 30, false),
        WhyDislike = new("why", "Why the dislike? (the reasons)", Surface.Window, Who.Player, Who.Nobody, Who.Nobody, You, 15, false, Drawn: true),
        // restarts and updates
        RestartAfterMatch = new("restartend", "The server restarts after this match", Surface.Banner, Who.Everybody, Who.Everybody, Who.Everybody, Server, 0),
        RestartCountdown = new("restart", "The restart counting down (10 s)", Surface.Countdown, Who.Everybody, Who.Everybody, Who.Everybody, Server, 0),
        GameUpdate = new("cs2update", "A CS2 update: update your game", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Server, 0),
        Updated = new("updated", "\"Updated to vX\" after the reconnect", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Server, 60),
        UpdatedJoiner = new("updatedjoin", "\"Updated to vX\": a player who joins within 10 minutes", Surface.Line, Who.Player, Who.Player, Who.Player, Server, 15, false),
        PlayerRestart = new("playerrestart", "A player's !restart", Surface.Countdown, Who.Everybody, Who.Everybody, Who.Everybody, Server, 0),
        MatchRestart = new("rematch", "Restart match", Surface.Countdown, Who.Everybody, Who.Everybody, Who.Everybody, Server, 0),
        // server news and health
        News = new("news", "A plain news item", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Server, 8),
        ServerDown = new("down", "The server went down on a map", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Server, 30),
        CrashNote = new("crash", "The joiner's crash note", Surface.Line, Who.Player, Who.Player, Who.Player, Server, 30, false),
        // answers to your own clicks
        Answer = new("answer", "The answer to your own click: refused, click again, already set", Surface.Line, Who.Player, Who.Player, Who.Player, You, 8, false),
        // the chat interface
        CommandReply = new("reply", "Command replies: unknown command, an alias taught, !help's list", Surface.None, Who.Nobody, Who.Player, Who.Nobody, You, 0, false),
        VoteClick = new("voteclick", "Lines for everybody: a vote clicked on a card, with who voted and who still has to", Surface.None, Who.Nobody, Who.Everybody, Who.Everybody, Vote, 0, false);

    public static readonly Kind[] All =
    [
        Welcome, Tagline, WelcomeBack, NowPlaying, Joined, Outdated, Newer, Hold, HoldBack, HoldNoMenu, CheckClick, CheckWarning, VoteWindow, VoteStarted, VoteInactivity, VoteLimit,
        VoteWaiting, VoteDecided, VoteKept, VoteRefused, VoteRefusedYou, FunChanged, FunRandom, FunDefault, FunRefused, GameChanger, BotsPreset, BotsOff, CatchUpBoost, Bounty, MapCountdown, ServerDownload, BackgroundFirst,
        DownloadFailed, WorkshopDownload, PlayAsksFirst, WhyDislike, RestartAfterMatch, RestartCountdown, GameUpdate, Updated, UpdatedJoiner, PlayerRestart, MatchRestart, News,
        ServerDown, CrashNote, Answer, CommandReply, VoteClick,
    ];

    public static Kind? Find(string id) => All.FirstOrDefault(k => k.Id == id);

    // ---- where one message goes: the one decision of both posters, the bridge's Program.Post (Notices.cs) and the plugin's HudScreen.Post ----

    /// <summary>One message of a kind as the catalogue routes it. Text: with its marks and every section's capital (chat, CS2's centre text); Plain: its words
    /// alone, clipped to its surface's room (the top line, a window line, the board); Seconds: how long. Who gets it, 0 for everybody or a player's account,
    /// null for nobody: On, its own surface - the top line, a player's window line, the centre text or the alert (a line for a player not known gets none; every
    /// other surface the live state draws); Chat and Board, those copies.</summary>
    public sealed record Routed(Kind Kind, string Text, string Plain, double Seconds, long? On, long? Chat, long? Board);

    /// <summary>Where a message of this kind goes. account: the player it is for or about, when its kind goes to a player (0: none); seconds: how long, when
    /// not the kind's; chat: false leaves chat out this once (a refreshed line says it only at its steps).</summary>
    public static Routed Route(Kind kind, string text, long account = 0, double? seconds = null, bool chat = true)
    {
        text = Sections.Capitalize(text);
        long? To(Who who) => who == Who.Everybody ? 0 : who == Who.Player && account > 0 ? account : null;
        var on = kind.Drawn ? null : kind.Surface switch
        {
            Surface.Top => 0,
            Surface.Line => kind.On == Who.Player ? To(Who.Player) : null, // a line for everybody is the window's own, which the live state draws
            Surface.Plain or Surface.Alert => To(kind.On),
            _ => (long?)null,
        };
        return new(kind, text, Hud.Clean(M.Plain(text), kind.Surface == Surface.Line ? LineMax : TopMax), seconds ?? (kind.Seconds > 0 ? kind.Seconds : DefaultSeconds),
            on, chat ? To(kind.Chat) : null, To(kind.Board));
    }

    // ---- the words the catalogue's helpers make ----

    /// <summary>A section's words with their first letter a capital.</summary>
    public static string Capital(string words) => words.Length > 0 && char.IsLower(words[0]) ? char.ToUpperInvariant(words[0]) + words[1..] : words;

    /// <summary>What a line from the restart watcher (os_notice) is, by its words: a download's line - the key its refreshed lines share, and which quarter of it this is, for chat - or plain news.</summary>
    public static (Kind Kind, string Key, int Step) OfNotice(string line)
    {
        var name = line.IndexOfAny([':', '(']) is > 0 and var end ? line[..end].TrimEnd() : "";
        var percent = Regex.Match(line, @"(\d+) percent");
        var step = percent.Success ? int.Parse(percent.Groups[1].Value, CultureInfo.InvariantCulture) / 25 : 0;
        if (line.StartsWith("Server is finishing", StringComparison.Ordinal)) return (BackgroundFirst, "background", step);
        if (line.StartsWith("Server is downloading", StringComparison.Ordinal)) return (ServerDownload, "download:" + name, step);
        if (line.StartsWith("The server could not download", StringComparison.Ordinal)) return (DownloadFailed, "", 0);
        return (News, "", 0);
    }

    /// <summary>A Workshop card's download as chat and the board say it; the card keeps its short form.</summary>
    public static string WorkshopLine(string player, string title, int percent, long doneBytes, long totalBytes) =>
        $"{M.N(player)} is downloading {M.A(Hud.Clean(title, 40))} to the server: {M.V($"{percent.ToString(CultureInfo.InvariantCulture)}%")} ({Mb(doneBytes)} of {Mb(totalBytes)})";

    static string Mb(long bytes) => $"{(bytes / 1_000_000).ToString(CultureInfo.InvariantCulture)} MB";

    // ---- how long ago, for the board's Lately list ----

    /// <summary>A line's age as the board says it: Now, 4 min, 2 h.</summary>
    public static string Ago(long atMs, long nowMs)
    {
        var minutes = (nowMs - atMs) / 60000;
        return minutes < 1 ? "Now" : minutes < 60 ? $"{minutes.ToString(CultureInfo.InvariantCulture)} min" : $"{(minutes / 60).ToString(CultureInfo.InvariantCulture)} h";
    }

    /// <summary>A line of the board's Lately list. Account 0: everybody's. Key: a line that replaces its own older version (a download's progress); "" none.</summary>
    public sealed record LatelyLine(long AtMs, long Account, string Key, string Text);

    /// <summary>The Lately rows one player sees: the newest first, a key's newest only, at most LatelyRows.</summary>
    public static List<LatelyLine> LatelyFor(IEnumerable<LatelyLine> lines, long account)
    {
        var shown = new List<LatelyLine>();
        var keys = new HashSet<string>();
        foreach (var line in lines.Where(l => l.Account == 0 || l.Account == account).OrderByDescending(l => l.AtMs))
        {
            if (line.Key != "" && !keys.Add(line.Key)) continue;
            shown.Add(line);
            if (shown.Count == LatelyRows) break;
        }
        return shown;
    }

    /// <summary>One line more for a board's list, a key's older version out, the oldest out at LatelyKeep.</summary>
    public static void AddLately(List<LatelyLine> lines, LatelyLine line)
    {
        if (line.Key != "") lines.RemoveAll(l => l.Key == line.Key && l.Account == line.Account);
        lines.Add(line);
        if (lines.Count > LatelyKeep) lines.RemoveRange(0, lines.Count - LatelyKeep);
    }
}
