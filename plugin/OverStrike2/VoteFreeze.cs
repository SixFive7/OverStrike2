using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The vote's freeze, in the game: while a vote is open, and while its result counts down to a map change (VoteState.Freeze, as the
/// HUD's line to the bridge last read it), nobody moves, shoots or takes damage - bots too. FreezePlan decides when; this does it, ticked by
/// the plugin every server tick:
///
///   - moving: every living pawn's m_MoveType and m_nActualMoveType (what the movement code goes by) are MOVETYPE_OBSOLETE, a type it does
///     not move, and both are marked for the network. Each is written through its own one-byte type: CounterStrikeSharp's
///     Schema.SetSchemaValue with an int would write four bytes there, over the three fields after it. A pawn that spawns meanwhile (a player
///     who joins, a respawn) is frozen at the next tick; the type it had before (walking, a ladder, noclip) comes back when everybody is freed;
///   - shooting: the next primary and secondary attack tick of every weapon a pawn carries stays HoldTicks ahead of the server's tick - a bot
///     cannot fire either - and is put back to the current tick when everybody is freed;
///   - damage: the player damage hook (Listeners.OnPlayerTakeDamagePre) answers Handled, so no hit, fall, fire or grenade thrown before the
///     freeze hurts anybody. Registered before the catch-up module's own hook, so the freeze has the first word.
///
/// Everybody is freed at the same tick. The freeze and the freeing are logged once each, with why.</summary>
sealed class VoteFreeze(BasePlugin plugin, ILogger log, Func<HudScreen?> screen)
{
    const int HoldTicks = 64;       // a weapon's next attack is kept this far ahead: one second at CS2's 64 ticks a second
    const int PushBelowTicks = 32;  // ...and pushed again once it is less than this far ahead: one network update a weapon every half second
    const int WeaponEvery = 4;      // the weapons are looked at every 4th tick (and at once for a pawn just frozen)

    readonly FreezePlan plan = new();
    readonly Dictionary<int, (uint Pawn, MoveType_t Was)> frozen = new(); // slot -> the pawn frozen, and how it moved before
    readonly HashSet<uint> held = new();                                  // the weapons whose next attack was pushed ahead
    readonly Dictionary<string, bool> networked = new();                  // "class.field" -> whether the field is networked (looked up once)
    DateTime mapStartedAt = DateTime.MinValue;
    int pawns;                                                            // pawns frozen in this freeze, for the log

    /// <summary>Everybody is frozen now.</summary>
    public bool Frozen => plan.Frozen;

    public void Load()
    {
        plugin.RegisterListener<Listeners.OnPlayerTakeDamagePre>(OnTakeDamage);
        plugin.RegisterListener<Listeners.OnMapStart>(_ => { mapStartedAt = DateTime.UtcNow; frozen.Clear(); held.Clear(); }); // the pawns go with the map
    }

    /// <summary>The plugin unloads (a reload, a new build): nobody may stay frozen by an instance that is gone.</summary>
    public void Unload()
    {
        if (!plan.Frozen && frozen.Count == 0) return;
        var freed = FreeEverybody();
        log.LogInformation("Over Strike 2 freeze: everybody free - the plugin unloads ({Freed} pawn(s) freed)", freed);
    }

    /// <summary>Every server tick, from the plugin.</summary>
    public void Tick()
    {
        var hud = screen();
        var vote = hud?.Vote;
        if (plan.Tick(vote?.Round ?? 0, vote?.Freeze == true, hud?.AnsweredAt ?? DateTime.MinValue, mapStartedAt, DateTime.UtcNow))
        {
            if (plan.Frozen)
            {
                pawns = 0;
                log.LogInformation("Over Strike 2 freeze: everybody frozen - vote round {Round} {State}", plan.Round, vote?.Open == true ? "is open" : "has a result counting down to a map change");
            }
            else
            {
                var freed = FreeEverybody();
                log.LogInformation("Over Strike 2 freeze: everybody free - {Why} ({Freed} of {Pawns} frozen pawn(s) freed, {Seconds:0} s after the freeze began)",
                    plan.FreeBecause(), freed, pawns, (DateTime.UtcNow - plan.Since).TotalSeconds);
            }
        }
        if (plan.Frozen) HoldEverybody();
    }

    /// <summary>Damage while everybody is frozen: none.</summary>
    HookResult OnTakeDamage(CCSPlayerPawn pawn, CTakeDamageInfo info) => plan.Frozen ? HookResult.Handled : HookResult.Continue;

