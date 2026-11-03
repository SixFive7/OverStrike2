// The end-of-match vote's engine (#26, the owner's decision of 2026-11-01; the numbers and the wire codec are MapVote.cs, the full rules
// docs/reference.md, "The end-of-match vote and the game modes").
//
// When a match ends - where the bridge used to serve the queue or roll the dice - every player gets a vote screen for MapVote.Seconds (60),
// or until every human present has voted (the owner's 40 c, 2026-11-03; a strict majority closed it before). Alone, one vote ends it at once.
// The ballot (the owner's rules of 2026-11-03):
//   1. the queue: every request in fair-queue order, each requester's vote already cast on their first one - a queued map is a vote its
//      requester cast before the screen is presented; a map that loses stays queued;
//   2. a map of each other mode that suits the players on (Modes.Mode.Suits: Valve's maxplayers, so Wingman up to 4), unless the requests
//      already hold one of that mode;
//   3. maps of the mode the server is in, rolled like every random pick (weighted by votes, the usual exclusions, from the random queues
//      and so on the server's disk), until the ballot holds MapVote.MinMaps (8) maps; MapVote.MaxMaps (48) at most;
//   4. Random: the dice after the vote, in the mode the server is in.
// While it is open a player may add any map from the map browser (or with !play): it goes on the ballot for everybody, with a number of its
// own after the others, and that player's vote moves to it; MapVote.MaxAdded a player. Numbers never change once given.
//
// Next (the button, !next and its aliases, !skip among them; the owner's 2026-11-03) opens the same vote at once, while the match runs, with one
// card more, first: Keep playing - the map that is on. Keep wins, or nobody votes: the map stays, and its starter waits MidVoteCooldownMinutes
// before starting another. A map somebody requested is replaced only when the vote decides so (the old skip vote is this now). Only the
// starter's window opens on it; everybody else hears of it on the news line. !random is Next with its player's vote on Random.
// A match that ends while such a vote is open makes it a match end's: its requests' turn is over, every window opens on it, and nobody
// voting is Random; Keep stays a card (numbers never change) and, if it wins, the map plays its next match.
// At the timer, or once everybody has voted, the most votes win; a tie is drawn at random among the tied; no votes at all is Random (Keep,
// mid-match). The winner loads in
// its own mode (ModeSwitch.cs) after the usual countdown. A queued request that wins is held by its requesters, as a request is; a rolled or
// Random winner is held by the vote (VoteHeld) until its match ends - requests queue for the next vote, and Next on it starts the vote.
//
// Only when the plugin draws the vote: it polls /hud with MapVote.Capability. Without that (or with MATCH_END_VOTE=off) a match end
// works as it always did. MATCH_END_VOTE=on opens the vote even without such a plugin (tests).
static partial class Program
{
    static readonly string MatchEndVote = Env("MATCH_END_VOTE", "auto").Trim().ToLowerInvariant(); // auto (the default) | on | off
    static long votePolledTicks;  // the last /hud poll that carried MapVote.Capability (UTC ticks, written by the HTTP thread)

    /// <summary>A plugin that draws the vote is polling. --selftest hands its own.</summary>
    static Func<bool> VoteCapable = () => DateTime.UtcNow.Ticks - Interlocked.Read(ref votePolledTicks) < TimeSpan.FromSeconds(HudActiveSeconds).Ticks;

    static void VotePolled() => Interlocked.Exchange(ref votePolledTicks, DateTime.UtcNow.Ticks);

    /// <summary>One line of the ballot: its number on screen, how it got there, the map (null for Random), the request behind it (queue
    /// options), and the mode it loads in.</summary>
    sealed class BallotOption(VoteSource source, MapInfo? map, Request? request, Modes.Mode mode)
    {
        public int N;
        public string AddedBy = "";  // the player who added it while the vote was open (VoteSource.Added)
        public VoteSource Source { get; } = source;
        public MapInfo? Map { get; } = map;
        public Request? Request { get; } = request;
        public Modes.Mode Mode { get; } = mode;
    }

