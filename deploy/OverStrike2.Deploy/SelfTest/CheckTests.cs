using System.Text.Json.Nodes;

namespace OverStrike2.Deploy;

/// <summary>deploy/test_check.py, ported: the guard's decision and what it shows, the settings and the secrets, the roll-call, the audits,
/// and verify from end to end - against stand-ins: a Docker API kept in memory, A2S, the audits' commands, git. Nothing reaches a server.</summary>
static class CheckTests
{
    const string GAME = "cs2", BRIDGE = "bridge", SYNC = "sync";
    static readonly Dictionary<string, string> N = new() { ["game"] = GAME, ["bridge"] = BRIDGE, ["sync"] = SYNC };
    const string BS = "BridgeValueOfThisTest0123";   // the bridge's secret in these tests: letters and digits, as bridge.secret holds
    const string RP = "RconValueOfThisTest4567";     // the RCON password in these tests
    const string HostDir = "/srv/overstrike2/";      // the host's folders a container mounts

    static Dictionary<string, object?> Base() => new()
    {
        ["bridge.game_container"] = GAME, ["bridge.container"] = BRIDGE, ["bridge.sync_container"] = SYNC, ["server.address"] = "192.0.2.1:27015",
        ["bridge.image"] = "localhost/overstrike2-bridge",
    };

    static Dictionary<string, object?> With(params Dictionary<string, object?>[] parts)
    {
        var d = Base();
        foreach (var p in parts)
            foreach (var (k, v) in p)
                d[k] = v;
        return d;
    }

    /// <summary>The calls a check makes on the Docker API: inspect, the container list, an exec in the game server. Any other call fails.</summary>
    sealed class Stand(Dictionary<string, JsonObject> containers, string health = "os2-health 1\nlog pushes 3\n", Dictionary<string, JsonObject>? images = null) : DockerBase
    {
        public readonly Dictionary<string, JsonObject> Containers = containers;
        public readonly List<(string Name, List<string> Argv)> Execs = [];
        readonly Dictionary<string, JsonObject> images = images ?? new()
        {
            ["localhost/overstrike2-bridge:latest"] = new JsonObject { ["Id"] = "sha256:bridge-image" },
            ["docker.io/joedwards32/cs2"] = new JsonObject { ["Id"] = "sha256:game-image" },
        };

        public override JsonObject? Inspect(string kind, string name) => kind == "images" ? images.GetValueOrDefault(name) : Containers.GetValueOrDefault(name);

        public override DockerReply Call(string method, string path, JsonNode? body = null, byte[]? raw = null, string ctype = "application/json", double timeout = 60)
        {
            if (method != "GET" || !path.StartsWith("/containers/json", StringComparison.Ordinal))
                throw new InvalidOperationException("an unexpected call: " + method + " " + path);
            var list = new JsonArray();
            foreach (var (n, c) in Containers)
                list.Add(new JsonObject
                {
                    ["Names"] = new JsonArray("/" + n), ["State"] = Json.At(c, "State", "Status")?.DeepClone(),
                    ["Status"] = Json.Truthy(Json.At(c, "State", "Running")) ? "Up" : "Exited (0)", ["Labels"] = Json.Obj(c, "Config", "Labels").DeepClone(),
                });
            return new DockerReply(200, Json: list);
        }

        public override string ExecIn(string container, IReadOnlyList<string> argv, double timeout = 60)
        {
            Execs.Add((container, [.. argv]));
            return health;
        }
    }

