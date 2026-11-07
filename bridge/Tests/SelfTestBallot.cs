// --selftest, the ballot's recipe and what it stands on: the Workshop's numbers (ratings.json, workshop.json), each map's popularity, the kept
// maps, the points with the step back of a card shown, the tags (MapTags) on the catalog and the vote's cards, the recipe itself - Keep, the
// picks, the other modes, the fresh card, the surprise and the favourites, every card but a pick on the server's disk - and what it remembers,
// both timers on the wire, the restart notices before and after a restart, and the background download. Runs in the harness (SelfTestHarness.cs),
// on its own lists.

static partial class Program
{
    static void BallotSelfTest(Action<string, bool> Check)
    {
        var h = new Harness("ballot", new DateTime(2026, 11, 5, 18, 0, 0, DateTimeKind.Utc));
        var statusHumans = 0;
        h.Server = command => command == "status" ? $"hostname : test\nspawn    : 2\nplayers  : {statusHumans} humans, 0 bots (0 max)\n#end" : "";
        try
        {
            long Unix(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();
            // Arms Race: 400 maps, the Workshop's favourites #1-#150 by score, subscribers and trend (#1 the best), so #1-#100 are kept; #5 uploaded
            // three days ago and #7 updated five days ago and tested (both kept and fresh); #300-#320 uploaded two days ago (fresh, not kept: the stock's
            // fresh half). Deathmatch and Wingman: 60 maps each, #1-#40 rated.
            void Fresh() // a bridge that has just started (ResetAll), with the plugin drawing the vote, on the lists below
            {
                h.Fresh();
                HudActive = () => true; StockSize = 10; statusHumans = 0;
                Maps = Enumerable.Range(1, 400).Select(r => new MapInfo(r, 9_000_000 + r, $"ar_{r}", 10 + r % 40)).ToList();
                ModeMaps.Clear();
                ModeMaps[Modes.DeathmatchId] = Enumerable.Range(1, 60).Select(r => new MapInfo(r, 9_100_000 + r, $"dm_{r}", 10, false, Modes.DeathmatchId)).ToList();
                ModeMaps[Modes.WingmanId] = Enumerable.Range(1, 60).Select(r => new MapInfo(r, 9_200_000 + r, $"wm_{r}", 10, false, Modes.WingmanId)).ToList();
                RatingScores = Maps.Take(150).Select((m, i) => (Key(m), (double?)(0.99 - i * 0.002), (long?)(200_000 - i * 100)))
                    .Concat(ModeMaps.Values.SelectMany(l => l.Take(40).Select((m, i) => (Key(m), (double?)(0.95 - i * 0.01), (long?)(50_000 - i * 100)))))
                    .ToDictionary(x => x.Item1, x => (x.Item2, x.Item3));
                RatingTrend = new() { [Modes.ArmsRaceId] = Maps.Take(150).Select(Key).ToList() };
                RatingsFetched = Unix(h.Clock.AddHours(-3));
                var old = Unix(h.Clock.AddDays(-400));
                WorkshopInfo = AllKnown.Where(m => !m.Official).GroupBy(Key).Select(g => g.First()).ToDictionary(Key, m =>
                {
                    var subs = Math.Max(1, 100_000 - m.Rank * 200);
                    return new WorkshopEntry(old, old, "", subs, subs * 2, subs / 10, subs / 5, subs * 10, old);
                });
                void MakeFresh(int rank, double createdDaysAgo, double updatedDaysAgo) =>
                    WorkshopInfo[Key(Maps[rank - 1])] = WorkshopInfo[Key(Maps[rank - 1])] with { Created = Unix(h.Clock.AddDays(-createdDaysAgo)), Updated = Unix(h.Clock.AddDays(-updatedDaysAgo)), Passed = Unix(h.Clock.AddDays(-updatedDaysAgo)) };
                MakeFresh(5, 3, 3);
                MakeFresh(7, 400, 5);
                for (var r = 300; r <= 320; r++) MakeFresh(r, 2, 2);
                NumbersStamp++;
                S.Map = Maps[200]; S.LogName = Maps[200].Name; S.Mode = Modes.ArmsRaceId; // the map that is on: ar_201, kept by nobody
            }
            MapInfo Ar(int rank) => Maps[rank - 1];
            void ReportAllStock() { FillStock(); ApplyReady("gg-ready\t1\n" + string.Join("\n", Modes.All.SelectMany(m => LaneOf(m).Select(e => e.Key))) + "\n"); }
            var players = Enumerable.Range(1, 9).Select(i => ($"P{i}", $"[U:1:{3000 + i}]")).ToArray();

            // ---- the Workshop's numbers: ratings.json and workshop.json, version 1 ----
            var (scores, trend, fetched) = ParseRatings("""{"version": 1, "fetched": 1793000000, "scores": {"3001": [0.91, 120, 4, 5400], "3002": 0.5, "x": [1], "3003": "bad"}, "trend": {"armsrace": ["3002", 3001, "3002", "z"]}, "trend_days": 30}""");
            Check("ratings.json: each score with its subscribers (an entry [score, up, down, subscribers] or a bare score), each mode's trend in order and once, when it was fetched; anything else skipped",
                scores.Count == 2 && scores["3001"] == (0.91, 5400) && scores["3002"] == (0.5, null) && trend["armsrace"].SequenceEqual(["3002", "3001"]) && fetched == 1793000000);
            var threw = 0;
            foreach (var bad in new[] { """{"version": 2}""", "[1, 2]", "{" }) try { ParseRatings(bad); } catch (Exception) { threw++; }
            Check("...another version, a file that is not an object, or one that does not parse is refused whole", threw == 3);
            var (ws, written) = ParseWorkshop("""{"version": 1, "written": 1793000100, "maps": {"3001": {"created": 100, "updated": 200, "content": "77", "subs": 5, "lifetime": 9, "favs": 1, "lifetimeFavs": 2, "views": 50, "passed": 200}, "3002": {"updated": 300, "passed": 200}, "3003": 4, "x": {}}}""");
            Check("workshop.json: each map's upload and content times, its numbers and the time_updated of the content its last passing test was of; the current content passed when that is not older",
                ws.Count == 2 && written == 1793000100 && ws["3001"] is { Created: 100, Updated: 200, Content: "77", Subs: 5, Lifetime: 9, Favs: 1, LifetimeFavs: 2, Views: 50, ContentPassed: true, ContentUntested: false }
                && ws["3002"] is { ContentPassed: false, ContentUntested: true } && new WorkshopEntry(1, 5, "", 0, 0, 0, 0, 0, 0) is { ContentPassed: false, ContentUntested: false });
            Check("Both are read beside maps.json, and watched like every data file (a new one is read within seconds)",
                RatingsFile == Path.Combine(Path.GetDirectoryName(Path.GetFullPath(MapsFile))!, "ratings.json") && WorkshopFile.EndsWith("workshop.json")
                && DataFiles().Any(f => f.Item1 == RatingsFile) && DataFiles().Any(f => f.Item1 == WorkshopFile));
            var ratingsPath = Path.Combine(h.Dir, "ratings-test.json");
            File.WriteAllText(ratingsPath, """{"version": 1, "fetched": 1, "scores": {"1": [0.5, 1, 1, 10]}, "trend": {}}""");
            var keptRatings = (RatingScores, RatingTrend, RatingsFetched);
            var tookGood = ReloadRatings(ratingsPath).StartsWith("ratings: 1 scores") && RatingScores.Count == 1;
            File.WriteAllText(ratingsPath, "{ broken");
            var keptOnBad = ReloadRatings(ratingsPath).Contains("unreadable") && RatingScores.Count == 1;
            Check("...a reload takes a good file, and an unreadable one leaves what was read", tookGood && keptOnBad);
            (RatingScores, RatingTrend, RatingsFetched) = keptRatings; NumbersStamp++;

            // ---- popularity: Rated, Played and Hot, each a percentile among the mode's listed maps ----
            var pc = Percentiles(["a", "b", "c", "d"], k => k switch { "a" => 1, "b" => 2, "c" => 2, _ => null })!;
            Check("A percentile: the highest 1, ties sharing the middle of their places, a map without a value at the very bottom; none with a value, no family",
                pc["d"] == 0 && Math.Abs(pc["a"] - 1 / 3.0) < 1e-9 && Math.Abs(pc["b"] - 5 / 6.0) < 1e-9 && pc["b"] == pc["c"] && Percentiles(["a"], _ => null) == null);
            Fresh();
            var table = PopularityTable();
            Check($"Popularity: the map best by score, by subscribers, lifetime subscribers, favourites and views, and first in the trend is 1; one with no score, the fewest players and no trend 0 - {PopularityPoints} points apart, 16 times the odds",
                Math.Abs(PopularityOf(Ar(1)) - 1) < 1e-9 && PopularityOf(Ar(400)) < 0.01 && Math.Abs(Weight(Ar(1)) / Weight(Ar(400)) - Math.Pow(2, PopularityPoints * (PopularityOf(Ar(1)) - PopularityOf(Ar(400))))) < 1e-6
                && Weight(Ar(1)) / Weight(Ar(400)) > 15);
            Check("...a map the Workshop ranks lower is less popular, all three families counting; Valve's official maps of another mode and our own maps count as 0.75",
                PopularityOf(Ar(2)) < PopularityOf(Ar(1)) && PopularityOf(Ar(160)) < PopularityOf(Ar(140)) && PopularityOf(PoolOf(Modes.Deathmatch).First(m => m.Official)) == OfficialPopularity
                && (Ours = [Ar(200).Id]) != null && PopularityOf(Ar(200)) == OfficialPopularity && (Ours = new()) != null);
            var noTrend = RatingTrend; RatingTrend = new(); NumbersStamp++;
            var rated = PopularityOf(Ar(1));
            RatingTrend = noTrend; NumbersStamp++;
            var noNumbers = (RatingScores, RatingTrend, WorkshopInfo); (RatingScores, RatingTrend, WorkshopInfo) = (new(), new(), new()); NumbersStamp++;
            Check("A family with no data at all is left out (no trend: Rated and Played alone); with no data at all every map is 0.5",
                Math.Abs(rated - 1) < 1e-9 && PopularityOf(Ar(1)) == NeutralPopularity && PopularityOf(Ar(400)) == NeutralPopularity && Weight(Ar(1)) == Weight(Ar(400)));
            (RatingScores, RatingTrend, WorkshopInfo) = noNumbers; NumbersStamp++;

            // ---- the points: popularity, likes, dislikes, and the step back of a card shown ----
            var w0 = Weight(Ar(50));
            Votes[Key(Ar(50))] = new() { ["[U:1:1]"] = 1 };
            var liked = Weight(Ar(50));
            Votes[Key(Ar(50))] = new() { ["[U:1:1]"] = DownReasons.Plain };
            var disliked = Weight(Ar(50));
            Votes = new();
            Check($"The points: a like is a point (twice the odds), a dislike takes {DislikePoints} (a quarter)", Math.Abs(liked / w0 - 2) < 1e-9 && Math.Abs(disliked / w0 - 0.25) < 1e-9);
            var now = Unix(h.Clock);
            S.Shown[Key(Ar(50))] = [now];
            var once = Weight(Ar(50));
            S.Shown[Key(Ar(50))] = [now, now - 3600];
            var twice = Weight(Ar(50));
            S.Shown[Key(Ar(50))] = [now, now, now];
            var thrice = Weight(Ar(50));
            S.Shown[Key(Ar(50))] = [now - ShownFadeDays / 2 * 86400L];
            var halfFaded = Weight(Ar(50));
            S.Shown[Key(Ar(50))] = [now - ShownFadeDays * 86400L];
            var faded = Weight(Ar(50));
            S.Shown.Clear();
            Check($"A card shown and not chosen: {ShownPoints} point off (half the odds), {ShownMaxPoints} after two (a quarter), never more; fading in a straight line to nothing over {ShownFadeDays} days",
                Math.Abs(once / w0 - 0.5) < 1e-9 && Math.Abs(twice / w0 - 0.25) < 0.01 && Math.Abs(thrice / w0 - 0.25) < 1e-9
                && Math.Abs(halfFaded / w0 - Math.Pow(2, -0.5)) < 1e-6 && Math.Abs(faded / w0 - 1) < 1e-9 && ShownFadeDays == 28 && ShownMaxPoints == 2);

            // ---- the kept maps ----
            Check($"The kept maps: per mode the three lists of the {KeepTop} best by score, by subscribers and in the trend - Arms Race's #1-#100 here - and nothing past them",
                Kept().Rated[Modes.ArmsRaceId].Count == KeepTop && Kept().Rated[Modes.ArmsRaceId][0] == Key(Ar(1)) && IsKept(Ar(100)) && !IsKept(Ar(101))
                && Kept().Subscribed[Modes.Deathmatch.Id].Count == 40 && Kept().Trending.GetValueOrDefault(Modes.WingmanId)?.Count == 0);
            S.Crashes[Key(Ar(3))] = 1;
            Check("...a map with a crash on record leaves the lists, and the next one moves up - as the keep set does it", !IsKept(Ar(3)) && IsKept(Ar(101)));
            S.Crashes.Clear();
            Votes[Key(Ar(390))] = new() { ["[U:1:1]"] = 1, ["[U:1:2]"] = -1 }; Ours = [Ar(391).Id];
            Check("...every map with a like is kept, however many dislikes it has, and every map of ours", IsKept(Ar(390)) && IsKept(Ar(391)) && KeptOnDisk(Ar(390)) && KeptOnDisk(Ar(391)));
            Votes = new(); Ours = new();

            // ---- the tags ----
            Check("The tags' table: ids unique and plain, chips of 12 characters at most in upper case, every colour #RRGGBB, every family known; in precedence order - the vote's state, a caution, then why",
                MapTags.All.Select(t => t.Id).Distinct().Count() == MapTags.All.Length && MapTags.All.All(t => t.Id.All(char.IsAsciiLetterLower) && t.Chip.Length <= 12 && t.Chip == t.Chip.ToUpperInvariant()
                    && System.Text.RegularExpressions.Regex.IsMatch(t.Colour, "^#[0-9A-F]{6}$")) && MapTags.ChipText == "#10141A"
                && MapTags.All.Select(t => t.Family).Distinct().SequenceEqual([MapTags.VoteFamily, MapTags.CautionFamily, MapTags.PlayerFamily, MapTags.UsFamily, MapTags.FreshFamily, MapTags.WorkshopFamily, MapTags.DiscoveryFamily])
                && MapTags.All.Select(t => t.Id).SequenceEqual(["next", "lead", "tie", "on", "big", "ads", "picked", "ours", "liked", "upload", "updated", "trending", "toprated", "popular", "newtous"]));
            Check("...the wire: known ids only, each once, in precedence order; the first why tag in words, a player's pick left out where the line names the player; two chips a card",
                MapTags.Join(["newtous", "zzz", "big", "liked", "big"]) == "big,liked,newtous" && MapTags.Clean("trending, liked;x") == "trending" && MapTags.FirstWhy("big,picked,liked")?.Id == "picked"
                && MapTags.WhyWords("big,picked,liked", picked: false) == "liked by us" && MapTags.WhyWords("big,ads") == "" && MapTags.Chips("big,liked,trending").Select(t => t.Id).SequenceEqual(["big", "liked"]));
            Sizes[Key(Ar(1))] = new SizeEntry(20000, "XL"); Votes[Key(Ar(1))] = new() { ["[U:1:1]"] = 1 };
            Check("A map's tags: too big, liked, the Workshop's three lists, new to us - in that order; a recent upload, a tested recent update; officials are no discovery",
                TagsOf(Ar(1)) == "big,liked,trending,toprated,popular,newtous" && TagsOf(Ar(5)).StartsWith("upload,") && TagsOf(Ar(7)).StartsWith("updated,") && TagsOf(Ar(310)) == "upload,newtous"
                && TagsOf(Ar(400)) == "newtous" && TagsOf(PoolOf(Modes.Deathmatch).First(m => m.Official)) == "" && TagsOf(Ar(400), picked: true) == "picked,newtous");
            Votes[Key(Ar(1))]["[U:1:2]"] = -1; Votes[Key(Ar(1))]["[U:1:3]"] = AdsVote;
            Check("...liked only with more likes than dislikes; an ads mark is a caution", TagsOf(Ar(1)) == "big,ads,trending,toprated,popular,newtous");
            Sizes = new(); Votes = new();
            WorkshopInfo[Key(Ar(7))] = WorkshopInfo[Key(Ar(7))] with { Passed = Unix(h.Clock.AddDays(-9)) }; NumbersStamp++;
            Check("...a recent update the sync has not tested is no UPDATED (and the map is not on the disk at its current version)", !TagsOf(Ar(7)).Contains("updated") && !KeptOnDisk(Ar(7)));
            WorkshopInfo[Key(Ar(7))] = WorkshopInfo[Key(Ar(7))] with { Passed = Unix(h.Clock.AddDays(-5)) }; NumbersStamp++;
            S.Met.Add(Key(Ar(400)));
            Check("...a map shown or played here is no longer new to us", TagsOf(Ar(400)) == "");
            S.Met.Clear();
            var catalog = BuildCatalog();
            var catText = catalog.Encode();
            var catBack = HudCatalog.Decode(catText)!;
            var line5 = catText.Split('\n').First(l => l.StartsWith($"m\t{Key(Ar(5))}\t"));
            Check("The catalog carries each map's tags as field 24, which an older plugin does not read (it stops at 23)",
                catBack.Find(Key(Ar(5)))!.Tags == TagsOf(Ar(5)) && line5.Split('\t').Length == 24 && line5.Split('\t')[23] == TagsOf(Ar(5))
                && HudCatalog.Decode(HudCatalog.Magic + "\n" + string.Join('\t', line5.Split('\t').Take(23)))!.Maps.Single().Tags == "");

            // ---- the recipe: four on, a map on, nobody's pick: Keep, the other modes, a fresh map, a surprise, the favourites ----
            Fresh();
            foreach (var p in players.Take(4)) h.Join(p);
            ReportAllStock();
            var laneBefore = LaneOf(Modes.ArmsRace).Select(e => e.Key).ToList();
            ClearNotices();
            StartVote(players[0].Item1, players[0].Item2);
            var b = Ballot!;
            BallotOption? ByOrder(int order) => b.Options.FirstOrDefault(o => o.Order == order);
            var fresh = ByOrder(FreshOrder);
            var surprise = ByOrder(SurpriseOrder);
            var favourites = b.Options.Where(o => o.Order >= FavouriteOrder && o.Order < OtherOrder).ToList();
            var surpriseKey = surprise?.Map is { } sk ? Key(sk) : "";
            var freshKey = fresh?.Map is { } fk ? Key(fk) : "";
            var others = b.Options.Where(o => o.Order >= OtherOrder && o.Source == VoteSource.Rolled).ToList();
            Console.WriteLine("recipe: " + string.Join(" | ", b.Options.Select(o => $"{o.N}. {OptionLabel(b, o)} ({o.Order}, {o.Tags})")));
            Check($"Four on, nobody's pick: Keep, a fresh map, a surprise, {MapVote.PageCards - 5} favourites, one Deathmatch and one Wingman map - the page's {MapVote.PageCards} - and Random",
                b.Options.Count == MapVote.PageCards + 1 && b.Options[0].Source == VoteSource.Keep && fresh != null && surprise != null && favourites.Count == MapVote.PageCards - 5
                && others.Select(o => o.Mode.Id).SequenceEqual([Modes.DeathmatchId, Modes.WingmanId]) && b.Options[^1].Source == VoteSource.Random
                && b.Options.Take(MapVote.PageCards).Select(o => o.N).SequenceEqual(Enumerable.Range(1, MapVote.PageCards)));
            Check("...the fresh card a recent upload or tested update on the disk; the surprise the stock's first downloaded entry, new to us; the favourites and the other modes' cards kept maps on the disk",
                fresh!.Map is { } fm && IsFresh(fm) && (KeptOnDisk(fm) || laneBefore.Contains(Key(fm))) && surprise!.Map is { } sm && laneBefore.Contains(Key(sm)) && surprise.Tags.Contains("newtous")
                && favourites.All(o => o.Map is { } x && KeptOnDisk(x) && IsKept(x)) && others.All(o => o.Map is { } x && KeptOnDisk(x)));
            Check("...the surprise is used up the moment it is shown: gone from its lane, which rolls the next at once",
                LaneOf(Modes.ArmsRace).All(e => e.Key != surpriseKey) && LaneOf(Modes.ArmsRace).Count == StockSize);
            Check("...everything shown is remembered: every card's map is no longer new to us, and each card the dice put on it has its showing - not the map that is on",
                b.Options.Where(o => o.Map != null).All(o => S.Met.Contains(Key(o.Map!))) && b.Options.Where(o => o.Source == VoteSource.Rolled).All(o => S.Shown.ContainsKey(Key(o.Map!)) && o.ShownAt > 0)
                && !S.Shown.ContainsKey(Key(Ar(201))));
            var wire = LiveVote();
            Check("...the cards' tags ride the live state (mvo field 11), as they were when each went on: the surprise NEW TO US, the fresh card NEW UPLOAD or UPDATED, the favourites the Workshop's lists",
                wire.Options.Single(o => o.Key == surpriseKey).Tags.Contains("newtous") && wire.Options.Single(o => o.Key == freshKey).Tags.Split(',').Intersect(["upload", "updated"]).Any()
                && favourites.All(f => wire.Options.Single(o => o.Key == Key(f.Map!)).Tags.Split(',').Intersect(["trending", "toprated", "popular"]).Any())
                && VoteState.Decode("mv\t5\topen\t40\t1\t0\tmid\t0\t1\t0\t\nmvo\t1\tarmsrace\trolled\t1\t1\tx\t\t0\t\n").Options.Single().Tags == "");
            var chosen = favourites[0];
            var chosenPoints = Points(chosen.Map!);
            var notChosen = favourites[1];
            var before = ShownPenalty(Key(notChosen.Map!), Unix(h.Clock));
            foreach (var p in players.Take(4)) Command(p.Item1, p.Item2, MapVote.Click(b.Round, chosen.N));
            Check("Decided: the chosen card's showing is taken back (it was chosen), a card not chosen keeps its step back",
                b.Result?.N == chosen.N && !S.Shown.ContainsKey(Key(chosen.Map!)) && ShownPenalty(Key(notChosen.Map!), Unix(h.Clock)) == before && before == ShownPoints
                && Math.Abs(Points(chosen.Map!) - chosenPoints - ShownPoints) < 1e-9);
            Check("...and the news line says why the winner was on it: \"(NN MB, trending on the Workshop)\" and the like", h.Said(MapTags.WhyWords(TagsOf(chosen.Map!), picked: false)));

            // ---- a fresh pick: no second fresh card; a pick's tags start with PICKED; the news line says why the map is good ----
            Fresh();
            foreach (var p in players.Take(4)) h.Join(p);
            ReportAllStock();
            ClearNotices();
            Command(players[0].Item1, players[0].Item2, $"!map {ArgOf(Ar(5))}");
            b = Ballot!;
            Check($"A pick of a fresh map: no second fresh card - Keep, the pick, a surprise, {MapVote.PageCards - 5} favourites and the other modes",
                b.Options.All(o => o.Order != FreshOrder) && b.Options.Count(o => o.Order == SurpriseOrder) == 1 && b.Options.Count(o => o.Order >= FavouriteOrder && o.Order < OtherOrder) == MapVote.PageCards - 5
                && b.Options.Single(o => o.Source == VoteSource.Picked).Tags.StartsWith("picked,upload"));
            Check("...and the news line says who put it on and why it is there: \"P1 put #5 ar_5 (a recent Workshop upload) to the vote\"", h.Said($"P1 put {Label(Ar(5))} (a recent Workshop upload) to the vote"));

            // ---- nothing fresh on the disk, the stock not downloaded: favourites fill the page ----
            Fresh();
            foreach (var p in players.Take(4)) h.Join(p);
            foreach (var r in new[] { 5, 7 }) WorkshopInfo[Key(Ar(r))] = WorkshopInfo[Key(Ar(r))] with { Created = Unix(h.Clock.AddDays(-400)), Updated = Unix(h.Clock.AddDays(-400)), Passed = Unix(h.Clock.AddDays(-400)) };
            NumbersStamp++;
            FillStock(); // the stock rolled, but no report says any of it is on the disk
            StartVote(players[0].Item1, players[0].Item2);
            b = Ballot!;
            Check($"No fresh map on the disk and the stock not downloaded yet: the fresh card and the surprise are favourites too - {MapVote.PageCards - 3} kept maps of the mode, nobody waits for a download",
                b.Options.All(o => o.Order is not (FreshOrder or SurpriseOrder)) && b.Options.Count(o => o.Order >= FavouriteOrder && o.Order < OtherOrder) == MapVote.PageCards - 3
                && b.Options.Where(o => o.Source == VoteSource.Rolled).All(o => KeptOnDisk(o.Map!)));

            // ---- picks while the vote is open: the favourites make room first, then the surprise, the fresh card last; the other modes keep their place ----
            Fresh();
            foreach (var p in players) h.Join(p); // nine on: no Wingman card; the clock stands still, so the vote stays open whoever votes
            ReportAllStock();
            StartVote(players[8].Item1, players[8].Item2);
            b = Ballot!;
            var order = b.Options.Where(o => o.Source == VoteSource.Rolled && o.Order < OtherOrder).OrderByDescending(o => o.Order).Select(o => o.Order).ToList();
            var dm = b.Options.Single(o => o.Order >= OtherOrder && o.Source == VoteSource.Rolled);
            var replaced = new List<int>();
            var picks = MapVote.PageCards - 1; // one more than the random cards of the mode: the last one opens a second page
            var extra = Maps.Skip(150).Where(x => b.Options.All(o => o.Map != x)).Take(picks).ToList();
            for (var i = 0; i < picks; i++)
            {
                var room = b.Options.Where(o => o.Source == VoteSource.Rolled && o.Order < OtherOrder).Select(o => o.Order).ToHashSet();
                Command(players[i].Item1, players[i].Item2, $"!map {ArgOf(extra[i])}");
                replaced.AddRange(room.Where(r => b.Options.All(o => o.Order != r)));
            }
            Check($"{picks} picks during the vote: each takes the place of a random card nobody voted for - the favourites first (the last rolled first), then the surprise, the fresh card last ({string.Join(", ", replaced)})",
                order.Count == MapVote.PageCards - 2 && replaced.SequenceEqual(order) && order[^2] == SurpriseOrder && order[^1] == FreshOrder);
            Check("...the Deathmatch card keeps its place and number; the last pick, with no random card of the mode left, opens a second page",
                b.Options.Contains(dm) && b.Options.Single(o => o.Order >= OtherOrder && o.Source == VoteSource.Rolled).N == dm.N && b.Options.Count(o => o.Source == VoteSource.Picked) == picks
                && b.Options.Max(o => o.Source == VoteSource.Random ? 0 : o.N) == MapVote.PageCards + 1 && HudVote.Pages(LiveVote()) == 2);

            // ---- Random takes a map on the disk ----
            Fresh();
            foreach (var p in players.Take(3)) h.Join(p);
            ReportAllStock();
            h.MatchEnd();
            foreach (var p in players.Take(3)) Command(p.Item1, p.Item2, MapVote.Click(Ballot!.Round, MapVote.RandomOption));
            Check("Random wins: the dice take a map on the server's disk - a kept one, or a downloaded stock entry - so it loads at once",
                Pending is { By: "the vote" } pr && (KeptOnDisk(pr.Map) || S.Met.Contains(Key(pr.Map))) && h.Said("The vote chose Random"));

            // ---- both timers on the wire ----
            Fresh();
            foreach (var p in players.Take(3)) h.Join(p);
            var inactivity = MapVote.InactivitySeconds;
            MapVote.InactivitySeconds = 5;
            try
            {
                StartVote(players[0].Item1, players[0].Item2);
                var v1 = LiveVote();
                Check("Both timers on the wire: while somebody has not voted, the vote's limit counts (SecondsLeft) and the inactivity timer does not",
                    v1.Open && v1.SecondsLeft == MapVote.Seconds && v1.Inactivity == 0 && v1.ResetBy == "");
                h.Run(20);
                foreach (var p in players.Take(3)) Command(p.Item1, p.Item2, MapVote.Click(Ballot!.Round, 1));
                var v2 = LiveVote();
                Check("...once everybody has voted, both: the inactivity timer with who reset it, and the limit's seconds left",
                    v2.Open && v2.Inactivity == MapVote.InactivitySeconds && v2.ResetBy == "P3" && v2.SecondsLeft == MapVote.Seconds - 20);
                Fresh();
                foreach (var p in players.Take(3)) h.Join(p);
                StartVote(players[0].Item1, players[0].Item2);
                h.Run(MapVote.Seconds - 3);
                foreach (var p in players.Take(3)) Command(p.Item1, p.Item2, MapVote.Click(Ballot!.Round, 1));
                var v3 = LiveVote();
                Check("...near the limit the inactivity timer shows no more than the limit leaves: the limit closes it first",
                    v3.Open && v3.SecondsLeft == 3 && v3.Inactivity == 3 && v3.ResetBy == "P3");
                h.Run(3);
                Check("...and closes it", Ballot is not { Open: true });
            }
            finally { MapVote.InactivitySeconds = inactivity; }

            // ---- the restart notices, as the server program's watcher sends them ----
            Fresh();
            foreach (var p in players.Take(3)) h.Join(p);
            Check("A restart notice: refused unless it is gg-restart 1 with a known kind, match-end or now, and whole seconds",
                ApplyRestartNotice("hello").StartsWith("refused") && ApplyRestartNotice("gg-restart\t1\thud\tnow\t5").StartsWith("refused") && ApplyRestartNotice("gg-restart\t1\tgame-update\tlater\t5").StartsWith("refused")
                && ApplyRestartNotice("gg-restart\t1\tgame-update\tnow\tsoon").StartsWith("refused") && RestartDue == null);
            Check("...a CS2 update due at the match's end: taken", ApplyRestartNotice("gg-restart\t1\tgame-update\tmatch-end\t0\n").StartsWith("ok: game-update restart at the end of this match"));
            var h1 = HudState.Decode(BuildHud(h.Clock).Encode())!;
            Check("...the warning stands on the news line, and the live state has it in its parts: kind, when, no countdown yet (-1), the words - which tell players to restart CS2 afterwards",
                h1.News == "A CS2 update: the server restarts at the end of this match - Restart CS2 afterwards, and Steam updates it" && h1.RestartKind == "game-update" && h1.RestartWhen == "match-end"
                && h1.RestartIn == -1 && h1.RestartAt == 0 && h1.RestartWords == h1.News);
            Announce("P1 turned Vampire on", 5);
            var covered = BuildHud(h.Clock).FillLeft(BuildHud(h.Clock).News) == "P1 turned Vampire on";
            h.Run(6);
            Check("...a newer line covers it while it lasts, and it comes back", covered && BuildHud(h.Clock).FillLeft(BuildHud(h.Clock).News).StartsWith("A CS2 update"));
            h.Run(30);
            ClearNotices();
            Check("...the watcher's reminder changes nothing and says nothing new", ApplyRestartNotice("gg-restart\t1\tgame-update\tmatch-end\t0").StartsWith("ok") && Told.Count == 0 && RestartDue is { GoesAt: null });
            Check("...a vote may still be called before the match's end", CannotCall(h.Clock) == null);
            h.MatchEnd();
            var h2 = HudState.Decode(BuildHud(h.Clock).Encode())!;
            Check("At the match's end: no vote opens - the server goes down under it - nobody may call one, and the warning says in a moment until the watcher's countdown comes",
                Ballot == null && h2.RestartIn == 0 && h2.News == "A CS2 update: the server restarts in a moment - Restart CS2 afterwards, and Steam updates it" && CannotCall(h.Clock) is { } refusal && refusal.StartsWith("The server restarts now"));
            Check("...the watcher's countdown: seconds left and the moment it goes, on the news line and in the live state",
                ApplyRestartNotice("gg-restart\t1\tgame-update\tnow\t30").StartsWith("ok: game-update restart in 30 s") && HudState.Decode(BuildHud(h.Clock).Encode()) is { RestartIn: 30, RestartWhen: "now" } hn
                && hn.RestartAt == Unix(h.Clock) + 30 && hn.News == "A CS2 update: the server restarts in 30 s - Restart CS2 afterwards, and Steam updates it");
            h.Run(5);
            Check("...counting down, the news line with it", HudState.Decode(BuildHud(h.Clock).Encode())!.RestartIn == 25 && BuildHud(h.Clock).FillLeft(BuildHud(h.Clock).News).Contains("restarts in 25 s"));
            Check("...and in the change-driven text its words stand still while it counts down - the HUD fills the countdown in from the end time",
                BuildHud(h.Clock).Encode(endTimes: true) == BuildHud(h.Clock.AddSeconds(1)).Encode(endTimes: true)
                && HudState.Decode(BuildHud(h.Clock).Encode(endTimes: true))!.AsOf(HudState.Ms(h.Clock)).News.Contains("restarts in 25 s")
                && HudState.Decode(BuildHud(h.Clock).Encode(endTimes: true))!.AsOf(HudState.Ms(h.Clock.AddSeconds(30))).RestartWords.Contains("restarts in a moment")); // the news line itself ends at the restart's moment
            ApplyQuitNotice("gg-quit\t1\tupdate");
            Check("The quit's notice: the restart is happening - the warning goes", RestartDue == null && HudState.Decode(BuildHud(h.Clock).Encode())!.RestartKind == "");
            Check("After the reconnect: \"Updated to ...\" on the news line - CS2's new version from what was installed and what is now",
                ApplyUpdated("gg-updated\t1\tgame-update\t1.41.9.4\t1.41.9.5\n") == "ok: Updated to CS2 1.41.9.5 - Welcome back" && NewsNow.Text == "Updated to CS2 1.41.9.5 - Welcome back");
            ClearNotices();
            h.Run(60);
            h.Join(players[5]);
            Check($"...and every player who joins in the next {UpdatedNoticeMinutes} minutes hears it", h.Said("Updated to CS2 1.41.9.5"));
            h.Run(UpdatedNoticeMinutes * 60);
            ClearNotices(); h.Join(players[6]);
            Check("...not later", !h.Said("Updated to"));
            Check("An update of ours: back in a minute; afterwards no fingerprint on the screen; the words as sent work too; a refused gg-updated changes nothing",
                ApplyRestartNotice("gg-restart\t1\tour-update\tnow\t30").StartsWith("ok") && BuildHud(h.Clock).FillLeft(BuildHud(h.Clock).News) == "An Over Strike 2 update: the server restarts in 30 s - Back in a minute"
                && ApplyUpdated("gg-updated\t2\tour-update\tx").StartsWith("refused") && RestartDue != null
                && ApplyUpdated("gg-updated\t1\tour-update\t3fa9c2\t77be01") == "ok: Over Strike 2 is updated - Welcome back"
                && ApplyUpdated("gg-updated\t1\tgame-update\tCS2 1.41.9.6") == "ok: Updated to CS2 1.41.9.6 - Welcome back");
            Check("The watcher's words alone (core's BridgeWire): no other spelling of a kind or a when is taken",
                ApplyRestartNotice("gg-restart\t1\tours\tmatchend\t0").StartsWith("refused") && ApplyRestartNotice("gg-restart\t1\tupdate\tnow\t5").StartsWith("refused")
                && ApplyRestartNotice("gg-restart\t1\tour-update\tmatchend\t0").StartsWith("refused")
                && ApplyRestartNotice(BridgeWire.RestartBody(BridgeWire.OurUpdate, BridgeWire.AtMatchEnd, 0)).StartsWith("ok: our-update restart") && RestartDue is { Kind: "our-update", When: "match-end" });
            Check("A cancel takes it back, and says so", ApplyRestartNotice("gg-restart\t1\tcancel\tdaily").StartsWith("refused") && ApplyRestartNotice("gg-restart\t1\tcancel\tour-update") == "ok: cancelled"
                && RestartDue == null && NewsNow.Text.Contains("no restart after all"));
            StartVote(players[0].Item1, players[0].Item2);
            Check("A restart now while a vote is open drops the vote", Ballot is { Open: true } && ApplyRestartNotice("gg-restart\t1\tdaily\tnow\t45").StartsWith("ok") && Ballot == null
                && BuildHud(h.Clock).FillLeft(BuildHud(h.Clock).News).StartsWith("The daily restart: the server restarts in 45 s"));
            h.Run(RestartStaleMinutes * 60 + 60);
            Check($"A countdown {RestartStaleMinutes} minutes past its end without the server going down is over", RestartDue == null && BuildHud(h.Clock).RestartKind == "");
            Check("The live state's restart and background lines read back, and a state without them has none",
                HudState.Decode(new HudState { RestartKind = "downloads", RestartWhen = "now", RestartIn = 7, RestartAt = 1793000000, RestartWords = "x\ty", BgId = 3001, BgPercent = 42, BgTitle = "Lake" }.Encode())
                    is { RestartKind: "downloads", RestartWhen: "now", RestartIn: 7, RestartAt: 1793000000, RestartWords: "x y", BgId: 3001, BgPercent: 42, BgTitle: "Lake" }
                && HudState.Decode(new HudState().Encode()) is { RestartKind: "", RestartIn: -1, BgId: 0, BgPercent: -1 });

            // ---- the background download: the live state carries it, a load behind it waits longer ----
            Fresh();
            foreach (var p in players.Take(2)) h.Join(p);
            FillStock();
            var bgMap = LaneOf(Modes.ArmsRace)[0].Key;
            ApplyReady($"gg-ready\t1\ncurrent\t{bgMap}\t40\tA long title\n");
            var h3 = HudState.Decode(BuildHud(h.Clock).Encode())!;
            Check("While the watcher downloads in the background the live state says what and how far (bgdl), and nothing goes on the news line",
                h3.BgId.ToString() == bgMap && h3.BgPercent == 40 && h3.BgTitle == "A long title" && h3.News == "");
            Command(players[0].Item1, players[0].Item2, $"!map {ArgOf(Ar(250))}");
            Command(players[1].Item1, players[1].Item2, MapVote.Click(Ballot!.Round, Ballot.Options.Single(o => o.Map == Ar(250)).N));
            h.Run(ChangeDelay);
            Check("A change goes out while a background download runs: the hold keeps the watcher from starting another meanwhile", AnnouncePending && ApplyReady($"gg-ready\t1\ncurrent\t{bgMap}\t45\tA long title\n").EndsWith("; hold: a map is loading"));
            for (var t = 0; t < LoadMinutes * 60 + 30; t += 60) { h.Run(60); ApplyReady($"gg-ready\t1\ncurrent\t{bgMap}\t50\tA long title\n"); }
            Check($"...its load is not given up after {LoadMinutes} minutes when the watcher reported a background download it may wait behind", AnnouncePending);
            ApplyReady("gg-ready\t1\n");
            h.Run((LoadBehindDownloadMinutes - LoadMinutes - 3) * 60);
            Check($"...nor once that download is over: the map's own download follows it - up to {LoadBehindDownloadMinutes} minutes after the change went out", AnnouncePending);
            h.Run(4 * 60);
            Check("...then a load that still has not come is given up, as ever", !AnnouncePending);
            Fresh();
            foreach (var p in players.Take(2)) h.Join(p);
            Command(players[0].Item1, players[0].Item2, $"!map {ArgOf(Ar(250))}");
            Command(players[1].Item1, players[1].Item2, MapVote.Click(Ballot!.Round, Ballot.Options.Single(o => o.Map == Ar(250)).N));
            h.Run(ChangeDelay + LoadMinutes * 60 + 5);
            Check($"Without a background download a load that never comes is given up after {LoadMinutes} minutes, as before", !AnnouncePending);

            // ---- the words: How it works and Help say what the code does ----
            var how = string.Join(" ", Hud.How.SelectMany(r => r.Lines.Prepend(r.Title)));
            var odds = (int)Math.Round(1 / TooBigChance);
            var claims = new (string Words, bool True)[]
            {
                ($"a fresh map (a Workshop upload or update of the last {FreshDays} days)", FreshDays == 30), ("a surprise new to us", true), ("favourites the server keeps fill the page", MapVote.PageCards == HudMenu.Cards),
                ("Every card but a player's pick is on the server's disk", true), ("a small stock new to us", StockSize > 0), ("Random is the dice after the vote", true),
                ($"1 random pick in {odds} takes a too-big map", Math.Abs(odds * TooBigChance - 1) < 1e-9), ($"the other {odds - 1} one that is not; an empty group hands the pick to the other", true),
                ("measured XL, or marked Too big by a player", true), ($"gives up to {PopularityPoints:0} points", true), ($"each Like a point (up to {UpvoteCap} Likes)", true),
                ($"each Dislike or Too big takes {DislikePoints:0}", DislikePoints == 2 && TooBigVote < 0), ($"loses {ShownPoints:0} point, {ShownMaxPoints:0} at most, fading to nothing over {ShownFadeDays} days", true),
                ("each one doubles the odds", true), ($"the last {RecentCount} maps played (while others are left)", true), ("a map's download size never makes it more or less likely", true),
                ($"{MapVote.Seconds} s at the most", true), ($"{MapVote.WaitSeconds / 60} minutes before the next one can be called", MapVote.WaitSeconds % 60 == 0),
                ($"Wingman takes {Modes.Wingman.MaxPlayers} players at most", true), ("a second page only when needed", true), ("A risky map shows its record", true),
                ("putting it to the vote takes a second click", PickConfirmSeconds > 0), ("No votes: mid-match the map stays, at a match end Random", true), ("A Workshop update lifts a crash mark", true),
                ($"{ResetEmptyMinutes} minutes with nobody on", true),
            };
            var wrong = claims.Where(c => !how.Contains(c.Words) || !c.True).Select(c => c.Words).ToList();
            Check($"How it works says what the code does: {claims.Length} claims, each number the bridge's own constant{(wrong.Count > 0 ? " - wrong or missing: " + string.Join(" | ", wrong) : "")}", wrong.Count == 0);
            Check($"How it works fits its page: {Hud.How.Length} sections of at most {Hud.HowLines} lines of at most 160 characters (longest {Hud.How.SelectMany(r => r.Lines).Max(l => l.Length)}); no new-maps setting left in it or in Help",
                Hud.How.Length <= Hud.HowSections && Hud.How.All(r => r.Lines.Length is > 0 and <= Hud.HowLines && r.Title.Length <= 40) && Hud.How.SelectMany(r => r.Lines).All(l => l.Length <= 160)
                && !System.Text.RegularExpressions.Regex.IsMatch(how, @"![a-z]") && !how.Contains("New maps") && !how.Contains("#1017")
                && Hud.Help.SelectMany(g => g.Rows).All(r => !r.Command.Contains("New maps")));
            Check("The health page says what the bridge has of the Workshop's numbers, the kept maps, and restarts", HealthText().Contains("ratings: ") && HealthText().Contains("kept ") && HealthText().Contains("restart: "));

            // ---- a vote always ends: with no Keep card and no votes, whatever its timer runs into, whatever another step of the tick runs into ----
            Fresh();
            S.Map = null; S.LogName = null; // the bridge does not know which map is on
            h.Join(players[0]); h.Join(players[1]);
            Check("A vote called while no map is known to be on has no Keep card",
                OpenBallot(h.Clock, 2, players[0].Item2, players[0].Item1) && Ballot is { } noKeep && noKeep.Options.All(o => o.Source != VoteSource.Keep));
            h.Run(MapVote.Seconds - 1);
            Check("...nobody voting, it stays open until its limit", Ballot is { Open: true } && BuildVoteState(h.Clock).Freeze);
            h.Run(2);
            Check($"...and ends cleanly at {MapVote.Seconds} s: nobody frozen, no change, the next vote waits, and the words name no map",
                Ballot == null && !BuildVoteState(h.Clock).Freeze && Pending == null && VoteWaitUntil > h.Clock && h.Said("Nobody voted - The map stays"));

            Fresh();
            h.Join(players[0]); h.Join(players[1]);
            OpenBallot(h.Clock, 2);
            var roundShown = LiveVote().Round;
            Ballot!.Options[1].N = Ballot.Options[0].N; // two cards under one number: counting the votes at the vote's end throws
            QueueTickAt = h.Clock.AddSeconds(MapVote.Seconds); // the stock is due in the very tick the vote's limit is: a step after the vote's timer
            h.Run(MapVote.Seconds);
            Check("A vote whose timer throws is dropped: nobody stays frozen", Ballot == null && !BuildVoteState(h.Clock).Freeze);
            Check("...and the rest of that tick ran: the stock is looked at after the vote's timer, and the live state says no vote is on",
                roundShown > 0 && QueueTickAt > h.Clock && LiveVote().Round == 0);

            Fresh();
            h.Join(players[0]); h.Join(players[1]);
            OpenBallot(h.Clock, 2);
            var sendBefore = Send;
            Send = command => command.Contains("game_type") ? throw new InvalidOperationException("a step before the vote's timer fails") : sendBefore(command);
            (ModeProbeEnabled, ModeProbing, ModeProbeAt) = (true, true, default);
            h.Run(MapVote.Seconds + MapVote.KeptSeconds + 1);
            Send = sendBefore;
            (ModeProbeEnabled, ModeProbing) = (false, false);
            Check("A step of the tick that throws before the vote's timer costs that step only: the vote still ends at its limit", Ballot == null && h.Said("Nobody voted"));

            // ---- the board's Most liked names a map of any mode ----
            Fresh();
            var likedElsewhere = ModeMaps[Modes.DeathmatchId][0];
            Votes[Key(likedElsewhere)] = new() { [players[0].Item2] = 1 };
            Check("Most liked names a map only Deathmatch or Wingman lists by its name, not as \"workshop <id>\"", TopRows() is [{ Map: { Name: "dm_1" } }]);

            // ---- a plugin on a quiet server: its long poll counts when it arrives, and the bridge then holds it up to Hud.LongPollSeconds ----
            var realHudActive = h.BridgeHudActive;
            var (polledBefore, heldBefore) = (Interlocked.Read(ref hudPolledTicks), Interlocked.Exchange(ref hudHeldTicks, 0));
            void PolledAgo(double seconds) => Interlocked.Exchange(ref hudPolledTicks, DateTime.UtcNow.AddSeconds(-seconds).Ticks);
            PolledAgo(Hud.LongPollSeconds + 1);
            Check($"A plugin whose long poll has been held the whole {Hud.LongPollSeconds} s still counts as drawing the HUD", realHudActive());
            PolledAgo(Hud.LongPollSeconds + 20);
            Check("...one gone for longer than that and a margin does not", !realHudActive());
            Fresh();
            h.Join(players[0]); h.Join(players[1]);
            HudActive = realHudActive;
            PolledAgo(Hud.LongPollSeconds - 1);
            h.MatchEnd();
            HudActive = () => true;
            Interlocked.Exchange(ref hudPolledTicks, polledBefore); Interlocked.Exchange(ref hudHeldTicks, heldBefore);
            Check("...so a match end in a quiet stretch opens the vote, and the dice do not change the map without one", Ballot is { Open: true, MatchEnded: true } && Pending == null);
            EndBallot("the test goes on");

        }
        finally { h.Dispose(); }
    }
}
