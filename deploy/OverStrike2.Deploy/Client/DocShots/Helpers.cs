using System.Text;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace OverStrike2.Deploy;

/// <summary>The docshots folder's four small Python helpers, ported as verbs: docname.py (name), merge_fix.py (merge), maskview.py (maskview) and
/// review_sheet.py (sheets).</summary>
static partial class DocShots
{
    const string DocNameUsage = """
The throwaway OsDocName (tools/client/docshots/OsDocName) on the test copy: copied to /tmp/osdocname in the game container and loaded, and the client named;
refuses unless the game server's container is the test copy and the harness holds the server lock. unload removes it. The pictures are public,
so the client plays under a test name: docshots --renamed then needs no mask for a name.

    os2-deploy docshots name load [<account> <name>]     build, copy, load, and name that account (default: this PC's Steam login, Alice)
    os2-deploy docshots name status                      what osd_name says
    os2-deploy docshots name unload                      unload and remove /tmp/osdocname
""";

    /// <summary>docname.py: OsDocName on the test copy, under the harness's lock.</summary>
    static int DocName(string[] argv, string? config)
    {
        var verb = argv.Length > 0 ? argv[0] : "status";
        if (verb is not ("load" or "status" or "unload"))
            throw Out.Stop(DocNameUsage);
        var h = new Harness(config);
        h.NeedLock();
        var game = h.Game;
        if (!TestCopy.IsTest(h.Inspect(game)))
            throw Out.Stop($"REFUSED: {game} is not the test copy");
        string Rcon(string cmd) => h.Rcon([cmd]);
        if (verb == "load")
        {
            var (account, name) = argv.Length > 2 ? (argv[1], argv[2]) : (Harness.SteamAccount() is var a && a != 0 ? a.ToString(System.Globalization.CultureInfo.InvariantCulture) : "", Me);
            if (!Py.IsDigits(account) || Py.Strip(name).Length == 0)
                throw Out.Stop("load: no account - Steam names no logged-in account on this PC; give <account> <name>");
            var project = Path.Combine(Here, "OsDocName");
            if (Proc.Run(["dotnet", "build", "-c", "Release", "-nologo", "-v", "q"], cwd: project) != 0)
                throw new Exit(1, "dotnet build of OsDocName failed");
            var data = File.ReadAllBytes(Path.Combine(project, "bin", "Release", "net10.0", "OsDocName.dll"));
            Out.Print($"copied {data.Length} bytes, HTTP {h.Upload(new() { ["OsDocName.dll"] = data }, folder: "osdocname")}");
            Out.Print(Py.Slice(Py.Strip(Rcon("css_plugins load /tmp/osdocname/OsDocName.dll")), 0, 300));
            Thread.Sleep(1000);
            Out.Print(Py.Repr(Py.SplitLines(Rcon("css_plugins list")).Where(l => l.Contains("LOADED", StringComparison.Ordinal)).Select(Py.Strip).ToList()));
            Out.Print(Py.Slice(Py.Strip(Rcon($"osd_name {account} {name}")), 0, 300));
        }
        else if (verb == "status")
            Out.Print(Py.Slice(Py.Strip(Rcon("osd_name")), 0, 300));
        else
        {
            Out.Print(Py.Slice(Py.Strip(Rcon("css_plugins unload OsDocName")), 0, 300));
            var gone = Py.Strip(h.ExecInGame(["rm", "-rf", "/tmp/osdocname"]));
            Out.Print(gone.Length > 0 ? gone : "removed /tmp/osdocname");
        }
        return 0;
    }

    /// <summary>merge_fix.py: a retake's rows into the main run - their raw frames replace the main run's for those rows, the manifest takes their frame
    /// list. Then --remask redoes the pictures. merge &lt;retake out&gt; &lt;main out&gt; &lt;res&gt; [row,row...] (default: every row the retake shot).</summary>
    static int Merge(string[] argv)
    {
        if (argv.Length < 3)
            throw new Exit(2, "usage: os2-deploy docshots merge <retake out> <main out> <res> [row,row...]");
        string fix = Path.Combine(argv[0], argv[2]), main = Path.Combine(argv[1], argv[2]);
        var only = argv.Length > 3 ? argv[3].Split(',').ToHashSet() : null;
        var fm = Json.Parse(File.ReadAllText(Path.Combine(fix, "manifest.json"), Encoding.UTF8));
        var mm = (JsonObject)Json.Parse(File.ReadAllText(Path.Combine(main, "manifest.json"), Encoding.UTF8))!;
        var mainRows = (JsonObject)mm["rows"]!;
        foreach (var (rid, got) in Json.At(fm, "rows") as JsonObject ?? [])
        {
            var frames = Json.At(got, "frames") as JsonArray;
            if (frames is not { Count: > 0 } || (only is not null && !only.Contains(rid)))
                continue;
            foreach (var old in (Json.At(mainRows, rid, "frames") as JsonArray ?? []).OfType<JsonObject>())
            {
                foreach (var k in new[] { "file", "crop" })
                    if (Json.Str(old, k) is { Length: > 0 } f)
                        File.Delete(Path.Combine(main, f));
                File.Delete(Path.Combine(main, "_raw", Json.Str(old, "file") ?? ""));
            }
            foreach (var f in frames.OfType<JsonObject>())
                File.Copy(Path.Combine(fix, "_raw", Json.Str(f, "file") ?? ""), Path.Combine(main, "_raw", Json.Str(f, "file") ?? ""), true);
            mainRows[rid] = new JsonObject { ["frames"] = frames.DeepClone() };
            Out.Print($"{rid} {Py.Repr(frames.OfType<JsonObject>().Select(f => Json.Str(f, "file")).ToList())}");
        }
        File.WriteAllText(Path.Combine(main, "manifest.json"), Json.Dumps(mm, indent: 1).Replace("\n", Environment.NewLine, StringComparison.Ordinal), new UTF8Encoding(false));
        return 0;
    }

