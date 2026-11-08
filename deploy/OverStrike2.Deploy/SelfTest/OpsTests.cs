using System.Text;
using System.Text.Json.Nodes;

namespace OverStrike2.Deploy;

/// <summary>The verbs a release did by hand before, against stand-ins: bridge prune (which tags go and which stay, a dry run first, never without
/// the images a rollback needs), server restart-sync (only production's sync, only between runs, and seen through to the program it starts), and
/// probe exec and logs on production's containers (the right container, the command as given, every secret of it scrubbed from what is printed).</summary>
static class OpsTests
{
    const string Repo = "localhost/overstrike2-bridge";

    /// <summary>The Docker Engine API, in memory: images and containers as the list endpoints give them, every call written down, DELETE and restart
    /// answering as told, and an exec's answer by its command.</summary>
    sealed class FakeDocker : DockerBase
    {
        public readonly List<(string Method, string Path)> Calls = [];
        public JsonArray Images = [], Containers = [];
        public readonly Dictionary<string, JsonObject> Inspects = new(StringComparer.Ordinal);
        public Func<string, string> ExecAnswer = _ => "";
        public int DeleteStatus = 200;
        readonly Dictionary<string, string> execCmds = new(StringComparer.Ordinal);

        public override DockerReply Call(string method, string path, JsonNode? body = null, byte[]? raw = null, string ctype = "application/json", double timeout = 60)
        {
            Calls.Add((method, path));
            var route = path.Split('?')[0];
            if (method == "GET" && route == "/images/json")
                return new DockerReply(200, Images.DeepClone());
            if (method == "GET" && route == "/containers/json")
                return new DockerReply(200, Containers.DeepClone());
            if (method == "GET" && route.StartsWith("/containers/", StringComparison.Ordinal) && route.EndsWith("/json", StringComparison.Ordinal))
                return Inspects.TryGetValue(route.Split('/')[2], out var c) ? new DockerReply(200, c.DeepClone()) : new DockerReply(404, Error: "no such container");
            if (method == "DELETE" && route.StartsWith("/images/", StringComparison.Ordinal))
                return new DockerReply(DeleteStatus, DeleteStatus == 200 ? new JsonArray(new JsonObject { ["Untagged"] = route[8..] }) : null,
                                       Error: DeleteStatus == 200 ? null : "conflict");
            if (method == "POST" && route.EndsWith("/restart", StringComparison.Ordinal))
            {
                var c = Inspects[route.Split('/')[2]];
                c["State"]!["StartedAt"] = "start-after";
                return new DockerReply(204);
            }
            if (method == "POST" && route.EndsWith("/exec", StringComparison.Ordinal))
            {
                var id = $"e{execCmds.Count}";
                execCmds[id] = Json.PyStr(Json.Arr(body, "Cmd")[^1]);
                return new DockerReply(201, new JsonObject { ["Id"] = id });
            }
            if (method == "POST" && route.StartsWith("/exec/", StringComparison.Ordinal))
            {
                var text = Encoding.UTF8.GetBytes(ExecAnswer(execCmds[route.Split('/')[2]]));
                return new DockerReply(200, Bytes: [1, 0, 0, 0, (byte)(text.Length >> 24), (byte)(text.Length >> 16), (byte)(text.Length >> 8), (byte)text.Length, .. text]);
            }
            throw new InvalidOperationException($"unexpected call {method} {path}");
        }
    }

    static JsonObject Image(string id, long created, params string[] tags) =>
        new() { ["Id"] = id, ["Created"] = created, ["RepoTags"] = new JsonArray(tags.Select(t => (JsonNode?)$"{Repo}:{t}").ToArray()) };

    public static void Run()
    {
        Prune();
        RestartSync();
        ExecAndLogs();
    }

