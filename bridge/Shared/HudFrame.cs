// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.

/// <summary>What the HUD shows in a frame, four times a second, for everybody and for each player: the live state, the vote and the menu's pages worked out
/// into texts, classes, dialog variables and pictures. The plugin takes the bridge's state, runs the HUD check and hands the frame what it knows of each
/// player (Human); the frame decides, and its screen (IScreen) puts it on the entity - the plugin's sends only what changed, inside a budget of engine calls
/// (HudScreenSend.cs). --selftest runs this same composition against a screen that records it. The order is the plugin's budget order: what everybody sees
/// first, then player by player.</summary>
public sealed class HudFrame
{
    /// <summary>Where a frame goes. slot: a player's, or Everybody. Class answers false when the screen could not send it this frame (out of budget).</summary>
    public interface IScreen
    {
        void Text(string id, string text, int slot = Everybody);
        bool Class(string id, string cls, bool on, int slot = Everybody);
        void Var(string id, string name, string text, int slot = Everybody);
        /// <summary>A picture is two classes (HudThumbs) on a panel: what a panel's previous picture had and the new one has not goes off first, and only then does
        /// the new pair come on, so a card never holds parts of two pictures at once.</summary>
        void Pictures(int slot, List<(string Panel, string[] Classes)> wanted);
        /// <summary>Whether this player's mouse belongs to the HUD: the window's cursor, or the board's while TAB is held.</summary>
        void Capture(int slot, bool on);
    }

    public const int Everybody = -1;

    /// <summary>A human on the server as the frame needs them: their slot and account, name and team, the HUD their game showed at the check's click
    /// (HudVersion.IsWord; "" not seen yet), whether the check waits for their click and its seconds left, how long they have been on since their last real
    /// join (null: not known), whether TAB's board has their cursor, and whether they were on when a hold began (they keep the HUD until the map changes).</summary>
    public sealed record Human(int Slot, long Account, string Name, string Team, string Version, bool Checking, int CheckSeconds, TimeSpan? JoinedFor,
        bool TabCursor = false, bool KeptMenu = false);

    /// <summary>What the frame draws from: the live state as of this frame (its countdowns worked out) and the vote; the map list, the search index and the Sync
    /// page's block; the mode the map that is on was loaded in; TAB's mode; the content level the version classes say (os_hud level fakes it); whether a vote
    /// holds everybody's window (its freeze) and whether joiners get the HUD (no hold); the Fun page's values and who set them; now, by the plugin's clock and
    /// the bridge's.</summary>
    public sealed record World(HudState State, VoteState Vote, HudCatalog? Catalog, HudSearch Search, HudDash? Dash, Modes.Mode Mode, HudTab.Mode TabMode, int Level,
        bool VoteLocked, bool AddonRequired, Func<string, string?> FunValue, Func<string, string?>? FunSetBy, DateTime Now, long BridgeNowMs);

    int latestSent; // the level of the latest-N class on os_global: turned off when it moves

    /// <summary>A new entity has nothing on it yet: every class goes out again.</summary>
    public void Forget() => latestSent = 0;

