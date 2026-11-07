// What is on the server's disk, and the discovery stock that brings maps new to us onto it - so every card a ballot shows but a player's own
// pick, its Random and every automatic change load at once, however fast votes follow each other.
//
//  - On the disk: an official map always; a kept map (Popularity.cs: the keep set, which the server stores); a stock entry the restart watcher
//    reports (POST /ready). The ballot's favourites and other modes' cards are kept maps on the disk; its fresh card a fresh one of those or of
//    the stock's; its surprise the stock's; Random and the automatic changes any of them (RandomNext). Only when none is left do the dice roll
//    over every eligible map (RandomPick) - the one case that can still download.
//  - The stock: per mode a lane of StockSize (10; DISCOVERY_STOCK sets 0 to 40, 0 turns it off) kept in state.json. Each entry is rolled with
//    the dice of a random pick (RollIn: the too-big group 1 time in 100, the last maps played left out, weighted by the points) from the mode's
//    listed maps the keep set does not hold, never shown on a ballot or played here (S.Met), whose current Workshop content passed the sync's
//    test (workshop.json) - half of a lane fresh while there are fresh ones, the rest the long tail. Never a map twice in a lane.
//  - An entry is used up once it is shown or played (S.Met: a surprise is a surprise once), and its lane rolls the next one at once, which the
//    watcher downloads between votes. An entry no longer eligible, on its list, tested as it is now, or with its too-big mark changed is dropped;
//    so is one not on the disk after NotReadyDropHours while the reports keep coming; one whose votes went down since its roll stays with the
//    odds the vote left it (kept with probability weight now / weight then, once per drop).
//  - queues.json (QueuesFile, version 1), for the watcher's background downloads, os2-server organize and the Workshop sync: "requested", the Workshop
//    maps on the open ballot, the most votes first - the watcher downloads those before anything else; "random", the lanes under the keys the watcher
//    and the keep set read: Arms Race's under both "armsrace-new" and "armsrace-original" (the keep set wants each of its four at least 10 long),
//    "deathmatch" and "wingman". Written atomically, only on change.
//  - The watcher's report (POST /ready/<secret>, BridgeWire.ReadyBody): "gg-ready\t1", one id a line of the maps queues.json names that are on the disk, and - while a background download
//    downloads - "current\t<id>\t<percent>\t<title>" (a whole percent 0-100, or "-" before any progress; the title last, the rest of the line): the
//    background download, which the Sync page and the website show. No fresh report for ReadyFreshMinutes: no stock entry counts as on the disk,
//    the kept maps still do. Its answer says "; hold: <why>" while a change counts down or a map loads - Steam's client runs one Workshop download
//    at a time and one already running cannot be stopped - so the map players wait for is never queued behind a background one that started then.
using System.Globalization;
using System.Net;
using System.Text.Json;

static partial class Program
{
    /// <summary>Entries per lane of the stock: bridge.discoveryStock (DISCOVERY_STOCK without the settings file), 0 to 40, 10 when unset; 0 turns the stock off (the surprise is then a favourite).
    /// The keep set wants each of queues.json's four lanes at least 10 long before it deletes anything, so below 10 nothing is deleted.</summary>
    public static int StockSize = BridgeConfig.Values.DiscoveryStock; // bridge.discoveryStock in the settings file

    /// <summary>The keys of queues.json's "random", each with the mode whose lane it carries: the keep set's four (core's KeepSet.QueueNames), which
    /// the watcher reads too, each named for its mode before any "-".</summary>
    public static readonly (string Key, string Mode)[] QueueKeys = [.. OverStrike2.Core.KeepSet.QueueNames.Select(name => (name, name.Split('-')[0]))];

    public const int ReadyFreshMinutes = 5;  // the watcher's report counts this long; it sends one at least every 2 minutes
    public const int NotReadyDropHours = 2;  // an entry that has not reached the disk in this long, while the reports keep coming, is dropped
    static string QueuesPath => Path.Combine(StateDir, "queues.json");

    static HashSet<string> ReadyIds = new();    // the maps queues.json names that are on the server's disk, by the watcher's last report
    static HashSet<string> CoveredIds = new();  // ...and the maps queues.json named when that report came: the report says nothing of the rest
    static DateTime ReadyAt;                    // when that report came (the bridge's clock)
    static HashSet<string> ExportedIds = new(); // the maps queues.json names now
    static string QueuesWritten = "";           // queues.json as last written, without its time: written again only when it changes
    static DateTime QueueTickAt;

