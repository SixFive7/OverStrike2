// The vote for the next map: the only way the map changes but for the dice's automatic changes (the numbers and the wire codec are MapVote.cs,
// the full rules docs/reference.md, "The vote and the game modes").
//
// A vote opens three ways: a player selects a map (Play on a card, !play, Previous: the vote opens with that map on it and that player's vote on
// it), a player starts one without a map (Start a vote, !next; !random with their vote on Random), or a match ends. Every vote opens everybody's
// window and freezes everybody - bots too, nobody takes damage, nobody closes the window (the plugin does that, off VoteState.Freeze) - until
// everybody present has voted, or MapVote.Seconds (60) as the safety net for somebody away. A player who joins meanwhile is frozen and counted.
// Then the window shows the result, everybody still frozen: through the change's countdown, or for MapVote.KeptSeconds when the vote kept the map; then
// everybody unfreezes at the same moment.
//
// What it says (the rows are in Messages.cs): who started it stands in the window nobody can close, its chat and board copy is the line for everybody;
// every vote - cast, changed, taken back, a map put on it - says in chat and on the board who voted and who still has to (VoteLine); the result likewise; and
// a player who tries to call a vote when none may be called reads why in their own line, and chat and the board name them (RefuseCall).
//
// The ballot, a page of cards (MapVote.PageCards): Keep - "Keep playing this map" while the match is on, "Play this map again" once it is over (Wingman: the
// whole match, not a round) - every map a player selected, one map of each other mode that suits the players on (Modes.Mode.Suits: Valve's
// maxplayers, so Wingman up to 4), then the server's own mode: one fresh map - a new Workshop upload or a recent update our sync tested, unless a
// card above is fresh already - one surprise from the discovery stock (a map new to us), and the Workshop's favourites the server keeps to fill
// the page. Every card but a player's pick is on the server's disk (Stock.cs), so whichever wins loads at once; an empty slot - no fresh
// map on the disk, the stock not downloaded yet - is a favourite. The cards are rolled by points (Dice.cs). Random is a button of its own,
// without a number. A map a player selects while the vote is open goes on it, with their vote, taking the place of a random card of the
// server's mode nobody voted for - favourites first, then the surprise, the fresh card last; the other modes' cards keep their place - so that
// one page holds it all; a second page only when what must be on it does not fit. Each card has its tags (MapTags) as they were when it went on.
//
// While the vote is open no card moves: each keeps the place it went on in - an added map the very place of the card it replaced - and a card's
// number is never given again in that vote, so a click sent for a card that has just gone counts for nothing rather than for the map in its place
// (CastBallot says so). Once the vote is decided, the cards show with the most votes first.
//
// Every card shown is remembered (S.Shown, S.Met): a card shown and not chosen steps back in the points for a while, and a map shown or played is
// no longer new to us - a stock entry shown is used up.
//
// A vote changes, or goes back, as often as its player likes: a click on another card moves it, a click on their own takes it back. A map a
// player put on the ballot that has no votes left leaves it, until somebody puts it on again; the random card it replaced comes back in its place,
// and whoever put it on is told. Once everybody present has voted the vote closes
// when the inactivity timer runs out: MapVote.InactivitySeconds after the last activity - a vote, a map put on it, a player's work in the map
// browser - with who reset it last on the screen, or at the vote's limit, whichever is first. The live state carries both timers.
//
// The most votes win; a tie is drawn at random; nobody voting keeps the map mid-match and is Random at a match end. A Wingman map cannot win
// with five or more on: the next most votes do. After any vote nobody can call another for MapVote.WaitSeconds (2 min); a match end opens
// its vote anyway. The maps on an open ballot go first in queues.json (Stock.cs), so the restart watcher downloads them at once.
static partial class Program
{
    /// <summary>One card of the ballot: its number on the wire, how it got there, the map (null for Random, and for Keep on a map the bridge only
    /// knows by name), the mode it loads in, its place among ties (Keep, the picks, the server's mode, the other modes), who selected it, its tags as
    /// it went on, and when it was shown (unix seconds, 0 for Keep and Random). Slot: its place on the screen while the vote is open, which never
    /// changes; Replaced: the random card a pick took the place of, which gets that place back when the pick leaves.</summary>
    sealed class BallotOption(VoteSource source, MapInfo? map, Modes.Mode mode, int order, string tags = "")
    {
        public int N;
        public int Slot;
        public BallotOption? Replaced;
        public VoteSource Source { get; } = source;
        public MapInfo? Map { get; } = map;
        public Modes.Mode Mode { get; } = mode;
        public int Order { get; } = order;
        public string Tags { get; } = tags;
        public long ShownAt;
        public readonly List<(string Id, string Name)> Pickers = new();
    }

