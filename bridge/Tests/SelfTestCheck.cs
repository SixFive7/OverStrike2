// --selftest, part four: the HUD check (HudCheck.cs) - who is verified and with which HUD, when the warning and the disconnect come, that warn
// never disconnects, that a verification lives in memory per connection and the live state carries only the players on the server, and that only
// the plugin's click can verify.
static partial class Program
{
    static void CheckSelfTest(Action<string, bool> Check)
    {
        const string R = "v2.1.1", Item = "3815436903";
        var t0 = new DateTime(2026, 10, 7, 22, 0, 0, DateTimeKind.Utc);

        // ---- the modes: one setting, off, warn or enforce ----
        Check("HUD check: the mode reads off, warn and enforce, in the settings and on the wire alike, and anything else - an old word like dryrun too - as off",
            HudCheck.Parse("warn") == HudCheck.Mode.Warn && HudCheck.Parse(" Enforce ") == HudCheck.Mode.Enforce && HudCheck.Parse("dryrun") == HudCheck.Mode.Off
            && HudCheck.Parse("off") == HudCheck.Mode.Off && HudCheck.Parse("") == HudCheck.Mode.Off && HudCheck.Parse(null) == HudCheck.Mode.Off && HudCheck.Parse("kick") == HudCheck.Mode.Off
            && Enum.GetValues<HudCheck.Mode>().All(m => HudCheck.Parse(HudCheck.Name(m)) == m));
        var live = new HudState { CheckMode = "enforce" };
        Check($"HUD check: no state, or no answer for {HudCheck.StaleSeconds:0} s (an idle long poll answers every {Hud.LongPollSeconds} s), means off - a silent bridge never gets anybody disconnected; a fresh one says the mode, no proof needed",
            HudCheck.ModeOf(null, 0) == HudCheck.Mode.Off && HudCheck.ModeOf(live, HudCheck.StaleSeconds + 1) == HudCheck.Mode.Off && HudCheck.ModeOf(live, Hud.LongPollSeconds + 1) == HudCheck.Mode.Enforce
            && HudCheck.ModeOf(live, 1) == HudCheck.Mode.Enforce
            && HudCheck.ModeOf(new HudState { CheckMode = "warn" }, 1) == HudCheck.Mode.Warn && HudCheck.ModeOf(new HudState(), 1) == HudCheck.Mode.Off);

        // ---- the version button: its id names the content that has it ----
        Check("HUD check: the version button's id names its content, os_hudver_v4.7.0; any other button names none, and a malformed one or the old form os_hudver_v4_7_0 is no version button",
            HudVersion.Button(HudVersion.Content).StartsWith(HudVersion.ButtonPrefix, StringComparison.Ordinal) && HudVersion.OfButton(HudVersion.Button(HudVersion.Content)) == HudVersion.Content
            && HudVersion.OfButton("os_hudver_v4.7.0") == "v4.7.0" && HudVersion.OfButton("os_hudver_v4_7_0") == null && HudVersion.OfButton("os_hudver_v4_7.0") == null && HudVersion.OfButton("os_hudver_x") == null && HudVersion.OfButton(HudView.ButtonAct) == null && HudVersion.OfButton("os_tab_maps") == null);
        Check("HUD check: what !hudok carries - a content, older, or unknown (which content not known), each version in the one form - and nothing else, the old form v4_5_0 neither",
            new[] { HudVersion.Content, "v4.5.0", HudVersion.Older, HudVersion.Unknown }.All(v => Hud.IsAction(HudCheck.Command(v))) && !Hud.IsAction(HudCheck.Command("v4_5_0")) && !Hud.IsAction("!hudok") && !Hud.IsAction("!hudok x")
            && !Hud.IsAction("!hudok v2.1.1 x") && !Hud.IsAction("!hudok v2.1.1; quit") && !Hud.IsAction("!hudok v2_1_1 x") && !Hud.IsAction("!hudok v2_1_1; quit") && !Hud.IsAction("!hudok v2_1.1") && !Hud.IsAction("!hudok newer"));
        Check($"HUD check: a version as a level, an older one outdated, the server's own or a newer one not; the Players page's words for each",
            HudVersion.LevelOf("v4.5.0") == 45 && HudVersion.LevelOf("v4.6.0") == 46 && HudVersion.LevelOf("older") == 0 && HudVersion.IsOutdated("v4.5.0") && HudVersion.IsOutdated(HudVersion.Older)
            && !HudVersion.IsBefore(HudVersion.Content, HudVersion.Oldest) && HudVersion.IsBefore("v4.7.0", HudVersion.Oldest) && !HudVersion.IsOutdated(HudVersion.Content) && !HudVersion.IsOutdated("v9.0.0") && !HudVersion.IsOutdated(HudVersion.Unknown) && !HudVersion.IsOutdated("")
            && HudVersion.Words("v4.5.0") == "v4.5.0 - Restart CS2" && HudVersion.Words(HudVersion.Older) == "older - Restart CS2" && HudVersion.Words(HudVersion.Unknown) == "version unknown"
            && HudVersion.Words("") == "not seen yet" && HudVersion.Words("v9.0.0") == "v9.0.0");
        Check("HUD check: a level is a content's line in the table, never its digits: a version with no line - a patch, v4.10.0, v5.0.0, v14.5.27 - is newer than this code knows, and a word that is no version stays refused",
            new[] { "v4.6.1", "v4.10.0", "v5.0.0", "v14.5.27" }.Where(v => HudVersion.Contents.All(c => c.Version != v)).All(v => HudVersion.IsWord(v) && HudVersion.LevelOf(v) == HudVersion.Newer && !HudVersion.IsOutdated(v) && HudVersion.Words(v) == v)
            && new[] { "v14.5", "14.5.27", "v14.x.27", "v14.5.27.1", "newer" }.All(w => !HudVersion.IsWord(w) && HudVersion.LevelOf(w) == 0));
        var roll = HudView.PlayersPage([new("Alice", "CT", HudVersion.Content, 0, "voted", "42 min"), new("Bob", "T", "v4.5.0", 0, "waiting", "5 min"),
            new("Cleo", "T", HudVersion.Older, 0, "", "1 h 05"), new("Dan", "CT", "v9.0.0", 0, "", "-"), new("Eve", "SPEC", HudVersion.Unknown, 0, "", "3 min"),
            new("Finn", "T", "", 0, "", "0 min"), new("Gus", "CT", "", 12, "", "0 min")]);
        string Roll(string name) => roll.Single(w => w.Name == name).Text;
        Check("HUD check: the Players page counts each HUD once - the server's, older, newer, of unknown content, not seen yet (the check's clock among those) - and says each row's",
            Roll(HudView.PlayersSummaryVar) == $"7 on the server  ·  1 on the server's HUD, {HudVersion.Content}  ·  2 older  ·  1 newer  ·  1 unknown  ·  2 not seen yet"
            && Roll("ospl1a") == "Alice" && Roll("ospl1c") == HudVersion.Content && Roll("ospl1o") == "" && Roll("ospl1d") == "voted" && Roll("ospl1e") == "42 min"
            && Roll("ospl2c") == "" && Roll("ospl2o") == "v4.5.0 - Restart CS2" && Roll("ospl3o") == "older - Restart CS2" && Roll("ospl4o") == "v9.0.0"
            && Roll("ospl5o") == "version unknown" && Roll("ospl6o") == "not seen yet" && Roll("ospl7o") == "" && Roll("ospl7k") == "HUD check: 12 s left"
            && Roll("ospl8a") == "" && Roll("ospl8o") == "" && HudView.PlayersPage([])[0] == (HudView.PlayersSummaryVar, "Nobody on the server"));
        Check("HUD check: Steam's Error page means not shown to everybody, a Workshop page means shown, a 429 or anything else means no verdict",
            SteamPage.Shown(200, "<html><head><title>Steam Community :: Error</title></head><body>There was a problem accessing the item.</body></html>") == false
            && SteamPage.Shown(200, "<html><head><title>Steam Workshop::Over Strike 2 HUD</title></head></html>") == true
            && SteamPage.Shown(429, null) == null && SteamPage.Shown(200, "<html>maintenance</html>") == null && SteamPage.Shown(500, "<title>Steam Workshop::x</title>") == null);

        // ---- the Web API's answer (#19) ----
        const string Held = "{\"response\":{\"result\":1,\"resultcount\":1,\"publishedfiledetails\":[{\"publishedfileid\":\"3831242232\",\"result\":1,\"creator\":\"76561190000000001\",\"file_size\":\"11134577\",\"hcontent_file\":\"7434112514802144923\",\"title\":\"gg_vinceville\",\"time_created\":1793000000,\"time_updated\":1793000000,\"visibility\":0,\"banned\":0,\"ban_reason\":\"\"}]}}";
        const string Gone = "{\"response\":{\"result\":1,\"resultcount\":1,\"publishedfiledetails\":[{\"publishedfileid\":\"3659287341\",\"result\":9}]}}";
        var upload = DateTimeOffset.FromUnixTimeSeconds(1793000000).UtcDateTime; // a made-up upload time
        var held = HudCheck.ParseItem(Held, 3831242232);
        Check("HUD item, Web API: an item under review reads ok with its time_updated and content handle; a gone one reads removed; another id is not read",
            held is { Verdict: Workshop.Ok, TimeUpdated: 1793000000, Content: "7434112514802144923" } && HudCheck.ParseItem(Gone, 3659287341) is { Verdict: Workshop.Removed, TimeUpdated: 0 }
            && HudCheck.ParseItem(Held, 3823993566) == null && HudCheck.ParseItem("not json", 1) == null && HudCheck.ParseItem("{}", 1) == null);

        // ---- when the item's page is visited, and what joiners must download (ItemPlan: no regular scraping of
        //      the community site - the Web API is the signal, the page only right after it shows a change, then at most every 15 minutes
        //      for 3 hours while it is not shown, an hour's pause after a refusal, never idly) ----
        var old = new HudCheck.ItemApi(Workshop.Ok, 1792700000, "111"); // an upload of days ago
        var plan = new ItemPlan();
        var t = upload.AddDays(1);
        var noVisits = true;
        for (var m = 0; m < 24 * 60; m++) { plan.ApiAnswer(old, t.AddMinutes(m)); noVisits &= !plan.VisitDue(t.AddMinutes(m)); }
        Check("HUD item plan: a day of the Web API saying nothing changed visits the page not once, and the item counts as shown (required, public)",
            noVisits && plan.Visits == 0 && plan.Required(true, t.AddHours(24)) && plan.Public(t.AddHours(24)) && plan.Cause == HudCheck.Cause.Unknown);
        var v = new HudCheck.ItemApi(Workshop.Ok, 1792800000, "222"); // the new version: time_updated and the content handle moved
        var c0 = t.AddHours(24).AddMinutes(1);
        plan.ApiAnswer(v, c0);
        var dueAtChange = plan.VisitDue(c0);
        plan.VisitStarted();
        plan.PageAnswer(false, c0.AddSeconds(2)); // Steam's not-shown page: the new version under review
        Check("HUD item plan: a change in the Web API makes one visit due at once; the not-shown page drops the requirement at the first visit and the check's interlock",
            dueAtChange && !plan.Required(true, c0.AddSeconds(3)) && !plan.Public(c0.AddSeconds(3)) && plan.Cause == HudCheck.Cause.Review
            && !plan.VisitDue(c0.AddMinutes(14)) && plan.VisitDue(c0.AddMinutes(15).AddSeconds(2)));
        var visitTimes = new List<double>();
        for (var sec = 910; sec <= 4 * 3600; sec += 10)
        {
            var at = c0.AddSeconds(sec);
            if (sec % 60 == 0) plan.ApiAnswer(v, at);
            if (plan.VisitDue(at)) { plan.VisitStarted(); visitTimes.Add(sec / 60.0); plan.PageAnswer(false, at); }
        }
        Check($"HUD item plan: while it stays hidden the page is visited at most every 15 minutes ({visitTimes.Count} visits after the first), and not at all once the 3 hours are over",
            visitTimes.Count is > 8 and <= 12 && visitTimes.Zip(visitTimes.Skip(1), (x, y) => y - x).All(d => d >= 14.99) && visitTimes.All(m => m <= 180)
            && !plan.VisitDue(c0.AddHours(4)) && !plan.Required(true, c0.AddHours(4)) && !plan.Public(c0.AddHours(4)));
        var refused = new ItemPlan();
        refused.ApiAnswer(old, t);
        refused.ApiAnswer(v, c0);
        refused.VisitStarted();
        refused.PageAnswer(null, c0.AddSeconds(2)); // the site's 429: no verdict
        Check("HUD item plan: a visit the site refuses leaves the page alone for an hour",
            !refused.VisitDue(c0.AddMinutes(59)) && refused.VisitDue(c0.AddHours(1).AddSeconds(2)) && refused.SinceChange == null);
        var early = !refused.Required(true, c0.AddMinutes(5)) && refused.Cause == HudCheck.Cause.Review;
        refused.ApiAnswer(v, c0.AddMinutes(62));
        var late = !refused.Required(true, c0.AddMinutes(62)) && refused.Required(false, c0.AddMinutes(63));
        Check($"HUD item plan: with no verdict joiners need not download it, and hear it is under review, until {ItemPlan.ReviewAssumed.TotalMinutes} minutes after the upload; then it is required again, and nobody can be disconnected for it until the window is over",
            early && late && !refused.Public(c0.AddMinutes(64)) && refused.Public(c0.AddHours(3).AddMinutes(1)) == false);
        refused.ApiAnswer(v, c0.AddHours(1));
        refused.VisitStarted();
        refused.PageAnswer(true, c0.AddHours(1));
        var afterRefused = new ItemPlan();
        afterRefused.ApiAnswer(old, t);
        afterRefused.ApiAnswer(v, c0);
        var dropsOnlyOnRefusal = afterRefused.VisitDue(c0) && afterRefused.Required(true, c0);
        afterRefused.VisitStarted();
        Check("HUD item plan: a later visit that shows it ends that; while a first visit is under way nothing changes, so a restart does not flicker; a not-shown page still drops it for as long as it takes",
            refused.Required(false, c0.AddHours(1)) && dropsOnlyOnRefusal && afterRefused.Required(true, c0.AddSeconds(1))
            && Do(() => afterRefused.PageAnswer(false, c0.AddSeconds(2))) && Do(() => afterRefused.PageAnswer(null, c0.AddMinutes(20))) && Do(() => afterRefused.ApiAnswer(v, c0.AddMinutes(70)))
            && !afterRefused.Required(true, c0.AddMinutes(70)));
        var neverVisited = new ItemPlan();
        neverVisited.ApiAnswer(old, t);
        neverVisited.ApiAnswer(v, c0);
        Check("HUD item page off (hud.itemPage false, a test copy): with no visit at all joiners download the item as ever, and nobody can be disconnected for it until the window is over",
            neverVisited.Required(true, c0.AddMinutes(5)) && !neverVisited.Public(c0.AddMinutes(5)));
        var p2 = new ItemPlan();
        p2.ApiAnswer(v, c0);
        p2.VisitStarted();
        p2.PageAnswer(false, c0.AddSeconds(2));
        p2.ApiAnswer(v, c0.AddMinutes(15));
        p2.VisitStarted();
        p2.PageAnswer(true, c0.AddMinutes(15)); // Valve approved: the page shows it
        Check("HUD item plan: the first visit that finds it shown puts the requirement back, makes it public, and ends the visits",
            p2.Required(false, c0.AddMinutes(16)) && p2.Public(c0.AddMinutes(16)) && !p2.VisitDue(c0.AddMinutes(45)) && !p2.VisitDue(c0.AddHours(2)) && p2.Visits == 2);
        var p3 = new ItemPlan();
        p3.ApiAnswer(held, upload.AddMinutes(20)); // a plugin start 20 minutes after an upload: the page once, at once
        var p4 = new ItemPlan();
        p4.ApiAnswer(held, upload.AddHours(5));    // ...5 hours after it: no visit
        Check("HUD item plan: a start within 3 hours of an upload visits the page once at once; a later start does not, and counts the item shown",
            p3.VisitDue(upload.AddMinutes(20)) && !p4.VisitDue(upload.AddHours(5)) && p4.Public(upload.AddHours(5)) && p4.Required(true, upload.AddHours(5)));
        var p5 = new ItemPlan();
        p5.ApiAnswer(old, t);
        p5.ApiAnswer(new HudCheck.ItemApi(Workshop.Removed, 0, ""), t.AddMinutes(1));
        Check("HUD item plan: the Web API calling it gone, private or banned drops the requirement and the interlock with no page visit's verdict needed",
            !p5.Required(true, t.AddMinutes(1)) && !p5.Public(t.AddMinutes(1)) && p5.Cause == HudCheck.Cause.Unavailable);
        var p6 = new ItemPlan();
        p6.ApiAnswer(old, t);
        Check("HUD item plan: no fresh Web API answer (over 10 minutes) changes nothing either way and allows no disconnect; a visit by hand is one visit",
            p6.Required(true, t.AddMinutes(11)) && !p6.Required(false, t.AddMinutes(11)) && !p6.Public(t.AddMinutes(11))
            && !p6.VisitDue(t.AddMinutes(1)) && Do(() => p6.VisitNow(t.AddMinutes(2))) && p6.VisitDue(t.AddMinutes(2)) && Do(p6.VisitStarted) && Do(() => p6.PageAnswer(true, t.AddMinutes(2)))
            && !p6.VisitDue(t.AddMinutes(18)));
        static bool Do(Action a) { a(); return true; }
        // Steam's not-shown page: the old Error page and since 2026-10-31 an HTTP 500 titled "Steam Workshop".
        const string NewPage = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Steam Workshop</title><link rel=\"stylesheet\" href=\"x.css\"></head><body><div id=\"root\"></div></body></html>";
        Check("HUD check (#19): Steam's new HTTP 500 page titled \"Steam Workshop\" means not shown to everybody; a 500 without that title (an outage) is still no verdict",
            SteamPage.Shown(500, NewPage) == false && SteamPage.Shown(500, "<html><head><title>500 Internal Server Error</title></head></html>") == null
            && SteamPage.Shown(500, null) == null && SteamPage.Shown(502, NewPage) == null && SteamPage.Shown(200, NewPage) == null);

        // ---- the words while the HUD is not handed out ----
        var causes = new[] { HudCheck.Cause.Review, HudCheck.Cause.Unavailable, HudCheck.Cause.Unknown };
        var banners = causes.Select(HudCheck.Dropped).Append((HudCheck.BackHead, HudCheck.BackLine)).ToList();
        var chats = causes.SelectMany(c => new[] { HudCheck.NoMenuChat(c, "23:59"), HudCheck.NoMenuChat(c, null) }).Append(HudCheck.BackChat).ToList();
        Check($"HUD item (#19): the banner's lines fit its width (longest {banners.Max(l => l.Item2.Length)} of 125) and the chat lines {HudCheck.LineMax} (longest {chats.Max(l => l.Length)})",
            banners.All(l => l.Item2.Length <= 125) && chats.All(l => l.Length <= HudCheck.LineMax));
        Check("HUD item (#19): every line says what is happening, that the game plays on and that everybody loses the menu at the next map change; the way back says how to get the menu",
            causes.All(c => HudCheck.Dropped(c).Line.Contains("without the menu") && HudCheck.Dropped(c).Line.Contains("next map change") && HudCheck.Dropped(c).Line.Contains("plays on") && HudCheck.NoMenuChat(c, "23:59").Contains("Play on"))
            && HudCheck.Dropped(HudCheck.Cause.Review).Head.Contains("reviewing") && HudCheck.NoMenuChat(HudCheck.Cause.Review, "23:59").Contains("since 23:59")
            && HudCheck.BackLine.Contains("next map change") && HudCheck.BackChat.Contains("retry")
            && banners.Select(l => l.Item2).Concat(chats).All(l => !l.Contains("!gg") && !l.Contains("!info")));

        // ---- the words: the disconnect reason fits what a disconnect carries, every chat line one line ----
        var reason = HudCheck.Reason(Item);
        Check($"HUD check: the disconnect reason fits {HudCheck.ReasonMax} characters ({reason.Length}) and has the item's link and the four steps",
            reason.Length <= HudCheck.ReasonMax && reason.Contains(HudCheck.ItemUrl(Item)) && reason.Contains("1) subscribe") && reason.Contains("2) quit CS2")
            && reason.Contains("3) let Steam update") && reason.Contains("4) rejoin") && !reason.Contains('"'));
        var lines = HudCheck.Warning(Item, 15, false).Concat(HudCheck.Warning(Item, 15, true)).ToList();
        Check($"HUD check: every chat line fits {HudCheck.LineMax} characters (longest {lines.Max(l => l.Length)}), the warning names the seconds and the link",
            lines.All(l => l.Length <= HudCheck.LineMax) && HudCheck.Warning(Item, 15, false)[0].Contains("15 s") && HudCheck.Warning(Item, 15, false)[1].Contains(HudCheck.ItemUrl(Item))
            && HudCheck.Warning(Item, 15, true)[0].Contains("nobody is disconnected"));
        Check("HUD check: the lines say how to open the window now (TAB + E) and never name a chat command",
            HudCheck.Warning(Item, 15, false)[0].Contains(HudCheck.Reopen) && HudCheck.Warning(Item, 15, true)[0].Contains(HudCheck.Reopen)
            && lines.Append(reason).All(l => !l.Contains("!gg") && !l.Contains("/gg") && !l.Contains("!info")));
        Check("HUD check: a release or a content looks like v2.1.1 or v14.5.27, and the old form is not one",
            HudVersion.Is(R) && HudVersion.Is(HudVersion.Unknown) && HudVersion.Is(HudVersion.Content) && HudVersion.Is("v14.5.27") && !HudVersion.Is("v2.1") && !HudVersion.Is("2.1.1")
            && !HudVersion.Is("v2.1.1.1") && !HudVersion.Is("v2.x.1") && !HudVersion.Is("v2_1_1"));
        Check("HUD check: the old form is no version word - no content and no plugin has sent it since content v4.6.0 and plugin 5.0.0",
            !HudVersion.IsWord("v4_6_0") && !HudVersion.IsWord("v14_5_27") && HudVersion.LevelOf("v4_6_0") == 0 && HudVersion.IsWord("v4.6.0") && HudVersion.IsWord(HudVersion.Older));

        // ---- the clock (HudGate), on a handed clock ----
        List<(int Slot, HudGate.Act Act, int Seconds, double At)> Run(HudGate g, HudCheck.Mode mode, HashSet<long> verified, double from, double to, Action<double>? at = null)
        {
            var acts = new List<(int, HudGate.Act, int, double)>();
            for (var s = from; s <= to; s += 0.25)
            {
                at?.Invoke(s);
                acts.AddRange(g.Frame(t0.AddSeconds(s), mode, verified.Contains).Select(a => (a.Slot, a.Act, a.Seconds, s)));
            }
            return acts;
        }
        var gate = new HudGate();
        Check("HUD check: a player already verified gets no window and no clock",
            !gate.Spawned(1, 101, true, t0) && !gate.Checking(1) && Run(gate, HudCheck.Mode.Enforce, [], 0, 200).Count == 0);
        gate = new HudGate();
        var clicked = new HashSet<long>();
        var opened = gate.Spawned(2, 102, false, t0);
        var acts = Run(gate, HudCheck.Mode.Enforce, clicked, 0, 200, s => { if (s == 30) clicked.Add(102); });
        Check("HUD check: a click in time - the window at the first spawn, then nothing at all", opened && acts.Count == 0 && !gate.Checking(2));
        gate = new HudGate();
        gate.Spawned(3, 103, false, t0);
        var again = gate.Spawned(3, 103, false, t0.AddSeconds(50));
        acts = Run(gate, HudCheck.Mode.Enforce, [], 0, 300);
        Check($"HUD check: no click - one warning 15 s before, one disconnect at {HudCheck.GraceSeconds} s after the first spawn (a second spawn restarts nothing)",
            !again && acts.Count == 2 && acts[0] is (3, HudGate.Act.Warn, 15, 75) && acts[1] is (3, HudGate.Act.Kick, 0, 90));
        gate = new HudGate();
        gate.Spawned(4, 104, false, t0);
        acts = Run(gate, HudCheck.Mode.Warn, [], 0, 300);
        Check("HUD check: warn warns and logs, and never disconnects",
            acts.Count == 2 && acts[0] is (4, HudGate.Act.Warn, 15, 75) && acts[1] is (4, HudGate.Act.WouldKick, 0, 90) && acts.All(a => a.Act != HudGate.Act.Kick));
        gate = new HudGate();
        gate.Spawned(5, 105, false, t0);
        Run(gate, HudCheck.Mode.Warn, [], 0, 100);
        var rearmed = gate.Rearm(t0.AddSeconds(100));
        acts = Run(gate, HudCheck.Mode.Enforce, [], 100, 400);
        Check("HUD check: when enforcement comes on, everybody still waiting gets a full grace period from then - nobody is thrown out on the spot",
            rearmed.SequenceEqual([5]) && acts.Count == 2 && acts[0] is (5, HudGate.Act.Warn, 15, 175) && acts[1] is (5, HudGate.Act.Kick, 0, 190));
        gate = new HudGate();
        gate.Spawned(6, 106, false, t0);
        acts = Run(gate, HudCheck.Mode.Off, [], 0, 300);
        Check($"HUD check: switched off, nobody is warned or disconnected - the end of the grace ({HudCheck.GraceSeconds} s) is a Lapse, once, which only closes the window",
            acts.Count == 1 && acts[0] is (6, HudGate.Act.Lapse, 0, 90) && HudGate.ClosesWindow(HudGate.Act.Lapse) && HudGate.ClosesWindow(HudGate.Act.WouldKick)
            && !HudGate.ClosesWindow(HudGate.Act.Kick) && !HudGate.ClosesWindow(HudGate.Act.Warn));
        gate.MapStarted();
        Check("HUD check: a new map (everybody reconnects) starts the clock over", gate.Spawned(6, 106, false, t0.AddSeconds(400)) && gate.Checking(6));
        gate.Gone(6);
        Check("HUD check: a player who left is forgotten", !gate.Checking(6) && Run(gate, HudCheck.Mode.Enforce, [], 400, 600).Count == 0);
        var mixed = new HudState { Verified = { [201] = HudVersion.Content, [202] = HudVersion.Older } };
        var clockGate = new HudGate();
        clockGate.Spawned(7, 107, false, t0);
        Check("HUD check: verified is any HUD the bridge has for the player since their last real join; the Players page reads the check's clock",
            HudCheck.IsVerified(mixed, 201) && HudCheck.IsVerified(mixed, 202) && !HudCheck.IsVerified(mixed, 203) && !HudCheck.IsVerified(null, 201)
            && clockGate.SecondsLeft(7, t0.AddSeconds(30)) == (int)HudCheck.GraceSeconds - 30 && clockGate.SecondsLeft(8, t0) == 0);

        // ---- the codec: the mode and the verifications reach the plugin; no proof lines any more ----
        var sent = new HudState { CheckMode = "warn", Verified = { [201] = HudVersion.Content, [202] = HudVersion.Older, [203] = HudVersion.Unknown } };
        var got = HudState.Decode(sent.Encode() + "proof\tv2.1.1\tx\nok\tx\tv2.1.1\nok\t204\tnewer\nok\t205\tv4_5_0\n")!;
        Check("HUD check: the state carries the mode and the verifications with their HUDs, and drops malformed ones and the old form (an old bridge's proof lines are skipped)",
            got.CheckMode == "warn" && got.Verified.Count == 3 && got.Verified[201] == HudVersion.Content && got.Verified[202] == HudVersion.Older && got.Verified[203] == HudVersion.Unknown && !got.Verified.ContainsKey(205)
            && !sent.Encode().Contains("proof\t"));

        // ---- the bridge: only a click verifies, in memory, for the players on the server, forgotten at a real join or a real departure ----
        var (savedMode, savedDir, savedNow, savedSend, savedS) = (HudCheckMode, StateDir, Now, Send, S);
        string dir; // Save() writes state.json: into a scratch folder, as the queue tests do (the image's /app is not writable)
        try { dir = Path.Combine(AppContext.BaseDirectory, $"selftest-check-{Environment.ProcessId}"); Directory.CreateDirectory(dir); }
        catch (Exception) { dir = Path.Combine(Path.GetTempPath(), $"os2-bridge-check-{Environment.ProcessId}"); Directory.CreateDirectory(dir); }
        try
        {
            StateDir = dir;
            var clock = t0;
            Now = () => clock;
            Send = _ => null; // a real join asks the plugin for the guide over RCON: nobody to answer here
            S = new State();
            HudCheckMode = HudCheck.Mode.Warn;
            HudSeen.Clear(); ClearNotices();
            foreach (var (id, name) in new[] { ("[U:1:5001]", "Alice"), ("[U:1:5002]", "Bob"), ("[U:1:5003]", "Cara"), ("[U:1:5009]", "Mallory") })
                Ingest($"10/07/2026 - 12:00:00.000 - \"{name}<2><{id}><CT>\" entered the game", null);
            Command("Mallory", "[U:1:5009]", HudCheck.Command(HudVersion.Content));
            Check("HUD check: !hudok typed in chat verifies nobody", HudSeen.Count == 0);
            HudAct($"[U:1:5001]\tAlice\t{HudCheck.Command(HudVersion.Content)}");
            HudAct($"[U:1:5002]\tBob\t{HudCheck.Command(HudVersion.Older)}");
            HudAct($"[U:1:5003]\tCara\t{HudCheck.Command(HudVersion.Unknown)}");
            HudAct($"[U:1:5001]\tAlice\t{HudCheck.Command(HudVersion.Unknown)}"); // a later click on another button: the version stays known
            HudAct("[U:1:5005]\tEd\t!hudok v3_0_0"); // the old form, which no plugin since 5.0.0 sends: refused at the door
            HudAct("[U:1:5006]\tFay\t!hudok v4_5_0");
            var served = HudState.Decode(BuildHud(clock).Encode())!;
            var joinNews = string.Join("\n", Told);
            Check("HUD check: a click verifies its player with the HUD it proved (the version button's content, an older content's gold button, or unknown), and a known one is kept",
                served.CheckMode == "warn" && served.Verified.GetValueOrDefault(5001) == HudVersion.Content && served.Verified.GetValueOrDefault(5002) == HudVersion.Older
                && served.Verified.GetValueOrDefault(5003) == HudVersion.Unknown && !served.Verified.ContainsKey(5009) && HudCheck.ModeOf(served, 0) == HudCheck.Mode.Warn
                && !served.Verified.ContainsKey(5005) && !served.Verified.ContainsKey(5006) && !HudSeen.ContainsKey("[U:1:5006]"));
            Check("HUD check: the first click with a known HUD says for everybody which one the player brought, in CS2's plain centre text, chat and on the board; an outdated one gets no note, its own banner says it",
                joinNews.Contains("joined with") && served.Centres.Any(c => M.Plain(c.Text).Contains("joined with")) && served.Says.Any(l => M.Plain(l.Text).Contains("joined with")) && served.Lately.Any(l => l.Text.Contains("joined with"))
                && HudView.NoteOf(served, 5002) == "" && HudView.NoteOf(served, 5001) == "");
            Ingest("10/07/2026 - 12:00:05.000 - \"Bob<2><[U:1:5002]><CT>\" disconnected (reason \"NETWORK_DISCONNECT_DISCONNECT_BY_USER\")", null);
            Ingest("10/07/2026 - 12:00:06.000 - \"Alice<2><[U:1:5001]><CT>\" disconnected (reason \"NETWORK_DISCONNECT_SHUTDOWN\")", null);
            var afterLeave = HudState.Decode(BuildHud(clock).Encode())!;
            Check("HUD check: a player who left is forgotten at once - the live state carries only the players on the server - and a map change's reconnect forgets nobody",
                !afterLeave.Verified.ContainsKey(5002) && afterLeave.Verified.ContainsKey(5001) && !HudSeen.ContainsKey("[U:1:5002]"));
            Ingest("10/07/2026 - 12:00:30.000 - \"Bob<2><[U:1:5002]><CT>\" connected, address \"\"", null);
            Ingest("10/07/2026 - 12:00:31.000 - \"Bob<2><[U:1:5002]><CT>\" entered the game", null);
            Check("HUD check: a real join asks again - the returning player may have a new HUD - and nothing of it is written to state.json",
                !HudState.Decode(BuildHud(clock).Encode())!.Verified.ContainsKey(5002) && !File.ReadAllText(Path.Combine(dir, "state.json")).Contains("hud", StringComparison.OrdinalIgnoreCase));
            Check("HUD check: refused click lines change nothing", !HudAct("[U:1:5004]\tDan\t!hudok v9") && !HudSeen.ContainsKey("[U:1:5004]"));
        }
        finally
        {
            (HudCheckMode, StateDir, Now, Send, S) = (savedMode, savedDir, savedNow, savedSend, savedS);
            HudSeen.Clear(); ClearNotices();
            foreach (var id in new[] { "[U:1:5001]", "[U:1:5002]", "[U:1:5003]", "[U:1:5009]" }) Players.Remove(id);
            try { Directory.Delete(dir, true); } catch (Exception e) { Console.WriteLine($"selftest: could not remove {dir}: {e.Message}"); }
        }
    }

