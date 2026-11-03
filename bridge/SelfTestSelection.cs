// --selftest, the owner's random-pick rules of 2026-10-22 (Selection.cs): the new-maps setting and its lists, the too-big maps and the
// 1% rule, the too-big vote, sizes.json, and the reset once nobody has been on for 10 minutes. Runs inside the queue harness
// (SelfTestQueue.cs): its handed clock, its recorded RCON, its log lines.
using System.Text.Json;

static partial class Program
{
    static void SelectionSelfTest(Action<string, bool> Check, Action Fresh, Action<(string Name, string Id)> Join, Action<(string Name, string Id), string> Leave,
        Action<(string Name, string Id), string> Talk, Action<double> Run, Func<string, bool> Said, Action Quiet, List<string> sent)
    {
        var (alice, bob) = (("Alice", "[U:1:1001]"), ("Bob", "[U:1:1002]"));
        var realMaps = Maps;

        // ---- sizes.json: the size tool's contract, version 1 ----
        Fresh();
        var sizesPath = Path.Combine(StateDir, "sizes-test.json");
        File.WriteAllText(sizesPath, """
            {"version": 1, "generated": "2026-10-22T12:00:00Z", "boundaries": {"S": 2700, "M": 4900, "L": 9000}, "anchors": {"ar_shoots": 2400},
             "maps": {"7001017": {"map": "big_one", "m2": 15000, "class": "XL", "method": "nav"}, "7001018": {"map": "mid", "m2": 3300.6, "class": "M", "method": "walk"},
                      "7001019": {"map": "unknown", "m2": null, "class": null, "method": "none"}, "ar_baggage": {"map": "ar_baggage", "m2": 2500, "class": "S", "method": "nav"},
                      "7001020": {"map": "odd", "m2": 100, "class": "XXL", "method": "nav"}, "7001021": "not an object"}}
            """);
        var line = ReloadSizes(sizesPath);
        Console.WriteLine("sizes: " + line);
        Check("sizes.json (version 1) loads: every object entry, its class, its area rounded, its method; the boundaries; a class outside S-XL reads as unmeasured",
            Sizes.Count == 5 && Sizes["7001017"] == new SizeEntry(15000, "XL", "nav") && Sizes["7001018"] == new SizeEntry(3301, "M", "walk")
            && Sizes["7001019"] == new SizeEntry(null, null, "none") && Sizes["ar_baggage"].Class == "S" && Sizes["7001020"].Class == null
            && SizeBounds == (2700, 4900, 9000) && line.Contains("1 too big (XL above 9000 m²)"));
        File.WriteAllText(sizesPath, """{"version": 2, "maps": {"7001017": {"class": "XL"}}}""");
        Check("Another version of the contract: nothing is too big by its size", ReloadSizes(sizesPath).Contains("unreadable") && Sizes.Count == 0);
        File.WriteAllText(sizesPath, """{"version": 1, "maps": {""");
        Check("A file that does not parse: nothing is too big by its size", ReloadSizes(sizesPath).Contains("unreadable") && Sizes.Count == 0);
        File.Delete(sizesPath);
        Check("No file at all: nothing is too big by its size", ReloadSizes(sizesPath).Contains("not found") && Sizes.Count == 0);
        Check("The bridge reads sizes.json beside maps.json, the size tool's fixed path (/data/sizes.json on the stack)",
            SizesFile == Path.Combine(Path.GetDirectoryName(Path.GetFullPath(MapsFile))!, "sizes.json") && DataFiles().Any(f => f.Item1 == SizesFile));

        // ---- the fixtures: the original list #1-#1016 and the new maps from #1017, as the real list numbers them ----
        Maps = Enumerable.Range(1, 1040).Select(r => new MapInfo(r, 7_000_000 + r, $"fixture_{r}", 10)).ToList();
        bool New(MapInfo x) => x.Rank >= FirstNewRank;
        List<MapInfo> Rolls(int n) => Enumerable.Range(0, n).Select(_ => RandomPick(true, 1)!).ToList();
        Check($"New maps are list numbers {FirstNewRank} and up; the original list is #1-#{FirstNewRank - 1}", Maps.Count(IsNew) == 24 && !IsNew(Maps[1015]) && IsNew(Maps[1016]));
        Check("The setting starts at always, and a state.json from before it reads as always too",
            new State().NewMaps == NewMapsAlways && JsonSerializer.Deserialize("""{"roundMinutes": 60}""", Ctx.Default.State)!.NewMaps == NewMapsAlways);

        Fresh();
        Check("Always (the default): every random pick is a new map", Rolls(2000).All(New));
        S.NewMaps = NewMapsNever;
        Check("Never: every random pick comes from the original list", Rolls(2000).All(x => !New(x)));
        S.NewMaps = NewMapsHalf;
        var half = Rolls(20000).Count(New) / 20000.0;
        Check($"Half: a fair coin per roll - {half:P1} of 20000 picks were new maps", half is > 0.47 and < 0.53);
        S.NewMaps = "sometimes";
        Check("A setting state.json does not know reads as always", Rolls(500).All(New));
        S.NewMaps = NewMapsAlways;
        Broken = Maps.Where(New).ToDictionary(Key, _ => 3);
        Check("Always, with no new map eligible: the original list rolls instead of nothing", Rolls(500).All(x => x != null && !New(x)));
        Check("...and every such roll says so (the owner's 4c): no new map was left to roll, so this one is from the original list",
            Enumerable.Range(0, 200).All(_ => RandomPick(true, 1) is { } x && !New(x) && RollNote == NoNewMapsNote));
        var allNewBroken = Broken;
        Fresh(); Broken = allNewBroken; Join(alice); Quiet(); Talk(alice, "!next");
        Check("...end to end: Next's news line names the map and ends with that note", Pending is { } fell && !New(fell.Map)
            && NewsNow.Text.StartsWith("Alice rolled the dice: ") && NewsNow.Text.EndsWith(NoNewMapsNote) && NewsNow.Text.Length <= NewsLength);
        Fresh(); Broken = allNewBroken; S.NewMaps = NewMapsHalf;
        var halfRolls = Enumerable.Range(0, 2000).Select(_ => (RandomPick(true, 1)!, RollNote)).ToList();
        Check($"Half with no new map eligible: only the rolls whose coin chose the new maps say so ({halfRolls.Count(r => r.RollNote != "") / 20.0:0}% of 2000)",
            halfRolls.All(r => !New(r.Item1)) && halfRolls.Count(r => r.RollNote == NoNewMapsNote) is > 850 and < 1150 && halfRolls.All(r => r.RollNote is "" or NoNewMapsNote));
        Broken = new();
        Check("A roll from the list the setting chose says nothing more", RandomPick(true, 1) is not null && RollNote == "");
        S.NewMaps = NewMapsAlways;
        Broken = Maps.Where(x => !New(x)).ToDictionary(Key, _ => 3);
        S.NewMaps = NewMapsNever;
        Check("Never, with no original map eligible: the new maps roll instead of nothing", Rolls(500).All(New));
        Check("...and say so: no original map was left to roll, so this one is a new map", RandomPick(true, 1) is { } n2 && New(n2) && RollNote == NoOriginalMapsNote);
        Broken = Maps.ToDictionary(Key, _ => 3);
        Check("Nothing eligible on either list: no roll", RandomPick(true, 1) is null);
        Broken = new(); S.NewMaps = NewMapsAlways;

        // ---- too big: XL in sizes.json, or a too-big vote; 1 random pick in 100 ----
        var xl = Maps.Where(New).Take(4).ToList();
        Sizes = xl.ToDictionary(Key, _ => new SizeEntry(20000, "XL", "nav"));
        Sizes[Key(Maps[1030])] = new SizeEntry(4000, "M", "nav");
        Check("Too big: measured XL; a measured M or an unmeasured map is not", xl.All(TooBig) && !TooBig(Maps[1030]) && !TooBig(Maps[1035]));
        var rolls = Rolls(60000);
        var bigShare = rolls.Count(TooBig) / 60000.0;
        Check($"1 random pick in 100 is a too-big map: {bigShare:P2} of 60000 (4 too big among 24 new maps)", bigShare is > 0.0075 and < 0.0125);
        Check("...and those still come from the list the setting chose", rolls.All(New));
        var winners = rolls.Where(TooBig).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        Check($"...spread over all {xl.Count} too-big maps", xl.All(winners.ContainsKey));
        Sizes = Maps.Where(New).ToDictionary(Key, _ => new SizeEntry(20000, "XL", "nav"));
        Check("Every eligible new map too big: the too-big group rolls every time (the other group is empty)", Rolls(500).All(x => New(x) && TooBig(x)));
        S.Crashes[Key(Maps[1020])] = 1;
        Votes = new() { [Key(Maps[1021])] = new() { ["[U:1:1]"] = 1 } };
        var allBig = Rolls(30000);
        Check("...without its exclusions lifted: a crashed too-big map is never rolled", allBig.All(x => Key(x) != Key(Maps[1020])));
        var counts = allBig.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count() / 30000.0);
        var weights = Maps.Where(x => New(x) && Key(x) != Key(Maps[1020])).ToDictionary(x => x, Weight);
        var worst = weights.Keys.Max(x => Math.Abs(counts.GetValueOrDefault(x) / (weights[x] / weights.Values.Sum()) - 1));
        Check($"...nor its weights changed: within the group every map within 15% of its share, the liked one twice as likely (worst {worst:P1})", worst < 0.15);
        S.History = Maps.Where(New).Take(10).ToList();
        Check("...nor the recent rule: the last 10 maps played wait while others are left in the group", Rolls(1000).All(x => !S.History.Contains(x)));
        S = new State(); Votes = new();
        Sizes = new();
        Votes = new() { [Key(Maps[1025])] = new() { ["[U:1:5]"] = TooBigVote } };
        Check("A too-big vote marks a map too big, and counts as a downvote everywhere a vote counts",
            TooBig(Maps[1025]) && TooBigVotes(Key(Maps[1025])) == 1 && Tally(Key(Maps[1025])) == (0, 1) && Math.Abs(Weight(Maps[1025]) / Weight(Maps[1024]) - 0.25) < 0.01);
        var votedShare = Rolls(60000).Count(x => x == Maps[1025]) / 60000.0;
        Check($"...so it comes up in about 1 random pick in 100 ({votedShare:P2} of 60000), not 1 in 24", votedShare is > 0.0075 and < 0.0125);
        Votes = new(); S = new State();

