using System.Globalization;
using System.Text;

namespace OverStrike2.Deploy;

/// <summary>A TOML text that does not parse: the line, and what was wrong.</summary>
sealed class TomlError(string message) : Exception(message);

/// <summary>A TOML 1.0 reader, for the private config: tables, arrays of tables, dotted and quoted keys, every kind of string, integers in
/// any base, floats, booleans, arrays and inline tables. A table is a Dictionary&lt;string, object?&gt; in the order its keys were written,
/// an array a List&lt;object?&gt;, a string a string, an integer a long, a float a double; a date or time stays its text.</summary>
static class Toml
{
    public static Dictionary<string, object?> Parse(string text)
    {
        var p = new Reader(text.StartsWith((char)0xFEFF) ? text[1..] : text);
        return p.Document();
    }

    sealed class Reader(string s)
    {
        int i;
        int line = 1;
        readonly Dictionary<string, object?> root = new(StringComparer.Ordinal);
        readonly HashSet<object> defined = new(ReferenceEqualityComparer.Instance);   // tables made by a header: a second header for one is an error
        readonly HashSet<object> inlines = new(ReferenceEqualityComparer.Instance);   // inline tables and static arrays: closed once written

        TomlError Error(string what) => new($"TOML line {line}: {what}");

        char Peek(int ahead = 0) => i + ahead < s.Length ? s[i + ahead] : '\0';

        bool AtEnd => i >= s.Length;

        void SkipBlank()
        {
            while (!AtEnd && (s[i] == ' ' || s[i] == '\t'))
                i++;
        }

        void SkipComment()
        {
            if (Peek() != '#')
                return;
            while (!AtEnd && s[i] != '\n')
            {
                if (s[i] < 0x20 && s[i] != '\t' && s[i] != '\r')
                    throw Error("a control character in a comment");
                i++;
            }
        }

        /// <summary>The end of an expression: blanks, a comment, then a newline or the end.</summary>
        void EndOfLine()
        {
            SkipBlank();
            SkipComment();
            if (AtEnd)
                return;
            if (s[i] == '\r' && Peek(1) == '\n')
                i++;
            if (s[i] != '\n')
                throw Error($"unexpected '{s[i]}' after a value");
            i++;
            line++;
        }

        /// <summary>Blanks, comments and newlines, inside an array.</summary>
        void SkipSpaceAndComments()
        {
            while (!AtEnd)
            {
                SkipBlank();
                SkipComment();
                if (AtEnd)
                    return;
                if (s[i] == '\r' && Peek(1) == '\n')
                    i++;
                if (s[i] == '\n')
                {
                    i++;
                    line++;
                    continue;
                }
                return;
            }
        }

        public Dictionary<string, object?> Document()
        {
            var current = root;
            while (true)
            {
                SkipSpaceAndComments();
                if (AtEnd)
                    return root;
                if (s[i] == '[')
                {
                    var array = Peek(1) == '[';
                    i += array ? 2 : 1;
                    SkipBlank();
                    var keys = Key();
                    SkipBlank();
                    if (Peek() != ']' || (array && Peek(1) != ']'))
                        throw Error("a table header without its closing bracket");
                    i += array ? 2 : 1;
                    current = array ? ArrayTable(keys) : Table(keys);
                    EndOfLine();
                    continue;
                }
                KeyValue(current);
                EndOfLine();
            }
        }

        Dictionary<string, object?> Table(List<string> keys)
        {
            var t = root;
            for (var k = 0; k < keys.Count; k++)
            {
                var last = k == keys.Count - 1;
                if (!t.TryGetValue(keys[k], out var next))
                {
                    var made = new Dictionary<string, object?>(StringComparer.Ordinal);
                    t[keys[k]] = made;
                    t = made;
                }
                else if (next is Dictionary<string, object?> d && !inlines.Contains(d))
                {
                    if (last && defined.Contains(d))
                        throw Error($"the table [{string.Join('.', keys)}] is defined twice");
                    t = d;
                }
                else if (next is List<object?> list && !inlines.Contains(list) && !last && list.Count > 0 && list[^1] is Dictionary<string, object?> tail)
                {
                    t = tail;
                }
                else
                {
                    throw Error($"[{string.Join('.', keys)}] redefines a value");
                }
            }
            defined.Add(t);
            return t;
        }

        Dictionary<string, object?> ArrayTable(List<string> keys)
        {
            var parent = keys.Count == 1 ? root : Table(keys.GetRange(0, keys.Count - 1), define: false);
            var name = keys[^1];
            if (!parent.TryGetValue(name, out var existing))
            {
                existing = new List<object?>();
                parent[name] = existing;
            }
            if (existing is not List<object?> list || inlines.Contains(list))
                throw Error($"[[{string.Join('.', keys)}]] redefines a value");
            var t = new Dictionary<string, object?>(StringComparer.Ordinal);
            list.Add(t);
            defined.Add(t);
            return t;
        }

