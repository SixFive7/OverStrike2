// --selftest, the chat commands (ChatCommands.cs): every alias runs its primary and teaches it, no word is two things but !maps with words, each
// fun setting has its own word, a bare number votes only while a vote is open, and the words are the same wherever they are said. The list below
// is the agreed one, kept here word for word: the table may not drift from it. !skip and its aliases are Next's (the vote for the next map), and
// so is !new now that the new-maps setting is gone; !fun has Random's and Default's words; !sync opens the Sync page; there is no queue (!q and
// !cancel are gone, !done's words are Next's), !toobig and !ads are !down big and !down ads, the Rules page's words are How it works', and !drops
// is the drops setting's own word.
using System.Text.RegularExpressions;

static partial class Program
{
    static readonly (string Primary, string[] Aliases)[] OwnersWords =
    [
        ("!play", ["!map", "!p", "!nominate", "!load", "!go", "!pick"]),
        ("!find", ["!search", "!s", "!lookup"]),                     // and !maps <words>
        ("!maps", ["!browse", "!list", "!maplist", "!m", "!top"]),
        ("!next", ["!n", "!rtv", "!change", "!skipmap", "!skip", "!voteskip", "!vs", "!boo", "!done", "!finish", "!end", "!release", "!letgo", "!over", "!new"]),
        ("!random", ["!roll", "!dice", "!rand", "!any", "!shuffle"]),
        ("!back", ["!previous", "!prev", "!last", "!undo", "!again"]),
        ("!up", ["!like", "!+", "!good", "!love", "!thumbsup"]),
        ("!down", ["!dislike", "!-", "!bad", "!meh", "!thumbsdown"]),
        ("!vote", ["!v", "!mv", "!choose"]),
        ("!fun", ["!set", "!settings", "!toggle", "!mode", "!randomize", "!funrandom", "!defaults", "!fundefault"]), // the last four: !fun random, !fun default
        ("!nades", ["!grenades", "!nade", "!he", "!boom"]),
        ("!catchup", ["!catch", "!boost", "!boosts", "!comeback"]), // the Fun page's Catch-up row in chat
        ("!time", ["!length", "!minutes", "!timelimit", "!roundtime", "!duration"]),
        ("!restart", ["!reboot", "!fix", "!unstuck", "!stuck", "!reset"]),
        ("!rematch", ["!restartmatch", "!matchrestart", "!restartgame", "!rr"]),
        ("!menu", ["!gg", "!hud", "!os", "!os2", "!open"]),
        ("!help", ["!commands", "!cmds", "!h", "!?", "!info"]),
        ("!how", ["!howitworks", "!odds", "!chances", "!why", "!explain", "!rules", "!rule", "!r", "!fair", "!howto", "!policy"]),
        ("!sync", ["!status", "!dashboard"]),
    ];

    /// <summary>A line that runs a primary with something it needs, for the checks below.</summary>
    static string SampleArg(string primary) => primary switch
    {
        "!play" => " 12", "!find" => " dust", "!vote" => " 2", "!nades" => " on", "!catchup" => " off", "!time" => " 10", _ => "",
    };

    /// <summary>A reply's words, without its colours: what the checks on the words read.</summary>
    static string? Taught(ChatCommands.Parsed p) => ChatCommands.Teach(p) is { } taught ? M.Plain(taught) : null;

