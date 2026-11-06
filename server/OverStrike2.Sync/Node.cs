using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>A value of a data file that is not what its reader takes. Nothing is published or assumed on it.</summary>
public sealed class BadDataException(string message) : Exception(message);

/// <summary>The Python sync's semantics, where the port has to keep them exactly - the files it wrote are read by this program now, and
/// the files the bridge reads are compared byte for byte: truthiness and int() and str() of JSON values, Python's float repr, round() and
/// "%.Nf" (correctly rounded, ties to even, on the exact binary value), code-point lengths and slices, and json.dumps with ensure_ascii
/// off (indent 0, 1, 2 or compact).</summary>
public static class Node
{
    // ------------------------------------------------------------------------------------------------ JSON values as Python reads them

    public static JsonNode? Get(JsonNode? obj, string key) => obj is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    public static bool Has(JsonNode? obj, string key) => obj is JsonObject o && o.ContainsKey(key);

    public static JsonObject Obj(JsonNode? v) => v as JsonObject ?? [];

    public static JsonArray Arr(JsonNode? v) => v as JsonArray ?? [];

    public static bool IsString(JsonNode? v) => v is JsonValue jv && jv.GetValueKind() == JsonValueKind.String;

    public static bool IsNumber(JsonNode? v) => v is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number;

    public static bool IsBool(JsonNode? v) => v is JsonValue jv && jv.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    /// <summary>A whole JSON number (as Python's int is), else null.</summary>
    public static long? Integer(JsonNode? v) => KeepSet.Integer(v);

    /// <summary>A JSON string's text, else null.</summary>
    public static string? Text(JsonNode? v) => IsString(v) ? v!.GetValue<string>() : null;

    /// <summary>Python's bool(x) of a JSON value: null, false, 0, "", [] and {} are false.</summary>
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

