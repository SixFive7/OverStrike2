// Full-text search in the HUD's map browser (2026-10-21): the on-screen keyboard edits a query, and every listed map is matched on its
// name, its Workshop tags and its Workshop description - not only on its name, as !find did. Shared like Hud.cs: the bridge reads the
// index (search.json beside maps.json, built by search-index.py from the site's rows and the Workshop scrape), serves it on
// /search/<secret> and tests the matching; the plugin fetches it when its version moves and filters locally on every key press.
// Plain BCL only.
using System.Globalization;
using System.Text;

public sealed class HudSearch
{
    public const string Magic = "gg-search\t1";
    public const int MaxQuery = 40;          // the keyboard stops there; the query box shows it whole
    public const int MaxText = 2000;         // per map, after normalising: name, tags and the description's start

    public long Version;
    readonly Dictionary<string, string> texts = new(StringComparer.Ordinal); // map key -> normalised text

    public int Count => texts.Count;

    public void Set(string key, string text)
    {
        var t = Normal(text);
        if (t.Length > MaxText) t = t[..MaxText];
        if (key != "" && t != "") texts[key] = t;
    }

    public string? TextOf(string key) => texts.TryGetValue(key, out var t) ? t : null;

    /// <summary>Lower case, letters and digits kept (any script), everything else one space: "Glass-War_2!" -> "glass war 2".</summary>
    public static string Normal(string? text)
    {
        var s = new StringBuilder();
        var space = true;
        foreach (var ch in (text ?? "").ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { s.Append(ch); space = false; }
            else if (!space) { s.Append(' '); space = true; }
        }
        return s.ToString().Trim();
    }

    /// <summary>The query as words: what the keyboard typed, normalised; empty for none.</summary>
    public static string[] Words(string? query) => Normal(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Every word somewhere in the map's name or its indexed text (name, tags, description). Word starts count most, so
    /// "aim" finds "aim_redline" and "AIM Map" before a description that mentions "claim".</summary>
    public int Score(string key, string name, string[] words)
    {
        if (words.Length == 0) return 1;
        var n = Normal(name);
        var t = texts.TryGetValue(key, out var x) ? x : "";
        var score = 0;
        foreach (var w in words)
        {
            if (StartsWord(n, w)) score += 100;
            else if (n.Contains(w, StringComparison.Ordinal)) score += 40;
            else if (StartsWord(t, w)) score += 10;
            else if (t.Contains(w, StringComparison.Ordinal)) score += 3;
            else return 0;
        }
        return score;
    }

    static bool StartsWord(string text, string word)
    {
        for (var at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
            if (at == 0 || text[at - 1] == ' ') return true;
        return false;
    }

    public string Encode()
    {
        var s = new StringBuilder(Magic).Append('\n');
        s.Append("ver\t").Append(Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var (key, text) in texts) s.Append("s\t").Append(Hud.Clean(key, 40)).Append('\t').Append(text).Append('\n');
        return s.ToString();
    }

    /// <summary>The index out of Encode's text, or null when it is not one. Unknown and malformed lines are skipped.</summary>
    public static HudSearch? Decode(string? text)
    {
        if (text == null || !text.StartsWith(Magic)) return null;
        var h = new HudSearch();
        foreach (var raw in text.Split('\n').Skip(1))
        {
            var f = raw.TrimEnd('\r').Split('\t');
            if (f[0] == "ver" && f.Length > 1 && long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) h.Version = v;
            else if (f[0] == "s" && f.Length > 2) h.Set(f[1], f[2]);
        }
        return h;
    }
}

/// <summary>The on-screen keyboard (a custom HUD has no text entry: Panel, Label, Image and Button only). Each key is a button whose id
/// says what it types; the plugin hands a click here and the player's query changes. Plain BCL.</summary>
public static class HudKeyboard
{
    public const string Open = "gg_q_kbd", Back = "gg_kb_back", Clear = "gg_kb_clear", Space = "gg_kb_space", Done = "gg_kb_done";
    public const string Letters = "abcdefghijklmnopqrstuvwxyz", Digits = "1234567890";

    /// <summary>Every key's button id, in the keyboard's order: the digits, the letters, then space, backspace, clear and done.</summary>
    public static IEnumerable<string> Keys() =>
        Digits.Select(c => $"gg_kb_{c}").Concat(Letters.Select(c => $"gg_kb_{c}")).Concat([Space, Back, Clear, Done]);

    /// <summary>The character a key types, or null for the editing keys and anything else.</summary>
    public static char? CharOf(string buttonId) =>
        buttonId.Length == 7 && buttonId.StartsWith("gg_kb_", StringComparison.Ordinal) && (Letters.Contains(buttonId[6]) || Digits.Contains(buttonId[6])) ? buttonId[6] : null;

    /// <summary>The query after one key: a character appended (up to HudSearch.MaxQuery), backspace, clear, or space (never two in a row,
    /// never first). Null when the button is not one of the keyboard's.</summary>
    public static string? Press(string query, string buttonId)
    {
        if (CharOf(buttonId) is { } ch) return query.Length < HudSearch.MaxQuery ? query + ch : query;
        return buttonId switch
        {
            Back => query.Length > 0 ? query[..^1] : "",
            Clear => "",
            Space => query.Length == 0 || query.EndsWith(' ') || query.Length >= HudSearch.MaxQuery ? query : query + " ",
            _ => null,
        };
    }
}
