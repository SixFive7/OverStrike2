// The in-game menu's map browser and game pages: the logic both programs share, like Hud.cs. The bridge serves the map catalog (HudCatalog,
// on /catalog/<secret>) and tests everything here in --selftest; the OverStrike2 plugin keeps one HudBrowser per player slot, works out that
// player's page from the catalog, and puts it on screen through HudMenu's texts and classes. Plain BCL only.
//
// Every click is a server round trip, so all of this is cheap on purpose: sorting and filtering ~1,000 entries is well under a
// millisecond, a page is 8 cards, and the plugin sends only what changed.
using System.Globalization;
using System.Text;

/// <summary>Every map the menu can show - the ranked list of maps.json, the Deathmatch and Wingman lists and every mode's official maps - with what the server knows about each:
/// its size, the broken-list verdict, the live crash counter, the votes and its tags. A text codec like HudState's. The bridge bumps Version
/// whenever any of it changes (a vote, a crash); the live state carries the version, so the plugin fetches this again only then.</summary>
public sealed class HudCatalog
{
    public const string Magic = "gg-catalog\t1";

    /// <summary>Key: what the bridge counts votes and crashes by (an official map's name, otherwise the Workshop id). Arg: what a chat
    /// command takes to name it unambiguously (the rank of a ranked map, an official map's name, the id of an unlisted one).
    /// Broken: crashes on the swept broken list (0 = listed without a count), -1 when not on it. Removed: gone from the Workshop
    /// (Workshop.cs) - nobody can download it, so it is refused rather than confirmed. RankAr, RankDm, RankWm: the map's place in that mode's
    /// list, 0 for an official map of that mode, -1 when that mode does not have it; Rank is the place in the first mode that has it.
    /// Tags: its tags' ids in precedence order, comma-separated (MapTags) - field 24; an older plugin reads up to field 23.</summary>
    public sealed record Entry(string Key, string Arg, int Rank, long Id, string Name, int SizeMb, bool Official, int Broken, int Crashes, int Up, int Down,
        bool Removed = false, bool TooBig = false, string SizeClass = "", int M2 = 0, int Big = 0, int Ads = 0, bool Ours = false, string Reason = "",
        int? RankAr = null, int RankDm = -1, int RankWm = -1, string Tags = "")
    {
        public bool Risky => !Removed && (Broken >= 0 || Crashes > 0);

        /// <summary>The map's place in a mode's list: 0 for an official map of that mode, -1 when that mode does not have it. An entry made without its
        /// places is Arms Race's, at its Rank.</summary>
        public int RankIn(string mode) => mode switch { Modes.ArmsRaceId => RankAr ?? Rank, Modes.DeathmatchId => RankDm, Modes.WingmanId => RankWm, _ => -1 };

        /// <summary>The first mode that has the map, in menu order: what a click plays it in unless the filter or the server's mode says otherwise.</summary>
        public string FirstMode => RankIn(Modes.ArmsRaceId) >= 0 ? Modes.ArmsRaceId : RankDm >= 0 ? Modes.DeathmatchId : Modes.WingmanId;
    }

    Dictionary<string, Entry>? byKey;

