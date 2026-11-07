// The HUD's live state as the bridge builds it - the state (BuildHud, and the vote's lines: VoteRound.cs), the map catalog - and whether a
// plugin that draws the HUD is polling. HudTransport.cs publishes the state and serves the polls.

static partial class Program
{
    // ---- the HUD: the plugin reads the live state here and sends players' clicks back (see Hud.cs) ----

    static long hudPolledTicks;                              // when the plugin last asked for it (UTC ticks, written by the HTTP thread)
    const int HudActiveSeconds = Hud.LongPollSeconds + 15; // a poll counts when it arrives, and the bridge then holds it up to LongPollSeconds: on a quiet stretch the last arrival is that old

    /// <summary>A plugin that draws the HUD is polling: the HUD addon is set on the game server and its plugin is alive. While it is, the vote opens
    /// at a match end (somebody sees the ballot) and the HUD shows the map-change countdown and the now-playing card. A plugin that runs undrawn
    /// polls with draw=0 and does not count, from that poll on: nobody would see a ballot that freezes everybody (a hold, HudHeld).</summary>
    static Func<bool> HudActive = () => Interlocked.Read(ref hudPolledTicks) is var at && at > Interlocked.Read(ref hudHeldTicks)
        && DateTime.UtcNow.Ticks - at < TimeSpan.FromSeconds(HudActiveSeconds).Ticks;

    static void HudPolled(bool draws) => Interlocked.Exchange(ref draws ? ref hudPolledTicks : ref hudHeldTicks, DateTime.UtcNow.Ticks);

    static long hudHeldTicks; // when a poll last said its plugin draws nothing: Steam is not showing the HUD (HudScreen.RequireAddon)

    /// <summary>A hold: the plugin's last poll said it draws nothing. Nobody may call a vote then: it freezes everybody, over a ballot not everybody can see.</summary>
    static Func<bool> HudHeld = () => Interlocked.Read(ref hudHeldTicks) > Interlocked.Read(ref hudPolledTicks) && DateTime.UtcNow.Ticks - Interlocked.Read(ref hudHeldTicks) < TimeSpan.FromSeconds(HudActiveSeconds).Ticks;

    /// <summary>Whether a poll's query says its plugin draws the HUD: draw=0 does not, draw=1 does.</summary>
    static bool Draws(string? query) => !(query ?? "").TrimStart('?').Split('&').Contains($"{Hud.DrawQuery}=0");

    /// <summary>After every piece of work on the event loop, before the state is published (Program.cs): the menu's map list again when its
    /// version moved, so the plugin that reads a new version in the state fetches that version's list - never the one before it.</summary>
    static void RefreshCatalog()
    {
        if (catalogBuilt == CatalogVersion) return;
        CatalogText = BuildCatalog().Encode();
        catalogBuilt = CatalogVersion;
    }

    /// <summary>The menu's map list: every listed and official map with its size, the broken-list verdict, the live crash counter and
    /// the votes. Rebuilt on the event loop only when CatalogVersion moved (a vote, a crash), read by the HTTP thread.</summary>
    static volatile string CatalogText = new HudCatalog().Encode();
    static long CatalogVersion = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond, catalogBuilt; // starts new with every bridge, so a plugin always refetches after one restarts

