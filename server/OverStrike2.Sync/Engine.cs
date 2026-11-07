using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Sync;

/// <summary>What A2S says of the test engine, as a test load reads it.</summary>
public sealed record EngineInfo(string Map, int Players, int Bots, int Humans);

/// <summary>The new lines of a log file since the last look.</summary>
public interface ILines
{
    List<string> Lines();
}

/// <summary>The test copy of the server, as the steps use it: a test stands in its own.</summary>
public interface ITestEngine
{
    int Starts { get; set; }
    ILines? Tail { get; }
    bool Alive();
    (bool Ok, string Why) Start();
    void Stop();
    string? Rcon(string command);
    EngineInfo? A2s();
}

/// <summary>A log file's new lines: where it ended at the start, from there on; read again from its start when it shrank.</summary>
public sealed class Tail : ILines
{
    readonly string path;
    long position;
    byte[] buffer = [];

    public Tail(string path)
    {
        this.path = path;
        try
        {
            position = new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            position = 0;
        }
    }

    public List<string> Lines()
    {
        long size;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return [];
            size = info.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        if (size < position)
            (position, buffer) = (0, []);
        if (size == position)
            return [];
        byte[] data;
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            f.Seek(position, SeekOrigin.Begin);
            data = new byte[Math.Min(size - position, 8 << 20)];
            data = data.AsSpan(0, f.ReadAtLeast(data, data.Length, false)).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        position += data.Length;
        var all = new byte[buffer.Length + data.Length];
        buffer.CopyTo(all, 0);
        data.CopyTo(all, buffer.Length);
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < all.Length; i++)
        {
            if (all[i] != (byte)'\n')
                continue;
            lines.Add(Encoding.UTF8.GetString(all, start, i - start).TrimEnd('\r'));
            start = i + 1;
        }
        buffer = all.AsSpan(start).ToArray();
        return lines;
    }
}

/// <summary>THE TEST COPY OF THE SERVER: a second engine from a read-only view of the game server's install - a disposable tree of
/// links, the engine's few files copied, no plugins - with its own port, RCON password, join password and Workshop folder (the staging
/// folder), its own anonymous game-server logon, no log push and no access to anything the game server uses.</summary>
public static partial class TestTree
{
    public const string TestServerCfg = """
        // Written by os2-sync for its test engine on every start. Not the game server's server.cfg: no log address.
        hostname "workshop sync test"
        sv_cheats 0
        sv_hibernate_when_empty 0
        sv_lan 0
        log off

        """;

    [GeneratedRegex("^PatchVersion=(\\S+)", RegexOptions.Multiline)] private static partial Regex PatchRx();

