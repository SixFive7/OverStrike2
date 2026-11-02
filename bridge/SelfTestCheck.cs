// --selftest, part four: the HUD check (HudCheck.cs) - who is verified, when the warning and the disconnect come, that a dry run never
// disconnects and turns into enforcement only on a release a real click has proven, and that only the plugin's click can verify.
static partial class Program
{
    static void CheckSelfTest(Action<string, bool> Check)
    {
        const string R = "v2_1_1", Old = "v2_1_0", Item = "3815436903";
        var t0 = new DateTime(2026, 10, 7, 22, 0, 0, DateTimeKind.Utc);
        var proven = new Dictionary<string, string> { [R] = "Remy [U:1:1] 2026-10-07 12:00" };

        // ---- the modes, and the safety interlock ----
        Check("HUD check: the mode flag reads off, dryrun and enforce, and anything else as off",
            HudCheck.Parse("dryrun") == HudCheck.Mode.DryRun && HudCheck.Parse(" Enforce ") == HudCheck.Mode.Enforce && HudCheck.Parse("off") == HudCheck.Mode.Off
            && HudCheck.Parse("") == HudCheck.Mode.Off && HudCheck.Parse(null) == HudCheck.Mode.Off && HudCheck.Parse("kick") == HudCheck.Mode.Off);
        Check("HUD check: a dry run becomes enforcement only on a release a real click has proven - a new release is back in dry run",
            HudCheck.Effective(HudCheck.Mode.DryRun, proven, R) == HudCheck.Mode.Enforce && HudCheck.Effective(HudCheck.Mode.DryRun, proven, "v2_2_0") == HudCheck.Mode.DryRun
            && HudCheck.Effective(HudCheck.Mode.DryRun, new Dictionary<string, string>(), R) == HudCheck.Mode.DryRun
            && HudCheck.Effective(HudCheck.Mode.Off, proven, R) == HudCheck.Mode.Off && HudCheck.Effective(HudCheck.Mode.Enforce, new Dictionary<string, string>(), R) == HudCheck.Mode.Enforce);
        var live = new HudState { CheckMode = "dryrun", Proofs = new(proven) };
        Check("HUD check: no state, or one older than 10 s, means off - a silent bridge never gets anybody disconnected",
            HudCheck.ModeOf(null, 0, R) == HudCheck.Mode.Off && HudCheck.ModeOf(live, 11, R) == HudCheck.Mode.Off && HudCheck.ModeOf(live, 1, R) == HudCheck.Mode.Enforce
            && HudCheck.ModeOf(new HudState(), 1, R) == HudCheck.Mode.Off);
        Check("HUD check: Steam's Error page means not shown to everybody, a Workshop page means shown, a 429 or anything else means no verdict",
            HudCheck.PagePublic(200, "<html><head><title>Steam Community :: Error</title></head><body>There was a problem accessing the item.</body></html>") == false
            && HudCheck.PagePublic(200, "<html><head><title>Steam Workshop::Over Strike 2 HUD</title></head></html>") == true
            && HudCheck.PagePublic(429, null) == null && HudCheck.PagePublic(200, "<html>maintenance</html>") == null && HudCheck.PagePublic(500, "<title>Steam Workshop::x</title>") == null);

        // ---- the Web API's answer (#19) ----
        const string Held = "{\"response\":{\"result\":1,\"resultcount\":1,\"publishedfiledetails\":[{\"publishedfileid\":\"3831242232\",\"result\":1,\"creator\":\"76561190000000001\",\"file_size\":\"11134577\",\"hcontent_file\":\"7434112514802144923\",\"title\":\"gg_vinceville\",\"time_created\":1793000000,\"time_updated\":1793000000,\"visibility\":0,\"banned\":0,\"ban_reason\":\"\"}]}}";
        const string Gone = "{\"response\":{\"result\":1,\"resultcount\":1,\"publishedfiledetails\":[{\"publishedfileid\":\"3659287341\",\"result\":9}]}}";
        var upload = DateTimeOffset.FromUnixTimeSeconds(1793000000).UtcDateTime; // a made-up upload time
        var held = HudCheck.ParseItem(Held, 3831242232);
        Check("HUD item, Web API: an item under review reads ok with its time_updated and content handle; a gone one reads removed; another id is not read",
            held is { Verdict: Workshop.Ok, TimeUpdated: 1793000000, Content: "7434112514802144923" } && HudCheck.ParseItem(Gone, 3659287341) is { Verdict: Workshop.Removed, TimeUpdated: 0 }
            && HudCheck.ParseItem(Held, 3823993566) == null && HudCheck.ParseItem("not json", 1) == null && HudCheck.ParseItem("{}", 1) == null);

        // ---- when the item's page is visited, and what joiners must download (ItemPlan; the owner, 2026-11-01: no regular scraping of
        //      the community site - the Web API is the signal, the page only right after it shows a change, then at most every 15 minutes
        //      for 3 hours while it is not shown, an hour's pause after a refusal, never idly: the owner, 2026-11-02) ----
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
        Check("HUD item plan: a visit the site refuses leaves the page alone for an hour, and changes nothing about what joiners must download",
            !refused.VisitDue(c0.AddMinutes(59)) && refused.VisitDue(c0.AddHours(1).AddSeconds(2)) && refused.SinceChange == null && refused.Required(true, c0.AddMinutes(5)));
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
        // Steam's not-shown page (#19): the old Error page and since 2026-10-31 an HTTP 500 titled "Steam Workshop".
        const string NewPage = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Steam Workshop</title><link rel=\"stylesheet\" href=\"x.css\"></head><body><div id=\"root\"></div></body></html>";
        Check("HUD check (#19): Steam's new HTTP 500 page titled \"Steam Workshop\" means not shown to everybody; a 500 without that title (an outage) is still no verdict",
            HudCheck.PagePublic(500, NewPage) == false && HudCheck.PagePublic(500, "<html><head><title>500 Internal Server Error</title></head></html>") == null
            && HudCheck.PagePublic(500, null) == null && HudCheck.PagePublic(502, NewPage) == null && HudCheck.PagePublic(200, NewPage) == null);

        // ---- the words while the HUD is not handed out ----
        var causes = new[] { HudCheck.Cause.Review, HudCheck.Cause.Unavailable, HudCheck.Cause.Unknown };
        var news = causes.Select(c => HudCheck.DroppedNews(c, "23:59")).Append(HudCheck.DroppedNews(HudCheck.Cause.Review, null)).Append(HudCheck.BackNews).ToList();
        var chats = causes.SelectMany(c => new[] { HudCheck.DroppedChat(c), HudCheck.NoMenuChat(c, "23:59"), HudCheck.NoMenuChat(c, null) }).Append(HudCheck.BackChat).ToList();
        Check($"HUD item (#19): the news lines fit the news line's 240 characters (longest {news.Max(l => l.Length)}) and the chat lines {HudCheck.LineMax} (longest {chats.Max(l => l.Length)})",
            news.All(l => l.Length <= 240 && l == Hud.Clean(l, 240)) && chats.All(l => l.Length <= HudCheck.LineMax));
        Check("HUD item (#19): every line says what is happening and that the game plays on; the review lines name the upload's time; the way back says how to get the menu",
            causes.All(c => HudCheck.DroppedNews(c, "23:59").Contains("without the menu") && HudCheck.DroppedChat(c).Contains("plays on") && HudCheck.NoMenuChat(c, "23:59").Contains("Play on"))
            && HudCheck.DroppedNews(HudCheck.Cause.Review, "23:59").Contains("reviewing") && HudCheck.DroppedNews(HudCheck.Cause.Review, "23:59").Contains("23:59")
            && HudCheck.NoMenuChat(HudCheck.Cause.Review, "23:59").Contains("since 23:59") && !HudCheck.DroppedNews(HudCheck.Cause.Review, null).Contains("uploaded")
            && HudCheck.BackNews.Contains("next map change") && HudCheck.BackChat.Contains("retry")
            && news.Concat(chats).All(l => !l.Contains("!gg") && !l.Contains("!info")));

        // ---- the words: the disconnect reason fits what a disconnect carries, every chat line one line ----
        var reason = HudCheck.Reason(Item);
        Check($"HUD check: the disconnect reason fits {HudCheck.ReasonMax} characters ({reason.Length}) and has the item's link and the four steps",
            reason.Length <= HudCheck.ReasonMax && reason.Contains(HudCheck.ItemUrl(Item)) && reason.Contains("1) subscribe") && reason.Contains("2) quit CS2")
            && reason.Contains("3) let Steam update") && reason.Contains("4) rejoin") && !reason.Contains('"'));
        var lines = HudCheck.Warning(Item, 15, false).Concat(HudCheck.Warning(Item, 15, true)).Append(HudCheck.Prompt).Append(HudCheck.Thanks).ToList();
        Check($"HUD check: every chat line fits {HudCheck.LineMax} characters (longest {lines.Max(l => l.Length)}), the warning names the seconds and the link",
            lines.All(l => l.Length <= HudCheck.LineMax) && HudCheck.Warning(Item, 15, false)[0].Contains("15 s") && HudCheck.Warning(Item, 15, false)[1].Contains(HudCheck.ItemUrl(Item))
            && HudCheck.Warning(Item, 15, true)[0].Contains("nobody is disconnected"));
        Check("HUD check: the lines say how to open the window now (TAB + E) and never name a chat command - typed commands are gone since v3_0_0",
            HudCheck.Prompt.Contains(HudCheck.Reopen) && HudCheck.Warning(Item, 15, false)[0].Contains(HudCheck.Reopen) && HudCheck.Warning(Item, 15, true)[0].Contains(HudCheck.Reopen)
            && lines.Append(reason).All(l => !l.Contains("!gg") && !l.Contains("/gg") && !l.Contains("!info")));
        Check("HUD check: a release looks like v2_1_1, and the click's !hudok takes exactly one",
            HudCheck.IsRelease(R) && HudCheck.IsRelease(Hud.Release) && !HudCheck.IsRelease("v2_1") && !HudCheck.IsRelease("2_1_1") && !HudCheck.IsRelease("v2_1_1_1") && !HudCheck.IsRelease("v2_x_1")
            && Hud.IsAction(HudCheck.Command(R)) && !Hud.IsAction("!hudok") && !Hud.IsAction("!hudok x") && !Hud.IsAction("!hudok v2_1_1 x") && !Hud.IsAction("!hudok v2_1_1; quit"));

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
        acts = Run(gate, HudCheck.Mode.DryRun, [], 0, 300);
        Check("HUD check: a dry run warns and logs, and never disconnects",
            acts.Count == 2 && acts[0] is (4, HudGate.Act.Warn, 15, 75) && acts[1] is (4, HudGate.Act.WouldKick, 0, 90) && acts.All(a => a.Act != HudGate.Act.Kick));
        gate = new HudGate();
        gate.Spawned(5, 105, false, t0);
        Run(gate, HudCheck.Mode.DryRun, [], 0, 100);
        var rearmed = gate.Rearm(t0.AddSeconds(100));
        acts = Run(gate, HudCheck.Mode.Enforce, [], 100, 400);
        Check("HUD check: when enforcement comes on, everybody still waiting gets a full grace period from then - nobody is thrown out on the spot",
            rearmed.SequenceEqual([5]) && acts.Count == 2 && acts[0] is (5, HudGate.Act.Warn, 15, 175) && acts[1] is (5, HudGate.Act.Kick, 0, 190));
        gate = new HudGate();
        gate.Spawned(6, 106, false, t0);
        Check("HUD check: switched off, nothing happens at all", Run(gate, HudCheck.Mode.Off, [], 0, 300).Count == 0);
        gate.MapStarted();
        Check("HUD check: a new map (everybody reconnects) starts the clock over", gate.Spawned(6, 106, false, t0.AddSeconds(400)) && gate.Checking(6));
        gate.Gone(6);
        Check("HUD check: a player who left is forgotten", !gate.Checking(6) && Run(gate, HudCheck.Mode.Enforce, [], 400, 600).Count == 0);
        var mixed = new HudState { Verified = { [201] = R, [202] = Old } };
        Check("HUD check: verified once per release - a click on the previous release does not count for this one",
            HudCheck.IsVerified(mixed, 201, R) && !HudCheck.IsVerified(mixed, 202, R) && HudCheck.IsVerified(mixed, 202, Old) && !HudCheck.IsVerified(mixed, 203, R) && !HudCheck.IsVerified(null, 201, R));

        // ---- the codec: mode, proofs and verifications reach the plugin ----
        var sent = new HudState { CheckMode = "dryrun", Proofs = new(proven), Verified = { [201] = R, [202] = Old } };
        var got = HudState.Decode(sent.Encode() + "proof\tnot-a-release\tx\nok\tx\tv2_1_1\n")!;
        Check("HUD check: the state carries the mode, the proofs and the verifications, and drops malformed ones",
            got.CheckMode == "dryrun" && got.Proofs.Count == 1 && got.Proofs[R] == proven[R] && got.Verified.Count == 2 && got.Verified[201] == R && got.Verified[202] == Old);

        // ---- the bridge: only a click verifies, the first real one proves the release, and it is kept ----
        var (savedMode, savedVerified, savedProofs, savedDir) = (HudCheckMode, S.HudVerified, S.HudProofs, StateDir);
        string dir; // Save() writes state.json: into a scratch folder, as the queue tests do (the image's /app is not writable)
        try { dir = Path.Combine(AppContext.BaseDirectory, $"selftest-check-{Environment.ProcessId}"); Directory.CreateDirectory(dir); }
        catch (Exception) { dir = Path.Combine(Path.GetTempPath(), $"gungamebridge-check-{Environment.ProcessId}"); Directory.CreateDirectory(dir); }
        try
        {
            StateDir = dir;
            HudCheckMode = HudCheck.Mode.DryRun;
            S.HudVerified = new(); S.HudProofs = new();
            Command("Mallory", "[U:1:5009]", HudCheck.Command(R));
            Check("HUD check: !hudok typed in chat verifies nobody and proves nothing", S.HudVerified.Count == 0 && S.HudProofs.Count == 0);
            HudAct($"[U:1:5001]\tAlice\t{HudCheck.Command(R)}");
            HudAct($"[U:1:5002]\tBob\t{HudCheck.Command(R)}");
            HudAct($"[U:1:5003]\tCara\t{HudCheck.Command(Old)}");
            Check("HUD check: a click verifies its player on its release, and the first click on a release is its proof",
                S.HudVerified.GetValueOrDefault(5001) == R && S.HudVerified.GetValueOrDefault(5002) == R && S.HudVerified.GetValueOrDefault(5003) == Old
                && S.HudProofs.Count == 2 && S.HudProofs[R].StartsWith("Alice [U:1:5001]") && S.HudProofs[Old].StartsWith("Cara"));
            var served = HudState.Decode(BuildHud(Now()).Encode())!;
            Check(HudCheckHold
                    ? "HUD check, held (the owner's rule while the HUD update is under review, Selection.cs): the bridge serves the verifications but no proof, so the plugin stays a dry run on every release - and keeps the proofs for when the hold lifts"
                    : "HUD check: the bridge serves them, and with HUD_CHECK=dryrun the plugin then enforces on the proven release only",
                served.CheckMode == "dryrun" && served.Verified.GetValueOrDefault(5001) == R && (HudCheckHold
                    ? served.Proofs.Count == 0 && HudCheck.ModeOf(served, 0, R) == HudCheck.Mode.DryRun && S.HudProofs.ContainsKey(R)
                    : HudCheck.ModeOf(served, 0, R) == HudCheck.Mode.Enforce)
                && HudCheck.ModeOf(served, 0, "v2_2_0") == HudCheck.Mode.DryRun);
            Check("HUD check: refused click lines change nothing", !HudAct("[U:1:5004]\tDan\t!hudok v9") && !S.HudVerified.ContainsKey(5004));
        }
        finally
        {
            (HudCheckMode, S.HudVerified, S.HudProofs, StateDir) = (savedMode, savedVerified, savedProofs, savedDir);
            try { Directory.Delete(dir, true); } catch (Exception e) { Console.WriteLine($"selftest: could not remove {dir}: {e.Message}"); }
        }
    }

