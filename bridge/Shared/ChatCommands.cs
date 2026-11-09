// The chat commands. Every command is a shortcut for something the menu does: the plugin reads a chat line that starts with ! or /, turns it
// into what the menu's click would send (the bridge's actions on /act, the same Hud.IsAction words), or opens the menu's page, and answers the
// player privately.
//
// All is the one table: one primary per action, in the order !help lists them, each with its aliases. An alias runs the action and its
// reply teaches the primary by showing what ran ("You typed !map 12 - Ran !play 12"); !help lists primaries only. Each fun setting's id
// (Fun.cs) is a first-class command of its own: !gravity low, !drops off, !catchup off. The one word that means two things is settled in
// Primary and nowhere else: !maps with words is !find. And !nades is both a primary and a fun setting's id: on|off are the grenade drops (the
// drops setting), the setting's own choices (normal|big|huge) the grenades' blast; !catchup is a primary and the catch-up setting's id alike.
// --selftest proves no word is anything else. While a vote is open !play and !back put their map on it and a bare !vote brings its window back;
// the cards have no numbers, so a !vote with one answers that a vote is a click, and a bare number is plain chat.
//
// Every reply is written with its functional parts marked (M, Messages.cs): chat shows a command Lime - and only what can be typed is one - an argument LightBlue,
// a value Gold, a refusal LightRed, what worked Green, the rest in the chat's own colour. Every section starts with a capital, a line holds at most
// Messages.ChatLineMax characters, and --selftest walks them all (ChatReply below is what the plugin says beyond this file's own lines).
//
// Shared like Hud.cs (plain BCL): the plugin runs it, the bridge's --selftest checks the table and the parsing.
using System.Text.RegularExpressions;

public static class ChatCommands
{
    /// <summary>What a chat command asks for: an action the bridge runs (Arg is its /act command), a menu page to open (Arg the page), the map
    /// browser sorted by likes, a map to find (Arg the words: the plugin resolves them), a fun setting (Arg "setting choice" - the grenade drops
    /// and catch-up among them), a vote (Arg the typed number), the list in chat, what to type while a vote is open, or nothing that is a command.</summary>
    public enum Kind { None, Bridge, Page, Top, Play, Find, Fun, Vote, Commands, Unknown, Usage, VoteOnly, FunAll }

    /// <summary>A line as a command. Name: the primary word. Ran: the primary as it would be typed, what the reply calls it. Typed: the line as
    /// the player typed it when it was not that - an alias, or the long way round to a fun setting - which is what makes the reply teach.</summary>
    public sealed record Parsed(Kind Kind, string Arg = "", string Name = "", string Ran = "", string Typed = "");

    /// <summary>One action: its primary word, what follows it, what it does, the menu group it belongs to (the Help page's groups), and its aliases.</summary>
    public sealed record Command(string Word, string Args, string Words, string Group, params string[] Aliases);

