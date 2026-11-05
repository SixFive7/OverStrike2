using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>What A2S says, as the watcher uses it.</summary>
public sealed record EngineInfo(string Version, int Humans, int Bots, string Map);

/// <summary>The engine beside the watcher: A2S and RCON to it on localhost, its process. A test stands in a fake one.</summary>
public interface IEngine
{
    /// <summary>One A2S_INFO. Null means "no answer" - which is never read as "empty".</summary>
    EngineInfo? Probe();

    /// <summary>Commands over one authenticated RCON session: the replies (null for one whose reply did not come), or null when the session
    /// could not be opened.</summary>
    List<string?>? Rcon(IReadOnlyList<string> commands, bool expectReply = true);

    /// <summary>Does the engine answer RCON at all (accepted or refused)? Sends no command: nothing is echoed.</summary>
    bool RconAlive();

    /// <summary>The engine's process id, or null.</summary>
    int? Pid();

    /// <summary>SIGTERM, then SIGKILL, until the process is gone.</summary>
    void Kill(int pid, string why);

    /// <summary>Seconds since the process started, or null.</summary>
    double? Age(int pid);
}

/// <summary>The bridge's HTTP side, as the watcher uses it: POST a text body to /&lt;what&gt;/&lt;secret&gt;. ("ok", reply) when the bridge
/// took it (its answer starts with "ok"), ("failed", why) when not, ("none", null) when no bridge is configured.</summary>
public interface IBridge
{
    string? Address { get; }
    (string Told, string? Reply) Post(string what, string body, double timeoutSeconds);
}

/// <summary>Valve's UpToDateCheck for a build, through this program's one Web API door.</summary>
public interface IValve
{
    (string Answer, int? Required, string? Why) Check(int vint);
}

static partial class Native
{
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int KillCall(int pid, int sig);

    [LibraryImport("libc", EntryPoint = "statvfs", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int StatVfs(string path, byte* buf);

    [LibraryImport("libc", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Access(string path, int mode);

    public const int SigTerm = 15, SigKill = 9;

    /// <summary>access(2) with W_OK: may this user write there? (Not on Linux: assumed.)</summary>
    public static bool MayWrite(string path) => !OperatingSystem.IsLinux() || Access(path, 2) == 0;

    /// <summary>kill(2); false when it failed (no such process, not ours).</summary>
    public static bool Signal(int pid, int sig) => OperatingSystem.IsLinux() && KillCall(pid, sig) == 0;

    /// <summary>Bytes free for an unprivileged user on the volume holding `path` (statvfs f_bavail * f_frsize), or null.</summary>
    public static unsafe long? DiskFree(string path)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return null;
        var buf = stackalloc byte[256];                     // glibc x86_64 struct statvfs: 11 unsigned longs and 6 ints
        if (StatVfs(path, buf) != 0)
            return null;
        var frsize = *(ulong*)(buf + 8);
        var bavail = *(ulong*)(buf + 32);
        return (long)(frsize * bavail);
    }
}

/// <summary>The real engine: 127.0.0.1 and /proc.</summary>
public sealed class LocalEngine(int port, string password, IClock clock, Log log) : IEngine
{
    public static readonly TimeSpan A2sTimeout = TimeSpan.FromSeconds(1);       // a healthy engine answers on localhost in milliseconds
    public static readonly TimeSpan RconProbeTimeout = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan RconTimeout = TimeSpan.FromSeconds(5);

    public EngineInfo? Probe() => A2s.Ask("127.0.0.1", port, A2sTimeout) is { } a ? new EngineInfo(a.Version, a.Humans, a.Bots, a.Map) : null;

    public List<string?>? Rcon(IReadOnlyList<string> commands, bool expectReply = true)
    {
        if (password.Length == 0)
            return null;
        Core.Rcon? r;
        try
        {
            r = Core.Rcon.Open("127.0.0.1", port, password, RconTimeout);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or AggregateException or ObjectDisposedException)
        {
            log.Once("rcon", $"WARNING: RCON connect failed ({e.Message})");
            return null;
        }
        if (r is null)
        {
            log.Once("rcon", "WARNING: RCON authentication was refused");
            return null;
        }
        var replies = new List<string?>();
        using (r)
        {
            foreach (var c in commands)
            {
                try
                {
                    replies.Add(r.Run(c, expectReply));
                }
                catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
                {
                    log.Once("rcon", $"WARNING: RCON failed ({e.Message})");
                    replies.Add(null);
                    return replies;
                }
            }
        }
        return replies;
    }

    public bool RconAlive() => Core.Rcon.Alive("127.0.0.1", port, password, RconProbeTimeout);

    public int? Pid()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(dir);
                if (name.Length == 0 || !name.All(char.IsAsciiDigit))
                    continue;
                byte[] cmd;
                try
                {
                    cmd = File.ReadAllBytes(Path.Combine(dir, "cmdline"));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                var end = Array.IndexOf(cmd, (byte)0);
                var argv0 = Encoding.UTF8.GetString(cmd, 0, end < 0 ? cmd.Length : end);
                if (argv0.EndsWith("linuxsteamrt64/cs2", StringComparison.Ordinal))
                    return int.Parse(name, CultureInfo.InvariantCulture);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    public void Kill(int pid, string why)
    {
        log.Line($"killing the engine (pid {pid}): {why}");
        foreach (var (sig, wait) in new[] { (Native.SigTerm, 10), (Native.SigKill, 5) })
        {
            if (!Native.Signal(pid, sig))
                return;
            for (var i = 0; i < wait * 2; i++)
            {
                if (Pid() != pid)
                    return;
                clock.Sleep(TimeSpan.FromSeconds(0.5));
            }
        }
    }

    public double? Age(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');   // after "pid (comm) ": state is field 3, starttime field 22
            var start = double.Parse(fields[19], CultureInfo.InvariantCulture) / 100.0;   // clock ticks (USER_HZ 100) after boot
            var uptime = double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], CultureInfo.InvariantCulture);
            return Math.Max(0, uptime - start);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>The bridge over HTTP.</summary>
public sealed class HttpBridge((string Base, string Secret)? endpoint) : IBridge
{
    public static readonly string UserAgent = "os2-server/" + (typeof(HttpBridge).Assembly.GetName().Version?.ToString(3) ?? "1");
    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });

    public string? Address => endpoint?.Base;

    public (string Told, string? Reply) Post(string what, string body, double timeoutSeconds)
    {
        if (endpoint is not { } e)
            return ("none", null);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{e.Base}/{what}/{e.Secret}")
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain"),
            };
            req.Headers.UserAgent.ParseAdd(UserAgent);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var resp = Http.Send(req, cts.Token);
            var reply = new StreamReader(resp.Content.ReadAsStream(cts.Token), Encoding.UTF8).ReadToEnd().Trim();
            return reply.StartsWith("ok", StringComparison.Ordinal) ? ("ok", reply) : ("failed", $"the bridge answered '{Words.ChatSafe(reply, 120)}' (HTTP {(int)resp.StatusCode})");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException or WebException)
        {
            return ("failed", $"{ex.GetType().Name}: {ex.Message.Replace(e.Secret, "<secret>")}");
        }
    }
}

