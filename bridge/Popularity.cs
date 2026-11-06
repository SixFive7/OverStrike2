// The Workshop's own numbers for every listed map, and what the bridge makes of them: each map's popularity, the maps the server keeps on its
// disk, the fresh maps and every map's tags (MapTags, HudBrowser.cs). Two files beside maps.json, both the Workshop sync's, both read again when
// they change (Reload.cs); missing or unreadable, the bridge goes on with what it had, or without:
//
//   ratings.json, version 1 (the sync's daily ratings step): {"fetched": unix time, "scores": {id: [score, votes up, votes down, subscriptions]},
//     "trend": {mode: [ids, Most Popular first]}}.
//   workshop.json, version 1 (the sync's check and publish): {"written": unix time, "maps": {id: {"created", "updated", "content", "subs",
//     "lifetime", "favs", "lifetimeFavs", "views", "passed"}}} - the Workshop's time_created and time_updated (unix seconds), its hcontent_file,
//     subscriptions, lifetime_subscriptions, favorited, lifetime_favorited and views, and "passed": the time_updated of the content the sync
//     accepts, the one its last passing test was of (0 or missing: none known). The current content passed when "passed" is at least "updated".
//
// Popularity, per map and mode among that mode's listed Workshop maps, from 0 to 1: the mean of three families - Rated, its percentile by
// score; Played, the mean of its percentiles by subscribers, lifetime subscribers, favourites and views; Hot, its percentile in the 30-day trend
// order, where the maps past the fetched list tie at the bottom. A family with no data at all is left out; with none at all a map is
// NeutralPopularity. Valve's official maps of Deathmatch and Wingman and our own maps are OfficialPopularity. The points make PopularityPoints
// of it (Dice.cs): the most popular map has 16 times the odds of the least.
//
// The kept maps are the bridge's copy of the keep set (core/KeepSet.cs decides what the server stores): every liked map, our own maps, and per
// mode the three lists of its KeepTop playable maps by score, by subscribers and in the trend order. A ballot fills from them: a kept map counts
// as on the server's disk when it is liked, ours, or within the first KeepTop - KeptMargin of a list - the margin covers a list that moved since
// the server last stored it - unless the restart watcher's report covers it and says it is not there, or workshop.json says its content changed
// since the version the sync accepts (loading it would download the update first). Without ratings.json there is no keep set to go by, and
// every eligible listed map counts.
//
// Fresh: uploaded in the last FreshDays (NEW UPLOAD), or its content changed in the last FreshDays and that content passed the sync's test
// (UPDATED). Without workshop.json no map is fresh.
using System.Text.Json;

static partial class Program
{
    public const int KeepTop = 100;      // each of a mode's three lists of maps the server keeps (the keep set's Top, core/KeepSet.cs)
    public const int KeptMargin = 10;    // a kept map counts as on the disk within the first KeepTop - KeptMargin of a list
    public const int FreshDays = 30;     // NEW UPLOAD and UPDATED: in the last this many days
    public const double NeutralPopularity = 0.5, OfficialPopularity = 0.75;

    /// <summary>One map's entry in workshop.json.</summary>
    public sealed record WorkshopEntry(long Created, long Updated, string Content, long Subs, long Lifetime, long Favs, long LifetimeFavs, long Views, long Passed)
    {
        /// <summary>The Workshop's current content is the one the sync accepts.</summary>
        public bool ContentPassed => Passed > 0 && Passed >= Updated;

        /// <summary>The content changed since the version the sync accepts: nobody tested it yet.</summary>
        public bool ContentUntested => Passed > 0 && Updated > Passed;
    }

    static Dictionary<string, (double? Score, long? Subs)> RatingScores = new(); // ratings.json's scores: Workshop id -> score and subscriptions
    static Dictionary<string, List<string>> RatingTrend = new();                // ...and each mode's trend order, Most Popular first
    static long RatingsFetched;                                                  // ...and when the sync fetched them (unix seconds)
    static Dictionary<string, WorkshopEntry> WorkshopInfo = new();               // workshop.json: Workshop id -> its numbers and its last passing test
    static long WorkshopWritten;                                                 // ...and when the sync wrote it
    static int NumbersStamp;                                                     // moves whenever either file is taken: the tables below start again

    static string RatingsFile => Path.Combine(DataDir, "ratings.json");
    static string WorkshopFile => Path.Combine(DataDir, "workshop.json");

