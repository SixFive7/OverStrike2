// The vote for the next map: the only way the map changes but for the dice's automatic changes (the numbers and the wire codec are MapVote.cs,
// the full rules docs/reference.md, "The vote and the game modes").
//
// A vote opens three ways: a player selects a map (Play on a card, !play, Previous: the vote opens with that map on it and that player's vote on
// it), a player starts one without a map (Start a vote, !next; !random with their vote on Random), or a match ends. Every vote opens everybody's
// window and freezes everybody - bots too, nobody takes damage, nobody closes the window (the plugin does that, off VoteState.Freeze) - until
// everybody present has voted, or MapVote.Seconds (60) as the safety net for somebody away. A player who joins meanwhile is frozen and counted.
// Then everybody unfreezes at the same moment; a result that changes the map keeps everybody frozen through the change's countdown.
//
// The ballot, a page of cards (MapVote.PageCards): Keep - "Keep playing this map" while the match is on, "Play this map again" once it is over (Wingman: the
// whole match, not a round) - every map a player selected, one map of each other mode that suits the players on (Modes.Mode.Suits: Valve's
// maxplayers, so Wingman up to 4), then the server's own mode: one fresh map - a new Workshop upload or a recent update our sync tested, unless a
// card above is fresh already - one surprise from the discovery stock (a map new to us), and the Workshop's favourites the server keeps to fill
// the page. Every card but a player's pick is on the server's disk (RandomQueue.cs), so whichever wins loads at once; an empty slot - no fresh
// map on the disk, the stock not downloaded yet - is a favourite. The cards are rolled by points (Votes.cs). Random is a button of its own,
// without a number. A map a player selects while the vote is open goes on it, with their vote, taking the place of a random card of the
// server's mode nobody voted for - favourites first, then the surprise, the fresh card last; the other modes' cards keep their place - so that
// one page holds it all; a second page only when what must be on it does not fit. The cards are shown with the most votes first, each with its
// tags (MapTags) as they were when it went on.
//
// Every card shown is remembered (S.Shown, S.Met): a card shown and not chosen steps back in the points for a while, and a map shown or played is
// no longer new to us - a stock entry shown is used up.
//
// A vote changes, or goes back, as often as its player likes: a click on another card moves it, a click on their own takes it back. A map a
// player put on the ballot that has no votes left leaves it, until somebody puts it on again. Once everybody present has voted the vote closes
// when the inactivity timer runs out: MapVote.InactivitySeconds after the last activity - a vote, a map put on it, a player's work in the map
// browser - with who reset it last on the screen, or at the vote's limit, whichever is first. The live state carries both timers.
//
// The most votes win; a tie is drawn at random; nobody voting keeps the map mid-match and is Random at a match end. A Wingman map cannot win
// with five or more on: the next most votes do. After any vote nobody can call another for MapVote.WaitSeconds (2 min); a match end opens
// its vote anyway. The maps on an open ballot go first in queues.json (RandomQueue.cs), so the restart watcher downloads them at once.
static partial class Program
{
    /// <summary>One card of the ballot: its number on the wire, how it got there, the map (null for Random, and for Keep on a map the bridge only
    /// knows by name), the mode it loads in, its place among ties (Keep, the picks, the server's mode, the other modes), who selected it, its tags as
    /// it went on, and when it was shown (unix seconds, 0 for Keep and Random).</summary>
    sealed class BallotOption(VoteSource source, MapInfo? map, Modes.Mode mode, int order, string tags = "")
    {
        public int N;
        public VoteSource Source { get; } = source;
        public MapInfo? Map { get; } = map;
        public Modes.Mode Mode { get; } = mode;
        public int Order { get; } = order;
        public string Tags { get; } = tags;
        public long ShownAt;
        public readonly List<(string Id, string Name)> Pickers = new();
    }

    /// <summary>One vote, open and then, for the seconds until its change goes out, decided.</summary>
    sealed class MatchVote
    {
        public long Round;
        public DateTime Opened, Ends, DoneAt;
        public readonly List<BallotOption> Options = new();
        public readonly Dictionary<string, int> Choices = new(); // SteamID -> the option it votes for
        public int PresentAtOpen;                                 // RCON status's humans, when the log stream knew nobody at the open
        public Modes.Mode FillMode = Modes.Default;               // the mode the random cards of the server's mode, and Random, come from
        public bool Open = true;
        public VoteState.Outcome? Result;
        public Change? Change;                                    // what the result started
        public string Starter = "", StarterName = "";             // who called it ("" at a match end)
        public bool MatchEnded;                                   // the match is over: Keep is "Play this map again", nobody voting is Random
        public int Picks;                                         // the picks so far, for their order among ties
        public DateTime? EverybodySince;                          // everybody present has voted, since then
        public DateTime LastChange;                               // the last activity: a vote cast, changed or taken back, a map put on it, the map browser
        public string LastBy = "";                                // ...and whose
    }

