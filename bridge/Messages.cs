// THE MESSAGE CATALOGUE: every message a player can get, one line each - which surface it goes on, who sees it there, whether chat and the TAB board
// carry a copy and for whom, its category and how long it stays. A change of mind about a message is one line here. The bridge posts through
// Program.Post (Notices.cs), the plugin through HudScreen.Post; both read this table, so one message cannot go two ways.
//
// A message's text is written once, with its functional parts marked (M): chat renders the marks in the chat's functional colours, every other surface
// takes the plain words (M.Plain). Every section of a message starts with a capital - after " - ", after " · " and on a second line - and --selftest holds
// every template to it (Sections).
//
// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>The functional parts of a message, marked inside its text with private-use characters: nothing a player can type survives Hud.Clean as one
/// of them (Unmark), and they are stripped from every surface but chat.</summary>
public static class M
{
    const char NameStart = '', ArgStart = '', ValueStart = '', CommandStart = '', End = '', RefusedLine = '', DoneLine = '', Break = '';

    static bool IsMark(char c) => c is >= '' and <= '';

    /// <summary>Text from outside (a player's name, a map's title) with any mark taken out: nobody colours a line by naming themselves so.</summary>
    public static string Unmark(string? text) => string.Concat((text ?? "").Where(c => !IsMark(c)));

    /// <summary>A player's name: their team's colour in chat.</summary>
    public static string N(string name) => $"{NameStart}{Unmark(name)}{End}";

    /// <summary>What a command or a setting is about: "gravity", a map, a page.</summary>
    public static string A(string argument) => $"{ArgStart}{Unmark(argument)}{End}";

    /// <summary>A value: "Moon", "41%", "2 min".</summary>
    public static string V(string value) => $"{ValueStart}{Unmark(value)}{End}";

    /// <summary>A chat command: "!help".</summary>
    public static string C(string command) => $"{CommandStart}{Unmark(command)}{End}";

    /// <summary>A whole line that refuses something: its plain words are the refusal's colour in chat.</summary>
    public static string Refused(string line) => RefusedLine + line;

    /// <summary>A whole line that says it worked: its plain words are the done colour in chat.</summary>
    public static string Done(string line) => DoneLine + line;

    /// <summary>Whether this place in a marked line lies inside a marked part: a name or a map's title, which stay as they are written.</summary>
    public static bool Inside(string marked, int at)
    {
        for (var i = at - 1; i >= 0; i--)
        {
            if (marked[i] == End) return false;
            if (marked[i] is NameStart or ArgStart or ValueStart or CommandStart) return true;
        }
        return false;
    }

    /// <summary>The words alone, for every surface but chat.</summary>
    public static string Plain(string? text) => string.Concat((text ?? "").Where(c => !IsMark(c)));

    /// <summary>Marked text with the dashes in a name's or an argument's own words out: a map's title is somebody else's text, and a " - " in it ends no section of ours (Sections).</summary>
    public static string OwnSections(string marked)
    {
        var s = new StringBuilder(marked.Length);
        var theirs = false;
        foreach (var c in marked)
        {
            if (c is NameStart or ArgStart) theirs = true;
            else if (c == End) theirs = false;
            s.Append(theirs && c is '-' or '·' ? ' ' : c);
        }
        return s.ToString();
    }

    /// <summary>A line break that survives the wire (Hud.Clean takes newlines out): CS2's plain centre text and alert break on it (Lines).</summary>
    public static string Lines(params string[] lines) => string.Join(Break, lines);

    /// <summary>The centre text's words from the wire: its line breaks back, no other mark.</summary>
    public static string Centre(string? text) => string.Concat((text ?? "").Select(c => c == Break ? '\n' : c).Where(c => !IsMark(c)));

    /// <summary>The colours chat uses, as the game's colour bytes: the plugin hands them over (CounterStrikeSharp's ChatColors), --selftest fakes them.
    /// NameColour: a player's team colour by name, or null when nobody on the server has that name.</summary>
    public sealed record Palette(char Plain, char Command, char Argument, char Value, char Refusal, char Done, Func<string, char?> NameColour);