    /// <summary>The entry of a map key, or null. Built once per catalog: the vote's cards ask for theirs every frame.</summary>
    public Entry? Find(string key)
    {
        byKey ??= Maps.GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.First());
        return byKey.GetValueOrDefault(key);
    }

    /// <summary>A too-big vote, as votes.json, the catalog's "v" lines and the live state's "vote" lines carry it: a downvote (an older
    /// plugin reads it as -1, a dislike) that also marks the map too big. TooBig: measured XL, or marked too big by at least one player;
    /// SizeClass and M2 the measurement (sizes.json); Big the too-big votes on it.</summary>
    public const int TooBigVote = -2;

    /// <summary>An ads vote, carried like a too-big vote: a downvote - an older plugin, taking the sign, reads
    /// it as a dislike - that also marks the map as one that shows ads or sends players to another server. A map with one is never a
    /// random pick (players can still pick it). Ads on an entry: the ads votes on it.</summary>
    public const int AdsVote = -3;

    /// <summary>The other reasons a downvote can give (DownReasons), carried the same way: each a dislike and
    /// nothing more, kept per vote.</summary>
    public const int SmallVote = -4, BrokenVote = -5, SlowVote = -6;

    /// <summary>A vote value as the codecs carry it: like, dislike, or one of the special downvotes; anything else by its sign.</summary>
    public static int Carried(long value) => value is >= SlowVote and <= TooBigVote ? (int)value : Math.Sign(value);

    public long Version;
    public List<Entry> Maps = new();
    public Dictionary<long, Dictionary<string, int>> Votes = new(); // account -> map key -> +1 / -1

    /// <summary>Workshop ids the server refuses outright that the map list does not show - the non-maps, the blocked maps and unlisted
    /// maps gone from the Workshop: id -> its name ("" when unknown) and why, in the card's words ("isn't a playable map - ..."). A typed
    /// or pasted one gets its refusal on the card at once (WorkshopCard.Refuse): no Workshop question, nothing downloaded.</summary>
    public Dictionary<long, (string Name, string Why)> Refused = new();

    public int MyVote(long account, string key) => Votes.TryGetValue(account, out var mine) ? mine.GetValueOrDefault(key) : 0;

    public string Encode()
    {
        var s = new StringBuilder(Magic).Append('\n');
        s.Append("ver\t").Append(Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var m in Maps)
            s.Append(string.Join('\t', "m", Hud.Clean(m.Key, 40), Hud.Clean(m.Arg, 40), I(m.Rank), I(m.Id), Hud.Clean(m.Name, 64), I(m.SizeMb), m.Official ? "1" : "0",
                I(m.Broken), I(m.Crashes), I(m.Up), I(m.Down), m.Removed ? "1" : "0", m.TooBig ? "1" : "0", Hud.Clean(m.SizeClass, 2), I(m.M2), I(m.Big), I(m.Ads), m.Ours ? "1" : "0", Hud.Clean(m.Reason, 64), I(m.RankIn(Modes.ArmsRaceId)), I(m.RankDm), I(m.RankWm),
                MapTags.Clean(m.Tags))).Append('\n'); // an older plugin reads the first twelve (3.1.0: seventeen, 3.2: eighteen, 4.3: twenty, 4.5: twenty-three)
        foreach (var (account, mine) in Votes)
            foreach (var (key, value) in mine)
                s.Append(string.Join('\t', "v", I(account), Hud.Clean(key, 40), I(Carried(value)))).Append('\n'); // an older plugin takes the sign (3.1.0 keeps -2 and takes the sign of -3)
        foreach (var (id, (name, why)) in Refused)
            s.Append(string.Join('\t', "n", I(id), Hud.Clean(name, 60), Hud.Clean(why, 100))).Append('\n'); // an older plugin skips these
        return s.ToString();
    }

    static string I(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>The catalog out of Encode's text, or null when it is not one. Unknown lines are skipped, malformed ones dropped.</summary>
    public static HudCatalog? Decode(string? text)
    {
        if (text == null || !text.StartsWith(Magic)) return null;
        var c = new HudCatalog();
        static long L(string[] f, int i) => i < f.Length && long.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        foreach (var raw in text.Split('\n').Skip(1))
        {
            var f = raw.TrimEnd('\r').Split('\t');
            switch (f[0])
            {
                case "ver": c.Version = L(f, 1); break;
                case "m" when f.Length >= 12 && f[1] != "":
                    c.Maps.Add(new Entry(f[1], f[2], (int)L(f, 3), L(f, 4), f[5], (int)L(f, 6), f[7] == "1", (int)L(f, 8), (int)L(f, 9), (int)L(f, 10), (int)L(f, 11),
                        f.Length > 12 && f[12] == "1", f.Length > 13 && f[13] == "1", f.Length > 14 ? f[14] : "", (int)L(f, 15), (int)L(f, 16), (int)L(f, 17),
                        f.Length > 18 && f[18] == "1", f.Length > 19 ? f[19] : "", f.Length > 22 ? (int)L(f, 20) : (int)L(f, 3), f.Length > 22 ? (int)L(f, 21) : -1, f.Length > 22 ? (int)L(f, 22) : -1,
                        f.Length > 23 ? MapTags.Clean(f[23]) : ""));
                    break;
                case "v" when f.Length >= 4 && L(f, 1) > 0 && f[2] != "" && L(f, 3) != 0:
                    var account = L(f, 1);
                    if (!c.Votes.TryGetValue(account, out var mine)) c.Votes[account] = mine = new();
                    mine[f[2]] = Carried(L(f, 3));
                    break;
                case "n" when f.Length >= 4 && L(f, 1) > 0 && f[3] != "":
                    c.Refused[L(f, 1)] = (f[2], f[3]);
                    break;
            }
        }
        return c;
    }
}

/// <summary>Why a map is where it is, in one word: one table for every surface - the ballot's cards and the map browser's (the first two that
/// apply, as chips), chat and the news line (the first "why" tag, in words), the website and the Sync page. The table is in precedence order:
/// the vote's state, a caution, then why the map is there. The bridge works out each map's tags (Popularity.cs) and sends their ids on the
/// catalog's entries (field 24) and the vote's cards (mvo field 11), comma-separated in this order; the vote's state - next, lead, tie, on -
/// is the screen's own to add. Shared like Hud.cs: the plugin draws them, the bridge's website and --selftest use the same table.</summary>
public static class MapTags
{
    /// <summary>One tag: its id on the wire, its chip (12 characters at most, upper case), its words in a sentence, its family, its colour, its
    /// chat colour (CounterStrikeSharp's ChatColors name).</summary>
    public sealed record Tag(string Id, string Chip, string Words, string Family, string Colour, string Chat);

    public const string VoteFamily = "vote", PlayerFamily = "player", CautionFamily = "caution", UsFamily = "us", FreshFamily = "fresh",
        WorkshopFamily = "workshop", DiscoveryFamily = "discovery";

    /// <summary>Chip text is dark on every chip.</summary>
    public const string ChipText = "#10141A";

    /// <summary>The chips a card shows: the first this many tags that apply.</summary>
    public const int OnCard = 2;

    public const string Next = "next", Lead = "lead", Tie = "tie", On = "on", Picked = "picked", Big = "big", Ads = "ads", Ours = "ours", Liked = "liked",
        Upload = "upload", Updated = "updated", Trending = "trending", TopRated = "toprated", Popular = "popular", NewToUs = "newtous";

    public static readonly Tag[] All =
    [
        new(Next, "NEXT MAP", "the next map", VoteFamily, "#FFD47E", "Gold"),
        new(Lead, "LEADING", "leading", VoteFamily, "#E9B44C", "Gold"),
        new(Tie, "TIE", "tied", VoteFamily, "#FFB547", "Gold"),
        new(On, "ON NOW", "on now", VoteFamily, "#E9B44C", "Gold"),
        new(Big, "TOO BIG", "too big for few players", CautionFamily, "#F07C6B", "LightRed"),
        new(Ads, "ADS", "shows ads", CautionFamily, "#F07C6B", "LightRed"),
        new(Picked, "PICKED", "picked by a player", PlayerFamily, "#F1EDE5", "White"),
        new(Ours, "OUR MAP", "our own map", UsFamily, "#4FD6A2", "Green"),
        new(Liked, "LIKED BY US", "liked by us", UsFamily, "#7BD88F", "Green"),
        new(Upload, "NEW UPLOAD", "a recent Workshop upload", FreshFamily, "#63D8E8", "LightBlue"),
        new(Updated, "UPDATED", "a recent Workshop update", FreshFamily, "#A6EAF2", "LightBlue"),
        new(Trending, "TRENDING", "trending on the Workshop", WorkshopFamily, "#CFD6DE", "Silver"),
        new(TopRated, "TOP RATED", "top rated on the Workshop", WorkshopFamily, "#CFD6DE", "Silver"),
        new(Popular, "POPULAR", "Workshop popular", WorkshopFamily, "#CFD6DE", "Silver"),
        new(NewToUs, "NEW TO US", "new to us", DiscoveryFamily, "#B48CFF", "LightPurple"),
    ];

