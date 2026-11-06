using System.Text;
using System.Text.Json.Nodes;

namespace OverStrike2.Deploy;

/// <summary>tools/client/probe/test_testcopy_bridge.py, ported: the candidate-bridge verbs against an in-memory Docker - production's data is
/// only ever mounted read-only, the candidate runs the new image on the copy, and everything comes back.</summary>
static class TestCopyBridgeTests
{
    const string Data = "/srv/overstrike2/bridge";   // the bridge's /data on the host, as its inspect names it (made up)
    static readonly string Mac65 = "02:00:00:00:00:" + "65";

    static JsonObject ProdBridge() => new()
    {
        ["Id"] = new string('b', 64), ["Name"] = "/bridge", ["Image"] = "sha256:oldimg", ["RestartCount"] = 0, ["State"] = new JsonObject { ["Status"] = "running" },
        ["Config"] = new JsonObject
        {
            ["Image"] = "localhost/overstrike2-bridge", ["User"] = "1000:1000",
            ["Labels"] = Json.O("""{"com.docker.compose.service": "bridge", "org.opencontainers.image.revision": "old"}"""),
            ["Env"] = new JsonArray("PATH=/usr/bin", "MAPS_FILE=/app/maps.json", "LOG_SECRET=s3cr3t", "RCON_PASSWORD=pw", "MAPS_FILE=/data/maps.json"),
        },
        ["HostConfig"] = new JsonObject { ["Binds"] = new JsonArray($"{Data}:/data:rw"), ["RestartPolicy"] = new JsonObject { ["Name"] = "always" }, ["ReadonlyRootfs"] = true },
        ["NetworkSettings"] = new JsonObject
        {
            ["Networks"] = new JsonObject
            {
                ["game"] = new JsonObject
                {
                    ["IPAMConfig"] = new JsonObject { ["IPv4Address"] = "192.0.2.101" }, ["MacAddress"] = Mac65, ["Aliases"] = new JsonArray("bridge", "os2-bridge"),
                    ["IPAddress"] = "192.0.2.101",
                },
            },
        },
    };

