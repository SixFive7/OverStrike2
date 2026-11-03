// The random queues (the owner's request of 2026-11-03): the dice are rolled ahead, so the maps random picks will take are on the
// server's disk before anybody asks - "next next next" past a few maps nobody likes never waits for a download. The rules of a random
// pick do not change (Selection.cs, Votes.cs: RandomPick, CanRoll, RollIn); only when the dice are rolled does.
//
//  - Four queues, kept in state.json so they survive a bridge restart: Arms Race's new maps (#FirstNewRank and up), its original list
//    (#1 to #FirstNewRank - 1), Deathmatch's pool and Wingman's. Each holds QueueLength entries, rolled one at a time with exactly the
//    dice of a random pick on its own list (RollIn: the too-big group 1 time in 100, the last maps played left out, weighted by Weight),
//    never a map already in it. Arms Race has two because the new-maps setting chooses between those lists at every pick: always takes
//    from the new maps' queue, never from the original's, half flips the fair coin RandomPick flips. A setting changed, or reset when the
//    server empties, so needs no new rolls, and the maps it brings are on disk already.
//  - A random pick takes from its mode's queue (RandomNext): the first entry still eligible - CanRoll, not played since it was rolled, its
//    too-big mark as it was - that the restart watcher reports on the server's disk (POST /ready, update-watch.py job 9; an official map
//    always is). No report for ReadyFreshMinutes: every entry counts as on disk, as before the queues. An entry no longer eligible is
//    dropped; one whose votes went down since its roll stays with the odds the vote left it (kept with probability Weight now / Weight
//    then, once per drop), so a downvote counts as it does on the dice. Nothing on disk: the first eligible entry anyway (the server
//    downloads it, as it always did). No eligible entry on the list the setting chose: the other Arms Race list, with RandomPick's note.
//    No queue at all: RandomPick itself.
//  - What a pick takes leaves the queue, and the queue rolls its next entry at once. The vote's random options (VoteRound.cs) are taken
//    the same way, shown or chosen alike, so a map shown in a vote and not chosen comes back no sooner than the dice would bring it; the
//    vote's Random takes the next entry after them.
//  - The queues and the players' requests go to /data/queues.json (QueuesFile) for the restart watcher, which downloads what is not on
//    disk - in the background, one map at a time, silently - and for update-watch.py --organize and the Workshop sync, which keep them.
using System.Net;
using System.Text;
using System.Text.Json;

static partial class Program
{
    /// <summary>Entries per queue (RANDOM_QUEUE, 10-40): at least 10 on disk per mode while a vote's 3-5 options and a few Nexts are taken.</summary>
    public static readonly int QueueLength = Math.Clamp(int.TryParse(Env("RANDOM_QUEUE", "15"), out var n) ? n : 15, 10, 40);
    public const string QueueNew = "armsrace-new", QueueOriginal = "armsrace-original";
    public static readonly string[] QueueNames = [QueueNew, QueueOriginal, Modes.DeathmatchId, Modes.WingmanId];
    public const int ReadyFreshMinutes = 5;  // the watcher's report counts this long; it sends one at least every 2 minutes
    public const int NotReadyDropHours = 2;  // an entry that has not reached the disk in this long, while the reports keep coming, is dropped
    const string ReadyPath = "/ready/";
    static string QueuesPath => Path.Combine(StateDir, "queues.json");

    static HashSet<string> ReadyIds = new(); // the queued maps on the server's disk, by the watcher's last report
    static DateTime ReadyAt;                 // when that report came (the bridge's clock)
    static string QueuesWritten = "";        // queues.json as last written, without its time: written again only when it changes
    static DateTime QueueTickAt;

    static List<RolledMap> QueueOf(string name) => S.RandomQueues.TryGetValue(name, out var q) ? q : S.RandomQueues[name] = new();

    static bool ReadyKnown => ReadyAt != default && (Now() - ReadyAt).TotalMinutes < ReadyFreshMinutes;

    /// <summary>On the server's disk, as far as the bridge knows: an official map always, a Workshop map when the watcher's fresh report
    /// names it, every map when there is no fresh report (the bridge then behaves as it did before the queues).</summary>
    static bool IsReady(MapInfo m) => m.Official || !ReadyKnown || ReadyIds.Contains(Key(m));

    /// <summary>A queue's list as it is now, by map key.</summary>
    static IEnumerable<MapInfo> QueuePool(string name) => name switch
    {
        QueueNew => Maps.Where(IsNew),
        QueueOriginal => Maps.Where(m => !IsNew(m)),
        _ => PoolOf(Modes.Of(name)),
    };

    /// <summary>One roll onto the end of a queue, with the dice of a random pick on the queue's own list, never a map already in it.
    /// False when nothing on the list is eligible.</summary>
    static bool RollInto(string name)
    {
        var q = QueueOf(name);
        var have = q.Select(e => e.Key).ToHashSet();
        var list = QueuePool(name).Where(m => CanRoll(m, have)).ToList();
        var bigFirst = Dice.NextDouble() < TooBigChance;
        if (RollIn(list, bigFirst) is not { } r) return false;
        q.Add(new RolledMap(Key(r.Pick), Weight(r.Pick), TooBig(r.Pick), Now()));
        if (!SelfTesting) Log($"queue {name}: rolled {Label(r.Pick)} ({(r.Big ? "too big" : "not too big")}{(r.Big != bigFirst ? ", the other group was empty" : "")}, "
            + $"{r.Candidates} candidates) as #{q.Count} of {QueueLength}");
        return true;
    }

