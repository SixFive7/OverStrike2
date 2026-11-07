using System.Runtime.InteropServices;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>os2-server &lt;command&gt;: the game server's own program, in its container beside the engine.</summary>
public static class Program
{
    const string Usage = """
        os2-server <command> [options]
          install [--from PATH]   the plugin layer, the HUD addon's delivery and the maps folder, before the engine starts (what pre.sh
                                  runs); PATH is this program's own file as shipped, which the watcher watches for a new version
          organize                the maps folder, the hand-over and the deletion
          watch                   the restart watcher, for the life of the container (pre.sh starts it in the background)
          doctor                  what this program sees: the settings, the paths, the versions, the watcher's state
          version                 this program's version
        options:
          --settings PATH         the settings file (else OVERSTRIKE2_SETTINGS; else the file's text in OVERSTRIKE2_SETTINGS_CONTENT;
                                  else /etc/overstrike2/overstrike2.jsonc)
        exit code 78: the settings cannot be used (hud.addon is required), or install found no MultiAddonManager: pre.sh does not start the engine
        """;

    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";

    public static int Main(string[] args)
    {
        var env = (Func<string, string?>)Environment.GetEnvironmentVariable;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            System.Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        var command = args[0];
        string? settingsArg = null, from = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--settings" when i + 1 < args.Length:
                    settingsArg = args[++i];
                    break;
                case "--from" when i + 1 < args.Length && command == "install":
                    from = args[++i];
                    break;
                default:
                    // never the watcher's loop in the foreground of pre.sh, which waits for it before the engine starts
                    System.Console.Error.WriteLine($"os2-server: unknown argument '{args[i]}' for {command} - nothing done\n\n{Usage}");
                    return 2;
            }
        }
        if (command == "version")
        {
            System.Console.WriteLine($"os2-server {Version} (.NET {Environment.Version}, {RuntimeInformation.RuntimeIdentifier})");
            if (Settings.Load(Part.Server, Settings.PathFor(settingsArg, env), env).Fatal is { } unusable)
                System.Console.WriteLine($"settings: NOT USABLE - {unusable} (os2-server doctor says more)");
            return 0;
        }
        if (command is not ("install" or "organize" or "watch" or "doctor"))
        {
            System.Console.Error.WriteLine($"os2-server: unknown command '{command}' - nothing done\n\n{Usage}");
            return 2;
        }
        var settingsPath = Settings.PathFor(settingsArg, env);
        var settings = Settings.Load(Part.Server, settingsPath, env);
        var zone = LocalTime.Zone();
        var clock = SystemClock.Instance;
        var log = new Log($"os2-server {command}", clock, zone);
        if (command == "doctor")
            return Doctor(settings, settingsPath, env, zone);
        if (settings.Fatal is { } fatal)   // no file, a broken one or no hud.addon in it: whichever, there is no HUD to give the players
        {
            log.Line($"ALERT: the settings file cannot be used ({fatal}) - a server without hud.addon cannot exist, so nothing is done{(command == "install" ? " and the engine does not start" : "")}: "
                     + "fix the file (docs/settings.md; os2-server doctor says what this program sees)");
            return Settings.RefusedExit;
        }
        var config = ServerConfig.From(settings, env);
        try
        {
            switch (command)
            {
                case "install":
                    foreach (var w in config.BootWarnings)
                        log.Line("WARNING: " + w);
                    return new Install(config, log, new HttpDownloader(clock), env, clock, zone).Run(from, settingsPath);
                case "organize":
                    log.Line(new MapsFolder(config, log, clock, zone).Organize());
                    return 0;
                default:
                    return WatchMain(config, settingsPath, env, clock, zone, log);
            }
        }
        catch (Exception e)   // never die silently: the 44-hour outage happened because nobody was told
        {
            log.Line($"ALERT: os2-server {command} crashed{(command == "watch" ? " - no update checks, daily restarts or freeze recovery until the container restarts" : "")} ({e.GetType().Name}: {e.Message})");
            throw;
        }
    }

    /// <summary>Whether every job of the watcher's loop is switched off: the restarts, and the Workshop jobs (the download restart, the background
    /// downloads, the download line), which run beside them.</summary>
    public static bool NothingToDo(ServerConfig c) =>
        !(c.Updates || c.NewVersions || c.FreezeRecovery || c.Daily is not null || c.DownloadRestart || c.BackgroundDownloads || c.DownloadLine);

    static int WatchMain(ServerConfig config, string settingsPath, Func<string, string?> env, IClock clock, TimeZoneInfo zone, Log log)
    {
        if (NothingToDo(config))
        {
            log.Line("restarts.updates, restarts.newVersions, restarts.freezeRecovery, restarts.downloadRestart, maps.backgroundDownloads and maps.downloadLine false and restarts.daily off: nothing to do, exiting");
            return 0;
        }
        using var stop = new CancellationTokenSource();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            stop.Cancel();
        });
        FileStream? lockFile = null;
        if (WatchState.PersistenceSelftest(config.Paths.StateDir) is null)
        {
            try
            {
                lockFile = new FileStream(config.Paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                log.Line($"another watcher already holds {config.Paths.LockFile}, exiting");
                return 0;
            }
        }
        using (lockFile)
        {
            var engine = new LocalEngine(config.Port, config.RconPassword, clock, log);
            var watch = new Watch(config, clock, zone, log, engine, new HttpBridge(config.Bridge), new ValveApi(new WebApiDoor(config.ApiGap, clock)))
            {
                SettingsPath = settingsPath,
                Env = env,
                Stopping = () => stop.IsCancellationRequested,
            };
            watch.Run();
        }
        return 0;
    }

    /// <summary>What this program sees, for a person: the settings as read, the paths, the versions, the watcher's state.</summary>
    static int Doctor(Settings settings, string settingsPath, Func<string, string?> env, TimeZoneInfo zone)
    {
        var w = System.Console.Out;
        w.WriteLine($"os2-server {Version} (.NET {Environment.Version}, {RuntimeInformation.RuntimeIdentifier}), local time zone {zone.Id}");
        w.WriteLine($"settings: {settings.Source}{(settings.Fatal is { } f ? $" - NOT USABLE: {f}; install and watch refuse (exit {Settings.RefusedExit}), and pre.sh does not start the engine" : "")}");
        foreach (var p in settings.Problems)
            w.WriteLine($"  problem: {p}");
        foreach (var x in settings.Warnings)
            w.WriteLine($"  warning: {x}");
        foreach (var line in settings.Describe())
            w.WriteLine($"  {line}");
        var config = ServerConfig.From(settings, env);
        foreach (var b in config.BootWarnings.Where(b => !b.StartsWith("settings: ", StringComparison.Ordinal)))
            w.WriteLine($"warning: {b}");
        var p2 = config.Paths;
        foreach (var (what, path) in new[] { ("game", p2.GameDir), ("bridge data", p2.BridgeData), ("hand-overs", p2.Handover), ("depotcache", p2.Depotcache), ("plugins", p2.Plugins), ("state", p2.StateDir) }
                     .Concat(config.MapsLayout == "preview" ? [("preview (maps.layout preview)", p2.PreviewDir)] : []))
            w.WriteLine($"path {what}: {path} {(Directory.Exists(path) ? "(there)" : "(MISSING)")}");
        w.WriteLine($"bridge: {(config.Bridge is { } br ? br.Base : "none (bridge.secret is empty)")}");
        w.WriteLine($"RCON password: {(config.RconPassword.Length > 0 ? "set" : "EMPTY - no restart can happen")}");
        var installed = Installed.Read(p2.InstalledFile);
        w.WriteLine(installed is { } i
            ? $"installed version: {i.Fingerprint} at {LocalTime.Clock(DateTimeOffset.FromUnixTimeSeconds(i.At), zone)} (this program from {i.ServerFrom ?? "?"})"
            : "installed version: no record (install has not run in this container)");
        try
        {
            var disk = Fingerprint.Compute(p2, installed?.ServerFrom, settingsPath, env);
            w.WriteLine($"on disk now: {disk.Total}" + (installed is { } i2 && i2.Fingerprint != disk.Total ? $" - NEW: {string.Join(", ", disk.Changed(i2.Parts))} changed" : installed is null ? "" : " - the installed one"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            w.WriteLine($"on disk now: cannot be read ({e.Message})");
        }
        var (state, fault) = WatchState.Load(p2.StateFile);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        w.WriteLine(fault is not null ? $"watcher state: DAMAGED - {fault}"
            : $"watcher state: {state.Restarts.Count(r => now - r.At < Rules.Window)} restart(s) in 24 h ({string.Join(", ", state.Restarts.Where(r => now - r.At < Rules.Window).Select(r => r.Kind))}), "
              + $"daily done {(state.DoneDay.Length > 0 ? state.DoneDay : "never")}, update breaker {(state.UpdateTripped != 0 ? "LATCHED" : "armed")}, freeze breaker {(state.FreezeTripped != 0 ? "LATCHED" : "armed")}"
              + (state.Pending is { } pd ? $", pending: {pd.Kind} at {LocalTime.Clock(DateTimeOffset.FromUnixTimeSeconds(pd.At), zone)}" : ""));
        return settings.Fatal is null ? 0 : 1;
    }
}
