using System.Globalization;

namespace OverStrike2;

/// <summary>Which game mode the settings module acts in (GameSettings.cs; ../../bridge/Shared/Modes.cs): the mode the bridge told (os_mode), the
/// mode the map that is on was loaded in, and whose block list and Arms Race-only features are on. No CounterStrikeSharp type in it: the
/// self-test drives it; GameSettings.cs reads the convars and does what it says.
///
/// A map's start decides by the mode that map was loaded in - its live game_type and game_mode - not by the one told: a changelevel (or map)
/// from a Workshop map to an official one brings back every convar CS2 saved when the server last went from an official map to a Workshop map,
/// the mode included (measured 2026-11-01: de_dust2 told Deathmatch came up in Arms Race; 2026-11-07: an operator's os_mode wingman, game_type 0,
/// game_mode 2 and changelevel de_inferno came up in Arms Race after a Workshop map, and in Wingman after de_dust2). host_workshop_map brings
/// back nothing, and nor does a change from an official map. Until such a map is loaded again it keeps its own mode's block list and features,
/// so Wingman's values are never forced onto an Arms Race game. Convars that select none of our modes fall back to the told mode.
///
/// Such a map is loaded once more, official to official, in the told mode (Again): by the bridge for a change it made, a few seconds after the
/// start (../../bridge/ModeSwitch.cs, SyncMode), and by the plugin for any change, an operator's included, AgainAfterSeconds after the start
/// (GameSettings.LoadAgain), unless a load came first and ended the wait with the map. A start right after an official map is never one, so the
/// second load is the last. The block, which keeps game_type and game_mode at the told mode's values, puts back what CS2 brought back without
/// blaming the map (Saved, CanBringBack): live on 2026-11-07 it did so a second before the old map ended, and the map then loaded in the told
/// mode at once - the second load is for when CS2's values land after the start.
///
/// A plugin loaded while a map is on (a reload, css_plugins load) takes the plan of the instance before it (ModeHandover), when that one
/// unloaded on this very map; else it decides with that map's live convars, as its start did, and so ends where a plugin loaded before the map
/// would be by its start. The plan is needed: the block holds game_type and game_mode at the told mode's values, so on a map CS2 brought another
/// mode back under, the convars name the mode told, not the one played (live 2026-11-07: a reload there put Wingman's list on an Arms Race game).
/// It hears of os_mode only around the next map change.</summary>
public sealed class ModePlan
{
    /// <summary>How long after the start of a map CS2 brought another mode back under the plugin loads it once more: well after the bridge's own
    /// second load of a change it made, which goes out 1.5 s after the bridge sees the start and two or three RCON answers later
    /// (../../bridge/ServerLife.cs, OnMapStart; ModeSwitch.cs, SyncMode).</summary>
    public const double AgainAfterSeconds = 8;

    /// <summary>The mode the server loads in: os_mode's, else the first map's own; null until either.</summary>
    public Modes.Mode? Told { get; private set; }

    /// <summary>The mode the map that is on was loaded in; null before a map start, or while its convars select none of our modes.</summary>
    public Modes.Mode? LoadedIn { get; private set; }

    /// <summary>Whose block list and features are on: Arms Race, the boot map's, until a map in another mode starts.</summary>
    public Modes.Mode On { get; private set; } = Modes.ArmsRace;

    /// <summary>The map that is on ("" before the first start this instance sees), and whether it is one of Valve's own maps rather than a
    /// Workshop map.</summary>
    public string Map { get; private set; } = "";
    public bool Official { get; private set; }

    /// <summary>The map that is on is an official map that started right after a Workshop map: CS2 brought back the convars it saved before
    /// the Workshop maps under it, game_type and game_mode with them.</summary>
    public bool Restored { get; private set; }

    /// <summary>The mode the second load went out in (Loading), until the next map start; null while none has.</summary>
    public Modes.Mode? LoadingIn { get; private set; }

    /// <summary>The game_type and game_mode CS2 saved when the server last went from an official map to a Workshop map - what a changelevel
    /// from a Workshop map to an official one brings back - as the Workshop map started with them (host_workshop_map brings nothing back, so
    /// they are the ones set right before it); null until this instance has seen such a change.</summary>
    public (int Type, int Mode)? Saved { get; private set; }

    /// <summary>What CS2 brings back now, for the block to put back without blaming the map (GameChangerBlock.Hold): Saved while a Workshop map
    /// is on (a map change from it to an official map brings them back, live on 2026-11-07 a second before the old map ended), and on an official
    /// map right after one (brought back under it); null on an official map after an official one, which brings nothing back.</summary>
    public (int Type, int Mode)? CanBringBack => Map != "" && (!Official || Restored) ? Saved : null;

    /// <summary>The mode the map that is on is to be loaded in once more, or null: CS2 brought another mode back under it than the one told.
    /// Asked at the map start, and again when the time comes: an os_mode since may have told the mode the map is in.</summary>
    public Modes.Mode? Again => Restored && LoadingIn == null && Told is { } t && LoadedIn?.Id != t.Id ? t : null;

