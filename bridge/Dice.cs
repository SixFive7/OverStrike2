// The weighted dice. They weigh every candidate by its points - each point doubles its odds: the Workshop's popularity (Popularity.cs), our likes
// and dislikes (Likes.cs), and a step back for a card shown lately - in the group the too-big rule chose (Sizes.cs). A map's download size plays
// no part in it.

static partial class Program
{
    public const int UpvoteCap = 5;             // each like is a point, up to this many
    public const int RecentCount = 10;          // the dice skip the maps played this many changes back
    public const double PopularityPoints = 4;   // the Workshop's popularity, 0 to 1, is worth up to this many points: the most popular map 16 times the least
    public const double DislikePoints = 2;      // each dislike takes this many: a quarter of the odds, twice as strong as a like, the other way
    public const double ShownPoints = 1;        // each time a card was shown and not chosen, this many off...
    public const double ShownMaxPoints = 2;     // ...this many at the most...
    public const int ShownFadeDays = 28;        // ...fading to nothing in a straight line over this many days: the one number of the curve

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
    /// maps TooBigChance of the time and the rest otherwise, the other group when that one is empty (Sizes.cs). Weighted by Weight.</summary>
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
