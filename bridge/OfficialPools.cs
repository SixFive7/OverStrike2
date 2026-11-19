// The official maps of each mode as the installed game has them, and the maps the server turned away.
//
//  - The game's own pools (core's GamePools.cs): at every engine start the restart watcher reads each game mode's official maps out of the
//    game's own files - gamemodes.txt and csgo_english.txt in pak01_dir.vpk, and which maps' files are installed - and posts them here (POST
//    /pools). The bridge keeps the newest report in game-pools.txt in its state folder, reads it again at its own start, and plays Deathmatch's
//    and Wingman's official maps from it: the installed maps of the game mode whose game_type and game_mode are the mode's. Modes.cs's tables
//    are the fallback, while there is no report or it has no such mode. So a map Valve brings back is in the pools, the catalog and the dice by
//    itself (its picture comes with the next HUD release), and one Valve takes away is out of them. Arms Race's official maps stay the settings'
//    (bridge.officialMaps, never rolled); its pool is compared and logged like the others. Each report is compared with Modes.cs's tables (and
//    Arms Race's with the settings), and every difference is logged and shown on GET /health, so the table can follow at leisure.
//  - Our own extras (Modes.Mode.Extras, and the official entries of a mode's list file) join a mode's pool when the report says their files are
//    installed - every one of them without a report.
//  - Valve's names of the official maps, from the report (else Modes.ValveNames), are search words in the map browser (SearchIndex.cs).
//  - A changelevel the server answers with "invalid map name" (engine2's own words, "changelevel %s:  invalid map name") fails at once: nothing is
//    awaited, the mode goes back, and the map leaves every pool until the server process is a new one (MapChange.cs) - rather than ten minutes of
//    waiting for a load that never comes, with every vote refused, and the dice free to roll it again.
static partial class Program
{
    static GamePools.Found? GameReport;            // the newest report of the game's own pools, or null: Modes.cs's tables
    static DateTime GameReportAt;                  // when it came (the file's time, after a restart of the bridge)
    static HashSet<string> GameInstalled = new(StringComparer.OrdinalIgnoreCase); // the report's installed maps (empty: nothing known)
    static readonly HashSet<string> Unloadable = new(StringComparer.OrdinalIgnoreCase); // official maps the server refused this process ("invalid map name")

