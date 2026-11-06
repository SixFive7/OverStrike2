// What the readers of the data files share: a value as text or as a number, a whole field of an object, and the version every file states.
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
}
