using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Deploy;

/// <summary>docshots.py's --selfcheck, ported check for check: the offline checks, every resolution; no server, no client, no lock. Its deep check runs
/// the real run's own code, the Doer and all, against a fake of the harness: this program's own `docshots fake-harness`, in place of docshots.py's
/// FAKE_HARNESS script.</summary>
static partial class DocShots
{
    static readonly string FakeToken = string.Concat(Enumerable.Repeat("ab", 4)) + string.Concat(Enumerable.Repeat("cd", 4)) + string.Concat(Enumerable.Repeat("12", 4))
                                       + string.Concat(Enumerable.Repeat("34", 4));   // a made-up secret in the fake harness's answers, which the scrubber must hide

    /// <summary>A stand-in for cs2client.ps1 in the deep check: answers each verb in the harness's words, the client's real name and the server's address in
    /// them as the real replies have them, and logs each call (a file per call: a shot and a background RCON run at once).</summary>
    static int FakeHarness(string[] a)
    {
        string Env(string n) => Environment.GetEnvironmentVariable(n) ?? "";
        File.WriteAllText(Path.Combine(Env("FAKE_LOG"), Guid.NewGuid().ToString("N")), Json.Dumps(new JsonArray(a.Select(x => (JsonNode?)x).ToArray())), new UTF8Encoding(false));
        var fail = Env("FAKE_FAIL");
        var name = Env("FAKE_NAME");
        var verb = a[0];
        string Arg(string n) => a[Array.IndexOf(a, n) + 1];
        void Print(string s) => Console.Out.WriteLine(s);
        switch (verb)
        {
            case "shot":
                var res = Env("FAKE_RES");
                var dst = Path.Combine(Arg("-OutDir"), Arg("-Name") + ".png");
                File.Copy(Env("FAKE_PNG"), dst, true);
                Print($"{dst}  ok {res} (from {res}) mean=72,7");
                break;
            case "post":
                if (a.Length > 1 && a[1] == "names-check" && fail == "names-check")
                {
                    Print("REFUSED: 1 private name(s) among the vote's names in the bridge's state - nothing may be shot");
                    return 1;
                }
                Print($"12:00:00 /x: {string.Join(" ", a.Skip(1))} -> 200 ok: heard, /log/{Env("FAKE_TOKEN")}");
                break;
            case "log":
                if (a.Contains("-Pattern"))
                    Print("   501: 11/05 05:10:02 ChangeGameUIState: CSGO_GAME_UI_STATE_LOADINGSCREEN -> CSGO_GAME_UI_STATE_INGAME");
                Print("lines: 500");
                break;
            case "cmd":
                var c = Arg("-Cmd");
                Print(c == "status" ? $"Client: Connected [{Env("FAKE_ADDRESS")}]\n1.41.9.6/14196 10955 insecure public" : $"{c}: done");
                break;
            case "rcon":
                var r = Arg("-Rcon");
                Print($"> {r}");
                var account = Env("FAKE_ACCOUNT");
                Print(r switch
                {
                    "ggd_name" => $"ggd_name: {account} goes by Alice; on the server: {account} is Alice",
                    _ when fail.Length > 0 && r.Contains(fail, StringComparison.Ordinal) => "gghud: no vote is on: vote and browse need one",
                    "css_gghud" => $"gghud: addon 3823993566, nobody frozen, entity 123, world ready, state 1s old: ar_shoots by , vote none, bridge ok, window open for [{name}]",
                    _ when r.StartsWith("css_gghud show", StringComparison.Ordinal) => $"gghud: window on {Words(r)[^1]} for {name}",
                    _ when r.StartsWith("css_gghud press", StringComparison.Ordinal) => $"gghud: pressed {Words(r)[^1]} for {name}",
                    _ when r.StartsWith("css_gghud click", StringComparison.Ordinal) => $"gghud: sent !next for account {Words(r)[2]}; the bridge's answer arrives with the next frame",
                    _ when r.StartsWith("css_gghud centre", StringComparison.Ordinal) => "gghud: \"words\" for 8 s, sent again every 3 s",
                    _ when r.StartsWith("css_gghud release", StringComparison.Ordinal) => "gghud: released: 1 window(s), 0 TAB cursor(s); capture off for everybody next frame",
                    "css_gghud level 0" => "gghud: server level real",
                    _ when r.StartsWith("css_gghud level", StringComparison.Ordinal) => $"gghud: join banner for {name} for 15 s (server level 47)",
                    _ when r.StartsWith("css_gghud content", StringComparison.Ordinal) => $"gghud: account {Words(r)[2]}: HUD v4.6.0",
                    _ when r.StartsWith("css_guide", StringComparison.Ordinal) => $"guide: showing to {name}",
                    _ when r.StartsWith("gg_notice", StringComparison.Ordinal) => "gg_notice: shown for 10 s: words",
                    _ when r.StartsWith("gg_fun reset", StringComparison.Ordinal) => "fun: reset - done",
                    _ when r.StartsWith("gg_fun", StringComparison.Ordinal) => "fun: gravity moon by Bob: sv_gravity 200",
                    _ when r.StartsWith("os_catchup", StringComparison.Ordinal) => $"catchup: showed {name}: words",
                    _ when r.StartsWith("os_mode", StringComparison.Ordinal) => "os_mode: mode deathmatch: kept",
                    _ => $"ok, /log/{Env("FAKE_TOKEN")} rcon_password hunter2",
                });
                break;
        }
        return 0;
    }