    static void Prune()
    {
        var check = SelfTest.Check;
        FakeDocker World()
        {
            var d = new FakeDocker
            {
                Images =
                [
                    Image("sha256:aaaaaaaaaaaaaaaa", 300, "latest", "candidate", "177aa545ca23", "lab-177aa545ca23"),
                    Image("sha256:bbbbbbbbbbbbbbbb", 200, "previous", "d40ab864e53b"),
                    Image("sha256:cccccccccccccccc", 100, "0dee310dbe7f", "lab-0dee310dbe7f"),
                    Image("sha256:dddddddddddddddd", 50, "rehearsal"),
                ],
                Containers =
                [
                    new JsonObject { ["Names"] = new JsonArray("/bridge"), ["ImageID"] = "sha256:aaaaaaaaaaaaaaaa" },
                    new JsonObject { ["Names"] = new JsonArray("/a-lab-thing"), ["ImageID"] = "sha256:dddddddddddddddd" },
                ],
            };
            return d;
        }
        var b = World();
        var (code, _, output) = SelfTest.Capture(() => new Bridge(b, Repo, "bridge", "game").Prune(apply: false));
        check("prune, a dry run: nothing removed - the old build's tags and a lab tag on the running image named as what would go",
              code == 0 && !b.Calls.Any(c => c.Method == "DELETE") && output.Contains("would remove 3 tag(s):", StringComparison.Ordinal)
              && output.Contains("cccccccccccc :0dee310dbe7f", StringComparison.Ordinal) && output.Contains("cccccccccccc :lab-0dee310dbe7f", StringComparison.Ordinal)
              && output.Contains("aaaaaaaaaaaa :lab-177aa545ca23", StringComparison.Ordinal) && output.Contains("dry run - add --apply", StringComparison.Ordinal));
        check("prune keeps the images :latest, :previous and :candidate point at, with their commit tags, and any image a container uses",
              output.Contains("aaaaaaaaaaaa (:latest, :candidate): keeps 177aa545ca23, candidate, latest; untags lab-177aa545ca23", StringComparison.Ordinal)
              && output.Contains("bbbbbbbbbbbb (:previous): keeps d40ab864e53b, previous", StringComparison.Ordinal)
              && output.Contains("dddddddddddd (used by a container): keeps rehearsal", StringComparison.Ordinal));
        b = World();
        (code, _, output) = SelfTest.Capture(() => new Bridge(b, Repo, "bridge", "game").Prune(apply: true));
        check("prune --apply: exactly those three tags deleted, by name, nothing forced",
              code == 0 && b.Calls.Where(c => c.Method == "DELETE").Select(c => c.Path).Order(StringComparer.Ordinal)
                  .SequenceEqual([$"/images/{Repo}:0dee310dbe7f", $"/images/{Repo}:lab-0dee310dbe7f", $"/images/{Repo}:lab-177aa545ca23"])
              && !b.Calls.Any(c => c.Path.Contains("force", StringComparison.Ordinal)) && output.Contains("removed 3 tag(s):", StringComparison.Ordinal));
        b = World();
        b.DeleteStatus = 409;
        (code, _, output) = SelfTest.Capture(() => new Bridge(b, Repo, "bridge", "game").Prune(apply: true));
        check("prune --apply: a removal the server refuses is said, and prune ends 1", code == 1 && output.Contains("FAILED 3:", StringComparison.Ordinal));
        b = World();
        b.Images.RemoveAt(1);
        var refused = SelfTest.Refusal(() => new Bridge(b, Repo, "bridge", "game").Prune(apply: true));
        check("prune without a :previous is refused, and nothing goes: never without the image a rollback needs",
              refused is not null && refused.Contains("no :previous", StringComparison.Ordinal) && !b.Calls.Any(c => c.Method == "DELETE"));
    }

