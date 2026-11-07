using System.Globalization;
using System.Text.Json.Nodes;

namespace OverStrike2.Deploy;

/// <summary>deploy/test_release.py, ported: both release commands against stand-ins - a Docker API whose RCON, exec and container starts are
/// canned, A2S, the commands a release runs, the lock and the maintenance flag in a scratch folder. Nothing reaches a server, Steam or a share.</summary>
static class ReleaseTests
{
    const string Hud = "3823993566";
    static readonly Dictionary<string, string> Files = new()
    {
        ["OverStrike2.dll"] = new string('a', 64), ["OverStrike2.pdb"] = new string('b', 64), ["blocked.txt"] = new string('c', 64),
    };

    static string scratch = "", verifyOk = "", verifyBad = "";

    /// <summary>The calls a release makes: inspect, and exec in the game container - RCON, the files' hashes, the plugin's log. A quit
    /// restarts the game container; os_hud page writes its probe line to the log.</summary>
    sealed class FakeDocker(Test t) : DockerBase
    {
        public readonly List<(string Command, string Password, bool Flag)> RconSent = [];
        public string Started = "start-1";
        int n = 1;
        public string Loaded = "[#2:LOADED]: \"OverStrike2\" (4.7.1) by the authors";
        public readonly Dictionary<string, string> Hashes = new(Files);
        public string ProbeAnswer = "page HTTP 500, title 'Steam Workshop' -> NOT shown; Web API ok";
        public bool OldPlugin;   // a plugin from before release 191, which knows os_hud by its old word only
        public readonly List<string> Log = [];
        public Dictionary<string, string> Lists = new() { ["mm_extra_addons"] = "", ["mm_client_extra_addons"] = Hud };
        public string[] Env = ["CS2_RCONPW=pw-secret", "RCON_PORT=27015"];
        public string[] BridgeEnv = [];   // a bridge on the settings file: no secret in its environment
        public string BridgeImage = "sha256:image-old-0000";

        public override DockerReply Call(string method, string path, JsonNode? body = null, byte[]? raw = null, string ctype = "application/json", double timeout = 60) =>
            throw new InvalidOperationException($"an unexpected call: {method} {path}");

