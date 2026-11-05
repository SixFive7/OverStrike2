// --selftest, part six: the menu that replaced the chat commands. The search index and its scoring, the on-screen keyboard,
// the card for a Workshop id that is not on the list, the news line and the notes, the board's buttons - and, command by command, the
// place in the menu that does what the command did (README "The menu replaces the chat commands" lists the same).
using System.Text.Json;

static partial class Program
{
    static void GuiSelfTest(Action<string, bool> Check)
    {
        // ---- the search index (HudSearch) ----
        Check("Search: text is lower case, letters and digits, one space between words - any script",
            HudSearch.Normal("  Glass-War_2!  ") == "glass war 2" && HudSearch.Normal("Ärmel ÖL") == "ärmel öl" && HudSearch.Normal("丨几千") == "丨几千" && HudSearch.Normal(null) == "");
        var index = new HudSearch();
        index.Set("1", "aim map 3084291314 cs2 classic deathmatch the classic map from cs 1 6 adapted with new textures");
        index.Set("2", "fy glass 3681294731 classic custom a simple map with glass floors");
        index.Set("3", "claim the hill 123 deathmatch");
        Check("Search: a word at the start of a word in the name counts most, then inside the name, then the tags and description",
            index.Score("1", "AIM Map", ["aim"]) > index.Score("3", "Claim the hill", ["aim"]) && index.Score("2", "fy_glass", ["glass"]) > index.Score("1", "AIM Map", ["glass"])
            && index.Score("1", "AIM Map", ["textures"]) > 0 && index.Score("2", "fy_glass", ["floors"]) > 0);
        Check("Search: every word has to be found somewhere, and no word means everything",
            index.Score("2", "fy_glass", ["glass", "floors"]) > 0 && index.Score("2", "fy_glass", ["glass", "dust"]) == 0 && index.Score("9", "x", []) == 1);
        var round = HudSearch.Decode(index.Encode());
        Check("Search: the index survives its codec, and anything else reads as none",
            round is { Count: 3 } && round.TextOf("2") == index.TextOf("2") && HudSearch.Decode("nope") == null && HudSearch.Decode(null) == null);
        var big = new HudSearch();
        big.Set("k", new string('a', 5000));
        Check($"Search: one map's text is kept to {HudSearch.MaxText} characters", big.TextOf("k")!.Length == HudSearch.MaxText);

        // ---- the keyboard (HudKeyboard) ----
        var keys = HudKeyboard.Keys().ToList();
        Check($"Keyboard: 26 letters, 10 digits, space, backspace, clear and done - {keys.Count} keys, each its own button", keys.Count == 40 && keys.Distinct().Count() == 40);
        Check("Keyboard: a key types its character; backspace, clear and space edit; space never first or twice; nothing past the limit",
            HudKeyboard.Press("", "gg_kb_g") == "g" && HudKeyboard.Press("g", "gg_kb_7") == "g7" && HudKeyboard.Press("ab", HudKeyboard.Back) == "a" && HudKeyboard.Press("", HudKeyboard.Back) == ""
            && HudKeyboard.Press("abc", HudKeyboard.Clear) == "" && HudKeyboard.Press("", HudKeyboard.Space) == "" && HudKeyboard.Press("ab ", HudKeyboard.Space) == "ab "
            && HudKeyboard.Press("ab", HudKeyboard.Space) == "ab " && HudKeyboard.Press(new string('x', HudSearch.MaxQuery), "gg_kb_y") == new string('x', HudSearch.MaxQuery)
            && HudKeyboard.Press("ab", "gg_m1_play") == null && HudKeyboard.CharOf("gg_kb_Q") == null && HudKeyboard.CharOf("gg_kb_q") == 'q');

        // ---- the map browser with full-text search and the keyboard ----
        var catalog = new HudCatalog
        {
            Version = 1,
            Maps =
            [
                new("3084291314", "1", 1, 3084291314, "AIM Map", 20, false, -1, 0, 4, 0),
                new("3681294731", "1015", 1015, 3681294731, "Fy_glass", 3, false, -1, 0, 0, 0),
                new("123", "7", 7, 123, "Claim the hill", 30, false, -1, 0, 9, 1),
                new("ar_shoots", "ar_shoots", 0, 0, "ar_shoots", 0, true, -1, 0, 0, 0),
            ],
        };
        index.Set("3084291314", "aim map 3084291314 cs2 classic deathmatch the classic map from cs 1 6 adapted with new textures");
        index.Set("3681294731", "fy glass 3681294731 classic custom a simple map with glass floors");
        index.Set("123", "claim the hill deathmatch");
        var b = new HudBrowser();
        foreach (var k in new[] { HudKeyboard.Open, "gg_kb_f", "gg_kb_l", "gg_kb_o", "gg_kb_o", "gg_kb_r", "gg_kb_s" }) b.Click(k, 0);
        Check("Keyboard in the browser: the search box opens it, keys type the query, the page goes back to the first",
            b.Keyboard && b.Query == "floors" && b.Matches(catalog, 1, index) is [{ Name: "Fy_glass" }]);
        b.Page = 3; b.Click(HudKeyboard.Back, 4);
        Check("...a key press starts from page one again, and Done closes the keyboard but keeps the query",
            b.Page == 0 && b.Query == "floor" && b.Click(HudKeyboard.Done, 4) && !b.Keyboard && b.Query == "floor");
        b.Query = "aim";
        Check("A search finds by description too, best match first under the default sort: AIM Map before Claim the hill",
            b.Matches(catalog, 1, index).Select(m => m.Name).SequenceEqual(["AIM Map", "Claim the hill"]));
        b.Query = "1015";
        Check("A number is also a rank", b.Matches(catalog, 1, index) is [{ Rank: 1015 }]);
        b.Query = "3681294731";
        Check("...and a Workshop id on the list is that map, no card", b.Matches(catalog, 1, index) is [{ Id: 3681294731 }] && b.WorkshopQuery(catalog) == null);
        b.Query = "3232901690";
        var (wt, wc) = HudMenu.Maps(catalog, new HudState(), b, 1, DateTime.UtcNow, index);
        Check("A Workshop id not on the list is the card's alone: no map cards under it (they ran off the page), no empty-page message",
            b.WorkshopQuery(catalog) == 3232901690 && b.Matches(catalog, 1, index).Count == 0 && !wc.Single(c => c.Id == "gg_m_empty").On && wt.Single(t => t.Id == "gg_m_empty").Text == "");
        var settle = new WorkshopCard();
        var typedAt = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        settle.Show(3232901, typedAt); settle.Show(3232901, typedAt.AddSeconds(5));
        Check($"The card remembers when its id was typed (the plugin asks the Workshop only {WorkshopCard.Settle.TotalSeconds} s later): the same id again keeps that time, a longer one starts again",
            settle.Since == typedAt && WorkshopCard.Settle > TimeSpan.Zero && Retyped(settle, typedAt.AddSeconds(6)) == typedAt.AddSeconds(6));
        static DateTime Retyped(WorkshopCard c, DateTime at) { c.Show(32329016, at); return c.Since; }
        b.Query = "glass"; b.Keyboard = true;
        var (kt, kc) = HudMenu.Maps(catalog, new HudState(), b, 1, DateTime.UtcNow, index);
        Check("The search box shows the query with a caret while the keyboard is open, and the keyboard's class is on the player's screen",
            kt.Single(t => t.Id == "gg_q_text").Text == "glass|" && kc.Contains((HudView.Screen, "kbd", true)) && kc.Contains(("gg_q_search", "used", true)));

        // ---- the card for a Workshop id that is not on the list (WorkshopCard) ----
        Check("Workshop ids: 7 to 12 digits; a shorter number is a rank, anything else is no id",
            WorkshopCard.IdOf("3232901690") == 3232901690 && WorkshopCard.IdOf("1234567") == 1234567 && WorkshopCard.IdOf("123456") == null
            && WorkshopCard.IdOf("12345678901234") == null && WorkshopCard.IdOf("12a4567") == null && WorkshopCard.IdOf(" 3232901690 ") == 3232901690);
        Check("...pasted in chat: a Workshop link or a bare id; plain chat is not one",
            WorkshopCard.IdInChat("https://steamcommunity.com/sharedfiles/filedetails/?id=3232901690&searchtext=glass") == 3232901690
            && WorkshopCard.IdInChat("steamcommunity.com/workshop/filedetails/?id=3631955210") == 3631955210 && WorkshopCard.IdInChat("3631955210") == 3631955210
            && WorkshopCard.IdInChat("gg wp") == null && WorkshopCard.IdInChat("!map 12") == null && WorkshopCard.IdInChat("https://example.com/?id=3232901690") == null);
        const string details = """
            {"response":{"result":1,"resultcount":5,"publishedfiledetails":[
              {"publishedfileid":"3232901690","result":1,"consumer_app_id":730,"title":"Glass War","file_size":"138467339","visibility":0,"banned":false,"tags":[{"tag":"Map"},{"tag":"Custom"}]},
              {"publishedfileid":"111","result":9},
              {"publishedfileid":"222","result":1,"consumer_app_id":730,"title":"Hidden","file_size":10,"visibility":2,"banned":false},
              {"publishedfileid":"333","result":1,"consumer_app_id":730,"title":"Banned","file_size":10,"visibility":0,"banned":true},
              {"publishedfileid":"444","result":1,"consumer_app_id":4000,"title":"A Garry's Mod thing","file_size":10,"visibility":0,"banned":false},
              {"result":1}]}}
            """;
        var items = WorkshopItem.Parse(details);
        Check("The Web API's answer: title, size (sent as a string), tags, and why an item cannot be played",
            items.Count == 5 && items[3232901690] is { Title: "Glass War", Bytes: 138467339, Refusal: null } g && g.Tags.SequenceEqual(["Map", "Custom"])
            && items[111].Refusal!.Contains("not on the Workshop") && items[222].Refusal!.Contains("not public") && items[333].Refusal!.Contains("banned")
            && items[444].Refusal!.Contains("not a Counter-Strike 2 item") && WorkshopItem.Parse("{}").Count == 0);
        var card = new WorkshopCard();
        card.Show(3232901690);
        Check("Card: asking Steam first - nothing to click yet", card.At == WorkshopCard.Step.Checking && card.Texts().Single(t => t.Id == "gg_ws_status").Text.Contains("Asking the Workshop")
            && card.Classes().Contains((WorkshopCard.Play, "off", true)) && card.Classes().Contains((WorkshopCard.Panel, "used", true)));
        card.Checked(items[3232901690]);
        Check("...a playable item: its title and size, and one button to download and play it",
            card.At == WorkshopCard.Step.Ready && card.Texts().Single(t => t.Id == "gg_ws_title").Text == "Glass War" && card.Texts().Single(t => t.Id == "gg_ws_info").Text.Contains("138 MB")
            && card.Texts().Single(t => t.Id == "gg_ws_play_text").Text == "Download and play" && card.Classes().Contains((WorkshopCard.Play, "off", false)));
        card.Started(queued: true);
        Check("...another download running: it waits its turn", card.At == WorkshopCard.Step.Waiting && card.Texts().Single(t => t.Id == "gg_ws_status").Text.Contains("starts right after"));
        card.Progress(0, steamBusy: true);
        Check("...Steam's once-a-boot cache check first: the card says so instead of a stuck bar",
            card.At == WorkshopCard.Step.Downloading && card.Texts().Single(t => t.Id == "gg_ws_status").Text.Contains("15 minutes") && card.Classes().Contains((WorkshopCard.Bar, "p0", true)));
        card.Progress(47, steamBusy: false);
        Check("...then the download, its progress in tenths on the bar", card.Texts().Single(t => t.Id == "gg_ws_status").Text.Contains("47%") && card.Classes().Contains((WorkshopCard.Bar, "p4", true))
            && card.Classes().Count(c => c.Id == WorkshopCard.Bar && c.On) == 1);
        card.Progress(100, steamBusy: false);
        Check("...never 100% before it is really installed", card.Percent == 99);
        card.Installed(); card.Requested();
        Check("...installed, then asked for: a full green bar and nothing more to click",
            card.At == WorkshopCard.Step.Requested && card.Classes().Contains((WorkshopCard.Bar, "p10", true)) && card.Classes().Contains((WorkshopCard.Play, "off", true)));
        var refused = new WorkshopCard();
        refused.Show(222); refused.Checked(items[222]);
        Check("A private item is refused with its reason, and its card has no button", refused.At == WorkshopCard.Step.Refused
            && refused.Texts().Single(t => t.Id == "gg_ws_status").Text.StartsWith("Not playable: it is not public") && refused.Classes().Contains((WorkshopCard.Play, "off", true)));
        var failed = new WorkshopCard();
        failed.Show(3232901690); failed.Checked(null, "the Workshop did not answer");
        Check("No answer from Steam: the card says so; a failed download can be tried again",
            failed.At == WorkshopCard.Step.Failed && failed.Texts().Single(t => t.Id == "gg_ws_status").Text.Contains("did not answer")
            && Retry(items[3232901690]) is { At: WorkshopCard.Step.Failed } again && again.Texts().Single(t => t.Id == "gg_ws_play_text").Text == "Try again" && again.Classes().Contains((WorkshopCard.Play, "off", false)));
        static WorkshopCard Retry(WorkshopItem item) { var c = new WorkshopCard(); c.Show(item.Id); c.Checked(item); c.Started(false); c.Failed("Steam said 'Failure'"); return c; }
        card.Show(null);
        Check("Clearing the query takes the card away", card.At == WorkshopCard.Step.None && card.Classes().Contains((WorkshopCard.Panel, "used", false)));

        // ---- the server's own refusals on the card: non-maps, blocked maps, unlisted maps gone from the Workshop ----
        var cat = new HudCatalog { Version = 5 };
        cat.Refused[3076804331] = ("glassbox", "kicked every player off the server (2026-10-20) - it is blocked");
        cat.Refused[3369214545] = ("", "was removed from the Workshop - nobody can download it");
        var catBack = HudCatalog.Decode(cat.Encode())!;
        Check("Catalog: the ids the server refuses survive the codec with their name and reason; a line without a reason is dropped",
            catBack.Refused.Count == 2 && catBack.Refused[3076804331] == ("glassbox", "kicked every player off the server (2026-10-20) - it is blocked")
            && catBack.Refused[3369214545].Name == "" && HudCatalog.Decode(HudCatalog.Magic + "\nn\t42\tx\t\n")!.Refused.Count == 0);
        var blockedCard = new WorkshopCard();
        blockedCard.Show(3076804331);
        blockedCard.Refuse("glassbox", catBack.Refused[3076804331].Why);
        Check("Card: a blocked id is refused at once in the server's words, under its own name, with no button and no Workshop question",
            blockedCard.At == WorkshopCard.Step.Refused && blockedCard.Item == null && blockedCard.Texts().Single(t => t.Id == "gg_ws_title").Text == "glassbox"
            && blockedCard.Texts().Single(t => t.Id == "gg_ws_status").Text == "Not playable: it kicked every player off the server (2026-10-20) - it is blocked."
            && blockedCard.Classes().Contains((WorkshopCard.Play, "off", true)));
        blockedCard.Checked(items[3232901690]);
        Check("...and a late Workshop answer does not undo the refusal", blockedCard.At == WorkshopCard.Step.Refused);
        if (Maps.FirstOrDefault(m => !m.Official) is not { } listedGone) Console.WriteLine("skip the catalog's refusals: no maps loaded");
        else
        {
            var (packs, blocked, removed) = (Packs, Blocked, Removed);
            try
            {
                Packs = new Dictionary<string, string> { ["3369214545"] = "CS2 Aim Map" };
                Blocked = new() { ["3076804331"] = ("glassbox", "glassboxfinal", "kicked every player off the server (2026-10-20)"), ["123456789"] = ("x", "", "is on the blocked list") };
                Removed = new Dictionary<string, string> { ["987654321"] = Workshop.Removed, [listedGone.Id.ToString()] = Workshop.Removed };
                var built = BuildCatalog();
                Check("Bridge: the catalog carries every non-map, blocked id and unlisted gone id the server refuses, each in the card's words - a listed gone map stays a card on the list",
                    built.Refused[3369214545] == ("CS2 Aim Map", NotAMap) && built.Refused[3076804331] == ("glassbox", "kicked every player off the server (2026-10-20) - it is blocked")
                    && built.Refused[123456789].Why == "is on the blocked list" && built.Refused[987654321].Why.EndsWith("nobody can download it")
                    && !built.Refused.ContainsKey(listedGone.Id) && built.Maps.Single(m => m.Id == listedGone.Id).Removed);
            }
            finally { (Packs, Blocked, Removed) = (packs, blocked, removed); }
        }

        // ---- the live state's new lines: the news, the notes, the previous map, the search index's version ----
        var st = new HudState { News = new string('n', 230), NewsFor = 6, PrevArg = "12", PrevLabel = "#12 aim_redline", SearchVersion = 77 };
        st.Notes[1001] = ("You voted already - 1/2, 1 more needed", 5);
        st.Notes[1002] = ("expired", 0);
        var back2 = HudState.Decode(st.Encode())!;
        Check("State: the news line (up to 240 characters), the notes per player, the previous map and the search version survive the codec; an expired note is not sent",
            back2.News.Length == 230 && back2.NewsFor == 6 && back2.Notes.Count == 1 && back2.Notes[1001] == ("You voted already - 1/2, 1 more needed", 5)
            && (back2.PrevArg, back2.PrevLabel) == ("12", "#12 aim_redline") && back2.SearchVersion == 77);
        Check("...the note only its player sees: on their screen, nobody else's",
            HudView.NoteOf(back2, 1001) != "" && HudView.NoteOf(back2, 1003) == "" && HudView.PlayerClasses(back2, 1001).Contains((HudView.Screen, "note", true))
            && HudView.PlayerClasses(back2, 1003).Contains((HudView.Screen, "note", false)) && HudView.Classes(back2).Contains((HudView.Global, "news", true)));

        // ---- the board: a click action panel ----
        var free = new HudState { Map = "#5 x" };
        var open = VoteState.Decode("mv\t1793900000\topen\t30\t2\t0\tmid\t2\t1\n");
        var waiting = new VoteState { WaitFor = 80 };
        Check("Board: Like and Dislike are likes on the map that is on, Too big and Ads a Dislike's reasons; the button under them is Start a vote - the vote's window while one is on, the answer with the time left during the wait, nothing during a change",
            HudView.Command(HudView.BoardUp, free) == "!up" && HudView.Command(HudView.BoardDown, free) == "!down"
            && HudView.Command(HudView.BoardBig, free) == "!down big" && HudView.Command(HudView.BoardAds, free) == "!down ads" && Hud.IsAction("!down big") && Hud.IsAction("!down ads")
            && HudView.Command(HudView.BoardNext, free) == "!next" && HudView.Command(HudView.BoardNext, free, open) == HudView.OpenVote
            && HudView.Command(HudView.BoardNext, free, waiting) == "!next" && HudView.Command(HudView.BoardNext, new HudState { ChangeIn = 3 }) == null);
        Check("Board: the button's words in each case - start one, the vote on, the wait after one - and none of them a typed command",
            HudView.PlayerTexts(free, 2, "Bob", false).Single(t => t.Id == "gg_b_next_text").Text == "Start a vote"
            && HudView.PlayerTexts(free, 2, "Bob", false, open).Single(t => t.Id == "gg_b_next_text").Text.Contains("The vote is on")
            && HudView.PlayerTexts(free, 2, "Bob", false, waiting).Single(t => t.Id == "gg_b_next_sub").Text == "The next one can start in 1 min 20 s"
            && HudView.PlayerClasses(free, 2, waiting).Contains((HudView.Screen, "act-off", true)) && HudView.PlayerClasses(free, 2).Contains((HudView.Screen, "act-off", false))
            && new VoteState?[] { null, open, waiting }.Select(v => HudView.Action(free, v)).All(a => !a.Text.Contains('!') && !a.Sub.Contains('!')));
        var leading = VoteState.Decode("mv\t1793900001\topen\t20\t3\t3\tmid\t2\t1\nmvo\t2\tarmsrace\tpicked\t44\t44\t#44 aim_aim\t\t2\tAlice\nmvo\t1\tarmsrace\tkeep\t\tk\tKeep playing this map\t#5 x\t1\t\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t0\t\n");
        var lt = HudView.Texts(free, leading);
        var lc = HudView.Classes(free, leading);
        string LT(string id) => lt.Single(t => t.Id == id).Text;
        Check("Board and live column: the vote in one line, its leading cards in the old queue rows (there is no queue); outside a vote how to call one, or the wait",
            HudView.VoteLine(null) == "No vote on: Play on a map, or Start a vote" && HudView.VoteLine(leading) == "The vote is on: 20 s left  ·  3 of 3 voted"
            && HudView.VoteLine(waiting) == "The next vote can start in 1 min 20 s" && LT("gg_b_queue") == HudView.VoteLine(leading)
            && LT("gg_w_n1") == "#44 aim_aim" && LT("gg_w_nw1") == "2 votes" && LT("gg_w_n2") == "Keep playing this map" && LT("gg_w_nw2") == "1 vote" && LT("gg_w_n3") == ""
            && lc.Contains(("gg_w_row_n2", "used", true)) && lc.Contains(("gg_w_row_n3", "used", false)) && lc.Contains(("gg_w_nempty", "used", false))
            && HudView.Classes(free).Contains(("gg_w_nempty", "used", true)) && HudView.VoteLine(leading).Length <= HudView.QueueLineMax);

        // ---- every chat command, and where the menu has it now (the README's checklist says the same) ----
        var live = new HudState { Map = "#1 AIM Map", Key = "3084291314", PrevArg = "7", PrevLabel = "#7 Claim the hill" };
        var mine = new HudBrowser();
        var page = mine.PageOf(mine.Matches(catalog, 1, index));
        string? Card(int k, string part) => HudMenu.Command($"gg_m{k}_{part}", catalog, live, mine, 1, DateTime.UtcNow, index);
        var claim = page.FindIndex(m => m.Name == "Claim the hill") + 1;
        var rows = new (string Chat, string Where, string? Sends, string Expected)[]
        {
            ("!map <number|name|id>", "Maps: a card's Play, to the vote", Card(claim, "play"), "!map 7"),
            ("!map <unlisted Workshop id>", "Maps: the keyboard's digits, the item's card", WorkshopCard.IdOf("3232901690")?.ToString(), "3232901690"),
            ("!next", "Board or menu: Start a vote", HudView.Command(HudView.BoardNext, new HudState { Map = "x" }), "!next"),
            ("!previous !prev !back", "Previous", HudMenu.Command(HudMenu.Previous, catalog, live, mine, 1, DateTime.UtcNow), "!previous"),
            ("!find <words>", "Maps: Search and the keyboard", HudKeyboard.Press("", "gg_kb_a"), "a"),
            ("!up", "Board or menu: Like", HudView.Command(HudView.BoardUp, live), "!up"),
            ("!down", "Board or menu: Dislike", HudView.Command(HudView.ButtonDown, live), "!down"),
            ("!up <map>", "Maps: a card's up arrow", Card(claim, "up"), "!up 7"),
            ("!down <map>", "Maps: a card's down arrow", Card(claim, "down"), "!down 7"),
            ("!down <map> big", "Maps: a card's down arrow, then Too big in the dialog", Card(claim, "down") == "!down 7"
                && HudVote.WhyClick(HudVote.WhyButton(Array.FindIndex(DownReasons.All, r => r.Word == "big") + 1)) is { } bigWhy ? $"!down {bigWhy.Word}" : null, "!down big"),
            ("!down <map> big", "Maps: a card's Too big (the contents before v4.6.0)", Card(claim, "big"), "!down 7 big"),
            ("!top", "Maps: Sort by Likes; the live column's Most liked", Sorted(catalog, index).FirstOrDefault()?.Name, "Claim the hill"),
            ("!time 10|60", "Game: Match length", HudMenu.Command(HudMenu.Time10, catalog, live, mine, 1, DateTime.UtcNow) + " " + HudMenu.Command(HudMenu.Time60, catalog, live, mine, 1, DateTime.UtcNow), "!time 10 !time 60"),
            ("!restart", "Game: Restart (twice)", HudMenu.Command(HudMenu.Restart, catalog, live, mine, 1, DateTime.UtcNow), "!restart"),
            ("!nades on|off", "Fun: Grenade drops (a fun setting)", HudMenu.NadesOn + " " + HudMenu.NadesOff, "gg_set_nades_on gg_set_nades_off"),
            ("!gg !info", "Board: Open full menu; TAB + E", HudView.BoardOpen, "gg_b_open"),
            ("!help !maps", "Help page", HudView.Tabs.First(t => t.Page == HudSlots.Page.Help).Id, "gg_tab_help"),
        };
        static List<HudCatalog.Entry> Sorted(HudCatalog c, HudSearch i) { var v = new HudBrowser(); v.Click("gg_sort_votes", 0); return v.Matches(c, 1, i); }
        foreach (var (chat, where, sends, expected) in rows)
            Check($"Menu instead of chat: {chat} -> {where}", sends == expected && (!expected.StartsWith('!') || Hud.IsAction(expected.Split(' ')[0] == "!time" ? "!time 10" : expected)));
        Check("!rcon is gone, not moved: no button asks for a server command, and the bridge refuses one on /act",
            !Hud.IsAction("!rcon status") && !Hud.IsAction("!rcon") && ChatOnlyCommands.All(c => !Hud.IsAction(c)));

        // ---- the bridge: chat lines are presence only ----
        var (savedS, savedPending) = (S, Pending);
        try
        {
            S = new State(); Pending = null; ClearNotices();
            Ingest("10/21/2026 - 12:00:00.000 - \"Remy<2><[U:1:12345678]><CT>\" say \"!map 12\"", null);
            Ingest("10/21/2026 - 12:00:01.000 - \"Remy<2><[U:1:12345678]><CT>\" say \"!next\"", null);
            Check("A !map or !next typed in chat does nothing at the bridge: no change, no note, no news (the plugin answers it with one private hint)",
                Pending == null && Told.Count == 0 && NoteFor.Count == 0);
            Tell("[U:1:1001]", "only Alice"); Announce("everybody");
            var h = new HudState();
            PutNotices(h, Now());
            Check("Tell is a note for that player alone, Announce the news line for all; both carry their seconds left",
                h.Notes.Keys.SequenceEqual([1001L]) && h.Notes[1001].Text == "only Alice" && h.Notes[1001].For is > 0 and <= (int)NoteSeconds && h.News == "everybody" && h.NewsFor > 0);
            PutNotices(h = new HudState(), Now().AddSeconds(NoteSeconds + 1));
            Check("...and both are gone once their time is up", h.Notes.Count == 0 && h.News == "" && NoteFor.Count == 0);
        }
        finally { S = savedS; Pending = savedPending; ClearNotices(); }

        // ---- the search index file (search.json beside maps.json), read again whenever it changes ----
        var tmp = Path.Combine(Path.GetTempPath(), $"gg-search-{Environment.ProcessId}.json");
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Dictionary<string, string> { ["3084291314"] = "AIM Map: classic deathmatch", ["123"] = "Claim the hill" }, Ctx.Default.DictionaryStringString));
            var said = ReloadSearch(tmp);
            Check("search.json loads: an object of map key -> text, served on /search as the plugin reads it",
                said.Contains("2 maps indexed") && Search.Count == 2 && HudSearch.Decode(SearchText) is { Count: 2 } served && served.TextOf("3084291314") == "aim map classic deathmatch");
            var version = Search.Version;
            File.WriteAllText(tmp, "[not an object]");
            Check("A search.json that is not an object keeps the index it had", ReloadSearch(tmp).Contains("kept") && Search.Count == 2 && Search.Version == version);
            File.WriteAllText(tmp, """{"1":"x"}""");
            Check("A new one moves the version, so the plugin fetches it again", ReloadSearch(tmp).Contains("1 maps") && Search.Version > version);
        }
        finally { File.Delete(tmp); Search = new(); SearchText = new HudSearch().Encode(); }

        // ---- the committed index itself (search-index.py), when the selftest runs where it is ----
        if (File.Exists("search.json") && Maps.Count > 0)
        {
            try
            {
                ReloadSearch("search.json");
                var unindexed = Maps.Where(m => Search.TextOf(Key(m)) == null).Select(m => m.Rank).ToList();
                var probe = new HudBrowser { Query = "wingman" };
                var listed = new HudCatalog();
                foreach (var m in Maps) listed.Maps.Add(new HudCatalog.Entry(Key(m), ArgOf(m), m.Rank, m.Id, m.Name, m.SizeMb, false, -1, 0, 0, 0));
                var byText = probe.Matches(listed, 0, Search).Count(m => !HudSearch.Normal(m.Name).Contains("wingman"));
                Check($"search.json indexes every listed map ({Search.Count} of {Maps.Count}{(unindexed.Count > 0 ? ", missing ranks " + string.Join(",", unindexed.Take(5)) : "")}), and a word finds maps by their tags, not only by name ({byText} for 'wingman')",
                    unindexed.Count == 0 && byText > 0);
            }
            finally { Search = new(); SearchText = new HudSearch().Encode(); }
        }
        else Console.WriteLine("skip the committed search.json check: no search.json or no maps beside the selftest");
    }

    /// <summary>The chat commands with no place in the menu at all: what the menu shows by itself, or what was never a player's job.</summary>
    static readonly string[] ChatOnlyCommands = ["!help", "!maps", "!queue", "!q", "!top", "!find aim", "!gg", "!info", "!nades on", "!rcon quit"];
}
