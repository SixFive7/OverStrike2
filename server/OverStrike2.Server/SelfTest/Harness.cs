using OverStrike2.Core;

namespace OverStrike2.Server.SelfTest;

/// <summary>A clock the tests move by hand: Sleep advances it.</summary>
sealed class FakeClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset Now { get; set; } = start;
    public double T => Watch.Seconds(Now);

    public void Sleep(TimeSpan span)
    {
        if (span > TimeSpan.Zero)
            Now += span;
    }

    public void Sleep(double seconds) => Sleep(TimeSpan.FromSeconds(seconds));
}

/// <summary>The engine as the scenarios need it: A2S and RCON that answer or not, a console.log the echo probe proves, a process that
/// quits or is killed. The hooks stand in other behaviour, the way the old watcher's tests replaced its functions.</summary>
sealed class FakeEngine : IEngine
{
    public int? PidValue = 4242;
    public bool A2s = true, RconUp = true, Hud;          // Hud: the plugin answers gg_notice (a published HUD is on)
    public int Humans, AliveProbes;
    public string Version = "1.41.8.8";
    public readonly List<string> Chat = [], Cmds = [], Killed = [];
    public readonly List<(int Seconds, string Text)> Notices = [];
    public readonly string ConsolePath;
    public Func<EngineInfo?>? OnProbe;
    public Func<IReadOnlyList<string>, bool, List<string?>?>? OnRcon;
    public Func<bool>? OnAlive;
    public Action<int, string>? OnKill;

    public FakeEngine(string consolePath)
    {
        ConsolePath = consolePath;
        Directory.CreateDirectory(Path.GetDirectoryName(consolePath)!);
        File.WriteAllText(consolePath, "");
    }

    public void Write(string line) => File.AppendAllText(ConsolePath, line + "\n");

    public EngineInfo? BaseProbe() => A2s ? new EngineInfo(Version, Humans, 0, "ar_shoots") : null;

    public List<string?> BaseRcon(IReadOnlyList<string> commands, bool expectReply = true)
    {
        var replies = new List<string?>();
        foreach (var c in commands)
        {
            Cmds.Add(c);
            if (c.StartsWith("echo ", StringComparison.Ordinal))
                Write($"L 10/04/2026 - 07:00:00: rcon from \"127.0.0.1:5555\": command \"{c}\"");
            if (c.StartsWith("say ", StringComparison.Ordinal))
                Chat.Add(c[5..^1]);
            if (c.StartsWith("gg_notice ", StringComparison.Ordinal) && Hud)
            {
                var rest = c["gg_notice ".Length..];
                var space = rest.IndexOf(' ');
                var text = rest[(space + 1)..].Trim('"');
                Notices.Add((int.Parse(rest[..space]), text));
                replies.Add($"gg_notice: shown for {rest[..space]} s: {text}");
                continue;
            }
            if (c == "quit")
                PidValue = null;
            replies.Add(c == "status" ? $"players  : {Humans} humans, 0 bots (10 max) (not hibernating)" : "");
        }
        return replies;
    }

    public EngineInfo? Probe() => OnProbe is { } p ? p() : BaseProbe();
    public List<string?>? Rcon(IReadOnlyList<string> commands, bool expectReply = true) => OnRcon is { } r ? r(commands, expectReply) : BaseRcon(commands, expectReply);
    public bool RconAlive() => OnAlive is { } a ? a() : BaseAlive();
    public bool BaseAlive()
    {
        AliveProbes++;
        return RconUp && PidValue is not null;
    }
    public int? Pid() => PidValue;
    public void Kill(int pid, string why)
    {
        if (OnKill is { } k)
            k(pid, why);
        else
            BaseKill(pid, why);
    }
    public void BaseKill(int pid, string why)
    {
        Killed.Add(why);
        PidValue = null;
    }
    public double? Age(int pid) => 42;

    /// <summary>A frozen engine: the process is there, nothing answers.</summary>
    public void Freeze() => (A2s, RconUp) = (false, false);
    public void Thaw() => (A2s, RconUp) = (true, true);
}

/// <summary>The bridge's HTTP side: every post recorded; Reply gives the answer's text (null: no connection), read the way HttpBridge
/// reads it - "ok" first means taken.</summary>
sealed class FakeBridge : IBridge
{
    public string? Address { get; set; }
    public readonly List<(string What, string Body)> Posts = [];
    public Func<string, string, string?> Reply = (what, _) => what switch
    {
        "quitting" => "ok: going down",
        "ready" => "ok: 0 on disk",
        _ => "ok",
    };

    public FakeBridge(string? address = "http://bridge.test:8080") => Address = address;

    public (string Told, string? Reply) Post(string what, string body, double timeoutSeconds)
    {
        if (Address is null)
            return ("none", null);
        Posts.Add((what, body));
        string? reply;
        try
        {
            reply = Reply(what, body);
        }
        catch (Exception e)
        {
            return ("failed", $"{e.GetType().Name}: {e.Message}");
        }
        if (reply is null)
            return ("failed", "HttpRequestException: Connection refused");
        return reply.StartsWith("ok", StringComparison.Ordinal) ? ("ok", reply) : ("failed", $"the bridge answered '{reply}'");
    }

    public List<string> Bodies(string what) => Posts.Where(p => p.What == what).Select(p => p.Body).ToList();
}