    /// <summary>A marked line as chat shows it.</summary>
    public static string Render(string marked, Palette p)
    {
        var s = new StringBuilder();
        var plain = p.Plain;
        var at = 0;
        if (marked.StartsWith(RefusedLine)) { plain = p.Refusal; at = 1; }
        else if (marked.StartsWith(DoneLine)) { plain = p.Done; at = 1; }
        if (plain != p.Plain) s.Append(plain);
        var name = -1;
        for (; at < marked.Length; at++)
        {
            var c = marked[at];
            switch (c)
            {
                case NameStart: name = s.Length; break;
                case ArgStart: s.Append(p.Argument); break;
                case ValueStart: s.Append(p.Value); break;
                case CommandStart: s.Append(p.Command); break;
                case End:
                    if (name >= 0) { if (p.NameColour(s.ToString(name, s.Length - name)) is { } team) s.Insert(name, team); name = -1; }
                    s.Append(plain);
                    break;
                case Break: s.Append(" - "); break;
                case RefusedLine or DoneLine: break;
                default: s.Append(c); break;
            }
        }
        return s.ToString().Replace("%s", "% s", StringComparison.Ordinal); // CS2 fills %s1 in a chat line with nothing
    }

    /// <summary>A marked line as chat lines of at most max characters a player sees: cut after a section where it can be, else at a space. A mark that a cut
    /// leaves open ends with its line. A refusal's or a done line's colour goes on with the second line.</summary>
    public static List<string> ChatLines(string marked, int max = Messages.ChatLineMax)
    {
        var lines = new List<string>();
        var rest = marked;
        var lead = marked.Length > 0 && marked[0] is RefusedLine or DoneLine ? marked[0].ToString() : "";
        while (Plain(rest).Length > max)
        {
            var seen = 0;
            var cut = -1;
            for (var i = 0; i < rest.Length && seen <= max; i++)
            {
                if (IsMark(rest[i])) continue;
                seen++;
                if (rest[i] == ' ' && seen <= max) cut = i;
            }
            if (cut <= 0) break;
            lines.Add(rest[..cut].TrimEnd());
            rest = lead + rest[(cut + 1)..].TrimStart();
        }
        lines.Add(rest);
        return lines;
    }
}

/// <summary>Where a message shows. Window: the window on its own page. Line: a line on the window's page (the player's own, or the page's own place for it).
/// Card: our card under the crosshair. Plain: CS2's plain centre text. Alert: CS2's alert text. Banner: the status banner. Top: the top line. Countdown: the
/// countdown banner. None: no surface of its own, chat or the board only.</summary>
public enum Surface { None, Window, Line, Card, Plain, Alert, Banner, Top, Countdown }

/// <summary>Who sees a copy: nobody, the player it is about, or everybody.</summary>
public enum Who { Nobody, Player, Everybody }

public sealed record Category(string Id, string Name)
{
    /// <summary>The class that colours the top line's tab, the window line's bar and eyebrow.</summary>
    public string Class => "cat-" + Id;
}

public static class Messages
{
    public const int ChatLineMax = 120; // characters a chat line holds on a 1920x1080 screen
    public const double DefaultSeconds = 8;
    public const int TopMax = 240, LineMax = 140, CentreMax = 80, LatelyRows = 3, LatelyKeep = 12, SayKeepSeconds = 20;

    public static readonly Category Join = new("join", "Join"), YourHud = new("hud", "Your HUD"), Vote = new("vote", "Vote"), Fun = new("fun", "Fun"),
        Maps = new("maps", "Maps"), Server = new("server", "Server"), CatchUp = new("catchup", "Catch-up"), You = new("you", "You");

    public static readonly Category[] Categories = [Join, YourHud, Vote, Fun, Maps, Server, CatchUp, You];

    public static Category? CategoryOf(string id) => Categories.FirstOrDefault(c => c.Id == id);

    /// <summary>A kind of message: what it is, its surface and who sees it there, who gets a chat copy and a board copy, its category and how long it
    /// stays (0: as long as its state lasts). News: whether the news line of the contents before v4.6.0 carries it as well.</summary>
    public sealed record Kind(string Id, string Row, Surface Surface, Who On, Who Chat, Who Board, Category Cat, double Seconds, bool News = true);

