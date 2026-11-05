// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Globalization;
using System.Text.Json;

/// <summary>One item as the Web API describes it (GetPublishedFileDetails), with what the card needs.</summary>
public sealed record WorkshopItem(long Id, int Result, int Visibility, bool Banned, long AppId, string Title, long Bytes, IReadOnlyList<string> Tags)
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
                Hud.Clean(e.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String ? ti.GetString() : "", 60), Long(e, "file_size"), tags);
        }
        return items;
    }

    /// <summary>A number the API sends as a number or as a string (file_size and publishedfileid come as strings).</summary>
    static long Long(JsonElement o, string name) =>
        !o.TryGetProperty(name, out var v) ? 0
        : v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n
        : v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
}
