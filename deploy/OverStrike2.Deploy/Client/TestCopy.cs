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
///     testcopy run [--env K=V]  the test copy: production's inspect with -insecure, the restart watcher's automatic actions off, restart
///                               policy "no", the test labels, production's network endpoint; waits until A2S answers with VAC off
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
///
/// Prints no secret: the saved inspects (they hold the RCON password and the bridge's secret) stay in the harness's state folder and are
/// deleted once production is itself again.</summary>
sealed class TestCopy
{
    public const string TestLabel = "cs2.test-config";
    public const string Tag = "insecure client-test copy (tools/client/probe/testcopy.py): -insecure, restart watcher off";
    public const string BridgeTag = "candidate bridge (tools/client/probe/testcopy.py): a new image on a copy of the bridge's data";
    public static readonly Dictionary<string, string> Off = new()
    {
        ["CS2_FREEZE_RECOVERY"] = "0", ["CS2_AUTOUPDATE"] = "0", ["CS2_RESTART_AT"] = "off", ["CS2_WARMUP"] = "0", ["CS2_PREFETCH_AT"] = "off",
        ["CS2_PREFETCH_RESTART"] = "0",
        ["HUD_ITEM_PAGE"] = "off",   // the plugin never visits the HUD item's page from a test copy: a copy's restarts must not add visits
    };
    public const string Content = Check.Content;
    /// <summary>OFF's work in the settings text: with restarts and these two maps keys off the watcher exits at once.</summary>
    public static JsonObject SettingsOff => Json.O("""
{"restarts": {"updates": false, "newVersions": false, "freezeRecovery": false, "daily": "off", "downloadRestart": false},
 "maps": {"delete": "off", "backgroundDownloads": false, "downloadLine": false},
 "hud": {"itemPage": false}}
""");

    public readonly Harness H;
    public readonly string Game, Bridge, Aside, BridgeAside, BridgeRepo, Flag, Host;
    public readonly int Port;
    public readonly Dictionary<string, string> ExtraLabels;
    public readonly string State, ProdSaved, FlagMine, BridgeSaved;

    public Func<double, A2sAnswer?> A2sAsk;
    public Func<int, double, (A2sAnswer? Answer, double Waited)> WaitA2s;

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
        A2sAsk = timeout => A2s.Full(Host, Port, timeout);
        WaitA2s = RealWaitA2s;
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

