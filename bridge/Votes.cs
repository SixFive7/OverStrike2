// Likes and dislikes, and the weighted dice. One mark per player per map - a like, or a dislike with its reason: too big (it also marks the
// map too big), ads (it also keeps the map out of random picks) and the rest - kept in STATE_DIR/votes.json (the file keeps its name; the
// players' word is "likes", so that "vote" means the vote for the next map alone). The dice weigh every candidate by its points - each point
// doubles its odds: the Workshop's popularity (Popularity.cs), our likes and dislikes, and a step back for a card shown lately - in the group the
// too-big rule chose (Selection.cs). A map's download size plays no part in it.
using System.Text.Json;

static partial class Program
{
    public const int UpvoteCap = 5;             // each like is a point, up to this many
    public const int RecentCount = 10;          // the dice skip the maps played this many changes back
    public const double PopularityPoints = 4;   // the Workshop's popularity, 0 to 1, is worth up to this many points: the most popular map 16 times the least
    public const double DislikePoints = 2;      // each dislike takes this many: a quarter of the odds, twice as strong as a like, the other way
    public const double ShownPoints = 1;        // each time a card was shown and not chosen, this many off...
    public const double ShownMaxPoints = 2;     // ...this many at the most...
    public const int ShownFadeDays = 28;        // ...fading to nothing in a straight line over this many days: the one number of the curve

    static Dictionary<string, Dictionary<string, int>> Votes = new(); // map key -> SteamID -> +1 or -1
    static string VotesPath => Path.Combine(StateDir, "votes.json");

    /// <summary>Reads votes.json. A file that does not parse is set aside as votes.json.bad-&lt;time&gt; rather than overwritten by the next vote.</summary>
    static void LoadVotes()
    {
        Votes = new();
        if (!File.Exists(VotesPath)) return;
        try { Votes = JsonSerializer.Deserialize(File.ReadAllText(VotesPath), Ctx.Default.DictionaryStringDictionaryStringInt32) ?? new(); }
        catch (Exception e)
        {
            var aside = $"{VotesPath}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(VotesPath, aside);
            Log($"votes: {VotesPath} unreadable ({e.Message}) - moved to {aside}, starting empty");
        }
    }

    /// <summary>Written like state.json: a temporary file, then an atomic rename over the old one.</summary>
    static void SaveVotes()
    {
        CatalogVersion++; // every saved vote changes a card in the menu
        WriteAtomic(VotesPath, JsonSerializer.Serialize(Votes, Ctx.Default.DictionaryStringDictionaryStringInt32));
    }

    static (int Up, int Down) Tally(string key) =>
        Votes.TryGetValue(key, out var v) ? (v.Values.Count(x => x > 0), v.Values.Count(x => x < 0)) : (0, 0);

    static string TallyText(string key) => Tally(key) is var (u, d) && u + d > 0 ? $" (+{u}{(d > 0 ? $" -{d}" : "")})" : "";

    /// <summary>!up / !down [map] [reason]: like or dislike the map that is on, or any map by rank, Workshop id or official name. One mark per
    /// SteamID per map. It steps down - none - up: the opposite vote on a voted map only takes the vote away, back to none, and the
    /// next one casts it; the same vote again changes nothing and says so (repeating a vote never takes it back). A
    /// too-big vote (TooBigVote, Selection.cs) is that player's dislike - every tally and weight counts it as a downvote - and it also marks the
    /// map too big; an ads vote (AdsVote) is a dislike too, and keeps the map out of random picks altogether; taking one away takes its mark
    /// with it. Named: !down with its reason (DownReasons) on a disliked map sets that reason; on a liked one it takes the like away first. A
    /// plain !down on a map already disliked keeps the reason. Without a map a reason goes to the map the player's popup is asking about, if it
    /// is open.</summary>
    static void Vote(string by, string id, string arg, int value, bool named = false)
    {
        MapInfo? map;
        if (arg == "" && named && WhyOpen.TryGetValue(id, out var asked)) map = asked.Map;
        else if (arg == "")
        {
            map = S.Map;
            if (AnnouncePending) { Tell(id, M.Refused("The next map is still loading - Like it once it is on, or on a card in the menu")); return; } // S.Map is already the map on its way
            if (map == null) { Tell(id, M.Refused("Not sure which map this is - Like it on its card in the menu instead")); return; }
        }
        else
        {
            if (Find(arg) is not [var found]) { Tell(id, M.Refused($"No map is '{M.A(Clip(arg, 40))}'")); return; }
            if (IsPack(found)) { Tell(id, PackRefusal(found)); return; }
            map = found;
        }
        CastVote(by, id, map, value, named);
    }

