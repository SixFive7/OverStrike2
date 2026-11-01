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
/// !down 12 big. The same words on /act set the reason and never take the vote back; plain !down keeps its old toggle. A plain !down also
/// opens the popup that lists the reasons (Votes.cs, HudVote.WhyWords) for PopupSeconds: choosing one, or typing its word, updates that vote.</summary>
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
    /// <summary>The server's content as a number, for the version check: v4_1_0 is 41. The classes go from FirstLevel to LastLevel.</summary>
    public const int ContentLevel = 41, FirstLevel = 31, LastLevel = 49;

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

    /// <summary>The vote on the Maps page's cards: option k on card k (Random last), each with its mode, its map, its votes and who queued
    /// it; this player's choice lit (confirm), queued options tagged (queued), the winner once decided (now); the count line says how long
    /// and how many have voted. Cards beyond the options are empty. The newer layouts also hide the toolbar (voting on gg_screen).</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) Cards(VoteState v, long account, HudCatalog? catalog)
    {
        var t = new List<(string, string)>();
        var c = new List<(string, string, bool)>();
        var mine = v.ChoiceOf(account);
        var done = !v.Open && v.Result != null;
        t.Add(("gg_m_count", done ? $"The vote chose {Hud.Clean(v.Result!.Label, 40)} - changing in {v.SecondsLeft} s"
            : $"Vote for the next map: {v.SecondsLeft} s left  ·  {v.Cast} of {v.Present} voted, {v.Needed} decide it"));
        t.Add(("gg_pg_text", mine > 0 ? "Your vote is lit - click another card or type its number" : "Click a card's Vote or type its number in chat"));
        t.Add(("gg_m_empty", ""));
        c.Add(("gg_m_empty", "used", false));
        c.Add((HudView.Screen, "voting", true));
        for (var k = 1; k <= HudMenu.Cards; k++)
        {
            var id = HudMenu.Card(k);
            var o = k <= v.Options.Count ? v.Options[k - 1] : null;
            var mode = o == null ? null : Modes.Find(o.Mode);
            var random = o?.Source == VoteSource.Random;
            t.Add(($"{id}_rank", o == null ? "" : $"{o.N}  ·  {(random ? "Random" : mode?.Name ?? "")}")); // the number a player types in chat, or after !vote
            t.Add(($"{id}_name", o == null ? "" : random ? $"Random {(mode?.Name ?? "map")}" : o.Label));
            t.Add(($"{id}_size", o?.Size ?? ""));
            t.Add(($"{id}_votes", o == null ? "" : $"{o.Votes} vote{(o.Votes == 1 ? "" : "s")}{(o.Who != "" ? $"  ·  queued by {o.Who}" : "")}"));
            t.Add(($"{id}_warn", ""));
            t.Add(($"{id}_play_text", o == null ? "" : done ? (v.Result!.N == o.N ? "Next map" : "") : mine == o.N ? "Your vote" : $"Vote {o.N}"));
            c.Add((id, "used", o != null));
            c.Add((id, "official", false));
            c.Add((id, "risky", false));
            c.Add((id, "broken", false));
            c.Add((id, "crashed", false));
            c.Add((id, "now", o != null && done && v.Result!.N == o.N));
            c.Add((id, "queued", o?.Source == VoteSource.Queue));
            c.Add((id, "liked", false));
            c.Add((id, "disliked", false));
            c.Add((id, "confirm", o != null && mine == o.N && !done));
            c.Add((id, "big", false));
            c.Add((id, "bigvoted", false));
            c.Add((id, "ads", false));
            c.Add((id, "adsvoted", false));
            c.Add((id, "ours", o != null && catalog?.Maps.FirstOrDefault(m => m.Key == o.Key) is { Ours: true }));
        }
        return (t, c);
    }

    /// <summary>The pictures of the vote's cards: an option's map's picture where the addon has one (a Workshop map of the list, an official
    /// Arms Race map); none for Random or a map without one.</summary>
    public static List<(string Panel, string[] Classes)> Thumbs(VoteState v, HudCatalog? catalog) =>
        Enumerable.Range(1, HudMenu.Cards).Select(k =>
        {
            var o = k <= v.Options.Count ? v.Options[k - 1] : null;
            var entry = o == null || o.Source == VoteSource.Random || catalog == null ? null : catalog.Maps.FirstOrDefault(m => m.Key == o.Key);
            return ($"{HudMenu.Card(k)}_thumb", entry != null ? HudThumbs.Classes(entry) : Array.Empty<string>());
        }).ToList();

    /// <summary>The dialog variable on gg_screen that content v4_1_0's popup inherits, for each player: the map it asks about. The reasons' words are
    /// oswhy1 ... on gg_global, the same for everybody, and their chat words oswhyw1 ...; nothing is sent to the popup's own panels.</summary>
    public const string WhyVar = "oswhy";

    /// <summary>The popup's buttons, one per reason, in DownReasons' order: k from 1.</summary>
    public static string WhyButton(int k) => $"gg_why_{k.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The popup's fixed words, as the dialog variables on gg_global that its labels inherit.</summary>
    public static List<(string Name, string Text)> WhyWords() =>
        DownReasons.All.SelectMany((r, i) => new[] { ($"{WhyVar}{i + 1}", r.Label), ($"{WhyVar}w{i + 1}", r.Word) }).ToList();

    /// <summary>A click on a reason in the popup as the reason it names, or null for any other button.</summary>
    public static DownReasons.Reason? WhyClick(string buttonId) =>
        buttonId.StartsWith("gg_why_", StringComparison.Ordinal) && int.TryParse(buttonId[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var k) && k >= 1 && k <= DownReasons.All.Length ? DownReasons.All[k - 1] : null;

    /// <summary>The grenade drops' state, on gg_global (content v4_1_0's Fun page lights the choice that is on; an older game has no rule for it).</summary>
    public static List<(string Id, string Class, bool On)> NadesClasses(bool? on) => [(HudView.Global, "nd-on", on == true), (HudView.Global, "nd-off", on == false)];

    /// <summary>What a card click says for everybody in chat, under that player's name (the owner's decision of 2026-11-01): the command a
    /// typed vote would be, so that menu users learn the chat way.</summary>
    public static string ChatWords(int option) => $"!vote {option.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The content a page's words need (HudView.NewIds, NewIds32): a game that has not shown it holds it gets the page empty until it
    /// clicks a button only that content has, such as the page's own tab. 0: every game has them.</summary>
    public static int PageLevel(string page) => page switch { "How" => 31, "Fun" => 32, _ => 0 };

    /// <summary>What chat says when a command opens such a page for such a game.</summary>
    public static string PageHint(string page) => $"Click the {(page == "How" ? "How it works" : page)} tab once to load this page";

    /// <summary>A click on card k's Play while the vote is open: !mv &lt;round&gt; &lt;k&gt;; null for anything else (the card's arrows do nothing then).</summary>
    public static string? Click(VoteState v, string buttonId)
    {
        if (!v.Open || v.Round <= 0 || !buttonId.StartsWith("gg_m", StringComparison.Ordinal) || !buttonId.EndsWith("_play", StringComparison.Ordinal)) return null;
        return int.TryParse(buttonId[4..^5], NumberStyles.None, CultureInfo.InvariantCulture, out var k) && k >= 1 && k <= v.Options.Count ? MapVote.Click(v.Round, v.Options[k - 1].N) : null;
    }
}
