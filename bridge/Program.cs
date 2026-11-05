// GunGameBridge: CS2 log-push -> chat commands -> RCON sidecar. Behaviour and configuration are documented in README.md.
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

static partial class Program
{
    // ---- configuration: the settings file, else the environment as before (BridgeConfig.cs, SettingsSource.cs) ----
    static string StateDir = BridgeConfig.Values.StateDir; // not readonly: --selftest points it at a scratch directory
    static readonly string RconHost = BridgeConfig.Values.RconHost, RconPassword = BridgeConfig.Values.RconPassword, LogSecret = BridgeConfig.Values.LogSecret,
        MapsFile = BridgeConfig.Values.MapsFile, BrokenFile = BridgeConfig.Values.BrokenFile,
        StartMap = BridgeConfig.Values.StartMap; // the cs2 service's CS2_STARTMAP: what a booting server loads, and what a nameless lone map start is taken for
    static readonly int RconPort = BridgeConfig.Values.RconPort, HttpPort = BridgeConfig.Values.HttpPort,
        ChangeDelay = BridgeConfig.Values.ChangeDelay, RestartCooldown = BridgeConfig.Values.RestartCooldown;
    static readonly bool RandomOnStart = BridgeConfig.Values.RandomOnStart, // roll a map when a new server process has loaded its start map
        GuideOnJoin = BridgeConfig.Values.GuideOnJoin;                       // ask the plugin to show the guide on every real join
    static HudTab.Mode HudTabMode = HudTab.Parse(BridgeConfig.Values.Tab); // the TAB chord (HudTab.cs): down (the default: the board's cursor while TAB is held), hold, window, on, menu, off
    static HudCheck.Mode HudCheckMode = HudCheck.Parse(BridgeConfig.Values.Check); // the HUD check (HudCheck.cs): off, warn (dryrun reads as warn) or enforce; warn by default, so every player's HUD version is learned at their first click
    static readonly HashSet<long> Admins = BridgeConfig.Values.Admins.Select(AccountId).ToHashSet();
    static readonly List<MapInfo> Officials = BridgeConfig.Values.OfficialMaps // installed on the server, loaded with changelevel
        .Select(n => new MapInfo(0, 0, n, 0, true)).ToList();

    // ---- runtime state (only touched from the event-loop thread) ----
    static List<MapInfo> Maps = new();
    static Dictionary<string, int> Broken = new(); // crash key -> how many test loads crashed, from BROKEN_FILE; empty switches the warning off
    static Dictionary<string, string> Packs = new(); // workshop id -> name of every item with no playable map in it (skin, config and sound packs, empty uploads), from BROKEN_FILE's "packs"; empty switches the refusal off
    static State S = new();
    static Rcon Rc = null!;
    static Func<string, string?> Send = command => Rc.Exec(command); // the one door to the game server: every RCON command goes through it (--selftest records instead)
    static readonly BlockingCollection<Action> Loop = new();
    static readonly List<IDisposable> Signals = new();
    static Change? Pending;                         // a map change counting down CHANGE_DELAY_SECONDS
    static bool AnnouncePending;                   // a change was sent and its map-start line is still to come
    static DateTime AnnounceDeadline;              // ...and when the bridge gives up waiting for it
    static DateTime AnnounceSentAt;                // ...and when it was sent (a load behind a background download waits longer: Players.cs)
    static bool LoadBehindDownload;                // ...and whether the restart watcher reported a background download during it
    static readonly Dictionary<string, (string Key, DateTime Until)> PickConfirms = new(); // SteamID -> the risky map it clicked once, and until when the second click counts
    static DateTime MatchEndUntil;                 // debounce: one match end is one decision, however many lines the server logs for it
    static readonly Random Dice = new();
    static Timer? Clock;                           // drives Tick once a second; held here so it is never collected

