using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>One event of the Steam client's workshop log: an item requested, its result, or a job's "Update canceled:" line (which comes
/// right before the result of the item that job was for).</summary>
public sealed record DownloadEvent(string Id, string Kind, string Text, string? Cancel = null);

public static partial class Downloads
{
    [GeneratedRegex(@"Download item (\d+) (?:(requested) by app|result : (.*?))\s*$")] private static partial Regex DownloadRx();
    [GeneratedRegex(@"\] Update canceled: (.*?)\s*$")] private static partial Regex CanceledRx();
    [GeneratedRegex("no connection|no download source", RegexOptions.IgnoreCase)] private static partial Regex StuckRx();

    public static List<DownloadEvent> Events(IEnumerable<string> lines)
    {
        var events = new List<DownloadEvent>();
        foreach (var line in lines)
        {
            var m = DownloadRx().Match(line);
            if (m.Success)
            {
                events.Add(new DownloadEvent(m.Groups[1].Value, m.Groups[2].Success ? "requested" : "result", m.Groups[3].Success ? m.Groups[3].Value : ""));
                continue;
            }
            m = CanceledRx().Match(line);
            if (m.Success)
                events.Add(new DownloadEvent("", "canceled", m.Groups[1].Value));
        }
        return events;
    }

    /// <summary>Does a failed download say the process has no download source left - for good? Its result ("No Connection") or the job's
    /// "Update canceled:" line: "Failed downloading 306 manifests (No connection)" (the source pool ran out during the manifest re-check),
    /// "... while downloading chunk ... "No Download Source"" (it ran out during the download), "... while starting download (No
    /// connection to content servers)" (it was empty already).</summary>
    public static bool Stuck(params string?[] texts) => texts.Any(t => StuckRx().IsMatch(t ?? ""));

    /// <summary>A map's size as the HUD shows it (the lists' sizeMb): binary units, labelled MB and GB.</summary>
    public static string SizeWords(double bytes) => bytes >= 1L << 30
        ? (bytes / (1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : Math.Max(1, Math.Round(bytes / (1L << 20), MidpointRounding.ToEven)).ToString("0", CultureInfo.InvariantCulture) + " MB";
}

/// <summary>A download somebody waits for, or job 9's: since when, when last seen, and its bytes when MultiAddonManager says them.</summary>
public sealed class Active
{
    public double Since, Seen;
    public double? Done, Total;
    public bool Engine;            // the engine itself waits for it: a map change does
}

/// <summary>JOB 8, THE DOWNLOAD LINE: which downloads run, and the news line about the newest one somebody waits for. Fed from console.log
/// (ConsoleLine) and the Steam client's workshop log (Started, Finished, Failed); Step sends the line. A failed RCON call only skips an
/// update. While the map a player waits for queues behind a background download (Steam runs one at a time and one running cannot be
/// stopped), the line says so, with that one's progress.</summary>
public sealed partial class DownloadLine(ServerConfig config, IEngine engine, Log log)
{
    public const double After = 3;          // a download this young says nothing: a cached map's "download" ends within a second
    public const double Every = 2;          // seconds between two updates of the line
    public const int Hold = 4;              // seconds one update stays up, so the line lasts until the next and goes by itself after the last
    public const double Forget = 1800;      // a download with no sign of life for this long is over, whatever it never said

    [GeneratedRegex(@"\[Workshop\] Updating (\d+): state (\d+)")] private static partial Regex EngineState();
    [GeneratedRegex(@"\[Workshop\] Loading custom game ""(\d+)""")] private static partial Regex EngineLoading();
    [GeneratedRegex(@"\[MultiAddonManager\] Addon download started for (\d+)")] private static partial Regex MamStarted();
    [GeneratedRegex(@"\[MultiAddonManager\] Downloading addon (\d+): ([\d.]+)/([\d.]+) MB")] private static partial Regex MamProgress();
    [GeneratedRegex(@"\[MultiAddonManager\] Addon (\d+) downloaded successfully")] private static partial Regex MamDone();
    [GeneratedRegex(@"\[MultiAddonManager\] Addon (\d+) download failed with reason ""([^""]*)""")] private static partial Regex MamFailed();

    public Dictionary<string, Active> ActiveDownloads { get; } = [];
    (string Id, string Why)? failure;          // one line about it goes out at the next step
    double sent;
    readonly HashSet<string> told = [];        // ids whose line was logged once
    Dictionary<string, (string Name, long Bytes)> names = [];
    double namesAt = double.NegativeInfinity;

    /// <summary>Bytes of an item in the cache's staging folders; a test stands in its own.</summary>
    public Func<string, long> Staged { get; set; } = _ => 0;

    public void Reset()
    {
        ActiveDownloads.Clear();
        failure = null;
        told.Clear();
    }

    public Active Started(string id, double now)
    {
        if (!ActiveDownloads.TryGetValue(id, out var e))
            ActiveDownloads[id] = e = new Active { Since = now, Seen = now };
        e.Seen = now;
        return e;
    }

    public void Finished(string id)
    {
        ActiveDownloads.Remove(id);
        told.Remove(id);
    }

    public void Failed(string id, string why, double now)
    {
        if (ActiveDownloads.ContainsKey(id) || told.Contains(id))
            failure = (id, why);
        Finished(id);
    }

    /// <summary>One console.log line. Cheap: most lines match nothing.</summary>
    public void ConsoleLine(string line, double now)
    {
        if (!line.Contains("[Workshop]", StringComparison.Ordinal) && !line.Contains("[MultiAddonManager]", StringComparison.Ordinal))
            return;
        if (EngineLoading().Match(line) is { Success: true } loading)
        {
            Finished(loading.Groups[1].Value);
            return;
        }
        if (EngineState().Match(line) is { Success: true } state)
        {
            if (state.Groups[2].Value == "5")
                Finished(state.Groups[1].Value);
            else
                Started(state.Groups[1].Value, now).Engine = true;
            return;
        }
        if (MamStarted().Match(line) is { Success: true } started)
        {
            Started(started.Groups[1].Value, now);
            return;
        }
        if (MamProgress().Match(line) is { Success: true } p)
        {
            var e = Started(p.Groups[1].Value, now);
            if (double.TryParse(p.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var done)
                && double.TryParse(p.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var total))
                (e.Done, e.Total) = (done * (1 << 20), total * (1 << 20));
            return;
        }
        if (MamDone().Match(line) is { Success: true } ok)
        {
            Finished(ok.Groups[1].Value);
            return;
        }
        if (MamFailed().Match(line) is { Success: true } failed)
            Failed(failed.Groups[1].Value, failed.Groups[2].Value, now);
    }

    /// <summary>{id: (name, bytes)} from the bridge's lists, read again once a minute while a line is due.</summary>
    public Dictionary<string, (string Name, long Bytes)> Lookup(double now)
    {
        if (now - namesAt >= 60)
        {
            namesAt = now;
            var found = new Dictionary<string, (string, long)>();
            foreach (var file in new[] { "maps-wingman.json", "maps-deathmatch.json", "maps.json" })
                if (Files.ReadJson(Path.Combine(config.Paths.BridgeData, file)) is JsonArray list)
                    foreach (var entry in list)
                        if (entry is JsonObject o && KeepSet.Digits(o["id"]) is { } id)
                            found[id] = (MapsFolder.Str(o["name"]), (KeepSet.Integer(o["sizeMb"]) ?? 0) << 20);
            names = found;
        }
        return names;
    }

    public string Title(string id, double now)
    {
        var name = Lookup(now).TryGetValue(id, out var n) ? n.Name.Replace("%", "") : "";
        name = (name.Length > 48 ? name[..48] : name).Trim();
        return name.Length > 0 ? name : $"Workshop item {id}";
    }

    /// <summary>(done, total) bytes of a download as far as known: MultiAddonManager's, else the staging folders' against the lists' size.</summary>
    public (double Done, double Total) Progress(string id, double now)
    {
        var size = Lookup(now).TryGetValue(id, out var n) ? n.Bytes : 0;
        ActiveDownloads.TryGetValue(id, out var e);
        var total = e?.Total is { } t && t > 0 ? t : size;
        var done = e?.Done ?? Staged(id);
        return (done, total);
    }

    public static int Percent(double done, double total) => total > 0 ? (int)Math.Min(99, Math.Floor(done * 100 / total)) : -1;

    public string Words(string id, double now, string? background = null)
    {
        var name = Title(id, now);
        var (done, total) = Progress(id, now);
        if (done <= 0 && background is { } bg && bg != id)
        {
            var (bd, bt) = Progress(bg, now);
            var p = bd > 0 && bt > 0 ? $" ({Percent(bd, bt)} percent)" : "";
            return $"Server is finishing the background download of {Title(bg, now)} first{p} - then {name}, the map changes when it is ready";
        }
        if (done > 0 && total > 0)    // no percent sign on a console line
            return $"Server is downloading {name}: {Percent(done, total)} percent ({Downloads.SizeWords(done)} of {Downloads.SizeWords(total)}) - the map changes when it is ready";
        if (done > 0)
            return $"Server is downloading {name}: {Downloads.SizeWords(done)} so far - the map changes when it is ready";
        if (total > 0)
            return $"Server is downloading {name} ({Downloads.SizeWords(total)}) - the map changes when it is ready";
        return $"Server is downloading {name} - the map changes when it is ready";
    }

    /// <summary>The line, every Every seconds while somebody waits for a download: `own` holds job 9's download nobody waits for.</summary>
    public void Step(double now, int humans, IReadOnlySet<string>? own = null)
    {
        if (!config.DownloadLine)
            return;
        own ??= new HashSet<string>();
        foreach (var id in ActiveDownloads.Where(kv => now - kv.Value.Seen > Forget).Select(kv => kv.Key).ToList())
            Finished(id);
        if (humans <= 0)
        {
            failure = null;
            return;
        }
        if (failure is { } f)
        {
            failure = null;
            var why = f.Why.Replace("%", "").Trim();
            var text = Core.Words.ChatSafe($"The server could not download {Title(f.Id, now)}: {(why.Length > 0 ? why : "no reason given")} - pick another map or press Next", 240);
            log.Line($"hud: {text}");
            engine.Rcon([$"gg_notice 10 \"{text}\""]);
            sent = now;
            return;
        }
        if (now - sent < Every)
            return;
        var waiting = ActiveDownloads.Where(kv => !own.Contains(kv.Key) && now - kv.Value.Since >= After).ToList();
        if (waiting.Count == 0)
            return;
        var newest = waiting.MaxBy(kv => kv.Value.Since).Key;
        var line = Core.Words.ChatSafe(Words(newest, now, own.FirstOrDefault()), 240);
        if (told.Add(newest))
            log.Line($"hud: {line}");
        sent = now;
        engine.Rcon([$"gg_notice {Hold} \"{line}\""]);
    }
}

/// <summary>JOB 9, THE QUEUED MAPS IN THE BACKGROUND: the maps the bridge's random queues and the open ballot play next (queues.json), on
/// disk before anybody asks - downloaded with MultiAddonManager's mm_download_addon, one at a time, also while people play, and silently
/// (job 8's line is for downloads somebody waits for). Never while a download somebody waits for runs, nor in a process whose downloads are
/// broken (job 6: a stuck result marks it). Every look, and at least every ReportEvery, the bridge hears which of them are on disk (POST
/// /ready), and which one downloads now with its progress (the report's "current" line); its random picks take what is on disk first.</summary>
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

    /// <summary>What queues.json names, in the order it is needed: the open ballot's maps ("requested"), then the queues' first entries,
    /// their second ones, and so on.</summary>
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
            var data = Files.ReadJson(path) as JsonObject;
            var ids = new List<string>();
            if (data?["requested"] is JsonArray req)
                ids.AddRange(req.Select(KeepSet.PyStr).Where(Digits));
            var lanes = data?["random"] is JsonObject random
                ? random.Select(kv => kv.Value is JsonArray a ? a.Select(KeepSet.PyStr).Where(Digits).ToList() : null).Where(l => l != null).Select(l => l!).ToList()
                : [];
            for (var i = 0; i < (lanes.Count == 0 ? 0 : lanes.Max(l => l.Count)); i++)
                ids.AddRange(lanes.Where(l => i < l.Count).Select(l => l[i]));
            queuesStamp = stamp;
            queuesIds = ids.Distinct().ToList();
        }
        return queuesIds;
    }

    static bool Digits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

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
        var percent = done > 0 && total > 0 ? DownloadLine.Percent(done, total).ToString(CultureInfo.InvariantCulture) : "-";
        var title = ws.Line.Title(c.Id, now).Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        return $"current\t{c.Id}\t{percent}\t{title}";
    }

    /// <summary>POST /ready to the bridge when what is on disk or the current download changed, every ReportEvery, or when forced (right
    /// before a background download starts). The bridge's answer, or null. An answer ending in "; hold: &lt;why&gt;" means a map a player
    /// waits for is on its way: Steam runs one Workshop download at a time and one already running cannot be stopped, so none may start.</summary>
    public string? Report(double now, HashSet<string> ready, int total, bool force = false)
    {
        if (ws.Watch.Bridge.Address is null)
        {
            Log.Once("ready-endpoint", "ready: no bridge address (bridge.url and bridge.secret, or the image's CS2_LOG_HTTP_URL) - the bridge counts every queued map as on disk", 21600);
            return null;
        }
        var current = CurrentLine(now);
        var key = string.Join("\n", ready.OrderBy(x => x, StringComparer.Ordinal)) + "\n#" + (current ?? "");
        if (!force && reported is { } r && r.Key == key && now - r.At < ReportEvery)
            return null;
        var body = "gg-ready\t1\n" + string.Concat(ready.OrderBy(x => x, StringComparer.Ordinal).Select(x => x + "\n")) + (current is null ? "" : current + "\n");
        var (told, reply) = ws.Watch.Bridge.Post("ready", body, 3);
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
                    Log.Line($"ready: {cur.Id} downloaded in the background in {Rules.Hms(now - cur.Sent)} ({DownloadCount} this engine process)");
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
                Log.Line($"ready: no result for {still.Id} after {Rules.Hms(ItemTimeout)} - tried again later");
                Current = null;
            }
        }
        if (now < LookAt)
            return;
        LookAt = now + LookEvery;
        var wanted = Wanted();
        if (wanted.Count == 0)
            return;
        var installed = Installed();
        var ready = wanted.Where(x => installed.ContainsKey(x) && Directory.Exists(Path.Combine(Config.Paths.Content, x))).ToHashSet();
        var missing = wanted.Where(x => !ready.Contains(x)).ToList();
        var todo = missing.Where(x => !FailedIds.TryGetValue(x, out var f) || (f.Tries < Tries && now >= f.NotBefore)).ToList();
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
        var replies = ws.Watch.Engine.Rcon([$"mm_download_addon {id}"]);
        if (replies is null || replies.Count == 0 || replies[0] is null)
            return;
        if (replies[0]!.Contains("unknown command", StringComparison.OrdinalIgnoreCase))
        {
            Log.Once("ready-mam", "ready: mm_download_addon is not loaded (MultiAddonManager runs only while the HUD is on) - no background downloads", 21600);
            return;
        }
        Current = (id, now);
        Log.Line($"ready: {id} is not on disk - downloading it in the background, silently ({missing.Count} of {wanted.Count} queued map(s) missing)");
    }
}

