using System.Diagnostics;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The live lab's probe for the spawn invisibility (the owner's 244 A, his answers of 2026-11-10): console verbs that put a player in
/// front of another, turn a player's view, log a player's spotted state as the game computes it - the radar's, m_entitySpottedState's m_bSpotted
/// and m_bSpottedByMask (a bit per slot of whoever has them spotted) - clear it after reading, the radar's mitigation to try, and log a player's
/// footstep events. The server console and RCON only; nothing happens until told, and a new map ends every watch.
///
///   os_probe place &lt;#slot&gt; &lt;account | #slot&gt; &lt;units&gt; [face | away]   the first player put that far ahead of the second, on the second's
///                                                                     view line, facing them or away
///   os_probe look &lt;account | #slot&gt; at &lt;#slot&gt;                         the first's view turned onto the second's chest, in one step
///   os_probe look &lt;account | #slot&gt; yaw &lt;deg | +deg | -deg&gt;            the view turned to a yaw, or by one, level
///   os_probe spotted &lt;#slot&gt; on | off                                    every change of that player's spotted state logged, with ms and tick,
///                                                                     read at the frame's start (OnTick) and after the entities' think
///   os_probe clearspotted &lt;#slot&gt; off | spotted | mask | both [think | tick | transmit]
///                                                                     after each read, the field(s) cleared for the clients, at that point of
///                                                                     the tick (think: after the entities' think, the default); "seen" is then
///                                                                     what the reads before each clear say, held SeenTicks after the last
///   os_probe footsteps &lt;#slot&gt; on | off                                  that player's footstep events logged
///   os_probe respawn &lt;account | #slot&gt; [here]                           that player spawned again - a new life, as the game gives one - and with
///                                                                     "here" put back where they stood, facing as they faced: a watched spawn, at will
///   os_probe status                                                     the watches, the field offsets, every watched player's state now
///
/// Each line says "t=" milliseconds since the plugin loaded and the server tick, so a snap of the view (look) and the spotted state's change
/// can be set side by side.</summary>
sealed class InvisProbe(BasePlugin plugin, ILogger log)
{
    public const string Command = "os_probe";

    /// <summary>How long "seen" holds after the last read that found the player spotted, while the clear is on: the game recomputes the spotted
    /// state about twice a second (radar), so a cleared field reads empty between its updates.</summary>
    public const int SeenTicks = 40;

    const double ChestHeight = 50;   // a player's chest above their feet (the hull is 72 high)
    const double Lift = 16;          // a placed player starts this high, and drops onto the floor

    sealed class Watch
    {
        public bool Spotted, Steps;
        public string Clear = "off", ClearAt = "think";
        public (bool Read, bool Spotted, uint M0, uint M1) AtTick, AtThink;
        public long LastFound = -1;  // the last tick a read before the clear found the player spotted
        public bool Seen;
    }

    readonly Dictionary<int, Watch> watches = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    long transmitTick = -1;
    int faults;

    public void Load()
    {
        plugin.AddCommand(Command, "Over Strike 2 lab probe: place | look | spotted | clearspotted | footsteps | respawn | status (the console only)", OnCommand);
        plugin.RegisterListener<Listeners.OnTick>(() => Pass("tick"));
        plugin.RegisterListener<Listeners.OnServerPostEntityThink>(() => Pass("think"));
        plugin.RegisterListener<Listeners.CheckTransmit>(_ =>
        {
            if (Server.TickCount == transmitTick) return; // once a tick, at its first client
            transmitTick = Server.TickCount;
            Pass("transmit");
        });
        plugin.RegisterEventHandler<EventPlayerFootstep>(OnFootstep);
        plugin.RegisterListener<Listeners.OnMapStart>(_ => { if (watches.Count > 0) Log("a new map: every watch ends"); watches.Clear(); });
    }

    string Stamp => $"t={clock.ElapsedMilliseconds} ms, tick {Server.TickCount}";

    void Log(string line) => log.LogInformation("Over Strike 2 probe: {Stamp}: {Line}", Stamp, line);