    /// <summary>botClicks: the Bots page's refused clicks, as funClicks holds the Fun page's; none, none drawn (a test about something else).</summary>
    public void Draw(World w, IReadOnlyList<Human> humans, HudSlots slots, Func<int, HudPlayer> playerOf, FunClicks funClicks, IScreen screen, FunClicks? botClicks = null)
    {
        var (s, vote, now) = (w.State, w.Vote, w.Now);
        botClicks ??= new FunClicks();
        foreach (var (id, text) in HudView.StaticTexts()) screen.Text(id, id == "os_b_cmds" ? HudTab.BoardLine(w.TabMode) : text);
        foreach (var (id, cls, on) in HudView.StaticClasses()) screen.Class(id, cls, on);
        var funWhy = FunPage(w, funClicks, screen);
        var botWhy = BotsPage(w, botClicks, screen);
        foreach (var (id, text) in HudView.Texts(s, vote)) screen.Text(id, text);
        foreach (var (id, cls, on) in HudView.Classes(s, vote)) screen.Class(id, cls, on);
        var (prevTexts, prevClasses) = HudMenu.PreviousButton(s); // the Maps page's Previous
        foreach (var (id, text) in prevTexts) screen.Text(id, text);
        foreach (var (id, cls, on) in prevClasses) screen.Class(id, cls, on);
        screen.Pictures(Everybody, [("os_t_thumb", ThumbOf(w.Catalog, s.Key)), ("os_k_thumb", ThumbOf(w.Catalog, s.ChangeKey))]); // the now-playing card's and the countdown's
        screen.Text("os_rst_secs", HudView.RestartCountdown(s, now)); // a restart coming: its countdown from its end time, by our own clock

        // Every vote opens everybody's window on the ballot - a player who joins meanwhile too - and holds it while everybody is frozen: while it is open, and
        // while its result counts down to a map change. Nothing closes it then (the plugin's VoteLocked). When the freeze ends, the windows it opened close for
        // everybody at the same moment. The Maps page draws the vote while it is on.
        if (w.VoteLocked)
            foreach (var h in humans)
            {
                var p = playerOf(h.Slot);
                if (p.VoteShown != vote.Round || !slots.WindowOpen(h.Slot))
                {
                    p.VoteShown = vote.Round;
                    slots.Show(h.Slot, HudSlots.Page.Maps, now);
                    p.VoteOpened = true;
                }
                slots.Hold(h.Slot, now);
            }
        else
            foreach (var h in humans)
                if (playerOf(h.Slot) is { VoteOpened: true } p) { slots.VoteOver(h.Slot, now); p.VoteOpened = false; }

        if (latestSent != 0 && latestSent != w.Level) screen.Class(HudView.Global, HudVersion.LatestClass(latestSent), false); // the newest content the server knows moved
        foreach (var (id, cls, on) in HudVersion.Classes(w.Level)) screen.Class(id, cls, on);
        latestSent = w.Level;
        foreach (var (name, words) in HudView.PlayersPage(PlayerRows(humans, vote))) screen.Var(HudView.Global, name, words); // SERVER, Players
        foreach (var (id, cls, on) in HudVote.ModeClasses(w.Mode.Id)) screen.Class(id, cls, on);
        screen.Var(HudView.Global, HudVersion.Var, w.Level == HudVersion.Level ? HudVersion.Content : HudVersion.OfLevel(w.Level)); // a faked level fakes the version's name too
        screen.Var(HudView.Global, HudVote.ModeVar, w.Mode.Name);
        foreach (var (name, words) in HudVote.WhyWords()) screen.Var(HudView.Global, name, words); // the popup's words
        var views = slots.Frame(now).ToDictionary(v => v.Slot, v => v.View);
        foreach (var h in humans) Player(w, h, playerOf(h.Slot), views.GetValueOrDefault(h.Slot), funClicks, funWhy, screen, botClicks, botWhy);
    }

