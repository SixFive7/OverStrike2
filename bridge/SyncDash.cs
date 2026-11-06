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
        var byKey = KnownByKey();
        return Modes.All.Select(m => new SyncWords.RandomQueue(m.Id, LaneOf(m).Count(e => Downloaded(e.Key)), LaneOf(m).Count,
            LaneOf(m).Select(e => new SyncWords.StockMap(e.Key, byKey.TryGetValue(e.Key, out var x) ? x.Name : e.Key, Downloaded(e.Key), byKey.TryGetValue(e.Key, out var y) ? TagsOf(y) : "")).ToList())).ToList();
    }

    /// <summary>Each map's tags by its Workshop id, for the sync's maps the bridge has on a list: "" for the rest.</summary>
    static Func<string, string> TagsById()
    {
        var byKey = KnownByKey();
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
                var built = DashboardPage();
                var page = Path.Combine(WebDir, "index.html");
                if (!File.Exists(page) || File.ReadAllText(page) != built) WriteAtomic(page, built);
                webPage = built; // once it is there: a page the folder refused is tried again at the next rebuild
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

    /// <summary>A file written whole: its readers see the old one or the new one. On Windows - builds and self-tests only - something else can hold
    /// the target for a moment (a scanner, by all signs) and the move fails with "access denied", so it is tried again there, for 2 s at the most. On
    /// Linux a rename does not care who has the file open: one try, and a real failure throws as ever.</summary>
    static void WriteAtomic(string path, string text)
    {
        File.WriteAllText(path + ".tmp", text, new UTF8Encoding(false));
        for (var tries = OperatingSystem.IsWindows() ? 100 : 1; ; tries--)
            try { File.Move(path + ".tmp", path, true); return; }
            catch (Exception e) when (tries > 1 && e is IOException or UnauthorizedAccessException) { Thread.Sleep(20); }
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
