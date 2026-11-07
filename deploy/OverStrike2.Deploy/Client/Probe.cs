using System.Formats.Tar;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Deploy;

/// <summary>A Docker Engine API call that answered with an error status: its words carry the code ("HTTP Error 404: ...").</summary>
sealed class DockerError(string message) : Exception(message);

/// <summary>An answer of the harness's one door to the Docker API: the status and the body's bytes (JSON as its text).</summary>
sealed record Reply(int Status, byte[] Body)
{
    public string Text => Encoding.UTF8.GetString(Body);
    public JsonNode? Json => Deploy.Json.Parse(Body);

    public static Reply Of(int status, string text) => new(status, Encoding.UTF8.GetBytes(text));
    public static Reply Of(int status, JsonNode json) => Of(status, Deploy.Json.Dumps(json));
}

/// <summary>The server half of the CS2 client test harness (tools/client/README.md; tools/client/probe/probe.py, ported): the server lock,
/// the Docker API and RCON that the harness's verbs and the test copy use, and the test-only GgClientProbe plugin, loaded into the game
/// container for one test and taken out again.
///
/// Nothing here knows a deployment. Everything comes from the private config: the server's query address (server.address), the lock file
/// (server.lock_file), the game server's and the bridge's containers (bridge.game_container, bridge.container), the Docker Engine API
/// (docker.*, its key an env: value). The RCON password is the game container's own (CS2_RCONPW in its inspect), handed to one exec in its
/// environment; it is never printed. Production actions need the server lock: `load` needs it, `unload` releases it. Nothing is ever written
/// to the plugins folder: the probe lives in /tmp of the game container, loaded by `css_plugins load &lt;absolute path&gt;`.
///
///     probe lock "&lt;purpose&gt;"                 take the server lock (refuses if anyone holds it)
///     probe purpose &lt;minutes&gt; "&lt;purpose&gt;"    the lock's since, expected end and purpose anew, its owner line kept (the harness's lock only)
///     probe unlock                           release it, if it is the harness's
///     probe load                             build if needed, copy the DLL to /tmp/ggprobe in the game container, css_plugins load it
///     probe players                          who is on (slot, name, account id)
///     probe html &lt;account|*&gt; &lt;seconds&gt; &lt;html or @file&gt;   centre-screen HTML to that player only, re-sent every tick
///     probe htmlfile &lt;account|*&gt; &lt;seconds&gt; &lt;file&gt;         the same from a file of any length
///     probe stop                             clear the probe's messages
///     probe clientcmd &lt;account&gt; &lt;command&gt;    ExecuteClientCommand (only FCVAR_CLIENT_CAN_EXECUTE commands run)
///     probe rcon "&lt;command&gt;" ["&lt;command&gt;" ...]   server console commands, each one's answer printed
///     probe unload                           css_plugins unload, delete /tmp/ggprobe, verify both, release the lock - kept while
///                                            the game server is a test copy (testcopy restore first)
///     probe status                           the probe's state on the server and the lock
///     probe config                           what the harness reads from the private config, as JSON (no credential in it)
///     probe server                           the running containers that publish the server's port, and the game container, as JSON</summary>
sealed class Harness
{
    public const string Owner = "cs2 client test harness (tools/client)";   // the lock's first line is "owner: " and this
    public const string RemoteDir = "/tmp/ggprobe";
    public const string RemoteDll = RemoteDir + "/GgClientProbe.dll";
    const int GameUid = 1000;                                                // the game image's user: the files it is handed are its own
    public static string ProbeProject => Repo.P("tools", "client", "probe", "GgClientProbe");
    static string ProbeDll => Path.Combine(ProbeProject, "bin", "Release", "net10.0", "GgClientProbe.dll");

    /// <summary>RCON from inside the game container, the way the game server's own tools reach it: no RCON port needs to reach this PC. The
    /// password and the port come from the exec's environment, the commands from its arguments; an answer in several packets is read whole.</summary>
    public const string InnerRcon = "\n" + """
import os,socket,struct,sys
s=socket.create_connection(("127.0.0.1",int(os.environ.get("OS2_RCON_PORT") or 27015)),5)
def pkt(i,t,b): b=b.encode()+b"\0\0"; return struct.pack("<iii",len(b)+8,i,t)+b
def take(n):
    d=b""
    while len(d)<n:
        c=s.recv(n-len(d))
        if not c: raise EOFError
        d+=c
    return d
def read():
    n=struct.unpack("<i",take(4))[0]; d=take(n)
    return struct.unpack("<ii",d[:8])+(d[8:-2].decode("utf-8","replace"),)
s.sendall(pkt(1,3,os.environ["OS2_RCON_PW"])); r=read()
if r[1]==0: r=read()
if r[0]==-1: sys.exit("rcon: the password was refused")
for k,c in enumerate(sys.argv[1:]):
    s.settimeout(30); s.sendall(pkt(10+k,2,c)); out=[read()[2]]
    s.settimeout(0.3)
    try:
        while True: out.append(read()[2])
    except (socket.timeout,EOFError): pass
    print("".join(out),flush=True)
""" + "\n";

