// --selftest, the HUD's frame (HudFrame.cs): what the plugin draws four times a second, for everybody and for each player, run here as the plugin runs it -
// against a screen that records what it is sent, with the slots' records (HudPlayer) and the window's state (HudSlots) the plugin keeps.
static partial class Program
{
    /// <summary>A screen that keeps the last of everything a frame sent it, by slot (-1: everybody).</summary>
    sealed class RecordedScreen : HudFrame.IScreen
    {
        readonly Dictionary<(int, string), string> texts = new();
        readonly Dictionary<(int, string, string), bool> classes = new();
        readonly Dictionary<(int, string, string), string> vars = new();
        public readonly Dictionary<(int Slot, string Panel), string[]> Pictured = new();
        public readonly Dictionary<int, bool> Captured = new();
        public void Text(string id, string text, int slot = HudFrame.Everybody) => texts[(slot, id)] = text;
        public bool Class(string id, string cls, bool on, int slot = HudFrame.Everybody) { classes[(slot, id, cls)] = on; return true; }
        public void Var(string id, string name, string text, int slot = HudFrame.Everybody) => vars[(slot, id, name)] = text;
        public void Pictures(int slot, List<(string Panel, string[] Classes)> wanted) { foreach (var (panel, cls) in wanted) Pictured[(slot, panel)] = cls; }
        public void Capture(int slot, bool on) => Captured[slot] = on;
        public string T(string id, int slot = HudFrame.Everybody) => texts.GetValueOrDefault((slot, id), "");
        public bool C(string id, string cls, int slot = HudFrame.Everybody) => classes.GetValueOrDefault((slot, id, cls));
        public bool Sent(string id, string cls, int slot = HudFrame.Everybody) => classes.ContainsKey((slot, id, cls));
        public string V(string id, string name, int slot = HudFrame.Everybody) => vars.GetValueOrDefault((slot, id, name), "");

        /// <summary>Every panel anything went to.</summary>
        public HashSet<string> Ids => [.. texts.Keys.Select(k => k.Item2), .. classes.Keys.Select(k => k.Item2), .. vars.Keys.Select(k => k.Item2), .. Pictured.Keys.Select(k => k.Panel)];

        /// <summary>Every panel anything went to for one slot - Everybody (-1) for the state every game reads.</summary>
        public HashSet<string> IdsOf(int slot) => [.. texts.Keys.Where(k => k.Item1 == slot).Select(k => k.Item2), .. classes.Keys.Where(k => k.Item1 == slot).Select(k => k.Item2),
            .. vars.Keys.Where(k => k.Item1 == slot).Select(k => k.Item2), .. Pictured.Keys.Where(k => k.Slot == slot).Select(k => k.Panel)];
    }

