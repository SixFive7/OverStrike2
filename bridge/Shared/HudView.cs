// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Globalization;

/// <summary>What goes on screen: label texts by panel id and classes by panel id, out of a HudState (and the vote's VoteState). Global ones
/// are the same for everybody and are set once for all players; per-player ones (your like, your note) are set per player. The plugin only
/// sends what changed since the last time.</summary>
public static class HudView
{
    /// <summary>Panel ids the plugin addresses. The layout's root may not carry an id, so global classes go on gg_global and a player's
    /// own state (window open, page, toast, banner) on gg_screen inside it.</summary>
    public const string Global = "gg_global", Screen = "gg_screen";

    public const string ButtonClose = "gg_btn_close", ButtonUp = "gg_btn_up", ButtonDown = "gg_btn_down", ButtonAct = "gg_btn_act";

    /// <summary>The board's buttons (TAB): your like on the map that is on, Start a vote, and the full menu.</summary>
    public const string BoardUp = "gg_b_vup", BoardDown = "gg_b_vdown", BoardNext = "gg_b_next", BoardOpen = "gg_b_open";

    public const string TabHow = "gg_tab_how", TabFun = "gg_tab_fun", TabGame = "gg_tab_game", TabMaps = "gg_tab_maps";

    /// <summary>The window's tabs and sub-tabs: START, MAPS (Browse and, while a vote is on, Vote - the vote's own buttons, HudVote.BackButton and
    /// BrowseButton), FUN, SERVER (Players, Game, Sync) and HELP (Help, How it works). A main tab opens its first page; a sub-tab its own. The
    /// tabs of the contents before v4.6.0 keep working under their ids - Start, Maps, Game, Fun, Help, How it works, Sync - and an older content's
    /// Rules tab opens How it works, where its words went; its Queue tab does nothing (there is no queue).</summary>
    public static readonly (string Id, HudSlots.Page Page)[] Tabs =
        [("gg_tab_start", HudSlots.Page.Start), (TabMaps, HudSlots.Page.Maps), (TabGame, HudSlots.Page.Game), ("gg_tab_help", HudSlots.Page.Help),
         (TabHow, HudSlots.Page.How), (TabFun, HudSlots.Page.Fun), (HudDash.Tab, HudSlots.Page.Sync), ("gg_tab_server", HudSlots.Page.Players),
         ("gg_sub_players", HudSlots.Page.Players), ("gg_sub_help", HudSlots.Page.Help), ("gg_tab_rules", HudSlots.Page.How)];

    /// <summary>The caption beside the sub-tabs, per page (a variable on gg_screen, SubCaptionVar): what the page is for; the Maps page's says
    /// whether a Play adds to the vote that is on.</summary>
    public const string SubCaptionVar = "ossub";

    public static string SubCaption(HudSlots.Page page, bool ballot, bool browsingVote) => page switch
    {
        HudSlots.Page.Start => "",
        HudSlots.Page.Maps => ballot ? "Click a map to vote, yours again to take it back" : browsingVote ? "Browse: Play puts a map on this vote, with your vote"
            : "Every map: Play puts it to the vote",
        HudSlots.Page.Fun => "Anybody, at any time, for everybody",
        HudSlots.Page.Players => "Who is on, and which HUD their game shows",
        HudSlots.Page.Game => "The match length, and the two restarts: the match or the whole server",
        HudSlots.Page.Sync => "The Workshop sync: what it does now, its queue, its history",
        HudSlots.Page.Help => "Where everything is",
        _ => "Every rule and chance of a map pick",
    };

