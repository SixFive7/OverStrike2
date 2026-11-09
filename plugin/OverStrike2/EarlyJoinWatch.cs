using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The fairness restart in the game: the rule is EarlyJoin's (../../bridge/Shared/EarlyJoin.cs) - in Arms Race and Deathmatch, a player who comes into
/// a match during its first minute starts it over, once per map, as soon as the last player of the map before has spawned too, at most when the minute is up.
/// This feeds the plan the game - the round starts out of warmup with the humans on a team, every human's spawn, a real departure (not the reconnect of a map
/// change or of the HUD addon's download) - and when the plan asks, tells the bridge (EarlyJoin.Command on /act, for the last player in), whose Restart match
/// counts it down and sends the plugin's own os_restart, which the restart guard lets through. Bots never count. A plugin loaded mid-map does nothing on that
/// map: it cannot tell who started its match. os_fair says where the map stands; os_fair live, the client harness's, takes this moment as the match going live
/// with whoever is on a team now (none too), so that a lone test game's spawn is a late join.</summary>
sealed class EarlyJoinWatch(BasePlugin plugin, ILogger log, Func<string> modeId, Func<long, string, bool> ask)
{
    public const string Command = "os_fair";

    readonly EarlyJoin plan = new();
    bool forced; // os_fair live: the next look takes the match as live, nobody on a team required

    public void Load(bool hotReload)
    {
        plugin.RegisterListener<Listeners.OnMapStart>(_ => plan.MapStarted());
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect);
        plugin.AddTimer(1f, Tick, TimerFlags.REPEAT);
        plugin.AddCommand(Command, "Over Strike 2 fairness restart: status | live (the match goes live now, for the client harness)", OnCommand);
        if (hotReload) plan.Skip("the plugin loaded during this map");
        log.LogInformation("Over Strike 2 fairness: in Arms Race and Deathmatch a player who comes in during a match's first {Window} s restarts it once per map, after the map before's players are in ({Command})",
            EarlyJoin.WindowSeconds, Command);
    }

    static long Account(CCSPlayerController p) => Hud.AccountId(p.SteamID.ToString());

    static bool Human(CCSPlayerController? p) => p is { IsValid: true, IsBot: false, IsHLTV: false };

    static bool OnTeam(CCSPlayerController p) => p.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist;

    static bool Warmup() => Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules?.WarmupPeriod ?? false;

    HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
    {
        try
        {
            if (Warmup()) return HookResult.Continue;
            var onTeam = Utilities.GetPlayers().Where(p => Human(p) && OnTeam(p)).Select(Account).Where(a => a > 0).ToList();
            if (onTeam.Count == 0 && !forced) return HookResult.Continue;
            Live(onTeam, "the match went live");
        }
        catch (Exception e) { log.LogError("Over Strike 2 fairness: the round start failed: {Error}", e.Message); }
        return HookResult.Continue;
    }

    void Live(List<long> onTeam, string why)
    {
        var mode = modeId();
        if (forced && onTeam.Count == 0) onTeam = [0]; // os_fair live with nobody on a team: a stand-in, so the plan takes the moment
        forced = false;
        if (plan.Live(DateTime.UtcNow, onTeam, EarlyJoin.Applies(mode)))
            log.LogInformation("Over Strike 2 fairness: {Why} with {Count} human(s) on a team; {Rule}", why, onTeam.Count(a => a > 0),
                EarlyJoin.Applies(mode) ? $"a late join in the next {EarlyJoin.WindowSeconds} s restarts it, {plan.Waiting} of the map before still loading" : $"not in {mode}");
    }

    HookResult OnSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        if (ev.Userid is not { } p || !Human(p) || !OnTeam(p)) return HookResult.Continue;
        var (account, name) = (Account(p), p.PlayerName);
        if (account > 0 && plan.Spawned(account, name, DateTime.UtcNow))
            Server.NextFrame(() => log.LogInformation("Over Strike 2 fairness: {Name} came in late; {Waiting} of the map before still loading", name, plan.Waiting));
        return HookResult.Continue;
    }

    /// <summary>A map change disconnects everybody (NETWORK_DISCONNECT_SHUTDOWN and the loop's own reasons), and the HUD addon's download makes a joining game
    /// reconnect (LOOPSHUTDOWN): neither is leaving. Any other reason is.</summary>
    static bool Reconnect(int reason)
    {
        var name = Enum.IsDefined(typeof(NetworkDisconnectionReason), reason) ? ((NetworkDisconnectionReason)reason).ToString() : "";
        return name.Contains("SHUTDOWN", StringComparison.Ordinal) || name.StartsWith("NETWORK_DISCONNECT_LOOP", StringComparison.Ordinal);
    }

    HookResult OnDisconnect(EventPlayerDisconnect ev, GameEventInfo info)
    {
        if (ev.Userid is { } p && Human(p) && !Reconnect(ev.Reason)) plan.Left(Account(p));
        return HookResult.Continue;
    }

    void Tick()
    {
        try
        {
            if (plan.Due(DateTime.UtcNow) is not { } who) return;
            var told = ask(who.Account, who.Name);
            log.LogInformation("Over Strike 2 fairness: the match restarts for fairness - {Name} is in{Told}", who.Name, told ? "; the bridge counts it down" : " - but there is no bridge to count it down, so nothing happens");
        }
        catch (Exception e) { log.LogError("Over Strike 2 fairness: the ask failed: {Error}", e.Message); }
    }

    void OnCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return; // the server console and RCON only
        if (command.ArgCount > 1 && command.GetArg(1).Equals("live", StringComparison.OrdinalIgnoreCase))
        {
            forced = true;
            Live(Utilities.GetPlayers().Where(p => Human(p) && OnTeam(p)).Select(Account).Where(a => a > 0).ToList(), $"{Command} live");
            command.ReplyToCommand($"{Command}: {plan.Describe(DateTime.UtcNow)}");
            return;
        }
        command.ReplyToCommand($"{Command}: {plan.Describe(DateTime.UtcNow)}");
    }
}