    /// <summary>The dry run's commands as lists, to hold them against what the real run's code launched.</summary>
    sealed class RecordingPrinter(string res, string output) : Printer(res, output, new StringWriter())
    {
        public readonly List<List<string>> Calls = [];

        public override string Cmdline(List<string> argv)
        {
            var self = Proc.Self().Count;
            if (argv.Count > self && argv[self] == "bridgepost")
            {
                var rest = argv.Skip(self + 1).ToList();
                if (rest is ["--config", _, ..])
                    rest = rest.Skip(2).ToList();
                Calls.Add(["post", .. rest]);
            }
            else
                Calls.Add(argv.Skip(argv.IndexOf(HarnessPath) + 1).ToList());
            return base.Cmdline(argv);
        }
    }

    static string Norm(IReadOnlyList<string> call) =>
        Json.Dumps(new JsonArray(call.Select((x, i) => (JsonNode?)(i > 0 && call[i - 1] == "-FromLine" ? "<mark>" : i > 0 && call[i - 1] == "-OutDir" ? "<raw>" : x)).ToArray()));

    static List<List<string>> Launched(string calls) =>
        Directory.GetFiles(calls).Select(f => ((JsonArray)Json.Parse(File.ReadAllText(f, Encoding.UTF8))!).Select(x => Json.AsStr(x) ?? "").ToList()).ToList();

