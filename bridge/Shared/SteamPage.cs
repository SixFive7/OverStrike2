// What an anonymous visit to a Workshop item's page says about it: the one reading of that page, for the plugin's item watch (ItemWatch.cs, which
// stops requiring the HUD addon while Steam does not show it) and for both Workshop publishers (hud\publisher, maps\publisher, which compile this
// file in). Plain BCL, no state.
using System.Text.RegularExpressions;

public static class SteamPage
{
    /// <summary>True - Steam shows the item to everybody (HTTP 200, its title reads "Steam Workshop::..."); false - the page Steam shows a visitor
    /// instead, which is what everybody but the owner gets while the item or its latest version waits for Valve's moderation; null - no answer worth
    /// reading (rate limited with 429, an error, anything else). That page comes in two forms: Steam's old Error page, HTTP 200 titled "Steam Community
    /// :: Error" ("There was a problem accessing the item", 2026-10-07, and still on 2026-11-01 for gg_moonglass's update), and since 2026-10-31 Steam's
    /// new Workshop page, an HTTP 500 titled just "Steam Workshop" - gg_vinceville showed it for over 50 minutes under review, and an id that does not
    /// exist gets it too, and an item hidden that way must drop the requirement. A 500 without that title (an outage page) stays no verdict. The Web
    /// API reports a held item as public: it is only the second signal (HudCheck.ItemApi).</summary>
    public static bool? Shown(int status, string? html)
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

    /// <summary>The page's title, for the words a tool says about it; "" without one.</summary>
    public static string Title(string? html) => html == null ? "" : Regex.Match(html, "<title>([^<]*)</title>").Groups[1].Value;
}
