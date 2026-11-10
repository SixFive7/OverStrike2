// --selftest, part six: the menu that replaced the chat commands. The search index and its scoring, the search hint (no on-screen keyboard since release 196),
// the card for a Workshop id that is not on the list, the news line and the notes, the board's buttons - and, command by command, the
// place in the menu that does what the command did.
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
        // search.json's texts are lowered by core's rule (SearchText: Python's lower()), which makes İ an i with a combining dot above and a capital
        // sigma at a word's end a final sigma; .NET's lower case keeps İ, so "FUN GAME AİM" was found by no typed word
        var turkish = new HudSearch();
        turkish.Set("5", OverStrike2.Core.SearchText.Of("FUN GAME AİM", "5", ["Classic"]));
        turkish.Set("6", OverStrike2.Core.SearchText.Of("ΟΔΥΣΣΕΑΣ", "6", []));
        Check("Search: names, search.json's texts and typed words are lowered by search.json's own rule - \"aim\" finds FUN GAME AİM by its name and by its text, the start of a Greek word typed in capitals finds the word",
            HudSearch.Normal("FUN GAME AİM") == "fun game aim" && turkish.TextOf("5") == "fun game aim 5 classic" && turkish.Score("5", "FUN GAME AİM", HudSearch.Words("aim")) >= 100
            && turkish.Score("5", "", HudSearch.Words("AİM")) > 0 && HudSearch.Words("ΟΔΥΣ") is ["οδυσ"] && turkish.Score("6", "ΟΔΥΣΣΕΑΣ", HudSearch.Words("ΟΔΥΣ")) >= 100
            && turkish.Score("6", "", HudSearch.Words("οδυσσεασ")) > 0);

        // ---- the search hint (release 196): no on-screen keyboard - typing is chat's (!s); v4.8.0's search box and keys only ask how ----
        Check("Search hint: its click, and v4.8.0's search box and its forty keys, ask how to search - nothing else does",
            HudBrowser.AsksHowToSearch(HudBrowser.HintButton) && HudBrowser.AsksHowToSearch(HudBrowser.OldSearchBox)
            && Enumerable.Range('0', 10).Concat(Enumerable.Range('a', 26)).All(ch => HudBrowser.AsksHowToSearch($"os_kb_{(char)ch}")) && new[] { "os_kb_space", "os_kb_back", "os_kb_clear", "os_kb_done" }.All(HudBrowser.AsksHowToSearch)
            && !HudBrowser.AsksHowToSearch(HudBrowser.ClearButton) && !HudBrowser.AsksHowToSearch("os_m1_play") && !HudBrowser.AsksHowToSearch("os_q_search"));
        var asked = new HudBrowser { Query = "lake", Page = 2 };
        Check("...and those clicks change nothing in the browser: no query typed, no page moved, nothing to open",
            new[] { HudBrowser.HintButton, HudBrowser.OldSearchBox, "os_kb_a", "os_kb_done" }.All(id => !asked.Click(id, 40)) && asked.Query == "lake" && asked.Page == 2);
        Check("...the answer, on the window's line and in chat, says how: chat is the search's text input - press Y, type part of a map name, Enter (233)",
            ChatReply.HowToSearch == "Search with the chat: press Y, type part of a map name, then press Enter - The matching maps show here"
            && M.Plain(ChatReply.HowToSearch).Length <= Messages.LineMax && HudMenu.SearchHint == "Press Y and type a map name");

        // ---- a chat line as the browser's search (233): only while the window is on MAPS > Browse, in a game that shows the HUD ----
        var viewer = new HudPlayer();
        var (noVote, voteOpen, voteDecided) = (new VoteState(), new VoteState { Round = 5, Open = true }, new VoteState { Round = 5, Open = false });
        var maps = HudSlots.Page.Maps;
        var beforeBrowse = HudFrame.BrowserOpen(true, maps, viewer, voteOpen, true);
        viewer.VoteView = (5, 0, true); // the vote's Browse
        Check("A chat line searches only on MAPS > Browse: in the window, on MAPS, in a game that shows the HUD, and not on the ballot - a vote's Browse is the browser, its ballot (a match end's gg) is chat",
            HudFrame.BrowserOpen(true, maps, new HudPlayer(), noVote, true) && !HudFrame.BrowserOpen(false, maps, new HudPlayer(), noVote, true)
            && !HudFrame.BrowserOpen(true, HudSlots.Page.Start, new HudPlayer(), noVote, true) && !HudFrame.BrowserOpen(true, HudSlots.Page.Fun, new HudPlayer(), noVote, true)
            && !HudFrame.BrowserOpen(true, maps, new HudPlayer(), noVote, false) && !beforeBrowse && HudFrame.BrowserOpen(true, maps, viewer, voteOpen, true)
            && !HudFrame.BrowserOpen(true, maps, viewer, voteDecided, true) && !HudFrame.BrowserOpen(true, maps, viewer, new VoteState { Round = 6, Open = true }, true)
            && HudFrame.OnBallot(new HudPlayer(), voteOpen) && !HudFrame.OnBallot(new HudPlayer(), noVote));

        // ---- the search helped (content v4.11.0): its words row - this player's last searches and the words most map names share; a click searches one ----
        var names = System.Text.Json.JsonDocument.Parse(File.ReadAllText("maps.json")).RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString() ?? "").ToList();
        var popularWords = HudSearch.Popular(names, HudBrowser.PopularButtons.Length);
        Check($"The words row's popular words, out of the committed list's {names.Count} names: {string.Join(" ", popularWords)} - aim and awp among them, each of 3 letters or more, no chat word, none that says nothing of a map, no digit but a duel's",
            popularWords.Count == HudBrowser.PopularButtons.Length && popularWords.Contains("aim") && popularWords.Contains("awp") && popularWords.All(w => w.Length >= 3)
            && !popularWords.Any(w => ChatCommands.ChatWords.Contains(w) || w is "map" or "cs2" or "port" or "wip" or "the") && popularWords.All(w => !w.Any(char.IsDigit) || System.Text.RegularExpressions.Regex.IsMatch(w, @"^\d+v\d+$"))
            && HudSearch.Popular(["de_dust2 remake", "aim_map", "Aim Arena", "awp_lego 1v1", "AWP Arena"], 3).SequenceEqual(["aim", "arena", "awp"])
            && HudSearch.Popular(["gg_one", "GG two", "wip map 2"], 3).Count == 2);
        Check("...a click on one is that word: three recent and six popular buttons, nothing else is one of them",
            HudBrowser.WordButton("os_q_r1") == (true, 0) && HudBrowser.WordButton("os_q_r3") == (true, 2) && HudBrowser.WordButton("os_q_p6") == (false, 5)
            && HudBrowser.WordButton("os_q_how") == null && HudBrowser.WordButton("os_q_clear") == null && HudBrowser.WordButton("os_q_r4") == null);
        var kept = HudBrowser.Remember(null, "dust");
        kept = HudBrowser.Remember(kept, "aim");
        kept = HudBrowser.Remember(kept, "Dust!");
        kept = HudBrowser.Remember(kept, "lake");
        kept = HudBrowser.Remember(kept, "office");
        Check("...a player's recent searches: the newest first, the same words once whatever their case, at most three",
            kept.SequenceEqual(["office", "lake", "Dust!"]) && HudBrowser.Remember(["a", "b", "c"], "b").SequenceEqual(["b", "a", "c"]));
        var (rt, rc) = HudMenu.RecentWords(["a long search for maps of glass and lakes", "dust"]);
        var (pt, pc) = HudMenu.PopularWords(["aim", "awp"]);
        Check("...the row: a word on each button that has one and used on it, the rest empty and not used; qrecent while this player has searched",
            rt.Single(x => x.Id == "os_q_r1_text").Text.Length <= HudBrowser.WordShown && rt.Single(x => x.Id == "os_q_r2_text").Text == "dust" && rt.Single(x => x.Id == "os_q_r3_text").Text == ""
            && rc.Contains(("os_q_r2", "used", true)) && rc.Contains(("os_q_r3", "used", false)) && rc.Contains((HudView.Screen, "qrecent", true))
            && HudMenu.RecentWords([]).Classes.Contains((HudView.Screen, "qrecent", false)) && pt.Single(x => x.Id == "os_q_p2_text").Text == "awp" && pc.Contains(("os_q_p3", "used", false)));

        // ---- content v4.12.0: Wingman's player count on a card, and a countdown's bar named for where it is going ----
        var wingmanCard = new HudCatalog.Entry("3600000002", "17", 17, 3600000002, "wm_yard", 30, false, -1, 0, 0, 0, RankWm: 3, Side: 3);
        Check("A Wingman card says its player count - its spawn points as a side's players - only where it plays in Wingman and the sync knows its counts",
            HudMenu.SideText(wingmanCard, Modes.WingmanId) == "3v3" && HudMenu.SideText(wingmanCard, Modes.ArmsRaceId) == ""
            && HudMenu.SideText(wingmanCard with { Side = 0 }, Modes.WingmanId) == "");
        Check("A countdown's bar names where it is going: the share left a second from now - full only before it starts, empty in its last second",
            HudVote.CountdownStep(10, 10) == "w9" && HudVote.CountdownStep(1, 10) == "w0" && HudVote.CountdownStep(0, 10) == "w0" && HudVote.CountdownStep(5, 5) == "w8"
            && HudVote.CountdownStep(1, 5) == "w0" && HudVote.CountdownStep(8, 8) == "w9" && HudVote.CountdownStep(3, 0) == "w10"
            && HudVote.CountdownClasses("os_set_restart", 6, 10).Count(c => c.On) == 1 && HudVote.CountdownClasses("os_set_restart", 6, 10).Single(c => c.On).Class == "w5");

        // ---- the map browser with full-text search ----
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
        b.Find("floors");
        Check("Search in the browser: !find's words are the query, and they find by the text", b.Query == "floors" && b.Page == 0 && b.Matches(catalog, 1, index) is [{ Name: "Fy_glass" }]);
        b.Page = 3; b.Find("floor");
        Check("...a new search starts from the first page", b.Page == 0 && b.Query == "floor");
        b.Query = "aim";
        Check("A search finds by description too, best match first under the default sort: AIM Map before Claim the hill",
            b.Matches(catalog, 1, index).Select(m => m.Name).SequenceEqual(["AIM Map", "Claim the hill"]));
        var withAim = new HudCatalog { Version = 2, Maps = [.. catalog.Maps, new("3600000001", "16", 16, 3600000001, "FUN GAME AİM", 12, false, -1, 0, 0, 0)] };
        index.Set("3600000001", OverStrike2.Core.SearchText.Of("FUN GAME AİM", "3600000001", []));
        var typing = new HudBrowser();
        typing.Find("aim");
        Check("!s aim finds FUN GAME AİM with the other aim maps: its name and its text are lowered as search.json's are",
            typing.Matches(withAim, 1, index).Select(m => m.Name).Order(StringComparer.Ordinal).SequenceEqual(["AIM Map", "Claim the hill", "FUN GAME AİM"]));
        b.Query = "1015";
        Check("A number is also a rank", b.Matches(catalog, 1, index) is [{ Rank: 1015 }]);
        b.Query = "3681294731";
        Check("...and a Workshop id on the list is that map, no card", b.Matches(catalog, 1, index) is [{ Id: 3681294731 }] && b.WorkshopQuery(catalog) == null);
        b.Query = "3232901690";
        var (wt, wc) = HudMenu.Maps(catalog, new HudState(), b, 1, DateTime.UtcNow, index);
        Check("A Workshop id not on the list is the card's alone: no map cards under it (they ran off the page), no empty-page message",
            b.WorkshopQuery(catalog) == 3232901690 && b.Matches(catalog, 1, index).Count == 0 && !wc.Single(c => c.Id == "os_m_empty").On && wt.Single(t => t.Id == "os_m_empty").Text == "");
        var settle = new WorkshopCard();
        var typedAt = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        settle.Show(3232901, typedAt); settle.Show(3232901, typedAt.AddSeconds(5));
        Check($"The card remembers when its id was typed (the plugin asks the Workshop only {WorkshopCard.Settle.TotalSeconds} s later): the same id again keeps that time, a longer one starts again",
            settle.Since == typedAt && WorkshopCard.Settle > TimeSpan.Zero && Retyped(settle, typedAt.AddSeconds(6)) == typedAt.AddSeconds(6));
        static DateTime Retyped(WorkshopCard c, DateTime at) { c.Show(32329016, at); return c.Since; }
        b.Query = "glass";
        var (kt, kc) = HudMenu.Maps(catalog, new HudState(), b, 1, DateTime.UtcNow, index);
        var idle = HudMenu.Maps(catalog, new HudState(), new HudBrowser(), 1, DateTime.UtcNow, index);
        Check("The search area shows the query as it is, with no caret and no keyboard's class; with no search on, the hint says to type !s in chat - in v4.8.0's search box too",
            kt.Single(t => t.Id == "os_q_text").Text == "glass" && kt.Single(t => t.Id == "os_q_hint").Text == "" && kc.Contains(("os_q_search", "used", true)) && !kc.Any(c => c.Class == "kbd")
            && idle.Texts.Single(t => t.Id == "os_q_hint").Text == HudMenu.SearchHint && HudMenu.SearchHint.Contains("Press Y", StringComparison.Ordinal)
            && idle.Classes.Contains(("os_q_search", "used", false)));

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
        Check("Card: asking Steam first - nothing to click yet", card.At == WorkshopCard.Step.Checking && card.Texts().Single(t => t.Id == "os_ws_status").Text.Contains("Asking the Workshop")
            && card.Classes().Contains((WorkshopCard.Play, "off", true)) && card.Classes().Contains((WorkshopCard.Panel, "used", true)));
        card.Checked(items[3232901690]);
        Check("...a playable item: its title and size, and one button to download and play it",
            card.At == WorkshopCard.Step.Ready && card.Texts().Single(t => t.Id == "os_ws_title").Text == "Glass War" && card.Texts().Single(t => t.Id == "os_ws_info").Text.Contains("138 MB")
            && card.Texts().Single(t => t.Id == "os_ws_play_text").Text == "Download and play" && card.Classes().Contains((WorkshopCard.Play, "off", false)));
        card.Started(queued: true);
        Check("...another download running: it waits its turn", card.At == WorkshopCard.Step.Waiting && card.Texts().Single(t => t.Id == "os_ws_status").Text.Contains("starts right after"));
        card.Progress(0, steamBusy: true);
        Check("...Steam's once-a-boot cache check first: the card says so instead of a stuck bar",
            card.At == WorkshopCard.Step.Downloading && card.Texts().Single(t => t.Id == "os_ws_status").Text.Contains("15 minutes") && card.Classes().Contains((WorkshopCard.Bar, "p0", true)));
        card.Progress(47, steamBusy: false);
        Check("...then the download, its progress in tenths on the bar", card.Texts().Single(t => t.Id == "os_ws_status").Text.Contains("47%") && card.Classes().Contains((WorkshopCard.Bar, "p4", true))
            && card.Classes().Count(c => c.Id == WorkshopCard.Bar && c.On) == 1);
        card.Progress(100, steamBusy: false);
        Check("...never 100% before it is really installed", card.Percent == 99);
        card.Installed(); card.Requested();
        Check("...installed, then asked for: a full green bar and nothing more to click",
            card.At == WorkshopCard.Step.Requested && card.Classes().Contains((WorkshopCard.Bar, "p10", true)) && card.Classes().Contains((WorkshopCard.Play, "off", true)));
        var refused = new WorkshopCard();
        refused.Show(222); refused.Checked(items[222]);
        Check("A private item is refused with its reason, and its card has no button", refused.At == WorkshopCard.Step.Refused
            && refused.Texts().Single(t => t.Id == "os_ws_status").Text.StartsWith("Not playable: it is not public") && refused.Classes().Contains((WorkshopCard.Play, "off", true)));
        var failed = new WorkshopCard();
        failed.Show(3232901690); failed.Checked(null, "the Workshop did not answer");
        Check("No answer from Steam: the card says so; a failed download can be tried again",
            failed.At == WorkshopCard.Step.Failed && failed.Texts().Single(t => t.Id == "os_ws_status").Text.Contains("did not answer")
            && Retry(items[3232901690]) is { At: WorkshopCard.Step.Failed } again && again.Texts().Single(t => t.Id == "os_ws_play_text").Text == "Try again" && again.Classes().Contains((WorkshopCard.Play, "off", false)));
        static WorkshopCard Retry(WorkshopItem item) { var c = new WorkshopCard(); c.Show(item.Id); c.Checked(item); c.Started(false); c.Failed("Steam said 'Failure'"); return c; }
        card.Show(null);
        Check("Clearing the query takes the card away", card.At == WorkshopCard.Step.None && card.Classes().Contains((WorkshopCard.Panel, "used", false)));

        // ---- the server's own refusals on the card: non-maps, blocked maps, unlisted maps gone from the Workshop ----
        var cat = new HudCatalog { Version = 5 };
        cat.Refused[3076804331] = ("glassbox", "kicked every player off the server (2026-10-20) - It is blocked");
        cat.Refused[3369214545] = ("", "was removed from the Workshop - Nobody can download it");
        var catBack = HudCatalog.Decode(cat.Encode())!;
        Check("Catalog: the ids the server refuses survive the codec with their name and reason; a line without a reason is dropped",
            catBack.Refused.Count == 2 && catBack.Refused[3076804331] == ("glassbox", "kicked every player off the server (2026-10-20) - It is blocked")
            && catBack.Refused[3369214545].Name == "" && HudCatalog.Decode(HudCatalog.Magic + "\nn\t42\tx\t\n")!.Refused.Count == 0);
        var blockedCard = new WorkshopCard();
        blockedCard.Show(3076804331);
        blockedCard.Refuse("glassbox", catBack.Refused[3076804331].Why);
        Check("Card: a blocked id is refused at once in the server's words, under its own name, with no button and no Workshop question",
            blockedCard.At == WorkshopCard.Step.Refused && blockedCard.Item == null && blockedCard.Texts().Single(t => t.Id == "os_ws_title").Text == "glassbox"
            && blockedCard.Texts().Single(t => t.Id == "os_ws_status").Text == "Not playable: it kicked every player off the server (2026-10-20) - It is blocked."
            && blockedCard.Classes().Contains((WorkshopCard.Play, "off", true)));
        blockedCard.Checked(items[3232901690]);
        Check("...and a late Workshop answer does not undo the refusal", blockedCard.At == WorkshopCard.Step.Refused);
        // our own HUD's item: no list of the bridge's names it (the sync never tests it) and the Workshop calls it a CS2 item like any map
        const string hudItem = "3900000777"; // made up: the plugin takes the real one from the settings (hud.addon)
        var refusing = new HudCatalog();
        refusing.Refused[3369214545] = ("CS2 Aim Map", NotAMap);
        Check("The server's own refusal, before the Workshop is asked: the catalog's, else our own HUD's item (the settings' hud.addon) as no map - also with no catalog yet; nothing for a map, nor without a HUD item",
            WorkshopCard.ServerRefusal(3369214545, refusing, hudItem) == ("CS2 Aim Map", NotAMap) && WorkshopCard.ServerRefusal(3900000777, refusing, hudItem) == (WorkshopCard.HudItemName, NotAMap)
            && WorkshopCard.ServerRefusal(3900000777, null, hudItem) == (WorkshopCard.HudItemName, NotAMap) && WorkshopCard.ServerRefusal(3232901690, refusing, hudItem) == null
            && WorkshopCard.ServerRefusal(3900000777, refusing, "") == null && WorkshopCard.ServerRefusal(3900000778, null, hudItem) == null);
        var hudCard = new WorkshopCard();
        hudCard.Show(3900000777);
        if (WorkshopCard.ServerRefusal(hudCard.Id, refusing, hudItem) is { } own) hudCard.Refuse(own.Name, own.Why);
        hudCard.Checked(items[3232901690]); // were the Workshop asked after all: a CS2 item it calls playable
        Check("...so the card for the HUD's own item says it is no map: no Download and play, whatever the Workshop says",
            hudCard.At == WorkshopCard.Step.Refused && hudCard.Texts().Single(t => t.Id == "os_ws_title").Text == "Over Strike 2 HUD"
            && hudCard.Texts().Single(t => t.Id == "os_ws_status").Text == "Not playable: it isn't a playable map - It can't be loaded."
            && hudCard.Texts().Single(t => t.Id == "os_ws_play_text").Text == "" && hudCard.Classes().Contains((WorkshopCard.Play, "off", true)));
        if (Maps.FirstOrDefault(m => !m.Official) is not { } listedGone) Console.WriteLine("skip the catalog's refusals: no maps loaded");
        else
        {
            var (nonMaps, blocked, removed) = (NonMaps, Blocked, Removed);
            try
            {
                NonMaps = new Dictionary<string, string> { ["3369214545"] = "CS2 Aim Map" };
                Blocked = new() { ["3076804331"] = ("glassbox", "glassboxfinal", "kicked every player off the server (2026-10-20)"), ["123456789"] = ("x", "", "is on the blocked list") };
                Removed = new Dictionary<string, string> { ["987654321"] = Workshop.Removed, [listedGone.Id.ToString()] = Workshop.Removed };
                var built = BuildCatalog();
                Check("Bridge: the catalog carries every non-map, blocked id and unlisted gone id the server refuses, each in the card's words - a listed gone map stays a card on the list",
                    built.Refused[3369214545] == ("CS2 Aim Map", NotAMap) && built.Refused[3076804331] == ("glassbox", "kicked every player off the server (2026-10-20) - It is blocked")
                    && built.Refused[123456789].Why == "is on the blocked list" && built.Refused[987654321].Why.EndsWith("Nobody can download it")
                    && !built.Refused.ContainsKey(listedGone.Id) && built.Maps.Single(m => m.Id == listedGone.Id).Removed);
            }
            finally { (NonMaps, Blocked, Removed) = (nonMaps, blocked, removed); }
        }

        // ---- the live state's new lines: the notes, the previous map, the search index's version ----
        var st = new HudState { PrevArg = "12", PrevLabel = "#12 aim_redline", SearchVersion = 77 };
        st.Notes[1001] = ("You voted already - 1/2, 1 more needed", 5);
        st.Notes[1002] = ("expired", 0);
        var back2 = HudState.Decode(st.Encode())!;
        Check("State: the notes per player, the previous map and the search version survive the codec; an expired note is not sent; an older bridge's news line is read as nothing",
            HudState.Decode(st.Encode() + "news\t6\tan older bridge's line\nnewsat\t1793000000\tand its end time\n") is { } older && older.Notes.Count == 1 && back2.Notes.Count == 1 && back2.Notes[1001] == ("You voted already - 1/2, 1 more needed", 5)
            && (back2.PrevArg, back2.PrevLabel) == ("12", "#12 aim_redline") && back2.SearchVersion == 77);
        Check("...the note only its player sees: on their screen, nobody else's - the window's line",
            HudView.NoteOf(back2, 1001) != "" && HudView.NoteOf(back2, 1003) == "" && HudView.PlayerClasses(back2, 1001).Contains((HudView.Screen, "wl", true))
            && HudView.PlayerClasses(back2, 1003).Contains((HudView.Screen, "wl", false)) && !HudView.Classes(back2).Any(x => x.Class == "news"));

        // ---- the board: a click action panel ----
        var free = new HudState { Map = "#5 x" };
        var open = VoteState.Decode("mv\t1793900000\topen\t30\t2\t0\tmid\t2\t1\n");
        var waiting = new VoteState { WaitFor = 80 };
        Check("Board: Like and Dislike are likes on the map that is on; the button under them is Start a vote - the vote's window while one is on, the answer with the time left during the wait, nothing during a change",
            HudView.Command(HudView.BoardUp, free) == "!up" && HudView.Command(HudView.BoardDown, free) == "!down"
            && HudView.Command(HudView.BoardNext, free) == "!next" && HudView.Command(HudView.BoardNext, free, open) == HudView.OpenVote
            && HudView.Command(HudView.BoardNext, free, waiting) == "!next" && HudView.Command(HudView.BoardNext, new HudState { ChangeIn = 3 }) == null);
        Check("Board: the button's words in each case - start one, the vote on, the wait after one - and none of them a typed command",
            HudView.PlayerTexts(free, 2, "Bob", false).Single(t => t.Id == "os_b_next_text").Text == "Start a vote"
            && HudView.PlayerTexts(free, 2, "Bob", false, open).Single(t => t.Id == "os_b_next_text").Text.Contains("The vote is on")
            && HudView.PlayerTexts(free, 2, "Bob", false, waiting).Single(t => t.Id == "os_b_next_sub").Text == "The next one can start in 1 min 20 s"
            && HudView.PlayerClasses(free, 2, waiting).Contains((HudView.Screen, "act-off", true)) && HudView.PlayerClasses(free, 2).Contains((HudView.Screen, "act-off", false))
            && new VoteState?[] { null, open, waiting }.Select(v => HudView.Action(free, v)).All(a => !a.Text.Contains('!') && !a.Sub.Contains('!')));
        var leading = VoteState.Decode("mv\t1793900001\topen\t20\t3\t3\tmid\t2\t1\nmvo\t2\tarmsrace\tpicked\t44\t44\t#44 aim_aim\t\t2\tAlice\nmvo\t1\tarmsrace\tkeep\t\tk\tKeep playing this map\t#5 x\t1\t\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t0\t\n");
        var lt = HudView.Texts(free, leading);
        var lc = HudView.Classes(free, leading);
        string LT(string id) => lt.Single(t => t.Id == id).Text;
        Check("Board and live column: the vote in one line, its leading cards in the rows below it; outside a vote how to call one, or the wait",
            HudView.VoteLine(null) == "No vote on: Play on a map, or Start a vote" && HudView.VoteLine(leading) == "The vote is on: 20 s left  ·  3 of 3 voted"
            && HudView.VoteLine(waiting) == "The next vote can start in 1 min 20 s" && LT("os_b_queue") == HudView.VoteLine(leading)
            && LT("os_w_n1") == "#44 aim_aim" && LT("os_w_nw1") == "2 votes" && LT("os_w_n2") == "Keep playing this map" && LT("os_w_nw2") == "1 vote" && LT("os_w_n3") == ""
            && lc.Contains(("os_w_row_n2", "used", true)) && lc.Contains(("os_w_row_n3", "used", false)) && lc.Contains(("os_w_nempty", "used", false))
            && HudView.Classes(free).Contains(("os_w_nempty", "used", true)) && HudView.VoteLine(leading).Length <= HudView.VoteLineMax);

        // ---- every chat command, and where the menu has it now (the README's checklist says the same) ----
        var live = new HudState { Map = "#1 AIM Map", Key = "3084291314", PrevArg = "7", PrevLabel = "#7 Claim the hill" };
        var mine = new HudBrowser();
        var page = mine.PageOf(mine.Matches(catalog, 1, index));
        string? Card(int k, string part) => HudMenu.Command($"os_m{k}_{part}", catalog, live, mine, 1, DateTime.UtcNow, index);
        var claim = page.FindIndex(m => m.Name == "Claim the hill") + 1;
        var rows = new (string Chat, string Where, string? Sends, string Expected)[]
        {
            ("!map <number|name|id>", "Maps: a card's Play, to the vote", Card(claim, "play"), "!map 7"),
            ("!map <unlisted Workshop id>", "Chat: the id, or a link pasted - the item's card", WorkshopCard.IdOf("3232901690")?.ToString(), "3232901690"),
            ("!next", "Board or menu: Start a vote", HudView.Command(HudView.BoardNext, new HudState { Map = "x" }), "!next"),
            ("!previous !prev !back", "Previous", HudMenu.Command(HudMenu.Previous, catalog, live, mine, 1, DateTime.UtcNow), "!previous"),
            ("!find <words>", "Maps: the search hint - typing is chat's", HudBrowser.AsksHowToSearch(HudBrowser.HintButton) ? HudBrowser.HintButton : null, "os_q_how"),
            ("!up", "Board or menu: Like", HudView.Command(HudView.BoardUp, live), "!up"),
            ("!down", "Board or menu: Dislike", HudView.Command(HudView.ButtonDown, live), "!down"),
            ("!up <map>", "Maps: a card's up arrow", Card(claim, "up"), "!up 7"),
            ("!down <map>", "Maps: a card's down arrow", Card(claim, "down"), "!down 7"),
            ("!down <map> big", "Maps: a card's down arrow, then Too big in the dialog", Card(claim, "down") == "!down 7"
                && HudVote.WhyClick(HudVote.WhyButton(Array.FindIndex(DownReasons.All, r => r.Word == "big") + 1)) is { } bigWhy ? $"!down {bigWhy.Word}" : null, "!down big"),
            ("!top", "Maps: Sort by Likes; the live column's Most liked", Sorted(catalog, index).FirstOrDefault()?.Name, "Claim the hill"),
            ("!time 10|60", "Game: Match length", HudMenu.Command(HudMenu.Time10, catalog, live, mine, 1, DateTime.UtcNow) + " " + HudMenu.Command(HudMenu.Time60, catalog, live, mine, 1, DateTime.UtcNow), "!time 10 !time 60"),
            ("!restart", "Game: Restart the server (twice)", HudMenu.Command(HudMenu.Restart, catalog, live, mine, 1, DateTime.UtcNow), "!restart"),
            ("!rematch", "Game: Restart the match (twice)", HudMenu.Command(HudMenu.Rematch, catalog, live, mine, 1, DateTime.UtcNow), "!rematch"),
            ("!nades on|off", "Fun: Grenade drops (a fun setting)", HudMenu.FunClick(HudMenu.FunButton(17, 1)) is ({ Id: "drops" }, { Id: "on" }) && HudMenu.FunClick(HudMenu.FunButton(17, 2)) is ({ Id: "drops" }, { Id: "off" }) ? "os_f17_1 os_f17_2" : null, "os_f17_1 os_f17_2"),
            ("!gg !info", "Board: Open full menu; TAB + E", HudView.BoardOpen, "os_b_open"),
            ("!help !maps", "Help page", HudView.Tabs.First(t => t.Page == HudSlots.Page.Help).Id, "os_tab_help"),
        };
        static List<HudCatalog.Entry> Sorted(HudCatalog c, HudSearch i) { var v = new HudBrowser(); v.Click("os_sort_votes", 0); return v.Matches(c, 1, i); }
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
            Check("A !map or !next typed in chat does nothing at the bridge: no change, no note, no news (chat commands are the plugin's)",
                Pending == null && Told.Count == 0 && NoteFor.Count == 0);
            Tell("[U:1:1001]", "only Alice"); Announce("Everybody");
            var h = new HudState();
            PutNotices(h, Now());
            Check("Tell is a note for that player alone, Announce the top line for all; both carry their end",
                h.Notes.Keys.SequenceEqual([1001L]) && h.Notes[1001].Text == "only Alice" && h.Notes[1001].For is > 0 and <= (int)Messages.DefaultSeconds && h.TopText == "Everybody" && h.TopUntilMs > 0);
            PutNotices(h = new HudState(), Now().AddSeconds(Messages.DefaultSeconds + 1));
            Check("...and both are gone once their time is up", h.Notes.Count == 0 && h.TopText == "" && NoteFor.Count == 0);
        }
        finally { S = savedS; Pending = savedPending; ClearNotices(); }

        // ---- the search index file (search.json beside maps.json), read again whenever it changes ----
        var tmp = Path.Combine(Path.GetTempPath(), $"os-search-{Environment.ProcessId}.json");
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

        // ---- the committed index's writer and checker (SearchIndexFile.cs, --search-index): the file as Python wrote it, byte for byte; a new
        // map's text by core's SearchText, whose rule core's self-test holds and the sync's runs every code point through, the sync's way and this ----
        const char DotAbove = (char)0x307, UnitSeparator = (char)0x1F;
        Check("--search-index: a new map's text is its name's words in lower case, its id, then its tags' words - with Python's lower() and split()",
            SearchIndexFile.TextOf("FUN GAME AİM", "7", ["Classic", $"Arms{UnitSeparator}Race", "  "]) == $"fun game ai{DotAbove}m 7 classic arms race");
        Check("--search-index: a known map keeps its text, an empty one is made again, a map that left loses its line - in maps.json's order",
            SearchIndexFile.Build([("1", "One"), ("2", "Two"), ("3", null)], [("2", "kept 2"), ("3", "three 3"), ("9", "gone 9"), ("1", "")],
                new Dictionary<string, List<string>> { ["1"] = ["Arms Race"] }) is [("1", "one 1 arms race"), ("2", "kept 2"), ("3", "three 3")]);
        Check("--search-index --check: a missing text, a stray one, the order, a text without its own id, one over 300 code points",
            SearchIndexFile.Problems(["1", "2"], [("1", "a 1")]) is ["1 map(s) of maps.json have no text, e.g. ['2']"]
            && SearchIndexFile.Problems(["1"], [("1", "a 1"), ("9", "b 9")]) is ["1 text(s) for maps not on maps.json, e.g. ['9']"]
            && SearchIndexFile.Problems(["1", "2"], [("2", "b 2"), ("1", "a 1")]) is ["the texts are not in maps.json's order"]
            && SearchIndexFile.Problems(["1", "2"], [("1", "a"), ("2", "2 " + new string('y', 299))]) is [var noId, var tooLong] && noId.Contains("without their own") && tooLong.Contains("over 300")
            && SearchIndexFile.Problems(["1"], [("1", "1 " + string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F329), 298)))]) is []);
        Check("--search-index writes the file as Python's json.dumps(indent=0, ensure_ascii=False): a map a line, only quotes, backslashes and controls escaped",
            SearchIndexFile.Encode([("1", $"a\"b\\c{(char)1}{(char)0x7F}é"), ("2", "x 2")]) == $"{{\n\"1\": \"a\\\"b\\\\c\\u0001{(char)0x7F}é\",\n\"2\": \"x 2\"\n}}\n"
            && SearchIndexFile.Encode([]) == "{}\n");

        // ---- the committed index itself (--search-index keeps it in step), when the selftest runs where it is ----
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
