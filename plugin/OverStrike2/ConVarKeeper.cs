using System.Diagnostics;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Cvars;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The game convars the settings module watches (GameSettings.cs), what each must be - the baseline - and the put-back at a map's
/// end.
///
/// What it watches: every game convar (flagged FCVAR_GAMEDLL - "sv" in cvarlist), those of convars.txt and those only the running build has
/// (ConVarNames.cs), that the engine cfgs do not name and that is not on <see cref="Never"/>. Their baseline is taken when the first map of the
/// server process ends: the boot map (CS2_STARTMAP, ar_shoots, which runs no commands of its own), once the command line, the cfgs and the
/// engine's own load-time settings have all run - think_limit 0, for one, is set by the engine during every load, after OnMapStart. When the
/// plugin is loaded while a map is on, the baseline saved earlier by this same server process is used (else the live values). game_type and
/// game_mode always want the mode's own values (KeepMode). The block's and the fun page's convars this leaves out get a baseline the same
/// way, cfg-named or not (Extra). Nothing outside them is ever touched (hostname, passwords, CSTV, MultiAddonManager).</summary>
sealed class ConVarKeeper(string moduleDirectory, ILogger log)
{
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

    sealed class Managed(ConVar var, string baseline)
    {
        public ConVar Var { get; } = var;
        public string Baseline { get; set; } = baseline;
    }

    readonly Dictionary<string, Managed> managed = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Managed> extra = new(StringComparer.OrdinalIgnoreCase); // the block's and the fun page's convars outside the managed set (cfg-named, not game convars)
    readonly HashSet<string> cfgNames = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> stubborn = new(StringComparer.OrdinalIgnoreCase); // refused a put-back once: reported, not retried noisily
    List<string> extraNames = [];
    int listed, fromBuild, notFound, notGame, leftToCfg, leftAlone, alsoWatched;
    string listFrom = "not read yet";

    /// <summary>Loaded at a server start: the baseline is taken when the first map ends; until then nothing is put back or blocked.</summary>
    public bool Pending { get; private set; }

    /// <summary>Where the baseline came from, and the last put-back: for the log and the console.</summary>
    public string BaselineFrom { get; private set; } = "";
    public string LastRestore { get; private set; } = "none yet";

    /// <summary>How the list was built (ConVarNames.Describe), and the put-backs the game refused.</summary>
    public string ListFrom => listFrom;
    public IReadOnlyCollection<string> Stubborn => stubborn;

    /// <summary>The watched set in numbers, for the load's log line.</summary>
    public string Counts =>
        $"{managed.Count} settings watched ({listed} game convars listed and {fromBuild} only this build has: {leftToCfg} left to the engine cfgs, " +
        $"{leftAlone} never touched, {notGame} not game convars, {notFound} unknown to this build; plus {alsoWatched} engine convars maps set)";

    public string Describe => $"{managed.Count} watched (+{extra.Count} for the block and the fun page), baseline {BaselineFrom}";

    string BaselineFile => Path.Combine(moduleDirectory, "baseline.json");

    /// <summary>The plugin's load: the engine cfgs' names, the watched set and the extras (the block's and the fun page's convars). Loaded while a
    /// map is on, the baseline saved earlier by this server process, else the live values; at a server start it waits for the first map's end.</summary>
    public void Load(bool midMap, string map, IEnumerable<string> extras, IReadOnlyDictionary<string, string> playersBefore, Modes.Mode? mode)
    {
        extraNames = extras.ToList();
        ReadEngineCfgs();
        BuildManagedSet();
        if (midMap) TakeMidMap(map, playersBefore, mode);
        else { Pending = true; BaselineFrom = "pending: taken when the first map ends"; }
    }

    // ---- what is watched ----------------------------------------------------------------------------------------------------

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
        Extra(extraNames);
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

