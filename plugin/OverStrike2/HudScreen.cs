using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The HUD on screen: one custom_hud_layout entity per map, driven from ../../bridge/Shared/. What to show is decided there - HudFrame composes each frame
/// out of HudView's words, HudSlots' windows and toasts and the menu's pages, and the bridge's --selftest runs it; this class takes the bridge's state, runs the
/// HUD check, keeps a record per slot (HudPlayer) and owns the entity, which it sends only what changed.
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
    readonly Dictionary<long, string> hudSeen = new(); // account -> the HUD its game showed at the check's click since its last real join (HudVersion.IsWord); the bridge keeps the same for the players on the server
    readonly ItemWatch item;                          // whether Steam shows the item to everybody yet: nobody is disconnected until it does
    HudCheck.Mode checkMode;                          // the check in force as of the last frame
    HudCheck.Mode checkHigh;                          // the strictest it has been on this map: only a stricter one re-arms the waiting players
    readonly HudTab tab = new();                      // TAB (HudTab.cs): the board's cursor while it is held
    HudTab.Mode tabMode;                              // down or off, as of the last frame (hud.tab, or os_hud tab)
    HudTab.Mode? tabOverride;                         // os_hud tab off|down: until the plugin reloads
    CCSCustomHudLayout? entity;
    bool worldReady;

    HudState? stateRaw, state;                        // the bridge's newest state as it came (end times), and as of this frame (its countdowns worked out); null until the first answer
    VoteState voteRaw = new(), vote = new();          // the vote and the modes, off the same text (MapVote.cs): as it came, and as of this frame
    readonly Dictionary<int, HudPlayer> players = new(); // slot -> everything the HUD keeps for that slot's player (HudPlayer.cs): new with every player, gone when they leave
    readonly HudFrame frame = new();                  // what a frame shows (../../bridge/Shared/HudFrame.cs): this class takes the state, runs the check and sends
    Modes.Mode mode = Modes.ArmsRace;                 // the mode the map that is on was loaded in (the settings module, os_mode)
    readonly JoinTimes joins = new();                 // each account's last real join: the version banner of v4.0.0 to v4.5.0 for a while, and the Players page's time on
    HudCatalog? catalog;                              // the menu's map list, fetched again whenever the state's version moves
    HudSearch search = new();                         // the search index (names, tags, descriptions), fetched again when its version moves
    readonly WorkshopPlay plays;                      // unlisted Workshop ids: the Web API and the downloads (WorkshopPlay.cs)
    (string Text, DateTime Until) localNews = ("", default);  // the older contents' news line from here, not the bridge: the restart watcher, the block's notices, the item check
    HudDash? dash;                                           // the Sync page's block: every word of it the bridge's, fetched when its version moves
    DateTime stateAt;
    string lastStateText = "";

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
        plugin.RegisterListener<Listeners.OnMapStart>(_ => { slots.MapStarted(); foreach (var p in players.Values) p.ResendAt = null; gate.MapStarted(); tab.MapStarted(); checkHigh = HudCheck.Mode.Off; keptMenu.Clear(); });
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
                     typeof(HudDash), typeof(VoteFreeze), typeof(FreezePlan), typeof(JoinTimes), typeof(HudFrame), typeof(HudPlayer), typeof(MapVote), typeof(VoteState),
                     typeof(MapTags), typeof(Modes) })
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
        joins.Joined(Account(player), DateTime.UtcNow);
        hudSeen.Remove(Account(player)); // a real join: the HUD check asks again - a reconnecting player may have a new HUD
        Post(Messages.Tagline, Hud.Headline, Account(player));
        if (player.PawnIsAlive) { worldReady = true; Server.NextFrame(EnsureEntity); }
    }

    /// <summary>The menu, on its map browser (the board's Open full menu, TAB + E); with words, the browser searching for them from its
    /// first page (a Workshop link pasted in chat).</summary>
    public void Menu(CCSPlayerController player, string? find = null)
    {
        worldReady = true;
        var me = Player(player.Slot);
        if (find != null) me.Browser.Find(find);
        if (find != null && vote is { Open: true, Round: > 0 }) { me.VoteView = (vote.Round, VoteView(player.Slot).Page, true); Busy(player, Account(player)); } // a search during the vote: the map browser that adds to it
        slots.Show(player.Slot, HudSlots.Page.Maps, DateTime.UtcNow);
        Server.NextFrame(() => { EnsureEntity(); Frame(); }); // not inside the player's own chat command (see the class summary)
    }

    /// <summary>The record of the player in this slot: a new one for a new player (Leave drops the last one's).</summary>
    HudPlayer Player(int slot) => players.TryGetValue(slot, out var p) ? p : players[slot] = new HudPlayer();

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
        Player(player.Slot).ResendAt = DateTime.UtcNow.AddSeconds(2);
        // The HUD check: the first spawn on a map of a player not seen clicking since their last real join opens the window, "Click to play" on
        // its gold button, and starts their clock (HudGate). Cheap bookkeeping only; the chat line goes out a frame later.
        var account = Account(player);
        if (gate.Spawned(player.Slot, account, Verified(account), DateTime.UtcNow) && checkMode != HudCheck.Mode.Off) Prompt(player.Slot, DateTime.UtcNow);
        TellNoMenu(player);
    }

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

    /// <summary>The settings module's mode went on (os_mode, a map start): the HUD's classes follow it.</summary>
    public void SetMode(Modes.Mode m) { mode = m; Server.NextFrame(Frame); }

    /// <summary>A player left (a map change's reconnect too): their slot forgets them (Leave), and the HUD their game showed is forgotten here too, not only at
    /// css_guide's real join - through a map change's reconnect the bridge keeps its own (the live state's ok lines), which Verified and VersionOf read.</summary>
    public void Gone(int slot)
    {
        if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: false } leaving) hudSeen.Remove(Account(leaving));
        Leave(slot);
        captured.Remove(slot);
    }

    /// <summary>Everything the HUD kept for the player in this slot goes, in one place: their record (HudPlayer) and their part of the window, the check, TAB and the
    /// Fun page's bubbles, and what was sent to them (sent again). What a player keeps through a reconnect is kept by account (the HUD their game showed, their time on).</summary>
    void Leave(int slot)
    {
        players.Remove(slot);
        slots.Gone(slot);
        gate.Gone(slot);
        tab.Gone(slot);
        funClicks.Gone(slot);
        Forget(slot);
    }

    public string Status() =>
        $"entity {(Live ? entity!.Index.ToString() : "none")}, world {(worldReady ? "ready" : "not ready")}, " +
        $"state {(state == null ? "none" : $"{(DateTime.UtcNow - stateAt).TotalSeconds:0}s old: {state.Map} by {state.By}")}, vote {(vote.Round > 0 ? $"round {vote.Round} {(vote.Open ? "open" : "decided")}{(vote.Freeze ? ", everybody frozen" : "")}" : "none")}, " +
        $"bridge {(bridge == null ? "off" : bridge.Failures == 0 ? "ok" : $"{bridge.Failures} failures ({bridge.LastError})")}, " +
        $"window open for [{string.Join(", ", Utilities.GetPlayers().Where(p => slots.WindowOpen(p.Slot)).Select(p => p.PlayerName))}], " +
        $"check {HudCheck.Name(checkMode)} (hud.check {(state == null ? "?" : HudCheck.Name(HudCheck.Parse(state.CheckMode)))}, item {item.Describe(DateTime.UtcNow)}), " +
        $"waiting for a click from [{string.Join(", ", Utilities.GetPlayers().Where(p => gate.Checking(p.Slot)).Select(p => p.PlayerName))}], " +
        $"TAB {HudTab.Name(tabMode)}{(tabOverride != null ? " (os_hud tab)" : "")}, board cursor for [{string.Join(", ", Utilities.GetPlayers().Where(p => tab.Cursor(p.Slot)).Select(p => p.PlayerName))}], " +
        $"search index {search.Count} maps (version {search.Version}), sync page {(dash == null ? "no block yet" : $"block {dash.Version}: {dash.Words.Count} words, {string.Join(", ", dash.Lists.Select(l => $"{l.Name} {l.Rows.Count}"))}")}, workshop {plays.Describe()}, " +
        $"addon {(addonRequired ? "required of joiners" : "NOT required (Steam does not show it to everybody)")}, HUDs seen: [{string.Join(", ", Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false }).Select(p => $"{p.PlayerName} {(VersionOf(Account(p)) is { Length: > 0 } v ? v : "-")}"))}], fun {string.Join(", ", Fun.Settings.Select(s => $"{s.Id} {Fun.Showing(s, FunValue).Id}"))}";

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
        Leave(slot);
        if (Live && Utilities.GetPlayerFromSlot(slot) is { IsValid: true, IsBot: false } p)
        {
            foreach (var cls in new[] { "win", "win-in", "draining", "pinned", "toast-1", "toast-in", "why", "why-run", "cu", "cu-in", "cu-run", "wl", "wback" }.Concat(Enum.GetValues<HudSlots.Page>().Select(page => "page-" + page.ToString().ToLowerInvariant())))
                entity!.SetHasClassForPlayer(p, HudView.Screen, cls, false);
            entity!.SetInputCaptureEnabled(p, false);
            playerDirty = true;
        }
        captured[slot] = false;
    }

    static long Account(CCSPlayerController p) => p.SteamID >= 76561197960265728UL ? (long)(p.SteamID - 76561197960265728UL) : -1;

    /// <summary>A vote freezes everybody: the window stays open until the freeze is over, and nothing closes it. The freeze's own decision (FreezePlan), not the bridge's last word: a
    /// lost bridge frees the windows at the moment it frees the pawns. Set by the plugin at its load.</summary>
    public Func<bool>? Frozen { get; set; }

    bool VoteLocked => vote.Round > 0 && Frozen?.Invoke() == true;

    /// <summary>The vote as the bridge last said it: the freeze reads it (VoteFreeze.cs).</summary>
    public VoteState Vote => vote;

    /// <summary>Whether this slot has the window open now (the spawn cloak reads it).</summary>
    public bool WindowOpen(int slot) => slots.WindowOpen(slot);
}
