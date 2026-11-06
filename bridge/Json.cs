// What the readers of the data files share: a value as text or as a number, a whole field of an object, the version every file states, and
// a list entry's key - a number, a string, or an object carrying "id" - which every list of maps (broken.json's, blocked.json, ours.json) is
// made of.
using System.Globalization;
using System.Text.Json;

static partial class Program
{
    /// <summary>A string's value, or any other value as JSON writes it, trimmed.</summary>
    static string JsonText(JsonElement e) => ((e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()) ?? "").Trim();

    /// <summary>A number, or a string that holds one; null for anything else.</summary>
    static double? JsonNumber(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) ? d
        : e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;

    /// <summary>An object's field as a whole number, 0 when it is missing or no number.</summary>
    static long Whole(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && JsonNumber(v) is { } d ? (long)d : 0;

    /// <summary>A file's root must be an object, and when it states a version, version 1; anything else throws to the caller.</summary>
    static void NeedVersion1(JsonElement root, string what)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("not an object");
        if (root.TryGetProperty("version", out var v) && JsonNumber(v) is not 1) throw new InvalidDataException($"not version 1 of {what}");
    }

    /// <summary>A list entry's key as text: a number or a string as it stands, an object's "id" (a number or a string) - or, orName, an object's
    /// "name" when it has no such id (an official map goes by its name). "" for anything else.</summary>
    static string EntryKey(JsonElement e, bool orName = false)
    {
        var v = e.ValueKind != JsonValueKind.Object ? e
            : e.TryGetProperty("id", out var i) && i.ValueKind is JsonValueKind.Number or JsonValueKind.String ? i
            : orName && e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n : default;
        return v.ValueKind is JsonValueKind.Number or JsonValueKind.String ? JsonText(v) : "";
    }

    /// <summary>A list entry's Workshop id: its key when that is a positive whole number, else null.</summary>
    static long? EntryId(JsonElement e) => long.TryParse(EntryKey(e), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    /// <summary>A list entry's field as text, "" when the entry is no object or the field no string.</summary>
    static string EntryText(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? JsonText(v) : "";
}
