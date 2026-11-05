// Playing a Workshop map that is not on the list (2026-10-21): a player types its id on the menu's keyboard, or pastes a link in chat,
// and the map browser shows a card for it instead of an empty page. The plugin asks the Steam Web API about the item first (the game server reaches Steam; the bridge stays off the internet) and refuses one that is gone, private, banned or not for CS2, saying so. Otherwise one click downloads
// it into the server's own Workshop cache with MultiAddonManager's mm_download_addon - nothing changes for anybody meanwhile - and only
// once it is installed does the plugin ask the bridge for the change (!map <id>), so the map loads at once instead of stalling the server
// on a download. The card shows each step and the download's progress. Shared like Hud.cs: the bridge's --selftest drives all of it;
// the plugin owns the HTTP, the download and the clock. Plain BCL.
using System.Globalization;
using System.Text.Json;

/// <summary>One item as the Web API describes it (GetPublishedFileDetails), with what the card needs.</summary>
public sealed record WorkshopItem(long Id, int Result, int Visibility, bool Banned, long AppId, string Title, long Bytes, IReadOnlyList<string> Tags, string Preview = "")
{
    public const long Cs2 = 730;

    /// <summary>Null when the server can play it; otherwise why not, in the card's words.</summary>
    public string? Refusal => Result != 1 ? "is not on the Workshop (removed, or a wrong id)"
        : Banned ? "was banned from the Workshop"
        : Visibility != 0 ? "is not public on the Workshop - Only its author can download it"
        : AppId != Cs2 ? "is not a Counter-Strike 2 item"
        : null;

    /// <summary>Every published file the answer reports on, by id. An entry without a usable id is skipped.</summary>
    public static Dictionary<long, WorkshopItem> Parse(string json)
    {
        var items = new Dictionary<long, WorkshopItem>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response) || !response.TryGetProperty("publishedfiledetails", out var list)
            || list.ValueKind != JsonValueKind.Array) return items;
        foreach (var e in list.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var id = Long(e, "publishedfileid");
            if (id <= 0) continue;
            var banned = e.TryGetProperty("banned", out var b) && (b.ValueKind == JsonValueKind.True || (b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var bn) && bn != 0));
            var tags = new List<string>();
            if (e.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
                foreach (var tag in t.EnumerateArray())
                    if (tag.ValueKind == JsonValueKind.Object && tag.TryGetProperty("tag", out var tv) && tv.ValueKind == JsonValueKind.String) tags.Add(tv.GetString() ?? "");
            items[id] = new WorkshopItem(id, (int)Long(e, "result"), (int)Long(e, "visibility"), banned, Long(e, "consumer_app_id"),
                Hud.Clean(e.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String ? ti.GetString() : "", 60), Long(e, "file_size"), tags,
                e.TryGetProperty("preview_url", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() ?? "" : "");
        }
        return items;
    }

    /// <summary>A number the API sends as a number or as a string (file_size and publishedfileid come as strings).</summary>
    static long Long(JsonElement o, string name) =>
        !o.TryGetProperty(name, out var v) ? 0
        : v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n
        : v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
}

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

    /// <summary>The server's own refusal (HudCatalog.Refused: a non-map, a blocked map, one gone from the Workshop) - decided before the
    /// Workshop is asked, so nothing is downloaded for a map the bridge would refuse anyway.</summary>
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
