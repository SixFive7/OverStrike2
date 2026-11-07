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
    public string? Logs { get; init; }                     // the Steam client's logs, when they are not beside the depotcache

    public string StateDir => Path.Combine(GameDir, "update-watch");   // the counters, the records, the lists of deletions
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

/// <summary>What the watcher and the maps folder read from the settings, and the image's own variables they are held to (BootWarnings).
/// Made by From alone, so every value is the settings' - their defaults are the table's (core/Settings.cs), never a second copy here; a test
/// changes one with `with`.</summary>
public sealed record ServerConfig
{
    ServerConfig()
    {
    }

    public ServerPaths Paths { get; init; } = null!;
    public bool Updates { get; init; }                         // restarts.updates
    public double CheckEvery { get; init; }                    // restarts.updateCheckMinutes, in seconds
    public bool NewVersions { get; init; }                     // restarts.newVersions
    public TimeOfDay? Daily { get; init; }                     // restarts.daily
    public bool FreezeRecovery { get; init; }                  // restarts.freezeRecovery
    public int FreezeGrace { get; init; }                      // restarts.freezeGraceSeconds
    public bool DownloadRestart { get; init; }                 // restarts.downloadRestart
    public string MapsLayout { get; init; } = "";              // maps.layout
    public string MapsDelete { get; init; } = "";              // maps.delete
    public bool BackgroundDownloads { get; init; }             // maps.backgroundDownloads
    public bool DownloadLine { get; init; }                    // maps.downloadLine
    public string HudAddon { get; init; } = "";                // hud.addon, which the settings require: the program never runs without it
    public bool PluginLayer { get; init; }                     // plugin.enabled
    public int Port { get; init; }                             // server.port
    public string RconPassword { get; init; } = "";            // server.rconPassword: the image's CS2_RCONPW by default
    public double ApiGap { get; init; }                        // steam.apiGapSeconds
    public (string Base, string Secret)? Bridge { get; init; } // bridge.url and bridge.secret; none while the secret is empty
    public KeepRules KeepRules { get; init; } = KeepRules.Default;   // the keep set's limits, its queues as long as bridge.discoveryStock
    public List<string> BootWarnings { get; init; } = [];      // logged when the watcher starts

    /// <summary>The settings for the server program, and a warning wherever the image's own variables say otherwise.</summary>
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
            Bridge = s.Bridge.Secret.Length > 0 ? (s.Bridge.Url.TrimEnd('/'), s.Bridge.Secret) : null,
        };
        foreach (var p in s.Problems.Concat(s.Warnings))   // read as Part.Server: this program's own keys only
            c.BootWarnings.Add("settings: " + p);
        if (env("CS2_PORT") is { Length: > 0 } port && port.Trim() != s.Server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            c.BootWarnings.Add($"the image's CS2_PORT is {port.Trim()} but server.port says {s.Server.Port}: the watcher asks port {s.Server.Port}");
        if (env("STEAMAPPDIR") is { Length: > 0 } dir && dir.TrimEnd('/') != s.Paths.GameDir.TrimEnd('/'))
            c.BootWarnings.Add($"the image's STEAMAPPDIR is {dir} but paths.gameDir says {s.Paths.GameDir}");
        if (env("CS2_STARTMAP") is { Length: > 0 } map && map.Trim() != s.Server.StartMap)
            c.BootWarnings.Add($"the image's CS2_STARTMAP is {map.Trim()} but server.startMap says {s.Server.StartMap}");
        if (c.Bridge is { } bridge && LogPush(env("CS2_LOG_HTTP_URL")) is { } push && push != bridge)
            c.BootWarnings.Add($"the image's CS2_LOG_HTTP_URL does not name bridge.url and bridge.secret (it says {push.Base}/log/..., the settings {bridge.Base} and their secret)");
        return c;
    }

    /// <summary>(base URL, secret) of the bridge as the image's log push names it (CS2_LOG_HTTP_URL, http://&lt;bridge&gt;:&lt;port&gt;/log/&lt;secret&gt;,
    /// which its templating may write with \/), or null: what bridge.url and bridge.secret are held to.</summary>
    public static (string Base, string Secret)? LogPush(string? logUrl) =>
        LogUrl().Match((logUrl ?? "").Trim().Replace("\\/", "/")) is { Success: true } m ? (m.Groups[1].Value, m.Groups[2].Value) : null;

    static Regex LogUrl() => new(@"^(https?://[^/\s]+)/log/([A-Za-z0-9]+)$");
}
