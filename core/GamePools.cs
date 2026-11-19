using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OverStrike2.Core;

/// <summary>THE GAME'S OWN MAP POOLS: each game mode's official maps as the installed Counter-Strike 2 has them, read out of its own files.
/// gamemodes.txt (inside game/csgo/pak01_dir.vpk) gives every mode its online map groups (mapgroupsMP, where a template#include names a template
/// of templates#include) and every group its maps; a map whose files are not installed - no non-empty game/csgo/maps/&lt;map&gt;.vpk, no .vpk in
/// game/csgo_community_addons/&lt;map&gt;/ - is set apart as missing, the way CS2 1.41.9.9 left de_boulder, de_fachwerk and cs_shelter on
/// 2026-11-18: in no template, their folders empty. Valve's English name of each map comes from resource/csgo_english.txt (its SFUI_Map_
/// tokens). os2-server reads it at every engine start and sends it to the bridge (BridgeWire.PoolsBody), which plays a mode's official maps
/// from it and falls back on its own table (bridge/Shared/Modes.cs) without one. Nothing of the game's files is kept in this repository.</summary>
public static partial class GamePools
{
    public const string ModesEntry = "gamemodes.txt", NamesEntry = "resource/csgo_english.txt";

    /// <summary>One game mode's online pool: its game type and mode as gamemodes.txt names them, the numbers game_type and game_mode select it
    /// by (-1 when the file gives none), its maps in the file's order, and those of them whose files are not installed.</summary>
    public sealed record Pool(string Type, string Mode, int TypeValue, int ModeValue, List<string> Maps, List<string> Missing)
    {
        /// <summary>The pool's maps that are installed: what a changelevel can load.</summary>
        public List<string> Playable => [.. Maps.Where(m => !Missing.Contains(m, StringComparer.OrdinalIgnoreCase))];
    }

    /// <summary>One read of the game: its version (steam.inf's PatchVersion, "?" without one), every game mode's pool, every installed map,
    /// Valve's names of the maps it names; Problem says what could not be read, and then the pools are empty.</summary>
    public sealed record Found(string Build, List<Pool> Pools, List<string> Installed, Dictionary<string, string> Names, string? Problem)
    {
        /// <summary>The installed maps the game ships in csgo_community_addons (AddonsIn) and mounts as community addons: the HUD did not show on
        /// de_eldorado, one of them (2026-11-19), so the bridge keeps every one of them out (bridge/OfficialPools.cs). Empty from a report without the line.</summary>
        public List<string> Addons { get; init; } = [];
    }

    // ---- the text ----

