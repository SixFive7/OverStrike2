// A run's publish: the verdicts into the bridge's files, the check's verdicts to the bridge, workshop.json, the reload.
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OverStrike2.Core;

namespace OverStrike2.Sync;

public sealed partial class SyncRun
{
    public void Publish(List<Verdicts.Outcome> outcomes, IReadOnlyList<(string Id, string Verdict)> removedNow, IReadOnlyDictionary<string, JsonObject> details)
    {
        var pub = new JsonObject();
        Report["published"] = pub;
        BridgeData files;
        try
        {
            files = BridgeData.Read(Cfg.BridgeData);          // read again: anything may have changed during the run
        }
        catch (Exception e) when (e is BadDataException or IOException or UnauthorizedAccessException)
        {
            pub["skipped"] = $"the bridge's data cannot be read ({e.Message})";
            return;
        }
        var (texts, summary) = PublishPlan.Of(files, State, outcomes, removedNow, Cfg.ModeBackfill, details);
        Merge(pub, summary);
        var verdicts = VerdictsToReport;
        if (texts.Count == 0 && verdicts.Count == 0)
        {
            MarkPublished(outcomes, files);
            return;
        }
        if (!Cfg.Publish)
        {
            pub["skipped"] = $"sync.publish is off (would write {(texts.Count > 0 ? string.Join(", ", texts.Keys.OrderBy(x => x, StringComparer.Ordinal)) : "no file")}"
                             + $"{(verdicts.Count > 0 ? $", and report {verdicts.Count} verdicts" : "")})";
            return;
        }
        if (!WaitForNobody("publish"))
        {
            pub["skipped"] = "somebody was on the server until the deadline - the results wait for the next run";
            return;
        }
        if (verdicts.Count > 0)
        {
            var (reached, said) = ReportVerdicts(verdicts);
            pub["verdicts"] = said;
            if (!reached)
                pub["verdicts_failed"] = true;
            L($"Workshop verdicts of {verdicts.Count} listed map(s) to the bridge: {Node.Str(pub["verdicts"])}");
        }
        if (texts.Count == 0)
        {
            MarkPublished(outcomes, files);
            return;
        }
        // the live files once more, right before writing: what is planned must be them plus additions, nothing else
        BridgeData live;
        try
        {
            live = BridgeData.Read(Cfg.BridgeData);
        }
        catch (Exception e) when (e is BadDataException or IOException or UnauthorizedAccessException)
        {
            pub["skipped"] = $"the bridge's data cannot be read ({e.Message})";
            return;
        }
        if (live.Raw.Keys.Union(files.Raw.Keys).Any(n => !SameBytes(live.Raw.GetValueOrDefault(n), files.Raw.GetValueOrDefault(n))))
        {
            (texts, summary) = PublishPlan.Of(live, State, outcomes, removedNow, Cfg.ModeBackfill, details);
            Merge(pub, summary);
            files = live;
            L("publish: the bridge's data changed while it was planned - planned again on the live files");
        }
        var problems = texts.SelectMany(kv => PublishPlan.MergeProblems(kv.Key, files.Raw.GetValueOrDefault(kv.Key), kv.Value)).ToList();
        if (problems.Count > 0)
        {
            pub["skipped"] = $"NOT written - the result would not be the live files plus additions: {string.Join("; ", problems)}";
            Errors.Add(Node.Str(pub["skipped"]));
            return;
        }
        var stamp = Clock.Local().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = Path.Combine(Cfg.StateDir, "backups", stamp);
        Directory.CreateDirectory(backup);
        foreach (var (name, raw) in files.Raw)
            File.WriteAllBytes(Path.Combine(backup, name), raw);
        var written = new List<string>();
        void WriteOne(string name)
        {
            var path = Path.Combine(Cfg.BridgeData, name);
            var nowRaw = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (!SameBytes(nowRaw, files.Raw.GetValueOrDefault(name)))
            {
                Errors.Add($"{name} changed on the share a moment before it was written - left alone");
                return;
            }
            Files.WriteAtomic(path, texts[name], FileModes.Shared);
            var back = File.ReadAllBytes(path);
            if (!SameBytes(back, System.Text.Encoding.UTF8.GetBytes(texts[name])))
                Errors.Add($"{name} does not read back as written");
            else if (PublishPlan.MergeProblems(name, files.Raw.GetValueOrDefault(name), System.Text.Encoding.UTF8.GetString(back)).Count > 0)
                Errors.Add($"{name}: the written file is not the old one plus additions");
            written.Add(name);
        }
        foreach (var name in new[] { "broken.json", "maps.json", "maps-deathmatch.json", "maps-wingman.json" })
            if (texts.ContainsKey(name))
                WriteOne(name);
        pub["reload"] = Reload();
        // search.json AFTER the reload: POST /reload does not read search.json yet marks it as read, so written before it the new texts would
        // wait for the next change; written now, the bridge's own watch reads it about 10 s later
        if (texts.ContainsKey("search.json"))
            WriteOne("search.json");
        pub["files"] = Node.Strings(written);
        pub["backup"] = backup;
        L($"published {string.Join(", ", written)}; bridge: {Node.Str(pub["reload"])}");
        PruneBackups(Path.GetDirectoryName(backup)!, stamp);
        MarkPublished(outcomes, BridgeData.Read(Cfg.BridgeData));
    }

