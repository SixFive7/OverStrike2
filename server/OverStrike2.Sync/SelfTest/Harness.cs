using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync.SelfTest;

/// <summary>A clock the tests move by hand: Sleep advances it and is recorded.</summary>
sealed class FakeClock(double t) : IClock
{
    public double T { get; set; } = t;
    public List<double> Sleeps { get; } = [];
    public DateTimeOffset Now => SyncClock.At(T);

    public void Sleep(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
            return;
        Sleeps.Add(Math.Round(span.TotalSeconds, 6));
        T += span.TotalSeconds;
    }
}

/// <summary>The world the tests set up: the paths a new config reads, the seams every run uses, the log every run writes to.</summary>
static class W
{
    public static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
    public const string Key = "0123456789ABCDEF0123456789ABCDEF";
    public static string State = "", Work = "", Data = "", GameRoot = "", Depotcache = "", BridgeUrl = "", BridgeSecret = "";
    public static readonly SyncLog Log = new(new SyncClock(new FakeClock(0), Paris)) { Echo = false };
    public static readonly Seams Hooks = new();

    /// <summary>A new config, as production reads one: the settings file's defaults, on the paths of the moment, asking no game server.</summary>
    public static SyncConfig Cfg()
    {
        var c = SyncConfig.From(Settings.Parse("""{ "schema": 1, "sync": { "changedSince": "2026-10-25" } }""", Part.Sync, _ => null), _ => null);
        (c.StateDir, c.WorkDir, c.GameDir, c.BridgeData, c.BridgeUrl, c.BridgeSecret, c.Depotcache) = (State, Work, GameRoot, Data, BridgeUrl, BridgeSecret, Depotcache);
        (c.GameHost, c.Home) = ("", Path.Combine(T.Root, "home"));
        return c;
    }

    public static SyncClock C(FakeClock f) => new(f, Paris);

    public static SyncRun Run(SyncConfig cfg, FakeClock fc, bool manual = true, Action<double>? sleep = null) =>
        new(cfg, C(fc), Hooks, Log, new WebApiDoor(cfg.ApiGap, fc), manual, sleep);

    public static JsonNode J(string json) => JsonNode.Parse(json)!;
    public static JsonObject O(string json) => (JsonObject)JsonNode.Parse(json)!;
    public static JsonArray A(string json) => (JsonArray)JsonNode.Parse(json)!;

    public static void Json(string path, JsonNode? value) => T.Put(path, value is null ? "null" : value.ToJsonString());

    public static JsonNode? Read(string path) => JsonNode.Parse(File.ReadAllText(path));

    /// <summary>An opener of stub answers: an int is an HTTP error status, "net" a network failure, else a JSON body; each call pops the next.</summary>
    public static Func<ApiRequest, ApiAnswer> Opener(List<object> answers, List<ApiRequest> sent) => r =>
    {
        sent.Add(r);
        var a = answers.Count > 0 ? answers[0] : J("{\"response\": {}}");
        if (answers.Count > 0)
            answers.RemoveAt(0);
        return a switch
        {
            "net" => throw new HttpRequestException("timed out"),
            int status => new ApiAnswer(status, "x"u8.ToArray()),
            JsonNode node => new ApiAnswer(200, Encoding.UTF8.GetBytes(node.ToJsonString())),
            _ => throw new InvalidOperationException("a stub answer of no known kind"),
        };
    };

    public static (ApiLedger Ledger, FakeClock Clock) Ledger(string path, int dayCap = 10000, double t = 1793500000.0)
    {
        var clock = new FakeClock(t);
        var led = new ApiLedger(path, dayCap, C(clock));
        led.Load();
        return (led, clock);
    }

    public static (SteamApi Api, List<ApiRequest> Sent, FakeClock Clock) ApiWith(ApiLedger led, FakeClock clock, List<object> answers, string? key = Key, double gap = 2.0)
    {
        var sent = new List<ApiRequest>();
        var api = new SteamApi(led, C(clock).Day(), new WebApiDoor(gap, clock), key, Opener(answers, sent), s => clock.Sleep(TimeSpan.FromSeconds(s)), Log.Line);
        return (api, sent, clock);
    }

    public static List<object> Many(object answer, int n) => Enumerable.Range(0, n).Select(_ => answer is JsonNode j ? j.DeepClone() : answer).ToList();
}