    static JsonObject Box(IEnumerable<string>? env = null, IEnumerable<(string Source, string Target)>? mounts = null, Dictionary<string, string>? labels = null,
                          string image = "sha256:game-image", string cimage = "docker.io/joedwards32/cs2", bool running = true) => new()
    {
        ["Image"] = image, ["RestartCount"] = 0,
        ["State"] = new JsonObject { ["Running"] = running, ["Status"] = running ? "running" : "exited", ["StartedAt"] = "start-1" },
        ["Config"] = new JsonObject
        {
            ["Env"] = new JsonArray((env ?? []).Select(e => (JsonNode?)e).ToArray()), ["Image"] = cimage,
            ["Labels"] = new JsonObject((labels ?? []).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        },
        ["Mounts"] = new JsonArray((mounts ?? []).Select(m => (JsonNode?)new JsonObject { ["Source"] = m.Source, ["Destination"] = m.Target }).ToArray()),
    };

    static JsonObject EBox(params string[] env) => new() { ["Config"] = new JsonObject { ["Env"] = new JsonArray(env.Select(e => (JsonNode?)e).ToArray()) } };

    static (object? R, string Out) OutOf(Func<int> body)
    {
        var (r, exit, output) = SelfTest.Capture(body);
        return (exit is int e ? ("exit", e) : r, output);
    }

    static readonly List<string[]> Calls = [];

    static Func<string, string[], string?> FakeGit(Dictionary<string, string?> versions) => (repo, args) =>
    {
        Calls.Add(args);
        return args[0] == "show" ? versions.GetValueOrDefault(args[1].Split(':', 2)[0]) : "";
    };

    static (object? R, string Out) GuardRun(int? on, Dictionary<string, object?>? values = null, IDocker? d = null, bool allowKick = false,
                                              Func<string, string[], string?>? git = null, Func<string, JsonNode?>? fetch = null)
    {
        var chk = new Check(new MemoryConfig(With(values ?? [])), d) { HumansOf = _ => on };
        if (fetch is not null)
            chk.Fetch = fetch;
        if (git is not null)
            chk.Git = git;
        return OutOf(() => chk.Guard(allowKick));
    }

    static readonly Dictionary<string, string> GameLabels = new() { [Check.Project] = "os2", [Check.Service] = "cs2" };

    static string Text => "{\n  \"schema\": 1,\n  \"server\": { \"rconPassword\": \"env:CS2_RCONPW\" },\n  \"bridge\": { \"secret\": \"" + BS + "\" }\n}\n";
    static readonly (string, string) Folder = (HostDir + "programs", Check.ProgramFolder);

    /// <summary>The stack on the C# programs: the settings text in each container's environment, but those in skip; the password in the
    /// game server's and the bridge's.</summary>
    static Dictionary<string, JsonObject?> After(string? text = null, string[]? gameEnv = null, string[]? bridgeEnv = null, string[]? syncEnv = null, string[]? skip = null)
    {
        text ??= Text;
        string[] Content(string role) => (skip ?? []).Contains(role) ? [] : [Check.Content + "=" + text];
        return new()
        {
            ["game"] = Box([.. Content("game"), "CS2_LOG_HTTP_URL=http://bridge:8080/log/" + BS, "CS2_RCONPW=" + RP, .. gameEnv ?? []], [Folder], GameLabels),
            ["bridge"] = Box([.. Content("bridge"), "CS2_RCONPW=" + RP, .. bridgeEnv ?? []], labels: new() { [Check.Project] = "os2", [Check.Service] = "bridge" },
                             image: "sha256:bridge-image", cimage: "localhost/overstrike2-bridge"),
            ["sync"] = Box([.. Content("sync"), .. syncEnv ?? []], [Folder], new() { [Check.Project] = "os2", [Check.Service] = "sync" }),
        };
    }

    /// <summary>The stack on the older Python programs: no settings text, the secret and the password in their variables.</summary>
    static Dictionary<string, JsonObject?> Older() => new()
    {
        ["game"] = Box(["CS2_LOG_HTTP_URL=http://bridge:8080/log/" + BS, "CS2_RCONPW=" + RP],
                       [(HostDir + "update-watch.py", "/home/steam/cs2-update-watch.py"), (HostDir + "keepset.py", "/home/steam/keepset.py")], GameLabels),
        ["bridge"] = Box(["LOG_SECRET=" + BS, "RCON_PASSWORD=" + RP], labels: new() { [Check.Project] = "os2", [Check.Service] = "bridge" }, image: "sha256:bridge-image"),
        ["sync"] = Box(["SYNC_BRIDGE_SECRET=" + BS], [(HostDir + "workshop-sync.py", "/home/steam/cs2-workshop-sync.py"), (HostDir + "workshop-sync", "/home/steam/sync-state")],
                       new() { [Check.Project] = "os2", [Check.Service] = "sync" }),
    };

    /// <summary>(what went wrong, all the words) of SettingsLines: the LOOK lines, and every line, which must never hold a value.</summary>
    static (List<string> Bad, string Words) Verdict(Dictionary<string, JsonObject?> c, Dictionary<string, string>? n = null)
    {
        var result = Check.SettingsLines(c.ToDictionary(kv => kv.Key, kv => (JsonNode?)kv.Value), n ?? N);
        return (result.Where(x => x.Ok == false).Select(x => x.Line).ToList(), string.Join(" | ", result.Select(x => x.Line)));
    }

    static JsonArray EnvOf(JsonObject? c) => (JsonArray)c!["Config"]!["Env"]!;

    static Dictionary<string, JsonObject?> Grouped(Dictionary<string, JsonObject?> c)
    {
        EnvOf(c["game"]).Add("CS2_MAPGROUP=" + Check.MapGroup);
        return c;
    }

    static string scratch = "";
    static readonly Check.AuditResult AuditOk = new(0, "audit: OK -- 3 pins\n", "");
    static readonly Check.AuditResult AuditElsewhere = new(1, "", "audit: 2 drift item(s):\n  DRIFT other: live MAC\n  DRIFT cs2-other: live IP\n");

    static Dictionary<string, object?> Full() => new()
    {
        ["check.test_label"] = "os2.test-copy", ["check.aside"] = new List<object?> { "cs2-aside", "bridge-aside" }, ["stack.repo"] = scratch,
        ["stack.file"] = "stack/compose.yml",
        ["check.audit"] = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["name"] = "the audit", ["argv"] = new List<object?> { "python", "-I", "audit.py" },
                ["env"] = new Dictionary<string, object?> { ["AUDIT_KEY"] = "env:OS2_TEST_AUDIT" }, ["done"] = @"^audit: (OK|\d+ drift)", ["look"] = "DRIFT (cs2|bridge|sync):",
            },
        },
    };

    /// <summary>verify's exit code, every word it printed, the stand-in Docker, and the audit's runs.</summary>
    static (object? Code, string Out, Stand Stand, List<(List<string> Argv, Dictionary<string, string?> Env)> Runs) RunVerify(
        Dictionary<string, JsonObject?> c, Dictionary<string, object?>? values = null, string health = "os2-health 1\nlog pushes 3\n", Check.AuditResult? audit = null,
        (int, int)? a2s = null, bool noA2s = false, Dictionary<string, JsonObject>? extra = null, bool auditEnv = true)
    {
        var containers = new Dictionary<string, JsonObject> { [GAME] = c["game"]!, [BRIDGE] = c["bridge"]! };
        if (c.GetValueOrDefault("sync") is { } s)
            containers[SYNC] = s;
        foreach (var (k, v) in extra ?? [])
            containers[k] = v;
        var stand = new Stand(containers, health);
        var runs = new List<(List<string>, Dictionary<string, string?>)>();
        var answer = noA2s ? ((int, int)?)null : a2s ?? (0, 1);
        var chk = new Check(new MemoryConfig(With(values ?? Full())), stand)
        {
            A2sInfo = _ => answer,
            Run = (argv, env) =>
            {
                runs.Add((argv, env));
                return audit ?? AuditOk;
            },
        };
        if (auditEnv)
            Environment.SetEnvironmentVariable("OS2_TEST_AUDIT", "the-audit-value");
        try
        {
            var (code, output) = OutOf(chk.Verify);
            return (code, output, stand, runs);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OS2_TEST_AUDIT", null);
        }
    }

    public static void Run()
    {
        var check = SelfTest.Check;

        // ---- the guard's decision
        check("guard: nobody on - GO", Check.Decide(0, false) == (0, "GO: nobody is on"));
        check("guard: somebody on - NO, exit 3", Check.Decide(2, false, GAME).Code == 3 && Check.Decide(2, false, GAME).Line.StartsWith("NO: a redeploy would restart cs2 with 2", StringComparison.Ordinal));
        check("guard: no A2S answer is not a go", Check.Decide(null, false).Code == 3 && Check.Decide(null, false).Line.Contains("an unknown number of"));
        check("guard: --allow-kick goes with players on, and says so", Check.Decide(3, true, GAME) == (0, "GO (--allow-kick): cs2 restarts with 3 player(s) on - the owner said so"));

        // ---- the guard from end to end
        var savedKey = Environment.GetEnvironmentVariable("OS2_TEST_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OS2_TEST_KEY", null);
            var nokey = new Dictionary<string, object?>
            {
                ["docker.api"] = "https://docker.example.com:2376", ["docker.headers"] = new Dictionary<string, object?> { ["X-API-Key"] = "env:OS2_TEST_KEY" },
            };
            var (r, output) = GuardRun(0, nokey);
            check("guard without the Docker API key: still decides by A2S - GO with nobody on, the drift not shown, the variable named",
                  r is 0 && output.Contains("GO: nobody is on") && output.Contains("docker.headers.X-API-Key reads OS2_TEST_KEY, which is not set: the drift is not shown"));
            (r, output) = GuardRun(2, nokey);
            check("guard without the key and players on: NO, exit 3", r is 3 && output.Contains("NO: a redeploy would restart cs2 with 2 player(s) on"));
            (r, output) = GuardRun(null, nokey);
            check("guard: no A2S answer - NO, exit 3, said as unknown", r is 3 && output.Contains("humans on the server: unknown (no A2S answer)"));
            (r, output) = GuardRun(0, new(nokey) { ["server.address"] = "" });
            check("guard: no server.address in the config - exit 2, never a go", Equals(r, ("exit", 2)) && !output.Contains("GO"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OS2_TEST_KEY", savedKey);
        }

        const string composeOld = "x-settings: &settings |\n  {}\nservices:\n  cs2:\n    image: game\n    environment:\n      OVERSTRIKE2_SETTINGS_CONTENT: *settings\n  bridge:\n    image: b1\n";
        var composeBridge = composeOld.Replace("image: b1", "image: b2", StringComparison.Ordinal);       // only the bridge's service changed
        var composeAnchor = composeOld.Replace("  {}\n", "  { \"schema\": 1 }\n", StringComparison.Ordinal);   // the settings anchor, which the game server takes
        var stack = new Dictionary<string, object?>
        {
            ["stack.repo"] = "the-repo", ["stack.file"] = "stack/compose.yml", ["stack.ref"] = "origin/main",
            ["stack.deployed_url"] = "https://manager.example.com/stacks/7", ["stack.deployed_field"] = "Git.Commit",
        };
        var d = new Stand(new() { [GAME] = Box(labels: GameLabels) });
        JsonNode? Deployed(string url) => Json.O("""{"Git": {"Commit": "1111111aaaa"}}""");
        {
            var (r, output) = GuardRun(0, stack, d, git: FakeGit(new() { ["1111111aaaa"] = composeOld, ["origin/main"] = composeOld }), fetch: Deployed);
            check("guard, the drift: the image, and the game server's service unchanged since the commit deployed - after a fetch of the ref's remote",
                  r is 0 && output.Contains("cs2 image: container game-image, tag docker.io/joedwards32/cs2 -> game-image")
                  && output.Contains("the cs2 service in stack/compose.yml: deployed 1111111 vs origin/main: unchanged") && Calls.Any(c => c.SequenceEqual(["fetch", "-q", "origin"])));
            Calls.Clear();
            (r, output) = GuardRun(0, stack, d, git: FakeGit(new() { ["1111111aaaa"] = composeOld, ["origin/main"] = composeAnchor }), fetch: Deployed);
            check("guard, the drift: a change in the settings anchor the game server takes is a change of its service", output.Contains("vs origin/main: changed"));
            (r, output) = GuardRun(0, stack, new Stand(new() { [GAME] = Box(labels: GameLabels, image: "sha256:older-image") }),
                                   git: FakeGit(new() { ["1111111aaaa"] = composeOld, ["origin/main"] = composeOld }), fetch: Deployed);
            check("guard, the drift: a container behind its tag - the redeploy also moves it to the newer image", output.Contains("(the redeploy also moves it to the newer image)"));
            (r, output) = GuardRun(0, stack.Where(kv => !kv.Key.StartsWith("stack.deployed", StringComparison.Ordinal)).ToDictionary(), d);
            check("guard, the drift: without stack.deployed_url it says the commit deployed is not known", r is 0 && output.Contains("the commit last deployed is not known"));
            (r, output) = GuardRun(1, stack, d, fetch: _ => throw new IOException("the manager did not answer"));
            check("guard: the drift's failure is only information - the image line stays, the decision is made (NO with 1 on)",
                  r is 3 && output.Contains("cs2 image: container") && output.Contains("could not read the drift: IOException"));
        }
        check("service_changed: another service's change is not the game server's", Check.ServiceChanged(composeOld, composeBridge, "cs2") == (false, true));
        check("service_changed: a version that could not be read counts as changed", Check.ServiceChanged(null, composeOld, "cs2") == (true, false));
        Check.YamlReader = false;   // as without a YAML reader
        try
        {
            check("service_changed without PyYAML: by the whole file", Check.ServiceChanged(composeOld, composeBridge, "cs2") == (true, false)
                  && Check.ServiceChanged(composeOld, composeOld, "cs2") == (false, false));
        }
        finally
        {
            Check.YamlReader = true;
        }

        // ---- the settings text and the secrets
        check("jsonc: comments and trailing commas go, a // or a comma inside a string stays",
              Json.Same(Json.Jsonc("// top\n{ \"a\": \"http://x,}\", /* b */ \"c\": [1, 2,], \"d\": {\"e\": 1,}, } // end\n"), Json.O("""{"a": "http://x,}", "c": [1, 2], "d": {"e": 1}}""")));
        var words0 = "";
        try
        {
            Json.Jsonc("{ \"x\": \"abc\" oops }");
        }
        catch (JsonSyntaxError e)
        {
            words0 = e.Message;
        }
        check("jsonc: a syntax error's words quote nothing of the text", words0.Length > 0 && !words0.Contains("abc") && !words0.Contains("oops"));

        string s1 = new('a', 32), s2 = new('b', 32);
        var places = Check.SecretPlaces(new() { ["bridge"] = EBox("LOG_SECRET=" + s1, "RCON_PASSWORD=x"), ["game"] = EBox("CS2_LOG_HTTP_URL=http://bridge:8080/log/" + s1),
                                               ["sync"] = EBox("SYNC_BRIDGE_SECRET=" + s1) }, N);
        var (ok, line) = Check.SecretsAgree(places, BRIDGE);
        check("secret: the same in the bridge, the game server's log URL and the sync - one secret, three places named, no value printed", ok && line.Contains("3 places") && !line.Contains(s1));
        places = Check.SecretPlaces(new() { ["bridge"] = EBox("LOG_SECRET=" + s2), ["game"] = EBox(@"CS2_LOG_HTTP_URL=http:\/\/bridge:8080\/log\/" + s2, "OVERSTRIKE2_BRIDGE_SECRET=" + s2),
                                           ["sync"] = EBox("OVERSTRIKE2_BRIDGE_SECRET=" + s1) }, N);
        (ok, line) = Check.SecretsAgree(places, BRIDGE);
        check("secret: a rotation that missed the sync names that place alone, and no value",
              !ok && line.Contains("sync OVERSTRIKE2_BRIDGE_SECRET") && !line.Contains("cs2 CS2_LOG_HTTP_URL") && !line.Contains(s1) && !line.Contains(s2));
        (ok, line) = Check.SecretsAgree(Check.SecretPlaces(new() { ["bridge"] = EBox("LOG_SECRET=" + s1), ["game"] = EBox(), ["sync"] = null }, N), BRIDGE);
        check("secret: no CS2_LOG_HTTP_URL in the game server is not agreement", !ok && line.Contains("cs2 CS2_LOG_HTTP_URL"));

        (ok, line) = Check.MapGroupOk(EBox("CS2_MAPGROUP=mg_ar_shoots"), name: GAME);
        check("mapgroup: mg_ar_shoots, the one-map group - ok", ok && line.Contains("mg_ar_shoots"));
        (ok, line) = Check.MapGroupOk(EBox("CS2_MAPGROUP=mg_armsrace"), name: GAME);
        check("mapgroup: mg_armsrace (four maps, CS2's VOTING tab) - look, and it says why", !ok && line.Contains("'mg_armsrace'") && line.Contains("VOTING"));
        check("mapgroup: none set (the image's default) - look", !Check.MapGroupOk(EBox()).Ok);
        check("mapgroup: another group the config asks for", Check.MapGroupOk(EBox("CS2_MAPGROUP=mg_custom"), "mg_custom").Ok);

        var (bad, words) = Verdict(Older());
        check("the older Python programs: it says they run, naming what is mounted; the variables agree - nothing to look at",
              bad.Count == 0 && words.Contains("the older Python programs run: no container holds the settings text") && words.Contains("cs2 mounts keepset.py, update-watch.py")
              && words.Contains("sync mounts workshop-sync.py") && words.Contains("one bridge secret in all 3 places") && !words.Contains(BS));
        var c = Older();
        c["sync"]!["Config"]!["Env"] = new JsonArray("SYNC_BRIDGE_SECRET=other");
        (bad, words) = Verdict(c);
        check("the older programs: a variable with another secret - look, the place named, no value", bad.Count == 1 && bad[0].Contains("sync SYNC_BRIDGE_SECRET") && !words.Contains("other"));
        c = Older();
        c["sync"]!["Mounts"] = new JsonArray();
        (bad, words) = Verdict(c);
        check("no settings text and no older programs mounted: it does not claim they run", bad.Any(b => b.Contains("neither way of running") && b.Contains("sync")) && !words.Contains(" run:"));

        (bad, words) = Verdict(After());
        check("the C# programs: one text in all three, one secret, the password compared, no older program, the program folder - nothing to look at, no value printed",
              bad.Count == 0 && words.Contains("identical in cs2, bridge, sync") && words.Contains("one bridge secret in all 4 places")
              && words.Contains("server.rconPassword as cs2 and bridge read it is cs2's CS2_RCONPW") && words.Contains("no container mounts one of the older Python programs")
              && words.Contains("cs2 and sync mount the program folder") && !words.Contains(BS) && !words.Contains(RP));
        c = After();
        EnvOf(c["sync"])[0] = Check.Content + "=" + Text.Replace("\"schema\": 1", "\"schema\": 1, \"sync\": {}", StringComparison.Ordinal);
        (bad, words) = Verdict(c);
        check("a text that differs in one container - look", bad.Any(b => b.Contains("differs between")));
        (bad, words) = Verdict(After(Text.Replace(BS, "AnotherValueAnotherValue00", StringComparison.Ordinal)));
        check("a rotation that missed CS2_LOG_HTTP_URL - look, naming that place, printing neither value",
              bad.Any(b => b.Contains("cs2 CS2_LOG_HTTP_URL")) && !words.Contains(BS) && !words.Contains("AnotherValue"));
        (bad, words) = Verdict(After(bridgeEnv: ["LOG_SECRET=stale"]));
        check("a variable left behind with another secret - look, naming it", bad.Any(b => b.Contains("bridge LOG_SECRET")) && !words.Contains("stale"));
        (bad, words) = Verdict(After(bridgeEnv: ["LOG_SECRET=" + BS]));
        check("a variable left behind with the same secret counts as one more place, and agrees", bad.Count == 0 && words.Contains("5 places"));
        c = After();
        EnvOf(c["game"])[2] = "CS2_RCONPW=changed";
        (bad, words) = Verdict(c);
        check("the password the bridge reads is not the game server's CS2_RCONPW - look, naming the bridge, no value",
              bad.Any(b => b.Contains("not cs2's CS2_RCONPW in: bridge")) && !words.Contains("changed") && !words.Contains(RP));
        c = After();
        c["bridge"]!["Config"]!["Env"] = new JsonArray(EnvOf(c["bridge"])[0]!.DeepClone());
        (bad, words) = Verdict(c);
        check("a bridge with no CS2_RCONPW reads no password - look", bad.Any(b => b.Contains("not cs2's CS2_RCONPW in: bridge")));
        (bad, words) = Verdict(After(Text.Replace("env:CS2_RCONPW", RP, StringComparison.Ordinal)));
        check("a literal password that is the game server's would do", bad.Count == 0 && !words.Contains(RP));
        c = After();
        ((JsonArray)c["game"]!["Mounts"]!).Add(new JsonObject { ["Source"] = HostDir + "update-watch.py", ["Destination"] = "/home/steam/cs2-update-watch.py" });
        (bad, words) = Verdict(c);
        check("an older program still mounted - look", bad.Any(b => b.Contains("still mounted: cs2: update-watch.py")));
        c = After();
        c["sync"]!["Mounts"] = new JsonArray();
        (bad, words) = Verdict(c);
        check("no program folder in the sync - look", bad.Any(b => b.Contains("no program folder") && b.Contains("sync")));
        var reference = Text.Replace(BS, "env:OVERSTRIKE2_BRIDGE_SECRET", StringComparison.Ordinal);
        string[] every = ["OVERSTRIKE2_BRIDGE_SECRET=" + BS];
        (bad, words) = Verdict(After(reference, every, every, every));
        check("references: env: is read in each container itself", bad.Count == 0 && !words.Contains(BS));
        (bad, words) = Verdict(After(reference, gameEnv: every, syncEnv: every));
        check("references: a container without the variable reads it as empty - look, naming that container", bad.Any(b => b.Contains("bridge settings")));
        (bad, words) = Verdict(After(Text.Replace(BS, "file:/run/secrets/bridge", StringComparison.Ordinal)));
        check("references: file: is inside the container, where this does not look - it says so by naming the places with no value",
              bad.Any(b => b.Contains("cs2 settings") && b.Contains("sync settings")) && !words.Contains(BS));
        (bad, words) = Verdict(After(skip: ["game", "sync"]));
        check("mixed: only the bridge holds the text - look, naming who has none, and the secret places it took with it",
              bad.Any(b => b.Contains("only bridge hold") && b.Contains("cs2, sync do not")) && bad.Any(b => b.Contains("cs2 settings")) && !words.Contains(" run:") && !words.Contains(BS));
        c = After();
        EnvOf(c["bridge"])[0] = Check.Content + "={ not json";
        (bad, words) = Verdict(c);
        check("a text that does not parse - look, naming it", bad.Any(b => b.Contains("bridge's settings text does not parse")));
        c = After();
        (bad, words) = Verdict(new() { ["game"] = c["game"], ["bridge"] = c["bridge"], ["sync"] = null }, new(N) { ["sync"] = "" });
        check("a stack without the sync: two places hold the text, and only the game server needs the program folder",
              bad.Count == 0 && words.Contains("identical in cs2, bridge") && words.Contains("cs2 mount the program folder"));

        // ---- verify from end to end
        scratch = SelfTest.Scratch("test-check-");
        try
        {
            Directory.CreateDirectory(Path.Combine(scratch, "stack"));
            File.WriteAllText(Path.Combine(scratch, "stack", "compose.yml"), "services:\n  cs2:\n    container_name: cs2\n  bridge:\n    container_name: bridge\n  sync:\n    container_name: sync\n"
                                                                             + "#  old:\n#    container_name: old-one\n");
            var (code, output, stand, runs) = RunVerify(Grouped(After()));
            check("verify, the C# programs: ALL GOOD - the image, the project, production, the map group, VAC, the health, the settings, the roll-call, the audit",
                  code is 0 && output.Contains("RESULT: ALL GOOD") && !output.Contains("LOOK") && output.Contains("bridge belongs to compose project 'os2', service 'bridge'")
                  && output.Contains("production: no os2.test-copy label on cs2, bridge, sync") && output.Contains("nothing set aside: no cs2-aside, bridge-aside")
                  && output.Contains("roll-call, stack/compose.yml: cs2, bridge, sync declared and running") && !output.Contains("old-one") && output.Contains("ok    the audit: no line matches"));
            check("verify: the bridge's health with the secret and port of the game server's CS2_LOG_HTTP_URL, printed nowhere",
                  stand.Execs[0].Name == GAME && stand.Execs[0].Argv[^1] == $"http://bridge:8080/health/{BS}" && !output.Contains(BS) && !output.Contains(RP));
            check("verify: the audit runs with this Python and the variable it needs, whose value is printed nowhere",
                  runs[0].Argv[0] == "python" && runs[0].Argv.Skip(1).SequenceEqual(["-I", "audit.py"]) && runs[0].Env["AUDIT_KEY"] == "the-audit-value" && !output.Contains("the-audit-value"));
            (code, output, _, _) = RunVerify(Grouped(Older()));
            check("verify, the older Python programs: ALL GOOD, a note that they run", code is 0 && output.Contains("note  the older Python programs run"));
            (code, output, _, _) = RunVerify(After());
            check("verify: another map group is a look", code is 1 && output.Contains("LOOK  cs2's CS2_MAPGROUP"));
            (code, output, _, _) = RunVerify(Grouped(After(skip: ["game", "sync"])));
            check("verify: two containers without the text - exit 1, and it says so", code is 1 && output.Contains("only bridge hold") && !output.Contains("RESULT: ALL GOOD"));
            c = Grouped(After());
            c["game"]!["Config"]!["Labels"]!["os2.test-copy"] = "a test copy";
            (code, output, _, _) = RunVerify(c, a2s: (0, 0));
            check("verify: a test copy - its label and the insecure server are both a look", code is 1 && output.Contains("a TEST COPY, not production: cs2 (a test copy)")
                  && output.Contains("A2S says VAC-secured: NO"));
            (code, output, _, _) = RunVerify(Grouped(After()), extra: new() { ["cs2-aside"] = Box() });
            check("verify: production set aside for a test - look", code is 1 && output.Contains("set aside for a test: cs2-aside (running)"));
            c = Grouped(After());
            c["bridge"]!["Image"] = "sha256:older-bridge";
            (code, output, _, _) = RunVerify(c);
            check("verify: a bridge not on <image>:latest - look", code is 1 && output.Contains("LOOK  bridge runs older-bridge"));
            c = Grouped(After());
            c["bridge"]!["Config"]!["Labels"] = new JsonObject();
            (code, output, _, _) = RunVerify(c);
            check("verify: a bridge outside the game server's compose project - look", code is 1 && output.Contains("LOOK  bridge belongs to compose project None"));
            (code, output, _, _) = RunVerify(Grouped(After()), health: "os2-health 1\nlog pushes 0\n");
            check("verify: no log push reaching the bridge - look", code is 1 && output.Contains("LOOK  log pushes reaching the bridge: 0"));
            (code, output, _, _) = RunVerify(Grouped(After()), health: "gg-health 1\nlog pushes 3\n");
            check("verify: a bridge from before release 191 (gg-health, after a rollback) - its health is as good as any other's",
                  !output.Contains("LOOK  bridge /health") && output.Contains("ok    bridge /health"));
            (code, output, _, _) = RunVerify(Grouped(After()), health: "Traceback: refused at /health/" + BS);
            check("verify: a failed health request - look, and the secret in its words is not printed", code is 1 && output.Contains("LOOK  bridge /health") && !output.Contains(BS));
            (code, output, _, _) = RunVerify(Grouped(After()), noA2s: true);
            check("verify: no A2S answer - look", code is 1 && output.Contains("the game server answers A2S: no answer"));
            c = Grouped(After());
            c["sync"]!["State"] = Json.O("""{"Running": false, "Status": "exited", "StartedAt": "start-1"}""");
            (code, output, _, _) = RunVerify(c);
            check("verify: the sync stopped - look, by its state and in the roll-call", code is 1 && output.Contains("LOOK  sync state exited") && output.Contains("not running: sync (Exited (0))"));
            (code, output, _, _) = RunVerify(Grouped(After()), extra: new() { ["strayed"] = Box(labels: new() { [Check.Project] = "os2" }) });
            check("verify: a container of the project that the compose file does not declare - a note",
                  code is 0 && output.Contains("note  in compose project 'os2' but declared nowhere in stack/compose.yml: strayed"));
            (code, output, _, _) = RunVerify(Grouped(After()), audit: AuditElsewhere);
            check("verify: the audit's drift elsewhere is a note, not a look", code is 0 && output.Contains("note  the audit: exit 1; 3 other line(s); audit: 2 drift item(s):"));
            (code, output, _, _) = RunVerify(Grouped(After()), audit: new Check.AuditResult(1, "", "audit: 1 drift item(s):\n  DRIFT bridge: live MAC x\n"));
            check("verify: the audit names this stack - look, with its line", code is 1 && output.Contains("LOOK  the audit: DRIFT bridge: live MAC x"));
            (code, output, _, _) = RunVerify(Grouped(After()), audit: new Check.AuditResult(1, "", "Traceback (most recent call last):\nKeyError: 'x'\n"));
            check("verify: an audit that did not finish is a look, not a quiet ok", code is 1 && output.Contains("LOOK  the audit: did not finish (exit 1): KeyError: 'x'"));
            var plain = Full();
            plain["check.audit"] = new List<object?> { new Dictionary<string, object?> { ["name"] = "plain", ["argv"] = new List<object?> { "audit" } } };
            (code, output, _, _) = RunVerify(Grouped(After()), plain, audit: new Check.AuditResult(2, "", "nope\n"));
            check("verify: an audit without look - its exit code decides", code is 1 && output.Contains("LOOK  plain: exit 2; nope"));
            (code, output, _, runs) = RunVerify(Grouped(After()), auditEnv: false);
            check("verify: an audit whose variable is not set is not run - look, naming the variable",
                  code is 1 && output.Contains("LOOK  the audit: not run - AUDIT_KEY (OS2_TEST_AUDIT is not set)") && runs.Count == 0);
            (code, output, _, _) = RunVerify(Grouped(After()), new() { ["stack.repo"] = scratch, ["stack.file"] = "stack/missing.yml" });
            check("verify: a compose file that cannot be read - look", code is 1 && output.Contains("LOOK  roll-call: stack/missing.yml cannot be read"));
            (code, output, _, _) = RunVerify(Grouped(After()), new() { ["bridge.container"] = "renamed", ["stack.repo"] = scratch, ["stack.file"] = "stack/compose.yml" });
            check("verify: a container the config names that the compose file does not declare - look", code is 1
                  && output.Contains("not declared there, though the private config names it: renamed"));
            (code, output, _, _) = RunVerify(Grouped(After()), []);
            check("verify with only the required keys: the optional checks are named as not done, and nothing else is a look",
                  code is 0 && output.Contains("no check.test_label or check.aside") && output.Contains("no stack.repo and stack.file") && output.Contains("no [[check.audit]]"));
            var savedKey2 = Environment.GetEnvironmentVariable("OS2_TEST_KEY");
            try
            {
                Environment.SetEnvironmentVariable("OS2_TEST_KEY", null);
                var chk = new Check(new MemoryConfig(With(new Dictionary<string, object?> { ["docker.api"] = "https://docker.example.com:2376",
                                                                   ["docker.headers"] = new Dictionary<string, object?> { ["X-API-Key"] = "env:OS2_TEST_KEY" } })));
                (code, output) = OutOf(chk.Verify);
                check("verify without the Docker API key: exit 1, nothing checked, the variable named", code is 1 && output.Contains("LOOK  docker.headers.X-API-Key reads OS2_TEST_KEY")
                      && output.Contains("nothing checked"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("OS2_TEST_KEY", savedKey2);
            }
        }
        finally
        {
            SelfTest.Remove(scratch);
        }
    }
}