    /// <summary>Only the newest BackupsKept of backups/&lt;yyyyMMdd-HHmmss&gt;/ stay, the one just made among them; anything else in the folder is
    /// not ours to delete. A failure here costs a little disk, never the run.</summary>
    void PruneBackups(string folder, string own)
    {
        try
        {
            var old = Directory.EnumerateDirectories(folder).Select(p => Path.GetFileName(p)!)
                .Where(n => n != own && Regex.IsMatch(n, @"^\d{8}-\d{6}$")).OrderByDescending(n => n, StringComparer.Ordinal).Skip(BackupsKept - 1).ToList();
            foreach (var name in old)
                Directory.Delete(Path.Combine(folder, name), true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            L($"backups not pruned: {e.Message}");
        }
    }

    static void Merge(JsonObject into, JsonObject from)
    {
        foreach (var (k, v) in from)
            into[k] = v?.DeepClone();
    }

    static bool SameBytes(byte[]? a, byte[]? b) => a is null || b is null ? a is null && b is null : a.AsSpan().SequenceEqual(b);

    static string VerdictsBody(IEnumerable<(string Id, string Verdict)> verdicts) => BridgeWire.RemovedBody(verdicts);

    /// <summary>This run's check of the listed maps, handed to the bridge (POST /removed/&lt;secret&gt;) - the only Workshop check it hears: the
    /// bridge keeps them in removed.json, blocks a map that is gone at once (and unblocks one that came back), and applies its own rule - more
    /// than 10% of 50+ gone is not believed. A report that does not arrive is a problem for the mail: nothing else tells the bridge; the next
    /// check reports again.</summary>
    public (bool Reached, string Words) ReportVerdicts(IEnumerable<(string Id, string Verdict)> verdicts)
    {
        if (Cfg.BridgeUrl.Length == 0 || Cfg.BridgeSecret.Length == 0)
            return (false, "not reported (no bridge.url / bridge.secret) - nothing else tells the bridge");
        try
        {
            var (status, text) = Game.Post(BridgeWire.Url(Cfg.BridgeUrl, BridgeWire.Removed, Cfg.BridgeSecret), VerdictsBody(verdicts), "text/plain; charset=utf-8");
            if (status is < 200 or >= 300)
                return (false, $"the report failed (HTTP Error {status}) - the next check reports again");
            var said = Text.Cut(text.Trim(), 300);
            return (true, said.Length > 0 ? said : $"HTTP {status}");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            return (false, $"the report failed ({e.Message.Replace(Cfg.BridgeSecret, "<secret>")}) - the next check reports again");
        }
    }

    public void MarkPublished(IReadOnlyList<Verdicts.Outcome> outcomes, BridgeData files)
    {
        var listed = new HashSet<string>(files.Maps.OfType<JsonObject>().Select(m => Node.Str(m["id"])));
        foreach (var (_, entries) in files.ModeLists)
            listed.UnionWith(entries.OfType<JsonObject>().Select(m => Node.Str(m["id"])));
        var on = BridgeData.BrokenIds(files.Broken);
        foreach (var o in outcomes)
        {
            var (wid, kind, detail, rec) = (o.Id, o.Kind, o.Detail, o.Rec);
            if ((listed.Contains(wid) || on.ContainsKey(wid)) && Node.Str(rec["status"]) != "published")
            {
                if (kind == "pass" && listed.Contains(wid))
                    Event(wid, "listed", $"It passed its test: it loads as {Node.StrOr(detail["level"], "its own map")}.");
                else if (on.ContainsKey(wid))
                    Event(wid, "broken", new[] { detail["evidence"], detail["detail"], detail["reason"] }.FirstOrDefault(Node.Truthy) is { } why ? Node.Str(why) : kind);
            }
            if (listed.Contains(wid) || on.ContainsKey(wid))
                rec["status"] = "published";
            else if (kind == "pass" && Node.Str(rec["why"]) == "changed")
                rec["status"] = "published";
            if (kind == "pass")
                State.Baseline[wid] = Node.Int(rec["updated"]);
        }
        State.Save();
        WriteWorkshop(files);
    }

    public const string WorkshopFile = "workshop.json";

    /// <summary>&lt;bridge&gt;/workshop.json, version 1, which the bridge reads (bridge/Popularity.cs) - its discovery stock takes only maps whose
    /// current Workshop content passed the sync's test: {"version": 1, "written": unix time, "maps": {id: {"created", "updated", "content",
    /// "subs", "lifetime", "favs", "lifetimeFavs", "views", "passed"}}} for every listed map. The Workshop's time_created, time_updated,
    /// hcontent_file, subscriptions, lifetime_subscriptions, favorited, lifetime_favorited and views as the last check saw them (the state's
    /// record of the map, with what it has, before the check has seen it), and "passed": the time_updated of the content the sync accepts - the
    /// baseline, the version its last passing test was of (or one unchanged since sync.changedSince); for our own maps (ours.json), never
    /// tested here, the newest the check saw. 0 while the map's Workshop numbers are not known. Written by the check and by every publish.</summary>
    public void WriteWorkshop(BridgeData files)
    {
        var own = new HashSet<string>(Node.Arr(Node.Get(Node.ReadJson(Path.Combine(Cfg.BridgeData, "ours.json")), "maps")).OfType<JsonObject>()
                                          .Select(e => Node.Str(e["id"])).Where(Text.Digits));
        var maps = new JsonObject();
        foreach (var wid in files.Listed())
        {
            if (State.Workshop[wid] is not JsonObject w)
            {
                var rec = State.Item(wid) ?? [];
                w = new JsonObject
                {
                    ["created"] = Node.Int(rec["created"]), ["updated"] = Node.Int(rec["updated"]), ["content"] = "", ["subs"] = Node.Int(rec["subs"]),
                    ["lifetime"] = Node.Int(rec["lifetime"]), ["favs"] = 0, ["lifetimeFavs"] = 0, ["views"] = 0,
                };
            }
            var entry = (JsonObject)w.DeepClone();
            var updated = Node.Int(entry["updated"]);
            var accepted = Node.Int(State.Baseline[wid]);
            if (own.Contains(wid))
                accepted = Math.Max(accepted, updated);
            entry["passed"] = updated > 0 ? accepted : 0;
            maps[wid] = entry;
        }
        var text = Node.Compact(new JsonObject { ["version"] = 1, ["written"] = (long)Math.Truncate(Clock.Now()), ["maps"] = maps }) + "\n";
        if (!Cfg.Publish)
            return;
        try
        {
            Files.WriteAtomic(Path.Combine(Cfg.BridgeData, WorkshopFile), text, FileModes.Shared);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Errors.Add($"{WorkshopFile} not written: {e.Message}");
        }
    }

    public string Reload()
    {
        if (Cfg.BridgeUrl.Length == 0 || Cfg.BridgeSecret.Length == 0)
            return "not asked (no bridge.url / bridge.secret): it reads the files again within about 10 s";
        try
        {
            var (status, text) = Game.Post(BridgeWire.Url(Cfg.BridgeUrl, BridgeWire.Reload, Cfg.BridgeSecret), "", "text/plain; charset=utf-8");
            if (status is < 200 or >= 300)
                return $"the reload request failed (HTTP Error {status}): the bridge reads the files again by itself within about 10 s";
            var said = Text.Cut(string.Join(" | ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)), 600);
            return said.Length > 0 ? said : $"HTTP {status}";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            return $"the reload request failed ({e.Message.Replace(Cfg.BridgeSecret, "<secret>")}): the bridge reads the files again by itself within about 10 s";
        }
    }
}
