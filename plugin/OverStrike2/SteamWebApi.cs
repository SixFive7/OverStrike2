namespace OverStrike2;

/// <summary>The plugin's one door to the Steam Web API (api.steampowered.com, GetPublishedFileDetails): one request at a time, and the next
/// no sooner than Gap (at least 2 s) after the last one ended - the rule for every Web API caller of the project. The HUD item's check every minute and a
/// map card's details both go through it, whichever comes first, so neither can crowd the other. Every request says who asks (UserAgent).
/// No CounterStrikeSharp type in it: the self-test runs it as it is.</summary>
public static class SteamWebApi
{
    /// <summary>The least gap there is: the project's rule for every Web API caller.</summary>
    public const double MinGapSeconds = 2;

    /// <summary>The time from the end of one request to the start of the next: steam.apiGapSeconds (PluginSetup), set at load; never under
    /// <see cref="MinGapSeconds"/>, whatever it is set to.</summary>
    public static TimeSpan Gap
    {
        get => gap;
        set => gap = value < TimeSpan.FromSeconds(MinGapSeconds) ? TimeSpan.FromSeconds(MinGapSeconds) : value;
    }
    static TimeSpan gap = TimeSpan.FromSeconds(MinGapSeconds);

    static readonly SemaphoreSlim One = new(1, 1);
    static DateTime lastEnded = DateTime.MinValue;

    /// <summary>Who asks, on every request to Steam - the Web API through this door, and the HUD item's page visit (ItemWatch): the project and
    /// its version, honestly; the plugin sets it at load (AgentOf).</summary>
    public static string UserAgent { get; set; } = AgentOf("unknown");

    public static string AgentOf(string version) => $"OverStrike2/{version} (the Over Strike 2 CS2 server plugin; plugin/OverStrike2)";

    /// <summary>POST to the Web API through the door. The answer is read in full before the door opens for the next (HttpClient's default
    /// completion), so the request really is over.</summary>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient http, string url, HttpContent content)
    {
        await One.WaitAsync().ConfigureAwait(false);
        try
        {
            var wait = lastEnded + Gap - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.ParseAdd(UserAgent);
            return await http.SendAsync(request).ConfigureAwait(false);
        }
        finally
        {
            lastEnded = DateTime.UtcNow;
            One.Release();
        }
    }
}
