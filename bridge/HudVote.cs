// Over Strike 2's screens that every HUD content can show: the vote for the next map - the only way a player changes the map; the bridge's side
// in VoteRound.cs and MapVote.cs - drawn on the Maps page's cards, so a game still holding an older content can vote too, during Valve's review of
// an update and until it has downloaded it; the HUD's version classes; and why a player disliked a map (DownReasons).
//
// The version classes need no click and no word from the client. latest-N on gg_global names the newest content the server knows, and every
// content from v4.6.0 on shows its outdated banner for an N above its own. The contents before it read svge<k> (the server's level is at least k)
// and svlt<k> (below k) at a join instead. The server's version name rides in the dialog variable osver on gg_global, which the labels inherit.
//
// Shared like Hud.cs (plain BCL): the plugin draws it, the bridge's --selftest checks it.
using System.Globalization;

/// <summary>Why a player disliked a map: one mark per player per map, whose value in votes.json says why - every one of them that player's dislike
/// wherever likes count. A plain Dislike is "I did not like the map". Too big and Ads have their own effects (Selection.cs); the rest are kept and
/// count as a dislike, nothing more. Chat steers it directly: !down big, !down 12 big. The same words on /act set the reason - the popup's click
/// too, "!down big", one command for both. Repeating a plain !down on a map already disliked keeps its reason and says so. A first plain !down opens
/// the popup that lists the reasons (Votes.cs, HudVote.WhyWords) for PopupSeconds: choosing one, or typing its word, updates that mark - without a
/// map, a reason goes to the map the popup asks about while it is open.</summary>
public static class DownReasons
{
    /// <summary>A reason: the word chat uses, the vote's value, what the popup says, and how a note names it.</summary>
    public sealed record Reason(string Word, int Value, string Label, string Short);

    public const int Plain = -1; // votes.json's plain downvote, since long before there were reasons

    /// <summary>How long the popup stands, and how long after that a click on its way still counts. The drain line runs a second shorter (HudView.PlayerClasses;
    /// the stylesheet's .why-run).</summary>
    public const double PopupSeconds = 15, PopupGraceSeconds = 2;

    public static readonly Reason[] All =
    [
        new("meh", Plain, "I did not like the map", "down"),
        new("big", HudCatalog.TooBigVote, "Map too big", "too big"),
        new("ads", HudCatalog.AdsVote, "Shows ads or sends players away", "ads"),
        new("small", HudCatalog.SmallVote, "Map too small", "too small"),
        new("broken", HudCatalog.BrokenVote, "Map is broken", "broken"),
        new("slow", HudCatalog.SlowVote, "Runs badly", "runs badly"),
    ];

    /// <summary>The reasons chat can name beyond the plain one, as a player reads them: "big, ads, small, broken or slow".</summary>
    public static string Words => string.Join(", ", All.Skip(1).SkipLast(1).Select(r => r.Word)) + " or " + All[^1].Word;

    public static Reason? Find(string word) => All.FirstOrDefault(r => r.Word == word);

    public static Reason? Of(int value) => All.FirstOrDefault(r => r.Value == value);

    /// <summary>What follows !down on the wire, split: "12 big" is that map and that reason, "big" the map that is on, "12" a plain vote on that map.</summary>
    public static (string Map, Reason? Reason) Split(string arg)
    {
        var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p switch
        {
            [var one] when Find(one) is { } r => ("", r),
            [var map, var word] when Find(word) is { } r => (map, r),
            _ => (arg.Trim(), null),
        };
    }

    /// <summary>What a chat line may say after !down, as the bridge takes it: "", a map, a reason, or both in either order - "12 big"; null when
    /// it is anything else.</summary>
    public static string? Normal(string low)
    {
        var p = low.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p switch
        {
            [] => "",
            [var one] when Find(one) != null || Hud.IsMapArg(one) => one,
            [var map, var word] when Hud.IsMapArg(map) && Find(word) != null => $"{map} {word}",
            [var word, var map] when Hud.IsMapArg(map) && Find(word) != null => $"{map} {word}",
            _ => null,
        };
    }

