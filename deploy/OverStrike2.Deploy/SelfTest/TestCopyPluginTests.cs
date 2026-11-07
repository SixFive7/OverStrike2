using System.Formats.Tar;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Deploy;

/// <summary>tools/client/probe/test_testcopy_plugin.py, ported: the plugin of a commit of main against an in-memory Docker - only a commit of
/// main is staged, the staged folder is made beside production's plugins folder by a helper (production's own folder only ever read-only),
/// byte for byte as built; run mounts it in the place of production's plugins folder and checks the copy runs it; plugin-rm and restore take
/// it away, restore only once production runs its own plugin again. The build itself (dotnet) is replaced by made-up files.</summary>
static class TestCopyPluginTests
{
    const string Plugins = "/srv/overstrike2/plugins";   // production's plugins folder on the host, as its inspect names it (made up)
    const string Stage = Plugins + "-candidate";
    const string Tree = "/home/steam/cs2-dedicated/game/csgo/addons/counterstrikesharp/plugins";
    static readonly Dictionary<string, byte[]> Built = new()
    {
        ["OverStrike2.dll"] = "MZ the new plugin"u8.ToArray(), ["OverStrike2.pdb"] = "its symbols"u8.ToArray(), ["blocked.txt"] = "game_type\n"u8.ToArray(),
        ["catchup.json"] = "{}\n"u8.ToArray(),
    };
    static readonly Dictionary<string, byte[]> Other = new() { ["./Other/Other.dll"] = "MZ another plugin"u8.ToArray() };   // kept as it is

    static string Sha(byte[] b) => Repo.Sha256(b);

    static JsonObject Production(string status = "running", string? text = "{ \"schema\": 1 }", bool mounts = false)
    {
        var env = new JsonArray("CS2_RCONPW=pw");
        if (text is not null)
            env.Add(TestCopy.Content + "=" + text);
        string[] binds = [$"{Plugins}:/home/steam/cs2-plugins:ro", "/srv/overstrike2/bridge:/data:ro", "/srv/cs2:/home/steam/cs2-dedicated"];
        var host = new JsonObject { ["RestartPolicy"] = new JsonObject { ["Name"] = "always" } };
        if (mounts)
            host["Mounts"] = new JsonArray(binds.Select(b => (JsonNode?)new JsonObject
            {
                ["Type"] = "bind", ["Source"] = b.Split(':')[0], ["Target"] = b.Split(':')[1], ["ReadOnly"] = b.EndsWith(":ro", StringComparison.Ordinal),
            }).ToArray());
        else
            host["Binds"] = new JsonArray(binds.Select(b => (JsonNode?)b).ToArray());
        return new JsonObject
        {
            ["Id"] = new string('p', 64), ["Name"] = "/cs2", ["Image"] = "sha256:gameimg", ["RestartCount"] = 0, ["State"] = new JsonObject { ["Status"] = status },
            ["Config"] = new JsonObject { ["Labels"] = new JsonObject(), ["Env"] = env }, ["HostConfig"] = host, ["NetworkSettings"] = new JsonObject { ["Networks"] = new JsonObject() },
        };
    }