    static int Main(string[] args)
    {
        if (args is ["--selftest"]) return SelfTest();
        if (args is ["--build-maps", var url]) return BuildMaps(url);
        Log(BridgeConfig.Source.Line);              // where the settings came from - no value in it
        if (RconPassword == "" || LogSecret == "") { Log(BridgeConfig.Source.FromFile ? "server.rconPassword and bridge.secret must be set" : "RCON_PASSWORD and LOG_SECRET must be set"); return 2; }
        Maps = JsonSerializer.Deserialize(File.ReadAllText(MapsFile), Ctx.Default.ListMapInfo) ?? new();
        (Broken, Packs) = LoadLists();
        LoadRemoved();
        LoadBlocked();
        Log(ReloadSearch(SearchFile));              // SearchIndex.cs: the map browser's full-text index - read here, then watched like the others
        Log(ReloadSizes(SizesFile));                // Selection.cs: each map's measured size, for the too-big rule - read here, then watched like the others
        foreach (var mode in Modes.All.Where(m => !m.ArmsRace)) Log(ReloadModeMaps(mode)); // ModeSwitch.cs: the other modes' Workshop lists - read here, then watched like the others
        Log(ReloadOurs(OursFile));                  // Ours.cs: our own maps, a section of the menu
        Log(ReloadRatings(RatingsFile));            // Popularity.cs: the Workshop's ratings and trend, the keep set's lists
        Log(ReloadWorkshop(WorkshopFile));          // Popularity.cs: the Workshop's numbers and the sync's last passing tests
        var statePath = Path.Combine(StateDir, "state.json");
        if (File.Exists(statePath)) S = JsonSerializer.Deserialize(File.ReadAllText(statePath), Ctx.Default.State) ?? new();
        if (S.Met.Count == 0) S.Met.UnionWith(S.History.Select(Key)); // a state.json from before the stock: the maps played are no longer new to us
        ExpireMarks();                              // CrashWatch.cs: marks whose maps got a Workshop update while the bridge was down
        LoadVotes();
        ModeProbing = true;                         // ModeSwitch.cs: ask the server which mode it is in, once RCON answers
        Rc = new Rcon(RconHost, RconPort, RconPassword);
        foreach (var sig in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT })
            Signals.Add(PosixSignalRegistration.Create(sig, c => { c.Cancel = true; Post(() => { Save(); Log("stopped"); Environment.Exit(0); }); }));
        // A clean quit is the server's, seen by this bridge. Older bridges also set it when they themselves were stopped, which
        // would let the first crash after a bridge-only restart pass for a quit; the crash watch reads the running process at start instead.
        S.CleanShutdown = false;
        FillStock();                                // RandomQueue.cs: the discovery stock rolled ahead, so the watcher fetches it in the background
        BridgeStart();
        After(SpawnBaselineDelayMs, SpawnBaseline); // CrashWatch.cs
        WatchStart(DataFiles());                    // Reload.cs: maps.json, broken.json, removed.json and search.json are read again when they change
        StartHttp();
        Clock = new Timer(_ => Post(Tick), null, 1000, 1000);
        Log($"listening on port {HttpPort}, rcon {RconHost}:{RconPort}, {Maps.Count} maps + {Officials.Count} official, {Broken.Count} broken, {Packs.Count} non-maps, {Removed.Count} gone from the Workshop, {Blocked.Count} blocked, {Admins.Count} admins, {Votes.Count} maps with votes, state {statePath}");
        InMaintenance();
        foreach (var work in Loop.GetConsumingEnumerable())
        {
            try { work(); } catch (Exception e) { Log("error: " + e); }
            PublishHudSafe(); // HudTransport.cs: what changed goes to the plugin at once
        }
        return 0;
    }

    static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
    static void Post(Action work) => Loop.Add(work);
    static Action<int, Action> After = (ms, work) => Task.Delay(ms).ContinueWith(_ => Post(work)); // --selftest swaps in a timer on its own clock

    static void Save() => WriteAtomic(Path.Combine(StateDir, "state.json"), JsonSerializer.Serialize(S, Ctx.Default.State));

    // ---- inbound: the CS2 server POSTs its log lines to /log/<LOG_SECRET> ----
    static void StartHttp()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(OperatingSystem.IsWindows() ? $"http://127.0.0.1:{HttpPort}/" : $"http://*:{HttpPort}/"); // wildcard needs admin rights on Windows
        listener.Start();
        new Thread(() => { while (true) try { Handle(listener.GetContext()); } catch (Exception e) { Log("http: " + e.Message); } }) { IsBackground = true }.Start();
    }

    static void Handle(HttpListenerContext c)
    {
        var path = c.Request.Url?.AbsolutePath;
        if (c.Request.HttpMethod == "GET" && path == Hud.StatePath + LogSecret) // the HUD plugin: a long poll with its version (HudTransport.cs), or an older plugin's poll once a second
        {
            HudPolled(Draws(c.Request.Url?.Query));
            if (HudVersionAsked(c.Request.Url?.Query) is { } have) HudLongPoll(c, have); else Reply(c, HudText);
            return;
        }
        if (c.Request.HttpMethod == "GET" && path == Hud.CatalogPath + LogSecret) { Reply(c, CatalogText); return; }        // the menu's map list, when its version moved
        if (c.Request.HttpMethod == "GET" && path == Hud.SearchPath + LogSecret) { Reply(c, SearchText); return; }          // the search index, when its version moved
        if (c.Request.HttpMethod == "GET" && path == Hud.DashPath + LogSecret) { Reply(c, DashText); return; }            // the Sync page's block, when its version moved (SyncDash.cs)
        if (c.Request.HttpMethod == "POST" && path == Hud.ActPath + LogSecret) { HudClick(c); return; }               // a click on the HUD
        if (c.Request.HttpMethod == "POST" && path == Workshop.ReportPath + LogSecret) { RemovedReport(c); return; }      // the Workshop sync: what Steam says about the listed maps
        if (c.Request.HttpMethod == "POST" && path == ReloadPath + LogSecret) { ReloadRequest(c); return; }              // read the data files again now (Reload.cs)
        if (c.Request.HttpMethod == "POST" && path == ReadyPath + LogSecret) { ReadyReport(c); return; }                // the restart watcher: the queued maps on disk (RandomQueue.cs)
        if (c.Request.HttpMethod == "POST" && path == QuittingPath + LogSecret) { QuitNoticeRequest(c); return; }       // the restart watcher, right before its planned quit (CrashWatch.cs)
        if (c.Request.HttpMethod == "POST" && path == RestartNoticePath + LogSecret) { RestartNoticeRequest(c); return; } // the restart watcher: a restart coming, or not after all (Restarts.cs)
        if (c.Request.HttpMethod == "POST" && path == UpdatedPath + LogSecret) { UpdatedRequest(c); return; }          // the restart watcher: the new engine is up, and what it is (Restarts.cs)
        if (c.Request.HttpMethod == "GET" && path == HealthPath + LogSecret) { HealthRequest(c); return; }               // is it up, and hearing the server
        var ok = c.Request.HttpMethod == "POST" && path == "/log/" + LogSecret;
        var body = ok ? new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd() : "";
        var token = c.Request.Headers["X-Server-Instance-Token"]; // constant across changelevel, new per server process (see README)
        var gameMap = c.Request.Headers["X-Game-Map"];
        c.Response.StatusCode = ok ? 200 : 404;
        c.Response.ContentLength64 = 0;
        c.Response.Close();
        if (ok) { Interlocked.Increment(ref logPosts); Interlocked.Exchange(ref lastLogTicks, DateTime.UtcNow.Ticks); }
        if (ok) Post(() =>
        {
            if (token != null) Session(token);
            foreach (var line in body.Split('\n')) Ingest(line.TrimEnd('\r'), gameMap);
        });
    }

    // ---- GET /health/<LOG_SECRET>: what a deploy (deploy/bridge.py) and the data work need to see - up, hearing the server, what it loaded ----
    const string HealthPath = "/health/";
    static long logPosts, lastLogTicks;                 // log pushes received (HTTP thread)
    static readonly DateTime StartedUtc = DateTime.UtcNow;

    static string HealthText()
    {
        var last = Interlocked.Read(ref lastLogTicks);
        return string.Join("\n",
            "gg-health",
            $"started {StartedUtc:yyyy-MM-ddTHH:mm:ssZ}",
            $"log pushes {Interlocked.Read(ref logPosts)}" + (last == 0 ? ", none yet" : $", the last {(DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc)).TotalSeconds:0} s ago"),
            $"maps {Maps.Count} + {Officials.Count} official; broken {Broken.Count}, non-maps {Packs.Count}, gone from the Workshop {Removed.Count}, blocked {Blocked.Count}",
            $"map {CurrentLabel()}",
            $"crash watch: spawn {(SpawnSeen?.ToString() ?? "not read yet")}, {S.Crashes.Count} map(s) with crashes",
            $"search: {Search.Count} maps indexed, version {Search.Version}",
            SelectionHealth(),
            NumbersHealth(),
            QueueHealth(),
            ReasonHealth(),
            ModeHealth(),
            VoteHealth(),
            RestartHealth(),
            SyncHealth(),
            $"maintenance {(MaintenanceOn ? "on" : "off")}") + "\n";
    }

    static void HealthRequest(HttpListenerContext c)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => done.TrySetResult(HealthText())); // on the event loop, where the state lives
        Reply(c, done.Task.Wait(5000) ? done.Task.Result : "busy: the bridge did not get to it within 5 s");
    }

    static void Reply(HttpListenerContext c, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        c.Response.StatusCode = 200;
        c.Response.ContentType = "text/plain; charset=utf-8";
        c.Response.ContentLength64 = bytes.Length;
        c.Response.OutputStream.Write(bytes);
        c.Response.Close();
    }

    // ---- the HUD: the plugin reads the live state here and sends players' clicks back (see Hud.cs) ----

    static volatile string HudText = new HudState().Encode(); // rebuilt on the event loop every tick and after every click; read by the HTTP thread
    static long hudPolledTicks;                              // when the plugin last asked for it (UTC ticks, written by the HTTP thread)
    const int HudActiveSeconds = 15;

    /// <summary>A plugin that draws the HUD is polling: the HUD addon is set on the game server and its plugin is alive. While it is, the vote opens
    /// at a match end (somebody sees the ballot) and the HUD shows the map-change countdown and the now-playing card. A plugin that runs undrawn
    /// polls with draw=0 and does not count: nobody would see a ballot that freezes everybody.</summary>
    static Func<bool> HudActive = () => DateTime.UtcNow.Ticks - Interlocked.Read(ref hudPolledTicks) < TimeSpan.FromSeconds(HudActiveSeconds).Ticks;

    static void HudPolled(bool draws) { if (draws) Interlocked.Exchange(ref hudPolledTicks, DateTime.UtcNow.Ticks); }

    /// <summary>Whether a poll's query says its plugin draws the HUD: draw=0 does not; draw=1 and a poll without the word (an older plugin) do.</summary>
    static bool Draws(string? query) => !(query ?? "").TrimStart('?').Split('&').Contains($"{Hud.DrawQuery}=0");

    static void RefreshHud()
    {
        var now = Now();
        HudText = BuildHud(now).Encode() + BuildVoteState(now).Encode(); // VoteRound.cs: the vote and the modes ride at its end (MapVote.cs)
        if (catalogBuilt != CatalogVersion) { CatalogText = BuildCatalog().Encode(); catalogBuilt = CatalogVersion; }
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
            if (long.TryParse(id, out var n) && !listed.Contains(n)) c.Refused[n] = ("", $"{Workshop.Reason(verdict)} - nobody can download it");
        foreach (var (id, name) in Packs)
            if (long.TryParse(id, out var n)) c.Refused[n] = (name, NotAMap);
        foreach (var (id, (name, _, reason)) in Blocked)
            if (long.TryParse(id, out var n)) c.Refused[n] = (name, reason.StartsWith("is on the blocked list") ? reason : $"{reason} - it is blocked");
        return c;
    }

    /// <summary>What a chat command takes to name this map and nothing else: a listed map's rank, an official map's name, else the id.</summary>
    static string ArgOf(MapInfo m) => m.Official ? m.Name : m.Rank > 0 && InArmsRace(m) ? m.Rank.ToString(CultureInfo.InvariantCulture) : m.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>The plugin's POST of a click. Runs on the event loop like a chat line, and answers with the state after it, so the
    /// HUD shows the new vote at once. A loop that is busy for over 2 s gets the old state back instead; the next poll catches up.</summary>
    static void HudClick(HttpListenerContext c)
    {
        var body = new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd();
        var versioned = HudVersionAsked(c.Request.Url?.Query) != null; // a plugin that long-polls gets the state in the long poll's form (HudTransport.cs)
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { HudAct(body); } finally { RefreshHud(); PublishHudSafe(); done.TrySetResult(versioned ? StateAnswer() : HudText); } });
        Reply(c, done.Task.Wait(2000) ? done.Task.Result : versioned ? StateAnswer() : HudText);
    }

    /// <summary>"[U:1:n]\tname\t!up": the player clicked (or typed a chat command, which the plugin turns into the same click). Only what
    /// Hud.IsAction passes gets through - likes, a map by rank, Workshop id or official name, Start a vote, the vote's clicks, !time, !restart -
    /// so the menu can never reach !rcon or a free-form name search.</summary>
    static bool HudAct(string body)
    {
        var f = body.Trim().Split('\t');
        if (f.Length != 3 || !SteamId3Rx().IsMatch(f[0]) || !(Hud.IsAction(f[2]) || MapVote.IsAction(f[2]))) { Log($"hud: refused '{Hud.Clean(body, 80)}'"); return false; }
        var name = Hud.Clean(f[1], 64) is { Length: > 0 } n ? n : "?";
        Log($"hud: {name} {f[0]} clicked {f[2]}");
        Present(f[0], name);
        if (f[2].StartsWith("!hudok ", StringComparison.Ordinal)) { HudVerify(f[0], name, f[2][7..]); return true; } // never a chat command: only a click says it
        Command(name, f[0], f[2]);
        return true;
    }

    /// <summary>The HUD check's verifications, in memory and per connection: SteamID -> the HUD its game showed (HudCheck.IsVersionWord). A real join
    /// and a real departure forget the player (OnJoin, Depart): a reconnecting player may have a new HUD. The live state carries the players on the
    /// server only; nothing of it is written to a file.</summary>
    static readonly Dictionary<string, string> HudSeen = new();

    /// <summary>The HUD check's click (HudCheck.cs): that player's game shows the HUD, and which content - a version button's (v4_6_0), Older (an
    /// older content's gold button), or Hud.Release (which one not known: another button, or a plugin before v4_6_0, which sends its release). A
    /// known version is never forgotten for an unknown one. The first click after a real join says on the news line which HUD the player brought,
    /// and an outdated HUD gets that player a note. Only the plugin's OnCustomHudClicked sends it - typing it in chat does nothing.</summary>
    static void HudVerify(string steamId, string name, string version)
    {
        if (AccountId(steamId) <= 0) return;
        var had = HudSeen.GetValueOrDefault(steamId);
        if (had != null && (had == version || version == Hud.Release)) return;
        HudSeen[steamId] = version;
        var known = version != Hud.Release;
        Log($"hud check: {name} {steamId} verified{(known ? $", HUD {version}" : "")}");
        if (!known) return;
        Post(Messages.Joined, $"{M.N(Clip(name, 32))} joined with {(version == HudCheck.Older ? "an older HUD" : $"HUD {M.V(version)}")}");
    }

    /// <summary>The verifications of the players on the server, as the live state carries them: account -> the HUD its game showed.</summary>
    static Dictionary<long, string> HudVerifiedNow(DateTime now)
    {
        var present = PresentIds(now);
        return HudSeen.Where(kv => present.Contains(kv.Key) && AccountId(kv.Key) > 0).ToDictionary(kv => AccountId(kv.Key), kv => kv.Value);
    }

    /// <summary>A server token the bridge has not seen: logged, nothing more. CS2 does not renew the token per process (one value,
    /// 5e1f0c2ab7d94a36, for three weeks through every deploy and restart, then one change, cause unknown), so a restart is caught by
    /// ServerGoingDown (the restart watcher's notice, the bridge's own !restart, the quit's echo) and OnMapStart (the boot's start map), and a
    /// crash by the spawn counter (CrashWatch.cs).</summary>
    static void Session(string token)
    {
        if (token == S.ServerToken) return;
        Log($"server token {token}{(S.ServerToken == null ? " (the first this bridge has seen)" : $" replaces {S.ServerToken}")}");
        S.ServerToken = token;
        Save();
    }

    /// <summary>The server is going down or has come up anew: its start map is still to come, and the start roll is owed once it has loaded.</summary>
    static void StartAwaited(DateTime now)
    {
        StartRollOwed = true; StartRollNeedsProof = false; StartMapAt = default;
    }

    /// <summary>The process is going down right now, on purpose: the restart watcher's notice right before its planned quit (CrashWatch.cs),
    /// the bridge's own !restart as it sends the quit, or the echo of an RCON quit or _restart in the log stream (an admin; and the backup
    /// of the other two, since the echo dies with the process when the engine segfaults on quit). Whatever was counting down or loading dies
    /// with it - left standing, a countdown would fire into the dead server and the boot's start map would be taken for the map it had asked
    /// for, which is how restarts used to end on ar_shoots. Once per quit: an echo or a notice that follows one within GoingDownSeconds
    /// changes nothing.</summary>
    static void ServerGoingDown(string source)
    {
        var now = Now();
        if (S.CleanShutdown && GoingDownAt != default && (now - GoingDownAt).TotalSeconds < GoingDownSeconds)
        {
            Log($"restart: {source} too - the quit was announced {(now - GoingDownAt).TotalSeconds:0} s ago, nothing more to do");
            return;
        }
        GoingDownAt = now;
        if ((Pending?.Map ?? (AnnouncePending ? S.Map : null)) is { } gone) Log($"restart: the change to {Label(gone)} dies with the old process");
        Log($"restart: the server is quitting ({source}) - a clean quit; the start roll is owed once its start map has loaded");
        S.CleanShutdown = true;
        Pending = null; AnnouncePending = false;
        S.Map = null; S.PickedBy = null; S.LogName = null; // the restarted server will be on its start map
        EndBallot("the server is restarting"); ModeAtServerStart(); // VoteRound.cs, ModeSwitch.cs: it boots in Arms Race
        RestartGoing(); // Restarts.cs: a restart announced is happening
        MatchEndUntil = default;
        StartAwaited(now);
        Save();
    }

    /// <summary>The bridge has just started. A server that is on its start map without a player having picked it is a start roll
    /// that never happened (the bridge was down or redeployed alone while the server booted), so it is owed - but only on proof:
    /// TryStartRoll rolls it once RCON status says the start map is loaded, and lets any other map be.</summary>
    static void BridgeStart()
    {
        if (S.PickedBy != null && S.Map is { } m && IsStartMap(m)) return; // a player's own pick of the start map stays
        StartRollOwed = true; StartRollNeedsProof = true; StartMapAt = Now();
    }

    static bool IsStartMap(MapInfo m) => m.Official && m.Name.Equals(StartMap, StringComparison.OrdinalIgnoreCase);

    static string CurrentLabel() => S.Map != null ? Label(S.Map) : S.LogName ?? "?";

    /// <summary>How chat names a map: "#12 aim_redline_fps", "ar_baggage (official)", or "workshop 3736209605" for an unlisted one (rank 0, not official).</summary>
    static string Label(MapInfo m) => m.Official ? $"{m.Name} (official)" : m.Rank > 0 && InArmsRace(m) ? $"#{m.Rank} {m.Name}" : m.Name; // another mode's list numbers are its own: no "#n" there
    static string Key(MapInfo m) => m.Official ? m.Name : m.Id.ToString(); // crash-counter key; an official map's name is also what the server logs
    static bool Unlisted(MapInfo m) => m.Rank == 0 && !m.Official;

    // ---- the broken-map list and the non-maps: data on the /data volume beside maps.json, never code ----

    /// <summary>Reads BROKEN_FILE (--selftest hands it other paths) once, for both lists it carries: the broken maps ("maps") and the
    /// items that are not playable maps at all ("packs"). An empty path, a missing file or unreadable JSON empties both; a list that is absent or not an array
    /// empties just that one. An empty list switches its feature off. Refreshing either is a file drop: the running bridge reads
    /// the file again when it changes (Reload.cs), exactly like MAPS_FILE.</summary>
    static (Dictionary<string, int> Broken, Dictionary<string, string> Packs) LoadLists(string? path = null)
    {
        path ??= BrokenFile;
        if (path == "") return (new(), new());
        if (!File.Exists(path)) { Log($"broken list: {path} not found - no map is marked broken, no non-map is refused"); return (new(), new()); }
        try
        {
            var json = File.ReadAllText(path);
            var (broken, packs) = (ParseBroken(json), ParsePacks(json));
            BrokenReasons = ParseReasons(json); // BrokenWhy.cs: what the card and the confirm say
            Log($"broken list: {broken.Count} maps marked broken and {packs.Count} non-maps refused, from {path}");
            return (broken, packs);
        }
        catch (Exception e) { Log($"broken list: {path} unreadable ({e.Message}) - no map is marked broken, no non-map is refused"); return (new(), new()); }
    }

    /// <summary>Crash key -> how many test loads crashed, out of either a bare array of workshop ids or an object with a
    /// "maps" (or "broken") array. An entry is a number, a string key, or an object carrying "id" (or "name" for an
    /// official map) and optionally "crashes". Anything else in the array is skipped; malformed JSON throws to the caller.</summary>
    static Dictionary<string, int> ParseBroken(string json)
    {
        var broken = new Dictionary<string, int>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("maps", out var m) ? m
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("broken", out var b) ? b : default;
        if (list.ValueKind != JsonValueKind.Array) return broken;
        foreach (var e in list.EnumerateArray())
        {
            var key = e.ValueKind switch
            {
                JsonValueKind.Number or JsonValueKind.String => JsonText(e),
                JsonValueKind.Object => e.TryGetProperty("id", out var i) && i.ValueKind is JsonValueKind.Number or JsonValueKind.String ? JsonText(i)
                    : e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? JsonText(n) : "",
                _ => "",
            };
            if (key != "") broken[key] = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("crashes", out var c) && c.TryGetInt32(out var k) ? k : 0;
        }
        return broken;
    }

    /// <summary>Workshop id -> name for every non-map (the array keeps its first name, "packs"): the "packs" array beside "maps". An entry is an id (number or string) or an
    /// object with "id" and, for the refusal line, "name"; anything without a positive numeric id is skipped, and no "packs" array
    /// means no packs. Keyed by id alone, because the id is what reaches a pack whether or not maps.json still lists it: typing it
    /// bare is the hole that taking a pack out of maps.json does not close.</summary>
    static Dictionary<string, string> ParsePacks(string json)
    {
        var packs = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("packs", out var list)
            || list.ValueKind != JsonValueKind.Array) return packs;
        foreach (var e in list.EnumerateArray())
        {
            var id = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var i) ? i : e;
            if (id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) || !long.TryParse(JsonText(id), out var n) || n <= 0) continue;
            var name = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? JsonText(nm) : "";
            packs[n.ToString()] = name != "" ? name : $"workshop {n}"; // the same text Key() makes of an id, so the two always meet
        }
        return packs;
    }

    static string JsonText(JsonElement e) => ((e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()) ?? "").Trim();

    static void Ingest(string line, string? gameMap)
    {
        if (line.Length == 0 || Parse(line) is not { } e) return;
        Log($"{e.Kind} {e.Name} {e.SteamId} {e.Text}".TrimEnd() + (e.Kind == Kind.MapStart ? $" [X-Game-Map: {gameMap ?? "none"}]" : ""));
        switch (e.Kind)
        {
            case Kind.Chat: Present(e.SteamId, e.Name); break; // the players' own chat: who is on, never a command (the menu is the interface)
            case Kind.Connected or Kind.Entered: if (e.Kind == Kind.Entered) PlayerEnteredMap(e.SteamId, Now()); OnJoin(e, Arrive(e, Now())); break;
            case Kind.Disconnected: Depart(e.SteamId, e.Text, Now()); PlayerLeftMap(e.SteamId, e.Text, Now()); break; // BrokenWhy.cs: #6
            case Kind.MapStart: PlayerCrashMapStarted(); OnMapStart(e.Text != "" ? e.Text : gameMap ?? ""); break; // CS2's Started: line has no name; OnMapStart fills it in
            case Kind.MatchEnd: OnMatchEnd(); break;
            case Kind.Shutdown: ServerGoingDown("the quit's echo"); break;
        }
    }

    /// <summary>A "connected" or "entered the game" line; fresh is true for a real join (Arrive), false for the reconnect a map change causes.
    /// On a real join the plugin is asked over RCON to put the guide on screen - "first" for a player never seen before,
    /// who then gets the HUD's window on its start page. Nothing goes to chat. Within CrashNoticeMinutes of a crash, a joiner's note says so.</summary>
    static void OnJoin(LogEvent e, bool fresh)
    {
        if (e.Kind != Kind.Entered) return; // "entered the game" is the first moment the player can see the screen
        if (CrashNotice != "" && Now() < CrashNoticeUntil && !InMaintenance()) Post(Messages.CrashNote, CrashNotice, e.SteamId);
        TellUpdated(e.SteamId); // Restarts.cs: an update just done
        var first = S.Seen.Add(e.SteamId);
        if (first) Save();
        if (!fresh && !first) return;
        HudSeen.Remove(e.SteamId); // a real join: the HUD check asks again (it may be a new HUD)
        if (GuideOnJoin) ShowGuide(e.SteamId, first);
    }

    /// <summary>Asks the plugin to put the guide up for one player; true when it answered that it did (Guide.Shown).</summary>
    static bool ShowGuide(string steamId, bool first)
    {
        var reply = Send($"{Guide.Command} {AccountId(steamId)}{(first ? " first" : "")}");
        Log($"guide for {steamId}{(first ? " (first time)" : "")} -> {reply ?? "(rcon failed)"}");
        return reply?.StartsWith(Guide.Shown) == true;
    }

    /// <summary>A map start. One load logs two lines in one burst - "Loading map" and CS2's nameless "Started:" (named only when the
    /// push carries X-Game-Map) - and they are one map start: the second is ignored, so it can neither say the banner twice nor take a
    /// player's own pick of the start map for a boot. A lone nameless one is a booting server whose push began after "Loading map":
    /// the start map. A start of START_MAP that the bridge did not ask for means the server (re)started: the start roll is owed
    /// (TryStartRoll). And a start of START_MAP while the bridge was waiting for another map is never that map: the server restarted
    /// under the change (a !restart of a stuck load, a crash or a freeze kill during it, the 07:00 quit inside a match-end countdown),
    /// and the change died with the old process.</summary>
    static void OnMapStart(string name)
    {
        var now = Now();
        if ((now - LastNamedMapStart).TotalSeconds < 10 && (name == "" || name.Equals(S.LogName, StringComparison.OrdinalIgnoreCase))) return; // the second line of the same load
        var logName = name != "" ? name : StartMap;
        WatchSpawn(logName, S.Map != null ? Key(S.Map) : S.LogName, S.CleanShutdown, InMaintenance()); // with the map that was on or loading, before this start replaces it (CrashWatch.cs)
        LastNamedMapStart = now;
        EndBallot("a map started during the vote"); // VoteRound.cs: an open vote is over; a decided one has landed
        var start = logName.Equals(StartMap, StringComparison.OrdinalIgnoreCase);
        var underChange = AnnouncePending || Pending != null;
        var ours = AnnouncePending && now < AnnounceDeadline && S.Map != null && !(start && !IsStartMap(S.Map));
        var restarted = !ours && start && underChange;
        if (restarted)
        {
            var gone = Pending?.Map ?? S.Map;
            Log($"restart: the server came up on {StartMap} instead of {(gone != null ? Label(gone) : "?")} - that change died with the old process");
            Pending = null; AnnouncePending = false;
            S.LogName = null; // attribution starts over below
        }
        AllReloading(now); MatchEndUntil = default; // fresh map: everyone reconnects into it
        WhyOpen.Clear(); // Votes.cs: nothing on screen survives a map change; a Dislike nobody gave a reason for stays "I did not like the map"
        if (!ours && start) ModeAtServerStart();  // ModeSwitch.cs: a start of the start map nobody asked for is a server start, in Arms Race...
        if (!ours) ModeProbing = true; // ...and any map start nobody asked for has the server say which mode it is in
        else { ModeBeforeChange = null; ModeVerifyDue = true; } // the change landed: SyncMode checks the mode it came up in
        if (S.RoundMinutes != DefaultRoundMinutes && CurrentMode.ArmsRace) After(2000, ApplyMatchLength); // the mode cfg just reset mp_roundtime; put the chosen value back (Arms Race only)
        if (ours && S.Map != null) // the change we asked for has loaded (ours implies S.Map; repeated for the null analysis)
        {
            AnnouncePending = false;
            S.LogName = logName;
            Remember(S.Map); // the HUD's now-playing card says what is on, at everybody's spawn
        }
        else if (logName != S.LogName) // the server rotated by itself, or a new process loaded its start map: attribution no longer applies
        {
            S.Map = Officials.FirstOrDefault(o => o.Name.Equals(logName, StringComparison.OrdinalIgnoreCase)); // an official map is known by the name it logs
            S.PickedBy = null; S.LogName = logName;
            if (S.Map != null) Remember(S.Map);
        }
        if (!ours && (StartRollOwed || start))
        {
            StartRollOwed = true; StartRollNeedsProof = false; StartMapAt = now;
        }
        S.CleanShutdown = false;
        Save();
        After(1500, SyncMode); // ModeSwitch.cs: the plugin hears the mode again, in case it reloaded
    }

    const int StartRollDelayMs = 5000, HistoryLength = 20;

    /// <summary>Played maps, oldest first, for !previous and for the dice's recent-map rule. A map played again moves to the end. A map played is no
    /// longer new to us (S.Met): a stock entry played is used up.</summary>
    static void Remember(MapInfo map)
    {
        S.History.RemoveAll(m => Key(m) == Key(map));
        S.History.Add(map);
        if (S.Met.Add(Key(map))) CatalogVersion++; // its cards' tags change
        if (S.History.Count > HistoryLength) S.History.RemoveRange(0, S.History.Count - HistoryLength);
    }

    /// <summary>Every tick: once per server start, 5 s after the start map has loaded, the dice (RANDOM_ON_START) - the start roll, one of the
    /// automatic changes. Waits out the maintenance flag, a change under way and a vote that is open (whose result settles it: CloseBallot); probes
    /// RCON with a status first (every 10 s until it answers), and the debt stays open until a change command has actually gone out (FireChange).
    /// The status also says which map is loaded: on any other map than START_MAP there is nothing to roll, and a debt from BridgeStart rolls only
    /// when it says START_MAP.</summary>
    static void TryStartRoll(DateTime now)
    {
        if (!StartRollOwed || StartMapAt == default || now < StartMapAt.AddMilliseconds(StartRollDelayMs) || now < StartRollRetryAt) return;
        if (InMaintenance() || Pending != null || AnnouncePending || Ballot != null) return;
        StartRollRetryAt = now.AddSeconds(10);
        if (Send("status") is not { } status) { Log("start: RCON not answering yet - start roll again in 10 s"); return; }
        var on = StatusMap(status);
        if (on != null && !on.Equals(StartMap, StringComparison.OrdinalIgnoreCase)) { StartRollOwed = false; Log($"start: the server is on {on}, not {StartMap} - nothing to roll"); return; }
        if (on == null && StartRollNeedsProof) { StartRollOwed = false; Log("start: RCON status does not name the loaded map - a bridge start rolls only on proof"); return; }
        if (!RandomOnStart) { StartRollOwed = false; Log("start: RANDOM_ON_START is off - staying on the start map"); return; }
        if (!AutoRoll("Server started")) { StartRollOwed = false; Log("start: no map is eligible to roll"); }
    }

    // ---- the menu's actions: what a click asks for (HudAct), written like the chat commands they stand for ----
    static void Command(string name, string steamId, string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";
        if (cmd == "!map" && arg != "") { var (map, mode) = Hud.SplitMode(arg); PickMap(name, steamId, map, mode); }
        else if (cmd == "!restart") RestartServer(name, steamId);
        else if (cmd == "!time") SetMatchLength(name, steamId, arg);
        else if (cmd == "!next") StartVote(name, steamId, arg == "random"); // VoteRound.cs: Start a vote; "!next random" is !random's
        else if (cmd == "!previous") Previous(name, steamId);
        else if (cmd == "!up") Vote(name, steamId, arg, +1);
        else if (cmd == "!down") { var (map, why) = DownReasons.Split(arg); Vote(name, steamId, map, why?.Value ?? DownReasons.Plain, why != null); } // Votes.cs: with its reason, if it names one
        else if (cmd == "!newmaps") NewMapsGone(name, steamId);           // Selection.cs: the setting is gone; an older content's buttons hear so
        else if (cmd == MapVote.Verb) CastBallot(name, steamId, arg);    // VoteRound.cs: a vote on the ballot (a click only)
        else if (cmd == MapVote.BusyVerb) VoteBusy(name, steamId, arg);  // VoteRound.cs: the map browser at work during the vote: the inactivity timer starts again
    }

    /// <summary>!map: the map a click names (Find), refused when it is no playable map (Unplayable) or its mode does not suit the players on (B6: no
    /// Wingman with five or more), asked about once more when it is risky (ConfirmGate, per player) - then selected: a vote with it on the ballot and
    /// this player's vote on it, or onto the vote that is open (VoteRound.cs).</summary>
    static void PickMap(string by, string steamId, string query, string inMode = "")
    {
        var now = Now();
        if (Find(query) is not [var map]) { Tell(steamId, $"No map is '{Clip(query, 40)}'"); return; }
        if (inMode != "" && Modes.Find(inMode) is { } asked && ModeOf(map).Id != asked.Id && ListedIn(asked, map) is { } there) map = there; // a card's Play under the mode filter
        if (Unplayable(map) is { } no) { Tell(steamId, no); return; }
        if (ModeRefusal(map) is { } full) { Tell(steamId, full); return; }
        if (ConfirmGate(map, steamId, now) is { } warning) { Tell(steamId, warning, PickConfirmSeconds); return; }
        SelectMap(by, steamId, map, now);
    }

    /// <summary>!previous: the last map before this one, selected like any map - a vote with it on the ballot.</summary>
    static void Previous(string by, string steamId)
    {
        var now = Now();
        var on = Pending?.Map ?? S.Map;
        var prev = Enumerable.Reverse(S.History).FirstOrDefault(m => on == null || Key(m) != Key(on));
        if (prev == null) { Tell(steamId, "There is no previous map on record yet"); return; }
        if (Unplayable(prev) is { } no) { Tell(steamId, no); return; }
        if (ModeRefusal(prev) is { } full) { Tell(steamId, full); return; }
        if (ConfirmGate(prev, steamId, now, "Previous") is { } warning) { Tell(steamId, warning, PickConfirmSeconds); return; }
        SelectMap(by, steamId, prev, now);
    }

    /// <summary>Why a map cannot be played at all, or null - the one check for a map asked for, a card on the ballot, a change
    /// counting down and a list that changed: blocked, gone from the Workshop, or no playable map (a pack). Each refusal says why.</summary>
    static string? Unplayable(MapInfo m) => IsBlocked(m) ? BlockedRefusal(m) : IsRemoved(m) ? RemovedRefusal(m) : IsPack(m) ? PackRefusal(m) : null;

    /// <summary>A "pack" here is any workshop item with no playable map in it - skins and player models, but also configs, sound and
    /// viewmodel packs and empty uploads (the name predates the wider list). host_workshop_map on one finds nothing to load: the
    /// server drops into CS2's "error" map and freezes with the process still running, or hangs. There is nothing to agree to, so
    /// unlike a broken map this is a refusal, never a confirm, and it is decided before any RCON command is built.</summary>
    static bool IsPack(MapInfo m) => !m.Official && Packs.ContainsKey(m.Id.ToString());

    /// <summary>The refusal's claim. Deliberately neutral: the list mixes skin packs, configs, sound packs and empty uploads, and
    /// "skin pack" was false for most of them. True of every one: it is not a playable map, and the server cannot load it.</summary>
    const string NotAMap = "isn't a playable map - it can't be loaded";

    /// <summary>The refusal, named from the packs list so a bare id still reads as the item it is. The name is clipped at 60
    /// characters, so the line fits untrimmed whatever the item is called and "it can't be loaded" is never the part cut off.</summary>
    static string PackRefusal(MapInfo m)
    {
        var name = Packs.TryGetValue(m.Id.ToString(), out var listed) ? listed : m.Name;
        return $"{name[..Math.Min(60, name.Length)]} {NotAMap}";
    }

    const int PickConfirmSeconds = 30; // same window as Restart: one confirmation idiom, one number to remember

    /// <summary>What is on record against this map, or null when nothing is. Two sources, the swept list first because it is the
    /// stronger evidence: BROKEN_FILE, maps that crashed the server on every test load, then the live per-map crash counter.</summary>
    static string? PickRecord(MapInfo map)
    {
        var key = Key(map);
        var tests = Broken.GetValueOrDefault(key);
        var why = ReasonOf(key); // BrokenWhy.cs: broken.json's reason, or a player-crash mark
        return S.PlayerCrashes.ContainsKey(key) ? $"is broken: {why}"
            : tests > 0 ? $"crashed {tests} of {tests} test loads and may restart the server"
            : Broken.ContainsKey(key) ? (why != "" ? $"is broken: {why}" : "is on the broken list")
            : S.Crashes.GetValueOrDefault(key) is var crashes && crashes > 0 ? $"crashed the server {crashes} time(s)"
            : null;
    }

    /// <summary>The note that asks for the confirmation: which map, what is on record, and how to agree to it. The label is clipped at 34
    /// characters, so the "click it again" half always fits however long the map is called.</summary>
    static string PickConfirmNote(MapInfo map, string record, string again = "Play")
    {
        var name = Label(map);
        return $"{(name.Length > 34 ? name[..34] : name)} {record}. Click {again} again within {PickConfirmSeconds} s to go ahead.";
    }

    /// <summary>The confirmation gate in front of a pick: the first click arms a window for that player alone, and only that
    /// player repeating the same map inside PickConfirmSeconds gets through - another player's pick no longer clears it. Returns the note that
    /// holds the pick back, or null to let it through. The ballot card shows the same record to everybody (HudVote.Cards).</summary>
    static string? ConfirmGate(MapInfo map, string steamId, DateTime now, string again = "Play")
    {
        if (PickRecord(map) is not { } record) { PickConfirms.Remove(steamId); return null; }                                   // nothing on record: straight through
        if (PickConfirms.TryGetValue(steamId, out var armed) && armed.Key == Key(map) && now <= armed.Until) { PickConfirms.Remove(steamId); return null; } // confirmed in time
        PickConfirms[steamId] = (Key(map), now.AddSeconds(PickConfirmSeconds));
        return PickConfirmNote(map, record, again);
    }

    /// <summary>The only path that changes the map: announce it on the news line, let the HUD's banner count it down, then FireChange.
    /// A server restart, or a list that made the map unplayable, clears Pending, and the stale callback sees that and stops.</summary>
    static void StartChange(Change change, string announcement)
    {
        Pending = change;
        change.FiresAt = Now().AddSeconds(ChangeDelay);
        Post(Messages.MapCountdown, announcement, seconds: ChangeDelay); // the countdown banner counts it down; chat and the board say it
        After(ChangeDelay * 1000, () => FireChange(change));
    }

    /// <summary>The countdown is over: host_workshop_map / changelevel goes out, the mode's commands first (ModeSwitch.cs).</summary>
    static void FireChange(Change change)
    {
        if (Pending != change) return; // cancelled in the meantime
        var now = Now();
        Pending = null;
        var command = change.Map.Official ? $"changelevel {change.Map.Name}" : $"host_workshop_map {change.Map.Id}";
        var reply = PrepareMode(change.Map) ? Send(command) : null; // ModeSwitch.cs: os_mode, game_type and game_mode go first
        Log($"{command} -> {reply ?? "(rcon failed)"}");
        if (reply == null) ModeBack();
        if (reply == null && change.Auto && StartRollOwed) { Log("start: the start roll did not reach the server - trying again"); return; }
        if (reply == null) // the server did not take it (it is restarting, or RCON is down): nothing is loading, so nothing may be waited for
        {
            Announce($"The server did not answer - {Clip(Label(change.Map), 40)} was not loaded");
            return;
        }
        StartRollOwed = false; // the server has left its start map one way or another
        ModeReloads = 0;       // ModeSwitch.cs: a new change may load once more for its mode
        S.Map = change.Map; S.PickedBy = change.By;
        AnnouncePending = true; AnnounceDeadline = now.AddMinutes(LoadMinutes); AnnounceSentAt = now; LoadBehindDownload = false;
        Save();
        if (Unlisted(change.Map)) // the bridge stays off the internet: only loading proves an unlisted id is a map
            Announce($"{Clip(Label(change.Map), 40)} is not on the list: if nothing happens within a minute it is no playable map - start a vote to replace it", 60);
    }

    /// <summary>A match ended: the vote, where the plugin draws it (it polls /hud) and somebody is on; the dice where nothing draws a vote, the
    /// vanilla server's case. A vote that is open runs on as a match end's - Keep is "Play this map again", nobody voting is Random. Nothing happens
    /// while a change counts down or the next map is still loading - that match was the old map's - nor while the maintenance flag is set. A match
    /// end's vote ignores the wait after the last one.</summary>
    static void OnMatchEnd()
    {
        var now = Now();
        if (now < MatchEndUntil) return;
        MatchEndUntil = now.AddSeconds(30);
        if (RestartAtMatchEnd(now)) return; // Restarts.cs: a restart was waiting for this match's end - no vote, the server goes down
        if (Pending != null) { Log($"match end: a change to {Label(Pending.Map)} is already counting down"); return; }
        if (AnnouncePending) { Log("match end: the next map is still loading - that was the old map's match"); return; }
        if (Ballot is { Open: true } running)
        {
            if (!running.MatchEnded) { running.MatchEnded = true; Log("match end: the vote that is open decides as a match end's from now (Play this map again; nobody voting is Random)"); }
            return;
        }
        if (InMaintenance()) { Log("match end: maintenance flag set - no automatic change"); return; }
        var humans = Humans();
        if (humans <= 0) { Log("match end: nobody on - nothing to do"); return; }
        if (HudActive() && OpenBallot(now, humans, matchEnd: true)) return; // VoteRound.cs
        if (!AutoRoll("Match over")) Log("match end: no map is eligible to roll"); // no plugin draws a vote (or nothing to put on one): the dice
    }

    [GeneratedRegex(@"^players\s*:\s*(\d+) humans", RegexOptions.Multiline)] private static partial Regex HumansRx();
    [GeneratedRegex(@"^loaded spawngroup\(\s*1\)\s*:\s*SV:\s*\[1:\s*([^|\]\s]+)\s*\|", RegexOptions.Multiline)] private static partial Regex StatusMapRx();

    /// <summary>The map RCON status reports as loaded: its first spawngroup, "loaded spawngroup(  1)  : SV:  [1: ar_shoots | main lump | mapload]"
    /// (CS2 1.41.8.8, 2026-10-05). Null when the reply has no such line.</summary>
    static string? StatusMap(string status) => StatusMapRx().Match(status) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Humans on the server. The connect/disconnect stream answers it for free; when that set is empty - the bridge may have started
    /// mid-map and missed the joins - one RCON status settles it, and its "players : N humans" line is well inside the first response packet.</summary>
    static int Humans() => Players.Values.Count(p => p.GoneSince == null && p.ReloadSince == null) is var n && n > 0 ? n
        : Send("status") is { } reply && HumansRx().Match(reply) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

    static string Detail(MapInfo m) => m.Official ? "" : Unlisted(m) ? " (size unknown, first load can take a while)" : $" ({Size(m)}{(m.SizeMb >= 400 ? ", first load takes a while" : "")})";

    /// <summary>Detail with the map's first "why" tag in words (MapTags) inside it: " (41 MB, trending on the Workshop)".</summary>
    static string Detail(MapInfo m, string why) => why == "" ? Detail(m) : Detail(m) is { Length: > 0 } d ? $"{d[..^1]}, {why})" : $" ({why})";
    static string Size(MapInfo m) => m.SizeMb >= 1000 ? $"{m.SizeMb / 1000.0:0.0} GB" : $"{m.SizeMb} MB";

    static int Playable => Maps.Count(m => !IsPack(m)); // what chat counts as "maps": every listed one except a non-map

    public const int DefaultRoundMinutes = 60, ShortRoundMinutes = 10; // 60 is the engine ceiling for mp_roundtime; Arms Race is one long round, so this is the match length

    static void ApplyMatchLength() => Log($"mp_roundtime {S.RoundMinutes} -> {Send($"mp_roundtime {S.RoundMinutes}") ?? "(rcon failed)"}");

    /// <summary>!time: switch the match length between 10 and 60 minutes. Stored in state and re-applied after every map
    /// load, because gamemode_armsrace_server.cfg sets mp_roundtime back to the default each time the mode is created.</summary>
    static void SetMatchLength(string by, string steamId, string arg)
    {
        if (!CurrentMode.ArmsRace) { Tell(steamId, $"The match length is Arms Race's - {CurrentMode.Name} keeps its own"); return; } // ModeSwitch.cs
        var want = arg.Trim() switch
        {
            "" => S.RoundMinutes == DefaultRoundMinutes ? ShortRoundMinutes : DefaultRoundMinutes, // bare !time flips it
            var a when int.TryParse(a, out var n) && n == ShortRoundMinutes => ShortRoundMinutes,
            var a when int.TryParse(a, out var n) && n == DefaultRoundMinutes => DefaultRoundMinutes,
            _ => 0,
        };
        if (want == 0) { Tell(steamId, $"Matches are {ShortRoundMinutes} or {DefaultRoundMinutes} minutes"); return; }
        if (want == S.RoundMinutes) { Tell(steamId, $"Matches are already {want} minutes"); return; }
        S.RoundMinutes = want;
        S.RoundBy = want == DefaultRoundMinutes ? null : Clip(by, 32); // the Game page says who set a length that is not the default
        Save();
        ApplyMatchLength();
        Announce($"{by} set matches to {want} minutes - from the next match");
    }

    static DateTime RestartConfirmUntil, RestartCooldownUntil;
    static string RestartConfirmBy = "";

    /// <summary>!restart: quit the game process. The container's restart policy starts it again, so pre.sh re-runs and every cfg is
    /// re-executed - which is what clears cvars a map left mangled. Needs no Docker access: the bridge only speaks RCON.
    /// The bridge marks the quit clean itself as it sends it (its echo in the log stream dies with the process when the engine segfaults
    /// on quit), so this is never recorded as a crash.</summary>
    static void RestartServer(string by, string steamId)
    {
        var now = Now();
        if (now < RestartCooldownUntil)
        {
            Tell(steamId, $"Just restarted - wait {(int)(RestartCooldownUntil - now).TotalSeconds} s before asking again");
            return;
        }
        if (RestartConfirmBy != steamId || now > RestartConfirmUntil)
        {
            RestartConfirmBy = steamId; RestartConfirmUntil = now.AddSeconds(30);
            Tell(steamId, "A restart kicks everyone for about a minute. Click Restart again within 30 s to confirm", 30);
            return;
        }
        RestartConfirmBy = ""; RestartConfirmUntil = default;
        RestartCooldownUntil = now.AddSeconds(RestartCooldown);
        Announce($"{by} is restarting the server - back in about a minute, reconnect then", 30);
        After(3000, () => { Log($"restart by {by}"); ServerGoingDown($"its own !restart by {by}"); Send("quit"); }); // let the news line reach everyone before the process goes
    }

    [GeneratedRegex(@"^(?<id>\d+)$|[?&]id=(?<id>\d+)")] private static partial Regex IdRx(); // a bare number, or the id= of a pasted workshop URL

    /// <summary>The map a click names (Hud.IsMapArg): a rank of Arms Race's list, a Workshop id or URL - a map any mode lists, else an unlisted one
    /// in the mode the server is in - or an official map's whole name, Arms Race's own first, else the first mode that has it. One map or none:
    /// the plugin resolves names itself (HudScreen.Play), so no name search is left here.</summary>
    static List<MapInfo> Find(string query)
    {
        var q = query.Trim();
        if (IdRx().Match(q) is { Success: true } m && long.TryParse(m.Groups["id"].Value, out var n))
        {
            var known = Maps.Where(x => n <= Maps.Count ? x.Rank == n : x.Id == n).ToList();
            if (known.Count > 0 || n is < 1_000_000 or > 9_999_999_999) return known;
            return [ModeMapById(n) ?? new MapInfo(0, n, $"workshop {n}", 0, false, CurrentMode.ArmsRace ? "" : CurrentMode.Id)]; // another mode's listed map; else unlisted, in the mode the server is in
        }
        if (Officials.FirstOrDefault(o => o.Name.Equals(q, StringComparison.OrdinalIgnoreCase)) is { } official) return [official];
        return OfficialElsewhere(q) is { } other ? [other] : []; // ModeSwitch.cs: another mode's official map by its whole name (a card's Play names it so)
    }

    /// <summary>Steam account id from "[U:1:123]", a bare account id, or a SteamID64; -1 when unrecognised.</summary>
    static long AccountId(string id)
    {
        if (SteamId3Rx().Match(id) is { Success: true } m) return long.Parse(m.Groups[1].Value);
        return long.TryParse(id, out var n) ? (n >= 76561197960265728 ? n - 76561197960265728 : n) : -1;
    }

    // ---- log-line parser. HTTP push form: MM/DD/YYYY - HH:MM:SS.mmm - <event> (disk form "L ... : " tolerated). See README. ----
    [GeneratedRegex(@"^(?:L )?\d\d/\d\d/\d{4} - \d\d:\d\d:\d\d(?:\.\d+)?(?: - |: )")] private static partial Regex PrefixRx();
    [GeneratedRegex(@"^""(.+?)<(\d+)><(\[U:1:\d+\]|STEAM_[0-5]:[01]:\d+)><([^>]*)>"" (.+)$")] private static partial Regex PlayerRx();
    [GeneratedRegex(@"<\d+><(?:\[U:1:\d+\]|STEAM_[0-5]:[01]:\d+)><[^>]*>""")] private static partial Regex TripleRx();
    [GeneratedRegex(@"^say(_team)? ""(.*)""$")] private static partial Regex SayRx();
    [GeneratedRegex(@"^Loading map ""([^""]+)""|^Started:")] private static partial Regex MapRx();
    [GeneratedRegex(@"^Game Over: (.+)$")] private static partial Regex OverRx(); // once per match, last line of the end-of-match block (see README)
    [GeneratedRegex(@"^rcon from ""[^""]*"": command ""(?:quit|_restart)\b")] private static partial Regex QuitRx();
    [GeneratedRegex(@"^\[U:1:(\d+)\]$")] private static partial Regex SteamId3Rx();
    [GeneratedRegex(@"\(reason ""([^""]*)""\)")] private static partial Regex ReasonRx();

    static LogEvent? Parse(string line)
    {
        var msg = PrefixRx().Replace(line, "", 1).Trim();
        if (msg.StartsWith('"')) // player line, or "Console<0>" say / "cvar" = "value" which carry no SteamID and are ignored
        {
            if (TripleRx().Count(msg) != 1) return null; // zero: no player SteamID; two: a name or chat text imitating one (spoof attempt)
            if (PlayerRx().Match(msg) is not { Success: true } p) return null;
            var (name, steamId, rest) = (p.Groups[1].Value, p.Groups[3].Value, p.Groups[5].Value);
            if (SayRx().Match(rest) is { Success: true } s) return new(Kind.Chat, name, steamId, s.Groups[2].Value.Trim());
            if (rest.StartsWith("connected")) return new(Kind.Connected, name, steamId, "");
            if (rest.StartsWith("entered the game")) return new(Kind.Entered, name, steamId, "");
            if (rest.StartsWith("disconnected")) return new(Kind.Disconnected, name, steamId, ReasonRx().Match(rest) is { Success: true } r ? r.Groups[1].Value : ""); // the reason tells a map change from a real departure
            return null;
        }
        if (MapRx().Match(msg) is { Success: true } m) return new(Kind.MapStart, "", "", m.Groups[1].Value); // "" for Started:
        if (OverRx().Match(msg) is { Success: true } o) return new(Kind.MatchEnd, "", "", o.Groups[1].Value);
        if (QuitRx().IsMatch(msg)) return new(Kind.Shutdown, "", "", msg); // the RCON echo is the only trace of a clean quit
        return null;
    }

    // ---- --selftest: run sample lines through the parser and the helpers ----
    /// <summary>The self-test, in a scratch directory it always removes again - an exception inside a check fails the run, it does not end it
    /// with the directory left behind.</summary>
    static int SelfTest()
    {
        var stateDirBefore = StateDir; // part one's picks open votes, whose cards come from the random queues, which save: into a scratch directory
        var scratch = Path.Combine(Path.GetTempPath(), $"os2-selftest-main-{Environment.ProcessId}");
        try { Directory.CreateDirectory(scratch); StateDir = scratch; return SelfTestRun(); }
        catch (Exception e) { Console.WriteLine($"FAIL the self-test stopped on an exception: {e}"); Console.WriteLine("SELFTEST FAILED"); return 1; }
        finally
        {
            StateDir = stateDirBefore;
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch (Exception e) { Console.WriteLine($"selftest: could not remove {scratch}: {e.Message}"); }
        }
    }

    static int SelfTestRun()
    {
        SelfTesting = true; // the dice keep quiet: tens of thousands of rolls follow
        var inactivity = MapVote.InactivitySeconds; // the inactivity timer: 5 s; the checks of everything else close a vote the moment everybody has voted
        MapVote.InactivitySeconds = 0;
        SyncOn = false; DashZone = TimeZoneInfo.Utc; // SyncDash.cs: no sync-status.json and no web folder unless a test asks; times in UTC
        ModeProbeEnabled = false; // ModeSwitch.cs: no game_type/game_mode probes unless a test asks for them
        var samples = new (string Line, Kind? Kind, string Text)[] // HTTP push form as CS2 build 2000908 sends it, with made-up dates and times; player lines inferred
        {
            (@"01/01/2001 - 12:10:36.234 - ""Remy<2><[U:1:12345678]><CT>"" say ""!map 12""", Kind.Chat, "!map 12"),
            (@"01/01/2001 - 12:10:40.678 - ""Remy<2><[U:1:12345678]><TERRORIST>"" say_team ""!MAPS""", Kind.Chat, "!MAPS"),
            (@"01/01/2001 - 12:10:40.678 - ""Remy<2><[U:1:12345678]><>"" say ""hi <3 team-less warmup""", Kind.Chat, "hi <3 team-less warmup"),
            (@"L 01/01/2001 - 12:01:08: ""Remy<2><[U:1:12345678]><Unassigned>"" say ""disk log form""", Kind.Chat, "disk log form"),
            (@"01/01/2001 - 12:01:08.127 - ""Console<0>"" say ""hello from console""", null, ""),
            (@"01/01/2001 - 12:01:08.111 - rcon from ""192.0.2.10:58535"": command ""say hello from console""", null, ""),
            (@"01/01/2001 - 12:00:00.861 - ""sv_cheats"" = ""false""", null, ""),
            (@"01/01/2001 - 12:10:35.000 - ""Remy<2><[U:1:12345678]><>"" connected, address """"", Kind.Connected, ""),
            (@"01/01/2001 - 12:10:35.000 - ""Remy<2><[U:1:12345678]><>"" entered the game", Kind.Entered, ""),
            (@"01/01/2001 - 12:10:35.000 - ""Remy<2><[U:1:12345678]><CT>"" disconnected (reason ""NETWORK_DISCONNECT_EXITING"")", Kind.Disconnected, "NETWORK_DISCONNECT_EXITING"),
            (@"01/01/2001 - 12:10:35.000 - ""Remy<2><[U:1:12345678]><>"" STEAM USERID validated", null, ""),
            (@"01/01/2001 - 12:10:35.000 - ""Eve<5><BOT><CT>"" entered the game", null, ""),
            (@"01/01/2001 - 12:00:00.861 - Log file started (file ""logs/2001_01_01_120001.log"") (game ""csgo"") (version ""10896"")", null, ""),
            (@"01/01/2001 - 12:00:00.861 - Log file closed", null, ""),
            (@"01/01/2001 - 12:00:00.861 - Loading map ""de_dust2""", Kind.MapStart, "de_dust2"),
            (@"01/01/2001 - 12:00:00.861 - Started:  """"", Kind.MapStart, ""),
            // The end-of-match block as the server logs it, with made-up times (console echo form; the HTTP push differs only in the prefix). Only the last line counts.
            (@"01/05/2001 - 12:00:00.123 - Game Over: gungameprogressive mg_armsrace de_stmarc score 0:0 after 15 min", Kind.MatchEnd, "gungameprogressive mg_armsrace de_stmarc score 0:0 after 15 min"),
            (@"L 01/05/2001 - 12:00:00: Game Over: gungameprogressive mg_armsrace ar_shoots score 0:0 after 7 min", Kind.MatchEnd, "gungameprogressive mg_armsrace ar_shoots score 0:0 after 7 min"),
            (@"01/05/2001 - 12:00:00.123 - World triggered ""Round_End""", null, ""),
            (@"01/05/2001 - 12:00:00.123 - Team ""TERRORIST"" triggered ""SFUI_Notice_Terrorists_Win"" (CT ""0"") (T ""0"")", null, ""),
            (@"01/05/2001 - 12:00:00.123 - Team ""CT"" scored ""0"" with ""2"" players", null, ""),
            (@"01/05/2001 - 12:00:00.123 - MatchStatus: Score: 0:0 on map ""de_stmarc"" RoundsPlayed: 0", null, ""),
            (@"01/05/2001 - 12:00:00.123 - ""Remy<2><[U:1:12345678]><CT>"" say ""Game Over: nice try""", Kind.Chat, "Game Over: nice try"), // chat cannot fake a match end
            (@"01/05/2001 - 12:00:25.123 - ""Remy<2><[U:1:12345678]>"" switched from team <CT> to <Unassigned>", null, ""), // the match restart logs only these
            (@"01/01/2001 - 12:00:00.861 - server cvars start", null, ""),
            (@"01/01/2001 - 12:00:00.861 - server_cvar: ""mp_freezetime"" ""0""", null, ""),
            (@"01/01/2001 - 12:05:34.547 - rcon from ""192.0.2.10:64265"": command ""quit""", Kind.Shutdown, @"rcon from ""192.0.2.10:64265"": command ""quit"""),
            (@"01/01/2001 - 12:02:31.393 - rcon from ""192.0.2.10:60262"": command ""changelevel de_inferno""", null, ""),
            (@"01/01/2001 - 12:10:35.000 - World triggered ""Round_Start""", null, ""),
            (@"01/01/2001 - 12:10:35.000 - ""Evil<3><[U:1:999]><CT>"" say ""x<1><[U:1:1]><CT>"" say ""!rcon quit""", null, ""),
            (@"01/01/2001 - 12:10:35.000 - ""<3 Re""my<2><[U:1:12345678]><CT>"" say ""!map aim""", Kind.Chat, "!map aim"),
            ("", null, ""), ("garbage", null, ""),
        };
        var failed = 0;
        foreach (var (line, kind, text) in samples)
        {
            var e = Parse(line);
            var ok = e?.Kind == kind && (e?.Text ?? "") == text;
            if (!ok) failed++;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {(e == null ? "ignored" : $"{e.Kind} name={e.Name} id={e.SteamId} text={e.Text}"),-60} <- {line}");
        }
        void Check(string what, bool ok) { if (!ok) failed++; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
        Check("AccountId forms agree", AccountId("[U:1:12345]") == 12345 && AccountId("12345") == 12345 && AccountId("76561197960278073") == 12345 && AccountId("x") == -1);
        if (File.Exists(MapsFile))
        {
            Maps = JsonSerializer.Deserialize(File.ReadAllText(MapsFile), Ctx.Default.ListMapInfo) ?? new();
            Check($"maps.json ranks are 1..N with no gaps ({Maps.Count} maps)", Maps.Count > 0 && Maps.Select((m, i) => m.Rank == i + 1).All(x => x));
            Check($"Find resolves the LAST rank, not just low ones (#{Maps.Count})", Find(Maps.Count.ToString()) is [{ } last] && last.Rank == Maps.Count);
            Check("A rank above the list is not silently treated as a workshop id", Find((Maps.Count + 1).ToString()).Count == 0);
            Check("Find by rank", Find("12") is [{ Rank: 12 }]);
            Check("Find by workshop id", Find(Maps[0].Id.ToString()) is [{ Rank: 1 }]);
            var uniq = Maps.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).First(g => g.Count() == 1).First();
            Check($"No name search is left in the bridge: a fragment or a Workshop map's name ({uniq.Name}) finds nothing - the plugin resolves names",
                Find("aim_").Count == 0 && Find(uniq.Name).Count == 0);
            Check("Find nothing", Find("zqxzqx").Count == 0 && Find("123456").Count == 0);
            const long absent = 9_876_543_210; // 10 digits, deliberately not a real workshop id, so this cannot start resolving if the list grows.
            Check("test fixture is genuinely absent from maps.json", Maps.All(m => m.Id != absent));
            Check("Find unlisted workshop id", Find($"{absent}") is [{ Rank: 0, Name: "workshop 9876543210", Official: false }]);
            Check("Find workshop URL", Find($"https://steamcommunity.com/sharedfiles/filedetails/?id={absent}") is [{ Rank: 0, Id: absent }]
                && Find($"https://steamcommunity.com/workshop/filedetails/?id={absent}&searchtext=x") is [{ Id: absent }]
                && Find($"https://steamcommunity.com/sharedfiles/filedetails/?id={Maps[0].Id}") is [{ Rank: 1 }]);
            Check("Find official: the whole name, in any case; a shortcut is no click's word", Find("ar_baggage") is [{ Name: "ar_baggage", Official: true }]
                && Find("AR_SHOOTS_NIGHT") is [{ Name: "ar_shoots_night" }] && Find("ar_shoots") is [{ Name: "ar_shoots", Official: true }]
                && Find("baggage").Count == 0 && Find("shoots").Count == 0);

            // ---- the random map a match end rolls when nobody picked ----
            S = new State(); // no crashes, no map: the state a fresh server has
            var eligible = Maps.ToList(); // the dice over every eligible map take the whole ranked list (Votes.cs: RandomPick)
            var draws = Enumerable.Range(0, 4000).Select(_ => RandomPick()!).ToList();
            Check($"A roll only ever lands on a ranked map ({eligible.Count} of {Maps.Count} eligible)", draws.All(m => m.Rank > 0 && !m.Official && Maps.Contains(m)));
            Check($"A map's download size plays no part: maps of 400 MB and up come up too ({draws.Count(m => m.SizeMb >= 400)} of 4000 rolls, {eligible.Count(m => m.SizeMb >= 400)} such maps eligible)",
                draws.Any(m => m.SizeMb >= 400) && eligible.Count(m => m.SizeMb >= 400) > 0);
            Check($"A roll spreads over the table ({draws.Select(m => m.Rank).Distinct().Count()} distinct maps in 4000 rolls)", draws.Select(m => m.Rank).Distinct().Count() > eligible.Count / 2);
            S.Map = eligible[0];                                                    // the map now loaded
            foreach (var m in eligible.Skip(1).Take(200)) S.Crashes[Key(m)] = 1;     // 200 maps that have crashed the server
            var after = Enumerable.Range(0, 4000).Select(_ => RandomPick()!).ToList();
            Check("A roll never lands on the map that is already loaded", after.All(m => Key(m) != Key(S.Map)));
            Check("A roll never lands on a map with a crash counter (so it never raises a confirm)", after.All(m => !S.Crashes.ContainsKey(Key(m))));
            Check($"Those 201 are gone from the pool ({after.Select(m => m.Rank).Distinct().Count()} distinct left)", after.Select(m => m.Rank).Distinct().Count() <= eligible.Count - 201);
            S = new State { LogName = eligible[3].Name };                            // no bridge-picked map: only the name the server logs
            Check("A roll excludes the loaded map by its log name too", Enumerable.Range(0, 2000).All(_ => RandomPick()!.Name != eligible[3].Name));
            S = new State();
            var dice = eligible.Select(m => $"Match over - rolling the dice: {Label(m)}{Detail(m)}").ToList();
            Console.WriteLine("dice: " + dice[Maps.Count / 7]);
            Check($"The dice line fits for {dice.Count(l => l.Length <= 120)} of {dice.Count} eligible maps (longest {dice.Max(l => l.Length)}, Say trims the rest)",
                dice.Count(l => l.Length > 120) * 100 < dice.Count);

            // ---- BROKEN_FILE as data: the file shapes it accepts ----
            Check("An empty path and a missing file both load as two empty lists, which is what switches both features off",
                LoadLists("") is { Broken.Count: 0, Packs.Count: 0 } && LoadLists("no-such-broken-file.json") is { Broken.Count: 0, Packs.Count: 0 });
            var configured = LoadLists();
            Console.WriteLine($"BROKEN_FILE={(BrokenFile == "" ? "(unset)" : BrokenFile)} -> {configured.Broken.Count} maps marked broken, {configured.Packs.Count} non-maps refused");
            Check("A bare array of ids parses, numbers and strings alike",
                ParseBroken("""[3084291314, "3453526792"]""") is { Count: 2 } bare && bare["3084291314"] == 0 && bare["3453526792"] == 0);
            Check("The generated object form parses, crash counts and official names and all",
                ParseBroken("""{"generated":"2026-10-03","maps":[{"id":3084291314,"rank":1,"name":"AIM Map","crashes":3},{"name":"ar_baggage"}]}""")
                    is { Count: 2 } obj && obj["3084291314"] == 3 && obj["ar_baggage"] == 0);
            Check("A legal but shapeless file marks nothing broken instead of throwing",
                ParseBroken("{}").Count == 0 && ParseBroken("[]").Count == 0 && ParseBroken("""{"maps":5}""").Count == 0 && ParseBroken("""{"broken":[]}""").Count == 0);
            Check("Array entries that are neither an id nor a name are skipped", ParseBroken("""[{"rank":7}, null, true, [1]]""").Count == 0);

            // ---- the confirmation gate in front of a pick, on a handed clock so expiry needs no waiting ----
            var clock = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
            var (bad, bad2, fine) = (Maps[10], Maps[11], Maps[12]);
            const string p1 = "[U:1:111]", p2 = "[U:1:222]";
            S = new State(); Broken = new(); PickConfirms.Clear();
            Check("An empty broken list disables the whole feature: nothing is gated",
                ConfirmGate(bad, p1, clock) is null && ConfirmGate(fine, p1, clock) is null);

            Broken = new Dictionary<string, int> { [Key(bad)] = 3, [Key(bad2)] = 3 };
            PickConfirms.Clear();
            var ask = ConfirmGate(bad, p1, clock);
            Console.WriteLine("warn: " + ask);
            Check("A broken map is held back first, and its note says what is on record and how to agree to it",
                ask is { } w && w.Contains(Label(bad)[..Math.Min(34, Label(bad).Length)]) && w.Contains("crashed 3 of 3 test loads and may restart the server")
                    && w.Contains($"Click Play again within {PickConfirmSeconds} s to go ahead"));
            var notes = Maps.Select(m => { Broken = new Dictionary<string, int> { [Key(m)] = 3 }; return PickConfirmNote(m, PickRecord(m)!, "Previous"); }).ToList();
            Check($"The note fits the HUD's note line untrimmed for all {notes.Count} maps, Previous's longer button word too (longest {notes.Max(n => n.Length)})", notes.All(n => n.Length <= NoticeLength));
            Broken = new Dictionary<string, int> { [Key(bad)] = 3, [Key(bad2)] = 3 };
            PickConfirms.Clear(); ConfirmGate(bad, p1, clock);
            Check("Per player: another player's click arms their own confirm and leaves the first player's standing, so the first player's repeat goes through",
                ConfirmGate(bad, p2, clock.AddSeconds(5)) is { } && PickConfirms.ContainsKey(p2) && ConfirmGate(bad, p1, clock.AddSeconds(6)) is null && PickConfirms.ContainsKey(p2));
            PickConfirms.Clear(); ConfirmGate(bad, p1, clock);
            Check("...and another player's ordinary pick leaves it standing too",
                ConfirmGate(fine, p2, clock.AddSeconds(2)) is null && ConfirmGate(bad, p1, clock.AddSeconds(3)) is null);
            PickConfirms.Clear(); ConfirmGate(bad, p1, clock);
            Check("The same player repeating the same map inside the window proceeds, and the window closes behind it",
                ConfirmGate(bad, p1, clock.AddSeconds(PickConfirmSeconds)) is null && !PickConfirms.ContainsKey(p1));
            PickConfirms.Clear(); ConfirmGate(bad, p1, clock);
            Check("The confirmation expires: one second past the window it asks again",
                ConfirmGate(bad, p1, clock.AddSeconds(PickConfirmSeconds + 1)) is { });
            PickConfirms.Clear(); ConfirmGate(bad, p1, clock);
            Check("Confirming is per map: a second broken map asks on its own account",
                ConfirmGate(bad2, p1, clock.AddSeconds(2)) is { } && PickConfirms[p1].Key == Key(bad2));
            PickConfirms.Clear(); ConfirmGate(bad, p1, clock);
            Check("An unbroken map is unaffected, and that player's pick of one drops their outstanding confirmation",
                ConfirmGate(fine, p1, clock.AddSeconds(2)) is null && !PickConfirms.ContainsKey(p1));
            PickConfirms.Clear();
            Check("Previous asks with its own button's name", ConfirmGate(bad, p1, clock, "Previous") is { } prevAsk && prevAsk.Contains("Click Previous again"));
            PickConfirms.Clear();

            // ---- the pre-existing live crash counter keeps its own wording and the same gate ----
            Broken = new(); S = new State(); S.Crashes[Key(bad)] = 2; PickConfirms.Clear();
            Check("The live crash counter still gates a pick, in its own words",
                ConfirmGate(bad, p1, clock) is { } c1 && c1.Contains("crashed the server 2 time(s)") && ConfirmGate(bad, p1, clock.AddSeconds(10)) is null);
            Broken = new Dictionary<string, int> { [Key(bad)] = 3 };
            Check("A map that is both swept-broken and has crashed live reads as broken, the stronger evidence",
                PickRecord(bad) is { } both && both.Contains("test loads"));

            // ---- the dice must never walk a broken map past a gate nobody was shown ----
            S = new State();
            Broken = eligible.Skip(5).Take(300).GroupBy(Key).ToDictionary(g => g.Key, _ => 3);
            var rolls = Enumerable.Range(0, 4000).Select(_ => RandomPick()!).ToList();
            Check($"The dice never roll one of the {Broken.Count} broken maps", rolls.All(m => !Broken.ContainsKey(Key(m))));

            // ---- non-maps (skin, config and sound packs, empty uploads): refused outright - by rank, name, bare workshop id or URL - never confirmed, never rolled ----
            Check("The packs array parses: ids as numbers or strings, a name when there is one, entries without a usable id skipped",
                ParsePacks("""{"maps":[],"packs":[3469279579,"3316991421",{"id":3391281371,"name":"Goku SS2 v1/ v2"},{"name":"no id"},{"id":"x"},{"id":-4},null,true]}""")
                    is { Count: 3 } pk && pk["3391281371"] == "Goku SS2 v1/ v2" && pk["3469279579"] == "workshop 3469279579" && pk.ContainsKey("3316991421"));
            Check("No packs array, an empty or shapeless one, or a bare-array file means no packs - and the broken maps beside them still load",
                ParsePacks("""{"maps":[{"id":1,"crashes":3}]}""").Count == 0 && ParsePacks("""{"packs":[]}""").Count == 0
                && ParsePacks("""{"packs":{"id":1}}""").Count == 0 && ParsePacks("[3469279579]").Count == 0
                && ParseBroken("""{"maps":[{"id":1,"crashes":3}],"packs":[2]}""") is { Count: 1 } beside && beside["1"] == 3);

            // Every RCON command the bridge would send, recorded instead of sent: what lands in `sent` is everything the server would have
            // received. A refusal is a note to the player (Told), and sends the server nothing at all. A pick is a vote; alone,
            // the picker's own vote decides it at once, so an ordinary pick ends in the change counting down.
            var sent = new List<string>();
            var door = Send;
            Send = command => { sent.Add(command); return ""; };
            void Pick(string query, string who = "Remy", string id = p1) { sent.Clear(); ClearNotices(); Pending = null; Ballot = null; VoteWaitUntil = default; PickMap(who, id, query); }
            static Dictionary<string, string> PacksOf(IEnumerable<MapInfo> ms) => ms.Where(m => !m.Official).GroupBy(Key).ToDictionary(g => g.Key, g => g.First().Name);
            S = new State(); Broken = new(); PickConfirms.Clear(); Players.Clear();
            var listed = Maps.Skip(200).First(); // a pack maps.json still lists, as the live list does today
            const long hole = 9_876_543_211;     // a pack maps.json does not list: only its id can reach it
            Check("test fixture for the unlisted pack is genuinely absent from maps.json", Maps.All(m => m.Id != hole));
            Packs = new Dictionary<string, string> { [Key(listed)] = listed.Name, [$"{hole}"] = $"Zebra Skins | Test Pack {Maps.Count + 1}" };

            Pick(fine.Rank.ToString());
            Check($"Control: an ordinary pick opens the vote and, the picker alone, starts the change and says so on the news line, so the silence below means something",
                Pending is { } go && go.Map == fine && Told.Count == 2 && Told[0].Contains("put") && Told[1].Contains("changing in") && NewsNow.Text == Told[1] && sent.Count == 0);
            Pick(listed.Rank.ToString());
            Console.WriteLine("refusal: " + string.Join(" | ", Told));
            Check($"A pack maps.json still lists is refused by its rank ({listed.Rank}), in the neutral words, in the player's own note",
                Told is [var byRank] && byRank == PackRefusal(listed) && byRank.Contains($"{listed.Name} {NotAMap}") && NoteFor.ContainsKey(111));
            Check("The refusal sends the server nothing at all: no chat, no host_workshop_map, no changelevel, no vote, nothing pending",
                sent.Count == 0 && Pending is null && Ballot is null && NewsNow.Text == "");
            Pick(listed.Id.ToString());
            var byId = Told.ToList();
            Pick($"https://steamcommunity.com/sharedfiles/filedetails/?id={listed.Id}");
            Check("...and by its workshop id and its pasted URL, each answered with that one note only",
                byId is [var a1] && a1.Contains(NotAMap) && Told is [var a2] && a2.Contains(NotAMap) && Pending is null && sent.Count == 0);
            Pick($"{hole}");
            Check("The hole: the bare id of a pack maps.json does not list is refused too, named from the packs list",
                Told is [var h1] && h1.Contains("Zebra Skins | Test Pack") && h1.Contains(NotAMap) && Pending is null);
            Pick($"https://steamcommunity.com/workshop/filedetails/?id={hole}&searchtext=x");
            Check("...and so is its pasted URL", Told is [var h2] && h2.Contains("Zebra Skins | Test Pack") && h2.Contains(NotAMap) && Pending is null);
            Pick($"{Maps.Count + 1}");
            Check($"A number past the list ({Maps.Count + 1}) gets the plain answer", Told is [var past] && past.Contains("No map is") && Pending is null);

            Packs = new();
            Pick(listed.Rank.ToString());
            var rankAgain = Pending;
            Pick($"{hole}");
            Check("With an empty packs list the same rank and the same bare id load like any map: the feature is off",
                rankAgain is { } r0 && r0.Map == listed && Pending is { } h0 && h0.Map.Id == hole && Unlisted(h0.Map) && !Told.Any(c => c.Contains(NotAMap)));

            Packs = PacksOf(eligible.Skip(3).Take(300));
            var packRolls = Enumerable.Range(0, 4000).Select(_ => RandomPick()!).ToList();
            Check($"The dice never roll one of the {Packs.Count} packs", packRolls.All(m => !IsPack(m)));
            Check($"The banner counts only real maps: {Playable} of {Maps.Count} listed", Playable == Maps.Count - Packs.Count);

            Packs = PacksOf([listed]);
            Broken = new Dictionary<string, int> { [Key(bad)] = 3 };
            PickConfirms.Clear();
            Pick(bad.Rank.ToString());
            var asked = Told.ToList();
            sent.Clear(); ClearNotices(); PickMap("Remy", p1, bad.Rank.ToString()); // the same player again, well inside the 30 s window
            Check("Beside a packs list a broken map still asks first, and the same player's repeat still goes through (to the vote, and alone the change)",
                asked is [var q1] && q1.Contains("may restart the server") && q1.Contains("to go ahead") && Pending is { } conf && conf.Map == bad);
            Pick(listed.Rank.ToString());
            Check("...while the pack beside it is still refused, with no confirm offered", Told is [var still] && still.Contains(NotAMap) && !still.Contains("again within") && Pending is null);
            Packs = new Dictionary<string, string> { [Key(bad)] = bad.Name };
            PickConfirms.Clear();
            Pick(bad.Rank.ToString());
            Check("An id on both lists is refused, not confirmed: a pack has nothing to agree to",
                Told is [var onBoth] && onBoth.Contains(NotAMap) && Pending is null && PickConfirms.Count == 0);

            var refusals = Maps.Select(m => { Packs = PacksOf([m]); return PackRefusal(m); }).ToList();
            Packs = new Dictionary<string, string> { [$"{hole}"] = new string('W', 200) };
            refusals.Add(PackRefusal(new MapInfo(0, hole, "", 0)));
            Check($"The refusal fits the note line untrimmed for every listed name and a 200-character one (longest {refusals.Max(l => l.Length)})",
                refusals.All(l => l.Length <= NoticeLength));
            Packs = new Dictionary<string, string> { [$"{hole}"] = """Jo"ri; quit""" };
            Pick($"{hole}");
            Check("A pack name full of quotes and semicolons reaches nothing but the note: the server gets no command at all", Told.Count == 1 && sent.Count == 0);

            // ---- the refusal is true for every kind of non-map on the list, not only for skin packs ----
            var kinds = new (string Kind, string Name)[] { ("skin pack", "ORENTI | Assets | RANDOM MODELS"),
                ("config", "Ultimate Low Latency & High FPS [CS2 Professional Config] by Vahanator"), ("sound pack", "Counterstrike 2 Quake Sounds"),
                ("empty upload", "Aim_Test"), ("single-file stub", "Mirage Silent") };
            Packs = kinds.Select((k, i) => (Key: $"{hole + 1 + i}", k.Name)).ToDictionary(p => p.Key, p => p.Name);
            var kindLines = kinds.Select((k, i) => { Pick($"{hole + 1 + i}"); return (k.Kind, k.Name, Line: Told.Count == 1 ? Told[0] : $"{Told.Count} notes"); }).ToList();
            Console.WriteLine("refusal by kind: " + string.Join(" | ", kindLines.Select(k => $"{k.Kind}: {k.Line}")));
            Check("A config, a sound pack, an empty upload and a stub get the same neutral line as a skin pack, and none is called a skin pack",
                kindLines.All(k => k.Line == $"{k.Name[..Math.Min(60, k.Name.Length)]} {NotAMap}" && !k.Line.Contains("skin pack")) && Pending is null);

            // ---- the committed list itself: broken.json beside maps.json, when the selftest runs where both are ----
            if (File.Exists("broken.json"))
            {
                var (cb, cp) = LoadLists("broken.json");
                Check($"broken.json loads: {cb.Count} broken maps, {cp.Count} non-maps, none on both lists", cb.Count > 0 && cp.Count > 0 && cb.Keys.All(k => !cp.ContainsKey(k)));
                Check($"No non-map is on maps.json: the {Maps.Count} listed are all maps", Maps.All(m => !cp.ContainsKey(Key(m))));
                Packs = cp;
                var lines = cp.Keys.Select(k => PackRefusal(new MapInfo(0, long.Parse(k), "", 0))).ToList();
                Check($"Every committed non-map's refusal fits the note line untrimmed and claims only that it is no playable map (longest {lines.Max(l => l.Length)})",
                    lines.All(l => l.Length <= NoticeLength && l.EndsWith(NotAMap) && !l.Contains("skin pack")));
                Packs = new(); Broken = cb; S = new State(); PickConfirms.Clear();
                var listedCrashers = Maps.Where(m => cb.ContainsKey(Key(m))).Select(m => (Map: m, Line: PickConfirmNote(m, PickRecord(m)!))).ToList();
                Console.WriteLine("broken.json confirm notes: " + string.Join(" | ", listedCrashers.Select(c => c.Line)));
                Check($"Every committed broken map on the list ({listedCrashers.Count}) gets a confirm note that fits: 'crashed N of N' and 'may restart the server' only when the file says crashes > 0, its reason otherwise",
                    listedCrashers.All(c => c.Line.Length <= NoticeLength && (cb[Key(c.Map)] > 0 ? c.Line.Contains($"crashed {cb[Key(c.Map)]} of {cb[Key(c.Map)]} test loads") && c.Line.Contains("may restart the server")
                        : !c.Line.Contains("restart the server") && (BrokenReasons.TryGetValue(Key(c.Map), out var rw) ? c.Line.Contains($"is broken: {rw}") : c.Line.Contains("is on the broken list")))));
                var offList = cb.Keys.Where(k => long.TryParse(k, out var id) && Maps.All(m => m.Id != id)).ToList();
                Check($"A committed crasher that is not on the list ({offList.Count}) still asks to confirm when its bare id is picked",
                    offList.All(k => Find(k) is [var u] && Unlisted(u) && PickRecord(u) is { } w && w.Contains("may restart the server")));
                Broken = new(); S = new State(); PickConfirms.Clear();
            }
            else Console.WriteLine("skip broken.json checks: no broken.json beside maps.json");
            Send = door; Packs = new(); Pending = null; Ballot = null; VoteWaitUntil = default; ClearNotices(); Players.Clear();
            S = new State(); Broken = new(); PickConfirms.Clear(); // the checks below this block read the unbroken wording
            QueueSelfTest(Check); // SelfTestQueue.cs: picks as votes, likes, the weighted dice, !previous, the start roll, crashes, maintenance, the guide
            VoteSelfTest(Check);  // SelfTestVote.cs: the vote for the next map and the game modes
        }
        else Console.WriteLine($"skip maps checks: {MapsFile} not found");
        // The "players : N humans" line of an RCON status reply, as the live server sends it.
        const string status = "os/type  : Linux dedicated\nplayers  : 4 humans, 0 bots (0 max) (not hibernating) (unreserved)\n---------spawngroups----\n";
        Check("Humans reads the count out of an RCON status reply", HumansRx().Match(status) is { Success: true } hm && hm.Groups[1].Value == "4");
        Check("The Help page: five groups that fit their slots, each row one line",
            Hud.Help.Length == 5 && Hud.Help.All(g => g.Rows.Length is > 0 and <= Hud.RowsPerGroup) && Hud.Help.SelectMany(g => g.Rows).All(r => (r.Command + r.Words + r.Also).Length <= 100));
        var menuWords = string.Join(" ", Hud.Help.SelectMany(g => g.Rows).Select(r => r.Command + " " + r.Words + " " + r.Also)) + " " + string.Join(" ", Hud.Steps.Select(x => x.Command + " " + x.Words))
            + " " + Hud.MenuHint + " " + string.Join(" ", Hud.How.SelectMany(r => r.Lines));
        Check("The menu's words name only real chat commands (ChatCommands.cs: they are back since Over Strike 2)",
            Regex.Matches(menuWords, @"![a-z]+").All(m => ChatCommands.Find(m.Value) != null));
        HudSelfTest(Check);
        CheckSelfTest(Check);
        Os2SelfTest(Check); // SelfTestOS2.cs: reasons, player crashes, our maps
        SettingsSelfTest(Check); // SelfTestSettings.cs: the settings file, else the environment, and the line that says which
        PicturesSelfTest(Check); // SelfTestPictures.cs: every mode's maps, the mode filter, the picture on request
        ChatSelfTest(Check); // SelfTestChat.cs: the chat commands' words, the aliases, the fun words, the vote's bare number
        RconSelfTest(Check); // SelfTestRcon.cs: a map command is never sent twice
        RandomQueueSelfTest(Check); // SelfTestRandomQueue.cs: the discovery stock, the disk, queues.json and the watcher's report
        BallotSelfTest(Check); // SelfTestBallot.cs: the Workshop's numbers, popularity, the kept maps, the points, the tags, the recipe, restarts
        DashSelfTest(Check); // SelfTestDash.cs: the Sync page's block, its words out of the sync's file, the website's data, the layout's labels
        TabSelfTest(Check);
        GuiSelfTest(Check);
        RulesSelfTest(Check); // SelfTestRules.cs: the fun page, the game-changers block, and How it works and Help against the code
        FunCombosSelfTest(Check); // SelfTestFun.cs: every combination of the fun settings
        MapVote.InactivitySeconds = inactivity;
        Check($"The inactivity timer is {MapVote.InactivitySeconds} s, and the second inactivity limit is off (a proposal of {MapVote.HardCapSeconds} s at the most), one constant away",
            MapVote.InactivitySeconds == 5 && MapVote.IdleLimitSeconds == 0 && MapVote.HardCapSeconds == 180);
        Console.WriteLine($"local time zone: {TimeZoneInfo.Local.Id} ({DateTime.Now:HH:mm})");
        Console.WriteLine(failed == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({failed})");
        return failed == 0 ? 0 : 1;
    }

    // ---- --build-maps <url>: scrape the rotation page into maps.json ----
    [GeneratedRegex(@"<div class=""rank""><span>(\d+)</span>.*?<h2 class=""name"">(.*?)</h2>.*?size-\w+"">([\d.]+) (MB|GB)</dd>.*?host_workshop_map (\d+)", RegexOptions.Singleline)]
    private static partial Regex RowRx();

    static int BuildMaps(string url)
    {
        var html = new HttpClient().GetStringAsync(url).GetAwaiter().GetResult();
        var maps = RowRx().Matches(html).Select(m => new MapInfo(int.Parse(m.Groups[1].Value), long.Parse(m.Groups[5].Value), WebUtility.HtmlDecode(m.Groups[2].Value).Trim(),
            (int)Math.Round(double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) * (m.Groups[4].Value == "GB" ? 1000 : 1)))).ToList();
        if (maps.Count == 0 || !maps.Select((m, i) => m.Rank == i + 1).All(x => x)) { Log($"unexpected page structure: {maps.Count} rows"); return 1; }
        File.WriteAllText("maps.json", JsonSerializer.Serialize(maps, Ctx.Default.ListMapInfo));
        Log($"wrote maps.json with {maps.Count} maps");
        return 0;
    }
}

