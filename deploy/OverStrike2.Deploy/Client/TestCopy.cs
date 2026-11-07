using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Deploy;

/// <summary>An INSECURE test copy of the production game server, for client tests that must never meet a VAC-secured server
/// (tools/client/probe/testcopy.py, ported). Production's game container is stopped and renamed aside, a test copy is created under
/// production's name from production's own inspect with overrides, and afterwards production is renamed back and started. Production is
/// down meanwhile: only under the server lock, only with nobody on.
///
/// Both prep and rm stop the container with a Docker stop alone, never an RCON quit first: after a quit the restart policy starts the
/// container again underneath, which runs the launcher's start for real.
///
///     testcopy prep             refuses with a human on A2S or without the harness's server lock; maintenance flag on; production's
///                               inspect saved; a Docker stop (no RCON quit), renamed aside to &lt;game&gt;-prod-aside only once it is exited
///     testcopy run [--env K=V] [--downloads]
///                               the test copy: production's inspect with -insecure, the restart watcher's automatic actions off, restart
///                               policy "no", the test labels, production's network endpoint; waits until A2S answers with VAC off. --env K=V
///                               (repeatable) sets one more of the image's variables for this copy alone, never a credential. --downloads keeps
///                               maps.backgroundDownloads at production's value: the restart watcher stays up for the stock's background
///                               downloads alone, into the game's tree, and sends the bridge its ready reports; refused while production's
///                               watcher has a restart of its own to judge (its state's pending record), which the copy's would judge instead.
///                               Production without the settings text gets no copy: nothing else turns the copy's watcher off
///     testcopy status           A2S (VAC flag), which container is the game server, whether production is aside
///     testcopy rm               a Docker stop (no RCON quit) and delete the test copy (refuses a game container without the test label)
///     testcopy restore          production renamed back and started; waits for A2S with VAC ON and a map; checks its network endpoint
///                               against the saved inspect, and the plugins; maintenance flag off (only if this set it)
///
/// A CANDIDATE BRIDGE beside the test copy: production's bridge set aside the same way, a candidate on a copy of the bridge's data.
///     testcopy bridge-prep      after prep: production's bridge inspect saved; a Docker stop; renamed aside to &lt;bridge&gt;-prod-aside
///     testcopy bridge-run &lt;tag&gt; [--keep-flag]   a copy of production's bridge data (a helper container: the game image as root, no network,
///                               production's data read-only) in &lt;data dir&gt;-candidate; then the candidate on &lt;bridge.image&gt;:&lt;tag&gt;
///     testcopy bridge-rm        a Docker stop and delete the candidate (refuses a bridge without the candidate's label), then the copy
///     testcopy bridge-restore   production's bridge renamed back and started; checks it is the same container, on its IP and MAC
/// A test copy run beside a candidate reads the candidate's copy of the bridge's data, read-only as production's game server reads
/// production's (paths.bridgeData): the queues the watcher downloads and the lists the copy reads are what the candidate writes. A candidate
/// started after run leaves the copy on production's data.
///
/// THE PLUGIN OF A COMMIT OF MAIN on the test copy: the copy mounts a staged plugins folder in place of production's, and production's own
/// plugins folder is never written. Production's start copies its own plugins into the game's tree, which restore checks.
///     testcopy plugin &lt;commit&gt;  the plugin built from &lt;commit&gt; (on main: origin/main or main), plugin/, bridge/ and core/ as committed;
///                               what plugin ship ships of it staged by a helper (the game image as root, no network) in &lt;plugins dir&gt;-candidate,
///                               beside production's plugins folder: production's other plugin folders as they are, checked byte for byte
///     testcopy plugin-rm        the staged folder deleted; refused while a test copy mounts it. restore does it once production runs its own
///
/// Prints no secret: the saved inspects (they hold the RCON password and the bridge's secret) stay in the harness's state folder and are
/// deleted once production is itself again.</summary>
sealed class TestCopy
{
    public const string TestLabel = "cs2.test-config";
    public const string Tag = "insecure client-test copy (tools/client/probe/testcopy.py): -insecure, restart watcher off";
    public const string BridgeTag = "candidate bridge (tools/client/probe/testcopy.py): a new image on a copy of the bridge's data";
    public const string Content = Check.Content;
    /// <summary>The copy's overrides of the settings text: with restarts and these maps keys off the watcher exits at once (run --downloads keeps
    /// maps.backgroundDownloads production's: the watcher then stays up for it); maps.layout off: the start's organize (os2-server's MapsFolder)
    /// returns before it touches production's maps folder, the sync's hand-overs and Steam's appworkshop_730.acf - no take-in, no map moved, linked or
    /// deleted, no acf written and no backup beside it - which are production's, in the game's tree the copy shares (a copy took them in early once,
    /// and a candidate os2-server would have edited them with untested code); production's paths stay as they are; no visit to the HUD item's page
    /// from a copy. No program reads anything else that would turn the watcher off - the older programs' variables are read by none.</summary>
    public static JsonObject SettingsOff => Json.O("""
{"restarts": {"updates": false, "newVersions": false, "freezeRecovery": false, "daily": "off", "downloadRestart": false},
 "maps": {"delete": "off", "backgroundDownloads": false, "downloadLine": false, "layout": "off"},
 "hud": {"itemPage": false}}
""");

    public readonly Harness H;
    public readonly string Game, Bridge, Aside, BridgeAside, BridgeRepo, Flag, Host;
    public readonly int Port;
    public readonly Dictionary<string, string> ExtraLabels;
    public readonly string State, ProdSaved, FlagMine, BridgeSaved, Staged, Builds;
    public const string PluginName = "OverStrike2";                   // the plugin's folder, DLL and name
    public const string PluginsAt = "/home/steam/cs2-plugins";        // paths.plugins' default: where the game server reads the plugins to install
    public const string GameDirAt = "/home/steam/cs2-dedicated";      // paths.gameDir's default: the game's install
    public const string BridgeDataAt = "/data";                       // paths.bridgeData's default: the bridge's data, read-only in the game server
    /// <summary>run --downloads: the one switch of SettingsOff a copy may keep at production's value.</summary>
    public static readonly (string Section, string Key) Downloads = ("maps", "backgroundDownloads");

    /// <summary>git -C &lt;repository&gt; args: (exit code, stdout).</summary>
    public Func<string[], (int Code, string Out)> Git = args =>
    {
        var r = Proc.Capture(["git", "-C", Repo.Root, .. args], timeoutSeconds: 300);
        return (r.Code, r.Out);
    };

    /// <summary>The plugin built from a commit: {file name: bytes} of what plugin ship ships of it.</summary>
    public Func<string, Dictionary<string, byte[]>> BuildPlugin;

    public Func<double, A2sAnswer?> A2sAsk;
    public Func<int, double, (A2sAnswer? Answer, double Waited)> WaitA2s;

    /// <summary>The helper container run --downloads reads production's watcher state with: Helper, unless a test puts its own here.</summary>
    public Func<string, IReadOnlyList<string>, string, (int Code, string Output)> HelperRun;

    public TestCopy(Harness h)
    {
        H = h;
        Game = h.Game;
        Bridge = h.BridgeName;
        (Aside, BridgeAside) = (Game + "-prod-aside", Bridge + "-prod-aside");
        BridgeRepo = h.Cfg.Str("bridge.image", "localhost/overstrike2-bridge");
        Flag = Path.Combine(h.Cfg.Str("bridge.data_dir", required: true), "maintenance");
        (Host, Port) = h.HostPort;
        ExtraLabels = h.TestLabels;
        State = Path.Combine(Harness.StateDir, "testcopy");
        ProdSaved = Path.Combine(State, "prod-inspect.json");
        FlagMine = Path.Combine(State, "flag-set-by-testcopy");
        BridgeSaved = Path.Combine(State, "bridge-inspect.json");
        Staged = Path.Combine(State, "plugin-staged.json");            // the staged plugin: its commit, folder, files and their hashes (no secret in it)
        Builds = Path.Combine(State, "plugin-build");                  // the commits' exports and builds, one folder each
        BuildPlugin = RealBuildPlugin;
        A2sAsk = timeout => A2s.Full(Host, Port, timeout);
        WaitA2s = RealWaitA2s;
        HelperRun = (name, binds, script) => Helper(name, binds, script);
    }

