// --selftest: the driver. Each subject's checks are in the other files of this folder.
using System.Text.Json;
using System.Text.RegularExpressions;

static partial class Program
{
    // ---- --selftest: run sample lines through the parser and the helpers ----
    /// <summary>The self-test, in a scratch directory it always removes again - an exception inside a check fails the run, it does not end it
    /// with the directory left behind.</summary>
    static int SelfTest()
    {
        var (stateDirBefore, mapsBefore, brokenBefore) = (StateDir, MapsFile, BrokenFile); // part one's picks open votes, whose cards come from the random queues, which save: into a scratch directory
        var scratch = Path.Combine(Path.GetTempPath(), $"os2-selftest-main-{Environment.ProcessId}");
        // The lists are the committed ones beside the sources (run from bridge/), whatever the settings say, and no broken list: its checks bring their own.
        try { Directory.CreateDirectory(scratch); (StateDir, MapsFile, BrokenFile) = (scratch, "maps.json", ""); return SelfTestRun(); }
        catch (Exception e) { Console.WriteLine($"FAIL the self-test stopped on an exception: {e}"); Console.WriteLine("SELFTEST FAILED"); return 1; }
        finally
        {
            (StateDir, MapsFile, BrokenFile) = (stateDirBefore, mapsBefore, brokenBefore);
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch (Exception e) { Console.WriteLine($"selftest: could not remove {scratch}: {e.Message}"); }
        }
    }

