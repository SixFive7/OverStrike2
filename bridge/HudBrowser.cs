// The in-game menu's map browser, queue and game pages: the logic both programs share, like Hud.cs. The bridge serves the map catalog
// (HudCatalog, on /catalog/<secret>) and tests everything here in --selftest; the GunGameWelcome plugin keeps one HudBrowser per player
// slot, works out that player's page from the catalog, and puts it on screen through HudMenu's texts and classes. Plain BCL only.
//
// Every click is a server round trip, so all of this is cheap on purpose: sorting and filtering ~1,000 entries is well under a
// millisecond, a page is 8 cards, and the plugin sends only what changed.
using System.Globalization;
using System.Text;

/// <summary>Every map the menu can show - the ranked list of maps.json and the official maps - with what the server knows about each:
/// its size, the broken-list verdict, the live crash counter and the votes. A text codec like HudState's. The bridge bumps Version
/// whenever any of it changes (a vote, a crash); the live state carries the version, so the plugin fetches this again only then.</summary>
public sealed class HudCatalog
{
    public const string Magic = "gg-catalog\t1";

    /// <summary>Key: what the bridge counts votes and crashes by (an official map's name, otherwise the Workshop id). Arg: what a chat
    /// command takes to name it unambiguously (the rank of a ranked map, an official map's name, the id of an unlisted one).
    /// Broken: crashes on the swept broken list (0 = listed without a count), -1 when not on it. Removed: gone from the Workshop
    /// (Workshop.cs) - nobody can download it, so it is refused rather than confirmed.</summary>
    public sealed record Entry(string Key, string Arg, int Rank, long Id, string Name, int SizeMb, bool Official, int Broken, int Crashes, int Up, int Down,
        bool Removed = false, bool TooBig = false, string SizeClass = "", int M2 = 0, int Big = 0, int Ads = 0, bool Ours = false, string Reason = "")
    {
        public bool Risky => !Removed && (Broken >= 0 || Crashes > 0);
    }

    /// <summary>A too-big vote, as votes.json, the catalog's "v" lines and the live state's "vote" lines carry it: a downvote (an older
    /// plugin reads it as -1, a dislike) that also marks the map too big. TooBig: measured XL, or marked too big by at least one player;
    /// SizeClass and M2 the measurement (sizes.json); Big the too-big votes on it.</summary>
    public const int TooBigVote = -2;

    /// <summary>An ads vote (the owner's 15b, 2026-10-24), carried like a too-big vote: a downvote - an older plugin, taking the sign, reads
    /// it as a dislike - that also marks the map as one that shows ads or sends players to another server. A map with one is never a
    /// random pick (players can still pick it). Ads on an entry: the ads votes on it.</summary>
    public const int AdsVote = -3;

    /// <summary>The other reasons a downvote can give (DownReasons, the owner's decision of 2026-11-01), carried the same way: each a dislike and
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
                I(m.Broken), I(m.Crashes), I(m.Up), I(m.Down), m.Removed ? "1" : "0", m.TooBig ? "1" : "0", Hud.Clean(m.SizeClass, 2), I(m.M2), I(m.Big), I(m.Ads), m.Ours ? "1" : "0", Hud.Clean(m.Reason, 64))).Append('\n'); // an older plugin reads the first twelve (3.1.0: seventeen, 3.2: eighteen)
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
                        f.Length > 18 && f[18] == "1", f.Length > 19 ? f[19] : ""));
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

/// <summary>One player's view of the map browser: sort, filters, search and page, plus a risky map's first click waiting for the second.
/// Lives in the plugin, one per player slot; a new player in the slot starts from a fresh one.</summary>
public sealed class HudBrowser
{
    public enum SortBy { Rank, Votes, Name, Size }
    public enum Band { All, Tiny, Small, Mid, Large }

    public const int PerPage = 8;            // the layout has eight cards
    public const int PerPageTyping = 4;      // ...and the keyboard covers the second row of them while it is open
    public const double ConfirmSeconds = 30; // the bridge's own window for confirming a broken or crashed map

    /// <summary>The site's size chips: under 25 MB, 25-120, 120-400, 400 MB and up.</summary>
    public static Band BandOf(int sizeMb) => sizeMb < 25 ? Band.Tiny : sizeMb < 120 ? Band.Small : sizeMb < 400 ? Band.Mid : Band.Large;

