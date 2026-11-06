using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>A value of a data file that is not what its reader takes. Nothing is published or assumed on it.</summary>
public sealed class BadDataException(string message) : Exception(message);

/// <summary>The values of the sync's JSON files and of Steam's answers, read the way the sync's files have always held them: a missing key
/// or a value of another kind reads as none, a whole number as a long wherever it is written as one, Steam's numbers that come as text
/// (file_size) as numbers; and the JSON the sync writes.</summary>
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

    /// <summary>A value as text: a string as it is, a number in its digits, True or False, None for null, anything else as JSON.</summary>
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
                    return NumberText(jv);
            }
        }
        return Dumps(v, null, ", ", ": ");
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
        _ => "j:" + Dumps(v, null, ",", ":"),
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
            return JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException
                                      or DecoderFallbackException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------------ numbers as Python writes them

    static string NumberText(JsonValue jv)
    {
        if (jv.TryGetValue<JsonElement>(out var el))
        {
            var raw = el.GetRawText();
            if (raw.IndexOfAny(['.', 'e', 'E']) < 0)
                return raw.StartsWith("-0", StringComparison.Ordinal) && raw.Trim('-', '0').Length == 0 ? "0" : raw;
            return Repr(double.Parse(raw, CultureInfo.InvariantCulture));
        }
        if (jv.TryGetValue<long>(out var l))
            return l.ToString(CultureInfo.InvariantCulture);
        if (jv.TryGetValue<int>(out var i))
            return i.ToString(CultureInfo.InvariantCulture);
        if (jv.TryGetValue<ulong>(out var u))
            return u.ToString(CultureInfo.InvariantCulture);
        if (jv.TryGetValue<double>(out var d))
            return Repr(d);
        if (jv.TryGetValue<float>(out var f))
            return Repr(f);
        if (jv.TryGetValue<decimal>(out var m))
            return Repr((double)m);
        return jv.ToJsonString();
    }

    /// <summary>Python's repr(float): the shortest digits that read back, fixed notation from 1e-4 to below 1e16 (always with a point),
    /// else 1.5e-07, 1e+16.</summary>
    public static string Repr(double d)
    {
        if (double.IsNaN(d))
            return "NaN";
        if (double.IsInfinity(d))
            return d > 0 ? "Infinity" : "-Infinity";
        if (d == 0)
            return double.IsNegative(d) ? "-0.0" : "0.0";
        var r = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
        string digits;
        int point;                                   // the value is 0.<digits> * 10^point
        var e = r.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            var mant = r[..e];
            var exp = int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var dot = mant.IndexOf('.');
            digits = mant.Replace(".", "");
            point = (dot < 0 ? mant.Length : dot) + exp;
        }
        else
        {
            var dot = r.IndexOf('.');
            var intPart = dot < 0 ? r : r[..dot];
            var frac = dot < 0 ? "" : r[(dot + 1)..];
            digits = intPart + frac;
            point = intPart.Length;
        }
        var lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0');
        point -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
            digits = "0";
        var sb = new StringBuilder();
        if (d < 0)
            sb.Append('-');
        var decExp = point - 1;
        if (decExp < -4 || decExp >= 16)
        {
            sb.Append(digits[0]);
            if (digits.Length > 1)
                sb.Append('.').Append(digits, 1, digits.Length - 1);
            sb.Append('e').Append(decExp < 0 ? '-' : '+').Append(Math.Abs(decExp).ToString("00", CultureInfo.InvariantCulture));
        }
        else if (point <= 0)
            sb.Append("0.").Append('0', -point).Append(digits);
        else if (point >= digits.Length)
            sb.Append(digits).Append('0', point - digits.Length).Append(".0");
        else
            sb.Append(digits, 0, point).Append('.').Append(digits, point, digits.Length - point);
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------------ json.dumps

    /// <summary>json.dumps(obj, ensure_ascii=False, indent=indent) - indent null with separators (", ", ": ") as json.dumps' default, or the
    /// compact (",", ":") the sync's own files use.</summary>
    public static string Dumps(JsonNode? node, int? indent = null, string itemSep = ", ", string keySep = ": ")
    {
        var sb = new StringBuilder();
        if (indent is not null)
            itemSep = ",";
        Write(sb, node, indent, itemSep, keySep, 0);
        return sb.ToString();
    }

    /// <summary>json.dumps(obj, separators=(",", ":"), ensure_ascii=False).</summary>
    public static string Compact(JsonNode? node) => Dumps(node, null, ",", ":");

    /// <summary>The sync's dumps(obj, indent): json.dumps(obj, indent=indent, ensure_ascii=False) and a final newline.</summary>
    public static string DumpsFile(JsonNode? node, int indent = 1) => Dumps(node, indent) + "\n";

    static void Write(StringBuilder sb, JsonNode? node, int? indent, string itemSep, string keySep, int depth)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject o:
                if (o.Count == 0)
                {
                    sb.Append("{}");
                    return;
                }
                sb.Append('{');
                var first = true;
                foreach (var (k, v) in o)
                {
                    if (!first)
                        sb.Append(itemSep);
                    first = false;
                    NewLine(sb, indent, depth + 1);
                    Quote(sb, k);
                    sb.Append(keySep);
                    Write(sb, v, indent, itemSep, keySep, depth + 1);
                }
                NewLine(sb, indent, depth);
                sb.Append('}');
                return;
            case JsonArray a:
                if (a.Count == 0)
                {
                    sb.Append("[]");
                    return;
                }
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0)
                        sb.Append(itemSep);
                    NewLine(sb, indent, depth + 1);
                    Write(sb, a[i], indent, itemSep, keySep, depth + 1);
                }
                NewLine(sb, indent, depth);
                sb.Append(']');
                return;
            case JsonValue jv:
                switch (jv.GetValueKind())
                {
                    case JsonValueKind.String:
                        Quote(sb, jv.GetValue<string>());
                        return;
                    case JsonValueKind.True:
                        sb.Append("true");
                        return;
                    case JsonValueKind.False:
                        sb.Append("false");
                        return;
                    case JsonValueKind.Number:
                        sb.Append(NumberText(jv));
                        return;
                    default:
                        sb.Append("null");
                        return;
                }
        }
    }

    static void NewLine(StringBuilder sb, int? indent, int depth)
    {
        if (indent is { } n)
            sb.Append('\n').Append(' ', n * depth);
    }

    static void Quote(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
