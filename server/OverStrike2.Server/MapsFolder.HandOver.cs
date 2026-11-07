using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>THE HAND-OVER's take-in (see MapsFolder.cs): the sync's complete hand-overs moved into their type's folders and linked
/// from content/730, like a download of Steam's own.</summary>
public sealed partial class MapsFolder
{
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
}