        Dictionary<string, object?> Table(List<string> keys, bool define)
        {
            if (define)
                return Table(keys);
            var t = root;
            foreach (var key in keys)
            {
                if (!t.TryGetValue(key, out var next))
                {
                    var made = new Dictionary<string, object?>(StringComparer.Ordinal);
                    t[key] = made;
                    t = made;
                }
                else if (next is Dictionary<string, object?> d && !inlines.Contains(d))
                    t = d;
                else if (next is List<object?> list && !inlines.Contains(list) && list.Count > 0 && list[^1] is Dictionary<string, object?> tail)
                    t = tail;
                else
                    throw Error($"[{string.Join('.', keys)}] redefines a value");
            }
            return t;
        }

        void KeyValue(Dictionary<string, object?> table)
        {
            var keys = Key();
            SkipBlank();
            if (Peek() != '=')
                throw Error("a key without '='");
            i++;
            SkipBlank();
            var value = Value();
            var t = table;
            for (var k = 0; k < keys.Count - 1; k++)
            {
                if (!t.TryGetValue(keys[k], out var next))
                {
                    var made = new Dictionary<string, object?>(StringComparer.Ordinal);
                    t[keys[k]] = made;
                    t = made;
                }
                else if (next is Dictionary<string, object?> d && !inlines.Contains(d) && !defined.Contains(d))
                    t = d;
                else
                    throw Error($"'{string.Join('.', keys)}' redefines a value");
            }
            if (t.ContainsKey(keys[^1]))
                throw Error($"'{string.Join('.', keys)}' is defined twice");
            t[keys[^1]] = value;
        }

        List<string> Key()
        {
            var keys = new List<string>();
            while (true)
            {
                SkipBlank();
                var c = Peek();
                if (c == '"')
                    keys.Add(BasicString());
                else if (c == '\'')
                    keys.Add(LiteralString());
                else
                {
                    var start = i;
                    while (!AtEnd && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '-'))
                        i++;
                    if (i == start)
                        throw Error("a key is missing");
                    keys.Add(s[start..i]);
                }
                SkipBlank();
                if (Peek() != '.')
                    return keys;
                i++;
            }
        }

