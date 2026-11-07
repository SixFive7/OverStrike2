using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The steward of the server's game settings: it puts back, when a map ends, every game setting the map changed (ConVarKeeper.cs);
/// it blocks the game-changers no map may set at all (GameChangerBlock.cs); and it makes the players' changes from the menu's fun page
/// (FunSwitches.cs, gg_fun), which last until the map changes and never become the baseline nor get blocked. It acts in the game mode the map
/// that is on was loaded in (ModePlan.cs), and its console commands are in GameSettingsConsole.cs.
///
/// Why it is needed (measured live on 2026-10-22, CS2 1.41.9.1/14191): <c>host_workshop_map</c> - how the bridge loads every Workshop map -
/// resets no game convar. fs_highsky's point_servercommand <c>sv_airaccelerate 20</c> survived into aim_snow_tundra, and sv_friction,
/// sv_jump_impulse and phys_timescale set over RCON survived a host_workshop_map too. The engine does save the convars when the server goes
/// from an official map to a Workshop map and restores them when a <c>changelevel</c> goes back to an official one, but between two Workshop
/// maps nothing is restored. On every load the engine runs server.cfg and the Arms Race cfgs again, so whatever those files name is set
/// again by the engine and is left to it here.
///
/// A map keeps its own settings for as long as it is on - except the blocked ones - and they are undone at its end (OnMapEnd), before the
/// next map's cfgs and entities run: the players' changes first, then the put-back. The players' changes end with the plugin too: an
/// unload undoes them, and the next instance says so (Unload, Ready).</summary>
sealed partial class GameSettings
{
    const float ReportDelay = 15f;       // after a map start: long enough for its OnMapSpawn commands to have run
    const float BlockEvery = 1f;         // the block's look at the listed convars: a map's attempt holds at most this long
    const float AfterRoundStart = 0.25f; // a map's OnNewGame / OnMultiNewRound commands run with the round start: look again right after

    readonly BasePlugin plugin;
    readonly ILogger log;
    readonly Action<Messages.Kind, string> post;
    readonly ConVarKeeper keeper;
    readonly GameChangerBlock block;
    readonly FunSwitches fun;
    ModePlan modes = new();          // os_mode's mode, the one the map that is on was loaded in, and whose block list is on (ModePlan.cs);
                                     // a load while a map is on takes the one the instance before handed over (ModeHandover)
    Modes.Mode? modeAtLoad;          // a load while a map is on found that map in another mode than Arms Race: the modules hear of it in Ready
    List<string>? undoneByReload;    // a load right after an unload on this map: what that unload put back, said in Ready (FunHandover)
    string map = "";
    bool endSeenSinceStart = true;   // the first start after a load restores nothing

    /// <summary>post: how a message reaches the players (the HUD's Post) - the block's notice of a map's game-changer undone, and a reload's
    /// fun changes put back.</summary>
    public GameSettings(BasePlugin plugin, ILogger log, Action<Messages.Kind, string> post)
    {
        this.plugin = plugin;
        this.log = log;
        this.post = post;
        keeper = new ConVarKeeper(plugin.ModuleDirectory, log);
        fun = new FunSwitches(keeper.Value, keeper.Write);
        fun.EffectChanged += (effect, on) => EffectChanged?.Invoke(effect, on);
        block = new GameChangerBlock(plugin.ModuleDirectory, log, keeper, fun, line => post(Messages.GameChanger, line));
    }

    /// <summary>The mode in force for the features: the one the map that is on was loaded in.</summary>
    public Modes.Mode Mode => modes.On;

    /// <summary>Raised when a mode's features go on (at the map start that loads it, or at once when the map that is on is in it, or - for a
    /// load while a map is on - when the host is ready for it).</summary>
    public event Action<Modes.Mode>? ModeApplied;

    /// <summary>A fun effect changed (FunSwitches.EffectChanged): the plugin hands the grenade drops, catch-up, Vampire and Ghost to their modules
    /// through it.</summary>
    public event Action<string, bool>? EffectChanged;

    /// <summary>The map or the plugin is ending (from a map's OnMapEnd to the next map's start, and in Unload): an effect put back now hands
    /// the players nothing - no grenade is handed out then.</summary>
    public bool Ending { get; private set; }

    /// <summary>What a fun setting's convar or effect holds now: the HUD's fun page reads it, the modules ask whether an effect is on.</summary>
    public string? FunValue(string name) => fun.Value(name);

    public bool EffectOn(string name) => fun.EffectOn(name);

    /// <summary>Who set a fun setting that is off its default, or null: the HUD's fun page says it.</summary>
    public string? SetBy(string settingId) => fun.SetBy(settingId);

