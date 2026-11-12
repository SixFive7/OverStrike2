using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>Everybody stays on their side when a match-end screen runs out on the same map (TeamKeepPlan.cs has the rule and why). This feeds the plan the game:
/// a match's end - the win panel, the intermission, or a round end that leaves the game on its match-end screen - with every human's side; every team change
/// of a human; every real departure (not the reconnect of the HUD addon's download, EarlyJoinWatch.Reconnect); and every map start. When the game begins moving
/// the screen's humans to Unassigned, the pass goes a frame later: each of them back to their side with the game's own ChangeTeam. Should the game close the
/// screen without those moves being seen, the next round's prestart or start does the pass instead. Bots are left alone: the game brings them back with the
/// first human. os_keepteams says where it stands; os_keepteams change|jointeam|off, the live lab's, sets how the pass puts players back - ChangeTeam (the
/// default), or each player's own jointeam run on the server as if they had typed it - or switches it off until the plugin loads again.</summary>
sealed class TeamKeep(BasePlugin plugin, ILogger log)
{
    public const string Command = "os_keepteams";

    /// <summary>How the pass puts a player back: Change, the game's ChangeTeam; JoinTeam, their jointeam as if typed; Off, not at all.</summary>
    public enum How { Change, JoinTeam, Off }

    readonly TeamKeepPlan plan = new();
    How how = How.Change;
    bool passDue; // the pass is on its way to the next frame
    const int PassFrames = 64; // about a second: how long the pass waits for the game to leave its match-end phase

    public void Load()
    {
        plugin.RegisterEventHandler<EventCsWinPanelMatch>((_, _) => { Snapshot("the win panel"); return HookResult.Continue; });
        plugin.RegisterEventHandler<EventCsIntermission>((_, _) => { Snapshot("the intermission"); return HookResult.Continue; });
        plugin.RegisterEventHandler<EventRoundEnd>((_, _) =>
        {
            Server.NextFrame(() => { if (Phase() == MatchPhase.Ended) Snapshot("a round end into the match-end screen"); });
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerTeam>(OnTeam);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect);
        plugin.RegisterEventHandler<EventRoundPrestart>((_, _) => { Closed("a round's prestart"); return HookResult.Continue; });
        plugin.RegisterEventHandler<EventRoundStart>((_, _) => { Closed("a round start"); return HookResult.Continue; });
        plugin.RegisterListener<Listeners.OnMapStart>(_ => { plan.MapStarted(); passDue = false; });
        plugin.AddCommand(Command, "Over Strike 2 teams: after a match-end screen on the same map everybody stays on their side - status | change | jointeam | off (the console only)", OnCommand);
        log.LogInformation("Over Strike 2 teams: after a match-end screen that runs out on the same map, every player goes back to their side ({Command})", Command);
    }

    static bool Human(CCSPlayerController? p) => p is { IsValid: true, IsBot: false, IsHLTV: false };

    static List<CCSPlayerController> Humans() => Utilities.GetPlayers().Where(p => Human(p) && p.SteamID != 0).ToList();

    static int Phase() => Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault() is { IsValid: true, GameRules: { } rules } ? rules.GamePhase : -1;

    /// <summary>A match ended: who is on which side, once a screen.</summary>
    void Snapshot(string why)
    {
        try
        {
            if (how == How.Off || plan.ScreenUp) return;
            plan.MatchEnded(Humans().Select(p => (p.SteamID, (int)p.Team)));
            log.LogInformation("Over Strike 2 teams: the match ended ({Why}) - {Count} player(s) on a side, kept for the next match on this map", why, plan.Count);
        }
        catch (Exception e) { log.LogError("Over Strike 2 teams: the match end's snapshot failed: {Error}", e.Message); }
    }

    HookResult OnTeam(EventPlayerTeam ev, GameEventInfo info)
    {
        try
        {
            if (ev.Disconnect || ev.Isbot || ev.Userid is not { } p || !Human(p)) return HookResult.Continue;
            if (plan.TeamChanged(p.SteamID, ev.Team) && !passDue)
            {
                passDue = true;
                Server.NextFrame(() => Pass("the game moved the players to Unassigned as it closed the match-end screen"));
            }
        }
        catch (Exception e) { log.LogError("Over Strike 2 teams: a team change failed: {Error}", e.Message); }
        return HookResult.Continue;
    }

    HookResult OnDisconnect(EventPlayerDisconnect ev, GameEventInfo info)
    {
        if (ev.Userid is { } p && Human(p) && !EarlyJoinWatch.Reconnect(ev.Reason)) plan.Left(p.SteamID);
        return HookResult.Continue;
    }

    /// <summary>A round's prestart or start while a screen's snapshot waits and the game has left the screen: it closed without the moves being seen - the pass now.</summary>
    void Closed(string why)
    {
        if (!plan.ScreenUp || passDue || Phase() == MatchPhase.Ended) return;
        passDue = true;
        Server.NextFrame(() => Pass($"{why} after the match-end screen"));
    }

    /// <summary>Every human with a snapshot who is on Unassigned now goes back to their side, in one pass (TeamKeepPlan.Pass has the order) - once the game has
    /// left its match-end phase: a reset still under way waits a frame more, up to PassFrames. No game rules - a map is changing - puts nobody back.</summary>
    void Pass(string why, int frames = 0)
    {
        var phase = Phase();
        if (phase == MatchPhase.Ended && frames < PassFrames) { Server.NextFrame(() => Pass(why, frames + 1)); return; }
        passDue = false;
        try
        {
            if (!plan.ScreenUp) return;
            if (phase < 0) { plan.MapStarted(); log.LogInformation("Over Strike 2 teams: {Why}, but there are no game rules - a map is changing: nobody put back", why); return; }
            var humans = Humans();
            var now = new Dictionary<ulong, int>();
            foreach (var p in humans) now[p.SteamID] = (int)p.Team;
            var order = plan.Pass(now);
            var back = new List<string>();
            foreach (var (id, team) in order)
            {
                if (humans.FirstOrDefault(h => h.SteamID == id) is not { IsValid: true } p) continue;
                try
                {
                    if (how == How.JoinTeam) p.ExecuteClientCommandFromServer($"jointeam {team} 1");
                    else p.ChangeTeam((CsTeam)team);
                    back.Add($"{p.PlayerName} {(team == TeamKeepPlan.Terrorist ? "T" : "CT")}");
                }
                catch (Exception e) { log.LogError("Over Strike 2 teams: {Name} could not go back to their side: {Error}", p.PlayerName, e.Message); }
            }
            log.LogInformation("Over Strike 2 teams: {Why} - {Count} back on their side ({How}){Who}", why, back.Count, how == How.JoinTeam ? "jointeam" : "ChangeTeam",
                back.Count > 0 ? $": {string.Join(", ", back)}" : "");
        }
        catch (Exception e) { log.LogError("Over Strike 2 teams: the pass failed: {Error}", e.Message); }
    }

    void OnCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return; // the server console and RCON only
        var arg = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "";
        if (arg is "change" or "jointeam" or "off")
        {
            how = arg == "change" ? How.Change : arg == "jointeam" ? How.JoinTeam : How.Off;
            if (how == How.Off) plan.MapStarted(); // nothing kept from now on
            log.LogInformation("Over Strike 2 teams: {Command} {How}", Command, arg);
        }
        command.ReplyToCommand($"{Command}: {(how == How.Change ? "change (ChangeTeam)" : how == How.JoinTeam ? "jointeam" : "off")}; "
            + (plan.ScreenUp ? $"a match-end screen is up, {plan.Count} player(s) to put back" : "no match-end screen"));
    }
}
