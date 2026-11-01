// Maps blocked for good, each for its own reason: broken.json's "blocked" list. The other lists did not fit glassbox (3076804331, map
// glassboxfinal, not on maps.json, loaded by id on 2026-10-20), which kicked every player off the server: a crasher only asks to be
// confirmed, a non-map or a removed map says the wrong thing. A blocked map is refused by id, URL or its own name, from the HUD's Play
// and from !previous - "glassbox (3076804331) kicked every player off the server (2026-10-20) - it is blocked." - never rolled, never
// queued, and left out of the HUD's map browser. Like the other lists: data on the /data volume, read once at start.
using System.Text.Json;

static partial class Program
{
    static Dictionary<string, (string Name, string Map, string Reason)> Blocked = new(); // workshop id -> its name, its map's name, why

    static bool IsBlocked(MapInfo m) => !m.Official && Blocked.ContainsKey(m.Id.ToString());

    /// <summary>The refusal, in the entry's own words. Name and reason are clipped so the line always fits.</summary>
    static string BlockedRefusal(MapInfo m)
    {
        var id = m.Id.ToString();
        var (name, _, reason) = Blocked.TryGetValue(id, out var b) ? b : (m.Name, "", "is on the blocked list");
        return $"{Clip(name, 40)} ({id}) {Clip(reason, 48)} - it is blocked."; // at most 40 + 14 + 48 + 17 = 119 characters
    }

    /// <summary>broken.json's "blocked": objects with a positive "id", and "name", "map" (the map name the server logs) and "reason";
    /// a bare id or a missing reason still blocks, with "is on the blocked list". No list, none.</summary>
    static Dictionary<string, (string, string, string)> ParseBlocked(string json)
    {
        var blocked = new Dictionary<string, (string, string, string)>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("blocked", out var list)
            || list.ValueKind != JsonValueKind.Array) return blocked;
        foreach (var e in list.EnumerateArray())
        {
            var id = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var i) ? i : e;
            if (id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) || !long.TryParse(JsonText(id), out var n) || n <= 0) continue;
            string Field(string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? Hud.Clean(JsonText(v), 120) : "";
            var name = Field("name");
            blocked[n.ToString()] = (name != "" ? name : $"workshop {n}", Field("map"), Field("reason") is { Length: > 0 } r ? r : "is on the blocked list");
        }
        return blocked;
    }

    /// <summary>A blocked map by its name or its map's name, exactly - for one maps.json does not list, which Find cannot name.</summary>
    static MapInfo? BlockedNamed(string query) =>
        query.Trim() is { Length: > 0 } q && Blocked.FirstOrDefault(b => b.Value.Name.Equals(q, StringComparison.OrdinalIgnoreCase)
            || b.Value.Map.Equals(q, StringComparison.OrdinalIgnoreCase)) is { Key: not null } hit ? new MapInfo(0, long.Parse(hit.Key), hit.Value.Name, 0) : null;

    static void LoadBlocked()
    {
        Blocked = new();
        if (BrokenFile != "" && File.Exists(BrokenFile))
            try { Blocked = ParseBlocked(File.ReadAllText(BrokenFile)); } catch (Exception e) { Log($"blocked list: {BrokenFile} unreadable ({e.Message})"); }
        Log($"blocked list: {Blocked.Count} map(s) blocked for good{(Blocked.Count > 0 ? ": " + string.Join(", ", Blocked.Select(b => $"{b.Value.Name} ({b.Key})")) : "")}");
    }
}
