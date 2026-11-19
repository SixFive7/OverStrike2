// The lab's seed (bridge --lab-seed DIR): a made-up history, ballots and likes for a test copy's candidate bridge, so the HISTORY and STATS pages have
// something to show on camera and in the lab - never production's players. Made-up players only (Test A to Test D and four more), on maps with pictures:
// the map list's first Workshop maps that have one, and Valve's own; dated over the five evenings before the day it is made, so the maps new this week show
// too; every record of the STATS page has data, the frags' included. os2-deploy testcopy bridge-run TAG --seed DIR puts it into the candidate's copy of the
// bridge's data, and testcopy bridge-rm takes the copy away with it (deploy/README.md); production's data is never written.
using System.Globalization;
using System.Text.Json;

static partial class Program
{
    /// <summary>The seed's players: made-up names and accounts (short ones, no real Steam account's shape).</summary>
    static readonly (long Account, string Name)[] SeedPlayers =
        [(9001, "Test A"), (9002, "Test B"), (9003, "Test C"), (9004, "Test D"), (9005, "Pixel Pete"), (9006, "Captain Crumble"), (9007, "Noodle Ninja"), (9008, "Mira Vex")];

    /// <summary>bridge --lab-seed DIR [--zone ZONE]: the seed's history.jsonl, ballots.jsonl and votes.json, written into DIR. 0, or 2 for words it cannot use.</summary>
    static int LabSeedCommand(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("usage: bridge --lab-seed DIR [--zone ZONE]"); return 2; }
        var zone = args.Length >= 3 && args[1] == "--zone" ? Zone(args[2]) : TimeZoneInfo.Local;
        var mapsAt = new[] { MapsFile, Path.Combine("bridge", "maps.json"), "maps.json" }.FirstOrDefault(File.Exists); // the settings' list, else the committed one (a run from the repository)
        var maps = mapsAt != null ? JsonSerializer.Deserialize(File.ReadAllText(mapsAt), Ctx.Default.ListMapInfo) ?? [] : [];
        var (entries, ballots, likes) = LabSeed(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone), maps);
        Directory.CreateDirectory(args[0]);
        File.WriteAllText(Path.Combine(args[0], "history.jsonl"), string.Concat(entries.Select(e => HudHistory.ToJsonLine(e) + "\n")));
        File.WriteAllText(Path.Combine(args[0], "ballots.jsonl"), string.Concat(ballots.Select(b => BallotLine(b) + "\n")));
        File.WriteAllText(Path.Combine(args[0], "votes.json"), JsonSerializer.Serialize(likes, Ctx.Default.DictionaryStringDictionaryStringInt32));
        Console.WriteLine($"lab seed: {entries.Count} matches over {entries.Select(e => e.From.Date).Distinct().Count()} evenings, {SeedPlayers.Length} made-up players, "
            + $"{ballots.Count} ballots, {likes.Count} liked or disliked maps, the maps from {mapsAt ?? "Valve's own alone (no map list found)"} -> {args[0]}");
        return 0;
    }

    /// <summary>The seed itself, made from now (local time) and the map list: its matches oldest first, its ballots, its likes (votes.json's shape).</summary>
    static (List<HudHistory.Entry> Entries, List<BallotRecord> Ballots, Dictionary<string, Dictionary<string, int>> Likes) LabSeed(DateTimeOffset now, List<MapInfo> list)
    {
        var dice = new Random(4242);
        var ar = list.Where(m => m.Id > 0 && HudThumbs.Classes(new HudCatalog.Entry(m.Id.ToString(CultureInfo.InvariantCulture), "", m.Rank, m.Id, m.Name, m.SizeMb, false, -1, 0, 0, 0)).Length > 0)
            .OrderBy(m => m.Rank).Take(6).ToList();
        (string Key, long Id, string Name, string Mode)[] pool =
        [
            .. ar.Select(m => (m.Id.ToString(CultureInfo.InvariantCulture), m.Id, m.Name, Modes.ArmsRaceId)),
            ("de_dust2", 0L, "de_dust2", Modes.DeathmatchId), ("de_mirage", 0L, "de_mirage", Modes.DeathmatchId), ("de_inferno", 0L, "de_inferno", Modes.WingmanId),
            ("de_nuke", 0L, "de_nuke", Modes.WingmanId), ("de_overpass", 0L, "de_overpass", Modes.WingmanId),
        ];
        if (ar.Count < 2) pool = [.. pool, ("ar_shoots", 0L, "ar_shoots", Modes.ArmsRaceId), ("ar_baggage", 0L, "ar_baggage", Modes.ArmsRaceId)];
        var arMaps = pool.Where(p => p.Mode == Modes.ArmsRaceId).ToList();
        // five evenings, three matches each: Arms Race, Deathmatch, Wingman - the last evening's Wingman a blowout after midnight
        var entries = new List<HudHistory.Entry>();
        var ballots = new List<BallotRecord>();
        var round = 1795000000L;
        for (var evening = 5; evening >= 1; evening--)
        {
            var day = now.Date.AddDays(-evening);
            var players = SeedPlayers.Take(4).Concat(SeedPlayers.Skip(4).Where(_ => dice.Next(2) == 0)).ToList();
            for (var n = 0; n < 3; n++)
            {
                var (key, id, name, mode) = n switch
                {
                    0 => arMaps[(5 - evening) % arMaps.Count],
                    1 => pool.Where(p => p.Mode == Modes.DeathmatchId).ElementAt(evening % 2),
                    _ => pool.Where(p => p.Mode == Modes.WingmanId).ElementAt(evening % 3),
                };
                var late = evening == 1 && n == 2;
                var start = new DateTimeOffset(day.AddHours(late ? 23.6 : 20 + n * 0.75), now.Offset);
                var minutes = late ? 32 : 11 + dice.Next(14);
                var inIt = players.Where((_, i) => i < 4 || dice.Next(3) > 0).ToList();
                var frags = new Dictionary<long, HudHistory.Frags>();
                var pairs = new Dictionary<(long, long), int>();
                foreach (var (account, _) in inIt)
                {
                    var kills = 8 + dice.Next(18) + (account == 9001 ? 12 : 0);
                    var hs = account == 9001 ? kills * 2 / 3 : dice.Next(kills / 2 + 1);
                    frags[account] = new HudHistory.Frags(Kills: kills, Deaths: 6 + dice.Next(14), Assists: dice.Next(6), Hs: hs, Knife: account == 9002 ? 2 + dice.Next(3) : dice.Next(2),
                        Taser: dice.Next(2), Noscope: mode == Modes.ArmsRaceId ? dice.Next(3) : 0, Wall: dice.Next(3), Air: dice.Next(2), Chickens: account == 9003 ? 3 + dice.Next(5) : dice.Next(2),
                        Suicides: dice.Next(2), Far: Math.Round(8 + dice.NextDouble() * (account == 9004 ? 50 : 30), 1), Streak: 2 + dice.Next(6));
                    foreach (var (victim, _) in inIt.Where(v => v.Account != account)) pairs[(account, victim)] = dice.Next(kills / 3 + 1) + (account == 9005 && victim == 9006 ? 6 : 0);
                }
                var score = mode == Modes.WingmanId ? late ? "13:2" : $"{9}:{2 + dice.Next(6)}" : "";
                var winner = mode == Modes.ArmsRaceId ? inIt.MaxBy(p => frags[p.Account].Kills).Account : 0;
                entries.Add(new HudHistory.Entry(start, start.AddMinutes(minutes), id, name, id > 0 ? "" : key, mode, HudHistory.ByMatchEnd, score,
                    [.. inIt.Select((p, i) => new HudHistory.Player(p.Account, p.Name, minutes * 60 - i * 7, start.AddSeconds(i * 7), frags[p.Account]))], [], "live",
                    inIt.Count, winner, [.. pairs.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Select(kv => new HudHistory.Pair(kv.Key.Item1, kv.Key.Item2, kv.Value))]));
                if (n == 1) // the evening's ballot: the next match's map won it
                {
                    var next = pool.Where(p => p.Mode == Modes.WingmanId).ElementAt(evening % 3);
                    var other = arMaps[(6 - evening) % arMaps.Count];
                    var votesFor = inIt.Count - 1;
                    ballots.Add(new BallotRecord(start.AddMinutes(minutes), round++, Modes.ArmsRaceId, "all", inIt.Count,
                        [new(next.Key, next.Name, next.Mode, votesFor), new(other.Key, other.Name, other.Mode, 1), new(RandomKey, "Random", Modes.ArmsRaceId, 0)], next.Key, "live"));
                }
            }
        }
        string Id(long account) => $"[U:1:{account.ToString(CultureInfo.InvariantCulture)}]";
        var likes = new Dictionary<string, Dictionary<string, int>>();
        foreach (var (p, k) in pool.Select((p, k) => (p, k)))
        {
            var votes = new Dictionary<string, int>();
            foreach (var (account, _) in SeedPlayers)
                if ((k * 7 + (int)account) % 5 < 2) votes[Id(account)] = 1;
            var critic = Id(SeedPlayers[(k + 3) % SeedPlayers.Length].Account); // a dislike on every map but every third: the crash on a third of them
            if (k % 3 == 0) votes[critic] = HudCatalog.CrashVote;
            else if (k % 3 == 1) votes[critic] = DownReasons.Plain;
            if (k == pool.Length - 1) votes[Id(SeedPlayers[1].Account)] = HudCatalog.TooBigVote;
            if (votes.Count > 0) likes[p.Key] = votes;
        }
        return (entries, ballots, likes);
    }
}
