// Our map votes, kept (the STATS page's most voted for; the owner's 304 a, 2026-11-19): every decided ballot a line of ballots.jsonl in the state folder,
// oldest first - when, its round, the server's mode, how it was decided, who was there, every card with its map, its mode and its final votes (a player's last
// vote of the round counts), and what won. The ballots from before it are read back out of the bridge's day logs by --ballots-backfill, as far back as they
// reach: their open line names every card, their vote lines each vote and its moves, their decision what won.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

static partial class Program
{
    /// <summary>A card of a decided ballot: its map as the map list keys it ("random" for Random; "name:" and the label for a map a backfill could not find on
    /// the lists), its name, its mode and its votes.</summary>
    sealed record BallotCard(string Key, string Name, string Mode, int Votes);

    /// <summary>A decided ballot: when, its round, the server's mode, how it was decided (all, time, tie, none ...), how many humans were there, its cards, the
    /// key of what won, and where the record came from ("live", "backfill").</summary>
    sealed record BallotRecord(DateTimeOffset At, long Round, string Mode, string How, int Present, List<BallotCard> Cards, string Won, string Source);

    public const string RandomKey = "random";

    static string BallotsPath => Path.Combine(StateDir, "ballots.jsonl");

    static readonly List<BallotRecord> Ballots = new(); // every decided ballot, oldest first

