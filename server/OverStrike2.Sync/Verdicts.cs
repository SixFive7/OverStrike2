using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>Verdicts: from the scan and the test loads to what is published.</summary>
public static class Verdicts
{
    public static readonly Dictionary<string, string> AdvertReason = new()
    {
        ["gambling ad"] = "posts a gambling ad", ["chat advert"] = "advertises another server", ["client connect"] = "sends players to another server",
    };

    static readonly Dictionary<string, string> AdvertEvidence = new()
    {
        ["gambling ad"] = "posts a gambling ad - a case-site promo code in the map (scan of the map, {0})",
        ["chat advert"] = "advertises another server - the map tells everyone in chat to connect to it (scan of the map, {0})",
        ["client connect"] = "sends players to another server - the map makes their game run a connect command (scan of the map, {0})",
    };

    public const double PaceDefault = 31.0;   // seconds a map takes, download and test, until a run has measured it: 7 s + 24 s
    public const int HoursADay = 22;          // what the estimate counts on: the day without the daily restart's stretch, with a margin

    /// <summary>The item's outcome once its scan and its loads are in: (kind, detail) where kind is one of pass | advert | crasher | kick |
    /// nonmap | pending; detail is what the publish step writes.</summary>
    public static (string Kind, JsonObject Detail) Final(JsonObject rec, string day)
    {
        var scan = Node.Obj(rec["scan"]);
        var loads = Node.Arr(rec["loads"]).OfType<JsonObject>().Where(l => Node.Str(l["verdict"]) != "abort").ToList();
        var sv = scan["verdict"] is null ? null : Node.Str(scan["verdict"]);
        if (sv is "pack" or "empty" or "legacy" or "unknown")
        {
            var kind = sv switch { "pack" => "pack", "empty" => "empty", "legacy" => "legacy", _ => "stub" };
            var names = Text.Cut(string.Join(", ", Node.StrList(scan["names"])), 120);
            var detail = kind switch
            {
                "pack" => $"no maps/<name>.vpk in its VPK, only {Node.Str(scan["models"])} compiled models - an asset pack (workshop sync {day})",
                "empty" => $"no map and no models in its VPK ({Node.Str(scan["files"])} files) - nothing the engine can load (workshop sync {day})",
                "legacy" => $"a CS:GO-era upload: it holds only {names} and no CS2 VPK - CS2 cannot load it (workshop sync {day})",
                _ => $"no readable VPK index ({names}) - nothing the engine can load (workshop sync {day})",
            };
            return ("nonmap", new JsonObject { ["kind"] = kind, ["detail"] = detail });
        }
        if (loads.Count == 0)
            return ("pending", []);
        var verdicts = loads.Select(l => Node.Str(l["verdict"])).ToList();
        var passAt = verdicts.IndexOf("pass");
        if (passAt >= 0)
        {
            var first = loads[passAt];
            var advert = scan["advert"] is null ? null : Node.Str(scan["advert"]);
            if (advert is not null && AdvertReason.TryGetValue(advert, out var reason))
                return ("advert", new JsonObject
                {
                    ["reason"] = reason, ["sample"] = scan["advert_sample"]?.DeepClone(), ["evidence"] = string.Format(AdvertEvidence[advert], day),
                });
            var chat = loads.SelectMany(l => Node.StrList(l["chat"])).Where(c => Scan.AdRx().IsMatch(c) || Scan.SayConnectRx().IsMatch("say " + c)).ToList();
            if (chat.Count > 0)
                return ("advert", new JsonObject
                {
                    ["reason"] = Scan.AdRx().IsMatch(chat[0]) ? "posts a gambling ad" : "advertises another server", ["sample"] = chat[0],
                    ["evidence"] = $"showed an advert in chat on a test load: {Text.Cut(chat[0], 80)} (workshop sync {day})",
                });
            return ("pass", new JsonObject
            {
                ["level"] = first["level"]?.DeepClone(), ["mode"] = first["mode"]?.DeepClone(), ["load_s"] = first["load_s"]?.DeepClone(),
            });
        }
        if (verdicts.Contains("kick"))
            return ("kick", new JsonObject { ["evidence"] = $"threw the bots off the server on a test load (workshop sync {day})" });
        if (loads.Count < 3)
            return ("pending", []);
        var crashes = verdicts.Count(v => v == "crash");
        var freezes = verdicts.Count(v => v == "freeze");
        var down = crashes + freezes;
        if (down > 0)
        {
            var how = down == verdicts.Count ? $"all {down}" : $"{down} of {verdicts.Count}";
            var text = crashes > 0 && freezes == 0 ? $"crashes the server while loading - it went down on {how} test loads ({day})"
                : freezes > 0 && crashes == 0 ? $"freezes the server while loading - it stopped answering for 45 s on {how} test loads ({day})"
                : $"crashes or freezes the server while loading - on {how} test loads ({day})";
            return ("crasher", new JsonObject { ["crashes"] = down, ["evidence"] = text });
        }
        if (verdicts.All(v => v == "errormap"))
            return ("nonmap", new JsonObject
            {
                ["kind"] = Node.Truthy(scan["maps"]) ? "stub" : "empty",
                ["detail"] = $"the engine finds no map in it - it loaded the error map on all 3 test loads (workshop sync {day})",
            });
        var level = loads.Select(l => l["level"]).FirstOrDefault(Node.Truthy) is { } lv ? Node.Str(lv) : "";
        return ("nonmap", new JsonObject
        {
            ["kind"] = "stub",
            ["detail"] = $"never loads a level of its own{(level.Length > 0 ? $" (the engine loads {level})" : "")} - {string.Join("/", verdicts)} on its 3 test loads (workshop sync {day})",
        });
    }

