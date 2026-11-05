// The Workshop sync's dashboard: a full status dashboard for the sync, in game for everybody and as a simple website, showing what it is doing
// now, what is on its queue and what it has done.
//
// The sync writes /data/sync-status.json (schema 1: now, today, totals, queue, keep, history; atomically, at every phase change, every 15 s in
// a run and every 60 s otherwise). The bridge reads it whenever it changed (every StatusLookSeconds), adds what only it knows - the discovery
// stock and how many of its maps the restart watcher reports on the server's disk, the background download the watcher runs (RandomQueue.cs),
// and each map's tags (MapTags) - and makes two things of it:
//   - the menu's Sync page: the block the plugin pages and passes through (HudDash.cs), on GET /dash/<secret>, its version in the live state;
//   - the website: /data/web/index.html (dashboard.html, built in) and /data/web/dashboard.json, which a static web server behind a login
//     serves - so nothing of the bridge's own HTTP port is ever reachable from outside.
// A file that is missing, does not parse or stops changing is shown as such, with its age: the last good one stays on screen meanwhile.
// Every word is written here; SyncWords holds them, --selftest (SelfTestDash.cs) drives them on a handed clock.
using System.Globalization;
using System.Text;
using System.Text.Json;

static partial class Program
{
    const string DashPathPrefix = "/dash/";
    public const int StatusLookSeconds = 2, DashBuildSeconds = 5, WebHeartbeatSeconds = 60;

    static string SyncStatusPath => Path.Combine(StateDir, "sync-status.json");
    static string WebDir => Path.Combine(StateDir, "web");

    static JsonDocument? SyncDoc;                 // the last sync-status.json that parsed
    static Stamp? SyncStamp;                      // its size and write time, as last looked at
    static DateTime SyncReadAt;                   // when it was read (the bridge's clock)
    static string SyncProblem = "";               // why the newest one was not taken ("" when it was)
    static volatile string DashText = new HudDash().Encode();
    static long DashVersion = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond; // new with every bridge, so a plugin always fetches after a restart
    static string dashBody = "", webWritten = "", webPage = "";
    static DateTime webWrittenAt;
    static int syncTicks;
    static bool SyncOn = true;                    // --selftest switches the file and the web folder off unless a test asks

    /// <summary>From Tick, once a second: the file looked at every StatusLookSeconds, the block rebuilt every DashBuildSeconds (or at once after a
    /// new file), the website's files written when they changed and every WebHeartbeatSeconds anyway.</summary>
    static void SyncTick(DateTime now)
    {
        if (!SyncOn) return;
        syncTicks++;
        var fresh = syncTicks % StatusLookSeconds == 0 && ReadSyncStatus();
        if (fresh || syncTicks % DashBuildSeconds == 0 || syncTicks == 1) RebuildDash(now);
    }

    /// <summary>sync-status.json again when its size or write time moved. True when a new one was taken. The writer replaces it atomically, so
    /// a file that does not parse is a broken one, not a half-written one: it is noted and the last good one kept.</summary>
    static bool ReadSyncStatus()
    {
        var stamp = StampOf(SyncStatusPath);
        if (stamp == SyncStamp) return false;
        SyncStamp = stamp;
        if (stamp == null) { SyncProblem = SyncDoc == null ? "" : "the file is gone"; return false; }
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllBytes(SyncStatusPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { doc.Dispose(); SyncProblem = "the file is not a JSON object"; return false; }
            var schema = SyncWords.Num(doc.RootElement, "schema");
            if (schema is not 1) { doc.Dispose(); SyncProblem = $"schema {schema?.ToString(CultureInfo.InvariantCulture) ?? "missing"}, not 1"; return false; }
            SyncDoc?.Dispose();
            (SyncDoc, SyncReadAt, SyncProblem) = (doc, Now(), "");
            return true;
        }
        catch (Exception e)
        {
            SyncProblem = "the file does not parse: " + Clip(e.Message, 80);
            return false;
        }
    }

    /// <summary>The stock as the dashboard shows it: a lane per mode, its entries the watcher's last report names on the disk out of its length, and
    /// each entry with its name and tags.</summary>
    static List<SyncWords.RandomQueue> StockNow()
    {
        var byKey = AllKnown.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        return Modes.All.Select(m => new SyncWords.RandomQueue(m.Id, LaneOf(m).Count(e => Downloaded(e.Key)), LaneOf(m).Count,
            LaneOf(m).Select(e => new SyncWords.StockMap(e.Key, byKey.TryGetValue(e.Key, out var x) ? x.Name : e.Key, Downloaded(e.Key), byKey.TryGetValue(e.Key, out var y) ? TagsOf(y) : "")).ToList())).ToList();
    }

    /// <summary>Each map's tags by its Workshop id, for the sync's maps the bridge has on a list: "" for the rest.</summary>
    static Func<string, string> TagsById()
    {
        var byKey = AllKnown.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        return id => byKey.TryGetValue(id, out var m) ? TagsOf(m) : "";
    }

    static void RebuildDash(DateTime now)
    {
        var input = new SyncWords.Input(SyncDoc?.RootElement, SyncReadAt, SyncProblem, StockNow(), ReadyKnown, ReadyAt, StockSize, now, BackgroundNow(), TagsById());
        var dash = SyncWords.Dash(input, DashZone);
        var body = dash.Encode();
        if (body != dashBody)
        {
            dashBody = body;
            DashVersion++;
            dash.Version = DashVersion;
            DashText = dash.Encode();
        }
        WriteWeb(input, dash, now);
    }

