using System.Text;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The daily Workshop check (../../bridge/Workshop.cs): which maps nobody can download any more. The bridge stays off the internet by design; the game server reaches Steam anyway. So, once a day (and a few minutes after every start, which the
/// daily restart makes daily as well): ask the bridge which ids to check (GET /check/&lt;secret&gt;), ask the public Steam Web API about them in
/// batches of 100 (ISteamRemoteStorage/GetPublishedFileDetails, anonymous) - one request per 100 ids, about 70 a run at 7,000 ids, each at least
/// 2 s after the last ended (SteamWebApi) - and hand the verdicts back (POST /removed/&lt;secret&gt;), where
/// the bridge blocks what went and unblocks what came back. Everything runs on the thread pool and touches no engine object; a failed run
/// is tried again an hour later. css_gghud workshop runs it now and says how the last one went.</summary>
sealed class WorkshopWatch
{
    static readonly TimeSpan FirstAfter = TimeSpan.FromMinutes(3), Every = TimeSpan.FromHours(24), Retry = TimeSpan.FromHours(1);

    readonly ILogger log;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly string? checkUrl, reportUrl;
    Task? running;
    DateTime next = DateTime.UtcNow + FirstAfter;

    /// <summary>How the last run went, for css_gghud.</summary>
    public string Last { get; private set; } = "not run yet";

    public WorkshopWatch(ILogger log)
    {
        this.log = log;
        var push = (Environment.GetEnvironmentVariable("CS2_LOG_HTTP_URL") ?? "").Replace("\\/", "/").Trim();
        var at = push.LastIndexOf("/log/", StringComparison.Ordinal);
        if (at < 0 || at + 5 >= push.Length) { Last = "off: CS2_LOG_HTTP_URL has no /log/<secret> to find the bridge by"; return; }
        var (root, secret) = (push[..at], push[(at + 5)..]);
        (checkUrl, reportUrl) = (root + Workshop.CheckPath + secret, root + Workshop.ReportPath + secret);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GunGameWelcome-WorkshopCheck/1");
    }

    /// <summary>On the game thread, from a plugin timer: starts a run when one is due. Cheap; the run itself is on the thread pool.</summary>
    public void Poll(DateTime now)
    {
        if (running is { IsCompleted: true }) running = null;
        if (running != null || checkUrl == null || now < next) return;
        next = now + Every;
        running = Task.Run(Run);
    }

    /// <summary>css_gghud workshop: the next poll starts a run.</summary>
    public string RunSoon()
    {
        if (checkUrl == null) return Last;
        if (running != null) return "a Workshop check is running now";
        next = DateTime.UtcNow;
        return $"a Workshop check starts within a minute (last: {Last})";
    }

    async Task Run()
    {
        var started = DateTime.UtcNow;
        try
        {
            var ids = Workshop.DecodeIds(await http.GetStringAsync(checkUrl).ConfigureAwait(false));
            if (ids.Count == 0) { Last = $"{started:yyyy-MM-dd HH:mm}Z: the bridge named no ids"; log.LogWarning("GunGameWelcome Workshop check: {Last}", Last); return; }
            var verdicts = new List<(long Id, string Verdict)>();
            foreach (var batch in ids.Chunk(Workshop.Batch))
            {
                var form = new List<KeyValuePair<string, string>> { new("itemcount", batch.Length.ToString()) };
                for (var i = 0; i < batch.Length; i++) form.Add(new($"publishedfileids[{i}]", batch[i].ToString()));
                using var response = await SteamWebApi.PostAsync(http, Workshop.DetailsUrl, new FormUrlEncodedContent(form)).ConfigureAwait(false); // one at a time, 2 s apart
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Steam Web API answered HTTP {(int)response.StatusCode}");
                verdicts.AddRange(Workshop.ParseDetails(await response.Content.ReadAsStringAsync().ConfigureAwait(false)));
            }
            using var content = new StringContent(Workshop.EncodeReport(verdicts), Encoding.UTF8, "text/plain");
            using var answer = await http.PostAsync(reportUrl, content).ConfigureAwait(false);
            var said = await answer.Content.ReadAsStringAsync().ConfigureAwait(false);
            var gone = verdicts.Where(v => v.Verdict != Workshop.Ok).Select(v => $"{v.Id} {v.Verdict}").ToList();
            Last = $"{started:yyyy-MM-dd HH:mm}Z: {ids.Count} id(s), {verdicts.Count} answered, {gone.Count} not downloadable" +
                   $"{(gone.Count > 0 ? $" ({string.Join(", ", gone.Take(10))}{(gone.Count > 10 ? ", ..." : "")})" : "")}; the bridge: {said}";
            log.LogInformation("GunGameWelcome Workshop check: {Last}", Last);
        }
        catch (Exception e)
        {
            next = DateTime.UtcNow + Retry;
            Last = $"{started:yyyy-MM-dd HH:mm}Z: failed ({e.GetBaseException().Message}) - again at {next:HH:mm}Z";
            log.LogWarning("GunGameWelcome Workshop check: {Last}", Last);
        }
    }
}
