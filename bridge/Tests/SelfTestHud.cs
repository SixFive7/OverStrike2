// --selftest, part three: the HUD's words and wiring (Hud.cs). Every panel id and class the
// plugin sends must exist in the Workshop layout, and every label in the layout must be fed - a mismatch there shows nothing on screen
// and logs nothing anywhere, so this is the only place it can be caught before players see it.
using System.Text.RegularExpressions;

static partial class Program
{
    const string HudLayoutFile = "../hud/layout/gungame_hud_" + Hud.ReleaseFile + ".xml", HudStyleFile = "../hud/styles/gungame_hud_" + Hud.ReleaseFile + ".css",
        HudThumbFile = "../hud/styles/gungame_hud_thumbs_" + Hud.ReleaseFile + ".css";

    /// <summary>The pages' height: the window's 920 px less its rule (3), head (76), tabs (48), sub-tabs' old 40 (kept, to spare) and foot (44); the Help page's,
    /// less its padding (14 + 20).</summary>
    const int PagesHeight = 920 - 3 - 76 - 48 - 40 - 44, HelpHeight = PagesHeight - 34;

    static void HudSelfTest(Action<string, bool> Check)
    {
        // ---- the words: the Help page's groups and rows, and room for them in the layout ----
        Check("The Help page's five groups: maps, the vote, likes, game, the board", Hud.Help.Select(g => g.Name).SequenceEqual(["Maps", "The vote", "Likes", "Game", "The board"]));
        Check("Every group fits its rows, every section of How it works its lines, the start page its three steps",
            Hud.Help.All(g => g.Rows.Length is > 0 and <= Hud.RowsPerGroup) && Hud.How.All(r => r.Lines.Length is > 0 and <= Hud.HowLines) && Hud.How.Length <= Hud.HowSections && Hud.Steps.Length == 3);
        var rows = Hud.Help.SelectMany(g => g.Rows).ToList();
        Check($"Every place to click fits its column: at most 22 characters (longest {rows.Max(r => r.Command.Length)})", rows.All(r => r.Command.Length <= 22));
        Check($"...and its words two lines of it: at most 100 characters (longest {rows.Max(r => (r.Words + r.Also).Length + 5)})", rows.All(r => (r.Words + r.Also).Length + 5 <= 100));
        Check($"Every line of How it works at most 160 characters, three lines of its column (longest {Hud.How.SelectMany(r => r.Lines).Max(l => l.Length)})", Hud.How.SelectMany(r => r.Lines).All(l => l.Length <= 160));
        Check("Start page steps: short titles and places, at most three lines of words", Hud.Steps.All(s => s.Title.Length <= 14 && s.Command.Length <= 14 && s.Words.Length <= 90));
        // The Help page's height: a row's words wrap at about 60 characters (14 px Stratum2 in the 446 px left of the pages beside the live column,
        // the group name and the place); a line is 19 px, a row 4 px more, a group 14 px; the page has HelpHeight px (PagesHeight less the page's
        // padding). Past it the last group runs off the window.
        static int HelpLines(Hud.Row r) => (int)Math.Ceiling((r.Also == "" ? r.Words : $"{r.Words}  ·  {r.Also}").Length / 60.0);
        var helpHeight = Hud.Help.Sum(g => g.Rows.Sum(r => HelpLines(r) * 19 + 4) + 14);
        Check($"The Help page fits the window: about {helpHeight} px of its {HelpHeight} ({Hud.Help.Sum(g => g.Rows.Sum(HelpLines))} lines in {Hud.Help.Sum(g => g.Rows.Length)} rows)", helpHeight <= HelpHeight);

        // ---- the codec: what the bridge serves is what the plugin reads ----
        var state = new HudState { Maps = 1000, Map = "#12 aim\tred\nline", By = "Jo\"ri; quit", Size = "17 MB", Key = "123", Up = 3, Down = 1, Status = "x",
            MyVotes = new() { [7] = 1, [9] = -1 }, Top = [("#44 aim_aim", 3, 1)], ChangeMap = "#5 x", ChangeBy = "the dice", ChangeIn = 6 };
        var decoded = HudState.Decode(state.Encode());
        Check("The codec carries every field, and a tab or newline in a name cannot break a line; no queue, holder or skip line any more",
            decoded is { Maps: 1000, Up: 3, Down: 1, ChangeIn: 6, Size: "17 MB", Key: "123" } && decoded.Map == "#12 aim red line" && decoded.By == "Jo\"ri; quit"
            && decoded.MyVotes[9] == -1 && decoded.Top is [("#44 aim_aim", 3, 1)] && decoded.ChangeBy == "the dice" && decoded.Encode() == state.Encode()
            && !Regex.IsMatch(state.Encode(), @"(?m)^(hold|skip|next|q|vopen)\t"));
        Check("Not a HUD state (an error page, an old bridge) decodes to nothing; unknown lines are skipped",
            HudState.Decode(null) is null && HudState.Decode("<html>") is null && HudState.Decode(HudState.Magic + "\nfuture\tline\nmaps\t5\n") is { Maps: 5 });

        // ---- the window's buttons ----
        var free = new HudState();
        Check("The action button: Start a vote; a change under way or a load in progress offers nothing",
            HudView.Action(free).Command == "!next" && HudView.Action(new HudState { ChangeIn = 3 }).Command == null && HudView.Action(new HudState { Loading = true }).Command == null);
        Check("Clicks map to commands: the like buttons, the action button; the close and tab buttons stay in the plugin",
            HudView.Command(HudView.ButtonUp, null) == "!up" && HudView.Command(HudView.ButtonDown, null) == "!down" && HudView.Command(HudView.ButtonAct, free) == "!next"
            && HudView.Command(HudView.ButtonClose, free) == null && HudView.Tabs.All(t => HudView.Command(t.Id, free) == null)
            && HudView.Command(HudView.ButtonAct, null) == null);
        Check("Every command a click can send is one the bridge accepts from the HUD", new[] { "!up", "!down", "!next", "!next random", "!previous" }.All(Hud.Actions.Contains)
            && new[] { free, new HudState { ChangeIn = 2 } }.All(s => HudView.Action(s).Command is not { } c || Hud.Actions.Contains(c)));

        // ---- the menu: what a click may say ----
        Check("Menu clicks: likes and picks name a map by rank, Workshop id or official name; a Dislike's reason; !time 10|60, !restart, Start a vote pass",
            new[] { "!up", "!down", "!next", "!next random", "!map 12", "!map 3084291314", "!up 12", "!down ar_shoots_night", "!down big", "!down 12 big", "!down ads",
                "!time", "!time 10", "!time 60", "!restart", "!previous", "!map ar_baggage", "!map 12 deathmatch" }.All(Hud.IsAction));
        Check("...and nothing free-form: no name search, no second command, no other !time, no !rcon, no unknown verb - and none of the queue's or the duplicates' old words",
            new[] { "!map aim", "!map 12 13", "!map 12;quit", "!map ar_Baggage", "!map ar_", "!up 1234567890123", "!time 30", "!rcon quit", "!nades on",
                "!cancel 7", "!cancel all", "!cancel", "!done", "!skip", "!toobig", "!ads", "!toobig 12", "!reason big", "!map", "!map ", "", "say hi", "!skip 3" }.All(c => !Hud.IsAction(c)));

        // ---- the menu's catalog: what the bridge serves on /catalog is what the plugin reads ----
        var cat = new HudCatalog { Version = 42 };
        cat.Maps.Add(new HudCatalog.Entry("3084291314", "1", 1, 3084291314, "AIM\tMap\nOne", 112, false, -1, 0, 5, 1));
        cat.Maps.Add(new HudCatalog.Entry("ar_shoots", "ar_shoots", 0, 0, "ar_shoots", 0, true, -1, 2, 0, 0));
        cat.Maps.Add(new HudCatalog.Entry("3070208661", "2", 2, 3070208661, "Zebra", 450, false, 3, 0, 0, 4));
        cat.Maps.Add(new HudCatalog.Entry("3070210812", "3", 3, 3070210812, "beta", 20, false, -1, 0, 2, 0));
        cat.Votes[1001] = new() { ["3084291314"] = 1, ["3070208661"] = -1 };
        var back = HudCatalog.Decode(cat.Encode())!;
        Check("The catalog codec carries every map and every vote, and a tab or newline in a name cannot break a line",
            back is { Version: 42 } && back.Maps.Count == 4 && back.Maps[0] is { Name: "AIM Map One", Up: 5, Down: 1, SizeMb: 112, Broken: -1 } && back.Maps[1] is { Official: true, Crashes: 2 }
            && back.Maps[2].Broken == 3 && back.MyVote(1001, "3070208661") == -1 && back.MyVote(1002, "3084291314") == 0 && back.Encode() == cat.Encode()
            && HudCatalog.Decode("<html>") == null && HudCatalog.Decode(HudCatalog.Magic + "\nm\tshort\n") is { Maps.Count: 0 });

        // ---- the map browser: sort, filter, search, pages ----
        var b = new HudBrowser();
        string Order(HudBrowser x, long account = 1001) => string.Join(",", x.Matches(back, account).Select(m => m.Arg));
        Check("Browser: by rank first, the official maps after the ranked ones", Order(b) == "1,2,3,ar_shoots");
        b.Click("os_sort_rank", 4);
        Check("...the same sort again turns it round", Order(b) == "ar_shoots,3,2,1" && b.Descending);
        b.Click("os_sort_votes", 4);
        Check("Browser: by votes - most liked first, the disliked last - and a new sort starts in its natural direction", Order(b) == "1,3,ar_shoots,2" && !b.Descending);
        b.Click("os_sort_name", 4);
        Check("Browser: by name, case ignored", Order(b) == "1,ar_shoots,3,2");
        b.Click("os_sort_size", 4);
        Check("Browser: by size, smallest first, official maps (no download) last", Order(b) == "3,1,2,ar_shoots");
        b = new HudBrowser();
        b.Click("os_fl_tiny", 4);
        Check("Browser: the size chips (<25 MB here) leave the official maps out", Order(b) == "3"
            && HudBrowser.BandOf(24) == HudBrowser.Band.Tiny && HudBrowser.BandOf(25) == HudBrowser.Band.Small && HudBrowser.BandOf(120) == HudBrowser.Band.Mid && HudBrowser.BandOf(400) == HudBrowser.Band.Large);
        b.Click("os_fl_tiny", 4);
        Check("...the same chip again is All again", b.Size == HudBrowser.Band.All && Order(b) == "1,2,3,ar_shoots");
        b.Click(HudBrowser.LikedButton, 4);
        Check("Browser: liked by me - this player's own upvotes only", Order(b) == "1" && Order(b, 1002) == "");
        b.Click(HudBrowser.LikedButton, 4); b.Click(HudBrowser.OfficialButton, 4);
        Check("Browser: official maps only", Order(b) == "ar_shoots");
        b.Find("ZEBRA");
        Check("!find: a search from the first page that drops the other filters, case ignored; a map's rank or id finds it too",
            Order(b) == "2" && !b.Official && b.Page == 0 && new HudBrowser { Query = "3070210812" }.Matches(back, 1).Single().Arg == "3"
            && new HudBrowser { Query = "ar_shoots" }.Matches(back, 1).Single().Official);
        b.Click(HudBrowser.ClearButton, 4);
        Check("...and the clear button drops the search", b.Query == "" && Order(b) == "1,2,3,ar_shoots");
        var big = new HudCatalog();
        for (var i = 1; i <= 20; i++) big.Maps.Add(new HudCatalog.Entry($"{1000 + i}", $"{i}", i, 1000 + i, $"m{i}", 10, false, -1, 0, 0, 0));
        var pb = new HudBrowser();
        var all = pb.Matches(big, 1);
        Check("Pages of twelve: 20 maps are two pages, the last one has eight", HudBrowser.PerPage == 12 && HudBrowser.Pages(20) == 2 && HudBrowser.Pages(0) == 1 && pb.PageOf(all).Count == 12);
        pb.Click(HudBrowser.NextButton, 20); pb.Click(HudBrowser.NextButton, 20); pb.Click(HudBrowser.NextButton, 20);
        Check("Next stops at the last page, which shows the rest", pb.Page == 1 && pb.PageOf(all).Select(m => m.Arg).SequenceEqual(["13", "14", "15", "16", "17", "18", "19", "20"]));
        pb.Click(HudBrowser.FirstButton, 20); pb.Click(HudBrowser.PrevButton, 20);
        Check("First and Prev stop at the first page, Last jumps to the end", pb.Page == 0 && pb.Click(HudBrowser.LastButton, 20) && pb.Page == 1);
        pb.Page = 9;
        Check("A page past the end (the list shrank under a filter) is clamped to the last one", pb.PageOf(all).Count == 8 && pb.Page == 1);
        var hint = new HudBrowser();
        hint.Click("os_pg_next", all.Count);
        Check("Pages of twelve always: the search hint, and v4.8.0's search box and keys, move no page and change no page size (no keyboard covers a row any more)",
            !hint.Click(HudBrowser.HintButton, all.Count) && !hint.Click(HudBrowser.OldSearchBox, all.Count) && !hint.Click("os_kb_done", all.Count)
            && hint.Page == 1 && hint.PagesFor(all.Count) == 2 && hint.PageOf(all).Select(m => m.Arg).SequenceEqual(["13", "14", "15", "16", "17", "18", "19", "20"]));
        Check("Any sort or filter starts again on the first page", pb.Click("os_sort_name", 20) && pb.Page == 0);

        // ---- the menu's pages and its clicks ----
        var now = new DateTime(2026, 10, 7, 20, 0, 0, DateTimeKind.Utc);
        var live = new HudState { Key = "3070210812", RoundMinutes = 10, PrevArg = "2", PrevLabel = "#2 Zebra" };
        var mb = new HudBrowser();
        var (mt, mc) = HudMenu.Maps(back, live, mb, 1001, now);
        string MT(string id) => mt.Single(x => x.Id == id).Text;
        bool MC(string id, string cls) => mc.Single(x => x.Id == id && x.Class == cls).On;
        Check("Cards: rank, name, size, likes, the broken warning and what Play does (it puts the map to the vote) - on now, broken", MT("os_m1_rank") == "#1" && MT("os_m1_name") == "AIM Map One"
            && MT("os_m1_size") == "112 MB" && MT("os_m1_votes") == "+5  -1" && MT("os_m1_play_text") == "Put to the vote" && !MC("os_m1", "queued") && MC("os_m1", "liked")
            && MT("os_m2_warn") == "Broken: crashed 3/3 test loads" && MC("os_m2", "risky") && MC("os_m2", "disliked") && MT("os_m2_play_text") == HudMenu.Locked
            && MC("os_m3", "now") && MT("os_m3_play_text") == "On now" && MT("os_m4_rank") == "Official" && MT("os_m4_size") == "" && MT("os_m4_warn") == "Crashed the server 2x"
            && !MC("os_m5", "used") && MT("os_m5_name") == "" && MT("os_m_count") == "4 maps" && MT("os_pg_text") == "Page 1 of 1" && !MC("os_m_empty", "used"));
        var inVote = VoteState.Decode("mv\t1793700100\topen\t40\t2\t1\tmid\t1001\t1\nmvo\t1\tarmsrace\tkeep\t\t3070210812\tKeep playing this map\t#3 beta\t0\t\nmvo\t2\tarmsrace\tpicked\t1\t3084291314\t#1 AIM Map One\t112 MB\t1\tAlice\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t0\t\nmvv\t1001\t2\n");
        var waitVote = new VoteState { WaitFor = 45 };
        string PT(long account, VoteState? v, int k) => HudMenu.Maps(back, live, new HudBrowser(), account, now, vote: v).Texts.Single(x => x.Id == $"os_m{k}_play_text").Text;
        Check("...while a vote is open a card on the ballot says Your vote or On the vote (the cards have no numbers), the map that is on is Keep's card, any other Add to the vote; after a vote, the wait",
            PT(1001, inVote, 1) == "Your vote" && PT(1002, inVote, 1) == "On the vote" && PT(1002, inVote, 3) == "On the vote" && PT(1002, inVote, 4) == HudMenu.Locked && PT(1002, inVote, 5) == "" 
            && HudMenu.Maps(back, live, new HudBrowser(), 1002, now, vote: inVote).Classes.Single(x => x.Id == "os_m1" && x.Class == "queued").On
            && PT(1002, waitVote, 1) == "Vote in 45 s" && PT(1002, waitVote, 3) == "On now");
        Check("Card buttons say what the chat would: likes and Play by the map's rank or official name; the map that is on is Keep's vote while one is open, nothing outside one",
            HudMenu.Command("os_m1_up", back, live, mb, 1001, now) == "!up 1" && HudMenu.Command("os_m4_down", back, live, mb, 1001, now) == "!down ar_shoots"
            && HudMenu.Command("os_m1_play", back, live, mb, 1001, now) == "!map 1" && HudMenu.Command("os_m1_play", back, live, new HudBrowser(), 1002, now) == "!map 1"
            && HudMenu.Command("os_m3_play", back, live, mb, 1001, now) == null && HudMenu.Command("os_m3_play", back, live, mb, 1001, now, vote: inVote) == "!map 3"
            && HudMenu.Command("os_m5_play", back, live, mb, 1001, now) == null && HudMenu.Command("os_m9_play", back, live, mb, 1001, now) == null);
        var first = HudMenu.Command("os_m2_play", back, live, mb, 1001, now);
        var armed = HudMenu.Maps(back, live, mb, 1001, now.AddSeconds(5));
        var second = HudMenu.Command("os_m2_play", back, live, mb, 1001, now.AddSeconds(10));
        Check("A broken map: the first Play asks (and arms the bridge's own confirmation, this player's alone), the second within 30 s puts it to the vote",
            first == "!map 2" && armed.Classes.Single(x => x.Id == "os_m2" && x.Class == "confirm").On && armed.Texts.Single(x => x.Id == "os_m2_play_text").Text == HudMenu.Unlocked
            && second == "!map 2" && mb.ConfirmKey == "");
        HudMenu.Command("os_m2_play", back, live, mb, 1001, now);
        Check("...and after 30 s the next click asks again", HudMenu.Command("os_m2_play", back, live, mb, 1001, now.AddSeconds(31)) == "!map 2" && mb.ConfirmKey == "3070208661");
        var dc = new HudBrowser();
        var dc1 = HudMenu.Command("os_m2_play", back, live, dc, 1001, now);
        var dc2 = HudMenu.Command("os_m2_play", back, live, dc, 1001, now.AddSeconds(0.2));
        var stillAsks = dc.ConfirmKey == "3070208661" && dc.DoubledAt == now.AddSeconds(0.2)
            && HudMenu.Maps(back, live, dc, 1001, now.AddSeconds(0.3)).Texts.Single(x => x.Id == "os_m2_play_text").Text == HudMenu.Unlocked;
        var dc3 = HudMenu.Command("os_m2_play", back, live, dc, 1001, now.AddSeconds(0.6)); // 0.4 s after the click before it: the burst goes on
        var dc4 = HudMenu.Command("os_m2_play", back, live, dc, 1001, now.AddSeconds(1.2)); // after a pause of 0.6 s: the confirmation
        Check($"A broken map's double click opens the padlock and nothing more: a click within {HudVote.DoubleClickSeconds} s of the one before sends nothing and leaves the first click's ask standing, a burst too; a click after a pause that long puts it to the vote",
            dc1 == "!map 2" && dc2 == null && stillAsks && dc3 == null && dc4 == "!map 2" && dc.ConfirmKey == "");
        var plain = new HudBrowser();
        var prev = new HudBrowser();
        Check("...a plain map's double click sends its pick once, and a click on another card within it counts as ever; Previous's double click sends it once - never the confirmation of a risky previous map",
            HudMenu.Command("os_m1_play", back, live, plain, 1001, now) == "!map 1" && HudMenu.Command("os_m1_play", back, live, plain, 1001, now.AddSeconds(0.2)) == null
            && HudMenu.Command("os_m2_play", back, live, plain, 1001, now.AddSeconds(0.3)) == "!map 2"
            && HudMenu.Command(HudMenu.Previous, back, live, prev, 1001, now) == "!previous" && HudMenu.Command(HudMenu.Previous, back, live, prev, 1001, now.AddSeconds(0.2)) == null
            && HudMenu.Command(HudMenu.Previous, back, live, prev, 1001, now.AddSeconds(0.8)) == "!previous");
        var rs = new HudBrowser();
        Check($"Restart the server and Restart the match: a click within {HudVote.DoubleClickSeconds} s of the one before on the same sends nothing - a double click never confirms a restart; after a pause that long it does, and the other button within it counts as ever",
            HudMenu.Command(HudMenu.Rematch, back, live, rs, 1001, now) == "!rematch" && HudMenu.Command(HudMenu.Rematch, back, live, rs, 1001, now.AddSeconds(0.2)) == null && rs.DoubledAt == now.AddSeconds(0.2)
            && HudMenu.Command(HudMenu.Rematch, back, live, rs, 1001, now.AddSeconds(0.8)) == "!rematch"
            && HudMenu.Command(HudMenu.Restart, back, live, rs, 1001, now.AddSeconds(1)) == "!restart" && HudMenu.Command(HudMenu.Restart, back, live, rs, 1001, now.AddSeconds(1.3)) == null
            && HudMenu.Command(HudMenu.Rematch, back, live, rs, 1001, now.AddSeconds(1.4)) == "!rematch" && HudMenu.Command(HudMenu.Restart, back, live, rs, 1001, now.AddSeconds(2)) == "!restart");
        var (qt, qc) = HudMenu.PreviousButton(live);
        Check("Previous: the map before this one on its button, which puts it to the vote; off while there is none",
            qt.Single(x => x.Id == "os_q_prev_text").Text == "Previous: #2 Zebra" && !qc.Single(x => x.Id == HudMenu.Previous && x.Class == "off").On
            && HudMenu.Command(HudMenu.Previous, back, live, mb, 1001, now) == "!previous" && HudMenu.PreviousButton(new HudState()).Classes.Single(x => x.Id == HudMenu.Previous && x.Class == "off").On
            && HudMenu.Command(HudMenu.Previous, back, new HudState(), mb, 1001, now) == null);
        // ---- a confirmed action's countdown in its button, with its Cancel: the restarts, Previous, a risky map's Play ----
        var t0Ms = HudState.Ms(now);
        var dueState = new HudState { Map = live.Map, Key = live.Key, PrevArg = live.PrevArg, PrevLabel = live.PrevLabel,
            RestartKind = HudState.PlayerRestartKind, RestartWhen = "now", RestartAt = t0Ms / 1000 + 7, RestartBy = "Alice", RematchAtMs = t0Ms + 4000, RematchBy = "Bob" };
        dueState.PicksDue.Add(("3070208661", "Cara", t0Ms + 6000, false)); // card 2's map, the broken one
        dueState.PicksDue.Add(("prev-map", "Dan", t0Ms + 9000, true));
        var asNow = dueState.AsOf(t0Ms);
        var asBars = dueState.AsOf(t0Ms + HudVote.BarLeadMs);
        var (pgt, pgc) = HudMenu.Game(asNow, 0, 0, asBars);
        string GT(string id) => pgt.Single(x => x.Id == id).Text;
        bool GC(string id, string cls) => pgc.Single(x => x.Id == id && x.Class == cls).On;
        Check($"A confirmed restart counts down in its button ({HudMenu.PendingSeconds} s, the bridge's PressedSeconds): the seconds left, Click to cancel and who pressed it, class pending, the line's step a little ahead; a click there is its Cancel",
            GT("os_set_restart_text") == "Restarting in 7 s" && GT("os_set_restart_sub") == "Click to cancel - Pressed by Alice" && GC(HudMenu.Restart, "pending") && !GC(HudMenu.Restart, "armed")
            && GT("os_set_rematch_text") == "Match restarts in 4 s" && GT("os_set_rematch_sub") == "Click to cancel - Pressed by Bob" && GC(HudMenu.Rematch, "pending")
            && GC(HudMenu.Restart, HudVote.CountdownStep(asBars.PlayerRestartIn(), HudMenu.PendingSeconds)) && GC(HudMenu.Rematch, HudVote.CountdownStep(asBars.PlayerRematchIn(), HudMenu.PendingSeconds))
            && HudMenu.PendingSeconds == PressedSeconds && PickCountdownSeconds == PressedSeconds
            && HudMenu.Command(HudMenu.Restart, back, asNow, new HudBrowser(), 1, now) == "!cancel restart" && HudMenu.Command(HudMenu.Rematch, back, asNow, new HudBrowser(), 1, now) == "!cancel rematch");
        var fair = new HudState { RematchAtMs = t0Ms + 4000, RematchBy = "Eve", RematchFair = true, RestartKind = "daily", RestartWhen = "now", RestartAt = t0Ms / 1000 + 30 }.AsOf(t0Ms);
        var (fgt, fgc) = HudMenu.Game(fair, 0);
        Check("...the fairness restart and the watcher's restart are no button's: no countdown in the buttons, and a click there asks as ever",
            !fgc.Single(x => x.Id == HudMenu.Restart && x.Class == "pending").On && !fgc.Single(x => x.Id == HudMenu.Rematch && x.Class == "pending").On
            && fgt.Single(x => x.Id == "os_set_rematch_text").Text == "Restart the match" && HudMenu.Command(HudMenu.Rematch, back, fair, new HudBrowser(), 1, now) == "!rematch");
        var (pqt, pqc) = HudMenu.PreviousButton(asNow, asBars);
        Check("...Previous's confirmed pick counts down on Previous: the map, the seconds, Cancel; pending and its step; a click there is its Cancel",
            pqt.Single(x => x.Id == "os_q_prev_text").Text == "Previous: #2 Zebra in 9 s - Cancel" && pqc.Single(x => x.Id == HudMenu.Previous && x.Class == "pending").On
            && pqc.Single(x => x.Id == HudMenu.Previous && x.Class == HudVote.CountdownStep(asBars.PreviousPickIn(), HudMenu.PendingSeconds)).On
            && HudMenu.Command(HudMenu.Previous, back, asNow, new HudBrowser(), 1001, now) == "!cancel previous");
        var pm = HudMenu.Maps(back, asNow, new HudBrowser(), 1001, now, bars: asBars);
        Check("...a risky map's confirmed pick counts down on its card's Play, for everybody: the seconds and Cancel, pending on the card, the step on the Play; a click there is its Cancel - and no other card",
            pm.Texts.Single(x => x.Id == "os_m2_play_text").Text == HudMenu.CardPending(6) && pm.Classes.Single(x => x.Id == "os_m2" && x.Class == "pending").On
            && pm.Classes.Single(x => x.Id == "os_m2_play" && x.Class == HudVote.CountdownStep(asBars.PickIn("3070208661"), HudMenu.PendingSeconds)).On
            && !pm.Classes.Single(x => x.Id == "os_m1" && x.Class == "pending").On && pm.Texts.Single(x => x.Id == "os_m1_play_text").Text != HudMenu.CardPending(6)
            && HudMenu.Command("os_m2_play", back, asNow, new HudBrowser(), 1001, now) == "!cancel pick 3070208661" && HudMenu.Command("os_m1_play", back, asNow, new HudBrowser(), 1001, now) == "!map 1"
            && HudState.Decode(dueState.Encode())!.PicksDue.SequenceEqual(dueState.PicksDue) && new HudState().PickIn("x") == -1 && dueState.PickIn("3070208661") == HudMenu.PendingSeconds);
        var (gt, gc) = HudMenu.Game(live, 0);
        Check("Game page: the match length that is set is lit, and the buttons say !time, !restart and !rematch",
            gc.Single(x => x.Id == HudMenu.Time10).On && !gc.Single(x => x.Id == HudMenu.Time60).On && HudMenu.Command(HudMenu.Time60, back, live, mb, 1, now) == "!time 60"
            && HudMenu.Command(HudMenu.Restart, back, live, mb, 1, now) == "!restart" && gt.Single(x => x.Id == "os_set_restart_text").Text == "Restart the server"
            && !gc.Single(x => x.Id == HudMenu.Restart && x.Class == "armed").On
            && HudMenu.Command(HudMenu.Rematch, back, live, mb, 1, now) == "!rematch" && gt.Single(x => x.Id == "os_set_rematch_text").Text == "Restart the match");
        var armedGame = Enumerable.Range(1, 30).Select(n => HudMenu.Game(live, n)).ToList();
        Check("Restart, after its first click: \"Press again within N s\", counting down every second from 30, armed (content v3.2.0 blinks it) - and back to normal at 0",
            armedGame.Select((g, i) => g.Texts.Single(x => x.Id == "os_set_restart_text").Text == $"Press again within {i + 1} s" && g.Classes.Single(x => x.Id == HudMenu.Restart && x.Class == "armed").On).All(x => x)
            && HudBrowser.ConfirmSeconds == 30 && PickConfirmSeconds == 30);
        var menuCommands = new[] { "os_m1_up", "os_m1_down", "os_m1_play", "os_m4_up", HudMenu.Previous, HudMenu.Time10, HudMenu.Time60, HudMenu.Restart, HudMenu.Rematch }
            .Select(id => HudMenu.Command(id, back, live, new HudBrowser(), 1001, now)).Where(x => x != null).ToList();
        Check($"Every command a menu click can send is one the bridge takes from the HUD ({menuCommands.Count})", menuCommands.All(x => Hud.IsAction(x!)));

        // ---- the layout: every id the plugin addresses exists in it, every label in it is fed, every class has a rule ----
        if (File.Exists(HudLayoutFile) && File.Exists(HudStyleFile))
        {
            var xml = File.ReadAllText(HudLayoutFile);
            var css = File.ReadAllText(HudStyleFile);
            var ids = Regex.Matches(xml, @"\bid=""([^""]+)""").Select(x => x.Groups[1].Value).ToList();
            var fed = Regex.Matches(xml, @"<Label\b[^>]*\bid=""([^""]+)""[^>]*text=""\{s:text\}""").Select(x => x.Groups[1].Value)
                .Concat(Regex.Matches(xml, @"<Panel id=""(os_chat_\d+)"" class=""os-chat-line"" hittest=""false""><Panel hittest=""false"" /><Label hittest=""false"" text=""\{s:text\}"" /></Panel>").Select(x => x.Groups[1].Value))
                .ToHashSet(); // a chat line's words are its own panel's text, which its one label shows
            var full = new HudState { Top = [.. Enumerable.Repeat(("m", 1, 0), 5)], ChangeIn = 1, PrevArg = "1", PrevLabel = "#1 m" };
            var menuMaps = HudMenu.Maps(back, full, new HudBrowser(), 1001, now);
            var menuQueue = HudMenu.PreviousButton(full);
            var menuGame = HudMenu.Game(full, 30);
            var ballotView = HudVote.Cards(inVote, 1001, back); // the ballot's page: its texts and classes land on the same cards
            var card = new WorkshopCard();
            card.Show(3232901690);
            var stock = Fun.Settings.SelectMany(s => s.Stock).GroupBy(x => x.ConVar).ToDictionary(g => g.Key, g => g.First().Value);
            var funPage = HudMenu.FunPage(cv => stock.GetValueOrDefault(cv));
            var botsPage = HudBots.Page(cv => stock.GetValueOrDefault(cv), Modes.Deathmatch);
            var chatLog = new HudChat();
            chatLog.Say("Alice", "CT", "gg", teamOnly: false);
            var chatSent = HudChat.Column(chatLog.For(1, "CT")); // the chat column (content v4.14.0): the frame draws it for every player
            var sentTexts = HudView.StaticTexts().Concat(HudView.Texts(full)).Concat(HudView.PlayerTexts(full, 1, "x", true)).Concat(menuMaps.Texts).Concat(menuQueue.Texts)
                .Concat(menuGame.Texts).Concat(card.Texts()).Concat(funPage.Texts).Concat(botsPage.Texts).Concat(ballotView.Texts).Concat(HudView.LatelyTexts(full, 1, 0)).Select(t => t.Id).Append("os_rst_secs").Append("os_cu_head")
                .Concat(Enumerable.Range(1, 6).Select(k => $"os_cu_{k}")).Concat(HudMenu.RecentWords(["dust"]).Texts.Concat(HudMenu.PopularWords(["aim"]).Texts).Select(t => t.Id))
                .Concat(chatSent.Texts.Select(x => x.Id)).ToList();
            List<(string Id, string Class, bool On)> sentClasses = HudView.StaticClasses().Concat(HudView.Classes(full)).Concat(HudView.PlayerClasses(full, 1)).Concat(menuMaps.Classes).Concat(menuQueue.Classes)
                .Concat(menuGame.Classes).Concat(card.Classes()).Concat(funPage.Classes).Concat(botsPage.Classes).Concat(ballotView.Classes).Concat(HudView.LatelyClasses(full, 1))
                .Concat(Enumerable.Range(1, 6).SelectMany(k => new[] { ($"os_cu_{k}", "used", true), ($"os_cu_{k}", "good", true), ($"os_cu_{k}", "bad", false) }))
                .Concat(HudMenu.RecentWords(["dust"]).Classes).Concat(HudMenu.PopularWords(["aim"]).Classes).Append((HudView.Screen, "qhelp", true))
                .Concat(chatSent.Classes).Concat([(HudView.TabGame, "off", true), (HudDash.Tab, "off", true), (HudVote.IdleBar, "snap", true)]).ToList(); // the frame's own (content v4.14.0)
            string[] screenClasses = ["win", "win-in", "draining", "toast-1", "toast-in", "vcheck", "vjoin", "vbrowse", "vlock", "wback", "cu", "cu-in", "cu-run", "chaton", "hb", "meos",
                .. Enum.GetValues<HudSlots.Page>().Select(p => "page-" + p.ToString().ToLowerInvariant())];
            // the buttons this content has, every one the plugin listens to
            var buttons = HudView.Tabs.Select(t => t.Id).Concat([HudView.ButtonClose, HudView.ButtonUp, HudView.ButtonDown, HudView.ButtonAct])
                .Concat([HudVersion.Button(HudVersion.Content), HudVote.RandomButton, HudVote.BrowseButton, HudVote.BackButton, HudMenu.FunRandom, HudMenu.FunDefault,
                    HudBrowser.OursButton])
                .Concat(Enumerable.Range(1, DownReasons.All.Length).Select(HudVote.WhyButton))
                .Concat(HudBrowser.ModeButtons.Select(b => b.Id)).Concat(HudDash.LayoutButtons)
                .Concat(Enumerable.Range(1, Fun.Rows).SelectMany(r => Enumerable.Range(1, Fun.ChoicesPerRow).Select(c => HudMenu.FunButton(r, c))))
                .Concat(Enumerable.Range(1, Bots.Rows).SelectMany(r => Enumerable.Range(1, Bots.ChoicesPerRow).Select(c => HudBots.Button(r, c))))
                .Concat(Enumerable.Range(1, Bots.PresetCount).Select(HudBots.Preset)).Concat([HudBots.Kick, HudBots.Default])
                .Concat(HudBrowser.SortButtons.Select(x => x.Id)).Concat(HudBrowser.BandButtons.Select(x => x.Id))
                .Concat([HudBrowser.LikedButton, HudBrowser.OfficialButton, HudBrowser.ClearButton, HudBrowser.FirstButton, HudBrowser.PrevButton, HudBrowser.NextButton, HudBrowser.LastButton])
                .Concat(Enumerable.Range(1, HudMenu.Cards).SelectMany(k => new[] { $"os_m{k}_play", $"os_m{k}_up", $"os_m{k}_down" }))
                .Concat([HudMenu.Previous, HudMenu.Time10, HudMenu.Time60, HudMenu.Restart, HudMenu.BoardOpen])
                .Concat([HudView.BoardUp, HudView.BoardDown, HudView.BoardNext, HudBrowser.HintButton, WorkshopCard.Play])
                .Concat(HudBrowser.RecentButtons).Concat(HudBrowser.PopularButtons).ToList();
            var thumbPanels = HudMenu.Thumbs(back, new HudBrowser(), 1001).Select(x => x.Panel).ToList();
            Check($"Layout: the twelve cards' pictures exist ({thumbPanels.Count})", thumbPanels.Count == HudMenu.Cards && thumbPanels.All(ids.Contains));
            Check($"Layout: no id twice ({ids.Count} ids), and the root panel has none (the resource compiler refuses it)",
                ids.Count == ids.Distinct().Count() && !Regex.IsMatch(xml, @"<root>\s*<styles>.*?</styles>\s*<Panel\b[^>]*\bid=", RegexOptions.Singleline));
            var unknownText = sentTexts.Where(id => !fed.Contains(id)).Distinct().ToList();
            Check($"Layout: every text the plugin sends lands on a {{s:text}} label ({sentTexts.Distinct().Count()}){(unknownText.Count > 0 ? ": missing " + string.Join(", ", unknownText) : "")}", unknownText.Count == 0);
            var unfed = fed.Where(id => !sentTexts.Contains(id)).ToList();
            Check($"Layout: every {{s:text}} label is fed by the plugin ({fed.Count}){(unfed.Count > 0 ? ": never set " + string.Join(", ", unfed) : "")}", unfed.Count == 0);
            var unknownPanels = sentClasses.Select(c => c.Id).Concat([HudView.Global, HudView.Screen]).Concat(buttons).Where(id => !ids.Contains(id)).Distinct().ToList();
            Check($"Layout: every panel the plugin sets a class on, and every button it listens to, exists - nothing goes to a panel this content lacks, so its game's console stays quiet{(unknownPanels.Count > 0 ? ": missing " + string.Join(", ", unknownPanels) : "")}", unknownPanels.Count == 0);
            Check("Layout: the buttons are Buttons", buttons.All(b => Regex.IsMatch(xml, $@"<Button\b[^>]*\bid=""{b}""")));
            var classes = sentClasses.Select(c => c.Class).Concat(screenClasses).Distinct().ToList();
            var unstyled = classes.Where(c => !Regex.IsMatch(css, $@"\.{Regex.Escape(c)}\b")).ToList();
            Check($"Stylesheet: every class the plugin sets has a rule ({classes.Count}){(unstyled.Count > 0 ? ": none for " + string.Join(", ", unstyled) : "")}", unstyled.Count == 0);
            Check("Layout: only Panel, Label, Image and Button (anything else makes the host refuse the whole layout)",
                Regex.Matches(xml, @"<([A-Za-z]+)\b").Select(x => x.Groups[1].Value).All(t => t is "root" or "styles" or "include" or "Panel" or "Label" or "Image" or "Button"));
            Check("Layout: no inline style attribute and no scaling attribute (each silently kills the layout in CS2)", !Regex.IsMatch(xml, @"\s(style|scaling)="));
            // Valve's whitelist (point_script.d.ts, CustomHudLayout): Panel id/class/hittest, Label id/class/hittest/text, Image
            // id/class/hittest/src, Button id/class. The client refuses the WHOLE layout for one attribute outside it ("Layout contains
            // disallowed attribute %s for panel type '%s'") - v2.1.0 had hittest on a Button and rendered nothing at all.
            var allowed = new Dictionary<string, string[]> { ["Panel"] = ["id", "class", "hittest"], ["Label"] = ["id", "class", "hittest", "text"],
                ["Image"] = ["id", "class", "hittest", "src"], ["Button"] = ["id", "class"] };
            var outside = Regex.Matches(Regex.Replace(xml, "<!--.*?-->", "", RegexOptions.Singleline), @"<(Panel|Label|Image|Button)\b([^>]*)>")
                .SelectMany(m => Regex.Matches(m.Groups[2].Value, @"([\w-]+)\s*=").Select(a => (Tag: m.Groups[1].Value, Attr: a.Groups[1].Value)))
                .Where(x => !allowed[x.Tag].Contains(x.Attr)).Select(x => $"{x.Attr} on {x.Tag}").Distinct().ToList();
            Check($"Layout: every attribute is on Valve's list for its panel type{(outside.Count > 0 ? ": not " + string.Join(", ", outside) : "")}", outside.Count == 0);
            var rules = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline); // comments may say anything
            Check("Stylesheet: every @keyframes name is quoted, and no comma selector (each silently kills the layout)",
                !Regex.IsMatch(rules, @"@keyframes\s+[^'\s]") && !Regex.IsMatch(rules, @"[^{};]*,[^{};]*\{"));
            Check("Layout: it includes the compiled stylesheet by its s2r:// path", xml.Contains($"s2r://panorama/styles/custom_game/gungame_hud_{Hud.ReleaseFile}.vcss_c"));
            Check($"Stylesheet: the drain bar runs out before the idle close ({HudSlots.IdleCloseSeconds} s): its {HudSlots.DrainSeconds} s, from up to {HudSlots.DrainAfterOpenSeconds} s after the open, with half a second to spare",
                Regex.IsMatch(rules, $@"\.os-drain-fill\s*\{{[^}}]*transition-duration:\s*{HudSlots.DrainSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}s;") && Regex.IsMatch(rules, @"\.draining \.os-drain-fill\s*\{[^}]*scaleX\( 0 \);[^}]*transition-duration:\s*0s;")
                && HudSlots.DrainAfterOpenSeconds + HudSlots.DrainSeconds <= HudSlots.IdleCloseSeconds - 0.5 && Regex.IsMatch(rules, @"(?m)^\.os-cu-fill\s*\{[^}]*transition-duration:\s*4\.5s;")
                && Regex.IsMatch(rules, @"(?m)^\.os-mlock-fill\s*\{[^}]*transition-duration:\s*29\.5s;") && HudBrowser.ConfirmSeconds - 29.5 >= 0.5);

            // ---- the shipped contents: the plugin draws for the contents from HudVersion.Oldest on, v4.8.0 - the first whose panels are named os_ - and newer;
            //      a game holding an older one, whose layout names its panels gg_, gets nothing. hud/reference/ keeps the shipped layouts these checks read and no
            //      other: the ones still drawn for and the one this content replaces. A missing one fails; every older layout is in git ----
            var shippedContents = HudVersion.Contents.SkipLast(1).Select(c => c.Version).ToArray(); // the table's lines but this content's
            static string ReferenceFile(string content) => $"../hud/reference/gungame_hud_{content}.xml";
            var replaced = shippedContents[^1];
            var inUseContents = HudVersion.Contents.SkipLast(1).Where(c => c.Level >= HudVersion.Oldest).Select(c => c.Version).ToArray(); // the shipped contents still drawn for
            var readContents = inUseContents.Append(replaced).ToHashSet();
            var kept = Directory.GetFiles("../hud/reference", "gungame_hud_*.xml").Select(f => Path.GetFileNameWithoutExtension(f)["gungame_hud_".Length..]).ToHashSet();
            Check($"The plugin draws for content {HudVersion.OfLevel(HudVersion.Oldest)} and newer, never for one that names its panels gg_; hud/reference/ holds the shipped layouts these checks read ({string.Join(", ", readContents.Order(StringComparer.Ordinal))}) and no other{(kept.SetEquals(readContents) ? "" : $": it holds {string.Join(" ", kept.Order(StringComparer.Ordinal))}")}",
                HudVersion.Oldest >= HudVersion.LevelOf("v4.8.0") && kept.SetEquals(readContents)
                && inUseContents.All(c => File.Exists(ReferenceFile(c)) && Regex.Matches(File.ReadAllText(ReferenceFile(c)), @"\bid=""([^""]+)""").All(m => m.Groups[1].Value.StartsWith("os_", StringComparison.Ordinal))));
            if (File.Exists(ReferenceFile(replaced))) // else the check above has failed
            {
                // a shipped layout's ids as this content names them: gg_ in the layouts before v4.8.0, os_ from it on
                static HashSet<string> IdsOf(string file) => Regex.Matches(File.ReadAllText(file), @"\bid=""([^""]+)""").Select(x => x.Groups[1].Value)
                    .Select(id => id.StartsWith("gg_", StringComparison.Ordinal) ? "os_" + id[3..] : id).ToHashSet();
                Check($"Content {HudVersion.Content}: every id begins os_ and every class os-, and no gg_ name is left in it: a game holding a content before it finds no panel of ours",
                    Regex.Matches(xml, @"\bid=""([^""]+)""").All(m => m.Groups[1].Value.StartsWith("os_", StringComparison.Ordinal))
                    && !Regex.IsMatch(Regex.Replace(xml, "<!--.*?-->", "", RegexOptions.Singleline) + Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline), @"(?<![A-Za-z0-9])gg[-_]"));
                var last = IdsOf(ReferenceFile(replaced));
                var sent = sentTexts.Concat(sentClasses.Select(c => c.Id)).Concat([HudView.Global, HudView.Screen]).Distinct().ToList();
                var retired = last.Where(id => !ids.Contains(id)).ToHashSet();
                var added = ids.Where(id => !last.Contains(id)).ToHashSet();
                // The panel v4.14.0 retires: v4.13.0's version button, whose place this content's own takes. Every other panel keeps its id - the old sub-tabs' buttons
                // are tabs now, or out of sight under their ids.
                var expectedRetired = new HashSet<string> { HudVersion.Button(replaced) };
                Check($"Content {HudVersion.Content}: it keeps every panel of {replaced} ({last.Count}), by its id, but the {retired.Count} it retires, each for a reason (the version button of {replaced}){(retired.SetEquals(expectedRetired) ? "" : ": unexpected " + string.Join(" ", retired.Except(expectedRetired).Concat(expectedRetired.Except(retired).Select(x => "-" + x))))}",
                    retired.SetEquals(expectedRetired));
                Check($"Content {HudVersion.Content}: nothing the plugin sends goes to a panel this content lacks - the retired ones neither - and the Previous button stays (on the Maps page); the cards' numbers and the chat column go to this content's games and to no game known to hold one before them (HudVote.NumbersLevel, HudChat.Level)",
                    sent.All(ids.Contains) && ids.Contains(HudMenu.Previous) && ids.Contains("os_q_prev_text")
                    && !HudVersion.IsBefore(HudVersion.Content, HudVote.NumbersLevel) && !HudVersion.IsBefore(HudVersion.Content, HudChat.Level) && HudVersion.IsBefore(replaced, HudChat.Level)
                    && Enumerable.Range(1, HudChat.Lines).All(k => !last.Contains(HudChat.Line(k))));
                var addedIdle = added.Where(id => !(sent.Contains(id) || buttons.Contains(id) || id.EndsWith("_thumb") || id is "os_vidle" or "os_vlim")).ToList();
                // ...its own version button, the chat column's lines (os_chat_<k>, a line's words its own text), the browser's eleventh and twelfth cards (os_m11, os_m12
                // and their parts) and the download's bar (os_vdl).
                var wordButtons = HudBrowser.RecentButtons.Concat(HudBrowser.PopularButtons).ToList();
                var expectedAdded = new HashSet<string>(Enumerable.Range(1, HudChat.Lines).Select(HudChat.Line).Concat(ids.Where(id => Regex.IsMatch(id, @"^os_m1[12](_|$)")))) { HudVersion.Button(HudVersion.Content), HudVote.DownloadBar };
                Check($"Content {HudVersion.Content}: it adds {added.Count} panel(s) - its own version button, the chat column's {HudChat.Lines} lines, the browser's eleventh and twelfth cards, and the download's bar - and every one is fed or listened to{(addedIdle.Count > 0 ? ": not " + string.Join(" ", addedIdle) : "")}{(added.SetEquals(expectedAdded) ? "" : ": unexpected " + string.Join(" ", added.Except(expectedAdded).Concat(expectedAdded.Except(added).Select(x => "-" + x))))}",
                    added.SetEquals(expectedAdded) && addedIdle.Count == 0);
                var noTakeBack = Regex.Replace(xml, "<!--.*?-->", "", RegexOptions.Singleline);
                Check($"Content {HudVersion.Content}: a vote is never taken back - no CLICK TO TAKE BACK on your own card's badge, and no hover that swaps the badge's words",
                    !noTakeBack.Contains("os-mmine-back", StringComparison.Ordinal) && !css.Contains("os-mmine-back", StringComparison.Ordinal) && !Regex.IsMatch(css, @":hover \.os-mmine")
                    && Regex.Matches(noTakeBack, @"<Panel class=""os-mmine"" hittest=""false""><Panel class=""os-mtick"" /><Label class=""os-mmine-text"" text=""Your vote"" /></Panel>").Count == HudMenu.Cards);
                Check($"Content {HudVersion.Content}: a confirmed action's countdown in its button - Restart the server, Restart the match, Previous and every card's Play hold a line that shows while pending and drains in steps w10 ... w0, a second each",
                    new[] { "os_set_restart", "os_set_rematch" }.All(b => Regex.IsMatch(noTakeBack, $@"<Button id=""{b}"" class=""os-restart"">.*?<Panel class=""os-pend"" hittest=""false""><Panel class=""os-pend-fill"" /></Panel>\s*</Button>", RegexOptions.Singleline))
                    && noTakeBack.Contains("<Button id=\"os_q_prev\" class=\"os-qprev\"><Label id=\"os_q_prev_text\" text=\"{s:text}\" /><Panel class=\"os-pend\" hittest=\"false\"><Panel class=\"os-pend-fill\" /></Panel></Button>", StringComparison.Ordinal)
                    && Regex.Matches(noTakeBack, @"<Panel class=""os-mlock-line""><Panel class=""os-mlock-fill"" /></Panel><Panel class=""os-pend"" hittest=""false""><Panel class=""os-pend-fill"" /></Panel>").Count == HudMenu.Cards
                    && new[] { ".os-restart.pending .os-pend", ".os-qprev.pending .os-pend", ".os-mcard.pending .os-pend" }.All(r => Regex.IsMatch(css, $@"(?m)^{Regex.Escape(r)}\s*\{{[^}}]*height:\s*3px;[^}}]*opacity:\s*1;"))
                    && Regex.IsMatch(css, @"(?m)^\.os-pend\s*\{[^}]*height:\s*0px;[^}]*opacity:\s*0;") && !Regex.IsMatch(css, @"(?m)^\.os-pend\s*\{[^}]*visibility:\s*collapse;") // resting in the layout, never collapsed (v4.12.1)
                    && new[] { ".os-qprev", ".os-mcard" }.All(p => Regex.IsMatch(css, $@"(?m)^{Regex.Escape(p)} \.os-pend\s*\{{\s*height:\s*3px;\s*opacity:\s*1;\s*background-color:\s*#FFFFFF01;")
                        && Regex.IsMatch(css, $@"(?m)^{Regex.Escape(p)}\.pending \.os-pend\s*\{{[^}}]*background-color:\s*#FFFFFF1F;")
                        && Regex.IsMatch(css, $@"(?m)^{Regex.Escape(p)} \.os-pend-fill\s*\{{\s*background-color:\s*#FFB4A601;")
                        && Regex.IsMatch(css, $@"(?m)^{Regex.Escape(p)}\.pending \.os-pend-fill\s*\{{\s*background-color:\s*#FFB4A6;")) // a card's and Previous's line stay drawn at rest, clear by their colours (v4.12.2)
                    && Enumerable.Range(0, 11).All(k => Regex.IsMatch(css, $@"(?m)^\.pending\.w{k} \.os-pend-fill\s*\{{\s*transform:\s*scaleX\(\s*{(k / 10.0).ToString(System.Globalization.CultureInfo.InvariantCulture)}\s*\);\s*transition-duration:\s*1s;")
                        && Regex.IsMatch(css, $@"(?m)^\.pending \.os-mplay\.w{k} \.os-pend-fill\s*\{{\s*transform:\s*scaleX\(\s*{(k / 10.0).ToString(System.Globalization.CultureInfo.InvariantCulture)}\s*\);\s*transition-duration:\s*1s;"))
                    && Regex.IsMatch(css, @"(?m)^\.os-pend-fill\s*\{[^}]*transition-duration:\s*0s;")); // the start snaps to its first step: the 0 s of the state it leaves
                Check($"Content {HudVersion.Content}: Wingman's player count on every card's picture, beside its mode's label - only while the card says one (sided), and never on the ballot",
                    Enumerable.Range(1, HudMenu.Cards).All(k => Regex.IsMatch(noTakeBack, $@"<Label class=""os-mmode os-mmode-wingman"" text=""Wingman"" /><Label id=""{HudMenu.Card(k)}_side"" class=""os-mside"" text=""\{{s:text\}}"" />"))
                    && Regex.IsMatch(css, @"(?m)^\.os-mside\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(css, @"(?m)^\.os-mcard\.sided \.os-mside\s*\{\s*visibility:\s*visible;")
                    && Regex.IsMatch(css, @"(?m)^\.os-screen\.voting \.os-mcard\.sided \.os-mside\s*\{\s*visibility:\s*collapse;"));
                Check($"Content {HudVersion.Content}: no on-screen keyboard - no key, no panel or rule of one, and no class for it on the player's screen - and the search area is the hint and the clear button",
                    !ids.Any(id => id.StartsWith("os_kb_", StringComparison.Ordinal)) && !ids.Contains(HudBrowser.OldSearchBox) && !Regex.IsMatch(xml + css, @"os-kbd|os-key\b|\.kbd\b")
                    && Regex.IsMatch(xml, @"<Panel id=""os_q_search"" class=""os-search"">\s*<Button id=""os_q_how"">") && ids.Contains(HudBrowser.ClearButton)
                    && !sentClasses.Any(c => c.Class == "kbd"));
                Check($"Content {HudVersion.Content}: the match length's row (SERVER, Game) shows in every mode, and its buttons say the mode's own count: minutes, in Wingman rounds ({MatchLength.ShortRounds} and {MatchLength.LongRounds})",
                    !Regex.IsMatch(css, @"\.os-global\.mode-[a-z]+ \.os-set-time\s*\{") && Regex.IsMatch(xml, @"<Panel class=""os-set os-set-time"">")
                    && xml.Contains($"<Label class=\"os-setb-text os-setb-min\" text=\"{MatchLength.ShortMinutes} minutes\" /><Label class=\"os-setb-text os-setb-rounds\" text=\"{MatchLength.ShortRounds} rounds\" />", StringComparison.Ordinal)
                    && xml.Contains($"<Label class=\"os-setb-text os-setb-min\" text=\"{MatchLength.LongMinutes} minutes\" /><Label class=\"os-setb-text os-setb-rounds\" text=\"{MatchLength.LongRounds} rounds\" />", StringComparison.Ordinal)
                    && Regex.IsMatch(css, @"(?m)^\.os-setb-rounds\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(css, @"\.os-global\.mode-wingman \.os-setb-min\s*\{\s*visibility:\s*collapse;")
                    && Regex.IsMatch(css, @"\.os-global\.mode-wingman \.os-setb-rounds\s*\{\s*visibility:\s*visible;"));
                var noCommentsXml = Regex.Replace(xml, "<!--.*?-->", "", RegexOptions.Singleline);
                Check($"Content {HudVersion.Content}: your own vote on the ballot - a badge on every card's Play, white on the card your vote is on (confirm); no grey for a map's fit (no player limit: unfit is gone)",
                    Regex.Matches(noCommentsXml, @"<Panel class=""os-mmine"" hittest=""false""><Panel class=""os-mtick"" />").Count == HudMenu.Cards
                    && Regex.IsMatch(css, @"\.os-screen\.voting \.os-mcard\.confirm \.os-mmine\s*\{") && !css.Contains(".unfit", StringComparison.Ordinal));
                var rules0 = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
                string[] helped = [".os-search", ".os-mbar-words", ".os-mbar-filters"];
                Check($"Content {HudVersion.Content}: the search helped - after a click on its area (qhelp) and while the player's chat is open (HUD_CHAT_VISIBLE), on MAPS > Browse alone: the box lit, the words row in the filters' place, a pointer beside the chat line, and nothing of it on the ballot; no lift - MAPS stands right of the chat",
                    helped.All(x => css.Contains($".os-screen.qhelp {x}", StringComparison.Ordinal) && css.Contains($".HUD_CHAT_VISIBLE .os-screen.page-maps {x}", StringComparison.Ordinal))
                    && !Regex.IsMatch(rules0, @"translateY\(\s*-40px\s*\)") && !rules0.Contains(".os-screen.win-in.qhelp .os-win", StringComparison.Ordinal) && !rules0.Contains(".HUD_CHAT_VISIBLE .os-screen.win-in.page-maps", StringComparison.Ordinal)
                    && Regex.IsMatch(css, @"\.HUD_CHAT_VISIBLE \.os-screen\.page-maps\.voting \.os-mbar-words\s*\{\s*visibility:\s*collapse;")
                    && Regex.IsMatch(css, @"\.HUD_CHAT_VISIBLE \.os-screen\.win\.page-maps\.voting \.os-chatptr\s*\{\s*visibility:\s*collapse;")
                    && css.Contains(".os-screen.win.qhelp .os-chatptr", StringComparison.Ordinal) && Regex.IsMatch(noCommentsXml, @"<Panel class=""os-chatptr"" hittest=""false"">")
                    && Regex.IsMatch(noCommentsXml, @"<Panel class=""os-mbar os-mbar-words"">") && wordButtons.All(b => Regex.IsMatch(noCommentsXml, $@"<Button id=""{b}"" class=""os-chipb os-qchip""><Label id=""{b}_text"" class=""os-chipb-text"" text=""\{{s:text\}}"" /></Button>"))
                    && noCommentsXml.Contains("<Label class=\"os-search-typing\" text=\"Type a map name, then press Enter\" />", StringComparison.Ordinal));
                // ---- content v4.13.0: MAPS right of CS2's chat (x 18-571 at every aspect), the ballot four across and three down with the vote's own numbers, its state,
                //      how to vote and one clock ----
                Check($"Content {HudVersion.Content}: every page docks right of CS2's chat - right-aligned 80 px from the edge at 16:9 (its 1,240 px from x 600), scaled to fit at 16:10 and 4:3, in the room right of the chat at 21:9, clear of the chat column's x 579; no page moves the window, 920 high on every one",
                    Regex.IsMatch(rules0, @"(?m)^\.os-screen \.os-win\s*\{\s*horizontal-align:\s*right;\s*margin-right:\s*80px;") && Regex.IsMatch(rules0, @"(?m)^\.os-win\s*\{[^}]*width:\s*1240px;\s*height:\s*920px;")
                    && Regex.IsMatch(rules0, @"(?m)^\.AspectRatio16x10 \.os-screen \.os-win\s*\{\s*ui-scale:\s*90%;\s*margin-right:\s*8px;") && Regex.IsMatch(rules0, @"(?m)^\.AspectRatio4x3 \.os-screen \.os-win\s*\{\s*ui-scale:\s*67%;")
                    && Regex.IsMatch(rules0, @"(?m)^\.AspectRatio21x9 \.os-screen \.os-win\s*\{\s*margin-right:\s*360px;") && !Regex.IsMatch(rules0, @"\.os-screen\.voting \.os-win\s*\{")
                    && !Regex.IsMatch(rules0, @"\.os-screen\.page-\w+ \.os-win\s*\{[^}]*(horizontal-align|margin)")
                    && 1920 - 80 - 1240 >= 579 + 20 && 1728 - 8 - 0.90 * 1240 >= 579 + 20 && 1440 - 10 - 0.67 * 1240 >= 579 + 20);
                Check($"Content {HudVersion.Content}: the ballot's tiles four across and three down - 4 x 285 + 3 x 10 in its 1,172 px, the fourth and eighth without the margin to wrap on - each with the vote's own number on its picture (os_m<k>_num), never in the browser",
                    Regex.IsMatch(rules0, @"(?m)^\.os-screen\.voting \.os-mcard\s*\{\s*width:\s*285px;\s*height:\s*181px;\s*margin:\s*0px 10px 8px 0px;") && 4 * 285 + 3 * 10 <= 1172 && 3 * (181 + 8) + 34 + 8 + 20 + 10 + 34 <= 920 - 3 - 76 - 49 - 45 - 28
                    && new[] { 4, 8 }.All(k => Regex.IsMatch(rules0, $@"(?m)^\.os-screen\.voting #os_m{k}\s*\{{\s*margin-right:\s*0px;")) && !Regex.IsMatch(rules0, @"#os_m(5|10)\s*\{")
                    && Enumerable.Range(1, HudMenu.Cards).All(k => noCommentsXml.Contains($"<Label id=\"{HudMenu.Card(k)}_rank\" class=\"os-mrank\" text=\"{{s:text}}\" />") && noCommentsXml.Contains($"<Label id=\"{HudMenu.Card(k)}_num\" class=\"os-mnum\" text=\"{{s:text}}\" />"))
                    && Regex.IsMatch(rules0, @"(?m)^\.os-mnum\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(rules0, @"(?m)^\.os-screen\.voting\.vnum \.os-mnum\s*\{\s*visibility:\s*visible;")
                    && !Regex.IsMatch(rules0, @"(?m)^\.os-screen\.voting \.os-mnum\s*\{") && Regex.IsMatch(rules0, @"(?m)^\.os-screen\.voting\.vnum \.os-mmode\s*\{\s*margin:\s*8px 0px 0px 46px;")
                    && Regex.IsMatch(rules0, @"(?m)^\.os-screen\.voting \.os-mmode\s*\{[^}]*margin:\s*8px 0px 0px 8px;")); // the badges and the step aside only with the numbers (vnum)
                Check($"Content {HudVersion.Content}: the ballot's header is the page bar every page has - Random, who has not voted (then what came of the vote) and the one clock: the limit's, the decision's once everybody has voted (vall), the change's once decided (vdone) - with how to vote under it, by a click or with !vote and the number; no title, no state chip, no count of its own (the tab says Vote, the clock's word the state); the strip hidden",
                    !Regex.IsMatch(noCommentsXml + rules0, @"os-vote-title|os-vchip|os-vcount|os-vhead-main|os-vhead-top|os-vhead-row")
                    && Regex.IsMatch(noCommentsXml, @"<Panel class=""os-vhead"">\s*<Button id=""os_v_random"" class=""os-vbtn os-vrandom"">.*?</Button>\s*<Panel class=""os-vstrip"">.*?</Panel></Panel>\s*<Label class=""os-vwait"" text=""\{s:" + HudVote.WaitVar + @"\}"" />\s*<Panel class=""os-clocks"">", RegexOptions.Singleline)
                    && Regex.IsMatch(rules0, @"(?m)^\.os-vhead\s*\{[^}]*height:\s*34px;[^}]*flow-children:\s*right;[^}]*margin-bottom:\s*8px;") && Regex.IsMatch(rules0, @"(?m)^\.os-mbar\s*\{[^}]*height:\s*34px;[^}]*margin-bottom:\s*8px;")
                    && Regex.IsMatch(noCommentsXml, @"<Panel class=""os-vhowbar"">\s*<Label class=""os-vhow os-vhow-num"" text=""Click a card to vote[^""]*!vote[^""]*number[^""]*!play[^""]*"" />\s*<Label class=""os-vhow os-vhow-plain"" text=""Click a card to vote[^""!]*!play[^""]*"" />\s*<Label class=""os-vpeek-hint"" text=""Hold TAB to see the scores"" />\s*</Panel>")
                    && Regex.IsMatch(rules0, @"(?m)^\.os-vhowbar\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(rules0, @"(?m)^\.os-screen\.voting \.os-vhowbar\s*\{\s*visibility:\s*visible;")
                    && Regex.IsMatch(rules0, @"(?m)^\.os-vhow-num\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(rules0, @"(?m)^\.os-screen\.vnum \.os-vhow-num\s*\{\s*visibility:\s*visible;")
                    && Regex.IsMatch(rules0, @"(?m)^\.os-screen\.vnum \.os-vhow-plain\s*\{\s*visibility:\s*collapse;")
                    && Regex.IsMatch(rules0, @"(?m)^\.os-screen\.vfetch \.os-clock-idle \.os-clock-bar\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(rules0, @"(?m)^\.os-screen\.voting\.vfetch \.os-clock-idle \.os-clock-text\s*\{\s*visibility:\s*visible;")
                    && Regex.IsMatch(rules0, @"(?m)^\.os-clock-idle\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(rules0, @"(?m)^\.os-screen\.vall \.os-clock\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(rules0, @"(?m)^\.os-screen\.vall \.os-clock-idle\s*\{\s*visibility:\s*visible;")
                    && Regex.IsMatch(rules0, @"(?m)^\.os-clocks\s*\{\s*width:\s*400px;\s*height:\s*100%;\s*\}") && Regex.IsMatch(rules0, @"(?m)^\.os-clock\s*\{\s*width:\s*100%;\s*height:\s*100%;")
                    && noCommentsXml.Contains("<Label class=\"os-clock-key os-clock-key-all\" text=\"Decided in\" />") && noCommentsXml.Contains($"<Label class=\"os-clock-key os-clock-key-done\" text=\"{{s:{HudVote.IdleKeyVar}}}\" />")
                    && noCommentsXml.Contains("<Label class=\"os-clock-key\" text=\"Ends in\" />") && Regex.IsMatch(rules0, @"(?m)^\.os-screen\.voting\.vstrip \.os-vstrip\s*\{\s*visibility:\s*collapse;"));
            }

            // ---- content v4.1.0: the popup that asks why a player disliked a map, and the grenade drops on the Fun page ----
            var noComments = Regex.Replace(xml, "<!--.*?-->", "", RegexOptions.Singleline);
            Check("The published layout and stylesheet depend on no site: no panel, class or word of one (the window's footer has no line for it)",
                !Regex.IsMatch(xml + css, @"\bsite\b|_site\b|-site\b|example\.com|https?://", RegexOptions.IgnoreCase));
            var whyFrom = noComments.IndexOf("class=\"os-why\"", StringComparison.Ordinal);
            Check($"The popup: a button for each of the {DownReasons.All.Length} reasons, with its words from the variables the plugin sets on os_global, the map's from os_screen, and no other id in it",
                whyFrom > 0 && Enumerable.Range(1, DownReasons.All.Length).All(k => Regex.IsMatch(noComments, $@"<Button id=""{HudVote.WhyButton(k)}"" class=""os-whyb""><Label class=""os-whyb-text"" text=""\{{s:{HudVote.WhyVar}s{k}\}}"" /><Label class=""os-whyb-word"" text=""\{{s:{HudVote.WhyVar}w{k}\}}"" /></Button>"))
                && noComments.Contains($"text=\"{{s:{HudVote.WhyVar}}}\"") && HudVote.WhyWords().Count == 3 * DownReasons.All.Length
                && HudVote.WhyWords().All(w => w.Text.Length > 0 && w.Text.Length <= 40) && !Regex.IsMatch(noComments[whyFrom..noComments.IndexOf("class=\"os-topstack\"", StringComparison.Ordinal)], @"\bid=""(?!os_why_)"));
            Check("The reasons are one dialog: no reasons in place on a card, the board or the live column, and nothing else counts as a reason", !rules.Contains("os-whycard") && !rules.Contains("whyin") && !noComments.Contains("os_whyw_") && !noComments.Contains("os_whyb_")
                && DownReasons.All.All(r => HudVote.ShortLabel(r).Length is > 0 and <= 12) && HudVote.WhyClick("os_whyz_1") == null && HudVote.WhyClick("os_whyw_1") == null);
            Check("The board's Too big and Ads row is gone, layout and rules, and a click on an older content's buttons there, or on an older card's, counts for nothing (the dialog's and chat's !down big stay, and the cards' tags); Our maps glows, still, while it is not chosen, and is simply lit once it is; the cards wear their mode",
                !Regex.IsMatch(rules + noComments, @"os-bigrow|os-bigvote|os-adsvote|voted-big|voted-ads|os_b_vbig|os_b_vads") && !Enumerable.Range(1, HudMenu.Cards).Any(k => ids.Contains($"{HudMenu.Card(k)}_big") || ids.Contains($"{HudMenu.Card(k)}_ads"))
                && new[] { "os_b_vbig", "os_b_vads" }.All(b => HudView.Command(b, full) == null) && new[] { "os_m1_big", "os_m1_ads" }.All(b => HudMenu.Command(b, back, full, new HudBrowser(), 1001, now) == null)
                && Regex.IsMatch(rules, @"\.os-ours-glow\s*\{[^}]*box-shadow:\s*#E9B44C80") && !rules.Contains("opulse")
                && !Regex.IsMatch(rules, @"(?m)^\.os-ours-glow\s*\{[^}]*opacity:") // the game draws a box-shadow at full strength at any opacity but 0
                && Enumerable.Range(1, 10).All(k => Regex.IsMatch(rules, $@"\.os-vseg\.w{k}\s*\{{\s*width:\s*fill-parent-flow\(\s*{k}\.0\s*\);")) // the strip: weights, so it fills exactly
                && Regex.IsMatch(rules, @"\.os-screen\.fl-ours \.os-ours-glow\s*\{\s*opacity:\s*0;")
                && Modes.All.All(m => Regex.IsMatch(rules, $@"\.os-mcard\.mode-{m.Id} \.os-mmode-{m.Id}\s*\{{\s*visibility:\s*visible;") && noComments.Contains($"class=\"os-mmode os-mmode-{m.Id}\""))
                && Regex.Matches(noComments, "class=\"os-mband\"").Count == HudMenu.Cards && noComments.Contains($"text=\"{{s:{HudVote.RandomVar}}}\"") && noComments.Contains($"text=\"{{s:{HudVote.MoreVar}}}\"")
                && noComments.Contains($"text=\"{{s:{HudVote.TabVar}}}\"") && noComments.Contains($"text=\"{{s:{HudVote.LimitVar}}}\"") && noComments.Contains($"text=\"{{s:{HudVote.IdleVar}}}\""));
            // ---- the cards on their side: every tag in two lines beside the picture, ten on the ballot, twelve in the browser ----
            Check($"The cards: {HudMenu.Cards} of them, each with its picture, the 15 tags of MapTags in their order (none but the mode label and the warning on the picture), a score and a line under the name; the browser shows {HudBrowser.PerPage}, the ballot {MapVote.PageCards}",
                HudMenu.Cards == Math.Max(MapVote.PageCards, HudBrowser.PerPage) && Enumerable.Range(1, HudMenu.Cards).All(k => ids.Contains(HudMenu.Card(k)) && ids.Contains($"{HudMenu.Card(k)}_score") && ids.Contains($"{HudMenu.Card(k)}_who"))
                && Regex.Matches(noComments, "class=\"os-mtags\"").Count == HudMenu.Cards && Regex.Matches(noComments, "class=\"os-mtag ").Count == HudMenu.Cards * MapTags.All.Length
                && Regex.IsMatch(rules, @"\.os-mtags\s*\{[^}]*flow-children:\s*right-wrap;") && Regex.IsMatch(rules, @"\.os-screen\.voting \.os-mthumb\s*\{\s*width:\s*283px;")
                && Regex.IsMatch(rules, @"\.os-mthumb\s*\{\s*width:\s*196px;") && Regex.IsMatch(rules, @"\.os-screen\.voting \.os-mcard\s*\{\s*width:\s*285px;\s*height:\s*181px;")
                && HudVote.TagClasses.All(c => Regex.IsMatch(rules, $@"\.os-mcard\.{c} \.os-mtag\.{c}\s*\{{\s*visibility:\s*visible;"))
                && Enumerable.Range(1, HudMenu.Cards).All(k => noComments.Contains($"text=\"{{s:{HudVote.HoverVar(k)}}}\"")));
            // ---- content v4.14.0: the chat column, the pager's pulse on its buttons, the decided state without a countdown, the download, the browser's cards ----
            var noCommentsCss = rules;
            Check($"Content v4.14.0: the chat column - {HudChat.Lines} rows, one id each, in a dock from the window's top (80 at 16:9 and 21:9, 172 at 16:10, 384 at 4:3: the window's foot at 1000 at every aspect, as the column's) down to just above CS2's typing line, x 0-579 over CS2's whole chat box; framed as the window is - its border, corners and shadow, the gold rule on top, the dots - and fully opaque: down to y 1000 with CS2's chat closed, in a match and at a match end; with it open its foot at 952 and at 674 at a match end (meos, or the game's own classes for its end screen with the scoreboard); shown only while chaton, kept by a beat (hb); the pointer to CS2's line above it, pointing down, at a match end too",
                Regex.IsMatch(noComments, @"<Panel id=""os_screen"" class=""os-screen"" hittest=""false"">\s*<Panel class=""os-chat-dock"" hittest=""false"">\s*<Panel class=""os-chat-top"" hittest=""false"" />\s*<Panel class=""os-chat"" hittest=""false"">\s*<Panel class=""os-chat-rule"" hittest=""false"" />\s*<Panel class=""os-chat-lift"" hittest=""false"" />\s*<Panel class=""os-chat-clip"" hittest=""false"">\s*<Panel class=""os-chat-lines"" hittest=""false"">")
                && Regex.IsMatch(noComments, @"</Panel>\s*</Panel>\s*</Panel>\s*<Panel class=""os-chat-foot"" hittest=""false"">\s*<Panel class=""os-chat-slot"" hittest=""false"" />\s*<Panel class=""os-chat-under"" hittest=""false"" />\s*</Panel>\s*</Panel>")
                && Enumerable.Range(1, HudChat.Lines).All(k => noComments.Contains($"<Panel id=\"{HudChat.Line(k)}\" class=\"os-chat-line\" hittest=\"false\"><Panel hittest=\"false\" /><Label hittest=\"false\" text=\"{{s:text}}\" /></Panel>"))
                && !noComments.Contains($"id=\"{HudChat.Line(HudChat.Lines + 1)}\"") && !Regex.IsMatch(noComments, @"os_chat_\d+_t")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-chat-dock\s*\{\s*width:\s*579px;\s*height:\s*100%;\s*horizontal-align:\s*left;\s*flow-children:\s*down;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-chat-top\s*\{\s*width:\s*100%;\s*height:\s*80px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.AspectRatio16x10 \.os-chat-top\s*\{\s*height:\s*172px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.AspectRatio4x3 \.os-chat-top\s*\{\s*height:\s*384px;")
                && 1000 - 920 == 80 && Math.Abs(1000 - 0.90 * 920 - 172) < 0.5 && Math.Abs(1000 - 0.67 * 920 - 384) < 0.5
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-win\s*\{[^}]*vertical-align:\s*bottom;\s*margin-bottom:\s*80px;") && !Regex.IsMatch(noCommentsCss, @"(?m)^\.os-win\s*\{[^}]*vertical-align:\s*center;")
                && Regex.Match(noCommentsCss, @"(?m)^\.os-chat\s*\{([^}]*)\}") is { Success: true } chatRule && Regex.Match(noCommentsCss, @"(?m)^\.os-win\s*\{([^}]*)\}") is { Success: true } winRule
                && new[] { @"border-radius:\s*5px;", @"border:\s*1px solid #FFFFFF12;", @"box-shadow:\s*#000000b3 0px 24px 72px 0px;", @"background-image:\s*url\( ""s2r://panorama/images/backgrounds/bluedots_large_png\.vtex"" \);", @"background-img-opacity:\s*0\.03;" }.All(x => Regex.IsMatch(chatRule.Groups[1].Value, x) && Regex.IsMatch(winRule.Groups[1].Value, x))
                && Regex.IsMatch(chatRule.Groups[1].Value, @"visibility:\s*collapse;[^}]*height:\s*fill-parent-flow\(\s*1\s*\);\s*flow-children:\s*down;\s*opacity:\s*0;") && Regex.IsMatch(chatRule.Groups[1].Value, @"background-color:\s*gradient\( linear, 0% 0%, 0% 100%, from\( #141A21 \), to\( #0A0D11 \) \);")
                && Regex.Match(noCommentsCss, @"(?m)^\.os-chat-rule\s*\{([^}]*)\}").Groups[1].Value.Trim() == Regex.Match(noCommentsCss, @"(?m)^\.os-win-rule\s*\{([^}]*)\}").Groups[1].Value.Trim()
                && !Regex.IsMatch(noCommentsCss, @"\.os-screen\.win \.os-chat\s*\{") && !Regex.IsMatch(noCommentsCss, @"\.os-screen\.meos \.os-chat\s*\{")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-chat-foot\s*\{\s*width:\s*100%;\s*height:\s*80px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.HUD_CHAT_VISIBLE \.os-screen \.os-chat-foot\s*\{\s*height:\s*128px;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.HUD_CHAT_VISIBLE \.os-screen\.meos \.os-chat-foot\s*\{\s*height:\s*406px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.HUD_ENDOFMATCH_VISIBLE\.HUD_SCOREBOARD_VISIBLE\.HUD_CHAT_VISIBLE \.os-screen \.os-chat-foot\s*\{\s*height:\s*406px;")
                && 1080 - 80 == 1000 && 1080 - 128 == 952 && 1080 - 406 == 674 && 952 - 278 == 674
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.chaton \.os-chat\s*\{\s*visibility:\s*visible;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.chaton\.hb \.os-chat\s*\{\s*animation-name:\s*os-chat-beat-a;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.chaton \.os-chat\s*\{\s*animation-name:\s*os-chat-beat-b;\s*animation-duration:\s*35s;") && 35 > 3 * HudChat.BeatSeconds
                && HudChat.KindClasses.All(c => Regex.IsMatch(noCommentsCss, $@"\.os-chat-line\.{c}\b"))
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-chat-foot\s*\{\s*width:\s*100%;\s*height:\s*80px;\s*flow-children:\s*down;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-chat-slot\s*\{\s*width:\s*100%;\s*height:\s*0px;\s*\}")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.HUD_CHAT_VISIBLE \.os-screen \.os-chat-slot\s*\{\s*height:\s*48px;\s*\}") && !Regex.IsMatch(noCommentsCss, @"\.os-chat-slot\s*\{[^}]*background")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.win-in\.chaton \.os-chat-top\s*\{\s*background-color:\s*#05070ACC;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.win-in\.chaton \.os-chat-under\s*\{\s*background-color:\s*#05070ACC;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-dim\s*\{[^}]*margin-left:\s*579px;\s*background-color:\s*#05070ACC;") && !Regex.IsMatch(noCommentsCss, @"\.os-dim\s*\{[^}]*margin-left:\s*0px")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.chatwin\.win \.os-chat-dock\s*\{\s*opacity:\s*0;\s*transition-property:\s*opacity;\s*transition-duration:\s*0\.25s;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.chatwin\.win-in \.os-chat-dock\s*\{\s*opacity:\s*1;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.HUD_CHAT_VISIBLE \.os-screen \.os-chat\s*\{\s*box-shadow:\s*#00000000 0px 0px 0px 0px;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-chatptr\s*\{[^}]*margin:\s*0px 0px 136px 18px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-chatptr-arrow\s*\{[^}]*rotateZ\(\s*-45deg\s*\);")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.meos \.os-chatptr\s*\{\s*margin-bottom:\s*414px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.HUD_ENDOFMATCH_VISIBLE\.HUD_SCOREBOARD_VISIBLE \.os-screen \.os-chatptr\s*\{\s*margin-bottom:\s*414px;"));
            // Whole rows only: a row is its height and its gap under it; the clip - the column less its border (2), its rule (3), the lift and the clip's margins - is a whole
            // number of rows at every aspect with CS2's chat closed, open, and open at a match end (the lift's 10 px), and the label never takes a second row.
            var rowCss = Regex.Match(noCommentsCss, @"(?m)^\.os-chat-line\s*\{[^}]*height:\s*(\d+)px;[^}]*margin-bottom:\s*(\d+)px;");
            var clipCss = Regex.Match(noCommentsCss, @"(?m)^\.os-chat-clip\s*\{[^}]*margin:\s*(\d+)px 0px (\d+)px 0px;\s*overflow:\s*clip;");
            int Pxv(string pattern) => Regex.Match(noCommentsCss, pattern) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
            var pitch = rowCss.Success ? int.Parse(rowCss.Groups[1].Value) + int.Parse(rowCss.Groups[2].Value) : 0;
            var lift = Pxv(@"(?m)^\.HUD_CHAT_VISIBLE \.os-screen\.meos \.os-chat-lift\s*\{\s*height:\s*(\d+)px;");
            var row43 = Regex.Match(noCommentsCss, @"(?m)^\.AspectRatio4x3 \.os-chat-line\s*\{\s*height:\s*(\d+)px;\s*margin-bottom:\s*(\d+)px;");
            var pitch43 = row43.Success ? int.Parse(row43.Groups[1].Value) + int.Parse(row43.Groups[2].Value) : 0;
            var clipTops = new[] { (Top: 80, Margin: clipCss.Success ? int.Parse(clipCss.Groups[1].Value) : -1, Pitch: pitch), (Top: 172, Margin: Pxv(@"(?m)^\.AspectRatio16x10 \.os-chat-clip\s*\{\s*margin-top:\s*(\d+)px;"), Pitch: pitch),
                (Top: 384, Margin: Pxv(@"(?m)^\.AspectRatio4x3 \.os-chat-clip\s*\{\s*margin-top:\s*(\d+)px;"), Pitch: pitch43) };
            var clipBottom = clipCss.Success ? int.Parse(clipCss.Groups[2].Value) : -1;
            var clips = clipTops.SelectMany(a => new[] { (Foot: 1000, Lift: 0), (Foot: 952, Lift: 0), (Foot: 674, Lift: lift) }.Select(st => (Px: st.Foot - a.Top - 2 - 3 - st.Lift - a.Margin - clipBottom, a.Pitch))).ToList();
            Check($"Content v4.14.0: the chat column shows whole rows only - a row {pitch} px ({pitch43} at 4:3, in 12 px type), and its clip a whole number of rows in every state ({string.Join(", ", clips.Select(c => c.Pitch > 0 ? $"{c.Px / c.Pitch}" : "?"))} rows: 16:9, 16:10, 4:3 - closed, open, open at a match end); the label one row, cut to fit with an ellipsis; a line's later rows with its mark down their whole height; the rows 543 px with 18 left of them and 16 right, inside the clip's 577",
                pitch == 24 && pitch43 == 16 && lift == 10 && Pxv(@"(?m)^\.HUD_ENDOFMATCH_VISIBLE\.HUD_SCOREBOARD_VISIBLE\.HUD_CHAT_VISIBLE \.os-screen \.os-chat-lift\s*\{\s*height:\s*(\d+)px;") == lift
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-chat-lift\s*\{\s*width:\s*100%;\s*height:\s*0px;") && clipTops.All(a => a.Margin >= 12) && clips.All(c => c.Px > 0 && c.Pitch > 0 && c.Px % c.Pitch == 0) && clips.Max(c => c.Px / c.Pitch) <= HudChat.Lines
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-chat-line Label\s*\{[^}]*height:\s*21px;[^}]*text-overflow:\s*ellipsis;\s*white-space:\s*nowrap;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-chat-line\.k-cont Panel\s*\{\s*height:\s*21px;\s*margin-top:\s*0px;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.AspectRatio4x3 \.os-chat-line Label\s*\{\s*height:\s*15px;\s*font-size:\s*12px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.AspectRatio4x3 \.os-chat-line\.k-cont Panel\s*\{\s*height:\s*15px;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-chat-lines\s*\{\s*width:\s*543px;[^}]*vertical-align:\s*bottom;[^}]*margin-left:\s*18px;") && !Regex.IsMatch(noCommentsCss, @"(?m)^\.os-chat-lines\s*\{[^}]*padding") && 18 + 543 + 16 == 579 - 2);
            var wrapLine = string.Join(" ", Enumerable.Repeat("a long line to wrap over the column's rows", 6));
            var wrapped = HudChat.Rows(wrapLine);
            var wide = HudChat.Rows(new string('W', 60) + " " + new string('\u4E2D', 40));
            var wrapChat = new HudChat();
            wrapChat.Say("Eve", "T", wrapLine, teamOnly: false);
            wrapChat.Say("Dan", "CT", "short", teamOnly: false);
            var wrapColumn = HudChat.Column(wrapChat.For(1, "CT"));
            var eveRows = HudChat.Rows(HudChat.Text(wrapChat.All[0])).Count; // the line as kept: Say keeps a player's words up to HudChat.SayMax characters
            Check($"The column's rows (HudChat.Rows): a line breaks at spaces into rows of at most {HudChat.RowUnits} units, never more than {HudChat.MaxRows} (the last ends in an ellipsis), a word longer than a row breaks inside it, wide characters count wide; its first row carries the mark, the rest are k-cont; a player's words kept whole up to {HudChat.SayMax}",
                wrapped.Count == HudChat.MaxRows && wrapped[^1].EndsWith(Hud.Ellipsis, StringComparison.Ordinal) && !string.Concat(wrapped).Contains('~') && HudChat.SayMax == 200 && wrapChat.All[0].Text.Length == Math.Min(wrapLine.Length, HudChat.SayMax) && wrapped.All(r => HudChat.Units(r) <= HudChat.RowUnits) && wrapped.Take(HudChat.MaxRows - 1).All(r => !r.EndsWith(' ') && !r.StartsWith(' '))
                && wide.All(r => HudChat.Units(r) <= HudChat.RowUnits) && wide.Count >= 3 && HudChat.Rows("hi") is ["hi"] && HudChat.Rows("") is []
                && wrapColumn.Texts.Single(x => x.Id == HudChat.Line(HudChat.Lines)).Text == "Dan: short"
                && eveRows >= 2 && wrapColumn.Texts.Single(x => x.Id == HudChat.Line(HudChat.Lines - eveRows)).Text.StartsWith("Eve: a long line", StringComparison.Ordinal)
                && wrapColumn.Classes.Contains((HudChat.Line(HudChat.Lines - eveRows), "k-cont", false)) && wrapColumn.Classes.Contains((HudChat.Line(HudChat.Lines - eveRows), "k-t", true))
                && wrapColumn.Classes.Contains((HudChat.Line(HudChat.Lines - 1), "k-cont", true)) && wrapColumn.Classes.Contains((HudChat.Line(HudChat.Lines - 1), "k-t", true))
                && wrapColumn.Classes.Contains((HudChat.Line(HudChat.Lines - eveRows - 1), "used", false)));
            // The audit of round 5: what can run past its box - one padding for every page; rows of a fixed height that never wrap; the vote bar and the download within it;
            // the browser card's words that never meet; the filter bar's worst case; the column's words in the window's colour; the small caps' spacing; the server's download.
            Check("The audit (content v4.14.0): every page one padding; Players', Fun's, Bots' and Sync's fixed rows single lines that end in an ellipsis; a choice's word inside its button; the presets' boxes room for four lines; Random's words in its button; the download's and the clock's words within the bar; the browser card's click words at the picture's foot, its mode and count at its top; Previous's words at 280 px; the column's words in the window's colour; the small caps 2 px apart; the server's download said as the server's",
                Regex.IsMatch(noCommentsCss, @"(?m)^\.os-page\s*\{[^}]*padding:\s*16px 34px 12px 34px;") && !Regex.IsMatch(noCommentsCss, @"(?m)^\.os-page-\w+\s*\{[^}]*padding") && !Regex.IsMatch(noCommentsCss, @"\.os-page-maps\s*\{[^}]*padding")
                && new[] { "os-pteam", "os-pvote", "os-pfor", "os-frow-title", "os-dline", "os-dwhy", "os-vdl-mb" }.All(c => Regex.IsMatch(noCommentsCss, $@"(?m)^\.{c}\s*\{{[^}}]*text-overflow:\s*ellipsis;\s*white-space:\s*nowrap;"))
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-funb \.os-setb-text\s*\{\s*max-width:\s*88px;\s*text-overflow:\s*ellipsis;\s*white-space:\s*nowrap;") && Regex.IsMatch(noCommentsCss, @"(?m)^#os_v_random \.os-vbtn-text\s*\{[^}]*max-width:\s*150px;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-bpreset\s*\{[^}]*height:\s*120px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-bpresets\s*\{[^}]*height:\s*128px;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.vfetch \.os-clocks\s*\{\s*width:\s*480px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.vdl \.os-clocks\s*\{\s*width:\s*460px;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.voting\.vfetch \.os-clock-idle \.os-clock-text\s*\{[^}]*width:\s*fill-parent-flow\(\s*1\s*\);") && 170 + 64 + 96 + 12 + 110 <= 460
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-mplay-text\s*\{\s*max-width:\s*267px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-mmode\s*\{\s*vertical-align:\s*top;\s*margin:\s*30px 0px 0px 6px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-mside\s*\{\s*vertical-align:\s*top;\s*margin:\s*30px 0px 0px 124px;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^#os_q_prev_text\s*\{\s*max-width:\s*280px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-chat-line Label\s*\{[^}]*color:\s*os-text;") && !Regex.IsMatch(noCommentsCss, @"(?m)^\.os-chat-line Label\s*\{[^}]*text-shadow")
                && !Regex.IsMatch(noCommentsCss, @"letter-spacing:\s*2\.[23]px") && noComments.Contains("<Label class=\"os-clock-key os-vdl-key\" text=\"Server downloading\" />")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.vfetch \.os-clock-key-done\s*\{\s*width:\s*170px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.vfetch \.os-mcard\.now \.os-mplay-text\s*\{\s*width:\s*170px;"));
            // The review of 218r4's frames (content v4.14.0): what it found that the stylesheet and the layout put right.
            var gridRows = Regex.Matches(noComments, @"<Panel class=""os-grid-row"">").Count;
            Check("The review of 218r4 (content v4.14.0): nothing of the top stack or the card stack over the open window; four cards a row in rows that never wrap; a greyed pager button still; the own vote's card and the winner opaque under their glow, the badge after the decision, no ON NOW and no LEADING there, no second more-pill; the padlock clear of the pill; the window's line over the whole pager; Fun's and Bots' rows their columns' 572 px; Game's settings framed; Sync's map events in two lines and its title as every page's; Players' head on one line, no rule under an empty row, the version as written; Start's box as wide as its steps; the last 10 s' bar red; the logo kept loaded; 4:3's small words larger; no Hold TAB in the window's own dialog, no capital mid-line in the ballot's how-to",
                Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.win \.os-topstack\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.win \.os-cardstack\s*\{\s*visibility:\s*collapse;")
                && gridRows == 3 && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-grid-row\s*\{[^}]*flow-children:\s*right;") && !Regex.IsMatch(noCommentsCss, @"(?m)^\.os-grid-row\s*\{[^}]*right-wrap")
                && Regex.IsMatch(noCommentsCss, @"(?m)^#os_pg_next\.off\s*\{\s*animation-name:\s*none;") && Regex.IsMatch(noCommentsCss, @"(?m)^#os_pg_prev\.off\s*\{\s*animation-name:\s*none;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.voting \.os-mcard\.confirm\s*\{[^}]*background-color:\s*gradient\(") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.vdone \.os-mcard\.now\s*\{[^}]*background-color:\s*gradient\(")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.vdone \.os-mcard\.confirm \.os-mplay-text\s*\{\s*visibility:\s*visible;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-mcard\.now \.os-mtag-now\s*\{\s*visibility:\s*collapse;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.vdone \.os-mcard\.vlead \.os-mtag-lead\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.voting\.vmore \.os-vmore\s*\{\s*visibility:\s*collapse;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-mlock-shut\s*\{[^}]*background-size:\s*44px 44px;\s*background-position:\s*50% 18%;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-mlock-open\s*\{\s*width:\s*44px;\s*height:\s*44px;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-wl\s*\{[^}]*height:\s*46px;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-fcol\s*\{\s*width:\s*572px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-fcol-right\s*\{\s*margin-left:\s*28px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-frow\s*\{[^}]*width:\s*572px;") && 572 + 28 + 572 == 1172
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-page-game \.os-set\s*\{[^}]*border:\s*1px solid")
                && Regex.Matches(noComments, @"<Panel class=""os-dmrow os-dm\d""><Panel class=""os-dmtop""><Label class=""os-dmat""").Count == SyncWords.MapRows
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-dmrow\s*\{[^}]*height:\s*40px;\s*flow-children:\s*down;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-dmwhy\s*\{\s*width:\s*100%;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-dtitle\s*\{[^}]*font-size:\s*22px;[^}]*color:\s*#FFFFFF;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-phead \.os-phud\s*\{\s*height:\s*fit-children;") && !Regex.IsMatch(noCommentsCss, @"(?m)^\.os-prow\s*\{[^}]*border") && !Regex.IsMatch(noCommentsCss, @"(?m)^\.os-psum\s*\{[^}]*uppercase")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-step\s*\{\s*width:\s*231px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-wback\s*\{[^}]*width:\s*721px;") && 3 * 231 + 2 * 14 == 721 && 3 * 231 + 3 * 14 <= 736
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.vlow \.os-clock-fill\s*\{\s*background-color:\s*#F07C6B;") && noComments.Contains("<Panel class=\"os-logo os-preload\" hittest=\"false\" />")
                && new[] { "os-mmode", "os-mtag", "os-mplay-text", "os-clock-key" }.All(c => Regex.IsMatch(noCommentsCss, $@"(?m)^\.AspectRatio4x3 \.os-win \.{c}\s*\{{[^}}]*font-size:\s*1[2-4]px;"))
                && !noComments.Contains("Hold TAB and click, or type", StringComparison.Ordinal) && !noComments.Contains("Or type !vote", StringComparison.Ordinal));
            // The TAB peek (content v4.14.0): the server's vpeek (TAB held by a player who may peek) and vpeekok (he may: voted, or the vote decided) on os_screen.
            var peekPanels = new[] { "os-win", "os-dim", "os-chat-dock", "os-chatptr", "os-why" };
            static int Specificity(string selector) => Regex.Matches(selector, @"\.[A-Za-z_][\w-]*").Count + 100 * Regex.Matches(selector, @"#[\w-]+").Count;
            var showsPeekPanel = Regex.Matches(noCommentsCss, @"([^{}]+)\{([^}]*)\}").Where(m => Regex.IsMatch(m.Groups[2].Value, @"visibility:\s*visible")
                && peekPanels.Any(p => Regex.IsMatch(m.Groups[1].Value.Trim(), $@"\.{p}$"))).Select(m => Specificity(m.Groups[1].Value)).DefaultIfEmpty(0).Max();
            Check($"The TAB peek (content v4.14.0): while the server reads TAB held by a player who may peek (vpeek) and CS2's own scoreboard is up - or its end screen - the window, the chat column, the dim, the pointer and the dislike's dialog collapse, no fade, by rules of six classes that outrank every rule that shows them ({showsPeekPanel} at the most); the board, the top stack and the card stack stay away; vpeekok says Hold TAB to see the scores at the right of the ballot's how-to bar",
                peekPanels.All(p => new[] { "HUD_SCOREBOARD_VISIBLE", "HUD_ENDOFMATCH_VISIBLE" }.All(r => Regex.IsMatch(noCommentsCss, $@"(?m)^\.{r} \.os-global \.os-screen\.win\.vpeek \.{p}\s*\{{\s*visibility:\s*collapse;\s*\}}")))
                && Specificity(".HUD_SCOREBOARD_VISIBLE .os-global .os-screen.win.vpeek .os-win") == 6 && showsPeekPanel is > 0 and < 6
                && !Regex.IsMatch(noCommentsCss, @"\.vpeek(ok)?\b[^{]*\{[^}]*(opacity|transition|animation)") && !Regex.IsMatch(noCommentsCss, @"\.vpeek(ok)?\b[^{]*\.os-(board|topstack|cardstack)\s*\{")
                && !Regex.IsMatch(noCommentsCss, @"(?m)^[^{]*\.vpeekok\b[^{]*\.os-(win|dim|chat-dock|chatptr|why)\s*\{")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-vpeek-hint\s*\{\s*visibility:\s*collapse;\s*horizontal-align:\s*right;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.vpeekok \.os-vpeek-hint\s*\{\s*visibility:\s*visible;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.vpeekok \.os-vhow\s*\{\s*width:\s*940px;") && noComments.Contains("<Label class=\"os-vpeek-hint\" text=\"Hold TAB to see the scores\" />"));
            var chatTest = new HudChat();
            chatTest.Say("Alice", "CT", "gg all", teamOnly: false);
            chatTest.Say("Bob", "T", "rush b", teamOnly: true);
            chatTest.Server("Your vote: 2. Lake", to: 1001);
            chatTest.Event(HudChat.Joined("Cara"));
            chatTest.Say("Dan", "SPEC", "hi", teamOnly: false);
            var ctView = chatTest.For(1001, "CT");
            var tView = chatTest.For(1002, "T");
            var column = HudChat.Column(ctView);
            Check("The chat column's lines (HudChat): a team line to its team alone, a server line for its one account or everybody, events and lines to everybody for all; the newest at the bottom, the rest empty above; each line's look by its sender's team, team, server or event",
                ctView.Count == 4 && tView.Count == 4 && !ctView.Any(e => e.Text == "rush b") && tView.Any(e => e.Kind == HudChat.Kind.Team && e.Text == "rush b") && !tView.Any(e => e.Kind == HudChat.Kind.Server)
                && column.Texts.Single(x => x.Id == HudChat.Line(HudChat.Lines)).Text == "Dan: hi"
                && column.Texts.Single(x => x.Id == HudChat.Line(HudChat.Lines - 3)).Text == "Alice: gg all" && column.Texts.Single(x => x.Id == HudChat.Line(1)).Text == ""
                && column.Classes.Contains((HudChat.Line(HudChat.Lines - 3), "k-ct", true)) && column.Classes.Contains((HudChat.Line(HudChat.Lines - 2), "k-srv", true)) && column.Classes.Contains((HudChat.Line(HudChat.Lines - 1), "k-evt", true))
                && column.Classes.Contains((HudChat.Line(1), "used", false)) && column.Classes.Contains((HudChat.Line(HudChat.Lines), "used", true))
                && HudChat.Text(tView.Single(e => e.Kind == HudChat.Kind.Team)) == "(Team) Bob: rush b" && HudChat.Look(tView.Single(e => e.Kind == HudChat.Kind.Team)).SetEquals(["k-t", "k-team"])
                && HudChat.Joined("Cara") == "Cara joined the game" && HudChat.Left("Cara") == "Cara left the game" && HudChat.TeamChanged("Cara", "CT") == "Cara joined the Counter-Terrorists");
            Check("The chat column's route is the server's (the owner's 277): always (a1), only while the window is open (c), never (d) - the same stylesheet for all, so no content update; c by default",
                HudChat.Default == HudChat.Route.Window && HudChat.Shows(HudChat.Route.Always, false) && HudChat.Shows(HudChat.Route.Always, true) && !HudChat.Shows(HudChat.Route.Window, false)
                && HudChat.Shows(HudChat.Route.Window, true) && !HudChat.Shows(HudChat.Route.Never, true));
            Check("The chat column's words go only to a game whose HUD check named v4.14.0 or later (HudChat.Feeds): none before the check, none to v4.13.0 or an unknown content - they would log \"Unable to find panel\" (the live lab, release 218: 36 of them)",
                HudChat.Feeds("v4.14.0") && HudChat.Feeds("v4.15.0") && !HudChat.Feeds("") && !HudChat.Feeds("v4.13.0") && !HudChat.Feeds(HudVersion.Unknown) && !HudChat.Feeds(HudVersion.Older));
            var many = new HudChat();
            for (var i = 0; i < HudChat.Keep + 5; i++) many.Event($"line {i}");
            Check($"The chat column keeps its newest {HudChat.Keep} lines and shows the newest {HudChat.Lines}; its beat turns every {HudChat.BeatSeconds} s",
                many.All.Count == HudChat.Keep && many.All[0].Text == "line 5" && many.For(1, "CT").Count == HudChat.Lines && many.For(1, "CT")[^1].Text == $"line {HudChat.Keep + 4}"
                && HudChat.BeatA(new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc)) != HudChat.BeatA(new DateTime(2026, 11, 17, 0, 0, HudChat.BeatSeconds, DateTimeKind.Utc)));
            var snapPlayer = new HudPlayer();
            List<(string Id, string Class, bool On)> Bar(int k) => [.. Enumerable.Range(0, 11).Select(s => (HudVote.IdleBar, $"w{s}", s == k))];
            string On(List<(string Id, string Class, bool On)> cs) => string.Join("+", cs.Where(x => x.On).Select(x => x.Class));
            var s1 = On(HudFrame.Snapped(Bar(5), HudVote.IdleBar, snapPlayer));
            var s2 = On(HudFrame.Snapped(Bar(4), HudVote.IdleBar, snapPlayer));
            var s3 = On(HudFrame.Snapped(Bar(9), HudVote.IdleBar, snapPlayer));
            var s4 = On(HudFrame.Snapped(Bar(9), HudVote.IdleBar, snapPlayer));
            var s5 = On(HudFrame.Snapped(Bar(8), HudVote.IdleBar, snapPlayer));
            Check($"A bar that steps up snaps (HudFrame.Snapped): down as it comes; up, first snap with the old step, then the new step under snap, then snap off - {s1}, {s2}, {s3}, {s4}, {s5}",
                s1 == "w5" && s2 == "w4" && s3 == "w4+snap" && s4 == "w9+snap" && s5 == "w8" && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-clock-bar\.snap \.os-clock-fill\s*\{\s*transition-duration:\s*0s;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^#os_vidle\.snap \.os-clock-fill\s*\{\s*transition-duration:\s*0s;") && Regex.IsMatch(noCommentsCss, @"(?m)^#os_vidle\.w0 \.os-clock-fill\s*\{\s*transition-duration:\s*0s;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^#os_vidle \.os-clock-fill\s*\{\s*transition-duration:\s*1s;")); // the bar's own 1 s by its id: the snap and the rest by its id too, or they lose
            Check("Content v4.14.0: the pager's pulse is the button's own - Next and Prev, their border and fill - and their words stay still: the gold panel behind the words is gone",
                Regex.IsMatch(noCommentsCss, @"(?m)^\.os-pgb-glow\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(noCommentsCss, @"(?m)^#os_pg_next\s*\{\s*animation-name:\s*os-pgpulse-btn;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^#os_pg_prev\s*\{\s*animation-name:\s*os-pgpulse-btn;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-pgb\.off\s*\{\s*animation-name:\s*none;"));
            Check("Content v4.14.0: decided with the change gone out at once (vnow), the clock says its key alone - no digits, no bar; a winner downloading with its download known (vdl) shows its percent, its bar (os_vdl) and how much of how much in the clocks' place, and its card the same bar in blue; the top line's news stays away while the window shows it",
                Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.vnow \.os-clock-idle \.os-clock-big\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.vnow \.os-clock-idle \.os-clock-bar\s*\{\s*visibility:\s*collapse;")
                && noComments.Contains($"<Panel id=\"{HudVote.DownloadBar}\" class=\"os-clock-bar\">") && noComments.Contains($"text=\"{{s:{HudVote.DownloadNumVar}}}\"") && noComments.Contains($"text=\"{{s:{HudVote.DownloadVar}}}\"")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.vdl \.os-vdl\s*\{\s*visibility:\s*visible;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.vdl \.os-clock\s*\{\s*visibility:\s*collapse;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.vfetch \.os-mcard\.now \.os-mvbar-fill\s*\{") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.win\.vfetch \.os-top\s*\{\s*visibility:\s*collapse;"));
            Check("Content v4.14.0: the browser's cards are the ballot's - 285 x 181, twelve a page, four across and three down (the fourth, eighth and twelfth without the margin to wrap on), the picture 283 x 112, one row of tags, the whole card its button with what a click does on the picture's right (its state there, not on the whole card), the likes and arrows on the picture; the ballot's words stay plain",
                Regex.IsMatch(noCommentsCss, @"(?m)^\.os-mcard\s*\{\s*width:\s*285px;\s*height:\s*181px;") && new[] { 4, 8, 12 }.All(k => Regex.IsMatch(noCommentsCss, $@"(?m)^#os_m{k}\s*\{{\s*margin-right:\s*0px;"))
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-mthumb\s*\{\s*width:\s*283px;\s*height:\s*112px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-mplay\s*\{\s*width:\s*100%;\s*height:\s*100%;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-mplay-text\s*\{[^}]*horizontal-align:\s*right;[^}]*margin:\s*84px 8px 0px 0px;") && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-screen\.voting \.os-mplay-text\s*\{\s*padding:\s*0px;")
                && Regex.IsMatch(noCommentsCss, @"(?m)^\.os-mbody\s*\{\s*width:\s*100%;\s*height:\s*fill-parent-flow\(\s*1\s*\);")
                && 4 * 285 + 3 * 10 <= 1172 && HudBrowser.PerPage == 12 && HudMenu.Cards == 12 && Enumerable.Range(1, 12).All(k => ids.Contains(HudMenu.Card(k)) && ids.Contains($"{HudMenu.Card(k)}_play_text"))
                && 34 + 8 + 34 + 10 + 3 * (181 + 8) + 34 <= 920 - 3 - 76 - 49 - 45 - 28);

            // ---- the menu's tabs and sub-tabs, the Players page, the version button, the outdated banner, the restart in the foot ----
            string[] rowTabs = ["os_tab_start", HudVote.BackButton, HudView.TabMaps, HudVote.BrowseButton, HudView.TabFun, HudBots.Tab, "os_sub_players", HudView.TabGame, HudDash.Tab, "os_tab_help", HudView.TabHow];
            var tabRow = Regex.Match(noComments, @"<Panel class=""os-tabs"">(.*?)</Panel>\s*<Panel class=""os-subs"">", RegexOptions.Singleline).Groups[1].Value;
            Check("One row of tabs (content v4.14.0), every page a tab - Start, Vote, Maps, Fun, Bots, Players, Game, Sync, Help, How it works - Vote only while a vote is on, Maps then the browser that adds to it; the sub-tab bar's other buttons out of sight under their ids; a tab with nothing to use greyed (off); the Players page's every row and the caption fed",
                Regex.Matches(tabRow, @"<Button id=""([^""]+)"" class=""os-tab"">").Select(m => m.Groups[1].Value).SequenceEqual(rowTabs) && rowTabs.All(id => HudView.Tabs.Any(x => x.Id == id) || id is HudVote.BackButton or HudVote.BrowseButton)
                && Regex.IsMatch(rules, @"(?m)^\.os-subs\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(rules, @"(?m)^#os_v_back\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(rules, @"\.os-screen\.voting #os_v_back\s*\{\s*visibility:\s*visible;")
                && Regex.IsMatch(rules, @"\.os-screen\.vbrowse #os_v_browse\s*\{\s*visibility:\s*visible;") && Regex.IsMatch(rules, @"\.os-screen\.voting #os_tab_maps\s*\{\s*visibility:\s*collapse;")
                && Regex.IsMatch(rules, @"(?m)^\.os-tab\.off\s*\{\s*opacity:") && Regex.IsMatch(rules, @"(?m)^\.os-subtitle\s*\{\s*visibility:\s*collapse;")
                && new[] { ("voting", HudVote.BackButton), ("vbrowse", HudVote.BrowseButton), ("page-bots", HudBots.Tab), ("page-players", "os_sub_players"), ("page-game", HudView.TabGame), ("page-sync", HudDash.Tab), ("page-how", HudView.TabHow) }
                    .All(x => Regex.IsMatch(rules, $@"(?m)^\.os-screen\.{x.Item1} #{x.Item2}\s*\{{\s*border-bottom:\s*2px solid os-gold;"))
                && !Regex.IsMatch(rules, @"\.os-screen\.page-bots #os_tab_fun") && !Regex.IsMatch(rules, @"\.os-screen\.page-how #os_tab_help")
                && Enum.GetValues<HudSlots.Page>().All(pg => Regex.IsMatch(rules, $@"\.os-screen\.page-{pg.ToString().ToLowerInvariant()} "))
                && noComments.Contains($"text=\"{{s:{HudView.SubCaptionVar}}}\"") && noComments.Contains($"text=\"{{s:{HudView.PlayersSummaryVar}}}\"")
                && HudView.PlayersPage([]).All(w => noComments.Contains($"text=\"{{s:{w.Name}}}\"")) && HudView.PlayersPage([]).Count == 1 + 7 * HudView.PlayerRows);
            Check("The HUD check's version button is the window's X: its id names this content, and it stands in for the plain close button while the server does not know the version (vcheck)",
                noComments.Contains($"<Button id=\"{HudVersion.Button(HudVersion.Content)}\" class=\"os-close os-hudver\">") && Regex.IsMatch(rules, @"\.os-screen\.vcheck \.os-hudver\s*\{\s*visibility:\s*visible;")
                && Regex.IsMatch(rules, @"\.os-screen\.vcheck #os_btn_close\s*\{\s*visibility:\s*collapse;") && Regex.Matches(noComments, @"id=""os_hudver_").Count == 1);
            Check("A restart coming shows in the window's foot, its words and its countdown; nothing there says everybody is frozen - everybody sees it",
                Regex.IsMatch(rules, @"\.os-global\.rst \.os-rstrip\s*\{\s*visibility:\s*visible;") && ids.Contains("os_rst_text") && ids.Contains("os_rst_secs")
                && HudView.RestartCountdown(new HudState { RestartKind = "daily", RestartAt = 1793000030 }, DateTimeOffset.FromUnixTimeSeconds(1793000000).UtcDateTime) == "0:30"
                && HudView.RestartCountdown(new HudState { RestartKind = "daily" }, now) == "After this match" && HudView.RestartCountdown(new HudState(), now) == ""
                && !rules.Contains("os-foot-frozen", StringComparison.Ordinal) && !noComments.Contains("os-foot-frozen", StringComparison.Ordinal));
            Check("The menu window has no knife behind its pages any more", !Regex.IsMatch(rules, @"\.os-pages\s*\{[^}]*background-image"));
            Check("The popup draws above the window, and a click on it is its reason, nothing else",
                noComments.IndexOf("class=\"os-win-foot\"", StringComparison.Ordinal) < whyFrom && whyFrom < noComments.IndexOf("class=\"os-topstack\"", StringComparison.Ordinal)
                && DownReasons.All.Select((r, i) => HudVote.WhyClick(HudVote.WhyButton(i + 1)) == r).All(x => x) && HudVote.WhyClick(HudVote.WhyButton(DownReasons.All.Length + 1)) == null
                && HudVote.WhyClick(HudView.ButtonDown) == null && HudVote.WhyClick("os_why_x") == null);
            Check("The dialog: 440 wide in the very centre of the screen, shown only while the player has a cursor (the window, or TAB) - its seconds run on meanwhile",
                Regex.IsMatch(rules, @"\.os-why\s*\{[^}]*width:\s*440px;[^}]*horizontal-align:\s*center;[^}]*vertical-align:\s*center;") && Regex.IsMatch(rules, @"\.os-screen\.why\.win \.os-why\s*\{[^}]*opacity:\s*1;")
                && Regex.IsMatch(rules, @"\.HUD_SCOREBOARD_VISIBLE \.os-screen\.why \.os-why\s*\{[^}]*opacity:\s*1;"));
            Check($"The popup's line drains over its {DownReasons.PopupSeconds} s, from the moment the plugin starts it, a second in",
                Regex.IsMatch(rules, $@"\.os-why-fill\s*\{{[^}}]*transition-duration:\s*{DownReasons.PopupSeconds - 1}s;") && Regex.IsMatch(rules, @"\.os-screen\.why-run \.os-why-fill\s*\{[^}]*scaleX\( 0 \);[^}]*transition-duration:\s*0s;"));
            var fixedWords = Regex.Matches(noComments, @"\btext=""([^""]*)""").Select(m => m.Groups[1].Value).ToList();
            var fixedCommands = fixedWords.SelectMany(w => Regex.Matches(w, @"![a-z]+").Select(m => m.Value)).ToList();
            Check($"The layout's own words name only primaries ({string.Join(" ", fixedCommands)})", fixedCommands.Count > 0 && fixedCommands.All(w => ChatCommands.Find(w) != null));
            var dropsRow = Array.FindIndex(Fun.Settings, s => s.Id == "drops") + 1;
            var catchUpRow = Array.FindIndex(Fun.Settings, s => s.Id == "catchup") + 1;
            Check("The grenade drops and catch-up are rows of the grid like any other, os_f17 and os_f18, Arms Race's alone; the layout has no fixed row left",
                (dropsRow, catchUpRow) == (17, 18) && !noComments.Contains("os_set_nades_on") && !noComments.Contains("os_set_catchup_on") && !noComments.Contains("{s:osfdrops}") && !noComments.Contains("{s:osfcatch}")
                && !Regex.IsMatch(rules, @"\.nd-on|\.nd-off|\.cu-on|\.cu-off|os-set-nades|os-set-catchup")
                && new[] { dropsRow, catchUpRow }.All(r => HudMenu.FunPage(cv => stock.GetValueOrDefault(cv)).Classes.Single(x => x.Id == HudMenu.FunRow(r) && x.Class == "used").On
                    && !HudMenu.FunPage(cv => stock.GetValueOrDefault(cv), armsRace: false).Classes.Single(x => x.Id == HudMenu.FunRow(r) && x.Class == "used").On));

            // ---- the Fun page and the Ads buttons, as the plugin fills and reads them ----
            var stockPage = HudMenu.FunPage(cv => stock.GetValueOrDefault(cv));
            bool FC(string id, string cls) => stockPage.Classes.Single(x => x.Id == id && x.Class == cls).On;
            string FT(string id) => stockPage.Texts.Single(x => x.Id == id).Text;
            Check($"Fun page: a row per setting ({Fun.Settings.Length} of the layout's {Fun.Rows}), the rest collapsed; a button per choice, the rest collapsed; on a stock server every first choice lit",
                Enumerable.Range(1, Fun.Rows).All(r => FC(HudMenu.FunRow(r), "used") == r <= Fun.Settings.Length)
                && Fun.Settings.Take(Fun.Rows).Select((s, i) => Enumerable.Range(1, Fun.ChoicesPerRow).All(k => FC(HudMenu.FunButton(i + 1, k), "used") == k <= s.Choices.Length
                    && FC(HudMenu.FunButton(i + 1, k), "on") == (k == 1) && FT($"{HudMenu.FunButton(i + 1, k)}_txt") == (k <= s.Choices.Length ? s.Choices[k - 1].Label : ""))).All(x => x)
                && FT("os_f1_title") == Fun.Settings[0].Title && FT("os_f1_txt") == Fun.Settings[0].Words);
            var moon = new Dictionary<string, string>(stock) { ["sv_gravity"] = "200", ["sv_airaccelerate"] = "2000" };
            var moonPage = HudMenu.FunPage(cv => moon.GetValueOrDefault(cv));
            var gravityRow = Array.FindIndex(Fun.Settings, s => s.Id == "gravity") + 1;
            var bhopRow = Array.FindIndex(Fun.Settings, s => s.Id == "bhop") + 1;
            Check("Fun page: Moon gravity lights Moon; a map's own air acceleration lights bunny hopping's Off and says the map runs its own",
                moonPage.Classes.Single(x => x.Id == HudMenu.FunButton(gravityRow, 3) && x.Class == "on").On && !moonPage.Classes.Single(x => x.Id == HudMenu.FunButton(gravityRow, 1) && x.Class == "on").On
                && moonPage.Classes.Single(x => x.Id == HudMenu.FunButton(bhopRow, 1) && x.Class == "on").On && moonPage.Texts.Single(x => x.Id == $"{HudMenu.FunRow(bhopRow)}_txt").Text.EndsWith("  ·  This map runs its own"));
            Check("Fun page: a click names its setting and choice, and nothing else does (the size chips are os_fl_*, the note a label)",
                HudMenu.FunClick(HudMenu.FunButton(gravityRow, 2)) is ({ Id: "gravity" }, { Id: "low" }) && HudMenu.FunClick(HudMenu.FunButton(gravityRow, 1)) is (_, { Id: "normal" })
                && HudMenu.FunClick(HudMenu.FunButton(Fun.Rows + 1, 1)) == null && HudMenu.FunClick(HudMenu.FunButton(2, 3)) == null
                && HudMenu.FunClick("os_fl_all") == null && HudMenu.FunClick(HudMenu.FunNote) == null && HudMenu.FunClick("os_f1_x") == null
                && Fun.Settings.Take(Fun.Rows).Select((s, i) => s.Choices.Select((c, k) => HudMenu.FunClick(HudMenu.FunButton(i + 1, k + 1)) is var (fs, fc) && fs == s && fc == c).All(x => x)).All(x => x));
            Check("Fun page: its note says how long a change lasts, in the bridge's numbers", HudMenu.FunNoteText.Contains($"{ResetEmptyMinutes} minutes with nobody on") && StrayNumbers(HudMenu.FunNoteText).Count == 0
                && Fun.Settings.All(s => StrayNumbers($"{s.Title} {s.Words} {string.Join(" ", s.Choices.Select(c => c.Label))}").Count == 0));
            // Friendly fire's three: Off on friendly fire dims the rows that need it, which then say so instead of their words.
            var ffOff = new Dictionary<string, string>(stock) { ["mp_friendlyfire"] = "false" };
            var ffOffPage = HudMenu.FunPage(cv => ffOff.GetValueOrDefault(cv));
            var ffRow = Array.FindIndex(Fun.Settings, s => s.Id == "ff") + 1;
            var needing = Fun.Settings.Select((s, i) => (Setting: s, Row: i + 1)).Where(x => x.Setting.Needs?.Any(n => n.Setting == "ff") == true).ToList();
            bool Idle((List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) page, int r) => page.Classes.Single(x => x.Id == HudMenu.FunRow(r) && x.Class == "idle").On;
            Check($"Fun page: friendly fire Off lights its Off and dims the {needing.Count} rows that need it ({string.Join(", ", needing.Select(x => x.Setting.Title))}), which say so instead of their words; nothing else dims, and nothing on a stock server",
                ffRow > 0 && ffOffPage.Classes.Single(x => x.Id == HudMenu.FunButton(ffRow, 2) && x.Class == "on").On && needing.Count == 2
                && needing.All(x => Idle(ffOffPage, x.Row) && ffOffPage.Texts.Single(t => t.Id == $"{HudMenu.FunRow(x.Row)}_txt").Text == x.Setting.Needs!.First(n => n.Setting == "ff").Idle
                    && ffOffPage.Classes.Single(c => c.Id == HudMenu.FunButton(x.Row, 1) && c.Class == "on").On)
                && Enumerable.Range(1, Fun.Rows).Where(r => needing.All(x => x.Row != r)).All(r => !Idle(ffOffPage, r))
                && Enumerable.Range(1, Fun.Rows).All(r => !Idle(stockPage, r)));
            // Free-for-all on dims friendly fire's three - everybody is an enemy, so they change nothing.
            var ffaOn = new Dictionary<string, string>(stock) { ["mp_teammates_are_enemies"] = "true" };
            var ffaPage = HudMenu.FunPage(cv => ffaOn.GetValueOrDefault(cv));
            var friendly = new[] { "ff", "instakill", "ffnades" }.Select(id => Array.FindIndex(Fun.Settings, s => s.Id == id) + 1).ToList();
            Check("Fun page: free-for-all On dims friendly fire, Instakill and Grenades, each saying free-for-all makes them do nothing; nothing else dims",
                friendly.All(r => r > 0 && Idle(ffaPage, r) && ffaPage.Texts.Single(t => t.Id == $"{HudMenu.FunRow(r)}_txt").Text.Contains("free-for-all"))
                && Enumerable.Range(1, Fun.Rows).Where(r => !friendly.Contains(r)).All(r => !Idle(ffaPage, r)));
            Check("Fun page: a dimmed row's buttons still work - Instakill Off and Grenades On are clicks while friendly fire is off",
                needing.All(x => HudMenu.FunClick(HudMenu.FunButton(x.Row, 2)) is var (fs, fc) && fs == x.Setting && fc == x.Setting.Choices[1]));

            // ---- the Fun page's Random and Default ----
            var dice = new Random(2026);
            var drawn = Fun.Settings.ToDictionary(s => s.Id, s => new int[s.Choices.Length]);
            var (nadesOn, moonJump, highJump, armsCount, catchUpOn) = (0, 0, 0, 0, 0);
            const int draws = 6000;
            for (var i = 0; i < draws; i++)
            {
                var picks = Fun.Randomized(n => dice.Next(n), armsRace: true);
                if (picks.Any(x => x.Setting.Id == "catchup" && x.Choice.Id == "on")) catchUpOn++;
                foreach (var (s, c) in picks) drawn[s.Id][Array.IndexOf(s.Choices, c)]++;
                if (picks.Any(x => x.Setting.Id == "drops" && x.Choice.Id == "on")) nadesOn++;
                var isMoon = picks.Any(x => x.Setting.Id == "gravity" && x.Choice.Id == "moon");
                var high = picks.Any(x => x.Setting.Id == "jump" && x.Choice != x.Setting.Normal);
                if (isMoon && high) moonJump++;
                if (high) highJump++;
                armsCount = picks.Count;
            }
            var otherDraw = Fun.Randomized(n => dice.Next(n), armsRace: false);
            var uneven = Fun.Settings.Where(s => s.Id is not ("jump" or "catchup")).Where(s => drawn[s.Id].Any(k => Math.Abs(k - (double)draws / s.Choices.Length) > (double)draws / s.Choices.Length * 0.1)).Select(s => s.Id).ToList();
            Check($"Fun's Random: every setting the page shows, each choice equally likely ({draws} draws, each within 10%{(uneven.Count > 0 ? "; not " + string.Join(" ", uneven) : "")}), the grenade drops on in {nadesOn}; Moon gravity never with Super jump ({highJump} high jumps, all on other gravity)",
                armsCount == Fun.Settings.Length && uneven.Count == 0 && nadesOn is > 2700 and < 3300 && moonJump == 0 && highJump is > 1700 and < 2300);
            string CatchUpOf(List<(Fun.Setting Setting, Fun.Choice Choice)> p) => p.SingleOrDefault(x => x.Setting.Id == "catchup").Choice?.Id ?? "none";
            Check($"...and catch-up on three times in four: on in {catchUpOn} of {draws}, off on the dice's 0 alone, Arms Race's own",
                catchUpOn is > 4300 and < 4700 && CatchUpOf(Fun.Randomized(n => n - 1, armsRace: true)) == "on" && CatchUpOf(Fun.Randomized(n => 0, armsRace: true)) == "off"
                && CatchUpOf(Fun.Randomized(n => 0, armsRace: false)) == "none");
            Check("...outside Arms Race it leaves out what the page leaves out there: friendly fire's three, the grenade drops and catch-up",
                otherDraw.Count == Fun.Settings.Count(s => !Fun.ArmsRaceOnly(s)) && otherDraw.All(x => !Fun.ArmsRaceOnly(x.Setting))
                && Fun.Randomized(n => n - 1, armsRace: true).Single(x => x.Setting.Id == "jump").Choice == Fun.Find("jump")!.Normal);
            var gravity = Fun.Find("gravity")!;
            var everyChange = Fun.Settings.Select(s => (s, s.Choices[^1])).ToList();
            Check("...and its one message says who and what changed, the rest counted past 200 characters; Default's says what it did",
                M.Plain(Fun.RandomLine("Remy", [(gravity, Fun.Find(gravity, "low")!), (Fun.Find("drops")!, Fun.Find(Fun.Find("drops")!, "off")!)])) == "Remy rolled: gravity low, grenade drops off"
                && M.Plain(Fun.RandomLine("Remy", [])) == "Remy rolled: nothing changed" && M.Plain(Fun.RandomLine("Remy", everyChange)) is { Length: <= 200 } longLine && longLine.EndsWith(" more")
                && M.Plain(Fun.DefaultLine("Remy")) == "Remy put every fun setting back to normal"
                && M.Plain(Fun.RandomLine("Remy", [(Fun.Find("catchup")!, Fun.Find(Fun.Find("catchup")!, "off")!)])) == "Remy rolled: catch-up off");
            // The Fun page's height (eighteen rows in two columns of nine): the pages have PagesHeight px (the window's 920 less
            // its rule, head, tabs, sub-tabs and foot); the Fun page spends its padding, the note's line (about 1.3 times its type) and margin, and a column's rows
            // with their margins. A row holds its title line (the buttons' height, at least 1.3 times the title's type) over its words (1.3 times
            // their type).
            var funPad = Regex.Match(rules, @"(?m)^\.os-page\s*\{[^}]*padding:\s*(\d+)px \d+px (\d+)px"); // every page's one padding (content v4.14.0)
            var funNote = Regex.Match(rules, @"\.os-fun-note\s*\{[^}]*margin-bottom:\s*(\d+)px;[^}]*font-size:\s*(\d+)px;");
            var funRowCss = Regex.Match(rules, @"\.os-frow\s*\{[^}]*height:\s*(\d+)px;[^}]*margin-bottom:\s*(\d+)px;");
            var funTop = Regex.Match(rules, @"\.os-frow-top\s*\{[^}]*height:\s*(\d+)px;");
            var funTitle = Regex.Match(rules, @"\.os-frow-title\s*\{[^}]*font-size:\s*(\d+)px;");
            var funTxt = Regex.Match(rules, @"\.os-frow-txt\s*\{[^}]*margin-top:\s*(\d+)px;[^}]*font-size:\s*(\d+)px;");
            var funButton = Regex.Match(rules, @"\.os-funb\s*\{[^}]*height:\s*(\d+)px;");
            var funTopCss = Regex.Match(rules, @"\.os-funtop\s*\{[^}]*margin-bottom:\s*(\d+)px;"); // content v4.4.0: the note's line holds Random and Default
            var funAllCss = Regex.Match(rules, @"\.os-funallb\s*\{[^}]*height:\s*(\d+)px;");
            static int Px(Match m, int g) => int.Parse(m.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture);
            var funParsed = funPad.Success && funNote.Success && funRowCss.Success && funTop.Success && funTitle.Success && funTxt.Success && funButton.Success && funTopCss.Success && funAllCss.Success;
            var funXml = xml[xml.IndexOf("os-page-fun", StringComparison.Ordinal)..];
            funXml = funXml[..(funXml.IndexOf("os-page-bots", StringComparison.Ordinal) is > 0 and var funEnd ? funEnd : funXml.Length)]; // the Bots page comes after it, with rows of its own
            var funSplit = funXml.IndexOf("os-fcol-right", StringComparison.Ordinal);
            var perColumn = Math.Max(Regex.Matches(funXml[..funSplit], @"class=""os-frow[ ""]").Count, Regex.Matches(funXml[funSplit..], @"class=""os-frow[ ""]").Count);
            // the note's line: the taller of the note and Random's and Default's buttons, and its row's margin (a real client showed the
            // last row under the window's foot with 40 px buttons and an 8 px margin there: 22 px past what fitted)
            var funTopLine = funParsed ? Math.Max((int)Math.Ceiling(Px(funNote, 2) * 1.3), Px(funAllCss, 1)) + Px(funTopCss, 1) : 0;
            var funHeight = funParsed ? Px(funPad, 1) + Px(funPad, 2) + funTopLine + perColumn * (Px(funRowCss, 1) + Px(funRowCss, 2)) : int.MaxValue;
            var rowNeeds = funParsed ? Math.Max(Px(funTop, 1), Math.Max((int)Math.Ceiling(Px(funTitle, 1) * 1.3), Px(funButton, 1))) + Px(funTxt, 1) + (int)Math.Ceiling(Px(funTxt, 2) * 1.3) : int.MaxValue;
            Check($"Fun page fits the window: about {funHeight} px of the pages' {PagesHeight} for its {Fun.Rows} rows ({perColumn} in the longer column), and a row's {(funParsed ? Px(funRowCss, 1) : 0)} px hold its title line and words (about {rowNeeds} px)",
                funHeight <= PagesHeight && funParsed && rowNeeds <= Px(funRowCss, 1) && perColumn == Fun.Rows / 2 && funTopLine <= (int)Math.Ceiling(Px(funNote, 2) * 1.3) + 8);
            // The Bots page's height (content v4.8.0: twelve rows in two columns of six, as the Fun page's rows, and the presets under them): its padding, the same
            // top line, a column's rows, the presets' head and their cards with their margins. Its five buttons a row fit a column beside the row's title.
            var botsPad = funPad;
            var presetsHead = Regex.Match(rules, @"\.os-bpresets-head\s*\{[^}]*height:\s*(\d+)px;[^}]*margin-top:\s*(\d+)px;");
            var presetsRow = Regex.Match(rules, @"\.os-bpresets\s*\{[^}]*height:\s*(\d+)px;[^}]*margin-top:\s*(\d+)px;");
            var botButton = Regex.Match(rules, @"\.os-botb\s*\{[^}]*width:\s*(\d+)px;");
            var funButtonMargin = Regex.Match(rules, @"\.os-botb\s*\{[^}]*margin-right:\s*(\d+)px;"); // a bot's choice has its own margin (the review of 218r4: 80 px wide, for SHOTGUNS)
            var botsXml = xml[xml.IndexOf("os-page-bots", StringComparison.Ordinal)..];
            var botsSplit = botsXml.IndexOf("os-fcol-right", StringComparison.Ordinal);
            var botsPerColumn = Math.Max(Regex.Matches(botsXml[..botsSplit], @"class=""os-frow[ ""]").Count, Regex.Matches(botsXml[botsSplit..botsXml.IndexOf("os-bpresets-head", StringComparison.Ordinal)], @"class=""os-frow[ ""]").Count);
            var botsParsed = funParsed && botsPad.Success && presetsHead.Success && presetsRow.Success && botButton.Success && funButtonMargin.Success;
            var botsHeight = botsParsed ? Px(botsPad, 1) + Px(botsPad, 2) + funTopLine + botsPerColumn * (Px(funRowCss, 1) + Px(funRowCss, 2)) + Px(presetsHead, 1) + Px(presetsHead, 2) + Px(presetsRow, 1) + Px(presetsRow, 2) : int.MaxValue;
            var botsButtons = botsParsed ? Bots.ChoicesPerRow * (Px(botButton, 1) + Px(funButtonMargin, 1)) : int.MaxValue; // a column: 572 px with the row's padding and mark (15), the title the rest
            Check($"Bots page fits the window: about {botsHeight} px of the pages' {PagesHeight} for its {Bots.Rows} rows ({botsPerColumn} in the longer column) and its presets; a row's {Bots.ChoicesPerRow} buttons take {botsButtons} px of a column's 572",
                botsHeight <= PagesHeight && botsParsed && botsPerColumn == Bots.Rows / 2 && botsButtons <= 572 - 140);
            // ---- content v4.4.0: the Maps page while the vote is open - a Play adds to it - and every card's mode; Our maps' glow ----
            var voteCat = new HudCatalog { Version = 1 };
            voteCat.Maps.Add(new HudCatalog.Entry("3084291314", "1", 1, 3084291314, "AIM Map", 20, false, -1, 0, 0, 0));
            voteCat.Maps.Add(new HudCatalog.Entry("3084291315", "2", 2, 3084291315, "Other Map", 20, false, -1, 0, 0, 0));
            var openVote = VoteState.Decode("mode\tarmsrace\tArms Race\nmv\t1793700000\topen\t40\t2\t1\tend\t0\t1\nmvo\t1\tarmsrace\trolled\t1\t3084291314\t#1 AIM Map\t20 MB\t1\t\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t0\t\nmvv\t1001\t1\n");
            string Play((List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) page, int k) => page.Texts.Single(x => x.Id == $"os_m{k}_play_text").Text;
            bool VC((List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) page, string id, string cls) => page.Classes.Single(x => x.Id == id && x.Class == cls).On;
            var voter = HudMenu.Maps(voteCat, new HudState(), new HudBrowser(), 1001, now, vote: openVote);
            var other = HudMenu.Maps(voteCat, new HudState(), new HudBrowser(), 1002, now, vote: openVote);
            var noVote = HudMenu.Maps(voteCat, new HudState(), new HudBrowser(), 1002, now);
            Check("The map browser while the vote is open: a map on the ballot says On the vote (or Your vote), any other one Add to the vote; with no vote, Put to the vote",
                Play(voter, 1) == "Your vote" && Play(voter, 2) == "Add to the vote" && Play(other, 1) == "On the vote" && Play(noVote, 1) == "Put to the vote" && Play(noVote, 2) == "Put to the vote");
            var oursOn = new HudBrowser();
            oursOn.Click(HudBrowser.OursButton, 0);
            var oursPage = HudMenu.Maps(voteCat, new HudState(), oursOn, 1002, now);
            Check("Every card wears the mode it plays in, exactly one; an empty card none; Our maps glows until it is chosen (fl-ours on os_screen)",
                VC(noVote, "os_m1", "mode-armsrace") && !VC(noVote, "os_m1", "mode-deathmatch") && Modes.All.All(m => !VC(noVote, "os_m3", $"mode-{m.Id}"))
                && Enumerable.Range(1, 2).All(k => Modes.All.Count(m => VC(noVote, $"os_m{k}", $"mode-{m.Id}")) == 1)
                && !VC(noVote, HudView.Screen, "fl-ours") && VC(oursPage, HudView.Screen, "fl-ours"));
            // ---- content v4.5.0: the Fun page's changed marks, the centre notice, and what the layout and stylesheet hold for them ----
            var lowGravity = Fun.Settings.SelectMany(s => s.Stock).GroupBy(x => x.ConVar).ToDictionary(g => g.Key, g => g.First().Value);
            lowGravity["sv_gravity"] = "400";
            var changedPage = HudMenu.FunPage(cv => lowGravity.GetValueOrDefault(cv));
            var gravityRow2 = Array.FindIndex(Fun.Settings, s => s.Id == "gravity") + 1;
            var setBy = HudMenu.FunPage(cv => lowGravity.GetValueOrDefault(cv), true, id => id == "gravity" ? "Alice" : "Bob");
            Check("Fun page: who set a setting that is off its default is in its row's words (Low - Set by Alice); a setting at its default says nobody, whoever set it",
                setBy.Texts.Single(x => x.Id == $"{HudMenu.FunRow(gravityRow2)}_txt").Text.StartsWith("Low - Set by Alice  ·  ")
                && Enumerable.Range(1, Fun.Rows).Where(r => r != gravityRow2).All(r => !setBy.Texts.Single(x => x.Id == $"{HudMenu.FunRow(r)}_txt").Text.Contains("Set by"))
                && !noComments.Contains("Stays as you set it"));
            Check("Game page: a match length that is not the default says who set it; the default says nobody",
                HudMenu.Game(new HudState { RoundMinutes = 10, RoundBy = "Bob" }, 0).Texts.Single(x => x.Id == "os_set_time_now").Text == "Matches last 10 minutes - Set by Bob"
                && HudMenu.Game(new HudState { RoundMinutes = 60 }, 0).Texts.Single(x => x.Id == "os_set_time_now").Text == "Matches last 60 minutes"
                && HudState.Decode(new HudState { RoundMinutes = 10, RoundBy = "Bob" }.Encode()) is { RoundMinutes: 10, RoundBy: "Bob" });
            Check("Fun page: a setting off its default is marked changed (gold, with a dot); every other row plain",
                changedPage.Classes.Single(x => x.Id == HudMenu.FunRow(gravityRow2) && x.Class == "changed").On
                && Enumerable.Range(1, Fun.Rows).Where(r => r != gravityRow2).All(r => !changedPage.Classes.Single(x => x.Id == HudMenu.FunRow(r) && x.Class == "changed").On)
                && Enumerable.Range(1, Fun.Rows).All(r => !stockPage.Classes.Single(x => x.Id == HudMenu.FunRow(r) && x.Class == "changed").On));
            string[] v45Classes = ["vlead", "vtie", "vhas", "vstrip", "vlock", "changed", "vrand", .. Enumerable.Range(0, 11).Select(k => $"w{k}")];
            var v45Unstyled = v45Classes.Where(cls => !Regex.IsMatch(rules, $@"\.{Regex.Escape(cls)}\b")).ToList();
            Check($"Where the votes go: its classes each have a rule{(v45Unstyled.Count > 0 ? ": none for " + string.Join(", ", v45Unstyled) : "")}; the layout holds every card's bar and its voters on pointing, the strip's nine segments and the waiting line",
                v45Unstyled.Count == 0 && Enumerable.Range(1, HudMenu.Cards).All(k => noComments.Contains($"text=\"{{s:{HudVote.HoverVar(k)}}}\""))
                && Regex.Matches(noComments, "class=\"os-mvbar\"").Count == HudMenu.Cards && Enumerable.Range(1, HudVote.StripSegments).All(i => noComments.Contains($"<Panel id=\"{HudVote.Segment(i)}\" class=\"os-vseg\"><Label class=\"os-vseg-text\" text=\"{{s:{HudVote.StripVar(i)}}}\" /></Panel>"))
                && noComments.Contains($"text=\"{{s:{HudVote.WaitVar}}}\"") && Regex.IsMatch(rules, @"\.os-screen\.vlock \.os-close\s*\{\s*visibility:\s*collapse;"));
            var adsCat = new HudCatalog { Version = 1 };
            adsCat.Maps.Add(new HudCatalog.Entry("3084291314", "1", 1, 3084291314, "AIM Map", 20, false, -1, 0, 0, 2, Ads: 1));
            adsCat.Votes[1001] = new() { ["3084291314"] = HudCatalog.AdsVote };
            var adsMenu = HudMenu.Maps(adsCat, new HudState(), new HudBrowser(), 1001, now);
            Check("Cards: a map with an ads mark shows the Ads tag, and this player's ads mark is a Dislike there",
                adsMenu.Classes.Single(x => x.Id == "os_m1" && x.Class == "ads").On && adsMenu.Classes.Single(x => x.Id == "os_m1" && x.Class == "disliked").On && Hud.IsAction("!down 1 ads"));
            var adsLive = new HudState { MyVotes = new() { [7] = HudCatalog.AdsVote } };
            Check("The board: the player's own ads mark on the map that is on says so, and lights Dislike",
                HudView.PlayerClasses(adsLive, 7).Single(x => x.Class == "voted-down").On && HudView.PlayerTexts(adsLive, 7, "x", false).Single(x => x.Id == "os_b_you").Text == "You marked this map: ads");
            Check("Help: one Fun row, one row for a Dislike's reasons, no Ads row, and no fifth row in any group",
                Hud.Help.Count(g => g.Rows.Any(r => r.Command.StartsWith("Fun"))) == 1 && !Hud.Help.Any(g => g.Rows.Any(r => r.Command == "Ads")) && !noComments.Contains("os-crow-c32"));
            // The Help page's height with its tighter rows (1 px a row each way, 4 + 5 px a group): the budget of the check above with those numbers.
            static int LinesTight(Hud.Row r) => (int)Math.Ceiling((r.Also == "" ? r.Words : $"{r.Words}  ·  {r.Also}").Length / 60.0);
            var helpTight = Hud.Help.Sum(g => g.Rows.Sum(r => LinesTight(r) * 19 + 2) + 10);
            Check($"Help with content {HudVersion.Content}: about {helpTight} px of its {HelpHeight} with its tighter rows", helpTight <= HelpHeight && Regex.IsMatch(rules, @"\.os-crow\s*\{\s*padding:\s*1px 0px;"));

            // ---- the thumbnails: a rule per map the menu can show, each its own map's picture, out of a few class names ----
            var thumbClasses = new HashSet<string>();
            if (File.Exists(HudThumbFile))
            {
                var thumbCss = File.ReadAllText(HudThumbFile);
                var thumbRules = Regex.Matches(thumbCss, @"^\.(th\d+)\.(tl\d+)\s*\{\s*background-image:\s*url\(\s*""([^""]+)""\s*\);\s*\}", RegexOptions.Multiline)
                    .ToDictionary(x => x.Groups[1].Value + "." + x.Groups[2].Value, x => x.Groups[3].Value);
                thumbClasses = thumbRules.Keys.SelectMany(k => k.Split('.')).ToHashSet();
                var shown = Maps.Where(x => !IsNonMap(x)).Concat(Officials).Select(x => new HudCatalog.Entry("", "", x.Rank, x.Id, x.Name, x.SizeMb, x.Official, -1, 0, 0, 0)).ToList();
                static string Rule(HudCatalog.Entry x) => string.Join(".", HudThumbs.Classes(x));
                // Every map the menu shows has its own picture. The hand-picked maps after the 1,000 most-subscribed - the 16 glass maps of
                // 2026-10-21, #1001-#1016 - got theirs with layout release v3.0.0: a map listed after a release has none until the next one
                // (its card shows an empty picture), since each release is a new Workshop item that waits for Valve's approval.
                // Since content v3.1.0 (2026-10-22) the list can grow after a picture release: maps listed after it (the tail of the list,
                // ranked after every map that has a picture) show the card's plain backdrop until the next release numbers them. Anything else
                // without a picture - a gap inside the list, an official map - is a release that forgot thumbs.py.
                var lastPictured = shown.Where(x => !x.Official && HudThumbs.Classes(x).Length == 2).Select(x => x.Rank).DefaultIfEmpty(0).Max();
                var tail = shown.Where(x => !x.Official && x.Rank > lastPictured).ToList();
                var pictured = shown.Except(tail).ToList();
                var late = pictured.Where(x => !x.Official && x.Rank > 1000).ToList();
                var without = pictured.Where(x => HudThumbs.Classes(x).Length != 2 || !thumbRules.ContainsKey(Rule(x))).Select(x => x.Official ? x.Name : $"#{x.Rank}").ToList();
                Check($"Thumbnails: every map the menu shows has its picture's rule ({thumbRules.Count}){(without.Count > 0 ? ": none for " + string.Join(", ", without.Take(8)) : "")}"
                    + (tail.Count > 0 ? $" - but the {tail.Count} listed after the last picture release (#{tail.Min(x => x.Rank)}-#{tail.Max(x => x.Rank)}) show the plain backdrop until the next one" : ""),
                    without.Count == 0);
                Check($"Thumbnails: the {late.Count} hand-picked and added maps after #1000 up to the last picture release have theirs too", late.All(x => HudThumbs.Classes(x).Length == 2));
                Check("Thumbnails: each map's rule is its own picture - a listed map's JPEG in the addon, an official map's the game's own screenshot",
                    pictured.All(x => thumbRules.GetValueOrDefault(Rule(x)) is { } url && (x.Official
                        ? url == $"s2r://panorama/images/map_icons/screenshots/360p/{x.Name}_png.vtex" : url == $"file://{{images}}/custom_game/gungame_hud/thumbs/{x.Id}.jpg")));
                var thumbNumbers = Enumerable.Range(0, HudThumbList.Keys.Length).Select(n => string.Join(".", HudThumbs.Classes(n))).ToHashSet();
                Check($"Thumbnails: every picture numbered once ({HudThumbList.Keys.Length}), in {HudThumbs.Base} x {HudThumbs.MaxHigh} numbers, and no rule without a number (a map not listed yet has its rule too: hud/thumbs.py --ahead)",
                    HudThumbList.Keys.Distinct().Count() == HudThumbList.Keys.Length && HudThumbs.Count == HudThumbList.Keys.Length
                    && HudThumbList.Keys.Length <= HudThumbs.Base * HudThumbs.MaxHigh && thumbRules.Keys.All(thumbNumbers.Contains));
                Check($"Thumbnails: {thumbClasses.Count} class names for all {thumbRules.Count} pictures, every one a picture class",
                    thumbClasses.All(HudThumbs.IsThumbClass) && thumbClasses.Count <= HudThumbs.Base + HudThumbs.MaxHigh && !Regex.IsMatch(rules, @"\.t[hl]\d"));
                Check("Thumbnails: the layout includes their stylesheet, and it has nothing but those rules (no comma selector, no keyframes)",
                    xml.Contains($"s2r://panorama/styles/custom_game/gungame_hud_thumbs_{Hud.ReleaseFile}.vcss_c") && Regex.Matches(Regex.Replace(thumbCss, @"/\*.*?\*/", "", RegexOptions.Singleline), @"^\{", RegexOptions.Multiline).Count == thumbRules.Count);
            }
            else Console.WriteLine($"skip thumbnail checks: {HudThumbFile} not found");

            // ---- the game's own limit: one layout entity interns at most 1,024 panel ids and 1,024 class names that the server sets, for
            // its whole life and for all players together. Past it the server logs "The maximum number of ... has been reached" and the
            // client's behaviour is unknown. Every class the plugin sets has a rule (checked above), so the stylesheets bound it - but for the latest-N rules, which are for
            // later servers: this server sets one, its own level (HudVersion.Classes), and a later one sets another. ----
            var styleClasses = Regex.Matches(rules, @"([^{}]+)\{").SelectMany(x => Regex.Matches(x.Groups[1].Value, @"\.([A-Za-z_][\w-]*)").Select(y => y.Groups[1].Value)).Where(c => !Regex.IsMatch(c, @"^latest-\d+$"))
                .Concat(thumbClasses).Concat(HudVersion.Classes().Select(c => c.Class)).ToHashSet();
            Check($"Limit: at most 1,024 class names and 1,024 panel ids per entity - the server can set {styleClasses.Count} classes, the layout {ids.Count} ids (both under 900)",
                styleClasses.Count < 900 && ids.Count < 900);
            Check("Limit: of the latest-N names the server sets one, its own level", HudVersion.Classes().Count(c => c.Class.StartsWith("latest-", StringComparison.Ordinal)) == 1);
            Check("A value starting with # (a rank, a name) is kept from being read as a localization token, and nothing else is touched",
                Hud.Plain("#5") == "\u2060#5" && Hud.Plain("Map #5") == "Map #5" && Hud.Plain("") == "");
        }
        else Console.WriteLine($"skip HUD layout checks: {HudLayoutFile} or {HudStyleFile} not found");
    }

    /// <summary>For comparing words: typographic apostrophes and dashes as plain ones, spaces collapsed.</summary>
    static string Norm(string s) => Regex.Replace(s.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u2013', '-').Replace('\u2014', '-').Replace('\u00a0', ' '), @"\s+", " ").Trim();
}
