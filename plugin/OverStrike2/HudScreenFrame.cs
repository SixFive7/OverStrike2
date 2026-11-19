// The frame, four times a second: the bridge's newest state taken, the HUD check run, and what everybody and every player sees drawn by HudFrame
// (../../bridge/Shared/HudFrame.cs, which --selftest runs too) onto the entity (HudScreenSend.cs).
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

sealed partial class HudScreen
{
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
            if (fresh.RematchAtMs > 0) rematchAtMs = fresh.RematchAtMs; // kept after the countdown leaves the state: the restart it sends comes then
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
        if (bridge?.TakeHistory(now, state?.HistoryVersion ?? 0) is { } past) history = past; // the HISTORY page's matches, decoded off the game thread
        if (bridge?.TakeStats(now, state?.StatsVersion ?? 0) is { } records) stats = records; // the STATS page's block, the same way
        if (bridge?.TakeSearch(now, state?.SearchVersion ?? 0) is { } searchText && HudSearch.Decode(searchText) is { } index)
        {
            search = index;
            log.LogInformation("Over Strike 2 HUD: search index {Version} - {Count} maps", index.Version, index.Count);
        }
        UpdateCheck(now);
        if (!Live) return;
        RunCheck(now);
        foreach (var (slot, player) in players)
            if (player.ResendAt is { } at && now >= at) { player.ResendAt = null; Forget(slot); }
        tabMode = tabOverride ?? HudTab.Parse(state?.TabMode);
        if (tabMode == HudTab.Mode.Off && tab.CursorSlots().Any())
            log.LogInformation("Over Strike 2 HUD: TAB switched off - {Count} TAB cursor(s) released", tab.ReleaseAll().Count);
        WorkshopCards(humans, now);