    /// <summary>Python's int(x or 0): a falsy value is 0, a number is cut toward zero, true is 1, a string of a whole number is read
    /// (whitespace around it allowed). Anything else (where Python would raise) is 0.</summary>
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
        if (Text(v) is { } s && long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var p))
            return p;
        return 0;
    }

    /// <summary>Python's float(x or 0), 0 where Python would raise.</summary>
    public static double Float(JsonNode? v)
    {
        if (!Truthy(v))
            return 0;
        if (IsBool(v))
            return 1;
        if (KeepSet.Number(v) is { } d)
            return d;
        if (Text(v) is { } s && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            return p;
        return 0;
    }

    /// <summary>Python's str(x): a string as it is, a whole number in digits, a float as repr, True/False, None.</summary>
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

    /// <summary>Python's str(x or ""): "" for a falsy value.</summary>
    public static string StrOr(JsonNode? v, string fallback = "") => Truthy(v) ? Str(v) : fallback;

    /// <summary>A string of ASCII digits (Python's isdigit, as the sync uses it for ids).</summary>
    public static bool Digits(string? s) => !string.IsNullOrEmpty(s) && s.All(char.IsAsciiDigit);

    /// <summary>Python's == of two JSON values: objects regardless of their keys' order, numbers by value (1 == 1.0, True == 1).</summary>
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

    /// <summary>A key for a set of JSON values the way Python's set() tells them apart (1 and 1.0 alike).</summary>
    public static string SetKey(JsonNode? v) => v switch
    {
        null => "None",
        JsonValue when NumLike(v) is { } d => "n:" + d.ToString("R", CultureInfo.InvariantCulture),
        JsonValue when IsString(v) => "s:" + v.GetValue<string>(),
        _ => "j:" + Dumps(v, null, ",", ":"),
    };

    public static JsonNode? Clone(JsonNode? v) => v?.DeepClone();

    /// <summary>A JSON array of strings.</summary>
    public static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    /// <summary>A JSON array of these values, each cloned (a node has one parent).</summary>
    public static JsonArray Array(params JsonNode?[] items) => new(items.Select(Clone).ToArray());

    /// <summary>The strings of a JSON array (Python's [str(x) for x in ...]); [] for anything else.</summary>
    public static List<string> StrList(JsonNode? v) => v is JsonArray a ? a.Select(Str).ToList() : [];

    /// <summary>Python's `x in list` of a string in a JSON list.</summary>
    public static bool In(JsonNode? list, string s) => list is JsonArray a && a.Any(e => IsString(e) && e!.GetValue<string>() == s);

    /// <summary>The bridge's file contents parsed (UTF-8, strict): BadDataException when it is no JSON.</summary>
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
            return JsonNode.Parse(text.StartsWith('﻿') ? text[1..] : text);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new BadDataException($"{what}: {e.Message}");
        }
    }

    /// <summary>Python's read_json(path, default): the file's JSON, or null when it is missing or does not parse.</summary>
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

    /// <summary>Python's "%.Nf" % x: the exact binary value, correctly rounded to N decimals, ties to even.</summary>
    public static string Fixed(double x, int decimals)
    {
        if (double.IsNaN(x))
            return "nan";
        if (double.IsInfinity(x))
            return x > 0 ? "inf" : "-inf";
        var negative = double.IsNegative(x);
        var bits = BitConverter.DoubleToInt64Bits(Math.Abs(x));
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0)
            exponent = 1;                            // subnormal
        else
            mantissa |= 1L << 52;
        exponent -= 1075;                            // x = mantissa * 2^exponent
        var scaled = new BigInteger(mantissa) * BigInteger.Pow(10, decimals);
        BigInteger q;
        if (exponent >= 0)
            q = scaled << exponent;
        else
        {
            q = scaled >> -exponent;
            var rest = scaled - (q << -exponent);
            var half = BigInteger.One << (-exponent - 1);
            if (rest > half || (rest == half && !q.IsEven))
                q += 1;
        }
        var digits = q.ToString(CultureInfo.InvariantCulture).PadLeft(decimals + 1, '0');
        var text = decimals == 0 ? digits : digits[..^decimals] + "." + digits[^decimals..];
        return (negative ? "-" : "") + text;          // Python keeps the sign of a negative that rounds to zero: "-0.0"
    }

    /// <summary>Python's round(x, n): correctly rounded, ties to even.</summary>
    public static double Round(double x, int decimals) =>
        double.IsFinite(x) ? double.Parse(Fixed(x, decimals), CultureInfo.InvariantCulture) : x;

    /// <summary>Python's "%d" % x of a number: cut toward zero.</summary>
    public static string D(double x) => ((long)Math.Truncate(x)).ToString(CultureInfo.InvariantCulture);

    public static string D(long x) => x.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------------------------------------ text by code points, as Python counts

    static int[] Points(string s)
    {
        var list = new List<int>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                list.Add(char.ConvertToUtf32(s[i], s[i + 1]));
                i++;
            }
            else
                list.Add(s[i]);
        }
        return list.ToArray();
    }

    static string FromPoints(ReadOnlySpan<int> points)
    {
        var sb = new StringBuilder(points.Length);
        foreach (var p in points)
        {
            if (p >= 0x10000)
                sb.Append(char.ConvertFromUtf32(p));
            else
                sb.Append((char)p);
        }
        return sb.ToString();
    }

    /// <summary>Python's len(s): code points.</summary>
    public static int Len(string s)
    {
        var n = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                i++;
            n++;
        }
        return n;
    }

    /// <summary>Python's s[:n].</summary>
    public static string Cut(string s, int n)
    {
        if (n >= s.Length && Len(s) <= n)
            return s;
        var p = Points(s);
        return n >= p.Length ? s : FromPoints(p.AsSpan(0, Math.Max(0, n)));
    }

    /// <summary>Python's s[start:end] (end null: to the end; negative counts from the end).</summary>
    public static string Slice(string s, int start, int? end = null)
    {
        var p = Points(s);
        int Fix(int i) => i < 0 ? Math.Max(0, p.Length + i) : Math.Min(i, p.Length);
        var a = Fix(start);
        var b = end is { } e ? Fix(e) : p.Length;
        return b <= a ? "" : FromPoints(p.AsSpan(a, b - a));
    }

    /// <summary>Python's s.rfind(sub) in code points.</summary>
    public static int RFind(string s, string sub)
    {
        var i = s.LastIndexOf(sub, StringComparison.Ordinal);
        return i < 0 ? -1 : Len(s[..i]);
    }

    /// <summary>Python's str.split() without arguments: runs of whitespace, no empty parts.</summary>
    public static string[] Words(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Python's "%-8s" % s.</summary>
    public static string LeftPad(string s, int width) => Len(s) >= width ? s : s + new string(' ', width - Len(s));

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

    /// <summary>Text as UTF-8 bytes, a lone surrogate replaced (Python would refuse to encode it).</summary>
    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