    /// <summary>The vote of one match end, open and then, for the few seconds until its change goes out, decided.</summary>
    sealed class MatchVote
    {
        public long Round;
        public DateTime Opened, Ends, DoneAt;
        public readonly List<BallotOption> Options = new();
        public readonly Dictionary<string, int> Choices = new(); // SteamID -> the option it votes for
        public readonly Dictionary<string, int> Added = new();   // SteamID -> how many maps it added while the vote was open
        public int PresentAtOpen;                                 // RCON status's humans, when the log stream knew nobody at the open
        public Modes.Mode FillMode = Modes.Default;               // the mode the rolled options of the server's mode, and Random, come from
        public bool Open = true;
        public VoteState.Outcome? Result;
        public Change? Change;                                    // what the result started
        public string Starter = "";                               // the SteamID whose Next started it mid-match ("" at a match end): Keep is on it
        public bool MatchEnded;                                   // the match ended while it was open: a match end's vote from then on
        public bool MidMatch => Starter != "" && !MatchEnded;
    }

    /// <summary>After a vote a player started ends in Keep, that player starts no other for this long (the owner's 2026-11-03: no spam).</summary>
    public const int MidVoteCooldownMinutes = 2;
    static readonly Dictionary<string, DateTime> MidVoteWait = new(); // SteamID -> when it may start a vote again

    static MatchVote? Ballot;
    static long LastRound;
    static bool VoteHeld; // the map that is on is the vote's (a rolled or Random winner): nobody's request replaces it before its match ends

    /// <summary>The players present for the vote: in game by the log stream, or reloading inside the load grace. One who left is not.</summary>
    static HashSet<string> PresentIds(DateTime now) => Players.Where(kv => kv.Value.GoneSince == null
        && (kv.Value.ReloadSince is not { } r || (now - r).TotalSeconds <= ReloadGraceSeconds)).Select(kv => kv.Key).ToHashSet();

    static int VotePresent(MatchVote b, DateTime now) => Math.Max(PresentIds(now).Count, b.PresentAtOpen);

    /// <summary>The humans the vote is for, or null when this match end has no vote: switched off, no plugin that draws it, inside the
    /// change cooldown, nobody on.</summary>
    static int? BallotWanted(DateTime now)
    {
        if (MatchEndVote == "off" || !RandomOnMatchEnd) return null;
        if (MatchEndVote != "on" && !VoteCapable()) { Log("match end: no vote - no plugin that draws it is polling, so the old match end"); return null; }
        if (now < CooldownUntil) return null;
        var humans = Humans();
        return humans > 0 ? humans : null;
    }

