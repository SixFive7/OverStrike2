// --selftest, Over Strike 2's part: why a map is broken on the card and in the confirm, the maps that crash players' games marked by themselves,
// our maps (ours.json), the catalog's two newer fields, the chat commands, the vote on the cards and the version classes.
static partial class Program
{
    static void Os2SelfTest(Action<string, bool> Check)
    {
        // ---- the reasons ----
        const string brokenJson = """{"maps":[{"id":1,"crashes":3},{"id":2,"crashes":0,"reason":"advertises another server"},{"id":3,"crashes":0},{"name":"ar_x","reason":"x"},{"id":4,"reason":""},5]}""";
        var reasons = ParseReasons(brokenJson);
        Check("Reasons: broken.json's reason per map, keyed like the list; none for an entry without one, an empty one or a bare id",
            reasons.Count == 2 && reasons["2"] == "advertises another server" && reasons["ar_x"] == "x" && !reasons.ContainsKey("1") && !reasons.ContainsKey("4")
            && ParseReasons("[1,2]").Count == 0 && ParseReasons("{}").Count == 0);

        var (sBefore, brokenBefore, reasonsBefore, dirBefore) = (S, Broken, BrokenReasons, StateDir);
        StateDir = Path.Combine(Path.GetTempPath(), $"os2-selftest-{Environment.ProcessId}");
        Directory.CreateDirectory(StateDir);
        S = new State();
        Broken = ParseBroken(brokenJson);
        BrokenReasons = reasons;
        MapInfo M(long id) => new(100 + (int)id, id, $"map {id}", 10);
        var crasher = PickRecord(M(1));
        var ads = PickRecord(M(2));
        var bare = PickRecord(M(3));
        Check("The confirm (#11): a crasher says it may restart the server; a map broken some other way says why and never that it may restart the server",
            crasher == "crashed 3 of 3 test loads and may restart the server" && ads == "is broken: advertises another server" && bare == "is on the broken list"
            && !ads!.Contains("restart") && !bare!.Contains("restart") && PickRecord(M(9)) == null);
        var longest = PickConfirmNote(new MapInfo(1, 1, new string('w', 60), 1), $"is broken: {new string('r', ReasonMax)}");
        Check($"The confirm note with the longest label and the longest reason fits the HUD's note line ({longest.Length} of {NoticeLength})", longest.Length <= NoticeLength);

        // ---- #6: three players timing out within 60 s of entering the map mark it ----
        var on = Maps.Count > 0 ? Maps[^1] : M(7); // a listed map, so the catalog carries its mark
        S.Map = on;
        Pending = new Change(M(8), "test"); // a change is under way, so the mark only announces (no dice in this test)
        var t = new DateTime(2026, 11, 1, 1, 0, 0, DateTimeKind.Utc);
        PlayerCrashMapStarted();
        foreach (var id in new[] { "a", "b", "c", "d" }) PlayerEnteredMap(id, t);
        PlayerLeftMap("a", "NETWORK_DISCONNECT_TIMEDOUT", t.AddSeconds(21));
        PlayerLeftMap("b", "NETWORK_DISCONNECT_DISCONNECT_BY_USER", t.AddSeconds(22)); // leaving is no crash
        PlayerLeftMap("c", "NETWORK_DISCONNECT_TIMEDOUT", t.AddSeconds(23));
        var afterTwo = S.PlayerCrashes.Count;
        PlayerLeftMap("d", "NETWORK_DISCONNECT_TIMEDOUT", t.AddSeconds(90));              // too late: 90 s after entering
        var lateNone = S.PlayerCrashes.Count;
        PlayerEnteredMap("e", t.AddSeconds(30));
        PlayerLeftMap("e", "NETWORK_DISCONNECT_TIMEDOUT", t.AddSeconds(55));
        Check("Player crashes (#6): two timeouts, a leave and a late timeout mark nothing; the third timeout within 60 s of entering marks the map, with its reason",
            afterTwo == 0 && lateNone == 0 && S.PlayerCrashes.TryGetValue(Key(on), out var why) && why == "crashed 3 players' games within 60 s"
            && PickRecord(on) == $"is broken: {why}" && ReasonOf(Key(on)) == why);
        PlayerCrashMapStarted();
        PlayerEnteredMap("x", t);
        PlayerLeftMap("x", "NETWORK_DISCONNECT_SHUTDOWN", t.AddSeconds(5));
        Check("Player crashes (#6): a map change's reconnect is no timeout; a map start forgets who entered", !IsTimeout("NETWORK_DISCONNECT_SHUTDOWN") && IsTimeout("NETWORK_DISCONNECT_TIMEDOUT"));
        var catalogOne = BuildCatalog().Maps.FirstOrDefault(m => m.Id == on.Id);
        Pending = null;

        // ---- our maps ----
        Check("Our maps: ours.json's ids out of objects or a bare array; anything else is skipped",
            ParseOurs("""{"maps":[{"id":3831189574,"name":"a"},{"id":"3831231522"},{"name":"x"},-4]}""") is { Count: 2 } o && o.Contains(3831189574) && o.Contains(3831231522)
            && ParseOurs("[1, 2, 2]").Count == 2 && ParseOurs("{}").Count == 0);
        if (File.Exists("ours.json"))
        {
            var mine = ParseOurs(File.ReadAllText("ours.json"));
            Check($"Our maps: the committed ours.json names {mine.Count} maps, every one on maps.json (the three of 2026-11-01 among them)",
                mine.Count >= 3 && mine.All(id => Maps.Any(m => m.Id == id)) && new long[] { 3831189574, 3831231522, 3831242232 }.All(mine.Contains));
        }

        // ---- the catalog's fields 19 (ours) and 20 (the reason), and what the card says ----
        var cat = new HudCatalog { Version = 3 };
        cat.Maps.Add(new HudCatalog.Entry("2", "102", 102, 2, "map 2", 10, false, 0, 0, 0, 0, Ours: true, Reason: "advertises another server"));
        var back = HudCatalog.Decode(cat.Encode())!.Maps.Single();
        var old18 = HudCatalog.Decode(string.Join('\t', cat.Encode().Split('\n')[2].Split('\t').Take(18)).Insert(0, HudCatalog.Magic + "\n"))!.Maps.Single();
        var page = HudMenu.Maps(cat, new HudState(), new HudBrowser(), 1, t);
        Check("Catalog: field 19 says ours and field 20 the reason, both read back; a catalog without them reads not ours and no reason",
            back.Ours && back.Reason == "advertises another server" && !old18.Ours && old18.Reason == "");
        Check("The card (#8, #11): a map broken some other way says why on its strip ('Broken: <reason>'), a crasher its crash count, one with no reason 'On the broken list'",
            page.Texts.Single(x => x.Id == "gg_m1_warn").Text == "Broken: advertises another server"
            && catalogOne is { Broken: 0, Reason: var r6 } && r6 == "crashed 3 players' games within 60 s");

        // ---- the chat commands (ChatCommands.cs) ----
        var parsed = ChatCommands.All.Select(c => (c, ChatCommands.Parse(c.Word + (c.Args.StartsWith('<') ? c.Word switch
        {
            "!play" => " 12", "!vote" => " 2", "!nades" => " on", "!catchup" => " on", "!time" => " 10", "!newmaps" => " half", _ => " x",
        } : "")))).ToList();
        Check($"Chat: every one of the {ChatCommands.All.Length} commands parses as itself (none Unknown or a usage line), words unique",
            parsed.All(x => x.Item2.Kind is not (ChatCommands.Kind.Unknown or ChatCommands.Kind.None or ChatCommands.Kind.Usage))
            && ChatCommands.All.Select(c => c.Word).Distinct().Count() == ChatCommands.All.Length);
        Check("Chat: what a command sends the bridge is a click the bridge takes (Hud.IsAction) - !next is the board's Next for the player",
            parsed.Where(x => x.Item2.Kind == ChatCommands.Kind.Bridge && x.Item2.Arg != "next").All(x => Hud.IsAction(x.Item2.Arg))
            && ChatCommands.Parse("!like 12") is { Kind: ChatCommands.Kind.Bridge, Arg: "!up 12" } && Hud.IsAction("!up 12")
            && ChatCommands.Parse("!cancel 7") is { Kind: ChatCommands.Kind.Unknown } && ChatCommands.Parse("!done") is { Kind: ChatCommands.Kind.Bridge, Arg: "next" });
        Check("Chat: a line that is no command is nothing, an unknown word says so, a command missing its argument gets its usage",
            ChatCommands.Parse("hello !play 1") is { Kind: ChatCommands.Kind.None } && ChatCommands.Parse("!") is { Kind: ChatCommands.Kind.None }
            && ChatCommands.Parse("!fly") is { Kind: ChatCommands.Kind.Unknown, Name: "!fly" } && ChatCommands.Parse("!vote 99") is { Kind: ChatCommands.Kind.Usage }
            && ChatCommands.Parse("/PLAY de dust") is { Kind: ChatCommands.Kind.Play, Arg: "de dust" } && ChatCommands.Parse("!play") is { Kind: ChatCommands.Kind.Usage }
            && ChatCommands.Parse("!fun gravity low") is { Kind: ChatCommands.Kind.Fun, Arg: "gravity low" } && ChatCommands.Parse("!fun gravity") is { Kind: ChatCommands.Kind.Usage }
            && ChatCommands.Parse("!fun") is { Kind: ChatCommands.Kind.Page, Arg: "Fun" } && ChatCommands.Parse("!vote 3") is { Kind: ChatCommands.Kind.Vote, Arg: "3" });
        var help = ChatCommands.HelpLines();
        Check($"Chat: !help's list fits chat ({help.Count} lines, the longest {help.Max(l => l.Length)} of {ChatCommands.LineMax}) and names every command; every usage line fits",
            help.All(l => l.Length <= ChatCommands.LineMax) && ChatCommands.All.All(c => help.Any(l => l.Contains(c.Word)))
            && ChatCommands.All.All(c => ChatCommands.UsageOf(c.Word).Length <= ChatCommands.LineMax) && ChatCommands.UnknownLine("!abcdefghijkl").Length <= ChatCommands.LineMax);
        Check("Chat: every page a command opens is a page of the menu", parsed.Where(x => x.Item2.Kind is ChatCommands.Kind.Page or ChatCommands.Kind.Commands)
            .All(x => Enum.TryParse<HudSlots.Page>(x.Item2.Arg, out _)));

        // ---- the vote on the cards (HudVote.cs) and the version check ----
        var ballot = VoteState.Decode("mode\tarmsrace\tArms Race\nmv\t1793500000\topen\t42\t3\t1\tmid\t1001\t1\nmvo\t1\tarmsrace\tpicked\t12\t3084291314\t#12 aim_x\t17 MB\t1\tAlice\nmvo\t2\tdeathmatch\trolled\tde_dust2\tde_dust2\tde_dust2 (official)\t\t0\t\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t0\t\nmvv\t1001\t1\n");
        var cardsNow = HudVote.Cards(ballot, 1001, null);
        string T(string id) => cardsNow.Texts.Single(x => x.Id == id).Text;
        bool On(string id, string cls) => cardsNow.Classes.Single(x => x.Id == id && x.Class == cls).On;
        Check("The vote on the cards: each card its mode (label and colour), votes and who picked it, the player's own vote lit, the picked one tagged, Random on its own button without a number, the cards after the maps empty; everybody frozen",
            ballot is { Freeze: true, MatchEnded: false, Starter: 1001 } && T("gg_m1_name") == "#12 aim_x" && T("gg_m1_votes") == "1 vote  ·  picked by Alice" && T("gg_m2_rank").EndsWith("Deathmatch") && T("gg_m3_name") == ""
            && On("gg_m1", "confirm") && !On("gg_m2", "confirm") && On("gg_m1", "queued") && !On("gg_m2", "queued") && !On("gg_m4", "used") && !On("gg_m3", "used") && T("gg_m1_play_text") == "Your vote"
            && On("gg_m1", "mode-armsrace") && On("gg_m2", "mode-deathmatch") && !On("gg_m2", "mode-armsrace") && Modes.All.All(m => !On("gg_m3", $"mode-{m.Id}"))
            && cardsNow.Vars.Single(x => x.Name == HudVote.RandomVar).Text == "Random  ·  0 votes" && !On(HudView.Screen, "vrandom") && !On(HudView.Screen, "vpages") && !On(HudView.Screen, "vmore")
            && T("gg_m_count").Contains("42 s") && On(HudView.Screen, "voting"));
        Check("The vote's click: a card's Play is !mv <round> <option> while the vote is open, which the bridge takes, Random's button option 99; nothing else is a vote",
            HudVote.Click(ballot, "gg_m2_play") == "!mv 1793500000 2" && MapVote.IsAction("!mv 1793500000 2") && HudVote.Click(ballot, "gg_m5_play") == null && HudVote.Click(ballot, "gg_m3_play") == null
            && HudVote.Click(ballot, HudVote.RandomButton) == $"!mv 1793500000 {MapVote.RandomOption}" && MapVote.IsAction($"!mv 1793500000 {MapVote.RandomOption}")
            && HudVote.Click(ballot, "gg_m1_up") == null && HudVote.Click(new VoteState(), "gg_m1_play") == null);
        var was = MapVote.Numbers;
        MapVote.Numbers = MapVote.Numbering.None;
        var plain = HudVote.Cards(ballot, 1002, null);
        Check("Numbering is one setting - none: a card says Vote and its mode only, chat can type no number, the page line says click",
            was == MapVote.Numbering.None &&
            plain.Texts.Single(x => x.Id == "gg_m2_play_text").Text == "Vote" && plain.Texts.Single(x => x.Id == "gg_m2_rank").Text == "Deathmatch" && ballot.Typed(2) == null && ballot.Highest == 0
            && plain.Texts.Single(x => x.Id == "gg_pg_text").Text == "Click a card's Vote");
        MapVote.Numbers = MapVote.Numbering.Position;
        Check("...by place: the card's place on the ballot, and a typed number names the card in that place",
            ballot.Shown(ballot.Maps[1]) == 2 && ballot.Typed(1)?.N == 1 && ballot.Typed(2)?.N == 2 && ballot.Typed(3) == null && ballot.Highest == 2);
        MapVote.Numbers = MapVote.Numbering.Fixed;
        Check("...fixed: each card its own number, Random none", ballot.Shown(ballot.Maps[1]) == 2 && ballot.Typed(2)?.N == 2 && ballot.Random is { } rnd && ballot.Shown(rnd) == 0 && ballot.Highest == 2);
        MapVote.Numbers = was;
        // ---- a ballot of twelve cards and Random, called mid-match by a player: the most votes first, pages, Keep playing, a map added ----
        var big = new System.Text.StringBuilder("mode\tarmsrace\tArms Race\nmv\t1793600000\topen\t30\t4\t2\tmid\t1002\t1\n");
        big.Append("mvo\t1\tarmsrace\tkeep\t5\t3084291315\tKeep playing this map\t#5 aim_on\t1\t\n");
        big.Append("mvo\t12\tarmsrace\tpicked\t77\t3084291377\t#77 aim_added\t9 MB\t1\tCara\n");
        for (var n = 2; n <= 11; n++) big.Append($"mvo\t{n}\t{(n == 9 ? "deathmatch" : n == 10 ? "wingman" : "armsrace")}\trolled\t{n + 20}\t30842913{n + 20}\t#{n + 20} map_{n}\t12 MB\t0\t\n");
        big.Append("mvo\t99\tarmsrace\trandom\t\t\tRandom\t\t0\t\nmvv\t1001\t12\nmvv\t1002\t1\n");
        var twelve = VoteState.Decode(big.ToString());
        var first = HudVote.Cards(twelve, 1001, null);
        var second = HudVote.Cards(twelve, 1001, null, page: 1);
        string P(List<(string Id, string Text)> t, string id) => t.Single(x => x.Id == id).Text;
        bool C((List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes, List<(string Name, string Text)> Vars) d, string id, string cls) => d.Classes.Single(x => x.Id == id && x.Class == cls).On;
        Check($"Twelve cards: {HudVote.Pages(twelve)} pages of {MapVote.PageCards} - in the order the bridge sends them (the most votes first); the pager and the hint for the next page impossible to miss, the Next button's pulse on",
            twelve is { MatchEnded: false, Starter: 1002 } && twelve.Maps.Count == 12 && HudVote.Pages(twelve) == 2 && P(first.Texts, "gg_m1_rank") == "Keep" && P(first.Texts, "gg_m1_play_text") == "Keep"
            && P(first.Texts, "gg_m1_name") == "Keep playing this map" && P(first.Texts, "gg_m1_size") == "#5 aim_on"
            && P(first.Texts, "gg_m2_name") == "#77 aim_added" && P(first.Texts, "gg_m2_votes").Contains("picked by Cara") && P(first.Texts, "gg_m2_play_text") == "Your vote"
            && P(first.Texts, "gg_m2_who") == "9 MB  ·  picked by Cara" && P(first.Texts, "gg_m2_score") == "1" && P(first.Texts, "gg_m10_name") == "#29 map_9"
            && P(first.Texts, "gg_pg_text") == "Page 1 of 2  ·  12 maps: 2 more on the next page" && C(first, HudView.Screen, "vpages") && C(first, HudView.Screen, "vmore") 
            && first.Vars.Single(x => x.Name == HudVote.MoreVar).Text == "2 more maps on the next page" && C(first, HudBrowser.PrevButton, "off") && !C(first, HudBrowser.NextButton, "off")
            && P(second.Texts, "gg_m2_name") == "#31 map_11" && P(second.Texts, "gg_m3_name") == "" && !C(second, HudView.Screen, "vmore") 
            && C(second, HudBrowser.NextButton, "off") && C(second, "gg_m1", "mode-wingman"));
        Check("...a page's card k is that page's option, the pager moves this player alone, and a card's page follows its votes",
            HudVote.Click(twelve, "gg_m2_play", 1) == "!mv 1793600000 11" && HudVote.Click(twelve, "gg_m2_play", 0) == "!mv 1793600000 12" && HudVote.Click(twelve, "gg_m3_play", 1) == null
            && HudVote.PageClick(twelve, HudBrowser.NextButton, 0) == 1 && HudVote.PageClick(twelve, HudBrowser.NextButton, 1) == 1 && HudVote.PageClick(twelve, HudBrowser.FirstButton, 1) == 0
            && HudVote.PageClick(twelve, HudBrowser.LastButton, 0) == 1 && HudVote.PageClick(twelve, "gg_m1_play", 0) == null && HudVote.PageOf(twelve, 12) == 0 && HudVote.PageOf(twelve, 11) == 1
            && HudVote.Click(twelve, HudVote.RandomButton) == $"!mv 1793600000 {MapVote.RandomOption}" && HudVote.BrowseLine(twelve).Contains("30 s left"));
        // ---- a game known to hold a content before v4_6_0 has eight cards: its pages are of eight, so its pager reaches every option ----
        var old8 = HudVote.CardsFor(HudCheck.Older);
        var oldFirst = HudVote.Cards(twelve, 1001, null, page: 0, perPage: old8);
        var oldSecond = HudVote.Cards(twelve, 1001, null, page: 1, perPage: old8);
        Check($"An older HUD's ballot: pages of {HudBrowser.PerPage} for a game known to hold a content before {Hud.Content} (older, or v4_5_0), pages of {MapVote.PageCards} for this one, a newer one or one not known",
            old8 == HudBrowser.PerPage && HudVote.CardsFor("v4_5_0") == HudBrowser.PerPage && new[] { Hud.Content, "v4_7_0", Hud.Release, "" }.All(x => HudVote.CardsFor(x) == MapVote.PageCards)
            && HudVote.Pages(twelve, old8) == 2 && !C(oldFirst, "gg_m9", "used") && C(oldFirst, "gg_m8", "used") && P(oldFirst.Texts, "gg_m9_name") == ""
            && P(oldFirst.Texts, "gg_pg_text") == "Page 1 of 2  ·  12 maps: 4 more on the next page" && P(oldSecond.Texts, "gg_m1_name") == P(first.Texts, "gg_m9_name")
            && HudVote.Click(twelve, "gg_m1_play", 1, -1, null, old8) == MapVote.Click(twelve.Round, twelve.Maps[old8].N) && HudVote.PageClick(twelve, HudBrowser.LastButton, 0, old8) == 1
            && HudVote.Order(twelve, 1, old8).Length == 12 - old8 && HudVote.PageOf(twelve, twelve.Maps[old8].N, old8) == 1 && HudVote.Thumbs(twelve, null, 1, old8).Count(x => x.Classes.Length > 0) == 0);
        // ---- a click counts against the order that player saw: the cards re-sort as votes come in ----
        var before = HudVote.Order(twelve, 0);
        var resorted = VoteState.Decode(big.ToString());
        resorted.Options.Reverse(0, 2); // the added map now leads: card 1 shows it, card 2 Keep
        var after = HudVote.Order(resorted, 0);
        Check($"A card's click counts for the map the player saw on it: against the order drawn before a re-sort for {HudVote.ReSortSeconds} s, the newest after; a map the vote dropped counts for nothing",
            before[0] == 1 && before[1] == 12 && after[0] == 12 && after[1] == 1
            && HudVote.Click(resorted, "gg_m1_play", 0, 1003, before) == "!mv 1793600000 1" && HudVote.Click(resorted, "gg_m1_play", 0, 1003, after) == "!mv 1793600000 12"
            && HudVote.Click(resorted, "gg_m1_play", 0, 1003) == "!mv 1793600000 12" && HudVote.Click(resorted, "gg_m1_play", 0, 1003, [42]) == null
            && HudVote.Click(resorted, "gg_m2_play", 0, 1001, before) == "!mv 1793600000 0");
        // ---- where the votes go - bars, voters, the leader or a tie, the strip, the waiting line; a risky map's record on its card ----
        var spread = VoteState.Decode("mode\tarmsrace\tArms Race\nmv\t1793800000\topen\t25\t5\t4\tend\t0\t1\n"
            + "mvo\t1\tarmsrace\tpicked\t12\t3084291312\t#12 aim_x\t17 MB\t2\tAlice\nmvo\t2\tdeathmatch\trolled\tde_dust2\tde_dust2\tde_dust2 (official)\t\t1\t\n"
            + "mvo\t3\twingman\trolled\tde_nuke\tde_nuke\tde_nuke (official)\t\t0\t\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t1\t\n"
            + "mvv\t1001\t1\nmvv\t1003\t1\nmvv\t1002\t2\nmvv\t1004\t99\nmvn\t1\tAlice\tCara\nmvn\t2\tBob\nmvn\t99\tDan\nmvw\tEve\n");
        var risky = new HudCatalog { Version = 9 };
        risky.Maps.Add(new HudCatalog.Entry("3084291312", "12", 12, 3084291312, "aim_x", 17, false, 3, 0, 0, 0));
        var sp = HudVote.Cards(spread, 1001, risky);
        bool SC(string id, string cls) => sp.Classes.Single(x => x.Id == id && x.Class == cls).On;
        string SV(string name) => sp.Vars.Single(x => x.Name == name).Text;
        Check("Where the votes go: a card's bar is its share in tenths (2 of 4 is w5), its voters by name, the leader lit, nothing on a card without votes",
            SC("gg_m1", "w5") && !SC("gg_m1", "w4") && SC("gg_m2", "w3") && SC("gg_m3", "w0") && SC("gg_m1", "vhas") && !SC("gg_m3", "vhas")
            && SV(HudVote.VotersVar(1)) == "Alice  ·  Cara" && SV(HudVote.VotersVar(2)) == "Bob" && SV(HudVote.VotersVar(3)) == ""
            && SC("gg_m1", "vlead") && !SC("gg_m1", "vtie") && !SC("gg_m2", "vlead") && sp.Classes.Count(x => x.Class.StartsWith('w') && x.Id == "gg_m1" && x.On) == 1);
        Check("...the strip: the options with votes, the most first, each its count (its weight in the strip) and its mode's colour, Random grey; the waiting line names who has not voted",
            SC(HudVote.Segment(1), "used") && SC(HudVote.Segment(1), "w2") && SC(HudVote.Segment(2), "w1") && SC(HudVote.Segment(3), "w1") && SC(HudVote.Segment(1), "mode-armsrace") && SV(HudVote.StripVar(1)) == $"{Hud.Clean("#12 aim_x", 12)}  ·  2"
            && SC(HudVote.Segment(2), "mode-deathmatch") && SV(HudVote.StripVar(2)) == $"{Hud.Clean("de_dust2 (official)", 12)}  ·  1" && SC(HudVote.Segment(3), "vrand") && SV(HudVote.StripVar(3)) == "Random  ·  1"
            && !SC(HudVote.Segment(4), "used") && SC(HudView.Screen, "vstrip") && SV(HudVote.WaitVar) == "Waiting for: Eve"
            && HudVote.Cards(spread, 1001, null).Texts.Single(x => x.Id == "gg_m_count").Text.Contains("waiting for Eve"));
        Check("...a risky map's record on its ballot card for everybody: the strip's words and its classes, from the catalog; none on a card the catalog has nothing against",
            sp.Texts.Single(x => x.Id == "gg_m1_warn").Text == "Broken: crashed 3/3 test loads" && SC("gg_m1", "risky") && SC("gg_m1", "broken")
            && sp.Texts.Single(x => x.Id == "gg_m2_warn").Text == "" && !SC("gg_m2", "risky"));
        Check("...the names a card shows while the mouse is on it: every voter, longer than the chips the older contents show (HoverVar beside VotersVar)",
            SV(HudVote.HoverVar(1)) == "Alice  ·  Cara" && SV(HudVote.HoverVar(3)) == "" && HudVote.HoverNames > HudVote.CardNames
            && HudVote.Names(Enumerable.Range(1, 12).Select(i => $"Player {i}").ToList(), HudVote.HoverNames).Length <= HudVote.HoverNames);
        var picked = VoteState.Decode("mode\tarmsrace\tArms Race\nmv\t1793800009\topen\t25\t2\t1\tmid\t1001\t1\n"
            + "mvo\t1\tarmsrace\tpicked\t12\t3084291312\t#12 aim_x\t17 MB\t1\tAlice\tpicked,big,liked,upload,updated,trending\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t0\t\n");
        var pk = HudVote.Cards(picked, 1001, null);
        bool PK(string cls) => pk.Classes.Single(x => x.Id == "gg_m1" && x.Class == cls).On;
        Check("Every tag on a ballot card: the cautions and the pick on the classes every content has (big, queued), the rest as t-* - a new upload updated since shows both",
            PK("queued") && PK("big") && !PK("ads") && PK("t-liked") && PK("t-upload") && PK("t-updated") && PK("t-trending") && !PK("t-toprated") && !PK("t-newtous")
            && HudVote.TagClasses.All(c => MapTags.Find(c[2..]) != null));
        var tie = VoteState.Decode("mode\tarmsrace\tArms Race\nmv\t1793800001\topen\t25\t2\t2\tend\t0\t1\nmvo\t1\tarmsrace\trolled\t5\t3084291305\t#5 a\t\t1\t\nmvo\t2\tarmsrace\trolled\t6\t3084291306\t#6 b\t\t1\t\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t0\t\nmvv\t1001\t1\nmvv\t1002\t2\n");
        var tc = HudVote.Cards(tie, 1001, null);
        Check("A tie shows as a tie: both tied cards and their segments tagged, no leader; everybody voted: the waiting line says so",
            tc.Classes.Single(x => x.Id == "gg_m1" && x.Class == "vtie").On && tc.Classes.Single(x => x.Id == "gg_m2" && x.Class == "vtie").On && !tc.Classes.Single(x => x.Id == "gg_m1" && x.Class == "vlead").On
            && tc.Classes.Single(x => x.Id == HudVote.Segment(1) && x.Class == "vtie").On && tc.Vars.Single(x => x.Name == HudVote.WaitVar).Text == "Everyone has voted");
        Check("Names that do not fit a card's chips end in +N; a share rounds to the nearest tenth; a strip segment counts its votes, ten and more as ten",
            HudVote.Names(["Alexander", "Bartholomew", "Constantine", "Dorothea"]) is var cut && cut.Length <= 34 && cut.EndsWith(" +2") && HudVote.Share(1, 3) == "w3" && HudVote.Share(2, 3) == "w7"
            && HudVote.Share(0, 0) == "w0" && HudVote.Share(1, 20) == "w1"
            && HudVote.Names(["Remy", "Test A", "Test B"], HudVote.CardNames) == "Remy  ·  Test A  ·  Test B" && HudVote.Names(["Alexander", "Test A", "Test B"], HudVote.CardNames) == "Alexander  ·  Test A  +1"
            && HudVote.CountClasses("s", 3).Single(x => x.On).Class == "w3" && HudVote.CountClasses("s", 14).Single(x => x.On).Class == "w10" && HudVote.CountClasses("s", 0).Single(x => x.On).Class == "w0");
        const string TieDone = "mode\tarmsrace\tArms Race\nmv\t1793800005\tdone\t6\t2\t2\tend\t0\t1\nmvo\t1\tarmsrace\trolled\t5\t3084291305\t#5 a\t\t1\t\nmvo\t2\tarmsrace\trolled\t6\t3084291306\t#6 b\t\t1\t\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t0\t\nmvv\t1001\t1\nmvv\t1002\t2\nmvr\t1\ttie\t#5 a\tarmsrace\n";
        var allDone = VoteState.Decode("mode\tarmsrace\tArms Race\nmv\t1793800003\tdone\t6\t2\t2\tend\t0\t1\nmvo\t1\tarmsrace\trolled\t5\t3084291305\t#5 a\t\t2\t\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t0\t\nmvv\t1001\t1\nmvv\t1002\t1\nmvr\t1\tall\t#5 a\tarmsrace\n");
        var mostDone = VoteState.Decode("mode\tarmsrace\tArms Race\nmv\t1793800004\tdone\t6\t3\t2\tend\t0\t1\nmvo\t1\tarmsrace\trolled\t5\t3084291305\t#5 a\t\t2\t\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t0\t\nmvv\t1001\t1\nmvv\t1002\t1\nmvr\t1\tmost\t#5 a\tarmsrace\n");
        Check("A vote everybody decided says so while it shows its result, one the clock decided does not; both keep everybody frozen through the countdown",
            allDone is { Open: false, Freeze: true, Result.How: "all" } && HudVote.WaitLine(allDone) == "Everyone has voted" && HudVote.WaitLine(mostDone) == ""
            && HudVote.WaitLine(VoteState.Decode(TieDone)) == "Everyone has voted" // a tie everybody voted in, drawn at once
            && HudVote.Cards(allDone, 1001, null).Vars.Single(x => x.Name == HudVote.WaitVar).Text == "Everyone has voted");
        Check("The wait after a vote rides the state without a vote ('mvwait'); option lines without a vote line are no vote, and freeze nothing",
            VoteState.Decode("mvwait\t75\n") is { WaitFor: 75, Round: 0, Freeze: false } && VoteState.Decode("mvo\t1\tarmsrace\tpicked\t1\t1\tx\t\t0\t\n") is { Options.Count: 0, Freeze: false }
            && new VoteState { WaitFor = 9 }.Encode().Contains("mvwait\t9"));
        var levels = HudVote.VersionClasses();
        Check($"The version classes: content {Hud.Content} is level {HudVote.ContentLevel}; the newest content the server knows is one class, {HudVote.LatestClass(HudVote.ContentLevel)}; the contents v4_0_0 to v4_5_0 still read their svge/svlt, each level exactly one of them",
            Hud.Content == $"v{HudVote.ContentLevel / 10}_{HudVote.ContentLevel % 10}_0" && HudCheck.LevelOf(Hud.Content) == HudVote.ContentLevel
            && levels.Count(x => x.Class.StartsWith("latest-") && x.On) == 1 && levels.Single(x => x.Class.StartsWith("latest-")).Class == $"latest-{HudVote.ContentLevel}"
            && Enumerable.Range(HudVote.OldFirst, HudVote.OldLast - HudVote.OldFirst + 1).All(l => levels.Single(x => x.Class == $"svge{l + 1}").On && !levels.Single(x => x.Class == $"svlt{l}").On)
            && HudVote.VersionClasses(HudVote.OldFirst).Single(x => x.Class == $"svlt{HudVote.OldFirst + 1}").On && levels.All(x => x.Id == HudView.Global));
        if (File.Exists(HudStyleFile))
        {
            var css = File.ReadAllText(HudStyleFile);
            var xml = File.ReadAllText(HudLayoutFile);
            var banners = Enumerable.Range(HudVote.ContentLevel + 1, HudVote.LatestLast - HudVote.ContentLevel).ToList();
            Check($"The outdated banner in this layout ({Hud.Content}): persistent under latest-{HudVote.ContentLevel + 1} ... latest-{HudVote.LatestLast}, never under its own level or below; its own version and the server's in its words",
                banners.All(n => css.Contains($".gg-global.latest-{n} .gg-outdated")) && !css.Contains($".gg-global.latest-{HudVote.ContentLevel} ") && !css.Contains(".gg-screen.vjoin")
                && xml.Contains($"yours {Hud.Content}") && xml.Contains("{s:" + HudVote.VersionVar + "}") && xml.Contains("{s:" + HudVote.ModeVar + "}") && xml.Contains($"HUD {Hud.Content}"));
        }
        (S, Broken, BrokenReasons) = (sBefore, brokenBefore, reasonsBefore);
        try { Directory.Delete(StateDir, true); } catch (IOException) { }
        StateDir = dirBefore;
    }
}
