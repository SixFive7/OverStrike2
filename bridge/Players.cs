// Who is on the server (from the log stream alone), the map change that counts down, the HUD's live state, the maintenance flag and the
// clock. There is no request queue: a player's pick calls a vote (VoteRound.cs).
static partial class Program
{
    public const int ReloadGraceSeconds = 600;    // a map change logs every player as disconnected; reloading (and downloading) the next map may take this long
    public const int LoadMinutes = 10;            // a change that has not logged its map start in this long is given up...
    public const int LoadBehindDownloadMinutes = 30; // ...unless the restart watcher reports a background download it may wait behind: then up to this long

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

    /// <summary>A chat line or a click proves its sender is on the server.</summary>
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

    // ---- the change that counts down ----

    /// <summary>A map change counting down CHANGE_DELAY_SECONDS: the vote's result, or one of the dice's automatic changes (Auto: the start roll,
    /// the way back to Arms Race on an empty server, a map gone from the Workshop or one that crashes the players' games).</summary>
    sealed class Change(MapInfo map, string by)
    {
        public MapInfo Map { get; } = map;
        public string By { get; } = by;
        public bool Auto { get; init; }
        public DateTime FiresAt; // when the countdown ends and the change goes out (StartChange); the HUD's banner counts down to it
    }

    static bool StartRollOwed;                             // a (re)started server: roll once, after its start map (TryStartRoll)
    static bool StartRollNeedsProof;                       // owed by BridgeStart: roll only if RCON status says the start map is loaded
    static DateTime StartMapAt, StartRollRetryAt;          // when the start map logged its start; when to probe RCON again
    static DateTime LastNamedMapStart;                     // a nameless map start within 10 s of a named one belongs to it
    static bool MaintenanceOn;

    /// <summary>The dice's automatic change (one helper for the four): the start roll, the empty server's way back to Arms Race, a map gone from
    /// the Workshop, a map that crashes the players' games - the times nobody can vote. A map on the server's disk (RandomQueue.cs). Lead says why.
    /// False: nothing to roll.</summary>
    static bool AutoRoll(string lead, Modes.Mode? mode = null)
    {
        if (RandomNext(mode) is not { } map) return false;
        StartChange(new Change(map, "the dice") { Auto = true }, $"{lead} - rolling the dice: {Label(map)}{Detail(map, MapTags.WhyWords(TagsOf(map), picked: false))}{ModeTag(map)}");
        return true;
    }

    /// <summary>The live state for the HUD (Hud.cs): the map that is on, who or what picked it, its likes, the most liked maps, a change
    /// counting down, a restart coming, the background download, the notes and the news. Rebuilt every tick.</summary>
    static HudState BuildHud(DateTime now)
    {
        var key = CurrentKey;
        var h = new HudState
        {
            Maps = Playable,
            Map = S.Map != null || S.LogName != null ? Clip(CurrentLabel(), 40) : "",
            By = S.PickedBy ?? "",
            Size = S.Map is { Official: false, Rank: > 0 } m ? Size(m) : "",
            Key = key,
            Loading = AnnouncePending,
            Status = Pending is { } p ? $"Changing to {Clip(Label(p.Map), 32)}"
                : Ballot is { Open: true } ? "Vote for the next map"
                : "Plays until its match ends - or start a vote",
        };
        (h.Up, h.Down) = key == "" ? (0, 0) : Tally(key);
        if (key != "" && Votes.TryGetValue(key, out var votes))
            foreach (var (id, value) in votes) if (AccountId(id) is >= 0 and var account) h.MyVotes[account] = value;
        h.Top = TopRows().Take(HudState.ListLength).Select(r => (Clip(Label(r.Map), 36), r.Up, r.Down)).ToList();
        h.RoundMinutes = S.RoundMinutes;
        if (key != "") (h.Big, h.SizeClass) = (TooBigVotes(key), S.Map is { } on ? SizeClass(on) ?? "" : Sizes.TryGetValue(key, out var e) ? e.Class ?? "" : "");
        if (key != "") h.Ads = AdsVotes(key);
        h.CatalogVersion = CatalogVersion;
        h.CheckMode = HudCheck.Name(HudCheckMode);
        h.TabMode = HudTab.Name(HudTabMode);
        h.Proofs = HudCheckHold ? new() : S.HudProofs; // Selection.cs: while held, no proof is served, so the plugin's check stays a dry run (lifted 2026-10-24)
        h.Verified = S.HudVerified;
        h.SearchVersion = Search.Version;
        h.DashVersion = DashVersion; // SyncDash.cs: the Sync page's block
        if (Enumerable.Reverse(S.History).FirstOrDefault(m => Key(m) != key) is { } prev) (h.PrevLabel, h.PrevArg) = (Clip(Label(prev), 40), ArgOf(prev));
        PutNotices(h, now);
        PutWhy(h, now); // Votes.cs: the Dislike popups that are open
        PutRestart(h, now); // Restarts.cs: a restart coming, with its countdown
        if (BackgroundNow() is { } bg) (h.BgId, h.BgPercent, h.BgTitle) = (long.TryParse(bg.Id, out var bgId) ? bgId : 0, bg.Percent ?? -1, bg.Title); // RandomQueue.cs
        if (Pending is { } c)
        {
            h.ChangeMap = Clip(Label(c.Map), 40);
            h.ChangeBy = c.By;
            h.ChangeIn = Math.Max(0, (int)Math.Ceiling((c.FiresAt - now).TotalSeconds));
        }
        return h;
    }