    // ---- the reads, the clear ----

    void Pass(string at)
    {
        if (watches.Count == 0) return;
        try
        {
            foreach (var (slot, w) in watches)
            {
                if (!w.Spotted && w.Clear == "off") continue;
                if (Pawn(slot) is not { } pawn) continue;
                var state = pawn.EntitySpottedState;
                var mask = state.SpottedByMask;
                var now = (Read: true, Spotted: state.Spotted, M0: mask[0], M1: mask.Length > 1 ? mask[1] : 0u);
                if (at != "transmit")
                {
                    ref var last = ref (at == "tick" ? ref w.AtTick : ref w.AtThink);
                    if (w.Spotted && now != last)
                        Log($"#{slot} {Utilities.GetPlayerFromSlot(slot)?.PlayerName}: spotted ({at}) {now.Spotted}, by {Slots(now.M0, now.M1)} (mask {now.M1:X8}:{now.M0:X8})");
                    last = now;
                }
                if (w.Clear == "off") continue;
                var tick = Server.TickCount;
                if (now.Spotted || now.M0 != 0 || now.M1 != 0) w.LastFound = tick;
                var seen = w.LastFound >= 0 && tick - w.LastFound <= SeenTicks;
                if (seen != w.Seen) { w.Seen = seen; Log($"#{slot}: seen {(seen ? "yes" : "no")} (the reads before the clear, held {SeenTicks} ticks)"); }
                if (at != w.ClearAt) continue;
                var offset = Schema.GetSchemaOffset("CCSPlayerPawn", "m_entitySpottedState");
                if (w.Clear is "spotted" or "both" && state.Spotted)
                {
                    state.Spotted = false;
                    Utilities.SetStateChanged(pawn, "EntitySpottedState_t", "m_bSpotted", offset);
                }
                if (w.Clear is "mask" or "both" && (mask[0] != 0 || (mask.Length > 1 && mask[1] != 0)))
                {
                    for (var i = 0; i < mask.Length; i++) mask[i] = 0;
                    Utilities.SetStateChanged(pawn, "EntitySpottedState_t", "m_bSpottedByMask", offset);
                }
            }
        }
        catch (Exception e)
        {
            if (++faults <= 3) log.LogError(e, "Over Strike 2 probe: error in the pass ({At})", at);
        }
    }

    static string Slots(uint m0, uint m1)
    {
        var list = new List<string>();
        for (var i = 0; i < 32; i++) if ((m0 & (1u << i)) != 0) list.Add($"#{i}");
        for (var i = 0; i < 32; i++) if ((m1 & (1u << i)) != 0) list.Add($"#{32 + i}");
        return list.Count == 0 ? "nobody" : string.Join(" ", list);
    }

    HookResult OnFootstep(EventPlayerFootstep ev, GameEventInfo info)
    {
        if (ev.Userid is { IsValid: true } p && watches.TryGetValue(p.Slot, out var w) && w.Steps && p.PlayerPawn.Value?.AbsOrigin is { } at)
            Log($"#{p.Slot} {p.PlayerName}: footstep at {at.X:0} {at.Y:0} {at.Z:0}");
        return HookResult.Continue;
    }

    // ---- the verbs ----