    /// <summary>A change of a fun setting, made now: what gg_fun &lt;setting&gt; &lt;choice&gt; does, and what the HUD does in process for a player's
    /// click on the Fun page or their chat command (HudScreen.FunSet), a frame after it. Its convars or its effect written, who set it kept while
    /// it is off its default; a Clash refused (the page and chat say it before they get here). Returns what was done, in gg_fun's words.</summary>
    public string ChangeFun(Fun.Setting setting, Fun.Choice choice, string by)
    {
        if (Fun.Refusal(setting, choice, fun.Value) is { } why)
        {
            log.LogInformation("Over Strike 2 settings: fun on {Map}: {Setting} {Choice} by {By} refused - {Why}", map, setting.Id, choice.Id, by, why);
            return $"refused - {why}";
        }
        var line = fun.Apply(setting, choice, by, DateTime.Now);
        log.LogInformation("Over Strike 2 settings: fun on {Map}: {Line}", map, line);
        return line;
    }

    /// <summary>The Fun page's Default, made now: every player change undone and every effect at its default, as gg_fun reset does
    /// (HudScreen.FunReset).</summary>
    public string ResetFun(string by) => UndoFun($"reset by {by}");

    public void Load(bool hotReload)
    {
        map = Server.MapName ?? "";
        // At a server start the plugin loads before the first map and before the command line has run (console.log: the plugins load
        // two seconds before "Loading map"): game_type would still read 0 then. Any other load - CounterStrikeSharp's hot reload, or
        // css_plugins load/reload, which passes hotReload false - happens while a map is on. That map's start has passed, so the mode is
        // decided here as its start decided it, before the block list is read: a load on a Deathmatch or Wingman map would otherwise keep
        // Arms Race's list and features on it until the next map start.
        var midMap = hotReload || !string.IsNullOrEmpty(map);
        var handedOver = midMap && TakeMode();
        if (midMap && !handedOver) modeAtLoad = MapMode(map);
        block.Read(modes.On);
        keeper.Load(midMap, map, Extras, fun.Before, modes.Told);
        if (midMap) undoneByReload = FunHandover.Take(map, DateTime.UtcNow);
        plugin.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        plugin.RegisterEventHandler<EventRoundStart>((_, _) => { plugin.AddTimer(AfterRoundStart, () => block.Hold(map, "round start", modes.Told), TimerFlags.STOP_ON_MAPCHANGE); return HookResult.Continue; });
        plugin.AddTimer(BlockEvery, () => block.Hold(map, null, modes.Told), TimerFlags.REPEAT);
        log.LogInformation("Over Strike 2 settings {Version}: {Counts}; {Blocked}; {Fun} fun settings ({FunVars} convars and effects; {On} on by default); baseline {From}.",
            OverStrike2Plugin.Version, keeper.Counts, block.From, Fun.Settings.Length, Fun.AllConVars.Count(), string.Join(", ", Fun.EffectsOn.Select(e => e.Effect)), keeper.BaselineFrom);
        if (handedOver) log.LogInformation("Over Strike 2 settings: mode {Mode} (loaded while {Map} is on, as the instance before had it: {Plan}) - {Blocked}", modes.On.Name, map, modes.Describe(), block.From);
        else if (modeAtLoad is { } m) log.LogInformation("Over Strike 2 settings: mode {Mode} (loaded while {Map} is on, which was loaded in it) - {Blocked}", m.Name, map, block.From);
        if (handedOver && modes.Again != null) ArmAgain(map, $"{map} is still in {modes.LoadedIn?.Name ?? "none of our modes"}, CS2's doing, not in {modes.Told?.Name} as told");
    }

    /// <summary>A load while a map is on: the plan of the instance before, when it unloaded on this very map (ModeHandover) - the live convars
    /// would name the told mode on a map CS2 brought another mode back under, since the block holds them at the told mode's values. True when
    /// it took one; the modules hear of its mode in Ready.</summary>
    bool TakeMode()
    {
        if (ModeHandover.Take(map, Server.CurrentTime, DateTime.UtcNow) is not { } plan) return false;
        modes = plan;
        modeAtLoad = plan.On.ArmsRace ? null : plan.On;
        return true;
    }

    /// <summary>The host has wired its modules to ModeApplied and its HUD to the messages: a load while a map in another mode than Arms Race is
    /// on tells the modules that mode now - the grenade drops, catch-up and the HUD follow it as they did from that map's start - and a load
    /// right after an unload that put the players' fun changes back tells everybody so, rather than leave them to guess.</summary>
    public void Ready()
    {
        if (modeAtLoad is { } m)
        {
            modeAtLoad = null;
            ModeApplied?.Invoke(m);
        }
        if (undoneByReload is { } undone)
        {
            undoneByReload = null;
            post(Messages.FunDefault, Fun.DefaultLine(FunHandover.Who));
            log.LogInformation("Over Strike 2 settings: loaded on {Map} right after an unload that put back {List} - said to everybody", map, string.Join(", ", undone));
        }
    }