        // ---- an ads vote (the owner's 15b, 2026-10-24): the map is never a random pick, whatever the list or the group ----
        var marked = Maps[1020];
        Votes = new() { [Key(marked)] = new() { ["[U:1:5]"] = AdsVote } };
        var adsRolls = Rolls(5000);
        Check($"A map with an ads vote is never a random pick: 5000 rolls over the {Maps.Count(New)} new maps, every other one came up, that one never",
            adsRolls.All(x => x != marked) && Maps.Where(x => New(x) && x != marked).All(adsRolls.Contains));
        Sizes = new() { [Key(marked)] = new SizeEntry(20000, "XL", "nav") };
        Check("...not even in the too-big group, where it would be the only map", Rolls(20000).All(x => x != marked));
        Sizes = new();
        Check("An ads vote counts as that player's dislike everywhere a vote counts, and does not make a map too big",
            Tally(Key(marked)) == (0, 1) && !TooBig(marked) && AdsVotes(Key(marked)) == 1 && TooBigVotes(Key(marked)) == 0);
        var adsWeight = Weight(marked);
        Votes[Key(marked)]["[U:1:5]"] = -1;
        Check("...it weighs exactly like a plain dislike", Math.Abs(Weight(marked) - adsWeight) < 1e-12);
        Votes = new(); S = new State();
        Maps = realMaps;

