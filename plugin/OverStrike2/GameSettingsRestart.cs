using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>Only our own code restarts the game (RestartGuard.cs, release 195). The convar's change callback only notes a set (NoteRestart): plugin
/// 5.4.2 wrote the 0 back right inside it, and the engine segfaulted some 170-200 ms later, twice in two refusals. The put-back is made from the
/// frame instead - before the world updates, before the entities think (the game's rules think after them), and at the tick - so a map's
/// mp_restartgame is gone before the game reads it, as long as one of those looks comes first. A set the game took before any look escaped: its one
/// restart goes through, and the convar is held for the rest of the map - flagged a cheat setting, so the game itself refuses the map's next set
/// (sv_cheats stays 0: the game-changers block keeps it there). A restart loop the round starts show is held the same way. The bridge's Restart
/// match and os_restart go through; the hold ends with the map.</summary>
sealed partial class GameSettings
{
    const int RestartLines = 3; // refusals logged one by one per map; past it one line a minute, and the count at the map's end

    readonly RestartGuard restarts = new();
    ConVar? restartVar;
    ConVarFlags? flagsBeforeHold; // the convar's own flags while it is held; null when it is not
    DateTime restartLineAt;       // the last line about a refusal on this map
    bool noteFaultSaid, lookFaultSaid;

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
        plugin.RegisterListener<Listeners.OnConVarChanged>(NoteRestart);
        plugin.RegisterListener<Listeners.OnServerPreWorldUpdate>(_ => LookAtRestart("the frame's start"));
        plugin.RegisterListener<Listeners.OnServerPreEntityThink>(() => LookAtRestart("the entities' think"));
        plugin.RegisterListener<Listeners.OnTick>(() => LookAtRestart("the tick"));
        plugin.RegisterEventHandler<EventRoundStart>((_, _) => { RoundStartedForRestarts(); return HookResult.Continue; });
    }

    /// <summary>The convar's change callback: it notes a set of mp_restartgame for the next look, and writes nothing - not the convar, not its flags.</summary>
    void NoteRestart(ConVarBase cv)
    {
        try
        {
            if (cv.Name.Equals(RestartGuard.ConVar, StringComparison.OrdinalIgnoreCase)) restarts.Noted(cv.ValueAsString);
        }
        catch (Exception e)
        {
            if (noteFaultSaid) return;
            noteFaultSaid = true;
            log.LogError("Over Strike 2 settings: the restart guard's note failed at a change (said once): {Error}", e.Message);
        }
    }

    /// <summary>A look from the frame, never from a change callback: a value that is not ours goes back to 0, a set the game took first is held.</summary>
    void LookAtRestart(string at)
    {
        if (restartVar is not { } v) return;
        try
        {
            var now = DateTime.UtcNow;
            var value = v.StringValue;
            var (rematchAt, bridgeNow) = Rematch?.Invoke(now) ?? (0, 0);
            switch (restarts.Look(value, now, rematchAt, bridgeNow))
            {
                case RestartGuard.Verdict.LetThrough:
                    log.LogInformation("Over Strike 2 settings: {ConVar} on {Map} - ours ({Whose}): the game restarts", RestartGuard.ConVar, map, restarts.LastOurs);
                    break;
                case RestartGuard.Verdict.Refused:
                    v.StringValue = "0";
                    if (restarts.Refused <= RestartLines)
                    {
                        log.LogWarning("Over Strike 2 settings: refused on {Map} at {At}: {ConVar} {Value} -> 0 - only our own code restarts the game (Restart match, os_restart)",
                            map, at, RestartGuard.ConVar, value);
                        restartLineAt = now;
                    }
                    else if ((now - restartLineAt).TotalSeconds >= 60)
                    {
                        log.LogWarning("Over Strike 2 settings: refused on {Map}: {ConVar} again - {Count} times on this map so far", map, RestartGuard.ConVar, restarts.Refused);
                        restartLineAt = now;
                    }
                    break;
                case RestartGuard.Verdict.Escaped:
                    log.LogWarning("Over Strike 2 settings: a restart we did not cause escaped on {Map} - the game took {ConVar} before {At}, so it restarts this once",
                        map, RestartGuard.ConVar, at);
                    Hold("a restart escaped the look");
                    break;
            }
        }
        catch (Exception e)
        {
            if (lookFaultSaid) return;
            lookFaultSaid = true;
            log.LogError("Over Strike 2 settings: the restart guard's look failed (said once): {Error}", e.Message);
        }
    }

    /// <summary>Held for the rest of the map: the convar is flagged a cheat setting, so the game refuses a set of it from a map or RCON while
    /// sv_cheats is 0. os_restart still goes through; the bridge's Restart match does not.</summary>
    void Hold(string why)
    {
        if (flagsBeforeHold != null || restartVar is not { } v) return;
        try
        {
            var before = v.Flags;
            v.Flags = before | ConVarFlags.FCVAR_CHEAT;
            flagsBeforeHold = before;
            if (v.StringValue != "0") v.StringValue = "0";
            log.LogWarning("Over Strike 2 settings: {ConVar} held on {Map} until the map ends ({Why}): a cheat setting now, which the game refuses to a map's set - "
                + "os_restart still restarts the match, the Restart match cannot", RestartGuard.ConVar, map, why);
        }
        catch (Exception e) { log.LogError("Over Strike 2 settings: could not hold {ConVar}: {Error}", RestartGuard.ConVar, e.Message); }
    }

    void FreeRestarts(string why)
    {
        if (flagsBeforeHold is not { } f || restartVar is not { } v) return;
        flagsBeforeHold = null;
        try { v.Flags = f; }
        catch (Exception e) { log.LogError("Over Strike 2 settings: could not free {ConVar}: {Error}", RestartGuard.ConVar, e.Message); return; }
        log.LogInformation("Over Strike 2 settings: {ConVar} free again ({Why})", RestartGuard.ConVar, why);
    }

    void RoundStartedForRestarts()
    {
        if (restartVar is not { } v) return;
        try
        {
            var value = v.StringValue;
            var (putBack, loop) = restarts.Started(DateTime.UtcNow, value);
            if (putBack) v.StringValue = "0";
            if (!loop) return;
            log.LogWarning("Over Strike 2 settings: a restart loop on {Map} that we did not cause - {Starts} round starts, each within {Gap} s of the one before; "
                + "{ConVar} read {Value}{PutBack}; refused {Refused}, escaped {Escaped} on this map so far", map, RestartGuard.LoopStarts, RestartGuard.LoopGapSeconds,
                RestartGuard.ConVar, value, putBack ? ", put back to 0" : "", restarts.Refused, restarts.Escaped);
            Hold("a restart loop");
        }
        catch (Exception e) { log.LogError("Over Strike 2 settings: the restart guard failed at a round start: {Error}", e.Message); }
    }

    /// <summary>A map's end, or a start with no end seen: what the guard refused on the map that goes, if anything; the hold is that map's alone.</summary>
    void RestartsAtMapEnd(string ended)
    {
        if (restarts.Refused > 0 || restarts.Escaped > 0 || restarts.Loops > 0)
            log.LogWarning("Over Strike 2 settings: end of {Map}: {ConVar} refused {Count} time(s), escaped {Escaped}{Loops}", ended, RestartGuard.ConVar, restarts.Refused,
                restarts.Escaped, restarts.Loops > 0 ? $", {restarts.Loops} restart loop(s) seen" : "");
        FreeRestarts($"end of {ended}");
    }

    /// <summary>os_restart [seconds]: the match restarts, as mp_restartgame makes it - ours, so the guard lets it through once (RestartGuard). The
    /// console's way and the server's tools', now that a bare mp_restartgame is refused; the bridge's Restart match needs none of it. os_restart hold:
    /// the hold on the map that is on (to see it work); os_restart free: the hold off.</summary>
    [ConsoleCommand("os_restart", "Over Strike 2 settings: restart the match - mp_restartgame, let through: os_restart [seconds, 1-60] | hold | free")]
    [CommandHelper(minArgs: 0, usage: "[seconds | hold | free]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnRestart(CCSPlayerController? player, CommandInfo command)
    {
        var arg = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "";
        if (arg == "hold")
        {
            Hold("os_restart hold");
            command.ReplyToCommand($"os_restart: {RestartGuard.ConVar} {(flagsBeforeHold != null ? "held until the map ends" : "not held (no such convar, or the hold failed)")}");
            return;
        }
        if (arg == "free")
        {
            FreeRestarts("os_restart free");
            command.ReplyToCommand($"os_restart: {RestartGuard.ConVar} free");
            return;
        }
        if (restartVar is not { } v)
        {
            command.ReplyToCommand($"os_restart: this build has no {RestartGuard.ConVar}");
            return;
        }
        var seconds = int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 1, 60) : 1;
        restarts.Own(DateTime.UtcNow);
        var held = flagsBeforeHold;
        try
        {
            if (held is { } f) v.Flags = f; // a held convar takes our own write too
            v.StringValue = seconds.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception e) { log.LogError("Over Strike 2 settings: os_restart could not set {ConVar}: {Error}", RestartGuard.ConVar, e.Message); }
        finally
        {
            if (held is { } f) { try { v.Flags = f | ConVarFlags.FCVAR_CHEAT; } catch { } }
        }
        command.ReplyToCommand($"os_restart: the match restarts in {seconds} s");
    }
}
