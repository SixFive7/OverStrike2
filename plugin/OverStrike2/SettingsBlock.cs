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

/// <summary>The settings module's game-changers block: blocked.txt's list for the mode in force, held every second.</summary>
sealed partial class Settings
{
    const float BlockEvery = 1f;     // the block's look at the listed convars: a map's attempt holds at most this long
    const float AfterRoundStart = 0.25f; // a map's OnNewGame / OnMultiNewRound commands run with the round start: look again right after
    const int BlockLogLines = 3;     // per convar per map; past it, one summary line a minute and one at the map's end
    // ---- the game-changers block (BlockList.cs) ----
    List<BlockList.Entry> blocked = new();
    string blockedFrom = "not read yet";
    bool blockOn = true;            // mapreset_block on|off, until the plugin reloads
    readonly Dictionary<string, (int Count, DateTime LastLine)> blocksThisMap = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> blockRefused = new(StringComparer.OrdinalIgnoreCase); // a put-back the game refused on this map: logged once
    int blocksTotal;
    string lastBlock = "none yet";


    string BlockedFile => Path.Combine(plugin.ModuleDirectory, Modes.BlockFile);

    /// <summary>The block's list as it must act now: the game type and mode always the mode's own values (os_mode), whatever the list in
    /// force says - between os_mode and the next map start the old mode's list is still on.</summary>
    IEnumerable<BlockList.Entry> BlockedNow() => modes.Told is not { } m ? blocked
        : blocked.Select(e => e.ConVar.Equals("game_type", StringComparison.OrdinalIgnoreCase) ? e with { Value = m.GameType.ToString() }
            : e.ConVar.Equals("game_mode", StringComparison.OrdinalIgnoreCase) ? e with { Value = m.GameMode.ToString() } : e);

    // ---- the block list ---------------------------------------------------------------------------------------------------------

    /// <summary>The block list for the mode in force: blocked.txt's values for it (Modes.Blocked - one file, a value per mode).</summary>
    void ReadBlocked()
    {
        if (!File.Exists(BlockedFile))
        {
            blocked = new();
            blockedFrom = $"no block list ({BlockedFile} not found: nothing is blocked)";
            log.LogWarning("Over Strike 2 settings: {File} not found - no game-changer is blocked", BlockedFile);
            return;
        }
        try
        {
            blocked = Modes.Blocked(File.ReadAllText(BlockedFile), modes.On, out var errors);
            foreach (var error in errors) log.LogWarning("Over Strike 2 settings: {File}: {Error}", Modes.BlockFile, error);
            var unknown = blocked.Where(b => FindVar(b.ConVar) == null).Select(b => b.ConVar).ToList();
            if (unknown.Count > 0) log.LogWarning("Over Strike 2 settings: {File}: unknown to this build, not blocked: {List}", Modes.BlockFile, string.Join(", ", unknown));
            blockedFrom = $"{blocked.Count - unknown.Count} game-changers blocked ({Modes.BlockFile}, {modes.On.Name}'s values{(errors.Count + unknown.Count > 0 ? $", {errors.Count} bad line(s), {unknown.Count} unknown" : "")})";
        }
        catch (Exception e)
        {
            blocked = new();
            blockedFrom = $"block list unreadable ({e.Message}): nothing is blocked";
            log.LogWarning("Over Strike 2 settings: {File} unreadable: {Error}", BlockedFile, e.Message);
        }
    }

    /// <summary>Every second, and right after a round start: each listed convar that a map changed goes back to what it must keep, unless a
    /// player chose it on the fun page. The first lines per convar per map are logged one by one; a map that keeps trying is summed up.</summary>
    void Block(string? when)
    {
        if (mode == "off" || !blockOn || baselinePending || blocked.Count == 0) return;
        try
        {
            var now = DateTime.UtcNow;
            var undone = new List<string>();
            foreach (var (entry, was, want) in BlockList.Due(BlockedNow(), Value, Baseline, fun.Chosen))
            {
                if (blockRefused.Contains(entry.ConVar)) continue;
                var seen = blocksThisMap.GetValueOrDefault(entry.ConVar);
                seen.Count++;
                blocksTotal++;
                if (mode == "dryrun")
                {
                    if (seen.Count <= BlockLogLines) log.LogInformation("Over Strike 2 settings: dry run - would block on {Map}: {Name} {Was} -> {Want} ({Why})", map, entry.ConVar, was, want, entry.Why);
                    blocksThisMap[entry.ConVar] = (seen.Count, now);
                    continue;
                }
                var ok = Write(entry.ConVar, want);
                lastBlock = $"{DateTime.Now:HH:mm:ss} {map}: {entry.ConVar} {was} -> {want}{(ok ? "" : $" (refused, reads {Value(entry.ConVar)})")}";
                if (!ok) { blockRefused.Add(entry.ConVar); log.LogWarning("Over Strike 2 settings: could not block {Name} on {Map}: {Was} -> {Want} refused, reads {Now}", entry.ConVar, map, was, want, Value(entry.ConVar)); }
                else if (seen.Count <= BlockLogLines)
                    log.LogInformation("Over Strike 2 settings: blocked on {Map}{When}: {Name} {Was} -> {Want} ({Why})", map, when != null ? $" at the {when}" : "", entry.ConVar, was, want, entry.Why);
                if (ok && seen.Count == 1) undone.Add(BlockList.Label(entry.ConVar));
                else if ((now - seen.LastLine).TotalSeconds >= 60)
                {
                    log.LogInformation("Over Strike 2 settings: blocked on {Map}: {Name} again - {Count} times on this map so far, the last {Was} -> {Want}", map, entry.ConVar, seen.Count, was, want);
                    seen.LastLine = now;
                }
                if (seen.Count <= BlockLogLines) seen.LastLine = now;
                blocksThisMap[entry.ConVar] = seen;
            }
            // Players are told what the map tried to change and that it was undone - once per setting per map.
            if (undone.Count > 0 && (plugin as OverStrike2Plugin)?.Screen is { } hud) hud.Post(Messages.GameChanger, BlockList.Notice(undone.Distinct().ToList()));
        }
        catch (Exception e) { log.LogWarning("Over Strike 2 settings: block failed: {Error}", e.Message); }
    }

    /// <summary>The block's summary for the map that ends: each convar the map tried more than the logged lines show.</summary>
    void BlockSummary(string ended)
    {
        var many = blocksThisMap.Where(kv => kv.Value.Count > BlockLogLines).Select(kv => $"{kv.Key} {kv.Value.Count} times").ToList();
        if (many.Count > 0) log.LogInformation("Over Strike 2 settings: {Map} kept trying: blocked {List}", ended, string.Join(", ", many));
        blocksThisMap.Clear();
        blockRefused.Clear();
    }
}
