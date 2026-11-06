using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace OverStrike2.Deploy;

/// <summary>The stack's two checks, both read-only: guard before a redeploy of the whole stack, verify after a deploy or a release
/// (deploy/check.py, ported).
///
/// guard. A redeploy of the whole stack may recreate every service in it, the game server included, whatever changed - a stack manager
/// that force-recreates on every redeploy does - and so disconnect everybody on it. The guard decides by the players alone: humans by
/// A2S_INFO (players minus bots), and no answer is "unknown", which is not a go. What it shows besides decides nothing: the server lock,
/// whether the redeploy moves the game server to a newer image, and, with [stack] in the private config, whether the game server's
/// service in the compose file changed since the commit last deployed.
///
/// verify. The bridge on its image (&lt;bridge.image&gt;:latest) and in the game server's compose project; the game server and the sync
/// running; the game server production, not a test copy (check.test_label, check.aside, and A2S answering VAC-secured); started on
/// check.mapgroup; the bridge's health from inside the game server (the path the log push takes); the settings and the secrets; the
/// roll-call of the compose file (stack.file); and the deployment's own audits ([[check.audit]]), each a command.
///
/// The settings and the secrets: each container's settings text is its OVERSTRIKE2_SETTINGS_CONTENT, read from its inspect: the texts
/// must be identical, bridge.secret as each container reads it must be the secret in the game server's CS2_LOG_HTTP_URL, and
/// server.rconPassword must be the game server's CS2_RCONPW - compared, never printed.</summary>
sealed class Check
{
    public const string Content = "OVERSTRIKE2_SETTINGS_CONTENT";     // the settings text, in each container's environment (docs/settings.md)
    public const string ProgramFolder = "/home/steam/overstrike2";    // where the game server and the sync mount os2-server and os2-sync
    static readonly string[] OldPrograms = ["update-watch.py", "keepset.py", "workshop-sync.py"];   // the Python programs os2-server and os2-sync replaced
    public const string MapGroup = "mg_ar_shoots";   // check.mapgroup's default: Valve's one-map Arms Race group, so CS2 shows no VOTING tab at Game Over
    public const string Project = "com.docker.compose.project", Service = "com.docker.compose.service";
    static readonly Regex ContainerName = new(@"^\s*container_name:\s*['""]?([\w.-]+)");   // a commented service's lines start with '#'

    public sealed record AuditResult(int ReturnCode, string Stdout, string Stderr);

    readonly IConfig cfg;
    IDocker? d;
    public readonly Dictionary<string, string> Names;   // game, bridge, sync - in that order
    public Func<string, int?> HumansOf = address => A2s.Humans(address);
    public Func<string, (int Humans, int Vac)?> A2sInfo = A2s.Info;
    public Func<List<string>, Dictionary<string, string?>, AuditResult> Run = RunAudit;
    public Func<string, JsonNode?> Fetch;
    public Func<string, string[], string?> Git = GitIn;
    /// <summary>Whether a YAML reader is at hand (always, here; the self-test takes it away to see the whole-file comparison).</summary>
    public static bool YamlReader = true;

    public Check(IConfig cfg, IDocker? docker = null)
    {
        this.cfg = cfg;
        Names = new Dictionary<string, string>
        {
            ["game"] = cfg.Str("bridge.game_container", required: true),
            ["bridge"] = cfg.Str("bridge.container", required: true),
            ["sync"] = cfg.Str("bridge.sync_container"),
        };
        d = docker;
        Fetch = GetJson;
    }

    bool injected => d is not null && d is not Docker;

    IDocker? D
    {
        get
        {
            if (d is null && DockerProblem(cfg).Length == 0)
                d = new Docker(cfg);
            return d;
        }
    }

    // ------------------------------------------------------------------------------------------------------------- reading

    public static JsonObject LabelsOf(JsonNode? c) => InspectOf.Labels(c);

