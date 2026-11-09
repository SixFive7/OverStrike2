using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;
using Part = OverStrike2.Core.Part;

namespace OverStrike2;

/// <summary>The server's one CounterStrikeSharp plugin: the HUD and the menu, the chat commands, the vote's freeze, the grenade drops, the
/// settings module and the fun page, catch-up and the game modes. Its interface is the HUD: the full menu (maps with full-text search - !s in chat
/// opens it on the matches, and on MAPS > Browse a chat line searches it - unlisted Workshop ids, the vote, settings, help), the board beside the scoreboard as a click action panel while TAB is held,
/// a now-playing card, the countdown banner, the top line, the status banner and the window's lines. See HudScreen and ../../bridge/Shared/Hud.cs;
/// the layout is the Workshop addon in hud/, which MultiAddonManager hands out. A real join gets the welcome window, until its X.
///
/// The bridge decides when to show the welcome (a real join, read off the log) and asks over RCON with css_guide. Chat: a line that starts with
/// ! or / is a chat command, one of the menu's actions (ChatCommands.cs; HudScreen.Chat); a pasted Workshop link opens its card.</summary>
public class OverStrike2Plugin : BasePlugin
{
    public const string Version = "5.9.0";
    public override string ModuleName => "OverStrike2";
    public override string ModuleVersion => Version;
    public override string ModuleDescription => "Over Strike 2: the HUD and menu, the chat commands, the vote's freeze, the grenade drops, the map settings and the fun page, catch-up, the game modes";

    /// <summary>How long a catch-up message stays: the card under the crosshair, or the centre panel, sent every tick since the game's centre
    /// panel goes within a second of its last message whatever duration it names.</summary>
    const double CatchUpMessageSeconds = 5;

    HudScreen? hud;
    GrenadeDrops? drops;     // the grenade drops
    GameSettings? settings;  // a map's settings put back, the game-changers block, the fun page's changes and effects
    FunEffects? effects;     // the fun page's effects Vampire, Ghost and no recoil
    Cloak? cloak;            // a player hidden from everybody else, all of them: Ghost's, the spawn cloak's (SpawnCloak), os_cloak's
    CatchUp.CatchUpModule? catchUp; // the Arms Race catch-up boosts
    VoteFreeze? freeze;      // nobody moves, shoots or takes damage while a vote freezes everybody
    bool catchUpOn = true;   // what catch-up was last switched to: on while its fun setting is on and the map is Arms Race

    string addon = "";
    readonly Dictionary<int, (string Html, DateTime Until)> boosts = new();     // slot -> its catch-up message on the centre panel, for a game not known to hold the card: sent every tick until then

