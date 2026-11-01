using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The fun page's effects that are no game convar (Fun.cs: Fun.Vampire, Fun.Ghost, Fun.NoRecoil - the owner's #24 of 2026-11-01).
/// The settings module keeps their switches like convars (a player's change, its undo at the map's end and in the reset); this module
/// does what they say, from game events. Nothing heavy inside an event: every write waits for the next frame, the way the HUD's work does.
///
///   - Vampire: every player spawns with Fun.VampireHealth health while it is on, and the damage they deal heals them up to Fun.VampireCap
///     (Fun.Heal). Switched on, everybody alive gets the spawn health at once; switched off, nobody keeps more than 100.
///   - Ghost: a player who kills is invisible for Fun.GhostSeconds - their model and the gun in their hand rendered transparent - and comes
///     back by itself; switched off, everybody comes back at once.
///   - No recoil (Laser guns' second half; the first is weapon_accuracy_nospread): after every shot the gun's recoil index goes back to
///     zero, so every shot of a spray is a first shot. CS2 has no recoil convar left (weapon_recoil_scale is gone on 1.41.9.x).
///
/// Writes only fields the game writes itself (health, max health, the render colour and mode, the recoil index), each marked changed
/// for the network the way CounterStrikeSharp marks any field. What each does in a game is inferred, not yet seen: the client harness
/// cannot shoot (README "Fun settings").</summary>
sealed class Effects(BasePlugin plugin, ILogger log, Func<string, bool> on)
{
    readonly Dictionary<int, int> hidden = new(); // slot -> the kill count that made them a ghost (a newer kill extends it)
    bool vampire, ghost;                           // as of the last look, to act on a switch

    public void Load()
    {
        plugin.RegisterEventHandler<EventPlayerHurt>(OnHurt);
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
        plugin.RegisterEventHandler<EventWeaponFire>(OnFire);
        plugin.AddTimer(0.5f, Look, TimerFlags.REPEAT);
        log.LogInformation("Over Strike 2 effects: Vampire ({Health} at spawn, up to {Cap}), Ghost ({Seconds} s after a kill), no recoil - off until a player switches one on",
            Fun.VampireHealth, Fun.VampireCap, Fun.GhostSeconds);
    }

    static IEnumerable<(CCSPlayerController Player, CCSPlayerPawn Pawn)> Alive() =>
        Utilities.GetPlayers().Where(p => p is { IsValid: true, PawnIsAlive: true }).Select(p => (p, p.PlayerPawn.Value)).Where(x => x.Item2 is { IsValid: true }).Select(x => (x.p, x.Item2!));

    /// <summary>Twice a second: a switch of Vampire or Ghost acts on everybody alive at once.</summary>
    void Look()
    {
        var v = on(Fun.Vampire);
        if (v != vampire)
        {
            vampire = v;
            foreach (var (_, pawn) in Alive()) SetHealth(pawn, v ? Math.Max(pawn.Health, Fun.VampireHealth) : Math.Min(pawn.Health, 100), v ? Fun.VampireCap : 100);
            log.LogInformation("Over Strike 2 effects: Vampire {State}", v ? "on" : "off");
        }
        var g = on(Fun.Ghost);
        if (g != ghost)
        {
            ghost = g;
            if (!g) foreach (var slot in hidden.Keys.ToList()) Show(slot);
            log.LogInformation("Over Strike 2 effects: Ghost {State}", g ? "on" : "off");
        }
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
        hidden.Remove(slot); // a new life is seen
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

    HookResult OnDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        if (!ghost || ev.Attacker is not { IsValid: true } killer || ev.Userid is not { IsValid: true } dead || killer.Slot == dead.Slot) return HookResult.Continue;
        var slot = killer.Slot;
        var kill = hidden.GetValueOrDefault(slot) + 1;
        hidden[slot] = kill;
        Server.NextFrame(() => Hide(slot));
        plugin.AddTimer((float)Fun.GhostSeconds, () => { if (hidden.GetValueOrDefault(slot) == kill) Show(slot); }, TimerFlags.STOP_ON_MAPCHANGE);
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

    void Hide(int slot) => Paint(slot, 0);

    void Show(int slot)
    {
        hidden.Remove(slot);
        Paint(slot, 255);
    }

    /// <summary>The player's model and the gun in their hand at this alpha (0 invisible, 255 as normal).</summary>
    static void Paint(int slot, int alpha)
    {
        if (Utilities.GetPlayerFromSlot(slot)?.PlayerPawn.Value is not { IsValid: true } pawn) return;
        foreach (CBaseModelEntity e in new CBaseModelEntity?[] { pawn, pawn.WeaponServices?.ActiveWeapon.Value }.Where(x => x is { IsValid: true })!)
        {
            e.RenderMode = alpha < 255 ? RenderMode_t.kRenderTransAlpha : RenderMode_t.kRenderNormal;
            e.Render = Color.FromArgb(alpha, e.Render.R, e.Render.G, e.Render.B);
            Utilities.SetStateChanged(e, "CBaseModelEntity", "m_nRenderMode");
            Utilities.SetStateChanged(e, "CBaseModelEntity", "m_clrRender");
        }
    }
}
