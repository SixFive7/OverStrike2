// --selftest, the search index's mode words (SearchIndex.cs): armsrace, deathmatch and wingman follow the mode lists the menu has a map in, not the
// Workshop tags its text of search.json was written with - a map that joins or leaves a list, a blocked map, an official one - while every other word
// of a text, a name and an id find maps as before; and search.json itself is never written. Played as the plugin plays it: the catalog and the index
// as /catalog/ and /search/ serve them, decoded, and the map browser's search over them.
using System.Text.Json;

static partial class Program
{
    static void SearchModesSelfTest(Action<string, bool> Check)
    {
        var h = new Harness("search", new DateTime(2026, 11, 13, 20, 0, 0, DateTimeKind.Utc));
        ModeMapsDir = h.Dir;
        try
        {
            // a: on Arms Race's and Deathmatch's lists, its text written when it was a Deathmatch map only (as_retreat_csco, release 214)
            // b: on Arms Race's list only, its tags and its description saying deathmatch and wingman
            // c: on Deathmatch's list only, its text written when its tags said Wingman and not yet Deathmatch
            // d: on Wingman's list only, its text naming no mode; e: on Arms Race's only, named Wingman Arena; f: on Arms Race's list, blocked
            // g: on Deathmatch's list, without a text in search.json; w: on Wingman's list, its text saying so
            const string a = "3900000001", b = "3900000002", c = "3900000003", d = "3900000004", e = "3900000005", f = "3900000006", g = "3900000007", w = "3900000008";
            Maps = [new(1, long.Parse(a), "as_retreat_csco - CSCO Assault", 174), new(2, long.Parse(b), "aim_bluebox", 30), new(3, long.Parse(e), "Wingman Arena", 20),
                new(4, long.Parse(f), "blocked map", 10)];
            static string ListOf(params (string Id, string Name)[] maps) =>
                "[" + string.Join(",", maps.Select((m, i) => $$"""{"rank": {{i + 1}}, "id": {{m.Id}}, "name": "{{m.Name}}", "sizeMb": 10}""")) + "]";
            File.WriteAllText(ModeMapsPath(Modes.Deathmatch), ListOf((a, "as_retreat_csco - CSCO Assault"), (c, "Vault"), (g, "dm_untexted")));
            File.WriteAllText(ModeMapsPath(Modes.Wingman), ListOf((d, "fy_pool"), (w, "wm other")));
            ReloadModeMaps(Modes.Deathmatch); ReloadModeMaps(Modes.Wingman);
            Blocked = new() { [f] = ("blocked map", "", "froze the server") };
            var searchPath = Path.Combine(h.Dir, "search.json");
            File.WriteAllText(searchPath, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [a] = "as_retreat_csco — csco assault 3900000001 cs2 map classic deathmatch custom",
                [b] = "aim_bluebox 3900000002 cs2 map classic deathmatch wingman armsrace custom a blue box for a team deathmatch, wingman-ready",
                [c] = "vault 3900000003 cs2 map classic wingman",
                [d] = "fy_pool 3900000004 classic custom",
                [e] = "wingman arena 3900000005 classic armsrace",
                [f] = "blocked map 3900000006 armsrace deathmatch",
                [w] = "wm other 3900000008 wingman",
            }, Ctx.Default.DictionaryStringString));
            var bytesBefore = File.ReadAllBytes(searchPath);
            var loaded = ReloadSearch(searchPath);
            RefreshCatalog(); // as the event loop does after every piece of work (AfterWork): the menu's map list, and the index follows its modes
            HudSearch Served() => HudSearch.Decode(SearchText)!;
            List<string> Found(string query)
            {
                RefreshCatalog();
                return new HudBrowser { Query = query }.Matches(HudCatalog.Decode(CatalogText)!, 0, Served()).Select(m => m.Key).Order(StringComparer.Ordinal).ToList();
            }
            static List<string> Keys(IEnumerable<string> ids, IEnumerable<string> officials) => ids.Concat(officials).Distinct().Order(StringComparer.Ordinal).ToList();
            var arOfficial = Officials.Select(o => o.Name).ToList();
            Console.WriteLine($"{loaded} | {SearchModesLine()}");

