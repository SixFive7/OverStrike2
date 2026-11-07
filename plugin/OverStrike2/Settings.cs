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
/// game-changers no map may set at all (blocked.txt beside the DLL, a value per game mode: ../../bridge/Shared/Modes.cs, ../../bridge/Shared/BlockList.cs);
/// and it makes the players' changes from the menu's fun page (gg_fun, ../../bridge/Shared/Fun.cs), which last until the map changes and never
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
sealed partial class Settings(BasePlugin plugin, ILogger log)
{
    /// <summary>The settings module of the Over Strike 2 plugin, with the host's version.</summary>
    const string ModuleVersion = OverStrike2Plugin.Version;

    const float ReportDelay = 15f;   // after a map start: long enough for its OnMapSpawn commands to have run

    public void Load(bool hotReload)
    {
        map = Server.MapName ?? "";
        // At a server start the plugin loads before the first map and before the command line has run (console.log: the plugins load
        // two seconds before "Loading map"): game_type would still read 0 then. Any other load - CounterStrikeSharp's hot reload, or
        // css_plugins load/reload, which passes hotReload false - happens while a map is on. That map's start has passed, so the mode is
        // decided here as its start decided it, before anything below reads the block list: a load on a Deathmatch or Wingman map would
        // otherwise keep Arms Race's list and features on it until the next map start.
        var midMap = hotReload || !string.IsNullOrEmpty(map);
        if (midMap) modeAtLoad = MapMode();
        ReadEngineCfgs();
        ReadBlocked();
        BuildManagedSet();
        if (midMap) TakeBaselineMidMap();
        else { baselinePending = true; baselineFrom = "pending: taken when the first map ends"; }
        plugin.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        if ((plugin as OverStrike2Plugin)?.Screen is { } hud) (hud.FunSetBy, hud.FunSet, hud.FunReset) = (SetBy, ChangeFun, ResetFun); // the Fun page says who set what, and makes its changes here
        plugin.RegisterEventHandler<EventRoundStart>((_, _) => { plugin.AddTimer(AfterRoundStart, () => Block("round start"), TimerFlags.STOP_ON_MAPCHANGE); return HookResult.Continue; });
        plugin.AddTimer(BlockEvery, () => Block(null), TimerFlags.REPEAT);
        log.LogInformation("Over Strike 2 settings {Version}: {Managed} settings watched ({Listed} game convars listed and {FromBuild} only this build has: {Cfg} left to the engine cfgs, " +
            "{Alone} never touched, {NotGame} not game convars, {NotFound} unknown to this build; plus {Also} engine convars maps set); {Blocked}; " +
            "{Fun} fun settings ({FunVars} convars and effects; {On} on by default); baseline {From}.",
            ModuleVersion, managed.Count, listed, fromBuild, leftToCfg, leftAlone, notGame, notFound, alsoWatched, blockedFrom, Fun.Settings.Length, Fun.AllConVars.Count(),
            string.Join(", ", Fun.EffectsOn.Select(e => e.Effect)), baselineFrom);
        if (modeAtLoad is { } m) log.LogInformation("Over Strike 2 settings: mode {Mode} (loaded while {Map} is on, which was loaded in it) - {Blocked}", m.Name, map, blockedFrom);
    }

    /// <summary>The host has wired its modules to ModeApplied: a load while a map in another mode than Arms Race is on tells them that mode
    /// now - the grenade drops, catch-up and the HUD follow it as they did from that map's start - not at the next map start.</summary>
    public void Ready()
    {
        if (modeAtLoad is not { } m) return;
        modeAtLoad = null;
        ModeApplied?.Invoke(m);
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
        // The mode this map loaded in - its live game_type/game_mode - not the one the bridge told (os_mode): ModePlan.cs says why. Without
        // an os_mode since the plugin loaded (a server start) the live mode is the told one too.
        var was = modes.On;
        if (MapMode() is { } m)
            UseMode(m, was, modes.Told is { } told && told.Id != m.Id ? $"the start of {name}, which loaded in {m.Name} although {told.Name} was told" : $"the start of {name}");
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
}
