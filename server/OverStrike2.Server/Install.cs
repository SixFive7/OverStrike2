using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>Fetches a file: what `install` downloads the pinned components with. A test stands in local archives.</summary>
public interface IDownloader
{
    void Download(string url, string path);
}

/// <summary>The real downloader: HTTP with two deadlines of its own. Their timers run on <c>timers</c>, the system's unless a self-test
/// gives timers it moves by hand.</summary>
public sealed class HttpDownloader(IClock clock, TimeSpan? noData = null, TimeSpan? atMost = null, TimeProvider? timers = null) : IDownloader
{
    /// <summary>No byte for this long is a stalled download, before the headers as much as after them.</summary>
    public static readonly TimeSpan NoData = TimeSpan.FromSeconds(60);

    /// <summary>One try takes at most this long, however it trickles.</summary>
    public static readonly TimeSpan AtMost = TimeSpan.FromMinutes(10);

    // No timeout of the client's own: it stops counting once the headers are in, and a body that stalls after them would hold install, so
    // pre.sh, so the engine's start, for ever. Once's two deadlines cover all of it.
    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = true, ConnectTimeout = TimeSpan.FromSeconds(30) }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    /// <summary>GET with redirects, three tries more on a transient failure (no answer, a stall, 408, 429, 5xx), 1 s, 2 s and 4 s apart.</summary>
    public void Download(string url, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Once(url, path, attempt).GetAwaiter().GetResult();
                return;
            }
            catch (Exception e) when (attempt < 3 && e is HttpRequestException or TaskCanceledException or IOException)
            {
                clock.Sleep(TimeSpan.FromSeconds(1 << attempt));
            }
        }
    }

    async Task Once(string url, string path, int attempt)
    {
        var quietFor = noData ?? NoData;
        var whole = atMost ?? AtMost;
        var time = timers ?? TimeProvider.System;
        using var deadline = new CancellationTokenSource(whole, time);
        using var quiet = new CancellationTokenSource(quietFor, time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, quiet.Token);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(HttpBridge.UserAgent);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, either.Token);
            var code = (int)resp.StatusCode;
            if (attempt < 3 && (code is 408 or 429 || code >= 500))
                throw new HttpRequestException($"HTTP {code}");
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(either.Token);
            await using var f = File.Create(path);
            var buffer = new byte[81920];
            while (true)
            {
                quiet.CancelAfter(quietFor);
                var n = await s.ReadAsync(buffer, either.Token);
                if (n == 0)
                    return;
                await f.WriteAsync(buffer.AsMemory(0, n));
            }
        }
        catch (OperationCanceledException) when (either.IsCancellationRequested)
        {
            static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            throw new IOException(deadline.IsCancellationRequested ? $"not done after {Seconds(whole)} s" : $"no data for {Seconds(quietFor)} s");
        }
    }
}

