using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace OverStrike2.Deploy;

/// <summary>deploy/test_deploy.py, ported: the bridge swap against a stand-in Docker API, which data files ship, the plugin's files, where
/// the server programs ship and how, the bridge's build context, and compose.yml, its installer and compose.example.yml held to what the
/// parts read. No server, no Docker, no private config.</summary>
static class DeployTests
{
    const string IP = "192.0.2.10", MAC = "mac-of-the-bridge", NET = "game_net";

    /// <summary>The Docker Engine API calls the bridge command makes, on containers and tags kept in memory. oldEngine: a stopped container
    /// keeps its MAC only in Config.MacAddress (engines before API 1.44), not on its network endpoint.</summary>
    sealed class FakeDocker : DockerBase
    {
        readonly bool oldEngine;
        public string HealthText;
        public readonly List<(string Method, string Route, string Arg)> Calls = [];
        int n;
        public readonly List<List<string>> Execs = [];
        public readonly Dictionary<string, string> Refs = new()
        {
            ["localhost/overstrike2-bridge:latest"] = "sha256:image-old-00000",
            ["localhost/overstrike2-bridge:candidate"] = "sha256:image-new-00000",
        };
        readonly Dictionary<string, JsonObject> images = [];
        public readonly Dictionary<string, JsonObject> Containers = [];

        public FakeDocker(bool bridgeRunning = true, bool oldEngine = false, bool pinned = true, string health = "os2-health 1\nlog pushes 3\n", bool bare = false)
        {
            this.oldEngine = oldEngine;
            HealthText = health;
            foreach (var i in Refs.Values.Distinct())
                images[i] = new JsonObject { ["Id"] = i, ["Config"] = Json.O("""{"Env": ["PATH=/usr/bin"], "Labels": {"org.opencontainers.image.title": "x"}}""") };
            var env = bare ? new JsonArray("PATH=/usr/bin", "OVERSTRIKE2_SETTINGS_CONTENT={ \"schema\": 1 }")   // bare: its settings, no secret beside them
                           : new JsonArray("PATH=/usr/bin", "HTTP_PORT=8080", "LOG_SECRET=s", "RCON_PASSWORD=p");
            var endpoint = new JsonObject
            {
                ["IPAMConfig"] = pinned ? new JsonObject { ["IPv4Address"] = IP } : null,
                ["Aliases"] = new JsonArray("bridge"),
                ["MacAddress"] = MAC,
                ["IPAddress"] = IP,
            };
            Containers["c-old"] = new JsonObject
            {
                ["Id"] = "c-old", ["Name"] = "/bridge", ["Image"] = "sha256:image-old-00000", ["RestartCount"] = 0,
                ["State"] = Json.O("""{"Running": true, "Status": "running", "StartedAt": "start-0"}"""),
                ["Config"] = new JsonObject { ["Image"] = "localhost/overstrike2-bridge", ["Env"] = env, ["Labels"] = Json.O("""{"com.docker.compose.service": "bridge"}""") },
                ["HostConfig"] = Json.O($$"""{"RestartPolicy": {"Name": "unless-stopped"}, "NetworkMode": "{{NET}}", "ReadonlyRootfs": true}"""),
                ["NetworkSettings"] = new JsonObject { ["Networks"] = new JsonObject { [NET] = endpoint } },
            };
            var gameEnv = new JsonArray("RCON_PORT=27015");
            if (bare)
            {
                gameEnv.Add("CS2_LOG_HTTP_URL=http://bridge:8090/log/fromthegame");
                gameEnv.Add("CS2_RCONPW=gamepw");
            }
            Containers["c-game"] = new JsonObject
            {
                ["Id"] = "c-game", ["Name"] = "/cs2", ["Image"] = "sha256:game", ["RestartCount"] = 0,
                ["State"] = Json.O("""{"Running": true, "Status": "running", "StartedAt": "start-game"}"""),
                ["Config"] = new JsonObject { ["Image"] = "game", ["Env"] = gameEnv },
                ["HostConfig"] = new JsonObject(), ["NetworkSettings"] = new JsonObject { ["Networks"] = new JsonObject() },
            };
            if (!bridgeRunning)
                Stop(Containers["c-old"]);
        }

        public JsonObject? Find(string name) => Containers.Values.FirstOrDefault(c => Json.Str(c, "Id") == name || Json.Str(c, "Name") == "/" + name);

        void Stop(JsonObject c)
        {
            c["State"]!["Running"] = false;
            c["State"]!["Status"] = "exited";
            foreach (var (_, e) in Json.Obj(c, "NetworkSettings", "Networks"))
            {
                e!["IPAddress"] = "";
                if (oldEngine)
                {
                    c["Config"]!["MacAddress"] = Json.Truthy(c["Config"]!["MacAddress"]) ? c["Config"]!["MacAddress"]!.DeepClone() : e["MacAddress"]!.DeepClone();
                    e["MacAddress"] = "";
                }
            }
        }

        public override JsonObject? Inspect(string kind, string name)
        {
            var c = kind == "containers" ? Find(name) : images.GetValueOrDefault(Refs.GetValueOrDefault(name, name));
            return c?.DeepClone() as JsonObject;
        }

