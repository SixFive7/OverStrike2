using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The cloak: a player hidden from everybody else, all of them, and put back exactly as they were. The fun page's Ghost (FunEffects.cs),
/// the spawn cloak (SpawnCloak, below) and os_cloak hide use it, each with its own reason (CloakWhy); a player is seen again once none holds.
///
/// What other players see of a player, and what hides it (CounterStrikeSharp 1.0.379; every field below is networked on CS2 1.41.9.x, by
/// Valve's network dump):
///   - the player model, the pawn: m_nRenderMode kRenderTransAlpha and m_clrRender's alpha 0. The world-model
///     gloves (C_WorldModelGloves) and an agent's patches are the client's own, drawn on the pawn's model from the pawn's fields - no entity of
///     the server's - and should go with it;
///   - every weapon the pawn carries, m_hMyWeapons: the one in the hand and every other - the knife, the pistol, the grenades, the C4, the Zeus.
///     Each is an entity of its own with its own render mode and colour, hidden the same way, so whatever the client draws of these entities,
///     in the hand or holstered on the body (its holster attachments), the belt's grenades included, is hidden with it. m_hActiveWeapon too,
///     should it ever not be among them;
///   - wearables, m_hMyWearables (CEconWearable): hidden too, should the game ever put one there (the gloves are not one);
///   - shadows: m_flShadowStrength 0 on all of them, since an invisible model may still cast one;
///   - the spawn protection's look, the pawn's m_GunGameImmunityColor: its alpha 0 too. The client "slams the player model render settings
///     each frame for immunity" unless sv_disable_immunity_alpha is set, and that colour is the likeliest one it slams them to - the spawn
///     cloak mostly hides players who are still protected.
/// The game is not known to write any of these when a weapon is switched, picked up or handed out. Even so a pass after the entities' think,
/// every tick, holds each hidden player's whole set again: a weapon they have newly (a pickup, Arms Race's next gun at a kill, a grenade
/// handed out) is hidden in the tick it came, before a snapshot shows it; one that has left them (dropped, thrown, taken away, left where
/// they died - mp_death_drop_grenade drops a grenade) is put back in the same tick; and anybody else's write is undone. Each entity's look is
/// taken before the cloak first touches it and put back exactly - mode, colour, shadow; one that goes from one hidden player to another keeps
/// the look from before the first (CloakBook).
///
/// Not used: m_fEffects' EF_NODRAW, which Valve documents as keeping the entity from every client - its own player's too, who would lose
/// their own gun - and which the game's own weapon code may set and clear; and keeping the entities out of the others' snapshots
/// (Listeners.CheckTransmit), which can make a client error out. Two more ways remain, should a real game show something still drawn:
/// EF_NODRAW_BUT_TRANSMIT (drawn by no client, still sent) and kRenderNone. A weapon's charm, for one, is a client-side model of the
/// weapon's (C_KeychainModule), and whether it follows the weapon's alpha is not known; nor whether the carried C4's light (a particle) does.
/// Never hidden: what is no model - the radar's dot while an enemy sees the player, the name over them when aimed at (mp_playerid), sounds,
/// muzzle flashes, tracers, blood.
///
/// Nothing heavy inside a player's callback: the events only change the reasons, and the pass makes every write. A death, a respawn and a
/// disconnect end every reason of that player's life; a new map forgets everything without touching an entity (they go with the map); the
/// plugin's unload puts everything back.</summary>
sealed class Cloak(BasePlugin plugin, ILogger log)
{
    public const string Command = "os_cloak";

    const double TestSeconds = 10;         // os_cloak hide without a time

    /// <summary>An entity as the cloak found it, to put back: its render mode, colour and shadow, and for the pawn the spawn protection's
    /// colour.</summary>
    sealed record Painted(CBaseModelEntity Entity, string Name, RenderMode_t Mode, int Colour, float Shadow, bool Pawn, int Immunity);

