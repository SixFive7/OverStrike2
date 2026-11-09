// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Globalization;

/// <summary>The menu's pages as texts and classes for one player, out of the catalog, the live state and that player's browser, and a
/// click on them as the chat command it stands for. Card k (1-10) is panel os_m{k}; its parts are os_m{k}_*. The browser shows the first eight,
/// the ballot ten.</summary>
public static class HudMenu
{
    public const int Cards = MapVote.PageCards > HudBrowser.PerPage ? MapVote.PageCards : HudBrowser.PerPage;

    /// <summary>Previous: the map before this one, to the vote - on the Maps page.</summary>
    public const string Time10 = "os_set_time_10", Time60 = "os_set_time_60", Restart = "os_set_restart", Rematch = "os_set_rematch", Previous = "os_q_prev",
        BoardOpen = HudView.BoardOpen;

    /// <summary>The Fun page's Random and Default (!fun random, !fun default).</summary>
    public const string FunRandom = "os_fun_random", FunDefault = "os_fun_default";

    /// <summary>Card k's panel id (k from 1); its parts are this plus "_name", "_thumb", "_play" and so on.</summary>
    public static string Card(int k) => $"os_m{k}";

    /// <summary>What a card calls its place in the list: Arms Race's maps a number, the other modes' a number in their own list.</summary>
    static string RankText(HudCatalog.Entry m, string filter)
    {
        if (m.Official) return "Official";
        var mode = filter != "" && m.RankIn(filter) >= 0 ? filter : m.FirstMode;
        return mode switch
        {
            Modes.DeathmatchId => $"DM #{m.RankIn(mode)}",
            Modes.WingmanId => $"Wingman #{m.RankIn(mode)}",
            _ => $"#{m.RankIn(mode)}",
        };
    }

    /// <summary>A risky map's Play is a lock: its card under a shut padlock with this on the button (risky), and after the first click, for this player and for
    /// HudBrowser.ConfirmSeconds, the padlock open with the second word (confirm): that click puts it to the vote. When the time is up the class goes and it shuts again.</summary>
    public const string Locked = "Locked - Click to unlock", Unlocked = "Click again - Vote anyway";

    /// <summary>What a card's Play button says (a pick is a vote), and whether this player's click on it is the second, confirming
    /// one. While the vote is open (MAPS, Browse) a Play puts the map on the vote, with this player's vote; after a vote, the wait before the next one.</summary>
    static string PlayText(HudCatalog.Entry m, HudState s, HudBrowser b, DateTime now, VoteState? vote = null, long account = 0)
    {
        if (vote is { Open: true, Round: > 0 } v)
        {
            if (v.Options.FirstOrDefault(o => o.Key == m.Key && o.Source != VoteSource.Random) is { } on)
                return v.ChoiceOf(account) == on.N ? "Your vote" : "On the vote";
            if (m.Removed) return "Unavailable";
            if (b.ConfirmKey == m.Key && now < b.ConfirmUntil) return Unlocked;
            return m.Risky ? Locked : "Add to the vote";
        }
        if (m.Key == s.Key && s.Key != "") return "On now";
        if (m.Removed) return "Unavailable";
        if (vote?.WaitFor > 0) return $"Vote in {MapVote.Left(vote.WaitFor)}";
        if (b.ConfirmKey == m.Key && now < b.ConfirmUntil) return Unlocked;
        return m.Risky ? Locked : "Put to the vote";
    }

    /// <summary>The search area's words while no search is on - in content v4.8.0's search box too, whose keyboard never opens since release 196: chat is the one
    /// text input there is, and a chat line typed on MAPS > Browse searches it (233).</summary>
    public const string SearchHint = "Press Y and type a map name";

    static string Likes(HudCatalog.Entry m) => m.Up + m.Down == 0 ? "no likes" : $"+{m.Up}{(m.Down > 0 ? $"  -{m.Down}" : "")}";

    /// <summary>The red strip across a card's picture - the map browser's and the ballot's: why the map is unavailable or broken
    /// (the reason, where the list gives one), or what it crashed. The one text of a map's record on screen.</summary>
    public static string Record(HudCatalog.Entry m) => m.Removed ? "Removed from the Workshop" : m.Broken > 0 ? $"Broken: crashed {m.Broken}/{m.Broken} test loads"
        : m.Broken == 0 ? (m.Reason != "" ? $"Broken: {m.Reason}" : "On the broken list") : m.Crashes > 0 ? $"Crashed the server {m.Crashes}x" : "";

