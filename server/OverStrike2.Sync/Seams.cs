using System.Text.Json.Nodes;

namespace OverStrike2.Sync;

/// <summary>The sync's outside world, each a seam a test replaces: SteamCMD, the test engine and its tree, the scan, a test load, A2S of the
/// game server, the Workshop check and the mail. The defaults are the real ones.</summary>
public sealed class Seams
{
    /// <summary>A SteamCMD session: (steamcmd.sh, the staging folder, its log, HOME).</summary>
    public Func<SyncConfig, string, string, SyncClock, ISteamCmd> SteamCmd { get; set; } =
        (cfg, staging, logPath, clock) => new SteamCmdProcess(cfg.SteamCmd, staging, logPath, cfg.Home, clock);

    /// <summary>The test engine: (the config, the work folder, HOME).</summary>
    public Func<SyncConfig, string, string, SyncClock, Action<string>, ITestEngine> TestEngine { get; set; } =
        (cfg, work, home, clock, log) => new TestEngine(cfg, work, home, clock, log);

    public Func<string, JsonObject> ScanItem { get; set; } = Scan.Item;
    public Func<string, string, string?, SyncClock, string> BuildTestTree { get; set; } = TestTree.Build;
    public Func<string, SyncClock, int> KillStaleEngines { get; set; } = TestTree.KillStale;
    public Func<string, string?> PatchVersion { get; set; } = TestTree.PatchVersion;

    /// <summary>One test load: (engine, id, its levels, its tags, the hold time, "somebody came on", the clock).</summary>
    public Func<ITestEngine, string, IReadOnlyList<string>, IReadOnlyList<string>, int, Func<bool>, SyncClock, JsonObject> TestLoad { get; set; } = Sync.TestLoad.Load;

    /// <summary>Humans on the game server by A2S, null when it does not answer (which counts as somebody).</summary>
    public Func<SyncConfig, SyncClock, int?> HumansOn { get; set; } = Game.HumansOn;

    /// <summary>One request to the Steam Web API as it goes out - the real HTTP, or a test's answers; the run's door, ledger and caps are the same.</summary>
    public Func<ApiRequest, ApiAnswer> WebApi { get; set; } = SteamApi.HttpSend;

    /// <summary>The check: (the API, the ids, the deadline, now, log) - ({id: item}, ids not asked).</summary>
    public Func<IWorkshopApi, IReadOnlyList<string>, double, Func<double>, Action<string>, (Dictionary<string, JsonObject>, List<string>)> Check { get; set; } = Discovery.Check;

    /// <summary>A problem mail: (config, subject, body) - null when sent, else why not.</summary>
    public Func<SyncConfig, string, string, string?> SendMail { get; set; } = Smtp.Send;
}
