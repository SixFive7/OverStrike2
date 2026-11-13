// The search index for the HUD's map browser (HudSearch.cs): search.json beside maps.json on the /data volume - map key -> the words a
// search finds it by (its name, Workshop id and tags); --search-index (SearchIndexFile.cs) keeps the repository's in step with maps.json,
// and the sync adds to the server's.
// Data like maps.json: read at start and again whenever it changes (Reload.cs), served to the plugin on /search/<secret>. A missing or
// unreadable file leaves names-only search, which still works.
//
// The mode words are the lists', not the texts'. armsrace, deathmatch and wingman - the modes' ids, and the words the Workshop's tags Armsrace,
// Deathmatch and Wingman put in a text - say in a text which modes its map had tags for when the text was written, and a text is written once and
// never rewritten: the sync's publish only adds to search.json (its merge check), and --search-index keeps every known text. So a map that joins
// or leaves a mode's list keeps the mode words it was written with. The index served is therefore every text without those three words, followed
// by the modes the menu has its map in now (the catalog's places, LiveState.cs) - made again whenever search.json is read again and whenever a
// map's modes in the menu change (a list read again, a map blocked or no longer blocked). A mode's word finds that mode's maps, its official ones
// among them, and no other map by its text, whatever the Workshop's tags said; a name still finds its map by every word of it. Every other word
// of a text - the name's, the id, the other tags, the description's - is served as written, and search.json itself is never written here.
using System.Text.Json;

static partial class Program
{
    static HudSearch Search = new();
    static volatile string SearchText = new HudSearch().Encode(); // what /search/<secret> answers; rebuilt on the event loop, read by the HTTP thread

    static string SearchFile => Path.Combine(DataDir, "search.json");

    /// <summary>The mode words, in the order of Modes.All: each mode's id, which is also the word its Workshop tag puts in a text.</summary>
    static readonly string[] ModeWords = [.. Modes.All.Select(m => m.Id)];

    /// <summary>The longest the mode words of one map make, with the space before them: what a text leaves room for within HudSearch.MaxText.</summary>
    static readonly int ModeRoom = Modes.All.Sum(m => m.Id.Length + 1);

    static Dictionary<string, (string Text, int Said)> SearchStored = new(); // search.json as read: map key -> its text as served without the mode words, and the modes it named
    static Dictionary<string, int> SearchModes = new();                     // map key -> the modes the menu had it in at its last build (a bit a mode, Modes.All's order)
    static (int Maps, int Lacked, int Named) SearchStale;                   // the last index made: the maps of the menu it gave mode words, and how many of their texts lacked one of their modes or named one they are not in

    /// <summary>search.json: an object of map key -> text. Anything else (a missing file, bad JSON, no object) keeps the index it had.</summary>
    static string ReloadSearch(string path)
    {
        if (!File.Exists(path)) return $"search: {path} not found - names-only search";
        var stored = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return $"search: {path} is not an object of map key -> text - kept the {Search.Count} entries loaded";
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) stored[p.Name] = Stored(p.Value.GetString() ?? "");
        }
        catch (Exception e) { return $"search: {path} unreadable ({Clip(e.Message, 120)}) - kept the {Search.Count} entries loaded"; }
        SearchStored = stored;
        ComposeSearch();
        return $"search: {Search.Count} maps indexed from {path}" + (SearchModes.Count > 0 ? $"; {SearchModesLine()}" : "");
    }

    /// <summary>A text of search.json as the index keeps it (HudSearch.Normal: lowered, letters and digits, one space between words) without the mode
    /// words, cut so that a map's mode words always fit after it; and the modes it named.</summary>
    static (string Text, int Said) Stored(string text)
    {
        var said = 0;
        var kept = new List<string>();
        foreach (var word in HudSearch.Normal(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (Array.IndexOf(ModeWords, word) is var mode and >= 0) said |= 1 << mode;
            else kept.Add(word);
        var s = string.Join(' ', kept);
        var max = HudSearch.MaxText - ModeRoom;
        if (s.Length > max) s = s[..(char.IsHighSurrogate(s[max - 1]) ? max - 1 : max)].TrimEnd();
        return (s, said);
    }

    /// <summary>The words of these modes (a bit a mode, Modes.All's order), one space between: "armsrace deathmatch".</summary>
    static string ModeWordsOf(int modes) => string.Join(' ', ModeWords.Where((_, i) => (modes & 1 << i) != 0));

    /// <summary>The index served: every text of search.json without the mode words, then the words of the modes the menu has its map in; a map of the
    /// menu without a text (an official map) its mode words alone. A new version, so the plugin fetches it again.</summary>
    static void ComposeSearch()
    {
        var index = new HudSearch { Version = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond };
        var (lacked, named) = (0, 0);
        foreach (var (key, (text, said)) in SearchStored)
        {
            var modes = SearchModes.GetValueOrDefault(key);
            var words = ModeWordsOf(modes);
            index.Set(key, words == "" ? text : text == "" ? words : $"{text} {words}");
            if (modes == 0) continue; // a map the menu does not show: never searched, and its text names no mode
            if ((modes & ~said) != 0) lacked++;
            if ((said & ~modes) != 0) named++;
        }
        foreach (var (key, modes) in SearchModes)
            if (!SearchStored.ContainsKey(key)) index.Set(key, ModeWordsOf(modes));
        if (index.Version <= Search.Version) index.Version = Search.Version + 1;
        SearchStale = (SearchModes.Count, lacked, named);
        Search = index;
        SearchText = index.Encode();
    }

    /// <summary>After every build of the menu's map list (RefreshCatalog): when the modes it has any map in differ from the ones the index was made
    /// with, the index is made again with them, and the log says so.</summary>
    static void FollowModes(HudCatalog catalog)
    {
        var modes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in catalog.Maps)
        {
            var mask = 0;
            for (var i = 0; i < Modes.All.Length; i++)
                if (m.RankIn(Modes.All[i].Id) >= 0) mask |= 1 << i;
            if (mask != 0) modes[m.Key] = mask;
        }
        var moved = modes.Count(kv => SearchModes.GetValueOrDefault(kv.Key) != kv.Value) + SearchModes.Keys.Count(k => !modes.ContainsKey(k));
        if (moved == 0) return;
        var first = SearchModes.Count == 0;
        SearchModes = modes;
        ComposeSearch();
        if (!first) Log($"search: the modes of {moved} map(s) changed in the menu - {SearchModesLine()}");
        else if (SearchStored.Count > 0) Log($"search: {SearchModesLine()}"); // the bridge's first map list: what its texts said, once
    }

    /// <summary>The last index's mode words, for the log and GET /health: how many maps of the menu had them from their lists, and how many of those
    /// maps' own texts said otherwise.</summary>
    static string SearchModesLine() =>
        $"the mode words of {SearchStale.Maps} maps from the lists they are on; search.json's texts lacked one of their modes for {SearchStale.Lacked} and named one they are not in for {SearchStale.Named}";
}
