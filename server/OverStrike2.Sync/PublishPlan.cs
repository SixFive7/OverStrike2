using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>THE PUBLISH'S PLAN: what the bridge's files become - pure, nothing written here - and the rules it keeps: the live files plus
/// additions only, and one search text a map.</summary>
public static class PublishPlan
{
    /// <summary>[] when the new text of a bridge file is the live one plus additions only: the publish never rewrites what is there (maps.json
    /// and broken.json's lists keep every old entry, in place; search.json every old text; a mode list every id).</summary>
    public static List<string> MergeProblems(string name, byte[]? oldRaw, string newText)
    {
        if (oldRaw is null)
            return [];
        JsonNode? old, fresh;
        try
        {
            old = Node.Parse(oldRaw, name);
            fresh = Node.Parse(newText, name);
        }
        catch (BadDataException e)
        {
            return [$"{name}: does not parse ({e.Message})"];
        }
        var problems = new List<string>();
        if (name == "maps.json")
        {
            var o = Node.Arr(old);
            var n = Node.Arr(fresh);
            if (n.Count < o.Count || Enumerable.Range(0, o.Count).Any(i => !Node.Eq(n[i], o[i])))
                problems.Add("maps.json: an existing entry changed or moved");
            var ids = n.OfType<JsonObject>().Select(e => Node.Str(e["id"])).ToList();
            var ranks = n.OfType<JsonObject>().Select(e => Node.SetKey(e["rank"])).ToList();
            if (ids.Distinct().Count() != ids.Count || ranks.Distinct().Count() != ranks.Count)
                problems.Add("maps.json: an id or a rank is there twice");
        }
        else if (name == "broken.json")
        {
            var n = Node.Obj(fresh);
            foreach (var (k, v) in Node.Obj(old))
            {
                if (v is JsonArray list)
                {
                    var now = n[k] is JsonArray a && a.Count > 0 ? a : [];
                    if (now.Count < list.Count || Enumerable.Range(0, list.Count).Any(i => !Node.Eq(now[i], list[i])))
                        problems.Add($"broken.json: the {k} list lost or changed an entry");
                }
                else if (!Node.Eq(n[k], v))
                    problems.Add($"broken.json: {k} changed");
            }
        }
        else if (name == "search.json")
        {
            var n = Node.Obj(fresh);
            if (Node.Obj(old).Any(kv => !Node.Eq(n[kv.Key], kv.Value)))
                problems.Add("search.json: an existing text changed");
        }
        else
        {
            static HashSet<string> Keys(JsonNode? list) => [.. Node.Arr(list).OfType<JsonObject>().Select(e => Node.Truthy(e["id"]) ? Node.Str(e["id"]) : "official:" + Node.Str(e["name"]))];
            if (!Keys(old).IsSubsetOf(Keys(fresh)))
                problems.Add($"{name}: a map left the list");
        }
        return problems;
    }

    /// <summary>An item's spawn points per side as its scan counted them (Scan.Item's "spawns"), or null: no scan, none counted, or none on a side.</summary>
    public static (int T, int Ct)? SpawnsOf(JsonObject? rec) =>
        Node.Get(rec?["scan"], "spawns") is JsonObject s && Node.Int(s["t"]) is var t and > 0 && Node.Int(s["ct"]) is var ct and > 0 ? ((int)t, (int)ct) : null;