/// <summary>A stand-in for SteamCMD: each download makes the item's folder with one file, unless Answers says why it failed.</summary>
class FakeSteam(string installDir) : ISteamCmd
{
    public static int Logins;
    public static Dictionary<string, string> Answers = [];
    public static List<string> Asked = [];
    protected readonly string InstallDir = installDir;

    public (bool Ok, string Why) Start()
    {
        Logins++;
        return (true, "logged in anonymously");
    }

    public virtual (bool Ok, string Reason, long Bytes) Download(string wid)
    {
        Asked.Add(wid);
        if (Answers.TryGetValue(wid, out var why))
            return (false, why, 0);
        var folder = Path.Combine(InstallDir, "steamapps", "workshop", "content", "730", wid);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, wid + "_dir.vpk"), "x");
        return (true, "OK", 1000);
    }

    public virtual void Stop()
    {
    }
}

/// <summary>A stand-in for SteamCMD that logs in, or not, as told.</summary>
sealed class ScriptedSteam(bool login, string why = "logged in anonymously") : ISteamCmd
{
    public (bool Ok, string Why) Start() => (login, why);

    public (bool Ok, string Reason, long Bytes) Download(string wid) => (false, "not asked", 0);

    public void Stop()
    {
    }
}

/// <summary>A stand-in for the test engine: it starts, it stops, it counts both.</summary>
class FakeEngine : ITestEngine
{
    public int Starts { get; set; }
    public int Stops;
    public bool Up;
    public ILines? Tail => null;
    public bool Alive() => Up;

    public (bool Ok, string Why) Start()
    {
        Starts++;
        Up = true;
        return (true, "test engine up");
    }

    public void Stop()
    {
        Up = false;
        Stops++;
    }

    public string? Rcon(string command) => "";
    public EngineInfo? A2s() => new("x", 0, 0, 0);
}

