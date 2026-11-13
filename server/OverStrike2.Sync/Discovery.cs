using System.Globalization;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>Discovery and the check: the Workshop's new maps found, the listed maps asked about and judged.</summary>
public static class Discovery
{
    public const int DetailsBatch = 100;
    public const double WalkEvery = 7 * 86400;     // the full walk of every page: at most this often

    /// <summary>Whether discovery's next pass is the full walk: none is recorded in the state ("walk"), or the last one started WalkEvery ago
    /// or more - and the run's pages are the settings' own (a run-now's max_pages takes none).</summary>
    public static bool WalkDue(SyncState state, SyncConfig cfg, double now) => !cfg.NoWalk && now - Node.Float(Node.Get(state.Data["walk"], "at")) >= WalkEvery;

    /// <summary>QueryFiles newest first; stops at the first page with nothing this tool has never seen (or a cap, or the clock). Returns
    /// {"pages", "seen", "new": [ids], "total", "stop"}. Once a week (WalkDue) it is the full walk instead: every page to the last one, so the
    /// items newest first never reaches are found too - one gone or hidden when its pages were read and public again now, an old upload newly
    /// given one of the tags - and a queued map the check had found gone from the Workshop is queued again when it is back ("walk": whole or
    /// cut short, "back": [ids]). A walk that read every page the Workshop has, or as many as sync.maxPages allows, is the week's: "walk" in the
    /// state records it; one cut short (the clock, a stop of the Web API) is not, and the next pass walks again. Every call goes through the
    /// same door, ledger and caps as any other.</summary>
    public static JsonObject Discover(IWorkshopApi api, SyncConfig cfg, IReadOnlyDictionary<string, string> known, SyncState state, string day,
                                      double deadline, Func<double> now)
    {
        var started = now();
        var walk = WalkDue(state, cfg, started);
        var pages = 0;
        var seen = 0L;
        var fresh = new List<string>();
        var back = new List<string>();
        long? total = null;
        string stop;
        var whole = false;                      // every page the Workshop has, or sync.maxPages of them
        var cursor = "*";
        while (true)
        {
            if (pages >= cfg.MaxPages)
            {
                stop = $"sync.maxPages ({cfg.MaxPages})";
                whole = true;
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
                if (!Text.Digits(wid) || known.ContainsKey(wid))
                    continue;
                if (state.Item(wid) is { } rec)
                {
                    // the walk: a queued map the check found gone from the Workshop drops out of the queue, and no check asks about it again;
                    // public on this page, it is remembered anew, its verdict ok, and queued again
                    if (walk && Node.Str(rec["ws"]) != "ok" && Node.Str(rec["status"]) is "queued" or "retry"
                        && Node.Str(state.Remember(item, day, Node.StrOr(rec["why"], "new"))["ws"]) == "ok")
                        back.Add(wid);
                    continue;
                }
                if (cfg.ExcludeCreators.Contains(Node.StrOr(item["creator"])))
                    continue;                       // listed by hand (sync.excludeCreators): never a candidate, never "new"
                state.Remember(item, day, "new");
                page.Add(wid);
            }
            fresh.AddRange(page);
            if (items.Count == 0)
            {
                stop = "an empty page";
                whole = true;
                break;
            }
            if (page.Count == 0 && !walk)
            {
                stop = $"page {pages} held nothing new";
                break;
            }
            if (next.Length == 0 || next == cursor)
            {
                stop = "the last page";
                whole = true;
                break;
            }
            cursor = next;
        }
        var result = new JsonObject { ["pages"] = pages, ["seen"] = seen, ["new"] = Node.Strings(fresh), ["total"] = total, ["stop"] = stop };
        if (walk)
        {
            result["walk"] = whole ? "whole" : "cut short";
            result["back"] = Node.Strings(back);
            if (whole)
                state.Data["walk"] = new JsonObject
                {
                    ["at"] = started, ["day"] = day, ["pages"] = pages, ["seen"] = seen, ["total"] = total, ["new"] = fresh.Count, ["back"] = back.Count, ["stop"] = stop,
                };
        }
        return result;
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
            ? UnixTime.Seconds(new DateTimeOffset(d, TimeSpan.Zero))
            : double.PositiveInfinity;

    /// <summary>The check's verdicts for the listed maps: {"removed": [(id, verdict)], "changed": [ids], "missing": n, "suspicious": bool}.
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
