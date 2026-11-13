using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The match-end screen cut short (../../bridge/Shared/NextMatch.cs says what and why): when the vote plays the map again, the bridge sends os_nextmatch
/// with the countdown's seconds, and mp_match_restart_delay, by which the game ends the screen counted from its start, becomes the time the screen has been up plus
/// those seconds - the game then starts the next match there, on the same map, as at any end of the screen. The value it had goes back once the next match's round
/// has started, or NextMatch.CheckSeconds after the new end at the latest; a map change drops it, since the new map's cfg sets its own. Neither change of the
/// convar is announced: the game's "server cvar changed" notice for it goes to nobody.</summary>
sealed partial class HudScreen
{
    int cutSeq;                  // the last cut: the check of an older one says nothing
    string? delayBack;           // mp_match_restart_delay as it was before a cut, until it goes back (null: nothing to put back)
    DateTime quietUntil;         // a change of the delay of our own until then: its notice goes to nobody

    void NextMatchLoad()
    {
        plugin.AddCommand(NextMatch.Command,
            "Over Strike 2: the next match starts in <seconds> - the match-end screen ends then: os_nextmatch <seconds> [start|hold] (the bridge's, when the vote decides)", OnNextMatchCommand);
        plugin.RegisterEventHandler<EventRoundStart>((_, _) => { PutDelayBack("the next match's round has started"); return HookResult.Continue; });
        plugin.RegisterEventHandler<EventServerCvar>(OnServerCvar, HookMode.Pre);
        plugin.RegisterListener<Listeners.OnMapStart>(_ => { delayBack = null; cutSeq++; }); // the new map's cfg has set its own delay: nothing to put back, no check to make
        plugin.AddCommand(MatchPhase.Command, "Over Strike 2: the game's phase - 5 while the match-end screen is up (the bridge's, before a restart of the match)", OnPhaseCommand);
    }