    /// <summary>The real run's own code, the Doer and all, with cs2client.ps1 swapped for the fake and the sleeps skipped: what it launches must be what
    /// the dry run printed, its pictures, manifest and index must come out, its log must hold no secret and no name, and a vote that does not open must
    /// stop its chain before a click that would count.</summary>
    static void DeepCheck(Info info, Action<bool, string> check)
    {
        var work = Path.Combine(Work, $"_selfcheck_deep-{Environment.ProcessId}");
        if (Directory.Exists(work))
            Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        const string res = "1280x720";
        var png = Path.Combine(work, "frame.png");
        var solid = new Rgb(1280, 720);
        for (var i = 0; i < solid.Pixels.Length; i += 3)
            (solid.Pixels[i], solid.Pixels[i + 1], solid.Pixels[i + 2]) = (60, 70, 80);
        File.WriteAllBytes(png, Pictures.Png(solid));
        var (realArgv, realPost, realSleep, realOut) = (HarnessArgv, PostArgv, Sleep, Console.Out);
        var calls = Path.Combine(work, "calls");
        Directory.CreateDirectory(calls);
        foreach (var (k, v) in new Dictionary<string, string>
                 {
                     ["FAKE_LOG"] = calls, ["FAKE_PNG"] = png, ["FAKE_RES"] = res, ["FAKE_TOKEN"] = FakeToken, ["FAKE_NAME"] = RealNames[0], ["FAKE_ADDRESS"] = $"{Host}:{Port}",
                     ["FAKE_ACCOUNT"] = Account,
                 })
            Environment.SetEnvironmentVariable(k, v);
        Environment.SetEnvironmentVariable("FAKE_FAIL", null);
        var output = Path.Combine(work, "out");
        try
        {
            var (plan, _) = BuildPlan(null, true);
            var printer = new RecordingPrinter(res, output);
            Walk(printer, plan);
            var expected = printer.Calls.Select(Norm).Order(StringComparer.Ordinal).ToList();
            HarnessArgv = verbArgs => [.. Proc.Self(), "docshots", "fake-harness", .. verbArgs];
            PostArgv = text => [.. Proc.Self(), "docshots", "fake-harness", "post", .. Words(text)];
            Sleep = _ => { };
            Console.SetOut(new StringWriter());
            HashSet<string> failed;
            Dictionary<string, string> doerFailed;
            using (var doer = new Doer(res, output, false))
            {
                failed = Walk(doer, plan);
                doer.WritePictures(plan, info);
                doerFailed = doer.Failed;
            }
            Console.SetOut(realOut);
            check(failed.Count == 0, $"deep check: rows failed against the fake: {string.Join(", ", failed.Order(StringComparer.Ordinal).Take(6))} {string.Join("; ", doerFailed.Take(2).Select(kv => kv.Key + ": " + kv.Value))}");
            var launched = Launched(calls);
            var got = launched.Select(Norm).Order(StringComparer.Ordinal).ToList();
            var firstDifference = got.Zip(expected).Where(p => p.First != p.Second).Select(p => p.First + " / " + p.Second).FirstOrDefault() ?? "in length";
            check(got.SequenceEqual(expected), $"deep check: the real run launched {got.Count} commands, the dry run printed {expected.Count}; first difference: {Py.Slice(firstDifference, 0, 300)}");
            check(launched.All(c => AllVerbs.Contains(c[0]) || c[0] == "post"), "deep check: a verb outside the four and bridgepost");
            check(launched.Where(c => c[0] == "post").All(c => PostVerbs.Contains(c[1])), "deep check: a bridgepost verb that is not one");
            check(launched.Where(c => c[0] == "shot").All(c => c.Contains("-MaxWidth") && c[c.IndexOf("-MaxWidth") + 1] == "0"), "deep check: a shot not at full size");
            check(launched.Count(c => c[0] == "rcon" && c[2].Contains("gg_set_restart", StringComparison.Ordinal)) == 1, "deep check: the restart's button is not clicked exactly once");
            check(!launched.Any(c => c[0] == "rcon" && c[2].Contains("gg_hudver_", StringComparison.Ordinal)), "deep check: the version button was clicked: its real news names the client");
            check(!launched.Any(c => c[0] == "rcon" && Regex.IsMatch(c[2], @"\b(quit|exit|gg_ws_play|mm_extra_addons|mm_client_extra_addons|host_workshop_map)\b")), "deep check: a forbidden command went out");
            var man = Json.Parse(File.ReadAllText(Path.Combine(output, res, "manifest.json"), Encoding.UTF8));
            foreach (var r in plan.Where(e => e.Mode == "shoot").Select(e => e.Row))
            {
                var frames = (Json.At(man, "rows", r.Id, "frames") as JsonArray ?? []).OfType<JsonObject>().ToList();
                check(frames.Count == FrameNames(r).Count, $"deep check: {r.Id}: {frames.Count} frames in the manifest, {FrameNames(r).Count} expected");
                foreach (var f in frames)
                    check(File.Exists(Path.Combine(output, res, Json.Str(f, "file") ?? "")), $"deep check: {Json.Str(f, "file")} is not there");
            }
            check(!Directory.Exists(Path.Combine(output, res, "_raw")), "deep check: the raw frames, with the real name in them, are still there");
            var log = File.ReadAllText(Path.Combine(output, res, "run.log"), Encoding.UTF8);
            check(!RealNameIn(LocalPathsOut(log)) && !log.Contains(Account, StringComparison.Ordinal) && !log.Contains(Host, StringComparison.Ordinal)
                  && !log.Contains("hunter2", StringComparison.Ordinal) && !log.Contains(FakeToken, StringComparison.Ordinal), "deep check: the run's log holds a secret, a name or the address");
            check(File.Exists(Path.Combine(output, "index.html")), "deep check: no index");

            // a ballot whose bridge names the client by a private name: the row stops before its picture
            Directory.Delete(output, true);
            Directory.Delete(calls, true);
            Directory.CreateDirectory(calls);
            Environment.SetEnvironmentVariable("FAKE_FAIL", "names-check");
            (plan, _) = BuildPlan(["vote-started-line"], true);
            Console.SetOut(new StringWriter());
            using (var doer = new Doer(res, output, false))
            {
                failed = Walk(doer, plan);
                doer.WritePictures(plan, info);
            }
            Console.SetOut(realOut);
            launched = Launched(calls);
            check(failed.Contains("vote-started-line"), $"deep check: a refused names-check did not fail its row: {string.Join(", ", failed.Order(StringComparer.Ordinal))}");
            check(!launched.Any(c => c[0] == "shot" && c[c.IndexOf("-Name") + 1].Contains("vote-started-line", StringComparison.Ordinal)), "deep check: a ballot was shot after its names-check refused");
            Environment.SetEnvironmentVariable("FAKE_FAIL", null);

            // a vote that does not open: the chain stops before its clicks count
            Directory.Delete(output, true);
            Directory.Delete(calls, true);
            Directory.CreateDirectory(calls);
            Environment.SetEnvironmentVariable("FAKE_FAIL", $"css_gghud show {Account} vote");
            (plan, _) = BuildPlan(["vote-kept"], true);
            Console.SetOut(new StringWriter());
            using (var doer = new Doer(res, output, false))
            {
                failed = Walk(doer, plan);
                doer.WritePictures(plan, info);
            }
            Console.SetOut(realOut);
            launched = Launched(calls);
            check(failed.Contains("vote-kept"), $"deep check: a vote that did not open did not stop its chain: {string.Join(", ", failed.Order(StringComparer.Ordinal))}");
            check(!launched.Any(c => c[0] == "rcon" && c[2].Contains("gg_m1_play", StringComparison.Ordinal)), "deep check: a card was clicked in a vote that did not open");

            // a run that keeps its raw frames, and --remask on them: no verb, the same files, and without the raw frames it refuses
            Environment.SetEnvironmentVariable("FAKE_FAIL", null);
            Directory.Delete(output, true);
            (plan, _) = BuildPlan(["chat-unknown", "welcome-back"], true);
            Console.SetOut(new StringWriter());
            using (var doer = new Doer(res, output, true))
            {
                failed = Walk(doer, plan);
                doer.WritePictures(plan, info);
            }
            var before = Directory.GetFiles(calls).Length;
            var shot = Path.Combine(output, res, "chat-unknown.png");
            File.Delete(shot);
            var options = new Options(res, output, null, false, false);
            var code = CmdRemask(options, info);
            Console.SetOut(realOut);
            check(failed.Count == 0 && code == 0 && File.Exists(shot) && Directory.GetFiles(calls).Length == before,
                  $"deep check: --remask: failed {string.Join(", ", failed.Order(StringComparer.Ordinal))}, exit {code}, the frame back: {File.Exists(shot)}");
            check(File.Exists(Path.Combine(output, res, "_raw", "chat-unknown.png")), "deep check: --keep-raw kept no raw frame");
            Directory.Delete(Path.Combine(output, res, "_raw"), true);
            Console.SetOut(new StringWriter());
            try
            {
                CmdRemask(options, info);
                check(false, "deep check: --remask did not refuse without raw frames");
            }
            catch (Exit)
            {
            }
            Console.SetOut(realOut);
        }
        finally
        {
            Console.SetOut(realOut);
            (HarnessArgv, PostArgv, Sleep) = (realArgv, realPost, realSleep);
            Environment.SetEnvironmentVariable("FAKE_FAIL", null);
            if (Directory.Exists(work))
                Directory.Delete(work, true);
        }
        check(!Directory.Exists(work), $"{work} is still there");
    }