    public static Tag? Find(string? id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>A tag that says why the map is there (not the vote's state, not a caution).</summary>
    public static bool IsWhy(Tag t) => t.Family is PlayerFamily or UsFamily or FreshFamily or WorkshopFamily or DiscoveryFamily;

    static int Place(string id) => Array.FindIndex(All, t => t.Id == id);

    /// <summary>Ids as the wire carries them: known ones only, each once, in precedence order, comma-separated.</summary>
    public static string Join(IEnumerable<string> ids) => string.Join(',', ids.Where(i => Find(i) != null).Distinct().OrderBy(Place));

    /// <summary>A wire value made safe: what Join would make of it (an unknown id or a stray character is dropped).</summary>
    public static string Clean(string? wire) => Join(Split(wire));

    /// <summary>The ids of a wire value, in its order.</summary>
    public static List<string> Split(string? wire) => (wire ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>The first tag that says why the map is there, or null. Without <paramref name="picked"/> a player's pick is no reason: the line
    /// that names the player says it already.</summary>
    public static Tag? FirstWhy(string? wire, bool picked = true) =>
        Split(wire).Select(Find).FirstOrDefault(t => t != null && IsWhy(t) && (picked || t.Id != Picked));

    /// <summary>The first "why" tag in words, for chat and the news line: "liked by us", or "" when there is none.</summary>
    public static string WhyWords(string? wire, bool picked = true) => FirstWhy(wire, picked)?.Words ?? "";

    /// <summary>The chips a card shows: the first OnCard tags of the wire value.</summary>
    public static List<Tag> Chips(string? wire) => Split(wire).Select(Find).OfType<Tag>().Take(OnCard).ToList();
}

/// <summary>One player's view of the map browser: sort, filters, search and page, plus a risky map's first click waiting for the second (per
/// player, like the bridge's own confirm). Lives in the plugin, one per player slot; a new player in the slot starts from a
/// fresh one.</summary>
public sealed class HudBrowser
{
    public enum SortBy { Rank, Votes, Name, Size }
    public enum Band { All, Tiny, Small, Mid, Large }

    public const int PerPage = 8;            // the browser's cards: two columns of four (the ballot's page is MapVote.PageCards)
    public const int PerPageTyping = 4;      // ...and the keyboard covers the second row of them while it is open
    public const double ConfirmSeconds = 30; // the bridge's own window for confirming a broken or crashed map

    /// <summary>The size chips: under 25 MB, 25-120, 120-400, 400 MB and up.</summary>
    public static Band BandOf(int sizeMb) => sizeMb < 25 ? Band.Tiny : sizeMb < 120 ? Band.Small : sizeMb < 400 ? Band.Mid : Band.Large;

    public SortBy Sort = SortBy.Rank;
    public bool Descending;                  // the natural direction flipped: a second click on the sort that is on
    public Band Size = Band.All;
    public bool Liked, Official, Ours;        // Ours: the server's own maps (ours.json; Over Strike 2's Our maps)
    public string Mode = "";                 // the mode filter (content v4_2_0): a mode's id, or "" for every mode's maps
    public string Query = "";                // what the on-screen keyboard typed (HudKeyboard), or a Workshop link pasted in chat
    public bool Keyboard;                    // the keyboard is open on the Maps page
    public int Page;                         // zero-based; clamped whenever the matches are counted
    public string ConfirmKey = "";           // a risky map whose Play was clicked once...
    public DateTime ConfirmUntil;            // ...and until when the second click confirms it

    /// <summary>Button ids of the browser's own controls.</summary>
    public static readonly (string Id, SortBy Sort)[] SortButtons =
        [("gg_sort_rank", SortBy.Rank), ("gg_sort_votes", SortBy.Votes), ("gg_sort_name", SortBy.Name), ("gg_sort_size", SortBy.Size)];
    public static readonly (string Id, Band Band)[] BandButtons =
        [("gg_fl_all", Band.All), ("gg_fl_tiny", Band.Tiny), ("gg_fl_small", Band.Small), ("gg_fl_mid", Band.Mid), ("gg_fl_large", Band.Large)];
    /// <summary>The mode filter's chips (content v4_2_0): their state is a class on gg_screen (ModeClass), so nothing is sent to these panels.</summary>
    public static readonly (string Id, string Mode)[] ModeButtons =
        [("gg_fm_all", ""), ("gg_fm_ar", Modes.ArmsRaceId), ("gg_fm_dm", Modes.DeathmatchId), ("gg_fm_wm", Modes.WingmanId)];

    /// <summary>The class on gg_screen that lights a mode chip.</summary>
    public static string ModeClass(string mode) => mode == "" ? "fm-all" : $"fm-{mode}";

    public const string LikedButton = "gg_fl_liked", OfficialButton = "gg_fl_official", OursButton = "gg_fl_ours", ClearButton = "gg_q_clear",
        FirstButton = "gg_pg_first", PrevButton = "gg_pg_prev", NextButton = "gg_pg_next", LastButton = "gg_pg_last";
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
        var q = Hud.Clean(Query, HudSearch.MaxQuery).Trim().ToLowerInvariant();
        if (WorkshopQuery(catalog) != null) return []; // a Workshop id no listed map has: its card answers it, alone (map cards under it ran off the page)
        var words = HudSearch.Words(q);
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

    /// <summary>Cards on this player's page: eight, or the four the keyboard leaves showing while it is open.</summary>
    public int PageSize => Keyboard ? PerPageTyping : PerPage;

    /// <summary>This player's pages for that many matches, at this player's page size.</summary>
    public int PagesFor(int count) => Pages(count, PageSize);

    /// <summary>This player's page: the entries on it (eight, four while typing; fewer on the last) after clamping the page number to
    /// what exists.</summary>
    public List<HudCatalog.Entry> PageOf(List<HudCatalog.Entry> matches)
    {
        Page = Math.Clamp(Page, 0, PagesFor(matches.Count) - 1);
        return matches.Skip(Page * PageSize).Take(PageSize).ToList();
    }

    /// <summary>The keyboard opens or closes, and the page follows so the first card shown stays the first card shown (a page of eight
    /// is two pages of four).</summary>
    void SetKeyboard(bool open)
    {
        if (open == Keyboard) return;
        Page = open ? Page * (PerPage / PerPageTyping) : Page / (PerPage / PerPageTyping);
        Keyboard = open;
    }

    /// <summary>The Workshop id the query is, when no map in the catalog has it: the menu then shows that item's card (WorkshopCard).</summary>
    public long? WorkshopQuery(HudCatalog? catalog) =>
        WorkshopCard.IdOf(Query) is { } id && catalog != null && !catalog.Maps.Any(m => m.Id == id) ? id : null;

    /// <summary>A click on one of the browser's own controls or the keyboard's keys. True when it was one (the page then has to be
    /// worked out again).</summary>
    public bool Click(string buttonId, int matchCount)
    {
        if (buttonId == HudKeyboard.Open) { SetKeyboard(!Keyboard); return true; }
        if (buttonId == HudKeyboard.Done) { SetKeyboard(false); return true; }
        if (HudKeyboard.Press(Query, buttonId) is { } typed)
        {
            if (typed != Query) { Query = typed; Page = 0; }
            return true;
        }
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

/// <summary>The menu's pages as texts and classes for one player, out of the catalog, the live state and that player's browser, and a
/// click on them as the chat command it stands for. Card k (1-10) is panel gg_m{k}; its parts are gg_m{k}_*. The browser shows the first eight,
/// the ballot ten.</summary>
public static class HudMenu
{
    public const int Cards = MapVote.PageCards > HudBrowser.PerPage ? MapVote.PageCards : HudBrowser.PerPage;

    /// <summary>Previous: the map before this one, to the vote - on the Maps page (on the Queue page of the contents before v4_6_0).</summary>
    public const string NadesOn = "gg_set_nades_on", NadesOff = "gg_set_nades_off", Time10 = "gg_set_time_10", Time60 = "gg_set_time_60",
        Restart = "gg_set_restart", Previous = "gg_q_prev", BoardOpen = HudView.BoardOpen;

    /// <summary>The Fun page's Random and Default (!fun random, !fun default).</summary>
    public const string FunRandom = "gg_fun_random", FunDefault = "gg_fun_default";

    /// <summary>The Fun page's Catch-up on and off (!catchup on|off). Arms Race only, like the boosts.</summary>
    public const string CatchUpOn = "gg_set_catchup_on", CatchUpOff = "gg_set_catchup_off";

    /// <summary>The new-maps setting's three buttons, which the Game page of the contents before v4_6_0 still has. The setting is gone - every ballot
    /// has a fresh map and a surprise instead - so the buttons only answer that (the bridge's !newmaps, Selection.cs).</summary>
    public const string NewAlways = "gg_set_new_always", NewHalf = "gg_set_new_half", NewNever = "gg_set_new_never";

    /// <summary>Card k's panel id (k from 1); its parts are this plus "_name", "_thumb", "_play" and so on.</summary>
    public static string Card(int k) => $"gg_m{k}";

    /// <summary>Card k's picture button, on a card whose map has no picture in the addon.</summary>
    public static string PicButton(int k) => $"{Card(k)}_pic";

    /// <summary>The card a picture button belongs to, or 0 when this is no picture button.</summary>
    public static int PicCard(string buttonId) => buttonId.StartsWith("gg_m", StringComparison.Ordinal) && buttonId.EndsWith("_pic", StringComparison.Ordinal)
        && int.TryParse(buttonId[4..^4], NumberStyles.None, CultureInfo.InvariantCulture, out var k) && k >= 1 && k <= Cards ? k : 0;

    /// <summary>What a card calls its place in the list: Arms Race's maps a number, the other modes' a number in their own list.</summary>
    static string RankText(HudCatalog.Entry m, string filter)
    {
        if (m.Official) return "Official";
        var mode = filter != "" && m.RankIn(filter) >= 0 ? filter : m.FirstMode;
        return mode switch
        {
            Modes.DeathmatchId => $"DM #{m.RankIn(mode)}",
            Modes.WingmanId => $"Wingman #{m.RankIn(mode)}",
            _ => $"#{m.RankIn(mode)}",
        };
    }

    /// <summary>What a card's Play button says (a pick is a vote), and whether this player's click on it is the second, confirming
    /// one. While the vote is open (MAPS, Browse) a Play puts the map on the vote, with this player's vote; after a vote, the wait before the next one.</summary>
    static string PlayText(HudCatalog.Entry m, HudState s, HudBrowser b, DateTime now, VoteState? vote = null, long account = 0)
    {
        if (vote is { Open: true, Round: > 0 } v)
        {
            if (v.Options.FirstOrDefault(o => o.Key == m.Key && o.Source != VoteSource.Random) is { } on)
                return v.ChoiceOf(account) == on.N ? "Your vote" : v.Shown(on) is > 0 and var n ? $"On the vote: {n}" : "On the vote";
            if (m.Removed) return "Unavailable";
            if (b.ConfirmKey == m.Key && now < b.ConfirmUntil) return "Click again to add";
            return m.Risky ? "Add anyway?" : "Add to the vote";
        }
        if (m.Key == s.Key && s.Key != "") return "On now";
        if (m.Removed) return "Unavailable";
        if (vote?.WaitFor > 0) return $"Vote in {MapVote.Left(vote.WaitFor)}";
        if (b.ConfirmKey == m.Key && now < b.ConfirmUntil) return "Click again"; // fits the narrower Play of the older contents' four small buttons
        return m.Risky ? "Vote anyway?" : "Put to the vote";
    }

    static string Likes(HudCatalog.Entry m) => m.Up + m.Down == 0 ? "no likes" : $"+{m.Up}{(m.Down > 0 ? $"  -{m.Down}" : "")}";

    /// <summary>The red strip across a card's picture - the map browser's and the ballot's: why the map is unavailable or broken
    /// (the reason, where the list gives one), or what it crashed. The one text of a map's record on screen.</summary>
    public static string Record(HudCatalog.Entry m) => m.Removed ? "Removed from the Workshop" : m.Broken > 0 ? $"Broken: crashed {m.Broken}/{m.Broken} test loads"
        : m.Broken == 0 ? (m.Reason != "" ? $"Broken: {m.Reason}" : "On the broken list") : m.Crashes > 0 ? $"Crashed the server {m.Crashes}x" : "";

    /// <summary>The maps page for one player: the toolbar's state, the search box and keyboard, the count, the page counter and the
    /// eight cards with every tag that applies (MapTags; ON NOW on the map that is on). A query that is a Workshop id no map has hides the cards'
    /// empty message: the plugin shows that item's card there. The cards past the eighth are the ballot's: empty here.</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) Maps(
        HudCatalog? catalog, HudState s, HudBrowser b, long account, DateTime now, HudSearch? search = null, string openPicture = "", string serverMode = "", VoteState? vote = null)
    {
        var t = new List<(string, string)>();
        var c = new List<(string, string, bool)>();
        var matches = catalog == null ? new List<HudCatalog.Entry>() : b.Matches(catalog, account, search);
        var page = b.PageOf(matches);
        var total = catalog?.Maps.Count ?? 0;
        var workshop = b.WorkshopQuery(catalog) != null;
        t.Add(("gg_m_count", catalog == null ? "Loading the map list..." : matches.Count == total ? $"{total} maps" : $"{matches.Count} of {total} maps"));
        t.Add(("gg_pg_text", $"Page {b.Page + 1} of {b.PagesFor(matches.Count)}"));
        t.Add(("gg_q_text", b.Keyboard ? b.Query + "|" : b.Query));
        t.Add(("gg_q_hint", b.Query == "" && !b.Keyboard ? "name, tag, description or Workshop ID" : ""));
        t.Add(("gg_m_empty", catalog == null || workshop ? "" : b.Query != "" ? $"No map matches \"{b.Query}\" - other words, or a Workshop ID."
            : b.Liked ? "You have not liked a map yet: the up arrow on a card likes it." : b.Ours ? "No map of ours on the list yet." : "No map matches these filters."));
        c.Add(("gg_m_empty", "used", catalog != null && matches.Count == 0 && !workshop));
        c.Add(("gg_q_search", "used", b.Query != ""));
        c.Add((HudView.Screen, "kbd", b.Keyboard));
        foreach (var (id, sort) in HudBrowser.SortButtons) c.Add((id, "on", b.Sort == sort));
        foreach (var (id, sort) in HudBrowser.SortButtons) c.Add((id, "desc", b.Sort == sort && b.Descending));
        foreach (var (id, band) in HudBrowser.BandButtons) c.Add((id, "on", b.Size == band));
        c.Add((HudBrowser.LikedButton, "on", b.Liked));
        c.Add((HudBrowser.OfficialButton, "on", b.Official));
        c.Add((HudBrowser.OursButton, "on", b.Ours));
        c.Add((HudView.Screen, "fl-ours", b.Ours));   // Our maps glows while it is off
        c.Add((HudBrowser.PrevButton, "off", b.Page == 0));
        c.Add((HudBrowser.FirstButton, "off", b.Page == 0));
        c.Add((HudBrowser.NextButton, "off", b.Page >= b.PagesFor(matches.Count) - 1));
        c.Add((HudBrowser.LastButton, "off", b.Page >= b.PagesFor(matches.Count) - 1));
        for (var k = 1; k <= Cards; k++)
        {
            var id = Card(k);
            var m = k <= page.Count ? page[k - 1] : null;
            var mine = m == null || catalog == null ? 0 : catalog.MyVote(account, m.Key);
            t.Add(($"{id}_rank", m == null ? "" : RankText(m, b.Mode)));
            t.Add(($"{id}_name", m?.Name ?? ""));
            t.Add(($"{id}_size", m == null || m.Official ? "" : m.SizeMb >= 1000 ? $"{m.SizeMb / 1000.0:0.0} GB" : $"{m.SizeMb} MB"));
            t.Add(($"{id}_votes", m == null ? "" : Likes(m)));
            t.Add(($"{id}_who", ""));   // the ballot's line under the name
            t.Add(($"{id}_score", "")); // the ballot's count
            t.Add(($"{id}_warn", m == null ? "" : Record(m)));
            t.Add(($"{id}_play_text", m == null ? "" : PlayText(m, s, b, now, vote, account)));
            c.Add((id, "used", m != null));
            c.Add((id, "official", m?.Official == true));
            c.Add((id, "risky", m?.Risky == true));
            c.Add((id, "broken", m != null && (m.Broken >= 0 || m.Removed))); // the red strip: a removed map is the stronger warning
            c.Add((id, "crashed", m != null && m.Crashes > 0));
            c.Add((id, "now", m != null && m.Key == s.Key && s.Key != ""));
            c.Add((id, "queued", m != null && vote is { Open: true, Round: > 0 } && vote.Options.Any(o => o.Key == m.Key && o.Source is VoteSource.Picked or VoteSource.Rolled))); // on the open ballot
            c.Add((id, "liked", mine > 0));
            c.Add((id, "disliked", mine < 0));
            c.Add((id, "confirm", m != null && b.ConfirmKey == m.Key && now < b.ConfirmUntil));
            c.Add((id, "big", m?.TooBig == true));                         // the TOO BIG tag...
            c.Add((id, "bigvoted", mine == HudCatalog.TooBigVote));        // ...and this player's too-big mark lit
            c.Add((id, "ads", m?.Ads > 0));                                 // the ADS tag...
            c.Add((id, "adsvoted", mine == HudCatalog.AdsVote));            // ...and this player's ads mark lit
            c.Add((id, "ours", m?.Ours == true));                           // the OUR MAP tag
            c.Add((id, "nopic", m != null && !HudThumbs.Has(HudThumbs.Key(m)) && m.Id > 0)); // no picture in the addon, so its button shows
            c.Add((id, "picopen", m != null && m.Key == openPicture));      // ...and says Hide picture while that picture is up
            c.AddRange(HudVote.CardModeClasses(id, m == null ? null : PlayMode(m, b.Mode, serverMode))); // the mode it plays in, on its picture and in its colour
            c.AddRange(HudVote.TagClassesOf(id, m?.Tags));                  // the rest of its tags
            c.AddRange(HudVote.ShareClasses(id, 0, 0));                     // no vote's share here
            c.Add((id, "vhas", false));
            c.Add((id, "vlead", false));
            c.Add((id, "vtie", false));
        }
        foreach (var (_, mode) in HudBrowser.ModeButtons) c.Add((HudView.Screen, HudBrowser.ModeClass(mode), b.Mode == mode)); // the mode filter's chips, lit
        return (t, c);
    }

    /// <summary>The picture each card should show for this player: (thumbnail panel, its classes), none where the card is empty or its
    /// map has no picture. The plugin switches off whatever the card's previous picture had and this one does not (it knows what it sent).</summary>
    public static List<(string Panel, string[] Classes)> Thumbs(HudCatalog? catalog, HudBrowser b, long account, HudSearch? search = null)
    {
        var page = catalog == null ? new List<HudCatalog.Entry>() : b.PageOf(b.Matches(catalog, account, search));
        return Enumerable.Range(1, Cards).Select(k => ($"{Card(k)}_thumb", k <= page.Count ? HudThumbs.Classes(page[k - 1]) : [])).ToList();
    }

    /// <summary>The Previous button (it puts the map before this one to the vote) - on the Maps page; the contents before v4_6_0 have it, under the
    /// same ids, on their Queue page.</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) PreviousButton(HudState s) =>
        ([("gg_q_prev_text", s.PrevArg == "" ? "No previous map yet" : $"Previous: {Hud.Clean(s.PrevLabel, 26)}")], [(Previous, "off", s.PrevArg == "")]);

    // ---- the Fun page: a row per setting of Fun.cs, a button per choice; the state is the server's, the same for all ----

    public const string FunNote = "gg_fun_note";

    /// <summary>The line at the top of the Fun page (every row alike, the grenade drops too).</summary>
    public const string FunNoteText = "Anybody switches these, for everybody. Everything lasts until the map changes (or 10 minutes with nobody on); the centre of the screen says who.";

    public static string FunRow(int r) => $"gg_f{r}";

    public static string FunButton(int r, int c) => $"gg_f{r}_{c}";

    /// <summary>A setting off its default, with who set it: "Moon - set by Alice", the words of its row; "" while it is at its default or nobody is
    /// known to have set it.</summary>
    public static string SetByWords(Fun.Setting s, Fun.Choice showing, string? by) => showing == s.Normal || string.IsNullOrWhiteSpace(by) ? "" : $"{showing.Label} - set by {Hud.Clean(by, 32)}";

    /// <summary>The Fun page as the live values have it: each row's title and words (with a word when the map runs its own value, and who set a
    /// setting that is off its default), each choice's label, the choice showing lit; rows and choices beyond the settings collapse. A row that does
    /// nothing at the moment (Instakill and Grenades while friendly fire is off: Fun.Idle) says so instead of its words and is dimmed (idle); its
    /// buttons still work. setBy: who set a setting, by its id (the settings module's word); null: nobody known.</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) FunPage(Func<string, string?> value, bool armsRace = true,
        Func<string, string?>? setBy = null)
    {
        var t = new List<(string, string)>();
        var c = new List<(string, string, bool)>();
        for (var r = 1; r <= Fun.Rows; r++)
        {
            var s = r <= Fun.Settings.Length ? Fun.Settings[r - 1] : null; // the grid's sixteen; the grenade drops and catch-up have fixed rows
            var showing = s == null ? null : Fun.Showing(s, value);
            var because = s == null ? null : Fun.IdleBecause(s, value);
            var idle = because != null;
            t.Add(($"{FunRow(r)}_title", s?.Title ?? ""));
            var who = s == null || showing == null ? "" : SetByWords(s, showing, setBy?.Invoke(s.Id));
            t.Add(($"{FunRow(r)}_txt", s == null ? "" : because != null ? because.Idle
                : showing == s.Normal && Fun.OwnValues(s, value).Count > 0 ? $"{s.Words}  ·  this map runs its own" : who != "" ? $"{who}  ·  {s.Words}" : s.Words));
            c.Add((FunRow(r), "used", s != null && (armsRace || !Fun.ArmsRaceOnly(s)))); // friendly fire's three are Arms Race's: Deathmatch is free-for-all, Wingman runs Valve's
            c.Add((FunRow(r), "idle", idle));
            c.Add((FunRow(r), "changed", s != null && showing != s.Normal)); // a setting off its default stands out
            for (var k = 1; k <= Fun.ChoicesPerRow; k++)
            {
                var choice = s != null && k <= s.Choices.Length ? s.Choices[k - 1] : null;
                t.Add(($"{FunButton(r, k)}_txt", choice?.Label ?? ""));
                c.Add((FunButton(r, k), "used", choice != null));
                c.Add((FunButton(r, k), "on", choice != null && choice == showing));
            }
        }
        return (t, c);
    }

    /// <summary>The words of the Fun page's two fixed rows - the grenade drops and catch-up, ordinary fun settings with rows of their own - as
    /// variables on gg_global that their labels inherit (osfdrops, osfcatch), with who set one that is off its default.</summary>
    public const string DropsWordsVar = "osfdrops", CatchUpWordsVar = "osfcatch";

    public static List<(string Name, string Text)> FixedRowWords(Func<string, string?> value, Func<string, string?>? setBy = null)
    {
        string Words(string id, string words)
        {
            if (Fun.Find(id) is not { } s) return words;
            var who = SetByWords(s, Fun.Showing(s, value), setBy?.Invoke(s.Id));
            return who == "" ? words : $"{who}  ·  {words}";
        }
        return [(DropsWordsVar, Words("drops", "A grenade for every player at each spawn")),
            (CatchUpWordsVar, Words("catchup", "Boosts for players behind the leader, a bounty on a runaway one"))];
    }

    /// <summary>A click on the Fun page as the setting and the choice it asks for, or null when it is no Fun button or names nothing - the grid's
    /// rows, and the grenade drops' and catch-up's fixed rows (ordinary fun settings).</summary>
    public static (Fun.Setting Setting, Fun.Choice Choice)? FunClick(string buttonId)
    {
        var fixedRow = buttonId switch { NadesOn => ("drops", "on"), NadesOff => ("drops", "off"), CatchUpOn => ("catchup", "on"), CatchUpOff => ("catchup", "off"), _ => ("", "") };
        if (fixedRow.Item1 != "" && Fun.Find(fixedRow.Item1) is { } fs && Fun.Find(fs, fixedRow.Item2) is { } fc) return (fs, fc);
        var parts = buttonId.StartsWith("gg_f", StringComparison.Ordinal) ? buttonId[4..].Split('_') : [];
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var r)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var k)) return null; // gg_fl_* are the size chips, gg_fun_note a label
        if (r < 1 || r > Math.Min(Fun.Rows, Fun.Settings.Length)) return null;
        var s = Fun.Settings[r - 1];
        return k >= 1 && k <= s.Choices.Length ? (s, s.Choices[k - 1]) : null;
    }

