// --selftest, part three: the HUD's words and wiring (Hud.cs). Every panel id and class the
// plugin sends must exist in the Workshop layout, and every label in the layout must be fed - a mismatch there shows nothing on screen
// and logs nothing anywhere, so this is the only place it can be caught before players see it.
using System.Text.RegularExpressions;

static partial class Program
{
    const string HudLayoutFile = "../hud/layout/gungame_hud_" + Hud.Release + ".xml", HudStyleFile = "../hud/styles/gungame_hud_" + Hud.Release + ".css",
        HudThumbFile = "../hud/styles/gungame_hud_thumbs_" + Hud.Release + ".css";

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
        // The Help page's height (2026-10-24): a row's words wrap at about 60 characters (14 px Stratum2 in the 446 px left of the 820 px
        // pages beside the live column, the group name and the place); a line is 19 px, a row 4 px more, a group 14 px; the page has
        // 599 px (the window's 820 less its rule, head, tabs, foot and the page's padding). Past it the last group runs off the window.
        static int HelpLines(Hud.Row r) => (int)Math.Ceiling((r.Also == "" ? r.Words : $"{r.Words}  ·  {r.Also}").Length / 60.0);
        var helpHeight = Hud.Help.Sum(g => g.Rows.Sum(r => HelpLines(r) * 19 + 4) + 14);
        Check($"The Help page fits the window: about {helpHeight} px of its 599 ({Hud.Help.Sum(g => g.Rows.Sum(HelpLines))} lines in {Hud.Help.Sum(g => g.Rows.Length)} rows)", helpHeight <= 599);

