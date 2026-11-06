using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Deploy;

/// <summary>The offline tests' deployment (tools/client/probe/simconfig.py): a private config of made-up values in a scratch folder under the
/// repository's .work/, and the harness's state beside it. No server is ever asked: each test replaces the harness's one door to the Docker API.</summary>
static class SimConfig
{
    public static (string Tmp, string Config, string? OldState) Make(string prefix)
    {
        var tmp = SelfTest.Scratch(prefix);
        Directory.CreateDirectory(Path.Combine(tmp, "bridge-data"));
        var config = Path.Combine(tmp, "config.toml");
        File.WriteAllText(config,
            "[docker]\napi = \"https://docker.example.com:2376\"\n\n"
            + "[bridge]\nimage = \"localhost/overstrike2-bridge\"\ncontainer = \"bridge\"\ngame_container = \"cs2\"\n"
            + $"data_dir = '{Path.Combine(tmp, "bridge-data")}'\n\n"
            + $"[server]\naddress = \"203.0.113.10:27015\"\nlock_file = '{Path.Combine(tmp, "server.lock")}'\n\n"
            + "[client]\nprivate_names = [\"Realname\"]\n\n[client.test_labels]\n\"monitor.mute\" = \"true\"\n");
        var old = Environment.GetEnvironmentVariable("CS2CLIENT_STATE");
        Environment.SetEnvironmentVariable("CS2CLIENT_STATE", Path.Combine(tmp, "state"));
        return (tmp, config, old);
    }

    public static void Done(string tmp, string? oldState)
    {
        Environment.SetEnvironmentVariable("CS2CLIENT_STATE", oldState);
        SelfTest.Remove(tmp);
    }
}

/// <summary>tools/client/probe/test_testcopy.py, ported: prep, rm and run against an in-memory Docker - no RCON quit, the rename only once
/// production has exited, never a credential as an override; the copy on production's own network endpoint; without a settings text in
/// production run works on the variables, with one the copy's environment holds production's text with the automatic actions off, or there
/// is no copy.</summary>
static class TestCopyTests
{
    static readonly string Id = string.Concat(Enumerable.Repeat("c0ffee", 10)) + "c0ff";
    static readonly string Image = "sha256:" + string.Concat(Enumerable.Repeat("be", 32));
    static readonly string Mac64 = "02:00:00:00:00:" + "64", Mac65 = "02:00:00:00:00:" + "65";

    static JsonObject Ins(string status, int rc = 0, bool test = false) => new()
    {
        ["Id"] = Id, ["Image"] = Image, ["RestartCount"] = rc, ["State"] = new JsonObject { ["Status"] = status },
        ["Config"] = new JsonObject { ["Labels"] = test ? new JsonObject { [TestCopy.TestLabel] = "x" } : new JsonObject() },
    };

