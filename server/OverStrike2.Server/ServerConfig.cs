using System.Text.RegularExpressions;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>Where the program finds and keeps its files, inside the game server's container. All of it follows from the settings file's
/// paths.* keys; a test builds one on a folder of its own.</summary>
public sealed record ServerPaths
{
    public required string GameDir { get; init; }          // the image's STEAMAPPDIR: the game install, on the game's volume
    public required string BridgeData { get; init; }       // the bridge's data, read-only here: the lists, votes.json, queues.json ...
    public required string Handover { get; init; }         // the sync's hand-overs, inside GameDir: a take-in is one rename
    public required string Depotcache { get; init; }       // the Steam client's verified manifests (~/Steam/depotcache)
    public required string Plugins { get; init; }          // the CounterStrikeSharp plugins to install, one folder each (read-only)
    public string? State { get; init; }                    // the state folder, when it is not GameDir/update-watch (an observing run's)
    public string? Logs { get; init; }                     // the Steam client's logs, when they are not beside the depotcache

    public string StateDir => State ?? Path.Combine(GameDir, "update-watch");   // the counters, the records, the lists of deletions
    public string StateFile => Path.Combine(StateDir, "state.json");
    public string LockFile => Path.Combine(StateDir, "lock");
    public string InstalledFile => Path.Combine(GameDir, "update-watch", "installed.json");   // what `install` put in place for this engine
    public string KeepFile => Path.Combine(StateDir, "keep.json");
    public string SnapshotDir => Path.Combine(StateDir, "steamlogs");
    public string Csgo => Path.Combine(GameDir, "game", "csgo");
    public string SteamInf => Path.Combine(Csgo, "steam.inf");
    public string WorkshopDir => Path.Combine(GameDir, "game", "bin", "linuxsteamrt64", "steamapps", "workshop");
    public string WorkshopAcf => Path.Combine(WorkshopDir, "appworkshop_730.acf");
    public string Content => Path.Combine(WorkshopDir, "content", "730");
    public string MapsDir => Path.Combine(GameDir, "maps");
    public string SteamLogs => Logs ?? Path.Combine(Path.GetDirectoryName(Depotcache.TrimEnd('/', '\\')) ?? Depotcache, "logs");
    public string WorkshopLog => Path.Combine(SteamLogs, "workshop_log.txt");
    public string PreSh => Path.Combine(GameDir, "pre.sh");
    public string CfgDir => Path.Combine(Csgo, "cfg");

    /// <summary>Where -condebug writes console.log: the FIRST writable game search path. With the plugin layer on, csgo/addons/metamod heads
    /// gameinfo.gi's list, so the log lands there; vanilla falls through to csgo/console.log.</summary>
    public string[] ConsoleCandidates =>
    [
        Path.Combine(Csgo, "addons", "metamod", "console.log"),
        Path.Combine(Csgo, "console.log"),
        Path.Combine(GameDir, "game", "console.log"),
        Path.Combine(GameDir, "game", "bin", "linuxsteamrt64", "console.log"),
    ];

    public static ServerPaths From(Settings s) => new()
    {
        GameDir = s.Paths.GameDir,
        BridgeData = s.Paths.BridgeData,
        Handover = s.Paths.Handover,
        Depotcache = s.Paths.Depotcache,
        Plugins = s.Paths.Plugins,
    };
}

/// <summary>What the watcher and the maps folder read from the settings file, with the image's own variables where the image needs them
/// (the RCON password, the log push's address). A test builds one by hand.</summary>
public sealed class ServerConfig
{
    public required ServerPaths Paths { get; init; }
    public bool Updates { get; set; } = true;                  // restarts.updates
    public double CheckEvery { get; set; } = 1800;             // restarts.updateCheckMinutes, in seconds
    public bool NewVersions { get; set; } = true;              // restarts.newVersions
    public TimeOfDay? Daily { get; set; } = new TimeOfDay(7, 0);   // restarts.daily
    public bool FreezeRecovery { get; set; } = true;           // restarts.freezeRecovery
    public int FreezeGrace { get; set; } = 10;                 // restarts.freezeGraceSeconds
    public bool DownloadRestart { get; set; } = true;          // restarts.downloadRestart
    public string MapsLayout { get; set; } = "folders";        // maps.layout
    public string MapsDelete { get; set; } = "on";             // maps.delete
    public bool BackgroundDownloads { get; set; } = true;      // maps.backgroundDownloads
    public bool DownloadLine { get; set; } = true;             // maps.downloadLine
    public string HudAddon { get; set; } = "";                 // hud.addon, which the settings require: the program never runs without it
    public bool PluginLayer { get; set; } = true;              // plugin.enabled
    public int Port { get; set; } = 27015;                     // server.port
    public string RconPassword { get; set; } = "";             // server.rconPassword, else the image's CS2_RCONPW
    public double ApiGap { get; set; } = WebApiDoor.MinimumGap;    // steam.apiGapSeconds
    public (string Base, string Secret)? Bridge { get; set; }  // bridge.url and bridge.secret, else the log push's own address
    public KeepRules KeepRules { get; set; } = KeepRules.Default;
    public List<string> BootWarnings { get; } = [];            // logged when the watcher starts