    /// <summary>Why the Docker API cannot be used, or "" when it can: no docker.api, or a header whose variable is not set. Names, never values.</summary>
    public static string DockerProblem(IConfig cfg)
    {
        if (cfg.Str("docker.api").Length == 0)
            return "no docker.api in the private config";
        foreach (var (k, v) in cfg.Table("docker.headers"))
            if (v is string s && s.StartsWith("env:", StringComparison.Ordinal) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(s[4..])))
                return $"docker.headers.{k} reads {s[4..]}, which is not set";
        return "";
    }

    /// <summary>A value in parsed JSON by its dotted path ("a.b.c"); "" when it is not there or is no string.</summary>
    public static string Dotted(JsonNode? data, string? path)
    {
        foreach (var part in (path ?? "").Split('.'))
        {
            if (data is not JsonObject o || !o.TryGetPropertyValue(part, out data))
                return "";
        }
        return Json.AsStr(data) ?? "";
    }

    /// <summary>[(source, target)] of a container's mounts, as its inspect names them.</summary>
    static List<(string Source, string Target)> Mounted(JsonNode? c) =>
        Json.Arr(c, "Mounts").Select(m => (Json.Str(m, "Source") ?? "", Json.Str(m, "Destination") ?? "")).ToList();

    /// <summary>The older Python programs a container mounts, sorted.</summary>
    static List<string> OldProgramsOf(JsonNode? c)
    {
        var mounted = Mounted(c);
        return OldPrograms.Where(s => mounted.Any(m => m.Source.Replace('\\', '/').EndsWith("/" + s, StringComparison.Ordinal)))
                          .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>A settings value as its container reads it: "env:NAME" from that container's environment, else itself. What finds nothing is
    /// empty, as in OverStrike2's reader, and so is "file:PATH", which is inside the container, where this check does not look.</summary>
    static string Resolve(JsonNode? value, JsonNode? container)
    {
        if (Json.AsStr(value) is not { } s || s.StartsWith("file:", StringComparison.Ordinal))
            return "";
        if (s.StartsWith("env:", StringComparison.Ordinal))
            return InspectOf.EnvOrNull(container, s[4..]) ?? "";
        return s;
    }

    // ------------------------------------------------------------------------------------------------- the guard's decision

    /// <summary>(exit code, line): GO only with nobody on, or with the owner's --allow-kick.</summary>
    public static (int Code, string Line) Decide(int? on, bool allowKick, string game = "the game server")
    {
        if (on == 0)
            return (0, "GO: nobody is on");
        var who = on is int n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "an unknown number of";
        if (allowKick)
            return (0, $"GO (--allow-kick): {game} restarts with {who} player(s) on - the owner said so");
        return (3, $"NO: a redeploy would restart {game} with {who} player(s) on. Wait for an empty server, or ask the owner for --allow-kick.");
    }

    /// <summary>(changed, by the service alone) between two versions of the compose file: the service's own definition, anchors resolved,
    /// when YAML reads both; else the whole file. null for either text: that version could not be read - changed.</summary>
    public static (bool Changed, bool Alone) ServiceChanged(string? oldText, string? newText, string service)
    {
        if (oldText is null || newText is null)
            return (true, false);
        if (YamlReader)
        {
            try
            {
                var a = Yaml.Plain(Yaml.Service(oldText, service));
                var b = Yaml.Plain(Yaml.Service(newText, service));
                return (!Yaml.Same(a, b), true);
            }
            catch (Exception e) when (e is YamlException or InvalidOperationException or ArgumentException)
            {
            }
        }
        return (oldText != newText, false);
    }

    // ---------------------------------------------------------------------------------------------- the settings and secrets

    /// <summary>(ok, line): the game server starts on that map group (CS2_MAPGROUP, which the image passes as +mapgroup).</summary>
    public static (bool Ok, string Line) MapGroupOk(JsonNode? game, string want = MapGroup, string name = "the game server")
    {
        var group = InspectOf.EnvOrNull(game, "CS2_MAPGROUP");
        var oneMap = want == MapGroup;
        if (group == want)
            return (true, $"{name} starts on the map group {want}" + (oneMap ? " (one map: no CS2 VOTING tab at Game Over)" : ""));
        return (false, $"{name}'s CS2_MAPGROUP is {Py.Repr(group)}, not {want}" + (oneMap ? ": CS2 shows its own VOTING tab with that group's maps at Game Over" : "")
                       + " (the compose file sets it; a redeploy of the stack applies it)");
    }

    /// <summary>{place: value} of every place the bridge's secret is written: the secret in the game server's CS2_LOG_HTTP_URL; every variable
    /// that still holds it (the bridge's LOG_SECRET, OVERSTRIKE2_BRIDGE_SECRET, SYNC_BRIDGE_SECRET); and bridge.secret as each container
    /// reads its settings text (settings: {role: value}). With no settings text the bridge's LOG_SECRET counts even when it is missing.</summary>
    public static List<KeyValuePair<string, string>> SecretPlaces(Dictionary<string, JsonNode?> c, Dictionary<string, string> n, List<KeyValuePair<string, string>>? settings = null)
    {
        var places = new List<KeyValuePair<string, string>>();
        void Put(string place, string value)
        {
            var at = places.FindIndex(p => p.Key == place);
            if (at >= 0)
                places[at] = new(place, value);
            else
                places.Add(new(place, value));
        }
        c.TryGetValue("bridge", out var bridge);
        c.TryGetValue("game", out var game);
        if (settings is null || InspectOf.EnvOrNull(bridge, "LOG_SECRET") is not null)
            Put($"{n["bridge"]} LOG_SECRET", InspectOf.EnvOrNull(bridge, "LOG_SECRET") ?? "");
        var m = Regex.Match(Py.Strip((InspectOf.EnvOrNull(game, "CS2_LOG_HTTP_URL") ?? "").Replace("\\/", "/", StringComparison.Ordinal)), "/log/([A-Za-z0-9]+)$");
        Put($"{n["game"]} CS2_LOG_HTTP_URL", m.Success ? m.Groups[1].Value : "");
        foreach (var role in new[] { "game", "sync", "bridge" })
        {
            if (!n.TryGetValue(role, out var name) || name.Length == 0)
                continue;
            c.TryGetValue(role, out var box);
            foreach (var key in new[] { "OVERSTRIKE2_BRIDGE_SECRET", "SYNC_BRIDGE_SECRET" })
                if (InspectOf.EnvOrNull(box, key) is { } v)
                    Put($"{name} {key}", v);
        }
        foreach (var (role, value) in settings ?? [])
            Put($"{n[role]} settings", value);
        return places;
    }

    /// <summary>(ok, line) - one secret, the same in every place; the line names places, never a value.</summary>
    public static (bool Ok, string Line) SecretsAgree(List<KeyValuePair<string, string>> places, string bridge)
    {
        string? Of(string place) => places.FirstOrDefault(p => p.Key == place).Value;
        var reference = Of($"{bridge} settings") ?? Of($"{bridge} LOG_SECRET") ?? "";
        var off = places.Where(p => p.Value.Length == 0 || p.Value != reference).Select(p => p.Key).ToList();
        if (off.Count == 0)
            return (true, $"one bridge secret in all {places.Count} places ({string.Join(", ", places.Select(p => p.Key))})");
        return (false, $"the bridge secret is not the bridge's own in: {string.Join(", ", off)} (of {places.Count} places)");
    }

    /// <summary>[(ok, line)] about the settings text and the secrets; ok null is a note. c: {role: inspect}, n: {role: container name}, for
    /// game, bridge and sync (no sync name: the stack has none). Every line names places, never a value.</summary>
    public static List<(bool? Ok, string Line)> SettingsLines(Dictionary<string, JsonNode?> c, Dictionary<string, string> n)
    {
        JsonNode? Box(string role) => c.TryGetValue(role, out var x) ? x : null;
        bool Named(string role) => n.TryGetValue(role, out var x) && x.Length > 0;
        var roles = new[] { "game", "bridge", "sync" }.Where(Named).ToList();
        var runners = new[] { "game", "sync" }.Where(Named).ToList();   // the two that run os2-server and os2-sync
        string Both(IEnumerable<string> rs) => string.Join(" and ", rs.Select(r => n[r]));
        var texts = roles.ToDictionary(r => r, r => InspectOf.EnvOrNull(Box(r), Content));
        var held = roles.Where(r => texts[r] is not null).ToList();
        var missing = roles.Where(r => texts[r] is null).ToList();
        var lines = new List<(bool? Ok, string Line)>();
        if (held.Count == 0)
        {
            var scripts = runners.ToDictionary(r => r, r => OldProgramsOf(Box(r)));
            if (scripts.Values.All(s => s.Count > 0))
                lines.Add((null, "the older Python programs run: no container holds the settings text; "
                                 + string.Join("; ", scripts.Select(kv => $"{n[kv.Key]} mounts {string.Join(", ", kv.Value)}"))));
            else
                lines.Add((false, $"no container holds the settings text ({Content}), and the older Python programs are not mounted either ("
                                  + string.Join(", ", scripts.Where(kv => kv.Value.Count == 0).Select(kv => n[kv.Key])) + "): neither way of running is in place"));
            var (ok0, line0) = SecretsAgree(SecretPlaces(c, n), n["bridge"]);
            lines.Add((ok0, line0));
            return lines;
        }
        if (missing.Count > 0)
            lines.Add((false, $"only {string.Join(", ", held.Select(r => n[r]))} hold the settings text ({Content}), {string.Join(", ", missing.Select(r => n[r]))} do not: "
                              + "compose gives them all the same, so a container was made another way; a redeploy of the stack at 0 humans puts it right"));
        var identical = held.Select(r => texts[r]).Distinct(StringComparer.Ordinal).Count() == 1;
        lines.Add((identical, identical ? $"the settings text is identical in {string.Join(", ", held.Select(r => n[r]))}"
                                        : $"the settings text differs between {string.Join(", ", held.Select(r => n[r]))}: compose gives them one text, so one was made another way"));
        var parsed = new Dictionary<string, JsonNode?>();
        foreach (var r in held)
        {
            try
            {
                parsed[r] = Json.Jsonc(texts[r]!);
            }
            catch (JsonSyntaxError)
            {
                lines.Add((false, $"{n[r]}'s settings text does not parse"));
            }
        }
        JsonNode? Section(string r, string name) =>
            parsed.TryGetValue(r, out var p) && p is JsonObject o && o.TryGetPropertyValue(name, out var s) && s is JsonObject ? s : null;
        var secrets = roles.Select(r => new KeyValuePair<string, string>(r, Resolve(Json.At(Section(r, "bridge"), "secret"), Box(r)))).ToList();
        var (ok1, line1) = SecretsAgree(SecretPlaces(c, n, secrets), n["bridge"]);
        lines.Add((ok1, line1));
        var rcon = InspectOf.EnvOrNull(Box("game"), "CS2_RCONPW") ?? "";
        var off = new[] { "game", "bridge" }.Where(r => rcon.Length == 0 || Resolve(Json.At(Section(r, "server"), "rconPassword"), Box(r)) != rcon).ToList();
        lines.Add((off.Count == 0, off.Count == 0 ? $"server.rconPassword as {Both(["game", "bridge"])} read it is {n["game"]}'s CS2_RCONPW"
                                                 : $"server.rconPassword is not {n["game"]}'s CS2_RCONPW in: {string.Join(", ", off.Select(r => n[r]))}"));
        var left = roles.ToDictionary(r => r, r => OldProgramsOf(Box(r)));
        var none = left.Values.All(s => s.Count == 0);
        lines.Add((none, none ? "no container mounts one of the older Python programs"
                              : "still mounted: " + string.Join("; ", left.Where(kv => kv.Value.Count > 0).Select(kv => $"{n[kv.Key]}: {string.Join(", ", kv.Value)}"))));
        var noFolder = runners.Where(r => !Mounted(Box(r)).Any(m => m.Target == ProgramFolder)).ToList();
        lines.Add((noFolder.Count == 0, noFolder.Count == 0 ? $"{Both(runners)} mount the program folder at {ProgramFolder}"
                                                           : $"no program folder at {ProgramFolder} in: {string.Join(", ", noFolder.Select(r => n[r]))}"));
        return lines;
    }

    // ---------------------------------------------------------------------------------------------------------- the checks

    /// <summary>An HTTP GET with the Docker API's headers and TLS settings (the stack manager is usually the same server), as JSON.</summary>
    JsonNode? GetJson(string url)
    {
        var docker = D;
        using var http = Docker.Client(docker?.TlsVerify ?? true);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (k, v) in docker?.Headers ?? new Dictionary<string, string>())
            req.Headers.TryAddWithoutValidation(k, v);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var resp = http.Send(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        return Json.Parse(resp.Content.ReadAsByteArrayAsync(cts.Token).GetAwaiter().GetResult());
    }

    static string? GitIn(string repo, string[] args)
    {
        var r = Proc.Capture(["git", "-C", repo, .. args]);
        return r.Code == 0 ? r.Out : null;
    }

    static AuditResult RunAudit(List<string> argv, Dictionary<string, string?> env)
    {
        var r = Proc.Capture(argv, env: env, timeoutSeconds: 600);
        return new AuditResult(r.Code, r.Out, r.Err);
    }

    // ---- guard

    public int Guard(bool allowKick)
    {
        var on = HumansOf(cfg.Str("server.address", required: true));   // no address: exit 2, never a go
        Out.Print($"humans on the server: {(on is null ? "unknown (no A2S answer)" : on.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        var note = Repo.LockNote(cfg);
        if (!string.IsNullOrEmpty(note))
            Out.Print("  server lock held: " + Repo.Oneline(note));
        Out.Print($"  a redeploy of the stack may recreate every service in it, {Names["game"]} included, whatever changed: this decides by the players alone");
        var problem = injected ? "" : DockerProblem(cfg);
        if (problem.Length > 0)
            Out.Print($"  ({problem}: the drift is not shown - it decides nothing)");
        else
        {
            try   // information only
            {
                foreach (var line in Drift())
                    Out.Print("  " + line);
            }
            catch (Exception e) when (e is not Exit)
            {
                Out.Print($"  (could not read the drift: {e.GetType().Name}: {Py.Slice(e.Message, 0, 200)})");
            }
        }
        Out.Print($"  for the bridge alone: os2-deploy bridge update ({Names["game"]} keeps running); for data files: nothing - the bridge reloads them");
        var (code, decision) = Decide(on, allowKick, Names["game"]);
        Out.Print(decision);
        return code;
    }

    /// <summary>The guard's information, line by line: the game server's image against its tag, and its service in the compose file, as last
    /// deployed against what a redeploy would take now (stack.ref, fetched first).</summary>
    IEnumerable<string> Drift()
    {
        var docker = D!;
        var name = Names["game"];
        var game = docker.Inspect("containers", name);
        if (game is null || game.Count == 0)
        {
            yield return $"no container {name} on the server";
            yield break;
        }
        var tag = Json.Str(game, "Config", "Image") ?? throw new KeyNotFoundException("Image");
        var tagged = docker.Inspect("images", tag);
        var tagId = Json.Str(tagged, "Id");
        var image = Json.Str(game, "Image");
        yield return $"{name} image: container {Py.Slice(image, 7, 19)}, tag {tag} -> {Py.SliceStr(tagId, 7, 19)}"
                     + (tagged is { Count: > 0 } && image != tagId ? " (the redeploy also moves it to the newer image)" : "");
        var repo = cfg.Str("stack.repo");
        var file = cfg.Str("stack.file");
        var reference = cfg.Str("stack.ref", "origin/main");
        var url = cfg.Str("stack.deployed_url");
        var field = cfg.Str("stack.deployed_field");
        if (repo.Length == 0 || file.Length == 0)
        {
            yield return "(no stack.repo and stack.file in the private config: the compose file's drift is not shown)";
            yield break;
        }
        var deployed = url.Length > 0 && field.Length > 0 ? Py.Strip(Dotted(Fetch(url), field)) : "";
        if (deployed.Length == 0)
        {
            yield return $"(the commit last deployed is not known{(url.Length > 0 && field.Length > 0 ? "" : ": no stack.deployed_url and stack.deployed_field")})";
            yield break;
        }
        if (reference.Contains('/'))
            Git(repo, ["fetch", "-q", reference.Split('/', 2)[0]]);
        var service = Json.AsStr(LabelsOf(game)[Service]) is { Length: > 0 } s ? s : name;
        var path = file.Replace('\\', '/');
        var (changed, alone) = ServiceChanged(Git(repo, ["show", $"{deployed}:{path}"]), Git(repo, ["show", $"{reference}:{path}"]), service);
        yield return $"the {service} service in {file}: deployed {Py.Slice(deployed, 0, 7)} vs {reference}: {(changed ? "changed" : "unchanged")}"
                     + (alone ? "" : " (by the whole file: a version could not be read as YAML)");
    }

    // ---- verify

    public int Verify()
    {
        var n = Names;
        var problem = injected ? "" : DockerProblem(cfg);
        if (problem.Length > 0)
        {
            Out.Print($"LOOK  {problem} - nothing checked");
            Out.Print("RESULT: 1 to look at");
            return 1;
        }
        var docker = D!;
        var bad = new List<string>();
        void Say(bool? ok, string text)
        {
            Out.Print((ok is null ? "note  " : ok.Value ? "ok    " : "LOOK  ") + text);
            if (ok == false)
                bad.Add(text);
        }

        var note = Repo.LockNote(cfg);
        if (!string.IsNullOrEmpty(note))
            Say(null, "the server lock is held - a test may have the server: " + Repo.Oneline(note));

        // 1. the bridge on its image, in its stack
        var image = cfg.Str("bridge.image", "localhost/overstrike2-bridge");
        var latest = docker.Inspect("images", $"{image}:latest") ?? [];
        var b = docker.Inspect("containers", n["bridge"]) ?? [];
        var game = docker.Inspect("containers", n["game"]) ?? [];
        JsonObject? sync = n["sync"].Length > 0 ? docker.Inspect("containers", n["sync"]) ?? [] : null;
        Say(b.Count > 0 && Json.Same(b["Image"], latest["Id"]),
            $"{n["bridge"]} runs {Py.Slice(Json.PyStr(b["Image"]), 7, 19)} ({Json.PyStr(Json.Obj(b, "Config")["Image"])}); {image}:latest is {Py.Slice(Json.PyStr(latest["Id"]), 7, 19)}");
        var state = Json.Obj(b, "State");
        var noRestarts = !b.TryGetPropertyValue("RestartCount", out var restarts) || Json.AsLong(restarts) == 0;   // b.get("RestartCount", 0) == 0
        Say(b.Count > 0 && Json.Truthy(state["Running"]) && noRestarts,
            $"{n["bridge"]} state {Json.PyStr(state["Status"])}, restarts {Json.PyStr(b["RestartCount"])}, started {Py.Slice(Json.AsStr(state["StartedAt"]) ?? "", 0, 19)}Z");
        var project = Json.AsStr(LabelsOf(b)[Project]);
        var service = Json.AsStr(LabelsOf(b)[Service]);
        var home = Json.AsStr(LabelsOf(game)[Project]);
        Say(!string.IsNullOrEmpty(project) && project == home && !string.IsNullOrEmpty(service),
            $"{n["bridge"]} belongs to compose project {Py.Repr(project)}, service {Py.Repr(service)}" + (project == home ? "" : $" - {n["game"]}'s project is {Py.Repr(home)}"));

        // 2. the game server and the sync; the game server is production, not a test copy
        foreach (var (name, c) in new[] { (n["game"], (JsonObject?)game), (n["sync"], sync) })
        {
            if (name.Length == 0)
                continue;
            var st = Json.Obj(c, "State");
            Say(c is { Count: > 0 } && Json.Truthy(st["Running"]),
                $"{name} state {Json.PyStr(st["Status"])}, started {Py.Slice(Json.AsStr(st["StartedAt"]) ?? "", 0, 19)}Z, image {Json.PyStr(Json.Obj(c, "Config")["Image"])}");
        }
        if (n["sync"].Length == 0)
            Say(null, "no bridge.sync_container in the private config: a stack without the Workshop sync");
        var label = cfg.Str("check.test_label");
        var aside = cfg.List("check.aside").Where(ConfigExtensions.Truthy).Select(Py.Str).ToList();
        if (label.Length > 0)
        {
            var tests = new[] { (n["game"], (JsonNode?)game), (n["bridge"], b), (n["sync"], sync) }
                .Where(x => x.Item1.Length > 0 && Json.Truthy(LabelsOf(x.Item2)[label]))
                .Select(x => $"{x.Item1} ({Json.PyStr(LabelsOf(x.Item2)[label])})").ToList();
            Say(tests.Count == 0, tests.Count == 0 ? $"production: no {label} label on {string.Join(", ", n.Values.Where(x => x.Length > 0))}"
                                                   : "a TEST COPY, not production: " + string.Join("; ", tests));
        }
        if (aside.Count > 0)
        {
            var found = aside.Select(a => (a, docker.Inspect("containers", a))).ToList();
            var there = found.Where(f => f.Item2 is not null).Select(f => $"{f.a} ({Json.PyStr(Json.Obj(f.Item2, "State")["Status"])})").ToList();
            Say(there.Count == 0, there.Count == 0 ? $"nothing set aside: no {string.Join(", ", aside)}"
                                                   : $"set aside for a test: {string.Join(", ", there)} - production is not what runs");
        }
        if (label.Length == 0 && aside.Count == 0)
            Say(null, "no check.test_label or check.aside in the private config: a test copy shows only in A2S's VAC flag");
        var group = cfg.Str("check.mapgroup", MapGroup);
        if (group.Length > 0)
        {
            var (ok, line) = MapGroupOk(game, group, n["game"]);
            Say(ok, line);
        }
        var info = A2sInfo(cfg.Str("server.address"));
        Say(info is not null, "the game server answers A2S: " + (info is null ? "no answer" : $"humans {info.Value.Humans}, VAC {info.Value.Vac}"));
        Say(info is { Vac: 1 }, $"A2S says VAC-secured: {(info is { Vac: 1 } ? "yes" : "NO - an insecure server")}");

        // 3. the bridge's health from inside the game server (the path the log push takes)
        if (b.Count > 0 && game.Count > 0)
        {
            var secret = InspectOf.BridgeSecret(game, b);
            var h = docker.ExecIn(n["game"], ["python3", "-c", Bridge.InnerGet, $"http://{n["bridge"]}:{InspectOf.BridgePort(game, b)}/health/{secret}"]) ?? "";
            var first = Py.Strip(h).Length > 0 ? Py.SplitLines(Py.Strip(h))[0] : "(no answer)";
            var m = Regex.Match(h, @"log pushes (\d+)");
            Say(h.StartsWith("gg-health", StringComparison.Ordinal), $"bridge /health from {n["game"]}: " + (secret.Length > 0 ? first.Replace(secret, "<secret>", StringComparison.Ordinal) : first));
            Say(m.Success && long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 0,
                $"log pushes reaching the bridge: {(m.Success ? m.Groups[1].Value : "?")} (0 right after a boot is normal for a minute)");
        }

        // 4. the settings text and the secrets: in every place, the same - compared, never printed
        foreach (var (ok, line) in SettingsLines(new Dictionary<string, JsonNode?> { ["game"] = game, ["bridge"] = b, ["sync"] = sync }, n))
            Say(ok, line);

        // 5. the roll-call of the compose file, and 6. the deployment's own audits
        RollCall(docker, Say, game);
        Audits(Say);

        Out.Print("RESULT: " + (bad.Count == 0 ? "ALL GOOD" : $"{bad.Count} to look at"));
        return bad.Count == 0 ? 0 : 1;
    }

    /// <summary>Every container_name in the compose file (stack.repo + stack.file, as it is on disk) against what runs, the names the private
    /// config gives among them; and, as a note, containers of the game server's compose project that the file does not declare.</summary>
    void RollCall(IDocker docker, Action<bool?, string> say, JsonObject game)
    {
        var repo = cfg.Str("stack.repo");
        var file = cfg.Str("stack.file");
        if (repo.Length == 0 || file.Length == 0)
        {
            say(null, "no stack.repo and stack.file in the private config: no roll-call of the compose file");
            return;
        }
        List<string> declared;
        try
        {
            declared = File.ReadAllLines(Path.Combine([repo, .. file.Replace('\\', '/').Split('/')]))
                           .Select(l => ContainerName.Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            say(false, $"roll-call: {file} cannot be read ({e.GetType().Name})");
            return;
        }
        var listed = docker.Call("GET", "/containers/json?all=1").Json as JsonArray ?? [];
        var live = new Dictionary<string, JsonObject>();
        foreach (var c in listed.OfType<JsonObject>())
            if (Json.Arr(c, "Names").Count > 0)
                live[(Json.AsStr(Json.Arr(c, "Names")[0]) ?? "").TrimStart('/')] = c;
        var undeclared = Names.Values.Where(x => x.Length > 0 && !declared.Contains(x)).ToList();
        var missing = declared.Where(x => !live.ContainsKey(x)).ToList();
        var stopped = declared.Where(x => live.ContainsKey(x) && Json.AsStr(live[x]["State"]) != "running").Select(x => $"{x} ({Json.PyStr(live[x]["Status"])})").ToList();
        var wrong = new List<string>();
        if (undeclared.Count > 0)
            wrong.Add($"not declared there, though the private config names it: {string.Join(", ", undeclared)}");
        if (missing.Count > 0)
            wrong.Add($"missing: {string.Join(", ", missing)}");
        if (stopped.Count > 0)
            wrong.Add($"not running: {string.Join(", ", stopped)}");
        say(wrong.Count == 0, $"roll-call, {file}: " + (wrong.Count > 0 ? string.Join("; ", wrong) : $"{(declared.Count > 0 ? string.Join(", ", declared) : "nothing")} declared and running"));
        var project = Json.AsStr(LabelsOf(game)[Project]);
        var extra = live.Where(kv => !string.IsNullOrEmpty(project) && Json.AsStr(Json.Obj(kv.Value, "Labels")[Project]) == project && !declared.Contains(kv.Key))
                        .Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (extra.Count > 0)
            say(null, $"in compose project {Py.Repr(project)} but declared nowhere in {file}: {string.Join(", ", extra)}");
    }

    /// <summary>The deployment's own read-only audits, [[check.audit]] in the private config: name, argv ("python" first: the Python on the
    /// PATH), env (variables the command needs; "env:NAME" read from this environment, never printed), and how its output is judged: look,
    /// a regex - a line that matches is something to look at, and then the exit code is only shown; done, a regex - the line that says the
    /// audit ran to its end. Without look the exit code decides: 0 is all good.</summary>
    void Audits(Action<bool?, string> say)
    {
        var audits = cfg.List("check.audit");
        if (audits.Count == 0)
        {
            say(null, "no [[check.audit]] in the private config: no audit of the deployment's own");
            return;
        }
        foreach (var item in audits)
        {
            var a = item as Dictionary<string, object?> ?? new Dictionary<string, object?>();
            var name = a.TryGetValue("name", out var nm) && ConfigExtensions.Truthy(nm) ? Py.Str(nm) : "audit";
            var argv = a.TryGetValue("argv", out var av) && av is List<object?> al ? al : [];
            var command = argv.Select(Py.Str).ToList();
            if (command.Count == 0)
            {
                say(false, $"{name}: no argv in the private config");
                continue;
            }
            var env = Proc.Environment();
            var unset = new List<string>();
            foreach (var (k, v) in a.TryGetValue("env", out var ev) && ev is Dictionary<string, object?> e ? e : new Dictionary<string, object?>())
            {
                var reference = v is string s && s.StartsWith("env:", StringComparison.Ordinal) ? s[4..] : null;
                var value = reference is not null ? Environment.GetEnvironmentVariable(reference) ?? "" : Py.Str(v);
                if (value.Length == 0)
                    unset.Add(reference is not null ? $"{k} ({reference} is not set)" : $"{k} (empty)");
                env[k] = value;
            }
            if (unset.Count > 0)
            {
                say(false, $"{name}: not run - {string.Join(", ", unset)}");
                continue;
            }
            AuditResult r;
            try
            {
                r = Run(command, env);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or TimeoutException or InvalidOperationException)
            {
                say(false, $"{name}: not run ({ex.GetType().Name})");
                continue;
            }
            var lines = Py.SplitLines(r.Stdout + "\n" + r.Stderr).Select(Py.Strip).Where(l => l.Length > 0).ToList();
            var look = a.TryGetValue("look", out var lk) && ConfigExtensions.Truthy(lk) ? Py.Str(lk) : null;
            var done = a.TryGetValue("done", out var dn) && ConfigExtensions.Truthy(dn) ? Py.Str(dn) : null;
            var ended = done is not null ? lines.FirstOrDefault(l => Regex.IsMatch(l, done)) : null;
            if (done is not null && ended is null)
                say(false, $"{name}: did not finish (exit {r.ReturnCode})" + (lines.Count > 0 ? $": {Py.Slice(lines[^1], 0, 200)}" : ""));
            else if (look is not null)
            {
                var looked = lines.Where(l => Regex.IsMatch(l, look)).ToList();
                say(looked.Count == 0, $"{name}: " + (looked.Count > 0 ? string.Join("; ", looked) : $"no line matches /{look}/"));
                say(null, $"{name}: exit {r.ReturnCode}; {lines.Count - looked.Count} other line(s)" + (ended is not null ? $"; {ended}" : ""));
            }
            else
                say(r.ReturnCode == 0, $"{name}: exit {r.ReturnCode}" + (lines.Count > 0 ? $"; {ended ?? Py.Slice(lines[^1], 0, 200)}" : ""));
        }
    }

    public static int Command(string[] args)
    {
        var a = new Args(args, "check guard|verify [--allow-kick] [--config FILE]", flags: ["--allow-kick"], options: ["--config"]);
        var command = a.Choice(["guard", "verify"]);
        a.Done();
        var check = new Check(new Config(a.Opt("--config")));
        return command == "guard" ? check.Guard(a.Flag("--allow-kick")) : check.Verify();
    }
}

/// <summary>A compose file read as YAML, the way PyYAML's safe_load did for check.py: anchors and aliases resolved, merge keys merged.</summary>
static class Yaml
{
    /// <summary>A service's definition (services.&lt;name&gt;), or null when the file has none.</summary>
    public static YamlNode? Service(string text, string service)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        if (stream.Documents.Count > 1)
            throw new InvalidOperationException("more than one YAML document");
        if (stream.Documents.Count == 0 || Plain(stream.Documents[0].RootNode) is null)
            return null;                                                  // (yaml.safe_load(text) or {})
        if (stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidOperationException("the compose file is no mapping");
        if (!root.Children.TryGetValue(new YamlScalarNode("services"), out var services) || Plain(services) is null)
            return null;                                                  // (... .get("services") or {})
        if (services is not YamlMappingNode s)
            throw new InvalidOperationException("services is no mapping");
        return s.Children.TryGetValue(new YamlScalarNode(service), out var node) ? node : null;
    }

    /// <summary>A node as plain values - a scalar its text (a null scalar null), a mapping a dictionary with merge keys applied, a sequence a
    /// list - so two versions compare by content, whatever their anchors.</summary>
    public static object? Plain(YamlNode? node, int depth = 0)
    {
        if (depth > 64)
            throw new InvalidOperationException("YAML nested too deep");
        switch (node)
        {
            case null:
                return null;
            case YamlScalarNode sc:
                if (sc.Style == YamlDotNet.Core.ScalarStyle.Plain && sc.Value is null or "" or "~" or "null" or "Null" or "NULL")
                    return null;
                return sc.Value ?? "";
            case YamlSequenceNode seq:
                return seq.Children.Select(c => Plain(c, depth + 1)).ToList();
            case YamlMappingNode map:
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (k, v) in map.Children)
                {
                    var key = (k as YamlScalarNode)?.Value ?? k.ToString();
                    if (key == "<<" && (k as YamlScalarNode)?.Style == YamlDotNet.Core.ScalarStyle.Plain)
                    {
                        var merged = v is YamlSequenceNode many ? many.Children : [v];
                        foreach (var m in merged)
                            if (Plain(m, depth + 1) is Dictionary<string, object?> md)
                                foreach (var (mk, mv) in md)
                                    d.TryAdd(mk, mv);
                        continue;
                    }
                    d[key] = Plain(v, depth + 1);
                }
                return d;
            default:
                return node.ToString();
        }
    }

    public static bool Same(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (string x, string y) => x == y,
        (List<object?> x, List<object?> y) => x.Count == y.Count && x.Zip(y).All(p => Same(p.First, p.Second)),
        (Dictionary<string, object?> x, Dictionary<string, object?> y) => x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var w) && Same(kv.Value, w)),
        _ => false,
    };
}
