// The catch-up module's game half: it hands the Arms Race events to the rules (CatchUpRules.cs) and applies what they decide. It is
// not a plugin: a host plugin constructs it in its Load and everything registers through that host.
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace CatchUp;

/// <summary>Arms Race catch-up: players behind the leader get a boost, the leader a bounty, and each of them a private centre-screen line
/// at spawn saying what and why. With RecalculateOnKill every living player's boost is decided again a frame after each change to the
/// ladder, silently, by the mid-life rules (Rules.Recalculate). Construct it in the host plugin's Load and call Load(hotReload); the
/// host's own unload removes everything it registered. Nothing heavy runs inside a player's own callback (spawn, hit, kill, shot): those
/// only count and queue, and the work is done a frame later or on a timer, so a player is never dropped for overflow.</summary>
public sealed class CatchUpModule
{
    /// <summary>The server command (console and RCON only): status, reload, on, off, show, give.</summary>
    public const string Command = "gg_catchup";

    sealed class Life
    {
        public int Serial;
        public ulong Id;
        public uint Pawn;
        public Boost Boost = Boost.None;
        public double SpawnedAt, ProtectedUntil, LastHurtAt, RegenCarry;
        public bool Attacked, SecondChanceUsed;
        public int BaseHealth, MaxHealth;
        public double Health; // the health this life holds, a fraction of BaseHealth: never lowered within the life
        public bool Given;    // a boost given from the console: kept until death, not decided again
    }

    readonly BasePlugin plugin;
    readonly ILogger log;
    readonly string configPath;
    CatchUpConfig config = new();
    readonly Ladder ladder;
    readonly Dictionary<int, Life> lives = new();
    readonly Dictionary<int, (int Base, int Set)> healthSet = new(); // what this module set a slot's pawn to: undone at an unboosted spawn
    readonly HashSet<int> fresh = new();                            // joined, not yet spawned on a team
    readonly Dictionary<int, double> taglineUntil = new();
    readonly HashSet<int> boostedLastLife = new();
    bool switchedOn = true;
    bool recalculation;  // a recalculation is queued for the next frame
    bool stopped;        // too many errors in a short time: off until 'gg_catchup on' or a reload
    int faults;          // errors in the current window
    DateTime faultWindow;
    int serial;
    float tickSeconds;
    double nextCvarRead;
    bool ffa, headshotsOnly;
    double gameProtection = 5;
    CCSGameRulesProxy? rules;

    const int FaultsToStop = 20;           // errors within FaultWindowMinutes that switch the effects off
    const double FaultWindowMinutes = 10;

    public CatchUpModule(BasePlugin plugin, ILogger log, string configPath)
    {
        this.plugin = plugin;
        this.log = log;
        this.configPath = configPath;
        ladder = new Ladder(config.Progress);
    }

    /// <summary>Replaces the centre panel as the way the message is shown: the player, the packed lines, the panel's HTML and the
    /// seconds it should stay. Unset: CCSPlayerController.PrintToCenterHtml.</summary>
    public Action<CCSPlayerController, string[], string, int>? Show { get; set; }

    bool Active => config.Enabled && switchedOn && !stopped;

    /// <summary>Whether the boosts run now (the Fun page lights it; content v4_5_0).</summary>
    public bool On => Active;

    /// <summary>The Fun page's and chat's switch (the owner's 2026-11-03): what 'gg_catchup on|off' does, until the map changes - the mode's own
    /// switch at the next map start puts it back on in Arms Race.</summary>
    public void Switch(bool on)
    {
        switchedOn = on;
        if (on) { stopped = false; faults = 0; }
        log.LogInformation("CatchUp: switched {State} from the Fun page or chat", on ? "on" : "off");
    }