    /// <summary>When a vote everybody has voted in closes: the inactivity timer, MapVote.InactivitySeconds after everybody had voted or after the last
    /// activity since.</summary>
    static DateTime SettleEnd(MatchVote b) =>
        (b.EverybodySince is { } e && e > b.LastChange ? e : b.LastChange).AddSeconds(MapVote.InactivitySeconds);

    /// <summary>When a vote ends while somebody has not voted: MapVote.Seconds after it opened - or, with the second inactivity limit on,
    /// MapVote.IdleLimitSeconds after the last activity, MapVote.HardCapSeconds after it opened at the most.</summary>
    static DateTime VoteEnds(MatchVote b) => MapVote.IdleLimitSeconds <= 0 ? b.Ends
        : new[] { b.Opened.AddSeconds(MapVote.HardCapSeconds), (b.LastChange > b.Opened ? b.LastChange : b.Opened).AddSeconds(MapVote.IdleLimitSeconds) }.Min();

    /// <summary>A player at work in the map browser while the vote is open (!mvb &lt;round&gt;, from the plugin): it resets the inactivity timer.</summary>
    static void VoteBusy(string by, string steamId, string arg)
    {
        if (!long.TryParse(arg, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var round)
            || Ballot is not { Open: true } b || b.Round != round || !PresentIds(Now()).Contains(steamId)) return;
        (b.LastChange, b.LastBy) = (Now(), by);
    }

    /// <summary>The cards' places among ties: Keep, the picks, the server's mode - its fresh card, its surprise, then its favourites, so that a pick
    /// replaces the favourites first and the fresh card last (Replaceable) - and the other modes.</summary>
    const int KeepOrder = 0, PickOrder = 100, FillOrder = 200, FreshOrder = FillOrder, SurpriseOrder = FillOrder + 1, FavouriteOrder = FillOrder + 2, OtherOrder = 300;

    static MatchVote? Ballot;
    static long LastRound;
    static DateTime VoteWaitUntil; // nobody calls a vote before this (MapVote.WaitSeconds after the last one)

    /// <summary>The players present: in game by the log stream, or reloading inside the load grace. One who left is not.</summary>
    static HashSet<string> PresentIds(DateTime now) => Players.Where(kv => kv.Value.GoneSince == null
        && (kv.Value.ReloadSince is not { } r || (now - r).TotalSeconds <= ReloadGraceSeconds)).Select(kv => kv.Key).ToHashSet();

    static int VotePresent(MatchVote b, DateTime now) => Math.Max(PresentIds(now).Count, b.PresentAtOpen);

    /// <summary>The key the map that is on goes by: its own, or the name the server logs when the bridge did not pick it.</summary>
    static string CurrentKey => S.Map != null ? Key(S.Map) : S.LogName ?? "";

    /// <summary>The map that is on, in the mode it is on in.</summary>
    static bool IsOn(MapInfo m) => CurrentKey != "" && Key(m) == CurrentKey && ModeOf(m).Id == CurrentMode.Id;

    static string OptionKey(BallotOption o) => o.Source == VoteSource.Keep ? CurrentKey : o.Map is { } m ? Key(m) : "";

    /// <summary>Why nobody may call a vote now, or null: a hold, a change under way, a map loading, a restart counting down, the wait after the last vote.</summary>
    static string? CannotCall(DateTime now)
    {
        if (HudHeld()) return "Steam is reviewing the HUD, so a vote cannot start now - The game plays on";
        if (Pending is { } p) return $"Already changing to {Clip(Label(p.Map), 40)} - Wait for it";
        if (AnnouncePending) return "The next map is still loading - Wait for it";
        if (RestartRefusal(now) is { } restart) return restart; // Restarts.cs
        if (now < VoteWaitUntil) return $"A vote was just held - The next one can start in {MapVote.Left((int)Math.Ceiling((VoteWaitUntil - now).TotalSeconds))}";
        return null;
    }

    /// <summary>One draw of the dice for a card: a map of the mode from the kept maps on the server's disk (KeptOnDisk), by points, never one already
    /// taken - and taken by it. Null: none is left.</summary>
    static MapInfo? DrawKept(Modes.Mode mode, HashSet<string> taken)
    {
        var pool = PoolOf(mode).Where(m => CanRoll(m, taken) && KeptOnDisk(m)).ToList();
        if (RollIn(pool, Dice.NextDouble() < TooBigChance) is not { } r || !taken.Add(Key(r.Pick))) return null;
        return r.Pick;
    }

