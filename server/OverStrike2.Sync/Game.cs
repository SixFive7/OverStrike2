using System.Text;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>The game server and the bridge, as the sync sees them from its own container.</summary>
public static class Game
{
    /// <summary>Humans on the game server by A2S, asked twice 2 s apart when the first answer is not 0. Null: no answer. No host: 0.</summary>
    public static int? HumansOn(SyncConfig cfg, SyncClock clock)
    {
        if (cfg.GameHost.Length == 0)
            return 0;
        var first = A2s.Ask(cfg.GameHost, cfg.GamePort, TimeSpan.FromSeconds(2));
        if (first is not null && first.Humans == 0)
            return 0;
        clock.Sleep(2);
        var second = A2s.Ask(cfg.GameHost, cfg.GamePort, TimeSpan.FromSeconds(2));
        if (second is null)
            return first?.Humans;
        return second.Humans;
    }

    static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>POST a text to the bridge; (status, the answer). Throws HttpRequestException or a timeout when there is none.</summary>
    public static (int Status, string Text) Post(string url, string body, string contentType)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        req.Headers.TryAddWithoutValidation("User-Agent", SteamApi.UserAgent);
        using var resp = Http.Send(req);
        var text = new StreamReader(resp.Content.ReadAsStream(), Encoding.UTF8).ReadToEnd();
        return ((int)resp.StatusCode, text);
    }
}
