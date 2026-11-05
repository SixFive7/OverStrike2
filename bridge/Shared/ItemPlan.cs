// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.

/// <summary>When the plugin looks at the HUD item, and what it makes of it - free of CounterStrikeSharp and of the network, so the bridge's
/// --selftest drives it on a handed clock; the plugin's ItemWatch does the fetching. Since 2026-11-01 the Web API (GetPublishedFileDetails, api.steampowered.com) is the signal, read every minute: its
/// verdict says gone, private or banned, and a change in time_updated or hcontent_file says a new version went up. The item's page on
/// the community site - the only thing that shows Valve's review - is visited only right after such a change, then at most every 15
/// minutes while it is not shown (nothing may put the account that runs the server at risk), for ReviewWindow after the
/// change, and at a plugin start only if the Web API's time_updated is that recent (an upload may be under review). A visit the site
/// refuses (429: no verdict) leaves it alone for an hour, and until a verdict comes joiners need not download the item for ReviewAssumed after
/// the upload: reviews have taken 9 to 63 minutes, and a new player without the menu is better off than one held at a download that cannot
/// succeed. Never otherwise: no idle polling. Until then (GunGameWelcome 3.1.3 and before) the page was visited every 2 minutes, all day.</summary>
public sealed class ItemPlan
{
    public static readonly TimeSpan ApiEvery = TimeSpan.FromMinutes(1), VisitEvery = TimeSpan.FromMinutes(15), RefusedWait = TimeSpan.FromHours(1), ApiFresh = TimeSpan.FromMinutes(10),
        ReviewAssumed = TimeSpan.FromMinutes(63);

    DateTime apiAt = DateTime.MinValue;

    /// <summary>The Web API's last answer about the item; null before the first.</summary>
    public HudCheck.ItemApi? Api { get; private set; }

    /// <summary>When the Web API last showed a change (or, at a start, the recent upload it showed); MinValue: none since the start.</summary>
    public DateTime ChangedAt { get; private set; } = DateTime.MinValue;

    /// <summary>Page visits are allowed until then (ReviewWindow after the change); MinValue: none.</summary>
    public DateTime WindowUntil { get; private set; } = DateTime.MinValue;

    /// <summary>The next page visit; MaxValue: none planned.</summary>
    public DateTime NextVisit { get; private set; } = DateTime.MaxValue;

    /// <summary>The latest readable page verdict since the Web API's last change: true shown, false not shown, null none yet.</summary>
    public bool? SinceChange { get; private set; }

    /// <summary>Page visits since the plugin loaded, and since the last change - a count kept small on purpose.</summary>
    public int Visits { get; private set; }
    public int VisitsSinceChange { get; private set; }

    /// <summary>A visit since the last change got no verdict (a 429, an error): what Valve is doing is not known.</summary>
    public bool Refused { get; private set; }

    static DateTime FromUnix(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

    /// <summary>A Web API answer (null: none - the last one is kept). A change in time_updated, hcontent_file or the verdict opens a
    /// window and makes a visit due now; the first answer after a start does so only for an upload within ReviewWindow.</summary>
    public void ApiAnswer(HudCheck.ItemApi? fresh, DateTime now)
    {
        if (fresh is not { } f) return;
        var first = Api == null;
        var changed = !first && (Api!.Value.TimeUpdated != f.TimeUpdated || Api.Value.Content != f.Content || Api.Value.Verdict != f.Verdict);
        Api = f;
        apiAt = now;
        if (changed) { Open(now, now); return; }
        if (!first || f.Verdict != Workshop.Ok || f.TimeUpdated <= 0) return;
        var uploaded = FromUnix(f.TimeUpdated);
        var age = now - uploaded;
        if (age > TimeSpan.FromMinutes(-5) && age <= HudCheck.ReviewWindow) Open(uploaded < now ? uploaded : now, now); // -5 min: two clocks
    }

    void Open(DateTime from, DateTime now)
    {
        ChangedAt = from;
        WindowUntil = from + HudCheck.ReviewWindow;
        NextVisit = now;
        SinceChange = null;
        VisitsSinceChange = 0;
        Refused = false;
    }

    /// <summary>A page visit should start now: one is planned, it is due, and the window is open.</summary>
    public bool VisitDue(DateTime now) => now >= NextVisit && now < WindowUntil;

    /// <summary>A visit went out: none more until its answer.</summary>
    public void VisitStarted()
    {
        NextVisit = DateTime.MaxValue;
        Visits++;
        VisitsSinceChange++;
    }

    /// <summary>The visit's verdict (HudCheck.PagePublic): shown ends the visits; not shown plans the next in VisitEvery; unreadable (429, an error) in RefusedWait.</summary>
    public void PageAnswer(bool? verdict, DateTime now)
    {
        if (verdict != null) SinceChange = verdict; else Refused = true;
        NextVisit = verdict == true ? DateTime.MaxValue : now + (verdict == null ? RefusedWait : VisitEvery);
    }

    /// <summary>css_gghud item: one visit now, whatever the Web API says (a window just long enough for it).</summary>
    public void VisitNow(DateTime now)
    {
        NextVisit = now;
        if (WindowUntil < now + VisitEvery) WindowUntil = now + VisitEvery;
    }

    /// <summary>The Web API answered within ApiFresh.</summary>
    public bool Fresh(DateTime now) => Api != null && now - apiAt <= ApiFresh;

    /// <summary>Whether joiners must download the item: not while the Web API calls it gone, private or banned, nor once a page visit
    /// since the last change found Steam's not-shown page (the first one is enough: the change explains it), nor while a refused visit has
    /// left it unknown, up to ReviewAssumed after the upload; again once a visit shows it. Without a fresh Web API answer nothing changes
    /// (current): no word from Steam is no reason to flip either way.</summary>
    public bool Required(bool current, DateTime now) => !Fresh(now) ? current
        : Api!.Value.Verdict == Workshop.Ok && SinceChange != false && !(SinceChange == null && Refused && ChangedAt != DateTime.MinValue && now < ChangedAt + ReviewAssumed);

    /// <summary>Whether Steam shows the item to everybody as far as the plugin knows - the HUD check's interlock (nobody is disconnected
    /// otherwise): a fresh Web API answer calling it ok and, since a change, a visit that showed it (once the window is over: none that did not).</summary>
    public bool Public(DateTime now) =>
        Fresh(now) && Api!.Value.Verdict == Workshop.Ok && SinceChange != false && (ChangedAt == DateTime.MinValue || SinceChange == true || now >= WindowUntil);

    /// <summary>Why it is not required, for the words (HudCheck.DroppedNews and the rest).</summary>
    public HudCheck.Cause Cause => Api is { } a && a.Verdict != Workshop.Ok ? HudCheck.Cause.Unavailable
        : SinceChange != true && ChangedAt != DateTime.MinValue ? HudCheck.Cause.Review : HudCheck.Cause.Unknown;
}
