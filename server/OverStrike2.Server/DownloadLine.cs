using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>A download somebody waits for, or a background download: since when, when last seen, and its bytes when MultiAddonManager says them.</summary>
public sealed class Active
{
    public double Since, Seen;
    public double? Done, Total;
    public bool Engine;            // the engine itself waits for it: a map change does
}

/// <summary>THE DOWNLOAD LINE: which downloads run, and the top line about the newest one somebody waits for. Fed from console.log
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
    // The engine's own progress of a download it waits for, many times a second, in decimal units: "(0 bytes / 335.80 MB)".
    [GeneratedRegex(@"\[Workshop\] Downloading (\d+) \(([\d.]+) (bytes|KB|MB|GB) / ([\d.]+) (bytes|KB|MB|GB)\)")] private static partial Regex EngineProgress();
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

    public void Failed(string id, string why)
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
        if (EngineProgress().Match(line) is { Success: true } ep)
        {
            var e = Started(ep.Groups[1].Value, now);
            e.Engine = true;                         // the engine itself waits for it: a map change does
            if (Bytes(ep.Groups[2].Value, ep.Groups[3].Value) is { } done && Bytes(ep.Groups[4].Value, ep.Groups[5].Value) is { } total && total > 0)
                (e.Done, e.Total) = (done, total);   // exact, where the staging folder and the lists' sizes are guesses
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
            Failed(failed.Groups[1].Value, failed.Groups[2].Value);
    }

    static double? Bytes(string value, string unit) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v * unit switch { "KB" => 1e3, "MB" => 1e6, "GB" => 1e9, _ => 1 } : null;

    /// <summary>{id: (name, bytes)} from the bridge's lists, read again once a minute while a line is due.</summary>
    public Dictionary<string, (string Name, long Bytes)> Lookup(double now)
    {
        if (now - namesAt >= 60)
        {
            namesAt = now;
            var found = new Dictionary<string, (string, long)>();
            foreach (var file in new[] { "maps-wingman.json", "maps-deathmatch.json", "maps.json" })
                if (Node.ReadJson(Path.Combine(config.Paths.BridgeData, file)) is JsonArray list)
                    foreach (var entry in list)
                        if (entry is JsonObject o && Node.Id(o["id"]) is { } id)
                            found[id] = (MapsFolder.Str(o["name"]), (Node.Integer(o["sizeMb"]) ?? 0) << 20);
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
            return $"Server is finishing the background download of {Title(bg, now)} first{p} - Then {name}, the map changes when it is ready";
        }
        if (done > 0 && total > 0)    // no percent sign on a console line
            return $"Server is downloading {name}: {Percent(done, total)} percent ({Downloads.SizeWords(done)} of {Downloads.SizeWords(total)}) - The map changes when it is ready";
        if (done > 0)
            return $"Server is downloading {name}: {Downloads.SizeWords(done)} so far - The map changes when it is ready";
        if (total > 0)
            return $"Server is downloading {name} ({Downloads.SizeWords(total)}) - The map changes when it is ready";
        return $"Server is downloading {name} - The map changes when it is ready";
    }

    /// <summary>The line, every Every seconds while somebody waits for a download: `own` holds the background download nobody waits for.</summary>
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
            var text = Core.Words.ChatSafe($"The server could not download {Title(f.Id, now)}: {(why.Length > 0 ? why : "no reason given")} - Pick another map", 240);
            log.Line($"hud: {text}");
            engine.Rcon($"gg_notice 10 \"{text}\"");
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
        engine.Rcon($"gg_notice {Hold} \"{line}\"");
    }
}
