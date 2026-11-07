using SkiaSharp;

namespace OverStrike2.Deploy;

/// <summary>A box in a picture's pixels, half-open: x0 &lt;= x &lt; x1, y0 &lt;= y &lt; y1, as docshots.py's place() gives one.</summary>
readonly record struct Px(int X0, int Y0, int X1, int Y1)
{
    public override string ToString() => Py.Tuple(X0, Y0, X1, Y1);
}

/// <summary>docshots.py's pictures: where a box lands at a resolution, the masks (Pillow's own BOX and BICUBIC resample and Gaussian blur, number for
/// number), the comparison of a retake with the committed picture, and a frame written as it may be published. The one thing not number for number
/// is a painted test name: SkiaSharp draws it, in Windows' Bahnschrift as Pillow does, but its glyphs are not FreeType's.</summary>
static partial class DocShots
{
    /// <summary>Anchors c, l, r as Box says; v: the big vote window, centred both ways, its ui-scale 92 % at 16:10 and 80 % at 4:3.</summary>
    public static Px Place(Box b, int w, int h)
    {
        var s = h / 1080.0;
        Func<double, double> fy = y => y * s;
        Func<double, double> fx;
        switch (b.Anchor)
        {
            case "c":
                fx = x => w / 2.0 + (x - 960) * s;
                break;
            case "l":
                fx = x => x * s;
                break;
            case "r":
                fx = x => w - (1920 - x) * s;
                break;
            default:
                var k = Math.Abs((double)w / h - 1.6) < 0.01 ? 0.92 : Math.Abs((double)w / h - 4.0 / 3) < 0.01 ? 0.8 : 1.0;
                fx = x => w / 2.0 + (x - 960) * s * k;
                fy = y => h / 2.0 + (y - 540) * s * k;
                break;
        }
        return new Px(Math.Max(0, Py.Round(fx(b.X0))), Math.Max(0, Py.Round(fy(b.Y0))), Math.Min(w, Py.Round(fx(b.X1))), Math.Min(h, Py.Round(fy(b.Y1))));
    }

