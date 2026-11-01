using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The HUD on screen: one custom_hud_layout entity per map, driven from ../../bridge/Hud.cs. What to show is decided there (HudView for
/// the words, HudSlots for who has the window or a toast); this class owns the entity and sends the engine only what changed.
///
/// Rules this follows, each of which cost somebody else an evening (CS2UIKit's docs/GOTCHAS.md, MIT):
///   - no entity is touched before the first round start of a cold server: CounterStrikeSharp caches a failed entity-system lookup for the
///     life of the process. A spawned player proves a map is loaded, so a spawn may create it too;
///   - leftovers carrying our layout (a plugin reload, a crash) are removed before a new entity is made;
///   - a slot's state is cleared when a new player takes it, so nobody inherits a captured mouse;
///   - per-player state is sent again at spawn and two seconds later, because a class set while the client is still loading the layout
///     never lands;
///   - nothing heavy runs inside a player's own callbacks (spawn, chat command, click, hurt): those run while the server processes that
///     player's network messages, and more than 313 ms there disconnects the player with "Overflow error" - which the first join after
///     a server start did on 2026-10-05, the layout entity being created (and all of this code compiled for the first time) inside
///     its spawn event. They queue the work for the next frame instead, and the code is compiled ahead at load (WarmUp).</summary>
sealed class HudScreen
{
    const float FrameSeconds = 0.25f;

    readonly BasePlugin plugin;
    readonly ILogger log;
    readonly BridgeLink? bridge;
    readonly HudSlots slots = new();
    readonly string addon;                            // the Workshop item the server hands out (GUNGAME_HUD_ADDON): its link is in the check's messages
    readonly HudGate gate = new();                    // the HUD check on this map (HudCheck.cs)
    readonly HashSet<long> clicked = new();           // accounts seen clicking on this release since the plugin loaded (the bridge keeps them for good)
    readonly ItemWatch item;                          // whether Steam shows the item to everybody yet: nobody is disconnected until it does
    HudCheck.Mode checkMode;                          // the check in force as of the last frame
    HudCheck.Mode checkHigh;                          // the strictest it has been on this map: only a stricter one re-arms the waiting players
    readonly HudTab tab = new();                      // the TAB chord (HudTab.cs)
    HudTab.Mode tabMode;                              // the chord's mode as of the last frame (HUD_TAB, or css_gghud tab)
    HudTab.Mode? tabOverride;                         // css_gghud tab <mode>: until the plugin reloads
    readonly Dictionary<int, int> tabLog = new();     // TAB button lines logged per slot this session (TabLogLines at most)
    const int TabLogLines = 60;
    CCSCustomHudLayout? entity;
    bool worldReady;

    HudState? state;                                  // the bridge's newest state; null until the first answer
    VoteState vote = new();                           // the end-of-match vote and the modes, off the same text (MapVote.cs)
    readonly Dictionary<int, long> voteShown = new(); // slot -> the vote round whose window was opened for it (once a round)
    Modes.Mode mode = Modes.ArmsRace;                 // the mode the map that is on was loaded in (the settings module, os_mode)
    readonly Dictionary<int, DateTime> joinedAt = new(); // slot -> a real join: the version banner (and an older HUD's note) for a while
    HudCatalog? catalog;                              // the menu's map list, fetched again whenever the state's version moves
    HudSearch search = new();                         // the search index (names, tags, descriptions), fetched again when its version moves
    readonly WorkshopPlay plays;                      // unlisted Workshop ids: the Web API and the downloads (WorkshopPlay.cs)
    readonly Dictionary<int, WorkshopCard> cards = new(); // slot -> that player's card for an unlisted Workshop id
    (string Text, DateTime Until) localNews = ("", default);  // the news line from here, not the bridge: the restart watcher, the grenade switch
    readonly Dictionary<int, (string Text, DateTime Until)> localNotes = new(); // slot -> a note from here: no line to the bridge
    readonly Dictionary<int, HudBrowser> browsers = new();   // slot -> that player's map browser (sort, filters, page)
    readonly Dictionary<int, DateTime> restartArmed = new(); // slot -> the restart button's second click counts until then
    readonly Dictionary<(int Slot, string Panel), HashSet<string>> thumbs = new(); // the picture classes a card has on for a player
    int budget;                                       // engine calls this frame may still make; the rest go out in the next one
    const int FrameBudget = 600;
    DateTime stateAt;
    DateTime changeAt = DateTime.MaxValue;            // when the counting-down change fires, by our own clock
    string lastStateText = "";

    // What the entity holds already, so a frame only sends differences. Cleared whenever the entity is new.
    readonly Dictionary<string, string> globalTexts = new();
    readonly Dictionary<(string, string), bool> globalClasses = new();
    readonly Dictionary<int, Dictionary<string, string>> playerTexts = new();
    readonly Dictionary<int, Dictionary<(string, string), bool>> playerClasses = new();
    readonly Dictionary<int, bool> captured = new();
    readonly Dictionary<int, DateTime> resendAt = new(); // slot -> forget what it has (and send it again) at this time
    bool globalDirty, playerDirty, markBroken;           // what this frame changed, for MarkChanged

    // Contents v3_1_0 and v3_2_0 on the same layout path (Hud.Content): a panel one of them added (HudView.NewIds, NewIds32) is sent
    // nothing until that player's game has clicked a button only that content has (HudView.NewButtons, NewButtons32) - a game holding an
    // older content must never hear of a panel it lacks. What would go to everybody for those panels waits here, and goes out per player
    // to the accounts that have shown the content (HudView.LevelOf says which content a panel came with).
    readonly Dictionary<long, int> content = new();      // account -> the newest content its game has shown since the plugin loaded: 31 or 32
    readonly Dictionary<string, string> newTexts = new();
    readonly Dictionary<(string, string), bool> newClasses = new();
    readonly Dictionary<string, ConVar?> funVars = new(); // the fun page's convars (Fun.cs), found once
    const float FunCheckSeconds = 0.5f;                  // a fun change is made a frame after the click (gg_fun); looked at after this long
    bool addonRequired = true;                           // what pre.sh set at start: mm_client_extra_addons <addon>, every joiner downloads it
    HudCheck.Cause droppedCause;                         // why it was last dropped, for the words players get
    readonly HashSet<long> toldNoMenu = new();           // accounts told in chat why they have no menu, since it was dropped

    /// <summary>Poll the bridge even with nobody on the server: set when the HUD was switched on by hand (css_gghud on), to try it.</summary>
    public bool AlwaysPoll { get; set; }

    public HudScreen(BasePlugin plugin, ILogger log, bool hotReload, string addon)
    {
        this.plugin = plugin;
        this.log = log;
        this.addon = addon;
        item = new ItemWatch(addon);
        plays = new WorkshopPlay(log);
        bridge = BridgeLink.FromEnvironment(out var where);
        log.LogInformation("GunGameWelcome HUD: layout {Layout}, live state {Source}", Hud.Layout, bridge != null ? $"from the bridge at {where}" : $"OFF - {where}");
        plugin.RegisterListener<Listeners.OnCustomHudClicked>(OnClicked);
        plugin.RegisterListener<Listeners.OnMapStart>(_ => { slots.MapStarted(); resendAt.Clear(); gate.MapStarted(); tab.MapStarted(); tabLog.Clear(); checkHigh = HudCheck.Mode.Off; });
        plugin.RegisterListener<Listeners.OnPlayerButtonsChanged>(OnButtons);
        plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
        plugin.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        plugin.RegisterListener<Listeners.OnClientPutInServer>(OnPutInServer);
        plugin.RegisterEventHandler<EventRoundStart>((_, _) => { worldReady = true; EnsureEntity(); return HookResult.Continue; });
        plugin.RegisterEventHandler<EventPlayerHurt>(OnHurt);
        plugin.AddTimer(FrameSeconds, Frame, TimerFlags.REPEAT);
        WarmUp();
        if (hotReload) { worldReady = true; EnsureEntity(); } // a map is running: the one case where entities may be touched right away
    }

    /// <summary>Compiles every method of the HUD's own types now, at plugin load, so the first player's spawn or click does not pay for
    /// the JIT while the server is processing that player's messages (see the class summary).</summary>
    void WarmUp()
    {
        var clock = Stopwatch.StartNew();
        var count = 0;
        foreach (var type in new[] { plugin.GetType(), typeof(HudScreen), typeof(BridgeLink), typeof(Hud), typeof(HudView), typeof(HudSlots), typeof(HudState),
                     typeof(HudCatalog), typeof(HudBrowser), typeof(HudMenu), typeof(HudThumbs), typeof(HudCheck), typeof(HudGate), typeof(ItemWatch), typeof(HudTab),
                     typeof(HudSearch), typeof(HudKeyboard), typeof(WorkshopCard), typeof(WorkshopItem), typeof(WorkshopPlay), typeof(Fun), typeof(ChatCommands), typeof(DownReasons), typeof(HudVote) })
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.IsAbstract || method.ContainsGenericParameters) continue;
                try { RuntimeHelpers.PrepareMethod(method.MethodHandle); count++; } catch (Exception) { } // a method the JIT cannot prepare ahead is compiled on first use, as before
            }
        var pictures = HudThumbs.Count; // builds the pictures' lookup now too, not in the first maps page
        log.LogInformation("GunGameWelcome HUD: {Count} methods compiled ahead in {Ms} ms, {Pictures} map pictures numbered", count, clock.ElapsedMilliseconds, pictures);
    }

    public void Unload()
    {
        foreach (var slot in captured.Where(c => c.Value).Select(c => c.Key).ToList())
            if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true } p && Live) entity!.SetInputCaptureEnabled(p, false);
        if (Live) entity!.Remove();
        entity = null;
    }

    bool Live => entity is { IsValid: true };

    // ---- what the plugin and the bridge ask for ----

    /// <summary>css_guide: a real join. First time the window, otherwise the welcome-back toast.</summary>
    public void Joined(CCSPlayerController player, bool first)
    {
        slots.Joined(player.Slot, first, player.PawnIsAlive, DateTime.UtcNow);
        joinedAt[player.Slot] = DateTime.UtcNow;
        // The version check (HudVote.cs): a game whose content is not known to be Over Strike 2's gets the note an older HUD shows (a newer
        // game's stylesheet hides it); every newer game compares the levels itself, from gg_global's classes.
        if (ContentOf(player) < 32) LocalNote(player.Slot, HudVote.OldHudNote, HudVote.JoinSeconds);
        if (player.PawnIsAlive) { worldReady = true; Server.NextFrame(EnsureEntity); }
    }

    public void Info(CCSPlayerController player, string arg)
    {
        worldReady = true; // a client typing a command is in a loaded map
        slots.Info(player.Slot, arg, DateTime.UtcNow);
        Server.NextFrame(() => { EnsureEntity(); Frame(); }); // not inside the player's own chat command (see the class summary)
    }

    /// <summary>The menu, on its map browser (the board's Open full menu, TAB + E); with words, the browser searching for them from its
    /// first page (a Workshop link pasted in chat).</summary>
    public void Menu(CCSPlayerController player, string? find = null)
    {
        worldReady = true;
        if (find != null) Browser(player.Slot).Find(find);
        slots.Show(player.Slot, HudSlots.Page.Maps, DateTime.UtcNow);
        Server.NextFrame(() => { EnsureEntity(); Frame(); }); // not inside the player's own chat command (see the class summary)
    }

    HudBrowser Browser(int slot) => browsers.TryGetValue(slot, out var b) ? b : browsers[slot] = new HudBrowser();

    /// <summary>A Workshop link or id pasted in chat: the menu on Maps, searching for it - its card if it is not on the list.</summary>
    public void OpenWorkshop(CCSPlayerController player, long id) => Menu(player, id.ToString());

    /// <summary>gg_notice from the restart watcher (and the grenade switch): the news line, for everybody, for this long.</summary>
    public string Notice(string text, int seconds)
    {
        localNews = (Hud.Clean(text, 240), DateTime.UtcNow.AddSeconds(Math.Clamp(seconds, 3, 3600)));
        Server.NextFrame(Frame);
        return $"shown for {Math.Clamp(seconds, 3, 3600)} s: {localNews.Text}";
    }

    void LocalNote(int slot, string text, double seconds = 8) => localNotes[slot] = (Hud.Clean(text, 140), DateTime.UtcNow.AddSeconds(seconds));

    public void Spawned(CCSPlayerController player)
    {
        if (player.IsBot) return;
        worldReady = true;
        Server.NextFrame(EnsureEntity); // not inside the player's spawn: the first one after a start would be disconnected (class summary)
        slots.Spawned(player.Slot, DateTime.UtcNow);
        Forget(player.Slot);
        resendAt[player.Slot] = DateTime.UtcNow.AddSeconds(2);
        // The HUD check: the first spawn on a map of a player not seen clicking on this release opens the window, "Click to play" on its
        // gold button, and starts their clock (HudGate). Cheap bookkeeping only; the chat line goes out a frame later.
        var account = Account(player);
        if (gate.Spawned(player.Slot, account, Verified(account), DateTime.UtcNow) && checkMode != HudCheck.Mode.Off) Prompt(player.Slot, DateTime.UtcNow);
        TellNoMenu(player);
    }

    /// <summary>Seen clicking on this release: by the bridge (for good), or since this plugin loaded.</summary>
    bool Verified(long account) => clicked.Contains(account) || HudCheck.IsVerified(state, account, Hud.Release);

    /// <summary>The check's window for a slot, and its chat line a frame later.</summary>
    void Prompt(int slot, DateTime now)
    {
        slots.Check(slot, now);
        Server.NextFrame(() => { if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: false } p) p.PrintToChat(" " + HudCheck.Prompt); });
    }

    /// <summary>Shot while the window is open: give the mouse and the player's movement back at once (HudSlots.Hurt).</summary>
    HookResult OnHurt(EventPlayerHurt ev, GameEventInfo info)
    {
        if (ev.Userid is { IsValid: true, IsBot: false } p)
        {
            var now = DateTime.UtcNow;
            var released = tab.Release(p.Slot); // the board cursor too: shot means the hands are needed
            if (slots.WindowOpen(p.Slot)) slots.Hurt(p.Slot, now);
            else if (!released) return HookResult.Continue;
            Server.NextFrame(Frame);
        }
        return HookResult.Continue;
    }

    HookResult OnDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        if (ev.Userid is { IsValid: true, IsBot: false } p)
        {
            var tabWindow = tab.Window(p.Slot);
            if (!tab.Release(p.Slot)) return HookResult.Continue;
            var slot = p.Slot;
            Server.NextFrame(() => { if (tabWindow) slots.Info(slot, "off", DateTime.UtcNow); Frame(); });
        }
        return HookResult.Continue;
    }

    /// <summary>TAB (HudTab.cs). CounterStrikeSharp calls this inside the server's think for every button any player presses or lets go,
    /// so it only does HudTab's bookkeeping; opening a window, switching a cursor or writing the log waits for the next frame. While a
    /// TAB cursor or window is up (or TAB is held in an experiment mode) every change is logged, at most TabLogLines a session: that is
    /// how the experiment learns what the client still sends while it is captured.</summary>
    void OnButtons(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)
    {
        if (player is not { IsValid: true, IsBot: false }) return;
        var now = DateTime.UtcNow;
        var slot = player.Slot;
        if (tabMode == HudTab.Mode.Off && !tab.Cursor(slot) && !tab.Window(slot)) return;
        var watched = tab.Watching(slot, tabMode);
        var act = tab.Buttons(slot, (ulong)pressed, (ulong)released, tabMode, now);
        if ((watched || tab.Watching(slot, tabMode)) && tabLog.GetValueOrDefault(slot) < TabLogLines)
        {
            tabLog[slot] = tabLog.GetValueOrDefault(slot) + 1;
            var line = $"pressed {HudTab.Names((ulong)pressed)}, released {HudTab.Names((ulong)released)}, cursor {(tab.Cursor(slot) ? "on" : "off")}, " +
                $"captured {(captured.GetValueOrDefault(slot) ? "yes" : "no")}, window {(slots.WindowOpen(slot) ? "open" : "closed")}";
            Server.NextFrame(() => log.LogInformation("GunGameWelcome HUD TAB buttons: slot {Slot} {Line}", slot, line));
        }
        if (act == null) return;
        Server.NextFrame(() =>
        {
            if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true, IsBot: false } p) return;
            var at = DateTime.UtcNow;
            log.LogInformation("GunGameWelcome HUD TAB: {Name} {Act} (mode {Mode})", p.PlayerName, act switch
            {
                HudTab.Act.OpenMenu => "TAB + E - the menu",
                HudTab.Act.CursorOn => "the TAB cursor on, until TAB comes up",
                HudTab.Act.CursorOff => "let go of TAB - the TAB cursor off",
                HudTab.Act.OpenWindow => "TAB down - the window",
                HudTab.Act.CloseWindow => "let go of TAB - the window closed",
                _ => $"TAB down - the cursor in {HudTab.HoldSeconds} s if still held",
            }, HudTab.Name(tabMode));
            switch (act)
            {
                case HudTab.Act.OpenMenu: Menu(p); return;
                case HudTab.Act.OpenWindow: worldReady = true; tabLog[slot] = 0; slots.Show(slot, slots.PageOf(slot), at); EnsureEntity(); Frame(); return;
                case HudTab.Act.CloseWindow: slots.Info(slot, "off", at); Frame(); return;
                case HudTab.Act.HoldCheck:
                    plugin.AddTimer((float)HudTab.HoldSeconds, () =>
                    {
                        if (tab.Hold(slot, tabMode, DateTime.UtcNow) == null) return;
                        tabLog[slot] = 0;
                        if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: false } q)
                            log.LogInformation("GunGameWelcome HUD TAB: {Name} the TAB cursor on after {Hold} s, until TAB comes up (mode hold)", q.PlayerName, HudTab.HoldSeconds);
                        Frame();
                    });
                    return;
                case HudTab.Act.CursorOn: tabLog[slot] = 0; Frame(); return;
                default: Frame(); return;
            }
        });
    }

    /// <summary>css_gghud tab &lt;off|menu|on|down|hold|window&gt;: the mode until the plugin reloads (null: back to HUD_TAB from the bridge).</summary>
    public string SetTab(string? mode)
    {
        tabOverride = mode == null ? null : HudTab.Parse(mode);
        Server.NextFrame(Frame);
        return tabOverride == null ? "TAB: back to HUD_TAB from the bridge" : $"TAB: {HudTab.Name(tabOverride.Value)} until the plugin reloads";
    }

    /// <summary>css_gghud release: every window closed and every cursor given back - the way out if one ever sticks.</summary>
    public string ReleaseAll()
    {
        var now = DateTime.UtcNow;
        var cursors = tab.ReleaseAll();
        var windows = 0;
        foreach (var p in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false }))
            if (slots.WindowOpen(p.Slot)) { slots.Info(p.Slot, "off", now); windows++; }
        foreach (var slot in captured.Keys.ToList()) captured[slot] = true; // force the next frame to send capture off to everybody
        Server.NextFrame(Frame);
        return $"released: {windows} window(s), {cursors.Count} TAB cursor(s) or TAB window(s); capture off for everybody next frame";
    }

    /// <summary>css_gghud level &lt;n&gt;: the server's content level as the version check's classes show it - a client test of the
    /// mismatch banner (the client harness); null is the real one.</summary>
    public int? TestLevel { get; set; }

    /// <summary>css_gghud level: a real join again for that account's slot, so the version banner shows for a while (the client harness).</summary>
    public string Rejoin(long account)
    {
        if (Utilities.GetPlayers().FirstOrDefault(x => x is { IsValid: true, IsBot: false } && Account(x) == account) is not { } p) return $"no player [U:1:{account}]";
        joinedAt[p.Slot] = DateTime.UtcNow;
        if (ContentOf(p) < 32) LocalNote(p.Slot, HudVote.OldHudNote, HudVote.JoinSeconds);
        Server.NextFrame(Frame);
        return $"join banner for {p.PlayerName} for {HudVote.JoinSeconds:0} s (server level {TestLevel ?? HudVote.ContentLevel})";
    }

    /// <summary>The settings module's mode went on (os_mode, a map start): the HUD's classes follow it.</summary>
    public void SetMode(Modes.Mode m) { mode = m; Server.NextFrame(Frame); }

    public void Gone(int slot)
    {
        slots.Gone(slot);
        voteShown.Remove(slot);
        joinedAt.Remove(slot);
        gate.Gone(slot);
        tab.Gone(slot);
        tabLog.Remove(slot);
        browsers.Remove(slot);
        cards.Remove(slot);
        localNotes.Remove(slot);
        restartArmed.Remove(slot);
        Forget(slot);
        captured.Remove(slot);
        resendAt.Remove(slot);
    }

    public string Status() =>
        $"entity {(Live ? entity!.Index.ToString() : "none")}, world {(worldReady ? "ready" : "not ready")}, " +
        $"state {(state == null ? "none" : $"{(DateTime.UtcNow - stateAt).TotalSeconds:0}s old: {state.Map} by {state.By}, {state.Next.Count} queued")}, " +
        $"bridge {(bridge == null ? "off" : bridge.Failures == 0 ? "ok" : $"{bridge.Failures} failures ({bridge.LastError})")}, " +
        $"window open for [{string.Join(", ", Utilities.GetPlayers().Where(p => slots.WindowOpen(p.Slot)).Select(p => p.PlayerName))}], " +
        $"check {HudCheck.Name(checkMode)} (HUD_CHECK {(state == null ? "?" : HudCheck.Name(HudCheck.Parse(state.CheckMode)))}, {Hud.Release} " +
        $"{(state?.Proofs.TryGetValue(Hud.Release, out var proof) == true ? "proven by " + proof : "not proven yet")}, item {item.Describe(DateTime.UtcNow)}), " +
        $"waiting for a click from [{string.Join(", ", Utilities.GetPlayers().Where(p => gate.Checking(p.Slot)).Select(p => p.PlayerName))}], " +
        $"TAB chord {HudTab.Name(tabMode)}{(tabOverride != null ? " (css_gghud tab)" : "")}, board cursor for [{string.Join(", ", Utilities.GetPlayers().Where(p => tab.Cursor(p.Slot)).Select(p => p.PlayerName))}], " +
        $"search index {search.Count} maps (version {search.Version}), workshop {plays.Describe()}, " +
        $"addon {(addonRequired ? "required of joiners" : "NOT required (Steam does not show it to everybody)")}, content v3_1_0 shown by {content.Values.Count(v => v >= 31)} and v3_2_0 by {content.Values.Count(v => v >= 32)} account(s), fun {string.Join(", ", Fun.Settings.Select(s => $"{s.Id} {Fun.Showing(s, FunValue).Id}"))}";

    // ---- the entity ----

    void EnsureEntity()
    {
        if (Live || !worldReady) return;
        var clock = Stopwatch.StartNew();
        try
        {
            var stale = 0;
            foreach (var old in Utilities.FindAllEntitiesByDesignerName<CCSCustomHudLayout>("custom_hud_layout"))
                if (old.IsValid && old.StrLayout == Hud.Layout) { old.Remove(); stale++; }
            entity = Utilities.CreateEntityByName<CCSCustomHudLayout>("custom_hud_layout");
            if (entity is not { IsValid: true }) { log.LogWarning("GunGameWelcome HUD: could not create custom_hud_layout"); entity = null; return; }
            entity.StrLayout = Hud.Layout;
            entity.DispatchSpawn();
            ForgetAll();
            log.LogInformation("GunGameWelcome HUD: layout entity {Index} spawned in {Ms} ms{Stale}", entity.Index, clock.ElapsedMilliseconds, stale > 0 ? $", {stale} leftover(s) removed" : "");
        }
        catch (Exception e)
        {
            entity = null;
            log.LogWarning("GunGameWelcome HUD: spawning the layout entity failed: {Message}", e.Message);
        }
    }

    void OnMapEnd()
    {
        worldReady = false; // the map takes the entity with it; the next round start (or spawn) makes a new one
        entity = null;
        ForgetAll();
        captured.Clear();
        gate.MapStarted();
        tab.MapStarted();
        checkHigh = HudCheck.Mode.Off;
    }

    /// <summary>A new player in a slot: whatever the last occupant had open is closed for them, before anything else is sent.</summary>
    void OnPutInServer(int slot)
    {
        slots.Gone(slot);
        gate.Gone(slot);
        tab.Gone(slot);
        tabLog.Remove(slot);
        browsers.Remove(slot);
        cards.Remove(slot);
        localNotes.Remove(slot);
        restartArmed.Remove(slot);
        Forget(slot);
        if (Live && Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: false } p)
        {
            foreach (var cls in new[] { "win", "win-in", "draining", "toast-1", "toast-2", "toast-in" }.Concat(Enum.GetValues<HudSlots.Page>().Select(page => "page-" + page.ToString().ToLowerInvariant())))
                entity!.SetHasClassForPlayer(p, HudView.Screen, cls, false);
            entity!.SetInputCaptureEnabled(p, false);
            playerDirty = true;
        }
        captured[slot] = false;
    }

    /// <summary>Send this slot everything again. What its cards' pictures have on is kept, through a new player in the slot too: those
    /// classes may still be on in the client, and the next maps page switches off whatever it does not want (only a new entity starts
    /// clean).</summary>
    void Forget(int slot) { playerTexts.Remove(slot); playerClasses.Remove(slot); }

    void ForgetAll() { globalTexts.Clear(); globalClasses.Clear(); globalVars.Clear(); playerTexts.Clear(); playerClasses.Clear(); thumbs.Clear(); }

    // ---- clicks ----

    void OnClicked(CCSPlayerController player, CCSCustomHudLayout layout, string buttonId)
    {
        if (!Live || player is not { IsValid: true, IsBot: false } || layout is not { IsValid: true } || layout.Index != entity!.Index) return;
        var now = DateTime.UtcNow;
        // Any click at all proves this player's game shows the HUD (the check, HudCheck.cs). While they were being checked, the gold
        // button said "Click to play": that click is the check and closes the window - it must not roll a map as !next would.
        var who = Account(player);
        var wasChecked = gate.Checking(player.Slot);
        if (!Verified(who)) Verify(player, who, wasChecked);
        if (who > 0 && HudView.LevelOfButton(buttonId) is > 0 and var proves && proves > content.GetValueOrDefault(who)) // only a game with that content has this button
        {
            content[who] = proves;
            log.LogInformation("GunGameWelcome HUD: {Name} [U:1:{Account}] clicked {Button} - their game has content {Content}; its new panels get words from now on",
                player.PlayerName, who, buttonId, proves >= 32 ? Hud.Content : "v3_1_0");
        }
        if (wasChecked && buttonId == HudView.ButtonAct) { slots.Click(player.Slot, HudView.ButtonClose, now); Server.NextFrame(Frame); return; }
        if (buttonId == HudMenu.BoardOpen && !slots.WindowOpen(player.Slot)) { tab.Release(player.Slot); Menu(player); return; } // the board's button: the window keeps the cursor
        if (buttonId is HudView.BoardUp or HudView.BoardDown or HudView.BoardNext or HudView.BoardBig or HudView.BoardAds) // the board's own buttons, under the TAB cursor
        {
            if (HudView.Command(buttonId, state, who) is { } boardCommand) Act(player, who, boardCommand);
            Server.NextFrame(Frame);
            return;
        }
        if (!slots.WindowOpen(player.Slot)) return;
        slots.Click(player.Slot, buttonId, now);
        var account = Account(player);
        var browser = Browser(player.Slot);
        if (vote.Round > 0 && slots.PageOf(player.Slot) == HudSlots.Page.Maps)
        {
            // The vote is on the Maps page's cards (HudVote.cs): a card's Play is a vote; the browser's own controls wait for the vote's end.
            if (HudVote.Click(vote, buttonId) is { } cast)
            {
                var was = vote.ChoiceOf(account);
                Act(player, account, cast);
                if (MapVote.ParseClick(cast) is { } picked && picked.Option != was) SayForAll(player, HudVote.ChatWords(picked.Option)); // so menu users learn the chat way
            }
            Server.NextFrame(Frame);
            return;
        }
        if (catalog != null && browser.Click(buttonId, browser.Matches(catalog, account).Count)) { Server.NextFrame(Frame); return; }
        if (buttonId is HudMenu.NadesOn or HudMenu.NadesOff && !mode.ArmsRace)
        {
            LocalNote(player.Slot, $"Grenade drops are Arms Race's own - not in {mode.Name}");
            Server.NextFrame(Frame);
            return;
        }
        if (buttonId is HudMenu.NadesOn or HudMenu.NadesOff)
        {
            // GunGameNades owns the toggle (gg_nades, a server command since chat commands went): run it, and say who switched it on the
            // news line. A frame later, like everything a click starts: it hands every living player a grenade.
            var onOff = buttonId == HudMenu.NadesOn ? "on" : "off";
            var name = Hud.Clean(player.PlayerName, 32).Replace("\"", "'");
            Server.NextFrame(() => { Server.ExecuteCommand($"gg_nades {onOff} \"{name}\""); Notice($"{name} switched grenade drops {onOff}", 8); });
            return;
        }
        if (HudMenu.FunClick(buttonId) is { } fun)
        {
            // MapSettingsReset makes the change (gg_fun): it owns the server's settings - the baseline, the put-back at the map's end and the
            // block a player's change is exempt from. The news line says who switched what once the value is there, as for the grenade drops.
            var (setting, choice) = fun;
            var slot = player.Slot;
            var name = Hud.Clean(player.PlayerName, 32).Replace("\"", "'");
            if (Fun.Showing(setting, FunValue) == choice) { LocalNote(slot, $"{setting.Title}: {choice.Label} already"); Server.NextFrame(Frame); return; }
            Server.NextFrame(() => Server.ExecuteCommand($"{Fun.Command} {setting.Id} {choice.Id} \"{name}\""));
            plugin.AddTimer(FunCheckSeconds, () =>
            {
                if (Fun.Showing(setting, FunValue) == choice) Notice(Fun.News(name, setting, choice), 8);
                else LocalNote(slot, $"{setting.Title} did not switch: the server's settings plugin did not take it");
                Frame();
            });
            return;
        }
        if (buttonId == WorkshopCard.Play) { Download(player); Server.NextFrame(Frame); return; }
        if (buttonId == HudMenu.Restart)
        {
            var armed = restartArmed.TryGetValue(player.Slot, out var until) && now < until;
            if (armed) restartArmed.Remove(player.Slot); else restartArmed[player.Slot] = now.AddSeconds(HudBrowser.ConfirmSeconds);
        }
        var command = HudView.Command(buttonId, state, account) ?? HudMenu.Command(buttonId, catalog, state, browser, account, now, search);
        if (command != null) Act(player, account, command);
        Server.NextFrame(Frame); // not inside the player's own click message (see the class summary)
    }

    /// <summary>A line for everybody that reads like this player's own chat line, in CS2's own format (Cstrike_Chat_All: " [ALL] name: text"):
    /// what a click on a ballot card says, as if they had typed the command. A frame later, like everything a click starts.</summary>
    void SayForAll(CCSPlayerController player, string words)
    {
        var colour = player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist ? ChatColors.ForTeam(player.Team) : ChatColors.Default;
        var line = $" {colour}[ALL] {Hud.Clean(player.PlayerName, 32).Replace('%', ' ')}{ChatColors.Default}: {words}";
        Server.NextFrame(() => Server.PrintToChatAll(line));
    }

    /// <summary>Ask the bridge for what a click stands for; without a line to it, the player's note says so.</summary>
    void Act(CCSPlayerController player, long account, string command)
    {
        if (bridge == null) LocalNote(player.Slot, "The menu has no line to the server right now - try again in a moment");
        else bridge.Act(account, player.PlayerName, command);
    }

    /// <summary>The card's Download and play: straight to the bridge when the item is in the server's cache already, otherwise its download
    /// (now, or after the one that runs) - the pick follows once it is installed (WorkshopCards).</summary>
    void Download(CCSPlayerController player)
    {
        if (!cards.TryGetValue(player.Slot, out var card) || card.Item is not { Refusal: null } item || card.At is not (WorkshopCard.Step.Ready or WorkshopCard.Step.Failed)) return;
        if (plays.Installed(item.Id)) { card.Installed(); return; }
        card.Started(queued: !plays.Start(item.Id, item.Bytes));
    }

    /// <summary>Every frame: each player's card moves on - the Workshop's answer, the download's progress, and once it is installed the
    /// pick itself, asked for in that player's name like a card's Play.</summary>
    void WorkshopCards(List<CCSPlayerController> humans, DateTime now)
    {
        foreach (var p in humans)
        {
            if (!cards.TryGetValue(p.Slot, out var card) || card.At == WorkshopCard.Step.None) continue;
            switch (card.At)
            {
                case WorkshopCard.Step.Checking:
                    if (catalog != null && catalog.Refused.TryGetValue(card.Id, out var no)) card.Refuse(no.Name, no.Why); // the server's own no: nothing to ask
                    else if (now - card.Since < WorkshopCard.Settle) break;                                                   // still being typed
                    else if (plays.Ask(card.Id, now) is { Done: true } answer) card.Checked(answer.Item, answer.Error);
                    break;
                case WorkshopCard.Step.Waiting or WorkshopCard.Step.Downloading:
                    var (step, percent, busy, why) = plays.Status(card.Id, now);
                    if (step == WorkshopCard.Step.Installed) card.Installed();
                    else if (step == WorkshopCard.Step.Failed) card.Failed(why ?? "the download failed");
                    else if (step == WorkshopCard.Step.Downloading) card.Progress(percent, busy);
                    break;
                case WorkshopCard.Step.Installed:
                    Act(p, Account(p), $"!map {card.Id}");
                    card.Requested();
                    log.LogInformation("GunGameWelcome Workshop: {Id} installed - asked for it as {Name}'s pick", card.Id, p.PlayerName);
                    break;
            }
        }
    }

    static long Account(CCSPlayerController p) => p.SteamID >= 76561197960265728UL ? (long)(p.SteamID - 76561197960265728UL) : -1;

    /// <summary>The first click of an account on this release: verified here at once, and for good at the bridge (!hudok, which only
    /// this real click sends - never css_gghud click, never chat). The bridge's first one on a release is the proof that clicks work.</summary>
    void Verify(CCSPlayerController player, long account, bool wasChecked)
    {
        if (account <= 0) return;
        clicked.Add(account);
        gate.Verified(player.Slot);
        bridge?.Act(account, player.PlayerName, HudCheck.Command(Hud.Release));
        log.LogInformation("GunGameWelcome HUD check: {Name} [U:1:{Account}] clicked in the HUD ({Release})", player.PlayerName, account, Hud.Release);
        var slot = player.Slot;
        if (wasChecked) Server.NextFrame(() => { if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: false } p) p.PrintToChat(" " + HudCheck.Thanks); });
    }

    /// <summary>The check's mode now: what the bridge says (a dry run turns into enforce at the first real click on this release), off
    /// without a fresh state, and never more than a dry run while Steam does not show the item to everybody - a player whose game cannot
    /// download it would be thrown out for nothing. A stricter mode gives everybody still waiting a fresh grace period and the window.</summary>
    void UpdateCheck(DateTime now)
    {
        var configured = HudCheck.Parse(state?.CheckMode);
        item.Poll(now, true); // always: whether joiners must download the addon follows it too (RequireAddon)
        RequireAddon(now);
        var answered = bridge == null || bridge.LastAnswer == default ? double.MaxValue : (now - bridge.LastAnswer).TotalSeconds;
        var mode = HudCheck.ModeOf(state, answered, Hud.Release);
        if (mode == HudCheck.Mode.Enforce && !item.Public(now)) mode = HudCheck.Mode.DryRun;
        if (!addonRequired) mode = HudCheck.Mode.Off; // nobody is made to download it now, so nobody may be asked for it either
        if (mode == checkMode) return;
        log.LogInformation("GunGameWelcome HUD check: {From} -> {To} (HUD_CHECK {Configured}, release {Release} {Proof}, item {Item})", HudCheck.Name(checkMode), HudCheck.Name(mode),
            HudCheck.Name(configured), Hud.Release, state?.Proofs.ContainsKey(Hud.Release) == true ? "proven" : "not proven yet", item.Describe(now));
        if (mode > checkHigh) // once per step up on a map, never on a flap - and never for a player the bridge knows (a spawn before its first answer)
        {
            foreach (var slot in gate.Rearm(now))
                if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: false } w && !Verified(Account(w))) Prompt(slot, now);
            checkHigh = mode;
        }
        checkMode = mode;
    }

    /// <summary>The owner's rule of 2026-10-22: while Steam does not show the HUD item to everybody (an update waiting for Valve's review
    /// can do that), the server must not require it - MultiAddonManager would hold every joiner at a download that cannot succeed. So
    /// a Web API verdict of gone, private or banned, or a page visit after the Web API showed a new version that finds Steam's not-shown
    /// page, takes the item off mm_client_extra_addons (new joiners come in without the HUD; players who have it keep it until they
    /// reconnect), and the visit that finds it shown puts it back (ItemPlan in HudCheck.cs: since 2026-11-01 the page is visited only
    /// after a change, at most every 10 minutes for 3 hours - no idle polling). MultiAddonManager re-reads the list for every connection
    /// (its change callback, v1.6.2 and v1.7.1). No fresh word from the Web API: as it was (at a start: as pre.sh set it).</summary>
    void RequireAddon(DateTime now)
    {
        var cause = item.Cause;
        var required = item.Required(addonRequired, now);
        if (!required && addonRequired)
        {
            Server.ExecuteCommand("mm_client_extra_addons \"\"");
            addonRequired = false;
            droppedCause = cause;
            toldNoMenu.Clear();
            log.LogWarning("Over Strike 2 HUD: Steam does not show Workshop item {Addon} to everybody ({Cause}) - joiners no longer have to download it (mm_client_extra_addons emptied), the HUD check is off",
                addon, cause switch { HudCheck.Cause.Review => $"the version uploaded {item.Updated} under review", HudCheck.Cause.Unavailable => "the Web API says it is not downloadable", _ => "a page visit by hand found it hidden" });
            // Informative errors for the players (the owner's decision of 2026-11-01): the news line for those who still have the HUD, and
            // one chat line for everybody, since a player who joins from now on has no HUD to read anything on.
            Notice(HudCheck.DroppedNews(cause, item.Updated), 45);
            var chat = HudCheck.DroppedChat(cause);
            foreach (var p in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false })) { p.PrintToChat(" " + chat); toldNoMenu.Add(Account(p)); }
        }
        else if (required && !addonRequired)
        {
            Server.ExecuteCommand($"mm_client_extra_addons {addon}");
            addonRequired = true;
            toldNoMenu.Clear();
            log.LogInformation("Over Strike 2 HUD: Steam shows Workshop item {Addon} to everybody again - joiners download it again (mm_client_extra_addons {Addon})", addon, addon);
            Notice(HudCheck.BackNews, 30);
            foreach (var p in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false })) p.PrintToChat(" " + HudCheck.BackChat);
        }
    }

    /// <summary>A spawn while joiners do not get the HUD: a player who joined since has no menu and no news line, so one private chat
    /// line - once per player until the HUD is back - says why and what to do (nothing: play on).</summary>
    void TellNoMenu(CCSPlayerController player)
    {
        if (addonRequired || !toldNoMenu.Add(Account(player))) return;
        var slot = player.Slot;
        var line = HudCheck.NoMenuChat(droppedCause, item.Updated);
        Server.NextFrame(() => { if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: false } p) p.PrintToChat(" " + line); });
    }

    /// <summary>css_gghud item: the item's page visited now; says what the last visit found and whether joiners must download it.</summary>
    public string CheckItemNow()
    {
        item.Soon();
        return $"one item page visit due now, by hand (the answer shows in css_gghud in a few seconds); so far: {item.Describe(DateTime.UtcNow)}, addon {(addonRequired ? "required of joiners" : "NOT required")}";
    }

    /// <summary>css_gghud page &lt;id&gt;: what the item check would make of any item's page and Web API answer right now - logged when the
    /// answers are in (a few seconds); changes nothing.</summary>
    public string ProbeItem(long other)
    {
        _ = item.Probe(other).ContinueWith(t => log.LogInformation("GunGameWelcome HUD item probe: {Result}",
            t.IsCompletedSuccessfully ? t.Result : $"{other}: failed ({t.Exception?.GetBaseException().Message})"), TaskScheduler.Default);
        return $"probing {other}'s Workshop page and the Web API - the answer goes to the server log as 'GunGameWelcome HUD item probe:'";
    }

    /// <summary>What the check's clock says is due: the warning in chat, then the disconnect - in a dry run only a log line.</summary>
    void RunCheck(DateTime now)
    {
        foreach (var (slot, act, seconds) in gate.Frame(now, checkMode, Verified))
        {
            if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true, IsBot: false, IsHLTV: false } p) { gate.Gone(slot); continue; }
            var account = Account(p);
            switch (act)
            {
                case HudGate.Act.Warn:
                    foreach (var line in HudCheck.Warning(addon, seconds, checkMode != HudCheck.Mode.Enforce)) p.PrintToChat(" " + line);
                    log.LogInformation("GunGameWelcome HUD check: warned {Name} [U:1:{Account}] ({Mode}, {Seconds} s left)", p.PlayerName, account, HudCheck.Name(checkMode), seconds);
                    break;
                case HudGate.Act.WouldKick:
                    log.LogInformation("GunGameWelcome HUD check (dry run): would disconnect {Name} [U:1:{Account}] - no click from the HUD in {Grace} s", p.PlayerName, account, HudCheck.GraceSeconds);
                    break;
                case HudGate.Act.Kick:
                    log.LogWarning("GunGameWelcome HUD check: disconnecting {Name} [U:1:{Account}] - no click from the HUD in {Grace} s", p.PlayerName, account, HudCheck.GraceSeconds);
                    if (p.UserId is int userId) Server.ExecuteCommand($"kickid {userId} \"{HudCheck.Reason(addon)}\"");
                    else p.Disconnect(CounterStrikeSharp.API.ValveConstants.Protobuf.NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
                    break;
            }
        }
    }

    // ---- the chat commands (ChatCommands.cs) ----

    readonly Dictionary<long, (DateTime At, string Note)> chatAsked = new(); // account -> a chat command's time, and the note seen before it

    /// <summary>The end-of-match vote is open: a bare number in chat is a vote (ChatCommands.Parse).</summary>
    public bool VoteOpen => vote is { Open: true, Round: > 0 };

    /// <summary>A chat command, a frame after the line: what the menu's click would do, and the answer in chat - the bridge's note for that
    /// player is echoed there once it comes (EchoNotes), for a player whose game shows no HUD. An alias's reply first teaches the primary.</summary>
    public void Chat(CCSPlayerController player, ChatCommands.Parsed cmd)
    {
        var account = Account(player);
        var now = DateTime.UtcNow;
        void Say(string line) => player.PrintToChat(" " + Hud.Clean(line, ChatCommands.LineMax));
        void Bridge(string command)
        {
            chatAsked[account] = (now, state?.Notes.TryGetValue(account, out var n) == true ? n.Text : "");
            Act(player, account, command);
        }
        if (ChatCommands.Teach(cmd) is { } taught) Say(taught);
        switch (cmd.Kind)
        {
            case ChatCommands.Kind.Bridge:
                var action = cmd.Arg == "next" ? HudView.Command(HudView.BoardNext, state, account) : cmd.Arg; // the board's Next, as the board reads for this player
                if (action == null) { Say("Next: nothing to do while a map change is under way"); return; }
                Bridge(action);
                return;
            case ChatCommands.Kind.Top:
                var sorted = Browser(player.Slot);
                sorted.Find("");
                (sorted.Sort, sorted.Descending) = (HudBrowser.SortBy.Votes, false);
                ShowFor(account, cmd.Arg);
                return;
            case ChatCommands.Kind.Page:
                ShowFor(account, cmd.Arg);
                if (cmd.Arg == "Fun") foreach (var line in ChatCommands.FunLines(mode.ArmsRace)) Say(line);
                else if (cmd.Typed == "") Say($"The menu is open on {cmd.Arg}");
                if (ContentOf(player) < HudVote.PageLevel(cmd.Arg)) Say(HudVote.PageHint(cmd.Arg)); // its words wait for a click on a button only that content has
                return;
            case ChatCommands.Kind.Find:
                Menu(player, cmd.Arg);
                Say($"The menu's map browser is searching for \"{Hud.Clean(cmd.Arg, 40)}\"");
                return;
            case ChatCommands.Kind.Play:
                Play(player, account, cmd.Arg, Say, Bridge);
                return;
            case ChatCommands.Kind.Fun:
                var parts = cmd.Arg.Split(' ');
                if (Fun.Find(parts[0]) is not { } setting || Fun.Find(setting, parts[1]) is not { } choice) return;
                // what the Fun page leaves out outside Arms Race (HudMenu.FunPage), chat leaves alone too
                if (!mode.ArmsRace && Fun.ArmsRaceOnly(setting)) { Say($"{setting.Title} is Arms Race's own - not in {mode.Name}"); return; }
                var name = Hud.Clean(player.PlayerName, 32).Replace("\"", "'");
                if (Fun.Showing(setting, FunValue) == choice) { Say($"{setting.Title}: {choice.Label} already"); return; }
                Server.ExecuteCommand($"{Fun.Command} {setting.Id} {choice.Id} \"{name}\"");
                plugin.AddTimer(FunCheckSeconds, () => { if (Fun.Showing(setting, FunValue) == choice) Notice(Fun.News(name, setting, choice), 8); else Say($"{setting.Title} did not switch"); });
                return;
            case ChatCommands.Kind.Nades:
                if (!mode.ArmsRace) { Say($"Grenade drops are Arms Race's own - not in {mode.Name}"); return; }
                if (NadesOn?.Invoke() == (cmd.Arg == "on")) { Say($"Grenade drops: {cmd.Arg} already"); return; }
                var who = Hud.Clean(player.PlayerName, 32).Replace("\"", "'");
                Server.ExecuteCommand($"gg_nades {cmd.Arg} \"{who}\"");
                Notice($"{who} switched grenade drops {cmd.Arg}", 8);
                return;
            case ChatCommands.Kind.Vote:
                var k = int.Parse(cmd.Arg);
                if (!vote.Open || vote.Round <= 0) { Say("No vote is on - it opens when a match ends"); return; }
                if (k > vote.Options.Count) { Say($"The vote has {vote.Options.Count} options: !vote 1 to {vote.Options.Count}"); return; }
                Bridge(MapVote.Click(vote.Round, vote.Options[k - 1].N));
                Say($"Your vote: {Hud.Clean(vote.Options[k - 1].Label, 40)}");
                return;
            case ChatCommands.Kind.Commands:
                foreach (var line in ChatCommands.HelpLines()) Say(line);
                ShowFor(account, "Help");
                return;
            case ChatCommands.Kind.Usage:
                Say(ChatCommands.UsageOf(cmd.Name));
                return;
            case ChatCommands.Kind.Unknown:
                Say(ChatCommands.UnknownLine(cmd.Name));
                return;
        }
    }

    /// <summary>!play &lt;map&gt;: a number is a map's rank (its !map); a Workshop id no listed map has opens its card (the menu's download);
    /// an official map's name is that map; words are a search - one clear best match is played, otherwise the browser opens on them.</summary>
    void Play(CCSPlayerController player, long account, string what, Action<string> say, Action<string> bridge)
    {
        var q = what.Trim();
        if (WorkshopCard.IdOf(q) is { } wid && catalog?.Maps.All(m => m.Id != wid) == true && q.Length > 6) { OpenWorkshop(player, wid); say("Its card is open in the menu: Download and play"); return; }
        if (catalog == null) { say("The map list is not loaded yet - try again in a moment"); return; }
        var exact = catalog.Maps.FirstOrDefault(m => m.Arg.Equals(q, StringComparison.OrdinalIgnoreCase) || m.Id.ToString(CultureInfo.InvariantCulture) == q
            || m.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
        if (exact != null) { bridge($"!map {exact.Arg}"); return; }
        var browser = new HudBrowser();
        browser.Find(q);
        var hits = browser.Matches(catalog, account, search);
        if (hits.Count == 1) { bridge($"!map {hits[0].Arg}"); return; }
        Menu(player, q);
        say(hits.Count == 0 ? $"No map matches \"{Hud.Clean(q, 40)}\" - other words, a number, or a Workshop ID" : $"{hits.Count} maps match - the menu shows them: click Play on one");
    }

    /// <summary>Every frame: a chat command's answer - the bridge's note for that player - goes to their chat too, once.</summary>
    void EchoNotes(DateTime now)
    {
        if (chatAsked.Count == 0 || state == null) return;
        foreach (var (account, (at, before)) in chatAsked.ToList())
        {
            if ((now - at).TotalSeconds > 5) { chatAsked.Remove(account); continue; }
            if (!state.Notes.TryGetValue(account, out var note) || note.Text == before || note.Text == "") continue;
            chatAsked.Remove(account);
            if (Utilities.GetPlayers().FirstOrDefault(x => x is { IsValid: true, IsBot: false } && Account(x) == account) is { } p) p.PrintToChat(" " + Hud.Clean(ChatCommands.InChat(note.Text), 140));
        }
    }

    /// <summary>css_gghud show &lt;account&gt; &lt;page&gt;: the window on that page for that account, as if they had clicked their way there - for
    /// the client test harness, whose CS2 never has focus and so cannot click.</summary>
    public string ShowFor(long account, string page)
    {
        if (!Enum.TryParse<HudSlots.Page>(page, ignoreCase: true, out var p)) return $"no page {page} (pages: {string.Join(", ", Enum.GetNames<HudSlots.Page>())})";
        if (Utilities.GetPlayers().FirstOrDefault(x => x is { IsValid: true, IsBot: false } && Account(x) == account) is not { } player) return $"no player [U:1:{account}] on the server";
        worldReady = true;
        slots.Show(player.Slot, p, DateTime.UtcNow);
        Server.NextFrame(() => { EnsureEntity(); Frame(); });
        return $"window on {p} for {player.PlayerName}";
    }

    /// <summary>css_gghud content &lt;account&gt; &lt;level&gt;: that account's game holds this content (31: v3_1_0, 32: v3_2_0's panels) - what
    /// a click on one of its buttons proves; for the client harness, which cannot click. 0 forgets it.</summary>
    public string SetContent(long account, int level)
    {
        if (level <= 0) content.Remove(account); else content[account] = level;
        Server.NextFrame(Frame);
        return $"account {account}: content level {level}";
    }

    /// <summary>css_gghud click: what a click on that button would send for that account, sent - without a client, to try the path
    /// from the button to the bridge. Returns what happened, for the console.</summary>
    /// <summary>css_gghud press &lt;account&gt; &lt;button&gt;: that player's click on a button, through the same path as a real one - a tab,
    /// a filter, a Fun choice, a card's Play - for the client harness, whose CS2 never has focus. Like a real click it proves the content
    /// the button belongs to, so press only buttons that player's game has.</summary>
    public string Press(long account, string buttonId)
    {
        if (entity is not { IsValid: true } e) return "no HUD entity";
        if (Utilities.GetPlayers().FirstOrDefault(x => x is { IsValid: true, IsBot: false } && Account(x) == account) is not { } p) return $"no player [U:1:{account}] on the server";
        OnClicked(p, e, buttonId);
        return $"pressed {buttonId} for {p.PlayerName}";
    }

    public string SimulateClick(long account, string buttonId)
    {
        var command = HudView.Command(buttonId, state, account);
        if (command == null) return $"button {buttonId} sends nothing for account {account} (state {(state == null ? "unknown" : "known")})";
        if (bridge == null) return "no bridge link";
        bridge.Act(account, "HUDtest", command);
        return $"sent {command} for account {account}; the bridge's answer arrives with the next frame";
    }

    // ---- the frame: four times a second ----

    void Frame()
    {
        var now = DateTime.UtcNow;
        budget = FrameBudget;
        var humans = Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false }).ToList();
        if (bridge?.Take(now, humans.Count > 0 || AlwaysPoll) is { } text && text != lastStateText && HudState.Decode(text) is { } fresh)
        {
            lastStateText = text;
            state = fresh;
            if (fresh.Site != "") Hud.Site = fresh.Site; // the bridge's SITE_URL: configuration, never code
            vote = VoteState.Decode(text);
            stateAt = now;
            changeAt = fresh.ChangeIn >= 0 ? now.AddSeconds(fresh.ChangeIn) : DateTime.MaxValue;
        }
        if (bridge?.TakeCatalog(now, state?.CatalogVersion ?? 0) is { } listText && HudCatalog.Decode(listText) is { } list) catalog = list;
        if (bridge?.TakeSearch(now, state?.SearchVersion ?? 0) is { } searchText && HudSearch.Decode(searchText) is { } index)
        {
            search = index;
            log.LogInformation("GunGameWelcome HUD: search index {Version} - {Count} maps", index.Version, index.Count);
        }
        UpdateCheck(now);
        EchoNotes(now);
        if (!Live) return;
        RunCheck(now);
        foreach (var (slot, at) in resendAt.ToList())
            if (now >= at) { resendAt.Remove(slot); Forget(slot); }

        tabMode = tabOverride ?? HudTab.Parse(state?.TabMode);
        if (tabMode == HudTab.Mode.Off)
            foreach (var slot in tab.CursorSlots().Concat(humans.Where(h => tab.Window(h.Slot)).Select(h => h.Slot)).Distinct().ToList())
            {
                if (tab.Window(slot)) slots.Info(slot, "off", now);
                tab.Release(slot);
                log.LogInformation("GunGameWelcome HUD TAB: switched off - the TAB cursor or window of slot {Slot} released", slot);
            }
        foreach (var (id, text2) in HudView.StaticTexts()) Text(id, id == "gg_b_cmds" ? HudTab.BoardLine(tabMode) : text2);
        foreach (var (id, cls, on) in HudView.StaticClasses()) Class(id, cls, on);
        var (funTexts, funClasses) = HudMenu.FunPage(FunValue, mode.ArmsRace); // Over Strike 2's content (NewIds32): the server's settings, the same for everybody whose game has the page
        foreach (var (id, text2) in funTexts) Text(id, text2);
        foreach (var (id, cls, on) in funClasses) Class(id, cls, on);
        var shown = state ?? new HudState();
        var ownNews = now < localNews.Until; // the watcher's or the grenade switch's line wins while it lasts
        foreach (var (id, text2) in HudView.Texts(shown)) if (!(ownNews && id == "gg_news_text")) Text(id, text2);
        foreach (var (id, cls, on) in HudView.Classes(shown)) if (!(ownNews && cls == "news")) Class(id, cls, on);
        if (ownNews) { Text("gg_news_text", localNews.Text); Class(HudView.Global, "news", true); }
        WorkshopCards(humans, now);
        if (changeAt != DateTime.MaxValue) Text("gg_k_secs", HudView.Countdown((int)Math.Ceiling((changeAt - now).TotalSeconds)));

        foreach (var h in humans) if (tab.Window(h.Slot)) slots.Hold(h.Slot, now); // the TAB window: no idle close while TAB is held
        // The end-of-match vote (#26): everybody's window opens on it once a round, and stays open while it runs; who closes it can open it
        // again with TAB + E. The Maps page draws it while it is on.
        if (vote.Round > 0)
            foreach (var h in humans)
            {
                if (voteShown.GetValueOrDefault(h.Slot) != vote.Round) { voteShown[h.Slot] = vote.Round; slots.Show(h.Slot, HudSlots.Page.Maps, now); }
                if (slots.WindowOpen(h.Slot) && vote.Open) slots.Hold(h.Slot, now);
            }
        foreach (var (id, cls, on) in HudVote.VersionClasses(TestLevel ?? HudVote.ContentLevel)) Class(id, cls, on);
        foreach (var (id, cls, on) in HudVote.ModeClasses(mode.Id)) Class(id, cls, on);
        Var(HudView.Global, HudVote.VersionVar, Hud.Content);
        Var(HudView.Global, HudVote.ModeVar, mode.Name);
        var views = slots.Frame(now).ToDictionary(v => v.Slot, v => v.View);
        foreach (var p in humans)
        {
            var view = views.GetValueOrDefault(p.Slot);
            var account = Account(p);
            // Being checked: the gold button says "Click to play" and is shown even while a map change hides its usual action.
            var checking = checkMode != HudCheck.Mode.Off && gate.Checking(p.Slot);
            var ownNote = localNotes.TryGetValue(p.Slot, out var note) && now < note.Until;
            foreach (var (id, text2) in HudView.PlayerTexts(shown, account, p.PlayerName, view.Toast == 2))
                Text(id, checking && id == "gg_btn_act_text" ? HudCheck.ActText : checking && id == "gg_btn_act_sub" ? HudCheck.ActSub : ownNote && id == "gg_note_text" ? note.Text : text2, p);
            foreach (var (id, cls, on) in HudView.PlayerClasses(shown, account)) Class(id, cls, (on || (ownNote && cls == "note")) && !(checking && cls == "act-off"), p);
            Class(HudView.Screen, "win", view.Window, p);
            Class(HudView.Screen, "win-in", view.WindowIn, p);
            Class(HudView.Screen, "draining", view.Draining, p);
            Class(HudView.Screen, "toast-1", view.Toast == 1, p);
            Class(HudView.Screen, "toast-2", view.Toast == 2, p);
            Class(HudView.Screen, "toast-in", view.ToastIn, p);
            var joined = joinedAt.TryGetValue(p.Slot, out var at) && (now - at).TotalSeconds < HudVote.JoinSeconds;
            Class(HudView.Screen, "vjoin", joined, p);                     // the version banner, in the games that have one
            Class(HudView.Screen, "vold", joined && ContentOf(p) < 32, p); // the older HUD's note, which a newer game hides
            if (vote.Round <= 0) Class(HudView.Screen, "voting", false, p);
            foreach (var page in Enum.GetValues<HudSlots.Page>()) Class(HudView.Screen, "page-" + page.ToString().ToLowerInvariant(), view.Window && view.Page == page, p);
            // Contents v3_1_0 and v3_2_0: what their new panels would get from everybody's state goes to this player alone, once their game
            // has shown it has them; c31 and c32 (on gg_screen, which v3_0_0 has too) uncover those panels.
            var level = ContentOf(p);
            Class(HudView.Screen, "c31", level >= 31, p);
            Class(HudView.Screen, "c32", level >= 32, p);
            if (level >= 31)
            {
                foreach (var (id, text2) in newTexts) if (HudView.LevelOf(id) <= level) Text(id, text2, p);
                foreach (var ((id, cls), on) in newClasses) if (HudView.LevelOf(id) <= level) Class(id, cls, on, p);
            }
            if (view.Window) Menu(p, view.Page, shown, account, now);
            var capture = view.Capture || tab.Cursor(p.Slot); // the window's cursor, or the board's while TAB is held
            if (captured.GetValueOrDefault(p.Slot) != capture)
            {
                entity!.SetInputCaptureEnabled(p, capture);
                captured[p.Slot] = capture;
                playerDirty = true;
            }
        }
        MarkChanged();
    }

    /// <summary>The menu page a player has open, for that player only - and only while it is open: the map browser's eight cards, the
    /// queue with their own requests' cancel buttons, or the game settings. Everything else on the window is the same for all.</summary>
    void Menu(CCSPlayerController p, HudSlots.Page page, HudState shown, long account, DateTime now)
    {
        var (texts, classes) = page switch
        {
            HudSlots.Page.Maps when vote.Round > 0 => HudVote.Cards(vote, account, catalog),
            HudSlots.Page.Maps => HudMenu.Maps(catalog, shown, Browser(p.Slot), account, now, search),
            HudSlots.Page.Queue => HudMenu.Queue(shown, account),
            HudSlots.Page.Game => HudMenu.Game(shown, restartArmed.TryGetValue(p.Slot, out var until) && now < until ? (int)Math.Ceiling((until - now).TotalSeconds) : 0),
            _ => (null, null),
        };
        if (texts == null || classes == null) return;
        foreach (var (id, text) in texts) Text(id, text, p);
        foreach (var (id, cls, on) in classes) Class(id, cls, on, p);
        if (page != HudSlots.Page.Maps) return;
        if (vote.Round > 0) { Pictures(p, HudVote.Thumbs(vote, catalog)); return; }
        var card = cards.TryGetValue(p.Slot, out var c) ? c : cards[p.Slot] = new WorkshopCard();
        card.Show(Browser(p.Slot).WorkshopQuery(catalog), now); // a typed Workshop id that no listed map has: its card, asked about once it holds still
        foreach (var (id, text) in card.Texts()) Text(id, text, p);
        foreach (var (id, cls, on) in card.Classes()) Class(id, cls, on, p);
        // A picture is two classes (HudThumbs): what the card's previous picture had and this one does not goes off first, and only then
        // does the new pair come on - a card never holds parts of two pictures at once, which could show the wrong map's.
        Pictures(p, HudMenu.Thumbs(catalog, Browser(p.Slot), account, search));
    }

    /// <summary>The cards' pictures for this player (two classes a picture, HudThumbs).</summary>
    void Pictures(CCSPlayerController p, List<(string Panel, string[] Classes)> wanted)
    {
        foreach (var (panel, want) in wanted)
        {
            var key = (p.Slot, panel);
            var on = thumbs.TryGetValue(key, out var had) ? had : thumbs[key] = new HashSet<string>();
            var cleared = true;
            foreach (var cls in on.Where(c => !want.Contains(c)).ToList())
            {
                if (!Class(panel, cls, false, p)) { cleared = false; break; } // out of budget: the rest next frame
                on.Remove(cls);
            }
            if (!cleared) continue;
            foreach (var cls in want)
                if (Class(panel, cls, true, p)) on.Add(cls); else break;
        }
    }

    /// <summary>CounterStrikeSharp's custom-HUD natives write the entity's networked vectors in place and notify nobody; CS2UIKit shows
    /// that clients see the changes anyway, but CS2KZ, which drives the same entity from C++, marks every change for the network
    /// explicitly. So after a frame that changed something, this marks the fields it touched as changed, the same way CounterStrikeSharp
    /// marks any entity field. Harmless when the engine would have noticed by itself; a first failure switches it off and is logged.</summary>
    void MarkChanged()
    {
        if ((!globalDirty && !playerDirty) || markBroken || !Live) { globalDirty = playerDirty = false; return; }
        try
        {
            foreach (var field in new[] { "m_vecPanelIds", "m_vecClassNames", "m_vecDialogVariableNames" })
                Utilities.SetStateChanged(entity!, "CCSCustomHudLayout", field);
            if (globalDirty) Utilities.SetStateChanged(entity!, "CCSCustomHudLayout", "m_globalLayoutState");
            if (playerDirty) Utilities.SetStateChanged(entity!, "CCSCustomHudLayout", "m_vecPlayerLayoutStates");
        }
        catch (Exception e)
        {
            markBroken = true;
            log.LogWarning("GunGameWelcome HUD: marking the layout for the network failed, left to the engine from now on: {Message}", e.Message);
        }
        globalDirty = playerDirty = false;
    }

    /// <summary>One engine call's worth of this frame's budget: false once it is spent - the caller then skips the call without
    /// remembering it as sent, so the next frame sends it. A page change for a full server stays inside a few frames instead of one long one.</summary>
    bool Spend() => budget-- > 0;

    /// <summary>The newest content this player's game has shown it holds (31: v3_1_0, 32: v3_2_0, 0: neither yet): it may be sent the panels
    /// that content and the ones before it added.</summary>
    int ContentOf(CCSPlayerController player) => content.GetValueOrDefault(Account(player));

    /// <summary>Where the fun page's values come from: the settings module (convars and its effects); without it, the engine's convars.</summary>
    public Func<string, string?>? FunSource { get; set; }

    /// <summary>The grenade drops as the grenades module has them.</summary>
    public Func<bool>? NadesOn { get; set; }

    /// <summary>A fun setting's convar (or effect) as the server has it now (Fun.cs), for the page and for checking a change landed.</summary>
    string? FunValue(string name)
    {
        if (FunSource != null) return FunSource(name);
        if (!funVars.TryGetValue(name, out var cv))
        {
            try { cv = ConVar.Find(name); } catch (Exception) { cv = null; }
            funVars[name] = cv;
        }
        try { return cv?.StringValue; } catch (Exception) { return null; }
    }

    readonly Dictionary<(string, string), string> globalVars = new();

    /// <summary>A named dialog variable on a panel every content has (gg_global): a newer layout's label inherits it ({s:osver}).</summary>
    void Var(string id, string name, string text)
    {
        if (globalVars.TryGetValue((id, name), out var had) && had == text) return;
        if (!Spend()) return;
        entity!.SetDialogVariableString(id, name, Hud.Plain(text));
        globalVars[(id, name)] = text;
        globalDirty = true;
    }

    void Text(string id, string text, CCSPlayerController? player = null)
    {
        if (HudView.LevelOf(id) is > 0 and var level) // a newer content's panel: never to everybody, only to the games that have it (Frame sends newTexts per player)
        {
            if (player == null) { newTexts[id] = text; return; }
            if (ContentOf(player) < level) return;
        }
        text = Hud.Plain(text);
        if (player == null)
        {
            if (globalTexts.TryGetValue(id, out var had) && had == text) return;
            if (!Spend()) return;
            entity!.SetDialogVariableString(id, "text", text);
            globalTexts[id] = text;
            globalDirty = true;
            return;
        }
        var mine = playerTexts.TryGetValue(player.Slot, out var t) ? t : playerTexts[player.Slot] = new();
        if (mine.TryGetValue(id, out var old) && old == text) return;
        if (!Spend()) return;
        entity!.SetDialogVariableStringForPlayer(player, id, "text", text);
        mine[id] = text;
        playerDirty = true;
    }

    /// <summary>Sets a class on a panel (for everybody, or for one player) unless it is so already. False when this frame's budget
    /// ran out before it could be sent.</summary>
    bool Class(string id, string cls, bool on, CCSPlayerController? player = null)
    {
        if (HudView.LevelOf(id) is > 0 and var level) // a newer content's panel, as in Text
        {
            if (player == null) { newClasses[(id, cls)] = on; return true; }
            if (ContentOf(player) < level) return true;
        }
        if (player == null)
        {
            if (globalClasses.TryGetValue((id, cls), out var had) && had == on) return true;
            if (!Spend()) return false;
            entity!.SetHasClass(id, cls, on);
            globalClasses[(id, cls)] = on;
            globalDirty = true;
            return true;
        }
        var mine = playerClasses.TryGetValue(player.Slot, out var c) ? c : playerClasses[player.Slot] = new();
        if (mine.TryGetValue((id, cls), out var old) && old == on) return true;
        if (!Spend()) return false;
        entity!.SetHasClassForPlayer(player, id, cls, on);
        mine[(id, cls)] = on;
        playerDirty = true;
        return true;
    }
}
