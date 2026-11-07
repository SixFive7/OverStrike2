// The HUD's versions, in one place: the one form a version is written in, every content that has shipped with its level, where a player's
// HUD stands against this server's, and the classes that tell a game holding an older content. Shared like Hud.cs (plain BCL): the bridge
// keeps the versions its players' games showed and tests all of it in --selftest; the plugin draws it.
//
// A version is "v4.6.0". A content's level is its line in Contents, never its digits, so a patch, v4.10.0 or v14.5.27 each take the next
// number. A game's HUD is known by the HUD check's click (HudCheck.cs): the version button's id names its content (Button), an older content's
// gold button says Older, and any other button says Unknown.
//
// The classes need no click: latest-N on gg_global names the newest content the server knows, and every content from v4.6.0 on shows its
// outdated banner for an N above its own. v4.5.0, the one content before it still drawn for (Oldest), reads svge46 (the server's level is
// above its own) and svlt45 (below it) at a join instead, and the contents from v4.0.0 to v4.4.0 still get their svge41 ... svge45: their
// red "yours is older" banner. The server's version name rides in the dialog variable osver on gg_global, which the labels inherit.
using System.Globalization;

public static class HudVersion
{
    /// <summary>Every HUD content that has shipped, and on the last line the one this code is for: its version and its level. The level is the number the layouts
    /// and the version classes know a content by (svge/svlt, latest-N). It is a release's place in line and says nothing about the version's digits: a shipped
    /// content keeps its number for good, because its layout and stylesheet hold it, and a release adds one line with the next number, whatever its version says
    /// (hud/PUBLISH.md). Content and Level read the last line. A version with no line is newer than this code knows (LevelOf).</summary>
    public static readonly (string Version, int Level)[] Contents =
    [
        ("v3.0.0", 30),
        ("v3.1.0", 31),
        ("v4.0.0", 40),
        ("v4.1.0", 41),
        ("v4.2.0", 42),
        ("v4.3.0", 43),
        ("v4.4.0", 44),
        ("v4.5.0", 45),
        ("v4.6.0", 46),
        ("v4.7.0", 47),
    ];

    /// <summary>The layout's content on its path (Hud.Layout): the version on Contents' last line. A content update keeps the path, so games holding older contents
    /// stay in use - during Valve's review and until a player's Steam has downloaded the update - and one plugin drives them all: it sends what this content has,
    /// and an older content simply lacks what it lacks (a panel a game does not have takes nothing in it). What v4.5.0, the oldest content still drawn for (Oldest),
    /// needs to show its own pages keeps coming: its page classes, c31 and c32 (it holds the panels those two uncover) and the svge/svlt classes its join banner
    /// reads; nothing comes for what only a content before it reads but its red "yours is older" class (JoinBannerFirst). hud/reference/ keeps the shipped layouts
    /// --selftest holds this one to.</summary>
    public static string Content => Contents[^1].Version;

    /// <summary>The server's content as a number: the level on Contents' last line.</summary>
    public static int Level => Contents[^1].Level;

    /// <summary>The oldest content the plugin still draws for, by its level: v4.5.0. Nothing is sent for what only an older content reads - the marks, counts and
    /// clicks of the Too big and Ads buttons the contents up to v4.3.0 show, the "yours is newer" half of their join banners - but their red "yours is older"
    /// (JoinBannerFirst), so a game holding one runs on what it shares with v4.5.0 and is told at a join to update. v4.5.0 is also the last content whose
    /// banner reads svge/svlt (Classes).</summary>
    public const int Oldest = 45;

    /// <summary>The first content with a join banner, v4.0.0: each content from it to v4.5.0, of level L, shows "yours is older" in red under svge(L + 1) - sent
    /// for every one of them, so a game still holding an older content than Oldest is told to quit CS2 and let Steam update it - and "yours is newer" under
    /// svlt(L), sent for v4.5.0 alone (no server this plugin runs is older than the contents below it).</summary>
    public const int JoinBannerFirst = 40;

    /// <summary>How long after a real join the version banner of a content from v4.0.0 to v4.5.0 shows (vjoin).</summary>
    public const double JoinSeconds = 15;

    /// <summary>The newest HUD content the server knows, as one class on gg_global: latest-N. Every content from v4.6.0 on shows its outdated banner by
    /// itself, from its own stylesheet, for each N from its own level + 1 to LatestLast. A stylesheet never changes once it has shipped, so a release that
    /// raises LatestLast widens only its own content and later ones (hud/PUBLISH.md).</summary>
    public const int LatestLast = 99;
    public static string LatestClass(int level) => $"latest-{level.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The dialog variable on gg_global that the layouts' labels inherit for the server's HUD version.</summary>
    public const string Var = "osver";

    /// <summary>The version classes on gg_global for everybody: the newest content the server knows (latest-N), and what the banners of v4.0.0 to v4.5.0 read
    /// at a join (vjoin on gg_screen, for JoinSeconds) - svge41 ... svge46 while the server's level is above that content's, its red "yours is older", and
    /// svlt45 while it is below v4.5.0's, "yours is newer". The plugin turns the latest class it sent before off itself when the level moves.</summary>
    public static List<(string Id, string Class, bool On)> Classes() => Classes(Level);