    readonly CloakBook<Painted> book = new();
    readonly Dictionary<int, CloakWhy> why = new();       // slot -> why that player is hidden
    readonly Dictionary<int, string> fresh = new();       // slots hidden since the last pass, and what hid them: logged once the pass has held them
    readonly Dictionary<int, int> tests = new();          // slot -> the os_cloak hide that runs for it (a newer one replaces it)
    readonly Dictionary<int, int> hides = new();          // slot -> the hide they are in (Hide): a number new each time they go from seen to hidden
    int hideCount;
    readonly Dictionary<string, bool> networked = new();  // "class.field" -> networked (looked up once)
    readonly List<int> slots = new();
    int testSerial;
    int faults;                                           // errors in the pass on this map, for the log
    long passes;                                          // passes run, for the watch on the listener (Load)
    bool onTick;                                          // the pass runs on the tick instead

    /// <summary>Before each pass: the spawn cloak's look at every player (SpawnCloak).</summary>
    public event Action? BeforePass;

    /// <summary>The spawn cloak's word on a player, for the console.</summary>
    public Func<CCSPlayerController, string>? About { get; set; }

    public void Load()
    {
        // After the entities' think; failing that - it throws, or never runs - every tick before the think instead, where a write of the game's in
        // the same tick can show for one snapshot. Never the whole plugin down for it, and never a cloak that silently does nothing.
        try { plugin.RegisterListener<Listeners.OnServerPostEntityThink>(Pass); }
        catch (Exception e) { OnTickInstead($"no listener after the entities' think ({e.Message})"); }
        plugin.AddTimer(5f, () => { if (passes == 0 && !onTick) OnTickInstead("the listener after the entities' think never ran"); });
        plugin.RegisterEventHandler<EventPlayerSpawn>((ev, _) => { if (ev.Userid is { IsValid: true } p) End(p, "a new life"); return HookResult.Continue; });
        plugin.RegisterEventHandler<EventPlayerDeath>((ev, _) => { if (ev.Userid is { IsValid: true } p) End(p, "died"); return HookResult.Continue; });
        plugin.RegisterEventHandler<EventPlayerDisconnect>((ev, _) => { if (ev.Userid is { IsValid: true } p) End(p, "left the server"); return HookResult.Continue; });
        plugin.RegisterListener<Listeners.OnMapEnd>(Forget);
        plugin.RegisterListener<Listeners.OnMapStart>(_ => Forget());
        plugin.AddCommand(Command, "Over Strike 2 cloak: status | hide <account | #slot> [seconds] | show <account | #slot>", OnCommand);
        WarmUp();
    }

    void OnTickInstead(string reason)
    {
        if (onTick) return;
        onTick = true;
        plugin.RegisterListener<Listeners.OnTick>(Pass);
        log.LogWarning("Over Strike 2 cloak: {Why} - the pass runs every tick before the think instead", reason);
    }

    /// <summary>The plugin unloads (a reload, a new build): everything the cloak painted goes back, nobody stays hidden by an instance that is gone.</summary>
    public void Unload()
    {
        var all = book.Clear();
        foreach (var (_, _, before) in all) Restore(before);
        why.Clear();
        fresh.Clear();
        if (all.Count > 0) log.LogInformation("Over Strike 2 cloak: the plugin unloads - {Count} entities put back", all.Count);
    }

    /// <summary>Why a player is hidden now (None: seen).</summary>
    public CloakWhy Why(int slot) => why.GetValueOrDefault(slot);

    /// <summary>The hide a player is in: a number new each time they go from seen to hidden, whatever the reason, and 0 while seen. The bots' guard
    /// (BotGuard, CloakShield) shields a hidden player from the bots until their own first attack in that hide.</summary>
    public int Hide(int slot) => why.ContainsKey(slot) ? hides.GetValueOrDefault(slot) : 0;

    /// <summary>A reason to hide a player starts (on) or ends; because is what the log says. On needs the player alive: false when they are
    /// not, and nothing changes. The writes come in the pass after the entities' think: in this tick when called before it, an event's
    /// included.</summary>
    public bool Want(int slot, CloakWhy reason, bool on, string because)
    {
        var was = Why(slot);
        if (on)
        {
            if ((was & reason) != 0) return true;
            if (Alive(slot) == null) return false;
            why[slot] = was | reason;
            if (was == CloakWhy.None) { fresh[slot] = because; hides[slot] = ++hideCount; } // seen until now: a new hide
            return true;
        }
        if ((was & reason) == 0) return true;
        var now = was & ~reason;
        if (now != CloakWhy.None) { why[slot] = now; return true; }
        why.Remove(slot);
        fresh.Remove(slot);
        log.LogInformation("Over Strike 2 cloak: {Name} seen again - {Because}", Utilities.GetPlayerFromSlot(slot)?.PlayerName ?? $"slot {slot}", because);
        return true;
    }

