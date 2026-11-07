using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>Bot war's promise kept (FUN, Bots: the bots fight each other and leave the players alone). CS2's bot_ignore_players only keeps the players out of
/// what the bots see - "Bots will not see non-bot players" - so a bot never aims at a player; but its shots at another bot, its grenades and its fire still hurt a
/// player in the way, and the live lab lost its player to one within 25 seconds. While Bot war is on, the player damage hook (Listeners.OnPlayerTakeDamagePre)
/// refuses a bot's damage to a player (Bots.Spares, ../../bridge/Shared/Bots.cs). It answers Stop, which ends the hook there: catch-up's hook after it never sees
/// a hit that did not land, so no second chance or spawn protection is spent on one. Registered after the vote's freeze, whose hook keeps the first word, and
/// before catch-up's. A player's damage to a bot, and a bot's to a bot, land as ever; so does anything while Bot war is off.</summary>
sealed class BotWarGuard(BasePlugin plugin, ILogger log, Func<bool> war)
{
    int spared;   // hits spared on this map, for the log: the first is logged, and how many at the map's end
    int faults;   // errors in the hook on this map: the first is logged

    public void Load()
    {
        plugin.RegisterListener<Listeners.OnPlayerTakeDamagePre>(OnTakeDamage);
        plugin.RegisterListener<Listeners.OnMapEnd>(() =>
        {
            if (spared > 1) log.LogInformation("Over Strike 2 settings: Bot war spared the players {Count} hits by bots on this map", spared);
            spared = faults = 0;
        });
    }

    HookResult OnTakeDamage(CCSPlayerPawn pawn, CTakeDamageInfo info)
    {
        try
        {
            if (info.Attacker.Value is not { IsValid: true, DesignerName: "player" } source) return HookResult.Continue; // the world, a fall
            if (source.As<CCSPlayerPawn>().Controller.Value?.As<CCSPlayerController>() is not { IsValid: true } attacker) return HookResult.Continue;
            if (pawn.Controller.Value?.As<CCSPlayerController>() is not { IsValid: true } victim) return HookResult.Continue;
            if (!Bots.Spares(war, attacker.IsBot, victim.IsBot)) return HookResult.Continue;
            if (spared++ == 0)
            {
                var (bot, player) = (attacker.PlayerName, victim.PlayerName);
                Server.NextFrame(() => log.LogInformation("Over Strike 2 settings: Bot war spared {Player} a hit by {Bot} - the bots see no player, and the plugin keeps their stray shots, grenades and fire off the players", player, bot));
            }
            return HookResult.Stop;
        }
        catch (Exception e)
        {
            if (faults++ == 0) log.LogError("Over Strike 2 settings: Bot war's damage hook failed - the hit lands as ever: {Error}", e);
            return HookResult.Continue;
        }
    }
}
