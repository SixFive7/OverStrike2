// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Text;

/// <summary>The functional parts of a message, marked inside its text with private-use characters: nothing a player can type survives Hud.Clean as one
/// of them (Unmark), and they are stripped from every surface but chat.</summary>
public static class M
{
    const char NameStart = '', ArgStart = '', ValueStart = '', CommandStart = '', End = '', RefusedLine = '', DoneLine = '', Break = '';

    static bool IsMark(char c) => c is >= '' and <= '';

    /// <summary>Text from outside (a player's name, a map's title) with any mark taken out: nobody colours a line by naming themselves so.</summary>
    public static string Unmark(string? text) => string.Concat((text ?? "").Where(c => !IsMark(c)));

    /// <summary>A player's name: their team's colour in chat.</summary>
    public static string N(string name) => $"{NameStart}{Unmark(name)}{End}";

    /// <summary>What a command or a setting is about: "gravity", a map, a page.</summary>
    public static string A(string argument) => $"{ArgStart}{Unmark(argument)}{End}";

    /// <summary>A value: "Moon", "41%", "2 min".</summary>
    public static string V(string value) => $"{ValueStart}{Unmark(value)}{End}";

    /// <summary>A chat command: "!help".</summary>
    public static string C(string command) => $"{CommandStart}{Unmark(command)}{End}";

    /// <summary>A whole line that refuses something: its plain words are the refusal's colour in chat.</summary>
    public static string Refused(string line) => RefusedLine + line;

    /// <summary>A whole line that says it worked: its plain words are the done colour in chat.</summary>
    public static string Done(string line) => DoneLine + line;

    /// <summary>Whether this place in a marked line lies inside a marked part: a name or a map's title, which stay as they are written.</summary>
    public static bool Inside(string marked, int at)
    {
        for (var i = at - 1; i >= 0; i--)
        {
            if (marked[i] == End) return false;
            if (marked[i] is NameStart or ArgStart or ValueStart or CommandStart) return true;
        }
        return false;
    }

    /// <summary>The words alone, for every surface but chat.</summary>
    public static string Plain(string? text) => string.Concat((text ?? "").Where(c => !IsMark(c)));

    /// <summary>Marked text with the dashes in a name's or an argument's own words out: a map's title is somebody else's text, and a " - " in it ends no section of ours (Sections).</summary>
    public static string OwnSections(string marked)
    {
        var s = new StringBuilder(marked.Length);
        var theirs = false;
        foreach (var c in marked)
        {
            if (c is NameStart or ArgStart) theirs = true;
            else if (c == End) theirs = false;
            s.Append(theirs && c is '-' or '·' ? ' ' : c);
        }
        return s.ToString();
    }

    /// <summary>A line break that survives the wire (Hud.Clean takes newlines out): CS2's plain centre text and alert break on it (Lines).</summary>
    public static string Lines(params string[] lines) => string.Join(Break, lines);

    /// <summary>The centre text's words from the wire: its line breaks back, no other mark.</summary>
    public static string Centre(string? text) => string.Concat((text ?? "").Select(c => c == Break ? '\n' : c).Where(c => !IsMark(c)));

    /// <summary>The colours chat uses, as the game's colour bytes: the plugin hands them over (CounterStrikeSharp's ChatColors), --selftest fakes them.
    /// NameColour: a player's team colour by name, or null when nobody on the server has that name.</summary>
    public sealed record Palette(char Plain, char Command, char Argument, char Value, char Refusal, char Done, Func<string, char?> NameColour);

    /// <summary>A marked line as chat shows it.</summary>
    public static string Render(string marked, Palette p)
    {
        var s = new StringBuilder();
        var plain = p.Plain;
        var at = 0;
        if (marked.StartsWith(RefusedLine)) { plain = p.Refusal; at = 1; }
        else if (marked.StartsWith(DoneLine)) { plain = p.Done; at = 1; }
        if (plain != p.Plain) s.Append(plain);
        var name = -1;
        for (; at < marked.Length; at++)
        {
            var c = marked[at];
            switch (c)
            {
                case NameStart: name = s.Length; break;
                case ArgStart: s.Append(p.Argument); break;
                case ValueStart: s.Append(p.Value); break;
                case CommandStart: s.Append(p.Command); break;
                case End:
                    if (name >= 0) { if (p.NameColour(s.ToString(name, s.Length - name)) is { } team) s.Insert(name, team); name = -1; }
                    s.Append(plain);
                    break;
                case Break: s.Append(" - "); break;
                case RefusedLine or DoneLine: break;
                default: s.Append(c); break;
            }
        }
        return s.ToString().Replace("%s", "% s", StringComparison.Ordinal); // CS2 fills %s1 in a chat line with nothing
    }

    /// <summary>A marked line as chat lines of at most max characters a player sees: cut after a section where it can be, else at a space. A mark that a cut
    /// leaves open ends with its line. A refusal's or a done line's colour goes on with the second line.</summary>
    public static List<string> ChatLines(string marked, int max = Messages.ChatLineMax)
    {
        var lines = new List<string>();
        var rest = marked;
        var lead = marked.Length > 0 && marked[0] is RefusedLine or DoneLine ? marked[0].ToString() : "";
        while (Plain(rest).Length > max)
        {
            var seen = 0;
            var cut = -1;
            for (var i = 0; i < rest.Length && seen <= max; i++)
            {
                if (IsMark(rest[i])) continue;
                seen++;
                if (rest[i] == ' ' && seen <= max) cut = i;
            }
            if (cut <= 0) break;
            lines.Add(rest[..cut].TrimEnd());
            rest = lead + rest[(cut + 1)..].TrimStart();
        }
        lines.Add(rest);
        return lines;
    }
}