    /// <summary>One vote, open and then, for the seconds until its change goes out, decided.</summary>
    sealed class BallotState
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
        public DateTime? KeptUntil;                               // the vote kept the map: the window shows that until then, everybody still frozen
        public string Starter = "", StarterName = "";             // who called it ("" at a match end)
        public bool MatchEnded;                                   // the match is over: Keep is "Play this map again", nobody voting is Random
        public bool ServerStart;                                  // a server start with players on: the vote in place of the start roll, nobody voting is Random
        public int Picks;                                         // the picks so far, for their order among ties
        public readonly HashSet<int> Given = new();               // every card number this vote has given (FreshNumber): none is given twice
        public DateTime? EverybodySince;                          // everybody present has voted, since then
        public DateTime LastChange;                               // the last activity: a vote cast, changed or taken back, a map put on it, the map browser
        public string LastBy = "";                                // ...and whose
    }

    /// <summary>When a vote everybody has voted in closes: the inactivity timer, MapVote.InactivitySeconds after everybody had voted or after the last
    /// activity since.</summary>
    static DateTime SettleEnd(BallotState b) =>
        (b.EverybodySince is { } e && e > b.LastChange ? e : b.LastChange).AddSeconds(MapVote.InactivitySeconds);

    /// <summary>When a vote ends while somebody has not voted: MapVote.Seconds after it opened.</summary>
    static DateTime VoteEnds(BallotState b) => b.Ends;

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

    static BallotState? Ballot;
    static long LastRound;
    static DateTime VoteWaitUntil; // nobody calls a vote before this (MapVote.WaitSeconds after the last one)

    static int VotePresent(BallotState b, DateTime now) => Math.Max(PresentIds(now).Count, b.PresentAtOpen);

    /// <summary>A card number this vote has not given yet: the lowest, never Random's (MapVote.RandomOption), within the click's two digits. A click names
    /// the number the player saw on the card, so a number is never given twice: a click sent for a card that has just gone finds no card, instead of
    /// the map that took its place. Only once a vote has given all 98 (a ballot holds MapVote.MaxMaps at once) does it take the lowest no card holds now.</summary>
    static int FreshNumber(BallotState b)
    {
        for (var n = 1; n < MapVote.RandomOption; n++) if (b.Given.Add(n)) return n;
        var held = b.Options.Select(o => o.N).ToHashSet();
        return Enumerable.Range(1, MapVote.RandomOption - 1).First(n => !held.Contains(n));
    }

    static string OptionKey(BallotOption o) => o.Source == VoteSource.Keep ? CurrentKey : o.Map is { } m ? Key(m) : "";

    /// <summary>Why nobody may call a vote now, or null: a hold, a change under way, a map loading, a restart counting down, the wait after the last vote. You: what the
    /// player who tried reads. All: what everybody reads after that player's name.</summary>
    static (string You, string All)? CallRefusal(DateTime now)
    {
        if (HudHeld()) return ("Steam is reviewing the HUD, so a vote cannot start now - The game plays on", "tried to start a vote - Steam is reviewing the HUD, so a vote cannot start now");
        if (Pending is { } p) return ($"Already changing to {M.A(Clip(Label(p.Map), 40))} - Wait for it", $"tried to start a vote - Already changing to {M.A(Clip(Label(p.Map), 40))}");
        if (LoadPending) return ("The next map is still loading - Wait for it", "tried to start a vote - The next map is still loading");
        if (RestartRefusal(now) is { } restart) return (restart, $"tried to start a vote - {restart}"); // Restarts.cs
        if (now < VoteWaitUntil && MapVote.Left((int)Math.Ceiling((VoteWaitUntil - now).TotalSeconds)) is var left)
            return ($"You can call a vote again in {left}", $"tried to start another vote - The next one can start in {M.V(left)}");
        return null;
    }

    static string? CannotCall(DateTime now) => CallRefusal(now)?.You;

    /// <summary>A player who keeps trying is named at most this often in chat; their own line and their row on the board stay current at every try.</summary>
    const double RefusedQuietSeconds = 10;
    static readonly Dictionary<string, DateTime> RefusedSaid = new();

    /// <summary>Nobody may call a vote now and this player tried: their own line says why, and chat and the board name them, for everybody.</summary>
    static void RefuseCall(string by, string steamId, (string You, string All) no, DateTime now)
    {
        Log($"vote: {by} {steamId} tried to call one - {no.You}");
        var say = !RefusedSaid.TryGetValue(steamId, out var said) || (now - said).TotalSeconds >= RefusedQuietSeconds;
        if (say) { if (RefusedSaid.Count > 64) RefusedSaid.Clear(); RefusedSaid[steamId] = now; }
        Post(Messages.VoteRefused, M.Refused($"{VoterMark(by)} {no.All}"), steamId, key: $"voteno{steamId}", chat: say);
        Post(Messages.VoteRefusedYou, no.You, steamId);
    }

    /// <summary>A player's name in a line of the vote's: their team's colour in chat.</summary>
    static string VoterMark(string name) => M.N(Clip(name, 24));

    /// <summary>A player's name as the vote's lists say it: the log stream's, the fakes of a test too.</summary>
    static string VoterName(string id) => Players.TryGetValue(id, out var pl) && pl.Name != "" ? Clip(pl.Name, 24) : id;

    /// <summary>The board keeps the newest of a vote's own lines (VoteLine): the list as it stands, with the line it started with and its result beside it.</summary>
    static string VoteLineKey(BallotState b) => $"vote{b.Round}";

    /// <summary>What every vote says in chat and on the board, one line of at most Messages.ChatLineMax characters: what this player did, who has voted and who still
    /// has to, each list as many names as fit and +N after them. Half the room for each, and what one leaves goes to the other.</summary>
    static string VoteLine(BallotState b, string what, DateTime now)
    {
        var voted = b.Choices.Keys.Select(VoterName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var waiting = PresentIds(now).Where(id => !b.Choices.ContainsKey(id)).Select(VoterName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var count = VotePresent(b, now);
        var head = voted.Count == 0 ? "Nobody has voted" : voted.Count >= count ? "Everybody has voted: " : $"{M.V($"{voted.Count} of {count}")} voted: ";
        const string wait = " - Waiting for: ";
        var room = Messages.ChatLineMax - M.Plain(what).Length - " - ".Length - M.Plain(head).Length - (waiting.Count > 0 ? wait.Length : 0);
        static int Width(List<string> names) => names.Sum(n => n.Length) + ", ".Length * Math.Max(0, names.Count - 1);
        var forWaiting = Math.Max(room / 2, room - Width(voted));
        var forVoted = room - Math.Min(forWaiting, Width(waiting));
        return $"{what} - {head}{(voted.Count > 0 ? HudVote.Names(voted, forVoted, ", ", M.N) : "")}{(waiting.Count > 0 ? wait + HudVote.Names(waiting, forWaiting, ", ", M.N) : "")}";
    }

    /// <summary>What a vote on this card says: Bob voted for #12 aim_x; on Keep, what keeping means now.</summary>
    static string VotedWords(BallotState b, string by, BallotOption o) => o.Source switch
    {
        VoteSource.Keep when b.MatchEnded => $"{VoterMark(by)} voted to play {M.A(Clip(CurrentLabel(), 32))} again",
        VoteSource.Keep => $"{VoterMark(by)} voted to keep {M.A(Clip(CurrentLabel(), 32))}",
        VoteSource.Random => $"{VoterMark(by)} voted for {M.A("Random")}",
        _ => $"{VoterMark(by)} voted for {M.A(Clip(Label(o.Map!), 32))}{ModeTag(o.Map!)}",
    };

    /// <summary>A vote cast, changed or taken back, or a map put on the ballot: the line for everybody.</summary>
    static void SayVote(BallotState b, string what, DateTime now) => Post(Messages.VoteClick, VoteLine(b, what, now), key: VoteLineKey(b));

    /// <summary>The longer words while they fit in a chat line, else the shorter.</summary>
    static string Fit(string longer, string shorter) => M.Plain(longer).Length <= Messages.ChatLineMax ? longer : shorter;

    /// <summary>One draw of the dice for a card: a map of the mode from the kept maps on the server's disk (KeptOnDisk), by points, never one already
    /// taken - and taken by it. Null: none is left.</summary>
    static MapInfo? DrawKept(Modes.Mode mode, HashSet<string> taken, int players = 0)
    {
        var pool = PoolOf(mode).Where(m => CanRoll(m, taken) && KeptOnDisk(m) && (players == 0 || Fits(m, players))).ToList();
        if (RollIn(pool) is not { } r || !taken.Add(Key(r.Pick))) return null;
        return r.Pick;
    }

    /// <summary>The fresh card's draw: a fresh map of the mode on the server's disk - kept, or a downloaded stock entry - by points. Null: none.</summary>
    static MapInfo? DrawFresh(Modes.Mode mode, HashSet<string> taken, int players = 0)
    {
        var pool = PoolOf(mode).Where(m => CanRoll(m, taken) && IsFresh(m) && KeptOnDisk(m) && (players == 0 || Fits(m, players))).ToList();
        var have = pool.Select(Key).ToHashSet();
        pool.AddRange(StockOnDisk(mode, taken).Where(m => IsFresh(m) && (players == 0 || Fits(m, players)) && have.Add(Key(m))));
        if (RollIn(pool) is not { } r || !taken.Add(Key(r.Pick))) return null;
        return r.Pick;
    }

    /// <summary>The surprise: the first of the mode's stock entries on the server's disk that may be shown. Null: none is downloaded yet.</summary>
    static MapInfo? DrawSurprise(Modes.Mode mode, HashSet<string> taken, int players = 0) =>
        StockOnDisk(mode, taken).FirstOrDefault(m => CanRoll(m, taken) && (players == 0 || Fits(m, players))) is { } m && taken.Add(Key(m)) ? m : null;

    /// <summary>A card shown: its map is no longer new to us, and a card the dice or a player put on it is remembered with the time, for the step back
    /// a card shown and not chosen takes in the points (Dice.cs: ShownPenalty). Showings older than ShownFadeDays are forgotten.</summary>
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

    /// <summary>Opens a vote: builds the ballot by its recipe, casts the caller's vote on the map they selected, remembers what it shows, says so in chat and on
    /// the board, and puts the ballot's maps first in queues.json. False when there is nothing to put on it. A match end's vote ignores the wait.</summary>
    static bool OpenBallot(DateTime now, int humans, string starter = "", string starterName = "", MapInfo? pick = null, bool matchEnd = false, bool serverStart = false)
    {
        var present = PresentIds(now).Count;
        var players = Math.Max(present, humans);
        var round = Math.Max(new DateTimeOffset(now).ToUnixTimeSeconds(), LastRound + 1);
        var b = new BallotState { Round = round, Opened = now, Ends = now.AddSeconds(MapVote.Seconds), PresentAtOpen = present > 0 ? 0 : humans,
            Starter = starter, StarterName = starterName, MatchEnded = matchEnd, ServerStart = serverStart };
        b.FillMode = ModeSuits(CurrentMode, players) ? CurrentMode : Modes.Default; // Wingman with more on than any of its maps takes: its random cards and Random come from Arms Race
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
        foreach (var mode in Modes.All.Where(m => m.Id != b.FillMode.Id && ModeSuits(m, players)))
        {
            if (pick != null && ModeOf(pick).Id == mode.Id) continue;
            if (DrawKept(mode, taken, players) is { } map) others.Add(Rolled(map, mode, OtherOrder + others.Count)); // a Wingman map with spawn points for them
        }
        // the server's own mode until the page is full: the fresh card, the surprise, the favourites
        var fill = new List<BallotOption>();
        var room = MapVote.PageCards - b.Options.Count - others.Count;
        if (room > 0 && !b.Options.Concat(others).Any(o => o.Map is { } x && IsFresh(x)) && DrawFresh(b.FillMode, taken, players) is { } fresh) { fill.Add(Rolled(fresh, b.FillMode, FreshOrder)); room--; }
        if (room > 0 && DrawSurprise(b.FillMode, taken, players) is { } surprise) { fill.Add(Rolled(surprise, b.FillMode, SurpriseOrder)); room--; }
        while (room > 0 && DrawKept(b.FillMode, taken, players) is { } favourite) { fill.Add(Rolled(favourite, b.FillMode, FavouriteOrder + fill.Count)); room--; }
        b.Options.AddRange(fill);
        b.Options.AddRange(others);
        if (b.Options.Count(o => o.Source != VoteSource.Keep) == 0) { Log("vote: nothing to put on the ballot"); return false; }
        for (var i = 0; i < b.Options.Count; i++) (b.Options[i].N, b.Options[i].Slot) = (FreshNumber(b), i); // the numbers and the places in that order: Keep 1, the pick 2, ...
        b.Options.Add(new BallotOption(VoteSource.Random, null, b.FillMode, int.MaxValue) { N = MapVote.RandomOption });
        if (pick != null && b.Options.FirstOrDefault(o => o.Source == VoteSource.Picked) is { } listed) b.Choices[starter] = listed.N;
        (b.LastChange, b.LastBy) = (now, starterName);
        foreach (var o in b.Options) Showing(o, now);
        CatalogVersion++; // the maps shown are no longer new to us: their cards' tags change
        Save();

        Ballot = b; LastRound = round;
        var count = VotePresent(b, now);
        Log($"vote: round {round} open for {MapVote.Seconds} s{(matchEnd ? " at a match end" : serverStart ? " at a server start" : starter != "" ? $", called by {starterName} {starter}" : "")}, {count} present: "
            + string.Join(" | ", b.Options.Select(o => $"{o.N}. {OptionLabel(b, o)} ({o.Source.ToString().ToLowerInvariant()}, {o.Mode.Id}{(o.Order is FreshOrder or SurpriseOrder ? o.Order == FreshOrder ? ", fresh" : ", surprise" : "")}{(o.Tags != "" ? $", {o.Tags}" : "")})")));
        var how = $"{M.V($"{MapVote.Seconds} s")}, or until {(count > 1 ? $"all {M.V($"{count}")} have voted" : "everybody has voted")}";
        Post(Messages.VoteStarted, matchEnd ? $"Match over - Vote for the next map: {how}"
            : serverStart ? $"Server started - Vote for the next map: {how}"
            : pick != null ? Fit($"{VoterMark(starterName)} put {M.A(Clip(Label(pick), 32))}{WhyNote(pick)}{ModeTag(pick)} to the vote: {how}", $"{VoterMark(starterName)} put {M.A(Clip(Label(pick), 32))}{ModeTag(pick)} to the vote: {how}")
            : $"{VoterMark(starterName)} started a vote for the next map: {how}", seconds: MapVote.Seconds);
        FillStock(); // Stock.cs: the shown stock entries are used up and rolled anew, and the ballot's maps go first in queues.json, so they download now
        BallotTick(now); // alone, the caller's own vote ends it at once
        return true;
    }

    static string KeepWords(BallotState b) => b.MatchEnded ? "Play this map again" : "Keep playing this map";

    static string OptionLabel(BallotState b, BallotOption o) => o.Source == VoteSource.Keep ? $"{KeepWords(b)} ({CurrentLabel()})" : o.Map is { } m ? Label(m) + ModeTag(m) : "Random";

    /// <summary>Start a vote (the board's button, !next; !random with this player's vote on Random): a vote without a map of the caller's own, Keep
    /// on it as on every ballot. While a vote is open, !random moves this player's vote to Random.</summary>
    static void StartVote(string by, string steamId, bool forRandom = false)
    {
        var now = Now();
        if (Ballot is { Open: true } open)
        {
            if (forRandom) { CastBallot(by, steamId, $"{open.Round} {MapVote.RandomOption}"); return; }
            Tell(steamId, M.Refused("The vote for the next map is on - Click a card in it"));
            return;
        }
        if (CallRefusal(now) is { } no) { RefuseCall(by, steamId, no, now); return; }
        Present(steamId, by);
        if (!OpenBallot(now, Math.Max(Humans(), 1), steamId, by)) { Tell(steamId, M.Refused("No map is eligible for a vote right now")); return; }
        Log($"vote: started by {by} {steamId}{(forRandom ? ", with their vote on Random" : "")}");
        if (forRandom && Ballot is { Open: true } b)
        {
            b.Choices[steamId] = MapVote.RandomOption;
            (b.LastChange, b.LastBy) = (now, by);
            SayVote(b, VotedWords(b, by, b.Options.First(o => o.Source == VoteSource.Random)), now);
            BallotTick(now);
        }
    }

    /// <summary>A map a player selected (Play, !play, Previous), past every refusal and a risky map's second click: a vote with it on the ballot and
    /// their vote on it - or, while a vote is open, onto that ballot with their vote. The map that is on is Keep: a vote for it while one is open,
    /// nothing otherwise.</summary>
    static void SelectMap(string by, string steamId, MapInfo map, DateTime now)
    {
        Present(steamId, by);
        if (Ballot is { Open: true } open) { AddToBallot(open, by, steamId, map, now); return; }
        if (IsOn(map)) { Tell(steamId, M.Refused($"{M.A(Clip(Label(map), 40))} is on now - Start a vote to change it")); return; }
        if (CallRefusal(now) is { } no) { RefuseCall(by, steamId, no, now); return; }
        if (!OpenBallot(now, Math.Max(Humans(), 1), steamId, by, map)) Tell(steamId, M.Refused("No vote could open right now - Try again in a moment"));
    }

    /// <summary>A player's vote: !mv &lt;round&gt; &lt;n&gt; from the vote screen (0 takes it back, 99 is Random) - as often as they like.</summary>
    static void CastBallot(string by, string steamId, string arg)
    {
        if (MapVote.ParseClick($"{MapVote.Verb} {arg}") is not { } click || Ballot is not { Open: true } b || b.Round != click.Round) { Tell(steamId, M.Refused("That vote is over")); return; }
        var now = Now();
        if (click.Option == 0)
        {
            if (!b.Choices.Remove(steamId)) return;
            Log($"vote: {by} {steamId} took their vote back");
            (b.LastChange, b.LastBy) = (now, by);
            SayVote(b, $"{VoterMark(by)} took their vote back", now);
            BallotTick(now);
            return;
        }
        if (b.Options.FirstOrDefault(o => o.N == click.Option) is not { } option) { Log($"vote: {by} {steamId} clicked {click.Option}, a card no longer on the ballot"); Tell(steamId, ChatReply.CardGone); return; }
        Present(steamId, by);
        var was = b.Choices.GetValueOrDefault(steamId);
        b.Choices[steamId] = click.Option;
        if (was != click.Option)
        {
            Log($"vote: {by} {steamId} votes {click.Option} ({OptionLabel(b, option)}){(was > 0 ? $", was {was}" : "")}");
            (b.LastChange, b.LastBy) = (now, by);
            SayVote(b, VotedWords(b, by, option), now);
        }
        BallotTick(now);
    }

    /// <summary>A map a player put on the ballot that has no votes left leaves it, until somebody puts it on again: the random card it replaced comes back
    /// in its place (LeaveBallot), and whoever put it on, if still on the server, is told - their own line and their chat.</summary>
    static void DropUnvotedPicks(BallotState b)
    {
        var gone = b.Options.Where(o => o.Source == VoteSource.Picked && !b.Choices.ContainsValue(o.N)).ToList();
        if (gone.Count == 0) return;
        var present = PresentIds(Now());
        foreach (var o in gone)
        {
            var back = LeaveBallot(b, o);
            Log($"vote: {Label(o.Map!)} has no votes left - it leaves the ballot{(back?.Map is { } bm ? $", {Label(bm)} back in its place as {back.N}" : "")}");
            var label = M.A(Clip(Label(o.Map!), 32));
            var words = back?.Map is { } m ? $"{label} left the vote, with no votes on it - {M.A(Clip(Label(m), 32))} is back in its place"
                : $"{label} left the vote, with no votes on it - Add it again from Browse to vote for it";
            foreach (var id in o.Pickers.Select(p => p.Id).Distinct().Where(present.Contains)) Tell(id, words);
        }
        ExportQueues();
    }

    /// <summary>A card leaves the ballot. A pick that took the place of a random card gives it that place back, with its own number - unless its map is on the
    /// ballot again by now or can no longer be played - so no card moves; otherwise the card goes. Returns the card that came back, or null.</summary>
    static BallotOption? LeaveBallot(BallotState b, BallotOption o)
    {
        var at = b.Options.IndexOf(o);
        if (at < 0) return null;
        if (o.Replaced is { Map: { } m } back && Unplayable(m) == null && b.Options.All(x => x == o || OptionKey(x) != Key(m)))
        {
            back.Slot = o.Slot;
            b.Options[at] = back;
            return back;
        }
        b.Options.RemoveAt(at);
        return null;
    }

    /// <summary>A map selected while the vote is open: onto the ballot for everybody, with this player's vote. A map the ballot holds already - the
    /// map that is on is Keep - gets the vote alone; somebody else's pick is joined. A new card takes the place of a random card of the server's
    /// mode nobody voted for - its very place on the screen, with a number of its own - so one page holds it all; MapVote.MaxMaps a ballot.</summary>
    static void AddToBallot(BallotState b, string by, string steamId, MapInfo map, DateTime now)
    {
        var key = Key(map);
        if (b.Options.FirstOrDefault(o => o.Source != VoteSource.Random && OptionKey(o) == key && (o.Source == VoteSource.Keep ? IsOn(map) : ModeOf(map).Id == o.Mode.Id)) is { } there)
        {
            var moved = b.Choices.GetValueOrDefault(steamId) != there.N;
            if (moved) (b.LastChange, b.LastBy) = (now, by);
            b.Choices[steamId] = there.N;
            if (there.Source == VoteSource.Picked && there.Pickers.All(p => p.Id != steamId)) there.Pickers.Add((steamId, by));
            Tell(steamId, M.Done(there.Source == VoteSource.Keep ? $"{KeepWords(b)} is on the vote - Your vote is on it" : $"{M.A(Clip(Label(map), 40))} is on the vote already - Your vote is on it"));
            Log($"vote: {by} {steamId} asked for {Label(map)}, on the ballot as {there.N} - votes {there.N}");
            if (moved) SayVote(b, VotedWords(b, by, there), now);
            BallotTick(now);
            return;
        }
        var cards = b.Options.Where(o => o.Source != VoteSource.Random).ToList();
        if (cards.Count >= MapVote.MaxMaps) { Tell(steamId, M.Refused($"The vote holds {M.V($"{MapVote.MaxMaps}")} maps - Vote for one of them")); return; }
        var o = new BallotOption(VoteSource.Picked, map, ModeOf(map), PickOrder + b.Picks++, TagsOf(map, picked: true));
        o.Pickers.Add((steamId, by));
        o.N = FreshNumber(b); // never the number of the card it replaces: a click sent for that card finds no card, instead of this one
        var gone = cards.Count >= MapVote.PageCards ? Replaceable(b) : null;
        if (gone != null)
        {
            (o.Slot, o.Replaced) = (gone.Slot, gone);
            b.Options[b.Options.IndexOf(gone)] = o; // its very place on the screen: no other card moves
            Log($"vote: {Label(gone.Map!)} makes room for {Label(map)}, as {o.N} in its place");
        }
        else
        {
            o.Slot = cards.Count == 0 ? 0 : cards.Max(x => x.Slot) + 1; // after the last card
            b.Options.Insert(b.Options.FindIndex(x => x.Source == VoteSource.Random) is var r and >= 0 ? r : b.Options.Count, o);
        }
        b.Choices[steamId] = o.N;
        (b.LastChange, b.LastBy) = (now, by);
        Showing(o, now);
        CatalogVersion++;
        Save();
        Log($"vote: {by} {steamId} added {o.N}. {OptionLabel(b, o)} and votes for it");
        SayVote(b, $"{VoterMark(by)} added {M.A(Clip(Label(map), 32))}{ModeTag(map)} to the vote{(gone?.Map is { } g ? $" in place of {M.A(Clip(Label(g), 32))}" : "")}", now);
        FillStock(); // a stock entry picked is used up; the new card goes into queues.json
        BallotTick(now);
    }

    /// <summary>The random card a new pick replaces: one of the server's mode nobody voted for - the favourites first, the last rolled first, then the
    /// surprise, the fresh card last. The other modes' cards keep their place. Null: there is none, and the ballot grows a page.</summary>
    static BallotOption? Replaceable(BallotState b) =>
        b.Options.Where(o => o.Source == VoteSource.Rolled && !b.Choices.ContainsValue(o.N) && o.Order >= FillOrder && o.Order < OtherOrder).MaxBy(o => o.Order);

    /// <summary>Every tick, and after every vote: a voter who left takes their vote along; everybody present having voted ends the vote, the timer
    /// ends it. Once decided it stays on screen until its change has gone out, or for MapVote.KeptSeconds when it kept the map. A vote always ends: whatever
    /// goes wrong in here drops it, so nobody stays frozen and the rest of the tick runs.</summary>
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
            if (b.KeptUntil is { } until) { if (now >= until) Ballot = null; return; } // everybody unfreezes, every window closes, at the same moment (the plugin)
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
        bool Takes(BallotOption o) => o.Source is VoteSource.Keep or VoteSource.Random || o.Map is not { } map || Fits(map, count); // a Wingman map takes as many a side as it has spawn points
        var ranked = b.Options.Where(Takes).ToList();
        var most = ranked.Max(o => tally[o.N]);
        foreach (var o in b.Options.Where(o => !Takes(o) && tally[o.N] > 0 && tally[o.N] >= most))
            Log($"vote: {OptionLabel(b, o)} has {tally[o.N]} vote(s), but it takes {(o.Mode.Id == Modes.WingmanId && o.Map is { } m ? Modes.WingmanTakes(SpawnsPerSide(m)) : $"{o.Mode.MaxPlayers} players")} and {count} are on - it cannot win");
        if (most == 0)
        {
            // Nobody voted: Random at a match end and at a server start (the dice the start roll would have rolled). Mid-match the map stays - also when the bridge does
            // not know which map that is, so the ballot has no Keep card.
            CloseBallot(b, now, b.MatchEnded || b.ServerStart ? b.Options.First(o => o.Source == VoteSource.Random)
                : b.Options.FirstOrDefault(o => o.Source == VoteSource.Keep) ?? new BallotOption(VoteSource.Keep, null, CurrentMode, KeepOrder), "none", tally, count);
            return;
        }
        var top = ranked.Where(o => tally[o.N] == most).ToList();
        if (top.Count == 1) CloseBallot(b, now, top[0], everybody ? "all" : "most", tally, count);
        else CloseBallot(b, now, top[Dice.Next(top.Count)], "tie", tally, count);
    }

    /// <summary>The map that is on, where it starts a section: "... stays": its label, or plain words when the bridge does not know which map it is.</summary>
    static string OnWords() => CurrentKey != "" ? Clip(CurrentLabel(), 40) : "The map";

    /// <summary>The vote is decided: Keep shows that for MapVote.KeptSeconds and the map plays on; anything else goes out through the usual countdown. Everybody stays
    /// frozen until then. Either way the next vote waits MapVote.WaitSeconds. The chosen card is no showing that steps back. "N of M" in what it says is the tile's
    /// N/M: its votes of everybody present.</summary>
    static void CloseBallot(BallotState b, DateTime now, BallotOption o, string how, Dictionary<int, int> tally, int present)
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
        var count = M.V($"{votes} of {present}");
        if (o.Source == VoteSource.Keep)
        {
            b.Result = new VoteState.Outcome(o.N, how, Clip(CurrentLabel(), 40));
            b.KeptUntil = now.AddSeconds(MapVote.KeptSeconds);
            Save();
            Log($"vote: round {b.Round} decided ({how}, {votes} of {cast} vote(s), {present} present): {KeepWords(b).ToLowerInvariant()} - {CurrentLabel()}");
            var kept = M.A(OnWords());
            Post(Messages.VoteKept, how == "none" ? $"Nobody voted - {kept} stays"
                : how == "tie" ? $"The vote kept {kept} ({count}) - A tie, drawn by the dice"
                : b.MatchEnded ? $"The vote: {kept} again ({count}) - Its next match starts on it"
                : $"The vote kept {kept} ({count})");
            if (!b.Options.Contains(o)) Ballot = null; // no card for it: the bridge does not know which map is on, so there is nothing to show - everybody unfreezes at once
            return;
        }
        var target = o.Source == VoteSource.Random ? null : o.Map is { } m && Unplayable(m) == null ? m : null;
        target ??= RandomNext(b.FillMode); // Random - or a card whose map has become unplayable since: the dice over the maps on the disk (Stock.cs)
        if (target == null) { Log("vote: nothing eligible to load - the map stays"); Ballot = null; return; }
        var change = new Change(target, "the vote");
        b.Result = new VoteState.Outcome(o.N, how, Clip(Label(target), 40));
        b.Change = change;
        Save();
        Log($"vote: round {b.Round} decided ({how}, {votes} of {cast} vote(s), {present} present): {o.N}. {OptionLabel(b, o)} -> {Label(target)} in {ModeOf(target).Name}");
        var delay = M.V($"{ChangeDelay} s");
        string Words(string shown) => how switch
        {
            "none" => $"Nobody voted - Rolling the dice: {M.A(shown)}, changing in {delay}",
            _ when o.Source == VoteSource.Random => $"The vote chose Random ({count}) - Rolling the dice: {M.A(shown)}, changing in {delay}",
            "tie" => $"A tie at {M.V($"{votes}")} vote{(votes == 1 ? "" : "s")} each, drawn by the dice: {M.A(shown)}, changing in {delay}",
            "all" => $"Everybody has voted: {M.A(shown)} ({count}) - Changing in {delay}",
            _ => $"The vote chose {M.A(shown)} ({count}) - Changing in {delay}",
        };
        StartChange(change, Fit(Words($"{Label(target)}{Detail(target, MapTags.WhyWords(TagsOf(target), picked: false))}{ModeTag(target)}"), Words($"{Clip(Label(target), 40)}{ModeTag(target)}")), Messages.VoteDecided);
    }

    /// <summary>Drops a vote without a change: a restart, a map start nobody asked for, the server emptied. Everybody unfreezes.</summary>
    static void EndBallot(string why)
    {
        if (Ballot is { Open: true } b) Log($"vote: round {b.Round} dropped - {why}");
        Ballot = null;
    }

    /// <summary>A list changed under an open vote (Reload.cs, Removed.cs): a card whose map is no longer playable leaves the ballot, its votes with
    /// it. Returns how many went.</summary>
    static int DropUnplayable(BallotState b)
    {
        var gone = b.Options.Where(o => o.Source is VoteSource.Picked or VoteSource.Rolled && o.Map is { } m && Unplayable(m) != null).ToList();
        foreach (var o in gone)
        {
            var back = LeaveBallot(b, o); // a pick gives the card it replaced its place back
            foreach (var id in b.Choices.Where(c => c.Value == o.N).Select(c => c.Key).ToList()) b.Choices.Remove(id);
            Log($"vote: {Label(o.Map!)} leaves the ballot - {M.Plain(Unplayable(o.Map!))}{(back?.Map is { } bm ? $"; {Label(bm)} back in its place as {back.N}" : "")}");
        }
        return gone.Count;
    }

    /// <summary>The vote, for the live state (the lines VoteState.Encode adds after HudState's): the cards in their places while it is open (no card moves
    /// under a player's mouse: each keeps the place it went on in), with the most votes first once it is decided; Random last.
    /// While it is open both timers are on the wire: SecondsLeft, the vote's limit, always; Inactivity and who reset it once everybody has voted,
    /// never more than SecondsLeft - the limit closes it first. Decided, SecondsLeft is the countdown: the change's, or the window's when it kept the map.</summary>
    static VoteState BuildVoteState(DateTime now)
    {
        var v = new VoteState();
        if (Ballot is not { Open: true } && now < VoteWaitUntil) (v.WaitFor, v.WaitUntilMs) = ((int)Math.Ceiling((VoteWaitUntil - now).TotalSeconds), HudState.Ms(VoteWaitUntil));
        if (Ballot is not { } b) return v;
        var count = VotePresent(b, now);
        v.Round = b.Round; v.Open = b.Open; v.MatchEnded = b.MatchEnded;
        v.StarterName = b.StarterName;
        var counting = b.Open ? VoteEnds(b) : b.KeptUntil ?? (Pending is { } p && p == b.Change ? p.FiresAt : (DateTime?)null);
        v.Freeze = b.Open || b.KeptUntil != null || (Pending != null && Pending == b.Change);
        v.SecondsLeft = counting is { } at ? Math.Max(0, (int)Math.Ceiling((at - now).TotalSeconds)) : 0;
        v.EndsAtMs = counting is { } at2 ? HudState.Ms(at2) : 0; // the plugin counts down from these (HudTransport.cs)
        v.DoneSeconds = b.Open ? 0 : b.KeptUntil != null ? MapVote.KeptSeconds : ChangeDelay;
        if (b.Open && MapVote.InactivitySeconds > 0 && b.EverybodySince != null)
        {
            (v.Inactivity, v.ResetBy) = (Math.Min(Math.Max(1, (int)Math.Ceiling((SettleEnd(b) - now).TotalSeconds)), Math.Max(1, v.SecondsLeft)), b.LastBy);
            v.InactivityAtMs = HudState.Ms(SettleEnd(b));
        }
        (v.Present, v.Cast) = (count, b.Choices.Count);
        // who voted what, by name (the cards' chips, the hover), and who has not voted yet (the waiting line)
        foreach (var g in b.Choices.GroupBy(c => c.Value)) v.Voters[g.Key] = g.Select(c => VoterName(c.Key)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (b.Open) v.Waiting = PresentIds(now).Where(id => !b.Choices.ContainsKey(id)).Select(VoterName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        int VotesOn(BallotOption o) => b.Choices.Values.Count(n => n == o.N);
        var shown = b.Options.OrderBy(o => o.Source == VoteSource.Random);
        foreach (var o in b.Open ? shown.ThenBy(o => o.Slot) : shown.ThenByDescending(VotesOn).ThenBy(o => o.Order))
        {
            var keep = o.Source == VoteSource.Keep;
            v.Options.Add(new VoteState.Option(o.N, o.Mode.Id, o.Source, OptionKey(o),
                keep ? KeepWords(b) : o.Map is { } l ? Clip(Label(l), 40) : "Random",
                keep ? Clip(CurrentLabel(), 40) : o.Map is { Official: false, Rank: > 0 } s ? Size(s) : "",
                VotesOn(o), Names(o.Pickers.Select(x => Clip(x.Name, 16)).Distinct()), o.Tags));
        }
        foreach (var (id, n) in b.Choices) if (AccountId(id) is >= 0 and var account) v.Choices[account] = n;
        v.Result = b.Result;
        return v;
    }

    /// <summary>The health page's line about the vote.</summary>
    static string VoteHealth() => "vote: " + (Ballot is { } b ? $"round {b.Round} {(b.Open ? "open" : "decided")}, {b.Choices.Count} vote(s), {b.Options.Count - 1} card(s)" : "none open")
        + (Now() < VoteWaitUntil ? $"; the next can be called in {(int)Math.Ceiling((VoteWaitUntil - Now()).TotalSeconds)} s" : "");
}