        // ---- the codec: what the bridge serves is what the plugin reads ----
        var state = new HudState { Maps = 1000, Map = "#12 aim\tred\nline", By = "Jo\"ri; quit", Size = "17 MB", Key = "123", Up = 3, Down = 1, Status = "x",
            MyVotes = new() { [7] = 1, [9] = -1 }, Top = [("#44 aim_aim", 3, 1)], ChangeMap = "#5 x", ChangeBy = "the dice", ChangeIn = 6 };
        var decoded = HudState.Decode(state.Encode());
        Check("The codec carries every field, and a tab or newline in a name cannot break a line; no queue, holder or skip line any more (the owner's 46, 84 a)",
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
                "!time 10", "!time 60", "!restart", "!previous", "!map ar_baggage", "!map 12 deathmatch" }.All(Hud.IsAction));
        Check("...and nothing free-form: no name search, no second command, no other !time, no !rcon, no unknown verb - and none of the queue's or the duplicates' old words (the owner's 46, 84 a)",
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
        b.Click("gg_sort_rank", 4);
        Check("...the same sort again turns it round", Order(b) == "ar_shoots,3,2,1" && b.Descending);
        b.Click("gg_sort_votes", 4);
        Check("Browser: by votes - most liked first, the disliked last - and a new sort starts in its natural direction", Order(b) == "1,3,ar_shoots,2" && !b.Descending);
        b.Click("gg_sort_name", 4);
        Check("Browser: by name, case ignored", Order(b) == "1,ar_shoots,3,2");
        b.Click("gg_sort_size", 4);
        Check("Browser: by size, smallest first, official maps (no download) last", Order(b) == "3,1,2,ar_shoots");
        b = new HudBrowser();
        b.Click("gg_fl_tiny", 4);
        Check("Browser: the size chips (<25 MB here) leave the official maps out", Order(b) == "3"
            && HudBrowser.BandOf(24) == HudBrowser.Band.Tiny && HudBrowser.BandOf(25) == HudBrowser.Band.Small && HudBrowser.BandOf(120) == HudBrowser.Band.Mid && HudBrowser.BandOf(400) == HudBrowser.Band.Large);
        b.Click("gg_fl_tiny", 4);
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
        var kb = new HudBrowser();
        kb.Click("gg_pg_next", all.Count);
        kb.Click(HudKeyboard.Open, all.Count);
        Check("The keyboard open: pages of the four cards it leaves showing, the first card shown staying first (page 2 of 3 -> 3 of 5)",
            kb.PageSize == 4 && kb.Page == 2 && kb.PageOf(all).Select(m => m.Arg).SequenceEqual(["9", "10", "11", "12"]) && kb.PagesFor(all.Count) == 5);
        kb.Click("gg_pg_last", all.Count);
        kb.Click(HudKeyboard.Done, all.Count);
        Check("...and closed again: pages of eight, on the page that holds the card that was first (17 -> page 3 of 3)",
            kb.PageSize == 8 && kb.Page == 2 && kb.PageOf(all).First().Arg == "17");
        Check("Any sort or filter starts again on the first page", pb.Click("gg_sort_name", 20) && pb.Page == 0);

        // ---- the menu's pages and its clicks ----
        var now = new DateTime(2026, 10, 7, 20, 0, 0, DateTimeKind.Utc);
        var live = new HudState { Key = "3070210812", RoundMinutes = 10, PrevArg = "2", PrevLabel = "#2 Zebra" };
        var mb = new HudBrowser();
        var (mt, mc) = HudMenu.Maps(back, live, mb, 1001, now);
        string MT(string id) => mt.Single(x => x.Id == id).Text;
        bool MC(string id, string cls) => mc.Single(x => x.Id == id && x.Class == cls).On;
        Check("Cards: rank, name, size, likes, the broken warning and what Play does (the owner's 46: it puts the map to the vote) - on now, broken", MT("gg_m1_rank") == "#1" && MT("gg_m1_name") == "AIM Map One"
            && MT("gg_m1_size") == "112 MB" && MT("gg_m1_votes") == "+5  -1" && MT("gg_m1_play_text") == "Put to the vote" && !MC("gg_m1", "queued") && MC("gg_m1", "liked")
            && MT("gg_m2_warn") == "Broken: crashed 3/3 test loads" && MC("gg_m2", "risky") && MC("gg_m2", "disliked") && MT("gg_m2_play_text") == "Vote anyway?"
            && MC("gg_m3", "now") && MT("gg_m3_play_text") == "On now" && MT("gg_m4_rank") == "Official" && MT("gg_m4_size") == "" && MT("gg_m4_warn") == "Crashed the server 2x"
            && !MC("gg_m5", "used") && MT("gg_m5_name") == "" && MT("gg_m_count") == "4 maps" && MT("gg_pg_text") == "Page 1 of 1" && !MC("gg_m_empty", "used"));
        var inVote = VoteState.Decode("mv\t1793700100\topen\t40\t2\t1\tmid\t1001\t1\nmvo\t1\tarmsrace\tkeep\t\t3070210812\tKeep playing this map\t#3 beta\t0\t\nmvo\t2\tarmsrace\tpicked\t1\t3084291314\t#1 AIM Map One\t112 MB\t1\tAlice\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t0\t\nmvv\t1001\t2\n");
        var waitVote = new VoteState { WaitFor = 45 };
        string PT(long account, VoteState? v, int k) => HudMenu.Maps(back, live, new HudBrowser(), account, now, vote: v).Texts.Single(x => x.Id == $"gg_m{k}_play_text").Text;
        Check("...while a vote is open a card on the ballot says Your vote or On the vote (no numbers since the owner's 89 a), the map that is on is Keep's card, any other Add to the vote; after a vote, the wait",
            PT(1001, inVote, 1) == "Your vote" && PT(1002, inVote, 1) == "On the vote" && PT(1002, inVote, 3) == "On the vote" && PT(1002, inVote, 4) == "Add anyway?" && PT(1002, inVote, 5) == "" 
            && HudMenu.Maps(back, live, new HudBrowser(), 1002, now, vote: inVote).Classes.Single(x => x.Id == "gg_m1" && x.Class == "queued").On
            && PT(1002, waitVote, 1) == "Vote in 45 s" && PT(1002, waitVote, 3) == "On now");
        Check("Card buttons say what the chat would: likes and Play by the map's rank or official name; the map that is on is Keep's vote while one is open, nothing outside one; Too big is a Dislike's reason",
            HudMenu.Command("gg_m1_up", back, live, mb, 1001, now) == "!up 1" && HudMenu.Command("gg_m4_down", back, live, mb, 1001, now) == "!down ar_shoots"
            && HudMenu.Command("gg_m1_play", back, live, mb, 1001, now) == "!map 1" && HudMenu.Command("gg_m1_play", back, live, mb, 1002, now) == "!map 1"
            && HudMenu.Command("gg_m1_big", back, live, mb, 1001, now) == "!down 1 big" && Hud.IsAction("!down 1 big")
            && HudMenu.Command("gg_m3_play", back, live, mb, 1001, now) == null && HudMenu.Command("gg_m3_play", back, live, mb, 1001, now, vote: inVote) == "!map 3"
            && HudMenu.Command("gg_m5_play", back, live, mb, 1001, now) == null && HudMenu.Command("gg_m9_play", back, live, mb, 1001, now) == null);
        var first = HudMenu.Command("gg_m2_play", back, live, mb, 1001, now);
        var armed = HudMenu.Maps(back, live, mb, 1001, now.AddSeconds(5));
        var second = HudMenu.Command("gg_m2_play", back, live, mb, 1001, now.AddSeconds(10));
        Check("A broken map: the first Play asks (and arms the bridge's own confirmation, this player's alone), the second within 30 s puts it to the vote",
            first == "!map 2" && armed.Classes.Single(x => x.Id == "gg_m2" && x.Class == "confirm").On && armed.Texts.Single(x => x.Id == "gg_m2_play_text").Text == "Click again"
            && second == "!map 2" && mb.ConfirmKey == "");
        HudMenu.Command("gg_m2_play", back, live, mb, 1001, now);
        Check("...and after 30 s the next click asks again", HudMenu.Command("gg_m2_play", back, live, mb, 1001, now.AddSeconds(31)) == "!map 2" && mb.ConfirmKey == "3070208661");
        var (qt, qc) = HudMenu.PreviousButton(live);
        Check("Previous: the map before this one on its button, which puts it to the vote; off while there is none",
            qt.Single(x => x.Id == "gg_q_prev_text").Text == "Previous: #2 Zebra" && !qc.Single(x => x.Id == HudMenu.Previous && x.Class == "off").On
            && HudMenu.Command(HudMenu.Previous, back, live, mb, 1001, now) == "!previous" && HudMenu.PreviousButton(new HudState()).Classes.Single(x => x.Id == HudMenu.Previous).On
            && HudMenu.Command(HudMenu.Previous, back, new HudState(), mb, 1001, now) == null);
        var (gt, gc) = HudMenu.Game(live, 0);
        Check("Game page: the match length that is set is lit, and the buttons say !time and !restart",
            gc.Single(x => x.Id == HudMenu.Time10).On && !gc.Single(x => x.Id == HudMenu.Time60).On && HudMenu.Command(HudMenu.Time60, back, live, mb, 1, now) == "!time 60"
            && HudMenu.Command(HudMenu.Restart, back, live, mb, 1, now) == "!restart" && gt.Single(x => x.Id == "gg_set_restart_text").Text == "Restart the server"
            && !gc.Single(x => x.Id == HudMenu.Restart && x.Class == "armed").On);
        var armedGame = Enumerable.Range(1, 30).Select(n => HudMenu.Game(live, n)).ToList();
        Check("Restart, after its first click (the owner's 14a): \"Press again within N s\", counting down every second from 30, armed (content v3_2_0 blinks it) - and back to normal at 0",
            armedGame.Select((g, i) => g.Texts.Single(x => x.Id == "gg_set_restart_text").Text == $"Press again within {i + 1} s" && g.Classes.Single(x => x.Id == HudMenu.Restart && x.Class == "armed").On).All(x => x)
            && HudBrowser.ConfirmSeconds == 30 && PickConfirmSeconds == 30);
        var menuCommands = new[] { "gg_m1_up", "gg_m1_down", "gg_m1_play", "gg_m1_big", "gg_m1_ads", "gg_m4_up", HudMenu.Previous, HudMenu.Time10, HudMenu.Time60, HudMenu.Restart }
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
            var card = new WorkshopCard();
            card.Show(3232901690);
            var stock = Fun.Settings.SelectMany(s => s.Stock).GroupBy(x => x.ConVar).ToDictionary(g => g.Key, g => g.First().Value);
            var funPage = HudMenu.FunPage(cv => stock.GetValueOrDefault(cv));
            var sentTexts = HudView.StaticTexts().Concat(HudView.Texts(full)).Concat(HudView.PlayerTexts(full, 1, "x", true)).Concat(menuMaps.Texts).Concat(menuQueue.Texts)
                .Concat(menuGame.Texts).Concat(card.Texts()).Concat(funPage.Texts).Select(t => t.Id).Append("gg_k_secs").ToList();
            var sentClasses = HudView.StaticClasses().Concat(HudView.Classes(full)).Concat(HudView.PlayerClasses(full, 1)).Concat(menuMaps.Classes).Concat(menuQueue.Classes)
                .Concat(menuGame.Classes).Concat(card.Classes()).Concat(funPage.Classes).Concat(HudVote.FixedRowClasses(cv => stock.GetValueOrDefault(cv), true)).ToList();
            string[] screenClasses = ["win", "win-in", "draining", "toast-1", "toast-2", "toast-in", "c31", "c32", .. Enum.GetValues<HudSlots.Page>().Select(p => "page-" + p.ToString().ToLowerInvariant())];
            var buttons = HudView.Tabs.Select(t => t.Id).Concat([HudView.ButtonClose, HudView.ButtonUp, HudView.ButtonDown, HudView.ButtonAct]).Concat(HudView.NewButtons).Concat(HudView.NewButtons32).Concat(HudView.NewButtons41)
                .Concat(HudView.NewButtons42).Concat(HudView.NewButtons43).Concat(HudVote.NewButtons44).Concat(HudVote.NewButtons45)
                .Concat(HudBrowser.SortButtons.Select(x => x.Id)).Concat(HudBrowser.BandButtons.Select(x => x.Id))
                .Concat([HudBrowser.LikedButton, HudBrowser.OfficialButton, HudBrowser.ClearButton, HudBrowser.FirstButton, HudBrowser.PrevButton, HudBrowser.NextButton, HudBrowser.LastButton])
                .Concat(Enumerable.Range(1, HudMenu.Cards).SelectMany(k => new[] { $"gg_m{k}_play", $"gg_m{k}_up", $"gg_m{k}_down" }))
                .Concat([HudMenu.Previous, HudMenu.NadesOn, HudMenu.NadesOff, HudMenu.Time10, HudMenu.Time60, HudMenu.Restart, HudMenu.BoardOpen])
                .Concat([HudView.BoardUp, HudView.BoardDown, HudView.BoardNext, HudKeyboard.Open, WorkshopCard.Play]).Concat(HudKeyboard.Keys()).ToList();
            var thumbPanels = HudMenu.Thumbs(back, new HudBrowser(), 1001).Select(x => x.Panel).ToList();
            Check($"Layout: the eight cards' pictures exist ({thumbPanels.Count})", thumbPanels.Count == HudMenu.Cards && thumbPanels.All(ids.Contains));
            Check($"Layout: no id twice ({ids.Count} ids), and the root panel has none (the resource compiler refuses it)",
                ids.Count == ids.Distinct().Count() && !Regex.IsMatch(xml, @"<root>\s*<styles>.*?</styles>\s*<Panel\b[^>]*\bid=", RegexOptions.Singleline));
            var unknownText = sentTexts.Where(id => !fed.Contains(id)).Distinct().ToList();
            Check($"Layout: every text the plugin sends lands on a {{s:text}} label ({sentTexts.Distinct().Count()}){(unknownText.Count > 0 ? ": missing " + string.Join(", ", unknownText) : "")}", unknownText.Count == 0);
            var unfed = fed.Where(id => !sentTexts.Contains(id)).ToList();
            Check($"Layout: every {{s:text}} label is fed by the plugin ({fed.Count}){(unfed.Count > 0 ? ": never set " + string.Join(", ", unfed) : "")}", unfed.Count == 0);
            var unknownPanels = sentClasses.Select(c => c.Id).Concat([HudView.Global, HudView.Screen]).Concat(buttons).Where(id => !ids.Contains(id)).Distinct().ToList();
            Check($"Layout: every panel the plugin sets a class on, and every button it listens to, exists{(unknownPanels.Count > 0 ? ": missing " + string.Join(", ", unknownPanels) : "")}", unknownPanels.Count == 0);
            Check("Layout: the buttons are Buttons", buttons.All(b => Regex.IsMatch(xml, $@"<Button\b[^>]*\bid=""{b}""")));
            var classes = sentClasses.Select(c => c.Class).Concat(screenClasses).Distinct().ToList();
            var unstyled = classes.Where(c => !Regex.IsMatch(css, $@"\.{Regex.Escape(c)}\b")).ToList();
            Check($"Stylesheet: every class the plugin sets has a rule ({classes.Count}){(unstyled.Count > 0 ? ": none for " + string.Join(", ", unstyled) : "")}", unstyled.Count == 0);
            Check("Layout: only Panel, Label, Image and Button (anything else makes the host refuse the whole layout)",
                Regex.Matches(xml, @"<([A-Za-z]+)\b").Select(x => x.Groups[1].Value).All(t => t is "root" or "styles" or "include" or "Panel" or "Label" or "Image" or "Button"));
            Check("Layout: no inline style attribute and no scaling attribute (each silently kills the layout in CS2)", !Regex.IsMatch(xml, @"\s(style|scaling)="));
            // Valve's whitelist (point_script.d.ts, CustomHudLayout): Panel id/class/hittest, Label id/class/hittest/text, Image
            // id/class/hittest/src, Button id/class. The client refuses the WHOLE layout for one attribute outside it ("Layout contains
            // disallowed attribute %s for panel type '%s'") - v2_1_0 had hittest on a Button and rendered nothing at all.
            var allowed = new Dictionary<string, string[]> { ["Panel"] = ["id", "class", "hittest"], ["Label"] = ["id", "class", "hittest", "text"],
                ["Image"] = ["id", "class", "hittest", "src"], ["Button"] = ["id", "class"] };
            var outside = Regex.Matches(Regex.Replace(xml, "<!--.*?-->", "", RegexOptions.Singleline), @"<(Panel|Label|Image|Button)\b([^>]*)>")
                .SelectMany(m => Regex.Matches(m.Groups[2].Value, @"([\w-]+)\s*=").Select(a => (Tag: m.Groups[1].Value, Attr: a.Groups[1].Value)))
                .Where(x => !allowed[x.Tag].Contains(x.Attr)).Select(x => $"{x.Attr} on {x.Tag}").Distinct().ToList();
            Check($"Layout: every attribute is on Valve's list for its panel type{(outside.Count > 0 ? ": not " + string.Join(", ", outside) : "")}", outside.Count == 0);
            var rules = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline); // comments may say anything
            Check("Stylesheet: every @keyframes name is quoted, and no comma selector (each silently kills the layout)",
                !Regex.IsMatch(rules, @"@keyframes\s+[^'\s]") && !Regex.IsMatch(rules, @"[^{};]*,[^{};]*\{"));
            Check("Layout: it includes the compiled stylesheet by its s2r:// path", xml.Contains($"s2r://panorama/styles/custom_game/gungame_hud_{Hud.Release}.vcss_c"));
            Check($"Stylesheet: the drain bar empties in exactly the idle close ({HudSlots.IdleCloseSeconds} s)",
                Regex.IsMatch(rules, $@"\.gg-drain-fill\s*\{{[^}}]*transition-duration:\s*{HudSlots.IdleCloseSeconds}s;") && Regex.IsMatch(rules, @"\.draining \.gg-drain-fill\s*\{[^}]*scaleX\( 0 \);[^}]*transition-duration:\s*0s;"));

            // ---- three contents on one path (v3_0_0, v3_1_0, v3_2_0): a game holding an older one must never be sent a panel it lacks ----
            const string shippedFile = "../hud/reference/gungame_hud_v3_0_0.xml", shipped31File = "../hud/reference/gungame_hud_v3_1_0.xml", shipped40File = "../hud/reference/gungame_hud_v4_0_0.xml", shipped41File = "../hud/reference/gungame_hud_v4_1_0.xml",
                shipped42File = "../hud/reference/gungame_hud_v4_2_0.xml", shipped43File = "../hud/reference/gungame_hud_v4_3_0.xml", shipped44File = "../hud/reference/gungame_hud_v4_4_0.xml"; // as they shipped
            if (File.Exists(shippedFile) && File.Exists(shipped31File) && File.Exists(shipped40File) && File.Exists(shipped41File) && File.Exists(shipped42File) && File.Exists(shipped43File) && File.Exists(shipped44File))
            {
                static HashSet<string> IdsOf(string file) => Regex.Matches(File.ReadAllText(file), @"\bid=""([^""]+)""").Select(x => x.Groups[1].Value).ToHashSet();
                var shipped = IdsOf(shippedFile);
                var shipped31 = IdsOf(shipped31File);
                var shipped40 = IdsOf(shipped40File);
                var shipped41 = IdsOf(shipped41File);
                var shipped42 = IdsOf(shipped42File);
                var shipped43 = IdsOf(shipped43File);
                var shipped44 = IdsOf(shipped44File);
                var added31 = shipped31.Where(id => !shipped.Contains(id)).ToHashSet();
                var added32 = shipped40.Where(id => !shipped31.Contains(id)).ToHashSet();
                var added41 = shipped41.Where(id => !shipped40.Contains(id)).ToHashSet();
                var added42 = shipped42.Where(id => !shipped41.Contains(id)).ToHashSet();
                var added43 = shipped43.Where(id => !shipped42.Contains(id)).ToHashSet();
                var added44 = shipped44.Where(id => !shipped43.Contains(id)).ToHashSet();
                var added45 = ids.Where(id => !shipped44.Contains(id)).ToHashSet();
                var sent = sentTexts.Concat(sentClasses.Select(c => c.Id)).Concat([HudView.Global, HudView.Screen]).Distinct().ToList();
                var toEverybody = sent.Where(id => HudView.LevelOf(id) == 0).ToList();
                var notShipped = toEverybody.Where(id => !shipped.Contains(id)).ToList();
                Check($"Content: everything the plugin sends a game it has not seen click a new button is in the shipped {Hud.Release} layout ({toEverybody.Count} ids){(notShipped.Count > 0 ? ": not there " + string.Join(", ", notShipped) : "")}",
                    notShipped.Count == 0);
                var to31 = sent.Where(id => HudView.LevelOf(id) <= 31).ToList();
                var not31 = to31.Where(id => !shipped31.Contains(id)).ToList();
                Check($"Content: everything it sends a game that has shown v3_1_0 but not v3_2_0 is in the shipped v3_1_0 layout ({to31.Count} ids){(not31.Count > 0 ? ": not there " + string.Join(", ", not31) : "")}",
                    not31.Count == 0);
                Check($"Content v3_1_0 (as shipped): it kept every panel {Hud.Release} had ({shipped.Count}) and only added ({added31.Count} new: {HudView.NewIds.Count} fed only to games that have them, {HudView.NewButtons.Length} buttons that prove it)",
                    shipped.All(shipped31.Contains) && HudView.NewIds.All(added31.Contains) && HudView.NewButtons.All(added31.Contains) && added31.All(id => HudView.NewIds.Contains(id) || HudView.NewButtons.Contains(id)));
                Check($"Content v4_0_0 (as shipped): it kept every panel v3_1_0 had ({shipped31.Count}) and only added ({added32.Count} new: {HudView.NewIds32.Count} fed only to games that have them, {HudView.NewButtons32.Length} buttons that prove it)",
                    shipped31.All(shipped40.Contains) && HudView.NewIds32.All(added32.Contains) && HudView.NewButtons32.All(added32.Contains) && added32.All(id => HudView.NewIds32.Contains(id) || HudView.NewButtons32.Contains(id)));
                Check($"Content v4_1_0 (as shipped): it kept every panel v4_0_0 had ({shipped40.Count}), the grenade drops' buttons among them, and added only the popup's {added41.Count} buttons ({HudView.NewButtons41.Length} that prove it), to which nothing is sent",
                    shipped40.Where(id => id != "gg_f_site").All(shipped41.Contains) && !shipped41.Contains("gg_f_site") && added41.SetEquals(HudView.NewButtons41) && !sent.Any(added41.Contains) && HudView.NewButtons41.All(b => HudView.LevelOfButton(b) == 41 && HudView.LevelOf(b) == 0));
                Check($"Content v4_2_0 (as shipped): it kept every panel v4_1_0 had ({shipped41.Count}) and added only the {added42.Count} buttons of the picture on request and the mode filter ({HudView.NewButtons42.Length} that prove it), to which nothing is sent",
                    shipped41.All(shipped42.Contains) && added42.SetEquals(HudView.NewButtons42) && !sent.Any(added42.Contains) && HudView.NewButtons42.All(b => HudView.LevelOfButton(b) == 42 && HudView.LevelOf(b) == 0));
                Check($"Content v4_3_0 (as shipped): it kept every panel v4_2_0 had ({shipped42.Count}) and added only the {added43.Count} buttons of the Sync page - its tab and its pagers ({HudView.NewButtons43.Length} that prove it), to which nothing is sent",
                    shipped42.All(shipped43.Contains) && added43.SetEquals(HudView.NewButtons43) && !sent.Any(added43.Contains) && HudView.NewButtons43.All(b => HudView.LevelOfButton(b) == 43 && HudView.LevelOf(b) == 0));
                Check($"Content v4_4_0 (as shipped): it kept every panel v4_3_0 had ({shipped43.Count}) and added only {added44.Count} buttons - the vote's Random, map browser and back, the inline reasons, the Fun page's Random and Default ({HudVote.NewButtons44.Length} that prove it), to which nothing is sent",
                    shipped43.All(shipped44.Contains) && added44.SetEquals(HudVote.NewButtons44) && !sent.Any(added44.Contains) && HudVote.NewButtons44.All(b => HudView.LevelOfButton(b) == 44 && HudView.LevelOf(b) == 0)
                    && HudVote.NewButtons44.Distinct().Count() == HudVote.NewButtons44.Length);
                Check($"Content {Hud.Content}: it keeps every panel v4_4_0 had ({shipped44.Count}) and adds {added45.Count}: {HudVote.NewButtons45.Length} buttons (each card's Remove mine, Catch-up on and off) that prove it and get nothing, and the summary strip's {HudVote.NewIds45.Length} segments, whose classes go only to a game that has shown it",
                    shipped44.All(ids.Contains) && added45.SetEquals(HudVote.NewButtons45.Concat(HudVote.NewIds45)) && !sent.Any(added45.Contains)
                    && HudVote.NewButtons45.All(b => HudView.LevelOfButton(b) == 45 && HudView.LevelOf(b) == 0) && HudVote.NewIds45.All(id => HudView.LevelOf(id) == 45 && HudView.LevelOfButton(id) == 0)
                    && HudVote.NewButtons45.Concat(HudVote.NewIds45).Distinct().Count() == added45.Count);
                var not40 = sent.Where(id => !shipped40.Contains(id)).ToList();
                Check($"Content: everything the plugin sends, to any game, is in the shipped v4_0_0 layout ({sent.Count} ids): a game holding it goes on as it was{(not40.Count > 0 ? ": not there " + string.Join(", ", not40) : "")}", not40.Count == 0);
                Check("Content v3_1_0: its new panels are sent only what HudView.NewIds names, and its buttons get nothing from the server (fixed labels, state in classes on older panels)",
                    sent.Where(added31.Contains).All(HudView.NewIds.Contains) && !sent.Any(HudView.NewButtons.Contains));
                Check("Content v4_0_0: its new panels are sent only what HudView.NewIds32 names (the Fun page's choices are buttons that do get classes - gated the same way)",
                    sent.Where(added32.Contains).All(HudView.NewIds32.Contains) && sent.Where(HudView.NewButtons32.Contains).All(HudView.NewIds32.Contains));
                Check("Content v3_1_0: every new label stays hidden until its words come (c31), and the new pieces' own classes have rules",
                    Regex.IsMatch(rules, @"\.gg-screen\.c31 \.gg-bigvote \.gg-vote-num") && Regex.IsMatch(rules, @"\.gg-screen\.c31 \.gg-set-new-now") && Regex.IsMatch(rules, @"\.gg-screen\.c31 \.gg-how")
                    && new[] { "gg-bigvote", "gg-mbig", "gg-mbig-text", "gg-mtag-big", "gg-set-new-now", "gg-page-how", "gg-how", "gg-hline", "gg-hline-txt" }.All(c => Regex.IsMatch(rules, $@"\.{c}\b")));
                Check("Content v4_0_0: every new label stays hidden until its words come (c32) - the Ads count, the Fun page's note and rows, the two Help rows - and the new pieces' own classes have rules",
                    Regex.IsMatch(rules, @"\.gg-screen\.c32 \.gg-adsvote \.gg-vote-num") && Regex.IsMatch(rules, @"\.gg-screen\.c32 \.gg-fun-note") && Regex.IsMatch(rules, @"\.gg-screen\.c32 \.gg-frow\.used")
                    && Regex.IsMatch(rules, @"\.gg-screen\.c32 \.gg-crow-c32\.used") && Regex.IsMatch(rules, @"\.gg-crow\.gg-crow-c32\s*\{[^}]*collapse")
                    && new[] { "gg-adsvote", "gg-mads", "gg-mads-text", "gg-mtag-ads", "gg-bigrow", "gg-page-fun", "gg-frow", "gg-frow-title", "gg-frow-txt", "gg-funb" }.All(c => Regex.IsMatch(rules, $@"\.{c}\b")));
                Check("Content v4_0_0: the restart button blinks fast while armed (keyframes on opacity, the one kind a custom HUD runs), on the class every content has",
                    Regex.IsMatch(rules, @"@keyframes 'gg-blink'") && Regex.IsMatch(rules, @"\.gg-restart\.armed\s*\{[^}]*animation-name:\s*gg-blink;[^}]*animation-iteration-count:\s*infinite;"));
            }
            else Console.WriteLine($"skip the content checks: {shippedFile}, {shipped31File}, {shipped40File}, {shipped41File}, {shipped42File}, {shipped43File} or {shipped44File} not found");

            // ---- content v4_1_0: the popup that asks why a player disliked a map, and the grenade drops on the Fun page ----
            var noComments = Regex.Replace(xml, "<!--.*?-->", "", RegexOptions.Singleline);
            Check("The published layout and stylesheet depend on no site: no panel, class or word of one (the owner's 10+11; the window's footer has no line for it)",
                !Regex.IsMatch(xml + css, @"\bsite\b|_site\b|-site\b|example\.com|https?://", RegexOptions.IgnoreCase));
            var whyFrom = noComments.IndexOf("class=\"gg-why\"", StringComparison.Ordinal);
            Check($"The popup: a button for each of the {DownReasons.All.Length} reasons, with its words from the variables the plugin sets on gg_global, the map's from gg_screen, and no other id in it",
                whyFrom > 0 && Enumerable.Range(1, DownReasons.All.Length).All(k => Regex.IsMatch(noComments, $@"<Button id=""{HudVote.WhyButton(k)}"" class=""gg-whyb""><Label class=""gg-whyb-text"" text=""\{{s:{HudVote.WhyVar}{k}\}}"" /><Label class=""gg-whyb-word"" text=""\{{s:{HudVote.WhyVar}w{k}\}}"" /></Button>"))
                && noComments.Contains($"text=\"{{s:{HudVote.WhyVar}}}\"") && HudVote.WhyWords().Count == 3 * DownReasons.All.Length && HudView.NewButtons41.Length == DownReasons.All.Length
                && HudVote.WhyWords().All(w => w.Text.Length > 0 && w.Text.Length <= 40) && !Regex.IsMatch(noComments[whyFrom..noComments.IndexOf("class=\"gg-news\"", StringComparison.Ordinal)], @"\bid=""(?!gg_why_)"));
            Check($"Content v4_4_0's reasons in place: in each of the {HudVote.WhyPlaces.Length} places (the live column, the TAB board, each card) a button per reason with its short words from gg_global, and a click on one is its reason",
                HudVote.WhyPlaces.All(place => Enumerable.Range(1, DownReasons.All.Length).All(k => noComments.Contains($"<Button id=\"{HudVote.WhyButton(place, k)}\" class=\"gg-whyib\"><Label class=\"gg-whyib-text\" text=\"{{s:{HudVote.WhyVar}s{k}}}\" /></Button>")
                    && HudVote.WhyClick(HudVote.WhyButton(place, k)) == DownReasons.All[k - 1]))
                && DownReasons.All.All(r => HudVote.ShortLabel(r).Length is > 0 and <= 12) && HudVote.WhyClick("gg_whyz_1") == null && HudVote.WhyClick("gg_whyw_7") == null
                && Enumerable.Range(1, HudMenu.Cards).All(k => Regex.IsMatch(rules, $@"\.gg-screen\.why\.whyc{k} \.gg-whycard-{k}\s*\{{\s*visibility:\s*visible;"))
                && Regex.IsMatch(rules, @"\.gg-screen\.why\.whyin \.gg-live \.gg-vote-buttons\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(rules, @"\.gg-screen\.why\.whyin \.gg-why\s*\{\s*visibility:\s*collapse;")
                && Regex.IsMatch(rules, @"\.HUD_SCOREBOARD_VISIBLE \.gg-screen\.why \.gg-whyboard\s*\{\s*visibility:\s*visible;") && Regex.IsMatch(rules, @"\.HUD_SCOREBOARD_VISIBLE \.gg-screen\.why \.gg-why\s*\{\s*visibility:\s*collapse;")
                && HudVote.DownCard("gg_m3_down") == 3 && HudVote.DownCard("gg_m9_down") == 0 && HudVote.DownCard("gg_m3_up") == 0 && HudVote.DownCard(HudView.ButtonDown) == 0);
            Check("Content v4_4_0: the board's Too big and Ads buttons step aside (chat's !down big and the cards' tags stay); Our maps pulses while it is not chosen (v4_5_0: opulse, which the plugin flips, moves its box-shadow under a transition - keyframes and opacity left the shadow as it was), and is simply lit once it is; the cards wear their mode",
                Regex.IsMatch(rules, @"\.gg-board \.gg-bigrow\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(rules, @"\.gg-mcard \.gg-mbig\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(rules, @"\.gg-mcard \.gg-mads\s*\{\s*visibility:\s*collapse;")
                && Regex.IsMatch(rules, @"\.gg-ours-glow\s*\{[^}]*box-shadow:\s*#E9B44C2E[^}]*transition-property:\s*box-shadow") && Regex.IsMatch(rules, @"\.gg-screen\.opulse \.gg-ours-glow\s*\{[^}]*box-shadow:\s*#E9B44CEB")
                && !Regex.IsMatch(rules, @"(?m)^\.gg-ours-glow\s*\{[^}]*opacity:") // the game draws a box-shadow at full strength at any opacity but 0 (the real client, 2026-11-04)
                && Enumerable.Range(1, 10).All(k => Regex.IsMatch(rules, $@"\.gg-vseg\.w{k}\s*\{{\s*width:\s*fill-parent-flow\(\s*{k}\.0\s*\);")) // the strip: weights, so it fills exactly
                && Regex.IsMatch(rules, @"\.gg-screen\.voting \.gg-mcard\.vlead\s*\{[^}]*border:\s*2px solid gg-gold;") && !Regex.IsMatch(rules, @"\.gg-mcard\.vlead\s*\{[^}]*box-shadow")
                && Regex.IsMatch(rules, @"\.gg-screen\.fl-ours \.gg-ours-glow\s*\{\s*opacity:\s*0;") && rules.IndexOf(".gg-screen.opulse .gg-ours-glow", StringComparison.Ordinal) < rules.IndexOf(".gg-screen.fl-ours .gg-ours-glow", StringComparison.Ordinal)
                && Modes.All.All(m => Regex.IsMatch(rules, $@"\.gg-mcard\.mode-{m.Id} \.gg-mmode-{m.Id}\s*\{{\s*visibility:\s*visible;") && noComments.Contains($"class=\"gg-mmode gg-mmode-{m.Id}\""))
                && Regex.Matches(noComments, "class=\"gg-mband\"").Count == HudMenu.Cards && noComments.Contains($"text=\"{{s:{HudVote.RandomVar}}}\"") && noComments.Contains($"text=\"{{s:{HudVote.MoreVar}}}\"") && noComments.Contains($"text=\"{{s:{HudVote.BrowseVar}}}\""));
            Check("The popup draws above the window, and a click on it is its reason, nothing else",
                noComments.IndexOf("class=\"gg-win-foot\"", StringComparison.Ordinal) < whyFrom && whyFrom < noComments.IndexOf("class=\"gg-news\"", StringComparison.Ordinal)
                && DownReasons.All.Select((r, i) => HudVote.WhyClick(HudVote.WhyButton(i + 1)) == r).All(x => x) && HudVote.WhyClick(HudVote.WhyButton(DownReasons.All.Length + 1)) == null
                && HudVote.WhyClick(HudView.ButtonDown) == null && HudVote.WhyClick("gg_why_x") == null);
            var whyBox = Regex.Match(rules, @"\.gg-why\s*\{[^}]*?visibility:\s*collapse;[^}]*?width:\s*(\d+)px;[^}]*?margin-left:\s*(\d+)px;[^}]*?padding:\s*\d+px (\d+)px \d+px (\d+)px;[^}]*?border-left:\s*(\d+)px");
            var whyWide = whyBox.Success ? Px(whyBox, 1) + Px(whyBox, 2) + Px(whyBox, 3) + Px(whyBox, 4) + Px(whyBox, 5) : int.MaxValue;
            Check($"The popup stays hidden until its class comes, and stands clear of the window at 16:9: {whyWide} px of the {(1920 - 1240) / 2} beside it",
                whyWide <= (1920 - 1240) / 2 && Regex.IsMatch(rules, @"\.gg-screen\.why \.gg-why\s*\{\s*visibility:\s*visible;"));
            Check($"The popup's line drains over its {DownReasons.PopupSeconds} s, from the moment the plugin starts it, a second in",
                Regex.IsMatch(rules, $@"\.gg-why-fill\s*\{{[^}}]*transition-duration:\s*{DownReasons.PopupSeconds - 1}s;") && Regex.IsMatch(rules, @"\.gg-screen\.why-run \.gg-why-fill\s*\{[^}]*scaleX\( 0 \);[^}]*transition-duration:\s*0s;"));
            var fixedWords = Regex.Matches(noComments, @"\btext=""([^""]*)""").Select(m => m.Groups[1].Value).ToList();
            var fixedCommands = fixedWords.SelectMany(w => Regex.Matches(w, @"![a-z]+").Select(m => m.Value)).ToList();
            Check($"The layout's own words name only primaries ({string.Join(" ", fixedCommands)})", fixedCommands.Count > 0 && fixedCommands.All(w => ChatCommands.Find(w) != null));
            var gamePage = noComments[noComments.IndexOf("gg-page-game", StringComparison.Ordinal)..noComments.IndexOf("gg-page-help", StringComparison.Ordinal)];
            var funOnwards = noComments[noComments.IndexOf("gg-page-fun", StringComparison.Ordinal)..];
            var bigRow = Array.FindIndex(Fun.Settings, s => s.Id == "nades") + 1;
            Check("The grenade drops moved from the Game page to the Fun page, above Big grenades, under the same button ids, and is Arms Race's alone",
                !gamePage.Contains(HudMenu.NadesOn) && funOnwards.Contains(HudMenu.NadesOn) && funOnwards.IndexOf(HudMenu.NadesOff, StringComparison.Ordinal) < funOnwards.IndexOf($"id=\"{HudMenu.FunRow(bigRow)}\"", StringComparison.Ordinal)
                && Regex.IsMatch(funOnwards, @"class=""gg-frow gg-frow-nades gg-set-nades""") && Regex.IsMatch(rules, @"\.gg-frow-nades\s*\{\s*visibility:\s*visible;")
                && Regex.IsMatch(rules, @"\.gg-global\.mode-deathmatch \.gg-set-nades") && Regex.IsMatch(rules, @"\.gg-global\.mode-wingman \.gg-set-nades")
                && Regex.IsMatch(rules, $@"\.gg-global\.nd-on #{HudMenu.NadesOn}") && Regex.IsMatch(rules, $@"\.gg-global\.nd-off #{HudMenu.NadesOff}"));
            var dropsOff = new Dictionary<string, string>(stock) { [Fun.Drops] = "0" };
            Check("...lit from gg_global, which every content has, off the drops' fun setting (the owner's 61 b): on is one class, off the other, and outside Arms Race neither",
                HudVote.FixedRowClasses(cv => stock.GetValueOrDefault(cv), true).Single(x => x.Class == "nd-on").On && !HudVote.FixedRowClasses(cv => stock.GetValueOrDefault(cv), true).Single(x => x.Class == "nd-off").On
                && HudVote.FixedRowClasses(cv => dropsOff.GetValueOrDefault(cv), true).Single(x => x.Class == "nd-off").On
                && HudVote.FixedRowClasses(cv => stock.GetValueOrDefault(cv), false).All(x => !x.On) && HudVote.FixedRowClasses(cv => stock.GetValueOrDefault(cv), true).All(x => x.Id == HudView.Global));

            // ---- the Fun page and the Ads buttons, as the plugin fills and reads them ----
            var stockPage = HudMenu.FunPage(cv => stock.GetValueOrDefault(cv));
            bool FC(string id, string cls) => stockPage.Classes.Single(x => x.Id == id && x.Class == cls).On;
            string FT(string id) => stockPage.Texts.Single(x => x.Id == id).Text;
            Check($"Fun page: a row per setting of the grid ({Math.Min(Fun.Settings.Length, Fun.Rows)} of {Fun.Rows}; the grenade drops and catch-up have fixed rows), the rest collapsed; a button per choice, the rest collapsed; on a stock server every first choice lit",
                Enumerable.Range(1, Fun.Rows).All(r => FC(HudMenu.FunRow(r), "used") == r <= Fun.Settings.Length)
                && Fun.Settings.Take(Fun.Rows).Select((s, i) => Enumerable.Range(1, Fun.ChoicesPerRow).All(k => FC(HudMenu.FunButton(i + 1, k), "used") == k <= s.Choices.Length
                    && FC(HudMenu.FunButton(i + 1, k), "on") == (k == 1) && FT($"{HudMenu.FunButton(i + 1, k)}_txt") == (k <= s.Choices.Length ? s.Choices[k - 1].Label : ""))).All(x => x)
                && FT("gg_f1_title") == Fun.Settings[0].Title && FT("gg_f1_txt") == Fun.Settings[0].Words);
            var moon = new Dictionary<string, string>(stock) { ["sv_gravity"] = "200", ["sv_airaccelerate"] = "2000" };
            var moonPage = HudMenu.FunPage(cv => moon.GetValueOrDefault(cv));
            var gravityRow = Array.FindIndex(Fun.Settings, s => s.Id == "gravity") + 1;
            var bhopRow = Array.FindIndex(Fun.Settings, s => s.Id == "bhop") + 1;
            Check("Fun page: Moon gravity lights Moon; a map's own air acceleration lights bunny hopping's Off and says the map runs its own",
                moonPage.Classes.Single(x => x.Id == HudMenu.FunButton(gravityRow, 3) && x.Class == "on").On && !moonPage.Classes.Single(x => x.Id == HudMenu.FunButton(gravityRow, 1) && x.Class == "on").On
                && moonPage.Classes.Single(x => x.Id == HudMenu.FunButton(bhopRow, 1) && x.Class == "on").On && moonPage.Texts.Single(x => x.Id == $"{HudMenu.FunRow(bhopRow)}_txt").Text.EndsWith("this map runs its own"));
            Check("Fun page: a click names its setting and choice, and nothing else does (the size chips are gg_fl_*, the note a label)",
                HudMenu.FunClick(HudMenu.FunButton(gravityRow, 2)) is ({ Id: "gravity" }, { Id: "low" }) && HudMenu.FunClick(HudMenu.FunButton(gravityRow, 1)) is (_, { Id: "normal" })
                && HudMenu.FunClick(HudMenu.FunButton(Fun.Rows + 1, 1)) == null && HudMenu.FunClick(HudMenu.FunButton(2, 3)) == null
                && HudMenu.FunClick("gg_fl_all") == null && HudMenu.FunClick(HudMenu.FunNote) == null && HudMenu.FunClick("gg_f1_x") == null
                && Fun.Settings.Take(Fun.Rows).Select((s, i) => s.Choices.Select((c, k) => HudMenu.FunClick(HudMenu.FunButton(i + 1, k + 1)) is var (fs, fc) && fs == s && fc == c).All(x => x)).All(x => x));
            Check("Fun page: its note says how long a change lasts, in the bridge's numbers", HudMenu.FunNoteText.Contains($"{ResetEmptyMinutes} minutes with nobody on") && StrayNumbers(HudMenu.FunNoteText).Count == 0
                && Fun.Settings.All(s => StrayNumbers($"{s.Title} {s.Words} {string.Join(" ", s.Choices.Select(c => c.Label))}").Count == 0));
            // Friendly fire's three (2026-10-29): Off on friendly fire dims the rows that need it, which then say so instead of their words.
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
            // #4 (2026-11-01): free-for-all on dims friendly fire's three - everybody is an enemy, so they change nothing.
            var ffaOn = new Dictionary<string, string>(stock) { ["mp_teammates_are_enemies"] = "true" };
            var ffaPage = HudMenu.FunPage(cv => ffaOn.GetValueOrDefault(cv));
            var friendly = new[] { "ff", "instakill", "ffnades" }.Select(id => Array.FindIndex(Fun.Settings, s => s.Id == id) + 1).ToList();
            Check("Fun page (#4): free-for-all On dims friendly fire, Instakill and Grenades, each saying free-for-all makes them do nothing; nothing else dims",
                friendly.All(r => r > 0 && Idle(ffaPage, r) && ffaPage.Texts.Single(t => t.Id == $"{HudMenu.FunRow(r)}_txt").Text.Contains("free-for-all"))
                && Enumerable.Range(1, Fun.Rows).Where(r => !friendly.Contains(r)).All(r => !Idle(ffaPage, r)));
            Check("Fun page: a dimmed row's buttons still work - Instakill Off and Grenades On are clicks while friendly fire is off",
                needing.All(x => HudMenu.FunClick(HudMenu.FunButton(x.Row, 2)) is var (fs, fc) && fs == x.Setting && fc == x.Setting.Choices[1]));

            // ---- content v4_4_0: the Fun page's Random and Default (the owner's 2026-11-03) ----
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
            Check($"...and catch-up on three times in four (the owner's 43 c): on in {catchUpOn} of {draws}, off on the dice's 0 alone, Arms Race's own",
                catchUpOn is > 4300 and < 4700 && CatchUpOf(Fun.Randomized(n => n - 1, armsRace: true)) == "on" && CatchUpOf(Fun.Randomized(n => 0, armsRace: true)) == "off"
                && CatchUpOf(Fun.Randomized(n => 0, armsRace: false)) == "none");
            Check("...outside Arms Race it leaves out what the page leaves out there: friendly fire's three, the grenade drops and catch-up",
                otherDraw.Count == Fun.Settings.Count(s => !Fun.ArmsRaceOnly(s)) && otherDraw.All(x => !Fun.ArmsRaceOnly(x.Setting))
                && Fun.Randomized(n => n - 1, armsRace: true).Single(x => x.Setting.Id == "jump").Choice == Fun.Find("jump")!.Normal);
            var gravity = Fun.Find("gravity")!;
            var everyChange = Fun.Settings.Select(s => (s, s.Choices[^1])).ToList();
            Check("...and its one chat line says who and what changed, the rest counted past 200 characters; Default's says what it did",
                Fun.RandomLine("Remy", [(gravity, Fun.Find(gravity, "low")!), (Fun.Find("drops")!, Fun.Find(Fun.Find("drops")!, "off")!)]) == "Remy rolled the fun settings: gravity low, grenade drops off"
                && Fun.RandomLine("Remy", []) == "Remy rolled the fun settings: nothing changed" && Fun.RandomLine("Remy", everyChange) is { Length: <= 200 } longLine && longLine.EndsWith(" more")
                && Fun.DefaultLine("Remy") == "Remy put every fun setting back to normal"
                && Fun.RandomLine("Remy", [(Fun.Find("catchup")!, Fun.Find(Fun.Find("catchup")!, "off")!)]) == "Remy rolled the fun settings: catch-up off"
                && HudView.LevelOfButton(HudMenu.FunRandom) == 44 && HudView.LevelOfButton(HudMenu.FunDefault) == 44);
            // The Fun page's height (Over Strike 2: sixteen rows in two columns of eight): the pages have 633 px (the window's 820 less its rule,
            // head, tabs and foot); the Fun page spends its padding, the note's line (about 1.3 times its type) and margin, and a column's rows
            // with their margins. A row holds its title line (the buttons' height, at least 1.3 times the title's type) over its words (1.3 times
            // their type).
            var funPad = Regex.Match(rules, @"\.gg-page-fun\s*\{[^}]*padding:\s*(\d+)px \d+px (\d+)px");
            var funNote = Regex.Match(rules, @"\.gg-fun-note\s*\{[^}]*margin-bottom:\s*(\d+)px;[^}]*font-size:\s*(\d+)px;");
            var funRowCss = Regex.Match(rules, @"\.gg-frow\s*\{[^}]*height:\s*(\d+)px;[^}]*margin-bottom:\s*(\d+)px;");
            var funTop = Regex.Match(rules, @"\.gg-frow-top\s*\{[^}]*height:\s*(\d+)px;");
            var funTitle = Regex.Match(rules, @"\.gg-frow-title\s*\{[^}]*font-size:\s*(\d+)px;");
            var funTxt = Regex.Match(rules, @"\.gg-frow-txt\s*\{[^}]*margin-top:\s*(\d+)px;[^}]*font-size:\s*(\d+)px;");
            var funButton = Regex.Match(rules, @"\.gg-funb\s*\{[^}]*height:\s*(\d+)px;");
            var funTopCss = Regex.Match(rules, @"\.gg-funtop\s*\{[^}]*margin-bottom:\s*(\d+)px;"); // content v4_4_0: the note's line holds Random and Default
            var funAllCss = Regex.Match(rules, @"\.gg-funallb\s*\{[^}]*height:\s*(\d+)px;");
            static int Px(Match m, int g) => int.Parse(m.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture);
            var funParsed = funPad.Success && funNote.Success && funRowCss.Success && funTop.Success && funTitle.Success && funTxt.Success && funButton.Success && funTopCss.Success && funAllCss.Success;
            var funXml = xml[xml.IndexOf("gg-page-fun", StringComparison.Ordinal)..];
            var funSplit = funXml.IndexOf("gg-fcol-right", StringComparison.Ordinal);
            var perColumn = Math.Max(Regex.Matches(funXml[..funSplit], @"class=""gg-frow[ ""]").Count, Regex.Matches(funXml[funSplit..], @"class=""gg-frow[ ""]").Count); // the grenade drops' row makes the right column the longer
            // the note's line: the taller of the note and Random's and Default's buttons, and its row's margin (the real client of 2026-11-03 showed the
            // last row under the window's foot with 40 px buttons and an 8 px margin there: 22 px past what fitted)
            var funTopLine = funParsed ? Math.Max((int)Math.Ceiling(Px(funNote, 2) * 1.3), Px(funAllCss, 1)) + Px(funTopCss, 1) : 0;
            var funHeight = funParsed ? Px(funPad, 1) + Px(funPad, 2) + funTopLine + perColumn * (Px(funRowCss, 1) + Px(funRowCss, 2)) : int.MaxValue;
            var rowNeeds = funParsed ? Math.Max(Px(funTop, 1), Math.Max((int)Math.Ceiling(Px(funTitle, 1) * 1.3), Px(funButton, 1))) + Px(funTxt, 1) + (int)Math.Ceiling(Px(funTxt, 2) * 1.3) : int.MaxValue;
            Check($"Fun page fits the window: about {funHeight} px of the pages' 633 for its {Fun.Rows} rows and the grenade drops ({perColumn} in the longer column), and a row's {(funParsed ? Px(funRowCss, 1) : 0)} px hold its title line and words (about {rowNeeds} px)",
                funHeight <= 633 && funParsed && rowNeeds <= Px(funRowCss, 1) && perColumn == Fun.Rows / 2 + 1 && funTopLine <= (int)Math.Ceiling(Px(funNote, 2) * 1.3) + 8);
            // ---- content v4_4_0: the Maps page while the vote is open - a Play adds to it - and every card's mode; Our maps' glow ----
            var voteCat = new HudCatalog { Version = 1 };
            voteCat.Maps.Add(new HudCatalog.Entry("3084291314", "1", 1, 3084291314, "AIM Map", 20, false, -1, 0, 0, 0));
            voteCat.Maps.Add(new HudCatalog.Entry("3084291315", "2", 2, 3084291315, "Other Map", 20, false, -1, 0, 0, 0));
            var openVote = VoteState.Decode("mode\tarmsrace\tArms Race\nmv\t1793700000\topen\t40\t2\t1\tend\t0\t1\nmvo\t1\tarmsrace\trolled\t1\t3084291314\t#1 AIM Map\t20 MB\t1\t\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t0\t\nmvv\t1001\t1\n");
            string Play((List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) page, int k) => page.Texts.Single(x => x.Id == $"gg_m{k}_play_text").Text;
            bool VC((List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) page, string id, string cls) => page.Classes.Single(x => x.Id == id && x.Class == cls).On;
            var voter = HudMenu.Maps(voteCat, new HudState(), new HudBrowser(), 1001, now, vote: openVote);
            var other = HudMenu.Maps(voteCat, new HudState(), new HudBrowser(), 1002, now, vote: openVote);
            var noVote = HudMenu.Maps(voteCat, new HudState(), new HudBrowser(), 1002, now);
            Check("The map browser while the vote is open: a map on the ballot says On the vote (or Your vote), any other one Add to the vote; with no vote, Put to the vote",
                Play(voter, 1) == "Your vote" && Play(voter, 2) == "Add to the vote" && Play(other, 1) == "On the vote" && Play(noVote, 1) == "Put to the vote" && Play(noVote, 2) == "Put to the vote");
            var oursOn = new HudBrowser();
            oursOn.Click(HudBrowser.OursButton, 0);
            var oursPage = HudMenu.Maps(voteCat, new HudState(), oursOn, 1002, now);
            Check("Every card wears the mode it plays in, exactly one; an empty card none; Our maps glows until it is chosen (fl-ours on gg_screen)",
                VC(noVote, "gg_m1", "mode-armsrace") && !VC(noVote, "gg_m1", "mode-deathmatch") && Modes.All.All(m => !VC(noVote, "gg_m3", $"mode-{m.Id}"))
                && Enumerable.Range(1, 2).All(k => Modes.All.Count(m => VC(noVote, $"gg_m{k}", $"mode-{m.Id}")) == 1)
                && !VC(noVote, HudView.Screen, "fl-ours") && VC(oursPage, HudView.Screen, "fl-ours"));
            // ---- content v4_5_0: the Fun page's changed marks, the centre notice, and what the layout and stylesheet hold for them ----
            var lowGravity = Fun.Settings.SelectMany(s => s.Stock).GroupBy(x => x.ConVar).ToDictionary(g => g.Key, g => g.First().Value);
            lowGravity["sv_gravity"] = "400";
            var changedPage = HudMenu.FunPage(cv => lowGravity.GetValueOrDefault(cv));
            var gravityRow2 = Array.FindIndex(Fun.Settings, s => s.Id == "gravity") + 1;
            Check("Fun page: a setting off its default is marked changed (gold, with a dot); every other row plain",
                changedPage.Classes.Single(x => x.Id == HudMenu.FunRow(gravityRow2) && x.Class == "changed").On
                && Enumerable.Range(1, Fun.Rows).Where(r => r != gravityRow2).All(r => !changedPage.Classes.Single(x => x.Id == HudMenu.FunRow(r) && x.Class == "changed").On)
                && Enumerable.Range(1, Fun.Rows).All(r => !stockPage.Classes.Single(x => x.Id == HudMenu.FunRow(r) && x.Class == "changed").On));
            var vampire = Fun.Find("vampire")!;
            Check("The centre notice of a fun change: who in gold, what changed under it, wrapped to the panel, names escaped",
                Fun.CentreLine("Alice", vampire, Fun.Find(vampire, "on")!) == "Alice turned Vampire on" && Fun.CentreLine("Remy", gravity, Fun.Find(gravity, "low")!) == "Remy set Gravity to Low"
                && Fun.CentreLine("Remy", gravity, gravity.Normal) == "Remy put Gravity back to normal" && FunNotice.Html("<b>x</b>", "turned Vampire on") == "<font color='#E9B44C'><b>&lt;b&gt;x&lt;/b&gt;</b></font><br>turned Vampire on"
                && FunNotice.Wrap(string.Join(" ", Enumerable.Repeat("gravity low,", 30)), FunNotice.Width, FunNotice.Lines) is { Count: FunNotice.Lines } wrapped && wrapped.All(l => l.Length <= FunNotice.Width) && wrapped[^1].EndsWith(" ...")
                && Fun.CentreSeconds == 10);
            Check("Catch-up's row: its two buttons prove content v4_5_0, its state is cu-on or cu-off on gg_global, and the page leaves it out outside Arms Race",
                noComments.Contains($"<Button id=\"{HudMenu.CatchUpOn}\"") && noComments.Contains($"<Button id=\"{HudMenu.CatchUpOff}\"") && noComments.Contains("class=\"gg-frow gg-frow-catchup gg-set-catchup\"")
                && HudVote.FixedRowClasses(cv => cv == Fun.CatchUp ? "0" : "1", true).Single(x => x.Class == "cu-off").On && !HudVote.FixedRowClasses(cv => "1", true).Single(x => x.Class == "cu-off").On
                && HudVote.FixedRowClasses(cv => "1", false).All(x => !x.On)
                && Regex.IsMatch(rules, @"\.gg-global\.mode-deathmatch \.gg-set-catchup\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(rules, @"\.gg-global\.mode-wingman \.gg-set-catchup\s*\{\s*visibility:\s*collapse;")
                && Regex.IsMatch(rules, @"\.gg-global\.cu-on #gg_set_catchup_on\s*\{") && Regex.IsMatch(rules, @"\.gg-global\.cu-off #gg_set_catchup_off\s*\{"));
            string[] v45Classes = ["vlead", "vtie", "vmine", "vhas", "vstrip", "vlock", "vaway", "opulse", "changed", "cu-on", "cu-off", "vrand", .. Enumerable.Range(0, 11).Select(k => $"w{k}")];
            var v45Unstyled = v45Classes.Where(cls => !Regex.IsMatch(rules, $@"\.{Regex.Escape(cls)}\b")).ToList();
            Check($"Content v4_5_0's classes each have a rule{(v45Unstyled.Count > 0 ? ": none for " + string.Join(", ", v45Unstyled) : "")}; the layout holds every card's bar, voters and Remove mine, the strip's nine segments, the waiting line and the line back",
                v45Unstyled.Count == 0 && Enumerable.Range(1, HudMenu.Cards).All(k => noComments.Contains($"text=\"{{s:{HudVote.VotersVar(k)}}}\"") && noComments.Contains($"<Button id=\"{HudVote.UnqueueButton(k)}\""))
                && Regex.Matches(noComments, "class=\"gg-mvbar\"").Count == HudMenu.Cards && Enumerable.Range(1, HudVote.StripSegments).All(i => noComments.Contains($"<Panel id=\"{HudVote.Segment(i)}\" class=\"gg-vseg\"><Label class=\"gg-vseg-text\" text=\"{{s:{HudVote.StripVar(i)}}}\" /></Panel>"))
                && noComments.Contains($"text=\"{{s:{HudVote.WaitVar}}}\"") && noComments.Contains($"text=\"{{s:{HudVote.AwayVar}}}\"") && Regex.IsMatch(rules, @"\.gg-screen\.vlock \.gg-close\s*\{\s*visibility:\s*collapse;"));
            var adsCat = new HudCatalog { Version = 1 };
            adsCat.Maps.Add(new HudCatalog.Entry("3084291314", "1", 1, 3084291314, "AIM Map", 20, false, -1, 0, 0, 2, Ads: 1));
            adsCat.Votes[1001] = new() { ["3084291314"] = HudCatalog.AdsVote };
            var adsMenu = HudMenu.Maps(adsCat, new HudState(), new HudBrowser(), 1001, now);
            Check("Cards: a map with an ads mark shows the Ads tag, this player's ads mark lights the card's Ads, and the card's Ads button sends !down <rank> ads",
                adsMenu.Classes.Single(x => x.Id == "gg_m1" && x.Class == "ads").On && adsMenu.Classes.Single(x => x.Id == "gg_m1" && x.Class == "adsvoted").On
                && adsMenu.Classes.Single(x => x.Id == "gg_m1" && x.Class == "disliked").On && HudMenu.Command("gg_m1_ads", adsCat, new HudState(), new HudBrowser(), 1001, now) == "!down 1 ads"
                && Hud.IsAction("!down 1 ads"));
            var adsLive = new HudState { Ads = 2, MyVotes = new() { [7] = HudCatalog.AdsVote } };
            Check("The board: Ads sends !down ads, its count is the ads marks on the map that is on, and the player's own ads mark lights it",
                HudView.Command(HudView.BoardAds, adsLive) == "!down ads" && HudView.Texts(adsLive).Single(x => x.Id == "gg_b_ads").Text == "2"
                && HudView.PlayerClasses(adsLive, 7).Single(x => x.Class == "voted-ads").On && HudView.PlayerTexts(adsLive, 7, "x", false).Single(x => x.Id == "gg_b_you").Text == "You marked this map: ads");
            Check("Help: content v3_2_0's two fifth rows stay empty since the owner's 64 a (one Fun row, one row for a Dislike's reasons, no Ads row)",
                Hud.HelpMore.Length == 0 && Hud.Help.Count(g => g.Rows.Any(r => r.Command.StartsWith("Fun"))) == 1 && !Hud.Help.Any(g => g.Rows.Any(r => r.Command == "Ads")));
            // The Help page's height with content v3_2_0's two rows and its tighter rows (1 px a row each way, 4 + 5 px a group): the budget of
            // the check above with those numbers.
            static int Lines32(Hud.Row r) => (int)Math.Ceiling((r.Also == "" ? r.Words : $"{r.Words}  ·  {r.Also}").Length / 60.0);
            var help32 = Hud.Help.Sum(g => g.Rows.Sum(r => Lines32(r) * 19 + 2) + 10) + Hud.HelpMore.Sum(m => Lines32(m.Row) * 19 + 2);
            Check($"Help with content {Hud.Content}: about {help32} px of its 599 with the two rows added", help32 <= 599 && Regex.IsMatch(rules, @"\.gg-crow\s*\{\s*padding:\s*1px 0px;"));

            // ---- the thumbnails: a rule per map the menu can show, each its own map's picture, out of a few class names ----
            var thumbClasses = new HashSet<string>();
            if (File.Exists(HudThumbFile))
            {
                var thumbCss = File.ReadAllText(HudThumbFile);
                var thumbRules = Regex.Matches(thumbCss, @"^\.(th\d+)\.(tl\d+)\s*\{\s*background-image:\s*url\(\s*""([^""]+)""\s*\);\s*\}", RegexOptions.Multiline)
                    .ToDictionary(x => x.Groups[1].Value + "." + x.Groups[2].Value, x => x.Groups[3].Value);
                thumbClasses = thumbRules.Keys.SelectMany(k => k.Split('.')).ToHashSet();
                var shown = Maps.Where(x => !IsPack(x)).Concat(Officials).Select(x => new HudCatalog.Entry("", "", x.Rank, x.Id, x.Name, x.SizeMb, x.Official, -1, 0, 0, 0)).ToList();
                static string Rule(HudCatalog.Entry x) => string.Join(".", HudThumbs.Classes(x));
                // Every map the menu shows has its own picture. The hand-picked maps after the 1,000 most-subscribed - the 16 glass maps of
                // 2026-10-21, #1001-#1016 - got theirs with layout release v3_0_0: a map listed after a release has none until the next one
                // (its card shows an empty picture), since each release is a new Workshop item that waits for Valve's approval.
                // Since content v3_1_0 (2026-10-22) the list can grow after a picture release: maps listed after it (the tail of the list,
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
                    xml.Contains($"s2r://panorama/styles/custom_game/gungame_hud_thumbs_{Hud.Release}.vcss_c") && Regex.Matches(Regex.Replace(thumbCss, @"/\*.*?\*/", "", RegexOptions.Singleline), @"^\{", RegexOptions.Multiline).Count == thumbRules.Count);
            }
            else Console.WriteLine($"skip thumbnail checks: {HudThumbFile} not found");

            // ---- the game's own limit: one layout entity interns at most 1,024 panel ids and 1,024 class names that the server sets, for
            // its whole life and for all players together. Past it the server logs "The maximum number of ... has been reached" and the
            // client's behaviour is unknown. Every class the plugin sets has a rule (checked above), so the stylesheets bound it. ----
            var styleClasses = Regex.Matches(rules, @"([^{}]+)\{").SelectMany(x => Regex.Matches(x.Groups[1].Value, @"\.([A-Za-z_][\w-]*)").Select(y => y.Groups[1].Value))
                .Concat(thumbClasses).ToHashSet();
            Check($"Limit: at most 1,024 class names and 1,024 panel ids per entity - the stylesheets name {styleClasses.Count} classes, the layout {ids.Count} ids (both under 900)",
                styleClasses.Count < 900 && ids.Count < 900);
            Check("A value starting with # (a rank, a name) is kept from being read as a localization token, and nothing else is touched",
                Hud.Plain("#5") == "\u2060#5" && Hud.Plain("Map #5") == "Map #5" && Hud.Plain("") == "");
        }
        else Console.WriteLine($"skip HUD layout checks: {HudLayoutFile} or {HudStyleFile} not found");
    }

    /// <summary>For comparing words: typographic apostrophes and dashes as plain ones, spaces collapsed.</summary>
    static string Norm(string s) => Regex.Replace(s.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u2013', '-').Replace('\u2014', '-').Replace('\u00a0', ' '), @"\s+", " ").Trim();
}