    public static (int W, int H) ParseRes(string res)
    {
        var m = System.Text.RegularExpressions.Regex.Match(res, @"^(\d+)x(\d+)$");
        if (!m.Success)
            throw Out.Stop($"--res takes WIDTHxHEIGHT, not {Py.Repr(res)}");
        return (int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    // at about 130 px of words from the line's start (measured on the ballot's label); the rest: 0
    static readonly Dictionary<(int, int), int> WordDrift = new() { [(1280, 720)] = -10, [(1680, 1050)] = -4, [(1600, 1200)] = -5, [(2560, 1080)] = -3 };

    /// <summary>Where a mask goes on this frame. A drifted left edge only ever widens the box to the left of the 1080p place, so the name stays inside.</summary>
    public static Px MaskBox(Mask m, int w, int h)
    {
        var p = Place(m.Box!, w, h);
        if (m.Drift)
            p = p with { X0 = Math.Max(0, p.X0 + Py.Round(WordDrift.GetValueOrDefault((w, h), 0) * h / 1080.0)) };
        return p;
    }

    /// <summary>Where chat is: the left of the screen, up to the window's edge while the window is open, which covers the rest of it.</summary>
    public static Px ChatZone(int w, int h, Win window, double y1 = 975)
    {
        var s = h / 1080.0;
        var right = 620 * s;
        if (window == Win.Vote)
            right = Math.Min(right, Place(new Box(80, 0, 81, 1, "v"), w, h).X0);
        else if (window == Win.Window)
            right = Math.Min(right, w / 2.0 - 620 * s);
        return new Px(0, Py.Round(740 * s), Math.Max(1, Py.Round(right)), Py.Round(y1 * s));
    }

    /// <summary>A box blurred past reading: a BOX reduction to an eighth, a BICUBIC enlargement back, a Gaussian blur of max(2, 3 s).</summary>
    public static void Smear(Rgb im, Px box, double s)
    {
        if (box.X1 <= box.X0 || box.Y1 <= box.Y0)
            return;
        var part = im.Crop(box.X0, box.Y0, box.X1, box.Y1);
        int w = part.Width, h = part.Height;
        var small = Pictures.Resize(part, Math.Max(1, w / 8), Math.Max(1, h / 8), Pictures.Filter.Box);
        var radius = Math.Max(2, 3 * s);
        im.Paste(Pictures.GaussianBlur(Pictures.Resize(small, w, h, Pictures.Filter.Bicubic), (float)radius), box.X0, box.Y0);
    }

    /// <summary>What was under the words, as near as can be: each column from the pixel above the box to the pixel below it, so the line's other words
    /// never smear in.</summary>
    public static void FillBox(Rgb im, Px box)
    {
        var px = im.Pixels;
        var h = box.Y1 - box.Y0;
        int top = Math.Max(0, box.Y0 - 1), bottom = Math.Min(im.Height - 1, box.Y1);
        for (var x = box.X0; x < box.X1; x++)
        {
            int ia = (top * im.Width + x) * 3, ib = (bottom * im.Width + x) * 3;
            int a0 = px[ia], a1 = px[ia + 1], a2 = px[ia + 2], b0 = px[ib], b1 = px[ib + 1], b2 = px[ib + 2];
            for (var y = box.Y0; y < box.Y1; y++)
            {
                var f = (double)(y - box.Y0 + 1) / (h + 1);
                var o = (y * im.Width + x) * 3;
                px[o] = (byte)Py.Round(a0 + (b0 - a0) * f);
                px[o + 1] = (byte)Py.Round(a1 + (b1 - a1) * f);
                px[o + 2] = (byte)Py.Round(a2 + (b2 - a2) * f);
            }
        }
    }

    static readonly Dictionary<(int, bool), SKFont> Fonts = [];

    /// <summary>Windows' Bahnschrift, the nearest stand-in for the HUD's Stratum2, in its bold or regular face; the default face without it.</summary>
    static SKFont FontFor(double size, bool bold)
    {
        var key = (Math.Max(6, Py.Round(size)), bold);
        if (!Fonts.TryGetValue(key, out var font))
        {
            var face = SKTypeface.FromFamilyName("Bahnschrift", bold ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.Default;
            font = new SKFont(face, key.Item1) { Subpixel = true, Edging = SKFontEdging.Antialias };
            Fonts[key] = font;
        }
        return font;
    }

    /// <summary>A flat fill of what was under the box, and the test name in the HUD's look over it, letter by letter at the mask's spacing, its middle
    /// at the box's middle (Pillow's anchor lm).</summary>
    public static void PaintName(Rgb im, Px box, Mask mask, double s)
    {
        if (box.X1 <= box.X0 || box.Y1 <= box.Y0)
            return;
        FillBox(im, box);
        var font = FontFor(mask.Size * s, mask.Bold);
        var spacing = mask.Spacing * s;
        var letters = System.Globalization.StringInfo.GetTextElementEnumerator(mask.Text);
        var chars = new List<string>();
        while (letters.MoveNext())
            chars.Add((string)letters.Current);
        var width = chars.Sum(c => font.MeasureText(c) + spacing);
        var x = mask.Align == "l" ? box.X0 + Math.Max(1, 3 * s) : box.X0 + (box.X1 - box.X0 - width) / 2;
        var y = box.Y0 + (box.Y1 - box.Y0) / 2.0;
        var metrics = font.Metrics;
        var baseline = y - (metrics.Ascent + metrics.Descent) / 2;
        using var bitmap = Pictures.ToBitmap(im);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = new SKColor((byte)mask.Color.R, (byte)mask.Color.G, (byte)mask.Color.B), IsAntialias = true })
        {
            foreach (var c in chars)
            {
                canvas.DrawText(c, (float)x, (float)baseline, SKTextAlign.Left, font, paint);
                x += font.MeasureText(c) + spacing;
            }
        }
        var drawn = Pictures.FromBitmap(bitmap);
        Array.Copy(drawn.Pixels, im.Pixels, im.Pixels.Length);
    }

    /// <summary>The part of a picture a retake is compared on, in its pixels: a crop file whole; a frame by its window, its big vote window, its crop,
    /// or whole when it has none of these.</summary>
    public static Px CompareRegion(Row row, int w, int h, bool crop = false)
    {
        if (crop)
        {
            var b = row.Crop is not null ? Place(row.Crop, w, h) : new Px(0, 0, w, h);
            return new Px(0, 0, b.X1 - b.X0, b.Y1 - b.Y0);
        }
        if (row.Win == Win.Vote)
            return Place(VoteWindow, w, h);
        if (row.Win == Win.Window)
            return Place(Window, w, h);
        return row.Crop is not null ? Place(row.Crop, w, h) : new Px(0, 0, w, h);
    }

    /// <summary>What differs on every shot in a picture of this row, in its pixels: its window kind's clocks and counters, and the row's own boxes. In a
    /// crop file the boxes are moved to the crop's corner and cut to it.</summary>
    public static List<Px> VolatileBoxes(Row row, int w, int h, bool crop = false)
    {
        var boxes = new List<Box>(row.Volatile);
        if (row.Win == Win.Vote)
            boxes.AddRange([VVoteMapCount, VVoteSubtab, CClocks, VVoteTiles]);
        else if (row.Win == Win.Window)
            boxes.AddRange([VMapCount, VIdle]);
        var output = boxes.Select(b => Place(b, w, h)).ToList();
        if (crop && row.Crop is not null)
        {
            var c = Place(row.Crop, w, h);
            output = output.Select(b => new Px(Math.Max(b.X0, c.X0) - c.X0, Math.Max(b.Y0, c.Y0) - c.Y0, Math.Min(b.X1, c.X1) - c.X0, Math.Min(b.Y1, c.Y1) - c.Y0)).ToList();
        }
        return output.Where(b => b.X1 > b.X0 && b.Y1 > b.Y0).ToList();
    }

    const int SameLevel = 48, SameBlock = 8;   // a pixel differs by more than 48 of 255 in a channel; an 8x8 block counts when 8 of its 64 pixels do

    /// <summary>Whether two pictures show the same within region, all but the volatile boxes: (true, words) or (false, where they differ). JPEG's noise
    /// and a translucent panel over a game world that differs behind it stay under SameLevel; a changed word, style or layout moves whole blocks.</summary>
    public static (bool Same, string Words) SamePicture(Rgb newer, Rgb old, Px region, IReadOnlyList<Px>? volatileBoxes = null)
    {
        if (newer.Width != old.Width || newer.Height != old.Height)
            return (false, $"size {newer.Width}x{newer.Height}, committed {old.Width}x{old.Height}");
        int rw = region.X1 - region.X0, rh = region.Y1 - region.Y0;
        var mask = new byte[rw * rh];
        for (var y = 0; y < rh; y++)
            for (var x = 0; x < rw; x++)
            {
                int ix = x + region.X0, iy = y + region.Y0;
                if (ix < 0 || iy < 0 || ix >= newer.Width || iy >= newer.Height)
                    continue;
                var p = (iy * newer.Width + ix) * 3;
                var d = Math.Max(Math.Max(Math.Abs(newer.Pixels[p] - old.Pixels[p]), Math.Abs(newer.Pixels[p + 1] - old.Pixels[p + 1])), Math.Abs(newer.Pixels[p + 2] - old.Pixels[p + 2]));
                mask[y * rw + x] = d > SameLevel ? (byte)255 : (byte)0;
            }
        foreach (var v in volatileBoxes ?? [])
            FillGray(mask, rw, rh, v.X0 - region.X0, v.Y0 - region.Y0, v.X1 - region.X0 - 1, v.Y1 - region.Y0 - 1, 0);
        int bw = Math.Max(1, rw / SameBlock), bh = Math.Max(1, rh / SameBlock);
        var blocks = Pictures.Resample(mask, rw, rh, 1, bw, bh, Pictures.Filter.Box);   // each block's share of differing pixels, 0-255
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < bh; y++)
            for (var x = 0; x < bw; x++)
                if (blocks[y * bw + x] * 64 >= SameBlock * 255)
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
        if (maxX < 0)
            return (true, "the same, all but what differs on every shot");
        return (false, $"differs at {region.X0 + minX * SameBlock},{region.Y0 + minY * SameBlock} - {region.X0 + (maxX + 1) * SameBlock},{region.Y0 + (maxY + 1) * SameBlock}");
    }

