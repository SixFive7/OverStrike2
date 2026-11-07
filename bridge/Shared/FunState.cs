// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.

/// <summary>The players' changes on the map that is on, kept by the settings module: for every convar a player changed, what it was before the
/// first such change on this map. That is what Normal (and the reset of all of them) puts back, so a map's own value survives a player's
/// detour; and while a convar holds a player's value it is exempt from the game-changers block (BlockList.cs) and kept out of the
/// baseline. Free of CounterStrikeSharp: the plugin hands it the live values and the write, which says whether the game took a value.</summary>
public sealed class FunState
{
    readonly Dictionary<string, string> before = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>convar -> its value before the first player change on this map.</summary>
    public IReadOnlyDictionary<string, string> Before => before;

    /// <summary>A player's value is in this convar now (it is exempt from the block, and is put back at the map's end).</summary>
    public bool Chosen(string convar) => before.ContainsKey(convar);

    /// <summary>A player picked this choice: each convar the choice sets gets its value, every other convar of the setting that a player had
    /// changed gets its value from before back - every value read first, then each written through write, which says whether the game took
    /// it. A convar becomes a player's (its value from before remembered, the first time on this map) when its value took or was so already,
    /// or when the game kept a value of its own that is not the one before (a bound), so Normal and the undo still put it back; a value the
    /// game refuses outright leaves the convar as it was, and so does its standing - no player's choice, so the game-changers block still
    /// watches it. A convar is the map's again only when its value from before took. Values that are right already are not written. The
    /// writes made, each with whether it took.</summary>
    public List<(string ConVar, string Value, bool Took)> Apply(Fun.Setting setting, Fun.Choice choice, Func<string, string?> value, Func<string, string, bool> write)
    {
        var plan = Fun.ConVarsOf(setting).Select(convar => (ConVar: convar, Now: value(convar),
            Set: choice.Sets.Where(x => x.ConVar.Equals(convar, StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).FirstOrDefault())).ToList();
        var writes = new List<(string, string, bool)>();
        foreach (var (convar, now, set) in plan)
        {
            if (set != null)
            {
                if ((Fun.Same(now, set) || Write(convar, set) || !Fun.Same(value(convar), now)) && now != null) before.TryAdd(convar, now);
            }
            else if (before.TryGetValue(convar, out var was) && (Fun.Same(now, was) || Write(convar, was))) before.Remove(convar);
        }
        return writes;

        bool Write(string convar, string to)
        {
            var took = write(convar, to);
            writes.Add((convar, to, took));
            return took;
        }
    }

    /// <summary>Every player change undone: each changed convar back to its value from before (the bridge's reset after 10 minutes with
    /// nobody on, and the map's end, before anything else is put back).</summary>
    public List<(string ConVar, string Value)> Reset(Func<string, string?> value) => Reset(value, _ => true);

    /// <summary>The same for the convars which says: one page's (the Fun page's Default leaves the bots alone, the Bots page's leaves the rest).</summary>
    public List<(string ConVar, string Value)> Reset(Func<string, string?> value, Func<string, bool> which)
    {
        var mine = before.Where(kv => which(kv.Key)).ToList();
        var writes = mine.Where(kv => !Fun.Same(value(kv.Key), kv.Value)).Select(kv => (kv.Key, kv.Value)).ToList();
        foreach (var kv in mine) before.Remove(kv.Key);
        return writes;
    }

    /// <summary>The settings a player changed, as "gravity low, bhop on, bots 4" - for the console and the log: those with a player's value that show another choice
    /// than Normal (Bots and Fill to share their convars, and one of them shows Off).</summary>
    public string Describe(Func<string, string?> value)
    {
        var changed = Fun.Settings.Concat(Bots.Settings).Where(s => Fun.ConVarsOf(s).Any(Chosen) && Fun.Showing(s, value) != s.Normal).Select(s => $"{s.Id} {Fun.Showing(s, value).Id}").ToList();
        return changed.Count == 0 ? "no player changes" : string.Join(", ", changed);
    }
}
