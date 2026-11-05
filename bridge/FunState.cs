// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.

/// <summary>The players' changes on the map that is on, kept by the settings module: for every convar a player changed, what it was before the
/// first such change on this map. That is what Normal (and the reset of all of them) puts back, so a map's own value survives a player's
/// detour; and while a convar holds a player's value it is exempt from the game-changers block (BlockList.cs) and kept out of the
/// baseline. Free of CounterStrikeSharp: the plugin hands it the live values and makes the writes it returns.</summary>
public sealed class FunState
{
    readonly Dictionary<string, string> before = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>convar -> its value before the first player change on this map.</summary>
    public IReadOnlyDictionary<string, string> Before => before;

    /// <summary>A player's value is in this convar now (it is exempt from the block, and is put back at the map's end).</summary>
    public bool Chosen(string convar) => before.ContainsKey(convar);

    /// <summary>A player picked this choice: the writes it takes - each convar the choice sets gets its value (and its value from before
    /// is remembered, the first time on this map), every other convar of the setting that a player had changed gets its value from before
    /// back and is no longer a player's. Values that are right already are not written.</summary>
    public List<(string ConVar, string Value)> Apply(Fun.Setting setting, Fun.Choice choice, Func<string, string?> value)
    {
        var writes = new List<(string, string)>();
        foreach (var convar in Fun.ConVarsOf(setting))
        {
            var now = value(convar);
            var set = choice.Sets.Where(x => x.ConVar.Equals(convar, StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).FirstOrDefault();
            if (set != null)
            {
                if (!before.ContainsKey(convar) && now != null) before[convar] = now;
                if (!Fun.Same(now, set)) writes.Add((convar, set));
            }
            else if (before.Remove(convar, out var was) && !Fun.Same(now, was)) writes.Add((convar, was));
        }
        return writes;
    }

    /// <summary>Every player change undone: each changed convar back to its value from before (the bridge's reset after 10 minutes with
    /// nobody on, and the map's end, before anything else is put back).</summary>
    public List<(string ConVar, string Value)> Reset(Func<string, string?> value)
    {
        var writes = before.Where(kv => !Fun.Same(value(kv.Key), kv.Value)).Select(kv => (kv.Key, kv.Value)).ToList();
        before.Clear();
        return writes;
    }

    /// <summary>The settings a player changed, as "gravity low, bhop on" - for the console and the log.</summary>
    public string Describe(Func<string, string?> value)
    {
        var changed = Fun.Settings.Where(s => Fun.ConVarsOf(s).Any(Chosen)).Select(s => $"{s.Id} {Fun.Showing(s, value).Id}").ToList();
        return changed.Count == 0 ? "no player changes" : string.Join(", ", changed);
    }
}
