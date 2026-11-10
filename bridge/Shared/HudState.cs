// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.
using System.Globalization;
using System.Text;

/// <summary>The live state the bridge serves on /hud/&lt;secret&gt; and the plugin puts on screen: the map that is on and what picked it, the
/// likes on it, the most liked maps and a change counting down. A text format of tab-separated lines rather than JSON, so the native-AOT bridge
/// needs no serializer context for it and both ends share one tested codec. The vote's own lines follow it (VoteState, MapVote.cs).</summary>
public sealed class HudState
{
    public const string Magic = "os2-hud\t1";
    public const int ListLength = 5;    // the board and the live column have five rows for the vote's leading cards and five for the most liked maps

    public int Maps;                                    // playable maps on the list, as the help counts them
    public string Map = "", By = "", Size = "", Key = "", Status = "";
    public int Up, Down;                                // likes and dislikes on the map that is on
    public bool Loading;                                // a change went out and its map has not logged its start yet
    public Dictionary<long, int> MyVotes = new();       // account id -> its like (+1) or dislike (a reason's value) on the map that is on
    public List<(string Map, int Up, int Down)> Top = new();
    public string ChangeMap = "", ChangeBy = "";        // a change counting down...
    public int ChangeIn = -1;                           // ...and the seconds until it goes out; -1 when none is
    public int RoundMinutes;                            // the match length !time set: Arms Race's or Deathmatch's minutes...
    public int Rounds;                                  // ...or Wingman's rounds (release 199)
    public string RoundBy = "";                         // ...and who set it, while it is not the default
    public long CatalogVersion;                         // the map catalog's version: the plugin fetches it again when this moves
    public string CheckMode = "";                       // the HUD check as configured (hud.check: off, warn or enforce)
    public Dictionary<long, string> Verified = new();   // account id -> the HUD its game showed at the check's click since its last real join (HudCheck.VersionWord), players on the server only
    public string TabMode = "";                         // the TAB chord (hud.tab: down or off)
    public Dictionary<long, (string Text, int For)> Notes = new(); // account id -> the note only that player sees, and its seconds left
    public Dictionary<long, (string Map, int For)> Why = new();    // account id -> the map a Dislike's popup asks about, and its seconds left (Likes.cs)
    public string PrevLabel = "", PrevArg = "";         // the map before this one, for the Previous button
    public long SearchVersion;                          // the search index's version: the plugin fetches it again when this moves
    public long DashVersion;                            // the Sync page's block (HudDash): the plugin fetches it again when this moves
    public string RestartKind = "";                     // a restart coming (the bridge's Restarts.cs): game-update (a CS2 update), our-update, daily, downloads; "" none
    public string RestartWhen = "";                     // ...match-end (at the end of this match) or now
    public int RestartIn = -1;                          // ...its seconds left, -1 while it waits for the match's end
    public long RestartAt;                              // ...the moment it goes, unix seconds (0 while it waits for the match's end): the end time a countdown counts to
    public string RestartWords = "";                    // ...and the warning in words, as the top line says it
    public string TopText = "", TopCat = "";            // the top line (Messages.cs): the words and the category's id...
    public long TopAtMs, TopUntilMs;                    // ...since when and until when, unix milliseconds
    public string BanTone = "", BanHead = "", BanLine = ""; // the status banner: its tone (problem, warning, done, info), headline and line...
    public long BanUntilMs;                             // ...and until when, 0 while its state lasts
    public Dictionary<long, string> NoteCats = new();   // account id -> the category of that player's note
    public List<Messages.LatelyLine> Lately = new();    // the board's Lately lines, oldest first
    public List<(long Seq, long Account, string Text)> Says = new(); // chat lines, each said once by its number (account 0: everybody); the text carries its marks (M)
    public List<(long Seq, long Account, bool Alert, long UntilMs, string Text)> Centres = new(); // CS2's plain centre text and alert, held until then
    public string CrashNote = "";                       // a crash within the last minutes, as the welcome page says it
    public string ChangeKey = "";                       // the key of the map a change counts down to: its picture
    public List<(string Key, string By, long AtMs, bool Previous)> PicksDue = new(); // a risky map's confirmed pick counting down to the vote: its key, who, when it goes, by Previous
    public string RestartBy = "";                       // the player who pressed a restart counting down
    public string RestartAfter = "";                    // what players do afterwards, as the countdown's second line says it
    public long RematchAtMs;                            // Restart match counting down: the moment it goes...
    public string RematchBy = "";                       // ...and who pressed it - or, a fairness restart's, who came in (EarlyJoin.Names: "Bob and Cas")
    public bool RematchFair;                            // ...the fairness restart (EarlyJoin): the countdown banner says so
    public int RematchCount = 1;                        // ...and how many came in, for the banner's "is" or "are"
    public int RematchIn = -1;                          // ...its seconds left, -1 when none is (worked out by AsOf)

