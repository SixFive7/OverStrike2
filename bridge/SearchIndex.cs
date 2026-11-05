// The search index for the HUD's map browser (HudSearch.cs): search.json beside maps.json on the /data volume - map key -> the words a
// search finds it by (its name, Workshop id and tags); search-index.py keeps it in step with maps.json, and the sync adds to it.
// Data like maps.json: read at start and again whenever it changes (Reload.cs), served to the plugin on /search/<secret>. A missing or
// unreadable file leaves names-only search, which still works.
using System.Text.Json;

static partial class Program
{
    static HudSearch Search = new();
    static volatile string SearchText = new HudSearch().Encode(); // what /search/<secret> answers; rebuilt on the event loop, read by the HTTP thread

    static string SearchFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(MapsFile)) ?? ".", "search.json");

    /// <summary>search.json: an object of map key -> text. Anything else (a missing file, bad JSON, no object) keeps the index it had.</summary>
    static string ReloadSearch(string path)
    {
        if (!File.Exists(path)) return $"search: {path} not found - names-only search";
        HudSearch index;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return $"search: {path} is not an object of map key -> text - kept the {Search.Count} entries loaded";
            index = new HudSearch { Version = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond };
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) index.Set(p.Name, p.Value.GetString() ?? "");
        }
        catch (Exception e) { return $"search: {path} unreadable ({Clip(e.Message, 120)}) - kept the {Search.Count} entries loaded"; }
        if (index.Version <= Search.Version) index.Version = Search.Version + 1;
        Search = index;
        SearchText = index.Encode();
        return $"search: {index.Count} maps indexed from {path}";
    }
}
