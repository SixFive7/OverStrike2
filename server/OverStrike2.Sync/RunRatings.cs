// A run's ratings: the Workshop's scores, subscriptions and Most Popular orders for the keep set's top lists, and ratings.json.
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

public sealed partial class SyncRun
{
    /// <summary>2b. Once every RatingsEvery - and at once when the ratings kept lack a list the keep set needs - the three lists the keep set
    /// takes each mode's best from: the Workshop's score (vote_data.score) and the current subscriptions of every listed map, every mode, and
    /// of every map still waiting for its test (GetDetails with includevotes, 100 a call); and each mode's Most Popular order, Steam's trend
    /// over the last TrendDays days (QueryFiles RankedByTrend by the mode's tag, pages of 100 until TrendWant of the mode's listed maps were in
    /// it, at most TrendPages). Kept in the state and written to the bridge's data as ratings.json. A run cut short merges what it got into
    /// what was there and keeps the old time: the keep set refuses deletions on ratings over 3 days old, so their age must stay honest.</summary>
    public void Ratings(IWorkshopApi api, IReadOnlyList<string> listed)
    {
        if (State.Data["ratings"] is not JsonObject st)
        {
            st = new JsonObject { ["fetched"] = 0, ["scores"] = new JsonObject() };
            State.Data["ratings"] = st;
        }
        var rep = Section("ratings", () => []);
        if (!Cfg.Ratings)
        {
            rep["skipped"] = "sync.ratings is off";
            return;
        }
        var age = Clock.Now() - Node.Float(st["fetched"]);
        var sample = Node.Obj(st["scores"]).Select(kv => kv.Value).Take(50).ToList();
        var whole = sample.Count > 0 && sample.All(v => v is JsonArray a && a.Count > 3)
                    && GameModes.All.All(m => Node.Get(st["trend"], m.Mode) is JsonArray);
        if (whole && age < RatingsEvery)
        {
            rep["skipped"] = $"fetched {Text.Hms(age)} ago, again after {Text.Hms(RatingsEvery)}";
            if (!File.Exists(Path.Combine(Cfg.BridgeData, "ratings.json")))
                WriteRatings(st);
            return;
        }
        var tags = new HashSet<string>(Cfg.Tags);
        var waiting = State.Items.Where(kv => kv.Value is JsonObject r && Node.Str(r["status"]) is "queued" or "retry" && Node.Str(r["ws"]) == "ok"
                                             && Node.StrList(r["tags"]).Any(tags.Contains)).Select(kv => kv.Key).ToList();
        var ids = listed.Concat(waiting).Distinct().ToList();
        var inputs = KeepInputs();
        var members = new Dictionary<string, HashSet<string>>();
        foreach (var (mode, _, _, _, name) in GameModes.All)
            members[mode] = [.. Node.Arr(inputs.GetValueOrDefault(name)).OfType<JsonObject>().Select(e => Node.StrOr(e["id"])).Where(Text.Digits)];
        var got = new Dictionary<string, JsonArray>();
        var trend = new Dictionary<string, List<string>>();
        var walked = new Dictionary<string, (int Read, int Listed)>();
        var failed = 0;
        try
        {
            for (var start = 0; start < ids.Count; start += Discovery.DetailsBatch)
            {
                if (Clock.Now() >= Deadline - 1200)
                {
                    rep["stop"] = "the run's deadline";
                    break;
                }
                var batch = ids.Skip(start).Take(Discovery.DetailsBatch).ToList();
                try
                {
                    foreach (var (k, v) in api.Votes(batch))
                        got[k] = v;
                }
                catch (ApiErrorException e)
                {
                    failed++;
                    L($"scores of {batch.Count} ids failed for good ({e.Message})");
                }
            }
            foreach (var (mode, tag, _, _, _) in GameModes.All)
            {
                if (Node.Truthy(rep["stop"]))
                    break;
                var order = new List<string>();
                var cursor = "*";
                var hits = 0;
                for (var page = 0; page < TrendPages; page++)
                {
                    List<string> pageIds;
                    string next;
                    try
                    {
                        (pageIds, next, _) = api.Trend(tag, cursor, TrendDays);
                    }
                    catch (ApiErrorException e)
                    {
                        failed++;
                        L($"Most Popular of {mode}, page {page + 1}, failed for good ({e.Message})");
                        break;
                    }
                    order.AddRange(pageIds.Where(w => !order.Contains(w)).Distinct());
                    hits = order.Count(members[mode].Contains);
                    if (hits >= TrendWant || pageIds.Count == 0 || next.Length == 0 || next == cursor)
                        break;
                    cursor = next;
                }
                trend[mode] = order;
                walked[mode] = (order.Count, hits);
            }
        }
        catch (StopApiException stop)
        {
            rep["stop"] = stop.Message;
            L($"ratings stopped: {stop.Message}");
        }
        var complete = !Node.Truthy(rep["stop"]) && failed == 0 && trend.Count == GameModes.All.Length;
        var gotObject = new JsonObject();
        foreach (var (k, v) in got)
            gotObject[k] = v.DeepClone();
        var trendObject = new JsonObject();
        foreach (var (k, v) in trend)
            trendObject[k] = Node.Strings(v);
        if (complete)
        {
            st["scores"] = gotObject;
            st["trend"] = trendObject;
            st["fetched"] = Clock.Now();
            st["trend_days"] = TrendDays;
        }
        else
        {
            if (got.Count > 0)
            {
                var merged = Node.Obj(st["scores"]?.DeepClone());
                foreach (var (k, v) in gotObject)
                    merged[k] = v?.DeepClone();
                st["scores"] = merged;
            }
            if (trend.Count > 0)
            {
                var merged = Node.Obj(st["trend"]?.DeepClone());
                foreach (var (k, v) in trendObject)
                    merged[k] = v?.DeepClone();
                st["trend"] = merged;
            }
        }
        rep["asked"] = ids.Count;
        rep["listed"] = listed.Count;
        rep["waiting"] = waiting.Count;
        rep["scored"] = got.Count;
        rep["failed"] = failed;
        rep["complete"] = complete;
        rep["calls"] = api.ByApi.GetValueOrDefault("GetDetails");
        var walkedObject = new JsonObject();
        foreach (var (m, v) in walked)
            walkedObject[m] = new JsonArray(v.Read, v.Listed);
        rep["trend"] = walkedObject;
        rep["trend_days"] = TrendDays;
        L($"ratings: {got.Count} of {ids.Count} scored with their subscriptions ({listed.Count} listed, {waiting.Count} waiting) in {Node.Str(rep["calls"])} call(s); "
          + $"Most Popular over {TrendDays} days: {(walked.Count > 0 ? string.Join(", ", walked.Select(kv => $"{kv.Key} {kv.Value.Read} read, {kv.Value.Listed} listed")) : "none")}"
          + $"{(complete ? "" : " - INCOMPLETE, the old time kept")}");
        State.Save();
        cut = null;
        if (got.Count > 0 || trend.Count > 0)
            WriteRatings(st);
    }

