// The game modes: Deathmatch and Wingman next to Arms Race. Arms Race is the server's game and its default - after every server restart,
// and again after 10 minutes with nobody on - and the other two come through the vote (MapVote.cs): the ballot always
// holds a map of each other mode that suits the players on (Valve's maxplayers).
//
// A map loads in its own mode. The bridge puts the mode's game_type and game_mode in place right before the map command, and first tells
// the plugin with the server command os_mode <id> (Command), so the plugin's settings module neither puts game_type/game_mode back to the
// boot values at the old map's end nor blocks the new mode's own settings; the plugin switches to the mode's values of its block list
// (BlockFile) and the Arms Race-only features (the grenade drops, the weapon ladder and catch-up, friendly fire's defaults) at the next
// map start. After a map start the bridge did not ask for, it asks the plugin which mode it was told (AskLine, StateLine) rather than tell
// it the one on its own record.
//
// Shared like Hud.cs: the bridge compiles it (the vote, the map loads, the per-mode lists, --selftest) and the OverStrike2 plugin links it
// in (os_mode, the per-mode features and the block file). Plain BCL only. The official pools are Valve's online map groups (mapgroupsMP)
// for each mode in the server's gamemodes.txt (CS2 1.41.9.4, read 2026-11-01); every one of those maps is installed on the server.
public static class Modes
{
    /// <summary>A game mode: its id (on the wire, in state.json, in file names), its name for players, the two convars that select it,
    /// the most players it takes - Valve's maxplayers for Arms Race and Deathmatch (gamemodes.txt; the vote offers a mode only while the humans on are at most
    /// this many), Wingman the same 16 (Valve's is 4: any number plays on any Wingman map here) - the server cfg
    /// the engine runs after Valve's own on every load of the mode (server/), and Valve's official maps for it.</summary>
    public sealed record Mode(string Id, string Name, int GameType, int GameMode, int MaxPlayers, string ValveCfg, string ServerCfg, string[] Official)
    {
        public bool ArmsRace => Id == ArmsRaceId;

        /// <summary>The mode's Workshop list, beside maps.json (Arms Race's is maps.json itself): the sync's output.</summary>
        public string MapsFile => ArmsRace ? "maps.json" : $"maps-{Id}.json";

        /// <summary>The two convars that select the mode, as an RCON command.</summary>
        public string ConVars => $"game_type {GameType};game_mode {GameMode}";

        /// <summary>Whether the vote may offer this mode with this many humans on.</summary>
        public bool Suits(int humans) => humans <= MaxPlayers;
    }

    public const string ArmsRaceId = "armsrace", DeathmatchId = "deathmatch", WingmanId = "wingman";

    /// <summary>The plugin's server command: os_mode &lt;id&gt;. See the file summary and docs/reference.md.</summary>
    public const string Command = "os_mode";

    public static readonly Mode ArmsRace = new(ArmsRaceId, "Arms Race", 1, 0, 16, "gamemode_armsrace.cfg", "gamemode_armsrace_server.cfg",
        ["ar_baggage", "ar_pool_day", "ar_shoots", "ar_shoots_night"]);

    public static readonly Mode Deathmatch = new(DeathmatchId, "Deathmatch", 1, 2, 16, "gamemode_deathmatch.cfg", "gamemode_deathmatch_server.cfg",
        ["de_dust2", "de_mirage", "de_inferno", "de_vertigo", "de_cache",           // mg_casualalpha
         "de_train", "de_anubis", "de_ancient_night", "de_overpass", "de_nuke",     // mg_casualdelta
         "de_boulder", "de_fachwerk", "cs_shelter",                                 // mg_casualcharlie
         "cs_office", "cs_italy"]);                                                 // mg_hostage

    public static readonly Mode Wingman = new(WingmanId, "Wingman", 0, 2, 16, "gamemode_competitive2v2.cfg", "gamemode_competitive2v2_server.cfg",
        ["de_debris", "de_eldorado", "de_poseidon", "de_overpass", "de_vertigo", "de_nuke", "de_inferno"]); // the "2v2" template

    /// <summary>Every mode, in menu order; the first is the default.</summary>
    public static readonly Mode[] All = [ArmsRace, Deathmatch, Wingman];

    // ---- a Wingman map's spawn points per side: the map browser's recommended count ("3v3"), never a limit - any number plays on any Wingman map ----