    static HudCatalog BuildCatalog()
    {
        var c = new HudCatalog { Version = CatalogVersion };
        // Every mode's maps, one entry a map: its place in each mode that lists it (ModeSwitch.cs), Arms Race's first.
        var places = new Dictionary<string, (MapInfo First, int Ar, int Dm, int Wm)>();
        var order = new List<string>();
        void Place(MapInfo m, Modes.Mode mode)
        {
            var key = Key(m);
            if (!places.TryGetValue(key, out var at)) { at = (m, -1, -1, -1); order.Add(key); }
            var rank = m.Official ? 0 : m.Rank;
            places[key] = mode.Id switch
            {
                Modes.ArmsRaceId => at with { Ar = rank },
                Modes.DeathmatchId => at with { Dm = rank },
                _ => at with { Wm = rank },
            };
        }
        foreach (var m in Maps.Where(m => !IsPack(m) && !IsBlocked(m)).Concat(Officials)) Place(m, Modes.ArmsRace);
        foreach (var mode in Modes.All.Where(x => !x.ArmsRace))
            foreach (var m in PoolOf(mode).Where(m => !IsPack(m) && !IsBlocked(m))) Place(m, mode);
        foreach (var key in order)
        {
            var (m, ar, dm, wm) = places[key];
            var (up, down) = Tally(key);
            c.Maps.Add(new HudCatalog.Entry(key, ArgOf(m), m.Rank, m.Id, m.Name, m.SizeMb, m.Official, Broken.TryGetValue(key, out var b) ? b : S.PlayerCrashes.ContainsKey(key) ? 0 : -1,
                S.Crashes.GetValueOrDefault(key), up, down, IsRemoved(m), TooBig(m), SizeClass(m) ?? "", AreaOf(m) ?? 0, TooBigVotes(key), AdsVotes(key),
                IsOurs(m), ReasonOf(key), ar, dm, wm, TagsOf(m))); // Ours.cs, BrokenWhy.cs: fields 19 and 20; the three places 21 to 23; the tags 24 (Popularity.cs; an older plugin reads up to 18, 4.3 up to 20, 4.5 up to 23)
        }
        foreach (var (key, votes) in Votes)
            foreach (var (sid, value) in votes)
                if (AccountId(sid) is > 0 and var account)
                {
                    if (!c.Votes.TryGetValue(account, out var mine)) c.Votes[account] = mine = new();
                    mine[key] = value;
                }
        // What the server refuses outright and the menu does not list: a typed or pasted id gets the refusal on its card at once, before
        // anything is asked of the Workshop or downloaded. Command() refuses the same ids again if one ever reaches it.
        var listed = c.Maps.Select(m => m.Id).ToHashSet();
        foreach (var (id, verdict) in Removed)
            if (long.TryParse(id, out var n) && !listed.Contains(n)) c.Refused[n] = ("", $"{Workshop.Reason(verdict)} - Nobody can download it");
        foreach (var (id, name) in Packs)
            if (long.TryParse(id, out var n)) c.Refused[n] = (name, NotAMap);
        foreach (var (id, (name, _, reason)) in Blocked)
            if (long.TryParse(id, out var n)) c.Refused[n] = (name, reason.StartsWith("is on the blocked list") ? reason : $"{reason} - It is blocked");
        return c;
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
                : "Plays until its match ends - Or start a vote",
        };
        (h.Up, h.Down) = key == "" ? (0, 0) : Tally(key);
        if (key != "" && Votes.TryGetValue(key, out var votes))
            foreach (var (id, value) in votes) if (AccountId(id) is >= 0 and var account) h.MyVotes[account] = value;
        h.Top = TopRows().Take(HudState.ListLength).Select(r => (Clip(Label(r.Map), 36), r.Up, r.Down)).ToList();
        h.RoundMinutes = S.RoundMinutes;
        h.RoundBy = S.RoundMinutes == DefaultRoundMinutes ? "" : S.RoundBy ?? "";
        h.CatalogVersion = CatalogVersion;
        h.CheckMode = HudCheck.Name(HudCheckMode);
        h.TabMode = HudTab.Name(HudTabMode);
        h.Verified = HudVerifiedNow(now); // Program.cs: the HUD check, for the players on the server
        h.SearchVersion = Search.Version;
        h.DashVersion = DashVersion; // SyncDash.cs: the Sync page's block
        if (Enumerable.Reverse(S.History).FirstOrDefault(m => Key(m) != key) is { } prev) (h.PrevLabel, h.PrevArg) = (Clip(Label(prev), 40), ArgOf(prev));
        PutNotices(h, now);
        if (CrashNotice != "" && now < CrashNoticeUntil && !InMaintenance()) h.CrashNote = CrashNotice; // the welcome page's line
        PutWhy(h, now); // Likes.cs: the Dislike popups that are open
        PutRestart(h, now); // Restarts.cs: a restart coming, with its countdown
        if (BackgroundNow() is { } bg) (h.BgId, h.BgPercent, h.BgTitle) = (long.TryParse(bg.Id, out var bgId) ? bgId : 0, bg.Percent ?? -1, bg.Title); // RandomQueue.cs
        if (Pending is { } c)
        {
            h.ChangeMap = Clip(Label(c.Map), 40);
            h.ChangeKey = Key(c.Map);
            h.ChangeBy = c.By;
            h.ChangeIn = Math.Max(0, (int)Math.Ceiling((c.FiresAt - now).TotalSeconds));
            h.ChangeAtMs = HudState.Ms(c.FiresAt);
        }
        return h;
    }
}