    readonly string? configPath;
    IConfig? cfg;
    IDocker? docker;

    // the seams the self-tests replace: the one door to the Docker API, RCON, the lock check, the clock
    public Func<string, string, JsonNode?, byte[]?, string, double, Reply> Req;
    public Func<string[], string> Rcon;
    public Func<string, IReadOnlyList<string>, Dictionary<string, string>?, double, string> Exec;
    public Action NeedLock;
    public Action<double> Sleep = s => Thread.Sleep(TimeSpan.FromSeconds(s));
    public Func<double> Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public Harness(string? configPath = null)
    {
        this.configPath = configPath;
        Req = RealReq;
        Rcon = RealRcon;
        Exec = RealExec;
        NeedLock = RealNeedLock;
    }

    // ---------------------------------------------------------------------------------------------------------- the deployment

    /// <summary>Whether there is a private config to read: the offline modes do without one.</summary>
    public bool HasConfig => Config.Exists(configPath);

    /// <summary>The private config, read once, on first use: the offline tests never need one.</summary>
    public IConfig Cfg => cfg ??= new Config(configPath);

    IDocker Docker => docker ??= new Docker(Cfg);

    /// <summary>The game server's container: production's, or the test copy under the same name.</summary>
    public string Game => Cfg.Str("bridge.game_container", required: true);

    public string BridgeName => Cfg.Str("bridge.container", required: true);

    /// <summary>The game server's query address, host:port (server.address).</summary>
    public string Address => Cfg.Str("server.address", required: true);

    public (string Host, int Port) HostPort
    {
        get
        {
            var a = Address;
            var colon = a.LastIndexOf(':');
            return (colon < 0 ? "" : a[..colon], int.Parse(a[(colon + 1)..], CultureInfo.InvariantCulture));
        }
    }

    public string LockPath
    {
        get
        {
            var p = Cfg.Str("server.lock_file");
            if (p.Length == 0)
                throw Out.Stop("REFUSED: no server.lock_file in the private config - the harness acts on the server only under a lock file; name one");
            return p;
        }
    }

    /// <summary>client.test_labels: extra labels every test container gets (a monitor's mute, say); empty when the config has none.</summary>
    public Dictionary<string, string> TestLabels => Cfg.Table("client.test_labels").ToDictionary(kv => kv.Key, kv => Py.Str(kv.Value));

    /// <summary>The repository's main working tree: a worktree's .git file names &lt;main&gt;/.git/worktrees/&lt;name&gt;, so every worktree shares
    /// one state.</summary>
    public static string MainCheckout(string? start = null)
    {
        start ??= Repo.Root;
        var dotgit = Path.Combine(start, ".git");
        if (File.Exists(dotgit))
        {
            var lines = File.ReadAllText(dotgit).Split('\n');
            var line = new FileInfo(dotgit).Length > 0 ? lines[0].TrimEnd('\r') : "";
            if (line.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                var gitdir = line[7..].Trim();
                gitdir = Path.IsPathRooted(gitdir) ? gitdir : Path.Combine(start, gitdir);
                var common = Directory.GetParent(Directory.GetParent(Path.GetFullPath(gitdir))!.FullName)!;
                if (common.Name == ".git")
                    return common.Parent!.FullName;
            }
        }
        return start;
    }

    /// <summary>Where the harness keeps its state: $CS2CLIENT_STATE, else .work/cs2-client in the main checkout.</summary>
    public static string StateDir => Environment.GetEnvironmentVariable("CS2CLIENT_STATE") is { Length: > 0 } env ? env : Path.Combine(MainCheckout(), ".work", "cs2-client");

    // ------------------------------------------------------------------------------------------------------------------ Steam