    /// <summary>The vote on a map that is settled: kept, taken away or told, and the popup opened or closed. A Dislike cast without a reason opens the
    /// popup; any other vote on that map closes it, as a map change does - but a plain Dislike again changes nothing, the popup included: a running
    /// one stays. The opposite vote on a voted map takes that vote away (back to none) and opens nothing.</summary>
    static void CastVote(string by, string id, MapInfo map, int value, bool named)
    {
        var key = Key(map);
        var old = Votes.TryGetValue(key, out var was) ? was.GetValueOrDefault(id) : 0;
        var plainAgain = value == DownReasons.Plain && !named && old < 0; // keeps the reason it has
        if (!plainAgain && WhyOpen.TryGetValue(id, out var open) && Key(open.Map) == key) WhyOpen.Remove(id);
        string Tallied()
        {
            var (up, down) = Tally(key);
            var marks = string.Join(", ", new[] { TooBigVotes(key) is > 0 and var big ? $"{big} too big" : "", AdsVotes(key) is > 0 and var ads ? $"{ads} ads" : "" }.Where(x => x != ""));
            return $"now {M.V($"+{up} -{down}")}{(marks != "" ? $" ({marks})" : "")}";
        }
        if (plainAgain || old == value)
        {
            var name = M.A(Clip(Label(map), PlainVoteLabel));
            Tell(id, old > 0 ? $"You like {name} already, {Tallied()}. {M.C("!down")} takes it back"
                : old == DownReasons.Plain ? $"You dislike {name} already, {Tallied()}. {M.C("!up")} takes it back"
                : $"You dislike {name} already: {M.V(DownReasons.Of(old)?.Short ?? "")}, {Tallied()}. {M.C("!up")} takes it back");
            return;
        }
        if (old != 0 && Math.Sign(old) != Math.Sign(value)) // the opposite vote: the vote goes, and nothing is cast
        {
            Votes[key].Remove(id);
            if (Votes[key].Count == 0) Votes.Remove(key);
            SaveVotes();
            var gone = old > 0 ? "like" : old == DownReasons.Plain ? "dislike" : $"dislike ({DownReasons.Of(old)?.Short})";
            Log($"likes: {by} {id} on {Label(map)}: {gone} taken away");
            var how = value > 0 ? "Like it again to add a like" : value != DownReasons.Plain && DownReasons.Of(value) is { } r ? $"{M.C("!down")} {M.V(r.Word)} again to dislike it: {M.V(r.Short)}" : "Dislike it again to add a dislike";
            Tell(id, M.Done($"Your {gone} of {M.A(Clip(Label(map), PlainVoteLabel))} is removed - {how}, {Tallied()}"));
            return;
        }
        if (!Votes.TryGetValue(key, out var mine)) Votes[key] = mine = new();
        mine[id] = value;
        SaveVotes();
        if (value < 0) Log($"likes: {by} {id} on {Label(map)}: {DownReasons.Of(value)?.Label}"); // the reason is kept per mark: votes.json's value
        if (value == DownReasons.Plain && !named) WhyOpen[id] = new WhyAsked(map, Now().AddSeconds(DownReasons.PopupSeconds));
        var tally = Tallied();
        static string To(int v) => v > 0 ? "up" : DownReasons.Of(v)?.Short ?? "down";
        Tell(id, M.Done(old != 0 ? $"You changed your mark on {M.A(Label(map))} to {M.V(To(value))}, {tally}"
            : value == TooBigVote ? $"You marked {M.A(Label(map))} {M.V("too big")} (a dislike; random picks take it 1 time in {Math.Round(1 / TooBigChance)}), {tally}"
            : value == AdsVote ? $"You marked {M.A(Label(map))}: {M.V("ads")} (a dislike; never a random pick), {tally}"
            : value == DownReasons.Plain ? $"You disliked {M.A(Clip(Label(map), PlainVoteLabel))}, {tally}. Why? {M.C("!down")} {M.V(DownReasons.Words)}"
            : value < 0 ? $"You disliked {M.A(Label(map))}: {M.V(To(value))}, {tally}"
            : $"You liked {M.A(Label(map))}, {tally}"));
    }