    /// <summary>The plugin unloads (a reload, css_plugins unload, a new build): every player's fun change ends with the instance that made it.
    /// Undone now, by the one that knows the value each convar had before: the next instance knows none of them, so it would keep the
    /// unblocked ones without a word on the page and take the blocked ones for the map's own. The effects go back to their defaults with them
    /// (Vampire's health cut at once). The next instance in this server process is told, to say so (FunHandover). It gets the mode plan too
    /// (ModeHandover): which mode was told, and which one the map that is on was loaded in.</summary>
    public void Unload()
    {
        Ending = true;
        try { ModeHandover.Put(modes, Server.CurrentTime, DateTime.UtcNow); }
        catch (Exception e) { log.LogError("Over Strike 2 settings: handing the mode on failed: {Error}", e); }
        try
        {
            var undone = fun.Undo("the plugin's unload", DateTime.Now);
            if (undone.Count == 0) return;
            log.LogInformation("Over Strike 2 settings: fun settings undone at the plugin's unload: {List}", string.Join(", ", undone));
            FunHandover.Put(map, DateTime.UtcNow, undone);
        }
        catch (Exception e) { log.LogError("Over Strike 2 settings: unload failed: {Error}", e); }
    }

    /// <summary>The convars the keeper watches besides the game convars: the block list's and the fun page's.</summary>
    IEnumerable<string> Extras => block.ConVars.Concat(Fun.AllConVars).ToList();

    // ---- the game mode (docs/reference.md) ----------------------------------------------------------------------------------------

    /// <summary>os_mode &lt;id&gt; (the bridge: before every map change, with the convars, and after every map start): game_type and
    /// game_mode keep the mode's values from now on - in the put-back at the old map's end and in the block - and the mode's block list
    /// and Arms Race-only features follow at the next map start, or at once when the map that is on was loaded in that mode.</summary>
    public string SetMode(Modes.Mode m)
    {
        var was = modes.On;
        var now = modes.Tell(m);
        keeper.KeepMode(modes.Told);
        if (now != null) { UseMode(now, was, "now: the map that is on is in it"); return $"mode {m.Id}: on now"; }
        if (modes.On.Id == m.Id) return $"mode {m.Id}: on already";
        // The plugin's own second load is out in this mode: the bridge, which loads a map once more on the words below, has nothing to do.
        if (modes.LoadingIn?.Id == m.Id) return $"mode {m.Id}: game_type {m.GameType}, game_mode {m.GameMode} kept; {modes.Map} is loading once more, in it";
        return $"mode {m.Id}: game_type {m.GameType}, game_mode {m.GameMode} kept; its block list and features at the next map start";
    }

    /// <summary>Bare os_mode (the bridge after a map start it did not ask for; an operator): the mode told and the one the map that is on loaded
    /// in, as the plan holds them - never the convars, which the block holds at the told mode's values and CS2 can bring another mode back
    /// into - and what is to come. Changes nothing.</summary>
    public string ModeState() => Modes.StateLine(modes.Told, modes.LoadedIn, modes.On, modes.Describe());

    /// <summary>The plan switched to m: its block list read, and the modules told.</summary>
    void UseMode(Modes.Mode m, Modes.Mode was, string why)
    {
        block.Read(m);
        keeper.Extra(Extras);
        log.LogInformation("Over Strike 2 settings: mode {Mode} ({Why}) - {Blocked}{Was}", m.Name, why, block.From, was.Id != m.Id ? $"; was {was.Name}" : "");
        ModeApplied?.Invoke(m);
    }

    static int IntOf(string? text) => int.TryParse(text, out var n) ? n : text?.Trim().ToLowerInvariant() is "true" ? 1 : 0;

    /// <summary>The map that is on started, or the plugin loaded while it is on: the mode its live game_type and game_mode select (ModePlan).
    /// The mode the plan switched to, or null.</summary>
    Modes.Mode? MapMode(string name) => modes.Started(name, OfficialMap(name), IntOf(keeper.Value("game_type")), IntOf(keeper.Value("game_mode")));

    /// <summary>One of Valve's own maps rather than a Workshop map: on a mode's official list (Modes.cs), or in the game's own maps folder.</summary>
    static bool OfficialMap(string name)
    {
        if (name == "") return false;
        if (Modes.All.Any(m => m.Official.Contains(name, StringComparer.OrdinalIgnoreCase))) return true;
        try { return File.Exists(Path.Combine(Server.GameDirectory, "csgo", "maps", name + ".vpk")); } catch { return false; }
    }