    static void FrameSelfTest(Action<string, bool> Check)
    {
        var t0 = new DateTime(2026, 11, 7, 20, 0, 0, DateTimeKind.Utc);
        var slots = new HudSlots();
        var players = new Dictionary<int, HudPlayer>();
        HudPlayer PlayerOf(int slot) => players.TryGetValue(slot, out var p) ? p : players[slot] = new HudPlayer();
        var funClicks = new FunClicks();
        var frame = new HudFrame();
        HudFrame.World World(HudState s, VoteState v, DateTime now, bool locked = false, bool addon = true, int? level = null) =>
            new(s, v, null, new HudSearch(), null, Modes.ArmsRace, HudTab.Mode.Down, level ?? HudVersion.Level, locked, addon, _ => null, null, now, HudState.Ms(now));
        HudFrame.Human Alice(TimeSpan? on = null, bool checking = false, string version = "", bool kept = false) =>
            new(1, 1001, "Alice", "CT", version == "" ? HudVersion.Content : version, checking, checking ? 40 : 0, on, false, kept);
        var live = new HudState { Map = "#12 aim_x", By = "the vote", Key = "3084291312", PrevArg = "11", PrevLabel = "#11 aim_y", Maps = 3 };

        // ---- the vote reaches every view: the wait after one, the board's and the window's Start a vote ----
        slots.Joined(1, first: false, alive: true, t0);
        var waiting = new VoteState { WaitFor = 80 };
        var screen = new RecordedScreen();
        frame.Draw(World(live, waiting, t0.AddSeconds(2)), [Alice(TimeSpan.FromSeconds(5))], slots, PlayerOf, funClicks, screen);
        Check("Frame: the wait after a vote reaches every view - the board's line, its Start a vote and the window's say when the next can start, and look off; the Previous button is fed",
            screen.T("os_b_queue").StartsWith("The next vote can start in", StringComparison.Ordinal) && screen.T("os_b_next_sub", 1).StartsWith("The next one can start in", StringComparison.Ordinal)
            && screen.T("os_btn_act_sub", 1).StartsWith("The next one can start in", StringComparison.Ordinal) && screen.C(HudView.Screen, "act-off", 1)
            && screen.T("os_q_prev_text") == "Previous: #11 aim_y" && !screen.C(HudMenu.Previous, "off") && screen.T("os_w_hello", 1) == "Welcome back, Alice");
        Check("Frame: a real join's window is the welcome, pinned until its X, and takes the mouse; the join banner of v4.0.0 to v4.5.0 shows its first seconds",
            screen.C(HudView.Screen, "win", 1) && screen.C(HudView.Screen, "pinned", 1) && screen.C(HudView.Screen, "page-start", 1) && screen.Captured[1] && screen.C(HudView.Screen, "vjoin", 1)
            && screen.Pictured.ContainsKey((HudFrame.Everybody, "os_t_thumb")));
        frame.Draw(World(live, waiting, t0.AddSeconds(30)), [Alice(TimeSpan.FromMinutes(42))], slots, PlayerOf, funClicks, screen);
        Check("Frame: the Players page's time on and the join banner come from the player's own last real join (JoinTimes, by account): 42 min on, the banner gone",
            screen.V(HudView.Global, $"{HudView.PlayersVar}1e") == "42 min" && screen.V(HudView.Global, $"{HudView.PlayersVar}1a") == "Alice" && !screen.C(HudView.Screen, "vjoin", 1));
        frame.Draw(World(live, waiting, t0.AddSeconds(31)), [Alice()], slots, PlayerOf, funClicks, screen);
        Check("Frame: a player whose real join the plugin has not seen has no time on, and no banner", screen.V(HudView.Global, $"{HudView.PlayersVar}1e") == "-" && !screen.C(HudView.Screen, "vjoin", 1));

        // ---- the HUD check: the gold button says Click to play, even while a change hides its usual action ----
        var changing = new HudState { Map = live.Map, ChangeIn = 5, ChangeMap = "#13 aim_z", ChangeBy = "the dice" };
        frame.Draw(World(changing, new VoteState(), t0.AddSeconds(32)), [Alice(checking: true, version: HudVersion.Unknown)], slots, PlayerOf, funClicks, screen);
        Check("Frame: a player the check waits for gets Click to play on the gold button, shown during a map change too; the window's X is the version button while the HUD's content is not known",
            screen.T("os_btn_act_text", 1) == HudCheck.ActText && screen.T("os_btn_act_sub", 1) == HudCheck.ActSub && !screen.C(HudView.Screen, "act-off", 1) && screen.C(HudView.Screen, "vcheck", 1)
            && screen.V(HudView.Global, $"{HudView.PlayersVar}1k") == "HUD check: 40 s left");

        // ---- a vote that freezes everybody opens every window on the ballot and holds it; its end closes what it opened ----
        var open = VoteState.Decode("mode\tarmsrace\tArms Race\nmv\t1793900000\topen\t25\t2\t1\tmid\t1001\t1\n"
            + "mvo\t1\tarmsrace\tpicked\t12\t3084291312\t#12 aim_x\t17 MB\t1\tAlice\t\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t0\t\nmvv\t1001\t1\n");
        slots.Joined(2, first: true, alive: true, t0);
        slots.Frame(t0.AddSeconds(2));
        slots.Click(2, HudView.ButtonClose, t0.AddSeconds(3)); // Bob closed his welcome: no window
        HudFrame.Human Bob() => new(2, 1002, "Bob", "T", "v4.5.0", false, 0, TimeSpan.FromMinutes(5));
        frame.Draw(World(live, open, t0.AddSeconds(40), locked: true), [Alice(), Bob()], slots, PlayerOf, funClicks, screen);
        Check("Frame: a vote that freezes everybody opens every window on MAPS, the ballot, held and with no close button",
            slots.WindowOpen(1) && slots.WindowOpen(2) && slots.PageOf(2) == HudSlots.Page.Maps && players[2].VoteOpened && screen.C(HudView.Screen, "vlock", 2) && screen.C(HudView.Screen, "voting", 2)
            && screen.T("os_m1_name", 2) == "#12 aim_x" && screen.V(HudView.Global, $"{HudView.PlayersVar}2d") == "waiting" && screen.V(HudView.Global, $"{HudView.PlayersVar}1d") == "voted"
            && players[2].VoteView.Round == 1793900000);
        // the scoreboard's peek (the owner, 2026-11-17): Alice has voted (mvv 1001 1), Bob has not
        frame.Draw(World(live, open, t0.AddSeconds(41), locked: true), [Alice() with { TabHeld = true }, Bob() with { TabHeld = true }], slots, PlayerOf, funClicks, screen);
        var peekVoted = screen.C(HudView.Screen, "vpeek", 1) && screen.C(HudView.Screen, "vpeekok", 1) && !screen.C(HudView.Screen, "vpeek", 2)
            && !screen.C(HudView.Screen, "vpeekok", 2) && slots.WindowOpen(1) && slots.WindowOpen(2) && screen.Captured[1] && screen.Captured[2];
        frame.Draw(World(live, open, t0.AddSeconds(42), locked: true), [Alice(), Bob()], slots, PlayerOf, funClicks, screen);
        var peekLetGo = !screen.C(HudView.Screen, "vpeek", 1) && screen.C(HudView.Screen, "vpeekok", 1) && slots.WindowOpen(1) && screen.Captured[1];
        var shut = VoteState.Decode(open.Encode());
        shut.Open = false;
        frame.Draw(World(live, shut, t0.AddSeconds(43), locked: true), [Alice(), Bob() with { TabHeld = true }], slots, PlayerOf, funClicks, screen);
        Check("Frame: the scoreboard's peek - once she has voted, TAB held steps her window aside (vpeek), never his before he votes; letting go brings it back; a decided "
            + "vote lets everybody peek; the window stays open, frozen and with the mouse, throughout - only classes change",
            peekVoted && peekLetGo && screen.C(HudView.Screen, "vpeek", 2) && screen.C(HudView.Screen, "vpeekok", 2) && !screen.C(HudView.Screen, "vpeek", 1)
            && slots.WindowOpen(2) && screen.Captured[2] && screen.C(HudView.Screen, "vlock", 2)
            && HudFrame.Peekable(shut, 1002) && !HudFrame.Peekable(open, 1002) && HudFrame.Peekable(open, 1001) && !HudFrame.Peekable(new VoteState(), 1001));
        frame.Draw(World(live, new VoteState { WaitFor = 120 }, t0.AddSeconds(50)), [Alice(), Bob()], slots, PlayerOf, funClicks, screen);
        Check("Frame: when the freeze ends, the windows it opened close together - but the welcome, which goes back to its start page and waits for its X",
            !slots.WindowOpen(2) && !players[2].VoteOpened && slots.WindowOpen(1) && slots.PageOf(1) == HudSlots.Page.Start);

        // ---- the ballot's clicks: her own card first in her view, the rest by votes; a vote moves, never goes back ----
        const long r = 1793950000;
        VoteState BallotOf(IEnumerable<int> cards, Dictionary<long, int>? choices = null)
        {
            var v = new VoteState { Round = r, Open = true, SecondsLeft = 30, Present = 2 };
            foreach (var n in cards) v.Options.Add(new VoteState.Option(n, "armsrace", VoteSource.Rolled, $"30700000{n:00}", $"map {n}", "", 0, ""));
            v.Options.Add(new VoteState.Option(MapVote.RandomOption, "armsrace", VoteSource.Random, "", "Random", "", 0, ""));
            foreach (var (a, n) in choices ?? new()) v.Choices[a] = n;
            return v;
        }
        var twelve = BallotOf(Enumerable.Range(1, 12), new() { [1001] = 12, [1002] = 3 }); // two pages, the bridge's order; her vote on card 12, the second page's
        var hers = twelve.ForPlayer(1001);
        var decided = BallotOf(Enumerable.Range(1, 12), new() { [1001] = 12 });
        decided.Open = false;
        var onRandom = BallotOf(Enumerable.Range(1, 12), new() { [1001] = MapVote.RandomOption });
        Check("Ballot: in her own view the card her vote is on comes first, the rest in the bridge's order (the most votes first); Random's vote, no vote, a decided vote and another player's view as they are",
            hers.Maps.Select(o => o.N).SequenceEqual([12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]) && hers.Options[^1].Source == VoteSource.Random && twelve.Maps[0].N == 1
            && twelve.ForPlayer(1002).Maps.Select(o => o.N).SequenceEqual([3, 1, 2, 4, 5, 6, 7, 8, 9, 10, 11, 12]) && ReferenceEquals(twelve.ForPlayer(1003), twelve)
            && ReferenceEquals(twelve.ForPlayer(-1), twelve) && ReferenceEquals(decided.ForPlayer(1001), decided) && ReferenceEquals(onRandom.ForPlayer(1001), onRandom)
            && HudVote.Order(hers, 0)[0] == 12 && HudVote.PageOf(hers, 12) == 0 && HudVote.PageOf(twelve, 12) == 1);
        Check("...a click counts for the card in her view: her own card (first) does nothing - a vote is never taken back - another card moves it, Random too; another player's click on card 1 is his own card's, and nothing",
            HudVote.Click(twelve, "os_m1_play", 0, 1001) == null && HudVote.Click(twelve, "os_m2_play", 0, 1001) == MapVote.Click(r, 1) && HudVote.Click(twelve, "os_m3_play", 1, 1001) == null
            && HudVote.Click(twelve, "os_m1_play", 1, 1001) == MapVote.Click(r, 10) && HudVote.Click(twelve, "os_m2_play", 1, 1001) == MapVote.Click(r, 11) && HudVote.Click(twelve, HudVote.RandomButton, 0, 1001) == MapVote.Click(r, MapVote.RandomOption)
            && HudVote.Click(onRandom, HudVote.RandomButton, 0, 1001) == null && HudVote.Click(onRandom, "os_m1_play", 0, 1001) == MapVote.Click(r, 1)
            && HudVote.Click(twelve, "os_m1_play", 0, 1002) == null && HudVote.Click(twelve, "os_m4_play", 0, 1002) == MapVote.Click(r, 4)
            && HudVote.Click(twelve, "os_m1_play", 0) == MapVote.Click(r, 1) && HudVote.Click(decided, "os_m1_play", 0, 1003) == null
            && HudVote.CardOf("os_m12_play") == 12 && HudVote.CardOf("os_m13_play") == 0 && HudVote.Click(twelve, "os_m11_play", 0, 1001) == null && HudVote.Click(twelve, "os_m12_play", 0, 1002) == null
            && HudVote.CardOf("os_m1_up") == 0 && HudVote.CardOf(HudVote.RandomButton) == 0 && HudVote.CardOf("os_m_play") == 0);
        var cards = HudVote.Cards(twelve, 1001, null);
        Check("...the cards drawn in her view: card 1 is her vote's (Your vote, lit), the rest by the bridge's order; the hint says her vote comes first and moves, never that it goes back",
            cards.Texts.Single(x => x.Id == "os_m1_name").Text == "map 12" && cards.Texts.Single(x => x.Id == "os_m1_play_text").Text == "Your vote"
            && cards.Classes.Single(x => x.Id == "os_m1" && x.Class == "confirm").On && !cards.Classes.Single(x => x.Id == "os_m2" && x.Class == "confirm").On
            && cards.Texts.Single(x => x.Id == "os_m2_name").Text == "map 1" && HudVote.Thumbs(twelve, null, 0, MapVote.PageCards, 1001).Count == HudMenu.Cards
            && !cards.Texts.Any(x => x.Text.Contains("take it back", StringComparison.OrdinalIgnoreCase))
            && Enumerable.Range(1, HudMenu.Cards).All(k => !cards.Classes.Single(x => x.Id == HudMenu.Card(k) && x.Class == "pending").On)); // a ballot card has no countdown: the browser's, on the same panels, goes
        var at = t0.AddMinutes(5);
        var adder = new HudPlayer { VoteView = (r, 1, false) };
        HudFrame.AddedToVote(adder, twelve, at, "os_m3_play");
        Check("Add to the vote: back to the ballot's first page - where the card her vote is on comes first, the map she added once the bridge's answer comes",
            adder.VoteView == (r, 0, false) && adder.Added == (r, "os_m3_play", at));
        // the second click of a double click on the map browser's Play: the Add made that card the ballot's card in its place
        var doubler = new HudPlayer();
        HudFrame.AddedToVote(doubler, twelve, at, "os_m3_play");
        var late = new HudPlayer();
        HudFrame.AddedToVote(late, twelve, at);
        var nextRound = new VoteState { Round = r + 1, Open = true, Options = twelve.Options };
        var addedBy = doubler.Added;
        var asEver = !HudFrame.SecondClick(doubler, twelve, "os_m4_play", at.AddSeconds(0.3)) && !HudFrame.SecondClick(doubler, nextRound, "os_m3_play", at.AddSeconds(0.3))
            && !HudFrame.SecondClick(new HudPlayer(), twelve, "os_m3_play", at.AddSeconds(0.3)) && late.Added == (r, "", at) && !HudFrame.SecondClick(late, twelve, "", at.AddSeconds(0.1));
        var burst = HudFrame.SecondClick(doubler, twelve, "os_m3_play", at.AddSeconds(0.3)) && HudFrame.SecondClick(doubler, twelve, "os_m3_play", at.AddSeconds(0.7)); // 0.4 s after the one before
        var paused = !HudFrame.SecondClick(doubler, twelve, "os_m3_play", at.AddSeconds(1.3)); // 0.6 s after the one before
        Check($"Add to the vote by card 3's Play: a click on that very button within {HudVote.DoubleClickSeconds} s of the click before it is the second click of a double click - the ballot's card 3 now - and counts for nothing, a burst of them too; after a pause that long, and on another card, in another round or with no Add, a click counts as ever",
            addedBy == (r, "os_m3_play", at) && asEver && burst && paused);

        // ---- the mouse during a hold: a player who joined without the HUD has no window to close ----
        slots.Show(2, HudSlots.Page.Maps, t0.AddSeconds(51));
        frame.Draw(World(live, new VoteState(), t0.AddSeconds(52), addon: false), [Alice(kept: true), Bob()], slots, PlayerOf, funClicks, screen);
        Check("Frame: during a hold the window takes the mouse only of a player who was on when it began (they keep the HUD on this map)", screen.Captured[1] && !screen.Captured[2]);

        // ---- every panel by its one name, os_ (HudVersion.Oldest): a game on a content before v4.8.0, whose layout names its panels gg_, gets nothing ----
        Check("Frame: every panel the frame sent - texts, classes, variables and pictures, for everybody and for each player, a game on v4.5.0 too - went out by its one name, os_",
            screen.Ids.Count > 100 && screen.Ids.All(id => id.StartsWith("os_", StringComparison.Ordinal)));
        if (File.Exists(HudLayoutFile))
        {
            var layoutIds = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(HudLayoutFile), @"\bid=""([^""]+)""").Select(m => m.Groups[1].Value).ToHashSet();
            var lacking = screen.Ids.Where(id => !layoutIds.Contains(id)).Order(StringComparer.Ordinal).ToList();
            Check($"Frame: every panel the frame sent is one content {HudVersion.Content} has, so a game holding it logs no \"Unable to find panel\"{(lacking.Count > 0 ? ": not " + string.Join(" ", lacking) : "")}",
                lacking.Count == 0);
        }
        Check("A click from a content before v4.8.0 names a gg_ button, which is none of ours: no version proved, no command, no choice of the Fun or Bots page, no tab, no reason",
            HudVersion.OfButton("gg_hudver_v4.7.0") == null && HudVersion.OfButton(HudVersion.Button("v4.8.0")) == "v4.8.0" && HudVersion.Button("v4.8.0") == "os_hudver_v4.8.0"
            && HudView.Command("gg_btn_up", new HudState()) == null && HudView.Command(HudView.ButtonUp, new HudState()) == "!up" && HudMenu.FunClick("gg_f1_1") == null
            && HudBots.Clicked("gg_b1_2") == null && HudVote.WhyClick("gg_why_1") == null && HudView.Tabs.All(t => t.Id.StartsWith("os_", StringComparison.Ordinal))
            && HudMenu.Command("gg_set_restart", null, new HudState(), new HudBrowser(), 1, t0) == null && HudMenu.Command(HudMenu.Restart, null, new HudState(), new HudBrowser(), 1, t0) == "!restart");

