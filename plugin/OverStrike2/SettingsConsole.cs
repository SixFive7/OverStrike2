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

/// <summary>The settings module's console commands (server only: RCON and the server console).</summary>
sealed partial class Settings
{
    // ---- console (server only: RCON and the server console) ------------------------------------------------------------------

    [ConsoleCommand("mapreset_check", "Over Strike 2 settings: the watched settings that differ from the baseline now, the last put-back, the block and the fun page")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnCheck(CCSPlayerController? player, CommandInfo command)
    {
        var diffs = Differences();
        command.ReplyToCommand($"mapreset: {ModuleVersion}, mode {mode}, {managed.Count} watched (+{extra.Count} for the block and the fun page), baseline {baselineFrom}");
        command.ReplyToCommand($"mapreset: the list: {listFrom}");
        command.ReplyToCommand($"mapreset: last: {lastRestore}");
        command.ReplyToCommand(diffs.Count == 0 ? "mapreset: nothing differs from the baseline"
            : $"mapreset: {diffs.Count} differ: " + string.Join(", ", diffs.Select(d => $"{d.Name} {d.Now} (baseline {d.Want})")));
        command.ReplyToCommand($"mapreset: block {(blockOn ? "on" : "OFF (mapreset_block)")}, {blockedFrom}; {blocksTotal} block(s) since load, the last {lastBlock}");
        command.ReplyToCommand($"mapreset: fun {fun.Describe(Value)}; last {lastFun}");
        if (stubborn.Count > 0) command.ReplyToCommand("mapreset: refused a put-back: " + string.Join(", ", stubborn));
    }

    [ConsoleCommand("mapreset_now", "Over Strike 2 settings: put the watched settings back right now")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnNow(CCSPlayerController? player, CommandInfo command)
    {
        Restore("by command");
        command.ReplyToCommand($"mapreset: {lastRestore}");
    }

    [ConsoleCommand("mapreset_mode", "Over Strike 2 settings: on (default) | dryrun (log only) | off, until the plugin reloads")]
    [CommandHelper(minArgs: 0, usage: "[on|dryrun|off]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnMode(CCSPlayerController? player, CommandInfo command)
    {
        var arg = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "";
        if (arg is "on" or "dryrun" or "off") { mode = arg; log.LogInformation("Over Strike 2 settings: mode {Mode}", mode); }
        command.ReplyToCommand($"mapreset: mode {mode}");
    }

    [ConsoleCommand("mapreset_block", "Over Strike 2 settings: the game-changers block - on (default) | off, until the plugin reloads; reload reads blocked.txt again; bare: the list")]
    [CommandHelper(minArgs: 0, usage: "[on|off|reload]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnBlock(CCSPlayerController? player, CommandInfo command)
    {
        var arg = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "";
        if (arg is "on" or "off") { blockOn = arg == "on"; log.LogInformation("Over Strike 2 settings: block {State}", arg); }
        if (arg == "reload") { ReadBlocked(); BuildExtra(); log.LogInformation("Over Strike 2 settings: {Blocked}", blockedFrom); }
        command.ReplyToCommand($"mapreset: block {(blockOn ? "on" : "off")}, {blockedFrom}: {string.Join(", ", blocked.Select(b => $"{b.ConVar} {b.Value ?? Baseline(b.ConVar) ?? "?"}"))}");
    }

    [ConsoleCommand("mapreset_rebaseline", "Over Strike 2 settings: take the current values as the wanted ones (after an intended change)")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnRebaseline(CCSPlayerController? player, CommandInfo command)
    {
        stubborn.Clear();
        baselinePending = false;
        TakeLiveBaseline($"live values on {map} at {DateTime.Now:HH:mm:ss} (mapreset_rebaseline)");
        command.ReplyToCommand($"mapreset: baseline {baselineFrom}");
    }
}