    /// <summary>The slots one reason hides now, into a list the caller keeps.</summary>
    public void Hidden(CloakWhy reason, List<int> into)
    {
        into.Clear();
        foreach (var (slot, w) in why) if ((w & reason) != 0) into.Add(slot);
    }

    /// <summary>A player's life is over or new: every reason of theirs ends (the pass puts their entities back). Light: inside the event.</summary>
    void End(CCSPlayerController player, string because)
    {
        var slot = player.Slot;
        if (!why.Remove(slot)) return;
        fresh.Remove(slot);
        var name = player.PlayerName;
        Server.NextFrame(() => log.LogInformation("Over Strike 2 cloak: {Name} seen again - {Because}", name, because));
    }

    /// <summary>A map ends or starts: its entities go with it, so nothing is put back; the book and every reason are forgotten.</summary>
    void Forget()
    {
        book.Forget();
        why.Clear();
        fresh.Clear();
        tests.Clear();
        hides.Clear();
        faults = 0;
    }

    static CCSPlayerPawn? Alive(int slot) =>
        Utilities.GetPlayerFromSlot(slot) is { IsValid: true } p && p.PlayerPawn.Value is { IsValid: true } pawn && pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE ? pawn : null;

    /// <summary>Every tick, after the entities' think and before the snapshot: the spawn cloak's look, then every hidden player's whole set held
    /// and hidden, and whatever nobody holds any more put back. Nobody hidden and nothing painted: one check.</summary>
    void Pass()
    {
        passes++;
        try { BeforePass?.Invoke(); }
        catch (Exception e) { Fault("in the spawn cloak's look", e); }
        if (why.Count == 0 && book.Count == 0) return;
        book.Begin();
        slots.Clear();
        slots.AddRange(why.Keys);
        foreach (var slot in slots)
        {
            try
            {
                if (Alive(slot) is not { } pawn) { why.Remove(slot); fresh.Remove(slot); continue; } // the life is over (its event ends it too)
                Hold(slot, pawn, pawn);
                if (pawn.WeaponServices is { } weapons)
                {
                    foreach (var weapon in weapons.MyWeapons) Hold(slot, weapon.Value, null);
                    Hold(slot, weapons.ActiveWeapon.Value, null);
                }
                foreach (var wearable in pawn.MyWearables) Hold(slot, wearable.Value, null);
                if (fresh.Remove(slot, out var because)) LogHidden(slot, because);
            }
            catch (Exception e)
            {
                book.Keep(slot); // what this player had stays as it is this tick: no flicker of a half-done pass
                Fault("hiding a player", e);
            }
        }
        foreach (var (_, _, before) in book.End())
        {
            try { Restore(before); }
            catch (Exception e) { Fault("putting an entity back", e); }
        }
    }

    /// <summary>An error in the pass is logged, the first few of each map with their stack; the pass goes on (a game update that moved a field
    /// must not take the put-back with it).</summary>
    void Fault(string where, Exception e)
    {
        if (++faults <= 3) log.LogError(e, "Over Strike 2 cloak: error {Where} ({Count} on this map)", where, faults);
        else if (faults % 1000 == 0) log.LogError("Over Strike 2 cloak: error {Where}: {Message} ({Count} on this map)", where, e.Message, faults);
    }

    void Hold(int slot, CBaseModelEntity? entity, CCSPlayerPawn? pawn)
    {
        if (entity is not { IsValid: true }) return;
        var handle = entity.EntityHandle.Raw;
        if (!book.Hold(handle, slot))
            book.Add(handle, slot, new Painted(entity, entity.DesignerName ?? "?", entity.RenderMode, entity.Render.ToArgb(), entity.ShadowStrength,
                pawn != null, pawn?.GunGameImmunityColor.ToArgb() ?? 0));
        Hide(entity, pawn);
    }