    const int PlainVoteLabel = 34; // a plain downvote's note names the map in this much, so "Why? ..." always fits the note line

    /// <summary>The map a player's popup asks about, and until when it stands.</summary>
    sealed record WhyAsked(MapInfo Map, DateTime Until);

    static readonly Dictionary<string, WhyAsked> WhyOpen = new(); // SteamID -> the Dislike still waiting for its reason; in memory: a lost one stays "I did not like the map"

    /// <summary>Every tick's reading of the open popups, into the live state: each one's map and seconds left. One past its time goes after a short grace,
    /// so that a click on its way still lands; its vote stays "I did not like the map".</summary>
    static void PutWhy(HudState h, DateTime now)
    {
        foreach (var (id, asked) in WhyOpen.ToList())
        {
            if (now >= asked.Until.AddSeconds(DownReasons.PopupGraceSeconds)) { WhyOpen.Remove(id); continue; }
            if (now < asked.Until && AccountId(id) is > 0 and var account) { h.Why[account] = (Clip(Label(asked.Map), 40), (int)Math.Ceiling((asked.Until - now).TotalSeconds)); h.WhyUntilMs[account] = HudState.Ms(asked.Until); }
        }
    }

    /// <summary>The health page's line about the downvotes' reasons.</summary>
    static string ReasonHealth() => "down votes: " + string.Join(", ", DownReasons.All.Select(r => $"{Votes.Values.Sum(v => v.Values.Count(x => x == r.Value))} {r.Short}"));

    /// <summary>The map a vote key stands for: a map any mode lists or an official map, else an unlisted workshop id.</summary>
    static MapInfo MapOf(string key) => AllKnown.FirstOrDefault(m => Key(m) == key)
        ?? new MapInfo(0, long.TryParse(key, out var n) ? n : 0, long.TryParse(key, out _) ? $"workshop {key}" : key, 0);

    /// <summary>The ten maps with the most likes, fewer dislikes then the better place on the list breaking ties: the HUD's "most liked" list (and
    /// the menu's Likes sort shows them all).</summary>
    static List<(MapInfo Map, int Up, int Down)> TopRows() =>
        Votes.Select(kv => (Map: MapOf(kv.Key), Up: kv.Value.Values.Count(x => x > 0), Down: kv.Value.Values.Count(x => x < 0)))
            .Where(r => r.Up > 0).OrderByDescending(r => r.Up).ThenBy(r => r.Down).ThenBy(r => r.Map.Rank == 0 ? int.MaxValue : r.Map.Rank).Take(10).ToList();

    /// <summary>When a card was shown and not chosen, the points it costs now: ShownPoints for each showing, fading in a straight line to nothing
    /// over ShownFadeDays, ShownMaxPoints at the most.</summary>
    static double ShownPenalty(string key, long now)
    {
        if (!S.Shown.TryGetValue(key, out var times) || times.Count == 0) return 0;
        var fade = ShownFadeDays * 86400.0;
        return Math.Min(ShownMaxPoints, times.Sum(t => ShownPoints * Math.Max(0, 1 - (now - t) / fade)));
    }

    /// <summary>A candidate's points: PopularityPoints times its popularity (Popularity.cs), a point for each like up to UpvoteCap, DislikePoints
    /// off for each dislike, and the step back for a card shown lately (ShownPenalty).</summary>
    static double Points(MapInfo m, Dictionary<string, Dictionary<string, double>>? table = null, long now = 0)
    {
        var key = Key(m);
        var (up, down) = Tally(key);
        return PopularityPoints * PopularityOf(m, table) + Math.Min(up, UpvoteCap) - DislikePoints * down - ShownPenalty(key, now == 0 ? NowUnix : now);
    }

    /// <summary>A candidate's weight in the dice: 2 to the power of its points, so each point doubles its odds - +1 like doubles, -1 dislike
    /// quarters, one of each halves.</summary>
    static double Weight(MapInfo m) => Math.Pow(2, Points(m));

