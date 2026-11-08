using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Deploy;

/// <summary>The bridge's hand-made data files: copy the committed ones to the server, verified (ported from deploy/data.py, gone since release 193).
///
///     data [--apply] [--force]    ours.json, sizes.json, blocked.json -> &lt;bridge.data_dir&gt; (and the first lists, once)
///
/// Without --apply it only shows what would change. The bridge reads its data files again by itself within about 10 seconds (nothing
/// restarts). Only committed bytes ship. One writer per file: the Workshop sync writes what it generates on the server itself - maps.json,
/// the mode lists, broken.json, search.json, ratings.json, sync-status.json - so this never ships over them: the repository's maps.json,
/// broken.json and search.json go only to a server that has none yet (a first install). What ships is what people make by hand: our maps
/// (ours.json), the measured sizes (sizes.json) and the maps blocked for good (blocked.json). A server copy of one of those that no commit
/// of this repository ever held was changed there: that is DRIFT, and nothing ships until the change is in the repository (or --force).</summary>
static class Data
{
    public static readonly Dictionary<string, (string Key, string Folder, string[] Files)> Sets = new()
    {
        ["bridge"] = ("bridge.data_dir", "bridge", ["ours.json", "sizes.json", "blocked.json", "maps.json", "broken.json", "search.json"]),
    };
    public static readonly HashSet<string> FirstOnly = ["maps.json", "broken.json", "search.json"];   // the sync's on the server: shipped only where there is none yet

    /// <summary>The files of a set that ship: the checkout must have them (else said and skipped), and a file the sync writes ships only to a
    /// server without one.</summary>
    public static List<string> Plan(string folder, IEnumerable<string> files, string dest)
    {
        var output = new List<string>();
        foreach (var f in files)
        {
            if (!File.Exists(Repo.P(folder, f)))
                Out.Log($"{folder}/{f}: not in this checkout - skipped");
            else if (FirstOnly.Contains(f) && Path.Exists(Path.Combine(dest, f)))
                Out.Log($"{f}: the server has its own - the Workshop sync writes it there, left alone");
            else
                output.Add(f);
        }
        return output;
    }

    public static int Command(string[] args)
    {
        var a = new Args(args, "data [--apply] [--force] [--config FILE]", flags: ["--apply", "--force"], options: ["--config"]);
        a.Done();
        var apply = a.Flag("--apply");
        var cfg = new Config(a.Opt("--config"));
        var (key, folder, all) = Sets["bridge"];
        var dest = cfg.Str(key);
        if (dest.Length == 0)
        {
            Out.Log($"{key} is empty in the private config: these files are not deployed from this repository");
            return 0;
        }
        var files = Plan(folder, all, dest);
        var commit = files.Count > 0 ? Repo.CleanTree(files.Select(f => $"{folder}/{f}").ToList()) : Repo.Git(["rev-parse", "--short=12", "HEAD"]);
        Preflight.Show(cfg, null);
        var drift = Preflight.Drifted(files.Select(f => (Repo.P(folder, f), $"{folder}/{f}", Path.Combine(dest, f)))).Select(Path.GetFileName).OfType<string>().ToList();
        Preflight.RefuseDrift(drift, a.Flag("--force"));
        foreach (var f in files)
            Out.Log($"{f}: {Repo.CopyVerified(Repo.P(folder, f), Path.Combine(dest, f), dry: !apply)}" + (drift.Contains(f) ? " (DRIFT overridden)" : ""));
        Out.Log($"{(apply ? "shipped" : "dry run - add --apply to copy")} ({folder}/ at {commit})");
        return 0;
    }
}

