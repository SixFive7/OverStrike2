using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The server's one CounterStrikeSharp plugin: the HUD and the menu, the chat commands, the vote's freeze, the grenade drops, the
/// settings module and the fun page, catch-up and the game modes. Its interface comes in one of two forms, chosen by the cs2 service's
/// Hud.AddonVariable (OS2_HUD_ADDON; HudAddon.cs reads the old name too, until the next release):
///
///   - set (the Workshop id of the published HUD addon): the HUD - the full menu (maps with full-text search on an on-screen keyboard, unlisted
///     Workshop ids, the queue, settings, help, rules), the board beside the scoreboard as a click action panel while TAB is held, a now-playing
///     card, a map-change banner, the news line and each player's note. See HudScreen and ../../bridge/Hud.cs; the layout is the Workshop addon in
///     hud/, which MultiAddonManager hands out. A real join gets the name and tagline centre screen for a few seconds.
///   - empty: nothing drawn, as when Steam hides the HUD item - the chat commands work and their answers come in chat (HudScreen without an
///     addon: no item watch, no HUD check), and a real join gets the small centre welcome panel instead (../../bridge/Guide.cs).
///
/// The bridge decides when to show the welcome (a real join, read off the log) and asks over RCON with css_guide. Chat: a line that starts with
/// ! or / is a chat command, one of the menu's actions (ChatCommands.cs; HudScreen.Chat); with the HUD drawn a pasted Workshop link opens its card.</summary>
public class OverStrike2Plugin : BasePlugin
{
    public const string Version = "4.7.1";
    public override string ModuleName => "OverStrike2";
    public override string ModuleVersion => Version;
    public override string ModuleDescription => "Over Strike 2: the HUD and menu, the chat commands, the vote's freeze, the grenade drops, the map settings and the fun page, catch-up, the game modes";

    /// <summary>How long a catch-up message stays centre screen: sent every tick, since the game's centre panel goes within a second of its last
    /// message whatever duration it names. At least this long; longer when the catch-up module asks for more.</summary>
    const double CatchUpMessageSeconds = 5;

    readonly GuidePanel panel = new();                                          // no HUD drawn: the welcome panel at a real join
    readonly GuidePanel tagline = new(Guide.TaglineHtml(), Guide.TaglineSeconds); // with the HUD: the name and tagline at a real join, ~3 s
    HudScreen? hud;
    Nades? nades;            // the grenade drops
    Settings? settings;      // a map's settings put back, the game-changers block, the fun page's changes and effects
    Effects? effects;        // the fun page's effects Vampire, Ghost and no recoil
    CatchUp.CatchUpModule? catchUp; // the Arms Race catch-up boosts
    VoteFreeze? freeze;      // nobody moves, shoots or takes damage while a vote freezes everybody
    bool catchUpOn = true;   // what catch-up was last switched to: on while its fun setting is on and the map is Arms Race

    /// <summary>The HUD (drawn or not): the modules put their lines on its news line (the block's notices) through this.</summary>
    internal HudScreen? Screen => hud;
    string addon = "";
    (string Html, DateTime Until) funNotice;                                    // a fun change, centre screen for everybody
    readonly Dictionary<int, (string Html, DateTime Until)> boosts = new();     // slot -> its catch-up message, sent every tick until then
    readonly HashSet<int> centreBusy = new();                                   // the slots whose centre panel got something of their own this tick

