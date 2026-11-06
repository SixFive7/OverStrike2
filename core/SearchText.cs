using System.Globalization;
using System.Text;

namespace OverStrike2.Core;

/// <summary>The words a search finds a map by, as search.json holds them (the HUD's map browser matches typed words against them:
/// bridge/Shared/HudSearch.cs). One rule for both writers of that file: the Workshop sync adds a map's text when it publishes the map - its
/// name, its Workshop id, its tags and the start of its description (server/OverStrike2.Sync, Verdicts.SearchBlob) - and the bridge's
/// --search-index keeps the repository's file in step with maps.json - name, id and tags, never another author's description
/// (bridge/SearchIndexFile.cs). Every text either of them wrote before the C# programs was Python's, so this is the Python sync's rule: each
/// part cleaned as the sync cleans a title, then Python's str.lower() and str.split(). Where that differs from .NET: İ (U+0130) becomes i and a
/// combining dot above, where .NET keeps it; a capital sigma that ends a word becomes the final sigma, which .NET's casing never makes;
/// U+001C-U+001F are whitespace too. Plain BCL, no state: the bridge is native AOT.</summary>
public static class SearchText
{
    /// <summary>How much of a description the sync's text takes: its first this many code points, cleaned and lowered.</summary>
    public const int DescriptionMax = 800;

    /// <summary>A map's text: its name's words, its Workshop id, its tags' words, then - the sync's only - its description's; one space
    /// between.</summary>
    public static string Of(string name, string id, IEnumerable<string> tags, string? description = null)
    {
        var words = Words(name);
        words.Add(id);
        foreach (var tag in tags)
            words.AddRange(Words(tag));
        if (description != null)
            words.AddRange(Words(description, DescriptionMax));
        return string.Join(' ', words.Where(w => w.Length > 0));
    }

    /// <summary>One part's words: cleaned, lowered, its first max code points, split.</summary>
    public static List<string> Words(string? part, int max = int.MaxValue) => Split(Cut(Lower(Clean(part)), max));

    /// <summary>One line of plain text, as the sync cleans a title (its Text.Clean, without a limit): control characters (U+0000-U+0008,
    /// U+000B, U+000C, U+000E-U+001F, U+007F-U+009F) become spaces, bidi overrides (U+202A-U+202E, U+2066-U+2069) go, whitespace runs become
    /// one space, and none is left at either end.</summary>
    public static string Clean(string? text)
    {
        var s = new StringBuilder(text?.Length ?? 0);
        var space = true; // nothing yet, or a space last: whitespace here adds none
        foreach (var c in text ?? "")
        {
            if (Bidi(c))
                continue;
            if (Control(c) || char.IsWhiteSpace(c))
            {
                if (!space)
                    s.Append(' ');
                space = true;
                continue;
            }
            s.Append(c);
            space = false;
        }
        if (s.Length > 0 && s[^1] == ' ')
            s.Length--;
        return s.ToString();
    }

    static bool Control(char c) => (int)c is <= 0x08 or 0x0B or 0x0C or (>= 0x0E and <= 0x1F) or (>= 0x7F and <= 0x9F);

    static bool Bidi(char c) => (int)c is (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069);

    /// <summary>Python's str.lower(): every code point's full lower case - İ (U+0130) becomes i and a combining dot above, where .NET keeps
    /// it - and a capital sigma that ends a word becomes the final sigma (Unicode's Final_Sigma, which .NET's casing does not apply).</summary>
    public static string Lower(string text)
    {
        var points = Points(text);
        var s = new StringBuilder(text.Length);
        for (var i = 0; i < points.Count; i++)
        {
            var c = points[i];
            if (c == 0x3A3)
                s.Append(FinalSigma(points, i) ? (char)0x3C2 : (char)0x3C3); // the final sigma, else the small sigma
            else if (c == 0x130)
                s.Append('i').Append((char)0x307);                    // i and the combining dot above
            else if (c is >= 0xD800 and <= 0xDFFF)
                s.Append((char)c);                                     // a lone surrogate stays as it is, as in Python
            else
                s.Append(Rune.ToLowerInvariant(new Rune(c)).ToString());
        }
        return s.ToString();
    }

    /// <summary>Python's str.split(): the runs between whitespace, where Python's whitespace also has U+001C-U+001F (.NET's does not).</summary>
    public static List<string> Split(string text)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var space = i == text.Length || IsSpace(text[i]);
            if (!space && start < 0)
                start = i;
            else if (space && start >= 0)
            {
                words.Add(text[start..i]);
                start = -1;
            }
        }
        return words;
    }

    static bool IsSpace(char c) =>
        (int)c is (>= 0x09 and <= 0x0D) or (>= 0x1C and <= 0x20) or 0x85 or 0xA0 or 0x1680 or (>= 0x2000 and <= 0x200A) or 0x2028 or 0x2029 or 0x202F
            or 0x205F or 0x3000;

    /// <summary>Python's text[:max]: the first max code points, a surrogate pair one.</summary>
    static string Cut(string text, int max)
    {
        var points = 0;
        for (var i = 0; i < text.Length; i++, points++)
        {
            if (points == max)
                return text[..i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
        }
        return text;
    }

    static List<int> Points(string text)
    {
        var points = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                points.Add(char.ConvertToUtf32(text[i], text[i + 1]));
                i++;
            }
            else
                points.Add(text[i]);
        }
        return points;
    }

    /// <summary>Unicode's Final_Sigma, as CPython tests it: a cased letter before the sigma (case-ignorable ones between are skipped), and
    /// none after it.</summary>
    static bool FinalSigma(List<int> points, int at)
    {
        var j = at - 1;
        while (j >= 0 && CaseIgnorable(points[j]))
            j--;
        if (j < 0 || !Cased(points[j]))
            return false;
        j = at + 1;
        while (j < points.Count && CaseIgnorable(points[j]))
            j++;
        return j == points.Count || !Cased(points[j]);
    }

    /// <summary>Unicode's Case_Ignorable: marks, format characters, modifier letters and symbols, and what sits inside a word without
    /// ending it - apostrophes, full stops, colons (Word_Break MidLetter, MidNumLet and Single_Quote).</summary>
    static bool CaseIgnorable(int c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format
            or UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol
        || c is 0x27 or 0x2E or 0x3A or 0xB7 or 0x387 or 0x55F or 0x5F4 or 0x2018 or 0x2019 or 0x2024 or 0x2027 or 0xFE13 or 0xFE52 or 0xFE55
            or 0xFF07 or 0xFF0E or 0xFF1A;

    /// <summary>Unicode's Cased, where Final_Sigma asks it (it skips the case-ignorable ones first): upper, lower and title case letters, and
    /// the few others with a case - the ordinal indicators, the Roman numerals and the circled and squared Latin letters.</summary>
    static bool Cased(int c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        || c is 0xAA or 0xBA or (>= 0x2160 and <= 0x217F) or (>= 0x24B6 and <= 0x24E9) or (>= 0x1F130 and <= 0x1F149) or (>= 0x1F150 and <= 0x1F169)
            or (>= 0x1F170 and <= 0x1F189);
}
