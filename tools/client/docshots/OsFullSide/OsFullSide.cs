using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace OsFullSide;

/// <summary>Throwaway test plugin for the lab (os2-deploy docshots full): loaded from /tmp on the INSECURE TEST COPY only, never copied to the plugins folder and
/// gone with the copy. It plays a join onto a side full of humans with the one game account the lab has. The game holds a side at its spawn point count
/// (CCSGameRules.m_iSpawnPointCount_Terrorist and _CT): a join to a side whose players are that many or more is refused - "Team is full", the event
/// jointeam_failed - unless a bot on it leaves instead (bot_auto_vacate). osf_full puts a side's count at the humans on it now, so the next human join to it
/// is refused as on a side full of humans, and Over Strike 2's own handler of that event (SpawnRoom, in Wingman) makes the room and logs "spawn room (a join
/// was refused)". It writes that one number, once - the field SpawnRoom raises - and changes nothing else: a round start or any player's side change, where
/// SpawnRoom makes the room again, puts the count back, so the join comes right after it. Server console only:
///
///   osf_full ct|t     that side's count put at the humans on it: the next join to it is refused
///   osf_full          each side: its humans and bots, the game's count, its spawn points on
///
/// A refused join is logged with each side as it stands then, and again two frames on, after SpawnRoom's room (its next frame).</summary>
public class OsFullSide : BasePlugin
{
    public override string ModuleName => "OsFullSide";
    public override string ModuleVersion => "1.0.0";

    public const string Command = "osf_full";

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventJointeamFailed>((ev, _) =>
        {
            Logger.LogInformation("OsFullSide: a join was refused - {Who}, reason {Reason}: {Sides}", Who(ev.Userid), ev.Reason, Sides());
            Server.NextFrame(() => Server.NextFrame(() => Logger.LogInformation("OsFullSide: two frames after the refusal: {Sides}", Sides())));
            return HookResult.Continue;
        });
    }

    static string Who(CCSPlayerController? p) => p is { IsValid: true } ? $"{p.PlayerName} ({p.SteamID & 0xFFFFFFFF})" : "?";

    static CCSGameRules? Rules() => Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;

    /// <summary>A side's players now: its humans and its bots.</summary>
    static (int Humans, int Bots) OnSide(CsTeam team)
    {
        var on = Utilities.GetPlayers().Where(p => p is { IsValid: true, IsHLTV: false } && p.Team == team).ToList();
        return (on.Count(p => !p.IsBot), on.Count(p => p.IsBot));
    }

    static string Sides()
    {
        var rules = Rules();
        return string.Join("; ", FullSideRule.Sides.Select(s =>
        {
            var (humans, bots) = OnSide(s.Team);
            var spots = Utilities.FindAllEntitiesByDesignerName<SpawnPoint>(s.Class).Count(p => p.IsValid && p.Enabled);
            var count = rules == null ? "?" : $"{(s.Team == CsTeam.Terrorist ? rules.SpawnPointCount_Terrorist : rules.SpawnPointCount_CT)}";
            return $"{s.Name} {humans} human(s), {bots} bot(s), the game's count {count}, {spots} spawn point(s) on";
        }));
    }

    [ConsoleCommand(Command, "OsFullSide: osf_full [ct|t]: that side's spawn count put at the humans on it - its next join is refused")]
    [CommandHelper(minArgs: 0, usage: "[ct|t]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnFull(CCSPlayerController? caller, CommandInfo command)
    {
        if (command.ArgCount < 2) { command.ReplyToCommand($"{Command}: {Sides()}"); return; }
        if (FullSideRule.SideOf(command.GetArg(1)) is not { } side) { command.ReplyToCommand($"{Command}: ct or t, not {command.GetArg(1)}"); return; }
        if (Rules() is not { } rules) { command.ReplyToCommand($"{Command}: no game rules - no map running"); return; }
        var (humans, bots) = OnSide(side.Team);
        ref var count = ref (side.Team == CsTeam.Terrorist ? ref rules.SpawnPointCount_Terrorist : ref rules.SpawnPointCount_CT);
        var was = count;
        count = FullSideRule.FullCount(humans);
        var said = $"{Command}: {side.Name}'s spawn count {was} -> {count}, its {humans} human(s) - {FullSideRule.Next(humans, bots, count)}";
        Logger.LogInformation("OsFullSide: {Said}", said);
        command.ReplyToCommand(said);
    }
}

/// <summary>osf_full's rule, apart from the game (its self-test runs it): the count it writes, and what the game does with the next join to that side.</summary>
public static class FullSideRule
{
    public static readonly (CsTeam Team, string Class, string Name)[] Sides =
        [(CsTeam.Terrorist, "info_player_terrorist", "T"), (CsTeam.CounterTerrorist, "info_player_counterterrorist", "CT")];

    public static (CsTeam Team, string Class, string Name)? SideOf(string word) => word.Trim().ToLowerInvariant() switch
    {
        "ct" or "3" or "counterterrorist" or "counter-terrorist" => Sides[1],
        "t" or "2" or "terrorist" => Sides[0],
        _ => null,
    };

    /// <summary>The count of a side full of its humans: as many spawn points as humans on it.</summary>
    public static int FullCount(int humans) => Math.Max(0, humans);

    /// <summary>The game's check of a join (CCSGameRules::TeamFull): refused when the side's players are as many as its count or more - unless a bot on it
    /// leaves instead (bot_auto_vacate, on by default), and the join goes through.</summary>
    public static bool Refused(int humans, int bots, int count, bool botLeaves = true) => humans + bots >= count && !(botLeaves && bots > 0);

    /// <summary>What the next human join to the side meets, in words.</summary>
    public static string Next(int humans, int bots, int count) =>
        Refused(humans, bots, count) ? "the next human join to it is refused (Team is full), and Over Strike 2's SpawnRoom makes room (in Wingman)"
        : bots > 0 ? $"but {bots} bot(s) on it: the game kicks one instead of refusing the join (bot_auto_vacate) - kick the side's bots first, then again"
        : "the next human join to it goes through";
}
