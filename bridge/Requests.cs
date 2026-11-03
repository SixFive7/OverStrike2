// Map requests: who is on the server, the fair queue, who holds the map that is on, and the one scheduler that decides
// when a request or the dice change the map. Rules for players: README.md, "Requests and the fair queue".
static partial class Program
{
    public const int DisconnectGraceSeconds = 60; // a player who left keeps their requests (and their map) this long
    public const int ReloadGraceSeconds = 600;    // a map change logs every player as disconnected; reloading (and downloading) the next map may take this long
    public const int NominalMapSeconds = 600;     // what one map is assumed to cost when the order looks ahead, and the most credit a newcomer gets
    public const int MaxRequestsEach = 5;         // queued requests per player
    public const int FreshStartMinutes = 30;      // nobody on the server this long: everyone's airtime goes back to zero, the next evening starts even
    public const int NewsQuietMs = 2000, NewsMaxWaitMs = 6000, NewsGapMs = 8000; // queue news: merged after 2 s of quiet, never later than 6 s, at most one per 8 s

    // ---- presence: who is on the server, from the log stream alone ----

    /// <summary>One player as the log stream describes them. A map change logs every player as "disconnected" with reason
    /// NETWORK_DISCONNECT_SHUTDOWN and then "connected" again once they have loaded the new map (captured 2026-09-19), so that
    /// reason means "reloading", not "left"; any other reason (DISCONNECT_BY_USER, ...) is a real departure.</summary>
    sealed class Player
    {
        public string Name = "";
        public DateTime? GoneSince;   // really left at this time
        public DateTime? ReloadSince; // reloading since this map change or server restart
        public bool Joining;          // a real join is under way: "connected" after being away, "entered the game" still to come
    }

    static readonly Dictionary<string, Player> Players = new();
    static Func<DateTime> Now = () => DateTime.UtcNow; // --selftest hands its own clock

    static Player PlayerOf(string id) => Players.TryGetValue(id, out var p) ? p : Players[id] = new Player();

    /// <summary>Still counts as on the server: in game, inside the 60 s grace after leaving, or reloading inside the 10-minute load grace.</summary>
    static bool IsConnected(string id, DateTime now) => Players.TryGetValue(id, out var p)
        && (p.GoneSince is { } g ? (now - g).TotalSeconds <= DisconnectGraceSeconds
            : p.ReloadSince is not { } r || (now - r).TotalSeconds <= ReloadGraceSeconds);

    /// <summary>When a player who no longer counts as connected stopped counting: the moment they left, or the end of their load grace.</summary>
    static DateTime LeftAt(string id) => Players.TryGetValue(id, out var p) && p.GoneSince is { } g ? g
        : Players.TryGetValue(id, out p) && p.ReloadSince is { } r ? r.AddSeconds(ReloadGraceSeconds) : Now();

    /// <summary>A "connected" or "entered the game" line. Returns true when this is a real join, as opposed to the reconnect every map change causes.</summary>
    static bool Arrive(LogEvent e, DateTime now)
    {
        var known = Players.TryGetValue(e.SteamId, out var p);
        p ??= Players[e.SteamId] = new Player();
        var real = !known || p.GoneSince != null || (p.ReloadSince is { } r && (now - r).TotalSeconds > ReloadGraceSeconds);
        p.Name = e.Name; p.GoneSince = null; p.ReloadSince = null;
        if (e.Kind == Kind.Connected) { p.Joining |= real; return false; } // a join survives the reconnects in between: a map change, MultiAddonManager's addon download
        var fresh = p.Joining || real;
        p.Joining = false;
        return fresh;
    }

    /// <summary>A "disconnected" line. Any reason with SHUTDOWN in it is a reconnect, not a departure: NETWORK_DISCONNECT_SHUTDOWN at a map
    /// change, NETWORK_DISCONNECT_LOOPSHUTDOWN when MultiAddonManager makes a joining client reconnect to fetch the HUD addon. A join under
    /// way stays under way through those.</summary>
    static void Depart(string id, string reason, DateTime now)
    {
        var p = PlayerOf(id);
        if (reason.Contains("SHUTDOWN")) { if (p.GoneSince == null) p.ReloadSince ??= now; }
        else { p.GoneSince = now; p.ReloadSince = null; p.Joining = false; }
    }

