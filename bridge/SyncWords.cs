// The sync's words for the website and the HUD's Sync page, from the status file the sync writes (SyncDash.cs reads it).
using System.Globalization;
using System.Text;
using System.Text.Json;

/// <summary>The dashboard's words and the website's data, out of the sync's file and the bridge's stock: a pure function of its input and a clock,
/// so --selftest can hold every word to it.</summary>
static class SyncWords
{
    /// <summary>A lane of the stock: its mode, its maps on the server's disk by the watcher's report, its length, and its entries.</summary>
    public sealed record StockLane(string Name, int Ready, int Total, List<StockMap>? Maps = null);

    /// <summary>A stock entry: its Workshop id, its name, whether it is on the disk, its tags.</summary>
    public sealed record StockMap(string Id, string Name, bool Ready, string Tags);

    /// <summary>What the words are made of: the sync's file, the stock, the watcher's background download, and each map's tags by id.</summary>
    public sealed record Input(JsonElement? Status, DateTime ReadAt, string Problem, List<StockLane> Stock, bool StockKnown, DateTime ReportAt, int StockLength, DateTime Now,
        Program.Download? Background = null, Func<string, string>? Tags = null);

    /// <summary>A map's name with its first "why" tag in words after it: "Lake (liked by us)".</summary>
    static string NameWithWhy(JsonElement m, Input input) =>
        MapName(m) + (input.Tags?.Invoke(Str(m, "id")) is { } t && MapTags.WhyWords(t) is { Length: > 0 } w ? $" ({w})" : "");

    /// <summary>The sync's file is old after this long without a new "updated": it writes one at least every minute, every 15 s in a run.</summary>
    public const int OldMinutes = 15;

    /// <summary>Rows on a page of each list: the layout has exactly this many (hud/layout, the Sync page).</summary>
    public const int QueueRows = 6, RunRows = 3, MapRows = 8;

    public const string QueueList = HudDash.QueueList, RunList = HudDash.RunList, MapList = HudDash.MapList;

    /// <summary>The sync's states as a player reads them, and which of the page's four looks each takes: busy, waiting, idle or stopped.</summary>
    public static readonly (string State, string Words, string Look)[] States =
    [
        ("idle", "Idle", "idle"),
        ("waiting", "Waiting", "wait"),
        ("discovery", "Finding new maps", "busy"),
        ("check", "Checking the Workshop", "busy"),
        ("ratings", "Fetching the Workshop's ratings", "busy"),
        ("serve", "Fetching maps for the server", "busy"),
        ("download", "Downloading new maps", "busy"),
        ("test", "Testing new maps", "busy"),
        ("publish", "Listing the new maps", "busy"),
        ("stopped", "Stopped", "stop"),
    ];

    public static readonly string[] Looks = ["busy", "wait", "idle", "stop"];

    /// <summary>A map event as a player reads it, and its row's colour (HudDash.Tones).</summary>
    public static readonly (string Event, string Words, string Tone)[] Events =
    [
        ("listed", "Listed", "ok"),
        ("handed-over", "To the server", "ok"),
        ("changed", "Updated", "hot"),
        ("broken", "Broken", "bad"),
        ("failed", "Failed", "bad"),
        ("removed", "Removed", "dim"),
    ];

    // ---- reading the file: every value may be missing, null or of another kind ----

