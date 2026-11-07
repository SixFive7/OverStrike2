using System.Text.Json.Nodes;

namespace OverStrike2.Sync;

/// <summary>Verdicts: from the scan and the test loads to an item's outcome; and the backlog - its order and how long it takes.</summary>
public static class Verdicts
{
    /// <summary>The adverts the scan tells apart: each one's reason, as broken.json gives it, and how the map makes it.</summary>
    static readonly Dictionary<string, (string Reason, string How)> Adverts = new()
    {
        ["gambling ad"] = ("posts a gambling ad", "a case-site promo code in the map"),
        ["chat advert"] = ("advertises another server", "the map tells everyone in chat to connect to it"),
        ["client connect"] = ("sends players to another server", "the map makes their game run a connect command"),
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
            if (advert is not null && Adverts.TryGetValue(advert, out var a))
                return ("advert", new JsonObject
                {
                    ["reason"] = a.Reason, ["sample"] = scan["advert_sample"]?.DeepClone(), ["evidence"] = $"{a.Reason} - {a.How} (scan of the map, {day})",
                });
            var chat = loads.SelectMany(l => Node.StrList(l["chat"])).Where(c => Scan.AdRx().IsMatch(c) || Scan.SayConnectRx().IsMatch("say " + c)).ToList();
            if (chat.Count > 0)
                return ("advert", new JsonObject
                {
                    ["reason"] = Adverts[Scan.AdRx().IsMatch(chat[0]) ? "gambling ad" : "chat advert"].Reason, ["sample"] = chat[0],
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

    /// <summary>Most subscribed first, then most subscribed ever, then the oldest id.</summary>
    public static (long, long, long) PopularityKey(JsonNode? rec, string wid) =>
        (-Node.Int(Node.Get(rec, "subs")), -Node.Int(Node.Get(rec, "lifetime")), long.TryParse(wid, out var n) ? n : 0);

    /// <summary>The backlog, strictly most subscribed first, whatever the mode. An item tagged for several modes is one item.</summary>
    public static List<string> OrderBacklog(JsonObject items, IEnumerable<string> wids) => wids.OrderBy(w => PopularityKey(items[w], w)).ToList();

    /// <summary>How much is waiting and how long it takes: {"items", "bytes", "by_mode", "days", "pace", "hours", "per_day"}. One thing
    /// bounds a day: HoursADay at the pace measured (or PaceDefault) - every hour somebody plays comes on
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

    /// <summary>One item this run decided: its id, kind, detail and state record.</summary>
    public sealed record Outcome(string Id, string Kind, JsonObject Detail, JsonObject Rec);
}