    static readonly JsonSerializerOptions BallotJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string BallotLine(BallotRecord r)
    {
        var cards = new JsonArray();
        foreach (var c in r.Cards) cards.Add((JsonNode)new JsonObject { ["key"] = c.Key, ["name"] = c.Name, ["mode"] = c.Mode, ["votes"] = c.Votes });
        return new JsonObject
        {
            ["v"] = 1, ["at"] = r.At.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), ["round"] = r.Round, ["mode"] = r.Mode, ["how"] = r.How,
            ["present"] = r.Present, ["cards"] = cards, ["won"] = r.Won, ["source"] = r.Source,
        }.ToJsonString(BallotJson);
    }

    static BallotRecord? BallotOf(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o) return null;
            static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            static long L(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;
            if (!DateTimeOffset.TryParseExact(S(o["at"]), "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return null;
            var cards = (o["cards"] as JsonArray ?? []).OfType<JsonObject>().Select(c => new BallotCard(S(c["key"]), S(c["name"]), S(c["mode"]), (int)Math.Max(0, L(c["votes"]))))
                .Where(c => c.Key != "").ToList();
            return new BallotRecord(at, L(o["round"]), S(o["mode"]), S(o["how"]), (int)Math.Max(0, L(o["present"])), cards, S(o["won"]), S(o["source"]));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>ballots.jsonl, at the bridge's start: a line that does not read is skipped, and said.</summary>
    static string LoadBallots()
    {
        Ballots.Clear();
        if (!File.Exists(BallotsPath)) return $"ballots: no {BallotsPath} yet - the first ballot decided starts it";
        var bad = 0;
        foreach (var line in File.ReadLines(BallotsPath))
            if (line.Trim() != "") { if (BallotOf(line) is { } r) Ballots.Add(r); else bad++; }
        return $"ballots: {Ballots.Count} decided ballot(s) from {BallotsPath}" + (bad > 0 ? $", {bad} line(s) that do not read skipped" : "");
    }

    /// <summary>A ballot decided (VoteRound.CloseBallot): its record, appended to ballots.jsonl - every card with its final votes, and what won. The record counts
    /// real humans alone: the lab's fake players (LabFakes), who vote on a test copy, are neither among its present nor in its cards' votes; what won is the
    /// vote's own. A ballot no real human was at or voted in is kept nowhere, as the history keeps no match nobody human was in.</summary>
    static void KeepBallot(BallotState b, BallotOption won, string how, Dictionary<int, int> tally, int present)
    {
        string KeyOf(BallotOption o) => o.Source == VoteSource.Random || o.Map == null ? RandomKey : Key(o.Map);
        var fakeVotes = b.Choices.Where(c => LabFakes.IsFake(AccountId(c.Key))).Select(c => c.Value).ToList();
        var fakesHere = PresentIds(Now()).Count(id => LabFakes.IsFake(AccountId(id)));
        var cards = b.Options.Select(o => new BallotCard(KeyOf(o), o.Map?.Name ?? "Random", o.Map != null ? ModeOf(o.Map).Id : b.FillMode.Id,
            Math.Max(0, tally.GetValueOrDefault(o.N) - fakeVotes.Count(n => n == o.N)))).ToList();
        var humans = Math.Max(0, present - fakesHere);
        if (humans == 0 && cards.All(c => c.Votes == 0)) { Log($"ballots: round {b.Round} not kept - no real human was at it (the lab's fake players count for none)"); return; }
        var r = new BallotRecord(HistoryNow(), b.Round, CurrentMode.Id, how, humans, cards, KeyOf(won), "live");
        Ballots.Add(r);
        try { File.AppendAllText(BallotsPath, BallotLine(r) + "\n"); }
        catch (Exception x) { Log($"ballots: {BallotsPath} could not be written - {x.Message}"); }
        StatsDirty();
    }

    // ---- --ballots-backfill: the ballots read back from the bridge's day logs ----

    [GeneratedRegex(@"^vote: round (\d+) open for \d+ s.*?, \d+ present: (.*)$")] private static partial Regex BallotOpenRx();
    [GeneratedRegex(@"^(\d+)\. (.+) \((keep|picked|rolled|random), ([a-z]+)(?:, [^)]*)?\)$")] private static partial Regex BallotCardRx();
    [GeneratedRegex(@"^vote: .+ \[U:1:(\d+)\] votes (\d+) \(")] private static partial Regex BallotVoteRx();
    [GeneratedRegex(@"^vote: .+ \[U:1:(\d+)\] added (\d+)\. (.+) and votes for it$")] private static partial Regex BallotAddedRx();
    [GeneratedRegex(@"^vote: .+ \[U:1:(\d+)\] asked for .+, on the ballot as (\d+) - votes (\d+)$")] private static partial Regex BallotAskedRx();
    [GeneratedRegex(@"^vote: started by .+ \[U:1:(\d+)\], with their vote on Random")] private static partial Regex BallotStarterRandomRx();
    [GeneratedRegex(@"^vote: round (\d+) decided \(([a-z]+), \d+ of \d+ vote\(s\), (\d+) present\): (.*)$")] private static partial Regex BallotDecidedRx();
    [GeneratedRegex(@"^vote: round (\d+) dropped")] private static partial Regex BallotDroppedRx();
    [GeneratedRegex(@"^(?:Keep playing this map|Play this map again) \((.+)\)$")] private static partial Regex KeepLabelRx();
    [GeneratedRegex(@"^#\d+ ")] private static partial Regex RankLabelRx();
    [GeneratedRegex(@" - (?:Arms Race|Deathmatch|Wingman)$")] private static partial Regex ModeLabelRx();

    /// <summary>A card's label in a day log as the map's own name: a kept map's inside its words, without its list place (#4), its mode (" - Wingman") and
    /// its mark (" (official)"); whether it is an official map.</summary>
    static (string Name, bool Official) LabelName(string label)
    {
        var l = KeepLabelRx().Match(label) is { Success: true } k ? k.Groups[1].Value : label;
        l = ModeLabelRx().Replace(RankLabelRx().Replace(l, ""), "");
        var official = l.EndsWith(" (official)", StringComparison.Ordinal);
        return (official ? l[..^11] : l, official);
    }

    /// <summary>bridge --ballots-backfill OUT --maps DIR [--zone ZONE] [--until "yyyy-MM-dd HH:mm"] FILE...: the ballots of the bridge's day logs (bridge-*.log),
    /// as ballots.jsonl has them, oldest first - read only, written to OUT. DIR holds the map lists the cards' names are looked up in (maps.json,
    /// maps-deathmatch.json, maps-wingman.json: a copy of the bridge's data folder); --until stops where the live record takes over. 0, or 2 for words it cannot use.</summary>
    static int BallotsBackfillCommand(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: bridge --ballots-backfill OUT --maps DIR [--zone ZONE] [--until \"yyyy-MM-dd HH:mm\"] FILE..."); return 2; }
        var output = args[0];
        var zone = TimeZoneInfo.Local;
        DateTimeOffset? until = null;
        string? dir = null;
        var files = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--maps" && i + 1 < args.Length) dir = args[++i];
            else if (args[i] == "--zone" && i + 1 < args.Length) zone = Zone(args[++i]);
            else if (args[i] == "--until" && i + 1 < args.Length) until = Local(DateTime.ParseExact(args[++i], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), zone);
            else files.Add(args[i]);
        }
        if (dir == null) { Console.Error.WriteLine("--ballots-backfill: --maps DIR names the folder of the map lists"); return 2; }
        List<MapInfo> List(string file)
        {
            var path = Path.Combine(dir, file);
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), Ctx.Default.ListMapInfo) ?? [] : [];
        }
        var lists = new Dictionary<string, List<MapInfo>> { [Modes.ArmsRaceId] = List("maps.json"), [Modes.DeathmatchId] = List("maps-deathmatch.json"), [Modes.WingmanId] = List("maps-wingman.json") };
        var (records, words) = BallotsBackfill(files.Select(f => File.ReadAllLines(f)).ToList(), zone, until, lists);
        File.WriteAllText(output, string.Concat(records.Select(r => BallotLine(r) + "\n")));
        Console.WriteLine(words);
        return 0;
    }

    /// <summary>The backfill proper: every day log's ballot lines in time order. A card's label is looked up by its exact name in its mode's list, then in the
    /// others, then in any case where only one map has it; an official map is its name; a name no list has stays a name ("name:" and the label).</summary>
    static (List<BallotRecord> Records, string Words) BallotsBackfill(List<string[]> files, TimeZoneInfo zone, DateTimeOffset? until, Dictionary<string, List<MapInfo>> lists)
    {
        var events = new List<(DateTimeOffset At, int Seq, string Msg)>();
        var seq = 0;
        foreach (var lines in files)
            foreach (var line in lines)
                if (BridgeLineRx().Match(line) is { Success: true } b && b.Groups[2].Value.StartsWith("vote: ", StringComparison.Ordinal))
                    events.Add((Local(DateTime.ParseExact(b.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), zone), seq++, b.Groups[2].Value));
        events = [.. events.Where(e => until == null || e.At < until.Value).DistinctBy(e => (e.At, e.Msg)).OrderBy(e => e.At).ThenBy(e => e.Seq)];
        var (found, unfound) = (0, new HashSet<string>(StringComparer.Ordinal));
        string KeyOf(string label, string mode)
        {
            var (name, official) = LabelName(label);
            if (official) { found++; return name; }
            foreach (var list in lists.OrderBy(l => l.Key == mode ? 0 : 1).Select(l => l.Value))
                if (list.FirstOrDefault(m => m.Name == name && m.Id > 0) is { } exact) { found++; return exact.Id.ToString(CultureInfo.InvariantCulture); }
            var loose = lists.Values.SelectMany(l => l).Where(m => m.Id > 0 && m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(m => m.Id).Distinct().ToList();
            if (loose.Count == 1) { found++; return loose[0].ToString(CultureInfo.InvariantCulture); }
            if (Modes.All.Any(m => m.Official.Contains(name, StringComparer.OrdinalIgnoreCase))) { found++; return name; }
            unfound.Add(name);
            return "name:" + name;
        }
        var records = new List<BallotRecord>();
        (long Round, string Mode, Dictionary<int, (string Key, string Name, string Mode)> Cards, Dictionary<long, int> Votes)? open = null;
        foreach (var (at, _, msg) in events)
        {
            if (BallotOpenRx().Match(msg) is { Success: true } o)
            {
                var cards = new Dictionary<int, (string, string, string)>();
                var serverMode = Modes.ArmsRaceId;
                foreach (var part in o.Groups[2].Value.Split(" | "))
                    if (BallotCardRx().Match(part.Trim()) is { Success: true } c)
                    {
                        var n = int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture);
                        var mode = c.Groups[4].Value;
                        if (c.Groups[3].Value == "random") { cards[n] = (RandomKey, "Random", mode); serverMode = mode; continue; }
                        cards[n] = (KeyOf(c.Groups[2].Value, mode), LabelName(c.Groups[2].Value).Name, mode);
                    }
                open = (long.Parse(o.Groups[1].Value, CultureInfo.InvariantCulture), serverMode, cards, new());
                continue;
            }
            if (open is not { } r) continue;
            if (BallotVoteRx().Match(msg) is { Success: true } v) { r.Votes[long.Parse(v.Groups[1].Value, CultureInfo.InvariantCulture)] = int.Parse(v.Groups[2].Value, CultureInfo.InvariantCulture); continue; }
            if (BallotAddedRx().Match(msg) is { Success: true } a)
            {
                var n = int.Parse(a.Groups[2].Value, CultureInfo.InvariantCulture);
                var mode = ModeOfLabel(a.Groups[3].Value) ?? r.Mode;
                r.Cards[n] = (KeyOf(a.Groups[3].Value, mode), LabelName(a.Groups[3].Value).Name, mode);
                r.Votes[long.Parse(a.Groups[1].Value, CultureInfo.InvariantCulture)] = n;
                continue;
            }
            if (BallotAskedRx().Match(msg) is { Success: true } q) { r.Votes[long.Parse(q.Groups[1].Value, CultureInfo.InvariantCulture)] = int.Parse(q.Groups[3].Value, CultureInfo.InvariantCulture); continue; }
            if (BallotStarterRandomRx().Match(msg) is { Success: true } sr)
            {
                if (r.Cards.FirstOrDefault(c => c.Value.Key == RandomKey) is { Value.Key: RandomKey } rnd) r.Votes[long.Parse(sr.Groups[1].Value, CultureInfo.InvariantCulture)] = rnd.Key;
                continue;
            }
            if (BallotDroppedRx().IsMatch(msg)) { open = null; continue; }
            if (BallotDecidedRx().Match(msg) is { Success: true } d)
            {
                var rest = d.Groups[4].Value;
                var wonN = Regex.Match(rest, @"^(\d+)\. ") is { Success: true } w ? int.Parse(w.Groups[1].Value, CultureInfo.InvariantCulture) : r.Cards.FirstOrDefault(c => c.Value.Key != RandomKey).Key; // a keep: the kept map's card
                var tally = r.Votes.Values.GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());
                var list = r.Cards.OrderBy(c => c.Key).Select(c => new BallotCard(c.Value.Key, c.Value.Name, c.Value.Mode, tally.GetValueOrDefault(c.Key))).ToList();
                records.Add(new BallotRecord(at, r.Round, r.Mode, d.Groups[2].Value, int.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture), list,
                    r.Cards.TryGetValue(wonN, out var won) ? won.Key : "", "backfill"));
                open = null;
            }
        }
        var words = records.Count == 0 ? "ballots backfill: no decided ballot in these logs"
            : $"ballots backfill: {records.Count} decided ballot(s) from {records[0].At:yyyy-MM-dd HH:mm} to {records[^1].At:yyyy-MM-dd HH:mm}, {records.Sum(r => r.Cards.Sum(c => c.Votes))} vote(s); "
              + $"{found} card name(s) found on the lists, {unfound.Count} not: {string.Join(", ", unfound.Take(12))}{(unfound.Count > 12 ? " ..." : "")}";
        return (records, words);
    }

    static string? ModeOfLabel(string label) =>
        label.EndsWith(" - Deathmatch", StringComparison.Ordinal) ? Modes.DeathmatchId : label.EndsWith(" - Wingman", StringComparison.Ordinal) ? Modes.WingmanId
        : label.EndsWith(" - Arms Race", StringComparison.Ordinal) ? Modes.ArmsRaceId : null;
}
