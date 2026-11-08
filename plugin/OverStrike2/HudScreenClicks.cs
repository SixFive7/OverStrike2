// A click on the HUD: the check's click, the board's and the window's buttons, the ballot's cards, the map browser, a Workshop card's download.
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

sealed partial class HudScreen
{
    // ---- clicks ----

    void OnClicked(CCSPlayerController player, CCSCustomHudLayout layout, string buttonId)
    {
        if (!Live || player is not { IsValid: true, IsBot: false } || layout is not { IsValid: true } || layout.Index != entity!.Index) return;
        var now = DateTime.UtcNow;
        // Any click at all proves this player's game shows the HUD (the check, HudCheck.cs). While they were being checked, the window's X is the version
        // button, whose id names the content (HudVersion.Button), and the live column's action button says "Click to play" too: either click is the check
        // and closes the window - the action button's must not roll a map as !next would. A click from a content before v4.8.0 names its button gg_, which
        // no button of ours is: it proves the HUD, which content not said, and asks for nothing.
        var who = Account(player);
        var wasChecked = gate.Checking(player.Slot);
        var proved = HudVersion.OfButton(buttonId);
        var version = proved ?? HudVersion.Unknown;
        if (!Verified(who) || (version != HudVersion.Unknown && VersionOf(who) != version)) Verify(player, who, wasChecked, version);
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
        if (buttonId is HudView.BoardUp or HudView.BoardDown or HudView.BoardNext) // the board's own buttons, under the TAB cursor
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
        var me = Player(player.Slot);
        if (HudDash.Click(dash, buttonId, me.DashPages)) { Server.NextFrame(Frame); return; } // the Sync page's pagers
        if (HudBrowser.AsksHowToSearch(buttonId)) { LocalNote(player.Slot, ChatReply.HowToSearch); Server.NextFrame(Frame); return; } // the search hint, or v4.8.0's search box and keys: typing is chat's
        if (buttonId == HudView.TabMaps && vote.Round > 0) { me.VoteView = (vote.Round, VoteView(player.Slot).Page, false); Server.NextFrame(Frame); return; } // MAPS opens on its first page: the ballot while a vote is on
        var account = Account(player);
        var browser = me.Browser;
        if (buttonId == HudView.ButtonAct && HudView.Command(buttonId, state, vote) == HudView.OpenVote) { slots.Show(player.Slot, HudSlots.Page.Maps, now); Server.NextFrame(Frame); return; } // the vote is on: the Maps page shows it
        if (vote.Round > 0 && slots.PageOf(player.Slot) == HudSlots.Page.Maps)
        {
            var view = VoteView(player.Slot);
            if (buttonId == HudVote.BrowseButton && vote.Open) { me.VoteView = (vote.Round, view.Page, true); Busy(player, account); Server.NextFrame(Frame); return; } // the map browser adds to the vote
            if (buttonId == HudVote.BackButton) { me.VoteView = (vote.Round, view.Page, false); Busy(player, account); Server.NextFrame(Frame); return; }
            if (!(view.Browse && vote.Open))
            {
                // The vote is on the Maps page's cards (HudVote.cs), a page of ten: a card's Play is a vote - on the card this player's vote is on, it
                // takes it back - Random's button too, the pager pages (activity: it resets the inactivity timer). A card names the map this player saw on
                // it: the cards re-sort as votes come in, and a click made on the order before a re-sort arrives after it (SeenOrder).
                if (HudVote.PageClick(vote, buttonId, view.Page, MapVote.PageCards) is { } page) { me.VoteView = (vote.Round, page, false); Busy(player, account); Server.NextFrame(Frame); return; }
                if (HudVote.Click(vote, buttonId, view.Page, account, HudFrame.SeenOrder(me, vote.Round, view.Page, now), MapVote.PageCards) is { } cast)
                {
                    Act(player, account, cast); // chat and the board say it: the bridge's line of who voted and who has yet to
                }
                Server.NextFrame(Frame);
                return;
            }
            // The map browser while the vote is on: its own controls as ever; a card's Play puts the map on the vote, with this player's vote (the bridge's
            // !map during a vote; its line says so in chat), and goes back to the ballot's last page, where the map appears for everybody.
            var browsing = me.Browser;
            if (catalog != null && browsing.Click(buttonId, browsing.Matches(catalog, account, search).Count)) { Busy(player, account); Server.NextFrame(Frame); return; } // a page, a sort, a filter: activity
            if (buttonId.StartsWith("os_m", StringComparison.Ordinal) && buttonId.EndsWith("_play", StringComparison.Ordinal)
                && HudMenu.Command(buttonId, catalog, state, browsing, account, now, search, mode.Id, vote) is { } add && add.StartsWith("!map ", StringComparison.Ordinal))
            {
                Act(player, account, add);
                if (browsing.ConfirmKey == "") me.VoteView = (vote.Round, int.MaxValue, false); // a risky map's first click only arms its confirmation: the second one adds it
                Server.NextFrame(Frame);
                return;
            }
        }
        if (catalog != null && browser.Click(buttonId, browser.Matches(catalog, account, search).Count)) { Server.NextFrame(Frame); return; }
        if (FunClicked(player, buttonId, now)) return; // the Fun page (HudScreenFun.cs)
        if (BotsClicked(player, buttonId, now)) return; // the Bots page (HudScreenBots.cs)
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
        var card = Player(player.Slot).Card;
        if (card.Item is not { Refusal: null } item || card.At is not (WorkshopCard.Step.Ready or WorkshopCard.Step.Failed)) return;
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
            var card = Player(p.Slot).Card;
            if (card.At == WorkshopCard.Step.None) continue;
            switch (card.At)
            {
                case WorkshopCard.Step.Checking:
                    if (WorkshopCard.ServerRefusal(card.Id, catalog, addon) is { } no) card.Refuse(no.Name, no.Why); // the server's own no, the HUD's item too: nothing to ask
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
}
