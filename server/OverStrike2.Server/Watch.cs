using System.Globalization;
using System.Text.RegularExpressions;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>THE RESTART WATCHER (os2-server watch): restarts the game server when it needs it, between matches, and never in a loop.
/// pre.sh starts it in the background right before the engine starts, and it lives as long as the container. Its jobs, each behind its
/// own switch (the last two never restart anything):
///   UPDATES (restarts.updates). The image's entry script runs SteamCMD only when the container starts, so a Valve patch left a server
///      rejecting every client for ~44 hours ("Bad host version"). Every restarts.updateCheckMinutes this asks Valve's UpToDateCheck
///      whether the installed build is current, and restarts the engine when it is not: at the end of the current match with players on,
///      at once with nobody on.
///   NEW VERSIONS (restarts.newVersions). A deployed new version of our own parts - the plugins, this program, pre.sh, the mode cfgs,
///      the settings the engine side reads (Fingerprint) - restarts the engine the same way, once what is on disk has been still for
///      VersionSettle: at the end of the match with players on, at once with nobody on.
///      Both are announced: the bridge hears of the restart when it becomes due (POST /restart-notice, "match-end") and at the match end
///      (a countdown of RestartCountdown s, "now"), shows it to the players and holds its own map change; after the restart, once the
///      engine answers, it hears what changed (POST /updated). A bridge that does not take the notice: nobody is warned - the watcher
///      words nothing for players, and without the bridge the menu is dead anyway.
///   DAILY RESTART (restarts.daily, local time). Once a day, and only while nobody is on (A2S and RCON) - never a kick, never a match
///      end. With players on it waits; a day they stay on until DailyWindow after the slot has none. It is the moment the hand-overs are
///      taken in and the maps outside the keep set go (organize, at every engine start). A planned restart of another kind inside the
///      window counts as the day's.
///   FREEZE RECOVERY (restarts.freezeRecovery). A frozen engine never exits, so Docker never restarts it; this kills it after
///      restarts.freezeGraceSeconds of silence, on either of two paths:
///      a. The error-map signature. Some workshop items drive the engine into its `error` map ("Level loading started for 'error'",
///         "LoopInit: in "error" map", "Assertion Failed in function LoopInit()"): measured, 27 of 27 such freezes printed that signature
///         and then not one further line, and none ever recovered. Kill once the signature is seen AND A2S has been silent for the grace
///         AND no other map has started loading since.
///      b. Silence alone, for the freezes that print nothing. Kill when A2S AND RCON have both failed on every poll for the grace - at
///         least SilentPolls polls each, so one lost packet is never a streak - but only for an engine process that has answered A2S since
///         it started (a boot or a relaunch is never killed for being slow to come up), never once the watcher itself has sent `quit` or
///         killed it, and never after a `quit`/`_restart` from anyone else shows in console.log (the bridge's !restart quits too).
///      Why 10 s and no map-load exemption: 368 healthy map loads (327 fresh downloads, up to 2.9 GB) kept A2S quiet for 2 s at most - the
///      old map keeps answering while a workshop map downloads, and the load itself is the only silent stretch.
///   DOWNLOAD RESTART (restarts.downloadRestart). An engine process whose Steam client has dropped every download source never gets one
///      back: every download ends "No Connection". Any download that says so - a background download or a player's - marks the process broken at once,
///      and a restart is due only while nobody is on, at most one per 2 h and 3 per 24 h; past that it only warns. The Steam client's logs
///      are copied to update-watch/steamlogs/ the moment it is marked.
///   THE DOWNLOAD LINE (maps.downloadLine): DownloadLine.
///   THE BACKGROUND DOWNLOADS (maps.backgroundDownloads): ReadyMaps.
///
/// HOW A RESTART HAPPENS. Planned: RCON `quit` - the engine exits, the image's entry script reaches its end and Docker's restart policy
/// runs it again, the only thing that re-runs SteamCMD (`_restart` would relaunch the binary in place and update nothing). Right before the
/// quit the bridge hears of it (POST /quitting), which it takes as the clean shutdown: the quit's echo in the log stream dies with the
/// process. A notice the bridge does not take holds the quit - tried again every QuitNoticeRetry s - and after QuitNoticePatience without
/// its answer any planned restart goes ahead anyway: a false crash mark can be cleared. A quit that cannot be delivered after a notice
/// sends the notice's cancel. Freeze: SIGTERM, then SIGKILL, with no notice: the bridge records that one as a crash of the map that caused
/// it, which is the truth.
///
/// WHERE IT LOOKS. The watcher cannot read the container's stdout, so the engine runs with -condebug (console.log) and sv_logecho 1. At
/// start it proves the file is live: an RCON `echo` of a random token must appear in a console.log within 10 s. If none does, freeze
/// recovery is off and the restarts that wait for a match end wait for an empty server instead.
///
/// LOOP PROTECTION, in update-watch/state.json (WatchState): planned restarts >= 10 min apart, &lt;= 4 in any 24 h; an update restart that
/// left the build unchanged is a miss, retried after 30 min and then 2 h, and the 3rd latches the update breaker for that build; a version
/// restart that did not install the new version latches for that version. Freeze kills >= 3 min apart, &lt;= 10 per 24 h; a kill after which
/// the server never answers within 10 min is a miss, and 2 latch the freeze breaker. A state file that will not parse, a state folder that
/// is not writable, no RCON password, Valve unreachable, steam.inf missing, A2S silence, or A2S and RCON disagreeing about players: each
/// means "no action", never "act".
///
/// THE FILES: this one holds the state, the engine in words and the loop; Watch.AfterRestart.cs judges the previous run's restart,
/// Watch.Console.cs reads console.log, Watch.Planned.cs decides and makes the planned restarts, Watch.Freeze.cs the freeze kills, and
/// WorkshopJobs (Downloads.cs) the download restart's mark, the download line and the background downloads.</summary>
public sealed partial class Watch
{
    public const double Tick = 1.0;
    public const double A2sEvery = 2;             // seconds between A2S probes: a 10 s grace needs several polls inside it
    public const double ProbeRetry = 300;         // console.log liveness probe: retry interval, up to ProbeTries times
    public const int ProbeTries = 3;
    public const double ProbeRconRetry = 10;      // RCON not accepting yet: it listens a moment after A2S at boot, so come back soon...
    public const int ProbeRconMisses = 30;        // ...but only this many times, then a dead RCON counts as tries again
    public const double OkRepeat = 21600;
    public const double RecoverWithin = 600;      // a restart must bring A2S back within 10 minutes to count as recovered
    public const double QuitLead = 3;             // seconds between our own last line and the quit (no bridge to count down)
    public const int QuitTimeout = 30;            // an engine still alive this long after `quit` is SIGTERMed, then SIGKILLed
    public const double NoticeTimeout = 8;        // the bridge answers a notice from its event loop, within 5 s
    public const double QuitNoticeRetry = 60;     // a quit notice the bridge did not take is tried again after this (the restart stays due)
    public const double QuitNoticePatience = 1800;    // ...and any planned restart goes ahead without one after this long
    public const int RestartCountdown = 10;       // seconds the bridge counts down at the end of the match before the quit
    public const double AnnounceRefresh = 300;    // a due restart is announced to the bridge again this often (a bridge swapped since)
    public const double VersionCheckEvery = 60;   // seconds between two looks at our own parts on disk
    public const double VersionSettle = 120;      // a new version must be still this long: a deploy in progress is not one
    public const double UpdatedRetry = 60;        // the after-restart note, tried again after this...
    public const int UpdatedTries = 10;           // ...this many times