    public override void Load(bool hotReload)
    {
        // The plugin's own settings (PluginSetup.cs). Settings it cannot use, or none at all: it does not load, as the server programs refuse
        // them. Thrown before anything is registered, so CounterStrikeSharp marks the plugin failed and nothing of it runs.
        var own = Core.Settings.Load(Part.Plugin);
        if (own.Fatal != null)
        {
            Logger.LogCritical("Over Strike 2 {Settings}", own.StartLine);
            throw new InvalidOperationException("Over Strike 2 does not load: " + own.StartLine);
        }
        var setup = PluginSetup.FromFile(own);
        if (own.Troubled) Logger.LogWarning("Over Strike 2 {Settings}", own.StartLine); // where the settings came from - no value in it
        else Logger.LogInformation("Over Strike 2 {Settings}", own.StartLine);
        addon = setup.Addon;
        SteamWebApi.Gap = TimeSpan.FromSeconds(setup.ApiGapSeconds);
        SteamWebApi.UserAgent = SteamWebApi.AgentOf(Version);
        hud = new HudScreen(this, Logger, hotReload, setup);
        RegisterListener<Listeners.OnTick>(OnTick); // the freeze, the centre panels, the catch-up messages, the fun notice
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        AddCommandListener("say", OnSay);
        AddCommandListener("say_team", OnSay);
        // The settings module posts through the HUD (the block's notice); its console commands are os_fun, which the HUD and the bridge's reset
        // send, and os_settings and os_block, an operator's.
        settings = new GameSettings(this, Logger, (kind, text) => hud?.Post(kind, text), setup.TestBots);
        settings.Load(hotReload);
        RegisterConsoleCommandAttributeHandlers(settings);
        drops = new GrenadeDrops(this, Logger, () => settings.EffectOn(Fun.Drops));
        drops.Load();
        cloak = new Cloak(this, Logger);
        cloak.Load();
        effects = new FunEffects(this, Logger, settings.EffectOn, cloak);
        effects.Load();
        freeze = new VoteFreeze(this, Logger, () => hud);
        freeze.Load(); // first of the damage hooks: while everybody is frozen its Stop ends the hit there, before the bots' guard and catch-up see it
        new BotGuard(this, Logger, () => Bots.War(settings.FunValue), cloak).Load(); // no bot's damage to a player while Bot war is on or the cloak hides them - after the freeze's hook, before catch-up's
        // The spawn cloak (the menu window open on the spawn hides its player) goes by the HUD's window and the vote's freeze. It lives on in the handlers it registers.
        new SpawnCloak(this, Logger, cloak, slot => hud?.WindowOpen(slot) == true, () => freeze?.Frozen == true).Load();
        // The fairness restart (EarlyJoinWatch.cs): a player who comes into an Arms Race or Deathmatch match during its first minute starts it over, once per map,
        // through the bridge's Restart match. It goes by the mode the map that is on was loaded in, as the settings module has it.
        new EarlyJoinWatch(this, Logger, () => settings.Mode.Id, (account, name) => hud?.AskFairRestart(account, name) == true).Load(hotReload);
        catchUp = new CatchUp.CatchUpModule(this, Logger, Path.Combine(ModuleDirectory, CatchUp.CatchUpConfig.FileName));
        catchUp.Load(hotReload);
        // Vampire heals only from enemies, by catch-up's rule of who is one; takes only what catch-up's lifesteal leaves of the half a hit may
        // heal; and tells catch-up of every heal, for the life's line in its log.
        (effects.Enemies, effects.OtherShare, effects.Healed) = (catchUp.Enemies, catchUp.LifestealShare, catchUp.NoteHeal);
        // The Arms Race-only features follow the mode the map that is on was loaded in (os_mode; docs/reference.md) and, the grenade drops and
        // catch-up, their fun settings: each runs while its setting is on and the map is Arms Race.
        settings.ModeApplied += mode =>
        {
            drops.ModeAllows = mode.ArmsRace;
            CatchUpFollows($"{mode.Name} loaded");
            hud.SetMode(mode);
            Logger.LogInformation("Over Strike 2: {Mode} - grenade drops {Drops}, catch-up {CatchUp}", mode.Name,
                !mode.ArmsRace ? "off (Arms Race's own)" : settings.EffectOn(Fun.Drops) ? "on" : "off (the fun setting)", catchUpOn ? "on" : mode.ArmsRace ? "off (the fun setting)" : "off (Arms Race's own)");
        };
        settings.EffectChanged += (effect, on) =>
        {
            if (effect.Equals(Fun.Drops, StringComparison.OrdinalIgnoreCase))
            {
                if (!settings.Ending) drops.Switched(on); // the undo at a map's end hands nothing to the players of the map that goes
                Logger.LogInformation("Over Strike 2: grenade drops {State} (the fun setting){Mode}", on ? "on" : "off", settings.Mode.ArmsRace ? "" : $" - Arms Race's own, so off in {settings.Mode.Name} anyway");
            }
            else if (effect.Equals(Fun.CatchUp, StringComparison.OrdinalIgnoreCase)) CatchUpFollows($"its fun setting {(on ? "on" : "off")}");
            else effects?.Switched(effect, on); // Vampire and Ghost
        };
        settings.Ready(); // a load while a Deathmatch or Wingman map is on: the modules follow that map's mode now, as from its start
        hud.FunSource = settings.FunValue; // the fun page shows the settings module's values, its effects - the grenade drops and catch-up among them - included
        (hud.FunSetBy, hud.FunSet, hud.FunReset) = (settings.SetBy, settings.ChangeFun, settings.ResetFunPage); // ...who set each setting off its default, and the module makes the page's changes in process
        (hud.BotsSet, hud.BotsRejoin, hud.BotsReset) = (settings.ChangeBot, settings.RejoinBots, settings.ResetBots); // the Bots page's: a preset's settings, its bots joining again once, Kick all bots and Default
        hud.Frozen = () => freeze?.Frozen == true; // the window held by a vote follows the freeze's decision
        settings.Rematch = now => hud?.Rematch(now) ?? (0, 0); // the bridge's Restart match goes through the restart guard around its moment (RestartGuard)
        catchUp.Show = (player, boost, html) =>
        {
            // The card under the crosshair, or CS2's centre panel for a game that shows no card (HudScreen.HasCards); the same line in that player's chat either way.
            var card = hud.HasCards(player);
            hud.ShowCatchUp(player, CatchUp.Words.Why(boost, false).TrimEnd(':'), CatchUp.Words.Card(boost), boost.IsBounty, CatchUpMessageSeconds, card);
            if (card) return;
            boosts[player.Slot] = (html, DateTime.UtcNow.AddSeconds(CatchUpMessageSeconds)); // sent every tick for its seconds (OnTick)
            player.PrintToCenterHtml(html);
        };
        Logger.LogInformation("Over Strike 2 {Version}: HUD (Workshop addon {Addon}), {Command} from the bridge, TAB, the chat commands, the vote's freeze.", ModuleVersion, addon, Guide.Command);
    }