    /// <summary>What the restart button says after its first click: press it again, and the seconds left of
    /// the bridge's 30 s window, counting down every second; the stylesheet also makes it blink fast while armed (.gg-restart.armed).</summary>
    public static string RestartAgain(int seconds) => $"Press again within {seconds} s";

    /// <summary>The game page: the match length that is set (and who set it, while it is not the default), and the restart button's two-click
    /// state - restartSeconds is what is left of the confirmation window after the first click (0: not armed).</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) Game(HudState s, int restartSeconds)
    {
        var armed = restartSeconds > 0;
        var t = new List<(string, string)>
        {
            ("gg_set_time_now", s.RoundMinutes <= 0 ? "Match length" : $"Matches last {s.RoundMinutes} minutes{(s.RoundBy != "" ? $" - set by {Hud.Clean(s.RoundBy, 32)}" : "")}"),
            ("gg_set_restart_text", armed ? RestartAgain(restartSeconds) : "Restart the server"),
            ("gg_set_restart_sub", armed ? "Restarts the whole server: everybody is kicked for about a minute"
                : "For a stuck server: the whole server, not the match. Asks to confirm."),
        };
        var c = new List<(string, string, bool)>
        {
            (Time10, "on", s.RoundMinutes == 10), (Time60, "on", s.RoundMinutes == 60), (Restart, "armed", armed),
        };
        return (t, c);
    }

