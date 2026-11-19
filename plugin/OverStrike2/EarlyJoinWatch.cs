using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The warm-up in the game: the rule is EarlyJoin's (../../bridge/Shared/EarlyJoin.cs) - every map starts in CS2's own warm-up, everybody coming is
/// waited for, and the real match starts for all of them at once. This begins CS2's warm-up a second into every map (its cfgs have run by then), with neither
/// "all players connected" nor a player count to end it, and keeps its clock: IdleClockSeconds until the first human spawns (renewed while nobody does), then
/// the most from that spawn - so the game's own timer counts down to the latest start, and the game's own end starts the match at the most (SetClock,
/// KeepClock: a change of mp_warmuptime may restart CS2's count from that moment, as CS:GO's did, or not - the next look sees which, and puts the clock right).
/// The bridge, which knows who is still coming, ends it sooner (os_warmup end: mp_warmup_end); should CS2's clock not end it, this does, SafetySeconds past the
/// most. Should CS2 leave it before anybody has spawned (the game commencing as the first player picks a side, say), it is begun again, AgainMost times on a
/// map at the most; after a spawn an end is the end. It feeds the plan the game - every human's spawn on a team, their entering the game, a real departure (not
/// the reconnect of a map change or of the HUD addon's download: EarlyJoin.Reconnects, the bridge's own rule) - and answers the bridge each second: os_warmup
/// (where it stands, and CS2's clock) and os_joining (who is coming). Bots never count. A plugin loaded mid-map begins nothing on that map: it cannot tell who
/// has spawned.</summary>
sealed class EarlyJoinWatch(BasePlugin plugin, ILogger log)
{
    public const string Command = EarlyJoin.WarmUpCommand;
    const float BeginDelay = 1f;   // after a map's start: its cfgs - which set mp_warmuptime and switch CS2's own online warm-up off - have run
    const int AgainMost = 3;       // CS2 left our warm-up before anybody spawned: begun again this often on a map at the most
    const double SettleBegin = 3;  // after a begin: CS2 may take a frame or two to be in its warm-up - no look calls it ended before this
    const float ClockSlack = 1f;   // CS2's own end this far off the latest start at the most (mp_warmuptime is set in whole seconds): else set again...
    const int ClockFixesMost = 3;  // ...this often on a map at the most
    const float ClockLeast = 5f;   // mp_warmuptime's least, as CS:GO's was: a clock set with less than this left gets this
    const float IdleRenew = 600f;  // nobody has spawned and the idle clock has less than this left: it gets IdleClockSeconds again (an empty server's map sits in its warm-up for hours)

    readonly EarlyJoin plan = new();
    (string Name, DateTime At)? lastConnect;        // CounterStrikeSharp's last OnClientConnect, and how many this map: os_warmup shows them (EarlyJoin.ConnectsLine)
    int connects;
    EarlyJoin.BridgeView? bridgeView;                // the bridge's last word (os_warmup bridge): its log stream's sign-ons and status's joiners
    bool ours;                                       // our warm-up is on on this map: begun here, not ended yet
    DateTime begunAt = DateTime.MinValue;            // ...begun (again) at
    int again;                                       // ...begun again on this map after CS2 ended it
    string ended = "";                               // how this map's warm-up ended, for the console
    float? firstSpawnTime;                           // the first human's spawn on this map, game time: CS2's clock ends WarmUpMostSeconds later
    bool? resets;                                    // whether a change of mp_warmuptime restarts CS2's count from that moment (null: not seen yet; CS:GO's did)
    (float At, float StartBefore)? lastSet;          // the last change of mp_warmuptime, game time, and the warm-up's start before it: the next look learns resets
    int clockFixes;                                  // CS2's clock set again on this map