    /// <summary>The second load, ModePlan.AgainAfterSeconds from now (ModePlan.Again): a load that comes first - the bridge's, of a change it
    /// made - ends the wait with the map.</summary>
    void ArmAgain(string name, string why)
    {
        log.LogInformation("Over Strike 2 settings: {Why} - loading it once more in {Mode} in {Seconds} s, unless a load comes first", why, modes.Again?.Name, ModePlan.AgainAfterSeconds);
        plugin.AddTimer((float)ModePlan.AgainAfterSeconds, () => LoadAgain(name), TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>game_type and game_mode at the told mode's values on the map's first frame - the block's work at its next look, within a second,
    /// done at once: the bridge's probe of a map start it did not ask for, its first RCON after the start, then reads the told mode rather than
    /// the one CS2 brought back, and tells the plugin that one after the start (../../bridge/ModeSwitch.cs, ProbeModeIfDue and SyncMode). Read
    /// before the block's look, the old mode would come back as os_mode and call the second load off.</summary>
    void HoldTold(Modes.Mode m)
    {
        keeper.Write("game_type", m.GameType.ToString());
        keeper.Write("game_mode", m.GameMode.ToString());
    }

    /// <summary>The time has come (ArmAgain): the map loaded once more in the told mode, official to official, which brings nothing back - the
    /// bridge's second load, made for any change, an operator's os_mode and changelevel included. Not when it is no longer due: an os_mode since
    /// told the mode the map is in.</summary>
    void LoadAgain(string name)
    {
        var mode = modes.Again;
        if (modes.Map != name || mode == null || modes.Loading() is not { } line)
        {
            log.LogInformation("Over Strike 2 settings: {Map} is not loaded once more - {Told} told since, the mode it is in", name, modes.Told?.Name);
            return;
        }
        log.LogInformation("Over Strike 2 settings: {Map} loads once more, in {Mode}: {Line}", name, mode.Name, line);
        post(Messages.News, $"{name} loads once more, in {mode.Name}");
        Server.ExecuteCommand(line);
    }

    // ---- a map's end and start ------------------------------------------------------------------------------------------------

    /// <summary>Every player change undone (a map's end, the bridge's reset after nobody has been on, the page's Default): each changed convar
    /// back to what it was before the first player change on this map, and every effect back at its default - the grenade drops and catch-up
    /// on. What it says for the console.</summary>
    string UndoFun(string when)
    {
        var done = fun.Undo(when, DateTime.Now);
        if (done.Count == 0) return "no player changes to undo";
        log.LogInformation("Over Strike 2 settings: fun settings undone at {When}: {List}", when, string.Join(", ", done));
        return $"undone {string.Join(", ", done)}";
    }

    void OnMapEnd()
    {
        endSeenSinceStart = true;
        Ending = true;
        try
        {
            UndoFun($"end of {map}"); // the players' changes first: they last until the map changes, and are never part of the baseline
            block.MapEnded(map);
            if (keeper.Pending) keeper.FirstMapEnded(map, fun.Before, modes.Told);
            else keeper.Restore($"end of {map}");
        }
        catch (Exception e) { log.LogError("Over Strike 2 settings: map end failed: {Error}", e); }
    }

    void OnMapStart(string name)
    {
        Ending = false;
        // The mode this map loaded in - its live game_type/game_mode - not the one the bridge told (os_mode): ModePlan.cs says why. Without
        // an os_mode since the plugin loaded (a server start) the live mode is the told one too. A map CS2 brought another mode back under
        // is loaded once more in the told one, unless the bridge does it first.
        var was = modes.On;
        if (MapMode(name) is { } m)
            UseMode(m, was, modes.Told is { } told && told.Id != m.Id ? $"the start of {name}, which loaded in {m.Name} although {told.Name} was told" : $"the start of {name}");
        if (modes.Again is { } again)
        {
            ArmAgain(name, $"{name} came up in {modes.LoadedIn?.Name ?? "none of our modes"} right after a Workshop map, not in {again.Name} as told: CS2 brought back the convars it saved before the Workshop maps");
            Server.NextFrame(() => HoldTold(again));
        }
        var missedEnd = !endSeenSinceStart;
        endSeenSinceStart = false;
        var before = map;
        map = name;
        try
        {
            // Only if no map end was seen since the last start: then this is the last moment before this map's own commands run.
            if (missedEnd) { UndoFun($"start of {name}"); block.MapEnded(before); }
            if (missedEnd && !keeper.Pending) keeper.Restore($"start of {name} (no map end seen after {before})");
        }
        catch (Exception e) { log.LogError("Over Strike 2 settings: map start failed: {Error}", e); }
        plugin.AddTimer(ReportDelay, () => keeper.Report(name), TimerFlags.STOP_ON_MAPCHANGE);
    }
}