    static void Log(params object?[] parts) => Out.Log(parts);

    public JsonObject? Inspect(string name) => H.Inspect(name);

    public A2sAnswer? A2sNow() => A2sAsk(2.0);

    (A2sAnswer?, double) RealWaitA2s(int wantVac, double limit)
    {
        var t0 = H.Now();
        while (H.Now() - t0 < limit)
        {
            var i = A2sNow();
            if (i is not null && i.Vac == wantVac && i.Map.Length > 0)
                return (i, H.Now() - t0);
            H.Sleep(3);
        }
        return (A2sNow(), H.Now() - t0);
    }

    public static bool IsTest(JsonNode? d) => d is not null && Json.Truthy(InspectOf.Labels(d)[TestLabel]);

    /// <summary>A container's network endpoint as its inspect has it, for a create: each network with its pinned IP (IPAMConfig), aliases and MAC.</summary>
    public static JsonObject Endpoints(JsonNode? d)
    {
        var c = Json.Obj(d, "Config");
        var output = new JsonObject();
        foreach (var (net, v) in Json.Obj(d, "NetworkSettings", "Networks"))
        {
            var e = new JsonObject();
            foreach (var k in new[] { "IPAMConfig", "Aliases", "MacAddress" })
                if (Json.Truthy(Json.At(v, k)))
                    e[k] = Json.At(v, k)!.DeepClone();
            if (!Json.Truthy(e["MacAddress"]) && Json.Truthy(c["MacAddress"]))
                e["MacAddress"] = c["MacAddress"]!.DeepClone();
            output[net] = e;
        }
        return output;
    }

    /// <summary>[(network, IP, MAC)] a container is configured with.</summary>
    public static List<NetAddress> Pinned(JsonNode? d) =>
        Json.Obj(d, "NetworkSettings", "Networks")
            .Select(kv => new NetAddress(kv.Key,
                Json.Str(kv.Value, "IPAMConfig", "IPv4Address") is { Length: > 0 } ip ? ip : Json.Str(kv.Value, "IPAddress") is { Length: > 0 } ip2 ? ip2 : "",
                Json.Str(kv.Value, "MacAddress") is { Length: > 0 } mac ? mac : ""))
            .OrderBy(x => x.Net, StringComparer.Ordinal).ThenBy(x => x.Ip, StringComparer.Ordinal).ThenBy(x => x.Mac, StringComparer.Ordinal).ToList();

    static string StatusOf(JsonNode? d) => Json.Str(d, "State", "Status") ?? "";

    /// <summary>A Docker stop of name, never an RCON quit first. Waits until it has exited; returns its inspect.</summary>
    public JsonObject? StopContainer(string name, int t)
    {
        var t0 = H.Now();
        try
        {
            var st = H.Call($"/containers/{name}/stop?t={t}", "POST").Status;
            Log($"{name}: stop -> {st} after {Fmt.Fixed(H.Now() - t0, 0)} s");
        }
        catch (Exception e) when (e is DockerError or IOException or HttpRequestException or TimeoutException or TaskCanceledException)
        {   // the stop goes on in Docker even when the call gives up first: its outcome is read below
            Log($"{name}: stop: {e.Message} - waiting for it to exit");
        }
        var d = Inspect(name);
        while (d is not null && StatusOf(d) != "exited" && H.Now() - t0 < t + 60)
        {
            H.Sleep(2);
            d = Inspect(name);
        }
        return d;
    }

    public void CmdPrep()
    {
        H.NeedLock();
        var d = Inspect(Game) ?? throw Out.Stop($"no {Game} container");
        if (IsTest(d))
            throw Out.Stop($"{Game} is already a test copy - nothing to prepare");
        var i = A2sNow();
        if (i is null || i.Humans > 0)
            throw Out.Stop($"REFUSED: A2S {(i is null ? "does not answer" : $"shows {i.Humans} human(s)")} - production is only set aside with nobody on");
        if (i.Vac != 1)
            throw Out.Stop($"REFUSED: production's A2S says VAC={i.Vac} - expected a VAC-secured production, look first");
        if (Inspect(Aside) is not null)
            throw Out.Stop($"REFUSED: {Aside} already exists - a previous test copy was not restored; run restore first");
        Directory.CreateDirectory(State);
        if (!File.Exists(Flag))
        {
            File.WriteAllText(Flag, $"client-test harness: insecure test copy of the game server, {Out.Clock().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}\n");
            File.WriteAllText(FlagMine, "1");
            Log("maintenance flag set; waiting 12 s for a change already counting down");
            H.Sleep(12);
        }
        else
            Log("maintenance flag already set by someone else - left as it is");
        File.WriteAllText(ProdSaved, Json.Dumps(d, indent: 1));
        Log("production inspect saved", Py.Slice(Json.Str(d, "Id"), 0, 12), "image", Py.Slice(Json.Str(d, "Image"), 7, 19), "net", Py.Repr(Pinned(d)));
        if (StatusOf(d) != "exited")
        {
            Log("production: a Docker stop, no RCON quit (after a quit the restart policy starts it again underneath)");
            var after = StopContainer(Game, 30);
            if (after is null || StatusOf(after) != "exited")
                throw Out.Stop($"REFUSED: production is {(after is null ? "gone" : StatusOf(after))} after the stop - not renamed aside. "
                               + "Look at it first; the maintenance flag is still on.");
            if (!Json.Same(after["RestartCount"], d["RestartCount"]))
                Log($"WARNING: production restarted underneath meanwhile (RestartCount {Json.PyStr(d["RestartCount"])} -> {Json.PyStr(after["RestartCount"])}): "
                    + "read its log before the test - its launcher ran again");
        }
        var renamed = H.Call($"/containers/{Game}/rename?name={Aside}", "POST").Status;
        Log($"production renamed to {Aside} ->", renamed, StatusOf(Inspect(Aside)));
    }

    /// <summary>Production's settings text, an entry of its Env, with SettingsOff set on it - but the (section, key) pairs in keep, which stay
    /// production's. Its comments go, if it has any; references (env:, file:) stay for the copy to read. A text that cannot be read stops here:
    /// a copy without its overrides is production.</summary>
    public static string SettingsForCopy(string entry, IReadOnlyCollection<(string Section, string Key)>? keep = null)
    {
        JsonNode? settings;
        try
        {
            settings = Json.Jsonc(entry.Split('=', 2)[1]);
        }
        catch (JsonSyntaxError)
        {
            settings = null;
        }
        if (settings is not JsonObject s)
            throw Out.Stop("REFUSED: production's settings text cannot be read - no test copy without its overrides");
        foreach (var (section, values) in SettingsOff)
        {
            if (s[section] is not JsonObject target)
            {
                if (!s.ContainsKey(section))
                    s[section] = target = new JsonObject();
                else
                    throw Out.Stop("REFUSED: production's settings text cannot be read - no test copy without its overrides");
            }
            foreach (var (k, v) in (JsonObject)values!)
                if (keep is null || !keep.Contains((section, k)))
                    target[k] = v?.DeepClone();
        }
        return $"{Content}={Json.Dumps(s)}";
    }