    /// <summary>What a card's Play names: the map, and the mode it plays in when that is not the first mode that has it - the one the filter
    /// shows it in, else the one the server is in when that mode has it too.</summary>
    public static string PlayArg(HudCatalog.Entry m, string filter, string serverMode)
    {
        var mode = PlayMode(m, filter, serverMode);
        return mode == m.FirstMode ? m.Arg : $"{m.Arg} {mode}";
    }

    /// <summary>The mode a card's Play plays its map in: the filter's, else the server's when that mode has it too, else the first mode that has it.
    /// Its label and colour on the card say this mode.</summary>
    public static string PlayMode(HudCatalog.Entry m, string filter, string serverMode) =>
        filter != "" && m.RankIn(filter) >= 0 ? filter : serverMode != "" && m.RankIn(serverMode) >= 0 ? serverMode : m.FirstMode;

    /// <summary>A click on a menu button as the action it asks the bridge for (null: nothing to send). Card buttons name the map shown on
    /// that card for this player; a risky map's Play arms a confirmation first, exactly like the bridge's own two-step pick.</summary>
    public static string? Command(string buttonId, HudCatalog? catalog, HudState? s, HudBrowser b, long account, DateTime now, HudSearch? search = null, string serverMode = "", VoteState? vote = null)
    {
        if (buttonId.StartsWith("gg_m") && buttonId.Length > 5 && char.IsDigit(buttonId[4]) && catalog != null && s != null)
        {
            var under = buttonId.IndexOf('_', 4);
            if (under < 0 || !int.TryParse(buttonId[4..under], NumberStyles.None, CultureInfo.InvariantCulture, out var k) || k < 1 || k > Cards) return null;
            var page = b.PageOf(b.Matches(catalog, account, search));
            if (k > page.Count) return null;
            var m = page[k - 1];
            switch (buttonId[(under + 1)..])
            {
                case "up": return $"!up {m.Arg}";
                case "down": return $"!down {m.Arg}";
                case "big": return $"!down {m.Arg} big"; // an older content's Too big: a Dislike's reason (one command for it)
                case "ads": return $"!down {m.Arg} ads"; // ...and its Ads
                case "play":
                    if (m.Key == s.Key && s.Key != "" && vote is not { Open: true, Round: > 0 }) return null; // the map that is on: Keep on the vote, nothing outside one
                    if (m.Removed) return $"!map {PlayArg(m, b.Mode, serverMode)}"; // the bridge refuses it, saying why in the player's note - no confirmation to arm
                    if (m.Risky && !(b.ConfirmKey == m.Key && now < b.ConfirmUntil)) { b.ConfirmKey = m.Key; b.ConfirmUntil = now.AddSeconds(HudBrowser.ConfirmSeconds); }
                    else { b.ConfirmKey = ""; b.ConfirmUntil = default; }
                    return $"!map {PlayArg(m, b.Mode, serverMode)}"; // the first click on a risky map arms the bridge's confirmation too, and says why in the note
            }
            return null;
        }
        return buttonId switch
        {
            Previous => s?.PrevArg is { Length: > 0 } ? "!previous" : null,
            Time10 => "!time 10",
            Time60 => "!time 60",
            Restart => "!restart",
            NewAlways => "!newmaps always",
            NewHalf => "!newmaps half",
            NewNever => "!newmaps never",
            _ => null,
        };
    }
}

