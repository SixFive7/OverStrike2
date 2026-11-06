using System.Text;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>The sync's outside world, each a seam a test replaces: SteamCMD,
/// the test engine and its farm, the scan, a test load, A2S of the game server, the Workshop check, the mail, the keep set, the bridge's
/// HTTP side and the Web API's. The defaults are the real ones.</summary>
public sealed class Seams
{
    /// <summary>A SteamCMD session: (steamcmd.sh, the staging folder, its log, HOME).</summary>
    public Func<SyncConfig, string, string, SyncClock, ISteamCmd> SteamCmd { get; set; } =
        (cfg, staging, logPath, clock) => new SteamCmdProcess(cfg.SteamCmd, staging, logPath, cfg.Home, clock);

    /// <summary>The test engine: (the config, the work folder, HOME).</summary>
    public Func<SyncConfig, string, string, SyncClock, Action<string>, ITestEngine> TestEngine { get; set; } =
        (cfg, work, home, clock, log) => new TestEngine(cfg, work, home, clock, log);

    public Func<string, JsonObject> ScanItem { get; set; } = Scan.Item;
    public Func<string, string, string?, SyncClock, string> BuildFarm { get; set; } = Farm.Build;
    public Func<string, SyncClock, int> KillStaleEngines { get; set; } = Farm.KillStale;
    public Func<string, string?> PatchVersion { get; set; } = Farm.PatchVersion;

    /// <summary>One test load: (engine, id, its levels, its tags, the hold time, "somebody came on", the clock).</summary>
    public Func<ITestEngine, string, IReadOnlyList<string>, IReadOnlyList<string>, int, Func<bool>, SyncClock, JsonObject> TestLoad { get; set; } = Sync.TestLoad.Load;

    /// <summary>Humans on the game server by A2S, null when it does not answer (which counts as somebody).</summary>
    public Func<SyncConfig, SyncClock, int?> HumansOn { get; set; } = Game.HumansOn;

    /// <summary>The check (step 2): (the API, the ids, the deadline, now, log) - ({id: item}, ids not asked).</summary>
    public Func<IWorkshopApi, IReadOnlyList<string>, double, Func<double>, Action<string>, (Dictionary<string, JsonObject>, List<string>)> Check { get; set; } = Discovery.Check;

    /// <summary>A problem mail: (config, subject, body) - null when sent, else why not.</summary>
    public Func<SyncConfig, string, string, string?> SendMail { get; set; } = Smtp.Send;

    /// <summary>THE KEEP SET (Core's KeepSet, the restart watcher's too), with the rules of the config (its queues as long as
    /// bridge.discoveryStock, as the watcher's); the sync never trusts it with an exception.</summary>
    public Func<IReadOnlyDictionary<string, JsonNode?>, DateTimeOffset, string, KeepRules, KeepSetResult> KeepSet { get; set; } =
        (inputs, now, hud, rules) => Core.KeepSet.Compute(inputs, now, hud, rules);

    /// <summary>A POST to the bridge: (URL, body, content type) - (status, the answer's text). Throws when there is no answer.</summary>
    public Func<string, string, string, (int Status, string Text)> BridgePost { get; set; } = Game.Post;

    /// <summary>One Web API request (the program's door and ledger are around it).</summary>
    public Func<ApiRequest, ApiAnswer> WebApi { get; set; } = SteamApi.HttpSend;
}

/// <summary>The game server and the bridge, as the sync sees them from its own container.</summary>
public static class Game
{
    /// <summary>Humans on the game server by A2S, asked twice 2 s apart when the first answer is not 0. Null: no answer. No host: 0.</summary>
    public static int? HumansOn(SyncConfig cfg, SyncClock clock)
    {
        if (cfg.GameHost.Length == 0)
            return 0;
        var first = A2s.Ask(cfg.GameHost, cfg.GamePort, TimeSpan.FromSeconds(2));
        if (first is not null && first.Humans == 0)
            return 0;
        clock.Sleep(2);
        var second = A2s.Ask(cfg.GameHost, cfg.GamePort, TimeSpan.FromSeconds(2));
        if (second is null)
            return first?.Humans;
        return second.Humans;
    }

    static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>POST a text to the bridge; (status, the answer). Throws HttpRequestException or a timeout when there is none.</summary>
    public static (int Status, string Text) Post(string url, string body, string contentType)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        req.Headers.TryAddWithoutValidation("User-Agent", SteamApi.UserAgent);
        using var resp = Http.Send(req);
        var text = new StreamReader(resp.Content.ReadAsStream(), Encoding.UTF8).ReadToEnd();
        return ((int)resp.StatusCode, text);
    }
}