    /// <summary>Opens the vote: builds the ballot, casts the requesters' votes, says so on the news line. False when there is nothing to
    /// put on it but Random - the old match end then rolls the dice. A starter (a player's Next while the match runs) puts Keep playing on it,
    /// first, counted among its MinMaps.</summary>
    static bool OpenBallot(DateTime now, int humans, string starter = "", string starterName = "")
    {
        Prune(now);
        var present = PresentIds(now).Count;
        var players = Math.Max(present, humans);
        var round = Math.Max(new DateTimeOffset(now).ToUnixTimeSeconds(), LastRound + 1);
        var b = new MatchVote { Round = round, Opened = now, Ends = now.AddSeconds(MapVote.Seconds), PresentAtOpen = present > 0 ? 0 : humans };
        b.FillMode = CurrentMode.Suits(players) ? CurrentMode : Modes.Default; // Wingman with 5 on: its rolled maps and Random come from Arms Race
        var taken = new HashSet<string>();
        var options = new List<BallotOption>();
        var keep = starter != "" ? 1 : 0; // Keep playing: one card of the eight
        if (keep == 1 && S.Map is { } on) taken.Add(Key(on));

        // 1. the queue: every request in play order (the owner's 2026-11-03: all of them), and each connected requester's vote on their first
        var order = Order(now);
        var firstOf = new Dictionary<string, Request>();
        foreach (var r in order)
            foreach (var a in r.By.Where(a => IsConnected(a.Id, now)))
                firstOf.TryAdd(a.Id, r);
        foreach (var r in order)
        {
            if (options.Count >= MapVote.MaxMaps) break;
            if (taken.Add(Key(r.Map))) options.Add(new BallotOption(VoteSource.Queue, r.Map, r, ModeOf(r.Map)));
        }

        // 2. a map of each other mode that suits the players on, unless the requests hold one of that mode - from that mode's random queue
        //    (RandomQueue.cs): shown or chosen, what a ballot takes leaves the queue, so a map shown and not chosen comes back no sooner than
        //    the dice bring it
        foreach (var mode in Modes.All.Where(m => m.Id != b.FillMode.Id && m.Suits(players)))
        {
            if (options.Count >= MapVote.MaxMaps) break;
            if (options.Any(o => o.Mode.Id == mode.Id)) continue;
            if (RandomNext(true, 1, mode, taken) is { } map && taken.Add(Key(map))) options.Add(new BallotOption(VoteSource.Rolled, map, null, mode));
        }

        // 3. the server's own mode, from its queue, until the ballot holds MinMaps maps
        while (options.Count + keep < MapVote.MinMaps && RandomNext(true, 1, b.FillMode, taken) is { } map && taken.Add(Key(map)))
            options.Add(new BallotOption(VoteSource.Rolled, map, null, b.FillMode));
        RollNote = ""; // the ballot's rolls say nothing on the news line

        if (options.Count == 0) { Log("vote: nothing to put on the ballot but Random - the dice instead"); return false; }

        // on screen: Keep playing (a vote a player started), the queue, the server's mode, the other modes, Random
        b.Starter = starter;
        if (keep == 1) b.Options.Add(new BallotOption(VoteSource.Keep, S.Map, null, CurrentMode));
        b.Options.AddRange(options.Where(o => o.Source == VoteSource.Queue));
        b.Options.AddRange(options.Where(o => o.Source == VoteSource.Rolled && o.Mode.Id == b.FillMode.Id));
        b.Options.AddRange(options.Where(o => o.Source == VoteSource.Rolled && o.Mode.Id != b.FillMode.Id));
        b.Options.Add(new BallotOption(VoteSource.Random, null, null, b.FillMode));
        for (var i = 0; i < b.Options.Count; i++) b.Options[i].N = i + 1;
        foreach (var (id, r) in firstOf)
            if (b.Options.FirstOrDefault(o => o.Request == r) is { } listed) b.Choices[id] = listed.N;

        Ballot = b; LastRound = round;
        var count = VotePresent(b, now);
        Log($"vote: round {round} open for {MapVote.Seconds} s, {count} present (all {count} voting decide it early), {b.Choices.Count} vote(s) cast by the queue: "
            + string.Join(" | ", b.Options.Select(o => $"{o.N}. {OptionLabel(o)} ({o.Source.ToString().ToLowerInvariant()}, {o.Mode.Id}, {b.Choices.Values.Count(n => n == o.N)})")));
        Announce(keep == 1 ? $"{Clip(starterName, 16)} started a vote for the next map: {MapVote.Seconds} s, or until all {count} have voted - Keep playing is on it. TAB + E, or type !vote"
            : $"Match over - vote for the next map: {MapVote.Seconds} s, or until all {count} have voted. Click a card or type its number; !play adds a map", MapVote.Seconds);
        BallotTick(now); // the queue's votes alone may be everybody's already (one player, with a request queued)
        return true;
    }

    static string OptionLabel(BallotOption o) => o.Source == VoteSource.Keep ? $"Keep playing {CurrentLabel()}" : o.Map is { } m ? Label(m) + ModeTag(m) : "Random";

    /// <summary>Next, while a match runs (the owner's 2026-11-03): the vote for the next map at once - the same ballot as a match end's, with Keep
    /// playing first. One vote at a time; nothing while a change counts down or a map loads, nor inside the change cooldown, nor for a player whose
    /// last vote kept the map within MidVoteCooldownMinutes. ForRandom (!random): with this player's vote on Random. Without a plugin that draws
    /// the vote, the dice as before (OldNext).</summary>
    static void StartVote(string by, string steamId, bool forRandom = false)
    {
        var now = Now();
        if (Ballot is { Open: true } open)
        {
            if (forRandom && open.Options.FirstOrDefault(o => o.Source == VoteSource.Random) is { } r) { CastBallot(by, steamId, $"{open.Round} {r.N}"); return; }
            Tell(steamId, "The vote for the next map is on - TAB + E opens it, or type !vote and a number");
            return;
        }
        if (Pending is { } p) { Tell(steamId, $"Already changing to {Label(p.Map)}, wait for it"); return; }
        if (AnnouncePending) { Tell(steamId, "The next map is still loading - wait for it"); return; }
        if (now < CooldownUntil) { Tell(steamId, $"Wait {(int)(CooldownUntil - now).TotalSeconds + 1} s"); return; }
        if (MidVoteWait.TryGetValue(steamId, out var until) && now < until)
        {
            var left = until - now;
            Tell(steamId, $"Your last vote kept the map - you can start another in {(left.TotalSeconds >= 60 ? $"{(int)left.TotalMinutes} min {left.Seconds} s" : $"{(int)Math.Ceiling(left.TotalSeconds)} s")}");
            return;
        }
        if (MatchEndVote == "off" || (MatchEndVote != "on" && !VoteCapable())) { OldNext(by, steamId); return; }
        Present(steamId, by);
        if (!OpenBallot(now, Math.Max(Humans(), 1), steamId, by)) { Tell(steamId, "No map is eligible for a vote right now"); return; }
        Log($"vote: started by {by} {steamId}{(forRandom ? ", with their vote on Random" : "")}");
        if (forRandom && Ballot?.Options.FirstOrDefault(o => o.Source == VoteSource.Random) is { } random) { Ballot.Choices[steamId] = random.N; BallotTick(now); }
    }