    /// <summary>The offline checks, with or without Steam on this PC: a made-up account and name stand in for the missing ones while they run.</summary>
    static int CmdSelfCheck(Info info)
    {
        var real = (RealNames, SteamAccountText);
        if (RealNames.Length == 0)
            RealNames = ["zz-real-player"];        // so the name checks below still check something
        if (SteamAccountText.Length == 0)
            SteamAccountText = Account;            // so the gate's other checks are reached
        try
        {
            return SelfCheck(info);
        }
        finally
        {
            (RealNames, SteamAccountText) = real;
        }
    }

    static int SelfCheck(Info info)
    {
        var fails = new List<string>();
        void Check(bool ok, string words)
        {
            if (!ok)
                fails.Add(words);
        }

        // no real name from the bridge: every ballot a row opens is checked (names-check) before its first picture, and the paints over the bridge's
        // names stay under --renamed
        foreach (var r in Rows)
        {
            var kinds = r.Steps.Select(st => (st.Kind, st.Text)).ToList();
            var opened = kinds.IndexOf(("vote_opened", ""));
            if (opened >= 0)
            {
                var firstShot = kinds.FindIndex(k => k.Kind == "shoot") is var f and >= 0 ? f : kinds.Count;
                Check(kinds.Skip(opened).Take(Math.Max(0, firstShot - opened)).Any(k => k == ("post", "names-check")), $"{r.Id} opens a vote and shoots it before a names-check");
            }
        }
        var wasRenamed = Renamed;
        Renamed = true;
        try
        {
            foreach (var r in Rows)
                Check(r.Masks.Where(m => m.Keep).All(m => KeptMasks(r).Contains(m)), $"{r.Id}: a paint over a name the bridge has is dropped under --renamed");
            Check(MVWait1.Keep && MVWait2.Keep, "the waiting lines' paints are not marked keep");
        }
        finally
        {
            Renamed = wasRenamed;
        }

        // the messages, by their own words in the file: Validate has held the rows to them; said again here so the check names them
        var picks = info.Picks;
        foreach (var p in picks)
            Check(Rows.Any(r => r.Pick == p.Name), $"no row for the pick {p.Name}");

        // every resolution, with and without the bridge copy: what would run
        var ready = Rows.Where(r => r.Status == Ready).ToList();
        var output = Path.Combine(Work, "out");
        var estimates = new List<(string Res, int Minutes)>();
        foreach (var res in Resolutions)
            foreach (var copy in new[] { true, false })
            {
                var buf = new StringWriter();
                CmdDryRun([res], null, copy, output, buf);
                var text = buf.ToString();
                foreach (var line in Py.SplitLines(text))
                {
                    var m = Regex.Match(line, @"cs2client\.ps1""? (\w+)");
                    if (m.Success && !line.Contains("start -Connect", StringComparison.Ordinal))
                        Check(AllVerbs.Contains(m.Groups[1].Value), $"{res}: a verb outside {string.Join(", ", AllVerbs)}: {Py.Slice(line, 0, 120)}");
                    if (line.Contains(" shot ", StringComparison.Ordinal))
                        Check(line.Contains("-MaxWidth 0", StringComparison.Ordinal), $"{res}: a shot not at full size: {Py.Slice(line, 0, 120)}");
                    m = Regex.Match(line, @"\sbridgepost(?: --config ""?[^""]+""?)? ([\w-]+)");
                    var head = line.TrimStart();
                    if (m.Success && (head.StartsWith('$') || head.StartsWith("GATE", StringComparison.Ordinal)))
                        Check(copy && PostVerbs.Contains(m.Groups[1].Value), $"{res}: a bridgepost line {(!copy ? "with no --bridge-copy" : "with no such verb")}: {Py.Slice(line, 0, 120)}");
                }
                Check(!RealNameIn(LocalPathsOut(text)), $"{res}: a real name in the dry run, outside this PC's own folder names");
                Check(!text.Contains("MaxWidth 1920", StringComparison.Ordinal), $"{res}: -MaxWidth 1920 in the dry run");
                foreach (var r in ready)
                {
                    if (!((copy || r.Bridge.Length == 0) && (Renamed || !r.Renamed)))
                        continue;
                    var chainOk = true;
                    for (var after = r.After; after.Length > 0;)
                    {
                        var parent = Rows.First(x => x.Id == after);
                        chainOk = chainOk && (copy || parent.Bridge.Length == 0);
                        after = parent.After;
                    }
                    foreach (var name in chainOk ? FrameNames(r) : [])
                        Check(text.Contains($"-Name {name} ", StringComparison.Ordinal), $"{res}: no shot of {name}");
                }
                if (copy)
                {
                    var m = Regex.Match(text, @"about (\d+) min");
                    estimates.Add((res, m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0));
                }
            }

        // every box lands on the frame at every resolution, the right size for it
        foreach (var res in Resolutions)
        {
            var (w, h) = ParseRes(res);
            foreach (var r in Rows)
            {
                var boxes = new List<Px>();
                boxes.AddRange(r.Masks.Where(m => m.Box is not null).Select(m => MaskBox(m, w, h)));
                if (r.Crop is not null)
                    boxes.Add(Place(r.Crop, w, h));
                boxes.Add(CompareRegion(r, w, h));
                boxes.AddRange(VolatileBoxes(r, w, h));
                foreach (var b in boxes)
                    Check(b.X1 > b.X0 && b.Y1 > b.Y0 && b.X0 >= 0 && b.Y0 >= 0 && b.X1 <= w && b.Y1 <= h, $"{res}, {r.Id}: box {b} off the frame");
            }
        }

        // a retake against the committed picture (publish): the same within noise and the volatile boxes, a real change anywhere else
        var rows = Rows.ToDictionary(r => r.Id);
        {
            const int w = 1920, h = 1080;
            var shot = Noise(w, h, 30, 1);
            var again = Pictures.DecodeRgb(Pictures.Jpeg(shot, 85, full: true))!;
            Row helpRow = rows["page-help"], playersRow = rows["page-players"], voteRow = rows["vote-open"];
            var region = CompareRegion(helpRow, w, h);
            Check(SamePicture(again, shot, region, VolatileBoxes(helpRow, w, h)).Same, "same_picture: the same frame through JPEG at quality 85 counts as changed");
            Rgb Painted(Rgb b, Px box, Rgb3? colour = null)
            {
                var im = new Rgb(b.Width, b.Height, (byte[])b.Pixels.Clone());
                Rectangle(im, box.X0, box.Y0, box.X1 - 1, box.Y1 - 1, colour ?? new Rgb3(250, 250, 250));   // a box is half-open, as Place gives it
                return im;
            }
            var word = new Px(900, 500, 960, 512);                     // a word's size, inside the window, outside every volatile box
            Check(!SamePicture(Painted(shot, word), shot, region, VolatileBoxes(helpRow, w, h)).Same, "same_picture: a changed word in the window counts as the same");
            var clock = Place(VIdle, w, h);
            Check(SamePicture(Painted(shot, new Px(clock.X0 + 4, clock.Y0 + 4, clock.X1 - 4, clock.Y1 - 4)), shot, region, VolatileBoxes(helpRow, w, h)).Same,
                  "same_picture: the idle bar's change counts as changed");
            var onfor = Place(VOnFor, w, h);
            Check(SamePicture(Painted(shot, onfor), shot, CompareRegion(playersRow, w, h), VolatileBoxes(playersRow, w, h)).Same
                  && !SamePicture(Painted(shot, onfor), shot, CompareRegion(helpRow, w, h), VolatileBoxes(helpRow, w, h)).Same,
                  "same_picture: a row's own volatile box is not its alone");
            var outside = new Px(10, 10, 200, 200);                    // the radar, outside the window: a frame is compared by its window
            Check(SamePicture(Painted(shot, outside), shot, region, VolatileBoxes(helpRow, w, h)).Same, "same_picture: the world outside the window counts");
            var tiles = Place(VVoteTiles, w, h);
            Check(SamePicture(Painted(shot, new Px(tiles.X0 + 50, tiles.Y0 + 50, tiles.X0 + 400, tiles.Y0 + 200)), shot, CompareRegion(voteRow, w, h), VolatileBoxes(voteRow, w, h)).Same,
                  "same_picture: the ballot's random tiles count");
            Check(!SamePicture(Pictures.Resize(shot, 1280, 720, Pictures.Filter.Bicubic), shot, region).Same, "same_picture: another size counts as the same");
            var cropRow = rows["chat-unknown"];
            var cb = Place(cropRow.Crop!, w, h);
            Rgb look = shot.Crop(cb.X0, cb.Y0, cb.X1, cb.Y1), lookAgain = again.Crop(cb.X0, cb.Y0, cb.X1, cb.Y1);
            Check(SamePicture(lookAgain, look, CompareRegion(cropRow, w, h, crop: true), VolatileBoxes(cropRow, w, h, crop: true)).Same
                  && !SamePicture(Painted(lookAgain, new Px(20, 20, 80, 32)), look, CompareRegion(cropRow, w, h, crop: true), VolatileBoxes(cropRow, w, h, crop: true)).Same,
                  "same_picture: a crop compared whole");

            // a crop over the game: the top line's pill on two different games - its panel alone compared, its width a change, no pill the whole region
            var topRow = rows["top-plain"];
            var otherGame = Noise(w, h, 30, 2);
            Rgb Pill(Rgb b, int half = 230, Px? word = null)
            {
                var im = new Rgb(b.Width, b.Height, (byte[])b.Pixels.Clone());
                Rectangle(im, 960 - half, 150, 960 + half, 186, new Rgb3(37, 43, 45));
                Rectangle(im, 960 - half + 8, 158, 960 - half + 70, 178, new Rgb3(233, 182, 79));    // its tag
                Rectangle(im, 960 - half + 90, 165, 960 + half - 40, 172, new Rgb3(235, 235, 235));  // its words
                if (word is { } wd)
                    Rectangle(im, wd.X0, wd.Y0, wd.X1, wd.Y1, new Rgb3(37, 43, 45));
                return im;
            }
            var (tp, tr, tv) = (PanelOf(topRow, w, h), CompareRegion(topRow, w, h), VolatileBoxes(topRow, w, h));
            Check(!SamePicture(Pill(otherGame), Pill(shot), tr, tv).Same, "same_picture: the game around a pill, without its panel, counts as a change (the old rule)");
            Check(SamePicture(Pill(otherGame), Pill(shot), tr, tv, tp).Same, "same_picture: the same pill over another game counts as changed");
            Check(!SamePicture(Pill(otherGame, word: new Px(1000, 165, 1040, 172)), Pill(shot), tr, tv, tp).Same, "same_picture: a changed word in the pill counts as the same");
            Check(!SamePicture(Pill(otherGame, half: 260), Pill(shot), tr, tv, tp).Same, "same_picture: a wider pill counts as the same");
            Check(!SamePicture(otherGame, Pill(shot), tr, tv, tp).Same, "same_picture: a pill gone counts as the same");
            var tcb = Place(topRow.Crop!, w, h);
            Check(SamePicture(Pill(otherGame).Crop(tcb.X0, tcb.Y0, tcb.X1, tcb.Y1), Pill(shot).Crop(tcb.X0, tcb.Y0, tcb.X1, tcb.Y1), CompareRegion(topRow, w, h, crop: true),
                              VolatileBoxes(topRow, w, h, crop: true), PanelOf(topRow, w, h, crop: true)).Same, "same_picture: a crop file's pill over another game counts as changed");
            Check(PanelOf(rows["page-help"], w, h) is null && PanelOf(rows["chat-unknown"], w, h) is null && PanelOf(rows["why-card"], w, h) is null,
                  "panel_of: a window's crop, a chat crop or a frame without a crop gets a panel");
        }

        // the --only chain: a row's ancestors run first, as setup, and take no picture
        var (plan, notes) = BuildPlan(["vote-kept"], true);
        Check(plan.Select(e => e.Row.Id).SequenceEqual(["vote-open", "vote-limit", "vote-my-vote", "vote-inactivity", "vote-kept"]), $"--only vote-kept plans [{string.Join(", ", plan.Select(e => e.Row.Id))}]");
        Check(plan[^1].Mode == "shoot" && plan.SkipLast(1).All(e => e.Mode == "setup"), "--only: ancestors are not setup only");
        Check(ReviewSet.All(id => rows.TryGetValue(id, out var r) && r.Status == Ready), "REVIEW_SET names a row that is not there or not ready");
        (plan, notes) = BuildPlan(["hud-hold"], true);
        Check(plan.Count == 0 && notes.Count > 0, "--only of a row that is not ready must run nothing and say why");
        (plan, notes) = BuildPlan(null, false);
        Check(!plan.Any(e => e.Row.Bridge.Length > 0) && notes.Any(n => n.Contains("bridge-copy", StringComparison.Ordinal)), "without --bridge-copy no row that writes the bridge's data may run");

        // the gate: it never goes to the network here
        A2sAnswer? NoA2s() => throw new InvalidOperationException("A2S asked without a good lock");
        var good = OwnerLine + "\nsince: x\n";
        var cases = new (string Name, Func<string?> Lock, Func<A2sAnswer?> Ask, bool Want, string Says)[]
        {
            ("no lock", () => null, NoA2s, false, "does not exist"),
            ("someone else's lock", () => "owner: release agent\n", NoA2s, false, "not the harness's"),
            ("an empty lock", () => "", NoA2s, false, "not the harness's"),
            ("no A2S", () => good, () => null, false, "does not answer"),
            ("VAC on", () => good, () => new A2sAnswer("Over Strike 2", StartMap, 0, 1), false, "VAC on"),
            ("another human", () => good, () => new A2sAnswer("Over Strike 2", StartMap, 2, 0), false, "humans"),
            ("a Workshop map", () => good, () => new A2sAnswer("Over Strike 2", "workshop/1/aim", 1, 0), false, "the map is"),
            ("all good", () => good, () => new A2sAnswer("Over Strike 2", StartMap, 1, 0), true, "VAC off"),
        };
        foreach (var (name, lockText, ask, want, says) in cases)
        {
            var (ok, words) = Gate(lockText, ask);
            Check(ok == want && words.Contains(says, StringComparison.Ordinal), $"gate, {name}: {ok}, {words}");
        }
        var account = SteamAccountText;
        SteamAccountText = "";
        var (noSteam, noSteamWords) = Gate(() => good, NoA2s);
        SteamAccountText = account;
        Check(!noSteam && noSteamWords.Contains("Steam", StringComparison.Ordinal), $"gate, no Steam login on this PC: {noSteam}, {noSteamWords}");

        // the real run's own code, against a fake of the harness: no pwsh, no client, no server
        DeepCheck(info, Check);

        // the real run's door itself: no lock, or a foreign lock, or VAC on: it refuses before any verb, and no folder appears
        var (realLock, realA2s, realCandidate) = (ReadLock, AskA2s, CandidateUp);
        var door = Path.Combine(Work, $"_selfcheck_door-{Environment.ProcessId}");
        var stdout = Console.Out;
        try
        {
            var args = new Options("1920x1080", door, null, true, false);
            foreach (var (name, lockText, ask) in new (string, Func<string?>, Func<A2sAnswer?>?)[]
                     {
                         ("no lock", () => null, null), ("a foreign lock", () => "owner: another agent\n", null),
                         ("VAC on", () => OwnerLine + "\n", () => new A2sAnswer("Over Strike 2", StartMap, 1, 1)),
                     })
            {
                (ReadLock, AskA2s) = (lockText, ask ?? NoA2s);
                Console.SetOut(new StringWriter());
                var code = CmdRun(args, info);
                Console.SetOut(stdout);
                Check(code == 2 && !Directory.Exists(door), $"the run's door, {name}: exit {code}, folder there: {Directory.Exists(door)}");
            }
            (ReadLock, AskA2s) = (() => OwnerLine + "\n", () => new A2sAnswer("Over Strike 2", StartMap, 1, 0));
            CandidateUp = () => (false, "REFUSED: the bridge is not the candidate - nothing sent");
            Console.SetOut(new StringWriter());
            var refused = CmdRun(args, info);
            Console.SetOut(stdout);
            Check(refused == 2 && !Directory.Exists(door), $"the run's door, a bridge that is not the candidate: exit {refused}, folder there: {Directory.Exists(door)}");
        }
        finally
        {
            Console.SetOut(stdout);
            (ReadLock, AskA2s, CandidateUp) = (realLock, realA2s, realCandidate);
        }

        // the scrubber, and the pictures at every resolution, on made-up frames in a folder of this program's
        Check(Scrub("http://x/log/abcDEF123456").Contains("<hidden>", StringComparison.Ordinal) && !RealNames.Any(n => RealNameIn(Scrub($"for {n.ToUpperInvariant()}")))
              && !Scrub($"account {Account}").Contains(Account, StringComparison.Ordinal) && !Scrub(Host).Contains(Host, StringComparison.Ordinal), "the scrubber lets something through");
        var work = Path.Combine(Work, $"_selfcheck-{Environment.ProcessId}");
        if (Directory.Exists(work))
            Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        try
        {
            var seed = 2;
            foreach (var res in Resolutions)
            {
                var (w, h) = ParseRes(res);
                var baseFrame = Noise(w, h, 40, seed++);
                var raw = Path.Combine(work, "raw");
                Directory.CreateDirectory(raw);
                foreach (var rid in new[] { "vote-my-vote", "chat-unknown", "welcome-back", "board-default", "vote-pager" })
                {
                    var row = rows[rid];
                    var path = Path.Combine(raw, $"{rid}.png");
                    File.WriteAllBytes(path, Pictures.Png(baseFrame));
                    var (file, crop) = PostFrame(path, row, Path.Combine(work, res));
                    var gotFrame = Pictures.Open(Path.Combine(work, res, file));
                    Check(gotFrame.Width == w && gotFrame.Height == h, $"{res}, {rid}: written at ({gotFrame.Width}, {gotFrame.Height}): nothing may be scaled");
                    if (crop.Length > 0)
                    {
                        var c = Pictures.Open(Path.Combine(work, res, crop));
                        var b = Place(row.Crop!, w, h);
                        Check(c.Width == b.X1 - b.X0 && c.Height == b.Y1 - b.Y0, $"{res}, {rid}: its crop is ({c.Width}, {c.Height})");
                    }
                    Check(Differs(gotFrame, baseFrame, Place(row.Avatar, w, h)), $"{res}, {rid}: the avatar was not touched");
                    foreach (var m in row.Masks.Where(m => m.Box is not null))
                        Check(Differs(gotFrame, baseFrame, MaskBox(m, w, h)), $"{res}, {rid}: a mask did nothing");
                }
                var man = new JsonObject
                {
                    ["res"] = res, ["commit"] = info.Commit, ["content"] = info.Content, ["plugin"] = info.Plugin, ["taken"] = "selfcheck",
                    ["rows"] = new JsonObject
                    {
                        ["vote-my-vote"] = new JsonObject { ["frames"] = new JsonArray(new JsonObject { ["file"] = "vote-my-vote.png", ["crop"] = "vote-my-vote-crop.png" }) },
                        ["chat-unknown"] = new JsonObject { ["frames"] = new JsonArray(new JsonObject { ["file"] = "chat-unknown.png", ["crop"] = "chat-unknown-crop.png" }), ["failed"] = "made up" },
                    },
                };
                File.WriteAllText(Path.Combine(work, res, "manifest.json"), Json.Dumps(man), new UTF8Encoding(false));
            }
            Directory.Delete(Path.Combine(work, "raw"), true);
            WriteIndex(work, info);
            var page = File.ReadAllText(Path.Combine(work, "index.html"), Encoding.UTF8);
            var links = Regex.Matches(page, @"(?:href|src)=""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
            Check(links.Count >= 14, $"the index has {links.Count} links");
            foreach (var link in links)
            {
                Check(!link.Contains(':') && !link.StartsWith('/') && !link.StartsWith('\\'), $"not a relative link: {link}");
                Check(File.Exists(Path.Combine(work, System.Net.WebUtility.HtmlDecode(link))), $"the index links a file that is not there: {link}");
            }
            Check(page.Contains("What is not here, and why", StringComparison.Ordinal) && page.Contains("FAILED at", StringComparison.Ordinal), "the index does not say what is missing");
            Check(!RealNameIn(page), "a real name in the index");
        }
        finally
        {
            if (Directory.Exists(work))
                Directory.Delete(work, true);
        }
        Check(!Directory.Exists(work), $"{work} is still there");

        if (fails.Count > 0)
        {
            Out.Print("SELFCHECK FAILED:");
            foreach (var f in fails)
                Out.Print("  " + f);
            return 1;
        }
        var counts = Statuses.ToDictionary(st => st, st => Rows.Count(r => r.Status == st));
        Out.Print($"SELFCHECK OK at HEAD {info.Commit} (content {info.Content}, plugin {info.Plugin}):");
        Out.Print($"  all {picks.Count} messages of the message list have a row; {counts[Ready]} ready, {counts[Needs]} {Needs}, {counts[Not]} {Not}");
        Out.Print($"  all {info.Named.Count} buttons the rows name are Buttons in the committed {Layout} ({info.Buttons.Count} there)");
        Out.Print($"  --dry-run at {Resolutions.Length} resolutions, with and without --bridge-copy: only the verbs ('rcon', 'cmd', 'shot', 'log') and, with the copy, bridgepost's, every shot -MaxWidth 0, no real name; estimated minutes with the copy: "
                  + string.Join(", ", estimates.Select(e => $"{e.Res} {e.Minutes}")));
        Out.Print("  the gate refuses in all eight bad cases without a packet, and the run's own door with it, no folder made; pictures keep their full size at every resolution; the index holds relative links only");
        Out.Print("  a retake's picture against the committed one: the same through JPEG's noise, a clock, a counter, the world dimmed behind a panel or the game around a crop's panel; changed by a word anywhere else");
        Out.Print("  the real run's own code against a fake harness: it launches exactly what the dry run prints, its log holds no secret or name, a vote that does not open stops its chain, --remask redoes the pictures with no verb");
        Out.Print("  NOT checked, and not checkable offline: anything against a real client or server");
        return 0;
    }
}
