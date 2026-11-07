// Playing a Workshop map that is not on the list: a player types its id on the menu's keyboard, or pastes a link in chat,
// and the map browser shows a card for it instead of an empty page. The server's own refusals come first (ServerRefusal: the catalog's, and
// our own HUD's item); then the plugin asks the Steam Web API about the item (the game server reaches Steam; the bridge stays off the internet)
// and refuses one that is gone, private, banned or not for CS2, saying so. Otherwise one click downloads
// it into the server's own Workshop cache with MultiAddonManager's mm_download_addon - nothing changes for anybody meanwhile - and only
// once it is installed does the plugin ask the bridge for the change (!map <id>), so the map loads at once instead of stalling the server
// on a download. The card shows each step and the download's progress. Shared like Hud.cs: the bridge's --selftest drives all of it;
// the plugin owns the HTTP, the download and the clock. Plain BCL.
using System.Globalization;

/// <summary>One player's card for an unlisted Workshop id, step by step. The plugin moves it on (Checked, Started, Progress, Installed,
/// Requested, Failed); the card only says where it is.</summary>
public sealed class WorkshopCard
{
    public enum Step { None, Checking, Refused, Ready, Waiting, Downloading, Installed, Requested, Failed }

    public const int MinIdDigits = 7, MaxIdDigits = 12; // a Workshop id; shorter numbers are ranks on the list
    public const string Panel = "gg_ws", Play = "gg_ws_play", Bar = "gg_ws_bar";

    public long Id { get; private set; }
    public Step At { get; private set; }
    public WorkshopItem? Item { get; private set; }
    public string Named { get; private set; } = "";          // the server's own name for a refused id (HudCatalog.Refused)
    public string Why { get; private set; } = "";
    public int Percent { get; private set; }
    public bool SteamBusy { get; private set; }              // asked for, but Steam has not started it: its once-a-boot cache check runs first
    public DateTime Since { get; private set; }              // when this id was first shown: the Workshop is asked once it has held still

    /// <summary>How long a typed id must hold still before the Workshop is asked: a 10-digit id is typed through 7, 8 and 9 digits first.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.2);

    /// <summary>The refusal's claim for an item with no map in it. Deliberately neutral: the bridge's list mixes skin packs, configs, sound packs
    /// and empty uploads, and "skin pack" was false for most of them. True of every one: it is not a playable map, and the server cannot load it.</summary>
    public const string NotAMap = "isn't a playable map - It can't be loaded";

    /// <summary>What the card calls our own HUD's Workshop item.</summary>
    public const string HudItemName = "Over Strike 2 HUD";

    /// <summary>The server's own refusal of an id, decided before the Workshop is asked: the catalog's (HudCatalog.Refused: a non-map, a blocked
    /// map, one gone from the Workshop), else our own item that is no map - the HUD's, hudItem, which the plugin reads from the settings
    /// (hud.addon, the one place its id lives). No list of the bridge's names the HUD: the sync never tests it, as it is never a candidate,
    /// and the Workshop calls it a CS2 item like any map. Null: nothing against it here.</summary>
    public static (string Name, string Why)? ServerRefusal(long id, HudCatalog? catalog, string hudItem) =>
        catalog != null && catalog.Refused.TryGetValue(id, out var no) ? no
        : hudItem != "" && id.ToString(CultureInfo.InvariantCulture) == hudItem ? (HudItemName, NotAMap) : null;

