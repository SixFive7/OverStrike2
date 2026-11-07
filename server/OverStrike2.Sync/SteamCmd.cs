using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace OverStrike2.Sync;

/// <summary>One SteamCMD session, as the steps use it: a test stands in its own.</summary>
public interface ISteamCmd
{
    (bool Ok, string Why) Start();

    /// <summary>(ok, reason, bytes) for one `workshop_download_item 730 &lt;id&gt;`.</summary>
    (bool Ok, string Reason, long Bytes) Download(string wid);

    void Stop();
}

/// <summary>The few libc calls the sync needs: signals to a process group, its liveness, a volume's free space; and a process started in a
/// session of its own.</summary>
public static partial class Native
{
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int KillCall(int pid, int sig);

    [LibraryImport("libc", EntryPoint = "statvfs", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int StatVfs(string path, byte* buf);

    public const int SigTerm = 15, SigKill = 9, Eperm = 1;

    /// <summary>kill(2); false when it failed.</summary>
    public static bool Signal(int pid, int sig) => OperatingSystem.IsLinux() && KillCall(pid, sig) == 0;

    /// <summary>killpg(2), as kill(-pgid).</summary>
    public static bool SignalGroup(int pgid, int sig) => OperatingSystem.IsLinux() && pgid > 0 && KillCall(-pgid, sig) == 0;

    /// <summary>Is anything left of this process group? Its leader's exit is not the group's: the engine's wrapper (cs2.sh) dies at a
    /// SIGTERM, a frozen engine under it does not, and it keeps the port: a stop that believed the wrapper would leave the next engine no
    /// port to answer on.</summary>
    public static bool GroupAlive(int pgid)
    {
        if (!OperatingSystem.IsLinux() || pgid <= 0)
            return false;
        if (KillCall(-pgid, 0) == 0)
            return true;
        return Marshal.GetLastPInvokeError() == Eperm;
    }

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

    /// <summary>A process in a session of its own (setsid), so its whole group can be signalled: the program, its arguments, a folder to
    /// start in, its environment. With stdin and stdout piped when asked.</summary>
    public static Process StartSession(IReadOnlyList<string> argv, string cwd, IDictionary<string, string>? env, bool pipes)
    {
        var psi = new ProcessStartInfo("setsid") { WorkingDirectory = cwd, UseShellExecute = false };
        foreach (var a in argv)
            psi.ArgumentList.Add(a);
        if (env is not null)
        {
            psi.Environment.Clear();
            foreach (var (k, v) in env)
                psi.Environment[k] = v;
        }
        if (pipes)
        {
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.StandardInputEncoding = new UTF8Encoding(false);
        }
        return Process.Start(psi) ?? throw new IOException($"{argv[0]} did not start");
    }
}

/// <summary>One interactive SteamCMD process, logged in anonymously - never the owner's account, which is logged in nowhere - fed one command
/// at a time through its stdin. Its output goes to &lt;work&gt;/steamcmd.log (20 MB, then .1).</summary>
public sealed partial class SteamCmdProcess(string exe, string installDir, string logPath, string? home, SyncClock clock) : ISteamCmd
{
    [GeneratedRegex("\\x1b\\[[0-9;]*m")] private static partial Regex AnsiRx();

    readonly object gate = new();
    readonly List<byte> buffer = [];
    Process? proc;
    bool eof;

    public int LoginTimeout { get; set; } = 240;
    public int DownloadTimeout { get; set; } = 900;

    void Log(ReadOnlySpan<byte> data)
    {
        try
        {
            if (File.Exists(logPath) && new FileInfo(logPath).Length > (20 << 20))
                File.Move(logPath, logPath + ".1", true);
            using var f = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            f.Write(data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    void Reader(Process p)
    {
        var s = p.StandardOutput.BaseStream;
        var chunk = new byte[65536];
        try
        {
            int n;
            while ((n = s.Read(chunk, 0, chunk.Length)) > 0)
            {
                Log(chunk.AsSpan(0, n));
                lock (gate)
                {
                    buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                    Monitor.PulseAll(gate);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
        lock (gate)
        {
            eof = true;
            Monitor.PulseAll(gate);
        }
    }

    /// <summary>The text up to and including the first match of `rx` (and what came with it), or null: the time ran out or the process ended.</summary>
    string? ReadUntil(Regex rx, double timeout)
    {
        var end = clock.Now() + timeout;
        lock (gate)
        {
            while (true)
            {
                var text = AnsiRx().Replace(Encoding.UTF8.GetString(buffer.ToArray()), "");
                if (rx.IsMatch(text))
                {
                    buffer.Clear();
                    return text;
                }
                if (eof)
                    return null;
                var left = end - clock.Now();
                if (left <= 0)
                    return null;
                Monitor.Wait(gate, TimeSpan.FromSeconds(Math.Min(1.0, left)));
                if (clock.Clock is StoppableClock s)
                    s.Check();
            }
        }
    }

    public (bool Ok, string Why) Start()
    {
        Directory.CreateDirectory(installDir);
        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
        if (!string.IsNullOrEmpty(home))
            env["HOME"] = home;
        Log(Encoding.UTF8.GetBytes($"\n==== {clock.Local().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} SteamCMD session (anonymous)\n"));
        lock (gate)
        {
            buffer.Clear();
            eof = false;
        }
        proc = Native.StartSession(["bash", "-c", "exec \"$@\" 2>&1", "steamcmd", exe, "+force_install_dir", installDir, "+login", "anonymous"], "/tmp", env, true);
        var p = proc;
        new Thread(() => Reader(p)) { IsBackground = true, Name = "steamcmd-out" }.Start();
        var text = ReadUntil(new Regex("Steam>|" + SteamCmdWords.LoginFailRx.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), LoginTimeout);
        if (text is null)
        {
            Stop();
            return (false, $"no Steam> prompt within {LoginTimeout} s");
        }
        var bad = SteamCmdWords.LoginFailRx.Match(text);
        if (bad.Success || !text.Replace("\n", "").Contains("Connecting anonymously to Steam Public...OK", StringComparison.Ordinal))
        {
            var reason = bad.Success ? bad.Value : "the anonymous login did not answer OK";
            Stop();
            return (false, reason);
        }
        return (true, "logged in anonymously");
    }

    public (bool Ok, string Reason, long Bytes) Download(string wid)
    {
        if (proc is null || proc.HasExited)
            return (false, SteamCmdWords.NotRunning, 0);
        try
        {
            proc.StandardInput.BaseStream.Write(Encoding.UTF8.GetBytes($"workshop_download_item 730 {wid}\n"));
            proc.StandardInput.BaseStream.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return (false, $"{SteamCmdWords.Stdin} {e.Message}", 0);
        }
        var rx = new Regex($"Success\\. Downloaded item {wid} to \"[^\"]*\" \\(\\d+ bytes\\)|ERROR! Download item {wid} failed \\([^)]*\\)");
        var text = ReadUntil(rx, DownloadTimeout);
        if (text is null)
            return (false, $"{SteamCmdWords.Stall} {Text.Hms(DownloadTimeout)}", 0);
        var ok = SteamCmdWords.SuccessRx().Match(text);
        if (ok.Success && ok.Groups[1].Value == wid)
            return (true, "OK", long.Parse(ok.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        var bad = SteamCmdWords.FailureRx().Match(text);
        return (false, bad.Success ? bad.Groups[2].Value : "unknown answer", 0);
    }

    public void Stop()
    {
        if (proc is null)
            return;
        var pid = proc.Id;
        try
        {
            if (!proc.HasExited)
            {
                proc.StandardInput.BaseStream.Write("quit\n"u8);
                proc.StandardInput.BaseStream.Flush();
                proc.WaitForExit(30000);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        if (Native.GroupAlive(pid))      // steamcmd.sh may be gone with its binary still running under it
        {
            Native.SignalGroup(pid, Native.SigKill);
            try
            {
                proc.WaitForExit(10000);
            }
            catch (InvalidOperationException)
            {
            }
            for (var i = 0; i < 50 && Native.GroupAlive(pid); i++)   // the whole group, not only its leader: a new session starts clean
                Thread.Sleep(200);
        }
        proc.Dispose();
        proc = null;
    }
}

/// <summary>What SteamCMD says, and what the sync makes of it.</summary>
public static partial class SteamCmdWords
{
    [GeneratedRegex("Success\\. Downloaded item (\\d+) to \"[^\"]*\" \\((\\d+) bytes\\)")] public static partial Regex SuccessRx();
    [GeneratedRegex("ERROR! Download item (\\d+) failed \\(([^)]*)\\)")] public static partial Regex FailureRx();
    [GeneratedRegex("FAILED (?:to log ?in|login)|Login Failure|Rate Limit Exceeded|Invalid Password|No Connection", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LoginFail();
    [GeneratedRegex("rate limit|limit exceeded|too many", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] public static partial Regex StopRx();
    /// <summary>About one item (1 of 2,129 once) - two in a row is about us: that stops the day.</summary>
    [GeneratedRegex("access denied", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] public static partial Regex AccessDeniedRx();

    public static Regex LoginFailRx => LoginFail();

    /// <summary>SteamCmdProcess.Download's answer when SteamCMD said nothing about the item in time: a stall.</summary>
    public const string Stall = "no result within";

    /// <summary>Its answers when there is no SteamCMD to ask any more: the process has ended, or its stdin is gone.</summary>
    public const string NotRunning = "SteamCMD is not running", Stdin = "SteamCMD's stdin:";
}