sealed class FakeValve : IValve
{
    public Func<int, (string, int?, string?)> Answer = _ => ("current", null, null);
    public int Calls;

    public (string Answer, int? Required, string? Why) Check(int vint)
    {
        Calls++;
        return Answer(vint);
    }
}

/// <summary>The scenarios' world: a game install under a test folder (steam.inf, console.log, the workshop folder), the bridge's data,
/// and the knobs the old watcher's tests turned (Template). Make() is their scenario(): a fresh state folder, a fresh clock,
/// engine and bridge, and a Watch on them.</summary>
sealed class Scenario
{
    public static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
    public static readonly TimeSpan Cest = TimeSpan.FromHours(2);
    public static string Root = "";
    public static ServerConfig Template = null!;
    public static Func<FakeBridge?> NewBridge = () => null;      // what each new scenario talks to (null: no bridge configured)

    public required Watch W;
    public required FakeClock Clock;
    public required FakeEngine Eng;
    public required FakeBridge Bridge;
    public required FakeValve Valve;
    public required Log Log;

    public const string SteamInf = "ClientVersion=2000924\nServerVersion=2000924\nPatchVersion=1.41.8.8\nProductName=cs2\nappID=730\nServerAppID=2347773\n"
                                   + "SourceRevision=11076591\nVersionDate=Oct 02 2026\nVersionTime=14:45:21\n";

    /// <summary>The world, once: the old watcher's tests' ROOT.</summary>
    public static void Setup()
    {
        Root = T.Dir("uw");
        T.Put(Path.Combine(Root, "game", "csgo", "steam.inf"), SteamInf);
        Template = new ServerConfig
        {
            Paths = new ServerPaths
            {
                GameDir = Root,
                BridgeData = Path.Combine(Root, "bridge-none"),
                Handover = Path.Combine(Root, "handover"),
                Depotcache = Path.Combine(Root, "depotcache"),
                Plugins = Path.Combine(Root, "plugins"),
                Logs = Path.Combine(Root, "steamlogs-none"),
            },
            RconPassword = "secret",
        };
    }

    /// <summary>A copy of the template with other paths: the old watcher's tests set the paths one by one.</summary>
    public static void Paths(Func<ServerPaths, ServerPaths> change) => Template = Copy(Template, change(Template.Paths));

    public static ServerConfig Copy(ServerConfig c, ServerPaths? paths = null) => new()
    {
        Paths = paths ?? c.Paths,
        Updates = c.Updates, CheckEvery = c.CheckEvery, NewVersions = c.NewVersions, Daily = c.Daily, FreezeRecovery = c.FreezeRecovery,
        FreezeGrace = c.FreezeGrace, DownloadRestart = c.DownloadRestart, MapsLayout = c.MapsLayout, MapsDelete = c.MapsDelete,
        BackgroundDownloads = c.BackgroundDownloads, DownloadLine = c.DownloadLine, HudAddon = c.HudAddon, PluginLayer = c.PluginLayer,
        Port = c.Port, RconPassword = c.RconPassword, ApiGap = c.ApiGap, Bridge = c.Bridge, KeepRules = c.KeepRules,
    };

    public static DateTimeOffset At(int y, int mo, int d, int h, int mi, int s) => new(y, mo, d, h, mi, s, Cest);

    public static Scenario Make(DateTimeOffset start, (string, int?, string?)? verdict = null, bool observe = false, Action<ServerConfig>? tune = null,
                                Installed? installed = null, string? state = null)
    {
        var stateDir = Path.Combine(Root, "update-watch");
        if (Directory.Exists(stateDir))
            Directory.Delete(stateDir, true);
        installed?.Write(Path.Combine(stateDir, "installed.json"));
        var clock = new FakeClock(start);
        var eng = new FakeEngine(Path.Combine(Root, "game", "csgo", "console.log"));
        var bridge = NewBridge() ?? new FakeBridge(null);
        var valve = new FakeValve();
        if (verdict is { } v)
            valve.Answer = _ => v;
        var log = new Log("test", clock, Paris, _ => { });
        var config = Copy(Template, state is null ? null : Template.Paths with { State = state });
        tune?.Invoke(config);
        var w = new Watch(config, clock, Paris, log, eng, bridge, valve, observe)
        {
            SettingsPath = Path.Combine(Root, "settings.jsonc"),
            Env = _ => null,
        };
        return new Scenario { W = w, Clock = clock, Eng = eng, Bridge = bridge, Valve = valve, Log = log };
    }

    /// <summary>Run Watch.Run()'s own loop body, LoopOnce - one tick, paced - for `seconds` of fake time.</summary>
    public void Step(double seconds, Action<double>? each = null)
    {
        var end = Clock.T + seconds;
        while (Clock.T < end)
        {
            each?.Invoke(Clock.T);
            W.LoopOnce();
        }
    }

    public string StateFile => W.Config.Paths.StateFile;

    /// <summary>The old watcher's tests' "the container came back": the state of another run written to this one's file, and read again.</summary>
    public void Carry(WatchState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, state.ToJson());
        (W.State, W.Fault) = WatchState.Load(StateFile);
    }

    public int Count(string command) => Eng.Cmds.Count(c => c == command);
    public bool Has(string command) => Eng.Cmds.Contains(command);
}
