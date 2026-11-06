using System.Globalization;
using System.Text.Json.Nodes;

namespace OverStrike2.Sync;

/// <summary>1. Discovery and 2. the check.</summary>
public static class Discovery
{
    public const int DetailsBatch = 100;

    /// <summary>QueryFiles newest first; stops at the first page with nothing this tool has never seen (or a cap, or the clock). Returns
    /// {"pages", "seen", "new": [ids], "total", "stop"}.</summary>
    public static JsonObject Discover(IWorkshopApi api, SyncConfig cfg, IReadOnlyDictionary<string, string> known, SyncState state, string day,
                                      double deadline, Func<double> now)
    {
        var pages = 0;
        var seen = 0L;
        var fresh = new List<string>();
        long? total = null;
        string? stop = null;
        var cursor = "*";
        while (true)
        {
            if (pages >= cfg.MaxPages)
            {
                stop = $"sync.maxPages ({cfg.MaxPages})";
                break;
            }
            if (now() >= deadline)
            {
                stop = "the run's deadline";
                break;
            }
            var (items, next, count) = api.QueryFiles(cfg.Tags, cursor, DetailsBatch);
            pages++;
            total = count;
            seen += items.Count;
            var page = new List<string>();
            foreach (var item in items)
            {
                var wid = Node.StrOr(item["publishedfileid"]);
                if (!Node.Digits(wid) || known.ContainsKey(wid) || state.Item(wid) is not null)
                    continue;
                if (cfg.ExcludeCreators.Contains(Node.StrOr(item["creator"])))
                    continue;                       // listed by hand (sync.excludeCreators): never a candidate, never "new"
                state.Remember(item, day, "new");
                page.Add(wid);
            }
            fresh.AddRange(page);
            if (items.Count == 0)
            {
                stop = "an empty page";
                break;
            }
            if (page.Count == 0)
            {
                stop = $"page {pages} held nothing new";
                break;
            }
            if (next.Length == 0 || next == cursor)
            {
                stop = "the last page";
                break;
            }
            cursor = next;
        }
        return new JsonObject { ["pages"] = pages, ["seen"] = seen, ["new"] = Node.Strings(fresh), ["total"] = total, ["stop"] = stop };
    }

    /// <summary>GetPublishedFileDetails for `ids`, 100 a call. Returns ({id: item}, ids not asked because of the clock or given up).</summary>
    public static (Dictionary<string, JsonObject> Got, List<string> Left) Check(IWorkshopApi api, IReadOnlyList<string> ids, double deadline,
                                                                                 Func<double> now, Action<string> log)
    {
        var got = new Dictionary<string, JsonObject>();
        var left = new List<string>();
        for (var start = 0; start < ids.Count; start += DetailsBatch)
        {
            var batch = ids.Skip(start).Take(DetailsBatch).ToList();
            if (now() >= deadline)
            {
                left.AddRange(ids.Skip(start));
                break;
            }
            try
            {
                foreach (var (k, v) in api.Details(batch))
                    got[k] = v;
            }
            catch (ApiErrorException e)
            {
                log($"details of {batch.Count} ids failed for good ({e.Message}) - they are left out of this check");
                left.AddRange(batch);
            }
        }
        return (got, left);
    }

    /// <summary>The unix time of sync.changedSince's day (UTC midnight); with none, no listed map counts as changed at its first check.</summary>
    public static double ChangedSinceEpoch(string day) =>
        DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? SyncClock.Seconds(new DateTimeOffset(d, TimeSpan.Zero))
            : double.PositiveInfinity;

    /// <summary>The step 2 verdicts for the listed maps: {"removed": [(id, verdict)], "changed": [ids], "missing": n, "suspicious": bool}.
    /// Updates the baseline for every map that is unchanged. More than 10% of 50 or more gone is not believed (the bridge's own rule).</summary>
    public static (List<(string Id, string Verdict)> Removed, List<string> Changed, int Missing, bool Suspicious) JudgeListed(
        IReadOnlyDictionary<string, JsonObject> details, IReadOnlyList<string> listed, SyncState state, SyncConfig cfg)
    {
        var removed = new List<(string, string)>();
        var changed = new List<string>();
        var missing = 0;
        var epoch = ChangedSinceEpoch(cfg.ChangedSince);
        var baseline = state.Baseline;
        foreach (var wid in listed)
        {
            if (!details.TryGetValue(wid, out var item))
            {
                missing++;
                continue;
            }
            var verdict = Text.WorkshopVerdict(item);
            if (verdict != "ok")
            {
                removed.Add((wid, verdict));
                continue;
            }
            var updated = Node.Int(item["time_updated"]);
            if (!baseline.TryGetPropertyValue(wid, out var known) || known is null)
            {
                if (updated > epoch)
                    changed.Add(wid);
                else
                    baseline[wid] = updated;
            }
            else if (updated > Node.Int(known))
                changed.Add(wid);
        }
        var asked = listed.Count - missing;
        var suspicious = asked >= 50 && removed.Count > asked / 10;
        return (removed, changed, missing, suspicious);
    }
}
