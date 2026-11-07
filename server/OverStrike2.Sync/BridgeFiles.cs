using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>The sync's modes: (id, Workshop tag, game_type, game_mode, the list file beside maps.json).</summary>
public static class GameModes
{
    public static readonly (string Mode, string Tag, int GameType, int GameMode, string File)[] All =
    [
        ("armsrace", "Armsrace", 1, 0, "maps.json"),
        ("deathmatch", "Deathmatch", 1, 2, "maps-deathmatch.json"),
        ("wingman", "Wingman", 0, 2, "maps-wingman.json"),
    ];
}

/// <summary>The bridge's data as it is now: maps.json, broken.json, blocked.json, the mode lists, search.json, and the raw bytes of each
/// file read (what a publish compares the live files with). blocked.json - the maps blocked for good, written by hand and shipped by
/// deploy/data.py, never by this tool (one writer per file) - may be missing; broken.json's own "blocked" counts as well.</summary>
public sealed class BridgeData
{
    public JsonArray Maps { get; set; } = [];
    public JsonObject Broken { get; set; } = [];
    public JsonArray Blocked { get; set; } = [];
    public Dictionary<string, JsonArray> ModeLists { get; set; } = new() { ["deathmatch"] = [], ["wingman"] = [] };
    public JsonObject? Search { get; set; }
    public Dictionary<string, byte[]> Raw { get; set; } = [];

    /// <summary>The bridge's files. Throws BadDataException when maps.json, broken.json or a blocked.json that is there does not parse:
    /// then nothing is published and nothing is assumed; IOException when one cannot be read.</summary>
    public static BridgeData Read(string dataDir)
    {
        var d = new BridgeData();
        var path = Path.Combine(dataDir, "maps.json");
        var raw = File.ReadAllBytes(path);
        d.Raw["maps.json"] = raw;
        if (Node.Parse(raw, path) is not JsonArray maps || maps.Count == 0)
            throw new BadDataException($"{path} holds no map list");
        d.Maps = maps;
        path = Path.Combine(dataDir, "broken.json");
        raw = File.ReadAllBytes(path);
        d.Raw["broken.json"] = raw;
        if (Node.Parse(raw, path) is not JsonObject broken)
            throw new BadDataException($"{path} is not the object form");
        d.Broken = broken;
        path = Path.Combine(dataDir, "blocked.json");
        if (File.Exists(path))
        {
            var blocked = Node.Parse(File.ReadAllBytes(path), path);
            var entries = blocked is JsonObject bo ? bo["blocked"] : blocked;
            if (entries is not JsonArray list)
                throw new BadDataException($"{path} is not a list of blocked maps");
            if (blocked is JsonObject owner)
                owner.Remove("blocked");
            d.Blocked = list;
        }
        foreach (var (mode, _, _, _, name) in GameModes.All.Skip(1))
        {
            path = Path.Combine(dataDir, name);
            if (File.Exists(path))
            {
                raw = File.ReadAllBytes(path);
                d.Raw[name] = raw;
                if (Node.Parse(raw, path) is not JsonArray list)
                    throw new BadDataException($"{path} is not a list");
                d.ModeLists[mode] = list;
            }
            else
                d.ModeLists[mode] = [];
        }
        path = Path.Combine(dataDir, "search.json");
        d.Search = Node.ReadJson(path) as JsonObject;
        if (d.Search is not null)
        {
            try
            {
                d.Raw["search.json"] = File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                d.Search = null;
            }
        }
        return d;
    }

    /// <summary>{id: section} of broken.json's lists (maps, packs, removed, blocked), the first section an id is in.</summary>
    public static Dictionary<string, string> BrokenIds(JsonObject broken)
    {
        var result = new Dictionary<string, string>();
        foreach (var section in new[] { "maps", "packs", "removed", "blocked" })
            foreach (var entry in Node.Arr(broken[section]))
            {
                var wid = Node.Str(entry is JsonObject o ? o["id"] : entry);
                if (Text.Digits(wid))
                    result.TryAdd(wid, section);
            }
        return result;
    }

    /// <summary>The ids on blocked.json: the maps blocked for good, by hand.</summary>
    public HashSet<string> BlockedIds()
    {
        var result = new HashSet<string>();
        foreach (var entry in Blocked)
        {
            var wid = Node.Str(entry is JsonObject o ? o["id"] : entry);
            if (Text.Digits(wid))
                result.Add(wid);
        }
        return result;
    }

    /// <summary>The ids of maps.json's entries, in order.</summary>
    public static IEnumerable<string> Ids(JsonArray list) =>
        list.OfType<JsonObject>().Select(m => Node.Str(m.TryGetPropertyValue("id", out var v) ? v : "")).Where(Text.Digits);

    /// <summary>Every id with a verdict of ours already: listed (any mode), on broken.json or blocked.json, or excluded by the settings.</summary>
    public Dictionary<string, string> KnownIds(SyncConfig cfg)
    {
        var known = new Dictionary<string, string>();
        foreach (var wid in Ids(Maps))
            known[wid] = "listed";
        foreach (var (mode, entries) in ModeLists)
            foreach (var wid in Ids(entries))
                known.TryAdd(wid, "listed:" + mode);
        foreach (var (wid, section) in BrokenIds(Broken))
            known.TryAdd(wid, "broken:" + section);
        foreach (var wid in BlockedIds())
            known.TryAdd(wid, "broken:blocked");
        foreach (var wid in cfg.Exclude)
            known.TryAdd(wid, "excluded");
        return known;
    }

