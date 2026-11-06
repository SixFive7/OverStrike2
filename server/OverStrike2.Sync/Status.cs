using System.Text.Json.Nodes;

namespace OverStrike2.Sync;

/// <summary>THE STATUS FILE: &lt;bridge&gt;/sync-status.json, schema 1, for the in-game dashboard (the bridge reads it; it does not watch it) -
/// what the sync does now and why, today's counts, the totals, the download queue (its next maps in the order they are taken), the keep
/// set's parts and the history of runs and map events. Written atomically (a tmp file, then a rename): at every phase change, at least every
/// TickSeconds while a run is active (a ticker thread writes the last picture with a fresh time; the main thread alone rebuilds it), and when
/// the service goes idle, waits or stops (again every IdleSeconds, so its time says the sync is alive). A failure to build or write it is
/// logged once and never stops the sync. Fields may be added, never renamed or dropped.</summary>
public sealed class Status(SyncConfig cfg, SyncClock clock, Action<string> log)
{
    public const double Heavy = 15;                  // seconds: the counts, the queue and the keep set are rebuilt at most this often during a run
    public const double IdleSeconds = 60;            // seconds: written again this often while the service is idle, waiting or stopped...
    public const double IdleHeavy = 300;             // ...with the counts rebuilt at most this often
    public const int Runs = 50, Maps = 200, Next = 25;
    public static readonly Dictionary<string, string> PhaseState = new()
    {
        ["start"] = "waiting", ["discovery"] = "discovery", ["check"] = "check", ["ratings"] = "ratings", ["serve"] = "serve", ["downloads"] = "download",
        ["tests"] = "test", ["publish"] = "publish",
    };
    public static readonly Dictionary<string, string> PhaseWhy = new()
    {
        ["start"] = "A run is starting.",
        ["discovery"] = "Looking for new maps on the Workshop.",
        ["check"] = "Asking the Workshop about every listed map: gone, hidden or updated.",
        ["ratings"] = "Fetching the Workshop's scores, subscriptions and trends for the lists of maps the server keeps.",
        ["serve"] = "Downloading the maps the server keeps but does not have yet, and handing them over to it.",
        ["downloads"] = "Downloading queued maps for their test.",
        ["tests"] = "Load-testing downloaded maps on a test copy of the server.",
        ["publish"] = "Putting the tested maps on the map lists.",
    };
    public static readonly Dictionary<string, string> QueueMode = new()
    {
        ["armsrace-new"] = "armsrace", ["armsrace-original"] = "armsrace", ["deathmatch"] = "deathmatch", ["wingman"] = "wingman",
    };
    public static readonly string[] BrokenKinds = ["advert", "crasher", "kick"];

    readonly object gate = new();
    public string Path { get; set; } = cfg.StatusFile;
    public double TickSeconds { get; set; } = 15;

    /// <summary>A test's look at each picture as it is written.</summary>
    public Action<JsonObject>? Wrote { get; set; }

    JsonObject now = new() { ["state"] = "idle", ["why"] = "The sync is starting.", ["map"] = null, ["step"] = null, ["run"] = null };
    JsonObject heavy = [];
    double heavyAt;
    string? failed;
    Thread? ticker;
    ManualResetEventSlim? stopTicker;

    public bool Ticking => ticker is not null;

    /// <summary>A new picture of "now" each time: the ticker never sees one half-changed.</summary>
    public void Set(params (string Key, JsonNode? Value)[] fields)
    {
        lock (gate)
        {
            var fresh = (JsonObject)now.DeepClone();
            foreach (var (k, v) in fields)
                fresh[k] = v?.DeepClone();
            now = fresh;
        }
    }

    public JsonObject Now
    {
        get
        {
            lock (gate)
                return (JsonObject)now.DeepClone();
        }
    }

    public JsonObject Doc()
    {
        lock (gate)
        {
            var out_ = new JsonObject { ["schema"] = 1, ["updated"] = (long)Math.Truncate(clock.Now()), ["now"] = now.DeepClone() };
            foreach (var (key, empty) in new (string, JsonNode)[]
                     {
                         ("today", new JsonObject()), ("totals", new JsonObject()), ("queue", new JsonObject()),
                         ("keep", new JsonObject { ["parts"] = new JsonArray(), ["totalGb"] = 0.0 }), ("history", new JsonObject { ["runs"] = new JsonArray(), ["maps"] = new JsonArray() }),
                     })
                out_[key] = heavy.TryGetPropertyValue(key, out var v) ? v?.DeepClone() : empty;
            return out_;
        }
    }