/// <summary>THE PLUGIN LAYER, before every engine start (os2-server install, which the image's pre.sh hook hands over to): Metamod:Source,
/// CounterStrikeSharp and MultiAddonManager at the pinned versions below, installed once each (a marker file per component records the
/// version; only the current one's is kept, so pinning an older one again extracts it again), the gameinfo.gi search path re-applied on
/// EVERY start (SteamCMD updates rewrite that file), MultiAddonManager loaded with the HUD's Workshop item (hud.addon, which the settings
/// require: this never runs without it), and the plugins copied in from their read-only folder (paths.plugins; the ones copied before
/// that are gone from it are removed). plugin.enabled false is the kill switch after a breaking Valve update: the search path goes and
/// the server runs vanilla, the files stay on disk. Then the
/// console logs are rotated, the maps put in order (organize) and what was put in place is recorded (update-watch/installed.json, which the
/// watcher compares with what is on disk: a new version restarts the engine).
///
/// To bump a pin: read the components' release pages (github.com/alliedmodders/metamod-source, roflmuffin/CounterStrikeSharp,
/// Source2ZE/MultiAddonManager), change the version below, rebuild the plugin (plugin/OverStrike2) against the same CounterStrikeSharp.API
/// NuGet version, and ship both: the new program restarts the engine by itself at the end of a match. Every step logs what it did, and a
/// failure costs the plugin layer, never the server, but for one: with the layer on and no MultiAddonManager on disk after its download was
/// tried, the HUD cannot reach anybody, and a server without the HUD cannot exist. That is <see cref="Settings.RefusedExit"/>: the engine does
/// not start, and the next start tries the download again.</summary>
public sealed partial class Install(ServerConfig config, Log log, IDownloader downloader, Func<string, string?> env, IClock clock, TimeZoneInfo zone)
{
    // Metamod:Source 2.0 dev build 1483: SourceHook API 18 and KHook, which CounterStrikeSharp 1.0.375+ and MultiAddonManager 1.6+ need
    // (1411 and older: API 17; 1460 segfaults on current CS2). Bump with CounterStrikeSharp.
    public const string MetamodVersion = "2.0.0-git1483";
    // CounterStrikeSharp with its .NET runtime (Linux): ConVar<T>, the client-authorization engine calls fixed (s2sdk's IVEngineServer2
    // slot fix); 1.0.374's library asked for an executable stack, 1.0.375 and later do not. 1.0.380 is its fix for CS2 1.41.9.7 (build 14197),
    // the SDK's net messages as plain records: 1.0.379 segfaults at every map change on that build. Its managed API only adds to 1.0.379's, so the
    // plugin, built against 1.0.379, loads and runs on it until the 1.0.380 package is published.
    public const string CssVersion = "1.0.380";
    // MultiAddonManager, the SteamRT4 build for the image's runtime: hands the HUD's Workshop addon to every client; reads the server's addon
    // string and client list through the SDK; tracks the Workshop map when a level really loads, also after a ChangeLevel. 1.7.4 is its fix for
    // CS2 1.41.9.7 (build 14197): 1.7.3 does not load on that build, and its main before the fix crashed the server at any client connect.
    public const string MamVersion = "1.7.4";

    public static string MetamodUrl => $"https://github.com/alliedmodders/metamod-source/releases/download/{ReplaceFirst(MetamodVersion, "-git", ".")}/mmsource-{MetamodVersion}-linux.tar.gz";
    public static string CssUrl => $"https://github.com/roflmuffin/CounterStrikeSharp/releases/download/v{CssVersion}/counterstrikesharp-with-runtime-linux-{CssVersion}.zip";
    public static string MamUrl => $"https://github.com/Source2ZE/MultiAddonManager/releases/download/v{MamVersion}/MultiAddonManager-v{MamVersion}-steamrt4.tar.gz";

    public const long RotateAt = 50L << 20;           // -condebug appends for ever: a console.log over this is moved aside before the engine starts
    const string Marker = ".pre.sh-installed-";       // the marker's name, the same since the shell version: no needless reinstall
    const string CopiedList = ".pre.sh-copied";

    [GeneratedRegex(@"^([ \t]*)Game_LowViolence[ \t]+csgo_lv.*$")] private static partial Regex LowViolence();

    ServerPaths P => config.Paths;

    bool MamThere => File.Exists(Path.Combine(P.Csgo, "addons", "multiaddonmanager", "bin", "multiaddonmanager.so"));

    static string ReplaceFirst(string s, string what, string with) => s.IndexOf(what, StringComparison.Ordinal) is var i and >= 0 ? s[..i] + with + s[(i + what.Length)..] : s;

