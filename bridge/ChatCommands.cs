// The chat commands, back (the owner's decision of 2026-11-01, inside the release): one command
// per action. Every command is a
// shortcut for something the menu does: the plugin reads a chat line that starts with ! or /, turns it into what the menu's click would
// send (the bridge's actions on /act, the same Hud.IsAction words), or opens the menu's page, and answers the player privately.
//
// Shared like Hud.cs (plain BCL): the plugin runs it, the bridge's --selftest checks the table and the parsing.
using System.Globalization;

public static class ChatCommands
{
    /// <summary>What a chat command asks for: an action the bridge runs (Arg is its /act command), a menu page to open (Arg the page),
    /// a map to find (Arg the words: the plugin resolves them), a fun setting (Arg "setting choice"), the grenade drops (Arg on|off), a vote
    /// in the end-of-match vote (Arg the option), the list in chat, or nothing that is a command.</summary>
    public enum Kind { None, Bridge, Page, Play, Find, Fun, Nades, Vote, Commands, Unknown, Usage }

    public sealed record Parsed(Kind Kind, string Arg = "", string Name = "");

    /// <summary>One command: its word, what follows it, what it does, and the menu group it belongs to (the Help page's groups).</summary>
    public sealed record Command(string Word, string Args, string Words, string Group);

    /// <summary>Every command, in the order !help lists them.</summary>
    public static readonly Command[] All =
    [
        new("!play", "<number, name or Workshop ID>", "Play a map now, or queue it", "Maps"),
        new("!find", "<words>", "Search the maps: the menu opens on the matches", "Maps"),
        new("!maps", "", "Open the map browser", "Maps"),
        new("!next", "", "The board's Next: a random map, I'm done on your own, a skip vote on another's", "Maps"),
        new("!random", "", "A random map, while nobody holds this one", "Maps"),
        new("!back", "", "Back to the last map", "Maps"),
        new("!queue", "", "Open the queue", "Queue"),
        new("!cancel", "[number]", "Drop your request for that map, or all of them", "Queue"),
        new("!done", "", "End your own map: the queue moves on", "Queue"),
        new("!skip", "", "Vote to skip someone else's map", "Queue"),
        new("!like", "[number]", "Like the map that is on, or that map", "Voting"),
        new("!dislike", "[number]", "Dislike it", "Voting"),
        new("!toobig", "[number]", "Too big: only 1 random pick in 100", "Voting"),
        new("!ads", "[number]", "It shows ads or sends players away: never a random pick", "Voting"),
        new("!vote", "<1-6>", "Your choice in the vote at a match's end", "Voting"),
        new("!fun", "[setting] [choice]", "The fun settings: the page, or e.g. !fun gravity low", "Game"),
        new("!nades", "<on|off>", "Grenade drops on or off", "Game"),
        new("!time", "<10|60>", "Match length in minutes, from the next match", "Game"),
        new("!newmaps", "<always|half|never>", "New maps in random picks", "Game"),
        new("!restart", "", "Restart a stuck server - twice to confirm", "Game"),
        new("!menu", "", "Open the full menu", "Menu"),
        new("!help", "", "The Help page, and this list", "Menu"),
        new("!rules", "", "The rules", "Menu"),
        new("!how", "", "How it works: every chance in map picks", "Menu"),
    ];

