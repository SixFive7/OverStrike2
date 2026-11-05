using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OverStrike2.Core;
using static OverStrike2.Server.SelfTest.T;

namespace OverStrike2.Server.SelfTest;

/// <summary>A downloader that hands out local archives, and fails for a URL it does not know.</summary>
sealed class LocalDownloads : IDownloader
{
    public readonly Dictionary<string, string> Files = [];
    public readonly List<string> Asked = [];

    public void Download(string url, string path)
    {
        Asked.Add(url);
        if (!Files.TryGetValue(url, out var file))
            throw new HttpRequestException("HTTP 404");
        File.Copy(file, path, true);
    }
}

/// <summary>THE PLUGIN LAYER's install (what pre.sh hands over to), the record of what it put in place, the bridge over real HTTP, and
/// the launcher.</summary>
static class InstallTests
{
    const string Gameinfo = "\"GameInfo\"\n{\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n\t\t\tGame_LowViolence\tcsgo_lv // Perfect World content override\n"
                            + "\t\t\tGame\tcsgo\n\t\t\tGame\tcsgo_imported\n\t\t}\n\t}\n}\n";

    static string Archive(string dir, string kind, params (string Path, string Text)[] files)
    {
        var src = Path.Combine(dir, "src-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var (p, text) in files)
            Put(Path.Combine(src, p), text);
        var archive = Path.Combine(dir, Path.GetFileName(src) + (kind == "tar" ? ".tar.gz" : ".zip"));
        if (kind == "tar")
        {
            using var f = File.Create(archive);
            using var gz = new GZipStream(f, CompressionLevel.Fastest);
            TarFile.CreateFromDirectory(src, gz, includeBaseDirectory: false);
        }
        else
            ZipFile.CreateFromDirectory(src, archive);
        return archive;
    }

    public static void Run(string root)
    {
        var game = Dir("install-game");
        var csgo = Path.Combine(game, "game", "csgo");
        Put(Path.Combine(csgo, "gameinfo.gi"), Gameinfo);
        var plugins = Dir("install-plugins");
        var config = Scenario.Copy(Scenario.Template, new ServerPaths
        {
            GameDir = game, BridgeData = Dir("install-data"), Handover = Path.Combine(game, "handover"),
            Depotcache = Path.Combine(game, "depotcache"), Plugins = plugins,
        });
        config.HudAddon = "3823993566";
        var lines = new List<string>();
        var log = new Log("test", SystemClock.Instance, Scenario.Paris, lines.Add);
        var downloads = new LocalDownloads();
        var tmp = Dir("install-archives");
        downloads.Files[Install.MetamodUrl] = Archive(tmp, "tar", ("addons/metamod/bin/linuxsteamrt64/metamod.2.cs2.so", "mm"), ("addons/metamod.vdf", "vdf"));
        downloads.Files[Install.CssUrl] = Archive(tmp, "zip", ("addons/counterstrikesharp/bin/linuxsteamrt64/counterstrikesharp.so", "css"), ("addons/counterstrikesharp/plugins/.keep", ""));
        downloads.Files[Install.MamUrl] = Archive(tmp, "tar", ("addons/multiaddonmanager/bin/multiaddonmanager.so", "mam"));
        var install = new Install(config, log, downloads, _ => null, SystemClock.Instance, Scenario.Paris);

        Check("install: the pinned URLs are the components' own release pages' (Metamod's build number without -git)",
              Install.MetamodUrl == $"https://github.com/alliedmodders/metamod-source/releases/download/2.0.0.1483/mmsource-{Install.MetamodVersion}-linux.tar.gz"
              && Install.CssUrl.EndsWith($"counterstrikesharp-with-runtime-linux-{Install.CssVersion}.zip") && Install.MamUrl.EndsWith($"MultiAddonManager-v{Install.MamVersion}-steamrt4.tar.gz"));
        Check("install: a component is downloaded, extracted into game/csgo and marked with its version",
              install.Component("metamod", Install.MetamodVersion, Install.MetamodUrl, "tar")
              && File.ReadAllText(Path.Combine(csgo, "addons", "metamod", "bin", "linuxsteamrt64", "metamod.2.cs2.so")) == "mm"
              && File.Exists(Path.Combine(csgo, "addons", "metamod", ".pre.sh-installed-" + Install.MetamodVersion)));
        downloads.Asked.Clear();
        Check("install: ... and not again while its marker is there (the shell version's marker counts: no needless download)",
              install.Component("metamod", Install.MetamodVersion, Install.MetamodUrl, "tar") && downloads.Asked.Count == 0 && lines[^1].Contains("already installed"));
        downloads.Files["https://example.invalid/mm-new.tar.gz"] = downloads.Files[Install.MetamodUrl];
        Check("install: a new pin extracts again and keeps only its own marker",
              install.Component("metamod", "2.0.0-git9999", "https://example.invalid/mm-new.tar.gz", "tar")
              && Directory.EnumerateFiles(Path.Combine(csgo, "addons", "metamod"), ".pre.sh-installed-*").Select(Path.GetFileName).SequenceEqual([".pre.sh-installed-2.0.0-git9999"]));
        Check("install: a download that fails costs the component, never the start (and says so)",
              !install.Component("nothing", "1", "https://example.invalid/none.tar.gz", "tar") && lines[^1].StartsWith("WARNING: nothing 1 download or extraction failed")
              && !Directory.EnumerateDirectories(game, "pre.sh-download.*").Any());
        Check("install: a zip component the same way", install.Component("counterstrikesharp", Install.CssVersion, Install.CssUrl, "zip")
              && File.Exists(Path.Combine(csgo, "addons", "counterstrikesharp", "bin", "linuxsteamrt64", "counterstrikesharp.so")));

        Check("gameinfo: the Metamod search path right after the Game_LowViolence line",
              install.Gameinfo(true) && File.ReadAllText(Path.Combine(csgo, "gameinfo.gi")).Contains("csgo_lv // Perfect World content override\n\t\t\tGame\tcsgo/addons/metamod\n\t\t\tGame\tcsgo\n"));
        Check("gameinfo: once only, on every start", install.Gameinfo(true) && lines[^1] == "gameinfo.gi already lists csgo/addons/metamod"
              && File.ReadAllText(Path.Combine(csgo, "gameinfo.gi")).Split("csgo/addons/metamod").Length == 2);
        Check("gameinfo: off removes it, and the file is Valve's again", install.Gameinfo(false) && File.ReadAllText(Path.Combine(csgo, "gameinfo.gi")) == Gameinfo
              && install.Gameinfo(false) && lines[^1] == "gameinfo.gi already vanilla");
        File.WriteAllText(Path.Combine(csgo, "gameinfo.gi"), Gameinfo.Replace("Game_LowViolence", "Game_Other"));
        Check("gameinfo: no line to anchor on - it says so, and Metamod does not load", !install.Gameinfo(true) && lines[^1].Contains("no Game_LowViolence line"));
        File.WriteAllText(Path.Combine(csgo, "gameinfo.gi"), Gameinfo);

        var vdf = Path.Combine(csgo, "addons", "metamod", "multiaddonmanager.vdf");
        var cfg = Path.Combine(csgo, "cfg", "multiaddonmanager", "multiaddonmanager.cfg");
        Check("hud: MultiAddonManager not installed: no vdf, and it says so", !install.Hud() && !File.Exists(vdf) && lines[^1].Contains("MultiAddonManager is not installed"));
        install.Component("multiaddonmanager", Install.MamVersion, Install.MamUrl, "tar");
        Check("hud: on - MultiAddonManager loaded, the server mounts the HUD addon, so every map change carries it to every client",
              install.Hud() && File.ReadAllText(vdf).Contains("addons/multiaddonmanager/bin/multiaddonmanager") && File.ReadAllText(cfg).Contains("mm_extra_addons \"3823993566\"")
              && File.ReadAllText(cfg).Contains("mm_client_extra_addons \"\"") && File.ReadAllText(cfg).Contains("mm_addon_mount_download 0")
              && File.ReadAllText(cfg).Contains("mm_cache_clients_with_addons 1") && File.ReadAllText(cfg).Contains("mm_addon_connection_timeout 0")
              && !File.ReadAllText(cfg).Contains('\r'));
        config.HudAddon = "not-an-id";
        Check("hud: a value that is no Workshop id: not delivered, and it says so", !install.Hud() && !File.Exists(vdf));
        config.HudAddon = "";
        Check("hud: off (hud.addon empty) - MultiAddonManager not loaded at all", install.Hud() && !File.Exists(vdf) && lines[^1].StartsWith("HUD off"));
        config.HudAddon = "3823993566";

        var dst = Path.Combine(csgo, "addons", "counterstrikesharp", "plugins");
        Put(Path.Combine(plugins, "OverStrike2", "OverStrike2.dll"), "os2");
        Put(Path.Combine(plugins, "CatchUp", "CatchUp.dll"), "cu");
        Put(Path.Combine(plugins, "CatchUp", "lang", "en.json"), "{}");
        Put(Path.Combine(dst, "ByHand", "ByHand.dll"), "mine");
        Check("plugins: every plugin folder copied in, its subfolders too", install.CopyPlugins() && File.ReadAllText(Path.Combine(dst, "OverStrike2", "OverStrike2.dll")) == "os2"
              && File.Exists(Path.Combine(dst, "CatchUp", "lang", "en.json")) && File.ReadAllText(Path.Combine(dst, ".pre.sh-copied")) == $"src {plugins}\nCatchUp\nOverStrike2\n");
        Directory.Delete(Path.Combine(plugins, "CatchUp"), true);
        Put(Path.Combine(plugins, "OverStrike2", "OverStrike2.dll"), "os2 v2");
        Check($"plugins: one taken off the source goes at the next start; one put there by hand stays ({lines[^1]})",
              install.CopyPlugins() && !Directory.Exists(Path.Combine(dst, "CatchUp")) && Directory.Exists(Path.Combine(dst, "ByHand"))
              && File.ReadAllText(Path.Combine(dst, "OverStrike2", "OverStrike2.dll")) == "os2 v2" && lines[^1].Contains("removed, gone from there: CatchUp"));
        Directory.Delete(Path.Combine(plugins, "OverStrike2"), true);
        Check("plugins: a source with no plugin folder (an empty or missing share) removes nothing",
              install.CopyPlugins() && Directory.Exists(Path.Combine(dst, "OverStrike2")) && lines[^1].Contains("nothing copied, nothing removed"));
        Put(Path.Combine(plugins, "OverStrike2", "OverStrike2.dll"), "os2 v3");

        var con = Path.Combine(csgo, "addons", "metamod", "console.log");
        using (var f = File.Create(con))
            f.SetLength(Install.RotateAt + 1);
        install.RotateConsoleLogs();
        Check("console.log: one over 50 MB is moved aside before the engine starts - also the one the plugin layer's search path puts it in",
              !File.Exists(con) && new FileInfo(con + ".1").Length == Install.RotateAt + 1);

        var settings = Path.Combine(game, "settings.jsonc");
        File.WriteAllText(settings, "{ \"schema\": 1 }");
        var from = Path.Combine(game, "os2-server-shipped");
        File.WriteAllText(from, "binary");
        lines.Clear();
        var rc = install.Run(from, settings);
        var rec = Installed.Read(config.Paths.InstalledFile);
        Check($"install: the whole run - the layer, the HUD, the plugins, the maps folder, and the record of what it put in place ({string.Join(" | ", lines.Where(l => l.StartsWith("SUMMARY") || l.StartsWith("maps folder") || l.StartsWith("installed")))})",
              rc == 0 && rec is not null && rec.ServerFrom == from && rec.Fingerprint == Fingerprint.Compute(config.Paths, from, settings, _ => null).Total
              && lines.Any(l => l.StartsWith("SUMMARY: plugin layer on")) && lines.Any(l => l.StartsWith("maps folder:")));
        config.PluginLayer = false;
        lines.Clear();
        install.Run(from, settings);
        Check("install: plugin.enabled false - the search path goes, vanilla server, the files stay on disk",
              !File.ReadAllText(Path.Combine(csgo, "gameinfo.gi")).Contains("metamod") && lines.Any(l => l.StartsWith("SUMMARY: plugin layer off"))
              && File.Exists(Path.Combine(csgo, "addons", "metamod", ".pre.sh-installed-" + Install.MetamodVersion)));

        var fromEnv = ServerConfig.From(Settings.Parse("{ \"schema\": 1 }", Part.Server, _ => null),
                                        n => n switch { "OS2_HUD_ADDON" => "3823993566", "GUNGAME_HUD_ADDON" => "1111111111", _ => null });
        var oldOnly = ServerConfig.From(Settings.Parse("{ \"schema\": 1 }", Part.Server, _ => null), n => n == "GUNGAME_HUD_ADDON" ? "1111111111" : null);
        var fromFile = ServerConfig.From(Settings.Parse("{ \"schema\": 1, \"hud\": { \"addon\": \"2222222222\" } }", Part.Server, _ => null),
                                         n => n == "OS2_HUD_ADDON" ? "3823993566" : null);
        Check("hud: with no hud.addon in the settings, the image's OS2_HUD_ADDON, then the old GUNGAME_HUD_ADDON; the settings first",
              fromEnv.HudAddon == "3823993566" && oldOnly.HudAddon == "1111111111" && fromFile.HudAddon == "2222222222"
              && fromEnv.BootWarnings.Any(w => w.Contains("OS2_HUD_ADDON")));
        Check("keep: the watcher's keep set follows the bridge's discovery stock (bridge.discoveryStock)",
              ServerConfig.From(Settings.Parse("{ \"schema\": 1, \"bridge\": { \"discoveryStock\": 4 } }", Part.Server, _ => null), _ => null).KeepRules.MinQueue == 4
              && ServerConfig.From(Settings.Parse("{ \"schema\": 1 }", Part.Server, _ => null), _ => null).KeepRules.MinQueue == 10);
        Bridge();
        Launcher(root);
    }

    /// <summary>The bridge over real HTTP: the address it posts to, the body, the answer read as the bridge gives it.</summary>
    static void Bridge()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var seen = new List<string>();
        string Serve(string status, string answer)
        {
            using var client = listener.AcceptTcpClient();
            using var stream = client.GetStream();
            var buf = new byte[65536];
            var got = 0;
            string head;
            while (true)
            {
                got += stream.Read(buf, got, buf.Length - got);
                head = Encoding.UTF8.GetString(buf, 0, got);
                var end = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0)
                    continue;
                var length = head.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))?.Split(':')[1].Trim();
                if (got >= end + 4 + int.Parse(length ?? "0"))
                    break;
            }
            var reply = Encoding.UTF8.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain\r\nContent-Length: {Encoding.UTF8.GetByteCount(answer)}\r\nConnection: close\r\n\r\n{answer}");
            stream.Write(reply);
            return head;
        }
        var bridge = new HttpBridge(($"http://127.0.0.1:{port}", "abc123"));
        var server = Task.Run(() => seen.Add(Serve("200 OK", "ok: going down")));
        var told = bridge.Post("quitting", "gg-quit\t1\tdaily\n", 5);
        server.Wait(5000);
        Check($"bridge: POST /quitting/<secret> with the notice as plain text, and its 'ok' read as taken ({told.Told})",
              told == ("ok", "ok: going down") && seen.Count == 1 && seen[0].StartsWith("POST /quitting/abc123 HTTP/1.1") && seen[0].EndsWith("gg-quit\t1\tdaily\n")
              && seen[0].Contains("text/plain"));
        server = Task.Run(() => seen.Add(Serve("404 Not Found", "Not Found")));
        told = bridge.Post("restart-notice", "gg-restart\t1\tdaily\tnow\t0\n", 5);
        server.Wait(5000);
        Check($"bridge: an older bridge's 404 page is no 'ok': not taken ({told.Reply})", told.Told == "failed" && told.Reply!.Contains("Not Found"));
        listener.Stop();
        told = bridge.Post("quitting", "gg-quit\t1\tdaily\n", 2);
        Check($"bridge: no bridge listening: not taken, and the secret is never in the words ({told.Reply})", told.Told == "failed" && !told.Reply!.Contains("abc123"));
    }

    /// <summary>The launcher: what pre.sh is now - a copy of the program, install, then the watcher in the background.</summary>
    static void Launcher(string root)
    {
        var path = Path.Combine(root, "server", "OverStrike2.Server", "pre.sh");
        if (root.Length == 0 || !File.Exists(path))
        {
            Skips++;
            Console.WriteLine("SKIP pre.sh: the launcher (not run from the repository)");
            return;
        }
        var text = File.ReadAllText(path);
        var code = text.Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')).ToList();
        Check($"pre.sh: a launcher of {code.Count} lines - the copy, install with the shipped file, the watcher in the background, never an exit",
              code.Count <= 12 && text.Contains("install --from \"$os2_from\"") && text.Contains("setsid \"$os2_bin\" watch </dev/null &")
              && !code.Any(l => l.Contains("exit") || l.Contains("set -e") || l.Contains("set -u")) && !text.Contains('\r'));
    }
}
