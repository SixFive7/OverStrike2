using System.Globalization;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>What the sync reads from the settings file (sync.*, paths.*, server.*, bridge.*, hud.addon, restarts.daily,
/// steam.apiGapSeconds), with the image's own variables where the image needs them (HOME). Hard limits clamp in the settings reader and
/// here: at most 10,000 Web API calls in 24 hours, a run's cap never above that, never less than 2 s between two calls, never less than
/// 2 s between two downloads. Made by From alone - a test's too, from its own settings text - so the defaults are the settings file's; a run-now may only lower a limit (<see cref="Lower"/>).</summary>
public sealed class SyncConfig
{
    public const int HardApiDayCap = 10000;
    public const double HardApiGap = WebApiDoor.MinimumGap;

    public bool Enabled { get; set; }                              // sync.enabled
    public bool Publish { get; set; }                              // sync.publish
    public TimeOfDay? DailyRestart { get; set; }                   // restarts.daily: no run 15 min before to 10 after; null: none
    public int RunHours { get; set; }                              // sync.runHours
    public double ApiEvery { get; set; }                           // sync.apiEveryHours, in seconds
    public List<string> Tags { get; set; } = [];                   // sync.tags
    public HashSet<string> Exclude { get; set; } = [];             // sync.excludeIds and hud.addon: never candidates
    public HashSet<string> ExcludeCreators { get; set; } = [];     // sync.excludeCreators: listed by hand
    public string ChangedSince { get; set; } = "";                 // sync.changedSince (YYYY-MM-DD); empty: no map counts as changed at its first check
    public bool ModeBackfill { get; set; }                         // sync.modeBackfill
    public int ApiDayCap { get; set; }                             // sync.apiDayCap
    public int ApiRunCap { get; set; }                             // sync.apiRunCap
    public double ApiGap { get; set; }                             // steam.apiGapSeconds
    public int MaxPages { get; set; }                              // sync.maxPages
    public double DownloadPause { get; set; }                      // sync.downloadPauseSeconds
    public int Batch { get; set; }                                 // sync.batch
    public double Quiet { get; set; }                              // sync.quietMinutes, in seconds
    public int Hold { get; set; }                                  // sync.holdSeconds
    public int TestPort { get; set; }                              // sync.testPort
    public int EngineStartsMax { get; set; }                       // sync.engineStartsMax
    public required string StateDir { get; set; }                  // paths.syncState
    public required string WorkDir { get; set; }                   // paths.syncWork
    public required string GameDir { get; set; }                   // paths.gameDir, read-only here
    public required string BridgeData { get; set; }                // paths.bridgeData
    public string BridgeUrl { get; set; } = "";                    // bridge.url
    public string BridgeSecret { get; set; } = "";                 // bridge.secret
    public string GameHost { get; set; } = "";                     // server.host: empty asks nobody (a test's)
    public int GamePort { get; set; }                              // server.port
    public string CfgDir { get; set; } = "";                       // paths.syncCfg
    public string WebApiKey { get; set; } = "";                    // sync.webApiKey (read through its reference); "" none
    public string SmtpHost { get; set; } = "";                    // sync.mail.*
    public int SmtpPort { get; set; }
    public string MailFrom { get; set; } = "";
    public List<string> MailTo { get; set; } = [];
    public string DashboardUrl { get; set; } = "";
    public string SteamCmd { get; set; } = "";                    // paths.steamCmd
    public bool Ratings { get; set; }                              // sync.ratings
    public bool ForceApi { get; set; }                             // a run-now's {"api": true}
    public bool SkipApi { get; set; }                              // a run-now's {"api": false}
    public long DiskLow { get; set; }                              // sync.diskLowGb, in bytes
    public bool Handover { get; set; }                             // sync.handover
    public string Depotcache { get; set; } = "";                  // paths.depotcache
    public string ServerName { get; set; } = "";                  // server.name, else "CS2 server"
    public string HudAddon { get; set; } = "";                     // hud.addon: the HUD item, for the dashboard's keep set
    public string Home { get; set; } = "";                        // the image's HOME, else /home/steam: SteamCMD's and the test engine's
    public List<string>? OnlyIds { get; set; }                     // a run-now's {"only_ids": [...]}
    public KeepRules KeepRules { get; set; } = KeepRules.Default;  // the keep set's limits: its queues as long as bridge.discoveryStock, the restart watcher's too
    public List<string> BootWarnings { get; } = [];

    public string HandoverDir => Path.Combine(WorkDir, "handover");   // paths.handover in the game server
    public string WatcherDir => Path.Combine(GameDir, "update-watch");   // the restart watcher's folder: keep.json, its last keep set
    /// <summary>The status file, in the bridge's data.</summary>
    public string StatusFile => Path.Combine(BridgeData, "sync-status.json");

    /// <summary>The settings for the sync, and a warning where the image's own variables say otherwise.</summary>
    public static SyncConfig From(Settings s, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var c = new SyncConfig
        {
            Enabled = s.Sync.Enabled,
            Publish = s.Sync.Publish,
            DailyRestart = s.Restarts.Daily,
            RunHours = s.Sync.RunHours,
            ApiEvery = s.Sync.ApiEveryHours * 3600.0,
            Tags = s.Sync.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).ToList(),
            ExcludeCreators = new HashSet<string>(s.Sync.ExcludeCreators.Where(Text.Digits)),
            ChangedSince = s.Sync.ChangedSince,
            ModeBackfill = s.Sync.ModeBackfill,
            ApiDayCap = Math.Clamp(s.Sync.ApiDayCap, 0, HardApiDayCap),
            ApiGap = Math.Max(HardApiGap, s.Steam.ApiGapSeconds),
            MaxPages = s.Sync.MaxPages,
            DownloadPause = Math.Max(2.0, s.Sync.DownloadPauseSeconds),
            Batch = s.Sync.Batch,
            Quiet = s.Sync.QuietMinutes * 60.0,
            Hold = s.Sync.HoldSeconds,
            TestPort = s.Sync.TestPort,
            EngineStartsMax = s.Sync.EngineStartsMax,
            StateDir = s.Paths.SyncState,
            WorkDir = s.Paths.SyncWork,
            GameDir = s.Paths.GameDir,
            BridgeData = s.Paths.BridgeData,
            BridgeUrl = s.Bridge.Url.TrimEnd('/'),
            BridgeSecret = s.Bridge.Secret,
            GameHost = s.Server.Host,
            GamePort = s.Server.Port,
            CfgDir = s.Paths.SyncCfg,
            WebApiKey = s.Sync.WebApiKey,
            SmtpHost = s.Sync.Mail.SmtpHost,
            SmtpPort = s.Sync.Mail.SmtpPort,
            MailFrom = s.Sync.Mail.From,
            MailTo = s.Sync.Mail.To.Select(a => a.Trim()).Where(a => a.Length > 0).ToList(),
            DashboardUrl = s.Sync.Mail.DashboardUrl.Trim(),
            SteamCmd = s.Paths.SteamCmd,
            Ratings = s.Sync.Ratings,
            DiskLow = (long)s.Sync.DiskLowGb << 30,
            Handover = s.Sync.Handover,
            Depotcache = s.Paths.Depotcache,
            ServerName = s.Server.Name.Length > 0 ? s.Server.Name : "CS2 server",
            HudAddon = s.Hud.Addon.Trim(),
            Home = env("HOME") is { Length: > 0 } home ? home : "/home/steam",
            KeepRules = KeepRules.ForStock(s.Bridge.DiscoveryStock),
        };
        c.ApiRunCap = Math.Clamp(s.Sync.ApiRunCap, 0, c.ApiDayCap);
        c.Exclude = new HashSet<string>(s.Sync.ExcludeIds.Where(Text.Digits));
        if (Text.Digits(c.HudAddon))
            c.Exclude.Add(c.HudAddon);                          // the HUD item is never a candidate
        foreach (var p in s.Problems.Concat(s.Warnings))   // read as Part.Sync: the sync's own keys only
            c.BootWarnings.Add("settings: " + p);
        if (env("STEAMAPPDIR") is { Length: > 0 } dir && dir.TrimEnd('/') != s.Paths.GameDir.TrimEnd('/'))
            c.BootWarnings.Add($"the image's STEAMAPPDIR is {dir} but paths.gameDir says {s.Paths.GameDir}: the sync reads {s.Paths.GameDir}");
        return c;
    }

    /// <summary>A copy for one run: a run-now's Lower must not reach the service's own.</summary>
    public SyncConfig Clone()
    {
        var c = (SyncConfig)MemberwiseClone();
        c.Tags = [.. Tags];
        c.Exclude = [.. Exclude];
        c.ExcludeCreators = [.. ExcludeCreators];
        c.MailTo = [.. MailTo];
        c.OnlyIds = OnlyIds is null ? null : [.. OnlyIds];
        return c;
    }

    /// <summary>run-now may only lower a limit for its run, never raise one: api_run_cap, max_pages and batch (whole numbers from 0);
    /// publish false; discover false (no key: no discovery); api true (discovery, the check and the ratings this run, whatever their
    /// cadence) or false (not at all in this run); only_ids (this run takes only these maps).</summary>
    public void Lower(JsonObject overrides)
    {
        if (Node.Integer(overrides["api_run_cap"]) is { } runCap && runCap >= 0)
            ApiRunCap = (int)Math.Min(ApiRunCap, runCap);
        if (Node.Integer(overrides["max_pages"]) is { } pages && pages >= 0)
            MaxPages = (int)Math.Min(MaxPages, pages);
        if (Node.Integer(overrides["batch"]) is { } batch && batch >= 0)
            Batch = (int)Math.Min(Batch, batch);
        if (IsFalse(overrides["publish"]))
            Publish = false;
        if (IsFalse(overrides["discover"]))
            WebApiKey = "";
        if (IsTrue(overrides["api"]))
            ForceApi = true;
        if (IsFalse(overrides["api"]))
            SkipApi = true;
        if (overrides["only_ids"] is JsonArray only)
            OnlyIds = only.Select(Node.Str).Where(Text.Digits).ToList();
    }

    static bool IsTrue(JsonNode? v) => Node.IsBool(v) && v!.GetValue<bool>();
    static bool IsFalse(JsonNode? v) => Node.IsBool(v) && !v!.GetValue<bool>();

    /// <summary>The run-now file's limits as one line, for the log.</summary>
    public static string Describe(JsonObject overrides) => overrides.Count == 0 ? "no overrides" : Node.Compact(overrides);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"runs of {RunHours} h, the API every {Text.Hms(ApiEvery)}, caps {ApiRunCap} a run and {ApiDayCap} in 24 h, {ApiGap} s apart");
}
