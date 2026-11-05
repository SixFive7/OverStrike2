using System.Diagnostics;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The steward of the server's game settings: it puts back, when a map ends, every game setting the map changed; it blocks the
/// game-changers no map may set at all (blocked.txt beside the DLL, a value per game mode: ../../bridge/Modes.cs, ../../bridge/BlockList.cs);
/// and it makes the players' changes from the menu's fun page (gg_fun, ../../bridge/Fun.cs), which last until the map changes and never
/// become the baseline nor get blocked. The fun page's effects that are no convar (Vampire, Ghost, no recoil, the grenade drops, catch-up)
/// it keeps like convars: each starts at its default - on for the grenade drops and catch-up (Fun.EffectsOn), off for the rest - and is
/// back at it after every map end and after the reset after nobody has been on (gg_fun reset); EffectChanged tells the modules that act on
/// them.
///
/// Why it is needed (measured live on 2026-10-22, CS2 1.41.9.1/14191): <c>host_workshop_map</c> - how the bridge loads every Workshop map -
/// resets no game convar. fs_highsky's point_servercommand <c>sv_airaccelerate 20</c> survived into aim_snow_tundra, and sv_friction,
/// sv_jump_impulse and phys_timescale set over RCON survived a host_workshop_map too. The engine does save the convars when the server goes
/// from an official map to a Workshop map and restores them when a <c>changelevel</c> goes back to an official one, but between two Workshop
/// maps nothing is restored. On every load the engine runs server.cfg and the Arms Race cfgs again, so whatever those files name is set
/// again by the engine and is left to it here.
///
/// What it manages: every game convar (flagged FCVAR_GAMEDLL - "sv" in cvarlist), those of convars.txt and those only the running build has
/// (ConVarNames.cs), that the engine cfgs do not
/// name and that is not on <see cref="Never"/>. Their wanted value (the baseline) is taken when the first map of the server process ends:
/// the boot map (CS2_STARTMAP, ar_shoots, which runs no commands of its own), once the command line, the cfgs and the engine's own load-time
/// settings have all run - think_limit 0, for one, is set by the engine during every load, after OnMapStart. When the plugin is loaded
/// while a map is on, the baseline saved earlier by this same server process is used (else the live values). game_type and game_mode
/// always want CS2_GAMETYPE / CS2_GAMEMODE. The blocked and the fun convars get a baseline the same way, cfg-named or not.
/// A map keeps its own settings for as long as it is on - except the blocked ones - and they are undone at its end (OnMapEnd), before the
/// next map's cfgs and entities run. Nothing outside the game convars, the block list and the fun page's effects is ever touched (hostname,
/// passwords, CSTV, MultiAddonManager).</summary>
sealed class Settings(BasePlugin plugin, ILogger log)
{
    /// <summary>The settings module of the Over Strike 2 plugin, with the host's version.</summary>
    const string ModuleVersion = OverStrike2Plugin.Version;

    /// <summary>Never touched: engine bookkeeping that changes by itself between maps, and the bridge's own runtime setting.</summary>
    static readonly HashSet<string> Never = new(StringComparer.OrdinalIgnoreCase)
    {
        "nextlevel", "nextmode", "mp_backup_round_file_last", "steamworks_sessionid_server", "sv_skirmish_id",
        "sv_versus_screen_scene_id", "logaddress_token_secret",
        "mp_roundtime", // the bridge's match length (10 or 60 minutes), sent over RCON 2 s after every map start; also set by the mode cfg
    };
    static readonly string[] NeverPrefix = ["css_", "gg_", "mm_", "tv_", "nav_"];

    /// <summary>Also watched, though not game convars: the engine convars Workshop maps were found setting (a scan of the commands in every
    /// cached map) - madebypichet's network rates, aim_notairu's cfg, de_genshin_impact, aim_snow_tundra's
    /// "developer", island's shadow cascades. Nothing else resets them; the cheat-flagged ones (host_timescale, mat_*) are reverted by the
    /// engine when server.cfg turns sv_cheats off at the next load.</summary>
    static readonly string[] Also =
    [
        "sv_voiceenable", "sv_visiblemaxplayers", "sv_maxrate", "sv_minrate", "sv_parallel_packentities", "spec_replay_enable", "developer",
        "csm_max_num_cascades_override", "lb_csm_cascade_size_override",
    ];

    /// <summary>The files the engine executes on every map load (console.log: "execing ..."); it sets what they name again itself.</summary>
    static readonly string[] EngineCfgs = ["server_default.cfg", "server.cfg", "gamemode_armsrace.cfg", "gamemode_armsrace_server.cfg"];

    /// <summary>The server's mode comes from the cs2 service's environment (deploy/compose.example.yml), whatever a map set.</summary>
    static readonly (string ConVar, string Env)[] FromEnvironment = [("game_type", "CS2_GAMETYPE"), ("game_mode", "CS2_GAMEMODE")];

    const float ReportDelay = 15f;   // after a map start: long enough for its OnMapSpawn commands to have run
    const float BlockEvery = 1f;     // the block's look at the listed convars: a map's attempt holds at most this long
    const float AfterRoundStart = 0.25f; // a map's OnNewGame / OnMultiNewRound commands run with the round start: look again right after
    const int BlockLogLines = 3;     // per convar per map; past it, one summary line a minute and one at the map's end