    // ---- the catalogue. A message that went has no kind ----

    public static readonly Kind
        // joining
        Welcome = new("welcome", "Welcome window, at every login", Surface.Window, Who.Player, Who.Nobody, Who.Nobody, Join, 0, false),
        Tagline = new("tagline", "Join tagline", Surface.Line, Who.Player, Who.Player, Who.Nobody, Join, 3, false),
        WelcomeBack = new("back", "Welcome-back card", Surface.Line, Who.Player, Who.Nobody, Who.Nobody, Join, 8, false),
        NowPlaying = new("playing", "Now-playing card, at each map's first spawn", Surface.Card, Who.Everybody, Who.Nobody, Who.Everybody, Join, 8, false),
        Joined = new("joined", "A player joined with a HUD version", Surface.Plain, Who.Everybody, Who.Everybody, Who.Everybody, Join, 8),
        // your HUD and its version
        Outdated = new("outdated", "Your HUD is out of date: the banner that stays", Surface.Banner, Who.Player, Who.Nobody, Who.Nobody, YourHud, 0, false),
        Newer = new("newer", "Your HUD is newer than the server's", Surface.Banner, Who.Player, Who.Nobody, Who.Nobody, YourHud, 15, false),
        Hold = new("hold", "The HUD not shown by Steam", Surface.Banner, Who.Everybody, Who.Nobody, Who.Nobody, YourHud, 45),
        HoldBack = new("holdback", "The HUD not shown by Steam: back", Surface.Banner, Who.Everybody, Who.Nobody, Who.Nobody, YourHud, 30),
        HoldNoMenu = new("nomenu", "A player without the HUD during a hold", Surface.None, Who.Nobody, Who.Player, Who.Nobody, YourHud, 0, false),
        CheckClick = new("check", "HUD check: Click to play", Surface.Window, Who.Player, Who.Nobody, Who.Nobody, Join, 0, false),
        CheckWarning = new("checkwarn", "HUD check: the disconnect warning", Surface.Alert, Who.Player, Who.Player, Who.Nobody, YourHud, 15, false),
        // the vote
        VoteWindow = new("votewin", "The vote: everybody's window opens and freezes", Surface.Window, Who.Everybody, Who.Nobody, Who.Nobody, Vote, 0, false),
        VoteStarted = new("votestart", "The vote started: the line for everybody", Surface.Line, Who.Everybody, Who.Everybody, Who.Everybody, Vote, 0),
        VoteInactivity = new("voteidle", "Inactivity timer, and who reset it", Surface.Line, Who.Everybody, Who.Nobody, Who.Nobody, Vote, 0, false),
        VoteLimit = new("votelimit", "The 60 s limit", Surface.Line, Who.Everybody, Who.Nobody, Who.Nobody, Vote, 0, false),
        VoteWaiting = new("votewait", "Waiting for: the players who have not voted (chat and the board: VoteClick's line says it)", Surface.Line, Who.Everybody, Who.Nobody, Who.Nobody, Vote, 0, false),
        VoteDecided = new("votedone", "The vote decided, and the change counting down", Surface.Line, Who.Everybody, Who.Everybody, Who.Everybody, Vote, 0),
        VoteKept = new("votekept", "The vote kept the map", Surface.Line, Who.Everybody, Who.Everybody, Who.Everybody, Vote, 8),
        VoteRefused = new("voteno", "You cannot call a vote yet", Surface.Line, Who.Player, Who.Everybody, Who.Everybody, Vote, 8, false),
        VoteRefusedYou = new("votenoyou", "You cannot call a vote yet: the player's own line, in their own words", Surface.Line, Who.Player, Who.Nobody, Who.Nobody, Vote, 8, false),
        // fun and game settings
        FunChanged = new("fun", "Fun: one setting changed (catch-up and the grenade drops the same)", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Fun, 10),
        FunRandom = new("funrandom", "Fun: Random", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Fun, 10),
        FunDefault = new("fundefault", "Fun: Default", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Fun, 10),
        GameChanger = new("blocked", "A map's game-changer undone", Surface.Top, Who.Everybody, Who.Everybody, Who.Everybody, Fun, 10),
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
        WhyDislike = new("why", "Why the dislike? (the reasons)", Surface.Window, Who.Player, Who.Nobody, Who.Nobody, You, 15, false),
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
        VoteWaiting, VoteDecided, VoteKept, VoteRefused, VoteRefusedYou, FunChanged, FunRandom, FunDefault, GameChanger, CatchUpBoost, Bounty, MapCountdown, ServerDownload, BackgroundFirst,
        DownloadFailed, WorkshopDownload, PlayAsksFirst, WhyDislike, RestartAfterMatch, RestartCountdown, GameUpdate, Updated, UpdatedJoiner, PlayerRestart, MatchRestart, News,
        ServerDown, CrashNote, Answer, CommandReply, VoteClick,
    ];

    public static Kind? Find(string id) => All.FirstOrDefault(k => k.Id == id);

    // ---- the words the catalogue's helpers make ----

    /// <summary>A section's words with their first letter a capital.</summary>
    public static string Capital(string words) => words.Length > 0 && char.IsLower(words[0]) ? char.ToUpperInvariant(words[0]) + words[1..] : words;

    /// <summary>What a line from the restart watcher (gg_notice) is, by its words: a download's line - the key its refreshed lines share, and which quarter of it this is, for chat - or plain news.</summary>
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

    /// <summary>One line more for a board's list, a key's older version out, the oldest out at LatelyKeep. Returns whether the line is new to chat too (a line
    /// with a key says so only when asked: its chat copy has its own step).</summary>
    public static void AddLately(List<LatelyLine> lines, LatelyLine line)
    {
        if (line.Key != "") lines.RemoveAll(l => l.Key == line.Key && l.Account == line.Account);
        lines.Add(line);
        if (lines.Count > LatelyKeep) lines.RemoveRange(0, lines.Count - LatelyKeep);
    }
}