    /// <summary>Every action, in the order !help lists them: nothing is an alias of two actions.</summary>
    public static readonly Command[] All =
    [
        new("!play", "<number, name or Workshop ID>", "Put a map to the vote, with your vote on it", "Maps", "!map", "!p", "!nominate", "!load", "!go", "!pick"),
        new("!find", "<words>", "Search the maps: the menu opens on the matches", "Maps", "!search", "!s", "!lookup"),
        new("!maps", "", "Open the map browser", "Maps", "!browse", "!list", "!maplist", "!m", TopWord),
        new("!next", "", "Start a vote for the next map, Keep playing on it", "Maps", "!n", "!rtv", "!change", "!skipmap", "!skip", "!voteskip", "!vs", "!boo",
            "!done", "!finish", "!end", "!release", "!letgo", "!over", "!new"), // !done's words: there is no own map to be done with; !new: no new-maps setting any more
        new("!random", "", "Start a vote for the next map, with your vote on Random", "Maps", "!roll", "!dice", "!rand", "!any", "!shuffle"),
        new("!back", "", "The map before this one, to the vote", "Maps", "!previous", "!prev", "!last", "!undo", "!again"),
        new("!up", "[number]", "Like the map that is on, or that map", "Likes", "!like", "!+", "!good", "!love", "!thumbsup"),
        new("!down", "[number] [reason]", $"Dislike it, and why: {DownReasons.Words}", "Likes", "!dislike", "!-", "!bad", "!meh", "!thumbsdown"),
        new("!vote", "", "Back to the vote for the next map: click a card to vote", "The vote", "!v", "!mv", "!choose"), // the cards have no numbers
        new("!fun", "", "The Fun page, each setting's own word (!gravity low), !fun random and !fun default", "Game", "!set", "!settings", "!toggle", "!mode", "!randomize", "!funrandom", "!defaults", "!fundefault"), // the last four: FunRandomWords, FunDefaultWords
        new("!nades", "<on|off>", "Grenade drops on or off", "Game", "!grenades", "!nade", "!he", "!boom"), // !drops is the drops setting's own word (Fun.cs)
        new("!catchup", "<on|off>", "Catch-up boosts on or off, until the map changes", "Game", "!catch", "!boost", "!boosts", "!comeback"), // a fun setting's own word too (Fun.cs)
        new("!time", "[10|60]", "Match length in minutes, this match too - Alone it switches", "Game", "!length", "!minutes", "!timelimit", "!roundtime", "!duration"),
        new("!restart", "", "Restart a stuck server - Twice to confirm", "Game", "!reboot", "!fix", "!unstuck", "!stuck", "!reset"),
        new("!rematch", "", "Restart the match - Twice to confirm", "Game", "!restartmatch", "!matchrestart", "!restartgame", "!rr"),
        new("!menu", "", "Open the full menu", "Menu", "!gg", "!hud", "!os", "!os2", "!open"),
        new("!help", "", "The Help page, and this list", "Menu", "!commands", "!cmds", "!h", "!?", "!info"),
        new("!how", "", "How it works: every rule and chance of a map pick", "Menu", "!howitworks", "!odds", "!chances", "!why", "!explain",
            "!rules", "!rule", "!r", "!fair", "!howto", "!policy"), // the Rules page went into How it works
        new("!sync", "", "The Sync page: what the map sync does now, its queue and its history", "Menu", "!status", "!dashboard"),
    ];

    /// <summary>!top, the old word: the map browser sorted by votes.</summary>
    public const string TopWord = "!top";

    /// <summary>!fun's aliases that run its Random and its Default: !randomize is !fun random, !defaults is !fun default.</summary>
    public static readonly string[] FunRandomWords = ["!randomize", "!funrandom"], FunDefaultWords = ["!defaults", "!fundefault"];

    static readonly Dictionary<string, string> aliases = AliasMap();