    public static JsonElement? Get(JsonElement? e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty(p, out var next)) return null;
            e = next;
        }
        return e is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } ? e : null;
    }

    public static double? Num(JsonElement? e, params string[] path) => Get(e, path) is { ValueKind: JsonValueKind.Number } n && n.TryGetDouble(out var d) ? d
        : Get(e, path) is { ValueKind: JsonValueKind.String } s && double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null;

    public static string Str(JsonElement? e, params string[] path) => Get(e, path) is { } v ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        _ => "",
    } : "";

    public static IEnumerable<JsonElement> Items(JsonElement? e, params string[] path) =>
        Get(e, path) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList() : [];

    /// <summary>A per-mode count: a number, or an object that carries one (count or total).</summary>
    static double? ModeCount(JsonElement? e, string mode) => Num(e, mode) ?? Num(e, mode, "count") ?? Num(e, mode, "total");

    // ---- the words' pieces ----

    static string N(double n) => ((long)Math.Round(n)).ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Bytes as the menu shows sizes: MB below a gigabyte, GB with one decimal above (binary, like the Maps page).</summary>
    public static string Bytes(double bytes) => bytes < 1024d * 1024 * 1024 ? $"{N(bytes / (1024d * 1024))} MB" : $"{(bytes / (1024d * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture)} GB";

    /// <summary>A map's size from the file's sizeMb, as a card shows it.</summary>
    public static string SizeMb(double? mb) => mb is not { } m || m <= 0 ? "" : m >= 1000 ? $"{(m / 1000).ToString("0.0", CultureInfo.InvariantCulture)} GB" : $"{N(m)} MB";

    public static string ModeName(string id) => Modes.Find(id)?.Name ?? "";

    static DateTime Local(double unix, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UnixEpoch.AddSeconds(unix), zone);

    /// <summary>A moment: the time alone today, the weekday before it within a week, the date otherwise.</summary>
    public static string When(double unix, DateTime nowUtc, TimeZoneInfo zone)
    {
        var t = Local(unix, zone);
        var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date;
        var hm = t.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (t.Date == today) return hm;
        if (t.Date == today.AddDays(1)) return $"tomorrow {hm}";
        if (t.Date == today.AddDays(-1)) return $"yesterday {hm}";
        return t.ToString("ddd d MMM", CultureInfo.InvariantCulture) + " " + hm;
    }

    /// <summary>How long ago, coarse on purpose: the block changes once a minute for it, not every second.</summary>
    public static string Ago(TimeSpan t) => t.TotalSeconds < 60 ? "less than a minute ago" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min ago"
        : t.TotalHours < 48 ? $"{(int)t.TotalHours} h {(int)t.TotalMinutes % 60} min ago" : $"{(int)t.TotalDays} days ago";

    static string Upper(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    static string Join(params string[] parts) => string.Join("  ·  ", parts.Where(p => p != ""));

    static string MapName(JsonElement m) => Str(m, "name") is { Length: > 0 } name ? name : Str(m, "id") is { Length: > 0 } id ? $"Workshop item {id}" : "a map";

    // ---- the menu's Sync page ----

    /// <summary>The block for the menu's Sync page: the Now strip's words, the queue's words and its next maps, the runs and the map events,
    /// newest first; the look of the state, the progress bar's step (osdp0 ... osdp100, 5 % a step) and whether the file is old or missing.</summary>
    public static HudDash Dash(Input input, TimeZoneInfo zone)
    {
        var d = new HudDash();
        var st = input.Status;
        var now = input.Now;
        void W(string name, string text) => d.Words[name] = text;
        void C(string name, bool on) => d.Classes[name] = on;

        // -- the file itself: present, its age, old --
        var updated = Num(st, "updated");
        var age = updated is { } u ? now - DateTime.UnixEpoch.AddSeconds(u) : (TimeSpan?)null;
        var old = st == null || age is not { } a || a.TotalMinutes >= OldMinutes;
        W("osdage", st == null ? (input.Problem != "" ? $"No word from the sync: {input.Problem}" : "No word from the sync yet")
            : age is not { } ag ? "The sync's file says nothing of when it was written"
            : old ? $"Old: the sync last wrote {Ago(ag)} - It may be down"
            : $"Updated {Ago(ag)}" + (input.Problem != "" ? $" (the newest file was not taken: {input.Problem})" : ""));
        C("osdnone", st == null);
        C("osdold", st != null && old);

        // -- now --
        var state = Str(st, "now", "state");
        var known = States.FirstOrDefault(s => s.State == state);
        var look = st == null ? "" : known.State != null ? known.Look : state == "" ? "idle" : "busy";
        foreach (var l in Looks) C($"osd{l}", look == l);
        W("osdstate", st == null ? "No word from the sync" : known.State != null ? known.Words : state == "" ? "Idle" : Upper(state));
        W("osdwhy", st == null ? "The Workshop sync finds new maps, tests them on a test server and lists the good ones. Its news shows here." : Str(st, "now", "why"));
        var map = Get(st, "now", "map");
        W("osdmap", map is { } m ? Join(MapName(m), ModeName(Str(m, "mode")), SizeMb(Num(m, "sizeMb"))) : "");
        C("osdmapon", map != null);
        var (done, total) = (Num(st, "now", "step", "done"), Num(st, "now", "step", "total"));
        var step = done is { } dn && total is { } tt && tt > 0 ? Math.Clamp(dn / tt, 0, 1) : (double?)null;
        W("osdstep", step != null ? $"{N(done!.Value)} of {N(total!.Value)}" : "");
        C("osdbar", step != null);
        var notch = step is { } sp ? (int)Math.Round(sp * 20) * 5 : -1;
        for (var p = 0; p <= 100; p += 5) C($"osdp{p}", notch == p);
        var run = Get(st, "now", "run");
        W("osdrun", run is { } r ? Join($"Run since {When(Num(r, "started") ?? 0, now, zone)}", Str(r, "kind") == "manual" ? "started by hand" : "automatic",
            Num(r, "deadline") is { } dl ? $"ends by {When(dl, now, zone)} at the latest" : "") : st == null ? "" : "No run at the moment");
        var today = Get(st, "today");
        W("osdtoday", today == null ? "" : "Today: " + Join($"{N(Num(today, "downloads") ?? 0)} downloads, {Bytes(Num(today, "bytes") ?? 0)}",
            $"{N(Num(today, "pass") ?? 0)} passed, {N(Num(today, "rejected") ?? 0)} rejected", $"{N(Num(today, "failedDownloads") ?? 0)} failed",
            $"{N(Num(today, "apiCalls") ?? 0)} API calls") + (Str(today, "stop") is { Length: > 0 } stop ? $"  ·  Steam stopped them: {stop}" : ""));
        var totals = Get(st, "totals");
        W("osdlisted", totals == null ? "" : "Listed: " + Join(Modes.All.Select(md => $"{md.Name} {N(ModeCount(Get(totals, "listed"), md.Id) ?? 0)}").ToArray()));
        W("osdtested", totals == null ? "" : $"Tested so far: {N(Num(totals, "tested") ?? 0)}  ·  {N(Num(totals, "pass") ?? 0)} passed, {N(Num(totals, "broken") ?? 0)} broken, {N(Num(totals, "nonmap") ?? 0)} not maps");

        // -- the queue --
        var queue = Get(st, "queue");
        var waiting = Num(queue, "total") ?? 0;
        W("osdqhead", queue == null ? "No queue yet" : waiting == 0 ? "Nothing waiting: every map found so far is tested" : $"{N(waiting)} maps waiting  ·  {Bytes(Num(queue, "bytes") ?? 0)}");
        W("osdqmodes", queue == null ? "" : Join(Modes.All.Select(md => $"{md.Name} {N(ModeCount(Get(queue, "byMode"), md.Id) ?? 0)}").ToArray()));
        W("osdqeta", queue == null || waiting == 0 ? "" : Num(queue, "eta") is { } eta ? $"All tested by about {When(eta, now, zone)}" : "No finish estimate yet");
        var serve = Get(queue, "serve");
        var missing = Num(serve, "missing") ?? 0;
        var handed = Num(serve, "handedOver") ?? 0;
        W("osdqserve", serve == null ? "" : Join(missing > 0 ? $"The server lacks {N(missing)} maps it keeps ({Bytes(Num(serve, "bytes") ?? 0)}): those come first" : "The server has every map it keeps",
            handed > 0 ? $"{N(handed)} handed over for the next restart" : ""));
        // the stock's line, and while the restart watcher downloads in the background, that download first: one line that fits beside the queue (the
        // layout wraps it once at most)
        var lanes = input.Stock.Select(q => $"{LaneName(q.Name)} {(input.StockKnown ? $"{q.Ready}/{q.Total}" : $"{q.Total}")}").ToArray();
        var stock = input.Stock.Count == 0 ? "" : input.StockKnown ? "Stock on disk for the ballots: " + Join(lanes) : $"Discovery stock: {Join(lanes)}  ·  disk not reported";
        W("osdqrand", input.Background is { } bg ? $"{BackgroundWords(bg)}{(input.Stock.Count == 0 ? "" : input.StockKnown ? $"  ·  stock on disk {input.Stock.Sum(q => q.Ready)} of {input.Stock.Sum(q => q.Total)}" : "")}"
            : stock); // the contents before v4.6.0: the background download folded into the stock's line
        W("osdqstock", stock);                                                                  // from v4.6.0: the stock's line...
        W("osdqbg", input.Background is { } running ? BackgroundWords(running) : "");             // ...and the background download on a row of its own
        C("osdqbg", input.Background != null);
        var next = Items(queue, "next").ToList();
        d.Lists.Add(new HudDash.DashList(QueueList, QueueRows, next.Select((q, i) => new HudDash.Row("",
            [(i + 1).ToString(CultureInfo.InvariantCulture), NameWithWhy(q, input), Join(ModeName(Str(q, "mode")), SizeMb(Num(q, "sizeMb"))), Upper(Str(q, "why"))])).ToList()));
        W("osdqshown", next.Count == 0 ? "" : next.Count < waiting ? $"The next {N(next.Count)} of {N(waiting)}" : $"All {N(next.Count)}");
        W("osdqnone", queue == null ? "" : "Nothing in the queue");

        // -- the history, newest first --
        var runs = Items(st, "history", "runs").OrderByDescending(x => Num(x, "started") ?? 0).ToList();
        d.Lists.Add(new HudDash.DashList(RunList, RunRows, runs.Select(x => new HudDash.Row(Num(x, "ended") == null ? "hot" : Str(x, "stop") != "" ? "dim" : "", RunRow(x, now, zone))).ToList()));
        W("osdrnone", "No runs yet");
        var events = Items(st, "history", "maps").OrderByDescending(x => Num(x, "at") ?? 0).ToList();
        d.Lists.Add(new HudDash.DashList(MapList, MapRows, events.Select(x =>
        {
            var ev = Events.FirstOrDefault(e => e.Event == Str(x, "event"));
            return new HudDash.Row(ev.Tone ?? "", [When(Num(x, "at") ?? 0, now, zone), ev.Words ?? Upper(Str(x, "event")), Join(NameWithWhy(x, input), ModeName(Str(x, "mode"))), Str(x, "why")]);
        }).ToList()));
        W("osdmnone", "No map news yet");
        return d;
    }

    /// <summary>A run as its row shows it: when and how it ran, what it did, why it ended.</summary>
    public static string[] RunRow(JsonElement x, DateTime now, TimeZoneInfo zone)
    {
        var started = Num(x, "started") ?? 0;
        var ended = Num(x, "ended");
        var minutes = ((ended ?? (now - DateTime.UnixEpoch).TotalSeconds) - started) / 60;
        var span = $"{When(started, now, zone)} - {(ended is { } e ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UnixEpoch.AddSeconds(e), zone).ToString("HH:mm", CultureInfo.InvariantCulture) : "now")}";
        var length = minutes >= 60 ? $"{(int)(minutes / 60)} h {(int)minutes % 60} min" : $"{Math.Max(0, (int)minutes)} min";
        return [Join(span, length, Str(x, "kind") == "manual" ? "by hand" : "automatic"),
            Join($"{N(Num(x, "downloads") ?? 0)} downloads ({Bytes(Num(x, "bytes") ?? 0)})", $"{N(Num(x, "pass") ?? 0)} passed, {N(Num(x, "rejected") ?? 0)} rejected",
                $"{N(Num(x, "listed") ?? 0)} listed, {N(Num(x, "removed") ?? 0)} removed"),
            ended == null ? "Running now" : Str(x, "stop") is { Length: > 0 } stop ? $"Stopped: {stop}" : "Finished"];
    }

    /// <summary>A stock lane's name as a player reads it: its mode's.</summary>
    public static string LaneName(string lane) => ModeName(lane) is { Length: > 0 } name ? name : lane;

    /// <summary>The background download in words: "Background download: Lake Aim, 63%" - its title, else its Workshop id.</summary>
    public static string BackgroundWords(Program.Download d) =>
        $"Background download: {(d.Title != "" ? (d.Title.Length <= 36 ? d.Title : d.Title[..35] + "~") : $"Workshop item {d.Id}")}{(d.Percent is { } p ? $", {p}%" : ", starting")}";

    // ---- the website's data ----

    /// <summary>dashboard.json without its "generated" stamp (WebStamped adds it): the page's words, the stock with its maps, the background
    /// download, the tags' table and the tags of every map the page names, and the sync's own file as it was read - the page draws the full lists
    /// from that.</summary>
    public static string Web(Input input, HudDash dash, TimeZoneInfo zone)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("zone", zone.Id);
            w.WriteNumber("oldMinutes", OldMinutes);
            w.WriteStartObject("words");
            foreach (var (name, text) in dash.Words) w.WriteString(name[HudDash.Prefix.Length..], text);
            w.WriteEndObject();
            w.WriteStartObject("looks");
            foreach (var (name, on) in dash.Classes.Where(c => !c.Key.StartsWith("osdp", StringComparison.Ordinal))) w.WriteBoolean(name[HudDash.Prefix.Length..], on);
            w.WriteEndObject();
            w.WriteStartObject("random");
            w.WriteBoolean("known", input.StockKnown);
            if (input.ReportAt != default) w.WriteNumber("reportAt", (long)(input.ReportAt - DateTime.UnixEpoch).TotalSeconds);
            w.WriteNumber("length", input.StockLength);
            w.WriteStartArray("queues");
            foreach (var q in input.Stock)
            {
                w.WriteStartObject();
                w.WriteString("name", q.Name);
                w.WriteString("label", LaneName(q.Name));
                w.WriteNumber("ready", q.Ready);
                w.WriteNumber("total", q.Total);
                w.WriteStartArray("maps");
                foreach (var m in q.Maps ?? [])
                {
                    w.WriteStartObject();
                    w.WriteString("id", m.Id);
                    w.WriteString("name", m.Name);
                    w.WriteBoolean("ready", m.Ready);
                    w.WriteString("tags", m.Tags);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            if (input.Background is { } bg)
            {
                w.WriteStartObject("background");
                w.WriteString("id", bg.Id);
                w.WriteString("title", bg.Title);
                if (bg.Percent is { } p) w.WriteNumber("percent", p); else w.WriteNull("percent");
                w.WriteString("words", BackgroundWords(bg));
                w.WriteEndObject();
            }
            w.WriteStartArray("tags");
            foreach (var t in MapTags.All)
            {
                w.WriteStartObject();
                w.WriteString("id", t.Id); w.WriteString("chip", t.Chip); w.WriteString("words", t.Words); w.WriteString("family", t.Family); w.WriteString("colour", t.Colour);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("mapTags");
            var ids = Items(input.Status, "queue", "next").Concat(Items(input.Status, "history", "maps")).Select(x => Str(x, "id")).Where(x => x != "").Distinct();
            foreach (var id in ids) if (input.Tags?.Invoke(id) is { Length: > 0 } tags) w.WriteString(id, tags);
            w.WriteEndObject();
            if (input.Problem != "") w.WriteString("problem", input.Problem);
            if (input.ReadAt != default) w.WriteNumber("readAt", (long)(input.ReadAt - DateTime.UnixEpoch).TotalSeconds);
            w.WritePropertyName("sync");
            if (input.Status is { } st) st.WriteTo(w); else w.WriteNullValue();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The data with the moment it was written, first.</summary>
    public static string WebStamped(string json, DateTime now) => "{\"generated\":" + ((long)(now - DateTime.UnixEpoch).TotalSeconds).ToString(CultureInfo.InvariantCulture) + "," + json[1..];
}