    /// <summary>The game's PatchVersion (game/csgo/steam.inf), or null.</summary>
    public static string? PatchVersion(string gameDir)
    {
        try
        {
            var m = PatchRx().Match(File.ReadAllText(Path.Combine(gameDir, "game", "csgo", "steam.inf")));
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var entry in Directory.EnumerateFileSystemEntries(src))
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
                CopyTree(entry, Path.Combine(dst, name));
            else
                File.Copy(entry, Path.Combine(dst, name), true);
        }
    }

    /// <summary>&lt;work&gt;/farm/game: links to every file and folder of &lt;gameDir&gt;/game, except what the test engine must own - the engine's
    /// own folder bin/linuxsteamrt64 (its files copied: the engine finds its root and its Workshop folder from where its executable is),
    /// csgo/cfg (copied, server.cfg replaced, every log address removed), csgo/gameinfo.gi (copied without the Metamod search path: no
    /// plugins), the engine's log files. Returns a line for the log; throws IOException.</summary>
    public static string Build(string gameDir, string workDir, string? cfgDir, SyncClock clock)
    {
        var t0 = clock.Now();
        var src = Path.Combine(gameDir, "game");
        var treeRoot = Path.Combine(workDir, "farm");          // the tree's folder keeps the name it has on every server
        var dst = Path.Combine(treeRoot, "game");
        var stage = Path.Combine(workDir, "staging");
        if (Handover.IsLink(treeRoot))
            File.Delete(treeRoot);
        else if (Directory.Exists(treeRoot))
            Directory.Delete(treeRoot, true);
        Directory.CreateDirectory(Path.Combine(dst, "bin", "linuxsteamrt64"));
        Directory.CreateDirectory(Path.Combine(stage, "steamapps", "workshop"));
        foreach (var path in Directory.EnumerateFileSystemEntries(src))
        {
            var name = Path.GetFileName(path);
            if (name is "bin" or "csgo")
                continue;
            if (name == "cs2.sh")
                File.Copy(path, Path.Combine(dst, name), true);
            else
                File.CreateSymbolicLink(Path.Combine(dst, name), path);
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(src, "bin")))
            if (Path.GetFileName(path) != "linuxsteamrt64")
                File.CreateSymbolicLink(Path.Combine(dst, "bin", Path.GetFileName(path)), path);
        var copied = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(Path.Combine(src, "bin", "linuxsteamrt64")))
        {
            var name = Path.GetFileName(path);
            if (name is "steamapps" or "steam_appid.txt" || name.EndsWith(".log", StringComparison.Ordinal) || Handover.IsLink(path) || !File.Exists(path))
                continue;
            File.Copy(path, Path.Combine(dst, "bin", "linuxsteamrt64", name), true);
            copied++;
        }
        File.CreateSymbolicLink(Path.Combine(dst, "bin", "linuxsteamrt64", "steamapps"), Path.Combine(stage, "steamapps"));
        string csgoSrc = Path.Combine(src, "csgo"), csgoDst = Path.Combine(dst, "csgo");
        Directory.CreateDirectory(csgoDst);
        var own = new HashSet<string> { "addons", "cfg", "gameinfo.gi", "console.log", "console.log.1", "backup_round00.txt", "rpt", "save", "logs" };
        foreach (var path in Directory.EnumerateFileSystemEntries(csgoSrc))
        {
            var name = Path.GetFileName(path);
            if (!own.Contains(name) && !name.StartsWith("backup_round", StringComparison.Ordinal))
                File.CreateSymbolicLink(Path.Combine(csgoDst, name), path);
        }
        CopyTree(Path.Combine(csgoSrc, "cfg"), Path.Combine(csgoDst, "cfg"));
        if (!string.IsNullOrEmpty(cfgDir) && Directory.Exists(cfgDir))
            foreach (var path in Directory.EnumerateFiles(cfgDir))
                if (path.EndsWith(".cfg", StringComparison.Ordinal))
                    File.Copy(path, Path.Combine(csgoDst, "cfg", Path.GetFileName(path)), true);
        var scrubbed = 0;
        foreach (var path in Directory.EnumerateFiles(Path.Combine(csgoDst, "cfg"), "*", SearchOption.AllDirectories))
        {
            string text;
            try
            {
                text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (text.Contains("logaddress", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(path, string.Join("\n", text.Split('\n').Where(l => !l.Contains("logaddress", StringComparison.OrdinalIgnoreCase))));
                scrubbed++;
            }
        }
        File.WriteAllText(Path.Combine(csgoDst, "cfg", "server.cfg"), TestServerCfg);
        var gameinfo = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(csgoSrc, "gameinfo.gi")));
        var vanilla = string.Join("\n", gameinfo.Split('\n').Where(l => !l.Contains("csgo/addons/metamod", StringComparison.Ordinal)));
        File.WriteAllText(Path.Combine(csgoDst, "gameinfo.gi"), vanilla);
        return $"test tree built in {Text.Fixed(clock.Now() - t0, 1)} s: {copied} engine files copied, the plugins' search path "
               + $"{(vanilla != gameinfo ? "removed" : "absent")}, log addresses removed from {scrubbed} cfg file(s)";
    }

    /// <summary>Kill any engine still running from the test tree (a run that died without stopping it): it would hold the port, and the tree
    /// is about to be rebuilt under it. Matched on its executable's path, read from /proc. Returns the count.</summary>
    public static int KillStale(string treeRoot, SyncClock clock)
    {
        if (!Directory.Exists("/proc"))
            return 0;
        var killed = 0;
        var prefix = Path.Join(treeRoot, "");
        if (!prefix.EndsWith('/'))
            prefix += "/";
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var pid = Path.GetFileName(dir);
            if (!Text.Digits(pid) || !int.TryParse(pid, out var n) || n == Environment.ProcessId)
                continue;
            string? exe;
            try
            {
                exe = new FileInfo(Path.Combine(dir, "exe")).LinkTarget;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (exe is not null && exe.StartsWith(prefix, StringComparison.Ordinal) && Native.Signal(n, Native.SigKill))
                killed++;
        }
        if (killed > 0)
            clock.Sleep(2);
        return killed;
    }
}

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
    public ILines? Tail { get; private set; }
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
        proc = Native.StartSession(["bash", "-c", "log=$1; shift; ulimit -c 0; exec \"$@\" </dev/null >>\"$log\" 2>&1", "engine", stdoutLog, .. args], TreeGame, env, false);
        var end = clock.Now() + timeout;
        while (clock.Now() < end)
        {
            clock.Sleep(2);
            if (!Alive())
                return (false, $"the test engine exited during its start (code {(proc.HasExited ? proc.ExitCode.ToString(CultureInfo.InvariantCulture) : "None")})");
            var info = A2s();
            if (info is not null && info.Map == startMap && Rcon("echo ok") is not null)
            {
                Tail = new Tail(Console);
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
        foreach (var sig in new[] { Native.SigTerm, Native.SigKill })
        {
            if (!Native.GroupAlive(pgid))
                break;
            Native.SignalGroup(pgid, sig);
            for (var i = 0; i < StopWait && Native.GroupAlive(pgid) && Waited(); i++)
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

/// <summary>One test load of an item that is in the staging folder, on the test engine: PASS when a level of its own loads, A2S reports
/// it and the engine holds it for the hold time with two bots; FAIL on the error map, a crash, a freeze (A2S silent 45 s), a stock map
/// instead of its own (a stub), no level of its own in time, or the bots thrown off.</summary>
public static partial class TestLoad
{
    [GeneratedRegex("Downloading (\\d+) \\([^/]*/\\s*([\\d.]+)\\s*(KB|MB|GB)\\)")] public static partial Regex DownloadingRx();
    [GeneratedRegex("Level loading started for '([^']*)'")] public static partial Regex LevelRx();
    [GeneratedRegex("kick|Dropped .* from server|was banned", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] public static partial Regex KickRx();
    [GeneratedRegex("rcon from|bot_kick|bot_quota|Unknown command|ReplicateConVar|sv_kick_ban_duration|sv_vote_issue_kick|sv_vote_kick_ban|autokick",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] public static partial Regex NoiseRx();
    [GeneratedRegex("\\[All Chat\\]\\[[^\\]]*\\]:\\s*(.*)$|DISALLOWED WORKSHOP COMMANDS:\\s*(.*)$")] public static partial Regex ChatRx();

    /// <summary>A2S cuts map names at 31 characters: equal, or A2S's name is a 31+-character prefix of the level.</summary>
    public static bool A2sIs(string? a2sMap, string? level)
    {
        var a = (a2sMap ?? "").ToLowerInvariant();
        var l = (level ?? "").ToLowerInvariant();
        return a == l || (a.Length >= 31 && l.StartsWith(a, StringComparison.Ordinal));
    }

    /// <summary>The mode a test load runs a map in: the first mode its tags name, else Arms Race - (mode, game_type, game_mode).</summary>
    public static (string Mode, int GameType, int GameMode) ModeOf(IEnumerable<string> tags)
    {
        var set = tags.ToList();
        foreach (var (mode, tag, gt, gm, _) in GameModes.All)
            if (set.Contains(tag))
                return (mode, gt, gm);
        return (GameModes.All[0].Mode, GameModes.All[0].GameType, GameModes.All[0].GameMode);
    }

    static void Chat(List<string> chat, string line)
    {
        var c = ChatRx().Match(line);
        if (c.Success)
            chat.Add(Text.Cut((c.Groups[1].Success && c.Groups[1].Value.Length > 0 ? c.Groups[1].Value : c.Groups[2].Value).Trim(), 200));
    }

    /// <summary>One test load. Returns a record with verdict pass | errormap | crash | freeze | rejected | kick | abort (somebody came on the
    /// game server: not counted).</summary>
    public static JsonObject Load(ITestEngine engine, string wid, IReadOnlyList<string> levelsIn, IReadOnlyList<string> tags, int hold, Func<bool> stopCheck,
                                  SyncClock clock)
    {
        var (mode, gt, gm) = ModeOf(tags);
        var rec = new JsonObject { ["id"] = wid, ["mode"] = mode, ["utc"] = SyncClock.At(clock.Now()).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) };
        var levels = levelsIn.Select(l => l.ToLowerInvariant()).ToList();
        engine.Tail?.Lines();
        var t0 = clock.Now();
        engine.Rcon($"game_type {gt}; game_mode {gm}; host_workshop_map {wid}");
        string? level = null;
        var seen = new List<string>();
        int? mounted = null;
        double? silent = null, foreignSince = null;
        var lines = new List<string>();
        var chat = new List<string>();
        var limit = 150.0;
        var loaded = false;
        while (clock.Now() - t0 < limit)
        {
            clock.Sleep(1);
            var fresh = engine.Tail?.Lines() ?? [];
            lines.AddRange(fresh);
            foreach (var line in fresh)
            {
                var d = DownloadingRx().Match(line);
                if (d.Success && d.Groups[1].Value == wid && double.TryParse(d.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                {
                    // the engine's own Steam client fetches a newer copy than SteamCMD's: counted in the day's downloads
                    var size = amount * (d.Groups[3].Value switch { "KB" => 1 << 10, "MB" => 1 << 20, _ => 1 << 30 });
                    rec["engine_download"] = Math.Max((long)Math.Truncate(size), Node.Int(rec["engine_download"]));
                    limit = 300;
                }
                if (mounted is null && line.Contains($"Mounting addon '{wid}'", StringComparison.Ordinal))
                    mounted = seen.Count;
                var m = LevelRx().Match(line);
                if (m.Success)
                {
                    seen.Add(m.Groups[1].Value);
                    if (level is null && (levels.Contains(m.Groups[1].Value.ToLowerInvariant()) || m.Groups[1].Value == "error"))
                        level = m.Groups[1].Value;
                }
                Chat(chat, line);
            }
            rec["levels_seen"] = Node.Strings(seen.Take(6));
            if (level == "error" || fresh.Any(l => l.Contains("Error map!", StringComparison.Ordinal)))
                return Done(rec, "errormap", "the engine loaded its error map: nothing in the item it can load");
            if (!engine.Alive())
                return Done(rec, "crash", "the test engine went down while loading it");
            var info = engine.A2s();
            if (info is null)
            {
                silent ??= clock.Now();
                if (clock.Now() - silent > 45)
                    return Done(rec, "freeze", "A2S silent for 45 s while loading it");
                continue;
            }
            silent = null;
            if (stopCheck())
                return Done(rec, "abort", "somebody came on the game server");
            if (level is not null && A2sIs(info.Map, level))
            {
                rec["load_s"] = Math.Round(clock.Now() - t0, 1);
                rec["level"] = level;
                loaded = true;
                break;
            }
            var other = mounted is { } at ? seen.Skip(at).Where(x => !levels.Contains(x.ToLowerInvariant()) && x != "error").ToList() : [];
            if (other.Count > 0 && level is null && A2sIs(info.Map, other[^1]))
            {
                foreignSince ??= clock.Now();
                if (clock.Now() - foreignSince >= 15)
                    return Done(rec, "rejected", $"the addon mounts, but the engine loads {other[^1]}, not a map of its own ({LevelsSaid(levels)})", other[^1]);
            }
            else
                foreignSince = null;
        }
        if (!loaded)
            return Done(rec, "rejected", $"no level of its own ({LevelsSaid(levels)}) within {(long)limit} s; levels seen {(seen.Count > 0 ? string.Join(", ", seen.Take(4)) : "none")}");
        engine.Rcon("bot_join_after_player 0; bot_quota_mode normal; bot_quota 2");
        var bots = new List<int>();
        var end = clock.Now() + hold;
        while (clock.Now() < end)
        {
            clock.Sleep(2);
            var fresh = engine.Tail?.Lines() ?? [];
            lines.AddRange(fresh);
            foreach (var line in fresh)
                Chat(chat, line);
            if (!engine.Alive())
                return Done(rec, "crash", $"the test engine went down {(long)(hold - (end - clock.Now()))} s into the map");
            var info = engine.A2s();
            if (info is null)
            {
                silent ??= clock.Now();
                if (clock.Now() - silent > 45)
                    return Done(rec, "freeze", "A2S silent for 45 s on the map");
                continue;
            }
            silent = null;
            bots.Add(info.Bots);
            if (stopCheck())
                return Done(rec, "abort", "somebody came on the game server");
        }
        engine.Rcon("bot_quota 0");
        var kicks = lines.Where(l => KickRx().IsMatch(l) && !NoiseRx().IsMatch(l)).Select(l => Text.Cut(l.Trim(), 160)).ToList();
        rec["bots"] = bots.Count > 0 ? bots[^1] : 0;
        rec["chat"] = Node.Strings(chat.Take(8));
        if (kicks.Count > 0 && bots.Count > 0 && bots.Max() > 0 && bots[^1] == 0)
            return Done(rec, "kick", $"the bots were thrown off the server ({Text.Cut(kicks[0], 100)})");
        return Done(rec, "pass", $"loads as {Node.Str(rec["level"])} in {Text.Fixed(Node.Float(rec["load_s"]), 0)} s and held {hold} s (bots {Node.Str(rec["bots"])})");
    }

    static string LevelsSaid(List<string> levels) => levels.Count > 0 ? string.Join(", ", levels.Take(3)) : "none in its VPK";

    static JsonObject Done(JsonObject rec, string verdict, string reason, string? level = null)
    {
        rec["verdict"] = verdict;
        if (level is not null)
            rec["level"] = level;
        rec["reason"] = reason;
        return rec;
    }
}