/// <summary>Valve's UpToDateCheck, through the one door. fetch: a test's stand-in for the HTTP call (status, body).</summary>
public sealed class ValveApi(WebApiDoor door, Func<int, (int Status, string Body)>? fetch = null) : IValve
{
    static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };

    public (string Answer, int? Required, string? Why) Check(int vint) => door.Call(() =>
    {
        try
        {
            var (status, body) = (fetch ?? Fetch)(vint);
            return status != 200 ? ("unknown", (int?)null, $"HTTP {status}") : Versions.ValveVerdict(body, vint);
        }
        catch (Exception e)   // the HTTP stack throws a wide family; none of them may stop the loop
        {
            return ("unknown", null, $"{e.GetType().Name}: {e.Message}");
        }
    });

    static (int, string) Fetch(int vint)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, string.Format(CultureInfo.InvariantCulture, Versions.ValveUrl, vint));
        req.Headers.UserAgent.ParseAdd(HttpBridge.UserAgent);
        using var resp = Http.Send(req);
        if (resp.StatusCode != HttpStatusCode.OK)
            return ((int)resp.StatusCode, "");
        using var s = resp.Content.ReadAsStream();
        var buf = new byte[65536];
        int n = 0, got;
        while (n < buf.Length && (got = s.Read(buf, n, buf.Length - n)) > 0)
            n += got;
        return (200, Encoding.UTF8.GetString(buf, 0, n));
    }
}

/// <summary>THE OBSERVING WATCHER's engine (watch --observe, beside the acting one): it looks - A2S, RCON status, the console probe's echo -
/// and does nothing: every other command is a log line saying what it would have sent, and the answer is what a running engine gives.</summary>
public sealed class ObservingEngine(IEngine real, Log log) : IEngine
{
    public EngineInfo? Probe() => real.Probe();
    public bool RconAlive() => real.RconAlive();
    public int? Pid() => real.Pid();
    public double? Age(int pid) => real.Age(pid);

    public List<string?>? Rcon(IReadOnlyList<string> commands, bool expectReply = true)
    {
        var look = commands.Where(c => c == "status" || c.StartsWith("echo ", StringComparison.Ordinal)).ToList();
        foreach (var c in commands.Except(look))
            log.Line($"OBSERVE: would send over RCON: {c}");
        if (look.Count == commands.Count)
            return real.Rcon(commands, expectReply);
        var replies = look.Count > 0 ? real.Rcon(look, expectReply) : [];
        if (replies is null)
            return null;
        var answer = new List<string?>();
        var i = 0;
        foreach (var c in commands)
            answer.Add(look.Contains(c) ? replies.ElementAtOrDefault(i++) : c.StartsWith("gg_notice ", StringComparison.Ordinal) ? "gg_notice: shown (observe)" : "");
        return answer;
    }

    public void Kill(int pid, string why) => log.Line($"OBSERVE: would kill the engine (pid {pid}): {why}");
}

/// <summary>The observing watcher's bridge: nothing is posted (the acting watcher reports), and every notice counts as taken.</summary>
public sealed class ObservingBridge(IBridge real, Log log) : IBridge
{
    public string? Address => real.Address;

    public (string Told, string? Reply) Post(string what, string body, double timeoutSeconds)
    {
        if (real.Address is null)
            return ("none", null);
        log.Line($"OBSERVE: would POST /{what}: {body.TrimEnd('\n').Replace("\n", " | ").Replace("\t", " ")}");
        return ("ok", "ok: observe");
    }
}