    /// <summary>The fresh card's draw: a fresh map of the mode on the server's disk - kept, or a downloaded stock entry - by points. Null: none.</summary>
    static MapInfo? DrawFresh(Modes.Mode mode, HashSet<string> taken)
    {
        var pool = PoolOf(mode).Where(m => CanRoll(m, taken) && IsFresh(m) && KeptOnDisk(m)).ToList();
        var have = pool.Select(Key).ToHashSet();
        pool.AddRange(StockOnDisk(mode, taken).Where(m => IsFresh(m) && have.Add(Key(m))));
        if (RollIn(pool, Dice.NextDouble() < TooBigChance) is not { } r || !taken.Add(Key(r.Pick))) return null;
        return r.Pick;
    }

    /// <summary>The surprise: the first of the mode's stock entries on the server's disk that may be shown. Null: none is downloaded yet.</summary>
    static MapInfo? DrawSurprise(Modes.Mode mode, HashSet<string> taken) =>
        StockOnDisk(mode, taken).FirstOrDefault(m => CanRoll(m, taken)) is { } m && taken.Add(Key(m)) ? m : null;

    /// <summary>A card shown: its map is no longer new to us, and a card the dice or a player put on it is remembered with the time, for the step back
    /// a card shown and not chosen takes in the points (Votes.cs: ShownPenalty). Showings older than ShownFadeDays are forgotten.</summary>
    static void Showing(BallotOption o, DateTime now)
    {
        if (o.Map is not { } m) { if (o.Source == VoteSource.Keep && CurrentKey != "") S.Met.Add(CurrentKey); return; }
        var key = Key(m);
        S.Met.Add(key);
        if (o.Source is VoteSource.Keep or VoteSource.Random) return;
        var t = new DateTimeOffset(now).ToUnixTimeSeconds();
        o.ShownAt = t;
        if (!S.Shown.TryGetValue(key, out var times)) S.Shown[key] = times = new();
        times.Add(t);
        var old = t - ShownFadeDays * 86400L;
        foreach (var (k, list) in S.Shown.ToList())
        {
            list.RemoveAll(x => x <= old);
            if (list.Count == 0) S.Shown.Remove(k);
        }
    }

