// The in-game menu's map browser and game pages: the logic both programs share, like Hud.cs. The bridge serves the map catalog (HudCatalog,
// on /catalog/<secret>) and tests everything here in --selftest; the OverStrike2 plugin keeps one HudBrowser per player slot, works out that
// player's page from the catalog, and puts it on screen through HudMenu's texts and classes. Plain BCL only.
//
// Every click is a server round trip, so all of this is cheap on purpose: sorting and filtering ~1,000 entries is well under a
// millisecond, a page is 8 cards, and the plugin sends only what changed.
using System.Globalization;

/// <summary>One player's view of the map browser: sort, filters, search and page, plus a risky map's first click waiting for the second (per
/// player, like the bridge's own confirm). Lives in the plugin, one per player slot; a new player in the slot starts from a
/// fresh one.</summary>
public sealed class HudBrowser
{
    public enum SortBy { Rank, Votes, Name, Size }
    public enum Band { All, Tiny, Small, Mid, Large }

    public const int PerPage = 8;            // the browser's cards: two columns of four (the ballot's page is MapVote.PageCards)
    public const double ConfirmSeconds = 30; // the bridge's own window for confirming a broken or crashed map

    /// <summary>The size chips: under 25 MB, 25-120, 120-400, 400 MB and up.</summary>
    public static Band BandOf(int sizeMb) => sizeMb < 25 ? Band.Tiny : sizeMb < 120 ? Band.Small : sizeMb < 400 ? Band.Mid : Band.Large;

    public SortBy Sort = SortBy.Rank;
    public bool Descending;                  // the natural direction flipped: a second click on the sort that is on
    public Band Size = Band.All;
    public bool Liked, Official, Ours;        // Ours: the server's own maps (ours.json; Over Strike 2's Our maps)
    public string Mode = "";                 // the mode filter (content v4.2.0): a mode's id, or "" for every mode's maps
    public string Query = "";                // what !find (!s) asked for in chat, a chat line typed on MAPS > Browse (233), or a Workshop link or id pasted there
    public int Page;                         // zero-based; clamped whenever the matches are counted
    public string ConfirmKey = "";           // a risky map whose Play was clicked once...
    public DateTime ConfirmUntil;            // ...and until when the second click confirms it

    /// <summary>Button ids of the browser's own controls.</summary>
    public static readonly (string Id, SortBy Sort)[] SortButtons =
        [("os_sort_rank", SortBy.Rank), ("os_sort_votes", SortBy.Votes), ("os_sort_name", SortBy.Name), ("os_sort_size", SortBy.Size)];
    public static readonly (string Id, Band Band)[] BandButtons =
        [("os_fl_all", Band.All), ("os_fl_tiny", Band.Tiny), ("os_fl_small", Band.Small), ("os_fl_mid", Band.Mid), ("os_fl_large", Band.Large)];
    /// <summary>The mode filter's chips (content v4.2.0): their state is a class on os_screen (ModeClass), so nothing is sent to these panels.</summary>
    public static readonly (string Id, string Mode)[] ModeButtons =
        [("os_fm_all", ""), ("os_fm_ar", Modes.ArmsRaceId), ("os_fm_dm", Modes.DeathmatchId), ("os_fm_wm", Modes.WingmanId)];

    /// <summary>The class on os_screen that lights a mode chip.</summary>
    public static string ModeClass(string mode) => mode == "" ? "fm-all" : $"fm-{mode}";

    public const string LikedButton = "os_fl_liked", OfficialButton = "os_fl_official", OursButton = "os_fl_ours", ClearButton = "os_q_clear",
        FirstButton = "os_pg_first", PrevButton = "os_pg_prev", NextButton = "os_pg_next", LastButton = "os_pg_last";

