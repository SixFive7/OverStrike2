// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Text.RegularExpressions;

/// <summary>Every section of a message starts with a capital, held by --selftest over every template and everything said while it runs. A
/// section is what stands between " - ", " · " and line breaks. What may start one besides a capital: a digit, a sign (+60% HP, #1, !help), a name (a word
/// with an underscore or a digit in it: a map's name stays as it is written).</summary>
public static partial class Sections
{
    [GeneratedRegex(@"\s+-\s+|\s+·\s+|\n|\r")] private static partial Regex Between();

    /// <summary>The sections of a text.</summary>
    public static IEnumerable<string> Of(string text) => Between().Split(M.Plain(M.OwnSections(text)).Replace('', '\n')).Select(s => s.Trim()).Where(s => s != "");

    /// <summary>The first section that starts wrong, or null. allowed: words a section may start with as they are (the names in a test's words).</summary>
    public static string? Lower(string text, IReadOnlyCollection<string>? allowed = null)
    {
        foreach (var section in Of(text))
        {
            var first = section[0];
            if (!char.IsLetter(first) || char.IsUpper(first)) continue;
            var word = section.Split(' ', 2)[0];
            if (word.Any(c => c == '_' || char.IsDigit(c))) continue;
            if (allowed != null && allowed.Any(a => section.StartsWith(a, StringComparison.Ordinal))) continue;
            return section;
        }
        return null;
    }

    /// <summary>The first word after every " - ", " · " and line break a capital, a name left as it is written (marked, or with an underscore or a digit in it): the net
    /// under every message, for a template no self-test reaches. A marked part stays whole, also a map's title with " - " in it. The line's own first word stays: it is
    /// usually a name. A line from outside (the restart watcher's) also goes through Messages.Capital.</summary>
    public static string Capitalize(string text) => Starts().Replace(text, m => M.Inside(text, m.Index) ? m.Value : m.Groups[1].Value + (m.Groups[2].Value.Any(c => c == '_' || char.IsDigit(c)) ? m.Groups[2].Value : Messages.Capital(m.Groups[2].Value)));

    [GeneratedRegex(@"(\s+-\s+|\s+·\s+|\n)(\S+)")] private static partial Regex Starts();
}
