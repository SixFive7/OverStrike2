using System.Net;
using System.Text;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>The bridge's HTTP side, as the watcher uses it: POST a text body to /&lt;what&gt;/&lt;secret&gt;. ("ok", reply) when the bridge
/// took it (its answer starts with "ok"), ("failed", why) when not, ("none", null) when no bridge is configured.</summary>
public interface IBridge
{
    string? Address { get; }
    (string Told, string? Reply) Post(string what, string body, double timeoutSeconds);
}

/// <summary>The bridge over HTTP.</summary>
public sealed class HttpBridge((string Base, string Secret)? endpoint) : IBridge
{
    public static readonly string UserAgent = "os2-server/" + (typeof(HttpBridge).Assembly.GetName().Version?.ToString(3) ?? "1");
    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });

    public string? Address => endpoint?.Base;

    public (string Told, string? Reply) Post(string what, string body, double timeoutSeconds)
    {
        if (endpoint is not { } e)
            return ("none", null);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BridgeWire.Url(e.Base, what, e.Secret))
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain"),
            };
            req.Headers.UserAgent.ParseAdd(UserAgent);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var resp = Http.Send(req, cts.Token);
            var reply = new StreamReader(resp.Content.ReadAsStream(cts.Token), Encoding.UTF8).ReadToEnd().Trim();
            return reply.StartsWith("ok", StringComparison.Ordinal) ? ("ok", reply) : ("failed", $"the bridge answered '{Words.ChatSafe(reply, 120)}' (HTTP {(int)resp.StatusCode})");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException or WebException)
        {
            return ("failed", $"{ex.GetType().Name}: {ex.Message.Replace(e.Secret, "<secret>")}");
        }
    }
}