enum Kind { Chat, Connected, Entered, Disconnected, MapStart, MatchEnd, Shutdown }
record LogEvent(Kind Kind, string Name, string SteamId, string Text);
record MapInfo(int Rank, long Id, string Name, int SizeMb, bool Official = false, string Mode = "", long Updated = 0); // Official: from OFFICIAL_MAPS or a mode's official pool, no rank or id; Rank 0 and not official: unlisted workshop map; Mode: the game mode it loads in (Modes.cs), "" for Arms Race; Updated: the Workshop's time_updated (unix seconds) when the list carries it, 0 unknown

sealed class State
{
    public string? ServerToken { get; set; }          // last X-Server-Instance-Token seen
    public MapInfo? Map { get; set; }                 // current map when the bridge picked it
    public string? LogName { get; set; }              // map name as the server logs it
    public string? PickedBy { get; set; }             // "the vote", "the dice": what picked the map that is on
    public bool CleanShutdown { get; set; }
    public Dictionary<string, int> Crashes { get; set; } = new();
    public Dictionary<string, long> CrashStamps { get; set; } = new(); // crash key -> the map's Workshop update time when it was marked: a newer update lifts the mark (CrashWatch.cs)
    public HashSet<string> Seen { get; set; } = new();
    public int RoundMinutes { get; set; } = Program.DefaultRoundMinutes;   // survives restarts; re-applied after every map load
    public List<MapInfo> History { get; set; } = new();  // maps played, oldest first, at most 20: !previous and the dice's recent-map rule
    public string? RoundBy { get; set; }              // who set a match length that is not the default
    public string Mode { get; set; } = Modes.ArmsRaceId;              // the game mode the server was last told to load in (ModeSwitch.cs): Arms Race after every restart and after 10 min with nobody on
    public Dictionary<string, string> PlayerCrashes { get; set; } = new(); // crash key -> why: maps that crashed players' games (BrokenWhy.cs)
    public Dictionary<string, List<StockEntry>> Stock { get; set; } = new(); // the discovery stock, a lane per mode (RandomQueue.cs)
    public Dictionary<string, List<long>> Shown { get; set; } = new();       // map key -> when it was a card on a ballot (unix seconds), the last ShownFadeDays (VoteRound.cs)
    public HashSet<string> Met { get; set; } = new();                        // every map shown on a ballot or played here: NEW TO US is the rest
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(State)), JsonSerializable(typeof(List<MapInfo>)), JsonSerializable(typeof(Dictionary<string, Dictionary<string, int>>)), JsonSerializable(typeof(RemovedFile)), JsonSerializable(typeof(Dictionary<string, string>)), JsonSerializable(typeof(QueuesFile))]
partial class Ctx : JsonSerializerContext { }