    static void RestartSync()
    {
        var check = SelfTest.Check;
        var Sum = new string('a', 64);
        var cfg = new MemoryConfig(new Dictionary<string, object?> { ["bridge.sync_container"] = "sync" });
        FakeDocker World(string status, JsonObject? labels = null, string running = "running", string? program = null)
        {
            var d = new FakeDocker();
            d.Inspects["sync"] = new JsonObject
            {
                ["Id"] = "sync-id", ["State"] = new JsonObject { ["Status"] = running, ["StartedAt"] = "start-before" },
                ["Config"] = new JsonObject { ["Labels"] = labels ?? new JsonObject(), ["Env"] = new JsonArray() },
            };
            d.ExecAnswer = cmd => cmd.StartsWith("cat /data/sync-status.json", StringComparison.Ordinal) ? status
                                  : cmd.StartsWith("sha256sum", StringComparison.Ordinal) ? $"{Sum}  /tmp/os2-sync\n{program ?? Sum}  /home/steam/overstrike2/os2-sync\n" : "";
            return d;
        }
        const string Idle = """{"schema": 1, "now": {"state": "idle", "why": "The run that started at 02:11 has ended.", "map": null, "step": null, "run": null}}""";
        var d = World(Idle);
        var (code, _, output) = SelfTest.Capture(() => ServerShip.RestartSync(d, cfg, _ => { }));
        check("restart-sync between runs: one restart, a new start, and /tmp/os2-sync the program folder's byte for byte",
              code == 0 && d.Calls.Count(c => c.Method == "POST" && c.Path.StartsWith("/containers/sync/restart", StringComparison.Ordinal)) == 1
              && output.Contains("restarted - started start-afterZ, was start-beforeZ", StringComparison.Ordinal) && output.Contains("is the program folder's os2-sync, sha256 aaaaaaaaaaaaaaaa", StringComparison.Ordinal));
        var refusals = new[]
        {
            ("a run going", World("""{"now": {"state": "downloads", "run": {"started": 1}}}"""), "a run is going (downloads)"),
            ("waiting inside a run", World("""{"now": {"state": "waiting", "run": {"started": 1}}}"""), "a run is going (waiting)"),
            ("no status to read", World("cat: /data/sync-status.json: No such file or directory"), "cannot be told"),
            ("the rehearsal's sync", World(Idle, new JsonObject { [TestCopy.TestLabel] = "the release rehearsal" }), "carries the test label"),
            ("a stopped sync", World(Idle, running: "exited"), "a restart is for a running sync"),
        };
        var allRefused = true;
        foreach (var (why, w, words) in refusals)
        {
            var r = SelfTest.Refusal(() => ServerShip.RestartSync(w, cfg, _ => { }));
            allRefused &= r is not null && r.Contains(words, StringComparison.Ordinal) && !w.Calls.Any(c => c.Path.Contains("/restart", StringComparison.Ordinal));
        }
        check("restart-sync refuses, and restarts nothing: a run going or waiting, no status to read, the rehearsal's sync, a stopped one", allRefused);
        var noSync = SelfTest.Refusal(() => ServerShip.RestartSync(World(Idle), new MemoryConfig([]), _ => { }));
        check("restart-sync with no bridge.sync_container in the config: refused", noSync is not null && noSync.Contains("no bridge.sync_container", StringComparison.Ordinal));
        d = World(Idle, program: new string('9', 64));
        var waits = 0;
        var (_, exit, said) = SelfTest.Capture(() => ServerShip.RestartSync(d, cfg, _ => waits++));
        check("restart-sync whose /tmp/os2-sync stays another file: it looks 10 times, 3 s apart, then fails and says where to look",
              exit == 1 && said.Contains("FAILED: /tmp/os2-sync is not the program folder's os2-sync", StringComparison.Ordinal) && waits == 10);
    }

