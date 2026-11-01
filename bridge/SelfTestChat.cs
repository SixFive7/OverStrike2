// --selftest, the chat commands (ChatCommands.cs; the owner's decisions of 2026-11-01): every alias runs its primary and teaches it, no word
// is two things outside the owner's context rules, each fun setting has its own word, a bare number votes only while a vote is open, and
// the words are the same wherever they are said. The list below is the owner's, kept here word for word: the table may not drift from it.
using System.Text.RegularExpressions;

static partial class Program
{
    static readonly (string Primary, string[] Aliases)[] OwnersWords =
    [
        ("!play", ["!map", "!p", "!nominate", "!load", "!go"]),      // and !pick, Context
        ("!find", ["!search", "!s", "!lookup"]),                     // and !maps <words>
        ("!maps", ["!browse", "!list", "!maplist", "!m", "!top"]),
        ("!next", ["!n", "!rtv", "!change", "!skipmap"]),            // and !new with no argument, Context
        ("!random", ["!roll", "!dice", "!rand", "!any", "!shuffle"]),
        ("!back", ["!previous", "!prev", "!last", "!undo", "!again"]),
        ("!q", ["!queue", "!upcoming", "!whatsnext", "!lineup", "!order"]),
        ("!cancel", ["!unqueue", "!remove", "!drop", "!withdraw", "!uq"]),
        ("!done", ["!finish", "!end", "!release", "!letgo", "!over"]),
        ("!skip", ["!voteskip", "!vs", "!boo"]),
        ("!up", ["!like", "!+", "!good", "!love", "!thumbsup"]),
        ("!down", ["!dislike", "!-", "!bad", "!meh", "!thumbsdown"]),
        ("!toobig", ["!big", "!huge", "!toolarge", "!size", "!xl"]),
        ("!ads", ["!ad", "!spam", "!advert", "!report", "!scam"]),
        ("!vote", ["!v", "!mv", "!choose"]),                         // and !pick while a vote is open, Context
        ("!fun", ["!set", "!settings", "!toggle", "!mode"]),
        ("!nades", ["!grenades", "!nade", "!he", "!drops", "!boom"]),
        ("!time", ["!length", "!minutes", "!timelimit", "!roundtime", "!duration"]),
        ("!newmaps", ["!fresh", "!pool", "!mix", "!newmap"]),        // and !new with an argument, Context
        ("!restart", ["!reboot", "!fix", "!unstuck", "!stuck", "!reset"]),
        ("!menu", ["!gg", "!hud", "!os", "!os2", "!open"]),
        ("!help", ["!commands", "!cmds", "!h", "!?", "!info"]),
        ("!rules", ["!rule", "!r", "!fair", "!howto", "!policy"]),
        ("!how", ["!howitworks", "!odds", "!chances", "!why", "!explain"]),
    ];

    /// <summary>A line that runs a primary with something it needs, for the checks below.</summary>
    static string SampleArg(string primary) => primary switch
    {
        "!play" => " 12", "!find" => " dust", "!vote" => " 2", "!nades" => " on", "!time" => " 10", "!newmaps" => " half", _ => "",
    };