/// <summary>How a card shows its map's picture. A custom HUD cannot take a picture's address from the server and the game refuses it web
/// pictures outright (its window is the one that may not load http or https, 47 forms tested on 2026-11-01), so every thumbnail is in the
/// Workshop addon - each map's Workshop preview as a small JPEG, which the resource compiler stores as it is, or for an official map the
/// game's own screenshot - and the card picks one with classes. Maps that are not listed yet ship theirs too (hud/thumbs.py --ahead), so
/// a map the nightly sync lists later has its picture without a new HUD; a map uploaded after that release has HudPreview instead.
///
/// Two classes per picture, not one per map: the game interns every class name the server ever sets on the layout entity, for the
/// entity's whole life and for all players together, and takes no more than 1,024 (the server logs "The maximum number of class names
/// has been reached" and what the client then does is unknown). Picture n is the pair th{n / 64} and tl{n % 64}, so 9,000 pictures
/// cost about 200 names. n is the map's place in HudThumbList (hud/thumbs.py appends, nothing moves); gungame_hud_thumbs.css has the rule
/// ".th{n / 64}.tl{n % 64}" for each, and a map without a number shows the card's plain backdrop and the button for HudPreview.</summary>
public static class HudThumbs
{
    public const int Base = 64;

    /// <summary>th0 ... th255: 16,384 pictures (hud/thumbs.py's MAX_HIGH), 320 class names at the very most. Tested in a real client on 2026-11-01:
    /// the stylesheet of all 16,384 loads and flips pages without a hitch.</summary>
    public const int MaxHigh = 256;

