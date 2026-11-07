// A run's download queue: what waits, best first - the maps the keep set would take first, new uploads, changed listed maps, then by popularity.
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

public sealed partial class SyncRun
{
    /// <summary>The download queue, best first: items created in the last 14 days (newest first), then listed maps changed since we last saw
    /// them, then everything else we have seen (most subscribed first) - and before all of them, best score first, a waiting map whose
    /// Workshop score would put it among its mode's best (the keep set's top lists), so the maps that will stay on the server are tested
    /// before the rest.</summary>
    public List<string> Queue(IReadOnlyDictionary<string, string> known)
    {
        var items = State.Items;
        var only = Cfg.OnlyIds;
        var now = Clock.Now();
        var fresh = new List<string>();
        var changed = new List<string>();
        var backlog = new List<string>();
        var tags = new HashSet<string>(Cfg.Tags);
        foreach (var (wid, node) in items)
        {
            if (node is not JsonObject rec || Node.Str(rec["status"]) is not ("queued" or "retry"))
                continue;
            if (only is not null && !only.Contains(wid))
                continue;
            if (known.ContainsKey(wid) && Node.Str(rec["why"]) != "changed")
            {
                rec["status"] = "known";
                continue;
            }
            if (Node.Str(rec["ws"]) != "ok" || !(Node.IsNumber(rec["app"]) && KeepSet.Number(rec["app"]) is 0 or 730))
                continue;
            if (Node.Truthy(rec["creator"]) && Cfg.ExcludeCreators.Contains(Node.Str(rec["creator"])))
            {
                rec["status"] = "skipped";
                rec["evidence"] = "listed by hand (its author is in sync.excludeCreators)";
                continue;
            }
            if (Node.Truthy(rec["filename"]))
            {
                rec["status"] = "skipped";
                rec["evidence"] = $"a CS:GO-era upload (its Workshop file is {Text.Cut(Node.Str(rec["filename"]), 60)}): no CS2 map, not downloaded";
                continue;
            }
            if (!Node.StrList(rec["tags"]).Any(tags.Contains))
                continue;
            if (Node.Str(rec["why"]) == "changed")
                changed.Add(wid);
            else if (now - Node.Int(rec["created"]) < 14 * Day)
                fresh.Add(wid);
            else
                backlog.Add(wid);
        }
        fresh = fresh.OrderBy(w => -Node.Int(Node.Get(items[w], "created"))).ToList();
        changed = changed.OrderBy(w => Verdicts.PopularityKey(items[w], w)).ToList();
        var ordered = fresh.Concat(changed).Concat(Verdicts.OrderBacklog(items, backlog)).ToList();
        var scores = Node.Obj(Node.Get(State.Data["ratings"], "scores"));
        var cuts = TopCutoffs();
        double? Best(string wid)
        {
            var v = scores[wid];
            var s = v is JsonArray a ? a.Count > 0 ? a[0] : null : v;
            var n = v is JsonArray b && b.Count > 3 ? b[3] : null;
            if (!Node.IsNumber(s))
                return null;
            var score = Node.Float(s);
            var itemTags = Node.Get(items[wid], "tags");
            foreach (var (tag, c) in cuts)
            {
                if (Node.In(itemTags, tag) && ((c.Score is null || score >= c.Score) || (Node.Integer(n) is { } subs && (c.Subs is null || subs >= c.Subs)) || c.Popular.Contains(wid)))
                    return score;
            }
            return null;
        }
        QFresh = [.. fresh];
        QChanged = [.. changed];
        var place = new Dictionary<string, int>();
        for (var i = 0; i < ordered.Count; i++)
            place[ordered[i]] = i;
        var top = ordered.Where(w => Best(w) is not null).OrderBy(w => -Best(w)!.Value).ThenBy(w => place[w]).ToList();
        QTop = [.. top];
        if (top.Count > 0)
            ordered = top.Concat(ordered.Where(w => !QTop.Contains(w))).ToList();
        Backlog = Verdicts.BacklogStats(items, ordered, Pace());
        return ordered;
    }

    /// <summary>{Workshop tag: (the score and the subscriptions a map needs to be among its mode's top by that list - null: the list is
    /// shorter, anything does -, the ids of Most Popular's order up to its mode's top-th listed map)}, from the keep set of the bridge's files
    /// as they are; empty without ratings. Kept for 10 minutes.</summary>
    public Dictionary<string, (double? Score, long? Subs, HashSet<string> Popular)> TopCutoffs()
    {
        if (cut is null || Clock.Now() - cut.Value.At > 600)
        {
            var inputs = KeepInputs();
            var keep = SafeKeepSet(inputs, Clock.Now());
            var trend = Node.Obj(Node.Get(inputs.GetValueOrDefault("ratings.json"), "trend"));
            var c = new Dictionary<string, (double?, long?, HashSet<string>)>();
            if (keep.Scored > 0)
            {
                foreach (var (mode, tag, _, _, _) in GameModes.All)
                {
                    var order = Node.StrList(trend[mode]);
                    var last = keep.Lists.TryGetValue(mode, out var l) ? l.Popular ?? [] : [];
                    var upto = last.Count >= Cfg.KeepRules.Top && order.Contains(last[^1]) ? order.IndexOf(last[^1]) + 1 : order.Count;
                    var (score, subs) = keep.Cutoff.TryGetValue(mode, out var co) ? co : (null, null);
                    c[tag] = (score, subs, [.. order.Take(upto)]);
                }
            }
            cut = (Clock.Now(), c);
        }
        return cut.Value.Cut;
    }

    public void ForgetCutoffs() => cut = null;
}