    /// <summary>One entity hidden: only what is not hidden yet is written, so a pass that finds everything in place writes nothing.</summary>
    void Hide(CBaseModelEntity e, CCSPlayerPawn? pawn)
    {
        if (e.RenderMode != RenderMode_t.kRenderTransAlpha) { e.RenderMode = RenderMode_t.kRenderTransAlpha; Changed(e, "CBaseModelEntity", "m_nRenderMode"); }
        var colour = e.Render;
        if (colour.A != 0) { e.Render = Color.FromArgb(0, colour.R, colour.G, colour.B); Changed(e, "CBaseModelEntity", "m_clrRender"); }
        if (e.ShadowStrength != 0) { e.ShadowStrength = 0; Changed(e, "CBaseModelEntity", "m_flShadowStrength"); }
        if (pawn == null) return;
        var immunity = pawn.GunGameImmunityColor;
        if (immunity.A != 0) { pawn.GunGameImmunityColor = Color.FromArgb(0, immunity.R, immunity.G, immunity.B); Changed(pawn, "CCSPlayerPawn", "m_GunGameImmunityColor"); }
    }

    /// <summary>One entity back as the cloak found it - when it still exists.</summary>
    void Restore(Painted p)
    {
        var e = p.Entity;
        if (!e.IsValid) return;
        if (e.RenderMode != p.Mode) { e.RenderMode = p.Mode; Changed(e, "CBaseModelEntity", "m_nRenderMode"); }
        if (e.Render.ToArgb() != p.Colour) { e.Render = Color.FromArgb(p.Colour); Changed(e, "CBaseModelEntity", "m_clrRender"); }
        if (e.ShadowStrength != p.Shadow) { e.ShadowStrength = p.Shadow; Changed(e, "CBaseModelEntity", "m_flShadowStrength"); }
        if (p.Pawn && e is CCSPlayerPawn pawn && pawn.GunGameImmunityColor.ToArgb() != p.Immunity)
        {
            pawn.GunGameImmunityColor = Color.FromArgb(p.Immunity);
            Changed(pawn, "CCSPlayerPawn", "m_GunGameImmunityColor");
        }
    }

    /// <summary>A field written here goes to the clients at the next snapshot - when the engine networks it, which is looked up once per field
    /// (Utilities.SetStateChanged logs a warning for every call on a field that is not).</summary>
    void Changed(CBaseEntity entity, string className, string field)
    {
        var key = className + "." + field;
        if (!networked.TryGetValue(key, out var yes))
        {
            try { yes = Schema.IsSchemaFieldNetworked(className, field); } catch (Exception) { yes = false; }
            networked[key] = yes;
            if (!yes) log.LogWarning("Over Strike 2 cloak: {Field} is not networked on this build: written on the server only, so it hides nothing", key);
        }
        if (yes) Utilities.SetStateChanged(entity, className, field);
    }

    /// <summary>The log line of a player just hidden: why, and what was hidden.</summary>
    void LogHidden(int slot, string because)
    {
        var held = book.Held(slot).Select(p => p.Name).ToList();
        log.LogInformation("Over Strike 2 cloak: {Name} hidden ({Why}: {Because}) - {Count} entities: {Held}",
            Utilities.GetPlayerFromSlot(slot)?.PlayerName ?? $"slot {slot}", Why(slot), because, held.Count, string.Join(", ", held));
    }

    // ---- os_cloak: the console's look at the cloak, and a hide to look at from another player's game (the client harness cannot shoot, so
    // it cannot make a ghost) ----

