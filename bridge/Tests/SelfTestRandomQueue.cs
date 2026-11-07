// --selftest, the disk and the discovery stock (RandomQueue.cs): what counts as on the server's disk, the stock's lanes - rolled from the maps
// new to us whose content the sync tested, half fresh, used up when shown or played, re-checked, kept in state.json - queues.json for the
// restart watcher, and its report with the background download. Runs in the harness (SelfTestHarness.cs), on its own lists and Workshop
// numbers.
using System.Text.Json;

static partial class Program
{
    static void RandomQueueSelfTest(Action<string, bool> Check)
    {
        var h = new Harness("rq", new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc));
        try
        {
            long Unix(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();
            // Arms Race: 1,100 maps, #1-#100 the Workshop's favourites (kept), #1050 and up uploaded in the last days (fresh), #500-#509 updated on the
            // Workshop since the sync tested them. Deathmatch and Wingman: 60 maps each, the first 30 kept.
            void Fresh() // a bridge that has just started (ResetAll), with no HUD plugin polling, on the lists below
            {
                h.Fresh();
                HudActive = () => false; StockSize = 10;
                Maps = Enumerable.Range(1, 1100).Select(r => new MapInfo(r, 8_000_000 + r, $"q_{r}", 10)).ToList();
                ModeMaps.Clear();
                ModeMaps[Modes.DeathmatchId] = Enumerable.Range(1, 60).Select(r => new MapInfo(r, 8_100_000 + r, $"dm_{r}", 10, false, Modes.DeathmatchId)).ToList();
                ModeMaps[Modes.WingmanId] = Enumerable.Range(1, 60).Select(r => new MapInfo(r, 8_200_000 + r, $"wm_{r}", 10, false, Modes.WingmanId)).ToList();
                var old = Unix(h.Clock.AddDays(-400));
                RatingScores = Maps.Take(100).Select((m, i) => (Key(m), (double?)(0.99 - i * 0.001), (long?)(100_000 - i)))
                    .Concat(ModeMaps.Values.SelectMany(l => l.Take(30)).Select((m, i) => (Key(m), (double?)0.9, (long?)(50_000 - i))))
                    .ToDictionary(x => x.Item1, x => (x.Item2, x.Item3));
                RatingTrend = new() { [Modes.ArmsRaceId] = Maps.Take(100).Select(Key).ToList() };
                RatingsFetched = Unix(h.Clock.AddHours(-3));
                WorkshopInfo = AllKnown.Where(m => !m.Official).Select(Key).Distinct().ToDictionary(k => k, k => new WorkshopEntry(old, old, "", 10, 20, 1, 2, 100, old));
                foreach (var m in Maps.Skip(1049)) WorkshopInfo[Key(m)] = WorkshopInfo[Key(m)] with { Created = Unix(h.Clock.AddDays(-3)), Updated = Unix(h.Clock.AddDays(-3)), Passed = Unix(h.Clock.AddDays(-3)) };
                foreach (var m in Maps.Skip(499).Take(10)) WorkshopInfo[Key(m)] = WorkshopInfo[Key(m)] with { Updated = Unix(h.Clock.AddDays(-1)) };
                NumbersStamp++;
            }
            MapInfo Ar(int rank) => Maps[rank - 1];
            List<StockEntry> Lane(Modes.Mode m) => LaneOf(m);

            // ---- the stock's lanes ----
            Fresh();
            FillStock();
            Check($"Stock: a lane per mode, each {StockSize} long, no map twice in one",
                Modes.All.All(m => Lane(m).Count == StockSize && Lane(m).Select(e => e.Key).Distinct().Count() == StockSize));
            Check("...each entry a listed map of its mode the keep set does not hold, never shown or played, its current content tested by the sync",
                Modes.All.All(m => Lane(m).All(e => PoolOf(m).Any(x => Key(x) == e.Key && !x.Official && !IsKept(x) && ContentPassed(x))))
                && Lane(Modes.ArmsRace).All(e => int.Parse(e.Key) - 8_000_000 > 100) && Lane(Modes.Deathmatch).All(e => int.Parse(e.Key) - 8_100_000 > 30));
            Check("...never one whose Workshop content changed since its last passing test (#500-#509 here)", Lane(Modes.ArmsRace).All(e => int.Parse(e.Key) - 8_000_000 is < 500 or > 509));
            Check($"...half of a lane fresh while there are fresh maps: {Lane(Modes.ArmsRace).Count(e => e.Fresh)} of Arms Race's {StockSize} are recent uploads (#1050 and up), the rest the long tail",
                Lane(Modes.ArmsRace).Count(e => e.Fresh) == (StockSize + 1) / 2 && Lane(Modes.ArmsRace).Where(e => e.Fresh).All(e => int.Parse(e.Key) - 8_000_000 >= 1050)
                && Lane(Modes.ArmsRace).Where(e => !e.Fresh).All(e => int.Parse(e.Key) - 8_000_000 < 1050) && Lane(Modes.Deathmatch).All(e => !e.Fresh));
            var text = JsonSerializer.Serialize(S, Ctx.Default.State);
            var back = JsonSerializer.Deserialize(text, Ctx.Default.State)!;
            Check("The stock lives in state.json and comes back as it was (a bridge restart keeps it), with what was shown and met",
                Modes.All.All(m => back.Stock[m.Id].SequenceEqual(S.Stock[m.Id])) && text.Contains("\"stock\"") && text.Contains("\"shown\"") && text.Contains("\"met\"")
                && JsonDocument.Parse(text).RootElement.TryGetProperty("randomQueues", out _) == false && JsonDocument.Parse(text).RootElement.TryGetProperty("newMaps", out _) == false);
            Check("...and a state.json from before it reads: its random queues and the new-maps setting are left aside",
                JsonSerializer.Deserialize("""{"newMaps": "half", "randomQueues": {"armsrace-new": [{"key": "1", "weight": 1, "big": false}]}, "roundMinutes": 60}""", Ctx.Default.State) is { Stock.Count: 0, RoundMinutes: 60 });

            // ---- queues.json: the watcher reads "requested" first, then the lanes under the four keys it and the keep set know ----
            var file = JsonSerializer.Deserialize(File.ReadAllText(QueuesPath), Ctx.Default.QueuesFile)!;
            Check("queues.json: version 1, the four keys the watcher and the keep set read - Arms Race's lane under both of its old keys - the length, a time, nothing requested without a vote",
                file.Version == 1 && file.Length == StockSize && file.Random.Keys.SequenceEqual(["armsrace-new", "armsrace-original", "deathmatch", "wingman"])
                && file.Random["armsrace-new"].SequenceEqual(Lane(Modes.ArmsRace).Select(e => e.Key)) && file.Random["armsrace-original"].SequenceEqual(file.Random["armsrace-new"])
                && file.Random["deathmatch"].SequenceEqual(Lane(Modes.Deathmatch).Select(e => e.Key)) && file.Random["wingman"].SequenceEqual(Lane(Modes.Wingman).Select(e => e.Key))
                && file.Random.Values.All(l => l.Count >= 10) && file.Written.EndsWith('Z') && file.Requested.Count == 0);
            var raw = File.ReadAllText(QueuesPath);
            Check("...in the names the watcher reads (\"random\", \"requested\", \"version\")", raw.Contains("\"random\"") && raw.Contains("\"requested\"") && raw.Contains("\"version\": 1"));

            // ---- the watcher's report ----
            var ar = Lane(Modes.ArmsRace).Select(e => e.Key).ToList();
            Check("A watcher's report: refused unless it is gg-ready 1", ApplyReady("hello\n1\n").StartsWith("refused") && ApplyReady("gg-ready\t2\n").StartsWith("refused"));
            var answer = ApplyReady($"gg-ready\t1\n{ar[2]}\n{ar[4]}\nnot-an-id\n");
            Check("...else every id it names is on the disk, the rest of what queues.json named is not, and no background download is said", answer.StartsWith("ok: 2 on disk")
                && Downloaded(ar[2]) && Downloaded(ar[4]) && !Downloaded(ar[0]) && ReportedMissing(ar[0]) && !ReportedMissing(Key(Ar(1))) && BackgroundNow() == null);
            ApplyReady($"gg-ready\t1\n{ar[2]}\ncurrent\t{ar[0]}\t63\tLake Aim\twith a tab\n");
            Check("...a current line is the background download: its id, its percent, its title with spaces (and a tab, made a space)",
                BackgroundNow() is { Percent: 63, Title: "Lake Aim with a tab" } bg && bg.Id == ar[0] && SyncWords.BackgroundWords(bg) == "Background download: Lake Aim with a tab, 63%"
                && QueueHealth().Contains("Background download: Lake Aim with a tab, 63%"));
            ApplyReady($"gg-ready\t1\ncurrent\t{ar[0]}\t-\t\n");
            Check("...a percent of '-' is none yet, an empty title the Workshop id; a percent over 100 is 100; no current line, nothing downloading",
                BackgroundNow() is { Percent: null } b2 && SyncWords.BackgroundWords(b2) == $"Background download: Workshop item {ar[0]}, starting"
                && ApplyReady($"gg-ready\t1\ncurrent\t{ar[0]}\t250\tx\n") != "" && BackgroundNow()?.Percent == 100
                && ApplyReady("gg-ready\t1\ncurrent\tnot-an-id\t5\tx\n") != "" && BackgroundNow() == null && ApplyReady("gg-ready\t1\n") != "" && BackgroundNow() == null);
            Check("The report's answer says hold while a map change counts down or loads, and nothing else - an open vote is no reason (its maps come first instead)",
                !ApplyReady("gg-ready\t1\n").Contains("hold") && (Pending = new Change(Maps[0], "test")) != null && ApplyReady("gg-ready\t1\n").EndsWith("; hold: a map change is counting down")
                && (Pending = null) == null && (AnnouncePending = true) && ApplyReady("gg-ready\t1\n").EndsWith("; hold: a map is loading") && !(AnnouncePending = false));
            ApplyReady($"gg-ready\t1\n{ar[2]}\ncurrent\t{ar[0]}\t10\tx\n");
            h.Clock = h.Clock.AddMinutes(ReadyFreshMinutes + 1);
            Check($"A report older than {ReadyFreshMinutes} minutes counts for nothing: no stock entry is on the disk, the background download is not said, the kept maps still count",
                !ReadyKnown && !Downloaded(ar[2]) && BackgroundNow() == null && KeptOnDisk(Ar(1)) && StockOnDisk(Modes.ArmsRace, null).Count == 0);

            // ---- the disk ----
            Fresh(); FillStock();
            Check($"On the disk without a report: an official map, a kept map inside the first {KeepTop - KeptMargin} of a list; not one past it, not a map nobody keeps, not a stock entry",
                KeptOnDisk(PoolOf(Modes.Deathmatch).First(m => m.Official)) && KeptOnDisk(Ar(1)) && KeptOnDisk(Ar(90)) && !KeptOnDisk(Ar(91)) && IsKept(Ar(100)) && !KeptOnDisk(Ar(300))
                && !IsKept(Ar(300)) && StockOnDisk(Modes.ArmsRace, null).Count == 0);
            Votes[Key(Ar(300))] = new() { ["[U:1:7]"] = 1 }; Ours = [Ar(301).Id];
            Check("...a liked map and our own map are kept and on the disk, whatever the lists say", IsKept(Ar(300)) && KeptOnDisk(Ar(300)) && IsKept(Ar(301)) && KeptOnDisk(Ar(301)));
            Votes = new(); Ours = new();
            WorkshopInfo[Key(Ar(5))] = WorkshopInfo[Key(Ar(5))] with { Updated = Unix(h.Clock.AddHours(-2)) }; NumbersStamp++;
            Check("...a kept map whose Workshop content changed since its tested version is not: loading it would download the update first", !KeptOnDisk(Ar(5)) && IsKept(Ar(5)));
            ExportQueues();
            var lane = Lane(Modes.ArmsRace).Select(e => e.Key).ToList();
            ApplyReady($"gg-ready\t1\n{lane[1]}\n{lane[3]}\n");
            Check("With a fresh report: the stock entries it names are on the disk, in their lane's order; the rest are not",
                StockOnDisk(Modes.ArmsRace, null).Select(Key).SequenceEqual([lane[1], lane[3]]) && StockWords().StartsWith("armsrace 2/10"));
            var plain = Maps.Skip(100).First(m => Modes.All.All(md => Lane(md).All(e => e.Key != Key(m)))); // kept by nobody, in no lane
            var savedRatings = (RatingScores, RatingTrend);
            (RatingScores, RatingTrend) = (new(), new()); NumbersStamp++;
            Check("Without ratings.json there is no keep set to go by: every eligible listed map counts as on the disk but one the report says is missing",
                !RatingsKnown && KeptOnDisk(plain) && KeptOnDisk(Ar(91)) && !KeptOnDisk(Maps.Single(m => Key(m) == lane[0])));
            (RatingScores, RatingTrend) = savedRatings; NumbersStamp++;

            // ---- taking: Random and the automatic changes take a map on the disk ----
            var takes = Enumerable.Range(0, 300).Select(_ => RandomNext()!).ToList();
            Check("Random and the automatic changes take only maps on the disk: kept ones and the stock's downloaded entries",
                takes.All(m => KeptOnDisk(m) || lane.Contains(Key(m))) && takes.Any(m => KeptOnDisk(m)));
            lane = Lane(Modes.ArmsRace).Select(e => e.Key).ToList(); // the lane as it is now: a take above may have used an entry up
            ExportQueues();
            ApplyReady($"gg-ready\t1\n{lane[1]}\n{lane[3]}\n");
            var all = Maps.ToList();
            foreach (var m in all.Take(100)) S.Crashes[Key(m)] = 1; // every kept map crashed: only the stock's two downloaded entries are left on the disk
            var taken = RandomNext();
            Check("...and a stock entry they take is used up at once: no longer new to us, gone from its lane, which rolls the next",
                taken != null && (Key(taken) == lane[1] || Key(taken) == lane[3]) && S.Met.Contains(Key(taken)) && Lane(Modes.ArmsRace).All(e => e.Key != Key(taken)) && Lane(Modes.ArmsRace).Count == StockSize);
            foreach (var m in all) S.Crashes[Key(m)] = 1; // nothing eligible at all
            S.Crashes.Remove(Key(plain));
            Check("Nothing on the disk eligible: the dice roll over every eligible map, so an automatic change never stops (the one case that may download)",
                RandomNext() == plain && (S.Crashes[Key(plain)] = 1) == 1 && RandomNext() is null);
            S.Crashes.Clear();

            // ---- used up, re-checked ----
            Fresh(); FillStock();
            var head = Lane(Modes.ArmsRace)[0].Key;
            S.Met.Add(head); FillStock();
            Check("A stock entry shown (or played) is used up: gone from its lane at the next look, which rolls a new one - never the same map", Lane(Modes.ArmsRace).All(e => e.Key != head) && Lane(Modes.ArmsRace).Count == StockSize);
            head = Lane(Modes.ArmsRace)[0].Key;
            Votes[head] = new() { ["[U:1:5]"] = AdsVote }; FillStock();
            Check("An entry a player marked as showing ads since its roll is dropped", Lane(Modes.ArmsRace).All(e => e.Key != head));
            head = Lane(Modes.ArmsRace)[0].Key;
            S.Crashes[head] = 1; FillStock();
            Check("An entry that crashed the server since its roll is dropped", Lane(Modes.ArmsRace).All(e => e.Key != head));
            head = Lane(Modes.ArmsRace)[0].Key;
            Sizes[head] = new SizeEntry(20000, "XL"); FillStock();
            Check("An entry whose too-big mark changed since its roll is dropped: the 1-in-100 group was not what rolled it (the map may come back only as a new roll of that group)", Lane(Modes.ArmsRace).All(e => e.Key != head || e.Big));
            Sizes = new();
            head = Lane(Modes.ArmsRace)[0].Key;
            WorkshopInfo[head] = WorkshopInfo[head] with { Updated = Unix(h.Clock) }; NumbersStamp++; FillStock();
            Check("An entry whose Workshop content changed since its roll is dropped until the sync has tested it", Lane(Modes.ArmsRace).All(e => e.Key != head));
            ExportQueues();
            var (stale, young) = (Lane(Modes.ArmsRace)[0], Lane(Modes.ArmsRace)[1]);
            Lane(Modes.ArmsRace)[0] = stale with { At = h.Clock.AddHours(-NotReadyDropHours - 1) };
            ApplyReady("gg-ready\t1\n");
            FillStock();
            Check($"An entry still not on the disk after {NotReadyDropHours} h, while the reports keep coming, is dropped; a younger one waits its turn",
                Lane(Modes.ArmsRace).All(e => e.Key != stale.Key) && Lane(Modes.ArmsRace).Any(e => e.Key == young.Key));
            ReadyAt = default;
            int kept = 0, trials = 2000;
            Fresh(); FillStock();
            var target = Lane(Modes.ArmsRace)[0];
            var targetMap = Maps.Single(m => Key(m) == target.Key);
            for (var i = 0; i < trials; i++)
            {
                Lane(Modes.ArmsRace).RemoveAll(e => e.Key == target.Key);
                Lane(Modes.ArmsRace).Insert(0, target with { Weight = Weight(targetMap) });
                Votes[target.Key] = new() { ["[U:1:6]"] = DownReasons.Plain }; // a dislike: a quarter of the odds
                FillStock();
                if (Lane(Modes.ArmsRace).Any(e => e.Key == target.Key)) kept++;
                Votes = new();
            }
            Check($"A dislike since its roll keeps an entry with the odds it left it: {kept} of {trials} kept, a quarter expected", kept is > 420 and < 580);

            // ---- the stock turned off ----
            Fresh(); StockSize = 0; FillStock();
            file = JsonSerializer.Deserialize(File.ReadAllText(QueuesPath), Ctx.Default.QueuesFile)!;
            Check("DISCOVERY_STOCK 0 turns the stock off: no lane holds anything, queues.json's lanes are empty (and the keep set then deletes nothing)",
                Modes.All.All(m => Lane(m).Count == 0) && file.Random.Values.All(l => l.Count == 0) && file.Length == 0);
            StockSize = 10;

            // ---- the open ballot's maps go first in queues.json: the watcher starts on them the moment the vote opens ----
            Fresh(); FillStock();
            ApplyReady($"gg-ready\t1\n{string.Join("\n", Lane(Modes.ArmsRace).Select(e => e.Key))}\n");
            Check($"A vote opens with a page of {MapVote.PageCards} cards (no map known to be on: no Keep card)", OpenBallot(h.Clock, 2) && Ballot is { } b && b.Options.Count(o => o.Source == VoteSource.Rolled) == MapVote.PageCards);
            var rolled = Ballot!.Options.Where(o => o.Map is { Official: false }).ToList();
            var voted = rolled.Skip(2).First();
            Ballot.Choices["[U:1:9]"] = voted.N;
            ExportQueues();
            file = JsonSerializer.Deserialize(File.ReadAllText(QueuesPath), Ctx.Default.QueuesFile)!;
            Check($"queues.json names the open ballot's {rolled.Count} Workshop maps as 'requested' (the watcher fetches those first), the one with votes first; no hold for the vote",
                file.Requested.Count == rolled.Count && file.Requested[0] == Key(voted.Map!) && rolled.All(o => file.Requested.Contains(Key(o.Map!))) && HoldWhy() == null);
            Ballot = null;
            ExportQueues();
            file = JsonSerializer.Deserialize(File.ReadAllText(QueuesPath), Ctx.Default.QueuesFile)!;
            Check("...and nothing once the vote is gone", file.Requested.Count == 0);
            Check("The health page says how many of each lane are on disk", QueueHealth().StartsWith($"stock of {StockSize} per mode: ") && QueueHealth().Contains("armsrace ") && QueueHealth().Contains(" s ago"));
            ReadyAt = default;
            Check("...and that no report came yet", QueueHealth().Contains("no report from the restart watcher yet"));
        }
        finally { h.Dispose(); }
    }
}