    /// <summary>&lt;bridge&gt;/ratings.json, version 1: {"fetched": unix time, "scores": {id: [score, votes up, votes down, subscriptions]},
    /// "trend": {mode: [ids in Most Popular order]}, "trend_days": days}.</summary>
    public void WriteRatings(JsonObject st)
    {
        var text = Node.Compact(new JsonObject
        {
            ["version"] = 1, ["fetched"] = Node.Truthy(st["fetched"]) ? st["fetched"]!.DeepClone() : 0,
            ["source"] = "IPublishedFileService/GetDetails vote_data and subscriptions; QueryFiles RankedByTrend",
            ["trend_days"] = Node.Truthy(st["trend_days"]) ? st["trend_days"]!.DeepClone() : TrendDays,
            ["scores"] = Node.Truthy(st["scores"]) ? st["scores"]!.DeepClone() : new JsonObject(), ["trend"] = Node.Truthy(st["trend"]) ? st["trend"]!.DeepClone() : new JsonObject(),
        }) + "\n";
        if (!Cfg.Publish)
        {
            Section("ratings", () => [])["file"] = "not written (sync.publish is off)";
            return;
        }
        try
        {
            Files.WriteAtomic(Path.Combine(Cfg.BridgeData, "ratings.json"), text, FileModes.Shared);
            Section("ratings", () => [])["file"] = "ratings.json written";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Errors.Add($"ratings.json not written: {e.Message}");
        }
    }
}