    /// <summary>A query that is a Workshop id: digits only, MinIdDigits to MaxIdDigits of them. Null for anything else.</summary>
    public static long? IdOf(string query)
    {
        var q = query.Trim();
        return q.Length is >= MinIdDigits and <= MaxIdDigits && q.All(char.IsAsciiDigit) && long.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// <summary>A pasted Workshop link (…filedetails/?id=123…) or a bare id, as chat delivers it; null for anything else.</summary>
    public static long? IdInChat(string text)
    {
        var t = text.Trim();
        var at = t.IndexOf("id=", StringComparison.OrdinalIgnoreCase);
        if (t.Contains("steamcommunity.com/", StringComparison.OrdinalIgnoreCase) && at >= 0)
        {
            var digits = new string(t[(at + 3)..].TakeWhile(char.IsAsciiDigit).ToArray());
            return IdOf(digits);
        }
        return IdOf(t);
    }

    /// <summary>The card for a new id (Checking), or nothing (None) - a query that is no Workshop id, or one on the list.</summary>
    public void Show(long? id, DateTime now = default)
    {
        if (id == Id && At != Step.None) return;
        Since = now;
        Id = id ?? 0;
        At = id != null ? Step.Checking : Step.None;
        Item = null; Named = ""; Why = ""; Percent = 0; SteamBusy = false;
    }

    /// <summary>The server's own refusal (ServerRefusal: the catalog's - a non-map, a blocked map, one gone from the Workshop - or our own
    /// HUD's item) - decided before the Workshop is asked, so nothing is downloaded for a map the server would refuse anyway.</summary>
    public void Refuse(string name, string why)
    {
        if (At != Step.Checking) return;
        (At, Named, Why) = (Step.Refused, name, why);
    }

    public void Checked(WorkshopItem? item, string? error = null)
    {
        if (At != Step.Checking) return;
        if (item == null) { (At, Why) = (Step.Failed, error ?? "the Workshop did not answer - Try again in a moment"); return; }
        Item = item;
        (At, Why) = item.Refusal is { } no ? (Step.Refused, no) : (Step.Ready, "");
    }

    /// <summary>The click: Waiting while another download runs, Downloading once it is this one's turn.</summary>
    public void Started(bool queued) { if (At is Step.Ready or Step.Waiting or Step.Failed && Item != null) (At, Percent, SteamBusy) = (queued ? Step.Waiting : Step.Downloading, 0, false); }

    public void Progress(int percent, bool steamBusy)
    {
        if (At is not (Step.Downloading or Step.Waiting)) return;
        (At, Percent, SteamBusy) = (Step.Downloading, Math.Clamp(percent, 0, 99), steamBusy);
    }

    public void Installed() { if (At is Step.Ready or Step.Waiting or Step.Downloading) (At, Percent) = (Step.Installed, 100); }

    public void Requested() { if (At == Step.Installed) At = Step.Requested; }

    public void Failed(string why) { if (At is Step.Waiting or Step.Downloading or Step.Installed) (At, Why) = (Step.Failed, why); }

    static string Size(long bytes) => bytes <= 0 ? "" : bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.0} GB" : $"{Math.Max(1, (bytes + 500_000) / 1_000_000)} MB";

    /// <summary>The card's words, for the panels gg_ws_*.</summary>
    public List<(string Id, string Text)> Texts()
    {
        var title = Item?.Title is { Length: > 0 } t ? t : Named != "" ? Named : $"Workshop item {Id}";
        var info = Item == null ? $"Workshop id {Id}" : string.Join("  ·  ", new[] { $"id {Id}", Size(Item.Bytes), string.Join(", ", Item.Tags.Take(3)) }.Where(x => x != ""));
        var status = At switch
        {
            Step.Checking => "Asking the Workshop about it...",
            Step.Refused => $"Not playable: it {Why}.",
            Step.Ready => "Not on the list, but playable. The server downloads it first; nobody's game is interrupted meanwhile.",
            Step.Waiting => "Another download is running - This one starts right after it.",
            Step.Downloading => SteamBusy ? "Waiting for Steam: the first download after a server restart waits for its map-cache check (up to 15 minutes)."
                : $"Downloading to the server: {Percent}%",
            Step.Installed => "Downloaded - Asking for the map change.",
            Step.Requested => "Requested: it plays when its turn comes, like any pick.",
            Step.Failed => $"Could not get it: {Why}.",
            _ => "",
        };
        var button = At switch
        {
            Step.Ready => "Download and play",
            Step.Failed when Item != null && Item.Refusal == null => "Try again",
            Step.Waiting or Step.Downloading => "Downloading...",
            Step.Installed or Step.Requested => "Requested",
            _ => "",
        };
        return [("gg_ws_title", title), ("gg_ws_info", info), ("gg_ws_status", status), ("gg_ws_play_text", button)];
    }

    /// <summary>The card's classes: shown at all, its step, the bar in tenths, and whether Play takes a click.</summary>
    public List<(string Id, string Class, bool On)> Classes()
    {
        var c = new List<(string, string, bool)> { (Panel, "used", At != Step.None) };
        foreach (var step in Enum.GetValues<Step>().Where(s => s != Step.None)) c.Add((Panel, step.ToString().ToLowerInvariant(), At == step));
        var tenths = At is Step.Installed or Step.Requested ? 10 : At == Step.Downloading && !SteamBusy ? Percent / 10 : 0;
        for (var i = 0; i <= 10; i++) c.Add((Bar, $"p{i}", i == tenths));
        c.Add((Play, "off", !(At == Step.Ready || (At == Step.Failed && Item != null && Item.Refusal == null))));
        return c;
    }
}
