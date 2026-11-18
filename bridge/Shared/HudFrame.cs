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
        bool TabCursor = false, bool KeptMenu = false, IReadOnlyList<string>? Recent = null, bool TabHeld = false);
    // Recent: this player's last searches, newest first (HudBrowser.Remember): the search's words row shows them. TabHeld: TAB is down as the server reads
    // their buttons (HudTab.Holding) - the vote's scoreboard peek (vpeek).

    /// <summary>What the frame draws from: the live state as of this frame (its countdowns worked out) and the vote; the map list, the search index and the Sync
    /// page's block; the mode the map that is on was loaded in; TAB's mode; the content level the version classes say (os_hud level fakes it); whether a vote
    /// holds everybody's window (its freeze) and whether joiners get the HUD (no hold); the Fun page's values and who set them; now, by the plugin's clock and
    /// the bridge's.</summary>
    public sealed record World(HudState State, VoteState Vote, HudCatalog? Catalog, HudSearch Search, HudDash? Dash, Modes.Mode Mode, HudTab.Mode TabMode, int Level,
        bool VoteLocked, bool AddonRequired, Func<string, string?> FunValue, Func<string, string?>? FunSetBy, DateTime Now, long BridgeNowMs,
        VoteState? BarVote = null, HudState? BarState = null, HudChat? Chat = null, bool ChatLive = false, bool MatchScreen = false, HudVote.Download? Download = null,
        HudChat.Route ChatRoute = HudChat.Default, bool ChatLift = false);
    // Chat, ChatLive: the chat column's lines and whether its feed is live - the plugin's frames run and the bridge answers (content v4.14.0, HudChat); MatchScreen:
    // the match-end screen is up (the game's phase); ChatLift: CS2 has lifted its chat for it - the phase ended a while ago, as CS2 lifts it only once its screen
    // shows the scoreboard - so with CS2's chat open the column's foot follows its typing line up (meos); Download: a winner's download, as the watcher's line says it.
    // BarVote, BarState: the vote and the state as of HudVote.BarLeadMs from now, for the countdowns' bars alone (their words stay as of now); null: as of now.

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
        var (prevTexts, prevClasses) = HudMenu.PreviousButton(s, w.BarState); // the Maps page's Previous: its countdown's line a step ahead
        foreach (var (id, text) in prevTexts) screen.Text(id, text);
        foreach (var (id, cls, on) in prevClasses) screen.Class(id, cls, on);
        screen.Pictures(Everybody, [("os_t_thumb", ThumbOf(w.Catalog, s.Key)), ("os_k_thumb", ThumbOf(w.Catalog, s.ChangeKey))]); // the now-playing card's and the countdown's
        screen.Text("os_rst_secs", HudView.RestartCountdown(s, now)); // a restart coming: its countdown from its end time, by our own clock
        var (popularTexts, popularClasses) = HudMenu.PopularWords(PopularOf(w.Catalog)); // the search's words row: the words most map names share, the same for everybody
        foreach (var (id, text) in popularTexts) screen.Text(id, text);
        foreach (var (id, cls, on) in popularClasses) screen.Class(id, cls, on);

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
        screen.Class(HudView.TabGame, "off", w.MatchScreen);  // a greyed tab (content v4.14.0): the game takes no restart while the match-end screen is up
        screen.Class(HudDash.Tab, "off", w.Dash == null);      // ...and no Sync page while the server has no sync's block
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
        ChatColumn(w, h, screen, view.Window);
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
        screen.Class(HudView.Screen, "qhelp", view.Window && view.Page == HudSlots.Page.Maps && now < p.SearchHelpUntil && !OnBallot(p, vote), slot); // the search helped, after a click on its area
        var perPage = MapVote.PageCards;
        var vv = VoteView(p, vote, perPage);
        var browsing = vote is { Round: > 0, Open: true } && vv.Browse;
        if (vote.Round <= 0 || browsing) screen.Class(HudView.Screen, "voting", false, slot);
        if (vote.Round <= 0 || browsing) { screen.Class(HudView.Screen, "vall", false, slot); screen.Class(HudView.Screen, "vnum", false, slot); screen.Class(HudView.Screen, "vfetch", false, slot); } // the ballot's own (HudVote.Cards)
        screen.Class(HudView.Screen, "vbrowse", browsing, slot);       // MAPS, Browse: the map browser adds to the vote
        screen.Class(HudView.Screen, "vlock", w.VoteLocked, slot);     // a vote that freezes everybody has no close button
        // The scoreboard's peek (the owner, 2026-11-17): once this player has voted, or the vote is decided, holding TAB steps the vote's window aside for CS2's
        // scoreboard, and letting go brings it back. Only classes: the window stays open and keeps the mouse, the player stays frozen. vpeekok - they may peek
        // now, TAB or not (for a stylesheet that also goes by CS2's own HUD_SCOREBOARD_VISIBLE); vpeek - they may, and TAB is down as the server reads it, or
        // their SCORES look is on. At the match-end screen no key reaches the server, so there the ballot's SCORES button does it (HudScores, the owner's 292):
        // vscoresok - the button may show (meos is on, they may peek, no look on); vscores - the look is on, Back to the vote shows.
        // TAB counts off the match-end screen alone: there CS2 has taken the pawn the server reads it from, so a TAB down as the match ended - people hold it to
        // see the scores - would read as held all through the screen, its TAB-up never reaching the server, and keep the window aside with no way back.
        var peekOk = view.Window && Peekable(vote, account);
        var scores = view.Window && HudScores.On(p, vote, w.ChatLift);
        screen.Class(HudView.Screen, "vpeekok", peekOk, slot);
        screen.Class(HudView.Screen, "vpeek", peekOk && ((h.TabHeld && !w.MatchScreen) || scores), slot);
        screen.Class(HudView.Screen, "vscoresok", HudScores.Allowed(view.Window, w.ChatLift, vote, account) && !scores, slot);
        screen.Class(HudView.Screen, "vscores", scores, slot);
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

    /// <summary>Whether a player may peek at the scoreboard past the vote's window: a vote is on, and they have voted - Random too - or it is decided.</summary>
    public static bool Peekable(VoteState vote, long account) => vote.Round > 0 && (!vote.Open || vote.ChoiceOf(account) > 0);

    /// <summary>The menu page a player has open, for that player only: the ballot while a vote is on (its map browser while it adds to the vote), the map browser,
    /// or the Game page. Everything else on the window is the same for all.</summary>
    static void Menu(World w, Human h, HudPlayer p, HudSlots.Page page, int perPage, IScreen screen)
    {
        var (vote, now, slot) = (w.Vote, w.Now, h.Slot);
        var ballot = page == HudSlots.Page.Maps && OnBallot(p, vote);
        var (texts, classes) = page switch
        {
            HudSlots.Page.Maps when ballot => BallotPage(w, h, p, perPage, screen),
            HudSlots.Page.Maps => HudMenu.Maps(w.Catalog, w.State, p.Browser, h.Account, now, w.Search, w.Mode.Id, vote, w.BarState),
            HudSlots.Page.Game => HudMenu.Game(w.State, HudPlayer.ArmedSeconds(p.RestartArmed, now), HudPlayer.ArmedSeconds(p.RematchArmed, now), w.BarState),
            _ => (null, null),
        };
        if (texts == null || classes == null) return;
        var numbers = !HudVersion.IsBefore(h.Version, HudVote.NumbersLevel); // the cards' numbers: not to a game known to hold a content without their labels
        foreach (var (id, text) in texts) if (numbers || !HudVote.IsNumber(id)) screen.Text(id, text, slot);
        foreach (var (id, cls, on) in classes) screen.Class(id, cls, on, slot);
        if (page != HudSlots.Page.Maps) return;
        if (ballot) { screen.Pictures(slot, HudVote.Thumbs(vote, w.Catalog, p.VoteView.Page, perPage, h.Account)); return; }
        var (recentTexts, recentClasses) = HudMenu.RecentWords(h.Recent ?? []); // the search's words row: this player's last searches
        foreach (var (id, text) in recentTexts) screen.Text(id, text, slot);
        foreach (var (id, cls, on) in recentClasses) screen.Class(id, cls, on, slot);
        p.Card.Show(p.Browser.WorkshopQuery(w.Catalog), now); // a typed Workshop id that no listed map has: its card, asked about once it holds still
        foreach (var (id, text) in p.Card.Texts()) screen.Text(id, text, slot);
        foreach (var (id, cls, on) in p.Card.Classes()) screen.Class(id, cls, on, slot);
        screen.Pictures(slot, HudMenu.Thumbs(w.Catalog, p.Browser, h.Account, w.Search));
    }

    /// <summary>The ballot's page for this player, in their own view (their card first, then the most votes - HudVote.Cards): its texts and classes, and its
    /// variables on os_screen (Random's button, the hint for the other pages, the header's clocks).</summary>
    static (List<(string Id, string Text)>, List<(string Id, string Class, bool On)>) BallotPage(World w, Human h, HudPlayer p, int perPage, IScreen screen)
    {
        var (texts, classes, vars) = HudVote.Cards(w.Vote, h.Account, w.Catalog, p.VoteView.Page, perPage, w.BarVote, w.Download);
        foreach (var (name, text) in vars) screen.Var(HudView.Screen, name, text, h.Slot);
        return (texts, Snapped(classes, HudVote.IdleBar, p));
    }

    /// <summary>The chat column for one player (content v4.14.0, HudChat): the newest lines they may see, the newest at the bottom, and on os_screen whether it
    /// shows (chaton: its feed is live), its beat (hb, on and off by turns every HudChat.BeatSeconds, which keeps the stylesheet's animation from hiding it) and
    /// CS2's chat lifted for the match-end screen (meos: with it open the column's foot follows its typing line up). Only a game whose HUD check named v4.14.0 or later gets the column's words
    /// (HudChat.Feeds) - any other the classes on os_screen alone, which every content has. The column shows empty too: after a plugin reload it has no line until the
    /// next one is said, and the window should not stand without it (the lab's round 5 of release 218: it did, and CS2's own chat showed beside the window).</summary>
    static void ChatColumn(World w, Human h, IScreen screen, bool windowOpen)
    {
        var slot = h.Slot;
        var fed = w.Chat != null && HudChat.Feeds(h.Version) && w.ChatRoute != HudChat.Route.Never;
        var shown = fed ? w.Chat!.For(h.Account, h.Team) : [];
        screen.Class(HudView.Screen, "chaton", fed && w.ChatLive && HudChat.Shows(w.ChatRoute, windowOpen), slot); // empty too - after a plugin reload, before its first line - so the window always has its column
        screen.Class(HudView.Screen, "hb", HudChat.BeatA(w.Now), slot);
        screen.Class(HudView.Screen, "chatwin", w.ChatRoute == HudChat.Route.Window, slot); // the column comes and goes with the window: it fades with it
        screen.Class(HudView.Screen, "meos", w.ChatLift, slot);
        if (!fed) return;
        var (texts, classes) = HudChat.Column(shown);
        foreach (var (id, text) in texts) screen.Text(id, text, slot);
        foreach (var (id, cls, on) in classes) screen.Class(id, cls, on, slot);
    }

    /// <summary>A bar that steps up snaps there (content v4.14.0): Panorama takes a transition's length from the state it leaves, so the inactivity timer's bar, reset by a
    /// moved vote, climbed back at its countdown's 1 s a step and fell short before the next step took it. Now when the bar's step goes up, it first gets snap (0 s)
    /// for one frame and keeps its step; the next frame takes the new step from that 0 s state - at once - and the one after drops snap. Only the countdown moves.</summary>
    public static List<(string Id, string Class, bool On)> Snapped(List<(string Id, string Class, bool On)> classes, string bar, HudPlayer p)
    {
        var want = classes.FirstOrDefault(x => x.Id == bar && x.On && Step(x.Class) >= 0).Class;
        if (want == null) return classes;
        var (shown, stage) = p.Snap.TryGetValue(bar, out var was) ? was : (want, 0);
        string show;
        bool snap;
        if (stage == 1) (show, snap, p.Snap[bar]) = (want, true, (want, 2));                  // the new step, from the 0 s state: at once
        else if (Step(want) > Step(shown)) (show, snap, p.Snap[bar]) = (shown, true, (shown, 1)); // up: snap first, the step as it was
        else (show, snap, p.Snap[bar]) = (want, false, (want, 0));                              // down, or the frame after the snap: as it comes
        var result = classes.Where(x => !(x.Id == bar && Step(x.Class) >= 0)).ToList();
        for (var k = 0; k <= 10; k++) result.Add((bar, $"w{k}", $"w{k}" == show));
        result.Add((bar, "snap", snap));
        return result;
    }

    /// <summary>A step class's number, w0 ... w10; -1 for any other class.</summary>
    static int Step(string cls) => cls.Length is 2 or 3 && cls[0] == 'w' && int.TryParse(cls[1..], out var k) && k is >= 0 and <= 10 ? k : -1;

    static HudCatalog? popularFor;
    static List<string> popular = [];

    /// <summary>The search's popular words for this map list (HudSearch.Popular), worked out once a list: the frame shows them, and a click on one searches the same word.</summary>
    public static IReadOnlyList<string> PopularOf(HudCatalog? catalog)
    {
        if (catalog == null) return [];
        if (!ReferenceEquals(catalog, popularFor)) (popular, popularFor) = (HudSearch.Popular(catalog.Maps.Select(m => m.Name), HudBrowser.PopularButtons.Length), catalog);
        return popular;
    }

    /// <summary>Whether MAPS shows this player the ballot rather than the map browser: a vote is on, and they are not on its Browse - which VoteView keeps to its round.</summary>
    public static bool OnBallot(HudPlayer p, VoteState vote) => vote.Round > 0 && !(vote.Open && VoteView(p, vote, MapVote.PageCards).Browse);

    /// <summary>Whether this player's window is on MAPS > Browse now - open, on MAPS, and the browser there, not the ballot - in a game that shows the HUD (showsHud: the rule of the
    /// catch-up card, HudVote.CatchUpCard). A chat line of theirs that is no command searches it then (ChatCommands.SearchOf, 233); on any other page, or the ballot, chat is chat.</summary>
    public static bool BrowserOpen(bool windowOpen, HudSlots.Page page, HudPlayer p, VoteState vote, bool showsHud) =>
        showsHud && windowOpen && page == HudSlots.Page.Maps && !OnBallot(p, vote);

    /// <summary>A player's view of the vote that is on: their page of the ballot, kept on the pages there are (perPage cards a page), and whether
    /// the map browser is open on it; a new round starts on its first page, on the ballot.</summary>
    public static (int Page, bool Browse) VoteView(HudPlayer p, VoteState vote, int perPage)
    {
        if (p.VoteView.Round != vote.Round) p.VoteView = (vote.Round, 0, false);
        var page = Math.Clamp(p.VoteView.Page, 0, HudVote.Pages(vote, perPage) - 1);
        if (page != p.VoteView.Page) p.VoteView = (p.VoteView.Round, page, p.VoteView.Browse);
        return (p.VoteView.Page, p.VoteView.Browse);
    }

    /// <summary>A map browser's Add to the vote, from this player, by the Play of button: back to the ballot's first page, where the card their vote is on comes
    /// first (VoteState.ForPlayer) - the map they added, once the bridge's answer comes; the button and the moment kept for the second click of a double click
    /// (SecondClick).</summary>
    public static void AddedToVote(HudPlayer p, VoteState vote, DateTime now, string button = "")
    {
        p.VoteView = (vote.Round, 0, false);
        p.Added = (vote.Round, button, now);
    }

    /// <summary>Whether a click on the ballot is the second click of a double click on the map browser's Play that added a map a moment ago: the same button, within
    /// HudVote.DoubleClickSeconds of the click before it - each such click is noted, so a burst counts once, like HudBrowser.DoubleClick. The Add turned that card
    /// into the ballot's card in its place, before the bridge's answer put the map there - so the click would count for whatever stood there, and take the
    /// player's vote off the map they just added. It counts for nothing; the Add's own answer speaks.</summary>
    public static bool SecondClick(HudPlayer p, VoteState vote, string buttonId, DateTime now)
    {
        if (!(p.Added.Round == vote.Round && vote.Round > 0 && p.Added.Button != "" && p.Added.Button == buttonId && now >= p.Added.At
            && (now - p.Added.At).TotalSeconds < HudVote.DoubleClickSeconds)) return false;
        p.Added = (p.Added.Round, p.Added.Button, now);
        return true;
    }

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

    /// <summary>The card's rows as the chat line's items: a row that ends in a colon - catch-up's "Against players ahead of you, up to:" - runs on into
    /// the next row with a space, the rest apart, so no separator follows the colon.</summary>
    public static string CatchUpLine(IReadOnlyList<string> rows)
    {
        var parts = new List<string>();
        foreach (var row in rows)
            if (parts.Count > 0 && parts[^1].EndsWith(':')) parts[^1] += " " + row;
            else parts.Add(row);
        return string.Join("  ·  ", parts);
    }

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