    static void ChatSelfTest(Action<string, bool> Check)
    {
        // ---- the table: the agreed list, one primary per action ----
        var table = ChatCommands.All.Select(c => (c.Word, Aliases: c.Aliases.Order(StringComparer.Ordinal).ToList())).ToList();
        var owners = OwnersWords.Select(o => (Word: o.Primary, Aliases: o.Aliases.Order(StringComparer.Ordinal).ToList())).ToList();
        var drift = owners.Where(o => !table.Any(t => t.Word == o.Word && t.Aliases.SequenceEqual(o.Aliases))).Select(o => o.Word).Concat(table.Select(t => t.Word).Except(owners.Select(o => o.Word))).ToList();
        Check($"Chat: the table is the agreed list - {ChatCommands.All.Length} primaries, {ChatCommands.All.Sum(c => c.Aliases.Length)} aliases{(drift.Count > 0 ? "; differs on " + string.Join(" ", drift) : "")}", drift.Count == 0);

        // ---- every word is one thing ----
        var primaries = ChatCommands.All.Select(c => c.Word).ToList();
        var aliasWords = ChatCommands.All.SelectMany(c => c.Aliases).ToList();
        var funWords = Fun.Settings.Select(s => "!" + s.Id).ToList();
        var twice = primaries.Concat(aliasWords).GroupBy(w => w).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Check($"Chat: no word maps to two actions: {primaries.Count + aliasWords.Count} words, each once{(twice.Count > 0 ? "; twice: " + string.Join(" ", twice) : "")}", twice.Count == 0);
        var shadows = aliasWords.Where(a => primaries.Contains(a) || funWords.Contains(a)).ToList();
        Check($"Chat: no alias shadows a primary or a fun word ({funWords.Count} of those){(shadows.Count > 0 ? ": " + string.Join(" ", shadows) : "")}", shadows.Count == 0);
        var sharedFun = funWords.Where(primaries.Contains).ToList();
        var blasts = Fun.Find("nades")!.Choices.Select(c => c.Id).ToList();
        Check("Chat: two fun words are also primaries - !catchup, the setting's own word, and !nades: on|off are the grenade drops, its setting's own choices the blast, and the two sets do not meet",
            sharedFun.Order(StringComparer.Ordinal).SequenceEqual(["!catchup", "!nades"]) && !blasts.Contains("on") && !blasts.Contains("off"));
        Check("Chat: no word means one thing or another by what follows it any more - !new was one, until the new-maps setting went; it is Next's now",
            ChatCommands.Find("!newmaps") == null);

        // ---- an alias runs the action and teaches the primary ----
        var wrong = new List<string>();
        foreach (var o in OwnersWords)
        {
            var arg = SampleArg(o.Primary);
            var own = ChatCommands.Parse(o.Primary + arg);
            if (own.Kind is ChatCommands.Kind.Unknown or ChatCommands.Kind.Usage or ChatCommands.Kind.None || own.Typed != "" || own.Ran != o.Primary + arg || own.Name != o.Primary) wrong.Add(o.Primary);
            foreach (var a in o.Aliases.Where(a => a != ChatCommands.TopWord && !ChatCommands.FunRandomWords.Contains(a) && !ChatCommands.FunDefaultWords.Contains(a)))
            {
                var p = ChatCommands.Parse(a + arg);
                if (p.Kind != own.Kind || p.Arg != own.Arg || p.Name != own.Name || p.Ran != own.Ran || p.Typed != a + arg
                    || Taught(p) != $"You typed {a}{arg} - Ran {o.Primary}{arg}") wrong.Add(a);
            }
        }
        Check($"Chat: every alias does what its primary does, and the reply teaches it: You typed !map 12 - Ran !play 12{(wrong.Count > 0 ? "; not " + string.Join(" ", wrong) : "")}",
            wrong.Count == 0 && Taught(ChatCommands.Parse("!map 12")) == "You typed !map 12 - Ran !play 12");
        Check("Chat: !fun random and !fun default are the Fun page's Random and Default; !randomize and !funrandom, !defaults and !fundefault run them and teach them, as !set does",
            ChatCommands.Parse("!fun random") is { Kind: ChatCommands.Kind.FunAll, Arg: Fun.RandomWord, Typed: "" } && ChatCommands.Parse("!FUN DEFAULT") is { Kind: ChatCommands.Kind.FunAll, Arg: Fun.DefaultWord, Typed: "" }
            && ChatCommands.FunRandomWords.All(w => ChatCommands.Parse(w) is { Kind: ChatCommands.Kind.FunAll, Arg: Fun.RandomWord, Name: "!fun" } p && Taught(p) == $"You typed {w} - Ran !fun random")
            && ChatCommands.FunDefaultWords.All(w => ChatCommands.Parse(w) is { Kind: ChatCommands.Kind.FunAll, Arg: Fun.DefaultWord, Name: "!fun" } p && Taught(p) == $"You typed {w} - Ran !fun default")
            && ChatCommands.Parse("!set random") is { Kind: ChatCommands.Kind.FunAll, Arg: Fun.RandomWord, Ran: "!fun random", Typed: "!set random" }
            && ChatCommands.Parse("!fun random", voteOpen: true) is { Kind: ChatCommands.Kind.FunAll } && ChatCommands.Parse("!fun randomly") is { Kind: ChatCommands.Kind.Usage, Name: "!fun" });
        Check("Chat: !sync opens the Sync page; !status and !dashboard teach it",
            ChatCommands.Parse("!sync") is { Kind: ChatCommands.Kind.Page, Arg: "Sync", Typed: "" } && Taught(ChatCommands.Parse("!status")) == "You typed !status - Ran !sync"
            && ChatCommands.Parse("!dashboard") is { Kind: ChatCommands.Kind.Page, Arg: "Sync", Ran: "!sync" });
        Check("Chat: !skip and its aliases are Next's now - the vote for the next map, Keep playing on it; the old skip vote is gone",
            new[] { "!skip", "!voteskip", "!vs", "!boo" }.All(w => ChatCommands.Parse(w) is { Kind: ChatCommands.Kind.Bridge, Arg: "next", Name: "!next" } p && Taught(p) == $"You typed {w} - Ran !next")
            && ChatCommands.Find("!skip") == null && ChatCommands.Parse("!random") is { Kind: ChatCommands.Kind.Bridge, Arg: "!next random" } && Hud.IsAction("!next random"));
        Check("Chat: the primary itself teaches nothing, however it is typed; the reply keeps what was typed and always fits a chat line",
            Taught(ChatCommands.Parse("!play 12")) == null && Taught(ChatCommands.Parse("/PLAY 12")) == null
            && Taught(ChatCommands.Parse("/MAP 12")) == "You typed /MAP 12 - Ran !play 12"
            && Taught(ChatCommands.Parse("!map " + new string('x', 300)))!.Length <= Messages.ChatLineMax);
        Check("Chat: !like 12, !dislike 12 big and the rest carry a map's number or an official map's name; anything else gets the primary's usage; !toobig and !ads are gone (!down big, !down ads)",
            ChatCommands.Parse("!like 12") is { Kind: ChatCommands.Kind.Bridge, Arg: "!up 12", Ran: "!up 12" } && ChatCommands.Parse("!dislike AR_shoots big") is { Kind: ChatCommands.Kind.Bridge, Arg: "!down ar_shoots big" }
            && ChatCommands.Parse("!like dust") is { Kind: ChatCommands.Kind.Usage, Name: "!up" } && ChatCommands.Parse("!toobig 12") is { Kind: ChatCommands.Kind.Unknown } && ChatCommands.Parse("!ads") is { Kind: ChatCommands.Kind.Unknown }
            && ChatCommands.Parse("!map") is { Kind: ChatCommands.Kind.Usage, Name: "!play", Typed: "" }
            && M.Plain(ChatCommands.UsageOf(ChatCommands.Parse("!map").Name)).StartsWith("!play <number"));

        var broken = new MapInfo(12, 1, "aim_x", 1);
        Check("Chat: a confirmation's note says to type the line again, as a second click and a second line count alike: the restart (and its aliases), a risky map's Play and Previous; every other note is left alone",
            M.Plain(ChatCommands.InChat(PickConfirmNote(broken, "is on the broken list"))) == $"{Label(broken)} is on the broken list. Type it again within {PickConfirmSeconds} s to vote anyway"
            && ChatCommands.InChat(PickConfirmNote(broken, "is on the broken list", "Previous")).Contains("Type it again within")
            && ChatCommands.InChat("A restart kicks everyone for about a minute. Click Restart again within 30 s to confirm") == "A restart kicks everyone for about a minute. Type it again within 30 s to confirm"
            && ChatCommands.InChat("You liked #1 x, now +1 -0") == "You liked #1 x, now +1 -0");

        // ---- the context rules ----
        Check("Chat: outside a vote !pick is a plain alias of !play",
            ChatCommands.Parse("!pick 3") is { Kind: ChatCommands.Kind.Play, Arg: "3", Ran: "!play 3", Typed: "!pick 3" } && ChatCommands.Parse("!pick dust") is { Kind: ChatCommands.Kind.Play, Arg: "dust" }
            && ChatCommands.Parse("!pick") is { Kind: ChatCommands.Kind.Usage, Name: "!play" });
        Check("Chat: while a vote is open !play and !back put their map on the ballot (the bridge adds it), !random puts the vote on Random, !next and !done bring the vote up - none is a VoteOnly",
            ChatCommands.Parse("!play 12", voteOpen: true) is { Kind: ChatCommands.Kind.Play, Arg: "12" } && ChatCommands.Parse("!map dust", voteOpen: true) is { Kind: ChatCommands.Kind.Play }
            && ChatCommands.Parse("!back", voteOpen: true) is { Kind: ChatCommands.Kind.Bridge, Arg: "!previous" } && ChatCommands.Parse("!next", voteOpen: true) is { Kind: ChatCommands.Kind.Bridge, Arg: "next" }
            && ChatCommands.Parse("!done", voteOpen: true) is { Kind: ChatCommands.Kind.Bridge, Arg: "next", Ran: "!next", Typed: "!done" }
            && ChatCommands.Parse("!random", voteOpen: true) is { Kind: ChatCommands.Kind.Bridge, Arg: "!next random" } && ChatCommands.Parse("!dice", voteOpen: true) is { Kind: ChatCommands.Kind.Bridge, Arg: "!next random" });
        Check("Chat: while a vote is open !vote and its aliases and the bare number vote; a wrong number gets the same answer, and a bare one that is no option stays plain chat",
            ChatCommands.Parse("!vote 3", voteOpen: true, highest: 6) is { Kind: ChatCommands.Kind.Vote, Arg: "3" } && ChatCommands.Parse("!mv 3", voteOpen: true, highest: 6) is { Kind: ChatCommands.Kind.Vote, Arg: "3", Typed: "!mv 3" }
            && ChatCommands.Parse("3", voteOpen: true, highest: 6) is { Kind: ChatCommands.Kind.Vote, Arg: "3" }
            && new[] { "!vote 9", "!vote dust", "!v 0", "!choose 12" }.All(w => ChatCommands.Parse(w, voteOpen: true, highest: 6).Kind == ChatCommands.Kind.VoteOnly)
            && ChatCommands.Parse("!vote", voteOpen: true) is { Kind: ChatCommands.Kind.Page, Arg: "Maps", Typed: "" } && ChatCommands.Parse("!v", voteOpen: true) is { Kind: ChatCommands.Kind.Page, Arg: "Maps", Ran: "!vote", Typed: "!v" }
            && ChatCommands.Parse("!vote") is { Kind: ChatCommands.Kind.Page, Arg: "Maps" } // a bare !vote brings a player back to the vote; outside one it opens the maps, where one starts
            && ChatCommands.Parse("9", voteOpen: true, highest: 6).Kind == ChatCommands.Kind.None && ChatCommands.Parse("12", voteOpen: true, highest: 14).Kind == ChatCommands.Kind.Vote);
        var untouched = new[] { "!up", "!down big", "!down 12 ads", "!maps", "!find dust", "!fun", "!gravity low", "!nades off", "!drops on", "!catchup off", "!time 10", "!restart", "!rematch", "!menu", "!help", "!rules", "!how" };
        var touched = untouched.Where(w => ChatCommands.Parse(w, voteOpen: true) != ChatCommands.Parse(w)).ToList();
        Check($"Chat: the commands that do not touch the map stay as they are while a vote is open{(touched.Count > 0 ? "; not " + string.Join(" | ", touched) : "")}",
            touched.Count == 0 && untouched.All(w => ChatCommands.Parse(w, voteOpen: true).Kind != ChatCommands.Kind.VoteOnly));
        var wait6 = ChatCommands.VoteOnlyLines(6).Select(M.Plain).ToArray();
        Check($"Chat: what a wrong number gets fits beside the window and names what to type - or, without card numbers, to click: {string.Join(" / ", wait6)}",
            wait6.Length == 1 && wait6.All(l => l.Length <= ChatCommands.BesideWindow) && wait6[0].Contains("!vote 1-6") && wait6[0].Contains("just the number")
            && ChatCommands.Parse(Regex.Match(wait6[0], @"!vote \d").Value, voteOpen: true).Kind == ChatCommands.Kind.Vote
            && ChatCommands.VoteOnlyLines(0).Select(M.Plain).ToArray() is [var click] && click.Contains("click a card") && click.Length <= ChatCommands.BesideWindow);
        Check("Chat: !new is an alias of !next, with or without words after it; !newmaps and its aliases are no commands any more",
            ChatCommands.Parse("!new") is { Kind: ChatCommands.Kind.Bridge, Arg: "next", Ran: "!next", Typed: "!new" }
            && ChatCommands.Parse("!new half") is { Kind: ChatCommands.Kind.Bridge, Arg: "next", Name: "!next" }
            && new[] { "!newmaps half", "!fresh", "!pool always", "!mix", "!newmap never" }.All(w => ChatCommands.Parse(w).Kind == ChatCommands.Kind.Unknown));
        Check("Chat: !maps with words is !find, its aliases too; alone it is the browser; !top the browser by votes",
            ChatCommands.Parse("!maps dust") is { Kind: ChatCommands.Kind.Find, Arg: "dust", Ran: "!find dust", Typed: "!maps dust" }
            && ChatCommands.Parse("!m dust") is { Kind: ChatCommands.Kind.Find, Arg: "dust" }
            && ChatCommands.Parse("!maps") is { Kind: ChatCommands.Kind.Page, Arg: "Maps", Typed: "" }
            && ChatCommands.Parse("!top") is { Kind: ChatCommands.Kind.Top, Arg: "Maps", Typed: "!top" }
            && Taught(ChatCommands.Parse("!top")) == "You typed !top - Ran !maps, sorted by likes");
        Check("Chat: !rtv and !change are the board's Next: they start a vote",
            ChatCommands.Parse("!rtv") is { Kind: ChatCommands.Kind.Bridge, Arg: "next", Ran: "!next" } && ChatCommands.Parse("!change") is { Kind: ChatCommands.Kind.Bridge, Arg: "next" });

        // ---- each fun setting's own word ----
        var funWrong = new List<string>();
        foreach (var s in Fun.Settings)
        {
            foreach (var c in s.Choices)
            {
                var want = $"{s.Id} {c.Id}";
                if (ChatCommands.Parse($"!{want}") is not { Kind: ChatCommands.Kind.Fun, Typed: "" } own || own.Arg != want || own.Ran != $"!{want}") funWrong.Add($"!{want}");
                if (ChatCommands.Parse($"!fun {want}") is not { Kind: ChatCommands.Kind.Fun } fun || fun.Arg != want || fun.Ran != $"!{want}" || fun.Typed != $"!fun {want}") funWrong.Add($"!fun {want}");
                if (ChatCommands.Parse($"!set {want}") is not { Kind: ChatCommands.Kind.Fun } set || set.Arg != want || Taught(set) != $"You typed !set {want} - Ran !{want}") funWrong.Add($"!set {want}");
            }
            if (ChatCommands.Parse($"!{s.Id}").Kind != ChatCommands.Kind.Usage || ChatCommands.Parse($"!{s.Id} nonsense").Kind != ChatCommands.Kind.Usage
                || M.Plain(ChatCommands.UsageOf($"!{s.Id}")).Length > Messages.ChatLineMax || !M.Plain(ChatCommands.UsageOf($"!{s.Id}")).StartsWith($"!{s.Id} ")) funWrong.Add($"!{s.Id}");
        }
        Check($"Chat: each fun setting is a word of its own, with its choices: !gravity low, !vampire on; !fun gravity low keeps working and teaches the short word; a word alone or a wrong choice gets its usage{(funWrong.Count > 0 ? "; not " + string.Join(" | ", funWrong) : "")}",
            funWrong.Count == 0 && ChatCommands.Parse("!VAMPIRE ON") is { Kind: ChatCommands.Kind.Fun, Arg: "vampire on" });
        Check("Chat: the grenade drops, a fun setting of their own: !drops on|off, and as before !nades on|off and !fun nades on|off with all of its aliases; the same word's blast choices are Big grenades; alone, a usage line naming both",
            ChatCommands.Parse("!nades off") is { Kind: ChatCommands.Kind.Fun, Arg: "drops off", Typed: "" } && ChatCommands.Parse("!fun nades on") is { Kind: ChatCommands.Kind.Fun, Arg: "drops on", Ran: "!nades on", Typed: "!fun nades on" }
            && ChatCommands.Parse("!boom off") is { Kind: ChatCommands.Kind.Fun, Arg: "drops off", Ran: "!nades off" } && ChatCommands.Parse("!drops on") is { Kind: ChatCommands.Kind.Fun, Arg: "drops on", Typed: "" }
            && ChatCommands.Parse("!catchup off") is { Kind: ChatCommands.Kind.Fun, Arg: "catchup off" } && ChatCommands.Parse("!boost on") is { Kind: ChatCommands.Kind.Fun, Arg: "catchup on", Ran: "!catchup on" }
            && ChatCommands.Parse("!nades big") is { Kind: ChatCommands.Kind.Fun, Arg: "nades big", Typed: "" }
            && ChatCommands.Parse("!grenades huge") is { Kind: ChatCommands.Kind.Fun, Arg: "nades huge", Ran: "!nades huge" } && ChatCommands.Parse("!nades") is { Kind: ChatCommands.Kind.Usage, Name: "!nades" }
            && M.Plain(ChatCommands.UsageOf("!nades")) is { Length: <= Messages.ChatLineMax } nu && nu.Contains("!nades on|off") && nu.Contains("!nades normal|big|huge"));
        Check("Chat: !fun alone opens the page; an unknown setting its usage, never a blank",
            ChatCommands.Parse("!fun") is { Kind: ChatCommands.Kind.Page, Arg: "Fun", Typed: "" } && ChatCommands.Parse("!mode") is { Kind: ChatCommands.Kind.Page, Arg: "Fun", Ran: "!fun", Typed: "!mode" }
            && ChatCommands.Parse("!fun nonsense on") is { Kind: ChatCommands.Kind.Usage, Name: "!fun" } && ChatCommands.Parse("!fun gravity") is { Kind: ChatCommands.Kind.Usage, Name: "!gravity" });
        var arms = ChatCommands.FunLines(true).Select(M.Plain).ToList();
        var other = ChatCommands.FunLines(false).Select(M.Plain).ToList();
        var armsWords = Regex.Matches(string.Join(" ", arms), @"!(\S+) (\S+)").Select(m => (Word: m.Groups[1].Value, Choice: m.Groups[2].Value)).ToList();
        Check($"Chat: a bare !fun's {arms.Count} lines fit beside the window it opens ({ChatCommands.BesideWindow} characters) and name every setting's word, each with a choice that runs: {string.Join("  ", arms.Skip(1).Take(1))} ...",
            arms.All(l => l.Length <= ChatCommands.BesideWindow) && other.All(l => l.Length <= ChatCommands.BesideWindow) && Fun.Settings.All(s => armsWords.Any(w => w.Word == s.Id))
            && armsWords.All(w => ChatCommands.Parse($"!{w.Word} {w.Choice}").Kind == ChatCommands.Kind.Fun) && armsWords.Contains(("drops", "off")) && armsWords.Contains(("catchup", "off")) && armsWords.Contains(("nades", "big")));
        Check("Chat: outside Arms Race they leave out what the page leaves out - friendly fire's three, the grenade drops and catch-up, not the blast",
            string.Join(" ", other) is var o2 && !o2.Contains("!ff ") && !o2.Contains("!instakill") && !o2.Contains("!ffnades") && o2.Contains("!nades big") && !o2.Contains("!drops") && !o2.Contains("!catchup"));
        var everyWord = ChatCommands.All.Select(c => c.Words + " " + c.Args).Concat(arms).Concat(Hud.Help.SelectMany(g => g.Rows).Select(r => r.Words)).ToList();
        Check("Chat: nothing teaches !fun <setting> <choice>: the short word is no alias of it, and every word a command's own line names runs",
            !everyWord.Any(w => Regex.IsMatch(w, @"!fun (?!random\b|default\b)\w")) && ChatCommands.All.SelectMany(c => Regex.Matches(c.Words, @"![a-z]+ ?[a-z]*").Select(m => m.Value.Trim())).All(w => ChatCommands.Parse(w).Kind is not (ChatCommands.Kind.Unknown or ChatCommands.Kind.Usage)));

        // ---- !help lists primaries only ----
        var helpLines = ChatCommands.HelpLines().Select(M.Plain).ToList();
        var listed = helpLines.SelectMany(l => Regex.Matches(l, @"!\S+").Select(m => m.Value)).ToList();
        Check($"Chat: !help's {helpLines.Count} lines list the {listed.Count} primaries, no alias, and fit beside the window it opens ({ChatCommands.BesideWindow} characters)",
            listed.Order().SequenceEqual(primaries.Order()) && helpLines.All(l => l.Length <= ChatCommands.BesideWindow));

        // ---- a bare number is a vote, while a vote is open ----
        Check($"Chat: with card numbers, while a vote is open a bare number 1 to the ballot's highest (at most {MapVote.MaxMaps}) is a vote, with no reply about aliases; without a vote it is plain chat; a Workshop id is never one; without numbers (highest 0) no number is",
            ChatCommands.Parse("3", voteOpen: true, highest: 6) is { Kind: ChatCommands.Kind.Vote, Arg: "3", Name: "!vote", Typed: "" } && ChatCommands.Parse(" 6 ", voteOpen: true, highest: 6) is { Kind: ChatCommands.Kind.Vote, Arg: "6" }
            && ChatCommands.Parse("3").Kind == ChatCommands.Kind.None && new[] { "0", "7", "12", "3 dust", "3631955210", "gg" }.All(x => ChatCommands.Parse(x, voteOpen: true, highest: 6).Kind == ChatCommands.Kind.None)
            && ChatCommands.Parse($"{MapVote.MaxMaps}", voteOpen: true).Kind == ChatCommands.Kind.Vote && ChatCommands.Parse($"{MapVote.MaxMaps + 1}", voteOpen: true).Kind == ChatCommands.Kind.None
            && WorkshopCard.IdInChat("3") == null && ChatCommands.Parse("3", voteOpen: true, highest: 0).Kind == ChatCommands.Kind.None
            && ChatCommands.Parse("!vote 3", voteOpen: true, highest: 0).Kind == ChatCommands.Kind.VoteOnly && M.Plain(ChatCommands.VoteOnlyLines(0)[0]).Contains("click a card"));
        Check("Chat: !vote takes the same numbers; its aliases teach it",
            ChatCommands.Parse("!vote 3") is { Kind: ChatCommands.Kind.Vote, Arg: "3" } && ChatCommands.Parse("!mv 3") is { Kind: ChatCommands.Kind.Vote, Ran: "!vote 3", Typed: "!mv 3" }
            && ChatCommands.Parse("!choose 77") is { Kind: ChatCommands.Kind.Usage, Name: "!vote" } && ChatCommands.Parse("!vote") is { Kind: ChatCommands.Kind.Page, Arg: "Maps" });
        Check("A card click says nothing in chat while the cards have no numbers (there is no typed vote to learn); with numbers, what a typed vote is",
            HudVote.ChatWords(3) == "" && (MapVote.Numbers = MapVote.Numbering.Fixed) == MapVote.Numbering.Fixed && HudVote.ChatWords(3) == "!vote 3"
            && ChatCommands.Parse(HudVote.ChatWords(3)) is { Kind: ChatCommands.Kind.Vote, Arg: "3", Typed: "" } && (MapVote.Numbers = MapVote.Numbering.None) == MapVote.Numbering.None);
        var ballot = VoteState.Decode("mode\tarmsrace\tArms Race\nmv\t1793500000\topen\t42\t3\t2\tmid\t0\t1\nmvo\t1\tarmsrace\tpicked\t12\t3084291314\t#12 aim_x\t17 MB\t1\tAlice\nmvo\t2\tdeathmatch\trolled\tde_dust2\tde_dust2\tde_dust2 (official)\t\t0\t\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t0\t\nmvv\t1001\t1\n");
        var drawn = HudVote.Cards(ballot, 1001, null);
        var cards = drawn.Texts;
        string T(string id) => cards.Single(x => x.Id == id).Text;
        Check("The ballot's cards carry no number: the mode on the picture, Vote on the button, Random's button its votes; the line under them says to click, and how to take a vote back",
            T("gg_m2_rank") == "Deathmatch" && T("gg_m3_rank") == "" && drawn.Vars.Single(x => x.Name == HudVote.RandomVar).Text == "Random  ·  0 votes"
            && T("gg_m1_play_text") == "Your vote" && T("gg_m2_play_text") == "Vote" && !T("gg_pg_text").Contains("number") && T("gg_pg_text").Contains("yours to take it back")
            && HudVote.Cards(ballot, 1002, null).Texts.Single(x => x.Id == "gg_pg_text").Text == "Click a card's Vote");

        // ---- a command that opens a page: its words are on it at once, as for everybody (no content has to prove anything first) ----
        Check("A command that opens How, Fun, Help or Sync opens that page of the menu; its words go to every game alike, and the older contents' c31 and c32 are on for everybody",
            ChatCommands.Parse("!fun") is { Kind: ChatCommands.Kind.Page, Arg: "Fun" } && ChatCommands.Parse("!how") is { Kind: ChatCommands.Kind.Page, Arg: "How" }
            && ChatCommands.Parse("!help") is { Kind: ChatCommands.Kind.Commands, Arg: "Help" } && ChatCommands.Parse("!rules") is { Kind: ChatCommands.Kind.Page, Arg: "How" }
            && HudView.StaticClasses().Contains((HudView.Screen, "c31", true)) && HudView.StaticClasses().Contains((HudView.Screen, "c32", true))
            && HudView.StaticTexts().Any(x => x.Id == "gg_h1_title") && HudView.StaticTexts().Any(x => x.Id == HudMenu.FunNote));

        // ---- the words are the same wherever they are said ----
        const string referenceFile = "../docs/reference.md", readmeFile = "../README.md";
        if (File.Exists(referenceFile))
        {
            var doc = File.ReadAllText(referenceFile);
            var section = Regex.Match(doc, @"## The menu and the commands(.*?)\n## ", RegexOptions.Singleline).Groups[1].Value;
            var undocumented = primaries.Concat(aliasWords).Where(w => !section.Contains($"`{w}") ).ToList();
            var codeWords = Regex.Matches(section, @"`(![^\s`]+)").Select(m => m.Groups[1].Value).Distinct().Where(w => !primaries.Contains(w) && !aliasWords.Contains(w) && !funWords.Contains(w)).ToList();
            Check($"docs/reference.md's table has every primary and every alias, and no other command{(undocumented.Count + codeWords.Count > 0 ? ": missing " + string.Join(" ", undocumented) + " unknown " + string.Join(" ", codeWords) : "")}",
                undocumented.Count == 0 && codeWords.Count == 0);
            var row = ChatCommands.All.Where(c => !Regex.IsMatch(section, $@"^\| `{Regex.Escape(c.Word)}[ `][^\n]*\| {string.Join(" ", c.Aliases.Select(a => Regex.Escape($"`{a}`")))}[ ,|]", RegexOptions.Multiline)).Select(c => c.Word).ToList();
            Check($"...each alias on its own primary's row, in the table's order{(row.Count > 0 ? ": not " + string.Join(" ", row) : "")}", row.Count == 0);
            Check("...and it says the rules: what ran, the context words, the fun words, the vote's bare number, what a vote does to !play and !back, the printed card click, the reasons",
                section.Contains("You typed !map 12 - Ran !play 12") && Regex.IsMatch(section, @"While a vote is open.*?`!play`.*?`!back`", RegexOptions.Singleline) && doc.Contains($"!down <number> <reason>") && DownReasons.All.All(r => doc.Contains($"`{r.Word}`") && doc.Contains(r.Label))
                && doc.Contains($"popup listing them for {DownReasons.PopupSeconds} s"));
        }
        if (File.Exists(readmeFile))
        {
            var readme = File.ReadAllText(readmeFile);
            var named = Regex.Matches(readme, @"`(![^\s`]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
            var unknown = named.Where(w => ChatCommands.Parse(w + " on").Kind == ChatCommands.Kind.Unknown).ToList();
            Check($"The README's chat words are real ({string.Join(" ", named)}), and it names !help{(unknown.Count > 0 ? ": not " + string.Join(" ", unknown) : "")}", unknown.Count == 0 && named.Contains("!help"));
        }
        ChatColoursSelfTest(Check);
    }

    /// <summary>Chat's colours as letters: π the chat's own, κ a command, α an argument, ν a value, ρ a refusal, δ what worked, τ the team of the player named Alice.</summary>
    static readonly M.Palette FakeColours = new('π', 'κ', 'α', 'ν', 'ρ', 'δ', name => name == "Alice" ? 'τ' : null);

    /// <summary>A marked line as chat shows it, with letters for its colours (FakeColours).</summary>
    static string Coloured(string marked) => M.Render(marked, FakeColours);

    /// <summary>The functional colours: every line chat can answer a command with has its parts apart, its sections start with a capital, it fits, and
    /// what is Lime can be typed. The words themselves are checked above.</summary>
    static void ChatColoursSelfTest(Action<string, bool> Check)
    {
        string T(string line) => Coloured(ChatCommands.Teach(ChatCommands.Parse(line))!);
        string U(string word) => Coloured(ChatCommands.UsageOf(word));

        Check("Chat's colours: a whole line - a name in its team's colour, an argument, a value, the words between in the chat's own",
            Coloured($"{M.N("Alice")} set {M.A("gravity")} to {M.V("Moon")} - Until the map changes") == "τAliceπ set αgravityπ to νMoonπ - Until the map changes");
        Check("...a name nobody on the server has stays in the colour around it; a refusal's and a done line's words have their own, their parts as ever",
            Coloured($"{M.N("Bob")} set it") == "Bobπ set it" && Coloured(M.Refused($"No {M.A("x")} here")) == "ρNo αxρ here" && Coloured(M.Done($"Your vote: {M.A("y")}")) == "δYour vote: αyδ");
        Check("...a name goes by what the line shows of it: whole, clipped with a ~, or with its \" said as '; never another player's",
            ChatCommands.IsName("Alice", "Alice") && ChatCommands.IsName("Alice Longn~", "Alice Longname") && ChatCommands.IsName("Al'ice", "Al\"ice")
            && !ChatCommands.IsName("Bob", "Alice") && !ChatCommands.IsName("Bob~", "Alice") && !ChatCommands.IsName("~", "Alice"));

        Check("Teaching an alias: the commands Lime, what they are about LightBlue",
            T("!map 12") == "You typed κ!mapπ α12π - Ran κ!playπ α12π" && T("!like 12") == "You typed κ!likeπ α12π - Ran κ!upπ α12π" && T("!maps dust") == "You typed κ!mapsπ αdustπ - Ran κ!findπ αdustπ");
        Check("...a value Gold: a fun setting's choice, a match length, a reason, and the setting itself the argument when it is named the long way round",
            T("!fun gravity low") == "You typed κ!funπ αgravityπ νlowπ - Ran κ!gravityπ νlowπ" && T("!boom off") == "You typed κ!boomπ νoffπ - Ran κ!nadesπ νoffπ"
            && T("!length 60") == "You typed κ!lengthπ ν60π - Ran κ!timeπ ν60π" && T("!dislike 12 big") == "You typed κ!dislikeπ α12π νbigπ - Ran κ!downπ α12π νbigπ"
            && T("!randomize") == "You typed κ!randomizeπ - Ran κ!funπ νrandomπ");
        Check("...and !top's words after its command are words", T("!top") == "You typed κ!topπ - Ran κ!mapsπ, sorted by likes" && T("!status") == "You typed κ!statusπ - Ran κ!syncπ");

        Check("A usage line: the command Lime; what it needs LightBlue, the few it can be Gold; a command or a reason its words name likewise",
            U("!play") == "κ!playπ α<number, name or Workshop ID>π: Put a map to the vote, with your vote on it" && U("!time") == "κ!timeπ ν<10|60>π: Match length in minutes, this match too"
            && U("!gravity") == $"κ!gravityπ νnormal|low|moonπ: {Fun.Find("gravity")!.Words}" && U("!down") == $"κ!downπ α[number] [reason]π: Dislike it, and why: ν{DownReasons.Words}π"
            && U("!fun") == "κ!funπ: The Fun page, each setting's own word (κ!gravityπ νlowπ), κ!funπ νrandomπ and κ!funπ νdefaultπ"
            && U("!nades") == $"κ!nadesπ νon|offπ: Grenade drops on or off  ·  κ!nadesπ νnormal|big|hugeπ: {Fun.Find("nades")!.Words}");
        Check("!help's list: the commands Lime beside the words of their group; a bare !fun's: each word Lime with a choice in Gold",
            Coloured(ChatCommands.HelpLines()[0]).StartsWith("Maps: κ!playπ κ!findπ κ!mapsπ") && Coloured(ChatCommands.FunLines(true)[0]) == "Every setting here has its own word:"
            && Coloured(ChatCommands.FunLines(true)[1]).StartsWith("κ!gravityπ νlowπ  κ!jumpπ νhighπ"));
        Check("A refusal is LightRed, and what it does not know is no command: Unknown command !foo is an argument, !help is the Lime in it",
            Coloured(ChatCommands.UnknownLine("!foo")) == "ρUnknown command α!fooρ - κ!helpρ lists them (and the menu: hold TAB, press E)"
            && Coloured(ChatCommands.VoteOnlyLines(6)[0]) == "ρVote now: type κ!voteρ ν1-6ρ or just the number");
        var moon = Fun.Find(Fun.Find("gravity")!, "moon")!;
        var jump = Fun.Find("jump")!;
        Check("A clash's refusal in chat: its settings arguments, their choices values; the plain words are the same words, for the window and the page",
            Coloured(Fun.RefusalLine(jump, Fun.Find(jump, "high")!, c => c == "sv_gravity" ? "200" : null)!) == "ραSuper jumpρ cannot be νHighρ while αGravityρ is νMoonρ: together they throw players out of most maps"
            && Fun.Refusal(jump, Fun.Find(jump, "high")!, c => c == "sv_gravity" ? "200" : null) == M.Plain(Fun.RefusalLine(jump, Fun.Find(jump, "high")!, c => c == "sv_gravity" ? "200" : null)!) && moon.Label == "Moon");
        Check("What worked is Green, what only informs the chat's own colour",
            Coloured(ChatReply.MenuOn("Maps")) == "δThe menu is open on αMapsδ" && Coloured(ChatReply.YourVote("#12 aim_x")) == "δYour vote: α#12 aim_xδ"
            && Coloured(ChatReply.Already(jump, Fun.Find(jump, "high")!)) == "αSuper jumpπ: νHighπ already" && Coloured(ChatReply.Matches(3)) == "ν3π maps match - The menu shows them: click αPlayπ on one");
        Check("The bridge's refusals of a map are LightRed too, the map an argument and a mode's limits values",
            Coloured(BlockedRefusal(new MapInfo(0, 5, "x", 0))) == "ραxρ (5) is on the blocked list - It is blocked." && Coloured(PackRefusal(new MapInfo(0, 6, "y", 0))) == $"ραyρ {NotAMap}"
            && Coloured(ModeFullLine(Modes.Wingman, 5)).StartsWith($"ρνWingmanρ takes ν{Modes.Wingman.MaxPlayers} playersρ, and ν5ρ are on - Not this map now"));

        var wire = new HudState();
        wire.Says.Add((7, 1001, ChatCommands.UnknownLine("!foo")));
        Check("The marks survive the wire from the bridge to the plugin", HudState.Decode(wire.Encode())?.Says is [var said] && said == (7, 1001, ChatCommands.UnknownLine("!foo")));

        var sneak = M.C("x")[0]; // a mark, as a player could paste one into a line or a name
        Check("A player's own words colour nothing: a mark in what they typed is taken out",
            T($"!map {sneak}12") == T("!map 12") && Coloured(ChatCommands.UnknownLine($"!f{sneak}oo")) == Coloured(ChatCommands.UnknownLine("!foo")) && Coloured(ChatReply.NoMatch($"{sneak}x")) == Coloured(ChatReply.NoMatch("x"))
            && Coloured(ChatReply.Searching($"{sneak}x")) == Coloured(ChatReply.Searching("x")));
        var longRefusal = M.Refused($"{M.A(new string('n', 70))} {string.Join(' ', Enumerable.Repeat("word", 20))}");
        var cut = M.ChatLines(longRefusal);
        Check("A line too long for chat is cut at a space, and the second part is still the refusal it was",
            cut.Count == 2 && cut.All(l => M.Plain(l).Length <= Messages.ChatLineMax) && cut.All(l => Coloured(l).StartsWith('ρ')) && string.Join(' ', cut.Select(M.Plain)) == M.Plain(longRefusal));

        // ---- every line: its sections, its length, what is Lime ----
        var lines = new List<string>();
        lines.AddRange(ChatCommands.HelpLines());
        lines.AddRange(ChatCommands.FunLines(true));
        lines.AddRange(ChatCommands.FunLines(false));
        lines.AddRange(ChatCommands.VoteOnlyLines(0));
        lines.AddRange(ChatCommands.VoteOnlyLines(6));
        lines.Add(ChatCommands.UnknownLine("!foo"));
        lines.Add(ChatCommands.UnknownLine("!" + new string('x', 300)));
        foreach (var c in ChatCommands.All)
        {
            lines.Add(ChatCommands.UsageOf(c.Word));
            lines.AddRange(c.Aliases.Select(a => ChatCommands.Teach(ChatCommands.Parse(a + SampleArg(c.Word)))).OfType<string>());
        }
        foreach (var s in Fun.Settings)
        {
            lines.Add(ChatCommands.UsageOf($"!{s.Id}"));
            lines.Add(ChatReply.NotInMode(s, Modes.Deathmatch.Name));
            lines.Add(ChatReply.DidNotSwitch(s));
            foreach (var c in s.Choices)
            {
                lines.Add(ChatReply.Already(s, c));
                lines.AddRange(new[] { ChatCommands.Teach(ChatCommands.Parse($"!fun {s.Id} {c.Id}")) }.OfType<string>());
            }
        }
        foreach (var x in Fun.Clashes)
        {
            var (mine, theirs) = (Fun.Find(x.Setting)!, Fun.Find(x.Other)!);
            var theirSets = Fun.Find(theirs, x.OtherChoice)!.Sets;
            lines.Add(Fun.RefusalLine(mine, Fun.Find(mine, x.Choice)!, c => theirSets.FirstOrDefault(v => v.ConVar == c).Value)!);
        }
        lines.AddRange([ChatReply.ChangeUnderWay, ChatReply.VoteOn, ChatReply.VoteWindowStays, ChatReply.NoVote, ChatReply.CardOpen, ChatReply.ListLoading, ChatReply.NoLine,
            ChatReply.MenuOn("Maps"), ChatReply.Searching("dust"), ChatReply.Searching(new string('x', 200)), ChatReply.NoMatch("dust"), ChatReply.NoMatch(new string('x', 200)), ChatReply.Matches(3), ChatReply.YourVote("#12 aim_x"), ChatReply.YourVote(new string('x', 200))]);
        var longMap = new MapInfo(0, 7, "aim_" + new string('w', 96), 0);
        lines.AddRange([BlockedRefusal(longMap), RemovedRefusal(longMap), PackRefusal(longMap), ModeFullLine(Modes.Wingman, 5), M.Refused(NewMapsGoneNote)]);

        var capitals = lines.Where(l => Sections.Lower(l) != null).Select(l => Sections.Lower(l)!).Distinct().ToList();
        Check($"All {lines.Count} chat lines: every section starts with a capital{(capitals.Count > 0 ? "; not: " + string.Join(" | ", capitals.Take(6)) : "")}", capitals.Count == 0);
        var tooLong = lines.Where(l => M.Plain(l).Length > Messages.ChatLineMax).Select(M.Plain).ToList();
        Check($"...and each fits a line of {Messages.ChatLineMax} characters (the longest {lines.Max(l => M.Plain(l).Length)}){(tooLong.Count > 0 ? "; not: " + string.Join(" | ", tooLong.Take(4)) : "")}", tooLong.Count == 0);
        var loose = lines.Select(Coloured).Where(l => Regex.IsMatch(l, @"(?<![κα])![a-z+?-]")).ToList();
        Check($"...a word that starts with ! is never left in plain words{(loose.Count > 0 ? ": " + string.Join(" | ", loose.Take(4)) : "")}", loose.Count == 0);
        var lime = lines.SelectMany(l => Regex.Matches(Coloured(l), @"κ([^πκανρδτ]+)").Select(m => m.Groups[1].Value)).Distinct().ToList();
        var noCommand = lime.Where(w => ChatCommands.Parse(w).Kind is ChatCommands.Kind.Unknown or ChatCommands.Kind.None).ToList();
        Check($"...and what is Lime can be typed: {lime.Count} words, each a command of ours{(noCommand.Count > 0 ? "; not: " + string.Join(" ", noCommand) : "")}", lime.Count > 0 && noCommand.Count == 0);
    }
}