/// <summary>Job 9 and the download line, a state machine stepped once per watcher tick - never blocking it for longer than one RCON command
/// or one report to the bridge. And job 6's mark: a process whose downloads say it has no download source left is broken for good.</summary>
public sealed class WorkshopJobs
{
    public Watch Watch { get; }
    public DownloadLine Line { get; }
    public ReadyMaps Ready { get; }
    public Tail Tail { get; }
    public (double Since, string Why)? Broken { get; set; }    // downloads are failing in this engine process: a restart is due
    int? pidSeen;
    string? cancel;                                            // the last "Update canceled:" reason, until its result line

    public WorkshopJobs(Watch watch)
    {
        Watch = watch;
        Line = new DownloadLine(watch.Config, watch.Engine, watch.Log) { Staged = Staged };
        Ready = new ReadyMaps(this);
        Tail = new Tail(watch.Config.Paths.WorkshopLog);
    }

    /// <summary>Bytes of this item in the cache's staging folders.</summary>
    long Staged(string id)
    {
        long total = 0;
        foreach (var stage in new[] { "downloads", "temp" })
        {
            var root = Path.Combine(Watch.Config.Paths.WorkshopDir, stage);
            if (!Directory.Exists(root))
                continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    var rel = Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
                    if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(id))
                        try
                        {
                            total += new FileInfo(file).Length;
                        }
                        catch (IOException)
                        {
                        }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return total;
    }

