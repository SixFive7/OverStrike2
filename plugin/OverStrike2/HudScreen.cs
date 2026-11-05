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

/// <summary>The HUD on screen: one custom_hud_layout entity per map, driven from ../../bridge/Shared/Hud.cs. What to show is decided there (HudView for
/// the words, HudSlots for who has the window or a toast); this class owns the entity and sends the engine only what changed.
///
/// A server without the HUD does not exist: the settings require the addon, and os2-server refuses to start without it. A Steam hold (RequireAddon) keeps playing: who is on
/// keeps the HUD on the map that is on, with the banner; at the next map change they lose it too, and new players join without it.
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
sealed partial class HudScreen
{
    const float FrameSeconds = 0.25f;

    readonly BasePlugin plugin;
    readonly ILogger log;
    readonly BridgeLink? bridge;
    readonly HudSlots slots = new();
    readonly string addon;                            // the Workshop item the server hands out (hud.addon): its link is in the check's messages
    readonly HudGate gate = new();                    // the HUD check on this map (HudCheck.cs)
    readonly Dictionary<long, string> hudSeen = new(); // account -> the HUD its game showed at the check's click since its last real join (HudCheck.IsVersionWord); the bridge keeps the same for the players on the server
    readonly ItemWatch item;                          // whether Steam shows the item to everybody yet: nobody is disconnected until it does
    HudCheck.Mode checkMode;                          // the check in force as of the last frame
    HudCheck.Mode checkHigh;                          // the strictest it has been on this map: only a stricter one re-arms the waiting players
    readonly HudTab tab = new();                      // TAB (HudTab.cs): the board's cursor while it is held
    HudTab.Mode tabMode;                              // down or off, as of the last frame (HUD_TAB, or css_gghud tab)
    HudTab.Mode? tabOverride;                         // css_gghud tab off|down: until the plugin reloads
    CCSCustomHudLayout? entity;
    bool worldReady;

    HudState? stateRaw, state;                        // the bridge's newest state as it came (end times), and as of this frame (its countdowns worked out); null until the first answer
    VoteState voteRaw = new(), vote = new();          // the vote and the modes, off the same text (MapVote.cs): as it came, and as of this frame
    readonly Dictionary<int, long> voteShown = new(); // slot -> the vote round whose window was opened for it (once a round; negative while mid-match)
    readonly Dictionary<int, (long Round, int Page, bool Browse)> voteView = new(); // slot -> that player's page of the ballot, and whether the map browser (MAPS, Browse) is open on the vote
    readonly Dictionary<int, (long Round, int Page, int[] Now, int[] Before, DateTime ChangedAt)> drawnOrder = new(); // slot -> the ballot's cards as last drawn for that player, and before the last re-sort (HudVote.Click)
    Modes.Mode mode = Modes.ArmsRace;                 // the mode the map that is on was loaded in (the settings module, os_mode)
    readonly Dictionary<int, DateTime> joinedAt = new(); // slot -> a real join: an older content's version banner for a while, and the Players page's time on
    HudCatalog? catalog;                              // the menu's map list, fetched again whenever the state's version moves
    HudSearch search = new();                         // the search index (names, tags, descriptions), fetched again when its version moves
    readonly WorkshopPlay plays;                      // unlisted Workshop ids: the Web API and the downloads (WorkshopPlay.cs)
    readonly Dictionary<int, WorkshopCard> cards = new(); // slot -> that player's card for an unlisted Workshop id
    (string Text, DateTime Until) localNews = ("", default);  // the older contents' news line from here, not the bridge: the restart watcher, the block's notices, the item check
    readonly Dictionary<int, (string Text, DateTime Until, string Cat)> localNotes = new(); // slot -> a note from here: no line to the bridge
    readonly Dictionary<int, HudBrowser> browsers = new();   // slot -> that player's map browser (sort, filters, page)
    readonly Dictionary<int, DateTime> restartArmed = new(); // slot -> the restart button's second click counts until then
    readonly Dictionary<(int Slot, string Panel), HashSet<string>> thumbs = new(); // the picture classes a card has on for a player
    HudDash? dash;                                           // the Sync page's block: every word of it the bridge's, fetched when its version moves
    readonly Dictionary<int, Dictionary<string, int>> dashPages = new(); // slot -> that player's page of each of its lists
    int budget;                                       // engine calls this frame may still make; the rest go out in the next one
    const int FrameBudget = 600;
    DateTime stateAt;
    string lastStateText = "";

    // What the entity holds already, so a frame only sends differences. Cleared whenever the entity is new.
    readonly Dictionary<string, string> globalTexts = new();
    readonly Dictionary<(string, string), bool> globalClasses = new();
    readonly Dictionary<int, Dictionary<string, string>> playerTexts = new();
    readonly Dictionary<int, Dictionary<(string, string), bool>> playerClasses = new();
    readonly Dictionary<int, bool> captured = new();
    readonly Dictionary<int, DateTime> resendAt = new(); // slot -> forget what it has (and send it again) at this time
    bool globalDirty, playerDirty, markBroken;           // what this frame changed, for MarkChanged

    // Every content on the layout path (Hud.Content) is sent the same: what this content has. An older content simply lacks what it lacks - a panel
    // a game does not have takes nothing in it - and keeps what it needs for its own pages (HudView.StaticClasses' c31 and c32, the svge/svlt
    // version classes and vjoin).
    int latestSent;                                      // the level of the latest-N class on gg_global (HudVote.LatestClass): turned off when it moves
    bool addonRequired;                                  // what the install step set at start: mm_extra_addons <addon> - a server-side addon, which survives every map change - so every joiner downloads it
    HudCheck.Cause droppedCause;                         // why it was last dropped, for the words players get
    readonly HashSet<long> toldNoMenu = new();           // accounts told in chat why they have no menu, since it was dropped
    readonly HashSet<long> keptMenu = new();             // accounts on the server when it was dropped: they keep the HUD until the map changes