    /// <summary>Production's settings text, an entry of its Env, with SettingsOff set on it. Its comments go, if it has any; references (env:,
    /// file:) stay for the copy to read. A text that cannot be read stops here: a copy without its overrides is production.</summary>
    public static string SettingsForCopy(string entry)
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
                target[k] = v?.DeepClone();
        }
        return $"{Content}={Json.Dumps(s)}";
    }

    public JsonObject Body(Dictionary<string, string>? extra = null)
    {
        var d = Json.Parse(File.ReadAllText(ProdSaved, Encoding.UTF8)) as JsonObject ?? [];
        var c = Json.Obj(d, "Config");
        var h = d["HostConfig"]?.DeepClone() as JsonObject ?? [];
        var over = new Dictionary<string, string>(Off);
        foreach (var (k, v) in extra ?? [])
            over[k] = v;
        var env = new List<string>();
        foreach (var e in Json.Arr(c, "Env").Select(Json.PyStr))
            if (!over.ContainsKey(e.Split('=', 2)[0]) && !e.StartsWith("CS2_ADDITIONAL_ARGS=", StringComparison.Ordinal))
                env.Add(e.StartsWith(Content + "=", StringComparison.Ordinal) ? SettingsForCopy(e) : e);
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

    public void CmdRun(IReadOnlyList<string> argv)
    {
        H.NeedLock();
        var extra = EnvOverrides(argv);
        if (Inspect(Aside) is null)
            throw Out.Stop("REFUSED: production is not aside - run prep first");
        if (Inspect(Game) is not null)
            throw Out.Stop($"REFUSED: a {Game} container exists");
        if (extra.Count > 0)
            Log("for this copy alone:", string.Join(", ", extra.Select(kv => $"{kv.Key}={kv.Value}")));
        var body = Body(extra);
        if (Json.Arr(body, "Env").Any(e => Json.PyStr(e).StartsWith(Content + "=", StringComparison.Ordinal)))
            Log("production holds the settings text: the copy has it with", string.Join(", ", SettingsOff.SelectMany(s => ((JsonObject)s.Value!).Select(k => $"{s.Key}.{k.Key}"))), "overridden");
        var created = H.Call($"/containers/create?name={Game}", "POST", body);
        Log("create test copy ->", created.Status, Py.Slice(Json.Str(created.Json, "Id") ?? "", 0, 12));
        var st = H.Call($"/containers/{Game}/start", "POST").Status;
        Log("start ->", st, "net", Py.Repr(Pinned(Inspect(Game))));
        var (i, t) = WaitA2s(0, 480);
        Log($"A2S after {Fmt.Fixed(t, 0)} s:", Py.Repr(i), i is { Vac: 0 } ? "- VAC OFF, the insecure test copy is up" : "- NOT an insecure server: check before any insecure client joins");
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
        if (File.Exists(FlagMine))
        {
            if (i is { Vac: 1 } && okNet)
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
    }

    // -------------------------------------------------------------------------------------------------- the candidate bridge

    /// <summary>The host folder a bridge container has at /data (a bind), or null.</summary>
    public static string? DataMount(JsonNode? d)
    {
        foreach (var b in Json.Arr(d, "HostConfig", "Binds").Select(Json.PyStr))
        {
            var colon = b.IndexOf(':');
            var (src, rest) = colon < 0 ? (b, "") : (b[..colon], b[(colon + 1)..]);
            if (rest.Split(':')[0] == "/data")
                return src;
        }
        foreach (var m in Json.Arr(d, "HostConfig", "Mounts"))
            if (Json.Str(m, "Target") == "/data" && Json.Str(m, "Type") == "bind")
                return Json.Str(m, "Source");
        return Json.Arr(d, "Mounts").Where(m => Json.Str(m, "Destination") == "/data" && Json.Str(m, "Type") == "bind").Select(m => Json.Str(m, "Source")).FirstOrDefault();
    }

    /// <summary>Where the candidate's copy goes: beside production's data, &lt;src&gt;-candidate. Never production's own folder.</summary>
    public static string CopyDir(string? src)
    {
        if (string.IsNullOrEmpty(src) || !src.StartsWith('/') || src.Split('/').Contains("..") || src.TrimEnd('/') is "" or "/mnt")
            throw Out.Stop($"REFUSED: cannot place a copy beside {Py.Repr(src)}");
        return src.TrimEnd('/') + "-candidate";
    }

    /// <summary>One short-lived container from the game image, as root, without a network: runs script with binds, is removed, and returns
    /// (exit code, its output).</summary>
    public (int Code, string Output) Helper(string name, IReadOnlyList<string> binds, string script, string? image = null, Dictionary<string, string>? labels = null)
    {
        image ??= Json.Str(Json.Parse(File.ReadAllText(ProdSaved, Encoding.UTF8)), "Image") ?? "";
        var labelObject = new JsonObject { [TestLabel] = "helper: " + name };
        foreach (var (k, v) in labels ?? [])
            labelObject[k] = v;
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

    public const string Doc = "testcopy prep | run [--env K=V] | status | rm | restore | bridge-prep | bridge-run <tag> [--keep-flag] | bridge-rm | bridge-restore";

    public static int Command(string[] argv)
    {
        var (args, config) = Harness.TakeConfig(argv);
        string[] verbs = ["prep", "run", "status", "rm", "restore", "bridge-prep", "bridge-run", "bridge-rm", "bridge-restore"];
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
        }
        return 0;
    }
}