    /// <summary>The background download running now, by the watcher's last report: the map, its progress (null before any) and its title.</summary>
    public sealed record Download(string Id, int? Percent, string Title);
    static Download? Background;

    static List<StockEntry> LaneOf(Modes.Mode mode) => S.Stock.TryGetValue(mode.Id, out var l) ? l : S.Stock[mode.Id] = new();

    static bool ReadyKnown => ReadyAt != default && (Now() - ReadyAt).TotalMinutes < ReadyFreshMinutes;

    /// <summary>The watcher looked for this map (queues.json named it) and did not find it on the disk.</summary>
    static bool ReportedMissing(string key) => ReadyKnown && CoveredIds.Contains(key) && !ReadyIds.Contains(key);

    /// <summary>A stock entry on the server's disk: the watcher's fresh report names it.</summary>
    static bool Downloaded(string key) => ReadyKnown && ReadyIds.Contains(key);

    /// <summary>The background download now, while the report that told of it is fresh.</summary>
    static Download? BackgroundNow() => ReadyKnown ? Background : null;

    // ---- the stock ----

    /// <summary>What a stock entry may be: a listed Workshop map of the mode, eligible, not kept, never shown or played here, its current content
    /// tested by the sync, not in the lane already.</summary>
    static bool StockCandidate(MapInfo m, ISet<string> have) =>
        !m.Official && !have.Contains(Key(m)) && CanRoll(m) && !S.Met.Contains(Key(m)) && ContentPassed(m) && !IsKept(m);

    /// <summary>One roll onto the end of a lane, with the dice of a random pick: fresh while fewer than half of the lane are, then the long tail.
    /// False when nothing is left to roll.</summary>
    static bool RollIntoStock(Modes.Mode mode)
    {
        var lane = LaneOf(mode);
        var have = lane.Select(e => e.Key).ToHashSet();
        var all = PoolOf(mode).Where(m => StockCandidate(m, have)).ToList();
        if (all.Count == 0) return false;
        var fresh = all.Where(IsFresh).ToList();
        var wantFresh = fresh.Count > 0 && lane.Count(e => e.Fresh) < (StockSize + 1) / 2;
        var list = wantFresh ? fresh : all.Where(m => !IsFresh(m)).ToList();
        if (list.Count == 0) list = all;
        if (RollIn(list) is not { } r) return false;
        lane.Add(new StockEntry(Key(r.Pick), Weight(r.Pick), TooBig(r.Pick), IsFresh(r.Pick), Now()));
        if (!SelfTesting) Log($"stock {mode.Id}: rolled {Label(r.Pick)} ({(IsFresh(r.Pick) ? "fresh" : "the long tail")}, {(r.Big ? "too big" : "not too big")}, {r.Candidates} candidates) as #{lane.Count} of {StockSize}");
        return true;
    }

    /// <summary>Why a stock entry goes, or null while it may stay.</summary>
    static string? StockDrop(StockEntry e, MapInfo? map, DateTime now)
    {
        if (map == null) return "it is no longer on its list";
        if (!CanRoll(map)) return "it is no longer eligible (CanRoll)";
        if (S.Met.Contains(e.Key)) return "it was shown or played - a surprise is one once";
        if (TooBig(map) != e.Big) return "its too-big mark changed since it was rolled";
        if (!ContentPassed(map)) return "its Workshop content changed, and the sync has not tested it yet";
        if (ReadyKnown && !Downloaded(e.Key) && (now - e.At).TotalHours > NotReadyDropHours) return $"it has not reached the server's disk in {NotReadyDropHours} h";
        return null;
    }

    /// <summary>Every lane checked - what may not stay goes - and topped up to StockSize; queues.json written when it changed.</summary>
    static void FillStock()
    {
        var changed = false;
        var now = Now();
        foreach (var mode in Modes.All)
        {
            var lane = LaneOf(mode);
            var pool = PoolOf(mode).GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
            for (var i = 0; i < lane.Count; i++)
            {
                var e = lane[i];
                var map = pool.GetValueOrDefault(e.Key);
                var why = StockDrop(e, map, now);
                if (why == null && map != null && Weight(map) < e.Weight)
                {
                    if (Dice.NextDouble() * e.Weight >= Weight(map)) why = "its votes went down since it was rolled, and the dice took it at its new odds";
                    else { lane[i] = e with { Weight = Weight(map) }; changed = true; } // judged once per drop of its odds
                }
                if (why == null) continue;
                if (!SelfTesting) Log($"stock {mode.Id}: {(map != null ? Label(map) : e.Key)} goes - {why}");
                lane.RemoveAt(i--);
                changed = true;
            }
            while (lane.Count > StockSize) { lane.RemoveAt(lane.Count - 1); changed = true; }
            while (lane.Count < StockSize && RollIntoStock(mode)) changed = true;
        }
        if (changed) Save();
        ExportQueues();
    }