    /// <summary>A player's vote: !mv &lt;round&gt; &lt;n&gt; from the vote screen (0 takes it back).</summary>
    static void CastBallot(string by, string steamId, string arg)
    {
        if (MapVote.ParseClick($"{MapVote.Verb} {arg}") is not { } click || Ballot is not { Open: true } b || b.Round != click.Round) { Tell(steamId, "That vote is over"); return; }
        if (click.Option == 0) { if (b.Choices.Remove(steamId)) Log($"vote: {by} {steamId} took their vote back"); return; }
        if (b.Options.FirstOrDefault(o => o.N == click.Option) is not { } option) { Tell(steamId, $"No option {click.Option} on this ballot"); return; }
        Present(steamId, by);
        var was = b.Choices.GetValueOrDefault(steamId);
        b.Choices[steamId] = click.Option;
        if (was != click.Option) Log($"vote: {by} {steamId} votes {click.Option} ({OptionLabel(option)}){(was > 0 ? $", was {was}" : "")}");
        BallotTick(Now());
    }

    /// <summary>A map asked for while the vote is open (a card's Play in the map browser, or !play in chat; the owner's 2026-11-03): onto the
    /// ballot for everybody, with a number after every other, and this player's vote moves to it. A map the ballot holds already gets the vote
    /// alone. MapVote.MaxAdded a player and MapVote.MaxMaps a ballot; the map passed PickMap's refusals already (a non-map, a removed or blocked
    /// map, a mode the players do not fit, a risky map's confirmation). One not on the server's disk downloads when it wins, with the download line.</summary>
    static void AddToBallot(MatchVote b, string by, string steamId, MapInfo map, DateTime now)
    {
        Present(steamId, by);
        var key = Key(map);
        if (b.Options.FirstOrDefault(o => o.Map is { } m && Key(m) == key) is { } there)
        {
            b.Choices[steamId] = there.N;
            Tell(steamId, $"{Clip(Label(map), 40)} is on the vote as {there.N} already - your vote is on it");
            Log($"vote: {by} {steamId} asked for {Label(map)}, on the ballot as {there.N} - votes {there.N}");
            BallotTick(now);
            return;
        }
        if (b.Added.GetValueOrDefault(steamId) >= MapVote.MaxAdded) { Tell(steamId, "You added a map to this vote already - vote for a card, or type its number"); return; }
        if (b.Options.Count(o => o.Map != null) >= MapVote.MaxMaps) { Tell(steamId, $"The vote holds {MapVote.MaxMaps} maps - vote for one of them"); return; }
        var o = new BallotOption(VoteSource.Added, map, null, ModeOf(map)) { N = b.Options.Max(x => x.N) + 1, AddedBy = by };
        var random = b.Options.FindIndex(x => x.Source == VoteSource.Random);
        b.Options.Insert(random < 0 ? b.Options.Count : random, o); // on screen with the maps, before Random; its number after all of them
        b.Added[steamId] = b.Added.GetValueOrDefault(steamId) + 1;
        b.Choices[steamId] = o.N;
        Log($"vote: {by} {steamId} added {o.N}. {OptionLabel(o)} and votes for it");
        Announce($"{Clip(by, 16)} added {Clip(Label(map), 32)}{ModeTag(map)} to the vote - !vote {o.N}", Math.Max(5, (int)Math.Ceiling((b.Ends - now).TotalSeconds)));
        BallotTick(now);
    }