    sealed class Managed(ConVar var, string baseline)
    {
        public ConVar Var { get; } = var;
        public string Baseline { get; set; } = baseline;
    }

    readonly Dictionary<string, Managed> managed = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Managed> extra = new(StringComparer.OrdinalIgnoreCase); // blocked or fun convars outside the managed set (cfg-named, not game convars)
    readonly HashSet<string> cfgNames = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> stubborn = new(StringComparer.OrdinalIgnoreCase); // refused a put-back once: reported, not retried noisily
    int listed, fromBuild, notFound, notGame, leftToCfg, leftAlone, alsoWatched;
    string baselineFrom = "", map = "", lastRestore = "none yet", listFrom = "not read yet";
    bool baselinePending;           // loaded at a server start: the baseline is taken when the first map ends
    bool endSeenSinceStart = true;  // the first start after a load restores nothing
    string mode = "on";             // on | dryrun | off (mapreset_mode)

    // ---- the game-changers block (BlockList.cs) ----
    List<BlockList.Entry> blocked = new();
    string blockedFrom = "not read yet";
    bool blockOn = true;            // mapreset_block on|off, until the plugin reloads
    readonly Dictionary<string, (int Count, DateTime LastLine)> blocksThisMap = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> blockRefused = new(StringComparer.OrdinalIgnoreCase); // a put-back the game refused on this map: logged once
    int blocksTotal;
    string lastBlock = "none yet";

    // ---- the fun page's changes (Fun.cs) ----
    readonly FunState fun = new();
    string lastFun = "none yet";
    readonly Dictionary<string, string> setBy = new(StringComparer.OrdinalIgnoreCase); // setting id -> who set it, while it is off its default (the HUD's Fun page says it)

    /// <summary>Who set a fun setting that is off its default (gg_fun's last argument), or null: at its default, or not known.</summary>
    public string? SetBy(string settingId) => setBy.GetValueOrDefault(settingId);

    string BaselineFile => Path.Combine(plugin.ModuleDirectory, "baseline.json");
    string BlockedFile => Path.Combine(plugin.ModuleDirectory, Modes.BlockFile);

    // ---- the game mode (docs/reference.md) ----
    Modes.Mode? gameMode;                   // os_mode: the mode the server loads in; null until told (then the live convars at a map start)
    Modes.Mode blockMode = Modes.ArmsRace;  // whose block list is on now
    Modes.Mode? loadedIn;                   // the mode the map that is on was loaded in (its game_type/game_mode at the start)

    /// <summary>The mode in force for the features: the one the map that is on was loaded in.</summary>
    public Modes.Mode Mode => blockMode;

    /// <summary>Raised when a mode's features go on (at the map start that loads it, or at once when the map that is on is in it).</summary>
    public event Action<Modes.Mode>? ModeApplied;

    /// <summary>os_mode &lt;id&gt; (the bridge: before every map change, with the convars, and after every map start): game_type and
    /// game_mode keep the mode's values from now on - in the put-back at the old map's end and in the block - and the mode's block list
    /// and Arms Race-only features follow at the next map start, or at once when the map that is on was loaded in that mode.</summary>
    public string SetMode(Modes.Mode m)
    {
        gameMode = m;
        KeepModeValues();
        if (loadedIn?.Id == m.Id && blockMode.Id != m.Id) { UseMode(m, "now: the map that is on is in it"); return $"mode {m.Id}: on now"; }
        return blockMode.Id == m.Id ? $"mode {m.Id}: on already" : $"mode {m.Id}: game_type {m.GameType}, game_mode {m.GameMode} kept; its block list and features at the next map start";
    }

    /// <summary>The mode's game_type and game_mode as the baseline the put-back and the block keep.</summary>
    void KeepModeValues()
    {
        if (gameMode is not { } m) return;
        foreach (var (name, value) in new[] { ("game_type", m.GameType), ("game_mode", m.GameMode) })
            if (managed.TryGetValue(name, out var x) || extra.TryGetValue(name, out x)) x.Baseline = value.ToString();
    }

    void UseMode(Modes.Mode m, string why)
    {
        var was = blockMode;
        blockMode = m;
        ReadBlocked();
        BuildExtra();
        log.LogInformation("Over Strike 2 settings: mode {Mode} ({Why}) - {Blocked}{Was}", m.Name, why, blockedFrom, was.Id != m.Id ? $"; was {was.Name}" : "");
        ModeApplied?.Invoke(m);
    }

    static int IntOf(string? text) => int.TryParse(text, out var n) ? n : text?.Trim().ToLowerInvariant() is "true" ? 1 : 0;

    /// <summary>The block's list as it must act now: the game type and mode always the mode's own values (os_mode), whatever the list in
    /// force says - between os_mode and the next map start the old mode's list is still on.</summary>
    IEnumerable<BlockList.Entry> BlockedNow() => gameMode is not { } m ? blocked
        : blocked.Select(e => e.ConVar.Equals("game_type", StringComparison.OrdinalIgnoreCase) ? e with { Value = m.GameType.ToString() }
            : e.ConVar.Equals("game_mode", StringComparison.OrdinalIgnoreCase) ? e with { Value = m.GameMode.ToString() } : e);

