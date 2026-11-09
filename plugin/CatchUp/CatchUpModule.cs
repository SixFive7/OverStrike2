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

/// <summary>Arms Race catch-up: every enemy hit takes its boost from the gap between its two players on the ladder (Rules.PairFor) - the one
/// behind hits harder and takes less from the one ahead, two players within a kill of each other play plain Counter-Strike - and each player
/// gets their own extras by their gap to the leader, the leader a bounty, with a private message at spawn saying what and why (Show). Every
/// living player's own boost is decided again a frame after each change to the ladder, silently (Rules.Recalculate). Every life's end goes to
/// the log with what it took (LifeTally). Construct it in the host plugin's Load and call Load(hotReload); the host's own unload removes
/// everything it registered. Nothing heavy runs inside a player's own callback (spawn, hit, kill, shot): those only count and queue, and the
/// work is done a frame later or on a timer, so a player is never dropped for overflow.</summary>
public sealed class CatchUpModule
{
    /// <summary>The server command (console and RCON only): status, reload, on, off, show, give.</summary>
    public const string Command = "os_catchup";

    sealed class Life
    {
        public int Serial;
        public uint Pawn;
        public Boost Boost = Boost.None; // this player's own decision, by their gap to the leader: regeneration, protection, ammo, the bounty, the message
        public double SpawnedAt, ProtectedUntil, LastHurtAt, RegenCarry;
        public bool Attacked, SecondChanceUsed;
        public int BaseHealth;           // the health this life spawned with: catch-up heals no higher (Vampire's own ceiling is its own)
        public int? Given;               // a boost given from the console: this player fights as that many kills behind everybody, until death
        public readonly LifeTally Tally = new();
    }

    readonly BasePlugin plugin;
    readonly ILogger log;
    readonly string configPath;
    CatchUpConfig config = new();
    readonly Ladder ladder;
    readonly Dictionary<int, Life> lives = new();
    readonly HashSet<int> fresh = new();                            // joined, not yet spawned on a team
    readonly HashSet<int> boostedLastLife = new();
    bool switchedOn = true;
    bool recalculation;  // a recalculation is queued for the next frame
    bool stopped;        // too many errors in a short time: off until 'os_catchup on' or a reload
    int faults;          // errors in the current window
    DateTime faultWindow;
    int serial;
    double nextCvarRead;
    bool ffa, headshotsOnly;
    double gameProtection = 5;
    CCSGameRulesProxy? rules;

    const int FaultsToStop = 20;           // errors within FaultWindowMinutes that switch the effects off
    const double FaultWindowMinutes = 10;
    const float ApplyDelaySeconds = 0.1f;  // after a spawn, before the boost is decided and applied: the game sets the spawn health and loadout first
    const float RegenTickSeconds = 0.5f;   // how often regeneration is applied
    const int HeadGroup = 1;               // the head's hit group

    public CatchUpModule(BasePlugin plugin, ILogger log, string configPath)
    {
        this.plugin = plugin;
        this.log = log;
        this.configPath = configPath;
        ladder = new Ladder();
    }

    /// <summary>How the host shows a player their message: the player, the boost, and the centre panel's HTML of it (Words.Html), for a game
    /// that cannot show the host's own card. Unset: nothing is shown.</summary>
    public Action<CCSPlayerController, Boost, string>? Show { get; set; }

    bool Active => switchedOn && !stopped;