    /// <summary>ratings.json was read: the keep set's lists can be worked out, and the ballot fills from the kept maps.</summary>
    static bool RatingsKnown => RatingScores.Count > 0 || RatingTrend.Count > 0;

    static long NowUnix => new DateTimeOffset(Now()).ToUnixTimeSeconds();

    /// <summary>ratings.json's three parts. A score entry is [score, votes up, votes down, subscriptions] or a bare score; anything else is skipped.
    /// Another version, or a file that is not an object, throws to the caller.</summary>
    static (Dictionary<string, (double? Score, long? Subs)> Scores, Dictionary<string, List<string>> Trend, long Fetched) ParseRatings(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        NeedVersion1(root, "ratings.json");
        var scores = new Dictionary<string, (double?, long?)>();
        if (root.TryGetProperty("scores", out var sc) && sc.ValueKind == JsonValueKind.Object)
            foreach (var p in sc.EnumerateObject())
            {
                if (!p.Name.All(char.IsAsciiDigit) || p.Name == "") continue;
                var v = p.Value;
                double? score = v.ValueKind == JsonValueKind.Array ? (v.GetArrayLength() > 0 ? JsonNumber(v[0]) : null) : JsonNumber(v);
                long? subs = v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 3 && JsonNumber(v[3]) is { } n ? (long)n : null;
                if (score != null || subs != null) scores[p.Name] = (score, subs);
            }
        var trend = new Dictionary<string, List<string>>();
        if (root.TryGetProperty("trend", out var tr) && tr.ValueKind == JsonValueKind.Object)
            foreach (var p in tr.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Array)
                    trend[p.Name] = p.Value.EnumerateArray().Select(JsonText).Where(x => x != "" && x.All(char.IsAsciiDigit)).Distinct().ToList();
        return (scores, trend, Whole(root, "fetched"));
    }