    void OnCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return; // the server console and RCON only
        var verb = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "";
        switch (verb)
        {
            case "hide":
            {
                if (Find(command.ArgCount > 2 ? command.GetArg(2) : "") is not { } player) { command.ReplyToCommand($"{Command}: hide <account | #slot> [seconds]: no such player"); return; }
                var seconds = command.ArgCount > 3 && double.TryParse(command.GetArg(3), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 1, 600) : TestSeconds;
                var slot = player.Slot;
                if (!Want(slot, CloakWhy.Test, true, $"{Command} hide, {seconds:0} s")) { command.ReplyToCommand($"{Command}: {player.PlayerName} is not alive"); return; }
                var serial = tests[slot] = ++testSerial;
                plugin.AddTimer((float)seconds, () =>
                {
                    if (tests.GetValueOrDefault(slot) != serial) return;
                    tests.Remove(slot);
                    Want(slot, CloakWhy.Test, false, $"{Command} hide's {seconds:0} s are over");
                }, TimerFlags.STOP_ON_MAPCHANGE);
                command.ReplyToCommand($"{Command}: {player.PlayerName} hidden for {seconds:0} s");
                return;
            }
            case "show":
            {
                if (Find(command.ArgCount > 2 ? command.GetArg(2) : "") is not { } player) { command.ReplyToCommand($"{Command}: show <account | #slot>: no such player"); return; }
                tests.Remove(player.Slot);
                Want(player.Slot, CloakWhy.Test, false, $"{Command} show");
                command.ReplyToCommand($"{Command}: {player.PlayerName} - {(Why(player.Slot) == CloakWhy.None ? "seen" : $"still hidden ({Why(player.Slot)})")}");
                return;
            }
            default:
                command.ReplyToCommand(Status());
                return;
        }
    }

    /// <summary>A player by account id, [U:1:n], SteamID64, or #slot (bots too).</summary>
    static CCSPlayerController? Find(string arg)
    {
        var players = Utilities.GetPlayers().Where(p => p is { IsValid: true, IsHLTV: false }).ToList();
        if (arg.StartsWith('#')) return int.TryParse(arg[1..], out var slot) ? players.FirstOrDefault(p => p.Slot == slot) : null;
        var id = Hud.AccountId(arg);
        return id < 0 ? null : players.FirstOrDefault(p => !p.IsBot && Hud.AccountId(p.SteamID.ToString()) == id);
    }

    string Status()
    {
        var lines = new List<string> { $"{Command}: {why.Count} hidden, {book.Count} entities painted" };
        foreach (var p in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsHLTV: false }))
            lines.Add($"  #{p.Slot} {p.PlayerName}: {(Why(p.Slot) == CloakWhy.None ? "seen" : $"hidden ({Why(p.Slot)}), {book.Of(p.Slot)} entities")}{(About?.Invoke(p) is { Length: > 0 } a ? $"; spawn cloak: {a}" : "")}");
        return string.Join("\n", lines);
    }

    /// <summary>Compiles the cloak's methods at load, so a player's first spawn, kill or shot does not pay for the JIT inside their callback.</summary>
    void WarmUp()
    {
        var clock = Stopwatch.StartNew();
        var count = 0;
        foreach (var type in new[] { typeof(Cloak), typeof(SpawnCloak), typeof(SpawnCloakPlan), book.GetType() })
            foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.IsAbstract || m.ContainsGenericParameters) continue;
                try { RuntimeHelpers.PrepareMethod(m.MethodHandle); count++; } catch (Exception) { } // compiled on first use instead
            }
        log.LogInformation("Over Strike 2 cloak: {Count} methods compiled ahead in {Ms} ms; Ghost and the spawn cloak hide a player's model, every weapon they carry, wearables and shadows ({Command} for the console)",
            count, clock.ElapsedMilliseconds, Command);
    }
}

/// <summary>The spawn cloak in the game; the rule is SpawnCloakPlan's (CloakPlan.cs). A player whose menu window is open while they are on their
/// spawn is hidden by the cloak while it stays open, in every game mode, Wingman too - not when they leave the spawn, attack or deal damage, nor
/// while a vote freeze is on, and leaving, attacking or a freeze's start or end spend it until the next spawn. A spawn with the window shut is a
/// normal spawn. Damage and death do not close the window, so a player killed with it open respawns with it open and is hidden again at that
/// respawn. While the window is open there is no other protection: the vote's freeze still protects everybody during a vote, and the game's own
/// spawn protection runs as ever.
///
/// The window is the HUD's (HudScreen.WindowOpen, handed in by the plugin) and the freeze the vote's (VoteFreeze.Frozen). The events only note (a
/// spawn, a death, a shot, a throw, a hit); the look at every player still on their spawn is the cloak's pass, every tick after the entities'
/// think, so the window's closing, a step off the spawn and an attack end the hide in the tick they happen.</summary>
sealed class SpawnCloak(BasePlugin plugin, ILogger log, Cloak cloak, Func<int, bool> windowOpen, Func<bool> frozen)
{
    readonly SpawnCloakPlan plan = new();
    readonly List<int> look = new();
    readonly List<int> hidden = new();

