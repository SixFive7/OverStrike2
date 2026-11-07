using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The game convars as the block reads and writes them: the settings module's (ConVarKeeper), or the self-test's fake.</summary>
public interface IConVars
{
    /// <summary>No baseline yet (loaded at a server start, before the first map's end): nothing is blocked.</summary>
    bool Pending { get; }

    string? Value(string name);

    /// <summary>Sets a convar and reads it back: false when the game kept another value.</summary>
    bool Write(string name, string value);

    /// <summary>What a watched convar must be, or null: not watched, or no baseline yet.</summary>
    string? Baseline(string name);

    /// <summary>The running build has a convar of this name.</summary>
    bool Known(string name);

    /// <summary>The convar is cheat-flagged (FCVAR_CHEAT): the engine reverts it to its own default when sv_cheats goes off (CheatsRevert).</summary>
    bool Cheat(string name);
}

/// <summary>The game-changers no map may set (blocked.txt beside the DLL, a value per game mode: ../../bridge/Shared/Modes.cs and
/// ../../bridge/Shared/BlockList.cs). Every second, and right after a round start (GameSettings.cs), each listed convar a map changed goes
/// back to what it must keep - the list's value for the mode, else the baseline (ConVarKeeper) - unless a player chose it on the fun page
/// (FunSwitches.Chosen). The first lines per convar per map are logged one by one, a map that keeps trying is summed up, and players are
/// told once per setting per map what the map tried to change (notice). When the block turns a map's cheats off, the engine's revert of every
/// cheat-flagged setting that follows is part of that one undo (CheatsRevert): put back, a player's choice too, with no notice of its own.
/// No CounterStrikeSharp type in it: the self-test drives it with fake convars and a handed clock.</summary>
public sealed class GameChangerBlock(string moduleDirectory, ILogger log, IConVars convars, FunSwitches fun, Action<string> notice, Func<DateTime>? clock = null)
{
    const int LogLines = 3; // per convar per map; past it, one summary line a minute and one at the map's end

    List<BlockList.Entry> blocked = new();
    readonly Dictionary<string, (int Count, DateTime LastLine)> thisMap = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> refused = new(StringComparer.OrdinalIgnoreCase); // a put-back the game refused on this map: logged once
    readonly CheatsRevert revert = new();
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
            var unknown = blocked.Where(b => !convars.Known(b.ConVar)).Select(b => b.ConVar).ToList();
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
        if (convars.Pending || blocked.Count == 0) return;
        try
        {
            var now = clock?.Invoke() ?? DateTime.UtcNow;
            var undone = new List<string>();
            foreach (var (entry, was, want) in BlockList.Due(Now(told), convars.Value, convars.Baseline, fun.Chosen))
            {
                if (refused.Contains(entry.ConVar)) continue;
                if (revert.Covers(entry.ConVar, convars.Cheat(entry.ConVar), now)) { Reverted(map, entry.ConVar, was, want, "on the list"); continue; }
                var cheatsOff = entry.ConVar.Equals(CheatsRevert.ConVar, StringComparison.OrdinalIgnoreCase) ? PlayersCheatChoices() : null; // before the write: the revert comes with it
                var seen = thisMap.GetValueOrDefault(entry.ConVar);
                seen.Count++;
                total++;
                var ok = convars.Write(entry.ConVar, want);
                if (ok && cheatsOff != null) revert.CheatsOff(now, cheatsOff);
                last = $"{DateTime.Now:HH:mm:ss} {map}: {entry.ConVar} {was} -> {want}{(ok ? "" : $" (refused, reads {convars.Value(entry.ConVar)})")}";
                if (!ok) { refused.Add(entry.ConVar); log.LogWarning("Over Strike 2 settings: could not block {Name} on {Map}: {Was} -> {Want} refused, reads {Now}", entry.ConVar, map, was, want, convars.Value(entry.ConVar)); }
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
            // The engine's revert after the block turned cheats off, when it came with the write: every cheat-flagged setting on the list, and the
            // players' cheat-flagged choices, back in this same look.
            if (revert.Open(now))
            {
                foreach (var (entry, was, want) in BlockList.Due(Now(told), convars.Value, convars.Baseline, fun.Chosen))
                    if (!refused.Contains(entry.ConVar) && revert.Covers(entry.ConVar, convars.Cheat(entry.ConVar), now)) Reverted(map, entry.ConVar, was, want, "on the list");
                foreach (var (name, value) in revert.Taken(convars.Value, fun.Chosen, now)) Reverted(map, name, convars.Value(name) ?? "?", value, "a player's fun choice");
            }
            // Players are told what the map tried to change and that it was undone - once per setting per map.
            if (undone.Count > 0) notice(BlockList.Notice(undone.Distinct().ToList()));
        }
        catch (Exception e) { log.LogWarning("Over Strike 2 settings: block failed: {Error}", e.Message); }
    }

    /// <summary>The players' cheat-flagged fun choices as they are now (Infinite ammo on is one): what the engine's revert takes when cheats go off.</summary>
    List<(string ConVar, string Value)> PlayersCheatChoices() =>
        Fun.AllConVars.Where(c => !Fun.IsEffect(c) && fun.Chosen(c) && convars.Cheat(c)).Select(c => (c, convars.Value(c))).Where(x => x.Item2 != null).Select(x => (x.c, x.Item2!)).ToList();

    /// <summary>A setting the engine reverted when the block turned cheats off, put back as part of that undo: logged, but no try of the map's -
    /// a later change of the map's own is still said - and no notice of its own: the one for cheats says it.</summary>
    void Reverted(string map, string name, string was, string want, string what)
    {
        total++;
        var ok = convars.Write(name, want);
        last = $"{DateTime.Now:HH:mm:ss} {map}: {name} {was} -> {want}, with cheats{(ok ? "" : $" (refused, reads {convars.Value(name)})")}";
        if (ok) log.LogInformation("Over Strike 2 settings: put back on {Map}: {Name} {Was} -> {Want} ({What}) - the engine reverted it when cheats went off, part of that undo", map, name, was, want, what);
        else if (refused.Add(name)) log.LogWarning("Over Strike 2 settings: could not put back {Name} on {Map} after cheats went off: {Was} -> {Want} refused, reads {Now}", name, map, was, want, convars.Value(name));
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
    public string List => string.Join(", ", blocked.Select(b => $"{b.ConVar} {b.Value ?? convars.Baseline(b.ConVar) ?? "?"}"));
}