    static string Clip(string text, int n) => text.Length <= n ? text : text[..(n - 1)] + "~";
    static string Names(IEnumerable<string> names) => names.ToList() is var l && l.Count <= 1 ? l.FirstOrDefault() ?? "" : string.Join(", ", l.SkipLast(1)) + " and " + l[^1];

    // ---- the maintenance flag: a file the map-test sweep creates and deletes ----

    static string MaintenanceFile => Path.Combine(StateDir, "maintenance");

    /// <summary>True while STATE_DIR/maintenance exists: no automatic map change of any kind, and a vote's result changes nothing. Checked at every
    /// decision and every tick; each flip is logged, never said in chat.</summary>
    static bool InMaintenance()
    {
        var on = File.Exists(MaintenanceFile);
        if (on != MaintenanceOn)
        {
            MaintenanceOn = on;
            Log(on ? $"maintenance: {MaintenanceFile} exists - no automatic map changes (the start roll, the dice) and no vote's result until it is gone"
                : $"maintenance: {MaintenanceFile} is gone - automatic map changes resume");
        }
        return on;
    }

    // ---- the clock: once a second ----

    /// <summary>A load that never arrived is given up on, the maintenance flag is noticed, the vote's timer runs, and the start roll gets its chance.
    /// A load the restart watcher reported a background download during may wait behind it (Steam's client runs one at a time) and then download its
    /// own map: it is given up only LoadBehindDownloadMinutes after the change went out.</summary>
    static void Tick()
    {
        var now = Now();
        InMaintenance();
        ResetWhenEmpty(now); // Selection.cs: nobody on for over 10 min - the fun settings back to their defaults
        if (AnnouncePending && BackgroundNow() is { } behind && !LoadBehindDownload)
        {
            LoadBehindDownload = true;
            Log($"load: {(S.Map != null ? Label(S.Map) : "?")} may wait behind the background download of {behind.Id} - it has {LoadBehindDownloadMinutes} min to start");
        }
        if (AnnouncePending && now >= AnnounceDeadline && LoadBehindDownload && AnnounceDeadline < AnnounceSentAt.AddMinutes(LoadBehindDownloadMinutes))
            AnnounceDeadline = AnnounceSentAt.AddMinutes(LoadBehindDownloadMinutes);
        if (AnnouncePending && now >= AnnounceDeadline)
        {
            AnnouncePending = false;
            Log($"load timed out: {(S.Map != null ? Label(S.Map) : "?")} never logged a map start");
            if (S.Map != null) Announce($"{Label(S.Map)} never finished loading");
            S.Map = null; S.PickedBy = null;
            ModeBack(); // ModeSwitch.cs: the mode it was going to goes back
            Save();
        }
        ProbeModeIfDue(now); // ModeSwitch.cs: which mode the server is in, when the bridge does not know
        BallotTick(now);     // VoteRound.cs: the vote's timer, and voters who left
        TryStartRoll(now);
        QueueTick(now);      // RandomQueue.cs: the stock checked and topped up, queues.json refreshed
        RestartTick(now);    // Restarts.cs: a restart's notice that has run out
        WatchTick();         // the data files (Reload.cs)
        SyncTick(now);       // SyncDash.cs: the sync's status file, the Sync page's block and the website's files
        RefreshHud();
    }
}
