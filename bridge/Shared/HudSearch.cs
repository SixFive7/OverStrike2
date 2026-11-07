// Full-text search in the HUD's map browser: the on-screen keyboard edits a query, and every listed map is matched on its
// name, its Workshop tags and its Workshop description - not only on its name, as !find did. Shared like Hud.cs: the bridge reads the
// index (search.json beside maps.json: the bridge's --search-index keeps it in step with maps.json, the sync adds to it), serves it on
// /search/<secret> and tests the matching; the plugin fetches it when its version moves and filters locally on every key press.
// Plain BCL only, and core's SearchText, the rule search.json's texts are written by (the plugin compiles it in as source).
using System.Globalization;
using System.Text;
using OverStrike2.Core;

public sealed class HudSearch
{
    public const string Magic = "os2-search\t1";
    public const int MaxQuery = 40;          // the keyboard stops there; the query box shows it whole
    public const int MaxText = 2000;         // per map, after normalising: name, tags and the description's start

    public long Version;
    readonly Dictionary<string, string> texts = new(StringComparer.Ordinal); // map key -> normalised text
    readonly Dictionary<string, string> names = new(StringComparer.Ordinal); // a map's name -> normalised: every key press scores every name

    public int Count => texts.Count;

    public void Set(string key, string text)
    {
        var t = Normal(text);
        if (t.Length > MaxText) t = t[..MaxText];
        if (key != "" && t != "") texts[key] = t;
    }

    public string? TextOf(string key) => texts.TryGetValue(key, out var t) ? t : null;

    /// <summary>A text as the search compares it - a name, a text of search.json, a query: lowered by the rule search.json's texts are written by (core's
    /// SearchText.Lower, Python's lower()), letters and digits kept in any script, everything else one space: "Glass-War_2!" -> "glass war 2". A combining
    /// mark goes where it stands, so the dot above that the rule puts after İ's i breaks no word ("FUN GAME AİM" is found by "aim"), and a final sigma is a
    /// sigma, so the start of a word typed in capitals finds the whole word.</summary>
    public static string Normal(string? text)
    {
        var s = new StringBuilder();
        var space = true;
        foreach (var r in SearchText.Lower(text ?? "").EnumerateRunes())
        {
            var kind = Rune.GetUnicodeCategory(r);
            if (kind is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) continue;
            if (Rune.IsLetterOrDigit(r) || kind == UnicodeCategory.SpacingCombiningMark) { s.Append(r.Value == FinalSigma ? Sigma : r.ToString()); space = false; }
            else if (!space) { s.Append(' '); space = true; }
        }
        return s.ToString().Trim();
    }

    const int FinalSigma = 0x3C2;
    const string Sigma = "σ";

    /// <summary>The query as words: what the keyboard typed, normalised; empty for none.</summary>
    public static string[] Words(string? query) => Normal(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Every word somewhere in the map's name or its indexed text (name, tags, description). Word starts count most, so
    /// "aim" finds "aim_redline" and "AIM Map" before a description that mentions "claim".</summary>
    public int Score(string key, string name, string[] words)
    {
        if (words.Length == 0) return 1;
        if (!names.TryGetValue(name, out var n)) names[name] = n = Normal(name);
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