    /// <summary>One player's part of the frame: their own words and marks, their window and its page, the menu page they have open.</summary>
    static void Player(World w, Human h, HudPlayer p, HudSlots.View view, FunClicks funClicks, List<(string Name, string Text)> funWhy, IScreen screen,
        FunClicks botClicks, List<(string Name, string Text)> botWhy)
    {
        var (s, vote, now, slot, account) = (w.State, w.Vote, w.Now, h.Slot, h.Account);
        if (p.Note is { } note && now < note.Until) { s.Notes[account] = (note.Text, (int)Math.Ceiling((note.Until - now).TotalSeconds)); s.NoteCats[account] = note.Cat; }
        // Being checked: the gold button says "Click to play" and is shown even while a map change hides its usual action.
        foreach (var (id, text) in HudView.PlayerTexts(s, account, h.Name, view.Returning, vote))
            screen.Text(id, h.Checking && id == "os_btn_act_text" ? HudCheck.ActText : h.Checking && id == "os_btn_act_sub" ? HudCheck.ActSub : text, slot);
        foreach (var (id, cls, on) in HudView.PlayerClasses(s, account, vote)) screen.Class(id, cls, on && !(h.Checking && cls == "act-off"), slot);
        foreach (var (id, text) in HudView.LatelyTexts(s, account, w.BridgeNowMs)) screen.Text(id, text, slot);
        foreach (var (id, cls, on) in HudView.LatelyClasses(s, account)) screen.Class(id, cls, on, slot);
        screen.Class(HudView.Screen, "vcheck", h.Version is "" or HudVersion.Unknown, slot); // the window's X is the version button
        screen.Class(HudView.Screen, "wback", view.Window && view.Returning, slot);           // the welcome page's line about the map that is on
        CatchUp(p, now, slot, screen);
        if (s.Why.TryGetValue(account, out var asked)) screen.Var(HudView.Screen, HudVote.WhyVar, asked.Map, slot); // the map the popup asks about: its label inherits it
        screen.Class(HudView.Screen, "win", view.Window, slot);
        screen.Class(HudView.Screen, "win-in", view.WindowIn, slot);
        screen.Class(HudView.Screen, "draining", view.Draining, slot);
        screen.Class(HudView.Screen, "pinned", view.Window && view.Pinned, slot); // the welcome closes only by its X: its foot has no idle bar
        screen.Class(HudView.Screen, "toast-1", view.Toast == 1, slot);
        screen.Class(HudView.Screen, "toast-in", view.ToastIn, slot);
        screen.Class(HudView.Screen, "vjoin", h.JoinedFor is { } joined && joined.TotalSeconds < HudVersion.JoinSeconds, slot); // a newer content's banner, for a while after a join
        var perPage = MapVote.PageCards;
        var vv = VoteView(p, vote, perPage);
        var browsing = vote is { Round: > 0, Open: true } && vv.Browse;
        if (vote.Round <= 0 || browsing) screen.Class(HudView.Screen, "voting", false, slot);
        screen.Class(HudView.Screen, "vbrowse", browsing, slot);       // MAPS, Browse: the map browser adds to the vote
        screen.Class(HudView.Screen, "vlock", w.VoteLocked, slot);     // a vote that freezes everybody has no close button
        if (browsing) screen.Var(HudView.Screen, HudVote.BrowseVar, HudVote.BrowseLine(vote), slot);
        if (vote.Round > 0) screen.Var(HudView.Screen, HudVote.TabVar, HudVote.TabLabel(vote), slot); // the Vote sub-tab's label
        screen.Var(HudView.Screen, HudView.SubCaptionVar, HudView.SubCaption(view.Page, vote.Round > 0 && !browsing, browsing), slot);
        foreach (var page in Enum.GetValues<HudSlots.Page>()) screen.Class(HudView.Screen, "page-" + page.ToString().ToLowerInvariant(), view.Window && view.Page == page, slot);
        if (view.Window)
        {
            Menu(w, h, p, view.Page, perPage, screen);
            var (words, classes) = HudDash.View(w.Dash, p.DashPages); // the Sync page's words and classes, for every open window: ready before its tab is clicked
            foreach (var (name, text) in words) screen.Var(HudView.Screen, name, text, slot);
            foreach (var (name, on) in classes) screen.Class(HudView.Screen, name, on, slot);
            foreach (var (name, words2) in funClicks.Words(slot, funWhy, now)) // the Fun page's bubbles, for every open window too: one player's refusal is not everybody's
            {
                screen.Var(HudView.Screen, name, words2, slot);
                screen.Class(HudMenu.FunRowOf(name), "clash", words2 != "", slot); // the reason is the row's second line while it has one
            }
            foreach (var (id, cls, on) in funClicks.Classes(slot, now)) screen.Class(id, cls, on, slot);
            foreach (var (name, words3) in botClicks.Words(slot, botWhy, now)) // the Bots page's: what this mode cannot make, or this player's refused click
                if (name != "")
                {
                    screen.Var(HudView.Screen, name, words3, slot);
                    screen.Class(HudBots.RowOf(name), "clash", words3 != "", slot);
                }
            foreach (var (id, cls, on) in botClicks.Classes(slot, now)) screen.Class(id, cls, on, slot);
        }
        // The window's cursor, or the board's while TAB is held. During a hold a player who joined has no HUD, so no window to close: nothing takes their mouse.
        screen.Capture(slot, (view.Capture || h.TabCursor) && (w.AddonRequired || h.KeptMenu));
    }