    /// <summary>The end times behind the seconds above, unix milliseconds (0: none). The bridge's change-driven text (EndTimes: true, HudTransport.cs)
    /// carries these instead of the seconds, which would change it every second; the plugin counts down from them itself (AsOf).</summary>
    public long ChangeAtMs;
    public Dictionary<long, long> NoteUntilMs = new(), WhyUntilMs = new();

    /// <summary>Unix milliseconds of a moment, as the end times carry it.</summary>
    public static long Ms(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>The state as it stands at this moment (unix milliseconds on the bridge's clock): every countdown worked out from its end time, what has
    /// run out gone. A text without end times (an older bridge) is as it came.</summary>
    /// <summary>The moment (unix milliseconds on the bridge's clock) this state is as of (AsOf); 0: as it came. The picks' countdowns count to it (PickIn).</summary>
    public long AsOfMs;

    /// <summary>The seconds left of the confirmed pick of this map counting down to the vote (PicksDue), -1 for none: what its card's button counts; a state as it
    /// came (AsOfMs 0) says the whole countdown.</summary>
    public int PickIn(string key) => PicksDue.FirstOrDefault(p => p.Key == key) is { AtMs: > 0 } p ? LeftTo(p.AtMs) : -1;

    /// <summary>...and of the one that came by Previous, whose button counts it then; -1 for none.</summary>
    public int PreviousPickIn() => PicksDue.FirstOrDefault(p => p.Previous) is { AtMs: > 0 } p ? LeftTo(p.AtMs) : -1;

    /// <summary>The seconds left of a player's Restart the server counting down (its button counts them and cancels it), -1 for none - the watcher's restarts are not the players' to cancel.</summary>
    public int PlayerRestartIn() => RestartKind == PlayerRestartKind && RestartWhen == "now" && RestartIn >= 0 && RestartBy != "" ? RestartIn : -1;

    /// <summary>...and of a player's Restart match, -1 for none: the fairness restart is no button's.</summary>
    public int PlayerRematchIn() => RematchIn >= 0 && !RematchFair ? RematchIn : -1;

    /// <summary>The kind of a restart a player pressed, as the bridge's Restarts.cs names it on the wire.</summary>
    public const string PlayerRestartKind = "player";

    int LeftTo(long atMs) => AsOfMs > 0 ? Math.Max(0, (int)Math.Ceiling((atMs - AsOfMs) / 1000.0)) : HudMenu.PendingSeconds;

    public HudState AsOf(long nowMs)
    {
        var h = (HudState)MemberwiseClone();
        h.AsOfMs = nowMs;
        int Secs(long until) => (int)Math.Ceiling((until - nowMs) / 1000.0);
        if (ChangeAtMs > 0) h.ChangeIn = Math.Max(0, Secs(ChangeAtMs));
        h.Notes = new(Notes);
        h.NoteCats = new(NoteCats);
        foreach (var (account, until) in NoteUntilMs)
            if (Secs(until) is > 0 and var left && Notes.TryGetValue(account, out var note)) h.Notes[account] = (note.Text, left); else h.Notes.Remove(account);
        h.Why = new(Why);
        foreach (var (account, until) in WhyUntilMs)
            if (Secs(until) is > 0 and var left && Why.TryGetValue(account, out var why)) h.Why[account] = (why.Map, left); else h.Why.Remove(account);
        if (RestartAt > 0) h.RestartIn = Math.Max(0, Secs(RestartAt * 1000));
        if (TopUntilMs > 0 && nowMs >= TopUntilMs) (h.TopText, h.TopCat, h.TopAtMs, h.TopUntilMs) = ("", "", 0, 0);
        if (BanUntilMs > 0 && nowMs >= BanUntilMs) (h.BanTone, h.BanHead, h.BanLine, h.BanUntilMs) = ("", "", "", 0);
        h.RematchIn = RematchAtMs > 0 ? Math.Max(0, Secs(RematchAtMs)) : -1;
        h.RestartWords = h.FillLeft(h.RestartWords);
        return h;
    }

    /// <summary>A restart's words with its countdown filled in from RestartIn: in so long, or in a moment once it is due.</summary>
    public string FillLeft(string words) => !words.Contains(Hud.LeftToken, StringComparison.Ordinal) ? words
        : words.Replace(Hud.LeftToken, RestartIn > 0 ? MapVote.Left(RestartIn) : "a moment", StringComparison.Ordinal);

    public string Encode(bool endTimes = false)
    {
        var restartWords = endTimes ? RestartWords : FillLeft(RestartWords); // counted down by the HUD itself, or filled in now
        var s = new StringBuilder(Magic).Append('\n');
        void Line(params object[] fields) => s.Append(string.Join('\t', fields.Select(f => f is string t ? Hud.Clean(t) : Convert.ToString(f, CultureInfo.InvariantCulture)))).Append('\n');
        Line("maps", Maps);
        Line("now", Map, By, Size, Key, Up, Down, Status);
        if (Loading) Line("loading");
        foreach (var (id, v) in MyVotes) Line("vote", id, v);
        foreach (var (map, up, down) in Top.Take(ListLength)) Line("top", map, up, down);
        if (ChangeIn >= 0 && endTimes) Line("changeat", ChangeMap, ChangeBy, ChangeAtMs);
        else if (ChangeIn >= 0) Line("change", ChangeMap, ChangeBy, ChangeIn);
        if (RoundMinutes > 0) Line("set", RoundMinutes, RoundBy); // an older plugin reads the first field
        if (Rounds > 0) Line("rounds", Rounds, RoundBy);          // ...and has no line for this one: its Game page shows the row in Arms Race and Deathmatch alone
        if (CatalogVersion > 0) Line("cat", CatalogVersion);
        if (CheckMode != "") Line("chk", CheckMode);
        foreach (var (account, version) in Verified) Line("ok", account, version);
        if (TabMode != "") Line("tab", TabMode);
        foreach (var (account, (text, secs)) in Notes)
            if (text != "" && secs > 0)
                s.Append(endTimes ? "noteat\t" : "note\t").Append(account.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append((endTimes ? NoteUntilMs.GetValueOrDefault(account) : secs).ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Hud.Clean(text, 140)).Append('\n');
        foreach (var (account, (map, secs)) in Why)
            if (secs > 0) Line(endTimes ? "whyat" : "why", account, endTimes ? WhyUntilMs.GetValueOrDefault(account) : secs, map); // an older plugin skips it
        if (PrevArg != "") Line("prev", PrevArg, PrevLabel);
        if (SearchVersion > 0) Line("srch", SearchVersion);
        if (DashVersion > 0) Line("dash", DashVersion);    // an older plugin skips it
        if (RestartKind != "") s.Append("rst\t").Append(Hud.Clean(RestartKind, 12)).Append('\t').Append(Hud.Clean(RestartWhen, 12)).Append('\t')
            .Append((endTimes && RestartAt > 0 ? 0 : RestartIn).ToString(CultureInfo.InvariantCulture)).Append('\t').Append(RestartAt.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Hud.Clean(restartWords, 240)).Append('\n');
        void Raw(string name, params object[] fields) => s.Append(name).Append('\t').Append(string.Join('\t', fields.Select(f => f is string t ? t : Convert.ToString(f, CultureInfo.InvariantCulture)))).Append('\n');
        if (TopText != "") Raw("topl", TopAtMs, TopUntilMs, Hud.Clean(TopCat, 12), Hud.Clean(TopText, Messages.TopMax));
        if (BanHead != "") Raw("ban", Hud.Clean(BanTone, 12), BanUntilMs, Hud.Clean(BanHead, 80), Hud.Clean(BanLine, Messages.TopMax));
        foreach (var (account, cat) in NoteCats) if (Notes.ContainsKey(account)) Raw("notec", account, Hud.Clean(cat, 12));
        foreach (var l in Lately) Raw("late", l.AtMs, l.Account, Hud.Clean(l.Key, 40), Hud.Clean(l.Text, Messages.TopMax));
        foreach (var (seq, account, text) in Says) Raw("say", seq, account, Hud.Clean(text, Messages.ChatMax));
        foreach (var (seq, account, alert, until, text) in Centres) Raw("ctr", seq, account, alert ? 1 : 0, until, Hud.Clean(text, Messages.CentreMax));
        if (CrashNote != "") Raw("crashn", Hud.Clean(CrashNote, Messages.TopMax));
        if (ChangeKey != "") Raw("chgkey", Hud.Clean(ChangeKey, 40));
        foreach (var p in PicksDue) Raw("pickdue", Hud.Clean(p.Key, 40), p.AtMs, Hud.Clean(p.By, 32), p.Previous ? 1 : 0); // an older plugin skips it
        if (RestartKind != "") Raw("rstby", Hud.Clean(RestartBy, 32), Hud.Clean(RestartAfter, 80));
        if (RematchAtMs > 0) Raw("rematch", RematchAtMs, Hud.Clean(RematchBy, 64), RematchFair ? 1 : 0, Math.Max(1, RematchCount)); // an older plugin reads the first two or three
        return s.ToString();
    }

    /// <summary>The state out of Encode's text, or null when it is not one (an old bridge, an error page). Unknown lines are skipped,
    /// so a newer bridge can add some without breaking an older plugin.</summary>
    public static HudState? Decode(string? text)
    {
        if (text == null || !text.StartsWith(Magic)) return null;
        var s = new HudState();
        static int Int(string[] f, int i) => i < f.Length && int.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        static long Long(string[] f, int i) => i < f.Length && long.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
        static string Str(string[] f, int i) => i < f.Length ? f[i] : "";
        foreach (var raw in text.Split('\n').Skip(1))
        {
            var f = raw.TrimEnd('\r').Split('\t');
            switch (f[0])
            {
                case "maps": s.Maps = Int(f, 1); break;
                case "now": (s.Map, s.By, s.Size, s.Key, s.Up, s.Down, s.Status) = (Str(f, 1), Str(f, 2), Str(f, 3), Str(f, 4), Int(f, 5), Int(f, 6), Str(f, 7)); break;
                case "loading": s.Loading = true; break;
                case "vote": if (Long(f, 1) is >= 0 and var id) s.MyVotes[id] = HudCatalog.Carried(Int(f, 2)); break;
                case "top": if (s.Top.Count < ListLength) s.Top.Add((Str(f, 1), Int(f, 2), Int(f, 3))); break;
                case "change": (s.ChangeMap, s.ChangeBy, s.ChangeIn) = (Str(f, 1), Str(f, 2), Int(f, 3)); break;
                case "changeat" when Long(f, 3) > 0: (s.ChangeMap, s.ChangeBy, s.ChangeIn, s.ChangeAtMs) = (Str(f, 1), Str(f, 2), 0, Long(f, 3)); break;
                case "noteat" when Long(f, 1) >= 0 && Long(f, 2) > 0: s.Notes[Long(f, 1)] = (Str(f, 3), 1); s.NoteUntilMs[Long(f, 1)] = Long(f, 2); break;
                case "whyat" when Long(f, 1) >= 0 && Long(f, 2) > 0: s.Why[Long(f, 1)] = (Str(f, 3), 1); s.WhyUntilMs[Long(f, 1)] = Long(f, 2); break;
                case "set": (s.RoundMinutes, s.RoundBy) = (Int(f, 1), Str(f, 2)); break;
                case "rounds": (s.Rounds, s.RoundBy) = (Int(f, 1), Str(f, 2)); break;
                case "cat": s.CatalogVersion = Long(f, 1); break;
                case "chk": s.CheckMode = Str(f, 1); break;
                case "tab": s.TabMode = Str(f, 1); break;
                case "note" when Long(f, 1) >= 0 && Int(f, 2) > 0: s.Notes[Long(f, 1)] = (Str(f, 3), Int(f, 2)); break;
                case "why" when Long(f, 1) >= 0 && Int(f, 2) > 0: s.Why[Long(f, 1)] = (Str(f, 3), Int(f, 2)); break;
                case "prev": (s.PrevArg, s.PrevLabel) = (Str(f, 1), Str(f, 2)); break;
                case "srch": s.SearchVersion = Long(f, 1); break;
                case "dash": s.DashVersion = Long(f, 1); break;
                case "rst" when Str(f, 1) != "": (s.RestartKind, s.RestartWhen, s.RestartIn, s.RestartAt, s.RestartWords) = (Str(f, 1), Str(f, 2), f.Length > 3 && int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ri) ? ri : -1, Math.Max(0, Long(f, 4)), Str(f, 5)); break;
                case "ok" when Long(f, 1) >= 0 && HudVersion.IsWord(Str(f, 2)): s.Verified[Long(f, 1)] = Str(f, 2); break;
                case "topl" when Str(f, 4) != "": (s.TopAtMs, s.TopUntilMs, s.TopCat, s.TopText) = (Long(f, 1), Long(f, 2), Str(f, 3), Str(f, 4)); break;
                case "ban" when Str(f, 3) != "": (s.BanTone, s.BanUntilMs, s.BanHead, s.BanLine) = (Str(f, 1), Math.Max(0, Long(f, 2)), Str(f, 3), Str(f, 4)); break;
                case "notec" when Long(f, 1) >= 0: s.NoteCats[Long(f, 1)] = Str(f, 2); break;
                case "late" when Str(f, 4) != "": s.Lately.Add(new Messages.LatelyLine(Long(f, 1), Math.Max(0, Long(f, 2)), Str(f, 3), Str(f, 4))); break;
                case "say" when Long(f, 1) > 0: s.Says.Add((Long(f, 1), Math.Max(0, Long(f, 2)), Str(f, 3))); break;
                case "ctr" when Long(f, 1) > 0: s.Centres.Add((Long(f, 1), Math.Max(0, Long(f, 2)), Int(f, 3) == 1, Long(f, 4), Str(f, 5))); break;
                case "crashn": s.CrashNote = Str(f, 1); break;
                case "chgkey": s.ChangeKey = Str(f, 1); break;
                case "pickdue" when Str(f, 1) != "" && Long(f, 2) > 0 && s.PicksDue.Count < 64: s.PicksDue.Add((Str(f, 1), Str(f, 3), Long(f, 2), Int(f, 4) == 1)); break;
                case "rstby": (s.RestartBy, s.RestartAfter) = (Str(f, 1), Str(f, 2)); break;
                case "rematch" when Long(f, 1) > 0: (s.RematchAtMs, s.RematchBy, s.RematchFair, s.RematchCount) = (Long(f, 1), Str(f, 2), Int(f, 3) == 1, Math.Max(1, Int(f, 4))); break;
            }
        }
        return s;
    }
}
