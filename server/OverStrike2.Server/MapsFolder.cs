using System.Globalization;
using System.Text;
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

    /// <summary>A JSON value as text (Node.Str), "" for none.</summary>
    public static string Str(JsonNode? v) => v is null ? "" : Node.Str(v);

    static bool Digits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    /// <summary>{id: (type, title)} from the bridge's lists: the HUD item, then our own maps, then Arms Race, Deathmatch, Wingman.</summary>
    public Dictionary<string, (string Kind, string Title)> Types()
    {
        var types = new Dictionary<string, (string, string)>();
        if (config.HudAddon.Length > 0)
            types[config.HudAddon] = ("hud", "hud");
        if (Node.ReadJson(Path.Combine(P.BridgeData, "ours.json")) is JsonObject ours && ours["maps"] is JsonArray own)
            foreach (var e in own)
                if (e is JsonObject o && Node.Id(o["id"]) is { } id)
                    types.TryAdd(id, ("own", Str(o["name"])));
        foreach (var (kind, file) in new[] { ("armsrace", "maps.json"), ("deathmatch", "maps-deathmatch.json"), ("wingman", "maps-wingman.json") })
            if (Node.ReadJson(Path.Combine(P.BridgeData, file)) is JsonArray list)
                foreach (var e in list)
                    if (e is JsonObject o && Node.Id(o["id"]) is { } id)
                        types.TryAdd(id, (kind, Str(o["name"])));
        return types;
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
                    if (Files.LinkTarget(path) is { } current0)
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
                                Files.RemoveTree(other, quiet: true);
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
                    if (Directory.Exists(path) && !Files.IsLink(path))
                    {
                        foreach (var old in holders)             // what Steam just wrote is the newer copy
                        {
                            Files.RemoveTree(old, quiet: true);
                            counts["replaced"]++;
                        }
                        Directory.Move(path, target);
                        Directory.CreateSymbolicLink(path, link);
                        counts["moved"]++;
                    }
                    else if (!Files.Lexists(path) && holders.Count > 0)
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
}
