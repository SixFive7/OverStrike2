// The frame, four times a second: the bridge's newest state taken, and every player's view of it sent.
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

sealed partial class HudScreen
{
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
}
