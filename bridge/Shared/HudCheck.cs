// The HUD check: whether a player's game shows this server's HUD, and which content of it. MultiAddonManager makes everybody download the addon,
// but whether the layout loaded the server cannot see - a game that kept an older copy shows nothing and says nothing - and a layout cannot send
// anything by itself (CS2 drops a layout with scripts in it). The only proof is a click: a Button press in the window reaches the server
// (OnCustomHudClicked) only from a game that has the layout and shows it. So a player not seen clicking since their last real join gets the window
// with one gold button, "Click to play". From content v4.6.0 on that button's id carries the content (HudVersion.Button: gg_hudver_v4.6.0), so its
// click proves the exact version; an older content's gold button is the live column's action button, whose click counts as older; any other
// button verifies the HUD without saying which (HudVersion.Older, Unknown). One who has not clicked GraceSeconds after their first spawn on a map is
// told in chat what to do, and with hud.check enforce disconnected; with warn only told.
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

    /// <summary>hud.check, and the live state's word for it: off, warn (tell, never disconnect) or enforce; anything else is off.</summary>
    public static Mode Parse(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "warn" => Mode.Warn,
        "enforce" => Mode.Enforce,
        _ => Mode.Off,
    };

    public static string Name(Mode mode) => mode switch { Mode.Warn => "warn", Mode.Enforce => "enforce", _ => "off" };

    /// <summary>The plugin's view: the mode the bridge serves, or off while it has no fresh state from the bridge. "Fresh" is the age of the bridge's
    /// last answer, not of the last change in it: on a quiet server nothing changes for minutes.</summary>
    public static Mode ModeOf(HudState? state, double stateAgeSeconds) =>
        state == null || stateAgeSeconds > StaleSeconds ? Mode.Off : Parse(state.CheckMode);

    /// <summary>Verified since the player's last real join, as far as the bridge knows.</summary>
    public static bool IsVerified(HudState? state, long account) => state != null && state.Verified.ContainsKey(account);

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

    /// <summary>The click's message to the bridge: verified, and which HUD (HudVersion.IsWord).</summary>
    public static string Command(string version) => "!hudok " + version;

    public static string ItemUrl(string addon) => "steamcommunity.com/sharedfiles/filedetails/?id=" + addon;

    /// <summary>The disconnect reason, at most ReasonMax characters with a ten-digit item id.</summary>
    public static string Reason(string addon) => $"HUD missing: 1) subscribe {ItemUrl(addon)} 2) quit CS2 3) let Steam update 4) rejoin";

    /// <summary>How a player opens the window again: TAB + E opens the menu unless hud.tab is off (HudTab.cs).</summary>
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