    /// <summary>A chat line proves its sender is on the server.</summary>
    static void Present(string id, string name)
    {
        var p = PlayerOf(id);
        p.Name = name; p.GoneSince = null; p.ReloadSince = null;
    }

    /// <summary>A map start or a server restart: everyone in game is now reloading. The log usually said so already; this is the safety net.</summary>
    static void AllReloading(DateTime now)
    {
        foreach (var p in Players.Values) if (p.GoneSince == null) p.ReloadSince ??= now;
    }

    // ---- the queue ----

    sealed record Asker(string Id, string Name, DateTime At, long Seq);

    /// <summary>One map somebody asked for, with everyone who asked for it in the order they did.</summary>
    sealed class Request(MapInfo map)
    {
        public MapInfo Map { get; } = map;
        public List<Asker> By { get; } = new();
    }

    /// <summary>A requester of the map that is on (or on its way). Since: when the map started playing for them; Done: they let go.</summary>
    sealed class Owner(string id, string name)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public DateTime? Since;
        public bool Done;
    }

    /// <summary>A map change counting down. No owners: an automatic one (the dice, !next, the start roll), which a request replaces at once.</summary>
    sealed class Change(MapInfo map, string by, List<Owner> owners)
    {
        public MapInfo Map { get; } = map;
        public string By { get; } = by;
        public List<Owner> Owners { get; } = owners;
        public bool Voted { get; init; }        // the end-of-match vote chose it (VoteRound.cs): no request replaces its countdown
        public bool Auto => Owners.Count == 0 && !Voted;
        public DateTime FiresAt; // when the countdown ends and the change goes out (StartChange); the HUD's banner counts down to it
    }

    static readonly List<Request> Queue = new();
    static readonly List<Owner> Owners = new();           // the requesters of the map that is on: S.Map, loaded or still loading
    static readonly Dictionary<string, double> Airtime = new(); // seconds their requested maps have played, per SteamID, since the bridge started
    static double VClock;                                  // the least airtime among active requesters, never decreasing: what a newcomer is lifted to
    static long AskSeq;
    static bool StartRollOwed;                             // a (re)started server: roll once, after its start map (TryStartRoll)
    static bool StartRollNeedsProof;                       // owed by BridgeStart: roll only if RCON status says the start map is loaded
    static DateTime StartMapAt, StartRollRetryAt;          // when the start map logged its start; when to probe RCON again
    static DateTime LastNamedMapStart;                     // a nameless map start within 10 s of a named one belongs to it
    static DateTime HoldUntil;                             // no automatic change before this: a new process is still loading its start map
    static DateTime? EmptySince;                           // nobody connected since then
    static bool MaintenanceOn;

    /// <summary>Owners of the map that is on who still hold it: not done and still connected (grace included).</summary>
    static List<Owner> Holders(DateTime now) => Owners.Where(o => !o.Done && IsConnected(o.Id, now)).ToList();

    static void Release(Owner o, DateTime at)
    {
        if (o.Done) return;
        if (o.Since is { } s && at > s) Airtime[o.Id] = Airtime.GetValueOrDefault(o.Id) + (at - s).TotalSeconds;
        o.Since = null; o.Done = true;
    }

    static void ReleaseAll(DateTime at) { foreach (var o in Owners) Release(o, at); }

    /// <summary>Airtime as the order sees it: what a player's requests have played, plus the map of theirs that is on now,
    /// counted as at least one nominal map - so the next request of someone whose map just started does not tie with a newcomer's first.</summary>
    static double Effective(string id, DateTime now)
    {
        var a = Airtime.GetValueOrDefault(id);
        var mine = AllOwners().FirstOrDefault(o => o.Id == id && !o.Done);
        if (mine != null) a += Math.Max(mine.Since is { } s ? (now - s).TotalSeconds : 0, NominalMapSeconds);
        return a;
    }

    static IEnumerable<Owner> AllOwners() => Pending is { } p ? Owners.Concat(p.Owners) : Owners;

    static bool Active(string id) => Queue.Any(r => r.By.Any(a => a.Id == id)) || AllOwners().Any(o => o.Id == id && !o.Done);

    /// <summary>The newcomer rule (min_vruntime in Linux's CFS): a player who becomes active is lifted to at least one nominal
    /// map below the least-served active requester, so idling all evening buys one catch-up turn, not five in a row.</summary>
    static void Normalize(string id, DateTime now)
    {
        if (Active(id)) return;
        var active = Queue.SelectMany(r => r.By.Select(a => a.Id)).Concat(AllOwners().Where(o => !o.Done).Select(o => o.Id)).Distinct().ToList();
        if (active.Count > 0) VClock = Math.Max(VClock, active.Min(a => Effective(a, now)));
        Airtime[id] = Math.Max(Airtime.GetValueOrDefault(id), VClock - NominalMapSeconds);
    }

    /// <summary>The play order. Maps that two or more connected players asked for come first (most askers first, then the oldest
    /// ask). Then one map at a time to whoever has had the least airtime, each turn counted as one nominal map; ties go to the
    /// earlier ask. A player's own requests keep the order they made them in. Deterministic: same state, same order.</summary>
    static List<Request> Order(DateTime now)
    {
        var live = Queue.Select(r => (Req: r, By: r.By.Where(a => IsConnected(a.Id, now)).ToList())).Where(x => x.By.Count > 0).ToList();
        var v = live.SelectMany(x => x.By).Select(a => a.Id).Distinct().ToDictionary(id => id, id => Effective(id, now));
        var order = live.Where(x => x.By.Count >= 2).OrderByDescending(x => x.By.Count).ThenBy(x => x.By.Min(a => a.Seq)).Select(x => x.Req).ToList();
        foreach (var x in live.Where(x => x.By.Count >= 2)) foreach (var a in x.By) v[a.Id] += NominalMapSeconds;
        var turns = live.Where(x => x.By.Count == 1).GroupBy(x => x.By[0].Id)
            .ToDictionary(g => g.Key, g => new System.Collections.Generic.Queue<(Request Req, Asker By)>(g.Select(x => (x.Req, x.By[0])).OrderBy(x => x.Item2.Seq)));
        while (turns.Values.Any(q => q.Count > 0))
        {
            var next = turns.Where(t => t.Value.Count > 0).OrderBy(t => v[t.Key]).ThenBy(t => t.Value.Peek().By.Seq).First();
            order.Add(next.Value.Dequeue().Req);
            v[next.Key] += NominalMapSeconds;
        }
        return order;
    }

    /// <summary>Drops what departed players left behind: their places in the queue, and their hold on the map that is on.
    /// Returns the names of holders it let go, for the announcement of whatever replaces their map.</summary>
    static string Prune(DateTime now)
    {
        var dropped = new Dictionary<string, int>();
        foreach (var r in Queue)
            foreach (var a in r.By.Where(a => !IsConnected(a.Id, now)).ToList())
            {
                r.By.Remove(a);
                dropped[a.Name] = dropped.GetValueOrDefault(a.Name) + 1;
            }
        Queue.RemoveAll(r => r.By.Count == 0);
        foreach (var (name, n) in dropped) News(n == 1 ? $"{name} left, their request is gone." : $"{name} left, their {n} requests are gone.");
        var gone = Owners.Where(o => !o.Done && !IsConnected(o.Id, now)).ToList();
        foreach (var o in gone) Release(o, LeftAt(o.Id));
        if (Pending is { Auto: false } p) p.Owners.RemoveAll(o => !IsConnected(o.Id, now));
        return gone.Count == 0 ? "" : Names(gone.Select(o => o.Name).Distinct());
    }

    /// <summary>The one decision point for requests. Starts the head of the queue when the map that is on is free - nobody
    /// holds it, nothing requested is counting down - replacing an automatic countdown outright. Direct: a player's command
    /// just asked, which the maintenance flag does not stop. Returns the request it started, or null.</summary>
    static Request? Schedule(DateTime now, bool direct = false, string lead = "")
    {
        var left = Prune(now);
        var head = Order(now).FirstOrDefault();
        if (head == null) return null;
        if (Ballot is { Open: true }) return null; // VoteRound.cs: while the vote is open, requests wait for the next one
        if (!direct && (now < HoldUntil || InMaintenance())) return null;
        if (Pending is { Auto: false }) return null;
        if (Holders(now).Count > 0 || VoteHeld) return null; // VoteHeld: the vote's map, held until its match ends
        if (Pending == null && now < CooldownUntil) return null; // the tick retries
        Begin(head, now, (left != "" ? $"{left} left. " : "") + lead);
        return head;
    }

    static void Begin(Request head, DateTime now, string lead)
    {
        Queue.Remove(head);
        var owners = head.By.Where(a => IsConnected(a.Id, now)).Select(a => new Owner(a.Id, a.Name)).ToList();
        var by = Names(owners.Select(o => o.Name));
        if (Pending is { Auto: true } auto) Log($"request replaces the automatic change to {Label(auto.Map)}");
        Pending = null; // the countdown callbacks check identity, so an automatic one stops here
        StartChange(new Change(head.Map, by, owners), $"{lead}{by} picked {Label(head.Map)}{Detail(head.Map)}, changing in {ChangeDelay} s");
    }

    // ---- what players type ----

    /// <summary>A map request that passed the lookup and the confirm gate. The map that is on: claimed or joined. Already
    /// queued by someone else: joined, which moves it up. Otherwise a new place in the queue - and if the map that is on is
    /// free, Schedule starts it at once.</summary>
    static void Ask(string by, string id, MapInfo map, DateTime now)
    {
        Present(id, by); // a request is a chat line: its sender is on the server
        var key = Key(map);
        var on = Pending?.Map ?? S.Map;
        if (on != null && Key(on) == key)
        {
            var owners = Pending?.Owners ?? Owners;
            if (owners.Any(o => o.Id == id && !o.Done)) { Tell(id, $"{Label(map)} is already yours"); return; }
            var held = owners.Where(o => !o.Done && IsConnected(o.Id, now)).Select(o => o.Name).ToList();
            if (held.Count == 0) SkipVotes.Clear(); // a new hold: votes against the last one do not carry over
            Normalize(id, now);
            owners.Add(new Owner(id, by) { Since = Pending == null && !AnnouncePending ? now : null });
            News(held.Count > 0 ? $"{by} joined {Names(held)} on {Clip(Label(map), 24)}." : $"{by} claimed {Clip(Label(map), 24)}, theirs until the match ends or they are done.");
            return;
        }
        var same = Queue.FirstOrDefault(r => Key(r.Map) == key);
        if (same != null && same.By.Any(a => a.Id == id)) { Tell(id, $"{Label(map)} is already in the queue, {Ordinal(Order(now).IndexOf(same) + 1)} in line"); return; }
        if (Queue.Count(r => r.By.Any(a => a.Id == id)) >= MaxRequestsEach) { Tell(id, $"You have {MaxRequestsEach} maps in the queue already - cancel one on the Queue page first"); return; }
        Normalize(id, now);
        var entry = same ?? new Request(map);
        entry.By.Add(new Asker(id, by, now, ++AskSeq));
        if (same == null) Queue.Add(entry);
        if (Schedule(now, direct: true) == entry) return;
        News(same != null ? $"{by} also wants {Clip(Label(map), 24)}." : $"{by} queued {Clip(Label(map), 24)}."); // the board's queue line shows the place
    }

    /// <summary>!done: the requester lets their map go. Last holder out, the queue moves on at once; with nothing queued the
    /// map stays on, free, and the next request replaces it.</summary>
    static void Done(string by, string id)
    {
        var now = Now();
        if (Pending is { Auto: false } p && p.Owners.FirstOrDefault(o => o.Id == id) is { } leaving)
        {
            p.Owners.Remove(leaving);
            Announce(p.Owners.Count > 0 ? $"{by} dropped out of {Label(p.Map)}, it still comes for {Names(p.Owners.Select(o => o.Name))}"
                : $"{by} let {Label(p.Map)} go - it still loads, and the next pick replaces it right away");
            return;
        }
        var mine = Owners.FirstOrDefault(o => o.Id == id && !o.Done);
        if (mine == null)
        {
            if (Holders(now).Count > 0 || VoteHeld) { Skip(by, id); return; } // somebody else's map, or the vote's: a non-holder's !done is a vote to skip it
            Tell(id, $"Nobody holds {CurrentLabel()} - pick a map and it plays at once");
            return;
        }
        Release(mine, now);
        var still = Holders(now);
        if (still.Count > 0) { News($"{by} is done with {Clip(CurrentLabel(), 24)}, it stays for {Names(still.Select(o => o.Name))}."); return; }
        if (Schedule(now, direct: true, lead: $"{by} is done. ") != null) return;
        News($"{by} is done with {Clip(CurrentLabel(), 24)}, the next pick replaces it right away.");
    }

    /// <summary>!cancel drops your newest request; !cancel all, every one; !cancel 3, your request in place 3 of !queue; !cancel rush, your request for that map.</summary>
    static void Cancel(string by, string id, string arg)
    {
        var now = Now();
        var order = Order(now);
        var mine = order.Where(r => r.By.Any(a => a.Id == id)).ToList();
        if (mine.Count == 0) { Tell(id, "You have nothing in the queue"); return; }
        List<Request> drop;
        if (arg == "") drop = [mine.MaxBy(r => r.By.First(a => a.Id == id).Seq)!];
        else if (arg.Equals("all", StringComparison.OrdinalIgnoreCase)) drop = mine;
        else if (int.TryParse(arg, out var n) && n >= 1 && n <= order.Count && mine.Contains(order[n - 1])) drop = [order[n - 1]];
        else if (Find(arg) is [var m] && mine.FirstOrDefault(r => Key(r.Map) == Key(m)) is { } hit) drop = [hit];
        else { Tell(id, "Cancel your requests on the Queue page"); return; }
        foreach (var r in drop)
        {
            r.By.RemoveAll(a => a.Id == id);
            if (r.By.Count == 0) Queue.Remove(r);
        }
        News(drop.Count == 1 ? $"{by} cancelled {Clip(Label(drop[0].Map), 24)}." : $"{by} cancelled {drop.Count} requests.");
    }

    /// <summary>!previous: ask for the last map before this one, with every rule of an ordinary !map request.</summary>
    static void Previous(string by, string id)
    {
        var now = Now();
        var on = Pending?.Map ?? S.Map;
        var prev = Enumerable.Reverse(S.History).FirstOrDefault(m => on == null || Key(m) != Key(on));
        if (prev == null) { Tell(id, "There is no previous map on record yet"); return; }
        if (IsPack(prev)) { Tell(id, PackRefusal(prev)); return; }
        if (IsBlocked(prev)) { Tell(id, BlockedRefusal(prev)); return; }
        if (IsRemoved(prev)) { Tell(id, RemovedRefusal(prev)); return; }
        if (ConfirmGate(prev, by, id, now, "Previous") is { } warning) { Tell(id, warning, PickConfirmSeconds); return; }
        Ask(by, id, prev, now);
    }

    // ---- !skip: the other players' way out of somebody's request ----

    static readonly HashSet<string> SkipVotes = new(); // SteamIDs voting the held map off; cleared whenever the map changes or is let go

    /// <summary>The skip count: votes from players in game who do not hold the map, out of all such players, and the majority
    /// needed. Null when nobody holds the map (the votes are then cleared) or the holders are alone: no majority to reach.</summary>
    static (int Votes, int Of, int Needed)? SkipTally(DateTime now)
    {
        var holders = Holders(now).Select(o => o.Id).ToHashSet();
        if (holders.Count == 0 && !VoteHeld) { SkipVotes.Clear(); return null; } // the vote's map is held too (VoteRound.cs)
        var others = Players.Where(kv => kv.Value.GoneSince == null && !holders.Contains(kv.Key)).Select(kv => kv.Key).ToList();
        return others.Count == 0 ? null : (others.Count(SkipVotes.Contains), others.Count, others.Count / 2 + 1);
    }

    /// <summary>!skip: a vote to end somebody else's requested map. It counts while the voter is on the server; a majority of the
    /// players who do not hold the map lets it go exactly as the holders' !done would. A holder's own !skip is their !done.</summary>
    static void Skip(string by, string id)
    {
        var now = Now();
        if (Owners.Any(o => o.Id == id && !o.Done)) { Done(by, id); return; }
        if (SkipTally(now) is not { } t) { Tell(id, $"Nobody holds {CurrentLabel()} - pick a map and it plays at once"); return; }
        if (!SkipVotes.Add(id)) { Tell(id, $"You voted already - {t.Votes}/{t.Of}, {t.Needed - t.Votes} more needed"); return; }
        CheckSkip(now, by);
    }

    /// <summary>After a vote, and every tick (a player leaving shrinks the count): release the map once the majority is there.</summary>
    static void CheckSkip(DateTime now, string? by = null)
    {
        if (SkipVotes.Count == 0 || SkipTally(now) is not { } t) return;
        if (t.Votes < t.Needed)
        {
            if (by != null) News($"{by} voted to skip {Clip(CurrentLabel(), 24)}: {t.Votes}/{t.Of} - {t.Needed - t.Votes} more needed.");
            return;
        }
        SkipVotes.Clear(); VoteHeld = false;
        foreach (var o in Owners.Where(o => !o.Done)) Release(o, now);
        Log($"skip: {t.Votes} of {t.Of} voted {CurrentLabel()} off");
        if (Schedule(now, direct: true, lead: $"Skipped by vote ({t.Votes}/{t.Of}). ") != null) return;
        News($"{Clip(CurrentLabel(), 24)} skipped by vote ({t.Votes}/{t.Of}), the next pick replaces it right away.");
    }


    /// <summary>The live state for the HUD (Hud.cs): the same facts !queue and !top say in chat - the map that is on, who picked it and
    /// who holds it, its votes, the queue in play order, the most liked maps - plus a change counting down. Rebuilt every tick.</summary>
    static HudState BuildHud(DateTime now)
    {
        var key = S.Map != null ? Key(S.Map) : S.LogName ?? "";
        var holders = Holders(now);
        var h = new HudState
        {
            Maps = Playable,
            Map = S.Map != null || S.LogName != null ? Clip(CurrentLabel(), 40) : "",
            By = S.PickedBy ?? "",
            Size = S.Map is { Official: false, Rank: > 0 } m ? Size(m) : "",
            Key = key,
            Loading = AnnouncePending,
            Holders = holders.Select(o => AccountId(o.Id)).Where(a => a >= 0).Distinct().Concat(VoteHeld && holders.Count == 0 ? [0L] : []).ToList(), // 0: the vote holds it, so the board's button is a skip vote
            Status = Pending is { } p ? $"Changing to {Clip(Label(p.Map), 32)}"
                : Ballot is { Open: true } ? "Vote for the next map"
                : holders.Count > 0 ? $"{Names(holders.Select(o => Clip(o.Name, 16)))}'s map until the match ends or they are done"
                : VoteHeld ? "The vote's map until the match ends, or a skip vote"
                : "Nobody holds it: the next pick replaces it right away",
        };
        (h.Up, h.Down) = key == "" ? (0, 0) : Tally(key);
        if ((holders.Count > 0 || VoteHeld) && SkipTally(now) is { } skip) h.Skip = skip;
        if (key != "" && Votes.TryGetValue(key, out var votes))
            foreach (var (id, value) in votes) if (AccountId(id) is >= 0 and var account) h.MyVotes[account] = value;
        foreach (var r in Order(now).Take(HudState.ListLength))
            h.Next.Add((Clip(Label(r.Map), 36), Names(r.By.Where(a => IsConnected(a.Id, now)).Select(a => Clip(a.Name, 16)))));
        h.Top = TopRows().Take(HudState.ListLength).Select(r => (Clip(Label(r.Map), 36), r.Up, r.Down)).ToList();
        foreach (var r in Order(now).Take(HudState.QueueLength))
            h.Queue.Add(new HudState.QueueEntry(ArgOf(r.Map), Key(r.Map), Clip(Label(r.Map), 40), Names(r.By.Where(a => IsConnected(a.Id, now)).Select(a => Clip(a.Name, 16))),
                r.By.Select(a => AccountId(a.Id)).Where(a => a >= 0).Distinct().ToList()));
        h.RoundMinutes = S.RoundMinutes;
        h.NewMaps = NewMapsMode;
        if (key != "") (h.Big, h.SizeClass) = (TooBigVotes(key), S.Map is { } on ? SizeClass(on) ?? "" : Sizes.TryGetValue(key, out var e) ? e.Class ?? "" : "");
        if (key != "") h.Ads = AdsVotes(key);
        h.CatalogVersion = CatalogVersion;
        h.CheckMode = HudCheck.Name(HudCheckMode);
        h.TabMode = HudTab.Name(HudTabMode);
        h.Proofs = HudCheckHold ? new() : S.HudProofs; // Selection.cs: while held, no proof is served, so the plugin's check stays a dry run (lifted 2026-10-24)
        h.Verified = S.HudVerified;
        h.SearchVersion = Search.Version;
        if (Enumerable.Reverse(S.History).FirstOrDefault(m => Key(m) != key) is { } prev) (h.PrevLabel, h.PrevArg) = (Clip(Label(prev), 40), ArgOf(prev));
        PutNotices(h, now);
        PutWhy(h, now); // Votes.cs: the Dislike popups that are open
        if (Pending is { } c)
        {
            h.ChangeMap = Clip(Label(c.Map), 40);
            h.ChangeBy = c.By;
            h.ChangeIn = Math.Max(0, (int)Math.Ceiling((c.FiresAt - now).TotalSeconds));
        }
        return h;
    }

    /// <summary>What is on and who holds it, then the queue in play order, packed into at most maxLines chat lines.</summary>
    static List<string> QueueLines(DateTime now, int maxLines)
    {
        var holders = Holders(now);
        var lines = new List<string>
        {
            Pending is { } p ? $"Changing to {Label(p.Map)}{(p.Owners.Count == 0 ? "" : $" for {Names(p.Owners.Select(o => o.Name))}")}"
            : holders.Count > 0 ? $"Now: {CurrentLabel()}, {Names(holders.Select(o => o.Name))}'s until the match ends or !done"
            : $"Now: {CurrentLabel()}, free - the next !map replaces it right away",
        };
        var order = Order(now);
        if (order.Count == 0) { lines.Add("The queue is empty - !map <number> to ask for a map"); return lines; }
        lines.AddRange(Pack("Next: ", order.Select((r, i) => $"{i + 1}. {Clip(Label(r.Map), 24)} ({string.Join('+', r.By.Where(a => IsConnected(a.Id, now)).Select(a => Clip(a.Name, 12)))})").ToList(), maxLines - 1));
        return lines;
    }

    /// <summary>Items joined into lines of at most 120 characters, the first with a prefix. Past maxLines the rest is counted, not
    /// shown: the last line ends in "+N more", dropping its own tail items if that is what it takes to fit.</summary>
    static List<string> Pack(string prefix, IReadOnlyList<string> items, int maxLines)
    {
        var lines = new List<List<string>>();
        var text = new List<string>();
        string Join(List<string> l, int index) => (index == 0 ? prefix : "") + string.Join(' ', l);
        foreach (var item in items)
        {
            if (text.Count > 0 && Join([.. text, item], lines.Count).Length > 120) { lines.Add(text); text = new(); }
            text.Add(item);
        }
        if (text.Count > 0) lines.Add(text);
        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            var last = lines[^1];
            while (true)
            {
                var hidden = items.Count - lines.Sum(l => l.Count);
                if (last.Count <= 1 || Join(last, lines.Count - 1).Length + $" +{hidden} more".Length <= 120) break;
                last.RemoveAt(last.Count - 1);
            }
            last.Add($"+{items.Count - lines.Sum(l => l.Count)} more");
        }
        return lines.Select((l, i) => Join(l, i)).ToList();
    }

    static string Clip(string text, int n) => text.Length <= n ? text : text[..(n - 1)] + "~";
    static string Names(IEnumerable<string> names) => names.ToList() is var l && l.Count <= 1 ? l.FirstOrDefault() ?? "nobody" : string.Join(", ", l.SkipLast(1)) + " and " + l[^1];
    static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    // ---- queue news: every change is announced, merged into one line (plus the order) per burst ----

    static readonly List<string> NewsItems = new();
    static DateTime NewsFirst, NewsLast, NewsSent;

    static void News(string item)
    {
        var now = Now();
        if (NewsItems.Count == 0) NewsFirst = now;
        NewsItems.Add(item);
        NewsLast = now;
        After(NewsQuietMs, FlushNews);
    }

    static void FlushNews()
    {
        if (NewsItems.Count == 0) return;
        var now = Now();
        var due = new[] { NewsLast.AddMilliseconds(NewsQuietMs), NewsFirst.AddMilliseconds(NewsMaxWaitMs) }.Min();
        if (NewsSent.AddMilliseconds(NewsGapMs) > due) due = NewsSent.AddMilliseconds(NewsGapMs);
        if (now < due) { After((int)Math.Ceiling((due - now).TotalMilliseconds), FlushNews); return; }
        var items = NewsItems.ToList();
        NewsItems.Clear();
        NewsSent = now;
        Announce(string.Join("  ", items));
        if (Order(now).Count > 0) Log("queue: " + string.Join(" | ", QueueLines(now, 2))); // the board's queue line shows the same
    }

    // ---- the maintenance flag: a file the map-test sweep creates and deletes ----

    static string MaintenanceFile => Path.Combine(StateDir, "maintenance");

    /// <summary>True while STATE_DIR/maintenance exists: no automatic map change of any kind. Checked at every decision and
    /// every tick; each flip is logged, never said in chat.</summary>
    static bool InMaintenance()
    {
        var on = File.Exists(MaintenanceFile);
        if (on != MaintenanceOn)
        {
            MaintenanceOn = on;
            Log(on ? $"maintenance: {MaintenanceFile} exists - no automatic map changes (start roll, match-end roll, queue advance) until it is gone"
                : $"maintenance: {MaintenanceFile} is gone - automatic map changes resume");
        }
        return on;
    }

    // ---- the clock: once a second ----

    /// <summary>Grace periods run out, a load that never arrived is given up on, the maintenance flag is noticed, and the queue gets its chance.</summary>
    static void Tick()
    {
        var now = Now();
        InMaintenance();
        if (Players.Keys.Any(id => IsConnected(id, now))) EmptySince = null; else EmptySince ??= now;
        if (EmptySince is { } empty && (now - empty).TotalMinutes >= FreshStartMinutes && (Airtime.Count > 0 || VClock > 0))
        {
            Airtime.Clear(); VClock = 0;
            Log($"airtime: nobody on for {FreshStartMinutes} min - everyone starts even");
        }
        ResetWhenEmpty(now); // Selection.cs: nobody on for over 10 min - the new-maps setting and the grenade drops back to their defaults
        if (AnnouncePending && now >= AnnounceDeadline)
        {
            AnnouncePending = false;
            ReleaseAll(now);
            Log($"load timed out: {(S.Map != null ? Label(S.Map) : "?")} never logged a map start");
            if (S.Map != null) Announce($"{Label(S.Map)} never finished loading - moving on");
            S.Map = null; S.PickedBy = null; S.PickedAt = null;
            ModeBack(); VoteHeld = false; // ModeSwitch.cs: the mode it was going to goes back
            Save();
        }
        CheckSkip(now);
        ProbeModeIfDue(now); // ModeSwitch.cs: which mode the server is in, when the bridge does not know
        BallotTick(now);     // VoteRound.cs: the vote's timer, and voters who left
        TryStartRoll(now); // before Schedule, so a waiting request is announced as the start's
        Schedule(now);
        QueueTick(now); // RandomQueue.cs: the random queues topped up, queues.json refreshed
        WatchTick(); // the data files (Reload.cs)
        RefreshHud();
    }
}