    /// <summary>maskview.py: the named boxes of the table drawn on a frame, offline, and a crop or a smaller copy written to look at.
    /// maskview &lt;frame.png&gt; &lt;out.png|jpg&gt; &lt;x0,y0,x1,y1 crop or 'small'&gt; NAME [NAME...] - NAME: a Mask or Box constant of docshots.py.</summary>
    static int MaskView(string[] argv)
    {
        if (argv.Length < 4)
            throw new Exit(2, "usage: os2-deploy docshots maskview <frame.png> <out.png|jpg> <x0,y0,x1,y1 crop or 'small'> NAME [NAME...]");
        var (frame, output, region) = (argv[0], argv[1], argv[2]);
        var im = Pictures.Open(frame);
        int w = im.Width, h = im.Height;
        Rgb3[] colours = [new(0, 255, 0), new(255, 0, 255), new(0, 200, 255), new(255, 255, 0), new(255, 120, 0), new(255, 60, 60), new(160, 120, 255)];
        for (var i = 0; i < argv.Length - 3; i++)
        {
            var name = argv[i + 3];
            if (!Named.TryGetValue(name, out var obj))
                throw Out.Stop($"no Mask or Box named {name}");
            var box = obj is Mask m ? MaskBox(m, w, h) : Place((Box)obj, w, h);
            var c = colours[i % colours.Length];
            Rectangle(im, box.X0, box.Y0, box.X1, box.Y1, c, fill: false, width: 2);
            Out.Print($"{name} {Py.Tuple(c.R, c.G, c.B)} {box}");
        }
        if (region == "small")
            im = Pictures.Resize(im, w / 2, h / 2, Pictures.Filter.Bicubic);
        else
        {
            var v = region.Split(',').Select(x => int.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            im = im.Crop(v[0], v[1], v[2], v[3]);
        }
        Pictures.Save(im, output, quality: 85);
        return 0;
    }

    /// <summary>review_sheet.py: contact sheets of a run's masked pictures, for review by eye: six frames a sheet (3 x 2, each 640 px wide, its file name on
    /// it), in the manifest's order. sheets &lt;out&gt;/&lt;res&gt; [--only a,b] writes &lt;out&gt;/&lt;res&gt;/_review/sheet-NN.jpg.</summary>
    static int Sheets(string[] argv)
    {
        if (argv.Length < 1)
            throw new Exit(2, "usage: os2-deploy docshots sheets <out>/<res> [--only a,b]");
        var resDir = argv[0];
        var at = Array.IndexOf(argv, "--only");
        var only = at >= 0 && at + 1 < argv.Length ? argv[at + 1].Split(',').ToHashSet() : null;
        var man = Json.Parse(File.ReadAllText(Path.Combine(resDir, "manifest.json"), Encoding.UTF8));
        var files = (Json.At(man, "rows") as JsonObject ?? []).Where(kv => only is null || only.Contains(kv.Key))
            .SelectMany(kv => (Json.At(kv.Value, "frames") as JsonArray ?? []).OfType<JsonObject>()).Select(f => Json.Str(f, "file") ?? "").ToList();
        var output = Path.Combine(resDir, "_review");
        Directory.CreateDirectory(output);
        foreach (var old in Directory.GetFiles(output, "sheet-*.jpg"))
            File.Delete(old);
        const int W = 640, H = 360;
        using var face = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
        using var font = new SKFont(face, 18) { Edging = SKFontEdging.Antialias };
        using var paint = new SKPaint { Color = new SKColor(255, 212, 126), IsAntialias = true };
        for (var n = 0; n < files.Count; n += 6)
        {
            var sheet = new Rgb(3 * W, 2 * (H + 24));
            for (var i = 0; i < sheet.Pixels.Length; i++)
                sheet.Pixels[i] = 20;
            var names = files.Skip(n).Take(6).ToList();
            for (var i = 0; i < names.Count; i++)
            {
                var im = Pictures.Open(Path.Combine(resDir, names[i]));
                var small = Pictures.Resize(im, W, Py.Round(im.Height * W / (double)im.Width));
                int x = i % 3 * W, y = i / 3 * (H + 24);
                sheet.Paste(small.Crop(0, 0, W, Math.Min(H, small.Height)), x, y + 24);
            }
            using (var bitmap = Pictures.ToBitmap(sheet))
            {
                using (var canvas = new SKCanvas(bitmap))
                    for (var i = 0; i < names.Count; i++)
                        canvas.DrawText(names[i], i % 3 * W + 4, i / 3 * (H + 24) + 2 - font.Metrics.Ascent, SKTextAlign.Left, font, paint);
                sheet = Pictures.FromBitmap(bitmap);
            }
            var path = Path.Combine(output, $"sheet-{n / 6 + 1:00}.jpg");
            File.WriteAllBytes(path, Pictures.Jpeg(sheet, 85));
            Out.Print($"{Path.GetFileName(path)} :  {string.Join(", ", names)}");
        }
        return 0;
    }

    /// <summary>Validate, for publish: the same checks, the same Info.</summary>
    public static Info ValidateForPublish() => Validate();
}
