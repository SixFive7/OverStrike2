using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Deploy;

/// <summary>The menu's map thumbnails: one small JPEG per map, the stylesheet that gives each one its class, and the list that numbers them
/// (hud/thumbs.py, ported).
///
///     thumbs [--export FILE] [--queues FILE] [--ahead FILE] [--workers 4]
///
/// The pictures are the maps' own Workshop previews, from Steam's image CDN: the preview the Workshop sync's export or an --ahead file names,
/// else the preview_url the public Web API gives for an item (no key) - one request at a time, each at least 2 s after the last one ended.
///
///   --export FILE   the Workshop sync's export (os2-sync export, on the server): every mode's list, each entry with its preview. Without it:
///                   bridge/maps.json, Arms Race's list as the repository has it, with no previews
///   --queues FILE   the bridge's queues.json: the maps its random queues will put on the next ballots, first in line for a picture. An
///                   export that carries the queues itself ("queues") needs no --queues
///   --ahead FILE    a JSON array of {"id", "preview"}: maps that are not listed yet but will be, most popular first
///
/// Writes hud/thumbs/&lt;id&gt;.jpg (336x189, quality 82; gitignored, other authors' pictures - a JPEG that exists is kept as it is),
/// bridge/Shared/HudThumbList.cs (every map that ever had a picture, in the order they got one: append-only) and
/// hud/styles/gungame_hud_thumbs_&lt;release&gt;.css (one rule per picture, ".th&lt;n / 64&gt;.tl&lt;n % 64&gt;"). Black bars baked into a preview are
/// filled with a blurred copy of the picture first. The picture work is Pillow's, number for number (Pictures).</summary>
static class Thumbs
{
    public const int Base = 64;       // HudThumbs.Base: picture n has the classes th<n / 64> and tl<n % 64>
    public const int MaxHigh = 256;   // HudThumbs.MaxHigh: th0 ... th255, 16,384 pictures
    public const int Width = 336, Height = 189, Quality = 82;
    const string Details = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    static readonly HashSet<string> Sizing = ["imw", "imh", "ima", "impolicy", "imcolor", "imsharp", "letterbox"];   // Steam's sizing query: without it, the upload
    const string Agent = "OverStrike2-thumbs/2.0 (the Over Strike 2 HUD's map pictures; os2-deploy thumbs)";
    const double ApiGap = 2.0;        // seconds from the end of one Web API request to the start of the next, never less
    const int MaxBytes = 32 * 1024 * 1024;
    const int Dark = 24, MinBar = 4;   // a bar line: every pixel's luma at most Dark; a side is cut only when its bar is at least MinBar px

    const string Head = """
/*
  Over Strike 2 - the menu's map thumbnails, written by hud/thumbs.py: do not edit by hand.

  A card's picture panel gets two classes, th<n / 64> and tl<n % 64>, where n is the map's place in bridge/HudThumbList.cs
  (HudThumbs in bridge/HudBrowser.cs). Two classes out of a small set rather than one per map: the game counts every class name
  the server ever sets on the layout entity and takes no more than 1,024 of them.

  A map's picture is its Workshop preview, a JPEG in the addon (file://{images}/... makes the resource compiler build it, storing
  the JPEG as it is); an official map's is the game's own 360p screenshot, so it costs the addon nothing. Maps that are not listed yet
  have theirs here too, so a map the nightly sync lists later has its picture without a new HUD.
*/

""";

    const string ListHead = """
// Written by hud/thumbs.py - do not edit by hand. Every map that ever had a picture in the menu, in the order
// they got one: a key's place in Keys is its picture's number (HudThumbs), and gungame_hud_thumbs.css has the rule for each number.
// Append-only on purpose: a player whose CS2 still holds an older addon then sees no picture for a newer map, never another map's.
static class HudThumbList
{
    public static readonly string[] Keys =
    [

""";

    // ---------------------------------------------------------------------------------------------------------- the pictures