    /// <summary>The search area's hint (content v4.9.0): "Search  Press Y and type a map name" (233), a button whose click says how on the window's line and in the
    /// player's chat (ChatReply.HowToSearch) - typing is chat's, and neither the HUD nor the server can open the chat box (the client refuses messagemode from the server). Content v4.8.0 typed on an on-screen keyboard: its search box
    /// (OldSearchBox) opened it, and its forty keys were os_kb_*. Release 196 removed it: the keyboard never opens since, and a
    /// click on that box, or on any key, asks the same as the hint's own and changes nothing.</summary>
    public const string HintButton = "os_q_how", OldSearchBox = "os_q_kbd";

    /// <summary>A click that asks how to search: the hint's, or v4.8.0's search box or keys.</summary>
    public static bool AsksHowToSearch(string buttonId) =>
        buttonId == HintButton || buttonId == OldSearchBox || buttonId.StartsWith("os_kb_", StringComparison.Ordinal);
    // (the pager's Next pulses while more pages follow, Prev while this is not the first page: the stylesheet's, off the buttons' "off" class)

    /// <summary>The maps this player's filters and search let through, in this player's order. Officials have no rank: they sort after
    /// the ranked maps by rank (first when only officials are shown, which is the same thing). The search is full text (HudSearch):
    /// every typed word in the name, the Workshop tags or the description; a number is also a rank or a Workshop id. While searching
    /// with the default sort, the best matches come first. Worked out once for each state of the browser and the catalog: the plugin asks
    /// several times a frame for every open window, and the list runs to thousands of maps.</summary>
    public List<HudCatalog.Entry> Matches(HudCatalog catalog, long account, HudSearch? search = null)
    {
        var state = (catalog, search, account, Query, Sort, Descending, Size, Liked, Official, Ours, Mode);
        if (lastMatches != null && lastMatchesFor.Equals(state)) return lastMatches;
        lastMatchesFor = state;
        return lastMatches = Work(catalog, account, search);
    }

    (HudCatalog?, HudSearch?, long, string, SortBy, bool, Band, bool, bool, bool, string) lastMatchesFor;
    List<HudCatalog.Entry>? lastMatches;

    List<HudCatalog.Entry> Work(HudCatalog catalog, long account, HudSearch? search)
    {
        var q = Hud.Clean(Query, HudSearch.MaxQuery).Trim();
        if (WorkshopQuery(catalog) != null) return []; // a Workshop id no listed map has: its card answers it, alone (map cards under it ran off the page)
        var words = HudSearch.Words(q); // lowered as search.json's texts are (HudSearch.Normal)
        var index = search ?? new HudSearch();
        IEnumerable<HudCatalog.Entry> hits = catalog.Maps;
        if (Size != Band.All) hits = hits.Where(m => !m.Official && BandOf(m.SizeMb) == Size);
        if (Liked) hits = hits.Where(m => catalog.MyVote(account, m.Key) > 0);
        if (Official) hits = hits.Where(m => m.Official);
        if (Ours) hits = hits.Where(m => m.Ours);
        if (Mode != "") hits = hits.Where(m => m.RankIn(Mode) >= 0);
        var score = new Dictionary<string, int>();
        if (q != "")
        {
            int Score(HudCatalog.Entry m) => m.Arg.Equals(q, StringComparison.OrdinalIgnoreCase) || m.Id.ToString(CultureInfo.InvariantCulture) == q ? 10_000
                : index.Score(m.Key, m.Name, words);
            foreach (var m in hits) if (Score(m) is > 0 and var n) score[m.Key] = n;
            hits = hits.Where(m => score.ContainsKey(m.Key));
        }
        // One mode: its own list's order, its official maps last. Every mode: Arms Race's list and official maps, then each other mode's own maps in
        // their lists' order, then the official maps only those modes have.
        int RankKey(HudCatalog.Entry m)
        {
            if (Mode != "") return m.RankIn(Mode) > 0 ? m.RankIn(Mode) : 100_000;
            if (m.RankIn(Modes.ArmsRaceId) > 0) return m.RankIn(Modes.ArmsRaceId);
            if (m.RankIn(Modes.ArmsRaceId) == 0) return 100_000;
            if (m.RankDm > 0) return 200_000 + m.RankDm;
            if (m.RankWm > 0) return 400_000 + m.RankWm;
            return 600_000;
        }
        IOrderedEnumerable<HudCatalog.Entry> sorted = Sort switch
        {
            SortBy.Votes => hits.OrderByDescending(m => m.Up - m.Down).ThenByDescending(m => m.Up).ThenBy(RankKey),
            SortBy.Name => hits.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(RankKey),
            SortBy.Size => hits.OrderBy(m => m.Official ? int.MaxValue : m.SizeMb).ThenBy(RankKey),
            _ when q != "" && !Descending => hits.OrderByDescending(m => score.GetValueOrDefault(m.Key)).ThenBy(RankKey),
            _ => hits.OrderBy(RankKey).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
        };
        var list = sorted.ToList();
        if (Descending) list.Reverse();
        return list;
    }