    public static void Run()
    {
        var check = SelfTest.Check;
        var (tmp, config, oldState) = SimConfig.Make("tc-bridge-sim-");
        try
        {
            var calls = new List<(string Method, string Path, JsonNode? Body)>();
            var created = new Dictionary<string, JsonObject>();
            var containers = new Dictionary<string, JsonObject>();
            var images = new Dictionary<string, JsonObject>
            {
                ["sha256:oldimg"] = Json.O("""{"Config": {"Env": ["PATH=/usr/bin", "MAPS_FILE=/app/maps.json"]}}"""),
                ["localhost/overstrike2-bridge:candidate"] = Json.O("""{"Config": {"Env": ["PATH=/usr/bin"]}}"""),
            };
            var helperExit = 0;
            Reply Req(string path, string method, JsonNode? body, byte[]? raw, string ctype, double timeout)
            {
                calls.Add((method, path, body?.DeepClone()));
                var parts = path.Split('?')[0].Split('/');
                if (method == "GET" && path.StartsWith("/images/", StringComparison.Ordinal))
                {
                    var reference = path["/images/".Length..^"/json".Length];
                    if (!images.TryGetValue(reference, out var image))
                        throw new DockerError("HTTP Error 404: Not Found");
                    return Reply.Of(200, image);
                }
                if (method == "GET" && parts[1] == "containers" && parts[^1] == "json")
                {
                    if (!containers.TryGetValue(parts[2], out var c))
                        throw new DockerError("HTTP Error 404: Not Found");
                    return Reply.Of(200, c);
                }
                if (method == "POST" && path.StartsWith("/containers/create", StringComparison.Ordinal))
                {
                    var name = path.Split("name=")[1];
                    var cid = name + "-id";
                    created[name] = (JsonObject)body!.DeepClone();
                    var net = new JsonObject();
                    foreach (var (n, e) in Json.Obj(body, "NetworkingConfig", "EndpointsConfig"))
                    {
                        var endpoint = (JsonObject)e!.DeepClone();
                        endpoint["IPAddress"] = Json.Str(e, "IPAMConfig", "IPv4Address") ?? "";
                        net[n] = endpoint;
                    }
                    containers[name] = new JsonObject
                    {
                        ["Id"] = cid, ["Name"] = "/" + name, ["Image"] = Json.At(body, "Image")!.DeepClone(), ["RestartCount"] = 0, ["State"] = new JsonObject { ["Status"] = "created" },
                        ["Config"] = new JsonObject { ["Labels"] = Json.Obj(body, "Labels").DeepClone(), ["Env"] = Json.Arr(body, "Env").DeepClone(), ["User"] = Json.At(body, "User")?.DeepClone() },
                        ["HostConfig"] = Json.Obj(body, "HostConfig").DeepClone(), ["NetworkSettings"] = new JsonObject { ["Networks"] = net },
                    };
                    return Reply.Of(201, new JsonObject { ["Id"] = cid });
                }
                var found = containers.FirstOrDefault(kv => Json.Str(kv.Value, "Id") == parts[2] || kv.Key == parts[2]).Key;
                if (method == "POST" && parts[^1] == "start")
                {
                    containers[found!]["State"]!["Status"] = "running";
                    return Reply.Of(204, "");
                }
                if (method == "POST" && parts[^1] == "stop")
                {
                    containers[found!]["State"]!["Status"] = "exited";
                    return Reply.Of(204, "");
                }
                if (method == "POST" && parts[^1] == "wait")
                {
                    containers[found!]["State"]!["Status"] = "exited";
                    return Reply.Of(200, new JsonObject { ["StatusCode"] = helperExit });
                }
                if (method == "GET" && parts[^1] == "logs")
                    return new Reply(200, [1, 0, 0, 0, 0, 0, 0, 0x14, .. Encoding.ASCII.GetBytes("files: 7, 2.0M\nok\n")]);
                if (method == "POST" && parts[^1] == "rename")
                {
                    var @new = path.Split("name=")[1];
                    var moved = containers[found!];
                    containers.Remove(found!);
                    containers[@new] = moved;
                    moved["Name"] = "/" + @new;
                    return Reply.Of(204, "");
                }
                if (method == "DELETE")
                {
                    if (found is not null)
                        containers.Remove(found);
                    return Reply.Of(204, "");
                }
                throw new InvalidOperationException($"unexpected call {method} {path}");
            }
            var h = new Harness(config) { Req = Req, NeedLock = () => { }, Rcon = _ => "", Sleep = _ => { } };
            var tc = new TestCopy(h);
            string? Refused(Action body) => SelfTest.Refusal(body);
            void Quiet(Action body) => SelfTest.Capture(() =>
            {
                body();
                return 0;
            });

            // production's game server not aside yet: no candidate bridge
            containers["bridge"] = ProdBridge();
            var r = Refused(tc.CmdBridgePrep);
            check("bridge-prep refuses while production's game server is not aside", r is not null && r.Contains("run prep first") && !containers.ContainsKey("bridge-prod-aside"));

            // prep was done: production's game server aside, its inspect saved
            Directory.CreateDirectory(tc.State);
            File.WriteAllText(tc.ProdSaved, """{"Image": "sha256:gameimg"}""");
            containers["cs2-prod-aside"] = new JsonObject { ["Id"] = new string('g', 64), ["State"] = new JsonObject { ["Status"] = "exited" }, ["Config"] = new JsonObject { ["Labels"] = new JsonObject() } };
            Quiet(tc.CmdBridgePrep);
            check("bridge-prep: production's bridge saved, stopped and renamed aside",
                  File.Exists(tc.BridgeSaved) && !containers.ContainsKey("bridge") && Json.Str(containers["bridge-prod-aside"], "State", "Status") == "exited");

            // a tag the server does not have
            r = Refused(() => tc.CmdBridgeRun(["nosuch"]));
            check("bridge-run refuses an image the server does not have, before any copy", r is not null && r.Contains("no image") && !created.ContainsKey("bridge-copy"));

            calls.Clear();
            Quiet(() => tc.CmdBridgeRun(["candidate"]));
            var helper = created["bridge-copy"];
            var cand = created["bridge"];
            var script = Json.Arr(helper, "Cmd").Select(Json.PyStr).FirstOrDefault() ?? "";
            check("bridge-run: the copy helper - game image as root, no network, production's data read-only, the copy beside it",
                  Json.Str(helper, "Image") == "sha256:gameimg" && Json.Str(helper, "User") == "0:0" && Json.Str(helper, "HostConfig", "NetworkMode") == "none"
                  && Json.Arr(helper, "HostConfig", "Binds").Select(Json.PyStr).SequenceEqual([$"{Data}:/src:ro", $"{Data}-candidate:/dst"]));
            check("bridge-run: the helper drops the maintenance flag and gives the copy to the bridge's user",
                  script.Contains("rm -f /dst/maintenance") && script.Contains("chown 1000:1000 /dst") && script.Contains("cp -a /src/. /dst/"));
            check("bridge-run: the helper is removed afterwards", !containers.ContainsKey("bridge-copy"));
            var binds = Json.Arr(cand, "HostConfig", "Binds").Select(Json.PyStr).ToList();
            check("bridge-run: the candidate runs the new image with /data on the copy - production's data in no candidate mount",
                  Json.Str(cand, "Image") == "localhost/overstrike2-bridge:candidate" && binds.SequenceEqual([$"{Data}-candidate:/data:rw"]) && !binds.Any(b => b.StartsWith(Data + ":", StringComparison.Ordinal)));
            check("bridge-run: restart policy no, the candidate's label, the config's extra labels, compose's config-hash poisoned, the old image's own labels gone",
                  Json.Str(cand, "HostConfig", "RestartPolicy", "Name") == "no" && Json.Str(cand, "Labels", TestCopy.TestLabel) == TestCopy.BridgeTag
                  && Json.Str(cand, "Labels", "monitor.mute") == "true" && Json.Str(cand, "Labels", "com.docker.compose.config-hash") == "test-config-not-from-main"
                  && !Json.Obj(cand, "Labels").ContainsKey("org.opencontainers.image.revision") && Json.Str(cand, "Labels", "com.docker.compose.service") == "bridge");
            var ep = Json.At(cand, "NetworkingConfig", "EndpointsConfig", "game");
            check("bridge-run: production's IP, MAC and aliases, so the test copy reaches it by name",
                  Json.Str(ep, "IPAMConfig", "IPv4Address") == "192.0.2.101" && Json.Str(ep, "MacAddress") == Mac65 && Json.Arr(ep, "Aliases").Select(Json.PyStr).SequenceEqual(["bridge", "os2-bridge"]));
            var env = Json.Arr(cand, "Env").Select(Json.PyStr).ToList();
            check("bridge-run: the environment compose set (secrets kept, the old image's own variables left to the new image)",
                  env.Contains("LOG_SECRET=s3cr3t") && env.Contains("MAPS_FILE=/data/maps.json") && !env.Contains("MAPS_FILE=/app/maps.json") && !env.Contains("PATH=/usr/bin")
                  && Json.Str(cand, "User") == "1000:1000");
            check("bridge-run: the candidate is running", Json.Str(containers["bridge"], "State", "Status") == "running");

            // production's bridge on the settings text: it is in the candidate's environment as it is, and nothing else is called for it
            containers.Remove("bridge");
            var production = (JsonObject)Json.Parse(File.ReadAllText(tc.BridgeSaved))!;
            var withText = (JsonObject)production.DeepClone();
            ((JsonArray)withText["Config"]!["Env"]!).Add(TestCopy.Content + "={ \"schema\": 1 }");
            ((JsonArray)withText["Config"]!["Env"]!).Add("CS2_RCONPW=pw");
            File.WriteAllText(tc.BridgeSaved, Json.Dumps(withText));
            calls.Clear();
            Quiet(() => tc.CmdBridgeRun(["candidate"]));
            var candEnv = Json.Arr(created["bridge"], "Env").Select(Json.PyStr).ToList();
            check("bridge-run, settings text: the candidate's environment holds production's text and password, byte for byte, read-only root and all",
                  candEnv.Contains(TestCopy.Content + "={ \"schema\": 1 }") && candEnv.Contains("CS2_RCONPW=pw") && Json.AsBool(Json.At(created["bridge"], "HostConfig", "ReadonlyRootfs")) == true
                  && !calls.Any(c => c.Path.Contains("/archive", StringComparison.Ordinal)));
            File.WriteAllText(tc.BridgeSaved, Json.Dumps(production));

            // --keep-flag
            containers.Remove("bridge");
            Quiet(() => tc.CmdBridgeRun(["candidate", "--keep-flag"]));
            check("bridge-run --keep-flag keeps the maintenance flag in the copy", !(Json.Arr(created["bridge-copy"], "Cmd").Select(Json.PyStr).FirstOrDefault() ?? "").Contains("maintenance"));

            // a failed copy starts no candidate
            containers.Remove("bridge");
            created.Remove("bridge");
            helperExit = 1;
            r = Refused(() => tc.CmdBridgeRun(["candidate"]));
            check("bridge-run: a failed copy - refused, no candidate", r is not null && r.Contains("copy failed") && !created.ContainsKey("bridge"));
            helperExit = 0;
            Quiet(() => tc.CmdBridgeRun(["candidate"]));

            // bridge-rm refuses production
            var savedCand = containers["bridge"];
            containers["bridge"] = ProdBridge();
            r = Refused(tc.CmdBridgeRm);
            check("bridge-rm refuses a bridge that is not the candidate", r is not null && r.Contains("NOT a candidate") && containers.ContainsKey("bridge"));
            containers["bridge"] = savedCand;

            calls.Clear();
            Quiet(tc.CmdBridgeRm);
            var rm = created["bridge-copy-rm"];
            check("bridge-rm: the candidate removed, then the copy - its folder alone, by name",
                  !containers.ContainsKey("bridge") && Json.Arr(rm, "HostConfig", "Binds").Select(Json.PyStr).SequenceEqual(["/srv/overstrike2:/p"])
                  && Json.PyStr(Json.Arr(rm, "Cmd")[0]) == "rm -rf -- '/p/bridge-candidate' && echo removed" && Json.Str(rm, "HostConfig", "NetworkMode") == "none");

            Quiet(tc.CmdBridgeRestore);
            containers.TryGetValue("bridge", out var d);
            check("bridge-restore: production's own container back under its name, running, the saved inspect gone",
                  d is not null && Json.Str(d, "Id") == new string('b', 64) && Json.Str(d, "State", "Status") == "running" && !containers.ContainsKey("bridge-prod-aside")
                  && !File.Exists(tc.BridgeSaved));
            check("never: a rw mount of production's data in any container this created",
                  !created.Values.SelectMany(body => Json.Arr(body, "HostConfig", "Binds").Select(Json.PyStr)).Any(b => b == $"{Data}:/data:rw" || b == $"{Data}:/dst"));

            // restore (production's game server) refuses while a candidate bridge or the aside is there
            containers["cs2-prod-aside"] = new JsonObject { ["Id"] = new string('g', 64), ["State"] = new JsonObject { ["Status"] = "exited" }, ["Config"] = new JsonObject { ["Labels"] = new JsonObject() } };
            containers["bridge-prod-aside"] = ProdBridge();
            r = Refused(tc.CmdRestore);
            check("restore refuses while production's bridge is aside", r is not null && r.Contains("bridge-restore first") && !containers.ContainsKey("cs2"));
            containers.Remove("bridge-prod-aside");
            containers["bridge"] = new JsonObject { ["Id"] = "c-id", ["State"] = new JsonObject { ["Status"] = "running" }, ["Config"] = new JsonObject { ["Labels"] = new JsonObject { [TestCopy.TestLabel] = TestCopy.BridgeTag } } };
            r = Refused(tc.CmdRestore);
            check("restore refuses while the bridge is the candidate", r is not null && r.Contains("bridge-restore first") && !containers.ContainsKey("cs2"));
            foreach (var bad in new[] { "", "relative/path", "/", "/mnt", "/srv/../etc" })
                check($"copy_dir refuses {Py.Repr(bad)}", Refused(() => TestCopy.CopyDir(bad)) is not null);
            check("copy_dir: beside production's folder", TestCopy.CopyDir(Data) == Data + "-candidate");
        }
        finally
        {
            SimConfig.Done(tmp, oldState);
        }
    }
}
