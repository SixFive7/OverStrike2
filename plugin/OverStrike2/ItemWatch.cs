using System.Globalization;

namespace OverStrike2;

/// <summary>Whether Steam shows the HUD's Workshop item to everybody. A new item, and every new version of one, waits for Valve's
/// moderation, and meanwhile nobody but the owner can download it (other players' games show ERROR 15) - so the HUD check must not
/// disconnect anybody then, and joiners must not be made to download it (HudScreen.RequireAddon).
///
/// Since 2026-11-01 the Web API is the signal: GetPublishedFileDetails on api.steampowered.com every minute, anonymous. The item's page, the one
/// place Valve's review shows, is visited only when ItemPlan (HudCheck.cs) says so - right after the Web API shows a change, then at most
/// every 15 minutes while it is not shown, for 3 hours - and never with HudCheck.PageVariable off, as on a test copy. The page is read as HudCheck.PagePublic says (Steam's old Error page and its new
/// HTTP 500 page both mean not shown). Everything is fetched on the thread pool and only ever hands back a verdict. The game server reaches Steam already.</summary>
sealed class ItemWatch
{
    const int PageBytesMax = 400_000; // the new 500 page is ~172 KB of client-side app; the title near its top is all that is read

    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly string? url;
    readonly long id;
    readonly ItemPlan plan = new();
    readonly bool pageOff = HudCheck.PageOff(Environment.GetEnvironmentVariable(HudCheck.PageVariable));
    Task<(int Status, string? Html)>? pending;
    Task<HudCheck.ItemApi?>? apiPending;
    DateTime apiNext;
    DateTime lastVisit = DateTime.MinValue, lastApi = DateTime.MinValue;
    string note = "no page visit (none needed)", apiNote = "not asked yet";

    public ItemWatch(string addon)
    {
        url = addon.Length is > 0 and <= 12 && addon.All(char.IsAsciiDigit) ? "https://" + HudCheck.ItemUrl(addon) : null;
        id = url != null ? long.Parse(addon, CultureInfo.InvariantCulture) : 0;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        if (url == null) note = "no item id";
        else if (pageOff) note = $"never visited here ({HudCheck.PageVariable} is off)";
    }

    /// <summary>On the game thread, every frame: takes a finished page visit's verdict and a finished Web API answer, asks the Web API
    /// every minute, and visits the page only when ItemPlan says one is due.</summary>
    public void Poll(DateTime now, bool wanted)
    {
        if (pending is { IsCompleted: true } done)
        {
            pending = null;
            var (status, html) = done.IsCompletedSuccessfully ? done.Result : (0, null);
            var verdict = HudCheck.PagePublic(status, html);
            plan.PageAnswer(verdict, now);
            note = verdict switch
            {
                true => "shown to everybody",
                false => $"NOT shown to everybody (Steam's {(status == 500 ? "HTTP 500 Workshop page" : "Error page")}{(plan.Cause == HudCheck.Cause.Review ? ": the new version under review" : "")})",
                _ => $"no verdict ({(done.IsCompletedSuccessfully ? $"HTTP {status}" : done.Exception?.GetBaseException().Message ?? "failed")})",
            };
        }
        if (apiPending is { IsCompleted: true } asked)
        {
            apiPending = null;
            var fresh = asked.IsCompletedSuccessfully ? asked.Result : null;
            plan.ApiAnswer(fresh, now);
            lastApi = now;
            apiNote = fresh is { } f ? $"{f.Verdict}, updated {Local(f.TimeUpdated)}" : $"no answer ({asked.Exception?.GetBaseException().Message ?? "nothing about the item"})";
        }
        if (url == null || !wanted) return;
        if (apiPending == null && now >= apiNext)
        {
            apiNext = now + ItemPlan.ApiEvery;
            apiPending = AskApi(id);
        }
        if (pending == null && !pageOff && plan.VisitDue(now))
        {
            plan.VisitStarted();
            lastVisit = now;
            pending = Fetch(url);
        }
    }

    /// <summary>Steam shows the item to everybody, as far as the plugin knows (ItemPlan.Public): the HUD check's interlock.</summary>
    public bool Public(DateTime now) => plan.Public(now);

    /// <summary>Whether joiners must download it now (ItemPlan.Required), given what they must now.</summary>
    public bool Required(bool current, DateTime now) => plan.Required(current, now);

    /// <summary>Why not, for the words players get.</summary>
    public HudCheck.Cause Cause => plan.Cause;

    /// <summary>When the item last changed, local time ("01:23"), if the Web API has said.</summary>
    public string? Updated => plan.Api is { TimeUpdated: > 0 } a ? Local(a.TimeUpdated) : null;

    /// <summary>css_gghud item: one page visit now, by hand.</summary>
    public void Soon() => plan.VisitNow(DateTime.UtcNow);

    public string Describe(DateTime now) =>
        $"Web API: {apiNote}{(lastApi == DateTime.MinValue ? "" : $", {(now - lastApi).TotalSeconds:0} s ago")}; page: {note}" +
        (lastVisit == DateTime.MinValue ? "" : $", {(now - lastVisit).TotalMinutes:0} min ago") +
        $"; {plan.Visits} page visit(s) since the plugin loaded" +
        (plan.ChangedAt == DateTime.MinValue ? ", no change seen" : $", change at {plan.ChangedAt.ToLocalTime():HH:mm} ({plan.VisitsSinceChange} visit(s) since; " +
            (now < plan.WindowUntil && plan.NextVisit != DateTime.MaxValue ? $"next at {plan.NextVisit.ToLocalTime():HH:mm})" : "no more planned)"));

    /// <summary>css_gghud page &lt;id&gt;: one visit to any item's page and one Web API question, with what this watch would make of them -
    /// changes nothing. How the page check was tried on the live server against items Steam hides (2026-11-01). By hand only.</summary>
    public async Task<string> Probe(long other)
    {
        if (pageOff) return $"{other}: no visit - {HudCheck.PageVariable} is off here";
        var (status, html) = await Fetch("https://" + HudCheck.ItemUrl(other.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
        var title = html == null ? "-" : System.Text.RegularExpressions.Regex.Match(html, "<title>([^<]*)</title>").Groups[1].Value;
        var verdict = HudCheck.PagePublic(status, html);
        var a = await AskApi(other).ConfigureAwait(false);
        return $"{other}: page HTTP {status}, title '{title}' -> {(verdict == true ? "shown to everybody" : verdict == false ? "NOT shown" : "no verdict")}; " +
               $"Web API {(a is { } x ? $"{x.Verdict}, updated {Local(x.TimeUpdated)}" : "no answer")}";
    }

    static string Local(long unix) => unix <= 0 ? "never" : DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    async Task<(int, string?)> Fetch(string address)
    {
        using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode && status != 500) return (status, null); // 429 and the like: nothing worth reading
        // Read the page's head only: the title is all PagePublic looks at, and Steam's new 500 page is a 172 KB client-side app.
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var buffer = new byte[PageBytesMax];
        var total = 0;
        int got;
        while (total < buffer.Length && (got = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total)).ConfigureAwait(false)) > 0) total += got;
        return (status, System.Text.Encoding.UTF8.GetString(buffer, 0, total));
    }

    async Task<HudCheck.ItemApi?> AskApi(long item)
    {
        using var form = new FormUrlEncodedContent([new("itemcount", "1"), new("publishedfileids[0]", item.ToString(CultureInfo.InvariantCulture))]);
        using var response = await http.PostAsync(Workshop.DetailsUrl, form).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return HudCheck.ParseItem(await response.Content.ReadAsStringAsync().ConfigureAwait(false), item);
    }
}
