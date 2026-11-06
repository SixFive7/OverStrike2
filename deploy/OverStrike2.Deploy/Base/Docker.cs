using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace OverStrike2.Deploy;

/// <summary>A Docker Engine API answer: its status and what came back - JSON when the body starts with { or [, else its bytes; an error
/// status's body as text (Error).</summary>
sealed record DockerReply(int Status, JsonNode? Json = null, byte[]? Bytes = null, string? Error = null)
{
    /// <summary>The body as Python's str() of it: JSON as its repr, bytes as b'...', an error's text as itself.</summary>
    public string Shown => Error ?? (Json is not null ? global::OverStrike2.Deploy.Json.PyRepr(Json) : Bytes is not null ? "b'" + Encoding.UTF8.GetString(Bytes) + "'" : "None");

    /// <summary>The body as text: JSON as its JSON, bytes decoded, an error's text.</summary>
    public string Text => Error ?? (Json is not null ? global::OverStrike2.Deploy.Json.Dumps(Json) : Bytes is not null ? Encoding.UTF8.GetString(Bytes) : "");
}

/// <summary>The calls the commands make on the Docker Engine API; the self-tests stand in for it.</summary>
interface IDocker
{
    /// <summary>Extra request headers a proxy wants (docker.headers), values resolved: check.py's get_json sends them to the stack manager.</summary>
    IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Whether TLS certificates are checked (docker.tls_verify).</summary>
    bool TlsVerify { get; }

    DockerReply Call(string method, string path, JsonNode? body = null, byte[]? raw = null, string ctype = "application/json", double timeout = 60);

    /// <summary>A call that must answer with one of the ok statuses, else the command fails with what.</summary>
    DockerReply Must(string method, string path, string what, int[]? ok = null, JsonNode? body = null, double timeout = 60);

    /// <summary>POST a large body (a build context, an image archive) and read the JSON-lines progress the daemon answers with.</summary>
    List<JsonObject> Stream(string method, string path, byte[] raw, string ctype, double timeout = 1800);

    /// <summary>GET /{kind}/{name}/json - an inspect, or null when the answer is not 200.</summary>
    JsonObject? Inspect(string kind, string name);

    /// <summary>One exec in a container, its output (stdout and stderr) as text; "(exec create N)" or "(exec start N)" when it failed.</summary>
    string ExecIn(string container, IReadOnlyList<string> argv, double timeout = 60);
}

/// <summary>What a Docker stand-in shares with the real one: Must and Inspect on top of its own Call.</summary>
abstract class DockerBase : IDocker
{
    public virtual IReadOnlyDictionary<string, string> Headers { get; } = new Dictionary<string, string>();
    public virtual bool TlsVerify => true;

    public abstract DockerReply Call(string method, string path, JsonNode? body = null, byte[]? raw = null, string ctype = "application/json", double timeout = 60);

    public virtual DockerReply Must(string method, string path, string what, int[]? ok = null, JsonNode? body = null, double timeout = 60)
    {
        var r = Call(method, path, body: body, timeout: timeout);
        if (!(ok ?? [200, 201, 204, 304]).Contains(r.Status))
            throw Out.Fail($"{what}: HTTP {r.Status} {Py.Slice(r.Shown, 0, 300)}");
        return r;
    }

    public virtual List<JsonObject> Stream(string method, string path, byte[] raw, string ctype, double timeout = 1800) =>
        throw new NotSupportedException("no stream here");

    public virtual JsonObject? Inspect(string kind, string name)
    {
        var r = Call("GET", $"/{kind}/{name}/json");   // unquoted on purpose: some proxies refuse an encoded slash
        return r.Status == 200 ? r.Json as JsonObject : null;
    }

    public virtual string ExecIn(string container, IReadOnlyList<string> argv, double timeout = 60)
    {
        var body = new JsonObject { ["AttachStdout"] = true, ["AttachStderr"] = true, ["Cmd"] = new JsonArray(argv.Select(a => (JsonNode?)a).ToArray()) };
        var created = Call("POST", $"/containers/{container}/exec", body);
        if (created.Status is not (200 or 201))
            return $"(exec create {created.Status})";
        var id = Json.Str(created.Json, "Id") ?? "";
        var started = Call("POST", $"/exec/{id}/start", new JsonObject { ["Detach"] = false, ["Tty"] = false }, timeout: timeout);
        if (started.Bytes is null)
            return $"(exec start {started.Status})";
        return Encoding.UTF8.GetString(Demux(started.Bytes, strict: false));
    }

    /// <summary>A container's output without a TTY: frames of an 8-byte header (stream, 0, 0, 0, length) and their bytes. strict (probe.py's):
    /// only frames whose header says stdout or stderr; else (os2.py's) any 8-byte header. Plain bytes as they are when no frame was read.</summary>
    public static byte[] Demux(byte[] raw, bool strict)
    {
        var output = new MemoryStream();
        var i = 0;
        var any = false;
        while (i + 8 <= raw.Length && (!strict || (raw[i] is 1 or 2 && raw[i + 1] == 0 && raw[i + 2] == 0 && raw[i + 3] == 0)))
        {
            var n = (raw[i + 4] << 24) | (raw[i + 5] << 16) | (raw[i + 6] << 8) | raw[i + 7];
            var take = Math.Min(n, Math.Max(0, raw.Length - (i + 8)));
            output.Write(raw, i + 8, take);
            any = true;
            i += 8 + n;
        }
        return any ? output.ToArray() : raw;
    }
}

/// <summary>A Docker Engine API base URL - the daemon's own, or a proxy that forwards it - plus any headers the proxy wants
/// (docker.api, docker.headers, docker.tls_verify in the private config).</summary>
sealed class Docker : DockerBase
{
    readonly string baseUrl;
    readonly HttpClient http;
    readonly Dictionary<string, string> headers = new(StringComparer.Ordinal);