    public SortBy Sort = SortBy.Rank;
    public bool Descending;                  // the natural direction flipped: a second click on the sort that is on
    public Band Size = Band.All;
    public bool Liked, Official, Ours;        // Ours: the server's own maps (ours.json; Over Strike 2's Our maps)
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
    public const string LikedButton = "gg_fl_liked", OfficialButton = "gg_fl_official", OursButton = "gg_fl_ours", ClearButton = "gg_q_clear",
        FirstButton = "gg_pg_first", PrevButton = "gg_pg_prev", NextButton = "gg_pg_next", LastButton = "gg_pg_last";

    /// <summary>The maps this player's filters and search let through, in this player's order. Officials have no rank: they sort after
    /// the ranked maps by rank (first when only officials are shown, which is the same thing). The search is full text (HudSearch):
    /// every typed word in the name, the Workshop tags or the description; a number is also a rank or a Workshop id. While searching
    /// with the default sort, the best matches come first.</summary>
    public List<HudCatalog.Entry> Matches(HudCatalog catalog, long account, HudSearch? search = null)
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
        var score = new Dictionary<string, int>();
        if (q != "")
        {
            int Score(HudCatalog.Entry m) => m.Arg.Equals(q, StringComparison.OrdinalIgnoreCase) || m.Id.ToString(CultureInfo.InvariantCulture) == q ? 10_000
                : index.Score(m.Key, m.Name, words);
            foreach (var m in hits) if (Score(m) is > 0 and var n) score[m.Key] = n;
            hits = hits.Where(m => score.ContainsKey(m.Key));
        }
        static int RankKey(HudCatalog.Entry m) => m.Rank > 0 ? m.Rank : 100_000;
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
        Size = Band.All; Liked = false; Official = false; Ours = false;
        Page = 0;
    }
}

/// <summary>The menu's pages as texts and classes for one player, out of the catalog, the live state and that player's browser, and a
/// click on them as the chat command it stands for. Card k (1-8) is panel gg_m{k}; its parts are gg_m{k}_*.</summary>
public static class HudMenu
{
    public const int Cards = HudBrowser.PerPage, QueueRows = HudState.QueueLength;

    public const string NadesOn = "gg_set_nades_on", NadesOff = "gg_set_nades_off", Time10 = "gg_set_time_10", Time60 = "gg_set_time_60",
        Restart = "gg_set_restart", CancelAll = "gg_q_cancelall", Previous = "gg_q_prev", BoardOpen = HudView.BoardOpen;

    /// <summary>Content v3_1_0: the new-maps setting's three buttons and its line (the bridge's !newmaps, Selection.cs).</summary>
    public const string NewAlways = "gg_set_new_always", NewHalf = "gg_set_new_half", NewNever = "gg_set_new_never", NewMapsNow = "gg_set_new_now";

    /// <summary>What the setting in force means, under its three buttons. The numbers are the bridge's (--selftest holds them to it).</summary>
    public static string NewMapsLine(string mode) => mode switch
    {
        "always" => "Always: every random pick is a new map, #1017 and up. Back to Always after 10 minutes with nobody on.",
        "half" => "Half: a coin flip per random pick between the new maps and the original #1-#1016. Back to Always after 10 minutes with nobody on.",
        "never" => "Never: random picks take only the original #1-#1016. Back to Always after 10 minutes with nobody on.",
        _ => "",
    };

    /// <summary>Card k's panel id (k from 1); its parts are this plus "_name", "_thumb", "_play" and so on.</summary>
    public static string Card(int k) => $"gg_m{k}";

    /// <summary>What a card's Play button says, and whether this player's click on it is the second, confirming one.</summary>
    static string PlayText(HudCatalog.Entry m, HudState s, HudBrowser b, DateTime now)
    {
        if (m.Key == s.Key && s.Key != "") return "On now";
        if (m.Removed) return "Unavailable";
        if (s.Queue.Any(q => q.Key == m.Key)) return "Queued - join it";
        if (b.ConfirmKey == m.Key && now < b.ConfirmUntil) return "Click again to play"; // fits the narrower Play of content v3_2_0's four small buttons
        return m.Risky ? "Play anyway?" : "Play it";
    }

    static string Votes(HudCatalog.Entry m) => m.Up + m.Down == 0 ? "no votes" : $"+{m.Up}{(m.Down > 0 ? $"  -{m.Down}" : "")}";

