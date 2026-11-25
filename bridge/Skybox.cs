// A Workshop item can carry its level's 3D skybox as a map of its own beside it (maps/<level>_skybox.vpk), and CS2 can load that submap, where
// nobody can spawn. Live on 2026-11-25 (release 226, CS2 1.42.0.1): a vote changed Assault's mode on Assault itself, the bridge sent
// host_workshop_map for the item that was on, and CS2 loaded cs_assault_skybox; the warm-up waited for a spawn that could not come, and the players
// left (every load of the item from another map gave cs_assault). Two guards. A change to the Workshop item that is on - its mode - goes by
// changelevel to the level it is on, so CS2 picks no map of the item (SameItemLevel, from FireChange); and a map start of a skybox submap, whatever
// loaded it, goes on at once to its level (SkyboxOut): the name without "_skybox", else the level the same item logged at its last start in the
// history - the item's addon is mounted then, so changelevel finds it.
static partial class Program
{
    public const string SkyboxSuffix = "_skybox";
    public const int SkyboxOutMs = 2000; // after a skybox's map start: past the mode's word 1.5 s in (SyncMode), as the mode's own second load goes

    /// <summary>Whether a level is a 3D-skybox submap ("&lt;level&gt;_skybox"), where nobody can spawn.</summary>
    static bool IsSkybox(string level) => level.Length > SkyboxSuffix.Length && level.EndsWith(SkyboxSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The levels a skybox submap's item is played on, to try in turn: the name without the suffix, then the level the same item (itemId, 0 when
    /// not known) logged at its last start in the history - never a skybox, each once.</summary>
    static List<string> LevelsOf(string skybox, long itemId)
    {
        var levels = new List<string> { skybox[..^SkyboxSuffix.Length] };
        if (itemId > 0 && History.Entries.FirstOrDefault(e => e.MapId == itemId && e.LogName != "" && !IsSkybox(e.LogName)) is { } last) levels.Add(last.LogName);
        return [.. levels.Where(l => l != "" && !IsSkybox(l)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The level a change to this Workshop map loads by changelevel, or null for host_workshop_map: the level the server is on when the map is the
    /// item that is on - a mode change on the same map - and its load has landed. Never a skybox: the level that one belongs to (LevelsOf) instead.</summary>
    static string? SameItemLevel(MapInfo map)
    {
        if (map.Official || map.Id <= 0 || LoadPending || S.Map is not { Official: false } on || on.Id != map.Id || S.LogName is not { Length: > 0 } level) return null;
        return IsSkybox(level) ? LevelsOf(level, map.Id).FirstOrDefault() : level;
    }

    /// <summary>A map start (ServerLife.cs): a skybox submap has SkyboxOut due, once.</summary>
    static void SkyboxStarted(string level)
    {
        if (IsSkybox(level)) After(SkyboxOutMs, () => SkyboxOut(level));
    }

    /// <summary>A skybox submap is on: its item's level at once, by changelevel, the mode's commands first as for a change (PrepareMode) when the bridge knows
    /// the item - each level of LevelsOf in turn until the server takes one (no "invalid map name"). Its map start is then awaited as the change's own, so
    /// the map stays the item, picked by whom it was. None taken, it stays, and the players hear that a vote takes them off it: a skybox's level is always
    /// beside it, so a name with "_skybox" and no other level the server finds may be a map of its own - never rolled away on its name alone.</summary>
    static void SkyboxOut(string skybox)
    {
        if (!(S.LogName ?? "").Equals(skybox, StringComparison.OrdinalIgnoreCase) || LoadPending || Pending != null) return; // another map, or a change, came meanwhile
        var item = S.Map is { Official: false } m ? m : null; // the item, when the bridge knows it (its own change; an operator's host_workshop_map it does not)
        var name = item != null ? Clip(Label(item), 40) : skybox;
        if (item != null && !PrepareMode(item)) { Log($"skybox: {name} came up as {skybox}, its 3D skybox - RCON did not take the mode's commands, so it stays"); return; }
        foreach (var level in LevelsOf(skybox, item?.Id ?? 0))
        {
            var reply = Send($"changelevel {level}");
            Log($"skybox: {name} came up as {skybox}, its 3D skybox, where nobody can spawn - changelevel {level} -> {reply ?? "(rcon failed)"}");
            if (reply == null) { ModeBack(); return; }      // RCON is down: nothing went out, the mode goes back with it
            if (InvalidMapName(reply)) continue;           // no such level: the next
            if (item != null) AwaitLoad(Now());             // the item's own load: its map start is the change's, as at FireChange
            LastNamedMapStart = default;                     // the level's map start must not pass for the skybox's second line
            Announce($"{name} came up as its skybox, where nobody can spawn - Loading {level} instead", 10);
            return;
        }
        ModeBack();
        Log($"skybox: {name} came up as {skybox}, and the server takes no other level of it - it stays: a name like that may be a map of its own");
        Announce($"{name} may have come up as its skybox - If nobody can spawn, Start a vote for another map");
    }
}
