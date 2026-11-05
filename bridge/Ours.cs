// "Our maps" (the owner, 2026-11-01): the maps the server's owner made or publishes get a section of their own - a filter on the HUD's
// map browser and a tag on their cards. Which maps they are is data, never code: ours.json beside maps.json (on the
// /data volume, like every list), read again while the bridge runs (Reload.cs). Adding a map later is a data change only: its entry on
// maps.json (or any listed id) and its id here.
//
//   {"maps": [ {"id": 3831189574, "name": "gg_glasspire"}, ... ]}    or a bare array of ids
//
// An id that maps.json does not list is kept (it may be listed later) and simply shows nowhere. The catalog carries the flag (HudCatalog
// field 19).
using System.Text.Json;

static partial class Program
{
    static HashSet<long> Ours = new();

    static string OursFile => Path.Combine(DataDir, "ours.json");

    /// <summary>The ids out of ours.json's text: a bare array of ids, or an object whose "maps" array holds ids or objects with an "id".
    /// Anything else is skipped; malformed JSON throws to the caller.</summary>
    static HashSet<long> ParseOurs(string json)
    {
        var ids = new HashSet<long>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = root.ValueKind == JsonValueKind.Array ? root : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("maps", out var m) ? m : default;
        if (list.ValueKind != JsonValueKind.Array) return ids;
        foreach (var e in list.EnumerateArray())
        {
            var v = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var i) ? i : e;
            if (v.ValueKind is JsonValueKind.Number or JsonValueKind.String && long.TryParse(JsonText(v), out var id) && id > 0) ids.Add(id);
        }
        return ids;
    }

    /// <summary>ours.json again (at start and when it changes): a missing file is no maps of ours, a file that does not parse keeps the last.</summary>
    static string ReloadOurs(string path)
    {
        if (!File.Exists(path)) { Ours = new(); CatalogVersion++; return $"ours: {path} not found - no map is ours"; }
        try { Ours = ParseOurs(File.ReadAllText(path)); }
        catch (Exception e) { return $"ours: {path} unreadable ({Clip(e.Message, 120)}) - kept {Ours.Count} map(s)"; }
        CatalogVersion++;
        var listed = Maps.Count(m => Ours.Contains(m.Id));
        return $"ours: {Ours.Count} map(s) of ours from {path} ({listed} on the list)";
    }

    static bool IsOurs(MapInfo m) => m.Id > 0 && Ours.Contains(m.Id);
}
