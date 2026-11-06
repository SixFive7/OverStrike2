// broken.json: the broken-map list, the reasons given for its maps, the non-maps and the maps it blocks - data on the /data volume beside
// maps.json, never code, read whole once at the start and again whenever it changes (Reload.cs).
using System.Text.Json;

static partial class Program
{
    /// <summary>broken.json's four lists: the crashers (crash key -> how many test loads crashed), the reasons given for them (BrokenWhy.cs), the
    /// non-maps (Workshop id -> name) and the maps it blocks, beside blocked.json's own (Blocked.cs).</summary>
    sealed record BrokenLists(Dictionary<string, int> Broken, Dictionary<string, string> Reasons, Dictionary<string, string> Packs,
        Dictionary<string, (string Name, string Map, string Reason)> Blocked);

    static BrokenLists NoBrokenLists => new(new(), new(), new(), new());

    /// <summary>broken.json's text, parsed once for all four lists. A list that is absent or not an array is empty; malformed JSON throws to the caller.</summary>
    static BrokenLists ParseBrokenFile(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new(ParseBroken(root), ParseReasons(root), ParsePacks(root),
            ParseBlockedList(root.ValueKind == JsonValueKind.Object && root.TryGetProperty("blocked", out var blocked) ? blocked : default));
    }

    /// <summary>At the start: BROKEN_FILE (--selftest hands it other paths), read once. An empty path, a missing file or unreadable JSON: no list at
    /// all. An empty list switches its feature off. Refreshing any of them is a file drop: the running bridge reads the file again when it changes
    /// (Reload.cs), exactly like MAPS_FILE.</summary>
    static BrokenLists LoadLists(string? path = null)
    {
        path ??= BrokenFile;
        if (path == "") return NoBrokenLists;
        if (!File.Exists(path)) { Log($"broken list: {path} not found - no map is marked broken, no non-map is refused"); return NoBrokenLists; }
        try
        {
            var lists = ParseBrokenFile(File.ReadAllText(path));
            Log($"broken list: {lists.Broken.Count} maps marked broken, {lists.Packs.Count} non-maps refused and {lists.Blocked.Count} blocked, from {path}");
            return lists;
        }
        catch (Exception e) { Log($"broken list: {path} unreadable ({e.Message}) - no map is marked broken, no non-map is refused"); return NoBrokenLists; }
    }

    /// <summary>broken.json's lists in force, its blocked maps merged with blocked.json's.</summary>
    static void TakeBrokenLists(BrokenLists lists)
    {
        (Broken, BrokenReasons, Packs, BlockedInBroken) = (lists.Broken, lists.Reasons, lists.Packs, lists.Blocked);
        Blocked = MergeBlocked();
    }

    /// <summary>The crashers' array: an object's "maps" (or "broken"), or a bare array of them.</summary>
    static JsonElement BrokenArray(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root
        : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("maps", out var m) ? m
        : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("broken", out var b) ? b : default;

    static Dictionary<string, int> ParseBroken(string json) { using var doc = JsonDocument.Parse(json); return ParseBroken(doc.RootElement); }

    /// <summary>Crash key -> how many test loads crashed, out of the crashers' array (BrokenArray). An entry is a number, a string key, or an object
    /// carrying "id" (or "name" for an official map) and optionally "crashes". Anything else in the array is skipped.</summary>
    static Dictionary<string, int> ParseBroken(JsonElement root)
    {
        var broken = new Dictionary<string, int>();
        if (BrokenArray(root) is not { ValueKind: JsonValueKind.Array } list) return broken;
        foreach (var e in list.EnumerateArray())
            if (EntryKey(e, orName: true) is { Length: > 0 } key)
                broken[key] = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("crashes", out var c) && c.TryGetInt32(out var k) ? k : 0;
        return broken;
    }

    static Dictionary<string, string> ParsePacks(string json) { using var doc = JsonDocument.Parse(json); return ParsePacks(doc.RootElement); }

    /// <summary>Workshop id -> name for every non-map (the array keeps its first name, "packs"): the "packs" array beside "maps". An entry is an id (number or string) or an
    /// object with "id" and, for the refusal line, "name"; anything without a positive numeric id is skipped, and no "packs" array
    /// means no packs. Keyed by id alone, because the id is what reaches a pack whether or not maps.json still lists it: typing it
    /// bare is the hole that taking a pack out of maps.json does not close.</summary>
    static Dictionary<string, string> ParsePacks(JsonElement root)
    {
        var packs = new Dictionary<string, string>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("packs", out var list) || list.ValueKind != JsonValueKind.Array) return packs;
        foreach (var e in list.EnumerateArray())
            if (EntryId(e) is { } n)
                packs[n.ToString()] = EntryText(e, "name") is { Length: > 0 } name ? name : $"workshop {n}"; // the same text Key() makes of an id, so the two always meet
        return packs;
    }

    /// <summary>A "pack" here is any workshop item with no playable map in it - skins and player models, but also configs, sound and
    /// viewmodel packs and empty uploads (the name predates the wider list). host_workshop_map on one finds nothing to load: the
    /// server drops into CS2's "error" map and freezes with the process still running, or hangs. There is nothing to agree to, so
    /// unlike a broken map this is a refusal, never a confirm, and it is decided before any RCON command is built.</summary>
    static bool IsPack(MapInfo m) => !m.Official && Packs.ContainsKey(m.Id.ToString());

    /// <summary>The refusal's claim. Deliberately neutral: the list mixes skin packs, configs, sound packs and empty uploads, and
    /// "skin pack" was false for most of them. True of every one: it is not a playable map, and the server cannot load it.</summary>
    const string NotAMap = "isn't a playable map - It can't be loaded";

    /// <summary>The refusal, named from the packs list so a bare id still reads as the item it is. The name is clipped at 60
    /// characters, so the line fits untrimmed whatever the item is called and "it can't be loaded" is never the part cut off.</summary>
    static string PackRefusal(MapInfo m)
    {
        var name = Packs.TryGetValue(m.Id.ToString(), out var listed) ? listed : m.Name;
        return M.Refused($"{M.A(name[..Math.Min(60, name.Length)])} {NotAMap}");
    }

    static int Playable => Maps.Count(m => !IsPack(m)); // what chat counts as "maps": every listed one except a non-map
}
