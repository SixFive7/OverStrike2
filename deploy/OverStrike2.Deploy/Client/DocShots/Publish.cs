using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Deploy;

/// <summary>tools/client/docshots/publish.py, ported: docshots' reviewed pictures into the documentation, as AGENTS.md asks ("The documentation shows the
/// whole HUD"): a page per part of the HUD under docs/hud/, their index in README.md between its hud-index markers, the gallery of every on-screen message
/// (docs/hud/messages.html), and every picture reviewed into tools/scan/allow.txt with its hash. Writes the working tree only: no git command, no commit.
///
///     os2-deploy docshots publish --out &lt;docshots run folder&gt; [--repo &lt;checkout&gt;] [--res 1920x1080] [--all]      (--repo: this one by default)
///     os2-deploy docshots publish --relink [--repo &lt;checkout&gt;]       a gallery that still carries its pictures inside it: each one a file of its own, linked
///
/// The history keeps every picture forever, so a retake commits only the pictures that really changed: each is compared with the committed one at its
/// path (DocShots.SamePicture) on what it is about, all but what differs on every shot - the clocks, timers, counters and random tiles VolatileBoxes
/// names, JPEG's noise, the game world dimmed behind a translucent panel. Not the game world a crop shows around a panel: the board, the top line's
/// messages, the banners and their gallery pictures are cut with a margin of the game around them, which differs in every run, so a retake flags them
/// even when the panel is the same - look at each beside the committed one, and keep the committed file when only the world changed. A picture that
/// shows the same keeps its committed bytes. --all
/// writes every picture anew. Pictures no page and no message uses any more are removed. It refuses unless &lt;out&gt;/&lt;res&gt;/reviewed.txt names every
/// picture the run wrote. The pages' pictures go in as JPEG at full size (quality 85, no chroma subsampling), a page takes a row's first and last frame;
/// the gallery's are files of their own under docs/hud/img/gallery/: how a message looks (the crop) and where it is (the frame, 800 px wide, marked).</summary>
static class Publish
{
    // README's parts, in its order: (slug, the part as README names it, what the page says first, its rows)
    static readonly (string Slug, string Part, string Lead, string[] Ids)[] Pages =
    [
        ("board", "The board", "Hold TAB: the board beside CS2's scoreboard - the map that is on, Like and Dislike, Start a vote, the way into the menu, Lately.",
         ["board-default", "board-liked", "board-why", "board-big", "board-ads", "board-wait"]),
        ("start", "START", "The window's first page, and the welcome at a join.",
         ["page-start", "welcome-first", "welcome-back", "join-tagline", "check-click-to-play", "top-over-window", "window-restart-strip", "updated-joiner", "mode-armsrace-back"]),
        ("maps-browse", "MAPS, Browse", "The map browser: cards, search and keyboard, sorting, filters, a Workshop map's card, a risky map's card.",
         ["page-maps", "maps-sort-votes", "maps-sort-name", "maps-sort-size", "maps-sort-size-desc", "maps-mode-armsrace", "maps-mode-deathmatch", "maps-mode-wingman", "maps-size-tiny",
          "maps-size-small", "maps-size-mid", "maps-size-large", "maps-liked", "maps-official", "maps-ours", "maps-page-2", "maps-page-last", "maps-keyboard", "maps-search",
          "maps-search-keyboard", "maps-search-empty", "maps-workshop-refused", "maps-workshop-ready", "maps-liked-card", "why-card", "maps-risky-card", "play-asks-first"]),
        ("maps-vote", "MAPS, Vote", "The vote for the next map: everybody's window opens on the ballot, frozen, until it is decided.",
         ["vote-open", "vote-limit", "vote-my-vote", "vote-inactivity", "vote-kept", "vote-cannot-call", "vote-browse", "vote-picked-card", "vote-decided", "now-playing", "vote-started-line",
          "vote-random-lit", "vote-waiting", "vote-pager", "vote-limit-low", "vote-tie-lead", "chat-vote-clicked"]),
        ("fun", "FUN", "The fun settings: every one at its default, set by somebody, greyed when impossible, idle in a mode, and the rows of the other modes.",
         ["page-fun", "fun-set-by", "fun-refused", "fun-refused-tip", "fun-already", "fun-idle", "fun-many", "mode-deathmatch-fun", "mode-wingman-fun"]),
        ("server-players", "SERVER, Players", "Who is on, and which HUD their game shows.", ["page-players", "players-older", "players-check", "vote-players"]),
        ("server-game", "SERVER, Game", "The match length, and the two restarts: the match or the whole server.",
         ["page-game", "game-time-10", "game-restart-armed", "rematch-countdown", "mode-deathmatch-game", "mode-wingman-game"]),
        ("server-sync", "SERVER, Sync", "The Workshop sync's dashboard: now, the queue, the history.", ["page-sync", "sync-queue-next", "sync-history-next", "sync-maps-next", "sync-maps-last"]),
        ("help", "HELP, Help", "Where everything is.", ["page-help"]),
        ("how", "HELP, How it works", "Every rule of the vote and every chance of a random pick.", ["page-how"]),
    ];
    const string Gallery = "messages.html";
    const string Docs = "docs/hud";
    const string Img = Docs + "/img";
    const string GalleryImg = Img + "/gallery";       // the gallery's own pictures: <frame>-look.jpg (the crop), <frame>-where.jpg (the frame, its place marked)
    const int WhereWidth = 800;
    const string AllowHead = "# The documentation's screenshots (docshots.py, a real client against a test copy): each looked at for names, Steam ids and addresses; test names only";
    static readonly HashSet<string> Dropped = [];   // <res>/dropped.txt: frames the review threw out; they go nowhere