    /// <summary>The picture without the black bars baked into its Workshop preview: whole edge rows or columns that are near-black from end
    /// to end are found, at most a third of each side, and the picture inside them is fitted whole into 336x189 on a blurred, darkened copy
    /// of itself. Nothing of the picture is cut. Returns the picture and the box that held it (null when there were no bars).</summary>
    public static (Rgb Picture, (int Left, int Top, int Right, int Bottom)? Box) Debar(Rgb im)
    {
        var g = Pictures.Luma(im);
        int w = im.Width, h = im.Height;
        bool DarkCol(int x)
        {
            for (var y = 0; y < h; y++)
                if (g[y * w + x] > Dark)
                    return false;
            return true;
        }
        bool DarkRow(int y)
        {
            for (var x = 0; x < w; x++)
                if (g[y * w + x] > Dark)
                    return false;
            return true;
        }
        var left = 0;
        while (left < w / 3 && DarkCol(left))
            left++;
        var right = w;
        while (right > w - w / 3 && DarkCol(right - 1))
            right--;
        var top = 0;
        while (top < h / 3 && DarkRow(top))
            top++;
        var bottom = h;
        while (bottom > h - h / 3 && DarkRow(bottom - 1))
            bottom--;
        left = left >= MinBar ? left : 0;
        top = top >= MinBar ? top : 0;
        right = w - right >= MinBar ? right : w;
        bottom = h - bottom >= MinBar ? bottom : h;
        if ((left, top, right, bottom) == (0, 0, w, h))
            return (im, null);
        var inner = im.Crop(left, top, right, bottom);
        int cw = inner.Width, ch = inner.Height;
        var cover = Math.Max((double)Width / cw, (double)Height / ch);
        var bg = Pictures.Resize(inner, Math.Max(Width, (int)Math.Round(cw * cover)), Math.Max(Height, (int)Math.Round(ch * cover)));
        bg = bg.Crop((bg.Width - Width) / 2, (bg.Height - Height) / 2, (bg.Width - Width) / 2 + Width, (bg.Height - Height) / 2 + Height);
        bg = Pictures.Brightness(Pictures.GaussianBlur(bg, 14), 0.55f);
        var fit = Math.Min((double)Width / cw, (double)Height / ch);
        var fg = Pictures.Resize(inner, (int)Math.Round(cw * fit), (int)Math.Round(ch * fit));
        bg.Paste(fg, (Width - fg.Width) / 2, (Height - fg.Height) / 2);
        return (bg, (left, top, right, bottom));
    }

    /// <summary>The upload scaled to cover 336x189 and cut from the middle: never letterboxed, never squashed.</summary>
    public static Rgb Cover(byte[] data)
    {
        var img = Pictures.Decode(data) ?? throw new InvalidDataException("cannot identify image file");
        var scale = Math.Max((double)Width / img.Width, (double)Height / img.Height);
        int w = Math.Max(Width, (int)Math.Ceiling(img.Width * scale)), h = Math.Max(Height, (int)Math.Ceiling(img.Height * scale));
        img = Pictures.Resize(img, w, h);
        int left = (w - Width) / 2, top = (h - Height) / 2;
        return img.Crop(left, top, left + Width, top + Height);
    }

    /// <summary>The preview's own upload: Steam's sizing query dropped, anything else in it kept (urlsplit, parse_qsl with blank values
    /// kept, urlencode, urlunsplit without the fragment, as thumbs.py did).</summary>
    public static string Original(string? url)
    {
        url = Py.Strip(url);
        var colon = url.IndexOf(':');
        if (colon <= 0)
            return "";
        var scheme = url[..colon].ToLowerInvariant();
        var rest = url[(colon + 1)..];
        if (scheme is not ("http" or "https") || !rest.StartsWith("//", StringComparison.Ordinal))
            return "";
        rest = rest[2..];
        var hash = rest.IndexOf('#');
        if (hash >= 0)
            rest = rest[..hash];
        var end = rest.IndexOfAny(['/', '?']);
        var netloc = end < 0 ? rest : rest[..end];
        if (netloc.Length == 0)
            return "";
        rest = end < 0 ? "" : rest[end..];
        var q = rest.IndexOf('?');
        var path = q < 0 ? rest : rest[..q];
        var query = q < 0 ? "" : rest[(q + 1)..];
        var kept = new List<(string, string)>();
        foreach (var part in query.Split('&'))
        {
            if (part.Length == 0)
                continue;
            var eq = part.IndexOf('=');
            var k = Unquote(eq < 0 ? part : part[..eq]);
            var v = Unquote(eq < 0 ? "" : part[(eq + 1)..]);
            if (!Sizing.Contains(k.ToLowerInvariant()))
                kept.Add((k, v));
        }
        var encoded = string.Join("&", kept.Select(p => Fmt.QuotePlus(p.Item1) + "=" + Fmt.QuotePlus(p.Item2)));
        return $"{scheme}://{netloc}{path}" + (encoded.Length > 0 ? "?" + encoded : "");
    }