    /// <summary>Everything above. serverFrom: this program's own file as shipped (what the watcher compares). Never throws.</summary>
    public int Run(string? serverFrom, string settingsPath)
    {
        VersionParts? parts = null;
        try
        {
            parts = Fingerprint.Compute(P, serverFrom, settingsPath, env);   // before the copies: a change during them is seen as a new version
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log.Line($"WARNING: our parts could not be read for the record ({e.Message}) - a new version is not detected until the next start");
        }
        try
        {
            if (config.PluginLayer)
            {
                Component("metamod", MetamodVersion, MetamodUrl, "tar");
                Component("counterstrikesharp", CssVersion, CssUrl, "zip");
                Component("multiaddonmanager", MamVersion, MamUrl, "tar");
                Gameinfo(true);
                var hud = Hud() ? $"on (Workshop addon {config.HudAddon})" : $"NOT delivered (Workshop addon {config.HudAddon}: the WARNING above says why)";
                CopyPlugins();
                log.Line($"SUMMARY: plugin layer on, Metamod {MetamodVersion} + CounterStrikeSharp {CssVersion} layer active, HUD {hud} (check 'meta list' and 'css_plugins list' on the server console)");
            }
            else
            {
                Gameinfo(false);
                log.Line("SUMMARY: plugin layer off (plugin.enabled false), vanilla server; Metamod and CounterStrikeSharp files are left on disk but not loaded");
            }
        }
        catch (Exception e)   // the plugin layer is never worth the server
        {
            log.Line($"WARNING: the plugin layer stopped ({e.GetType().Name}: {e.Message})");
        }
        if (config.PluginLayer && !MamThere)   // after the catch: the layer may have stopped before it got that far
        {
            log.Line($"ALERT: MultiAddonManager is not installed, so the HUD (Workshop addon {config.HudAddon}) cannot reach the players - a server without the HUD cannot exist, so the engine does not start; "
                     + "the next start tries its download again");
            return Settings.RefusedExit;
        }
        RotateConsoleLogs();
        try
        {
            log.Line(new MapsFolder(config, log, clock, zone).Organize());
        }
        catch (Exception e)
        {
            log.Line($"WARNING: the maps folder stopped ({e.GetType().Name}: {e.Message}) - the engine starts anyway");
        }
        if (parts is not null)
        {
            try
            {
                new Installed(parts, clock.Now.ToUnixTimeSeconds(), serverFrom).Write(P.InstalledFile);
                log.Line($"installed version {parts.Total} recorded ({P.InstalledFile})");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Line($"WARNING: {P.InstalledFile} not written ({e.Message}) - the watcher cannot tell a new version from this one");
            }
        }
        return 0;
    }