    /// <summary>The words that never change: help, how it works, the start page. Sent once per layout entity (they are global too).</summary>
    public static List<(string Id, string Text)> StaticTexts()
    {
        var t = new List<(string, string)>
        {
            ("gg_w_headline", Hud.Headline), ("gg_w_hint", Hud.MenuHint),
            ("gg_b_cmds", HudTab.BoardLine(HudTab.Mode.Off)),
        };
        for (var i = 0; i < Hud.Steps.Length; i++)
        {
            t.Add(($"gg_s{i + 1}_title", Hud.Steps[i].Title));
            t.Add(($"gg_s{i + 1}_cmd", Hud.Steps[i].Command));
            t.Add(($"gg_s{i + 1}_txt", Hud.Steps[i].Words));
        }
        for (var g = 0; g < Hud.Help.Length; g++)
        {
            t.Add(($"gg_c{g + 1}_name", Hud.Help[g].Name));
            for (var r = 0; r < Hud.RowsPerGroup; r++)
            {
                var row = r < Hud.Help[g].Rows.Length ? Hud.Help[g].Rows[r] : null;
                t.Add(($"gg_c{g + 1}_{r + 1}_cmd", row?.Command ?? ""));
                t.Add(($"gg_c{g + 1}_{r + 1}_txt", row == null ? "" : row.Also == "" ? row.Words : $"{row.Words}  ·  {row.Also}"));
            }
        }
        for (var s = 0; s < Hud.HowSections; s++)
        {
            var how = s < Hud.How.Length ? Hud.How[s] : null;
            t.Add(($"gg_h{s + 1}_title", how?.Title ?? ""));
            for (var l = 0; l < Hud.HowLines; l++) t.Add(($"gg_h{s + 1}_{l + 1}_txt", how != null && l < how.Lines.Length ? how.Lines[l] : ""));
        }
        t.Add((HudMenu.FunNote, HudMenu.FunNoteText));
        t.Add(("gg_w_back_eyebrow", "Now playing"));
        t.Add(("gg_w_back_hint", LikeHint));
        return t;
    }

    /// <summary>Rows of the help and how-it-works pages that hold something: the layout collapses the rest. And c31 and c32 on gg_screen, for
    /// everybody: the contents before v4.6.0 show their How it works, Fun and Help rows only under them, and every content in use holds those panels.</summary>
    public static List<(string Id, string Class, bool On)> StaticClasses()
    {
        var c = new List<(string, string, bool)> { (Screen, "c31", true), (Screen, "c32", true) };
        for (var g = 0; g < Hud.Help.Length; g++)
            for (var r = 0; r < Hud.RowsPerGroup; r++) c.Add(($"gg_c{g + 1}_{r + 1}", "used", r < Hud.Help[g].Rows.Length));
        for (var s = 0; s < Hud.HowSections; s++)
            for (var l = 0; l < Hud.HowLines; l++) c.Add(($"gg_h{s + 1}_{l + 1}", "used", s < Hud.How.Length && l < Hud.How[s].Lines.Length));
        return c;
    }

    /// <summary>The classes the plugin sends only for v4.5.0, which this content's stylesheet has no rule for: c31 and c32 (StaticClasses), its news line and
    /// note (news, note), and its fixed Fun rows' marks (nd-on, nd-off, cu-on, cu-off: HudVote.FixedRowClasses).</summary>
    public static readonly string[] OlderContentClasses = ["c31", "c32", "news", "note", "nd-on", "nd-off", "cu-on", "cu-off"];

    /// <summary>The panels only v4.5.0 has of the contents still drawn for: its news line, its note, its lede.</summary>
    public static readonly string[] OlderContentTexts = ["gg_news_text", "gg_note_text", "gg_w_lede"];

    public const string LikeHint = "Like it? Hold TAB and click Like or Dislike.";

    /// <summary>The board's one line about the vote, about this many characters at the most.</summary>
    public const int QueueLineMax = 52;

    /// <summary>The board's vote line: the vote that is on, its result, the wait before the next one, or how to call one.</summary>
    public static string VoteLine(VoteState? v) => v == null || v.Round <= 0
        ? (v?.WaitFor > 0 ? $"The next vote can start in {MapVote.Left(v.WaitFor)}" : "No vote on: Play on a map, or Start a vote")
        : v.Open ? $"The vote is on: {v.SecondsLeft} s left  ·  {v.Cast} of {v.Present} voted"
        : v.Result is { } r ? Hud.Clean($"The vote {(v.Kept ? "kept" : "chose")} {r.Label}", QueueLineMax) : "The vote is over";

