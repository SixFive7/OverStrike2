using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OverStrike2.Deploy;

/// <summary>A settings text or a JSON document that does not parse. Its words quote nothing of the text, which may hold secrets.</summary>
sealed class JsonSyntaxError(string message) : Exception(message);

/// <summary>JSON as the Python scripts handled it: parsed with the last of two equal keys winning, written as json.dumps writes it, read
/// with comments and trailing commas as check.py's jsonc did, compared and printed the Python way.</summary>
static class Json
{
    static readonly JsonDocumentOptions Strict = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 256 };

    /// <summary>json.loads: a JSON text as nodes; null for the literal null. JsonSyntaxError when it is not JSON.</summary>
    public static JsonNode? Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, Strict);
            return From(doc.RootElement);
        }
        catch (JsonException)
        {
            throw new JsonSyntaxError("not valid JSON");
        }
    }

    public static JsonNode? Parse(byte[] utf8) => Parse(Encoding.UTF8.GetString(utf8));

    /// <summary>Nodes from an element, an object's later key replacing an earlier one in its place, as Python's dict does.</summary>
    static JsonNode? From(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var o = new JsonObject();
                foreach (var p in e.EnumerateObject())
                    o[p.Name] = From(p.Value);
                return o;
            case JsonValueKind.Array:
                var a = new JsonArray();
                foreach (var x in e.EnumerateArray())
                    a.Add(From(x));
                return a;
            case JsonValueKind.String:
                return JsonValue.Create(e.GetString());
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            case JsonValueKind.Number:
                var raw = e.GetRawText();
                if (raw.IndexOfAny(['.', 'e', 'E']) < 0)
                {
                    if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                        return JsonValue.Create(l);
                    return JsonValue.Create(BigInteger.Parse(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                }
                return JsonValue.Create(double.Parse(raw, CultureInfo.InvariantCulture));
            default:
                return null;
        }
    }

    /// <summary>check.py's jsonc: the settings text as values - JSON with // and /* */ comments and trailing commas, what OverStrike2's
    /// reader takes. A syntax error's words quote nothing of the text.</summary>
    public static JsonNode? Jsonc(string text)
    {
        var output = new List<string>();
        int i = 0, n = text.Length;
        while (i < n)
        {
            var c = text[i];
            if (c == '"')
            {
                var j = i + 1;
                while (j < n && text[j] != '"')
                    j += text[j] == '\\' ? 2 : 1;
                output.Add(Py.Slice(text, i, j + 1));
                i = j + 1;
            }
            else if (string.CompareOrdinal(text, i, "//", 0, 2) == 0)
            {
                var j = text.IndexOf('\n', i);
                i = j < 0 ? n : j;
            }
            else if (string.CompareOrdinal(text, i, "/*", 0, 2) == 0)
            {
                var j = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = j < 0 ? n : j + 2;
            }
            else
            {
                if (c is '}' or ']')
                {
                    var k = output.Count - 1;
                    while (k >= 0 && Py.IsSpace(output[k]))
                        k--;
                    if (k >= 0 && output[k] == ",")
                        output.RemoveAt(k);
                }
                output.Add(c.ToString());
                i++;
            }
        }
        return Parse(string.Concat(output));
    }

    // ------------------------------------------------------------------------------------------------------------ writing

    /// <summary>json.dumps(node) - ", " and ": " between items, every non-ASCII character escaped - or with indent the way json.dumps(...,
    /// indent=n) lays it out.</summary>
    public static string Dumps(JsonNode? node, int? indent = null)
    {
        var b = new StringBuilder();
        Write(b, node, indent, 0);
        return b.ToString();
    }

    static void Write(StringBuilder b, JsonNode? node, int? indent, int level)
    {
        switch (node)
        {
            case null:
                b.Append("null");
                return;
            case JsonObject o:
                if (o.Count == 0)
                {
                    b.Append("{}");
                    return;
                }
                b.Append('{');
                var first = true;
                foreach (var (k, v) in o)
                {
                    if (!first)
                        b.Append(indent is null ? ", " : ",");
                    first = false;
                    if (indent is not null)
                        b.Append('\n').Append(' ', indent.Value * (level + 1));
                    WriteString(b, k);
                    b.Append(": ");
                    Write(b, v, indent, level + 1);
                }
                if (indent is not null)
                    b.Append('\n').Append(' ', indent.Value * level);
                b.Append('}');
                return;
            case JsonArray a:
                if (a.Count == 0)
                {
                    b.Append("[]");
                    return;
                }
                b.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0)
                        b.Append(indent is null ? ", " : ",");
                    if (indent is not null)
                        b.Append('\n').Append(' ', indent.Value * (level + 1));
                    Write(b, a[i], indent, level + 1);
                }
                if (indent is not null)
                    b.Append('\n').Append(' ', indent.Value * level);
                b.Append(']');
                return;
            case JsonValue v:
                WriteValue(b, v);
                return;
        }
    }

    static void WriteValue(StringBuilder b, JsonValue v)
    {
        if (v.TryGetValue<string>(out var s))
        {
            WriteString(b, s);
            return;
        }
        if (v.TryGetValue<bool>(out var t))
        {
            b.Append(t ? "true" : "false");
            return;
        }
        if (v.TryGetValue<long>(out var l))
        {
            b.Append(l.ToString(CultureInfo.InvariantCulture));
            return;
        }
        if (v.TryGetValue<int>(out var i))
        {
            b.Append(i.ToString(CultureInfo.InvariantCulture));
            return;
        }
        if (v.TryGetValue<double>(out var d))
        {
            b.Append(double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "Infinity" : double.IsNegativeInfinity(d) ? "-Infinity" : Py.Float(d));
            return;
        }
        var e = v.GetValue<JsonElement>();
        b.Append(e.GetRawText());
    }

    static void WriteString(StringBuilder b, string s)
    {
        b.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                case '\b': b.Append("\\b"); break;
                case '\f': b.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7e)
                        b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        b.Append(c);
                    break;
            }
        }
        b.Append('"');
    }

    // ---------------------------------------------------------------------------------------------------------- reading values

    /// <summary>node[key] when node is an object; else null.</summary>
    public static JsonNode? At(JsonNode? node, string key) => node is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    /// <summary>node[key...] down a path of keys, null where it stops.</summary>
    public static JsonNode? At(JsonNode? node, params string[] keys)
    {
        foreach (var k in keys)
            node = At(node, k);
        return node;
    }

    /// <summary>(node.get(key) or {}): the object there, or an empty one.</summary>
    public static JsonObject Obj(JsonNode? node, params string[] keys) => At(node, keys) as JsonObject ?? [];

    /// <summary>(node.get(key) or []): the array there, or an empty one.</summary>
    public static JsonArray Arr(JsonNode? node, params string[] keys) => At(node, keys) as JsonArray ?? [];

    /// <summary>The string at a path, or null when it is missing or no string.</summary>
    public static string? Str(JsonNode? node, params string[] keys) =>
        At(node, keys) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A string value's text, else null.</summary>
    public static string? AsStr(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool? AsBool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static long? AsLong(JsonNode? node)
    {
        if (node is not JsonValue v)
            return null;
        if (v.TryGetValue<long>(out var l))
            return l;
        if (v.TryGetValue<int>(out var i))
            return i;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d))
            return (long)d;
        return null;
    }

    /// <summary>Python's truth of a JSON value: None, "", 0, False, {} and [] are false.</summary>
    public static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonObject o => o.Count > 0,
        JsonArray a => a.Count > 0,
        JsonValue v => v.TryGetValue<string>(out var s) ? s.Length > 0
            : v.TryGetValue<bool>(out var b) ? b
            : AsLong(v) is long l ? l != 0
            : !(v.TryGetValue<double>(out var d) && d == 0),
        _ => true,
    };

    /// <summary>x in (None, "", {}, [], False) - which in Python also holds for 0, since 0 == False.</summary>
    public static bool Emptyish(JsonNode? node) => !Truthy(node);

    /// <summary>a == b for JSON values, as Python compares what json.loads made.</summary>
    public static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    public static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    /// <summary>str() of a JSON value: a string as itself, None, True, False, a number as Python prints it, a dict or list as its repr.</summary>
    public static string PyStr(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : PyRepr(node);

    /// <summary>repr() of a JSON value.</summary>
    public static string PyRepr(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "None";
            case JsonObject o:
                return "{" + string.Join(", ", o.Select(p => Py.ReprStr(p.Key) + ": " + PyRepr(p.Value))) + "}";
            case JsonArray a:
                return "[" + string.Join(", ", a.Select(PyRepr)) + "]";
            case JsonValue v:
                if (v.TryGetValue<string>(out var s))
                    return Py.ReprStr(s);
                if (v.TryGetValue<bool>(out var b))
                    return b ? "True" : "False";
                if (AsLong(v) is long l && !(v.TryGetValue<double>(out _) && !v.TryGetValue<long>(out _)))
                    return l.ToString(CultureInfo.InvariantCulture);
                if (v.TryGetValue<double>(out var d))
                    return Py.Float(d);
                return v.ToJsonString();
        }
        return node.ToJsonString();
    }

    /// <summary>A node from a JSON literal, for building request bodies and the self-tests' fixtures.</summary>
    public static JsonObject O(string json) => (JsonObject)Parse(json)!;
}
