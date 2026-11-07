namespace OverStrike2;

/// <summary>Which game mode the settings module acts in (GameSettings.cs; ../../bridge/Shared/Modes.cs): the mode the bridge told (os_mode), the
/// mode the map that is on was loaded in, and whose block list and Arms Race-only features are on. No CounterStrikeSharp type in it: the
/// self-test drives it; GameSettings.cs reads the convars and does what it says.
///
/// A map's start decides by the mode that map was loaded in - its live game_type and game_mode - not by the one told: a changelevel from a
/// Workshop map to an official one can bring back the convars CS2 saved before, the mode included (measured 2026-11-01: de_dust2 told
/// Deathmatch came up in Arms Race). The bridge then loads it once more in the told mode; until that load, the map keeps its own mode's
/// block list and features, so Deathmatch's values are never forced onto an Arms Race map. Convars that select none of our modes fall back
/// to the told mode. A plugin loaded while a map is on (a reload, css_plugins load) decides the same way at its load, with that map's live
/// convars, and so ends where a plugin loaded before the map would be by its start; it hears of os_mode only around the next map change.</summary>
public sealed class ModePlan
{
    /// <summary>The mode the server loads in: os_mode's, else the first map's own; null until either.</summary>
    public Modes.Mode? Told { get; private set; }

    /// <summary>The mode the map that is on was loaded in; null before a map start, or while its convars select none of our modes.</summary>
    public Modes.Mode? LoadedIn { get; private set; }

    /// <summary>Whose block list and features are on: Arms Race, the boot map's, until a map in another mode starts.</summary>
    public Modes.Mode On { get; private set; } = Modes.ArmsRace;

    /// <summary>A map started - or the plugin loaded while one is on - with these live game_type and game_mode. The mode to switch the block
    /// list and the features to, or null when they stay.</summary>
    public Modes.Mode? Started(int gameType, int gameMode)
    {
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

    Modes.Mode? Switch(Modes.Mode? m)
    {
        if (m == null || m.Id == On.Id) return null;
        On = m;
        return m;
    }
}