    public override IReadOnlyDictionary<string, string> Headers => headers;
    public override bool TlsVerify { get; }

    public Docker(IConfig cfg)
    {
        baseUrl = cfg.Str("docker.api", required: true).TrimEnd('/');
        foreach (var (k, v) in cfg.Table("docker.headers"))
        {
            var value = ConfigExtensions.Resolve(v);
            if (value.Length == 0)
                throw Out.Fail($"docker.headers.{k} is empty (its environment variable is not set?)", 2);
            headers[k] = value;
        }
        TlsVerify = cfg.Bool("docker.tls_verify", true);
        http = Client(TlsVerify);
    }

    /// <summary>An HTTP client for the API and the stack manager: no certificate check when tls_verify is false, no time limit of its own.</summary>
    public static HttpClient Client(bool tlsVerify)
    {
        var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (!tlsVerify)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    HttpRequestMessage Request(string method, string path, byte[]? data, string ctype)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), new Uri(baseUrl + path));
        foreach (var (k, v) in headers)
            req.Headers.TryAddWithoutValidation(k, v);
        if (data is not null || method is "POST" or "PUT")
        {
            req.Content = new ByteArrayContent(data ?? []);
            req.Content.Headers.TryAddWithoutValidation("Content-Type", ctype);
        }
        return req;
    }

    public override DockerReply Call(string method, string path, JsonNode? body = null, byte[]? raw = null, string ctype = "application/json", double timeout = 60)
    {
        var data = raw ?? (body is not null ? Encoding.UTF8.GetBytes(Json.Dumps(body)) : null);
        using var req = Request(method, path, data, ctype);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var resp = http.Send(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        var bytes = resp.Content.ReadAsByteArrayAsync(cts.Token).GetAwaiter().GetResult();
        var status = (int)resp.StatusCode;
        if (status >= 400)
            return new DockerReply(status, Error: Encoding.UTF8.GetString(bytes));
        if (bytes.Length > 0 && (bytes[0] == (byte)'{' || bytes[0] == (byte)'['))
            return new DockerReply(status, Json: Json.Parse(bytes));
        return new DockerReply(status, Bytes: bytes);
    }

    public override List<JsonObject> Stream(string method, string path, byte[] raw, string ctype, double timeout = 1800)
    {
        using var req = Request(method, path, raw, ctype);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var resp = http.Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if ((int)resp.StatusCode >= 400)
            throw new HttpRequestException($"HTTP Error {(int)resp.StatusCode}: {resp.ReasonPhrase}");
        var lines = new List<JsonObject>();
        using var reader = new StreamReader(resp.Content.ReadAsStream(cts.Token), new UTF8Encoding(false));
        while (reader.ReadLine() is { } line)
        {
            try
            {
                if (Json.Parse(line) is JsonObject o)
                    lines.Add(o);
            }
            catch (JsonSyntaxError)
            {
            }
        }
        return lines;
    }
}

/// <summary>A container's inspect, read the ways the scripts read it.</summary>
static class InspectOf
{
    /// <summary>A variable of a container's environment; "" when it has none (os2.env_of).</summary>
    public static string EnvOf(JsonNode? container, string key) => EnvOrNull(container, key) ?? "";

    /// <summary>A variable of a container's environment; null when it has none (check.py's env_of).</summary>
    public static string? EnvOrNull(JsonNode? container, string key)
    {
        foreach (var e in Json.Arr(container, "Config", "Env"))
            if (Json.AsStr(e) is { } s && s.StartsWith(key + "=", StringComparison.Ordinal))
                return s[(key.Length + 1)..];
        return null;
    }

    public static JsonObject Labels(JsonNode? container) => Json.Obj(container, "Config", "Labels");

    static readonly System.Text.RegularExpressions.Regex LogUrl = new(@"^https?://[^/\s:]+(?::([0-9]+))?/log/([A-Za-z0-9]+)$");

    /// <summary>(port, secret) of the game container's log push address, CS2_LOG_HTTP_URL, &lt;bridge.url&gt;/log/&lt;bridge.secret&gt; (the image's
    /// templating may write "\/" for "/"); ("", "") when it says neither.</summary>
    static (string Port, string Secret) LogPush(JsonNode? game)
    {
        if (game is null)
            return ("", "");
        var m = LogUrl.Match(Py.Strip(EnvOf(game, "CS2_LOG_HTTP_URL").Replace("\\/", "/", StringComparison.Ordinal)));
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : ("", "");
    }

    /// <summary>The bridge's secret: from the game container's log push address, else the bridge's own LOG_SECRET. Never printed.</summary>
    public static string BridgeSecret(JsonNode? game, JsonNode? bridge = null) =>
        LogPush(game).Secret is { Length: > 0 } s ? s : bridge is not null ? EnvOf(bridge, "LOG_SECRET") : "";

    /// <summary>The port the bridge listens on: the log push address's, else the bridge's HTTP_PORT, else 8080.</summary>
    public static string BridgePort(JsonNode? game, JsonNode? bridge = null)
    {
        var p = LogPush(game).Port;
        if (p.Length > 0)
            return p;
        var h = bridge is not null ? EnvOf(bridge, "HTTP_PORT") : "";
        return h.Length > 0 ? h : "8080";
    }

    /// <summary>The RCON password: the game container's CS2_RCONPW, else the bridge's RCON_PASSWORD. Never printed.</summary>
    public static string RconPassword(JsonNode? game, JsonNode? bridge = null)
    {
        var g = game is not null ? EnvOf(game, "CS2_RCONPW") : "";
        return g.Length > 0 ? g : bridge is not null ? EnvOf(bridge, "RCON_PASSWORD") : "";
    }
}
