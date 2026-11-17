// The chat column (HUD content v4.14.0): our own chat history beside the window, taller than CS2's and there whether the window is open or not. CS2's own
// chat cannot be made taller or kept on screen: its history box is 327 px high in its own stylesheet (styles/hud/hudchat.css), which this HUD's stylesheet
// cannot reach, and no setting a server may change keeps its lines. So the plugin keeps what it sees - every chat line a player sends as CS2 shows it (say and
// say_team; a / line is CounterStrikeSharp's silent trigger, which CS2 does not show), every line of its own (for everybody or for one player), joins, leaves
// and team changes - and the frame draws the newest that each player may see, the newest at the bottom, over CS2's own history and up toward the radar;
// CS2's typing line stays free below it. A team's line goes to that team alone, as CS2's does.
//
// The column shows only while the plugin feeds it (chaton on os_screen) and keeps beating (hb, on and off by turns every BeatSeconds): a stylesheet animation
// hides the column a while after the last swap, so a plugin that stops - unloaded, reloaded, crashed - or a game whose server never feeds it shows CS2's own
// chat again. A bridge that stops answering turns chaton off at once (the plugin's ChatLive).
//
// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Globalization;

public sealed class HudChat
{
    /// <summary>When the column shows, the server's choice (the owner's 277): Always (route a1 - solid over CS2's chat area, the window open or not), Window (route c - only
    /// while our window is open; CS2's own chat the rest of the time) or Never (route d - CS2's own chat alone). The stylesheet is the same for all three, so a change
    /// needs no content update: Default is the plugin's, and os_hud chat always|window|off overrides it until the plugin reloads.</summary>
    public enum Route { Always, Window, Never }
    public const Route Default = Route.Always;

    /// <summary>Whether the column shows by its route, with the window open or not.</summary>
    public static bool Shows(Route route, bool windowOpen) => route == Route.Always || route == Route.Window && windowOpen;

    /// <summary>The column's lines; the newest is the last, at the bottom.</summary>
    public const int Lines = 18;

    /// <summary>How many lines the plugin keeps, newest last.</summary>
    public const int Keep = 60;

    /// <summary>The first content with the column: only a game whose HUD check named it or a later one gets its words (Feeds) - any other has no labels for them,
    /// or may have none, and would log "Unable to find panel" for each (release 193; a v4.13.0 game logged 36 of them before its check, the live lab, release 218).</summary>
    public const int Level = 56;

    /// <summary>Whether a game gets the column: its HUD check has named a content, and that is v4.14.0 or later (a version newer than this code knows too).</summary>
    public static bool Feeds(string version) => HudVersion.Is(version) && version != HudVersion.Unknown && !HudVersion.IsBefore(version, Level);

    /// <summary>How long one beat lasts: hb goes on and off by turns every this many seconds while the plugin feeds the column, each turn restarting the stylesheet's
    /// animation, which runs 35 s - three beats and a half - so a missed frame or two never hides it.</summary>
    public const int BeatSeconds = 10;

    /// <summary>The line panels, 1 the top and Lines the newest at the bottom, and each line's words.</summary>
    public static string Line(int k) => $"os_chat_{k.ToString(CultureInfo.InvariantCulture)}";
    public static string Words(int k) => $"os_chat_{k.ToString(CultureInfo.InvariantCulture)}_t";

    /// <summary>What a line is: a player's line to everybody, a player's line to their team, one of the server's (to everybody or to one player), or an event
    /// (a join, a leave, a team change).</summary>
    public enum Kind { Chat, Team, Server, Event }

    /// <summary>One line: its kind; the sender's name and team (CT, T or SPEC) for a player's line - a team line goes to that team alone, and the team colours the
    /// line's mark; the words; for a server line, the one account it is for (0: everybody).</summary>
    public sealed record Entry(long Seq, Kind Kind, string Name, string Team, string Text, long To);

    /// <summary>A line's look on its panel: the sender's team for a player's line (k-ct, k-t; a spectator's none), k-team besides for a line to the team, the server's,
    /// an event's.</summary>
    public static readonly string[] KindClasses = ["k-ct", "k-t", "k-team", "k-srv", "k-evt"];