    [GeneratedRegex(@"^\s*players\s*:\s*([0-9]+)\s+humans,\s*([0-9]+)\s+bots", RegexOptions.Multiline)] private static partial Regex PlayersRx();

    public ServerConfig Config { get; }
    public IClock Clock { get; }
    public TimeZoneInfo Zone { get; }
    public Log Log { get; }
    public IEngine Engine { get; }
    public IBridge Bridge { get; }
    public IValve Valve { get; }
    public string SettingsPath { get; init; } = Core.Settings.DefaultPath;
    public Func<string, string?> Env { get; init; } = Environment.GetEnvironmentVariable;
    public Func<bool> Stopping { get; init; } = () => false;

    public WatchState State { get; set; }
    public string? Fault { get; set; }
    public string? DirFault { get; set; }
    readonly List<Tail> tails;
    public Tail? Console { get; private set; }     // the console.log proven live by the echo probe
    public int ProbeTriesDone { get; private set; }
    public int ProbeRconMissesDone { get; private set; }
    double probeNext;
    string? probeToken;
    double probeSent;
    public EngineInfo? A2sInfo { get; private set; }
    double a2sAt = double.NegativeInfinity;
    double? silentSince;                           // A2S: when the current run of failed polls began (null: it answered last)
    int a2sFails;                                  // ...and how many polls in a row have failed
    double? rconSince;                             // the same two for the RCON liveness probe, asked only while A2S fails
    int rconFails;
    public int? ArmedPid { get; private set; }     // the engine pid the silence path may kill: it answered A2S since it started
    public double? GoingDown { get; private set; } // when the watcher itself quit or killed the engine (no silence kill after)
    double? firstAnswer;
    double? signatureAt;
    bool otherLevel;
    readonly List<double> matchEnds = [];
    double usedMatchEnd = double.NegativeInfinity; // the match end a countdown or a quit was started on: the next needs a newer one
    public (string Answer, int? Required, string? Why) Verdict { get; set; } = ("unchecked", null, null);
    int? verdictBuild;
    public double VerdictAt { get; set; }
    double checkBackoff;
    public (string Kind, double Since)? Due { get; private set; }   // a planned restart waiting for its moment
    bool warned;
    double? noticeFailing;                         // since when the bridge has not taken a quit notice (null: it took the last one)
    double noticeRetry;                            // the next notice may go no sooner than this
    public double? CountdownUntil { get; private set; }   // the bridge counts down; the quit goes then
    string? announced;                             // the kind whose restart notice the bridge took last
    double announceTried;                          // when the match-end notice was last sent
    public Installed? InstalledNow { get; private set; }  // what install put in place for this engine
    public VersionParts? OnDisk { get; private set; }     // our own parts as they are on disk now
    public VersionParts? VersionDue { get; private set; } // a new version, still for VersionSettle: a restart is due for it
    string? versionSig;
    double versionSigAt;
    double versionCheckAt;
    bool versionUnread;                            // the last look at our parts could not read them
    readonly List<(string Body, int Tries, double Next)> updated = [];   // after-restart notes for the bridge
    public double Started { get; }
    public WorkshopJobs Workshop { get; }