    public static void Run()
    {
        var check = SelfTest.Check;
        var (tmp, config, oldState) = SimConfig.Make("tc-sim-");
        try
        {
            var calls = new List<string[]>();
            var state = new Dictionary<string, List<JsonObject>>();
            JsonObject? createdBody = null;
            var stopRaises = false;
            Reply Req(string path, string method, JsonNode? body, byte[]? raw, string ctype, double timeout)
            {
                calls.Add([method, path]);
                if (method == "POST" && path.StartsWith("/containers/create", StringComparison.Ordinal))
                {
                    var name = path.Split("name=")[1];
                    state[name] = [new JsonObject
                    {
                        ["Id"] = "new-id", ["State"] = new JsonObject { ["Status"] = "created" },
                        ["Config"] = new JsonObject { ["Labels"] = Json.At(body, "Labels")?.DeepClone() ?? new JsonObject() },
                        ["NetworkSettings"] = new JsonObject { ["Networks"] = new JsonObject { ["cs2"] = new JsonObject() } },
                    }];
                    createdBody = body!.DeepClone() as JsonObject;
                    return Reply.Of(201, new JsonObject { ["Id"] = "new-id" });
                }
                if (method == "GET" && path.StartsWith("/containers/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal))
                {
                    var name = path.Split('/')[2];
                    if (!state.TryGetValue(name, out var seq) || seq.Count == 0)
                        throw new DockerError("HTTP Error 404: Not Found");
                    var cur = seq[0];
                    if (seq.Count > 1)
                        seq.RemoveAt(0);
                    return Reply.Of(200, cur);
                }
                if (method == "POST" && path.Contains("/stop", StringComparison.Ordinal))
                {
                    if (stopRaises)
                        throw new TimeoutException("timed out");
                    return Reply.Of(204, "");
                }
                if (method == "POST" && path.Contains("/rename", StringComparison.Ordinal))
                {
                    var (old, @new) = (path.Split('/')[2], path.Split("name=")[1]);
                    var moved = (JsonObject)state[old][^1].DeepClone();
                    moved["Name"] = "/" + @new;
                    state[@new] = [moved];
                    state.Remove(old);
                    return Reply.Of(204, "");
                }
                if (method == "DELETE")
                {
                    state.Remove("cs2");
                    return Reply.Of(204, "");
                }
                return Reply.Of(200, "{}");
            }
            var h = new Harness(config)
            {
                Req = Req,
                Rcon = c =>
                {
                    calls.Add(["rcon", .. c]);
                    return "";
                },
                NeedLock = () => { },
                Sleep = _ => { },
            };
            var tc = new TestCopy(h) { A2sAsk = _ => new A2sAnswer("srv", "de_dust2", 0, 1) };

            check("the deployment's names come from the private config", (tc.Game, tc.Bridge, tc.Aside, tc.BridgeAside) == ("cs2", "bridge", "cs2-prod-aside", "bridge-prod-aside")
                  && (tc.Host, tc.Port) == ("203.0.113.10", 27015) && tc.Flag == Path.Combine(tmp, "bridge-data", "maintenance") && tc.State == Path.Combine(tmp, "state", "testcopy"));

            int? Exits(Action body)
            {
                try
                {
                    body();
                    return null;
                }
                catch (Exit)
                {
                    return 1;
                }
            }
            string? Refusal(Action body) => SelfTest.Refusal(body);
            void Reset()
            {
                state.Clear();
                calls.Clear();
            }
            void Quiet(Action body) => SelfTest.Capture(() =>
            {
                body();
                return 0;
            });
            bool Stopped() => calls.Any(c => c[0] == "rcon");

            // 1. prep, running -> exited after the stop
            Reset();
            state["cs2"] = [Ins("running"), Ins("running"), Ins("exited")];
            Quiet(tc.CmdPrep);
            check("prep: no RCON quit", !Stopped());
            check("prep: one stop, t=30", calls.Where(c => c[1].Contains("/stop")).Select(c => c[0] + " " + c[1]).SequenceEqual(["POST /containers/cs2/stop?t=30"]));
            check("prep: renamed aside", state.ContainsKey("cs2-prod-aside") && !state.ContainsKey("cs2"));
            check("prep: rename after the stop", calls.FindIndex(c => c[1].Contains("/stop")) < calls.FindIndex(c => c[1].Contains("/rename")));
            check("prep: the maintenance flag set in the bridge's data folder, and noted as this script's", File.Exists(tc.Flag) && File.Exists(tc.FlagMine));

            // 2. prep, the stop call gives up first, the container exits later
            File.Delete(tc.Flag);
            File.Delete(tc.FlagMine);
            Reset();
            state["cs2"] = [Ins("running"), Ins("running"), Ins("running"), Ins("exited")];
            stopRaises = true;
            Quiet(tc.CmdPrep);
            stopRaises = false;
            check("prep after a timed-out stop call: waited, renamed", state.ContainsKey("cs2-prod-aside"));

            // 3. prep, still running after the stop -> refused, not renamed
            File.Delete(tc.Flag);
            File.Delete(tc.FlagMine);
            Reset();
            state["cs2"] = [Ins("running")];
            var clock = 1_000_000.0;
            var realNow = h.Now;
            h.Now = () => clock += 5;
            var refusal = Refusal(tc.CmdPrep);
            h.Now = realNow;
            Out.Print("    " + refusal);
            check("prep: still running -> REFUSED, no rename", refusal is not null && refusal.Contains("REFUSED") && !calls.Any(c => c[1].Contains("/rename")) && state.ContainsKey("cs2"));

            // 4. prep, restarted underneath meanwhile -> warning, still renamed (it is exited now)
            File.Delete(tc.Flag);
            File.Delete(tc.FlagMine);
            Reset();
            state["cs2"] = [Ins("running", 0), Ins("running", 0), Ins("exited", 1)];
            Quiet(tc.CmdPrep);
            check("prep: restart count changed -> renamed after the warning", state.ContainsKey("cs2-prod-aside"));

            // 4b. prep refuses a production with somebody on, or one without VAC
            foreach (var (a2s, why) in new[] { (new A2sAnswer("srv", "m", 1, 1), "a human on"), ((A2sAnswer?)null, "no A2S answer"), (new A2sAnswer("srv", "m", 0, 0), "VAC off") })
            {
                tc.A2sAsk = _ => a2s;
                Reset();
                state["cs2"] = [Ins("running")];
                var r = Refusal(tc.CmdPrep);
                check($"prep refuses with {why}, nothing stopped", r is not null && r.Contains("REFUSED") && !calls.Any(c => c[0] == "POST"));
            }
            tc.A2sAsk = _ => new A2sAnswer("srv", "de_dust2", 0, 1);

            // 5. rm, running test copy
            Reset();
            state["cs2"] = [Ins("running", test: true), Ins("running", test: true), Ins("exited", test: true)];
            Quiet(tc.CmdRm);
            check("rm: no RCON quit", !Stopped());
            check("rm: stop t=10 then delete", calls.Where(c => c[0] is "POST" or "DELETE").Select(c => c[0] + " " + c[1])
                                                    .SequenceEqual(["POST /containers/cs2/stop?t=10", "DELETE /containers/cs2?force=true"]));

            // 6. rm refuses production
            Reset();
            state["cs2"] = [Ins("running")];
            refusal = Refusal(tc.CmdRm);
            check("rm: refuses production, no stop", refusal is not null && refusal.Contains("NOT a test copy") && !calls.Any(c => c[0] is "POST" or "DELETE"));

            // 7. run --env: one more image variable for the copy alone, never a credential
            Directory.CreateDirectory(Path.GetDirectoryName(tc.ProdSaved)!);
            File.WriteAllText(tc.ProdSaved, """{"Image": "sha256:img", "Config": {"Env": ["CS2_MAPGROUP=mg_armsrace", "CS2_RCONPW=pw", "CS2_ADDITIONAL_ARGS=-condebug"], "Labels": {}}, "HostConfig": {"RestartPolicy": {"Name": "always"}}}""");
            var b = tc.Body(TestCopy.EnvOverrides(["--env", "CS2_MAPGROUP=mg_ar_shoots"]));
            var env = Json.Arr(b, "Env").Select(Json.PyStr).ToList();
            check("run --env: the copy gets CS2_MAPGROUP=mg_ar_shoots instead of production's, and -insecure",
                  env.Contains("CS2_MAPGROUP=mg_ar_shoots") && !env.Contains("CS2_MAPGROUP=mg_armsrace") && env.Contains("CS2_ADDITIONAL_ARGS=-condebug -insecure") && env.Contains("CS2_RCONPW=pw"));
            check("run: the copy's labels - the test label, the private config's extra labels, compose's config-hash poisoned; restart policy no",
                  Json.Str(b, "Labels", TestCopy.TestLabel) == TestCopy.Tag && Json.Str(b, "Labels", "monitor.mute") == "true"
                  && Json.Str(b, "Labels", "com.docker.compose.config-hash") == "test-config-not-from-main" && Json.Str(b, "HostConfig", "RestartPolicy", "Name") == "no");
            var one = TestCopy.EnvOverrides(["--env=CS2_MAPGROUP=mg_ar_shoots"]);
            check("run --env=K=V works too", one.Count == 1 && one["CS2_MAPGROUP"] == "mg_ar_shoots");
            foreach (var bad in new[] { new[] { "--env", "CS2_RCONPW=x" }, ["--env", "SRCDS_TOKEN=x"], ["--env", "CS2_PW=x"], ["--env", "CS2_ADDITIONAL_ARGS=x"], ["--env", "novalue"], ["--bogus"] })
                check("run --env refuses " + string.Join(" ", bad), Exits(() => TestCopy.EnvOverrides(bad)) is not null);
            check("run without --env: the copy as before", Json.Arr(tc.Body(), "Env").Count(e => Json.PyStr(e) == "CS2_MAPGROUP=mg_armsrace") == 1);

            // 7b. the copy on production's own network endpoint, from production's inspect: its networks, pinned IP, MAC and aliases
            var net = new JsonObject { ["game"] = new JsonObject { ["IPAMConfig"] = new JsonObject { ["IPv4Address"] = "192.0.2.100" }, ["MacAddress"] = Mac64, ["Aliases"] = new JsonArray("cs2", "game"), ["IPAddress"] = "192.0.2.100" } };
            File.WriteAllText(tc.ProdSaved, Json.Dumps(new JsonObject { ["Image"] = "sha256:img", ["Config"] = Json.O("""{"Env": [], "Labels": {}}"""), ["HostConfig"] = new JsonObject(), ["NetworkSettings"] = new JsonObject { ["Networks"] = net.DeepClone() } }));
            check("run: the copy's endpoint is production's - network, IP, MAC and aliases from its inspect",
                  Json.Same(Json.At(tc.Body(), "NetworkingConfig", "EndpointsConfig"), new JsonObject { ["game"] = new JsonObject { ["IPAMConfig"] = new JsonObject { ["IPv4Address"] = "192.0.2.100" }, ["MacAddress"] = Mac64, ["Aliases"] = new JsonArray("cs2", "game") } }));
            check("pinned: (network, IP, MAC)", TestCopy.Pinned(new JsonObject { ["NetworkSettings"] = new JsonObject { ["Networks"] = net.DeepClone() } }).SequenceEqual([new NetAddress("game", "192.0.2.100", Mac64)]));

            // 8. run against a production without the settings text: the variables do the work, nothing else is touched
            tc.WaitA2s = (want, limit) => (new A2sAnswer("srv", "m", 0, want), 1);
            const string prod = """{"Image": "sha256:img", "Config": {"Env": ["CS2_RCONPW=pw", "CS2_ADDITIONAL_ARGS=-condebug"], "Labels": {}}, "HostConfig": {"RestartPolicy": {"Name": "always"}}}""";
            void RunCopy(string saved)
            {
                Reset();
                File.WriteAllText(tc.ProdSaved, saved);
                state["cs2-prod-aside"] = [Ins("exited")];
            }
            List<string> Steps() => calls.Where(c => c[0] is "POST" or "PUT" or "DELETE").Select(c => c[1].Split('?')[0]).ToList();
            RunCopy(prod);
            Quiet(() => tc.CmdRun([]));
            check("run, no settings text in production: create and start as ever", Steps().SequenceEqual(["/containers/create", "/containers/cs2/start"]));
            var createdEnv = Json.Arr(createdBody, "Env").Select(Json.PyStr).ToList();
            check("run, no settings text in production: the variables that do the work are in the copy's environment, and nothing else is added",
                  TestCopy.Off.All(kv => createdEnv.Contains($"{kv.Key}={kv.Value}")) && createdEnv.Contains("CS2_ADDITIONAL_ARGS=-condebug -insecure")
                  && !createdEnv.Any(e => e.StartsWith(TestCopy.Content, StringComparison.Ordinal)));

            // 9. run against a production on the settings text: its text, with the automatic actions off, in the copy's environment
            const string text = """
{
  "schema": 1,
  "server": { "rconPassword": "env:CS2_RCONPW" },
  "bridge": { "secret": "theBridgeSecret", "url": "http://bridge:8080" },
  "hud": { "addon": "3823993566", "check": "warn" },
  "restarts": { "daily": "07:00" },
  "sync": { "mail": { "from": "A // B <a@example.com>" } }
}

""";
            var switched = new JsonObject
            {
                ["Image"] = "sha256:img", ["Config"] = new JsonObject { ["Env"] = new JsonArray("CS2_RCONPW=pw", "CS2_ADDITIONAL_ARGS=-condebug", TestCopy.Content + "=" + text), ["Labels"] = new JsonObject() },
                ["HostConfig"] = Json.O("""{"RestartPolicy": {"Name": "always"}}"""),
            };
            RunCopy(Json.Dumps(switched));
            var (_, _, printed) = SelfTest.Capture(() =>
            {
                tc.CmdRun([]);
                return 0;
            });
            createdEnv = Json.Arr(createdBody, "Env").Select(Json.PyStr).ToList();
            var texts = createdEnv.Where(e => e.StartsWith(TestCopy.Content + "=", StringComparison.Ordinal)).ToList();
            check("run, settings text: no call beyond create and start, one text in the copy's environment", Steps().SequenceEqual(["/containers/create", "/containers/cs2/start"]) && texts.Count == 1);
            var copySettings = Json.Parse(texts[0].Split('=', 2)[1]);
            check("run, settings text: the watcher's automatic actions off, the deletion off, no visits to the HUD item's page",
                  Json.Same(Json.At(copySettings, "restarts"), Json.O("""{"daily": "off", "updates": false, "newVersions": false, "freezeRecovery": false, "downloadRestart": false}"""))
                  && Json.Same(Json.At(copySettings, "maps"), Json.O("""{"delete": "off", "backgroundDownloads": false, "downloadLine": false}""")) && Json.AsBool(Json.At(copySettings, "hud", "itemPage")) == false);
            check("run, settings text: every other value production's, a // in a string kept, a reference still a reference",
                  Json.Str(copySettings, "server", "rconPassword") == "env:CS2_RCONPW" && Json.Str(copySettings, "bridge", "secret") == "theBridgeSecret"
                  && Json.Str(copySettings, "hud", "addon") == "3823993566" && Json.Str(copySettings, "hud", "check") == "warn"
                  && Json.Str(copySettings, "sync", "mail", "from") == "A // B <a@example.com>" && createdEnv.Contains("CS2_RCONPW=pw"));
            check("run, settings text: no secret printed", !printed.Contains("theBridgeSecret") && printed.Contains("settings text"));
            const string commented = "// a comment\n{ \"schema\": 1, \"hud\": { \"addon\": \"1\", }, }\n";
            check("run, settings text with comments and trailing commas, as core takes it: read, and it leaves without them",
                  Json.Same(Json.At(Json.Parse(TestCopy.SettingsForCopy(TestCopy.Content + "=" + commented).Split('=', 2)[1]), "hud"), Json.O("""{"addon": "1", "itemPage": false}""")));
            var props = Json.Obj(Json.Parse(File.ReadAllText(Repo.P("docs", "settings.schema.json"))), "properties");
            check("run, settings text: every overridden key is a real one (docs/settings.schema.json)",
                  TestCopy.SettingsOff.All(s => ((JsonObject)s.Value!).All(k => Json.Obj(props, s.Key, "properties").ContainsKey(k.Key))));
            var told = Regex.Match(File.ReadAllText(Repo.P("server", "OverStrike2.Server", "Program.cs")), @"log\.Line\(""(restarts\.updates,[^""]*)nothing to do");
            var keys = told.Success ? Regex.Matches(told.Groups[1].Value, @"\b(restarts|maps)\.(\w+)").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList() : [];
            check($"run, settings text: every switch that keeps the watcher running is off, so it exits at once and the copy has none ({keys.Count}, from os2-server's own line)",
                  keys.Count > 0 && keys.All(k => Json.At(TestCopy.SettingsOff, k.Item1, k.Item2) is { } v && (Json.AsBool(v) == false || Json.AsStr(v) == "off")));

            // 10. a copy that cannot get its overrides is never made
            foreach (var (badText, what) in new[] { ("{ not json", "that does not parse"), ("[1, 2]", "that is no object") })
            {
                RunCopy(Json.Dumps(new JsonObject { ["Image"] = "sha256:img", ["Config"] = new JsonObject { ["Env"] = new JsonArray(TestCopy.Content + "=" + badText), ["Labels"] = new JsonObject() },
                                                    ["HostConfig"] = Json.O("""{"RestartPolicy": {"Name": "always"}}""") }));
                var r = Refusal(() => tc.CmdRun([]));
                check($"run, a settings text {what}: refused before anything is created, quoting nothing of it", r is not null && r.Contains("REFUSED") && Steps().Count == 0 && !r.Contains("not json"));
            }
            check("run --env refuses the settings text: it holds secrets", Exits(() => TestCopy.EnvOverrides(["--env", TestCopy.Content + "=x"])) is not null);

            // 11. restore: production back on the endpoint saved at prep, running what its plugins folder holds, or the flag stays on
            var up = Ins("running");
            up["NetworkSettings"] = new JsonObject { ["Networks"] = net.DeepClone() };
            var otherMac = (JsonObject)up.DeepClone();
            otherMac["NetworkSettings"]!["Networks"]!["game"]!["MacAddress"] = Mac65;
            var installed = "files 4 differ 0";
            h.Exec = (container, cmd, env, timeout) =>
            {
                calls.Add(["exec", container, Py.Slice(cmd[^1], 0, 40)]);
                return installed;
            };
            foreach (var (saved, plugins, wantOk, what) in new[]
                     {
                         ((JsonObject?)up, "files 4 differ 0", true, "the same container on the saved endpoint, its own plugins in the game's tree"),
                         (otherMac, "files 4 differ 0", false, "another MAC than at prep"),
                         (null, "files 4 differ 0", false, "no saved inspect"),
                         (up, "DIFF OverStrike2/OverStrike2.dll\nfiles 4 differ 1", false, "a plugin in the game's tree that is not its plugins folder's"),
                     })
            {
                Reset();
                state["cs2-prod-aside"] = [(JsonObject)up.DeepClone()];
                installed = plugins;
                File.WriteAllText(tc.Flag, "x");
                File.WriteAllText(tc.FlagMine, "1");
                if (saved is null)
                    File.Delete(tc.ProdSaved);
                else
                    File.WriteAllText(tc.ProdSaved, Json.Dumps(saved));
                Quiet(tc.CmdRestore);
                check($"restore, {what}: the flag {(wantOk ? "removed" : "left on")}", File.Exists(tc.Flag) != wantOk && File.Exists(tc.FlagMine) != wantOk);
            }
            check("restore: the plugins compared in production itself, its plugins folder against the game's tree",
                  calls.Any(c => c[0] == "exec" && c[1] == "cs2" && c[2].StartsWith("cd /home/steam/cs2-plugins", StringComparison.Ordinal)));
        }
        finally
        {
            SimConfig.Done(tmp, oldState);
        }
    }
}
