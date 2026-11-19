using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The warm-up in the game: the rule is EarlyJoin's (../../bridge/Shared/EarlyJoin.cs) - every map starts with a warm-up, everybody coming is waited
/// for, and the real match starts for all of them at once. How, is the mode's method (settings warmup.<mode>, EarlyJoin.Native, Restart, Off). Native, CS2's own
/// (Wingman): begun a second into the map (its cfgs have run by then), with neither "all players connected" nor a player count to end it, its clock
/// IdleClockSeconds until the first human spawns (renewed while nobody does), then the most from that spawn - so the game's own timer counts down to the latest
/// start (SetClock, KeepClock: a change of mp_warmuptime may restart CS2's count from that moment, as CS:GO's did, or not - the next look sees which, and puts
/// the clock right); ended with mp_warmup_end, which only Wingman's mode rules take. Should CS2 leave it before anybody has spawned (the game commencing as the
/// first player picks a side, say), it is begun again, AgainMost times on a map at the most; within EarlyJoin.EarlyLeaveSeconds of a human's first spawn,
/// AgainAfterSpawnMost times; after that an end is the end. Restart (Arms Race, Deathmatch): no CS2 warm-up - the match itself is the waiting time (waiting),
/// and its end is one restart, os_restart 1; it never lets its match end once somebody has spawned (EarlyJoin.EndBeforeSeconds: a match end leaves no way
/// to keep the real match on its sides), and a match end before that rolls on into the next match, the waiting time still. Either way the bridge, which
/// knows who is still coming, ends it sooner (os_warmup end), and this ends
/// it at the most, and looks until the end has come (KeepEnding). It feeds the plan the game - every human's spawn on a team, their entering the game, a real departure (not
/// the reconnect of a map change or of the HUD addon's download: EarlyJoin.Reconnects, the bridge's own rule) - and answers the bridge each second: os_warmup
/// (where it stands, and CS2's clock) and os_joining (who is coming). Bots never count. A plugin loaded mid-map begins nothing on that map: it cannot tell who
/// has spawned.</summary>
sealed class EarlyJoinWatch(BasePlugin plugin, ILogger log, Func<string> modeId, Func<string, string> methodOf)
{
    public const string Command = EarlyJoin.WarmUpCommand;
    const float BeginDelay = 1f;   // after a map's start: its cfgs - which set mp_warmuptime and switch CS2's own online warm-up off - have run
    const int AgainMost = 3;       // CS2 left our warm-up before anybody spawned: begun again this often on a map at the most
    const int AgainAfterSpawnMost = 3; // ...and within EarlyJoin.EarlyLeaveSeconds of a human's first spawn: begun again this often (Wingman: CS2 left it 0.0-0.7 s after every first spawn, the live lab on 5794269)
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
    int again;                                       // ...begun again on this map after CS2 ended it before anybody spawned
    int againAfterSpawn;                             // ...and soon after a human's first spawn (EarlyJoin.EarlyLeaveSeconds)
    DateTime? newestSpawn;                           // the newest human's first spawn on this map: CS2 may leave its warm-up soon after any of them
    string ended = "";                               // how this map's warm-up ended, for the console
    float? firstSpawnTime;                           // the first human's spawn on this map, game time: CS2's clock ends WarmUpMostSeconds later
    bool? resets;                                    // whether a change of mp_warmuptime restarts CS2's count from that moment (null: not seen yet; CS:GO's did)
    (float At, float StartBefore)? lastSet;          // the last change of mp_warmuptime, game time, and the warm-up's start before it: the next look learns resets
    int clockFixes;                                  // CS2's clock set again on this map
    (string Why, DateTime At, int Step, int Starts)? ending; // an end asked for (End), and the round starts before it - looked at each second, pressed harder if it does not come
    string method = EarlyJoin.Native;                // this map's warm-up method (settings warmup.<mode>, methodOf), taken at its begin
    bool waiting;                                    // the restart method's waiting time runs: our warm-up is on, and CS2 has none
    bool matchOver;                                  // ...and its match ended (an Arms Race win, its length run out): on for the bridge until its word, its next match the real one
    bool endFailed;                                  // the last end asked for would not come with somebody on a side: os_warmup marks it (EarlyJoin.FailedMark), so the bridge says nothing of one
    int roundStarts;                                 // round starts seen: the restart method's end is seen to come by the next one
    bool? wasOn;                                     // CS2's warm-up as the last look found it: a change nothing here asked for is logged (Watch)
    readonly Dictionary<string, DateTime> sentAt = new(); // this plugin's own warm-up commands, when sent: the command listener and Watch tell them from the console's
    List<(string ConVar, string Was, string Ours)> raised = []; // the team damage the warm-up raised (EarlyJoin.TeamDamage): put back at its end

