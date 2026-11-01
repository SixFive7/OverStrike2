// The game modes (#27 c, the owner's decision of 2026-11-01): Deathmatch and Wingman next to Arms Race. Arms Race is the server's game
// and its default - after every server restart, and again after 10 minutes with nobody on - and the other two come through the vote at
// a match's end (MapVote.cs): the ballot always holds a map of each other mode that suits the players on (Valve's maxplayers).
//
// A map loads in its own mode. The bridge puts the mode's game_type and game_mode in place right before the map command, and first tells
// the plugin with the server command os_mode <id> (Command), so MapSettingsReset neither puts game_type/game_mode back to the boot values
// at the old map's end nor blocks the new mode's own settings; the plugin switches its block list (BlockFile) and the Arms Race-only
// features (the grenade drops, the weapon ladder and catch-up, friendly fire's defaults) at the next map start.
//
// Shared like Hud.cs: the bridge compiles it (the vote, the map loads, the per-mode lists, --selftest) and the OverStrike2 plugin links it
// in (os_mode, the per-mode features and block files). Plain BCL only. The official pools are Valve's online map groups (mapgroupsMP)
// for each mode in the server's gamemodes.txt (CS2 1.41.9.4, read 2026-11-01); every one of those maps is installed on the server.
public static class Modes
{
    /// <summary>A game mode: its id (on the wire, in state.json, in file names), its name for players, the two convars that select it,
    /// Valve's maxplayers for it (gamemodes.txt; the vote offers a mode only while the humans on are at most this many), the server cfg
    /// the engine runs after Valve's own on every load of the mode (server/), and Valve's official maps for it.</summary>
    public sealed record Mode(string Id, string Name, int GameType, int GameMode, int MaxPlayers, string ValveCfg, string ServerCfg, string[] Official)
    {
        public bool ArmsRace => Id == ArmsRaceId;

        /// <summary>The mode's Workshop list, beside maps.json (Arms Race's is maps.json itself): the nightly sync's output.</summary>
        public string MapsFile => ArmsRace ? "maps.json" : $"maps-{Id}.json";

        /// <summary>MapSettingsReset's game-changers for the mode, beside its DLL: Arms Race keeps blocked.txt (values from the boot
        /// baseline where none is given), the others carry their own explicit values.</summary>
        public string BlockFile => ArmsRace ? "blocked.txt" : $"blocked-{Id}.txt";

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

    public static readonly Mode Wingman = new(WingmanId, "Wingman", 0, 2, 4, "gamemode_competitive2v2.cfg", "gamemode_competitive2v2_server.cfg",
        ["de_debris", "de_eldorado", "de_poseidon", "de_overpass", "de_vertigo", "de_nuke", "de_inferno"]); // the "2v2" template

    /// <summary>Every mode, in menu order; the first is the default.</summary>
    public static readonly Mode[] All = [ArmsRace, Deathmatch, Wingman];

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
}