    /// <summary>workshop.json's maps. An entry that is not an object, or an id that is not a number, is skipped.</summary>
    static (Dictionary<string, WorkshopEntry> Maps, long Written) ParseWorkshop(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        NeedVersion1(root, "workshop.json");
        var maps = new Dictionary<string, WorkshopEntry>();
        if (root.TryGetProperty("maps", out var ms) && ms.ValueKind == JsonValueKind.Object)
            foreach (var p in ms.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object || p.Name == "" || !p.Name.All(char.IsAsciiDigit)) continue;
                var e = p.Value;
                maps[p.Name] = new WorkshopEntry(Whole(e, "created"), Whole(e, "updated"), e.TryGetProperty("content", out var c) ? JsonText(c) : "",
                    Whole(e, "subs"), Whole(e, "lifetime"), Whole(e, "favs"), Whole(e, "lifetimeFavs"), Whole(e, "views"), Whole(e, "passed"));
            }
        return (maps, Whole(root, "written"));
    }

    /// <summary>ratings.json again (at start, and whenever it changes). Missing at start: no keep set to go by. Unreadable: what was read stays.</summary>
    static string ReloadRatings(string path)
    {
        if (!File.Exists(path)) return RatingsKnown ? $"ratings: {path} is gone - kept the ratings read" : $"ratings: {path} not found - no keep set to go by: a ballot fills from every eligible map";
        try { (RatingScores, RatingTrend, RatingsFetched) = ParseRatings(File.ReadAllText(path)); }
        catch (Exception e) { return $"ratings: {path} unreadable ({Clip(e.Message, 120)}) - kept {RatingScores.Count} scores"; }
        NumbersStamp++; CatalogVersion++; // the cards carry the Workshop's tags
        return $"ratings: {RatingScores.Count} scores and the trend of {string.Join(", ", RatingTrend.Select(t => $"{t.Key} {t.Value.Count}"))} from {path}, fetched {(RatingsFetched > 0 ? $"{(NowUnix - RatingsFetched) / 3600} h ago" : "at no time it says")}";
    }

    /// <summary>workshop.json again (at start, and whenever it changes). Missing at start: no map is fresh, and the stock waits. Unreadable: what was
    /// read stays.</summary>
    static string ReloadWorkshop(string path)
    {
        if (!File.Exists(path)) return WorkshopInfo.Count > 0 ? $"workshop: {path} is gone - kept what was read" : $"workshop: {path} not found - no map is fresh, and no map is known to have passed a test: the stock waits";
        try { (WorkshopInfo, WorkshopWritten) = ParseWorkshop(File.ReadAllText(path)); }
        catch (Exception e) { return $"workshop: {path} unreadable ({Clip(e.Message, 120)}) - kept {WorkshopInfo.Count} maps"; }
        NumbersStamp++; CatalogVersion++;
        return $"workshop: {WorkshopInfo.Count} maps from {path}, {WorkshopInfo.Values.Count(w => w.ContentPassed)} with their current content tested";
    }

    // ---- popularity ----

    /// <summary>What the popularity table was worked out from: when any of it is another object or another size, it is worked out again.</summary>
    readonly record struct ListsKey(List<MapInfo> Ar, int ArCount, List<MapInfo>? Dm, int DmCount, List<MapInfo>? Wm, int WmCount, HashSet<long> Ours, int OursCount, int Numbers);

    static ListsKey ListsNow() => new(Maps, Maps.Count, ModeMaps.GetValueOrDefault(Modes.DeathmatchId), ModeMaps.GetValueOrDefault(Modes.DeathmatchId)?.Count ?? 0,
        ModeMaps.GetValueOrDefault(Modes.WingmanId), ModeMaps.GetValueOrDefault(Modes.WingmanId)?.Count ?? 0, Ours, Ours.Count, NumbersStamp);

    static ListsKey? popularityFor;
    static readonly Dictionary<string, Dictionary<string, double>> popularity = new(); // mode -> map key -> 0..1

    /// <summary>Each listed Workshop map's percentile among <paramref name="keys"/> by a value: 0 the lowest, 1 the highest, ties sharing the middle
    /// of their places; the keys without a value tie at the very bottom, 0. Null when no key has a value.</summary>
    static Dictionary<string, double>? Percentiles(List<string> keys, Func<string, double?> value)
    {
        var values = keys.Select(k => (Key: k, Value: value(k))).ToList();
        if (values.All(v => v.Value == null)) return null;
        var sorted = values.Select(v => (v.Key, Value: v.Value ?? double.NegativeInfinity)).OrderBy(v => v.Value).ToList();
        var result = new Dictionary<string, double>(sorted.Count);
        var n = sorted.Count;
        for (var i = 0; i < n;)
        {
            var j = i;
            while (j + 1 < n && sorted[j + 1].Value == sorted[i].Value) j++;
            var p = double.IsNegativeInfinity(sorted[i].Value) ? 0 : n == 1 ? NeutralPopularity : (i + j) / 2.0 / (n - 1);
            for (var k = i; k <= j; k++) result[sorted[k].Key] = p;
            i = j + 1;
        }
        return result;
    }

    /// <summary>A mode's popularity table, worked out again when its inputs moved.</summary>
    static Dictionary<string, Dictionary<string, double>> PopularityTable()
    {
        var now = ListsNow();
        if (popularityFor == now) return popularity;
        popularityFor = now;
        popularity.Clear();
        foreach (var mode in Modes.All)
        {
            var keys = WorkshopOf(mode).Select(Key).Distinct().ToList();
            var trend = RatingTrend.GetValueOrDefault(mode.Id) ?? [];
            var place = trend.Select((id, i) => (id, i)).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i);
            WorkshopEntry? W(string k) => WorkshopInfo.GetValueOrDefault(k);
            var rated = Percentiles(keys, k => RatingScores.TryGetValue(k, out var s) ? s.Score : null);
            var played = new[]
            {
                Percentiles(keys, k => W(k) is { Subs: > 0 } w ? w.Subs : RatingScores.TryGetValue(k, out var s) ? s.Subs : null),
                Percentiles(keys, k => W(k) is { } w && w.Lifetime > 0 ? w.Lifetime : null),
                Percentiles(keys, k => W(k) is { } w && w.Favs > 0 ? w.Favs : null),
                Percentiles(keys, k => W(k) is { } w && w.LifetimeFavs > 0 ? w.LifetimeFavs : null),
                Percentiles(keys, k => W(k) is { } w && w.Views > 0 ? w.Views : null),
            }.OfType<Dictionary<string, double>>().ToList();
            var hot = trend.Count == 0 ? null : Percentiles(keys, k => place.TryGetValue(k, out var i) ? trend.Count - i : null);
            var table = new Dictionary<string, double>(keys.Count);
            foreach (var k in keys)
            {
                var families = new List<double>();
                if (rated != null) families.Add(rated[k]);
                if (played.Count > 0) families.Add(played.Average(p => p[k]));
                if (hot != null) families.Add(hot[k]);
                table[k] = families.Count == 0 ? NeutralPopularity : families.Average();
            }
            popularity[mode.Id] = table;
        }
        return popularity;
    }

    /// <summary>A map's popularity in the mode it plays in, 0 to 1 (the file's summary).</summary>
    static double PopularityOf(MapInfo m, Dictionary<string, Dictionary<string, double>>? table = null)
    {
        if (m.Official || IsOurs(m)) return OfficialPopularity;
        table ??= PopularityTable();
        return table.TryGetValue(ModeOf(m).Id, out var t) && t.TryGetValue(Key(m), out var p) ? p : NeutralPopularity;
    }

    // ---- the kept maps ----

    /// <summary>The keep set's parts as the bridge works them out: per mode the three lists (in their order) and, across the modes, the maps kept and
    /// the maps that count as on the disk by being kept.</summary>
    sealed class KeptMaps
    {
        public readonly Dictionary<string, List<string>> Rated = new(), Subscribed = new(), Trending = new(); // mode -> its KeepTop list, best first
        public readonly HashSet<string> TopRated = new(), MostSubscribed = new(), Trend = new();             // every mode's lists: the Workshop's tags
        public readonly HashSet<string> All = new(), Sure = new();                                           // kept; kept and counting as on the disk
    }

    readonly record struct KeptKey(ListsKey Lists, object Broken, int BrokenCount, object Packs, int PacksCount, object Removed, int RemovedCount, object Blocked,
        int BlockedCount, State S, int Crashes, int PlayerCrashes, object Votes, long Catalog);

    static KeptKey? keptFor;
    static KeptMaps kept = new();

    static KeptMaps Kept()
    {
        var now = new KeptKey(ListsNow(), Broken, Broken.Count, Packs, Packs.Count, Removed, Removed.Count, Blocked, Blocked.Count, S, S.Crashes.Count, S.PlayerCrashes.Count, Votes, CatalogVersion);
        if (keptFor == now) return kept;
        keptFor = now;
        var k = new KeptMaps();
        foreach (var key in Votes.Where(v => v.Value.Values.Any(x => x > 0)).Select(v => v.Key)) { k.All.Add(key); k.Sure.Add(key); } // liked: every map with an up vote
        foreach (var id in Ours) { k.All.Add(id.ToString()); k.Sure.Add(id.ToString()); }
        if (RatingsKnown)
            foreach (var mode in Modes.All)
            {
                var playable = WorkshopOf(mode).Where(m => !Bad(m)).Select(Key).Distinct().ToList(); // MapStanding.cs: nothing against it
                var place = playable.Select((key, i) => (key, i)).ToDictionary(x => x.key, x => x.i);
                var rated = playable.Where(x => RatingScores.TryGetValue(x, out var s) && s.Score != null).OrderByDescending(x => RatingScores[x].Score).ThenBy(x => place[x]).Take(KeepTop).ToList();
                var subscribed = playable.Where(x => RatingScores.TryGetValue(x, out var s) && s.Subs != null).OrderByDescending(x => RatingScores[x].Subs).ThenBy(x => place[x]).Take(KeepTop).ToList();
                var trending = (RatingTrend.GetValueOrDefault(mode.Id) ?? []).Where(place.ContainsKey).Take(KeepTop).ToList();
                (k.Rated[mode.Id], k.Subscribed[mode.Id], k.Trending[mode.Id]) = (rated, subscribed, trending);
                k.TopRated.UnionWith(rated); k.MostSubscribed.UnionWith(subscribed); k.Trend.UnionWith(trending);
                foreach (var list in new[] { rated, subscribed, trending })
                {
                    k.All.UnionWith(list);
                    k.Sure.UnionWith(list.Take(KeepTop - KeptMargin));
                }
            }
        return kept = k;
    }

    /// <summary>In the keep set: the server stores it, whatever the bridge's report says.</summary>
    static bool IsKept(MapInfo m) => Kept().All.Contains(Key(m));

    /// <summary>On the server's disk because the server keeps it - an official map always; a Workshop map when it is liked, ours or well inside a list
    /// (KeptMargin), unless the watcher's report covers it and says it is not there, or its content changed since the version the sync accepts.
    /// Without ratings.json: every map but one the report says is missing.</summary>
    static bool KeptOnDisk(MapInfo m)
    {
        if (m.Official) return true;
        var key = Key(m);
        if (ReportedMissing(key) || WorkshopInfo.GetValueOrDefault(key) is { ContentUntested: true }) return false;
        return !RatingsKnown || Kept().Sure.Contains(key);
    }

    // ---- fresh ----

    static bool IsNewUpload(MapInfo m) => !m.Official && WorkshopInfo.TryGetValue(Key(m), out var w) && w.Created > 0 && NowUnix - w.Created <= FreshDays * 86400L;

    static bool IsFreshUpdate(MapInfo m) => !m.Official && WorkshopInfo.TryGetValue(Key(m), out var w) && w.Updated > 0 && NowUnix - w.Updated <= FreshDays * 86400L
        && w.ContentPassed && !IsNewUpload(m);

    /// <summary>A new upload whose Workshop copy changed after it went up, the change tested by our sync: its tags say NEW UPLOAD and UPDATED.</summary>
    static bool UpdatedSinceUpload(MapInfo m) => IsNewUpload(m) && WorkshopInfo.TryGetValue(Key(m), out var w) && w.Updated > w.Created && w.ContentPassed;

    /// <summary>A fresh map: a new Workshop upload, or a recent update our sync has tested.</summary>
    static bool IsFresh(MapInfo m) => IsNewUpload(m) || IsFreshUpdate(m);

    /// <summary>Its current Workshop content passed the sync's test: what a stock entry needs.</summary>
    static bool ContentPassed(MapInfo m) => WorkshopInfo.TryGetValue(Key(m), out var w) && w.ContentPassed;

    // ---- the tags ----

    /// <summary>A map's tags (MapTags), in precedence order, as the wire carries them: a caution (too big, ads), then why it is there - PICKED for a
    /// player's pick, ours, liked, a recent upload or update, the Workshop's three lists, new to us (never shown or played here).</summary>
    static string TagsOf(MapInfo m, bool picked = false)
    {
        var key = Key(m);
        var k = Kept();
        var (up, down) = Tally(key);
        var tags = new List<string>(6);
        if (picked) tags.Add(MapTags.Picked);
        if (TooBig(m)) tags.Add(MapTags.Big);
        if (AdsVotes(key) > 0) tags.Add(MapTags.Ads);
        if (IsOurs(m)) tags.Add(MapTags.Ours);
        if (up > 0 && up > down) tags.Add(MapTags.Liked);
        if (IsNewUpload(m)) tags.Add(MapTags.Upload);
        if (IsFreshUpdate(m) || UpdatedSinceUpload(m)) tags.Add(MapTags.Updated); // a new upload updated since shows both
        if (k.Trend.Contains(key)) tags.Add(MapTags.Trending);
        if (k.TopRated.Contains(key)) tags.Add(MapTags.TopRated);
        if (k.MostSubscribed.Contains(key)) tags.Add(MapTags.Popular);
        if (!m.Official && !S.Met.Contains(key)) tags.Add(MapTags.NewToUs);
        return MapTags.Join(tags);
    }

    /// <summary>The top line's few words of why: " (liked by us)", or nothing.</summary>
    static string WhyNote(MapInfo m) => MapTags.WhyWords(TagsOf(m), picked: false) is { Length: > 0 } w ? $" ({w})" : "";

    /// <summary>The health page's line about the Workshop's numbers and the kept maps.</summary>
    static string NumbersHealth()
    {
        var k = Kept();
        return $"ratings: {(RatingsKnown ? $"{RatingScores.Count} scores, fetched {(RatingsFetched > 0 ? $"{(NowUnix - RatingsFetched) / 3600} h ago" : "at no known time")}" : "none - a ballot fills from every eligible map")}; "
            + $"workshop.json: {(WorkshopInfo.Count > 0 ? $"{WorkshopInfo.Count} maps, {WorkshopInfo.Values.Count(w => w.ContentPassed)} tested as they are" : "none - no map is fresh, the stock waits")}; "
            + $"kept {k.All.Count}, on the disk by being kept {k.Sure.Count}; fresh " + string.Join(", ", Modes.All.Select(md => $"{md.Id} {PoolOf(md).Count(IsFresh)}"));
    }
}