    static Dictionary<string, string> AliasMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in All)
            foreach (var a in c.Aliases) map.TryAdd(a, c.Word); // a word listed twice is what --selftest looks for; nothing here may throw in the plugin
        return map;
    }

    public static Command? Find(string word) => All.FirstOrDefault(c => c.Word.Equals(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>A card's number, typed bare: one or two digits, 1 up to the most cards a ballot can have (MapVote.MaxMaps).</summary>
    public static bool IsBallotNumber(string s) => s.Length is 1 or 2 && s.All(char.IsAsciiDigit) && s[0] != '0' && int.Parse(s, System.Globalization.CultureInfo.InvariantCulture) <= MapVote.MaxMaps;

    /// <summary>The primary a typed word runs: its alias's, and !maps with words is !find.</summary>
    static string Primary(string word, string arg)
    {
        var primary = aliases.GetValueOrDefault(word, word);
        return primary == "!maps" && arg != "" ? "!find" : primary;
    }

    /// <summary>The chat line as a command: None when it is no command (it does not start with ! or /), Unknown for a word that is none of
    /// ours, Usage when the command needs something it did not get. While a vote is open a !vote with anything after it is VoteOnly: the cards
    /// have no numbers, so a vote is a click (and a bare number is plain chat, as every chat line that is no command).</summary>
    public static Parsed Parse(string? line, bool voteOpen = false)
    {
        var text = (line ?? "").Trim();
        if (text.Length < 2 || text[0] is not ('!' or '/')) return new(Kind.None);
        var parts = text[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return new(Kind.None);
        var typed = "!" + parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        var word = Primary(typed, arg);
        if (voteOpen && word == "!vote" && arg == "") return new(Kind.Page, "Maps", word, typed != word ? word : "", typed != word ? text : ""); // a bare !vote: back to the vote
        if (voteOpen && word == "!vote") return new(Kind.VoteOnly, "", word);
        var p = Run(typed, word, arg);
        if (p.Kind is Kind.Usage or Kind.Unknown) return p;
        var teaches = typed != word || p.Ran != ""; // an alias, !top, or !fun gravity low: the reply shows what ran
        return p with { Ran = p.Ran != "" ? p.Ran : arg == "" ? word : $"{word} {arg}", Typed = teaches ? text : "" };
    }

    static Parsed Run(string typed, string word, string arg)
    {
        var low = arg.ToLowerInvariant();
        Parsed Usage() => new(Kind.Usage, "", word);
        Parsed OnMap(string verb) => arg == "" ? new(Kind.Bridge, verb, word) : Hud.IsMapArg(low) ? new(Kind.Bridge, $"{verb} {low}", word) : Usage();
        return word switch
        {
            "!play" => arg == "" ? Usage() : new(Kind.Play, arg, word),
            "!find" => arg == "" ? new(Kind.Page, "Maps", word) : new(Kind.Find, arg, word),
            "!maps" => typed == TopWord ? new(Kind.Top, "Maps", word, "!maps, sorted by likes") : new(Kind.Page, "Maps", word),
            "!next" => new(Kind.Bridge, "next", word),          // the plugin turns it into the board's Start a vote for this player (HudView.Command)
            "!random" => new(Kind.Bridge, "!next random", word), // the vote with this player's vote on Random (VoteRound.cs)
            "!back" => new(Kind.Bridge, "!previous", word),
            "!up" => OnMap("!up"),
            "!down" => DownReasons.Normal(low) is { } down ? new(Kind.Bridge, down == "" ? "!down" : $"!down {down}", word) : Usage(),
            "!vote" => arg == "" ? new(Kind.Page, "Maps", word) : IsBallotNumber(arg) ? new(Kind.Vote, arg, word) : Usage(), // alone outside a vote: the maps, where one starts
            "!fun" => FunCommand(typed, low, word),
            "!nades" => low is "on" or "off" ? new(Kind.Fun, $"drops {low}", word)
                : Fun.Find("nades") is { } size && Fun.Find(size, low) is { } blast ? new(Kind.Fun, $"{size.Id} {blast.Id}", word) : Usage(),
            "!time" => low is "10" or "60" ? new(Kind.Bridge, $"!time {low}", word) : low == "" ? new(Kind.Bridge, "!time", word) : Usage(), // alone: the other length (release 198)
            "!restart" => new(Kind.Bridge, "!restart", word),
            "!rematch" => new(Kind.Bridge, "!rematch", word),
            "!menu" => new(Kind.Page, "Start", word),
            "!help" => new(Kind.Commands, "Help", word),
            "!how" => new(Kind.Page, "How", word),
            "!sync" => new(Kind.Page, "Sync", word),
            _ when Fun.Find(word[1..]) is { } s => Fun.Find(s, low) is { } c ? new(Kind.Fun, $"{s.Id} {c.Id}", word) : Usage(),
            _ => new(Kind.Unknown, "", word),
        };
    }

    /// <summary>!fun alone is the page. !fun gravity low, the long way round, runs the setting's own word and says so; !fun nades on|off the
    /// grenade drops (the drops setting). A setting alone toggles nothing: that word's usage line.</summary>
    static Parsed FunCommand(string typed, string low, string word)
    {
        if (FunRandomWords.Contains(typed)) return new(Kind.FunAll, Fun.RandomWord, word, $"!fun {Fun.RandomWord}");   // the Fun page's Random
        if (FunDefaultWords.Contains(typed)) return new(Kind.FunAll, Fun.DefaultWord, word, $"!fun {Fun.DefaultWord}"); // ...and its Default
        var p = low.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p is [Fun.RandomWord] or [Fun.DefaultWord]) return new(Kind.FunAll, p[0], word);
        if (p.Length == 0) return new(Kind.Page, "Fun", word);
        if (p is ["nades", "on" or "off"]) return new(Kind.Fun, $"drops {p[1]}", "!nades", $"!nades {p[1]}");
        if (Fun.Find(p[0]) is not { } s) return new(Kind.Usage, "", word);
        return p.Length == 2 && Fun.Find(s, p[1]) is { } c ? new(Kind.Fun, $"{s.Id} {c.Id}", $"!{s.Id}", $"!{s.Id} {c.Id}") : new(Kind.Usage, "", $"!{s.Id}");
    }

    /// <summary>What a command needs, for its usage line: "!vote &lt;number&gt;: your choice in the vote for the next map". A fun setting's word too.</summary>
    public static string UsageOf(string word)
    {
        if (word == "!nades" && Find(word) is { } nades && Fun.Find("nades") is { } size)
            return $"{M.C("!nades")} {M.V("on|off")}: {nades.Words}  ·  {M.C("!nades")} {M.V(ChoiceWords(size))}: {size.Words}";
        if (Find(word) is { } c) return $"{M.C(c.Word)}{(c.Args != "" ? " " + ArgsMarked(c.Args) : "")}: {WordsMarked(c.Words)}";
        return word.Length > 1 && Fun.Find(word[1..]) is { } s ? $"{M.C($"!{s.Id}")} {M.V(ChoiceWords(s))}: {s.Words}" : UnknownLine(word);
    }

    /// <summary>What follows a command in its usage line: the few it can be (&lt;on|off&gt;, &lt;10|60&gt;) are values, anything else what the command is about.</summary>
    static string ArgsMarked(string args) => args.Contains('|') ? M.V(args) : M.A(args);

    /// <summary>A command's own words in its usage line: a command they name is one, with the value it is named with (!gravity low, !fun random); the reasons !down takes are values.</summary>
    static string WordsMarked(string words) =>
        Regex.Replace(words, @"(![a-z]+)( [a-z]+)?", m => Parse(m.Value) is { Kind: Kind.Fun or Kind.FunAll } p ? Marked(m.Value, p) : M.C(m.Groups[1].Value) + m.Groups[2].Value)
            .Replace(DownReasons.Words, M.V(DownReasons.Words));

    /// <summary>A command line as the reply that teaches shows it: its word a command, what follows an argument - and the last word a value where the command takes one of a few
    /// (!gravity moon, !fun gravity moon, !time 60, !down 12 big). "!maps, sorted by likes" has words after its command, no argument.</summary>
    static string Marked(string line, Parsed p)
    {
        if (line.IndexOf(' ') is not (> 0 and var at)) return M.C(line);
        var (word, rest) = (line[..at], line[(at + 1)..]);
        if (word.EndsWith(',')) return $"{M.C(word[..^1])}, {rest}";
        var last = rest.LastIndexOf(' ');
        var end = rest[(last + 1)..];
        if (!IsValue(p, end)) return $"{M.C(word)} {M.A(rest)}";
        return last < 0 ? $"{M.C(word)} {M.V(end)}" : $"{M.C(word)} {M.A(rest[..last])} {M.V(end)}";
    }

    static bool IsValue(Parsed p, string word) => p.Kind is Kind.Fun or Kind.FunAll || p.Name == "!time" || (p.Name == "!down" && DownReasons.All.Any(r => r.Word.Equals(word, StringComparison.OrdinalIgnoreCase)));

    static string ChoiceWords(Fun.Setting s) => string.Join('|', s.Choices.Select(c => c.Id));

    /// <summary>The bridge's note for a player, as chat says it: a confirmation asks a click of a menu user, and of a typist the same line again
    /// (a risky map's Play, Previous, the restart: the bridge counts both alike).</summary>
    public static string InChat(string note) => note.Replace("Click again", "Type it again").Replace("Click Previous again", "Type it again").Replace("Click Restart again", "Type it again");

    /// <summary>The reply that teaches the primary by showing what was executed, or null when the player typed the primary.</summary>
    public static string? Teach(Parsed p) => p.Typed == "" ? null : $"You typed {Marked(Hud.Clean(p.Typed, 44), p)} - Ran {Marked(Hud.Clean(p.Ran, 50), p)}";

    /// <summary>What a line can hold when the command also opens the window: chat stands at the left and the window covers it from 340 px in, at
    /// about 7.2 px a character on a 1920x1080 screen (seen in a real client, 2026-11-01).</summary>
    public const int BesideWindow = 46;

    /// <summary>The list in chat: each group's primaries, a line that fits beside the window the command opens. A command typed alone says what it needs.</summary>
    public static List<string> HelpLines()
    {
        var lines = new List<string>();
        foreach (var g in All.GroupBy(c => c.Group))
        {
            var line = $"{g.Key}:";
            foreach (var c in g)
            {
                if (M.Plain(line).Length + 1 + c.Word.Length > BesideWindow) { lines.Add(line); line = $"{g.Key}:"; }
                line += " " + M.C(c.Word);
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>What a bare !fun writes in chat beside the page, in lines that fit beside it: each setting's own word with a choice the page shows. What the
    /// page leaves out in another game mode (friendly fire's three, the grenade drops, catch-up) it leaves out too.</summary>
    public static List<string> FunLines(bool armsRace)
    {
        var lines = new List<string> { "Every setting here has its own word:" };
        var line = "";
        foreach (var s in Fun.Settings.Where(s => armsRace || !Fun.ArmsRaceOnly(s)))
        {
            var add = $"{M.C($"!{s.Id}")} {M.V(s.Choices[1].Id)}";
            if (M.Plain(line).Length + 2 + M.Plain(add).Length > BesideWindow) { lines.Add(line); line = ""; }
            line += (line == "" ? "" : "  ") + add;
        }
        if (line != "") lines.Add(line);
        return lines;
    }

    /// <summary>What chat says, to that player alone, to a !vote with a number while a vote is open: the cards have no numbers, so click one. A line
    /// that fits beside the window, which stands open for the vote.</summary>
    public static string[] VoteOnlyLines() => [M.Refused("Vote now: click a card in the window")];

    /// <summary>What chat says for a word that is no command of ours.</summary>
    public static string UnknownLine(string word) => M.Refused($"Unknown command {M.A(Hud.Clean(word, 40))} - {M.C("!help")} lists them (and the menu: hold TAB, press E)");

    /// <summary>Whether a name as a line shows it is this player's: whole, or clipped with a ~ at its end (Clip, Hud.Clean), a " in it said as ' (the plugin's fun lines). Chat's team colour goes by it.</summary>
    public static bool IsName(string shown, string name)
    {
        var whole = Hud.Clean(name, 64).Replace('"', '\'');
        return shown == whole || (shown.Length > 1 && shown.EndsWith('~') && whole.StartsWith(shown[..^1], StringComparison.Ordinal));
    }
}
