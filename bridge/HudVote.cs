// Over Strike 2's screens that every HUD content can show (2026-11-01): the end-of-match vote (#26, the bridge's side in VoteRound.cs and
// MapVote.cs), drawn on the Maps page's eight cards - so a game still holding an older content (v3_0_0, v3_1_0) can vote too, during
// Valve's review of the update and until it has downloaded it - the HUD version check, and why a player down-voted a map (DownReasons).
//
// The version check needs no click and no word from the client: the server sets threshold classes on gg_global - svge<k> while its own
// content level is at least k, svlt<k> while it is below k - and every content from v4_0_0 on carries rules for its own level: a v4_0_0
// game shows "yours is older" under svge41 and "yours is newer" under svlt40, and nothing when the levels match. The server's version
// name rides in the dialog variable osver on gg_global, which the newer layouts' labels inherit. A game older than v4_0_0 has none of
// those rules, so it gets the note line instead - a note that every v4_0_0 game's stylesheet hides (vold on gg_screen).
//
// Shared like Hud.cs (plain BCL): the plugin draws it, the bridge's --selftest checks it.
using System.Globalization;

/// <summary>Why a player down-voted a map (the owner's decision of 2026-11-01): one vote per player per map, whose value in votes.json says
/// why - every one of them that player's dislike wherever a vote counts. A plain Dislike is "I did not like the map". Too big and Ads
/// have their own effects (Selection.cs); the rest are kept and count as a dislike, nothing more. Chat steers it directly: !down big,
/// !down 12 big. The same words on /act set the reason. Nothing takes a vote back (the owner's 27, 2026-11-02): a plain !down on a map already
/// disliked keeps its reason and says so. A first plain !down opens the popup that lists the reasons (Votes.cs, HudVote.WhyWords) for
/// PopupSeconds: choosing one, or typing its word, updates that vote.</summary>
public static class DownReasons
{
    /// <summary>A reason: the word chat uses, the vote's value, what the popup says, and how a note names it.</summary>
    public sealed record Reason(string Word, int Value, string Label, string Short);

    public const int Plain = -1; // votes.json's plain downvote, since long before there were reasons

    /// <summary>How long the popup stands, and how long after that a click on its way still counts. The drain line runs a second shorter (HudView.PlayerClasses;
    /// the stylesheet's .why-run).</summary>
    public const double PopupSeconds = 15, PopupGraceSeconds = 2;

    /// <summary>The click on a reason in the popup, on /act: "!reason big". It updates the vote the popup asks about, and nothing else; once the popup is gone it does nothing.</summary>
    public const string PopupVerb = "!reason";

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
    /// <summary>The server's content as a number, for the version check: v4_4_0 is 44. The classes go from FirstLevel to LastLevel.</summary>
    public const int ContentLevel = 45, FirstLevel = 31, LastLevel = 49;

    /// <summary>The dialog variables on gg_global that the newer layouts' labels inherit: the server's HUD version and the game mode's name.</summary>
    public const string VersionVar = "osver", ModeVar = "osmode";

    /// <summary>How long after a real join the version banner (and, for an older HUD, the note) shows.</summary>
    public const double JoinSeconds = 15;

    /// <summary>The note a game older than v4_0_0 shows at a join (a v4_0_0 game hides it: vold).</summary>
    public static string OldHudNote => $"Your HUD is older than the server's ({Hud.Content}): quit CS2 and start it again, and Steam updates it.";

    /// <summary>The version check's classes, on gg_global for everybody.</summary>
    public static List<(string Id, string Class, bool On)> VersionClasses(int level = ContentLevel)
    {
        var c = new List<(string, string, bool)>();
        for (var k = FirstLevel; k <= LastLevel; k++)
        {
            c.Add((HudView.Global, $"svge{k}", level >= k));
            c.Add((HudView.Global, $"svlt{k}", level < k));
        }
        return c;
    }

    /// <summary>The game mode's classes on gg_global (mode-armsrace, mode-deathmatch, mode-wingman): the newer layouts hide what is Arms
    /// Race's own outside it - friendly fire's rows on the Fun page, the match length on the Game page.</summary>
    public static List<(string Id, string Class, bool On)> ModeClasses(string mode) =>
        Modes.All.Select(m => (HudView.Global, $"mode-{m.Id}", m.Id == mode)).ToList();

