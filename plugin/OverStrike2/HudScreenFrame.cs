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
        foreach (var p in humans) drawing[p.Slot] = p;
        var world = new HudFrame.World(state ?? new HudState(), vote, catalog, search, dash, mode, tabMode, TestLevel ?? HudVersion.Level, VoteLocked, addonRequired,
            FunValue, FunSetBy, now, bridgeNow);
        frame.Draw(world, humans.Select(p => Human(p, now)).ToList(), slots, Player, funClicks, this, botClicks); // every panel by its one name, os_ (HudVersion.Oldest)
        MarkChanged();
    }

    /// <summary>A human on the server as the frame needs them: the HUD their game showed, the check's clock, their time on, TAB's cursor, the hold.</summary>
    HudFrame.Human Human(CCSPlayerController p, DateTime now)
    {
        var account = Account(p);
        var checking = checkMode != HudCheck.Mode.Off && gate.Checking(p.Slot);
        return new(p.Slot, account, p.PlayerName, p.Team switch { CsTeam.CounterTerrorist => "CT", CsTeam.Terrorist => "T", _ => "SPEC" }, VersionOf(account),
            checking, checking ? gate.SecondsLeft(p.Slot, now) : 0, joins.Since(account, now), tab.Cursor(p.Slot), keptMenu.Contains(account));
    }

    /// <summary>This player's view of the vote that is on (HudFrame.VoteView), in pages of the ballot's cards (MapVote.PageCards).</summary>
    (int Page, bool Browse) VoteView(int slot) => HudFrame.VoteView(Player(slot), vote, MapVote.PageCards);
}
