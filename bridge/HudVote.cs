// Over Strike 2's screens that every HUD content can show (2026-11-01): the end-of-match vote (#26, the bridge's side in VoteRound.cs and
// MapVote.cs), drawn on the Maps page's eight cards - so a game still holding an older content (v3_0_0, v3_1_0) can vote too, during
// Valve's review of the update and until it has downloaded it - and the HUD version check.
//
// The version check needs no click and no word from the client: the server sets threshold classes on gg_global - svge<k> while its own
// content level is at least k, svlt<k> while it is below k - and every content from v4_0_0 on carries rules for its own level: a v4_0_0
// game shows "yours is older" under svge41 and "yours is newer" under svlt40, and nothing when the levels match. The server's version
// name rides in the dialog variable osver on gg_global, which the newer layouts' labels inherit. A game older than v4_0_0 has none of
// those rules, so it gets the note line instead - a note that every v4_0_0 game's stylesheet hides (vold on gg_screen).
//
// Shared like Hud.cs (plain BCL): the plugin draws it, the bridge's --selftest checks it.
using System.Globalization;

public static class HudVote
{
    /// <summary>The server's content as a number, for the version check: v4_0_0 is 40. The classes go from FirstLevel to LastLevel.</summary>
    public const int ContentLevel = 40, FirstLevel = 31, LastLevel = 49;

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
        t.Add(("gg_pg_text", mine > 0 ? "Your vote is lit - click another card to change it" : "Click a card's Vote"));
        t.Add(("gg_m_empty", ""));
        c.Add(("gg_m_empty", "used", false));
        c.Add((HudView.Screen, "voting", true));
        for (var k = 1; k <= HudMenu.Cards; k++)
        {
            var id = HudMenu.Card(k);
            var o = k <= v.Options.Count ? v.Options[k - 1] : null;
            var mode = o == null ? null : Modes.Find(o.Mode);
            var random = o?.Source == VoteSource.Random;
            t.Add(($"{id}_rank", o == null ? "" : random ? "Random" : mode != null && mode.Id != v.Mode ? mode.Name : mode?.Name ?? ""));
            t.Add(($"{id}_name", o == null ? "" : random ? $"Random {(mode?.Name ?? "map")}" : o.Label));
            t.Add(($"{id}_size", o?.Size ?? ""));
            t.Add(($"{id}_votes", o == null ? "" : $"{o.Votes} vote{(o.Votes == 1 ? "" : "s")}{(o.Who != "" ? $"  ·  queued by {o.Who}" : "")}"));
            t.Add(($"{id}_warn", ""));
            t.Add(($"{id}_play_text", o == null ? "" : done ? (v.Result!.N == o.N ? "Next map" : "") : mine == o.N ? "Your vote" : "Vote"));
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

    /// <summary>A click on card k's Play while the vote is open: !mv &lt;round&gt; &lt;k&gt;; null for anything else (the card's arrows do nothing then).</summary>
    public static string? Click(VoteState v, string buttonId)
    {
        if (!v.Open || v.Round <= 0 || !buttonId.StartsWith("gg_m", StringComparison.Ordinal) || !buttonId.EndsWith("_play", StringComparison.Ordinal)) return null;
        return int.TryParse(buttonId[4..^5], NumberStyles.None, CultureInfo.InvariantCulture, out var k) && k >= 1 && k <= v.Options.Count ? MapVote.Click(v.Round, v.Options[k - 1].N) : null;
    }
}
