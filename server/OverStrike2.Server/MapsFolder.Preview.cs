using System.Globalization;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>THE PREVIEW (maps.layout preview): what a start with these settings would do to the maps folder, and nothing else - for a
/// rehearsal that runs on production's volume. It reads the maps folder, content/730, the hand-overs, the acf and the bridge's lists as they
/// are; it works out, in memory only, where a start would put every map (each new download Steam left in content/730 moved into its type's
/// folder, each complete hand-over taken in, as Organize and TakeIn decide); it computes the keep set as a start does; and it writes
/// keep-preview.json and deleted-&lt;local time&gt;-preview.tsv - the maps a start would delete, the rows its own list would have - into
/// paths.preview (else update-watch/preview). It moves, links, renames, takes in and deletes nothing, and writes nothing else: no folder in
/// the maps folder, no INDEX.tsv, no acf, no acf backup, no marker or manifest touched.</summary>
public sealed partial class MapsFolder
{
    public const string PreviewWords = "PREVIEW (maps.layout preview) - nothing moved, linked, taken in or deleted";

    /// <summary>THE PREVIEW; a line for the log. Never throws, like Organize.</summary>
    string Preview(string content, string mapsDir)
    {
        var dir = P.PreviewDir;
        try
        {
            var types = Types();
            if (!types.Values.Any(t => t.Kind == "armsrace"))
                return $"maps folder: {PreviewWords}; the bridge's lists are not readable - no preview";
            string text;
            Dictionary<string, long> installedNow;
            try
            {
                text = File.ReadAllText(P.WorkshopAcf);
                installedNow = KeyValues.Installed(text);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
            {
                return $"maps folder: {PreviewWords}; {P.WorkshopAcf} cannot be read ({e.Message}) - no preview";
            }
            var sizes = KeyValues.Sizes(text);
            // every map a start would hold, by id: its folders in the type folders now, and a real folder Steam left in content/730 (a new
            // download, or an update that replaced its link: what Steam just wrote is the copy a start keeps)
            var holders = Where(mapsDir, onlyDirs: true);
            if (Directory.Exists(content))
                foreach (var path in Directory.EnumerateFileSystemEntries(content).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var id = Path.GetFileName(path);
                    if (Digits(id) && Directory.Exists(path) && !Files.IsLink(path))
                        holders[id] = [path];
                }
            var taken = WouldTakeIn(installedNow, holders);
            var keep = KeepSetNow();
            var protect = new HashSet<string>(keep.Own.Concat(keep.Hud).Concat(keep.Requested).Concat(keep.Queues.Values.SelectMany(q => q)));
            protect.UnionWith(taken.Keys);
            var onDisk = holders.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => sizes.GetValueOrDefault(kv.Key));
            foreach (var (id, size) in taken)
                onDisk[id] = size;
            var summary = Summary(keep, onDisk, ListedSizes());
            // where a start puts each map: its type's folder (other for a map no list names), named <id>-<slug of its listed title>
            (string Kind, string Folder) Place(string id)
            {
                var (kind, title) = types.TryGetValue(id, out var t) ? t : ("other", "");
                var slug = Slug(title);
                return (kind, slug.Length > 0 ? $"{id}-{slug}" : id);
            }
            var gone = holders.Keys.Where(id => holders[id].Count > 0 && !keep.All.Contains(id) && !protect.Contains(id))
                .Select(id => (Id: id, Place: Place(id))).Where(g => DeleteTypes.Contains(g.Place.Kind))
                .OrderBy(g => Array.IndexOf(DeleteTypes, g.Place.Kind)).ThenBy(g => g.Place.Folder, StringComparer.Ordinal).ToList();
            var rows = gone.Select(g => (g.Place.Kind, g.Id, Title: RowTitle(types, g.Id, Path.Combine(mapsDir, g.Place.Kind, g.Place.Folder)),
                                         Mb: sizes.GetValueOrDefault(g.Id) / 1000000)).ToList();
            var volume = gone.Sum(g => (double)sizes.GetValueOrDefault(g.Id));
            var byType = string.Join(", ", DeleteTypes.Where(k => rows.Any(r => r.Kind == k)).Select(k => $"{k} {rows.Count(r => r.Kind == k)}"));
            var would = gone.Count == 0 ? "nothing to delete"
                : config.MapsDelete == "dryrun" ? "list them only (maps.delete dryrun)"
                : config.MapsDelete != "on" ? $"keep them (maps.delete {config.MapsDelete})"
                : keep.Problems.Count > 0 ? "REFUSE to delete them: " + string.Join("; ", keep.Problems)
                : "DELETE them";
            LogKeep(keep, summary, "keep set (preview)");
            var keepFile = Path.Combine(dir, "keep-preview.json");
            WriteKeepFile(keep, summary, keepFile, new JsonObject
            {
                ["preview"] = "what a start with these settings would do; nothing was moved, linked, taken in, deleted or written but this file and its list",
                ["would"] = would,
                ["takeIn"] = new JsonArray(taken.Keys.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode)x).ToArray()),
                ["outside"] = gone.Count,
            });
            var listed = WriteList(rows, "preview", dir);
            return $"maps folder: {PreviewWords}; a start would take in {taken.Count} hand-over(s) and find {gone.Count} map(s) outside the keep set"
                   + (gone.Count > 0 ? $" ({Gib(volume)}, {byType})" : "") + $" - it would {would}; the list: {listed ?? "not written"}, the keep set: {keepFile}";
        }
        catch (Exception e)   // never in the way of the engine's start
        {
            return $"maps folder: {PreviewWords}; the preview stopped ({e.GetType().Name}: {e.Message})";
        }
    }

    /// <summary>{id: installed size} of the complete hand-overs a start would take in - TakeIn's choices, made without touching anything: a
    /// marker that reads complete over a folder that holds what it names, unless the server stores that version already (then a start drops
    /// it as not needed); and one whose folder a cut-short take-in moved already, which a start finishes.</summary>
    Dictionary<string, long> WouldTakeIn(Dictionary<string, long> installedNow, Dictionary<string, List<string>> holders)
    {
        var taken = new Dictionary<string, long>();
        List<string> names;
        try
        {
            names = Directory.EnumerateFileSystemEntries(P.Handover).Select(p => Path.GetFileName(p)!).OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return taken;
        }
        foreach (var name in names)
        {
            if (!name.EndsWith(".json", StringComparison.Ordinal) || !Digits(name[..^5]))
                continue;
            var id = name[..^5];
            var (rec, _) = ReadMarker(Path.Combine(P.Handover, name), id);
            if (rec is null)
                continue;
            var folder = Path.Combine(P.Handover, id);
            var inst = (JsonObject)rec["installed"]!;
            var stamp = long.Parse(Str(inst["timeupdated"]), CultureInfo.InvariantCulture);
            var want = (Node.Integer(rec["files"])!.Value, Node.Integer(rec["bytes"])!.Value);
            var size = long.TryParse(Str(inst["size"]), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
            var stored = holders.TryGetValue(id, out var h) ? h : [];
            if (!Directory.Exists(folder) || Files.IsLink(folder))
            {
                if (stored.Any(p => Files.FolderStats(p) == want))
                    taken[id] = size;
                continue;
            }
            if (Files.FolderStats(folder) != want || (installedNow.GetValueOrDefault(id) >= stamp && stored.Count > 0))
                continue;
            taken[id] = size;
        }
        return taken;
    }
}
