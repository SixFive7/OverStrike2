// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Globalization;

/// <summary>THE HISTORY PAGE (content v4.15.0; the owner's 304 a, 2026-11-19): every match a human played, from the plugin's copy of the bridge's history
/// (HudHistory), four wide cards a page, newest first. A card is a match on its map: the map's picture with its mode and On now, its name, Official and size;
/// when it ran and how long, how it ended, everyone who joined, how often the map was played and a fun line; its likes now with Like and Dislike, and Vote for
/// it - the map browser's Play for that map, in the match's mode where the map plays in it. A bar of mode chips, Mine, the order and the search (a chat line
/// typed while the page shows), the count and a pager. Every word is a dialog variable on the player's os_screen and every state a class - on a card
/// (os_h1..4), or on os_screen for the page's own; the buttons take clicks only. One per player (HudPlayer.History), like the browser.</summary>
public sealed class HudHistoryPage
{
    public const int Cards = 4;

    public int Page;            // zero-based; kept on the pages there are
    public string Mode = "";    // the mode chips: a mode's id, or "" for every mode
    public bool Mine;           // only the matches this player was in
    public bool Oldest;         // the oldest first
    public string Query = "";   // a chat line typed while the page shows: a map's name or a player's

    public const string FirstButton = "os_hpg_first", PrevButton = "os_hpg_prev", NextButton = "os_hpg_next", LastButton = "os_hpg_last",
        MineButton = "os_hf_mine", OrderButton = "os_hf_order", ClearButton = "os_hf_clear";

    /// <summary>The mode chips: their state is a class on os_screen (hm-all, hm-armsrace, hm-deathmatch, hm-wingman).</summary>
    public static readonly (string Id, string Mode)[] ModeButtons =
        [("os_hf_all", ""), ("os_hf_ar", Modes.ArmsRaceId), ("os_hf_dm", Modes.DeathmatchId), ("os_hf_wm", Modes.WingmanId)];

    public static string Card(int k) => $"os_h{k.ToString(CultureInfo.InvariantCulture)}";
    public static string Thumb(int k) => $"{Card(k)}_thumb";
    static string Var(int k, char word) => $"osh{k.ToString(CultureInfo.InvariantCulture)}{word}";

    /// <summary>Every button of the page, each taking clicks only: the cards' Like, Dislike and Vote for it, the chips, Mine, the order, Clear and the pager.</summary>
    public static IEnumerable<string> Buttons =>
        Enumerable.Range(1, Cards).SelectMany(k => new[] { $"{Card(k)}_up", $"{Card(k)}_down", $"{Card(k)}_play" })
            .Concat(ModeButtons.Select(b => b.Id)).Concat([MineButton, OrderButton, ClearButton, FirstButton, PrevButton, NextButton, LastButton]);

    /// <summary>The page with no match in it yet.</summary>
    public const string Empty = "No match played yet - Every match a human plays shows here, the newest first";

    // ---- the matches ----

    (HudHistory? Past, long Version, long Account, string Mode, bool Mine, bool Oldest, string Query) lastFor;
    List<HudHistory.Entry> last = [];

    /// <summary>The matches this player's chips, Mine and search let through, in this player's order. Worked out once per state of the page and the history.</summary>
    public List<HudHistory.Entry> Matches(HudHistory? past, long account)
    {
        var state = (past, past?.Version ?? -1, account, Mode, Mine, Oldest, Query);
        if (state == lastFor) return last;
        lastFor = state;
        var q = Hud.Clean(Query, HudSearch.MaxQuery).Trim();
        var list = new List<HudHistory.Entry>();
        foreach (var e in past?.Entries ?? [])
        {
            if (Mode != "" && e.Mode != Mode) continue;
            if (Mine && e.Players.All(p => p.Account != account)) continue;
            if (q != "" && !(e.MapName.Contains(q, StringComparison.OrdinalIgnoreCase) || e.LogName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.Players.Any(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))) continue;
            list.Add(e);
        }
        if (Oldest) list.Reverse();
        return last = list;
    }