        // ---- the vote's own tabs from any page (release 221's evening: "if you leave the vote tab you cannot return to it") ----
        // While a vote is on, os_screen keeps voting - vbrowse in its map browser - on every page, and the stylesheet shows the vote's Vote and Maps tabs in MAPS'
        // place. Each must take the player to MAPS from wherever they are; the plugin's Maps block then puts their view on the ballot or on the browser.
        var tabFrame = new HudFrame();
        var tabSlots = new HudSlots();
        var tabPlayers = new Dictionary<int, HudPlayer>();
        HudPlayer TabPlayer(int n) => tabPlayers.TryGetValue(n, out var pp) ? pp : tabPlayers[n] = new HudPlayer();
        HudFrame.Human ida = new(9, 1009, "Ida", "CT", HudVersion.Content, false, 0, null);
        var tabScreen = new RecordedScreen();
        tabSlots.Joined(9, first: false, alive: true, t0);
        tabSlots.Show(9, HudSlots.Page.Maps, t0.AddSeconds(1));
        tabFrame.Draw(World(live, twelve, t0.AddSeconds(2)), [ida], tabSlots, TabPlayer, funClicks, tabScreen);
        var onBallot = tabScreen.C(HudView.Screen, "voting", 9) && tabScreen.C(HudView.Screen, "page-maps", 9);
        tabSlots.Click(9, HudBrowser.HistoryTab, t0.AddSeconds(3));
        tabFrame.Draw(World(live, twelve, t0.AddSeconds(4)), [ida], tabSlots, TabPlayer, funClicks, tabScreen);
        var leftIt = tabSlots.PageOf(9) == HudSlots.Page.History && tabScreen.C(HudView.Screen, "page-history", 9) && tabScreen.C(HudView.Screen, "voting", 9);
        var stuck = new List<string>();
        foreach (var page in Enum.GetValues<HudSlots.Page>().Where(p => p != HudSlots.Page.Maps))
            foreach (var tab in new[] { HudVote.BackButton, HudVote.BrowseButton })
            {
                tabSlots.Show(9, page, t0.AddSeconds(5));
                tabSlots.Click(9, tab, t0.AddSeconds(6));
                if (!tabSlots.WindowOpen(9) || tabSlots.PageOf(9) != HudSlots.Page.Maps) stuck.Add($"{page} + {tab}: {(tabSlots.WindowOpen(9) ? tabSlots.PageOf(9) : "closed")}");
            }
        Check($"The vote's Vote and Maps tabs take the player to MAPS from every other page: on HISTORY the window keeps voting on os_screen, so the stylesheet shows them in "
              + $"MAPS' place, and a click on either is the way back to the vote{(stuck.Count > 0 ? $" - stuck: {string.Join(", ", stuck)}" : "")}",
            onBallot && leftIt && stuck.Count == 0 && HudView.Tabs.Count(t => t.Id is HudVote.BackButton or HudVote.BrowseButton && t.Page == HudSlots.Page.Maps) == 2
            && HudView.Tabs.First(t => t.Page == HudSlots.Page.Maps).Id == HudView.TabMaps);
        tabSlots.Show(9, HudSlots.Page.Stats, t0.AddSeconds(7));
        tabFrame.Draw(World(live, twelve, t0.AddSeconds(8)), [ida], tabSlots, TabPlayer, funClicks, tabScreen);
        tabSlots.Click(9, HudVote.BackButton, t0.AddSeconds(9));
        TabPlayer(9).VoteView = (r, 0, false); // the plugin's Maps block: Vote is the ballot
        tabFrame.Draw(World(live, twelve, t0.AddSeconds(10)), [ida], tabSlots, TabPlayer, funClicks, tabScreen);
        var backOn = tabScreen.C(HudView.Screen, "page-maps", 9) && !tabScreen.C(HudView.Screen, "page-stats", 9) && tabScreen.C(HudView.Screen, "voting", 9)
            && !tabScreen.C(HudView.Screen, "vbrowse", 9) && tabScreen.T("os_m1_name", 9) == "map 1";
        tabSlots.Show(9, HudSlots.Page.Fun, t0.AddSeconds(11));
        tabSlots.Click(9, HudVote.BrowseButton, t0.AddSeconds(12));
        TabPlayer(9).VoteView = (r, 0, true); // ...and Maps the browser that adds to the vote
        tabFrame.Draw(World(live, twelve, t0.AddSeconds(13)), [ida], tabSlots, TabPlayer, funClicks, tabScreen);
        Check("...and the frame draws what the tab names: from STATS, Vote is the ballot on MAPS (voting, its cards); from FUN, Maps the browser that adds to the vote (vbrowse)",
            backOn && tabScreen.C(HudView.Screen, "page-maps", 9) && !tabScreen.C(HudView.Screen, "page-fun", 9) && tabScreen.C(HudView.Screen, "vbrowse", 9)
            && !tabScreen.C(HudView.Screen, "voting", 9));

