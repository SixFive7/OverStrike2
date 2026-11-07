using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OverStrike2.Core;
using OverStrike2.TestKit;
using static OverStrike2.TestKit.T;

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
        var config = Scenario.Template with
        {
            Paths = new ServerPaths
            {
                GameDir = game, BridgeData = Dir("install-data"), Handover = Path.Combine(game, "handover"),
                Depotcache = Path.Combine(game, "depotcache"), Plugins = plugins,
            },
            HudAddon = "3823993566",
        };
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
        File.WriteAllText(settings, "{ \"schema\": 1, \"hud\": { \"addon\": \"3823993566\" } }");
        var from = Path.Combine(game, "os2-server-shipped");
        File.WriteAllText(from, "binary");
        lines.Clear();
        var rc = install.Run(from, settings);
        var rec = Installed.Read(config.Paths.InstalledFile);
        Check($"install: the whole run - the layer, the HUD, the plugins, the maps folder, and the record of what it put in place ({string.Join(" | ", lines.Where(l => l.StartsWith("SUMMARY") || l.StartsWith("maps folder") || l.StartsWith("installed")))})",
              rc == 0 && rec is not null && rec.ServerFrom == from && rec.Fingerprint == Fingerprint.Compute(config.Paths, from, settings, _ => null).Total
              && lines.Any(l => l.StartsWith("SUMMARY: plugin layer on") && l.Contains("HUD on (Workshop addon 3823993566)")) && lines.Any(l => l.StartsWith("maps folder:")));
        var mamUrl = downloads.Files[Install.MamUrl];
        downloads.Files.Remove(Install.MamUrl);
        Directory.Delete(Path.Combine(csgo, "addons", "multiaddonmanager"), true);
        lines.Clear();
        rc = install.Run(from, settings);
        Check($"install: no MultiAddonManager after its download failed: the HUD cannot reach anybody - one ALERT, exit {Settings.RefusedExit}, nothing done after it",
              rc == Settings.RefusedExit && !File.Exists(vdf) && lines.Count(l => l.StartsWith("ALERT")) == 1
              && lines.Any(l => l.StartsWith("ALERT: MultiAddonManager is not installed") && l.Contains("the engine does not start")) && !lines.Any(l => l.StartsWith("maps folder:")));
        var vanilla = new Install(config with { PluginLayer = false }, log, downloads, _ => null, SystemClock.Instance, Scenario.Paris);
        rc = vanilla.Run(from, settings);
        Check("install: ... plugin.enabled false, the emergency switch, does not ask for it", rc == 0);
        downloads.Files[Install.MamUrl] = mamUrl;
        lines.Clear();
        rc = install.Run(from, settings);
        Check("install: ... and the next start tries the download again, and goes on", rc == 0 && File.Exists(vdf) && lines.Any(l => l.Contains("HUD on (Workshop addon 3823993566)")));
        lines.Clear();
        vanilla.Run(from, settings);
        Check("install: plugin.enabled false - the search path goes, vanilla server, the files stay on disk",
              !File.ReadAllText(Path.Combine(csgo, "gameinfo.gi")).Contains("metamod") && lines.Any(l => l.StartsWith("SUMMARY: plugin layer off"))
              && File.Exists(Path.Combine(csgo, "addons", "metamod", ".pre.sh-installed-" + Install.MetamodVersion)));

        var fromFile = ServerConfig.From(Settings.Parse("{ \"schema\": 1, \"hud\": { \"addon\": \"2222222222\" } }", Part.Server, _ => null),
                                         n => n is "OS2_HUD_ADDON" or "GUNGAME_HUD_ADDON" ? "3823993566" : null);
        var noneInFile = ServerConfig.From(Settings.Parse("{ \"schema\": 1 }", Part.Server, _ => null), n => n is "OS2_HUD_ADDON" or "GUNGAME_HUD_ADDON" ? "3823993566" : null);
        Check("hud: only the settings file names the item - the image's OS2_HUD_ADDON and GUNGAME_HUD_ADDON count for nothing",
              fromFile.HudAddon == "2222222222" && noneInFile.HudAddon == "" && !noneInFile.BootWarnings.Concat(fromFile.BootWarnings).Any(w => w.Contains("HUD_ADDON")));
        Check("keep: the watcher's keep set follows the bridge's discovery stock (bridge.discoveryStock)",
              ServerConfig.From(Settings.Parse("{ \"schema\": 1, \"bridge\": { \"discoveryStock\": 4 } }", Part.Server, _ => null), _ => null).KeepRules.MinQueue == 4
              && ServerConfig.From(Settings.Parse("{ \"schema\": 1 }", Part.Server, _ => null), _ => null).KeepRules.MinQueue == 10);
        Refusal();
        Downloader(log, lines, config);
        Bridge();
        Launcher(root);
        ModeCfgs(root);
    }

    /// <summary>A server without the HUD cannot exist: install, watch and organize refuse a settings file with no usable hud.addon - or with none
    /// at all to read - with one ALERT line and the exit code pre.sh stops the start on; doctor and version still work and say so.</summary>
    static void Refusal()
    {
        var dir = Dir("refusal");
        var file = Path.Combine(dir, "settings.jsonc");
        (int Code, string Said) Run(params string[] args) => WorkshopTests.Quiet(() => Program.Main(args));
        foreach (var (what, text, why) in new[]
        {
            ("hud.addon missing", "{ \"schema\": 1 }", "hud.addon: missing - required: the Workshop id"),
            ("hud.addon empty", "{ \"schema\": 1, \"hud\": { \"addon\": \"\" } }", "hud.addon: empty - required: the Workshop id"),
            ("hud.addon not digits", "{ \"schema\": 1, \"hud\": { \"addon\": \"abc\" } }", "hud.addon: not digits - required: the Workshop id"),
            ("a file that is no JSON", "{ nope", "not JSON"),
            ("a schema it does not know", "{ \"schema\": 7, \"hud\": { \"addon\": \"3823993566\" } }", "schema 7"),
        })
        {
            File.WriteAllText(file, text);
            foreach (var command in new[] { "install", "watch", "organize" })
            {
                var (code, said) = Run(command, "--settings", file);
                var alerts = said.Split('\n').Where(l => l.Contains("ALERT")).ToList();
                Check($"refusal: {command} with {what} - one ALERT line that names the key and the fix, exit {Settings.RefusedExit}, nothing else ({code})",
                      code == Settings.RefusedExit && alerts.Count == 1 && alerts[0].Contains(why) && alerts[0].Contains("hud.addon") && alerts[0].Contains("fix the file")
                      && (command == "install") == alerts[0].Contains("the engine does not start") && said.Trim().Split('\n').Length == 1);
            }
        }
        var (missing, noFileSaid) = Run("install", "--settings", Path.Combine(dir, "no-such-file.jsonc"));
        Check($"refusal: no settings file at all - the same (the fallback on defaults is gone for this program): {noFileSaid.Trim()}",
              missing == Settings.RefusedExit && noFileSaid.Contains("no settings file at") && noFileSaid.Contains("hud.addon: missing") && noFileSaid.Contains("ALERT"));
        File.WriteAllText(file, "{ \"schema\": 1 }");
        var (saved, savedOld) = (Environment.GetEnvironmentVariable("OS2_HUD_ADDON"), Environment.GetEnvironmentVariable("GUNGAME_HUD_ADDON"));
        Environment.SetEnvironmentVariable("OS2_HUD_ADDON", "3823993566");
        Environment.SetEnvironmentVariable("GUNGAME_HUD_ADDON", "3823993566");
        var (fellBack, _) = Run("install", "--settings", file);
        Environment.SetEnvironmentVariable("OS2_HUD_ADDON", saved);
        Environment.SetEnvironmentVariable("GUNGAME_HUD_ADDON", savedOld);
        Check("refusal: the image's OS2_HUD_ADDON and GUNGAME_HUD_ADDON cannot stand in for hud.addon", fellBack == Settings.RefusedExit);
        var (doctor, doctorSaid) = Run("doctor", "--settings", file);
        Check("refusal: doctor still works and says so: not usable, why, what it costs; its usual lines all the same",
              doctor == 1 && doctorSaid.Contains("NOT USABLE: hud.addon: missing") && doctorSaid.Contains($"install and watch refuse (exit {Settings.RefusedExit})")
              && doctorSaid.Contains("path game:") && doctorSaid.Contains("RCON password:"));
        var (version, versionSaid) = Run("version", "--settings", file);
        Check("refusal: version still works and says so", version == 0 && versionSaid.StartsWith("os2-server ") && versionSaid.Contains("settings: NOT USABLE - hud.addon: missing"));
        File.WriteAllText(file, "{ \"schema\": 1, \"hud\": { \"addon\": \"3823993566\" }, \"restarts\": { \"updates\": false, \"newVersions\": false, \"freezeRecovery\": false, \"downloadRestart\": false, \"daily\": \"off\" }, \"maps\": { \"backgroundDownloads\": false, \"downloadLine\": false } }");
        var (fine, fineSaid) = Run("version", "--settings", file);
        Check("refusal: with hud.addon in the file version says its one line, doctor shows the item and exits 0, and watch gets past the refusal",
              fine == 0 && !fineSaid.Contains("NOT USABLE") && fineSaid.Trim().Split('\n').Length == 1
              && Run("doctor", "--settings", file) is { Code: 0 } good && good.Said.Contains("hud.addon = 3823993566") && !good.Said.Contains("NOT USABLE")
              && Run("watch", "--settings", file) is { Code: 0 } watch && watch.Said.Contains("nothing to do"));
        var (help, helpSaid) = Run("help");
        Check("refusal: the usage names the exit code", help == 0 && helpSaid.Contains($"exit code {Settings.RefusedExit}"));

        // the settings inline in an environment variable: the programs read it like the file, and a file named for a test wins
        const string inline = "{ \"schema\": 1, \"hud\": { \"addon\": \"3823993566\" }, \"plugin\": { \"enabled\": false } }";
        var savedContent = Environment.GetEnvironmentVariable(Settings.ContentVariable);
        var savedPath = Environment.GetEnvironmentVariable(Settings.PathVariable);
        Environment.SetEnvironmentVariable(Settings.PathVariable, null);
        Environment.SetEnvironmentVariable(Settings.ContentVariable, inline);
        var (fromVar, fromVarSaid) = Run("doctor");
        Check($"settings inline: doctor reads {Settings.ContentVariable} like a file, names the variable, shows no text of it",
              fromVar == 0 && fromVarSaid.Contains($"settings: {Settings.ContentSource}") && fromVarSaid.Contains("hud.addon = 3823993566") && !fromVarSaid.Contains("\"schema\""));
        var (named, namedSaid) = Run("doctor", "--settings", file);
        Check("settings inline: --settings wins, for a test", named == 0 && namedSaid.Contains($"settings: {file}") && !namedSaid.Contains(Settings.ContentSource));
        Environment.SetEnvironmentVariable(Settings.ContentVariable, "{ \"schema\": 1 }");
        var (badVar, badVarSaid) = Run("install");
        Check("settings inline: a value with no hud.addon refuses the same way, and the ALERT names the variable",
              badVar == Settings.RefusedExit && badVarSaid.Contains($"{Settings.ContentSource}: hud.addon: missing") && badVarSaid.Trim().Split('\n').Length == 1);
        Environment.SetEnvironmentVariable(Settings.ContentVariable, null);
        var hashFile = Path.Combine(dir, "same.jsonc");
        File.WriteAllText(hashFile, inline);
        string? ContentOnly(string name) => name == Settings.ContentVariable ? inline : null;
        Check("settings inline: the version's settings part is the same from the variable as from a file with that text, and none with neither",
              Fingerprint.HashSettings(Settings.DefaultPath, ContentOnly) == Fingerprint.HashSettings(hashFile, _ => null) && Fingerprint.HashSettings(hashFile, _ => null) != Fingerprint.None
              && Fingerprint.HashSettings(Path.Combine(dir, "not-there.jsonc"), _ => null) == Fingerprint.None);
        Check("settings inline: a value that cannot be used is no version: nothing is decided on it", RestartTests.Throws<InvalidDataException>(() => Fingerprint.HashSettings(Settings.DefaultPath, n => n == Settings.ContentVariable ? "{ nope" : null)));
        Environment.SetEnvironmentVariable(Settings.ContentVariable, savedContent);
        Environment.SetEnvironmentVariable(Settings.PathVariable, savedPath);
        var game = Path.Combine(dir, "game");
        Linux("refusal: install leaves the game's folder alone", () =>
        {
            File.WriteAllText(file, $"{{ \"schema\": 1, \"paths\": {{ \"gameDir\": \"{game}\" }} }}");
            return Run("install", "--settings", file).Code == Settings.RefusedExit && !Directory.Exists(game);
        });
    }

    /// <summary>The real downloader against a stand-in server over real HTTP: a stall after the headers or before them ends, and a slow download
    /// that keeps moving does not.</summary>
    static void Downloader(Log log, List<string> lines, ServerConfig config)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/archive";
        var connections = 0;
        Action<NetworkStream> behave = _ => { };
        var open = new List<TcpClient>();                    // never closed before the end: a stalled server says nothing, and hangs up on nobody
        _ = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    var client = listener.AcceptTcpClient();
                    lock (open)
                        open.Add(client);
                    Interlocked.Increment(ref connections);
                    var answer = behave;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var (head, buffer) = ("", new byte[4096]);
                            while (!head.Contains("\r\n\r\n"))
                            {
                                var n = stream.Read(buffer);
                                if (n == 0)
                                    return;
                                head += Encoding.ASCII.GetString(buffer, 0, n);
                            }
                            answer(stream);
                        }
                        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
                        {
                        }
                    });
                }
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
            {
            }
        });
        static void Say(NetworkStream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));
        var clock = new FakeClock(Scenario.At(2026, 10, 4, 15, 0, 0));
        var file = Path.Combine(Dir("downloader"), "archive");
        (string? Failed, int Tries, TimeSpan Took) Fetch(TimeSpan noData, TimeSpan? atMost = null)
        {
            connections = 0;
            var took = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                new HttpDownloader(clock, noData, atMost).Download(url, file);
                return (null, connections, took.Elapsed);
            }
            catch (IOException e)
            {
                return (e.Message, connections, took.Elapsed);
            }
        }
        var longEnough = TimeSpan.FromSeconds(30);           // far under the default deadlines, far over what the short ones here should take

        behave = s => { Say(s, "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello"); s.Close(); };
        var whole = Fetch(TimeSpan.FromSeconds(5));
        Check("download: a whole answer lands in the file, in one try", whole.Failed is null && whole.Tries == 1 && File.ReadAllText(file) == "hello");

        behave = s => Say(s, "HTTP/1.1 200 OK\r\nContent-Length: 1000\r\n\r\npart of it");
        var stalled = Fetch(TimeSpan.FromMilliseconds(300));
        Check($"download: a body that stalls after the headers ends - four tries, then the failure, never a wait for ever ({stalled.Failed}, {stalled.Tries} tries, {stalled.Took.TotalSeconds:0.0} s)",
              stalled.Failed == "no data for 0.3 s" && stalled.Tries == 4 && stalled.Took < longEnough);

        behave = _ => { };
        var silent = Fetch(TimeSpan.FromMilliseconds(300));
        Check($"download: a server that takes the request and never answers ends the same way ({silent.Failed}, {silent.Tries} tries)",
              silent.Failed == "no data for 0.3 s" && silent.Tries == 4 && silent.Took < longEnough);

        behave = s =>
        {
            Say(s, "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\n");
            foreach (var c in "slowy")
            {
                Thread.Sleep(200);
                Say(s, c.ToString());
            }
            s.Close();
        };
        var slow = Fetch(TimeSpan.FromMilliseconds(800));
        Check($"download: a slow one that keeps moving is no stall ({slow.Took.TotalSeconds:0.0} s, longer than the 0.8 s it may be quiet)",
              slow.Failed is null && slow.Tries == 1 && File.ReadAllText(file) == "slowy" && slow.Took > TimeSpan.FromMilliseconds(800));

        behave = s =>
        {
            Say(s, "HTTP/1.1 200 OK\r\nContent-Length: 1000\r\n\r\n");
            for (var i = 0; i < 100; i++)
            {
                Thread.Sleep(50);
                Say(s, "x");
            }
        };
        var trickle = Fetch(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(400));
        Check($"download: one that trickles on ends at its deadline ({trickle.Failed}, {trickle.Tries} tries)", trickle.Failed == "not done after 0.4 s" && trickle.Tries == 4 && trickle.Took < longEnough);

        behave = s => Say(s, "HTTP/1.1 200 OK\r\nContent-Length: 1000\r\n\r\npart of it");
        lines.Clear();
        var fetching = new Install(config, log, new HttpDownloader(clock, TimeSpan.FromMilliseconds(200)), _ => null, clock, Scenario.Paris);
        var gotPast = !fetching.Component("stalled", "1", url, "tar");
        Check($"download: install gets past a stalled component, and says so ({lines[^1]})", gotPast && lines[^1].StartsWith("WARNING: stalled 1 download or extraction failed (no data for 0.2 s)"));

        listener.Stop();
        lock (open)
            foreach (var client in open)
                client.Dispose();
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

    /// <summary>The three mode cfgs (server/gamemode_*_server.cfg: Arms Race, Deathmatch, Wingman) set 23 of their settings alike, each in its
    /// own file - one cfg that the three exec is untested, and the server mounts each file on its own. So a setting all three set holds one
    /// value in all three, but for those named here, which differ on purpose: a change to one of them is made in all three.</summary>
    static void ModeCfgs(string root)
    {
        var folder = Path.Combine(root, "server");
        string[] files = root.Length == 0 || !Directory.Exists(folder) ? [] : Directory.GetFiles(folder, "gamemode_*_server.cfg").Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
        {
            Skips++;
            Console.WriteLine("SKIP mode cfgs: the settings the three share (not run from the repository)");
            return;
        }
        string[] onPurpose = ["mp_warmuptime"]; // Wingman warms up for 15 s, the other two for 1 s (which the engine makes 5)
        var cfgs = files.Select(f => File.ReadAllLines(f).Select(l => l.Split("//")[0].Trim()).Where(l => l.Length > 0)
                                     .Select(l => l.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                     .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.Select(p => p.Length > 1 ? p[1] : "").ToList())).ToList();
        var shared = cfgs.Skip(1).Aggregate(cfgs[0].Keys.ToHashSet(), (s, c) => { s.IntersectWith(c.Keys); return s; });
        var twice = cfgs.SelectMany(c => c.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key)).ToList();
        var apart = shared.Where(k => !onPurpose.Contains(k) && cfgs.Select(c => c[k][0]).Distinct().Count() > 1).Order(StringComparer.Ordinal).ToList();
        Check($"mode cfgs: the {shared.Count(k => !onPurpose.Contains(k))} settings all three set hold one value in all three, and no file sets one twice ({Show(apart.Concat(twice))})",
              files.Length == 3 && shared.Count > 0 && apart.Count == 0 && twice.Count == 0);
    }

    /// <summary>The launcher: what pre.sh is now - a copy of the program, install, then the watcher in the background; and, on Linux, what it does
    /// sourced by an entry script: a stand-in for the image's, and in the game image the image's own, with a stand-in for SteamCMD, the engine
    /// and the program.</summary>
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
        Check($"pre.sh: a launcher of {code.Count} lines - the copy, install with the shipped file, the watcher in the background; its one exit is the refusal's code, {Settings.RefusedExit}",
              code.Count <= 16 && text.Contains("install --from \"$os2_from\"") && text.Contains("setsid \"$os2_bin\" watch </dev/null &")
              && code.Count(l => l.Contains("exit")) == 1 && code.Any(l => l.Trim() == $"exit {Settings.RefusedExit}") && code.Any(l => l.Contains($"\"$os2_rc\" = {Settings.RefusedExit} ]"))
              && code.Any(l => l.Trim() == "sleep \"${OS2_REFUSED_PAUSE:-300}\"") && !code.Any(l => l.Contains("set -e") || l.Contains("set -u")) && !text.Contains('\r'));
        if (!OperatingSystem.IsLinux())
        {
            Skips++;
            Console.WriteLine("SKIP pre.sh: what it does when an entry script sources it (Linux only)");
            return;
        }
        const UnixFileMode Runnable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const string ImageEntry = "/home/steam/entry.sh";
        var dir = Dir("launcher");
        var game = Path.Combine(dir, "cs2-dedicated");
        var calls = Path.Combine(dir, "calls");
        var program = Path.Combine(dir, "os2-server-shipped");
        var standIn = Path.Combine(dir, "entry-stand-in.sh");
        var home = Path.Combine(dir, "home");
        Directory.CreateDirectory(home);
        Put(Path.Combine(game, "pre.sh"), text);
        Put(Path.Combine(game, "game", "cs2.sh"), "#!/bin/bash\necho ENGINE-STARTS\n");
        File.SetUnixFileMode(Path.Combine(game, "game", "cs2.sh"), Runnable);
        Put(Path.Combine(dir, "steamcmd", "steamcmd.sh"), "#!/bin/bash\nexit 0\n");
        Put(program, "#!/bin/bash\necho \"$*\" >> \"$OS2_TEST_CALLS\"\nif [ \"$1\" = install ]; then exit \"$OS2_TEST_INSTALL\"; fi\n");
        File.SetUnixFileMode(program, Runnable);
        Put(standIn, "#!/bin/bash\ncd \"$STEAMAPPDIR/game/\"\nsource \"$STEAMAPPDIR/pre.sh\"\n./cs2.sh\n");

        // The entry script's exit code and what it said. Reading to the end also waits for the watcher: it holds the same stdout.
        (int Rc, string Said, string Calls) Start(string entry, int install, string? shipped = null, int pause = 0)
        {
            File.Delete(calls);
            var psi = new System.Diagnostics.ProcessStartInfo("bash", [entry]) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
            foreach (var (name, value) in new Dictionary<string, string>
            {
                ["STEAMAPPDIR"] = game, ["STEAMCMDDIR"] = Path.Combine(dir, "steamcmd"), ["HOME"] = home, ["OS2_SERVER"] = shipped ?? program, ["OS2_REFUSED_PAUSE"] = pause.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["OS2_TEST_CALLS"] = calls, ["OS2_TEST_INSTALL"] = install.ToString(System.Globalization.CultureInfo.InvariantCulture), ["CS2_RCON_PORT"] = "",
            })
                psi.Environment[name] = value;
            using var p = System.Diagnostics.Process.Start(psi)!;
            var errors = p.StandardError.ReadToEndAsync();
            var said = p.StandardOutput.ReadToEnd() + errors.GetAwaiter().GetResult();
            p.WaitForExit();
            return (p.ExitCode, said, File.Exists(calls) ? File.ReadAllText(calls) : "");
        }

        foreach (var (what, entry) in File.Exists(ImageEntry) ? new[] { ("a stand-in entry script", standIn), ("the image's own entry.sh", ImageEntry) } : [("a stand-in entry script", standIn)])
        {
            var (rc, said, ran) = Start(entry, Settings.RefusedExit);
            Check($"pre.sh, sourced by {what}: install's refusal ({Settings.RefusedExit}) stops the start - the entry script ends with that code, no engine, no watcher, and the log says why (rc {rc})",
                  rc == Settings.RefusedExit && !said.Contains("ENGINE-STARTS") && said.Contains("pre.sh: ALERT") && said.Contains("the engine does not start") && !said.Contains("restart watcher started")
                  && ran.StartsWith($"install --from {program}") && !ran.Contains("watch"));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            (rc, said, _) = Start(entry, Settings.RefusedExit, pause: 2);
            Check($"pre.sh, sourced by {what}: ...it waits before it exits, so Docker's next try is no new Steam login at once, and says when ({watch.Elapsed.TotalSeconds:0.0} s)",
                  rc == Settings.RefusedExit && watch.Elapsed >= TimeSpan.FromSeconds(2) && said.Contains("the next try in 2 s"));
            foreach (var other in new[] { 1, 2, 77, 79, 134 })
            {
                (rc, said, ran) = Start(entry, other);
                Check($"pre.sh, sourced by {what}: any other failure of install ({other}) costs the plugin layer, never the server - a warning, the engine, the watcher (rc {rc})",
                      rc == 0 && said.Contains("ENGINE-STARTS") && said.Contains("WARNING: os2-server install failed - the engine starts anyway") && ran.Contains("watch"));
            }
            (rc, said, ran) = Start(entry, 0);
            Check($"pre.sh, sourced by {what}: install fine - no warning, the engine, the watcher (rc {rc})",
                  rc == 0 && said.Contains("ENGINE-STARTS") && !said.Contains("WARNING") && !said.Contains("ALERT") && said.Contains("restart watcher started") && ran.Contains("watch"));
            (rc, said, ran) = Start(entry, 0, Path.Combine(dir, "no-such-program"));
            Check($"pre.sh, sourced by {what}: a program that cannot be copied costs the plugin layer and the watcher, never the server (rc {rc})",
                  rc == 0 && said.Contains("ENGINE-STARTS") && said.Contains("could not be copied") && ran.Length == 0);
        }
    }
}