    /// <summary>The spawn points per side of Valve's own Wingman maps, read from their entity lumps (2026-11-09): the 2v2 set ("spawnpoints.2v2") that Wingman
    /// switches on where a map has one - inferno, nuke and overpass - else every team spawn. 3v3 on de_inferno was tried on a test copy that day: every player spawned.</summary>
    public static readonly IReadOnlyDictionary<string, int> WingmanOfficialSpawns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["de_debris"] = 9, ["de_eldorado"] = 10, ["de_poseidon"] = 5, ["de_overpass"] = 4, ["de_vertigo"] = 15, ["de_nuke"] = 4, ["de_inferno"] = 4,
    };

    public static Mode Default => ArmsRace;

    /// <summary>The mode with this id, or null. An empty id (a map entry from before the modes) is Arms Race.</summary>
    public static Mode? Find(string? id)
    {
        var t = (id ?? "").Trim();
        return t == "" ? ArmsRace : All.FirstOrDefault(m => m.Id.Equals(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The mode with this id, else the default.</summary>
    public static Mode Of(string? id) => Find(id) ?? Default;

    /// <summary>The mode these two convars select, or null when they select none of ours (a map switched to Casual, say).</summary>
    public static Mode? FromConVars(int gameType, int gameMode) => All.FirstOrDefault(m => m.GameType == gameType && m.GameMode == gameMode);

    /// <summary>What the bridge sends right before a map command: the plugin is told first, then the convars are set.</summary>
    public static string PrepareLine(Mode mode) => $"{Command} {mode.Id};{mode.ConVars}";

    /// <summary>What the bridge sends after every map start, so a plugin that reloaded knows the mode again.</summary>
    public static string SyncLine(Mode mode) => $"{Command} {mode.Id}";

    /// <summary>What the bridge asks after a map start it did not ask for: os_mode without a mode. The plugin answers with StateLine and
    /// changes nothing; a plugin from before the question answers otherwise - CounterStrikeSharp's usage line, os_mode taking one argument
    /// there - and changes nothing either, so ParseState gives null and the bridge reads the convars as it always did.</summary>
    public static string AskLine => Command;

    /// <summary>The plugin's answer to AskLine: the mode it was told - an operator's os_mode before a map command included - the mode the map
    /// that is on loaded in, and whose list is on, each an id or "none"; after "; " the plugin's own words.</summary>
    public static string StateLine(Mode? told, Mode? loadedIn, Mode on, string words = "") =>
        $"{Command}: told {told?.Id ?? "none"}, loaded in {loadedIn?.Id ?? "none"}, on {on.Id}{(words == "" ? "" : $"; {words}")}";

    /// <summary>StateLine read back out of an RCON answer, which may hold other lines: the told mode and the one the map loaded in (null for
    /// "none"); null when the answer holds no such line, or names a mode that is none of ours.</summary>
    public static (Mode? Told, Mode? LoadedIn)? ParseState(string? reply)
    {
        if (reply == null) return null;
        var head = $"{Command}: told ";
        foreach (var raw in reply.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(head, StringComparison.Ordinal)) continue;
            if (line[head.Length..].Split(';')[0].Split(", ") is not [var told, var loadedIn, var on] || !loadedIn.StartsWith("loaded in ", StringComparison.Ordinal)
                || !on.StartsWith("on ", StringComparison.Ordinal)) return null;
            static (bool Ok, Mode? Mode) Of(string id) => id == "none" ? (true, null) : All.FirstOrDefault(m => m.Id == id) is { } m ? (true, m) : (false, null);
            var (t, l, o) = (Of(told), Of(loadedIn["loaded in ".Length..]), Of(on["on ".Length..]));
            return t.Ok && l.Ok && o is { Ok: true, Mode: not null } ? (t.Mode, l.Mode) : null;
        }
        return null;
    }

    /// <summary>The game-changers no map may set (BlockList.cs), every mode's in one file beside the plugin's DLL: a convar a line with the
    /// value it keeps in each mode, in the order of All (armsrace deathmatch wingman), then // and why. Baseline in a mode's place keeps the
    /// server's own value there (its baseline, taken when the boot map ends).</summary>
    public const string BlockFile = "blocked.txt", Baseline = "*";

    /// <summary>One mode's game-changers out of the block file's text: each convar with that mode's value (none for Baseline), and the errors -
    /// a line without a value for every mode, and what BlockList.Parse finds (a name that is no convar name, a name twice), each with the
    /// file's own line number.</summary>
    public static List<BlockList.Entry> Blocked(string text, Mode mode, out List<string> errors)
    {
        var column = 1 + Array.FindIndex(All, m => m.Id == mode.Id);
        var lines = text.Replace("\r", "").Split('\n');
        var wrong = new List<string>();
        var one = new System.Text.StringBuilder(); // the same lines, each with this mode's value alone: BlockList.Parse's own format
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            var body = (comment >= 0 ? line[..comment] : line).Trim();
            var words = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 1 + All.Length) one.Append(words[0]).Append(words[column] == Baseline ? "" : " " + words[column]).Append(' ').Append(comment >= 0 ? line[comment..] : "");
            else if (words.Length > 0) wrong.Add($"line {i + 1}: '{body}' is not a convar and a value for each of the {All.Length} modes ({string.Join(" ", All.Select(m => m.Id))}; {Baseline} for the server's own)");
            one.Append('\n');
        }
        var list = BlockList.Parse(one.ToString(), out var parsed);
        errors = [.. wrong, .. parsed];
        return list;
    }
}
