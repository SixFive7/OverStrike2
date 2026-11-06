// The broken-map list and the non-maps (broken.json), and what makes a map unplayable.
using System.Text.Json;

static partial class Program
{
    // ---- the broken-map list and the non-maps: data on the /data volume beside maps.json, never code ----

    /// <summary>Reads BROKEN_FILE (--selftest hands it other paths) once, for both lists it carries: the broken maps ("maps") and the
    /// items that are not playable maps at all ("packs"). An empty path, a missing file or unreadable JSON empties both; a list that is absent or not an array
    /// empties just that one. An empty list switches its feature off. Refreshing either is a file drop: the running bridge reads
    /// the file again when it changes (Reload.cs), exactly like MAPS_FILE.</summary>
    static (Dictionary<string, int> Broken, Dictionary<string, string> Packs) LoadLists(string? path = null)
    {
        path ??= BrokenFile;
        if (path == "") return (new(), new());
        if (!File.Exists(path)) { Log($"broken list: {path} not found - no map is marked broken, no non-map is refused"); return (new(), new()); }
        try
        {
            var json = File.ReadAllText(path);
            var (broken, packs) = (ParseBroken(json), ParsePacks(json));
            BrokenReasons = ParseReasons(json); // BrokenWhy.cs: what the card and the confirm say
            Log($"broken list: {broken.Count} maps marked broken and {packs.Count} non-maps refused, from {path}");
            return (broken, packs);
        }
        catch (Exception e) { Log($"broken list: {path} unreadable ({e.Message}) - no map is marked broken, no non-map is refused"); return (new(), new()); }
    }

    /// <summary>Crash key -> how many test loads crashed, out of either a bare array of workshop ids or an object with a
    /// "maps" (or "broken") array. An entry is a number, a string key, or an object carrying "id" (or "name" for an
    /// official map) and optionally "crashes". Anything else in the array is skipped; malformed JSON throws to the caller.</summary>
    static Dictionary<string, int> ParseBroken(string json)
    {
        var broken = new Dictionary<string, int>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("maps", out var m) ? m
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("broken", out var b) ? b : default;
        if (list.ValueKind != JsonValueKind.Array) return broken;
        foreach (var e in list.EnumerateArray())
        {
            var key = e.ValueKind switch
            {
                JsonValueKind.Number or JsonValueKind.String => JsonText(e),
                JsonValueKind.Object => e.TryGetProperty("id", out var i) && i.ValueKind is JsonValueKind.Number or JsonValueKind.String ? JsonText(i)
                    : e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? JsonText(n) : "",
                _ => "",
            };
            if (key != "") broken[key] = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("crashes", out var c) && c.TryGetInt32(out var k) ? k : 0;
        }
        return broken;
    }

    /// <summary>Workshop id -> name for every non-map (the array keeps its first name, "packs"): the "packs" array beside "maps". An entry is an id (number or string) or an
    /// object with "id" and, for the refusal line, "name"; anything without a positive numeric id is skipped, and no "packs" array
    /// means no packs. Keyed by id alone, because the id is what reaches a pack whether or not maps.json still lists it: typing it
    /// bare is the hole that taking a pack out of maps.json does not close.</summary>
    static Dictionary<string, string> ParsePacks(string json)
    {
        var packs = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("packs", out var list)
            || list.ValueKind != JsonValueKind.Array) return packs;
        foreach (var e in list.EnumerateArray())
        {
            var id = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var i) ? i : e;
            if (id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) || !long.TryParse(JsonText(id), out var n) || n <= 0) continue;
            var name = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? JsonText(nm) : "";
            packs[n.ToString()] = name != "" ? name : $"workshop {n}"; // the same text Key() makes of an id, so the two always meet
        }
        return packs;
    }

    /// <summary>Why a map cannot be played at all, or null - the one check for a map asked for, a card on the ballot, a change
    /// counting down and a list that changed: blocked, gone from the Workshop, or no playable map (a pack). Each refusal says why.</summary>
    static string? Unplayable(MapInfo m) => IsBlocked(m) ? BlockedRefusal(m) : IsRemoved(m) ? RemovedRefusal(m) : IsPack(m) ? PackRefusal(m) : null;

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
