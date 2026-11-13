// --selftest, the chat commands (ChatCommands.cs): every alias runs its primary and teaches it, every shortcut runs its line and teaches it, no
// word is two things but !maps with words, each fun setting has its own word, a bare number is never a vote (!vote 3 is), and the words are the
// same wherever they are said. The list below is the agreed one, kept here word for word: the table may not drift from it; the shortcuts are
// held to docs/reference.md's table of them. !skip and its aliases are Next's (the vote for the next map), and so is !new now that the new-maps
// setting is gone; !reset is the Fun page's Default and !fix is no word any more; !sync opens the Sync page; there is no queue (!q is gone,
// !done's words are Next's, and !cancel stops the player's own countdown); the Rules page's words are How it works', and !drops is the drops
// setting's own word.
using System.Text.RegularExpressions;

static partial class Program
{
    static readonly (string Primary, string[] Aliases)[] OwnersWords =
    [
        ("!play", ["!map", "!p", "!nominate", "!load", "!go", "!pick", "!download", "!dl", "!workshop", "!playmap"]),
        ("!find", ["!search", "!s", "!lookup", "!f", "!findmap", "!searchmap", "!mapsearch", "!look", "!seek"]), // and !maps <words>
        ("!maps", ["!browse", "!list", "!maplist", "!m", "!top", "!browser", "!allmaps", "!mapmenu", "!catalog", "!library"]),
        ("!next", ["!n", "!rtv", "!change", "!skipmap", "!skip", "!voteskip", "!vs", "!boo", "!done", "!finish", "!end", "!release", "!letgo", "!over", "!new",
            "!rockthevote", "!votemap", "!mapvote", "!changemap", "!startvote", "!callvote"]),
        ("!random", ["!roll", "!dice", "!rand", "!any", "!shuffle", "!randommap", "!rollmap", "!randomvote"]),
        ("!back", ["!previous", "!prev", "!last", "!undo", "!again", "!lastmap", "!prevmap", "!previousmap", "!goback", "!before"]),
        ("!up", ["!like", "!+", "!good", "!love", "!thumbsup", "!upvote", "!likemap", "!+1", "!great", "!fav"]),
        ("!down", ["!dislike", "!-", "!bad", "!meh", "!thumbsdown", "!downvote", "!dislikemap", "!-1", "!hate", "!boring"]),
        ("!vote", ["!v", "!mv", "!choose", "!ballot", "!votes", "!cast", "!votefor"]),
        ("!fun", ["!set", "!settings", "!toggle", "!mode", "!funmenu", "!funpage", "!modifiers", "!mods"]), // Random's and Default's words are shortcuts (!randomize, !reset)
        ("!nades", ["!grenades", "!nade", "!he", "!boom", "!grenade"]),
        ("!catchup", ["!catch", "!boost", "!boosts", "!comeback", "!rubberband", "!handicap", "!underdog"]), // the Fun page's Catch-up row in chat
        ("!bots", ["!bot", "!botmenu", "!ai", "!npc", "!npcs", "!botpage", "!addbots", "!addbot", "!botcount"]),
        ("!time", ["!length", "!minutes", "!timelimit", "!roundtime", "!duration", "!matchlength", "!matchtime", "!mins", "!rounds"]),
        ("!restart", ["!reboot", "!unstuck", "!stuck", "!restartserver", "!serverrestart", "!rebootserver", "!serverreboot"]), // !fix is gone, !reset is the Fun page's Default
        ("!rematch", ["!restartmatch", "!matchrestart", "!restartgame", "!rr", "!newmatch", "!resetmatch", "!matchreset", "!newgame"]),
        ("!cancel", ["!stop", "!abort", "!nvm", "!nevermind", "!hold", "!wait", "!norestart", "!norematch"]),
        ("!menu", ["!gg", "!hud", "!os", "!os2", "!open", "!mainmenu", "!overstrike", "!panel", "!home", "!window"]),
        ("!help", ["!commands", "!cmds", "!h", "!?", "!info", "!command", "!cmd", "!helpme", "!support", "!guide"]),
        ("!how", ["!howitworks", "!odds", "!chances", "!why", "!explain", "!rules", "!rule", "!r", "!fair", "!howto", "!policy", "!faq"]),
        ("!players", ["!who", "!online", "!people", "!roster", "!playerlist", "!humans"]),
        ("!sync", ["!status", "!dashboard", "!syncstatus", "!downloads", "!mapsync", "!queue", "!progress"]),
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
        var shortcutWords = ChatCommands.Shortcuts.Select(s => s.Word).ToList();
        var funWords = Fun.Settings.Select(s => "!" + s.Id).ToList();
        var twice = primaries.Concat(aliasWords).Concat(shortcutWords).GroupBy(w => w).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Check($"Chat: no word maps to two actions: {primaries.Count + aliasWords.Count + shortcutWords.Count} words ({primaries.Count} primaries, {aliasWords.Count} aliases, {shortcutWords.Count} shortcuts), each once{(twice.Count > 0 ? "; twice: " + string.Join(" ", twice) : "")}", twice.Count == 0);
        var shadows = aliasWords.Concat(shortcutWords).Where(a => primaries.Contains(a) || funWords.Contains(a)).ToList();
        Check($"Chat: no alias or shortcut shadows a primary or a fun word ({funWords.Count} of those){(shadows.Count > 0 ? ": " + string.Join(" ", shadows) : "")}", shadows.Count == 0);
        var heavy = new[] { "!fix", "!start", "!get", "!add", "!spam", "!lag", "!fps", "!bug", "!nextmap", "!newmap", "!1v1", "!war", "!practice", "!space", "!normal" };
        var taken = heavy.Where(w => ChatCommands.Parse(w).Kind != ChatCommands.Kind.Unknown).ToList();
        Check($"Chat: no generic word sets off something heavy - !fix restarts nothing any more, and !reset is the Fun page's Default; the others are no words of ours{(taken.Count > 0 ? ": " + string.Join(" ", taken) : "")}",
            taken.Count == 0 && ChatCommands.Parse("!reset") is { Kind: ChatCommands.Kind.FunAll, Arg: Fun.DefaultWord });
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
            foreach (var a in o.Aliases.Where(a => a != ChatCommands.TopWord))
            {
                var p = ChatCommands.Parse(a + arg);
                if (p.Kind != own.Kind || p.Arg != own.Arg || p.Name != own.Name || p.Ran != own.Ran || p.Typed != a + arg
                    || Taught(p) != $"You typed {a}{arg} - Ran {o.Primary}{arg}") wrong.Add(a);
            }
        }
        Check($"Chat: every alias does what its primary does, and the reply teaches it: You typed !map 12 - Ran !play 12{(wrong.Count > 0 ? "; not " + string.Join(" ", wrong) : "")}",
            wrong.Count == 0 && Taught(ChatCommands.Parse("!map 12")) == "You typed !map 12 - Ran !play 12");
        var randomWords = ChatCommands.Shortcuts.Where(s => s.Line == $"!fun {Fun.RandomWord}").Select(s => s.Word).ToList();
        var defaultWords = ChatCommands.Shortcuts.Where(s => s.Line == $"!fun {Fun.DefaultWord}").Select(s => s.Word).ToList();
        Check($"Chat: !fun random and !fun default are the Fun page's Random and Default; {string.Join(" ", randomWords)} run Random and {string.Join(" ", defaultWords)} Default, each taught, as !set teaches !fun",
            ChatCommands.Parse("!fun random") is { Kind: ChatCommands.Kind.FunAll, Arg: Fun.RandomWord, Typed: "" } && ChatCommands.Parse("!FUN DEFAULT") is { Kind: ChatCommands.Kind.FunAll, Arg: Fun.DefaultWord, Typed: "" }
            && randomWords.Count >= 5 && defaultWords.Count >= 5 && randomWords.Contains("!randomize") && defaultWords.Contains("!reset") && defaultWords.Contains("!defaults")
            && randomWords.All(w => ChatCommands.Parse(w) is { Kind: ChatCommands.Kind.FunAll, Arg: Fun.RandomWord, Name: "!fun" } p && Taught(p) == $"You typed {w} - Ran !fun random")
            && defaultWords.All(w => ChatCommands.Parse(w) is { Kind: ChatCommands.Kind.FunAll, Arg: Fun.DefaultWord, Name: "!fun" } p && Taught(p) == $"You typed {w} - Ran !fun default")
            && ChatCommands.Parse("!set random") is { Kind: ChatCommands.Kind.FunAll, Arg: Fun.RandomWord, Ran: "!fun random", Typed: "!set random" }
            && ChatCommands.Parse("!fun randomly") is { Kind: ChatCommands.Kind.Usage, Name: "!fun" });
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
        Check("Chat: !like 12, !dislike 12 big and the rest carry a map's number or an official map's name; anything else gets the primary's usage; !toobig 12 runs !down 12 big, !ads !down ads, and teach them",
            ChatCommands.Parse("!like 12") is { Kind: ChatCommands.Kind.Bridge, Arg: "!up 12", Ran: "!up 12" } && ChatCommands.Parse("!dislike AR_shoots big") is { Kind: ChatCommands.Kind.Bridge, Arg: "!down ar_shoots big" }
            && ChatCommands.Parse("!like dust") is { Kind: ChatCommands.Kind.Usage, Name: "!up" }
            && ChatCommands.Parse("!toobig 12") is { Kind: ChatCommands.Kind.Bridge, Arg: "!down 12 big", Ran: "!down 12 big", Typed: "!toobig 12" } && Taught(ChatCommands.Parse("!toobig 12")) == "You typed !toobig 12 - Ran !down 12 big"
            && ChatCommands.Parse("!ads") is { Kind: ChatCommands.Kind.Bridge, Arg: "!down ads", Name: "!down" } && ChatCommands.Parse("!toobig dust") is { Kind: ChatCommands.Kind.Usage, Name: "!down" }
            && DownReasons.All.Skip(1).All(r => ChatCommands.Shortcuts.Count(s => s.Line == $"!down _ {r.Word}") >= 5)
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
        Check("Chat: !play and !back are the same commands while a vote is open - the bridge puts their map on that ballot - !random and its aliases move the vote to Random, !next and !done bring it up",
            ChatCommands.Parse("!play 12") is { Kind: ChatCommands.Kind.Play, Arg: "12" } && ChatCommands.Parse("!map dust") is { Kind: ChatCommands.Kind.Play }
            && ChatCommands.Parse("!back") is { Kind: ChatCommands.Kind.Bridge, Arg: "!previous" } && ChatCommands.Parse("!next") is { Kind: ChatCommands.Kind.Bridge, Arg: "next" }
            && ChatCommands.Parse("!done") is { Kind: ChatCommands.Kind.Bridge, Arg: "next", Ran: "!next", Typed: "!done" }
            && ChatCommands.Parse("!random") is { Kind: ChatCommands.Kind.Bridge, Arg: "!next random" } && ChatCommands.Parse("!dice") is { Kind: ChatCommands.Kind.Bridge, Arg: "!next random" });
        Check("Chat: !vote 3 votes for the card with number 3, its aliases too (!v 3); !vote random and its words for Random; a bare !vote brings the window back; a bare number stays plain chat",
            ChatCommands.Parse("!vote 3") is { Kind: ChatCommands.Kind.Vote, Arg: "3", Typed: "" } && ChatCommands.Parse("!v 3") is { Kind: ChatCommands.Kind.Vote, Arg: "3", Ran: "!vote 3", Typed: "!v 3" }
            && ChatCommands.Parse("!choose 12") is { Kind: ChatCommands.Kind.Vote, Arg: "12" } && ChatCommands.Parse($"!vote {MapVote.RandomOption - 1}") is { Kind: ChatCommands.Kind.Vote }
            && ChatCommands.Parse("!vote random") is { Kind: ChatCommands.Kind.Vote, Typed: "" } r1 && r1.Arg == $"{MapVote.RandomOption}"
            && ChatCommands.Parse("!v dice") is { Kind: ChatCommands.Kind.Vote, Ran: "!vote random", Typed: "!v dice" } r2 && r2.Arg == $"{MapVote.RandomOption}"
            && new[] { "!vote 0", $"!vote {MapVote.RandomOption}", "!vote 100", "!vote 03", "!vote dust", "!v 3 4" }.All(w => ChatCommands.Parse(w) is { Kind: ChatCommands.Kind.Usage, Name: "!vote" })
            && ChatCommands.Parse("!vote") is { Kind: ChatCommands.Kind.Page, Arg: "Maps", Typed: "" } && ChatCommands.Parse("!v") is { Kind: ChatCommands.Kind.Page, Arg: "Maps", Ran: "!vote", Typed: "!v" }
            && ChatCommands.Parse("3").Kind == ChatCommands.Kind.None && ChatCommands.Parse("12").Kind == ChatCommands.Kind.None
            && Taught(ChatCommands.Parse("!v 3")) == "You typed !v 3 - Ran !vote 3" && MapVote.IsAction(MapVote.Click(7, 3)));

        // ---- the shortcuts: hidden words for a command with words of its own ----
        var bad = new List<string>();
        foreach (var s in ChatCommands.Shortcuts)
        {
            var bare = ChatCommands.Expand(s, "");
            var ran = ChatCommands.Parse(bare);
            if (ChatCommands.Shortcuts.Any(x => bare.Split(' ')[0] == x.Word)) { bad.Add($"{s.Word} (runs a shortcut)"); continue; }
            var alone = ChatCommands.Parse(s.Word);
            if (ran.Kind is ChatCommands.Kind.Unknown or ChatCommands.Kind.None) bad.Add($"{s.Word} (runs nothing)");
            else if (ran.Kind == ChatCommands.Kind.Usage)
            {
                // a setting's word without its choice: its usage line alone, and with a choice the setting
                if (alone.Kind != ChatCommands.Kind.Usage || s.Default != "" || !s.Line.EndsWith(" _", StringComparison.Ordinal)) bad.Add($"{s.Word} (usage)");
                var row = bare.StartsWith("!bots ", StringComparison.Ordinal) ? Bots.Find(bare[6..]) : Fun.Find(bare[1..]);
                if (row == null || ChatCommands.Parse($"{s.Word} {row.Choices[^1].Id}") is not { Typed: not "" } chosen || chosen.Kind is ChatCommands.Kind.Usage or ChatCommands.Kind.Unknown) bad.Add($"{s.Word} (no choice runs)");
            }
            else if (alone.Kind != ran.Kind || alone.Arg != ran.Arg || alone.Typed != s.Word || Taught(alone) != $"You typed {s.Word} - Ran {ran.Ran}") bad.Add($"{s.Word} (taught {Taught(alone)})");
        }
        Check($"Chat: every one of the {ChatCommands.Shortcuts.Length} shortcuts runs its line and teaches it (You typed !lowgrav - Ran !gravity low); a setting's word alone gets that setting's usage{(bad.Count > 0 ? "; not " + string.Join(" | ", bad.Take(8)) : "")}",
            bad.Count == 0 && Taught(ChatCommands.Parse("!lowgrav")) == "You typed !lowgrav - Ran !gravity low" && Taught(ChatCommands.Parse("!zombies")) == "You typed !zombies - Ran !bots zombies");
        Check("...what follows a shortcut takes its place, or its default when nothing does: !headshots is !hs on, !headshots off !hs off; !grav low is !gravity low, !grav alone !gravity's usage; !toobig 12 is !down 12 big",
            ChatCommands.Parse("!headshots") is { Kind: ChatCommands.Kind.Fun, Arg: "hs on", Ran: "!hs on", Typed: "!headshots" } && ChatCommands.Parse("!headshots off") is { Kind: ChatCommands.Kind.Fun, Arg: "hs off", Ran: "!hs off" }
            && ChatCommands.Parse("!grav low") is { Kind: ChatCommands.Kind.Fun, Arg: "gravity low", Typed: "!grav low" } && ChatCommands.Parse("!grav") is { Kind: ChatCommands.Kind.Usage, Name: "!gravity" }
            && ChatCommands.Parse("!moon") is { Kind: ChatCommands.Kind.Fun, Arg: "gravity moon" } && ChatCommands.Parse("!lowgrav now") is { Kind: ChatCommands.Kind.Usage, Name: "!gravity" }
            && ChatCommands.Parse("!blast") is { Kind: ChatCommands.Kind.Fun, Arg: "nades big" } && ChatCommands.Parse("!blast off") is { Kind: ChatCommands.Kind.Usage } // never the drops: off is theirs
            && ChatCommands.Expand(new ChatCommands.Shortcut("!x", "!down _ big"), " 12 ") == "!down 12 big" && ChatCommands.Expand(new ChatCommands.Shortcut("!x", "!hs _", "on"), "") == "!hs on");
        var sets = Fun.Settings.Select(s => (s.Id, Words: ChatCommands.Shortcuts.Count(x => x.Line.Split(' ')[0] == $"!{s.Id}" && x.Line != "!nades big" && x.Line != "!nades huge"))).ToList();
        var thin = sets.Where(x => x.Words < 5 && x.Id is not ("catchup" or "nades")).Select(x => $"!{x.Id} {x.Words}").ToList();
        Check($"...every fun setting has five hidden words or more, the big grenades too, besides its own (!catchup and !nades have their aliases){(thin.Count > 0 ? "; not " + string.Join(" ", thin) : "")}",
            thin.Count == 0 && ChatCommands.Shortcuts.Count(x => x.Line is "!nades big" or "!nades huge") >= 5);

        // ---- !bots: the Bots page in chat ----
        Check("Chat: !bots alone opens the Bots page; !bots 4 sets how many play, !bots zombies starts a preset, !bots kick and !bots default are the page's two buttons",
            ChatCommands.Parse("!bots") is { Kind: ChatCommands.Kind.Page, Arg: "Bots", Typed: "" } && ChatCommands.Parse("!bot") is { Kind: ChatCommands.Kind.Page, Arg: "Bots", Ran: "!bots" }
            && ChatCommands.Parse("!bots 4") is { Kind: ChatCommands.Kind.Bots, Arg: "bots 4", Typed: "" } && ChatCommands.Parse("!bots zombies") is { Kind: ChatCommands.Kind.Bots, Arg: "zombies", Typed: "" }
            && ChatCommands.Parse("!bots kick") is { Kind: ChatCommands.Kind.Bots, Arg: Bots.KickWord, Typed: "" } && ChatCommands.Parse("!bots default") is { Kind: ChatCommands.Kind.Bots, Arg: Bots.DefaultWord }
            && Bots.Presets.All(p => ChatCommands.Parse($"!bots {p.Id}") is { Kind: ChatCommands.Kind.Bots } x && x.Arg == p.Id)
            && Bots.Find("bots")!.Choices.Skip(1).All(c => ChatCommands.Parse($"!bots {c.Id}") is { Kind: ChatCommands.Kind.Bots } x && x.Arg == $"bots {c.Id}"));
        Check("...!bots off, 0 and none kick every bot and teach !bots kick; a preset in the singular is the preset (!bots zombie); !addbots 8 is !bots 8",
            new[] { "off", "0", "none" }.All(w => ChatCommands.Parse($"!bots {w}") is { Kind: ChatCommands.Kind.Bots, Arg: Bots.KickWord } x && Taught(x) == $"You typed !bots {w} - Ran !bots kick")
            && ChatCommands.Parse("!bots zombie") is { Kind: ChatCommands.Kind.Bots, Arg: "zombies", Ran: "!bots zombies" } && ChatCommands.Parse("!addbots 8") is { Kind: ChatCommands.Kind.Bots, Arg: "bots 8", Ran: "!bots 8" });
        var botWrong = new List<string>();
        foreach (var s in Bots.Settings)
        {
            foreach (var c in s.Choices)
                if (ChatCommands.Parse($"!bots {s.Id} {c.Id}") is not { Kind: ChatCommands.Kind.Bots, Typed: "" } x || x.Arg != $"{s.Id} {c.Id}") botWrong.Add($"{s.Id} {c.Id}");
            if (ChatCommands.Parse($"!bots {s.Id}") is not { Kind: ChatCommands.Kind.Usage } u || !M.Plain(ChatCommands.UsageOf(u.Name)).StartsWith($"!bots {s.Id} ", StringComparison.Ordinal)
                || M.Plain(ChatCommands.UsageOf(u.Name)).Length > Messages.ChatLineMax) botWrong.Add(s.Id);
        }
        Check($"...!bots <setting> <choice> sets a row, every row and choice of the page; a row alone gets its usage line, its choices in it{(botWrong.Count > 0 ? "; not " + string.Join(" | ", botWrong) : "")}",
            botWrong.Count == 0 && ChatCommands.Parse("!bots level hard") is { Kind: ChatCommands.Kind.Bots, Arg: "botlevel hard", Ran: "!bots botlevel hard", Typed: "!bots level hard" }
            && ChatCommands.Parse("!bots hard") is { Kind: ChatCommands.Kind.Bots, Arg: "botlevel hard", Typed: "" } && ChatCommands.Parse("!bots t") is { Kind: ChatCommands.Kind.Bots, Arg: "botteam t" }
            && ChatCommands.Parse("!bots war on") is { Kind: ChatCommands.Kind.Bots, Arg: "botwar on" } && ChatCommands.Parse("!bots war") is { Kind: ChatCommands.Kind.Bots, Arg: "war" }
            && ChatCommands.Parse("!bots on") is { Kind: ChatCommands.Kind.Usage, Name: "!bots" } && ChatCommands.Parse("!bots 3") is { Kind: ChatCommands.Kind.Usage, Name: "!bots" }
            && ChatCommands.Parse("!bots level") is { Kind: ChatCommands.Kind.Usage, Name: "!bots botlevel" } && ChatCommands.Parse("!bots botlevel godlike") is { Kind: ChatCommands.Kind.Usage, Name: "!bots botlevel" });
        Check("...and the Bots page's two buttons and six presets each have five hidden words or more (!kickbots, !zombies, !botwar ...)",
            Bots.Presets.All(p => ChatCommands.Shortcuts.Count(s => s.Line == $"!bots {p.Id}") >= 5)
            && ChatCommands.Shortcuts.Count(s => s.Line == $"!bots {Bots.KickWord}") >= 5 && ChatCommands.Shortcuts.Count(s => s.Line == $"!bots {Bots.DefaultWord}") >= 5);

        // ---- !cancel and !players ----
        Check("Chat: !cancel stops the player's own newest countdown - the bridge's !cancel mine, a click it takes; with anything after it, its usage; !stop and the rest teach it",
            ChatCommands.Parse("!cancel") is { Kind: ChatCommands.Kind.Bridge, Arg: Hud.CancelMine, Typed: "" } && Hud.IsAction(Hud.CancelMine)
            && ChatCommands.Parse("!stop") is { Kind: ChatCommands.Kind.Bridge, Arg: Hud.CancelMine, Ran: "!cancel", Typed: "!stop" }
            && ChatCommands.Parse("!cancel 7") is { Kind: ChatCommands.Kind.Usage, Name: "!cancel" });
        Check("Chat: !players opens the Players page, !who and the rest teach it",
            ChatCommands.Parse("!players") is { Kind: ChatCommands.Kind.Page, Arg: "Players", Typed: "" } && Taught(ChatCommands.Parse("!who")) == "You typed !who - Ran !players"
            && Enum.TryParse<HudSlots.Page>("Players", out _) && Enum.TryParse<HudSlots.Page>("Bots", out _));
        var few = ChatCommands.All.Where(c => c.Aliases.Length < 5).Select(c => c.Word).ToList();
        Check($"Chat: every primary has five aliases or more{(few.Count > 0 ? "; not " + string.Join(" ", few) : "")}", few.Count == 0);
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

        // ---- a number in chat: the cards have none, so a vote is a click ----
        Check("Chat: a bare number is never a vote, nor a command; !vote with a number is the vote, whatever the window shows (the plugin answers when no vote is on)",
            new[] { "3", " 6 ", "0", "7", "12", $"{MapVote.MaxMaps}", "3 dust", "3631955210", "gg" }.All(x => ChatCommands.Parse(x).Kind == ChatCommands.Kind.None)
            && WorkshopCard.IdInChat("3") == null && ChatCommands.Parse("!vote 3") is { Kind: ChatCommands.Kind.Vote, Arg: "3" });
        Check("Chat: !vote takes every card's number - a card keeps its number for the whole vote, and new cards get new ones up to 98 - and its aliases teach it",
            ChatCommands.Parse("!vote 3") is { Kind: ChatCommands.Kind.Vote, Arg: "3" } && ChatCommands.Parse("!mv 3") is { Kind: ChatCommands.Kind.Vote, Ran: "!vote 3", Typed: "!mv 3" }
            && ChatCommands.Parse("!choose 77") is { Kind: ChatCommands.Kind.Vote, Arg: "77" } && ChatCommands.Parse("!vote") is { Kind: ChatCommands.Kind.Page, Arg: "Maps" }
            && Enumerable.Range(1, MapVote.RandomOption - 1).All(n => ChatCommands.IsCardNumber($"{n}")) && !ChatCommands.IsCardNumber($"{MapVote.RandomOption}"));
        var ballot = VoteState.Decode("mode\tarmsrace\tArms Race\nmv\t1793500000\topen\t42\t3\t2\tmid\t0\t1\nmvo\t1\tarmsrace\tpicked\t12\t3084291314\t#12 aim_x\t17 MB\t1\tAlice\nmvo\t2\tdeathmatch\trolled\tde_dust2\tde_dust2\tde_dust2 (official)\t\t0\t\nmvo\t99\tarmsrace\trandom\t\t\tRandom\t\t0\t\nmvv\t1001\t1\n");
        var drawn = HudVote.Cards(ballot, 1001, null);
        var cards = drawn.Texts;
        string T(string id) => cards.Single(x => x.Id == id).Text;
        Check("The ballot's cards carry no number: the mode on the picture, Vote on the button, Random's button its votes; the line under them says to click, that a vote comes first and moves - never how to take one back",
            T("os_m2_rank") == "Deathmatch" && T("os_m3_rank") == "" && drawn.Vars.Single(x => x.Name == HudVote.RandomVar).Text == "Random  ·  0 votes"
            && T("os_m1_play_text") == "Your vote" && T("os_m2_play_text") == "Vote" && !T("os_pg_text").Contains("number") && T("os_pg_text").Contains("first") && !T("os_pg_text").Contains("back")
            && HudVote.Cards(ballot, 1002, null).Texts.Single(x => x.Id == "os_pg_text").Text == "Click a card's Vote");

        // ---- a command that opens a page: its words are on it at once, as for everybody (no content has to prove anything first) ----
        Check("A command that opens How, Fun, Help or Sync opens that page of the menu; its words go to every game alike",
            ChatCommands.Parse("!fun") is { Kind: ChatCommands.Kind.Page, Arg: "Fun" } && ChatCommands.Parse("!how") is { Kind: ChatCommands.Kind.Page, Arg: "How" }
            && ChatCommands.Parse("!help") is { Kind: ChatCommands.Kind.Commands, Arg: "Help" } && ChatCommands.Parse("!rules") is { Kind: ChatCommands.Kind.Page, Arg: "How" }
            && !HudView.StaticClasses().Any(x => x.Class is "c31" or "c32")
            && HudView.StaticTexts().Any(x => x.Id == "os_h1_title") && HudView.StaticTexts().Any(x => x.Id == HudMenu.FunNote));

        // ---- the words are the same wherever they are said ----
        const string referenceFile = "../docs/reference.md", readmeFile = "../README.md";
        if (File.Exists(referenceFile))
        {
            var doc = File.ReadAllText(referenceFile);
            var section = Regex.Match(doc, @"## The menu and the commands(.*?)\n## ", RegexOptions.Singleline).Groups[1].Value;
            var undocumented = primaries.Concat(aliasWords).Concat(shortcutWords).Where(w => !section.Contains($"`{w}") ).ToList();
            var codeWords = Regex.Matches(section, @"`(![^\s`]+)").Select(m => m.Groups[1].Value).Distinct().Where(w => !primaries.Contains(w) && !aliasWords.Contains(w) && !funWords.Contains(w) && !shortcutWords.Contains(w)).ToList();
            Check($"docs/reference.md's tables have every primary, every alias and every shortcut, and no other command{(undocumented.Count + codeWords.Count > 0 ? ": missing " + string.Join(" ", undocumented) + " unknown " + string.Join(" ", codeWords) : "")}",
                undocumented.Count == 0 && codeWords.Count == 0);
            // the shortcuts' table: a row for each line they run, its words in the code's order and what they run - the line as it runs alone
            var unlisted = ChatCommands.Shortcuts.GroupBy(s => (s.Line, s.Default)).Where(g => !Regex.IsMatch(section,
                $@"^\| {string.Join(" ", g.Select(s => Regex.Escape($"`{s.Word}`")))} \|[^\n]*`{Regex.Escape(ChatCommands.Expand(g.First(), ""))}`", RegexOptions.Multiline)).Select(g => g.First().Word).ToList();
            Check($"...each shortcut on the row of the line it runs, in the code's order{(unlisted.Count > 0 ? ": not " + string.Join(" ", unlisted) : "")}", unlisted.Count == 0);
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
        Check("Chat: a bare !time is the bridge's !time with no value, which switches the length (release 198) - its alias too; a value other than 10 or 60 shows the usage",
            ChatCommands.Parse("!time") is { Kind: ChatCommands.Kind.Bridge, Arg: "!time" } && ChatCommands.Parse("!length") is { Kind: ChatCommands.Kind.Bridge, Arg: "!time" }
            && ChatCommands.Parse("!time 30") is { Kind: ChatCommands.Kind.Usage } && ChatCommands.Parse("!time 60") is { Kind: ChatCommands.Kind.Bridge, Arg: "!time 60" } && Hud.IsAction("!time"));

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
            U("!play") == "κ!playπ α<number, name or Workshop ID>π: Put a map to the vote, with your vote on it" && U("!time") == "κ!timeπ ν[10|60]π: Match length in minutes, this match too - Wingman's in rounds, 8 or 16 - Alone it switches"
            && U("!gravity") == $"κ!gravityπ νnormal|low|moonπ: {Fun.Find("gravity")!.Words}" && U("!down") == $"κ!downπ α[number] [reason]π: Dislike it, and why: ν{DownReasons.Words}π"
            && U("!fun") == "κ!funπ: The Fun page, each setting's own word (κ!gravityπ νlowπ), κ!funπ νrandomπ and κ!funπ νdefaultπ"
            && U("!nades") == $"κ!nadesπ νon|offπ: Grenade drops on or off  ·  κ!nadesπ νnormal|big|hugeπ: {Fun.Find("nades")!.Words}");
        Check("!help's list: the commands Lime beside the words of their group; a bare !fun's: each word Lime with a choice in Gold",
            Coloured(ChatCommands.HelpLines()[0]).StartsWith("Maps: κ!playπ κ!findπ κ!mapsπ") && Coloured(ChatCommands.FunLines(true)[0]) == "Every setting here has its own word:"
            && Coloured(ChatCommands.FunLines(true)[1]).StartsWith("κ!gravityπ νlowπ  κ!jumpπ νhighπ"));
        Check("A refusal is LightRed, and what it does not know is no command: Unknown command !foo is an argument, !help is the Lime in it",
            Coloured(ChatCommands.UnknownLine("!foo")) == "ρUnknown command α!fooρ - κ!helpρ lists them (and the menu: hold TAB, press E)"
            && Coloured(ChatReply.NoCard(7)) == "ρNo card in this vote has the number ν7ρ");
        Check("A shortcut's reply: the word typed Lime, what it ran as any command - a setting's choice, a preset, a reason Gold",
            T("!lowgrav") == "You typed κ!lowgravπ - Ran κ!gravityπ νlowπ" && T("!zombies") == "You typed κ!zombiesπ - Ran κ!botsπ νzombiesπ"
            && T("!toobig 12") == "You typed κ!toobigπ α12π - Ran κ!downπ α12π νbigπ" && T("!bots level hard") == "You typed κ!botsπ αlevelπ νhardπ - Ran κ!botsπ αbotlevelπ νhardπ"
            && T("!v 3") == "You typed κ!vπ ν3π - Ran κ!voteπ ν3π" && U("!bots") == "κ!botsπ α[number, preset, kick or default]π: The Bots page - κ!botsπ ν4π, κ!botsπ νzombiesπ, κ!botsπ νkickπ");
        var moon = Fun.Find(Fun.Find("gravity")!, "moon")!;
        var jump = Fun.Find("jump")!;
        Check("A clash's refusal in chat: its settings arguments, their choices values; the plain words are the same words, for the window and the page",
            Coloured(Fun.RefusalLine(jump, Fun.Find(jump, "high")!, c => c == "sv_gravity" ? "200" : null)!) == "ραSuper jumpρ cannot be νHighρ while αGravityρ is νMoonρ: together they throw players out of most maps"
            && Fun.Refusal(jump, Fun.Find(jump, "high")!, c => c == "sv_gravity" ? "200" : null) == M.Plain(Fun.RefusalLine(jump, Fun.Find(jump, "high")!, c => c == "sv_gravity" ? "200" : null)!) && moon.Label == "Moon");
        Check("What worked is Green, what only informs the chat's own colour",
            Coloured(ChatReply.MenuOn("Maps")) == "δThe menu is open on αMapsδ" && Coloured(ChatReply.YourVote("#12 aim_x")) == "δYour vote: α#12 aim_xδ"
            && Coloured(ChatReply.Already(jump, Fun.Find(jump, "high")!)) == "αSuper jumpπ: νHighπ already" && Coloured(ChatReply.Matches(3)) == "ν3π maps match - The menu shows them: click αPlayπ on one");
        Check("The bridge's refusals of a map are LightRed too, the map an argument and a mode's limits values",
            Coloured(BlockedRefusal(new MapInfo(0, 5, "x", 0))) == "ραxρ (5) is on the blocked list - It is blocked." && Coloured(NonMapRefusal(new MapInfo(0, 6, "y", 0))) == $"ραyρ {NotAMap}"
            && Coloured(ModeFullLine(new MapInfo(0, 0, "de_dust2", 0, true, Modes.DeathmatchId), 17)) == $"ρνDeathmatchρ takes ν{Modes.Deathmatch.MaxPlayers} playersρ, and ν17ρ are on - Not this map now");

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

        // ---- a chat line typed on MAPS > Browse: its search (233) ----
        Check("On MAPS > Browse a chat line is its search, all chat and team chat alike: the line as typed",
            ChatCommands.SearchOf("dust 2", true) == "dust 2" && ChatCommands.SearchOf("  aim map ", true) == "aim map" && ChatCommands.SearchOf("12", true) == "12"
            && ChatCommands.SearchOf("dust", false) == null && ChatCommands.SearchOf("gg dust", true) == "gg dust");
        Check("...but no command - !s still searches - no Workshop link or id (they open its card), no bare number while a vote is open, and no line of nothing but chat words or without a letter or a digit",
            new[] { "!s dust", "!next", "/s dust", "https://steamcommunity.com/sharedfiles/filedetails/?id=3232901690", "3232901690" }.All(l => ChatCommands.SearchOf(l, true) == null)
            && ChatCommands.SearchOf("12", true, voteOpen: true) == null && ChatCommands.SearchOf("12 dust", true, voteOpen: true) == "12 dust"
            && ChatCommands.ChatWords.All(w => ChatCommands.SearchOf(w, true) == null) && new[] { "GG", "gg!", "Gg wp", "glhf", "ty gg", "xD", "nice!!", "ok ok" }.All(l => ChatCommands.SearchOf(l, true) == null)
            && new[] { "", "   ", ":)", ":(", "?", "!", "...", null }.All(l => ChatCommands.SearchOf(l, true) == null)
            && ChatCommands.ChatWords.SequenceEqual(["gg", "gl", "hf", "glhf", "wp", "ty", "thx", "lol", "xd", "ok", "nice", "ns", "np", "ez"]));
        Check("...a line whose words find nothing says so on the window's line, in the words agreed",
            M.Plain(ChatReply.ChatSearchNone("dsut2")) == "No map matches \"dsut2\" - Try fewer letters or other words" && M.Plain(ChatReply.ChatSearchNone(new string('x', 200))).Length <= Messages.LineMax);

        // ---- every line: its sections, its length, what is Lime ----
        var lines = new List<string>();
        lines.AddRange(ChatCommands.HelpLines());
        lines.AddRange(ChatCommands.FunLines(true));
        lines.AddRange(ChatCommands.FunLines(false));
        lines.AddRange(ChatCommands.Shortcuts.Select(s => ChatCommands.Teach(ChatCommands.Parse(s.Word)) ?? ChatCommands.UsageOf(ChatCommands.Parse(s.Word).Name)));
        foreach (var s in Bots.Settings)
        {
            lines.Add(ChatCommands.UsageOf($"!bots {s.Id}"));
            foreach (var c in s.Choices) { lines.Add(ChatReply.Already(s, c)); lines.Add(ChatReply.BotRefused(s, c, "Arms Race's ladder hands out the weapons, to the bots too")); }
        }
        lines.AddRange(Bots.Presets.Select(p => ChatReply.PresetRefused(p, "Wingman has room for 4 players, one of them yours")));
        lines.AddRange([ChatReply.NoBots, ChatReply.NoCard(98), ChatReply.VoteAlready("Play this map again"), ChatReply.VoteAlready(new string('x', 200))]);
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
            ChatReply.MenuOn("Maps"), ChatReply.ChatSearchNone("dust"), ChatReply.ChatSearchNone(new string('x', 200)), ChatReply.HowToSearch, ChatReply.Searching("dust"), ChatReply.Searching(new string('x', 200)), ChatReply.NoMatch("dust"), ChatReply.NoMatch(new string('x', 200)), ChatReply.Matches(3), ChatReply.YourVote("#12 aim_x"), ChatReply.YourVote(new string('x', 200))]);
        var longMap = new MapInfo(0, 7, "aim_" + new string('w', 96), 0);
        lines.AddRange([BlockedRefusal(longMap), RemovedRefusal(longMap), NonMapRefusal(longMap), ModeFullLine(longMap with { Mode = Modes.WingmanId }, 5), ModeFullLine(new MapInfo(0, 0, "de_dust2", 0, true, Modes.DeathmatchId), 17)]);

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