    public static int PagesFor(int count) => Math.Max(1, (count + Cards - 1) / Cards);

    /// <summary>This player's page of the matches, kept on the pages there are.</summary>
    public List<HudHistory.Entry> PageOf(List<HudHistory.Entry> matches)
    {
        Page = Math.Clamp(Page, 0, PagesFor(matches.Count) - 1);
        return [.. matches.Skip(Page * Cards).Take(Cards)];
    }

    /// <summary>A click on the page's own controls: true when it was one (the plugin draws again).</summary>
    public bool Click(string buttonId, int matchCount)
    {
        foreach (var (id, mode) in ModeButtons)
            if (buttonId == id) { Mode = mode; Page = 0; return true; }
        switch (buttonId)
        {
            case MineButton: Mine = !Mine; Page = 0; return true;
            case OrderButton: Oldest = !Oldest; Page = 0; return true;
            case ClearButton: Query = ""; Page = 0; return true;
            case FirstButton: Page = 0; return true;
            case PrevButton: Page = Math.Max(0, Page - 1); return true;
            case NextButton: Page = Math.Min(PagesFor(matchCount) - 1, Page + 1); return true;
            case LastButton: Page = PagesFor(matchCount) - 1; return true;
        }
        return false;
    }

    /// <summary>A chat line typed while the page shows: the matches on a map, or with a player, whose name has these words, from the first page.</summary>
    public void Find(string words)
    {
        Query = Hud.Clean(words, HudSearch.MaxQuery).Trim();
        Page = 0;
    }

    // ---- what the whole history says of a map: how often it was played, and when first ----

    static (HudHistory? Past, long Version, Dictionary<string, (int Plays, DateTimeOffset First)> Maps) facts = (null, -1, new());

