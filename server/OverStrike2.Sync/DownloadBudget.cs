using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>The day's downloads, booked in state.json's "downloads" (so a restart of the service cannot reset them, and every run of the day
/// shares them). Only Steam's push-back stops them - a SteamCMD rate-limit answer or two Access Denied in a row end the day's
/// downloads (RefuseForToday, kept here), and a run ends its own at 3 failures in a row. The bug brake instead of a volume cap: a map
/// downloaded DownloadsPerMapDay times today is not downloaded again today.</summary>
public sealed class DownloadBudget
{
    public const int DownloadsPerMapDay = 2;
    public JsonObject Today { get; }

    public DownloadBudget(SyncState state, string day)
    {
        var book = state.Section("downloads");
        foreach (var old in book.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).SkipLast(14).ToList())
            book.Remove(old);
        if (book[day] is not JsonObject today)
        {
            today = new JsonObject { ["items"] = 0, ["bytes"] = 0 };
            book[day] = today;
        }
        Today = today;
    }

    /// <summary>Steam pushed back today: no more downloads until tomorrow.</summary>
    public bool DayOver => Node.Truthy(Today["stop"]);

    /// <summary>(ok, why): no when Steam pushed back today (DayOver); for a map, no when it was downloaded DownloadsPerMapDay times today already.</summary>
    public (bool Ok, string Why) Allows(string? wid = null)
    {
        if (DayOver)
            return (false, $"no more downloads today: {Node.Str(Today["stop"])}");
        if (wid is not null && Node.Int(Node.Get(Today["maps"], wid)) >= DownloadsPerMapDay)
            return (false, $"downloaded {DownloadsPerMapDay} times today already - not again today (the bug brake)");
        return (true, "");
    }

    public void RefuseForToday(string why) => Today["stop"] = Text.Cut(why, 200);

    public void Spend(long bytes, string? wid = null)
    {
        Today["items"] = Node.Int(Today["items"]) + 1;
        Today["bytes"] = Node.Int(Today["bytes"]) + Math.Max(0, bytes);
        if (wid is not null)
        {
            if (Today["maps"] is not JsonObject maps)
            {
                maps = new JsonObject();
                Today["maps"] = maps;
            }
            maps[wid] = Node.Int(maps[wid]) + 1;
        }
    }

    public void Fail() => Today["failed"] = Node.Int(Today["failed"]) + 1;

    /// <summary>A SteamCMD stall on this map: booked for the day, and how many it has had today.</summary>
    public long Stall(string wid)
    {
        if (Today["stalls"] is not JsonObject stalls)
        {
            stalls = new JsonObject();
            Today["stalls"] = stalls;
        }
        stalls[wid] = Node.Int(stalls[wid]) + 1;
        return Node.Int(stalls[wid]);
    }

    /// <summary>The test engine's own copy of an item SteamCMD already fetched: volume, not another item.</summary>
    public void SpendBytes(long bytes)
    {
        Today["bytes"] = Node.Int(Today["bytes"]) + Math.Max(0, bytes);
        Today["engine"] = Node.Int(Today["engine"]) + 1;
    }

    public long Items => Node.Int(Today["items"]);
    public long Bytes => Node.Int(Today["bytes"]);
    public long Engine => Node.Int(Today["engine"]);
}