    /// <summary>A card's mode, as classes on the card (content v4_4_0: the label on its picture and its colour - the same three names as gg_global's,
    /// on another panel): mode-armsrace, mode-deathmatch, mode-wingman, exactly one on for a card in use.</summary>
    public static IEnumerable<(string Id, string Class, bool On)> CardModeClasses(string card, string? mode) =>
        Modes.All.Select(m => (card, $"mode-{m.Id}", m.Id == mode));

    // ---- the vote's own buttons (content v4_4_0): Random beside the cards, the map browser, and back to the ballot from it ----

    public const string RandomButton = "gg_v_random", BrowseButton = "gg_v_browse", BackButton = "gg_v_back";

    /// <summary>The dialog variables on gg_screen the vote's labels inherit (content v4_4_0), per player: Random's button, the hint that more
    /// cards are on the other pages, and the line over the map browser while it adds to the vote.</summary>
    public const string RandomVar = "osvrand", MoreVar = "osvmore", BrowseVar = "osvleft";

    /// <summary>Content v4_5_0 (the owner's 2026-11-03): who has not voted yet (WaitVar), the voters on card k (VotersVar(k)), the summary strip's
    /// segment i (StripVar(i)), and the line that brings a player who closed the window back to a vote a player started (AwayVar).</summary>
    public const string WaitVar = "osvwait", AwayVar = "osvaway";
    public static string VotersVar(int k) => $"osvn{k.ToString(CultureInfo.InvariantCulture)}";
    public static string StripVar(int i) => $"osvs{i.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The summary strip's segments (content v4_5_0): one panel each, the options with votes, the most first. A segment's class is its
    /// count of votes (CountClasses), which the stylesheet makes its weight in the strip (fill-parent-flow), so the segments always fill the strip
    /// exactly - tenths left a gap or ran past its end (three tied votes were 90 %, four were 120 %; the real client, 2026-11-04).</summary>
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

    /// <summary>How many characters of names a card's chips take, so a longer list ends in "+N" and never in "..." (the chips' 160 px beside the
    /// mode label hold about 40 at 12 px; 62 % held 30 no more and cut "Test B" off in the real client, 2026-11-04).</summary>
    public const int CardNames = 30;

    /// <summary>A card's "Remove mine" (content v4_5_0): on a ballot card of this player's own queued request, it takes that request back.</summary>
    public static string UnqueueButton(int k) => $"{HudMenu.Card(k)}_unq";

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

    /// <summary>The waiting line (content v4_5_0, the owner's 2026-11-03): every player present who has not voted, by name, then "Everyone has voted" -
    /// which a vote decided by everybody voting says for as long as it shows its result (it closes the moment the last one votes).</summary>
    public static string WaitLine(VoteState v) => !v.Open ? (v.Result != null && v.Present > 0 && v.Cast >= v.Present ? "Everyone has voted" : "") // a tie everybody voted in too
        : v.Waiting.Count == 0 ? "Everyone has voted" : "Waiting for: " + Names(v.Waiting, 110);

    /// <summary>The ballot's pages: eight cards a page (MapVote.MinMaps is one page's worth). Random is a button of its own, not a card.</summary>
    public static int Pages(VoteState v) => Math.Max(1, (v.Maps.Count + HudMenu.Cards - 1) / HudMenu.Cards);