    public void Load(bool hotReload)
    {
        plugin.RegisterListener<Listeners.OnMapStart>(_ =>
        {
            plan.MapStarted();
            (connects, ours, again, againAfterSpawn, ended, firstSpawnTime, lastSet, clockFixes, ending, wasOn, waiting, matchOver, newestSpawn, endFailed) = (0, false, 0, 0, "", null, null, 0, null, null, false, false, null, false);
            if (raised.Count > 0) { log.LogWarning("Over Strike 2 warm-up: the team damage raised on the map before was not put back at its end - this map's cfgs have set their own"); raised = []; }
            plugin.AddTimer(BeginDelay, () => Begin("the map's start"), TimerFlags.STOP_ON_MAPCHANGE);
        });
        plugin.RegisterListener<Listeners.OnClientConnect>((_, name, address) => { if (address != "127.0.0.1") { lastConnect = (name, DateTime.UtcNow); connects++; } }); // not CounterStrikeSharp's call for a bot
        plugin.RegisterEventHandler<EventPlayerSpawn>(OnSpawn);
        plugin.RegisterEventHandler<EventRoundStart>((_, _) => { roundStarts++; return HookResult.Continue; }); // the restart method's end: the restart's round start
        plugin.RegisterListener<Listeners.OnMapEnd>(() => PutBackTeamDamage("the map ends during it")); // a map change that cuts it short: before the next map's cfgs
        plugin.RegisterEventHandler<EventPlayerConnectFull>(OnConnectFull);
        foreach (var name in WarmUpCommands) // the console's and RCON's warm-up commands logged too, as this plugin's own are (the live lab's showed nothing)
            plugin.AddCommandListener(name, (caller, info) =>
            {
                if (!Recent(name)) log.LogInformation("Over Strike 2 warm-up: {Command} from {Who}", info.GetCommandString, caller == null ? "the console or RCON" : caller.PlayerName);
                return HookResult.Continue;
            });
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect);
        plugin.AddTimer(1f, Keep, TimerFlags.REPEAT);
        plugin.AddCommand(Command, "Over Strike 2 warm-up: where it stands | start | end (the bridge ends it once everybody coming is in)", OnCommand);
        plugin.AddCommand(EarlyJoin.JoiningCommand, "Over Strike 2: the humans still coming - connecting, downloading or loading the map, or in the game and not yet spawned on a team (the bridge's, each second of a warm-up)", OnJoining);
        if (hotReload || RulesSafe() is not null) LoadedDuringTheMap(); // css_plugins reload: what the plugin before it knew of the warm-up is gone
        log.LogInformation("Over Strike 2 warm-up: every map starts with one, {Most} s from the first human's spawn at the most - CS2's own in {Native}, the match itself and one restart in {Restart}; the bridge ends it sooner once everybody coming has spawned, or at !start ({Command})",
            EarlyJoin.WarmUpMostSeconds, string.Join(", ", Modes.All.Where(m => methodOf(m.Id) == EarlyJoin.Native).Select(m => m.Name).DefaultIfEmpty("none")),
            string.Join(", ", Modes.All.Where(m => methodOf(m.Id) == EarlyJoin.Restart).Select(m => m.Name).DefaultIfEmpty("none")), Command);
    }

    static long Account(CCSPlayerController p) => Hud.AccountId(p.SteamID.ToString());

    static bool Human(CCSPlayerController? p) => p is { IsValid: true, IsBot: false, IsHLTV: false };

    static bool OnTeam(CCSPlayerController p) => p.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist;

    static CCSGameRules? Rules() => Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;

    /// <summary>The warm-up begins, by the mode's method (EarlyJoin.Native, Restart, Off: settings warmup.<mode>). Restart: no CS2 warm-up - the match runs as the
    /// waiting time (waiting), and End restarts it once. Native: CS2's own warm-up, its clock IdleClockSeconds until somebody spawns: mp_warmuptime - none of the
    /// block's (blocked.txt holds mp_warmup_pausetimer at 0, so no pause) - with "all players connected" switched off (0) and a player count no server reaches
    /// (EarlyJoin.EndWarmUpNever): Valve's Arms Race cfg ends it at 6 players, and 0 there ended it at the first spawn. Off: none.</summary>
    void Begin(string why)
    {
        try
        {
            if (plan.Skipped != null || Rules() is not { } rules) return;
            (method, endFailed) = (methodOf(modeId()), false);
            if (method == EarlyJoin.Off)
            {
                log.LogInformation("Over Strike 2 warm-up: none in {Mode} (warmup.{Mode} off) - the match is on at once", modeId(), modeId());
                return;
            }
            if (method == EarlyJoin.Restart)
            {
                (ours, waiting, matchOver, begunAt) = (true, true, false, DateTime.UtcNow);
                RaiseTeamDamage();
                log.LogInformation("Over Strike 2 warm-up: begun ({Why}) - {Mode}'s restarts: the match itself is the waiting time, and one restart begins the real match", why, modeId());
                return;
            }
            Send($"mp_warmuptime_all_players_connected 0; mp_endwarmup_player_count {EarlyJoin.EndWarmUpNever}; mp_warmuptime {EarlyJoin.IdleClockSeconds}");
            if (!rules.WarmupPeriod) Send("mp_warmup_start");
            (ours, begunAt, lastSet) = (true, DateTime.UtcNow, null);
            RaiseTeamDamage();
            log.LogInformation("Over Strike 2 warm-up: begun ({Why}) - CS2's clock {Clock} s until somebody spawns", why, EarlyJoin.IdleClockSeconds);
        }
        catch (Exception e) { log.LogError("Over Strike 2 warm-up: the begin failed: {Error}", e.Message); }
    }

    /// <summary>mp_warmuptime as it stands, in seconds; null when it cannot be read.</summary>
    static float? WarmUpTime() => ConVarKeeper.Find("mp_warmuptime") is { } cv
        && float.TryParse(cv.StringValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : null;

    /// <summary>The seconds left on CS2's own clock - its warm-up's start and mp_warmuptime - while our warm-up has a latest start; null before anybody has spawned,
    /// or off it.</summary>
    double? Left(CCSGameRules rules) => ours && method == EarlyJoin.Native && rules.WarmupPeriod && firstSpawnTime != null && WarmUpTime() is { } time ? Math.Max(0, rules.WarmupPeriodStart + time - Server.CurrentTime) : null;

    /// <summary>The restart kind's waiting time about to see its match end - its round's clock with EarlyJoin.EndBeforeSeconds or less left, or in Arms Race a
    /// player holding the last weapon, one knife kill from the win - as the reason its end gives; null while neither is so.</summary>
    string? MatchEndNear(CCSGameRules rules)
    {
        var left = rules.RoundStartTime + rules.RoundTime - Server.CurrentTime;
        if (MatchLength.OneRound(modeId()) && rules.RoundTime > 0 && left <= EarlyJoin.EndBeforeSeconds) return $"its match's clock was running out, {Math.Max(0, left):0} s left";
        if (modeId() != Modes.ArmsRaceId) return null;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is not { IsValid: true, PawnIsAlive: true } || p.PlayerPawn.Value?.WeaponServices?.MyWeapons is not { } weapons) continue;
            foreach (var w in weapons)
                if (CatchUp.Weapons.Name(w.Value?.DesignerName) == CatchUp.Weapons.Name(EarlyJoin.LastWeapon)) return $"{p.PlayerName} holds the last weapon, one knife kill from the win";
        }
        return null;
    }

    /// <summary>Whether our warm-up is on as os_warmup says it: the restart method's waiting time, or CS2's own warm-up.</summary>
    bool On(CCSGameRules rules) => method == EarlyJoin.Restart ? waiting : rules.WarmupPeriod;

    /// <summary>CS2's own end of the warm-up set to the latest start (game time): mp_warmuptime in whole seconds from the warm-up's start, or from now should a
    /// change restart its count - as CS:GO's did, so that is taken until a look has seen otherwise (KeepClock).</summary>
    void SetClock(CCSGameRules rules, float target, string why)
    {
        var (now, start) = (Server.CurrentTime, rules.WarmupPeriodStart);
        var seconds = (int)Math.Ceiling(Math.Max(ClockLeast, resets == false ? target - start : target - now));
        lastSet = (now, start);
        Send($"mp_warmuptime {seconds}");
        log.LogInformation("Over Strike 2 warm-up: CS2's clock set to {Seconds} s from {From} ({Why}; a change of it {Resets})", seconds, resets == false ? "the warm-up's start" : "now", why,
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

    /// <summary>A convar's value as the game has it now; null when it cannot be read.</summary>
    static string? Read(string name) => ConVarKeeper.Find(name)?.StringValue;

    /// <summary>The warm-up's team damage (EarlyJoin.TeamDamage), at its begin: in Arms Race and Wingman, friendly fire on and every team-damage share below an
    /// enemy's damage raised to it - Wingman's, Valve's below 1; Arms Race's cfg has its Instakill, 100, which stays. Once a map: a warm-up begun again keeps it.</summary>
    void RaiseTeamDamage()
    {
        if (raised.Count > 0 || !EarlyJoin.TeamDamageMode(modeId(), Read("mp_teammates_are_enemies"))) return;
        raised = EarlyJoin.RaiseTeamDamage(Read);
        if (raised.Count == 0) { log.LogInformation("Over Strike 2 warm-up: team damage is full already ({Mode}) - nothing raised", modeId()); return; }
        foreach (var (convar, _, value) in raised) Server.ExecuteCommand($"{convar} {value}");
        log.LogInformation("Over Strike 2 warm-up: team damage raised for it: {List}", string.Join(", ", raised.Select(r => $"{r.ConVar} {r.Was} -> {r.Ours}")));
    }

    /// <summary>...and at its end - any end, a map change that cuts it short (OnMapEnd), the plugin's unload: each value it raised back to its own, unless
    /// something else changed it meanwhile (a player's Fun page choice, the map), which keeps.</summary>
    void PutBackTeamDamage(string why)
    {
        if (raised.Count == 0) return;
        try
        {
            var (back, kept) = EarlyJoin.PutBackTeamDamage(raised, Read);
            foreach (var (convar, value) in back) Server.ExecuteCommand($"{convar} {value}");
            log.LogInformation("Over Strike 2 warm-up: team damage put back ({Why}): {Back}{Kept}", why, back.Count == 0 ? "nothing" : string.Join(", ", back.Select(b => $"{b.ConVar} {b.Value}")),
                kept.Count == 0 ? "" : $"; changed meanwhile, so kept: {string.Join(", ", kept)}");
        }
        catch (Exception e) { log.LogError("Over Strike 2 warm-up: putting the team damage back failed: {Error}", e.Message); }
        raised = [];
    }

    /// <summary>The plugin unloads (a reload, a new build): the team damage this instance raised goes back with it.</summary>
    public void Unload() => PutBackTeamDamage("the plugin unloads");

    /// <summary>The console commands of CS2's warm-up, which the command listener logs when this plugin did not just send them itself.</summary>
    static readonly string[] WarmUpCommands = ["mp_warmup_start", "mp_warmup_end", "mp_endwarmup_player_count", "mp_warmuptime"];

    /// <summary>A command of this plugin's own, sent now and noted by its first word for the listener (Recent).</summary>
    void Send(string command)
    {
        foreach (var part in command.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) sentAt[part.Split(' ')[0]] = DateTime.UtcNow;
        Server.ExecuteCommand(command);
    }

    bool Recent(string command) => sentAt.TryGetValue(command, out var at) && (DateTime.UtcNow - at).TotalSeconds < 5;

    /// <summary>The warm-up ends, and the real match starts over for everybody at once - by the method. Restart: os_restart 1, the plugin's own restart, which the
    /// guard lets through (RestartGuard) - or, should the match have ended during the waiting time (an Arms Race win, its length run out), nothing: its next
    /// match is the real one, which the bridge starts at once (os_nextmatch). Native: CS2's own warm-up end, mp_warmup_end, which only Wingman's mode rules take
    /// (EarlyJoin.Native). Each look then sees whether it came, and presses harder if not (KeepEnding). What it says: ending, ending already, ended, or off
    /// already.</summary>
    string End(string why)
    {
        var rules = Rules();
        if (rules != null && plan.Skipped != null && method == EarlyJoin.Restart && !ours && !waiting && AnyoneOnASide())
        {
            // loaded during the map: the bridge, which followed the waiting time, asks for its end - taken up here for its one restart
            (ours, waiting) = (true, true);
            log.LogInformation("Over Strike 2 warm-up: loaded during the map, and asked for the waiting time's end ({Why}) - taken up for its one restart", why);
        }
        if (rules == null || !On(rules))
        {
            log.LogInformation("Over Strike 2 warm-up: {Why} - off already", why);
            (ours, ended, ending, waiting) = (false, why, null, false);
            PutBackTeamDamage(why);
            return "off already";
        }
        if (ending is { } asked) return $"ending already ({asked.Why}, {(DateTime.UtcNow - asked.At).TotalSeconds:0} s ago)";
        if (method == EarlyJoin.Restart && (rules.GamePhase == MatchPhase.Ended || matchOver))
        {
            log.LogInformation("Over Strike 2 warm-up: ended ({Why}) - the match ended during the waiting time: its next match is the real one", why);
            (ours, ended, waiting) = (false, why, false);
            PutBackTeamDamage(why);
            return "ended (the match ended during the waiting time: its next match is the real one)";
        }
        if (method == EarlyJoin.Restart && !AnyoneOnASide())
        {
            StartOver($"nobody on a side to restart for ({why})");
            return "waiting again (nobody on a side to restart for: its clock from the next spawn)";
        }
        (ending, endFailed) = ((why, DateTime.UtcNow, 0, roundStarts), false);
        if (method == EarlyJoin.Restart)
        {
            Send("os_restart 1");
            log.LogInformation("Over Strike 2 warm-up: ending ({Why}) - os_restart 1: the restart begins the real match", why);
        }
        else
        {
            Send("mp_warmup_end");
            log.LogInformation("Over Strike 2 warm-up: ending ({Why}) - mp_warmup_end, CS2's own end", why);
        }
        return "ending";
    }

    /// <summary>Each look while an end is asked for, until it came: the restart method's by the next round start, the native one's by CS2's warm-up off - ended,
    /// the match starts, the team damage back. Not come EarlyJoin.EndStepSeconds after the request: the restart again, or CS2's own player-count check as well
    /// (mp_endwarmup_player_count EndWarmUpNow) with mp_warmup_end; after twice as long the same again; after four times given up and logged.</summary>
    void KeepEnding(CCSGameRules rules, (string Why, DateTime At, int Step, int Starts) asked)
    {
        var waited = (DateTime.UtcNow - asked.At).TotalSeconds;
        if (method == EarlyJoin.Restart ? roundStarts > asked.Starts : !rules.WarmupPeriod)
        {
            log.LogInformation("Over Strike 2 warm-up: ended ({Why}), {Waited:0.0} s after it was asked for - the match starts", asked.Why, waited);
            (ours, ended, ending, waiting) = (false, asked.Why, null, false);
            PutBackTeamDamage(asked.Why);
            return;
        }
        var again = method == EarlyJoin.Restart ? "os_restart 1" : $"mp_endwarmup_player_count {EarlyJoin.EndWarmUpNow}; mp_warmup_end";
        if (asked.Step < 2 && waited >= (asked.Step + 1) * EarlyJoin.EndStepSeconds)
        {
            ending = (asked.Why, asked.At, asked.Step + 1, asked.Starts);
            Send(again);
            log.LogWarning("Over Strike 2 warm-up: not ended {Waited:0} s after the request ({Why}) - {Again}", waited, asked.Why, again);
        }
        else if (asked.Step == 2 && waited >= 4 * EarlyJoin.EndStepSeconds)
        {
            if (method == EarlyJoin.Restart && !AnyoneOnASide()) // everybody went meanwhile: a restart with nobody on a side starts no round
            {
                StartOver($"nobody on a side to restart for ({asked.Why}: no round start {waited:0} s after the request)");
                return;
            }
            log.LogError("Over Strike 2 warm-up: it would not end ({Why}) - {Waited:0} s after the request; left so, the plugin asks no more, and os_warmup marks it {Mark}: the bridge says nothing of an end",
                asked.Why, waited, EarlyJoin.FailedMark);
            (ours, ended, ending, waiting, endFailed) = (false, $"would not end ({asked.Why})", null, false, true);
            PutBackTeamDamage(ended);
        }
    }

    /// <summary>A human on a side - T or CT - now.</summary>
    static bool AnyoneOnASide() => Utilities.GetPlayers().Any(p => Human(p) && OnTeam(p));

    /// <summary>The waiting time starts over, its clock from the next human's spawn as on a map nobody has spawned on yet: everybody who spawned has left during
    /// it (a lone player on an empty map - in Wingman CS2's clock ended it at 120 s all the same, and the next player joined a running match), or the restart
    /// kind's end found nobody on a side to restart for (a restart there starts no round, and is no end: the live lab on 997c7bb). The native kind's CS2 clock
    /// goes back to the idle clock; os_warmup says no first spawn, which the bridge follows (its own clock from that spawn too).</summary>
    void StartOver(string why)
    {
        (firstSpawnTime, newestSpawn, againAfterSpawn, ending, endFailed, clockFixes) = (null, null, 0, null, false, 0);
        plan.StartOver();
        if (method == EarlyJoin.Restart) waiting = true;
        else if (Rules() is { WarmupPeriod: true } rules) SetClock(rules, Server.CurrentTime + EarlyJoin.IdleClockSeconds, $"{why}: the idle clock again");
        log.LogWarning("Over Strike 2 warm-up: {Why} - the waiting time goes on, its clock from the next spawn", why);
    }

    /// <summary>The plugin loaded during a map (css_plugins reload, a restart of the plugin alone): what the one before it knew of the warm-up - who spawned, when,
    /// its clock - is gone with it. With humans on, it cannot tell who has spawned: its plan says so (os_warmup's blind mark), and the bridge, which followed the
    /// warm-up, has it end at once, cleanly - this carries the end out (End: CS2's warm-up end, or the restart kind's one restart; the live lab's W8 on ce3e9af
    /// saw the bridge drop the warm-up instead, and CS2's own clock end it). With nobody on, there is nothing to lose: a warm-up as at the map's start.</summary>
    void LoadedDuringTheMap()
    {
        method = methodOf(modeId());
        if (Utilities.GetPlayers().Any(p => Human(p)))
        {
            plan.Skip("the plugin loaded during this map, with players on: it cannot tell who has spawned - the bridge has the warm-up it follows end now");
            log.LogInformation("Over Strike 2 warm-up: loaded during the map with players on - it cannot tell who has spawned: os_warmup says so ({Mark}), and the bridge ends the warm-up it follows", EarlyJoin.BlindMark);
        }
        else plugin.AddTimer(BeginDelay, () => Begin("the plugin loaded during the map, nobody on: a warm-up as at its start"), TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>The game rules, or null - also where the entity system cannot be asked yet (a plugin loaded before the first map).</summary>
    static CCSGameRules? RulesSafe()
    {
        try { return Rules(); }
        catch (Exception) { return null; }
    }

    /// <summary>Each look: CS2's warm-up as it is now against the last look, and a change nothing here asked for logged - the console's mp_warmup_start or
    /// mp_warmup_end, a map's own, CS2's own clock on a map that is not ours - so every start and end of a warm-up shows in the log. Our own begin and end, and
    /// CS2 leaving ours, log themselves.</summary>
    void Watch(CCSGameRules rules)
    {
        var on = rules.WarmupPeriod;
        if (wasOn is { } was && was != on)
        {
            if (on && !Recent("mp_warmup_start")) log.LogInformation("Over Strike 2 warm-up: CS2's warm-up began - not at this plugin's word (the console's mp_warmup_start, a map's)");
            else if (!on && !ours && ending == null) log.LogInformation("Over Strike 2 warm-up: CS2's warm-up ended - not at this plugin's word (the console's mp_warmup_end, a map's, CS2's own clock)");
        }
        wasOn = on;
    }

    /// <summary>Each second: a change of CS2's warm-up nothing here asked for logged (Watch); an end asked for looked after (KeepEnding); and while our warm-up is on -
    /// off within EarlyJoin.EarlyLeaveSeconds of the first spawn (CS2 leaving it by itself, not at its clock's end) it begins again, once a map, its clock still from
    /// that spawn; off after that it is over - CS2's own clock at the most, an operator's end; off before anybody spawned it begins again (AgainMost times on a
    /// map); on, its clock kept to the latest start (KeepClock), and at the latest start ended here (End): CS2's own clock does not end it in Arms Race while the
    /// count holds it.</summary>
    void Keep()
    {
        try
        {
            if (Rules() is not { } rules) return;
            Watch(rules);
            if (ending is { } asked) { KeepEnding(rules, asked); return; }
            if (!ours) return;
            if (method == EarlyJoin.Restart) // the match itself is the waiting time: at the most it restarts, and it never lets its match end once somebody has spawned
            {
                if (!waiting) return;
                // a match end during it all the same (a win between two looks; before anybody spawned, an empty map's match run out): on for the bridge through the
                // match-end screen, so its look never finds the warm-up off before its match end has come - the bridge's word then ends it (os_warmup end: End),
                // with no vote. Its next match begun with no word: ended here - or, nobody having spawned yet, the waiting time goes on into it
                if (rules.GamePhase == MatchPhase.Ended)
                {
                    if (!matchOver) log.LogInformation("Over Strike 2 warm-up: the match ended during the waiting time ({Who}) - on until the bridge's word",
                        firstSpawnTime == null ? "nobody has spawned yet: the waiting time goes on into the next match" : "a win, its length run out: its next match is the real one");
                    matchOver = true;
                    return;
                }
                if (matchOver)
                {
                    matchOver = false;
                    if (firstSpawnTime == null)
                    {
                        log.LogInformation("Over Strike 2 warm-up: the next match began, nobody having spawned yet - the waiting time goes on in it");
                        return;
                    }
                    (ours, waiting, ended) = (false, false, "the match ended during the waiting time");
                    log.LogInformation("Over Strike 2 warm-up: over - {Ended}, and its next match, the real one, began with no word from the bridge", ended);
                    PutBackTeamDamage(ended);
                    return;
                }
                if (firstSpawnTime is not { } from) return;
                if (plan.SpawnedCount == 0) { StartOver("everybody who spawned has left"); return; }
                if (Server.CurrentTime >= from + EarlyJoin.WarmUpMostSeconds) { End($"{EarlyJoin.WarmUpMostSeconds} s since the first spawn"); return; }
                if (MatchEndNear(rules) is { } near) End(near);
                return;
            }
            if (!rules.WarmupPeriod)
            {
                if ((DateTime.UtcNow - begunAt).TotalSeconds < SettleBegin) return;
                if (plan.FirstSpawn is { } spawned)
                {
                    var atMost = firstSpawnTime is { } f && Server.CurrentTime >= f + EarlyJoin.WarmUpMostSeconds - 1;
                    var soon = (DateTime.UtcNow - (newestSpawn is { } n && n > spawned ? n : spawned)).TotalSeconds; // since the newest human's first spawn
                    if (!atMost && soon < EarlyJoin.EarlyLeaveSeconds && againAfterSpawn < AgainAfterSpawnMost)
                    {
                        againAfterSpawn++;
                        log.LogWarning("Over Strike 2 warm-up: CS2 left it by itself {Soon:0.0} s after a spawn - begun again ({Again} of {Most}), its clock still to {Clock} s from the first spawn",
                            soon, againAfterSpawn, AgainAfterSpawnMost, EarlyJoin.WarmUpMostSeconds);
                        Begin($"CS2 left it {soon:0.0} s after a spawn");
                        if (firstSpawnTime is { } from)
                            Server.NextFrame(() => { if (ours && Rules() is { WarmupPeriod: true } rulesNow) SetClock(rulesNow, from + EarlyJoin.WarmUpMostSeconds, "begun again after a spawn"); });
                        return;
                    }
                    (ours, ended) = (false, atMost ? "CS2's own clock, the most since the first spawn"
                        : soon < EarlyJoin.EarlyLeaveSeconds ? $"CS2 left it by itself again, {soon:0.0} s after a spawn, begun again {AgainAfterSpawnMost} times already - the match runs as CS2 has it"
                        : "CS2 left it by itself (an operator's mp_warmup_end?)");
                    log.LogInformation("Over Strike 2 warm-up: over - {Ended}; the match is on", ended);
                    PutBackTeamDamage(ended);
                    return;
                }
                if (again >= AgainMost)
                {
                    (ours, ended) = (false, $"CS2 left it by itself {AgainMost + 1} times before anybody spawned");
                    log.LogWarning("Over Strike 2 warm-up: CS2 left it by itself again before anybody spawned - begun {Again} times, so the match runs as CS2 has it", again);
                    PutBackTeamDamage(ended);
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
            if (plan.SpawnedCount == 0) { StartOver("everybody who spawned has left"); return; } // the lone player gone: not CS2's clock to 120 s for nobody
            var target = first + EarlyJoin.WarmUpMostSeconds;
            if (Server.CurrentTime >= target)
            {
                End($"{EarlyJoin.WarmUpMostSeconds} s since the first spawn"); // the latest start: CS2's own clock does not end it in Arms Race while the count holds
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
            if (!plan.Spawned(account, DateTime.UtcNow)) return HookResult.Continue;
            newestSpawn = DateTime.UtcNow; // a human's first spawn on this map: CS2 may leave its warm-up soon after it (the guard in Keep)
            if (!first) return HookResult.Continue;
            firstSpawnTime = Server.CurrentTime;
            if (!ours || method != EarlyJoin.Native) return HookResult.Continue; // CS2's clock is the native method's alone
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
            if (firstSpawnTime is { } f && method == EarlyJoin.Native)
                Server.NextFrame(() => { if (ours && Rules() is { WarmupPeriod: true } r) SetClock(r, f + EarlyJoin.WarmUpMostSeconds, "the console's begin"); });
            said = "; begun";
        }
        var rules = Rules();
        var on = rules != null && On(rules);
        var joining = Joining().Select(p => p.PlayerName).ToList();
        var where = ours ? $"ours, {method}, begun again {again + againAfterSpawn} time(s)" : ended != "" ? $"ended: {ended}" : rules is { WarmupPeriod: true } ? "CS2's, not ours" : $"none ({method})";
        var mark = plan.Skipped != null ? EarlyJoin.BlindMark : endFailed ? EarlyJoin.FailedMark : "";
        command.ReplyToCommand(EarlyJoin.WarmUpReply(on, plan.FirstSpawn is { } first ? (now - first).TotalSeconds : null, plan.SpawnedCount, rules == null ? null : Left(rules), mark)
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
