// The chat commands (the owner's decisions of 2026-11-01). Every command is a shortcut for something the menu does: the plugin reads a chat
// line that starts with ! or /, turns it into what the menu's click would send (the bridge's actions on /act, the same Hud.IsAction words),
// or opens the menu's page, and answers the player privately.
//
// All is the one table: one primary per action, in the order !help lists them, each with its aliases. An alias runs the action and its
// reply teaches the primary by showing what ran ("You typed !map 12 - ran !play 12"); !help lists primaries only. Each fun setting's id
// (Fun.cs) is a first-class command of its own: !gravity low. The few words that mean two things are settled by the owner's rules, in
// Primary and nowhere else: !new with an argument is !newmaps, without one !next; !maps with words is !find. And !nades is both a primary
// and a fun setting's id: on|off are the grenade drops, the setting's own choices (normal|big|huge) the grenades' blast. --selftest proves no
// word is anything else. While an end-of-match vote is open only the vote picks a map (the owner's 26, 2026-11-02): !vote, its aliases and the
// bare number vote; whatever asks for another map (WaitForVote) answers what to type instead, and so does a !vote with a wrong number.
//
// Shared like Hud.cs (plain BCL): the plugin runs it, the bridge's --selftest checks the table and the parsing.
public static class ChatCommands
{
    /// <summary>What a chat command asks for: an action the bridge runs (Arg is its /act command), a menu page to open (Arg the page), the map
    /// browser sorted by votes, a map to find (Arg the words: the plugin resolves them), a fun setting (Arg "setting choice"), the grenade
    /// drops (Arg on|off), a vote in the end-of-match vote (Arg the option), the list in chat, what to type while a vote is open, or nothing that is a command.</summary>
    public enum Kind { None, Bridge, Page, Top, Play, Find, Fun, Nades, Vote, Commands, Unknown, Usage, VoteOnly, FunAll, CatchUp }

    /// <summary>A line as a command. Name: the primary word. Ran: the primary as it would be typed, what the reply calls it. Typed: the line as
    /// the player typed it when it was not that - an alias, or the long way round to a fun setting - which is what makes the reply teach.</summary>
    public sealed record Parsed(Kind Kind, string Arg = "", string Name = "", string Ran = "", string Typed = "");

    /// <summary>One action: its primary word, what follows it, what it does, the menu group it belongs to (the Help page's groups), and its aliases.</summary>
    public sealed record Command(string Word, string Args, string Words, string Group, params string[] Aliases);