    public void Load(bool hotReload)
    {
        map = Server.MapName ?? "";
        ReadEngineCfgs();
        ReadBlocked();
        BuildManagedSet();
        // At a server start the plugin loads before the first map and before the command line has run (console.log: the plugins load
        // two seconds before "Loading map"): game_type would still read 0 then. Any other load - CounterStrikeSharp's hot reload, or
        // css_plugins load/reload, which passes hotReload false - happens while a map is on.
        var midMap = hotReload || !string.IsNullOrEmpty(map);
        if (midMap) TakeBaselineMidMap();
        else { baselinePending = true; baselineFrom = "pending: taken when the first map ends"; }
        plugin.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        if ((plugin as OverStrike2Plugin)?.Screen is { } hud) hud.FunSetBy = SetBy; // the Fun page says who set each setting off its default
        plugin.RegisterEventHandler<EventRoundStart>((_, _) => { plugin.AddTimer(AfterRoundStart, () => Block("round start"), TimerFlags.STOP_ON_MAPCHANGE); return HookResult.Continue; });
        plugin.AddTimer(BlockEvery, () => Block(null), TimerFlags.REPEAT);
        log.LogInformation("Over Strike 2 settings {Version}: {Managed} settings watched ({Listed} game convars listed and {FromBuild} only this build has: {Cfg} left to the engine cfgs, " +
            "{Alone} never touched, {NotGame} not game convars, {NotFound} unknown to this build; plus {Also} engine convars maps set); {Blocked}; " +
            "{Fun} fun settings ({FunVars} convars and effects; {On} on by default); baseline {From}.",
            ModuleVersion, managed.Count, listed, fromBuild, leftToCfg, leftAlone, notGame, notFound, alsoWatched, blockedFrom, Fun.Settings.Length, Fun.AllConVars.Count(),
            string.Join(", ", Fun.EffectsOn.Select(e => e.Effect)), baselineFrom);
    }

    // ---- what is managed ----------------------------------------------------------------------------------------------------

