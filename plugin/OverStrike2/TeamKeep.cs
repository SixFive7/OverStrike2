using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>Everybody stays on their side when a match-end screen runs out on the same map (TeamKeepPlan.cs has the rule and why). This feeds the plan the game:
/// a match's end - the win panel, the intermission, or a round end that leaves the game on its match-end screen - with every human's side; every team change
/// of a human; every real departure (not the reconnect of the HUD addon's download, EarlyJoinWatch.Reconnect); and every map start. When the game moves the
/// screen's humans to Unassigned, each of them goes back to their side with the game's own ChangeTeam.
///
/// When (os_keepteams when ...). On 211's rehearsal (2026-11-12) a pass a frame after the moves put everybody back, but in Arms Race and Deathmatch CS2's team
/// menu opened at the reset and stayed open over the player, alive on their side behind it - not in Wingman, whose reset starts the next match at once. So:
///   - sameframe (the default): the pass after the entities' think of the frame the moves came in - the game resets the match in the same frame - and a frame
///     later only if that look never came (logged either way);
///   - instant: each player back the moment the game moves them, inside the game's own move - for the live lab;
///   - nextframe: 211's, a frame later.
/// The team menu (os_keepteams quiet on|off, close on|off; both off by default, for the live lab): quiet keeps the game's team menu message (the user message
/// CCSUsrMsg_VGUIMenu, name "team") from the players the plan keeps, from the match end until 3 s after the pass - every other player gets it as ever, and a
/// kept player the pass could not put back gets it then; close sends each player put back that message with show off right after the pass. Either resolves
/// the message's id by name at the moment it is switched on and logs it (the proto's 301 if the name finds nothing); quiet hooks it only while on. Every such
/// message seen while quiet is on and a screen or its window is up is logged. os_keepteams says where it stands; on|off switches the whole thing.</summary>
sealed class TeamKeep(BasePlugin plugin, ILogger log)
{
    public const string Command = "os_keepteams";

    /// <summary>When the pass goes: see the class summary.</summary>
    public enum When { SameFrame, Instant, NextFrame }

    /// <summary>The team menu's user message, and the id its proto gives it - used only when the name finds nothing.</summary>
    public const string MenuMessage = "CCSUsrMsg_VGUIMenu";
    const int MenuMessageProtoId = 301;
    const double QuietAfterSeconds = 3; // quiet's window after the pass
    const int PassFrames = 64;          // about a second: how long the pass waits for the game to leave its match-end phase

