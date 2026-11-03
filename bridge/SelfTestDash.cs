// --selftest, the Sync page (content v4_3_0, 2026-11-03): the block's codec, what a player with the page open is sent and how its pagers page,
// the words out of the sync's file (SyncWords) on a handed clock, the website's data, the bridge's own loop over the file, and the layout's
// labels and the stylesheet's rules against everything the view can send. Self-contained: its own file, clock and state directory, put back.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

static partial class Program
{
    /// <summary>A sync-status.json of schema 1 as the sync writes it (made-up maps), with a field the schema does not know.</summary>
    static string DashSample(long updated, int queued = 25, int events = 20, string state = "serve")
    {
        var next = string.Join(",", Enumerable.Range(1, queued).Select(i =>
            $"{{\"id\":\"39{i:D8}\",\"name\":\"queued_map_{i}\",\"mode\":\"{(i % 3 == 0 ? "wingman" : "deathmatch")}\",\"sizeMb\":{(i == 1 ? 2451 : 100 + i)},\"why\":\"it would make one of its mode's lists of maps the server keeps\"}}"));
        string[] kinds = ["handed-over", "failed", "listed", "broken", "removed", "changed"];
        var maps = string.Join(",", Enumerable.Range(1, events).Select(i =>
            $"{{\"at\":{updated - 600 - i * 60},\"id\":\"38{i:D8}\",\"name\":\"{(i == 2 ? "" : $"event_map_{i}")}\",\"mode\":\"armsrace\",\"event\":\"{kinds[i % kinds.Length]}\",\"why\":\"Reason {i}.\"}}"));
        var runs = string.Join(",", Enumerable.Range(1, 7).Select(i => i == 1
            ? $"{{\"started\":{updated - 60},\"ended\":null,\"kind\":\"auto\",\"downloads\":3,\"bytes\":3000000,\"pass\":2,\"rejected\":0,\"listed\":0,\"removed\":0,\"stop\":null}}"
            : $"{{\"started\":{updated - i * 7200},\"ended\":{updated - i * 7200 + 3700},\"kind\":\"{(i % 2 == 0 ? "manual" : "auto")}\",\"downloads\":{i * 10},\"bytes\":{i * 1_000_000_000L},\"pass\":{i * 9},\"rejected\":{i},\"listed\":{i * 8},\"removed\":1,\"stop\":{(i == 3 ? "\"3 downloads in a row failed - no more in this run\"" : "null")}}}"));
        return $"{{\"schema\":1,\"updated\":{updated},\"extra\":{{\"ignored\":true}},\"now\":{{\"state\":\"{state}\",\"why\":\"Downloading the maps the server keeps but does not have yet.\","
            + "\"map\":{\"id\":\"3700000001\",\"name\":\"aim_test_one\",\"mode\":\"armsrace\",\"sizeMb\":42},\"step\":{\"done\":160,\"total\":320},"
            + $"\"run\":{{\"started\":{updated - 60},\"deadline\":{updated + 10740},\"kind\":\"auto\"}}}},"
            + "\"today\":{\"downloads\":847,\"bytes\":170156555775,\"pass\":582,\"rejected\":21,\"failedDownloads\":1,\"apiCalls\":526},"
            + "\"totals\":{\"tested\":1606,\"pass\":1521,\"broken\":22,\"nonmap\":63,\"listed\":{\"armsrace\":3718,\"deathmatch\":1096,\"wingman\":827}},"
            + $"\"queue\":{{\"total\":{(queued == 0 ? 0 : 3845)},\"bytes\":560708125123,\"byMode\":{{\"armsrace\":22,\"deathmatch\":2100,\"wingman\":{{\"count\":2473}}}},\"eta\":{updated + 129971},\"next\":[{next}],"
            + "\"serve\":{\"missing\":307,\"bytes\":56449040384,\"handedOver\":198,\"handedOverBytes\":42475609895}},"
            + "\"keep\":{\"parts\":[{\"part\":\"up\",\"mode\":\"\",\"count\":42,\"onDisk\":42,\"gb\":5.8}],\"totalGb\":134.7,\"count\":709,\"onDisk\":204,\"problems\":[]},"
            + $"\"history\":{{\"runs\":[{runs}],\"maps\":[{maps}]}}}}";
    }

    static void DashSelfTest(Action<string, bool> Check)
    {
        const long updated = 1793725560; // a Tuesday, six minutes past five in the afternoon, UTC
        var now = DateTime.UnixEpoch.AddSeconds(updated + 30);
        var zone = TimeZoneInfo.Utc;
        List<SyncWords.RandomQueue> rq = [new(QueueNew, 15, 15), new(QueueOriginal, 14, 15), new(Modes.DeathmatchId, 15, 15), new(Modes.WingmanId, 9, 15)];
        SyncWords.Input In(string? json, DateTime at, string problem = "", bool known = true) =>
            new(json == null ? null : JsonDocument.Parse(json).RootElement, now, problem, rq, known, now.AddSeconds(-40), 15, at);
        HudDash D(string? json, DateTime at, string problem = "", bool known = true) => SyncWords.Dash(In(json, at, problem, known), zone);

        // ---- the words out of the file ----
        var d = D(DashSample(updated), now);
        string Wd(HudDash x, string n) => x.Words.GetValueOrDefault(n, "<none>");
        bool Cl(HudDash x, string n) => x.Classes.GetValueOrDefault(n);
        Check("Sync page, now: the state in words, its look busy and no other, the why, the map with its mode and size, the step and its notch on the bar (160 of 320: osdp50)",
            Wd(d, "osdstate") == "Fetching maps for the server" && Cl(d, "osdbusy") && !Cl(d, "osdwait") && !Cl(d, "osdidle") && !Cl(d, "osdstop")
            && Wd(d, "osdwhy").StartsWith("Downloading the maps") && Wd(d, "osdmap") == "aim_test_one  ·  Arms Race  ·  42 MB" && Cl(d, "osdmapon")
            && Wd(d, "osdstep") == "160 of 320" && Cl(d, "osdbar") && Cl(d, "osdp50") && Enumerable.Range(0, 21).Count(k => Cl(d, $"osdp{k * 5}")) == 1);
        Check("...the run (since, automatic, its deadline), today's numbers in the menu's units, the lists per mode, the tests so far, and a fresh file's age",
            Wd(d, "osdrun") == "Run since 17:05  ·  automatic  ·  ends by 20:05 at the latest"
            && Wd(d, "osdtoday") == "Today: 847 downloads (158.5 GB)  ·  582 passed, 21 rejected  ·  1 failed downloads  ·  526 Web API calls"
            && Wd(d, "osdlisted") == "Listed: Arms Race 3,718  ·  Deathmatch 1,096  ·  Wingman 827" && Wd(d, "osdtested") == "Tested so far: 1,606  ·  1,521 passed, 22 broken, 63 not maps"
            && Wd(d, "osdage") == "Updated less than a minute ago" && !Cl(d, "osdold") && !Cl(d, "osdnone"));
        Check("Sync page, queue: how many wait and their size, per mode (a count or an object with one), the finish estimate, the server's own missing maps, the random queues ready on disk",
            Wd(d, "osdqhead") == "3,845 maps waiting  ·  522.2 GB" && Wd(d, "osdqmodes") == "Arms Race 22  ·  Deathmatch 2,100  ·  Wingman 2,473"
            && Wd(d, "osdqeta") == $"All tested by about {SyncWords.When(updated + 129971, now, zone)}" && SyncWords.When(updated + 129971, now, zone) == "Thu 5 Nov 05:12"
            && Wd(d, "osdqserve") == "The server lacks 307 maps it keeps (52.6 GB): those come first  ·  198 handed over for the next restart"
            && Wd(d, "osdqrand") == "Random picks ready on the server: Arms Race new 15/15  ·  Arms Race original 14/15  ·  Deathmatch 15/15  ·  Wingman 9/15"
            && Wd(d, "osdqshown") == "The next 25 of 3,845");
        var queue = d.Lists.Single(l => l.Name == HudDash.QueueList);
        Check($"...the next maps in order, {SyncWords.QueueRows} a page: place, name, mode and size, the why with a capital",
            queue.PageSize == SyncWords.QueueRows && queue.Rows.Count == 25 && queue.Pages == 5 && queue.Rows[0].Fields.SequenceEqual(["1", "queued_map_1", "Deathmatch  ·  2.5 GB", "It would make one of its mode's lists of maps the server keeps"])
            && queue.Rows[2].Fields[2] == "Wingman  ·  103 MB" && queue.Rows[24].Fields[0] == "25");
        var runs = d.Lists.Single(l => l.Name == HudDash.RunList);
        Check($"Sync page, history: the runs newest first, {SyncWords.RunRows} a page - the one running now gold (hot) and saying so, one stopped early dimmed with why, the others finished",
            runs.PageSize == SyncWords.RunRows && runs.Rows.Count == 7 && runs.Rows[0].Tone == "hot" && runs.Rows[0].Fields[2] == "Running now" && runs.Rows[0].Fields[0] == "17:05 - now  ·  1 min  ·  automatic"
            && runs.Rows[1].Fields[0] == "13:06 - 14:07  ·  1 h 1 min  ·  by hand" && runs.Rows[1].Fields[1] == "20 downloads (1.9 GB)  ·  18 passed, 2 rejected  ·  16 listed, 1 removed" && runs.Rows[1].Fields[2] == "Finished"
            && runs.Rows[2].Tone == "dim" && runs.Rows[2].Fields[2] == "Stopped: 3 downloads in a row failed - no more in this run");
        var events = d.Lists.Single(l => l.Name == HudDash.MapList);
        Check($"...the map events newest first, {SyncWords.MapRows} a page: when, what (in words, coloured by kind), the map and its mode, why; a nameless one by its Workshop id",
            events.PageSize == SyncWords.MapRows && events.Rows.Count == 20 && events.Rows[0].Fields.SequenceEqual(["16:55", "Failed", "event_map_1  ·  Arms Race", "Reason 1."]) && events.Rows[0].Tone == "bad"
            && events.Rows[1].Fields[2] == "Workshop item 3800000002  ·  Arms Race" && events.Rows[1].Fields[1] == "Listed" && events.Rows[1].Tone == "ok"
            && events.Rows[2].Tone == "bad" && events.Rows[3].Tone == "dim" && events.Rows[4].Tone == "hot" && events.Rows[4].Fields[1] == "Updated" && events.Rows[5].Fields[1] == "To the server");
        var oldD = D(DashSample(updated), now.AddMinutes(SyncWords.OldMinutes));
        var gone = D(null, now);
        var broken = D(DashSample(updated), now, "the file does not parse: x");
        var odd = D(DashSample(updated, state: "frobnicate"), now);
        Check($"The file's age: old after {SyncWords.OldMinutes} minutes without a new 'updated' (said, and red), missing (said, osdnone), a newer one that was not taken (said beside the age)",
            Cl(oldD, "osdold") && Wd(oldD, "osdage") == "Old: the sync last wrote 15 min ago - it may be down" && Cl(gone, "osdnone") && !Cl(gone, "osdold")
            && Wd(gone, "osdstate") == "No word from the sync" && Wd(gone, "osdage") == "No word from the sync yet" && Looks(gone) == 0 && Wd(gone, "osdqhead") == "No queue yet"
            && Wd(broken, "osdage") == "Updated less than a minute ago (the newest file was not taken: the file does not parse: x)"
            && Wd(D(null, now, "schema 2, not 1"), "osdage") == "No word from the sync: schema 2, not 1");
        static int Looks(HudDash x) => SyncWords.Looks.Count(l => x.Classes.GetValueOrDefault($"osd{l}"));
        Check("A state the dashboard does not know shows as the sync names it, busy; each known state has its words and one look",
            Wd(odd, "osdstate") == "Frobnicate" && Cl(odd, "osdbusy") && SyncWords.States.All(s => Looks(D(DashSample(updated, state: s.State), now)) == 1 && Wd(D(DashSample(updated, state: s.State), now), "osdstate") == s.Words));
        var unknownDisk = D(DashSample(updated), now, known: false);
        var empty = D(DashSample(updated, queued: 0, events: 0), now);
        Check("Without a fresh report from the server the random queues say how many are rolled, not how many are on disk; an empty queue says so",
            Wd(unknownDisk, "osdqrand") == "Random picks rolled ahead: Arms Race new 15  ·  Arms Race original 15  ·  Deathmatch 15  ·  Wingman 15  ·  no fresh word from the server on which are on its disk"
            && Wd(empty, "osdqhead") == "Nothing waiting: every map found so far is tested" && Wd(empty, "osdqeta") == "" && empty.Lists.Single(l => l.Name == HudDash.QueueList).Rows.Count == 0
            && Wd(empty, "osdqshown") == "");
        Check("Sizes as the Maps page shows them, and times: today the time alone, else the day",
            SyncWords.Bytes(56449040384) == "52.6 GB" && SyncWords.Bytes(3000000) == "3 MB" && SyncWords.SizeMb(2451) == "2.5 GB" && SyncWords.SizeMb(42) == "42 MB" && SyncWords.SizeMb(null) == ""
            && SyncWords.When(updated, now, zone) == "17:06" && SyncWords.When(updated - 86400, now, zone) == "yesterday 17:06" && SyncWords.When(updated + 86400, now, zone) == "tomorrow 17:06"
            && SyncWords.Ago(TimeSpan.FromMinutes(75)) == "1 h 15 min ago");
        Check("Every word fits the block (a field and a word at most 120 characters), and every name is one the plugin takes",
            d.Words.All(w => HudDash.IsName(w.Key) && w.Value.Length <= HudDash.MaxText) && d.Classes.Keys.All(HudDash.IsName) && d.Lists.All(l => HudDash.IsListName(l.Name))
            && d.Words.Count <= HudDash.MaxWords && d.Classes.Count <= HudDash.MaxClasses);

        // ---- the codec ----
        d.Version = 42;
        var text = d.Encode();
        var back = HudDash.Decode(text);
        Check("The block's codec: what the bridge encodes decodes to the same block, version included",
            back != null && back.Version == 42 && back.Encode() == text && back.Lists.Count == 3 && back.Lists[0].Rows.Count == 25);
        var junk = HudDash.Decode(HudDash.Magic + "\nver\t7\nw\tosdgood\tyes\nw\tbad\tno\nw\tosd-x\tno\nc\tosdlit\t1\nc\tgg_win\t1\nl\tosdz\t3\nr\tosdz\tweird\tone\nr\tosdy\t\tlost\nl\tosdz\t4\nzz\tq\nl\tosdk9\t2\nl\tosdw\t99\n");
        Check("...takes only names that start with osd, lists it declared, a known tone (or none), and skips anything else",
            junk != null && junk.Version == 7 && junk.Words.Count == 1 && junk.Words["osdgood"] == "yes" && junk.Classes.Count == 1 && junk.Classes["osdlit"]
            && junk.Lists.Count == 1 && junk.Lists[0].PageSize == 3 && junk.Lists[0].Rows.Single().Tone == "" && junk.Lists[0].Rows.Single().Fields.Single() == "one"
            && HudDash.Decode("gg-hud\t1\n") == null && HudDash.Decode(null) == null);
        Check("The live state carries the block's version, an older plugin skips it",
            HudState.Decode(new HudState { DashVersion = 9 }.Encode())!.DashVersion == 9 && HudState.Decode(new HudState().Encode())!.DashVersion == 0);

        // ---- what a player is sent, and the pagers ----
        var pages = new Dictionary<string, int>();
        (Dictionary<string, string> W, Dictionary<string, bool> C) V() { var (w, c) = HudDash.View(back, pages); return (w.ToDictionary(x => x.Name, x => x.Text), c.ToDictionary(x => x.Name, x => x.On)); }
        var v1 = V();
        Check("A player's view: the first page of each list - its rows' fields, the rows in use and their tones, the page's number and count, first and not last",
            v1.W["osdq1a"] == "1" && v1.W["osdq1b"] == "queued_map_1" && v1.W["osdq6a"] == "6" && v1.C["osdq6"] && v1.W["osdqpn"] == "1" && v1.W["osdqps"] == "5" && v1.C["osdqfirst"] && !v1.C["osdqlast"]
            && !v1.C["osdqnone"] && v1.C["osdr1hot"] && !v1.C["osdr1ok"] && v1.C["osdm1bad"] && !v1.C["osdm1ok"] && v1.W["osdstate"] == "Fetching maps for the server" && v1.C["osdbusy"]);
        Check("...a pager's Next moves that list alone, Last goes to its last page, where the rows it lacks are empty and off",
            HudDash.Click(back, "gg_osdq_next", pages) && V().W["osdq1a"] == "7" && V().W["osdmpn"] == "1"
            && HudDash.Click(back, "gg_osdq_last", pages) && V() is var vl && vl.W["osdq1a"] == "25" && vl.W["osdq2a"] == "" && vl.W["osdq2b"] == "" && !vl.C["osdq2"] && vl.C["osdq1"] && vl.C["osdqlast"] && !vl.C["osdqfirst"]
            && HudDash.Click(back, "gg_osdq_next", pages) && pages["osdq"] == 4 && HudDash.Click(back, "gg_osdq_first", pages) && pages["osdq"] == 0 && HudDash.Click(back, "gg_osdq_prev", pages) && pages["osdq"] == 0);
        Check("...anything but a pager of a list the block has is no pager click, and the menu's own buttons are not taken",
            !HudDash.Click(back, "gg_osdx_next", pages) && !HudDash.Click(back, "gg_osdq_up", pages) && !HudDash.Click(back, HudDash.Tab, pages) && !HudDash.Click(back, "gg_pg_next", pages)
            && !HudDash.Click(null, "gg_osdq_next", pages) && !HudDash.Click(back, "gg_m1_play", pages));
        pages["osdm"] = 2;
        var shrunk = HudDash.Decode(D(DashSample(updated, queued: 3, events: 5), now).Encode());
        var (sw, sc) = HudDash.View(shrunk, pages);
        Check("...a page that the list no longer has falls back to its last one; an empty list says so (osdqnone)",
            pages["osdm"] == 0 && sw.Single(x => x.Name == "osdm5a").Text != "" && sw.Single(x => x.Name == "osdm6a").Text == ""
            && HudDash.View(HudDash.Decode(empty.Encode()), new()).Classes.Single(x => x.Name == "osdqnone").On);
        var (nw, nc) = HudDash.View(null, new());
        Check("...and before the first block: one word that says so", nw.Single().Name == "osdstate" && nc.Single() == ("osdnone", true));
        Check("The Sync tab and its pagers prove content v4_3_0, and the tab opens the Sync page (no chat command: the owner's list is as it was)",
            HudView.NewButtons43.All(b => HudView.LevelOfButton(b) == 43) && HudView.Tabs.Single(t => t.Id == HudDash.Tab).Page == HudSlots.Page.Sync
            && ChatCommands.Parse("!sync").Kind == ChatCommands.Kind.Unknown && HudVote.PageLevel("Sync") == 0);

        // ---- the website's data ----
        var web = SyncWords.WebStamped(SyncWords.Web(In(DashSample(updated), now), d, zone), now);
        using (var doc = JsonDocument.Parse(web))
        {
            var r = doc.RootElement;
            Check("The website's data: when it was written first, the page's words, the random queues, and the sync's own file as it was read (unknown fields too)",
                web.StartsWith("{\"generated\":" + (updated + 30).ToString(CultureInfo.InvariantCulture) + ",", StringComparison.Ordinal) && r.GetProperty("words").GetProperty("state").GetString() == "Fetching maps for the server"
                && r.GetProperty("random").GetProperty("queues").GetArrayLength() == 4 && r.GetProperty("random").GetProperty("known").GetBoolean()
                && r.GetProperty("sync").GetProperty("extra").GetProperty("ignored").GetBoolean() && r.GetProperty("sync").GetProperty("history").GetProperty("maps").GetArrayLength() == 20
                && r.GetProperty("looks").GetProperty("busy").GetBoolean() && r.GetProperty("oldMinutes").GetInt32() == SyncWords.OldMinutes);
        }
        using (var doc = JsonDocument.Parse(SyncWords.WebStamped(SyncWords.Web(In(null, now), gone, zone), now)))
            Check("...and without a file: no sync, the words that say so", doc.RootElement.GetProperty("sync").ValueKind == JsonValueKind.Null && doc.RootElement.GetProperty("words").GetProperty("state").GetString() == "No word from the sync");
        Check("The website's page is built into the bridge and reads its data from beside itself, every 5 s, with nothing from elsewhere",
            DashboardPage() is var page && page.Contains("dashboard.json?t=") && page.Contains("setInterval(load, 5000)") && !Regex.IsMatch(page, @"<(script|link|img)[^>]+(src|href)=""https?://")
            && !page.Contains("<iframe", StringComparison.OrdinalIgnoreCase));

        // ---- the bridge's own loop: the file read when it changes, the block rebuilt, the website's files written; a bad file keeps the last good one ----
        var saved = (StateDir, Now, SyncOn, SyncDoc, SyncStamp, SyncReadAt, SyncProblem, DashText, DashVersion, dashBody, webWritten, webPage, webWrittenAt, syncTicks, ReadyAt, ReadyIds);
        var dir = Path.Combine(Path.GetTempPath(), $"gungamebridge-dash-{Environment.ProcessId}");
        try
        {
            Directory.CreateDirectory(dir);
            StateDir = dir;
            var clock = now;
            Now = () => clock;
            (SyncOn, SyncDoc, SyncStamp, SyncProblem, dashBody, webWritten, webPage, webWrittenAt, syncTicks) = (true, null, null, "", "", "", "", default, 0);
            var v0 = DashVersion;
            SyncTick(clock);
            var before = HudDash.Decode(DashText);
            File.WriteAllText(SyncStatusPath, DashSample(updated));
            for (var i = 0; i < StatusLookSeconds; i++) SyncTick(clock = clock.AddSeconds(1));
            var after = HudDash.Decode(DashText);
            Check("The bridge's loop: no file - the block says so; the file read within its look, the block rebuilt under a new version, the website's two files written",
                before != null && before.Words["osdstate"] == "No word from the sync" && after != null && after.Words["osdstate"] == "Fetching maps for the server" && DashVersion > v0 && after.Version == DashVersion
                && File.Exists(Path.Combine(dir, "web", "index.html")) && File.Exists(Path.Combine(dir, "web", "dashboard.json"))
                && JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "web", "dashboard.json"))).RootElement.GetProperty("words").GetProperty("state").GetString() == "Fetching maps for the server"
                && BuildHud(clock).DashVersion == DashVersion);
            var version = DashVersion;
            for (var i = 0; i < DashBuildSeconds; i++) SyncTick(clock = clock.AddSeconds(1));
            Check("...nothing changed: the same version, so the plugin fetches nothing", DashVersion == version);
            File.WriteAllText(SyncStatusPath, "{\"schema\":1, broken");
            File.SetLastWriteTimeUtc(SyncStatusPath, DateTime.UtcNow.AddSeconds(5));
            for (var i = 0; i < StatusLookSeconds; i++) SyncTick(clock = clock.AddSeconds(1));
            var kept = HudDash.Decode(DashText);
            Check("...a file that does not parse: the last good one stays on screen, and the age line says the newest was not taken",
                kept != null && kept.Words["osdstate"] == "Fetching maps for the server" && kept.Words["osdage"].Contains("the newest file was not taken: the file does not parse") && SyncHealth().Contains("not taken"));
        }
        finally
        {
            (StateDir, Now, SyncOn, SyncDoc, SyncStamp, SyncReadAt, SyncProblem, DashText, DashVersion, dashBody, webWritten, webPage, webWrittenAt, syncTicks, ReadyAt, ReadyIds) = saved;
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }

        // ---- the layout: every word and class the view can send has its label or rule, and every label of the page is fed ----
        if (!File.Exists(HudLayoutFile) || !File.Exists(HudStyleFile)) { Console.WriteLine($"skip the Sync page's layout checks: {HudLayoutFile} or {HudStyleFile} not found"); return; }
        var xml = Regex.Replace(File.ReadAllText(HudLayoutFile), "<!--.*?-->", "", RegexOptions.Singleline);
        var css = Regex.Replace(File.ReadAllText(HudStyleFile), @"/\*.*?\*/", "", RegexOptions.Singleline);
        var from = xml.IndexOf("class=\"gg-page gg-page-sync\"", StringComparison.Ordinal);
        var to = xml.IndexOf("class=\"gg-live\"", StringComparison.Ordinal);
        var syncPage = from > 0 && to > from ? xml[from..to] : "";
        var labelled = Regex.Matches(syncPage, @"\{s:(osd[a-z0-9]+)\}").Select(m => m.Groups[1].Value).ToHashSet();
        var everywhere = Regex.Matches(xml, @"\{s:(osd[a-z0-9]+)\}").Select(m => m.Groups[1].Value).ToHashSet();
        // everything a view can send, across every state the words have: a full page, an old file, no file, every look, every list's first and last page
        var sentWords = new HashSet<string>();
        var sentClasses = new HashSet<string>();
        void Take(HudDash? x)
        {
            foreach (var p in new[] { 0, 99 })
            {
                var (w, c) = HudDash.View(x, new Dictionary<string, int> { [HudDash.QueueList] = p, [HudDash.RunList] = p, [HudDash.MapList] = p });
                foreach (var (n, _) in w) sentWords.Add(n);
                foreach (var (n, _) in c) sentClasses.Add(n);
            }
        }
        foreach (var x in new[] { d, oldD, gone, broken, empty, unknownDisk }.Concat(SyncWords.States.Select(s => D(DashSample(updated, state: s.State), now)))) Take(x);
        Take(null);
        var unlabelled = sentWords.Where(n => !labelled.Contains(n)).ToList();
        var unfed = labelled.Where(n => !sentWords.Contains(n)).ToList();
        Check($"Sync page layout: every word the view can send has a label on the page ({sentWords.Count}){(unlabelled.Count > 0 ? ": none for " + string.Join(", ", unlabelled) : "")}", syncPage != "" && unlabelled.Count == 0);
        Check($"...every label on the page is fed ({labelled.Count}), and no osd word is used outside it{(unfed.Count > 0 ? ": never sent " + string.Join(", ", unfed) : "")}", unfed.Count == 0 && everywhere.SetEquals(labelled));
        var unstyled = sentClasses.Where(c => !Regex.IsMatch(css, $@"\.{Regex.Escape(c)}\b")).ToList();
        Check($"...every class it can send has a rule ({sentClasses.Count}){(unstyled.Count > 0 ? ": none for " + string.Join(", ", unstyled) : "")}", unstyled.Count == 0);
        Check($"...{sentClasses.Count} class names at most, well inside the 1,024 the game takes for the whole HUD", sentClasses.Count <= 160);
        var pageButtons = Regex.Matches(syncPage, @"<Button\b[^>]*\bid=""([^""]+)""").Select(m => m.Groups[1].Value).Append(HudDash.Tab).ToHashSet();
        Check("...its buttons are exactly the tab and the pagers the plugin pages (HudDash.LayoutButtons), and nothing else on the page has an id",
            pageButtons.SetEquals(HudDash.LayoutButtons) && Regex.Matches(syncPage, @"\bid=""").Count == HudDash.LayoutButtons.Length - 1 && xml.Contains($"<Button id=\"{HudDash.Tab}\" class=\"gg-tab\">"));
        Check($"...a row for each line of a page: {SyncWords.QueueRows} in the queue, {SyncWords.RunRows} runs, {SyncWords.MapRows} map events",
            Regex.Matches(syncPage, @"class=""gg-dqrow gg-dq\d""").Count == SyncWords.QueueRows && Regex.Matches(syncPage, @"class=""gg-drrow gg-dr\d""").Count == SyncWords.RunRows
            && Regex.Matches(syncPage, @"class=""gg-dmrow gg-dm\d""").Count == SyncWords.MapRows);
        Check("...the page takes the whole window (the live column steps aside) and its tab is lit while it is open",
            Regex.IsMatch(css, @"\.gg-screen\.page-sync \.gg-live\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(css, @"\.gg-screen\.page-sync \.gg-page-sync\s*\{\s*visibility:\s*visible;")
            && Regex.IsMatch(css, @"\.gg-screen\.page-sync #gg_tab_sync\s*\{"));
    }
}
