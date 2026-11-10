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
            && HudVote.CardOf("os_m10_play") == 10 && HudVote.CardOf("os_m11_play") == 0 && HudVote.CardOf("os_m1_up") == 0 && HudVote.CardOf(HudVote.RandomButton) == 0 && HudVote.CardOf("os_m_play") == 0);
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

        // ---- the version classes: one latest-N, the old one off when the level moves (os_hud level) ----
        var levels = new RecordedScreen();
        frame.Forget();
        frame.Draw(World(live, new VoteState(), t0.AddSeconds(53)), [], slots, PlayerOf, funClicks, levels);
        frame.Draw(World(live, new VoteState(), t0.AddSeconds(54), level: HudVersion.Level + 1), [], slots, PlayerOf, funClicks, levels);
        Check($"Frame: the newest content the server knows is one class; a faked level turns the real one's off and names a newer content ({HudVersion.OfLevel(HudVersion.Level + 1)})",
            levels.C(HudView.Global, HudVersion.LatestClass(HudVersion.Level + 1)) && levels.Sent(HudView.Global, HudVersion.LatestClass(HudVersion.Level)) && !levels.C(HudView.Global, HudVersion.LatestClass(HudVersion.Level))
            && levels.V(HudView.Global, HudVersion.Var) == HudVersion.OfLevel(HudVersion.Level + 1));

        // ---- a slot's record: a new player starts from nothing of the last one's ----
        PlayerOf(1).Browser.Find("aim");
        PlayerOf(1).Note = ("Refused", t0.AddMinutes(5), "you");
        players.Remove(1); // what HudScreen.Leave does when a player leaves or a new one takes the slot
        Check("A slot's record: the next player in the slot starts with a fresh browser, no note, no vote of the last one's", PlayerOf(1) is { Browser.Query: "", Note: null, VoteOpened: false, VoteShown: 0 });
    }
}