    /// <summary>--selftest, part five: TAB (HudTab.cs: down, or off) on a handed clock, and the check's pause handling.</summary>
    static void TabSelfTest(Action<string, bool> Check)
    {
        var t0 = new DateTime(2026, 10, 11, 20, 0, 0, DateTimeKind.Utc);
        const ulong TAB = HudTab.Scoreboard, E = HudTab.Use, RMB = 1UL << 11, W = 1UL << 3, JUMP = 1UL << 1;
        Check("TAB: the bits are CounterStrikeSharp's (IN_SCORE 1<<33, IN_USE 1<<5)", HudTab.Scoreboard == 0x200000000UL && HudTab.Use == 0x20UL);
        Check("TAB: the mode flag reads down and off; the names of the modes TAB had before read as down, anything else as off - and every name reads back",
            HudTab.Parse("down") == HudTab.Mode.Down && HudTab.Parse(" Down ") == HudTab.Mode.Down && HudTab.Parse("off") == HudTab.Mode.Off
            && new[] { "hold", "window", "on", "chord", "board", "menu" }.All(old => HudTab.Parse(old) == HudTab.Mode.Off)
            && HudTab.Parse(null) == HudTab.Mode.Off && HudTab.Parse("") == HudTab.Mode.Off && HudTab.Parse("yes") == HudTab.Mode.Off
            && Enum.GetValues<HudTab.Mode>().All(m => HudTab.Parse(HudTab.Name(m)) == m));
        var lines = Enum.GetValues<HudTab.Mode>().Select(HudTab.BoardLine).ToList();
        Check($"TAB: the board's command line fits it in both modes (longest {lines.Max(l => l.Length)}, the old one 52)", lines.All(l => l.Length <= 56)
            && HudTab.BoardLine(HudTab.Mode.Off) == HudView.StaticTexts().First(t => t.Id == "os_b_cmds").Text);

        var tab = new HudTab();
        Check("TAB off: nothing, whatever is pressed",
            tab.Buttons(1, TAB, 0, HudTab.Mode.Off, t0) == null && tab.Buttons(1, E | RMB, 0, HudTab.Mode.Off, t0) == null && !tab.Cursor(1));
        tab = new HudTab();
        Check("TAB: E, right-click, moving or jumping without TAB do nothing",
            tab.Buttons(2, E, 0, HudTab.Mode.Down, t0) == null && tab.Buttons(2, 0, E, HudTab.Mode.Down, t0) == null && tab.Buttons(2, RMB, 0, HudTab.Mode.Down, t0) == null
            && tab.Buttons(2, 0, RMB, HudTab.Mode.Down, t0) == null && tab.Buttons(2, W | JUMP, 0, HudTab.Mode.Down, t0) == null && !tab.Cursor(2));
        tab = new HudTab();
        var downOn = tab.Buttons(20, TAB, 0, HudTab.Mode.Down, t0);
        var downHeld = tab.Cursor(20);
        var downOff = tab.Buttons(20, 0, TAB, HudTab.Mode.Down, t0.AddSeconds(5));
        Check("TAB down: the cursor from the moment TAB goes down, off when it comes up",
            downOn == HudTab.Act.CursorOn && downHeld && downOff == HudTab.Act.CursorOff && !tab.Cursor(20));
        tab = new HudTab();
        tab.Buttons(21, TAB, 0, HudTab.Mode.Down, t0);
        Check("TAB down: TAB held for minutes, moving and clicking - the cursor stays (a time limit ended it with TAB still held, and that click "
            + "fired the gun); TAB coming up ends it, the next TAB brings it back",
            tab.Buttons(21, W, 0, HudTab.Mode.Down, t0.AddSeconds(10)) == null && tab.Buttons(21, 1UL, 0, HudTab.Mode.Down, t0.AddSeconds(10)) == null && tab.Cursor(21)
            && tab.Buttons(21, 0, W | 1UL, HudTab.Mode.Down, t0.AddMinutes(5)) == null && tab.Cursor(21) && tab.CursorSlots().SequenceEqual([21])
            && tab.Buttons(21, 0, TAB, HudTab.Mode.Down, t0.AddMinutes(5)) == HudTab.Act.CursorOff && !tab.Cursor(21) && !tab.CursorSlots().Any()
            && tab.Buttons(21, TAB, 0, HudTab.Mode.Down, t0.AddMinutes(6)) == HudTab.Act.CursorOn);
        tab = new HudTab();
        tab.Buttons(6, TAB, 0, HudTab.Mode.Down, t0);
        var toMenu = tab.Buttons(6, E, 0, HudTab.Mode.Down, t0.AddSeconds(1));
        var noCursor = !tab.Cursor(6);
        var upAfter = tab.Buttons(6, 0, TAB, HudTab.Mode.Down, t0.AddSeconds(2));
        Check("TAB + E: the menu window, which takes the cursor over and stays when TAB comes up (TAB up then asks nothing)",
            toMenu == HudTab.Act.OpenMenu && noCursor && upAfter == null && !tab.Cursor(6));
        tab = new HudTab();
        tab.Buttons(5, TAB, 0, HudTab.Mode.Down, t0);
        Check("TAB: a right-click with TAB held changes nothing - the cursor is on already", tab.Buttons(5, RMB, 0, HudTab.Mode.Down, t0.AddSeconds(1)) == null && tab.Cursor(5));
        tab = new HudTab();
        foreach (var slot in new[] { 7, 8, 9 }) tab.Buttons(slot, TAB, 0, HudTab.Mode.Down, t0);
        var hit = tab.Release(7) && !tab.Release(7) && !tab.Cursor(7);
        tab.Gone(8);
        var all = tab.ReleaseAll();
        Check("TAB: a hit or death takes the cursor back (once), leaving forgets the slot, os_hud release (and switching TAB off) takes every cursor back",
            hit && !tab.Cursor(8) && all.SequenceEqual([9]) && !tab.CursorSlots().Any());
        tab = new HudTab();
        tab.Buttons(10, TAB, 0, HudTab.Mode.Down, t0);
        tab.MapStarted();
        Check("TAB: a map change forgets everything (everybody reconnects)", !tab.Cursor(10) && tab.Buttons(10, E, 0, HudTab.Mode.Down, t0.AddSeconds(1)) == null);
        tab = new HudTab();
        tab.Buttons(11, TAB, 0, HudTab.Mode.Down, t0);
        Check("TAB: a cursor that stuck (its TAB-up never seen) goes with the next tap of TAB",
            tab.Buttons(11, TAB, 0, HudTab.Mode.Down, t0.AddSeconds(30)) == null && tab.Buttons(11, 0, TAB, HudTab.Mode.Down, t0.AddSeconds(30.2)) == HudTab.Act.CursorOff && !tab.Cursor(11));
        var held = new HudSlots();
        held.Show(40, HudSlots.Page.Start, t0);
        for (var sec = 0.25; sec <= 120; sec += 0.25) { held.Hold(40, t0.AddSeconds(sec)); held.Frame(t0.AddSeconds(sec)); }
        var heldView = held.Frame(t0.AddSeconds(120)).First(v => v.Slot == 40).View;
        var afterHold = held.Frame(t0.AddSeconds(120 + HudSlots.IdleCloseSeconds)).First(v => v.Slot == 40).View;
        var notHeld = new HudSlots();
        notHeld.Show(41, HudSlots.Page.Start, t0);
        Check("A held window (the vote holds everybody's): no idle close while it is held (two minutes here) and no drain bar; once the hold ends "
            + "the idle close is back - and a window nothing holds still closes after its 30 s",
            heldView is { Capture: true, Draining: false } && !afterHold.Capture && !notHeld.Frame(t0.AddSeconds(HudSlots.IdleCloseSeconds)).First(v => v.Slot == 41).View.Capture);

        Check("TAB: down is the default - HUD_TAB unset - and reads back", HudTab.Default == HudTab.Mode.Down
            && HudTab.Parse(HudTab.Name(HudTab.Default)) == HudTab.Mode.Down && (Environment.GetEnvironmentVariable("HUD_TAB") is { Length: > 0 } || HudTabMode == HudTab.Mode.Down));
        var sent = new HudState { TabMode = "down" };
        Check("TAB: the state carries the mode to the plugin, and an older bridge's state means off",
            HudState.Decode(sent.Encode())!.TabMode == "down" && HudTab.Parse(HudState.Decode(new HudState().Encode())!.TabMode) == HudTab.Mode.Off);
        var savedTab = HudTabMode;
        try
        {
            HudTabMode = HudTab.Mode.Off;
            Check("TAB: the bridge serves HUD_TAB", HudState.Decode(BuildHud(Now()).Encode())!.TabMode == "off");
        }
        finally { HudTabMode = savedTab; }

        // ---- the HUD check's pause: no answer from the bridge for a while must not swallow anybody's warning ----
        var gate = new HudGate();
        gate.Spawned(11, 111, false, t0);
        var acts = new List<(int Slot, HudGate.Act Act, int Seconds, double At)>();
        for (var sec = 0.0; sec <= 200; sec += 0.25)
        {
            var mode = sec is >= 60 and < 120 ? HudCheck.Mode.Off : HudCheck.Mode.Enforce; // no word from the bridge from 60 to 120 s
            acts.AddRange(gate.Frame(t0.AddSeconds(sec), mode, _ => false).Select(a => (a.Slot, a.Act, a.Seconds, sec)));
        }
        Check("HUD check: after a pause past the deadline the player still gets the warning, and 15 s to click, before the disconnect - the end of the "
            + "grace during the pause only closed their window, once (a Lapse)",
            acts.Count == 3 && acts[0] is (11, HudGate.Act.Lapse, 0, 90) && acts[1] is (11, HudGate.Act.Warn, 15, 120) && acts[2] is (11, HudGate.Act.Kick, 0, 135));
    }
}
