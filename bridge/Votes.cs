// Map votes and the weighted dice. One vote per player per map - up, down, too big (a downvote that also marks the map too big) or ads
// (a downvote that also keeps the map out of random picks) - kept in STATE_DIR/votes.json; the dice weigh every candidate by Workshop
// rank and by those votes (README.md "Voting"), in the group that the new-maps setting and the too-big rule chose (Selection.cs). A map's
// download size plays no part in it (the owner's 4c, 2026-10-24).
using System.Text.Json;

static partial class Program
{
    public const int UpvoteCap = 5;        // upvotes beyond this many do not raise a map's odds further
    public const int RecentCount = 10;     // the dice skip the maps played this many changes back
    const double DownvoteFactor = 0.25;    // each downvote: a quarter of the odds - twice as strong as an upvote, the other way
    const double TopRankBoost = 3;         // the Workshop's #1 is this many times as likely as the last map of the table

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
        File.WriteAllText(VotesPath + ".tmp", JsonSerializer.Serialize(Votes, Ctx.Default.DictionaryStringDictionaryStringInt32));
        File.Move(VotesPath + ".tmp", VotesPath, true);
    }

    static (int Up, int Down) Tally(string key) =>
        Votes.TryGetValue(key, out var v) ? (v.Values.Count(x => x > 0), v.Values.Count(x => x < 0)) : (0, 0);

    static string TallyText(string key) => Tally(key) is var (u, d) && u + d > 0 ? $" (+{u}{(d > 0 ? $" -{d}" : "")})" : "";

    /// <summary>!up / !down / !toobig / !ads [map]: vote on the map that is on, or on any map by rank, name, id or URL. The same vote again
    /// takes it back; another one replaces it. One vote per SteamID per map. A too-big vote (TooBigVote, Selection.cs) is that player's
    /// dislike - every tally and weight counts it as a downvote - and it also marks the map too big; an ads vote (AdsVote) is a dislike
    /// too, and keeps the map out of random picks altogether. Named: !down with its reason (DownReasons): the vote is that, whatever it
    /// was; the same reason again changes nothing, and never takes the vote back. Without a map a reason goes to the map the player's
    /// popup is asking about, if it is open.</summary>
    static void Vote(string by, string id, string arg, int value, bool named = false)
    {
        var word = value > 0 ? "!up" : value == TooBigVote ? "!toobig" : value == AdsVote ? "!ads" : "!down";
        MapInfo? map;
        if (arg == "" && named && WhyOpen.TryGetValue(id, out var asked)) map = asked.Map;
        else if (arg == "")
        {
            map = S.Map;
            if (AnnouncePending) { Tell(id, "The next map is still loading - vote once it is on, or on a card in the menu"); return; } // S.Map is already the map on its way
            if (map == null) { Tell(id, "Not sure which map this is - vote on its card in the menu instead"); return; }
        }
        else
        {
            var found = Find(arg);
            var hits = found.Where(m => !IsPack(m)).ToList();
            if (hits.Count == 0 && (found.FirstOrDefault(IsPack) ?? PackNamed(arg)) is { } pack) { Tell(id, PackRefusal(pack)); return; }
            if (hits.Count == 0) { Tell(id, $"No map matches '{Clip(arg, 40)}'"); return; }
            if (hits.Count > 1) { Tell(id, Choice(hits, word)); return; }
            map = hits[0];
        }
        CastVote(by, id, map, value, named);
    }

    /// <summary>The vote on a map that is settled: kept, told, and the popup opened or closed. A Dislike that stands without a reason opens the popup;
    /// any other vote on that map closes it, as a map change does.</summary>
    static void CastVote(string by, string id, MapInfo map, int value, bool named)
    {
        var key = Key(map);
        if (!Votes.TryGetValue(key, out var mine)) Votes[key] = mine = new();
        var old = mine.GetValueOrDefault(id);
        if (WhyOpen.TryGetValue(id, out var open) && Key(open.Map) == key) WhyOpen.Remove(id);
        if (named && old == value) { Tell(id, $"Your vote on {Label(map)}: {DownReasons.Of(value)?.Label}, already"); return; }
        if (old == value) mine.Remove(id); else mine[id] = value;
        if (mine.Count == 0) Votes.Remove(key);
        SaveVotes();
        if (value < 0 && old != value) Log($"vote: {by} {id} on {Label(map)}: {DownReasons.Of(value)?.Label}"); // the reason is kept per vote: votes.json's value
        if (value == DownReasons.Plain && !named && old != value) WhyOpen[id] = new WhyAsked(map, Now().AddSeconds(DownReasons.PopupSeconds));
        var (up, down) = Tally(key);
        var marks = string.Join(", ", new[] { TooBigVotes(key) is > 0 and var big ? $"{big} too big" : "", AdsVotes(key) is > 0 and var ads ? $"{ads} ads" : "" }.Where(x => x != ""));
        var tally = $"now +{up} -{down}{(marks != "" ? $" ({marks})" : "")}";
        static string Noun(int v) => v > 0 ? "upvote" : v == TooBigVote ? "too-big vote" : v == AdsVote ? "ads vote" : "downvote";
        static string To(int v) => v > 0 ? "up" : DownReasons.Of(v)?.Short ?? "down";
        Tell(id, old == value ? $"You took back your {Noun(value)} on {Label(map)}, {tally}"
            : old != 0 ? $"You changed your vote on {Label(map)} to {To(value)}, {tally}"
            : value == TooBigVote ? $"You marked {Label(map)} too big (a dislike; random picks take it 1 time in {Math.Round(1 / TooBigChance)}), {tally}"
            : value == AdsVote ? $"You marked {Label(map)}: ads (a dislike; never a random pick), {tally}"
            : value == DownReasons.Plain ? $"You downvoted {Clip(Label(map), PlainVoteLabel)}, {tally}. Why? !down {DownReasons.Words}"
            : value < 0 ? $"You disliked {Label(map)}: {To(value)}, {tally}"
            : $"You upvoted {Label(map)}, {tally}");
    }

    const int PlainVoteLabel = 34; // a plain downvote's note names the map in this much, so "Why? ..." always fits the note line

    /// <summary>The map a player's popup asks about, and until when it stands.</summary>
    sealed record WhyAsked(MapInfo Map, DateTime Until);

    static readonly Dictionary<string, WhyAsked> WhyOpen = new(); // SteamID -> the Dislike still waiting for its reason; in memory: a lost one stays "I did not like the map"

    /// <summary>A click on a reason in the popup (DownReasons.PopupVerb): the vote it asks about takes that reason. Too late (it is gone), nothing changes:
    /// the vote stays what it was.</summary>
    static void PopupReason(string by, string id, string word)
    {
        if (DownReasons.Find(word) is not { } reason) return;
        if (!WhyOpen.TryGetValue(id, out var asked)) { Tell(id, $"That question has passed - !down {reason.Word} still sets it"); return; }
        CastVote(by, id, asked.Map, reason.Value, named: true);
    }

    /// <summary>Every tick's reading of the open popups, into the live state: each one's map and seconds left. One past its time goes after a short grace,
    /// so that a click on its way still lands; its vote stays "I did not like the map".</summary>
    static void PutWhy(HudState h, DateTime now)
    {
        foreach (var (id, asked) in WhyOpen.ToList())
        {
            if (now >= asked.Until.AddSeconds(DownReasons.PopupGraceSeconds)) { WhyOpen.Remove(id); continue; }
            if (now < asked.Until && AccountId(id) is > 0 and var account) h.Why[account] = (Clip(Label(asked.Map), 40), (int)Math.Ceiling((asked.Until - now).TotalSeconds));
        }
    }

    /// <summary>The health page's line about the downvotes' reasons.</summary>
    static string ReasonHealth() => "down votes: " + string.Join(", ", DownReasons.All.Select(r => $"{Votes.Values.Sum(v => v.Values.Count(x => x == r.Value))} {r.Short}"));

    /// <summary>The map a vote key stands for: a listed or official map, else an unlisted workshop id.</summary>
    static MapInfo MapOf(string key) => Maps.Concat(Officials).FirstOrDefault(m => Key(m) == key)
        ?? new MapInfo(0, long.TryParse(key, out var n) ? n : 0, long.TryParse(key, out _) ? $"workshop {key}" : key, 0);

    /// <summary>The ten maps with the most upvotes, fewer downvotes then the better Workshop rank breaking ties: the HUD's "most liked"
    /// list (and the menu's Votes sort shows them all).</summary>
    static List<(MapInfo Map, int Up, int Down)> TopRows() =>
        Votes.Select(kv => (Map: MapOf(kv.Key), Up: kv.Value.Values.Count(x => x > 0), Down: kv.Value.Values.Count(x => x < 0)))
            .Where(r => r.Up > 0).OrderByDescending(r => r.Up).ThenBy(r => r.Down).ThenBy(r => r.Map.Rank == 0 ? int.MaxValue : r.Map.Rank).Take(10).ToList();

    /// <summary>A candidate's weight in the dice, exactly Guide.VoteRule: Workshop rank sets a base from 3 (#1) down to 1 (the last
    /// rank), each upvote doubles it up to UpvoteCap, each downvote quarters it. So +1 doubles, -1 quarters, +1 -1 halves.</summary>
    static double Weight(MapInfo m)
    {
        var (up, down) = Tally(Key(m));
        var listed = InArmsRace(m) ? Maps.Count : ModeWorkshop(ModeOf(m)).Count; // a rank is its own list's (ModeSwitch.cs)
        var rank = !InArmsRace(m) && m.Official ? TopRankBoost // another mode's official maps are what it starts from: weighed like a list's #1
            : m.Rank > 0 && listed > 1 ? 1 + (TopRankBoost - 1) * (listed - Math.Min(m.Rank, listed)) / (listed - 1) : 1;
        return rank * Math.Pow(2, Math.Min(up, UpvoteCap)) * Math.Pow(DownvoteFactor, down);
    }

    /// <summary>What the news line adds to a roll's announcement when the list the setting chose had no map left to roll (the owner's 4c,
    /// 2026-10-24): which list the pick came from instead. Empty after a roll from the chosen list; set by every RandomPick.</summary>
    static string RollNote = "";

    public const string NoNewMapsNote = " - no new map was left to roll, so this one is from the original list";
    public const string NoOriginalMapsNote = " - no original map was left to roll, so this one is a new map";

    /// <summary>The map the dice roll, or null when the toggle is off, the server is empty, or nothing is eligible. Excluded: the map
    /// now loaded, every map with a crash on record and every map on the BROKEN_FILE list (so a roll can never raise a confirm nobody
    /// asked for), every non-map, every map gone from the Workshop, every blocked map, every map a player marked as showing ads (AdsVote),
    /// and - while anything else is left in the group - the last RecentCount maps played. Ranked maps only: officials and unlisted maps
    /// are never rolled. A map's download size plays no part (until 2026-10-24 the dice skipped maps of 400 MB and up unless liked).
    /// Then the owner's rules of 2026-10-22 (Selection.cs): the list - the new maps (#FirstNewRank and up) or the original ones - by the
    /// new-maps setting (always, a coin flip, never), the other list when that one has nothing eligible (RollNote says so on the news
    /// line); in it the too-big maps TooBigChance of the time and the rest otherwise, the other group when that one is empty. Weighted by
    /// Weight.</summary>
    static MapInfo? RandomPick(bool enabled, int humans, Modes.Mode? inMode = null, ISet<string>? exclude = null)
    {
        RollNote = "";
        if (!enabled || humans <= 0) return null;
        inMode ??= CurrentMode; // ModeSwitch.cs: a roll is in one mode - the server's, unless the ballot asks for another
        var current = S.Map != null ? Key(S.Map) : S.LogName; // by workshop id when the bridge picked it, else by the name the server logs
        bool Rollable(MapInfo m) => Key(m) != current && m.Name != current && S.Crashes.GetValueOrDefault(Key(m)) == 0
            && !Broken.ContainsKey(Key(m)) && !S.PlayerCrashes.ContainsKey(Key(m)) && !IsPack(m) && !IsRemoved(m) && !IsBlocked(m) && AdsVotes(Key(m)) == 0 && exclude?.Contains(Key(m)) != true;
        if (!inMode.ArmsRace) return RollOtherMode(PoolOf(inMode).Where(Rollable).ToList(), inMode);
        var eligible = Maps.Where(Rollable).ToList();
        var mode = NewMapsMode;
        var newFirst = mode == NewMapsNever ? false : mode != NewMapsHalf || Dice.NextDouble() < 0.5; // always: the new maps; half: a fair coin; never: the original list
        var bigFirst = Dice.NextDouble() < TooBigChance;
        var recent = S.History.TakeLast(RecentCount).Select(Key).ToHashSet();
        foreach (var newer in new[] { newFirst, !newFirst })
        {
            var list = eligible.Where(m => IsNew(m) == newer).ToList();
            if (list.Count == 0) continue; // nothing eligible on this list: the other list rolls
            var (group, big) = (list.Where(m => TooBig(m) == bigFirst).ToList(), bigFirst);
            if (group.Count == 0) (group, big) = (list, !bigFirst); // that group is empty: the other one, which is then the whole list
            if (group.Any(m => !recent.Contains(Key(m)))) group = group.Where(m => !recent.Contains(Key(m))).ToList();
            var weights = group.Select(Weight).ToList();
            var roll = Dice.NextDouble() * weights.Sum();
            var pick = group[^1];
            for (var i = 0; i < group.Count; i++)
                if ((roll -= weights[i]) < 0) { pick = group[i]; break; }
            if (!SelfTesting)
                Log($"dice: {(newer ? "new" : "original")} maps{(newer != newFirst ? " (the other list had none eligible)" : "")}, {(big ? "too big" : "not too big")}"
                    + $"{(big != bigFirst ? " (the other group was empty)" : "")}: {group.Count} candidates, new maps {mode} -> {Label(pick)}");
            if (newer != newFirst) RollNote = newFirst ? NoNewMapsNote : NoOriginalMapsNote;
            return pick;
        }
        return null;
    }

    /// <summary>A roll in Deathmatch or Wingman (ModeSwitch.cs): the same exclusions (already applied to the candidates), the too-big rule
    /// and the recent-map rule, and the same weights; no new-maps setting (that is Arms Race's list numbers).</summary>
    static MapInfo? RollOtherMode(List<MapInfo> eligible, Modes.Mode mode)
    {
        if (eligible.Count == 0) return null;
        var bigFirst = Dice.NextDouble() < TooBigChance;
        var group = eligible.Where(m => TooBig(m) == bigFirst).ToList();
        if (group.Count == 0) group = eligible;
        var recent = S.History.TakeLast(RecentCount).Select(Key).ToHashSet();
        if (group.Any(m => !recent.Contains(Key(m)))) group = group.Where(m => !recent.Contains(Key(m))).ToList();
        var weights = group.Select(Weight).ToList();
        var roll = Dice.NextDouble() * weights.Sum();
        var pick = group[^1];
        for (var i = 0; i < group.Count; i++)
            if ((roll -= weights[i]) < 0) { pick = group[i]; break; }
        if (!SelfTesting) Log($"dice: {mode.Name}, {group.Count} candidates -> {Label(pick)}");
        return pick;
    }
}