    /// <summary>The settings for the server program, and the image's variables it falls back on.</summary>
    public static ServerConfig From(Settings s, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var c = new ServerConfig
        {
            Paths = ServerPaths.From(s),
            Updates = s.Restarts.Updates,
            CheckEvery = s.Restarts.UpdateCheckMinutes * 60.0,
            NewVersions = s.Restarts.NewVersions,
            Daily = s.Restarts.Daily,
            FreezeRecovery = s.Restarts.FreezeRecovery,
            FreezeGrace = s.Restarts.FreezeGraceSeconds,
            DownloadRestart = s.Restarts.DownloadRestart,
            MapsLayout = s.Maps.Layout,
            MapsDelete = s.Maps.Delete,
            BackgroundDownloads = s.Maps.BackgroundDownloads,
            DownloadLine = s.Maps.DownloadLine,
            HudAddon = s.Hud.Addon,
            PluginLayer = s.Plugin.Enabled,
            Port = s.Server.Port,
            RconPassword = s.Server.RconPassword,
            ApiGap = s.Steam.ApiGapSeconds,
            KeepRules = KeepRules.ForStock(s.Bridge.DiscoveryStock),   // the bridge's stock lanes are the keep set's queues
        };
        if (c.RconPassword.Length == 0 && env("CS2_RCONPW") is { Length: > 0 } pw)
            c.RconPassword = pw;                                // the image's own variable: it needs it anyway
        c.Bridge = BridgeEndpoint(s.Bridge.Url, s.Bridge.Secret, env("CS2_LOG_HTTP_URL"));
        foreach (var p in s.Problems.Concat(s.Warnings).Where(x => !x.StartsWith("sync.", StringComparison.Ordinal)))   // the sync's keys are its own business
            c.BootWarnings.Add("settings: " + p);
        if (env("CS2_PORT") is { Length: > 0 } port && port.Trim() != s.Server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            c.BootWarnings.Add($"the image's CS2_PORT is {port.Trim()} but server.port says {s.Server.Port}: the watcher asks port {s.Server.Port}");
        if (env("STEAMAPPDIR") is { Length: > 0 } dir && dir.TrimEnd('/') != s.Paths.GameDir.TrimEnd('/'))
            c.BootWarnings.Add($"the image's STEAMAPPDIR is {dir} but paths.gameDir says {s.Paths.GameDir}");
        if (env("CS2_STARTMAP") is { Length: > 0 } map && map.Trim() != s.Server.StartMap)
            c.BootWarnings.Add($"the image's CS2_STARTMAP is {map.Trim()} but server.startMap says {s.Server.StartMap}");
        if (s.Bridge.Secret.Length > 0 && BridgeEndpoint("", "", env("CS2_LOG_HTTP_URL")) is { } push && (push.Base != s.Bridge.Url.TrimEnd('/') || push.Secret != s.Bridge.Secret))
            c.BootWarnings.Add($"the image's CS2_LOG_HTTP_URL does not name bridge.url and bridge.secret (it says {push.Base}/log/..., the settings {s.Bridge.Url.TrimEnd('/')} and their secret)");
        return c;
    }

    /// <summary>(base URL, secret) of the bridge: bridge.url with bridge.secret, else the log push's own address (the image's
    /// CS2_LOG_HTTP_URL, http://&lt;bridge&gt;:&lt;port&gt;/log/&lt;secret&gt;, which its templating may write with \/). Null when neither
    /// says.</summary>
    public static (string Base, string Secret)? BridgeEndpoint(string url, string secret, string? logUrl)
    {
        if (url.Length > 0 && secret.Length > 0)
            return (url.TrimEnd('/'), secret);
        var m = LogUrl().Match((logUrl ?? "").Trim().Replace("\\/", "/"));
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    static Regex LogUrl() => new(@"^(https?://[^/\s]+)/log/([A-Za-z0-9]+)$");
}
