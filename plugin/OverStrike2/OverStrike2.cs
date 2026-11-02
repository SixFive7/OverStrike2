using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The server's interface, in one of two forms, chosen by GUNGAME_HUD_ADDON on the cs2 service:
///
///   - set (the Workshop id of the published HUD addon): the HUD - since 3.0.0 (2026-10-21) the whole interface, chat commands are gone:
///     the full menu (maps with full-text search on an on-screen keyboard, unlisted Workshop ids, the queue, settings, help, rules), the
///     board beside the scoreboard as a click action panel while TAB is held, a now-playing card, a map-change banner, the news line and
///     each player's note. See HudScreen and ../../bridge/Hud.cs; the layout is the Workshop addon in hud/, which MultiAddonManager hands out.
///   - empty: the small centre panel of 1.x, re-sent every tick for Guide.ShowSeconds (../../bridge/Guide.cs), on a real join only.
///
/// The bridge decides when to show the welcome (a real join, read off the log) and asks over RCON with css_guide. Chat: since Over Strike 2
/// a typed command is one of the menu's actions (ChatCommands.cs; HudScreen.Chat) - with the HUD off it gets one private hint per player
/// (Hud.ChatOff) - and a pasted Workshop link opens its card.</summary>
public class OverStrike2Plugin : BasePlugin
{
    public const string Version = "4.4.0";
    public override string ModuleName => "OverStrike2";
    public override string ModuleVersion => Version;
    public override string ModuleDescription => "Over Strike 2: the HUD and menu, the grenade drops, the map settings and the fun page, the game modes";

    readonly GuidePanel panel = new();
    readonly GuidePanel tagline = new(Guide.TaglineHtml(), Guide.TaglineSeconds); // with the HUD on: the name and tagline at a real join, ~3 s
    HudScreen? hud;
    WorkshopWatch? workshop; // the daily Workshop check: which maps nobody can download any more (WorkshopWatch.cs)
    Nades? nades;            // the grenade drops (GunGameNades until 2026-11-01)
    Settings? settings;      // a map's settings put back, the game-changers block, the fun page's changes (MapSettingsReset until 2026-11-01)
    Effects? effects;        // the fun page's effects that are no convar: Vampire, Ghost, no recoil
    CatchUp.CatchUpModule? catchUp; // the Arms Race catch-up boosts (#28)

    /// <summary>The HUD, when it is on: the modules put their lines on its news line (the block's notices) through this.</summary>
    internal HudScreen? Screen => hud;
    string addon = "";
    readonly HashSet<ulong> hinted = new(); // players who typed a chat command and were told, once, that the menu replaced them

