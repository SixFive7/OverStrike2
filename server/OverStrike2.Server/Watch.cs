using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>THE RESTART WATCHER (os2-server watch): restarts the game server when it needs it, between matches, and never in a loop.
/// pre.sh starts it in the background right before the engine starts, and it lives as long as the container. Its jobs, each behind its
/// own switch (9 never restarts anything):
///   1. UPDATES (restarts.updates). The image's entry script runs SteamCMD only when the container starts, so a Valve patch left a server
///      rejecting every client for ~44 hours ("Bad host version"). Every restarts.updateCheckMinutes this asks Valve's UpToDateCheck
///      whether the installed build is current, and restarts the engine when it is not: at the end of the current match with players on,
///      at once with nobody on.
///   1b. NEW VERSIONS (restarts.newVersions). A deployed new version of our own parts - the plugins, this program, pre.sh, the mode cfgs,
///      the settings the engine side reads (Fingerprint) - restarts the engine the same way, once what is on disk has been still for
///      VersionSettle: at the end of the match with players on, at once with nobody on.
///      Both are announced: the bridge hears of the restart when it becomes due (POST /restart-notice, "match-end") and at the match end
///      (a countdown of RestartCountdown s, "now"), shows it to the players and holds its own map change; after the restart, once the
///      engine answers, it hears what changed (POST /updated). A bridge that does not take the notice: nobody is warned - the watcher
///      words nothing for players, and without the bridge the menu is dead anyway.
///   2. DAILY RESTART (restarts.daily, local time). Once a day, and only while nobody is on (A2S and RCON) - never a kick, never a match
///      end. With players on it waits; a day they stay on until DailyWindow after the slot has none. It is the moment the hand-overs are
///      taken in and the maps outside the keep set go (organize, at every engine start). A planned restart of another kind inside the
///      window counts as the day's.
///   3. FREEZE RECOVERY (restarts.freezeRecovery). A frozen engine never exits, so Docker never restarts it; this kills it after
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
///   6. DOWNLOAD RESTART (restarts.downloadRestart). An engine process whose Steam client has dropped every download source never gets one
///      back: every download ends "No Connection". Any download that says so - job 9's or a player's - marks the process broken at once,
///      and a restart is due only while nobody is on, at most one per 2 h and 3 per 24 h; past that it only warns. The Steam client's logs
///      are copied to update-watch/steamlogs/ the moment it is marked.
///   8. THE DOWNLOAD LINE (maps.downloadLine): DownloadLine.
///   9. THE QUEUED MAPS IN THE BACKGROUND (maps.backgroundDownloads): ReadyMaps.
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
/// means "no action", never "act".</summary>
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

    public static double Seconds(DateTimeOffset t) => (t.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;
    public double Now => Seconds(Clock.Now);
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
    /// ..."); chat gets it only where there is no HUD to show it.</summary>
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

    string? PatchVersion() => Versions.ReadPatchVersion(Config.Paths.SteamInf);

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

    // ------------------------------------------------------------------------------------------------------------ the previous restart

    /// <summary>Judge the restart a PREVIOUS run of the watcher triggered (it is recorded just before the container goes down, so the verdict
    /// belongs to the next start). A record from this very run is left for the next one.</summary>
    void VerifyPending(double now)
    {
        if (State.Pending is not { } pending || pending.At >= Started)
            return;
        if (firstAnswer is null)
        {
            if (now - pending.At > RecoverWithin && now - Started > RecoverWithin)
            {
                State.Pending = null;
                if (pending.Kind == "freeze")
                {
                    State.FreezeMisses++;
                    if (State.FreezeMisses >= Rules.FreezeMaxMisses)
                        State.FreezeTripped = (long)now;
                    Log.Line($"ALERT: the server has not answered A2S {Rules.Hms(now - pending.At)} after the freeze kill (miss {State.FreezeMisses} of {Rules.FreezeMaxMisses}"
                             + (State.FreezeTripped != 0 ? $", FREEZE BREAKER LATCHED - delete {Config.Paths.StateFile} to re-arm" : "") + ")");
                }
                else
                    Log.Line($"WARNING: the {pending.Kind} restart was not followed by an A2S answer within {Rules.Hms(RecoverWithin)}");
                Save();
            }
            return;
        }
        State.Pending = null;
        var took = (long)(firstAnswer.Value - pending.At);
        if (pending.Kind == "freeze")
        {
            State.FreezeMisses = 0;
            Log.Line($"freeze recovery worked: A2S answered {took} s after the kill");
        }
        else
        {
            if (took is > 0 and < 3600 && pending.Kind == "daily")
                State.Durations = [.. State.Durations.Append((int)took).TakeLast(5)];
            Log.Line($"measured: the {pending.Kind} restart took {took} s from quit to A2S answering (last [{string.Join(", ", State.Durations)}])");
        }
        var nowVersion = PatchVersion();
        if (pending.Kind == "update")
        {
            if (nowVersion is not null && nowVersion != pending.From)
            {
                State.UpdateMisses = 0;
                Log.Line($"update installed: PatchVersion {pending.From} -> {nowVersion}");
            }
            else
            {
                State.UpdateMisses++;
                var misses = State.UpdateMisses;
                if (misses >= Rules.UpdateMaxMisses)
                {
                    State.UpdateTripped = (long)now;
                    State.UpdateTrippedFor = pending.Required ?? 0;
                }
                else
                    State.UpdateRetryAfter = (long)now + (long)Rules.UpdateRetry[(int)Math.Min(misses, Rules.UpdateRetry.Length) - 1];
                Log.Line($"{(State.UpdateTripped != 0 ? "ALERT" : "WARNING")}: the update restart left PatchVersion on {nowVersion ?? "None"} (miss {State.UpdateMisses} of {Rules.UpdateMaxMisses})"
                         + (State.UpdateTripped != 0 ? $" - UPDATE BREAKER LATCHED for this build (a newer required build, or deleting {Config.Paths.StateFile}, re-arms it)" : ""));
            }
        }
        if (pending.Kind == "version")
        {
            if (InstalledNow is { } inst && inst.Fingerprint == pending.Source)
                Log.Line($"new version installed: {pending.Installed ?? "?"} -> {inst.Fingerprint}");
            else
            {
                State.VersionTrippedFor = pending.Source ?? "";
                Log.Line($"ALERT: the version restart did not install the new version {pending.Source} (installed now: {InstalledNow?.Fingerprint ?? "nothing recorded"}) - "
                         + "no restart for it again; a newer version re-arms it");
            }
        }
        if (pending.Kind != "freeze")
        {
            if (pending.From is not null && nowVersion is not null && nowVersion != pending.From)
                updated.Add(($"gg-updated\t1\tgame-update\t{pending.From}\t{nowVersion}\n", 0, now));
            if (pending.Installed is not null && InstalledNow is { } i2 && i2.Fingerprint != pending.Installed)
                updated.Add(($"gg-updated\t1\tour-update\t{pending.Installed}\t{i2.Fingerprint}\n", 0, now));
        }
        Save();
    }

    /// <summary>After a restart that changed something, the bridge hears what (POST /updated): it shows the players a short note.</summary>
    void SendUpdated(double now)
    {
        for (var i = 0; i < updated.Count; i++)
        {
            var (body, tries, next) = updated[i];
            if (now < next)
                continue;
            var (told, reply) = Bridge.Post("updated", body, NoticeTimeout);
            if (told == "ok" || told == "none" || tries + 1 >= UpdatedTries)
            {
                Log.Line(told == "ok" ? $"the bridge took the after-restart note ({body.Split('\t')[2]})" : told == "none" ? "no bridge to tell what the restart changed"
                         : $"WARNING: the bridge did not take the after-restart note {UpdatedTries} times ({reply}) - dropped");
                updated.RemoveAt(i--);
            }
            else
            {
                if (tries == 0)
                    Log.Line($"the bridge did not take the after-restart note ({body.Split('\t')[2]}: {reply}) - tried again every {UpdatedRetry:0} s, up to {UpdatedTries} times");
                updated[i] = (body, tries + 1, now + UpdatedRetry);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------ console.log

    void ReadConsole(double now)
    {
        foreach (var tail in Console is { } c ? [c] : tails)
        {
            foreach (var line in tail.Lines())
            {
                if (probeToken is not null && Console is null && line.Contains(probeToken, StringComparison.Ordinal))
                {
                    Console = tail;
                    Log.Line($"console.log is live: {tail.Path} (echo seen {(now - probeSent).ToString("0.0", CultureInfo.InvariantCulture)} s after sending)");
                    probeToken = null;
                }
                if (tail != Console)
                    continue;
                try
                {
                    Workshop.Line.ConsoleLine(line, now);
                }
                catch (Exception e)   // a bug in the line must never cost the freeze recovery below
                {
                    Log.Once("download-line", $"WARNING: the download line ({e.GetType().Name}: {e.Message}) - no line");
                }
                if (ConsoleLines.IsQuitEcho(line))
                {
                    if (ArmedPid is not null)
                        Log.Line("a quit/restart was sent over RCON - the silence watch stands down until the engine answers again");
                    ArmedPid = null;
                    continue;
                }
                if (ConsoleLines.IsFreezeSignature(line))
                {
                    if (signatureAt is null)
                        Log.Line($"freeze signature seen: {Clip(line.Trim(), 120)}");
                    signatureAt ??= now;
                    otherLevel = false;
                    continue;
                }
                if (ConsoleLines.LevelStarted(line) is { } level && level != "error")
                {
                    if (signatureAt is not null)
                        Log.Line($"a new map is loading after the freeze signature ({level}) - not a freeze after all");
                    (signatureAt, otherLevel) = (null, true);
                }
                if (ConsoleLines.IsGameOver(line))
                {
                    matchEnds.Add(now);
                    if (matchEnds.Count > 10)
                        matchEnds.RemoveAt(0);
                }
            }
        }
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>Prove a console.log is live: RCON `echo &lt;token&gt;` is logged (sv_logecho) and must show up in it.</summary>
    void ProbeConsole(double now)
    {
        if (Console is not null || ProbeTriesDone >= ProbeTries || now < probeNext || A2sInfo is null)
        {
            if (probeToken is not null && now - probeSent > 10)
            {
                probeToken = null;
                Log.Line($"console.log probe {ProbeTriesDone} of {ProbeTries}: the echo token did not appear within 10 s");
                if (ProbeTriesDone >= ProbeTries)
                    Log.Line($"ALERT: no console.log showed the echo token after {ProbeTries} tries - freeze recovery is OFF and planned restarts wait for an empty "
                             + "server. Check the engine's -condebug and sv_logecho 1 (the image's CS2_ADDITIONAL_ARGS and CS2_LOG_ECHO).");
            }
            return;
        }
        ProbeTriesDone++;
        probeNext = now + ProbeRetry;
        probeToken = "os2-watch-probe-" + RandomNumberGenerator.GetHexString(12, true);
        probeSent = now;
        if (!Engine.Rcon("echo " + probeToken).Opened)
        {
            probeToken = null;
            // The session never opened, so nothing was echoed: that says nothing about console.log. At boot the first probe lands about a
            // second after A2S answers, before RCON listens, and counting it would leave freeze recovery off for the retry after every start.
            if (ProbeRconMissesDone < ProbeRconMisses)
            {
                ProbeRconMissesDone++;
                ProbeTriesDone--;
                probeNext = now + ProbeRconRetry;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------ the decisions

    void CheckValve(double now)
    {
        if (!Config.Updates)
            return;
        var dotted = PatchVersion();
        var vint = Versions.VersionInt(dotted);
        if (vint is not { } v)
        {
            Log.Once("valve", $"WARNING: steam.inf PatchVersion is {(dotted is null ? "None" : $"'{dotted}'")} - cannot ask Valve, taking no action");
            return;
        }
        if (A2sInfo is { } a && a.Version != dotted && Versions.IsDotted(a.Version))
        {
            Log.Once("valve", $"WARNING: A2S reports {a.Version} but steam.inf says {dotted} - not deciding on a disagreement");
            return;
        }
        if (v == verdictBuild && now - VerdictAt < Config.CheckEvery + checkBackoff)
            return;
        var answer = Valve.Check(v);
        (Verdict, verdictBuild, VerdictAt) = (answer, v, now);
        if (answer.Answer == "unknown")
        {
            checkBackoff = Math.Min(21600, Math.Max(Config.CheckEvery, checkBackoff * 2));
            Log.Once("valve", $"WARNING: cannot establish whether {dotted} ({v}) is current - {answer.Why}. No action; next try in {Rules.Hms(Config.CheckEvery + checkBackoff)}");
        }
        else
        {
            checkBackoff = 0;
            if (answer.Answer == "stale")
                Log.Once("valve", $"Valve reports {dotted} ({v}) as OUT OF DATE, required {answer.Required}");
            else
                Log.Once("valve", $"build {dotted} ({v}) is current", OkRepeat);
        }
    }

    /// <summary>Our own parts on disk, against what install put in place: a new version once it has been still for VersionSettle.</summary>
    void CheckVersion(double now)
    {
        if (!Config.NewVersions || InstalledNow is not { } inst || now < versionCheckAt)
            return;
        versionCheckAt = now + VersionCheckEvery;
        try
        {
            var sig = Fingerprint.Signature(Config.Paths, inst.ServerFrom, SettingsPath);
            if (sig != versionSig || OnDisk is null)
            {
                OnDisk = Fingerprint.Compute(Config.Paths, inst.ServerFrom, SettingsPath, Env);
                (versionSig, versionSigAt) = (sig, now);   // a change (or the first look): it must be still for VersionSettle
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            versionUnread = true;
            Log.Once("version", $"WARNING: our parts on disk cannot be read ({e.Message}) - no decision on a new version");
            return;
        }
        versionUnread = false;
        var disk = OnDisk!;
        if (disk.Total == inst.Fingerprint)
        {
            if (VersionDue is not null)
                Log.Line($"what is on disk is the installed version again ({inst.Fingerprint}) - no version restart due");
            VersionDue = null;
            return;
        }
        var changed = string.Join(", ", disk.Changed(inst.Parts));
        if (now - versionSigAt < VersionSettle)
        {
            if (VersionDue is { } waiting)
                Log.Line($"our parts are being written again ({changed}) - the restart for {waiting.Total} is off until what is on disk is still");
            VersionDue = null;                         // a match end now would quit into a half-written deploy
            Log.Once("version", $"a new version of our parts is being written ({changed}) - waiting until it is still for {VersionSettle:0} s", 600);
            return;
        }
        if (VersionDue?.Total != disk.Total)
        {
            VersionDue = disk;
            Log.Line($"a new version of our parts is on disk: {changed} (installed {inst.Fingerprint}, on disk {disk.Total})");
        }
    }

    /// <summary>Whether our parts are being written right now, looked at afresh: a deploy that began after CheckVersion's last look counts too.
    /// A quit now would have install copy a half-written plugin folder, and the check after the restart would raise a false alarm. Parts that
    /// cannot be read are no deploy: nothing is decided on them, and they hold no other restart back.</summary>
    bool Deploying(double now)
    {
        if (!Config.NewVersions || InstalledNow is not { } inst || versionUnread)
            return false;
        try
        {
            if (Fingerprint.Signature(Config.Paths, inst.ServerFrom, SettingsPath) != versionSig)
            {
                versionCheckAt = 0;                    // CheckVersion looks at it on the next tick
                return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return OnDisk is { } disk && disk.Total != inst.Fingerprint && now - versionSigAt < VersionSettle;
    }

    string? PlannedKind(double now)
    {
        if (Config.Updates && Verdict.Answer == "stale" && verdictBuild == Versions.VersionInt(PatchVersion()))
        {
            var required = Verdict.Required ?? 0;
            if (State.UpdateTripped != 0 && required > State.UpdateTrippedFor)
            {
                Log.Line($"Valve now requires {required}, newer than the {State.UpdateTrippedFor} the update breaker latched on - re-arming it");
                (State.UpdateTripped, State.UpdateTrippedFor, State.UpdateMisses, State.UpdateRetryAfter) = (0, 0, 0, 0);
                Save();
            }
            if (State.UpdateTripped != 0)
                Log.Once("breaker", $"ALERT: the build is stale (Valve requires {required}) but the update breaker is latched for it - not restarting. Delete {Config.Paths.StateFile} to re-arm.");
            else if (now >= State.UpdateRetryAfter)
                return "update";
        }
        if (Config.NewVersions && VersionDue is { } v)
        {
            if (v.Total != State.VersionTrippedFor)
                return "version";
            Log.Once("version-breaker", $"ALERT: a new version ({v.Total}) is on disk, but a restart did not install it before - not restarting for it again; a newer one re-arms");
        }
        if (Rules.DailyDue(now, Config.Daily, Zone, State.DoneDay))
            return "daily";
        if (Config.DownloadRestart && Workshop.Broken is { } broken)
        {
            if (Rules.WorkshopBlock(State, now, Zone) is { } why)
            {
                Log.Once("workshop-limit", $"WARNING: workshop downloads are broken in this engine process ({broken.Why}) - NOT restarting: {why}. Only warning.", 3600);
                return null;
            }
            return "workshop";
        }
        return null;
    }

    /// <summary>Tell the bridge of a restart: "match-end" when it becomes due (it waits for the end of the current match), "now" with the
    /// seconds left before the quit. True when the bridge took it (it shows the players; it holds its own map change).</summary>
    bool Announce(string kind, string when, int seconds, double now)
    {
        var (told, reply) = Bridge.Post("restart-notice", $"gg-restart\t1\t{Kinds.Wire(kind)}\t{when}\t{seconds}\n", NoticeTimeout);
        if (told == "ok")
        {
            announced = kind;
            Log.Once("announce", $"the bridge took the {kind} restart's notice ({when}{(when == "now" ? $", {seconds} s" : "")})", 60);
            return true;
        }
        if (told == "failed")
            Log.Once("announce-failed", $"WARNING: the bridge did not take the {kind} restart's notice ({reply})", 900);
        return false;
    }

    void CancelAnnounced()
    {
        if (announced is not { } kind)
            return;
        announced = null;
        Bridge.Post("restart-notice", $"gg-restart\t1\tcancel\t{Kinds.Wire(kind)}\n", NoticeTimeout);
        Log.Line($"the bridge was told that the {kind} restart is off");
    }

    void TryPlanned(double now)
    {
        var kind = PlannedKind(now);
        if (kind is null)
        {
            if (Due is { } d)
            {
                Log.Line($"the {d.Kind} restart is no longer due (window passed or build current) - standing down");
                CancelAnnounced();
            }
            (Due, warned, noticeFailing, CountdownUntil) = (null, false, null, null);
            return;
        }
        if (Due is not { } due || due.Kind != kind)
        {
            if (Due is not null)
                CancelAnnounced();                     // another kind took its place
            (Due, warned, CountdownUntil) = ((kind, now), false, null);
            Log.Line($"{kind} restart due");
        }
        var blockedWhy = Blocked();
        var block = blockedWhy is null ? Rules.PlannedBlock(State, now, Zone) : (true, blockedWhy);
        if (block is { } b)
        {
            Log.Once("planned", $"{(b.Alert ? "ALERT: " : "")}{kind} restart due but {(b.Alert ? "blocked" : "deferred")}: {b.Why}");
            return;
        }
        if (A2sInfo is not { } a2s)
        {
            Log.Once("planned", $"{kind} restart due, but the engine is not answering A2S - waiting");
            return;
        }
        if (CountdownUntil is { } until)
        {
            if (now >= until)
            {
                CountdownUntil = null;
                if (Deploying(now) || !Quit(kind, now, null))   // the bridge showed the countdown and the last line
                {
                    Announce(kind, "match-end", RestartCountdown, now);   // it did not go: it waits for the next match end again
                    announceTried = now;
                }
            }
            return;
        }
        if (Deploying(now))
        {
            Log.Once("planned", $"{kind} restart due, but a new version of our parts is being written - it waits until what is on disk is still");
            return;
        }
        var final = kind == "workshop" ? Rules.WorkshopLine(Rules.BackIn(State.Durations)) : null; // the download restart's line for the log, at an empty server
        if (Kinds.EmptyOnly.Contains(kind) && a2s.Humans > 0)
        {
            var what = kind == "workshop" ? $"downloads broken: {Workshop.Broken?.Why}"
                : $"the daily one, until {AtClock((Rules.NextSlot(now - Rules.DailyWindow, Config.Daily, Zone) ?? now) + Rules.DailyWindow)} at the latest";
            Log.Once("planned", $"{kind} restart due ({what}) but {a2s.Humans} human(s) on - waiting for an empty server, never a kick", 900);
            return;
        }
        if (a2s.Humans == 0)
        {
            if (now < noticeRetry)
                return;                                // the quit notice is tried again then
            var humans = RconHumans();
            if (humans == 0)
            {
                Announce(kind, "now", 0, now);
                Quit(kind, now, final);
            }
            else
                Log.Once("planned", $"A2S says the server is empty but RCON status says {(humans?.ToString(CultureInfo.InvariantCulture) ?? "None")} - not restarting on a disagreement");
            return;
        }
        if (Console is null)
        {
            Log.Once("planned", $"{kind} restart due with {a2s.Humans} player(s) on, but no live console.log to see the match end - waiting until the server is empty");
            return;
        }
        if (!warned)
        {
            Announce(kind, "match-end", RestartCountdown, now);   // a bridge that does not take it: nobody is warned
            (warned, announceTried) = (true, now);
        }
        else if (now - announceTried >= AnnounceRefresh)
        {
            Announce(kind, "match-end", RestartCountdown, now);       // again: a bridge swapped (or back) since knows it too
            announceTried = now;
        }
        var since = Due!.Value.Since;
        if (matchEnds.Any(t => t >= since) && matchEnds[^1] >= now - 20 && matchEnds[^1] > usedMatchEnd)
        {
            usedMatchEnd = matchEnds[^1];
            if (Announce(kind, "now", RestartCountdown, now))
            {
                CountdownUntil = now + RestartCountdown;
                Log.Line($"{kind} restart: the match is over - the bridge counts down {RestartCountdown} s, then the quit");
            }
            else
                Quit(kind, now, null);                 // no bridge to count down: the quit, unannounced
        }
        else
            Log.Once("planned", $"{kind} restart due, waiting for the end of the current match ({a2s.Humans} player(s))", 900);
    }

    /// <summary>Count a restart on disk BEFORE it happens - the container goes down seconds later. The in-memory state only changes if the
    /// write succeeded, so a failed write can never leave a half-counted restart behind.</summary>
    bool Commit(double now, string kind, int? required = null)
    {
        var fresh = State.Copy();
        fresh.Restarts = [.. fresh.Restarts.Where(e => now - e.At >= 0 && now - e.At < Rules.Window), new Restart((long)now, kind)];
        fresh.Pending = new Pending(kind, (long)now, kind != "freeze" ? PatchVersion() : null, required,
                                    kind != "freeze" ? InstalledNow?.Fingerprint : null, kind != "freeze" ? OnDisk?.Total : null);
        if (kind != "freeze" && Rules.DailyDue(now, Config.Daily, Zone, fresh.DoneDay))   // any quit inside the window is the day's restart:
            fresh.DoneDay = Rules.LocalDay(Rules.NextSlot(now - Rules.DailyWindow, Config.Daily, Zone)!.Value, Zone);   // the slot's day
        var previous = State;
        State = fresh;
        if (Save())
            return true;
        State = previous;
        Log.Line($"ALERT: {kind} restart abandoned - the counters could not be written");
        return false;
    }

    (string Told, string? Why) QuitNotice(string kind, bool cancel = false) =>
        Bridge.Post("quitting", $"gg-quit\t1\t{(cancel ? "cancel\t" + kind : kind)}\n", NoticeTimeout);

    /// <summary>The only place a planned restart happens. The bridge hears of it first: before the counters, so a notice it does not take
    /// costs no restart, and a restart that does not happen after it is taken back. True when the quit went out.</summary>
    bool Quit(string kind, double now, string? finalLine)
    {
        if (now < noticeRetry)
            return false;
        var (told, why) = QuitNotice(kind);
        if (told == "failed")
        {
            noticeFailing ??= now;
            if (now - noticeFailing >= QuitNoticePatience)
                Log.Line($"WARNING: the bridge has not taken the quit notice for {Rules.Hms(now - noticeFailing.Value)} ({why}) - the {kind} restart goes ahead without it: a false crash mark can be cleared");
            else
            {
                noticeRetry = now + QuitNoticeRetry;
                Log.Once("quit-notice", $"WARNING: the bridge did not take the quit notice ({why}) - not quitting yet: without it the bridge would book a crash on an innocent map "
                                        + $"(the quit's echo dies with the process). The {kind} restart stays due; the notice is tried again every {QuitNoticeRetry:0} s, and the "
                                        + $"restart goes ahead without it after {Rules.Hms(QuitNoticePatience)}", 900);
                return false;
            }
        }
        noticeFailing = null;
        if (!Commit(now, kind, kind == "update" ? Verdict.Required : null))
        {
            if (told == "ok")
                QuitNotice(kind, cancel: true);
            CancelAnnounced();
            return false;
        }
        if (!string.IsNullOrEmpty(finalLine))
        {
            Notice(finalLine, 30);
            Sleep(QuitLead);
        }
        var planned = State.Restarts.Count(e => e.Kind != "freeze" && now - e.At < Rules.Window);
        var bridgeWords = told switch
        {
            "ok" => $"The bridge took the quit notice: {why}.",
            "none" => "No bridge to tell: the echo alone.",
            _ => $"The bridge did not take the quit notice: {why}.",
        };
        Log.Line($"RESTARTING NOW ({kind}): RCON quit, so the image's entry script ends and Docker runs it again (SteamCMD runs again). {planned} planned restart(s) in 24 h. {bridgeWords}");
        var pid = Engine.Pid();
        GoingDown = now;                               // from here on the silence is ours: the silence path must not "rescue" it
        if (!Engine.Rcon("quit", expectReply: false).Opened)
        {
            GoingDown = null;                          // never force an undelivered quit: the engine is not going down
            if (told == "ok")
                QuitNotice(kind, cancel: true);
            CancelAnnounced();
            Log.Line($"WARNING: RCON quit could not be delivered - not forcing it; the {kind} restart stays due and is retried after the {Rules.PlannedMinGap / 60:0} min floor");
            return false;
        }
        (Due, warned, announced) = (null, false, null);   // done: no "no longer due" line while the container goes down
        for (var i = 0; i < QuitTimeout * 2; i++)
        {
            if (Engine.Pid() is null)
                return true;
            Sleep(0.5);
        }
        if (pid is { } p && Engine.Pid() == p)
            Engine.Kill(p, $"it was still alive {QuitTimeout} s after `quit`");
        return true;
    }

    /// <summary>Path a: the error-map signature, confirmed by A2S silence.</summary>
    void TryFreeze(double now)
    {
        if (!Config.FreezeRecovery || signatureAt is not { } sig)
            return;
        var pid = Engine.Pid();
        if (!Rules.FreezeConfirmed(now, sig, silentSince, pid is not null, otherLevel, Config.FreezeGrace))
        {
            if (silentSince is null && now - sig > 60)
            {
                Log.Line("the freeze signature was followed by A2S answering - not a freeze; standing down");
                signatureAt = null;
            }
            return;
        }
        Kill(now, pid!.Value, $"error-map freeze: signature {now - sig:0} s ago, A2S silent for {now - silentSince!.Value:0} s, no other map loading since",
             $"frozen engine (signature {now - sig:0} s ago, A2S silent {now - silentSince.Value:0} s)");
    }

    /// <summary>Path b: silence alone - A2S and RCON both dead for the grace - for the freezes that print nothing.</summary>
    void TrySilence(double now)
    {
        if (!Config.FreezeRecovery || silentSince is not { } silent || now - silent < Config.FreezeGrace)
            return;
        var pid = Engine.Pid();
        var armed = Console is not null && pid is not null && pid == ArmedPid;
        if (!Rules.SilenceConfirmed(now, silentSince, a2sFails, rconSince, rconFails, armed, pid is not null, GoingDown is not null, Config.FreezeGrace))
        {
            var why = GoingDown is not null ? "the watcher is taking it down itself"
                : pid is null ? "no engine process"
                : Console is null ? "no live console.log, so a quit or restart could not be told apart"
                : pid != ArmedPid ? "this engine process has not answered A2S since it started, or a quit/restart was sent"
                : rconSince is null ? "RCON still answers"
                : Math.Min(a2sFails, rconFails) < Rules.SilentPolls ? $"{a2sFails} A2S and {rconFails} RCON polls failed so far, {Rules.SilentPolls} needed"
                : $"RCON silent since {AtClock(rconSince.Value)} only";
            Log.Once("silent", $"A2S silent since {AtClock(silent)} - not killing: {why}", 300);
            return;
        }
        Kill(now, pid!.Value, $"silent freeze: A2S silent for {now - silent:0} s ({a2sFails} polls) and RCON for {now - rconSince!.Value:0} s ({rconFails} polls), no error-map signature",
             $"silent engine (A2S {now - silent:0} s, RCON {now - rconSince.Value:0} s)");
    }

    /// <summary>The one place a freeze kill happens, for both paths: the same gates, the same counters, the same record.</summary>
    void Kill(double now, int pid, string why, string what)
    {
        var blockedWhy = Blocked();
        var block = blockedWhy is null ? Rules.FreezeBlock(State, now, Zone) : (true, blockedWhy);
        if (block is { } b)
        {
            Log.Once("freeze", $"{(b.Alert ? "ALERT: " : "")}{what} but {(b.Alert ? "not killing it" : "waiting")}: {b.Why}");
            return;
        }
        if (!Commit(now, "freeze"))
            return;
        GoingDown = now;
        Engine.Kill(pid, why);
        signatureAt = null;
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
        Log.Line($"started: updates {(Config.Updates ? "on" : "OFF")} (Valve check every {Rules.Hms(Config.CheckEvery)}), "
                 + $"new versions {(Config.NewVersions ? InstalledNow is { } i ? $"on (installed {i.Fingerprint})" : "on, but no record of what install put in place - not detected" : "OFF")}, "
                 + $"daily restart {Config.Daily?.ToString() ?? "off"}, freeze recovery {(Config.FreezeRecovery ? "on" : "OFF")} (after {Config.FreezeGrace} s of silence: A2S after the error-map "
                 + $"signature, or A2S and RCON on {Rules.SilentPolls}+ polls each without it); planned restarts >= {Rules.PlannedMinGap / 60:0} min apart and <= {Rules.PlannedMaxPerDay} per 24 h, "
                 + $"freeze kills >= {Rules.FreezeMinGap / 60:0} min apart and <= {Rules.FreezeMaxPerDay} per 24 h");
        Log.Line("workshop: download restart " + (Config.DownloadRestart ? $"on (only at 0 humans, <= 1 per {Rules.Hms(Rules.WorkshopRestartGap)} and {Rules.WorkshopRestartMax} per 24 h)" : "off")
                 + ", the queued maps in the background " + (Config.BackgroundDownloads ? Bridge.Address is { } addr ? $"on, reported to the bridge at {addr}" : "on, no bridge address" : "off")
                 + ", maps outside the keep set at engine start: " + Config.MapsDelete switch { "on" => "deleted", "dryrun" => "listed only (dry run)", _ => "kept" });
        LogDepotcache();
        if (Blocked() is { } blocked)
            Log.Line($"ALERT: RESTARTS ARE DISABLED - {blocked}. Observing only.");
        foreach (var (at, what) in new[] { (State.UpdateTripped, "update"), (State.FreezeTripped, "freeze") })
            if (at != 0)
                Log.Line($"ALERT: the {what} breaker is latched since {LocalTime.Day(Rules.At(at), Zone)} {LocalTime.Clock(Rules.At(at), Zone)} - delete {Config.Paths.StateFile} to re-arm");
    }
}
