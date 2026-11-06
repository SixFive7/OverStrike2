// A run's picture for the status file: today, the totals, the queue, the keep set, the history.
using System.Text.Json.Nodes;

namespace OverStrike2.Sync;

public sealed partial class SyncRun
{
    /// <summary>The status file's parts but "now", from this run's state (and ledger).</summary>
    public JsonObject StatusHeavy()
    {
        var items = State.Items;
        var day = Clock.Day();
        var book = Node.Obj(Node.Get(State.Data["downloads"], day));
        var decided = items.Select(kv => kv.Value).OfType<JsonObject>().Where(r => Node.Str(r["decided"]) == day && Node.Truthy(r["verdict"])).ToList();
        var today = new JsonObject
        {
            ["downloads"] = Node.Int(book["items"]), ["bytes"] = Node.Int(book["bytes"]), ["pass"] = decided.Count(r => Node.Str(r["verdict"]) == "pass"),
            ["rejected"] = decided.Count(r => Node.Str(r["verdict"]) != "pass"), ["failedDownloads"] = Node.Int(book["failed"]),
            ["apiCalls"] = Ledger.Data is not null ? Ledger.Count(day) : 0,
        };
        if (Node.Truthy(book["stop"]))
            today["stop"] = Text.Cut(Node.Str(book["stop"]), 200);
        var verdicts = items.Select(kv => kv.Value).OfType<JsonObject>().Where(r => Node.Truthy(r["verdict"])).Select(r => Node.Str(r["verdict"])).ToList();
        BridgeData? files;
        try
        {
            files = BridgeData.Read(Cfg.BridgeData);
        }
        catch (Exception e) when (e is BadDataException or IOException or UnauthorizedAccessException)
        {
            files = null;
        }
        var listed = new Dictionary<string, string>();
        var sizes = new Dictionary<string, long>();
        var names = new Dictionary<string, string>();
        if (files is not null)
        {
            foreach (var (mode, entries) in new[] { ("armsrace", files.Maps), ("deathmatch", files.ModeLists["deathmatch"]), ("wingman", files.ModeLists["wingman"]) })
                foreach (var entry in entries.OfType<JsonObject>())
                {
                    var id = Node.StrOr(entry["id"]);
                    if (!Text.Digits(id))
                        continue;
                    listed.TryAdd(id, mode);
                    sizes.TryAdd(id, Node.Int(entry["sizeMb"]) << 20);
                    names.TryAdd(id, Text.Clean(Node.StrOr(entry["name"]), 80));
                }
        }
        (ListedMode, ListedSize, ListedName) = (listed, sizes, names);
        var totals = new JsonObject
        {
            ["tested"] = verdicts.Count, ["pass"] = verdicts.Count(v => v == "pass"), ["broken"] = verdicts.Count(v => Status.BrokenKinds.Contains(v)),
            ["nonmap"] = verdicts.Count(v => v == "nonmap"),
            ["listed"] = new JsonObject
            {
                ["armsrace"] = files?.Maps.Count ?? 0, ["deathmatch"] = files?.ModeLists["deathmatch"].Count ?? 0, ["wingman"] = files?.ModeLists["wingman"].Count ?? 0,
            },
        };
        long SizeOf(string wid) => sizes.TryGetValue(wid, out var s) && s != 0 ? s : Node.Int(Node.Get(items[wid], "size"));
        // the queue, in the order the downloads take it
        var order = Queue(files?.KnownIds(Cfg) ?? new Dictionary<string, string>(), new Dictionary<string, JsonObject>());
        string Why(string wid)
        {
            if (QTop.Contains(wid))
                return "it would make one of its mode's lists of maps the server keeps";
            if (QChanged.Contains(wid))
                return "it changed on the Workshop since its test";
            if (QFresh.Contains(wid))
                return "a new upload";
            return $"by popularity: {Node.Int(Node.Get(items[wid], "subs"))} subscribers";
        }
        var pace = Pace() ?? Verdicts.PaceDefault;
        var byMode = new JsonObject();
        foreach (var (mode, tag, _, _, _) in GameModes.All)
            byMode[mode] = order.Count(w => Node.In(Node.Get(items[w], "tags"), tag));
        var queue = new JsonObject
        {
            ["total"] = order.Count, ["bytes"] = order.Sum(w => Node.Int(Node.Get(items[w], "size"))), ["byMode"] = byMode,
            ["eta"] = order.Count > 0 ? (long)Math.Truncate(Clock.Now() + order.Count * pace * Day / (Verdicts.HoursADay * 3600.0)) : null,
            ["next"] = new JsonArray(order.Take(Status.Next).Select(w => (JsonNode?)new JsonObject
            {
                ["id"] = w, ["name"] = Text.Clean(Node.StrOr(Node.Get(items[w], "title")), 80), ["mode"] = Status.ListMode(items[w], listed, w),
                ["sizeMb"] = Node.Int(Node.Get(items[w], "size")) >> 20, ["why"] = Why(w),
            }).ToArray()),
        };
        // the keep set: its parts as the game server stores them, and what the sync still has to hand over; the HUD item is hud.addon
        var hud = Text.Digits(Cfg.HudAddon) ? Cfg.HudAddon : "";
        var keep = SafeKeepSet(KeepInputs(), Clock.Now(), hud);
        var has = Handover.ServerMaps(Cfg.GameDir);
        var pending = Handover.Pending(Cfg.HandoverDir);
        var (want, outdated) = Handover.ServeWants(keep, has, pending, State.Data, Cfg);
        queue["serve"] = new JsonObject
        {
            ["missing"] = want.Count, ["bytes"] = want.Sum(SizeOf), ["outdated"] = outdated.Count, ["outdatedBytes"] = outdated.Sum(SizeOf),
            ["handedOver"] = pending.Count, ["handedOverBytes"] = pending.Values.Sum(),
        };
        var parts = new JsonArray();
        void Part(string name, string mode, IEnumerable<string> ids, string? queueName = null)
        {
            var list = ids.Distinct().ToList();
            var p = new JsonObject
            {
                ["part"] = name, ["mode"] = mode, ["count"] = list.Count, ["onDisk"] = list.Count(has.ContainsKey),
                ["gb"] = Math.Round(list.Sum(w => (double)SizeOf(w)) / (1L << 30), 1),
            };
            if (queueName is not null)
                p["queue"] = queueName;
            parts.Add(p);
        }
        Part("up", "", keep.Up);
        foreach (var (mode, _, _, _, _) in GameModes.All)
        {
            var lists = keep.Lists.TryGetValue(mode, out var l) ? l : ([], [], []);
            Part("rated", mode, lists.Rated ?? []);
            Part("popular", mode, lists.Popular ?? []);
            Part("subscribed", mode, lists.Subscribed ?? []);
        }
        Part("own", "", keep.Own);
        Part("hud", "", keep.Hud);
        foreach (var (name, ids) in keep.Queues)
            Part("queues", Status.QueueMode.GetValueOrDefault(name, ""), ids, name);
        if (keep.Requested.Count > 0)
            Part("queues", "", keep.Requested, "requested");
        var keepOut = new JsonObject
        {
            ["parts"] = parts, ["totalGb"] = Math.Round(keep.All.Sum(w => (double)SizeOf(w)) / (1L << 30), 1), ["count"] = keep.All.Count,
            ["onDisk"] = keep.All.Count(has.ContainsKey),
            ["problems"] = Node.Strings(keep.Problems.Where(p => !(p.StartsWith("no HUD item", StringComparison.Ordinal) && hud.Length == 0)).Take(5)),
        };
        var hist = State.Section("history");
        var history = new JsonObject
        {
            ["runs"] = new JsonArray(Node.Arr(hist["runs"]).Take(Status.Runs).Select(e => e?.DeepClone()).ToArray()),
            ["maps"] = new JsonArray(Node.Arr(hist["maps"]).Reverse().Take(Status.Maps).Select(e => e?.DeepClone()).ToArray()),
        };
        return new JsonObject { ["today"] = today, ["totals"] = totals, ["queue"] = queue, ["keep"] = keepOut, ["history"] = history };
    }
}
