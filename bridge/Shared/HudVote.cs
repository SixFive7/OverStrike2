// Over Strike 2's screens that every HUD content can show: the vote for the next map - the only way a player changes the map; the bridge's side
// in VoteRound.cs and MapVote.cs - drawn on the Maps page's cards, so a game still holding an older content can vote too, during Valve's review of
// an update and until it has downloaded it; a card's mode and tags; and why a player disliked a map (DownReasons). The HUD's version classes are
// HudVersion's.
//
// Shared like Hud.cs (plain BCL): the plugin draws it, the bridge's --selftest checks it.
using System.Globalization;

public static class HudVote
{
    /// <summary>The dialog variable on gg_global that the layouts' labels inherit for the game mode's name.</summary>
    public const string ModeVar = "osmode";

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
    /// the line over the map browser while it adds to the vote (the contents before v4.6.0), the Vote sub-tab's label, and the ballot's header: who started
    /// the vote, the limit's digits and line, the second row's key, digits and line (the inactivity timer, or the countdown once decided).</summary>
    public const string RandomVar = "osvrand", MoreVar = "osvmore", BrowseVar = "osvleft", TabVar = "osvtab", LimitVar = "osvlim", IdleVar = "osvidle",
        StartVar = "osvstart", LimitNumVar = "osvlimn", IdleKeyVar = "osvidlek", IdleNumVar = "osvidlen", NextVar = "osvnext";

    /// <summary>Who has not voted yet and then what came of the vote (WaitVar), the voters on card k (VotersVar(k), short, for the chips of the contents before v4.6.0; HoverVar(k),
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
    public static IEnumerable<(string Id, string Class, bool On)> ShareClasses(string id, int votes, int cast) => StepClasses(id, Share(votes, cast));

    /// <summary>A strip segment's votes as the same eleven classes, w0 ... w10 - its count, not a share (ten and more are w10): its weight in the strip.</summary>
    public static IEnumerable<(string Id, string Class, bool On)> CountClasses(string id, int votes) => StepClasses(id, $"w{Math.Clamp(votes, 0, 10).ToString(CultureInfo.InvariantCulture)}");

    /// <summary>How long one class of the limit's bar lasts: its transition in the stylesheet takes as long (.gg-clock-fill), so the bar moves steadily and is
    /// right to within a second. Ten of them are the limit.</summary>
    public const int LimitStepSeconds = MapVote.Seconds / 10;

    /// <summary>The limit's bar by the seconds left, as the same eleven classes: full from MapVote.Seconds up, then a step down every LimitStepSeconds. The class
    /// names where the bar is going, so a late joiner is right at once and it cannot drift; w0 snaps (the stylesheet), so the next vote's first w10 is full at once.</summary>
    public static string LimitStep(int secondsLeft) => secondsLeft >= MapVote.Seconds ? "w10"
        : $"w{Math.Max(0, (int)Math.Ceiling(secondsLeft / (double)LimitStepSeconds) - 1).ToString(CultureInfo.InvariantCulture)}";

    public static IEnumerable<(string Id, string Class, bool On)> LimitClasses(string id, int secondsLeft) => StepClasses(id, LimitStep(secondsLeft));

    static IEnumerable<(string Id, string Class, bool On)> StepClasses(string id, string on)
    {
        for (var s = 0; s <= 10; s++) yield return (id, $"w{s.ToString(CultureInfo.InvariantCulture)}", $"w{s.ToString(CultureInfo.InvariantCulture)}" == on);
    }

    /// <summary>How many characters of names a card's chips take in the contents before v4.6.0, so a longer list ends in "+N" and never in "..."; how
    /// many the names a v4.6.0 card shows on pointing take (three lines over its picture); and how many the waiting line in the ballot's header takes
    /// after its "Waiting for: " (its 518 px at 15 px bold).</summary>
    public const int CardNames = 30, HoverNames = 90, WaitNames = 52;

    /// <summary>Names as a card's chips say them: as many as fit in about 34 characters, then "+N". between: what stands between two names; mark: what a name
    /// becomes in the text (a chat line's colour, M.N), which takes no room on screen.</summary>
    public static string Names(IReadOnlyList<string> names, int max = 34, string between = "  ·  ", Func<string, string>? mark = null)
    {
        var shown = "";
        var length = 0;
        for (var i = 0; i < names.Count; i++)
        {
            var gap = i == 0 ? "" : between;
            var left = names.Count - i - 1;
            if (length + gap.Length + names[i].Length + (left > 0 ? 4 : 0) > max)
                return i > 0 ? $"{shown}  +{names.Count - i}" : names.Count == 1 ? "1 player" : $"{names.Count} players";
            shown += gap + (mark?.Invoke(names[i]) ?? names[i]);
            length += gap.Length + names[i].Length;
        }
        return shown;
    }

