using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>Keeps an HE grenade in play in Arms Race, which hands out only the progression weapon and ignores the grenade convars.
/// Every player gets one shortly after spawning; the engine's mp_death_drop_grenade (set to 2 in gamemode_armsrace_server.cfg) drops it
/// where they die. Nothing is created in the world by this plugin: spawning weapon entities directly segfaulted the server.
/// Toggled for everyone with !nades (or /nades, or css_nades in the console).
///
/// It also replaces the golden knife on the last level with that grenade (GUNGAME_GRENADE_WIN, default on), so the match is won by
/// blowing somebody up rather than by knifing them. See the "grenade to win" region for why that works and what backs it up.</summary>
sealed class Nades(BasePlugin plugin, ILogger log)
{
    /// <summary>GunGameNades 1.3.1 until 2026-11-01; a module of the Over Strike 2 plugin since, with the host's version.</summary>
    const string ModuleVersion = OverStrike2Plugin.Version;

    const string Grenade = ArmsRaceLoadout.Grenade;
    const float GiveDelay = 0.3f; // The mode equips the progression weapon right after spawn; hand out the grenade once that is done.

    bool enabled = true;

    /// <summary>The grenade drops as gg_nades left them, for the menu and the chat command.</summary>
    public bool Enabled => enabled;

    /// <summary>The grenade drops are Arms Race's (#27 c: players buy in Deathmatch and Wingman): off in another mode, whatever gg_nades says.</summary>
    public bool ModeAllows { get; set; } = true;

    public void Load(bool hotReload)
    {
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        plugin.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        plugin.AddTimer(SweepInterval, SweepLastLevel, TimerFlags.REPEAT);
        log.LogInformation("Over Strike 2 grenades {Version}: grenade on spawn, grenade-to-win {Win}{Knife}.",
            ModuleVersion, grenadeWin ? "on" : "off", grenadeWin && keepKnife ? " (announce only, golden knife kept)" : "");
    }

