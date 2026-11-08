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
/// os2-deploy data, never by this tool (one writer per file) - may be missing; broken.json's own "blocked" counts as well.</summary>
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
