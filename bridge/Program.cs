// The bridge: hears the game server's log and the plugin's clicks, decides, and sends the server its orders over RCON. See docs/reference.md.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

static partial class Program
{
    // ---- configuration: the settings (BridgeConfig.cs) ----
    static string StateDir = BridgeConfig.Values.StateDir, // not readonly: --selftest points these three at a scratch directory and the committed lists
        MapsFile = BridgeConfig.Values.MapsFile, BrokenFile = BridgeConfig.Values.BrokenFile;
    static readonly string RconHost = BridgeConfig.Values.RconHost, RconPassword = BridgeConfig.Values.RconPassword, LogSecret = BridgeConfig.Values.LogSecret,
        StartMap = BridgeConfig.Values.StartMap; // the cs2 service's CS2_STARTMAP: what a booting server loads, and what a nameless lone map start is taken for
    static readonly int RconPort = BridgeConfig.Values.RconPort, HttpPort = BridgeConfig.Values.HttpPort,
        ChangeDelay = BridgeConfig.Values.ChangeDelay, RestartCooldown = BridgeConfig.Values.RestartCooldown;
    static HudTab.Mode HudTabMode = HudTab.Parse(BridgeConfig.Values.Tab); // the TAB chord (HudTab.cs): down (the default: the board's cursor while TAB is held), hold, window, on, menu, off
    static HudCheck.Mode HudCheckMode = HudCheck.Parse(BridgeConfig.Values.Check); // the HUD check (HudCheck.cs): off, warn or enforce; warn by default, so every player's HUD version is learned at their first click
    static readonly List<MapInfo> Officials = BridgeConfig.Values.OfficialMaps // installed on the server, loaded with changelevel
        .Select(n => new MapInfo(0, 0, n, 0, true)).ToList();
    static string DataDir => Path.GetDirectoryName(Path.GetFullPath(MapsFile)) ?? "."; // the data files' folder: beside maps.json (/data in the stack)

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
    static DateTime AnnounceSentAt;                // ...and when it was sent (a load behind a background download waits longer: MapChange.cs)
    static bool LoadBehindDownload;                // ...and whether the restart watcher reported a background download during it
    static readonly Dictionary<string, (string Key, DateTime Until)> PickConfirms = new(); // SteamID -> the risky map it clicked once, and until when the second click counts
    static DateTime MatchEndUntil;                 // debounce: one match end is one decision, however many lines the server logs for it
    static readonly Random Dice = new();
    static Timer? Clock;                           // drives Tick once a second; held here so it is never collected

    static int Main(string[] args)
    {
        if (args is ["--selftest"]) return SelfTest();
        if (args is ["--search-index", .. var rest]) return SearchIndexFile.Command(rest); // SearchIndexFile.cs: the committed search.json, written or checked
        Log(BridgeConfig.Settings.StartLine);       // where the settings came from - no value in it
        if (BridgeConfig.StopCode(BridgeConfig.Settings) is { } refused) return refused; // settings it cannot use, or none: its line said why
        if (RconPassword == "" || LogSecret == "") { Log("server.rconPassword and bridge.secret must be set"); return 2; }
        Maps = JsonSerializer.Deserialize(File.ReadAllText(MapsFile), Ctx.Default.ListMapInfo) ?? new();
        TakeBrokenLists(LoadLists());               // BrokenList.cs: broken.json, read once for its four lists
        LoadRemoved();
        Log(ReadBlockedFile());                     // Blocked.cs: blocked.json, beside broken.json's own blocked maps
        Log(ReloadSearch(SearchFile));              // SearchIndex.cs: the map browser's full-text index - read here, then watched like the others
        Log(ReloadSizes(SizesFile));                // Sizes.cs: each map's measured size, for the too-big rule - read here, then watched like the others
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
        Log($"listening on port {HttpPort}, rcon {RconHost}:{RconPort}, {Maps.Count} maps + {Officials.Count} official, {Broken.Count} broken, {Packs.Count} non-maps, {Removed.Count} gone from the Workshop, {Blocked.Count} blocked, {Votes.Count} maps with votes, state {statePath}");
        InMaintenance();
        foreach (var work in Loop.GetConsumingEnumerable())
        {
            try { work(); } catch (Exception e) { Log("error: " + e); }
            AfterWork();
        }
        return 0;
    }