    public void Write()
    {
        lock (gate)
        {
            try
            {
                var doc = Doc();
                OverStrike2.Core.Files.WriteAtomic(Path, Node.Compact(doc) + "\n", FileModes.Private);
                Wrote?.Invoke(doc);
                if (failed is not null)
                    log($"status: {Path} written again");
                failed = null;
            }
            catch (Exception e) when (e is not StopRequested)   // the dashboard must never stop the sync
            {
                if (failed != e.Message)
                    log($"status: {Path} not written ({e.Message})");
                failed = e.Message;
            }
        }
    }

    /// <summary>The counts, the queue, the keep set and the history again (the main thread only), at most every `every` seconds.</summary>
    public void Rebuild(SyncRun run, bool force = false, double every = Heavy)
    {
        if (!force && clock.Now() - heavyAt < every)
            return;
        heavyAt = clock.Now();
        try
        {
            var fresh = run.StatusHeavy();
            lock (gate)
                heavy = fresh;
        }
        catch (Exception e) when (e is not StopRequested)
        {
            var why = $"rebuild: {Text.Of(e)}";
            if (failed != why)
                log($"status: not rebuilt ({Text.Of(e)}) - the last picture stays");
            failed = why;
        }
    }

    public void StartTicker()
    {
        StopTicker();
        var stop = new ManualResetEventSlim(false);
        stopTicker = stop;
        ticker = new Thread(() =>
        {
            while (!stop.Wait(TimeSpan.FromSeconds(TickSeconds)))
                Write();
        }) { IsBackground = true, Name = "sync-status" };
        ticker.Start();
    }

    public void StopTicker()
    {
        if (ticker is null)
            return;
        stopTicker!.Set();
        ticker.Join(5000);
        ticker = null;
    }

    /// <summary>A run's line in the status file's history, from its report (the ones written before the status file existed too).</summary>
    public static JsonObject RunEntry(JsonObject r, SyncClock clock)
    {
        JsonObject dl = Node.Obj(r["downloads"]), sv = Node.Obj(r["serve"]), t = Node.Obj(r["tests"]), pub = Node.Obj(r["published"]);
        var rows = Node.StrList(t["rows"]);
        var stop = new[] { dl["stop"], t["stop"], sv["stop"], r["api_stop"] }.FirstOrDefault(Node.Truthy) is { } s ? Node.Str(s) : null;
        return new JsonObject
        {
            ["started"] = Unix(clock, r["started"]), ["ended"] = Unix(clock, r["ended"]), ["kind"] = Node.Truthy(r["manual"]) ? "manual" : "auto",
            ["downloads"] = Node.Int(dl["ok"]) + Node.Int(sv["downloads"]),   // serve's own count, there once it downloaded: its ok also has what was staged already
            ["bytes"] = Node.Int(dl["bytes"]) + Node.Int(sv["dl_bytes"]),
            ["pass"] = rows.Count(x => x.StartsWith("PASS", StringComparison.Ordinal)), ["rejected"] = rows.Count(x => !x.StartsWith("PASS", StringComparison.Ordinal)),
            ["listed"] = Node.Arr(pub["listed"]).Count + Node.Obj(pub["modes"]).Sum(kv => Node.Arr(kv.Value).Count),
            ["removed"] = Node.Arr(Node.Get(r["check"], "removed")).Count, ["stop"] = stop is null ? null : Text.Cut(stop, 200),
        };
    }

    static JsonNode? Unix(SyncClock clock, JsonNode? stamp) => clock.UnixOf(stamp is null ? null : Node.Str(stamp)) is { } u ? (long)u : null;

    /// <summary>The history's runs from the reports kept so far, newest first: what the status file starts with.</summary>
    public static JsonArray SeedRuns(string stateDir, SyncClock clock)
    {
        var folder = System.IO.Path.Combine(stateDir, "reports");
        List<string> names;
        try
        {
            names = Directory.EnumerateFiles(folder, "*.json").Select(p => System.IO.Path.GetFileName(p)!).OrderByDescending(x => x, StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var runs = new List<JsonObject>();
        foreach (var name in names)
        {
            if (Node.ReadJson(System.IO.Path.Combine(folder, name)) is JsonObject r && Node.Truthy(r["started"]) && Node.Truthy(r["ended"]))
                runs.Add(RunEntry(r, clock));
            if (runs.Count >= Runs)
                break;
        }
        return new JsonArray(runs.OrderBy(e => -Node.Float(e["started"])).Cast<JsonNode?>().ToArray());
    }

    /// <summary>armsrace | deathmatch | wingman: the list a map is on, else the first of its Workshop tags; armsrace when neither says.</summary>
    public static string ListMode(JsonNode? rec, IReadOnlyDictionary<string, string>? listed = null, string? wid = null)
    {
        if (listed is not null && wid is not null && listed.TryGetValue(wid, out var mode))
            return mode;
        var tags = Node.Get(rec, "tags");
        foreach (var (m, tag, _, _, _) in GameModes.All)
            if (Node.In(tags, tag))
                return m;
        return "armsrace";
    }
}