    /// <summary>Whether a click may say this after !down: a reason, or a map and a reason. A map alone is every vote verb's own (Hud.IsAction).</summary>
    public static bool IsArg(string arg) => Normal(arg) is { Length: > 0 } n && n == arg && Split(arg).Reason != null;
}

public static class HudVote
{
    /// <summary>Every HUD content that has shipped, and on the last line the one this code is for: its version and its level. The level is the number the layouts
    /// and the version classes know a content by (svge/svlt, latest-N). It is a release's place in line and says nothing about the version's digits: a shipped
    /// content keeps its number for good, because its layout and stylesheet hold it, and a release adds one line with the next number, whatever its version says
    /// (hud/PUBLISH.md). Hud.Content and ContentLevel read the last line. A version with no line is newer than this code knows (HudCheck.LevelOf).</summary>
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
    ];

    /// <summary>The server's content as a number: the level on Contents' last line.</summary>
    public static int ContentLevel => Contents[^1].Level;

    /// <summary>The newest HUD content the server knows, as one class on gg_global: latest-N. Every content from v4.6.0 on shows its outdated banner by
    /// itself, from its own stylesheet, for each N from its own level + 1 to LatestLast: this one for latest-47 ... latest-59, the next 13 releases. A stylesheet
    /// never changes once it has shipped, so a release that raises LatestLast widens only its own content and later ones (hud/PUBLISH.md).</summary>
    public const int LatestLast = 59;
    public static string LatestClass(int level) => $"latest-{level.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The version classes the contents before v4.6.0 read at a join (vjoin on gg_screen, for JoinSeconds): svge(k) while the server's
    /// level is at least k, svlt(k) while it is below - a content of level L shows "yours is older" under svge(L + 1) and "yours is newer" under
    /// svlt(L). Those contents are v4.0.0 to v4.5.0, so only svge41 ... svge46 and svlt40 ... svlt45 are sent.</summary>
    public const int OldFirst = 40, OldLast = 45;

    /// <summary>The dialog variables on gg_global that the layouts' labels inherit: the server's HUD version and the game mode's name.</summary>
    public const string VersionVar = "osver", ModeVar = "osmode";

    /// <summary>A content's name for its level, as the banner says it: its line in Contents. css_gghud level and content also ask for levels no line has. Above the
    /// last line that is a name no content has, which reads as newer: the last version's next minors (47 is v4.7.0 while v4.6.0 is the last line). Between two lines
    /// it is the older of them, and below the first line the first.</summary>
    public static string ContentOfLevel(int level)
    {
        var (last, top) = Contents[^1];
        if (level > top) return last[1..].Split('.') is [var major, var minor, _] ? $"v{major}.{(int.Parse(minor, CultureInfo.InvariantCulture) + level - top).ToString(CultureInfo.InvariantCulture)}.0" : last;
        for (var i = Contents.Length - 1; i > 0; i--) if (Contents[i].Level <= level) return Contents[i].Version;
        return Contents[0].Version;
    }

    /// <summary>How long after a real join an older content's version banner shows (vjoin).</summary>
    public const double JoinSeconds = 15;

    /// <summary>The version classes on gg_global for everybody: the older contents' svge/svlt and the newest content the server knows (latest-N).
    /// The plugin turns the latest class it sent before off itself when the level moves (css_gghud level).</summary>
    public static List<(string Id, string Class, bool On)> VersionClasses() => VersionClasses(ContentLevel);

    /// <summary>The same for a level css_gghud level fakes.</summary>
    public static List<(string Id, string Class, bool On)> VersionClasses(int level)
    {
        var c = new List<(string, string, bool)>();
        for (var k = OldFirst + 1; k <= OldLast + 1; k++) c.Add((HudView.Global, $"svge{k}", level >= k));
        for (var k = OldFirst; k <= OldLast; k++) c.Add((HudView.Global, $"svlt{k}", level < k));
        c.Add((HudView.Global, LatestClass(level), true));
        return c;
    }

    /// <summary>The game mode's classes on gg_global (mode-armsrace, mode-deathmatch, mode-wingman): the layouts hide what is Arms Race's own outside
    /// it - friendly fire's rows on the Fun page, the match length on the Game page.</summary>
    public static List<(string Id, string Class, bool On)> ModeClasses(string mode) =>
        Modes.All.Select(m => (HudView.Global, $"mode-{m.Id}", m.Id == mode)).ToList();

    /// <summary>A card's mode, as classes on the card (the label on its picture and its colour - the same three names as gg_global's, on another
    /// panel): mode-armsrace, mode-deathmatch, mode-wingman, exactly one on for a card in use.</summary>
    public static IEnumerable<(string Id, string Class, bool On)> CardModeClasses(string card, string? mode) =>
        Modes.All.Select(m => (card, $"mode-{m.Id}", m.Id == mode));

    // ---- a card's tags (MapTags): every tag that applies, in precedence order, as chips beside the picture ----

    /// <summary>The classes on a card that show its chips. The vote's state and the cautions use the classes every content has: now (NEXT MAP on the
    /// ballot, ON NOW in the browser), vlead and vtie, big, ads, queued (PICKED, the ballot's) and ours; the rest are t-&lt;id&gt;, which an older
    /// content has no rule for.</summary>
    public static readonly string[] TagClasses = ["t-liked", "t-upload", "t-updated", "t-trending", "t-toprated", "t-popular", "t-newtous"];

    static string TagClass(string id) => $"t-{id}";

    /// <summary>The t-* classes of a card from its tags on the wire (the catalog's field 24, a ballot card's mvo field 11).</summary>
    public static IEnumerable<(string Id, string Class, bool On)> TagClassesOf(string card, string? wire)
    {
        var tags = MapTags.Split(wire).ToHashSet();
        foreach (var cls in TagClasses) yield return (card, cls, tags.Contains(cls[2..]));
    }

    /// <summary>Whether the wire's tags name this one.</summary>
    public static bool HasTag(string? wire, string id) => MapTags.Split(wire).Contains(id);

    // ---- the vote's own buttons: Random in the ballot's header, the MAPS sub-tabs Browse and Vote (v4.6.0; before it, a button and a banner) ----

    public const string RandomButton = "gg_v_random", BrowseButton = "gg_v_browse", BackButton = "gg_v_back";

    /// <summary>The dialog variables on gg_screen the vote's labels inherit, per player: Random's button, the hint that more cards are on the other pages,
    /// the line over the map browser while it adds to the vote (the contents before v4.6.0), the Vote sub-tab's label, the limit's and the inactivity
    /// timer's lines in the ballot's header.</summary>
    public const string RandomVar = "osvrand", MoreVar = "osvmore", BrowseVar = "osvleft", TabVar = "osvtab", LimitVar = "osvlim", IdleVar = "osvidle";

    /// <summary>Who has not voted yet (WaitVar), the voters on card k (VotersVar(k), short, for the chips of the contents before v4.6.0; HoverVar(k),
    /// longer, for the names a card shows while the mouse is on it) and the summary strip's segment i (StripVar(i)).</summary>
    public const string WaitVar = "osvwait";
    public static string VotersVar(int k) => $"osvn{k.ToString(CultureInfo.InvariantCulture)}";
    public static string HoverVar(int k) => $"osvh{k.ToString(CultureInfo.InvariantCulture)}";
    public static string StripVar(int i) => $"osvs{i.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The ballot header's two clocks: the limit's bar (gg_vlim) and the inactivity timer's (gg_vidle), each filled by one of w0 ... w10.</summary>
    public const string LimitBar = "gg_vlim", IdleBar = "gg_vidle";

    /// <summary>The summary strip's segments: one panel each, the options with votes, the most first. A segment's class is its count of votes
    /// (CountClasses), which the stylesheet makes its weight in the strip (fill-parent-flow), so the segments always fill the strip exactly.</summary>
    public const int StripSegments = 9;
    public static string Segment(int i) => $"gg_vs{i.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A share as one of eleven classes, w0 ... w10 - tenths of the votes cast: a card's bar and a strip segment's width (the same names
    /// on both, so the eleven cost eleven of the 1,024 class names).</summary>
    public static string Share(int votes, int cast) => $"w{(cast <= 0 ? 0 : (int)Math.Round(10.0 * votes / cast, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture)}";
    public static IEnumerable<(string Id, string Class, bool On)> ShareClasses(string id, int votes, int cast)
    {
        var on = Share(votes, cast);
        for (var s = 0; s <= 10; s++) yield return (id, $"w{s.ToString(CultureInfo.InvariantCulture)}", $"w{s.ToString(CultureInfo.InvariantCulture)}" == on);
    }

    /// <summary>A strip segment's votes as the same eleven classes, w0 ... w10 - its count, not a share (ten and more are w10): its weight in the strip.</summary>
    public static IEnumerable<(string Id, string Class, bool On)> CountClasses(string id, int votes)
    {
        var on = $"w{Math.Clamp(votes, 0, 10).ToString(CultureInfo.InvariantCulture)}";
        for (var s = 0; s <= 10; s++) yield return (id, $"w{s.ToString(CultureInfo.InvariantCulture)}", $"w{s.ToString(CultureInfo.InvariantCulture)}" == on);
    }

    /// <summary>How many characters of names a card's chips take in the contents before v4.6.0, so a longer list ends in "+N" and never in "..."; and
    /// how many the names a v4.6.0 card shows on pointing take (three lines over its picture).</summary>
    public const int CardNames = 30, HoverNames = 90;

    /// <summary>Names as a card's chips say them: as many as fit in about 34 characters, then "+N".</summary>
    public static string Names(IReadOnlyList<string> names, int max = 34)
    {
        var shown = "";
        for (var i = 0; i < names.Count; i++)
        {
            var next = (shown == "" ? "" : "  ·  ") + names[i];
            var left = names.Count - i - 1;
            if (shown.Length + next.Length + (left > 0 ? 4 : 0) > max) return shown == "" ? $"{names.Count} players" : $"{shown}  +{names.Count - i}";
            shown += next;
        }
        return shown;
    }

    /// <summary>The waiting line: every player present who has not voted, by name, then "Everyone has voted" - which a vote decided by everybody
    /// voting says for as long as it shows its result (it closes the moment the last one votes).</summary>
    public static string WaitLine(VoteState v) => !v.Open ? (v.Result != null && v.Present > 0 && v.Cast >= v.Present ? "Everyone has voted" : "") // a tie everybody voted in too
        : v.Waiting.Count > 0 ? "Waiting for: " + Names(v.Waiting, 110)
        : v.Inactivity > 0 ? $"Everyone has voted  ·  {InactivityLine(v)}" : "Everyone has voted";

    /// <summary>The inactivity timer as the screen says it: Inactivity timer 4 s - reset by Bob.</summary>
    public static string InactivityLine(VoteState v) => $"Inactivity timer {v.Inactivity} s{(v.ResetBy != "" ? $" - Reset by {Hud.Clean(v.ResetBy, 24)}" : "")}";

    /// <summary>The ballot header's two clocks while the vote is open: the limit's line (its seconds left, how many have voted), and the inactivity
    /// timer's once everybody has voted ("" before); decided, the first says what comes and when.</summary>
    public static (string Limit, string Idle) Clocks(VoteState v) =>
        !v.Open ? (v.Result is { } r ? $"The vote chose {Hud.Clean(r.Label, 40)} - Changing in {v.SecondsLeft} s" : "", "")
        : ($"{v.SecondsLeft} s left  ·  {v.Cast} of {v.Present} voted", v.Inactivity > 0 ? InactivityLine(v) : "");

    /// <summary>The Vote sub-tab's label: Vote and the limit's seconds left while it is open.</summary>
    public static string TabLabel(VoteState v) => v.Open ? $"Vote  ·  {v.SecondsLeft} s" : "Vote";

    /// <summary>The ballot's cards a page for a game holding this HUD (HudCheck.IsVersionWord): a content before v4.6.0 has eight cards
    /// (HudBrowser.PerPage), so a game known to hold one gets pages of eight and reaches every option on its pager; any other - this content, a newer
    /// one, or one not known (no HUD check's click yet, or HUD_CHECK off) - pages of MapVote.PageCards.</summary>
    public static int CardsFor(string version) => HudCheck.IsOutdated(version) ? HudBrowser.PerPage : MapVote.PageCards;

    /// <summary>The ballot's pages: perPage cards a page (CardsFor). Random is a button of its own, not a card.</summary>
    public static int Pages(VoteState v, int perPage = MapVote.PageCards) => Math.Max(1, (v.Maps.Count + perPage - 1) / perPage);

    /// <summary>The options on a page of the ballot, in screen order (the most votes first, as the bridge sends them).</summary>
    public static List<VoteState.Option> PageOptions(VoteState v, int page, int perPage = MapVote.PageCards) =>
        v.Maps.Skip(Math.Clamp(page, 0, Pages(v, perPage) - 1) * perPage).Take(perPage).ToList();

    /// <summary>The option numbers of a page's cards, card 1 first: what the plugin remembers it drew for a player (Click's order).</summary>
    public static int[] Order(VoteState v, int page, int perPage = MapVote.PageCards) => PageOptions(v, page, perPage).Select(o => o.N).ToArray();

    /// <summary>How long after a page's order changed for a player (the cards re-sort, the most votes first) a click still counts against the order
    /// that player saw before: a click made on the old order arrives a frame and a network trip after the new one went out.</summary>
    public const double ReSortSeconds = 1.0;

    /// <summary>The vote on the Maps page's cards, a page of them, the most votes first: option k of this page on card k, each with its mode (a label on
    /// the picture and the card's colour), its map, its votes and who picked it, every tag that applies, its record when it is risky; this player's
    /// choice lit (confirm), the winner once decided (now); the header's clocks and the page line where the other cards are. Random's button and the
    /// hint for the other pages are variables on gg_screen, and the pager's classes on gg_screen too. perPage: that player's page size (CardsFor).</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes, List<(string Name, string Text)> Vars) Cards(
        VoteState v, long account, HudCatalog? catalog, int page = 0, int perPage = MapVote.PageCards)
    {
        var t = new List<(string, string)>();
        var c = new List<(string, string, bool)>();
        var w = new List<(string, string)>();
        var mine = v.ChoiceOf(account);
        var done = !v.Open && v.Result != null;
        var maps = v.Maps;
        var pages = Pages(v, perPage);
        page = Math.Clamp(page, 0, pages - 1);
        var shown = PageOptions(v, page, perPage);
        var rest = maps.Count - (page + 1) * perPage;
        t.Add(("gg_m_count", done ? $"The vote chose {Hud.Clean(v.Result!.Label, 40)} - Changing in {v.SecondsLeft} s"
            : v.Inactivity > 0 ? $"Everyone has voted  ·  {InactivityLine(v)}"
            : $"Vote for the next map: {v.SecondsLeft} s left  ·  {v.Cast} of {v.Present} voted{(v.Waiting.Count > 0 && v.Waiting.Count <= 2 ? $"  ·  waiting for {string.Join(" and ", v.Waiting)}" : "")}"));
        var counted = v.Options.Where(o => o.Votes > 0).ToList();
        var most = counted.Count == 0 ? 0 : counted.Max(o => o.Votes);
        var tied = counted.Count(o => o.Votes == most) > 1;
        var typing = MapVote.Numbers == MapVote.Numbering.None ? "" : " or type its number";
        t.Add(("gg_pg_text", pages > 1 ? $"Page {page + 1} of {pages}  ·  {maps.Count} maps{(rest > 0 ? $": {rest} more on the next page" : "")}"
            : mine > 0 ? $"Your vote is lit - Click another card{typing} to change it, yours to take it back" : $"Click a card's Vote{(typing == "" ? "" : typing + " in chat")}"));
        t.Add(("gg_m_empty", ""));
        c.Add(("gg_m_empty", "used", false));
        c.Add((HudView.Screen, "voting", true));
        c.Add((HudView.Screen, "vpages", pages > 1));
        c.Add((HudView.Screen, "vmore", rest > 0));
        c.Add((HudBrowser.FirstButton, "off", page == 0));
        c.Add((HudBrowser.PrevButton, "off", page == 0));
        c.Add((HudBrowser.NextButton, "off", page >= pages - 1));
        c.Add((HudBrowser.LastButton, "off", page >= pages - 1));
        for (var k = 1; k <= HudMenu.Cards; k++)
        {
            var id = HudMenu.Card(k);
            var o = k <= shown.Count ? shown[k - 1] : null;
            var mode = o == null ? null : Modes.Find(o.Mode);
            var keep = o?.Source == VoteSource.Keep;
            var shownN = o == null ? 0 : v.Shown(o);
            var number = shownN > 0 ? $"{shownN}  ·  " : "";
            var entry = o == null || o.Key == "" || catalog == null ? null : catalog.Find(o.Key);
            var record = keep ? null : entry; // its record, for a risky map
            var who = o == null || o.Who == "" ? "" : $"picked by {o.Who}";
            t.Add(($"{id}_rank", o == null ? "" : $"{number}{(keep ? "Keep" : mode?.Name ?? "")}")); // the contents before v4.6.0 show it on the picture
            t.Add(($"{id}_name", o?.Label ?? ""));
            t.Add(($"{id}_size", o?.Size ?? ""));
            t.Add(($"{id}_votes", o == null ? "" : $"{o.Votes} vote{(o.Votes == 1 ? "" : "s")}{(who == "" ? "" : $"  ·  {who}")}"));
            t.Add(($"{id}_who", o == null ? "" : string.Join("  ·  ", new[] { o.Size, who }.Where(x => x != ""))));
            t.Add(($"{id}_score", o == null ? "" : o.Votes.ToString(CultureInfo.InvariantCulture)));
            t.Add(($"{id}_warn", record == null ? "" : HudMenu.Record(record)));
            t.Add(($"{id}_play_text", o == null ? "" : done ? (v.Result!.N == o.N ? "Next map" : "") : mine == o.N ? "Your vote" : keep ? (shownN > 0 ? $"Keep {shownN}" : "Keep") : shownN > 0 ? $"Vote {shownN}" : "Vote"));
            c.Add((id, "used", o != null));
            c.Add((id, "official", false));
            c.Add((id, "risky", record?.Risky == true));
            c.Add((id, "broken", record != null && (record.Broken >= 0 || record.Removed)));
            c.Add((id, "crashed", record is { Crashes: > 0 }));
            c.Add((id, "now", o != null && done && v.Result!.N == o.N));
            c.Add((id, "queued", o?.Source == VoteSource.Picked));
            c.Add((id, "liked", false));
            c.Add((id, "disliked", false));
            c.Add((id, "confirm", o != null && mine == o.N && !done));
            c.Add((id, "big", o != null && HasTag(o.Tags, MapTags.Big)));
            c.Add((id, "bigvoted", false));
            c.Add((id, "ads", o != null && HasTag(o.Tags, MapTags.Ads)));
            c.Add((id, "adsvoted", false));
            c.Add((id, "ours", o != null && (HasTag(o.Tags, MapTags.Ours) || catalog?.Find(o.Key) is { Ours: true })));
            c.AddRange(CardModeClasses(id, o?.Mode));                                     // the mode's label and colour
            c.AddRange(TagClassesOf(id, o?.Tags));                                         // the rest of its tags
            c.AddRange(ShareClasses(id, o?.Votes ?? 0, v.Cast));                         // its share of the votes, as a bar
            c.Add((id, "vhas", o != null && o.Votes > 0));                                // its voters' names show
            c.Add((id, "vlead", o != null && most > 0 && o.Votes == most && !tied));
            c.Add((id, "vtie", o != null && most > 0 && o.Votes == most && tied));
            var names = o != null && v.Voters.TryGetValue(o.N, out var ns) ? ns : [];
            w.Add((VotersVar(k), Names(names, CardNames)));
            w.Add((HoverVar(k), Names(names, HoverNames)));
        }
        var strip = counted.OrderByDescending(o => o.Votes).ThenBy(o => o.N).Take(StripSegments).ToList();
        for (var i = 1; i <= StripSegments; i++)
        {
            var o = i <= strip.Count ? strip[i - 1] : null;
            var seg = Segment(i);
            c.Add((seg, "used", o != null));
            c.AddRange(CountClasses(seg, o?.Votes ?? 0));
            c.AddRange(CardModeClasses(seg, o?.Source == VoteSource.Random ? null : o?.Mode));
            c.Add((seg, "vrand", o?.Source == VoteSource.Random));
            c.Add((seg, "vlead", o != null && o.Votes == most && !tied));
            c.Add((seg, "vtie", o != null && o.Votes == most && tied));
            var tag = o == null ? "" : o.Source == VoteSource.Random ? "Random" : o.Source == VoteSource.Keep ? "Keep" : v.Shown(o) is > 0 and var sn ? sn.ToString(CultureInfo.InvariantCulture) : Hud.Clean(o.Label, 12);
            w.Add((StripVar(i), o == null ? "" : $"{tag}  ·  {o.Votes}"));
        }
        c.Add((HudView.Screen, "vstrip", counted.Count > 0));
        w.Add((WaitVar, WaitLine(v)));
        var random = v.Random;
        c.Add((HudView.Screen, "vrandom", random != null && mine == random.N && !done));
        w.Add((RandomVar, random == null ? "" : $"Random  ·  {random.Votes} vote{(random.Votes == 1 ? "" : "s")}"));
        w.Add((MoreVar, rest > 0 ? $"{rest} more map{(rest == 1 ? "" : "s")} on the next page" : ""));
        var (limit, idle) = Clocks(v);
        w.Add((LimitVar, limit));
        w.Add((IdleVar, idle));
        c.AddRange(ShareClasses(LimitBar, v.Open ? v.SecondsLeft : 0, MapVote.Seconds));
        c.AddRange(ShareClasses(IdleBar, v.Open ? v.Inactivity : 0, Math.Max(1, MapVote.InactivitySeconds)));
        return (t, c, w);
    }

    /// <summary>The pictures of a page of the vote's cards: an option's map's picture where the addon has one (a Workshop map, an official map of any
    /// mode, the map that is on for Keep playing); none for a map without one.</summary>
    public static List<(string Panel, string[] Classes)> Thumbs(VoteState v, HudCatalog? catalog, int page = 0, int perPage = MapVote.PageCards)
    {
        var shown = PageOptions(v, page, perPage);
        return Enumerable.Range(1, HudMenu.Cards).Select(k =>
        {
            var o = k <= shown.Count ? shown[k - 1] : null;
            var entry = o == null || o.Key == "" || catalog == null ? null : catalog.Find(o.Key);
            return ($"{HudMenu.Card(k)}_thumb", entry != null ? HudThumbs.Classes(entry) : Array.Empty<string>());
        }).ToList();
    }

    /// <summary>The ballot's pager (the Maps page's First, Prev, Next and Last, shown while the ballot has pages): the page a click moves this player
    /// to, or null for any other button.</summary>
    public static int? PageClick(VoteState v, string buttonId, int page, int perPage = MapVote.PageCards)
    {
        var last = Pages(v, perPage) - 1;
        page = Math.Clamp(page, 0, last);
        return buttonId switch
        {
            HudBrowser.FirstButton => 0,
            HudBrowser.PrevButton => Math.Max(0, page - 1),
            HudBrowser.NextButton => Math.Min(last, page + 1),
            HudBrowser.LastButton => last,
            _ => null,
        };
    }

    /// <summary>The line over the map browser while it adds to the vote (the contents before v4.6.0: a banner; a variable on gg_screen).</summary>
    public static string BrowseLine(VoteState v) => $"The vote is on  ·  {v.SecondsLeft} s left  ·  a map's Play puts it on the vote, with your vote";

    /// <summary>The page a card of this option is on (an added map's, for its adder to see it).</summary>
    public static int PageOf(VoteState v, int n, int perPage = MapVote.PageCards) => Math.Max(0, v.Maps.FindIndex(o => o.N == n)) / perPage;

    /// <summary>The dialog variable on gg_screen that the popup inherits, for each player: the map it asks about. The reasons' words are oswhy1 ... on
    /// gg_global, the same for everybody, their chat words oswhyw1 ... and their short words oswhys1 ...; nothing is sent to the popup's own panels.</summary>
    public const string WhyVar = "oswhy";

    /// <summary>The popup's buttons, one per reason, in DownReasons' order: k from 1.</summary>
    public static string WhyButton(int k) => $"gg_why_{k.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The popup's fixed words, as the dialog variables on gg_global that its labels inherit.</summary>
    public static List<(string Name, string Text)> WhyWords() =>
        DownReasons.All.SelectMany((r, i) => new[] { ($"{WhyVar}{i + 1}", r.Label), ($"{WhyVar}w{i + 1}", r.Word), ($"{WhyVar}s{i + 1}", ShortLabel(r)) }).ToList();

    /// <summary>A reason as the inline buttons say it: short enough for half a card's picture.</summary>
    public static string ShortLabel(DownReasons.Reason r) => r.Value == DownReasons.Plain ? "Not for me" : char.ToUpperInvariant(r.Short[0]) + r.Short[1..];

    /// <summary>A click on a reason - the popup's or an inline one - as the reason it names, or null for any other button.</summary>
    public static DownReasons.Reason? WhyClick(string buttonId)
    {
        for (var k = 1; k <= DownReasons.All.Length; k++)
            if (buttonId == WhyButton(k)) return DownReasons.All[k - 1];
        return null;
    }

    /// <summary>The grenade drops' and catch-up's fixed rows on the Fun page, lit by classes on gg_global - nd-on/off and cu-on/off - from their fun
    /// settings, in Arms Race only (the rows are Arms Race's own).</summary>
    public static List<(string Id, string Class, bool On)> FixedRowClasses(Func<string, string?> value, bool armsRace)
    {
        bool? On(string id) => !armsRace || Fun.Find(id) is not { } s ? null : Fun.Showing(s, value).Id == "on";
        var (drops, catchUp) = (On("drops"), On("catchup"));
        return [(HudView.Global, "nd-on", drops == true), (HudView.Global, "nd-off", drops == false), (HudView.Global, "cu-on", catchUp == true), (HudView.Global, "cu-off", catchUp == false)];
    }

    /// <summary>What a card click says for everybody in chat, under that player's name: the command a typed vote would be, so that menu users learn
    /// the chat way - nothing while the cards have no numbers: there is no typed vote to learn.</summary>
    public static string ChatWords(int option) => MapVote.Numbers == MapVote.Numbering.None ? "" : $"!vote {option.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A click on card k's Play while the vote is open, on this player's page: !mv &lt;round&gt; &lt;n&gt;; Random's button likewise; a click on the
    /// card (or Random) this player's vote is on takes it back, !mv &lt;round&gt; 0; null for anything else (the card's arrows do nothing then).
    /// account: the player's; -1 when not known, which never takes a vote back. order: the option numbers this player's cards showed (Order), so a
    /// click counts for the map that player saw on the card - an option the vote no longer has counts for nothing; null: the newest order, in pages
    /// of perPage (CardsFor).</summary>
    public static string? Click(VoteState v, string buttonId, int page = 0, long account = -1, IReadOnlyList<int>? order = null, int perPage = MapVote.PageCards)
    {
        if (!v.Open || v.Round <= 0) return null;
        string Toggle(int n) => MapVote.Click(v.Round, account >= 0 && v.ChoiceOf(account) == n ? 0 : n);
        if (buttonId == RandomButton) return v.Random is { } r ? Toggle(r.N) : null;
        if (!buttonId.StartsWith("gg_m", StringComparison.Ordinal) || !buttonId.EndsWith("_play", StringComparison.Ordinal)) return null;
        if (!int.TryParse(buttonId[4..^5], NumberStyles.None, CultureInfo.InvariantCulture, out var k) || k < 1 || k > HudMenu.Cards) return null;
        if (order != null) return k <= order.Count && v.Numbered(order[k - 1]) is { Source: not VoteSource.Random } seen ? Toggle(seen.N) : null;
        var shown = PageOptions(v, page, perPage);
        return k <= shown.Count ? Toggle(shown[k - 1].N) : null;
    }
}
