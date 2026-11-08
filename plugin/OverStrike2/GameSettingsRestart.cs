using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>Only our own code restarts the game (RestartGuard.cs, release 195): a map's mp_restartgame is put back to 0 the moment it is set (the
/// engine's change callbacks run inside the write, before the game's next think reads the value) and at the look every tick behind it; the bridge's
/// Restart match and os_restart go through; and quick round starts that we did not cause are logged as a loop, with mp_restartgame put back.</summary>
sealed partial class GameSettings
{
    const int RestartLines = 3; // refusals logged one by one per map; past it one line a minute, and the count at the map's end

    readonly RestartGuard restarts = new();
    ConVar? restartVar;
    bool puttingBack;          // our own write of 0: the change it causes is ours, not looked at again
    DateTime restartLineAt;    // the last line about a refusal on this map
    bool restartFaultSaid;     // a failure at a change is logged once: the listener hears every convar's change

    /// <summary>The bridge's Restart match: its moment on the bridge's clock (0: none heard of) and that clock now - the HUD's (HudScreen.Rematch),
    /// handed in by the plugin at its load. Without it only os_restart goes through.</summary>
    public Func<DateTime, (long AtMs, long NowMs)>? Rematch { get; set; }

    void LoadRestarts()
    {
        restartVar = ConVarKeeper.Find(RestartGuard.ConVar);
        if (restartVar == null)
        {
            log.LogWarning("Over Strike 2 settings: this build has no {ConVar} - nothing to guard", RestartGuard.ConVar);
            return;
        }
        plugin.RegisterListener<Listeners.OnConVarChanged>(OnConVarChanged);
        plugin.RegisterListener<Listeners.OnTick>(() => { if (!puttingBack && restartVar is { } v) LookAtRestart(SafeValue(v), "a look", s => v.StringValue = s); });
        plugin.RegisterEventHandler<EventRoundStart>((_, _) => { RoundStartedForRestarts(); return HookResult.Continue; });
    }

    static string? SafeValue(ConVar v)
    {
        try { return v.StringValue; } catch { return null; }
    }

    void OnConVarChanged(ConVarBase cv)
    {
        if (puttingBack) return;
        try
        {
            if (!cv.Name.Equals(RestartGuard.ConVar, StringComparison.OrdinalIgnoreCase)) return;
            LookAtRestart(cv.ValueAsString, "its change", s => cv.ValueAsString = s);
        }
        catch (Exception e)
        {
            if (restartFaultSaid) return;
            restartFaultSaid = true;
            log.LogError("Over Strike 2 settings: the restart guard failed at a change (said once): {Error}", e.Message);
        }
    }

    void LookAtRestart(string? value, string how, Action<string> write)
    {
        var now = DateTime.UtcNow;
        var (at, bridgeNow) = Rematch?.Invoke(now) ?? (0, 0);
        switch (restarts.Look(value, now, at, bridgeNow))
        {
            case RestartGuard.Verdict.LetThrough:
                log.LogInformation("Over Strike 2 settings: {ConVar} {Value} on {Map} - ours ({Whose}): the game restarts", RestartGuard.ConVar, value, map, restarts.LastOurs);
                break;
            case RestartGuard.Verdict.Refused:
                PutRestartBack(write);
                if (restarts.Refused <= RestartLines)
                {
                    log.LogWarning("Over Strike 2 settings: refused on {Map} at {How}: {ConVar} {Value} -> 0 - only our own code restarts the game (Restart match, os_restart)",
                        map, how, RestartGuard.ConVar, value);
                    restartLineAt = now;
                }
                else if ((now - restartLineAt).TotalSeconds >= 60)
                {
                    log.LogWarning("Over Strike 2 settings: refused on {Map}: {ConVar} again - {Count} times on this map so far", map, RestartGuard.ConVar, restarts.Refused);
                    restartLineAt = now;
                }
                break;
        }
    }

    void PutRestartBack(Action<string> write)
    {
        puttingBack = true;
        try { write("0"); }
        catch (Exception e) { log.LogError("Over Strike 2 settings: could not put {ConVar} back to 0: {Error}", RestartGuard.ConVar, e.Message); }
        finally { puttingBack = false; }
    }

    void RoundStartedForRestarts()
    {
        try
        {
            if (restartVar is not { } v) return;
            var value = SafeValue(v);
            var (putBack, loop) = restarts.Started(DateTime.UtcNow, value);
            if (putBack) PutRestartBack(s => v.StringValue = s);
            if (loop)
                log.LogWarning("Over Strike 2 settings: a restart loop on {Map} that we did not cause - {Starts} round starts, each within {Gap} s of the one before; "
                    + "{ConVar} read {Value}{PutBack}; refused {Refused} time(s) on this map so far", map, RestartGuard.LoopStarts, RestartGuard.LoopGapSeconds,
                    RestartGuard.ConVar, value, putBack ? ", put back to 0" : "", restarts.Refused);
        }
        catch (Exception e) { log.LogError("Over Strike 2 settings: the restart guard failed at a round start: {Error}", e.Message); }
    }

    /// <summary>The map's end: what the guard refused on it, if anything.</summary>
    void RestartsAtMapEnd(string ended)
    {
        if (restarts.Refused > 0 || restarts.Loops > 0)
            log.LogWarning("Over Strike 2 settings: end of {Map}: {ConVar} refused {Count} time(s){Loops}", ended, RestartGuard.ConVar, restarts.Refused,
                restarts.Loops > 0 ? $", {restarts.Loops} restart loop(s) seen" : "");
    }

    /// <summary>os_restart [seconds]: the match restarts, as mp_restartgame makes it - ours, so the guard lets it through once (RestartGuard). The
    /// console's way and the server's tools', now that a bare mp_restartgame is refused; the bridge's Restart match needs none of it.</summary>
    [ConsoleCommand("os_restart", "Over Strike 2 settings: restart the match - mp_restartgame, let through: os_restart [seconds, 1-60]")]
    [CommandHelper(minArgs: 0, usage: "[seconds]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnRestart(CCSPlayerController? player, CommandInfo command)
    {
        var seconds = command.ArgCount > 1 && int.TryParse(command.GetArg(1), out var s) ? Math.Clamp(s, 1, 60) : 1;
        restarts.Own(DateTime.UtcNow);
        if (restartVar is { } v)
        {
            try { v.StringValue = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            catch { Server.ExecuteCommand($"{RestartGuard.ConVar} {seconds}"); }
        }
        else Server.ExecuteCommand($"{RestartGuard.ConVar} {seconds}");
        command.ReplyToCommand($"os_restart: the match restarts in {seconds} s");
    }
}
