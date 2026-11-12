using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The fairness restart in the game: the rule is EarlyJoin's (../../bridge/Shared/EarlyJoin.cs) - in every mode, every human who joins a match while
/// somebody else plays it and it has run less than a minute starts it over, as often as players join. This feeds the plan the game - a match's start (a round
/// start out of warmup with no round played yet, the restart's own included) with the humans on a team then, every human's spawn with the others on a team,
/// a real departure (not the reconnect of a map change or of the HUD addon's download) - and at once, for each join that asks, tells the bridge
/// (EarlyJoin.Command on /act, naming who came in), whose Restart match counts it down, folds every join that comes meanwhile into the same restart, and sends
/// the plugin's own os_restart, which the restart guard lets through. Bots never count. A plugin loaded mid-map does nothing on that map: it cannot tell who has
/// joined it. os_fair says where the map stands; os_fair live, the client harness's, starts the match running now and counts the next join as joining
/// somebody, so that a lone test game's spawn asks.</summary>
sealed class EarlyJoinWatch(BasePlugin plugin, ILogger log, Func<long, string, bool> ask)
{
    public const string Command = "os_fair";

    readonly EarlyJoin plan = new();

    public void Load(bool hotReload)
    {
        plugin.RegisterListener<Listeners.OnMapStart>(_ => plan.MapStarted());
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect);
        plugin.AddCommand(Command, "Over Strike 2 fairness restart: status | live (the match runs from now and the next join counts, for the client harness)", OnCommand);
        if (hotReload) plan.Skip("the plugin loaded during this map");
        log.LogInformation("Over Strike 2 fairness: in every mode, every player who joins a match in its first {Window} s while somebody else plays it restarts it ({Command})",
            EarlyJoin.WindowSeconds, Command);
    }

    static long Account(CCSPlayerController p) => Hud.AccountId(p.SteamID.ToString());

    static bool Human(CCSPlayerController? p) => p is { IsValid: true, IsBot: false, IsHLTV: false };

    static bool OnTeam(CCSPlayerController p) => p.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist;

    static CCSGameRules? Rules() => Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;

    /// <summary>A match's start: a round start out of warmup with no round played yet - the map's first, a restart (the fairness one's too), the game commencing.</summary>
    HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
    {
        try
        {
            if (Rules() is not { } rules || rules.WarmupPeriod || rules.TotalRoundsPlayed != 0) return HookResult.Continue;
            var humans = Utilities.GetPlayers().Count(p => Human(p) && OnTeam(p) && Account(p) > 0);
            plan.MatchStarted(DateTime.UtcNow, humans);
            log.LogInformation("Over Strike 2 fairness: the match starts with {Count} human(s) on a team; {Rule}", humans,
                humans > 0 ? $"a join in the next {EarlyJoin.WindowSeconds} s restarts it" : "it runs from the first human's spawn");
        }
        catch (Exception e) { log.LogError("Over Strike 2 fairness: the round start failed: {Error}", e.Message); }
        return HookResult.Continue;
    }

    HookResult OnSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        try
        {
            if (ev.Userid is not { } p || !Human(p) || !OnTeam(p)) return HookResult.Continue;
            var (account, name, slot) = (Account(p), p.PlayerName, p.Slot);
            if (account <= 0) return HookResult.Continue;
            var others = Utilities.GetPlayers().Count(o => o.Slot != slot && Human(o) && OnTeam(o));
            if (!plan.Spawned(account, DateTime.UtcNow, others)) return HookResult.Continue;
            var told = ask(account, name);
            Server.NextFrame(() => log.LogInformation("Over Strike 2 fairness: {Name} joined a match in its first {Window} s - it restarts{Told}", name, EarlyJoin.WindowSeconds,
                told ? "; the bridge counts it down" : " - but there is no bridge to count it down, so nothing happens"));
        }
        catch (Exception e) { log.LogError("Over Strike 2 fairness: the spawn failed: {Error}", e.Message); }
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

    void OnCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return; // the server console and RCON only
        if (command.ArgCount > 1 && command.GetArg(1).Equals("live", StringComparison.OrdinalIgnoreCase))
        {
            plan.Force(DateTime.UtcNow);
            log.LogInformation("Over Strike 2 fairness: {Command} live - the match runs from now, and the next join counts as joining somebody", Command);
        }
        command.ReplyToCommand($"{Command}: {plan.Describe(DateTime.UtcNow)}");
    }
}