        // ---- the version classes: one latest-N, the old one off when the level moves (os_hud level) ----
        var levels = new RecordedScreen();
        frame.Forget();
        frame.Draw(World(live, new VoteState(), t0.AddSeconds(53)), [], slots, PlayerOf, funClicks, levels);
        frame.Draw(World(live, new VoteState(), t0.AddSeconds(54), level: HudVersion.Level + 1), [], slots, PlayerOf, funClicks, levels);
        Check($"Frame: the newest content the server knows is one class; a faked level turns the real one's off and names a newer content ({HudVersion.OfLevel(HudVersion.Level + 1)})",
            levels.C(HudView.Global, HudVersion.LatestClass(HudVersion.Level + 1)) && levels.Sent(HudView.Global, HudVersion.LatestClass(HudVersion.Level)) && !levels.C(HudView.Global, HudVersion.LatestClass(HudVersion.Level))
            && levels.V(HudView.Global, HudVersion.Var) == HudVersion.OfLevel(HudVersion.Level + 1));

        // ---- 287 b: the chat column's 40 line panels go only to a game whose click named v4.14.0 or later - never to the live item's v4.12.2, nor to one not seen yet ----
        var chat = new HudChat();
        chat.Say("Bob", "T", "hello", false);
        var olderScreen = new RecordedScreen();
        var chatSlots = new HudSlots();
        var chatPlayers = new Dictionary<int, HudPlayer>();
        foreach (var n in new[] { 1, 2, 3 }) chatSlots.Joined(n, first: false, alive: true, t0);
        frame.Draw(new HudFrame.World(live, new VoteState(), null, new HudSearch(), null, Modes.ArmsRace, HudTab.Mode.Down, HudVersion.Level, false, true, _ => null, null,
                t0.AddSeconds(5), HudState.Ms(t0.AddSeconds(5)), Chat: chat, ChatLive: true, ChatRoute: HudChat.Route.Always),
            [new(1, 1001, "Alice", "CT", "v4.12.2", false, 0, null), new(2, 1002, "Bob", "T", HudVersion.Content, false, 0, null), new(3, 1003, "Cas", "CT", "", false, 0, null)],
            chatSlots, n => chatPlayers.TryGetValue(n, out var cp) ? cp : chatPlayers[n] = new HudPlayer(), new FunClicks(), olderScreen);
        bool ChatSent(int slot) => Enumerable.Range(1, HudChat.Lines).Any(k => olderScreen.Sent(HudChat.Line(k), "used", slot) || olderScreen.T(HudChat.Line(k), slot) != "");
        Check($"Frame: the chat column's {HudChat.Lines} line panels (os_chat_1 to os_chat_{HudChat.Lines}) go to a game that named {HudVersion.Content} - never to v4.12.2's, "
            + "which has none and would log \"Unable to find panel\" for each, nor to a game whose content is not known yet (287 b)",
            ChatSent(2) && !ChatSent(1) && !ChatSent(3) && !olderScreen.C(HudView.Screen, "chaton", 1) && olderScreen.C(HudView.Screen, "chaton", 2));

