// The HUD check: whether a player's game shows this server's HUD, and which content of it. MultiAddonManager makes everybody download the addon,
// but whether the layout loaded the server cannot see - a game that kept an older copy shows nothing and says nothing - and a layout cannot send
// anything by itself (CS2 drops a layout with scripts in it). The only proof is a click: a Button press in the window reaches the server
// (OnCustomHudClicked) only from a game that has the layout and shows it. So a player not seen clicking since their last real join gets the window
// with one gold button, "Click to play". From content v4.6.0 on that button's id carries the content (VersionButton: gg_hudver_v4.6.0), so its click
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

    /// <summary>The gold button's words while a player is being checked: the live column's action button of the contents before v4.6.0 (set from
    /// the server); from v4.6.0 on, the version button's own fixed words are the same.</summary>
    public const string ActText = "Click to play", ActSub = "One click shows the server your HUD works";

    /// <summary>HUD_CHECK: off, warn (tell, never disconnect) or enforce. "dryrun", the word the plugins before v4.6.0 know for warn, reads as warn.</summary>
    public static Mode Parse(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "warn" or "dryrun" or "dry-run" or "dry" or "test" => Mode.Warn,
        "enforce" or "on" => Mode.Enforce,
        _ => Mode.Off,
    };

    public static string Name(Mode mode) => mode switch { Mode.Warn => "warn", Mode.Enforce => "enforce", _ => "off" };

    /// <summary>The mode as the live state carries it: warn as "dryrun", which a plugin before v4.6.0 reads as its dry run - and without a release's
    /// proof, which no bridge serves any more, a dry run never disconnects anybody either.</summary>
    public static string WireName(Mode mode) => mode == Mode.Warn ? "dryrun" : Name(mode);

    /// <summary>The plugin's view: the mode the bridge serves, or off while it has no fresh state from the bridge. "Fresh" is the age of the bridge's
    /// last answer, not of the last change in it: on a quiet server nothing changes for minutes.</summary>
    public static Mode ModeOf(HudState? state, double stateAgeSeconds) =>
        state == null || stateAgeSeconds > StaleSeconds ? Mode.Off : Parse(state.CheckMode);

    /// <summary>Verified since the player's last real join, as far as the bridge knows.</summary>
    public static bool IsVerified(HudState? state, long account) => state != null && state.Verified.ContainsKey(account);

    /// <summary>"v2.1.1", "v14.5.27": what a release or a content looks like (Hud.Release, Hud.Content), and the one form everything shows, logs and compares.</summary>
    public static bool IsRelease(string text)
    {
        var parts = text.Length > 1 && text[0] == 'v' ? text[1..].Split('.') : [];
        return parts.Length == 3 && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit));
    }

    /// <summary>A version word in the one form: "v4_6_0" reads as "v4.6.0", anything else comes back as it is. The only reader of the old form, used where a
    /// word comes in - a version button's id, a click's !hudok, the live state's ok lines: what a game or a plugin from before the new form sends lasts until
    /// Steam or the next restart updates it. Nothing past those doors sees the old form.</summary>
    public static string Normal(string text) => !text.Contains('.') && text.Replace('_', '.') is var dotted && IsRelease(dotted) ? dotted : text;

    /// <summary>What a game's HUD is known as after the check's click: a content (v4.6.0, from its version button), Older (an older content's gold
    /// button), or Hud.Release (verified, which content not known - the click was another button, or a plugin before v4.6.0 sent it).</summary>
    public const string Older = "older";

    /// <summary>Whether !hudok may carry this: a content or release, or Older.</summary>
    public static bool IsVersionWord(string text) => text == Older || IsRelease(Normal(text));

    /// <summary>The version button: the gold "Click to play" of the HUD check, whose id names the content that has it. The layout may put it
    /// anywhere (the welcome window's close button could carry it one day): any button with this prefix proves its content.</summary>
    public const string VersionPrefix = "gg_hudver_";

    /// <summary>The version button's id for a content: the one place that says the id's form, gg_hudver_v4.6.0, and --selftest holds the layout's one id to it.
    /// That CS2 takes a dot in a panel id is not proven until a real client has loaded this layout. If it does not, this line becomes
    /// VersionPrefix + content.Replace('.', '_') and the layout's id follows; VersionOf reads both.</summary>
    public static string VersionButton(string content) => VersionPrefix + content;

    /// <summary>The content a version button proves, in the one form whichever way its id writes it, or null for any other button.</summary>
    public static string? VersionOf(string buttonId) =>
        buttonId.StartsWith(VersionPrefix, StringComparison.Ordinal) && Normal(buttonId[VersionPrefix.Length..]) is var content && IsRelease(content) ? content : null;

    /// <summary>The level of a version with no line in HudVote.Contents: newer than this bridge or plugin knows, so above every level there is.</summary>
    public const int NewerLevel = int.MaxValue;

    /// <summary>A content as a number: its line in HudVote.Contents, never its digits (v4.6.0 is 46); NewerLevel for a version with no line; 0 for a word that is no version.</summary>
    public static int LevelOf(string version)
    {
        if (!IsRelease(version)) return 0;
        foreach (var (known, level) in HudVote.Contents) if (known == version) return level;
        return NewerLevel;
    }

    /// <summary>A HUD other than the server's own as the Players page says it: older, its content not known (Hud.Release: a click on a button
    /// that names none), or not seen yet ("": no click since the last real join).</summary>
    public static string VersionWords(string version) => version == Older ? "older - Restart CS2" : version == "" ? "not seen yet"
        : version == Hud.Release ? "version unknown" : LevelOf(version) < HudVote.ContentLevel ? $"{version} - Restart CS2" : version;

    /// <summary>Whether a known version is older than this server's content.</summary>
    public static bool IsOutdated(string version) => IsBefore(version, HudVote.ContentLevel);

    /// <summary>Whether this version is known to be a content older than this level: a shipped one below it, or the old word for "too old to say which".</summary>
    public static bool IsBefore(string version, int level) => version == Older || IsRelease(version) && version != Hud.Release && LevelOf(version) < level;

    /// <summary>What an anonymous visit to the item's Workshop page says about it: true - Steam shows it to everybody (HTTP 200, its title
    /// reads "Steam Workshop::..."); false - the page Steam shows a visitor instead, which is what everybody but the owner gets while the
    /// item or its latest version waits for Valve's moderation; null - no answer worth reading (rate limited with 429, an error, anything
    /// else). That page comes in two forms: Steam's old Error page, HTTP 200 titled "Steam Community :: Error" ("There was a problem
    /// accessing the item", 2026-10-07, and still on 2026-11-01 for gg_moonglass's update), and since 2026-10-31 Steam's new Workshop page,
    /// an HTTP 500 titled just "Steam Workshop" - gg_vinceville showed it for over 50 minutes under review, and an id that does not exist
    /// gets it too, and an item hidden that way must drop the requirement.
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
    // Who sees what: a player who still has the HUD sees the banner; one who joined while it was not required has no HUD at all, so
    // chat is the only way to reach them - one private line at their first spawn, and a line for everybody when it is dropped and back.

    /// <summary>The status banner when joiners stop getting the HUD: its headline and its line. Who is on keeps the HUD on the map that is on and loses it at the next map change,
    /// because the map change no longer carries it (MultiAddonManager, HudScreen.RequireAddon).</summary>
    public static (string Head, string Line) Dropped(Cause why) => why switch
    {
        Cause.Review => ("Steam is reviewing the new HUD", "Usually 15-60 min. New players join without the menu, and everybody loses it at the next map change - The game plays on"),
        Cause.Unavailable => ("The HUD is unavailable on Steam", "New players join without the menu, and everybody loses it at the next map change - The game plays on"),
        _ => ("Steam is not showing the HUD", "New players join without the menu, and everybody loses it at the next map change - The game plays on"),
    };

    /// <summary>A player's first spawn without the HUD during a hold: why they have no menu (at most LineMax characters).</summary>
    public static string NoMenuChat(Cause why, string? since) => why switch
    {
        Cause.Review => $"No menu right now: Steam is reviewing the HUD's update{(since != null ? $" (since {since})" : "")} - Play on, it comes back by itself",
        Cause.Unavailable => "No menu right now: The HUD is unavailable on Steam - Play on, random maps keep coming",
        _ => "No menu right now: Steam is not showing the HUD - Play on, it comes back by itself",
    };

    /// <summary>The status banner when the HUD is handed out again, and the chat line for those without it, who cannot read a banner.</summary>
    public const string BackHead = "The HUD is back on Steam", BackLine = "Everybody gets the menu again at the next map change - Or now, by reconnecting (console: retry)",
        BackChat = "The menu is back: You get it at the next map change - Or now, by reconnecting (console: retry)";

    /// <summary>CS2's alert text before the disconnect, two short lines (warn: the same, saying nobody is disconnected).</summary>
    public static string Alert(int seconds, bool warnOnly) => M.Lines("HUD check: No click yet", warnOnly ? "Close the window with its X" : $"Click any button within {seconds.ToString(CultureInfo.InvariantCulture)} s");

    /// <summary>The click's message to the bridge: verified, and which HUD (IsVersionWord).</summary>
    public static string Command(string version) => "!hudok " + version;

    public static string ItemUrl(string addon) => "steamcommunity.com/sharedfiles/filedetails/?id=" + addon;

    /// <summary>The disconnect reason, at most ReasonMax characters with a ten-digit item id.</summary>
    public static string Reason(string addon) => $"HUD missing: 1) subscribe {ItemUrl(addon)} 2) quit CS2 3) let Steam update 4) rejoin";

    /// <summary>How a player opens the window again: TAB + E opens the menu in every HUD_TAB mode but off (HudTab.cs).</summary>
    public const string Reopen = "hold TAB, press E";

    /// <summary>The warning before the disconnect (warn: the same, saying nobody is disconnected).</summary>
    public static string[] Warning(string addon, int seconds, bool warnOnly) =>
    [
        warnOnly ? "HUD check (nobody is disconnected): No click from your HUD. Click any button in the window (" + Reopen + ")."
               : $"HUD check: No click from your HUD yet - You are disconnected in {seconds.ToString(CultureInfo.InvariantCulture)} s. Click any button in the window ({Reopen}).",
        $"No window? Then your game lacks this server's HUD: 1) Subscribe {ItemUrl(addon)}",
        "2) Quit CS2  3) Let Steam finish the download  4) Start CS2 and rejoin",
    ];
}