            Check("Mode words: armsrace finds Arms Race's list and its official maps - a map whose text was written before it joined the list among them - and no other map by its text",
                Found("armsrace").SequenceEqual(Keys([a, b, e], arOfficial)));
            Check("...deathmatch finds Deathmatch's list and Valve's 15 Deathmatch maps, a listed map without any text among them; a map whose Workshop tags and description say deathmatch while it is on no Deathmatch list is not found by them",
                Found("deathmatch").SequenceEqual(Keys([a, c, g], Modes.Deathmatch.Official)) && !Found("deathmatch").Contains(b));
            Check("...wingman finds Wingman's list and Valve's 7 Wingman maps, and a map named Wingman Arena by its name; a text that said wingman for a map on no Wingman list finds it no more",
                Found("wingman").SequenceEqual(Keys([d, e, w], Modes.Wingman.Official)) && !Found("wingman").Contains(c) && !Found("wingman").Contains(b));
            Check("...the text served: as the index keeps it, without the mode words it was written with, then the modes the menu has the map in, in the menu's order",
                Served().TextOf(a) == "as retreat csco csco assault 3900000001 cs2 map classic custom armsrace deathmatch"
                && Served().TextOf(c) == "vault 3900000003 cs2 map classic deathmatch" && Served().TextOf(g) == "deathmatch" && Served().TextOf("de_inferno") == "deathmatch wingman"
                && Served().TextOf("ar_baggage") == "armsrace");
            Check("...a blocked map is out of the menu, so its text names no mode and no mode word finds it",
                Served().TextOf(f) == "blocked map 3900000006" && !Found("armsrace").Contains(f) && !Found("deathmatch").Contains(f));
            Check("Every other word finds what it found: a tag (classic), a word of the name (csco), of the description (team, box, ready), an id, the start of a mode word (death, wing)",
                Found("classic").SequenceEqual([a, b, c, d, e]) && Found("csco").SequenceEqual([a]) && Found("team box").SequenceEqual([b]) && Found("ready").SequenceEqual([b])
                && Found("3900000004").SequenceEqual([d]) && Found("death").SequenceEqual(Found("deathmatch")) && Found("wing").SequenceEqual(Found("wingman")));
            var menuMaps = 7 + arOfficial.Count + Modes.Deathmatch.Official.Union(Modes.Wingman.Official).Count();
            Check($"The log and GET /health say it: the mode words of the menu's {menuMaps} maps from their lists, and how many texts of search.json said otherwise ({SearchModesLine()})",
                SearchStale == (menuMaps, 3, 3) && HealthText().Contains(SearchModesLine()));

            // a map joins a list and leaves one - by the sync's next publish, or by hand: the list read again, the index follows at the next piece of work
            var version = Served().Version;
            File.WriteAllText(ModeMapsPath(Modes.Wingman), ListOf((d, "fy_pool"), (w, "wm other"), (c, "Vault")));
            ReloadModeMaps(Modes.Wingman);
            Check("A map that joins Wingman's list is found by wingman at once, its text in search.json untouched, and the index's version moves, so the plugin fetches it",
                Found("wingman").Contains(c) && Served().Version > version && Served().TextOf(c) == "vault 3900000003 cs2 map classic deathmatch wingman");
            File.WriteAllText(ModeMapsPath(Modes.Deathmatch), ListOf((a, "as_retreat_csco - CSCO Assault"), (g, "dm_untexted")));
            ReloadModeMaps(Modes.Deathmatch);
            File.WriteAllText(ModeMapsPath(Modes.Wingman), ListOf((d, "fy_pool"), (c, "Vault")));
            ReloadModeMaps(Modes.Wingman);
            Check("...one that leaves a list is no longer found by its mode's word: a map whose word came from its list alone, and one whose own text says wingman",
                !Found("deathmatch").Contains(c) && Found("deathmatch").Contains(a) && !Found("wingman").Contains(w) && Found("wingman").Contains(c)
                && Served().TextOf(w) == "wm other 3900000008");
            Blocked = new(); CatalogVersion++; // blocked.json read again (ReloadBlocked)
            Check("...a map no longer blocked is back in the menu, and its mode's word finds it", Found("armsrace").Contains(f) && Served().TextOf(f) == "blocked map 3900000006 armsrace");
            version = Served().Version;
            CatalogVersion++; RefreshCatalog(); // a vote, a crash: the menu's list made again, no map's modes changed
            Check("A new map list in which no map's modes changed leaves the index and its version as they were: the plugin fetches nothing", Served().Version == version);
            Check("search.json is read, never written: after all of that its bytes are the ones it was written with", File.ReadAllBytes(searchPath).SequenceEqual(bytesBefore));

            // the plugin cuts a text at HudSearch.MaxText: never through its mode words
            var longPath = Path.Combine(h.Dir, "search-long.json");
            File.WriteAllText(longPath, JsonSerializer.Serialize(new Dictionary<string, string> { [b] = "aim_bluebox 3900000002 " + string.Join(' ', Enumerable.Repeat("word", 1000)) },
                Ctx.Default.DictionaryStringString));
            ReloadSearch(longPath);
            Check($"A text longer than the index keeps ({HudSearch.MaxText} characters) is cut before its mode words, not through them",
                Served().TextOf(b) is { } cut && cut.Length <= HudSearch.MaxText && cut.EndsWith(" armsrace", StringComparison.Ordinal) && Found("armsrace").Contains(b));
        }
        finally { h.Dispose(); ModeMapsDir = null; }
    }
}