    static void ChatSelfTest(Action<string, bool> Check)
    {
        // ---- the table: the owner's list, one primary per action ----
        var table = ChatCommands.All.Select(c => (c.Word, Aliases: c.Aliases.Order(StringComparer.Ordinal).ToList())).ToList();
        var owners = OwnersWords.Select(o => (Word: o.Primary, Aliases: o.Aliases.Order(StringComparer.Ordinal).ToList())).ToList();
        var drift = owners.Where(o => !table.Any(t => t.Word == o.Word && t.Aliases.SequenceEqual(o.Aliases))).Select(o => o.Word).Concat(table.Select(t => t.Word).Except(owners.Select(o => o.Word))).ToList();
        Check($"Chat: the table is the owner's list - {ChatCommands.All.Length} primaries, {ChatCommands.All.Sum(c => c.Aliases.Length)} aliases{(drift.Count > 0 ? "; differs on " + string.Join(" ", drift) : "")}", drift.Count == 0);

        // ---- every word is one thing ----
        var primaries = ChatCommands.All.Select(c => c.Word).ToList();
        var aliasWords = ChatCommands.All.SelectMany(c => c.Aliases).ToList();
        var funWords = Fun.Settings.Select(s => "!" + s.Id).ToList();
        var contextWords = ChatCommands.Context.Select(c => c.Word).ToList();
        var twice = primaries.Concat(aliasWords).Concat(contextWords).GroupBy(w => w).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Check($"Chat: no word maps to two actions: {primaries.Count + aliasWords.Count + contextWords.Count} words, each once{(twice.Count > 0 ? "; twice: " + string.Join(" ", twice) : "")}", twice.Count == 0);
        var shadows = aliasWords.Concat(contextWords).Where(a => primaries.Contains(a) || funWords.Contains(a)).ToList();
        Check($"Chat: no alias shadows a primary or a fun word ({funWords.Count} of those){(shadows.Count > 0 ? ": " + string.Join(" ", shadows) : "")}", shadows.Count == 0);
        var sharedFun = funWords.Where(primaries.Contains).ToList();
        var blasts = Fun.Find("nades")!.Choices.Select(c => c.Id).ToList();
        Check("Chat: one fun word is also a primary, !nades: on|off are the grenade drops, its setting's own choices the blast, and the two sets do not meet",
            sharedFun.SequenceEqual(["!nades"]) && !blasts.Contains("on") && !blasts.Contains("off"));
        Check("Chat: the words that mean two things are the owner's (!pick, !new), each between two primaries that exist",
            contextWords.SequenceEqual(["!pick", "!new"]) && ChatCommands.Context.All(c => ChatCommands.Find(c.Otherwise) != null && ChatCommands.Find(c.When) != null));

        // ---- an alias runs the action and teaches the primary ----
        var wrong = new List<string>();
        foreach (var o in OwnersWords)
        {
            var arg = SampleArg(o.Primary);
            var own = ChatCommands.Parse(o.Primary + arg);
            if (own.Kind is ChatCommands.Kind.Unknown or ChatCommands.Kind.Usage or ChatCommands.Kind.None || own.Typed != "" || own.Ran != o.Primary + arg || own.Name != o.Primary) wrong.Add(o.Primary);
            foreach (var a in o.Aliases.Where(a => a != ChatCommands.TopWord))
            {
                var p = ChatCommands.Parse(a + arg);
                if (p.Kind != own.Kind || p.Arg != own.Arg || p.Name != own.Name || p.Ran != own.Ran || p.Typed != a + arg
                    || ChatCommands.Teach(p) != $"You typed {a}{arg} - ran {o.Primary}{arg}") wrong.Add(a);
            }
        }
        Check($"Chat: every alias does what its primary does, and the reply teaches it: You typed !map 12 - ran !play 12{(wrong.Count > 0 ? "; not " + string.Join(" ", wrong) : "")}",
            wrong.Count == 0 && ChatCommands.Teach(ChatCommands.Parse("!map 12")) == "You typed !map 12 - ran !play 12");
        Check("Chat: the primary itself teaches nothing, however it is typed; the reply keeps what was typed and always fits a chat line",
            ChatCommands.Teach(ChatCommands.Parse("!play 12")) == null && ChatCommands.Teach(ChatCommands.Parse("/PLAY 12")) == null
            && ChatCommands.Teach(ChatCommands.Parse("/MAP 12")) == "You typed /MAP 12 - ran !play 12"
            && ChatCommands.Teach(ChatCommands.Parse("!map " + new string('x', 300)))!.Length <= ChatCommands.LineMax);
        Check("Chat: !like 12, !dislike 12 and the rest carry a map's number or an official map's name; anything else gets the primary's usage",
            ChatCommands.Parse("!like 12") is { Kind: ChatCommands.Kind.Bridge, Arg: "!up 12", Ran: "!up 12" } && ChatCommands.Parse("!huge AR_shoots") is { Kind: ChatCommands.Kind.Bridge, Arg: "!toobig ar_shoots" }
            && ChatCommands.Parse("!spam dust") is { Kind: ChatCommands.Kind.Usage, Name: "!ads" } && ChatCommands.Parse("!map") is { Kind: ChatCommands.Kind.Usage, Name: "!play", Typed: "" }
            && ChatCommands.UsageOf(ChatCommands.Parse("!map").Name).StartsWith("!play <number"));

        var broken = new MapInfo(12, 1, "aim_x", 1);
        Check("Chat: a confirmation's note says to type the line again, as a second click and a second line count alike: the restart (and its aliases), a broken map's Play and Previous; every other note is left alone",
            ChatCommands.InChat(PickConfirmNote(broken, "is on the broken list")) == $"{Label(broken)} is on the broken list. Type it again within {PickConfirmSeconds} s to play it anyway."
            && ChatCommands.InChat(PickConfirmNote(broken, "is on the broken list", "Previous")).Contains("Type it again within")
            && ChatCommands.InChat("A restart kicks everyone for about a minute. Click Restart again within 30 s to confirm") == "A restart kicks everyone for about a minute. Type it again within 30 s to confirm"
            && ChatCommands.InChat("You upvoted #1 x, now +1 -0") == "You upvoted #1 x, now +1 -0");

        // ---- the owner's context rules ----
        Check("Chat: !pick is a vote while a vote is open and a number was given, otherwise play",
            ChatCommands.Parse("!pick 3", voteOpen: true) is { Kind: ChatCommands.Kind.Vote, Arg: "3", Ran: "!vote 3", Typed: "!pick 3" }
            && ChatCommands.Parse("!pick 3") is { Kind: ChatCommands.Kind.Play, Arg: "3", Ran: "!play 3", Typed: "!pick 3" }
            && ChatCommands.Parse("!pick dust", voteOpen: true) is { Kind: ChatCommands.Kind.Play, Arg: "dust" }
            && ChatCommands.Parse("!pick 7", voteOpen: true) is { Kind: ChatCommands.Kind.Play, Arg: "7" }
            && ChatCommands.Parse("!pick") is { Kind: ChatCommands.Kind.Usage, Name: "!play" });
        Check("Chat: !new with no argument is !next, with one !newmaps",
            ChatCommands.Parse("!new") is { Kind: ChatCommands.Kind.Bridge, Arg: "next", Ran: "!next", Typed: "!new" }
            && ChatCommands.Parse("!new half") is { Kind: ChatCommands.Kind.Bridge, Arg: "!newmaps half", Ran: "!newmaps half", Typed: "!new half" }
            && ChatCommands.Parse("!new sometimes") is { Kind: ChatCommands.Kind.Usage, Name: "!newmaps" });
        Check("Chat: !maps with words is !find, its aliases too; alone it is the browser; !top the browser by votes",
            ChatCommands.Parse("!maps dust") is { Kind: ChatCommands.Kind.Find, Arg: "dust", Ran: "!find dust", Typed: "!maps dust" }
            && ChatCommands.Parse("!m dust") is { Kind: ChatCommands.Kind.Find, Arg: "dust" }
            && ChatCommands.Parse("!maps") is { Kind: ChatCommands.Kind.Page, Arg: "Maps", Typed: "" }
            && ChatCommands.Parse("!top") is { Kind: ChatCommands.Kind.Top, Arg: "Maps", Typed: "!top" }
            && ChatCommands.Teach(ChatCommands.Parse("!top")) == "You typed !top - ran !maps, sorted by votes");
        Check("Chat: !rtv and !change are the board's Next, which is what also makes them a vote to skip on somebody's map",
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
                if (ChatCommands.Parse($"!set {want}") is not { Kind: ChatCommands.Kind.Fun } set || set.Arg != want || ChatCommands.Teach(set) != $"You typed !set {want} - ran !{want}") funWrong.Add($"!set {want}");
            }
            if (ChatCommands.Parse($"!{s.Id}").Kind != ChatCommands.Kind.Usage || ChatCommands.Parse($"!{s.Id} nonsense").Kind != ChatCommands.Kind.Usage
                || ChatCommands.UsageOf($"!{s.Id}").Length > ChatCommands.LineMax || !ChatCommands.UsageOf($"!{s.Id}").StartsWith($"!{s.Id} ")) funWrong.Add($"!{s.Id}");
        }
        Check($"Chat: each fun setting is a word of its own, with its choices: !gravity low, !vampire on; !fun gravity low keeps working and teaches the short word; a word alone or a wrong choice gets its usage{(funWrong.Count > 0 ? "; not " + string.Join(" | ", funWrong) : "")}",
            funWrong.Count == 0 && ChatCommands.Parse("!VAMPIRE ON") is { Kind: ChatCommands.Kind.Fun, Arg: "vampire on" });
        Check("Chat: the grenade drops: !nades on|off and !fun nades on|off, all of its aliases; the same word's blast choices are Big grenades; alone, a usage line naming both",
            ChatCommands.Parse("!nades off") is { Kind: ChatCommands.Kind.Nades, Arg: "off", Typed: "" } && ChatCommands.Parse("!fun nades on") is { Kind: ChatCommands.Kind.Nades, Arg: "on", Ran: "!nades on", Typed: "!fun nades on" }
            && ChatCommands.Parse("!boom off") is { Kind: ChatCommands.Kind.Nades, Arg: "off", Ran: "!nades off" } && ChatCommands.Parse("!nades big") is { Kind: ChatCommands.Kind.Fun, Arg: "nades big", Typed: "" }
            && ChatCommands.Parse("!grenades huge") is { Kind: ChatCommands.Kind.Fun, Arg: "nades huge", Ran: "!nades huge" } && ChatCommands.Parse("!nades") is { Kind: ChatCommands.Kind.Usage, Name: "!nades" }
            && ChatCommands.UsageOf("!nades") is { Length: <= ChatCommands.LineMax } nu && nu.Contains("!nades on|off") && nu.Contains("!nades normal|big|huge"));
        Check("Chat: !fun alone opens the page; an unknown setting its usage, never a blank",
            ChatCommands.Parse("!fun") is { Kind: ChatCommands.Kind.Page, Arg: "Fun", Typed: "" } && ChatCommands.Parse("!mode") is { Kind: ChatCommands.Kind.Page, Arg: "Fun", Ran: "!fun", Typed: "!mode" }
            && ChatCommands.Parse("!fun nonsense on") is { Kind: ChatCommands.Kind.Usage, Name: "!fun" } && ChatCommands.Parse("!fun gravity") is { Kind: ChatCommands.Kind.Usage, Name: "!gravity" });
        var arms = ChatCommands.FunLines(true);
        var other = ChatCommands.FunLines(false);
        var armsWords = Regex.Matches(string.Join(" ", arms), @"!(\S+) (\S+)").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        Check($"Chat: a bare !fun's {arms.Count} lines fit chat and list every setting's word with its choices: {string.Join("  ", arms.Skip(1).Take(1))} ...",
            arms.All(l => l.Length <= ChatCommands.LineMax) && other.All(l => l.Length <= ChatCommands.LineMax) && armsWords.Count == Fun.Settings.Length
            && Fun.Settings.All(s => armsWords.TryGetValue(s.Id, out var w) && s.Choices.All(c => w.Split('|').Contains(c.Id))) && armsWords["nades"].StartsWith("on|off|"));
        Check("Chat: outside Arms Race they leave out what the page leaves out - friendly fire's three and the grenade drops, not the blast",
            string.Join(" ", other) is var o2 && !o2.Contains("!ff ") && !o2.Contains("!instakill") && !o2.Contains("!ffnades") && o2.Contains("!nades normal|big|huge") && !o2.Contains("!nades on"));
        var everyWord = ChatCommands.All.Select(c => c.Words + " " + c.Args).Concat(arms).Concat(Hud.Help.SelectMany(g => g.Rows).Select(r => r.Words)).ToList();
        Check("Chat: nothing teaches !fun <setting> <choice>: the short word is no alias of it, and every word a command's own line names runs",
            !everyWord.Any(w => Regex.IsMatch(w, @"!fun \w")) && ChatCommands.All.SelectMany(c => Regex.Matches(c.Words, @"![a-z]+ ?[a-z]*").Select(m => m.Value.Trim())).All(w => ChatCommands.Parse(w).Kind is not (ChatCommands.Kind.Unknown or ChatCommands.Kind.Usage)));

