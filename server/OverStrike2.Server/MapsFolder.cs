using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>THE MAPS FOLDER (os2-server organize, which install runs before every engine start, so at the latest by the daily restart).
/// The maps live in real folders per type - maps/armsrace, deathmatch, wingman, own, hud, other, each map &lt;id&gt;-&lt;name&gt; - on the game
/// volume, and Steam's steamapps/workshop/content/730/&lt;id&gt; is a relative link to its folder. Every step is one rename or one link on one
/// file system, with no engine running. What Steam downloaded since the last run is a real folder in content/730 (a new map; an update may
/// replace a link by a real folder too): it moves into its type's folder and gets its link; an older copy of it there goes. Moved between
/// types when the lists say so, renamed when a title changes. maps.layout off leaves everything alone. Then, in the same run:
///
/// THE HAND-OVER: the sync leaves each map it tested for the server in its hand-over folder: &lt;id&gt;/ (the content), &lt;id&gt;.manifest (the
/// Steam client's verified manifest) and, written last, &lt;id&gt;.json (its acf entries as SteamCMD wrote them, and how many files and bytes
/// the folder holds). Every complete one - the folder holds exactly what its marker says - is taken in: the folder into its type's folder
/// (a rename, the same volume), its link in content/730, its two entries in appworkshop_730.acf (backed up beside itself first, SizeOnDisk
/// summed again), the manifest into the depotcache. A map the server has as new already is dropped; an older copy is replaced. Steam's
/// client then holds it exactly as if it had downloaded it itself.
///
/// THE DELETION (maps.delete: on, dryrun, off): every stored map outside the keep set - its acf entries (backed up, one atomic write), its
/// link, its folder - in armsrace, deathmatch, wingman and other; never own, never hud, never a queued or a requested map, whatever the
/// keep set says. Refused, with the reason in the log, whenever the keep set names a problem (KeepSet). Every run writes its list to
/// update-watch/deleted-&lt;time&gt;-&lt;done | dryrun | refused&gt;.tsv and logs the count, the size and the volume's free space before and
/// after.</summary>
public sealed partial class MapsFolder(ServerConfig config, Log log, IClock clock, TimeZoneInfo zone)
{
    public static readonly string[] MapTypes = ["armsrace", "deathmatch", "wingman", "own", "hud", "other"];
    public static readonly string[] DeleteTypes = ["armsrace", "deathmatch", "wingman", "other"];   // never own, never hud
    public const int DeleteListsKeep = 20;
    public const int AcfBackupsKeep = 5;

    [GeneratedRegex("[^a-z0-9_.+-]+")] private static partial Regex NotSlug();

    ServerPaths P => config.Paths;

    /// <summary>What a folder may say of a title: lowercase ASCII, at most 40 characters; empty if nothing is left.</summary>
    public static string Slug(string? name)
    {
        var s = NotSlug().Replace((name ?? "").ToLowerInvariant(), "_").Trim('_', '.', '-', '+');
        s = s.Length > 40 ? s[..40] : s;
        return s.TrimEnd('_', '.', '-', '+');
    }

    /// <summary>A JSON value as text (KeepSet.PyStr), "" for none.</summary>
    public static string Str(JsonNode? v) => v is null ? "" : KeepSet.PyStr(v);

    static bool Digits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    /// <summary>{id: (type, title)} from the bridge's lists: the HUD item, then our own maps, then Arms Race, Deathmatch, Wingman.</summary>
    public Dictionary<string, (string Kind, string Title)> Types()
    {
        var types = new Dictionary<string, (string, string)>();
        if (config.HudAddon.Length > 0)
            types[config.HudAddon] = ("hud", "hud");
        if (Files.ReadJson(Path.Combine(P.BridgeData, "ours.json")) is JsonObject ours && ours["maps"] is JsonArray own)
            foreach (var e in own)
                if (e is JsonObject o && KeepSet.Digits(o["id"]) is { } id)
                    types.TryAdd(id, ("own", Str(o["name"])));
        foreach (var (kind, file) in new[] { ("armsrace", "maps.json"), ("deathmatch", "maps-deathmatch.json"), ("wingman", "maps-wingman.json") })
            if (Files.ReadJson(Path.Combine(P.BridgeData, file)) is JsonArray list)
                foreach (var e in list)
                    if (e is JsonObject o && KeepSet.Digits(o["id"]) is { } id)
                        types.TryAdd(id, (kind, Str(o["name"])));
        return types;
    }

    public static string? LinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool IsLink(string path) => LinkTarget(path) != null;
    public static bool Lexists(string path) => IsLink(path) || File.Exists(path) || Directory.Exists(path);

