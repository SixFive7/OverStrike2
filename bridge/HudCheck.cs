// The HUD check: whether a player's game has this release's HUD loaded. MultiAddonManager already makes everybody download the addon,
// but whether the layout loaded the server cannot see - a game that kept an older copy (the owner's did, on 2026-10-07) shows nothing
// and says nothing. The only proof is a click: a Button press in the window reaches the server (OnCustomHudClicked) only from a game
// that has the layout and shows it. So a player the server has not seen click on this release gets the window with one gold button,
// "Click to play"; any click in it verifies them, for good on this release (the bridge keeps it per SteamID and release). One who has
// not clicked HudCheck.GraceSeconds after their first spawn on a map is told in chat what to do, and disconnected.
//
// Shared like Hud.cs: the bridge persists the verifications and the mode and tests all of this in --selftest; the GunGameWelcome plugin
// runs HudGate per map. Plain BCL only.
//
// The safety interlock: nobody had proven a real click reaches the server when this was written, and a check whose clicks never
// arrive would throw everybody out. So the mode (HUD_CHECK on the bridge) starts at dryrun - warn and log, never disconnect - and
// dryrun turns itself into enforce only once a real click has been verified on that very release: a new layout release (which could be
// broken, as v2_1_0 was) is back in dry run until somebody's click proves it. No fresh state from the bridge means off: no kicks.
using System.Globalization;
using System.Text.Json;

public static class HudCheck
{
    public enum Mode { Off, DryRun, Enforce }

    public const double GraceSeconds = 90;  // from a player's first spawn on a map: the map has loaded and the team is picked by then
    public const double WarnSeconds = 15;   // the chat warning, this long before the disconnect
    public const double StaleSeconds = 10;  // a bridge state older than this switches the check off - no state, no kicks
    public const int ReasonMax = 127;       // a disconnect reason travels as SteamNetworkingSockets' close reason: 128 bytes with its NUL
    public const int LineMax = 127;         // what a private chat line may take here

    /// <summary>The gold button's words while a player is being checked (the contextual button, set from the server: no new layout).</summary>
    public const string ActText = "Click to play", ActSub = "One click shows the server your HUD works";

    public static Mode Parse(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "dryrun" or "dry-run" or "dry" or "test" => Mode.DryRun,
        "enforce" or "on" => Mode.Enforce,
        _ => Mode.Off,
    };

    public static string Name(Mode mode) => mode switch { Mode.DryRun => "dryrun", Mode.Enforce => "enforce", _ => "off" };

    /// <summary>The mode in force for a release: dryrun becomes enforce once a real click has been verified on that release.</summary>
    public static Mode Effective(Mode configured, IReadOnlyDictionary<string, string> proofs, string release) =>
        configured == Mode.DryRun && proofs.ContainsKey(release) ? Mode.Enforce : configured;

    /// <summary>The plugin's view: the effective mode, or off while it has no fresh state from the bridge. "Fresh" is the age of the
    /// bridge's last answer, NOT of the last change in it: until 2026-10-11 the plugin measured the latter, so on a quiet server the check
    /// went off every ten seconds and back on with the next vote - 2026-10-08 shows it flapping through a whole evening.</summary>
    public static Mode ModeOf(HudState? state, double stateAgeSeconds, string release) =>
        state == null || stateAgeSeconds > StaleSeconds ? Mode.Off : Effective(Parse(state.CheckMode), state.Proofs, release);

    /// <summary>Verified on this release, as far as the bridge knows.</summary>
    public static bool IsVerified(HudState? state, long account, string release) =>
        state != null && state.Verified.TryGetValue(account, out var r) && r == release;