    readonly TeamKeepPlan plan = new();
    bool on = true, quiet, close;
    When when = When.SameFrame;
    bool passDue;                       // the pass is on its way
    bool lookDue;                       // sameframe: the moves came; the entities' think of this frame does the pass
    int movesTick = -1;                 // ...the tick they came in
    readonly HashSet<ulong> justBack = new(); // the players the last pass put back: quiet's window after it
    DateTime quietUntil;
    int menuId;                         // the team menu message's id, once resolved (0: not yet)
    bool hooked, ownMenu;               // quiet's hook is on; a message of our own is on its way (the hook lets it through)
    UserMessage.UserMessageHandler? menuHook; // quiet's handler, as hooked: the unhook names the same

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
        plugin.RegisterListener<Listeners.OnServerPostEntityThink>(AfterThink);
        plugin.RegisterListener<Listeners.OnMapStart>(_ => { plan.MapStarted(); (passDue, lookDue) = (false, false); justBack.Clear(); });
        plugin.AddCommand(Command, "Over Strike 2 teams: after a match-end screen on the same map everybody stays on their side - status | on | off | when sameframe|instant|nextframe | quiet on|off | close on|off (the console only)", OnCommand);
        log.LogInformation("Over Strike 2 teams: after a match-end screen that runs out on the same map, every player goes back to their side ({Command}: {Setting})", Command, Setting());
    }

    static bool Human(CCSPlayerController? p) => p is { IsValid: true, IsBot: false, IsHLTV: false };

    static List<CCSPlayerController> Humans() => Utilities.GetPlayers().Where(p => Human(p) && p.SteamID != 0).ToList();

    static int Phase() => Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault() is { IsValid: true, GameRules: { } rules } ? rules.GamePhase : -1;

    static string Side(int team) => team == TeamKeepPlan.Terrorist ? "T" : "CT";

    string Setting() => $"{(on ? "on" : "off")}, when {(when == When.SameFrame ? "sameframe" : when == When.Instant ? "instant" : "nextframe")}, quiet {(quiet ? "on" : "off")}, close {(close ? "on" : "off")}";

    /// <summary>A match ended: who is on which side, once a screen.</summary>
    void Snapshot(string why)
    {
        try
        {
            if (!on || plan.ScreenUp) return;
            plan.MatchEnded(Humans().Select(p => (p.SteamID, (int)p.Team)));
            justBack.Clear();
            log.LogInformation("Over Strike 2 teams: the match ended ({Why}) - {Count} player(s) on a side, kept for the next match on this map", why, plan.Count);
        }
        catch (Exception e) { log.LogError("Over Strike 2 teams: the match end's snapshot failed: {Error}", e.Message); }
    }

    HookResult OnTeam(EventPlayerTeam ev, GameEventInfo info)
    {
        try
        {
            if (ev.Disconnect || ev.Isbot || ev.Userid is not { } p || !Human(p)) return HookResult.Continue;
            var id = p.SteamID;
            if (when == When.Instant && ev.Team == TeamKeepPlan.Unassigned && plan.Kept(id))
            {
                var first = plan.TeamChanged(id, ev.Team);
                var team = plan.Back(id);
                Put(p, team, "instant, inside the game's move");
                justBack.Add(id);
                quietUntil = DateTime.UtcNow.AddSeconds(QuietAfterSeconds);
                if (first && !passDue) { passDue = true; Server.NextFrame(() => Pass("the rest, a frame after the game's moves to Unassigned")); }
                return HookResult.Continue;
            }
            if (plan.TeamChanged(id, ev.Team) && !passDue)
            {
                passDue = true;
                if (when == When.SameFrame)
                {
                    (lookDue, movesTick) = (true, Server.TickCount);
                    Server.NextFrame(() => { if (lookDue) { lookDue = false; Pass("the game moved the players to Unassigned - a frame later, the look after the entities' think never came"); } });
                }
                else Server.NextFrame(() => Pass("the game moved the players to Unassigned as it closed the match-end screen - a frame later"));
            }
        }
        catch (Exception e) { log.LogError("Over Strike 2 teams: a team change failed: {Error}", e.Message); }
        return HookResult.Continue;
    }

    /// <summary>sameframe: after the entities' think of the frame the moves came in.</summary>
    void AfterThink()
    {
        if (!lookDue) return;
        lookDue = false;
        var same = Server.TickCount == movesTick;
        Pass($"the game moved the players to Unassigned - {(same ? "in the same frame" : $"{Server.TickCount - movesTick} tick(s) later")}, after the entities' think");
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
            var back = new List<string>();
            foreach (var (id, team) in plan.Pass(now))
            {
                justBack.Add(id); // quiet's window, and its safety a second on: put back or not
                if (humans.FirstOrDefault(h => h.SteamID == id) is { IsValid: true } p && Put(p, team, null)) back.Add($"{p.PlayerName} {Side(team)}");
            }
            quietUntil = DateTime.UtcNow.AddSeconds(QuietAfterSeconds);
            log.LogInformation("Over Strike 2 teams: {Why}{Frames} - {Count} back on their side (ChangeTeam){Who}", why, frames > 0 ? $", {frames} frame(s) waiting for the reset" : "",
                back.Count, back.Count > 0 ? $": {string.Join(", ", back)}" : "");
            if (quiet && justBack.Count > 0) plugin.AddTimer(1f, StrandedMenus, CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
        }
        catch (Exception e) { log.LogError("Over Strike 2 teams: the pass failed: {Error}", e.Message); }
    }

    /// <summary>One player back to their side, and close's message after it. False when the game refused.</summary>
    bool Put(CCSPlayerController p, int team, string? how)
    {
        try
        {
            p.ChangeTeam((CsTeam)team);
            if (how != null) log.LogInformation("Over Strike 2 teams: {Name} back on {Side} ({How})", p.PlayerName, Side(team), how);
            if (close) Menu(p, show: false, "close");
            return true;
        }
        catch (Exception e) { log.LogError("Over Strike 2 teams: {Name} could not go back to their side: {Error}", p.PlayerName, e.Message); return false; }
    }

    /// <summary>quiet's safety: a second after the pass, a player it was for who is still on Unassigned gets the team menu after all - never stranded without one.</summary>
    void StrandedMenus()
    {
        foreach (var p in Humans().Where(p => justBack.Contains(p.SteamID) && (int)p.Team == TeamKeepPlan.Unassigned))
        {
            justBack.Remove(p.SteamID);
            Menu(p, show: true, "the pass did not put them back");
        }
    }

    /// <summary>The team menu's message to one player, shown or closed - let through quiet's hook.</summary>
    void Menu(CCSPlayerController p, bool show, string why)
    {
        try
        {
            var id = MenuId();
            if (id <= 0) return;
            var message = UserMessage.FromId(id);
            message.SetString("name", "team");
            message.SetBool("show", show);
            ownMenu = true;
            try { message.Send(new RecipientFilter { p }); }
            finally { ownMenu = false; }
            log.LogInformation("Over Strike 2 teams: the team menu {What} for {Name} - {Why}", show ? "opened" : "closed", p.PlayerName, why);
        }
        catch (Exception e) { log.LogError("Over Strike 2 teams: the team menu's message to {Name} failed: {Error}", p.PlayerName, e.Message); }
    }

    /// <summary>The team menu message's id: by name, else the proto's - logged once.</summary>
    int MenuId()
    {
        if (menuId > 0) return menuId;
        int id;
        string how;
        try { id = UserMessage.FindIdByName(MenuMessage); how = $"{MenuMessage} found by name"; }
        catch (Exception e) { (id, how) = (0, $"{MenuMessage} not found by name ({e.Message})"); }
        if (id <= 0) { id = MenuMessageProtoId; how += $" - the proto's {MenuMessageProtoId}"; }
        menuId = id;
        log.LogInformation("Over Strike 2 teams: the team menu's user message is id {Id}: {How}", id, how);
        return id;
    }

    /// <summary>quiet: the team menu message on its way - kept from the players the plan keeps (or the last pass put back, for QuietAfterSeconds).</summary>
    HookResult OnMenuMessage(UserMessage message)
    {
        try
        {
            if (ownMenu || !quiet) return HookResult.Continue;
            var window = plan.ScreenUp || DateTime.UtcNow < quietUntil;
            if (!window) return HookResult.Continue;
            var name = message.HasField("name") ? message.ReadString("name") : "";
            var show = !message.HasField("show") || message.ReadBool("show");
            var kept = new List<string>();
            if (name == "team" && show)
                foreach (var p in message.Recipients.ToList())
                    if (p is { IsValid: true } && (plan.Kept(p.SteamID) || justBack.Contains(p.SteamID)) && message.Recipients.Remove(p)) kept.Add(p.PlayerName);
            log.LogInformation("Over Strike 2 teams: {Message} \"{Name}\" show {Show} to {Count} player(s){Kept}", message.Name, name, show, message.Recipients.Count,
                kept.Count > 0 ? $" - kept from {string.Join(", ", kept)}" : "");
            return kept.Count > 0 && message.Recipients.Count == 0 ? HookResult.Stop : HookResult.Continue;
        }
        catch (Exception e) { log.LogError("Over Strike 2 teams: the team menu's hook failed: {Error}", e.Message); return HookResult.Continue; }
    }

    void Quiet(bool want)
    {
        if (want && !hooked && MenuId() is > 0 and var id) { menuHook = OnMenuMessage; plugin.HookUserMessage(id, menuHook, HookMode.Pre); hooked = true; }
        if (!want && hooked && menuHook != null) { plugin.UnhookUserMessage(menuId, menuHook, HookMode.Pre); hooked = false; }
        quiet = want && hooked;
    }

    void OnCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null) return; // the server console and RCON only
        var (what, value) = (command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "", command.ArgCount > 2 ? command.GetArg(2).ToLowerInvariant() : "");
        string? note = null;
        try
        {
            switch (what)
            {
                case "on" or "off" or "change":
                    on = what != "off";
                    if (!on) { plan.MapStarted(); (passDue, lookDue) = (false, false); }
                    break;
                case "jointeam":
                    note = "refused: the jointeam form is gone - run on the server, the game ignored it and left the player stranded at the team select (211's rehearsal)";
                    break;
                case "when" when value is "sameframe" or "instant" or "nextframe":
                    when = value == "sameframe" ? When.SameFrame : value == "instant" ? When.Instant : When.NextFrame;
                    break;
                case "quiet" when value is "on" or "off":
                    Quiet(value == "on");
                    if (value == "on" && !quiet) note = "refused: the team menu's message could not be hooked";
                    break;
                case "close" when value is "on" or "off":
                    close = value == "on";
                    if (close) MenuId();
                    break;
                case "": break;
                default: note = "usage: on | off | when sameframe|instant|nextframe | quiet on|off | close on|off"; break;
            }
        }
        catch (Exception e) { note = $"failed: {e.Message}"; }
        if (what != "" && note == null) log.LogInformation("Over Strike 2 teams: {Command} {What} {Value} - {Setting}", Command, what, value, Setting());
        command.ReplyToCommand($"{Command}: {Setting()}; {(plan.ScreenUp ? $"a match-end screen is up, {plan.Count} player(s) to put back" : "no match-end screen")}"
            + (menuId > 0 ? $"; the team menu message id {menuId}" : "") + (note != null ? $" - {note}" : ""));
    }
}