    static string Unquote(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    // ---------------------------------------------------------------------------------------------------------------- Steam

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    static byte[] Get(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", Agent);
        using var resp = Http.Send(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        using var stream = resp.Content.ReadAsStream();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = stream.Read(buffer)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > MaxBytes)
                throw new InvalidDataException("a preview larger than 32 MB");
        }
        return ms.ToArray();
    }

    /// <summary>The one door to the Steam Web API: one request at a time, and the next no sooner than ApiGap after the last one ENDED, however
    /// it ended (an answer, an error, a timeout).</summary>
    sealed class WebApi
    {
        readonly Lock gate = new();
        long ended = -1;

        public JsonNode? Post(string url, IEnumerable<KeyValuePair<string, string>> form)
        {
            lock (gate)
            {
                if (ended >= 0)
                {
                    var wait = ApiGap - Stopwatch.GetElapsedTime(ended).TotalSeconds;
                    if (wait > 0)
                        Thread.Sleep(TimeSpan.FromSeconds(wait));
                }
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
                    req.Headers.TryAddWithoutValidation("User-Agent", Agent);
                    using var resp = Http.Send(req);
                    resp.EnsureSuccessStatusCode();
                    return Json.Parse(resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                }
                finally
                {
                    ended = Stopwatch.GetTimestamp();
                }
            }
        }
    }

    static readonly WebApi Api = new();

    /// <summary>Each id's preview_url from the Web API: 100 ids a call, through the one door.</summary>
    static Dictionary<string, string> PreviewsFromSteam(IReadOnlyList<string> ids)
    {
        var found = new Dictionary<string, string>();
        for (var i = 0; i < ids.Count; i += 100)
        {
            var batch = ids.Skip(i).Take(100).ToList();
            var form = new List<KeyValuePair<string, string>> { new("itemcount", batch.Count.ToString(CultureInfo.InvariantCulture)) };
            form.AddRange(batch.Select((v, k) => new KeyValuePair<string, string>($"publishedfileids[{k}]", v)));
            var details = Json.Arr(Api.Post(Details, form), "response", "publishedfiledetails");
            foreach (var d in details)
                if (Json.Truthy(Json.At(d, "preview_url")))
                    found[Json.PyStr(Json.At(d, "publishedfileid"))] = Json.PyStr(Json.At(d, "preview_url"));
        }
        return found;
    }

    // ------------------------------------------------------------------------------------------------------------------ lists

    /// <summary>A list's entries (objects with an id and a rank, as maps.json and the export have them), most popular first.</summary>
    public static List<JsonObject> ByRank(JsonNode? entries)
    {
        long Rank(JsonObject e)   // int(e.get("rank")), else last
        {
            if (Json.At(e, "rank") is not JsonValue v)
                return 1L << 30;
            if (v.TryGetValue<bool>(out var b))
                return b ? 1 : 0;
            if (Json.AsLong(v) is long l)
                return l;
            if (v.TryGetValue<double>(out var d) && double.IsFinite(d))
                return (long)Math.Truncate(d);
            if (v.TryGetValue<string>(out var s) && long.TryParse(Py.Strip(s).Replace("_", "", StringComparison.Ordinal), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var p))
                return p;
            return 1L << 30;
        }
        return (entries as JsonArray ?? []).OfType<JsonObject>().Where(e => Py.IsDigits(Json.PyStr(e["id"]))).OrderBy(Rank).ToList();
    }

