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

/// <summary>The settings module's game mode: which one it acts in (ModePlan), and os_mode.</summary>
sealed partial class Settings
{
    // ---- the game mode (docs/reference.md) ----
    readonly ModePlan modes = new();        // os_mode's mode, the one the map that is on was loaded in, and whose block list is on (ModePlan.cs)
    Modes.Mode? modeAtLoad;                 // a load while a map is on found that map in another mode than Arms Race: the modules hear of it in Ready

    /// <summary>The mode in force for the features: the one the map that is on was loaded in.</summary>
    public Modes.Mode Mode => modes.On;

    /// <summary>Raised when a mode's features go on (at the map start that loads it, or at once when the map that is on is in it, or - for a
    /// load while a map is on - when the host is ready for it).</summary>
    public event Action<Modes.Mode>? ModeApplied;

    /// <summary>os_mode &lt;id&gt; (the bridge: before every map change, with the convars, and after every map start): game_type and
    /// game_mode keep the mode's values from now on - in the put-back at the old map's end and in the block - and the mode's block list
    /// and Arms Race-only features follow at the next map start, or at once when the map that is on was loaded in that mode.</summary>
    public string SetMode(Modes.Mode m)
    {
        var was = modes.On;
        var now = modes.Tell(m);
        KeepModeValues();
        if (now != null) { UseMode(now, was, "now: the map that is on is in it"); return $"mode {m.Id}: on now"; }
        return modes.On.Id == m.Id ? $"mode {m.Id}: on already" : $"mode {m.Id}: game_type {m.GameType}, game_mode {m.GameMode} kept; its block list and features at the next map start";
    }

    /// <summary>The mode's game_type and game_mode as the baseline the put-back and the block keep.</summary>
    void KeepModeValues()
    {
        if (modes.Told is not { } m) return;
        foreach (var (name, value) in new[] { ("game_type", m.GameType), ("game_mode", m.GameMode) })
            if (managed.TryGetValue(name, out var x) || extra.TryGetValue(name, out x)) x.Baseline = value.ToString();
    }

    /// <summary>The plan switched to m: its block list read, and the modules told.</summary>
    void UseMode(Modes.Mode m, Modes.Mode was, string why)
    {
        ReadBlocked();
        BuildExtra();
        log.LogInformation("Over Strike 2 settings: mode {Mode} ({Why}) - {Blocked}{Was}", m.Name, why, blockedFrom, was.Id != m.Id ? $"; was {was.Name}" : "");
        ModeApplied?.Invoke(m);
    }

    static int IntOf(string? text) => int.TryParse(text, out var n) ? n : text?.Trim().ToLowerInvariant() is "true" ? 1 : 0;

    /// <summary>The map that is on started, or the plugin loaded while it is on: the mode its live game_type and game_mode select (ModePlan).
    /// The mode the plan switched to, or null.</summary>
    Modes.Mode? MapMode() => modes.Started(IntOf(Value("game_type")), IntOf(Value("game_mode")));
}