    /// <summary>The account id (the n of [U:1:n]) Steam is logged in with on this PC, which the harness's client plays under; 0 when unknown.</summary>
    public static long SteamAccount()
    {
        if (!OperatingSystem.IsWindows())
            return 0;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            return k?.GetValue("ActiveUser") is int i ? i : k?.GetValue("ActiveUser") is { } o && long.TryParse(o.ToString(), out var l) ? l : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return 0;
        }
    }

    /// <summary>The persona name of that account, from Steam's loginusers.vdf: what the game shows of the client. Empty when unknown.</summary>
    public static List<string> SteamNames(long? account = null)
    {
        var a = account ?? SteamAccount();
        if (a == 0 || !OperatingSystem.IsWindows())
            return [];
        string text;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("SteamPath") is not string steam)
                return [];
            text = File.ReadAllText(Path.Combine(steam, "config", "loginusers.vdf"), Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
        var block = Regex.Match(text, "\"" + (76561197960265728 + a).ToString(CultureInfo.InvariantCulture) + @"""\s*\{([^}]*)\}");
        var name = block.Success ? Regex.Match(block.Groups[1].Value, @"""PersonaName""\s+""([^""]*)""") : Match.Empty;
        return name.Success && Py.Strip(name.Groups[1].Value).Length > 0 ? [name.Groups[1].Value] : [];
    }

    /// <summary>Names that must never reach a picture, a command or a log: the client's own Steam name, and client.private_names.</summary>
    public List<string> PrivateNames()
    {
        var names = SteamNames();
        if (HasConfig)
            names.AddRange(Cfg.List("client.private_names").Select(Py.Str));
        return names.Distinct().Where(n => Py.Strip(n).Length > 0).ToList();
    }

    // -------------------------------------------------------------------------------------------------------------- the Docker API

    Reply RealReq(string path, string method, JsonNode? body, byte[]? raw, string ctype, double timeout)
    {
        var r = Docker.Call(method, path, body: raw is null ? body : null, raw: raw, ctype: ctype, timeout: timeout);
        if (r.Status >= 400)
            throw new DockerError($"HTTP Error {r.Status}: {Py.Slice(r.Shown, 0, 300)}");
        return new Reply(r.Status, r.Json is not null ? Encoding.UTF8.GetBytes(Json.Dumps(r.Json)) : r.Bytes ?? []);
    }

    /// <summary>One call through the door: (status, body). An error status throws DockerError, its code in the words.</summary>
    public Reply Call(string path, string method = "GET", JsonNode? body = null, byte[]? raw = null, string ctype = "application/json", double timeout = 120) =>
        Req(path, method, body, raw, ctype, timeout);

    /// <summary>A container's inspect, or null when there is no such container.</summary>
    public JsonObject? Inspect(string name)
    {
        try
        {
            return Call($"/containers/{name}/json").Json as JsonObject;
        }
        catch (DockerError e) when (e.Message.Contains("404", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>A container's output without a TTY, as text.</summary>
    public static string Demux(byte[] raw) => Encoding.UTF8.GetString(DockerBase.Demux(raw, strict: true));

    /// <summary>One exec in a container, its output (stdout and stderr) as text. env: variables for this exec alone - how a secret travels.</summary>
    public string ExecIn(string container, IReadOnlyList<string> cmd, Dictionary<string, string>? env = null, double timeout = 120) =>
        Exec(container, cmd, env, timeout);

    string RealExec(string container, IReadOnlyList<string> cmd, Dictionary<string, string>? env, double timeout)
    {
        var body = new JsonObject { ["AttachStdout"] = true, ["AttachStderr"] = true, ["Cmd"] = new JsonArray(cmd.Select(c => (JsonNode?)c).ToArray()) };
        if (env is { Count: > 0 })
            body["Env"] = new JsonArray(env.Select(kv => (JsonNode?)$"{kv.Key}={kv.Value}").ToArray());
        var created = Call($"/containers/{container}/exec", "POST", body);
        var id = Json.Str(created.Json, "Id") ?? "";
        var started = Call($"/exec/{id}/start", "POST", new JsonObject { ["Detach"] = false, ["Tty"] = false }, timeout: timeout);
        return Demux(started.Body);
    }

    public string ExecInGame(IReadOnlyList<string> cmd) => ExecIn(Game, cmd);

    /// <summary>Server console commands by RCON from inside the game container, each one's answer in order. The password is the container's
    /// own (its inspect), handed to the exec in its environment and scrubbed from the answer, should it ever be in there.</summary>
    string RealRcon(string[] commands)
    {
        var g = Inspect(Game) ?? throw Out.Stop($"no container {Game}");
        var pw = InspectOf.RconPassword(g) is { Length: > 0 } p ? p : InspectOf.RconPassword(null, Inspect(BridgeName));
        if (pw.Length == 0)
            throw Out.Stop($"no RCON password in {Game}'s inspect (CS2_RCONPW)");
        var port = InspectOf.EnvOf(g, "RCON_PORT") is { Length: > 0 } rp ? rp : "27015";
        var output = ExecIn(Game, ["python3", "-c", InnerRcon, .. commands], new() { ["OS2_RCON_PW"] = pw, ["OS2_RCON_PORT"] = port });
        return output.Replace(pw, "<hidden>", StringComparison.Ordinal).Replace("\0", "", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------- the lock

    public string? LockText() => File.Exists(LockPath) ? File.ReadAllText(LockPath, Encoding.UTF8) : null;

    public bool HoldsLock() => LockText() is { } t && t.StartsWith($"owner: {Owner}", StringComparison.Ordinal);

    void RealNeedLock()
    {
        if (!HoldsLock())
            throw Out.Stop($"the server lock is not held by the harness - run `os2-deploy probe lock \"<purpose>\"` first\ncurrent lock: {Py.Repr(LockText())}");
    }

    public static string LockWords(string purpose, int minutes = 30)
    {
        var now = Out.Clock();
        return $"owner: {Owner}\nsince: {now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}\n"
               + $"expected end: {now.AddMinutes(minutes).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}\npurpose: {purpose}\n";
    }

    public void CmdLock(string purpose)
    {
        try
        {
            using var fs = new FileStream(LockPath, FileMode.CreateNew, FileAccess.Write);
            fs.Write(Encoding.UTF8.GetBytes(LockWords(purpose)));
        }
        catch (IOException) when (File.Exists(LockPath))
        {
            throw Out.Stop($"REFUSED: the server lock is held:\n{LockText()}");
        }
        Out.Print($"server lock taken: {purpose}");
    }

    /// <summary>After a hand-over: the harness's lock keeps its owner line and gets this session's since, expected end and purpose.</summary>
    public void CmdPurpose(string minutes, string purpose)
    {
        if (!HoldsLock())
        {
            var t = LockText();
            throw Out.Stop($"REFUSED: the lock is not the harness's: {(t is not null ? Py.Slice(Py.SplitLines(t).FirstOrDefault() ?? "", 0, 100) : "(none)")}");
        }
        File.WriteAllText(LockPath, LockWords(purpose, int.Parse(minutes, CultureInfo.InvariantCulture)));
        Out.Print(LockText() ?? "");
    }

    public void CmdUnlock()
    {
        if (HoldsLock())
        {
            File.Delete(LockPath);
            Out.Print("server lock released");
        }
        else
            Out.Print($"not ours, left alone: {Py.Repr(LockText())}");
    }

    // ------------------------------------------------------------------------------------------------------- the probe plugin

    /// <summary>files: {name: bytes} -> one tar into the game container's /tmp/&lt;folder&gt; (world-readable, owned by the game's user).</summary>
    public int Upload(Dictionary<string, byte[]> files, string folder = "ggprobe")
    {
        var now = DateTimeOffset.FromUnixTimeSeconds((long)Now());
        var buf = new MemoryStream();
        using (var tar = new TarWriter(buf, TarEntryFormat.Gnu, leaveOpen: true))
        {
            tar.WriteEntry(new GnuTarEntry(TarEntryType.Directory, folder)
            {
                Mode = (UnixFileMode)0b111_101_101, Uid = GameUid, Gid = GameUid, ModificationTime = now,
            });
            foreach (var (name, data) in files)
                tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, $"{folder}/{name}")
                {
                    Mode = (UnixFileMode)0b110_100_100, Uid = GameUid, Gid = GameUid, ModificationTime = now, DataStream = new MemoryStream(data),
                });
        }
        return Call($"/containers/{Game}/archive?path=/tmp", "PUT", raw: buf.ToArray(), ctype: "application/x-tar").Status;
    }

    void CmdLoad()
    {
        NeedLock();
        var build = Proc.Run(["dotnet", "build", "-c", "Release", "-nologo", "-v", "q", "-nodeReuse:false", "-p:UseSharedCompilation=false"], cwd: ProbeProject, below: true);
        if (build != 0)
            throw new Exit(1, $"dotnet build of GgClientProbe: exit {build}");
        var loaded = Py.SplitLines(Rcon(["css_plugins list"])).Where(l => l.Contains("GgClientProbe", StringComparison.Ordinal)).ToList();
        if (loaded.Count > 0)   // a newer build replaces the loaded one
            Out.Print(Py.Strip(Rcon(["css_plugins unload GgClientProbe"])));
        var data = File.ReadAllBytes(ProbeDll);
        var st = Upload(new() { ["GgClientProbe.dll"] = data });
        Out.Print($"copied {data.Length} bytes to {Game}:{RemoteDll} (HTTP {st})");
        Out.Print(Py.Strip(Rcon([$"css_plugins load {RemoteDll}"])));
        Sleep(1);
        var line = Py.SplitLines(Rcon(["css_plugins list"])).Where(l => l.Contains("GgClientProbe", StringComparison.Ordinal)).ToList();
        Out.Print("css_plugins list: " + (line.Count > 0 ? Py.Repr(line) : "GgClientProbe NOT LISTED"));
        if (line.Count == 0)
            throw new Exit(1);
    }

    public void CmdUnload()
    {
        Out.Print(Py.Strip(Rcon(["css_plugins unload GgClientProbe"])));
        var removed = Py.Strip(ExecInGame(["rm", "-rf", RemoteDir]));
        Out.Print(removed.Length > 0 ? removed : $"removed {RemoteDir}");
        var left = Rcon(["css_plugins list"]);
        // CounterStrikeSharp keeps an unloaded plugin in its list as [#n:UNLOADED] until the server restarts: inert, its DLL gone.
        var still = Py.SplitLines(left).Where(l => l.Contains("GgClientProbe", StringComparison.Ordinal) && !l.Contains("UNLOADED", StringComparison.Ordinal)).ToList();
        var files = Py.Strip(ExecInGame(["sh", "-c", $"ls -la {RemoteDir} 2>&1 || true"]));
        var listed = Py.SplitLines(left).Where(l => l.Contains("GgClientProbe", StringComparison.Ordinal)).Select(Py.Strip).ToList();
        Out.Print("css_plugins list: " + (still.Count > 0 ? Py.Repr(still) : listed.Count > 0 ? Py.Repr(listed) : "GgClientProbe gone"));
        Out.Print($"{Game}:{RemoteDir}: {files}");
        if (still.Count > 0 || !files.Contains("No such file", StringComparison.Ordinal))
            throw Out.Stop("the probe is NOT fully gone - check by hand before releasing the lock");
        if (TestCopy.IsTest(Inspect(Game)))
        {
            Out.Print("server lock kept: the game server is a test copy, and production is back only after testcopy rm and restore (then probe unlock)");
            return;
        }
        CmdUnlock();
    }

    void CmdHtml(string who, string seconds, string html)
    {
        if (html.StartsWith('@'))
            html = Py.Strip(File.ReadAllText(html[1..], Encoding.UTF8));
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(html)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Out.Print(Py.Strip(Rcon([$"ggprobe_html64 {who} {(int)double.Parse(seconds, CultureInfo.InvariantCulture)} {b64}"])));
    }

    /// <summary>A message of any length: uploaded as /tmp/ggprobe/msg.html, then ggprobe_htmlfile.</summary>
    void CmdHtmlFile(string who, string seconds, string path)
    {
        Upload(new() { ["msg.html"] = File.ReadAllBytes(path) });
        Out.Print(Py.Strip(Rcon([$"ggprobe_htmlfile {who} {(int)double.Parse(seconds, CultureInfo.InvariantCulture)} {RemoteDir}/msg.html"])));
    }

    void CmdStatus()
    {
        Out.Print("lock: " + (LockText() is { Length: > 0 } t ? t : "free"));
        var listed = Py.SplitLines(Rcon(["css_plugins list"])).Where(l => l.Contains("GgClientProbe", StringComparison.Ordinal)).Select(Py.Strip).ToList();
        Out.Print("css_plugins list: " + (listed.Count > 0 ? Py.Repr(listed) : "GgClientProbe not loaded"));
        Out.Print($"{Game}:{RemoteDir}: {Py.Strip(ExecInGame(["sh", "-c", $"ls -la {RemoteDir} 2>&1 || true"]))}");
    }

    // ------------------------------------------------------------------------------------------- what the PowerShell side asks

    /// <summary>The harness's view of the private config, as JSON: no credential is in it.</summary>
    void CmdConfig() => Out.Print(Json.Dumps(new JsonObject
    {
        ["address"] = Address, ["lock_file"] = Cfg.Str("server.lock_file"), ["game_container"] = Game, ["bridge_container"] = BridgeName, ["owner"] = Owner,
    }));

    /// <summary>The running containers that publish the server's port, and the game container: what answers on the address right now (a test
    /// copy answers there under production's name).</summary>
    void CmdServer()
    {
        var port = HostPort.Port;
        var rows = new JsonArray();
        foreach (var c in (Call("/containers/json").Json as JsonArray ?? []).OfType<JsonObject>())
        {
            var ports = Json.Arr(c, "Ports").Select(p => Json.AsLong(Json.At(p, "PublicPort"))).Where(p => p is > 0).Select(p => p!.Value).Distinct().Order().ToList();
            var first = Json.AsStr(Json.Arr(c, "Names").FirstOrDefault()) ?? "";
            if (ports.Contains(port) || first == "/" + Game)
                rows.Add(new JsonObject
                {
                    ["name"] = first.TrimStart('/'), ["state"] = Json.At(c, "State")?.DeepClone(), ["status"] = Json.At(c, "Status")?.DeepClone(),
                    ["image"] = Py.Slice(Json.Str(c, "Image"), 0, 71), ["ports"] = new JsonArray(ports.Select(p => (JsonNode?)p).ToArray()),
                    ["test"] = Json.Obj(c, "Labels").ContainsKey(TestCopy.TestLabel),
                });
        }
        Out.Print(Json.Dumps(rows));
    }

    public const string Doc = """
probe lock "<purpose>" | purpose <minutes> "<purpose>" | unlock | load | players | html <account|*> <seconds> <html or @file> |
      htmlfile <account|*> <seconds> <file> | stop | clientcmd <account> <command> | rcon "<command>" [...] | unload | status | config | server
""";

    public static int Command(string[] argv)
    {
        var (args, config) = TakeConfig(argv);
        if (args.Count == 0)
        {
            Out.Print("usage: os2-deploy " + Doc.TrimEnd());
            return 0;
        }
        var h = new Harness(config);
        var (verb, rest) = (args[0], args.Skip(1).ToList());
        switch (verb)
        {
            case "lock":
                h.CmdLock(rest.Count > 0 ? string.Join(" ", rest) : "client test");
                break;
            case "purpose":
                if (rest.Count != 2 || !Py.IsDigits(rest[0]))
                    throw Out.Stop("usage: os2-deploy probe purpose <minutes> \"<purpose>\"");
                h.CmdPurpose(rest[0], rest[1]);
                break;
            case "unlock":
                h.CmdUnlock();
                break;
            case "load":
                h.CmdLoad();
                break;
            case "unload":
                h.CmdUnload();
                break;
            case "players":
                Out.Print(Py.Strip(h.Rcon(["ggprobe_players"])));
                break;
            case "html":
                h.NeedLock();
                h.CmdHtml(rest[0], rest[1], string.Join(" ", rest.Skip(2)));
                break;
            case "htmlfile":
                h.NeedLock();
                h.CmdHtmlFile(rest[0], rest[1], rest[2]);
                break;
            case "stop":
                Out.Print(Py.Strip(h.Rcon(["ggprobe_stop"])));
                break;
            case "clientcmd":
                h.NeedLock();
                Out.Print(Py.Strip(h.Rcon([$"ggprobe_clientcmd {rest[0]} {string.Join(" ", rest.Skip(1))}"])));
                break;
            case "rcon":
                if (rest.Count == 0)
                    throw Out.Stop("usage: os2-deploy probe rcon \"<command>\" [\"<command>\" ...]");
                Out.Print(Py.Strip(h.Rcon([.. rest])));
                break;
            case "status":
                h.CmdStatus();
                break;
            case "config":
                h.CmdConfig();
                break;
            case "server":
                h.CmdServer();
                break;
            default:
                throw Out.Stop($"unknown verb {verb}\nusage: os2-deploy {Doc.TrimEnd()}");
        }
        return 0;
    }

    /// <summary>--config FILE (or --config=FILE) taken out of a verb's arguments, which are its own otherwise.</summary>
    public static (List<string> Args, string? Config) TakeConfig(string[] argv)
    {
        var args = new List<string>();
        string? config = null;
        for (var i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--config" && i + 1 < argv.Length)
                config = argv[++i];
            else if (argv[i].StartsWith("--config=", StringComparison.Ordinal))
                config = argv[i][9..];
            else
                args.Add(argv[i]);
        }
        return (args, config);
    }
}

static class Probe
{
    public static int Command(string[] args) => Harness.Command(args);
}
