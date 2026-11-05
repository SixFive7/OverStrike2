using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>os2-sync &lt;command&gt;: the Workshop sync, in its own container - new Arms Race, Deathmatch and Wingman maps found, checked,
/// downloaded with SteamCMD's anonymous login, scanned and load-tested on a test copy of the server, and published into the bridge's data;
/// the Workshop's scores for the keep set; the keep set's maps handed over to the game server.</summary>
public static class Program
{
    const string Usage = """
        os2-sync <command> [options]
          serve                   the service: a run whenever nobody plays and there is work (or at once: <state>/run-now)
          once [JSON]             one run now, then exit (the same rules); JSON lowers its limits, e.g. '{"batch": 3}' (api_run_cap,
                                  max_pages, batch; publish false; discover false; api true or false; only_ids [...])
          status                  the Web API ledger's counters and the state's totals
          export                  what it has published, as entries a republish from the repository can merge, and the random queues
          doctor                  what this program sees: the settings as read, the paths
          version                 this program's version
        options:
          --settings PATH         the settings file (else OVERSTRIKE2_SETTINGS; else the file's text in OVERSTRIKE2_SETTINGS_CONTENT;
                                  else /etc/overstrike2/overstrike2.jsonc)
          --dry-run              publishing off and nothing written for anybody else: no file in the bridge's data (the status file goes
                                  to the state folder), nothing posted to the bridge, no mail; what it would publish goes to
                                  <state>/would-publish/. Its own folders and port unless given: <paths.syncState>/dry-run,
                                  <paths.syncWork>/dry-run, sync.testPort + 10
          --state DIR             the sync's state folder (else paths.syncState): its ledger, state and reports
          --work DIR              the sync's work folder (else paths.syncWork): its staging, test tree and hand-overs
          --test-port N           the test engine's port (else sync.testPort)
          --no-handover           no hand-overs to the game server in this run (sync.handover false)
        exit code 78: the settings file cannot be used (hud.addon is required): nothing done
        """;

    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>A dry run's test engine listens this far from sync.testPort, so it never meets the live sync's.</summary>
    public const int DryRunPortOffset = 10;