    public static Command? Find(string word) => All.FirstOrDefault(c => c.Word.Equals(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>A number a map is named by (its rank), or a Workshop id: 1-12 digits.</summary>
    static bool IsNumber(string s) => s.Length is > 0 and <= 12 && s.All(char.IsAsciiDigit);

    /// <summary>The chat line as a command: None when it is no command (it does not start with ! or /), Unknown for a word that is none of
    /// ours, Usage when the command needs something it did not get.</summary>
    public static Parsed Parse(string line)
    {
        var text = (line ?? "").Trim();
        if (text.Length < 2 || text[0] is not ('!' or '/')) return new(Kind.None);
        var parts = text[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return new(Kind.None);
        var word = "!" + parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        var low = arg.ToLowerInvariant();
        Parsed Usage() => new(Kind.Usage, "", word);
        return word switch
        {
            "!play" => arg == "" ? Usage() : new(Kind.Play, arg, word),
            "!find" => arg == "" ? new(Kind.Page, "Maps", word) : new(Kind.Find, arg, word),
            "!maps" => new(Kind.Page, "Maps", word),
            "!next" => new(Kind.Bridge, "next", word),          // the plugin turns it into the board's Next for this player (HudView.Command)
            "!random" => new(Kind.Bridge, "!next", word),
            "!back" => new(Kind.Bridge, "!previous", word),
            "!queue" => new(Kind.Page, "Queue", word),
            "!cancel" => arg == "" || low == "all" ? new(Kind.Bridge, "!cancel all", word) : IsNumber(arg) ? new(Kind.Bridge, $"!cancel {arg}", word) : Usage(),
            "!done" => new(Kind.Bridge, "!done", word),
            "!skip" => new(Kind.Bridge, "!skip", word),
            "!like" => arg == "" ? new(Kind.Bridge, "!up", word) : IsNumber(arg) ? new(Kind.Bridge, $"!up {arg}", word) : Usage(),
            "!dislike" => arg == "" ? new(Kind.Bridge, "!down", word) : IsNumber(arg) ? new(Kind.Bridge, $"!down {arg}", word) : Usage(),
            "!toobig" => arg == "" ? new(Kind.Bridge, "!toobig", word) : IsNumber(arg) ? new(Kind.Bridge, $"!toobig {arg}", word) : Usage(),
            "!ads" => arg == "" ? new(Kind.Bridge, "!ads", word) : IsNumber(arg) ? new(Kind.Bridge, $"!ads {arg}", word) : Usage(),
            "!vote" => int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= MapVote.MaxOptions ? new(Kind.Vote, n.ToString(CultureInfo.InvariantCulture), word) : Usage(),
            "!fun" => arg == "" ? new(Kind.Page, "Fun", word) : FunArg(low) is { } fun ? new(Kind.Fun, fun, word) : Usage(),
            "!nades" => low is "on" or "off" ? new(Kind.Nades, low, word) : Usage(),
            "!time" => low is "10" or "60" ? new(Kind.Bridge, $"!time {low}", word) : Usage(),
            "!newmaps" => low is "always" or "half" or "never" ? new(Kind.Bridge, $"!newmaps {low}", word) : Usage(),
            "!restart" => new(Kind.Bridge, "!restart", word),
            "!menu" => new(Kind.Page, "Start", word),
            "!help" => new(Kind.Commands, "Help", word),
            "!rules" => new(Kind.Page, "Rules", word),
            "!how" => new(Kind.Page, "How", word),
            _ => new(Kind.Unknown, "", word),
        };
    }

    /// <summary>"!fun gravity low" as "gravity low", when the setting and the choice exist (Fun.cs); a setting alone toggles nothing.</summary>
    static string? FunArg(string arg)
    {
        var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p.Length == 2 && Fun.Find(p[0]) is { } s && Fun.Find(s, p[1]) is { } c ? $"{s.Id} {c.Id}" : null;
    }

    /// <summary>What a command needs, for its usage line: "!vote <1-6>: your choice in the vote at a match's end".</summary>
    public static string UsageOf(string word) => Find(word) is { } c ? $"{c.Word} {c.Args}: {c.Words}".Replace("  ", " ") : $"Unknown command {word} - !help lists them";

    /// <summary>The list in chat, a few lines of at most LineMax characters: each group's commands.</summary>
    public const int LineMax = 120;

    public static List<string> HelpLines()
    {
        var lines = new List<string>();
        foreach (var g in All.GroupBy(c => c.Group))
        {
            var line = $"{g.Key}:";
            foreach (var c in g)
            {
                var add = $" {c.Word}{(c.Args != "" ? " " + c.Args.Split(' ')[0] : "")}";
                if (line.Length + add.Length > LineMax) { lines.Add(line); line = $"{g.Key}:"; }
                line += add;
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>What chat says for a word that is no command of ours.</summary>
    public static string UnknownLine(string word) => $"Unknown command {word} - !help lists them (and the menu: hold TAB, press E)";
}