    /// <summary>The line beside the ballot's strip: every player present who has not voted, by name; Everybody has voted once nobody is left; decided, what came of
    /// it (DoneLine). A player the log stream does not know yet (the bridge started mid-map) has no name: only a number.</summary>
    public static string WaitLine(VoteState v) => !v.Open ? DoneLine(v)
        : v.Waiting.Count > 0 ? $"Waiting for: {Names(v.Waiting, WaitNames, ", ")}"
        : v.Cast < v.Present ? $"Waiting for: {(v.Present - v.Cast == 1 ? "1 player" : $"{v.Present - v.Cast} players")}"
        : "Everybody has voted";

    /// <summary>What came of the vote, as the window says it while it counts down: the map that won and its votes of everybody present (N of M, like the tile's N/M),
    /// the dice's part in it, or the map kept; "" before it is decided.</summary>
    public static string DoneLine(VoteState v)
    {
        if (v.Result is not { } r) return "";
        var label = Hud.Clean(r.Label, 40);
        var card = v.Numbered(r.N);
        var count = $"({card?.Votes ?? 0} of {v.Present})";
        if (v.Kept)
            return r.How == "none" ? $"Nobody voted - {label} stays"
                : r.How == "tie" ? $"The vote kept {label} {count} - A tie, drawn by the dice"
                : v.MatchEnded ? $"{label} again {count} - Its next match starts on it"
                : $"The vote kept {label} {count}";
        return r.How == "none" ? $"Nobody voted - The dice rolled {label}"
            : card?.Source == VoteSource.Random ? $"Random won {count} - The dice rolled {label}"
            : r.How == "tie" ? $"{label} won {count} - A tie, drawn by the dice"
            : $"{label} won {count} - Picked by the vote";
    }

    /// <summary>The inactivity timer as a line of its own says it, for the contents before v4.6.0: Inactivity timer 4 s - Reset by Bob.</summary>
    public static string InactivityLine(VoteState v) => $"Inactivity timer {v.Inactivity} s{(v.ResetBy != "" ? $" - Reset by {Hud.Clean(v.ResetBy, 24)}" : "")}";

    /// <summary>Who started the vote, in the window nobody can close, in place of a top line. A match end's vote has nobody.</summary>
    public static string StartLine(VoteState v) => v.StarterName != "" ? $"Started by {Hud.Clean(v.StarterName, 24)}" : v.MatchEnded ? "Match over" : "";

    /// <summary>The words of the ballot header's two rows. The limit: its digits and how many have voted. The second row: the inactivity timer while the vote is open -
    /// its digits and who reset it once everybody has voted - and, decided, the limit's row gone, the countdown to the change or to the window's close.</summary>
    public readonly record struct ClockWords(string LimitNum, string Limit, string IdleKey, string IdleNum, string Idle);

    public static ClockWords Clocks(VoteState v) =>
        !v.Open ? new("", "", v.Kept ? "Closes in" : "Changing in", $"{v.SecondsLeft.ToString(CultureInfo.InvariantCulture)} s", "")
        : new($"{v.SecondsLeft.ToString(CultureInfo.InvariantCulture)} s", $"{v.Cast} of {v.Present} voted", "Inactivity timer",
            v.Inactivity > 0 ? $"{v.Inactivity.ToString(CultureInfo.InvariantCulture)} s" : "",
            v.Inactivity <= 0 ? "Starts when everybody has voted" : v.ResetBy != "" ? $"Reset by {Hud.Clean(v.ResetBy, 24)}" : "");

    /// <summary>The last seconds of the limit, when its digits turn red and pulse (vlow on gg_screen).</summary>
    public const int LowSeconds = 10;

    /// <summary>The Vote sub-tab's label: Vote and the limit's seconds left while it is open.</summary>
    public static string TabLabel(VoteState v) => v.Open ? $"Vote  ·  {v.SecondsLeft} s" : "Vote";

    /// <summary>The ballot's cards a page for a game holding this HUD (HudVersion.IsWord): a content before v4.6.0 has eight cards
    /// (HudBrowser.PerPage), so a game known to hold one gets pages of eight and reaches every option on its pager; any other - this content, a newer
    /// one, or one not known (no HUD check's click yet, or hud.check off) - pages of MapVote.PageCards.</summary>
    public static int CardsFor(string version) => HudVersion.IsBefore(version, CardsLevel) ? HudBrowser.PerPage : MapVote.PageCards;

