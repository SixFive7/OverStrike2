// The HUD check: whether a player's game shows this server's HUD, and which content of it. MultiAddonManager makes everybody download the addon,
// but whether the layout loaded the server cannot see - a game that kept an older copy shows nothing and says nothing - and a layout cannot send
// anything by itself (CS2 drops a layout with scripts in it). The only proof is a click: a Button press in the window reaches the server
// (OnCustomHudClicked) only from a game that has the layout and shows it. So a player not seen clicking since their last real join gets the window
// with one gold button, "Click to play". From content v4_6_0 on that button's id carries the content (VersionButton: gg_hudver_v4_6_0), so its click
// proves the exact version; an older content's gold button is the live column's action button, whose click counts as "older"; any other button
// verifies the HUD without saying which. One who has not clicked GraceSeconds after their first spawn on a map is told in chat what to do, and
// with HUD_CHECK=enforce disconnected; with warn only told.
//
// A verification lives in memory, per connection: the plugin keeps it, the bridge keeps it for the players on the server and serves only those,
// and both forget it at the player's next real join (a reconnecting player may have a new HUD). Nothing of it is written to a file.
//
// Shared like Hud.cs: the bridge serves the mode and the verifications and tests all of this in --selftest; the plugin runs HudGate per map. Plain
// BCL only. No fresh state from the bridge means off: no kicks.
using System.Globalization;
using System.Text.Json;

public static class HudCheck
{
    public enum Mode { Off, Warn, Enforce }

    public const double GraceSeconds = 90;  // from a player's first spawn on a map: the map has loaded and the team is picked by then
    public const double WarnSeconds = 15;   // the chat warning, this long before the disconnect
    public const double StaleSeconds = Hud.LongPollSeconds + 15; // no answer from the bridge for this long switches the check off - no state, no kicks (an idle long poll answers every LongPollSeconds)
    public const int ReasonMax = 127;       // a disconnect reason travels as SteamNetworkingSockets' close reason: 128 bytes with its NUL
    public const int LineMax = 127;         // what a private chat line may take here

    /// <summary>The gold button's words while a player is being checked: the live column's action button of the contents before v4_6_0 (set from
    /// the server); from v4_6_0 on, the version button's own fixed words are the same.</summary>
    public const string ActText = "Click to play", ActSub = "One click shows the server your HUD works";

    /// <summary>HUD_CHECK: off, warn (tell, never disconnect) or enforce. "dryrun", the word the plugins before v4_6_0 know for warn, reads as warn.</summary>
    public static Mode Parse(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "warn" or "dryrun" or "dry-run" or "dry" or "test" => Mode.Warn,
        "enforce" or "on" => Mode.Enforce,
        _ => Mode.Off,
    };

    public static string Name(Mode mode) => mode switch { Mode.Warn => "warn", Mode.Enforce => "enforce", _ => "off" };

    /// <summary>The mode as the live state carries it: warn as "dryrun", which a plugin before v4_6_0 reads as its dry run - and without a release's
    /// proof, which no bridge serves any more, a dry run never disconnects anybody either.</summary>
    public static string WireName(Mode mode) => mode == Mode.Warn ? "dryrun" : Name(mode);

    /// <summary>The plugin's view: the mode the bridge serves, or off while it has no fresh state from the bridge. "Fresh" is the age of the bridge's
    /// last answer, not of the last change in it: on a quiet server nothing changes for minutes.</summary>
    public static Mode ModeOf(HudState? state, double stateAgeSeconds) =>
        state == null || stateAgeSeconds > StaleSeconds ? Mode.Off : Parse(state.CheckMode);

    /// <summary>Verified since the player's last real join, as far as the bridge knows.</summary>
    public static bool IsVerified(HudState? state, long account) => state != null && state.Verified.ContainsKey(account);

