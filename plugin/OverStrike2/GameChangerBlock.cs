using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The game-changers no map may set (blocked.txt beside the DLL, a value per game mode: ../../bridge/Shared/Modes.cs and
/// ../../bridge/Shared/BlockList.cs). Every second, and right after a round start (GameSettings.cs), each listed convar a map changed goes
/// back to what it must keep - the list's value for the mode, else the baseline (ConVarKeeper) - unless a player chose it on the fun page
/// (FunSwitches.Chosen). The first lines per convar per map are logged one by one, a map that keeps trying is summed up, and players are
/// told once per setting per map what the map tried to change (notice).</summary>
sealed class GameChangerBlock(string moduleDirectory, ILogger log, ConVarKeeper keeper, FunSwitches fun, Action<string> notice)
{
    const int LogLines = 3; // per convar per map; past it, one summary line a minute and one at the map's end

    List<BlockList.Entry> blocked = new();
    readonly Dictionary<string, (int Count, DateTime LastLine)> thisMap = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> refused = new(StringComparer.OrdinalIgnoreCase); // a put-back the game refused on this map: logged once
    int total;
    string last = "none yet";

    /// <summary>Where the list in force came from, in words: "30 game-changers blocked (blocked.txt, Arms Race's values)".</summary>
    public string From { get; private set; } = "not read yet";

    /// <summary>The convars on the list in force.</summary>
    public IEnumerable<string> ConVars => blocked.Select(b => b.ConVar);

    string BlockedFile => Path.Combine(moduleDirectory, Modes.BlockFile);

    /// <summary>The block list for this mode: blocked.txt's values for it (Modes.Blocked - one file, a value per mode).</summary>
    public void Read(Modes.Mode mode)
    {
        if (!File.Exists(BlockedFile))
        {
            blocked = new();
            From = $"no block list ({BlockedFile} not found: nothing is blocked)";
            log.LogWarning("Over Strike 2 settings: {File} not found - no game-changer is blocked", BlockedFile);
            return;
        }
        try
        {
            blocked = Modes.Blocked(File.ReadAllText(BlockedFile), mode, out var errors);
            foreach (var error in errors) log.LogWarning("Over Strike 2 settings: {File}: {Error}", Modes.BlockFile, error);
            var unknown = blocked.Where(b => ConVarKeeper.Find(b.ConVar) == null).Select(b => b.ConVar).ToList();
            if (unknown.Count > 0) log.LogWarning("Over Strike 2 settings: {File}: unknown to this build, not blocked: {List}", Modes.BlockFile, string.Join(", ", unknown));
            From = $"{blocked.Count - unknown.Count} game-changers blocked ({Modes.BlockFile}, {mode.Name}'s values{(errors.Count + unknown.Count > 0 ? $", {errors.Count} bad line(s), {unknown.Count} unknown" : "")})";
        }
        catch (Exception e)
        {
            blocked = new();
            From = $"block list unreadable ({e.Message}): nothing is blocked";
            log.LogWarning("Over Strike 2 settings: {File} unreadable: {Error}", BlockedFile, e.Message);
        }
    }

    /// <summary>The list as it must act now: the game type and mode always the told mode's own values (os_mode), whatever the list in force
    /// says - between os_mode and the next map start the old mode's list is still on.</summary>
    IEnumerable<BlockList.Entry> Now(Modes.Mode? told) => told is not { } m ? blocked
        : blocked.Select(e => e.ConVar.Equals("game_type", StringComparison.OrdinalIgnoreCase) ? e with { Value = m.GameType.ToString() }
            : e.ConVar.Equals("game_mode", StringComparison.OrdinalIgnoreCase) ? e with { Value = m.GameMode.ToString() } : e);

    /// <summary>Every listed convar that a map changed goes back to what it must keep, unless a player chose it on the fun page. when: what
    /// prompted this look ("round start"), or null for the look every second.</summary>
    public void Hold(string map, string? when, Modes.Mode? told)
    {
        if (keeper.Pending || blocked.Count == 0) return;
        try
        {
            var now = DateTime.UtcNow;
            var undone = new List<string>();
            foreach (var (entry, was, want) in BlockList.Due(Now(told), keeper.Value, keeper.Baseline, fun.Chosen))
            {
                if (refused.Contains(entry.ConVar)) continue;
                var seen = thisMap.GetValueOrDefault(entry.ConVar);
                seen.Count++;
                total++;
                var ok = keeper.Write(entry.ConVar, want);
                last = $"{DateTime.Now:HH:mm:ss} {map}: {entry.ConVar} {was} -> {want}{(ok ? "" : $" (refused, reads {keeper.Value(entry.ConVar)})")}";
                if (!ok) { refused.Add(entry.ConVar); log.LogWarning("Over Strike 2 settings: could not block {Name} on {Map}: {Was} -> {Want} refused, reads {Now}", entry.ConVar, map, was, want, keeper.Value(entry.ConVar)); }
                else if (seen.Count <= LogLines)
                    log.LogInformation("Over Strike 2 settings: blocked on {Map}{When}: {Name} {Was} -> {Want} ({Why})", map, when != null ? $" at the {when}" : "", entry.ConVar, was, want, entry.Why);
                if (ok && seen.Count == 1) undone.Add(BlockList.Label(entry.ConVar));
                else if ((now - seen.LastLine).TotalSeconds >= 60)
                {
                    log.LogInformation("Over Strike 2 settings: blocked on {Map}: {Name} again - {Count} times on this map so far, the last {Was} -> {Want}", map, entry.ConVar, seen.Count, was, want);
                    seen.LastLine = now;
                }
                if (seen.Count <= LogLines) seen.LastLine = now;
                thisMap[entry.ConVar] = seen;
            }
            // Players are told what the map tried to change and that it was undone - once per setting per map.
            if (undone.Count > 0) notice(BlockList.Notice(undone.Distinct().ToList()));
        }
        catch (Exception e) { log.LogWarning("Over Strike 2 settings: block failed: {Error}", e.Message); }
    }

    /// <summary>The block's summary for the map that ends: each convar the map tried more than the logged lines show.</summary>
    public void MapEnded(string map)
    {
        var many = thisMap.Where(kv => kv.Value.Count > LogLines).Select(kv => $"{kv.Key} {kv.Value.Count} times").ToList();
        if (many.Count > 0) log.LogInformation("Over Strike 2 settings: {Map} kept trying: blocked {List}", map, string.Join(", ", many));
        thisMap.Clear();
        refused.Clear();
    }

    /// <summary>The block for the console: where the list came from, how often it held, the last time.</summary>
    public string Describe => $"{From}; {total} block(s) since load, the last {last}";

    /// <summary>Every listed convar and what it must keep now.</summary>
    public string List => string.Join(", ", blocked.Select(b => $"{b.ConVar} {b.Value ?? keeper.Baseline(b.ConVar) ?? "?"}"));
}