    public void Load()
    {
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        plugin.RegisterEventHandler<EventPlayerDeath>((ev, _) => { if (ev.Userid is { IsValid: true } p) plan.Ended(p.Slot); return HookResult.Continue; });
        plugin.RegisterEventHandler<EventPlayerDisconnect>((ev, _) => { if (ev.Userid is { IsValid: true } p) plan.Ended(p.Slot); return HookResult.Continue; });
        plugin.RegisterEventHandler<EventPlayerHurt>(OnHurt);
        plugin.RegisterEventHandler<EventWeaponFire>((ev, _) => { if (ev.Userid is { IsValid: true } p) plan.Spend(p.Slot, SpawnCloakPlan.Attacked); return HookResult.Continue; });
        plugin.RegisterEventHandler<EventGrenadeThrown>((ev, _) => { if (ev.Userid is { IsValid: true } p) plan.Spend(p.Slot, SpawnCloakPlan.Attacked); return HookResult.Continue; });
        plugin.RegisterListener<Listeners.OnMapStart>(_ => plan.MapStarted());
        cloak.BeforePass += Pass;
        cloak.About = About;
        log.LogInformation("Over Strike 2 spawn cloak: the menu window open on the spawn ({Radius} units sideways, {Height} up or down) hides its player, in every mode",
            SpawnCloakPlan.Radius, SpawnCloakPlan.Height);
    }

    HookResult OnSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        if (ev.Userid is { IsValid: true, IsBot: false } p) plan.Spawned(p.Slot, Server.TickCount); // bots never open the window
        return HookResult.Continue;
    }

    /// <summary>Damage dealt to anybody else spends the attacker's cloak (a grenade of their last life's too).</summary>
    HookResult OnHurt(EventPlayerHurt ev, GameEventInfo info)
    {
        if (ev.Attacker is { IsValid: true } attacker && ev.Userid is { IsValid: true } victim && attacker.Slot != victim.Slot) plan.Spend(attacker.Slot, SpawnCloakPlan.Hurt);
        return HookResult.Continue;
    }

    /// <summary>Before the cloak's pass, every tick: a freeze's start or end first, then every life still on its spawn, then every player the spawn
    /// cloak hides whose life has been spent or is over since.</summary>
    void Pass()
    {
        var freeze = frozen();
        plan.Freeze(freeze);
        var allowed = !freeze;
        var tick = Server.TickCount;
        plan.Unspent(look);
        foreach (var slot in look)
        {
            if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true } p || p.PlayerPawn.Value is not { IsValid: true } pawn
                || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE || pawn.AbsOrigin is not { } at) continue;
            var open = windowOpen(slot);
            var hides = plan.Hides(slot, tick, at.X, at.Y, at.Z, open, allowed);
            var hiding = (cloak.Why(slot) & CloakWhy.Spawn) != 0;
            if (hides && !hiding) cloak.Want(slot, CloakWhy.Spawn, true, "the menu window is open on their spawn");
            else if (!hides && hiding) cloak.Want(slot, CloakWhy.Spawn, false, plan.SpentBecause(slot) ?? (open ? "the cloak is not allowed now" : "the menu window closed"));
        }
        cloak.Hidden(CloakWhy.Spawn, hidden);
        foreach (var slot in hidden)
            if (!plan.Ready(slot)) cloak.Want(slot, CloakWhy.Spawn, false, plan.SpentBecause(slot) ?? "their life ended");
    }

    string About(CCSPlayerController p) =>
        p.IsBot ? "" : p.PlayerPawn.Value?.AbsOrigin is { } at ? plan.Describe(p.Slot, at.X, at.Y, at.Z) + (windowOpen(p.Slot) ? ", window open" : "") : "no pawn";
}