/// <summary>A stand-in bridge on localhost: every POST recorded (path, content type, body); /removed/ and /reload/ answered as the bridge does.</summary>
sealed class MiniHttp : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly Thread thread;
    public readonly List<(string Path, string? ContentType, string Body)> Got = [];
    volatile bool stopping;

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public MiniHttp()
    {
        listener.Start();
        thread = new Thread(Serve) { IsBackground = true };
        thread.Start();
    }

    void Serve()
    {
        while (!stopping)
        {
            TcpClient c;
            try
            {
                c = listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            using (c)
            {
                try
                {
                    var s = c.GetStream();
                    var head = new List<byte>();
                    while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
                    {
                        var b = s.ReadByte();
                        if (b < 0)
                            break;
                        head.Add((byte)b);
                    }
                    var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
                    var path = lines[0].Split(' ')[1];
                    var headers = lines.Skip(1).Where(l => l.Contains(':')).ToDictionary(l => l[..l.IndexOf(':')].Trim().ToLowerInvariant(), l => l[(l.IndexOf(':') + 1)..].Trim());
                    var length = headers.TryGetValue("content-length", out var cl) ? int.Parse(cl) : 0;
                    var body = new byte[length];
                    s.ReadExactly(body);
                    lock (Got)
                        Got.Add((path, headers.GetValueOrDefault("content-type"), Encoding.UTF8.GetString(body)));
                    var answer = Encoding.UTF8.GetBytes(path.StartsWith("/removed/", StringComparison.Ordinal) ? "3 checked, 1 not downloadable, 1 newly blocked" : "reload: maps: 9 maps");
                    s.Write(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {answer.Length}\r\nConnection: close\r\n\r\n"));
                    s.Write(answer);
                }
                catch (Exception e) when (e is IOException or SocketException or IndexOutOfRangeException or FormatException)
                {
                }
            }
        }
    }

    public void Dispose()
    {
        stopping = true;
        listener.Stop();
    }
}

/// <summary>The exception the tests raise for a crashed run: its name is what the mail says.</summary>
sealed class TestFailure(string message) : Exception(message);

/// <summary>An engine the tests count the instances of.</summary>
sealed class SpyEngine : FakeEngine
{
    public static readonly List<SpyEngine> All = [];
    public SpyEngine() => All.Add(this);
}

/// <summary>SteamCMD that writes an acf naming the item it just downloaded (alone), and once it quits every item of its session still staged, as
/// the real one's acf is complete by then. A hand-over that could not read the acf at once waits for that quit; on Windows a read can be refused
/// for a moment when something else has the new file open, and on this clock the 20 s the run waits for the entry take no time at all.</summary>
sealed class AcfSteam(string installDir) : FakeSteam(installDir)
{
    readonly List<string> session = [];

    string Acf => Path.Combine(InstallDir, "steamapps", "workshop", "appworkshop_730.acf");

    void WriteAcf(IEnumerable<string> wids) =>
        File.WriteAllText(Acf, "\"AppWorkshop\"\n{\n\t\"WorkshopItemsInstalled\"\n\t{\n"
                               + string.Concat(wids.Select(w => $"\t\t\"{w}\"\n\t\t{{\n\t\t\t\"size\"\t\t\"1\"\n\t\t\t\"timeupdated\"\t\t\"9\"\n\t\t\t\"manifest\"\t\t\"4{w}\"\n\t\t}}\n")) + "\t}\n}\n");

    public override (bool Ok, string Reason, long Bytes) Download(string wid)
    {
        var ok = base.Download(wid);
        session.Add(wid);
        WriteAcf([wid]);
        return ok;
    }

    public override void Stop()
    {
        var staged = session.Where(w => Directory.Exists(Path.Combine(InstallDir, "steamapps", "workshop", "content", "730", w))).ToList();
        if (staged.Count > 0)
            WriteAcf(staged);
        session.Clear();
    }
}

/// <summary>SteamCMD at its worst: its acf names the downloads only once it quits.</summary>
sealed class LateAcfSteam(string installDir) : FakeSteam(installDir)
{
    public static readonly List<string> Got = [];
    public static Action<Dictionary<string, string>> WriteAcf = _ => { };

    public override (bool Ok, string Reason, long Bytes) Download(string wid)
    {
        Got.Add(wid);
        return base.Download(wid);
    }

    public override void Stop()
    {
        if (Got.Count > 0)
            WriteAcf(Got.ToDictionary(w => w, _ => "200"));
    }
}

/// <summary>A test-engine stand-in that is gone the moment it is asked: the real test load runs its first steps and says crash.</summary>
sealed class DownEngine : ITestEngine
{
    public static readonly List<string> Sent = [];
    sealed class NoLines : ILines
    {
        public List<string> Lines() => [];
    }
    public int Starts { get; set; }
    public ILines? Tail { get; } = new NoLines();
    public bool Alive() => false;
    public (bool Ok, string Why) Start() => (false, "no");
    public void Stop()
    {
    }
    public string? Rcon(string command)
    {
        Sent.Add(command);
        return "";
    }
    public EngineInfo? A2s() => null;
}

/// <summary>A ratings API stand-in: scores for some ids, Most Popular orders per tag, a stop after some calls.</summary>
sealed class VoteApi(Dictionary<string, double> scores, int? stopAfter = null, Dictionary<string, List<string>>? trends = null) : IWorkshopApi
{
    public Dictionary<string, int> ByApi { get; } = [];
    public readonly List<string> Asked = [];
    public readonly List<(string Tag, int Days)> Tags = [];

    public Dictionary<string, JsonArray> Votes(IReadOnlyList<string> ids)
    {
        ByApi["GetDetails"] = ByApi.GetValueOrDefault("GetDetails") + 1;
        if (stopAfter is { } n && ByApi["GetDetails"] > n)
            throw new StopApiException("HTTP 429");
        Asked.AddRange(ids);
        return ids.Where(scores.ContainsKey).ToDictionary(w => w, w => new JsonArray(scores[w], 1, 0, 5));
    }

    public (List<string> Ids, string NextCursor, long Total) Trend(string tag, string cursor, int days, int perPage = 100)
    {
        Tags.Add((tag, days));
        var list = trends?.GetValueOrDefault(tag) ?? [];
        return ([.. list], "", list.Count);
    }

    public (List<JsonObject> Items, string NextCursor, long Total) QueryFiles(IReadOnlyList<string> tags, string cursor, int perPage) => throw new NotSupportedException();
    public Dictionary<string, JsonObject> Details(IReadOnlyList<string> ids) => throw new NotSupportedException();
}

/// <summary>A clock for a whole service loop: Sleep moves it on, and past the end it says stop, as the service's own clock does at a SIGTERM.</summary>
sealed class EndingClock(double t, double end) : IClock
{
    public double T { get; private set; } = t;
    public DateTimeOffset Now => SyncClock.At(T);

    public void Sleep(TimeSpan span)
    {
        if (span > TimeSpan.Zero)
            T += span.TotalSeconds;
        if (T >= end)
            throw new StopRequested();
    }
}