    void HoldEverybody()
    {
        var tick = Server.TickCount;
        var weapons = tick % WeaponEvery == 0;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is not { IsValid: true, IsHLTV: false, PawnIsAlive: true } || p.PlayerPawn.Value is not { IsValid: true } pawn) continue;
            var fresh = !frozen.TryGetValue(p.Slot, out var f) || f.Pawn != pawn.Index;
            if (fresh || pawn.MoveType != MoveType_t.MOVETYPE_OBSOLETE || pawn.ActualMoveType != MoveType_t.MOVETYPE_OBSOLETE)
            {
                var was = !fresh && pawn.MoveType == MoveType_t.MOVETYPE_OBSOLETE ? f.Was : pawn.MoveType; // a respawn walks again: that is how it moves now
                Move(pawn, MoveType_t.MOVETYPE_OBSOLETE);
                frozen[p.Slot] = (pawn.Index, was);
                if (fresh) pawns++;
                HoldFire(pawn, tick);
            }
            else if (weapons) HoldFire(pawn, tick);
        }
    }

    /// <summary>Everybody free, at once: each pawn still frozen moves as before, and its weapons may fire now. Returns how many pawns.</summary>
    int FreeEverybody()
    {
        var tick = Server.TickCount;
        var freed = 0;
        foreach (var (slot, f) in frozen)
        {
            if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } p || p.PlayerPawn.Value is not { IsValid: true } pawn || pawn.Index != f.Pawn) continue;
            if (pawn.MoveType == MoveType_t.MOVETYPE_OBSOLETE || pawn.ActualMoveType == MoveType_t.MOVETYPE_OBSOLETE)
            {
                Move(pawn, f.Was is MoveType_t.MOVETYPE_NONE or MoveType_t.MOVETYPE_OBSOLETE or MoveType_t.MOVETYPE_OBSERVER ? MoveType_t.MOVETYPE_WALK : f.Was);
                freed++;
            }
            ReleaseFire(pawn, tick);
        }
        frozen.Clear();
        held.Clear();
        return freed;
    }

    void Move(CCSPlayerPawn pawn, MoveType_t type)
    {
        pawn.MoveType = type;
        pawn.ActualMoveType = type;
        Changed(pawn, "CBaseEntity", "m_MoveType");
        Changed(pawn, "CBaseEntity", "m_nActualMoveType");
    }

    /// <summary>Every weapon the pawn carries: its next attack at least PushBelowTicks ahead, pushed to HoldTicks ahead when it gets closer.</summary>
    void HoldFire(CCSPlayerPawn pawn, int tick)
    {
        if (pawn.WeaponServices?.MyWeapons is not { } carried) return;
        foreach (var handle in carried)
        {
            if (handle.Value is not { IsValid: true } weapon) continue;
            held.Add(weapon.Index);
            if (weapon.NextPrimaryAttackTick < tick + PushBelowTicks)
            {
                weapon.NextPrimaryAttackTick = tick + HoldTicks;
                Changed(weapon, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
            }
            if (weapon.NextSecondaryAttackTick < tick + PushBelowTicks)
            {
                weapon.NextSecondaryAttackTick = tick + HoldTicks;
                Changed(weapon, "CBasePlayerWeapon", "m_nNextSecondaryAttackTick");
            }
        }
    }

    /// <summary>The weapons this freeze held: they may fire from this tick on. A weapon whose own next attack lies beyond the hold keeps it.</summary>
    void ReleaseFire(CCSPlayerPawn pawn, int tick)
    {
        if (pawn.WeaponServices?.MyWeapons is not { } carried) return;
        foreach (var handle in carried)
        {
            if (handle.Value is not { IsValid: true } weapon || !held.Contains(weapon.Index)) continue;
            if (weapon.NextPrimaryAttackTick > tick && weapon.NextPrimaryAttackTick <= tick + HoldTicks)
            {
                weapon.NextPrimaryAttackTick = tick;
                Changed(weapon, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
            }
            if (weapon.NextSecondaryAttackTick > tick && weapon.NextSecondaryAttackTick <= tick + HoldTicks)
            {
                weapon.NextSecondaryAttackTick = tick;
                Changed(weapon, "CBasePlayerWeapon", "m_nNextSecondaryAttackTick");
            }
        }
    }

    /// <summary>A field written here goes to the clients at the next snapshot - when the engine networks it at all, which is looked up once
    /// per field (Utilities.SetStateChanged logs a warning for every call on a field that is not).</summary>
    void Changed(CBaseEntity entity, string className, string field)
    {
        var key = className + "." + field;
        if (!networked.TryGetValue(key, out var yes))
        {
            try { yes = Schema.IsSchemaFieldNetworked(className, field); } catch (Exception) { yes = false; }
            networked[key] = yes;
            log.LogInformation("Over Strike 2 freeze: {Field} {Networked}", key, yes ? "is networked: marked for the network at every write" : "is not networked: written on the server only");
        }
        if (yes) Utilities.SetStateChanged(entity, className, field);
    }
}
