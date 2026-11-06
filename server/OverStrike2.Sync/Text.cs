using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Sync;

/// <summary>Text as a player may read it, and the small words of the reports: no control characters, no bidi overrides, lengths in code
/// points; sizes, durations and numbers for people.</summary>
public static partial class Text
{
    [GeneratedRegex("[\\x00-\\x08\\x0b\\x0c\\x0e-\\x1f\\x7f-\\x9f]")] private static partial Regex CtrlRx();
    [GeneratedRegex("[\u202a-\u202e\u2066-\u2069]")] private static partial Regex BidiRx();
    [GeneratedRegex("\\s+")] private static partial Regex WsRx();
    [GeneratedRegex("\\[/?[a-zA-Z0-9\\*]+(?:=[^\\]]*)?\\]")] private static partial Regex BbcodeRx();
    [GeneratedRegex("\\[img\\].*?\\[/img\\]", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)] private static partial Regex ImgRx();
    [GeneratedRegex("<[^>]+>")] private static partial Regex TagRx();

    /// <summary>One line of plain text: control characters become spaces, bidi overrides go, whitespace runs become one space. With a limit:
    /// at most that many code points, cut at a space when one is near the end, and an ellipsis.</summary>
    public static string Clean(string? value, int? limit = null)
    {
        var text = CtrlRx().Replace(value ?? "", " ");
        text = BidiRx().Replace(text, "");
        text = WsRx().Replace(text, " ").Trim();
        if (limit is { } n && n > 0 && Len(text) > n)
        {
            var cut = Cut(text, n).TrimEnd();
            var space = RFind(cut, " ");
            if (space > n * 0.6)
                cut = Cut(cut, space).TrimEnd();
            text = cut.TrimEnd(',', ';', ':', '.', '-') + "…";
        }
        return text;
    }

    /// <summary>A Workshop description as plain text: its pictures, BBCode and HTML tags out, its entities read.</summary>
    public static string CleanDescription(string? raw)
    {
        var text = ImgRx().Replace(raw ?? "", " ");
        text = BbcodeRx().Replace(text, " ");
        text = TagRx().Replace(text, " ");
        return Clean(WebUtility.HtmlDecode(text));
    }

    /// <summary>Code points: a surrogate pair is one.</summary>
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

    /// <summary>The first n code points: a surrogate pair is never cut in two (a map's name on disk would get a U+FFFD).</summary>
    public static string Cut(string s, int n)
    {
        var points = 0;
        for (var i = 0; i < s.Length; i++, points++)
        {
            if (points >= n)
                return s[..i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                i++;
        }
        return s;
    }

    /// <summary>Where the last `sub` starts, in code points; -1 when it is not there.</summary>
    public static int RFind(string s, string sub)
    {
        var i = s.LastIndexOf(sub, StringComparison.Ordinal);
        return i < 0 ? -1 : Len(s[..i]);
    }

    /// <summary>A Workshop id's shape: ASCII digits, at least one.</summary>
    public static bool Digits(string? s) => !string.IsNullOrEmpty(s) && s.All(char.IsAsciiDigit);

    /// <summary>maps.json's sizeMb: whole MB, rounded half up, never 0 (the bridge would announce '(0 MB)').</summary>
    public static long SizeMb(JsonNode? bytes) => Math.Max(1, (long)Math.Truncate(Node.Int(bytes) / 1048576.0 + 0.5));

    public static string ItemName(JsonNode? item)
    {
        var name = Clean(WebUtility.HtmlDecode(Node.StrOr(Node.Get(item, "title"))));
        return name.Length > 0 ? name : $"Workshop item {Node.Str(Node.Get(item, "publishedfileid"))}";
    }

    public static List<string> ItemTags(JsonNode? item) =>
        Node.Arr(Node.Get(item, "tags")).OfType<JsonObject>().Where(t => Node.Truthy(t["tag"])).Select(t => Node.Str(t["tag"])).ToList();

    /// <summary>ok | removed | hidden | banned, as the bridge's own check reads the same reply.</summary>
    public static string WorkshopVerdict(JsonNode? item)
    {
        if (!Node.Truthy(item) || Node.Int(Node.Get(item, "result")) != 1)
            return "removed";
        if (Node.Truthy(Node.Get(item, "banned")))
            return "banned";
        if (Node.Int(Node.Get(item, "visibility")) != 0)
            return "hidden";
        return "ok";
    }

    /// <summary>"1h05m", "5m03s", "42s".</summary>
    public static string Hms(double seconds) => OverStrike2.Core.Words.Duration(seconds);

    /// <summary>"1.4 GB" from a gigabyte on, else "730 MB".</summary>
    public static string Mb(long bytes) => OverStrike2.Core.Words.Size(bytes);

    /// <summary>A number with this many decimals, for people ("12.3"); whatever the machine's culture.</summary>
    public static string Fixed(double x, int decimals) => x.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>An exception as the reports and the mail name it: its type and its message, ValueError('boom').</summary>
    public static string Of(Exception e) => $"{e.GetType().Name}('{e.Message}')";
}
