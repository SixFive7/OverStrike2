// The game modes on the bridge's side (the table is Modes.cs): which mode the server is in, the maps each mode rolls and offers,
// and how a map loads in its own mode.
//
//  - S.Mode (state.json) is the mode the server was last told to load in. Every server start is Arms Race (the cs2 service's
//    CS2_GAMETYPE/CS2_GAMEMODE and its start map), and so is a server that has had nobody on for ResetEmptyMinutes (EmptyServer.cs): it is
//    put on a random Arms Race map. A bridge that starts, and every map start the bridge did not ask for, asks the plugin which mode it was
//    told (bare os_mode, Modes.AskLine) and takes its word; a plugin that cannot say (one from before the question) has game_type and
//    game_mode read over RCON instead of guessing.
//  - A map carries its mode (MapInfo.Mode, "" = Arms Race). FireChange sends Modes.PrepareLine (os_mode, game_type, game_mode) and then
//    the map command; after the start of its own change the bridge sends os_mode again, for a plugin that reloaded, and the reply says
//    whether the map loaded in that mode (SyncMode); after a start it did not ask for it asks instead, and tells only a plugin that cannot
//    say. A change that never loads gives the old mode back (the load timeout in Tick).
//  - The pools: Arms Race is maps.json (MAPS_FILE) as it always was. Deathmatch and Wingman are Valve's official maps for the mode plus
//    the Workshop maps of maps-<mode>.json beside maps.json (the sync's output, same entry shape as maps.json; docs/reference.md).
//    Missing or empty, a mode has its official maps only. Read again when it changes, like maps.json (Reload.cs).
//  - Outside Arms Race the match length setting (!time) does not apply: Deathmatch runs Valve's 10-minute round, Wingman its rounds.
using System.Text.Json;
using System.Text.RegularExpressions;

static partial class Program
{
    static readonly Dictionary<string, List<MapInfo>> ModeMaps = new(); // mode id -> the entries of maps-<mode>.json (Workshop maps, and any official extras)

    static Modes.Mode CurrentMode => Modes.Of(S.Mode);
    static Modes.Mode ModeOf(MapInfo m) => Modes.Of(m.Mode);
    static bool InArmsRace(MapInfo m) => ModeOf(m).ArmsRace;

    static string? ModeMapsDir; // where the per-mode files are: beside MAPS_FILE, unless --selftest points elsewhere
    static string ModeMapsPath(Modes.Mode mode) => Path.Combine(ModeMapsDir ?? DataDir, mode.MapsFile);