    /// <summary>Every queue topped up to QueueLength; queues.json written when it changed. Cheap when they are full.</summary>
    static void FillQueues()
    {
        var rolled = false;
        foreach (var name in QueueNames)
            while (QueueOf(name).Count < QueueLength && RollInto(name)) rolled = true;
        if (rolled) Save();
        ExportQueues();
    }

    /// <summary>The first entry of a queue that may be played now - eligible, not in <paramref name="exclude"/>, and on disk when
    /// <paramref name="readyOnly"/> - taken out of the queue. Entries no longer eligible are dropped on the way, and stay dropped.</summary>
    static MapInfo? TakeFrom(string name, ISet<string>? exclude, bool readyOnly)
    {
        var q = QueueOf(name);
        var pool = QueuePool(name).GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        var recent = S.History.TakeLast(RecentCount).Select(Key).ToHashSet();
        var now = Now();
        var changed = false;
        MapInfo? pick = null;
        for (var i = 0; i < q.Count && pick == null; i++)
        {
            var e = q[i];
            var map = pool.GetValueOrDefault(e.Key);
            var why = map == null ? "it is no longer on its list"
                : !CanRoll(map) ? "it is no longer eligible (CanRoll)"
                : recent.Contains(e.Key) ? "it was played since it was rolled"
                : TooBig(map) != e.Big ? "its too-big mark changed since it was rolled"
                : ReadyKnown && !IsReady(map) && (now - e.At).TotalHours > NotReadyDropHours ? $"it has not reached the server's disk in {NotReadyDropHours} h"
                : null;
            if (why == null && map != null && Weight(map) < e.Weight)
            {
                if (Dice.NextDouble() * e.Weight >= Weight(map)) why = "its votes went down since it was rolled, and the dice took it at its new odds";
                else { q[i] = e with { Weight = Weight(map) }; changed = true; } // judged once per drop of its odds
            }
            if (why != null)
            {
                if (!SelfTesting) Log($"queue {name}: {(map != null ? Label(map) : e.Key)} dropped - {why}");
                q.RemoveAt(i--);
                changed = true;
                continue;
            }
            if (exclude?.Contains(e.Key) == true || (readyOnly && !IsReady(map!))) continue; // stays for a later pick
            q.RemoveAt(i);
            pick = map;
            changed = true;
        }
        if (changed) Save();
        return pick;
    }

    /// <summary>A queue's next pick: an entry on disk, else any eligible entry, else - when the queue ran dry on drops - a fresh roll onto
    /// it and that.</summary>
    static MapInfo? TakeNext(string name, ISet<string>? exclude) =>
        TakeFrom(name, exclude, readyOnly: true) ?? TakeFrom(name, exclude, readyOnly: false)
        ?? (RollInto(name) ? TakeFrom(name, exclude, readyOnly: false) : null);

    /// <summary>A random pick, from the queues: RandomPick's contract - null when switched off or nobody is on; the mode the server is in
    /// unless asked for another; RollNote set as RandomPick sets it - with the map taken from the queue of its mode, and RandomPick itself
    /// when no queue has an eligible entry. Every caller that used to roll the dice calls this.</summary>
    static MapInfo? RandomNext(bool enabled, int humans, Modes.Mode? inMode = null, ISet<string>? exclude = null)
    {
        RollNote = "";
        if (!enabled || humans <= 0) return null;
        inMode ??= CurrentMode;
        MapInfo? pick = null;
        var from = inMode.Id;
        if (inMode.ArmsRace)
        {
            var setting = NewMapsMode;
            var newFirst = setting == NewMapsNever ? false : setting != NewMapsHalf || Dice.NextDouble() < 0.5; // RandomPick's coin
            foreach (var newer in new[] { newFirst, !newFirst })
            {
                if ((pick = TakeNext(newer ? QueueNew : QueueOriginal, exclude)) == null) continue;
                from = newer ? QueueNew : QueueOriginal;
                if (newer != newFirst) RollNote = newFirst ? NoNewMapsNote : NoOriginalMapsNote;
                break;
            }
        }
        else pick = TakeNext(inMode.Id, exclude);
        if (pick == null)
        {
            Log($"queue: no entry of the {inMode.Name} queue{(inMode.ArmsRace ? "s" : "")} is eligible - the dice roll live");
            FillQueues();
            return RandomPick(enabled, humans, inMode, exclude);
        }
        if (!SelfTesting)
            Log($"queue {from}: took {Label(pick)} - {(pick.Official ? "an official map" : !ReadyKnown ? "no fresh report from the restart watcher" : ReadyIds.Contains(Key(pick)) ? "on the server's disk" : "NOT on the server's disk yet, it downloads")}"
                + $"{(inMode.ArmsRace ? $" (new maps {NewMapsMode})" : "")}; {QueueOf(from).Count} left, rolling the next");
        FillQueues(); // the entry taken is rolled anew at once, so the watcher can fetch it in the background
        return pick;
    }