    /// <summary>A frame or a crop as it is published: RGB, the gallery's place marked in gold, at most width pixels wide.</summary>
    static Rgb Picture(string png, int? width = null, Px? box = null)
    {
        var im = Pictures.Open(png);
        if (box is { } b)
            DocShots.Rectangle(im, b.X0, b.Y0, b.X1, b.Y1, DocShots.Gold, fill: false, width: Math.Max(3, Py.Round(im.Height / 270.0)));
        if (width is int w && im.Width > w)
            im = Pictures.Resize(im, w, Py.Round(im.Height * w / (double)im.Width));
        return im;
    }

    static byte[] Encode(Rgb im, int quality = 85) => Pictures.Jpeg(im, quality, full: true);

    static Px Scaled(Px box, double f) => new(Py.Round(box.X0 * f), Py.Round(box.Y0 * f), Py.Round(box.X1 * f), Py.Round(box.Y1 * f));

    static List<JsonObject> FramesOf(JsonNode? man, string rid) =>
        (Json.At(man, "rows", rid, "frames") as JsonArray ?? []).OfType<JsonObject>().Where(f => !Dropped.Contains(Json.Str(f, "file") ?? "")).ToList();

    /// <summary>The pictures under docs/hud/img/ as this publish leaves them: each one compared with the committed one at its path and kept when it shows
    /// the same (its bytes untouched), written when it differs or is new; what is not used any more goes at the end.</summary>
    sealed class Store(string repo, bool allAnew)
    {
        public readonly HashSet<string> Used = [];
        public readonly List<string> Kept = [], Changed = [], New = [];

        /// <summary>rel: the path under the repository; im: the picture to publish; region and volatile: what a comparison looks at and leaves out, in
        /// im's pixels. Returns the path relative to docs/hud/, for a link.</summary>
        public string Put(string rel, Rgb im, int quality, Px region, IReadOnlyList<Px>? volatileBoxes = null)
        {
            var path = Path.Combine(repo, rel);
            Used.Add(rel);
            if (File.Exists(path) && !allAnew)
            {
                var old = Pictures.Open(path);
                var (same, why) = DocShots.SamePicture(im, old, region, volatileBoxes);
                if (same)
                {
                    Kept.Add(rel);
                    return rel[(Docs.Length + 1)..];
                }
                Changed.Add($"{rel} ({why})");
            }
            else
                New.Add(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Encode(im, quality));
            return rel[(Docs.Length + 1)..];
        }

