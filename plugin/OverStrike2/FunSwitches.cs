using System.Globalization;

namespace OverStrike2;

/// <summary>The fun page's switches, as the settings module keeps them (GameSettings.cs): the players' changes of the map that is on
/// (../../bridge/Shared/FunState.cs: each changed convar's value from before, so Normal and the undo put back a map's own value), who made
/// each one, and the effects that are no game convar (Fun.Vampire, Fun.Ghost, Fun.NoRecoil, Fun.Drops, Fun.CatchUp) - kept here like convars,
/// "1" or "0", each starting at its default: on for the grenade drops and catch-up (Fun.EffectsOn), off for the rest. A player's change lasts
/// until the map changes, is never part of the baseline and is never blocked. No CounterStrikeSharp type in it: the convars are read and
/// written through the two functions handed in, and the self-test drives it with fakes.</summary>
public sealed class FunSwitches(Func<string, string?> convar, Func<string, string, bool> write)
{
    readonly FunState fun = new();
    readonly Dictionary<string, string> effects = new(StringComparer.OrdinalIgnoreCase); // an effect not in it holds its default
    readonly Dictionary<string, string> setBy = new(StringComparer.OrdinalIgnoreCase);   // setting id -> who set it, while it is off its default

    /// <summary>The last change or undo, for the console: "21:04:11 gravity moon by Remy: sv_gravity 200".</summary>
    public string Last { get; private set; } = "none yet";

    /// <summary>An effect changed (a player's change, the undo at a map's end, the reset): its name and whether it is on now. The plugin hands
    /// it to the modules that act on them.</summary>
    public event Action<string, bool>? EffectChanged;

    /// <summary>What a fun setting's convar or effect holds now: the HUD's fun page reads it, the modules ask whether an effect is on.</summary>
    public string? Value(string name) => Fun.IsEffect(name) ? effects.TryGetValue(name, out var on) ? on : EffectDefault(name) : convar(name);

    public bool EffectOn(string name) => Fun.Same(Value(name), "1");

    /// <summary>Who set a fun setting that is off its default (gg_fun's last argument), or null: at its default, or not known.</summary>
    public string? SetBy(string settingId) => setBy.GetValueOrDefault(settingId);

    /// <summary>A player's value is in this convar now: it is exempt from the block and kept out of the baseline.</summary>
    public bool Chosen(string name) => fun.Chosen(name);

    /// <summary>convar -> its value before the first player change on this map: what a baseline taken now takes instead of the player's.</summary>
    public IReadOnlyDictionary<string, string> Before => fun.Before;

    /// <summary>The settings a player changed, as "gravity low, bhop on".</summary>
    public string Describe() => fun.Describe(Value);

    /// <summary>Every setting and the choice it shows now, as "gravity normal, jump high, ...".</summary>
    public string Showing() => string.Join(", ", Fun.Settings.Select(s => $"{s.Id} {Fun.Showing(s, Value).Id}"));

    /// <summary>An effect's default: on for those of Fun.EffectsOn (the grenade drops, catch-up), off for every other.</summary>
    static string EffectDefault(string name) => Fun.EffectsOn.FirstOrDefault(e => e.Effect.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? "0";

    /// <summary>Every effect the fun page can switch.</summary>
    static IEnumerable<string> EffectNames => Fun.AllConVars.Where(Fun.IsEffect);

    /// <summary>A convar written as the put-back always writes it, and read back: false when the game kept another value. An effect is this
    /// class's own: it always takes, and says so when it changed.</summary>
    bool Write(string name, string value)
    {
        if (!Fun.IsEffect(name)) return write(name, value);
        var was = EffectOn(name);
        effects[name] = value;
        var on = EffectOn(name);
        if (on != was) EffectChanged?.Invoke(name, on);
        return true;
    }

    /// <summary>A player's change from the fun page, chat or gg_fun: the writes it takes (FunState.Apply), made, and who made it noted until
    /// the setting is back at its default. The line for the log and the console's reply.</summary>
    public string Apply(Fun.Setting setting, Fun.Choice choice, string by, DateTime now)
    {
        var writes = fun.Apply(setting, choice, Value);
        var refused = writes.Where(w => !Write(w.ConVar, w.Value)).Select(w => w.ConVar).ToList();
        if (choice == setting.Normal) setBy.Remove(setting.Id); else if (refused.Count == 0) setBy[setting.Id] = by;
        var line = $"{setting.Id} {choice.Id} by {by}: {(writes.Count == 0 ? "nothing to change" : string.Join(", ", writes.Select(w => $"{w.ConVar} {w.Value}")))}{(refused.Count > 0 ? $"; refused {string.Join(", ", refused)}" : "")}";
        Last = $"{now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} {line}";
        return line;
    }

    /// <summary>Every player change undone (a map's end, the reset after nobody has been on, the page's Default): each
    /// changed convar back to what it was before the first player change on this map, then every effect back at its default, whatever left
    /// it elsewhere. What it changed, as "sv_gravity -> 800"; empty when there was nothing to undo.</summary>
    public List<string> Undo(string when, DateTime now)
    {
        setBy.Clear(); // every setting goes back to its default: nobody set any
        var done = fun.Reset(Value).Select(w => $"{w.ConVar} -> {w.Value}{(Write(w.ConVar, w.Value) ? "" : " (refused)")}").ToList();
        foreach (var name in EffectNames)
        {
            var want = EffectDefault(name);
            if (Fun.Same(Value(name), want)) continue;
            Write(name, want);
            done.Add($"{name} -> {want} (its default)");
        }
        if (done.Count > 0) Last = $"{now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} {when}: undone {string.Join(", ", done)}";
        return done;
    }
}