    /// <summary>The menu page a player has open, for that player only: the ballot while a vote is on (its map browser while it adds to the vote), the map browser,
    /// or the Game page. Everything else on the window is the same for all.</summary>
    static void Menu(World w, Human h, HudPlayer p, HudSlots.Page page, int perPage, IScreen screen)
    {
        var (vote, now, slot) = (w.Vote, w.Now, h.Slot);
        var ballot = page == HudSlots.Page.Maps && vote.Round > 0 && !(vote.Open && p.VoteView.Browse);
        var (texts, classes) = page switch
        {
            HudSlots.Page.Maps when ballot => BallotPage(w, h, p, perPage, screen),
            HudSlots.Page.Maps => HudMenu.Maps(w.Catalog, w.State, p.Browser, h.Account, now, w.Search, w.Mode.Id, vote),
            HudSlots.Page.Game => HudMenu.Game(w.State, HudPlayer.ArmedSeconds(p.RestartArmed, now), HudPlayer.ArmedSeconds(p.RematchArmed, now)),
            _ => (null, null),
        };
        if (texts == null || classes == null) return;
        foreach (var (id, text) in texts) screen.Text(id, text, slot);
        foreach (var (id, cls, on) in classes) screen.Class(id, cls, on, slot);
        if (page != HudSlots.Page.Maps) return;
        if (ballot) { screen.Pictures(slot, HudVote.Thumbs(vote, w.Catalog, p.VoteView.Page, perPage)); return; }
        p.Card.Show(p.Browser.WorkshopQuery(w.Catalog), now); // a typed Workshop id that no listed map has: its card, asked about once it holds still
        foreach (var (id, text) in p.Card.Texts()) screen.Text(id, text, slot);
        foreach (var (id, cls, on) in p.Card.Classes()) screen.Class(id, cls, on, slot);
        screen.Pictures(slot, HudMenu.Thumbs(w.Catalog, p.Browser, h.Account, w.Search));
    }

    /// <summary>The ballot's page for this player: its texts and classes, and its variables on os_screen (Random's button, the hint for the other pages, the
    /// header's clocks). The order of its cards is remembered as drawn: a re-sort keeps the order before it for HudVote.ReSortSeconds (SeenOrder).</summary>
    static (List<(string Id, string Text)>, List<(string Id, string Class, bool On)>) BallotPage(World w, Human h, HudPlayer p, int perPage, IScreen screen)
    {
        var vote = w.Vote;
        var page = p.VoteView.Page;
        var (texts, classes, vars) = HudVote.Cards(vote, h.Account, w.Catalog, page, perPage);
        foreach (var (name, text) in vars) screen.Var(HudView.Screen, name, text, h.Slot);
        var order = HudVote.Order(vote, page, perPage);
        if (p.Drawn.Round != vote.Round || p.Drawn.Page != page) p.Drawn = (vote.Round, page, order, order, DateTime.MinValue);
        else if (!p.Drawn.Now.SequenceEqual(order)) p.Drawn = (vote.Round, page, order, p.Drawn.Now, w.Now);
        return (texts, classes);
    }

    /// <summary>A player's view of the vote that is on: their page of the ballot, kept on the pages there are (perPage cards a page), and whether
    /// the map browser is open on it; a new round starts on its first page, on the ballot.</summary>
    public static (int Page, bool Browse) VoteView(HudPlayer p, VoteState vote, int perPage)
    {
        if (p.VoteView.Round != vote.Round) p.VoteView = (vote.Round, 0, false);
        var page = Math.Clamp(p.VoteView.Page, 0, HudVote.Pages(vote, perPage) - 1);
        if (page != p.VoteView.Page) p.VoteView = (p.VoteView.Round, page, p.VoteView.Browse);
        return (p.VoteView.Page, p.VoteView.Browse);
    }

    /// <summary>The order of a player's ballot cards a click counts against: the one drawn before the last re-sort while that re-sort is younger than
    /// HudVote.ReSortSeconds, else the one drawn last; null when nothing of this round and page was drawn for them (the newest order then).</summary>
    public static int[]? SeenOrder(HudPlayer p, long round, int page, DateTime now) =>
        p.Drawn.Round == round && p.Drawn.Page == page && p.Drawn.Now.Length > 0 ? ((now - p.Drawn.ChangedAt).TotalSeconds < HudVote.ReSortSeconds ? p.Drawn.Before : p.Drawn.Now) : null;

    /// <summary>The Fun page, the same for everybody: as the live values have it - the settings, who set them, what is grey. Returns what its bubbles say this
    /// frame.</summary>
    static List<(string Name, string Text)> FunPage(World w, FunClicks funClicks, IScreen screen)
    {
        var (texts, classes) = HudMenu.FunPage(w.FunValue, w.Mode.ArmsRace, w.FunSetBy);
        foreach (var (id, text) in texts) screen.Text(id, text);
        foreach (var (id, cls, on) in classes)
        {
            screen.Class(id, cls, on);
            if (cls == "off") funClicks.Grey(id, on, w.Now);
        }
        return HudMenu.FunWhy(w.FunValue);
    }

