using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The grenade drops: keeps an HE grenade in play in Arms Race, which hands out only the progression weapon and ignores the grenade
/// convars. Every player gets one shortly after spawning; the engine's mp_death_drop_grenade (set to 2 in gamemode_armsrace_server.cfg) drops it
/// where they die. Nothing is created in the world by this module: spawning weapon entities directly segfaulted the server.
///
/// On or off is the fun page's "drops" setting (Fun.Drops, os2_drops), kept by the settings module like every other fun setting: on by default,
/// back on at every map end and at the reset after nobody has been on. Arms Race's only: off in another mode, whatever the setting says.</summary>
sealed class GrenadeDrops(BasePlugin plugin, ILogger log, Func<bool> dropsOn)
{
    const string Grenade = "weapon_hegrenade";
    const float GiveDelay = 0.3f; // The mode equips the progression weapon right after spawn; hand out the grenade once that is done.

    /// <summary>The grenade drops are Arms Race's (players buy in Deathmatch and Wingman): off in another mode, whatever the setting says.</summary>
    public bool ModeAllows { get; set; } = true;

    bool On => dropsOn() && ModeAllows;

    public void Load()
    {
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        log.LogInformation("Over Strike 2 grenades: a grenade at every spawn in Arms Race while the fun page's grenade drops are on ({State} now)", dropsOn() ? "on" : "off");
    }

    HookResult OnPlayerSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        if (ev.Userid is not { IsValid: true } player || !On) return HookResult.Continue;
        var slot = player.Slot; // Re-resolve by slot later: the controller may be gone by then.
        plugin.AddTimer(GiveDelay, () => GiveGrenade(Utilities.GetPlayerFromSlot(slot)), TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    /// <summary>The drops were switched (the settings module's effect changed): switched on in Arms Race, everyone alive gets a grenade right
    /// away, not only at the next spawn. Switched off, nobody loses the grenade they carry; the next spawns bring none.</summary>
    public void Switched(bool on)
    {
        if (!on || !ModeAllows) return;
        foreach (var p in Utilities.GetPlayers()) GiveGrenade(p);
    }

    static void GiveGrenade(CCSPlayerController? player)
    {
        if (player is not { IsValid: true, PawnIsAlive: true }) return;
        if (Carries(player, Grenade)) return; // Already carrying one (picked up from a body or not thrown yet).
        player.GiveNamedItem(Grenade);
    }

    /// <summary>Whether a player holds a weapon, by designer name - also one that arrives without the prefix or cased differently. MyWeapons
    /// is a vector of CHandle, which is the one element type CounterStrikeSharp's NetworkedVector can actually read.</summary>
    static bool Carries(CCSPlayerController player, string weapon) =>
        player.PlayerPawn.Value?.WeaponServices?.MyWeapons.Any(w => w.Value?.DesignerName?.Trim().ToLowerInvariant() is { Length: > 0 } n
            && (n.StartsWith("weapon_", StringComparison.Ordinal) ? n : "weapon_" + n) == weapon) == true;
}
