// Maps gone from the Workshop (Workshop.cs): which ones, where that comes from, and what the bridge does about one.
//
// Two sources. broken.json's "removed" list is the curated seed (the two found by hand on 2026-10-20). The daily check (the plugin asks
// the Steam Web API and posts the verdicts to /removed/<secret>) is kept in /data/removed.json, and for every id it has spoken about
// its verdict wins - so a map that comes back is unblocked even when the seed still lists it. A removed map is refused in chat (and from
// the HUD's Play button, which is the same !map), never rolled by the dice, dropped from the queue, and the HUD's browser shows it as
// unavailable. If the daily check finds the map that is ON removed, nobody new could download it, so the dice roll another at once.
using System.Net;
using System.Text;
using System.Text.Json;

sealed class RemovedFile
{
    public string Checked { get; set; } = "";                         // when the last report came in (UTC, ISO 8601)
    public Dictionary<string, string> Verdicts { get; set; } = new(); // workshop id -> ok | removed | hidden | banned, per the last report that named it
}

static partial class Program
{
    static Dictionary<string, string> RemovedSeed = new(); // workshop id -> verdict, broken.json's "removed"
    static RemovedFile Checked = new();                    // the daily check's verdicts, /data/removed.json
    static Dictionary<string, string> Removed = new();     // in force: workshop id -> verdict, for every map nobody can download
    const int ImplausiblePercent = 10;                      // a report calling more than this share of 50+ ids gone is not believed

    static bool IsRemoved(MapInfo m) => !m.Official && Removed.ContainsKey(m.Id.ToString());

    /// <summary>The refusal: neutral, says why, and what to do. The label is clipped so the line always fits.</summary>
    static string RemovedRefusal(MapInfo m) =>
        $"{Clip(Label(m), 44)} {Workshop.Reason(Removed.GetValueOrDefault(m.Id.ToString(), Workshop.Removed))} - nobody can download it. Pick another.";

    static string RemovedPath => Path.Combine(StateDir, "removed.json");

