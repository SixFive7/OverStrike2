// --selftest, part three: the HUD's words and wiring (Hud.cs). Every panel id and class the
// plugin sends must exist in the Workshop layout, and every label in the layout must be fed - a mismatch there shows nothing on screen
// and logs nothing anywhere, so this is the only place it can be caught before players see it.
using System.Text.RegularExpressions;

static partial class Program
{
    const string HudLayoutFile = "../hud/layout/gungame_hud_" + Hud.ReleaseFile + ".xml", HudStyleFile = "../hud/styles/gungame_hud_" + Hud.ReleaseFile + ".css",
        HudThumbFile = "../hud/styles/gungame_hud_thumbs_" + Hud.ReleaseFile + ".css";

    /// <summary>The pages' height: the window's 820 px less its rule (3), head (76), tabs (48), sub-tabs (40) and foot (44); the Help page's, less its
    /// padding (14 + 20).</summary>
    const int PagesHeight = 820 - 3 - 76 - 48 - 40 - 44, HelpHeight = PagesHeight - 34;

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
        Check("Pages of eight: 20 maps are three pages, the last one has four", HudBrowser.Pages(20) == 3 && HudBrowser.Pages(0) == 1 && pb.PageOf(all).Count == 8);
        pb.Click(HudBrowser.NextButton, 20); pb.Click(HudBrowser.NextButton, 20); pb.Click(HudBrowser.NextButton, 20);
        Check("Next stops at the last page, which shows the rest", pb.Page == 2 && pb.PageOf(all).Select(m => m.Arg).SequenceEqual(["17", "18", "19", "20"]));
        pb.Click(HudBrowser.FirstButton, 20); pb.Click(HudBrowser.PrevButton, 20);
        Check("First and Prev stop at the first page, Last jumps to the end", pb.Page == 0 && pb.Click(HudBrowser.LastButton, 20) && pb.Page == 2);
        pb.Page = 9;
        Check("A page past the end (the list shrank under a filter) is clamped to the last one", pb.PageOf(all).Count == 4 && pb.Page == 2);
        var hint = new HudBrowser();
        hint.Click("os_pg_next", all.Count);
        Check("Pages of eight always: the search hint, and v4.8.0's search box and keys, move no page and change no page size (no keyboard covers a row any more)",
            !hint.Click(HudBrowser.HintButton, all.Count) && !hint.Click(HudBrowser.OldSearchBox, all.Count) && !hint.Click("os_kb_done", all.Count)
            && hint.Page == 1 && hint.PagesFor(all.Count) == 3 && hint.PageOf(all).Select(m => m.Arg).SequenceEqual(["9", "10", "11", "12", "13", "14", "15", "16"]));
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
            && HudMenu.Command("os_m1_play", back, live, mb, 1001, now) == "!map 1" && HudMenu.Command("os_m1_play", back, live, mb, 1002, now) == "!map 1"
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
        var (qt, qc) = HudMenu.PreviousButton(live);
        Check("Previous: the map before this one on its button, which puts it to the vote; off while there is none",
            qt.Single(x => x.Id == "os_q_prev_text").Text == "Previous: #2 Zebra" && !qc.Single(x => x.Id == HudMenu.Previous && x.Class == "off").On
            && HudMenu.Command(HudMenu.Previous, back, live, mb, 1001, now) == "!previous" && HudMenu.PreviousButton(new HudState()).Classes.Single(x => x.Id == HudMenu.Previous).On
            && HudMenu.Command(HudMenu.Previous, back, new HudState(), mb, 1001, now) == null);
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
            var fed = Regex.Matches(xml, @"<Label\b[^>]*\bid=""([^""]+)""[^>]*text=""\{s:text\}""").Select(x => x.Groups[1].Value).ToHashSet();
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
            var sentTexts = HudView.StaticTexts().Concat(HudView.Texts(full)).Concat(HudView.PlayerTexts(full, 1, "x", true)).Concat(menuMaps.Texts).Concat(menuQueue.Texts)
                .Concat(menuGame.Texts).Concat(card.Texts()).Concat(funPage.Texts).Concat(botsPage.Texts).Concat(ballotView.Texts).Concat(HudView.LatelyTexts(full, 1, 0)).Select(t => t.Id).Append("os_rst_secs").Append("os_cu_head")
                .Concat(Enumerable.Range(1, 6).Select(k => $"os_cu_{k}")).ToList();
            List<(string Id, string Class, bool On)> sentClasses = HudView.StaticClasses().Concat(HudView.Classes(full)).Concat(HudView.PlayerClasses(full, 1)).Concat(menuMaps.Classes).Concat(menuQueue.Classes)
                .Concat(menuGame.Classes).Concat(card.Classes()).Concat(funPage.Classes).Concat(botsPage.Classes).Concat(ballotView.Classes).Concat(HudView.LatelyClasses(full, 1))
                .Concat(Enumerable.Range(1, 6).SelectMany(k => new[] { ($"os_cu_{k}", "used", true), ($"os_cu_{k}", "good", true), ($"os_cu_{k}", "bad", false) })).ToList();
            string[] screenClasses = ["win", "win-in", "draining", "toast-1", "toast-in", "vcheck", "vjoin", "vbrowse", "vlock", "wback", "cu", "cu-in", "cu-run",
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
                .Concat([HudView.BoardUp, HudView.BoardDown, HudView.BoardNext, HudBrowser.HintButton, WorkshopCard.Play]).ToList();
            var thumbPanels = HudMenu.Thumbs(back, new HudBrowser(), 1001).Select(x => x.Panel).ToList();
            Check($"Layout: the eight cards' pictures exist ({thumbPanels.Count})", thumbPanels.Count == HudMenu.Cards && thumbPanels.All(ids.Contains));
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
            Check($"Stylesheet: the drain bar empties in exactly the idle close ({HudSlots.IdleCloseSeconds} s)",
                Regex.IsMatch(rules, $@"\.os-drain-fill\s*\{{[^}}]*transition-duration:\s*{HudSlots.IdleCloseSeconds}s;") && Regex.IsMatch(rules, @"\.draining \.os-drain-fill\s*\{[^}]*scaleX\( 0 \);[^}]*transition-duration:\s*0s;"));

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
                // The panel v4.10.0 retires: v4.9.0's version button, whose place this content's own takes. Every other panel keeps its id.
                var expectedRetired = new HashSet<string> { HudVersion.Button(replaced) };
                Check($"Content {HudVersion.Content}: it keeps every panel of {replaced} ({last.Count}), by its id, but the {retired.Count} it retires, each for a reason (the version button of {replaced}){(retired.SetEquals(expectedRetired) ? "" : ": unexpected " + string.Join(" ", retired.Except(expectedRetired).Concat(expectedRetired.Except(retired).Select(x => "-" + x))))}",
                    retired.SetEquals(expectedRetired));
                Check($"Content {HudVersion.Content}: nothing the plugin sends goes to a panel this content lacks - the retired ones neither - and the Previous button stays (on the Maps page)",
                    sent.All(ids.Contains) && ids.Contains(HudMenu.Previous) && ids.Contains("os_q_prev_text"));
                var addedIdle = added.Where(id => !(sent.Contains(id) || buttons.Contains(id) || id.EndsWith("_thumb") || id is "os_vidle" or "os_vlim")).ToList();
                // ...its own version button alone: the match length's row shows in Deathmatch by the stylesheet, with no panel of its own.
                var expectedAdded = new HashSet<string> { HudVersion.Button(HudVersion.Content) };
                Check($"Content {HudVersion.Content}: it adds {added.Count} panel(s) - its own version button - and every one is fed or listened to{(addedIdle.Count > 0 ? ": not " + string.Join(" ", addedIdle) : "")}{(added.SetEquals(expectedAdded) ? "" : ": unexpected " + string.Join(" ", added.Except(expectedAdded).Concat(expectedAdded.Except(added).Select(x => "-" + x))))}",
                    added.SetEquals(expectedAdded) && addedIdle.Count == 0);
                Check($"Content {HudVersion.Content}: no on-screen keyboard - no key, no panel or rule of one, and no class for it on the player's screen - and the search area is the hint and the clear button",
                    !ids.Any(id => id.StartsWith("os_kb_", StringComparison.Ordinal)) && !ids.Contains(HudBrowser.OldSearchBox) && !Regex.IsMatch(xml + css, @"os-kbd|os-key\b|\.kbd\b")
                    && Regex.IsMatch(xml, @"<Panel id=""os_q_search"" class=""os-search"">\s*<Button id=""os_q_how"">") && ids.Contains(HudBrowser.ClearButton)
                    && !sentClasses.Any(c => c.Class == "kbd"));
                Check($"Content {HudVersion.Content}: the match length's row (SERVER, Game) shows in Arms Race and Deathmatch - each one long round - and only Wingman, a match of rounds, collapses it",
                    Regex.IsMatch(css, @"\.os-global\.mode-wingman \.os-set-time\s*\{\s*visibility:\s*collapse;") && !css.Contains(".os-global.mode-deathmatch .os-set-time", StringComparison.Ordinal)
                    && !css.Contains(".os-global.mode-armsrace .os-set-time", StringComparison.Ordinal) && Regex.IsMatch(xml, @"<Panel class=""os-set os-set-time"">"));
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
            // ---- the cards on their side: every tag in two lines beside the picture, ten on the ballot, eight in the browser ----
            Check($"The cards: {HudMenu.Cards} of them, each with its picture, the 15 tags of MapTags in their order (none but the mode label and the warning on the picture), a score and a line under the name; the browser shows {HudBrowser.PerPage}, the ballot {MapVote.PageCards}",
                HudMenu.Cards == MapVote.PageCards && Enumerable.Range(1, HudMenu.Cards).All(k => ids.Contains(HudMenu.Card(k)) && ids.Contains($"{HudMenu.Card(k)}_score") && ids.Contains($"{HudMenu.Card(k)}_who"))
                && Regex.Matches(noComments, "class=\"os-mtags\"").Count == HudMenu.Cards && Regex.Matches(noComments, "class=\"os-mtag ").Count == HudMenu.Cards * MapTags.All.Length
                && Regex.IsMatch(rules, @"\.os-mtags\s*\{[^}]*flow-children:\s*right-wrap;") && Regex.IsMatch(rules, @"\.os-screen\.voting \.os-mthumb\s*\{\s*width:\s*328px;")
                && Regex.IsMatch(rules, @"\.os-mthumb\s*\{\s*width:\s*196px;") && Regex.IsMatch(rules, @"\.os-screen\.voting \.os-mcard\s*\{\s*width:\s*330px;\s*height:\s*276px;")
                && HudVote.TagClasses.All(c => Regex.IsMatch(rules, $@"\.os-mcard\.{c} \.os-mtag\.{c}\s*\{{\s*visibility:\s*visible;"))
                && Enumerable.Range(1, HudMenu.Cards).All(k => noComments.Contains($"text=\"{{s:{HudVote.HoverVar(k)}}}\"")));
            // ---- the menu's tabs and sub-tabs, the Players page, the version button, the outdated banner, the restart in the foot ----
            Check("Five tabs - Start, Maps, Fun, Server, Help - and the sub-tabs of the tab that is on; the Vote sub-tab only while a vote is on; the Players page's every row and the caption fed",
                Regex.Matches(noComments, "class=\"os-tab\"").Count == 5 && new[] { "os_tab_start", HudView.TabMaps, HudView.TabFun, "os_tab_server", "os_tab_help" }.All(id => noComments.Contains($"<Button id=\"{id}\" class=\"os-tab\">"))
                && new[] { HudVote.BackButton, HudVote.BrowseButton, "os_sub_players", HudView.TabGame, HudDash.Tab, "os_sub_help", HudView.TabHow }.All(id => Regex.IsMatch(noComments, $@"<Button id=""{id}"" class=""os-stab[ ""]"))
                && Regex.IsMatch(rules, @"\.os-stab-vote\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(rules, @"\.os-screen\.voting \.os-stab-vote\s*\{\s*visibility:\s*visible;")
                && Enum.GetValues<HudSlots.Page>().All(pg => Regex.IsMatch(rules, $@"\.os-screen\.page-{pg.ToString().ToLowerInvariant()} "))
                && noComments.Contains($"text=\"{{s:{HudView.SubCaptionVar}}}\"") && noComments.Contains($"text=\"{{s:{HudView.PlayersSummaryVar}}}\"")
                && HudView.PlayersPage([]).All(w => noComments.Contains($"text=\"{{s:{w.Name}}}\"")) && HudView.PlayersPage([]).Count == 1 + 7 * HudView.PlayerRows);
            Check("The HUD check's version button is the window's X: its id names this content, and it stands in for the plain close button while the server does not know the version (vcheck)",
                noComments.Contains($"<Button id=\"{HudVersion.Button(HudVersion.Content)}\" class=\"os-close os-hudver\">") && Regex.IsMatch(rules, @"\.os-screen\.vcheck \.os-hudver\s*\{\s*visibility:\s*visible;")
                && Regex.IsMatch(rules, @"\.os-screen\.vcheck #os_btn_close\s*\{\s*visibility:\s*collapse;") && Regex.Matches(noComments, @"id=""os_hudver_").Count == 1);
            Check("A restart coming shows in the window's foot, its words and its countdown; the freeze says so there too",
                Regex.IsMatch(rules, @"\.os-global\.rst \.os-rstrip\s*\{\s*visibility:\s*visible;") && ids.Contains("os_rst_text") && ids.Contains("os_rst_secs")
                && HudView.RestartCountdown(new HudState { RestartKind = "daily", RestartAt = 1793000030 }, DateTimeOffset.FromUnixTimeSeconds(1793000000).UtcDateTime) == "0:30"
                && HudView.RestartCountdown(new HudState { RestartKind = "daily" }, now) == "After this match" && HudView.RestartCountdown(new HudState(), now) == ""
                && Regex.IsMatch(rules, @"\.os-screen\.vlock \.os-foot-frozen\s*\{\s*visibility:\s*visible;"));
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
            // The Fun page's height (eighteen rows in two columns of nine): the pages have PagesHeight px (the window's 820 less
            // its rule, head, tabs, sub-tabs and foot); the Fun page spends its padding, the note's line (about 1.3 times its type) and margin, and a column's rows
            // with their margins. A row holds its title line (the buttons' height, at least 1.3 times the title's type) over its words (1.3 times
            // their type).
            var funPad = Regex.Match(rules, @"\.os-page-fun\s*\{[^}]*padding:\s*(\d+)px \d+px (\d+)px");
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
            var botsPad = Regex.Match(rules, @"\.os-page-bots\s*\{[^}]*padding:\s*(\d+)px \d+px (\d+)px");
            var presetsHead = Regex.Match(rules, @"\.os-bpresets-head\s*\{[^}]*height:\s*(\d+)px;[^}]*margin-top:\s*(\d+)px;");
            var presetsRow = Regex.Match(rules, @"\.os-bpresets\s*\{[^}]*height:\s*(\d+)px;[^}]*margin-top:\s*(\d+)px;");
            var botButton = Regex.Match(rules, @"\.os-botb\s*\{[^}]*width:\s*(\d+)px;");
            var funButtonMargin = Regex.Match(rules, @"\.os-setb\s*\{[^}]*margin-right:\s*(\d+)px;");
            var botsXml = xml[xml.IndexOf("os-page-bots", StringComparison.Ordinal)..];
            var botsSplit = botsXml.IndexOf("os-fcol-right", StringComparison.Ordinal);
            var botsPerColumn = Math.Max(Regex.Matches(botsXml[..botsSplit], @"class=""os-frow[ ""]").Count, Regex.Matches(botsXml[botsSplit..botsXml.IndexOf("os-bpresets-head", StringComparison.Ordinal)], @"class=""os-frow[ ""]").Count);
            var botsParsed = funParsed && botsPad.Success && presetsHead.Success && presetsRow.Success && botButton.Success && funButtonMargin.Success;
            var botsHeight = botsParsed ? Px(botsPad, 1) + Px(botsPad, 2) + funTopLine + botsPerColumn * (Px(funRowCss, 1) + Px(funRowCss, 2)) + Px(presetsHead, 1) + Px(presetsHead, 2) + Px(presetsRow, 1) + Px(presetsRow, 2) : int.MaxValue;
            var botsButtons = botsParsed ? Bots.ChoicesPerRow * (Px(botButton, 1) + Px(funButtonMargin, 1)) : int.MaxValue; // a column: (1240 - 68) / 2 - 14 = 572 px, the title the rest
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