        // ---- !help lists primaries only ----
        var listed = ChatCommands.HelpLines().SelectMany(l => Regex.Matches(l, @"!\S+").Select(m => m.Value)).ToList();
        Check($"Chat: !help's {ChatCommands.HelpLines().Count} lines list the {listed.Count} primaries, no alias", listed.Order().SequenceEqual(primaries.Order()));

        // ---- a bare number is a vote, while a vote is open ----
        Check($"Chat: while a vote is open a bare number 1 to {MapVote.MaxOptions} is a vote, with no reply about aliases; without a vote it is plain chat; a Workshop id is never one",
            ChatCommands.Parse("3", voteOpen: true) is { Kind: ChatCommands.Kind.Vote, Arg: "3", Name: "!vote", Typed: "" } && ChatCommands.Parse(" 6 ", voteOpen: true) is { Kind: ChatCommands.Kind.Vote, Arg: "6" }
            && ChatCommands.Parse("3").Kind == ChatCommands.Kind.None && new[] { "0", "7", "12", "3 dust", "3631955210", "gg" }.All(x => ChatCommands.Parse(x, voteOpen: true).Kind == ChatCommands.Kind.None)
            && WorkshopCard.IdInChat("3") == null);
        Check("Chat: !vote takes the same numbers; its aliases teach it",
            ChatCommands.Parse("!vote 3") is { Kind: ChatCommands.Kind.Vote, Arg: "3" } && ChatCommands.Parse("!mv 3") is { Kind: ChatCommands.Kind.Vote, Ran: "!vote 3", Typed: "!mv 3" }
            && ChatCommands.Parse("!choose 7") is { Kind: ChatCommands.Kind.Usage, Name: "!vote" } && ChatCommands.Parse("!vote") is { Kind: ChatCommands.Kind.Usage });
        Check("A card click says for everybody what a typed vote is: the line parses back as that vote",
            HudVote.ChatWords(3) == "!vote 3" && ChatCommands.Parse(HudVote.ChatWords(3)) is { Kind: ChatCommands.Kind.Vote, Arg: "3", Typed: "" });
        var ballot = VoteState.Decode("mode\tarmsrace\tArms Race\nmv\t1793500000\topen\t42\t3\t2\t1\nmvo\t1\tarmsrace\tqueue\t12\t3084291314\t#12 aim_x\t17 MB\t1\tAlice\nmvo\t2\tdeathmatch\trolled\tde_dust2\tde_dust2\tde_dust2 (official)\t\t0\t\nmvo\t3\tarmsrace\trandom\t\t\tRandom\t\t0\t\nmvv\t1001\t1\n");
        var cards = HudVote.Cards(ballot, 1001, null).Texts;
        string T(string id) => cards.Single(x => x.Id == id).Text;
        Check("The ballot's cards show the number to type: on the picture and on the button; the line under them says so",
            T("gg_m2_rank") == "2  ·  Deathmatch" && T("gg_m3_rank") == "3  ·  Random" && T("gg_m1_play_text") == "Your vote" && T("gg_m2_play_text") == "Vote 2" && T("gg_pg_text").Contains("type its number")
            && HudVote.Cards(ballot, 1002, null).Texts.Single(x => x.Id == "gg_pg_text").Text.Contains("type its number in chat"));