        drawing.Clear();
        foreach (var p in humans) { drawing[p.Slot] = p; shownNames[p.Slot] = p.PlayerName; } // the name each slot's player is shown by: their leave's line says it (NameShown)
        var matchScreen = MatchScreen();
        matchEndLifted = ChatLift(matchScreen, now);
        var world = new HudFrame.World(state ?? new HudState(), vote, catalog, search, dash, mode, tabMode, TestLevel ?? HudVersion.Level, VoteLocked, addonRequired,
            FunValue, FunSetBy, now, bridgeNow, voteRaw.AsOf(bridgeNow + HudVote.BarLeadMs), stateRaw?.AsOf(bridgeNow + HudVote.BarLeadMs), // the bars a step ahead (HudVote.BarLeadMs)
            chat, ChatLive(now), matchScreen, download is { } d && now < d.Until ? d.Download : null, chatRoute, matchEndLifted, history, stats);
        frame.Draw(world, humans.Select(p => Human(p, now)).ToList(), slots, Player, funClicks, this, botClicks); // every panel by its one name, os_ (HudVersion.Oldest)
        ScoresEnded(humans);
        MarkChanged();
    }

    /// <summary>Whether the chat column's feed is live (HudChat): the bridge answers - no failed poll in a row, and its last answer within ChatStaleSeconds. A quiet bridge
    /// answers the long poll every Hud.LongPollSeconds (25 s), so the window is longer than that: 15 s hid the column between two answers (the live lab, release 218,
    /// 08:59:21-28). A bridge that stops answering turns the column off within two polls, so CS2's own chat shows again; a plugin that stops drawing frames leaves
    /// its last beat, which the stylesheet lets run out.</summary>
    bool ChatLive(DateTime now) => bridge is { } link && link.Failures < 2 && now - link.LastAnswer < TimeSpan.FromSeconds(ChatStaleSeconds);
    const int ChatStaleSeconds = Hud.LongPollSeconds + 35;

    /// <summary>Whether the match-end screen is up (the game's phase, MatchPhase.Ended): CS2 lifts its chat then, and the chat column follows it.</summary>
    bool MatchScreen()
    {
        if (rulesProxy is not { IsValid: true })
            rulesProxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        return rulesProxy is { IsValid: true, GameRules: { } rules } && rules.GamePhase == MatchPhase.Ended;
    }

    CCSGameRulesProxy? rulesProxy;

    /// <summary>A SCORES look that ended by itself - the vote's decision, the screen's end, the window's close (HudScores.Ended; no timer, 292 a) - is logged once,
    /// and forgotten; Back to the vote's own click logs itself. scoresOn: the slots whose look was on at the last frame or click.</summary>
    void ScoresEnded(IEnumerable<CCSPlayerController> humans)
    {
        foreach (var p in humans)
        {
            var me = Player(p.Slot);
            var window = slots.WindowOpen(p.Slot);
            if (window && HudScores.On(me, vote, matchEndLifted)) { scoresOn.Add(p.Slot); continue; }
            if (!scoresOn.Remove(p.Slot) || me.Scores == null) continue;
            log.LogInformation("Over Strike 2 HUD: {Name} the window is back ({Why})", p.PlayerName, HudScores.Ended(me, vote, window, matchEndLifted));
            me.Scores = null;
        }
    }

    readonly HashSet<int> scoresOn = new();

    /// <summary>The map ends with a SCORES look on: the match-end screen goes with the map, and no frame comes to see the look end (ScoresEnded) - the lab round on
    /// rel-220 found no line for it. Logged here as the look's own end, and forgotten.</summary>
    void ScoresEndWithMap()
    {
        foreach (var slot in scoresOn.ToList())
        {
            if (players.TryGetValue(slot, out var me) && me.Scores != null)
            {
                log.LogInformation("Over Strike 2 HUD: {Name} the window is back ({Why})", Utilities.GetPlayerFromSlot(slot) is { IsValid: true } p ? p.PlayerName : $"slot {slot}",
                    "the match-end screen ended with the map");
                me.Scores = null;
            }
            scoresOn.Remove(slot);
        }
    }

    /// <summary>The match history as the bridge last sent it (BridgeLink.TakeHistory): the HISTORY page.</summary>
    HudHistory? history;

    /// <summary>The STATS page's block as the bridge last sent it (BridgeLink.TakeStats).</summary>
    HudStats? stats;

    /// <summary>Whether CS2 has lifted its chat for the match-end screen (meos), as of the last frame - the SCORES button's clicks go by it (HudScores).</summary>
    bool matchEndLifted;

    /// <summary>Whether CS2 has lifted its chat for the match-end screen: the phase has been Ended for ChatLiftSeconds. CS2 lifts it only once that screen shows its
    /// scoreboard, a second or two after the phase changes - the column lifted with the phase while CS2's fresh line still stood below it (the live lab's film of
    /// release 218, 82.2-82.7 s) - so with CS2's chat open the column's foot follows its typing line up a moment after it has moved (meos), and at once where the
    /// game names that moment itself (the stylesheet: HUD_ENDOFMATCH_VISIBLE with HUD_SCOREBOARD_VISIBLE). With CS2's chat closed the column runs down to y 1000
    /// in every state, so it covers CS2's lines at either height.</summary>
    bool ChatLift(bool matchScreen, DateTime now)
    {
        if (!matchScreen) { matchEndAt = null; return false; }
        matchEndAt ??= now;
        return now - matchEndAt.Value >= TimeSpan.FromSeconds(ChatLiftSeconds);
    }

    DateTime? matchEndAt;
    const int ChatLiftSeconds = 3;

    /// <summary>The chat column's route (HudChat.Route): the plugin's default, or os_hud chat's until the plugin reloads.</summary>
    HudChat.Route chatRoute = HudChat.Default;

    /// <summary>A human on the server as the frame needs them: the HUD their game showed, the check's clock, their time on, TAB's cursor, the hold.</summary>
    HudFrame.Human Human(CCSPlayerController p, DateTime now)
    {
        var account = Account(p);
        var checking = checkMode != HudCheck.Mode.Off && gate.Checking(p.Slot);
        return new(p.Slot, account, p.PlayerName, p.Team switch { CsTeam.CounterTerrorist => "CT", CsTeam.Terrorist => "T", _ => "SPEC" }, VersionOf(account),
            checking, checking ? gate.SecondsLeft(p.Slot, now) : 0, joins.Since(account, now), tab.Cursor(p.Slot), keptMenu.Contains(account), recentSearches.GetValueOrDefault(account),
            tab.Holding(p.Slot));
    }

    /// <summary>This player's view of the vote that is on (HudFrame.VoteView), in pages of the ballot's cards (MapVote.PageCards).</summary>
    (int Page, bool Browse) VoteView(int slot) => HudFrame.VoteView(Player(slot), vote, MapVote.PageCards);
}