    /// <summary>Official maps kept out because the HUD does not show on them (Modes.HudLess): out of every pool, the catalog, the dice and the ballot
    /// (OfficialsOf), and a pick of one - typed, a card, Previous, a vote's winner - is refused with HudLessRefusal's words (Unplayable).</summary>
    static HashSet<string> HudLess = new(Modes.HudLess, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the HUD does not show on this official map: one of HudLess, or one the game's report says it ships as a community addon (its addons
    /// line, GamePools.AddonsIn - the way CS2 ships the three of Modes.HudLess).</summary>
    static bool OffForHud(string map) => HudLess.Contains(map) || GameReport?.Addons.Contains(map, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>Whether this is an official map the HUD does not show on (OffForHud).</summary>
    static bool IsHudLess(MapInfo m) => m.Official && OffForHud(m.Name);

    /// <summary>The refusal of a map the HUD does not show on: which, and why.</summary>
    static string HudLessRefusal(MapInfo m) => M.Refused($"{M.A(Clip(m.Name, 40))} is off for now - The HUD does not show on it. Pick another.");

    /// <summary>A mode's official maps that HudLess keeps out of its pool (OfficialsOf), as they would be in it: still in the catalog (LiveState.cs), so a card's
    /// Play, a HISTORY card's Vote for it and a typed name reach the bridge, whose refusal says why.</summary>
    static IEnumerable<MapInfo> HudLessOf(Modes.Mode mode) => mode.ArmsRace || HudLess.Count == 0 && GameReport?.Addons is not { Count: > 0 } ? []
        : (GamePool(mode) ?? mode.Official.Where(InstalledNow)).Concat(mode.Extras.Where(InstalledNow)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => OffForHud(n) && !Unloadable.Contains(n)).Select(n => new MapInfo(0, 0, n, 0, true, mode.Id));

    /// <summary>An official map the HUD does not show on, by its whole name, in the first mode that has it (HudLessOf), or null: what Find hands a pick of one,
    /// so that its refusal says why rather than that no map has that name.</summary>
    static MapInfo? HudLessByName(string name) =>
        OffForHud(name) ? Modes.All.SelectMany(HudLessOf).FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) : null;

    static string GamePoolsFile => Path.Combine(StateDir, "game-pools.txt");

    /// <summary>At the bridge's start: the last report the restart watcher sent, as kept in game-pools.txt.</summary>
    static string LoadGamePools()
    {
        if (!File.Exists(GamePoolsFile)) return "pools: no report of the game's own pools yet - Modes.cs's tables until the restart watcher sends one";
        string body;
        try { body = File.ReadAllText(GamePoolsFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"pools: {GamePoolsFile} unreadable ({Clip(e.Message, 120)}) - Modes.cs's tables"; }
        if (BridgeWire.ReadPools(body) is not { } report) return $"pools: {GamePoolsFile} is no report of the game's pools - Modes.cs's tables";
        TakeReport(report, File.GetLastWriteTime(GamePoolsFile));
        return $"pools: CS2 {report.Build}'s own, as reported {GameReportAt:yyyy-MM-dd HH:mm} - {string.Join("; ", PoolDifferences())}";
    }

    /// <summary>POST /pools/&lt;secret&gt;: the restart watcher's report of the game's own pools (BridgeWire.PoolsBody), at every engine start.</summary>
    static void PoolsReport(System.Net.HttpListenerContext c) => OnLoop(c, TakePools, "pools");

    /// <summary>A report taken: played from now on, kept in game-pools.txt, compared with Modes.cs's tables in the log. "ok: ..." with the comparison.</summary>
    static string TakePools(string body)
    {
        if (BridgeWire.ReadPools(body) is not { } report)
        {
            Log($"pools: a report that is no os2-pools 1 with a pool - refused, the pools stay {(GameReport is { } r ? $"CS2 {r.Build}'s" : "Modes.cs's")}");
            return "refused: no os2-pools 1 report with a pool in it";
        }
        TakeReport(report, Now());
        try { WriteAtomic(GamePoolsFile, body); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log($"pools: could not keep the report in {GamePoolsFile} ({Clip(e.Message, 120)}) - it holds until the bridge restarts"); }
        var compared = string.Join("; ", PoolDifferences());
        Log($"pools: CS2 {report.Build} reported its own pools - {compared}");
        return $"ok: CS2 {report.Build} - {compared}";
    }

    static void TakeReport(GamePools.Found report, DateTime at)
    {
        GameReport = report; GameReportAt = at;
        GameInstalled = new(report.Installed, StringComparer.OrdinalIgnoreCase);
        CatalogVersion++; // the menu's map list follows, and with it the dice's pools
        ComposeSearch();  // SearchIndex.cs: Valve's names as search words
    }

    /// <summary>The game's own pool for a mode - the installed maps of the game mode whose game_type and game_mode are the mode's - or null: no report,
    /// or none of the mode with a map in it.</summary>
    static List<string>? GamePool(Modes.Mode mode) =>
        GameReport?.Pools.FirstOrDefault(p => p.TypeValue == mode.GameType && p.ModeValue == mode.GameMode)?.Playable is { Count: > 0 } maps ? maps : null;

    /// <summary>Whether a map's files are installed as the last report says: true while nothing is known.</summary>
    static bool InstalledNow(string map) => GameInstalled.Count == 0 || GameInstalled.Contains(map);

    /// <summary>Valve's name of an official map: the game's own, else Modes.ValveNames; "" for any other map.</summary>
    static string ValveName(string map) => GameReport?.Names.GetValueOrDefault(map) ?? Modes.ValveNames.GetValueOrDefault(map) ?? "";

    /// <summary>Each mode's official maps against the table they would otherwise come from - Modes.cs's, Arms Race's the settings' - for the log and
    /// GET /health: equal, or what the game adds and lacks; the maps it names whose files are not installed; our extras that join.</summary>
    static List<string> PoolDifferences()
    {
        var lines = new List<string>();
        foreach (var mode in Modes.All)
        {
            var own = mode.ArmsRace ? Officials.Select(o => o.Name).ToList() : [.. mode.Official];
            var table = mode.ArmsRace ? "the settings' officials" : "Modes.cs";
            var pool = GameReport?.Pools.FirstOrDefault(p => p.TypeValue == mode.GameType && p.ModeValue == mode.GameMode);
            string line;
            if (GameReport is null) line = $"{mode.Id} {own.Count} from {table}";
            else if (pool is null || pool.Playable.Count == 0) line = $"{mode.Id}: none in the game's report - {own.Count} from {table}";
            else
            {
                var added = pool.Playable.Where(m => !own.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
                var lacked = own.Where(m => !pool.Playable.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
                var differs = (added.Count > 0 ? $", not in {table}: {string.Join(" ", added)}" : "") + (lacked.Count > 0 ? $", only in {table}: {string.Join(" ", lacked)}" : "");
                line = added.Count + lacked.Count == 0 ? $"{mode.Id} {pool.Playable.Count} = {table}"
                    : mode.ArmsRace ? $"{mode.Id} {own.Count} from {table}, which stay - the game's {pool.Playable.Count}{differs}"
                    : $"{mode.Id} {pool.Playable.Count} from the game{differs}";
                if (pool.Missing.Count > 0) line += $", named but not installed: {string.Join(" ", pool.Missing)}";
            }
            var extras = mode.ArmsRace ? [] : mode.Extras.Where(InstalledNow).ToList();
            if (extras.Count > 0) line += $", ours too: {string.Join(" ", extras)}";
            var dropped = mode.ArmsRace ? [] : mode.Extras.Where(e => !InstalledNow(e)).ToList();
            if (dropped.Count > 0) line += $", ours not installed: {string.Join(" ", dropped)}";
            var off = HudLessOf(mode).Select(m => m.Name).ToList();
            if (off.Count > 0) line += $", off - the HUD does not show on them: {string.Join(" ", off)}";
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>GET /health's line: where the official maps come from, and what the server turned away this process.</summary>
    static string PoolsHealth() =>
        (GameReport is { } r ? $"pools: CS2 {r.Build}'s own, reported {GameReportAt:yyyy-MM-dd HH:mm} - " : "pools: no report of the game's own yet - ")
        + string.Join("; ", PoolDifferences())
        + (Unloadable.Count > 0 ? $"; refused by the server this process: {string.Join(" ", Unloadable.Order(StringComparer.Ordinal))}" : "");

    // ---- the server's own word that a map is not there ----

    /// <summary>Whether a map command's answer is the engine's refusal of a map it does not have ("changelevel %s:  invalid map name").</summary>
    static bool InvalidMapName(string reply) => reply.Contains("invalid map name", StringComparison.OrdinalIgnoreCase);

    /// <summary>The server process is a new one (a quit, a crash's new spawn counter): the maps it turned away may be there now.</summary>
    static void ForgetUnloadable(string why)
    {
        if (Unloadable.Count == 0) return;
        Log($"pools: {why} - {string.Join(" ", Unloadable.Order(StringComparer.Ordinal))} may load again");
        Unloadable.Clear();
        CatalogVersion++;
    }
}
