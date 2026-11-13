using System.Globalization;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>THE BACKGROUND DOWNLOADS: the maps the bridge's stock and the open ballot play next (queues.json), on disk before
/// anybody asks - downloaded with MultiAddonManager's mm_download_addon, one at a time, also while people play, and silently (the download
/// line is for downloads somebody waits for). Never while a download somebody waits for runs, nor in a process whose downloads are broken
/// (a stuck result marks it, for the download restart). Every look, and at least every ReportEvery, the bridge hears which of them are on disk (POST
/// /ready), and which one downloads now with its progress (the report's "current" line); its random picks take what is on disk first. A change of
/// queues.json is looked at the next second, not LookEvery later: a map put on the open ballot starts downloading at once. While a vote is open
/// (queues.json names its maps, "ballot") only its maps are fetched: Steam runs one download at a time and one running cannot be stopped, so a stock
/// download started then would hold up a map a player puts on the ballot a moment later - which may win.</summary>
public sealed class ReadyMaps(WorkshopJobs ws)
{
    public const double LookEvery = 20;         // seconds between two looks at queues.json and the cache
    public const double ReportEvery = 120;      // the report goes again at least this often (the bridge trusts one for 5 minutes)
    public const double RetryAfter = 1800;      // a queued map whose background download failed is tried again after this...
    public const int Tries = 3;                 // ...up to this many times in one engine process
    public const double ItemTimeout = 900;      // a background download without a result this long is over: tried again later

    public (string Id, double Sent)? Current { get; set; }          // the background download in flight
    public Dictionary<string, (int Tries, double NotBefore)> FailedIds { get; } = [];
    public double LookAt { get; set; }
    (string Key, double At)? reported;                              // what was reported, and when
    (DateTime, long)? queuesStamp;
    List<string> queuesIds = [];
    HashSet<string> ballotIds = [];                                 // the open ballot's maps, of what queues.json names
    (DateTime, long)? acfStamp;
    Dictionary<string, long> acfInstalled = [];
    public int DownloadCount { get; private set; }

    ServerConfig Config => ws.Watch.Config;
    Log Log => ws.Watch.Log;

    public void Reset()
    {
        Current = null;
        FailedIds.Clear();
        LookAt = 0;
    }