    static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
    static void Post(Action work) => Loop.Add(work);

    /// <summary>After every piece of work on the event loop, what changed goes to the plugin at once: the menu's map list first, when its version
    /// moved (LiveState.cs), then the state that names that version (HudTransport.cs). Neither may end the loop.</summary>
    static void AfterWork()
    {
        try { RefreshCatalog(); } catch (Exception e) { Log("hud: rebuilding the map list failed: " + e.Message); }
        PublishHudSafe();
    }
    static Action<int, Action> After = (ms, work) => Task.Delay(ms).ContinueWith(_ => Post(work)); // --selftest swaps in a timer on its own clock

    static void Save() => WriteAtomic(Path.Combine(StateDir, "state.json"), JsonSerializer.Serialize(S, Ctx.Default.State));

    /// <summary>A file written whole: its readers see the old one or the new one. On Windows - builds and self-tests only - something else can hold
    /// the target for a moment (a scanner, by all signs) and the move fails with "access denied", so it is tried again there, for 2 s at the most. On
    /// Linux a rename does not care who has the file open: one try, and a real failure throws as ever.</summary>
    static void WriteAtomic(string path, string text)
    {
        File.WriteAllText(path + ".tmp", text, new UTF8Encoding(false));
        for (var tries = OperatingSystem.IsWindows() ? 100 : 1; ; tries--)
            try { File.Move(path + ".tmp", path, true); return; }
            catch (Exception e) when (tries > 1 && e is IOException or UnauthorizedAccessException) { Thread.Sleep(20); }
    }

    /// <summary>Steam account id from "[U:1:123]", a bare account id, or a SteamID64; -1 when unrecognised.</summary>
    static long AccountId(string id)
    {
        if (SteamId3Rx().Match(id) is { Success: true } m) return long.Parse(m.Groups[1].Value);
        return long.TryParse(id, out var n) ? (n >= 76561197960265728 ? n - 76561197960265728 : n) : -1;
    }

    static Func<DateTime> Now = () => DateTime.UtcNow; // --selftest hands its own clock

    static string Clip(string text, int n) => text.Length <= n ? text : text[..(n - 1)] + "~";
    static string Names(IEnumerable<string> names) => names.ToList() is var l && l.Count <= 1 ? l.FirstOrDefault() ?? "" : string.Join(", ", l.SkipLast(1)) + " and " + l[^1];

    // ---- the clock: once a second ----

    /// <summary>A load that never arrived is given up on, the maintenance flag is noticed, the vote's timer runs, and the start roll gets its chance.
    /// A load the restart watcher reported a background download during may wait behind it (Steam's client runs one at a time) and then download its
    /// own map: it is given up only LoadBehindDownloadMinutes after the change went out.</summary>
    static void Tick()
    {
        var now = Now();
        // A step that throws costs that step, never the rest of the tick: the vote's timer above all has to run.
        void Step(string what, Action step) { try { step(); } catch (Exception e) { Log($"tick: {what} failed: {e}"); } }
        Step("the maintenance flag", () => InMaintenance());
        Step("the empty server's reset", () => ResetWhenEmpty(now)); // EmptyServer.cs: nobody on for over 10 min - the fun settings back to their defaults
        Step("the load's deadline", () => LoadDeadline(now));
        Step("the mode's probe", () => ProbeModeIfDue(now));         // ModeSwitch.cs: which mode the server is in, when the bridge does not know
        BallotTick(now);                                             // VoteRound.cs: the vote's timer, and voters who left; it never throws
        Step("the start roll", () => TryStartRoll(now));
        Step("the stock", () => QueueTick(now));                     // RandomQueue.cs: the stock checked and topped up, queues.json refreshed
        Step("a restart's notice", () => RestartTick(now));          // Restarts.cs: a restart's notice that has run out
        Step("the data files", WatchTick);                           // Reload.cs
        Step("the sync's status", () => SyncTick(now));              // SyncDash.cs: the sync's status file, the Sync page's block and the website's files
        Step("the menu's map list", RefreshCatalog);                 // LiveState.cs, as after every piece of work (AfterWork)
    }

    static bool SelfTesting; // --selftest: the dice roll tens of thousands of times without a log line each
}