    /// <summary>Pillow's rectangle fill on a one-band picture: x0..x1 and y0..y1 inclusive, cut to the picture.</summary>
    static void FillGray(byte[] g, int w, int h, int x0, int y0, int x1, int y1, byte value)
    {
        for (var y = Math.Max(0, y0); y <= Math.Min(h - 1, y1); y++)
            for (var x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++)
                g[y * w + x] = value;
    }

    /// <summary>Pillow's ImageDraw.rectangle on an RGB picture, x0..x1 and y0..y1 inclusive, cut to the picture: filled, or its outline width pixels
    /// thick, drawn inside the box.</summary>
    public static void Rectangle(Rgb im, int x0, int y0, int x1, int y1, Rgb3 colour, bool fill = true, int width = 1)
    {
        if (x0 > x1)
            (x0, x1) = (x1, x0);
        if (y0 > y1)
            (y0, y1) = (y1, y0);
        for (var y = Math.Max(0, y0); y <= Math.Min(im.Height - 1, y1); y++)
            for (var x = Math.Max(0, x0); x <= Math.Min(im.Width - 1, x1); x++)
            {
                if (!fill && y > y0 + width - 1 && y < y1 - width + 1 && x > x0 + width - 1 && x < x1 - width + 1)
                    continue;
                var p = (y * im.Width + x) * 3;
                im.Pixels[p] = (byte)colour.R;
                im.Pixels[p + 1] = (byte)colour.G;
                im.Pixels[p + 2] = (byte)colour.B;
            }
    }