    /// <summary>KeyValues as Valve writes its game files: quoted or bare tokens, // comments to the end of the line, conditions such as [$WIN32]
    /// skipped, a key that may come again. A commented-out line is no entry: gamemodes.txt keeps `// "mg_casualcharlie" "4"` in a template.
    /// Throws FormatException for a block without a key, a key without a value, a block never closed or a } without its {.</summary>
    public static List<Kv> ParseText(string text)
    {
        var tokens = new List<(bool Brace, string Text)>();
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == '\uFEFF')
                i++;
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                i = text.IndexOf('\n', i) is var end and >= 0 ? end : text.Length;
            else if (c is '{' or '}')
            {
                tokens.Add((true, c.ToString()));
                i++;
            }
            else if (c == '"')
            {
                var s = new StringBuilder();
                var j = i + 1;
                for (; j < text.Length && text[j] != '"'; j++)
                {
                    if (text[j] == '\\' && j + 1 < text.Length && text[j + 1] is '"' or '\\')
                        j++;
                    s.Append(text[j]);
                }
                tokens.Add((false, s.ToString()));
                i = j + 1;
            }
            else if (c == '[')
                i = text.IndexOf(']', i) is var close and >= 0 ? close + 1 : text.Length;
            else
            {
                var j = i;
                while (j < text.Length && !char.IsWhiteSpace(text[j]) && text[j] is not ('{' or '}' or '"'))
                    j++;
                tokens.Add((false, text[i..j]));
                i = j;
            }
        }
        var pos = 0;
        List<Kv> Block(bool inner)
        {
            var list = new List<Kv>();
            while (pos < tokens.Count)
            {
                var (brace, key) = tokens[pos++];
                if (brace && key == "}")
                    return inner ? list : throw new FormatException("a } without its {");
                if (brace)
                    throw new FormatException("a block without a key");
                if (pos >= tokens.Count || tokens[pos] is (true, "}"))
                    throw new FormatException($"the key '{key}' has no value");
                var (vBrace, value) = tokens[pos++];
                list.Add(vBrace ? Kv.Of(key, Block(true)) : Kv.Text(key, value));
            }
            return inner ? throw new FormatException("a block that is never closed") : list;
        }
        return Block(false);
    }

    /// <summary>The first entry with this key, in any case, or null.</summary>
    static Kv? Get(List<Kv>? kv, string key) => kv?.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    static int Number(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;

    // ---- gamemodes.txt ----

    /// <summary>Every game mode's online pool in gamemodes.txt's text: its mapgroupsMP's groups - a template#include brings a template's groups
    /// in the order of their numbers, any other key is a group - and each group's maps (mapgroups, its "maps"), in order, each map once. Missing
    /// is empty here: Read fills it from the install.</summary>
    public static List<Pool> PoolsIn(string gameModes)
    {
        var top = ParseText(gameModes);
        var root = Get(top, "GameModes.txt")?.Block ?? top;
        var templates = Get(root, "templates#include")?.Block ?? [];
        var groups = Get(root, "mapgroups")?.Block ?? [];
        List<string> GroupsOf(List<Kv>? mapgroups)
        {
            var found = new List<string>();
            foreach (var e in mapgroups ?? [])
                if (e.Key.Equals("template#include", StringComparison.OrdinalIgnoreCase) && e.Value is { } template)
                    found.AddRange((Get(templates, template)?.Block ?? []).Where(g => g.Value is not null).OrderBy(g => Number(g.Value)).Select(g => g.Key));
                else if (e.Value is not null)
                    found.Add(e.Key);
            return [.. found.Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        IEnumerable<string> MapsOf(string group) => (Get(Get(groups, group)?.Block, "maps")?.Block ?? []).Select(m => m.Key.Trim()).Where(m => m.Length > 0);
        var pools = new List<Pool>();
        foreach (var type in Get(root, "gameTypes")?.Block ?? [])
        {
            if (type.Block is null)
                continue;
            foreach (var mode in Get(type.Block, "gameModes")?.Block ?? [])
                if (mode.Block is not null)
                    pools.Add(new Pool(type.Key, mode.Key, Number(Get(type.Block, "value")?.Value), Number(Get(mode.Block, "value")?.Value),
                        [.. GroupsOf(Get(mode.Block, "mapgroupsMP")?.Block).SelectMany(MapsOf).Distinct(StringComparer.OrdinalIgnoreCase)], []));
        }
        return pools;
    }

    // ---- csgo_english.txt ----

    [GeneratedRegex("\"SFUI_Map_([A-Za-z0-9_]+)\"\\s+\"([^\"\\r\\n]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MapNameRx();

    /// <summary>Valve's English names of maps in csgo_english.txt's text - its SFUI_Map_&lt;map&gt; tokens - by map name, lowered; a token's first
    /// value counts, an empty one none.</summary>
    public static Dictionary<string, string> NamesIn(string english)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in MapNameRx().Matches(english))
            if (m.Groups[2].Value.Trim() is { Length: > 0 } name)
                names.TryAdd(m.Groups[1].Value.ToLowerInvariant(), name);
        return names;
    }

    // ---- the install ----

    /// <summary>The maps whose files are installed in the game folder (STEAMAPPDIR/game), lowered and sorted: a non-empty csgo/maps/&lt;map&gt;.vpk,
    /// or a folder csgo_community_addons/&lt;map&gt; holding a .vpk, the way the game ships a community map. An empty such folder is a map the game
    /// took away. Vanity and Workshop-preview packages are no maps; the rest of csgo/maps is harmless here, since only a pool's names are looked up.</summary>
    public static List<string> InstalledIn(string gameDir)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var maps = Path.Combine(gameDir, "csgo", "maps");
        if (Directory.Exists(maps))
            foreach (var f in Directory.EnumerateFiles(maps, "*.vpk"))
            {
                var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (!name.EndsWith("_vanity", StringComparison.Ordinal) && !name.StartsWith("workshop_preview_", StringComparison.Ordinal) && new FileInfo(f).Length > 0)
                    names.Add(name);
            }
        names.UnionWith(AddonsIn(gameDir));
        return [.. names];
    }

    /// <summary>The maps the game ships as community addons, lowered and sorted: each a folder csgo_community_addons/&lt;map&gt; holding a .vpk (an empty
    /// one is a map the game took away). CS2 mounts such a map as a community addon when it loads - addons(&lt;map&gt;,...) - and the HUD did not show on
    /// one (de_eldorado, 2026-11-19).</summary>
    public static List<string> AddonsIn(string gameDir)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var addons = Path.Combine(gameDir, "csgo_community_addons");
        if (Directory.Exists(addons))
            foreach (var d in Directory.EnumerateDirectories(addons))
                if (Directory.EnumerateFiles(d, "*.vpk").Any())
                    names.Add(Path.GetFileName(d).ToLowerInvariant());
        return [.. names];
    }

    // ---- all of it ----

    /// <summary>The whole read, from the game folder (STEAMAPPDIR/game): steam.inf's PatchVersion, gamemodes.txt and csgo_english.txt out of
    /// csgo/pak01_dir.vpk, the installed maps and which of them are community addons. Never throws: what it could not read is its Problem. Valve's names only of the maps it names.</summary>
    public static Found Read(string gameDir)
    {
        var build = SteamInf.ReadPatchVersion(Path.Combine(gameDir, "csgo", "steam.inf")) ?? "?";
        List<string> installed, addons;
        try
        {
            installed = InstalledIn(gameDir);
            addons = AddonsIn(gameDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(build, [], [], new(), $"the maps folders cannot be read ({e.Message})");
        }
        var vpkPath = Path.Combine(gameDir, "csgo", "pak01_dir.vpk");
        Vpk vpk;
        try
        {
            vpk = new Vpk(vpkPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(build, [], installed, new(), $"{vpkPath} cannot be read ({e.Message})") { Addons = addons };
        }
        string? Text(string entry) => vpk.Entries.FirstOrDefault(e => e.Path.Equals(entry, StringComparison.OrdinalIgnoreCase)) is { } e && vpk.Read(e) is { } bytes
            ? Encoding.UTF8.GetString(bytes) : null;
        if (Text(ModesEntry) is not { } modesText)
            return new(build, [], installed, new(), $"no {ModesEntry} in {vpkPath}") { Addons = addons };
        List<Pool> pools;
        try
        {
            pools = PoolsIn(modesText);
        }
        catch (FormatException e)
        {
            return new(build, [], installed, new(), $"{ModesEntry} does not parse ({e.Message})") { Addons = addons };
        }
        var have = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        pools = [.. pools.Select(p => p with { Missing = [.. p.Maps.Where(m => !have.Contains(m))] })];
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Text(NamesEntry) is { } english)
        {
            var all = NamesIn(english);
            foreach (var m in pools.SelectMany(p => p.Maps).Concat(installed))
                if (all.TryGetValue(m, out var name))
                    names.TryAdd(m, name);
        }
        return new(build, pools, installed, names, pools.Count == 0 ? $"{ModesEntry} names no game mode" : null) { Addons = addons };
    }
}