    /// <summary>The block's and the fun page's convars that the managed set leaves out (named by an engine cfg, or not game convars): found
    /// here too, so they have a baseline for the block and a way back for the fun page. Put back at a map's end only when a player
    /// changed one (the engine cfgs set the cfg-named ones again at every load). One found before keeps its baseline (a reload of the
    /// list mid-map); a new one takes the live value until the next baseline is taken.</summary>
    public void Extra(IEnumerable<string> names)
    {
        extraNames = names.ToList();
        var had = new Dictionary<string, Managed>(extra, StringComparer.OrdinalIgnoreCase);
        extra.Clear();
        foreach (var name in extraNames)
        {
            if (managed.ContainsKey(name) || extra.ContainsKey(name)) continue;
            if (had.TryGetValue(name, out var kept)) { extra[name] = kept; continue; }
            try { if (ConVar.Find(name) is { } cv) extra[name] = new Managed(cv, cv.StringValue); } catch { }
        }
    }

    ConVar? Var(string name) => managed.TryGetValue(name, out var m) ? m.Var : extra.TryGetValue(name, out var x) ? x.Var : Find(name);

    /// <summary>A convar by name, or null: this build has none of that name.</summary>
    public static ConVar? Find(string name) { try { return ConVar.Find(name); } catch { return null; } }

    /// <summary>A convar's live value, or null.</summary>
    public string? Value(string name)
    {
        try { return Var(name)?.StringValue; } catch { return null; }
    }

    /// <summary>Sets a convar the way the put-back always has (the engine's own SetString, which also runs its change callbacks) and reads
    /// it back: false when the game kept another value.</summary>
    public bool Write(string name, string value)
    {
        var cv = Var(name);
        if (cv == null) return false;
        try { cv.StringValue = value; return Fun.Same(cv.StringValue, value); } catch { return false; }
    }

    /// <summary>What a watched convar must be, or null: not watched, or no baseline yet.</summary>
    public string? Baseline(string name) => Pending ? null : managed.TryGetValue(name, out var m) ? m.Baseline : extra.TryGetValue(name, out var x) ? x.Baseline : null;

    // ---- the baseline ---------------------------------------------------------------------------------------------------------

    sealed record BaselineDoc(int Pid, string ProcessStart, string Taken, string Map, Dictionary<string, string> Values);

    static (int Pid, string Start) ProcessId()
    {
        using var p = Process.GetCurrentProcess();
        return (p.Id, p.StartTime.ToUniversalTime().ToString("o"));
    }

    /// <summary>The mode's game_type and game_mode as the baseline the put-back and the block keep (os_mode's, else the mode the first map
    /// loaded in), whatever the live values say.</summary>
    public void KeepMode(Modes.Mode? m)
    {
        if (m == null) return;
        foreach (var (name, value) in new[] { ("game_type", m.GameType), ("game_mode", m.GameMode) })
            if (managed.TryGetValue(name, out var x) || extra.TryGetValue(name, out x)) x.Baseline = value.ToString();
    }

    /// <summary>The live values become the baseline (BuildManagedSet read them), with the mode's. A player's change on the fun page is never
    /// part of it: those convars take their value from before the players' changes.</summary>
    void TakeLive(string from, string map, IReadOnlyDictionary<string, string> playersBefore, Modes.Mode? mode)
    {
        foreach (var (name, m) in managed.Concat(extra)) { try { m.Baseline = playersBefore.TryGetValue(name, out var was) ? was : m.Var.StringValue; } catch { } }
        KeepMode(mode);
        BaselineFrom = from;
        Save(map);
    }