    /// <summary>The downloads nobody waits for: job 9's - unless the engine itself waits for it (a pick of the very map it was fetching:
    /// then it is the player's download, and the line shows it).</summary>
    public HashSet<string> Own()
    {
        var own = new HashSet<string>();
        if (Ready.Current is { } c && !(Line.ActiveDownloads.TryGetValue(c.Id, out var e) && e.Engine))
            own.Add(c.Id);
        return own;
    }

    public void MarkBroken(double now, string why)
    {
        if (Broken is not null)
            return;
        Broken = (now, why);
        Watch.Log.Line($"WARNING: workshop downloads are broken in this engine process ({why}) - a restart is due as soon as nobody is on (at most one per {Rules.Hms(Rules.WorkshopRestartGap)}, {Rules.WorkshopRestartMax} per 24 h)");
        Snapshots.SteamLogs(Watch, why, Watch.ArmedPid);   // before any restart: a recreate would lose them
    }

    public void Step(double now)
    {
        var events = new List<DownloadEvent>();
        foreach (var e in Downloads.Events(Tail.Lines()))
        {
            if (e.Kind == "canceled")
                cancel = e.Text;                     // the job's reason, attached to the result that follows it
            else if (e.Kind == "result")
            {
                events.Add(e with { Cancel = cancel });
                cancel = null;
            }
            else
                events.Add(e);
        }
        if (Watch.ArmedPid is { } pid && pid != pidSeen)
        {
            pidSeen = pid;
            if (Broken is not null)
                Watch.Log.Line($"workshop downloads: engine pid {pid} is a new process - the broken flag is cleared");
            Broken = null;
            Line.Reset();
            Ready.Reset();
        }
        if (Broken is not null)
            foreach (var e in events)
                if (e.Kind == "result" && e.Text.Trim().ToUpperInvariant() == "OK")
                {
                    Watch.Log.Line($"workshop downloads work again (item {e.Id} OK) - the download restart is no longer due");
                    Broken = null;
                    break;
                }
        StepPlayers(now, events);
        Ready.Step(now, events);
    }

    /// <summary>Every download that is not job 9's: job 8's line, and job 6's broken mark for it.</summary>
    void StepPlayers(double now, List<DownloadEvent> events)
    {
        var own = Own();
        foreach (var e in events)
        {
            if (e.Id.Length == 0 || own.Contains(e.Id))
                continue;
            if (e.Kind == "requested")
                Line.Started(e.Id, now);
            else if (e.Text.Trim().ToUpperInvariant() == "OK")
                Line.Finished(e.Id);
            else
            {
                Line.Failed(e.Id, e.Text, now);
                if (Downloads.Stuck(e.Text, e.Cancel))
                    MarkBroken(now, $"a download for players ({e.Id}) came back '{e.Text}'");
            }
        }
        Line.Step(now, Watch.A2sInfo?.Humans ?? 0, own);
    }
}
