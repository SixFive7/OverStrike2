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

/// <summary>The settings module's game convars: what it watches, their baseline, and the put-back at a map's end.</summary>
sealed partial class Settings
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

    /// <summary>The server's mode comes from the cs2 service's environment (deploy/compose.example.yml), whatever a map set.</summary>
    static readonly (string ConVar, string Env)[] FromEnvironment = [("game_type", "CS2_GAMETYPE"), ("game_mode", "CS2_GAMEMODE")];

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

    string BaselineFile => Path.Combine(plugin.ModuleDirectory, "baseline.json");

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
}