    /// <summary>Every listed id, maps.json first, then the mode lists, each once.</summary>
    public List<string> Listed()
    {
        var listed = Ids(Maps).ToList();
        foreach (var (_, entries) in ModeLists)
            foreach (var wid in Ids(entries))
                if (!listed.Contains(wid))
                    listed.Add(wid);
        return listed;
    }
}

/// <summary>&lt;state&gt;/state.json: the items this tool has seen and what it decided, the update baseline, the day's downloads, the ratings,
/// the history, the last run. Read and written as a JSON object, not as classes: a key this version does not read stays as it was.</summary>
public sealed class SyncState(string path)
{
    public string Path { get; } = path;
    public JsonObject Data { get; set; } = new() { ["schema"] = 1, ["items"] = new JsonObject(), ["baseline"] = new JsonObject() };

    public JsonObject Items => Section("items");
    public JsonObject Baseline => Section("baseline");

    /// <summary>A section of the state, made when it is missing.</summary>
    public JsonObject Section(string name)
    {
        if (Data[name] is JsonObject o)
            return o;
        var fresh = new JsonObject();
        Data[name] = fresh;
        return fresh;
    }

    public SyncState Load()
    {
        var data = Node.ReadJson(Path);
        if (data is null && File.Exists(Path))
            throw new BadDataException($"{Path} does not parse - fix or delete it");
        if (data is JsonObject o)
        {
            Data = o;
            _ = Items;
            _ = Baseline;
        }
        return this;
    }

    public void Save() => Files.WriteAtomic(Path, Node.Compact(Data) + "\n", FileModes.Private);

    public JsonObject? Item(string wid) => Items[wid] as JsonObject;

    /// <summary>The Workshop's numbers of each map as the check last saw it: what workshop.json gives the bridge.</summary>
    public JsonObject Workshop => Section("workshop");

    /// <summary>An item's Workshop numbers as the check saw them (result 1; a map gone or hidden keeps what was seen before).</summary>
    public void NoteWorkshop(string wid, JsonObject item)
    {
        if (Node.Int(item["result"]) != 1)
            return;
        Workshop[wid] = new JsonObject
        {
            ["created"] = Node.Int(item["time_created"]), ["updated"] = Node.Int(item["time_updated"]), ["content"] = Node.StrOr(item["hcontent_file"]),
            ["subs"] = Node.Int(item["subscriptions"]), ["lifetime"] = Node.Int(item["lifetime_subscriptions"]), ["favs"] = Node.Int(item["favorited"]),
            ["lifetimeFavs"] = Node.Int(item["lifetime_favorited"]), ["views"] = Node.Int(item["views"]),
        };
    }

    /// <summary>Record a Workshop item we were shown (discovery, or a changed listed map), keeping what we decided before. "attempts" is the
    /// download tries of one content version: a newer version has them all again, and a changed listed map that used them up on an older
    /// one is queued for the new one.</summary>
    public JsonObject Remember(JsonObject item, string day, string why)
    {
        var wid = Node.Str(item["publishedfileid"]);
        if (Items[wid] is not JsonObject rec)
        {
            rec = new JsonObject { ["first"] = day, ["status"] = "queued", ["why"] = why, ["attempts"] = 0, ["tests"] = 0 };
            Items[wid] = rec;
        }
        var newer = Node.Int(item["time_updated"]) > Node.Int(rec["updated"]);
        var verdictOf = (JsonObject)item.DeepClone();
        if (!verdictOf.ContainsKey("result"))
            verdictOf["result"] = 1;
        rec["title"] = Text.ItemName(item);
        rec["tags"] = Node.Strings(Text.ItemTags(item));
        rec["size"] = Node.Int(item["file_size"]);
        rec["created"] = Node.Int(item["time_created"]);
        rec["updated"] = Node.Int(item["time_updated"]);
        rec["subs"] = Node.Int(item["subscriptions"]);
        rec["lifetime"] = Node.Int(item["lifetime_subscriptions"]);
        rec["filename"] = Node.Truthy(item["filename"]) ? item["filename"]!.DeepClone() : "";
        rec["app"] = Node.Int(item["consumer_appid"]);
        rec["creator"] = Node.StrOr(item["creator"]);
        rec["ws"] = Text.WorkshopVerdict(verdictOf);
        rec["preview"] = Node.Truthy(item["preview_url"]) ? item["preview_url"]!.DeepClone() : "";
        rec["description"] = Text.Cut(Text.CleanDescription(Node.StrOr(item["file_description"])), 300);
        if (newer)
            rec["attempts"] = 0;
        if (why == "changed" && (Node.Str(rec["status"]) is "published" or "pass" || (newer && Node.Str(rec["status"]) == "failed")))
        {
            rec["status"] = "queued";
            rec["why"] = "changed";
        }
        return rec;
    }

    /// <summary>Whether this version of a map failed its test today. A version that fails stays ahead of the baseline, so every check finds the
    /// map changed for as long as the Workshop keeps it: this is what holds it to one test a day.</summary>
    public bool FailedToday(string wid, JsonObject item, string day) =>
        Item(wid) is { } rec && Node.Str(rec["decided"]) == day && Node.Truthy(rec["verdict"]) && Node.Str(rec["verdict"]) != "pass"
        && Node.Int(item["time_updated"]) <= Node.Int(rec["updated"]);
}