    /// <summary>The host's switch: on while catch-up's fun setting is on and the map that is on is Arms Race (the plugin's CatchUpFollows).
    /// On also clears a stop after errors, as 'os_catchup on' does.</summary>
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
        plugin.AddTimer(RegenTickSeconds, Tick, TimerFlags.REPEAT);
        plugin.AddCommand(Command, "Arms Race catch-up: os_catchup [status | reload | on | off | show <player> <behind | lead N> | give <player> <behind | lead N>]", OnCommand);
        WarmUp();
        log.LogInformation("CatchUp: {Summary}; {State}", summary,
            hotReload ? "loaded during a map: counting starts at the next match" : "counting starts at the first match");
    }

    /// <summary>The boost of a slot's current life.</summary>
    Boost BoostOf(int slot) => lives.TryGetValue(slot, out var life) ? life.Boost : Boost.None;

    // ---- what the host's other modules ask ----

    /// <summary>Two players are enemies on different teams, and everybody is under free-for-all (mp_teammates_are_enemies, read every second).</summary>
    public bool Enemies(CCSPlayerController a, CCSPlayerController b) => ffa || a.Team != b.Team;

    /// <summary>The share of the health the victim loses to this attacker's hit that catch-up's lifesteal gives the attacker back: the pair's,
    /// while catch-up is on, 0 otherwise. Vampire takes no more than what it leaves of the half a hit may heal (FunEffects.OtherShare).</summary>
    public double LifestealShare(CCSPlayerController attacker, CCSPlayerController victim)
    {
        if (!Active || attacker.Slot == victim.Slot || !Enemies(attacker, victim)) return 0;
        if (!lives.TryGetValue(attacker.Slot, out var al) || !lives.TryGetValue(victim.Slot, out var vl)) return 0;
        return PairOf(attacker, al, victim, vl).Lifesteal;
    }

    /// <summary>A heal another module gave (Vampire), for the life's line in the log.</summary>
    public void NoteHeal(int slot, int amount)
    {
        if (amount > 0 && lives.TryGetValue(slot, out var life)) life.Tally.Vampire += amount;
    }

    /// <summary>The boost of one enemy hit, from the gap between its two players on the ladder now. A boost given from the console counts as
    /// its gap to everybody; outside a counted match (warmup, loaded mid-match) nothing but that.</summary>
    Pair PairOf(CCSPlayerController attacker, Life? al, CCSPlayerController victim, Life vl)
    {
        int gap;
        if (al?.Given is not null || vl.Given is not null) gap = (al?.Given ?? 0) - (vl.Given ?? 0);
        else if (ladder.Tracking) gap = ladder.Progress(IdOf(victim)) - ladder.Progress(IdOf(attacker));
        else return Pair.Even;
        return Rules.PairFor(config, gap, vl.Boost.Bounty);
    }

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
        fresh.Clear();
        boostedLastLife.Clear();
        recalculation = false; // a frame queued before the map change may never come
        rules = null;
        nextCvarRead = 0; // the game's clock starts over with a map: an old map's deadline would leave its rules (free-for-all, headshots only) in force for as long as it ran
    }

    void OnPutInServer(int slot) => fresh.Add(slot);

    HookResult OnDisconnect(EventPlayerDisconnect ev, GameEventInfo info)
    {
        if (ev.Userid is not { IsValid: true } player) return HookResult.Continue;
        var slot = player.Slot;
        ladder.Leave(IdOf(player)); // CS2 starts a returning player at the bottom; if it does not, their next gun kill places them
        lives.Remove(slot);
        fresh.Remove(slot);
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

    // ---- kills: the ladder, and the life's line ----

    HookResult OnDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        try
        {
            if (ev.Userid is not { IsValid: true } victim) return HookResult.Continue;
            var attacker = ev.Attacker;
            var enemyKill = attacker is { IsValid: true } && attacker.Slot != victim.Slot && Enemies(attacker, victim);
            if (lives.Remove(victim.Slot, out var ended)) // the life is over, its boost with it; its line in the log
            {
                var line = Words.Life(victim.PlayerName, TeamName(victim), Server.CurrentTime - ended.SpawnedAt, ended.Boost, ended.Tally,
                    attacker is { IsValid: true } && attacker.Slot != victim.Slot ? attacker.PlayerName : null, Weapons.Name(ev.Weapon), enemyKill);
                Server.NextFrame(() => log.LogInformation("CatchUp: life: {Line}", line));
            }
            if (!ladder.Tracking || !Playing(victim)) return HookResult.Continue;
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
            QueueRecalculation(); // the ladder changed: every living player's own boost again, a frame later
        }
        catch (Exception e) { Fault("counting a kill", e); }
        return HookResult.Continue;
    }

    static string TeamName(CCSPlayerController p) => p.Team switch { CsTeam.Terrorist => "T", CsTeam.CounterTerrorist => "CT", var t => t.ToString() };

    /// <summary>A player's place on the ladder as the counter has it, after the kill: "level 7 + 1 = 15" (kills), and whether it followed them from the start.</summary>
    string Where(CCSPlayerController p)
    {
        var (level, kills, trusted) = ladder.State(IdOf(p));
        return $"level {level} + {kills} = {level * Ladder.KillsPerLevel + kills}{(trusted ? "" : ", placed")}";
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

    // ---- spawns: the decision, the message ----

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
        plugin.AddTimer(ApplyDelaySeconds, () => Apply(slot, s, at, first), TimerFlags.STOP_ON_MAPCHANGE);
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

            Standings? standings = null;
            var boost = Boost.None;
            if (Active && ladder.Tracking && !Warmup())
            {
                var present = Players();
                standings = ladder.Standings(present.Select(IdOf));
                var leaderId = standings.Leader;
                var leader = leaderId == 0 ? null : present.FirstOrDefault(p => IdOf(p) == leaderId);
                boost = Rules.Decide(config, standings, id, leader != null && leader.Slot != player.Slot && !Enemies(player, leader)); // the leader's teammate: no bounty in their duel factor
            }

            // The base is the health this life spawned with - the game's 100, or a map's own: catch-up's heals go no higher. Nobody's health is
            // raised: the tiers' extra health is part of the damage a player ahead does to them (Rules.Taken).
            lives[slot] = new Life
            {
                Serial = s, Pawn = pawn.Index, Boost = boost, SpawnedAt = spawnedAt, LastHurtAt = now,
                ProtectedUntil = Rules.ProtectedUntil(spawnedAt, gameProtection, boost), BaseHealth = Math.Max(1, pawn.Health),
            };

            if (standings != null)
            {
                if (boost.IsBoost || boost.IsBounty)
                {
                    boostedLastLife.Add(slot);
                    log.LogInformation("CatchUp: {Line}", Words.Decision(player.PlayerName, boost, standings, LeaderName(standings)));
                }
                else if (boostedLastLife.Remove(slot))
                    log.LogInformation("CatchUp: {Line}", Words.Decision(player.PlayerName, boost, standings, LeaderName(standings)));
            }

            var lines = Words.Lines(boost);
            if (lines.Length == 0 || player.IsBot) return;
            var wait = Rules.MessageDelay(first) - (now - spawnedAt);
            plugin.AddTimer((float)Math.Max(0.01, wait), () => ShowMessage(slot, s, lines, boost), TimerFlags.STOP_ON_MAPCHANGE);
        }
        catch (Exception e) { Fault("applying a spawn", e); }
    }

    void ShowMessage(int slot, int s, string[] lines, Boost boost)
    {
        if (!lives.TryGetValue(slot, out var life) || life.Serial != s) return; // died or respawned since: that spawn says its own
        if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true, IsBot: false } player) return;
        Show?.Invoke(player, boost, Words.Html(lines, boost));
    }

    // ---- the ladder changed: every living player's own boost again ----

    /// <summary>Once per frame however many kills it had (a grenade can take several): the work is done the next frame, never inside the
    /// death event.</summary>
    void QueueRecalculation()
    {
        if (recalculation) return;
        recalculation = true;
        Server.NextFrame(Recalculate);
    }

    /// <summary>Decides every living player's own boost again by the standings now: regeneration, infinite ammo and the bounty follow the new
    /// decision both ways; the spawn protection and the message stay the spawn's. The hits need nothing of it, each looks its own gap up. One
    /// log line when a player's tier or bounty step changes, none otherwise.</summary>
    void Recalculate()
    {
        recalculation = false;
        try
        {
            if (!Active || !ladder.Tracking || lives.Count == 0 || Warmup()) return;
            var present = Players();
            var standings = ladder.Standings(present.Select(IdOf));
            var leader = standings.Leader == 0 ? null : present.FirstOrDefault(p => IdOf(p) == standings.Leader);
            foreach (var (slot, life) in lives)
            {
                if (life.Given != null) continue;
                var player = present.FirstOrDefault(p => p.Slot == slot);
                if (player is not { PawnIsAlive: true } || player.PlayerPawn.Value is not { IsValid: true } pawn || pawn.Index != life.Pawn) continue;
                var mate = leader != null && leader.Slot != slot && !Enemies(player, leader); // the leader's teammate: no bounty in their duel factor
                var (next, changed) = Rules.Recalculate(config, standings, IdOf(player), mate, life.Boost);
                var before = life.Boost;
                life.Boost = next;
                if (changed) log.LogInformation("CatchUp: {Line}", Words.Change(player.PlayerName, before, next, standings, leader?.PlayerName));
            }
        }
        catch (Exception e) { Fault("recalculating the boosts", e); }
    }

    // ---- hits: the pair's boost, the bounty, the one-hit knife, the second chance, the protection ----

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
            var pair = PairOf(attacker, al, victim, vl);
            var bits = info.BitsDamageType;
            var melee = pair.OneHitMelee
                ? Rules.MeleeOf(info.Ability.Value?.DesignerName, attackerPawn.WeaponServices?.ActiveWeapon.Value?.DesignerName,
                    (bits & (DamageTypes_t.DMG_BLAST | DamageTypes_t.DMG_BURN)) != 0)
                : Melee.None;
            var zeroed = headshotsOnly && Rules.GameZeroes(true, (bits & DamageTypes_t.DMG_BULLET) != 0, (uint)info.HitGroupId);
            var now = Server.CurrentTime;
            var hit = Rules.Hit(new HitInput(info.Damage, pair, pawn.Health, pawn.ArmorValue, melee,
                VictimProtected: vl.ProtectedUntil > now && !vl.Attacked, SecondChanceLeft: !vl.SecondChanceUsed, GameZeroesIt: zeroed));
            if (hit.Block) return HookResult.Handled;
            if (hit.Damage != info.Damage) info.Damage = hit.Damage;
            if (hit.SecondChanceUsed)
            {
                vl.SecondChanceUsed = true;
                vl.Tally.SecondChance = true;
                var name = victim.PlayerName;
                var by = attacker.PlayerName;
                var gap = pair.Gap;
                Server.NextFrame(() => log.LogInformation("CatchUp: {Name} survived a killing hit from {By}, {Gap} kills ahead, on their second chance", name, by, -gap));
            }
        }
        catch (Exception e) { Fault("in the damage hook", e); }
        return HookResult.Continue;
    }

    // ---- after a hit: the life's tally, lifesteal, and the regeneration clock ----

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
            if (ev.Attacker is not { IsValid: true } attacker || attacker.Slot == victim.Slot || !Enemies(attacker, victim)) return HookResult.Continue;
            lives.TryGetValue(attacker.Slot, out var al);
            var pair = vl != null && Active ? PairOf(attacker, al, victim, vl) : Pair.Even;
            vl?.Tally.Took(attacker.Slot, attacker.PlayerName, ev.Hitgroup == HeadGroup, ev.DmgHealth, ev.DmgArmor, pair.Multiplier);
            if (al != null) al.Tally.HitsLanded++;
            if (!Active || al == null || pair.Lifesteal <= 0) return HookResult.Continue;
            var heal = Rules.Lifesteal(pair.Lifesteal, ev.DmgHealth, victim.PlayerPawn.Value?.MaxHealth ?? 100);
            if (heal <= 0) return HookResult.Continue;
            var slot = attacker.Slot;
            var s = al.Serial;
            Server.NextFrame(() => Heal(slot, s, heal)); // not inside the attacker's own shot
        }
        catch (Exception e) { Fault("after a hit", e); }
        return HookResult.Continue;
    }

    /// <summary>Lifesteal: health back, up to what the life spawned with.</summary>
    void Heal(int slot, int s, int amount)
    {
        try
        {
            if (!lives.TryGetValue(slot, out var life) || life.Serial != s) return;
            if (Utilities.GetPlayerFromSlot(slot)?.PlayerPawn.Value is not { IsValid: true } pawn || pawn.Index != life.Pawn) return;
            if (pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE || pawn.Health >= life.BaseHealth) return;
            var to = Math.Min(life.BaseHealth, pawn.Health + amount);
            life.Tally.Lifesteal += to - pawn.Health;
            pawn.Health = to;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        }
        catch (Exception e) { Fault("healing", e); }
    }

    // ---- shots: the protection ends, the clip refills ----

    HookResult OnFire(EventWeaponFire ev, GameEventInfo info)
    {
        if (ev.Userid is not { IsValid: true } player || !lives.TryGetValue(player.Slot, out var life)) return HookResult.Continue;
        life.Attacked = true; // attacking ends the extra spawn protection
        var gun = Weapons.Kind(ev.Weapon) == WeaponKind.Gun;
        if (gun) life.Tally.Shots++;
        if (!Active || !life.Boost.InfiniteAmmo || !gun) return HookResult.Continue;
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
                var add = Rules.Regen(life.Boost, now - life.LastHurtAt, RegenTickSeconds, life.BaseHealth, ref life.RegenCarry);
                if (add <= 0) continue;
                if (Utilities.GetPlayerFromSlot(slot)?.PlayerPawn.Value is not { IsValid: true } pawn || pawn.Index != life.Pawn) continue;
                if (pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE || pawn.Health >= life.BaseHealth) continue;
                var to = Math.Min(life.BaseHealth, pawn.Health + add);
                life.Tally.Regen += to - pawn.Health;
                pawn.Health = to;
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
                command.ReplyToCommand("catchup: off until the plugin reloads");
                return;
            case "show" or "give":
                command.ReplyToCommand($"catchup: {Try(command, verb == "give")}");
                return;
            default:
                command.ReplyToCommand(Status());
                return;
        }
    }

    /// <summary>show: that player sees the message a gap (or a lead) would give, now. give: their current life gets that boost too - the
    /// extras, protection from now, and in every fight the boost of a player that many kills behind the other (a lead: the bounty) - to try the
    /// effects with two players; kept until they die, not decided again.</summary>
    string Try(CommandInfo command, bool give)
    {
        if (command.ArgCount < 4) return "show|give <#slot | account id | [U:1:n] | SteamID64> <kills behind | lead N>";
        var player = Find(command.GetArg(2));
        if (player == null) return $"no player {command.GetArg(2)}";
        var lead = command.GetArg(3).Equals("lead", StringComparison.OrdinalIgnoreCase);
        if (!int.TryParse(command.GetArg(lead ? 4 : 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0)
            return "a number of kills, please";
        var boost = lead ? new Boost { Lead = n, Bounty = Rules.BountyFor(config.Bounty, n) } : Rules.ForGap(config, n, 0);
        var lines = Words.Lines(boost);
        if (lines.Length == 0) return $"{(lead ? $"a lead of {n}" : $"{n} behind")} gives nothing";
        if (give)
        {
            if (!lives.TryGetValue(player.Slot, out var life) || player.PlayerPawn.Value is not { IsValid: true }) return $"{player.PlayerName} is not alive";
            life.Boost = boost;
            life.Given = lead ? -n : n;
            life.ProtectedUntil = Server.CurrentTime + boost.ExtraProtection;
            life.Attacked = false;
            life.SecondChanceUsed = false;
            log.LogInformation("CatchUp: given by the console to {Name}: {Sentence}", player.PlayerName, Words.Sentence(boost));
        }
        if (!player.IsBot) Show?.Invoke(player, boost, Words.Html(lines, boost)); // what a real boost shows: the card under the crosshair, or an older HUD's centre panel
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
        var state = !switchedOn ? "off (switched off: catch-up's fun setting, a mode other than Arms Race, or the console)" : stopped ? $"off after {FaultsToStop} errors in {FaultWindowMinutes} minutes" : "on";
        var lines = new List<string> { $"catchup: {state}; {(ladder.Tracking ? $"counting this match, {ladder.Rungs} rungs learned, {ladder.Notes} placements" : "waiting for a match start")}; free-for-all {(ffa ? "on" : "off")}, headshots only {(headshotsOnly ? "on" : "off")}, game spawn protection {gameProtection.ToString(CultureInfo.InvariantCulture)} s; every hit by the gap between its two players" };
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
            var now = boost.IsBounty ? $"bounty +{boost.Bounty * 100:0}%" : boost.IsBoost ? Words.Sentence(boost) : "no boost";
            lines.Add($"  {p.PlayerName}: {s.Of(id)} (level {level} + {kills}{(held != level ? $", the level-{held} gun in hand" : "")}{(trusted ? "" : ", placed by guns")}), {s.Behind(id)} behind - {now}");
        }
        return string.Join("\n", lines);
    }

    string ReadConfig()
    {
        var notes = new List<string>();
        config = CatchUpConfig.Load(configPath, notes);
        foreach (var note in notes) log.LogWarning("CatchUp config: {Note}", note);
        var tiers = string.Join(", ", config.Tiers.Select(t => $"{t.MinBehind}+"));
        var summary = $"{config.Tiers.Count} tiers ({tiers} kills between two players, their own extras by the gap to the leader), bounty from {config.Bounty.MinLead} ahead, decided again after every kill";
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
            log.LogError("CatchUp: {Count} errors in {Minutes} minutes - the boosts are off until 'os_catchup on' or a plugin reload", faults, FaultWindowMinutes);
        }
    }

    /// <summary>Compiles the module's methods at load, so the first hit or spawn does not pay for the JIT inside a player's callback.</summary>
    void WarmUp()
    {
        var clock = Stopwatch.StartNew();
        var count = 0;
        foreach (var type in new[] { typeof(CatchUpModule), typeof(Rules), typeof(Ladder), typeof(Standings), typeof(Boost), typeof(Words), typeof(Weapons), typeof(CatchUpConfig), typeof(LifeTally) })
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.IsAbstract || method.ContainsGenericParameters) continue;
                try { RuntimeHelpers.PrepareMethod(method.MethodHandle); count++; }
                catch (Exception) { } // compiled on first use instead
            }
        log.LogInformation("CatchUp: {Count} methods compiled ahead in {Ms} ms", count, clock.ElapsedMilliseconds);
    }
}