    public static int Pages(int count, int perPage = PerPage) => Math.Max(1, (count + perPage - 1) / perPage);

    /// <summary>This player's pages for that many matches.</summary>
    public int PagesFor(int count) => Pages(count, PerPage);

    /// <summary>This player's page: the entries on it (eight; fewer on the last) after clamping the page number to what exists.</summary>
    public List<HudCatalog.Entry> PageOf(List<HudCatalog.Entry> matches)
    {
        Page = Math.Clamp(Page, 0, PagesFor(matches.Count) - 1);
        return matches.Skip(Page * PerPage).Take(PerPage).ToList();
    }

    /// <summary>The Workshop id the query is, when no map in the catalog has it: the menu then shows that item's card (WorkshopCard).</summary>
    public long? WorkshopQuery(HudCatalog? catalog) =>
        WorkshopCard.IdOf(Query) is { } id && catalog != null && !catalog.Maps.Any(m => m.Id == id) ? id : null;

    /// <summary>A click on one of the browser's own controls. True when it was one (the page then has to be worked out again). The search hint and
    /// v4.8.0's typing controls are none: the plugin answers them in chat (AsksHowToSearch).</summary>
    public bool Click(string buttonId, int matchCount)
    {
        foreach (var (id, sort) in SortButtons)
            if (buttonId == id)
            {
                if (Sort == sort) Descending = !Descending; else { Sort = sort; Descending = false; }
                Page = 0;
                return true;
            }
        foreach (var (id, band) in BandButtons)
            if (buttonId == id) { Size = Size == band ? Band.All : band; Page = 0; return true; }
        foreach (var (id, mode) in ModeButtons)
            if (buttonId == id) { Mode = mode; Page = 0; return true; }
        switch (buttonId)
        {
            case LikedButton: Liked = !Liked; Page = 0; return true;
            case OfficialButton: Official = !Official; if (Official) { Size = Band.All; Ours = false; } Page = 0; return true;
            case OursButton: Ours = !Ours; if (Ours) { Size = Band.All; Official = false; } Page = 0; return true;
            case ClearButton: Query = ""; Page = 0; return true;
            case FirstButton: Page = 0; return true;
            case PrevButton: Page = Math.Max(0, Page - 1); return true;
            case NextButton: Page = Math.Min(PagesFor(matchCount) - 1, Page + 1); return true;
            case LastButton: Page = PagesFor(matchCount) - 1; return true;
        }
        return false;
    }

    /// <summary>A new search from the first page (a Workshop link pasted in chat), keeping the sort and dropping the other filters (a
    /// search that a filter hides would look like a search that found nothing).</summary>
    public void Find(string text)
    {
        Query = Hud.Clean(text, HudSearch.MaxQuery);
        Size = Band.All; Liked = false; Official = false; Ours = false; Mode = "";
        Page = 0;
    }
}