    readonly List<Entry> entries = [];
    long seq;

    /// <summary>Moves with every line kept.</summary>
    public long Version => seq;

    public IReadOnlyList<Entry> All => entries;

    /// <summary>A player's chat line as CS2 shows it: to everybody, or to the sender's team alone.</summary>
    public void Say(string name, string team, string text, bool teamOnly) => Add(new(0, teamOnly ? Kind.Team : Kind.Chat, Hud.Clean(name, 32), TeamOf(team), Hud.Clean(text, 127), 0));

    /// <summary>A line of the server's own: for everybody (to 0) or for one account.</summary>
    public void Server(string text, long to = 0) => Add(new(0, Kind.Server, "", "", Hud.Clean(text, 160), to));

    /// <summary>A join, a leave or a team change, for everybody.</summary>
    public void Event(string text) => Add(new(0, Kind.Event, "", "", Hud.Clean(text, 120), 0));

    void Add(Entry e)
    {
        if (e.Text == "") return;
        entries.Add(e with { Seq = ++seq });
        if (entries.Count > Keep) entries.RemoveAt(0);
    }

    static string TeamOf(string team) => team is "CT" or "T" ? team : "SPEC";

    /// <summary>Whether this player sees the line, as CS2 would show it to them: a team line only within its team, a server line for everybody or for them alone.</summary>
    public static bool Sees(Entry e, long account, string team) => e.Kind switch
    {
        Kind.Team => e.Team == TeamOf(team),
        Kind.Server => e.To == 0 || e.To == account,
        _ => true,
    };

    /// <summary>The newest lines this player sees, oldest first: at most Lines.</summary>
    public List<Entry> For(long account, string team) => [.. entries.Where(e => Sees(e, account, team)).TakeLast(Lines)];

    /// <summary>A line's words as the column shows them: "Name: words" for a player's line to everybody, "(Team) Name: words" for a team line, the words alone
    /// for the server's and for an event.</summary>
    public static string Text(Entry e) => e.Kind switch
    {
        Kind.Chat => $"{e.Name}: {e.Text}",
        Kind.Team => $"(Team) {e.Name}: {e.Text}",
        _ => e.Text,
    };

    /// <summary>A line's look: the classes of KindClasses that are on for it.</summary>
    public static HashSet<string> Look(Entry e) => e.Kind switch
    {
        Kind.Chat or Kind.Team => [.. new[] { e.Team == "CT" ? "k-ct" : e.Team == "T" ? "k-t" : "", e.Kind == Kind.Team ? "k-team" : "" }.Where(x => x != "")],
        Kind.Server => ["k-srv"],
        _ => ["k-evt"],
    };

    /// <summary>The column for one player: its Lines panels bottom up - the newest on the last - each with its words, used while it holds a line, and its look.</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) Column(IReadOnlyList<Entry> shown)
    {
        var t = new List<(string, string)>();
        var c = new List<(string, string, bool)>();
        var first = Lines - Math.Min(shown.Count, Lines) + 1;
        var from = Math.Max(0, shown.Count - Lines);
        for (var k = 1; k <= Lines; k++)
        {
            var e = k >= first ? shown[from + k - first] : null;
            t.Add((Words(k), e == null ? "" : Text(e)));
            c.Add((Line(k), "used", e != null));
            var look = e == null ? [] : Look(e);
            foreach (var cls in KindClasses) c.Add((Line(k), cls, look.Contains(cls)));
        }
        return (t, c);
    }

    /// <summary>The beat for this moment: hb on in even beats, off in odd ones (BeatSeconds each), by the plugin's clock.</summary>
    public static bool BeatA(DateTime now) => now.Ticks / TimeSpan.FromSeconds(BeatSeconds).Ticks % 2 == 0;

    /// <summary>The chat line of a join, a leave and a team change, as the column says them.</summary>
    public static string Joined(string name) => $"{Hud.Clean(name, 32)} joined the game";
    public static string Left(string name) => $"{Hud.Clean(name, 32)} left the game";
    public static string TeamChanged(string name, string team) => $"{Hud.Clean(name, 32)} joined the {team switch { "CT" => "Counter-Terrorists", "T" => "Terrorists", _ => "Spectators" }}";
}