    /// <summary>"v2_1_1": what a release or a content looks like (Hud.Release, Hud.Content).</summary>
    public static bool IsRelease(string text)
    {
        var parts = text.Length > 1 && text[0] == 'v' ? text[1..].Split('_') : [];
        return parts.Length == 3 && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit));
    }

    /// <summary>What a game's HUD is known as after the check's click: a content (v4_6_0, from its version button), Older (an older content's gold
    /// button), or Hud.Release (verified, which content not known - the click was another button, or a plugin before v4_6_0 sent it).</summary>
    public const string Older = "older";

    /// <summary>Whether !hudok may carry this: a content or release, or Older.</summary>
    public static bool IsVersionWord(string text) => text == Older || IsRelease(text);

    /// <summary>The version button: the gold "Click to play" of the HUD check, whose id names the content that has it. The layout may put it
    /// anywhere (the welcome window's close button could carry it one day): any button with this prefix proves its content.</summary>
    public const string VersionPrefix = "gg_hudver_";

    public static string VersionButton(string content) => VersionPrefix + content;

    /// <summary>The content a version button proves, or null for any other button.</summary>
    public static string? VersionOf(string buttonId) =>
        buttonId.StartsWith(VersionPrefix, StringComparison.Ordinal) && IsRelease(buttonId[VersionPrefix.Length..]) ? buttonId[VersionPrefix.Length..] : null;

    /// <summary>A content as a number: v4_6_0 is 46; 0 for anything else.</summary>
    public static int LevelOf(string version) =>
        IsRelease(version) && version[1..].Split('_') is [var major, var minor, _] && int.TryParse(major, out var a) && int.TryParse(minor, out var b) ? a * 10 + b : 0;

    /// <summary>A HUD other than the server's own as the Players page says it: older, its content not known (Hud.Release: a click on a button
    /// that names none), or not seen yet ("": no click since the last real join).</summary>
    public static string VersionWords(string version) => version == Older ? "older - restart CS2" : version == "" ? "not seen yet"
        : version == Hud.Release ? "version unknown" : LevelOf(version) < HudVote.ContentLevel ? $"{version} - restart CS2" : version;

    /// <summary>The note a player whose HUD is older than the server's gets at its click.</summary>
    public static string OutdatedNote(string version) =>
        $"Your HUD is {(version == Older ? "older than the server's" : version)} - the server has {Hud.Content}: quit CS2 and start it again, and Steam updates it";

    /// <summary>Whether a known version is older than this server's content.</summary>
    public static bool IsOutdated(string version) => version == Older || IsRelease(version) && version != Hud.Release && LevelOf(version) < HudVote.ContentLevel;

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

    /// <summary>The setting that keeps a plugin off the item's page for good, by hand included: HUD_ITEM_PAGE=off, what a test copy of the server runs
    /// with: nobody is on it, and its restarts must not add visits. It still reads the Web API, and without a visit it never
    /// has a verdict: joiners download the item as ever, and the HUD check's interlock stays off for ReviewWindow after an upload.</summary>
    public const string PageVariable = "HUD_ITEM_PAGE";

    public static bool PageOff(string? value) => string.Equals(value?.Trim(), "off", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>The click's message to the bridge: verified, and which HUD (IsVersionWord).</summary>
    public static string Command(string version) => "!hudok " + version;

    public static string ItemUrl(string addon) => "steamcommunity.com/sharedfiles/filedetails/?id=" + addon;

    /// <summary>The disconnect reason, at most ReasonMax characters with a ten-digit item id.</summary>
    public static string Reason(string addon) => $"HUD missing: 1) subscribe {ItemUrl(addon)} 2) quit CS2 3) let Steam update 4) rejoin";

    /// <summary>How a player opens the window again: TAB + E opens the menu in every HUD_TAB mode but off (HudTab.cs).</summary>
    public const string Reopen = "hold TAB, press E";

    /// <summary>The window has just opened for a player the server has not seen click.</summary>
    public const string Prompt = "HUD check: click 'Click to play' in the window - any button counts. Closed it? " + Reopen + ", click any button.";

    public const string Thanks = "HUD check: thanks - your HUD works. Have fun!";

    /// <summary>The warning before the disconnect (warn: the same, saying nobody is disconnected).</summary>
    public static string[] Warning(string addon, int seconds, bool warnOnly) =>
    [
        warnOnly ? "HUD check (nobody is disconnected): no click from your HUD. Click any button in the window (" + Reopen + ")."
               : $"HUD check: no click from your HUD yet - you are disconnected in {seconds.ToString(CultureInfo.InvariantCulture)} s. Click any button in the window ({Reopen}).",
        $"No window? Then your game lacks this server's HUD: 1) subscribe {ItemUrl(addon)}",
        "2) quit CS2  3) let Steam finish the download  4) start CS2 and rejoin",
    ];
}

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

/// <summary>One map's worth of the check in the plugin, per player slot: whose clock runs, who was warned, whose time is up. Every map
/// change reconnects everybody, so it starts over then; a verification lasts until the player's next real join.</summary>
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

    /// <summary>The seconds this slot's clock has left (the Players page says it); 0 when nothing waits.</summary>
    public int SecondsLeft(int slot, DateTime now) => waiting.TryGetValue(slot, out var s) && !s.Ended ? Math.Max(0, (int)Math.Ceiling((s.Due - now).TotalSeconds)) : 0;

    public void Gone(int slot) { waiting.Remove(slot); spawned.Remove(slot); }

    public void MapStarted() { waiting.Clear(); spawned.Clear(); }

    /// <summary>The check has just become stricter (off to warn, or warn to enforce): everybody still waiting gets a full grace
    /// period from now, so nobody who ignored the dry run is thrown out on the spot. Returns their slots, for the window.</summary>
    public List<int> Rearm(DateTime now)
    {
        foreach (var s in waiting.Values) { s.Due = now.AddSeconds(HudCheck.GraceSeconds); s.Warned = s.Ended = false; }
        return waiting.Keys.ToList();
    }

    /// <summary>What is due now: the warning WarnSeconds before the end, then the disconnect - or, with warn, only the log line.
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
