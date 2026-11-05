// Maps that are gone from the Workshop. On 2026-10-20 !map 166 loaded de_coop from the server's own cache, but the item had been
// removed from the Workshop: every client got ERROR 15 in its download prompt and nobody could join until the map was switched by hand.
// A removed map is refused like a non-map, never rolled and never queued; the HUD's map browser shows it as unavailable.
//
// Which maps are gone is checked every 4 hours against the public Steam Web API (ISteamRemoteStorage/GetPublishedFileDetails) by the
// Workshop sync, which posts its verdicts here (POST /removed/<secret>); the bridge stays off the internet by design and keeps them in
// /data/removed.json. (The plugin's own daily check went with the owner's 74 a: one checker, inside the sync's request budget.) Shared like
// Hud.cs: the codec and the verdict are tested by the bridge's --selftest, and the plugin's HUD item watch reads the Web API's answer with
// ParseDetails. Plain BCL.
//
// What it cannot see: an item held in Valve's moderation queue (a new version awaiting approval) - the Web API calls it public while
// nobody but its owner can download it (seen on the HUD's own item, 2026-10-07).
using System.Globalization;
using System.Text;
using System.Text.Json;

public static class Workshop
{
    public const string ReportPath = "/removed/";
    public const string ReportMagic = "gg-removed\t1";
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

    /// <summary>The verdicts the sync hands the bridge: "id&lt;TAB&gt;verdict" per line after the magic line.</summary>
    public static string EncodeReport(IEnumerable<(long Id, string Verdict)> verdicts)
    {
        var s = new StringBuilder(ReportMagic).Append('\n');
        foreach (var (id, v) in verdicts) s.Append(id.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(v).Append('\n');
        return s.ToString();
    }

    /// <summary>A report back out of its text: lines with a positive id and a known verdict, the last one per id; anything else is dropped.</summary>
    public static List<(long Id, string Verdict)> DecodeReport(string? text)
    {
        if (text == null || !text.StartsWith(ReportMagic)) return [];
        var by = new Dictionary<long, string>();
        foreach (var raw in text.Split('\n').Skip(1))
        {
            var f = raw.TrimEnd('\r').Split('\t');
            if (f.Length == 2 && long.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 && IsVerdict(f[1])) by[id] = f[1];
        }
        return by.Select(x => (x.Key, x.Value)).ToList();
    }

    /// <summary>The Web API's answer (JSON) as verdicts, one per published file it reported on. An entry without a usable
    /// publishedfileid is skipped; one that says no more than its id and a result (an item that is gone) is judged on the result.</summary>
    public static List<(long Id, string Verdict)> ParseDetails(string json)
    {
        var verdicts = new List<(long, string)>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response) || !response.TryGetProperty("publishedfiledetails", out var list)
            || list.ValueKind != JsonValueKind.Array) return verdicts;
        foreach (var e in list.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("publishedfileid", out var pid)) continue;
            var idText = pid.ValueKind == JsonValueKind.String ? pid.GetString() : pid.GetRawText();
            if (!long.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) continue;
            static int Int(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
            var banned = e.TryGetProperty("banned", out var b) && (b.ValueKind == JsonValueKind.True || (b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var bn) && bn != 0));
            verdicts.Add((id, Verdict(Int(e, "result"), Int(e, "visibility"), banned)));
        }
        return verdicts;
    }
}