    /// <summary>From Tick: the queues topped up and queues.json refreshed every 10 s (the players' requests change it too).</summary>
    static void QueueTick(DateTime now)
    {
        if (now < QueueTickAt) return;
        QueueTickAt = now.AddSeconds(10);
        FillQueues();
    }

    /// <summary>/data/queues.json: the four queues' keys in order and the requested maps, for the restart watcher (job 9 downloads what
    /// is not on disk), update-watch.py --organize and the sync (they keep these maps). Version 1; written atomically, only on change.</summary>
    static void ExportQueues()
    {
        var file = new QueuesFile
        {
            Version = 1, Length = QueueLength,
            Random = QueueNames.ToDictionary(n => n, n => QueueOf(n).Select(e => e.Key).ToList()),
            Requested = Queue.Where(r => !r.Map.Official).Select(r => Key(r.Map)).Distinct().ToList(),
        };
        var text = JsonSerializer.Serialize(file, Ctx.Default.QueuesFile);
        if (text == QueuesWritten) return;
        file.Written = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        try
        {
            File.WriteAllText(QueuesPath + ".tmp", JsonSerializer.Serialize(file, Ctx.Default.QueuesFile));
            File.Move(QueuesPath + ".tmp", QueuesPath, true);
            QueuesWritten = text;
        }
        catch (Exception e) { Log($"queue: {QueuesPath} not written ({Clip(e.Message, 120)})"); }
    }

    /// <summary>POST /ready/&lt;secret&gt;: the restart watcher's report of the queued maps on the server's disk ("gg-ready\t1", then one
    /// id a line). Runs on the event loop.</summary>
    static void ReadyReport(HttpListenerContext c)
    {
        var body = new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd();
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { done.TrySetResult(ApplyReady(body)); } catch (Exception e) { Log("ready: " + e); done.TrySetResult("error: " + e.Message); } });
        Reply(c, done.Task.Wait(5000) ? done.Task.Result : "busy: the bridge did not get to it within 5 s");
    }

    static string ApplyReady(string body)
    {
        var lines = body.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || lines[0].Trim() != "gg-ready\t1") return "refused: not a gg-ready 1 report";
        var ids = lines.Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0 && l.All(char.IsAsciiDigit)).ToHashSet();
        var news = ReadyAt == default || !ids.SetEquals(ReadyIds);
        (ReadyIds, ReadyAt) = (ids, Now());
        if (news && !SelfTesting) Log($"ready: the restart watcher reports {ids.Count} map(s) on the server's disk - {QueueReadyWords()}");
        return $"ok: {ids.Count} on disk - {QueueReadyWords()}" + (HoldWhy() is { } why ? $"; hold: {why}" : "");
    }

    /// <summary>Why the watcher should start no background download now, or null: the map a player waits for would queue behind it.
    /// Steam's client runs one Workshop download at a time - measured on a test copy on 2026-11-03: a map change's 449 MB started
    /// only when the 350 MB in the background had ended, 8 s later - and one already running cannot be stopped, so the watcher asks
    /// right before it starts one. A change counting down, a map loading (it may be downloading), a vote open (its winner loads next).</summary>
    static string? HoldWhy() => Pending != null ? "a map change is counting down" : AnnouncePending ? "a map is loading"
        : Ballot is { Open: true } ? "the vote is open" : null;

    /// <summary>"armsrace-new 12/15, ..." - entries on disk of the entries queued.</summary>
    static string QueueReadyWords() => string.Join(", ", QueueNames.Select(n => $"{n} {QueueOf(n).Count(e => !e.Key.All(char.IsAsciiDigit) || ReadyIds.Contains(e.Key))}/{QueueOf(n).Count}"));

    /// <summary>The health page's line about the queues.</summary>
    static string QueueHealth() => $"random queues of {QueueLength}: " + (ReadyAt == default ? string.Join(", ", QueueNames.Select(n => $"{n} {QueueOf(n).Count}")) + "; no report from the restart watcher yet"
        : QueueReadyWords() + $" on disk by the watcher's report {(Now() - ReadyAt).TotalSeconds:0} s ago{(ReadyKnown ? "" : " (stale: every entry counts as on disk)")}")
        + $"; requested {Queue.Count(r => !r.Map.Official)}";
}

/// <summary>One entry of a random queue (state.json): the map's key, its weight and too-big mark when it was rolled, and when.</summary>
sealed record RolledMap(string Key, double Weight, bool Big, DateTime At);

/// <summary>/data/queues.json, version 1 (RandomQueue.cs): the random queues' keys in play order - Workshop ids, or an official map's
/// name - and the Workshop ids players have queued.</summary>
sealed class QueuesFile
{
    public int Version { get; set; }
    public string Written { get; set; } = "";
    public int Length { get; set; }
    public Dictionary<string, List<string>> Random { get; set; } = new();
    public List<string> Requested { get; set; } = new();
}
