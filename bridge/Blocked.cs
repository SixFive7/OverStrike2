// Maps blocked for good, each for its own reason: blocked.json beside maps.json - written by hand, shipped by deploy/data.py, never by the
// Workshop sync (one writer per file) - and broken.json's own "blocked" list, which still counts. The other lists did not fit glassbox (3076804331, map
// glassboxfinal, not on maps.json, loaded by id on 2026-10-20), which kicked every player off the server: a crasher only asks to be
// confirmed, a non-map or a removed map says the wrong thing. A blocked map is refused by id, URL or its own name, from the HUD's Play
// and from !previous - "glassbox (3076804331) kicked every player off the server (2026-10-20) - it is blocked." - never rolled, never
// put on a ballot, and left out of the HUD's map browser. Like the other lists: data on the /data volume, read at start and again when it changes.
using System.Text.Json;

static partial class Program
{
    static Dictionary<string, (string Name, string Map, string Reason)> Blocked = new(); // workshop id -> its name, its map's name, why: both lists
    static Dictionary<string, (string Name, string Map, string Reason)> BlockedOwn = new(), BlockedInBroken = new(); // blocked.json's; broken.json's "blocked"

    /// <summary>blocked.json: the maps blocked for good, beside maps.json like ours.json and sizes.json.</summary>
    static string BlockedMapsFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(MapsFile)) ?? ".", "blocked.json");

    /// <summary>The two lists as one: blocked.json's entry wins where both name a map.</summary>
    static Dictionary<string, (string Name, string Map, string Reason)> MergeBlocked() =>
        BlockedInBroken.Concat(BlockedOwn).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value);

    static bool IsBlocked(MapInfo m) => !m.Official && Blocked.ContainsKey(m.Id.ToString());

    /// <summary>The refusal, in the entry's own words. Name and reason are clipped so the line always fits.</summary>
    static string BlockedRefusal(MapInfo m)
    {
        var id = m.Id.ToString();
        var (name, _, reason) = Blocked.TryGetValue(id, out var b) ? b : (m.Name, "", "is on the blocked list");
        return M.Refused($"{M.A(Clip(name, 40))} ({id}) {Clip(reason, 48)} - It is blocked."); // at most 40 + 14 + 48 + 17 = 119 characters
    }

    /// <summary>A list of blocked maps - blocked.json's, a plain list or an object holding it under "blocked"; broken.json's "blocked": objects with a
    /// positive "id", and "name", "map" (the map name the server logs) and "reason"; a bare id or a missing reason still blocks, with "is on the blocked
    /// list". No list, none.</summary>
    static Dictionary<string, (string, string, string)> ParseBlocked(string json)
    {
        var blocked = new Dictionary<string, (string, string, string)>();
        using var doc = JsonDocument.Parse(json);
        var list = doc.RootElement;
        if (list.ValueKind == JsonValueKind.Object && !list.TryGetProperty("blocked", out list)) return blocked;
        if (list.ValueKind != JsonValueKind.Array) return blocked;
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

    static void LoadBlocked()
    {
        BlockedInBroken = new();
        if (BrokenFile != "" && File.Exists(BrokenFile))
            try { BlockedInBroken = ParseBlocked(File.ReadAllText(BrokenFile)); } catch (Exception e) { Log($"blocked list: {BrokenFile} unreadable ({e.Message})"); }
        Log(ReadBlockedFile());
    }

    /// <summary>blocked.json read (at start, and whenever it changes): a file that does not parse keeps what was read; a missing one is no maps.</summary>
    static string ReadBlockedFile()
    {
        var path = BlockedMapsFile;
        if (!File.Exists(path)) BlockedOwn = new();
        else
            try { BlockedOwn = ParseBlocked(File.ReadAllText(path)); }
            catch (Exception e) { Blocked = MergeBlocked(); return $"blocked list: {path} unreadable ({Clip(e.Message, 120)}) - kept the {BlockedOwn.Count} read before"; }
        Blocked = MergeBlocked();
        return $"blocked list: {Blocked.Count} map(s) blocked for good ({BlockedOwn.Count} from {path}{(File.Exists(path) ? "" : ", not found")}, {BlockedInBroken.Count} from broken.json)"
            + (Blocked.Count > 0 ? ": " + string.Join(", ", Blocked.Select(b => $"{b.Value.Name} ({b.Key})")) : "");
    }

    /// <summary>blocked.json again, while the bridge runs: what has just become blocked leaves an open ballot and stops a countdown (Reload.cs).</summary>
    static string ReloadBlocked()
    {
        var before = Unplayables();
        var line = ReadBlockedFile();
        CatalogVersion++;
        return line + AfterListChange(before);
    }
}
