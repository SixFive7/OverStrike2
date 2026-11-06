using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>A value of a data file that is not what its reader takes. Nothing is published or assumed on it.</summary>
public sealed class BadDataException(string message) : Exception(message);

/// <summary>The values of the sync's JSON files and of Steam's answers, read the way the sync's files have always held them: a missing key
/// or a value of another kind reads as none, a whole number as a long wherever it is written as one, Steam's numbers that come as text
/// (file_size) as numbers; and the sync's JSON, written as System.Text.Json writes it.</summary>
public static class Node
{
    // ------------------------------------------------------------------------------------------------ reading values

    public static JsonNode? Get(JsonNode? obj, string key) => obj is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    public static JsonObject Obj(JsonNode? v) => v as JsonObject ?? [];

    public static JsonArray Arr(JsonNode? v) => v as JsonArray ?? [];

    public static bool IsString(JsonNode? v) => v is JsonValue jv && jv.GetValueKind() == JsonValueKind.String;

    public static bool IsNumber(JsonNode? v) => v is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number;

    public static bool IsBool(JsonNode? v) => v is JsonValue jv && jv.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    /// <summary>A whole JSON number, else null (5.0 too: the files write whole numbers whole).</summary>
    public static long? Integer(JsonNode? v) => KeepSet.Integer(v);

    static string? StringOf(JsonNode? v) => IsString(v) ? v!.GetValue<string>() : null;

    /// <summary>Whether a value is set: null, false, 0, "", [] and {} are not.</summary>
    public static bool Truthy(JsonNode? v) => v switch
    {
        null => false,
        JsonObject o => o.Count > 0,
        JsonArray a => a.Count > 0,
        JsonValue jv => jv.GetValueKind() switch
        {
            JsonValueKind.String => jv.GetValue<string>().Length > 0,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => (KeepSet.Number(jv) ?? 0) != 0,
            _ => false,
        },
        _ => true,
    };