    /// <summary>broken.json's "removed": ids (numbers or strings) or objects with an "id" (and optionally a "verdict"); no list, none.</summary>
    static Dictionary<string, string> ParseRemoved(string json)
    {
        var seed = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("removed", out var list)
            || list.ValueKind != JsonValueKind.Array) return seed;
        foreach (var e in list.EnumerateArray())
        {
            var id = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var i) ? i : e;
            if (id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) || !long.TryParse(JsonText(id), out var n) || n <= 0) continue;
            var verdict = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("verdict", out var v) && v.ValueKind == JsonValueKind.String
                && Workshop.IsVerdict(JsonText(v)) && JsonText(v) != Workshop.Ok ? JsonText(v) : Workshop.Removed;
            seed[n.ToString()] = verdict;
        }
        return seed;
    }

    /// <summary>At start: the seed out of BROKEN_FILE and the last check out of /data. Either may be missing.</summary>
    static void LoadRemoved()
    {
        RemovedSeed = new();
        if (BrokenFile != "" && File.Exists(BrokenFile))
            try { RemovedSeed = ParseRemoved(File.ReadAllText(BrokenFile)); } catch (Exception e) { Log($"removed list: {BrokenFile} unreadable ({e.Message})"); }
        Checked = new();
        if (File.Exists(RemovedPath))
            try { Checked = JsonSerializer.Deserialize(File.ReadAllText(RemovedPath), Ctx.Default.RemovedFile) ?? new(); }
            catch (Exception e) { Log($"removed list: {RemovedPath} unreadable ({e.Message}) - only the seed counts"); }
        RecomputeRemoved();
        Log($"removed list: {Removed.Count} map(s) blocked - {RemovedSeed.Count} in {(BrokenFile == "" ? "(no BROKEN_FILE)" : BrokenFile)}, " +
            $"{Checked.Verdicts.Count} id(s) in the last Workshop check{(Checked.Checked != "" ? $" of {Checked.Checked}" : " (none yet)")}");
    }

    static void SaveRemoved()
    {
        File.WriteAllText(RemovedPath + ".tmp", JsonSerializer.Serialize(Checked, Ctx.Default.RemovedFile));
        File.Move(RemovedPath + ".tmp", RemovedPath, true);
        NoteOwnWrite(RemovedPath); // the watch (Reload.cs) is not to read the bridge's own write back
    }

    static void RecomputeRemoved()
    {
        var r = new Dictionary<string, string>();
        foreach (var (id, v) in RemovedSeed) if (!Checked.Verdicts.ContainsKey(id)) r[id] = v; // the seed, where no check has spoken
        foreach (var (id, v) in Checked.Verdicts) if (v != Workshop.Ok) r[id] = v;
        Removed = r;
    }

    /// <summary>Every Workshop map the server lists: maps.json and the other modes' lists, whose maps the menu browses too.</summary>
    static IEnumerable<MapInfo> AllListed => Maps.Concat(Modes.All.Where(x => !x.ArmsRace).SelectMany(ModeWorkshop));

    /// <summary>What the plugin is to check: every map the server can list or roll (without the non-maps), and the seed.</summary>
    static string CheckList() => Workshop.EncodeIds(AllListed.Where(m => !IsPack(m) && m.Id > 0).Select(m => m.Id)
        .Concat(RemovedSeed.Keys.Select(long.Parse)).Distinct());

    static string NameOf(string id) => AllListed.FirstOrDefault(m => m.Id.ToString() == id) is { } m ? Label(m) : $"workshop {id}";

    /// <summary>A report from the plugin, on the event loop: keep it, log every change, and act on maps that just went - their queued
    /// requests go, a countdown to one stops, and the map that is on, if it is one, makes way for the dice. Returns a line for the plugin's log.</summary>
    static string ApplyRemovedReport(string body)
    {
        var report = Workshop.DecodeReport(body);
        if (report.Count == 0) { Log("workshop check: an empty or unreadable report - ignored"); return "ignored: no verdicts"; }
        var gone = report.Count(r => r.Verdict != Workshop.Ok);
        if (report.Count >= 50 && gone * 100 > report.Count * ImplausiblePercent)
        {
            Log($"workshop check: {gone} of {report.Count} ids not downloadable - implausible (more than {ImplausiblePercent}%), ignored; nothing changes");
            return $"ignored: {gone} of {report.Count} gone is implausible";
        }
        var before = new Dictionary<string, string>(Removed);
        foreach (var (id, v) in report) Checked.Verdicts[id.ToString()] = v;
        Checked.Checked = Now().ToString("o");
        SaveRemoved();
        RecomputeRemoved();
        var newly = Removed.Keys.Where(id => !before.ContainsKey(id)).ToList();
        var back = before.Keys.Where(id => !Removed.ContainsKey(id)).ToList();
        foreach (var id in newly) Log($"workshop check: {NameOf(id)} ({id}) {Workshop.Reason(Removed[id])} - blocked");
        foreach (var id in back) Log($"workshop check: {NameOf(id)} ({id}) is downloadable again - unblocked");
        if (newly.Count > 0 || back.Count > 0) CatalogVersion++;
        if (newly.Count > 0) ActOnRemoved(newly.ToHashSet());
        Log($"workshop check: {report.Count} id(s) checked, {gone} not downloadable; {newly.Count} newly blocked, {back.Count} unblocked, {Removed.Count} blocked in all");
        return $"{report.Count} checked, {gone} not downloadable, {newly.Count} newly blocked, {back.Count} unblocked, {Removed.Count} blocked in all";
    }

    static void ActOnRemoved(HashSet<string> newly)
    {
        foreach (var r in Queue.Where(r => newly.Contains(Key(r.Map))).ToList())
        {
            Queue.Remove(r);
            News($"{Clip(Label(r.Map), 24)} is gone from the Workshop - its request is dropped.");
        }
        if (Pending is { } p && newly.Contains(Key(p.Map)))
        {
            Pending = null; // its countdown callbacks see they are stale and stop
            Announce($"{Clip(Label(p.Map), 44)} {Workshop.Reason(Removed[Key(p.Map)])} - the change is off.");
            Schedule(Now(), direct: true);
        }
        if (Pending == null && !AnnouncePending && S.Map is { } on && newly.Contains(Key(on)))
        {
            if (RandomNext(true, 1) is not { } map) { Log($"workshop check: the map that is on is gone, and no map is eligible to roll"); return; } // RandomQueue.cs
            StartChange(new Change(map, "the dice", []), $"{Clip(Label(on), 40)} {Workshop.Reason(Removed[Key(on)])} - nobody new can download it. Rolling the dice: {Label(map)}{Detail(map)}{RollNote}");
        }
    }

    /// <summary>The plugin's POST of a report. Runs on the event loop like everything else that changes state.</summary>
    static void RemovedReport(HttpListenerContext c)
    {
        var body = new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd();
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { done.TrySetResult(ApplyRemovedReport(body)); } catch (Exception e) { Log("workshop check: " + e); done.TrySetResult("error: " + e.Message); } });
        Reply(c, done.Task.Wait(5000) ? done.Task.Result : "busy: the bridge did not get to it within 5 s");
    }
}