    /// <summary>A map started - or the plugin loaded while one is on - with these live game_type and game_mode; official: it is one of Valve's
    /// own maps. The mode to switch the block list and the features to, or null when they stay.</summary>
    public Modes.Mode? Started(string map, bool official, int gameType, int gameMode)
    {
        Restored = official && Map != "" && !Official; // the first start an instance sees follows no map it knows of
        if (!official && Map != "" && Official) Saved = (gameType, gameMode); // official to Workshop: what CS2 saved on the way
        (Map, Official, LoadingIn) = (map, official, null);
        LoadedIn = Modes.FromConVars(gameType, gameMode);
        Told ??= LoadedIn;
        return Switch(LoadedIn ?? Told);
    }

    /// <summary>os_mode: the mode the server loads in from now on. The mode to switch to at once - the map that is on was loaded in it - or
    /// null: it follows at the next map start.</summary>
    public Modes.Mode? Tell(Modes.Mode m)
    {
        Told = m;
        return LoadedIn?.Id == m.Id ? Switch(m) : null;
    }

    /// <summary>The second load goes out now: what the server runs for it - the told mode's two convars, then the map command, the bridge's
    /// order (Modes.PrepareLine). Null when it is no longer due (Again).</summary>
    public string? Loading()
    {
        if (Again is not { } m) return null;
        LoadingIn = m;
        return $"{m.ConVars};changelevel {Map}";
    }

    /// <summary>What os_settings says of the mode: the one in force, and what is to come when the told one is another.</summary>
    public string Describe() =>
        Told is not { } t ? $"in {On.Name}"
        : LoadingIn is { } l ? $"in {On.Name} ({t.Name} told: {Map} is loading once more, in {l.Name})"
        : Again != null ? $"in {On.Name} ({t.Name} told: {Map} came up in {LoadedIn?.Name ?? "none of our modes"} right after a Workshop map, and loads once more in {t.Name})"
        : t.Id != On.Id ? $"in {On.Name} ({t.Name} told, from the next map start)"
        : $"in {On.Name}";

    Modes.Mode? Switch(Modes.Mode? m)
    {
        if (m == null || m.Id == On.Id) return null;
        On = m;
        return m;
    }

    /// <summary>The plan in one line, for the next instance (ModeHandover); Parse reads it back.</summary>
    public string Save() => string.Join(' ', Map == "" ? "-" : Map, Official ? "1" : "0", Restored ? "1" : "0", LoadingIn?.Id ?? "-", Told?.Id ?? "-", LoadedIn?.Id ?? "-", On.Id,
        Saved is { } s ? $"{s.Type}/{s.Mode}" : "-");

    /// <summary>A plan Save wrote, or null when the text is not one.</summary>
    public static ModePlan? Parse(string text)
    {
        if (text.Split(' ') is not [var map, var official, var restored, var loading, var told, var loadedIn, var on, var saved] || Modes.Find(on) is not { } inForce) return null;
        static Modes.Mode? ModeOf(string id) => id == "-" ? null : Modes.All.FirstOrDefault(m => m.Id == id);
        (int, int)? pair = saved.Split('/') is [var t, var g] && int.TryParse(t, out var type) && int.TryParse(g, out var mode) ? (type, mode) : null;
        if (pair == null && saved != "-") return null;
        return new ModePlan
        {
            Map = map == "-" ? "" : map, Official = official == "1", Restored = restored == "1",
            LoadingIn = ModeOf(loading), Told = ModeOf(told), LoadedIn = ModeOf(loadedIn), On = inForce, Saved = pair,
        };
    }
}

/// <summary>The mode plan handed from an instance that unloads to the next one in this server process (GameSettings.Unload and Load), the way
/// FunHandover hands on the fun settings' word: AppDomain data, which outlives the plugin's own load context. Good once, on the same map, while
/// that map's clock runs on from where it was (a map loaded again since starts its clock anew), and for Fresh.</summary>
public static class ModeHandover
{
    const string Key = "OverStrike2.ModePlan";

    /// <summary>How long the plan stays good: a load later than this has the map's live convars decide.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

    /// <summary>clock: the map's own (the game's current time, which starts anew with every map load).</summary>
    public static void Put(ModePlan plan, double clock, DateTime at) =>
        AppDomain.CurrentDomain.SetData(Key, string.Join('\n', at.ToString("o", CultureInfo.InvariantCulture), clock.ToString("R", CultureInfo.InvariantCulture), plan.Save()));

    /// <summary>Taken once: the plan of the instance before this one, when it unloaded on this map, at most Fresh ago, with the map's clock on
    /// from where it was; else null.</summary>
    public static ModePlan? Take(string map, double clock, DateTime now)
    {
        var text = AppDomain.CurrentDomain.GetData(Key) as string;
        AppDomain.CurrentDomain.SetData(Key, null);
        if (text?.Split('\n') is not [var when, var then, var saved]) return null;
        if (!DateTime.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) || now < at || now - at > Fresh) return null;
        if (!double.TryParse(then, NumberStyles.Float, CultureInfo.InvariantCulture, out var was) || clock < was) return null;
        return ModePlan.Parse(saved) is { } plan && plan.Map != "" && plan.Map == map ? plan : null;
    }
}