    /// <summary>Opens a vote: builds the ballot by its recipe, casts the caller's vote on the map they selected, remembers what it shows, says so on
    /// the news line, and puts the ballot's maps first in queues.json. False when there is nothing to put on it. A match end's vote ignores the
    /// wait.</summary>
    static bool OpenBallot(DateTime now, int humans, string starter = "", string starterName = "", MapInfo? pick = null, bool matchEnd = false)
    {
        var present = PresentIds(now).Count;
        var players = Math.Max(present, humans);
        var round = Math.Max(new DateTimeOffset(now).ToUnixTimeSeconds(), LastRound + 1);
        var b = new MatchVote { Round = round, Opened = now, Ends = now.AddSeconds(MapVote.Seconds), PresentAtOpen = present > 0 ? 0 : humans,
            Starter = starter, StarterName = starterName, MatchEnded = matchEnd };
        b.FillMode = CurrentMode.Suits(players) ? CurrentMode : Modes.Default; // Wingman with 5 on: its random cards and Random come from Arms Race
        var taken = new HashSet<string>();
        if (CurrentKey != "") { b.Options.Add(new BallotOption(VoteSource.Keep, S.Map, CurrentMode, KeepOrder, S.Map is { } on ? TagsOf(on) : "")); taken.Add(CurrentKey); }
        if (pick != null && taken.Add(Key(pick)))
        {
            var p = new BallotOption(VoteSource.Picked, pick, ModeOf(pick), PickOrder + b.Picks++, TagsOf(pick, picked: true));
            p.Pickers.Add((starter, starterName));
            b.Options.Add(p);
        }
        BallotOption Rolled(MapInfo map, Modes.Mode mode, int order) => new(VoteSource.Rolled, map, mode, order, TagsOf(map));
        // one map of each other mode that suits the players on - the pick stands for its own mode - from that mode's kept maps on the disk
        var others = new List<BallotOption>();
        foreach (var mode in Modes.All.Where(m => m.Id != b.FillMode.Id && m.Suits(players)))
        {
            if (pick != null && ModeOf(pick).Id == mode.Id) continue;
            if (DrawKept(mode, taken) is { } map) others.Add(Rolled(map, mode, OtherOrder + others.Count));
        }
        // the server's own mode until the page is full: the fresh card, the surprise, the favourites
        var fill = new List<BallotOption>();
        var room = MapVote.PageCards - b.Options.Count - others.Count;
        if (room > 0 && !b.Options.Concat(others).Any(o => o.Map is { } x && IsFresh(x)) && DrawFresh(b.FillMode, taken) is { } fresh) { fill.Add(Rolled(fresh, b.FillMode, FreshOrder)); room--; }
        if (room > 0 && DrawSurprise(b.FillMode, taken) is { } surprise) { fill.Add(Rolled(surprise, b.FillMode, SurpriseOrder)); room--; }
        while (room > 0 && DrawKept(b.FillMode, taken) is { } favourite) { fill.Add(Rolled(favourite, b.FillMode, FavouriteOrder + fill.Count)); room--; }
        b.Options.AddRange(fill);
        b.Options.AddRange(others);
        if (b.Options.Count(o => o.Source != VoteSource.Keep) == 0) { Log("vote: nothing to put on the ballot"); return false; }
        for (var i = 0; i < b.Options.Count; i++) b.Options[i].N = i + 1; // the numbers in that order: Keep 1, the pick 2, ...
        b.Options.Add(new BallotOption(VoteSource.Random, null, b.FillMode, int.MaxValue) { N = MapVote.RandomOption });
        if (pick != null && b.Options.FirstOrDefault(o => o.Source == VoteSource.Picked) is { } listed) b.Choices[starter] = listed.N;
        (b.LastChange, b.LastBy) = (now, starterName);
        foreach (var o in b.Options) Showing(o, now);
        CatalogVersion++; // the maps shown are no longer new to us: their cards' tags change
        Save();

        Ballot = b; LastRound = round;
        var count = VotePresent(b, now);
        Log($"vote: round {round} open for {MapVote.Seconds} s{(matchEnd ? " at a match end" : starter != "" ? $", called by {starterName} {starter}" : "")}, {count} present: "
            + string.Join(" | ", b.Options.Select(o => $"{o.N}. {OptionLabel(b, o)} ({o.Source.ToString().ToLowerInvariant()}, {o.Mode.Id}{(o.Order is FreshOrder or SurpriseOrder ? o.Order == FreshOrder ? ", fresh" : ", surprise" : "")}{(o.Tags != "" ? $", {o.Tags}" : "")})")));
        var how = $"Everybody votes, {MapVote.Seconds} s at the most - {HowToVote}";
        Announce(matchEnd ? $"Match over - Vote for the next map: {how}"
            : pick != null ? $"{Clip(starterName, 16)} put {Clip(Label(pick), 32)}{WhyNote(pick)}{ModeTag(pick)} to the vote: {how}"
            : $"{Clip(starterName, 16)} started a vote for the next map: {how}", MapVote.Seconds);
        FillStock(); // RandomQueue.cs: the shown stock entries are used up and rolled anew, and the ballot's maps go first in queues.json, so they download now
        BallotTick(now); // alone, the caller's own vote ends it at once
        return true;
    }

    /// <summary>How the news line tells players to vote: a card, and its number while the cards have numbers (MapVote.Numbers).</summary>
    static string HowToVote => MapVote.Numbers == MapVote.Numbering.None ? "Click a card (again: take it back)" : "Click a card or type its number";

    static string KeepWords(MatchVote b) => b.MatchEnded ? "Play this map again" : "Keep playing this map";

    static string OptionLabel(MatchVote b, BallotOption o) => o.Source == VoteSource.Keep ? $"{KeepWords(b)} ({CurrentLabel()})" : o.Map is { } m ? Label(m) + ModeTag(m) : "Random";

    /// <summary>Start a vote (the board's button, !next; !random with this player's vote on Random): a vote without a map of the caller's own, Keep
    /// on it as on every ballot. While a vote is open, !random moves this player's vote to Random.</summary>
    static void StartVote(string by, string steamId, bool forRandom = false)
    {
        var now = Now();
        if (Ballot is { Open: true } open)
        {
            if (forRandom) { CastBallot(by, steamId, $"{open.Round} {MapVote.RandomOption}"); return; }
            Tell(steamId, "The vote for the next map is on - Click a card in it");
            return;
        }
        if (CannotCall(now) is { } why) { Tell(steamId, why); return; }
        Present(steamId, by);
        if (!OpenBallot(now, Math.Max(Humans(), 1), steamId, by)) { Tell(steamId, "No map is eligible for a vote right now"); return; }
        Log($"vote: started by {by} {steamId}{(forRandom ? ", with their vote on Random" : "")}");
        if (forRandom && Ballot is { Open: true } b) { b.Choices[steamId] = MapVote.RandomOption; (b.LastChange, b.LastBy) = (now, by); BallotTick(now); }
    }