    public static void Run()
    {
        var check = SelfTest.Check;
        var (tmp, config, oldState) = SimConfig.Make("tc-plugin-sim-");
        try
        {
            var calls = new List<(string Method, string Path, object? Body)>();
            var created = new Dictionary<string, JsonObject>();
            var containers = new Dictionary<string, JsonObject>();
            var disk = new Dictionary<string, byte[]>(StringComparer.Ordinal);       // the staged folder's files as the helper leaves them
            var copyTree = new Dictionary<string, byte[]>(StringComparer.Ordinal);   // the game's tree in the test copy
            bool treeDiffers = false, corrupt = false;
            var execs = new Dictionary<string, (string? Name, List<string> Cmd)>();
            var builds = new List<string>();

            string Answer(List<string> cmd)
            {
                var text = cmd.Count > 0 ? cmd[^1] : "";
                if (cmd.Count > 0 && cmd[0] == "sha256sum")
                    return copyTree.TryGetValue(cmd[1], out var data) ? $"{Sha(data)}  {cmd[1]}\n" : $"sha256sum: {cmd[1]}: No such file or directory\n";
                if (text.Contains("files $n differ $bad"))
                    return treeDiffers ? "DIFF OverStrike2/OverStrike2.dll\nfiles 5 differ 1\n" : "files 5 differ 0\n";
                if (text.Contains("find /dst -mindepth 1 -delete"))
                {
                    disk.Clear();
                    foreach (var (k, v) in Other)
                        disk[k] = v;
                    return "EXIT=0\n";
                }
                if (text.Contains("sha256sum") && text.Contains("chown -R --reference=/src /dst"))
                {
                    var files = new Dictionary<string, byte[]>(disk, StringComparer.Ordinal);
                    if (corrupt)
                        files["./OverStrike2/OverStrike2.dll"] = "something else"u8.ToArray();
                    return string.Concat(files.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{Sha(kv.Value)}  {kv.Key}\n")) + "EXIT=0\n";
                }
                throw new InvalidOperationException($"unexpected exec {string.Join(" ", cmd)}");
            }

            Reply Req(string path, string method, JsonNode? body, byte[]? raw, string ctype, double timeout)
            {
                calls.Add((method, path, raw is not null ? raw.ToArray() : body?.DeepClone()));
                var parts = path.Split('?')[0].Split('/');
                if (method == "GET" && parts[1] == "containers" && parts[^1] == "json")
                    return containers.TryGetValue(parts[2], out var c) ? Reply.Of(200, c) : throw new DockerError("HTTP Error 404: Not Found");
                if (method == "POST" && path.StartsWith("/containers/create", StringComparison.Ordinal))
                {
                    var name = path.Split("name=")[1];
                    created[name] = (JsonObject)body!.DeepClone();
                    containers[name] = new JsonObject
                    {
                        ["Id"] = name + "-id", ["Name"] = "/" + name, ["Image"] = Json.At(body, "Image")!.DeepClone(), ["State"] = new JsonObject { ["Status"] = "created" },
                        ["Config"] = new JsonObject { ["Labels"] = Json.Obj(body, "Labels").DeepClone(), ["Env"] = Json.Arr(body, "Env").DeepClone() },
                        ["HostConfig"] = Json.Obj(body, "HostConfig").DeepClone(),
                    };
                    return Reply.Of(201, new JsonObject { ["Id"] = name + "-id" });
                }
                if (method == "POST" && parts[1] == "exec" && parts[^1] == "start")
                    return Reply.Of(200, Answer(execs[parts[2]].Cmd));
                var found = containers.FirstOrDefault(kv => Json.Str(kv.Value, "Id") == parts[2] || kv.Key == parts[2]).Key;
                if (method == "POST" && parts[^1] == "exec")
                {
                    var id = $"x{execs.Count}";
                    execs[id] = (found, Json.Arr(body, "Cmd").Select(Json.PyStr).ToList());
                    return Reply.Of(201, new JsonObject { ["Id"] = id });
                }
                if (method == "PUT" && parts[^1] == "archive")
                {
                    if (!path.EndsWith("?path=/dst", StringComparison.Ordinal))
                        throw new InvalidOperationException(path);
                    using var reader = new TarReader(new MemoryStream(raw!));
                    while (reader.GetNextEntry(copyData: true) is { } e)
                    {
                        if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                            continue;
                        using var ms = new MemoryStream();
                        e.DataStream?.CopyTo(ms);
                        disk["./" + e.Name] = ms.ToArray();
                    }
                    return Reply.Of(200, "");
                }
                if (method == "POST" && parts[^1] == "start")
                {
                    containers[found!]["State"]!["Status"] = "running";
                    if (found == "cs2" && Json.Obj(containers[found], "Config", "Labels").ContainsKey(TestCopy.TestLabel))   // the copy's start copies its plugins into the game's tree
                    {
                        var src = Json.Arr(containers[found], "HostConfig", "Binds").Select(Json.PyStr).First(b => b.Split(':')[1] == "/home/steam/cs2-plugins").Split(':')[0];
                        copyTree.Clear();
                        if (src == Stage)
                            foreach (var (p, b) in disk)
                                copyTree[$"{Tree}/{p[2..]}"] = b;
                    }
                    return Reply.Of(204, "");
                }
                if (method == "POST" && parts[^1] == "wait")
                {
                    containers[found!]["State"]!["Status"] = "exited";
                    return Reply.Of(200, new JsonObject { ["StatusCode"] = 0 });
                }
                if (method == "GET" && parts[^1] == "logs")
                    return Reply.Of(200, "removed\n");
                if (method == "POST" && parts[^1] is "stop" or "rename")
                {
                    if (parts[^1] == "rename")
                    {
                        var moved = containers[found!];
                        containers.Remove(found!);
                        containers[path.Split("name=")[1]] = moved;
                    }
                    else
                        containers[found!]["State"]!["Status"] = "exited";
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

            var h = new Harness(config) { Req = Req, NeedLock = () => { }, Rcon = _ => "[#0:LOADED]: \"OverStrike2\" (5.0.0)\n", Sleep = _ => { } };
            var tc = new TestCopy(h)
            {
                A2sAsk = _ => new A2sAnswer("srv", "ar_shoots", 0, 1),
                WaitA2s = (want, limit) => (new A2sAnswer("srv", "ar_shoots", 0, want), 1),
            };
            tc.BuildPlugin = s =>
            {
                builds.Add(s);
                return new Dictionary<string, byte[]>(Built);
            };
            string? Refused(Action body) => SelfTest.Refusal(body);
            string Printed(Action body) => SelfTest.Capture(() =>
            {
                body();
                return 0;
            }).Output;
            List<(string Method, string Path, object? Body)> Posts() => calls.Where(c => c.Method is "POST" or "PUT" or "DELETE").ToList();

            var realGit = tc.Git;
            var root = realGit(["rev-list", "--max-parents=0", "HEAD"]).Out.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)[^1];   // the first commit: on main

            // 1. what is refused before anything is built or touched
            containers.Clear();
            containers["cs2"] = Production();
            foreach (var (args, what) in new[] { (Array.Empty<string>(), "no commit"), (["--all"], "an option"), (["0123456789abcdef0123"], "no such commit"), ([root, "x"], "two arguments") })
            {
                calls.Clear();
                builds.Clear();
                var r = Refused(() => tc.CmdPlugin(args));
                check($"plugin refuses {what}, nothing built, nothing sent", r is not null && builds.Count == 0 && Posts().Count == 0);
            }
            tc.Git = args => args[0] == "rev-parse" ? realGit(args) : (1, "");
            calls.Clear();
            var refusal = Refused(() => tc.CmdPlugin([root]));
            check("plugin refuses a commit that is not on main, nothing built", refusal is not null && refusal.Contains("not on main") && builds.Count == 0 && Posts().Count == 0);
            tc.Git = realGit;
            h.NeedLock = () => throw Out.Stop("the server lock is not held by the harness");
            refusal = Refused(() => tc.CmdPlugin([root]));
            check("plugin refuses without the server lock", refusal is not null && refusal.Contains("lock") && builds.Count == 0);
            h.NeedLock = () => { };
            check("commit_of_main: a commit of main gives its full id", tc.CommitOfMain(root) == root);

            // 2. staged while production still runs: only read, never touched
            calls.Clear();
            created.Clear();
            var output = Printed(() => tc.CmdPlugin([root]));
            var helper = created["cs2-plugin-stage"];
            check("plugin: built from the commit asked for", builds.SequenceEqual([root]));
            check("plugin: the helper - the game image as root, no network, production's plugins folder read-only, the staged folder beside it",
                  Json.Str(helper, "Image") == "sha256:gameimg" && Json.Str(helper, "User") == "0:0" && Json.Str(helper, "HostConfig", "NetworkMode") == "none"
                  && Json.Arr(helper, "HostConfig", "Binds").Select(Json.PyStr).SequenceEqual([$"{Plugins}:/src:ro", $"{Stage}:/dst"]));
            check("plugin: the helper carries the test label and the config's extra labels, and is removed",
                  (Json.Str(helper, "Labels", TestCopy.TestLabel) ?? "").StartsWith("helper", StringComparison.Ordinal) && Json.Str(helper, "Labels", "monitor.mute") == "true"
                  && !containers.ContainsKey("cs2-plugin-stage"));
            var puts = calls.Where(c => c.Method == "PUT").ToList();
            var names = new List<string>();
            using (var reader = new TarReader(new MemoryStream((byte[])puts[0].Body!)))
                while (reader.GetNextEntry() is { } e)
                    names.Add(e.Name.TrimEnd('/'));
            names.Sort(StringComparer.Ordinal);
            check("plugin: one upload, the plugin's folder and exactly what ships",
                  puts.Count == 1 && names.SequenceEqual(new[] { "OverStrike2" }.Concat(Built.Keys.Order(StringComparer.Ordinal).Select(n => $"OverStrike2/{n}"))));
            check("plugin: production's game server untouched - no stop, rename, start or exec in it",
                  !Posts().Any(c => c.Path.StartsWith("/containers/cs2/", StringComparison.Ordinal) || c.Path.StartsWith("/containers/cs2?", StringComparison.Ordinal)));
            var rec = (JsonObject)Json.Parse(File.ReadAllText(tc.Staged))!;
            check("plugin: the record - the commit, the folder beside production's, the image, each file's hash, production's other plugin folders",
                  Json.Str(rec, "commit") == root && Json.Str(rec, "dir") == Stage && Json.Str(rec, "image") == "sha256:gameimg"
                  && Json.Same(rec["files"], new JsonObject(Built.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)Sha(kv.Value)))))
                  && Json.Arr(rec, "others").Select(Json.PyStr).SequenceEqual(["Other"]));
            check("plugin: says it, and names no host folder", output.Contains("staged in <plugins>-candidate") && !output.Contains(Plugins));

            // 3. a staged folder that is not the build byte for byte: refused, no record
            File.Delete(tc.Staged);
            corrupt = true;
            refusal = Refused(() => tc.CmdPlugin([root]));
            check("plugin: a staged file that differs from the build - refused, no record", refusal is not null && refusal.Contains("byte for byte") && !File.Exists(tc.Staged));
            corrupt = false;
            Printed(() => tc.CmdPlugin([root]));

            // 4. prep, then run: the copy mounts the staged folder in production's place
            containers["cs2"] = Production();
            Printed(tc.CmdPrep);
            check("prep after a stage: production aside as ever", containers.ContainsKey("cs2-prod-aside") && !containers.ContainsKey("cs2"));
            var b = tc.Body();
            check("run: the copy's plugins folder is the staged one, read-only as production's, every other bind production's",
                  Json.Arr(b, "HostConfig", "Binds").Select(Json.PyStr).SequenceEqual([$"{Stage}:/home/steam/cs2-plugins:ro", "/srv/overstrike2/bridge:/data:ro", "/srv/cs2:/home/steam/cs2-dedicated"]));
            calls.Clear();
            output = Printed(() => tc.CmdRun([]));
            check("run: production's plugins folder in no mount of the copy", !Json.Arr(created["cs2"], "HostConfig", "Binds").Select(Json.PyStr).Any(x => x.StartsWith(Plugins + ":", StringComparison.Ordinal)));
            check("run: the copy runs the staged build - its DLL in the game's tree, the plugin loaded", output.Contains($"plugin on the copy: the build of {root[..12]}") && !output.Contains("LOOK"));

            // 5. the copy's game tree holds another DLL: run says so
            copyTree[$"{Tree}/OverStrike2/OverStrike2.dll"] = "production's plugin"u8.ToArray();
            var (ok, words) = tc.PluginOnCopy(rec);
            check("run's check: another DLL in the copy's game tree is NOT the build", !ok && words.Contains("NOT the build") && words.Contains("another DLL"));

            // 6. plugin-rm while the copy mounts it: refused; after rm: the helper removes the folder by its name
            refusal = Refused(tc.CmdPluginRm);
            check("plugin-rm refuses while the test copy mounts the staged folder", refusal is not null && refusal.Contains("rm it first") && File.Exists(tc.Staged));
            Printed(tc.CmdRm);

            // 7. restore: production back, its own plugins in the game's tree, then the staged folder goes
            containers["cs2-prod-aside"]["State"]!["Status"] = "exited";
            File.WriteAllText(tc.Flag, "x");
            File.WriteAllText(tc.FlagMine, "1");
            created.Remove("cs2-plugin-rm");
            treeDiffers = true;
            output = Printed(tc.CmdRestore);
            check("restore: production's game tree not what its plugins folder holds - the flag left on, the staged folder kept",
                  File.Exists(tc.Flag) && File.Exists(tc.Staged) && !created.ContainsKey("cs2-plugin-rm") && output.Contains("NOT WHAT ITS PLUGINS FOLDER HOLDS"));
            var back = containers["cs2"];
            containers.Remove("cs2");
            containers["cs2-prod-aside"] = back;
            back["State"]!["Status"] = "exited";
            File.WriteAllText(tc.ProdSaved, Json.Dumps(Production()));
            treeDiffers = false;
            Printed(tc.CmdRestore);
            var rm = created.GetValueOrDefault("cs2-plugin-rm") ?? [];
            check("restore: production runs its own plugins - the flag removed, then the staged folder by its name and the record",
                  !File.Exists(tc.Flag) && !File.Exists(tc.Staged) && Json.Arr(rm, "HostConfig", "Binds").Select(Json.PyStr).SequenceEqual(["/srv/overstrike2:/p"])
                  && Json.Arr(rm, "Cmd").Select(Json.PyStr).SequenceEqual(["rm -rf -- '/p/plugins-candidate' && echo removed"]) && Json.Str(rm, "Image") == "sha256:gameimg");

            // 8. plugin-rm with nothing staged; a stale record whose folder is not beside production's; a production on Mounts; a settings text's paths
            check("plugin-rm with nothing staged says so", Refused(tc.CmdPluginRm) is null);
            var stale = (JsonObject)rec.DeepClone();
            stale["dir"] = "/elsewhere/plugins-candidate";
            File.WriteAllText(tc.Staged, Json.Dumps(stale));
            File.WriteAllText(tc.ProdSaved, Json.Dumps(Production()));
            refusal = Refused(() => tc.Body());
            check("run refuses a staged record that is not beside production's plugins folder: no copy that runs production's plugin", refusal is not null && refusal.Contains("REFUSED"));
            File.WriteAllText(tc.Staged, Json.Dumps(rec));
            File.WriteAllText(tc.ProdSaved, Json.Dumps(Production(mounts: true)));
            var m = Json.Arr(tc.Body(), "HostConfig", "Mounts").Select(x => (Json.Str(x, "Source"), Json.Str(x, "Target"), Json.AsBool(Json.At(x, "ReadOnly")))).ToList();
            check("run, a production on Mounts: the plugins mount's source is the staged folder, read-only kept, the rest as they were",
                  m.SequenceEqual([(Stage, "/home/steam/cs2-plugins", true), ("/srv/overstrike2/bridge", "/data", true), ("/srv/cs2", "/home/steam/cs2-dedicated", false)]));
            var movedPaths = Production(text: "{ \"schema\": 1, \"paths\": { \"plugins\": \"/opt/plugins\", \"gameDir\": \"/opt/cs2\" } }");
            ((JsonArray)movedPaths["HostConfig"]!["Binds"]!)[0] = $"{Plugins}:/opt/plugins:ro";
            File.WriteAllText(tc.ProdSaved, Json.Dumps(movedPaths));
            check("run, a settings text that moves paths.plugins: the bind at that path is swapped",
                  Json.PyStr(Json.Arr(tc.Body(), "HostConfig", "Binds")[0]) == $"{Stage}:/opt/plugins:ro" && TestCopy.PluginsAtOf(movedPaths) == "/opt/plugins"
                  && TestCopy.GamePluginsDir(movedPaths) == "/opt/cs2/game/csgo/addons/counterstrikesharp/plugins");
            check("paths without a settings text: the older variables, else the defaults",
                  TestCopy.PluginsAtOf(Json.O("""{"Config": {"Env": ["CS2_PLUGINS_SRC=/p2"]}}""")) == "/p2" && TestCopy.PluginsAtOf(Json.O("""{"Config": {"Env": []}}""")) == TestCopy.PluginsAt
                  && TestCopy.GamePluginsDir(Json.O("""{"Config": {"Env": ["STEAMAPPDIR=/g"]}}""")) == "/g/game/csgo/addons/counterstrikesharp/plugins");
            File.Delete(tc.Staged);
            containers.Clear();
            var noBind = Production();
            noBind["HostConfig"] = new JsonObject { ["Binds"] = new JsonArray("/srv/overstrike2/bridge:/data:ro") };
            containers["cs2"] = noBind;
            File.Delete(tc.ProdSaved);
            refusal = Refused(() => tc.CmdPlugin([root]));
            check("plugin refuses a production without a plugins bind", refusal is not null && refusal.Contains("no bind at /home/steam/cs2-plugins"));
            var copyNamed = Production();
            copyNamed["Config"] = new JsonObject { ["Labels"] = new JsonObject { [TestCopy.TestLabel] = "x" }, ["Env"] = new JsonArray() };
            containers["cs2"] = copyNamed;
            refusal = Refused(() => tc.CmdPlugin([root]));
            check("plugin refuses when the game server's name is a test copy and production's inspect is not saved", refusal is not null && refusal.Contains("neither aside"));
            var entries = new List<(string Name, UnixFileMode Mode)>();
            using (var reader = new TarReader(new MemoryStream(tc.PluginTar(Built))))
                while (reader.GetNextEntry() is { } e)
                    entries.Add((e.Name, e.Mode));
            check("the tar: the folder, then its files, readable by the game's user",
                  Regex.IsMatch(string.Join(" ", entries.Select(e => e.Name)), @"OverStrike2/OverStrike2\.dll") && entries.All(e => (int)e.Mode is 0b110_100_100 or 0b111_101_101));
        }
        finally
        {
            SimConfig.Done(tmp, oldState);
        }
    }
}