    /// <summary>Every tick, and after every vote: a voter who left takes their vote along; everybody present having voted ends the vote early
    /// (the owner's 40 c), the timer ends it. Once decided it stays on screen until its change has gone out.</summary>
    static void BallotTick(DateTime now)
    {
        if (Ballot is not { } b) return;
        if (!b.Open)
        {
            if (Pending == null || Pending != b.Change || (now - b.DoneAt).TotalSeconds > ChangeDelay + 5) Ballot = null;
            return;
        }
        var present = PresentIds(now);
        foreach (var id in b.Choices.Keys.Where(id => !present.Contains(id)).ToList()) b.Choices.Remove(id);
        var count = VotePresent(b, now);
        if (count == 0) { Log("vote: everybody left - no change"); Ballot = null; return; }
        var tally = b.Options.ToDictionary(o => o.N, o => b.Choices.Values.Count(n => n == o.N));
        var everybody = b.Choices.Count >= count; // every human present has voted (a vote of a player not counted as present counts too)
        if (!everybody && now < b.Ends) return;
        var most = tally.Values.Max();
        var top = tally.Where(t => t.Value == most).Select(t => t.Key).ToList();
        if (most == 0) CloseBallot(b, now, b.Options.First(o => o.Source == (b.MidMatch ? VoteSource.Keep : VoteSource.Random)).N, "none", tally, count); // no votes: Keep mid-match, Random at a match end
        else if (top.Count == 1) CloseBallot(b, now, top[0], everybody ? "all" : "most", tally, count);
        else CloseBallot(b, now, top[Dice.Next(top.Count)], "tie", tally, count);
    }

    /// <summary>The vote is decided: what it chose goes out through the usual countdown, held as docs/reference.md says.</summary>
    static void CloseBallot(MatchVote b, DateTime now, int n, string how, Dictionary<int, int> tally, int present)
    {
        b.Open = false; b.DoneAt = now;
        if (InMaintenance()) { Log("vote: decided while the maintenance flag is set - no change"); Ballot = null; return; }
        var o = b.Options.First(x => x.N == n);
        if (o.Source == VoteSource.Keep)
        {
            // the map stays: nothing changes, its holders keep it; the starter waits before starting another
            Ballot = null;
            if (b.Starter != "") MidVoteWait[b.Starter] = now.AddMinutes(MidVoteCooldownMinutes);
            Log($"vote: round {b.Round} decided ({how}, {tally.GetValueOrDefault(n)} of {tally.Values.Sum()} vote(s), {present} present): keep {CurrentLabel()}");
            Announce(how == "none" ? $"Nobody voted - {Clip(CurrentLabel(), 40)} stays" : $"The vote kept {Clip(CurrentLabel(), 40)} ({tally.GetValueOrDefault(n)} of {tally.Values.Sum()})");
            return;
        }
        Change? change = null;
        if (o.Source == VoteSource.Queue && o.Request is { } r && Queue.Contains(r))
        {
            Queue.Remove(r); // played as a request: its requesters hold it, their airtime runs from its map start
            var owners = r.By.Where(a => IsConnected(a.Id, now)).Select(a => new Owner(a.Id, a.Name)).ToList();
            change = new Change(r.Map, owners.Count > 0 ? Names(owners.Select(x => x.Name)) : "the vote", owners) { Voted = true };
        }
        else if (o.Map is { } map && !IsPack(map) && !IsRemoved(map) && !IsBlocked(map))
            change = new Change(map, "the vote", []) { Voted = true };
        else if (RandomNext(true, 1, b.FillMode) is { } rolled) // Random - or a map on the ballot that has become unplayable since: the queue's next entry (RandomQueue.cs)
            change = new Change(rolled, "the vote", []) { Voted = true };
        if (change == null) { Log("vote: nothing eligible to load - the map stays"); Ballot = null; return; }
        var m = change.Map;
        b.Result = new VoteState.Outcome(n, how, Clip(Label(m), 40), ModeOf(m).Id);
        b.Change = change;
        var votes = tally.GetValueOrDefault(n);
        var cast = tally.Values.Sum();
        Log($"vote: round {b.Round} decided ({how}, {votes} of {cast} vote(s), {present} present): {n}. {OptionLabel(o)} -> {Label(m)} in {ModeOf(m).Name}");
        var target = $"{Label(m)}{Detail(m)}{ModeTag(m)}";
        StartChange(change, how switch
        {
            "none" => $"Nobody voted - rolling the dice: {target}, changing in {ChangeDelay} s",
            _ when o.Source == VoteSource.Random => $"The vote chose Random ({votes} of {cast}) - rolling the dice: {target}, changing in {ChangeDelay} s",
            "tie" => $"A tie at {votes} votes, drawn by the dice: {target}, changing in {ChangeDelay} s",
            "all" => $"Everybody has voted: {target} ({votes} of {cast}) - changing in {ChangeDelay} s",
            _ => $"The vote chose {target} ({votes} of {cast} votes) - changing in {ChangeDelay} s",
        });
    }