    /// <summary>The search text of a map: core's SearchText.Of, the rule the bridge's --search-index follows too - its name's words, its
    /// id, its tags' words, and the start of its description's.</summary>
    public static string SearchBlob(string name, string wid, IEnumerable<string> tags, string description) => SearchText.Of(name, wid, tags, description);

    /// <summary>Most subscribed first, then most subscribed ever, then the oldest id.</summary>
    public static (long, long, long) PopularityKey(JsonNode? rec, string wid) =>
        (-Node.Int(Node.Get(rec, "subs")), -Node.Int(Node.Get(rec, "lifetime")), long.TryParse(wid, out var n) ? n : 0);

    /// <summary>The backlog, strictly most subscribed first, whatever the mode. An item tagged for several modes is one item.</summary>
    public static List<string> OrderBacklog(JsonObject items, IEnumerable<string> wids) => wids.OrderBy(w => PopularityKey(items[w], w)).ToList();

    /// <summary>How much is waiting and how long it takes: {"items", "bytes", "by_mode", "days", "pace", "hours", "per_day"}. With no daily
    /// caps and no time windows one thing bounds a day: HoursADay at the pace measured (or PaceDefault) - every hour somebody plays comes on
    /// top.</summary>
    public static JsonObject BacklogStats(JsonObject items, IReadOnlyList<string> wids, double? pace = null)
    {
        var byMode = new JsonObject();
        foreach (var (mode, tag, _, _, _) in GameModes.All)
            byMode[mode] = wids.Count(w => Node.In(Node.Get(items[w], "tags"), tag));
        var total = wids.Sum(w => Node.Int(Node.Get(items[w], "size")));
        var p = pace is { } given && given > 0 ? given : PaceDefault;
        var hours = (double)HoursADay;
        var perDay = Math.Max(1, (long)Math.Truncate(hours * 3600 / p));
        var days = wids.Count > 0 ? (wids.Count + perDay - 1) / perDay : 0;
        return new JsonObject
        {
            ["items"] = wids.Count, ["bytes"] = total, ["by_mode"] = byMode, ["days"] = days, ["pace"] = Math.Round(p, 1), ["hours"] = Math.Round(hours, 1),
            ["per_day"] = perDay,
        };
    }

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

    /// <summary>One item this run decided: its id, kind, detail and state record.</summary>
    public sealed record Outcome(string Id, string Kind, JsonObject Detail, JsonObject Rec);

    /// <summary>What a publish writes. Pure: returns ({file name: new text}, summary); nothing is written here. outcomes: the items this run
    /// decided. removedNow: [(id, verdict)] of listed maps.</summary>
    public static (Dictionary<string, string> Texts, JsonObject Summary) PlanPublish(BridgeData files, SyncState state, IReadOnlyList<Outcome> outcomes,
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
            .OrderBy(o => PopularityKey(o.Rec, o.Id)).ToList();
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
                newList.Add(new JsonObject
                {
                    ["rank"] = k + 1, ["id"] = long.Parse(ids[k]), ["name"] = entries[ids[k]]["name"]?.DeepClone(), ["sizeMb"] = Node.Int(entries[ids[k]]["sizeMb"]),
                });
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
            var recs = new Dictionary<string, JsonObject>();
            foreach (var o in outcomes)
                recs[o.Id] = o.Rec;
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
}