    /// <summary>A mode's official maps: Arms Race's from OFFICIAL_MAPS (never rolled, as always), the others' Valve's pool plus any official
    /// extra the mode's file names - all rollable, because they are what the mode starts from.</summary>
    static List<MapInfo> OfficialsOf(Modes.Mode mode)
    {
        if (mode.ArmsRace) return Officials;
        var extra = ModeMaps.GetValueOrDefault(mode.Id)?.Where(m => m.Official).Select(m => m.Name) ?? [];
        return mode.Official.Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase).Select(n => new MapInfo(0, 0, n, 0, true, mode.Id)).ToList();
    }

    /// <summary>What a mode's dice take from: Arms Race's ranked list (officials never), or a mode's official maps and its Workshop maps.</summary>
    static List<MapInfo> PoolOf(Modes.Mode mode) => mode.ArmsRace ? Maps
        : OfficialsOf(mode).Concat(ModeWorkshop(mode)).GroupBy(Key).Select(g => g.First()).ToList();

    static List<MapInfo> ModeWorkshop(Modes.Mode mode) => ModeMaps.GetValueOrDefault(mode.Id)?.Where(m => !m.Official).ToList() ?? [];

    /// <summary>A mode's listed Workshop maps: Arms Race's maps.json, another mode's maps-&lt;mode&gt;.json without its official extras.</summary>
    static IEnumerable<MapInfo> WorkshopOf(Modes.Mode mode) => (mode.ArmsRace ? Maps : ModeWorkshop(mode)).Where(m => !m.Official);

    /// <summary>maps-&lt;mode&gt;.json read (at start, and whenever it changes): a JSON array in maps.json's entry shape. An entry needs an id
    /// (> 0) and a name, or "official": true and a name; anything else is skipped. Missing means the official maps only. Kept as it was when
    /// it does not parse, or when it holds less than half the Workshop maps loaded.</summary>
    static string ReloadModeMaps(Modes.Mode mode, bool force = false)
    {
        var path = ModeMapsPath(mode);
        var had = ModeMaps.GetValueOrDefault(mode.Id) ?? [];
        if (!File.Exists(path)) { ModeMaps[mode.Id] = []; return $"{mode.Id} maps: {path} not found - {mode.Name} has Valve's {mode.Official.Length} official maps only"; }
        List<MapInfo>? list;
        try { list = JsonSerializer.Deserialize(File.ReadAllText(path), Ctx.Default.ListMapInfo); }
        catch (Exception e) { return $"{mode.Id} maps: {path} unreadable ({Clip(e.Message, 120)}) - kept the {had.Count} loaded"; }
        var good = (list ?? []).Where(m => m != null && m.Name?.Trim() is { Length: > 0 } && (m.Official || m.Id > 0))
            .Select(m => m with { Name = m.Name.Trim(), Mode = mode.Id, Rank = m.Official ? 0 : Math.Max(0, m.Rank), Id = m.Official ? 0 : m.Id }).ToList();
        var (workshop, before) = (good.Count(m => !m.Official), had.Count(m => !m.Official));
        if (!force && before > 0 && workshop * 2 < before)
            return $"{mode.Id} maps: {path} has {workshop} Workshop maps, less than half of the {before} loaded - kept them";
        ModeMaps[mode.Id] = good;
        ExpireMarks(); // CrashWatch.cs: a map's Workshop update lifts its crash mark
        return $"{mode.Id} maps: {workshop} Workshop maps + {good.Count - workshop} official extra(s) from {path} (were {before}), {PoolOf(mode).Count} in the {mode.Name} pool";
    }

    /// <summary>A mode's own entry for this map, when that mode lists it: its Workshop list, its officials, Arms Race's maps.json.</summary>
    static MapInfo? ListedIn(Modes.Mode mode, MapInfo map) =>
        (mode.ArmsRace ? Maps.Concat(Officials) : PoolOf(mode)).FirstOrDefault(m => Key(m) == Key(map));

    /// <summary>A Deathmatch or Wingman official map by its whole name, in the first mode that has it, or null.</summary>
    static MapInfo? OfficialElsewhere(string name) =>
        Modes.All.Where(m => !m.ArmsRace).SelectMany(OfficialsOf).FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Why a pick cannot load in its mode right now, or null: more humans on than the map takes - a Wingman map as many a side as it has spawn
    /// points (Fits), Deathmatch Valve's maxplayers - the rule the ballot and its result follow too (VoteRound.cs). A Deathmatch map is never refused in
    /// Deathmatch: whoever is on plays it already.</summary>
    static string? ModeRefusal(MapInfo map)
    {
        var mode = ModeOf(map);
        if (mode.ArmsRace || (mode.Id == CurrentMode.Id && mode.Id != Modes.WingmanId)) return null;
        var humans = Humans();
        return Fits(map, humans) ? null : ModeFullLine(map, humans);
    }

    static string ModeFullLine(MapInfo map, int humans) => ModeOf(map) is { Id: Modes.WingmanId } wingman
        ? M.Refused($"{M.V(Clip(Label(map), 40))} has spawn points for {M.V(Modes.WingmanTakes(SpawnsPerSide(map)))} in {wingman.Name}, and {M.V($"{humans}")} are on - Not this map now")
        : M.Refused($"{M.V(ModeOf(map).Name)} takes {M.V($"{ModeOf(map).MaxPlayers} players")}, and {M.V($"{humans}")} are on - Not this map now");

    /// <summary>A Wingman map's spawn points per side as the bridge knows them: Valve's maps' own (Modes.WingmanOfficialSpawns), a Workshop map's as the sync
    /// counted them in its files (maps-wingman.json's spawnsT and spawnsCt, the smaller of the two), 0 while nobody has (Modes.PerSide takes 2 then).</summary>
    static int SpawnsPerSide(MapInfo map)
    {
        if (map.Official) return Modes.WingmanOfficialSpawns.GetValueOrDefault(map.Name);
        var listed = ListedIn(Modes.Wingman, map) ?? map;
        return listed.SpawnsT > 0 && listed.SpawnsCt > 0 ? Math.Min(listed.SpawnsT, listed.SpawnsCt) : 0;
    }

    /// <summary>Whether a map takes this many humans in its own mode: a Wingman map as many a side as it has spawn points; the others Valve's maxplayers.</summary>
    static bool Fits(MapInfo map, int humans) => ModeOf(map) is { Id: Modes.WingmanId } ? Modes.WingmanFits(SpawnsPerSide(map), humans) : ModeOf(map).Suits(humans);

    /// <summary>Whether a mode takes this many humans at all: Wingman when one of its maps the dice may take - an official one, or one on the server's disk -
    /// has spawn points for them; the others by Valve's maxplayers.</summary>
    static bool ModeSuits(Modes.Mode mode, int humans) =>
        mode.Id != Modes.WingmanId ? mode.Suits(humans) : PoolOf(mode).Any(m => Fits(m, humans) && CanRoll(m) && (m.Official || KeptOnDisk(m) || InStock(m)));

    /// <summary>A listed map of another mode by its Workshop id (a pasted link), or null.</summary>
    static MapInfo? ModeMapById(long id) => Modes.All.Where(m => !m.ArmsRace).SelectMany(ModeWorkshop).FirstOrDefault(m => m.Id == id);

    // ---- the mode on the server ----

    static Modes.Mode? ModeBeforeChange;   // the mode before the change now loading: a load that never lands gives it back
    static bool ModeProbing;               // read game_type/game_mode over RCON (a bridge that just started, a map start nobody asked for)
    static bool ModeProbeEnabled = true;   // --selftest switches the probe off, except where a test drives it
    static DateTime ModeProbeAt;

    [GeneratedRegex(@"=\s*""?(-?\d+)")] private static partial Regex ConVarIntRx();

    /// <summary>game_type and game_mode as RCON reads them, or null when it did not answer with both.</summary>
    static (int Type, int Mode)? ProbeConVars()
    {
        static int? Read(string? reply) => reply != null && ConVarIntRx().Match(reply) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;
        var (type, mode) = (Read(Send("game_type")), Read(Send("game_mode")));
        return type is { } t && mode is { } g ? (t, g) : null;
    }

    /// <summary>The plugin's word on the mode (Modes.AskLine, StateLine): the mode it was told and the one the map that is on loaded in, or
    /// null when it gives none - a plugin from before the question (it answers with CounterStrikeSharp's usage line), one with no told mode
    /// yet, no plugin, or no RCON.</summary>
    static (Modes.Mode Told, Modes.Mode? LoadedIn)? AskPluginMode()
    {
        var reply = Send(Modes.AskLine);
        Log($"{Modes.AskLine} -> {reply ?? "(rcon failed)"}");
        return Modes.ParseState(reply) is { Told: { } told } said ? (told, said.LoadedIn) : null;
    }

    /// <summary>The plugin's told mode becomes the one on record: it heard every os_mode, an operator's before a changelevel included, while
    /// the convars can hold the mode CS2 brought back under an official map. The convar probe is not needed after it.</summary>
    static void TakePluginMode(Modes.Mode told, Modes.Mode? loadedIn)
    {
        ModeProbing = false;
        if (told.Id == CurrentMode.Id) return;
        var came = loadedIn is { } l && l.Id != told.Id ? $" (the map that is on loaded in {l.Name})" : "";
        Log($"mode: the plugin was told {told.Name}{came}, not {CurrentMode.Name} as last recorded - taking the plugin's");
        S.Mode = told.Id; Save();
    }

    /// <summary>From Tick: the mode probe, when one is due - every 10 s until RCON answers. The plugin's word first (AskPluginMode); without it,
    /// the convars. Convars that select none of our modes (a map switched to Casual) leave the mode as recorded: the next change sets them again.
    /// The convars alone can mislead after an operator's change: read before the plugin has put the told values back on the map's first frame,
    /// they say the mode CS2 brought back.</summary>
    static void ProbeModeIfDue(DateTime now)
    {
        if (!ModeProbeEnabled || !ModeProbing || now < ModeProbeAt) return;
        ModeProbeAt = now.AddSeconds(10);
        if (AskPluginMode() is { } said) { TakePluginMode(said.Told, said.LoadedIn); return; }
        if (ProbeConVars() is not { } cv) { Log("mode: RCON did not say which mode the server is in - asking again in 10 s"); return; }
        ModeProbing = false;
        if (Modes.FromConVars(cv.Type, cv.Mode) is not { } on) { Log($"mode: game_type {cv.Type}, game_mode {cv.Mode} is none of ours - keeping {CurrentMode.Name}"); return; }
        if (on.Id == CurrentMode.Id) return;
        Log($"mode: the server is in {on.Name}, not {CurrentMode.Name} as last recorded - taking the server's");
        S.Mode = on.Id; Save();
    }

    static bool ModeVerifyDue; // the bridge's own change just landed: check that the server came up in the mode it was told
    static int ModeReloads;    // loads again of the change now on, to put its mode right (one at most)

    /// <summary>The words of the plugin's reply to os_mode (Settings.SetMode; --selftest holds the two together) when the map that is on did
    /// not load in the mode it is told: that mode's block list and features wait for the next map start.</summary>
    const string ModeNotLoadedWords = "at the next map start";

    /// <summary>After every map start: tell the plugin the mode again (it may have reloaded since it was told) - and after the bridge's own
    /// change, check the mode it came up in. A changelevel from a Workshop map to an official one restores every convar CS2 saved when the
    /// server last went from an official map to a Workshop map, game_type and game_mode included (measured live on 2026-11-01: de_dust2
    /// told Deathmatch came up in Arms Race; `map` does the same), so an official map of another mode can come up in the old mode. Then it
    /// loads once more - official to official, which restores nothing. A Workshop map is not loaded again: nothing restores there.
    /// The plugin's reply is what tells. It read the mode at the map start, and the convars cannot say any more by now: its block, which
    /// looks every second, has put the told values back before this runs, 1.5 s after the start (cs_office told Deathmatch stayed in Arms
    /// Race on 2026-11-01 with both convars reading Deathmatch). The convars still count, for a plugin that does not answer.
    /// After a map start the bridge did not ask for (an operator's os_mode and changelevel), the mode on its record can be the one CS2 brought
    /// back, read by the probe before the plugin put the told values back: told again, it would undo the operator's os_mode and call off the
    /// plugin's own second load of the map (live 2026-11-07, plugin 5.1.0). So it asks the plugin instead and takes its word (AskPluginMode);
    /// a plugin from before the question gives none, and is told as it always was.</summary>
    static void SyncMode()
    {
        if (!ModeVerifyDue && AskPluginMode() is { } said) { TakePluginMode(said.Told, said.LoadedIn); return; }
        var told = Send(Modes.SyncLine(CurrentMode));
        Log($"{Modes.SyncLine(CurrentMode)} -> {told ?? "(rcon failed)"}");
        if (!ModeVerifyDue) return;
        ModeVerifyDue = false;
        var notLoaded = told?.Contains(ModeNotLoadedWords, StringComparison.Ordinal) == true;
        var cv = ProbeConVars();
        if (cv == null && !notLoaded) { Log("mode: RCON did not say which mode the map came up in"); return; }
        var want = CurrentMode;
        var differ = cv is { } read && read != (want.GameType, want.GameMode);
        if (!differ && !notLoaded) return;
        var on = differ ? Modes.FromConVars(cv!.Value.Type, cv.Value.Mode) : null; // what the convars say, when they still differ
        var came = !differ ? "another mode, the plugin says" : on?.Name ?? $"game_type {cv!.Value.Type}, game_mode {cv.Value.Mode}";
        if (S.Map is not { Official: true } map || ModeReloads > 0 || Pending != null || LoadPending)
        {
            Log($"mode: {CurrentLabel()} came up in {came}, not {want.Name} - left as it is");
            if (on != null) { S.Mode = on.Id; Save(); }
            return;
        }
        ModeReloads++;
        Log($"mode: {Label(map)} came up in {came}, not {want.Name} (CS2 restored the convars it saved before the Workshop maps) - loading it once more in {want.Name}");
        var line = Modes.PrepareLine(want);
        if (Send(line) == null || Send($"changelevel {map.Name}") is not { } reply) { Log("mode: RCON did not take the second load"); return; }
        Log($"{line}; changelevel {map.Name} -> {reply}");
        AwaitLoad(Now()); // MapChange.cs: its own map start, from now
        LastNamedMapStart = default; // the second load's map start must not pass for the first load's second line (it came 3 s later, live)
        Announce($"{Label(map)} loads once more, in {want.Name}", 10);
    }

    /// <summary>The server is starting anew: it boots in Arms Race.</summary>
    static void ModeAtServerStart()
    {
        if (!CurrentMode.ArmsRace) Log($"mode: the server restarted - back to {Modes.Default.Name} (was {CurrentMode.Name})");
        S.Mode = Modes.Default.Id; ModeBeforeChange = null;
    }

    /// <summary>From FireChange, right before the map command: the plugin first, then the mode's two convars. Returns false when RCON did
    /// not take it; the map command is then not sent either.</summary>
    static bool PrepareMode(MapInfo map)
    {
        var mode = ModeOf(map);
        var line = Modes.PrepareLine(mode);
        var reply = Send(line);
        Log($"{line} -> {reply ?? "(rcon failed)"}");
        if (reply == null) return false;
        if (mode.Id != CurrentMode.Id) { ModeBeforeChange = CurrentMode; Log($"mode: {CurrentMode.Name} -> {mode.Name} for {Label(map)}"); }
        else ModeBeforeChange = null;
        S.Mode = mode.Id;
        return true;
    }

    /// <summary>A change that never landed (the load timeout, or a map command RCON did not take): the mode it was going to goes back.</summary>
    static void ModeBack()
    {
        if (ModeBeforeChange is not { } before) return;
        ModeBeforeChange = null;
        var line = Modes.PrepareLine(before);
        Log($"mode: the change did not land - back to {before.Name}: {line} -> {Send(line) ?? "(rcon failed)"}");
        S.Mode = before.Id;
    }

    /// <summary>The empty server's way back to Arms Race (from ResetWhenEmpty, once nobody has been on for ResetEmptyMinutes): a random
    /// Arms Race map, unless one is on already, a change is under way or the maintenance flag is set.</summary>
    static DateTime ModeResetRetryAt;
    static void ModeBackToDefault(DateTime now)
    {
        if (CurrentMode.ArmsRace || Pending != null || LoadPending || now < ModeResetRetryAt || InMaintenance()) return;
        ModeResetRetryAt = now.AddMinutes(1);
        var from = CurrentMode.Name;
        if (!AutoRoll($"Nobody on for {ResetEmptyMinutes} minutes - Back to {Modes.Default.Name}", Modes.Default)) { Log($"reset: no {Modes.Default.Name} map is eligible to roll - asking again in a minute"); return; }
        Log($"reset: nobody on for over {ResetEmptyMinutes} min in {from} - back to {Modes.Default.Name} on {Label(Pending!.Map)}");
    }

    /// <summary>The mode's name after a map's label when it is not the mode the server is in: what the ballot and the top line show.</summary>
    static string ModeTag(MapInfo m) => ModeOf(m).Id == CurrentMode.Id ? "" : $" - {ModeOf(m).Name}";

    /// <summary>The health page's line about the modes.</summary>
    static string ModeHealth() => $"mode {CurrentMode.Id}; pools: " + string.Join(", ", Modes.All.Select(m => $"{m.Id} {(m.ArmsRace ? Maps.Count : PoolOf(m).Count)}"));
}
