using System.Text;
using System.Text.RegularExpressions;
using OverStrike2.Core;

namespace OverStrike2.Sync;

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
            if (exe is not null && exe.StartsWith(prefix, StringComparison.Ordinal) && Posix.Signal(n, Posix.SigKill))
                killed++;
        }
        if (killed > 0)
            clock.Sleep(2);
        return killed;
    }
}