    /// <summary>The weights of many candidates at once, the popularity table and the clock read once.</summary>
    static Func<MapInfo, double> Weigher()
    {
        var table = PopularityTable();
        var now = NowUnix;
        return m => Math.Pow(2, Points(m, table, now));
    }

    /// <summary>The map the dice roll over every eligible map of a mode, whether it is on the server's disk or not, or null when nothing is eligible:
    /// what RandomNext falls back on when nothing on the disk is eligible. Excluded: the map now loaded, every map with a crash on record and every map
    /// on the BROKEN_FILE list (so a roll can never raise a confirm nobody asked for), every non-map, every map gone from the Workshop, every blocked
    /// map, every map a player marked as showing ads (AdsVote), and - while anything else is left in the group - the last RecentCount maps played.
    /// Arms Race's ranked maps only: its official maps and unlisted maps are never rolled; another mode's official maps are. In the list the too-big
    /// maps TooBigChance of the time and the rest otherwise, the other group when that one is empty (Selection.cs). Weighted by Weight.</summary>
    static MapInfo? RandomPick(Modes.Mode? inMode = null, ISet<string>? exclude = null)
    {
        inMode ??= CurrentMode; // ModeSwitch.cs: a roll is in one mode - the server's, unless the ballot asks for another
        var eligible = PoolOf(inMode).Where(m => CanRoll(m, exclude)).ToList();
        var bigFirst = Dice.NextDouble() < TooBigChance;
        if (RollIn(eligible, bigFirst) is not { } r) return null;
        if (!SelfTesting) Log($"dice: {inMode.Name}, {(r.Big ? "too big" : "not too big")}{(r.Big != bigFirst ? " (the other group was empty)" : "")}: {r.Candidates} candidates -> {Label(r.Pick)}");
        return r.Pick;
    }

    /// <summary>The rule every random pick applies to a candidate, as the dice have always applied it: not the map that is on, no crash on
    /// record, not on the broken list, no player crashes, not a non-map, not gone from the Workshop, not blocked, not marked as showing ads,
    /// and not in <paramref name="exclude"/> (the ballot's own maps). The stock (RandomQueue.cs) rolls with it and checks it again when an
    /// entry is used.</summary>
    static bool CanRoll(MapInfo m, ISet<string>? exclude = null)
    {
        var current = S.Map != null ? Key(S.Map) : S.LogName; // by workshop id when the bridge picked it, else by the name the server logs
        var key = Key(m);
        return key != current && m.Name != current && S.Crashes.GetValueOrDefault(key) == 0 && !Broken.ContainsKey(key) && !S.PlayerCrashes.ContainsKey(key)
            && !IsPack(m) && !IsRemoved(m) && !IsBlocked(m) && AdsVotes(key) == 0 && exclude?.Contains(key) != true;
    }

    /// <summary>The dice inside one list of eligible maps: the too-big group when <paramref name="bigFirst"/> (TooBigChance of the time),
    /// the other group when that one is empty; the last RecentCount maps played left out while anything else is left; weighted by Weight.
    /// Null for an empty list. One Dice draw, after the caller's: RandomPick, RandomNext, the ballot's cards and the stock roll the same way.</summary>
    static (MapInfo Pick, bool Big, int Candidates)? RollIn(List<MapInfo> list, bool bigFirst)
    {
        if (list.Count == 0) return null;
        var (group, big) = (list.Where(m => TooBig(m) == bigFirst).ToList(), bigFirst);
        if (group.Count == 0) (group, big) = (list, !bigFirst); // that group is empty: the other one, which is then the whole list
        var recent = S.History.TakeLast(RecentCount).Select(Key).ToHashSet();
        if (group.Any(m => !recent.Contains(Key(m)))) group = group.Where(m => !recent.Contains(Key(m))).ToList();
        var weigh = Weigher();
        var weights = group.Select(weigh).ToList();
        var roll = Dice.NextDouble() * weights.Sum();
        var pick = group[^1];
        for (var i = 0; i < group.Count; i++)
            if ((roll -= weights[i]) < 0) { pick = group[i]; break; }
        return (pick, big, group.Count);
    }
}