    /// <summary>A map a player selected (Play, !play, Previous), past every refusal and a risky map's second click: a vote with it on the ballot and
    /// their vote on it - or, while a vote is open, onto that ballot with their vote. The map that is on is Keep: a vote for it while one is open,
    /// nothing otherwise.</summary>
    static void SelectMap(string by, string steamId, MapInfo map, DateTime now)
    {
        Present(steamId, by);
        if (Ballot is { Open: true } open) { AddToBallot(open, by, steamId, map, now); return; }
        if (IsOn(map)) { Tell(steamId, $"{Clip(Label(map), 40)} is on now - Start a vote to change it"); return; }
        if (CannotCall(now) is { } why) { Tell(steamId, why); return; }
        if (!OpenBallot(now, Math.Max(Humans(), 1), steamId, by, map)) Tell(steamId, "No vote could open right now - Try again in a moment");
    }

    /// <summary>A player's vote: !mv &lt;round&gt; &lt;n&gt; from the vote screen (0 takes it back, 99 is Random) - as often as they like.</summary>
    static void CastBallot(string by, string steamId, string arg)
    {
        if (MapVote.ParseClick($"{MapVote.Verb} {arg}") is not { } click || Ballot is not { Open: true } b || b.Round != click.Round) { Tell(steamId, "That vote is over"); return; }
        var now = Now();
        if (click.Option == 0)
        {
            if (!b.Choices.Remove(steamId)) return;
            Log($"vote: {by} {steamId} took their vote back");
            (b.LastChange, b.LastBy) = (now, by);
            BallotTick(now);
            return;
        }
        if (b.Options.FirstOrDefault(o => o.N == click.Option) is not { } option) { Tell(steamId, "That card is not on this ballot any more"); return; }
        Present(steamId, by);
        var was = b.Choices.GetValueOrDefault(steamId);
        b.Choices[steamId] = click.Option;
        if (was != click.Option) { Log($"vote: {by} {steamId} votes {click.Option} ({OptionLabel(b, option)}){(was > 0 ? $", was {was}" : "")}"); (b.LastChange, b.LastBy) = (now, by); }
        BallotTick(now);
    }

    /// <summary>A map a player put on the ballot that has no votes left leaves it, until somebody puts it on again.</summary>
    static void DropUnvotedPicks(MatchVote b)
    {
        var gone = b.Options.Where(o => o.Source == VoteSource.Picked && !b.Choices.ContainsValue(o.N)).ToList();
        foreach (var o in gone)
        {
            b.Options.Remove(o);
            Log($"vote: {Label(o.Map!)} has no votes left - it leaves the ballot");
        }
        if (gone.Count > 0) ExportQueues();
    }

    /// <summary>A map selected while the vote is open: onto the ballot for everybody, with this player's vote. A map the ballot holds already - the
    /// map that is on is Keep - gets the vote alone; somebody else's pick is joined. A new card takes the place of a random card of the server's
    /// mode nobody voted for, and its number, so one page holds it all; MapVote.MaxMaps a ballot.</summary>
    static void AddToBallot(MatchVote b, string by, string steamId, MapInfo map, DateTime now)
    {
        var key = Key(map);
        if (b.Options.FirstOrDefault(o => o.Source != VoteSource.Random && OptionKey(o) == key && (o.Source == VoteSource.Keep ? IsOn(map) : ModeOf(map).Id == o.Mode.Id)) is { } there)
        {
            if (b.Choices.GetValueOrDefault(steamId) != there.N) (b.LastChange, b.LastBy) = (now, by);
            b.Choices[steamId] = there.N;
            if (there.Source == VoteSource.Picked && there.Pickers.All(p => p.Id != steamId)) there.Pickers.Add((steamId, by));
            Tell(steamId, there.Source == VoteSource.Keep ? $"{KeepWords(b)} is on the vote - Your vote is on it" : $"{Clip(Label(map), 40)} is on the vote already - Your vote is on it");
            Log($"vote: {by} {steamId} asked for {Label(map)}, on the ballot as {there.N} - votes {there.N}");
            BallotTick(now);
            return;
        }
        var cards = b.Options.Where(o => o.Source != VoteSource.Random).ToList();
        if (cards.Count >= MapVote.MaxMaps) { Tell(steamId, $"The vote holds {MapVote.MaxMaps} maps - vote for one of them"); return; }
        var o = new BallotOption(VoteSource.Picked, map, ModeOf(map), PickOrder + b.Picks++, TagsOf(map, picked: true));
        o.Pickers.Add((steamId, by));
        if (cards.Count >= MapVote.PageCards && Replaceable(b) is { } gone)
        {
            b.Options.Remove(gone);
            o.N = gone.N; // nobody voted for it: its number is free
            Log($"vote: {Label(gone.Map!)} makes room for {Label(map)}, as {o.N}");
        }
        else o.N = cards.Count == 0 ? 1 : cards.Max(x => x.N) + 1;
        b.Options.Insert(b.Options.FindIndex(x => x.Source == VoteSource.Random) is var r and >= 0 ? r : b.Options.Count, o);
        b.Choices[steamId] = o.N;
        (b.LastChange, b.LastBy) = (now, by);
        Showing(o, now);
        CatalogVersion++;
        Save();
        Log($"vote: {by} {steamId} added {o.N}. {OptionLabel(b, o)} and votes for it");
        Announce($"{Clip(by, 16)} added {Clip(Label(map), 32)}{WhyNote(map)}{ModeTag(map)} to the vote", Math.Max(5, (int)Math.Ceiling((b.Ends - now).TotalSeconds)));
        FillStock(); // a stock entry picked is used up; the new card goes into queues.json
        BallotTick(now);
    }