    /// <summary>The maps page for one player: the toolbar's state, the search area (the query from chat, or the hint), the count, the page counter and the
    /// eight cards with every tag that applies (MapTags; ON NOW on the map that is on). A query that is a Workshop id no map has hides the cards'
    /// empty message: the plugin shows that item's card there. The cards past the eighth are the ballot's: empty here.</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) Maps(
        HudCatalog? catalog, HudState s, HudBrowser b, long account, DateTime now, HudSearch? search = null, string serverMode = "", VoteState? vote = null)
    {
        var t = new List<(string, string)>();
        var c = new List<(string, string, bool)>();
        var matches = catalog == null ? new List<HudCatalog.Entry>() : b.Matches(catalog, account, search);
        var page = b.PageOf(matches);
        var total = catalog?.Maps.Count ?? 0;
        var workshop = b.WorkshopQuery(catalog) != null;
        t.Add(("os_m_count", catalog == null ? "Loading the map list..." : matches.Count == total ? $"{total} maps" : $"{matches.Count} of {total} maps"));
        t.Add(("os_pg_text", $"Page {b.Page + 1} of {b.PagesFor(matches.Count)}"));
        t.Add(("os_q_text", b.Query));
        t.Add(("os_q_hint", b.Query == "" ? SearchHint : ""));
        t.Add(("os_m_empty", catalog == null || workshop ? "" : b.Query != "" ? $"No map matches \"{b.Query}\" - Other words, or a Workshop ID."
            : b.Liked ? "You have not liked a map yet: the up arrow on a card likes it." : b.Ours ? "No map of ours on the list yet." : "No map matches these filters."));
        c.Add(("os_m_empty", "used", catalog != null && matches.Count == 0 && !workshop));
        c.Add(("os_q_search", "used", b.Query != ""));
        c.Add((HudView.Screen, "vdone", false)); // the vote's, still on from the last one: they would dim these cards (HudVote.Cards)
        c.Add((HudView.Screen, "vlow", false));
        foreach (var (id, sort) in HudBrowser.SortButtons) c.Add((id, "on", b.Sort == sort));
        foreach (var (id, sort) in HudBrowser.SortButtons) c.Add((id, "desc", b.Sort == sort && b.Descending));
        foreach (var (id, band) in HudBrowser.BandButtons) c.Add((id, "on", b.Size == band));
        c.Add((HudBrowser.LikedButton, "on", b.Liked));
        c.Add((HudBrowser.OfficialButton, "on", b.Official));
        c.Add((HudBrowser.OursButton, "on", b.Ours));
        c.Add((HudView.Screen, "fl-ours", b.Ours));   // Our maps glows while it is off
        c.Add((HudBrowser.PrevButton, "off", b.Page == 0));
        c.Add((HudBrowser.FirstButton, "off", b.Page == 0));
        c.Add((HudBrowser.NextButton, "off", b.Page >= b.PagesFor(matches.Count) - 1));
        c.Add((HudBrowser.LastButton, "off", b.Page >= b.PagesFor(matches.Count) - 1));
        for (var k = 1; k <= Cards; k++)
        {
            var id = Card(k);
            var m = k <= page.Count ? page[k - 1] : null;
            var mine = m == null || catalog == null ? 0 : catalog.MyVote(account, m.Key);
            t.Add(($"{id}_rank", m == null ? "" : RankText(m, b.Mode)));
            t.Add(($"{id}_name", m?.Name ?? ""));
            t.Add(($"{id}_size", m == null || m.Official ? "" : m.SizeMb >= 1000 ? $"{m.SizeMb / 1000.0:0.0} GB" : $"{m.SizeMb} MB"));
            t.Add(($"{id}_votes", m == null ? "" : Likes(m)));
            t.Add(($"{id}_who", ""));   // the ballot's line under the name
            t.Add(($"{id}_score", "")); // the ballot's count
            t.Add(($"{id}_warn", m == null ? "" : Record(m)));
            t.Add(($"{id}_play_text", m == null ? "" : PlayText(m, s, b, now, vote, account)));
            c.Add((id, "used", m != null));
            c.Add((id, "official", m?.Official == true));
            c.Add((id, "risky", m?.Risky == true));
            c.Add((id, "broken", m != null && (m.Broken >= 0 || m.Removed))); // the red strip: a removed map is the stronger warning
            c.Add((id, "crashed", m != null && m.Crashes > 0));
            c.Add((id, "now", m != null && m.Key == s.Key && s.Key != ""));
            c.Add((id, "queued", m != null && vote is { Open: true, Round: > 0 } && vote.Options.Any(o => o.Key == m.Key && o.Source is VoteSource.Picked or VoteSource.Rolled))); // on the open ballot
            c.Add((id, "liked", mine > 0));
            c.Add((id, "disliked", mine < 0));
            c.Add((id, "confirm", m != null && b.ConfirmKey == m.Key && now < b.ConfirmUntil));
            c.Add((id, "big", m?.TooBig == true));                         // the TOO BIG tag
            c.Add((id, "ads", m?.Ads > 0));                                 // the ADS tag
            c.Add((id, "ours", m?.Ours == true));                           // the OUR MAP tag
            c.AddRange(HudVote.CardModeClasses(id, m == null ? null : PlayMode(m, b.Mode, serverMode))); // the mode it plays in, on its picture and in its colour
            c.AddRange(HudVote.TagClassesOf(id, m?.Tags));                  // the rest of its tags
            c.AddRange(HudVote.ShareClasses(id, 0, 0));                     // no vote's share here
            c.Add((id, "vhas", false));
            c.Add((id, "vlead", false));
            c.Add((id, "vtie", false));
        }
        foreach (var (_, mode) in HudBrowser.ModeButtons) c.Add((HudView.Screen, HudBrowser.ModeClass(mode), b.Mode == mode)); // the mode filter's chips, lit
        return (t, c);
    }

    /// <summary>The picture each card should show for this player: (thumbnail panel, its classes), none where the card is empty or its
    /// map has no picture. The plugin switches off whatever the card's previous picture had and this one does not (it knows what it sent).</summary>
    public static List<(string Panel, string[] Classes)> Thumbs(HudCatalog? catalog, HudBrowser b, long account, HudSearch? search = null)
    {
        var page = catalog == null ? new List<HudCatalog.Entry>() : b.PageOf(b.Matches(catalog, account, search));
        return Enumerable.Range(1, Cards).Select(k => ($"{Card(k)}_thumb", k <= page.Count ? HudThumbs.Classes(page[k - 1]) : [])).ToList();
    }

    /// <summary>The Previous button (it puts the map before this one to the vote) - on the Maps page.</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) PreviousButton(HudState s) =>
        ([("os_q_prev_text", s.PrevArg == "" ? "No previous map yet" : $"Previous: {Hud.Clean(s.PrevLabel, 26)}")], [(Previous, "off", s.PrevArg == "")]);

    // ---- the Fun page: a row per setting of Fun.cs, a button per choice; the state is the server's, the same for all ----

    public const string FunNote = "os_fun_note";

    /// <summary>The line at the top of the Fun page (every row alike, the grenade drops too).</summary>
    public const string FunNoteText = "Anybody switches these, for everybody. Everything lasts until the map changes (or 10 minutes with nobody on); the top of the screen says who.";

    public static string FunRow(int r) => $"os_f{r}";

    public static string FunButton(int r, int c) => $"os_f{r}_{c}";

    /// <summary>A setting off its default, with who set it: "Moon - Set by Alice", the words of its row; "" while it is at its default or nobody is
    /// known to have set it. On the page alone: the change's own message said it in chat and on the board.</summary>
    public static string SetByWords(Fun.Setting s, Fun.Choice showing, string? by) => showing == s.Normal || string.IsNullOrWhiteSpace(by) ? "" : $"{showing.Label} - Set by {Hud.Clean(by, 32)}";

    /// <summary>The Fun page as the live values have it: each row's title and words (with a word when the map runs its own value, and who set a
    /// setting that is off its default), each choice's label, the choice showing lit, a choice that cannot be made greyed (off: FunWhy says why); rows
    /// and choices beyond the settings collapse. A row that does nothing at the moment (Instakill and Grenades while friendly fire is off: Fun.IdleBecause) says
    /// so instead of its words and is dimmed (idle); its buttons still work. setBy: who set a setting, by its id (the settings module's word); null: nobody known.</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) FunPage(Func<string, string?> value, bool armsRace = true,
        Func<string, string?>? setBy = null)
    {
        var t = new List<(string, string)>();
        var c = new List<(string, string, bool)>();
        for (var r = 1; r <= Fun.Rows; r++)
        {
            var s = r <= Fun.Settings.Length ? Fun.Settings[r - 1] : null; // the grenade drops and catch-up are the last two, like any other
            var showing = s == null ? null : Fun.Showing(s, value);
            var because = s == null ? null : Fun.IdleBecause(s, value);
            var idle = because != null;
            t.Add(($"{FunRow(r)}_title", s?.Title ?? ""));
            var who = s == null || showing == null ? "" : SetByWords(s, showing, setBy?.Invoke(s.Id));
            t.Add(($"{FunRow(r)}_txt", s == null ? "" : because != null ? because.Idle
                : showing == s.Normal && Fun.OwnValues(s, value).Count > 0 ? $"{s.Words}  ·  This map runs its own" : who != "" ? $"{who}  ·  {s.Words}" : s.Words));
            c.Add((FunRow(r), "used", s != null && (armsRace || !Fun.ArmsRaceOnly(s)))); // friendly fire's three are Arms Race's: Deathmatch is free-for-all, Wingman runs Valve's
            c.Add((FunRow(r), "idle", idle));
            c.Add((FunRow(r), "changed", s != null && showing != s.Normal)); // a setting off its default stands out
            for (var k = 1; k <= Fun.ChoicesPerRow; k++)
            {
                var choice = s != null && k <= s.Choices.Length ? s.Choices[k - 1] : null;
                t.Add(($"{FunButton(r, k)}_txt", choice?.Label ?? ""));
                c.Add((FunButton(r, k), "used", choice != null));
                c.Add((FunButton(r, k), "on", choice != null && choice == showing));
                c.Add((FunButton(r, k), "off", s != null && choice != null && Fun.Impossible(s, choice, value)));
            }
        }
        return (t, c);
    }

    /// <summary>The variable a row's bubbles read: a greyed choice's reason, or what a refused click there says.</summary>
    public static string FunWhyVar(int r) => $"osfw{r}";

    /// <summary>The row a reason's variable belongs to (osfw2 -> os_f2): it takes class clash while that reason shows, as its own second line.</summary>
    public static string FunRowOf(string whyVar) => $"os_f{whyVar[4..]}";

    /// <summary>What each row's bubbles say as the live values have it: why its greyed choice cannot be made (Fun.Refusal), "" on a row with none. One a row is enough: there is one clash
    /// (Fun.Clashes), and it greys one choice of a row.</summary>
    public static List<(string Name, string Text)> FunWhy(Func<string, string?> value) =>
        Fun.Settings.Take(Fun.Rows).Select((s, i) => (FunWhyVar(i + 1), s.Choices.Where(c => Fun.Impossible(s, c, value)).Select(c => Fun.Refusal(s, c, value)).FirstOrDefault() ?? "")).ToList();

    /// <summary>A click on the Fun page as the setting and the choice it asks for, or null when it is no Fun button or names nothing.</summary>
    public static (Fun.Setting Setting, Fun.Choice Choice)? FunClick(string buttonId)
    {
        var parts = buttonId.StartsWith("os_f", StringComparison.Ordinal) ? buttonId[4..].Split('_') : [];
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var r)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var k)) return null; // os_fl_* are the size chips, os_fun_note a label
        if (r < 1 || r > Math.Min(Fun.Rows, Fun.Settings.Length)) return null;
        var s = Fun.Settings[r - 1];
        return k >= 1 && k <= s.Choices.Length ? (s, s.Choices[k - 1]) : null;
    }

    /// <summary>What a restart button says after its first click: press it again, and the seconds left of
    /// the bridge's 30 s window, counting down every second; the stylesheet also makes it blink fast while armed (.os-restart.armed).</summary>
    public static string RestartAgain(int seconds) => $"Press again within {seconds} s";

    /// <summary>Whether the state says the bridge will refuse a click on this restart button: its first click then has nothing to arm, and the player's note says why. The whole
    /// server: one counts down. The match: a restart of either kind, a map change or a load, a vote (the bridge's RematchRefusal has the rest, which the plugin cannot see).</summary>
    public static bool RestartRefused(string buttonId, HudState? s, VoteState? v) =>
        s != null && (s.RestartWhen == "now" || (buttonId == Rematch && (s.RematchIn >= 0 || s.ChangeIn >= 0 || s.Loading || v is { Round: > 0 })));

    /// <summary>The game page: the match length that is set (and who set it, while it is not the default), and the two restart buttons' two-click
    /// state - restartSeconds and rematchSeconds are what is left of each one's confirmation window after its first click (0: not armed).</summary>
    public static (List<(string Id, string Text)> Texts, List<(string Id, string Class, bool On)> Classes) Game(HudState s, int restartSeconds, int rematchSeconds = 0)
    {
        var (armed, rearmed) = (restartSeconds > 0, rematchSeconds > 0);
        var t = new List<(string, string)>
        {
            ("os_set_time_now", Length(s) is not { Length: > 0 } length ? "Match length" : $"Matches last {length}{(s.RoundBy != "" ? $" - Set by {Hud.Clean(s.RoundBy, 32)}" : "")}"),
            ("os_set_restart_text", armed ? RestartAgain(restartSeconds) : "Restart the server"),
            ("os_set_restart_sub", armed ? "Restarts the whole server: everybody is kicked for about a minute"
                : "For a stuck server: the whole server, not the match. Asks to confirm."),
            ("os_set_rematch_text", rearmed ? RestartAgain(rematchSeconds) : "Restart the match"),
            ("os_set_rematch_sub", rearmed ? "Starts the match over for everybody, 10 s after your click" : "Starts this match over, for everybody. Asks to confirm."),
        };
        var c = new List<(string, string, bool)>
        {
            (Time10, "on", s.Rounds > 0 ? s.Rounds == MatchLength.ShortRounds : s.RoundMinutes == MatchLength.ShortMinutes),
            (Time60, "on", s.Rounds > 0 ? s.Rounds == MatchLength.LongRounds : s.RoundMinutes == MatchLength.LongMinutes), (Restart, "armed", armed), (Rematch, "armed", rearmed),
        };
        return (t, c);
    }

    /// <summary>The match length the state says, in its mode's words: "60 minutes", "16 rounds" (Wingman's, release 199); "" when it says none.</summary>
    static string Length(HudState s) => s.Rounds > 0 ? $"{s.Rounds} rounds" : s.RoundMinutes > 0 ? $"{s.RoundMinutes} minutes" : "";

    /// <summary>What a card's Play names: the map, and the mode it plays in when that is not the first mode that has it - the one the filter
    /// shows it in, else the one the server is in when that mode has it too.</summary>
    public static string PlayArg(HudCatalog.Entry m, string filter, string serverMode)
    {
        var mode = PlayMode(m, filter, serverMode);
        return mode == m.FirstMode ? m.Arg : $"{m.Arg} {mode}";
    }

    /// <summary>The mode a card's Play plays its map in: the filter's, else the server's when that mode has it too, else the first mode that has it.
    /// Its label and colour on the card say this mode.</summary>
    public static string PlayMode(HudCatalog.Entry m, string filter, string serverMode) =>
        filter != "" && m.RankIn(filter) >= 0 ? filter : serverMode != "" && m.RankIn(serverMode) >= 0 ? serverMode : m.FirstMode;

    /// <summary>A click on a menu button as the action it asks the bridge for (null: nothing to send). Card buttons name the map shown on
    /// that card for this player; a risky map's Play arms a confirmation first, exactly like the bridge's own two-step pick.</summary>
    public static string? Command(string buttonId, HudCatalog? catalog, HudState? s, HudBrowser b, long account, DateTime now, HudSearch? search = null, string serverMode = "", VoteState? vote = null)
    {
        if (buttonId.StartsWith("os_m") && buttonId.Length > 5 && char.IsDigit(buttonId[4]) && catalog != null && s != null)
        {
            var under = buttonId.IndexOf('_', 4);
            if (under < 0 || !int.TryParse(buttonId[4..under], NumberStyles.None, CultureInfo.InvariantCulture, out var k) || k < 1 || k > Cards) return null;
            var page = b.PageOf(b.Matches(catalog, account, search));
            if (k > page.Count) return null;
            var m = page[k - 1];
            switch (buttonId[(under + 1)..])
            {
                case "up": return $"!up {m.Arg}";
                case "down": return $"!down {m.Arg}";
                case "play":
                    if (m.Key == s.Key && s.Key != "" && vote is not { Open: true, Round: > 0 }) return null; // the map that is on: Keep on the vote, nothing outside one
                    if (m.Removed) return $"!map {PlayArg(m, b.Mode, serverMode)}"; // the bridge refuses it, saying why in the player's note - no confirmation to arm
                    if (m.Risky && !(b.ConfirmKey == m.Key && now < b.ConfirmUntil)) { b.ConfirmKey = m.Key; b.ConfirmUntil = now.AddSeconds(HudBrowser.ConfirmSeconds); }
                    else { b.ConfirmKey = ""; b.ConfirmUntil = default; }
                    return $"!map {PlayArg(m, b.Mode, serverMode)}"; // the first click on a risky map arms the bridge's confirmation too, and says why in the note
            }
            return null;
        }
        return buttonId switch
        {
            Previous => s?.PrevArg is { Length: > 0 } ? "!previous" : null,
            Time10 => $"!time {MatchLength.Choices(serverMode).Short}", // the short one, in the mode's own count: Wingman's 8 rounds (release 199)
            Time60 => $"!time {MatchLength.Choices(serverMode).Long}",
            Restart => "!restart",
            Rematch => "!rematch",
            _ => null,
        };
    }
}