        // ---- the too-big vote, from the board and the cards (!toobig, !toobig <map>) ----
        Fresh();
        Join(alice); Join(bob);
        var small = Maps.Where(x => x.SizeMb < 400).ToList(); // small maps: their notes stay short
        var on = small[30];
        (S.Map, S.LogName, AnnouncePending) = (on, on.Name, false); // the map that is on
        Quiet(); Talk(alice, "!toobig");
        Check("!toobig marks the map that is on too big - that player's dislike - and the note says what it does",
            Votes[Key(on)]["[U:1:1001]"] == TooBigVote && Tally(Key(on)) == (0, 1) && TooBig(on) && Said($"You marked {Label(on)} too big") && Said("1 time in 100"));
        Quiet(); Talk(alice, "!toobig");
        Check("The same again changes nothing and says so", Votes[Key(on)]["[U:1:1001]"] == TooBigVote && TooBig(on) && Said("is down already: too big") && !Said("took back"));
        int VoteOf(string key, string id) => Votes.TryGetValue(key, out var v) ? v.GetValueOrDefault(id) : 0;
        Quiet(); Talk(alice, "!up");
        Check("A Like takes the too-big vote back first (the owner's 2026-11-03), and the mark goes with it: no vote, nothing cast",
            VoteOf(Key(on), "[U:1:1001]") == 0 && !TooBig(on) && Said("Your dislike (too big) of") && Said("like it again to add a like"));
        Quiet(); Talk(alice, "!up");
        Check("...the next Like casts it", VoteOf(Key(on), "[U:1:1001]") == 1 && Said("You upvoted"));
        Talk(bob, "!down"); Quiet(); Talk(bob, "!toobig");
        Check("A dislike turns into a too-big vote", Votes[Key(on)]["[U:1:1002]"] == TooBigVote && Said("changed your vote") && Said("to too big")
            && Said("(1 too big)"));
        Quiet(); Talk(bob, "!down");
        Check("...a plain Dislike keeps that reason and says so", Votes[Key(on)]["[U:1:1002]"] == TooBigVote && Said("is down already: too big"));
        Talk(bob, "!down meh");
        Check("...and !down meh puts it back to the plain one: still one vote per player per map", Votes[Key(on)]["[U:1:1002]"] == -1 && Votes[Key(on)].Count == 2 && !TooBig(on));
        Talk(bob, $"!toobig {small[40].Rank}");
        Check("!toobig <number> marks any map's card", Votes[Key(small[40])]["[U:1:1002]"] == TooBigVote && TooBig(small[40]));
        var catalog = HudCatalog.Decode(BuildCatalog().Encode())!;
        var card = catalog.Maps.First(x => x.Key == Key(small[40]));
        Check("The menu's catalog carries the too-big mark and the vote as a too-big vote (-2)", card.TooBig && card.Big == 1 && card.Down == 1
            && catalog.MyVote(1002, Key(small[40])) == TooBigVote && catalog.MyVote(1002, Key(on)) == -1);
        var oldReader = BuildCatalog().Encode().Split('\n').Where(l => l.StartsWith("v\t1002\t" + Key(small[40]))).Select(l => Math.Sign(long.Parse(l.Split('\t')[3]))).ToList();
        Check("...which an older plugin (it takes the sign) reads as a dislike, and its twelve map fields stay where they were (eighteen since the ads count, twenty since Over Strike 2's ours and reason, twenty-three since the places in each mode)",
            oldReader is [-1] && BuildCatalog().Encode().Split('\n').First(l => l.StartsWith("m\t" + Key(small[40]))).Split('\t') is { Length: 23 } f && f[11] == "1" && f[13] == "1");
        Talk(bob, $"!toobig {small[30].Rank}");
        var state = HudState.Decode(BuildHud(Now()).Encode())!;
        Check("The live state says the too-big votes on the map that is on, and each player's own vote as -2", state.Big == 1 && state.MyVotes[1002] == TooBigVote && state.Down == 1);
        Check("A click may send the too-big vote and the setting, and nothing like them that is free-form",
            new[] { "!toobig", "!toobig 12", "!toobig ar_baggage", "!newmaps always", "!newmaps half", "!newmaps never" }.All(Hud.IsAction)
            && new[] { "!toobig aim", "!toobig 12 13", "!newmaps", "!newmaps sometimes", "!newmaps always; quit", "!newmaps ALWAYS" }.All(x => !Hud.IsAction(x)));
        Votes = new();

