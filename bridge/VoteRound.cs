// The end-of-match vote's engine (#26, the owner's decision of 2026-11-01; the numbers and the wire codec are MapVote.cs, the full rules
// docs/reference.md, "The end-of-match vote and the game modes").
//
// When a match ends - where the bridge used to serve the queue or roll the dice - every player gets a vote screen for MapVote.Seconds (60),
// or until a strict majority of the players present has voted for one option. The ballot, up to MapVote.MapOptions (5) maps and Random:
//   1. the queue: each requester's first request in fair-queue order, with that requester's vote already cast: a queued
//      map is a vote its requester cast before the screen is presented; a map that loses stays queued;
//   2. a map of each other mode that suits the players on (Modes.Mode.Suits: Valve's maxplayers), while there is room;
//   3. maps of the mode the server is in, rolled like every random pick (weighted by votes, the usual exclusions), up to 5 maps;
//   4. Random: the dice after the vote, in the mode the server is in.
// At the timer the most votes win; a tie is drawn at random among the tied; no votes at all is Random. The winner loads in its own mode
// (ModeSwitch.cs) after the usual countdown. A queued request that wins is held by its requesters, as a request is; a rolled or Random
// winner is held by the vote (VoteHeld) until its match ends - requests queue for the next vote, and Next on it is a vote to skip.
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
        public int PresentAtOpen;                                 // RCON status's humans, when the log stream knew nobody at the open
        public Modes.Mode FillMode = Modes.Default;               // the mode the rolled options of the server's mode, and Random, come from
        public bool Open = true;
        public VoteState.Outcome? Result;
        public Change? Change;                                    // what the result started
    }

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
    /// put on it but Random - the old match end then rolls the dice.</summary>
    static bool OpenBallot(DateTime now, int humans)
    {
        Prune(now);
        var present = PresentIds(now).Count;
        var players = Math.Max(present, humans);
        var round = Math.Max(new DateTimeOffset(now).ToUnixTimeSeconds(), LastRound + 1);
        var b = new MatchVote { Round = round, Opened = now, Ends = now.AddSeconds(MapVote.Seconds), PresentAtOpen = present > 0 ? 0 : humans };
        b.FillMode = CurrentMode.Suits(players) ? CurrentMode : Modes.Default; // Wingman with 5 on: its rolled maps and Random come from Arms Race
        var taken = new HashSet<string>();
        var options = new List<BallotOption>();

        // 1. the queue: each connected requester's first request in play order - and their vote on it
        var order = Order(now);
        var firstOf = new Dictionary<string, Request>();
        foreach (var r in order)
            foreach (var a in r.By.Where(a => IsConnected(a.Id, now)))
                firstOf.TryAdd(a.Id, r);
        foreach (var r in order.Where(firstOf.ContainsValue))
        {
            if (options.Count >= MapVote.MapOptions) break;
            if (taken.Add(Key(r.Map))) options.Add(new BallotOption(VoteSource.Queue, r.Map, r, ModeOf(r.Map)));
        }

        // 2. a map of each other mode that suits the players on, while there is room
        foreach (var mode in Modes.All.Where(m => m.Id != b.FillMode.Id && m.Suits(players)))
        {
            if (options.Count >= MapVote.MapOptions) break;
            if (options.Any(o => o.Mode.Id == mode.Id)) continue;
            if (RandomPick(true, 1, mode, taken) is { } map && taken.Add(Key(map))) options.Add(new BallotOption(VoteSource.Rolled, map, null, mode));
        }

        // 3. the server's own mode, rolled, up to MapOptions maps
        while (options.Count < MapVote.MapOptions && RandomPick(true, 1, b.FillMode, taken) is { } map && taken.Add(Key(map)))
            options.Add(new BallotOption(VoteSource.Rolled, map, null, b.FillMode));
        RollNote = ""; // the ballot's rolls say nothing on the news line

        if (options.Count == 0) { Log("vote: nothing to put on the ballot but Random - the dice instead"); return false; }

        // on screen: the queue, the server's mode, the other modes, Random
        b.Options.AddRange(options.Where(o => o.Source == VoteSource.Queue));
        b.Options.AddRange(options.Where(o => o.Source == VoteSource.Rolled && o.Mode.Id == b.FillMode.Id));
        b.Options.AddRange(options.Where(o => o.Source == VoteSource.Rolled && o.Mode.Id != b.FillMode.Id));
        b.Options.Add(new BallotOption(VoteSource.Random, null, null, b.FillMode));
        for (var i = 0; i < b.Options.Count; i++) b.Options[i].N = i + 1;
        foreach (var (id, r) in firstOf)
            if (b.Options.FirstOrDefault(o => o.Request == r) is { } listed) b.Choices[id] = listed.N;

        Ballot = b; LastRound = round;
        var count = VotePresent(b, now);
        Log($"vote: round {round} open for {MapVote.Seconds} s, {count} present ({MapVote.Needed(count)} to decide early), {b.Choices.Count} vote(s) cast by the queue: "
            + string.Join(" | ", b.Options.Select(o => $"{o.N}. {OptionLabel(o)} ({o.Source.ToString().ToLowerInvariant()}, {o.Mode.Id}, {b.Choices.Values.Count(n => n == o.N)})")));
        Announce($"Match over - vote for the next map: {MapVote.Seconds} s, or until {MapVote.Needed(count)} of {count} agree. Click a card, or type its number in chat", MapVote.Seconds);
        BallotTick(now); // the queue's votes alone may be a majority already
        return true;
    }

    static string OptionLabel(BallotOption o) => o.Map is { } m ? Label(m) + ModeTag(m) : "Random";

    /// <summary>A player's vote: !mv &lt;round&gt; &lt;n&gt; from the vote screen (0 takes it back).</summary>
    static void CastBallot(string by, string steamId, string arg)
    {
        if (MapVote.ParseClick($"{MapVote.Verb} {arg}") is not { } click || Ballot is not { Open: true } b || b.Round != click.Round) { Tell(steamId, "That vote is over"); return; }
        if (click.Option == 0) { if (b.Choices.Remove(steamId)) Log($"vote: {by} {steamId} took their vote back"); return; }
        if (b.Options.All(o => o.N != click.Option)) { Tell(steamId, $"No option {click.Option} on this ballot"); return; }
        Present(steamId, by);
        var was = b.Choices.GetValueOrDefault(steamId);
        b.Choices[steamId] = click.Option;
        if (was != click.Option) Log($"vote: {by} {steamId} votes {click.Option} ({OptionLabel(b.Options[click.Option - 1])}){(was > 0 ? $", was {was}" : "")}");
        BallotTick(Now());
    }

    /// <summary>Every tick, and after every vote: a voter who left takes their vote along; a majority ends the vote early; the timer ends it.
    /// Once decided it stays on screen until its change has gone out.</summary>
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
        var needed = MapVote.Needed(count);
        var won = tally.Where(t => t.Value >= needed).Select(t => t.Key).FirstOrDefault();
        if (won > 0) { CloseBallot(b, now, won, "majority", tally, count); return; }
        if (now < b.Ends) return;
        var most = tally.Values.Max();
        var top = tally.Where(t => t.Value == most).Select(t => t.Key).ToList();
        if (most == 0) CloseBallot(b, now, b.Options[^1].N, "none", tally, count);
        else if (top.Count == 1) CloseBallot(b, now, top[0], "most", tally, count);
        else CloseBallot(b, now, top[Dice.Next(top.Count)], "tie", tally, count);
    }

    /// <summary>The vote is decided: what it chose goes out through the usual countdown, held as docs/reference.md says.</summary>
    static void CloseBallot(MatchVote b, DateTime now, int n, string how, Dictionary<int, int> tally, int present)
    {
        b.Open = false; b.DoneAt = now;
        if (InMaintenance()) { Log("vote: decided while the maintenance flag is set - no change"); Ballot = null; return; }
        var o = b.Options.First(x => x.N == n);
        Change? change = null;
        if (o.Source == VoteSource.Queue && o.Request is { } r && Queue.Contains(r))
        {
            Queue.Remove(r); // played as a request: its requesters hold it, their airtime runs from its map start
            var owners = r.By.Where(a => IsConnected(a.Id, now)).Select(a => new Owner(a.Id, a.Name)).ToList();
            change = new Change(r.Map, owners.Count > 0 ? Names(owners.Select(x => x.Name)) : "the vote", owners) { Voted = true };
        }
        else if (o.Map is { } map && !IsPack(map) && !IsRemoved(map) && !IsBlocked(map))
            change = new Change(map, "the vote", []) { Voted = true };
        else if (RandomPick(true, 1, b.FillMode) is { } rolled) // Random - or a map on the ballot that has become unplayable since
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
            "majority" => $"The vote chose {target} ({votes} of {present}) - changing in {ChangeDelay} s",
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
        v.Round = b.Round; v.Open = b.Open;
        v.SecondsLeft = b.Open ? Math.Max(0, (int)Math.Ceiling((b.Ends - now).TotalSeconds))
            : Pending is { } p && p == b.Change ? Math.Max(0, (int)Math.Ceiling((p.FiresAt - now).TotalSeconds)) : 0;
        (v.Present, v.Needed, v.Cast) = (count, MapVote.Needed(Math.Max(count, 1)), b.Choices.Count);
        foreach (var o in b.Options)
        {
            var who = o.Request?.By.Where(a => IsConnected(a.Id, now)).Select(a => Clip(a.Name, 16)).ToList() is { Count: > 0 } names ? Names(names) : "";
            v.Options.Add(new VoteState.Option(o.N, o.Mode.Id, o.Source, o.Map is { } m ? ArgOf(m) : "", o.Map is { } k ? Key(k) : "",
                o.Map is { } l ? Clip(Label(l), 40) : "Random", o.Map is { Official: false, Rank: > 0 } s ? Size(s) : "", b.Choices.Values.Count(n => n == o.N), who));
        }
        foreach (var (id, n) in b.Choices) if (AccountId(id) is >= 0 and var account) v.Choices[account] = n;
        v.Result = b.Result;
        return v;
    }

    /// <summary>The health page's line about the vote.</summary>
    static string VoteHealth() => $"vote: MATCH_END_VOTE {MatchEndVote}, a plugin that draws it {(VoteCapable() ? "is" : "is not")} polling; "
        + (Ballot is { } b ? $"round {b.Round} {(b.Open ? "open" : "decided")}, {b.Choices.Count} vote(s)" : "none open") + (VoteHeld ? "; the map on is the vote's" : "");
}