    /// <summary>Every map's plays in the history and its first match, worked out once per history.</summary>
    public static Dictionary<string, (int Plays, DateTimeOffset First)> MapFacts(HudHistory? past)
    {
        if (past == null) return new(StringComparer.OrdinalIgnoreCase);
        if (ReferenceEquals(facts.Past, past) && facts.Version == past.Version) return facts.Maps;
        var maps = new Dictionary<string, (int Plays, DateTimeOffset First)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in past.Entries)
            maps[e.Key] = maps.TryGetValue(e.Key, out var f) ? (f.Plays + 1, e.From < f.First ? e.From : f.First) : (1, e.From);
        facts = (past, past.Version, maps);
        return maps;
    }

    /// <summary>How often a map was played: "Played once", "Played twice", "Played 14 times" - every match on it, in any mode.</summary>
    public static string Played(int plays) => plays switch { <= 1 => "Played once", 2 => "Played twice", _ => $"Played {plays.ToString(CultureInfo.InvariantCulture)} times" };

    /// <summary>A card's fun line: the map's first match and the most humans in this one at once, and Arms Race's winner or the match's top fragger where they
    /// were recorded: "First played Fri 3 Oct  ·  Most at once: 9  ·  Won by Alice".</summary>
    public static string Fun(HudHistory.Entry e, DateTimeOffset firstPlayed)
    {
        var parts = new List<string> { $"First played {HudHistory.Day(firstPlayed)}" };
        if (e.Most > 0) parts.Add($"Most at once: {e.Most.ToString(CultureInfo.InvariantCulture)}");
        if (e.Winner > 0 && e.NameOf(e.Winner) is { Length: > 0 } winner) parts.Add($"Won by {winner}");
        else if (e.TopFragger is { } top) parts.Add($"Most kills: {(top.Player.Name.Trim() == "" ? "a player" : top.Player.Name.Trim())}");
        return string.Join("  ·  ", parts);
    }

    /// <summary>A match's map as the map list has it: by its Workshop id, or an official map by its name; null for a map on no list now.</summary>
    public static HudCatalog.Entry? MapOf(HudHistory.Entry e, HudCatalog? catalog) =>
        catalog == null ? null : e.MapId > 0 ? catalog.Find(e.Key) : catalog.Find(e.LogName) is { Official: true } o ? o : catalog.Find(e.MapName) is { Official: true } n ? n : null;

    /// <summary>The words on a card's size: an official map's, or the map list's size.</summary>
    static string SizeOf(HudCatalog.Entry? m) => m == null ? "" : m.Official ? HudMenu.OfficialSize : m.SizeMb <= 0 ? ""
        : m.SizeMb >= 1000 ? $"{(m.SizeMb / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)} GB" : $"{m.SizeMb.ToString(CultureInfo.InvariantCulture)} MB";

    // ---- the page, for this player ----

    /// <summary>The page's words (variables on os_screen), its classes - the cards' and os_screen's - and the cards' pictures, for this player. past null: the
    /// history is still on its way (the count says so).</summary>
    public (List<(string Name, string Text)> Vars, List<(string Id, string Class, bool On)> Classes, List<(string Panel, string[] Classes)> Pictures)
        View(HudHistory? past, HudCatalog? catalog, HudState s, HudBrowser b, long account, DateTime now, VoteState? vote = null, string serverMode = "")
    {
        var vars = new List<(string, string)>();
        var classes = new List<(string, string, bool)>();
        var pictures = new List<(string, string[])>();
        var matches = Matches(past, account);
        var page = PageOf(matches);
        var all = past?.Entries ?? [];
        var facts = MapFacts(past);
        var filtered = Mode != "" || Mine || Query != "";
        vars.Add(("oshcount", past == null ? "Loading the history..." : filtered ? $"{matches.Count.ToString(CultureInfo.InvariantCulture)} of {Count(all.Count, "match", "matches")}"
            : all.Count == 0 ? "No match yet" : $"{Count(all.Count, "match", "matches")}  ·  {Count(facts.Count, "map", "maps")}  ·  since {HudHistory.Day(all[^1].From)}"));
        vars.Add(("oshpage", $"Page {(Page + 1).ToString(CultureInfo.InvariantCulture)} of {PagesFor(matches.Count).ToString(CultureInfo.InvariantCulture)}"));
        vars.Add(("oshempty", past == null || matches.Count > 0 ? "" : all.Count == 0 ? Empty
            : Query != "" ? $"No match on a map or with a player like \"{Hud.Clean(Query, 40)}\"" : "No match played matches these filters"));
        classes.Add((HudView.Screen, "hfirst", Page == 0));
        classes.Add((HudView.Screen, "hlast", Page >= PagesFor(matches.Count) - 1));
        foreach (var (_, mode) in ModeButtons) classes.Add((HudView.Screen, mode == "" ? "hm-all" : $"hm-{mode}", Mode == mode));
        classes.Add((HudView.Screen, "hmine", Mine));
        classes.Add((HudView.Screen, "holdest", Oldest));
        classes.Add((HudView.Screen, "hquery", Query != ""));
        classes.Add((HudView.Screen, "hnone", past != null && matches.Count == 0));
        for (var k = 1; k <= Cards; k++)
        {
            var id = Card(k);
            var e = k <= page.Count ? page[k - 1] : null;
            var m = e == null ? null : MapOf(e, catalog);
            var key = e == null ? "" : m?.Key ?? e.Key;
            var mine = m == null || catalog == null ? 0 : catalog.MyVote(account, m.Key);
            vars.Add((Var(k, 'n'), e == null ? "" : m?.Name ?? e.MapName));
            vars.Add((Var(k, 'w'), e == null ? "" : HudHistory.When(e)));
            vars.Add((Var(k, 'd'), e == null ? "" : HudHistory.Duration(e)));
            vars.Add((Var(k, 'e'), e == null ? "" : HudHistory.Ended(e)));
            vars.Add((Var(k, 'p'), e == null ? "" : HudHistory.Joined(e)));
            vars.Add((Var(k, 'c'), e == null ? "" : Played(facts.TryGetValue(e.Key, out var f) ? f.Plays : 1)));
            vars.Add((Var(k, 'f'), e == null ? "" : Fun(e, facts.TryGetValue(e.Key, out var g) ? g.First : e.From)));
            vars.Add((Var(k, 'v'), m == null ? "" : HudMenu.Likes(m)));
            vars.Add((Var(k, 's'), SizeOf(m)));
            vars.Add((Var(k, 'a'), e == null ? "" : m == null ? "Not on the list" : HudMenu.PlayText(m, s, b, now, vote, account, serverMode)));
            classes.Add((id, "used", e != null));
            classes.Add((id, "liked", mine > 0));
            classes.Add((id, "disliked", mine < 0));
            classes.Add((id, "official", m?.Official == true));
            classes.AddRange(HudVote.CardModeClasses(id, e == null ? null : e.Mode != "" ? e.Mode : m?.FirstMode));
            classes.Add((id, "hinc", e?.Incomplete == true));
            classes.Add((id, "now", e != null && key == s.Key && s.Key != ""));
            classes.Add((id, "queued", e != null && vote is { Open: true, Round: > 0 } && vote.Options.Any(o => o.Key == key && o.Source is VoteSource.Picked or VoteSource.Rolled)));
            classes.Add((id, "mine", e != null && e.Players.Any(p => p.Account == account)));
            pictures.Add((Thumb(k), m == null ? [] : HudThumbs.Classes(m)));
        }
        return (vars, classes, pictures);
    }

    static string Count(int n, string one, string many) => $"{n.ToString(CultureInfo.InvariantCulture)} {(n == 1 ? one : many)}";

    /// <summary>A card's button as the action it asks the bridge for - the map browser's for that match's map: Like, Dislike (the bridge asks why, the crash
    /// among the reasons), and Vote for it - a risky map's first click arming the confirmation, a click on its pick's countdown its Cancel. null: nothing to
    /// send (no such card, a map on no list, the map that is on outside a vote, the second click of a double click).</summary>
    public string? Command(string buttonId, HudHistory? past, HudCatalog? catalog, HudState? s, HudBrowser b, long account, DateTime now, string serverMode = "", VoteState? vote = null)
    {
        if (!buttonId.StartsWith("os_h", StringComparison.Ordinal) || buttonId.Length < 7 || !char.IsDigit(buttonId[4]) || s == null) return null;
        var under = buttonId.IndexOf('_', 4);
        if (under < 0 || !int.TryParse(buttonId[4..under], NumberStyles.None, CultureInfo.InvariantCulture, out var k) || k < 1 || k > Cards) return null;
        var page = PageOf(Matches(past, account));
        if (k > page.Count || MapOf(page[k - 1], catalog) is not { } m) return null;
        var e = page[k - 1];
        switch (buttonId[(under + 1)..])
        {
            case "up": return $"!up {m.Arg}";
            case "down": return $"!down {m.Arg}";
            case "play":
                if (m.Key == s.Key && s.Key != "" && vote is not { Open: true, Round: > 0 }) return null; // the map that is on: Keep on the vote, nothing outside one
                if (b.DoubleClick(m.Key, now)) return null; // the second click of a double click: nothing - never the confirmation of a risky map's first click
                if (s.PickIn(m.Key) >= 0) return $"!cancel pick {m.Key}"; // its confirmed pick counts down: the click is its Cancel, whoever clicks
                var arg = HudMenu.PlayArg(m, e.Mode, serverMode); // in the match's own mode, where the map plays in it
                if (m.Removed) return $"!map {arg}";
                if (m.Risky && !(b.ConfirmKey == m.Key && now < b.ConfirmUntil)) { b.ConfirmKey = m.Key; b.ConfirmUntil = now.AddSeconds(HudBrowser.ConfirmSeconds); }
                else { b.ConfirmKey = ""; b.ConfirmUntil = default; }
                return $"!map {arg}";
        }
        return null;
    }
}