    static void ExecAndLogs()
    {
        var check = SelfTest.Check;
        var (tmp, config, oldState) = SimConfig.Make("ops-sim-");
        try
        {
            File.WriteAllText(config, File.ReadAllText(config).Replace("game_container = \"cs2\"\n", "game_container = \"cs2\"\nsync_container = \"sync\"\n", StringComparison.Ordinal));
            const string Rcon = "rcon-pass-1234", Bridge = "bridge-secret-99", ApiKey = "api-key-5678", Mail = "mail-pass-4321";
            var settings = $$"""{ "schema": 1, "bridge": { "secret": "env:LOG_SECRET" }, "sync": { "webApiKey": "{{ApiKey}}", "mail": { "password": "{{Mail}}" } } }""";
            JsonObject Inspect(params string[] env) => new() { ["Id"] = "x", ["State"] = new JsonObject { ["Status"] = "running" }, ["Config"] = new JsonObject { ["Env"] = new JsonArray(env.Select(e => (JsonNode?)e).ToArray()) } };
            var containers = new Dictionary<string, JsonObject>
            {
                ["cs2"] = Inspect($"CS2_RCONPW={Rcon}", $"CS2_LOG_HTTP_URL=http://bridge:8080/log/{Bridge}", $"{TestCopy.Content}={settings}", $"LOG_SECRET={Bridge}", "CS2_MAXPLAYERS=10"),
                ["bridge"] = Inspect($"{TestCopy.Content}={settings}", $"LOG_SECRET={Bridge}"),
                ["sync"] = Inspect($"{TestCopy.Content}={settings}", $"LOG_SECRET={Bridge}"),
            };
            var execs = new List<(string Container, string Cmd)>();
            var logPaths = new List<string>();
            var leak = $"pw {Rcon} and {Bridge}, key {ApiKey}, mail {Mail}";
            Reply Req(string path, string method, JsonNode? body, byte[]? raw, string ctype, double timeout)
            {
                var parts = path.Split('?')[0].Split('/');
                if (method == "GET" && parts[1] == "containers" && parts[^1] == "json")
                    return containers.TryGetValue(parts[2], out var c) ? Reply.Of(200, c) : throw new DockerError("HTTP Error 404: Not Found");
                if (method == "GET" && parts[^1] == "logs")
                {
                    logPaths.Add(path);
                    var text = Encoding.UTF8.GetBytes($"02:00 serve: a line\n02:01 phase: downloads {leak}\n02:02 other\n");
                    return new Reply(200, [1, 0, 0, 0, 0, 0, 0, (byte)text.Length, .. text]);
                }
                throw new InvalidOperationException($"unexpected call {method} {path}");
            }
            var h = new Harness(config)
            {
                Req = Req,
                Exec = (container, cmd, env, timeout) =>
                {
                    execs.Add((container, string.Join(" ", cmd)));
                    return "queues version 2\n" + leak + "\n";
                },
                Now = () => 1_000_000,
            };
            var (_, _, output) = SelfTest.Capture(() => { h.CmdExec("game", "cat /data/queues.json"); return 0; });
            check("probe exec: the command, through sh -c, in production's container of the role (the private config's name), as given",
                  execs.Count == 1 && execs[0] == ("cs2", "sh -c cat /data/queues.json") && output.Contains("queues version 2", StringComparison.Ordinal));
            check("probe exec: every secret scrubbed from what it prints - the RCON password, the bridge's secret from its log address and its env: reference, the settings' API key and mail password",
                  !new[] { Rcon, Bridge, ApiKey, Mail }.Any(s => output.Contains(s, StringComparison.Ordinal)) && output.Contains("pw <secret> and <secret>, key <secret>, mail <secret>", StringComparison.Ordinal));
            (_, _, output) = SelfTest.Capture(() => { h.CmdLogs("sync", "600", "phase:"); return 0; });
            check("probe logs: the role's container, its lines of the last seconds, those the regex matches, scrubbed of every secret of the three - the RCON password the sync does not hold too",
                  logPaths.Count == 1 && logPaths[0] == "/containers/sync/logs?stdout=1&stderr=1&since=999400" && !output.Contains("serve:", StringComparison.Ordinal)
                  && output.Contains("phase: downloads pw <secret> and <secret>, key <secret>, mail <secret>", StringComparison.Ordinal) && !output.Contains(ApiKey, StringComparison.Ordinal));
            var badRole = SelfTest.Refusal(() => h.CmdExec("db", "ls"));
            var badSeconds = SelfTest.Refusal(() => h.CmdLogs("game", "soon", null));
            var gone = SelfTest.Refusal(() => { containers.Remove("bridge"); h.CmdExec("bridge", "ls"); });
            check("probe exec and logs refuse a role that is none of game, bridge and sync, seconds that are no number, and a container that is not there - running nothing",
                  badRole is not null && badRole.Contains("a role is game, bridge or sync", StringComparison.Ordinal) && badSeconds is not null && gone is not null
                  && gone.Contains("no container bridge", StringComparison.Ordinal) && execs.Count == 1);
        }
        finally
        {
            SimConfig.Done(tmp, oldState);
        }
    }
}