    /// <summary>The live texts, the same for everybody. The board (b) and the window's live column (w) show the same facts; the column also lists
    /// the vote's leading cards and the most liked maps, the board has the one vote line.</summary>
    public static List<(string Id, string Text)> Texts(HudState s, VoteState? v = null)
    {
        var map = s.Map == "" ? "Waiting for the server" : s.Map;
        var by = Hud.ByWords(s.By);
        var byline = string.Join("  ·  ", new[] { by, s.Size }.Where(x => x != ""));
        var status = s.Loading ? "Loading - Everyone reconnects in a moment" : s.Status;
        var t = new List<(string, string)>();
        foreach (var p in new[] { "b", "w" })
        {
            t.Add(($"gg_{p}_map", map));
            t.Add(($"gg_{p}_by", byline));
            t.Add(($"gg_{p}_up", s.Up.ToString(CultureInfo.InvariantCulture)));
            t.Add(($"gg_{p}_down", s.Down.ToString(CultureInfo.InvariantCulture)));
            t.Add(($"gg_{p}_status", status));
        }
        var leading = Leading(v);
        for (var i = 0; i < HudState.ListLength; i++)
        {
            var n = i < leading.Count ? leading[i] : null;
            t.Add(($"gg_w_n{i + 1}", n?.Label ?? ""));
            t.Add(($"gg_w_nw{i + 1}", n == null ? "" : $"{n.Votes} vote{(n.Votes == 1 ? "" : "s")}"));
            var top = i < s.Top.Count ? s.Top[i] : ("", 0, 0);
            t.Add(($"gg_w_t{i + 1}", top.Item1));
            t.Add(($"gg_w_ts{i + 1}", top.Item1 == "" ? "" : $"+{top.Item2}{(top.Item3 > 0 ? $" -{top.Item3}" : "")}"));
        }
        t.Add(("gg_w_nempty", v is { Round: > 0 } ? "No votes yet: click a card on the Maps page." : "No vote on. Play on a map puts it to the vote."));
        t.Add(("gg_w_tempty", "No likes yet. Like a map: its card's up arrow."));
        t.Add(("gg_b_queue", VoteLine(v)));
        t.Add(("gg_rst_text", s.RestartKind == "" ? "" : s.RestartWords)); // a restart coming: its words in the window's foot (its countdown: RestartCountdown)
        t.Add(("gg_news_text", s.NewsFor > 0 ? s.News : ""));
        t.Add(("gg_b_count", s.Maps > 0 ? $"{s.Maps} maps" : ""));
        t.Add(("gg_w_lede", ""));
        t.Add(("gg_w_back", string.Join("  ·  ", new[] { map, byline, Hud.Tally(s.Up, s.Down) }.Where(x => x != ""))));
        t.Add(("gg_w_crash", s.CrashNote));
        t.Add(("gg_top_tab", Messages.CategoryOf(s.TopCat)?.Name ?? ""));
        t.Add(("gg_top_text", s.TopText));
        t.Add(("gg_ban_head", s.BanHead));
        t.Add(("gg_ban_line", s.BanLine));
        t.Add(("gg_w_maps", s.Maps > 0 ? $"{s.Maps} Workshop maps" : "Workshop maps"));
        var countdown = CountdownOf(s);
        t.Add(("gg_k_eyebrow", countdown?.Eyebrow ?? ""));
        t.Add(("gg_k_map", countdown?.Title ?? ""));
        t.Add(("gg_k_by", countdown?.Line ?? ""));
        t.Add(("gg_k_secs", countdown == null ? "" : Countdown(countdown.Value.Seconds)));
        t.Add(("gg_t_map", map));
        t.Add(("gg_t_by", byline == "" ? Hud.Tally(s.Up, s.Down) : $"{byline}  ·  {Hud.Tally(s.Up, s.Down)}"));
        t.Add(("gg_t_hint", LikeHint));
        return t;
    }

    /// <summary>The vote's cards that have votes, the most first: the live column's rows while a vote is on.</summary>
    static List<VoteState.Option> Leading(VoteState? v) => v is not { Round: > 0 } ? [] : v.Options.Where(o => o.Votes > 0).OrderByDescending(o => o.Votes).Take(HudState.ListLength).ToList();

