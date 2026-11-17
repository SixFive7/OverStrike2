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
    public const string Command = EarlyJoin.FairCommand;

    readonly EarlyJoin plan = new();
    (string Name, DateTime At)? lastConnect;        // CounterStrikeSharp's last OnClientConnect, and how many this map: os_fair shows them (EarlyJoin.ViewLine)
    int connects;
    (IReadOnlyList<string> Names, DateTime At)? bridgeView; // whom the bridge's log stream has signing on, as it last said (os_fair bridge)

    public void Load(bool hotReload)
    {
        plugin.RegisterListener<Listeners.OnMapStart>(_ => { plan.MapStarted(); connects = 0; });
        plugin.RegisterListener<Listeners.OnClientConnect>((_, name, address) => { if (address != "127.0.0.1") { lastConnect = (name, DateTime.UtcNow); connects++; } }); // not CounterStrikeSharp's call for a bot
        plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect);
        plugin.AddCommand(Command, "Over Strike 2 fairness restart: status | live (the match runs from now and the next join counts, for the client harness)", OnCommand);
        plugin.AddCommand(EarlyJoin.JoiningCommand, "Over Strike 2: the humans still joining - connecting, downloading or loading the map (the bridge's, before a fairness restart)", OnJoining);
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
        var now = DateTime.UtcNow;
        var word = command.ArgCount > 1 ? command.GetArg(1) : "";
        if (word.Equals(EarlyJoin.BridgeWord, StringComparison.OrdinalIgnoreCase)) // the bridge: whom its log stream has signing on
        {
            var names = EarlyJoin.ReadBridgeView(Enumerable.Range(2, Math.Max(0, command.ArgCount - 2)).Select(command.GetArg));
            bridgeView = (names, now);
            command.ReplyToCommand($"{Command}: the bridge's log stream has {names.Count} signing on");
            return;
        }
        if (word.Equals("live", StringComparison.OrdinalIgnoreCase))
        {
            plan.Force(now);
            log.LogInformation("Over Strike 2 fairness: {Command} live - the match runs from now, and the next join counts as joining somebody", Command);
        }
        var joining = Joining().Select(p => p.PlayerName).ToList();
        command.ReplyToCommand($"{Command}: {plan.Describe(now)}; still joining: {(joining.Count == 0 ? "nobody" : string.Join(", ", joining))}; "
            + EarlyJoin.ViewLine(bridgeView, lastConnect, connects, now));
    }

    /// <summary>os_joining (EarlyJoin.JoiningCommand): the humans still joining, for the bridge, which holds a fairness restart while there are any - the controllers
    /// connecting or reconnecting. A joiner at CS2's first-download prompt is not among them: the game hears of a client only once it has every addon, so it has
    /// no controller and no callback yet (the live lab's runs AW and AX); the bridge sees that one in the server's own list and its log stream. The server console
    /// and RCON only.</summary>
    void OnJoining(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return;
        command.ReplyToCommand(EarlyJoin.JoiningReply(Joining().Select(p => p.PlayerName)));
    }

    /// <summary>The humans whose game is still on its way in: their controller connecting - the connect itself, CS2's prompt to confirm a first Workshop download, the
    /// download, the map's load - or reconnecting, as every game does through a map change and through MultiAddonManager's download of the HUD addon. In the game
    /// once it says connected.</summary>
    static List<CCSPlayerController> Joining() => [.. Enumerable.Range(0, Server.MaxPlayers).Select(Utilities.GetPlayerFromSlot)
        .Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false } && p.Connected is PlayerConnectedState.Connecting or PlayerConnectedState.Reconnecting).Select(p => p!)];
}