    public Watch(ServerConfig config, IClock clock, TimeZoneInfo zone, Log log, IEngine engine, IBridge bridge, IValve valve)
    {
        Config = config;
        Clock = clock;
        Zone = zone;
        Log = log;
        Engine = engine;
        Bridge = bridge;
        Valve = valve;
        (State, Fault) = WatchState.Load(config.Paths.StateFile);
        DirFault = WatchState.PersistenceSelftest(config.Paths.StateDir);
        tails = config.Paths.ConsoleCandidates.Select(p => new Tail(p)).ToList();   // positioned before the engine starts writing
        Started = Now;
        InstalledNow = Installed.Read(config.Paths.InstalledFile);
        Workshop = new WorkshopJobs(this);
    }

    public double Now => UnixTime.Seconds(Clock.Now);
    void Sleep(double seconds) => Clock.Sleep(TimeSpan.FromSeconds(Math.Max(0, seconds)));
    string AtClock(double t) => Rules.AtClock(t, Zone);

    // ------------------------------------------------------------------------------------------------------------ the engine, in words

    public bool Say(string text)
    {
        var line = Words.ChatSafe(text);
        var ok = Engine.Rcon($"say \"{line}\"").Opened;
        Log.Line(ok ? $"chat: {line}" : $"WARNING: could not say: {line}");
        return ok;
    }

    /// <summary>A line for everybody on the HUD's top line (the plugin's server-only gg_notice, which answers "gg_notice: shown for N s:
    /// ..."); chat gets it only when the plugin does not answer.</summary>
    public bool Notice(string text, int seconds)
    {
        var line = Words.ChatSafe(text, 240);
        if (line.Length == 0)
            return true;
        var reply = (Engine.Rcon($"gg_notice {seconds} \"{line}\"").Reply ?? "").Trim();
        if (reply.StartsWith("gg_notice: shown", StringComparison.Ordinal))
        {
            Log.Line($"hud: {line} ({seconds} s)");
            return true;
        }
        return Say(text);
    }