    /// <summary>The export's maps, every mode's: (ids most popular first, Arms Race's list first; their previews).</summary>
    public static (List<string> Ids, Dictionary<string, string> Previews) FromExport(JsonNode? export)
    {
        var lists = new List<JsonNode?> { Json.At(export, "maps.json") };
        lists.AddRange(Json.Obj(export, "modes").OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value));
        var ids = new List<string>();
        var previews = new Dictionary<string, string>();
        foreach (var entries in lists)
        {
            foreach (var e in ByRank(entries))
            {
                var id = Json.PyStr(e["id"]);
                ids.Add(id);
                if (Json.Truthy(e["preview"]))
                    previews[id] = Json.PyStr(e["preview"]);
            }
        }
        return (ids, previews);
    }

    /// <summary>The Workshop ids queues.json (version 2) names: the open ballot's first ("ballot"), then every lane of the stock in play order.</summary>
    public static List<string> FromQueues(JsonNode? queues)
    {
        if (queues is not JsonObject o || !o.ContainsKey("version") || Json.AsLong(o["version"]) != 2)
            throw Out.Stop("the queues are not queues.json version 2");
        var stock = Json.Obj(queues, "stock");
        var keys = Json.Arr(queues, "ballot").Select(Json.PyStr).ToList();
        foreach (var (_, list) in stock.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            keys.AddRange((list as JsonArray ?? []).Select(Json.PyStr));
        return keys.Where(Py.IsDigits).ToList();
    }

    /// <summary>The list as committed: the keys in their order (empty when there is none yet).</summary>
    static List<string> KnownKeys(string list)
    {
        if (!File.Exists(list))
            return [];
        var body = File.ReadAllText(list, Encoding.UTF8);
        return Regex.Matches(body[body.IndexOf("Keys =", StringComparison.Ordinal)..], @"^\s*""([^""]+)"",\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
    }

    /// <summary>Every official map of every mode, from Modes.cs: the game's own screenshot is its picture.</summary>
    static List<string> Officials(string modes) =>
        Regex.Matches(File.ReadAllText(modes, Encoding.UTF8), @"""((?:ar|de|cs)_[a-z0-9_]+)""").Select(m => m.Groups[1].Value).Distinct().ToList();

    /// <summary>Picture n's rule.</summary>
    static string Rule(int n, string url) => $".th{n / Base}.tl{n % Base}\n{{\n\tbackground-image: url( \"{url}\" );\n}}\n";

    static JsonNode? ReadJson(string path) => Json.Parse(File.ReadAllText(path, Encoding.UTF8));

    public static int Command(string[] args)
    {
        var a = new Args(args, "thumbs [--export FILE] [--queues FILE] [--ahead FILE] [--workers N] | thumbs --one PREVIEW OUT.jpg|OUT.png",
                         flags: ["--one"], options: ["--export", "--queues", "--ahead", "--workers"]);
        if (a.Flag("--one"))
        {
            var (preview, target) = (a.Positional(), a.Positional());
            a.Done();
            if (preview is null || target is null)
                throw new Exit(2, "usage: os2-deploy thumbs --one PREVIEW OUT.jpg|OUT.png");
            return One(preview, target);
        }
        a.Done();
        return Run(Repo.Root, a.Opt("--export"), a.Opt("--queues"), a.Opt("--ahead"), a.IntOpt("--workers", 4));
    }

    /// <summary>One picture from a preview file, offline, as Run makes it - a JPEG, or a PNG (lossless: every pixel as made) to look at.</summary>
    static int One(string preview, string target)
    {
        var (im, box) = Debar(Cover(File.ReadAllBytes(preview)));
        File.WriteAllBytes(target, target.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? Pictures.Png(im) : Pictures.Jpeg(im, Quality));
        Out.Print($"{Path.GetFileName(target)}: {im.Width}x{im.Height}" + (box is { } b ? $", black bars filled, kept ({b.Left}, {b.Top}, {b.Right}, {b.Bottom})" : ""));
        return 0;
    }

    public static int Run(string root, string? exportPath, string? queuesPath, string? aheadPath, int workers)
    {
        var maps = Path.Combine(root, "bridge", "maps.json");
        var modes = Path.Combine(root, "bridge", "Shared", "Modes.cs");
        var outDir = Path.Combine(root, "hud", "thumbs");
        var release = Regex.Match(File.ReadAllText(Path.Combine(root, "bridge", "Shared", "Hud.cs"), Encoding.UTF8), @"ReleaseFile = ""(\w+)""").Groups[1].Value;
        var css = Path.Combine(root, "hud", "styles", $"gungame_hud_thumbs_{release}.css");
        var listFile = Path.Combine(root, "bridge", "Shared", "HudThumbList.cs");

        var export = exportPath is not null ? ReadJson(exportPath) : null;
        List<string> listed;
        Dictionary<string, string> previews;
        if (export is not null)
        {
            (listed, previews) = FromExport(export);
            Out.Print($"the sync's export: {listed.Count} listed map(s) of every mode, {previews.Count} with their preview");
        }
        else
        {
            listed = ByRank(ReadJson(maps)).Select(m => Json.PyStr(m["id"])).ToList();
            previews = [];
            Out.Print($"{Path.GetFileName(maps)}: {listed.Count} Arms Race map(s), no previews (--export gives every mode's, with them)");
        }
        var queues = queuesPath is not null ? ReadJson(queuesPath) : export is JsonObject eo && eo.TryGetPropertyValue("queues", out var qn) ? qn : null;
        var queued = queues is not null ? FromQueues(queues) : [];
        if (queues is null)
            Out.Print("no random queues (--queues, or an export that carries them): the next ballots' maps get no head start");
        var ahead = aheadPath is not null ? (ReadJson(aheadPath) as JsonArray ?? []) : [];
        Directory.CreateDirectory(outDir);
        var keys = KnownKeys(listFile);
        var number = new Dictionary<string, int>();
        foreach (var (k, i) in keys.Select((k, i) => (k, i)))
            number[k] = i;
        if (number.Count != keys.Count)
            throw Out.Stop($"{Path.GetFileName(listFile)} names a map twice - fix it by hand before anything else");
        var official = Officials(modes);
        var wanted = queued.Concat(listed).Concat(ahead.Select(e => Json.PyStr(Json.At(e, "id")))).Distinct().ToList();
        var urlOf = new ConcurrentDictionary<string, string>(previews);
        foreach (var e in ahead)
            if (Json.Truthy(Json.At(e, "preview")))
                urlOf[Json.PyStr(Json.At(e, "id"))] = Json.PyStr(Json.At(e, "preview"));
        // Every numbered map keeps its picture; every map wanted gets one. Only a JPEG that is missing is fetched.
        var workshop = keys.Where(Py.IsDigits).Concat(wanted).Distinct().ToList();
        string Jpeg(string key) => Path.Combine(outDir, key + ".jpg");
        var missing = workshop.Where(k => !File.Exists(Jpeg(k))).ToList();
        var ask = missing.Where(k => !urlOf.TryGetValue(k, out var u) || u.Length == 0).ToList();
        if (ask.Count > 0)
        {
            Out.Print($"asking the Web API for {ask.Count} preview(s)");
            foreach (var (k, v) in PreviewsFromSteam(ask))
                urlOf[k] = v;
        }
        var made = new List<string>();
        var failed = new List<string>();
        var debarred = new List<string>();
        var gate = new Lock();
        var started = Stopwatch.StartNew();
        void Make(string key)
        {
            var url = Original(urlOf.GetValueOrDefault(key, ""));
            try
            {
                if (url.Length == 0)
                    throw new InvalidOperationException("no preview url");
                byte[]? data = null;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        data = Get(url);
                        break;
                    }
                    catch (Exception) when (attempt < 2)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(2 + attempt * 3));
                    }
                }
                var (im, box) = Debar(Cover(data!));
                File.WriteAllBytes(Jpeg(key), Pictures.Jpeg(im, Quality));
                lock (gate)
                {
                    made.Add(key);
                    if (box is { } b)
                        debarred.Add($"{key} kept ({b.Left}, {b.Top}, {b.Right}, {b.Bottom})");
                    if (made.Count % 250 == 0)
                        Out.Print($"  {made.Count} of {missing.Count} in {Fmt.Fixed(started.Elapsed.TotalSeconds, 0)} s");
                }
            }
            catch (Exception e) when (e is not Exit)   // a map without a picture keeps the card's plain backdrop and the picture on request
            {
                lock (gate)
                    failed.Add(key);
                Console.Error.WriteLine($"no picture for {key}: {e.Message}");
            }
        }
        if (missing.Count > 0)
        {
            Out.Print($"fetching {missing.Count} preview(s) from Steam's image CDN, {workers} at a time");
            Parallel.ForEach(missing, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers) }, Make);
        }
        if (failed.Count > 0)   // an author changed the preview since the address was noted: ask Steam for the one it has now, once
        {
            var again = failed.ToList();
            failed.Clear();
            Out.Print($"asking the Web API again for {again.Count} preview(s) that failed");
            foreach (var (k, v) in PreviewsFromSteam(again))
                urlOf[k] = v;
            foreach (var key in again)
                Make(key);
        }
        var added = 0;
        foreach (var key in official.Concat(workshop))
        {
            if (!number.ContainsKey(key) && (!Py.IsDigits(key) || File.Exists(Jpeg(key))))
            {
                number[key] = keys.Count;
                keys.Add(key);
                added++;
            }
        }
        if (keys.Count > Base * MaxHigh)
            throw Out.Stop($"{keys.Count} pictures do not fit {Base} x {MaxHigh} numbers: raise MaxHigh here and in HudThumbs");
        var rules = new List<(int N, string Text)>();
        long total = 0;
        foreach (var (key, n) in number)
        {
            if (!Py.IsDigits(key))
                rules.Add((n, Rule(n, $"s2r://panorama/images/map_icons/screenshots/360p/{key}_png.vtex")));
            else if (File.Exists(Jpeg(key)))
            {
                total += new FileInfo(Jpeg(key)).Length;
                rules.Add((n, Rule(n, $"file://{{images}}/custom_game/gungame_hud/thumbs/{key}.jpg")));
            }
        }
        rules.Sort((x, y) => x.N.CompareTo(y.N));
        File.WriteAllText(css, Head + "\n" + string.Join("\n", rules.Select(r => r.Text)), new UTF8Encoding(false));
        File.WriteAllText(listFile, ListHead + string.Concat(keys.Select(k => $"        \"{k}\",\n")) + "    ];\n}\n", new UTF8Encoding(false));
        var classes = rules.Select(r => r.N / Base).Distinct().Count() + rules.Select(r => r.N % Base).Distinct().Count();
        if (debarred.Count > 0)
        {
            File.AppendAllText(Path.Combine(outDir, "debarred.txt"), string.Join("\n", debarred) + "\n", new UTF8Encoding(false));
            Out.Print($"black bars filled on {debarred.Count} new picture(s): {Path.Combine(outDir, "debarred.txt")}");
        }
        var jpegs = number.Keys.Count(k => Py.IsDigits(k) && File.Exists(Jpeg(k)));
        Out.Print($"{rules.Count} rules: {jpegs} JPEGs, {Fmt.Fixed(total / 1e6, 1)} MB; {made.Count} new, {failed.Count} failed ({string.Join(", ", failed.Take(8))}); "
                  + $"{number.Keys.Count(k => !Py.IsDigits(k))} official; {keys.Count} numbered ({added} new); {classes} class names; {Path.GetFileName(css)} and {Path.GetFileName(listFile)} written");
        return 0;
    }
}
