using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>The test copy: one engine process at a time, started from the test tree on its own port with its own passwords.</summary>
public sealed class TestEngine : ITestEngine
{
    public static readonly (string GameType, string GameMode, string Group, string StartMap) ArmsRace = ("1", "0", "mg_armsrace", "ar_shoots");
    public int QuitWait { get; set; } = 15;      // seconds a clean quit gets
    public int StopWait { get; set; } = 10;      // seconds each signal gets

    readonly SyncConfig cfg;
    readonly string workDir, home;
    readonly SyncClock clock;
    readonly Action<string> log;
    public string TreeGame { get; }
    public string Console { get; }
    public int Port { get; }
    readonly string password = RandomNumberGenerator.GetHexString(32, true);
    Process? proc;
    public int Starts { get; set; }
    public Core.ILines? Tail { get; private set; }
    public bool HasProcess => proc is not null;

    public TestEngine(SyncConfig cfg, string workDir, string home, SyncClock clock, Action<string> log)
    {
        this.cfg = cfg;
        this.workDir = workDir;
        this.home = home;
        this.clock = clock;
        this.log = log;
        TreeGame = Path.Combine(workDir, "farm", "game");
        Console = Path.Combine(TreeGame, "csgo", "console.log");
        Port = cfg.TestPort;
    }

    public string? Rcon(string command)
    {
        try
        {
            using var r = Core.Rcon.Open("127.0.0.1", Port, password, TimeSpan.FromSeconds(6));
            return r?.Run(command);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or AggregateException or ObjectDisposedException)
        {
            return null;
        }
    }

    public EngineInfo? A2s() => Core.A2s.Ask("127.0.0.1", Port, TimeSpan.FromSeconds(2)) is { } a ? new EngineInfo(a.Map, a.Players, a.Bots, a.Humans) : null;

    public bool Alive() => proc is not null && !proc.HasExited;

    /// <summary>A test's own process as this engine's: the stop is what is tested.</summary>
    public void Adopt(Process p) => proc = p;

    public (bool Ok, string Why) Start() => Start(180);

    /// <summary>Start a fresh engine and wait until A2S answers on its start map and RCON works.</summary>
    public (bool Ok, string Why) Start(int timeout)
    {
        Stop();
        var stale = TestTree.KillStale(Path.GetDirectoryName(TreeGame)!, clock);   // whatever still holds the port: a new engine would never answer
        if (stale > 0)
            log($"killed {stale} test engine(s) still running from the test tree");
        if (Starts >= cfg.EngineStartsMax)
            return (false, $"this run's cap of {cfg.EngineStartsMax} test-engine starts is reached");
        Starts++;
        var sdk = Path.Combine(home, ".steam", "sdk64");
        Directory.CreateDirectory(sdk);
        var link = Path.Combine(sdk, "steamclient.so");
        var client = Path.Combine(Path.GetDirectoryName(cfg.SteamCmd) ?? "", "linux64", "steamclient.so");
        if (!Handover.Lexists(link) && File.Exists(client))
            File.CreateSymbolicLink(link, client);
        try
        {
            File.Delete(Console);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        var (gt, gm, group, startMap) = ArmsRace;
        var args = new List<string>
        {
            "./cs2.sh", "-dedicated", "-ip", "0.0.0.0", "-port", Port.ToString(CultureInfo.InvariantCulture), "-console", "-usercon", "-condebug",
            "-maxplayers", "10", "+game_type", gt, "+game_mode", gm, "+mapgroup", group, "+map", startMap,
            "+rcon_password", password, "+sv_password", RandomNumberGenerator.GetHexString(24, true), "+sv_lan", "0", "+tv_enable", "0",
        };
        var env = System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(e => ((string)e.Key, (string?)e.Value ?? ""))
            .Where(e => !e.Item1.StartsWith("CS2_", StringComparison.Ordinal) && !e.Item1.StartsWith("SYNC_", StringComparison.Ordinal)
                        && !e.Item1.StartsWith("STEAM_WEBAPI", StringComparison.Ordinal) && !e.Item1.StartsWith("OVERSTRIKE2_", StringComparison.Ordinal))
            .ToDictionary(e => e.Item1, e => e.Item2);
        env["HOME"] = home;
        var stdoutLog = Path.Combine(workDir, "engine-stdout.log");
        try
        {
            if (new FileInfo(stdoutLog).Length > (10 << 20))
                File.Move(stdoutLog, stdoutLog + ".1", true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        proc = ProcessGroup.Start(["bash", "-c", "log=$1; shift; ulimit -c 0; exec \"$@\" </dev/null >>\"$log\" 2>&1", "engine", stdoutLog, .. args], TreeGame, env, false);
        var end = clock.Now() + timeout;
        while (clock.Now() < end)
        {
            clock.Sleep(2);
            if (!Alive())
                return (false, $"the test engine exited during its start (code {(proc.HasExited ? proc.ExitCode.ToString(CultureInfo.InvariantCulture) : "None")})");
            var info = A2s();
            if (info is not null && info.Map == startMap && Rcon("echo ok") is not null)
            {
                Tail = new Core.Tail(Console, 8 << 20);   // 8 MiB a look at most
                var listing = Rcon("logaddress_list_http") ?? "";
                if (Regex.IsMatch(listing, "https?://"))
                {
                    Stop();
                    return (false, $"the test engine has a log address ({Text.Cut(listing.Trim(), 80)}) - it must never push anything");
                }
                return (true, $"test engine up in {Text.Fixed(timeout - (end - clock.Now()), 0)} s (start {Starts} of this run)");
            }
        }
        Stop();
        return (false, $"the test engine did not answer within {timeout} s");
    }

    /// <summary>The quit, then SIGTERM, then SIGKILL on the group, each with its wait. Under a SIGTERM of the service's own the waits are over at once
    /// and the signals still go, so no frozen engine stays behind; the stop then goes on up.</summary>
    public void Stop()
    {
        if (proc is null)
            return;
        var pgid = proc.Id;
        var stopping = false;
        bool Waited()
        {
            if (stopping)
                return false;
            try
            {
                clock.Sleep(1);
                return true;
            }
            catch (StopRequested)
            {
                stopping = true;
                return false;
            }
        }
        if (Alive())
        {
            Rcon("quit");
            for (var i = 0; i < QuitWait && Alive() && Waited(); i++)
            {
            }
        }
        foreach (var sig in new[] { Posix.SigTerm, Posix.SigKill })
        {
            if (!Posix.GroupAlive(pgid))
                break;
            Posix.SignalGroup(pgid, sig);
            for (var i = 0; i < StopWait && Posix.GroupAlive(pgid) && Waited(); i++)
            {
            }
        }
        try
        {
            proc.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }
        proc.Dispose();
        proc = null;
        if (stopping)
            throw new StopRequested();
    }
}