    /// <summary>The stock entries of a mode on the server's disk, in their lane's order, as maps - eligible, not excluded.</summary>
    static List<MapInfo> StockOnDisk(Modes.Mode mode, ISet<string>? exclude)
    {
        var pool = PoolOf(mode).GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        var now = Now();
        return LaneOf(mode).Where(e => Downloaded(e.Key) && exclude?.Contains(e.Key) != true && pool.ContainsKey(e.Key) && StockDrop(e, pool[e.Key], now) == null)
            .Select(e => pool[e.Key]).ToList();
    }

    static bool InStock(MapInfo m) => LaneOf(ModeOf(m)).Any(e => e.Key == Key(m));

    // ---- the dice over what is on the disk ----

    /// <summary>A mode's maps on the server's disk that a random pick may take: the kept ones, and the stock's downloaded entries.</summary>
    static List<MapInfo> OnDiskPool(Modes.Mode mode, ISet<string>? exclude)
    {
        var kept = PoolOf(mode).Where(m => CanRoll(m, exclude) && KeptOnDisk(m)).ToList();
        var have = kept.Select(Key).ToHashSet();
        return kept.Concat(StockOnDisk(mode, exclude).Where(m => CanRoll(m, exclude) && have.Add(Key(m)))).ToList();
    }

    /// <summary>A random pick - RandomPick's contract: the mode the server is in unless asked for another, null when nothing is eligible - from the
    /// maps on the server's disk (OnDiskPool). A stock entry it takes is used up. Nothing on the disk eligible: RandomPick over every eligible map.
    /// Every caller that rolls the dice calls this: Random, a card that became unplayable, and the automatic changes.</summary>
    static MapInfo? RandomNext(Modes.Mode? inMode = null, ISet<string>? exclude = null)
    {
        inMode ??= CurrentMode;
        var pool = OnDiskPool(inMode, exclude);
        if (RollIn(pool) is not { } r)
        {
            Log($"dice: nothing of {inMode.Name} on the server's disk is eligible - the dice roll over every eligible map");
            return RandomPick(inMode, exclude);
        }
        var stock = InStock(r.Pick);
        if (!SelfTesting) Log($"dice: {inMode.Name}, {r.Candidates} candidates on the disk ({(r.Big ? "too big" : "not too big")}) -> {Label(r.Pick)}{(stock ? ", from the stock" : "")}");
        if (stock) { S.Met.Add(Key(r.Pick)); CatalogVersion++; FillStock(); } // used up: its lane rolls the next at once
        return r.Pick;
    }

    /// <summary>From Tick: the stock checked, topped up and queues.json refreshed every 10 s.</summary>
    static void QueueTick(DateTime now)
    {
        if (now < QueueTickAt) return;
        QueueTickAt = now.AddSeconds(10);
        FillStock();
    }

    // ---- queues.json ----

    /// <summary>/data/queues.json: the Workshop maps on the open ballot as "requested", the most votes first - the watcher downloads those first, so they
    /// start downloading the moment the vote opens - and the stock's lanes as "random", in the order the watcher takes them. Version 1; written atomically,
    /// only on change.</summary>
    static void ExportQueues()
    {
        var file = new QueuesFile
        {
            Version = 1, Length = StockSize,
            Random = QueueKeys.ToDictionary(q => q.Key, q => LaneOf(Modes.Of(q.Mode)).Select(e => e.Key).ToList()),
            Requested = BallotMaps().Select(Key).Distinct().ToList(),
        };
        ExportedIds = file.Requested.Concat(file.Random.Values.SelectMany(v => v)).ToHashSet();
        var text = JsonSerializer.Serialize(file, Ctx.Default.QueuesFile);
        if (text == QueuesWritten) return;
        file.Written = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        try
        {
            WriteAtomic(QueuesPath, JsonSerializer.Serialize(file, Ctx.Default.QueuesFile));
            QueuesWritten = text;
        }
        catch (Exception e) { Log($"queue: {QueuesPath} not written ({Clip(e.Message, 120)})"); }
    }

    /// <summary>The Workshop maps on the open ballot, the most votes first: what queues.json asks the watcher for before anything else.</summary>
    static List<MapInfo> BallotMaps() => Ballot is not { Open: true } b ? []
        : b.Options.Where(o => o.Source is VoteSource.Picked or VoteSource.Rolled && o.Map is { Official: false })
            .OrderByDescending(o => b.Choices.Values.Count(n => n == o.N)).ThenBy(o => o.Order).Select(o => o.Map!).ToList();