    public override void Load(bool hotReload)
    {
        addon = (Environment.GetEnvironmentVariable(Hud.AddonVariable) ?? "").Trim();
        if (addon != "") hud = new HudScreen(this, Logger, hotReload, addon);
        RegisterListener<Listeners.OnTick>(OnTick); // the centre panel, or with the HUD the tagline at a join
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        AddCommandListener("say", OnSay);
        AddCommandListener("say_team", OnSay);
        workshop = new WorkshopWatch(Logger);
        AddTimer(60f, () => workshop.Poll(DateTime.UtcNow), CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
        // The two plugins folded in on 2026-11-01, as modules: their console commands keep their names (gg_nades, gg_fun, mapreset_*),
        // so the bridge and the restart watcher talk to them as before.
        nades = new Nades(this, Logger);
        nades.Load(hotReload);
        RegisterConsoleCommandAttributeHandlers(nades);
        settings = new Settings(this, Logger);
        settings.Load(hotReload);
        RegisterConsoleCommandAttributeHandlers(settings);
        effects = new Effects(this, Logger, settings.EffectOn);
        effects.Load();
        catchUp = new CatchUp.CatchUpModule(this, Logger, Path.Combine(ModuleDirectory, CatchUp.CatchUpConfig.FileName));
        catchUp.Load(hotReload);
        // #27 c: the Arms Race-only features follow the mode the map that is on was loaded in (os_mode; docs/reference.md).
        settings.ModeApplied += mode =>
        {
            nades.ModeAllows = mode.ArmsRace;
            Server.ExecuteCommand(mode.ArmsRace ? "gg_catchup on" : "gg_catchup off");
            hud?.SetMode(mode);
            Logger.LogInformation("Over Strike 2: {Mode} - grenade drops {Nades}, catch-up {CatchUp}", mode.Name, mode.ArmsRace ? "as set" : "off", mode.ArmsRace ? "on" : "off");
        };
        if (hud != null) { hud.FunSource = settings.FunValue; hud.NadesOn = () => nades.Enabled; } // the fun page shows the settings module's values, its effects included
        Logger.LogInformation("Over Strike 2 {Version}: {Mode}, {Command} from the bridge, the menu on TAB and the chat commands.", ModuleVersion,
            hud != null ? $"HUD (Workshop addon {addon})" : $"centre panel for {Guide.ShowSeconds} s ({Hud.AddonVariable} is empty)", Guide.Command);
    }

    public override void Unload(bool hotReload) => hud?.Unload();

    /// <summary>Centre panel only: every tick while anyone has it up, as CounterStrikeSharp's CenterHtmlMenu does - a single send lasts
    /// only a few seconds and other centre messages overwrite it. Nothing to do costs one boolean check.</summary>
    void OnTick()
    {
        hud?.PictureTick();
        foreach (var centre in new[] { panel, tagline })
        {
            if (centre.Idle) continue;
            foreach (var (slot, html) in centre.Frame(DateTime.UtcNow))
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player is { IsValid: true, IsBot: false }) player.PrintToCenterHtml(html);
                else centre.Gone(slot);
            }
        }
    }

    HookResult OnPlayerSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        if (ev.Userid is not { IsValid: true } player) return HookResult.Continue;
        if (hud != null) { hud.Spawned(player); tagline.Spawned(player.Slot, DateTime.UtcNow); }
        else panel.Spawned(player.Slot, DateTime.UtcNow);
        return HookResult.Continue;
    }

    HookResult OnPlayerDisconnect(EventPlayerDisconnect ev, GameEventInfo info)
    {
        if (ev.Userid is not { IsValid: true } player) return HookResult.Continue;
        panel.Gone(player.Slot); // a map change disconnects everyone too: nothing on screen survives it
        tagline.Gone(player.Slot);
        hud?.Gone(player.Slot);
        return HookResult.Continue;
    }

    /// <summary>Sent by the bridge over RCON on a real join, "first" for a player it has never seen: the window on its start page, or the
    /// welcome-back card. The reply starts with Guide.Shown when it worked; the bridge logs it.</summary>
    [ConsoleCommand(Guide.Command, "Show the guide to one player (the chat bridge sends this on a real join)")]
    [CommandHelper(minArgs: 1, usage: "<account id | [U:1:n] | SteamID64> [first]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnGuideCommand(CCSPlayerController? caller, CommandInfo command)
    {
        var id = Hud.AccountId(command.GetArg(1));
        var first = command.ArgCount > 2 && command.GetArg(2).Equals("first", StringComparison.OrdinalIgnoreCase);
        var player = id < 0 ? null : Utilities.GetPlayers().FirstOrDefault(p => !p.IsBot && Hud.AccountId(p.SteamID.ToString()) == id);
        if (player == null) { command.ReplyToCommand($"guide: no player {command.GetArg(1)} on the server"); return; }
        if (hud != null)
        {
            hud.Joined(player, first);
            tagline.Request(player.Slot, player.PawnIsAlive, DateTime.UtcNow);
            catchUp?.TaglineShown(player.Slot, Guide.TaglineSeconds + (player.PawnIsAlive ? GuidePanel.SpawnDelaySeconds : 0)); // its message comes after the tagline
        }
        else panel.Request(player.Slot, player.PawnIsAlive, DateTime.UtcNow);
        command.ReplyToCommand($"{Guide.Shown} to {player.PlayerName}{(first ? " (first time)" : "")}");
    }

    /// <summary>A player's chat line. It always shows as they typed it: the plugin only reads it (a / line is CounterStrikeSharp's silent trigger,
    /// which hides it). A line that starts with ! or / is a chat command, a bare number a vote while one is open, and a Workshop link or id
    /// opens that item in the menu, which is the one thing worth pasting: the keyboard cannot paste. With the HUD off a command gets one
    /// private hint per player per server process instead: "This server uses the menu: press TAB".</summary>
    HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player is not { IsValid: true, IsBot: false }) return HookResult.Continue;
        var text = info.ArgCount > 1 ? info.ArgString.Trim().Trim('"').Trim() : "";
        var who = player;
        // The chat commands are back (ChatCommands.cs; the owner, 2026-11-01): a line that starts with ! or / does what the menu's click
        // would, a frame later (not inside the player's own chat message: HudScreen's summary); a Workshop link or id opens its card.
        var command = ChatCommands.Parse(text, hud?.VoteOpen == true);
        if (hud != null && command.Kind != ChatCommands.Kind.None)
            Server.NextFrame(() => { if (who.IsValid) hud.Chat(who, command); });
        else if (hud != null && WorkshopCard.IdInChat(text) is { } id)
            Server.NextFrame(() => { if (who.IsValid) hud.OpenWorkshop(who, id); });
        else if (command.Kind != ChatCommands.Kind.None && hinted.Add(player.SteamID))
            Server.NextFrame(() => { if (who.IsValid) who.PrintToChat($" {ChatColors.Gold}{Hud.ChatOff}"); });
        return HookResult.Continue;
    }

    /// <summary>The bridge, before every map change and after every map start: os_mode &lt;id&gt; - the mode the next map loads in (Modes.cs;
    /// docs/reference.md). Server only.</summary>
    [ConsoleCommand(Modes.Command, "Over Strike 2: the game mode the next map loads in: os_mode armsrace|deathmatch|wingman")]
    [CommandHelper(minArgs: 1, usage: "<mode id>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnModeCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (Modes.Find(command.GetArg(1)) is not { } mode || command.GetArg(1).Trim() == "") { command.ReplyToCommand($"os_mode: no mode {command.GetArg(1)} ({string.Join(", ", Modes.All.Select(m => m.Id))})"); return; }
        command.ReplyToCommand(settings == null ? "os_mode: no settings module" : $"os_mode: {settings.SetMode(mode)}");
    }

    /// <summary>Server console / RCON (the restart watcher, server/update-watch.py): a line for everybody on the HUD's news line, for
    /// that many seconds - the restart warnings it used to say in chat. No HUD (an empty GUNGAME_HUD_ADDON): the reply says so and the
    /// watcher says it in chat instead.</summary>
    [ConsoleCommand("gg_notice", "GunGameWelcome: a line on the HUD's news line for everybody: gg_notice <seconds> <text>")]
    [CommandHelper(minArgs: 2, usage: "<seconds> <text>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnNoticeCommand(CCSPlayerController? caller, CommandInfo command)
    {
        var parts = command.ArgString.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out var seconds)) { command.ReplyToCommand("gg_notice: <seconds> <text>"); return; }
        command.ReplyToCommand(hud == null ? "gg_notice: no HUD" : $"gg_notice: {hud.Notice(parts[1].Trim('"'), seconds)}");
    }

    /// <summary>Server console / RCON: what the HUD is doing - the entity, the bridge link, the last state, who has the window open.
    /// "css_gghud on" switches the HUD on until the plugin reloads, whatever GUNGAME_HUD_ADDON says, and polls the bridge even with
    /// nobody on: a way to try the server half without recreating the container (players only see it once their client has the
    /// addon). "css_gghud click &lt;account id&gt; &lt;button id&gt;" sends what that button would, for that account, without a client.</summary>
    [ConsoleCommand("css_gghud", "GunGameWelcome: HUD status; 'on' switches it on until the plugin reloads; 'click <account> <button>' tries a button; 'tab <mode>' sets the TAB chord; 'release' gives every cursor back; 'workshop' runs the Workshop check; 'item' visits the HUD item's page now; 'page <id>' tries the item check on any item")]
    [CommandHelper(minArgs: 0, usage: "[on | click <account id> <button id> | tab off|menu|on|down|hold|window|bridge | release | workshop | item | page <id> | show <account> <page> | content <account> <level>]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnStatusCommand(CCSPlayerController? caller, CommandInfo command)
    {
        var verb = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "";
        if (verb == "on" && hud == null)
        {
            hud = new HudScreen(this, Logger, hotReload: Utilities.GetPlayers().Count > 0 || Server.MapName != "", addon) { AlwaysPoll = true };
            Logger.LogInformation("GunGameWelcome: HUD switched on by css_gghud (until the plugin reloads)");
        }
        if (verb == "tab")
        {
            var arg = command.ArgCount > 2 ? command.GetArg(2).ToLowerInvariant() : "";
            command.ReplyToCommand(hud == null ? "gghud: off - css_gghud on first"
                : arg is "off" or "menu" or "on" or "down" or "hold" or "window" ? $"gghud: {hud.SetTab(arg)}" : arg == "bridge" ? $"gghud: {hud.SetTab(null)}" : "gghud: tab off | menu | on | down | hold | window | bridge");
            return;
        }
        if (verb == "workshop")
        {
            command.ReplyToCommand($"gghud: {workshop?.RunSoon() ?? "no Workshop check"}");
            return;
        }
        if (verb == "release")
        {
            command.ReplyToCommand(hud == null ? "gghud: off - nothing to release" : $"gghud: {hud.ReleaseAll()}");
            return;
        }
        if (verb == "item") // after publishing an update: visit the item's Workshop page now, not in up to 2 minutes
        {
            command.ReplyToCommand(hud == null ? "gghud: off - no item to check" : $"gghud: {hud.CheckItemNow()}");
            return;
        }
        if (verb == "page") // any item: what the item check would make of its page and Web API answer now - changes nothing
        {
            command.ReplyToCommand(hud == null ? "gghud: off - css_gghud on first"
                : command.ArgCount < 3 || !long.TryParse(command.GetArg(2), out var probe) || probe <= 0 ? "gghud: page <workshop id>" : $"gghud: {hud.ProbeItem(probe)}");
            return;
        }
        if (verb == "show") // the client harness: the window on a page for one account (it cannot click)
        {
            command.ReplyToCommand(hud == null ? "gghud: off" : command.ArgCount < 4 || Hud.AccountId(command.GetArg(2)) < 0 ? "gghud: show <account id> <page>"
                : $"gghud: {hud.ShowFor(Hud.AccountId(command.GetArg(2)), command.GetArg(3))}");
            return;
        }
        if (verb == "level") // the client harness: the version check's server level faked (css_gghud level <n> [account]), and the join banner again
        {
            if (hud == null) { command.ReplyToCommand("gghud: off"); return; }
            hud.TestLevel = command.ArgCount > 2 && int.TryParse(command.GetArg(2), out var lv) && lv > 0 ? lv : null;
            command.ReplyToCommand(command.ArgCount > 3 && Hud.AccountId(command.GetArg(3)) > 0 ? $"gghud: {hud.Rejoin(Hud.AccountId(command.GetArg(3)))}"
                : $"gghud: server level {(hud.TestLevel?.ToString() ?? "real")}");
            return;
        }
        if (verb == "content") // the client harness: which content that account's game holds (normally a click proves it)
        {
            command.ReplyToCommand(hud == null ? "gghud: off" : command.ArgCount < 4 || Hud.AccountId(command.GetArg(2)) < 0 || !int.TryParse(command.GetArg(3), out var level) ? "gghud: content <account id> <level>"
                : $"gghud: {hud.SetContent(Hud.AccountId(command.GetArg(2)), level)}");
            return;
        }
        if (verb == "press") // the client harness: a click through the real click path (css_gghud press <account> <button>)
        {
            command.ReplyToCommand(hud == null ? "gghud: off" : command.ArgCount < 4 || Hud.AccountId(command.GetArg(2)) < 0 ? "gghud: press <account id> <button id>"
                : $"gghud: {hud.Press(Hud.AccountId(command.GetArg(2)), command.GetArg(3))}");
            return;
        }
        if (verb == "click")
        {
            command.ReplyToCommand(hud == null ? "gghud: off - css_gghud on first"
                : command.ArgCount < 4 || Hud.AccountId(command.GetArg(2)) < 0 ? "gghud: click <account id> <button id>"
                : $"gghud: {hud.SimulateClick(Hud.AccountId(command.GetArg(2)), command.GetArg(3))}");
            return;
        }
        command.ReplyToCommand(hud == null ? $"gghud: off ({Hud.AddonVariable} is empty) - centre panel mode" : $"gghud: addon {(addon == "" ? "(switched on by hand)" : addon)}, {hud.Status()}");
    }
}