    /// <summary>Whether two pictures differ anywhere in this box (ImageChops.difference(...).getbbox() is not None).</summary>
    public static bool Differs(Rgb a, Rgb b, Px box)
    {
        for (var y = box.Y0; y < box.Y1; y++)
        {
            var p = (y * a.Width + box.X0) * 3;
            if (!a.Pixels.AsSpan(p, (box.X1 - box.X0) * 3).SequenceEqual(b.Pixels.AsSpan(p, (box.X1 - box.X0) * 3)))
                return true;
        }
        return false;
    }

    /// <summary>A grey picture of Gaussian noise about 128 (Pillow's effect_noise, then RGB): the made-up game world of the offline checks.</summary>
    public static Rgb Noise(int w, int h, double sigma, int seed)
    {
        var random = new Random(seed);
        var im = new Rgb(w, h);
        double next = 0;
        var nextOk = false;
        for (var i = 0; i < w * h; i++)
        {
            double value;
            if (nextOk)
            {
                value = next;
                nextOk = false;
            }
            else
            {
                double v1, v2, radius;
                do
                {
                    v1 = random.NextDouble() * 2 - 1;
                    v2 = random.NextDouble() * 2 - 1;
                    radius = v1 * v1 + v2 * v2;
                }
                while (radius >= 1.0 || radius == 0);
                var factor = Math.Sqrt(-2.0 * Math.Log(radius) / radius);
                value = factor * v1;
                next = factor * v2;
                nextOk = true;
            }
            var g = (byte)Math.Clamp((int)(128 + sigma * value), 0, 255);
            im.Pixels[i * 3] = im.Pixels[i * 3 + 1] = im.Pixels[i * 3 + 2] = g;
        }
        return im;
    }

    // ---------------------------------------------------------------- a frame as it may be published

    /// <summary>The row's masks for this run: under --renamed only those that hide more than a name (CS2's scoreboard: its avatars).</summary>
    static IEnumerable<Mask> KeptMasks(Row row) => row.Masks.Where(m => !Renamed || m == BlurScoreboard);

    /// <summary>The frame as it may be published: the avatar, chat (or what the row's chat mask says), the row's own; under --renamed the avatar and
    /// the scoreboard alone.</summary>
    public static Rgb ApplyMasks(Rgb im, Row row)
    {
        int w = im.Width, h = im.Height;
        var s = h / 1080.0;
        Smear(im, Place(row.Avatar, w, h), s);
        Smear(im, Place(BuildInfo, w, h), s);
        if (!row.Chat && !Renamed)
            Smear(im, ChatZone(w, h, row.Win), s);
        foreach (var m in KeptMasks(row))
        {
            if (m.Kind == "chat")
                Smear(im, ChatZone(w, h, row.Win, m.Y1), s);
            else if (m.Kind == "blur")
                Smear(im, MaskBox(m, w, h), s);
            else
                PaintName(im, MaskBox(m, w, h), m, s);
        }
        return im;
    }

    public static string DescribePost(Row row, int w, int h)
    {
        var parts = new List<string> { $"blur avatar {Place(row.Avatar, w, h)}", $"blur build line {Place(BuildInfo, w, h)}" };
        if (!row.Chat && !Renamed)
            parts.Add($"blur chat {ChatZone(w, h, row.Win)}");
        foreach (var m in KeptMasks(row))
        {
            if (m.Kind == "chat")
                parts.Add($"blur chat above its lines {ChatZone(w, h, row.Win, m.Y1)}");
            else if (m.Kind == "blur")
                parts.Add($"blur {MaskBox(m, w, h)}");
            else
                parts.Add($"paint {Py.Repr(m.Text)} {MaskBox(m, w, h)}");
        }
        if (row.Crop is not null)
            parts.Add($"crop {Place(row.Crop, w, h)}");
        return string.Join("; ", parts);
    }

    /// <summary>Masks a raw frame, writes it and its crop at full size. Returns the file names.</summary>
    public static (string File, string Crop) PostFrame(string raw, Row row, string outDir)
    {
        var im = Pictures.Open(raw);
        ApplyMasks(im, row);
        Directory.CreateDirectory(outDir);
        var stem = Path.GetFileNameWithoutExtension(raw);
        var name = stem + ".png";
        File.WriteAllBytes(Path.Combine(outDir, name), Pictures.Png(im));
        var crop = "";
        if (row.Crop is not null)
        {
            crop = stem + "-crop.png";
            var c = Place(row.Crop, im.Width, im.Height);
            File.WriteAllBytes(Path.Combine(outDir, crop), Pictures.Png(im.Crop(c.X0, c.Y0, c.X1, c.Y1)));
        }
        return (name, crop);
    }
}
