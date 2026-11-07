using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>THE KEEP SET and THE DELETION (see MapsFolder.cs), and KeepAndDelete, which runs the hand-over and the deletion once the
/// maps are in their folders.</summary>
public sealed partial class MapsFolder
{
    /// <summary>[(type, id, folder)] of every stored map outside the keep set, in DeleteTypes' folders only; never a protected id.</summary>
    public static List<(string Kind, string Id, string Path)> Unkept(string mapsDir, HashSet<string> keep, HashSet<string> protect)
    {
        var gone = new List<(string, string, string)>();
        foreach (var kind in DeleteTypes)
        {
            var dir = Path.Combine(mapsDir, kind);
            if (!Directory.Exists(dir))
                continue;
            foreach (var path in Directory.EnumerateFileSystemEntries(dir).OrderBy(x => x, StringComparer.Ordinal))
            {
                var id = Path.GetFileName(path).Split('-', 2)[0];
                if (Digits(id) && !keep.Contains(id) && !protect.Contains(id) && Directory.Exists(path) && !Files.IsLink(path))
                    gone.Add((kind, id, path));
            }
        }
        return gone;
    }

    public static string Gib(double bytes) => (bytes / (1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";

    string Stamp() => TimeZoneInfo.ConvertTime(clock.Now, zone).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>update-watch/deleted-&lt;local time&gt;-&lt;what&gt;.tsv: type, id, title, MB - the record of a deletion or a dry run.</summary>
    string? WriteList(List<(string Kind, string Id, string Title, long Mb)> rows, string what)
    {
        var path = Path.Combine(P.StateDir, $"deleted-{Stamp()}-{what}.tsv");
        try
        {
            Directory.CreateDirectory(P.StateDir);
            File.WriteAllText(path, "type\tid\ttitle\tMB\n" + string.Concat(rows.Select(r => $"{r.Kind}\t{r.Id}\t{r.Title}\t{r.Mb}\n")));
            foreach (var old in Directory.EnumerateFiles(P.StateDir, "deleted-*.tsv").Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).SkipLast(DeleteListsKeep).ToList())
                File.Delete(Path.Combine(P.StateDir, old!));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Line($"maps folder: the list {path} was not written ({e.Message})");
            return null;
        }
        return path;
    }

    /// <summary>[(part, ids)] in the order the log and keep.json give them: per mode its three lists, then their union (what stays).</summary>
    public static List<(string Part, List<string> Ids)> Parts(KeepSetResult keep)
    {
        var parts = new List<(string, List<string>)> { ("up", keep.Up) };
        foreach (var (mode, _) in KeepSet.Modes)
        {
            var lists = keep.Lists.TryGetValue(mode, out var l) ? l : ([], [], []);
            parts.Add(($"rated {mode}", lists.Rated ?? []));
            parts.Add(($"subscribed {mode}", lists.Subscribed ?? []));
            parts.Add(($"popular {mode}", lists.Popular ?? []));
            parts.Add(($"top {mode}", keep.Top.TryGetValue(mode, out var top) ? top : []));
        }
        parts.Add(("own", keep.Own));
        parts.Add(("hud", keep.Hud));
        foreach (var name in KeepSet.QueueNames)
            parts.Add(($"queue {name}", keep.Queues.TryGetValue(name, out var q) ? q : []));
        parts.Add(("requested", keep.Requested));
        parts.Add(("all", keep.All.OrderBy(x => x, StringComparer.Ordinal).ToList()));
        return parts;
    }

    /// <summary>{part: maps, how many the server stores, their size on disk and in all} (the acf's sizes for what is on disk, the lists'
    /// sizeMb for the rest), in GiB.</summary>
    public static JsonObject Summary(KeepSetResult keep, Dictionary<string, long> onDisk, Dictionary<string, long> listedMb)
    {
        var o = new JsonObject();
        foreach (var (part, ids) in Parts(keep))
        {
            var stored = ids.Where(onDisk.ContainsKey).ToList();
            var disk = stored.Sum(w => (double)onDisk[w]);
            var rest = ids.Where(w => !onDisk.ContainsKey(w)).Sum(w => (double)(listedMb.GetValueOrDefault(w) << 20));
            o[part] = new JsonObject
            {
                ["maps"] = ids.Count,
                ["on_disk"] = stored.Count,
                ["gb_on_disk"] = Math.Round(disk / (1L << 30), 1, MidpointRounding.ToEven),
                ["gb"] = Math.Round((disk + rest) / (1L << 30), 1, MidpointRounding.ToEven),
            };
        }
        return o;
    }

    /// <summary>{id: sizeMb} from the bridge's three lists.</summary>
    Dictionary<string, long> ListedSizes()
    {
        var sizes = new Dictionary<string, long>();
        foreach (var (_, file) in KeepSet.Modes)
            if (Node.ReadJson(Path.Combine(P.BridgeData, file)) is JsonArray list)
                foreach (var e in list)
                    if (e is JsonObject o && Node.Id(o["id"]) is { } id)
                        sizes.TryAdd(id, Node.Integer(o["sizeMb"]) ?? 0);
        return sizes;
    }

    KeepSetResult KeepSetNow() => KeepSet.Safe(KeepSet.Inputs(P.BridgeData), clock.Now, config.HudAddon, log.Line, config.KeepRules);

    void WriteKeepFile(KeepSetResult keep, JsonObject summary)
    {
        var cutoff = new JsonObject();
        foreach (var (mode, (score, subs)) in keep.Cutoff)
            cutoff[mode] = new JsonObject { ["score"] = score, ["subscriptions"] = subs };
        var parts = new JsonObject();
        foreach (var (part, ids) in Parts(keep).Where(p => p.Part != "all"))
            parts[part] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray());
        var record = new JsonObject
        {
            ["computed"] = TimeZoneInfo.ConvertTime(clock.Now, zone).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            ["delete"] = config.MapsDelete,
            ["problems"] = new JsonArray(keep.Problems.Select(p => (JsonNode)p).ToArray()),
            ["summary"] = summary,
            ["cutoff"] = cutoff,
            ["parts"] = parts,
        };
        try
        {
            Directory.CreateDirectory(P.StateDir);
            Files.WriteAtomic(P.KeepFile, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }) + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Line($"keep set: {P.KeepFile} not written ({e.Message})");
        }
    }

    void LogKeep(KeepSetResult keep, JsonObject summary) =>
        log.Line("keep set: " + string.Join(", ", summary.Select(kv => $"{kv.Key} {kv.Value!["maps"]} ({kv.Value["on_disk"]} on disk, "
            + $"{kv.Value["gb_on_disk"]!.GetValue<double>().ToString("0.0", CultureInfo.InvariantCulture)} GB)"))
            + $"; {keep.Scored} scored; problems: {(keep.Problems.Count > 0 ? string.Join("; ", keep.Problems) : "none")}");

    /// <summary>THE HAND-OVER and THE DELETION, after the maps are in their folders. Words for the summary line.</summary>
    List<string> KeepAndDelete(string content, string mapsDir, Dictionary<string, (string Kind, string Title)> types, Dictionary<string, List<string>> where)
    {
        string text;
        try
        {
            text = File.ReadAllText(P.WorkshopAcf);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Line($"maps folder: {P.WorkshopAcf} cannot be read ({e.Message}) - no hand-over taken in, nothing deleted");
            return [];
        }
        Dictionary<string, long> installedNow;
        try
        {
            installedNow = KeyValues.Installed(text);
        }
        catch (FormatException e)
        {
            log.Line($"maps folder: {P.WorkshopAcf} does not parse ({e.Message}) - no hand-over taken in, nothing deleted");
            return [];
        }
        var sizes = KeyValues.Sizes(text);
        var (add, finish, counts) = TakeIn(content, mapsDir, types, where, installedNow);
        var keep = KeepSetNow();
        var protect = new HashSet<string>(keep.Own.Concat(keep.Hud).Concat(keep.Requested).Concat(keep.Queues.Values.SelectMany(q => q)));
        protect.UnionWith(add.Keys);                                     // just handed over: the keep set asked for them
        var onDisk = where.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => sizes.GetValueOrDefault(kv.Key));
        foreach (var (id, (inst, _)) in add)
            onDisk[id] = long.TryParse(inst.FirstOrDefault(e => e.Item1 == "size").Item2, NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
        var summary = Summary(keep, onDisk, ListedSizes());
        var gone = Unkept(mapsDir, keep.All, protect);
        var words = new List<string>();
        LogKeep(keep, summary);
        WriteKeepFile(keep, summary);
        var rows = gone.Select(g => (g.Kind, g.Id, Title: RowTitle(types, g.Id, g.Path), Mb: sizes.GetValueOrDefault(g.Id) / 1000000)).ToList();
        var byType = string.Join(", ", DeleteTypes.Where(k => rows.Any(r => r.Kind == k)).Select(k => $"{k} {rows.Count(r => r.Kind == k)}"));
        var volume = gone.Sum(g => (double)sizes.GetValueOrDefault(g.Id));
        var delete = config.MapsDelete == "on" && keep.Problems.Count == 0 && gone.Count > 0;
        if (gone.Count > 0 && !delete)
        {
            var listed = WriteList(rows, config.MapsDelete == "on" ? "refused" : config.MapsDelete);
            var why = config.MapsDelete != "on" ? $"maps.delete is {config.MapsDelete}" : "REFUSED: " + string.Join("; ", keep.Problems);
            log.Line($"maps folder: {gone.Count} map(s) outside the keep set ({Gib(volume)}, {byType}) - not deleted, {why}; the list: {listed}");
            words.Add($"{gone.Count} outside the keep set, not deleted ({(config.MapsDelete != "on" ? config.MapsDelete : "refused")})");
        }
        var drop = delete ? gone.Select(g => g.Id).ToHashSet() : [];
        if (add.Count > 0 || drop.Count > 0)
        {
            var edited = KeyValues.Edit(text, add, drop);
            if (edited is null)
            {
                log.Line($"maps folder: {P.WorkshopAcf} does not round-trip through the parser - no hand-over registered, nothing deleted");
                return [.. words, "acf left alone"];
            }
            string backup;
            try
            {
                backup = AcfWrite(edited, "organize");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Line($"maps folder: {P.WorkshopAcf} not written ({e.Message}) - no hand-over registered, nothing deleted");
                return [.. words, "acf not written"];
            }
            log.Line($"maps folder: {P.WorkshopAcf} written - {add.Count} hand-over(s) registered, {drop.Count} map(s) dropped; the old one is {backup}");
        }
        foreach (var f in finish)
        {
            if (f.Manifest is { } m)
            {
                var target = Path.Combine(P.Depotcache, $"730_{f.Gid}.manifest");
                try
                {
                    if (!File.Exists(target))
                        File.Copy(m, target);           // the Steam client's verified manifest: no re-check request for it
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log.Line($"hand-over {f.Id}: its manifest was not copied into {P.Depotcache} ({e.Message}) - the client asks for it once");
                }
            }
            foreach (var path in new[] { f.Marker, f.Manifest })
                try
                {
                    if (path is not null && File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
        }
        if (add.Count > 0)
        {
            var ids = add.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
            log.Line($"hand-over: {add.Count} map(s) taken in ({string.Join(", ", ids.Take(20))}{(ids.Count > 20 ? " ..." : "")})");
            words.Add($"{add.Count} handed over");
        }
        if (counts["redundant"] > 0 || counts["incomplete"] > 0)
            words.Add($"{counts["redundant"]} hand-over(s) not needed, {counts["incomplete"]} left alone");
        if (delete)
        {
            var before = Posix.DiskFree(mapsDir);
            var listed = WriteList(rows, "done");
            var failed = 0;
            foreach (var (_, id, path) in gone)
            {
                try
                {
                    var link = Path.Combine(content, id);
                    if (Files.IsLink(link))
                        File.Delete(link);
                    Directory.Delete(path, true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    log.Line($"maps folder: {path} not deleted ({e.Message})");
                }
            }
            var after = Posix.DiskFree(mapsDir);
            log.Line($"maps folder: DELETED {gone.Count - failed} map(s) outside the keep set, {Gib(volume)} ({byType}){(failed > 0 ? $", {failed} failed" : "")}; "
                     + $"free on the volume {(before is { } b ? Gib(b) : "?")} -> {(after is { } a ? Gib(a) : "?")} (a copy-on-write volume may free it later); the list: {listed}");
            words.Add($"{gone.Count - failed} deleted ({Gib(volume)})");
        }
        return words;
    }

    static string RowTitle(Dictionary<string, (string Kind, string Title)> types, string id, string path)
    {
        var title = types.TryGetValue(id, out var t) ? t.Title.Replace('\t', ' ') : "";
        return title.Length > 0 ? title : Path.GetFileName(path).Split('-', 2)[^1];
    }

    /// <summary>Replace the workshop acf: first a copy of the old one beside it (appworkshop_730.acf.bak-&lt;local time&gt;-&lt;why&gt;; the newest
    /// AcfBackupsKeep of each reason are kept), then a temporary file with the old file's mode and an atomic rename. The backup's path;
    /// throws IOException, and then the old file is still in place.</summary>
    string AcfWrite(string text, string why)
    {
        var acf = P.WorkshopAcf;
        var folder = Path.GetDirectoryName(acf)!;
        var backup = $"{acf}.bak-{Stamp()}-{why}";
        File.Copy(acf, backup, true);
        var tmp = $"{acf}.{why}-tmp";
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            f.Write(Encoding.UTF8.GetBytes(text));
            f.Flush(true);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, File.GetUnixFileMode(acf));
        File.Move(tmp, acf, true);
        var prefix = Path.GetFileName(acf) + ".bak-";
        foreach (var old in Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(n => n!.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith("-" + why, StringComparison.Ordinal))
                     .OrderBy(x => x, StringComparer.Ordinal).SkipLast(AcfBackupsKeep).ToList())
            try
            {
                File.Delete(Path.Combine(folder, old!));
            }
            catch (IOException)
            {
            }
        return backup;
    }
}
