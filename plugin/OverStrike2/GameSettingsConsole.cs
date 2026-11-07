using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The settings module's console commands, server only (RCON and the server console): gg_fun, which the HUD and the bridge send,
/// and os_settings and os_block, an operator's look at the put-back and the block.</summary>
sealed partial class GameSettings
{
    /// <summary>gg_fun &lt;setting&gt; &lt;choice&gt; ["who"]: a change from the console, as the HUD makes a player's in process (ChangeFun).
    /// gg_fun reset ["who"]: every player change undone and every effect at its default - the bridge's reset after 10 minutes with nobody on
    /// (ResetFun). Bare gg_fun: what each setting shows now. Server only: no chat trigger reaches it.</summary>
    [ConsoleCommand(Fun.Command, "Fun settings: gg_fun <setting> <choice> [who] | gg_fun reset [who] | gg_fun")]
    [CommandHelper(minArgs: 0, usage: "[<setting> <choice> | reset] [who]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnFun(CCSPlayerController? player, CommandInfo command)
    {
        var verb = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "";
        if (verb == "")
        {
            command.ReplyToCommand($"fun: {fun.Showing()} ({fun.Describe()}); last: {fun.Last}");
            return;
        }
        if (verb == "reset")
        {
            command.ReplyToCommand($"fun: reset - {ResetFun(command.ArgCount > 2 ? command.GetArg(2) : "Console")}");
            return;
        }
        if (Fun.Find(verb) is not { } setting || command.ArgCount < 3 || Fun.Find(setting, command.GetArg(2)) is not { } choice)
        {
            command.ReplyToCommand("fun: gg_fun <setting> <choice> [who] - " + string.Join("; ", Fun.Settings.Select(s => $"{s.Id} {string.Join("|", s.Choices.Select(c => c.Id))}")));
            return;
        }
        command.ReplyToCommand($"fun: {ChangeFun(setting, choice, command.ArgCount > 3 ? command.GetArg(3) : "Console")}");
    }

    /// <summary>os_settings: the watched settings that differ from the baseline now, the last put-back, the block and the fun page. os_settings
    /// now: the put-back right now. os_settings rebaseline: the current values become the wanted ones (after an intended change).</summary>
    [ConsoleCommand("os_settings", "Over Strike 2 settings: what differs from the baseline, the block and the fun page; 'now' puts the watched settings back now; 'rebaseline' takes the current values as the wanted ones")]
    [CommandHelper(minArgs: 0, usage: "[now | rebaseline]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnSettings(CCSPlayerController? player, CommandInfo command)
    {
        var verb = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "";
        if (verb == "now")
        {
            keeper.Restore("by command");
            command.ReplyToCommand($"os_settings: {keeper.LastRestore}");
            return;
        }
        if (verb == "rebaseline")
        {
            keeper.Rebaseline(map, fun.Before, modes.Told);
            command.ReplyToCommand($"os_settings: baseline {keeper.BaselineFrom}");
            return;
        }
        var diffs = keeper.Differences();
        command.ReplyToCommand($"os_settings: {OverStrike2Plugin.Version}, in {modes.On.Name}{(modes.Told is { } told && told.Id != modes.On.Id ? $" ({told.Name} told, from the next map start)" : "")}, {keeper.Describe}");
        command.ReplyToCommand($"os_settings: the list: {keeper.ListFrom}");
        command.ReplyToCommand($"os_settings: last: {keeper.LastRestore}");
        command.ReplyToCommand(diffs.Count == 0 ? "os_settings: nothing differs from the baseline"
            : $"os_settings: {diffs.Count} differ: " + string.Join(", ", diffs.Select(d => $"{d.Name} {d.Now} (baseline {d.Want})")));
        command.ReplyToCommand($"os_settings: block {block.Describe}");
        command.ReplyToCommand($"os_settings: fun {fun.Describe()}; last {fun.Last}");
        if (keeper.Stubborn.Count > 0) command.ReplyToCommand("os_settings: refused a put-back: " + string.Join(", ", keeper.Stubborn));
    }

    /// <summary>os_block: the game-changers block's list for the mode in force, each with what it keeps. os_block reload: blocked.txt read again,
    /// no rebuild needed.</summary>
    [ConsoleCommand("os_block", "Over Strike 2 settings: the game-changers block's list; 'reload' reads blocked.txt again")]
    [CommandHelper(minArgs: 0, usage: "[reload]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnBlock(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount > 1 && command.GetArg(1).Equals("reload", StringComparison.OrdinalIgnoreCase))
        {
            block.Read(modes.On);
            keeper.Extra(Extras);
            log.LogInformation("Over Strike 2 settings: {Blocked}", block.From);
        }
        command.ReplyToCommand($"os_block: {block.From}: {block.List}");
    }
}