        public List<string> Sweep()
        {
            var gone = new List<string>();
            foreach (var p in Jpgs(repo))
            {
                var rel = Rel(repo, p);
                if (!Used.Contains(rel))
                {
                    File.Delete(p);
                    gone.Add(rel);
                }
            }
            return gone;
        }
    }

    static string Rel(string repo, string path) => Path.GetRelativePath(repo, path).Replace('\\', '/');

    static List<string> Jpgs(string repo)
    {
        var dir = Path.Combine(repo, Img);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jpg", SearchOption.AllDirectories).OrderBy(p => Rel(repo, p), StringComparer.OrdinalIgnoreCase).ToList() : [];
    }

    static void WriteText(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));

    static string ReadText(string path) => File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>allow.txt's pictures: every file under docs/hud/img/ by its hash, under one heading; the lines of pictures that are gone go with them.</summary>
    static int WriteAllow(string repo)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in Jpgs(repo))
            hashes[Rel(repo, p)] = Repo.Sha256(p);
        var allow = Path.Combine(repo, "tools", "scan", "allow.txt");
        var lines = Py.SplitLines(ReadText(allow)).Where(l => !l.StartsWith($"binary {Img}/", StringComparison.Ordinal) && l != AllowHead).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        lines.AddRange(["", AllowHead]);
        lines.AddRange(hashes.Select(kv => $"binary {kv.Key} {kv.Value}  # a documentation screenshot: the HUD as a test client drew it on a test copy, test names only"));
        WriteText(allow, string.Join("\n", lines) + "\n");
        return hashes.Count;
    }

    static byte[] UnquoteToBytes(string s)
    {
        var output = new List<byte>();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                output.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
                output.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
        }
        return output.ToArray();
    }

    /// <summary>A gallery that carries its pictures inside it (data URLs, every byte %XX): each picture a file of its own under docs/hud/img/gallery/, its
    /// very bytes, named as publish names them (&lt;frame&gt;-look.jpg, &lt;frame&gt;-where.jpg from its alt text), and the gallery linking them; allow.txt
    /// takes their hashes.</summary>
    static int Relink(string repo)
    {
        var page = Path.Combine(repo, Docs, Gallery);
        var text = ReadText(page);
        var made = new List<string>();
        text = Regex.Replace(text, @"<img ((?:class=""where"" )?)src=""(data:image/jpeg,[^""]*)"" alt=""([^""]*)"">", m =>
        {
            var (attrs, url, alt) = (m.Groups[1].Value, m.Groups[2].Value, System.Net.WebUtility.HtmlDecode(m.Groups[3].Value));
            var where = attrs.Contains("class=\"where\"", StringComparison.Ordinal);
            var stem = where && alt.EndsWith(" on the screen", StringComparison.Ordinal) ? alt[..^" on the screen".Length] : alt;
            if (!Regex.IsMatch(stem, @"^[a-z0-9][a-z0-9-]*\z"))
                throw Out.Stop($"{Gallery}: a picture whose alt text names no frame: {Py.Repr(Py.Slice(alt, 0, 60))}");
            var rel = $"{GalleryImg}/{stem}-{(where ? "where" : "look")}.jpg";
            var data = UnquoteToBytes(url["data:image/jpeg,".Length..]);
            if (data.Length < 2 || data[0] != 0xFF || data[1] != 0xD8)
                throw Out.Stop($"{Gallery}: {Py.Repr(Py.Slice(alt, 0, 60))} is not a JPEG");
            var path = Path.Combine(repo, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
            made.Add(rel);
            return $"<img {attrs}src=\"{Py.HtmlEscape(rel[(Docs.Length + 1)..])}\" alt=\"{m.Groups[3].Value}\">";
        });
        if (text.Contains("data:image", StringComparison.Ordinal))
            throw Out.Stop($"{Gallery}: a picture is left inside it that this does not read");
        if (made.Count != made.Distinct().Count())
            throw Out.Stop($"{Gallery}: two pictures for one file name");
        WriteText(page, text);
        var n = WriteAllow(repo);
        Out.Print($"docs/hud/{Gallery}: {made.Count} pictures out of it into {GalleryImg}/, linked; {text.Length} characters now");
        Out.Print($"tools/scan/allow.txt: {n} pictures under docs/hud/img/ with their hashes");
        return 0;
    }

    public static int Command(string[] argv)
    {
        string? output = null, repoArg = null;
        var res = "1920x1080";
        bool all = false, relink = false;
        for (var i = 0; i < argv.Length; i++)
        {
            string Value() => i + 1 < argv.Length ? argv[++i] : throw new Exit(2, $"publish: error: argument {argv[i]}: expected one argument");
            switch (argv[i])
            {
                case "--out": output = Value(); break;
                case "--repo": repoArg = Value(); break;
                case "--res": res = Value(); break;
                case "--all": all = true; break;
                case "--relink": relink = true; break;
                default: throw new Exit(2, $"usage: docshots publish --out <run folder> [--repo <checkout>] [--res WxH] [--all] | --relink [--repo <checkout>]\npublish: error: unrecognized arguments: {argv[i]}");
            }
        }
        var repo = repoArg ?? DocShots.Os2;
        if (relink)
            return Relink(repo);
        if (output is null)
            throw new Exit(2, "usage: docshots publish --out <run folder> [--repo <checkout>] [--res WxH] [--all] | --relink [--repo <checkout>]\npublish: error: --out <docshots run folder>, or --relink");
        var resDir = Path.Combine(output, res);
        var (w, h) = DocShots.ParseRes(res);
        var man = Json.Parse(File.ReadAllText(Path.Combine(resDir, "manifest.json"), Encoding.UTF8));
        var info = DocShots.ValidateForPublish();
        var byId = new Dictionary<string, Row>();
        foreach (var r in DocShots.Rows)
            byId[r.Id] = r;
        var byName = info.ByName;
        Dropped.Clear();
        var droppedFile = Path.Combine(resDir, "dropped.txt");
        if (File.Exists(droppedFile))
            Dropped.UnionWith(ReadText(droppedFile).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var manRows = Json.At(man, "rows") as JsonObject ?? [];
        var wrote = manRows.SelectMany(g => (Json.At(g.Value, "frames") as JsonArray ?? []).OfType<JsonObject>())
            .Where(f => !Dropped.Contains(Json.Str(f, "file") ?? ""))
            .SelectMany(f => new[] { Json.Str(f, "file"), Json.Str(f, "crop") }).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList();
        var reviewedFile = Path.Combine(resDir, "reviewed.txt");
        var reviewed = File.Exists(reviewedFile) ? ReadText(reviewedFile).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet() : [];
        var missing = wrote.Where(x => !reviewed.Contains(x)).ToList();
        if (missing.Count > 0)
            throw Out.Stop($"REFUSED: {missing.Count} of {wrote.Count} pictures are not in {reviewedFile}: {string.Join(", ", missing.Take(8))}");
        var builtOrCommit = Json.Truthy(Json.At(man, "built")) ? Py.Str(Json.At(man, "built")) : Py.Str(Json.At(man, "commit"));
        var taken = $"at {res}, from OverStrike2 {builtOrCommit} (HUD content {Py.Str(Json.At(man, "content"))}, plugin {Py.Str(Json.At(man, "plugin"))})";
        var pics = new Store(repo, all);

        var placed = new HashSet<string>();
        foreach (var (slug, part, lead, ids) in Pages)
        {
            var lines = new List<string> { $"# The HUD: {part}", "", lead, "",
                $"A real game client draws every picture here against a test copy of the server, taken by script {taken}. Test names only: the avatar is blurred."
                + " The index of every part is in the [README](../../README.md#the-hud-page-by-page); every kind of on-screen message is in the [gallery](messages.html).", "" };
            foreach (var rid in ids)
            {
                var r = byId[rid];
                var got = FramesOf(man, rid);
                if (got.Count == 0)
                    continue;
                placed.Add(rid);
                lines.AddRange([$"## {r.Shows}", ""]);
                foreach (var f in got.Count <= 2 ? got : [got[0], got[^1]])      // a page takes a row's first and last frame: the gallery and the owner's review have them all
                {
                    var stem = Path.GetFileNameWithoutExtension(Json.Str(f, "file") ?? "");
                    var link = pics.Put($"{Img}/{stem}.jpg", Picture(Path.Combine(resDir, Json.Str(f, "file") ?? "")), 85, DocShots.CompareRegion(r, w, h), DocShots.VolatileBoxes(r, w, h));
                    lines.AddRange([$"![{Py.HtmlEscape(r.Shows, quote: false)}]({link})", ""]);   // Markdown alt text: < > & escaped, quotes as they are
                }
                if (r.Pick.Length > 0)
                    lines.AddRange([$"The message: {byName[r.Pick].Name}. Where it goes: {byName[r.Pick].Surfaces}", ""]);
            }
            WriteText(Path.Combine(repo, Docs, $"{slug}.md"), string.Join("\n", lines).TrimEnd() + "\n");
            Out.Print($"docs/hud/{slug}.md: {ids.Count(x => FramesOf(man, x).Count > 0)} of {ids.Length} rows with pictures");
        }

        // the gallery: every message of the message list, how it looks (the crop, full size) and where (the whole screen, smaller, the place marked)
        var parts = new List<string>
        {
            "<!doctype html><meta charset=utf-8><title>Over Strike 2 - every kind of on-screen message</title>",
            "<style>body{font:14px system-ui,sans-serif;background:#111;color:#ddd;margin:24px;max-width:1500px}h1{color:#E9B44C}h2{color:#E9B44C;margin-top:40px;border-top:1px solid #333;padding-top:12px}"
            + "h3{margin:22px 0 6px}figure{margin:6px 0 18px}figcaption{color:#99a;margin-top:4px}img{max-width:100%;border:1px solid #333}.where{width:640px}.none{color:#c96}</style>",
            "<h1>Over Strike 2 - every kind of on-screen message</h1>",
            "<p>One section per message, as the message system has it: how it looks, and where on the screen it appears (the gold box). A real game client drew each one against a test copy of"
            + $" the server, taken by script {Py.HtmlEscape(taken)}. Test names only. Messages without a picture say why.</p>",
        };
        var heading = "";
        var shown = 0;
        var fWhere = Math.Min(1.0, (double)WhereWidth / w);
        foreach (var p in info.Picks)
        {
            if (p.Heading != heading)
            {
                heading = p.Heading;
                parts.Add($"<h2>{Py.HtmlEscape(heading[0] + heading[1..].ToLowerInvariant())}</h2>");
            }
            parts.Add($"<h3>{Py.HtmlEscape(p.Name)}</h3><p>{Py.HtmlEscape(p.Surfaces)}</p>");
            var rows = DocShots.Rows.Where(r => r.Pick == p.Name).ToList();
            var pictured = false;
            foreach (var r in rows)
            {
                foreach (var f in FramesOf(man, r.Id))
                {
                    var stem = Path.GetFileNameWithoutExtension(Json.Str(f, "file") ?? "");
                    var full = Path.Combine(resDir, Json.Str(f, "file") ?? "");
                    Px? box = r.Crop is not null ? DocShots.Place(r.Crop, w, h) : null;
                    if (Json.Str(f, "crop") is { Length: > 0 } cropFile)
                    {
                        var look = Picture(Path.Combine(resDir, cropFile));
                        var lookLink = pics.Put($"{GalleryImg}/{stem}-look.jpg", look, 85, DocShots.CompareRegion(r, w, h, crop: true), DocShots.VolatileBoxes(r, w, h, crop: true));
                        parts.Add($"<figure><img src=\"{Py.HtmlEscape(lookLink)}\" alt=\"{Py.HtmlEscape(stem)}\"><figcaption>How it looks: {Py.HtmlEscape(r.Shows)}</figcaption></figure>");
                    }
                    var where = Picture(full, WhereWidth, box);
                    var link = pics.Put($"{GalleryImg}/{stem}-where.jpg", where, 75, Scaled(DocShots.CompareRegion(r, w, h), fWhere), DocShots.VolatileBoxes(r, w, h).Select(v => Scaled(v, fWhere)).ToList());
                    parts.Add($"<figure><img class=\"where\" src=\"{Py.HtmlEscape(link)}\" alt=\"{Py.HtmlEscape(stem)} on the screen\"><figcaption>Where: {Py.HtmlEscape(r.Shows)}</figcaption></figure>");
                    pictured = true;
                    break;      // one frame a row is enough for the gallery; the pages have them all
                }
            }
            if (pictured)
                shown++;
            else
            {
                var why = rows.Where(r => r.Status != DocShots.Ready && r.Reason.Length > 0).Select(r => r.Reason).FirstOrDefault() ?? "";
                if (why.Length == 0)
                    why = rows.Select(r => Json.Str(man, "rows", r.Id, "failed") ?? "").FirstOrDefault(x => x.Length > 0) ?? "";
                parts.Add($"<p class=\"none\">No picture: {Py.HtmlEscape(why.Length > 0 ? why : "not taken in this run")}</p>");
            }
        }
        WriteText(Path.Combine(repo, Docs, Gallery), string.Join("\n", parts) + "\n");
        Out.Print($"docs/hud/{Gallery}: {shown} of {info.Picks.Count} messages with a picture");

        // README's index: the Page column
        var readme = Path.Combine(repo, "README.md");
        var text = ReadText(readme);
        int a = text.IndexOf("<!-- hud-index:begin", StringComparison.Ordinal), b = text.IndexOf("<!-- hud-index:end -->", StringComparison.Ordinal);
        if (a < 0 || b < 0)
            throw Out.Stop("README.md: no hud-index markers");
        var block = text[a..b];
        foreach (var (slug, part, _, _) in Pages)
            block = Regex.Replace(block, $@"^(\| {Regex.Escape(part)} \|[^|\n]*\|) [^|\n]* \|$", m => $"{m.Groups[1].Value} [{slug}](docs/hud/{slug}.md) |", RegexOptions.Multiline);
        block = Regex.Replace(block, @"^(\| Every kind of on-screen message \|[^|\n]*\|) [^|\n]* \|$", m => $"{m.Groups[1].Value} [the gallery](docs/hud/{Gallery}) |", RegexOptions.Multiline);
        WriteText(readme, text[..a] + block + text[b..]);
        Out.Print("README.md: the Page column filled");

        var gone = pics.Sweep();
        var n = WriteAllow(repo);
        Out.Print($"pictures: {pics.Kept.Count} kept as committed (the same, all but what differs on every shot), {pics.Changed.Count} written anew, {pics.New.Count} new, {gone.Count} no longer used and removed");
        foreach (var c in pics.Changed)
            Out.Print("  changed: " + c);
        var noPage = manRows.Select(kv => kv.Key).Where(r => !placed.Contains(r) && byId[r].Pick.Length == 0).Order(StringComparer.Ordinal).ToList();
        Out.Print($"tools/scan/allow.txt: {n} pictures under docs/hud/img/ with their hashes; rows on no page: {(noPage.Count > 0 ? string.Join(", ", noPage) : "none")}");
        return 0;
    }
}
