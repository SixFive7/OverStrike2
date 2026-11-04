namespace OverStrike2;

/// <summary>The plugin's one door to the Steam Web API (api.steampowered.com, GetPublishedFileDetails): one request at a time, and the next
/// no sooner than Gap after the last one ended - the owner's rule for every Web API caller (2026-11-04). The daily Workshop check's batches,
/// the HUD item's check every minute and a map card's details all go through it, whichever comes first, so none of them can crowd another.
/// No CounterStrikeSharp type in it: the self-test runs it as it is.</summary>
public static class SteamWebApi
{
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(2);

    static readonly SemaphoreSlim One = new(1, 1);
    static DateTime lastEnded = DateTime.MinValue;

    /// <summary>POST to the Web API through the door. The answer is read in full before the door opens for the next (HttpClient's default
    /// completion), so the request really is over.</summary>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient http, string url, HttpContent content)
    {
        await One.WaitAsync().ConfigureAwait(false);
        try
        {
            var wait = lastEnded + Gap - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
            return await http.PostAsync(url, content).ConfigureAwait(false);
        }
        finally
        {
            lastEnded = DateTime.UtcNow;
            One.Release();
        }
    }
}