    // ---- the watcher's report ----

    /// <summary>POST /ready/&lt;secret&gt;: the restart watcher's report (the file's summary). Runs on the event loop.</summary>
    static void ReadyReport(HttpListenerContext c) => OnLoop(c, ApplyReady, "ready");

    /// <summary>A "current" line's download: the id, a whole percent 0-100 or "-", and the title - the rest of the line, tabs and all.</summary>
    static Download? ParseCurrent(string line, DateTime now)
    {
        var f = line.Split('\t', 4);
        if (f.Length < 3 || f[0] != BridgeWire.Current || f[1].Trim() is not { Length: > 0 } id || !id.All(char.IsAsciiDigit)) return null;
        int? percent = int.TryParse(f[2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? Math.Clamp(p, 0, 100) : null;
        return new Download(id, percent, Hud.Clean(f.Length > 3 ? f[3] : "", 60));
    }

    static string ApplyReady(string body)
    {
        var lines = body.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines[0].Trim() != BridgeWire.Head(BridgeWire.ReadyWord)) return $"refused: not a {BridgeWire.ReadyWord} {BridgeWire.Version} report";
        var now = Now();
        var ids = lines.Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0 && l.All(char.IsAsciiDigit)).ToHashSet();
        var current = lines.Skip(1).Select(l => ParseCurrent(l, now)).FirstOrDefault(d => d != null);
        var news = ReadyAt == default || !ids.SetEquals(ReadyIds);
        if (current?.Id != Background?.Id && !SelfTesting) Log(current == null ? "ready: no background download any more" : $"ready: the background download is {current.Id} ({current.Title})");
        (ReadyIds, CoveredIds, ReadyAt, Background) = (ids, ExportedIds.ToHashSet(), now, current);
        if (news && !SelfTesting) Log($"ready: the restart watcher reports {ids.Count} map(s) on the server's disk - {StockWords()}");
        return $"ok: {ids.Count} on disk - {StockWords()}" + (HoldWhy() is { } why ? $"; hold: {why}" : "");
    }

    /// <summary>Why the watcher should start no background download now, or null: the map a player waits for would queue behind it. Steam's client
    /// runs one Workshop download at a time - measured on a test copy on 2026-11-03: a map change's 449 MB started only when the 350 MB in the
    /// background had ended, 8 s later - and one already running cannot be stopped, so the watcher asks right before it starts one. A change
    /// counting down, a map loading (it may be downloading). An open vote is no reason: its maps are what the watcher fetches first.</summary>
    static string? HoldWhy() => Pending != null ? "a map change is counting down" : AnnouncePending ? "a map is loading" : null;

    /// <summary>"armsrace 7/10, deathmatch 10/10, wingman 9/10" - each lane's entries on the disk of its entries.</summary>
    static string StockWords() => string.Join(", ", Modes.All.Select(m => $"{m.Id} {LaneOf(m).Count(e => Downloaded(e.Key))}/{LaneOf(m).Count}"));

    /// <summary>The health page's line about the disk and the stock.</summary>
    static string QueueHealth() => $"stock of {StockSize} per mode: " + (ReadyAt == default ? string.Join(", ", Modes.All.Select(m => $"{m.Id} {LaneOf(m).Count}")) + "; no report from the restart watcher yet"
        : StockWords() + $" on disk by the watcher's report {(Now() - ReadyAt).TotalSeconds:0} s ago{(ReadyKnown ? "" : " (stale: no stock entry counts as on the disk)")}")
        + $"; on the open ballot {BallotMaps().Count}" + (BackgroundNow() is { } d ? $"; {SyncWords.BackgroundWords(d)} ({d.Id})" : "");
}

/// <summary>One entry of a stock lane (state.json): the map's key, its weight and too-big mark when it was rolled, whether it was fresh, and when.</summary>
sealed record StockEntry(string Key, double Weight, bool Big, bool Fresh, DateTime At);

/// <summary>/data/queues.json, version 1 (RandomQueue.cs): the stock's lanes by the four keys the watcher and the keep set read - Workshop ids - and
/// the Workshop ids on the open ballot ("requested", the name the watcher reads first).</summary>
sealed class QueuesFile
{
    public int Version { get; set; }
    public string Written { get; set; } = "";
    public int Length { get; set; }
    public Dictionary<string, List<string>> Random { get; set; } = new();
    public List<string> Requested { get; set; } = new();
}