        // ---- the ads vote from the board and the cards (!ads, !ads <map>): the owner's 15b ----
        Fresh();
        Join(alice); Join(bob);
        var adsOn = small[50];
        (S.Map, S.LogName, AnnouncePending) = (adsOn, adsOn.Name, false);
        Quiet(); Talk(alice, "!ads");
        Check("!ads marks the map that is on as showing ads - that player's dislike - and the note says what it does",
            Votes[Key(adsOn)]["[U:1:1001]"] == AdsVote && Tally(Key(adsOn)) == (0, 1) && AdsVotes(Key(adsOn)) == 1 && Said($"You marked {Label(adsOn)}: ads") && Said("never a random pick"));
        Quiet(); Talk(alice, "!ads");
        Check("The same again changes nothing and says so", Votes[Key(adsOn)]["[U:1:1001]"] == AdsVote && Said("is down already: ads") && !Said("took back"));
        Quiet(); Talk(alice, "!up");
        Check("A Like takes the ads vote back first, and its mark with it", VoteOf(Key(adsOn), "[U:1:1001]") == 0 && AdsVotes(Key(adsOn)) == 0 && Said("Your dislike (ads) of"));
        Talk(alice, "!up");
        Check("...the next Like casts it", VoteOf(Key(adsOn), "[U:1:1001]") == 1);
        Talk(bob, "!toobig"); Quiet(); Talk(bob, "!ads");
        Check("A too-big vote turns into an ads vote: still one vote per player per map", Votes[Key(adsOn)]["[U:1:1002]"] == AdsVote && Votes[Key(adsOn)].Count == 2
            && Said("changed your vote") && Said("to ads") && Said("(1 ads)"));
        Quiet(); Talk(alice, "!toobig");
        Check("...a too-big vote on a liked map takes the like back first, and says how to mark it", VoteOf(Key(adsOn), "[U:1:1001]") == 0 && Said("!down big again to dislike it: too big"));
        Quiet(); Talk(alice, "!toobig");
        Check("...the next one casts it, and the tally names both kinds", Said("now +0 -2 (1 too big, 1 ads)"));
        var cardMap = small[60];
        Talk(bob, $"!ads {cardMap.Rank}");
        Check("!ads <number> marks any map's card", Votes[Key(cardMap)]["[U:1:1002]"] == AdsVote);
        var adsCatalogText = BuildCatalog().Encode();
        var adsCatalog = HudCatalog.Decode(adsCatalogText)!;
        Check("The menu's catalog carries the ads votes on a map (field 18) and the player's vote as an ads vote (-3)",
            adsCatalog.Maps.First(x => x.Key == Key(cardMap)) is { Ads: 1, Down: 1 } && adsCatalog.MyVote(1002, Key(cardMap)) == AdsVote
            && adsCatalogText.Split('\n').First(l => l.StartsWith("m\t" + Key(cardMap) + "\t")).Split('\t') is { Length: 23 } af && af[17] == "1"); // ads stays field 18
        Check("...which a 3.1.0 plugin (it keeps -2 and takes the sign of the rest) reads as a dislike",
            adsCatalogText.Split('\n').First(l => l.StartsWith("v\t1002\t" + Key(cardMap) + "\t")).Split('\t')[3] is var raw && long.Parse(raw) is var v31 && (v31 == TooBigVote ? v31 : Math.Sign(v31)) == -1);
        var adsState = HudState.Decode(BuildHud(Now()).Encode())!;
        Check("The live state says the ads votes on the map that is on, and each player's own vote as -3", adsState.Ads == 1 && adsState.Big == 1 && adsState.MyVotes[1002] == AdsVote
            && adsState.MyVotes[1001] == TooBigVote && adsState.Down == 2);
        Check("A click may send the ads vote, and nothing like it that is free-form",
            new[] { "!ads", "!ads 12", "!ads ar_baggage" }.All(Hud.IsAction) && new[] { "!ads aim", "!ads 12 13", "!ads 12;quit", "!adsx" }.All(x => !Hud.IsAction(x)));
        Quiet(); Talk(alice, $"!map {cardMap.Rank}");
        Check("Players can still pick a map marked ads: a pick is a pick", Pending is { } picked && picked.Map == cardMap && Said("Alice picked"));
        Votes = new();