    /// <summary>Every action, in the order !help lists them. The owner's list: nothing is an alias of two actions but the words of Context.</summary>
    public static readonly Command[] All =
    [
        new("!play", "<number, name or Workshop ID>", "Play a map now, or queue it", "Maps", "!map", "!p", "!nominate", "!load", "!go", "!pick"),
        new("!find", "<words>", "Search the maps: the menu opens on the matches", "Maps", "!search", "!s", "!lookup"),
        new("!maps", "", "Open the map browser", "Maps", "!browse", "!list", "!maplist", "!m", TopWord),
        new("!next", "", "A vote for the next map, Keep playing on it; on your own map, I'm done", "Maps", "!n", "!rtv", "!change", "!skipmap", "!skip", "!voteskip", "!vs", "!boo"),
        new("!random", "", "A vote for the next map, with your vote on Random", "Maps", "!roll", "!dice", "!rand", "!any", "!shuffle"),
        new("!back", "", "Back to the last map", "Maps", "!previous", "!prev", "!last", "!undo", "!again"),
        new("!q", "", "Open the queue", "Queue", "!queue", "!upcoming", "!whatsnext", "!lineup", "!order"),
        new("!cancel", "[number]", "Drop your request for that map, or all of them", "Queue", "!unqueue", "!remove", "!drop", "!withdraw", "!uq"),
        new("!done", "", "End your own map: a vote picks the next one", "Queue", "!finish", "!end", "!release", "!letgo", "!over"),
        new("!up", "[number]", "Like the map that is on, or that map", "Voting", "!like", "!+", "!good", "!love", "!thumbsup"),
        new("!down", "[number] [reason]", $"Dislike it, and why: {DownReasons.Words}", "Voting", "!dislike", "!-", "!bad", "!meh", "!thumbsdown"),
        new("!toobig", "[number]", "Too big: only 1 random pick in 100", "Voting", "!big", "!huge", "!toolarge", "!size", "!xl"),
        new("!ads", "[number]", "It shows ads or sends players away: never a random pick", "Voting", "!ad", "!spam", "!advert", "!report", "!scam"),
        new("!vote", "<number>", "Your choice in the vote at a match's end; the bare number works too", "Voting", "!v", "!mv", "!choose"),
        new("!fun", "", "The Fun page, each setting's own word (!gravity low), !fun random and !fun default", "Game", "!set", "!settings", "!toggle", "!mode", "!randomize", "!funrandom", "!defaults", "!fundefault"), // the last four: FunRandomWords, FunDefaultWords
        new("!nades", "<on|off>", "Grenade drops on or off", "Game", "!grenades", "!nade", "!he", "!drops", "!boom"),
        new("!catchup", "<on|off>", "Catch-up boosts on or off, until the map changes", "Game", "!catch", "!boost", "!boosts", "!comeback"),
        new("!time", "<10|60>", "Match length in minutes, from the next match", "Game", "!length", "!minutes", "!timelimit", "!roundtime", "!duration"),
        new("!newmaps", "<always|half|never>", "New maps in random picks", "Game", "!fresh", "!pool", "!mix", "!newmap"),
        new("!restart", "", "Restart a stuck server - twice to confirm", "Game", "!reboot", "!fix", "!unstuck", "!stuck", "!reset"),
        new("!menu", "", "Open the full menu", "Menu", "!gg", "!hud", "!os", "!os2", "!open"),
        new("!help", "", "The Help page, and this list", "Menu", "!commands", "!cmds", "!h", "!?", "!info"),
        new("!rules", "", "The rules", "Menu", "!rule", "!r", "!fair", "!howto", "!policy"),
        new("!how", "", "How it works: every chance in map picks", "Menu", "!howitworks", "!odds", "!chances", "!why", "!explain"),
        new("!sync", "", "The Sync page: what the map sync does now, its queue and its history", "Menu", "!status", "!dashboard"),
    ];

    /// <summary>!top, the old word: the map browser sorted by votes.</summary>
    public const string TopWord = "!top";

    /// <summary>!fun's aliases that run its Random and its Default (the owner's 2026-11-03): !randomize is !fun random, !defaults is !fun default.</summary>
    public static readonly string[] FunRandomWords = ["!randomize", "!funrandom"], FunDefaultWords = ["!defaults", "!fundefault"];

    /// <summary>The words that mean one thing or another, and the two primaries they can run: the owner's collisions, settled (Primary).</summary>
    public static readonly (string Word, string Otherwise, string When)[] Context = [("!new", "!next", "!newmaps")];

    /// <summary>The primaries that ask for another map: while an end-of-match vote is open they do nothing but say what to type (VoteOnly; the bridge refuses
    /// the same clicks, MapVote.WaitLine). !play is not one since 2026-11-03: while the vote is open it puts its map on the ballot, with the player's vote
    /// (VoteRound.cs), and !random puts the player's vote on Random. The commands that do not touch the map stay as they are.</summary>
    public static readonly string[] WaitForVote = ["!next", "!back", "!done"];

    static readonly Dictionary<string, string> aliases = AliasMap();