    /// <summary>The vote on the Maps page's cards, a page of them: option k of this page on card k, each with its mode (a label on the picture and
    /// the card's colour), its map, its votes and who queued or added it; Keep playing first on a vote a player started; this player's choice lit
    /// (confirm), queued options tagged (queued), the winner once decided (now); the count line says how long and how many have voted, the page line
    /// where the other cards are. Random's button and the hint for the other pages are variables on gg_screen (content v4_4_0); a game holding an
    /// older content sees the first page's cards and reads the rest off the page line - typing a card's number works from any content.</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes, List<(string Name, string Text)> Vars) Cards(
        VoteState v, long account, HudCatalog? catalog, string openPicture = "", int page = 0, ISet<string>? myQueued = null)
    {
        var t = new List<(string, string)>();
        var c = new List<(string, string, bool)>();
        var w = new List<(string, string)>();
        var mine = v.ChoiceOf(account);
        var done = !v.Open && v.Result != null;
        var maps = v.Maps;
        var pages = Pages(v);
        page = Math.Clamp(page, 0, pages - 1);
        var shown = maps.Skip(page * HudMenu.Cards).Take(HudMenu.Cards).ToList();
        var rest = maps.Count - (page + 1) * HudMenu.Cards;
        t.Add(("gg_m_count", done ? $"The vote chose {Hud.Clean(v.Result!.Label, 40)} - changing in {v.SecondsLeft} s"
            : $"Vote for the next map: {v.SecondsLeft} s left  ·  {v.Cast} of {v.Present} voted{(v.Waiting.Count > 0 && v.Waiting.Count <= 2 ? $"  ·  waiting for {string.Join(" and ", v.Waiting)}" : "")}"));
        // where the votes go (content v4_5_0): every card its share and its voters, the leader lit or a tie, the strip across the top
        var counted = v.Options.Where(o => o.Votes > 0).ToList();
        var most = counted.Count == 0 ? 0 : counted.Max(o => o.Votes);
        var tied = counted.Count(o => o.Votes == most) > 1;
        t.Add(("gg_pg_text", pages > 1 ? $"Page {page + 1} of {pages}  ·  {maps.Count} maps{(rest > 0 ? $": {rest} more on the next page" : "")}"
            : mine > 0 ? "Your vote is lit - click another card or type its number" : "Click a card's Vote or type its number in chat"));
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
            t.Add(($"{id}_rank", o == null ? "" : $"{o.N}  ·  {(keep ? "Keep" : mode?.Name ?? "")}")); // the number a player types in chat, or after !vote
            t.Add(($"{id}_name", o?.Label ?? ""));
            t.Add(($"{id}_size", o?.Size ?? ""));
            t.Add(($"{id}_votes", o == null ? "" : $"{o.Votes} vote{(o.Votes == 1 ? "" : "s")}{(o.Who == "" ? "" : o.Source == VoteSource.Added ? $"  ·  added by {o.Who}" : $"  ·  queued by {o.Who}")}"));
            t.Add(($"{id}_warn", ""));
            t.Add(($"{id}_play_text", o == null ? "" : done ? (v.Result!.N == o.N ? "Next map" : "") : mine == o.N ? "Your vote" : keep ? $"Keep {o.N}" : $"Vote {o.N}"));
            c.Add((id, "used", o != null));
            c.Add((id, "official", false));
            c.Add((id, "risky", false));
            c.Add((id, "broken", false));
            c.Add((id, "crashed", false));
            c.Add((id, "now", o != null && done && v.Result!.N == o.N));
            c.Add((id, "queued", o?.Source == VoteSource.Queue && o.Who != "")); // a request taken back (Remove mine) leaves its card on the ballot, untagged
            c.Add((id, "liked", false));
            c.Add((id, "disliked", false));
            c.Add((id, "confirm", o != null && mine == o.N && !done));
            c.Add((id, "big", false));
            c.Add((id, "bigvoted", false));
            c.Add((id, "ads", false));
            c.Add((id, "adsvoted", false));
            c.Add((id, "ours", o != null && catalog?.Find(o.Key) is { Ours: true }));
            c.Add((id, "nopic", o != null && o.Key.All(char.IsAsciiDigit) && o.Key != "" && !HudThumbs.Has(o.Key))); // content v4_2_0: the picture on request
            c.Add((id, "picopen", o != null && o.Key != "" && o.Key == openPicture));
            c.AddRange(CardModeClasses(id, o?.Mode));                                     // content v4_4_0: the mode's label and colour
            c.AddRange(ShareClasses(id, o?.Votes ?? 0, v.Cast));                         // content v4_5_0: its share of the votes, as a bar
            c.Add((id, "vhas", o != null && o.Votes > 0));                                // its voters' chips show
            c.Add((id, "vlead", o != null && most > 0 && o.Votes == most && !tied));
            c.Add((id, "vtie", o != null && most > 0 && o.Votes == most && tied));
            c.Add((id, "vmine", o != null && v.Open && o.Source == VoteSource.Queue && myQueued?.Contains(o.Key) == true)); // Remove mine
            w.Add((VotersVar(k), o != null && v.Voters.TryGetValue(o.N, out var names) ? Names(names, CardNames) : ""));
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
            w.Add((StripVar(i), o == null ? "" : $"{(o.Source == VoteSource.Random ? "Random" : o.Source == VoteSource.Keep ? $"{o.N} Keep" : o.N.ToString(CultureInfo.InvariantCulture))}  ·  {o.Votes}"));
        }
        c.Add((HudView.Screen, "vstrip", counted.Count > 0));
        w.Add((WaitVar, WaitLine(v)));
        var random = v.Random;
        c.Add((HudView.Screen, "vrandom", random != null && mine == random.N && !done));
        w.Add((RandomVar, random == null ? "" : $"{random.N}  ·  Random  ·  {random.Votes} vote{(random.Votes == 1 ? "" : "s")}"));
        w.Add((MoreVar, rest > 0 ? $"{rest} more map{(rest == 1 ? "" : "s")} on the next page" : ""));
        return (t, c, w);
    }

    /// <summary>The pictures of a page of the vote's cards: an option's map's picture where the addon has one (a Workshop map, an official map of any
    /// mode, the map that is on for Keep playing); none for a map without one.</summary>
    public static List<(string Panel, string[] Classes)> Thumbs(VoteState v, HudCatalog? catalog, int page = 0)
    {
        var shown = v.Maps.Skip(Math.Clamp(page, 0, Pages(v) - 1) * HudMenu.Cards).Take(HudMenu.Cards).ToList();
        return Enumerable.Range(1, HudMenu.Cards).Select(k =>
        {
            var o = k <= shown.Count ? shown[k - 1] : null;
            var entry = o == null || o.Key == "" || catalog == null ? null : catalog.Find(o.Key);
            return ($"{HudMenu.Card(k)}_thumb", entry != null ? HudThumbs.Classes(entry) : Array.Empty<string>());
        }).ToList();
    }

    /// <summary>The ballot's pager (the Maps page's First, Prev, Next and Last; content v4_4_0 shows them while the vote is on): the page a click
    /// moves this player to, or null for any other button.</summary>
    public static int? PageClick(VoteState v, string buttonId, int page)
    {
        var last = Pages(v) - 1;
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

    /// <summary>The line over the map browser while it adds to the vote (content v4_4_0's banner, a variable on gg_screen).</summary>
    public static string BrowseLine(VoteState v) => $"The vote is on  ·  {v.SecondsLeft} s left  ·  a map's Play puts it on the vote, with your vote";

    /// <summary>The page a card of this option is on (an added map's, for its adder to see it).</summary>
    public static int PageOf(VoteState v, int n) => Math.Max(0, v.Maps.FindIndex(o => o.N == n)) / HudMenu.Cards;

    /// <summary>The dialog variable on gg_screen that content v4_1_0's popup inherits, for each player: the map it asks about. The reasons' words are
    /// oswhy1 ... on gg_global, the same for everybody, their chat words oswhyw1 ... and (content v4_4_0) their short words oswhys1 ...; nothing is
    /// sent to the popup's own panels.</summary>
    public const string WhyVar = "oswhy";

    /// <summary>The popup's buttons, one per reason, in DownReasons' order: k from 1.</summary>
    public static string WhyButton(int k) => $"gg_why_{k.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Content v4_4_0's inline reasons (the owner's 2026-11-03): in the window's live column (w), on the TAB board (b), and over the picture of the
    /// card that was disliked (c1 ... c8) - each a button per reason, k from 1.</summary>
    public static string WhyButton(string place, int k) => $"gg_why{place}_{k.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The places of content v4_4_0's inline reasons: the live column, the board, and each card.</summary>
    public static readonly string[] WhyPlaces = ["w", "b", .. Enumerable.Range(1, HudMenu.Cards).Select(k => $"c{k}")];

    /// <summary>The popup's fixed words, as the dialog variables on gg_global that its labels inherit.</summary>
    public static List<(string Name, string Text)> WhyWords() =>
        DownReasons.All.SelectMany((r, i) => new[] { ($"{WhyVar}{i + 1}", r.Label), ($"{WhyVar}w{i + 1}", r.Word), ($"{WhyVar}s{i + 1}", ShortLabel(r)) }).ToList();

    /// <summary>A reason as content v4_4_0's inline buttons say it: short enough for half a card.</summary>
    public static string ShortLabel(DownReasons.Reason r) => r.Value == DownReasons.Plain ? "Not for me" : char.ToUpperInvariant(r.Short[0]) + r.Short[1..];

    /// <summary>A click on a reason - the popup's or an inline one - as the reason it names, or null for any other button.</summary>
    public static DownReasons.Reason? WhyClick(string buttonId)
    {
        for (var k = 1; k <= DownReasons.All.Length; k++)
        {
            if (buttonId == WhyButton(k)) return DownReasons.All[k - 1];
            foreach (var place in WhyPlaces) if (buttonId == WhyButton(place, k)) return DownReasons.All[k - 1];
        }
        return null;
    }

    /// <summary>The card a Dislike came from (gg_m3_down: 3), or 0 for any other button: the inline reasons go over that card's picture.</summary>
    public static int DownCard(string buttonId) => buttonId.StartsWith("gg_m", StringComparison.Ordinal) && buttonId.EndsWith("_down", StringComparison.Ordinal)
        && int.TryParse(buttonId[4..^5], NumberStyles.None, CultureInfo.InvariantCulture, out var k) && k >= 1 && k <= HudMenu.Cards ? k : 0;

    /// <summary>The buttons content v4_4_0 added: the vote's Random, map browser and back buttons, the inline reasons, the Fun page's Random and Default.
    /// A click on one proves that player's game holds v4_4_0; nothing is sent to them.</summary>
    public static readonly string[] NewButtons44 =
        [RandomButton, BrowseButton, BackButton, .. WhyPlaces.SelectMany(place => Enumerable.Range(1, DownReasons.All.Length).Select(k => WhyButton(place, k))),
         HudMenu.FunRandom, HudMenu.FunDefault];

    /// <summary>The buttons content v4_5_0 added: each ballot card's Remove mine and the Fun page's Catch-up on and off. A click on one proves that
    /// player's game holds v4_5_0; nothing is sent to them.</summary>
    public static readonly string[] NewButtons45 = [.. Enumerable.Range(1, HudMenu.Cards).Select(UnqueueButton), HudMenu.CatchUpOn, HudMenu.CatchUpOff];

    /// <summary>The panels content v4_5_0 added that get classes from the server: the summary strip's segments. Only a game that has shown v4_5_0 gets them.</summary>
    public static readonly string[] NewIds45 = [.. Enumerable.Range(1, StripSegments).Select(Segment)];

    /// <summary>Catch-up's state on gg_global (content v4_5_0's Fun page lights on or off; an older game has no rule for it).</summary>
    public static List<(string Id, string Class, bool On)> CatchUpClasses(bool? on) => [(HudView.Global, "cu-on", on == true), (HudView.Global, "cu-off", on == false)];

    /// <summary>The grenade drops' state, on gg_global (content v4_1_0's Fun page lights the choice that is on; an older game has no rule for it).</summary>
    public static List<(string Id, string Class, bool On)> NadesClasses(bool? on) => [(HudView.Global, "nd-on", on == true), (HudView.Global, "nd-off", on == false)];

    /// <summary>What a card click says for everybody in chat, under that player's name (the owner's decision of 2026-11-01): the command a
    /// typed vote would be, so that menu users learn the chat way.</summary>
    public static string ChatWords(int option) => $"!vote {option.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The content a page's words need (HudView.NewIds, NewIds32). A game gets them once it has shown it holds that content: by a click on a
    /// button only that content has, such as the page's own tab - or by its player asking for the page in chat (the owner: !fun opens the GUI, rows
    /// and all). Asking counts as the click, for this player alone: the published HUD has these pages, and a game too old to have them
    /// shows a page without rows either way. 0: every game has them.</summary>
    public static int PageLevel(string page) => page switch { "How" => 31, "Fun" or "Help" => 32, _ => 0 };

    /// <summary>A click on card k's Play while the vote is open, on this player's page: !mv &lt;round&gt; &lt;n&gt;; Random's button likewise; null for
    /// anything else (the card's arrows do nothing then).</summary>
    public static string? Click(VoteState v, string buttonId, int page = 0)
    {
        if (!v.Open || v.Round <= 0) return null;
        if (buttonId == RandomButton) return v.Random is { } r ? MapVote.Click(v.Round, r.N) : null;
        if (!buttonId.StartsWith("gg_m", StringComparison.Ordinal) || !buttonId.EndsWith("_play", StringComparison.Ordinal)) return null;
        if (!int.TryParse(buttonId[4..^5], NumberStyles.None, CultureInfo.InvariantCulture, out var k) || k < 1 || k > HudMenu.Cards) return null;
        var shown = v.Maps.Skip(Math.Clamp(page, 0, Pages(v) - 1) * HudMenu.Cards).Take(HudMenu.Cards).ToList();
        return k <= shown.Count ? MapVote.Click(v.Round, shown[k - 1].N) : null;
    }
}