    /// <summary>os_phase: the game rules' phase, for the bridge (../../bridge/Shared/MatchPhase.cs). Not logged: the bridge asks at every press while a match's end is fresh.</summary>
    void OnPhaseCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return; // the server console and RCON only
        command.ReplyToCommand(Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault() is { IsValid: true, GameRules: { } rules }
            ? MatchPhase.Reply(rules.GamePhase) : MatchPhase.NoRules);
    }

    void OnNextMatchCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return; // the server console and RCON only
        var reply = int.TryParse(command.GetArg(1), out var seconds) && seconds is >= NextMatch.LeastSeconds and <= NextMatch.MostSeconds
            ? CutMatchEnd(seconds, command.GetArg(2) == "start", command.GetArg(2) == NextMatch.HoldWord)
            : NextMatch.Reply(NextMatch.Result.Refused, $"{NextMatch.Command} <seconds, {NextMatch.LeastSeconds} to {NextMatch.MostSeconds}> [start|{NextMatch.HoldWord}]");
        log.LogInformation("Over Strike 2: {Reply}", reply);
        command.ReplyToCommand(reply);
    }

    /// <summary>The screen ends seconds from now, if it is up and would end later - or, with hold, also when it would end sooner: the delay becomes the time it has been
    /// up plus the seconds (NextMatch.NewDelay), or, with moveStart, its start moves to now + seconds - the delay. The reply says when it ends then, to a hundredth
    /// of a second (the whole-second convar ends it up to a second after the seconds asked: the bridge counts to that), and the game rules' clocks as they were.</summary>
    string CutMatchEnd(int seconds, bool moveStart, bool hold = false)
    {
        var proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        if (proxy is not { IsValid: true } || proxy.GameRules is not { } rules) return NextMatch.Reply(NextMatch.Result.Refused, "no game rules");
        var now = Server.CurrentTime;
        var start = rules.IntermissionStartTime;
        var cv = ConVarKeeper.Find(NextMatch.Delay);
        var clocks = string.Create(CultureInfo.InvariantCulture,
            $"now {now:0.00}, start {start:0.00}, end {rules.IntermissionEndTime:0.00}, restart {rules.RestartRoundTime:0.00}, next phase in {rules.TimeUntilNextPhaseStarts:0.00}, phase {rules.GamePhase}, {NextMatch.Delay} {cv?.StringValue ?? "none"}");
        if (rules.GamePhase != MatchEndedPhase) return NextMatch.Reply(NextMatch.Result.NotEnded, $"nothing changed ({clocks})");
        if (cv == null || !double.TryParse(cv.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var delay) || delay <= 0)
            return NextMatch.Reply(NextMatch.Result.Refused, $"no {NextMatch.Delay} to read ({clocks})");
        if (start <= 0 || start > now) return NextMatch.Reply(NextMatch.Result.Refused, $"the screen's start is not known ({clocks})");
        var left = start + delay - now;
        if (NextMatch.NewDelay(now, start, delay, seconds, whole: cv.Type is not (ConVarType.Float32 or ConVarType.Float64), hold) is not { } value)
            return NextMatch.Reply(NextMatch.Result.Sooner, string.Create(CultureInfo.InvariantCulture, $"the screen ends in {left:0.0} s by itself, nothing changed ({clocks})"));
        string what;
        var endsIn = moveStart ? seconds : start + value - now; // the convar ends it at the screen's start plus the new delay
        if (moveStart)
        {
            rules.IntermissionStartTime = (float)(now + seconds - delay);
            what = string.Create(CultureInfo.InvariantCulture, $"the screen's start moved {start:0.00} -> {rules.IntermissionStartTime:0.00}");
        }
        else
        {
            delayBack ??= cv.StringValue;
            var text = value.ToString(CultureInfo.InvariantCulture);
            quietUntil = DateTime.UtcNow.AddSeconds(1);
            cv.StringValue = text;
            what = $"{NextMatch.Delay} {delay.ToString(CultureInfo.InvariantCulture)} -> {cv.StringValue}";
        }
        var seq = ++cutSeq;
        plugin.AddTimer((float)endsIn + NextMatch.CheckSeconds, () => CheckCut(seq), TimerFlags.STOP_ON_MAPCHANGE);
        return NextMatch.Reply(NextMatch.Result.Set, string.Create(CultureInfo.InvariantCulture, $"the screen, up {now - start:0.0} s, ends in {endsIn:0.00} s, not {left:0.0} s{(hold ? ", held" : "")}: {what} ({clocks})"));
    }

    /// <summary>NextMatch.CheckSeconds after the new end: did the next match start? Logged either way, and the delay goes back if it has not yet - a screen still
    /// up then means the game did not take the new end, and ends it at its own time, as before.</summary>
    void CheckCut(int seq)
    {
        if (seq != cutSeq) return;
        var phase = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault() is { IsValid: true, GameRules: { } rules } ? rules.GamePhase : -1;
        PutDelayBack(phase == MatchEndedPhase ? "the screen is still up" : $"{NextMatch.CheckSeconds} s after the new end");
        if (phase == MatchEndedPhase)
            log.LogWarning("Over Strike 2: {Command}: the match-end screen is still up {Seconds} s after its new end - the game did not take it, and ends the screen at its own time",
                NextMatch.Command, NextMatch.CheckSeconds);
        else log.LogInformation("Over Strike 2: {Command}: the next match started at the screen's new end (game phase {Phase} {Seconds} s after it)", NextMatch.Command, phase, NextMatch.CheckSeconds);
    }

    /// <summary>The delay as it was before the cut goes back, once.</summary>
    void PutDelayBack(string why)
    {
        if (delayBack is not { } back) return;
        delayBack = null;
        if (ConVarKeeper.Find(NextMatch.Delay) is not { } cv) return;
        quietUntil = DateTime.UtcNow.AddSeconds(1);
        cv.StringValue = back;
        log.LogInformation("Over Strike 2: {Command}: {Delay} back at {Value} - {Why}", NextMatch.Command, NextMatch.Delay, cv.StringValue, why);
    }

    /// <summary>The game's notice of a changed convar, before it goes out: one of ours to the delay goes to nobody.</summary>
    HookResult OnServerCvar(EventServerCvar e, GameEventInfo info)
    {
        if (DateTime.UtcNow < quietUntil && string.Equals(e.Cvarname, NextMatch.Delay, StringComparison.OrdinalIgnoreCase)) info.DontBroadcast = true;
        return HookResult.Continue;
    }
}