        // ---- the known issue of release 220, fixed in content v4.16.0: the server addresses no panel id an older content lacks - CS2's game looks up every id the
        // HUD's entity has been given on the map, at its full update, and one on an older content logged each it lacks. HISTORY and STATS go to a game whose check
        // named v4.16.0 or newer alone (rule 6): their cards' states and the bars are classes on os_screen and os_global, their pictures go to four picture panels
        // every content has, per viewer and never to everybody (rule 3), and replaced as the viewer moves between the pages or leaves them (rule 4). Nothing ever goes
        // to os_h* or os_st_*, nor to the v4.15.0 panels a v4.14.0 game lacks (the two tabs, the seventh reason, HISTORY's chips and pager, which take clicks only) ----
        string[] newIn415 =
        [
            .. Enumerable.Range(1, 4).SelectMany(k => new[] { HudHistoryPage.Card(k), HudHistoryPage.Thumb(k), $"{HudHistoryPage.Card(k)}_up", $"{HudHistoryPage.Card(k)}_down", $"{HudHistoryPage.Card(k)}_play" }),
            "os_hf_all", "os_hf_ar", "os_hf_dm", "os_hf_wm", "os_hf_mine", "os_hf_order", "os_hf_clear", "os_hpg_first", "os_hpg_prev", "os_hpg_next", "os_hpg_last",
            "os_st_bar1", "os_st_bar2", "os_st_bar3", "os_st_bar4", "os_st_bar5", "os_st_mar", "os_st_mdm", "os_st_mwm", "os_st_pic1", "os_st_pic2",
            HudBrowser.HistoryTab, HudStats.Tab, HudVote.WhyButton(7),
        ];
        var pageStats = new HudStats { Version = 3, Vars = [("osst", "15 matches")], Classes = { ["stf1"] = true } };
        pageStats.Steps["stp2"] = 10;
        pageStats.Steps["stmar"] = 5;
        pageStats.Pictures[HudStats.PlayedPicture] = live.Key;
        var pageHistory = new HudHistory { Version = 2 };
        pageHistory.Entries.Add(new HudHistory.Entry(new DateTimeOffset(t0.AddHours(-1)), new DateTimeOffset(t0.AddMinutes(-45)), 3084291312, "aim_x", "aim_x", Modes.ArmsRaceId,
            HudHistory.ByMatchEnd, "", [new HudHistory.Player(1001, "Alice", 900, new DateTimeOffset(t0.AddHours(-1)))], [], "live"));
        var pageSlots = new HudSlots();
        var pagePlayers = new Dictionary<int, HudPlayer>();
        foreach (var n in new[] { 1, 2, 3, 4, 5, 6, 7 }) pageSlots.Joined(n, first: false, alive: true, t0);
        foreach (var (n, page) in new[] { (1, HudSlots.Page.Stats), (2, HudSlots.Page.History), (3, HudSlots.Page.Stats), (4, HudSlots.Page.History), (5, HudSlots.Page.Stats),
                     (6, HudSlots.Page.History), (7, HudSlots.Page.Start) })
            pageSlots.Show(n, page, t0.AddSeconds(6));
        RecordedScreen PageFrame(int sec, HudSlots slotsNow, params HudFrame.Human[] humans)
        {
            var screenNow = new RecordedScreen();
            frame.Draw(new HudFrame.World(live, new VoteState(), null, new HudSearch(), null, Modes.ArmsRace, HudTab.Mode.Down, HudVersion.Level, false, true, _ => null, null,
                    t0.AddSeconds(sec), HudState.Ms(t0.AddSeconds(sec)), History: pageHistory, Stats: pageStats),
                [.. humans], slotsNow, n => pagePlayers.TryGetValue(n, out var pp) ? pp : pagePlayers[n] = new HudPlayer(), new FunClicks(), screenNow);
            return screenNow;
        }
        HudFrame.Human[] six =
        [
            new(1, 1001, "Alice", "CT", HudVersion.Content, false, 0, null), new(2, 1002, "Bob", "T", HudVersion.Content, false, 0, null),
            new(3, 1003, "Cas", "CT", "v4.14.0", false, 0, null), new(4, 1004, "Dan", "T", "", false, 0, null), new(5, 1005, "Eve", "CT", HudVersion.Unknown, false, 0, null),
            new(6, 1006, "Fay", "T", "v4.15.0", false, 0, null), new(7, 1007, "Gus", "CT", HudVersion.Content, false, 0, null),
        ];
        var pageScreen = PageFrame(7, pageSlots, six);
        PageFrame(8, pageSlots, six); // a second frame: what changed since the first one too
        var everySlot = new[] { HudFrame.Everybody, 1, 2, 3, 4, 5, 6, 7 };
        var toOldPanels = everySlot.SelectMany(n => pageScreen.IdsOf(n).Intersect(newIn415).Select(id => $"{(n < 0 ? "everybody" : $"slot {n}")}: {id}")).ToList();
        var olderGot = new[] { 3, 4, 5, 6 }.SelectMany(n => pageScreen.IdsOf(n).Where(id => HudHistoryPage.PicturePanels.Contains(id) && pageScreen.Pictured.TryGetValue((n, id), out var on) && on.Length > 0)
            .Select(id => $"slot {n}: a picture on {id}")).ToList();
        bool PageWords(int slot) => pageScreen.V(HudView.Screen, "osh1n", slot) != "" || pageScreen.V(HudView.Screen, "ossy1", slot) != ""
            || HudHistoryPage.CardStates.Any(st => pageScreen.Sent(HudView.Screen, HudHistoryPage.CardClass(1, st), slot));
        Check($"Frame: nothing goes to a panel content v4.14.0 lacks - none to os_h*, os_st_*, the two tabs, the seventh reason, HISTORY's chips and pager, for anybody "
            + $"(rule 6){(toOldPanels.Count > 0 ? ": " + string.Join(", ", toOldPanels) : "")}",
            toOldPanels.Count == 0 && (!File.Exists(HudLayoutFile) || HudHistoryPage.PicturePanels.All(id => File.ReadAllText(HudLayoutFile).Contains($"id=\"{id}\"", StringComparison.Ordinal))));
        Check($"Frame: HISTORY and STATS go to a game whose check named {HudVersion.OfLevel(HudBrowser.HistoryLevel)} or newer alone - their words, card states and pictures to its "
            + "own slot - and none to v4.15.0's, v4.14.0's, one not seen yet or not known, on those very pages (rule 6)"
            + (olderGot.Count > 0 ? ": " + string.Join(", ", olderGot) : ""),
            PageWords(1) && PageWords(2) && !PageWords(3) && !PageWords(4) && !PageWords(5) && !PageWords(6) && olderGot.Count == 0
            && pageScreen.C(HudView.Screen, HudHistoryPage.CardClass(1, "used"), 2) && pageScreen.Pictured.ContainsKey((2, HudHistoryPage.PicturePanel(1))));
        Check("Frame: STATS' words, classes and bars on os_global, which every content has - a step's four bits (row 2's 10: stp2-2 and stp2-8; Arms Race's 5: stmar-1 and "
            + "stmar-4) - while somebody views the page; no picture class ever goes to everybody, and none to the picture panels for a game below the pages' content (rule 3)",
            pageScreen.V(HudView.Global, "osst") == "15 matches" && pageScreen.C(HudView.Global, "stf1") && pageScreen.C(HudView.Global, "stp2-2") && pageScreen.C(HudView.Global, "stp2-8")
            && !pageScreen.C(HudView.Global, "stp2-1") && pageScreen.C(HudView.Global, "stmar-1") && pageScreen.C(HudView.Global, "stmar-4") && !pageScreen.C(HudView.Global, "stmar-2")
            && !HudHistoryPage.PicturePanels.Any(id => pageScreen.Pictured.ContainsKey((HudFrame.Everybody, id))) && olderGot.Count == 0
            && new[] { 3, 4, 5, 6 }.All(n => HudHistoryPage.PicturePanels.All(id => !pageScreen.Pictured.ContainsKey((n, id)))));
        // rule 4: one viewer moving HISTORY -> STATS -> MAPS -> STATS: the four picture panels take the page's pictures, an unused one none, and leaving clears them
        var moveSlots = new HudSlots();
        moveSlots.Joined(7, first: false, alive: true, t0);
        HudFrame.Human gus = new(7, 1007, "Gus", "CT", HudVersion.Content, false, 0, null);
        moveSlots.Show(7, HudSlots.Page.History, t0.AddSeconds(10));
        var onHistory = PageFrame(11, moveSlots, gus);
        moveSlots.Show(7, HudSlots.Page.Stats, t0.AddSeconds(12));
        var onStats = PageFrame(13, moveSlots, gus);
        moveSlots.Show(7, HudSlots.Page.Maps, t0.AddSeconds(14));
        var onMaps = PageFrame(15, moveSlots, gus);
        string[] Pic(RecordedScreen sc, int k) => sc.Pictured.TryGetValue((7, HudHistoryPage.PicturePanel(k)), out var on) ? on : ["(not sent)"];
        Check($"Frame: a viewer moving between HISTORY and STATS, or leaving them, gets the four picture panels' classes replaced with the new page's - HISTORY a card's on "
            + $"each, STATS the most played and most liked on panels 1 and 2 and none on 3 and 4, any other page none on all four (rule 4)",
            Pic(onHistory, 2).Length == 0 && Pic(onHistory, 3).Length == 0 && Pic(onHistory, 4).Length == 0
            && Pic(onStats, 3).Length == 0 && Pic(onStats, 4).Length == 0 && onStats.Pictured.ContainsKey((7, HudHistoryPage.PicturePanel(1))) && onStats.Pictured.ContainsKey((7, HudHistoryPage.PicturePanel(2)))
            && Enumerable.Range(1, 4).All(k => Pic(onMaps, k).Length == 0));
        // hud-422's suggestion: a page turn on HISTORY changed all four pictures in one frame (an 83.5 ms frame); now a new picture comes one a frame, the others none meanwhile
        var stag = new HudPlayer();
        string Pp(int k) => HudHistoryPage.PicturePanel(k);
        List<(string Panel, string[] Classes)> Four(params string[][] pics) => [.. pics.Select((c, i) => (Pp(i + 1), c))];
        string Shown(List<(string Panel, string[] Classes)> sent) => string.Join(" ", sent.Select(x => x.Classes.Length == 0 ? "-" : x.Classes[0]));
        var page1 = Four(["ta"], ["tb"], ["tc"], ["td"]);
        var turns = new[] { page1, page1, page1, page1, page1 }.Select(w => Shown(HudFrame.OnePerFrame(stag, w))).ToList();
        var turnedBack = new[] { Four(["te"], [], [], []), Four(["te"], [], [], []) }.Select(w => Shown(HudFrame.OnePerFrame(stag, w))).ToList();
        var some = new[] { Four(["te"], ["tf"], ["tg"], []), Four(["te"], ["tf"], ["tg"], []), Four(["th"], ["tf"], ["tg"], []) }.Select(w => Shown(HudFrame.OnePerFrame(stag, w))).ToList();
        Check($"Frame: HISTORY's pictures come one new one a frame - the rest none until theirs, never the page before's - while one that stays stays and one that goes goes at once "
              + $"({string.Join(" | ", turns.Concat(turnedBack).Concat(some))})",
            turns.SequenceEqual(["ta - - -", "ta tb - -", "ta tb tc -", "ta tb tc td", "ta tb tc td"]) && turnedBack.SequenceEqual(["te - - -", "te - - -"])
            && some.SequenceEqual(["te tf - -", "te tf tg -", "th tf tg -"]));
        // item 7: an outdated game's banner and its window's foot say why it needs the server's content - osver on its own os_screen; an up-to-date one keeps os_global's
        var why = PageFrame(16, pageSlots, six);
        Check($"Frame: an outdated game's osver, on its own os_screen, says the server's content and why it needs it (\"{why.V(HudView.Screen, HudVersion.Var, 3)}\") - v4.14.0's and "
            + "v4.15.0's alike - while an up-to-date game, one not seen yet and one not known keep the plain version from os_global",
            why.V(HudView.Screen, HudVersion.Var, 3) == $"{HudVersion.Content} - HISTORY and STATS need it" && why.V(HudView.Screen, HudVersion.Var, 6) == $"{HudVersion.Content} - HISTORY and STATS need it"
            && why.V(HudView.Screen, HudVersion.Var, 1) == "" && why.V(HudView.Screen, HudVersion.Var, 4) == "" && why.V(HudView.Screen, HudVersion.Var, 5) == ""
            && why.V(HudView.Global, HudVersion.Var) == HudVersion.Content && HudVersion.OutdatedLine(HudVersion.Content).Length <= 60);