        // ---- a downvote's reason (DownReasons, the owner's decision of 2026-11-01): chat steers it directly, and it is kept per vote ----
        Fresh();
        Join(alice); Join(bob);
        var whyOn = small[70];
        var whyKey = Key(whyOn);
        (S.Map, S.LogName, AnnouncePending) = (whyOn, whyOn.Name, false);
        const string aliceId = "[U:1:1001]", bobId = "[U:1:1002]";
        Check("The reasons: the plain one first, then too big and ads, each value a downvote the codecs carry; words plain, no number among them",
            DownReasons.All[0] is { Word: "meh", Value: DownReasons.Plain, Label: "I did not like the map" } && DownReasons.All[1].Value == TooBigVote && DownReasons.All[1].Label == "Map too big"
            && DownReasons.All[2].Value == AdsVote && DownReasons.All[2].Label == "Shows ads or sends players away"
            && DownReasons.All.Select(r => r.Value).Distinct().Count() == DownReasons.All.Length && DownReasons.All.All(r => r.Value < 0 && HudCatalog.Carried(r.Value) == r.Value)
            && DownReasons.All.All(r => r.Word.All(char.IsAsciiLetterLower) && !Hud.IsMapArg(r.Word)) && DownReasons.Words == "big, ads, small, broken or slow");
        Quiet(); Talk(alice, "!down");
        Check("A plain !down counts at once as 'I did not like the map', and its note says how to give a reason",
            Votes[whyKey][aliceId] == DownReasons.Plain && Tally(whyKey) == (0, 1) && Said("You downvoted") && Said("Why? !down big, ads, small, broken or slow"));
        Quiet(); Talk(alice, "!down big");
        Check("!down big: that vote, now too big, as !toobig leaves it - no second vote", Votes[whyKey][aliceId] == TooBigVote && Votes[whyKey].Count == 1 && TooBig(whyOn) && TooBigVotes(whyKey) == 1 && Said("changed your vote") && Said("to too big"));
        Quiet(); Talk(alice, "!down big");
        Check("...the same reason again changes nothing and says so", Votes[whyKey][aliceId] == TooBigVote && Tally(whyKey) == (0, 1) && Said("already") && !Said("took back"));
        Quiet(); Talk(alice, "!down ads");
        Check("!down ads: as !ads leaves it - out of random picks, and no longer too big", Votes[whyKey][aliceId] == AdsVote && AdsVotes(whyKey) == 1 && !TooBig(whyOn) && Tally(whyKey) == (0, 1) && Said("to ads"));
        foreach (var r in DownReasons.All.Where(r => r.Value < AdsVote))
        {
            Quiet(); Talk(alice, $"!down {r.Word}");
            Check($"!down {r.Word}: kept with the vote, a dislike and nothing more ({r.Label})",
                Votes[whyKey][aliceId] == r.Value && Tally(whyKey) == (0, 1) && !TooBig(whyOn) && AdsVotes(whyKey) == 0 && Math.Abs(Weight(whyOn) - Weight(new MapInfo(whyOn.Rank, 1, "unvoted", 1)) * 0.25) < 1e-9 && Said($"to {r.Short}"));
        }
        Quiet(); Talk(alice, "!down meh");
        Check("!down meh puts a reason back to the plain one", Votes[whyKey][aliceId] == DownReasons.Plain && Tally(whyKey) == (0, 1));
        Quiet(); Talk(alice, "!down");
        Check("...and a plain !down on a plain downvote changes nothing and says it is down already", Votes[whyKey][aliceId] == DownReasons.Plain && Said("is down already") && !Said("took back"));
        Quiet(); Talk(alice, "!up");
        Check("The opposite vote takes it back first: no vote, and the note says how to like it", VoteOf(whyKey, aliceId) == 0 && Tally(whyKey) == (0, 0) && Said("Your dislike of") && Said("like it again to add a like"));
        Quiet(); Talk(alice, "!up");
        Check("...the next Like casts it", VoteOf(whyKey, aliceId) == 1 && Tally(whyKey) == (1, 0) && Said("You upvoted"));
        Quiet(); Talk(alice, "!up");
        Check("...and the same vote again says it is up already", Votes[whyKey][aliceId] == 1 && Said("is up already"));
        Quiet(); Talk(alice, "!down big");
        Check("A reason on a liked map takes the like back first and says how to dislike it with that reason: nothing cast, no question",
            VoteOf(whyKey, aliceId) == 0 && Said("Your like of") && Said("!down big again to dislike it: too big") && !WhyOpen.ContainsKey(aliceId) && !TooBig(whyOn));
        Quiet(); Talk(alice, "!down big");
        Check("...the same words again cast it", VoteOf(whyKey, aliceId) == TooBigVote && TooBig(whyOn) && Said("too big"));
        Talk(alice, "!up"); Talk(alice, "!up");
        Check("...and two Likes step it back up: none, then a like", VoteOf(whyKey, aliceId) == 1 && !TooBig(whyOn));
        var whyCard = small[71];
        Quiet(); Talk(bob, $"!down {whyCard.Rank} big");
        Check("!down <number> big: a vote on that map with its reason at once, in !toobig's words", Votes[Key(whyCard)][bobId] == TooBigVote && Said($"You marked {Label(whyCard)} too big") && !Said("Why?"));
        Quiet(); Talk(bob, $"!down {whyCard.Rank} slow");
        Check("...and another reason changes it", Votes[Key(whyCard)][bobId] == HudCatalog.SlowVote && Votes[Key(whyCard)].Count == 1 && Said("to runs badly"));
        Quiet(); Talk(bob, "!down small");
        Check("!down small with no vote yet: a new vote with that reason, and no question", Votes[whyKey][bobId] == HudCatalog.SmallVote && Said($"You disliked {Label(whyOn)}: too small") && !Said("Why?"));
        var whyCatalog = HudCatalog.Decode(BuildCatalog().Encode())!;
        var whyState = HudState.Decode(BuildHud(Now()).Encode())!;
        Check("The catalog and the live state carry the reason; the board says it; the card lights Dislike; an older plugin's sign reads a dislike",
            whyCatalog.MyVote(1002, Key(whyCard)) == HudCatalog.SlowVote && whyState.MyVotes[1002] == HudCatalog.SmallVote && whyState.Down == 1
            && HudView.PlayerTexts(whyState, 1002, "Bob", false).Single(x => x.Id == "gg_b_you").Text == "You disliked this map: too small"
            && HudView.PlayerClasses(whyState, 1002).Single(x => x.Class == "voted-down").On && !HudView.PlayerClasses(whyState, 1002).Single(x => x.Class == "voted-big").On
            && Math.Sign(whyState.MyVotes[1002]) == -1);
        var whySaved = Votes[Key(whyCard)][bobId];
        LoadVotes();
        Check("The reason is in votes.json: it reads back with the vote", Votes[Key(whyCard)][bobId] == whySaved && HealthText().Contains("down votes: ") && HealthText().Contains("1 too small") && HealthText().Contains("1 runs badly"));
        Check("A click may say a reason, with or without its map, and nothing like it that is free-form",
            new[] { "!down big", "!down ads", "!down meh", "!down 12 big", "!down 3084291314 slow", "!down ar_baggage small" }.All(Hud.IsAction)
            && new[] { "!down bad", "!down big 12", "!down 12 13", "!down 12 big x", "!down aim", "!up big", "!toobig big", "!down BIG", "!down  big", "!down big;quit" }.All(x => !Hud.IsAction(x))
            && HudAct($"{aliceId}\tAlice\t!down {whyCard.Rank} ads") && Votes[Key(whyCard)][aliceId] == AdsVote && !HudAct($"{aliceId}\tAlice\t!down {whyCard.Rank} rubbish"));
        Check("What chat may say after !down, as the bridge takes it: a map, a reason, or both in either order",
            DownReasons.Normal("") == "" && DownReasons.Normal("12") == "12" && DownReasons.Normal("big") == "big" && DownReasons.Normal("12 big") == "12 big" && DownReasons.Normal("big 12") == "12 big"
            && DownReasons.Normal("rubbish") == null && DownReasons.Normal("12 13") == null && DownReasons.Split("12 big") is ("12", { Word: "big" }) && DownReasons.Split("big") is ("", { Word: "big" }) && DownReasons.Split("12") is ("12", null) && DownReasons.Split("") is ("", null));
        var chatDown = ChatCommands.Parse("!thumbsdown ads 12");
        Check("In chat: !down big, !down 12 big and any alias become the click the bridge takes; a word it does not know gets the usage line",
            ChatCommands.Parse("!down big") is { Kind: ChatCommands.Kind.Bridge, Arg: "!down big" } && ChatCommands.Parse("!down 12 big") is { Arg: "!down 12 big" } && chatDown is { Kind: ChatCommands.Kind.Bridge, Arg: "!down 12 ads" }
            && ChatCommands.Parse("!down rubbish") is { Kind: ChatCommands.Kind.Usage, Name: "!down" } && Hud.IsAction(chatDown.Arg) && ChatCommands.UsageOf("!down").Contains(DownReasons.Words));
        var fullNote = $"You downvoted {Clip(new string('w', 64), PlainVoteLabel)}, now +99 -99 (99 too big, 99 ads). Why? !down {DownReasons.Words}";
        Check($"The plain downvote's note fits the note line with a long name and a long tally ({fullNote.Length} of {NoticeLength})", fullNote.Length <= NoticeLength);
        Votes = new();