    void TakeMidMap(string map, IReadOnlyDictionary<string, string> playersBefore, Modes.Mode? mode)
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
                    KeepMode(mode);
                    BaselineFrom = $"kept from {doc.Taken} on {doc.Map} ({used} values; this server process)";
                    return;
                }
            }
            catch (Exception e) { log.LogWarning("Over Strike 2 settings: {File} unreadable ({Error}); taking the live values", BaselineFile, e.Message); }
        }
        TakeLive($"live values on {map} at {DateTime.Now:HH:mm:ss} (loaded mid-map; no saved baseline from this server process)", map, playersBefore, mode);
    }

    void Save(string map)
    {
        try
        {
            var (pid, start) = ProcessId();
            var values = managed.Concat(extra).ToDictionary(kv => kv.Key, kv => kv.Value.Baseline, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(BaselineFile, JsonSerializer.Serialize(new BaselineDoc(pid, start, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), map, values)));
        }
        catch (Exception e) { log.LogWarning("Over Strike 2 settings: could not save {File}: {Error}", BaselineFile, e.Message); }
    }

    /// <summary>The end of the server process's first map, the boot map: everything the engine sets has been set, and the map ran nothing.</summary>
    public void FirstMapEnded(string map, IReadOnlyDictionary<string, string> playersBefore, Modes.Mode? mode)
    {
        if (managed.Count < listed / 2) BuildManagedSet(); // too few convars were registered when the plugin loaded
        Pending = false;
        TakeLive($"end of the first map ({map}) at {DateTime.Now:HH:mm:ss}", map, playersBefore, mode);
        log.LogInformation("Over Strike 2 settings: baseline taken at the end of {Map}: {Count} game settings watched, {Extra} more for the block and the fun page", map, managed.Count, extra.Count);
    }

    /// <summary>The current values become the wanted ones (os_settings rebaseline, after an intended change).</summary>
    public void Rebaseline(string map, IReadOnlyDictionary<string, string> playersBefore, Modes.Mode? mode)
    {
        stubborn.Clear();
        Pending = false;
        TakeLive($"live values on {map} at {DateTime.Now:HH:mm:ss} (os_settings rebaseline)", map, playersBefore, mode);
    }

    // ---- a map's end: put everything back -------------------------------------------------------------------------------------

    public List<(string Name, string Now, string Want)> Differences()
    {
        var list = new List<(string, string, string)>();
        if (Pending) return list;
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

    /// <summary>Every watched game convar that differs from its baseline put back (a map's end, os_settings now).</summary>
    public void Restore(string when)
    {
        if (Pending) return;
        var diffs = Differences();
        if (diffs.Count == 0) { LastRestore = $"{DateTime.Now:HH:mm:ss} {when}: nothing to put back"; log.LogInformation("Over Strike 2 settings: {When}: nothing to put back", when); return; }
        var done = new List<string>();
        var refused = new List<string>();
        foreach (var (name, now, want) in diffs)
        {
            try
            {
                managed[name].Var.StringValue = want;
                var after = managed[name].Var.StringValue;
                if (after == want) done.Add($"{name} {now} -> {want}");
                else if (stubborn.Add(name)) refused.Add($"{name} {now} -> {want} (reads {after})");
            }
            catch (Exception e) { if (stubborn.Add(name)) refused.Add($"{name} ({e.Message})"); }
        }
        LastRestore = $"{DateTime.Now:HH:mm:ss} {when}: put back {done.Count}: {string.Join(", ", done)}" + (refused.Count > 0 ? $"; refused {string.Join(", ", refused)}" : "");
        log.LogInformation("Over Strike 2 settings: {When}: put back {Count} setting(s): {List}", when, done.Count, string.Join(", ", done));
        if (refused.Count > 0) log.LogWarning("Over Strike 2 settings: {When}: could not put back: {List}", when, string.Join(", ", refused));
    }

    /// <summary>What the map that just started changed (its own settings, kept while it is on): one log line, nothing is touched.</summary>
    public void Report(string map)
    {
        try
        {
            var diffs = Differences();
            if (diffs.Count > 0)
                log.LogInformation("Over Strike 2 settings: {Map} runs with {Count} changed setting(s), put back at its end: {List}", map, diffs.Count,
                    string.Join(", ", diffs.Select(d => $"{d.Name} {d.Now} (normally {d.Want})")));
        }
        catch (Exception e) { log.LogWarning("Over Strike 2 settings: report failed: {Error}", e.Message); }
    }
}