/// <summary>Every section of a message starts with a capital, held by --selftest over every template and everything said while it runs. A
/// section is what stands between " - ", " · " and line breaks. What may start one besides a capital: a digit, a sign (+60% HP, #1, !help), a name (a word
/// with an underscore or a digit in it: a map's name stays as it is written).</summary>
public static partial class Sections
{
    [GeneratedRegex(@"\s+-\s+|\s+·\s+|\n|\r")] private static partial Regex Between();

    /// <summary>The sections of a text.</summary>
    public static IEnumerable<string> Of(string text) => Between().Split(M.Plain(M.OwnSections(text)).Replace('', '\n')).Select(s => s.Trim()).Where(s => s != "");

    /// <summary>The first section that starts wrong, or null. allowed: words a section may start with as they are (the names in a test's words).</summary>
    public static string? Lower(string text, IReadOnlyCollection<string>? allowed = null)
    {
        foreach (var section in Of(text))
        {
            var first = section[0];
            if (!char.IsLetter(first) || char.IsUpper(first)) continue;
            var word = section.Split(' ', 2)[0];
            if (word.Any(c => c == '_' || char.IsDigit(c))) continue;
            if (allowed != null && allowed.Any(a => section.StartsWith(a, StringComparison.Ordinal))) continue;
            return section;
        }
        return null;
    }

    /// <summary>The first word after every " - ", " · " and line break a capital, a name left as it is written (marked, or with an underscore or a digit in it): the net
    /// under every message, for a template no self-test reaches. A marked part stays whole, also a map's title with " - " in it. The line's own first word stays: it is
    /// usually a name. A line from outside (the restart watcher's) also goes through Messages.Capital.</summary>
    public static string Capitalize(string text) => Starts().Replace(text, m => M.Inside(text, m.Index) ? m.Value : m.Groups[1].Value + (m.Groups[2].Value.Any(c => c == '_' || char.IsDigit(c)) ? m.Groups[2].Value : Messages.Capital(m.Groups[2].Value)));

    [GeneratedRegex(@"(\s+-\s+|\s+·\s+|\n)(\S+)")] private static partial Regex Starts();
}