    /// <summary>Drops an open vote without a change: a restart, a map start nobody asked for, the server emptied.</summary>
    static void EndBallot(string why)
    {
        if (Ballot is not { Open: true } b) { Ballot = null; return; }
        Log($"vote: round {b.Round} dropped - {why}");
        Ballot = null;
    }

    /// <summary>The vote and the modes, for the live state (the lines VoteState.Encode adds after HudState's).</summary>
    static VoteState BuildVoteState(DateTime now)
    {
        var mode = CurrentMode;
        var v = new VoteState { Mode = mode.Id, ModeName = mode.Name };
        var humans = Math.Max(PresentIds(now).Count, 1);
        foreach (var m in Modes.All) v.Modes.Add(new VoteState.ModeInfo(m.Id, m.Name, m.MaxPlayers, m.Suits(humans)));
        if (Ballot is not { } b) return v;
        var count = VotePresent(b, now);
        v.Round = b.Round; v.Open = b.Open; v.MidMatch = b.MidMatch; v.Starter = b.MidMatch ? Math.Max(0, AccountId(b.Starter)) : 0;
        v.SecondsLeft = b.Open ? Math.Max(0, (int)Math.Ceiling((b.Ends - now).TotalSeconds))
            : Pending is { } p && p == b.Change ? Math.Max(0, (int)Math.Ceiling((p.FiresAt - now).TotalSeconds)) : 0;
        (v.Present, v.Needed, v.Cast) = (count, MapVote.Needed(Math.Max(count, 1)), b.Choices.Count);
        // who voted what, by name (the cards' chips), and who has not voted yet (the waiting line): the log stream's players, the fakes of a test too
        string NameOf(string id) => Players.TryGetValue(id, out var pl) && pl.Name != "" ? Clip(pl.Name, 24) : id;
        foreach (var g in b.Choices.GroupBy(c => c.Value)) v.Voters[g.Key] = g.Select(c => NameOf(c.Key)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (b.Open) v.Waiting = PresentIds(now).Where(id => !b.Choices.ContainsKey(id)).Select(NameOf).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var o in b.Options)
        {
            var who = o.Request?.By.Where(a => IsConnected(a.Id, now)).Select(a => Clip(a.Name, 16)).ToList() is { Count: > 0 } names ? Names(names) : Clip(o.AddedBy, 16);
            v.Options.Add(new VoteState.Option(o.N, o.Mode.Id, o.Source, o.Map is { } m ? ArgOf(m) : "", o.Map is { } k ? Key(k) : "",
                o.Source == VoteSource.Keep ? Clip($"Keep playing {CurrentLabel()}", 40) : o.Map is { } l ? Clip(Label(l), 40) : "Random", o.Map is { Official: false, Rank: > 0 } s ? Size(s) : "",
                b.Choices.Values.Count(n => n == o.N), who));
        }
        foreach (var (id, n) in b.Choices) if (AccountId(id) is >= 0 and var account) v.Choices[account] = n;
        v.Result = b.Result;
        return v;
    }

    /// <summary>The health page's line about the vote.</summary>
    static string VoteHealth() => $"vote: MATCH_END_VOTE {MatchEndVote}, a plugin that draws it {(VoteCapable() ? "is" : "is not")} polling; "
        + (Ballot is { } b ? $"round {b.Round} {(b.Open ? "open" : "decided")}, {b.Choices.Count} vote(s)" : "none open") + (VoteHeld ? "; the map on is the vote's" : "");
}
