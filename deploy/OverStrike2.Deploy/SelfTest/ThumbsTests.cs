using System.Text.Json.Nodes;

namespace OverStrike2.Deploy;

/// <summary>The map pictures' logic, offline (hud/thumbs.py had no tests; hud/test_thumbs.py now holds its reading of the queues to the same
/// cases as here): the bars a preview has baked in, the preview's own upload, the lists and the queues in their order. The picture work itself
/// was held to Pillow's, pixel for pixel, on real previews.</summary>
static class ThumbsTests
{
    static Rgb Picture(int w, int h, Func<int, int, (byte R, byte G, byte B)> at)
    {
        var im = new Rgb(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = at(x, y);
                var i = (y * w + x) * 3;
                (im.Pixels[i], im.Pixels[i + 1], im.Pixels[i + 2]) = (r, g, b);
            }
        return im;
    }

    public static void Run()
    {
        var check = SelfTest.Check;
        (byte, byte, byte) Scene(int x, int y) => ((byte)(60 + x % 120), (byte)(40 + y % 150), 90);

        var pillarbox = Picture(Thumbs.Width, Thumbs.Height, (x, y) => x < 20 || x >= Thumbs.Width - 20 ? ((byte)5, (byte)5, (byte)5) : Scene(x, y));
        var (filled, box) = Thumbs.Debar(pillarbox);
        check("debar: bars at both sides are found, and the picture inside them is kept whole on a blurred, darkened copy",
              box == (20, 0, Thumbs.Width - 20, Thumbs.Height) && filled.Width == Thumbs.Width && filled.Height == Thumbs.Height);
        var thin = Picture(Thumbs.Width, Thumbs.Height, (x, y) => y < 3 ? ((byte)0, (byte)0, (byte)0) : Scene(x, y));
        check("debar: a dark line thinner than four pixels is no bar", Thumbs.Debar(thin).Box is null);
        var night = Picture(Thumbs.Width, Thumbs.Height, (x, y) => (x + y) % 37 == 0 ? ((byte)40, (byte)40, (byte)40) : ((byte)10, (byte)10, (byte)10));
        check("debar: a dark scene keeps its edges - a real picture has a lighter pixel in every edge line", Thumbs.Debar(night).Box is null);

        var wide = Picture(640, 200, Scene);
        var resized = Pictures.Resize(wide, 336, 105);
        check("resize: to the size asked, either axis alone or both", resized.Width == 336 && resized.Height == 105
              && Pictures.Resize(wide, 640, 100).Width == 640 && Pictures.Resize(wide, 320, 200).Height == 200);
        var flat = Picture(50, 30, (_, _) => (100, 150, 200));
        var blurred = Pictures.GaussianBlur(flat, 14);
        check("blur and brightness: a flat picture stays flat under the blur, and 0.55 of 200 is 110", blurred.Pixels.SequenceEqual(flat.Pixels)
              && Pictures.Brightness(flat, 0.55f).Pixels[2] == 110);

        const string sized = "https://images.steamusercontent.com/ugc/12/AB/?imw=637&imh=358&ima=fit&impolicy=Letterbox&imcolor=%23000000&letterbox=true";
        check("original: Steam's sizing query goes, anything else stays, the fragment goes",
              Thumbs.Original(sized) == "https://images.steamusercontent.com/ugc/12/AB/" && Thumbs.Original("https://example.com/p.jpg?imw=1&v=a b#x") == "https://example.com/p.jpg?v=a+b"
              && Thumbs.Original("ftp://example.com/p.jpg") == "" && Thumbs.Original("") == "");

        var ranked = Thumbs.ByRank(Json.Parse("""[{"id": "3", "rank": 2}, {"id": "1"}, {"id": "2", "rank": "1"}, {"id": "x", "rank": 0}, "not a map", {"id": 4, "rank": 1.7}]"""));
        check("by rank: most popular first, a rank as text or a float read as Python's int() reads it, no rank last, no id no entry",
              ranked.Select(e => Json.PyStr(e["id"])).SequenceEqual(["2", "4", "3", "1"]));
        var (ids, previews) = Thumbs.FromExport(Json.O("""{"maps.json": [{"id": "1", "rank": 1, "preview": "p1"}], "modes": {"wingman": [{"id": "3"}], "deathmatch": [{"id": "2", "preview": "p2"}]}}"""));
        check("export: Arms Race's list first, then every mode's in their names' order, with their previews", ids.SequenceEqual(["1", "2", "3"]) && previews.Count == 2 && previews["2"] == "p2");
        var (v2, v2Exit, v2Said) = SelfTest.Capture(() => Thumbs.FromQueues(Json.O("""{"version": 2, "ballot": ["9"], "stock": {"b": ["7", 8], "a": ["6", "x"], "c": {"no": "lane"}}}""")));
        check("queues: the open ballot's first, then every lane of the stock in its name's order, numbers or text; a lane of another shape is empty; nothing said",
              v2 is { } got && got.SequenceEqual(["9", "6", "7", "8"]) && v2Exit is null && v2Said == "");
        foreach (var (name, text) in new[] { ("an older bridge's (version 1)", """{"version": 1, "requested": ["9"], "random": {"a": ["6"]}}"""),
                                             ("one with no version", """{"stock": {"a": ["6"]}}"""), ("not an object", """["9"]""") })
        {
            var (queued, exit, said) = SelfTest.Capture(() => Thumbs.FromQueues(Json.Parse(text)));
            check($"queues: {name} is no queues - one line says so, and the run goes on with no head start",
                  queued is { Count: 0 } && exit is null && Py.SplitLines(said).Count == 1 && said.Contains("not queues.json version 2", StringComparison.Ordinal));
        }
    }
}