    public static int Main(string[] args)
    {
        var env = (Func<string, string?>)Environment.GetEnvironmentVariable;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        var command = args[0];
        string? settingsArg = null, stateArg = null, workArg = null, onceJson = null;
        int? testPort = null;
        bool dryRun = false, noHandover = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--settings" when i + 1 < args.Length:
                    settingsArg = args[++i];
                    break;
                case "--state" when i + 1 < args.Length:
                    stateArg = args[++i];
                    break;
                case "--work" when i + 1 < args.Length:
                    workArg = args[++i];
                    break;
                case "--test-port" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1024 and <= 65535:
                    testPort = port;
                    i++;
                    break;
                case "--dry-run" when command is "serve" or "once":
                    dryRun = true;
                    break;
                case "--no-handover" when command is "serve" or "once":
                    noHandover = true;
                    break;
                default:
                    if (command == "once" && onceJson is null && !args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        onceJson = args[i];
                        break;
                    }
                    Console.Error.WriteLine($"os2-sync: unknown argument '{args[i]}' for {command} - nothing done\n\n{Usage}");
                    return 2;
            }
        }
        if (command == "version")
        {
            Console.WriteLine($"os2-sync {Version} (.NET {Environment.Version}, {RuntimeInformation.RuntimeIdentifier})");
            if (Settings.Load(Part.Server, settingsArg ?? (env(Settings.PathVariable) is { Length: > 0 } given ? given : Settings.DefaultPath), env).Fatal is { } unusable)
                Console.WriteLine($"settings: NOT USABLE - {unusable} (os2-sync doctor says more)");
            return 0;
        }
        if (command is not ("serve" or "once" or "status" or "export" or "doctor"))
        {
            Console.Error.WriteLine($"os2-sync: unknown command '{command}' - nothing done\n\n{Usage}");
            return 2;
        }
        JsonObject overrides = [];
        if (onceJson is not null)
        {
            try
            {
                overrides = JsonNode.Parse(onceJson) as JsonObject ?? throw new FormatException("not an object");
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or FormatException)
            {
                Console.Error.WriteLine("os2-sync: once takes an optional JSON object of lower limits, e.g. '{\"batch\": 3}'");
                return 2;
            }
        }
        var settingsPath = settingsArg ?? (env(Settings.PathVariable) is { Length: > 0 } p ? p : Settings.DefaultPath);
        var settings = Settings.Load(Part.Server, settingsPath, env);
        var zone = LocalTime.Zone();
        var stoppable = StoppableClock.Instance;
        var clock = new SyncClock(stoppable, zone);
        var log = new SyncLog(clock);
        var cfg = Options(SyncConfig.From(settings, env), dryRun, stateArg, workArg, testPort, noHandover);
        if (command == "doctor")
            return Doctor(settings, cfg, zone);
        if (settings.Fatal is { } fatal)
        {
            log.Line($"ALERT: the settings file cannot be used ({fatal}) - nothing done: fix the file (docs/settings.md; os2-sync doctor says what this program sees)");
            return Settings.RefusedExit;
        }
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;                       // the run's clean-up runs: SteamCMD told to quit, the test engine stopped, the status idle
            stoppable.Stop();
        });
        var service = new Service(cfg, clock, new Seams(), log, new WebApiDoor(cfg.ApiGap, stoppable));
        try
        {
            switch (command)
            {
                case "status":
                    Console.WriteLine(service.StatusText());
                    return 0;
                case "export":
                    Console.WriteLine(service.Export());
                    return 0;
                case "once":
                    service.Once(overrides);
                    return 0;
                default:
                    service.Serve();
                    return 0;
            }
        }
        catch (StopRequested)
        {
            log.Line("stopped (SIGTERM)");
            return 0;
        }
        catch (Exception e) when (command is "status" or "export" && e is BadDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"os2-sync {command}: {e.Message}");
            return 1;
        }
    }

    /// <summary>The command line's options over the settings. A dry run never shares the live sync's ledger, staging, test tree or test port,
    /// even without --state, --work and --test-port: &lt;paths.syncState&gt;/dry-run, &lt;paths.syncWork&gt;/dry-run, sync.testPort + 10.</summary>
    public static SyncConfig Options(SyncConfig cfg, bool dryRun, string? state, string? work, int? testPort, bool noHandover)
    {
        if (dryRun)
        {
            cfg.DryRun = true;
            cfg.Publish = false;
            cfg.StateDir = Path.Combine(cfg.StateDir, "dry-run");
            cfg.WorkDir = Path.Combine(cfg.WorkDir, "dry-run");
            cfg.TestPort = cfg.TestPort + DryRunPortOffset <= 65535 ? cfg.TestPort + DryRunPortOffset : cfg.TestPort - DryRunPortOffset;
        }
        if (state is not null)
            cfg.StateDir = Path.GetFullPath(state);
        if (work is not null)
            cfg.WorkDir = Path.GetFullPath(work);
        if (testPort is { } tp)
            cfg.TestPort = tp;
        if (noHandover)
            cfg.Handover = false;
        return cfg;
    }

    /// <summary>What this program sees, for a person: the settings as read, the paths, the key, the bridge.</summary>
    static int Doctor(Settings settings, SyncConfig cfg, TimeZoneInfo zone)
    {
        var w = Console.Out;
        w.WriteLine($"os2-sync {Version} (.NET {Environment.Version}, {RuntimeInformation.RuntimeIdentifier}), local time zone {zone.Id}");
        w.WriteLine($"settings: {settings.Source}{(settings.Fatal is { } f ? " - NOT USABLE: " + f : "")}");
        foreach (var x in settings.Problems)
            w.WriteLine($"  problem: {x}");
        foreach (var x in settings.Warnings)
            w.WriteLine($"  warning: {x}");
        foreach (var line in settings.Describe())
            w.WriteLine($"  {line}");
        foreach (var b in cfg.BootWarnings.Where(b => !b.StartsWith("settings: ", StringComparison.Ordinal)))
            w.WriteLine($"warning: {b}");
        foreach (var (what, path) in new[] { ("state", cfg.StateDir), ("work", cfg.WorkDir), ("game (read-only)", cfg.GameDir), ("bridge data", cfg.BridgeData),
                                             ("hand-overs", cfg.HandoverDir), ("depotcache", cfg.Depotcache), ("SteamCMD", cfg.SteamCmd) })
            w.WriteLine($"path {what}: {path} {(Directory.Exists(path) || File.Exists(path) ? "(there)" : "(MISSING)")}");
        w.WriteLine($"Web API key: {(cfg.WebApiKey.Length > 0 ? "present" : "absent - discovery and the ratings are skipped")}");
        w.WriteLine($"bridge: {(cfg.BridgeUrl.Length > 0 && cfg.BridgeSecret.Length > 0 ? cfg.BridgeUrl : "no bridge.url / bridge.secret - the check's verdicts are not reported")}");
        w.WriteLine($"mail: {(cfg.SmtpHost.Length > 0 && cfg.MailFrom.Length > 0 && cfg.MailTo.Count > 0 ? $"{cfg.SmtpHost}:{cfg.SmtpPort}, to {cfg.MailTo.Count} recipient(s)" : "none configured")}");
        w.WriteLine($"the HUD item: {(cfg.HudAddon.Length > 0 ? cfg.HudAddon : "none (hud.addon)")}; excluded ids {cfg.Exclude.Count}, excluded authors {cfg.ExcludeCreators.Count}");
        w.WriteLine($"limits: {cfg}");
        return settings.Fatal is null ? 0 : 1;
    }
}
