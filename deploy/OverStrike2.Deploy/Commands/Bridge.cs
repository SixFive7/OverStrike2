using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Deploy;

/// <summary>The bridge's image, built here and shipped to the server's Docker - no registry, no CI (deploy/bridge.py, ported).
///
///     bridge check                 read-only: what would ship, the running bridge, players, the server lock
///     bridge build [--tag T]       build the committed bridge/ and core/ locally, save it, load it into the server's Docker
///     bridge images                read-only: the bridge image's tags on the server
///     bridge swap                  recreate the bridge container on &lt;image&gt;:candidate, verified; the game keeps running
///     bridge update                check, build, swap
///     bridge rollback              swap back to &lt;image&gt;:previous
///
/// Tags on the server: &lt;image&gt;:&lt;commit&gt; for every build (immutable), :candidate (the newest build, not yet running), :latest (what
/// the compose file names and the container runs), :previous (what the last swap replaced). --tag builds &lt;image&gt;:&lt;T&gt; instead and
/// leaves candidate alone (a test build).
///
/// The swap does for the bridge alone what compose would do, so nobody is disconnected: the container is stopped, renamed aside, created
/// anew under its own name from its own inspect (only the image differs) and started, then verified: running, on the IP and MAC it is
/// configured with, GET /health answering from inside the game container, a log push arriving, and the game container untouched. Only
/// then is the old container removed; on any failure it is put back as it was. A stopped bridge is swapped the same way - checked against
/// its configured network, not the runtime address it lacks - and put back stopped; one already on the target image is started.</summary>
sealed class Bridge
{
    public static readonly string[] Sources = ["bridge", "core"];   // the build context: the bridge, and core/, the library it shares with the server programs
    public const string Dockerfile = "bridge/Dockerfile";           // within that context

    public const string InnerGet = "import sys,urllib.request;print(urllib.request.urlopen(sys.argv[1],timeout=5).read().decode())";
    public const string InnerRcon = "\n" + """
import socket,struct,sys
def pkt(i,t,b): b=b.encode()+b"\0\0"; return struct.pack("<iii",len(b)+8,i,t)+b
s=socket.create_connection(("127.0.0.1",int(sys.argv[3])),5)
def read():
    n=struct.unpack("<i",s.recv(4))[0]; d=b""
    while len(d)<n: d+=s.recv(n-len(d))
    return struct.unpack("<ii",d[:8])+(d[8:-2].decode(errors="replace"),)
s.sendall(pkt(1,3,sys.argv[1])); r=read()
if r[1]==0: r=read()
if r[0]==-1: sys.exit("rcon auth refused")
s.sendall(pkt(2,2,sys.argv[2])); print(read()[2])
""" + "\n";

    readonly IConfig? cfg;
    public readonly IDocker D;
    public readonly string RepoName;   // the image's name on the server, without a tag
    public readonly string Name;       // the bridge's container
    public readonly string Game;       // the game server's container
    public Action<double> Sleep = s => Thread.Sleep(TimeSpan.FromSeconds(s));

    public Bridge(IConfig cfg) : this(new Docker(cfg), cfg.Str("bridge.image", "localhost/overstrike2-bridge"),
                                      cfg.Str("bridge.container", required: true), cfg.Str("bridge.game_container", required: true), cfg)
    {
    }

    public Bridge(IDocker d, string repoName, string name, string game, IConfig? cfg = null)
    {
        this.cfg = cfg;
        D = d;
        RepoName = repoName;
        Name = name;
        Game = game;
    }

    // ---- what ships