    /// <summary>The live classes, the same for everybody: which list rows are in use, an empty vote or top list, a change counting down.</summary>
    public static List<(string Id, string Class, bool On)> Classes(HudState s, VoteState? v = null)
    {
        var c = new List<(string, string, bool)>
        {
            (Global, "ready", true), (Global, "changing", CountdownOf(s) != null), (Global, "cd-logo", CountdownOf(s)?.Logo == true), (Global, "loading", s.Loading),
            (Global, "news", s.NewsFor > 0 && s.News != ""), (Global, "rst", s.RestartKind != ""), (Global, "top", s.TopText != ""), (Global, "ban", s.BanHead != ""), (Global, "crash", s.CrashNote != ""),
        };
        foreach (var cat in Messages.Categories) c.Add((TopPanel, cat.Class, cat.Id == s.TopCat));
        foreach (var tone in Tones) c.Add((BannerPanel, "tone-" + tone, tone == s.BanTone));
        var leading = Leading(v).Count;
        for (var i = 0; i < HudState.ListLength; i++)
        {
            c.Add(($"gg_w_row_n{i + 1}", "used", i < leading));
            c.Add(($"gg_w_row_t{i + 1}", "used", i < s.Top.Count));
        }
        c.Add(("gg_w_nempty", "used", leading == 0));
        c.Add(("gg_w_tempty", "used", s.Top.Count == 0));
        return c;
    }

    public const string TopPanel = "gg_top", BannerPanel = "gg_ban", LinePanel = "gg_wl";
    public static readonly string[] Tones = ["problem", "warning", "done", "info"];

    /// <summary>What the countdown banner shows: a map change, Restart match or a restart counting down. Logo: the project's logo in the picture's place.</summary>
    public readonly record struct CountdownWords(string Eyebrow, string Title, string Line, bool Logo, int Seconds);

    public static CountdownWords? CountdownOf(HudState s)
    {
        if (s.ChangeIn >= 0) return new("Map change", s.ChangeMap, Hud.ByWords(s.ChangeBy), false, s.ChangeIn);
        if (s.RematchIn >= 0) return new("Match", $"{Hud.Clean(s.RematchBy, 32)} is restarting the match", "The whole match starts over", true, s.RematchIn);
        if (s.RestartKind != "" && s.RestartWhen == "now" && s.RestartIn >= 0)
            return new("Server", s.RestartBy != "" ? $"{Hud.Clean(s.RestartBy, 32)} is restarting the server" : $"The server restarts in {MapVote.Left(s.RestartIn)}", s.RestartAfter, true, s.RestartIn);
        return null;
    }

    /// <summary>The seconds on the map-change banner, recomputed by the plugin every second from the deadline it took off the state.</summary>
    public static string Countdown(int seconds) => seconds <= 0 ? "Now" : seconds.ToString(CultureInfo.InvariantCulture);

    /// <summary>A restart's countdown beside its words in the window's foot, from its end time by the plugin's own clock (0:25), or what it waits
    /// for; "" without one.</summary>
    public static string RestartCountdown(HudState s, DateTime now)
    {
        if (s.RestartKind == "") return "";
        if (s.RestartAt <= 0) return "After this match";
        var left = Math.Max(0, (int)Math.Ceiling((DateTimeOffset.FromUnixTimeSeconds(s.RestartAt).UtcDateTime - now).TotalSeconds));
        return left <= 0 ? "Now" : $"{left / 60}:{left % 60:00}";
    }

    /// <summary>One row of the Players page (SERVER, Players): a player on the server, their team, the HUD their game showed at the HUD check's
    /// click (HudVersion.IsWord: a content, older, or HudVersion.Unknown for a HUD whose content is not known; "" not seen yet), the check's seconds
    /// left while it waits for that click, their vote while one is open, and how long they have been on.</summary>
    public sealed record PlayerRow(string Name, string Team, string Version, int CheckSeconds, string Vote, string OnFor);

    public const int PlayerRows = 16;
    public const string PlayersVar = "ospl", PlayersSummaryVar = "ospsum";