    /// <summary>The first content with the ballot's ten cards and catch-up's card under the crosshair: a game on an older one gets eight cards a page and the centre panel.</summary>
    public const int CardsLevel = 46;

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
        t.Add(("gg_m_count", done ? $"{DoneLine(v)} - {(v.Kept ? "Closes in" : "Changing in")} {v.SecondsLeft} s"
            : v.Inactivity > 0 ? $"Everybody has voted  ·  {InactivityLine(v)}"
            : $"Vote for the next map: {v.SecondsLeft} s left  ·  {v.Cast} of {v.Present} voted{(v.Waiting.Count > 0 && v.Waiting.Count <= 2 ? $"  ·  Waiting for {string.Join(" and ", v.Waiting)}" : "")}"));
        var counted = v.Options.Where(o => o.Votes > 0).ToList();
        var most = counted.Count == 0 ? 0 : counted.Max(o => o.Votes);
        var tied = counted.Count(o => o.Votes == most) > 1;
        t.Add(("gg_pg_text", pages > 1 ? $"Page {page + 1} of {pages}  ·  {maps.Count} maps{(rest > 0 ? $": {rest} more on the next page" : "")}"
            : mine > 0 ? "Your vote is lit - Click another card to change it, yours to take it back" : "Click a card's Vote"));
        t.Add(("gg_m_empty", ""));
        c.Add(("gg_m_empty", "used", false));
        c.Add((HudView.Screen, "voting", true));
        c.Add((HudView.Screen, "vdone", done));                      // decided or kept: the limit's row goes, the second row counts down, the tiles dim but the winner
        c.Add((HudView.Screen, "vlow", v.Open && v.SecondsLeft <= LowSeconds));
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
            var entry = o == null || o.Key == "" || catalog == null ? null : catalog.Find(o.Key);
            var record = keep ? null : entry; // its record, for a risky map
            var who = o == null || o.Who == "" ? "" : $"Picked by {o.Who}";
            t.Add(($"{id}_rank", o == null ? "" : keep ? "Keep" : mode?.Name ?? "")); // the contents before v4.6.0 show it on the picture
            t.Add(($"{id}_name", o?.Label ?? ""));
            t.Add(($"{id}_size", o?.Size ?? ""));
            t.Add(($"{id}_votes", o == null ? "" : $"{o.Votes} vote{(o.Votes == 1 ? "" : "s")}{(who == "" ? "" : $"  ·  {who}")}"));
            t.Add(($"{id}_who", o == null ? "" : string.Join("  ·  ", new[] { o.Size, who }.Where(x => x != ""))));
            t.Add(($"{id}_score", o == null ? "" : $"{o.Votes.ToString(CultureInfo.InvariantCulture)}/{v.Present.ToString(CultureInfo.InvariantCulture)}")); // votes/present, like the game-end screen
            t.Add(($"{id}_warn", record == null ? "" : HudMenu.Record(record)));
            t.Add(($"{id}_play_text", o == null ? "" : done ? (v.Result!.N == o.N ? (keep ? "Kept" : "Next map") : "") : mine == o.N ? "Your vote" : keep ? "Keep" : "Vote"));
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
            c.Add((id, "ads", o != null && HasTag(o.Tags, MapTags.Ads)));
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
            var tag = o == null ? "" : o.Source == VoteSource.Random ? "Random" : o.Source == VoteSource.Keep ? "Keep" : Hud.Clean(o.Label, 12);
            w.Add((StripVar(i), o == null ? "" : $"{tag}  ·  {o.Votes}"));
        }
        c.Add((HudView.Screen, "vstrip", counted.Count > 0));
        w.Add((WaitVar, WaitLine(v)));
        var random = v.Random;
        c.Add((HudView.Screen, "vrandom", random != null && mine == random.N && !done));
        w.Add((RandomVar, random == null ? "" : $"Random  ·  {random.Votes} vote{(random.Votes == 1 ? "" : "s")}"));
        w.Add((MoreVar, rest > 0 ? $"{rest} more map{(rest == 1 ? "" : "s")} on the next page" : ""));
        var clocks = Clocks(v);
        w.Add((StartVar, StartLine(v)));
        w.Add((NextVar, v.Kept ? "Kept" : "Next map")); // the winner's tag: a kept map is no next map
        w.Add((LimitNumVar, clocks.LimitNum));
        w.Add((LimitVar, clocks.Limit));
        w.Add((IdleKeyVar, clocks.IdleKey));
        w.Add((IdleNumVar, clocks.IdleNum));
        w.Add((IdleVar, clocks.Idle));
        c.AddRange(LimitClasses(LimitBar, v.Open ? v.SecondsLeft : 0));
        c.AddRange(v.Open ? ShareClasses(IdleBar, v.Inactivity, Math.Max(1, MapVote.InactivitySeconds)) // a reset jumps to w10: the bar refills in a second, which is the visible reset
            : ShareClasses(IdleBar, v.SecondsLeft, Math.Max(v.SecondsLeft, Math.Max(1, v.DoneSeconds))));    // decided: this bar counts down, not the limit's - leaving its state would take 6 s to refill
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
    public static string BrowseLine(VoteState v) => $"The vote is on  ·  {v.SecondsLeft} s left  ·  A map's Play puts it on the vote, with your vote";

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