    /// <summary>The same at a level css_gghud level fakes.</summary>
    public static List<(string Id, string Class, bool On)> Classes(int level) =>
        [.. Enumerable.Range(JoinBannerFirst + 1, Oldest + 1 - JoinBannerFirst).Select(k => (HudView.Global, $"svge{k}", level >= k)), // "yours is older": v4.0.0's svge41 ... v4.5.0's svge46
         (HudView.Global, $"svlt{Oldest}", level < Oldest), (HudView.Global, LatestClass(level), true)];

    /// <summary>A content's name for its level, as the banner says it: its line in Contents. css_gghud level and content also ask for levels no line has. Above the
    /// last line that is a name no content has, which reads as newer: the last version's next minors (48 is v4.8.0 while v4.7.0 is the last line). Between two lines
    /// it is the older of them, and below the first line the first.</summary>
    public static string OfLevel(int level)
    {
        var (last, top) = Contents[^1];
        if (level > top) return last[1..].Split('.') is [var major, var minor, _] ? $"v{major}.{(int.Parse(minor, CultureInfo.InvariantCulture) + level - top).ToString(CultureInfo.InvariantCulture)}.0" : last;
        for (var i = Contents.Length - 1; i > 0; i--) if (Contents[i].Level <= level) return Contents[i].Version;
        return Contents[0].Version;
    }

    // ---- a version word: what the HUD check's click says a game's HUD is ----

    /// <summary>A game's HUD as known by an older content's gold button: older than v4.6.0, which content not said.</summary>
    public const string Older = "older";

    /// <summary>A game that showed the HUD by a click on a button that names no content: verified, which content not known. On the wire it is the layout
    /// release's own version, v3.0.0 (Hud.ReleaseFile's), which the plugin and the bridge of v4.6.0 send; content v3.0.0 itself never says its name, so the
    /// word cannot be mistaken for it.</summary>
    public const string Unknown = "v3.0.0";

    /// <summary>The level of a version with no line in Contents: newer than this bridge or plugin knows, so above every level there is.</summary>
    public const int Newer = int.MaxValue;

    /// <summary>"v2.1.1", "v14.5.27": the one form a version is shown, logged, compared and sent in - by the version buttons of every content that has one
    /// (v4.6.0 on), and by the plugin and the bridge since 5.0.0.</summary>
    public static bool Is(string text)
    {
        var parts = text.Length > 1 && text[0] == 'v' ? text[1..].Split('.') : [];
        return parts.Length == 3 && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit));
    }

    /// <summary>Whether !hudok and the live state's ok lines may carry this: a version, or Older.</summary>
    public static bool IsWord(string text) => text == Older || Is(text);

    /// <summary>A content as a number: its line in Contents, never its digits (v4.6.0 is 46); Newer for a version with no line; 0 for a word that is no version.</summary>
    public static int LevelOf(string version)
    {
        if (!Is(version)) return 0;
        foreach (var (known, level) in Contents) if (known == version) return level;
        return Newer;
    }

    /// <summary>Whether this version is known to be a content older than this level: a shipped one below it, or Older. Unknown is not known to be anything.</summary>
    public static bool IsBefore(string version, int level) => version == Older || Is(version) && version != Unknown && LevelOf(version) < level;

    /// <summary>Whether a known version is older than this server's content.</summary>
    public static bool IsOutdated(string version) => IsBefore(version, Level);

    /// <summary>A HUD as the Players page says it when it is not the server's own: older, its content not known (Unknown), not seen yet ("": no click since
    /// the last real join), or its version - with what to do when it is older.</summary>
    public static string Words(string version) => version == Older ? "older - Restart CS2" : version == "" ? "not seen yet"
        : version == Unknown ? "version unknown" : LevelOf(version) < Level ? $"{version} - Restart CS2" : version;

    // ---- the version button: the gold "Click to play" of the HUD check, whose id names the content that has it ----

    /// <summary>The prefix of a version button's id. The layout may put the button anywhere (it is the welcome window's X): any button with this prefix proves its content.</summary>
    public const string ButtonPrefix = "gg_hudver_";

    /// <summary>A content's version button: gg_hudver_v4.7.0, the one place that says the id's form; --selftest holds the layout's one id to it. v4.6.0's layout,
    /// which has its own, loads in a real client (the documentation's screenshots); a click on it is in the bridge's log as that version (hud check: ... verified,
    /// HUD v4.6.0).</summary>
    public static string Button(string content) => ButtonPrefix + content;

    /// <summary>The content a version button proves, or null for any other button.</summary>
    public static string? OfButton(string buttonId) =>
        buttonId.StartsWith(ButtonPrefix, StringComparison.Ordinal) && buttonId[ButtonPrefix.Length..] is var content && Is(content) ? content : null;
}