    void OnCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return; // the server console and RCON only
        string Arg(int i) => command.ArgCount > i ? command.GetArg(i) : "";
        void Reply(string text) { command.ReplyToCommand($"{Command}: {text}"); log.LogInformation("Over Strike 2 probe: {Stamp}: {Text}", Stamp, text); }
        try
        {
            switch (Arg(1).ToLowerInvariant())
            {
                case "place":
                {
                    if (Find(Arg(2)) is not { } who || Find(Arg(3)) is not { } viewer) { Reply("place <#slot> <account | #slot> <units> [face | away]: no such player"); return; }
                    if (Pawn(who.Slot) is not { } pawn || Pawn(viewer.Slot) is not { } eye || eye.AbsOrigin is not { } from) { Reply("place: both must be alive"); return; }
                    var units = double.TryParse(Arg(4), NumberStyles.Float, CultureInfo.InvariantCulture, out var u) ? Math.Clamp(u, 16, 8192) : 300;
                    var yaw = eye.EyeAngles.Y;
                    var rad = yaw * Math.PI / 180;
                    var (x, y, z) = (from.X + units * Math.Cos(rad), from.Y + units * Math.Sin(rad), from.Z + Lift);
                    var facing = Arg(5).Equals("away", StringComparison.OrdinalIgnoreCase) ? yaw : yaw + 180;
                    pawn.Teleport(new Vector((float)x, (float)y, (float)z), new QAngle(0, (float)Normal(facing), 0), new Vector(0, 0, 0));
                    Reply($"#{who.Slot} {who.PlayerName} placed {units:0} units ahead of #{viewer.Slot} {viewer.PlayerName} (yaw {yaw:0.0}), at {x:0} {y:0} {z:0}, facing {(facing == yaw ? "away" : "them")}");
                    return;
                }
                case "look":
                {
                    if (Find(Arg(2)) is not { } viewer || Pawn(viewer.Slot) is not { } eye || eye.AbsOrigin is not { } from) { Reply("look <account | #slot> at <#slot> | yaw <deg | +deg | -deg>: no such living player"); return; }
                    double pitch, yaw;
                    var before = eye.EyeAngles.Y;
                    if (Arg(3).Equals("at", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Find(Arg(4)) is not { } target || Pawn(target.Slot)?.AbsOrigin is not { } to) { Reply("look ... at <#slot>: no such living player"); return; }
                        var (dx, dy, dz) = (to.X - from.X, to.Y - from.Y, to.Z + ChestHeight - (from.Z + eye.ViewOffset.Z));
                        yaw = Math.Atan2(dy, dx) * 180 / Math.PI;
                        pitch = -Math.Atan2(dz, Math.Sqrt(dx * dx + dy * dy)) * 180 / Math.PI;
                    }
                    else if (Arg(3).Equals("yaw", StringComparison.OrdinalIgnoreCase) && double.TryParse(Arg(4), NumberStyles.Float, CultureInfo.InvariantCulture, out var deg))
                    {
                        yaw = Arg(4).StartsWith('+') || Arg(4).StartsWith('-') ? before + deg : deg;
                        pitch = 0;
                    }
                    else { Reply("look <account | #slot> at <#slot> | yaw <deg | +deg | -deg>"); return; }
                    eye.Teleport((Vector?)null, new QAngle((float)pitch, (float)Normal(yaw), 0), (Vector?)null);
                    Reply($"#{viewer.Slot} {viewer.PlayerName}'s view snapped from yaw {before:0.0} to yaw {Normal(yaw):0.0}, pitch {pitch:0.0}");
                    return;
                }
                case "spotted":
                case "footsteps":
                {
                    if (Find(Arg(2)) is not { } who) { Reply($"{Arg(1)} <#slot> on | off: no such player"); return; }
                    var on = !Arg(3).Equals("off", StringComparison.OrdinalIgnoreCase);
                    var w = watches.TryGetValue(who.Slot, out var had) ? had : watches[who.Slot] = new Watch();
                    if (Arg(1).Equals("spotted", StringComparison.OrdinalIgnoreCase)) { w.Spotted = on; w.AtTick = w.AtThink = default; }
                    else w.Steps = on;
                    Forget(who.Slot, w);
                    Reply($"#{who.Slot} {who.PlayerName}: {Arg(1).ToLowerInvariant()} {(on ? "logged" : "no longer logged")}");
                    return;
                }
                case "respawn":
                {
                    if (Find(Arg(2)) is not { } who) { Reply("respawn <account | #slot> [here]: no such player"); return; }
                    // where they stand now, copied: the pawn is the same entity after the respawn, and its position moves with it
                    var here = Arg(3).Equals("here", StringComparison.OrdinalIgnoreCase) && Pawn(who.Slot) is { AbsOrigin: { } at } before
                        ? (Pos: new Vector(at.X, at.Y, at.Z), Look: new QAngle(before.EyeAngles.X, before.EyeAngles.Y, 0)) : ((Vector Pos, QAngle Look)?)null;
                    who.Respawn();
                    // in the same tick, before any look at the new life: its spawn point is taken here
                    if (here is { } h && who.PlayerPawn.Value is { IsValid: true } pawn) pawn.Teleport(h.Pos, h.Look, new Vector(0, 0, 0));
                    Reply($"#{who.Slot} {who.PlayerName} spawned again{(here is { } w ? $", put back at {w.Pos.X:0} {w.Pos.Y:0} {w.Pos.Z:0}" : "")}");
                    return;
                }
                case "clearspotted":
                {
                    if (Find(Arg(2)) is not { } who) { Reply("clearspotted <#slot> off | spotted | mask | both [think | tick | transmit]: no such player"); return; }
                    var mode = Arg(3).ToLowerInvariant();
                    if (mode is not ("off" or "spotted" or "mask" or "both")) { Reply("clearspotted <#slot> off | spotted | mask | both [think | tick | transmit]"); return; }
                    var where = Arg(4).ToLowerInvariant() is "tick" or "transmit" ? Arg(4).ToLowerInvariant() : "think";
                    var w = watches.TryGetValue(who.Slot, out var had) ? had : watches[who.Slot] = new Watch();
                    (w.Clear, w.ClearAt, w.LastFound, w.Seen) = (mode, where, -1, false);
                    Forget(who.Slot, w);
                    Reply($"#{who.Slot} {who.PlayerName}: spotted state {(mode == "off" ? "no longer cleared" : $"cleared ({mode}) at {where}, after each read")}");
                    return;
                }
                default:
                    Reply(Status());
                    return;
            }
        }
        catch (Exception e)
        {
            Reply($"error: {e.Message}");
        }
    }

    /// <summary>A watch with nothing left on goes.</summary>
    void Forget(int slot, Watch w)
    {
        if (!w.Spotted && !w.Steps && w.Clear == "off") watches.Remove(slot);
    }

    string Status()
    {
        var lines = new List<string>
        {
            $"{watches.Count} watched; m_entitySpottedState at {Schema.GetSchemaOffset("CCSPlayerPawn", "m_entitySpottedState")}, m_bSpotted +{Schema.GetSchemaOffset("EntitySpottedState_t", "m_bSpotted")}, m_bSpottedByMask +{Schema.GetSchemaOffset("EntitySpottedState_t", "m_bSpottedByMask")}",
        };
        foreach (var (slot, w) in watches)
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            var now = Pawn(slot) is { } pawn ? $"spotted {pawn.EntitySpottedState.Spotted}, by {Slots(pawn.EntitySpottedState.SpottedByMask[0], pawn.EntitySpottedState.SpottedByMask.Length > 1 ? pawn.EntitySpottedState.SpottedByMask[1] : 0)}" : "not alive";
            lines.Add($"  #{slot} {p?.PlayerName}: {now}; spotted logged {w.Spotted}, cleared {w.Clear} at {w.ClearAt}, seen {w.Seen}, footsteps {w.Steps}");
        }
        return string.Join("\n", lines);
    }

    static double Normal(double yaw) => ((yaw % 360) + 540) % 360 - 180;

    static CCSPlayerPawn? Pawn(int slot) =>
        Utilities.GetPlayerFromSlot(slot) is { IsValid: true } p && p.PlayerPawn.Value is { IsValid: true } pawn && pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE ? pawn : null;

    /// <summary>A player by account id, [U:1:n], SteamID64, or #slot (bots too).</summary>
    static CCSPlayerController? Find(string arg)
    {
        var players = Utilities.GetPlayers().Where(p => p is { IsValid: true, IsHLTV: false }).ToList();
        if (arg.StartsWith('#')) return int.TryParse(arg[1..], out var slot) ? players.FirstOrDefault(p => p.Slot == slot) : null;
        var id = Hud.AccountId(arg);
        return id < 0 ? null : players.FirstOrDefault(p => !p.IsBot && Hud.AccountId(p.SteamID.ToString()) == id);
    }
}
