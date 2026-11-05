using System.Text;

namespace OverStrike2;

/// <summary>The HUD's line to the bridge, over the stack network the log push already uses: GET /hud/&lt;secret&gt;?draw=1 (or draw=0, Hud.DrawQuery) for the
/// live state (once a second while anyone is on the server), GET /catalog/, /search/ and /dash/ when their versions move, POST /act/&lt;secret&gt; for a click. The address
/// and the secret come from the cs2 service's own CS2_LOG_HTTP_URL (http://bridge:8080/log/&lt;secret&gt;), so there is nothing new
/// to configure.
///
/// Every request runs on the thread pool and only ever hands back a string; the game thread picks it up in Take, so nothing here
/// touches the engine off its thread.</summary>
sealed class BridgeLink
{
    public const double PollSeconds = 1;

    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
    readonly string stateUrl, actUrl, catalogUrl, searchUrl, dashUrl;
    readonly List<(Task<string?> Task, DateTime Sent)> acts = new();
    Task<string?>? fetch, catalogFetch, searchFetch, dashFetch;
    DateTime nextFetch, nextCatalogTry, nextSearchTry, nextDashTry, fetchSent;
    long catalogHave, catalogAsked, searchHave, searchAsked, dashHave, dashAsked;

    public int Failures { get; private set; }          // in a row; reset by any answer
    public string LastError { get; private set; } = "";
    public DateTime LastAnswer { get; private set; }

    /// <summary>When the newest state answer taken was asked for (its request went out then): the state is the bridge's word as of then, at
    /// the latest - an answer asked for before a map change is the old map's, however late the game thread takes it. MinValue: none yet.</summary>
    public DateTime StateAsOf { get; private set; }

    BridgeLink(string stateUrl, string actUrl, string catalogUrl, string searchUrl, string dashUrl) { this.stateUrl = stateUrl; this.actUrl = actUrl; this.catalogUrl = catalogUrl; this.searchUrl = searchUrl; this.dashUrl = dashUrl; }

    /// <summary>The link built from CS2_LOG_HTTP_URL, or null with the reason when that is missing or has no /log/ path.</summary>
    public static BridgeLink? FromEnvironment(out string where)
    {
        var log = (Environment.GetEnvironmentVariable("CS2_LOG_HTTP_URL") ?? "").Replace("\\/", "/").Trim();
        var at = log.LastIndexOf("/log/", StringComparison.Ordinal);
        if (at < 0 || at + 5 >= log.Length) { where = "CS2_LOG_HTTP_URL has no /log/<secret> to derive the bridge from"; return null; }
        var (root, secret) = (log[..at], log[(at + 5)..]);
        where = root;
        return new BridgeLink(root + Hud.StatePath + secret, root + Hud.ActPath + secret, root + Hud.CatalogPath + secret, root + Hud.SearchPath + secret,
            root + Hud.DashPath + secret);
    }

    /// <summary>On the game thread, every frame of the plugin's timer: hands back the newest state text that arrived since the last
    /// call (from a poll or from an action's answer), and starts the next poll when one is due and wanted. draws: whether this plugin draws the HUD,
    /// which the poll tells the bridge - only a drawing plugin makes it open votes nobody else would see.</summary>
    public string? Take(DateTime now, bool wanted, bool draws = true)
    {
        string? newest = null;
        if (fetch is { IsCompleted: true } done)
        {
            if (Result(done) is { } polled) { newest = polled; StateAsOf = Max(StateAsOf, fetchSent); }
            fetch = null;
        }
        foreach (var act in acts.Where(a => a.Task.IsCompleted).ToList())
        {
            if (Result(act.Task) is { } answered) { newest = answered; StateAsOf = Max(StateAsOf, act.Sent); }
            acts.Remove(act);
        }
        if (wanted && fetch == null && now >= nextFetch)
        {
            nextFetch = now.AddSeconds(PollSeconds);
            fetchSent = DateTime.UtcNow;
            fetch = GetText($"{stateUrl}?{Hud.DrawQuery}={(draws ? 1 : 0)}");
        }
        return newest;
    }

    /// <summary>On the game thread, every frame: the map catalog's text once a fetch for a newer version than the one we have has
    /// come back; starts that fetch when the live state names a version we do not have (every 5 s at most after a failure).</summary>
    public string? TakeCatalog(DateTime now, long wanted)
    {
        string? text = null;
        if (catalogFetch is { IsCompleted: true } done)
        {
            text = Result(done);
            catalogFetch = null;
            if (text != null) catalogHave = catalogAsked; else nextCatalogTry = now.AddSeconds(5);
        }
        if (wanted > 0 && wanted != catalogHave && catalogFetch == null && now >= nextCatalogTry)
        {
            catalogAsked = wanted;
            catalogFetch = GetText(catalogUrl);
        }
        return text;
    }

    /// <summary>On the game thread, every frame: the search index's text (HudSearch) once a fetch for a newer version has come back;
    /// starts that fetch when the live state names a version we do not have (every 10 s at most after a failure). About a megabyte,
    /// so only when it changed - the bridge moves the version when search.json does.</summary>
    public string? TakeSearch(DateTime now, long wanted)
    {
        string? text = null;
        if (searchFetch is { IsCompleted: true } done)
        {
            text = Result(done);
            searchFetch = null;
            if (text != null) searchHave = searchAsked; else nextSearchTry = now.AddSeconds(10);
        }
        if (wanted > 0 && wanted != searchHave && searchFetch == null && now >= nextSearchTry)
        {
            searchAsked = wanted;
            searchFetch = GetText(searchUrl);
        }
        return text;
    }

    /// <summary>On the game thread, every frame: the Sync page's block (HudDash) once a fetch for a newer version has come back; starts that fetch
    /// when the live state names a version we do not have (every 5 s at most after a failure). A few tens of kilobytes, when the sync's news moved.</summary>
    public string? TakeDash(DateTime now, long wanted)
    {
        string? text = null;
        if (dashFetch is { IsCompleted: true } done)
        {
            text = Result(done);
            dashFetch = null;
            if (text != null) dashHave = dashAsked; else nextDashTry = now.AddSeconds(5);
        }
        if (wanted > 0 && wanted != dashHave && dashFetch == null && now >= nextDashTry)
        {
            dashAsked = wanted;
            dashFetch = GetText(dashUrl);
        }
        return text;
    }

    /// <summary>A click: the action it asks the bridge for (written like the chat command it replaced). The bridge answers with the state
    /// after it, so the vote or the note shows on the HUD at once.</summary>
    public void Act(long account, string name, string command)
    {
        if (acts.Count >= 8) return; // a stuck bridge must not pile up requests
        acts.Add((Post($"[U:1:{account}]\t{Hud.Clean(name, 64)}\t{command}"), DateTime.UtcNow));
    }

    static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    string? Result(Task<string?> t)
    {
        if (t.IsCompletedSuccessfully && t.Result is { } text) { Failures = 0; LastAnswer = DateTime.UtcNow; return text; }
        Failures++;
        LastError = t.Exception?.GetBaseException().Message ?? "no answer";
        return null;
    }

    async Task<string?> GetText(string url)
    {
        using var response = await http.GetAsync(url).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync().ConfigureAwait(false) : throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
    }

    async Task<string?> Post(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "text/plain");
        using var response = await http.PostAsync(actUrl, content).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync().ConfigureAwait(false) : throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
    }
}
