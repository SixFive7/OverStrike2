using System.Text;
using System.Text.Json.Nodes;

namespace OverStrike2.Deploy;

/// <summary>tools/client/docshots/test_publish.py, ported check for check: publish's retake rule, on made-up frames in a scratch copy of the docs - a first
/// publish writes every picture, a retake of the same frames keeps every committed byte, a retake with one changed word writes that one picture alone, a
/// clock that ticked changes nothing, the gallery links its pictures and carries none, and a picture no longer used goes with its allow.txt line. No
/// server, no client, no git.</summary>
static class PublishTests
{
    public static void Run()
    {
        var check = SelfTest.Check;
        DocShots.Init(null);
        var tmp = SelfTest.Scratch("publish-sim-");
        try
        {
            string repo = Path.Combine(tmp, "repo"), res = Path.Combine(tmp, "run", "1920x1080");
            Directory.CreateDirectory(Path.Combine(repo, "tools", "scan"));
            Directory.CreateDirectory(Path.Combine(repo, "docs", "hud"));
            Directory.CreateDirectory(res);
            File.WriteAllText(Path.Combine(repo, "README.md"), "# x\n<!-- hud-index:begin -->\n| HELP, Help | where | page |\n<!-- hud-index:end -->\n");
            File.WriteAllText(Path.Combine(repo, "tools", "scan", "allow.txt"), "# allow\nbinary docs/hud/img/old.jpg 00  # a picture of a run before\n");
            var rows = DocShots.Rows.ToDictionary(r => r.Id);
            const int W = 1920, H = 1080;
            var baseFrame = DocShots.Noise(W, H, 25, 7);   // the game world behind: one made-up picture for every frame of this test

            // a made-up frame: noise, the same for a seed; a word painted in the window, or the idle bar's clock painted at a length
            Rgb Frame(int seed, Px? word = null, int? clock = null)
            {
                var rnd = new Random(seed);
                var im = new Rgb(W, H, (byte[])baseFrame.Pixels.Clone());
                for (var i = 0; i < 40; i++)   // some "HUD" for the frame to be about
                {
                    int x = rnd.Next(360, 1500), y = rnd.Next(150, 900);
                    DocShots.Rectangle(im, x, y, x + 60, y + 12, DocShots.Gold);
                }
                if (word is { } w)
                    DocShots.Rectangle(im, w.X0, w.Y0, w.X1, w.Y1, new Rgb3(250, 250, 250));
                if (clock is int c)
                {
                    var b = DocShots.Place(DocShots.VIdle, W, H);
                    DocShots.Rectangle(im, b.X0 + 30, b.Y0 + 8, b.X0 + 30 + c, b.Y0 + 16, DocShots.Gold);
                }
                return im;
            }

            // one docshots run made of these frames (row id -> picture, with a crop where the row has one), then publish on it
            (int Code, string Out) RunPublish(Dictionary<string, Rgb> frames, params string[] extra)
            {
                Directory.Delete(res, true);
                Directory.CreateDirectory(res);
                var manRows = new JsonObject();
                var reviewed = new List<string>();
                foreach (var (rid, im) in frames)
                {
                    File.WriteAllBytes(Path.Combine(res, $"{rid}.png"), Pictures.Png(im));
                    var crop = "";
                    if (rows[rid].Crop is { } cb)
                    {
                        var p = DocShots.Place(cb, W, H);
                        File.WriteAllBytes(Path.Combine(res, $"{rid}-crop.png"), Pictures.Png(im.Crop(p.X0, p.Y0, p.X1, p.Y1)));
                        crop = $"{rid}-crop.png";
                    }
                    manRows[rid] = new JsonObject { ["frames"] = new JsonArray(new JsonObject { ["file"] = $"{rid}.png", ["crop"] = crop }) };
                    reviewed.Add($"{rid}.png");
                    if (crop.Length > 0)
                        reviewed.Add(crop);
                }
                File.WriteAllText(Path.Combine(res, "manifest.json"), Json.Dumps(new JsonObject
                {
                    ["res"] = "1920x1080", ["commit"] = "test", ["content"] = "v0", ["plugin"] = "0", ["taken"] = "test", ["rows"] = manRows,
                }));
                File.WriteAllText(Path.Combine(res, "reviewed.txt"), string.Join("\n", reviewed));
                var (code, exit, output) = SelfTest.Capture(() => Publish.Command(["--out", Path.Combine(tmp, "run"), "--repo", repo, .. extra]));
                return (exit ?? code, output);
            }

            Dictionary<string, string> Hashes()
            {
                var img = Path.Combine(repo, "docs", "hud", "img");
                return Directory.Exists(img)
                    ? Directory.GetFiles(img, "*.jpg", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(repo, p).Replace('\\', '/'), Repo.Sha256)
                    : [];
            }

            string[] picked = ["page-help", "join-tagline", "vote-open"];   // a page frame, a message with a crop on a page, a big vote window
            var (code1, _) = RunPublish(picked.Select((rid, i) => (rid, i)).ToDictionary(x => x.rid, x => Frame(x.i)));
            var h1 = Hashes();
            check($"a first publish writes the pages' pictures and the gallery's: [{string.Join(", ", h1.Keys.Order(StringComparer.Ordinal))}]",
                  code1 == 0 && h1.ContainsKey("docs/hud/img/page-help.jpg") && h1.ContainsKey("docs/hud/img/gallery/join-tagline-look.jpg") && h1.ContainsKey("docs/hud/img/gallery/join-tagline-where.jpg"));
            var gallery = File.ReadAllText(Path.Combine(repo, "docs", "hud", "messages.html"), Encoding.UTF8);
            check("the gallery links its pictures by relative path and carries none",
                  !gallery.Contains("data:", StringComparison.Ordinal) && gallery.Contains("src=\"img/gallery/join-tagline-look.jpg\"", StringComparison.Ordinal)
                  && gallery.Contains("src=\"img/gallery/join-tagline-where.jpg\"", StringComparison.Ordinal));
            var allow = File.ReadAllText(Path.Combine(repo, "tools", "scan", "allow.txt"), Encoding.UTF8);
            check("allow.txt holds every picture by its hash, and no more the one that is gone",
                  h1.All(kv => allow.Contains($"binary {kv.Key} {kv.Value}", StringComparison.Ordinal)) && !allow.Contains("old.jpg", StringComparison.Ordinal));

            var (code2, out2) = RunPublish(picked.Select((rid, i) => (rid, i)).ToDictionary(x => x.rid, x => Frame(x.i, clock: x.rid == "page-help" ? 40 : null)));
            var lines2 = Py.SplitLines(Py.Strip(out2));
            check($"a retake of the same frames, a clock ticked: every committed byte kept ({Py.Slice(lines2.Count > 1 ? lines2[^2] : "", 0, 120)})",
                  code2 == 0 && Hashes().OrderBy(kv => kv.Key).SequenceEqual(h1.OrderBy(kv => kv.Key)) && out2.Contains("0 written anew, 0 new", StringComparison.Ordinal));

            var word = new Px(900, 500, 960, 512);
            var (code3, _) = RunPublish(picked.Select((rid, i) => (rid, i)).ToDictionary(x => x.rid, x => Frame(x.i, word: x.rid == "page-help" ? word : null)));
            var h3 = Hashes();
            check("a changed word: that picture written anew, every other one kept",
                  code3 == 0 && h3["docs/hud/img/page-help.jpg"] != h1["docs/hud/img/page-help.jpg"] && h1.Keys.Where(k => k != "docs/hud/img/page-help.jpg").All(k => h3[k] == h1[k]));

            var (code4, _) = RunPublish(picked.Select((rid, i) => (rid, i)).Where(x => x.rid != "vote-open").ToDictionary(x => x.rid, x => Frame(x.i)));
            var h4 = Hashes();
            check("a picture no page uses any more is removed, its allow.txt line with it",
                  code4 == 0 && !h4.ContainsKey("docs/hud/img/vote-open.jpg") && !File.ReadAllText(Path.Combine(repo, "tools", "scan", "allow.txt")).Contains("vote-open.jpg", StringComparison.Ordinal));

            var (code5, out5) = RunPublish(picked.Select((rid, i) => (rid, i)).ToDictionary(x => x.rid, x => Frame(x.i)), "--all");
            check("--all writes every picture anew", code5 == 0 && out5.Contains("0 kept as committed", StringComparison.Ordinal));
        }
        finally
        {
            SelfTest.Remove(tmp);
        }
    }
}