        public override DockerReply Call(string method, string path, JsonNode? body = null, byte[]? raw = null, string ctype = "application/json", double timeout = 60)
        {
            var qi = path.IndexOf('?');
            var route = qi < 0 ? path : path[..qi];
            var q = new Dictionary<string, string>();
            foreach (var p in (qi < 0 ? "" : path[(qi + 1)..]).Split('&').Where(p => p.Contains('=')))
                q[p.Split('=', 2)[0]] = p.Split('=', 2)[1];
            var parts = route.Trim('/').Split('/');
            Calls.Add((method, route, q.GetValueOrDefault("name") is { Length: > 0 } nm ? nm : q.GetValueOrDefault("tag") ?? ""));
            if (parts[0] == "images" && parts[^1] == "tag")
            {
                Refs[$"{q["repo"]}:{q["tag"]}"] = parts[1];
                return new DockerReply(201, Bytes: []);
            }
            if (route == "/containers/create")
            {
                if (Find(q["name"]) is not null)
                    return new DockerReply(409, Error: "name in use");
                n++;
                var cid = $"c-new-{n}";
                var nets = new JsonObject();
                foreach (var (net, e) in Json.Obj(body, "NetworkingConfig", "EndpointsConfig"))
                    nets[net] = new JsonObject
                    {
                        ["IPAMConfig"] = Json.At(e, "IPAMConfig")?.DeepClone(), ["Aliases"] = Json.At(e, "Aliases")?.DeepClone(),
                        ["MacAddress"] = Json.Str(e, "MacAddress") is { Length: > 0 } m ? m : "", ["IPAddress"] = "",
                    };
                Containers[cid] = new JsonObject
                {
                    ["Id"] = cid, ["Name"] = "/" + q["name"], ["Image"] = Refs[Json.Str(body, "Image") + ":latest"], ["RestartCount"] = 0,
                    ["State"] = Json.O("""{"Running": false, "Status": "created", "StartedAt": ""}"""), ["body"] = body!.DeepClone(),
                    ["Config"] = new JsonObject { ["Image"] = Json.At(body, "Image")!.DeepClone(), ["Env"] = Json.At(body, "Env")!.DeepClone(), ["Labels"] = Json.At(body, "Labels")!.DeepClone() },
                    ["HostConfig"] = Json.At(body, "HostConfig")?.DeepClone(), ["NetworkSettings"] = new JsonObject { ["Networks"] = nets },
                };
                return new DockerReply(201, Json: new JsonObject { ["Id"] = cid });
            }
            var c = parts[0] == "containers" ? Find(parts[1]) : null;
            if (c is null)
                return new DockerReply(404, Error: "no such container");
            if (method == "DELETE")
                Containers.Remove(Json.Str(c, "Id")!);
            else if (parts[^1] == "stop")
                Stop(c);
            else if (parts[^1] == "start")
            {
                n++;
                c["State"]!["Running"] = true;
                c["State"]!["Status"] = "running";
                c["State"]!["StartedAt"] = $"start-{n}";
                foreach (var (_, e) in Json.Obj(c, "NetworkSettings", "Networks"))
                {
                    e!["IPAddress"] = Json.Str(e, "IPAMConfig", "IPv4Address") is { Length: > 0 } ip ? ip : "198.51.100.99";   // unpinned: whatever is free
                    e["MacAddress"] = Json.Str(e, "MacAddress") is { Length: > 0 } mac ? mac : Json.Str(c, "Config", "MacAddress") is { Length: > 0 } cm ? cm : "a-new-random-mac";
                }
            }
            else if (parts[^1] == "rename")
            {
                if (Find(q["name"]) is not null)
                    return new DockerReply(409, Error: "name in use");
                c["Name"] = "/" + q["name"];
            }
            return new DockerReply(204, Bytes: []);
        }

        public override string ExecIn(string container, IReadOnlyList<string> argv, double timeout = 60)
        {
            Execs.Add([.. argv]);
            if (argv[2] == Bridge.InnerRcon)   // something said on the server: its log push reaches the bridge
                HealthText = "os2-health 1\nlog pushes 1\n";
            return HealthText;
        }

        public List<(string Method, string Route, string Arg)> Writes() => Calls.Where(c => c.Method is "POST" or "DELETE").ToList();
    }

    static object Swap(FakeDocker fake, string source = "candidate")
    {
        try
        {
            return new Bridge(fake, "localhost/overstrike2-bridge", "bridge", "cs2") { Sleep = _ => { } }.Swap(source, "previous");
        }
        catch (Exit e)
        {
            return $"exit {e.Code}";
        }
    }

    static bool Is(object r, int code) => r is int i && i == code;

    static readonly List<NetAddress> Pinned = [new(NET, IP, MAC)];

    static JsonObject Box(params string[] env) => new() { ["Config"] = new JsonObject { ["Env"] = new JsonArray(env.Select(e => (JsonNode?)e).ToArray()) } };