    HookResult OnPlayerSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        if (ev.Userid is not { IsValid: true } player) return HookResult.Continue;
        var slot = player.Slot; // Re-resolve by slot later: the controller may be gone by then.
        spawnedAt[slot] = Server.CurrentTime;
        if (!enabled || !ModeAllows) return HookResult.Continue;
        plugin.AddTimer(GiveDelay, () => GiveGrenade(Utilities.GetPlayerFromSlot(slot)), TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    static void GiveGrenade(CCSPlayerController? player)
    {
        if (player is not { IsValid: true, PawnIsAlive: true }) return;
        if (Carried(player).Contains(Grenade)) return; // Already carrying one (picked up from a body or not thrown yet).
        player.GiveNamedItem(Grenade);
    }

    /// <summary>The weapons a player is holding, as canonical designer names. MyWeapons is a vector of CHandle, which is
    /// the one element type CounterStrikeSharp's NetworkedVector can actually read, and this plugin has been reading it
    /// in production since 1.1.0.</summary>
    static List<string> Carried(CCSPlayerController player)
    {
        var carried = new List<string>();
        var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons;
        if (weapons is null) return carried;
        foreach (var w in weapons)
        {
            var name = ArmsRaceLoadout.Canonical(w.Value?.DesignerName);
            if (name.Length > 0) carried.Add(name);
        }
        return carried;
    }

    /// <summary>Grenade drops on or off for everyone. Since 2026-10-21 nobody types it (chat commands are gone): the HUD's Game page asks
    /// for it - GunGameWelcome runs it as the server, naming who clicked, and says so on the HUD's news line. A server command without the
    /// css_ prefix, so no chat trigger reaches it.</summary>
    [ConsoleCommand("gg_nades", "Grenade drops on death: gg_nades on|off [who]")]
    [CommandHelper(minArgs: 0, usage: "[on|off] [who]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnNadesCommand(CCSPlayerController? player, CommandInfo command)
    {
        var arg = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "";
        enabled = arg switch { "on" => true, "off" => false, _ => !enabled };
        if (enabled && ModeAllows) foreach (var p in Utilities.GetPlayers()) GiveGrenade(p); // Everyone alive gets one right away, not only on the next spawn.
        // Off means vanilla Arms Race, so the last level gets its knife back: a disarmed player with no grenade could never win.
        // The players stay on the lastLevel list, so switching back on takes their knives again without waiting for another level-up.
        else RestoreKnives();
        var who = command.ArgCount > 2 ? command.GetArg(2) : "Console";
        log.LogInformation("Over Strike 2 grenades: drops {State} (by {Who})", enabled ? "on" : "off", who);
        command.ReplyToCommand($"nades: grenade drops {(enabled ? "on" : "off")}");
    }

    #region grenade to win

    // Arms Race awards the match to the first player who gets a kill while standing on the last rung of the ladder, and the
    // check does not care which weapon made the kill. In CS:GO's source that is explicit: CCSPlayer::Event_KilledOther
    // increments m_iNumGunGameKillsWithCurrentWeapon for *any* enemy kill, and the IsPlayingGunGameProgressive() branch then
    // only asks IsFinalGunGameProgressiveWeapon(m_iGunGameProgressiveWeaponIndex) && kills == required — no weapon comparison
    // at all, unlike the gungametrbomb branch right above it, which does compare and says so in a comment. CS2's end-of-match
    // log on this server matches that code line for line (Terrorists_Win, score still 0:0, RoundsPlayed 0, Round_End then
    // Game Over in the same second), so the port kept the rule. Taking the knife away therefore makes the grenade the only
    // way to make that kill, and the engine still ends the match.
    //
    // That is an inference from CS:GO source plus CS2 log shape, not a live observation of a grenade kill on the last level,
    // so a watchdog ends the match if the engine does not, and gives the knives back if that fails too (see ArmWinWatchdog).
    //
    // Finding the last level needs no ladder and no schema read: the engine gives a respawning player the weapon for the rung
    // they are on, so the last rung is the only one on which somebody has no gun. ArmsRaceLoadout turns that into a verdict
    // from weapon names alone. The first version of this feature did read the ladder out of
    // CCSGameModeRules_ArmsRace.m_WeaponSequence, and it threw NotSupportedException twice a second on the live server:
    // CounterStrikeSharp's NetworkedVector indexer only supports CHandle<T>, so a vector of strings cannot be read at all.
    // The member existing was not proof it was readable. Nothing here reads anything that this plugin has not read in
    // production already, and the sweep is wrapped in a catch that switches the feature off rather than repeating a throw.

    const string EnvGrenadeWin = "GUNGAME_GRENADE_WIN";       // Master switch, default on.
    const string EnvKeepKnife = "GUNGAME_GRENADE_WIN_KEEP_KNIFE"; // Escape hatch: announce and hand out grenades, but leave the knife.

    const float SweepInterval = 0.5f; // How often everybody's loadout is looked at.
    const float RefillDelay = 3.0f;   // A disarmed last-level player who threw their grenade gets another one this long after running out.
    const float WinGrace = 2.0f;      // How long the engine gets to end the match after a last-level kill before the watchdog steps in.

    bool grenadeWin = ArmsRaceLoadout.EnvFlag(Environment.GetEnvironmentVariable(EnvGrenadeWin), true);
    readonly bool keepKnife = ArmsRaceLoadout.EnvFlag(Environment.GetEnvironmentVariable(EnvKeepKnife), false);

    bool armsRace;   // Re-checked once per match; the detector's reasoning only holds in gungameprogressive.
    bool modeChecked;

    readonly HashSet<int> lastLevel = [];  // Slots standing on the last rung.
    readonly HashSet<int> announced = [];  // Slots already announced this match.
    readonly HashSet<int> heldAGun = [];   // Slots seen carrying a progression weapon at some point this match.
    readonly Dictionary<int, float> spawnedAt = [];
    readonly Dictionary<int, float> gunlessSince = [];
    readonly Dictionary<int, float> grenadeGoneAt = [];
    int matchSeq;         // Bumped on every round start and round end, so a watchdog armed in one match can tell it outlived it.
    int watchdogSeq = -1; // The matchSeq a watchdog is already running for: one per match, however many kills that grenade got.

    HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
    {
        // Arms Race is one round per match and mp_match_end_restart replays the same map, so round start is the match boundary.
        matchSeq++;
        modeChecked = false;
        lastLevel.Clear();
        announced.Clear();
        heldAGun.Clear();
        gunlessSince.Clear();
        grenadeGoneAt.Clear();
        return HookResult.Continue;
    }

    HookResult OnRoundEnd(EventRoundEnd ev, GameEventInfo info)
    {
        matchSeq++; // Proof for the watchdog that the match ended without it.
        return HookResult.Continue;
    }

    HookResult OnPlayerDisconnect(EventPlayerDisconnect ev, GameEventInfo info)
    {
        if (ev.Userid is { IsValid: true } player) Forget(player.Slot);
        return HookResult.Continue;
    }

    void Forget(int slot)
    {
        lastLevel.Remove(slot);
        announced.Remove(slot);
        heldAGun.Remove(slot);
        spawnedAt.Remove(slot);
        gunlessSince.Remove(slot);
        grenadeGoneAt.Remove(slot);
    }

    static CCSGameRules? GameRules() =>
        Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;

    static int ConVarInt(string name) =>
        int.TryParse(ConVar.Find(name)?.StringValue, out var value) ? value : -1;

    /// <summary>Whether this is gungameprogressive. Outside it, "carrying no gun" means nothing like the last level — in a
    /// competitive eco round it is most of a team — so the feature must not act.</summary>
    bool ArmsRace()
    {
        if (modeChecked) return armsRace;
        modeChecked = true;
        int type = ConVarInt("game_type"), mode = ConVarInt("game_mode");
        armsRace = type == 1 && mode == 0;
        if (!armsRace)
            log.LogInformation("game_type {Type} / game_mode {Mode} is not Arms Race; grenade-to-win stays out of the way.", type, mode);
        return armsRace;
    }

    /// <summary>One throw is a bug; the same throw twice a second for a night is a mess. Anything unexpected in here switches
    /// the feature off, hands the knives back and is logged once.</summary>
    void SweepLastLevel()
    {
        if (!grenadeWin || !enabled) return;
        try
        {
            Sweep();
        }
        catch (Exception e)
        {
            grenadeWin = false;
            log.LogError(e, "Grenade-to-win threw while looking for the last level. Switching it off and handing the knives "
                + "back rather than repeating this every half second.");
            Server.PrintToChatAll("[Nades] Grenade-to-win hit an error and switched itself off - the golden knife is back.");
            try { RestoreKnives(); } catch (Exception inner) { log.LogError(inner, "...and the knives could not be handed back."); }
        }
    }

    /// <summary>Finds everybody on the last level, tells the server, takes their knife and keeps a grenade in their hands.</summary>
    void Sweep()
    {
        if (!ArmsRace()) return;
        // Warmup equips players on its own schedule and nobody can win during it, so there is nothing to gain by acting.
        if (GameRules() is not { WarmupPeriod: false }) return;
        var now = Server.CurrentTime;
        foreach (var player in Utilities.GetPlayers())
        {
            if (player is not { IsValid: true, PawnIsAlive: true }) continue;
            if (player.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist)) continue;
            var slot = player.Slot;
            var carried = Carried(player);

            if (ArmsRaceLoadout.HasGun(carried)) { heldAGun.Add(slot); gunlessSince.Remove(slot); }
            else if (!gunlessSince.ContainsKey(slot)) gunlessSince[slot] = now;

            var sinceSpawn = spawnedAt.TryGetValue(slot, out var spawned) ? now - spawned : float.MaxValue;
            var gunlessFor = gunlessSince.TryGetValue(slot, out var gunless) ? now - gunless : 0f;
            var verdict = ArmsRaceLoadout.Classify(carried, heldAGun.Contains(slot), sinceSpawn, gunlessFor);

            if (verdict == ArmsRaceLoadout.Verdict.HoldingAGun)
            {
                // Demoted off the last rung, or holding a gun again some other way: give the melee weapon back.
                if (lastLevel.Remove(slot) && !keepKnife) GiveKnife(player);
                announced.Remove(slot);
                grenadeGoneAt.Remove(slot);
                continue;
            }
            // Already known to be on the last level: keep them disarmed and armed even inside the spawn grace, because the
            // engine hands the knife back on every respawn. Otherwise wait for the verdict to be certain.
            if (verdict == ArmsRaceLoadout.Verdict.TooSoonToTell && !lastLevel.Contains(slot)) continue;

            if (lastLevel.Add(slot) && !carried.Contains(Grenade)) player.GiveNamedItem(Grenade); // Never leave them with nothing.
            Announce(player);
            Disarm(player, carried);
            Rearm(player, carried, slot, now);
        }
    }

    void Announce(CCSPlayerController player)
    {
        if (!announced.Add(player.Slot)) return;
        Server.PrintToChatAll($"[Nades] {player.PlayerName} is on the last level - grenade to win");
    }

    /// <summary>Removes the melee weapon so the only kill left is a grenade kill. By slot first, because CS2 appears to hand
    /// the golden knife out as the ordinary team knife (a kill with it logs as knife_t) and a knife skin has its own designer
    /// name; then by name, in case the weapon's VData carried no gear slot. Both of CounterStrikeSharp's removers work by
    /// queueing an entity IO "Kill" on the weapon, so nothing is created in the world.</summary>
    void Disarm(CCSPlayerController player, List<string> carried)
    {
        if (keepKnife) return;
        Utilities.RemoveItemBySlot(player, gear_slot_t.GEAR_SLOT_KNIFE);
        foreach (var w in carried)
            if (ArmsRaceLoadout.IsKnife(w)) Utilities.RemoveItemByDesignerName(player, w);
    }

    /// <summary>A disarmed player who threw their grenade has nothing at all, so hand them another one shortly after.</summary>
    void Rearm(CCSPlayerController player, List<string> carried, int slot, float now)
    {
        if (keepKnife) return;
        if (carried.Contains(Grenade)) { grenadeGoneAt.Remove(slot); return; }
        if (!grenadeGoneAt.TryGetValue(slot, out var gone)) { grenadeGoneAt[slot] = now; return; }
        if (now - gone < RefillDelay) return;
        grenadeGoneAt.Remove(slot);
        player.GiveNamedItem(Grenade);
    }

    HookResult OnPlayerDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        if (!grenadeWin || !enabled || keepKnife) return HookResult.Continue; // With the knife still in play the engine's own win path is intact.
        if (ev.Attacker is not { IsValid: true } killer || ev.Userid is not { IsValid: true } victim) return HookResult.Continue;
        if (killer.Slot == victim.Slot || killer.Team == victim.Team) return HookResult.Continue;
        if (!lastLevel.Contains(killer.Slot)) return HookResult.Continue;
        ArmWinWatchdog(killer);
        return HookResult.Continue;
    }

    /// <summary>The fail-safe. A kill by a disarmed last-level player must end the match; if the engine does not do it, this
    /// does, and if that does not work either the feature switches itself off and hands the knives back, so a match can
    /// never become unwinnable. Every step is announced in chat and logged, so the first live win says which one ran.</summary>
    void ArmWinWatchdog(CCSPlayerController killer)
    {
        if (watchdogSeq == matchSeq) return; // One grenade can kill two people; the match only needs ending once.
        watchdogSeq = matchSeq;
        var armed = matchSeq;
        var name = killer.PlayerName;
        var team = killer.Team;
        plugin.AddTimer(WinGrace, () =>
        {
            if (matchSeq != armed) return; // The engine ended the match on that kill, which is what Arms Race is expected to do.
            log.LogWarning("{Name} got a kill on the last level and the engine did not end the match; ending it here.", name);
            Server.PrintToChatAll($"[Nades] {name} wins with a grenade.");
            GameRules()?.TerminateRound(0.0f, team == CsTeam.CounterTerrorist ? RoundEndReason.CTsWin : RoundEndReason.TerroristsWin);
            plugin.AddTimer(WinGrace, () => { if (matchSeq == armed) GiveUp(); }, TimerFlags.STOP_ON_MAPCHANGE);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>Neither the engine nor TerminateRound ended the match: stop taking knives for the rest of this server's life,
    /// hand back the ones already taken, and say so. Vanilla Arms Race is winnable, which beats a stalemate.</summary>
    void GiveUp()
    {
        grenadeWin = false;
        log.LogError("A last-level kill ended neither by itself nor through TerminateRound. Grenade-to-win is off for this "
            + "session and the knives are back. Set {Env}=0 in the compose file to keep it that way.", EnvGrenadeWin);
        Server.PrintToChatAll("[Nades] Grenade-to-win could not end the match - the golden knife is back.");
        RestoreKnives();
    }

    void RestoreKnives()
    {
        foreach (var slot in lastLevel)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true, PawnIsAlive: true }) continue;
            if (Carried(player).Exists(ArmsRaceLoadout.IsKnife)) continue;
            GiveKnife(player);
        }
    }

    static void GiveKnife(CCSPlayerController player) =>
        player.GiveNamedItem(player.Team == CsTeam.Terrorist ? "weapon_knife_t" : "weapon_knife");

    #endregion
}