    static void RemoveTree(string path)
    {
        try
        {
            if (IsLink(path))
                File.Delete(path);
            else
                Directory.Delete(path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    Dictionary<string, List<string>> Where(string mapsDir, bool onlyDirs)
    {
        var where = new Dictionary<string, List<string>>();
        foreach (var kind in MapTypes)
        {
            var dir = Path.Combine(mapsDir, kind);
            if (!Directory.Exists(dir))
                continue;
            foreach (var path in Directory.EnumerateFileSystemEntries(dir).OrderBy(x => x, StringComparer.Ordinal))
            {
                var id = Path.GetFileName(path).Split('-', 2)[0];
                if (Digits(id) && (!onlyDirs || Directory.Exists(path)))
                {
                    if (!where.TryGetValue(id, out var list))
                        where[id] = list = [];
                    list.Add(path);
                }
            }
        }
        return where;
    }

    /// <summary>Everything above; a line for the log. Never throws: the engine's start must not depend on it.</summary>
    public string Organize(string? content = null, string? mapsDir = null)
    {
        content = Path.GetFullPath(content ?? P.Content);
        mapsDir = Path.GetFullPath(mapsDir ?? P.MapsDir);
        if (config.MapsLayout != "folders")
            return $"maps folder: off (maps.layout {config.MapsLayout})";
        var counts = new Dictionary<string, int> { ["moved"] = 0, ["renamed"] = 0, ["linked"] = 0, ["replaced"] = 0, ["broken"] = 0, ["errors"] = 0 };
        List<string> extra;
        try
        {
            var types = Types();
            if (!types.Values.Any(t => t.Kind == "armsrace"))
                return "maps folder: the bridge's lists are not readable - left as it is";
            foreach (var kind in MapTypes)
                Directory.CreateDirectory(Path.Combine(mapsDir, kind));
            var where = Where(mapsDir, onlyDirs: false);
            var rel = Path.GetRelativePath(content, mapsDir);
            var installed = Directory.Exists(content) ? Directory.EnumerateFileSystemEntries(content).Select(Path.GetFileName).Select(n => n!).ToHashSet() : [];
            foreach (var id in installed.Union(where.Keys).OrderBy(x => x, StringComparer.Ordinal).ToList())
            {
                if (!Digits(id))
                    continue;
                try
                {
                    var path = Path.Combine(content, id);
                    var (kind, title) = types.TryGetValue(id, out var t) ? t : ("other", "");
                    var slug = Slug(title);
                    var folder = slug.Length > 0 ? $"{id}-{slug}" : id;
                    var target = Path.Combine(mapsDir, kind, folder);
                    var link = Path.Combine(rel, kind, folder);
                    where.TryGetValue(id, out var holders);
                    holders ??= [];
                    if (LinkTarget(path) is { } current0)
                    {
                        var current = Path.GetFullPath(Path.Combine(content, current0));
                        if (!Directory.Exists(current))
                        {
                            File.Delete(path);                  // its folder is gone: Steam fetches it again when it is wanted
                            counts["broken"]++;
                            installed.Remove(id);
                            if (holders.Count == 0)
                                continue;
                        }
                        else if (current == target && current0 == link)
                        {
                            foreach (var other in holders.Where(h => h != target))
                                RemoveTree(other);
                            continue;
                        }
                        else
                        {
                            if (current != target)
                                Directory.Move(current, target);
                            File.Delete(path);
                            Directory.CreateSymbolicLink(path, link);
                            counts["renamed"]++;
                            continue;
                        }
                    }
                    if (Directory.Exists(path) && !IsLink(path))
                    {
                        foreach (var old in holders)             // what Steam just wrote is the newer copy
                        {
                            RemoveTree(old);
                            counts["replaced"]++;
                        }
                        Directory.Move(path, target);
                        Directory.CreateSymbolicLink(path, link);
                        counts["moved"]++;
                    }
                    else if (!Lexists(path) && holders.Count > 0)
                    {
                        var keep = holders.Contains(target) ? target : holders[0];   // a folder that lost its link
                        if (keep != target)
                            Directory.Move(keep, target);
                        Directory.CreateSymbolicLink(path, link);
                        counts["linked"]++;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    counts["errors"]++;
                    log.Line($"maps folder: {id}: {e.Message}");
                }
            }
            where = Where(mapsDir, onlyDirs: true);                  // again, now that everything is in its place
            extra = KeepAndDelete(content, mapsDir, types, where);
            WriteIndex(content, mapsDir, types);
        }
        catch (Exception e)   // never in the way of the engine's start
        {
            return $"maps folder: stopped ({e.GetType().Name}: {e.Message}) - the engine starts anyway";
        }
        var done = counts.Where(kv => kv.Value > 0).Select(kv => $"{kv.Value} {kv.Key}").Concat(extra).ToList();
        return done.Count > 0 ? "maps folder: " + string.Join(", ", done) : "maps folder: in order";
    }

    /// <summary>maps/INDEX.tsv: type, id, title, megabytes, for looking around over a file share. Sizes from Steam's acf.</summary>
    void WriteIndex(string content, string mapsDir, Dictionary<string, (string Kind, string Title)> types)
    {
        Dictionary<string, long> sizes;
        try
        {
            sizes = KeyValues.Sizes(File.ReadAllText(P.WorkshopAcf));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            sizes = [];
        }
        var rows = new StringBuilder("type\tid\ttitle\tMB\n");
        var entries = Directory.Exists(content) ? Directory.EnumerateFileSystemEntries(content) : [];   // a new volume has no Steam folder yet
        foreach (var id in entries.Select(Path.GetFileName).Where(n => Digits(n!)).OrderBy(x => x, StringComparer.Ordinal))
        {
            var (kind, title) = types.TryGetValue(id!, out var t) ? t : ("other", "");
            rows.Append(kind).Append('\t').Append(id).Append('\t').Append(title.Replace('\t', ' ')).Append('\t')
                .Append((sizes.GetValueOrDefault(id!) / 1000000).ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        var tmp = Path.Combine(mapsDir, ".INDEX.tsv.tmp");
        File.WriteAllText(tmp, rows.ToString());
        File.Move(tmp, Path.Combine(mapsDir, "INDEX.tsv"), true);
    }

    // ---------------------------------------------------------------------------------------------------- the hand-over and the deletion

    /// <summary>(files, bytes) of everything under a folder.</summary>
    public static (long Files, long Bytes) FolderStats(string path)
    {
        long files = 0, total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                try
                {
                    total += new FileInfo(f).Length;
                    files++;
                }
                catch (IOException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return (files, total);
    }

    /// <summary>A hand-over marker of a complete hand-over (the sync writes it last): its record, or why not.</summary>
    public static (JsonObject? Record, string? Why) ReadMarker(string marker, string id)
    {
        JsonNode? rec;
        try
        {
            rec = JsonNode.Parse(File.ReadAllText(marker));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, $"its marker does not parse ({e.Message})");
        }
        if (rec is not JsonObject o || KeepSet.Integer(o["version"]) != 1 || Str(o["id"]) != id)
            return (null, $"its marker is not a version-1 hand-over of {id}");
        if (o["installed"] is not JsonObject inst || !new[] { "size", "timeupdated", "manifest" }.All(k => Digits(Falsy(inst[k]))))
            return (null, "its marker has no complete WorkshopItemsInstalled entry");
        if (o["details"] is not JsonObject det || !new[] { "manifest", "timeupdated" }.All(k => Digits(Falsy(det[k]))))
            return (null, "its marker has no complete WorkshopItemDetails entry");
        if (KeepSet.Integer(o["files"]) is not { } files || KeepSet.Integer(o["bytes"]) is null || files <= 0)
            return (null, "its marker does not say what the folder holds");
        return (o, null);
    }

    /// <summary>A JSON value as text, "" for a missing, empty, zero or false one.</summary>
    static string Falsy(JsonNode? v) => v is null || (v is JsonValue jv && ((jv.GetValueKind() == JsonValueKind.String && jv.GetValue<string>().Length == 0)
        || jv.GetValueKind() == JsonValueKind.False || (KeepSet.Number(v) is { } n && n == 0))) ? "" : Str(v);

    sealed record Finish(string Id, string Marker, string? Manifest, string Gid);

    /// <summary>Every complete hand-over moved into its type's folder and linked from content/730, like a download of Steam's own: the acf
    /// entries to add, what to finish once the acf is written, and the counts. A marker whose folder was moved already (a take-in cut short)
    /// still counts.</summary>
    (Dictionary<string, (List<(string, string)> Installed, List<(string, string)> Details)> Add, List<Finish> Finish, Dictionary<string, int> Counts)
        TakeIn(string content, string mapsDir, Dictionary<string, (string Kind, string Title)> types, Dictionary<string, List<string>> where,
               Dictionary<string, long> installedNow)
    {
        var add = new Dictionary<string, (List<(string, string)>, List<(string, string)>)>();
        var finish = new List<Finish>();
        var counts = new Dictionary<string, int> { ["taken"] = 0, ["redundant"] = 0, ["incomplete"] = 0 };
        List<string> names;
        try
        {
            names = Directory.EnumerateFileSystemEntries(P.Handover).Select(p => Path.GetFileName(p)!).OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (add, finish, counts);
        }
        foreach (var name in names)
        {
            if (!name.EndsWith(".json", StringComparison.Ordinal) || !Digits(name[..^5]))
                continue;
            var id = name[..^5];
            var marker = Path.Combine(P.Handover, name);
            var folder = Path.Combine(P.Handover, id);
            var manifest = Path.Combine(P.Handover, id + ".manifest");
            var (rec, why) = ReadMarker(marker, id);
            if (rec is null)
            {
                counts["incomplete"]++;
                log.Line($"hand-over {id}: left alone - {why}");
                continue;
            }
            var (kind, title) = types.TryGetValue(id, out var t) ? t : ("other", Falsy(rec["title"]));
            var slug = Slug(title);
            var target = Path.Combine(mapsDir, kind, slug.Length > 0 ? $"{id}-{slug}" : id);
            var inst = (JsonObject)rec["installed"]!;
            var stamp = long.Parse(Str(inst["timeupdated"]), CultureInfo.InvariantCulture);
            var want = (KeepSet.Integer(rec["files"])!.Value, KeepSet.Integer(rec["bytes"])!.Value);
            where.TryGetValue(id, out var holders);
            holders ??= [];
            try
            {
                if (!Directory.Exists(folder) || IsLink(folder))
                {
                    var moved = holders.Where(h => FolderStats(h) == want).ToList();
                    if (moved.Count == 0)
                    {
                        counts["incomplete"]++;
                        log.Line($"hand-over {id}: its marker is there, its folder is not - left alone");
                        continue;
                    }
                    target = moved[0];                                   // taken in before, cut short before the acf: finish it
                }
                else
                {
                    if (FolderStats(folder) != want)
                    {
                        counts["incomplete"]++;
                        log.Line($"hand-over {id}: its folder does not hold the {want.Item1} files and {want.Item2} bytes its marker names - left alone");
                        continue;
                    }
                    if (installedNow.GetValueOrDefault(id) >= stamp && holders.Count > 0)
                    {
                        RemoveTree(folder);                              // the server's own copy is as new: nothing to take in
                        foreach (var path in new[] { marker, manifest })
                            if (File.Exists(path))
                                File.Delete(path);
                        counts["redundant"]++;
                        continue;
                    }
                    var aside = new List<string>();
                    foreach (var path in holders)                        // an older copy of the same map: aside, then gone
                    {
                        Directory.Move(path, $"{path}.old-{Environment.ProcessId}");
                        aside.Add($"{path}.old-{Environment.ProcessId}");
                    }
                    Directory.Move(folder, target);
                    foreach (var path in aside)
                        RemoveTree(path);
                }
                var link = Path.Combine(content, id);
                if (IsLink(link))
                    File.Delete(link);
                else if (Directory.Exists(link))
                    RemoveTree(link);
                Directory.CreateSymbolicLink(link, Path.GetRelativePath(content, target));
                where[id] = [target];
                var det = (JsonObject)rec["details"]!;
                var installedEntry = new[] { "size", "timeupdated", "manifest" }.Select(k => (k, Str(inst[k]))).ToList();
                var detailsEntry = new[] { "manifest", "timeupdated", "timetouched", "latest_timeupdated", "latest_manifest" }
                    .Where(k => Digits(Falsy(det[k]))).Select(k => (k, Str(det[k]))).ToList();
                add[id] = (installedEntry, detailsEntry);
                finish.Add(new Finish(id, marker, File.Exists(manifest) ? manifest : null, Str(inst["manifest"])));
                counts["taken"]++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                counts["incomplete"]++;
                log.Line($"hand-over {id}: not taken in ({e.Message})");
            }
        }
        return (add, finish, counts);
    }

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
                if (Digits(id) && !keep.Contains(id) && !protect.Contains(id) && Directory.Exists(path) && !IsLink(path))
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
            if (Files.ReadJson(Path.Combine(P.BridgeData, file)) is JsonArray list)
                foreach (var e in list)
                    if (e is JsonObject o && KeepSet.Digits(o["id"]) is { } id)
                        sizes.TryAdd(id, KeepSet.Integer(o["sizeMb"]) ?? 0);
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
            var before = Native.DiskFree(mapsDir);
            var listed = WriteList(rows, "done");
            var failed = 0;
            foreach (var (_, id, path) in gone)
            {
                try
                {
                    var link = Path.Combine(content, id);
                    if (IsLink(link))
                        File.Delete(link);
                    Directory.Delete(path, true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    log.Line($"maps folder: {path} not deleted ({e.Message})");
                }
            }
            var after = Native.DiskFree(mapsDir);
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
