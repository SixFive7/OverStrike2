using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The bots' damage to a player, refused where a bot should leave the player alone - one rule, Bots.Spares (../../bridge/Shared/Bots.cs), two cases:
///   - Bot war (FUN, Bots): bot_ignore_players covers the bots' sight only - its help text is "Bots will not see non-bot players" - so a bot never aims at a
///     player, while its shots at another bot, its grenades and its fire are not covered. While Bot war is on, none of them hurts a player;
///   - the cloak (Cloak.cs: the spawn cloak, the fun page's Ghost, os_cloak hide): it hides a player from people - the game draws nothing of them - but a bot aims
///     by what the server knows, and shoots a hidden player as readily as any. The live lab's player, on its spawn with the window open 19 s into the spawn
///     cloak, fell to one shot from a bot about 1300 units away. While the cloak hides a player, no bot's damage hurts them - until the player's own first
///     attack in that hide (CloakShield, CloakPlan.cs): a shot, a throw or a hit on somebody else ends it, though not the hide, so nobody hidden fights the
///     bots without the bots fighting back.
/// A player's damage lands on everybody, hidden or not, and a bot's on a bot. The hook answers Stop, which ends the damage hooks there (CounterStrikeSharp
/// 1.0.379 calls them in the order registered and returns at the first Stop): catch-up's hook after it never sees a hit that did not land, so no second
/// chance or spawn protection is spent on one. Registered after the vote's freeze, whose own Stop comes first, and before catch-up. The first hit spared on a
/// map is logged for each case, and how many at the map's end.</summary>
sealed class BotGuard(BasePlugin plugin, ILogger log, Func<bool> war, Cloak cloak)
{
    readonly CloakShield shield = new();
    int byWar, byCloak;   // hits spared on this map, for the log
    int faults;           // errors in the hook on this map: the first is logged

    public void Load()
    {
        plugin.RegisterListener<Listeners.OnPlayerTakeDamagePre>(OnTakeDamage);
        // A hidden player's own attack ends the shield of the hide they are in: a shot (a knife's swing and the Zeus's too), a throw, a hit on somebody else.
        plugin.RegisterEventHandler<EventWeaponFire>((ev, _) => Attacked(ev.Userid));
        plugin.RegisterEventHandler<EventGrenadeThrown>((ev, _) => Attacked(ev.Userid));
        plugin.RegisterEventHandler<EventPlayerHurt>((ev, _) => ev.Attacker is { IsValid: true } a && ev.Userid is { IsValid: true } v && a.Slot != v.Slot ? Attacked(a) : HookResult.Continue);
        plugin.RegisterListener<Listeners.OnMapEnd>(() =>
        {
            if (byWar > 0) log.LogInformation("Over Strike 2 settings: Bot war spared the players {Count} hit(s) by bots on this map", byWar);
            if (byCloak > 0) log.LogInformation("Over Strike 2 cloak: spared hidden players {Count} hit(s) by bots on this map", byCloak);
            byWar = byCloak = faults = 0;
            shield.Clear();
        });
    }

    HookResult Attacked(CCSPlayerController? player)
    {
        if (player is { IsValid: true, IsBot: false }) shield.Attacked(player.Slot, cloak.Hide(player.Slot));
        return HookResult.Continue;
    }

    HookResult OnTakeDamage(CCSPlayerPawn pawn, CTakeDamageInfo info)
    {
        try
        {
            if (info.Attacker.Value is not { IsValid: true, DesignerName: "player" } source) return HookResult.Continue; // the world, a fall
            if (source.As<CCSPlayerPawn>().Controller.Value?.As<CCSPlayerController>() is not { IsValid: true } attacker) return HookResult.Continue;
            if (pawn.Controller.Value?.As<CCSPlayerController>() is not { IsValid: true } victim) return HookResult.Continue;
            var slot = victim.Slot;
            var spare = Bots.Spares(attacker.IsBot, victim.IsBot, war, () => shield.Shields(slot, cloak.Hide(slot)));
            if (spare == Bots.Spare.None) return HookResult.Continue;
            if (spare == Bots.Spare.BotWar ? byWar++ == 0 : byCloak++ == 0)
            {
                var (bot, player, hidden) = (attacker.PlayerName, victim.PlayerName, cloak.Why(slot));
                Server.NextFrame(() =>
                {
                    if (spare == Bots.Spare.BotWar)
                        log.LogInformation("Over Strike 2 settings: Bot war spared {Player} a hit by {Bot} - the bots see no player, and the plugin keeps their stray shots, grenades and fire off the players", player, bot);
                    else
                        log.LogInformation("Over Strike 2 cloak: spared {Player} a hit by {Bot} - hidden ({Why}) with no attack of theirs since: a bot aims by what the server knows, so the cloak alone would not hide them from it", player, bot, hidden);
                });
            }
            return HookResult.Stop;
        }
        catch (Exception e)
        {
            if (faults++ == 0) log.LogError("Over Strike 2: the bots' damage guard failed - the hit lands as ever: {Error}", e);
            return HookResult.Continue;
        }
    }
}