    /// <summary>The test copy's create body. downloads: maps.backgroundDownloads stays production's (run --downloads). bridgeCopy: a candidate
    /// bridge's data folder, which the copy reads in the place of production's bridge data.</summary>
    public JsonObject Body(Dictionary<string, string>? extra = null, bool downloads = false, string? bridgeCopy = null)
    {
        var d = Json.Parse(File.ReadAllText(ProdSaved, Encoding.UTF8)) as JsonObject ?? [];
        var c = Json.Obj(d, "Config");
        var h = d["HostConfig"]?.DeepClone() as JsonObject ?? [];
        if (StagedPlugin() is { } staged)   // the staged plugins folder in the place of production's, its options (read-only) kept
        {
            var at = PluginsAtOf(d);
            var src = BindSource(d, at);
            if (string.IsNullOrEmpty(src) || Json.Str(staged, "dir") != CopyDirOrNull(src) || SwapBind(h, at, src, Json.Str(staged, "dir")!) != 1)
                throw Out.Stop($"REFUSED: the staged plugin cannot take the place of production's plugins folder at {at} - no copy, which would run "
                               + "production's plugin (plugin-rm, then plugin <commit> again)");
        }
        if (bridgeCopy is not null)   // beside a candidate bridge: the candidate's copy of the bridge's data in the place of production's, read-only as it is
        {
            var at = BridgeDataAtOf(d);
            var src = BindSource(d, at);
            if (!string.IsNullOrEmpty(src) && (CopyDir(src) != bridgeCopy || SwapBind(h, at, src, bridgeCopy) != 1))
                throw Out.Stop($"REFUSED: the candidate bridge's data is not the copy beside the bridge data production's game server reads at {at} - "
                               + "no copy, which would read production's bridge data beside a candidate");
        }
        if (!Json.Arr(c, "Env").Any(e => Json.PyStr(e).StartsWith(Content + "=", StringComparison.Ordinal)))
            throw Out.Stop("REFUSED: production has no settings text - nothing would turn the copy's restart watcher off, so no copy");
        var over = new Dictionary<string, string>(extra ?? []);
        (string, string)[] keep = downloads ? [Downloads] : [];
        var env = new List<string>();
        foreach (var e in Json.Arr(c, "Env").Select(Json.PyStr))
            if (!over.ContainsKey(e.Split('=', 2)[0]) && !e.StartsWith("CS2_ADDITIONAL_ARGS=", StringComparison.Ordinal))
                env.Add(e.StartsWith(Content + "=", StringComparison.Ordinal) ? SettingsForCopy(e, keep) : e);
        var args = Json.Arr(c, "Env").Select(Json.PyStr).FirstOrDefault(e => e.StartsWith("CS2_ADDITIONAL_ARGS=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? "";
        env.AddRange(over.Select(kv => $"{kv.Key}={kv.Value}"));
        env.Add($"CS2_ADDITIONAL_ARGS={Py.Strip(args + " -insecure")}");
        var labels = Json.Obj(c, "Labels").DeepClone() as JsonObject ?? [];
        foreach (var (k, v) in ExtraLabels)
            labels[k] = v;
        labels[TestLabel] = Tag;
        labels["com.docker.compose.config-hash"] = "test-config-not-from-main";   // any redeploy recreates it from main
        h["RestartPolicy"] = new JsonObject { ["Name"] = "no", ["MaximumRetryCount"] = 0 };
        h.Remove("ConsoleSize");
        return new JsonObject
        {
            ["Image"] = d["Image"]?.DeepClone(), ["Env"] = new JsonArray(env.Select(e => (JsonNode?)e).ToArray()), ["Cmd"] = c["Cmd"]?.DeepClone(),
            ["Entrypoint"] = c["Entrypoint"]?.DeepClone(), ["User"] = c["User"]?.DeepClone(), ["WorkingDir"] = c["WorkingDir"]?.DeepClone(), ["Labels"] = labels,
            ["ExposedPorts"] = c["ExposedPorts"]?.DeepClone(), ["StopTimeout"] = c["StopTimeout"]?.DeepClone(), ["AttachStdout"] = true, ["AttachStderr"] = true,
            ["Tty"] = Json.Truthy(c["Tty"]), ["OpenStdin"] = Json.Truthy(c["OpenStdin"]), ["HostConfig"] = h,
            ["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = Endpoints(d) },
        };
    }

    /// <summary>run's --env K=V pairs: more of the image's variables for this copy alone. Never a credential (those stay production's).</summary>
    public static Dictionary<string, string> EnvOverrides(IReadOnlyList<string> argv)
    {
        var output = new Dictionary<string, string>();
        for (var i = 0; i < argv.Count; i++)
        {
            var a = argv[i];
            string? kv = a == "--env" ? (i + 1 < argv.Count ? argv[++i] : "") : a.StartsWith("--env=", StringComparison.Ordinal) ? a[6..] : null;
            if (kv is null)
                throw Out.Stop($"run: unknown argument {Py.Repr(a)}");
            var eq = kv.IndexOf('=');
            var (k, v) = eq < 0 ? (kv, "") : (kv[..eq], kv[(eq + 1)..]);
            if (eq < 0 || !Regex.IsMatch(k, "^[A-Z][A-Z0-9_]*$") || Regex.IsMatch(k, "PASS|SECRET|TOKEN|RCON|KEY|_PW$") || k is "CS2_ADDITIONAL_ARGS" or Content)
                throw Out.Stop($"run: --env takes K=V for one of the image's ordinary variables, not {Py.Repr(k.Length > 0 ? k : kv)}");
            output[k] = v;
        }
        return output;
    }

    /// <summary>Where the game server reads the bridge's data (paths.bridgeData).</summary>
    public static string BridgeDataAtOf(JsonNode? d) => SettingPath(d, "bridgeData", BridgeDataAt);

    /// <summary>Whether production's restart watcher has a restart of its own recorded - update-watch/state.json's "pending", which the next
    /// watcher to start judges: true, false (no record, or no state yet), null when it cannot be read. Read by a helper, the game's tree
    /// read-only.</summary>
    public bool? WatcherPending(JsonNode? d)
    {
        var src = BindSource(d, SettingPath(d, "gameDir", GameDirAt, "STEAMAPPDIR"));
        if (string.IsNullOrEmpty(src))
            return null;
        var (code, output) = HelperRun($"{Game}-watch-read", [$"{src}:/g:ro"], "cat /g/update-watch/state.json 2>/dev/null || echo '{}'");
        JsonNode? state;
        try
        {
            state = code == 0 ? Json.Parse(output) : null;
        }
        catch (Exception e) when (e is JsonSyntaxError or System.Text.Json.JsonException)
        {
            state = null;
        }
        return state is JsonObject o ? o.TryGetPropertyValue("pending", out var p) && p is not null : null;
    }

    /// <summary>run --downloads: production's own maps.backgroundDownloads, in words. Stops when a copy cannot keep it: no settings text, or a
    /// restart of production's watcher still to be judged (the copy's watcher would judge it in its place, with the copy's versions).</summary>
    public string DownloadsWords(JsonNode? d)
    {
        var text = InspectOf.EnvOrNull(d, Content) ?? throw Out.Stop("REFUSED: --downloads keeps a value of production's settings text, and production has none");
        const string unreadable = "REFUSED: production's settings text cannot be read - no test copy without its overrides";
        JsonNode? settings;
        try
        {
            settings = Json.Jsonc(text);
        }
        catch (JsonSyntaxError)
        {
            throw Out.Stop(unreadable);
        }
        // as Python reads ((settings or {}).get("maps") or {}).get("backgroundDownloads", True): an empty value counts as none, any other
        // that is no object cannot be read
        var maps = !Json.Truthy(settings) ? null : settings is JsonObject so ? so["maps"] : throw Out.Stop(unreadable);
        var value = !Json.Truthy(maps) ? JsonValue.Create(true)
                    : maps is JsonObject mo ? (mo.TryGetPropertyValue("backgroundDownloads", out var v) ? v : JsonValue.Create(true))
                    : throw Out.Stop(unreadable);
        var pending = WatcherPending(d);
        if (pending is null)
            throw Out.Stop("REFUSED: --downloads: production's restart watcher state (update-watch/state.json) cannot be read - no watcher on the copy");
        if (pending == true)
            throw Out.Stop("REFUSED: --downloads: production's restart watcher has a restart of its own to judge (update-watch/state.json, pending) - "
                           + "a copy's watcher would judge it in its place; run the copy without --downloads");
        if (value is JsonValue jv && jv.TryGetValue<bool>(out var on) && on)
            return "maps.backgroundDownloads at production's value (on): the restart watcher stays up for the background downloads alone and reports to the bridge";
        return $"maps.backgroundDownloads is {Json.Dumps(value)} in production too: the copy's restart watcher still has nothing to do and exits";
    }

    public void CmdRun(IReadOnlyList<string> argv)
    {
        H.NeedLock();
        var downloads = argv.Contains("--downloads");
        var extra = EnvOverrides(argv.Where(a => a != "--downloads").ToList());
        if (Inspect(Aside) is null)
            throw Out.Stop("REFUSED: production is not aside - run prep first");
        if (Inspect(Game) is not null)
            throw Out.Stop($"REFUSED: a {Game} container exists");
        var prod = Json.Parse(File.ReadAllText(ProdSaved, Encoding.UTF8));
        var cand = Inspect(Bridge);
        var bridgeCopy = IsTest(cand) ? DataMount(cand) : null;
        if (IsTest(cand) && string.IsNullOrEmpty(bridgeCopy))
            throw Out.Stop("REFUSED: the candidate bridge has no bind at /data - no copy beside it");
        var said = downloads ? DownloadsWords(prod) : null;
        if (extra.Count > 0)
            Log("for this copy alone:", string.Join(", ", extra.Select(kv => $"{kv.Key}={kv.Value}")));
        var body = Body(extra, downloads, bridgeCopy);
        if (Json.Arr(body, "Env").Any(e => Json.PyStr(e).StartsWith(Content + "=", StringComparison.Ordinal)))
            Log("production holds the settings text: the copy has it with",
                string.Join(", ", SettingsOff.SelectMany(s => ((JsonObject)s.Value!).Where(k => !(downloads && (s.Key, k.Key) == Downloads)).Select(k => $"{s.Key}.{k.Key}"))), "overridden");
        if (said is not null)
            Log(said);
        if (bridgeCopy is not null)
            Log(!string.IsNullOrEmpty(BindSource(prod, BridgeDataAtOf(prod)))
                ? "beside the candidate bridge: the copy reads its copy of the bridge's data (<data>-candidate) in the place of production's"
                : "beside the candidate bridge: production's game server reads no bridge data, nor does the copy");
        var stagedNow = StagedPlugin();
        Log(stagedNow is not null ? $"plugin: the build of {Py.Slice(Json.Str(stagedNow, "commit"), 0, 12)}, staged, in the place of production's plugins folder" : "plugin: production's");
        var created = H.Call($"/containers/create?name={Game}", "POST", body);
        Log("create test copy ->", created.Status, Py.Slice(Json.Str(created.Json, "Id") ?? "", 0, 12));
        var st = H.Call($"/containers/{Game}/start", "POST").Status;
        Log("start ->", st, "net", Py.Repr(Pinned(Inspect(Game))));
        var (i, t) = WaitA2s(0, 480);
        Log($"A2S after {Fmt.Fixed(t, 0)} s:", Py.Repr(i), i is { Vac: 0 } ? "- VAC OFF, the insecure test copy is up" : "- NOT an insecure server: check before any insecure client joins");
        if (stagedNow is not null && i is not null)
        {
            H.Sleep(5);
            var (ok, words) = PluginOnCopy(stagedNow);
            Log(ok ? words : words + " - LOOK before testing: the copy does not run the staged plugin");
        }
    }

    public void CmdStatus()
    {
        Out.Print("lock: " + (Py.SplitLines(H.LockText() is { Length: > 0 } t ? t : "free").FirstOrDefault() ?? ""));
        var b = Inspect(Bridge);
        Out.Print($"{Bridge}: " + (b is null ? "none" : $"{(IsTest(b) ? "CANDIDATE" : "production")} {StatusOf(b)} image {Py.Slice(Json.Str(b, "Image"), 7, 19)}"));
        var ba = Inspect(BridgeAside);
        Out.Print($"{BridgeAside}: " + (ba is null ? "none" : StatusOf(ba)));
        var d = Inspect(Game);
        Out.Print($"{Game}: " + (d is null ? "none" : $"{(IsTest(d) ? "TEST COPY" : "production")} {StatusOf(d)} image {Py.Slice(Json.Str(d, "Image"), 7, 19)}"));
        var a = Inspect(Aside);
        Out.Print($"{Aside}: " + (a is null ? "none" : StatusOf(a)));
        Out.Print($"A2S: {Py.Repr(A2sNow())} (name, map, humans, VAC)");
        Out.Print($"maintenance flag: {Py.Str(File.Exists(Flag))} {(File.Exists(FlagMine) ? "(set by testcopy)" : "")}");
        var s = StagedPlugin();
        Out.Print("plugin for a copy: " + (s is not null ? $"the build of {Py.Slice(Json.Str(s, "commit"), 0, 12)}, staged {Json.Str(s, "staged")} ({Json.Obj(s, "files").Count} files)" : "production's (none staged)"));
    }

    public void CmdRm()
    {
        H.NeedLock();
        var d = Inspect(Game);
        if (d is null)
        {
            Log($"no {Game} container");
            return;
        }
        if (!IsTest(d))
            throw Out.Stop($"REFUSED: {Game} is NOT a test copy");
        if (StatusOf(d) != "exited")
            StopContainer(Game, 10);   // no RCON quit: a copy with a restart policy would start again
        H.Call($"/containers/{Game}?force=true", "DELETE");
        Log("test copy removed");
    }

    public void CmdRestore()
    {
        H.NeedLock();
        if (Inspect(Game) is not null)
            throw Out.Stop($"REFUSED: a {Game} container exists - rm the test copy first");
        if (Inspect(Aside) is null)
            throw Out.Stop($"REFUSED: no {Aside}");
        var b = Inspect(Bridge);
        if (Inspect(BridgeAside) is not null || IsTest(b))
            throw Out.Stop("REFUSED: production's bridge is not back - bridge-rm and bridge-restore first (production never meets a candidate bridge)");
        var saved = File.Exists(ProdSaved) ? Json.Parse(File.ReadAllText(ProdSaved, Encoding.UTF8)) as JsonObject : null;
        Log($"{Aside} -> {Game}:", H.Call($"/containers/{Aside}/rename?name={Game}", "POST").Status);
        Log("start ->", H.Call($"/containers/{Game}/start", "POST").Status);
        var (i, t) = WaitA2s(1, 600);
        Log($"A2S after {Fmt.Fixed(t, 0)} s:", Py.Repr(i), i is { Vac: 1 } ? "- production, VAC ON" : "- PRODUCTION NOT BACK AS EXPECTED: look now");
        var d = Inspect(Game);
        var okNet = saved is not null && Json.Str(d, "Id") == Json.Str(saved, "Id") && Pinned(d).SequenceEqual(Pinned(saved));
        Log("production", Py.Slice(Json.Str(d, "Id"), 0, 12), StatusOf(d), IsTest(d) ? "test label" : "no test label", "net", Py.Repr(Pinned(d)),
            okNet ? "OK: as saved at prep" : $"NOT AS SAVED AT PREP ({(saved is not null ? Py.Repr(Pinned(saved)) : "no saved inspect")})");
        H.Sleep(5);
        var plugins = Py.SplitLines(H.Rcon(["css_plugins list"])).Where(l => l.Contains("LOADED", StringComparison.Ordinal)).Select(Py.Strip).ToList();
        Log("plugins:", Py.Repr(plugins));
        var (okPlugins, installed) = PluginsAsInstalled(d);   // its start copied its own plugins over whatever a copy ran
        Log("production's plugins in the game's tree:", installed, okPlugins ? "- OK: what its plugins folder holds" : "- NOT WHAT ITS PLUGINS FOLDER HOLDS: look now");
        if (File.Exists(FlagMine))
        {
            if (i is { Vac: 1 } && okNet && okPlugins)
            {
                File.Delete(Flag);
                File.Delete(FlagMine);
                Log("maintenance flag removed");
            }
            else
                Log("maintenance flag LEFT ON: production is not verified");
        }
        if (okNet)
            File.Delete(ProdSaved);   // it holds secrets; production is itself again
        if (StagedPlugin() is { } stagedThen && okNet && okPlugins)
            RemoveStaged(stagedThen);
    }

    // -------------------------------------------------------------------------------------------------- the candidate bridge

    /// <summary>The host folder a container has at target (a bind), or null.</summary>
    public static string? BindSource(JsonNode? d, string target)
    {
        target = target.TrimEnd('/');
        foreach (var b in Json.Arr(d, "HostConfig", "Binds").Select(Json.PyStr))
        {
            var colon = b.IndexOf(':');
            var (src, rest) = colon < 0 ? (b, "") : (b[..colon], b[(colon + 1)..]);
            if (rest.Split(':')[0].TrimEnd('/') == target)
                return src;
        }
        foreach (var m in Json.Arr(d, "HostConfig", "Mounts"))
            if ((Json.Str(m, "Target") ?? "").TrimEnd('/') == target && Json.Str(m, "Type") == "bind")
                return Json.Str(m, "Source");
        return Json.Arr(d, "Mounts").Where(m => (Json.Str(m, "Destination") ?? "").TrimEnd('/') == target && Json.Str(m, "Type") == "bind")
                   .Select(m => Json.Str(m, "Source")).FirstOrDefault();
    }

    /// <summary>The host folder a bridge container has at /data (a bind), or null.</summary>
    public static string? DataMount(JsonNode? d) => BindSource(d, "/data");

    /// <summary>Where the candidate's copy goes: beside production's data, &lt;src&gt;-candidate. Never production's own folder.</summary>
    public static string CopyDir(string? src)
    {
        if (string.IsNullOrEmpty(src) || !src.StartsWith('/') || src.Split('/').Contains("..") || src.TrimEnd('/') is "" or "/mnt")
            throw Out.Stop($"REFUSED: cannot place a copy beside {Py.Repr(src)}");
        return src.TrimEnd('/') + "-candidate";
    }

    static string? CopyDirOrNull(string? src)
    {
        try
        {
            return CopyDir(src);
        }
        catch (Exit)
        {
            return null;
        }
    }

    /// <summary>One short-lived container from the game image, as root, without a network: runs script with binds, is removed, and returns
    /// (exit code, its output).</summary>
    public (int Code, string Output) Helper(string name, IReadOnlyList<string> binds, string script, string? image = null)
    {
        image ??= Json.Str(Json.Parse(File.ReadAllText(ProdSaved, Encoding.UTF8)), "Image") ?? "";
        var labelObject = new JsonObject();
        foreach (var (k, v) in ExtraLabels)
            labelObject[k] = v;
        labelObject[TestLabel] = "helper: " + name;
        var created = H.Call($"/containers/create?name={name}", "POST", new JsonObject
        {
            ["Image"] = image, ["User"] = "0:0", ["Entrypoint"] = new JsonArray("/bin/bash", "-c"), ["Cmd"] = new JsonArray(script), ["Labels"] = labelObject,
            ["HostConfig"] = new JsonObject { ["Binds"] = new JsonArray(binds.Select(b => (JsonNode?)b).ToArray()), ["NetworkMode"] = "none", ["RestartPolicy"] = new JsonObject { ["Name"] = "no" } },
        });
        var cid = Json.Str(created.Json, "Id") ?? "";
        try
        {
            H.Call($"/containers/{cid}/start", "POST");
            var waitedFor = H.Call($"/containers/{cid}/wait", "POST", timeout: 600);
            var code = (int)(Json.AsLong(Json.At(waitedFor.Json, "StatusCode")) ?? -1);
            var logs = H.Call($"/containers/{cid}/logs?stdout=1&stderr=1");
            return (code, Py.Strip(Harness.Demux(logs.Body)));
        }
        finally
        {
            H.Call($"/containers/{cid}?force=true", "DELETE");
        }
    }

    /// <summary>The candidate: production's bridge inspect with the new image, its /data on the copy, restart policy "no", the test labels and
    /// production's network endpoint (IP, MAC, aliases). The environment is what compose set; the new image brings its own.</summary>
    public JsonObject BridgeBody(JsonObject d, string image, string dataCopy)
    {
        var c = Json.Obj(d, "Config");
        var h = d["HostConfig"]?.DeepClone() as JsonObject ?? [];
        var imageEnv = Json.Arr(H.Call($"/images/{Json.Str(d, "Image")}/json").Json, "Config", "Env").Select(Json.PyStr).ToHashSet(StringComparer.Ordinal);
        var src = DataMount(d) ?? "";
        if (h["Binds"] is JsonArray binds)
            h["Binds"] = new JsonArray(binds.Select(b => Json.PyStr(b)).Select(b => (JsonNode?)(b.StartsWith(src + ":", StringComparison.Ordinal) ? dataCopy + b[src.Length..] : b)).ToArray());
        else
            h["Binds"] = new JsonArray();
        foreach (var m in Json.Arr(h, "Mounts").OfType<JsonObject>())
            if (Json.Str(m, "Target") == "/data")
                m["Source"] = dataCopy;
        h["RestartPolicy"] = new JsonObject { ["Name"] = "no", ["MaximumRetryCount"] = 0 };
        h.Remove("ConsoleSize");
        var labels = new JsonObject();
        foreach (var (k, v) in Json.Obj(c, "Labels"))
            if (!k.StartsWith("org.opencontainers.image.", StringComparison.Ordinal))
                labels[k] = v?.DeepClone();
        foreach (var (k, v) in ExtraLabels)
            labels[k] = v;
        labels[TestLabel] = BridgeTag;
        labels["com.docker.compose.config-hash"] = "test-config-not-from-main";
        var body = new JsonObject
        {
            ["Image"] = image, ["Env"] = new JsonArray(Json.Arr(c, "Env").Select(Json.PyStr).Where(e => !imageEnv.Contains(e)).Select(e => (JsonNode?)e).ToArray()),
            ["Labels"] = labels, ["HostConfig"] = h, ["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = Endpoints(d) },
        };
        foreach (var k in new[] { "User", "ExposedPorts", "StopTimeout", "Tty", "OpenStdin" })
            if (!Json.Emptyish(c[k]))
                body[k] = c[k]!.DeepClone();
        return body;
    }

    public void CmdBridgePrep()
    {
        H.NeedLock();
        if (Inspect(Aside) is null || !File.Exists(ProdSaved))
            throw Out.Stop("REFUSED: production's game server is not aside - run prep first (a candidate bridge only beside a test copy)");
        var d = Inspect(Bridge) ?? throw Out.Stop($"no {Bridge} container");
        if (IsTest(d))
            throw Out.Stop($"REFUSED: {Bridge} is a candidate already - bridge-rm first");
        if (Inspect(BridgeAside) is not null)
            throw Out.Stop($"REFUSED: {BridgeAside} already exists - a previous candidate was not restored; run bridge-restore first");
        if (DataMount(d) is null)
            throw Out.Stop($"REFUSED: {Bridge} has no bind at /data - nothing to copy");
        Directory.CreateDirectory(State);
        File.WriteAllText(BridgeSaved, Json.Dumps(d, indent: 1));
        Log("production's bridge inspect saved", Py.Slice(Json.Str(d, "Id"), 0, 12), "image", Py.Slice(Json.Str(d, "Image"), 7, 19));
        if (StatusOf(d) != "exited")
        {
            var after = StopContainer(Bridge, 15);
            if (after is null || StatusOf(after) != "exited")
                throw Out.Stop($"REFUSED: production's bridge is {(after is null ? "gone" : StatusOf(after))} after the stop - not renamed aside");
        }
        var st = H.Call($"/containers/{Bridge}/rename?name={BridgeAside}", "POST").Status;
        Log($"production's bridge renamed to {BridgeAside} ->", st, StatusOf(Inspect(BridgeAside)));
    }

    public void CmdBridgeRun(IReadOnlyList<string> argv)
    {
        H.NeedLock();
        var keepFlag = argv.Contains("--keep-flag");
        var tags = argv.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (tags.Count != 1 || argv.Any(a => a.StartsWith("--", StringComparison.Ordinal) && a != "--keep-flag") || !Regex.IsMatch(tags[0], @"^[\w.-]+$"))
            throw Out.Stop("usage: os2-deploy testcopy bridge-run <image tag> [--keep-flag]");
        var image = $"{BridgeRepo}:{tags[0]}";
        if (Inspect(BridgeAside) is null || !File.Exists(BridgeSaved))
            throw Out.Stop("REFUSED: production's bridge is not aside - run bridge-prep first");
        if (Inspect(Bridge) is not null)
            throw Out.Stop($"REFUSED: a {Bridge} container exists");
        try
        {
            H.Call($"/images/{image}/json");
        }
        catch (Exception e) when (e is DockerError or IOException or HttpRequestException)
        {
            throw Out.Stop($"REFUSED: no image {image} on the server ({e.Message})");
        }
        var d = Json.Parse(File.ReadAllText(BridgeSaved, Encoding.UTF8)) as JsonObject ?? [];
        var src = DataMount(d);
        var dst = CopyDir(src);
        var owner = Json.Str(d, "Config", "User") ?? "";
        var script = "set -e; find /dst -mindepth 1 -delete; cp -a /src/. /dst/; " + (keepFlag ? "" : "rm -f /dst/maintenance; ")
                     + (Regex.IsMatch(owner, @"^\d+(:\d+)?$") ? $"chown {owner} /dst; " : "")
                     + "echo files: $(find /dst -type f | wc -l), $(du -sh /dst | cut -f1); ls -ln /dst/state.json 2>/dev/null || echo 'no state.json'";
        var (code, output) = Helper($"{Bridge}-copy", [$"{src}:/src:ro", $"{dst}:/dst"], script);
        Log($"copy of the bridge's data -> <data>-candidate: exit {code}; " + string.Join(" | ", Py.SplitLines(output).TakeLast(2)));
        if (code != 0)
            throw Out.Stop("REFUSED: the copy failed - no candidate started");
        var created = H.Call($"/containers/create?name={Bridge}", "POST", BridgeBody(d, image, dst));
        Log("create candidate ->", created.Status, Py.Slice(Json.Str(created.Json, "Id") ?? "", 0, 12), image);
        var st = H.Call($"/containers/{Bridge}/start", "POST").Status;
        H.Sleep(5);
        var now = Inspect(Bridge);
        Log("start ->", st, StatusOf(now), "restarts", Json.PyStr(now?["RestartCount"]), "net", Py.Repr(Pinned(now)),
            Pinned(now).SequenceEqual(Pinned(d)) ? "- the same as production's" : $"- NOT production's {Py.Repr(Pinned(d))}");
    }

    public void CmdBridgeRm()
    {
        H.NeedLock();
        var d = Inspect(Bridge);
        if (d is not null)
        {
            if (Json.AsStr(InspectOf.Labels(d)[TestLabel]) != BridgeTag)
                throw Out.Stop($"REFUSED: {Bridge} is NOT a candidate");
            if (StatusOf(d) != "exited")
                StopContainer(Bridge, 10);
            H.Call($"/containers/{Bridge}?force=true", "DELETE");
            Log("candidate bridge removed");
        }
        else
            Log($"no {Bridge} container");
        if (File.Exists(BridgeSaved))
        {
            var dst = CopyDir(DataMount(Json.Parse(File.ReadAllText(BridgeSaved, Encoding.UTF8))));
            var slash = dst.LastIndexOf('/');
            var (parent, name) = (dst[..slash], dst[(slash + 1)..]);
            var (code, output) = Helper($"{Bridge}-copy-rm", [$"{parent}:/p"], $"rm -rf -- '/p/{name}' && echo removed");
            Log($"the copy <data>-candidate: exit {code}, {(Py.SplitLines(output).LastOrDefault() ?? "?")}");
        }
    }

    public void CmdBridgeRestore()
    {
        H.NeedLock();
        if (Inspect(Bridge) is not null)
            throw Out.Stop($"REFUSED: a {Bridge} container exists - bridge-rm the candidate first");
        if (Inspect(BridgeAside) is null)
            throw Out.Stop($"REFUSED: no {BridgeAside}");
        var saved = File.Exists(BridgeSaved) ? Json.Parse(File.ReadAllText(BridgeSaved, Encoding.UTF8)) as JsonObject : null;
        Log($"{BridgeAside} -> {Bridge}:", H.Call($"/containers/{BridgeAside}/rename?name={Bridge}", "POST").Status);
        H.Call($"/containers/{Bridge}/start", "POST");
        H.Sleep(5);
        var d = Inspect(Bridge);
        var ok = StatusOf(d) == "running" && !IsTest(d) && (saved is null || (Json.Str(d, "Id") == Json.Str(saved, "Id") && Pinned(d).SequenceEqual(Pinned(saved))));
        Log($"production's bridge {Py.Slice(Json.Str(d, "Id"), 0, 12)} {StatusOf(d)}, image {Py.Slice(Json.Str(d, "Image"), 7, 19)}, net {Py.Repr(Pinned(d))}",
            ok ? "- OK" : "- NOT AS SAVED: look now");
        if (ok)
            File.Delete(BridgeSaved);   // it holds secrets; production's bridge is itself again
    }

    // ---------------------------------------------------------------------------------------------------- the plugin of a commit

    /// <summary>paths.&lt;key&gt; as the game server reads it: its settings text, else (no text: the older programs) the variable older, else the
    /// default.</summary>
    static string SettingPath(JsonNode? d, string key, string dflt, string? older = null)
    {
        var text = InspectOf.EnvOrNull(d, Content);
        string? value;
        if (text is not null)
        {
            try
            {
                value = Json.AsStr(Json.At(Json.At(Json.Jsonc(text), "paths"), key));
            }
            catch (JsonSyntaxError)
            {
                value = null;
            }
        }
        else
            value = older is not null ? InspectOf.EnvOrNull(d, older) : null;
        return value is not null && value.StartsWith('/') ? value.TrimEnd('/') : dflt;
    }

    /// <summary>Where the game server reads the plugins it installs at every start (paths.plugins).</summary>
    public static string PluginsAtOf(JsonNode? d) => SettingPath(d, "plugins", PluginsAt, "CS2_PLUGINS_SRC");

    /// <summary>CounterStrikeSharp's plugins in the game's tree, where every start copies them to (paths.gameDir, os2-server's CopyPlugins).</summary>
    public static string GamePluginsDir(JsonNode? d) => SettingPath(d, "gameDir", GameDirAt, "STEAMAPPDIR") + "/game/csgo/addons/counterstrikesharp/plugins";

    /// <summary>HostConfig h with its bind of src at target taken from newSrc instead, its options kept; how many it changed.</summary>
    static int SwapBind(JsonObject h, string target, string src, string newSrc)
    {
        target = target.TrimEnd('/');
        var n = 0;
        if (h["Binds"] is JsonArray binds && binds.Count > 0)
        {
            var output = new JsonArray();
            foreach (var b in binds.Select(Json.PyStr))
            {
                var colon = b.IndexOf(':');
                var (s, rest) = colon < 0 ? (b, "") : (b[..colon], b[(colon + 1)..]);
                if (s == src && rest.Split(':')[0].TrimEnd('/') == target)
                {
                    output.Add(newSrc + ":" + rest);
                    n++;
                }
                else
                    output.Add(b);
            }
            h["Binds"] = output;
        }
        foreach (var m in Json.Arr(h, "Mounts").OfType<JsonObject>())
        {
            if (Json.Str(m, "Type") == "bind" && Json.Str(m, "Source") == src && (Json.Str(m, "Target") ?? "").TrimEnd('/') == target)
            {
                m["Source"] = newSrc;
                n++;
            }
        }
        return n;
    }

    /// <summary>The staged plugin's record, or null.</summary>
    public JsonObject? StagedPlugin() => File.Exists(Staged) ? Json.Parse(File.ReadAllText(Staged, Encoding.UTF8)) as JsonObject : null;

    /// <summary>Production's game server: the inspect saved at prep while it is aside, else the running container (only read); never a copy's.</summary>
    JsonObject ProductionInspect()
    {
        if (File.Exists(ProdSaved) && Inspect(Aside) is not null)
            return Json.Parse(File.ReadAllText(ProdSaved, Encoding.UTF8)) as JsonObject ?? [];
        var d = Inspect(Game);
        if (d is null || IsTest(d))
            throw Out.Stop($"REFUSED: production's game server is neither aside (with the inspect prep saved) nor {Game} - no plugin staged");
        return d;
    }

    /// <summary>The full id of rev, which must be a commit on main (origin/main or main); refused otherwise.</summary>
    public string CommitOfMain(string rev)
    {
        var (code, output) = Git(["rev-parse", "--verify", "--quiet", rev + "^{commit}"]);
        var sha = Py.Strip(output);
        if (code != 0 || !Regex.IsMatch(sha, "^[0-9a-f]{40}$"))
            throw Out.Stop($"REFUSED: {Py.Repr(rev)} is no commit of this repository");
        if (!new[] { "origin/main", "main" }.Any(r => Git(["merge-base", "--is-ancestor", sha, r]).Code == 0))
            throw Out.Stop($"REFUSED: {sha[..12]} is not on main (origin/main or main) - only a commit of main goes on a test copy");
        return sha;
    }

    /// <summary>{file name: bytes}: the plugin built from commit sha as committed - plugin/, bridge/ and core/ exported with git archive into the
    /// state folder and built there with dotnet build -c Release (warnings are errors), its output in a log file, not a pipe - and of its output
    /// what plugin ship ships: the DLL, its .pdb and the config files the project declares.</summary>
    Dictionary<string, byte[]> RealBuildPlugin(string sha)
    {
        var where = Path.Combine(Builds, sha[..12]);
        if (Directory.Exists(Builds))
            foreach (var old in Directory.EnumerateDirectories(Builds))   // one build kept: this one, and its log
                SelfTest.Remove(old);
        Directory.CreateDirectory(where);
        var tar = Proc.CaptureBytes(["git", "-C", Repo.Root, "archive", "--format=tar", sha, "plugin", "bridge", "core"]);
        if (tar.Code != 0)
            throw Out.Stop($"REFUSED: git archive {sha[..12]} failed");
        System.Formats.Tar.TarFile.ExtractToDirectory(new MemoryStream(tar.Out), where, overwriteFiles: true);
        Log($"building the plugin of {sha[..12]} (dotnet build -c Release; its log: build.log in the state folder)");
        var code = Proc.RunToFile($"dotnet build plugin/{PluginName} -c Release -nologo -nodeReuse:false -p:UseSharedCompilation=false", where, "build.log", below: true);
        if (code != 0)
        {
            var tail = Py.SplitLines(File.ReadAllText(Path.Combine(where, "build.log"))).TakeLast(12);
            throw Out.Stop($"REFUSED: the plugin of {sha[..12]} does not build:\n" + string.Join("\n", tail));
        }
        var outDir = Path.Combine(where, "plugin", PluginName, "bin", "Release", "net10.0");
        var declared = Plugin.DeclaredContent(File.ReadAllText(Path.Combine(where, "plugin", PluginName, PluginName + ".csproj"), Encoding.UTF8));
        List<string> names = [PluginName + ".dll", PluginName + ".pdb", .. declared.Where(n => File.Exists(Path.Combine(outDir, n)))];
        if (!names.Take(2).All(n => File.Exists(Path.Combine(outDir, n))))
            throw Out.Stop($"REFUSED: the build of {sha[..12]} left no {PluginName}.dll and .pdb");
        return names.ToDictionary(n => n, n => File.ReadAllBytes(Path.Combine(outDir, n)));
    }

    /// <summary>The plugin's folder as a tar: &lt;PluginName&gt;/ and its files.</summary>
    public byte[] PluginTar(Dictionary<string, byte[]> files)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds((long)H.Now());
        var buf = new MemoryStream();
        using (var tar = new System.Formats.Tar.TarWriter(buf, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.Directory, PluginName) { Mode = (UnixFileMode)0b111_101_101, ModificationTime = now });
            foreach (var (name, data) in files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, $"{PluginName}/{name}")
                {
                    Mode = (UnixFileMode)0b110_100_100, ModificationTime = now, DataStream = new MemoryStream(data),
                });
        }
        return buf.ToArray();
    }

    /// <summary>A short-lived container from the game image, as root and without a network, held open (it sleeps) for several commands and a
    /// file upload; removed on the way out, whatever happened.</summary>
    sealed class HelperSession : IDisposable
    {
        readonly TestCopy tc;
        readonly string cid;

        public HelperSession(TestCopy tc, string name, string image, IReadOnlyList<string> binds)
        {
            this.tc = tc;
            var labels = new JsonObject();
            foreach (var (k, v) in tc.ExtraLabels)
                labels[k] = v;
            labels[TestLabel] = "helper: " + name;
            var created = tc.H.Call($"/containers/create?name={name}", "POST", new JsonObject
            {
                ["Image"] = image, ["User"] = "0:0", ["Entrypoint"] = new JsonArray("/bin/bash", "-c"), ["Cmd"] = new JsonArray("sleep 900"), ["Labels"] = labels,
                ["HostConfig"] = new JsonObject { ["Binds"] = new JsonArray(binds.Select(b => (JsonNode?)b).ToArray()), ["NetworkMode"] = "none", ["RestartPolicy"] = new JsonObject { ["Name"] = "no" } },
            });
            cid = Json.Str(created.Json, "Id") ?? "";
            if (cid.Length == 0)
                throw Out.Stop($"REFUSED: the helper {name} was not created ({created.Status})");
            tc.H.Call($"/containers/{cid}/start", "POST");
        }

        /// <summary>(exit code, output) of one bash script in the helper.</summary>
        public (int Code, string Output) Run(string script)
        {
            var output = tc.H.ExecIn(cid, ["/bin/bash", "-c", "( " + script + " ) 2>&1; echo EXIT=$?"], timeout: 600);
            var m = Regex.Match(output, @"EXIT=(\d+)\s*$");
            return (m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1, Py.Strip(Regex.Replace(output, @"EXIT=\d+\s*$", "")));
        }

        public void Put(byte[] data, string path) => tc.H.Call($"/containers/{cid}/archive?path={path}", "PUT", raw: data, ctype: "application/x-tar", timeout: 300);

        public void Dispose() => tc.H.Call($"/containers/{cid}?force=true", "DELETE");
    }

    public void CmdPlugin(IReadOnlyList<string> argv)
    {
        H.NeedLock();
        if (argv.Count != 1 || argv[0].StartsWith('-'))
            throw Out.Stop("usage: os2-deploy testcopy plugin <commit of main>");
        var sha = CommitOfMain(argv[0]);
        var prod = ProductionInspect();
        var at = PluginsAtOf(prod);
        var src = BindSource(prod, at);
        if (string.IsNullOrEmpty(src))
            throw Out.Stop($"REFUSED: production's game server has no bind at {at}, its plugins folder - nothing to take the place of");
        var dst = CopyDir(src);
        var files = BuildPlugin(sha);
        var want = files.ToDictionary(kv => $"./{PluginName}/{kv.Key}", kv => Repo.Sha256(kv.Value));
        Log("built: " + string.Join(", ", files.Select(kv => $"{kv.Key} ({kv.Value.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes)")));
        int code;
        string output;
        using (var h = new HelperSession(this, $"{Game}-plugin-stage", Json.Str(prod, "Image") ?? "", [$"{src}:/src:ro", $"{dst}:/dst"]))
        {
            (code, output) = h.Run($"set -e; find /dst -mindepth 1 -delete; cp -a /src/. /dst/; rm -rf -- '/dst/{PluginName}'");
            if (code != 0)
                throw Out.Stop($"REFUSED: the staged folder could not be made (exit {code}): {Py.Slice(output, -300)}");
            h.Put(PluginTar(files), "/dst");
            (code, output) = h.Run("set -e; chown -R --reference=/src /dst; chmod -R u+rwX,go+rX,go-w /dst; cd /dst; "
                                   + "find . -type f -print0 | sort -z | xargs -0 -r sha256sum");
        }
        var got = new Dictionary<string, string>();
        foreach (var l in Py.SplitLines(output).Where(l => Regex.IsMatch(l, @"^[0-9a-f]{64}\s+\S.*$")))
        {
            var parts = l.Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            got[Py.Strip(parts[1])] = parts[0];
        }
        var mine = got.Where(kv => kv.Key.StartsWith($"./{PluginName}/", StringComparison.Ordinal)).ToDictionary();
        if (code != 0 || mine.Count != want.Count || mine.Any(kv => want.GetValueOrDefault(kv.Key) != kv.Value))
            throw Out.Stop($"REFUSED: the staged folder does not hold the build byte for byte (exit {code}; {mine.Count(kv => want.GetValueOrDefault(kv.Key) == kv.Value)} of {want.Count} files as built) - "
                           + "run plugin-rm, then plugin again");
        var others = got.Keys.Where(k => !k.StartsWith($"./{PluginName}/", StringComparison.Ordinal)).Select(k => k.Split('/')[1]).Distinct().Order(StringComparer.Ordinal).ToList();
        Directory.CreateDirectory(State);
        File.WriteAllText(Staged, Json.Dumps(new JsonObject
        {
            ["commit"] = sha, ["dir"] = dst, ["image"] = Json.Str(prod, "Image"),
            ["staged"] = Out.Clock().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            ["files"] = new JsonObject(files.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)Repo.Sha256(kv.Value)))),
            ["others"] = new JsonArray(others.Select(o => (JsonNode?)o).ToArray()),
        }, indent: 1));
        Log($"staged in <plugins>-candidate: {PluginName} of {sha[..12]} ({files.Count} files, each as built)"
            + (others.Count > 0 ? $", production's other plugin folders as they are: {string.Join(", ", others)}" : ", no other plugin folder")
            + ". The next run mounts it in the place of production's plugins folder");
    }

    /// <summary>(ok, words): whether the test copy's game tree holds the staged DLL and CounterStrikeSharp has the plugin loaded.</summary>
    public (bool Ok, string Words) PluginOnCopy(JsonObject staged)
    {
        var d = Inspect(Game);
        var output = Py.Strip(H.ExecIn(Game, ["sha256sum", $"{GamePluginsDir(d)}/{PluginName}/{PluginName}.dll"]));
        var have = output.Length > 0 ? output.Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)[0] : "";
        var loaded = Py.SplitLines(H.Rcon(["css_plugins list"])).Where(l => l.Contains(PluginName, StringComparison.Ordinal) && l.Contains("LOADED", StringComparison.Ordinal)
                                                                            && !l.Contains("UNLOADED", StringComparison.Ordinal)).Select(Py.Strip).ToList();
        var same = have == Json.Str(staged, "files", PluginName + ".dll");
        var ok = same && loaded.Count > 0;
        return (ok, $"plugin on the copy: {(ok ? "the" : "NOT the")} build of {Py.Slice(Json.Str(staged, "commit"), 0, 12)} - "
                    + $"{(same ? "its" : "another")} DLL in the game's tree, {(loaded.Count > 0 ? loaded[0] : PluginName + " not loaded")}");
    }

    /// <summary>shlex.quote: a word bash reads as itself.</summary>
    static string ShQuote(string s) =>
        s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || "@%+=:,./-_".Contains(c)) ? s : "'" + s.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    /// <summary>(ok, words): whether the game's tree holds what this container's plugins folder holds, file for file - its start copies them there.</summary>
    (bool Ok, string Words) PluginsAsInstalled(JsonNode? d)
    {
        var (at, tree) = (PluginsAtOf(d), GamePluginsDir(d));
        var script = $"cd {ShQuote(at)} || exit 3; n=0; bad=0; while IFS= read -r -d '' f; do n=$((n+1)); "
                     + $"cmp -s \"$f\" {ShQuote(tree)}/\"${{f#./}}\" || {{ bad=$((bad+1)); echo \"DIFF ${{f#./}}\"; }}; "
                     + "done < <(find . -mindepth 2 -type f ! -path './.*' -print0); echo \"files $n differ $bad\"";
        var output = Py.Strip(H.ExecIn(Game, ["bash", "-c", script]));
        var m = Regex.Match(output, @"files (\d+) differ (\d+)");
        var words = string.Join(" | ", Py.SplitLines(output).TakeLast(4));
        return (m.Success && m.Groups[2].Value == "0", words.Length > 0 ? words : "no answer");
    }

    /// <summary>The staged folder deleted by a helper (its parent mounted, the folder by its own name), and the record.</summary>
    void RemoveStaged(JsonObject staged)
    {
        var dir = (Json.Str(staged, "dir") ?? "").TrimEnd('/');
        var slash = dir.LastIndexOf('/');
        var (parent, name) = slash < 0 ? ("", dir) : (dir[..slash], dir[(slash + 1)..]);
        if (!name.EndsWith("-candidate", StringComparison.Ordinal) || !parent.StartsWith('/'))
            throw Out.Stop($"REFUSED: {Py.Repr(Json.Str(staged, "dir"))} is not a staged folder");
        var (code, output) = Helper($"{Game}-plugin-rm", [$"{parent}:/p"], $"rm -rf -- '/p/{name}' && echo removed", image: Json.Str(staged, "image"));
        Log($"the staged plugin <plugins>-candidate: exit {code}, {(Py.SplitLines(output).LastOrDefault() ?? "?")}");
        if (code == 0)
            File.Delete(Staged);
    }

    public void CmdPluginRm()
    {
        H.NeedLock();
        var s = StagedPlugin();
        if (s is null)
        {
            Log("no plugin staged");
            return;
        }
        var d = Inspect(Game);
        if (IsTest(d) && BindSource(d, PluginsAtOf(d)) == Json.Str(s, "dir"))
            throw Out.Stop("REFUSED: the test copy mounts the staged folder - rm it first");
        RemoveStaged(s);
    }


    public const string Doc = "testcopy prep | run [--env K=V] [--downloads] | status | rm | restore | bridge-prep | bridge-run <tag> [--keep-flag] | bridge-rm | bridge-restore"
                              + " | plugin <commit of main> | plugin-rm";

    public static int Command(string[] argv)
    {
        var (args, config) = Harness.TakeConfig(argv);
        string[] verbs = ["prep", "run", "status", "rm", "restore", "bridge-prep", "bridge-run", "bridge-rm", "bridge-restore", "plugin", "plugin-rm"];
        if (args.Count == 0 || !verbs.Contains(args[0]))
        {
            Out.Print("usage: os2-deploy " + Doc);
            return args.Count == 0 ? 0 : 1;
        }
        var tc = new TestCopy(new Harness(config));
        var rest = args.Skip(1).ToList();
        switch (args[0])
        {
            case "prep": tc.CmdPrep(); break;
            case "run": tc.CmdRun(rest); break;
            case "status": tc.CmdStatus(); break;
            case "rm": tc.CmdRm(); break;
            case "restore": tc.CmdRestore(); break;
            case "bridge-prep": tc.CmdBridgePrep(); break;
            case "bridge-run": tc.CmdBridgeRun(rest); break;
            case "bridge-rm": tc.CmdBridgeRm(); break;
            case "bridge-restore": tc.CmdBridgeRestore(); break;
            case "plugin": tc.CmdPlugin(rest); break;
            case "plugin-rm": tc.CmdPluginRm(); break;
        }
        return 0;
    }
}
