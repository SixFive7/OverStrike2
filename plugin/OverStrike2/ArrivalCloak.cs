using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The spawn invisibility in the game; the rule is ArrivalPlan's (ArrivalPlan.cs). Every human who spawns on a team is hidden by the cloak (Cloak.cs,
/// CloakWhy.Arrival) - model, every weapon, wearables, shadow - in every mode, until no enemy has them spotted, their first attack, leaving their spawn, or
/// 10 s. "Spotted" is the game's own m_entitySpottedState, the radar's: the enemies' bits in m_bSpottedByMask, every other player's in free-for-all
/// (mp_teammates_are_enemies). Read in the cloak's look before its pass, which then clears it while the player is hidden, so the radar shows no dot for them
/// (the live lab, 2026-11-10: both fields cleared, at the pass after the entities' think). The bots' guard keeps the bots' damage off a hidden player until
/// their own first attack (BotGuard, CloakShield). The cfgs' mp_playerid keeps an enemy's name off the crosshair, hidden or not. Bots are never hidden by it.
/// The hides and how they ended are counted, and logged at each map's end, not one by one: Arms Race respawns all the time. os_arrival says where every life
/// stands.</summary>
sealed class ArrivalCloak(BasePlugin plugin, ILogger log, Cloak cloak)
{
    public const string Command = "os_arrival";

    readonly ArrivalPlan plan = new();
    readonly List<int> look = new();
    readonly List<int> hidden = new();
    readonly Dictionary<string, int> ended = new();
    int count;     // hides on this map
    ConVar? teammatesAreEnemies;

    public void Load()
    {
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        plugin.RegisterEventHandler<EventPlayerDeath>((ev, _) => { if (ev.Userid is { IsValid: true } p) plan.Ended(p.Slot); return HookResult.Continue; });
        plugin.RegisterEventHandler<EventPlayerDisconnect>((ev, _) => { if (ev.Userid is { IsValid: true } p) plan.Ended(p.Slot); return HookResult.Continue; });
        plugin.RegisterEventHandler<EventWeaponFire>((ev, _) => { if (ev.Userid is { IsValid: true } p) plan.Spend(p.Slot, ArrivalPlan.Attacked); return HookResult.Continue; });
        plugin.RegisterEventHandler<EventGrenadeThrown>((ev, _) => { if (ev.Userid is { IsValid: true } p) plan.Spend(p.Slot, ArrivalPlan.Attacked); return HookResult.Continue; });
        plugin.RegisterEventHandler<EventPlayerHurt>((ev, _) =>
        {
            if (ev.Attacker is { IsValid: true } a && ev.Userid is { IsValid: true } v && a.Slot != v.Slot) plan.Spend(a.Slot, ArrivalPlan.Hurt);
            return HookResult.Continue;
        });
        plugin.RegisterListener<Listeners.OnMapStart>(_ => { plan.MapStarted(); teammatesAreEnemies = null; });
        plugin.RegisterListener<Listeners.OnMapEnd>(() =>
        {
            if (count > 0)
                log.LogInformation("Over Strike 2 spawn invisibility: {Hides} spawn(s) hidden on this map - seen again because {Ended}", count,
                    string.Join(", ", ended.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
            count = 0;
            ended.Clear();
        });
        cloak.BeforePass += Pass;
        plugin.AddCommand(Command, "Over Strike 2 spawn invisibility: where every life stands (the console only)", OnCommand);
        log.LogInformation("Over Strike 2 spawn invisibility: a human is hidden from their spawn until no enemy sees them, their first attack, leaving the spawn or {Max} s, in every mode ({Command})",
            ArrivalPlan.MaxSeconds, Command);
    }

    static bool OnTeam(CCSPlayerController p) => p.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist;

    HookResult OnSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        if (ev.Userid is { IsValid: true, IsBot: false, IsHLTV: false } p && OnTeam(p))
        {
            plan.Spawned(p.Slot, Server.TickCount, Server.CurrentTime);
            if (cloak.Want(p.Slot, CloakWhy.Arrival, true, "they spawned", quiet: true)) count++;
        }
        return HookResult.Continue;
    }

    /// <summary>Before the cloak's pass, every tick: every life still hidden looked at - who has it spotted, where it stands, how long - then every player it hides
    /// whose hide has ended or whose life is over since.</summary>
    void Pass()
    {
        var now = Server.CurrentTime;
        var tick = Server.TickCount;
        teammatesAreEnemies ??= ConVar.Find("mp_teammates_are_enemies");
        var ffa = teammatesAreEnemies?.GetPrimitiveValue<bool>() == true;
        plan.Unspent(look);
        foreach (var slot in look)
        {
            if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } p || p.PlayerPawn.Value is not { IsValid: true } pawn
                || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE || pawn.AbsOrigin is not { } at) continue;
            var hides = plan.Hides(slot, tick, now, at.X, at.Y, at.Z, Seen(p, pawn, ffa));
            var hiding = (cloak.Why(slot) & CloakWhy.Arrival) != 0;
            if (hides && !hiding && cloak.Want(slot, CloakWhy.Arrival, true, "they spawned", quiet: true)) count++; // the pawn was not alive yet at the spawn's event
            else if (!hides && hiding) Seen(slot, plan.SpentBecause(slot) ?? ArrivalPlan.Unseen);
        }
        cloak.Hidden(CloakWhy.Arrival, hidden);
        foreach (var slot in hidden)
            if (!plan.Ready(slot)) Seen(slot, plan.SpentBecause(slot) ?? "their life ended");
    }

    void Seen(int slot, string because)
    {
        cloak.Want(slot, CloakWhy.Arrival, false, because, quiet: true);
        ended[because] = ended.GetValueOrDefault(because) + 1;
    }

    /// <summary>Whether an enemy has the player spotted in the game's state now: a bit of m_bSpottedByMask (one per slot) of a player on a team who is not on theirs -
    /// or, in free-for-all, of any other player.</summary>
    static bool Seen(CCSPlayerController p, CCSPlayerPawn pawn, bool ffa)
    {
        var mask = pawn.EntitySpottedState.SpottedByMask;
        for (var word = 0; word < mask.Length; word++)
        {
            var bits = mask[word];
            for (var bit = 0; bits != 0 && bit < 32; bit++, bits >>= 1)
            {
                if ((bits & 1) == 0) continue;
                if (Utilities.GetPlayerFromSlot(word * 32 + bit) is { IsValid: true } o && o.Slot != p.Slot && OnTeam(o) && (ffa || o.Team != p.Team)) return true;
            }
        }
        return false;
    }

    void OnCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return; // the server console and RCON only
        var now = Server.CurrentTime;
        var lines = new List<string> { $"{Command}: {count} hidden on this map; seen again because {(ended.Count == 0 ? "-" : string.Join(", ", ended.Select(kv => $"{kv.Key} {kv.Value}")))}" };
        foreach (var p in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false }))
            lines.Add($"  #{p.Slot} {p.PlayerName}: {plan.Describe(p.Slot, now)}");
        command.ReplyToCommand(string.Join("\n", lines));
    }
}