    public int? RconHumans()
    {
        if (Engine.Rcon("status").Reply is not { } status)
            return null;
        var m = PlayersRx().Match(status);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    string? PatchVersion() => SteamInf.ReadPatchVersion(Config.Paths.SteamInf);

    // ------------------------------------------------------------------------------------------------------------ state

    /// <summary>Faults that disable every restart: the gates are only as good as the file they live in.</summary>
    public string? Blocked()
    {
        if (DirFault is not null)
            return $"the state directory is unusable ({DirFault})";
        if (Fault is not null)
            return $"the state file is damaged ({Fault})";
        if (Config.RconPassword.Length == 0)
            return "the RCON password is empty (server.rconPassword, the image's CS2_RCONPW), so neither `quit` nor a chat line can be sent";
        return null;
    }

    bool Save()
    {
        if (DirFault is not null || Fault is not null)
            return false;
        try
        {
            State.Save(Config.Paths.StateFile);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DirFault = $"cannot write {Config.Paths.StateFile} ({e.Message})";
            Log.Line($"ALERT: {DirFault} - every restart is disabled");
            return false;
        }
    }

    // ------------------------------------------------------------------------------------------------------------ the loop

    /// <summary>A2S every A2sEvery s. While it fails, ask RCON too (auth only), so path b sees both. Each keeps a streak: since when it has
    /// failed, and on how many polls in a row.</summary>
    void Poll(double now)
    {
        if (now - a2sAt < A2sEvery)
            return;
        a2sAt = now;
        A2sInfo = Engine.Probe();
        if (A2sInfo is { } a)
        {
            (silentSince, a2sFails, rconSince, rconFails) = (null, 0, null, 0);
            var pid = Engine.Pid();
            if (pid is not null && pid != ArmedPid && GoingDown is null)
            {
                ArmedPid = pid;
                Log.Line($"silence watch armed for engine pid {pid}");
            }
            if (firstAnswer is null)
            {
                firstAnswer = now;
                Log.Line($"engine answering A2S: {a.Version} on {a.Map}, {a.Humans} human(s)");
            }
            return;
        }
        silentSince ??= now;
        a2sFails++;
        if (!Config.FreezeRecovery || ArmedPid is null)
            return;
        if (Engine.RconAlive())
            (rconSince, rconFails) = (null, 0);
        else
        {
            rconSince ??= now;                         // the poll's start: A2S and RCON failed on the same polls
            rconFails++;
        }
    }

    /// <summary>One pass of the loop, everything but the sleep.</summary>
    public void TickOnce(double now)
    {
        ReadConsole(now);
        var suspect = signatureAt is not null;
        Poll(now);
        now = Now;                                     // a poll of a dead engine takes up to 2.5 s; judge on the clock after it
        VerifyPending(now);
        SendUpdated(now);
        ProbeConsole(now);
        try
        {
            CheckValve(now);
        }
        catch (Exception e)
        {
            Log.Once("valve", $"WARNING: the Valve check crashed ({e.GetType().Name}: {e.Message}) - no action");
        }
        try
        {
            CheckVersion(now);
        }
        catch (Exception e)
        {
            Log.Once("version", $"WARNING: the version check crashed ({e.GetType().Name}: {e.Message}) - no action");
        }
        TryFreeze(now);
        TrySilence(now);
        if (!suspect)
            TryPlanned(now);
        try
        {
            Workshop.Step(Now);
        }
        catch (Exception e)   // a bug here must never cost the restarts and the freeze recovery above
        {
            Log.Once("workshop", $"WARNING: the background download step crashed ({e.GetType().Name}: {e.Message}) - no action");
        }
    }

    /// <summary>One tick, paced to Tick. A tick that polled a dead engine already took longer than that, so the next poll follows at once:
    /// while silent the probes' own timeouts set the pace, never a spin. The scenario tests drive this very method.</summary>
    public double LoopOnce()
    {
        var now = Now;
        TickOnce(now);
        Sleep(Tick - (Now - now));
        return now;
    }

    /// <summary>How many verified manifests the Steam client starts with - whether this process's first download re-checks every installed
    /// item (~15 min, and the stuck state's only way in).</summary>
    void LogDepotcache()
    {
        var (count, mounted, writable) = Snapshots.Depotcache(Config.Paths.Depotcache);
        int? installed;
        try
        {
            installed = KeyValues.Installed(File.ReadAllText(Config.Paths.WorkshopAcf)).Count;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            installed = null;
        }
        Log.Line($"workshop: {(count?.ToString(CultureInfo.InvariantCulture) ?? "no")} manifests in {Config.Paths.Depotcache} for {(installed?.ToString(CultureInfo.InvariantCulture) ?? "?")} installed items "
                 + $"({(mounted ? "a mounted volume" : "NOT a mounted volume")}, {(writable ? "writable" : "NOT writable")})");
        if (!mounted || !writable)
            Log.Line($"WARNING: the depotcache {(!mounted ? "is not a mounted volume" : "is not writable for this user")} - a new container starts without the manifests, so its "
                     + "first download re-checks every installed item, the only way into the stuck state (mount it on a volume)");
        else if (count is { } c && installed is { } n && n > 0 && c < n / 2)
            Log.Line($"WARNING: the depotcache holds {c} manifests for {n} installed items - the first download in this process re-checks the rest (up to ~15 min)");
    }

    public void Run()
    {
        Start();
        var lastStatus = double.NegativeInfinity;
        while (!Stopping())
        {
            var now = LoopOnce();
            if (now - lastStatus >= OkRepeat)
            {
                lastStatus = now;
                Log.Line($"status: A2S {(A2sInfo is { } a ? $"answering ({a.Humans} human(s))" : "silent")}, console.log {(Console is not null ? "live" : "not proven")}, "
                         + $"silence watch {(ArmedPid is { } p ? $"armed (pid {p})" : "not armed")}, verdict {Verdict.Answer}, "
                         + $"{State.Restarts.Count(e => now - e.At < Rules.Window)} restart(s) in 24 h");
            }
        }
        Log.Line("stopping");
    }

    /// <summary>What the watcher says when it starts: its switches, its limits, the depotcache, any fault that disables the restarts.</summary>
    public void Start()
    {
        foreach (var w in Config.BootWarnings)
            Log.Line("WARNING: " + w);
        Log.Line($"started: updates {(Config.Updates ? "on" : "OFF")} (Valve check every {Words.Hm(Config.CheckEvery)}), "
                 + $"new versions {(Config.NewVersions ? InstalledNow is { } i ? $"on (installed {i.Fingerprint})" : "on, but no record of what install put in place - not detected" : "OFF")}, "
                 + $"daily restart {Config.Daily?.ToString() ?? "off"}, freeze recovery {(Config.FreezeRecovery ? "on" : "OFF")} (after {Config.FreezeGrace} s of silence: A2S after the error-map "
                 + $"signature, or A2S and RCON on {Rules.SilentPolls}+ polls each without it); planned restarts >= {Rules.PlannedMinGap / 60:0} min apart and <= {Rules.PlannedMaxPerDay} per 24 h, "
                 + $"freeze kills >= {Rules.FreezeMinGap / 60:0} min apart and <= {Rules.FreezeMaxPerDay} per 24 h");
        Log.Line("workshop: download restart " + (Config.DownloadRestart ? $"on (only at 0 humans, <= 1 per {Words.Hm(Rules.WorkshopRestartGap)} and {Rules.WorkshopRestartMax} per 24 h)" : "off")
                 + ", the queued maps in the background " + (Config.BackgroundDownloads ? Bridge.Address is { } addr ? $"on, reported to the bridge at {addr}" : "on, no bridge address" : "off")
                 + ", maps outside the keep set at engine start: " + (Config.MapsPreview
                     ? $"PREVIEWED only (maps.preview: nothing moved, taken in or deleted; the list in {Config.Paths.PreviewDir})"
                     : Config.MapsLayout == "off" ? "kept (maps.layout off)"
                     : Config.MapsDelete switch { "on" => "deleted", "dryrun" => "listed only (dry run)", _ => "kept" }));
        LogDepotcache();
        if (Blocked() is { } blocked)
            Log.Line($"ALERT: RESTARTS ARE DISABLED - {blocked}. Observing only.");
        foreach (var (at, what) in new[] { (State.UpdateTripped, "update"), (State.FreezeTripped, "freeze") })
            if (at != 0)
                Log.Line($"ALERT: the {what} breaker is latched since {LocalTime.Day(UnixTime.At(at), Zone)} {LocalTime.Clock(UnixTime.At(at), Zone)} - delete {Config.Paths.StateFile} to re-arm");
    }
}
