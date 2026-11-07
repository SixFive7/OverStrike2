using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>Three of the fun page's effects that are no game convar (Fun.cs: Fun.Vampire, Fun.Ghost, Fun.NoRecoil); the grenade drops and catch-up,
/// the other two, are the grenades module's and the catch-up module's.
/// The settings module keeps their switches like convars (a player's change, its undo at the map's end and in the reset) and tells this
/// module of every switch (Switched, from its EffectChanged); this module does what they say, from game events. Nothing heavy inside an event: every write waits for the next frame, the way the HUD's work does, or
/// for the cloak's pass after the entities' think.
///
///   - Vampire: every player spawns with Fun.VampireHealth health while it is on, and the damage they deal heals them up to Fun.VampireCap
///     (Fun.Heal). Switched on, everybody alive gets the spawn health at once; switched off, nobody keeps more than 100.
///   - Ghost: a player who kills is hidden by the cloak (Cloak.cs) for Fun.GhostSeconds - the model, every weapon they carry, held or
///     holstered, the grenades included, any wearable and the shadows - and is seen again by themselves; a newer kill starts the time again.
///     Their death or respawn ends it, and so does switching Ghost off, for everybody at once; whatever they drop meanwhile is seen at once.
///   - No recoil (Laser guns' second half; the first is weapon_accuracy_nospread): after every shot the gun's recoil index goes back to
///     zero, so every shot of a spray is a first shot. CS2 has no recoil convar left (weapon_recoil_scale is gone on 1.41.9.x).
///
/// Writes only fields the game writes itself (health, max health, the recoil index; the cloak's are its own, Cloak.cs), each marked changed
/// for the network the way CounterStrikeSharp marks any field. The client harness cannot shoot, so it cannot make a
/// ghost; os_cloak hide makes the same hide to look at, and bots that kill each other make ghosts too.</summary>
sealed class Effects(BasePlugin plugin, ILogger log, Func<string, bool> on, Cloak cloak)
{
    readonly Dictionary<int, int> ghosts = new(); // slot -> the kill that made them a ghost: a newer kill takes over, and only its own timer ends it
    int kills;                                     // every ghost's kill counted, never reset - so no timer of an older kill, or life, ends a newer one
    bool vampire, ghost;                           // as last switched
    bool cutWaiting;                               // Vampire was switched off, and its cut waits for the next frame

    public void Load()
    {
        plugin.RegisterEventHandler<EventPlayerHurt>(OnHurt);
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
        plugin.RegisterEventHandler<EventWeaponFire>(OnFire);
        plugin.RegisterListener<Listeners.OnMapStart>(_ => ghosts.Clear()); // the cloak forgets the old map's too
        Switched(Fun.Vampire, on(Fun.Vampire));                          // what the switches already say when the plugin loads
        Switched(Fun.Ghost, on(Fun.Ghost));
        log.LogInformation("Over Strike 2 effects: Vampire ({Health} at spawn, up to {Cap}), Ghost ({Seconds} s after a kill), no recoil - off until a player switches one on",
            Fun.VampireHealth, Fun.VampireCap, Fun.GhostSeconds);
    }

    static IEnumerable<(CCSPlayerController Player, CCSPlayerPawn Pawn)> Alive() =>
        Utilities.GetPlayers().Where(p => p is { IsValid: true, PawnIsAlive: true }).Select(p => (p, p.PlayerPawn.Value)).Where(x => x.Item2 is { IsValid: true }).Select(x => (x.p, x.Item2!));

    /// <summary>An effect was switched (the settings module's EffectChanged): Vampire and Ghost act on everybody alive at once - the health
    /// on the next frame, like every write of this module. The other effects are other modules'.</summary>
    public void Switched(string effect, bool isOn)
    {
        if (effect.Equals(Fun.Vampire, StringComparison.OrdinalIgnoreCase) && isOn != vampire)
        {
            vampire = isOn;
            cutWaiting = !isOn;
            Server.NextFrame(() =>
            {
                cutWaiting = false;
                VampireHealth(isOn);
            });
            log.LogInformation("Over Strike 2 effects: Vampire {State}", isOn ? "on" : "off");
        }
        if (effect.Equals(Fun.Ghost, StringComparison.OrdinalIgnoreCase) && isOn != ghost)
        {
            var g = isOn;
            ghost = g;
            if (!g)
            {
                foreach (var slot in ghosts.Keys) cloak.Want(slot, CloakWhy.Ghost, false, "Ghost switched off");
                ghosts.Clear();
            }
            log.LogInformation("Over Strike 2 effects: Ghost {State}", g ? "on" : "off");
        }
    }