        // ---- a chat line a command opens on a page its game has to prove: where it says so ----
        Check("A command that opens How or Fun tells a game that has not shown that content to click the tab: the page's words wait for that",
            HudVote.PageLevel("How") == HudView.LevelOf("gg_h1_title") && HudVote.PageLevel("Fun") == HudView.LevelOf(HudMenu.FunRow(1)) && HudVote.PageLevel("Maps") == 0
            && HudVote.PageHint("How").Contains("How it works tab") && HudVote.PageHint("Fun").Contains("Fun tab")
            && HudView.LevelOfButton(HudView.TabHow) == HudVote.PageLevel("How") && HudView.LevelOfButton(HudView.TabFun) == HudVote.PageLevel("Fun"));

        // ---- the words are the same wherever they are said ----
        const string referenceFile = "../docs/reference.md", readmeFile = "../README.md";
        if (File.Exists(referenceFile))
        {
            var doc = File.ReadAllText(referenceFile);
            var section = Regex.Match(doc, @"## The menu and the commands(.*?)\n## ", RegexOptions.Singleline).Groups[1].Value;
            var undocumented = primaries.Concat(aliasWords).Concat(contextWords).Where(w => !section.Contains($"`{w}") ).ToList();
            var codeWords = Regex.Matches(section, @"`(![^\s`]+)").Select(m => m.Groups[1].Value).Distinct().Where(w => !primaries.Contains(w) && !aliasWords.Contains(w) && !contextWords.Contains(w) && !funWords.Contains(w)).ToList();
            Check($"docs/reference.md's table has every primary and every alias, and no other command{(undocumented.Count + codeWords.Count > 0 ? ": missing " + string.Join(" ", undocumented) + " unknown " + string.Join(" ", codeWords) : "")}",
                undocumented.Count == 0 && codeWords.Count == 0);
            var row = ChatCommands.All.Where(c => !Regex.IsMatch(section, $@"^\| `{Regex.Escape(c.Word)}[ `][^\n]*\| {string.Join(" ", c.Aliases.Select(a => Regex.Escape($"`{a}`")))}[ ,|]", RegexOptions.Multiline)).Select(c => c.Word).ToList();
            Check($"...each alias on its own primary's row, in the table's order{(row.Count > 0 ? ": not " + string.Join(" ", row) : "")}", row.Count == 0);
            Check("...and it says the rules: what ran, the context words, the fun words, the vote's bare number, the printed card click, the reasons",
                section.Contains("You typed !map 12 - ran !play 12") && doc.Contains($"!down <number> <reason>") && DownReasons.All.All(r => doc.Contains($"`{r.Word}`") && doc.Contains(r.Label))
                && doc.Contains($"popup listing them for {DownReasons.PopupSeconds} s"));
        }
        if (File.Exists(readmeFile))
        {
            var readme = File.ReadAllText(readmeFile);
            var named = Regex.Matches(readme, @"`(![^\s`]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
            var unknown = named.Where(w => ChatCommands.Parse(w + " on").Kind == ChatCommands.Kind.Unknown).ToList();
            Check($"The README's chat words are real ({string.Join(" ", named)}), and it names !help{(unknown.Count > 0 ? ": not " + string.Join(" ", unknown) : "")}", unknown.Count == 0 && named.Contains("!help"));
        }
        if (File.Exists(SiteFile))
        {
            var html = File.ReadAllText(SiteFile);
            var start = html.IndexOf("<!-- GUIDE-START", StringComparison.Ordinal);
            var end = html.IndexOf("<!-- GUIDE-END -->", StringComparison.Ordinal);
            if (start >= 0 && end > start)
            {
                var named = Regex.Matches(Regex.Replace(html[start..end], @"<script\b.*?</script>", " ", RegexOptions.Singleline), @"(?<=[>\s])![a-z]+").Select(m => m.Value).Distinct().ToList();
                Check($"The site's guide names only primaries ({string.Join(" ", named)})", named.All(w => ChatCommands.Find(w) != null));
            }
        }
    }
}
