// Maps that are gone from the Workshop. On 2026-10-20 !map 166 loaded de_coop from the server's own cache, but the item had been
// removed from the Workshop: every client got ERROR 15 in its download prompt and nobody could join until the map was switched by hand.
// A removed map is refused like a non-map, never rolled and never queued; the HUD's map browser shows it as unavailable.
//
// Which maps are gone is checked every 4 hours against the public Steam Web API (ISteamRemoteStorage/GetPublishedFileDetails) by the
// Workshop sync, which posts its verdicts to the bridge (POST /removed/<secret>, core's BridgeWire.RemovedBody; the bridge's Removed.cs reads
// them); the bridge stays off the internet by design and keeps them in /data/removed.json. The verdicts are shared: the plugin judges its own
// HUD item by them too (HudCheck.cs). Plain BCL.
//
// What it cannot see: an item held in Valve's moderation queue (a new version awaiting approval) - the Web API calls it public while
// nobody but its owner can download it (seen on the HUD's own item, 2026-10-07).

public static class Workshop
{
    public const string Ok = "ok", Removed = "removed", Hidden = "hidden", Banned = "banned";
    public const string DetailsUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    public static bool IsVerdict(string v) => v is Ok or Removed or Hidden or Banned;

    /// <summary>One published file in the Web API's answer: result 1, public (visibility 0) and not banned is downloadable.</summary>
    public static string Verdict(int result, int visibility, bool banned) => result != 1 ? Removed : banned ? Banned : visibility != 0 ? Hidden : Ok;

    /// <summary>What chat says about a map with this verdict, after its label.</summary>
    public static string Reason(string verdict) => verdict switch
    {
        Hidden => "is no longer public on the Workshop",
        Banned => "was banned from the Workshop",
        _ => "was removed from the Workshop",
    };
}