    public override void Unload(bool hotReload)
    {
        settings?.Unload(); // every player's fun change ends with this instance, and the next one says so (GameSettings.Unload)
        effects?.Unload();  // ...Vampire's health cut now, not on a frame after the instance has gone
        cloak?.Unload(); // nobody stays hidden by an instance that is gone
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

    /// <summary>Every tick: the freeze first; then the held centre texts, and the catch-up message of a game not known to hold the card, sent every tick as CounterStrikeSharp's
    /// CenterHtmlMenu does: a single send lasts only a moment. Nothing to show costs a few checks.</summary>
    void OnTick()
    {
        freeze?.Tick();
        hud?.CentreTick();
        if (boosts.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var (slot, (html, until)) in boosts.ToList())
        {
            if (now >= until) { boosts.Remove(slot); continue; }
            if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: false } p) p.PrintToCenterHtml(html);
            else boosts.Remove(slot);
        }
    }

    HookResult OnPlayerSpawn(EventPlayerSpawn ev, GameEventInfo info)
    {
        if (ev.Userid is not { IsValid: true } player) return HookResult.Continue;
        boosts.Remove(player.Slot); // a new life: its own message, if any, comes from the catch-up module
        hud?.Spawned(player);
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
        boosts.Remove(player.Slot); // a map change disconnects everyone too: nothing on screen survives it
        hud?.Gone(player.Slot);
        return HookResult.Continue;
    }

    /// <summary>Sent by the bridge over RCON on a real join, "first" for a player it has never seen: the welcome window, until its X. The reply starts with Guide.Shown when it
    /// worked; the bridge logs it.</summary>
    [ConsoleCommand(Guide.Command, "Over Strike 2: show the welcome to one player (the bridge sends this on a real join)")]
    [CommandHelper(minArgs: 1, usage: "<account id | [U:1:n] | SteamID64> [first]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnGuideCommand(CCSPlayerController? caller, CommandInfo command)
    {
        var id = Hud.AccountId(command.GetArg(1));
        var first = command.ArgCount > 2 && command.GetArg(2).Equals("first", StringComparison.OrdinalIgnoreCase);
        var player = id < 0 ? null : Utilities.GetPlayers().FirstOrDefault(p => !p.IsBot && Hud.AccountId(p.SteamID.ToString()) == id);
        if (player == null) { command.ReplyToCommand($"guide: no player {command.GetArg(1)} on the server"); return; }
        hud?.Joined(player, first);
        command.ReplyToCommand($"{Guide.Shown} to {player.PlayerName}{(first ? " (first time)" : "")}");
    }

    /// <summary>A player's chat line. It always shows as they typed it: the plugin only reads it (a / line is CounterStrikeSharp's silent trigger,
    /// which hides it). A line that starts with ! or / is a chat command; a Workshop link or id opens that item in the menu; and with the window on
    /// MAPS > Browse, any other line that is not just chat (gg, ty...) searches the browser, when its words find a map (233): chat is where a player
    /// types, and pastes - the HUD has no text entry, and the server cannot open the chat box.</summary>
    HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player is not { IsValid: true, IsBot: false } || hud == null) return HookResult.Continue;
        var text = info.ArgCount > 1 ? info.ArgString.Trim().Trim('"').Trim() : "";
        var who = player;
        var screen = hud;
        // A line that starts with ! or / does what the menu's click would, a frame later (not inside the player's own chat message: HudScreen's
        // summary), and answers in chat.
        var command = ChatCommands.Parse(text, screen.VoteOpen);
        if (command.Kind != ChatCommands.Kind.None)
            Server.NextFrame(() => { if (who.IsValid) screen.Chat(who, command); });
        else if (WorkshopCard.IdInChat(text) is { } id)
            Server.NextFrame(() => { if (who.IsValid) screen.OpenWorkshop(who, id); });
        else if (screen.SearchOf(who, text) is { } words) // the window on MAPS > Browse: the line searches it too, when its words find a map (233)
            Server.NextFrame(() => { if (who.IsValid) screen.ChatSearch(who, words); });
        return HookResult.Continue;
    }

    /// <summary>The bridge, before every map change and after every map start: os_mode &lt;id&gt; - the mode the next map loads in (Modes.cs;
    /// docs/reference.md). Bare os_mode - the bridge's question after a map start it did not ask for, an operator's look - says which mode was
    /// told and which one the map that is on loaded in (Modes.StateLine), and changes nothing. Server only.</summary>
    [ConsoleCommand(Modes.Command, "Over Strike 2: the game mode the next map loads in: os_mode armsrace|deathmatch|wingman; bare os_mode says which mode was told and which one the map loaded in")]
    [CommandHelper(minArgs: 0, usage: "[<mode id>]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnModeCommand(CCSPlayerController? caller, CommandInfo command)
    {
        var id = command.ArgCount > 1 ? command.GetArg(1).Trim() : "";
        if (id == "") { command.ReplyToCommand(settings == null ? "os_mode: no settings module" : settings.ModeState()); return; }
        if (Modes.Find(id) is not { } mode) { command.ReplyToCommand($"os_mode: no mode {id} ({string.Join(", ", Modes.All.Select(m => m.Id))})"); return; }
        command.ReplyToCommand(settings == null ? "os_mode: no settings module" : $"os_mode: {settings.SetMode(mode)}");
    }

    /// <summary>Server console / RCON (the restart watcher, os2-server): a line for everybody on the top line, in chat and on the board, for that many seconds.</summary>
    [ConsoleCommand("os_notice", "Over Strike 2: a line for everybody on the HUD's top line, in chat and on the TAB board: os_notice <seconds> <text>")]
    [CommandHelper(minArgs: 2, usage: "<seconds> <text>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnNoticeCommand(CCSPlayerController? caller, CommandInfo command)
    {
        var parts = command.ArgString.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out var seconds)) { command.ReplyToCommand("os_notice: <seconds> <text>"); return; }
        command.ReplyToCommand(hud == null ? "os_notice: no HUD" : $"os_notice: {hud.Notice(parts[1].Trim('"'), seconds)}");
    }

    /// <summary>Server console / RCON: what the HUD is doing - the entity, the bridge link, the last state, who has the window open.
    /// "os_hud click &lt;account id&gt; &lt;button id&gt;" sends what that button would, for that account, without a client.</summary>
    [ConsoleCommand("os_hud", "Over Strike 2 HUD: status; 'click <account> <button>' tries a button; 'tab off|down|bridge' sets TAB; 'release' gives every cursor back; 'item' visits the HUD item's page now; 'page <id>' tries the item check on any item")]
    [CommandHelper(minArgs: 0, usage: "[click <account id> <button id> | tab off|down|bridge | release | item | page <id> | show <account> <page> | content <account> <level> | level <n> [account] | press <account> <button>]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnStatusCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (hud == null) { command.ReplyToCommand("os_hud: not loaded"); return; }
        var verb = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "";
        if (verb == "centre") // the client harness: how CS2's plain centre text holds - os_hud centre <resend seconds> <seconds> <text>
        {
            command.ReplyToCommand(command.ArgCount < 5 || !double.TryParse(command.GetArg(2), out var every) || !double.TryParse(command.GetArg(3), out var length) ? "os_hud: centre <resend seconds> <seconds> <text>"
                : $"os_hud: {hud.TestCentre(every, length, command.GetArg(4), false)}");
            return;
        }
        if (verb == "tab")
        {
            var arg = command.ArgCount > 2 ? command.GetArg(2).ToLowerInvariant() : "";
            command.ReplyToCommand(arg is "off" or "down" ? $"os_hud: {hud.SetTab(arg)}" : arg == "bridge" ? $"os_hud: {hud.SetTab(null)}" : "os_hud: tab off | down | bridge");
            return;
        }
        if (verb == "release")
        {
            command.ReplyToCommand($"os_hud: {hud.ReleaseAll()}");
            return;
        }
        if (verb == "item") // after publishing an update: visit the item's Workshop page now, not in up to 2 minutes
        {
            command.ReplyToCommand($"os_hud: {hud.CheckItemNow()}");
            return;
        }
        if (verb == "page") // any item: what the item check would make of its page and Web API answer now - changes nothing
        {
            command.ReplyToCommand(command.ArgCount < 3 || !long.TryParse(command.GetArg(2), out var probe) || probe <= 0 ? "os_hud: page <workshop id>" : $"os_hud: {hud.ProbeItem(probe)}");
            return;
        }
        if (verb == "show") // the client harness: the window on a page for one account (it cannot click)
        {
            command.ReplyToCommand(command.ArgCount < 4 || Hud.AccountId(command.GetArg(2)) < 0 ? "os_hud: show <account id> <page>"
                : $"os_hud: {hud.ShowFor(Hud.AccountId(command.GetArg(2)), command.GetArg(3))}");
            return;
        }
        if (verb == "level") // the client harness: the version check's server level faked (os_hud level <n> [account]), and the join banner again
        {
            hud.TestLevel = command.ArgCount > 2 && int.TryParse(command.GetArg(2), out var lv) && lv > 0 ? lv : null;
            command.ReplyToCommand(command.ArgCount > 3 && Hud.AccountId(command.GetArg(3)) > 0 ? $"os_hud: {hud.Rejoin(Hud.AccountId(command.GetArg(3)))}"
                : $"os_hud: server level {(hud.TestLevel?.ToString() ?? "real")}");
            return;
        }
        if (verb == "content") // the client harness: which content that account's game holds (normally a click proves it)
        {
            command.ReplyToCommand(command.ArgCount < 4 || Hud.AccountId(command.GetArg(2)) < 0 || !int.TryParse(command.GetArg(3), out var level) ? "os_hud: content <account id> <level>"
                : $"os_hud: {hud.SetContent(Hud.AccountId(command.GetArg(2)), level)}");
            return;
        }
        if (verb == "press") // the client harness: a click through the real click path (os_hud press <account> <button>)
        {
            command.ReplyToCommand(command.ArgCount < 4 || Hud.AccountId(command.GetArg(2)) < 0 ? "os_hud: press <account id> <button id>"
                : $"os_hud: {hud.Press(Hud.AccountId(command.GetArg(2)), command.GetArg(3))}");
            return;
        }
        if (verb == "click")
        {
            command.ReplyToCommand(command.ArgCount < 4 || Hud.AccountId(command.GetArg(2)) < 0 ? "os_hud: click <account id> <button id>"
                : $"os_hud: {hud.SimulateClick(Hud.AccountId(command.GetArg(2)), command.GetArg(3))}");
            return;
        }
        command.ReplyToCommand($"os_hud: addon {addon}, {(freeze?.Frozen == true ? "everybody frozen for the vote" : "nobody frozen")}, {hud.Status()}");
    }
}