    /// <summary>"v2_1_1": what a release looks like (Hud.Release) - the only argument !hudok takes.</summary>
    public static bool IsRelease(string text)
    {
        var parts = text.Length > 1 && text[0] == 'v' ? text[1..].Split('_') : [];
        return parts.Length == 3 && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit));
    }

    /// <summary>What an anonymous visit to the item's Workshop page says about it: true - Steam shows it to everybody (HTTP 200, its title
    /// reads "Steam Workshop::..."); false - the page Steam shows a visitor instead, which is what everybody but the owner gets while the
    /// item or its latest version waits for Valve's moderation; null - no answer worth reading (rate limited with 429, an error, anything
    /// else). That page comes in two forms: Steam's old Error page, HTTP 200 titled "Steam Community :: Error" ("There was a problem
    /// accessing the item", 2026-10-07, and still on 2026-11-01 for gg_moonglass's update), and since 2026-10-31 Steam's new Workshop page,
    /// an HTTP 500 titled just "Steam Workshop" - gg_vinceville showed it for over 50 minutes under review, and an id that does not exist
    /// gets it too. Until GunGameWelcome 3.1.3 a 500 was no verdict, so an item hidden that way never dropped the requirement (#19).
    /// A 500 without that title (an outage page) stays no verdict. The Web API reports a held item as public: it is only the second
    /// signal (ItemApi).</summary>
    public static bool? PagePublic(int status, string? html)
    {
        if (html == null) return null;
        if (status == 200)
        {
            if (html.Contains(OldErrorTitle, StringComparison.Ordinal)) return false;
            return html.Contains("<title>Steam Workshop::", StringComparison.Ordinal) ? true : null;
        }
        return status == 500 && html.Contains(NewErrorTitle, StringComparison.Ordinal) ? false : null;
    }

    public const string OldErrorTitle = "<title>Steam Community :: Error</title>", NewErrorTitle = "<title>Steam Workshop</title>";

    /// <summary>The Web API's view of the HUD item (GetPublishedFileDetails, api.steampowered.com: anonymous, and not rate-limited like the
    /// community site that answers the page visits with 429 half the time): its Workshop verdict (Workshop.Ok, Removed, Hidden, Banned),
    /// when it last changed (time_updated, unix seconds) and its content's handle (hcontent_file). It cannot see moderation - a held item
    /// reads ok - but it can say two things the page cannot: that a new version was uploaded a moment ago (time_updated, which moves at the
    /// upload and sometimes again at the approval), and that the item is gone, private or banned.</summary>
    public readonly record struct ItemApi(string Verdict, long TimeUpdated, string Content);

    /// <summary>The Web API's answer (GetPublishedFileDetails JSON) for one item: its verdict, time_updated and hcontent_file; null when
    /// the answer does not mention that item. An item that is gone comes back with no more than its id and a result: Removed, time 0.</summary>
    public static ItemApi? ParseItem(string json, long id)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var response) || !response.TryGetProperty("publishedfiledetails", out var list)
                || list.ValueKind != JsonValueKind.Array) return null;
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("publishedfileid", out var pid)) continue;
                var idText = pid.ValueKind == JsonValueKind.String ? pid.GetString() : pid.GetRawText();
                if (idText != id.ToString(CultureInfo.InvariantCulture)) continue;
                static long Long(JsonElement o, string name) => !o.TryGetProperty(name, out var v) ? 0 : v.ValueKind switch
                {
                    JsonValueKind.Number when v.TryGetInt64(out var n) => n,
                    JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) => n,
                    _ => 0,
                };
                var banned = e.TryGetProperty("banned", out var b) && (b.ValueKind == JsonValueKind.True || (b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var bn) && bn != 0));
                var content = e.TryGetProperty("hcontent_file", out var h) ? (h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : h.GetRawText()) : "";
                return new ItemApi(Workshop.Verdict((int)Long(e, "result"), (int)Long(e, "visibility"), banned), Long(e, "time_updated"), content);
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>How long after a change the Web API shows (an upload: time_updated or hcontent_file moved) the item's page may be visited
    /// while it is not shown: Valve's reviews took 9 to over 50 minutes on 2026-10-21..11-01, so three hours is generous.</summary>
    public static readonly TimeSpan ReviewWindow = TimeSpan.FromHours(3);

    /// <summary>Why joiners stop getting the item, for the words players get: Review - the Web API showed a new version and the page then
    /// showed Steam's not-shown page, which is Valve reviewing it; Unavailable - the Web API itself says the item is gone, private or
    /// banned; Unknown - a not-shown page nobody's upload explains (a visit asked for by hand).</summary>
    public enum Cause { Unknown, Review, Unavailable }

    // ---- the words players get while the HUD is not handed out ----
    // Who sees what: a player who still has the HUD sees the news line; one who joined while it was not required has no HUD at all, so
    // chat is the only way to reach them - one private line at their first spawn, and a line for everybody when it is dropped and back.

    /// <summary>The news line when joiners stop getting the HUD (at most 240 characters). since: the upload's local time, if known.</summary>
    public static string DroppedNews(Cause why, string? since) => why switch
    {
        Cause.Review => $"HUD update: Steam is reviewing the new version{(since != null ? $" (uploaded {since})" : "")}, usually 15-60 min. Until then new players join without the menu and yours goes at the next map change - the game plays on. It comes back by itself.",
        Cause.Unavailable => "The HUD is unavailable on Steam right now: new players join without the menu and yours goes at the next map change - the game plays on as normal.",
        _ => "Steam is not showing the HUD right now: new players join without the menu and yours goes at the next map change - the game plays on. It comes back by itself.",
    };

    /// <summary>The same in chat, for everybody, when it is dropped (at most LineMax characters).</summary>
    public static string DroppedChat(Cause why) => why switch
    {
        Cause.Review => "[HUD] Steam is reviewing the HUD's update: no menu (TAB) for a while. The game plays on; the menu comes back by itself.",
        Cause.Unavailable => "[HUD] The HUD is unavailable on Steam right now: no menu (TAB) for a while. The game plays on as normal.",
        _ => "[HUD] Steam is not showing the HUD right now: no menu (TAB) for a while. The game plays on; it comes back by itself.",
    };

    /// <summary>A player's first spawn while joiners do not get the HUD: why they have no menu (at most LineMax characters).</summary>
    public static string NoMenuChat(Cause why, string? since) => why switch
    {
        Cause.Review => $"[HUD] No menu right now: Steam is reviewing the HUD's update{(since != null ? $" (since {since})" : "")}. Play on - it comes back by itself.",
        Cause.Unavailable => "[HUD] No menu right now: the HUD is unavailable on Steam. Play on - random maps keep coming.",
        _ => "[HUD] No menu right now: Steam is not showing the HUD. Play on - it comes back by itself.",
    };

    /// <summary>The news line and the chat line for everybody when the HUD is handed out again.</summary>
    public const string BackNews = "The HUD is back on Steam: everybody gets the menu again at the next map change - or now, by reconnecting (console: retry).",
        BackChat = "[HUD] The menu is back: you get it at the next map change, or now by reconnecting (console: retry).";

    /// <summary>The click's message to the bridge: verified on this release.</summary>
    public static string Command(string release) => "!hudok " + release;

    public static string ItemUrl(string addon) => "steamcommunity.com/sharedfiles/filedetails/?id=" + addon;

    /// <summary>The disconnect reason, at most ReasonMax characters with a ten-digit item id.</summary>
    public static string Reason(string addon) => $"HUD missing: 1) subscribe {ItemUrl(addon)} 2) quit CS2 3) let Steam update 4) rejoin";

    /// <summary>How a player opens the window again: chat commands are gone since v3_0_0 (a typed !gg only gets Hud.ChatOff), and
    /// TAB + E opens the menu in every HUD_TAB mode but off (HudTab.cs). Until 2026-10-24 these lines still said "!gg".</summary>
    public const string Reopen = "hold TAB, press E";

    /// <summary>The window has just opened for a player the server has not seen click.</summary>
    public const string Prompt = "HUD check: click 'Click to play' in the window - any button counts. Closed it? " + Reopen + ", click any button.";

    public const string Thanks = "HUD check: thanks - your HUD works. Have fun!";

    /// <summary>The warning before the disconnect (dry run: the same, saying nobody is disconnected yet).</summary>
    public static string[] Warning(string addon, int seconds, bool dryRun) =>
    [
        dryRun ? "HUD check (test - nobody is disconnected yet): no click from your HUD. Click any button in the window (" + Reopen + ")."
               : $"HUD check: no click from your HUD yet - you are disconnected in {seconds.ToString(CultureInfo.InvariantCulture)} s. Click any button in the window ({Reopen}).",
        $"No window? Then your game lacks this server's HUD: 1) subscribe {ItemUrl(addon)}",
        "2) quit CS2  3) let Steam finish the download  4) start CS2 and rejoin",
    ];
}