    /// <summary>Everybody alive gets Vampire's health and ceiling (on), or keeps no more than 100 (off).</summary>
    static void VampireHealth(bool isOn)
    {
        foreach (var (_, pawn) in Alive()) SetHealth(pawn, isOn ? Math.Max(pawn.Health, Fun.VampireHealth) : Math.Min(pawn.Health, 100), isOn ? Fun.VampireCap : 100);
    }

    /// <summary>The plugin unloads (GameSettings.Unload switched Vampire off with every other fun change): its cut is made now, since the frame
    /// it waits for may come after this instance has gone - nobody keeps Vampire's health past the instance that gave it.</summary>
    public void Unload()
    {
        if (!cutWaiting) return;
        cutWaiting = false;
        VampireHealth(false);
    }

    HookResult OnHurt(EventPlayerHurt ev, GameEventInfo info)
    {
        if (!vampire || ev.Attacker is not { IsValid: true } attacker || ev.Userid is not { IsValid: true } victim || attacker.Slot == victim.Slot) return HookResult.Continue;
        var (slot, damage) = (attacker.Slot, ev.DmgHealth);
        Server.NextFrame(() =>
        {
            if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true, PawnIsAlive: true } p && p.PlayerPawn.Value is { IsValid: true } pawn)
            {
                var healed = Fun.Heal(pawn.Health, damage);
                if (healed != pawn.Health) SetHealth(pawn, healed, Math.Max(pawn.MaxHealth, Fun.VampireCap)); // a catch-up boost may have raised it
            }
        });
        return HookResult.Continue;
    }

    HookResult OnSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        if (ev.Userid is not { IsValid: true } player) return HookResult.Continue;
        var slot = player.Slot;
        ghosts.Remove(slot); // a new life is seen: the cloak ends every reason of the old one itself
        if (!vampire) return HookResult.Continue;
        // The next frame: before the catch-up module applies its boost 0.1 s after the spawn, which is relative to the spawn health
        // - so a player behind gets the boost on top of the vampire's health.
        Server.NextFrame(() =>
        {
            if (vampire && Utilities.GetPlayerFromSlot(slot) is { IsValid: true, PawnIsAlive: true } p && p.PlayerPawn.Value is { IsValid: true } pawn)
                SetHealth(pawn, Math.Max(pawn.Health, Fun.VampireHealth), Math.Max(pawn.MaxHealth, Fun.VampireCap));
        });
        return HookResult.Continue;
    }

    /// <summary>A kill makes its killer a ghost, from this tick: the cloak's pass after the entities' think hides them before the next snapshot,
    /// with whatever gun Arms Race hands out for the kill. Only bookkeeping in here; a dead ghost's own ghost ends with the life.</summary>
    HookResult OnDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        if (ev.Userid is not { IsValid: true } dead) return HookResult.Continue;
        ghosts.Remove(dead.Slot);
        if (!ghost || ev.Attacker is not { IsValid: true } killer || killer.Slot == dead.Slot) return HookResult.Continue;
        var slot = killer.Slot;
        var kill = ++kills;
        if (!cloak.Want(slot, CloakWhy.Ghost, true, "a kill")) return HookResult.Continue; // the killer is dead too (a grenade from beyond)
        ghosts[slot] = kill;
        plugin.AddTimer((float)Fun.GhostSeconds, () =>
        {
            if (ghosts.GetValueOrDefault(slot) != kill) return; // a newer kill, a death, a respawn or Ghost switched off came first
            ghosts.Remove(slot);
            cloak.Want(slot, CloakWhy.Ghost, false, $"{Fun.GhostSeconds:0} s after the kill");
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    HookResult OnFire(EventWeaponFire ev, GameEventInfo info)
    {
        if (!on(Fun.NoRecoil) || ev.Userid is not { IsValid: true } shooter) return HookResult.Continue;
        var slot = shooter.Slot;
        Server.NextFrame(() =>
        {
            if (Utilities.GetPlayerFromSlot(slot)?.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value is not { IsValid: true } held) return;
            if (held.As<CCSWeaponBase>() is not { } gun) return;
            gun.FlRecoilIndex = 0;
            gun.IRecoilIndex = 0;
            Utilities.SetStateChanged(gun, "CCSWeaponBase", "m_flRecoilIndex");
            Utilities.SetStateChanged(gun, "CCSWeaponBase", "m_iRecoilIndex");
        });
        return HookResult.Continue;
    }

    static void SetHealth(CCSPlayerPawn pawn, int health, int max)
    {
        if (pawn.MaxHealth != max) { pawn.MaxHealth = max; Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iMaxHealth"); }
        if (pawn.Health != health) { pawn.Health = health; Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth"); }
    }
}