        // ---- content v4.16.0 (the owner's 309 a and 310): the server names no panel a content in use lacks - CS2 keeps one list of every panel id named on a map,
        // and every game looks all of it up at its full update - so every panel these frames named, for everybody and for each player, is one v4.14.0 (PanelFloor)
        // and every later content has; and the pages' pictures (HudHistoryPage.PicturePanels, panels the older contents have) go per viewer only, to a game at v4.16.0 or later ----
        if (File.Exists(HudLayoutFile))
        {
            static HashSet<string> LayoutIds(string text) => System.Text.RegularExpressions.Regex.Matches(text, @"\bid=""([^""]+)""").Select(m => m.Groups[1].Value).ToHashSet();
            var floorLayouts = HudVersion.Contents.Where(c => c.Level >= HudVersion.LevelOf(PanelFloor) && c.Version != HudVersion.Content).Select(c => $"../hud/reference/gungame_hud_{c.Version}.xml")
                .Where(File.Exists).Select(f => LayoutIds(File.ReadAllText(f))).Append(LayoutIds(File.ReadAllText(HudLayoutFile))).ToList();
            var floorIds = floorLayouts.Skip(1).Aggregate(new HashSet<string>(floorLayouts[0]), (all, one) => { all.IntersectWith(one); return all; });
            var named = screen.Ids.Concat(pageScreen.Ids).Concat(olderScreen.Ids).Concat(levels.Ids).Concat(onHistory.Ids).Concat(onStats.Ids).Concat(onMaps.Ids).Concat(why.Ids).ToHashSet();
            var beyond = named.Where(id => !floorIds.Contains(id)).Order(StringComparer.Ordinal).ToList();
            Check($"Frame (content v4.16.0): every panel these frames named ({named.Count}) is one {PanelFloor} and every later content has ({floorLayouts.Count} layouts), so no game on a content in use logs \"Unable to find panel\" for it, whoever was shown what on the map{(beyond.Count > 0 ? ": not " + string.Join(" ", beyond) : "")}",
                floorLayouts.Count == HudVersion.Contents.Count(c => c.Level >= HudVersion.LevelOf(PanelFloor)) && beyond.Count == 0 && named.Count > 100);
            var picturedOld = pageScreen.Pictured.Keys.Where(k => HudHistoryPage.PicturePanels.Contains(k.Panel) && (k.Slot == HudFrame.Everybody || k.Slot is 3 or 4 or 5 or 6)).Select(k => $"{k.Slot}: {k.Panel}").ToList();
            Check($"Frame (content v4.16.0): HISTORY's and STATS' pictures, on panels the older contents have ({string.Join(" ", HudHistoryPage.PicturePanels)}), go to a viewer's own slot - never into everybody's state, never to a game before v4.16.0 or not known{(picturedOld.Count > 0 ? ": " + string.Join(", ", picturedOld) : "")}",
                picturedOld.Count == 0 && pageScreen.Pictured.ContainsKey((1, HudHistoryPage.PicturePanel(1))) && pageScreen.Pictured.ContainsKey((2, HudHistoryPage.PicturePanel(1)))
                && pageScreen.Pictured.ContainsKey((2, HudHistoryPage.PicturePanel(4))));
        }