    /// <summary>A value as a whole number: an unset one is 0, a number is cut toward zero, true is 1, the text of a whole number is read
    /// (whitespace around it allowed). Anything else is 0.</summary>
    public static long Int(JsonNode? v)
    {
        if (!Truthy(v))
            return 0;
        if (IsBool(v))
            return 1;
        if (KeepSet.Integer(v) is { } n)
            return n;
        if (KeepSet.Number(v) is { } d)
            return double.IsFinite(d) ? (long)Math.Truncate(d) : 0;
        if (StringOf(v) is { } s && long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var p))
            return p;
        return 0;
    }

    /// <summary>A value as a number: an unset one is 0, true is 1, the text of a number is read. Anything else is 0.</summary>
    public static double Float(JsonNode? v)
    {
        if (!Truthy(v))
            return 0;
        if (IsBool(v))
            return 1;
        if (KeepSet.Number(v) is { } d)
            return d;
        if (StringOf(v) is { } s && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            return p;
        return 0;
    }

    /// <summary>A value as text: a string as it is, a number as JSON writes it, True or False, None for null, anything else as JSON.</summary>
    public static string Str(JsonNode? v)
    {
        if (v is null)
            return "None";
        if (v is JsonValue jv)
        {
            switch (jv.GetValueKind())
            {
                case JsonValueKind.String:
                    return jv.GetValue<string>();
                case JsonValueKind.True:
                    return "True";
                case JsonValueKind.False:
                    return "False";
                case JsonValueKind.Number:
                    return jv.ToJsonString();
            }
        }
        return Compact(v);
    }

    /// <summary>A value as text, or the fallback ("" unless given) when it is not set.</summary>
    public static string StrOr(JsonNode? v, string fallback = "") => Truthy(v) ? Str(v) : fallback;

    /// <summary>Two values equal as values: objects whatever their keys' order, numbers by value (1 is 1.0, true is 1).</summary>
    public static bool Eq(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a is JsonObject oa)
        {
            if (b is not JsonObject ob || oa.Count != ob.Count)
                return false;
            foreach (var (k, va) in oa)
                if (!ob.TryGetPropertyValue(k, out var vb) || !Eq(va, vb))
                    return false;
            return true;
        }
        if (a is JsonArray aa)
        {
            if (b is not JsonArray ab || aa.Count != ab.Count)
                return false;
            for (var i = 0; i < aa.Count; i++)
                if (!Eq(aa[i], ab[i]))
                    return false;
            return true;
        }
        if (b is JsonObject or JsonArray)
            return false;
        if (NumLike(a) is { } na && NumLike(b) is { } nb)
            return na == nb;
        if (IsString(a) && IsString(b))
            return a.GetValue<string>() == b.GetValue<string>();
        return false;
    }

    static double? NumLike(JsonNode v) => IsBool(v) ? (v.GetValue<bool>() ? 1 : 0) : KeepSet.Number(v);

    /// <summary>A key that is the same for two values Eq calls equal (1 and 1.0 alike), for telling values apart in a set.</summary>
    public static string SetKey(JsonNode? v) => v switch
    {
        null => "None",
        JsonValue when NumLike(v) is { } d => "n:" + d.ToString("R", CultureInfo.InvariantCulture),
        JsonValue when IsString(v) => "s:" + v.GetValue<string>(),
        _ => "j:" + Compact(v),
    };

    /// <summary>A JSON array of strings.</summary>
    public static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    /// <summary>A JSON array of these values, each cloned (a node has one parent).</summary>
    public static JsonArray Array(params JsonNode?[] items) => new(items.Select(v => v?.DeepClone()).ToArray());

    /// <summary>The texts (Str) of a JSON array's values; [] for anything else.</summary>
    public static List<string> StrList(JsonNode? v) => v is JsonArray a ? a.Select(Str).ToList() : [];

    /// <summary>Whether a JSON array holds this string.</summary>
    public static bool In(JsonNode? list, string s) => list is JsonArray a && a.Any(e => IsString(e) && e!.GetValue<string>() == s);

    /// <summary>A data file's bytes parsed (UTF-8, strict): BadDataException when it is no JSON.</summary>
    public static JsonNode? Parse(byte[] raw, string what)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(raw);
        }
        catch (DecoderFallbackException e)
        {
            throw new BadDataException($"{what}: not UTF-8 ({e.Message})");
        }
        return Parse(text, what);
    }

    public static JsonNode? Parse(string text, string what)
    {
        try
        {
            return JsonNode.Parse(text.StartsWith((char)0xFEFF) ? text[1..] : text);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new BadDataException($"{what}: {e.Message}");
        }
    }

    /// <summary>A JSON file, or null when it is missing or does not parse.</summary>
    public static JsonNode? ReadJson(string path)
    {
        try
        {
            return JsonNode.Parse(System.IO.File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException
                                      or DecoderFallbackException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------------ writing JSON

    // text as it is - names in every script stay readable in a file and in a diff - but for what JSON must escape; the encoder escapes a few
    // more (a character beyond the BMP, a C1 control, a line separator), which every reader takes back as the same text
    static readonly JavaScriptEncoder Escapes = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    static readonly JsonSerializerOptions OneLine = new() { Encoder = Escapes };
    static readonly JsonSerializerOptions[] Indents = [.. Enumerable.Range(0, 3).Select(n => new JsonSerializerOptions { Encoder = Escapes, WriteIndented = true, IndentSize = n, NewLine = "\n" })];

    /// <summary>A value's JSON on one line, without spaces: the sync's own files, its log lines and its words.</summary>
    public static string Compact(JsonNode? node) => node?.ToJsonString(OneLine) ?? "null";

    /// <summary>A value's JSON with each member and each item on a line of its own, `indent` (0 to 2) spaces a level.</summary>
    public static string Indented(JsonNode? node, int indent) => node?.ToJsonString(Indents[indent]) ?? "null";

    /// <summary>A file of the bridge's data: Indented and a final newline. Each list keeps the indent it has always had - maps.json and the mode
    /// lists 1, broken.json 2, search.json 0 - so a diff of one shows the maps that changed.</summary>
    public static string File(JsonNode? node, int indent) => Indented(node, indent) + "\n";
}
