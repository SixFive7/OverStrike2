using System.Globalization;
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

    /// <summary>One command over an authenticated RCON session of its own: whether the session opened, and the reply - null when none came
    /// or none was asked for.</summary>
    (bool Opened, string? Reply) Rcon(string command, bool expectReply = true);

    /// <summary>Does the engine answer RCON at all (accepted or refused)? Sends no command: nothing is echoed.</summary>
    bool RconAlive();

    /// <summary>The engine's process id, or null.</summary>
    int? Pid();

    /// <summary>SIGTERM, then SIGKILL, until the process is gone.</summary>
    void Kill(int pid, string why);

    /// <summary>Seconds since the process started, or null.</summary>
    double? Age(int pid);
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

    public (bool Opened, string? Reply) Rcon(string command, bool expectReply = true)
    {
        if (password.Length == 0)
            return (false, null);
        Core.Rcon? r;
        try
        {
            r = Core.Rcon.Open("127.0.0.1", port, password, RconTimeout);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or AggregateException or ObjectDisposedException)
        {
            log.Once("rcon", $"WARNING: RCON connect failed ({e.Message})");
            return (false, null);
        }
        if (r is null)
        {
            log.Once("rcon", "WARNING: RCON authentication was refused");
            return (false, null);
        }
        using (r)
        {
            try
            {
                return (true, r.Run(command, expectReply));
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                log.Once("rcon", $"WARNING: RCON failed ({e.Message})");
                return (true, null);
            }
        }
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