/// <summary>When the plugin looks at the HUD item, and what it makes of it - free of CounterStrikeSharp and of the network, so the bridge's
/// --selftest drives it on a handed clock; the plugin's ItemWatch does the fetching. Since 2026-11-01 the Web API (GetPublishedFileDetails, api.steampowered.com) is the signal, read every minute: its
/// verdict says gone, private or banned, and a change in time_updated or hcontent_file says a new version went up. The item's page on
/// the community site - the only thing that shows Valve's review - is visited only right after such a change, then at most every 10
/// minutes while it is not shown, for ReviewWindow after the change, and at a plugin start only if the Web API's time_updated is that
/// recent (an upload may be under review). Never otherwise: no idle polling. Until then (GunGameWelcome 3.1.3 and before) the page was
/// visited every 2 minutes, all day.</summary>
public sealed class ItemPlan
{
    public static readonly TimeSpan ApiEvery = TimeSpan.FromMinutes(1), VisitEvery = TimeSpan.FromMinutes(10), ApiFresh = TimeSpan.FromMinutes(10);

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

    /// <summary>Page visits since the plugin loaded, and since the last change - the count the owner wants kept small.</summary>
    public int Visits { get; private set; }
    public int VisitsSinceChange { get; private set; }

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

    /// <summary>The visit's verdict (HudCheck.PagePublic): shown ends the visits; not shown or unreadable (429) plans the next in VisitEvery.</summary>
    public void PageAnswer(bool? verdict, DateTime now)
    {
        if (verdict != null) SinceChange = verdict;
        NextVisit = verdict == true ? DateTime.MaxValue : now + VisitEvery;
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
    /// since the last change found Steam's not-shown page (the first one is enough: the change explains it); again once a visit shows it.
    /// Without a fresh Web API answer nothing changes (current): no word from Steam is no reason to flip either way.</summary>
    public bool Required(bool current, DateTime now) => !Fresh(now) ? current : Api!.Value.Verdict == Workshop.Ok && SinceChange != false;

    /// <summary>Whether Steam shows the item to everybody as far as the plugin knows - the HUD check's interlock (nobody is disconnected
    /// otherwise): a fresh Web API answer calling it ok and, since a change, a visit that showed it (once the window is over: none that did not).</summary>
    public bool Public(DateTime now) =>
        Fresh(now) && Api!.Value.Verdict == Workshop.Ok && SinceChange != false && (ChangedAt == DateTime.MinValue || SinceChange == true || now >= WindowUntil);

    /// <summary>Why it is not required, for the words (HudCheck.DroppedNews and the rest).</summary>
    public HudCheck.Cause Cause => Api is { } a && a.Verdict != Workshop.Ok ? HudCheck.Cause.Unavailable
        : SinceChange == false && ChangedAt != DateTime.MinValue ? HudCheck.Cause.Review : HudCheck.Cause.Unknown;
}

/// <summary>One map's worth of the check in the plugin, per player slot: whose clock runs, who was warned, whose time is up. Every map
/// change reconnects everybody, so it starts over then; a verification is kept by the bridge and never needs repeating.</summary>
public sealed class HudGate
{
    public enum Act { Warn, Kick, WouldKick }