    /// <summary>Download and extract a component into game/csgo unless the marker for this exact version sits in its addons folder
    /// (deleting the folder reinstalls it).</summary>
    public bool Component(string name, string version, string url, string kind)
    {
        var addons = Path.Combine(P.Csgo, "addons", name);
        var marker = Path.Combine(addons, Marker + version);
        if (File.Exists(marker))
        {
            log.Line($"{name} {version} already installed");
            return true;
        }
        var tmp = Path.Combine(P.GameDir, "pre.sh-download." + System.Security.Cryptography.RandomNumberGenerator.GetHexString(6, true));
        try
        {
            Directory.CreateDirectory(tmp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Line($"WARNING: cannot create a download directory, {name} not installed ({e.Message})");
            return false;
        }
        try
        {
            log.Line($"downloading {name} {version} from {url}");
            var archive = Path.Combine(tmp, "archive");
            downloader.Download(url, archive);
            Directory.CreateDirectory(P.Csgo);
            if (kind == "tar")
            {
                using var fs = File.OpenRead(archive);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gz, P.Csgo, overwriteFiles: true);
            }
            else
                ZipFile.ExtractToDirectory(archive, P.Csgo, overwriteFiles: true);
            Directory.CreateDirectory(addons);
            foreach (var old in Directory.EnumerateFiles(addons, Marker + "*"))
                File.Delete(old);
            File.WriteAllBytes(marker, []);
            log.Line($"{name} {version} installed into {Path.Combine(P.Csgo, "addons")}");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException or InvalidDataException or WebException)
        {
            log.Line($"WARNING: {name} {version} download or extraction failed ({e.Message})");
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Exactly one "Game csgo/addons/metamod" search path, right after the Game_LowViolence line that heads the list, or none at
    /// all. Idempotent, so it runs on every start.</summary>
    public bool Gameinfo(bool on)
    {
        var gi = Path.Combine(P.Csgo, "gameinfo.gi");
        if (!File.Exists(gi))
        {
            log.Line($"WARNING: {gi} is missing, nothing patched");
            return false;
        }
        var text = File.ReadAllText(gi);
        var has = text.Contains("csgo/addons/metamod", StringComparison.Ordinal);
        if (on)
        {
            if (has)
            {
                log.Line("gameinfo.gi already lists csgo/addons/metamod");
                return true;
            }
            var lines = text.Split('\n').ToList();
            for (var i = lines.Count - 1; i >= 0; i--)
                if (LowViolence().Match(lines[i]) is { Success: true } m)
                    lines.Insert(i + 1, m.Groups[1].Value + "Game\tcsgo/addons/metamod");
            var patched = string.Join('\n', lines);
            if (!patched.Contains("csgo/addons/metamod", StringComparison.Ordinal))
            {
                log.Line("WARNING: gameinfo.gi has no Game_LowViolence line to anchor on (Valve changed the file?); Metamod will not load");
                return false;
            }
            Write(gi, patched);
            log.Line("gameinfo.gi: csgo/addons/metamod search path added");
            return true;
        }
        if (!has)
        {
            log.Line("gameinfo.gi already vanilla");
            return true;
        }
        Write(gi, string.Join('\n', text.Split('\n').Where(l => !l.Contains("csgo/addons/metamod", StringComparison.Ordinal))));
        log.Line("gameinfo.gi: csgo/addons/metamod search path removed");
        return true;
    }

    static void Write(string path, string text)
    {
        var mode = OperatingSystem.IsWindows() ? (UnixFileMode?)null : File.GetUnixFileMode(path);
        Files.WriteAtomic(path, text, mode);
    }

    /// <summary>The in-game HUD: MultiAddonManager is loaded and the server mounts hud.addon, the Workshop id of the published HUD addon
    /// (mm_extra_addons), so every map change - a changelevel to an official map included - carries it to every client. A client-only addon
    /// (mm_client_extra_addons) dropped the HUD at every official-map change; the mounted one keeps it, with one reconnect per change, at about
    /// 5 MiB of the server's memory. The settings have checked the id: digits only, so it is safe in the cfg. False when it could not: no
    /// MultiAddonManager (which <see cref="Run"/> then refuses), or a cfg that cannot be written.</summary>
    public bool Hud()
    {
        var vdf = Path.Combine(P.Csgo, "addons", "metamod", "multiaddonmanager.vdf");
        var cfg = Path.Combine(P.Csgo, "cfg", "multiaddonmanager", "multiaddonmanager.cfg");
        var id = config.HudAddon;
        void NoVdf()
        {
            try
            {
                File.Delete(vdf);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        if (!MamThere)
        {
            NoVdf();
            log.Line($"WARNING: MultiAddonManager is not installed; HUD addon {id} cannot reach the players");
            return false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
            File.WriteAllText(cfg, $"""
                // Written by os2-server install before every engine start, from hud.addon in the settings file. Edits here do not survive.
                mm_extra_addons "{id}"              // The HUD addon, mounted by the server: every map change, an official map's too, carries it to every client.
                mm_client_extra_addons ""           // Nothing for the clients alone (a client-only addon is lost at a changelevel to an official map).
                mm_extra_addons_timeout 10
                mm_addon_connection_timeout 0       // Never kick a client for taking long: a first download of a big workshop map can take minutes.
                mm_addon_mount_download 0           // The server's own copy is not downloaded again at a mount; clients use their own Steam copy.
                mm_cache_clients_with_addons 1      // A client that has the addon is not sent it again on every map change (one reconnect less each time).
                mm_cache_clients_duration 0         // ...for the life of the server process.
                mm_block_disconnect_messages 0      // On, 1.6.1 crashed clients (MultiAddonManager #77); off costs a "left the game" line while someone downloads.
                mm_addon_debug 0

                """.Replace("\r\n", "\n"));
            Directory.CreateDirectory(Path.GetDirectoryName(vdf)!);
            File.WriteAllText(vdf, "\"Metamod Plugin\"\n{\n\t\"alias\"\t\"multiaddonmanager\"\n\t\"file\"\t\"addons/multiaddonmanager/bin/multiaddonmanager\"\n}\n");
            log.Line($"HUD on: MultiAddonManager {MamVersion} loaded, every client gets Workshop addon {id}");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            NoVdf();
            log.Line($"WARNING: could not write the MultiAddonManager config ({e.Message}); HUD addon not delivered");
            return false;
        }
    }

    /// <summary>Replace every plugin folder from the read-only folder in CounterStrikeSharp's plugins directory, and remove the folders an
    /// earlier start copied from that same folder which are gone from it now, so a plugin taken off it stops loading at the next start.
    /// What was copied is listed in .pre.sh-copied beside them (its first line names the source); a folder never copied from there -
    /// CounterStrikeSharp's own files, anything put there by hand - is never touched, and a source with no plugin folder at all (empty or
    /// missing) removes nothing.</summary>
    public bool CopyPlugins()
    {
        var dst = Path.Combine(P.Csgo, "addons", "counterstrikesharp", "plugins");
        var list = Path.Combine(dst, CopiedList);
        if (!Directory.Exists(dst))
        {
            log.Line($"WARNING: {dst} is missing (CounterStrikeSharp not installed?), no plugins copied");
            return false;
        }
        var names = Directory.Exists(P.Plugins)
            ? Directory.EnumerateDirectories(P.Plugins).Select(d => Path.GetFileName(d)!).Where(n => !n.StartsWith('.')).OrderBy(n => n, StringComparer.Ordinal).ToList()
            : [];
        var copied = new List<string>();
        foreach (var name in names)
        {
            try
            {
                var target = Path.Combine(dst, name);
                if (Directory.Exists(target))
                    Directory.Delete(target, true);
                CopyTree(Path.Combine(P.Plugins, name), target);
                copied.Add(name);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Line($"WARNING: plugin {name} could not be copied ({e.Message})");
            }
        }
        if (names.Count == 0)
        {
            log.Line($"plugins: {P.Plugins} holds no plugin folder - nothing copied, nothing removed");
            return true;
        }
        var removed = new List<string>();
        if (File.Exists(list))
        {
            var lines = File.ReadAllLines(list);
            if (lines.Length > 0 && lines[0] == $"src {P.Plugins}")
                foreach (var name in lines.Skip(1))
                {
                    if (name.Length == 0 || name.Contains('/') || name.StartsWith('.') || names.Contains(name) || !Directory.Exists(Path.Combine(dst, name)))
                        continue;
                    try
                    {
                        Directory.Delete(Path.Combine(dst, name), true);
                        removed.Add(name);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        log.Line($"WARNING: plugin {name} is gone from {P.Plugins} but could not be removed ({e.Message})");
                    }
                }
        }
        try
        {
            File.WriteAllText(list, string.Join("\n", new[] { $"src {P.Plugins}" }.Concat(names)) + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Line($"WARNING: could not write {list} ({e.Message})");
        }
        log.Line($"plugins copied from {P.Plugins}: {(copied.Count > 0 ? string.Join(" ", copied) : "(none)")}{(removed.Count > 0 ? "; removed, gone from there: " + string.Join(" ", removed) : "")}");
        return true;
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    /// <summary>-condebug appends to console.log for ever: every one over RotateAt moves aside now, while nothing holds it open.</summary>
    public void RotateConsoleLogs()
    {
        foreach (var path in P.ConsoleCandidates)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > RotateAt)
                {
                    File.Move(path, path + ".1", true);
                    log.Line($"rotated {path} (over {RotateAt >> 20} MB) to {path}.1");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Line($"WARNING: {path} could not be rotated ({e.Message})");
            }
        }
    }
}
