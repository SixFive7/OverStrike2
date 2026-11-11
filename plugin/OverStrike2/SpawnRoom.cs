using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>Room for any number of players on any Wingman map (the rule is SpawnRoomPlan's, SpawnRoomPlan.cs). The game holds a side at its spawn points - its
/// spawn point count (CCSGameRules.m_iSpawnPointCount_Terrorist and _CT) caps the side, and the bots get "Team is full" - so before every round, and after
/// every join, each side gets room for one more than it holds: the map's own spawn points that are off switched on, then new ones made beside the ones that
/// are on, and the game's count raised to what is on. In Wingman only; the other modes are as the maps make them. os_room says, per side, what the map has,
/// what is on, the game's count and its spawn lists, and who plays, and the map's deathmatch spawn points; os_room apply makes the room now.</summary>
sealed class SpawnRoom(BasePlugin plugin, ILogger log, Func<string> modeId)
{
    public const string Command = "os_room";

    static readonly (CsTeam Team, string Class, string Name)[] Sides =
        [(CsTeam.Terrorist, "info_player_terrorist", "T"), (CsTeam.CounterTerrorist, "info_player_counterterrorist", "CT")];

    int made;      // spawn points made on this map, for the log
    int faults;

    public void Load()
    {
        plugin.RegisterEventHandler<EventRoundPrestart>((_, _) => { Make("the round starts"); return HookResult.Continue; });
        // ...and again once the round has started: the game counts a side's spawn points itself as the round starts, which may leave out the ones made here
        plugin.RegisterEventHandler<EventRoundPoststart>((_, _) => { Make("the round started"); return HookResult.Continue; });
        // a join the game refused ("Team is full"): the room for the next try
        plugin.RegisterEventHandler<EventJointeamFailed>((_, _) => { Server.NextFrame(() => Make("a join was refused")); return HookResult.Continue; });
        plugin.RegisterEventHandler<EventPlayerTeam>((ev, _) =>
        {
            if (ev.Team is (int)CsTeam.Terrorist or (int)CsTeam.CounterTerrorist && !ev.Disconnect) Server.NextFrame(() => Make("a player joined a side"));
            return HookResult.Continue;
        });
        plugin.RegisterListener<Listeners.OnMapStart>(_ => { (made, faults) = (0, 0); plugin.AddTimer(1f, () => Make("the map loaded")); });
        plugin.AddCommand(Command, "Over Strike 2 spawn room: status | apply (the console only)", OnCommand);
    }

    static CCSGameRules? Rules() => Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;

    /// <summary>Each side's room, in Wingman: its spawn points on, made if need be, and the game's count raised to them.</summary>
    void Make(string why, bool always = false)
    {
        if (!always && modeId() != Modes.WingmanId) return;
        try
        {
            var rules = Rules();
            foreach (var (team, cls, name) in Sides)
            {
                var spots = Utilities.FindAllEntitiesByDesignerName<SpawnPoint>(cls).Where(s => s.IsValid).ToList();
                var onTeam = Utilities.GetPlayers().Count(p => p is { IsValid: true, IsHLTV: false } && p.Team == team);
                var needed = SpawnRoomPlan.Needed(onTeam, Server.MaxPlayers);
                var (enable, add) = SpawnRoomPlan.Plan(spots.Select(Spot).ToList(), needed);
                foreach (var i in enable)
                    if (spots.FirstOrDefault(s => (int)s.Index == i) is { } s) s.AcceptInput("SetEnabled");
                foreach (var (x, y, z, yaw) in add)
                {
                    if (Utilities.CreateEntityByName<SpawnPoint>(cls) is not { } point) continue;
                    point.Teleport(new Vector((float)x, (float)y, (float)z), new QAngle(0, (float)yaw, 0), new Vector(0, 0, 0));
                    point.DispatchSpawn();
                    point.AcceptInput("SetEnabled");
                    made++;
                }
                var on = Utilities.FindAllEntitiesByDesignerName<SpawnPoint>(cls).Count(s => s.IsValid && s.Enabled);
                var raised = false;
                if (rules != null)
                {
                    ref var count = ref (team == CsTeam.Terrorist ? ref rules.SpawnPointCount_Terrorist : ref rules.SpawnPointCount_CT);
                    if (count < on) { count = on; raised = true; }
                }
                if (enable.Count > 0 || add.Count > 0 || raised)
                    log.LogInformation("Over Strike 2 spawn room ({Why}): {Side} holds {OnTeam}, needs {Needed} - {Enabled} of the map's switched on, {Made} made, the game's count {Count}{Raised}",
                        why, name, onTeam, needed, enable.Count, add.Count, rules == null ? "?" : team == CsTeam.Terrorist ? rules.SpawnPointCount_Terrorist : rules.SpawnPointCount_CT,
                        raised ? " (raised)" : "");
            }
        }
        catch (Exception e)
        {
            if (faults++ < 3) log.LogError("Over Strike 2 spawn room: failed ({Why}): {Error}", why, e.Message);
        }
    }

    static SpawnSpot Spot(SpawnPoint s) =>
        new((int)s.Index, s.Enabled, s.AbsOrigin?.X ?? 0, s.AbsOrigin?.Y ?? 0, s.AbsOrigin?.Z ?? 0, s.AbsRotation?.Y ?? 0);

    void OnCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return; // the server console and RCON only
        if (command.ArgCount > 1 && command.GetArg(1).Equals("apply", StringComparison.OrdinalIgnoreCase)) Make($"{Command} apply", always: true);
        command.ReplyToCommand(Status());
    }

    string Status()
    {
        var rules = Rules();
        var lines = new List<string> { $"{Command}: {modeId()}, {Server.MaxPlayers} slots, {made} made on this map" };
        foreach (var (team, cls, name) in Sides)
        {
            var spots = Utilities.FindAllEntitiesByDesignerName<SpawnPoint>(cls).Where(s => s.IsValid).ToList();
            var onTeam = Utilities.GetPlayers().Count(p => p is { IsValid: true, IsHLTV: false } && p.Team == team);
            var (count, list, master) = rules == null ? ("?", "?", "?")
                : team == CsTeam.Terrorist ? ($"{rules.SpawnPointCount_Terrorist}", $"{rules.TerroristSpawnPoints.Count}", $"{rules.TerroristSpawnPointsMasterList.Count}")
                : ($"{rules.SpawnPointCount_CT}", $"{rules.CTSpawnPoints.Count}", $"{rules.CTSpawnPointsMasterList.Count}");
            lines.Add($"  {name}: {onTeam} on the side; {spots.Count} spawn points, {spots.Count(s => s.Enabled)} on; the game's count {count}, its list {list}, its master list {master}");
        }
        // the deathmatch spawn points, which random spawning (mp_randomspawn) may take instead of the sides' own
        var dm = Utilities.FindAllEntitiesByDesignerName<SpawnPoint>("info_deathmatch_spawn").Where(s => s.IsValid).ToList();
        lines.Add($"  deathmatch: {dm.Count} info_deathmatch_spawn, {dm.Count(s => s.Enabled)} on");
        return string.Join("\n", lines);
    }
}
