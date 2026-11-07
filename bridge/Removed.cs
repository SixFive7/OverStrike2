// Maps gone from the Workshop (Workshop.cs): which ones, where that comes from, and what the bridge does about one.
//
// One source: the Workshop sync asks the Steam Web API every 4 hours and posts its verdicts to /removed/<secret> (BridgeWire.RemovedBody: "id\tverdict"
// a line after "os2-removed\t1"); they are kept in /data/removed.json.
// A removed map is refused (Play, chat), never rolled, leaves an open ballot, stops a countdown to it, and the HUD's browser shows it as
// unavailable. If the map that is ON is removed, nobody new could download it, so the dice roll another at once (AutoRoll).
using System.Globalization;
using System.Net;
using System.Text.Json;

sealed class RemovedFile
{
    public string Checked { get; set; } = "";                         // when the last report came in (UTC, ISO 8601)
    public Dictionary<string, string> Verdicts { get; set; } = new(); // workshop id -> ok | removed | hidden | banned, per the last report that named it
}

static partial class Program
{
    static RemovedFile WorkshopCheck = new();              // the sync's last check: its verdicts and when, /data/removed.json
    static Dictionary<string, string> Removed = new();     // in force: workshop id -> verdict, for every map nobody can download
    const int ImplausiblePercent = 10;                      // a report calling more than this share of 50+ ids gone is not believed

    static bool IsRemoved(MapInfo m) => !m.Official && Removed.ContainsKey(m.Id.ToString());

    /// <summary>The refusal: neutral, says why, and what to do. The label is clipped so the line always fits.</summary>
    static string RemovedRefusal(MapInfo m) =>
        M.Refused($"{M.A(Clip(Label(m), 44))} {Workshop.Reason(Removed.GetValueOrDefault(m.Id.ToString(), Workshop.Removed))} - Nobody can download it. Pick another.");

    static string RemovedPath => Path.Combine(StateDir, "removed.json");

    /// <summary>At start: the sync's last verdicts out of /data. Missing: nothing is gone from the Workshop until its first report.</summary>
    static void LoadRemoved()
    {
        WorkshopCheck = new();
        if (File.Exists(RemovedPath))
            try { WorkshopCheck = JsonSerializer.Deserialize(File.ReadAllText(RemovedPath), Ctx.Default.RemovedFile) ?? new(); }
            catch (Exception e) { Log($"removed list: {RemovedPath} unreadable ({e.Message}) - nothing counts as gone until the next report"); }
        RecomputeRemoved();
        Log($"removed list: {Removed.Count} map(s) blocked - {WorkshopCheck.Verdicts.Count} id(s) in the last Workshop check{(WorkshopCheck.Checked != "" ? $" of {WorkshopCheck.Checked}" : " (none yet)")}");
    }

    static void SaveRemoved()
    {
        WriteAtomic(RemovedPath, JsonSerializer.Serialize(WorkshopCheck, Ctx.Default.RemovedFile));
        NoteOwnWrite(RemovedPath); // the watch (Reload.cs) is not to read the bridge's own write back
    }

    static void RecomputeRemoved() => Removed = WorkshopCheck.Verdicts.Where(kv => kv.Value != Workshop.Ok).ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>The sync's report out of its body: lines with a positive id and a known verdict (Workshop.cs), the last one per id; anything else is
    /// dropped, and a body that is no report in version 1 has none.</summary>
    static List<(long Id, string Verdict)> DecodeRemovedReport(string? body)
    {
        var lines = (body ?? "").Replace("\r", "").Split('\n');
        if (lines[0] != BridgeWire.Head(BridgeWire.RemovedWord)) return [];
        var by = new Dictionary<long, string>();
        foreach (var f in lines.Skip(1).Select(l => l.Split('\t')))
            if (f.Length == 2 && long.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 && Workshop.IsVerdict(f[1])) by[id] = f[1];
        return by.Select(x => (x.Key, x.Value)).ToList();
    }

    /// <summary>A report from the Workshop sync, on the event loop: keep it, log every change, and act on maps that just went (AfterListChange).
    /// Returns a line for the sync's log.</summary>
    static string ApplyRemovedReport(string body)
    {
        var report = DecodeRemovedReport(body);
        if (report.Count == 0) { Log("workshop check: an empty or unreadable report - ignored"); return "ignored: no verdicts"; }
        var gone = report.Count(r => r.Verdict != Workshop.Ok);
        if (report.Count >= 50 && gone * 100 > report.Count * ImplausiblePercent)
        {
            Log($"workshop check: {gone} of {report.Count} ids not downloadable - implausible (more than {ImplausiblePercent}%), ignored; nothing changes");
            return $"ignored: {gone} of {report.Count} gone is implausible";
        }
        var before = new Dictionary<string, string>(Removed);
        foreach (var (id, v) in report) WorkshopCheck.Verdicts[id.ToString()] = v;
        WorkshopCheck.Checked = Now().ToString("o");
        SaveRemoved();
        RecomputeRemoved();
        var newly = Removed.Keys.Where(id => !before.ContainsKey(id)).ToList();
        var back = before.Keys.Where(id => !Removed.ContainsKey(id)).ToList();
        foreach (var id in newly) Log($"workshop check: {NameOf(id)} ({id}) {Workshop.Reason(Removed[id])} - blocked");
        foreach (var id in back) Log($"workshop check: {NameOf(id)} ({id}) is downloadable again - unblocked");
        if (newly.Count > 0 || back.Count > 0) CatalogVersion++;
        if (newly.Count > 0) ActOnUnplayable(newly.ToHashSet());
        Log($"workshop check: {report.Count} id(s) checked, {gone} not downloadable; {newly.Count} newly blocked, {back.Count} unblocked, {Removed.Count} blocked in all");
        return $"{report.Count} checked, {gone} not downloadable, {newly.Count} newly blocked, {back.Count} unblocked, {Removed.Count} blocked in all";
    }

    /// <summary>Maps that have just become unplayable (gone from the Workshop, blocked, found to be no map - one check, packs
    /// included): their cards leave an open ballot, a countdown to one stops, and the map that is on, if it has just gone from the Workshop, makes
    /// way for the dice - nobody new could download it.</summary>
    static void ActOnUnplayable(HashSet<string> newly)
    {
        if (Ballot is { Open: true } b && DropUnplayable(b) > 0) { ExportQueues(); BallotTick(Now()); }
        if (Pending is { } p && newly.Contains(Key(p.Map)) && Unplayable(p.Map) is { } why)
        {
            Pending = null; // its countdown callbacks see they are stale and stop
            if (Ballot is { Open: false } decided && decided.Change == p) Ballot = null; // everybody unfreezes
            Announce($"{Clip(why, 120)} - The change is off.");
        }
        if (Pending == null && !LoadPending && Ballot is not { Open: true } && S.Map is { } on && IsRemoved(on) && newly.Contains(Key(on))
            && !AutoRoll($"{Clip(Label(on), 40)} {Workshop.Reason(Removed[Key(on)])} - Nobody new can download it"))
            Log("workshop check: the map that is on is gone, and no map is eligible to roll");
    }

    /// <summary>The sync's POST of a report. Runs on the event loop like everything else that changes state.</summary>
    static void RemovedReport(HttpListenerContext c) => OnLoop(c, ApplyRemovedReport, "workshop check");
}