        static JsonArray Arr(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)i).ToArray());

        public override JsonObject? Inspect(string kind, string name)
        {
            if (kind == "images")
                return name.EndsWith(":rel", StringComparison.Ordinal) ? Json.O("""{"Id": "sha256:image-new-0000", "Config": {"Labels": {"org.opencontainers.image.revision": "abc123"}}}""") : null;
            if (name == "game")
                return new JsonObject { ["Id"] = "c-game", ["State"] = new JsonObject { ["Running"] = true, ["StartedAt"] = Started }, ["Config"] = new JsonObject { ["Env"] = Arr(Env) } };
            if (name == "bridge")
                return new JsonObject { ["Id"] = "c-bridge", ["Image"] = BridgeImage, ["State"] = new JsonObject { ["Running"] = true }, ["Config"] = new JsonObject { ["Env"] = Arr(BridgeEnv) } };
            return null;
        }

        public override string ExecIn(string container, IReadOnlyList<string> argv, double timeout = 60)
        {
            if (argv[2] == Bridge.InnerRcon)
            {
                var (pw, command) = (argv[3], argv[4]);
                RconSent.Add((command, pw, File.Exists(t.Flag)));
                if (command == "status")
                    return string.Create(CultureInfo.InvariantCulture, $"  players  : {t.RconHumans} humans, 2 bots (16 max)\n");
                if (command == "quit")
                {
                    n++;
                    Started = $"start-{n}";
                    return "";
                }
                if (command == "css_plugins list")
                    return Loaded + "\n[#1:LOADED]: \"Other\" (1.0)\n";
                if (Lists.TryGetValue(command, out var list))
                    return $"{command} = {list}\n";
                if (command.StartsWith(OldPlugin ? "css_gghud page" : "os_hud page", StringComparison.Ordinal))
                {
                    Log.Add($"OverStrike2 HUD item probe: {Release.NoSuchItem}: {ProbeAnswer}");
                    return "probing ... the answer goes to the server log";
                }
                if (command.StartsWith(OldPlugin ? "os_hud " : "css_gghud ", StringComparison.Ordinal))
                    return $"Unknown command '{command.Split(' ')[0]}'!";
                return "";
            }
            if (argv[2] == Release.InnerSha)
                return Json.Dumps(new JsonObject(argv.Skip(4).Where(Hashes.ContainsKey).Select(f => KeyValuePair.Create(f, (JsonNode?)Hashes[f])))) + "\n";
            if (argv[2] == Release.InnerGrep)
                return string.Join("\n", Log.Where(l => l.Contains(argv[4], StringComparison.Ordinal)));
            return "(exec start 500)";
        }
    }

    sealed class Test
    {
        public readonly string Dir, Flag, Lock;
        readonly List<int?> humans;
        public readonly int RconHumans;
        public readonly List<List<string>> Runs = [];
        public readonly List<double> Sleeps = [];
        public readonly List<(string Tag, string Keep)> Swaps = [];
        public readonly List<(string Item, int Sleeps)> Api = [];
        readonly string? verify;
        public readonly FakeDocker D;
        public Func<List<string>, int> RunFn;

        public Test(int?[]? humans = null, int rconHumans = 0, string? lockBy = null, string verify = "ok")
        {
            Dir = Path.Combine(scratch, "release-test-" + Path.GetRandomFileName().Replace(".", "", StringComparison.Ordinal));
            Directory.CreateDirectory(Dir);
            Flag = Path.Combine(Dir, "maintenance");
            Lock = Path.Combine(Dir, "server.lock");
            if (lockBy is not null)
                File.WriteAllText(Lock, $"owner: {lockBy}\nsince: x\n");
            this.humans = [.. humans ?? [0, 0, 0, 0]];
            RconHumans = rconHumans;
            this.verify = verify switch { "ok" => verifyOk, "bad" => verifyBad, "missing" => Path.Combine(scratch, "no-such-check.py"), _ => null };
            D = new FakeDocker(this);
            RunFn = argv =>
            {
                Runs.Add(argv);
                return argv[^1] == verifyBad ? 1 : 0;
            };
        }

        int? Humans()
        {
            if (humans.Count > 1)
            {
                var h = humans[0];
                humans.RemoveAt(0);
                return h;
            }
            return humans[0];
        }

        public Release Release(bool dry = false, Func<string, JsonObject?>? fetch = null, string? repoDir = null)
        {
            var cfg = new MemoryConfig(new()
            {
                ["bridge.game_container"] = "game", ["bridge.container"] = "bridge", ["bridge.image"] = "localhost/overstrike2-bridge",
                ["server.lock_file"] = Lock, ["bridge.data_dir"] = Dir, ["server.address"] = "192.0.2.1:27015", ["release.verify"] = verify,
            });
            var rel = new Release(cfg, d: D, humans: Humans, run: argv => RunFn(argv), sleep: Sleeps.Add, fetch: fetch, dry: dry)
            {
                PluginFiles = () => new Dictionary<string, string>(Files),
                PluginVersion = () => "4.7.1",
            };
            rel.Swap = (tag, keep) =>
            {
                Swaps.Add((tag, keep));
                D.BridgeImage = "sha256:image-new-0000";
                return 0;
            };
            if (repoDir is not null)
                rel.RepoDir = repoDir;
            return rel;
        }
    }

    /// <summary>The body's exit code (or "STOP " and the stop's words) and what it printed.</summary>
    static (string R, string Out) Go(Func<int> body)
    {
        var (r, _, output) = SelfTest.Capture(() =>
        {
            try
            {
                return body().ToString(CultureInfo.InvariantCulture);
            }
            catch (ReleaseStop stop)
            {
                return "STOP " + stop.Message;
            }
        });
        return (r ?? "", output);
    }

    static List<string> Sent(Test t) => t.D.RconSent.Select(c => c.Command).ToList();

    public static void Run()
    {
        var check = SelfTest.Check;
        scratch = SelfTest.Scratch("test-release-");
        verifyOk = Path.Combine(scratch, "check-ok.py");
        verifyBad = Path.Combine(scratch, "check-bad.py");
        foreach (var path in new[] { verifyOk, verifyBad })
            File.WriteAllText(path, "raise SystemExit(0)\n");
        try
        {
            // ---- live, the whole way
            var t = new Test();
            var rel = t.Release();
            var (r, output) = Go(() => rel.Live("rel", true, false));
            var sent = Sent(t);
            check("live: exit 0, the bridge swapped to that exact tag, keeping :previous", r == "0" && t.Swaps.SequenceEqual([("rel", "previous")]));
            check("live: the plugin shipped with deploy/plugin.py ship --apply", t.Runs.Any(a => a.Count >= 3 && a[^2] == "ship" && a[^1] == "--apply" && a[^3] == "plugin"));
            check("live: the flag was on at the quit, and is off now", t.D.RconSent.Where(c => c.Command == "quit").Select(c => c.Flag).SequenceEqual([true]) && !File.Exists(t.Flag));
            check("live: 0 humans checked before the release and again right before the quit", sent.Count(c => c == "status") == 2 && sent.IndexOf("quit") > sent.IndexOf("status"));
            check("live: the lock taken and released", !File.Exists(t.Lock) && output.Contains("server lock taken") && output.Contains("server lock released"));
            check("live: RCON with the game container's own password, never printed", t.D.RconSent.All(c => c.Password == "pw-secret") && !output.Contains("pw-secret"));
            check("live: the record's lines - the image, the plugin LOADED, the addon lists, the hashes, the deployment's check",
                  output.Contains("RECORD - live") && output.Contains("sha256:image-new-0000") && output.Contains("\"OverStrike2\" (4.7.1)")
                  && output.Contains("`mm_client_extra_addons = " + Hud + "`") && output.Contains("hashes those of the build")
                  && output.Contains("the deployment's check: ALL GOOD") && t.Runs[^1][^1] == verifyOk);

            // ---- the HUD mounted by the server (mm_extra_addons); the item is hud/workshop_id.txt, whatever the container's variables say
            check("the HUD item is the repository's hud/workshop_id.txt", new Test().Release().WorkshopItem() == Hud);
            t = new Test();
            t.D.Lists = new() { ["mm_extra_addons"] = Hud, ["mm_client_extra_addons"] = "" };
            t.D.Env = ["CS2_RCONPW=pw-secret", "OS2_HUD_ADDON=1234", "GUNGAME_HUD_ADDON=5678"];
            (r, output) = Go(() => t.Release().Live("rel", true, false));
            check("live: the HUD item mounted by the server passes; OS2_HUD_ADDON and GUNGAME_HUD_ADDON mean nothing any more",
                  r == "0" && output.Contains("`mm_extra_addons = " + Hud + "`"));
            t = new Test();
            t.D.Lists = new() { ["mm_extra_addons"] = "1234", ["mm_client_extra_addons"] = "5678" };
            t.D.Env = ["CS2_RCONPW=pw-secret", "OS2_HUD_ADDON=1234", "GUNGAME_HUD_ADDON=5678"];
            (r, output) = Go(() => t.Release().Live("rel", true, false));
            check("live: the items the variables name, handed out in place of the one HUD item - stopped", r.StartsWith("STOP the server hands out no HUD item " + Hud, StringComparison.Ordinal));
            var noId = Path.Combine(scratch, "no-id");
            Directory.CreateDirectory(Path.Combine(noId, "hud"));
            File.WriteAllText(Path.Combine(noId, "hud", "workshop_id.txt"), "\n");
            t = new Test();
            (r, output) = Go(() => t.Release(repoDir: noId).Live("rel", true, false));
            check("live: a workshop_id.txt that names no item - refused before the lock, the swap or the copy",
                  r.StartsWith("STOP REFUSED: hud/workshop_id.txt names no item", StringComparison.Ordinal) && t.Swaps.Count == 0 && t.Runs.Count == 0 && !File.Exists(t.Lock));

            // ---- the bridge's environment and the RCON password
            t = new Test();
            t.D.BridgeEnv = ["RCON_PASSWORD=from-the-bridge", "LOG_SECRET=from-the-bridge"];
            (r, output) = Go(() => t.Release().Live("rel", true, false));
            check("live: the game container's CS2_RCONPW before the bridge's RCON_PASSWORD", r == "0" && t.D.RconSent.All(c => c.Password == "pw-secret"));
            t = new Test();
            (t.D.Env, t.D.BridgeEnv) = (["RCON_PORT=27015"], ["RCON_PASSWORD=from-the-bridge"]);
            (r, output) = Go(() => t.Release().Live("rel", true, false));
            check("live: a game container with no CS2_RCONPW - the bridge's RCON_PASSWORD, never printed",
                  r == "0" && t.D.RconSent.All(c => c.Password == "from-the-bridge") && !output.Contains("from-the-bridge"));
            t = new Test();
            t.D.Lists = new() { ["mm_extra_addons"] = "", ["mm_client_extra_addons"] = "" };
            (r, output) = Go(() => t.Release().Live("rel", true, false));
            check("live: the configured HUD item in neither list - stopped, the flag off, the lock kept",
                  r.StartsWith("STOP the server hands out no HUD item " + Hud, StringComparison.Ordinal) && !File.Exists(t.Flag) && File.Exists(t.Lock));

            // ---- live refuses: somebody on, by A2S or by RCON
            foreach (var (label, humans, rh) in new[] { ("A2S shows 1", new int?[] { 1, 0 }, 0), ("A2S 0 at first, 1 ten seconds later", [0, 1], 0), ("RCON's status shows 1", [0, 0], 1) })
            {
                t = new Test(humans, rh);
                (r, output) = Go(() => t.Release().Live("rel", true, false));
                check($"live refused - {label}: nothing done (no lock, swap, copy, flag or quit)",
                      r.StartsWith("STOP REFUSED: somebody is on", StringComparison.Ordinal) && t.Swaps.Count == 0 && t.Runs.Count == 0 && !File.Exists(t.Lock)
                      && !File.Exists(t.Flag) && !Sent(t).Contains("quit"));
            }

            // ---- the lock: held by somebody else; --under-lock uses it and leaves it
            t = new Test(lockBy: "somebody else's test");
            (r, output) = Go(() => t.Release().Live("rel", true, false));
            check("live refused - the lock is held: nothing done", r.StartsWith("STOP REFUSED: the server lock is held", StringComparison.Ordinal) && t.Swaps.Count == 0 && t.Runs.Count == 0);
            (r, output) = Go(() => t.Release().Live("rel", true, true));
            check("live --under-lock: done, and the lock is left as it was", r == "0" && t.Swaps.Count > 0 && File.ReadAllText(t.Lock).StartsWith("owner: somebody else's", StringComparison.Ordinal));
            t = new Test();
            (r, output) = Go(() => t.Release().Live("rel", true, true));
            check("live --under-lock with a free lock: refused", r.StartsWith("STOP REFUSED: --under-lock, but the server lock is free", StringComparison.Ordinal) && t.Swaps.Count == 0);

            // ---- failures after the quit: the flag off, the lock kept
            foreach (var (label, spoil, want) in new (string, Action<FakeDocker>, string)[]
                     {
                         ("the plugin not LOADED", d => d.Loaded = "[#2:FAILED]: \"OverStrike2\" (4.7.1)", "is not LOADED"),
                         ("an old plugin version", d => d.Loaded = "[#2:LOADED]: \"OverStrike2\" (4.7.0)", "is not LOADED"),
                         ("a file not the build's", d => d.Hashes["blocked.txt"] = new string('d', 64), "not the build's: blocked.txt"),
                         ("another HUD addon", d => d.Lists["mm_client_extra_addons"] = "1234", "hands out no HUD item"),
                     })
            {
                t = new Test();
                spoil(t.D);
                rel = t.Release();
                var current = rel;
                (r, output) = Go(() => current.Live("rel", true, false));
                check($"live, {label}: stopped ({Py.Slice(r, 5, 70)}), the flag off, the lock kept",
                      r.StartsWith("STOP ", StringComparison.Ordinal) && r.Contains(want) && !File.Exists(t.Flag) && File.Exists(t.Lock) && rel.TookLock);
            }

            // ---- somebody joins between the ship and the quit: no quit
            t = new Test([0, 0, 0, 1]);
            rel = t.Release();
            (r, output) = Go(() => rel.Live("rel", true, false));
            check("live: a join right before the quit - no quit, the flag off, the lock kept",
                  r.StartsWith("STOP REFUSED: somebody is on", StringComparison.Ordinal) && !Sent(t).Contains("quit") && !File.Exists(t.Flag) && File.Exists(t.Lock));

            // ---- the deployment's own check
            t = new Test(verify: "bad");
            rel = t.Release();
            (r, output) = Go(() => rel.Live("rel", true, false));
            check("live: the deployment's check says look - exit 1, the lock kept", r == "1" && output.Contains("the deployment's check: LOOK") && File.Exists(t.Lock));
            t = new Test(verify: "missing");
            (r, output) = Go(() => t.Release().Live("rel", true, false));
            check("live: a check named in the config but missing - exit 1 (NOT RUN), the lock kept", r == "1" && output.Contains("the deployment's check: NOT RUN") && File.Exists(t.Lock));
            t = new Test(verify: "none");
            (r, output) = Go(() => t.Release().Live("rel", true, false));
            List<string> selfCheck = [.. Proc.Self(), "check", "verify", "--config", Path.GetFullPath("the-private-config.toml")];
            check("live: no release.verify - deploy/check.py verify runs, on the config this release runs with; ALL GOOD, the lock released",
                  r == "0" && t.Runs[^1].SequenceEqual(selfCheck) && File.Exists(t.Runs[^1][0]) && output.Contains("the deployment's check: ALL GOOD") && !File.Exists(t.Lock));
            var env = Release.ChildEnv(new MemoryConfig([], "the-private-config.toml"));
            check("the deploy scripts a release runs read the release's own config: OVERSTRIKE2_CONFIG, made absolute, the rest of the environment kept",
                  env is not null && env["OVERSTRIKE2_CONFIG"] == Path.GetFullPath("the-private-config.toml") && env.GetValueOrDefault("PATH") == Environment.GetEnvironmentVariable("PATH")
                  && Release.ChildEnv(new MemoryConfig([], null)) is null);
            t = new Test(verify: "none");
            var self = Proc.Self();
            var tt = t;
            t.RunFn = argv =>
            {
                tt.Runs.Add(argv);
                return argv.Skip(self.Count).Take(2).SequenceEqual(["check", "verify"]) ? 1 : 0;
            };
            (r, output) = Go(() => t.Release().Live("rel", true, false));
            check("live: deploy/check.py verify says look - exit 1, the lock kept", r == "1" && output.Contains("the deployment's check: LOOK") && File.Exists(t.Lock));

            // ---- --no-plugin: the swap alone
            t = new Test();
            (r, output) = Go(() => t.Release().Live("rel", false, false));
            check("live --no-plugin: the swap and the check only - no copy, no flag, no quit",
                  r == "0" && t.Swaps.SequenceEqual([("rel", "previous")]) && !t.Runs.Any(a => a.Contains("plugin"))
                  && !Sent(t).Contains("quit") && !output.Contains("maintenance flag") && output.Contains("the deployment's check: ALL GOOD"));

            // ---- --dry-run: reads only
            t = new Test();
            (r, output) = Go(() => t.Release(dry: true).Live("rel", true, false));
            check("live --dry-run: the checks, and nothing written", r == "0" && t.Swaps.Count == 0 && t.Runs.Count == 0 && !File.Exists(t.Lock)
                  && !File.Exists(t.Flag) && Sent(t).SequenceEqual(["status"]) && output.Contains("dry run: would swap"));
            t = new Test();
            (r, output) = Go(() => t.Release().Live("nosuch", true, false));
            check("live: an image tag the server does not have is refused first", r.StartsWith("STOP REFUSED: no image", StringComparison.Ordinal) && t.D.RconSent.Count == 0);

            // ---- publish
            var runDir = Path.Combine(scratch, "release-test-run");
            Directory.CreateDirectory(Path.Combine(runDir, "shots"));
            Func<string, JsonObject?> Fetches(Test test, params long[] times)
            {
                var seq = times.ToList();
                return item =>
                {
                    test.Api.Add((item, test.Sleeps.Count));
                    long tu;
                    if (seq.Count > 1)
                    {
                        tu = seq[0];
                        seq.RemoveAt(0);
                    }
                    else
                        tu = seq[0];
                    return new JsonObject { ["result"] = 1, ["visibility"] = 0, ["file_size"] = 1, ["time_updated"] = tu };
                };
            }
            t = new Test();
            (r, output) = Go(() => t.Release(fetch: Fetches(t, 100, 100, 200, 200, 200, 300)).Publish("v9: what changed", runDir, false, 120));
            check("publish: the held-item check read NOT shown, then the upload with the note and the update's flag",
                  r == "0" && t.Runs.Any(a => a.Count >= 4 && a.TakeLast(4).SequenceEqual(["hud-publish", "--note", "v9: what changed", "--user-approved-update"]))
                  && Sent(t).Contains("os_hud page " + Release.NoSuchItem));
            check("publish: time_updated polled one call at a time, a minute apart, until it moved twice",
                  t.Api.Count == 6 && t.Sleeps.Count(s => s == 60) == 5 && output.Contains("the upload: time_updated") && output.Contains("Valve's approval, most likely"));
            check("publish: the record's line, and the lock released", output.Contains("RECORD - publish") && output.Contains("release-test-run") && !File.Exists(t.Lock));
            t = new Test();
            t.D.OldPlugin = true;
            (r, output) = Go(() => t.Release(fetch: Fetches(t, 100, 100, 200, 200, 200, 300)).Publish("v9: what changed", runDir, false, 120));
            var asked = Sent(t);
            check("publish: a live plugin from before release 191 answers os_hud as unknown - the held-item check asks it by its old word, and reads NOT shown",
                  r == "0" && asked.Contains("os_hud page " + Release.NoSuchItem) && asked.Contains("css_gghud page " + Release.NoSuchItem)
                  && asked.IndexOf("os_hud page " + Release.NoSuchItem) < asked.IndexOf("css_gghud page " + Release.NoSuchItem) && output.Contains("held-item check: css_gghud page"));
            t = new Test();
            t.D.ProbeAnswer = "page HTTP 200, title 'Steam Workshop :: x' -> shown to everybody";
            (r, output) = Go(() => t.Release(fetch: Fetches(t, 100)).Publish("note", runDir, false, 120));
            check("publish refused - the held-item check does not read NOT shown: no upload, the lock released",
                  r.StartsWith("STOP REFUSED: the held-item check", StringComparison.Ordinal) && t.Runs.Count == 0 && !File.Exists(t.Lock));
            t = new Test();
            (r, output) = Go(() => t.Release(fetch: Fetches(t, 100)).Publish("note", Path.Combine(scratch, "no-such-run"), false, 120));
            check("publish refused - no client test run with shots", r.StartsWith("STOP REFUSED: --client-test", StringComparison.Ordinal) && t.Runs.Count == 0 && t.D.RconSent.Count == 0);
            t = new Test();
            (r, output) = Go(() => t.Release(fetch: Fetches(t, 100, 100, 200)).Publish("note", runDir, false, 3));
            check("publish: no approval within --wait-minutes - says so, the lock released", r.StartsWith("STOP time_updated moved 1 time(s)", StringComparison.Ordinal) && !File.Exists(t.Lock));
            t = new Test();
            (r, output) = Go(() => t.Release(dry: true, fetch: Fetches(t, 100)).Publish("note", runDir, false, 120));
            check("publish --dry-run: the held-item check, no upload, no lock", r == "0" && t.Runs.Count == 0 && !File.Exists(t.Lock) && output.Contains("dry run: would publish"));
        }
        finally
        {
            SelfTest.Remove(scratch);
        }
    }
}