    /// <summary>The website's two files in /data/web, each replaced in one step. The page when it differs from the one built in; the data when it
    /// changed, and every WebHeartbeatSeconds so its "generated" says the bridge is alive.</summary>
    static void WriteWeb(SyncWords.Input input, HudDash dash, DateTime now)
    {
        try
        {
            Directory.CreateDirectory(WebDir);
            if (webPage == "")
            {
                webPage = DashboardPage();
                var page = Path.Combine(WebDir, "index.html");
                if (!File.Exists(page) || File.ReadAllText(page) != webPage) WriteAtomic(page, webPage);
            }
            var json = SyncWords.Web(input, dash, DashZone);
            var same = json == webWritten;
            if (same && (now - webWrittenAt).TotalSeconds < WebHeartbeatSeconds) return;
            WriteAtomic(Path.Combine(WebDir, "dashboard.json"), SyncWords.WebStamped(json, now));
            (webWritten, webWrittenAt) = (json, now);
        }
        catch (Exception e)
        {
            if ((now - webWrittenAt).TotalSeconds >= WebHeartbeatSeconds) { Log("web: " + Clip(e.Message, 160)); webWrittenAt = now; }
        }
    }

    static void WriteAtomic(string path, string text)
    {
        File.WriteAllText(path + ".tmp", text, new UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
    }

    /// <summary>The website's page, built into the bridge (dashboard.html), so a bridge update brings its page along.</summary>
    static string DashboardPage()
    {
        using var s = typeof(Program).Assembly.GetManifestResourceStream("dashboard.html");
        return s == null ? "<!doctype html><title>Over Strike 2</title><p>The dashboard page is missing from this bridge build." : new StreamReader(s, Encoding.UTF8).ReadToEnd();
    }

    /// <summary>The time zone the words give times in: the container's own (TZ), UTC in --selftest.</summary>
    static TimeZoneInfo DashZone = TimeZoneInfo.Local;

    /// <summary>For /health: what the bridge has of the sync's file.</summary>
    static string SyncHealth()
    {
        if (SyncDoc == null) return $"sync status: {(SyncProblem != "" ? SyncProblem : $"no {SyncStatusPath} yet")}; dash version {DashVersion}";
        var updated = SyncWords.Num(SyncDoc.RootElement, "updated");
        var state = SyncWords.Str(SyncDoc.RootElement, "now", "state");
        return $"sync status: {state}, updated {(updated is { } u ? $"{(Now() - DateTime.UnixEpoch.AddSeconds(u)).TotalSeconds:0} s ago" : "at no time it says")}"
            + $"{(SyncProblem != "" ? $"; the newest file was not taken: {SyncProblem}" : "")}; dash version {DashVersion}, web {(webWrittenAt == default ? "not written" : $"written {(Now() - webWrittenAt).TotalSeconds:0} s ago")}";
    }
}

/// <summary>The dashboard's words and the website's data, out of the sync's file and the bridge's stock: a pure function of its input and a clock,
/// so --selftest can hold every word to it.</summary>
static class SyncWords
{
    /// <summary>A lane of the stock: its mode, its maps on the server's disk by the watcher's report, its length, and its entries.</summary>
    public sealed record RandomQueue(string Name, int Ready, int Total, List<StockMap>? Maps = null);

    /// <summary>A stock entry: its Workshop id, its name, whether it is on the disk, its tags.</summary>
    public sealed record StockMap(string Id, string Name, bool Ready, string Tags);

    /// <summary>What the words are made of: the sync's file, the stock (Random), the watcher's background download, and each map's tags by id.</summary>
    public sealed record Input(JsonElement? Status, DateTime ReadAt, string Problem, List<RandomQueue> Random, bool RandomKnown, DateTime RandomAt, int RandomLength, DateTime Now,
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
            : old ? $"Old: the sync last wrote {Ago(ag)} - it may be down"
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
        var rand = input.Random.Select(q => $"{RandomName(q.Name)} {(input.RandomKnown ? $"{q.Ready}/{q.Total}" : $"{q.Total}")}").ToArray();
        W("osdqrand", input.Background is { } bg ? $"{BackgroundWords(bg)}{(input.Random.Count == 0 ? "" : input.RandomKnown ? $"  ·  stock on disk {input.Random.Sum(q => q.Ready)} of {input.Random.Sum(q => q.Total)}" : "")}"
            : input.Random.Count == 0 ? "" : input.RandomKnown ? "Stock on disk for the ballots: " + Join(rand)
            : $"Discovery stock: {Join(rand)}  ·  disk not reported");
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
    public static string RandomName(string lane) => ModeName(lane) is { Length: > 0 } name ? name : lane;

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
            w.WriteBoolean("known", input.RandomKnown);
            if (input.RandomAt != default) w.WriteNumber("reportAt", (long)(input.RandomAt - DateTime.UnixEpoch).TotalSeconds);
            w.WriteNumber("length", input.RandomLength);
            w.WriteStartArray("queues");
            foreach (var q in input.Random)
            {
                w.WriteStartObject();
                w.WriteString("name", q.Name);
                w.WriteString("label", RandomName(q.Name));
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