    /// <summary>The Bots page, the same for everybody: as the live values have it in the mode that is on - the rows, who set them, what the mode greys, the presets, and
    /// Kick all bots greyed while no bot plays. Returns what its rows' second lines say this frame.</summary>
    static List<(string Name, string Text)> BotsPage(World w, FunClicks botClicks, IScreen screen)
    {
        var (texts, classes) = HudBots.Page(w.FunValue, w.Mode, w.FunSetBy);
        foreach (var (id, text) in texts) screen.Text(id, text);
        foreach (var (id, cls, on) in classes)
        {
            screen.Class(id, cls, on);
            if (cls == "off") botClicks.Grey(id, on, w.Now);
        }
        return HudBots.Why(w.FunValue, w.Mode);
    }

    /// <summary>Catch-up's card under the crosshair for this player: a row per item, green for a boost and red for a bounty, shown in two steps (HudSlots.FadeSeconds).</summary>
    static void CatchUp(HudPlayer p, DateTime now, int slot, IScreen screen)
    {
        var on = p.CatchUp is { } c && now < c.Until.AddSeconds(HudSlots.FadeSeconds);
        if (p.CatchUp != null && !on) p.CatchUp = null;
        var card = p.CatchUp ?? ("", Array.Empty<string>(), false, DateTime.MinValue, DateTime.MinValue);
        var shown = on && now >= card.From.AddSeconds(HudSlots.FadeSeconds) && now < card.Until;
        screen.Class(HudView.Screen, "cu", on, slot);
        screen.Class(HudView.Screen, "cu-in", shown, slot);
        screen.Class(HudView.Screen, "cu-run", shown, slot);
        screen.Text("os_cu_head", on ? card.Head : "", slot);
        for (var k = 1; k <= CatchUpRows; k++)
        {
            var row = on && k <= card.Rows.Length ? card.Rows[k - 1] : "";
            screen.Text($"os_cu_{k}", row, slot);
            screen.Class($"os_cu_{k}", "used", row != "", slot);
            screen.Class($"os_cu_{k}", "good", row != "" && !card.Bad, slot);
            screen.Class($"os_cu_{k}", "bad", row != "" && card.Bad, slot);
        }
    }

    /// <summary>The rows catch-up's card has.</summary>
    public const int CatchUpRows = 6;

    /// <summary>A boost's items as the card's rows: one an item while they fit, else the last ones two to a row ("A second chance  ·  +3 s spawn protection"),
    /// so no item of up to twelve is lost - a 7+ boost has nine, its infinite ammo and one-hit knife among the last. A row holds about 50 characters (14 px
    /// on the card's 440 px).</summary>
    public static string[] CatchUpRowsOf(IReadOnlyList<string> items)
    {
        var doubles = Math.Clamp(items.Count - CatchUpRows, 0, CatchUpRows);
        var singles = Math.Min(items.Count, CatchUpRows - doubles);
        var rows = items.Take(singles).ToList();
        for (var i = singles; i < items.Count; i += 2) rows.Add(string.Join("  ·  ", items.Skip(i).Take(i == items.Count - 1 ? 1 : 2)));
        return [.. rows];
    }

    /// <summary>The Players page's rows (SERVER, Players): every human on the server, by name - their team, the HUD their game showed (the check's click), the
    /// check's clock while it waits for that click, their vote while one is open, and how long they have been on since their last real join.</summary>
    static List<HudView.PlayerRow> PlayerRows(IReadOnlyList<Human> humans, VoteState vote) =>
        humans.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).Select(h => new HudView.PlayerRow(h.Name, h.Team, h.Version, h.Checking ? h.CheckSeconds : 0,
            vote is { Open: true, Round: > 0 } ? vote.ChoiceOf(h.Account) > 0 ? "voted" : "waiting" : "", HudView.OnFor(h.JoinedFor))).ToList();

    /// <summary>The picture of the map with this key: its two classes, none for no key or a map without one.</summary>
    static string[] ThumbOf(HudCatalog? catalog, string key) => key != "" && catalog?.Find(key) is { } entry ? HudThumbs.Classes(entry) : [];
}