    /// <summary>The random card a new pick replaces: one of the server's mode nobody voted for - the favourites first, the last rolled first, then the
    /// surprise, the fresh card last. The other modes' cards keep their place. Null: there is none, and the ballot grows a page.</summary>
    static BallotOption? Replaceable(MatchVote b) =>
        b.Options.Where(o => o.Source == VoteSource.Rolled && !b.Choices.ContainsValue(o.N) && o.Order >= FillOrder && o.Order < OtherOrder).MaxBy(o => o.Order);

    /// <summary>Every tick, and after every vote: a voter who left takes their vote along; everybody present having voted ends the vote, the timer
    /// ends it. Once decided it stays on screen until its change has gone out. A vote always ends: whatever goes wrong in here drops it, so nobody
    /// stays frozen and the rest of the tick runs.</summary>
    static void BallotTick(DateTime now)
    {
        try { BallotStep(now); }
        catch (Exception e) { Log($"vote: its timer failed: {e}"); EndBallot("its timer failed"); }
    }

    static void BallotStep(DateTime now)
    {
        if (Ballot is not { } b) return;
        if (!b.Open)
        {
            if (Pending == null || Pending != b.Change || (now - b.DoneAt).TotalSeconds > ChangeDelay + 5) Ballot = null;
            return;
        }
        var present = PresentIds(now);
        foreach (var id in b.Choices.Keys.Where(id => !present.Contains(id)).ToList()) { b.Choices.Remove(id); b.LastChange = now; }
        DropUnvotedPicks(b);
        var count = VotePresent(b, now);
        if (count == 0) { Log("vote: everybody left - no change"); Ballot = null; return; }
        var everybody = b.Choices.Count >= count; // every human present has voted (a vote of a player not counted as present counts too)
        if (!everybody) b.EverybodySince = null;
        else b.EverybodySince ??= now;
        if (now < VoteEnds(b) && (!everybody || now < SettleEnd(b))) return; // InactivitySeconds 0: the moment the last one votes
        var tally = b.Options.ToDictionary(o => o.N, o => b.Choices.Values.Count(n => n == o.N));
        bool Fits(BallotOption o) => o.Source is VoteSource.Keep or VoteSource.Random || o.Mode.Suits(count); // no Wingman map with five or more on
        var ranked = b.Options.Where(Fits).ToList();
        var most = ranked.Max(o => tally[o.N]);
        foreach (var o in b.Options.Where(o => !Fits(o) && tally[o.N] > 0 && tally[o.N] >= most))
            Log($"vote: {OptionLabel(b, o)} has {tally[o.N]} vote(s), but {o.Mode.Name} takes {o.Mode.MaxPlayers} players and {count} are on - it cannot win");
        if (most == 0)
        {
            // Nobody voted: Random at a match end. Mid-match the map stays - also when the bridge does not know which map that is, so the ballot has no Keep card.
            CloseBallot(b, now, b.MatchEnded ? b.Options.First(o => o.Source == VoteSource.Random)
                : b.Options.FirstOrDefault(o => o.Source == VoteSource.Keep) ?? new BallotOption(VoteSource.Keep, null, CurrentMode, KeepOrder), "none", tally, count);
            return;
        }
        var top = ranked.Where(o => tally[o.N] == most).ToList();
        if (top.Count == 1) CloseBallot(b, now, top[0], everybody ? "all" : "most", tally, count);
        else CloseBallot(b, now, top[Dice.Next(top.Count)], "tie", tally, count);
    }

    /// <summary>The map that is on, for "... stays": its label, or plain words when the bridge does not know which map it is.</summary>
    static string OnWords() => CurrentKey != "" ? Clip(CurrentLabel(), 40) : "The map";

