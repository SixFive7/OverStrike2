// The weighted dice. They weigh every candidate by its points - each point doubles its odds: the Workshop's popularity (Popularity.cs), our likes
// and dislikes (Likes.cs), and a step back for a card shown lately - in the group the too-big rule chose (Sizes.cs). A map's download size weighs
// nothing; it is a cap instead (DiceMaxMb): a roll that changes the map, or fills the stock, takes none bigger.

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
        var eligible = UnderCap(PoolOf(inMode).Where(m => CanRoll(m, exclude)).ToList(), $"every eligible map of {inMode.Name}"); // MapStanding.cs
        if (RollIn(eligible) is not { } r) return null;
        if (!SelfTesting) Log($"dice: {inMode.Name}, {(r.Big ? "too big" : "not too big")}{(r.OtherGroup ? " (the other group was empty)" : "")}: {r.Candidates} candidates -> {Label(r.Pick)}");
        return r.Pick;
    }

    /// <summary>The most a map the dice roll may take to download, in MB (bridge.diceMaxMb, 300 by default; 0: no cap): a map new to a player's game asks them to
    /// confirm its download (CS2's Confirm Workshop Download, seen for maps of 464 MB, 624 MB and 1.6 GB), and a big one keeps them waiting - the live lab's movie of
    /// release 214 rolled 1.6 GB at a match end nobody voted at, and the empty server's start rolls of 2026-11-12 took 600, 993 and 363 MB maps. Every roll of the dice
    /// that changes the map, or fills the stock, keeps to it: RandomNext and RandomPick (the start roll, the way back to Arms Race, Random and nobody voting, a replaced
    /// card) and RollIntoStock. The ballot's own cards show their size and are the players' to choose.</summary>
    public static int DiceMaxMb = BridgeConfig.Values.DiceMaxMb;

    /// <summary>Whether the dice may take this map under the cap: an official map always (nothing to download), a Workshop map of at most DiceMaxMb.</summary>
    static bool UnderDiceCap(MapInfo m) => m.Official || DiceMaxMb <= 0 || m.SizeMb <= DiceMaxMb;

    /// <summary>A list the dice roll over, kept to the cap - or, when no map in it fits, its smallest alone, so a roll still finds a map (logged with why).</summary>
    static List<MapInfo> UnderCap(List<MapInfo> list, string what)
    {
        if (list.Count == 0 || DiceMaxMb <= 0) return list;
        var fit = list.Where(UnderDiceCap).ToList();
        if (fit.Count > 0) return fit;
        var smallest = list.MinBy(m => m.SizeMb)!;
        if (!SelfTesting) Log($"dice: no map of {what} ({list.Count}) is {DiceMaxMb} MB or less - the smallest, {Label(smallest)} ({Size(smallest)})");
        return [smallest];
    }

    /// <summary>The dice inside one list of eligible maps: the too-big group TooBigChance of the time and the rest otherwise, the other group when
    /// that one is empty (OtherGroup); the last RecentCount maps played left out while anything else is left; weighted by Weight. Null for an empty
    /// list. Two Dice draws, the group's and the pick's: RandomPick, RandomNext, the ballot's cards and the stock all roll this way.</summary>
    static (MapInfo Pick, bool Big, bool OtherGroup, int Candidates)? RollIn(List<MapInfo> list)
    {
        if (list.Count == 0) return null;
        var bigFirst = Dice.NextDouble() < TooBigChance;
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
        return (pick, big, big != bigFirst, group.Count);
    }
}