    /// <summary>The Players page's words, as variables on gg_global that the page's labels inherit: osplNa the name, osplNb the team, osplNc the HUD
    /// when it is the server's own, osplNo an older or unknown one, osplNk the HUD check's clock, osplNd the vote, osplNe how long on; ospsum the
    /// line over them. The rows the list does not reach are empty.</summary>
    public static List<(string Name, string Text)> PlayersPage(IReadOnlyList<PlayerRow> rows)
    {
        var w = new List<(string, string)>();
        static string Var(int k, char f) => $"{PlayersVar}{k.ToString(CultureInfo.InvariantCulture)}{f}";
        var seen = rows.Where(r => r.CheckSeconds <= 0).Select(r => r.Version).ToList();
        var current = seen.Count(v => v == HudVersion.Content);
        var older = seen.Count(HudVersion.IsOutdated);
        var newer = seen.Count(v => HudVersion.LevelOf(v) > HudVersion.Level);
        var unknown = seen.Count(v => v == HudVersion.Unknown);
        var unseen = rows.Count - current - older - newer - unknown;
        w.Add((PlayersSummaryVar, rows.Count == 0 ? "Nobody on the server" : string.Join("  ·  ", new[]
        {
            $"{rows.Count} on the server", current > 0 ? $"{current} on the server's HUD, {HudVersion.Content}" : "", older > 0 ? $"{older} older" : "",
            newer > 0 ? $"{newer} newer" : "", unknown > 0 ? $"{unknown} unknown" : "", unseen > 0 ? $"{unseen} not seen yet" : "",
            rows.Count > PlayerRows ? $"the first {PlayerRows} here" : "",
        }.Where(x => x != ""))));
        for (var k = 1; k <= PlayerRows; k++)
        {
            var r = k <= rows.Count ? rows[k - 1] : null;
            var checking = r is { CheckSeconds: > 0 };
            var mine = r != null && !checking && r.Version == HudVersion.Content;
            w.Add((Var(k, 'a'), r == null ? "" : Hud.Clean(r.Name, 32)));
            w.Add((Var(k, 'b'), r?.Team ?? ""));
            w.Add((Var(k, 'c'), mine ? r!.Version : ""));
            w.Add((Var(k, 'o'), r == null || mine || checking ? "" : HudVersion.Words(r.Version)));
            w.Add((Var(k, 'k'), checking ? $"HUD check: {r!.CheckSeconds} s left" : ""));
            w.Add((Var(k, 'd'), r?.Vote ?? ""));
            w.Add((Var(k, 'e'), r?.OnFor ?? ""));
        }
        return w;
    }

    /// <summary>How long a player has been on since their last real join, as the Players page says it: "42 min", "1 h 05", "-" when not known.</summary>
    public static string OnFor(TimeSpan? since) => since is not { } d ? "-" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min" : $"{(int)d.TotalHours} h {d.Minutes:00}";

    /// <summary>What the contextual button asks for while the vote is open: the plugin opens that player's window on it (nothing goes to the bridge).</summary>
    public const string OpenVote = "!vote";

    /// <summary>The button under the likes (the board's, and the window's live column's): Start a vote, Keep playing on it; the vote
    /// open - open it; the wait after a vote - when the next can start; a change under way - nothing. Command is what a click makes the player say,
    /// null for nothing, OpenVote for the window.</summary>
    public static (string Text, string Sub, string? Command) Action(HudState s, VoteState? v = null)
    {
        if (s.ChangeIn >= 0 || s.Loading) return ("Map change under way", "Wait for the next map", null);
        if (v is { Open: true, Round: > 0 }) return ("The vote is on: open it", "Pick the next map, or keep playing", OpenVote);
        if (v?.WaitFor > 0) return ("Start a vote", $"The next one can start in {MapVote.Left(v.WaitFor)}", "!next");
        return ("Start a vote", "For the next map - Keep playing is on it", "!next");
    }

    /// <summary>The note this player sees now, if any: the bridge's answer to what they clicked.</summary>
    public static string NoteOf(HudState s, long account) => s.Notes.TryGetValue(account, out var n) && n.For > 0 ? n.Text : "";