    public override void Load(bool hotReload)
    {
        string from;
        (addon, from) = HudAddon.Read(Environment.GetEnvironmentVariable, Hud.AddonVariable, Hud.OldAddonVariable);
        if (from == Hud.OldAddonVariable) Logger.LogWarning("Over Strike 2: the HUD addon comes from {Old}, the old name of {Variable} - rename it; the old name is read until the next release", Hud.OldAddonVariable, Hud.AddonVariable);
        SteamWebApi.UserAgent = SteamWebApi.AgentOf(Version);
        hud = new HudScreen(this, Logger, hotReload, addon);
        RegisterListener<Listeners.OnTick>(OnTick); // the freeze, the centre panels, the catch-up messages, the fun notice
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        AddCommandListener("say", OnSay);
        AddCommandListener("say_team", OnSay);
        // The modules' console commands keep their names (gg_fun, mapreset_*), so the bridge and the restart watcher talk to them as before.
        settings = new Settings(this, Logger);
        settings.Load(hotReload);
        RegisterConsoleCommandAttributeHandlers(settings);
        nades = new Nades(this, Logger, () => settings.EffectOn(Fun.Drops));
        nades.Load();
        effects = new Effects(this, Logger, settings.EffectOn);
        effects.Load();
        freeze = new VoteFreeze(this, Logger, () => hud);
        freeze.Load(); // before catch-up: its damage hook has the first word while everybody is frozen
        catchUp = new CatchUp.CatchUpModule(this, Logger, Path.Combine(ModuleDirectory, CatchUp.CatchUpConfig.FileName));
        catchUp.Load(hotReload);
        // The Arms Race-only features follow the mode the map that is on was loaded in (os_mode; docs/reference.md) and, the grenade drops and
        // catch-up, their fun settings: each runs while its setting is on and the map is Arms Race.
        settings.ModeApplied += mode =>
        {
            nades.ModeAllows = mode.ArmsRace;
            CatchUpFollows($"{mode.Name} loaded");
            hud.SetMode(mode);
            Logger.LogInformation("Over Strike 2: {Mode} - grenade drops {Nades}, catch-up {CatchUp}", mode.Name,
                !mode.ArmsRace ? "off (Arms Race's own)" : settings.EffectOn(Fun.Drops) ? "on" : "off (the fun setting)", catchUpOn ? "on" : mode.ArmsRace ? "off (the fun setting)" : "off (Arms Race's own)");
        };
        settings.EffectChanged += (effect, on) =>
        {
            if (effect.Equals(Fun.Drops, StringComparison.OrdinalIgnoreCase))
            {
                if (!settings.MapEnding) nades.Switched(on); // the undo at a map's end hands nothing to the players of the map that goes
                Logger.LogInformation("Over Strike 2: grenade drops {State} (the fun setting){Mode}", on ? "on" : "off", settings.Mode.ArmsRace ? "" : $" - Arms Race's own, so off in {settings.Mode.Name} anyway");
            }
            else if (effect.Equals(Fun.CatchUp, StringComparison.OrdinalIgnoreCase)) CatchUpFollows($"its fun setting {(on ? "on" : "off")}");
        };
        hud.FunSource = settings.FunValue; // the fun page shows the settings module's values, its effects - the grenade drops and catch-up among them - included
        hud.Centre = (who, what) => funNotice = (FunNotice.Html(who, what), DateTime.UtcNow.AddSeconds(Fun.CentreSeconds));
        catchUp.Show = (player, lines, html, seconds) => // sent every tick for its seconds (OnTick); the fun notice steps aside for it
        {
            boosts[player.Slot] = (html, DateTime.UtcNow.AddSeconds(Math.Max(seconds, CatchUpMessageSeconds)));
            player.PrintToCenterHtml(html);
        };
        Logger.LogInformation("Over Strike 2 {Version}: {Mode}, {Command} from the bridge, TAB, the chat commands, the vote's freeze.", ModuleVersion,
            hud.Drawn ? $"HUD (Workshop addon {addon})" : $"no HUD ({Hud.AddonVariable} is empty): chat answers in chat, the centre welcome panel for {Guide.ShowSeconds} s", Guide.Command);
    }

    public override void Unload(bool hotReload)
    {
        freeze?.Unload();
        hud?.Unload();
    }

    /// <summary>Catch-up runs while its fun setting is on and the map that is on is in Arms Race: switched whenever either changes - a
    /// player's change, the undo at every map end and the reset after nobody has been on (both put the setting back on), a mode.</summary>
    void CatchUpFollows(string why)
    {
        if (catchUp == null || settings == null) return;
        var on = settings.EffectOn(Fun.CatchUp) && settings.Mode.ArmsRace;
        if (on == catchUpOn) return;
        catchUpOn = on;
        Logger.LogInformation("Over Strike 2: catch-up {State} - {Why}{Mode}", on ? "on" : "off", why, settings.Mode.ArmsRace ? "" : $" (Arms Race's own: off in {settings.Mode.Name})");
        catchUp.Switch(on);
    }