    /// <summary>The red strip across a card's picture: why the map is unavailable or broken (#8, #11: the reason, where the list gives one).</summary>
    static string Warning(HudCatalog.Entry m) => m.Removed ? "Removed from the Workshop" : m.Broken > 0 ? $"Broken: crashed {m.Broken}/{m.Broken} test loads"
        : m.Broken == 0 ? (m.Reason != "" ? $"Broken: {m.Reason}" : "On the broken list") : m.Crashes > 0 ? $"Crashed the server {m.Crashes}x" : "";

    /// <summary>The maps page for one player: the toolbar's state, the search box and keyboard, the count, the page counter and the
    /// eight cards. A query that is a Workshop id no map has hides the cards' empty message: the plugin shows that item's card there.</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) Maps(
        HudCatalog? catalog, HudState s, HudBrowser b, long account, DateTime now, HudSearch? search = null)
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
        c.Add((HudBrowser.OursButton, "on", b.Ours)); // Over Strike 2's content (NewIds32)
        c.Add((HudBrowser.PrevButton, "off", b.Page == 0));
        c.Add((HudBrowser.FirstButton, "off", b.Page == 0));
        c.Add((HudBrowser.NextButton, "off", b.Page >= b.PagesFor(matches.Count) - 1));
        c.Add((HudBrowser.LastButton, "off", b.Page >= b.PagesFor(matches.Count) - 1));
        for (var k = 1; k <= Cards; k++)
        {
            var id = Card(k);
            var m = k <= page.Count ? page[k - 1] : null;
            var mine = m == null || catalog == null ? 0 : catalog.MyVote(account, m.Key);
            t.Add(($"{id}_rank", m == null ? "" : m.Official ? "Official" : $"#{m.Rank}"));
            t.Add(($"{id}_name", m?.Name ?? ""));
            t.Add(($"{id}_size", m == null || m.Official ? "" : m.SizeMb >= 1000 ? $"{m.SizeMb / 1000.0:0.0} GB" : $"{m.SizeMb} MB"));
            t.Add(($"{id}_votes", m == null ? "" : Votes(m)));
            t.Add(($"{id}_warn", m == null ? "" : Warning(m)));
            t.Add(($"{id}_play_text", m == null ? "" : PlayText(m, s, b, now)));
            c.Add((id, "used", m != null));
            c.Add((id, "official", m?.Official == true));
            c.Add((id, "risky", m?.Risky == true));
            c.Add((id, "broken", m != null && (m.Broken >= 0 || m.Removed))); // the red strip: a removed map is the stronger warning
            c.Add((id, "crashed", m != null && m.Crashes > 0));
            c.Add((id, "now", m != null && m.Key == s.Key && s.Key != ""));
            c.Add((id, "queued", m != null && s.Queue.Any(q => q.Key == m.Key)));
            c.Add((id, "liked", mine > 0));
            c.Add((id, "disliked", mine < 0));
            c.Add((id, "confirm", m != null && b.ConfirmKey == m.Key && now < b.ConfirmUntil));
            c.Add((id, "big", m?.TooBig == true));                         // content v3_1_0: the Too big tag on the picture...
            c.Add((id, "bigvoted", mine == HudCatalog.TooBigVote));        // ...and this player's too-big vote lit (classes on the card, which v3_0_0 has)
            c.Add((id, "ads", m?.Ads > 0));                                 // content v3_2_0: the Ads tag on the picture...
            c.Add((id, "adsvoted", mine == HudCatalog.AdsVote));            // ...and this player's ads vote lit
            c.Add((id, "ours", m?.Ours == true));                           // Over Strike 2's content: the Ours tag on the picture
        }
        return (t, c);
    }

    /// <summary>The picture each card should show for this player: (thumbnail panel, its classes), none where the card is empty or its
    /// map has no picture. The plugin switches off whatever the card's previous picture had and this one does not (it knows what it sent).</summary>
    public static List<(string Panel, string[] Classes)> Thumbs(HudCatalog? catalog, HudBrowser b, long account, HudSearch? search = null)
    {
        var page = catalog == null ? new List<HudCatalog.Entry>() : b.PageOf(b.Matches(catalog, account, search));
        return Enumerable.Range(1, Cards).Select(k => ($"{Card(k)}_thumb", k <= page.Count ? HudThumbs.Classes(page[k - 1]) : [])).ToList();
    }

    /// <summary>The queue page: every request in play order, who asked, and a cancel button on this player's own.</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) Queue(HudState s, long account)
    {
        var t = new List<(string, string)>();
        var c = new List<(string, string, bool)>();
        for (var i = 0; i < QueueRows; i++)
        {
            var q = i < s.Queue.Count ? s.Queue[i] : null;
            t.Add(($"gg_qr{i + 1}_pos", q == null ? "" : (i + 1).ToString(CultureInfo.InvariantCulture)));
            t.Add(($"gg_qr{i + 1}_map", q?.Label ?? ""));
            t.Add(($"gg_qr{i + 1}_who", q?.Who ?? ""));
            c.Add(($"gg_qr{i + 1}", "used", q != null));
            c.Add(($"gg_qr{i + 1}", "mine", q != null && q.Accounts.Contains(account)));
        }
        var mineCount = s.Queue.Count(q => q.Accounts.Contains(account));
        t.Add(("gg_q_empty", "Nothing queued. Pick a map on the Maps page - it plays at once when nobody holds this one."));
        t.Add(("gg_q_mine", mineCount == 0 ? "You have nothing in the queue." : $"You have {mineCount} request{(mineCount == 1 ? "" : "s")} in the queue (up to 5)."));
        t.Add(("gg_q_prev_text", s.PrevArg == "" ? "No previous map yet" : $"Previous: {Hud.Clean(s.PrevLabel, 26)}"));
        c.Add(("gg_q_empty", "used", s.Queue.Count == 0));
        c.Add((CancelAll, "off", mineCount == 0));
        c.Add((Previous, "off", s.PrevArg == ""));
        return (t, c);
    }

    // ---- the Fun page (content v3_2_0): a row per setting of Fun.cs, a button per choice; the state is the server's, the same for all ----

    public const string FunNote = "gg_fun_note";

    /// <summary>The line at the top of the Fun page.</summary>
    public const string FunNoteText = "Anybody switches these, for everybody. A change lasts until the map changes (or 10 minutes with nobody on), and the news line says who.";

    public static string FunRow(int r) => $"gg_f{r}";

    public static string FunButton(int r, int c) => $"gg_f{r}_{c}";

    /// <summary>The Fun page as the live values have it: each row's title and words (with a word when the map runs its own value), each choice's
    /// label, the choice showing lit; rows and choices beyond the settings collapse. A row that does nothing at the moment (Instakill and
    /// Grenades while friendly fire is off: Fun.Idle) says so instead of its words and is dimmed (idle); its buttons still work.</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) FunPage(Func<string, string?> value, bool armsRace = true)
    {
        var t = new List<(string, string)>();
        var c = new List<(string, string, bool)>();
        for (var r = 1; r <= Fun.Rows; r++)
        {
            var s = r <= Fun.Settings.Length ? Fun.Settings[r - 1] : null;
            var showing = s == null ? null : Fun.Showing(s, value);
            var because = s == null ? null : Fun.IdleBecause(s, value);
            var idle = because != null;
            t.Add(($"{FunRow(r)}_title", s?.Title ?? ""));
            t.Add(($"{FunRow(r)}_txt", s == null ? "" : because != null ? because.Idle
                : showing == s.Normal && Fun.OwnValues(s, value).Count > 0 ? $"{s.Words}  ·  this map runs its own" : s.Words));
            c.Add((FunRow(r), "used", s != null && (armsRace || !Fun.ArmsRaceOnly(s)))); // friendly fire's three are Arms Race's (#27 c): Deathmatch is free-for-all, Wingman runs Valve's
            c.Add((FunRow(r), "idle", idle));
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

    /// <summary>A click on the Fun page as the setting and the choice it asks for, or null when it is no Fun button or names nothing.</summary>
    public static (Fun.Setting Setting, Fun.Choice Choice)? FunClick(string buttonId)
    {
        var parts = buttonId.StartsWith("gg_f", StringComparison.Ordinal) ? buttonId[4..].Split('_') : [];
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var r)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var k)) return null; // gg_fl_* are the size chips, gg_fun_note a label
        if (r < 1 || r > Fun.Settings.Length) return null;
        var s = Fun.Settings[r - 1];
        return k >= 1 && k <= s.Choices.Length ? (s, s.Choices[k - 1]) : null;
    }

    /// <summary>What the restart button says after its first click (the owner's 14a, 2026-10-24): press it again, and the seconds left of
    /// the bridge's 30 s window, counting down every second; content v3_2_0 also makes it blink fast while armed (.gg-restart.armed).</summary>
    public static string RestartAgain(int seconds) => $"Press again within {seconds} s";

    /// <summary>The game page: the match length that is set, and the restart button's two-click state - restartSeconds is what is left of
    /// the confirmation window after the first click (0: not armed).</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) Game(HudState s, int restartSeconds)
    {
        var armed = restartSeconds > 0;
        var t = new List<(string, string)>
        {
            ("gg_set_time_now", s.RoundMinutes > 0 ? $"Matches last {s.RoundMinutes} minutes" : "Match length"),
            ("gg_set_restart_text", armed ? RestartAgain(restartSeconds) : "Restart the server"),
            ("gg_set_restart_sub", armed ? "Restarts the whole server: everybody is kicked for about a minute"
                : "For a stuck server: the whole server, not the match. Asks to confirm."),
            (NewMapsNow, NewMapsLine(s.NewMaps)), // content v3_1_0 (HudView.NewIds)
        };
        var c = new List<(string, string, bool)>
        {
            (Time10, "on", s.RoundMinutes == 10), (Time60, "on", s.RoundMinutes == 60), (Restart, "armed", armed),
        };
        return (t, c);
    }

    /// <summary>A click on a menu button as the action it asks the bridge for (null: nothing to send). Card buttons name the map shown on
    /// that card for this player; a risky map's Play arms a confirmation first, exactly like the bridge's own two-step pick.</summary>
    public static string? Command(string buttonId, HudCatalog? catalog, HudState? s, HudBrowser b, long account, DateTime now, HudSearch? search = null)
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
                case "big": return $"!toobig {m.Arg}"; // content v3_1_0
                case "ads": return $"!ads {m.Arg}";    // content v3_2_0
                case "play":
                    if (m.Key == s.Key && s.Key != "") return null;
                    if (m.Removed) return $"!map {m.Arg}"; // the bridge refuses it, saying why in the player's note - no confirmation to arm
                    if (m.Risky && !(b.ConfirmKey == m.Key && now < b.ConfirmUntil)) { b.ConfirmKey = m.Key; b.ConfirmUntil = now.AddSeconds(HudBrowser.ConfirmSeconds); }
                    else { b.ConfirmKey = ""; b.ConfirmUntil = default; }
                    return $"!map {m.Arg}"; // the first click on a risky map arms the bridge's confirmation too, and says why in the note
            }
            return null;
        }
        if (buttonId.StartsWith("gg_qr") && buttonId.EndsWith("_cancel") && s != null
            && int.TryParse(buttonId[5..^7], NumberStyles.None, CultureInfo.InvariantCulture, out var row) && row >= 1 && row <= s.Queue.Count)
        {
            var q = s.Queue[row - 1];
            return q.Accounts.Contains(account) ? $"!cancel {q.Arg}" : null;
        }
        return buttonId switch
        {
            CancelAll => "!cancel all",
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

/// <summary>How a card shows its map's picture. A custom HUD cannot take a picture's address from the server, so every thumbnail is in
/// the Workshop addon - the site's own picture of each map as a small JPEG, which the resource compiler stores as it is, or for an
/// official map the game's own screenshot - and the card picks one with classes.
///
/// Two classes per picture, not one per map: the game interns every class name the server ever sets on the layout entity, for the
/// entity's whole life and for all players together, and takes no more than 1,024 (the server logs "The maximum number of class names
/// has been reached" and what the client then does is unknown). Picture n is the pair th{n / 64} and tl{n % 64}, so 1,004 pictures
/// cost 80 names. n is the map's place in HudThumbList (hud/thumbs.py appends, nothing moves); gungame_hud_thumbs.css has the rule
/// ".th{n / 64}.tl{n % 64}" for each, and a map without a number shows the card's plain backdrop.</summary>
public static class HudThumbs
{
    public const int Base = 64;

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

    public static bool IsThumbClass(string cls) => cls.Length > 2 && cls[0] == 't' && cls[1] is 'h' or 'l' && cls[2..].All(char.IsAsciiDigit);
}