    /// <summary>The build context as a tar: the committed bridge/ and core/ at HEAD, as the repository lays them out - the Dockerfile is
    /// bridge/Dockerfile - without core's self-test. A git archive holds committed files only, so no build output (bin/, obj/) is in it.</summary>
    public static (string Commit, byte[] Tar) Context()
    {
        var commit = Repo.CleanTree(Sources);
        var raw = Proc.CaptureBytes(["git", "-C", Repo.Root, "archive", "--format=tar", "HEAD", "--", .. Sources]).Out;
        var entries = new List<TarEntry>();
        using (var reader = new TarReader(new MemoryStream(raw)))
        {
            while (reader.GetNextEntry(copyData: true) is { } e)
                if (e.EntryType is not TarEntryType.GlobalExtendedAttributes)
                    entries.Add(e);
        }
        static string NameOf(TarEntry e) => e.Name.TrimEnd('/');
        List<string> leftOut = ["core/SelfTest"];
        var output = new MemoryStream();
        using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var e in entries)
            {
                var name = NameOf(e);
                if (leftOut.Any(p => name == p || name.StartsWith(p + "/", StringComparison.Ordinal)))
                    continue;
                e.DataStream?.Seek(0, SeekOrigin.Begin);
                writer.WriteEntry(new PaxTarEntry(e));
            }
        }
        Out.Log($"build context: the committed {string.Join(" and ", Sources.Select(s => s + "/"))} at {commit} ({output.Length / 1024} KB), "
                + $"left out: {string.Join(", ", leftOut)}");
        return (commit, output.ToArray());
    }

    // ---- build

    public string BuildLocal(string? tag)
    {
        var (commit, ctx) = Context();
        var engine = cfg?.Str("build.engine", "wslc") ?? "wslc";
        var image = $"{RepoName}:{(string.IsNullOrEmpty(tag) ? commit : tag)}";   // a test build carries only its own tag
        Directory.CreateDirectory(Repo.P(".work"));                                  // the repo's own scratch (gitignored)
        var work = Path.Combine(Repo.P(".work"), "bridge-build-" + Path.GetRandomFileName().Replace(".", "", StringComparison.Ordinal));
        Directory.CreateDirectory(work);
        TarFile.ExtractToDirectory(new MemoryStream(ctx), work, overwriteFiles: true);
        var archive = Path.Combine(work, "image.tar");
        Proc.Result Run(List<string> argv)
        {
            Out.Log("  " + string.Join(" ", argv.Take(2)) + " ...");
            return Proc.Capture(argv, below: true);
        }
        Out.Log($"building {image} locally with {engine} (below normal priority)");
        var t0 = Stopwatch.StartNew();
        var r = Run([engine, "build", "-t", image, "-f", Path.Combine([work, .. Dockerfile.Split('/')]), "-l", $"org.opencontainers.image.revision={commit}",
                     "-l", "org.opencontainers.image.title=Over Strike 2 bridge", work]);
        if (r.Code != 0)
            throw Out.Fail($"{engine} build: exit {r.Code}\n" + Py.Slice(r.All, -3000));
        Out.Log($"built in {Fmt.Fixed(t0.Elapsed.TotalSeconds, 0)} s; saving");
        r = Run([engine, "save", "-o", archive, image]);
        if (r.Code != 0)
            throw Out.Fail($"{engine} save: exit {r.Code}\n" + Py.Slice(r.All, -2000));
        var size = new FileInfo(archive).Length;
        Out.Log($"image archive {Fmt.Fixed(size / 1e6, 1)} MB; loading it into the server's Docker");
        var lines = D.Stream("POST", "/images/load?quiet=1", File.ReadAllBytes(archive), "application/x-tar");
        var errors = lines.Where(l => Json.Truthy(l["error"])).Select(l => Json.PyStr(l["error"])).ToList();
        if (errors.Count > 0)
            throw Out.Fail("image load: " + string.Join("; ", errors));
        var said = string.Join("; ", lines.Where(l => Json.Truthy(l["stream"])).Select(l => Py.Strip(Json.AsStr(l["stream"]) ?? "")));
        Out.Log("server: " + (said.Length > 0 ? said : "loaded"));
        Finish(commit, tag, image);
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return commit;
    }

    void Finish(string commit, string? tag, string image)
    {
        var built = D.Inspect("images", image);
        if (built is null || built.Count == 0)
            throw Out.Fail($"{image} is not on the server after the build");
        var id = Json.Str(built, "Id") ?? "";
        if (string.IsNullOrEmpty(tag))   // a release build: the candidate is the newest build, for swap
            D.Must("POST", $"/images/{id}/tag?repo={RepoName}&tag=candidate", $"tag {RepoName}:candidate");
        var labels = Json.Obj(built, "Config", "Labels");
        var rev = labels.TryGetPropertyValue("org.opencontainers.image.revision", out var rv) ? Json.PyStr(rv) : "?";
        Out.Log($"on the server: {image}{(string.IsNullOrEmpty(tag) ? $" = {RepoName}:candidate" : "")} = {Py.Slice(id, 7, 19)}, "
                + $"{Fmt.Fixed((Json.AsLong(built["Size"]) ?? 0) / 1e6, 0)} MB, revision {rev}");
    }

    // ---- read-only

    public string CheckCmd()
    {
        var commit = Repo.Git(["rev-parse", "--short=12", "HEAD"]);
        var dirty = Repo.Git(["status", "--porcelain", "--", .. Sources]);
        Out.Log($"would ship: the committed bridge/ and core/ at {commit}" + (dirty.Length > 0 ? " (uncommitted changes there are NOT shipped - commit first)" : ""));
        if (cfg is not null)
            Preflight.Show(cfg, " - a swap disconnects nobody");
        var b = D.Inspect("containers", Name);
        if (b is null || b.Count == 0)
            throw Out.Fail($"no container {Name} on the server");
        var image = Json.Str(b, "Image") ?? "";
        var img = D.Inspect("images", image) ?? [];
        var labels = Json.Obj(img, "Config", "Labels");
        var rev = labels.TryGetPropertyValue("org.opencontainers.image.revision", out var rv) ? Json.PyStr(rv) : "?";
        Out.Log($"running bridge: container {Py.Slice(Json.Str(b, "Id"), 0, 12)}, image {Py.Slice(image, 7, 19)} ({Json.PyStr(Json.Obj(b, "Config")["Image"])}, revision {rev}), "
                + $"started {Py.Slice(Json.Str(b, "State", "StartedAt"), 0, 19)}Z");
        return commit;
    }

    public void Images()
    {
        var r = D.Call("GET", "/images/json?filters=" + Fmt.Quote(Json.Dumps(new JsonObject { ["reference"] = new JsonArray(RepoName) })));
        foreach (var i in (r.Json as JsonArray ?? []).OfType<JsonObject>())
        {
            var created = DateTimeOffset.FromUnixTimeSeconds(Json.AsLong(i["Created"]) ?? 0).ToLocalTime();
            Out.Log($"{Py.Slice(Json.Str(i, "Id"), 7, 19)}  {string.Join(", ", Json.Arr(i, "RepoTags").Select(Json.PyStr))}  {Fmt.Fixed((Json.AsLong(i["Size"]) ?? 0) / 1e6, 0)} MB  "
                    + $"created {created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        }
    }

    // ---- swap

    public JsonObject CreateBody(JsonObject old, JsonObject oldImage, string newImageId)
    {
        var c = Json.Obj(old, "Config");
        var h = old["HostConfig"]?.DeepClone();
        var img = Json.Obj(oldImage, "Config");
        var imageEnv = Json.Arr(img, "Env").Select(Json.PyStr).ToHashSet(StringComparer.Ordinal);
        var labels = new JsonObject();
        foreach (var (k, v) in Json.Obj(c, "Labels"))
            if (!k.StartsWith("org.opencontainers.image.", StringComparison.Ordinal))
                labels[k] = v?.DeepClone();
        labels["com.docker.compose.image"] = newImageId;
        var config = new JsonObject
        {
            ["Image"] = Json.Truthy(c["Image"]) ? c["Image"]!.DeepClone() : RepoName,   // the reference compose names; it points at the new image by now
            ["Env"] = new JsonArray(Json.Arr(c, "Env").Where(e => !imageEnv.Contains(Json.PyStr(e))).Select(e => e?.DeepClone()).ToArray()),   // what compose set
            ["Labels"] = labels,
        };
        foreach (var k in new[] { "User", "ExposedPorts", "StopTimeout", "AttachStdout", "AttachStderr", "Tty", "OpenStdin", "Domainname" })
            if (!Json.Emptyish(c[k]))
                config[k] = c[k]!.DeepClone();
        foreach (var k in new[] { "Entrypoint", "Cmd", "WorkingDir", "Healthcheck", "StopSignal" })   // only where compose overrode the image
            if (c[k] is not null && !Json.Same(c[k], img[k]))
                config[k] = c[k]!.DeepClone();
        var endpoints = new JsonObject();
        foreach (var (net, v) in Json.Obj(old, "NetworkSettings", "Networks"))
        {
            var e = new JsonObject();
            foreach (var k in new[] { "IPAMConfig", "Aliases", "MacAddress", "DriverOpts", "Links" })
                if (Json.Truthy(Json.At(v, k)))
                    e[k] = Json.At(v, k)!.DeepClone();
            if (!Json.Truthy(e["MacAddress"]) && Json.Truthy(c["MacAddress"]))
                e["MacAddress"] = c["MacAddress"]!.DeepClone();   // an older engine keeps a stopped container's MAC in its config only
            endpoints[net] = e;
        }
        config["HostConfig"] = h;
        config["NetworkingConfig"] = new JsonObject { ["EndpointsConfig"] = endpoints };
        return config;
    }

    static List<NetAddress> Sorted(IEnumerable<NetAddress> a) =>
        a.OrderBy(x => x.Net, StringComparer.Ordinal).ThenBy(x => x.Ip ?? "", StringComparer.Ordinal).ThenBy(x => x.Mac ?? "", StringComparer.Ordinal).ToList();

    /// <summary>[(network, IP, MAC)] a container is on now: its runtime address, which a stopped container does not have.</summary>
    public static List<NetAddress> Addresses(JsonNode? c) =>
        Sorted(Json.Obj(c, "NetworkSettings", "Networks").Select(kv => new NetAddress(kv.Key, Json.Str(kv.Value, "IPAddress"), Json.Str(kv.Value, "MacAddress"))));

    /// <summary>[(network, IP, MAC)] a container is configured with: its IPAM address and its MAC - what a stopped container still names
    /// while its runtime address is empty, so a swap of a stopped bridge has something to recreate it on and to check it against.</summary>
    public static List<NetAddress> Pinned(JsonNode? c)
    {
        var cfgMac = Json.Str(c, "Config", "MacAddress") is { Length: > 0 } m ? m : "";
        return Sorted(Json.Obj(c, "NetworkSettings", "Networks").Select(kv => new NetAddress(kv.Key,
            Json.Str(kv.Value, "IPAMConfig", "IPv4Address") is { Length: > 0 } ip ? ip : Json.Str(kv.Value, "IPAddress") is { Length: > 0 } ip2 ? ip2 : "",
            Json.Str(kv.Value, "MacAddress") is { Length: > 0 } mac ? mac : cfgMac)));
    }

    string Health(JsonObject bridge)
    {
        var game = D.Inspect("containers", Game);
        return D.ExecIn(Game, ["python3", "-c", InnerGet, $"http://{Name}:{InspectOf.BridgePort(game, bridge)}/health/{InspectOf.BridgeSecret(game, bridge)}"]);
    }

    static int Pushes(string text)
    {
        var m = Regex.Match(text, @"log pushes (\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    List<string> Verify((string? Id, string? Started) gameBefore, List<NetAddress> want)
    {
        var problems = new List<string>();
        Sleep(8);
        var fresh = D.Inspect("containers", Name);
        if (fresh is null || fresh.Count == 0 || !Json.Truthy(Json.At(fresh, "State", "Running")) || Json.Truthy(fresh["RestartCount"]))
        {
            var st = fresh is null ? null : Json.At(fresh, "State");
            var status = st is JsonObject so && so.TryGetPropertyValue("Status", out var sv) ? Json.PyStr(sv) : "gone";
            return [$"not running ({status}, restarts {Json.PyStr(fresh?["RestartCount"])})"];
        }
        var got = Addresses(fresh);
        var line = $"{Name} on {Py.Repr(got)}" + (got.SequenceEqual(want) ? "" : $" - it was on {Py.Repr(want)}");
        if (got.SequenceEqual(want))
            Out.Log(line);
        else
            problems.Add(line);
        var game = D.Inspect("containers", Game);
        if ((Json.Str(game, "Id"), Json.Str(game, "State", "StartedAt")) != gameBefore)
            problems.Add($"{Game} is not the container it was before - it restarted");
        else
            Out.Log($"{Game} untouched: container {Py.Slice(Json.Str(game, "Id"), 0, 12)}, started {Py.Slice(Json.Str(game, "State", "StartedAt"), 0, 19)}Z");
        var h = Health(fresh);
        if (!h.StartsWith("gg-health", StringComparison.Ordinal))
            return [.. problems, "GET /health from the game container: " + Py.Slice(Py.Strip(h), 0, 200)];
        if (Pushes(h) == 0)   // say something, so the server has a line to push
        {
            var port = InspectOf.EnvOf(game, "RCON_PORT") is { Length: > 0 } p ? p : "27015";
            D.ExecIn(Game, ["python3", "-c", InnerRcon, InspectOf.RconPassword(game, fresh), "say The map bot was updated.", port]);
            for (var k = 0; k < 12; k++)
            {
                Sleep(5);
                h = Health(fresh);
                if (Pushes(h) != 0)
                    break;
            }
        }
        Out.Log("bridge /health:\n  " + string.Join("\n  ", Py.SplitLines(Py.Strip(h)).Skip(1)));
        if (Pushes(h) == 0)
            problems.Add("no log push has reached the new bridge within a minute");
        return problems;
    }

    public int Swap(string sourceTag, string keepTag)
    {
        var old = D.Inspect("containers", Name);
        if (old is null || old.Count == 0)
            throw Out.Fail($"no container {Name}");
        var target = D.Inspect("images", $"{RepoName}:{sourceTag}");
        if (target is null || target.Count == 0)
            throw Out.Fail($"no image {RepoName}:{sourceTag} on the server");
        // a stopped bridge is swapped too (a release may stop it to edit its state): what it is checked against is the network it is
        // configured with, not the runtime address it has only while it runs - and it is put back stopped
        var running = Json.Truthy(Json.At(old, "State", "Running"));
        var want = Pinned(old);
        if (want.Count == 0 || want.Any(w => string.IsNullOrEmpty(w.Ip) || string.IsNullOrEmpty(w.Mac)))
            throw Out.Fail($"{Name} names no pinned IP and MAC to recreate it on ({Py.Repr(want)}) - nothing changed; "
                           + $"{(running ? "" : "start it once, or ")}redeploy the stack");
        var game = D.Inspect("containers", Game) ?? throw new KeyNotFoundException($"no container {Game}");
        var gameBefore = (Json.Str(game, "Id"), Json.Str(game, "State", "StartedAt"));
        var targetId = Json.Str(target, "Id") ?? "";
        var oldId = Json.Str(old, "Id") ?? "";
        var oldImage = Json.Str(old, "Image") ?? "";
        if (targetId == oldImage)
        {
            if (running)
            {
                Out.Log($"{Name} already runs {RepoName}:{sourceTag} ({Py.Slice(targetId, 7, 19)}) - nothing to do");
                return 0;
            }
            Out.Log($"{Name} is stopped on {RepoName}:{sourceTag} ({Py.Slice(targetId, 7, 19)}) already - starting it");
            D.Must("POST", $"/containers/{oldId}/start", "start the bridge");
            var issues = Verify(gameBefore, want);
            if (issues.Count == 0)
            {
                Out.Log($"DONE: {Name} runs {Py.Slice(targetId, 7, 19)}");
                return 0;
            }
            Out.Log("PROBLEMS: " + string.Join("; ", issues) + " - stopping it again, as it was");
            D.Call("POST", $"/containers/{oldId}/stop?t=10", timeout: 60);
            return 1;
        }
        if (!running)
            Out.Log($"{Name} is stopped - swapped all the same, onto the network it is configured with: {Py.Repr(want)}");
        var oldImageInspect = D.Inspect("images", oldImage) ?? [];
        var body = CreateBody(old, oldImageInspect, targetId);
        var runningRef = Json.Str(old, "Config", "Image") is { Length: > 0 } r ? r : RepoName;
        var colon = runningRef.IndexOf(':');
        var refRepo = colon < 0 ? runningRef : runningRef[..colon];
        var refTag = colon < 0 || colon == runningRef.Length - 1 ? "latest" : runningRef[(colon + 1)..];
        D.Must("POST", $"/images/{oldImage}/tag?repo={RepoName}&tag={keepTag}", $"tag the old image {RepoName}:{keepTag}");
        D.Must("POST", $"/images/{targetId}/tag?repo={refRepo}&tag={refTag}", $"tag {runningRef}");
        Out.Log($"{RepoName}:{keepTag} = {Py.Slice(oldImage, 7, 19)} ({(running ? "running" : "stopped")}), {refRepo}:{refTag} = {Py.Slice(targetId, 7, 19)} (new)");
        var aside = $"{Name}-replaced-{Out.Clock().ToString("HHmmss", CultureInfo.InvariantCulture)}";
        string? newId = null;
        List<string> problems;
        try
        {
            if (running)
                D.Must("POST", $"/containers/{oldId}/stop?t=15", "stop the old bridge", timeout: 60);
            D.Must("POST", $"/containers/{oldId}/rename?name={aside}", "rename the old bridge aside");
            newId = Json.Str(D.Must("POST", $"/containers/create?name={Name}", "create the new bridge", body: body).Json, "Id");
            D.Must("POST", $"/containers/{newId}/start", "start the new bridge");
            Out.Log($"new {Name} {Py.Slice(newId, 0, 12)} started (the old one is stopped, as {aside})");
            problems = Verify(gameBefore, want);
        }
        catch (Exit e)
        {
            problems = [e.Message];
        }
        if (problems.Count == 0)
        {
            D.Must("DELETE", $"/containers/{oldId}", "remove the old bridge");
            Out.Log($"DONE: {Name} runs {Py.Slice(targetId, 7, 19)}; rollback: os2-deploy bridge rollback");
            return 0;
        }
        Out.Log("PROBLEMS: " + string.Join("; ", problems) + " - putting the old bridge back");
        if (newId is not null)
        {
            D.Call("POST", $"/containers/{newId}/stop?t=10", timeout: 60);
            D.Call("DELETE", $"/containers/{newId}?force=1");
        }
        D.Call("POST", $"/containers/{oldId}/rename?name={Name}");
        D.Call("POST", $"/images/{oldImage}/tag?repo={refRepo}&tag={refTag}");
        if (!running)
        {
            var back0 = D.Inspect("containers", Name);
            Out.Log($"old bridge {(back0 is not null && Json.Str(back0, "Id") == oldId ? "back, stopped as it was" : "NOT back under its name - rename it by hand")}");
            return 1;
        }
        var s = D.Call("POST", $"/containers/{oldId}/start");
        var back = D.Inspect("containers", Name);
        Out.Log($"old bridge {(back is not null && Json.Str(back, "Id") == oldId && Json.Truthy(Json.At(back, "State", "Running")) ? "back up" : "NOT back - start it by hand")} (start HTTP {s.Status})");
        return 1;
    }

    public static int Command(string[] args)
    {
        var a = new Args(args, "bridge check|build|images|swap|update|rollback [--tag T] [--config FILE]", options: ["--tag", "--config"]);
        var command = a.Choice(["check", "build", "images", "swap", "update", "rollback"]);
        a.Done();
        var b = new Bridge(new Config(a.Opt("--config")));
        switch (command)
        {
            case "check":
                b.CheckCmd();
                break;
            case "images":
                b.Images();
                break;
            case "build":
                b.BuildLocal(a.Opt("--tag"));
                break;
            case "swap":
                return b.Swap("candidate", "previous");
            case "update":
                b.CheckCmd();
                b.BuildLocal(null);
                return b.Swap("candidate", "previous");
            case "rollback":
                return b.Swap("previous", "undone");
        }
        return 0;
    }
}

/// <summary>Numbers and URLs written the Python way.</summary>
static class Fmt
{
    /// <summary>f"{x:.Nf}": rounded half to even, as Python rounds a float it prints.</summary>
    public static string Fixed(double x, int digits) =>
        Math.Round(x, digits, MidpointRounding.ToEven).ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    static bool Unreserved(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~';

    /// <summary>urllib.parse.quote(s): every byte but the unreserved ones and the safe ones percent-encoded.</summary>
    public static string Quote(string s, string safe = "/")
    {
        var b = new StringBuilder();
        foreach (var by in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)by;
            if (by < 0x80 && (Unreserved(c) || safe.Contains(c)))
                b.Append(c);
            else
                b.Append('%').Append(by.ToString("X2", CultureInfo.InvariantCulture));
        }
        return b.ToString();
    }

    /// <summary>urllib.parse.quote_plus(s): quote with spaces as plus signs.</summary>
    public static string QuotePlus(string s) => Quote(s, " ").Replace(' ', '+');
}