    public void Load(bool hotReload)
    {
        var summary = ReadConfig();
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
        plugin.RegisterEventHandler<EventPlayerHurt>(OnHurt);
        plugin.RegisterEventHandler<EventWeaponFire>(OnFire);
        plugin.RegisterEventHandler<EventPlayerTeam>(OnTeam);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect);
        plugin.RegisterListener<Listeners.OnClientPutInServer>(OnPutInServer);
        plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        plugin.RegisterListener<Listeners.OnPlayerTakeDamagePre>(OnTakeDamage);
        tickSeconds = (float)config.Effects.RegenTickSeconds;
        plugin.AddTimer(tickSeconds, Tick, TimerFlags.REPEAT);
        plugin.AddCommand(Command, "Arms Race catch-up: gg_catchup [status | reload | on | off | show <player> <behind | lead N> | give <player> <behind | lead N>]", OnCommand);
        WarmUp();
        log.LogInformation("CatchUp: {Summary}; {State}", summary,
            hotReload ? "loaded during a map: counting starts at the next match" : "counting starts at the first match");
    }

    /// <summary>Optional: the host has shown its own centre-screen line (the join tagline) to this slot for that long. The slot's next
    /// message then waits until it is gone. Without it, the first spawn after a join waits Message.FirstSpawnDelaySeconds.</summary>
    public void TaglineShown(int slot, double seconds) => taglineUntil[slot] = Server.CurrentTime + Math.Max(0, seconds);

    /// <summary>The boost of a slot's current life, for a host that wants to show it elsewhere.</summary>
    public Boost BoostOf(int slot) => lives.TryGetValue(slot, out var life) ? life.Boost : Boost.None;

    // ---- the match ----

    HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
    {
        try
        {
            if (Warmup())
            {
                ladder.Stop(); // warmup kills count for nothing
                return HookResult.Continue;
            }
            // Arms Race is one round per match and mp_match_end_restart plays the next match on the same map: round start is the start.
            var present = Players();
            ladder.StartMatch(present.Select(IdOf));
            boostedLastLife.Clear();
            log.LogInformation("CatchUp: match start, {Count} players counted from here", present.Count);
        }
        catch (Exception e) { Fault("at a round start", e); }
        return HookResult.Continue;
    }

    void OnMapStart(string map)
    {
        ladder.Stop();
        lives.Clear();
        healthSet.Clear();
        fresh.Clear();
        taglineUntil.Clear();
        boostedLastLife.Clear();
        recalculation = false; // a frame queued before the map change may never come
        rules = null;
    }

    void OnPutInServer(int slot)
    {
        fresh.Add(slot);
        healthSet.Remove(slot);
    }

    HookResult OnDisconnect(EventPlayerDisconnect ev, GameEventInfo info)
    {
        if (ev.Userid is not { IsValid: true } player) return HookResult.Continue;
        var slot = player.Slot;
        ladder.Leave(IdOf(player)); // CS2 starts a returning player at the bottom; if it does not, their next gun kill places them
        lives.Remove(slot);
        healthSet.Remove(slot);
        fresh.Remove(slot);
        taglineUntil.Remove(slot);
        boostedLastLife.Remove(slot);
        QueueRecalculation(); // the standings lose a player: the leader may be gone
        return HookResult.Continue;
    }

    HookResult OnTeam(EventPlayerTeam ev, GameEventInfo info)
    {
        if (ev.Disconnect || ev.Userid is not { IsValid: true } player || !ladder.Tracking) return HookResult.Continue;
        if (ev.Oldteam is (int)CsTeam.Terrorist or (int)CsTeam.CounterTerrorist && ev.Team != ev.Oldteam) ladder.Untrust(IdOf(player));
        QueueRecalculation(); // who plays, and who is whose teammate, may have changed
        return HookResult.Continue;
    }

    // ---- kills: the ladder ----

    HookResult OnDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        try
        {
            if (ev.Userid is not { IsValid: true } victim) return HookResult.Continue;
            lives.Remove(victim.Slot); // the life is over, its boost with it
            if (!ladder.Tracking || !Playing(victim)) return HookResult.Continue;
            var attacker = ev.Attacker;
            string kill;
            if (attacker is { IsValid: true } && attacker.Slot != victim.Slot && Playing(attacker))
            {
                string? line = null, note;
                if (Enemies(attacker, victim)) note = ladder.Kill(IdOf(attacker), IdOf(victim), ev.Weapon, CarriesGun(attacker));
                else (line, note) = ladder.TeamKill(IdOf(attacker), IdOf(victim), ev.Weapon); // friendly fire's team kill: Arms Race takes the killer down a level
                if (line != null || note != null)
                {
                    var name = attacker.PlayerName;
                    Server.NextFrame(() =>
                    {
                        if (line != null) log.LogInformation("CatchUp: {Name} {Note}", name, line);
                        if (note != null) log.LogInformation("CatchUp: {Name} {Note}", name, note);
                    });
                }
                kill = $"{attacker.PlayerName} ({TeamName(attacker)}, {Where(attacker)}) killed {victim.PlayerName} ({TeamName(victim)}, {Where(victim)}) with {Weapons.Name(ev.Weapon)}"
                    + (Enemies(attacker, victim) ? "" : " - a team kill");
            }
            else
            {
                ladder.Suicide(IdOf(victim));
                kill = $"{victim.PlayerName} ({TeamName(victim)}, {Where(victim)}) died without an enemy ({Weapons.Name(ev.Weapon)})";
            }
            Server.NextFrame(() => log.LogInformation("CatchUp: kill: {Kill}", kill)); // every kill, with the ladder after it, for the next look at the counts
            QueueRecalculation(); // the ladder changed: every living player's boost again, a frame later
        }
        catch (Exception e) { Fault("counting a kill", e); }
        return HookResult.Continue;
    }

    static string TeamName(CCSPlayerController p) => p.Team switch { CsTeam.Terrorist => "T", CsTeam.CounterTerrorist => "CT", var t => t.ToString() };

    /// <summary>A player's place on the ladder as the counter has it, after the kill: "level 7 + 1 = 15" (kills), and whether it followed them from the start.</summary>
    string Where(CCSPlayerController p)
    {
        var (level, kills, trusted) = ladder.State(IdOf(p));
        return $"level {level} + {kills} = {level * config.Progress.KillsPerLevel + kills}{(trusted ? "" : ", placed")}";
    }

    /// <summary>Whether a player carries a gun. Nobody does on the last level, the golden knife. A dead attacker (a grenade thrown
    /// before dying) counts as carrying one.</summary>
    static bool CarriesGun(CCSPlayerController player)
    {
        if (!player.PawnIsAlive) return true;
        var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons;
        if (weapons is null) return true;
        foreach (var w in weapons)
            if (Weapons.Kind(w.Value?.DesignerName) == WeaponKind.Gun) return true;
        return false;
    }

    // ---- spawns: the decision, the health, the message ----

    HookResult OnSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        if (ev.Userid is not { IsValid: true } player) return HookResult.Continue;
        var slot = player.Slot;
        lives.Remove(slot);
        if (!Playing(player)) return HookResult.Continue;
        var s = ++serial;
        var at = Server.CurrentTime;
        var first = fresh.Remove(slot);
        // not inside the player's own spawn: the game sets health and loadout first, and a spawn callback must stay light
        plugin.AddTimer((float)config.Effects.ApplyDelaySeconds, () => Apply(slot, s, at, first), TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    void Apply(int slot, int s, double spawnedAt, bool first)
    {
        try
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true, PawnIsAlive: true } || !Playing(player)) return;
            if (player.PlayerPawn.Value is not { IsValid: true } pawn) return;
            var now = Server.CurrentTime;
            var id = IdOf(player);

            // The base is the health this life spawned with - the game's 100, or a fun setting's own (Vampire's 1,000) - never the maximum:
            // Vampire raises that to its 2,000 cap, and a boost of it gave 2,300 to 2,900 at a spawn (the analysis of 2026-11-03).
            // The pawn lives on across respawns: if it still holds what this module set last life, the base is the one under it.
            var baseHealth = Math.Max(1, pawn.Health);
            var stale = healthSet.TryGetValue(slot, out var set) && pawn.MaxHealth == set.Set;
            if (stale) baseHealth = set.Base;
            healthSet.Remove(slot);

            Standings? standings = null;
            var boost = Boost.None;
            if (Active && ladder.Tracking && !Warmup())
            {
                var present = Players();
                standings = ladder.Standings(present.Select(IdOf));
                var leaderId = standings.Leader;
                var leader = leaderId == 0 ? null : present.FirstOrDefault(p => IdOf(p) == leaderId);
                boost = Rules.Decide(config, standings, id, leader != null && leader.Slot != player.Slot && !Enemies(player, leader)); // the leader's teammate: no bounty in the cap
            }

            var spawnHealth = baseHealth;
            if (boost.IsBoost && boost.Health > 0)
            {
                spawnHealth = Rules.SpawnHealth(baseHealth, boost);
                SetHealth(pawn, spawnHealth, spawnHealth);
                healthSet[slot] = (baseHealth, spawnHealth);
            }
            else if (stale) SetHealth(pawn, baseHealth, baseHealth);

            lives[slot] = new Life
            {
                Serial = s, Id = id, Pawn = pawn.Index, Boost = boost, SpawnedAt = spawnedAt, LastHurtAt = now,
                ProtectedUntil = Rules.ProtectedUntil(spawnedAt, gameProtection, boost),
                BaseHealth = baseHealth, MaxHealth = Math.Max(spawnHealth, pawn.MaxHealth),
                Health = boost.IsBoost && boost.Health > 0 ? boost.Health : 0,
            };

            if (standings != null)
            {
                if (boost.IsBoost || boost.IsBounty)
                {
                    boostedLastLife.Add(slot);
                    log.LogInformation("CatchUp: {Line}", Words.Decision(player.PlayerName, boost, standings, LeaderName(standings), baseHealth, spawnHealth));
                }
                else if (boostedLastLife.Remove(slot))
                    log.LogInformation("CatchUp: {Line}", Words.Decision(player.PlayerName, boost, standings, LeaderName(standings), baseHealth, spawnHealth));
            }

            var lines = Lines(boost);
            if (lines.Length == 0 || player.IsBot) return;
            double? taglineLeft = taglineUntil.TryGetValue(slot, out var until) ? until - now : null;
            var wait = Rules.MessageDelay(config.Message, first, taglineLeft) - (now - spawnedAt);
            plugin.AddTimer((float)Math.Max(0.01, wait), () => ShowMessage(slot, s, lines, boost), TimerFlags.STOP_ON_MAPCHANGE);
        }
        catch (Exception e) { Fault("applying a spawn", e); }
    }

    string[] Lines(Boost boost) =>
        (boost.IsBounty && config.Message.ToLeader) || (boost.IsBoost && config.Message.ToBoosted) ? Words.Lines(boost, config.Message) : [];

    void ShowMessage(int slot, int s, string[] lines, Boost boost)
    {
        if (!lives.TryGetValue(slot, out var life) || life.Serial != s) return; // died or respawned since: that spawn says its own
        if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true, IsBot: false } player) return;
        var html = Words.Html(lines, boost);
        if (Show != null) Show(player, lines, html, config.Message.Seconds);
        else player.PrintToCenterHtml(html, config.Message.Seconds);
    }

    static void SetHealth(CCSPlayerPawn pawn, int health, int max)
    {
        pawn.MaxHealth = max;
        pawn.Health = health;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iMaxHealth");
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }

    // ---- the ladder changed: every living player's boost again (RecalculateOnKill) ----

    /// <summary>Once per frame however many kills it had (a grenade can take several): the work is done the next frame, never inside the
    /// death event.</summary>
    void QueueRecalculation()
    {
        if (recalculation || !config.RecalculateOnKill) return;
        recalculation = true;
        Server.NextFrame(Recalculate);
    }

    /// <summary>Decides every living player's boost again by the standings now, and applies it to the life by the mid-life rules: damage,
    /// resistance, lifesteal, regeneration, the second chance (still once a life), infinite ammo, the one-hit knife and Zeus and the
    /// bounty follow the new decision both ways; health only goes up, as a higher maximum - the current health is never touched; the spawn
    /// protection and the message stay the spawn's. One log line when a player's tier or bounty step changes, none otherwise.</summary>
    void Recalculate()
    {
        recalculation = false;
        try
        {
            if (!Active || !config.RecalculateOnKill || !ladder.Tracking || lives.Count == 0 || Warmup()) return;
            var present = Players();
            var standings = ladder.Standings(present.Select(IdOf));
            var leader = standings.Leader == 0 ? null : present.FirstOrDefault(p => IdOf(p) == standings.Leader);
            foreach (var (slot, life) in lives)
            {
                if (life.Given) continue;
                var player = present.FirstOrDefault(p => p.Slot == slot);
                if (player is not { PawnIsAlive: true } || player.PlayerPawn.Value is not { IsValid: true } pawn || pawn.Index != life.Pawn) continue;
                var mate = leader != null && leader.Slot != slot && !Enemies(player, leader); // the leader's teammate: no bounty in the cap
                var r = Rules.Recalculate(config, standings, IdOf(player), mate, life.Boost, life.Health, life.BaseHealth, life.MaxHealth);
                if (r.MaxHealth > life.MaxHealth)
                {
                    if (r.MaxHealth > pawn.MaxHealth) // a higher ceiling only: a mode's own higher maximum stays
                    {
                        pawn.MaxHealth = r.MaxHealth;
                        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iMaxHealth");
                        healthSet[slot] = (life.BaseHealth, r.MaxHealth); // the next spawn finds the base under it, as after a spawn boost
                    }
                    life.MaxHealth = r.MaxHealth;
                }
                var before = life.Boost;
                life.Boost = r.Boost;
                life.Health = r.Health;
                if (r.TierChanged)
                    log.LogInformation("CatchUp: {Line}", Words.Change(player.PlayerName, before, r.Boost, standings, leader?.PlayerName, life.BaseHealth, life.MaxHealth));
            }
        }
        catch (Exception e) { Fault("recalculating the boosts", e); }
    }

    // ---- hits: resistance, damage, the bounty, the one-hit knife, the second chance, the protection ----

    HookResult OnTakeDamage(CCSPlayerPawn pawn, CTakeDamageInfo info)
    {
        if (!Active || lives.Count == 0) return HookResult.Continue;
        try
        {
            if (info.Damage <= 0) return HookResult.Continue;
            if (pawn.Controller.Value?.As<CCSPlayerController>() is not { IsValid: true } victim) return HookResult.Continue;
            if (!lives.TryGetValue(victim.Slot, out var vl) || vl.Pawn != pawn.Index) return HookResult.Continue;
            if (info.Attacker.Value is not { IsValid: true, DesignerName: "player" } source) return HookResult.Continue; // the world, a fall
            var attackerPawn = source.As<CCSPlayerPawn>();
            if (attackerPawn.Controller.Value?.As<CCSPlayerController>() is not { IsValid: true } attacker) return HookResult.Continue;
            if (attacker.Slot == victim.Slot || !Enemies(attacker, victim)) return HookResult.Continue; // friendly fire is the fun page's

            lives.TryGetValue(attacker.Slot, out var al);
            if (al != null) al.Attacked = true; // hitting somebody ends your own extra spawn protection
            var bits = info.BitsDamageType;
            var melee = al is { Boost.OneHitMelee: true }
                ? Rules.MeleeOf(info.Ability.Value?.DesignerName, attackerPawn.WeaponServices?.ActiveWeapon.Value?.DesignerName,
                    (bits & (DamageTypes_t.DMG_BLAST | DamageTypes_t.DMG_BURN)) != 0)
                : Melee.None;
            var zeroed = headshotsOnly && Rules.GameZeroes(true, (bits & DamageTypes_t.DMG_BULLET) != 0, (uint)info.HitGroupId);
            var now = Server.CurrentTime;
            var hit = Rules.Hit(new HitInput(info.Damage, al?.Boost ?? Boost.None, vl.Boost, pawn.Health, pawn.ArmorValue, melee,
                VictimProtected: vl.ProtectedUntil > now && !vl.Attacked, SecondChanceLeft: !vl.SecondChanceUsed, GameZeroesIt: zeroed));
            if (hit.Block) return HookResult.Handled;
            if (hit.Damage != info.Damage) info.Damage = hit.Damage;
            if (hit.SecondChanceUsed)
            {
                vl.SecondChanceUsed = true;
                var name = victim.PlayerName;
                Server.NextFrame(() => log.LogInformation("CatchUp: {Name} survived a killing hit on their second chance", name));
            }
        }
        catch (Exception e) { Fault("in the damage hook", e); }
        return HookResult.Continue;
    }

    // ---- after a hit: lifesteal, and the regeneration clock ----

    HookResult OnHurt(EventPlayerHurt ev, GameEventInfo info)
    {
        try
        {
            if (ev.Userid is not { IsValid: true } victim) return HookResult.Continue;
            lives.TryGetValue(victim.Slot, out var vl);
            if (vl != null)
            {
                vl.LastHurtAt = Server.CurrentTime; // any damage, a fall too, restarts the wait before regeneration
                vl.RegenCarry = 0;
            }
            if (!Active || ev.Attacker is not { IsValid: true } attacker || attacker.Slot == victim.Slot) return HookResult.Continue;
            if (!lives.TryGetValue(attacker.Slot, out var al) || al.Boost.Lifesteal <= 0 || !Enemies(attacker, victim)) return HookResult.Continue;
            var heal = Rules.Lifesteal(al.Boost, ev.DmgHealth, vl?.MaxHealth ?? 100);
            if (heal <= 0) return HookResult.Continue;
            var slot = attacker.Slot;
            var s = al.Serial;
            Server.NextFrame(() => Heal(slot, s, heal)); // not inside the attacker's own shot
        }
        catch (Exception e) { Fault("after a hit", e); }
        return HookResult.Continue;
    }

    void Heal(int slot, int s, int amount)
    {
        try
        {
            if (!lives.TryGetValue(slot, out var life) || life.Serial != s) return;
            if (Utilities.GetPlayerFromSlot(slot)?.PlayerPawn.Value is not { IsValid: true } pawn || pawn.Index != life.Pawn) return;
            if (pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE || pawn.Health >= life.MaxHealth) return;
            pawn.Health = Math.Min(life.MaxHealth, pawn.Health + amount);
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        }
        catch (Exception e) { Fault("healing", e); }
    }

    // ---- shots: the protection ends, the clip refills ----

    HookResult OnFire(EventWeaponFire ev, GameEventInfo info)
    {
        if (ev.Userid is not { IsValid: true } player || !lives.TryGetValue(player.Slot, out var life)) return HookResult.Continue;
        life.Attacked = true; // attacking ends the extra spawn protection
        if (!Active || !life.Boost.InfiniteAmmo || Weapons.Kind(ev.Weapon) != WeaponKind.Gun) return HookResult.Continue;
        var slot = player.Slot;
        var s = life.Serial;
        Server.NextFrame(() => Refill(slot, s)); // after the game has taken the round out of the clip
        return HookResult.Continue;
    }

    void Refill(int slot, int s)
    {
        try
        {
            if (!lives.TryGetValue(slot, out var life) || life.Serial != s) return;
            var weapon = Utilities.GetPlayerFromSlot(slot)?.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
            if (weapon is not { IsValid: true } || Weapons.Kind(weapon.DesignerName) != WeaponKind.Gun) return;
            var data = weapon.VData;
            if (data == null || data.Handle == IntPtr.Zero) return;
            var max = data.MaxClip1;
            if (max <= 1 || weapon.Clip1 >= max) return;
            weapon.Clip1 = max;
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_iClip1");
        }
        catch (Exception e) { Fault("refilling a clip", e); }
    }

    // ---- the tick: regeneration, and the game settings the rules depend on ----

    void Tick()
    {
        try
        {
            var now = Server.CurrentTime;
            if (now >= nextCvarRead)
            {
                ReadCvars();
                nextCvarRead = now + 1;
            }
            if (!Active || lives.Count == 0) return;
            foreach (var (slot, life) in lives)
            {
                if (life.Boost.RegenPerSecond <= 0) continue;
                var add = Rules.Regen(life.Boost, now - life.LastHurtAt, tickSeconds, life.BaseHealth, ref life.RegenCarry);
                if (add <= 0) continue;
                if (Utilities.GetPlayerFromSlot(slot)?.PlayerPawn.Value is not { IsValid: true } pawn || pawn.Index != life.Pawn) continue;
                if (pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE || pawn.Health >= life.MaxHealth) continue;
                pawn.Health = Math.Min(life.MaxHealth, pawn.Health + add);
                Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
            }
        }
        catch (Exception e) { Fault("in the tick", e); }
    }

    void ReadCvars()
    {
        ffa = Truthy(Cvar("mp_teammates_are_enemies"));
        headshotsOnly = Truthy(Cvar("mp_damage_headshot_only"));
        if (double.TryParse(Cvar("mp_respawn_immunitytime"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) gameProtection = seconds;
    }

    /// <summary>A game setting's value as text. ConVar&lt;T&gt;'s type argument plays no part in ValueAsString.</summary>
    static string? Cvar(string name)
    {
        try { return ConVar<bool>.Find(name)?.ValueAsString; }
        catch (Exception) { return null; }
    }

    static bool Truthy(string? value) => (value ?? "").Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    // ---- who ----

    /// <summary>Two players are enemies on different teams, and everybody is under free-for-all (mp_teammates_are_enemies).</summary>
    bool Enemies(CCSPlayerController a, CCSPlayerController b) => ffa || a.Team != b.Team;

    static bool Playing(CCSPlayerController p) => p is { IsValid: true, IsHLTV: false } && p.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist;

    static List<CCSPlayerController> Players() => Utilities.GetPlayers().Where(Playing).ToList();

    /// <summary>A player's id for the counter: the SteamID64, or for a bot a value no SteamID64 takes.</summary>
    static ulong IdOf(CCSPlayerController p) => p.IsBot || p.SteamID == 0 ? 0xB000_0000_0000_0000UL | (uint)p.Slot : p.SteamID;

    static string? LeaderName(Standings s) => s.Leader == 0 ? null : Players().FirstOrDefault(p => IdOf(p) == s.Leader)?.PlayerName;

    bool Warmup()
    {
        if (rules is not { IsValid: true }) rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        return rules?.GameRules?.WarmupPeriod ?? false;
    }

    // ---- the command ----

    void OnCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return; // the server console and RCON only
        var verb = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "";
        switch (verb)
        {
            case "reload":
                command.ReplyToCommand($"catchup: {ReadConfig()} (the next spawns use it)");
                return;
            case "on":
                switchedOn = true;
                stopped = false;
                faults = 0;
                command.ReplyToCommand("catchup: on until the plugin reloads");
                return;
            case "off":
                switchedOn = false;
                command.ReplyToCommand("catchup: off until the plugin reloads (lives already boosted keep their health)");
                return;
            case "show" or "give":
                command.ReplyToCommand($"catchup: {Try(command, verb == "give")}");
                return;
            default:
                command.ReplyToCommand(Status());
                return;
        }
    }

    /// <summary>show: that player sees the message a gap (or a lead) would give, now. give: their current life gets that boost too
    /// (health, protection from now, every effect), to try the effects with two players; kept until they die, not decided again.</summary>
    string Try(CommandInfo command, bool give)
    {
        if (command.ArgCount < 4) return "show|give <#slot | account id | [U:1:n] | SteamID64> <kills behind | lead N>";
        var player = Find(command.GetArg(2));
        if (player == null) return $"no player {command.GetArg(2)}";
        var lead = command.GetArg(3).Equals("lead", StringComparison.OrdinalIgnoreCase);
        if (!int.TryParse(command.GetArg(lead ? 4 : 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0)
            return "a number of kills, please";
        var boost = lead ? new Boost { Lead = n, Bounty = Rules.BountyFor(config.Bounty, n) } : Rules.ForGap(config, n, n, 0, 0);
        var lines = Words.Lines(boost, config.Message);
        if (lines.Length == 0) return $"{(lead ? $"a lead of {n}" : $"{n} behind")} gives nothing";
        if (give)
        {
            if (!lives.TryGetValue(player.Slot, out var life) || player.PlayerPawn.Value is not { IsValid: true } pawn) return $"{player.PlayerName} is not alive";
            life.Boost = boost;
            life.Given = true;
            life.ProtectedUntil = Server.CurrentTime + boost.ExtraProtection;
            life.Attacked = false;
            life.SecondChanceUsed = false;
            if (boost.Health > 0)
            {
                var hp = Rules.SpawnHealth(life.BaseHealth, boost);
                SetHealth(pawn, hp, hp);
                life.MaxHealth = hp;
                life.Health = boost.Health;
                healthSet[player.Slot] = (life.BaseHealth, hp);
            }
            log.LogInformation("CatchUp: given by the console to {Name}: {Sentence}", player.PlayerName, Words.Sentence(boost));
        }
        if (!player.IsBot) player.PrintToCenterHtml(Words.Html(lines, boost), config.Message.Seconds);
        return $"{(give ? "gave" : "showed")} {player.PlayerName}: {Words.Sentence(boost)}";
    }

    static CCSPlayerController? Find(string arg)
    {
        var players = Utilities.GetPlayers();
        if (arg.StartsWith('#') && int.TryParse(arg[1..], out var slot)) return players.FirstOrDefault(p => p.Slot == slot);
        var text = arg.Trim().Trim('[', ']');
        if (text.StartsWith("U:1:", StringComparison.OrdinalIgnoreCase)) text = text[4..];
        if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return null;
        return players.FirstOrDefault(p => !p.IsBot && (p.SteamID == number || (p.SteamID & 0xFFFFFFFF) == number));
    }

    public string Status()
    {
        var state = !config.Enabled ? "off (catchup.json)" : !switchedOn ? "off (gg_catchup off)" : stopped ? $"off after {FaultsToStop} errors in {FaultWindowMinutes} minutes" : "on";
        var lines = new List<string> { $"catchup: {state}, {(config.RecalculateOnKill ? "decided again after every kill" : "decided at spawn")}; {(ladder.Tracking ? $"counting this match, {ladder.Rungs} rungs learned, {ladder.Notes} placements" : "waiting for a match start")}; free-for-all {(ffa ? "on" : "off")}, headshots only {(headshotsOnly ? "on" : "off")}, game spawn protection {gameProtection.ToString(CultureInfo.InvariantCulture)} s" };
        if (!ladder.Tracking) return string.Join("\n", lines);
        var present = Players();
        var s = ladder.Standings(present.Select(IdOf));
        lines.Add($"top {s.Top}, second {s.Second}, leader {LeaderName(s) ?? "none (tied)"}, lead {s.Lead}, bounty {Rules.BountyFor(config.Bounty, s.Lead) * 100:0}%");
        foreach (var p in present.OrderByDescending(p => s.Of(IdOf(p))))
        {
            var id = IdOf(p);
            var (level, kills, trusted) = ladder.State(id);
            var held = ladder.Held(id);
            var boost = BoostOf(p.Slot);
            var kept = boost.Health > 0 && !boost.IsBoost ? $", +{boost.Health * 100:0}% health held" : "";
            var now = (boost.IsBounty ? $"bounty +{boost.Bounty * 100:0}%" : boost.IsBoost ? Words.Sentence(boost) : "no boost") + kept;
            lines.Add($"  {p.PlayerName}: {s.Of(id)} (level {level} + {kills}{(held != level ? $", the level-{held} gun in hand" : "")}{(trusted ? "" : ", placed by guns")}), {s.Behind(id)} behind - {now}");
        }
        return string.Join("\n", lines);
    }

    string ReadConfig()
    {
        var notes = new List<string>();
        config = CatchUpConfig.Load(configPath, notes);
        ladder.Configure(config.Progress);
        foreach (var note in notes) log.LogWarning("CatchUp config: {Note}", note);
        var tiers = string.Join(", ", config.Tiers.Select(t => $"{t.MinBehind}+"));
        var summary = $"{config.Tiers.Count} tiers ({tiers} behind), bounty from {config.Bounty.MinLead} ahead, duel factor at most "
                      + $"{config.Balance.MaxDuelFactor.ToString(CultureInfo.InvariantCulture)}{(config.Balance.ScoreboardCap ? " and the scoreboard's ratio" : "")}, "
                      + (config.RecalculateOnKill ? "decided again after every kill" : "decided at spawn")
                      + (config.Enabled ? "" : ", switched off in the file");
        return notes.Count == 0 ? summary : $"{summary} ({notes.Count} config note(s) in the log)";
    }

    /// <summary>One error is logged and shrugged off; a burst of them (FaultsToStop within FaultWindowMinutes) switches the effects off,
    /// rather than repeating a throw at every hit for a night. The first three of each window are logged with their stack.</summary>
    void Fault(string where, Exception e)
    {
        var now = DateTime.UtcNow;
        if ((now - faultWindow).TotalMinutes > FaultWindowMinutes)
        {
            faultWindow = now;
            faults = 0;
        }
        faults++;
        if (faults <= 3) log.LogError(e, "CatchUp: error {Where} ({Count} in this window)", where, faults);
        if (faults == FaultsToStop && !stopped)
        {
            stopped = true;
            log.LogError("CatchUp: {Count} errors in {Minutes} minutes - the boosts are off until 'gg_catchup on' or a plugin reload", faults, FaultWindowMinutes);
        }
    }

    /// <summary>Compiles the module's methods at load, so the first hit or spawn does not pay for the JIT inside a player's callback.</summary>
    void WarmUp()
    {
        var clock = Stopwatch.StartNew();
        var count = 0;
        foreach (var type in new[] { typeof(CatchUpModule), typeof(Rules), typeof(Ladder), typeof(Standings), typeof(Boost), typeof(Words), typeof(Weapons), typeof(CatchUpConfig) })
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.IsAbstract || method.ContainsGenericParameters) continue;
                try { RuntimeHelpers.PrepareMethod(method.MethodHandle); count++; }
                catch (Exception) { } // compiled on first use instead
            }
        log.LogInformation("CatchUp: {Count} methods compiled ahead in {Ms} ms", count, clock.ElapsedMilliseconds);
    }
}