    static Dictionary<string, string> AliasMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in All)
            foreach (var a in c.Aliases) map.TryAdd(a, c.Word); // a word listed twice is what --selftest looks for; nothing here may throw in the plugin
        return map;
    }

    public static Command? Find(string word) => All.FirstOrDefault(c => c.Word.Equals(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>An option on the ballot, typed bare: one or two digits, 1 up to the most options a ballot can have (49 since 2026-11-03).</summary>
    public static bool IsBallotNumber(string s) => s.Length is 1 or 2 && s.All(char.IsAsciiDigit) && s[0] != '0' && int.Parse(s, System.Globalization.CultureInfo.InvariantCulture) <= MapVote.MaxOptions;

    /// <summary>The primary a typed word runs, by the owner's rules.</summary>
    static string Primary(string word, string arg)
    {
        if (word == "!new") return arg == "" ? "!next" : "!newmaps";
        var primary = aliases.GetValueOrDefault(word, word);
        return primary == "!maps" && arg != "" ? "!find" : primary;
    }

    /// <summary>The chat line as a command: None when it is no command (it does not start with ! or /), Unknown for a word that is none of
    /// ours, Usage when the command needs something it did not get. While a vote is open a bare number is a vote too (the line still shows
    /// normally, as every chat line does), and whatever asks for another map, or a !vote without a number on the ballot, is VoteOnly. Highest: the
    /// ballot's highest number, so a bare number it does not have stays plain chat.</summary>
    public static Parsed Parse(string? line, bool voteOpen = false, int highest = MapVote.MaxOptions)
    {
        var text = (line ?? "").Trim();
        bool OnBallot(string s) => IsBallotNumber(s) && int.Parse(s, System.Globalization.CultureInfo.InvariantCulture) <= highest;
        if (voteOpen && OnBallot(text)) return new(Kind.Vote, text, "!vote", $"!vote {text}");
        if (text.Length < 2 || text[0] is not ('!' or '/')) return new(Kind.None);
        var parts = text[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return new(Kind.None);
        var typed = "!" + parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        var word = Primary(typed, arg);
        if (voteOpen && word == "!vote" && arg == "") return new(Kind.Page, "Maps", word, typed != word ? word : "", typed != word ? text : ""); // a bare !vote: back to the vote (the owner's 2026-11-03)
        if (voteOpen && (WaitForVote.Contains(word) || (word == "!vote" && !OnBallot(arg)))) return new(Kind.VoteOnly, "", word);
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
            "!maps" => typed == TopWord ? new(Kind.Top, "Maps", word, "!maps, sorted by votes") : new(Kind.Page, "Maps", word),
            "!next" => new(Kind.Bridge, "next", word),          // the plugin turns it into the board's Next for this player (HudView.Command)
            "!random" => new(Kind.Bridge, "!next random", word), // the vote with this player's vote on Random (VoteRound.cs)
            "!back" => new(Kind.Bridge, "!previous", word),
            "!q" => new(Kind.Page, "Queue", word),
            "!cancel" => arg == "" || low == "all" ? new(Kind.Bridge, "!cancel all", word) : Hud.IsMapArg(low) ? new(Kind.Bridge, $"!cancel {low}", word) : Usage(),
            "!done" => new(Kind.Bridge, "!done", word),
            "!up" => OnMap("!up"),
            "!down" => DownReasons.Normal(low) is { } down ? new(Kind.Bridge, down == "" ? "!down" : $"!down {down}", word) : Usage(),
            "!toobig" => OnMap("!toobig"),
            "!ads" => OnMap("!ads"),
            "!vote" => IsBallotNumber(arg) ? new(Kind.Vote, arg, word) : Usage(),
            "!fun" => FunCommand(typed, low, word),
            "!catchup" => low is "on" or "off" ? new(Kind.CatchUp, low, word) : Usage(),
            "!nades" => low is "on" or "off" ? new(Kind.Nades, low, word)
                : Fun.Find("nades") is { } size && Fun.Find(size, low) is { } blast ? new(Kind.Fun, $"{size.Id} {blast.Id}", word) : Usage(),
            "!time" => low is "10" or "60" ? new(Kind.Bridge, $"!time {low}", word) : Usage(),
            "!newmaps" => low is "always" or "half" or "never" ? new(Kind.Bridge, $"!newmaps {low}", word) : Usage(),
            "!restart" => new(Kind.Bridge, "!restart", word),
            "!menu" => new(Kind.Page, "Start", word),
            "!help" => new(Kind.Commands, "Help", word),
            "!rules" => new(Kind.Page, "Rules", word),
            "!how" => new(Kind.Page, "How", word),
            "!sync" => new(Kind.Page, "Sync", word),
            _ when Fun.Find(word[1..]) is { } s => Fun.Find(s, low) is { } c ? new(Kind.Fun, $"{s.Id} {c.Id}", word) : Usage(),
            _ => new(Kind.Unknown, "", word),
        };
    }

    /// <summary>!fun alone is the page. !fun gravity low, the long way round, runs the setting's own word and says so; !fun nades on|off the
    /// grenade drops. A setting alone toggles nothing: that word's usage line.</summary>
    static Parsed FunCommand(string typed, string low, string word)
    {
        if (FunRandomWords.Contains(typed)) return new(Kind.FunAll, Fun.RandomWord, word, $"!fun {Fun.RandomWord}");   // the Fun page's Random
        if (FunDefaultWords.Contains(typed)) return new(Kind.FunAll, Fun.DefaultWord, word, $"!fun {Fun.DefaultWord}"); // ...and its Default
        var p = low.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p is [Fun.RandomWord] or [Fun.DefaultWord]) return new(Kind.FunAll, p[0], word);
        if (p.Length == 0) return new(Kind.Page, "Fun", word);
        if (p is ["nades", "on" or "off"]) return new(Kind.Nades, p[1], "!nades", $"!nades {p[1]}");
        if (Fun.Find(p[0]) is not { } s) return new(Kind.Usage, "", word);
        return p.Length == 2 && Fun.Find(s, p[1]) is { } c ? new(Kind.Fun, $"{s.Id} {c.Id}", $"!{s.Id}", $"!{s.Id} {c.Id}") : new(Kind.Usage, "", $"!{s.Id}");
    }

    /// <summary>What a command needs, for its usage line: "!vote &lt;1-6&gt;: your choice in the vote at a match's end". A fun setting's word too.</summary>
    public static string UsageOf(string word)
    {
        if (word == "!nades" && Find(word) is { } nades && Fun.Find("nades") is { } size)
            return $"!nades on|off: {nades.Words}  ·  !nades {ChoiceWords(size)}: {size.Words}";
        if (Find(word) is { } c) return $"{c.Word}{(c.Args != "" ? " " + c.Args : "")}: {c.Words}";
        return word.Length > 1 && Fun.Find(word[1..]) is { } s ? $"!{s.Id} {ChoiceWords(s)}: {s.Words}" : UnknownLine(word);
    }

    static string ChoiceWords(Fun.Setting s) => string.Join('|', s.Choices.Select(c => c.Id));

    /// <summary>The bridge's note for a player, as chat says it: a confirmation asks a click of a menu user, and of a typist the same line again
    /// (a broken map's Play, Previous, the restart: the bridge counts both alike).</summary>
    public static string InChat(string note) => note.Replace("Click Play again", "Type it again").Replace("Click Previous again", "Type it again").Replace("Click Restart again", "Type it again");

    /// <summary>The reply that teaches the primary by showing what was executed, or null when the player typed the primary.</summary>
    public static string? Teach(Parsed p) => p.Typed == "" ? null : $"You typed {Hud.Clean(p.Typed, 44)} - ran {Hud.Clean(p.Ran, 50)}";

    public const int LineMax = 120;

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
                if (line.Length + 1 + c.Word.Length > BesideWindow) { lines.Add(line); line = $"{g.Key}:"; }
                line += " " + c.Word;
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>What a bare !fun writes in chat beside the page, in lines that fit beside it: each setting's own word with a choice the page shows. What the
    /// page leaves out in another game mode (friendly fire's three, the grenade drops) it leaves out too.</summary>
    public static List<string> FunLines(bool armsRace)
    {
        var lines = new List<string> { "Every setting here has its own word:" };
        var line = "";
        foreach (var s in Fun.Settings.Where(s => armsRace || !Fun.ArmsRaceOnly(s)))
            foreach (var add in (armsRace && s.Id == "nades" ? new[] { "!nades off" } : []).Append($"!{s.Id} {s.Choices[1].Id}"))
            {
                if (line.Length + 2 + add.Length > BesideWindow) { lines.Add(line); line = ""; }
                line += (line == "" ? "" : "  ") + add;
            }
        if (line != "") lines.Add(line);
        return lines;
    }

    /// <summary>What chat says, to that player alone, to whatever does not vote while a vote is open: what to type, and that requests come back. Two
    /// lines that fit beside the window, which stands open for the vote.</summary>
    public static string[] VoteOnlyLines(int options) => [$"Vote now: type !vote 1-{options} or just the number", MapVote.RequestsReopen];

    /// <summary>What chat says for a word that is no command of ours.</summary>
    public static string UnknownLine(string word) => $"Unknown command {word} - !help lists them (and the menu: hold TAB, press E)";
}
