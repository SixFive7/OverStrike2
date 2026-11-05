// --selftest, the message system's part: the catalogue (Messages.cs), the marks chat renders, the capitals rule over every template and everything the run made the
// bridge post, the routing (Post), the live state's new lines, and the views over them.
using System.Text.RegularExpressions;

static partial class Program
{
    /// <summary>Words a section may start with as they are in this run's made-up data: map names without an underscore or a digit, written that way on purpose.</summary>
    static readonly string[] TestNames = ["beta", "aim", "m ", "map ", "stock", "workshop ", "only", "keep", "fresh", "popular", "x", "glassbox", "started a vote"];

    static void MessagesSelfTest(Action<string, bool> Check)
    {
        // ---- the catalogue ----
        Check("Catalogue: every kind once, in the list, saying what it is, with a category", Messages.All.Select(k => k.Id).Distinct().Count() == Messages.All.Length
            && Messages.All.All(k => k.Row != "" && Messages.Categories.Contains(k.Cat) && Messages.Find(k.Id) == k));
        Check("Catalogue: the picks' surfaces: the top line, the countdown, the banner, the plain text, the alert, the card, the window",
            Messages.FunChanged.Surface == Surface.Top && Messages.MapCountdown.Surface == Surface.Countdown && Messages.RestartAfterMatch.Surface == Surface.Banner && Messages.Joined.Surface == Surface.Plain
            && Messages.CheckWarning.Surface == Surface.Alert && Messages.NowPlaying.Surface == Surface.Card && Messages.Welcome.Surface == Surface.Window && Messages.Answer.Surface == Surface.Line
            && Messages.CatchUpBoost is { Chat: Who.Player, On: Who.Player } && Messages.Bounty is { Chat: Who.Player, Board: Who.Nobody } && Messages.VoteRefused is { On: Who.Player, Chat: Who.Everybody, Board: Who.Everybody });

        // ---- the marks ----
        var palette = new M.Palette('P', 'C', 'A', 'V', 'R', 'D', name => name == "Remy" ? 'T' : null);
        var line = $"{M.N("Remy")} set {M.A("gravity")} to {M.V("Moon")} - Until the map changes";
        Check("Marks: chat renders a fun change in the functional colours, a name in its team's", M.Render(line, palette) == "TRemyP set AgravityP to VMoonP - Until the map changes");
        Check("Marks: every other surface has the words alone", M.Plain(line) == "Remy set gravity to Moon - Until the map changes");
        Check("Marks: a name nobody on the server has gets no colour of its own; a refusal's and a done line's plain words have theirs",
            M.Render($"{M.N("Nobody")} left", palette) == "NobodyP left" && M.Render(M.Refused($"No {M.C("!foo")}"), palette) == "RNo C!fooR" && M.Render(M.Done("Done"), palette) == "DDone");
        Check("Marks: a player cannot colour a line by naming themselves so", M.Plain(M.N("ab")) == "ab" && M.Render(M.N("ab"), palette) == "abP");
        var longLine = $"{M.N("Remy")} " + string.Join(" ", Enumerable.Repeat("word", 40));
        var cutLines = M.ChatLines(longLine);
        Check($"Chat lines: at most {Messages.ChatLineMax} characters each, nothing lost ({cutLines.Count} lines)", cutLines.Count == 2 && cutLines.All(l => M.Plain(l).Length <= Messages.ChatLineMax)
            && M.Plain(string.Join(" ", cutLines)) == M.Plain(longLine) && M.ChatLines(line).Count == 1);
        Check("Chat lines: %s1 in a name never reaches CS2's formatter", !M.Render(M.N("a%s1"), palette).Contains("%s"));
        Check("Centre text: the wire's line break comes back, no mark does", M.Centre(M.Lines("HUD check: No click yet", "Click any button within 15 s")) == "HUD check: No click yet\nClick any button within 15 s");

        // ---- the capitals rule ----
        Check("Capitals: what starts a section", Sections.Lower("Updated to CS2 1.41.9.6 - Welcome back") == null && Sections.Lower("Yours v4.5.0  ·  The server's v4.6.0") == null && Sections.Lower("+60% HP  ·  -25% damage taken  ·  Regeneration") == null
            && Sections.Lower("#1  ·  112 MB  ·  +12  -1") == null && Sections.Lower("38 s left  ·  4 of 4 voted") == null && Sections.Lower("!help - !play 12") == null && Sections.Lower("aim_rush won - Picked by the vote") == null
            && Sections.Lower("Updated to CS2 - welcome back") == "welcome back" && Sections.Lower("Yours  ·  the server's") == "the server's" && Sections.Lower("First\nsecond") == "second");
        Check("Capitals: the net under every message: after every separator, a map's name stays, the line's first word too (it is usually a name)", Sections.Capitalize("Server is downloading x: 61 percent - the map changes when it is ready") == "Server is downloading x: 61 percent - The map changes when it is ready"
            && Sections.Capitalize("glassbox froze - aim_x first") == "glassbox froze - aim_x first" && Sections.Capitalize("First  ·  second\nthird") == "First  ·  Second\nThird"
            && Sections.Capitalize($"{M.A("aim - by Bob")} won - by a mile") == $"{M.A("aim - by Bob")} won - By a mile");
        var templates = new List<string>();
        templates.AddRange(HudView.StaticTexts().Select(t => t.Text));
        templates.AddRange(Hud.Help.SelectMany(g => g.Rows).SelectMany(r => new[] { r.Command, r.Words, r.Also }));
        templates.AddRange(Hud.How.SelectMany(r => r.Lines.Append(r.Title)));
        templates.AddRange(Hud.Steps.SelectMany(s => new[] { s.Title, s.Command, s.Words }));
        templates.AddRange(new[] { Hud.Headline, Hud.MenuHint, HudView.LikeHint });
        foreach (var why in new[] { HudCheck.Cause.Review, HudCheck.Cause.Unavailable, HudCheck.Cause.Unknown })
            templates.AddRange(new[] { HudCheck.Dropped(why).Head, HudCheck.Dropped(why).Line, HudCheck.NoMenuChat(why, "23:59") });
        templates.AddRange(new[] { HudCheck.BackHead, HudCheck.BackLine, HudCheck.BackChat, M.Centre(HudCheck.Alert(15, false)), M.Centre(HudCheck.Alert(15, true)) });
        templates.AddRange(HudCheck.Warning("1", 15, false).Concat(HudCheck.Warning("1", 15, true)));
        templates.AddRange(Enum.GetValues<HudSlots.Page>().SelectMany(p => new[] { HudView.SubCaption(p, false, false), HudView.SubCaption(p, true, false), HudView.SubCaption(p, false, true) }));
        var sample = new HudState { Map = "AIM Map", By = "Bob", Size = "112 MB", Up = 3, Down = 1, Status = "Plays until its match ends - Or start a vote", ChangeMap = "Lake", ChangeBy = "the dice", ChangeIn = 8,
            RestartKind = "daily", RestartWhen = "now", RestartIn = 10, RestartAfter = "Back in a minute - Reconnect then (console: retry)", RematchBy = "Bob", RematchIn = 9, CrashNote = "Server restarted at 02:19 after a crash on bigjoe_water" };
        templates.AddRange(HudView.Texts(sample).Concat(HudView.Texts(new HudState { Map = "AIM Map", By = "the dice" })).Concat(HudView.PlayerTexts(sample, 1, "Bob", true)).Select(t => t.Text));
        foreach (var v in new[] { new VoteState(), new VoteState { WaitFor = 80 }, new VoteState { Round = 1, Open = true, SecondsLeft = 38, Present = 4, Cast = 2 } })
            templates.AddRange(HudView.PlayerTexts(new HudState(), 1, "Bob", false, v).Concat(HudView.Texts(new HudState(), v)).Select(t => t.Text));
        var lower = templates.Where(t => Sections.Lower(t, TestNames) != null).Select(t => $"\"{Sections.Lower(t, TestNames)}\" in \"{t}\"").Distinct().ToList();
        Check($"Capitals: every section of every template starts with one ({templates.Count} templates){(lower.Count > 0 ? ": " + string.Join("; ", lower.Take(12)) : "")}", lower.Count == 0);

        // The plugin's own lines: --selftest runs none of them, so it reads what the plugin's source says, notes and posts.
        if (Directory.Exists("../plugin/OverStrike2") && Directory.Exists("../plugin/CatchUp"))
        {
            var pluginLower = Directory.GetFiles("../plugin/OverStrike2", "*.cs").Concat(Directory.GetFiles("../plugin/CatchUp", "*.cs")).SelectMany(File.ReadAllLines)
                .Where(l => Regex.IsMatch(l, @"\b(Say|say|LocalNote|Post|FunRefuse)\(")).SelectMany(l => Regex.Matches(l, @"""[^""]*? - \p{Ll}[^""]*""").Select(m => m.Value)).Distinct().ToList();
            Check($"Capitals: every section of every line the plugin says starts with one{(pluginLower.Count > 0 ? ": " + string.Join("; ", pluginLower.Take(12)) : "")}", pluginLower.Count == 0);
        }

        // ---- a hold ----
        HudPolled(false);
        var heldWords = CannotCall(Now());
        Check("A hold: the plugin's poll says it draws nothing, so nobody can call a vote, in words that say why", HudHeld() && heldWords != null && heldWords.StartsWith("Steam is reviewing the HUD"));
        Thread.Sleep(1);
        HudPolled(true);
        Check("...a poll that draws ends it", !HudHeld());

        // ---- the routing ----
        var (sb, nb) = (Now, StateDir);
        var at = new DateTime(2026, 11, 5, 18, 0, 0, DateTimeKind.Utc);
        Now = () => at;
        try
        {
            ClearNotices();
            Post(Messages.FunChanged, $"{M.N("Bob")} set {M.A("gravity")} to {M.V("Moon")} - Until the map changes");
            var h = BuildHud(at);
            Check("Post, the top line: the plain words, its category, its seconds; the marked words in chat for everybody; the board's Lately; the older contents' news line",
                h is { TopText: "Bob set gravity to Moon - Until the map changes", TopCat: "fun", News: "Bob set gravity to Moon - Until the map changes" } && h.TopUntilMs - h.TopAtMs == 10000
                && h.Says is [(_, 0, var said)] && said.Contains('') && h.Lately is [{ Account: 0, Text: "Bob set gravity to Moon - Until the map changes" }]);
            const string me = "[U:1:777]";
            ClearNotices();
            Tell(me, M.Refused("Gravity is Moon already"));
            h = BuildHud(at);
            Check("Post, an answer: the line in that player's window, their chat, their board; nobody else's; no top line and no news line",
                h is { TopText: "", News: "" } && HudView.NoteOf(h, 777) == "Gravity is Moon already" && h.NoteCats[777] == "you" && h.Says is [(_, 777, _)] && h.Lately is [{ Account: 777 }]
                && Messages.LatelyFor(h.Lately, 888).Count == 0 && Messages.LatelyFor(h.Lately, 777).Count == 1);
            ClearNotices();
            Post(Messages.VoteRefused, $"{M.N("Bob")} tried to start a vote: The next one can start in {M.V("1 min 20 s")}", me);
            h = BuildHud(at);
            Check("Post, a refusal for everybody: the line for that player alone, chat and the board for everybody", HudView.NoteOf(h, 777) != "" && HudView.NoteOf(h, 888) == "" && h.Says is [(_, 0, _)] && h.Lately is [{ Account: 0 }]);
            ClearNotices();
            Post(Messages.Joined, $"{M.N("Erik")} joined with HUD {M.V("v4.6.0")}");
            h = BuildHud(at);
            Check("Post, the plain centre text: held for its 8 s, with its chat and board copies", h.Centres is [(_, 0, false, var until, var words)] && until == HudState.Ms(at.AddSeconds(8)) && M.Plain(words) == "Erik joined with HUD v4.6.0" && h.Says.Count == 1 && h.Lately.Count == 1);
            ClearNotices();
            Post(Messages.ServerDownload, "Server is downloading x: 25 percent", key: "download:x");
            Post(Messages.ServerDownload, "Server is downloading x: 26 percent", key: "download:x", chat: false);
            h = BuildHud(at);
            Check("A refreshed line: one row on the board, and chat only when asked", h.Lately is [{ Text: "Server is downloading x: 26 percent" }] && h.Says.Count == 1);
            at = at.AddSeconds(Messages.SayKeepSeconds + 1);
            Check("A chat line leaves the wire after its seconds", BuildHud(at).Says.Count == 0);

            // ---- the wire ----
            ClearNotices();
            var full = new HudState { TopText = "T", TopCat = "fun", TopAtMs = 5, TopUntilMs = 9000, BanTone = "problem", BanHead = "H", BanLine = "L", BanUntilMs = 8000, CrashNote = "C", ChangeKey = "12", RestartKind = "daily", RestartBy = "Bob", RestartAfter = "Back", RematchAtMs = 7000, RematchBy = "Eve",
                Lately = [new(1, 0, "k", "w")], Says = [(3, 4, "s" + M.N("n"))], Centres = [(6, 0, true, 9000, M.Lines("a", "b"))], Notes = new() { [7] = ("n", 3) }, NoteCats = new() { [7] = "you" } };
            var back = HudState.Decode(full.Encode())!;
            Check("The wire carries the top line, the banner, Lately, chat with its marks, the centre texts, the note's category and the countdowns' fields",
                back is { TopText: "T", TopCat: "fun", TopAtMs: 5, TopUntilMs: 9000, BanTone: "problem", BanHead: "H", BanLine: "L", BanUntilMs: 8000, CrashNote: "C", ChangeKey: "12", RestartBy: "Bob", RestartAfter: "Back", RematchAtMs: 7000, RematchBy: "Eve" }
                && back.Lately is [{ AtMs: 1, Account: 0, Key: "k", Text: "w" }] && back.Says is [(3, 4, var s)] && M.Plain(s) == "sn" && s.Contains('') && back.Centres is [(6, 0, true, 9000, var cw)] && M.Centre(cw) == "a\nb" && back.NoteCats[7] == "you");
            var asOf = back.AsOf(8500);
            Check("As of a moment: what has run out goes, the rematch counts down", asOf is { TopText: "T", BanHead: "" } && back.AsOf(9500).TopText == "" && back.AsOf(5000).RematchIn == 2 && new HudState().AsOf(1).RematchIn == -1);

            // ---- the views ----
            bool On(List<(string Id, string Class, bool On)> c, string id, string cls) => c.Any(x => x.Id == id && x.Class == cls && x.On);
            var seen = new HudState { TopText = "T", TopCat = "maps", BanTone = "done", BanHead = "H", CrashNote = "C" };
            var cls = HudView.Classes(seen);
            Check("The views: the top line's category and the banner's tone are one class each, the crash note's line shows", On(cls, HudView.Global, "top") && On(cls, HudView.TopPanel, "cat-maps") && !On(cls, HudView.TopPanel, "cat-fun")
                && On(cls, HudView.Global, "ban") && On(cls, HudView.BannerPanel, "tone-done") && !On(cls, HudView.BannerPanel, "tone-problem") && On(cls, HudView.Global, "crash")
                && HudView.Texts(seen).Single(t => t.Id == "gg_top_tab").Text == "Maps");
            string K(HudState st, string id) => HudView.Texts(st).Single(t => t.Id == id).Text;
            var change = new HudState { ChangeMap = "Lake", ChangeBy = "Bob", ChangeIn = 8 };
            var restart = new HudState { RestartKind = "player", RestartWhen = "now", RestartIn = 10, RestartBy = "Remy", RestartAfter = "Back in a minute - Reconnect then (console: retry)" };
            var daily = new HudState { RestartKind = "daily", RestartWhen = "now", RestartIn = 10, RestartAfter = "Back in a minute" };
            var waitsForEnd = new HudState { RestartKind = "daily", RestartWhen = "match-end" };
            Check("The countdown banner: a map change with its picture, a restart with the logo naming who pressed it, none while it waits for the match's end",
                K(change, "gg_k_eyebrow") == "Map change" && K(change, "gg_k_map") == "Lake" && K(change, "gg_k_by") == "Picked by Bob" && K(change, "gg_k_secs") == "8" && !On(HudView.Classes(change), HudView.Global, "cd-logo")
                && K(restart, "gg_k_eyebrow") == "Server" && K(restart, "gg_k_map") == "Remy is restarting the server" && K(restart, "gg_k_secs") == "10" && On(HudView.Classes(restart), HudView.Global, "cd-logo") && On(HudView.Classes(restart), HudView.Global, "changing")
                && K(daily, "gg_k_map") == "The server restarts in 10 s" && K(waitsForEnd, "gg_k_map") == "" && !On(HudView.Classes(waitsForEnd), HudView.Global, "changing"));
            var late = new HudState { Lately = [new(60_000, 0, "", "Old"), new(120_000, 5, "", "Mine"), new(150_000, 6, "", "Not mine"), new(180_000, 0, "k", "Newer"), new(170_000, 0, "k", "Older")] };
            var rows = HudView.LatelyTexts(late, 5, 240_000);
            string R(string id) => rows.Single(t => t.Id == id).Text;
            Check("The board's Lately: the three most recent of this player's, a key's newest only, with how long ago", R("gg_bl_w1") == "Newer" && R("gg_bl_t1") == "1 min" && R("gg_bl_w2") == "Mine" && R("gg_bl_t2") == "2 min" && R("gg_bl_w3") == "Old" && R("gg_bl_t3") == "3 min"
                && Messages.Ago(0, 30_000) == "Now" && Messages.Ago(0, 7_200_000) == "2 h" && HudView.LatelyClasses(new HudState(), 1).All(c => !c.On));
            var noted = new HudState { Notes = new() { [7] = ("Refused", 3) }, NoteCats = new() { [7] = "vote" } };
            var pc = HudView.PlayerClasses(noted, 7);
            Check("The window's line: the player's note, its category's colour and eyebrow", On(pc, HudView.Screen, "wl") && On(pc, HudView.LinePanel, "cat-vote") && !On(HudView.PlayerClasses(noted, 8), HudView.Screen, "wl")
                && HudView.PlayerTexts(noted, 7, "x", false).Single(t => t.Id == "gg_wl_eyebrow").Text == "Vote" && HudView.PlayerTexts(noted, 7, "x", false).Single(t => t.Id == "gg_wl_text").Text == "Refused");
            Check("The welcome: says welcome back to a returning player, the tagline is its large text", HudView.PlayerTexts(new HudState(), 1, "Bob", true).Single(t => t.Id == "gg_w_hello").Text == "Welcome back, Bob"
                && HudView.PlayerTexts(new HudState(), 1, "Bob", false).Single(t => t.Id == "gg_w_hello").Text == "Welcome, Bob" && HudView.StaticTexts().Single(t => t.Id == "gg_w_headline").Text == "Over the top - On the Huis");

            // ---- the plugin hands the views the vote, which only its source can show: without it the board says no vote is on, and the wait looks live ----
            const string screen = "../plugin/OverStrike2/HudScreen.cs";
            if (File.Exists(screen))
            {
                var code = File.ReadAllText(screen);
                Check("The plugin's frame hands every view the vote", code.Contains("HudView.Texts(shown, vote)") && code.Contains("HudView.Classes(shown, vote)") && code.Contains("view.Returning, vote)") && code.Contains("HudView.PlayerClasses(shown, account, vote)")
                    && code.Contains("search, mode.Id, vote)"));
                Check("A click on the map browser counts its matches with the search, as its page does", code.Contains("Matches(catalog, account, search).Count") && !code.Contains("Matches(catalog, account).Count"));
                Check("The window held by a vote follows the freeze's own decision", code.Contains("bool VoteLocked => vote.Round > 0 && Frozen?.Invoke() == true") && !code.Contains("if (vote.Round > 0 && vote.Freeze)"));
            }
            else Console.WriteLine($"skip the plugin's source checks: {screen} not found");
        }
        finally { Now = sb; StateDir = nb; ClearNotices(); }
    }

    /// <summary>At the end of a run: every message the run made the bridge post, by every path it went, holds the capitals rule.</summary>
    static void MessagesCapitalsCheck(Action<string, bool> Check)
    {
        var all = ToldAll ?? [];
        // A map's own title may have a section in lower case ("aim_map_reactor - by Burnzera"): its author's words, and whichever map the dice roll says them.
        var titles = File.Exists("maps.json")
            ? Regex.Matches(File.ReadAllText("maps.json"), @"""name"":\s*""([^""]*)""").SelectMany(m => Sections.Of(m.Groups[1].Value).Skip(1)).Where(s => char.IsLower(s[0])).Distinct().ToList() : [];
        var names = TestNames.Concat(titles).ToList();
        var lower = all.Where(t => Sections.Lower(t, names) != null).Select(t => $"\"{Sections.Lower(t, names)}\" in \"{M.Plain(t)}\"").Distinct().ToList();
        Check($"Capitals: every section of everything the bridge said in this run starts with one ({all.Distinct().Count()} different lines){(lower.Count > 0 ? $": {lower.Count} do not: " + string.Join("; ", lower) : "")}", lower.Count == 0);
        ToldAll = null;
    }
}