        object? Value()
        {
            var c = Peek();
            switch (c)
            {
                case '"':
                    return Peek(1) == '"' && Peek(2) == '"' ? MultiBasic() : BasicString();
                case '\'':
                    return Peek(1) == '\'' && Peek(2) == '\'' ? MultiLiteral() : LiteralString();
                case '[':
                    return Array();
                case '{':
                    return InlineTable();
            }
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0 && !IsBare(Peek(4)))
            {
                i += 4;
                return true;
            }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0 && !IsBare(Peek(5)))
            {
                i += 5;
                return false;
            }
            return Scalar();
        }

        static bool IsBare(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '+' or '.' or ':';

        object Scalar()
        {
            var start = i;
            while (!AtEnd && IsBare(s[i]))
                i++;
            // a local date-time with a space between the date and the time
            if (i - start == 10 && Peek() == ' ' && char.IsAsciiDigit(Peek(1)) && char.IsAsciiDigit(Peek(2)) && Peek(3) == ':')
            {
                i++;
                while (!AtEnd && IsBare(s[i]))
                    i++;
            }
            var token = s[start..i];
            if (token.Length == 0)
                throw Error("a value is missing");
            if (token.Length >= 8 && (char.IsAsciiDigit(token[0]) && (token.Length > 4 && token[4] == '-' || token.Length > 2 && token[2] == ':')))
                return token;   // a date, a time or a date-time: kept as its text
            var t = token.Replace("_", "", StringComparison.Ordinal);
            if (token.Contains("__", StringComparison.Ordinal) || token.StartsWith('_') || token.EndsWith('_'))
                throw Error($"a malformed number '{token}'");
            if (t is "inf" or "+inf")
                return double.PositiveInfinity;
            if (t == "-inf")
                return double.NegativeInfinity;
            if (t is "nan" or "+nan" or "-nan")
                return double.NaN;
            if (t.StartsWith("0x", StringComparison.Ordinal))
                return Convert.ToInt64(t[2..], 16);
            if (t.StartsWith("0o", StringComparison.Ordinal))
                return Convert.ToInt64(t[2..], 8);
            if (t.StartsWith("0b", StringComparison.Ordinal))
                return Convert.ToInt64(t[2..], 2);
            if (t.IndexOfAny(['.', 'e', 'E']) >= 0)
            {
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return d;
                throw Error($"a malformed float '{token}'");
            }
            var digits = t.TrimStart('+', '-');
            if (digits.Length > 1 && digits[0] == '0')
                throw Error($"an integer with a leading zero '{token}'");
            if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                return l;
            throw Error($"an unknown value '{token}'");
        }

        List<object?> Array()
        {
            i++;
            var list = new List<object?>();
            while (true)
            {
                SkipSpaceAndComments();
                if (Peek() == ']')
                {
                    i++;
                    inlines.Add(list);
                    return list;
                }
                if (AtEnd)
                    throw Error("an array without its closing bracket");
                list.Add(Value());
                SkipSpaceAndComments();
                if (Peek() == ',')
                {
                    i++;
                    continue;
                }
                if (Peek() == ']')
                    continue;
                throw Error("an array whose items are not separated by commas");
            }
        }

        Dictionary<string, object?> InlineTable()
        {
            i++;
            var t = new Dictionary<string, object?>(StringComparer.Ordinal);
            SkipBlank();
            if (Peek() == '}')
            {
                i++;
                inlines.Add(t);
                return t;
            }
            while (true)
            {
                SkipBlank();
                KeyValue(t);
                SkipBlank();
                if (Peek() == ',')
                {
                    i++;
                    continue;
                }
                if (Peek() == '}')
                {
                    i++;
                    inlines.Add(t);
                    return t;
                }
                throw Error("an inline table without its closing brace");
            }
        }

        string BasicString()
        {
            i++;
            var b = new StringBuilder();
            while (true)
            {
                if (AtEnd || s[i] == '\n')
                    throw Error("a string without its closing quote");
                var c = s[i++];
                if (c == '"')
                    return b.ToString();
                if (c == '\\')
                    Escape(b);
                else if (c < 0x20 && c != '\t' || c == 0x7f)
                    throw Error("a control character in a string");
                else
                    b.Append(c);
            }
        }

        string LiteralString()
        {
            i++;
            var start = i;
            while (!AtEnd && s[i] != '\'')
            {
                if (s[i] == '\n')
                    throw Error("a string without its closing quote");
                i++;
            }
            if (AtEnd)
                throw Error("a string without its closing quote");
            var text = s[start..i];
            i++;
            return text;
        }

        string MultiBasic()
        {
            i += 3;
            SkipFirstNewline();
            var b = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                    throw Error("a multi-line string without its closing quotes");
                if (s[i] == '"' && Peek(1) == '"' && Peek(2) == '"')
                {
                    // up to two quotes right before the closing three belong to the string
                    var extra = 0;
                    while (extra < 2 && Peek(3 + extra) == '"')
                        extra++;
                    b.Append('"', extra);
                    i += 3 + extra;
                    return b.ToString();
                }
                var c = s[i++];
                if (c == '\n')
                    line++;
                if (c == '\\')
                {
                    // a line-ending backslash eats the newline and the white space after it
                    var j = i;
                    while (j < s.Length && (s[j] == ' ' || s[j] == '\t'))
                        j++;
                    if (j < s.Length && (s[j] == '\n' || s[j] == '\r' && j + 1 < s.Length && s[j + 1] == '\n'))
                    {
                        i = j;
                        while (!AtEnd && (s[i] is ' ' or '\t' or '\r' or '\n'))
                        {
                            if (s[i] == '\n')
                                line++;
                            i++;
                        }
                        continue;
                    }
                    Escape(b);
                }
                else
                    b.Append(c);
            }
        }

        string MultiLiteral()
        {
            i += 3;
            SkipFirstNewline();
            var start = i;
            while (true)
            {
                if (AtEnd)
                    throw Error("a multi-line string without its closing quotes");
                if (s[i] == '\'' && Peek(1) == '\'' && Peek(2) == '\'')
                {
                    var extra = 0;
                    while (extra < 2 && Peek(3 + extra) == '\'')
                        extra++;
                    var text = s[start..(i + extra)];
                    i += 3 + extra;
                    return text;
                }
                if (s[i] == '\n')
                    line++;
                i++;
            }
        }

        void SkipFirstNewline()
        {
            if (Peek() == '\n')
            {
                i++;
                line++;
            }
            else if (Peek() == '\r' && Peek(1) == '\n')
            {
                i += 2;
                line++;
            }
        }

        void Escape(StringBuilder b)
        {
            if (AtEnd)
                throw Error("an escape at the end of the text");
            var e = s[i++];
            switch (e)
            {
                case 'b': b.Append('\b'); break;
                case 't': b.Append('\t'); break;
                case 'n': b.Append('\n'); break;
                case 'f': b.Append('\f'); break;
                case 'r': b.Append('\r'); break;
                case '"': b.Append('"'); break;
                case '\\': b.Append('\\'); break;
                case 'u':
                case 'U':
                    var len = e == 'u' ? 4 : 8;
                    if (i + len > s.Length || !int.TryParse(s.AsSpan(i, len), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                        throw Error("a malformed unicode escape");
                    b.Append(char.ConvertFromUtf32(cp));
                    i += len;
                    break;
                default:
                    throw Error($"an unknown escape '\\{e}'");
            }
        }
    }
}
