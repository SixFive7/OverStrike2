using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>Unlisted Workshop maps from the menu (../../bridge/HudWorkshop.cs holds the card and the tested logic): asks the public Steam Web
/// API about an id - this container has internet, the bridge has none - and downloads one item at a time into the server's own Workshop
/// cache with MultiAddonManager's mm_download_addon, which changes nothing for anybody playing. Progress comes from the cache's staging
/// folders, the outcome from the Steam client's workshop_log.txt ("Download item &lt;id&gt; result : OK"), the same log the restart watcher
/// reads (os2-server watch). HTTP runs on the thread pool and only hands back data; the engine (Server.ExecuteCommand) and the
/// file reads happen on the game thread, in Poll and Status.</summary>
sealed class WorkshopPlay
{
    const string LogFile = "/home/steam/Steam/logs/workshop_log.txt";
    static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10), DownloadLimit = TimeSpan.FromMinutes(30), SteamBusyAfter = TimeSpan.FromSeconds(10);

    readonly ILogger log;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    readonly Dictionary<long, Task<WorkshopItem?>> asking = new();
    readonly Dictionary<long, (WorkshopItem? Item, DateTime At)> known = new();
    readonly List<(long Id, long Bytes)> waiting = new();
    (long Id, long Bytes, DateTime Since, bool Requested)? current;
    readonly Dictionary<long, string> failed = new();          // id -> why its download failed (until it is tried again)
    string? workshopDir;

    public WorkshopPlay(ILogger log) { this.log = log; }

    /// <summary>The server's Workshop folder (…/game/bin/linuxsteamrt64/steamapps/workshop), found once.</summary>
    string? Dir()
    {
        if (workshopDir != null) return workshopDir;
        foreach (var root in new[] { Server.GameDirectory, Path.Combine(Server.GameDirectory, "..") })
        {
            var dir = Path.GetFullPath(Path.Combine(root, "bin", "linuxsteamrt64", "steamapps", "workshop"));
            if (Directory.Exists(dir)) return workshopDir = dir;
        }
        return null;
    }

    /// <summary>What the Workshop says about an id: null while the question is out (it is asked here, once), the item or null after.</summary>
    public (bool Done, WorkshopItem? Item, string? Error) Ask(long id, DateTime now)
    {
        if (known.TryGetValue(id, out var k) && now - k.At < CacheFor) return (true, k.Item, k.Item == null ? "the Workshop did not answer - Try again in a moment" : null);
        if (asking.TryGetValue(id, out var task))
        {
            if (!task.IsCompleted) return (false, null, null);
            asking.Remove(id);
            var item = task.IsCompletedSuccessfully ? task.Result : null;
            if (!task.IsCompletedSuccessfully) log.LogWarning("Over Strike 2 Workshop: details for {Id} failed: {Error}", id, task.Exception?.GetBaseException().Message);
            known[id] = (item, now);
            return (true, item, item == null ? "the Workshop did not answer - Try again in a moment" : null);
        }
        asking[id] = Details(id);
        return (false, null, null);
    }

    async Task<WorkshopItem?> Details(long id)
    {
        var form = new List<KeyValuePair<string, string>> { new("itemcount", "1"), new("publishedfileids[0]", id.ToString()) };
        using var response = await SteamWebApi.PostAsync(http, Workshop.DetailsUrl, new FormUrlEncodedContent(form)).ConfigureAwait(false); // one at a time, 2 s apart
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        var items = WorkshopItem.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return items.TryGetValue(id, out var item) ? item : null;
    }

    /// <summary>Already in the server's cache: its content folder holds a .vpk.</summary>
    public bool Installed(long id) =>
        Dir() is { } dir && Directory.Exists(Path.Combine(dir, "content", "730", id.ToString())) && Directory.EnumerateFiles(Path.Combine(dir, "content", "730", id.ToString()), "*.vpk", SearchOption.AllDirectories).Any()
        && !(current is { } c && c.Id == id && Staged(id) > 0);

    /// <summary>Download it, now or after the one that runs. False when it is queued behind another.</summary>
    public bool Start(long id, long bytes)
    {
        failed.Remove(id);
        if (current is { } c && c.Id == id) return true;
        if (current != null) { if (!waiting.Any(w => w.Id == id)) waiting.Add((id, bytes)); return false; }
        Begin(id, bytes);
        return true;
    }

    void Begin(long id, long bytes)
    {
        current = (id, bytes, DateTime.UtcNow, false);
        Server.ExecuteCommand($"mm_download_addon {id}");
        log.LogInformation("Over Strike 2 Workshop: downloading {Id} ({Bytes} bytes) for a player's pick - mm_download_addon", id, bytes);
    }

    /// <summary>Bytes of this item in the cache's staging folders (downloads/, temp/) - how far its download is.</summary>
    long Staged(long id)
    {
        if (Dir() is not { } dir) return 0;
        long sum = 0;
        foreach (var stage in new[] { "downloads", "temp" })
        {
            var root = Path.Combine(dir, stage);
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var folder in Directory.EnumerateDirectories(root, id.ToString(), SearchOption.AllDirectories))
                    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) sum += new FileInfo(file).Length;
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return sum;
    }

    /// <summary>What the Steam client's workshop log says about this id since the download began: requested yet, and its result.</summary>
    (bool Requested, string? Result) Logged(long id, DateTime since)
    {
        try
        {
            using var f = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            f.Seek(Math.Max(0, f.Length - 131072), SeekOrigin.Begin);
            var text = new StreamReader(f).ReadToEnd();
            var requested = false;
            string? result = null;
            foreach (Match m in Regex.Matches(text, $@"^\[(?<at>[\d-]+ [\d:]+)\] \[AppID 730\] Download item {id} (?:(?<req>requested) by app|result : (?<res>.*?))\s*$", RegexOptions.Multiline))
            {
                if (!DateTime.TryParse(m.Groups["at"].Value, out var at) || at.ToUniversalTime() < since.AddSeconds(-5)) continue;
                if (m.Groups["req"].Success) requested = true; else result = m.Groups["res"].Value;
            }
            return (requested, result);
        }
        catch (IOException) { return (false, null); }
        catch (UnauthorizedAccessException) { return (false, null); }
    }

    /// <summary>Where this id's download is: waiting, running (percent, Steam still busy with its once-a-boot cache check), installed, or
    /// failed (why). Moves the queue on when the running one ends.</summary>
    public (WorkshopCard.Step Step, int Percent, bool SteamBusy, string? Why) Status(long id, DateTime now)
    {
        if (failed.TryGetValue(id, out var why)) return (WorkshopCard.Step.Failed, 0, false, why);
        if (current is not { } c || c.Id != id)
            return waiting.Any(w => w.Id == id) ? (WorkshopCard.Step.Waiting, 0, false, null)
                : Installed(id) ? (WorkshopCard.Step.Installed, 100, false, null) : (WorkshopCard.Step.Ready, 0, false, null);
        var (requested, result) = Logged(id, c.Since);
        if (result != null)
        {
            Next();
            if (result.Equals("OK", StringComparison.OrdinalIgnoreCase) || Installed(id)) return (WorkshopCard.Step.Installed, 100, false, null);
            failed[id] = $"Steam said '{Hud.Clean(result, 40)}'";
            log.LogWarning("Over Strike 2 Workshop: download of {Id} failed: {Result}", id, result);
            return (WorkshopCard.Step.Failed, 0, false, failed[id]);
        }
        if (!requested && now - c.Since > TimeSpan.FromSeconds(20) && Installed(id)) { Next(); return (WorkshopCard.Step.Installed, 100, false, null); } // nothing to fetch: it was in the cache
        if (now - c.Since > DownloadLimit)
        {
            Next();
            failed[id] = "the download did not finish within 30 minutes";
            return (WorkshopCard.Step.Failed, 0, false, failed[id]);
        }
        var percent = c.Bytes > 0 ? (int)Math.Min(99, Staged(id) * 100 / c.Bytes) : 0;
        return (WorkshopCard.Step.Downloading, percent, !requested && percent == 0 && now - c.Since > SteamBusyAfter, null);
    }

    /// <summary>Every frame: the running download's end moves the queue on, whether or not a card of that id still asks (a search cleared, a map change).</summary>
    public void Tick(DateTime now) { if (current is { } c) Status(c.Id, now); }

    void Next()
    {
        current = null;
        if (waiting.Count == 0) return;
        var (id, bytes) = waiting[0];
        waiting.RemoveAt(0);
        Begin(id, bytes);
    }

    public string Describe() => current is { } c ? $"downloading {c.Id} for {(DateTime.UtcNow - c.Since).TotalSeconds:0} s, {waiting.Count} waiting" : "no download";
}