    static readonly Dictionary<string, int> numbers = Number();

    static Dictionary<string, int> Number()
    {
        var d = new Dictionary<string, int>(HudThumbList.Keys.Length, StringComparer.Ordinal);
        for (var i = 0; i < HudThumbList.Keys.Length; i++) d.TryAdd(HudThumbList.Keys[i], i);
        return d;
    }

    /// <summary>How many pictures are numbered (touching it builds the lookup, which the plugin does at load).</summary>
    public static int Count => numbers.Count;

    /// <summary>What a map is numbered by: an official map's name, otherwise its Workshop id.</summary>
    public static string Key(HudCatalog.Entry m) => m.Official ? m.Name : m.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>Picture n's two classes.</summary>
    public static string[] Classes(int n) => [$"th{n / Base}", $"tl{n % Base}"];

    /// <summary>A map's picture classes, or none when it has no picture.</summary>
    public static string[] Classes(HudCatalog.Entry m) => numbers.TryGetValue(Key(m), out var n) ? Classes(n) : [];

    /// <summary>Whether the addon has a picture for this map key.</summary>
    public static bool Has(string key) => numbers.ContainsKey(key);

    public static bool IsThumbClass(string cls) => cls.Length > 2 && cls[0] == 't' && cls[1] is 'h' or 'l' && cls[2..].All(char.IsAsciiDigit);
}