        // ---- the popup: a plain Dislike opens it, a reason, its time or a map change closes it; the vote counted all along ----
        Fresh();
        Join(alice); Join(bob);
        var askOn = small[72];
        var askCard = small[73];
        var askOther = small[74];
        (S.Map, S.LogName, AnnouncePending) = (askOn, askOn.Name, false);
        HudState Live() => HudState.Decode(BuildHud(Now()).Encode())!;
        bool Cls(HudState s, long a, string c) => HudView.PlayerClasses(s, a).Single(x => x.Class == c).On;
        Talk(alice, "!down");
        var asking = Live();
        Check($"A plain Dislike counts at once, and that player's popup asks about that map for {DownReasons.PopupSeconds} s; nobody else's opens; the line under it is not running yet",
            Votes[Key(askOn)][aliceId] == DownReasons.Plain && asking.Why[1001] == (Clip(Label(askOn), 40), (int)DownReasons.PopupSeconds) && !asking.Why.ContainsKey(1002)
            && Cls(asking, 1001, "why") && !Cls(asking, 1001, "why-run") && !Cls(asking, 1002, "why"));
        Run(2);
        Check("...two seconds on the line runs, and the seconds count down", Live().Why[1001].For == (int)DownReasons.PopupSeconds - 2 && Cls(Live(), 1001, "why") && Cls(Live(), 1001, "why-run"));
        Quiet();
        Check("A click on a reason updates that vote: no second vote, and the popup closes", HudAct($"{aliceId}\tAlice\t{DownReasons.PopupVerb} big") && Votes[Key(askOn)][aliceId] == TooBigVote
            && Votes[Key(askOn)].Count == 1 && Tally(Key(askOn)) == (0, 1) && TooBig(askOn) && !Live().Why.ContainsKey(1001) && !Cls(Live(), 1001, "why") && Said("to too big"));
        Quiet(); Talk(alice, "!down");
        Check("A plain Dislike on a reason keeps it, says so and does not ask again", Votes[Key(askOn)][aliceId] == TooBigVote && !Live().Why.ContainsKey(1001) && Said("is down already: too big"));
        Quiet(); Talk(alice, "!up");
        Check("A Like takes it back and asks nothing", VoteOf(Key(askOn), aliceId) == 0 && !Live().Why.ContainsKey(1001) && !Cls(Live(), 1001, "why"));
        Talk(alice, "!down");
        Check("...and a Dislike after that is a plain one that asks again", Votes[Key(askOn)][aliceId] == DownReasons.Plain && Live().Why.ContainsKey(1001));
        Run(2);
        var whyLeft = Live().Why[1001].For;
        Quiet(); Talk(alice, "!down");
        Check("A second Dislike changes nothing, and the popup goes on running: the same vote, the same seconds left", Votes[Key(askOn)][aliceId] == DownReasons.Plain && Live().Why[1001].For == whyLeft && Cls(Live(), 1001, "why") && Said("is down already") && !Said("took back"));
        Quiet(); Talk(alice, "!down ads");
        Check("While it is open the same words set the reason, with no second vote, and close it", Votes[Key(askOn)][aliceId] == AdsVote && Votes[Key(askOn)].Count == 1 && !Live().Why.ContainsKey(1001));
        Talk(alice, $"!down {askCard.Rank}");
        var onCard = Live();
        Check("A card's down arrow asks about that card's map, not the map that is on", onCard.Why[1001].Map == Clip(Label(askCard), 40) && Votes[Key(askCard)][aliceId] == DownReasons.Plain && Votes[Key(askOn)][aliceId] == AdsVote);
        Quiet(); Talk(alice, $"!down {askOther.Rank} big");
        Check("...a reason that names another map goes there and leaves the question open", Votes[Key(askOther)][aliceId] == TooBigVote && Live().Why[1001].Map == Clip(Label(askCard), 40));
        Quiet(); Talk(alice, "!down big");
        Check("...and the words with no map go to the map it asks about", Votes[Key(askCard)][aliceId] == TooBigVote && Votes[Key(askOn)][aliceId] == AdsVote && !Live().Why.ContainsKey(1001));
        Talk(alice, $"!up {askCard.Rank}"); Talk(alice, $"!down {askCard.Rank}");
        var whyAgain = Live().Why.ContainsKey(1001);
        Talk(alice, $"!up {askCard.Rank}");
        Check("...any other vote on that map closes it - a Like takes the dislike back", whyAgain && VoteOf(Key(askCard), aliceId) == 0 && !Live().Why.ContainsKey(1001));
        var noAsk = new[] { "!up", "!toobig", "!ads", "!down small", $"!down {askOther.Rank} slow" };
        var asked = false;
        foreach (var said in noAsk) { Talk(bob, said); asked |= Live().Why.ContainsKey(1002); }
        Check($"No popup when a reason is given, or for any other vote: {string.Join(", ", noAsk)}", !asked);
        Votes = new();
        Talk(alice, "!down"); Run(DownReasons.PopupSeconds + 0.5);
        Check("Its time up, it leaves the screen, and the vote stays 'I did not like the map'", !Live().Why.ContainsKey(1001) && Votes[Key(askOn)][aliceId] == DownReasons.Plain);
        HudAct($"{aliceId}\tAlice\t{DownReasons.PopupVerb} slow");
        Check("...a click already on its way still lands", Votes[Key(askOn)][aliceId] == HudCatalog.SlowVote);
        Votes = new();
        Talk(alice, "!down"); Run(DownReasons.PopupSeconds + DownReasons.PopupGraceSeconds + 1); Quiet();
        HudAct($"{aliceId}\tAlice\t{DownReasons.PopupVerb} big");
        Check("...a later one changes nothing, says so, and names the words that still work", Votes[Key(askOn)][aliceId] == DownReasons.Plain && Said("That question has passed") && Said("!down big"));
        Check("A click may say !reason with a reason, and nothing else", new[] { "!reason big", "!reason meh", "!reason slow" }.All(Hud.IsAction) && new[] { "!reason", "!reason x", "!reason big 12", "!reason 12", "!reason BIG" }.All(x => !Hud.IsAction(x)));
        Talk(alice, "!toobig"); Talk(alice, "!down meh"); Talk(alice, "!up"); Talk(alice, "!down");
        var carried = Live();
        var whyLine = BuildHud(Now()).Encode().Split('\n').Single(l => l.StartsWith("why\t"));
        Check("The live state's line for it: an older plugin skips a line it does not know, and an expired question is not sent",
            whyLine.Split('\t').Length == 4 && whyLine.StartsWith("why\t1001\t") && carried.Why.Count == 1 && !new HudState { Why = new() { [1] = ("x", 0) } }.Encode().Contains("why\t")
            && HudState.Decode(HudState.Magic + "\nwhy\tx\t3\tm\nwhy\t5\t0\tm\nwhy\t6\t3\tm\n")!.Why.Keys.SequenceEqual([6L]));
        OnMapStart("some_other_map");
        Check("A map change closes it: nothing on screen survives one, and the vote stays", !Live().Why.ContainsKey(1001) && Votes[Key(askOn)][aliceId] == DownReasons.Plain);
        Votes = new();