    /// <summary>Whether a file is still the one it is now - not another renamed over it: its index on the volume on Windows; elsewhere a
    /// handle kept open on it, which reads the new bytes only if they were written into this very file.</summary>
    static Func<bool> Watch(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var id = FileId(path);
            return () => id is not null && FileId(path) == id;
        }
        var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return () =>
        {
            using (handle)
            {
                handle.Position = 0;
                using var ms = new MemoryStream();
                handle.CopyTo(ms);
                return ms.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(path));
            }
        };
    }

    static string? FileId(string path)
    {
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(h, out var info) ? $"{info.VolumeSerialNumber}:{info.FileIndexHigh}:{info.FileIndexLow}" : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

#pragma warning disable SYSLIB1054 // DllImport: no unsafe code for one call in a self-test
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);
#pragma warning restore SYSLIB1054

    static int? Exits(Action body)
    {
        try
        {
            body();
            return null;
        }
        catch (Exit e)
        {
            return e.Code;
        }
    }

    public static void Run()
    {
        var check = SelfTest.Check;

        // a running bridge: stopped, renamed aside, recreated on the same address, verified, the old one removed
        var f = new FakeDocker();
        var r = Swap(f);
        var created = f.Find("bridge")!;
        check("swap, running: done, the new bridge runs the candidate on the IP and MAC it had",
              Is(r, 0) && Json.Str(created, "Id") != "c-old" && Json.Truthy(Json.At(created, "State", "Running")) && Json.Str(created, "Image") == "sha256:image-new-00000"
              && Bridge.Addresses(created).SequenceEqual(Pinned) && !f.Containers.ContainsKey("c-old"));
        check("swap, running: :previous is the old image, :latest the new one",
              f.Refs["localhost/overstrike2-bridge:previous"] == "sha256:image-old-00000" && f.Refs["localhost/overstrike2-bridge:latest"] == "sha256:image-new-00000");
        check("swap, running: the old bridge was stopped first", f.Calls.Contains(("POST", "/containers/c-old/stop", "")));
        check("swap, running: the game container is untouched", Json.Str(f.Find("cs2"), "State", "StartedAt") == "start-game");

        // a stopped bridge: no runtime address, swapped all the same onto the pinned one, started on the new image
        f = new FakeDocker(bridgeRunning: false);
        r = Swap(f);
        created = f.Find("bridge")!;
        check("swap, stopped: done - checked against its configured IP and MAC, not the runtime address it lacks",
              Is(r, 0) && Json.Truthy(Json.At(created, "State", "Running")) && Json.Str(created, "Image") == "sha256:image-new-00000" && Bridge.Addresses(created).SequenceEqual(Pinned));
        check("swap, stopped: no stop call for a bridge that is not running", !f.Calls.Any(c => c.Route.EndsWith("/stop", StringComparison.Ordinal)));
        var endpoint = Json.At(created, "body", "NetworkingConfig", "EndpointsConfig", NET);
        check("swap, stopped: the new bridge is created on the pinned address",
              Json.Same(Json.At(endpoint, "IPAMConfig"), new JsonObject { ["IPv4Address"] = IP }) && Json.Str(endpoint, "MacAddress") == MAC);

        // an older engine keeps a stopped container's MAC in Config only: the new bridge still gets it
        f = new FakeDocker(bridgeRunning: false, oldEngine: true);
        r = Swap(f);
        created = f.Find("bridge")!;
        check("swap, stopped on an older engine: the MAC comes from Config.MacAddress, and it is kept",
              Is(r, 0) && Json.Str(created, "body", "NetworkingConfig", "EndpointsConfig", NET, "MacAddress") == MAC && Bridge.Addresses(created).SequenceEqual(Pinned));

        // a stopped bridge whose new image fails its check: the new one goes, the old one is back under its name, stopped as it was
        f = new FakeDocker(bridgeRunning: false, health: "(exec start 500)");
        r = Swap(f);
        var back = f.Find("bridge")!;
        check("swap, stopped, check fails: the old bridge is back, stopped, on the old image; the new one is gone",
              Is(r, 1) && Json.Str(back, "Id") == "c-old" && !Json.Truthy(Json.At(back, "State", "Running")) && f.Containers.Count == 2
              && f.Refs["localhost/overstrike2-bridge:latest"] == "sha256:image-old-00000");
        check("swap, stopped, check fails: the old bridge is not started", !f.Calls.Contains(("POST", "/containers/c-old/start", "")));

        // a running bridge whose new image fails: the old one is back up
        f = new FakeDocker(health: "(exec start 500)");
        r = Swap(f);
        back = f.Find("bridge")!;
        check("swap, running, check fails: the old bridge is back up", Is(r, 1) && Json.Str(back, "Id") == "c-old" && Json.Truthy(Json.At(back, "State", "Running"))
              && f.Containers.Count == 2);

        // a stopped bridge already on the target image: started and checked, nothing recreated
        f = new FakeDocker(bridgeRunning: false);
        f.Refs["localhost/overstrike2-bridge:candidate"] = "sha256:image-old-00000";
        r = Swap(f);
        check("swap, stopped on the target already: started and checked, not recreated",
              Is(r, 0) && Json.Str(f.Find("bridge"), "Id") == "c-old" && Json.Truthy(Json.At(f.Find("bridge"), "State", "Running"))
              && !f.Calls.Any(c => c.Route == "/containers/create"));

        // a running bridge on the target: nothing at all
        f = new FakeDocker();
        f.Refs["localhost/overstrike2-bridge:candidate"] = "sha256:image-old-00000";
        check("swap, running on the target already: nothing to do, nothing written", Is(Swap(f), 0) && f.Writes().Count == 0);

        // the settings are an environment variable: a new container made from the old one's inspect has them, on a read-only root too
        f = new FakeDocker(bare: true, health: "os2-health 1\nlog pushes 3\n");
        r = Swap(f);
        created = f.Find("bridge")!;
        check("swap, the settings in the environment: the new bridge has them byte for byte, its root still read-only, and nothing but the inspect was needed",
              Is(r, 0) && Json.Arr(created, "Config", "Env").Any(e => Json.AsStr(e) == "OVERSTRIKE2_SETTINGS_CONTENT={ \"schema\": 1 }")
              && Json.AsBool(Json.At(created, "HostConfig", "ReadonlyRootfs")) == true && !f.Calls.Any(c => c.Route.Contains("archive", StringComparison.Ordinal)));

        // a bridge with no secret in its environment: the game container's are used
        f = new FakeDocker(bare: true, health: "os2-health 1\nlog pushes 0\n");
        r = Swap(f);
        var gets = f.Execs.Where(a => a[2] == Bridge.InnerGet).Select(a => a[3]).ToList();
        var says = f.Execs.Where(a => a[2] == Bridge.InnerRcon).ToList();
        check("swap, no LOG_SECRET and no RCON_PASSWORD in the bridge: /health with the secret and port of the game's CS2_LOG_HTTP_URL, RCON with its CS2_RCONPW",
              Is(r, 0) && gets.Count > 0 && gets.All(g => g == "http://bridge:8090/health/fromthegame") && says.Count > 0 && says[0][3] == "gamepw");
        f = new FakeDocker(health: "os2-health 1\nlog pushes 0\n");
        r = Swap(f);
        gets = f.Execs.Where(a => a[2] == Bridge.InnerGet).Select(a => a[3]).ToList();
        says = f.Execs.Where(a => a[2] == Bridge.InnerRcon).ToList();
        check("swap, a game container without them: the bridge's own LOG_SECRET, HTTP_PORT and RCON_PASSWORD, as before",
              Is(r, 0) && gets.Count > 0 && gets.All(g => g == "http://bridge:8080/health/s") && says.Count > 0 && says[0][3] == "p");

        // a rollback to a bridge from before release 191: its /health begins gg-health, and the swap takes it as up; anything else is no bridge's
        f = new FakeDocker(health: "gg-health 1\nlog pushes 3\n");
        check("swap, a bridge from before release 191 (gg-health): up, as any other - a rollback to one goes through", Is(Swap(f), 0));
        f = new FakeDocker(health: "Not Found\n");
        check("swap, a /health that is no bridge's: not up - the old bridge put back", Is(Swap(f), 1));

        // a stopped bridge with no pinned IP: nothing to recreate it on - refused before any change
        f = new FakeDocker(bridgeRunning: false, pinned: false);
        r = Swap(f);
        check($"swap, stopped and unpinned: refused, nothing written ({r})", r is "exit 1" && f.Writes().Count == 0);

        // ---- os2: the secrets of a bridge that has none in its environment
        check("os2: the bridge's secret and port from the log push address, its templated form too",
              InspectOf.BridgeSecret(Box(@"CS2_LOG_HTTP_URL=http:\/\/bridge:8090\/log\/abc123")) == "abc123"
              && InspectOf.BridgePort(Box("CS2_LOG_HTTP_URL=http://bridge:8090/log/abc123")) == "8090");
        check("os2: the log push address first, then the bridge's own variables; a URL with no port is the default port",
              InspectOf.BridgeSecret(Box("CS2_LOG_HTTP_URL=http://b:1/log/a"), Box("LOG_SECRET=b")) == "a" && InspectOf.BridgeSecret(Box(), Box("LOG_SECRET=b")) == "b"
              && InspectOf.BridgeSecret(null, null) == "" && InspectOf.BridgePort(Box("CS2_LOG_HTTP_URL=http://b/log/a"), Box("HTTP_PORT=9")) == "9"
              && InspectOf.BridgePort(Box()) == "8080");
        check("os2: the RCON password from the game container first, then the bridge's",
              InspectOf.RconPassword(Box("CS2_RCONPW=a"), Box("RCON_PASSWORD=b")) == "a" && InspectOf.RconPassword(Box(), Box("RCON_PASSWORD=b")) == "b"
              && InspectOf.RconPassword(null, null) == "");

        // ---- data: what ships
        var dest = SelfTest.Scratch("deploy-data-");
        try
        {
            var hand = new[] { "ours.json", "sizes.json", "blocked.json" }.Where(x => File.Exists(Repo.P("bridge", x))).ToList();
            var first = new[] { "maps.json", "broken.json", "search.json" }.Where(x => File.Exists(Repo.P("bridge", x))).ToList();
            var files = Data.Sets["bridge"].Files;
            check($"data: a server with no lists yet gets the hand-made files and the first lists ({string.Join(", ", Data.Plan("bridge", files, dest))})",
                  Data.Plan("bridge", files, dest).SequenceEqual(files.Where(x => hand.Concat(first).Contains(x))));
            foreach (var x in new[] { "maps.json", "broken.json", "search.json" })
                File.WriteAllText(Path.Combine(dest, x), "{}");
            check("data: once the server has its lists, only the hand-made files ship - the sync writes the rest there (77 a)",
                  Data.Plan("bridge", files, dest).SequenceEqual(files.Where(hand.Contains)) && hand.Contains("ours.json") && hand.Contains("sizes.json"));
            check("data: blocked.json ships when the checkout has it, and is skipped (said) when not",
                  Data.Plan("bridge", files, dest).Contains("blocked.json") == File.Exists(Repo.P("bridge", "blocked.json")));
            check("data: the bridge's files are its only set - the server side ships with server.py", Data.Sets.Keys.SequenceEqual(["bridge"]));
        }
        finally
        {
            SelfTest.Remove(dest);
        }

        // ---- plugin: what ships
        var csproj = @"<None Include=""..\CatchUp\catchup.json"" Link=""catchup.json"" CopyToOutputDirectory=""PreserveNewest"" />" + "\n"
                     + @"<EmbeddedResource Include=""convars.txt"" LogicalName=""convars.txt"" />" + "\n"
                     + @"<None Include=""blocked.txt"" CopyToOutputDirectory=""PreserveNewest"" />" + "\n" + @"<None Remove=""hud\**"" />" + "\n";
        check("plugin: only the config files the project declares ship - blocked.txt and the linked catchup.json, never a stale file of an older build",
              Plugin.DeclaredContent(csproj).SequenceEqual(["blocked.txt", "catchup.json"]));
        var projectText = File.ReadAllText(Path.Combine(Plugin.Project, Plugin.Name + ".csproj"));
        var real = Plugin.DeclaredContent(projectText);
        check($"plugin: the real project declares blocked.txt and catchup.json ({string.Join(", ", real)})", real.SequenceEqual(["blocked.txt", "catchup.json"]));
        var compilesCore = Regex.Matches(projectText, @"Include=""[^""]*?\.\.\\(core)\\").Any(m => m.Groups[1].Value == "core");
        var seen = new List<string>();
        var (savedClean, savedDotnet) = (Repo.CleanTree, Proc.Dotnet);
        try
        {
            Repo.CleanTree = paths =>
            {
                seen.AddRange(paths);
                return "c0ffee";
            };
            Proc.Dotnet = (_, _) => (0, "self-test passed\n");
            Plugin.Build();
        }
        finally
        {
            (Repo.CleanTree, Proc.Dotnet) = (savedClean, savedDotnet);
        }
        check($"plugin: the project compiles core/ in, and a build refuses uncommitted changes there too ({string.Join(", ", seen)})",
              compilesCore && new[] { "plugin", "bridge", "core" }.All(seen.Contains));
        var ran = new List<(string, string)>();
        int? catchupFailed = null;
        (savedClean, savedDotnet) = (Repo.CleanTree, Proc.Dotnet);
        try
        {
            Repo.CleanTree = _ => "c0ffee";
            Proc.Dotnet = (args, cwd) =>
            {
                ran.Add((args[0], Path.GetRelativePath(Repo.Root, cwd).Replace('\\', '/')));
                return (0, "passed\n");
            };
            SelfTest.Capture(Plugin.Build);
            Proc.Dotnet = (_, cwd) => cwd == Plugin.CatchUpSelfTest ? (1, "CatchUp self-test: 1 of 9 checks FAILED\n") : (0, "passed\n");
            catchupFailed = SelfTest.Capture(Plugin.Build).Exit;
        }
        finally
        {
            (Repo.CleanTree, Proc.Dotnet) = (savedClean, savedDotnet);
        }
        check($"plugin: a build runs the plugin's self-test and catch-up's, and catch-up's failing fails the build ([{string.Join(", ", ran.Select(r => Py.Repr(r)))}])",
              ran.SequenceEqual([("build", "plugin/OverStrike2"), ("run", "plugin/OverStrike2.SelfTest"), ("run", "plugin/CatchUp/SelfTest")]) && catchupFailed == 1);

        // ---- server: what ships where, only what the build made, renamed into place
        check("server: the launcher pre.sh and the three mode cfgs it ships are in the checkout",
              new[] { ServerShip.Launcher }.Concat(ServerShip.Cfgs).All(p => File.Exists(Repo.P(p.Split('/')))) && ServerShip.Cfgs.Length == 3);
        var tmp = SelfTest.Scratch("deploy-server-");
        var (savedGit, savedClean2, savedCommitted) = (Repo.Git, Repo.CleanTree, Repo.CommittedBefore);
        try
        {
            ServerShip.OutDir = Path.Combine(tmp, "ship");
            ServerShip.Record = Path.Combine(tmp, "ship", "build.json");
            var record = new JsonObject { ["commit"] = "c0ffee", ["programs"] = new JsonObject() };
            foreach (var name in ServerShip.Programs.Keys)
            {
                Directory.CreateDirectory(Path.Combine(ServerShip.OutDir, name));
                File.WriteAllBytes(Path.Combine(ServerShip.OutDir, name, name), Encoding.UTF8.GetBytes("ELF " + name));
                record["programs"]![name] = new JsonObject { ["sha256"] = Repo.Sha256(Path.Combine(ServerShip.OutDir, name, name)) };
            }
            File.WriteAllText(ServerShip.Record, Json.Dumps(record));
            var diff = "";
            Repo.Git = a => a[0] == "diff" ? diff : "c0ffee";
            Repo.CleanTree = _ => "c0ffee";
            Repo.CommittedBefore = (_, _, _) => null;
            string prog = Path.Combine(tmp, "program"), conf = Path.Combine(tmp, "config");
            var cfg = new MemoryConfig(new() { ["server.program_dir"] = prog, ["server.config_dir"] = conf });
            var targets = ServerShip.Plan(cfg).Select(x => Path.GetRelativePath(tmp, x.Dst).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
            check($"server: os2-server, os2-sync and pre.sh go to the program folder, the mode cfgs to the config folder ({string.Join(", ", targets)})",
                  targets.SequenceEqual(new[] { "program/os2-server", "program/os2-sync", "program/pre.sh" }.Concat(ServerShip.Cfgs.Select(c => "config/" + Path.GetFileName(c))).Order(StringComparer.Ordinal)));
            check("server: an empty program folder in the config ships the cfgs alone",
                  ServerShip.Plan(new MemoryConfig(new() { ["server.config_dir"] = conf })).Select(x => Path.GetFileName(x.Dst)).SequenceEqual(ServerShip.Cfgs.Select(Path.GetFileName)));
            ServerShip.Ship(cfg, apply: false);
            check("server: ship without --apply copies nothing", !Path.Exists(prog) && !Path.Exists(conf));
            foreach (var (empty, what) in new[] { (new MemoryConfig([]), "a config that names no folder"), (new MemoryConfig(new() { ["server.config_dir"] = conf }), "a config with no program folder") })
            {
                check($"server: --apply with {what} fails loudly, and copies nothing", Exits(() => ServerShip.Ship(empty, true)) == 2 && !Path.Exists(conf) && !Path.Exists(prog));
                check($"server: a dry run with {what} just says so", Exits(() => ServerShip.Ship(empty, false)) is null);
            }
            ServerShip.Ship(cfg, apply: true);
            var landed = ServerShip.Plan(cfg).All(x => File.Exists(x.Dst) && File.ReadAllBytes(x.Dst).AsSpan().SequenceEqual(File.ReadAllBytes(x.Src)));
            check("server: ship --apply puts every file in place, byte for byte, and leaves no .new behind",
                  landed && !new[] { prog, conf }.SelectMany(Directory.EnumerateFiles).Any(x => x.EndsWith(".new", StringComparison.Ordinal)));
            foreach (var (_, dst) in ServerShip.Plan(cfg))   // the server has older copies of everything, the same files as before
                File.AppendAllText(dst, "\nan older copy\n");
            var before = ServerShip.Plan(cfg).ToDictionary(x => x.Dst, x => Watch(x.Dst));
            ServerShip.Ship(cfg, apply: true, force: true);
            var same = before.Where(kv => kv.Value()).Select(kv => Path.GetFileName(kv.Key)).Order(StringComparer.Ordinal).ToList();
            var known = true;
            check($"server: pre.sh and the cfgs are written in place: a container's single-file mount stays on that file and sees the new one ({string.Join(", ", same)})",
                  known && same.ToHashSet().SetEquals(ServerShip.Cfgs.Select(Path.GetFileName).Append("pre.sh")!));
            check("server: the programs are renamed into place: what runs from the old file keeps it", known && !same.Intersect(["os2-server", "os2-sync"]).Any());
            check("server: both ways the bytes are the build's and the repository's", ServerShip.Plan(cfg).All(x => File.ReadAllBytes(x.Dst).AsSpan().SequenceEqual(File.ReadAllBytes(x.Src))));
            var locked = Path.Combine(prog, "pre.sh");
            File.AppendAllText(locked, "\nan older copy\n");
            File.SetAttributes(locked, FileAttributes.ReadOnly);
            check("server: a pre.sh or cfg that cannot be written in place is a failure that says so, not a traceback", Exits(() => ServerShip.Ship(cfg, true, true)) == 1);
            File.SetAttributes(locked, FileAttributes.Normal);
            ServerShip.Ship(cfg, apply: true, force: true);
            File.AppendAllText(Path.Combine(ServerShip.OutDir, "os2-sync", "os2-sync"), " changed");
            check("server: a program that is not the file the build recorded is refused", Exits(() => ServerShip.Ship(cfg, true)) == 1);
            File.WriteAllText(Path.Combine(ServerShip.OutDir, "os2-sync", "os2-sync"), "ELF os2-sync");
            diff = "server/OverStrike2.Server/Watch.cs";
            check("server: sources changed since the build - refused until it is built again", Exits(() => ServerShip.Ship(cfg, true)) == 1);
            diff = "";
            File.WriteAllText(Path.Combine(conf, Path.GetFileName(ServerShip.Cfgs[0])), "sv_cheats 1 // made on the server\n");
            check("server: a cfg changed on the server that no commit holds is DRIFT - refused", Exits(() => ServerShip.Ship(cfg, true)) == 1);
            ServerShip.Ship(cfg, apply: true, force: true);
            check("server: --force ships over it",
                  File.ReadAllBytes(Path.Combine(conf, Path.GetFileName(ServerShip.Cfgs[0]))).AsSpan().SequenceEqual(File.ReadAllBytes(Repo.P(ServerShip.Cfgs[0].Split('/')))));
            var one = Path.Combine(tmp, "single");
            Directory.CreateDirectory(one);
            foreach (var x in new[] { "os2-server", "OverStrike2.Core.pdb" })
                File.WriteAllText(Path.Combine(one, x), "");
            check("server: a published program with only debug symbols beside it is a single file", Exits(() => ServerShip.SingleFile(one, "os2-server")) is null);
            File.WriteAllText(Path.Combine(one, "libSystem.Native.so"), "");
            check("server: a published program with a library beside it is refused - it would need that file too", Exits(() => ServerShip.SingleFile(one, "os2-server")) == 1);
            string fa = Path.Combine(tmp, "a.bin"), fb = Path.Combine(tmp, "b.bin");
            File.WriteAllText(fa, "new");
            File.WriteAllText(fb, "old");
            check("os2: an atomic copy replaces the file and leaves no .new",
                  Repo.CopyVerified(fa, fb, atomic: true) == "copied" && File.ReadAllText(fb) == "new" && !File.Exists(fb + ".new"));
        }
        finally
        {
            (Repo.Git, Repo.CleanTree, Repo.CommittedBefore) = (savedGit, savedClean2, savedCommitted);
            ServerShip.OutDir = null!;
            ServerShip.Record = null!;
            SelfTest.Remove(tmp);
        }

        // ---- the bridge's build context: bridge/ and core/ as the repository lays them out, the Dockerfile at bridge/Dockerfile
        var savedClean3 = Repo.CleanTree;
        byte[] ctx;
        try
        {
            Repo.CleanTree = _ => "test";      // the context is HEAD's either way; a dirty tree is refused in real use
            ctx = Bridge.Context().Tar;
        }
        finally
        {
            Repo.CleanTree = savedClean3;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        byte[]? dockerfileInTar = null;
        using (var reader = new TarReader(new MemoryStream(ctx)))
        {
            while (reader.GetNextEntry(copyData: true) is { } e)
            {
                names.Add(e.Name.TrimEnd('/'));
                if (e.Name == "bridge/Dockerfile" && e.DataStream is { } ds)
                {
                    using var ms = new MemoryStream();
                    ds.CopyTo(ms);
                    dockerfileInTar = ms.ToArray();
                }
            }
        }
        var committedDockerfile = Repo.Show("HEAD", "bridge/Dockerfile");
        check("bridge context: bridge/ and core/ at their own paths, the Dockerfile at bridge/Dockerfile, no bin/obj and no core self-test",
              new[] { "bridge/Dockerfile", "bridge/OverStrike2.Bridge.csproj", "bridge/maps.json", "core/OverStrike2.Core.csproj", "core/Settings.cs" }.All(names.Contains)
              && !names.Any(x => x.StartsWith("core/SelfTest", StringComparison.Ordinal) || x.Contains("/bin/") || x.Contains("/obj/")) && Bridge.Dockerfile == "bridge/Dockerfile"
              && dockerfileInTar is not null && committedDockerfile is not null && dockerfileInTar.AsSpan().SequenceEqual(committedDockerfile));
        var bridgeDf = File.ReadAllText(Repo.P("bridge", "Dockerfile"));
        var sources = Regex.Matches(bridgeDf, @"^COPY (?!--from)(.+)$", RegexOptions.Multiline)
                           .SelectMany(m => m.Groups[1].Value.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries).SkipLast(1)).Select(s => s.TrimEnd('/')).ToList();
        check($"bridge context: every source the Dockerfile copies is in it ({string.Join(", ", sources)})",
              sources.Count > 0 && sources.All(s => names.Contains(s) || names.Any(x => x.StartsWith(s + "/", StringComparison.Ordinal))));

        // ---- compose.yml and its installer: everything they name is in the checkout, and they ship what the deploy commands ship
        var deploy = Repo.P("deploy");
        var compose = File.ReadAllText(Path.Combine(deploy, "compose.yml"));
        var dockerfile = File.ReadAllText(Path.Combine(deploy, "installer", "Dockerfile"));
        var install = File.ReadAllText(Path.Combine(deploy, "installer", "install.sh"));
        var named = Regex.Matches(compose, @"(\.\./[\w./-]+?)(?=:|\s|$)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToList();
        var dockerfiles = Regex.Matches(compose, @"^\s+dockerfile: (\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        check($"compose: every file of the checkout it mounts or builds from is there ({named.Count}, and the Dockerfiles {string.Join(", ", dockerfiles)})",
              named.Count > 0 && named.All(p => Path.Exists(Path.GetFullPath(Path.Combine(deploy, p)))) && dockerfiles.Order(StringComparer.Ordinal).SequenceEqual(["bridge/Dockerfile", "deploy/installer/Dockerfile"])
              && Regex.Matches(compose, Regex.Escape("context: ..")).Count == 2 && dockerfiles.All(x => File.Exists(Repo.P(x.Split('/')))));
        var copied = Regex.Matches(dockerfile, @"^COPY (?!--from)(.+)$", RegexOptions.Multiline)
                          .SelectMany(m => m.Groups[1].Value.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries).SkipLast(1)).ToList();
        check($"installer: every source its Dockerfile copies from the checkout is there ({copied.Count})", copied.Count > 0 && copied.All(s => Path.Exists(Repo.P(s.TrimEnd('/').Split('/')))));
        var pluginFiles = Regex.Match(dockerfile, @"COPY --from=build (/out/plugin/\S+(?: /out/plugin/\S+)*) /payload/plugins/OverStrike2/");
        check($"installer: the plugin's files are the ones deploy/plugin.py ships ({(pluginFiles.Success ? pluginFiles.Groups[1].Value : "none")})",
              pluginFiles.Success && pluginFiles.Groups[1].Value.Split(' ').Select(p => p[(p.LastIndexOf('/') + 1)..]).Order(StringComparer.Ordinal)
                                     .SequenceEqual(new[] { Plugin.Name + ".dll", Plugin.Name + ".pdb" }.Concat(real).Order(StringComparer.Ordinal)));
        var handFiles = Regex.Match(install, @"for f in ([\w. ]+); do\s+put ""/payload/data");
        var firstFiles = Regex.Match(install, @"for f in ([\w. ]+); do\s+if \[ -e");
        check("installer: the bridge's hand-made files always, the sync's lists only to a server without them - as data.py ships them",
              handFiles.Success && firstFiles.Success
              && handFiles.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Union(firstFiles.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet().SetEquals(Data.Sets["bridge"].Files)
              && firstFiles.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet().SetEquals(Data.FirstOnly));
        check("installer: the programs and the launcher go to the program volume, pre.sh also to the top of the game's",
              ServerShip.Programs.Keys.Append("pre.sh").All(x => install.Contains($"/overstrike2/{x}", StringComparison.Ordinal)) && install.Contains("/game/pre.sh", StringComparison.Ordinal));
        var mounts = new Dictionary<string, string>
        {
            ["/etc/overstrike2/overstrike2.jsonc"] = "the settings file", ["program:/home/steam/overstrike2:ro"] = "pre.sh's OS2_SERVER",
            ["plugins:/home/steam/cs2-plugins:ro"] = "paths.plugins", ["data:/data"] = "paths.bridgeData",
            ["target: /home/steam/workshop-sync"] = "paths.syncWork", ["sync-state:/home/steam/sync-state"] = "paths.syncState",
        };
        check($"compose: the settings file, the program volume and the data paths are where the defaults look ({string.Join(", ", mounts.Where(kv => !compose.Contains(kv.Key, StringComparison.Ordinal)).Select(kv => kv.Value))})",
              mounts.Keys.All(k => compose.Contains(k, StringComparison.Ordinal))
              && File.ReadAllText(Repo.P(ServerShip.Launcher.Split('/'))).Contains("/home/steam/overstrike2/os2-server", StringComparison.Ordinal));

        // ---- compose.example.yml, the shape of a host the commands ship to: nothing builds or runs it here, so this holds it to what the parts read
        var example = File.ReadAllText(Path.Combine(deploy, "compose.example.yml"));
        var exampleMounts = new Dictionary<string, string>
        {
            ["<program dir>:/home/steam/overstrike2:ro"] = "the programs", ["<program dir>/pre.sh:/home/steam/cs2-dedicated/pre.sh:ro"] = "the launcher",
            ["<plugins dir>:/home/steam/cs2-plugins:ro"] = "paths.plugins", ["<data dir>:/data:ro"] = "the game server's paths.bridgeData",
            ["<data dir>:/data #"] = "the bridge's and the sync's paths.bridgeData", [":/home/steam/workshop-sync #"] = "paths.syncWork",
            [":/home/steam/sync-state #"] = "paths.syncState", [":/home/steam/sync-cfg/"] = "paths.syncCfg", ["CS2_MAPGROUP: mg_ar_shoots"] = "the map group",
        };
        check($"compose.example: the program folder, the launcher and the data paths are where the defaults look ({string.Join(", ", exampleMounts.Where(kv => !example.Contains(kv.Key, StringComparison.Ordinal)).Select(kv => kv.Value))})",
              exampleMounts.Keys.All(k => example.Contains(k, StringComparison.Ordinal)));
        check("compose.example: every service has the settings as the variable, and the sync runs a copy of os2-sync it can execute",
              new[] { "cs2", "bridge", "sync" }.All(s => Service(example, s).Contains("OVERSTRIKE2_SETTINGS_CONTENT: *settings", StringComparison.Ordinal))
              && Service(example, "sync").Contains("cp /home/steam/overstrike2/os2-sync /tmp/os2-sync && chmod 755 /tmp/os2-sync && exec /tmp/os2-sync serve", StringComparison.Ordinal));
        check("compose.example: no inline config, and the bridge has its read-only root back",
              !Regex.IsMatch(example, @"^\s*configs:", RegexOptions.Multiline) && Service(example, "bridge").Contains("read_only: true", StringComparison.Ordinal));
        var inlineMatch = Regex.Match(example, @"^x-settings: &settings \|\n(.*?)^\S", RegexOptions.Multiline | RegexOptions.Singleline);
        var inline = inlineMatch.Success ? Json.Parse(inlineMatch.Groups[1].Value) as JsonObject ?? [] : [];   // plain JSON: a tool that collapses newlines must not be able to harm it
        var sample = SettingsOf(File.ReadAllText(Path.Combine(deploy, "overstrike2.example.jsonc")));
        (inline["server"] as JsonObject)?.Remove("name");
        (sample["server"] as JsonObject)?.Remove("name");
        check("compose.example: the settings are overstrike2.example.jsonc's, the server's name aside (core's self-test reads that file)", Json.Same(inline, sample));
        check("compose.yml: its settings are a file: compose makes that a read-only bind mount (buildContainerConfigMounts), so its bridge keeps its read-only root",
              Regex.IsMatch(compose, @"^  overstrike2:\n    file: ", RegexOptions.Multiline) && Service(compose, "bridge").Contains("read_only: true", StringComparison.Ordinal));
        foreach (var (name, text) in new[] { ("compose.yml", compose), ("compose.example.yml", example) })
        {
            check($"{name}: no inline settings file on a read-only service - Compose refuses it",
                  !(Regex.IsMatch(text, @"^    content: \|", RegexOptions.Multiline) && Regex.IsMatch(text, @"^    read_only: true", RegexOptions.Multiline)));
            check($"{name}: each service has the variables the settings file's env: values read (else the bridge exits at its start)",
                  new[] { ("cs2", new[] { "CS2_RCONPW", "OVERSTRIKE2_BRIDGE_SECRET" }), ("bridge", new[] { "CS2_RCONPW", "OVERSTRIKE2_BRIDGE_SECRET" }), ("sync", new[] { "OVERSTRIKE2_BRIDGE_SECRET" }) }
                      .All(sv => sv.Item2.All(v => Regex.IsMatch(Service(text, sv.Item1), $"^      {v}: ", RegexOptions.Multiline))));
        }
    }

    /// <summary>One service's block of a compose file: from its two-space key to the next one.</summary>
    static string Service(string text, string name)
    {
        var found = Regex.Match(text, $@"^  {name}:.*?(?=^  \w|^\S|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        return found.Success ? found.Value : "";
    }

    /// <summary>A settings file's values: JSON once the comments (only ever after white space here) and the trailing commas are gone.</summary>
    static JsonObject SettingsOf(string jsonc) =>
        Json.Parse(Regex.Replace(Regex.Replace(jsonc, @"(^|\s)//.*$", "", RegexOptions.Multiline), @",(\s*[}\]])", "$1")) as JsonObject ?? [];
}