    /// <summary>The vote is decided: Keep unfreezes everybody at once and the map plays on; anything else goes out through the usual countdown,
    /// everybody still frozen. Either way the next vote waits MapVote.WaitSeconds. The chosen card is no showing that steps back.</summary>
    static void CloseBallot(MatchVote b, DateTime now, BallotOption o, string how, Dictionary<int, int> tally, int present)
    {
        b.Open = false; b.DoneAt = now;
        VoteWaitUntil = now.AddSeconds(MapVote.WaitSeconds);
        if (o.Map is { } chosen && o.ShownAt > 0 && S.Shown.TryGetValue(Key(chosen), out var times))
        {
            times.Remove(o.ShownAt);
            if (times.Count == 0) S.Shown.Remove(Key(chosen));
        }
        // The maintenance flag stops what the bridge does by itself: a match end's vote, and the dice when nobody voted. A vote a player called
        // decides as a pick always did (docs/reference.md, "Maintenance flag").
        if ((b.Starter == "" || how == "none") && InMaintenance())
        {
            Log("vote: decided while the maintenance flag is set - a match end's vote, or nobody voted: no change");
            Announce($"Maintenance - {OnWords()} stays");
            Ballot = null;
            return;
        }
        StartRollOwed = false; // the players have had their say: a server start's roll is no longer owed (TryStartRoll waits for an open vote)
        var votes = tally.GetValueOrDefault(o.N);
        var cast = tally.Values.Sum();
        if (o.Source == VoteSource.Keep)
        {
            Ballot = null; // everybody unfreezes, every window closes, at the same moment (the plugin)
            Save();
            Log($"vote: round {b.Round} decided ({how}, {votes} of {cast} vote(s), {present} present): {KeepWords(b).ToLowerInvariant()} - {CurrentLabel()}");
            Announce(how == "none" ? $"Nobody voted - {OnWords()} stays"
                : b.MatchEnded ? $"The vote: {Clip(CurrentLabel(), 40)} again ({votes} of {cast}) - Its next match starts on it"
                : $"The vote kept {Clip(CurrentLabel(), 40)} ({votes} of {cast})");
            return;
        }
        var target = o.Source == VoteSource.Random ? null : o.Map is { } m && Unplayable(m) == null ? m : null;
        target ??= RandomNext(b.FillMode); // Random - or a card whose map has become unplayable since: the dice over the maps on the disk (RandomQueue.cs)
        if (target == null) { Log("vote: nothing eligible to load - the map stays"); Ballot = null; return; }
        var change = new Change(target, "the vote");
        b.Result = new VoteState.Outcome(o.N, how, Clip(Label(target), 40), ModeOf(target).Id);
        b.Change = change;
        Save();
        Log($"vote: round {b.Round} decided ({how}, {votes} of {cast} vote(s), {present} present): {o.N}. {OptionLabel(b, o)} -> {Label(target)} in {ModeOf(target).Name}");
        var shown = $"{Label(target)}{Detail(target, MapTags.WhyWords(TagsOf(target), picked: false))}{ModeTag(target)}";
        StartChange(change, how switch
        {
            "none" => $"Nobody voted - Rolling the dice: {shown}, changing in {ChangeDelay} s",
            _ when o.Source == VoteSource.Random => $"The vote chose Random ({votes} of {cast}) - Rolling the dice: {shown}, changing in {ChangeDelay} s",
            "tie" => $"A tie at {votes} vote{(votes == 1 ? "" : "s")} each, drawn by the dice: {shown}, changing in {ChangeDelay} s",
            "all" => $"Everybody has voted: {shown} ({votes} of {cast}) - Changing in {ChangeDelay} s",
            _ => $"The vote chose {shown} ({votes} of {cast} votes) - Changing in {ChangeDelay} s",
        });
    }

    /// <summary>Drops a vote without a change: a restart, a map start nobody asked for, the server emptied. Everybody unfreezes.</summary>
    static void EndBallot(string why)
    {
        if (Ballot is { Open: true } b) Log($"vote: round {b.Round} dropped - {why}");
        Ballot = null;
    }

    /// <summary>A list changed under an open vote (Reload.cs, Removed.cs): a card whose map is no longer playable leaves the ballot, its votes with
    /// it. Returns how many went.</summary>
    static int DropUnplayable(MatchVote b)
    {
        var gone = b.Options.Where(o => o.Source is VoteSource.Picked or VoteSource.Rolled && o.Map is { } m && Unplayable(m) != null).ToList();
        foreach (var o in gone)
        {
            b.Options.Remove(o);
            foreach (var id in b.Choices.Where(c => c.Value == o.N).Select(c => c.Key).ToList()) b.Choices.Remove(id);
            Log($"vote: {Label(o.Map!)} leaves the ballot - {M.Plain(Unplayable(o.Map!))}");
        }
        return gone.Count;
    }

