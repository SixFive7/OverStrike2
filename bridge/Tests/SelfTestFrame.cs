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
            new(s, v, null, new HudSearch(), null, Modes.ArmsRace, HudTab.Mode.Down, level ?? HudVersion.Level, ("", default), locked, addon, _ => null, null, now, HudState.Ms(now));
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
        Check("Frame: a vote that freezes everybody opens every window on MAPS, the ballot, held and with no close button; a game on v4.5.0 gets pages of eight",
            slots.WindowOpen(1) && slots.WindowOpen(2) && slots.PageOf(2) == HudSlots.Page.Maps && players[2].VoteOpened && screen.C(HudView.Screen, "vlock", 2) && screen.C(HudView.Screen, "voting", 2)
            && screen.T("os_m1_name", 2) == "#12 aim_x" && screen.V(HudView.Global, $"{HudView.PlayersVar}2d") == "waiting" && screen.V(HudView.Global, $"{HudView.PlayersVar}1d") == "voted"
            && players[2].Drawn.Round == 1793900000 && HudFrame.SeenOrder(players[2], 1793900000, 0, t0.AddSeconds(40)) is [1] && HudVote.CardsFor(Bob().Version) == HudBrowser.PerPage);
        frame.Draw(World(live, new VoteState { WaitFor = 120 }, t0.AddSeconds(50)), [Alice(), Bob()], slots, PlayerOf, funClicks, screen);
        Check("Frame: when the freeze ends, the windows it opened close together - but the welcome, which goes back to its start page and waits for its X",
            !slots.WindowOpen(2) && !players[2].VoteOpened && slots.WindowOpen(1) && slots.PageOf(1) == HudSlots.Page.Start);

        // ---- the mouse during a hold: a player who joined without the HUD has no window to close ----
        slots.Show(2, HudSlots.Page.Maps, t0.AddSeconds(51));
        frame.Draw(World(live, new VoteState(), t0.AddSeconds(52), addon: false), [Alice(kept: true), Bob()], slots, PlayerOf, funClicks, screen);
        Check("Frame: during a hold the window takes the mouse only of a player who was on when it began (they keep the HUD on this map)", screen.Captured[1] && !screen.Captured[2]);

        // ---- a panel under every name it has (HudIds): ours, os_, in content v4.8.0 on; gg_ in the contents shipped before it ----
        Check("HudIds: an id of ours as a shipped content names it, and a click from one made ours - its version button too; what is no id stays as it is",
            HudIds.As("os_m1_play", HudIds.Shipped) == "gg_m1_play" && HudIds.As("os_m1_play", HudIds.Ours) == "os_m1_play" && HudIds.As("text", HudIds.Shipped) == "text"
            && HudIds.ToOurs("gg_m1_play") == "os_m1_play" && HudIds.ToOurs("os_m1_play") == "os_m1_play" && HudIds.ToOurs("bogus") == "bogus"
            && HudVersion.OfButton(HudIds.ToOurs("gg_hudver_v4.7.0")) == "v4.7.0" && HudVersion.OfButton(HudVersion.Button("v4.8.0")) == "v4.8.0"
            && HudVersion.Button("v4.8.0") == "os_hudver_v4.8.0");
        Check("HudIds: a game's names by what its HUD check said - v4.7.0 and older gg_, v4.8.0 and newer os_, both while it has not said; both for everybody while v4.5.0 to v4.7.0 are drawn for",
            HudIds.Of("v4.7.0").SequenceEqual([HudIds.Shipped]) && HudIds.Of("v4.5.0").SequenceEqual([HudIds.Shipped]) && HudIds.Of(HudVersion.Older).SequenceEqual([HudIds.Shipped])
            && HudIds.Of("v4.8.0").SequenceEqual([HudIds.Ours]) && HudIds.Of("v9.1.0").SequenceEqual([HudIds.Ours])
            && HudIds.Of("").SequenceEqual(HudIds.Drawn) && HudIds.Of(HudVersion.Unknown).SequenceEqual(HudIds.Drawn) && HudIds.Drawn.SequenceEqual([HudIds.Ours, HudIds.Shipped])
            && HudIds.PrefixOf(HudVersion.LevelOf("v4.7.0")) == HudIds.Shipped && HudIds.PrefixOf(HudVersion.Level) == HudIds.Ours);
        var named = new RecordedScreen();
        var names = new HudIds.Names(named, slot => slot == 2 ? HudIds.Of("v4.8.0") : slot == 3 ? HudIds.Of("v4.6.0") : HudIds.Drawn);
        names.Text("os_b_queue", "x"); names.Text("os_m1_name", "y", 2); names.Text("os_m1_name", "z", 3);
        names.Class(HudView.Global, "news", true); names.Var(HudView.Global, HudVersion.Var, "v4.8.0"); names.Pictures(HudFrame.Everybody, [("os_t_thumb", ["th1", "tl2"])]);
        Check("HudIds.Names: a panel for everybody goes out under both names, a player's under their content's alone - texts, classes, variables and pictures alike",
            named.T("os_b_queue") == "x" && named.T("gg_b_queue") == "x" && named.T("os_m1_name", 2) == "y" && named.T("gg_m1_name", 2) == ""
            && named.T("gg_m1_name", 3) == "z" && named.T("os_m1_name", 3) == "" && named.C("os_global", "news") && named.C("gg_global", "news")
            && named.V("gg_global", HudVersion.Var) == "v4.8.0" && named.V("os_global", HudVersion.Var) == "v4.8.0"
            && named.Pictured.ContainsKey((HudFrame.Everybody, "gg_t_thumb")) && named.Pictured.ContainsKey((HudFrame.Everybody, "os_t_thumb")));
        names.Text(HudBots.Note, "n"); names.Text("os_b3_2_txt", "T"); names.Class(HudBots.Row(3), "used", true); names.Class(HudBots.Preset(1), "on", true);
        names.Class(HudBots.Tab, "x", true); names.Var(HudView.Screen, HudBots.WhyVar(5), "why", 3); names.Var(HudView.Screen, HudBots.WhyVar(5), "why");
        Check("HudIds.Names: the Bots page - v4.8.0's own panels, which no shipped content has - goes out under our name alone, its rows' variables too; a v4.6.0 game gets none of it",
            named.T("os_bots_note") == "n" && named.T("gg_bots_note") == "" && named.T("os_b3_2_txt") == "T" && named.T("gg_b3_2_txt") == "" && named.C("os_b3", "used") && !named.C("gg_b3", "used")
            && named.C("os_bp1", "on") && !named.C("gg_bp1", "on") && named.C("os_tab_bots", "x") && !named.C("gg_tab_bots", "x")
            && named.V("os_screen", "osbw5") == "why" && named.V("gg_screen", "osbw5") == "" && named.V("gg_screen", "osbw5", 3) == ""
            && HudIds.OursOnly("os_b12_5") && HudIds.OursOnly(HudBots.SubFun) && !HudIds.OursOnly("os_b_queue") && !HudIds.OursOnly("os_b_vup") && !HudIds.OursOnly("os_btn_close") && !HudIds.OursOnly("os_m1_name"));

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
