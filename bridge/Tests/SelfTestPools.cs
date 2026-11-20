// --selftest, the official maps' pools (OfficialPools.cs): the installed game's own as the restart watcher reports them, Modes.cs's tables while there
// is none, our extras when their files are installed, Valve's names as search words, and a changelevel the server answers with "invalid map name" -
// given up at once, out of every pool until the server process is a new one. Played through the HTTP body, the catalog, the dice and the search as the
// plugin sees them.
static partial class Program
{
    static void PoolsSelfTest(Action<string, bool> Check)
    {
        var h = new Harness("pools", new DateTime(2026, 11, 19, 20, 0, 0, DateTimeKind.Utc));
        ModeMapsDir = h.Dir;
        var alice = ("Alice", "[U:1:4101]");
        try
        {
            List<string> Names(Modes.Mode m) => [.. OfficialsOf(m).Select(o => o.Name)];
            bool Same(IEnumerable<string> a, IEnumerable<string> b) => a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
            var dm = Modes.Deathmatch.Official;
            var wm = Modes.Wingman.Official;
            var ar = Officials.Select(o => o.Name).ToList();
            var all = dm.Concat(wm).Concat(ar).Append("de_ancient").Distinct().ToList();
            // a report as the restart watcher sends it (BridgeWire.PoolsBody): Arms Race's, Deathmatch's and Wingman's game modes, and one more we do not play
            static string Report(string build, IEnumerable<string> arPool, IEnumerable<string> dmPool, IEnumerable<string> dmMissing, IEnumerable<string> wmPool, IEnumerable<string> installed,
                params (string Map, string Name)[] names) =>
                $"os2-pools\t1\t{build}\npool\tgungame\tgungameprogressive\t1\t0\t{string.Join(',', arPool)}\t\npool\tgungame\tdeathmatch\t1\t2\t{string.Join(',', dmPool)}\t{string.Join(',', dmMissing)}\n"
                + $"pool\tclassic\tscrimcomp2v2\t0\t2\t{string.Join(',', wmPool)}\t\npool\tclassic\tcasual\t0\t0\tde_dust2,de_casualonly\t\ninstalled\t{string.Join(',', installed)}\n"
                + string.Concat(names.Select(n => $"name\t{n.Map}\t{n.Name}\n"));
            HudSearch Served() => HudSearch.Decode(SearchText)!;
            HudCatalog Catalog() { RefreshCatalog(); return HudCatalog.Decode(CatalogText)!; }
            List<string> Found(string query)
            {
                RefreshCatalog();
                return [.. new HudBrowser { Query = query }.Matches(HudCatalog.Decode(CatalogText)!, 0, Served()).Select(m => m.Key)];
            }

            // ---- no report: Modes.cs's tables, our extra ----
            h.Fresh();
            Check($"Pools without a report: Deathmatch plays Modes.cs's {dm.Length} and our extra de_ancient, Wingman Modes.cs's {wm.Length}, Arms Race the settings' officials, and GET /health says so ({PoolsHealth()})",
                Names(Modes.Deathmatch).SequenceEqual(dm.Append("de_ancient")) && Names(Modes.Wingman).SequenceEqual(wm) && Names(Modes.ArmsRace).SequenceEqual(ar)
                && PoolsHealth() == $"pools: no report of the game's own yet - armsrace {ar.Count} from the settings' officials; deathmatch {dm.Length} from Modes.cs, ours too: de_ancient; wingman {wm.Length} from Modes.cs"
                && HealthText().Contains(PoolsHealth()));
            Check("...de_ancient, Ancient by day, is a Deathmatch map of the catalog like Valve's own, rolled by Deathmatch's dice, and nobody else's",
                Enumerable.Range(0, 3000).Select(_ => RandomPick(Modes.Deathmatch)?.Name).Contains("de_ancient") && Found("de_ancient").Contains("de_ancient")
                && Catalog().Maps.SingleOrDefault(m => m.Key == "de_ancient") is { Official: true, RankDm: 0, RankWm: -1 } && !Names(Modes.Wingman).Contains("de_ancient"));
            Check($"Valve's names are search words of the official maps (Modes.ValveNames without a report): cobblestone finds de_cbble besides any Workshop port, office cs_office, "
                  + $"dust ii de_dust2, ancient both Ancients ({string.Join(", ", Found("cobblestone").Take(8))})",
                Found("cobblestone").Contains("de_cbble") && Found("office").Contains("cs_office") && Found("dust ii").Contains("de_dust2")
                && Found("ancient").Contains("de_ancient") && Found("ancient").Contains("de_ancient_night") && Served().TextOf("de_cbble") == "cobblestone deathmatch wingman"
                && Served().TextOf("ar_baggage") == "baggage armsrace" && Served().TextOf("de_ancient") == "ancient deathmatch");

            // ---- the game's report, the same pools as Modes.cs's ----
            var answer = TakePools(Report("1.41.9.9", ar, dm, [], wm, all));
            Check($"A report of the game's own pools equal to Modes.cs's: taken, kept in game-pools.txt, the same pools played; the answer and the log compare them ({answer})",
                answer == $"ok: CS2 1.41.9.9 - armsrace {ar.Count} = the settings' officials; deathmatch {dm.Length} = Modes.cs, ours too: de_ancient; wingman {wm.Length} = Modes.cs"
                && File.Exists(GamePoolsFile) && Names(Modes.Deathmatch).SequenceEqual(dm.Append("de_ancient")) && Same(Names(Modes.Wingman), wm));

            // ---- a build that adds a map, drops one from its template and names one whose files are gone ----
            var version = CatalogVersion;
            var newDm = dm.Where(n => n != "de_cache").Prepend("de_returning").Append("de_gone").ToList();
            answer = TakePools(Report("1.41.10.0", ar, newDm, ["de_gone"], wm, all.Append("de_returning"), ("de_returning", "Returning"), ("de_cbble", "Cobblestone")));
            Check($"A build whose Deathmatch template adds de_returning, drops de_cache and names de_gone without its files: the game's pool is played - de_returning in, the other two out ({answer})",
                answer == $"ok: CS2 1.41.10.0 - armsrace {ar.Count} = the settings' officials; deathmatch {dm.Length} from the game, not in Modes.cs: de_returning, only in Modes.cs: de_cache, named but not installed: de_gone, ours too: de_ancient; wingman {wm.Length} = Modes.cs"
                && Names(Modes.Deathmatch).Contains("de_returning") && !Names(Modes.Deathmatch).Contains("de_cache") && !Names(Modes.Deathmatch).Contains("de_gone")
                && Names(Modes.Deathmatch).Contains("de_ancient") && CatalogVersion > version);
            var rolls = Enumerable.Range(0, 3000).Select(_ => RandomPick(Modes.Deathmatch)?.Name).ToList();
            Check("...a returning map by itself: rolled by Deathmatch's dice, an official Deathmatch map of the catalog, found by Valve's name; the maps out of the pool are rolled never",
                rolls.Contains("de_returning") && !rolls.Contains("de_cache") && !rolls.Contains("de_gone")
                && Catalog().Maps.SingleOrDefault(m => m.Key == "de_returning") is { Official: true, RankDm: 0 } && Found("returning").Contains("de_returning")
                && Served().TextOf("de_returning") == "returning deathmatch" && Catalog().Maps.All(m => m.Key != "de_gone" && m.Key != "de_cache")
                && HealthText().Contains("not in Modes.cs: de_returning, only in Modes.cs: de_cache"));
            Check("...the report kept: a bridge that starts again plays it from game-pools.txt, as reported",
                (GameReport = null) is null && LoadGamePools().StartsWith("pools: CS2 1.41.10.0's own, as reported", StringComparison.Ordinal) && Names(Modes.Deathmatch).Contains("de_returning"));

            // ---- our extra without its files, a mode the report lacks, a report that is none ----
            TakePools(Report("1.41.10.1", ar, dm, [], wm, all.Where(n => n != "de_ancient")));
            Check("Our extra joins only with its files installed: without de_ancient's, Deathmatch has Valve's pool alone, and the comparison says so",
                Names(Modes.Deathmatch).SequenceEqual(dm) && PoolDifferences()[1] == $"deathmatch {dm.Length} = Modes.cs, ours not installed: de_ancient");
            TakePools($"os2-pools\t1\t1.41.10.2\npool\tgungame\tgungameprogressive\t1\t0\t{string.Join(',', ar)}\t\ninstalled\t{string.Join(',', all.Where(n => n != "de_vertigo"))}\n");
            Check("A report without a mode's pool leaves that mode on Modes.cs's table - only what is installed of it",
                Names(Modes.Deathmatch).SequenceEqual(dm.Where(n => n != "de_vertigo").Append("de_ancient")) && Names(Modes.Wingman).SequenceEqual(wm.Where(n => n != "de_vertigo"))
                && PoolDifferences()[2] == $"wingman: none in the game's report - {wm.Length} from Modes.cs");
            var before = GameReport;
            Check("A body that is no os2-pools 1 report with a pool is refused, and the pools stay as they were",
                TakePools("os2-pools\t1\t9\ninstalled\tde_dust2\n").StartsWith("refused:", StringComparison.Ordinal) && TakePools("os2-ready\t1\n").StartsWith("refused:", StringComparison.Ordinal)
                && ReferenceEquals(GameReport, before));
            TakePools(Report("1.41.10.3", ar.Append("ar_newone"), dm, [], wm, all.Append("ar_newone")));
            Check($"Arms Race keeps the settings' officials whatever the game's pool says - compared and shown only ({PoolDifferences()[0]})",
                Names(Modes.ArmsRace).SequenceEqual(ar) && PoolDifferences()[0] == $"armsrace {ar.Count} from the settings' officials, which stay - the game's {ar.Count + 1}, not in the settings' officials: ar_newone");

            // ---- the server says it has no such map ----
            h.Fresh();
            S.Mode = Modes.ArmsRaceId;
            h.Join(alice);
            h.Quiet();
            h.Server = c => c.StartsWith("changelevel de_cbble", StringComparison.Ordinal) ? "changelevel de_cbble:  invalid map name" : "";
            StartChange(new Change(new MapInfo(0, 0, "de_cbble", 0, true, Modes.DeathmatchId), "the vote"), "A test's change");
            Check($"A changelevel the server answers with \"invalid map name\" is given up at once: nothing awaited, the mode back to Arms Race, the map out of every pool, said so ({string.Join(" | ", h.Sent)})",
                !LoadPending && Pending == null && S.Map == null && S.Mode == Modes.ArmsRaceId && h.Sent[^1] == Modes.PrepareLine(Modes.ArmsRace)
                && Unloadable.Contains("de_cbble") && !Names(Modes.Deathmatch).Contains("de_cbble") && !Names(Modes.Wingman).Contains("de_cbble")
                && h.Said("de_cbble (official) is not on the server - It was not loaded - Start a vote for another map") && PoolsHealth().EndsWith("; refused by the server this process: de_cbble", StringComparison.Ordinal));
            h.Run(LoadMinutes * 60 + 5);
            Check("...so no ten-minute wait follows (no load timeout), and the dice and the catalog never offer it again in this process",
                !Told.Any(t => t.Contains("never finished loading", StringComparison.Ordinal)) && Enumerable.Range(0, 2000).All(_ => RandomPick(Modes.Deathmatch)?.Name != "de_cbble")
                && Catalog().Maps.All(m => m.Key != "de_cbble"));
            h.Server = c => c.StartsWith("changelevel ", StringComparison.Ordinal) ? $"{c}:  invalid map name" : "";
            h.Quiet();
            var rolled = AutoRoll("A test's roll", Modes.Deathmatch);
            Check($"An automatic change the server turns away rolls the dice again at once, until a map loads or none is left: every Deathmatch map refused, none awaited, said once ({Unloadable.Count} refused)",
                rolled != null && Names(Modes.Deathmatch).Count == 0 && Unloadable.Count == dm.Length + 1 && !LoadPending && Pending == null
                && Told.Count(t => t.Contains("is not on the server - It was not loaded", StringComparison.Ordinal)) == 1);
            h.Server = _ => "";
            ServerGoingDown("a test's quit");
            Check("A new server process may have them: the server going down forgets every map it turned away", Unloadable.Count == 0 && Names(Modes.Deathmatch).Count == dm.Length + 1);
            Unloadable.Add("de_cbble");
            SpawnStarts.Clear(); SpawnSeen = 7;
            JudgeSpawn(1);
            Check("...and so does a new process the crash watch finds (its spawn counter back to 1)", Unloadable.Count == 0);

            // ---- the official maps the HUD does not show on (Modes.HudLess): out of every pool, the dice and the ballot, still listed; every pick plays the map's
            // Workshop copy, saying so (the owner's 317 b) ----
            h.Fresh();
            HudLess = new(Modes.HudLess.Select(c => c.Map), StringComparer.OrdinalIgnoreCase);
            // the three copies as our lists hold them: Wingman's with their spawns, Deathmatch's too
            File.WriteAllText(Path.Combine(h.Dir, Modes.Wingman.MapsFile), "[" + string.Join(", ", Modes.HudLess.Select((c, i) =>
                $"{{\"rank\": {40 + i}, \"id\": {c.Copy}, \"name\": \"{c.Name}\", \"sizeMb\": 700, \"spawnsT\": 9, \"spawnsCt\": 9}}")) + "]");
            File.WriteAllText(Path.Combine(h.Dir, Modes.Deathmatch.MapsFile), "[" + string.Join(", ", Modes.HudLess.Select((c, i) =>
                $"{{\"rank\": {60 + i}, \"id\": {c.Copy}, \"name\": \"{c.Name}\", \"sizeMb\": 700}}")) + "]");
            ReloadModeMaps(Modes.Wingman, force: true);
            ReloadModeMaps(Modes.Deathmatch, force: true);
            CatalogVersion++;
            var off = wm.Where(n => Modes.HudLess.Any(c => c.Map == n)).ToList();
            long CopyOf(string map) => Modes.HudLess.Single(c => c.Map == map).Copy;
            string OffWords(IEnumerable<string> maps) => string.Join(" ", maps.Select(n => $"{n} (plays as {CopyOf(n)})"));
            static string OffLine(string map) => $"{map} is off for now - The HUD does not show on it. Pick another.";
            var wmRolls = Enumerable.Range(0, 3000).Select(_ => RandomPick(Modes.Wingman)?.Name).ToList();
            Check($"The maps the HUD does not show on are all Wingman's ({string.Join(" ", off)}), each with its Workshop copy: out of the pool and the dice, the rest of Valve's "
                  + $"pool in, the copies rolled as any Workshop map; still in the catalog as Wingman's official maps, so a pick reaches the bridge; GET /health says so ({PoolDifferences()[2]})",
                off.Count == Modes.HudLess.Length && off.Count == 3 && Modes.HudLess.All(c => c.Copy > 0 && c.Name != "") && Same(Names(Modes.Wingman), wm.Except(off))
                && wmRolls.All(n => n != null && !off.Contains(n)) && wm.Except(off).All(wmRolls.Contains) && Modes.HudLess.All(c => wmRolls.Contains(c.Name))
                && off.All(n => Catalog().Maps.SingleOrDefault(m => m.Key == n) is { Official: true, RankWm: 0 })
                && PoolDifferences()[2].EndsWith($", off - the HUD does not show on them: {OffWords(off)}", StringComparison.Ordinal) && HealthText().Contains(PoolDifferences()[2]));
            h.Join(alice);
            h.Join(("Bob", "[U:1:4102]")); // two on: a pick's vote stays open with one vote cast
            h.Quiet();
            var asked = new List<string>();
            foreach (var (map, text, click, mode) in new[] { ("de_eldorado", "!map de_eldorado", true, Modes.WingmanId), ("de_eldorado", "!map de_eldorado wingman", true, Modes.WingmanId),
                         ("de_poseidon", "!map de_poseidon deathmatch", true, Modes.DeathmatchId), ("de_debris", "!map de_debris", false, Modes.WingmanId) })
            { // a click's Play in the server's mode, a card's in Wingman (HISTORY's Vote for it too), a card under Deathmatch's filter; and typed in chat
                ClearNotices(); Ballot = null; Pending = null; LoadPending = false; S.Map = null; VoteWaitUntil = default;
                if (click) HudAct($"{alice.Item2}\t{alice.Item1}\t{text}"); else h.Talk(alice, text);
                var name = Modes.HudLess.Single(c => c.Map == map).Name;
                var card = Ballot?.Options.FirstOrDefault(o => o.Source == VoteSource.Picked);
                if (!(h.Said($"{name} plays as its Workshop copy - The HUD needs it") && card?.Map is { Official: false } m && m.Id == CopyOf(map) && ModeOf(m).Id == mode
                      && Ballot!.Options.All(o => o.Map is not { Official: true } om || !off.Contains(om.Name)) && !Told.Any(t => t.Contains("is off for now", StringComparison.Ordinal))))
                    asked.Add($"{text}: {string.Join(" | ", Told)}; card {card?.Map}");
            }
            Check($"A pick of one - a card's Play in any mode, under any mode's filter, or typed - plays its Workshop copy: the vote opens with the copy's card, in the mode asked for "
                  + $"where a list has it there, and the player is told why{(asked.Count > 0 ? $" ({string.Join("; ", asked)})" : "")}", asked.Count == 0);
            ClearNotices(); Ballot = null; Pending = null; LoadPending = false; S.Map = null; VoteWaitUntil = default;
            S.History.Add(new MapInfo(0, 0, "de_eldorado", 0, true, Modes.WingmanId));
            h.Talk(alice, "!previous");
            var offMap = new MapInfo(0, 0, "de_poseidon", 0, true, Modes.WingmanId);
            Check("...and so does Previous to one; a ballot card of one wins as its copy (PlayableTarget); the official maps themselves are never rolled, nor kept for the disk",
                h.Said("El Dorado plays as its Workshop copy - The HUD needs it") && Ballot?.Options.Any(o => o.Source == VoteSource.Picked && o.Map?.Id == CopyOf("de_eldorado")) == true
                && PlayableTarget(offMap) is { Official: false, Mode: Modes.WingmanId } won && won.Id == CopyOf("de_poseidon") && !CanRoll(offMap) && Bad(offMap)
                && PlayableTarget(new MapInfo(0, 0, "de_nuke", 0, true, Modes.WingmanId)) is { Name: "de_nuke", Official: true } && Unplayable(new MapInfo(0, 0, "de_nuke", 0, true, Modes.WingmanId)) == null);
            ModeMaps.Clear(); // no copy listed: the pick still goes to it, unlisted, in the official map's mode
            Check("...a copy no list holds still plays, unlisted, in the official map's mode",
                HudCopyOf(offMap) is { Official: false, Rank: 0, Mode: Modes.WingmanId, Name: "Poseidon" } unlisted && unlisted.Id == CopyOf("de_poseidon"));
            ReloadModeMaps(Modes.Wingman, force: true);
            ReloadModeMaps(Modes.Deathmatch, force: true);
            TakePools(Report("1.41.9.9", ar, dm, [], wm, all));
            Check($"...and the game's own report changes none of it: its Wingman pool played without them, the comparison says so ({PoolDifferences()[2]})",
                Same(Names(Modes.Wingman), wm.Except(off)) && PoolDifferences()[2] == $"wingman {wm.Length} = Modes.cs, off - the HUD does not show on them: {OffWords(off)}"
                && Find("de_eldorado") is [{ Official: true, Mode: Modes.WingmanId }] && Find("de_eldorado_not") is []);

            // ---- the game's report names the maps it ships as community addons (its addons line, os2-server from release 221a): each of them is off too ----
            h.Fresh(); // none kept out by the table now, as in every section: the report alone
            var shipped = TakePools(Report("1.41.9.9", ar, dm.Append("de_shipped"), [], wm, all.Append("de_shipped")) + "addons\tde_eldorado,de_shipped\n");
            h.Join(alice);
            h.Quiet();
            HudAct($"{alice.Item2}\t{alice.Item1}\t!map de_shipped deathmatch");
            Check($"A report's community addons are off like the table's: de_shipped out of Deathmatch's pool and de_eldorado out of Wingman's, both still in the catalog, "
                  + $"a pick refused; de_debris, which the report does not name, plays ({shipped})",
                !Names(Modes.Deathmatch).Contains("de_shipped") && !Names(Modes.Wingman).Contains("de_eldorado") && Names(Modes.Wingman).Contains("de_debris")
                && Catalog().Maps.Any(m => m.Key == "de_shipped" && m.Official) && h.Said(OffLine("de_shipped")) && Ballot == null
                && PoolDifferences()[1].EndsWith(", off - the HUD does not show on them: de_shipped", StringComparison.Ordinal)
                && PoolDifferences()[2].EndsWith(", off - the HUD does not show on them: de_eldorado (plays as 3408790618)", StringComparison.Ordinal));
            TakePools(Report("1.41.9.9", ar, dm.Append("de_shipped"), [], wm, all.Append("de_shipped")));
            Check("...and a report without the line - an os2-server before release 221a - keeps none out",
                Names(Modes.Deathmatch).Contains("de_shipped") && Same(Names(Modes.Wingman), wm) && OffForHud("de_shipped") == false);
        }
        finally
        {
            ModeMapsDir = null;
            h.Dispose();
        }
    }
}
