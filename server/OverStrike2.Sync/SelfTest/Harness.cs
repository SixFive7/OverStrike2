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

    /// <summary>A new config: the defaults, on the paths of the moment.</summary>
    public static SyncConfig Cfg() => new()
    {
        StateDir = State, WorkDir = Work, GameDir = GameRoot, BridgeData = Data, BridgeUrl = BridgeUrl, BridgeSecret = BridgeSecret, Depotcache = Depotcache,
        ChangedSince = "2026-10-25", GameHost = "", Home = Path.Combine(T.Root, "home"),
    };

    public static SyncClock C(FakeClock f) => new(f, Paris);

    public static SyncRun Run(SyncConfig cfg, FakeClock fc, bool manual = true, Action<double>? sleep = null) =>
        new(cfg, C(fc), Hooks, Log, manual, sleep, door: new WebApiDoor(cfg.ApiGap, fc));

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
