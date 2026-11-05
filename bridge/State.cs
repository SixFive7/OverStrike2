// What the bridge keeps across its restarts (state.json, written by Save) and the map list's entries, and the JSON code they are read and written with.
using System.Text.Json.Serialization;

record MapInfo(int Rank, long Id, string Name, int SizeMb, bool Official = false, string Mode = "", long Updated = 0); // Official: from OFFICIAL_MAPS or a mode's official pool, no rank or id; Rank 0 and not official: unlisted workshop map; Mode: the game mode it loads in (Modes.cs), "" for Arms Race; Updated: the Workshop's time_updated (unix seconds) when the list carries it, 0 unknown

sealed class State
{
    public string? ServerToken { get; set; }          // last X-Server-Instance-Token seen
    public MapInfo? Map { get; set; }                 // current map when the bridge picked it
    public string? LogName { get; set; }              // map name as the server logs it
    public string? PickedBy { get; set; }             // "the vote", "the dice": what picked the map that is on
    public bool CleanShutdown { get; set; }
    public Dictionary<string, int> Crashes { get; set; } = new();
    public Dictionary<string, long> CrashStamps { get; set; } = new(); // crash key -> the map's Workshop update time when it was marked: a newer update lifts the mark (CrashWatch.cs)
    public HashSet<string> Seen { get; set; } = new();
    public int RoundMinutes { get; set; } = Program.DefaultRoundMinutes;   // survives restarts; re-applied after every map load
    public List<MapInfo> History { get; set; } = new();  // maps played, oldest first, at most 20: !previous and the dice's recent-map rule
    public string? RoundBy { get; set; }              // who set a match length that is not the default
    public string Mode { get; set; } = Modes.ArmsRaceId;              // the game mode the server was last told to load in (ModeSwitch.cs): Arms Race after every restart and after 10 min with nobody on
    public Dictionary<string, string> PlayerCrashes { get; set; } = new(); // crash key -> why: maps that crashed players' games (BrokenWhy.cs)
    public Dictionary<string, List<StockEntry>> Stock { get; set; } = new(); // the discovery stock, a lane per mode (RandomQueue.cs)
    public Dictionary<string, List<long>> Shown { get; set; } = new();       // map key -> when it was a card on a ballot (unix seconds), the last ShownFadeDays (VoteRound.cs)
    public HashSet<string> Met { get; set; } = new();                        // every map shown on a ballot or played here: NEW TO US is the rest
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(State)), JsonSerializable(typeof(List<MapInfo>)), JsonSerializable(typeof(Dictionary<string, Dictionary<string, int>>)), JsonSerializable(typeof(RemovedFile)), JsonSerializable(typeof(Dictionary<string, string>)), JsonSerializable(typeof(QueuesFile))]
partial class Ctx : JsonSerializerContext { }