    public HudScreen(BasePlugin plugin, ILogger log, bool hotReload, PluginSetup setup)
    {
        this.plugin = plugin;
        this.log = log;
        addon = setup.Addon;
        addonRequired = addon != "";
        item = new ItemWatch(addon, setup.ItemPage, setup.ItemPageSwitch);
        plays = new WorkshopPlay(log);
        bridge = setup.Bridge is { } root ? BridgeLink.At(root, setup.BridgeSecret) : null;
        if (addon == "") log.LogError("Over Strike 2 HUD: no Workshop item for the HUD: a server without the HUD cannot exist - hud.addon must be set");
        log.LogInformation("Over Strike 2 HUD: {Layout}, live state {Source}", $"layout {Hud.Layout} from Workshop item {addon}",
            bridge != null ? $"from the bridge at {setup.BridgeWhy}" : $"OFF - {setup.BridgeWhy}");
        plugin.RegisterListener<Listeners.OnCustomHudClicked>(OnClicked);
        plugin.RegisterListener<Listeners.OnMapStart>(_ => { slots.MapStarted(); resendAt.Clear(); gate.MapStarted(); tab.MapStarted(); checkHigh = HudCheck.Mode.Off; keptMenu.Clear(); });
        plugin.RegisterListener<Listeners.OnPlayerButtonsChanged>(OnButtons);
        plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath);
        plugin.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        plugin.RegisterListener<Listeners.OnClientPutInServer>(OnPutInServer);
        plugin.RegisterEventHandler<EventRoundStart>((_, _) => { worldReady = true; EnsureEntity(); return HookResult.Continue; });
        plugin.RegisterEventHandler<EventPlayerHurt>(OnHurt);
        plugin.AddTimer(FrameSeconds, Frame, TimerFlags.REPEAT);
        MatchLoad();
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
                     typeof(HudSearch), typeof(HudKeyboard), typeof(WorkshopCard), typeof(WorkshopItem), typeof(WorkshopPlay), typeof(Fun), typeof(FunClicks), typeof(ChatCommands), typeof(DownReasons), typeof(HudVote), typeof(Messages), typeof(M), typeof(Sections),
                     typeof(HudDash), typeof(VoteFreeze), typeof(FreezePlan) })
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.IsAbstract || method.ContainsGenericParameters) continue;
                try { RuntimeHelpers.PrepareMethod(method.MethodHandle); count++; } catch (Exception) { } // a method the JIT cannot prepare ahead is compiled on first use, as before
            }
        var pictures = HudThumbs.Count; // builds the pictures' lookup now too, not in the first maps page
        log.LogInformation("Over Strike 2 HUD: {Count} methods compiled ahead in {Ms} ms, {Pictures} map pictures numbered", count, clock.ElapsedMilliseconds, pictures);
    }

    /// <summary>As of when the state is the bridge's word: when the newest answer taken was asked for (MinValue: never). The freeze goes by it
    /// (VoteFreeze.cs): an answer asked for before this map started is the old map's.</summary>
    public DateTime AnsweredAt => bridge?.StateAsOf ?? DateTime.MinValue;

    /// <summary>The bridge's clock now, unix milliseconds: the freeze measures a vote's end against it.</summary>
    public long BridgeNowMs => bridge?.BridgeNowMs(DateTime.UtcNow) ?? HudState.Ms(DateTime.UtcNow);

    public void Unload()
    {
        foreach (var slot in captured.Where(c => c.Value).Select(c => c.Key).ToList())
            if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true } p && Live) entity!.SetInputCaptureEnabled(p, false);
        if (Live) entity!.Remove();
        entity = null;
    }

    bool Live => entity is { IsValid: true };

    // ---- what the plugin and the bridge ask for ----

    /// <summary>css_guide: a real join: the welcome window, until its X; its tagline in that player's chat.</summary>
    public void Joined(CCSPlayerController player, bool first)
    {
        slots.Joined(player.Slot, first, player.PawnIsAlive, DateTime.UtcNow);
        joinedAt[player.Slot] = DateTime.UtcNow;
        hudSeen.Remove(Account(player)); // a real join: the HUD check asks again - a reconnecting player may have a new HUD
        Post(Messages.Tagline, Hud.Headline, Account(player));
        if (player.PawnIsAlive) { worldReady = true; Server.NextFrame(EnsureEntity); }
    }

    /// <summary>The menu, on its map browser (the board's Open full menu, TAB + E); with words, the browser searching for them from its
    /// first page (a Workshop link pasted in chat).</summary>
    public void Menu(CCSPlayerController player, string? find = null)
    {
        worldReady = true;
        if (find != null) Browser(player.Slot).Find(find);
        if (find != null && vote is { Open: true, Round: > 0 }) { voteView[player.Slot] = (vote.Round, VoteView(player.Slot).Page, true); Busy(player, Account(player)); } // a search during the vote: the map browser that adds to it
        slots.Show(player.Slot, HudSlots.Page.Maps, DateTime.UtcNow);
        Server.NextFrame(() => { EnsureEntity(); Frame(); }); // not inside the player's own chat command (see the class summary)
    }

    HudBrowser Browser(int slot) => browsers.TryGetValue(slot, out var b) ? b : browsers[slot] = new HudBrowser();

    /// <summary>A Workshop link or id pasted in chat: the menu on Maps, searching for it - its card if it is not on the list.</summary>
    public void OpenWorkshop(CCSPlayerController player, long id) => Menu(player, id.ToString());

    /// <summary>The answer to this player's own click from the plugin itself: the line in their window, their chat and their board (Messages.Answer). text may carry marks (M).</summary>
    void LocalNote(int slot, string text, double seconds = 8)
    {
        if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: false } p) Post(Messages.Answer, text, Account(p), seconds: seconds);
    }

    public void Spawned(CCSPlayerController player)
    {
        if (player.IsBot) return;
        worldReady = true;
        Server.NextFrame(EnsureEntity); // not inside the player's spawn: the first one after a start would be disconnected (class summary)
        slots.Spawned(player.Slot, DateTime.UtcNow);
        Forget(player.Slot);
        captured.Remove(player.Slot); // the window's mouse is sent again: a death or a respawn never costs an open window its clicks
        resendAt[player.Slot] = DateTime.UtcNow.AddSeconds(2);
        // The HUD check: the first spawn on a map of a player not seen clicking since their last real join opens the window, "Click to play" on
        // its gold button, and starts their clock (HudGate). Cheap bookkeeping only; the chat line goes out a frame later.
        var account = Account(player);
        if (gate.Spawned(player.Slot, account, Verified(account), DateTime.UtcNow) && checkMode != HudCheck.Mode.Off) Prompt(player.Slot, DateTime.UtcNow);
        TellNoMenu(player);
    }

    /// <summary>Seen clicking since the player's last real join: here, or by the bridge (which keeps it through a plugin reload).</summary>
    bool Verified(long account) => hudSeen.ContainsKey(account) || HudCheck.IsVerified(state, account);

    /// <summary>The HUD a player's game showed at the check's click (HudCheck.IsVersionWord), "" while not seen: here, else the bridge's word.</summary>
    string VersionOf(long account) => hudSeen.TryGetValue(account, out var v) && v != Hud.Release ? v
        : state?.Verified.TryGetValue(account, out var b) == true && b != Hud.Release ? b : hudSeen.ContainsKey(account) || state?.Verified.ContainsKey(account) == true ? Hud.Release : "";

    /// <summary>The check's window for a slot: the welcome window, until its X.</summary>
    void Prompt(int slot, DateTime now) => slots.Check(slot, now);

    /// <summary>The server does not yet know which HUD this player's game shows: the window's X is the version button.</summary>
    bool WantsVersion(long account) => VersionOf(account) is var v && (v == "" || v == Hud.Release);

    /// <summary>Shot while TAB's board has the cursor: give it back (shot means the hands are needed). The window never closes for damage.</summary>
    HookResult OnHurt(EventPlayerHurt ev, GameEventInfo info)
    {
        if (ev.Userid is { IsValid: true, IsBot: false } p && tab.Release(p.Slot)) Server.NextFrame(Frame);
        return HookResult.Continue;
    }

    HookResult OnDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        if (ev.Userid is { IsValid: true, IsBot: false } p && tab.Release(p.Slot)) Server.NextFrame(Frame);
        return HookResult.Continue;
    }

    /// <summary>TAB (HudTab.cs: down, or off). CounterStrikeSharp calls this inside the server's think for every button any player presses or
    /// lets go, so it only does HudTab's bookkeeping; opening the window or switching the cursor waits for the next frame.</summary>
    void OnButtons(CCSPlayerController player, PlayerButtons pressed, PlayerButtons released)
    {
        if (player is not { IsValid: true, IsBot: false }) return;
        var slot = player.Slot;
        if (tabMode == HudTab.Mode.Off && !tab.Cursor(slot)) return;
        var act = tab.Buttons(slot, (ulong)pressed, (ulong)released, tabMode, DateTime.UtcNow);
        if (act == null) return;
        Server.NextFrame(() =>
        {
            if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true, IsBot: false } p) return;
            switch (act)
            {
                case HudTab.Act.OpenMenu: Menu(p); return; // TAB + E: the window takes the cursor over
                case HudTab.Act.CursorOn when vote is { Open: true, Round: > 0 } && !slots.WindowOpen(slot): tab.Release(slot); Menu(p); return; // TAB brings a vote back
                default: Frame(); return;
            }
        });
    }

    /// <summary>css_gghud tab off|down: TAB's mode until the plugin reloads (null: back to HUD_TAB from the bridge).</summary>
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
        return $"released: {windows} window(s), {cursors.Count} TAB cursor(s); capture off for everybody next frame";
    }

    /// <summary>css_gghud level &lt;n&gt;: the server's content level as the version classes show it - latest-N, which from v4.6.0 on lights the
    /// outdated banner of every content below N, and the svge/svlt of the older contents' join banner (the client harness); null is the real one.</summary>
    public int? TestLevel { get; set; }

    /// <summary>css_gghud level: a real join again for that account's slot, so an older content's join banner shows for a while (the client harness).</summary>
    public string Rejoin(long account)
    {
        if (Utilities.GetPlayers().FirstOrDefault(x => x is { IsValid: true, IsBot: false } && Account(x) == account) is not { } p) return $"no player [U:1:{account}]";
        joinedAt[p.Slot] = DateTime.UtcNow;
        Server.NextFrame(Frame);
        return $"join banner for {p.PlayerName} for {HudVote.JoinSeconds:0} s (server level {TestLevel ?? HudVote.ContentLevel})";
    }

    /// <summary>The settings module's mode went on (os_mode, a map start): the HUD's classes follow it.</summary>
    public void SetMode(Modes.Mode m) { mode = m; Server.NextFrame(Frame); }

    public void Gone(int slot)
    {
        // The HUD this player's game showed is forgotten here too, not only at css_guide's real join (which bridge.guideOnJoin off never sends): a real leave
        // forgets it, and through a map change's reconnect the bridge keeps its own (the live state's ok lines), which Verified and VersionOf read.
        if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: false } leaving) hudSeen.Remove(Account(leaving));
        slots.Gone(slot);
        voteShown.Remove(slot);
        voteView.Remove(slot);
        drawnOrder.Remove(slot);
        gate.Gone(slot);
        tab.Gone(slot);
        browsers.Remove(slot);
        cards.Remove(slot);
        localNotes.Remove(slot);
        funClicks.Gone(slot);
        restartArmed.Remove(slot);
        rematchArmed.Remove(slot);
        cuCards.Remove(slot);
        dashPages.Remove(slot);
        Forget(slot);
        captured.Remove(slot);
        resendAt.Remove(slot);
    }

    public string Status() =>
        $"entity {(Live ? entity!.Index.ToString() : "none")}, world {(worldReady ? "ready" : "not ready")}, " +
        $"state {(state == null ? "none" : $"{(DateTime.UtcNow - stateAt).TotalSeconds:0}s old: {state.Map} by {state.By}")}, vote {(vote.Round > 0 ? $"round {vote.Round} {(vote.Open ? "open" : "decided")}{(vote.Freeze ? ", everybody frozen" : "")}" : "none")}, " +
        $"bridge {(bridge == null ? "off" : bridge.Failures == 0 ? "ok" : $"{bridge.Failures} failures ({bridge.LastError})")}, " +
        $"window open for [{string.Join(", ", Utilities.GetPlayers().Where(p => slots.WindowOpen(p.Slot)).Select(p => p.PlayerName))}], " +
        $"check {HudCheck.Name(checkMode)} (HUD_CHECK {(state == null ? "?" : HudCheck.Name(HudCheck.Parse(state.CheckMode)))}, item {item.Describe(DateTime.UtcNow)}), " +
        $"waiting for a click from [{string.Join(", ", Utilities.GetPlayers().Where(p => gate.Checking(p.Slot)).Select(p => p.PlayerName))}], " +
        $"TAB {HudTab.Name(tabMode)}{(tabOverride != null ? " (css_gghud tab)" : "")}, board cursor for [{string.Join(", ", Utilities.GetPlayers().Where(p => tab.Cursor(p.Slot)).Select(p => p.PlayerName))}], " +
        $"search index {search.Count} maps (version {search.Version}), sync page {(dash == null ? "no block yet" : $"block {dash.Version}: {dash.Words.Count} words, {string.Join(", ", dash.Lists.Select(l => $"{l.Name} {l.Rows.Count}"))}")}, workshop {plays.Describe()}, " +
        $"addon {(addonRequired ? "required of joiners" : "NOT required (Steam does not show it to everybody)")}, HUDs seen: [{string.Join(", ", Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false }).Select(p => $"{p.PlayerName} {(VersionOf(Account(p)) is { Length: > 0 } v ? v : "-")}"))}], fun {string.Join(", ", Fun.Settings.Select(s => $"{s.Id} {Fun.Showing(s, FunValue).Id}"))}";

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
            if (entity is not { IsValid: true }) { log.LogWarning("Over Strike 2 HUD: could not create custom_hud_layout"); entity = null; return; }
            entity.StrLayout = Hud.Layout;
            entity.DispatchSpawn();
            ForgetAll();
            log.LogInformation("Over Strike 2 HUD: layout entity {Index} spawned in {Ms} ms{Stale}", entity.Index, clock.ElapsedMilliseconds, stale > 0 ? $", {stale} leftover(s) removed" : "");
        }
        catch (Exception e)
        {
            entity = null;
            log.LogWarning("Over Strike 2 HUD: spawning the layout entity failed: {Message}", e.Message);
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
        browsers.Remove(slot);
        cards.Remove(slot);
        localNotes.Remove(slot);
        funClicks.Gone(slot);
        restartArmed.Remove(slot);
        rematchArmed.Remove(slot);
        cuCards.Remove(slot);
        dashPages.Remove(slot);
        drawnOrder.Remove(slot);
        Forget(slot);
        if (Live && Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: false } p)
        {
            foreach (var cls in new[] { "win", "win-in", "draining", "pinned", "toast-1", "toast-in", "why", "why-run", "cu", "cu-in", "cu-run", "wl", "wback" }.Concat(Enum.GetValues<HudSlots.Page>().Select(page => "page-" + page.ToString().ToLowerInvariant())))
                entity!.SetHasClassForPlayer(p, HudView.Screen, cls, false);
            entity!.SetInputCaptureEnabled(p, false);
            playerDirty = true;
        }
        captured[slot] = false;
    }

    /// <summary>Send this slot everything again. What its cards' pictures have on is kept, through a new player in the slot too: those
    /// classes may still be on in the client, and the next maps page switches off whatever it does not want (only a new entity starts
    /// clean).</summary>
    void Forget(int slot) { playerTexts.Remove(slot); playerClasses.Remove(slot); playerVars.Remove(slot); }

    void ForgetAll() { globalTexts.Clear(); globalClasses.Clear(); globalVars.Clear(); playerTexts.Clear(); playerClasses.Clear(); playerVars.Clear(); thumbs.Clear(); latestSent = 0; }

    // ---- clicks ----

    void OnClicked(CCSPlayerController player, CCSCustomHudLayout layout, string buttonId)
    {
        if (!Live || player is not { IsValid: true, IsBot: false } || layout is not { IsValid: true } || layout.Index != entity!.Index) return;
        var now = DateTime.UtcNow;
        // Any click at all proves this player's game shows the HUD (the check, HudCheck.cs). While they were being checked, the gold button said
        // "Click to play": from v4.6.0 on its id names the content (HudCheck.VersionButton), an older content's is the live column's action button,
        // which counts as an older HUD. That click is the check and closes the window - it must not roll a map as !next would.
        var who = Account(player);
        var wasChecked = gate.Checking(player.Slot);
        var proved = HudCheck.VersionOf(buttonId);
        var version = proved ?? (wasChecked && buttonId == HudView.ButtonAct ? HudCheck.Older : Hud.Release);
        if (!Verified(who) || (version != Hud.Release && VersionOf(who) != version)) Verify(player, who, wasChecked, version);
        if ((wasChecked && buttonId == HudView.ButtonAct) || proved != null) { slots.Click(player.Slot, HudView.ButtonClose, now); Server.NextFrame(Frame); return; }
        if (HudVote.WhyClick(buttonId) is { } reason)
        {
            // The popup's reason, under whichever cursor this player has: the window's or TAB's. The bridge knows which vote it is about.
            Act(player, who, $"!down {reason.Word}"); // the same words a typist uses
            if (slots.WindowOpen(player.Slot)) slots.Click(player.Slot, buttonId, now);
            Server.NextFrame(Frame);
            return;
        }
        if (buttonId == HudMenu.BoardOpen && !slots.WindowOpen(player.Slot)) { tab.Release(player.Slot); Menu(player); return; } // the board's button: the window keeps the cursor
        if (buttonId is HudView.BoardUp or HudView.BoardDown or HudView.BoardNext or HudView.BoardBig or HudView.BoardAds) // the board's own buttons, under the TAB cursor
        {
            var boardCommand = HudView.Command(buttonId, state, vote);
            if (boardCommand == HudView.OpenVote) { tab.Release(player.Slot); Menu(player); return; } // the vote is on: its window
            if (boardCommand != null) Act(player, who, boardCommand);
            Server.NextFrame(Frame);
            return;
        }
        if (!slots.WindowOpen(player.Slot)) return;
        if (buttonId == HudView.ButtonClose && VoteLocked) { LocalNote(player.Slot, ChatReply.VoteWindowStays); Server.NextFrame(Frame); return; } // the vote's window stays
        slots.Click(player.Slot, buttonId, now);
        if (HudDash.Click(dash, buttonId, DashPages(player.Slot))) { Server.NextFrame(Frame); return; } // the Sync page's pagers
        if (buttonId == HudView.TabMaps && vote.Round > 0) { voteView[player.Slot] = (vote.Round, VoteView(player.Slot).Page, false); Server.NextFrame(Frame); return; } // MAPS opens on its first page: the ballot while a vote is on
        var account = Account(player);
        var browser = Browser(player.Slot);
        if (buttonId == HudView.ButtonAct && HudView.Command(buttonId, state, vote) == HudView.OpenVote) { slots.Show(player.Slot, HudSlots.Page.Maps, now); Server.NextFrame(Frame); return; } // the vote is on: the Maps page shows it
        if (vote.Round > 0 && slots.PageOf(player.Slot) == HudSlots.Page.Maps)
        {
            var view = VoteView(player.Slot);
            if (buttonId == HudVote.BrowseButton && vote.Open) { voteView[player.Slot] = (vote.Round, view.Page, true); Busy(player, account); Server.NextFrame(Frame); return; } // the map browser adds to the vote
            if (buttonId == HudVote.BackButton) { voteView[player.Slot] = (vote.Round, view.Page, false); Busy(player, account); Server.NextFrame(Frame); return; }
            if (!(view.Browse && vote.Open))
            {
                // The vote is on the Maps page's cards (HudVote.cs), a page of ten: a card's Play is a vote - on the card this player's vote is on, it
                // takes it back - Random's button too, the pager pages (activity: it resets the inactivity timer). A card names the map this player saw on
                // it: the cards re-sort as votes come in, and a click made on the order before a re-sort arrives after it (SeenOrder).
                if (HudVote.PageClick(vote, buttonId, view.Page, BallotCards(player.Slot)) is { } page) { voteView[player.Slot] = (vote.Round, page, false); Busy(player, account); Server.NextFrame(Frame); return; }
                if (HudVote.Click(vote, buttonId, view.Page, account, SeenOrder(player.Slot, view.Page, now), BallotCards(player.Slot)) is { } cast)
                {
                    Act(player, account, cast); // chat and the board say it: the bridge's line of who voted and who has yet to
                }
                Server.NextFrame(Frame);
                return;
            }
            // The map browser while the vote is on: its own controls as ever; a card's Play puts the map on the vote, with this player's vote (the bridge's
            // !map during a vote; its line says so in chat), and goes back to the ballot's last page, where the map appears for everybody.
            var browsing = Browser(player.Slot);
            if (catalog != null && browsing.Click(buttonId, browsing.Matches(catalog, account, search).Count)) { Busy(player, account); Server.NextFrame(Frame); return; } // a page, a sort, a filter: activity
            if (buttonId.StartsWith("gg_m", StringComparison.Ordinal) && buttonId.EndsWith("_play", StringComparison.Ordinal)
                && HudMenu.Command(buttonId, catalog, state, browsing, account, now, search, mode.Id, vote) is { } add && add.StartsWith("!map ", StringComparison.Ordinal))
            {
                Act(player, account, add);
                if (browsing.ConfirmKey == "") voteView[player.Slot] = (vote.Round, int.MaxValue, false); // a risky map's first click only arms its confirmation: the second one adds it
                Server.NextFrame(Frame);
                return;
            }
        }
        if (catalog != null && browser.Click(buttonId, browser.Matches(catalog, account, search).Count)) { Server.NextFrame(Frame); return; }
        if (FunClicked(player, buttonId, now)) return; // the Fun page (HudScreenFun.cs)
        if (buttonId == WorkshopCard.Play) { Download(player); Server.NextFrame(Frame); return; }
        ArmGameButtons(player, buttonId, now);
        var command = HudView.Command(buttonId, state, vote) ?? HudMenu.Command(buttonId, catalog, state, browser, account, now, search, mode.Id, vote);
        if (command != null) Act(player, account, command);
        Server.NextFrame(Frame); // not inside the player's own click message (see the class summary)
    }

    /// <summary>Work in the map browser while a vote is open - a page, a sort, a filter, a search - resets the vote's inactivity timer (the bridge's
    /// !mvb). Scrolling and hovering never reach the server, so they cannot count.</summary>
    void Busy(CCSPlayerController player, long account) { if (vote is { Open: true, Round: > 0 }) Act(player, account, MapVote.Busy(vote.Round)); }

    /// <summary>Ask the bridge for what a click stands for; without a line to it, the player's note says so.</summary>
    void Act(CCSPlayerController player, long account, string command)
    {
        if (bridge == null) LocalNote(player.Slot, ChatReply.NoLine);
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
        plays.Tick(now); // the queue moves on whoever still looks at a card
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
                    else if (step == WorkshopCard.Step.Failed) card.Failed(why ?? "The download failed");
                    else if (step == WorkshopCard.Step.Downloading) { card.Progress(percent, busy); WorkshopLine(p, card, percent); }
                    break;
                case WorkshopCard.Step.Installed:
                    Act(p, Account(p), $"!map {card.Id}");
                    card.Requested();
                    log.LogInformation("Over Strike 2 Workshop: {Id} installed - asked for it as {Name}'s pick", card.Id, p.PlayerName);
                    break;
            }
        }
    }

    static long Account(CCSPlayerController p) => p.SteamID >= 76561197960265728UL ? (long)(p.SteamID - 76561197960265728UL) : -1;

    /// <summary>An account's first click since its last real join, or the one that says which HUD it is: verified here at once, and at the bridge
    /// (!hudok with the version, which only this real click sends - never css_gghud click, never chat). A known version is never forgotten for an
    /// unknown one (Hud.Release).</summary>
    void Verify(CCSPlayerController player, long account, bool wasChecked, string version)
    {
        if (account <= 0) return;
        if (version == Hud.Release && hudSeen.TryGetValue(account, out var known)) version = known;
        hudSeen[account] = version;
        gate.Verified(player.Slot);
        bridge?.Act(account, player.PlayerName, HudCheck.Command(version));
        log.LogInformation("Over Strike 2 HUD check: {Name} [U:1:{Account}] clicked in the HUD ({Version})", player.PlayerName, account, version == Hud.Release ? "which content not known" : version);
    }

    /// <summary>The check's mode now: what the bridge says (HUD_CHECK: off, warn, enforce), off without a fresh state, and never more than warn
    /// while Steam does not show the item to everybody - a player whose game cannot download it would be thrown out for nothing. A stricter mode
    /// gives everybody still waiting a fresh grace period and the window.</summary>
    void UpdateCheck(DateTime now)
    {
        var configured = HudCheck.Parse(state?.CheckMode);
        item.Poll(now);
        RequireAddon(now);
        var answered = bridge == null || bridge.LastAnswer == default ? double.MaxValue : (now - bridge.LastAnswer).TotalSeconds;
        var mode = HudCheck.ModeOf(state, answered);
        if (mode == HudCheck.Mode.Enforce && !item.Public(now)) mode = HudCheck.Mode.Warn;
        if (!addonRequired) mode = HudCheck.Mode.Off; // nobody is made to download it now, so nobody may be asked for it either
        if (mode == checkMode) return;
        log.LogInformation("Over Strike 2 HUD check: {From} -> {To} (HUD_CHECK {Configured}, item {Item})", HudCheck.Name(checkMode), HudCheck.Name(mode),
            HudCheck.Name(configured), item.Describe(now));
        if (mode > checkHigh) // once per step up on a map, never on a flap - and never for a player the bridge knows (a spawn before its first answer)
        {
            foreach (var slot in gate.Rearm(now))
                if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: false } w && !Verified(Account(w))) Prompt(slot, now);
            checkHigh = mode;
        }
        checkMode = mode;
    }

    /// <summary>While Steam does not show the HUD item to everybody (an update waiting for Valve's review can do that), the server must not
    /// require it - MultiAddonManager would hold every joiner at a download that cannot succeed. So a Web API verdict of gone, private or
    /// banned, or a page visit after the Web API showed a new version that finds Steam's not-shown page, takes the item off
    /// mm_extra_addons, and the visit that finds it shown puts it back (ItemPlan in HudCheck.cs: the page is visited only after a change, at most
    /// every 15 minutes for 3 hours - no idle polling). No fresh word from the Web API: as it was (at a start: as the install step set it).
    /// What that does, by MultiAddonManager's own code (v1.7.1): the list empty, new joiners come in without the HUD at once, no map reloads, and who is on keeps it for the map
    /// that is on, with the banner; the next map change no longer carries it, so their game unmounts it, and they play on without a menu until the hold ends. Nobody without
    /// a ballot may be frozen by a vote, so during a hold the plugin tells the bridge it draws nothing (HudActive: a match end rolls the dice) and the bridge refuses a vote.</summary>
    void RequireAddon(DateTime now)
    {
        var cause = item.Cause;
        var required = item.Required(addonRequired, now);
        if (!required && addonRequired)
        {
            Server.ExecuteCommand("mm_extra_addons \"\"");
            addonRequired = false;
            droppedCause = cause;
            toldNoMenu.Clear();
            keptMenu.Clear();
            foreach (var p in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false })) keptMenu.Add(Account(p));
            log.LogWarning("Over Strike 2 HUD: Steam does not show Workshop item {Addon} to everybody ({Cause}) - joiners no longer have to download it (mm_extra_addons emptied), the HUD check is off",
                addon, cause switch { HudCheck.Cause.Review => $"the version uploaded {item.Updated} under review", HudCheck.Cause.Unavailable => "the Web API says it is not downloadable", _ => "a page visit by hand found it hidden" });
            var (head, line) = HudCheck.Dropped(cause);
            PostBanner(Messages.Hold, "problem", head, line);
            Post(Messages.Hold, $"{head} - {line}");
        }
        else if (required && !addonRequired)
        {
            Server.ExecuteCommand($"mm_extra_addons {addon}");
            addonRequired = true;
            log.LogInformation("Over Strike 2 HUD: Steam shows Workshop item {Addon} to everybody again - joiners download it again (mm_extra_addons {Addon})", addon, addon);
            PostBanner(Messages.HoldBack, "done", HudCheck.BackHead, HudCheck.BackLine);
            Post(Messages.HoldBack, $"{HudCheck.BackHead} - {HudCheck.BackLine}");
            foreach (var account in toldNoMenu) Post(Messages.HoldNoMenu, HudCheck.BackChat, account); // they have no banner to read
            toldNoMenu.Clear();
            keptMenu.Clear();
        }
    }

    /// <summary>A spawn while joiners do not get the HUD: a player without it has no menu and no banner, so one private chat line - once per player until the HUD is back - says
    /// why and what to do (nothing: play on). Who was on when the hold began still has it on that map.</summary>
    void TellNoMenu(CCSPlayerController player)
    {
        if (addonRequired || keptMenu.Contains(Account(player)) || !toldNoMenu.Add(Account(player))) return;
        Post(Messages.HoldNoMenu, HudCheck.NoMenuChat(droppedCause, item.Updated), Account(player));
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
        _ = item.Probe(other).ContinueWith(t => log.LogInformation("Over Strike 2 HUD item probe: {Result}",
            t.IsCompletedSuccessfully ? t.Result : $"{other}: failed ({t.Exception?.GetBaseException().Message})"), TaskScheduler.Default);
        return $"probing {other}'s Workshop page and the Web API - the answer goes to the server log as 'Over Strike 2 HUD item probe:'";
    }

    /// <summary>What the check's clock says is due: the warning in chat, then the disconnect - with warn only a log line.</summary>
    void RunCheck(DateTime now)
    {
        foreach (var (slot, act, seconds) in gate.Frame(now, checkMode, Verified))
        {
            if (Utilities.GetPlayerFromSlot(slot) is not { IsValid: true, IsBot: false, IsHLTV: false } p) { gate.Gone(slot); continue; }
            var account = Account(p);
            switch (act)
            {
                case HudGate.Act.Warn:
                    Post(Messages.CheckWarning, HudCheck.Alert(seconds, checkMode != HudCheck.Mode.Enforce), account, chat: false);
                    foreach (var line in HudCheck.Warning(addon, seconds, checkMode != HudCheck.Mode.Enforce)) Say(line, account);
                    log.LogInformation("Over Strike 2 HUD check: warned {Name} [U:1:{Account}] ({Mode}, {Seconds} s left)", p.PlayerName, account, HudCheck.Name(checkMode), seconds);
                    break;
                case HudGate.Act.WouldKick:
                    log.LogInformation("Over Strike 2 HUD check (warn): would disconnect {Name} [U:1:{Account}] - no click from the HUD in {Grace} s", p.PlayerName, account, HudCheck.GraceSeconds);
                    break;
                case HudGate.Act.Kick:
                    log.LogWarning("Over Strike 2 HUD check: disconnecting {Name} [U:1:{Account}] - no click from the HUD in {Grace} s", p.PlayerName, account, HudCheck.GraceSeconds);
                    if (p.UserId is int userId) Server.ExecuteCommand($"kickid {userId} \"{HudCheck.Reason(addon)}\"");
                    else p.Disconnect(CounterStrikeSharp.API.ValveConstants.Protobuf.NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);
                    break;
            }
        }
    }

    // ---- the chat commands (ChatCommands.cs) ----

    /// <summary>The end-of-match vote is open: a bare number in chat is a vote (ChatCommands.Parse).</summary>
    public bool VoteOpen => vote is { Open: true, Round: > 0 };

    /// <summary>The open vote's highest number: a bare number above it stays plain chat.</summary>
    public int VoteHighest => vote.Highest;

    /// <summary>A chat command, a frame after the line: what the menu's click would do, and the answer in chat, in its functional colours (Messages.CommandReply; the words are
    /// ChatCommands' and ChatReply's) - the bridge's answer to that player has a chat copy of its own (Messages.Answer). An alias's reply first teaches the primary.</summary>
    public void Chat(CCSPlayerController player, ChatCommands.Parsed cmd)
    {
        var account = Account(player);
        void Say(string line) => Post(Messages.CommandReply, Hud.Clean(line, 400), account);
        void Bridge(string command) { Typed(account); Act(player, account, command); }
        if (ChatCommands.Teach(cmd) is { } taught) Say(taught);
        switch (cmd.Kind)
        {
            case ChatCommands.Kind.Bridge:
                var action = cmd.Arg == "next" ? HudView.Command(HudView.BoardNext, state, vote) : cmd.Arg; // the board's Start a vote, as the board reads for this player
                if (action == null && cmd.Arg == "next" && state is not { ChangeIn: >= 0 } && state is not { Loading: true }) action = "!next"; // the wait after a vote: the bridge answers with the time left
                if (action == null) { Say(ChatReply.ChangeUnderWay); return; }
                if (action == HudView.OpenVote) { Menu(player); Say(ChatReply.VoteOn); return; }
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
                else if (cmd.Typed == "") Say(ChatReply.MenuOn(cmd.Arg));
                return;
            case ChatCommands.Kind.Find:
                Menu(player, cmd.Arg);
                Say(ChatReply.Searching(cmd.Arg));
                return;
            case ChatCommands.Kind.Play:
                Play(player, account, cmd.Arg, Say, Bridge);
                return;
            case ChatCommands.Kind.Fun:
                FunTyped(player, cmd.Arg); // the same change as a click on the Fun page (HudScreenFun.cs)
                return;
            case ChatCommands.Kind.Vote:
                var k = int.Parse(cmd.Arg);
                if (!vote.Open || vote.Round <= 0) { Say(ChatReply.NoVote); return; }
                if (vote.Numbered(k) is not { } numbered) { foreach (var line in ChatCommands.VoteOnlyLines(vote.Highest)) Say(line); return; }
                Bridge(MapVote.Click(vote.Round, numbered.N));
                Say(ChatReply.YourVote(numbered.Label));
                return;
            case ChatCommands.Kind.VoteOnly:
                foreach (var line in ChatCommands.VoteOnlyLines(vote.Highest)) Say(line);
                return;
            case ChatCommands.Kind.FunAll:
                FunAll(player, cmd.Arg); // the Fun page's Random or Default
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
        if (WorkshopCard.IdOf(q) is { } wid && catalog?.Maps.All(m => m.Id != wid) == true && q.Length > 6) { OpenWorkshop(player, wid); say(ChatReply.CardOpen); return; }
        if (catalog == null) { say(ChatReply.ListLoading); return; }
        var exact = catalog.Maps.FirstOrDefault(m => m.Arg.Equals(q, StringComparison.OrdinalIgnoreCase) || m.Id.ToString(CultureInfo.InvariantCulture) == q
            || m.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
        if (exact != null) { bridge($"!map {HudMenu.PlayArg(exact, "", mode.Id)}"); return; } // in the mode a card's Play would load it in (B7)
        var browser = new HudBrowser();
        browser.Find(q);
        var hits = browser.Matches(catalog, account, search);
        if (hits.Count == 1) { bridge($"!map {HudMenu.PlayArg(hits[0], "", mode.Id)}"); return; }
        Menu(player, q);
        say(hits.Count == 0 ? ChatReply.NoMatch(q) : ChatReply.Matches(hits.Count));
    }

    /// <summary>css_gghud show &lt;account&gt; &lt;page&gt;: the window on that page for that account, as if they had clicked their way there - for
    /// the client test harness, whose CS2 never has focus and so cannot click. Pages: start, maps, fun, players, game, sync, help, how; and while a
    /// vote is on, vote (MAPS on its ballot) and browse (MAPS on the map browser that adds to the vote).</summary>
    public string ShowFor(long account, string page)
    {
        var word = page.Trim().ToLowerInvariant();
        var sub = word is "vote" or "browse" ? word : "";
        if (sub != "") page = nameof(HudSlots.Page.Maps);
        if (!Enum.TryParse<HudSlots.Page>(page, ignoreCase: true, out var p)) return $"no page {page} (pages: {string.Join(", ", Enum.GetNames<HudSlots.Page>())}, and vote or browse while a vote is on)";
        if (Utilities.GetPlayers().FirstOrDefault(x => x is { IsValid: true, IsBot: false } && Account(x) == account) is not { } player) return $"no player [U:1:{account}] on the server";
        if (sub != "" && vote.Round <= 0) return "no vote is on: vote and browse need one (Start a vote first: css_gghud press <account> gg_b_next)";
        worldReady = true;
        if (sub != "") voteView[player.Slot] = (vote.Round, VoteView(player.Slot).Page, sub == "browse" && vote.Open);
        slots.Show(player.Slot, p, DateTime.UtcNow);
        Server.NextFrame(() => { EnsureEntity(); Frame(); });
        return $"window on {p}{(sub != "" ? $" ({sub})" : "")} for {player.PlayerName}";
    }

    /// <summary>css_gghud content &lt;account&gt; &lt;level&gt;: the HUD that account's game showed at the HUD check's click, as if it had clicked - for the
    /// client harness, which cannot click: 46 is v4.6.0, any level a content; 1 an older content (its gold button); 0 forgets it. It lasts until that
    /// player's next disconnect, a map change's included (Gone): the bridge never hears of it.</summary>
    public string SetContent(long account, int level)
    {
        if (level <= 0) hudSeen.Remove(account);
        else hudSeen[account] = level == 1 ? HudCheck.Older : HudVote.ContentOfLevel(level);
        Server.NextFrame(Frame);
        return $"account {account}: HUD {(level <= 0 ? "not seen" : hudSeen[account])}";
    }

    /// <summary>css_gghud press &lt;account&gt; &lt;button&gt;: that player's click on a button, through the same path as a real one - a tab,
    /// a filter, a Fun choice, a card's Play - for the client harness, whose CS2 never has focus. Like a real click it is the HUD check's click:
    /// the version button (gg_hudver_v4.6.0) verifies that content, any other button a HUD of unknown content (HudCheck.cs).</summary>
    public string Press(long account, string buttonId)
    {
        if (entity is not { IsValid: true } e) return "no HUD entity";
        if (Utilities.GetPlayers().FirstOrDefault(x => x is { IsValid: true, IsBot: false } && Account(x) == account) is not { } p) return $"no player [U:1:{account}] on the server";
        OnClicked(p, e, buttonId);
        return $"pressed {buttonId} for {p.PlayerName}";
    }

    /// <summary>css_gghud click: what a click on that button would send for that account, sent - without a client, to try the path
    /// from the button to the bridge. Returns what happened, for the console.</summary>
    public string SimulateClick(long account, string buttonId)
    {
        var command = HudView.Command(buttonId, state, vote);
        if (command == null) return $"button {buttonId} sends nothing for account {account} (state {(state == null ? "unknown" : "known")})";
        if (bridge == null) return "no bridge link";
        var name = Utilities.GetPlayers().FirstOrDefault(x => x is { IsValid: true, IsBot: false } && Account(x) == account)?.PlayerName ?? "HUDtest"; // the player's own name: the bridge keeps it
        bridge.Act(account, name, command);
        return $"sent {command} for account {account}; the bridge's answer arrives with the next frame";
    }

    // ---- the frame: four times a second ----

    void Frame()
    {
        var now = DateTime.UtcNow;
        budget = FrameBudget;
        var humans = Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false }).ToList();
        if (bridge?.Take(now, humans.Count > 0, addonRequired) is { } text && text != lastStateText && HudState.Decode(text) is { } fresh) // a hold draws nothing the bridge may count on (RequireAddon)
        {
            lastStateText = text;
            stateRaw = fresh;
            voteRaw = VoteState.Decode(text);
            stateAt = now;
        }
        // The countdowns, worked out here every frame from the state's end times (BridgeLink, the bridge's HudTransport.cs): the bridge sends a
        // new state only when something changes.
        var bridgeNow = bridge?.BridgeNowMs(now) ?? HudState.Ms(now);
        state = stateRaw?.AsOf(bridgeNow);
        if (state != null) MergeMessages(state, bridgeNow);
        SayNew(stateRaw);
        vote = voteRaw.AsOf(bridgeNow);
        if (bridge?.TakeCatalog(now, state?.CatalogVersion ?? 0) is { } listText && HudCatalog.Decode(listText) is { } list) catalog = list;
        if (bridge?.TakeDash(now, state?.DashVersion ?? 0) is { } dashText && HudDash.Decode(dashText) is { } block) dash = block;
        if (bridge?.TakeSearch(now, state?.SearchVersion ?? 0) is { } searchText && HudSearch.Decode(searchText) is { } index)
        {
            search = index;
            log.LogInformation("Over Strike 2 HUD: search index {Version} - {Count} maps", index.Version, index.Count);
        }
        UpdateCheck(now);
        if (!Live) return;
        RunCheck(now);
        foreach (var (slot, at) in resendAt.ToList())
            if (now >= at) { resendAt.Remove(slot); Forget(slot); }

        tabMode = tabOverride ?? HudTab.Parse(state?.TabMode);
        if (tabMode == HudTab.Mode.Off && tab.CursorSlots().Any())
            log.LogInformation("Over Strike 2 HUD: TAB switched off - {Count} TAB cursor(s) released", tab.ReleaseAll().Count);
        foreach (var (id, text2) in HudView.StaticTexts()) Text(id, id == "gg_b_cmds" ? HudTab.BoardLine(tabMode) : text2);
        foreach (var (id, cls, on) in HudView.StaticClasses()) Class(id, cls, on);
        FunFrame(now); // the Fun page, the same for everybody
        var shown = state ?? new HudState();
        var ownNews = now < localNews.Until; // the news line of the older contents: the plugin's own line wins while it lasts
        foreach (var (id, text2) in HudView.Texts(shown, vote)) if (!(ownNews && id == "gg_news_text")) Text(id, text2);
        foreach (var (id, cls, on) in HudView.Classes(shown, vote)) if (!(ownNews && cls == "news")) Class(id, cls, on);
        if (ownNews) { Text("gg_news_text", localNews.Text); Class(HudView.Global, "news", true); }
        var (prevTexts, prevClasses) = HudMenu.PreviousButton(shown); // the Maps page's Previous
        foreach (var (id, text2) in prevTexts) Text(id, text2);
        foreach (var (id, cls, on) in prevClasses) Class(id, cls, on);
        Pictures(null, [("gg_t_thumb", ThumbOf(shown.Key)), ("gg_k_thumb", ThumbOf(shown.ChangeKey))]); // the now-playing card's and the countdown's pictures, the same for everybody
        WorkshopCards(humans, now);
        Text("gg_rst_secs", HudView.RestartCountdown(shown, now)); // a restart coming: its countdown from its end time, by our own clock

        // Every vote opens everybody's window on the ballot - a player who joins meanwhile too - and holds it while everybody is frozen: while it is
        // open, and while its result counts down to a map change. Nothing closes it then (VoteLocked). When the freeze ends, the windows it opened
        // close for everybody at the same moment. The Maps page draws the vote while it is on.
        if (VoteLocked)
            foreach (var h in humans)
            {
                if (voteShown.GetValueOrDefault(h.Slot) != vote.Round || !slots.WindowOpen(h.Slot))
                {
                    voteShown[h.Slot] = vote.Round;
                    slots.Show(h.Slot, HudSlots.Page.Maps, now);
                    voteOpened.Add(h.Slot);
                }
                slots.Hold(h.Slot, now);
            }
        else if (voteOpened.Count > 0)
        {
            foreach (var slot in voteOpened) slots.VoteOver(slot, now);
            voteOpened.Clear();
        }
        var level = TestLevel ?? HudVote.ContentLevel;
        if (latestSent != 0 && latestSent != level) Class(HudView.Global, HudVote.LatestClass(latestSent), false); // the newest content the server knows moved
        foreach (var (id, cls, on) in HudVote.VersionClasses(level)) Class(id, cls, on);
        latestSent = level;
        foreach (var (name, words) in HudView.PlayersPage(PlayerRows(humans, now))) Var(HudView.Global, name, words); // SERVER, Players
        foreach (var (id, cls, on) in HudVote.ModeClasses(mode.Id)) Class(id, cls, on);
        Var(HudView.Global, HudVote.VersionVar, TestLevel is { } faked ? HudVote.ContentOfLevel(faked) : Hud.Content); // css_gghud level fakes the version's name too
        Var(HudView.Global, HudVote.ModeVar, mode.Name);
        foreach (var (name, words) in HudVote.WhyWords()) Var(HudView.Global, name, words); // the popup's words
        var views = slots.Frame(now).ToDictionary(v => v.Slot, v => v.View);
        foreach (var p in humans)
        {
            var view = views.GetValueOrDefault(p.Slot);
            var account = Account(p);
            // Being checked: the gold button says "Click to play" and is shown even while a map change hides its usual action.
            var checking = checkMode != HudCheck.Mode.Off && gate.Checking(p.Slot);
            if (localNotes.TryGetValue(p.Slot, out var note) && now < note.Until) { shown.Notes[account] = (note.Text, (int)Math.Ceiling((note.Until - now).TotalSeconds)); shown.NoteCats[account] = note.Cat; }
            foreach (var (id, text2) in HudView.PlayerTexts(shown, account, p.PlayerName, view.Returning, vote))
                Text(id, checking && id == "gg_btn_act_text" ? HudCheck.ActText : checking && id == "gg_btn_act_sub" ? HudCheck.ActSub : text2, p);
            foreach (var (id, cls, on) in HudView.PlayerClasses(shown, account, vote)) Class(id, cls, on && !(checking && cls == "act-off"), p);
            foreach (var (id, text2) in HudView.LatelyTexts(shown, account, bridgeNow)) Text(id, text2, p);
            foreach (var (id, cls, on) in HudView.LatelyClasses(shown, account)) Class(id, cls, on, p);
            Class(HudView.Screen, "vcheck", WantsVersion(account), p); // the window's X is the version button (an older content keeps its "Click to play")
            Class(HudView.Screen, "wback", view.Window && view.Returning, p);               // the welcome page's line about the map that is on
            CatchUpFrame(p, now);
            if (shown.Why.TryGetValue(account, out var asked)) Var(HudView.Screen, HudVote.WhyVar, asked.Map, p); // the map the popup asks about: its label inherits it
            Class(HudView.Screen, "win", view.Window, p);
            Class(HudView.Screen, "win-in", view.WindowIn, p);
            Class(HudView.Screen, "draining", view.Draining, p);
            Class(HudView.Screen, "pinned", view.Window && view.Pinned, p); // the welcome closes only by its X: its foot has no idle bar
            Class(HudView.Screen, "toast-1", view.Toast == 1, p);
            Class(HudView.Screen, "toast-in", view.ToastIn, p);
            var joined = joinedAt.TryGetValue(p.Slot, out var at) && (now - at).TotalSeconds < HudVote.JoinSeconds;
            Class(HudView.Screen, "vjoin", joined, p);                                      // an older content's join banner (svge/svlt)
            var vv = VoteView(p.Slot);
            var browsing = vote is { Round: > 0, Open: true } && vv.Browse;
            if (vote.Round <= 0 || browsing) Class(HudView.Screen, "voting", false, p);
            Class(HudView.Screen, "vbrowse", browsing, p);                                  // MAPS, Browse: the map browser adds to the vote
            Class(HudView.Screen, "vlock", VoteLocked, p);                                  // a vote that freezes everybody has no close button
            if (browsing) Var(HudView.Screen, HudVote.BrowseVar, HudVote.BrowseLine(vote), p);
            if (vote.Round > 0) Var(HudView.Screen, HudVote.TabVar, HudVote.TabLabel(vote), p); // the Vote sub-tab's label
            Var(HudView.Screen, HudView.SubCaptionVar, HudView.SubCaption(view.Page, vote.Round > 0 && !browsing, browsing), p);
            foreach (var page in Enum.GetValues<HudSlots.Page>()) Class(HudView.Screen, "page-" + page.ToString().ToLowerInvariant(), view.Window && view.Page == page, p);
            if (view.Window) Menu(p, view.Page, shown, account, now);
            if (view.Window) DashFrame(p); // the Sync page's words and classes, for every open window: ready before its tab is clicked
            if (view.Window) FunBubbles(p, now); // the Fun page's bubbles, for every open window too
            // The window's cursor, or the board's while TAB is held. During a hold a player who joined has no HUD, so no window to close: nothing takes their mouse.
            var capture = (view.Capture || tab.Cursor(p.Slot)) && (addonRequired || keptMenu.Contains(account));
            if (captured.GetValueOrDefault(p.Slot) != capture)
            {
                entity!.SetInputCaptureEnabled(p, capture);
                captured[p.Slot] = capture;
                playerDirty = true;
            }
        }
        MarkChanged();
    }

    /// <summary>The menu page a player has open, for that player only - and only while it is open: the map browser's cards, the
    /// ballot, or the game settings. Everything else on the window is the same for all.</summary>
    void Menu(CCSPlayerController p, HudSlots.Page page, HudState shown, long account, DateTime now)
    {
        var (texts, classes) = page switch
        {
            HudSlots.Page.Maps when vote.Round > 0 && !(vote.Open && VoteView(p.Slot).Browse) => BallotPage(p, account),
            HudSlots.Page.Maps => HudMenu.Maps(catalog, shown, Browser(p.Slot), account, now, search, mode.Id, vote),
            HudSlots.Page.Game => GamePage(p, shown, now),
            _ => (null, null),
        };
        if (texts == null || classes == null) return;
        foreach (var (id, text) in texts) Text(id, text, p);
        foreach (var (id, cls, on) in classes) Class(id, cls, on, p);
        if (page != HudSlots.Page.Maps) return;
        if (vote.Round > 0 && !(vote.Open && VoteView(p.Slot).Browse)) { Pictures(p, HudVote.Thumbs(vote, catalog, VoteView(p.Slot).Page, BallotCards(p.Slot))); return; }
        var card = cards.TryGetValue(p.Slot, out var c) ? c : cards[p.Slot] = new WorkshopCard();
        card.Show(Browser(p.Slot).WorkshopQuery(catalog), now); // a typed Workshop id that no listed map has: its card, asked about once it holds still
        foreach (var (id, text) in card.Texts()) Text(id, text, p);
        foreach (var (id, cls, on) in card.Classes()) Class(id, cls, on, p);
        // A picture is two classes (HudThumbs): what the card's previous picture had and this one does not goes off first, and only then
        // does the new pair come on - a card never holds parts of two pictures at once, which could show the wrong map's.
        Pictures(p, HudMenu.Thumbs(catalog, Browser(p.Slot), account, search));
    }

    Dictionary<string, int> DashPages(int slot) => dashPages.TryGetValue(slot, out var p) ? p : dashPages[slot] = new();

    /// <summary>The Sync page for one player with the window open: the bridge's words as dialog variables on gg_screen that the page's labels
    /// inherit, its classes on gg_screen, and this player's page of each list (HudDash.View). Only what changed goes out.</summary>
    void DashFrame(CCSPlayerController p)
    {
        var (words, classes) = HudDash.View(dash, DashPages(p.Slot));
        foreach (var (name, text) in words) Var(HudView.Screen, name, text, p);
        foreach (var (name, on) in classes) Class(HudView.Screen, name, on, p);
    }

    /// <summary>This player's view of the vote that is on: their page of the ballot and whether the map browser is open on it; a new round starts on its
    /// first page, on the ballot.</summary>
    (int Page, bool Browse) VoteView(int slot)
    {
        if (!voteView.TryGetValue(slot, out var v) || v.Round != vote.Round) { voteView[slot] = v = (vote.Round, 0, false); }
        var page = Math.Clamp(v.Page, 0, HudVote.Pages(vote, BallotCards(slot)) - 1);
        if (page != v.Page) voteView[slot] = v = (v.Round, page, v.Browse);
        return (v.Page, v.Browse);
    }

    /// <summary>The ballot's cards a page for this slot's player: eight for a game known to hold a content before v4.6.0, which has no more cards -
    /// its pager then reaches every option - and MapVote.PageCards for any other (HudVote.CardsFor).</summary>
    int BallotCards(int slot) => Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: false } p ? HudVote.CardsFor(VersionOf(Account(p))) : MapVote.PageCards;

    /// <summary>The ballot's page for this player: its texts and classes, and its variables on gg_screen (Random's button, the hint for the other pages,
    /// the header's clocks). The order of its cards is remembered as drawn: a re-sort keeps the order before it for HudVote.ReSortSeconds (SeenOrder).</summary>
    (List<(string Id, string Text)>, List<(string Id, string Class, bool On)>) BallotPage(CCSPlayerController p, long account)
    {
        var page = VoteView(p.Slot).Page;
        var perPage = BallotCards(p.Slot);
        var (texts, classes, vars) = HudVote.Cards(vote, account, catalog, page, perPage);
        foreach (var (name, text) in vars) Var(HudView.Screen, name, text, p);
        var order = HudVote.Order(vote, page, perPage);
        if (!drawnOrder.TryGetValue(p.Slot, out var drawn) || drawn.Round != vote.Round || drawn.Page != page) drawnOrder[p.Slot] = (vote.Round, page, order, order, DateTime.MinValue);
        else if (!drawn.Now.SequenceEqual(order)) drawnOrder[p.Slot] = (vote.Round, page, order, drawn.Now, DateTime.UtcNow);
        return (texts, classes);
    }

    /// <summary>The order of this player's ballot cards a click counts against: the one drawn before the last re-sort while that re-sort is younger
    /// than HudVote.ReSortSeconds, else the one drawn last; null when nothing of this round and page was drawn for them (the newest order then).</summary>
    int[]? SeenOrder(int slot, int page, DateTime now) =>
        drawnOrder.TryGetValue(slot, out var d) && d.Round == vote.Round && d.Page == page ? ((now - d.ChangedAt).TotalSeconds < HudVote.ReSortSeconds ? d.Before : d.Now) : null;

    /// <summary>The picture of the map with this key: its two classes, none for no key or a map without one.</summary>
    string[] ThumbOf(string key) => key != "" && catalog?.Find(key) is { } entry ? HudThumbs.Classes(entry) : [];

    /// <summary>The pictures for this player, or for everybody (two classes a picture, HudThumbs).</summary>
    void Pictures(CCSPlayerController? p, List<(string Panel, string[] Classes)> wanted)
    {
        foreach (var (panel, want) in wanted)
        {
            var key = (p?.Slot ?? -1, panel);
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
            log.LogWarning("Over Strike 2 HUD: marking the layout for the network failed, left to the engine from now on: {Message}", e.Message);
        }
        globalDirty = playerDirty = false;
    }

    /// <summary>One engine call's worth of this frame's budget: false once it is spent - the caller then skips the call without
    /// remembering it as sent, so the next frame sends it. A page change for a full server stays inside a few frames instead of one long one.</summary>
    bool Spend() => budget-- > 0;

    /// <summary>The Players page's rows (SERVER, Players): every human on the server, by name - their team, the HUD their game showed (the check's
    /// click), the check's clock while it waits for that click, their vote while one is open, and how long they have been on since their last real join.</summary>
    List<HudView.PlayerRow> PlayerRows(List<CCSPlayerController> humans, DateTime now)
    {
        var rows = new List<HudView.PlayerRow>();
        foreach (var p in humans.OrderBy(x => x.PlayerName, StringComparer.OrdinalIgnoreCase))
        {
            var account = Account(p);
            var team = p.Team switch { CsTeam.CounterTerrorist => "CT", CsTeam.Terrorist => "T", _ => "SPEC" };
            var checking = checkMode != HudCheck.Mode.Off && gate.Checking(p.Slot) ? gate.SecondsLeft(p.Slot, now) : 0;
            var voted = vote is { Open: true, Round: > 0 } ? vote.ChoiceOf(account) > 0 ? "voted" : "waiting" : "";
            var since = joinedAt.TryGetValue(p.Slot, out var at) ? now - at : (TimeSpan?)null;
            rows.Add(new HudView.PlayerRow(p.PlayerName, team, VersionOf(account), checking, voted, HudView.OnFor(since)));
        }
        return rows;
    }

    /// <summary>A vote freezes everybody: the window stays open until the freeze is over, and nothing closes it. The freeze's own decision (FreezePlan), not the bridge's last word: a
    /// lost bridge frees the windows at the moment it frees the pawns. Set by the plugin at its load.</summary>
    public Func<bool>? Frozen { get; set; }

    bool VoteLocked => vote.Round > 0 && Frozen?.Invoke() == true;

    /// <summary>The vote as the bridge last said it: the freeze reads it (VoteFreeze.cs).</summary>
    public VoteState Vote => vote;

    readonly HashSet<int> voteOpened = new(); // the slots whose window the vote that is on opened: closed together when the freeze ends

    /// <summary>Whether this slot has the window open now (the spawn cloak reads it).</summary>
    public bool WindowOpen(int slot) => slots.WindowOpen(slot);

    readonly Dictionary<(string, string), string> globalVars = new();

    /// <summary>A named dialog variable on a panel every content has (gg_global): the layout's labels inherit it ({s:osver}).</summary>
    void Var(string id, string name, string text)
    {
        if (globalVars.TryGetValue((id, name), out var had) && had == text) return;
        if (!Spend()) return;
        entity!.SetDialogVariableString(id, name, Hud.Plain(text));
        globalVars[(id, name)] = text;
        globalDirty = true;
    }

    readonly Dictionary<int, Dictionary<(string, string), string>> playerVars = new();

    /// <summary>The same for one player alone (the map the popup asks about, on gg_screen).</summary>
    void Var(string id, string name, string text, CCSPlayerController player)
    {
        var mine = playerVars.TryGetValue(player.Slot, out var v) ? v : playerVars[player.Slot] = new();
        if (mine.TryGetValue((id, name), out var had) && had == text) return;
        if (!Spend()) return;
        entity!.SetDialogVariableStringForPlayer(player, id, name, Hud.Plain(text));
        mine[(id, name)] = text;
        playerDirty = true;
    }

    void Text(string id, string text, CCSPlayerController? player = null)
    {
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
