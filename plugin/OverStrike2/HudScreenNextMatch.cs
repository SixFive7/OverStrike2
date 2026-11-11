using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The match-end screen cut short (../../bridge/Shared/NextMatch.cs says what and why): when the vote plays the map again, the bridge sends os_nextmatch
/// with the countdown's seconds, and the game's own end of the screen moves to that moment - the game then starts the next match there, on the same map, as at
/// any end of the screen.</summary>
sealed partial class HudScreen
{
    int cutSeq; // the last cut: the check of an older one says nothing

    void NextMatchLoad() => plugin.AddCommand(NextMatch.Command,
        "Over Strike 2: the next match starts in <seconds> - the match-end screen ends then: os_nextmatch <seconds> [start] (the bridge's, when the vote plays the map again)", OnNextMatchCommand);

    void OnNextMatchCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return; // the server console and RCON only
        var reply = int.TryParse(command.GetArg(1), out var seconds) && seconds is >= NextMatch.LeastSeconds and <= NextMatch.MostSeconds
            ? CutMatchEnd(seconds, command.GetArg(2) == "start")
            : NextMatch.Reply(NextMatch.Result.Refused, $"{NextMatch.Command} <seconds, {NextMatch.LeastSeconds} to {NextMatch.MostSeconds}> [start]");
        log.LogInformation("Over Strike 2: {Reply}", reply);
        command.ReplyToCommand(reply);
    }

    /// <summary>The screen's end moves to seconds from now, if it is up and would end later. The field is the server's own (the clients' countdown is the game's to
    /// work out), so nothing is marked for the network. moveStart: its start moves back by as much (NextMatch.Command).</summary>
    string CutMatchEnd(int seconds, bool moveStart)
    {
        var proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        if (proxy is not { IsValid: true } || proxy.GameRules is not { } rules) return NextMatch.Reply(NextMatch.Result.Refused, "no game rules");
        if (rules.GamePhase != MatchEndedPhase) return NextMatch.Reply(NextMatch.Result.NotEnded, $"game phase {rules.GamePhase}, nothing moved");
        var now = Server.CurrentTime;
        var (start, end) = (rules.IntermissionStartTime, rules.IntermissionEndTime);
        var was = end > 0 ? $"{end - now:0.0} s" : "no end time";
        if (NextMatch.NewEnd(now, end, seconds) is not { } newEnd) return NextMatch.Reply(NextMatch.Result.Sooner, $"the screen ends in {was} by itself, nothing moved");
        rules.IntermissionEndTime = newEnd;
        if (moveStart && end > 0) rules.IntermissionStartTime = start - (end - newEnd);
        var seq = ++cutSeq;
        plugin.AddTimer(seconds + NextMatch.CheckSeconds, () => CheckCut(seq), TimerFlags.STOP_ON_MAPCHANGE);
        return NextMatch.Reply(NextMatch.Result.Set, $"the screen ends in {seconds} s, not {was}{(moveStart && end > 0 ? $", its start moved {end - newEnd:0.0} s back" : "")}"
            + $" (up {now - start:0.0} s, game time {now:0.0})");
    }

    /// <summary>NextMatch.CheckSeconds after the new end: did the next match start? Logged either way - a screen still up means the game did not take the new end,
    /// and it ends the screen at its own time, as before.</summary>
    void CheckCut(int seq)
    {
        if (seq != cutSeq) return;
        var phase = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault() is { IsValid: true, GameRules: { } rules } ? rules.GamePhase : -1;
        if (phase == MatchEndedPhase)
            log.LogWarning("Over Strike 2: {Command}: the match-end screen is still up {Seconds} s after its new end - the game did not take it, and ends the screen at its own time",
                NextMatch.Command, NextMatch.CheckSeconds);
        else log.LogInformation("Over Strike 2: {Command}: the next match started at the screen's new end (game phase {Phase} {Seconds} s after it)", NextMatch.Command, phase, NextMatch.CheckSeconds);
    }
}
