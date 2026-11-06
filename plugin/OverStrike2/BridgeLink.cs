using System.Text;

namespace OverStrike2;

/// <summary>The HUD's line to the bridge, over the stack network the log push already uses - the plugin's one place for it (the bridge's is
/// HudTransport.cs). The live state is a long poll, GET /hud/&lt;secret&gt;?v=&lt;version&gt;&amp;draw=1 (or draw=0, Hud.DrawQuery): the bridge answers when its
/// state changes, or after Hud.LongPollSeconds that nothing did; every answer starts with its version and the bridge's clock, and its countdowns
/// are end times the HUD counts down itself (HudState.AsOf). GET /catalog/, /search/ and /dash/ when their versions move, POST /act/&lt;secret&gt;?v=1
/// for a click, answered with the state after it in the same form. The address and the secret are the settings file's bridge.url and
/// bridge.secret, or without the file the cs2 service's own CS2_LOG_HTTP_URL (http://bridge:8080/log/&lt;secret&gt;) - PluginSetup.cs.
///
/// Every request runs on the thread pool and only ever hands back a string; the game thread picks it up in Take, so nothing here
/// touches the engine off its thread.</summary>
sealed class BridgeLink
{
    public const double PollSeconds = 1; // a poll that got no answer in the long poll's form is sent again this much later

    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };                                // clicks, the catalog, the index, the Sync block
    readonly HttpClient poll = new() { Timeout = TimeSpan.FromSeconds(Hud.LongPollSeconds + 15) };          // the long poll: the bridge holds it up to LongPollSeconds
    long version = -1;   // the version of the newest state taken; -1 none yet
    bool? sentDraws;     // what the last poll told the bridge about drawing the HUD (Hud.DrawQuery); null: no poll yet

    /// <summary>The bridge's clock less ours, in milliseconds, from its newest answer: the HUD counts the state's end times against the bridge's clock.</summary>
    public long ClockOffsetMs { get; private set; }

    /// <summary>The time the bridge's clock says it is now, as the HUD counts its countdowns (unix milliseconds).</summary>
    public long BridgeNowMs(DateTime now) => HudState.Ms(now) + ClockOffsetMs;
    readonly string stateUrl, actUrl, catalogUrl, searchUrl, dashUrl;
    readonly List<(Task<string?> Task, DateTime Sent)> acts = new();
    Task<string?>? fetch, catalogFetch, searchFetch, dashFetch;
    DateTime nextFetch, nextCatalogTry, nextSearchTry, nextDashTry;
    long catalogHave, catalogAsked, searchHave, searchAsked, dashHave, dashAsked;

    public int Failures { get; private set; }          // in a row; reset by any answer
    public string LastError { get; private set; } = "";
    public DateTime LastAnswer { get; private set; }

    /// <summary>When the newest state answer taken was asked for (its request went out then): the state is the bridge's word as of then, at
    /// the latest - an answer asked for before a map change is the old map's, however late the game thread takes it. MinValue: none yet.</summary>
    public DateTime StateAsOf { get; private set; }

    BridgeLink(string stateUrl, string actUrl, string catalogUrl, string searchUrl, string dashUrl) { this.stateUrl = stateUrl; this.actUrl = actUrl; this.catalogUrl = catalogUrl; this.searchUrl = searchUrl; this.dashUrl = dashUrl; }

    /// <summary>The link to the bridge at root (http://host:port) with the secret in its paths - both from PluginSetup.</summary>
    public static BridgeLink At(string root, string secret) =>
        new(root + Hud.StatePath + secret, root + Hud.ActPath + secret, root + Hud.CatalogPath + secret, root + Hud.SearchPath + secret, root + Hud.DashPath + secret);

    /// <summary>On the game thread, every frame of the plugin's timer: hands back the newest state text that arrived since the last
    /// call (from a poll or from an action's answer), and starts the next poll when one is due and wanted. draws: whether this plugin draws the HUD,
    /// which the poll tells the bridge - only a drawing plugin makes it open votes nobody else would see.</summary>
    public string? Take(DateTime now, bool wanted, bool draws = true)
    {
        string? newest = null;
        if (fetch is { IsCompleted: true } done)
        {
            if (Result(done) is { } polled && Versioned(polled, out var text))
            {
                if (text != null) newest = text;
                StateAsOf = Max(StateAsOf, DateTime.UtcNow); // a long poll's answer: as of now
                nextFetch = now;
            }
            else nextFetch = now.AddSeconds(PollSeconds); // no answer, or none in the long poll's form: try again in a second
            fetch = null;
        }
        foreach (var act in acts.Where(a => a.Task.IsCompleted).ToList())
        {
            if (Result(act.Task) is { } answered && Versioned(answered, out var text))
            {
                if (text != null) newest = text;
                StateAsOf = Max(StateAsOf, act.Sent);
            }
            acts.Remove(act);
        }
        if (fetch != null && sentDraws != null && sentDraws != draws)
        {
            // A hold begins or ends: the bridge hears it now, not when the long poll in flight comes back (up to Hud.LongPollSeconds) - in between a match end
            // would open a vote nobody without the HUD sees. The short poll's answer is the state as of now, which the next long poll brings again anyway.
            _ = GetText($"{stateUrl}?{Hud.DrawQuery}={(draws ? 1 : 0)}", http).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            sentDraws = draws;
        }
        if (wanted && fetch == null && now >= nextFetch)
        {
            sentDraws = draws;
            fetch = GetText($"{stateUrl}?v={version}&{Hud.DrawQuery}={(draws ? 1 : 0)}", poll);
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

    /// <summary>An answer in the long poll's form: its version line read - the version kept, the clocks' difference noted - and the state it carries,
    /// or null when it says nothing changed or is older than the one already taken. False: no answer in that form (no state in it).</summary>
    bool Versioned(string answer, out string? state)
    {
        state = null;
        var lines = answer.Split('\n', 4);
        if (lines.Length < 2 || !lines[0].StartsWith(HudState.Magic, StringComparison.Ordinal) || lines[1].Split('\t') is not [Hud.VersionLine, var v, var clock]) return false;
        if (long.TryParse(clock, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ms))
            ClockOffsetMs = ms - HudState.Ms(DateTime.UtcNow);
        if (!long.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)) return true;
        if (lines.Length > 2 && lines[2].TrimEnd('\r') == Hud.Unchanged) { version = Math.Max(version, n); return true; }
        if (n < version) return true; // a slower answer that an older state rode on
        version = n;
        state = answer;
        return true;
    }

    string? Result(Task<string?> t)
    {
        if (t.IsCompletedSuccessfully && t.Result is { } text) { Failures = 0; LastAnswer = DateTime.UtcNow; return text; }
        Failures++;
        LastError = t.Exception?.GetBaseException().Message ?? "no answer";
        return null;
    }

    Task<string?> GetText(string url) => GetText(url, http);

    async Task<string?> GetText(string url, HttpClient client)
    {
        using var response = await client.GetAsync(url).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync().ConfigureAwait(false) : throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
    }

    async Task<string?> Post(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "text/plain");
        using var response = await http.PostAsync(actUrl + "?v=1", content).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync().ConfigureAwait(false) : throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
    }
}
