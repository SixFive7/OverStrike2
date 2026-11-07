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
/// production has exited, never a credential as an override; the copy on production's own network endpoint; the copy's environment holds
/// production's settings text with the automatic actions off, or there is no copy - none without the text either; run --downloads keeps the
/// background downloads production's only while production's watcher has nothing of its own to judge; beside a candidate bridge the copy
/// reads the candidate's copy of the bridge's data; probe unload keeps the lock while the game server is a test copy.</summary>
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
            File.WriteAllText(tc.ProdSaved, Json.Dumps(new JsonObject
            {
                ["Image"] = "sha256:img", ["Config"] = new JsonObject { ["Env"] = new JsonArray("CS2_MAPGROUP=mg_armsrace", "CS2_RCONPW=pw", "CS2_ADDITIONAL_ARGS=-condebug", TestCopy.Content + "={}"), ["Labels"] = new JsonObject() },
                ["HostConfig"] = Json.O("""{"RestartPolicy": {"Name": "always"}}"""),
            }));
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
            File.WriteAllText(tc.ProdSaved, Json.Dumps(new JsonObject { ["Image"] = "sha256:img", ["Config"] = new JsonObject { ["Env"] = new JsonArray(TestCopy.Content + "={}"), ["Labels"] = new JsonObject() }, ["HostConfig"] = new JsonObject(), ["NetworkSettings"] = new JsonObject { ["Networks"] = net.DeepClone() } }));
            check("run: the copy's endpoint is production's - network, IP, MAC and aliases from its inspect",
                  Json.Same(Json.At(tc.Body(), "NetworkingConfig", "EndpointsConfig"), new JsonObject { ["game"] = new JsonObject { ["IPAMConfig"] = new JsonObject { ["IPv4Address"] = "192.0.2.100" }, ["MacAddress"] = Mac64, ["Aliases"] = new JsonArray("cs2", "game") } }));
            check("pinned: (network, IP, MAC)", TestCopy.Pinned(new JsonObject { ["NetworkSettings"] = new JsonObject { ["Networks"] = net.DeepClone() } }).SequenceEqual([new NetAddress("game", "192.0.2.100", Mac64)]));

            // 8. a production without the settings text gets no copy: no program reads anything else that turns the watcher off
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
            refusal = Refusal(() => tc.CmdRun([]));
            check("run, no settings text in production: refused, nothing created", refusal is not null && refusal.Contains("REFUSED") && refusal.Contains("no settings text") && Steps().Count == 0);
            check("the older programs' variables are gone: a copy's environment is production's but for -insecure and --env",
                  typeof(TestCopy).GetMember("Off", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
                                                    | System.Reflection.BindingFlags.Instance).Length == 0);
            List<string> createdEnv;

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

            // 10b. run --downloads: maps.backgroundDownloads production's, so the watcher stays up for it alone; never while production's watcher
            // has a restart of its own to judge (its state's pending record), never without the settings text
            var helped = new List<(string Name, List<string> Binds, string Script)>();
            var (watchText, watchCode) = ("{\"schema\": 1, \"pending\": null}\n", 0);
            tc.HelperRun = (name, binds, script) =>
            {
                helped.Add((name, binds.ToList(), script));
                return (watchCode, watchText);
            };
            var gameDir = Json.O("""{"Binds": ["/srv/game:/home/steam/cs2-dedicated", "/srv/bdata:/data:ro"], "RestartPolicy": {"Name": "always"}}""");
            JsonObject With(JsonObject d, string key, JsonNode value)
            {
                var o = (JsonObject)d.DeepClone();
                o[key] = value.DeepClone();
                return o;
            }
            JsonObject WithText(JsonObject d, string newText)
            {
                var o = (JsonObject)d.DeepClone();
                var e = new JsonArray(Json.Arr(d, "Config", "Env").Select(x => (JsonNode?)(Json.PyStr(x).StartsWith(TestCopy.Content + "=", StringComparison.Ordinal) ? TestCopy.Content + "=" + newText : Json.PyStr(x))).ToArray());
                o["Config"]!["Env"] = e;
                return o;
            }
            (string? Refused, string Said) RunOut(JsonObject saved, string[] argv)
            {
                RunCopy(Json.Dumps(saved));
                helped.Clear();
                var was = Console.Out;
                var sw = new StringWriter();
                Console.SetOut(sw);
                try
                {
                    tc.CmdRun(argv);
                    return (null, sw.ToString());
                }
                catch (Exit e)
                {
                    return (e.Message, sw.ToString());
                }
                finally
                {
                    Console.SetOut(was);
                }
            }
            JsonNode? CopySettings() => Json.Parse(Json.Arr(createdBody, "Env").Select(Json.PyStr).First(e => e.StartsWith(TestCopy.Content + "=", StringComparison.Ordinal)).Split('=', 2)[1]);
            var dl = With(switched, "HostConfig", gameDir);
            var (refused, said) = RunOut(dl, ["--downloads"]);
            var cs = CopySettings();
            check("run --downloads: maps.backgroundDownloads left as production has it (absent: core's default, on), every other switch still off",
                  refused is null && Json.Same(Json.At(cs, "maps"), Json.O("""{"delete": "off", "downloadLine": false}"""))
                  && Json.Same(Json.At(cs, "restarts"), Json.O("""{"daily": "off", "updates": false, "newVersions": false, "freezeRecovery": false, "downloadRestart": false}"""))
                  && Json.AsBool(Json.At(cs, "hud", "itemPage")) == false);
            check("run --downloads: says so, and the overridden keys it names leave it out",
                  said.Contains("maps.backgroundDownloads at production's value (on)") && said.Contains("maps.delete, maps.downloadLine, hud.itemPage overridden") && !said.Contains("theBridgeSecret"));
            check("run --downloads: production's watcher state read by a helper with the game's tree read-only, before anything is created",
                  helped.Count == 1 && helped[0].Name == "cs2-watch-read" && helped[0].Binds.SequenceEqual(["/srv/game:/g:ro"]) && helped[0].Script.Contains("update-watch/state.json"));
            var offInProd = WithText(dl, text.Replace("\"schema\": 1,", "\"schema\": 1, \"maps\": { \"backgroundDownloads\": false },"));
            (refused, said) = RunOut(offInProd, ["--downloads"]);
            check("run --downloads, production has it off: the copy too, and it says the watcher still exits",
                  refused is null && Json.AsBool(Json.At(CopySettings(), "maps", "backgroundDownloads")) == false && said.Contains("still has nothing to do"));
            foreach (var (wText, wCode, why) in new[] { ("{\"schema\": 1, \"pending\": {\"kind\": \"version\", \"at\": 1}}\n", 0, "a restart of its own to judge"), ("not json", 0, "cannot be read"), ("", 1, "cannot be read") })
            {
                (watchText, watchCode) = (wText, wCode);
                (refused, said) = RunOut(dl, ["--downloads"]);
                check($"run --downloads refuses when production's watcher state has {why}, nothing created", refused is not null && refused.Contains("REFUSED") && refused.Contains(why) && Steps().Count == 0);
            }
            (watchText, watchCode) = ("{}\n", 0);
            (refused, said) = RunOut(dl, ["--downloads"]);
            check("run --downloads with no watcher state yet: taken", refused is null && Steps().SequenceEqual(["/containers/create", "/containers/cs2/start"]));
            (refused, said) = RunOut(With(Json.O(prod), "HostConfig", gameDir), ["--downloads"]);
            check("run --downloads refuses a production without the settings text, nothing created", refused is not null && refused.Contains("has none") && Steps().Count == 0);
            (refused, said) = RunOut(dl, ["--downloads", "--env", "CS2_MAPGROUP=mg_ar_shoots"]);
            check("run --downloads with --env: both taken", refused is null && Json.Arr(createdBody, "Env").Select(Json.PyStr).Contains("CS2_MAPGROUP=mg_ar_shoots"));
            (refused, said) = RunOut(dl, []);
            check("run without --downloads: no helper, the downloads off as ever", helped.Count == 0 && Json.AsBool(Json.At(CopySettings(), "maps", "backgroundDownloads")) == false);

            // 10c. beside a candidate bridge the copy reads the candidate's copy of the bridge's data, read-only as production reads production's
            var cand = new JsonObject
            {
                ["Id"] = "cand", ["Config"] = new JsonObject { ["Labels"] = new JsonObject { [TestCopy.TestLabel] = TestCopy.BridgeTag } },
                ["HostConfig"] = Json.O("""{"Binds": ["/srv/bdata-candidate:/data"]}"""), ["State"] = Json.O("""{"Status": "running"}"""),
            };
            (string? Refused, List<string>? Binds, string Said) BindsBeside(JsonObject? candidate, JsonObject? saved = null)
            {
                var (r, s) = (default(string), "");
                RunCopy(Json.Dumps(saved ?? dl));
                if (candidate is not null)
                    state["bridge"] = [candidate];
                var was = Console.Out;
                var sw = new StringWriter();
                Console.SetOut(sw);
                try
                {
                    tc.CmdRun([]);
                }
                catch (Exit e)
                {
                    r = e.Message;
                }
                finally
                {
                    Console.SetOut(was);
                    s = sw.ToString();
                }
                return (r, r is null ? Json.Arr(createdBody, "HostConfig", "Binds").Select(Json.PyStr).ToList() : null, s);
            }
            var gameBinds = Json.Arr(gameDir, "Binds").Select(Json.PyStr).ToList();
            var (refusedB, binds, saidB) = BindsBeside(cand);
            check("run beside a candidate: the bridge data at /data is the candidate's copy, its read-only kept; the game's tree as it is",
                  refusedB is null && binds!.SequenceEqual(["/srv/game:/home/steam/cs2-dedicated", "/srv/bdata-candidate:/data:ro"]) && saidB.Contains("<data>-candidate") && !saidB.Contains("/srv/"));
            (refusedB, binds, saidB) = BindsBeside(null);
            check("run without a candidate: production's bridge data", refusedB is null && binds!.SequenceEqual(gameBinds) && !saidB.Contains("candidate"));
            (refusedB, binds, saidB) = BindsBeside(With(cand, "Config", Json.O("""{"Labels": {}}""")));
            check("run beside production's own bridge: production's bridge data", refusedB is null && binds!.SequenceEqual(gameBinds));
            (refusedB, binds, saidB) = BindsBeside(With(cand, "HostConfig", Json.O("""{"Binds": ["/elsewhere:/data"]}""")));
            check("run beside a candidate whose data is not the copy beside production's: refused, nothing created", refusedB is not null && refusedB.Contains("REFUSED") && Steps().Count == 0);
            (refusedB, binds, saidB) = BindsBeside(With(cand, "HostConfig", Json.O("""{"Binds": []}""")));
            check("run beside a candidate without a /data bind: refused, nothing created", refusedB is not null && refusedB.Contains("REFUSED") && Steps().Count == 0);
            var moved = WithText(With(dl, "HostConfig", Json.O("""{"Binds": ["/srv/game:/home/steam/cs2-dedicated", "/srv/bdata:/bridge:ro"]}""")),
                                 text.Replace("\"schema\": 1,", "\"schema\": 1, \"paths\": { \"bridgeData\": \"/bridge\" },"));
            (refusedB, binds, saidB) = BindsBeside(cand, moved);
            check("run beside a candidate, paths.bridgeData elsewhere: the bind there is the one swapped",
                  refusedB is null && binds!.SequenceEqual(["/srv/game:/home/steam/cs2-dedicated", "/srv/bdata-candidate:/bridge:ro"]));
            (refusedB, binds, saidB) = BindsBeside(cand, With(dl, "HostConfig", Json.O("""{"Binds": ["/srv/game:/home/steam/cs2-dedicated"]}""")));
            check("run beside a candidate, production reads no bridge data: nothing to swap, and it says so",
                  refusedB is null && binds!.SequenceEqual(["/srv/game:/home/steam/cs2-dedicated"]) && saidB.Contains("reads no bridge data"));
            state.Remove("bridge");
            tc.HelperRun = (name, bnds, script) => tc.Helper(name, bnds, script);

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

            // 12. probe unload on a test copy keeps the server lock: production is still aside; on production it releases it
            h.Exec = (container, cmd, env, timeout) => "ls: cannot access '/tmp/ggprobe': No such file or directory";
            foreach (var (test, kept, what) in new[] { (true, true, "a test copy: the lock kept"), (false, false, "production: the lock released") })
            {
                state.Clear();
                state["cs2"] = [Ins("running", test: test)];
                File.Delete(h.LockPath);
                Quiet(() =>
                {
                    h.CmdLock("unload test");
                    h.CmdUnload();
                });
                check($"probe unload on {what}", File.Exists(h.LockPath) == kept);
            }
            File.Delete(h.LockPath);
        }
        finally
        {
            SimConfig.Done(tmp, oldState);
        }
    }
}
