// A run's discovery and check: the Workshop's new maps found, every listed map and the head of the download queue asked about.
using System.Text.Json.Nodes;

namespace OverStrike2.Sync;

public sealed partial class SyncRun
{
    /// <summary>Discovery: the Workshop's newest items in the sync's tags (QueryFiles, the Web API key), page by page until a page holds
    /// nothing this sync has seen (Discovery.Discover); each new one queued for its test. Nothing without a key.</summary>
    void DiscoverStep(SteamApi api, IReadOnlyDictionary<string, string> known)
    {
        if (api.Key is null)
        {
            L("discovery skipped: no Steam Web API key (sync.webApiKey) - the other steps run");
            Report["discovery"] = new JsonObject { ["skipped"] = "no Web API key in the secret file" };
            return;
        }
        try
        {
            var d = Discovery.Discover(api, Cfg, known, State, Day0, Deadline - 1800, Clock.Now);
            Report["discovery"] = d;
            L($"discovery: {Node.Str(d["pages"])} page(s), {Node.Str(d["seen"])} seen, {Node.Arr(d["new"]).Count} new ({Node.Str(d["stop"])})");
        }
        catch (StopApiException stop)
        {
            Report["discovery"] = new JsonObject { ["skipped"] = stop.Message };
            L($"discovery stopped: {stop.Message}");
        }
        catch (ApiErrorException e)
        {
            Report["discovery"] = new JsonObject { ["skipped"] = $"failed: {e.Message}", ["failed"] = true };
        }
        State.Save();
    }

    /// <summary>The check: every listed map, then the head of the download queue, asked about (GetPublishedFileDetails, no key) - a listed
    /// map gone from the Workshop is reported to the bridge, one changed is queued for its test again, the queue's head gets the Workshop's
    /// numbers; workshop.json is written after it. Returns the answers by id, and the listed maps gone (none when more than 10% of 50 or
    /// more seem to be: that is not believed).</summary>
    (Dictionary<string, JsonObject> Details, List<(string Id, string Verdict)> Removed) CheckStep(SteamApi api, BridgeData files,
                                                                                              IReadOnlyDictionary<string, string> known, List<string> listed)
    {
        var details = new Dictionary<string, JsonObject>();
        var queueHead = Queue(known, details).Take(QueueCheck).ToList();
        var ids = listed.Concat(queueHead).Distinct().ToList();
        var chk = new JsonObject { ["asked"] = ids.Count };
        try
        {
            var (got, left) = Seams.Check(api, ids, Deadline - 1500, Clock.Now, L);
            details = got;
            chk["calls"] = api.ByApi.GetValueOrDefault("GetPublishedFileDetails");
            chk["left"] = left.Count;
        }
        catch (StopApiException stop)
        {
            chk["skipped"] = stop.Message;
            L($"check stopped: {stop.Message}");
        }
        var judged = details.Count > 0 ? Discovery.JudgeListed(details, listed, State, Cfg) : ([], [], listed.Count, false);
        List<(string Id, string Verdict)> removedNow = judged.Suspicious ? [] : [.. judged.Removed];
        // the bridge's verdicts so far (removed.json is this sync's, kept by the bridge): a map that goes is an event once
        var bridgeVerdicts = Node.Get(Node.ReadJson(Path.Combine(Cfg.BridgeData, "removed.json")), "verdicts") as JsonObject ?? [];
        var newly = removedNow.Where(rv => !(bridgeVerdicts[rv.Id] is { } v && Node.IsString(v) && Node.Str(v) == rv.Verdict)).ToList();
        chk["removed"] = new JsonArray(removedNow.Select(rv => (JsonNode?)Node.Array(rv.Id, rv.Verdict)).ToArray());
        chk["changed"] = Node.Strings(judged.Changed);
        chk["suspicious"] = judged.Suspicious;
        if (!judged.Suspicious)
            VerdictsToReport = listed.Where(details.ContainsKey).Select(w => (w, Text.WorkshopVerdict(details[w]))).ToList();
        Report["check"] = chk;
        var failedToday = new List<string>();
        foreach (var wid in judged.Changed)
        {
            if (!details.TryGetValue(wid, out var item))
                continue;
            if (State.FailedToday(wid, item, Day0))
            {
                failedToday.Add(wid);                 // its next test is tomorrow's: nothing to download, nothing waits
                continue;
            }
            var before = State.Item(wid);
            var was = before is null ? null : Node.Str(before["status"]);
            var sameFailed = before is not null && Node.Truthy(before["verdict"]) && Node.Str(before["verdict"]) != "pass"
                             && Node.Int(item["time_updated"]) <= Node.Int(before["updated"]);
            var rec = State.Remember(item, Day0, "changed");
            if (Node.Str(rec["status"]) == "queued" && was != "queued")   // one event when it is queued, not one at every check it waits
                Event(wid, "changed", sameFailed ? "This version failed its test: it is tested again, once a day." : "It changed on the Workshop: it is tested again.");
        }
        if (failedToday.Count > 0)
            chk["failed_today"] = Node.Strings(failedToday);
        foreach (var (wid, verdict) in newly)         // one event when a map goes, not one at every check
            Event(wid, "removed", $"The Workshop says it is {(verdict == "removed" ? "gone" : verdict)}: it is blocked.");
        foreach (var wid in queueHead)
            if (details.TryGetValue(wid, out var item))
                State.Remember(item, Day0, Node.StrOr(State.Item(wid)?["why"], "new"));
        foreach (var (wid, item) in details)
            State.NoteWorkshop(wid, item);
        State.Save();
        L($"check: {ids.Count} asked, {judged.Removed.Count} gone ({newly.Count} newly), {judged.Changed.Count} changed"
          + (failedToday.Count > 0 ? $" ({failedToday.Count} failed their test today: again tomorrow)" : "") + (judged.Suspicious ? " - NOT BELIEVED (over 10%)" : ""));
        WriteWorkshop(files);
        if (api.StopReason is { } r1)
            Report["api_stop"] = r1;
        return (details, removedNow);
    }
}