        // ---- the setting: anybody switches it, for everybody, and the news line says who ----
        Fresh();
        Join(alice); Join(bob);
        Quiet(); Talk(alice, "!newmaps half");
        Check("!newmaps half: switched for everybody, on the news line with who did it, and kept in state.json",
            S.NewMaps == NewMapsHalf && NewsNow.Text.StartsWith("Alice set new maps in random picks to half")
            && JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(StateDir, "state.json")), Ctx.Default.State)!.NewMaps == NewMapsHalf);
        Quiet(); Talk(bob, "!newmaps half");
        Check("The same again: a note that it is so already, nothing on the news line", S.NewMaps == NewMapsHalf && Said("already") && NewsNow.Text == "");
        Quiet(); Talk(bob, "!newmaps sometimes");
        Check("Anything else: a note naming the three", S.NewMaps == NewMapsHalf && Said("always, half or never"));
        Talk(bob, "!newmaps never");
        Check("The live state carries it for the Game page", HudState.Decode(BuildHud(Now()).Encode())!.NewMaps == NewMapsNever);

        // ---- the reset: nobody on for more than 10 minutes, and the setting and the grenade drops go back to their defaults ----
        Quiet();
        Leave(alice, "NETWORK_DISCONNECT_DISCONNECT_BY_USER"); Leave(bob, "NETWORK_DISCONNECT_DISCONNECT_BY_USER");
        Run(ResetEmptyMinutes * 60 - 5);
        Check($"Nobody on for just under {ResetEmptyMinutes} min: nothing is reset yet", S.NewMaps == NewMapsNever && !sent.Contains(NadesDefaultCommand));
        Run(70);
        Check($"Past {ResetEmptyMinutes} min: the setting is back to always, the grenade drops on (gg_nades on over RCON), and the fun page's changes undone (gg_fun reset)",
            S.NewMaps == NewMapsAlways && sent.Count(x => x == NadesDefaultCommand) == 1 && sent.Contains("status")
            && sent.Count(x => x == FunResetCommand) == 1 && FunResetCommand == "gg_fun reset \"the server\""
            && JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(StateDir, "state.json")), Ctx.Default.State)!.NewMaps == NewMapsAlways);
        Run(1800);
        Check("...once per empty spell, however long it lasts", sent.Count(x => x == NadesDefaultCommand) == 1);
        Join(alice); Talk(alice, "!newmaps never"); Run(5); Leave(alice, "NETWORK_DISCONNECT_DISCONNECT_BY_USER");
        Quiet(); Run(ResetEmptyMinutes * 60 + 70);
        Check("Somebody coming and going starts a new spell, with its own reset", S.NewMaps == NewMapsAlways && sent.Count(x => x == NadesDefaultCommand) == 1);

        Fresh();
        Join(alice); Talk(alice, "!newmaps half");
        Leave(alice, "NETWORK_DISCONNECT_SHUTDOWN"); // a map change: reloading, which counts as connected for the 10-minute load grace
        Quiet(); Run(ReloadGraceSeconds + ResetEmptyMinutes * 60 - 30);
        Check("A player reloading a map is on the server for the load grace: the 10 minutes start only after it", S.NewMaps == NewMapsHalf && !sent.Contains(NadesDefaultCommand));
        Run(100);
        Check("...and then the reset comes", S.NewMaps == NewMapsAlways && sent.Contains(NadesDefaultCommand));

        Fresh();
        var harness = Send;
        Send = c => harness(c) is var r && c == "status" ? "players  : 2 humans, 0 bots (0 max) (not hibernating) (unreserved)\n" : r; // recorded like every command
        S.NewMaps = NewMapsNever;
        Quiet(); Run(ResetEmptyMinutes * 60 + 200); // a fresh bridge that never saw anybody join, on a server where two are playing
        Check("The log stream saw nobody but RCON status counts humans (a bridge started mid-map): no reset", S.NewMaps == NewMapsNever && !sent.Contains(NadesDefaultCommand) && sent.Contains("status"));
        Send = c => c == "status" ? null : harness(c);
        Quiet(); Run(300);
        Check("RCON not answering: no reset either", S.NewMaps == NewMapsNever && !sent.Contains(NadesDefaultCommand));
        Send = harness;
        Run(70);
        Check("...until status answers with nobody on", S.NewMaps == NewMapsAlways && sent.Contains(NadesDefaultCommand));
        Check("GET /health says the setting and the sizes", HealthText().Contains("random picks: new maps always") && HealthText().Contains("sizes 0 measured"));
        Maps = realMaps; Votes = new(); S = new State(); Sizes = new();

        // ---- the How it works page says what the code does: every number in it is one of the bridge's constants ----
        var how = string.Join(" ", Hud.How.SelectMany(r => r.Lines.Prepend(r.Title)));
        var odds = (int)Math.Round(1 / TooBigChance);
        var claims = new (string Words, bool True)[]
        {
            ($"the new maps, #{FirstNewRank} and up", true), ($"the original #1-#{FirstNewRank - 1}", true), ("Always - ", NewMapsModes[0] == NewMapsAlways && DefaultNewMaps == NewMapsAlways),
            ("Half - a coin flip per pick", NewMapsModes.Contains(NewMapsHalf)), ("Never - the original", NewMapsModes.Contains(NewMapsNever)),
            ($"{ResetEmptyMinutes} minutes with nobody on: back to Always", true), ($"1 random pick in {odds} takes a too-big map", Math.Abs(odds * TooBigChance - 1) < 1e-9),
            ($"the other {odds - 1} one that is not; an empty group hands the pick to the other", true), ("measured XL, or marked Too big by a player", true),
            ("hands the pick to the other list", true),
            ($"Workshop rank gives up to {TopRankBoost:0}x", TopRankBoost == Math.Round(TopRankBoost)), ("each Like doubles a map's odds", true), ($"up to {UpvoteCap} Likes", true),
            ("each Dislike or Too big quarters them", DownvoteFactor == 0.25 && TooBigVote < 0), ($"the last {RecentCount} maps played (while others are left)", true),
            ("a map's download size never makes it more or less likely", true), // the dice test in part one rolls maps of 400 MB and up
            ("hands the pick to the other list, and the news line says so", NoNewMapsNote.Contains("original list") && NoOriginalMapsNote.Contains("new map")),
            ($"Up to {MaxRequestsEach} requests each", true), ("gone over a minute while others wait", DisconnectGraceSeconds == 60),
            ("a map two or more players asked for goes first", true), ("A map a player picked or queued is never random", true),
        };
        var gameLines = string.Join(" ", NewMapsModes.Select(HudMenu.NewMapsLine));
        Check("The Game page's line under the setting names the same lists and the same reset as the bridge",
            NewMapsModes.All(m => HudMenu.NewMapsLine(m).StartsWith(char.ToUpperInvariant(m[0]) + m[1..] + ": ")) && HudMenu.NewMapsLine("") == ""
            && gameLines.Contains($"#{FirstNewRank} and up") && gameLines.Contains($"#1-#{FirstNewRank - 1}") && gameLines.Contains($"Back to Always after {ResetEmptyMinutes} minutes with nobody on")
            && HudMenu.NewMapsLine(NewMapsHalf).Contains("coin flip") && HudMenu.NewMapsLine(NewMapsNever).Contains("only the original"));
        var wrong = claims.Where(c => !how.Contains(c.Words) || !c.True).Select(c => c.Words).ToList();
        Check($"How it works says what the code does: {claims.Length} claims, each number the bridge's own constant{(wrong.Count > 0 ? " - wrong or missing: " + string.Join(" | ", wrong) : "")}", wrong.Count == 0);
        Check($"How it works fits its page: {Hud.How.Length} sections of at most {Hud.HowLines} lines of at most 160 characters (longest {Hud.How.SelectMany(r => r.Lines).Max(l => l.Length)})",
            Hud.How.Length <= 5 && Hud.How.All(r => r.Lines.Length is > 0 and <= Hud.HowLines && r.Title.Length <= 40) && Hud.How.SelectMany(r => r.Lines).All(l => l.Length <= 160)
            && !System.Text.RegularExpressions.Regex.IsMatch(how, @"![a-z]"));
    }
}