        // ---- a slot's record: a new player starts from nothing of the last one's ----
        PlayerOf(1).Browser.Find("aim");
        PlayerOf(1).Note = ("Refused", t0.AddMinutes(5), "you");
        players.Remove(1); // what HudScreen.Leave does when a player leaves or a new one takes the slot
        Check("A slot's record: the next player in the slot starts with a fresh browser, no note, no vote of the last one's", PlayerOf(1) is { Browser.Query: "", Note: null, VoteOpened: false, VoteShown: 0 });

        // ---- the scores at a match-end vote (the owner's 292): no key reaches the server there, so the ballot's SCORES button looks (HudScores) - on fresh
        // slots of its own; the same vote as above, Alice voted (mvv 1001 1), Bob not ----
        var ms = new HudSlots();
        var mp = new Dictionary<int, HudPlayer>();
        HudPlayer MP(int slot) => mp.TryGetValue(slot, out var p) ? p : mp[slot] = new HudPlayer();
        var mf = new HudFrame();
        var sc = new RecordedScreen();
        var tm = t0.AddMinutes(10);
        ms.Joined(1, first: false, alive: true, tm);
        ms.Joined(2, first: false, alive: true, tm);
        HudFrame.World AtEnd(VoteState v, DateTime now, bool lifted = true) => World(live, v, now, locked: true) with { MatchScreen = true, ChatLift = lifted };
        bool On(string cls, int slot) => sc.C(HudView.Screen, cls, slot);
        mf.Draw(AtEnd(open, tm.AddSeconds(1), lifted: false), [Alice(), Bob()], ms, MP, new FunClicks(), sc);
        var notYetLifted = !On("vscoresok", 1) && On("vpeekok", 1) && ms.WindowOpen(1) && ms.WindowOpen(2);
        mf.Draw(AtEnd(open, tm.AddSeconds(4)), [Alice() with { TabHeld = true }, Bob()], ms, MP, new FunClicks(), sc); // her TAB down as the match ended: never a peek there
        var buttonShown = On("vscoresok", 1) && !On("vscoresok", 2) && !On("vscores", 1) && !On("vpeek", 1);
        mf.Draw(World(live, open, tm.AddSeconds(5), locked: true), [Alice(), Bob()], ms, MP, new FunClicks(), sc);
        var noButtonMidMatch = !On("vscoresok", 1) && On("vpeekok", 1);
        var notVoted = HudScores.Click(MP(2), HudScores.Button, true, true, open, 1002);
        var midMatch = HudScores.Click(MP(1), HudScores.Button, true, false, open, 1001);
        var started = HudScores.Click(MP(1), HudScores.Button, true, true, open, 1001);
        mf.Draw(AtEnd(open, tm.AddSeconds(7)), [Alice(), Bob()], ms, MP, new FunClicks(), sc);
        var looking = On("vscores", 1) && On("vpeek", 1) && !On("vscoresok", 1) && ms.WindowOpen(1) && sc.Captured[1] && On("vlock", 1) && !On("vscores", 2) && !On("vpeek", 2);
        var again = HudScores.Click(MP(1), HudScores.Button, true, true, open, 1001);
        mf.Draw(AtEnd(open, tm.AddSeconds(22)), [Alice(), Bob()], ms, MP, new FunClicks(), sc); // 15 s on: no timer (292 a)
        var at15 = On("vscores", 1) && On("vpeek", 1);
        mf.Draw(AtEnd(open, tm.AddSeconds(67)), [Alice(), Bob()], ms, MP, new FunClicks(), sc); // 60 s on, the vote still open
        var at60 = On("vscores", 1) && On("vpeek", 1) && !On("vscoresok", 1) && sc.Captured[1] && HudScores.Ended(MP(1), open, true, true) == "";
        Check("Frame: the scores at a match-end vote - SCORES shows once she has voted, at the match-end screen once CS2 shows its scoreboard there (vscoresok, meos), never his "
            + "before he votes nor mid-match, and a TAB read as held there (its TAB-up never reaches the server) is no peek; its click steps her window aside (vscores, vpeek) "
            + "with no timer (292 a) - still aside 15 s and 60 s on while the vote is open - the window open, frozen and with the mouse; a click before the vote, mid-match "
            + "or during the look changes nothing",
            notYetLifted && buttonShown && noButtonMidMatch && notVoted == "not now (not voted yet)" && MP(2).Scores == null
            && midMatch == "not now (not at the match-end screen - TAB is the way)" && started == "the window steps aside until Back to the vote or the vote's decision" && looking
            && again == "already looking - nothing" && at15 && at60);
        var back1 = HudScores.Click(MP(1), HudScores.BackButton, true, true, open, 1001);
        mf.Draw(AtEnd(open, tm.AddSeconds(68)), [Alice(), Bob()], ms, MP, new FunClicks(), sc);
        var backOff = !On("vscores", 1) && !On("vpeek", 1) && On("vscoresok", 1);
        HudScores.Click(MP(1), HudScores.Button, true, true, open, 1001);
        var onBefore = HudScores.On(MP(1), open, true);
        mf.Draw(AtEnd(shut, tm.AddSeconds(70)), [Alice(), Bob()], ms, MP, new FunClicks(), sc);
        var decidedEnds = onBefore && !On("vscores", 1) && On("vscoresok", 1) && HudScores.Ended(MP(1), shut, true, true) == "the vote decided";
        var afterDecision = HudScores.Click(MP(1), HudScores.Button, true, true, shut, 1001) == "the window steps aside until Back to the vote"
            && HudScores.Click(MP(2), HudScores.Button, true, true, shut, 1002) == "the window steps aside until Back to the vote";
        mf.Draw(AtEnd(shut, tm.AddMinutes(5)), [Alice(), Bob()], ms, MP, new FunClicks(), sc); // minutes on: still aside, no timer
        var bothLook = On("vscores", 1) && On("vscores", 2) && On("vpeek", 2) && HudScores.Ended(MP(1), shut, true, true) == "";
        var back = HudScores.Click(MP(1), HudScores.BackButton, true, true, shut, 1001);
        var backAgain = HudScores.Click(MP(1), HudScores.BackButton, true, true, shut, 1001);
        mf.Draw(AtEnd(shut, tm.AddMinutes(5).AddSeconds(1)), [Alice(), Bob()], ms, MP, new FunClicks(), sc);
        var backShown = !On("vscores", 1) && !On("vpeek", 1) && On("vscoresok", 1) && On("vscores", 2);
        mf.Draw(AtEnd(shut, tm.AddMinutes(5).AddSeconds(2), lifted: false), [Alice(), Bob()], ms, MP, new FunClicks(), sc);
        var screenGone = !On("vscores", 2) && !On("vpeek", 2) && !On("vscoresok", 2) && HudScores.Ended(MP(2), shut, true, false) == "the match-end screen ended";
        Check("Frame: the scores' look ends at Back to the vote, at the decision of the vote it began in - she sees the result - or at the match-end screen's end, and at "
            + "nothing else; once decided everybody may look, and a look begun then stays, minutes on, until Back to the vote or the screen's end; a second Back does "
            + "nothing; no other button is SCORES' or Back's",
            back1 == "the window is back" && backOff && decidedEnds && afterDecision && bothLook && back == "the window is back" && backAgain == "not looking - nothing"
            && backShown && screenGone
            && HudScores.Click(MP(1), HudVote.RandomButton, true, true, shut, 1001) == null && HudScores.Click(MP(1), HudVote.BackButton, true, true, shut, 1001) == null
            && HudScores.Button != HudVote.BackButton && HudScores.BackButton != HudVote.BackButton);
    }
}