    sealed class Slot { public long Account; public DateTime Due; public bool Warned, Ended; }

    readonly Dictionary<int, Slot> waiting = new();
    readonly HashSet<int> spawned = new(); // slots that spawned on this map already
    HudCheck.Mode lastMode = HudCheck.Mode.Off;

    /// <summary>A spawn. The first on this map of a player the server has not seen click starts their clock - true then, so the plugin
    /// opens the window. Later spawns, and verified players, change nothing.</summary>
    public bool Spawned(int slot, long account, bool verified, DateTime now)
    {
        if (!spawned.Add(slot) || verified) return false;
        waiting[slot] = new Slot { Account = account, Due = now.AddSeconds(HudCheck.GraceSeconds) };
        return true;
    }

    /// <summary>Still waiting for this slot's click: the gold button says "Click to play" for them, and a click is the check, not a command.</summary>
    public bool Checking(int slot) => waiting.ContainsKey(slot);

    public void Verified(int slot) => waiting.Remove(slot);

    public void Gone(int slot) { waiting.Remove(slot); spawned.Remove(slot); }

    public void MapStarted() { waiting.Clear(); spawned.Clear(); }

    /// <summary>The check has just become stricter (off to dry run, or dry run to enforce): everybody still waiting gets a full grace
    /// period from now, so nobody who ignored the dry run is thrown out on the spot. Returns their slots, for the window.</summary>
    public List<int> Rearm(DateTime now)
    {
        foreach (var s in waiting.Values) { s.Due = now.AddSeconds(HudCheck.GraceSeconds); s.Warned = s.Ended = false; }
        return waiting.Keys.ToList();
    }

    /// <summary>What is due now: the warning WarnSeconds before the end, then the disconnect - or, in a dry run, only the log line.
    /// Verified players leave the list; with the check off nothing happens at all.</summary>
    public List<(int Slot, Act Act, int Seconds)> Frame(DateTime now, HudCheck.Mode mode, Func<long, bool> verified)
    {
        var acts = new List<(int, Act, int)>();
        var resumed = lastMode == HudCheck.Mode.Off && mode != HudCheck.Mode.Off;
        lastMode = mode;
        if (mode == HudCheck.Mode.Off) return acts;
        foreach (var (slot, s) in waiting.ToList())
        {
            if (verified(s.Account)) { waiting.Remove(slot); continue; }
            if (s.Ended) continue;
            // The check was off for a while (no word from the bridge): nobody's warning is swallowed by the pause.
            if (resumed && !s.Warned && (s.Due - now).TotalSeconds < HudCheck.WarnSeconds) s.Due = now.AddSeconds(HudCheck.WarnSeconds);
            var left = (s.Due - now).TotalSeconds;
            if (!s.Warned && left <= HudCheck.WarnSeconds) { s.Warned = true; acts.Add((slot, Act.Warn, (int)Math.Ceiling(Math.Max(0, left)))); }
            if (left <= 0) { s.Ended = true; acts.Add((slot, mode == HudCheck.Mode.Enforce ? Act.Kick : Act.WouldKick, 0)); }
        }
        return acts;
    }
}