    /// <summary>What queues.json names, in the order it is needed: the open ballot's maps ("ballot"), then the stock lanes' first entries,
    /// their second ones, and so on. A file of any version but KeepSet.QueuesVersion names nothing: an older bridge's is not read.</summary>
    public List<string> Wanted()
    {
        var path = Path.Combine(Config.Paths.BridgeData, "queues.json");
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
                return [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var stamp = (info.LastWriteTimeUtc, info.Length);
        if (stamp != queuesStamp)
        {
            var data = Node.ReadJson(path) as JsonObject;
            if (data is not null && Node.Number(data["version"]) != KeepSet.QueuesVersion)
                data = null;
            var ids = new List<string>();
            if (data?["ballot"] is JsonArray ballot)
                ids.AddRange(ballot.Select(Node.Str).Where(Digits));
            ballotIds = [.. ids];
            var lanes = data?["stock"] is JsonObject stock
                ? stock.Select(kv => kv.Value is JsonArray a ? a.Select(Node.Str).Where(Digits).ToList() : null).Where(l => l != null).Select(l => l!).ToList()
                : [];
            for (var i = 0; i < (lanes.Count == 0 ? 0 : lanes.Max(l => l.Count)); i++)
                ids.AddRange(lanes.Where(l => i < l.Count).Select(l => l[i]));
            queuesStamp = stamp;
            queuesIds = ids.Distinct().ToList();
        }
        return queuesIds;
    }

    static bool Digits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    /// <summary>Whether queues.json changed since a look last read it: a map put on the open ballot, a vote opened or over, the stock rolled on.</summary>
    bool QueuesChanged()
    {
        try
        {
            var info = new FileInfo(Path.Combine(Config.Paths.BridgeData, "queues.json"));
            return info.Exists && (info.LastWriteTimeUtc, info.Length) != queuesStamp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public Dictionary<string, long> Installed()
    {
        FileInfo info;
        try
        {
            info = new FileInfo(Config.Paths.WorkshopAcf);
            if (!info.Exists)
                return [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var stamp = (info.LastWriteTimeUtc, info.Length);
        if (stamp != acfStamp)
        {
            try
            {
                acfInstalled = KeyValues.Installed(File.ReadAllText(info.FullName));
                acfStamp = stamp;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
            {
            }
        }
        return acfInstalled;
    }

    /// <summary>Why no background download may start now, or null.</summary>
    public string? Gate()
    {
        var w = ws.Watch;
        if (w.A2sInfo is null || w.ArmedPid is null)
            return "the engine is not answering A2S";
        if (ws.Broken is { } b)
            return $"workshop downloads are broken in this engine process ({b.Why})";
        var own = ws.Own();
        if (ws.Line.ActiveDownloads.Keys.Any(x => !own.Contains(x)))
            return "a download somebody waits for goes first";
        return null;
    }

    /// <summary>The report's line about the background download in flight: "current", its id, its percent ("-" when unknown) and title.</summary>
    public string? CurrentLine(double now)
    {
        if (Current is not { } c)
            return null;
        var (done, total) = ws.Line.Progress(c.Id, now);
        return BridgeWire.CurrentLine(c.Id, done > 0 && total > 0 ? DownloadLine.Percent(done, total) : null, ws.Line.Title(c.Id, now));
    }

    /// <summary>POST /ready to the bridge when what is on disk or the current download changed, every ReportEvery, or when forced (right
    /// before a background download starts). The bridge's answer, or null. An answer ending in "; hold: &lt;why&gt;" means a map a player
    /// waits for is on its way: Steam runs one Workshop download at a time and one already running cannot be stopped, so none may start.</summary>
    public string? Report(double now, HashSet<string> ready, int total, bool force = false)
    {
        if (ws.Watch.Bridge.Address is null)
        {
            Log.Once("ready-endpoint", "ready: no bridge address (bridge.secret is empty) - the bridge counts every queued map as on disk", 21600);
            return null;
        }
        var current = CurrentLine(now);
        var key = string.Join("\n", ready.OrderBy(x => x, StringComparer.Ordinal)) + "\n#" + (current ?? "");
        if (!force && reported is { } r && r.Key == key && now - r.At < ReportEvery)
            return null;
        var (told, reply) = ws.Watch.Bridge.Post(BridgeWire.Ready, BridgeWire.ReadyBody(ready.OrderBy(x => x, StringComparer.Ordinal), current), 3);
        if (told != "ok")
        {
            Log.Once("ready-report", $"WARNING: the ready report to the bridge failed ({reply}) - it counts every queued map as on disk", 3600);
            return null;
        }
        var news = reported is null || reported.Value.Key.Split("\n#")[0] != key.Split("\n#")[0];
        reported = (key, now);
        if (news)
            Log.Line($"ready: {ready.Count} of {total} queued map(s) on disk, reported to the bridge ({Clip(reply ?? "", 240)})");
        return reply;
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..n];

    public void Step(double now, List<DownloadEvent> events)
    {
        if (!Config.BackgroundDownloads)
            return;
        if (Current is { } cur)
        {
            foreach (var e in events)
            {
                if (e.Id != cur.Id || e.Kind != "result")
                    continue;
                Current = null;
                ws.Line.Finished(cur.Id);                         // whatever MultiAddonManager printed of it is over too
                if (e.Text.Trim().ToUpperInvariant() == "OK")
                {
                    DownloadCount++;
                    FailedIds[cur.Id] = (0, now + RetryAfter);    // should the acf not show it, not again at once: no download loop
                    LookAt = 0;                                   // look again at once: the bridge hears of it now
                    Log.Line($"ready: {cur.Id} downloaded in the background in {Words.Hm(now - cur.Sent)} ({DownloadCount} this engine process)");
                }
                else
                {
                    var tries = (FailedIds.TryGetValue(cur.Id, out var f) ? f.Tries : 0) + 1;
                    FailedIds[cur.Id] = (tries, now + RetryAfter);
                    Log.Line($"ready: the background download of {cur.Id} came back '{e.Text}' (try {tries} of {Tries} in this engine process)");
                    if (Downloads.Stuck(e.Text, e.Cancel))
                        ws.MarkBroken(now, $"a background download ({cur.Id}) came back '{e.Text}'");
                }
                break;
            }
            if (Current is { } still && now - still.Sent > ItemTimeout)
            {
                FailedIds[still.Id] = ((FailedIds.TryGetValue(still.Id, out var f) ? f.Tries : 0) + 1, now + RetryAfter);
                Log.Line($"ready: no result for {still.Id} after {Words.Hm(ItemTimeout)} - tried again later");
                Current = null;
            }
        }
        if (now < LookAt && !QueuesChanged())
            return;
        LookAt = now + LookEvery;
        var wanted = Wanted();
        if (wanted.Count == 0)
            return;
        var installed = Installed();
        var ready = wanted.Where(x => installed.ContainsKey(x) && Directory.Exists(Path.Combine(Config.Paths.Content, x))).ToHashSet();
        var missing = wanted.Where(x => !ready.Contains(x)).ToList();
        var todo = missing.Where(x => !FailedIds.TryGetValue(x, out var f) || (f.Tries < Tries && now >= f.NotBefore)).ToList();
        if (ballotIds.Count > 0)
            todo = todo.Where(ballotIds.Contains).ToList(); // a vote is open: its maps alone (the class summary)
        var why = Current is null && todo.Count > 0 ? Gate() : null;
        var reply = Report(now, ready, wanted.Count, force: Current is null && todo.Count > 0 && why is null);
        if (Current is not null || todo.Count == 0)
            return;
        if (why is null && reply is not null && reply.Contains("; hold:", StringComparison.Ordinal))
            why = $"the bridge says hold ({reply.Split("; hold:", 2)[1].Trim()})";
        if (why is not null)
        {
            Log.Once("ready-wait", $"ready: {missing.Count} of {wanted.Count} queued map(s) not on disk - waiting: {why}", 1800);
            return;
        }
        var id = todo[0];
        if (ws.Watch.Engine.Rcon($"mm_download_addon {id}").Reply is not { } answer)
            return;
        if (answer.Contains("unknown command", StringComparison.OrdinalIgnoreCase))
        {
            Log.Once("ready-mam", "ready: mm_download_addon is not loaded (MultiAddonManager is not running) - no background downloads", 21600);
            return;
        }
        Current = (id, now);
        Log.Line($"ready: {id} is not on disk - downloading it in the background, silently ({missing.Count} of {wanted.Count} queued map(s) missing)");
    }
}