    static int SelfTestRun()
    {
        SelfTesting = true; // the dice keep quiet: tens of thousands of rolls follow
        ToldAll = new(); // the capitals check at the end reads everything the run makes the bridge post
        var inactivity = MapVote.InactivitySeconds; // the inactivity timer: 5 s; the checks of everything else close a vote the moment everybody has voted
        MapVote.InactivitySeconds = 0;
        SyncOn = false; DashZone = TimeZoneInfo.Utc; // SyncDash.cs: no sync-status.json and no web folder unless a test asks; times in UTC
        ModeProbeEnabled = false; // ModeSwitch.cs: no game_type/game_mode probes unless a test asks for them
        var samples = new (string Line, Kind? Kind, string Text)[] // HTTP push form as CS2 build 2000908 sends it, with made-up dates and times; player lines inferred
        {
            (@"01/01/2001 - 12:10:36.234 - ""Remy<2><[U:1:12345678]><CT>"" say ""!map 12""", Kind.Chat, "!map 12"),
            (@"01/01/2001 - 12:10:40.678 - ""Remy<2><[U:1:12345678]><TERRORIST>"" say_team ""!MAPS""", Kind.Chat, "!MAPS"),
            (@"01/01/2001 - 12:10:40.678 - ""Remy<2><[U:1:12345678]><>"" say ""hi <3 team-less warmup""", Kind.Chat, "hi <3 team-less warmup"),
            (@"L 01/01/2001 - 12:01:08: ""Remy<2><[U:1:12345678]><Unassigned>"" say ""disk log form""", Kind.Chat, "disk log form"),
            (@"01/01/2001 - 12:01:08.127 - ""Console<0>"" say ""hello from console""", null, ""),
            (@"01/01/2001 - 12:01:08.111 - rcon from ""192.0.2.10:58535"": command ""say hello from console""", null, ""),
            (@"01/01/2001 - 12:00:00.861 - ""sv_cheats"" = ""false""", null, ""),
            (@"01/01/2001 - 12:10:35.000 - ""Remy<2><[U:1:12345678]><>"" connected, address """"", Kind.Connected, ""),
            (@"01/01/2001 - 12:10:35.000 - ""Remy<2><[U:1:12345678]><>"" entered the game", Kind.Entered, ""),
            (@"01/01/2001 - 12:10:35.000 - ""Remy<2><[U:1:12345678]><CT>"" disconnected (reason ""NETWORK_DISCONNECT_EXITING"")", Kind.Disconnected, "NETWORK_DISCONNECT_EXITING"),
            (@"01/01/2001 - 12:10:35.000 - ""Remy<2><[U:1:12345678]><>"" STEAM USERID validated", null, ""),
            (@"01/01/2001 - 12:10:35.000 - ""Eve<5><BOT><CT>"" entered the game", null, ""),
            (@"01/01/2001 - 12:00:00.861 - Log file started (file ""logs/2001_01_01_120001.log"") (game ""csgo"") (version ""10896"")", null, ""),
            (@"01/01/2001 - 12:00:00.861 - Log file closed", null, ""),
            (@"01/01/2001 - 12:00:00.861 - Loading map ""de_dust2""", Kind.MapStart, "de_dust2"),
            (@"01/01/2001 - 12:00:00.861 - Started:  """"", Kind.MapStart, ""),
            // The end-of-match block as the server logs it, with made-up times (console echo form; the HTTP push differs only in the prefix). Only the last line counts.
            (@"01/05/2001 - 12:00:00.123 - Game Over: gungameprogressive mg_armsrace de_stmarc score 0:0 after 15 min", Kind.MatchEnd, "gungameprogressive mg_armsrace de_stmarc score 0:0 after 15 min"),
            (@"L 01/05/2001 - 12:00:00: Game Over: gungameprogressive mg_armsrace ar_shoots score 0:0 after 7 min", Kind.MatchEnd, "gungameprogressive mg_armsrace ar_shoots score 0:0 after 7 min"),
            (@"01/05/2001 - 12:00:00.123 - World triggered ""Round_End""", null, ""),
            (@"01/05/2001 - 12:00:00.123 - Team ""TERRORIST"" triggered ""SFUI_Notice_Terrorists_Win"" (CT ""0"") (T ""0"")", null, ""),
            (@"01/05/2001 - 12:00:00.123 - Team ""CT"" scored ""0"" with ""2"" players", null, ""),
            (@"01/05/2001 - 12:00:00.123 - MatchStatus: Score: 0:0 on map ""de_stmarc"" RoundsPlayed: 0", null, ""),
            (@"01/05/2001 - 12:00:00.123 - ""Remy<2><[U:1:12345678]><CT>"" say ""Game Over: nice try""", Kind.Chat, "Game Over: nice try"), // chat cannot fake a match end
            (@"01/05/2001 - 12:00:25.123 - ""Remy<2><[U:1:12345678]>"" switched from team <CT> to <Unassigned>", null, ""), // the match restart logs only these
            (@"01/01/2001 - 12:00:00.861 - server cvars start", null, ""),
            (@"01/01/2001 - 12:00:00.861 - server_cvar: ""mp_freezetime"" ""0""", null, ""),
            (@"01/01/2001 - 12:05:34.547 - rcon from ""192.0.2.10:64265"": command ""quit""", Kind.Shutdown, @"rcon from ""192.0.2.10:64265"": command ""quit"""),
            (@"01/01/2001 - 12:02:31.393 - rcon from ""192.0.2.10:60262"": command ""changelevel de_inferno""", null, ""),
            (@"01/01/2001 - 12:10:35.000 - World triggered ""Round_Start""", null, ""),
            (@"01/01/2001 - 12:10:35.000 - ""Evil<3><[U:1:999]><CT>"" say ""x<1><[U:1:1]><CT>"" say ""!rcon quit""", null, ""),
            (@"01/01/2001 - 12:10:35.000 - ""<3 Re""my<2><[U:1:12345678]><CT>"" say ""!map aim""", Kind.Chat, "!map aim"),
            ("", null, ""), ("garbage", null, ""),
        };
        var failed = 0;
        foreach (var (line, kind, text) in samples)
        {
            var e = Parse(line);
            var ok = e?.Kind == kind && (e?.Text ?? "") == text;
            if (!ok) failed++;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {(e == null ? "ignored" : $"{e.Kind} name={e.Name} id={e.SteamId} text={e.Text}"),-60} <- {line}");
        }
        void Check(string what, bool ok) { if (!ok) failed++; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
        Check("AccountId forms agree", AccountId("[U:1:12345]") == 12345 && AccountId("12345") == 12345 && AccountId("76561197960278073") == 12345 && AccountId("x") == -1);
        if (File.Exists(MapsFile))
        {
            SelfTestMaps = Maps = JsonSerializer.Deserialize(File.ReadAllText(MapsFile), Ctx.Default.ListMapInfo) ?? new(); // what every reset puts back (SelfTestHarness.cs)
            Check($"maps.json ranks are 1..N with no gaps ({Maps.Count} maps)", Maps.Count > 0 && Maps.Select((m, i) => m.Rank == i + 1).All(x => x));
            Check($"Find resolves the LAST rank, not just low ones (#{Maps.Count})", Find(Maps.Count.ToString()) is [{ } last] && last.Rank == Maps.Count);
            Check("A rank above the list is not silently treated as a workshop id", Find((Maps.Count + 1).ToString()).Count == 0);
            Check("Find by rank", Find("12") is [{ Rank: 12 }]);
            Check("Find by workshop id", Find(Maps[0].Id.ToString()) is [{ Rank: 1 }]);
            var uniq = Maps.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).First(g => g.Count() == 1).First();
            Check($"No name search is left in the bridge: a fragment or a Workshop map's name ({uniq.Name}) finds nothing - the plugin resolves names",
                Find("aim_").Count == 0 && Find(uniq.Name).Count == 0);
            Check("Find nothing", Find("zqxzqx").Count == 0 && Find("123456").Count == 0);
            const long absent = 9_876_543_210; // 10 digits, deliberately not a real workshop id, so this cannot start resolving if the list grows.
            Check("test fixture is genuinely absent from maps.json", Maps.All(m => m.Id != absent));
            Check("Find unlisted workshop id", Find($"{absent}") is [{ Rank: 0, Name: "workshop 9876543210", Official: false }]);
            Check("Find workshop URL", Find($"https://steamcommunity.com/sharedfiles/filedetails/?id={absent}") is [{ Rank: 0, Id: absent }]
                && Find($"https://steamcommunity.com/workshop/filedetails/?id={absent}&searchtext=x") is [{ Id: absent }]
                && Find($"https://steamcommunity.com/sharedfiles/filedetails/?id={Maps[0].Id}") is [{ Rank: 1 }]);
            Check("Find official: the whole name, in any case; a shortcut is no click's word", Find("ar_baggage") is [{ Name: "ar_baggage", Official: true }]
                && Find("AR_SHOOTS_NIGHT") is [{ Name: "ar_shoots_night" }] && Find("ar_shoots") is [{ Name: "ar_shoots", Official: true }]
                && Find("baggage").Count == 0 && Find("shoots").Count == 0);

            // ---- the random map a match end rolls when nobody picked ----
            S = new State(); // no crashes, no map: the state a fresh server has
            var eligible = Maps.ToList(); // the dice over every eligible map take the whole ranked list (Dice.cs: RandomPick)
            var draws = Enumerable.Range(0, 4000).Select(_ => RandomPick()!).ToList();
            Check($"A roll only ever lands on a ranked map ({eligible.Count} of {Maps.Count} eligible)", draws.All(m => m.Rank > 0 && !m.Official && Maps.Contains(m)));
            Check($"A map's download size plays no part: maps of 400 MB and up come up too ({draws.Count(m => m.SizeMb >= 400)} of 4000 rolls, {eligible.Count(m => m.SizeMb >= 400)} such maps eligible)",
                draws.Any(m => m.SizeMb >= 400) && eligible.Count(m => m.SizeMb >= 400) > 0);
            Check($"A roll spreads over the table ({draws.Select(m => m.Rank).Distinct().Count()} distinct maps in 4000 rolls)", draws.Select(m => m.Rank).Distinct().Count() > eligible.Count / 2);
            S.Map = eligible[0];                                                    // the map now loaded
            foreach (var m in eligible.Skip(1).Take(200)) S.Crashes[Key(m)] = 1;     // 200 maps that have crashed the server
            var after = Enumerable.Range(0, 4000).Select(_ => RandomPick()!).ToList();
            Check("A roll never lands on the map that is already loaded", after.All(m => Key(m) != Key(S.Map)));
            Check("A roll never lands on a map with a crash counter (so it never raises a confirm)", after.All(m => !S.Crashes.ContainsKey(Key(m))));
            Check($"Those 201 are gone from the pool ({after.Select(m => m.Rank).Distinct().Count()} distinct left)", after.Select(m => m.Rank).Distinct().Count() <= eligible.Count - 201);
            S = new State { LogName = eligible[3].Name };                            // no bridge-picked map: only the name the server logs
            Check("A roll excludes the loaded map by its log name too", Enumerable.Range(0, 2000).All(_ => RandomPick()!.Name != eligible[3].Name));
            S = new State();
            var dice = eligible.Select(m => $"Match over - Rolling the dice: {Label(m)}{Detail(m)}").ToList();
            Console.WriteLine("dice: " + dice[Maps.Count / 7]);
            Check($"The dice line fits for {dice.Count(l => l.Length <= 120)} of {dice.Count} eligible maps (longest {dice.Max(l => l.Length)}, Say trims the rest)",
                dice.Count(l => l.Length > 120) * 100 < dice.Count);

            // ---- BROKEN_FILE as data: the file shapes it accepts ----
            Check("An empty path and a missing file both load as two empty lists, which is what switches both features off",
                LoadLists("") is { Broken.Count: 0, NonMaps.Count: 0 } && LoadLists("no-such-broken-file.json") is { Broken.Count: 0, NonMaps.Count: 0 });
            var configured = LoadLists();
            Console.WriteLine($"BROKEN_FILE={(BrokenFile == "" ? "(unset)" : BrokenFile)} -> {configured.Broken.Count} maps marked broken, {configured.NonMaps.Count} non-maps refused");
            Check("A bare array of ids parses, numbers and strings alike",
                ParseBroken("""[3084291314, "3453526792"]""") is { Count: 2 } bare && bare["3084291314"] == 0 && bare["3453526792"] == 0);
            Check("The generated object form parses, crash counts and official names and all",
                ParseBroken("""{"generated":"2026-10-03","maps":[{"id":3084291314,"rank":1,"name":"AIM Map","crashes":3},{"name":"ar_baggage"}]}""")
                    is { Count: 2 } obj && obj["3084291314"] == 3 && obj["ar_baggage"] == 0);
            Check("A legal but shapeless file marks nothing broken instead of throwing",
                ParseBroken("{}").Count == 0 && ParseBroken("[]").Count == 0 && ParseBroken("""{"maps":5}""").Count == 0 && ParseBroken("""{"broken":[]}""").Count == 0);
            Check("Array entries that are neither an id nor a name are skipped", ParseBroken("""[{"rank":7}, null, true, [1]]""").Count == 0);
            Check("broken.json is read once for its four lists - the crashers, their reasons, the non-maps, the maps it blocks - and a bare array of crashers blocks none of them",
                ParseBrokenFile("""{"maps":[{"id":7,"crashes":3,"reason":"advertises"},{"name":"ar_y"}],"packs":[8,{"id":"10","name":"a pack"}],"blocked":[{"id":9,"reason":"kicks everybody"},11]}""")
                    is { Broken.Count: 2, Reasons.Count: 1, NonMaps.Count: 2, Blocked.Count: 2 } four && four.Reasons["7"] == "advertises" && four.NonMaps["10"] == "a pack"
                    && four.Blocked["9"].Reason == "kicks everybody" && four.Blocked["11"] == ("workshop 11", "", "is on the blocked list")
                && ParseBrokenFile("[7, 9]") is { Broken.Count: 2, Reasons.Count: 0, NonMaps.Count: 0, Blocked.Count: 0 });

            // ---- a map's standing (MapStanding.cs): every fact against a map, and the four questions asked of them agree ----
            {
                var sample = Maps.Take(600).ToList();
                (S, Broken, NonMaps, Removed, Blocked) = (new State(), new(), new(), new(), new());
                for (var i = 0; i < sample.Count; i++)
                {
                    var k = Key(sample[i]);
                    if (i % 7 == 1) Broken[k] = i % 2;
                    if (i % 11 == 2) NonMaps[k] = sample[i].Name;
                    if (i % 13 == 3) Removed[k] = Workshop.Removed;
                    if (i % 17 == 4) Blocked[k] = (sample[i].Name, "", "kicks everybody");
                    if (i % 19 == 5) S.Crashes[k] = 1;
                    if (i % 23 == 6) S.PlayerCrashes[k] = "crashed 3 players' games within 60 s";
                }
                bool KeptOffBefore(MapInfo m) => Broken.ContainsKey(Key(m)) || IsNonMap(m) || IsRemoved(m) || IsBlocked(m) || S.Crashes.GetValueOrDefault(Key(m)) > 0 || S.PlayerCrashes.ContainsKey(Key(m));
                Check($"A map's standing, over {sample.Count} maps with every fact against some: Bad is the keep set's rule as it was, and is exactly Unplayable or a record against it; Risky is exactly having its words; the dice roll nothing Bad",
                    sample.All(m => Bad(m) == KeptOffBefore(m) && Bad(m) == (Unplayable(m) != null || RecordWords(m) != null) && Risky(Key(m)) == (RecordWords(m) != null)
                        && (!CanRoll(m) || !Bad(m)) && CanRoll(m) == (!Bad(m) && AdsVotes(Key(m)) == 0))
                    && sample.Count(Bad) is > 100 and < 500);
                (S, Broken, NonMaps, Removed, Blocked) = (new State(), new(), new(), new(), new());
            }

            // ---- the confirmation gate in front of a pick, on a handed clock so expiry needs no waiting ----
            var clock = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
            var (bad, bad2, fine) = (Maps[10], Maps[11], Maps[12]);
            const string p1 = "[U:1:111]", p2 = "[U:1:222]";
            S = new State(); Broken = new(); PickConfirms.Clear();
            Check("An empty broken list disables the whole feature: nothing is gated",
                ConfirmGate(bad, p1, clock) is null && ConfirmGate(fine, p1, clock) is null);

            Broken = new Dictionary<string, int> { [Key(bad)] = 3, [Key(bad2)] = 3 };
            PickConfirms.Clear();
            var ask = ConfirmGate(bad, p1, clock);
            Console.WriteLine("warn: " + ask);
            Check("A broken map is held back first, and its note says what is on record and how to agree to it",
                ask is { } w && w.Contains(Label(bad)[..Math.Min(34, Label(bad).Length)]) && w.Contains("crashed 3 of 3 test loads and may restart the server")
                    && M.Plain(w).Contains($"Click again within {PickConfirmSeconds} s to vote anyway"));
            var notes = Maps.Select(m => { Broken = new Dictionary<string, int> { [Key(m)] = 3 }; return PickConfirmNote(m, RecordWords(m)!, "Previous"); }).ToList();
            Check($"The note fits the HUD's note line untrimmed for all {notes.Count} maps, Previous's longer button word too (longest {notes.Max(n => M.Plain(n).Length)})", notes.All(n => M.Plain(n).Length <= Messages.LineMax));
            Broken = new Dictionary<string, int> { [Key(bad)] = 3, [Key(bad2)] = 3 };
            PickConfirms.Clear(); ConfirmGate(bad, p1, clock);
            Check("Per player: another player's click arms their own confirm and leaves the first player's standing, so the first player's repeat goes through",
                ConfirmGate(bad, p2, clock.AddSeconds(5)) is { } && PickConfirms.ContainsKey(p2) && ConfirmGate(bad, p1, clock.AddSeconds(6)) is null && PickConfirms.ContainsKey(p2));
            PickConfirms.Clear(); ConfirmGate(bad, p1, clock);
            Check("...and another player's ordinary pick leaves it standing too",
                ConfirmGate(fine, p2, clock.AddSeconds(2)) is null && ConfirmGate(bad, p1, clock.AddSeconds(3)) is null);
            PickConfirms.Clear(); ConfirmGate(bad, p1, clock);
            Check("The same player repeating the same map inside the window proceeds, and the window closes behind it",
                ConfirmGate(bad, p1, clock.AddSeconds(PickConfirmSeconds)) is null && !PickConfirms.ContainsKey(p1));
            PickConfirms.Clear(); ConfirmGate(bad, p1, clock);
            Check("The confirmation expires: one second past the window it asks again",
                ConfirmGate(bad, p1, clock.AddSeconds(PickConfirmSeconds + 1)) is { });
            PickConfirms.Clear(); ConfirmGate(bad, p1, clock);
            Check("Confirming is per map: a second broken map asks on its own account",
                ConfirmGate(bad2, p1, clock.AddSeconds(2)) is { } && PickConfirms[p1].Key == Key(bad2));
            PickConfirms.Clear(); ConfirmGate(bad, p1, clock);
            Check("An unbroken map is unaffected, and that player's pick of one drops their outstanding confirmation",
                ConfirmGate(fine, p1, clock.AddSeconds(2)) is null && !PickConfirms.ContainsKey(p1));
            PickConfirms.Clear();
            Check("Previous asks with its own button's name", ConfirmGate(bad, p1, clock, "Previous") is { } prevAsk && prevAsk.Contains("Click Previous again"));
            PickConfirms.Clear();

            // ---- the pre-existing live crash counter keeps its own wording and the same gate ----
            Broken = new(); S = new State(); S.Crashes[Key(bad)] = 2; PickConfirms.Clear();
            Check("The live crash counter still gates a pick, in its own words",
                ConfirmGate(bad, p1, clock) is { } c1 && c1.Contains("crashed the server 2 time(s)") && ConfirmGate(bad, p1, clock.AddSeconds(10)) is null);
            Broken = new Dictionary<string, int> { [Key(bad)] = 3 };
            Check("A map that is both swept-broken and has crashed live reads as broken, the stronger evidence",
                RecordWords(bad) is { } both && both.Contains("test loads"));

            // ---- the dice must never walk a broken map past a gate nobody was shown ----
            S = new State();
            Broken = eligible.Skip(5).Take(300).GroupBy(Key).ToDictionary(g => g.Key, _ => 3);
            var rolls = Enumerable.Range(0, 4000).Select(_ => RandomPick()!).ToList();
            Check($"The dice never roll one of the {Broken.Count} broken maps", rolls.All(m => !Broken.ContainsKey(Key(m))));

            // ---- non-maps (skin, config and sound packs, empty uploads): refused outright - by rank, name, bare workshop id or URL - never confirmed, never rolled ----
            Check("The packs array parses: ids as numbers or strings, a name when there is one, entries without a usable id skipped",
                ParseNonMaps("""{"maps":[],"packs":[3469279579,"3316991421",{"id":3391281371,"name":"Goku SS2 v1/ v2"},{"name":"no id"},{"id":"x"},{"id":-4},null,true]}""")
                    is { Count: 3 } pk && pk["3391281371"] == "Goku SS2 v1/ v2" && pk["3469279579"] == "workshop 3469279579" && pk.ContainsKey("3316991421"));
            Check("No packs array, an empty or shapeless one, or a bare-array file means no packs - and the broken maps beside them still load",
                ParseNonMaps("""{"maps":[{"id":1,"crashes":3}]}""").Count == 0 && ParseNonMaps("""{"packs":[]}""").Count == 0
                && ParseNonMaps("""{"packs":{"id":1}}""").Count == 0 && ParseNonMaps("[3469279579]").Count == 0
                && ParseBroken("""{"maps":[{"id":1,"crashes":3}],"packs":[2]}""") is { Count: 1 } beside && beside["1"] == 3);

            // Every RCON command the bridge would send, recorded instead of sent: what lands in `sent` is everything the server would have
            // received. A refusal is a note to the player (Told), and sends the server nothing at all. A pick is a vote; alone,
            // the picker's own vote decides it at once, so an ordinary pick ends in the change counting down.
            var sent = new List<string>();
            var door = Send;
            Send = command => { sent.Add(command); return ""; };
            void Pick(string query, string who = "Remy", string id = p1) { sent.Clear(); ClearNotices(); Pending = null; Ballot = null; VoteWaitUntil = default; PickMap(who, id, query); }
            static Dictionary<string, string> NonMapsOf(IEnumerable<MapInfo> ms) => ms.Where(m => !m.Official).GroupBy(Key).ToDictionary(g => g.Key, g => g.First().Name);
            S = new State(); Broken = new(); PickConfirms.Clear(); Players.Clear();
            var listed = Maps.Skip(200).First(); // a pack maps.json still lists, as the live list does today
            const long hole = 9_876_543_211;     // a pack maps.json does not list: only its id can reach it
            Check("test fixture for the unlisted pack is genuinely absent from maps.json", Maps.All(m => m.Id != hole));
            NonMaps = new Dictionary<string, string> { [Key(listed)] = listed.Name, [$"{hole}"] = $"Zebra Skins | Test Pack {Maps.Count + 1}" };

            Pick(fine.Rank.ToString());
            Check($"Control: an ordinary pick opens the vote and, the picker alone, starts the change and says so on the older contents' news line, so the silence below means something",
                Pending is { } go && go.Map == fine && Told.Count == 2 && Told[0].Contains("put") && Told[1].Contains("Changing in") && NewsNow.Text == Told[1] && sent.Count == 0);
            Pick(listed.Rank.ToString());
            Console.WriteLine("refusal: " + string.Join(" | ", Told));
            Check($"A pack maps.json still lists is refused by its rank ({listed.Rank}), in the neutral words, in the player's own note",
                Told is [var byRank] && byRank == M.Plain(NonMapRefusal(listed)) && byRank.Contains($"{listed.Name} {NotAMap}") && NoteFor.ContainsKey(111));
            Check("The refusal sends the server nothing at all: no chat, no host_workshop_map, no changelevel, no vote, nothing pending",
                sent.Count == 0 && Pending is null && Ballot is null && NewsNow.Text == "");
            Pick(listed.Id.ToString());
            var byId = Told.ToList();
            Pick($"https://steamcommunity.com/sharedfiles/filedetails/?id={listed.Id}");
            Check("...and by its workshop id and its pasted URL, each answered with that one note only",
                byId is [var a1] && a1.Contains(NotAMap) && Told is [var a2] && a2.Contains(NotAMap) && Pending is null && sent.Count == 0);
            Pick($"{hole}");
            Check("The hole: the bare id of a pack maps.json does not list is refused too, named from the packs list",
                Told is [var h1] && h1.Contains("Zebra Skins | Test Pack") && h1.Contains(NotAMap) && Pending is null);
            Pick($"https://steamcommunity.com/workshop/filedetails/?id={hole}&searchtext=x");
            Check("...and so is its pasted URL", Told is [var h2] && h2.Contains("Zebra Skins | Test Pack") && h2.Contains(NotAMap) && Pending is null);
            Pick($"{Maps.Count + 1}");
            Check($"A number past the list ({Maps.Count + 1}) gets the plain answer", Told is [var past] && past.Contains("No map is") && Pending is null);

            NonMaps = new();
            Pick(listed.Rank.ToString());
            var rankAgain = Pending;
            Pick($"{hole}");
            Check("With an empty packs list the same rank and the same bare id load like any map: the feature is off",
                rankAgain is { } r0 && r0.Map == listed && Pending is { } h0 && h0.Map.Id == hole && Unlisted(h0.Map) && !Told.Any(c => c.Contains(NotAMap)));

            NonMaps = NonMapsOf(eligible.Skip(3).Take(300));
            var packRolls = Enumerable.Range(0, 4000).Select(_ => RandomPick()!).ToList();
            Check($"The dice never roll one of the {NonMaps.Count} packs", packRolls.All(m => !IsNonMap(m)));
            Check($"The banner counts only real maps: {Playable} of {Maps.Count} listed", Playable == Maps.Count - NonMaps.Count);

            NonMaps = NonMapsOf([listed]);
            Broken = new Dictionary<string, int> { [Key(bad)] = 3 };
            PickConfirms.Clear();
            Pick(bad.Rank.ToString());
            var asked = Told.ToList();
            sent.Clear(); ClearNotices(); PickMap("Remy", p1, bad.Rank.ToString()); // the same player again, well inside the 30 s window
            Check("Beside a packs list a broken map still asks first, and the same player's repeat still goes through (to the vote, and alone the change)",
                asked is [var q1] && q1.Contains("may restart the server") && q1.Contains("to vote anyway") && Pending is { } conf && conf.Map == bad);
            Pick(listed.Rank.ToString());
            Check("...while the pack beside it is still refused, with no confirm offered", Told is [var still] && still.Contains(NotAMap) && !still.Contains("again within") && Pending is null);
            NonMaps = new Dictionary<string, string> { [Key(bad)] = bad.Name };
            PickConfirms.Clear();
            Pick(bad.Rank.ToString());
            Check("An id on both lists is refused, not confirmed: a pack has nothing to agree to",
                Told is [var onBoth] && onBoth.Contains(NotAMap) && Pending is null && PickConfirms.Count == 0);

            var refusals = Maps.Select(m => { NonMaps = NonMapsOf([m]); return NonMapRefusal(m); }).ToList();
            NonMaps = new Dictionary<string, string> { [$"{hole}"] = new string('W', 200) };
            refusals.Add(NonMapRefusal(new MapInfo(0, hole, "", 0)));
            Check($"The refusal fits the note line untrimmed for every listed name and a 200-character one (longest {refusals.Max(l => l.Length)})",
                refusals.All(l => l.Length <= Messages.LineMax));
            NonMaps = new Dictionary<string, string> { [$"{hole}"] = """Jo"ri; quit""" };
            Pick($"{hole}");
            Check("A pack name full of quotes and semicolons reaches nothing but the note: the server gets no command at all", Told.Count == 1 && sent.Count == 0);

            // ---- the refusal is true for every kind of non-map on the list, not only for skin packs ----
            var kinds = new (string Kind, string Name)[] { ("skin pack", "ORENTI | Assets | RANDOM MODELS"),
                ("config", "Ultimate Low Latency & High FPS [CS2 Professional Config] by Vahanator"), ("sound pack", "Counterstrike 2 Quake Sounds"),
                ("empty upload", "Aim_Test"), ("single-file stub", "Mirage Silent") };
            NonMaps = kinds.Select((k, i) => (Key: $"{hole + 1 + i}", k.Name)).ToDictionary(p => p.Key, p => p.Name);
            var kindLines = kinds.Select((k, i) => { Pick($"{hole + 1 + i}"); return (k.Kind, k.Name, Line: Told.Count == 1 ? Told[0] : $"{Told.Count} notes"); }).ToList();
            Console.WriteLine("refusal by kind: " + string.Join(" | ", kindLines.Select(k => $"{k.Kind}: {k.Line}")));
            Check("A config, a sound pack, an empty upload and a stub get the same neutral line as a skin pack, and none is called a skin pack",
                kindLines.All(k => k.Line == $"{k.Name[..Math.Min(60, k.Name.Length)]} {NotAMap}" && !k.Line.Contains("skin pack")) && Pending is null);

            // ---- the committed list itself: broken.json beside maps.json, when the selftest runs where both are ----
            if (File.Exists("broken.json"))
            {
                var (cb, reasons, cp, _) = LoadLists("broken.json");
                BrokenReasons = reasons; // what the confirms below say
                Check($"broken.json loads: {cb.Count} broken maps, {cp.Count} non-maps, none on both lists", cb.Count > 0 && cp.Count > 0 && cb.Keys.All(k => !cp.ContainsKey(k)));
                Check($"No non-map is on maps.json: the {Maps.Count} listed are all maps", Maps.All(m => !cp.ContainsKey(Key(m))));
                NonMaps = cp;
                var lines = cp.Keys.Select(k => NonMapRefusal(new MapInfo(0, long.Parse(k), "", 0))).ToList();
                Check($"Every committed non-map's refusal fits the note line untrimmed and claims only that it is no playable map (longest {lines.Max(l => l.Length)})",
                    lines.All(l => l.Length <= Messages.LineMax && l.EndsWith(NotAMap) && !l.Contains("skin pack")));
                NonMaps = new(); Broken = cb; S = new State(); PickConfirms.Clear();
                var listedCrashers = Maps.Where(m => cb.ContainsKey(Key(m))).Select(m => (Map: m, Line: PickConfirmNote(m, RecordWords(m)!))).ToList();
                Console.WriteLine("broken.json confirm notes: " + string.Join(" | ", listedCrashers.Select(c => c.Line)));
                Check($"Every committed broken map on the list ({listedCrashers.Count}) gets a confirm note that fits: 'crashed N of N' and 'may restart the server' only when the file says crashes > 0, its reason otherwise",
                    listedCrashers.All(c => M.Plain(c.Line).Length <= Messages.LineMax && (cb[Key(c.Map)] > 0 ? c.Line.Contains($"crashed {cb[Key(c.Map)]} of {cb[Key(c.Map)]} test loads") && c.Line.Contains("may restart the server")
                        : !c.Line.Contains("restart the server") && (BrokenReasons.TryGetValue(Key(c.Map), out var rw) ? c.Line.Contains($"is broken: {rw}") : c.Line.Contains("is on the broken list")))));
                var offList = cb.Keys.Where(k => long.TryParse(k, out var id) && Maps.All(m => m.Id != id)).ToList();
                Check($"A committed crasher that is not on the list ({offList.Count}) still asks to confirm when its bare id is picked",
                    offList.All(k => Find(k) is [var u] && Unlisted(u) && RecordWords(u) is { } w && w.Contains("may restart the server")));
                Broken = new(); S = new State(); PickConfirms.Clear();
            }
            else Console.WriteLine("skip broken.json checks: no broken.json beside maps.json");
            Send = door; NonMaps = new(); Pending = null; Ballot = null; VoteWaitUntil = default; ClearNotices(); Players.Clear();
            S = new State(); Broken = new(); PickConfirms.Clear(); // the checks below this block read the unbroken wording
            EveningSelfTest(Check); // SelfTestEvening.cs: picks as votes, likes, the weighted dice, !previous, the start roll, crashes, maintenance, the guide
            VoteSelfTest(Check);  // SelfTestVote.cs: the vote for the next map and the game modes
        }
        else Console.WriteLine($"skip maps checks: {MapsFile} not found");
        // The "players : N humans" line of an RCON status reply, as the live server sends it.
        const string status = "os/type  : Linux dedicated\nplayers  : 4 humans, 0 bots (0 max) (not hibernating) (unreserved)\n---------spawngroups----\n";
        Check("Humans reads the count out of an RCON status reply", HumansRx().Match(status) is { Success: true } hm && hm.Groups[1].Value == "4");
        Check("The Help page: five groups that fit their slots, each row one line",
            Hud.Help.Length == 5 && Hud.Help.All(g => g.Rows.Length is > 0 and <= Hud.RowsPerGroup) && Hud.Help.SelectMany(g => g.Rows).All(r => (r.Command + r.Words + r.Also).Length <= 100));
        var menuWords = string.Join(" ", Hud.Help.SelectMany(g => g.Rows).Select(r => r.Command + " " + r.Words + " " + r.Also)) + " " + string.Join(" ", Hud.Steps.Select(x => x.Command + " " + x.Words))
            + " " + Hud.MenuHint + " " + string.Join(" ", Hud.How.SelectMany(r => r.Lines));
        Check("The menu's words name only real chat commands (ChatCommands.cs: they are back since Over Strike 2)",
            Regex.Matches(menuWords, @"![a-z]+").All(m => ChatCommands.Find(m.Value) != null));
        HudSelfTest(Check);
        FrameSelfTest(Check); // SelfTestFrame.cs: the HUD's frame as the plugin draws it, run against a recorded screen
        CheckSelfTest(Check);
        Os2SelfTest(Check); // SelfTestOS2.cs: reasons, player crashes, our maps
        SettingsSelfTest(Check); // SelfTestSettings.cs: the settings file, else the environment, and the line that says which
        MessagesSelfTest(Check); // SelfTestMessages.cs: the catalogue, the marks, the capitals, the routing, the wire, the views
        PicturesSelfTest(Check); // SelfTestPictures.cs: every mode's maps, the mode filter
        ChatSelfTest(Check); // SelfTestChat.cs: the chat commands' words, the aliases, the fun words, the vote's bare number
        RconSelfTest(Check); // SelfTestRcon.cs: a map command is never sent twice
        StockSelfTest(Check); // SelfTestStock.cs: the discovery stock, the disk, queues.json and the watcher's report
        BallotSelfTest(Check); // SelfTestBallot.cs: the Workshop's numbers, popularity, the kept maps, the points, the tags, the recipe, restarts
        MatchSelfTest(Check);  // SelfTestMatch.cs: a player's restart, Restart match, the match length for the match that is on
        DashSelfTest(Check); // SelfTestDash.cs: the Sync page's block, its words out of the sync's file, the website's data, the layout's labels
        TabSelfTest(Check);
        GuiSelfTest(Check);
        RulesSelfTest(Check); // SelfTestRules.cs: the fun page, the game-changers block, and How it works and Help against the code
        FunCombosSelfTest(Check); // SelfTestFun.cs: every combination of the fun settings
        MapVote.InactivitySeconds = inactivity;
        Check($"The inactivity timer is {MapVote.InactivitySeconds} s", MapVote.InactivitySeconds == 5);
        StaticsSelfTest(Check); // SelfTestHarness.cs: every static is reset by ResetAll or kept by name
        MessagesCapitalsCheck(Check); // last: every message this whole run posted holds the capitals rule
        Console.WriteLine($"local time zone: {TimeZoneInfo.Local.Id} ({DateTime.Now:HH:mm})");
        Console.WriteLine(failed == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({failed})");
        return failed == 0 ? 0 : 1;
    }
}
