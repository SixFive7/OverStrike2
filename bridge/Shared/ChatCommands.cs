// The chat commands. Every command is a shortcut for something the menu does: the plugin reads a chat line that starts with ! or /, turns it
// into what the menu's click would send (the bridge's actions on /act, the same Hud.IsAction words), or opens the menu's page, and answers the
// player privately.
//
// All is the one table: one primary per action, in the order !help lists them, each with its aliases. An alias runs the action and its
// reply teaches the primary by showing what ran ("You typed !map 12 - Ran !play 12"); !help lists primaries only. Each fun setting's id
// (Fun.cs) is a first-class command of its own: !gravity low, !drops off, !catchup off. The one word that means two things is settled in
// Primary and nowhere else: !maps with words is !find. And !nades is both a primary and a fun setting's id: on|off are the grenade drops (the
// drops setting), the setting's own choices (normal|big|huge) the grenades' blast; !catchup is a primary and the catch-up setting's id alike.
// Shortcuts are hidden words for a command with words of its own - !toobig is !down big, !lowgrav !gravity low, !zombies !bots zombies,
// !reset !fun default - and teach what they ran as an alias does. --selftest proves no word is anything else. While a vote is open !play and
// !back put their map on it, a bare !vote brings its window back and !vote 3 votes for card 3: every card has a number of its own for the whole
// vote, the same for everybody (VoteState.Option.N). A bare number is plain chat.
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
    /// and catch-up among them), a vote (Arg the card's number), the list in chat, the Fun page's Random or Default, a change on the Bots page (Arg
    /// "kick", "default", a preset's id, or "setting choice"), or nothing that is a command.</summary>
    public enum Kind { None, Bridge, Page, Top, Play, Find, Fun, Vote, Commands, Unknown, Usage, FunAll, Bots }

    /// <summary>A line as a command. Name: the primary word. Ran: the primary as it would be typed, what the reply calls it. Typed: the line as
    /// the player typed it when it was not that - an alias, a shortcut, or the long way round to a fun setting - which is what makes the reply teach.</summary>
    public sealed record Parsed(Kind Kind, string Arg = "", string Name = "", string Ran = "", string Typed = "");

    /// <summary>One action: its primary word, what follows it, what it does, the menu group it belongs to (the Help page's groups), and its aliases.</summary>
    public sealed record Command(string Word, string Args, string Words, string Group, params string[] Aliases);

    /// <summary>Every action, in the order !help lists them: nothing is an alias of two actions. Many hidden aliases each, so the word a player
    /// tries is likely one of ours; none a generic word that would set off something heavy (!fix and !reset no longer restart the server).</summary>
    public static readonly Command[] All =
    [
        new("!play", "<number, name or Workshop ID>", "Put a map to the vote, with your vote on it", "Maps", "!map", "!p", "!nominate", "!load", "!go", "!pick",
            "!download", "!dl", "!workshop", "!playmap"),
        new("!find", "<words>", "Search the maps: the menu opens on the matches", "Maps", "!search", "!s", "!lookup", "!f", "!findmap", "!searchmap", "!mapsearch", "!look", "!seek"),
        new("!maps", "", "Open the map browser", "Maps", "!browse", "!list", "!maplist", "!m", TopWord, "!browser", "!allmaps", "!mapmenu", "!catalog", "!library"),
        new("!next", "", "Start a vote for the next map, Keep playing on it", "Maps", "!n", "!rtv", "!change", "!skipmap", "!skip", "!voteskip", "!vs", "!boo",
            "!done", "!finish", "!end", "!release", "!letgo", "!over", "!new", "!rockthevote", "!votemap", "!mapvote", "!changemap", "!startvote", "!callvote"), // !done's words: there is no own map to be done with; !new: no new-maps setting any more
        new("!random", "", "Start a vote for the next map, with your vote on Random", "Maps", "!roll", "!dice", "!rand", "!any", "!shuffle", "!randommap", "!rollmap", "!randomvote"),
        new("!back", "", "The map before this one, to the vote", "Maps", "!previous", "!prev", "!last", "!undo", "!again", "!lastmap", "!prevmap", "!previousmap", "!goback", "!before"),
        new("!up", "[number]", "Like the map that is on, or that map", "Likes", "!like", "!+", "!good", "!love", "!thumbsup", "!upvote", "!likemap", "!+1", "!great", "!fav"),
        new("!down", "[number] [reason]", $"Dislike it, and why: {DownReasons.Words}", "Likes", "!dislike", "!-", "!bad", "!meh", "!thumbsdown", "!downvote", "!dislikemap", "!-1", "!hate", "!boring"),
        new("!vote", "[number]", "Vote for the card with that number - Alone: back to the vote for the next map", "The vote", "!v", "!mv", "!choose", "!ballot", "!votes", "!cast", "!votefor"),
        new("!fun", "", "The Fun page, each setting's own word (!gravity low), !fun random and !fun default", "Game", "!set", "!settings", "!toggle", "!mode", "!funmenu", "!funpage", "!modifiers", "!mods"),
        new("!nades", "<on|off>", "Grenade drops on or off", "Game", "!grenades", "!nade", "!he", "!boom", "!grenade"), // !drops is the drops setting's own word (Fun.cs)
        new("!catchup", "<on|off>", "Catch-up boosts on or off, until the map changes", "Game", "!catch", "!boost", "!boosts", "!comeback", "!rubberband", "!handicap", "!underdog"), // a fun setting's own word too (Fun.cs)
        new("!bots", "[number, preset, kick or default]", "The Bots page - !bots 4, !bots zombies, !bots kick", "Game", "!bot", "!botmenu", "!ai", "!npc", "!npcs", "!botpage", "!addbots", "!addbot", "!botcount"),
        new("!time", "[10|60]", "Match length in minutes, this match too - Wingman's in rounds, 8 or 16 - Alone it switches", "Game", "!length", "!minutes", "!timelimit", "!roundtime", "!duration",
            "!matchlength", "!matchtime", "!mins", "!rounds"),
        new("!restart", "", "Restart a stuck server - Twice to confirm", "Game", "!reboot", "!unstuck", "!stuck", "!restartserver", "!serverrestart", "!rebootserver", "!serverreboot"),
        new("!rematch", "", "Restart the match - Twice to confirm", "Game", "!restartmatch", "!matchrestart", "!restartgame", "!rr", "!newmatch", "!resetmatch", "!matchreset", "!newgame"),
        new("!cancel", "", "Stop your own countdown: a pick, Previous, Restart match or the server's restart", "Game", "!stop", "!abort", "!nvm", "!nevermind", "!hold", "!wait", "!norestart", "!norematch"),
        new("!menu", "", "Open the full menu", "Menu", "!gg", "!hud", "!os", "!os2", "!open", "!mainmenu", "!overstrike", "!panel", "!home", "!window"),
        new("!help", "", "The Help page, and this list", "Menu", "!commands", "!cmds", "!h", "!?", "!info", "!command", "!cmd", "!helpme", "!support", "!guide"),
        new("!how", "", "How it works: every rule and chance of a map pick", "Menu", "!howitworks", "!odds", "!chances", "!why", "!explain",
            "!rules", "!rule", "!r", "!fair", "!howto", "!policy", "!faq"), // the Rules page went into How it works
        new("!players", "", "The Players page: who is on, their side, their HUD and time on", "Menu", "!who", "!online", "!people", "!roster", "!playerlist", "!humans"),
        new("!sync", "", "The Sync page: what the map sync does now, its queue and its history", "Menu", "!status", "!dashboard", "!syncstatus", "!downloads", "!mapsync", "!queue", "!progress"),
    ];

    /// <summary>!top, the old word: the map browser sorted by votes.</summary>
    public const string TopWord = "!top";

    /// <summary>A hidden word for a command with words of its own: Line is what it runs, _ where what the player typed after the word goes - or, typed
    /// alone, Default. !toobig 12 runs !down 12 big; !headshots runs !hs on, !headshots off !hs off; !grav low runs !gravity low. The reply teaches the
    /// line ("You typed !toobig - Ran !down big"); !help lists none of them.</summary>
    public sealed record Shortcut(string Word, string Line, string Default = "");

    static Shortcut[] Words(string line, string def, params string[] words) => [.. words.Select(w => new Shortcut(w, line, def))];

    /// <summary>Every shortcut, grouped by what it runs: the Fun page's Random and Default, the dislike's reasons, each fun setting, the Bots page's
    /// rows, presets, Kick all bots and Default.</summary>
    public static readonly Shortcut[] Shortcuts =
    [
        .. Words($"!fun {Fun.RandomWord}", "", "!randomize", "!funrandom", "!chaos", "!surprise", "!randomfun"),
        .. Words($"!fun {Fun.DefaultWord}", "", "!defaults", "!fundefault", "!reset", "!vanilla", "!funreset", "!resetfun"),
        .. Words("!down _ big", "", "!toobig", "!toolarge", "!bigmap", "!hugemap", "!oversized"),
        .. Words("!down _ ads", "", "!ads", "!ad", "!advert", "!adverts", "!advertising"),
        .. Words("!down _ small", "", "!toosmall", "!tiny", "!cramped", "!smallmap", "!tinymap"),
        .. Words("!down _ broken", "", "!broken", "!bugged", "!buggy", "!glitched", "!glitchy"),
        .. Words("!down _ slow", "", "!laggy", "!stutter", "!lowfps", "!slowmap", "!runsbadly"),
        .. Words("!gravity _", "", "!grav"),
        .. Words("!gravity low", "", "!lowgrav", "!lowgravity"),
        .. Words("!gravity moon", "", "!moon", "!moongrav"),
        .. Words("!jump _", "", "!jumps"),
        .. Words("!jump high", "", "!superjump", "!highjump", "!bigjump", "!jumphigh"),
        .. Words("!bhop _", "on", "!bunnyhop", "!bunnyhopping", "!bunny", "!autobhop"),
        .. Words("!bhop turbo", "", "!turbo", "!turbobhop"),
        .. Words("!fall _", "", "!falldamage", "!falls"),
        .. Words("!fall off", "", "!nofall", "!nofalldamage", "!softlanding"),
        .. Words("!ice _", "on", "!icefloor", "!icy", "!slippery", "!skate", "!skating"),
        .. Words("!aim _", "on", "!laser", "!lasers", "!laserguns", "!nospread", "!norecoil"),
        .. Words("!ammo _", "on", "!infiniteammo", "!infammo", "!noreload", "!unlimitedammo", "!infinite"),
        .. Words("!hs _", "on", "!headshots", "!headshot", "!hsonly", "!onlyhs", "!headshotsonly"),
        .. Words("!vampire _", "on", "!vamp", "!vampires", "!lifesteal", "!leech", "!vampiric"),
        .. Words("!ghost _", "on", "!ghosts", "!invisible", "!invis", "!stealth", "!vanish"),
        .. Words("!nades big", "", "!bignades", "!biggrenades", "!blast"),
        .. Words("!nades huge", "", "!hugenades", "!kaboom"),
        .. Words("!damage _", "on", "!doubledamage", "!double", "!2x", "!dmg", "!doubledmg"),
        .. Words("!ffa _", "on", "!freeforall", "!allvsall", "!allvall", "!noteams", "!ffamode"),
        .. Words("!ff _", "", "!friendlyfire", "!teamdamage", "!teamkill", "!tk", "!teamfire"),
        .. Words("!instakill _", "", "!insta", "!onehit", "!oneshot", "!instakills", "!onehitkill"),
        .. Words("!ffnades _", "", "!teamnades", "!ffgrenades", "!nadetk", "!teamgrenades", "!ffnade"),
        .. Words("!drops _", "", "!nadedrops", "!grenadedrops", "!dropnades", "!dropgrenades", "!grenadedrop"),
        .. Words("!bots botfill _", "", "!botfill", "!fillto", "!fillbots", "!quota", "!botquota"),
        .. Words("!bots botteam _", "", "!botteam", "!botside", "!botsteam"),
        .. Words("!bots botteam t", "", "!botst"),
        .. Words("!bots botteam ct", "", "!botsct"),
        .. Words("!bots botlevel _", "", "!botlevel", "!difficulty", "!botdiff", "!botskill", "!botdifficulty"),
        .. Words("!bots botweapons _", "", "!botweapons", "!botweapon", "!botguns", "!botgun", "!botloadout"),
        .. Words("!bots botchatter _", "", "!botchatter", "!botchat"),
        .. Words("!bots botchatter radio", "", "!botradio"),
        .. Words("!bots botchatter quiet", "", "!quietbots", "!silentbots"),
        .. Words("!bots botfire _", "", "!botfire"),
        .. Words("!bots botfire _", "on", "!holdfire", "!dontshoot", "!nobotfire", "!harmlessbots"),
        .. Words("!bots botcrouch _", "on", "!botcrouch", "!botsduck", "!botduck", "!crouchbots", "!botsneak"),
        .. Words("!bots botmove _", "", "!botmove"),
        .. Words("!bots botmove walk", "", "!botwalk", "!walkbots"),
        .. Words("!bots botmove frozen", "", "!botfreeze", "!freezebots", "!frozenbots"),
        .. Words("!bots botmimic _", "on", "!botmimic", "!mimic", "!botcopy", "!mimicbots", "!copybots"),
        .. Words("!bots botwar _", "on", "!ignoreme", "!botsignore", "!ignoreplayers", "!botsignoreme"),
        .. Words("!bots bottag _", "", "!bottag", "!botname", "!botprefix", "!botnames"),
        .. Words("!bots bottag zombie", "", "!zombietag"),
        .. Words("!bots zombies", "", "!zombies", "!zombie", "!horde", "!zombiehorde", "!undead"),
        .. Words("!bots duel", "", "!duel", "!sniperduel", "!awpduel", "!botduel", "!duelbot"),
        .. Words("!bots targets", "", "!targets", "!targetpractice", "!dummies", "!aimtrain", "!aimtraining"),
        .. Words("!bots copycats", "", "!copycats", "!copycat", "!clones", "!mimics", "!clonebots"),
        .. Words("!bots war", "", "!botwar", "!botsfight", "!botfight", "!botbattle", "!botbrawl"),
        .. Words("!bots pistols", "", "!pistolparty", "!pistols", "!pistolbots", "!deagles", "!pistolsparty"),
        .. Words($"!bots {Bots.KickWord}", "", "!kickbots", "!kickbot", "!nobots", "!botkick", "!removebots", "!botsoff"),
        .. Words($"!bots {Bots.DefaultWord}", "", "!botdefault", "!botsdefault", "!resetbots", "!botreset", "!botsnormal"),
    ];

    /// <summary>The line a shortcut runs with what was typed after it: in its place, or its Default when nothing was.</summary>
    public static string Expand(Shortcut s, string arg)
    {
        var rest = arg.Trim() != "" ? arg.Trim() : s.Default;
        var line = s.Line.Contains('_') ? s.Line.Replace("_", rest) : rest == "" ? s.Line : $"{s.Line} {rest}";
        return string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    static readonly Dictionary<string, string> aliases = AliasMap();
    static readonly Dictionary<string, Shortcut> shortcuts = ShortcutMap();

    static Dictionary<string, string> AliasMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in All)
            foreach (var a in c.Aliases) map.TryAdd(a, c.Word); // a word listed twice is what --selftest looks for; nothing here may throw in the plugin
        return map;
    }

    static Dictionary<string, Shortcut> ShortcutMap()
    {
        var map = new Dictionary<string, Shortcut>(StringComparer.Ordinal);
        foreach (var s in Shortcuts) map.TryAdd(s.Word, s); // likewise
        return map;
    }

    public static Command? Find(string word) => All.FirstOrDefault(c => c.Word.Equals(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>A card's number as !vote takes it: one or two digits, 1 up to the last a card can have - Random's (MapVote.RandomOption) is no card's and
    /// nobody types it: !vote random is Random.</summary>
    public static bool IsCardNumber(string s) => s.Length is 1 or 2 && s.All(char.IsAsciiDigit) && s[0] != '0' && int.Parse(s, System.Globalization.CultureInfo.InvariantCulture) < MapVote.RandomOption;

    /// <summary>The words !vote takes for Random's button.</summary>
    public static readonly string[] RandomVoteWords = ["random", "dice", "roll"];

    /// <summary>The primary a typed word runs: its alias's, and !maps with words is !find.</summary>
    static string Primary(string word, string arg)
    {
        var primary = aliases.GetValueOrDefault(word, word);
        return primary == "!maps" && arg != "" ? "!find" : primary;
    }

    /// <summary>The chat line as a command: None when it is no command (it does not start with ! or /), Unknown for a word that is none of
    /// ours, Usage when the command needs something it did not get. A shortcut runs its line and teaches it. A bare number is plain chat, as
    /// every chat line that is no command.</summary>
    public static Parsed Parse(string? line)
    {
        var text = (line ?? "").Trim();
        if (text.Length < 2 || text[0] is not ('!' or '/')) return new(Kind.None);
        var parts = text[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return new(Kind.None);
        var typed = "!" + parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        if (shortcuts.GetValueOrDefault(typed) is { } shortcut)
        {
            var ran = Parse(Expand(shortcut, arg)); // the line it stands for, as if typed: never a shortcut itself (--selftest)
            return ran.Kind is Kind.Usage or Kind.Unknown or Kind.None ? ran : ran with { Typed = text };
        }
        var word = Primary(typed, arg);
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
            "!vote" => arg == "" ? new(Kind.Page, "Maps", word) // alone: back to the vote, or outside one the maps, where one starts
                : IsCardNumber(low) ? new(Kind.Vote, low, word)
                : RandomVoteWords.Contains(low) ? new(Kind.Vote, MapVote.RandomOption.ToString(System.Globalization.CultureInfo.InvariantCulture), word, low == "random" ? "" : "!vote random") : Usage(),
            "!fun" => FunCommand(low, word),
            "!nades" => low is "on" or "off" ? new(Kind.Fun, $"drops {low}", word)
                : Fun.Find("nades") is { } size && Fun.Find(size, low) is { } blast ? new(Kind.Fun, $"{size.Id} {blast.Id}", word) : Usage(),
            "!bots" => BotsCommand(low, word),
            "!time" => low is "10" or "60" or "8" or "16" ? new(Kind.Bridge, $"!time {low}", word) : low == "" ? new(Kind.Bridge, "!time", word) : Usage(), // alone: the other length (release 198); 8|16 Wingman's rounds (199)
            "!restart" => new(Kind.Bridge, "!restart", word),
            "!rematch" => new(Kind.Bridge, "!rematch", word),
            "!cancel" => arg == "" ? new(Kind.Bridge, Hud.CancelMine, word) : Usage(), // the player's own newest countdown (the bridge's CancelMine)
            "!menu" => new(Kind.Page, "Start", word),
            "!help" => new(Kind.Commands, "Help", word),
            "!how" => new(Kind.Page, "How", word),
            "!players" => new(Kind.Page, "Players", word),
            "!sync" => new(Kind.Page, "Sync", word),
            _ when Fun.Find(word[1..]) is { } s => Fun.Find(s, low) is { } c ? new(Kind.Fun, $"{s.Id} {c.Id}", word) : Usage(),
            _ => new(Kind.Unknown, "", word),
        };
    }

    /// <summary>!fun alone is the page. !fun gravity low, the long way round, runs the setting's own word and says so; !fun nades on|off the
    /// grenade drops (the drops setting); !fun random and !fun default the page's Random and Default. A setting alone toggles nothing: that word's usage line.</summary>
    static Parsed FunCommand(string low, string word)
    {
        var p = low.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p is [Fun.RandomWord] or [Fun.DefaultWord]) return new(Kind.FunAll, p[0], word);
        if (p.Length == 0) return new(Kind.Page, "Fun", word);
        if (p is ["nades", "on" or "off"]) return new(Kind.Fun, $"drops {p[1]}", "!nades", $"!nades {p[1]}");
        if (Fun.Find(p[0]) is not { } s) return new(Kind.Usage, "", word);
        return p.Length == 2 && Fun.Find(s, p[1]) is { } c ? new(Kind.Fun, $"{s.Id} {c.Id}", $"!{s.Id}", $"!{s.Id} {c.Id}") : new(Kind.Usage, "", $"!{s.Id}");
    }

    /// <summary>!bots alone is the Bots page. !bots 4 sets how many play beside the players (off, 0 and none are Kick all bots); !bots zombies starts
    /// a preset; !bots kick and !bots default are the page's two buttons; !bots botlevel hard - or !bots level hard - sets a row, and a choice only one
    /// row has names it alone (!bots hard). Arg: kick, default, a preset's id, or "setting choice"; Ran says what ran when that is not what was typed.</summary>
    static Parsed BotsCommand(string low, string word)
    {
        var p = low.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Parsed Ran(string arg, string shown) => new(Kind.Bots, arg, word, shown == string.Join(' ', p) ? "" : $"!bots {shown}");
        switch (p)
        {
            case []: return new(Kind.Page, "Bots", word);
            case ["off" or "0" or "none" or Bots.KickWord]: return Ran(Bots.KickWord, Bots.KickWord);
            case [Bots.DefaultWord]: return Ran(Bots.DefaultWord, Bots.DefaultWord);
            case [var one] when (Bots.FindPreset(one) ?? Bots.FindPreset(one + "s")) is { } preset: return Ran(preset.Id, preset.Id); // !bots zombie is the horde too
            case [var one] when Bots.Find("bots") is { } count && Fun.Find(count, one) is { } n: return Ran($"{count.Id} {n.Id}", n.Id);
            case [var one] when RowOf(one) is { } row: return Ran($"{row.Id} {one}", one);
            case [var one] when BotSetting(one) is { } alone: return new(Kind.Usage, "", $"!bots {alone.Id}");
            case [var id, var choice] when BotSetting(id) is { } setting:
                return Fun.Find(setting, choice) is { } c ? Ran($"{setting.Id} {c.Id}", $"{setting.Id} {c.Id}") : new(Kind.Usage, "", $"!bots {setting.Id}");
            default: return new(Kind.Usage, "", word);
        }
    }

    /// <summary>A bot setting by its id, or its id without "bot": botlevel or level.</summary>
    static Fun.Setting? BotSetting(string id) => Bots.Find(id) ?? (id.StartsWith("bot", StringComparison.Ordinal) ? null : Bots.Find("bot" + id));

    /// <summary>The one bot setting a choice belongs to, when only one has it (hard, knives, quiet, frozen, t): the counts and every row's Normal aside.</summary>
    static Fun.Setting? RowOf(string choice) =>
        Bots.Settings.Where(s => !Bots.IsCount(s) && s.Choices.Any(c => c != s.Normal && c.Id == choice)).ToList() is [var only] ? only : null;

    /// <summary>What a command needs, for its usage line: "!vote [number]: Vote for the card with that number". A fun setting's word too, and a row of the
    /// Bots page as !bots names it ("!bots botlevel").</summary>
    public static string UsageOf(string word)
    {
        if (word == "!nades" && Find(word) is { } nades && Fun.Find("nades") is { } size)
            return $"{M.C("!nades")} {M.V("on|off")}: {nades.Words}  ·  {M.C("!nades")} {M.V(ChoiceWords(size))}: {size.Words}";
        if (word.StartsWith("!bots ", StringComparison.Ordinal) && Bots.Find(word[6..]) is { } row)
            return $"{M.C("!bots")} {M.A(row.Id)} {M.V(ChoiceWords(row))}: {Sections.Capitalize(row.Words)}";
        if (Find(word) is { } c) return $"{M.C(c.Word)}{(c.Args != "" ? " " + ArgsMarked(c.Args) : "")}: {WordsMarked(c.Words)}";
        return word.Length > 1 && Fun.Find(word[1..]) is { } s ? $"{M.C($"!{s.Id}")} {M.V(ChoiceWords(s))}: {s.Words}" : UnknownLine(word);
    }

    /// <summary>What follows a command in its usage line: the few it can be (&lt;on|off&gt;, &lt;10|60&gt;) are values, anything else what the command is about.</summary>
    static string ArgsMarked(string args) => args.Contains('|') ? M.V(args) : M.A(args);

    /// <summary>A command's own words in its usage line: a command they name is one, with the value it is named with (!gravity low, !fun random, !bots 4); the reasons !down takes are values.</summary>
    static string WordsMarked(string words) =>
        Regex.Replace(words, @"(![a-z]+)( [a-z0-9]+)?", m => Parse(m.Value) is { Kind: Kind.Fun or Kind.FunAll or Kind.Bots } p ? Marked(m.Value, p) : M.C(m.Groups[1].Value) + m.Groups[2].Value)
            .Replace(DownReasons.Words, M.V(DownReasons.Words));

    /// <summary>A command line as the reply that teaches shows it: its word a command, what follows an argument - and the last word a value where the command takes one of a few
    /// (!gravity moon, !fun gravity moon, !time 60, !down 12 big, !bots zombies, !vote 3). "!maps, sorted by likes" has words after its command, no argument.</summary>
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

    static bool IsValue(Parsed p, string word) => p.Kind is Kind.Fun or Kind.FunAll or Kind.Bots or Kind.Vote || p.Name == "!time" || (p.Name == "!down" && DownReasons.All.Any(r => r.Word.Equals(word, StringComparison.OrdinalIgnoreCase)));

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

    /// <summary>The words that are chat and nothing else: a line of only these is no search (233), whatever the case and the punctuation ("GG!", "gg wp").</summary>
    public static readonly string[] ChatWords = ["gg", "gl", "hf", "glhf", "wp", "ty", "thx", "lol", "xd", "ok", "nice", "ns", "np", "ez"];

    /// <summary>A chat line typed while that player's window is on MAPS > Browse (HudFrame.BrowserOpen), as the words it searches the browser for (233) - or null for a line
    /// that is no search: a command (! or /: it runs as it does everywhere, and !s still searches), a Workshop link or id (it opens that item's card, as anywhere), a bare
    /// number while a vote is open (plain chat: !vote 3 is the vote), a line of nothing but chat words (ChatWords) or with no letter or digit at all, and any line while the browser is not on the screen.
    /// All chat and team chat alike, and the line still shows in chat as it was typed: this only reads it. The words become the search only when they find a map (HudScreen.ChatSearch).</summary>
    public static string? SearchOf(string? line, bool browserOpen, bool voteOpen = false)
    {
        var text = (line ?? "").Trim();
        if (!browserOpen || Parse(text).Kind != Kind.None || WorkshopCard.IdInChat(text) != null) return null;
        var words = HudSearch.Words(text);
        if (words.Length == 0 || words.All(w => ChatWords.Contains(w))) return null;
        return voteOpen && text.All(char.IsAsciiDigit) ? null : text; // a bare number while a vote is open is chat, never a vote: that is !vote 3
    }

    /// <summary>What chat says for a word that is no command of ours.</summary>
    public static string UnknownLine(string word) => M.Refused($"Unknown command {M.A(Hud.Clean(word, 40))} - {M.C("!help")} lists them (and the menu: hold TAB, press E)");

    /// <summary>Whether a name as a line shows it is this player's: whole, or clipped with a ~ at its end (Clip, Hud.Clean), a " in it said as ' (the plugin's fun lines). Chat's team colour goes by it.</summary>
    public static bool IsName(string shown, string name)
    {
        var whole = Hud.Clean(name, 64).Replace('"', '\'');
        return shown == whole || (shown.Length > 1 && shown.EndsWith('~') && whole.StartsWith(shown[..^1], StringComparison.Ordinal));
    }
}