/// <summary>The game server's plugin (plugin/OverStrike2): build it, test it, ship the DLL to the server's plugin folder (deploy/plugin.py).
///
///     plugin build             dotnet build -c Release, then the plugin's own self-test and catch-up's (below normal priority)
///     plugin ship              show what would change in &lt;server.plugins_dir&gt;/OverStrike2/
///     plugin ship --apply      copy it there, verified byte for byte
///
/// The folder name must equal the DLL name. What ships: OverStrike2.dll, its .pdb and the config files the build copies beside it
/// (blocked.txt; a module's .json). The game server copies the plugin folder in at its next start (pre.sh): a restart of the game
/// container, which disconnects everybody - do it at 0 humans. Only committed sources ship (plugin/, and the bridge sources and core/ the
/// plugin compiles in).</summary>
static class Plugin
{
    public const string Name = "OverStrike2";
    public static string Project => Repo.P("plugin", Name);
    public static string SelfTest => Repo.P("plugin", Name + ".SelfTest");
    public static string CatchUpSelfTest => Repo.P("plugin", "CatchUp", "SelfTest");   // the CatchUp module the plugin compiles in: its decisions and real matches
    public static string OutDir => Path.Combine(Project, "bin", "Release", "net10.0");

    /// <summary>The config files the project copies beside the DLL - its None items with CopyToOutputDirectory, by the name they get there
    /// (blocked.txt; catchup.json, linked from the CatchUp module). Only these ship: a file an older build left in bin/Release is no part of
    /// the plugin any more.</summary>
    public static List<string> DeclaredContent(string csproj)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(csproj, @"<None\s+([^>]*?CopyToOutputDirectory=""(?:PreserveNewest|Always)""[^>]*?)/?>"))
        {
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match at in Regex.Matches(m.Groups[1].Value, @"(\w+)=""([^""]*)"""))
                attrs[at.Groups[1].Value] = at.Groups[2].Value;
            if (attrs.TryGetValue("Include", out var include) && include.Length > 0)
                names.Add(attrs.TryGetValue("Link", out var link) && link.Length > 0 ? link : Path.GetFileName(include.Replace('\\', '/')));
        }
        return [.. names];
    }

    /// <summary>The DLL and its .pdb, and the config files the project declares as the build put them beside it. Not the .deps.json or the
    /// runtimeconfig - CounterStrikeSharp needs neither.</summary>
    public static List<string> ShipList()
    {
        var declared = DeclaredContent(File.ReadAllText(Path.Combine(Project, Name + ".csproj")));
        return [Name + ".dll", Name + ".pdb", .. declared.Where(n => File.Exists(Path.Combine(OutDir, n)))];
    }

    static string LastLine(string text) => Py.SplitLines(Py.Strip(text)) is { Count: > 0 } l ? l[^1] : "(no output)";

    public static string Build()
    {
        var commit = Repo.CleanTree(["plugin", "bridge", "core"]);
        var (code, output) = Proc.Dotnet(["build", "-c", "Release"], Project);
        var warnings = Py.SplitLines(output).Where(l => l.Contains(": warning ", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (code != 0)
            throw Out.Fail("build:\n" + Py.Slice(output, -3000));
        Out.Log($"built {Name}.dll at {commit} ({warnings.Count} warnings)");
        (code, output) = Proc.Dotnet(["run", "-c", "Release"], SelfTest);
        Out.Log("self-test: " + LastLine(output));
        if (code != 0)
            throw Out.Fail("the plugin's self-test failed:\n" + Py.Slice(output, -2000));
        (code, output) = Proc.Dotnet(["run", "-c", "Release"], CatchUpSelfTest);
        Out.Log("catch-up's self-test: " + LastLine(output));
        if (code != 0)
            throw Out.Fail("catch-up's self-test failed:\n" + Py.Slice(output, -2000));
        return commit;
    }

    public static void Ship(IConfig cfg, bool apply)
    {
        var dest = Path.Combine(cfg.Str("server.plugins_dir", required: true), Name);
        if (!Directory.Exists(OutDir))
            throw Out.Fail("not built - run: os2-deploy plugin build");
        var ship = ShipList();
        var missing = ship.Where(f => !File.Exists(Path.Combine(OutDir, f))).ToList();
        if (missing.Count > 0)
            throw Out.Fail($"not built: {string.Join(", ", missing)} - run: os2-deploy plugin build");
        Preflight.Show(cfg, " (copying changes nothing until the game container restarts)");
        foreach (var f in ship)
            Out.Log($"{f}: {Repo.CopyVerified(Path.Combine(OutDir, f), Path.Combine(dest, f), dry: !apply)}");
        if (!apply)
            Out.Log("dry run - add --apply to copy");
        else
            Out.Log($"shipped to {Name}/ in the plugin folder; it loads at the game server's next start (restart it at 0 humans)");
    }

    public static int Command(string[] args)
    {
        var a = new Args(args, "plugin build|ship [--apply] [--config FILE]", flags: ["--apply"], options: ["--config"]);
        var command = a.Choice(["build", "ship"]);
        a.Done();
        if (command == "build")
            Build();
        else
            Ship(new Config(a.Opt("--config")), a.Flag("--apply"));
        return 0;
    }
}

/// <summary>The server side: the server program os2-server, the sync program os2-sync, the launcher pre.sh and the mode cfgs - build and
/// test them here, ship them to the server's folders (ported from deploy/server.py, gone since release 193).
///
///     server build [--image]   both programs, one self-contained linux-x64 file each (dotnet publish), then both self-tests, at below normal
///                              priority, from committed sources only; --image runs both self-tests in the game server's image as well
///     server ship              show what would change: os2-server, os2-sync and pre.sh in &lt;server.program_dir&gt;, the mode cfgs in &lt;server.config_dir&gt;
///     server ship --apply      copy them there, verified byte for byte: the programs written beside their targets and renamed over them,
///                              pre.sh and the cfgs written in place (a container mounts each as a single file, which stays on the old file
///                              when another is renamed over it). --apply with no server.program_dir in the config fails: nothing would ship.
///     server restart-sync      the Workshop sync's container restarted, so it starts the os2-sync on disk (its entrypoint copies the program
///                              folder's into /tmp): only production's and only while it runs no run (its sync-status.json says idle or
///                              stopped, with no run), then seen through - a new start, /tmp/os2-sync the program folder's byte for byte
///
/// What ships is what `build` built - refused once the programs' sources changed since - and the committed pre.sh and cfgs. A pre.sh or
/// cfg on the server that no commit of this repository ever held was changed there: that is DRIFT, and nothing ships until the change is
/// in the repository (or --force).</summary>
static class ServerShip
{
    public static readonly string[] Sources = ["core", "server/OverStrike2.Server", "server/OverStrike2.Sync"];
    public static readonly Dictionary<string, string> Programs = new() { ["os2-server"] = "server/OverStrike2.Server", ["os2-sync"] = "server/OverStrike2.Sync" };
    public static readonly Dictionary<string, string> SelfTests = new()
    {
        ["server/OverStrike2.Server/SelfTest"] = "OverStrike2.Server.SelfTest",
        ["server/OverStrike2.Sync/SelfTest"] = "OverStrike2.Sync.SelfTest",
    };
    public const string Launcher = "server/OverStrike2.Server/pre.sh";
    public static readonly string[] Cfgs = ["server/gamemode_armsrace_server.cfg", "server/gamemode_deathmatch_server.cfg", "server/gamemode_competitive2v2_server.cfg"];
    public const string GameImage = "docker.io/joedwards32/cs2";

    static string? outDir, record;
    /// <summary>One folder per program, and build.json (bin/ is ignored); the self-tests move it.</summary>
    public static string OutDir { get => outDir ?? Repo.P("server", "bin", "ship"); set => outDir = value; }
    public static string Record { get => record ?? Path.Combine(OutDir, "build.json"); set => record = value; }
    public static string TestOut => Repo.P("server", "bin", "ship-selftest");

    static (int Code, string Output) Dotnet(string[] args) => Proc.Dotnet(args, Repo.Root);

    static string LastLine(string text) => Py.Strip(Py.SplitLines(Py.Strip(text)) is { Count: > 0 } l ? l[^1] : "(no output)");

    /// <summary>A published program must be one file: anything beside it but debug symbols would have to ship too.</summary>
    public static string SingleFile(string folder, string name)
    {
        var extra = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>()
                             .Where(f => f != name && !f.EndsWith(".pdb", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        if (extra.Count > 0)
            throw Out.Fail($"{name} is not a single file - its publish folder also holds {string.Join(", ", extra)}");
        return Path.Combine(folder, name);
    }

    static void Publish(string project, string folder)
    {
        var (code, output) = Dotnet(["publish", project, "-c", "Release", "-r", "linux-x64", "-o", folder]);
        if (code != 0)
            throw Out.Fail($"publish {project}:\n" + Py.Slice(output, -3000));
    }

    /// <summary>One published self-test in the game image, as the image's own user; the repository read-only at /repo. The test is not the
    /// script's last command, so bash stays the container's first process and reaps the orphans of the checks that kill process groups.</summary>
    static (int Code, string Output) InImage(IConfig? cfg, string name)
    {
        var engine = cfg?.Str("build.engine", "wslc") ?? "wslc";
        var image = cfg?.Str("build.game_image", GameImage) ?? GameImage;
        var script = $"cp /t/{name} /tmp/{name} && chmod 755 /tmp/{name} && cd /repo && /tmp/{name}; rc=$?; echo \"exit $rc\"; exit $rc";
        var r = Proc.Capture([engine, "run", "--rm", "--entrypoint", "/bin/bash", "-v", $"{TestOut}:/t:ro", "-v", $"{Repo.Root}:/repo:ro", image, "-c", script], below: true);
        return (r.Code, string.Join("\n", Py.SplitLines(r.All).Where(l => !Regex.IsMatch(Py.Strip(l), @"^exit \d+$"))));
    }

    public static JsonObject Build(bool image, IConfig? cfg)
    {
        var commit = Repo.CleanTree(Sources);
        var now = DateTimeOffset.Now;
        var offset = now.Offset;
        var record = new JsonObject
        {
            ["commit"] = commit,
            ["built"] = now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString("hhmm", CultureInfo.InvariantCulture),
            ["programs"] = new JsonObject(),
            ["selftests"] = new JsonObject(),
        };
        foreach (var (name, project) in Programs)
        {
            var folder = Path.Combine(OutDir, name);
            Publish(project, folder);
            var path = SingleFile(folder, name);
            var sha = Repo.Sha256(path);
            var bytes = new FileInfo(path).Length;
            record["programs"]![name] = new JsonObject { ["sha256"] = sha, ["bytes"] = bytes };
            Out.Log($"published {name} at {commit}: {Fmt.Fixed(bytes / 1e6, 1)} MB, sha256 {sha[..16]}");
        }
        foreach (var (project, name) in SelfTests)
        {
            var (code, output) = Dotnet(["run", "-c", "Release", "--project", project]);
            record["selftests"]![name + " here"] = LastLine(output);
            Out.Log($"{name} here: {LastLine(output)}");
            if (code != 0)
                throw Out.Fail($"{name} failed here:\n" + Py.Slice(output, -2000));
        }
        if (image)
        {
            foreach (var (project, name) in SelfTests)
            {
                Publish(project, TestOut);
                var (code, output) = InImage(cfg, name);
                record["selftests"]![name + " in the game image"] = LastLine(output);
                Out.Log($"{name} in the game image: {LastLine(output)}");
                if (code != 0)
                    throw Out.Fail($"{name} failed in the game image:\n" + Py.Slice(output, -2000));
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Record)!);
        File.WriteAllText(Record, Json.Dumps(record, indent: 1));
        Out.Log($"built at {commit}: ship with  os2-deploy server ship");
        return record;
    }

    /// <summary>The last build's record, checked: its files are the ones it recorded, and the programs' sources are unchanged since.</summary>
    public static JsonObject Built()
    {
        if (!File.Exists(Record))
            throw Out.Fail("not built - run: os2-deploy server build");
        var record = Json.Parse(File.ReadAllText(Record)) as JsonObject ?? throw Out.Fail($"{Record} is not a build record - build again");
        var commit = Json.Str(record, "commit") ?? "";
        var changed = Repo.Git(["diff", "--name-only", commit, "--", .. Sources]);
        if (changed.Length > 0)
            throw Out.Fail($"the programs' sources changed since the build at {commit} ({string.Join(", ", changed.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Take(5))}) - build again");
        foreach (var (name, info) in Json.Obj(record, "programs"))
        {
            var path = Path.Combine(OutDir, name, name);
            if (!File.Exists(path) || Repo.Sha256(path) != Json.Str(info, "sha256"))
                throw Out.Fail($"{name} is not the file the build at {commit} made - build again");
        }
        return record;
    }

    /// <summary>(source, target) for every file that ships; a folder the private config leaves empty is not deployed from here.</summary>
    public static List<(string Src, string Dst)> Plan(IConfig cfg)
    {
        var programDir = cfg.Str("server.program_dir");
        var configDir = cfg.Str("server.config_dir");
        var output = new List<(string, string)>();
        if (programDir.Length > 0)
        {
            output.AddRange(Programs.Keys.Select(n => (Path.Combine(OutDir, n, n), Path.Combine(programDir, n))));
            output.Add((Repo.P(Launcher.Split('/')), Path.Combine(programDir, "pre.sh")));
        }
        else
            Out.Log("server.program_dir is empty in the private config: the programs and pre.sh are not deployed from here");
        if (configDir.Length > 0)
            output.AddRange(Cfgs.Select(c => (Repo.P(c.Split('/')), Path.Combine(configDir, Path.GetFileName(c)))));
        else
            Out.Log("server.config_dir is empty in the private config: the mode cfgs are not deployed from here");
        return output;
    }

    /// <summary>The targets of the committed files (pre.sh, the cfgs) whose server copy is neither this checkout's nor any earlier commit's.
    /// Never the programs: a server copy that differs from a new build is the version before it.</summary>
    static List<string> Drift(List<(string Src, string Dst)> files)
    {
        var committed = new[] { Launcher }.Concat(Cfgs).ToDictionary(rel => Repo.P(rel.Split('/')), rel => rel);
        return Preflight.Drifted(files.Where(f => committed.ContainsKey(f.Src)).Select(f => (f.Src, committed[f.Src], f.Dst)));
    }

    public static int Ship(IConfig cfg, bool apply, bool force = false)
    {
        if (apply && cfg.Str("server.program_dir").Length == 0)
            throw Out.Fail($"server.program_dir is empty in {cfg.Path ?? "the private config"}: --apply has nowhere to ship the programs, so nothing is "
                           + "shipped. Is this the deployment's config? OVERSTRIKE2_CONFIG or --config names it", 2);
        var record = Built();
        var head = Repo.CleanTree([.. Sources, Launcher, .. Cfgs]);
        var files = Plan(cfg);
        if (files.Count == 0)
            return 0;
        Preflight.Show(cfg, " (a new os2-server, pre.sh or cfg restarts the engine at the end of the match, or at once with nobody on)");
        var changed = Drift(files);
        Preflight.RefuseDrift(changed, force);
        var copied = 0;
        var programs = Programs.Keys.Select(n => Path.Combine(OutDir, n, n)).ToHashSet(StringComparer.Ordinal);
        foreach (var (src, dst) in files)
        {
            string result;
            try
            {
                result = Repo.CopyVerified(src, dst, dry: !apply, atomic: programs.Contains(src));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw Out.Fail($"{dst} could not be written ({error.GetType().Name}): "
                               + (programs.Contains(src) ? "" : "it goes in place, so the file itself must be writable, not only its folder; ")
                               + "look at its owner and mode. What was copied before it stays; ship again when it is fixed");
            }
            if (result == "copied")
                copied++;
            Out.Log($"{dst}: {result}" + (changed.Contains(dst) ? " (DRIFT overridden)" : ""));
        }
        if (!apply)
            Out.Log($"dry run (programs built at {Json.Str(record, "commit")}, pre.sh and cfgs at {head}) - add --apply to copy");
        else if (copied > 0)
            Out.Log($"shipped {copied} file(s): the restart watcher takes a new os2-server, pre.sh or cfg in by itself; os2-sync at the "
                    + "sync container's next start");
        else
            Out.Log("nothing to copy: the server has these files already");
        return 0;
    }

    /// <summary>server restart-sync: see the class's summary. The program folder as the sync sees it, and where its entrypoint puts the copy it runs.</summary>
    public const string SyncProgram = "/home/steam/overstrike2/os2-sync", SyncRunning = "/tmp/os2-sync";

    public static int RestartSync(IDocker d, IConfig cfg, Action<double>? sleep = null)
    {
        sleep ??= s => Thread.Sleep(TimeSpan.FromSeconds(s));
        var name = cfg.Str("bridge.sync_container");
        if (name.Length == 0)
            throw Out.Stop("REFUSED: no bridge.sync_container in the private config - no sync to restart");
        var before = d.Inspect("containers", name) ?? throw Out.Stop($"REFUSED: no container {name}");
        if (Json.Truthy(InspectOf.Labels(before)[TestCopy.TestLabel]))
            throw Out.Stop($"REFUSED: {name} carries the test label - not production's sync");
        if (Json.Str(before, "State", "Status") != "running")
            throw Out.Stop($"REFUSED: {name} is {Json.Str(before, "State", "Status") ?? "?"} - a restart is for a running sync");
        JsonNode? status = null;
        try
        {
            status = Json.Parse(d.ExecIn(name, ["sh", "-c", "cat /data/sync-status.json"], 60));
        }
        catch (JsonSyntaxError)
        {
        }
        if (status is not JsonObject so || so["now"] is not JsonObject now)
            throw Out.Stop($"REFUSED: {name}'s sync-status.json does not say what it does now - whether a run is going cannot be told");
        var state = Json.Str(now, "state") ?? "";
        if (now["run"] is not null || state is not ("idle" or "stopped"))
            throw Out.Stop($"REFUSED: a run is going ({state}) - restart the sync once it has ended");
        var startedBefore = Json.Str(before, "State", "StartedAt") ?? "";
        var r = d.Call("POST", $"/containers/{name}/restart?t=30", timeout: 120);
        if (r.Status != 204)
            throw Out.Fail($"restart of {name}: HTTP {r.Status} {Py.Slice(r.Shown, 0, 200)}");
        var startedAfter = Json.Str(d.Inspect("containers", name), "State", "StartedAt") ?? "";
        if (startedAfter == startedBefore)
            throw Out.Fail($"{name} did not start anew - look");
        Out.Log($"{name}: idle ({state}), restarted - started {Py.Slice(startedAfter, 0, 19)}Z, was {Py.Slice(startedBefore, 0, 19)}Z");
        for (var i = 0; i < 10; i++)
        {
            var sums = Py.SplitLines(d.ExecIn(name, ["sh", "-c", $"sha256sum {SyncRunning} {SyncProgram} 2>/dev/null"], 60)).Select(l => l.Split(' ')[0]).ToList();
            if (sums.Count == 2 && sums[0].Length == 64 && sums[0] == sums[1])
            {
                Out.Log($"{name}: {SyncRunning} is the program folder's os2-sync, sha256 {sums[0][..16]}");
                return 0;
            }
            sleep(3);
        }
        throw Out.Fail($"{SyncRunning} is not the program folder's os2-sync 30 s after the start - look at {name}'s log");
    }

    public static int Command(string[] args)
    {
        var a = new Args(args, "server build [--image] | server ship [--apply] [--force] | server restart-sync [--config FILE]", flags: ["--apply", "--force", "--image"],
                         options: ["--config"]);
        var command = a.Choice(["build", "ship", "restart-sync"]);
        a.Done();
        if (command == "restart-sync")
        {
            var cfg = new Config(a.Opt("--config"));
            return RestartSync(new Docker(cfg), cfg);
        }
        if (command == "build")
        {
            var useConfig = a.Flag("--image") && (a.Opt("--config") is not null || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OVERSTRIKE2_CONFIG"))
                                                  || File.Exists(Repo.P(".private", "config.toml")));
            Build(a.Flag("--image"), useConfig ? new Config(a.Opt("--config")) : null);
            return 0;
        }
        return Ship(new Config(a.Opt("--config")), a.Flag("--apply"), a.Flag("--force"));
    }
}

/// <summary>The Steam Workshop items: the HUD addon and our own maps, through the Steam client logged in on this PC (ported from deploy/workshop.py, gone since release 193).
///
///     workshop hud-thumbs [args...]     the menu's map pictures (the thumbs command, with these arguments)
///     workshop hud-build [--no-thumbs]  compile the addon with the CS2 Workshop Tools (hud/build.ps1)
///     workshop hud-query | hud-public   read-only: the item as Steam has it / whether everybody can see it
///     workshop hud-publish --note "..." --user-approved-update
///                                       a content update of the ONE HUD item (hud/PUBLISH.md, AGENTS.md)
///     workshop map &lt;args...&gt;            maps/publisher/publish.ps1 with these arguments (-Map gg_x -Query, ...)
///
/// Read hud/PUBLISH.md before any upload: every new version waits in Valve's moderation queue, and a new HUD item is never created. The
/// private config gives the CS2 folder (workshop.cs2_dir; empty: found through Steam's library folders).</summary>
static class Workshop
{
    /// <summary>How a PowerShell script is run: its exit code. The self-tests take its command line instead.</summary>
    public static Func<List<string>, int> RunScript = argv => Proc.Run(argv);

    public static List<string> PsArgv(string script, IEnumerable<string> args, IConfig cfg)
    {
        var cs2 = cfg.Str("workshop.cs2_dir");
        return ["powershell", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, .. args, .. cs2.Length > 0 ? new[] { "-Cs2", cs2 } : []];
    }

    public static int Command(string[] args)
    {
        var a = new Args(args, "workshop hud-thumbs|hud-build|hud-query|hud-public|hud-publish|map [--no-thumbs] [--note N] [--user-approved-update] [--config FILE] ...",
                         flags: ["--no-thumbs", "--user-approved-update"], options: ["--note", "--config"]);
        var command = a.Choice(["hud-thumbs", "hud-build", "hud-query", "hud-public", "hud-publish", "map"]);
        var rest = a.Rest();   // what hud-thumbs and map hand on
        var cfg = new Config(a.Opt("--config"));
        var hud = Repo.P("hud");
        switch (command)
        {
            case "hud-thumbs":
                return Thumbs.Command([.. rest]);
            case "hud-build":
                return RunScript(PsArgv(Path.Combine(hud, "build.ps1"), a.Flag("--no-thumbs") ? ["-NoThumbs"] : [], cfg));
            case "hud-query":
                return RunScript(PsArgv(Path.Combine(hud, "publish.ps1"), ["-Query"], cfg));
            case "hud-public":
                return RunScript(PsArgv(Path.Combine(hud, "publish.ps1"), ["-Public"], cfg));
            case "hud-publish":
                if (string.IsNullOrEmpty(a.Opt("--note")))
                    throw Out.Fail("--note: the change note Steam shows with the update", 2);
                return RunScript(PsArgv(Path.Combine(hud, "publish.ps1"), ["-Note", a.Opt("--note")!, .. a.Flag("--user-approved-update") ? new[] { "-UserApprovedUpdate" } : []], cfg));
            default:
                return RunScript(PsArgv(Repo.P("maps", "publisher", "publish.ps1"), rest, cfg));
        }
    }
}