    /// <summary>Per-player texts: the greeting, the toast's first line, your own like, the button under it (in the window and on the board) and
    /// your note.</summary>
    public static List<(string Id, string Text)> PlayerTexts(HudState s, long account, string name, bool welcomeBack, VoteState? v = null)
    {
        var mine = s.MyVotes.GetValueOrDefault(account);
        var (text, sub, _) = Action(s, v);
        var note = NoteOf(s, account);
        return
        [
            ("gg_w_hello", welcomeBack ? $"Welcome back, {Hud.Clean(name, 32)}" : $"Welcome, {Hud.Clean(name, 32)}"),
            ("gg_t_eyebrow", "Now playing"),
            ("gg_wl_eyebrow", note == "" ? "" : Messages.CategoryOf(s.NoteCats.GetValueOrDefault(account, ""))?.Name ?? Messages.You.Name),
            ("gg_wl_text", note),
            ("gg_b_you", mine > 0 ? "You liked this map" : mine == HudCatalog.TooBigVote ? "You marked this map too big" : mine == HudCatalog.AdsVote ? "You marked this map: ads"
                : mine < DownReasons.Plain && DownReasons.Of(mine) is { } why ? $"You disliked this map: {why.Short}" : mine < 0 ? "You disliked this map" : "Like it or not?"),
            ("gg_btn_act_text", text),
            ("gg_btn_act_sub", sub),
            ("gg_b_next_text", text),
            ("gg_b_next_sub", sub),
            ("gg_note_text", NoteOf(s, account)),
        ];
    }

    public static List<(string Id, string Class, bool On)> PlayerClasses(HudState s, long account, VoteState? v = null)
    {
        var mine = s.MyVotes.GetValueOrDefault(account);
        var (_, _, command) = Action(s, v);
        if (v?.WaitFor > 0 && v is not { Round: > 0 }) command = null; // the wait after a vote: it looks off, and a click still gets its answer with the time left
        var asked = s.Why.TryGetValue(account, out var why) ? why.For : 0; // the popup (content v4.1.0): classes on gg_screen, for which an older game has no rule; the drain line a second in, so a second Dislike starts it again
        var cat = s.NoteCats.GetValueOrDefault(account, Messages.You.Id);
        var note = NoteOf(s, account) != "";
        List<(string Id, string Class, bool On)> c = [(Screen, "voted-up", mine > 0), (Screen, "voted-down", mine < 0), (Screen, "act-off", command == null),
            (Screen, "note", note), (Screen, "wl", note), (Screen, "why", asked > 0), (Screen, "why-run", asked is > 0 and < (int)DownReasons.PopupSeconds)];
        foreach (var k in Messages.Categories) c.Add((LinePanel, k.Class, note && k.Id == cat));
        return c;
    }

    /// <summary>The board's Lately rows for one player: the three most recent lines, with how long ago (Messages.LatelyFor).</summary>
    public static List<(string Id, string Text)> LatelyTexts(HudState s, long account, long nowMs)
    {
        var rows = Messages.LatelyFor(s.Lately, account);
        var t = new List<(string, string)>();
        for (var k = 1; k <= Messages.LatelyRows; k++)
        {
            var row = k <= rows.Count ? rows[k - 1] : null;
            t.Add(($"gg_bl_w{k}", row?.Text ?? ""));
            t.Add(($"gg_bl_t{k}", row == null ? "" : Messages.Ago(row.AtMs, nowMs)));
        }
        return t;
    }

    public static List<(string Id, string Class, bool On)> LatelyClasses(HudState s, long account)
    {
        var count = Messages.LatelyFor(s.Lately, account).Count;
        return Enumerable.Range(1, Messages.LatelyRows).Select(k => ($"gg_bl_r{k}", "used", k <= count)).ToList();
    }

    /// <summary>A click, as the plugin receives it: the action it asks the bridge for, or null for the buttons that only work the window.
    /// The board's buttons are the same as the live column's: a like on the map that is on, and Start a vote.</summary>
    public static string? Command(string buttonId, HudState? s, VoteState? v = null) => buttonId switch
    {
        ButtonUp or BoardUp => "!up",
        ButtonDown or BoardDown => "!down",
        ButtonAct or BoardNext => s == null ? null : Action(s, v).Command,
        _ => null,
    };
}