    /// <summary>The vote and the modes, for the live state (the lines VoteState.Encode adds after HudState's): the cards with the most votes first.
    /// While it is open both timers are on the wire: SecondsLeft, the vote's limit, always; Inactivity and who reset it once everybody has voted,
    /// never more than SecondsLeft - the limit closes it first.</summary>
    static VoteState BuildVoteState(DateTime now)
    {
        var mode = CurrentMode;
        var v = new VoteState { Mode = mode.Id, ModeName = mode.Name };
        var humans = Math.Max(PresentIds(now).Count, 1);
        foreach (var m in Modes.All) v.Modes.Add(new VoteState.ModeInfo(m.Id, m.Name, m.MaxPlayers, m.Suits(humans)));
        if (Ballot is not { Open: true } && now < VoteWaitUntil) (v.WaitFor, v.WaitUntilMs) = ((int)Math.Ceiling((VoteWaitUntil - now).TotalSeconds), HudState.Ms(VoteWaitUntil));
        if (Ballot is not { } b) return v;
        var count = VotePresent(b, now);
        v.Round = b.Round; v.Open = b.Open; v.MatchEnded = b.MatchEnded;
        v.Starter = b.Starter != "" ? Math.Max(0, AccountId(b.Starter)) : 0;
        v.Freeze = b.Open || (Pending is { } p && p == b.Change);
        v.SecondsLeft = b.Open ? Math.Max(0, (int)Math.Ceiling((VoteEnds(b) - now).TotalSeconds))
            : Pending is { } q && q == b.Change ? Math.Max(0, (int)Math.Ceiling((q.FiresAt - now).TotalSeconds)) : 0;
        v.EndsAtMs = b.Open ? HudState.Ms(VoteEnds(b)) : Pending is { } q2 && q2 == b.Change ? HudState.Ms(q2.FiresAt) : 0; // the plugin counts down from these (HudTransport.cs)
        if (b.Open && MapVote.InactivitySeconds > 0 && b.EverybodySince != null)
        {
            (v.Inactivity, v.ResetBy) = (Math.Min(Math.Max(1, (int)Math.Ceiling((SettleEnd(b) - now).TotalSeconds)), Math.Max(1, v.SecondsLeft)), b.LastBy);
            v.InactivityAtMs = HudState.Ms(SettleEnd(b));
        }
        (v.Present, v.Cast) = (count, b.Choices.Count);
        // who voted what, by name (the cards' chips, the hover), and who has not voted yet (the waiting line): the log stream's players, the fakes of a test too
        string NameOf(string id) => Players.TryGetValue(id, out var pl) && pl.Name != "" ? Clip(pl.Name, 24) : id;
        foreach (var g in b.Choices.GroupBy(c => c.Value)) v.Voters[g.Key] = g.Select(c => NameOf(c.Key)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (b.Open) v.Waiting = PresentIds(now).Where(id => !b.Choices.ContainsKey(id)).Select(NameOf).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        int Votes(BallotOption o) => b.Choices.Values.Count(n => n == o.N);
        foreach (var o in b.Options.OrderBy(o => o.Source == VoteSource.Random).ThenByDescending(Votes).ThenBy(o => o.Order))
        {
            var keep = o.Source == VoteSource.Keep;
            v.Options.Add(new VoteState.Option(o.N, o.Mode.Id, o.Source, o.Map is { } m ? ArgOf(m) : "", OptionKey(o),
                keep ? KeepWords(b) : o.Map is { } l ? Clip(Label(l), 40) : "Random",
                keep ? Clip(CurrentLabel(), 40) : o.Map is { Official: false, Rank: > 0 } s ? Size(s) : "",
                Votes(o), Names(o.Pickers.Select(x => Clip(x.Name, 16)).Distinct()), o.Tags));
        }
        foreach (var (id, n) in b.Choices) if (AccountId(id) is >= 0 and var account) v.Choices[account] = n;
        v.Result = b.Result;
        return v;
    }

    /// <summary>The health page's line about the vote.</summary>
    static string VoteHealth() => "vote: " + (Ballot is { } b ? $"round {b.Round} {(b.Open ? "open" : "decided")}, {b.Choices.Count} vote(s), {b.Options.Count - 1} card(s)" : "none open")
        + (Now() < VoteWaitUntil ? $"; the next can be called in {(int)Math.Ceiling((VoteWaitUntil - Now()).TotalSeconds)} s" : "") + $"; cards numbered {MapVote.Numbers.ToString().ToLowerInvariant()}";
}