    /// <summary>Every tick: the freeze first; then each player's centre panel gets at most one thing of its own, in this order - a picture
    /// (HudScreen), the join's welcome panel or tagline, a catch-up message; and the fun notice goes to everybody whose panel got none of
    /// those. Each is sent every tick while it lasts, as CounterStrikeSharp's CenterHtmlMenu does: a single send lasts only a moment and other
    /// centre messages overwrite it. Nothing to show costs a few checks.</summary>
    void OnTick()
    {
        freeze?.Tick();
        hud?.PictureTick();
        var now = DateTime.UtcNow;
        centreBusy.Clear();
        Centre(panel, now);
        Centre(tagline, now);
        if (boosts.Count > 0)
            foreach (var (slot, (html, until)) in boosts.ToList())
            {
                if (now >= until) { boosts.Remove(slot); continue; }
                if (centreBusy.Contains(slot) || hud?.PictureShowing(slot) == true) continue;
                if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: false } p) { p.PrintToCenterHtml(html); centreBusy.Add(slot); }
                else boosts.Remove(slot);
            }
        if (now >= funNotice.Until) return;
        // A fun change, every tick for its 10 s (a slower message flashes the panel anew each time), for everybody whose centre panel is not
        // showing something of their own: a picture, the join's panel, a catch-up message.
        foreach (var p in Utilities.GetPlayers())
            if (p is { IsValid: true, IsBot: false } && !centreBusy.Contains(p.Slot) && hud?.PictureShowing(p.Slot) != true)
                p.PrintToCenterHtml(funNotice.Html);
    }

    /// <summary>One of the join's centre panels, this tick: what each slot with it up gets now (its last frame clears the panel).</summary>
    void Centre(GuidePanel centre, DateTime now)
    {
        if (centre.Idle) return;
        foreach (var (slot, html) in centre.Frame(now))
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true, IsBot: false }) { centre.Gone(slot); continue; }
            player.PrintToCenterHtml(html);
            if (html != GuidePanel.Clear) centreBusy.Add(slot);
        }
    }

    HookResult OnPlayerSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        if (ev.Userid is not { IsValid: true } player) return HookResult.Continue;
        boosts.Remove(player.Slot); // a new life: its own message, if any, comes from the catch-up module
        if (hud is { Drawn: true }) { hud.Spawned(player); tagline.Spawned(player.Slot, DateTime.UtcNow); }
        else panel.Spawned(player.Slot, DateTime.UtcNow);
        return HookResult.Continue;
    }

    HookResult OnPlayerDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        if (ev.Userid is { IsValid: true } player) boosts.Remove(player.Slot); // the message was about the life that ended
        return HookResult.Continue;
    }

    HookResult OnPlayerDisconnect(EventPlayerDisconnect ev, GameEventInfo info)
    {
        if (ev.Userid is not { IsValid: true } player) return HookResult.Continue;
        panel.Gone(player.Slot); // a map change disconnects everyone too: nothing on screen survives it
        tagline.Gone(player.Slot);
        boosts.Remove(player.Slot);
        hud?.Gone(player.Slot);
        return HookResult.Continue;
    }

    /// <summary>Sent by the bridge over RCON on a real join, "first" for a player it has never seen: with the HUD drawn, the window on its start
    /// page or the welcome-back card and the tagline; without, the welcome panel. The reply starts with Guide.Shown when it worked; the bridge
    /// logs it.</summary>
    [ConsoleCommand(Guide.Command, "Over Strike 2: show the welcome to one player (the bridge sends this on a real join)")]
    [CommandHelper(minArgs: 1, usage: "<account id | [U:1:n] | SteamID64> [first]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnGuideCommand(CCSPlayerController? caller, CommandInfo command)
    {
        var id = Hud.AccountId(command.GetArg(1));
        var first = command.ArgCount > 2 && command.GetArg(2).Equals("first", StringComparison.OrdinalIgnoreCase);
        var player = id < 0 ? null : Utilities.GetPlayers().FirstOrDefault(p => !p.IsBot && Hud.AccountId(p.SteamID.ToString()) == id);
        if (player == null) { command.ReplyToCommand($"guide: no player {command.GetArg(1)} on the server"); return; }
        var delay = player.PawnIsAlive ? GuidePanel.SpawnDelaySeconds : 0;
        if (hud is { Drawn: true })
        {
            hud.Joined(player, first);
            tagline.Request(player.Slot, player.PawnIsAlive, DateTime.UtcNow);
            catchUp?.TaglineShown(player.Slot, Guide.TaglineSeconds + delay); // its message comes after the tagline
        }
        else
        {
            panel.Request(player.Slot, player.PawnIsAlive, DateTime.UtcNow);
            catchUp?.TaglineShown(player.Slot, Guide.ShowSeconds + delay); // ...and after the welcome panel, which stays longer
        }
        command.ReplyToCommand($"{Guide.Shown} to {player.PlayerName}{(first ? " (first time)" : "")}");
    }

    /// <summary>A player's chat line. It always shows as they typed it: the plugin only reads it (a / line is CounterStrikeSharp's silent trigger,
    /// which hides it). A line that starts with ! or / is a chat command, a bare number a vote while one is open; with the HUD drawn a Workshop
    /// link or id opens that item in the menu, which is the one thing worth pasting: the keyboard cannot paste.</summary>
    HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player is not { IsValid: true, IsBot: false } || hud == null) return HookResult.Continue;
        var text = info.ArgCount > 1 ? info.ArgString.Trim().Trim('"').Trim() : "";
        var who = player;
        var screen = hud;
        // A line that starts with ! or / does what the menu's click would, a frame later (not inside the player's own chat message: HudScreen's
        // summary), and answers in chat.
        var command = ChatCommands.Parse(text, screen.VoteOpen, screen.VoteHighest);
        if (command.Kind != ChatCommands.Kind.None)
            Server.NextFrame(() => { if (who.IsValid) screen.Chat(who, command); });
        else if (screen.Drawn && WorkshopCard.IdInChat(text) is { } id)
            Server.NextFrame(() => { if (who.IsValid) screen.OpenWorkshop(who, id); });
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

    /// <summary>Server console / RCON (the restart watcher, server/update-watch.py): a line for everybody on the HUD's news line, for that many
    /// seconds - the restart warnings. Nothing drawn (an empty Hud.AddonVariable): the reply says "no HUD", and the watcher says it in chat
    /// instead.</summary>
    [ConsoleCommand("gg_notice", "Over Strike 2: a line on the HUD's news line for everybody: gg_notice <seconds> <text>")]
    [CommandHelper(minArgs: 2, usage: "<seconds> <text>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnNoticeCommand(CCSPlayerController? caller, CommandInfo command)
    {
        var parts = command.ArgString.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out var seconds)) { command.ReplyToCommand("gg_notice: <seconds> <text>"); return; }
        command.ReplyToCommand(hud is not { Drawn: true } ? "gg_notice: no HUD" : $"gg_notice: {hud.Notice(parts[1].Trim('"'), seconds)}");
    }

    /// <summary>Server console / RCON: what the HUD is doing - the entity, the bridge link, the last state, who has the window open.
    /// "css_gghud on" draws the HUD until the plugin reloads, whatever Hud.AddonVariable says, and polls the bridge even with nobody on: a way
    /// to try the server half without recreating the container (players only see it once their client has the addon). "css_gghud click
    /// &lt;account id&gt; &lt;button id&gt;" sends what that button would, for that account, without a client.</summary>
    [ConsoleCommand("css_gghud", "Over Strike 2 HUD: status; 'on' draws it until the plugin reloads; 'click <account> <button>' tries a button; 'tab off|down|bridge' sets TAB; 'release' gives every cursor back; 'item' visits the HUD item's page now; 'page <id>' tries the item check on any item")]
    [CommandHelper(minArgs: 0, usage: "[on | click <account id> <button id> | tab off|down|bridge | release | item | page <id> | show <account> <page> | content <account> <level> | level <n> [account] | press <account> <button>]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnStatusCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (hud == null) { command.ReplyToCommand("gghud: not loaded"); return; }
        var verb = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "";
        if (verb == "on" && !hud.Drawn)
        {
            hud.DrawByHand();
            Logger.LogInformation("Over Strike 2 HUD: drawn by css_gghud on, without an addon (until the plugin reloads)");
        }
        if (verb == "tab")
        {
            var arg = command.ArgCount > 2 ? command.GetArg(2).ToLowerInvariant() : "";
            command.ReplyToCommand(arg is "off" or "down" ? $"gghud: {hud.SetTab(arg)}" : arg == "bridge" ? $"gghud: {hud.SetTab(null)}" : "gghud: tab off | down | bridge");
            return;
        }
        if (verb == "release")
        {
            command.ReplyToCommand($"gghud: {hud.ReleaseAll()}");
            return;
        }
        if (verb == "item") // after publishing an update: visit the item's Workshop page now, not in up to 2 minutes
        {
            command.ReplyToCommand($"gghud: {hud.CheckItemNow()}");
            return;
        }
        if (verb == "page") // any item: what the item check would make of its page and Web API answer now - changes nothing
        {
            command.ReplyToCommand(command.ArgCount < 3 || !long.TryParse(command.GetArg(2), out var probe) || probe <= 0 ? "gghud: page <workshop id>" : $"gghud: {hud.ProbeItem(probe)}");
            return;
        }
        if (verb == "show") // the client harness: the window on a page for one account (it cannot click)
        {
            command.ReplyToCommand(command.ArgCount < 4 || Hud.AccountId(command.GetArg(2)) < 0 ? "gghud: show <account id> <page>"
                : $"gghud: {hud.ShowFor(Hud.AccountId(command.GetArg(2)), command.GetArg(3))}");
            return;
        }
        if (verb == "level") // the client harness: the version check's server level faked (css_gghud level <n> [account]), and the join banner again
        {
            hud.TestLevel = command.ArgCount > 2 && int.TryParse(command.GetArg(2), out var lv) && lv > 0 ? lv : null;
            command.ReplyToCommand(command.ArgCount > 3 && Hud.AccountId(command.GetArg(3)) > 0 ? $"gghud: {hud.Rejoin(Hud.AccountId(command.GetArg(3)))}"
                : $"gghud: server level {(hud.TestLevel?.ToString() ?? "real")}");
            return;
        }
        if (verb == "content") // the client harness: which content that account's game holds (normally a click proves it)
        {
            command.ReplyToCommand(command.ArgCount < 4 || Hud.AccountId(command.GetArg(2)) < 0 || !int.TryParse(command.GetArg(3), out var level) ? "gghud: content <account id> <level>"
                : $"gghud: {hud.SetContent(Hud.AccountId(command.GetArg(2)), level)}");
            return;
        }
        if (verb == "press") // the client harness: a click through the real click path (css_gghud press <account> <button>)
        {
            command.ReplyToCommand(command.ArgCount < 4 || Hud.AccountId(command.GetArg(2)) < 0 ? "gghud: press <account id> <button id>"
                : $"gghud: {hud.Press(Hud.AccountId(command.GetArg(2)), command.GetArg(3))}");
            return;
        }
        if (verb == "click")
        {
            command.ReplyToCommand(command.ArgCount < 4 || Hud.AccountId(command.GetArg(2)) < 0 ? "gghud: click <account id> <button id>"
                : $"gghud: {hud.SimulateClick(Hud.AccountId(command.GetArg(2)), command.GetArg(3))}");
            return;
        }
        command.ReplyToCommand((hud.Drawn ? $"gghud: addon {(addon == "" ? "(none - drawn by hand)" : addon)}" : $"gghud: no HUD ({Hud.AddonVariable} is empty) - chat and the centre welcome panel")
            + $", {(freeze?.Frozen == true ? "everybody frozen for the vote" : "nobody frozen")}, {hud.Status()}");
    }
}
