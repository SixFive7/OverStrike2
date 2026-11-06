// The maps the bridge knows, in every mode, and how it names them: a map's key, its label in chat and on the HUD, the map that is on, and a map
// by its key.
using System.Globalization;

static partial class Program
{
    /// <summary>A map's key: its Workshop id, or an official map's name - which is also what the server logs. The crash counter, the likes, the
    /// lists and the HUD all go by it.</summary>
    static string Key(MapInfo m) => m.Official ? m.Name : m.Id.ToString();

    /// <summary>How chat names a map: "#12 aim_redline_fps", "ar_baggage (official)", or "workshop 3736209605" for an unlisted one (rank 0, not official).</summary>
    static string Label(MapInfo m) => m.Official ? $"{m.Name} (official)" : m.Rank > 0 && InArmsRace(m) ? $"#{m.Rank} {m.Name}" : m.Name; // another mode's list numbers are its own: no "#n" there

    static bool Unlisted(MapInfo m) => m.Rank == 0 && !m.Official;

    /// <summary>The key the map that is on goes by: its own, or the name the server logs when the bridge did not pick it.</summary>
    static string CurrentKey => S.Map != null ? Key(S.Map) : S.LogName ?? "";

    static string CurrentLabel() => S.Map != null ? Label(S.Map) : S.LogName ?? "?";

    /// <summary>The map that is on, in the mode it is on in.</summary>
    static bool IsOn(MapInfo m) => CurrentKey != "" && Key(m) == CurrentKey && ModeOf(m).Id == CurrentMode.Id;

    /// <summary>What a chat command takes to name this map and nothing else: a listed map's rank, an official map's name, else the id.</summary>
    static string ArgOf(MapInfo m) => m.Official ? m.Name : m.Rank > 0 && InArmsRace(m) ? m.Rank.ToString(CultureInfo.InvariantCulture) : m.Id.ToString(CultureInfo.InvariantCulture);

    static string Detail(MapInfo m) => m.Official ? "" : Unlisted(m) ? " (size unknown, first load can take a while)" : $" ({Size(m)}{(m.SizeMb >= 400 ? ", first load takes a while" : "")})";

    /// <summary>Detail with the map's first "why" tag in words (MapTags) inside it: " (41 MB, trending on the Workshop)".</summary>
    static string Detail(MapInfo m, string why) => why == "" ? Detail(m) : Detail(m) is { Length: > 0 } d ? $"{d[..^1]}, {why})" : $" ({why})";
    static string Size(MapInfo m) => m.SizeMb >= 1000 ? $"{m.SizeMb / 1000.0:0.0} GB" : $"{m.SizeMb} MB";

    // ---- a map by its key ----

    /// <summary>Every map the server knows, in every mode: Arms Race's list and official maps, the other modes' Workshop lists and official pools.</summary>
    static IEnumerable<MapInfo> AllKnown => Maps.Concat(Officials).Concat(Modes.All.Where(m => !m.ArmsRace).SelectMany(PoolOf));

    /// <summary>A map key as a player reads it: the map's label in whichever mode lists it - a Deathmatch or Wingman map by its name, not its Workshop
    /// id (B13) - else the key itself.</summary>
    static string NameOfKey(string key) => AllKnown.FirstOrDefault(m => Key(m) == key) is { } m ? Label(m) : key;

    /// <summary>A map's Workshop update time as the lists carry it (MapInfo.Updated), 0 when none of them does.</summary>
    static long UpdatedOf(string key) => AllKnown.Where(m => Key(m) == key).Select(m => m.Updated).DefaultIfEmpty(0).Max();

    /// <summary>Every Workshop map the server lists: maps.json and the other modes' lists, whose maps the menu browses too.</summary>
    static IEnumerable<MapInfo> AllListed => Maps.Concat(Modes.All.Where(x => !x.ArmsRace).SelectMany(ModeWorkshop));

    static string NameOf(string id) => AllListed.FirstOrDefault(m => m.Id.ToString() == id) is { } m ? Label(m) : $"workshop {id}";

    /// <summary>The map a vote key stands for: a map any mode lists or an official map, else an unlisted workshop id.</summary>
    static MapInfo MapOf(string key) => AllKnown.FirstOrDefault(m => Key(m) == key)
        ?? new MapInfo(0, long.TryParse(key, out var n) ? n : 0, long.TryParse(key, out _) ? $"workshop {key}" : key, 0);
}