    /// <summary>What a publish writes. Pure: returns ({file name: new text}, summary); nothing is written here. outcomes: the items this run
    /// decided. removedNow: [(id, verdict)] of listed maps.</summary>
    public static (Dictionary<string, string> Texts, JsonObject Summary) Of(BridgeData files, SyncState state, IReadOnlyList<Verdicts.Outcome> outcomes,
                                                                                    IReadOnlyList<(string Id, string Verdict)> removedNow, bool modeBackfill,
                                                                                    IReadOnlyDictionary<string, JsonObject> listedDetails)
    {
        var maps = (JsonArray)files.Maps.DeepClone();
        var broken = (JsonObject)files.Broken.DeepClone();
        var listed = new Dictionary<string, JsonObject>();
        foreach (var m in maps.OfType<JsonObject>())
        {
            var id = Node.Str(m.TryGetPropertyValue("id", out var v) ? v : "");
            if (Text.Digits(id))
                listed[id] = m;
        }
        var on = BridgeData.BrokenIds(broken);
        foreach (var wid in files.BlockedIds())
            on.TryAdd(wid, "blocked");
        var summary = new JsonObject
        {
            ["listed"] = new JsonArray(), ["modes"] = new JsonObject(), ["broken"] = new JsonArray(), ["packs"] = new JsonArray(), ["removed"] = new JsonArray(),
            ["advert"] = new JsonArray(),
        };
        JsonArray S(string key) => (JsonArray)summary[key]!;
        JsonArray Section(string key)
        {
            if (broken[key] is not JsonArray a)
            {
                a = [];
                broken[key] = a;
            }
            return a;
        }
        var outTexts = new Dictionary<string, string>();
        // broken.json: crashers, kicks and adverts under "maps"; non-maps under "packs"
        foreach (var o in outcomes)
        {
            var (wid, kind, detail, rec) = (o.Id, o.Kind, o.Detail, o.Rec);
            var name = Text.Clean(Node.StrOr(rec["title"]));
            if (name.Length == 0)
                name = $"Workshop item {wid}";
            JsonNode? rank = listed.TryGetValue(wid, out var lm) ? lm["rank"]?.DeepClone() : null;
            var placed = on.TryGetValue(wid, out var where) && where is "maps" or "packs" or "blocked";
            if (kind is "crasher" or "kick" or "advert" && !placed)
            {
                var entry = new JsonObject
                {
                    ["id"] = long.Parse(wid), ["rank"] = rank, ["name"] = name, ["crashes"] = Node.Int(detail["crashes"]), ["evidence"] = detail["evidence"]?.DeepClone(),
                };
                if (kind == "advert")
                {
                    entry["reason"] = detail["reason"]?.DeepClone();
                    S("advert").Add(Node.Array(wid, name, detail["reason"], detail["sample"]));
                }
                else if (kind == "kick")
                    entry["reason"] = "throws players off the server";
                Section("maps").Add(entry);
                on[wid] = "maps";
                S("broken").Add(Node.Array(wid, name, kind, detail["evidence"]));
            }
            else if (kind == "nonmap" && !placed && !listed.ContainsKey(wid))
            {
                Section("packs").Add(new JsonObject
                {
                    ["id"] = long.Parse(wid), ["name"] = name, ["kind"] = detail["kind"]?.DeepClone(), ["evidence"] = "confirmed by test", ["phrase"] = null,
                    ["detail"] = detail["detail"]?.DeepClone(), ["list"] = "sync", ["formerRank"] = null, ["spareRank"] = null,
                    ["preview"] = Node.Truthy(rec["preview"]) ? rec["preview"]?.DeepClone() : "", ["description"] = Text.Clean(Node.StrOr(rec["description"]), 200),
                });
                on[wid] = "packs";
                S("packs").Add(Node.Array(wid, name, detail["kind"], detail["detail"]));
            }
            else if (kind == "nonmap" && listed.ContainsKey(wid) && !placed)
            {
                // a listed map whose update turned it into a non-map: gate it like a crasher (a pick asks to confirm)
                var listedName = Node.Truthy(listed[wid]["name"]) ? listed[wid]["name"]?.DeepClone() : name;
                Section("maps").Add(new JsonObject
                {
                    ["id"] = long.Parse(wid), ["rank"] = rank, ["name"] = listedName, ["crashes"] = 0, ["evidence"] = detail["detail"]?.DeepClone(),
                    ["reason"] = "is no longer a playable map",
                });
                on[wid] = "maps";
                S("broken").Add(Node.Array(wid, name, "nonmap", detail["detail"]));
            }
        }
        // removed listed maps go to the bridge as this run's verdicts (POST /removed), which blocks them at once; broken.json holds no list
        foreach (var (wid, verdict) in removedNow)
            if (listed.TryGetValue(wid, out var m))
                S("removed").Add(Node.Array(wid, m["rank"], Node.Truthy(m["name"]) ? m["name"] : "", verdict));
        if (!Node.Eq(broken, files.Broken))
            outTexts["broken.json"] = Node.File(broken, 2);
        // maps.json: the Arms Race passes that are new, appended in popularity order (a number, once given, keeps its map)
        var newAr = outcomes.Where(o => o.Kind == "pass" && !listed.ContainsKey(o.Id) && !on.ContainsKey(o.Id) && Node.In(o.Rec["tags"], "Armsrace"))
            .OrderBy(o => Verdicts.PopularityKey(o.Rec, o.Id)).ToList();
        var rankNow = maps.OfType<JsonObject>().Select(m => Node.Int(m["rank"])).DefaultIfEmpty(0).Max();
        var search = files.Search is null ? null : (JsonObject)files.Search.DeepClone();
        foreach (var o in newAr)
        {
            rankNow++;
            var name = Text.Clean(Node.StrOr(o.Rec["title"]));
            var entry = new JsonObject
            {
                ["rank"] = rankNow, ["id"] = long.Parse(o.Id), ["name"] = name.Length > 0 ? name : $"Workshop item {o.Id}", ["sizeMb"] = Text.SizeMb(o.Rec["size"]),
            };
            maps.Add(entry);
            listed[o.Id] = entry;
            S("listed").Add(Node.Array(o.Id, rankNow, entry["name"], entry["sizeMb"]));
            if (search is not null)
                search[o.Id] = SearchBlob(Node.Str(entry["name"]), o.Id, Node.StrList(o.Rec["tags"]), Node.StrOr(o.Rec["description"]));
        }
        if (newAr.Count > 0)
        {
            outTexts["maps.json"] = Node.File(maps, 1);
            if (search is not null)
                outTexts["search.json"] = Node.File(search, 0);
        }
        // the mode lists: the maps.json entry shape, rank 1..N by popularity, never shrinking
        var recs = new Dictionary<string, JsonObject>();      // an item this run decided has its record here, the rest in the state
        foreach (var o in outcomes)
            recs[o.Id] = o.Rec;
        foreach (var (mode, tag, _, _, fname) in GameModes.All.Skip(1))
        {
            var current = files.ModeLists.TryGetValue(mode, out var cur) ? cur : [];
            var entries = new Dictionary<string, JsonObject>();
            foreach (var e in current.OfType<JsonObject>())
            {
                var id = Node.Str(e.TryGetPropertyValue("id", out var v) ? v : "");
                if (Text.Digits(id))
                    entries[id] = (JsonObject)e.DeepClone();
                else if (Node.Truthy(e["official"]))
                    entries["official:" + Node.Str(e["name"])] = (JsonObject)e.DeepClone();
            }
            var pop = new Dictionary<string, JsonObject>();
            foreach (var o in outcomes)
            {
                if (o.Kind == "pass" && Node.In(o.Rec["tags"], tag) && !on.ContainsKey(o.Id) && !entries.ContainsKey(o.Id))
                {
                    var name = Text.Clean(Node.StrOr(o.Rec["title"]));
                    entries[o.Id] = new JsonObject { ["id"] = long.Parse(o.Id), ["name"] = name.Length > 0 ? name : $"Workshop item {o.Id}", ["sizeMb"] = Text.SizeMb(o.Rec["size"]) };
                    pop[o.Id] = o.Rec;
                    ModeRows(summary, mode).Add(Node.Array(o.Id, entries[o.Id]["name"]));
                }
            }
            if (modeBackfill)
            {
                foreach (var (wid, m) in listed)
                {
                    if (!listedDetails.TryGetValue(wid, out var item) || entries.ContainsKey(wid) || on.ContainsKey(wid) || Text.WorkshopVerdict(item) != "ok")
                        continue;
                    if (Text.ItemTags(item).Contains(tag))
                    {
                        entries[wid] = new JsonObject
                        {
                            ["id"] = long.Parse(wid), ["name"] = Node.Truthy(m["name"]) ? m["name"]?.DeepClone() : Text.ItemName(item), ["sizeMb"] = Node.Int(m["sizeMb"]),
                        };
                        pop[wid] = new JsonObject { ["subs"] = item["subscriptions"]?.DeepClone(), ["lifetime"] = item["lifetime_subscriptions"]?.DeepClone() };
                        if (summary["backfill"] is not JsonObject fill)
                        {
                            fill = new JsonObject();
                            summary["backfill"] = fill;
                        }
                        fill[mode] = Node.Int(fill[mode]) + 1;
                    }
                }
            }
            foreach (var wid in entries.Keys.Where(Text.Digits).ToList())
            {
                if (pop.ContainsKey(wid))
                    continue;
                var item = listedDetails.TryGetValue(wid, out var it) ? it : [];
                var old = state.Item(wid) ?? [];
                pop[wid] = new JsonObject
                {
                    ["subs"] = item.ContainsKey("subscriptions") ? item["subscriptions"]?.DeepClone() : old["subs"]?.DeepClone(),
                    ["lifetime"] = item.ContainsKey("lifetime_subscriptions") ? item["lifetime_subscriptions"]?.DeepClone() : old["lifetime"]?.DeepClone(),
                };
            }
            var ids = entries.Keys.Where(Text.Digits).OrderBy(w => (-Node.Int(pop[w]["subs"]), -Node.Int(pop[w]["lifetime"]), long.Parse(w))).ToList();
            var newList = new JsonArray();
            for (var k = 0; k < ids.Count; k++)
            {
                var entry = new JsonObject
                {
                    ["rank"] = k + 1, ["id"] = long.Parse(ids[k]), ["name"] = entries[ids[k]]["name"]?.DeepClone(), ["sizeMb"] = Node.Int(entries[ids[k]]["sizeMb"]),
                };
                // Wingman's: the map's spawn points per side, as the scan counted them in its files (Spawns.cs) - the bridge lets a map take as many a side
                if (mode == "wingman" && SpawnsOf(recs.GetValueOrDefault(ids[k]) ?? state.Item(ids[k])) is var (t, ct))
                    (entry["spawnsT"], entry["spawnsCt"]) = (t, ct);
                newList.Add(entry);
            }
            foreach (var key in entries.Keys.Where(k => !Text.Digits(k)))
                newList.Add(entries[key].DeepClone());
            if (current.Count > 0 && newList.Count < current.Count)
                newList = (JsonArray)current.DeepClone();       // never shrink (the bridge refuses a list that drops below half anyway)
            if (!Node.Eq(newList, current) && newList.Count > 0)
            {
                outTexts[fname] = Node.File(newList, 1);
                ModeRows(summary, mode);
                if (summary["mode_totals"] is not JsonObject totals)
                {
                    totals = new JsonObject();
                    summary["mode_totals"] = totals;
                }
                totals[mode] = newList.Count;
            }
        }
        // search.json: the mode lists' maps have their texts too, since the HUD's Maps page browses every mode: a map with none gets it now,
        // from this run's outcome or the state, so this also brings the maps listed before it (old texts stay, MergeProblems)
        if (search is not null)
        {
            var missing = 0;
            foreach (var (mode, _, _, _, fname) in GameModes.All.Skip(1))
            {
                var list = outTexts.TryGetValue(fname, out var t) ? Node.Arr(Node.Parse(t, fname)) : files.ModeLists.TryGetValue(mode, out var l) ? l : [];
                foreach (var e in list)
                {
                    var wid = e is JsonObject eo ? Node.Str(eo.TryGetPropertyValue("id", out var v) ? v : "") : "";
                    if (!Text.Digits(wid) || search.ContainsKey(wid))
                        continue;
                    var rec = recs.TryGetValue(wid, out var r) ? r : state.Item(wid) ?? [];
                    var name = Node.Truthy(Node.Get(e, "name")) ? Node.Str(Node.Get(e, "name")) : Node.StrOr(rec["title"]);
                    search[wid] = SearchBlob(name, wid, Node.StrList(rec["tags"]), Node.StrOr(rec["description"]));
                    missing++;
                }
            }
            if (missing > 0)
                outTexts["search.json"] = Node.File(search, 0);
        }
        return (outTexts, summary);
    }

    static JsonArray ModeRows(JsonObject summary, string mode)
    {
        var modes = (JsonObject)summary["modes"]!;
        if (modes[mode] is not JsonArray rows)
        {
            rows = [];
            modes[mode] = rows;
        }
        return rows;
    }

    /// <summary>The search text of a map: core's SearchText.Of, the rule the bridge's --search-index follows too - its name's words, its
    /// id, its tags' words, and the start of its description's.</summary>
    public static string SearchBlob(string name, string wid, IEnumerable<string> tags, string description) => SearchText.Of(name, wid, tags, description);
}