    /// <summary>--selftest, part five: the TAB chord (HudTab.cs) on a handed clock, and the check's pause handling.</summary>
    static void TabSelfTest(Action<string, bool> Check)
    {
        var t0 = new DateTime(2026, 10, 11, 20, 0, 0, DateTimeKind.Utc);
        const ulong TAB = HudTab.Scoreboard, E = HudTab.Use, RMB = HudTab.Attack2, W = 1UL << 3, JUMP = 1UL << 1;
        Check("TAB chord: the bits are CounterStrikeSharp's (IN_SCORE 1<<33, IN_USE 1<<5, IN_ATTACK2 1<<11)",
            HudTab.Scoreboard == 0x200000000UL && HudTab.Use == 0x20UL && HudTab.Attack2 == 0x800UL);
        Check("TAB: the mode flag reads off, menu, on (chord, board), down, hold and window, anything else as off - and every name reads back",
            HudTab.Parse("menu") == HudTab.Mode.Menu && HudTab.Parse(" ON ") == HudTab.Mode.On && HudTab.Parse("chord") == HudTab.Mode.On && HudTab.Parse("down") == HudTab.Mode.Down
            && HudTab.Parse("hold") == HudTab.Mode.Hold && HudTab.Parse("Window") == HudTab.Mode.Window && HudTab.Parse("off") == HudTab.Mode.Off
            && HudTab.Parse(null) == HudTab.Mode.Off && HudTab.Parse("yes") == HudTab.Mode.Off
            && Enum.GetValues<HudTab.Mode>().All(m => HudTab.Parse(HudTab.Name(m)) == m));
        var lines = Enum.GetValues<HudTab.Mode>().Select(HudTab.BoardLine).ToList();
        Check($"TAB chord: the board's command line fits it in every mode (longest {lines.Max(l => l.Length)}, the old one 52)", lines.All(l => l.Length <= 56)
            && HudTab.BoardLine(HudTab.Mode.Off) == HudView.StaticTexts().First(t => t.Id == "gg_b_cmds").Text);

        var tab = new HudTab();
        Check("TAB chord: off - nothing, whatever is pressed",
            tab.Buttons(1, TAB, 0, HudTab.Mode.Off, t0) == null && tab.Buttons(1, E | RMB, 0, HudTab.Mode.Off, t0) == null && !tab.Cursor(1));
        tab = new HudTab();
        Check("TAB chord: E, right-click, moving or jumping without TAB do nothing - only with TAB held",
            tab.Buttons(2, E, 0, HudTab.Mode.On, t0) == null && tab.Buttons(2, 0, E, HudTab.Mode.On, t0) == null && tab.Buttons(2, RMB, 0, HudTab.Mode.On, t0) == null
            && tab.Buttons(2, 0, RMB, HudTab.Mode.On, t0) == null && tab.Buttons(2, W | JUMP, 0, HudTab.Mode.On, t0) == null
            && tab.Buttons(2, TAB, W | JUMP, HudTab.Mode.On, t0) == null && !tab.Cursor(2));
        tab = new HudTab();
        tab.Buttons(3, TAB, 0, HudTab.Mode.Menu, t0);
        Check("TAB chord: TAB + E opens the menu (menu mode), and TAB + right-click there does nothing",
            tab.Buttons(3, E, 0, HudTab.Mode.Menu, t0.AddSeconds(1)) == HudTab.Act.OpenMenu && tab.Buttons(3, RMB, 0, HudTab.Mode.Menu, t0.AddSeconds(2)) == null
            && !tab.Cursor(3));
        tab = new HudTab();
        tab.Buttons(4, TAB, 0, HudTab.Mode.On, t0);
        var on = tab.Buttons(4, RMB, 0, HudTab.Mode.On, t0.AddSeconds(1));
        var during = tab.Cursor(4);
        var off = tab.Buttons(4, 0, TAB, HudTab.Mode.On, t0.AddSeconds(3));
        Check("TAB chord: TAB + right-click gives the board's cursor, letting go of TAB takes it back",
            on == HudTab.Act.CursorOn && during && off == HudTab.Act.CursorOff && !tab.Cursor(4));
        tab = new HudTab();
        tab.Buttons(5, TAB, 0, HudTab.Mode.On, t0);
        tab.Buttons(5, RMB, 0, HudTab.Mode.On, t0);
        var stillOn = tab.Buttons(5, W, 0, HudTab.Mode.On, t0.AddMinutes(30)) == null && tab.Cursor(5) && tab.CursorSlots().SequenceEqual([5]);
        Check("TAB chord: no time limit - the board's cursor stays while TAB is held, however long, and TAB coming up ends it (2026-10-21)",
            stillOn && tab.Buttons(5, 0, TAB, HudTab.Mode.On, t0.AddMinutes(30)) == HudTab.Act.CursorOff && !tab.CursorSlots().Any());
        tab = new HudTab();
        tab.Buttons(6, TAB, 0, HudTab.Mode.On, t0);
        tab.Buttons(6, RMB, 0, HudTab.Mode.On, t0);
        var again = tab.Buttons(6, 0, RMB, HudTab.Mode.On, t0.AddSeconds(1)) == null && tab.Buttons(6, RMB, 0, HudTab.Mode.On, t0.AddSeconds(2)) == null;
        var toMenu = tab.Buttons(6, E, 0, HudTab.Mode.On, t0.AddSeconds(3));
        Check("TAB chord: a second right-click changes nothing; TAB + E with the board's cursor on opens the menu, which keeps the cursor itself",
            again && toMenu == HudTab.Act.OpenMenu && !tab.Cursor(6));
        tab = new HudTab();
        foreach (var slot in new[] { 7, 8, 9 }) { tab.Buttons(slot, TAB, 0, HudTab.Mode.On, t0); tab.Buttons(slot, RMB, 0, HudTab.Mode.On, t0); }
        var hit = tab.Release(7) && !tab.Release(7) && !tab.Cursor(7);
        tab.Gone(8);
        var all = tab.ReleaseAll();
        Check("TAB chord: a hit or death takes the cursor back (once), leaving forgets the slot, css_gghud release takes every cursor back",
            hit && !tab.Cursor(8) && all.SequenceEqual([9]) && !tab.CursorSlots().Any());
        tab = new HudTab();
        tab.Buttons(10, TAB, 0, HudTab.Mode.On, t0);
        tab.Buttons(10, RMB, 0, HudTab.Mode.On, t0);
        tab.MapStarted();
        Check("TAB chord: a map change forgets everything (everybody reconnects)", !tab.Cursor(10) && tab.Buttons(10, E, 0, HudTab.Mode.On, t0.AddSeconds(1)) == null);

        // ---- the experiment modes (css_gghud tab down|hold|window) ----
        tab = new HudTab();
        var downOn = tab.Buttons(20, TAB, 0, HudTab.Mode.Down, t0);
        var downHeld = tab.Cursor(20);
        var downOff = tab.Buttons(20, 0, TAB, HudTab.Mode.Down, t0.AddSeconds(5));
        Check("TAB down: the cursor from the moment TAB goes down, off when it comes up",
            downOn == HudTab.Act.CursorOn && downHeld && downOff == HudTab.Act.CursorOff && !tab.Cursor(20));
        tab = new HudTab();
        tab.Buttons(21, TAB, 0, HudTab.Mode.Down, t0);
        Check("TAB down: TAB held for minutes, moving and clicking - the cursor stays (the old 10 s limit ended it "
            + "with TAB still held, and that click fired the gun); TAB coming up ends it, the next TAB brings it back",
            tab.Buttons(21, W, 0, HudTab.Mode.Down, t0.AddSeconds(10)) == null && tab.Buttons(21, 1UL, 0, HudTab.Mode.Down, t0.AddSeconds(10)) == null && tab.Cursor(21)
            && tab.Buttons(21, 0, W | 1UL, HudTab.Mode.Down, t0.AddMinutes(5)) == null && tab.Cursor(21)
            && tab.Buttons(21, 0, TAB, HudTab.Mode.Down, t0.AddMinutes(5)) == HudTab.Act.CursorOff && !tab.Cursor(21)
            && tab.Buttons(21, TAB, 0, HudTab.Mode.Down, t0.AddMinutes(6)) == HudTab.Act.CursorOn);
        tab = new HudTab();
        var holdArm = tab.Buttons(22, TAB, 0, HudTab.Mode.Hold, t0);
        var holdEarly = tab.Hold(22, HudTab.Mode.Hold, t0.AddSeconds(0.1));
        var holdOn = tab.Hold(22, HudTab.Mode.Hold, t0.AddSeconds(HudTab.HoldSeconds));
        var holdOff = tab.Buttons(22, 0, TAB, HudTab.Mode.Hold, t0.AddSeconds(2));
        tab.Buttons(22, TAB, 0, HudTab.Mode.Hold, t0.AddSeconds(3));
        tab.Buttons(22, 0, TAB, HudTab.Mode.Hold, t0.AddSeconds(3.1));
        var holdTapped = tab.Hold(22, HudTab.Mode.Hold, t0.AddSeconds(3 + HudTab.HoldSeconds));
        Check($"TAB hold: the cursor only once TAB has been held {HudTab.HoldSeconds} s - a quick look at the scores gets none - and off when TAB comes up",
            holdArm == HudTab.Act.HoldCheck && holdEarly == null && holdOn == HudTab.Act.CursorOn && holdOff == HudTab.Act.CursorOff && holdTapped == null
            && tab.Hold(22, HudTab.Mode.On, t0.AddSeconds(5)) == null);
        tab = new HudTab();
        var winOpen = tab.Buttons(23, TAB, 0, HudTab.Mode.Window, t0);
        var winIs = tab.Window(23) && !tab.Cursor(23);
        var winClose = tab.Buttons(23, 0, TAB, HudTab.Mode.Window, t0.AddSeconds(4));
        tab.Buttons(23, TAB, 0, HudTab.Mode.Window, t0.AddSeconds(5));
        var winHit = tab.Release(23) && !tab.Window(23) && tab.Buttons(23, 0, TAB, HudTab.Mode.Window, t0.AddSeconds(7)) == null;
        Check("TAB window: the menu window while TAB is held (the window keeps the cursor), closed when TAB comes up; a hit or death closes it, and TAB up then asks nothing",
            winOpen == HudTab.Act.OpenWindow && winIs && winClose == HudTab.Act.CloseWindow && !tab.Window(23) && winHit);
        var held = new HudSlots();
        held.Show(40, HudSlots.Page.Start, t0);
        for (var sec = 0.25; sec <= 120; sec += 0.25) { held.Hold(40, t0.AddSeconds(sec)); held.Frame(t0.AddSeconds(sec)); }
        var heldView = held.Frame(t0.AddSeconds(120)).First(v => v.Slot == 40).View;
        var afterHold = held.Frame(t0.AddSeconds(120 + HudSlots.IdleCloseSeconds)).First(v => v.Slot == 40).View;
        var notHeld = new HudSlots();
        notHeld.Show(41, HudSlots.Page.Start, t0);
        Check("TAB window: no idle close while TAB holds it (two minutes here) and no drain bar; once the hold ends the idle close is back - "
            + "and a window TAB does not hold still closes after its 30 s",
            heldView is { Capture: true, Draining: false } && !afterHold.Capture && !notHeld.Frame(t0.AddSeconds(HudSlots.IdleCloseSeconds)).First(v => v.Slot == 41).View.Capture);
        Check("TAB: E with TAB held opens the menu in every mode but off",
            Enum.GetValues<HudTab.Mode>().Where(m => m != HudTab.Mode.Off).All(m =>
            {
                var t = new HudTab();
                t.Buttons(30, TAB, 0, m, t0);
                return t.Buttons(30, E, 0, m, t0.AddSeconds(0.5)) == HudTab.Act.OpenMenu && !t.Cursor(30) && !t.Window(30);
            }));
        tab = new HudTab();
        tab.Buttons(24, TAB, 0, HudTab.Mode.Down, t0);
        tab.Buttons(25, TAB, 0, HudTab.Mode.Window, t0);
        var released = tab.ReleaseAll();
        Check("TAB: css_gghud release (and switching off) takes every TAB cursor and window back",
            released.OrderBy(x => x).SequenceEqual([24, 25]) && !tab.Cursor(24) && !tab.Window(25));
        Check("TAB: the log names the buttons the client sent",
            HudTab.Names(TAB | W | E) == "TAB+E+W" && HudTab.Names(0) == "-" && HudTab.Names(1UL << 40) == "0x10000000000" && HudTab.Names(1UL | RMB) == "LMB+RMB");
        tab = new HudTab();
        tab.Buttons(26, TAB, 0, HudTab.Mode.Hold, t0);
        Check("TAB: what is worth logging - TAB held in an experiment mode, a TAB cursor or window up; not plain play",
            tab.Watching(26, HudTab.Mode.Hold) && !tab.Watching(26, HudTab.Mode.On) && !new HudTab().Watching(27, HudTab.Mode.Down));

        Check("TAB: down is the default (the owner's choice, 2026-10-21) - HUD_TAB unset - and reads back", HudTab.Default == HudTab.Mode.Down
            && HudTab.Parse(HudTab.Name(HudTab.Default)) == HudTab.Mode.Down && (Environment.GetEnvironmentVariable("HUD_TAB") is { Length: > 0 } || HudTabMode == HudTab.Mode.Down));
        var sent = new HudState { TabMode = "on" };
        Check("TAB chord: the state carries the mode to the plugin, and an older bridge's state means off",
            HudState.Decode(sent.Encode())!.TabMode == "on" && HudTab.Parse(HudState.Decode(new HudState().Encode())!.TabMode) == HudTab.Mode.Off);
        var savedTab = HudTabMode;
        try
        {
            HudTabMode = HudTab.Mode.Menu;
            Check("TAB chord: the bridge serves HUD_TAB", HudState.Decode(BuildHud(Now()).Encode())!.TabMode == "menu");
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
        Check("HUD check: after a pause past the deadline the player still gets the warning, and 15 s to click, before the disconnect",
            acts.Count == 2 && acts[0] is (11, HudGate.Act.Warn, 15, 120) && acts[1] is (11, HudGate.Act.Kick, 0, 135));
    }
}