    public void Load(bool hotReload)
    {
        plugin.RegisterListener<Listeners.OnMapStart>(_ =>
        {
            plan.MapStarted();
            (connects, ours, again, ended, firstSpawnTime, lastSet, clockFixes) = (0, false, 0, "", null, null, 0);
            plugin.AddTimer(BeginDelay, () => Begin("the map's start"), TimerFlags.STOP_ON_MAPCHANGE);
        });
        plugin.RegisterListener<Listeners.OnClientConnect>((_, name, address) => { if (address != "127.0.0.1") { lastConnect = (name, DateTime.UtcNow); connects++; } }); // not CounterStrikeSharp's call for a bot
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        plugin.RegisterEventHandler<EventPlayerConnectFull>(OnConnectFull);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect);
        plugin.AddTimer(1f, Keep, TimerFlags.REPEAT);
        plugin.AddCommand(Command, "Over Strike 2 warm-up: where it stands | start | end (the bridge ends it once everybody coming is in)", OnCommand);
        plugin.AddCommand(EarlyJoin.JoiningCommand, "Over Strike 2: the humans still coming - connecting, downloading or loading the map, or in the game and not yet spawned on a team (the bridge's, each second of a warm-up)", OnJoining);
        if (hotReload) plan.Skip("the plugin loaded during this map");
        log.LogInformation("Over Strike 2 warm-up: every map starts in CS2's own, its clock {Most} s from the first human's spawn; the bridge ends it sooner once everybody coming has spawned, or at !start ({Command})",
            EarlyJoin.WarmUpMostSeconds, Command);
    }

    static long Account(CCSPlayerController p) => Hud.AccountId(p.SteamID.ToString());

    static bool Human(CCSPlayerController? p) => p is { IsValid: true, IsBot: false, IsHLTV: false };

    static bool OnTeam(CCSPlayerController p) => p.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist;

    static CCSGameRules? Rules() => Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;

    /// <summary>CS2's own warm-up begins, its clock IdleClockSeconds until somebody spawns: mp_warmuptime - none of the block's (blocked.txt holds
    /// mp_warmup_pausetimer at 0, so no pause) - and "all players connected" and a player count switched off, both of which would end it early (Valve's Arms Race
    /// cfg: 6 players).</summary>
    void Begin(string why)
    {
        try
        {
            if (plan.Skipped != null || Rules() is not { } rules) return;
            Server.ExecuteCommand($"mp_warmuptime_all_players_connected 0; mp_endwarmup_player_count 0; mp_warmuptime {EarlyJoin.IdleClockSeconds}");
            if (!rules.WarmupPeriod) Server.ExecuteCommand("mp_warmup_start");
            (ours, begunAt, lastSet) = (true, DateTime.UtcNow, null);
            log.LogInformation("Over Strike 2 warm-up: begun ({Why}) - CS2's clock {Clock} s until somebody spawns", why, EarlyJoin.IdleClockSeconds);
        }
        catch (Exception e) { log.LogError("Over Strike 2 warm-up: the begin failed: {Error}", e.Message); }
    }

    /// <summary>mp_warmuptime as it stands, in seconds; null when it cannot be read.</summary>
    static float? WarmUpTime() => ConVarKeeper.Find("mp_warmuptime") is { } cv
        && float.TryParse(cv.StringValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : null;

    /// <summary>The seconds left on CS2's own clock - its warm-up's start and mp_warmuptime - while our warm-up has a latest start; null before anybody has spawned,
    /// or off it.</summary>
    double? Left(CCSGameRules rules) => ours && rules.WarmupPeriod && firstSpawnTime != null && WarmUpTime() is { } time ? Math.Max(0, rules.WarmupPeriodStart + time - Server.CurrentTime) : null;

    /// <summary>CS2's own end of the warm-up set to the latest start (game time): mp_warmuptime in whole seconds from the warm-up's start, or from now should a
    /// change restart its count - as CS:GO's did, so that is taken until a look has seen otherwise (KeepClock).</summary>
    void SetClock(CCSGameRules rules, float target, string why)
    {
        var (now, start) = (Server.CurrentTime, rules.WarmupPeriodStart);
        var seconds = (int)Math.Ceiling(Math.Max(ClockLeast, resets == false ? target - start : target - now));
        lastSet = (now, start);
        Server.ExecuteCommand($"mp_warmuptime {seconds}");
        log.LogInformation("Over Strike 2 warm-up: CS2's clock set to {Seconds} s ({Why}; a change of it {Resets})", seconds, why,
            resets switch { true => "restarts its count", false => "keeps its count", _ => "taken to restart its count, as CS:GO's did" });
    }

    /// <summary>What the last change of mp_warmuptime did to the warm-up's start says whether a change restarts CS2's count: the start moved with it, or it stayed
    /// on a warm-up that had run a while.</summary>
    void Learn(CCSGameRules rules)
    {
        if (lastSet is not { } set) return;
        if (Math.Abs(rules.WarmupPeriodStart - set.StartBefore) > 0.25f) resets = true;
        else if (set.At - set.StartBefore > 1f) resets = false;
        lastSet = null;
    }

    /// <summary>Each look once somebody has spawned: a clock whose end is more than ClockSlack off the latest start is set again (ClockFixesMost times on a map).</summary>
    void KeepClock(CCSGameRules rules, float target)
    {
        Learn(rules);
        var start = rules.WarmupPeriodStart;
        if (WarmUpTime() is not { } time || Math.Abs(start + time - target) <= ClockSlack || clockFixes >= ClockFixesMost) return;
        clockFixes++;
        SetClock(rules, target, $"its end was {start + time - target:+0.0;-0.0} s off the latest start");
    }

    /// <summary>The warm-up ends - CS2's warm-up end starts the real match over for everybody at once. What it says: ended, or off already.</summary>
    string End(string why)
    {
        ours = false;
        ended = why;
        if (Rules() is not { WarmupPeriod: true }) { log.LogInformation("Over Strike 2 warm-up: {Why} - off already", why); return "off already"; }
        Server.ExecuteCommand("mp_warmup_end");
        log.LogInformation("Over Strike 2 warm-up: ended ({Why}) - the match starts", why);
        return "ended";
    }

    /// <summary>Each second, while our warm-up is on: off after somebody spawned, it is over - CS2's own clock at the most, the bridge's end, an operator's; off
    /// before anybody spawned, it begins again (AgainMost times on a map); on, its clock kept to the latest start (KeepClock), and SafetySeconds past it with
    /// CS2's clock not run out, ended here.</summary>
    void Keep()
    {
        try
        {
            if (!ours || Rules() is not { } rules) return;
            if (!rules.WarmupPeriod)
            {
                if ((DateTime.UtcNow - begunAt).TotalSeconds < SettleBegin) return;
                if (plan.FirstSpawn != null)
                {
                    var atMost = firstSpawnTime is { } f && Server.CurrentTime >= f + EarlyJoin.WarmUpMostSeconds - 1;
                    (ours, ended) = (false, atMost ? "CS2's own clock, the most since the first spawn" : "CS2 left it by itself (an operator's mp_warmup_end?)");
                    log.LogInformation("Over Strike 2 warm-up: over - {Ended}; the match is on", ended);
                    return;
                }
                if (again >= AgainMost)
                {
                    (ours, ended) = (false, $"CS2 left it by itself {AgainMost + 1} times before anybody spawned");
                    log.LogWarning("Over Strike 2 warm-up: CS2 left it by itself again before anybody spawned - begun {Again} times, so the match runs as CS2 has it", again);
                    return;
                }
                again++;
                Begin($"CS2 left it before anybody spawned - again, {again} of {AgainMost}");
                return;
            }
            if (firstSpawnTime is not { } first) // nobody has spawned: the idle clock kept from running out - an empty server's map sits in its warm-up
            {
                Learn(rules);
                if (WarmUpTime() is { } idle && rules.WarmupPeriodStart + idle - Server.CurrentTime < IdleRenew)
                    SetClock(rules, Server.CurrentTime + EarlyJoin.IdleClockSeconds, "nobody has spawned yet: the idle clock again");
                return;
            }
            var target = first + EarlyJoin.WarmUpMostSeconds;
            if (Server.CurrentTime >= target + EarlyJoin.SafetySeconds)
            {
                End($"{EarlyJoin.SafetySeconds} s past the most, and CS2's own clock had not ended it");
                return;
            }
            KeepClock(rules, target);
        }
        catch (Exception e) { log.LogError("Over Strike 2 warm-up: the look failed: {Error}", e.Message); }
    }

    /// <summary>A human's spawn on a team: in. The map's first sets CS2's clock to end the most from now (a frame later: nothing heavy in the spawn's own call).</summary>
    HookResult OnSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        try
        {
            if (ev.Userid is not { } p || !Human(p) || !OnTeam(p) || Account(p) is not (> 0 and var account)) return HookResult.Continue;
            var first = plan.FirstSpawn == null;
            if (!plan.Spawned(account, DateTime.UtcNow) || !first) return HookResult.Continue;
            firstSpawnTime = Server.CurrentTime;
            if (!ours) return HookResult.Continue;
            var (name, target) = (p.PlayerName, firstSpawnTime.Value + EarlyJoin.WarmUpMostSeconds);
            Server.NextFrame(() =>
            {
                if (!ours || Rules() is not { WarmupPeriod: true } rules) return;
                SetClock(rules, target, $"{name} spawned first - the match starts {EarlyJoin.WarmUpMostSeconds} s from the spawn at the latest");
            });
        }
        catch (Exception e) { log.LogError("Over Strike 2 warm-up: the spawn failed: {Error}", e.Message); }
        return HookResult.Continue;
    }

    /// <summary>A map change disconnects everybody (NETWORK_DISCONNECT_SHUTDOWN), and the HUD addon's download makes a joining game reconnect
    /// (NETWORK_DISCONNECT_LOOPSHUTDOWN): neither is leaving. Any other reason is, NETWORK_DISCONNECT_LOOPDEACTIVATE too - the bridge's log stream decides by the
    /// same rule (EarlyJoin.Reconnects), so a player who left that way and comes back is coming again here as there.</summary>
    static bool Reconnect(int reason) =>
        EarlyJoin.Reconnects(Enum.IsDefined(typeof(NetworkDisconnectionReason), reason) ? ((NetworkDisconnectionReason)reason).ToString() : "");

    HookResult OnDisconnect(EventPlayerDisconnect ev, GameEventInfo info)
    {
        if (ev.Userid is { } p && Human(p) && !Reconnect(ev.Reason)) plan.Left(Account(p));
        return HookResult.Continue;
    }

    /// <summary>A human enters the game - connected in full, a new connect or a map change's reconnect: still coming until their first spawn on a team
    /// (EarlyJoin.StillToSpawn), which os_joining says to the bridge.</summary>
    HookResult OnConnectFull(EventPlayerConnectFull ev, GameEventInfo info)
    {
        try
        {
            if (ev.Userid is { } p && Human(p) && Account(p) is > 0 and var account) plan.Entered(account, DateTime.UtcNow);
        }
        catch (Exception e) { log.LogError("Over Strike 2 warm-up: the connect failed: {Error}", e.Message); }
        return HookResult.Continue;
    }

    /// <summary>os_warmup, the server console and RCON only: where the warm-up stands - its first line the bridge's (EarlyJoin.WarmUpReply), then for people: the
    /// map, the warm-up, who is coming by whom, the connect callbacks. os_warmup start begins one, os_warmup end ends it (the bridge's), os_warmup bridge is the
    /// bridge's view of who is signing on.</summary>
    void OnCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return;
        var now = DateTime.UtcNow;
        var word = command.ArgCount > 1 ? command.GetArg(1) : "";
        if (word.Equals(EarlyJoin.BridgeWord, StringComparison.OrdinalIgnoreCase)) // the bridge: its log stream's sign-ons and status's joiners
        {
            var (signing, status) = EarlyJoin.ReadBridgeView([.. Enumerable.Range(2, Math.Max(0, command.ArgCount - 2)).Select(command.GetArg)]);
            bridgeView = new(signing, status, now);
            command.ReplyToCommand($"{Command}: the bridge's log stream has {signing.Count} signing on, status {(status == null ? "not read" : $"{status.Count} not yet active")}");
            return;
        }
        var said = "";
        if (word.Equals(EarlyJoin.EndWord, StringComparison.OrdinalIgnoreCase)) said = $"; {End("the bridge's word, or the console's")}";
        else if (word.Equals(EarlyJoin.StartWord, StringComparison.OrdinalIgnoreCase)) // the console's, for the lab: a warm-up now, its clock from now once anybody has spawned
        {
            (again, clockFixes) = (0, 0);
            if (firstSpawnTime != null) firstSpawnTime = Server.CurrentTime;
            Begin("the console's");
            if (firstSpawnTime is { } f) Server.NextFrame(() => { if (ours && Rules() is { WarmupPeriod: true } r) SetClock(r, f + EarlyJoin.WarmUpMostSeconds, "the console's begin"); });
            said = "; begun";
        }
        var rules = Rules();
        var on = rules is { WarmupPeriod: true };
        var joining = Joining().Select(p => p.PlayerName).ToList();
        var where = ours ? $"ours, begun again {again} time(s)" : ended != "" ? $"ended: {ended}" : on ? "CS2's, not ours" : "none";
        command.ReplyToCommand(EarlyJoin.WarmUpReply(on, plan.FirstSpawn is { } first ? (now - first).TotalSeconds : null, plan.SpawnedCount, rules == null ? null : Left(rules))
            + $"\n{Command}: {(on ? "on" : "off")} ({where}){said}; {plan.Describe(now)}; {EarlyJoin.JoiningLine(joining, bridgeView, now)}; {EarlyJoin.ConnectsLine(lastConnect, connects, now)}");
    }

    /// <summary>os_joining (EarlyJoin.JoiningCommand): the humans still coming, for the bridge, which waits for them - the controllers connecting or reconnecting,
    /// and the humans in the game who have not spawned on a team since they entered it (EarlyJoin.StillToSpawn). A joiner at CS2's first-download prompt is not
    /// among them: the game hears of a client only once it has every addon, so it has no controller and no callback yet (the live lab's runs AW and AX); the
    /// bridge sees that one in the server's own list and its log stream. The server console and RCON only.</summary>
    void OnJoining(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return;
        command.ReplyToCommand(EarlyJoin.JoiningReply(Joining().Select(p => p.PlayerName)));
    }

    /// <summary>The humans whose game is still on its way in: their controller connecting - the connect itself, CS2's prompt to confirm a first Workshop download, the
    /// download, the map's load - or reconnecting, as every game does through a map change and through MultiAddonManager's download of the HUD addon; and, once
    /// it says connected, a human not yet spawned on a team since entering the game, EarlyJoin.SpawnMostSeconds at the most: CS2's team menu after every load,
    /// or a game still finishing its load.</summary>
    List<CCSPlayerController> Joining()
    {
        var humans = Enumerable.Range(0, Server.MaxPlayers).Select(Utilities.GetPlayerFromSlot).Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false }).Select(p => p!).ToList();
        var toSpawn = plan.StillToSpawn(humans.Where(p => p.Connected == PlayerConnectedState.Connected).Select(Account), DateTime.UtcNow).ToHashSet();
        return [.. humans.Where(p => p.Connected is PlayerConnectedState.Connecting or PlayerConnectedState.Reconnecting || p.Connected == PlayerConnectedState.Connected && toSpawn.Contains(Account(p)))];
    }
}