/// <summary>The picture on request: a map with no picture in the addon has a button on its card, and its Workshop preview shows in
/// the game's own message panel (PrintToCenterHtml), which unlike a custom HUD may load web pictures; the window goes short meanwhile, because it
/// is drawn above that panel. The picture comes from Steam's own image CDN, scaled by Steam: nothing of ours is asked, and the panel shows
/// the 336x189 whole. Measured in a real client on production, 2026-11-02: the panel is 300 px wide at x 810-1110, y 750-908 at 1920x1080 and
/// the picture 252 px wide inside it; the player's Steam client fetches it. The panel pulses a little once a second, by itself, at any duration.</summary>
public static class HudPreview
{
    public const string SteamHost = "images.steamusercontent.com";
    public const string Scaled = "?imw=336&imh=189&ima=fit&impolicy=Letterbox&imcolor=%23000000&letterbox=false";

    /// <summary>How long a picture stays up without a click. The window holds its idle clock meanwhile.</summary>
    public const int Seconds = 30;

    /// <summary>The address a Workshop item's preview_url makes, scaled: only Steam's image CDN, only an address that cannot break out of the
    /// message's HTML. Null for anything else: the card then says there is no picture.</summary>
    public static string? Url(string? previewUrl)
    {
        if (string.IsNullOrEmpty(previewUrl) || !Uri.TryCreate(previewUrl, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host != SteamHost) return null;
        var path = u.AbsolutePath;
        return path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.') ? $"https://{SteamHost}{path}{Scaled}" : null;
    }

    /// <summary>The message: the picture and nothing after it, since a picture that fails to load takes the rest with it.</summary>
    public static string Html(string url) => $"<img src='{url}'>";

    /// <summary>The window's rows of cards a card is on, as the class on gg_screen that keeps them and collapses the others: pic1 for cards 1-4,
    /// pic2 for 5-8, pic3 for the ballot's 9 and 10.</summary>
    public static string RowClass(int card) => card <= HudBrowser.PerPageTyping ? "pic1" : card <= HudBrowser.PerPage ? "pic2" : "pic3";
}