    void ReadEngineCfgs()
    {
        var dir = Path.Combine(Server.GameDirectory, "csgo", "cfg");
        foreach (var file in EngineCfgs)
        {
            var path = Path.Combine(dir, file);
            try
            {
                if (!File.Exists(path)) { log.LogWarning("Over Strike 2 settings: {Path} not found", path); continue; }
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw;
                    var comment = line.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0) line = line[..comment];
                    foreach (var statement in line.Split(';'))
                    {
                        var parts = statement.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0) cfgNames.Add(parts[0].Trim('"'));
                    }
                }
            }
            catch (Exception e) { log.LogWarning("Over Strike 2 settings: could not read {Path}: {Error}", path, e.Message); }
        }
    }

    /// <summary>Finds every game convar: the embedded list's, then those only this build has (ConVarNames.cs: anything wrong there leaves
    /// the list as it is, and it says so here). Each one's baseline is provisionally its live value.</summary>
    void BuildManagedSet()
    {
        managed.Clear();
        listed = fromBuild = notFound = notGame = leftToCfg = leftAlone = alsoWatched = 0;
        var list = ReadList();
        foreach (var name in list) { listed++; Manage(name); }
        var clock = Stopwatch.StartNew();
        var found = ConVarNames.Ask(() => File.OpenRead(ConVarNames.BinaryPath(Server.GameDirectory, OperatingSystem.IsWindows())), list, AskEngine);
        foreach (var name in found.New) { fromBuild++; Manage(name); }
        listFrom = found.Describe(clock.ElapsedMilliseconds);
        log.Log(found.Refused == null ? LogLevel.Information : LogLevel.Warning, "Over Strike 2 settings: the list: {List}", listFrom);
        foreach (var more in Also)
        {
            if (managed.ContainsKey(more) || cfgNames.Contains(more) || Never.Contains(more)) continue;
            try { if (ConVar.Find(more) is { } cv) { managed[more] = new Managed(cv, cv.StringValue); alsoWatched++; } } catch { }
        }
        BuildExtra();
    }

    /// <summary>The names of convars.txt, embedded in the DLL.</summary>
    List<string> ReadList()
    {
        using var stream = GetType().Assembly.GetManifestResourceStream("convars.txt");
        if (stream == null) { log.LogError("Over Strike 2 settings: convars.txt is not embedded; only what this build's game DLL names is watched"); return []; }
        using var reader = new StreamReader(stream);
        return ConVarNames.ParseList(reader);
    }

    /// <summary>One name into the managed set, if it is a game convar this plugin may watch.</summary>
    void Manage(string name)
    {
        if (Never.Contains(name) || NeverPrefix.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) { leftAlone++; return; }
        if (cfgNames.Contains(name)) { leftToCfg++; return; }
        ConVar? cv;
        try { cv = ConVar.Find(name); } catch { cv = null; }
        if (cv == null) { notFound++; return; }
        try
        {
            if (!cv.Flags.HasFlag(ConVarFlags.FCVAR_GAMEDLL)) { notGame++; return; }
            managed[name] = new Managed(cv, cv.StringValue);
        }
        catch { notFound++; }
    }

    /// <summary>What the engine says about a string of the game DLL. Most are no convar: not found.</summary>
    static ConVarNames.Answer? AskEngine(string name)
    {
        try { return ConVar.Find(name) is { } cv ? new ConVarNames.Answer((ulong)cv.Flags, (short)cv.Type) : null; }
        catch { return null; }
    }

    /// <summary>The blocked and the fun convars that the managed set leaves out (named by an engine cfg, or not game convars): found
    /// here too, so they have a baseline for the block and a way back for the fun page. Put back at a map's end only when a player
    /// changed one (the engine cfgs set the cfg-named ones again at every load). One found before keeps its baseline (a reload of the
    /// list mid-map); a new one takes the live value until the next baseline is taken.</summary>
    void BuildExtra()
    {
        var had = new Dictionary<string, Managed>(extra, StringComparer.OrdinalIgnoreCase);
        extra.Clear();
        foreach (var name in blocked.Select(b => b.ConVar).Concat(Fun.AllConVars))
        {
            if (managed.ContainsKey(name) || extra.ContainsKey(name)) continue;
            if (had.TryGetValue(name, out var kept)) { extra[name] = kept; continue; }
            try { if (ConVar.Find(name) is { } cv) extra[name] = new Managed(cv, cv.StringValue); } catch { }
        }
    }

    ConVar? Var(string name) => managed.TryGetValue(name, out var m) ? m.Var : extra.TryGetValue(name, out var x) ? x.Var : FindVar(name);

    static ConVar? FindVar(string name) { try { return ConVar.Find(name); } catch { return null; } }

    string? Value(string name)
    {
        if (Fun.IsEffect(name)) return effects.TryGetValue(name, out var on) ? on : EffectDefault(name); // the plugin's own switches, kept like convars
        try { return Var(name)?.StringValue; } catch { return null; }
    }

    /// <summary>The fun page's effects that are no game convar (Fun.Vampire, Fun.Ghost, Fun.NoRecoil, Fun.Drops, Fun.CatchUp): "1" or "0", kept
    /// here like a convar; one not in it holds its default.</summary>
    readonly Dictionary<string, string> effects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An effect's default: on for those of Fun.EffectsOn (the grenade drops, catch-up), off for every other.</summary>
    static string EffectDefault(string name) => Fun.EffectsOn.FirstOrDefault(e => e.Effect.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? "0";

    /// <summary>Every effect the fun page can switch.</summary>
    static IEnumerable<string> EffectNames => Fun.AllConVars.Where(Fun.IsEffect);

    /// <summary>An effect changed (a player's change, the undo at a map's end, the reset): its name and whether it is on now. The plugin hands
    /// the grenade drops and catch-up to their modules through it; the Effects module looks for itself.</summary>
    public event Action<string, bool>? EffectChanged;

    /// <summary>The map is ending (from its OnMapEnd to the next map's start): an effect put back now must not act on the players of the map
    /// that goes - no grenade is handed out then.</summary>
    public bool MapEnding { get; private set; }

    /// <summary>What a fun setting's convar or effect holds now: the HUD's fun page reads it, the Effects module asks whether one is on.</summary>
    public string? FunValue(string name) => Value(name);

    public bool EffectOn(string name) => Fun.Same(Value(name), "1");

    string? Baseline(string name) => baselinePending ? null : managed.TryGetValue(name, out var m) ? m.Baseline : extra.TryGetValue(name, out var x) ? x.Baseline : null;

    /// <summary>Sets a convar the way the put-back always has (the engine's own SetString, which also runs its change callbacks) and reads
    /// it back: false when the game kept another value. An effect is the plugin's own: it always takes, and says so when it changed.</summary>
    bool Write(string name, string value)
    {
        if (Fun.IsEffect(name))
        {
            var was = EffectOn(name);
            effects[name] = value;
            var on = EffectOn(name);
            if (on != was) EffectChanged?.Invoke(name, on);
            return true;
        }
        var cv = Var(name);
        if (cv == null) return false;
        try { cv.StringValue = value; return Fun.Same(cv.StringValue, value); } catch { return false; }
    }

    /// <summary>Every effect back at its default (after the players' changes are undone at a map's end and at the reset): whatever left one
    /// elsewhere - a reload of the plugin keeps none - this is where they all are afterwards. Returns what it changed.</summary>
    List<string> EffectsBack()
    {
        var changed = new List<string>();
        foreach (var name in EffectNames)
        {
            var want = EffectDefault(name);
            if (Fun.Same(Value(name), want)) continue;
            Write(name, want);
            changed.Add($"{name} -> {want}");
        }
        return changed;
    }

    // ---- the block list ---------------------------------------------------------------------------------------------------------

    /// <summary>The block list for the mode in force: blocked.txt's values for it (Modes.Blocked - one file, a value per mode).</summary>
    void ReadBlocked()
    {
        if (!File.Exists(BlockedFile))
        {
            blocked = new();
            blockedFrom = $"no block list ({BlockedFile} not found: nothing is blocked)";
            log.LogWarning("Over Strike 2 settings: {File} not found - no game-changer is blocked", BlockedFile);
            return;
        }
        try
        {
            blocked = Modes.Blocked(File.ReadAllText(BlockedFile), blockMode, out var errors);
            foreach (var error in errors) log.LogWarning("Over Strike 2 settings: {File}: {Error}", Modes.BlockFile, error);
            var unknown = blocked.Where(b => FindVar(b.ConVar) == null).Select(b => b.ConVar).ToList();
            if (unknown.Count > 0) log.LogWarning("Over Strike 2 settings: {File}: unknown to this build, not blocked: {List}", Modes.BlockFile, string.Join(", ", unknown));
            blockedFrom = $"{blocked.Count - unknown.Count} game-changers blocked ({Modes.BlockFile}, {blockMode.Name}'s values{(errors.Count + unknown.Count > 0 ? $", {errors.Count} bad line(s), {unknown.Count} unknown" : "")})";
        }
        catch (Exception e)
        {
            blocked = new();
            blockedFrom = $"block list unreadable ({e.Message}): nothing is blocked";
            log.LogWarning("Over Strike 2 settings: {File} unreadable: {Error}", BlockedFile, e.Message);
        }
    }

    /// <summary>Every second, and right after a round start: each listed convar that a map changed goes back to what it must keep, unless a
    /// player chose it on the fun page. The first lines per convar per map are logged one by one; a map that keeps trying is summed up.</summary>
    void Block(string? when)
    {
        if (mode == "off" || !blockOn || baselinePending || blocked.Count == 0) return;
        try
        {
            var now = DateTime.UtcNow;
            var undone = new List<string>();
            foreach (var (entry, was, want) in BlockList.Due(BlockedNow(), Value, Baseline, fun.Chosen))
            {
                if (blockRefused.Contains(entry.ConVar)) continue;
                var seen = blocksThisMap.GetValueOrDefault(entry.ConVar);
                seen.Count++;
                blocksTotal++;
                if (mode == "dryrun")
                {
                    if (seen.Count <= BlockLogLines) log.LogInformation("Over Strike 2 settings: dry run - would block on {Map}: {Name} {Was} -> {Want} ({Why})", map, entry.ConVar, was, want, entry.Why);
                    blocksThisMap[entry.ConVar] = (seen.Count, now);
                    continue;
                }
                var ok = Write(entry.ConVar, want);
                lastBlock = $"{DateTime.Now:HH:mm:ss} {map}: {entry.ConVar} {was} -> {want}{(ok ? "" : $" (refused, reads {Value(entry.ConVar)})")}";
                if (!ok) { blockRefused.Add(entry.ConVar); log.LogWarning("Over Strike 2 settings: could not block {Name} on {Map}: {Was} -> {Want} refused, reads {Now}", entry.ConVar, map, was, want, Value(entry.ConVar)); }
                else if (seen.Count <= BlockLogLines)
                    log.LogInformation("Over Strike 2 settings: blocked on {Map}{When}: {Name} {Was} -> {Want} ({Why})", map, when != null ? $" at the {when}" : "", entry.ConVar, was, want, entry.Why);
                if (ok && seen.Count == 1) undone.Add(BlockList.Label(entry.ConVar));
                else if ((now - seen.LastLine).TotalSeconds >= 60)
                {
                    log.LogInformation("Over Strike 2 settings: blocked on {Map}: {Name} again - {Count} times on this map so far, the last {Was} -> {Want}", map, entry.ConVar, seen.Count, was, want);
                    seen.LastLine = now;
                }
                if (seen.Count <= BlockLogLines) seen.LastLine = now;
                blocksThisMap[entry.ConVar] = seen;
            }
            // Players are told what the map tried to change and that it was undone - once per setting per map.
            if (undone.Count > 0 && (plugin as OverStrike2Plugin)?.Screen is { } hud) hud.Notice(BlockList.Notice(undone.Distinct().ToList()), 10);
        }
        catch (Exception e) { log.LogWarning("Over Strike 2 settings: block failed: {Error}", e.Message); }
    }

    /// <summary>The block's summary for the map that ends: each convar the map tried more than the logged lines show.</summary>
    void BlockSummary(string ended)
    {
        var many = blocksThisMap.Where(kv => kv.Value.Count > BlockLogLines).Select(kv => $"{kv.Key} {kv.Value.Count} times").ToList();
        if (many.Count > 0) log.LogInformation("Over Strike 2 settings: {Map} kept trying: blocked {List}", ended, string.Join(", ", many));
        blocksThisMap.Clear();
        blockRefused.Clear();
    }

    // ---- the baseline ---------------------------------------------------------------------------------------------------------

    sealed record BaselineDoc(int Pid, string ProcessStart, string Taken, string Map, Dictionary<string, string> Values);

    static (int Pid, string Start) ProcessId()
    {
        using var p = Process.GetCurrentProcess();
        return (p.Id, p.StartTime.ToUniversalTime().ToString("o"));
    }

    /// <summary>The live values become the baseline (BuildManagedSet read them), with the mode from the environment. A player's change on
    /// the fun page is never part of it: those convars take their value from before the players' changes.</summary>
    void TakeLiveBaseline(string from)
    {
        foreach (var (name, m) in managed.Concat(extra)) { try { m.Baseline = fun.Before.TryGetValue(name, out var was) ? was : m.Var.StringValue; } catch { } }
        ApplyEnvironment();
        baselineFrom = from;
        SaveBaseline();
    }

    void TakeBaselineMidMap()
    {
        var (pid, start) = ProcessId();
        if (File.Exists(BaselineFile))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<BaselineDoc>(File.ReadAllText(BaselineFile));
                if (doc != null && doc.Pid == pid && doc.ProcessStart == start)
                {
                    var used = 0;
                    foreach (var (n, m) in managed.Concat(extra))
                        if (doc.Values.TryGetValue(n, out var v)) { m.Baseline = v; used++; }
                    ApplyEnvironment();
                    baselineFrom = $"kept from {doc.Taken} on {doc.Map} ({used} values; this server process)";
                    return;
                }
            }
            catch (Exception e) { log.LogWarning("Over Strike 2 settings: {File} unreadable ({Error}); taking the live values", BaselineFile, e.Message); }
        }
        TakeLiveBaseline($"live values on {map} at {DateTime.Now:HH:mm:ss} (loaded mid-map; no saved baseline from this server process)");
    }

    void ApplyEnvironment()
    {
        foreach (var (name, env) in FromEnvironment)
            if (managed.TryGetValue(name, out var m) && int.TryParse(Environment.GetEnvironmentVariable(env), out var value))
                m.Baseline = value.ToString();
        KeepModeValues(); // os_mode's values win over the boot environment's
    }

    void SaveBaseline()
    {
        try
        {
            var (pid, start) = ProcessId();
            var values = managed.Concat(extra).ToDictionary(kv => kv.Key, kv => kv.Value.Baseline, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(BaselineFile, JsonSerializer.Serialize(new BaselineDoc(pid, start, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), map, values)));
        }
        catch (Exception e) { log.LogWarning("Over Strike 2 settings: could not save {File}: {Error}", BaselineFile, e.Message); }
    }

    // ---- map end: put everything back ---------------------------------------------------------------------------------------

    List<(string Name, string Now, string Want)> Differences()
    {
        var list = new List<(string, string, string)>();
        if (baselinePending) return list;
        foreach (var (name, m) in managed)
        {
            try
            {
                var now = m.Var.StringValue;
                if (now != m.Baseline) list.Add((name, now, m.Baseline));
            }
            catch { /* unreadable this time: skipped */ }
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return list;
    }

    void Restore(string when)
    {
        if (mode == "off" || baselinePending) return;
        var diffs = Differences();
        if (diffs.Count == 0) { lastRestore = $"{DateTime.Now:HH:mm:ss} {when}: nothing to put back"; log.LogInformation("Over Strike 2 settings: {When}: nothing to put back", when); return; }
        var done = new List<string>();
        var refused = new List<string>();
        foreach (var (name, now, want) in diffs)
        {
            if (mode == "dryrun") { done.Add($"{name} {now} (would be {want})"); continue; }
            try
            {
                managed[name].Var.StringValue = want;
                var after = managed[name].Var.StringValue;
                if (after == want) done.Add($"{name} {now} -> {want}");
                else if (stubborn.Add(name)) refused.Add($"{name} {now} -> {want} (reads {after})");
            }
            catch (Exception e) { if (stubborn.Add(name)) refused.Add($"{name} ({e.Message})"); }
        }
        var verb = mode == "dryrun" ? "would put back" : "put back";
        lastRestore = $"{DateTime.Now:HH:mm:ss} {when}: {verb} {done.Count}: {string.Join(", ", done)}" + (refused.Count > 0 ? $"; refused {string.Join(", ", refused)}" : "");
        log.LogInformation("Over Strike 2 settings: {When}: {Verb} {Count} setting(s): {List}", when, verb, done.Count, string.Join(", ", done));
        if (refused.Count > 0) log.LogWarning("Over Strike 2 settings: {When}: could not put back: {List}", when, string.Join(", ", refused));
    }

    void OnMapEnd()
    {
        endSeenSinceStart = true;
        MapEnding = true;
        try
        {
            UndoFun($"end of {map}"); // the players' changes first: they last until the map changes, and are never part of the baseline
            BlockSummary(map);
            if (baselinePending)
            {
                // The end of the server process's first map, the boot map: everything the engine sets has been set, and the map ran nothing.
                if (managed.Count < listed / 2) BuildManagedSet(); // too few convars were registered when the plugin loaded
                baselinePending = false;
                TakeLiveBaseline($"end of the first map ({map}) at {DateTime.Now:HH:mm:ss}");
                log.LogInformation("Over Strike 2 settings: baseline taken at the end of {Map}: {Count} game settings watched, {Extra} more for the block and the fun page", map, managed.Count, extra.Count);
                return;
            }
            Restore($"end of {map}");
        }
        catch (Exception e) { log.LogError("Over Strike 2 settings: map end failed: {Error}", e); }
    }

    void OnMapStart(string name)
    {
        MapEnding = false;
        // The mode this map loaded in - its live game_type/game_mode - not the one the bridge told (os_mode): a changelevel from a Workshop
        // map to an official one can bring back the convars CS2 saved before, the mode included (measured 2026-11-01: de_dust2 told
        // Deathmatch came up in Arms Race). The bridge then loads it once more in the told mode; until that load, this map keeps its own
        // mode's block list and features, so Deathmatch's values are never forced onto an Arms Race map. Convars that are none of our
        // modes fall back to the told mode; without an os_mode since the plugin loaded (a server start, a reload) the live mode is the told
        // one too.
        loadedIn = Modes.FromConVars(IntOf(Value("game_type")), IntOf(Value("game_mode")));
        if (gameMode == null && loadedIn != null) gameMode = loadedIn;
        if ((loadedIn ?? gameMode) is { } m && blockMode.Id != m.Id)
            UseMode(m, gameMode != null && gameMode.Id != m.Id ? $"the start of {name}, which loaded in {m.Name} although {gameMode.Name} was told" : $"the start of {name}");
        var missedEnd = !endSeenSinceStart;
        endSeenSinceStart = false;
        var before = map;
        map = name;
        try
        {
            // Only if no map end was seen since the last start: then this is the last moment before this map's own commands run.
            if (missedEnd) { UndoFun($"start of {name}"); BlockSummary(before); }
            if (missedEnd && !baselinePending) Restore($"start of {name} (no map end seen after {before})");
        }
        catch (Exception e) { log.LogError("Over Strike 2 settings: map start failed: {Error}", e); }
        plugin.AddTimer(ReportDelay, () => Report(name), TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>What the map that just started changed (its own settings, kept while it is on): one log line, nothing is touched.</summary>
    void Report(string name)
    {
        try
        {
            var diffs = Differences();
            if (diffs.Count > 0)
                log.LogInformation("Over Strike 2 settings: {Map} runs with {Count} changed setting(s), put back at its end: {List}", name, diffs.Count,
                    string.Join(", ", diffs.Select(d => $"{d.Name} {d.Now} (normally {d.Want})")));
        }
        catch (Exception e) { log.LogWarning("Over Strike 2 settings: report failed: {Error}", e.Message); }
    }

    // ---- the fun page: gg_fun (Fun.cs) ----------------------------------------------------------------------------------------

    /// <summary>Every player change undone (the map's end, the bridge's reset after nobody has been on): each changed convar back to what it
    /// was before the first player change on this map, and every effect back at its default - the grenade drops and catch-up on.</summary>
    string UndoFun(string when)
    {
        setBy.Clear(); // every setting goes back to its default: nobody set any
        var writes = fun.Reset(Value);
        var done = writes.Select(w => $"{w.ConVar} -> {w.Value}{(Write(w.ConVar, w.Value) ? "" : " (refused)")}").ToList();
        done.AddRange(EffectsBack().Select(b => b + " (its default)")); // after the undo: every effect where it starts, whatever left it elsewhere
        if (done.Count == 0) return "no player changes to undo";
        lastFun = $"{DateTime.Now:HH:mm:ss} {when}: undone {string.Join(", ", done)}";
        log.LogInformation("Over Strike 2 settings: fun settings undone at {When}: {List}", when, string.Join(", ", done));
        return $"undone {string.Join(", ", done)}";
    }

    /// <summary>gg_fun &lt;setting&gt; &lt;choice&gt; ["who"]: a player's change from the menu's fun page or chat (the HUD runs it, naming who
    /// asked; once the value has landed the centre of the screen says who switched what, and chat says it as the command). gg_fun reset
    /// ["who"]: every player change undone and every effect at its default - the bridge's reset after 10 minutes with nobody on, and the
    /// page's Default. Bare gg_fun: what each setting shows now. Server only: no chat trigger reaches it.</summary>
    [ConsoleCommand(Fun.Command, "Fun settings: gg_fun <setting> <choice> [who] | gg_fun reset [who] | gg_fun")]
    [CommandHelper(minArgs: 0, usage: "[<setting> <choice> | reset] [who]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnFun(CCSPlayerController? player, CommandInfo command)
    {
        var verb = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "";
        if (verb == "")
        {
            command.ReplyToCommand("fun: " + string.Join(", ", Fun.Settings.Select(s => $"{s.Id} {Fun.Showing(s, Value).Id}")) + $" ({fun.Describe(Value)}); last: {lastFun}");
            return;
        }
        if (verb == "reset")
        {
            var who = command.ArgCount > 2 ? command.GetArg(2) : "Console";
            var said = UndoFun($"reset by {who}");
            command.ReplyToCommand($"fun: reset - {said}");
            return;
        }
        if (Fun.Find(verb) is not { } setting || command.ArgCount < 3 || Fun.Find(setting, command.GetArg(2)) is not { } choice)
        {
            command.ReplyToCommand("fun: gg_fun <setting> <choice> [who] - " + string.Join("; ", Fun.Settings.Select(s => $"{s.Id} {string.Join("|", s.Choices.Select(c => c.Id))}")));
            return;
        }
        var by = command.ArgCount > 3 ? command.GetArg(3) : "Console";
        if (mode == "off") { command.ReplyToCommand("fun: refused - mapreset_mode is off"); return; }
        if (Fun.Refusal(setting, choice, Value) is { } why) // a Clash: the page and chat say it before they get here
        {
            log.LogInformation("Over Strike 2 settings: fun on {Map}: {Setting} {Choice} by {By} refused - {Why}", map, setting.Id, choice.Id, by, why);
            command.ReplyToCommand($"fun: refused - {why}");
            return;
        }
        var writes = fun.Apply(setting, choice, Value);
        var refused = writes.Where(w => !Write(w.ConVar, w.Value)).Select(w => w.ConVar).ToList();
        if (choice == setting.Normal) setBy.Remove(setting.Id); else if (refused.Count == 0) setBy[setting.Id] = by; // who set it, until it is back at its default
        var line = $"{setting.Id} {choice.Id} by {by}: {(writes.Count == 0 ? "nothing to change" : string.Join(", ", writes.Select(w => $"{w.ConVar} {w.Value}")))}{(refused.Count > 0 ? $"; refused {string.Join(", ", refused)}" : "")}";
        lastFun = $"{DateTime.Now:HH:mm:ss} {line}";
        log.LogInformation("Over Strike 2 settings: fun on {Map}: {Line}", map, line);
        command.ReplyToCommand($"fun: {line}");
    }

    // ---- console (server only: RCON and the server console) ------------------------------------------------------------------

    [ConsoleCommand("mapreset_check", "Over Strike 2 settings: the watched settings that differ from the baseline now, the last put-back, the block and the fun page")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnCheck(CCSPlayerController? player, CommandInfo command)
    {
        var diffs = Differences();
        command.ReplyToCommand($"mapreset: {ModuleVersion}, mode {mode}, {managed.Count} watched (+{extra.Count} for the block and the fun page), baseline {baselineFrom}");
        command.ReplyToCommand($"mapreset: the list: {listFrom}");
        command.ReplyToCommand($"mapreset: last: {lastRestore}");
        command.ReplyToCommand(diffs.Count == 0 ? "mapreset: nothing differs from the baseline"
            : $"mapreset: {diffs.Count} differ: " + string.Join(", ", diffs.Select(d => $"{d.Name} {d.Now} (baseline {d.Want})")));
        command.ReplyToCommand($"mapreset: block {(blockOn ? "on" : "OFF (mapreset_block)")}, {blockedFrom}; {blocksTotal} block(s) since load, the last {lastBlock}");
        command.ReplyToCommand($"mapreset: fun {fun.Describe(Value)}; last {lastFun}");
        if (stubborn.Count > 0) command.ReplyToCommand("mapreset: refused a put-back: " + string.Join(", ", stubborn));
    }

    [ConsoleCommand("mapreset_now", "Over Strike 2 settings: put the watched settings back right now")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnNow(CCSPlayerController? player, CommandInfo command)
    {
        Restore("by command");
        command.ReplyToCommand($"mapreset: {lastRestore}");
    }

    [ConsoleCommand("mapreset_mode", "Over Strike 2 settings: on (default) | dryrun (log only) | off, until the plugin reloads")]
    [CommandHelper(minArgs: 0, usage: "[on|dryrun|off]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnMode(CCSPlayerController? player, CommandInfo command)
    {
        var arg = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "";
        if (arg is "on" or "dryrun" or "off") { mode = arg; log.LogInformation("Over Strike 2 settings: mode {Mode}", mode); }
        command.ReplyToCommand($"mapreset: mode {mode}");
    }

    [ConsoleCommand("mapreset_block", "Over Strike 2 settings: the game-changers block - on (default) | off, until the plugin reloads; reload reads blocked.txt again; bare: the list")]
    [CommandHelper(minArgs: 0, usage: "[on|off|reload]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnBlock(CCSPlayerController? player, CommandInfo command)
    {
        var arg = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "";
        if (arg is "on" or "off") { blockOn = arg == "on"; log.LogInformation("Over Strike 2 settings: block {State}", arg); }
        if (arg == "reload") { ReadBlocked(); BuildExtra(); log.LogInformation("Over Strike 2 settings: {Blocked}", blockedFrom); }
        command.ReplyToCommand($"mapreset: block {(blockOn ? "on" : "off")}, {blockedFrom}: {string.Join(", ", blocked.Select(b => $"{b.ConVar} {b.Value ?? Baseline(b.ConVar) ?? "?"}"))}");
    }

    [ConsoleCommand("mapreset_rebaseline", "Over Strike 2 settings: take the current values as the wanted ones (after an intended change)")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnRebaseline(CCSPlayerController? player, CommandInfo command)
    {
        stubborn.Clear();
        baselinePending = false;
        TakeLiveBaseline($"live values on {map} at {DateTime.Now:HH:mm:ss} (mapreset_rebaseline)");
        command.ReplyToCommand($"mapreset: baseline {baselineFrom}");
    }
}
