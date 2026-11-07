using System.Globalization;
using System.Text;

namespace OverStrike2.Deploy;

/// <summary>A command's end with an exit code, as Python's sys.exit: Text (when there is one) goes to stderr, Code is the exit code.
/// Fail logs its line first and ends with the code alone, as deploy/os2.py's fail did.</summary>
sealed class Exit(int code, string? text = null) : Exception(text ?? code.ToString(CultureInfo.InvariantCulture))
{
    public int Code { get; } = code;
    public string? Text { get; } = text;
}

/// <summary>What every command prints: a log line is the local time and the words, as the Python scripts printed them.</summary>
static class Out
{
    /// <summary>Now, for the log lines and the lock's words; the self-tests hand their own.</summary>
    public static Func<DateTime> Clock = () => DateTime.Now;

    public static void Log(params object?[] parts)
    {
        Console.Out.WriteLine(Clock().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + string.Join(" ", parts.Select(Py.Str)));
        Console.Out.Flush();
    }

    public static void Print(string line)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }

    /// <summary>deploy/os2.py's fail: the FAILED line, then the end of the command with that exit code.</summary>
    public static Exit Fail(string message, int code = 1)
    {
        Log("FAILED: " + message);
        return new Exit(code);
    }

    /// <summary>sys.exit("words"): the words on stderr, exit code 1.</summary>
    public static Exit Stop(string text) => new(1, text);

    /// <summary>UTF-8 out, whatever the console's code page: names and map titles are not all ASCII.</summary>
    public static void Utf8()
    {
        var utf8 = new UTF8Encoding(false);
        try
        {
            Console.OutputEncoding = utf8;
        }
        catch (IOException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}

/// <summary>Python's ways with text, where the output has to read as the Python scripts' did: str(), repr(), slices, strip, splitlines.</summary>
static class Py
{
    /// <summary>str(x): None, True and False as Python writes them, numbers invariant, everything else as itself.</summary>
    public static string Str(object? x) => x switch
    {
        null => "None",
        string s => s,
        bool b => b ? "True" : "False",
        double d => Float(d),
        float f => Float(f),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        System.Text.Json.Nodes.JsonNode n => Json.PyStr(n),
        ITuple t => Repr(t),
        System.Collections.IEnumerable e => Repr(e),
        _ => x.ToString() ?? "",
    };

    /// <summary>repr(x) of a str, None, a number, a bool, a list or a tuple.</summary>
    public static string Repr(object? x) => x switch
    {
        null => "None",
        string s => ReprStr(s),
        bool b => b ? "True" : "False",
        double d => Float(d),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        System.Text.Json.Nodes.JsonNode n => Json.PyRepr(n),
        ITuple t => "(" + string.Join(", ", Enumerable.Range(0, t.Length).Select(i => Repr(t[i]))) + (t.Length == 1 ? ",)" : ")"),
        System.Collections.IDictionary d => "{" + string.Join(", ", d.Keys.Cast<object>().Select(k => Repr(k) + ": " + Repr(d[k]))) + "}",
        System.Collections.IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Repr)) + "]",
        _ => x.ToString() ?? "",
    };

    /// <summary>repr of a str: single quotes unless the text holds a single quote and no double quote.</summary>
    public static string ReprStr(string s)
    {
        var q = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var b = new StringBuilder();
        b.Append(q);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                default:
                    if (c == q)
                        b.Append('\\').Append(c);
                    else if (c < 0x20 || c == 0x7f)
                        b.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    else
                        b.Append(c);
                    break;
            }
        }
        return b.Append(q).ToString();
    }

    /// <summary>repr of a float: the shortest text that reads back the same, with Python's exponent rules.</summary>
    public static string Float(double d)
    {
        if (double.IsNaN(d))
            return "nan";
        if (double.IsInfinity(d))
            return d > 0 ? "inf" : "-inf";
        var r = d.ToString("R", CultureInfo.InvariantCulture);
        var e = r.IndexOfAny(['E', 'e']);
        string mantissa = e < 0 ? r : r[..e], digits;
        int exp;
        var neg = mantissa.StartsWith('-');
        if (neg)
            mantissa = mantissa[1..];
        // normalise to digits and a decimal exponent: value = 0.digits x 10^exp
        var dot = mantissa.IndexOf('.');
        var intPart = dot < 0 ? mantissa : mantissa[..dot];
        var frac = dot < 0 ? "" : mantissa[(dot + 1)..];
        exp = (e < 0 ? 0 : int.Parse(r[(e + 1)..], CultureInfo.InvariantCulture)) + intPart.Length;
        digits = (intPart + frac).TrimStart('0');
        exp -= (intPart + frac).Length - (intPart + frac).TrimStart('0').Length;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
            return neg ? "-0.0" : "0.0";
        var sci = exp - 1;   // the exponent of the first digit
        string text;
        if (sci < -4 || sci >= 16)
        {
            text = digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (sci < 0 ? "-" : "+") + Math.Abs(sci).ToString("00", CultureInfo.InvariantCulture);
        }
        else if (exp <= 0)
        {
            text = "0." + new string('0', -exp) + digits;
        }
        else if (exp >= digits.Length)
        {
            text = digits + new string('0', exp - digits.Length) + ".0";
        }
        else
        {
            text = digits[..exp] + "." + digits[exp..];
        }
        return neg ? "-" + text : text;
    }

    /// <summary>s[start:end] with Python's forgiveness: past the end is empty, never an error.</summary>
    public static string Slice(string? s, int start, int? end = null)
    {
        s ??= "";
        var n = s.Length;
        var a = start < 0 ? Math.Max(0, n + start) : Math.Min(start, n);
        var b = end is null ? n : end.Value < 0 ? Math.Max(0, n + end.Value) : Math.Min(end.Value, n);
        return b <= a ? "" : s[a..b];
    }

    /// <summary>str(x)[start:end].</summary>
    public static string SliceStr(object? x, int start, int? end = null) => Slice(Str(x), start, end);

    static bool IsPySpace(char c) => char.IsWhiteSpace(c) || c is '\x1c' or '\x1d' or '\x1e' or '\x1f';

    /// <summary>str.strip(): Python's whitespace, both ends.</summary>
    public static string Strip(string? s)
    {
        s ??= "";
        int a = 0, b = s.Length;
        while (a < b && IsPySpace(s[a]))
            a++;
        while (b > a && IsPySpace(s[b - 1]))
            b--;
        return s[a..b];
    }

    public static string RStrip(string? s)
    {
        s ??= "";
        var b = s.Length;
        while (b > 0 && IsPySpace(s[b - 1]))
            b--;
        return s[..b];
    }

    public static bool IsSpace(string s) => s.Length > 0 && s.All(IsPySpace);

    const char LineSeparator = (char)0x2028, ParagraphSeparator = (char)0x2029;

    /// <summary>str.splitlines(): every line break Python knows, the breaks themselves dropped, no empty last line.</summary>
    public static List<string> SplitLines(string? s)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(s))
            return lines;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or (char)0x2028 or (char)0x2029)
            {
                lines.Add(s[start..i]);
                if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n')
                    i++;
                start = i + 1;
            }
        }
        if (start < s.Length)
            lines.Add(s[start..]);
        return lines;
    }

    /// <summary>str.isdigit() for the ASCII digits the scripts meant.</summary>
    public static bool IsDigits(string? s) => !string.IsNullOrEmpty(s) && s.All(char.IsAsciiDigit);

    /// <summary>subprocess.list2cmdline: the command line Windows' CreateProcess takes for these arguments, quoted by the MS C runtime's rules
    /// (an argument with a space or a tab, or an empty one, in double quotes; a quote escaped, the backslashes before it doubled).</summary>
    public static string List2CmdLine(IEnumerable<string> argv)
    {
        var result = new StringBuilder();
        foreach (var arg in argv)
        {
            var backslashes = 0;
            if (result.Length > 0)
                result.Append(' ');
            var quote = arg.Contains(' ') || arg.Contains('\t') || arg.Length == 0;
            if (quote)
                result.Append('"');
            foreach (var c in arg)
            {
                if (c == '\\')
                    backslashes++;
                else if (c == '"')
                {
                    result.Append('\\', backslashes * 2);
                    backslashes = 0;
                    result.Append("\\\"");
                }
                else
                {
                    result.Append('\\', backslashes);
                    backslashes = 0;
                    result.Append(c);
                }
            }
            result.Append('\\', backslashes);
            if (quote)
            {
                result.Append('\\', backslashes);
                result.Append('"');
            }
        }
        return result.ToString();
    }

    /// <summary>Python's round() of a float to an int: halves to even.</summary>
    public static int Round(double v) => (int)Math.Round(v, MidpointRounding.ToEven);

    /// <summary>Python's format(v, 'g'): six significant digits, no trailing zeros.</summary>
    public static string G(double v)
    {
        if (v == Math.Floor(v) && Math.Abs(v) < 1e6)
            return ((long)v).ToString(CultureInfo.InvariantCulture);
        return v.ToString("G6", CultureInfo.InvariantCulture).Replace("E", "e", StringComparison.Ordinal);
    }

    /// <summary>Python's format(v, '.0f'): the nearest whole number, halves to even.</summary>
    public static string F0(double v) => Math.Round(v, MidpointRounding.ToEven).ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>html.escape(s, quote): &amp;, &lt;, &gt;, and with quote the two quotes.</summary>
    public static string HtmlEscape(string s, bool quote = true)
    {
        s = s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
        return quote ? s.Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#x27;", StringComparison.Ordinal) : s;
    }

    /// <summary>A tuple of ints as Python prints one: (1, 2, 3, 4).</summary>
    public static string Tuple(params int[] items) => "(" + string.Join(", ", items.Select(i => i.ToString(CultureInfo.InvariantCulture))) + ")";
}

/// <summary>A tuple, for repr.</summary>
interface ITuple
{
    int Length { get; }
    object? this[int index] { get; }
}

/// <summary>(network, IP, MAC): a container's address on one network, as bridge.py and testcopy.py compare and print them.</summary>
sealed record NetAddress(string Net, string? Ip, string? Mac) : ITuple
{
    public int Length => 3;
    public object? this[int index] => index switch { 0 => Net, 1 => Ip, _ => Mac };
}
